using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Mints signed access tokens and manages the refresh tokens that renew them.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: net-new. There is no legacy predecessor to port, because the legacy application had no
/// token concept at all - it issued an encrypted forms-authentication cookie through the ASP.NET 2.0
/// membership pipeline registered at <c>Website/release.config</c> line 218, and signed out by
/// destroying four cookies by name at <c>Library/Components/Security/PortalSecurity.vb:L77</c>:
/// <c>language</c>, <c>authentication</c>, <c>portalaliasid</c> and <c>portalroles</c>. Nothing in that
/// arrangement survives: a bearer token is not a cookie, and there is no server-side session to end,
/// which is why signing out here means revoking refresh material and letting the access token lapse.
/// </para>
/// <para>
/// <strong>Why this type is a singleton, and the trap that comes with it.</strong> Signing is stateless
/// and its configuration is fixed for the process lifetime, so a singleton is the intended registration.
/// A singleton must not capture a scoped dependency: no database context, no repository, no unit of
/// work may be held in a field here. Renewal genuinely needs scoped services - it re-reads the caller's
/// entitlements - so it resolves a scope for the duration of that one call and releases it. Holding a
/// scoped instance in a field would serve whichever request happened to arrive first to every later
/// caller, and neither the compiler nor the container's validation catches it once the instance has
/// been captured through a factory.
/// </para>
/// <para>
/// <strong>What the access token asserts.</strong> The subject identifier, the sign-in name, the portal
/// identifier, the super-user flag, one entry per role and one entry per permission key - exactly the
/// facts <see cref="ICurrentUser"/> projects at the API edge, which is their only consumer. Roles and
/// permission keys are asserted precisely as handed over on issue: this type never invents, filters,
/// deduplicates or re-orders them, because doing so would make the token disagree with the repository
/// read that produced it.
/// </para>
/// <para>
/// <strong>Identifiers are asserted verbatim.</strong> Neither 0 nor -1 is treated as absent anywhere
/// here. <c>Portals.PortalID</c> seeds at -1 and <c>Modules.ModuleID</c>, <c>Tabs.TabID</c> and
/// <c>Roles.RoleID</c> all seed at 0, so every one of those values identifies something real.
/// </para>
/// <para>
/// <strong>No inspection member, by design.</strong> This type never validates an inbound token. The
/// API layer's bearer handler already does that on the request path, and a second validation path could
/// disagree with the first about whether a token is acceptable - which is a security defect rather than
/// a convenience.
/// </para>
/// <para>
/// <strong>Nothing here logs, and nothing here returns credential material.</strong> No token, secret
/// or hash reaches a log, a message or a failure reason. A failure says which rule refused the request
/// and nothing more.
/// </para>
/// </remarks>
internal sealed class JwtTokenService : ITokenService
{
    /// <summary>Claim type carrying the tenant the token was issued for.</summary>
    /// <remarks>
    /// A private claim rather than a registered one, because the tenant is this application's concept
    /// and no registered claim means it. The API edge reads this name and the frontend never parses the
    /// token at all, so the two ends agree by construction.
    /// </remarks>
    public const string PortalIdClaimType = DnnClaimTypes.PortalId;

    /// <summary>Claim type carrying the host-level super-user flag.</summary>
    /// <remarks>
    /// Asserted as its own entry rather than inferred from a role name, because the legacy checks
    /// short-circuited on this flag independently of role membership - see
    /// <c>PortalSecurity.IsInRoles</c> at line 123, which returned true for a super-user before
    /// examining a single role.
    /// </remarks>
    public const string SuperUserClaimType = DnnClaimTypes.SuperUser;

    /// <summary>Claim type carrying one permission key the caller holds.</summary>
    /// <remarks>
    /// One entry per key, never a delimited list. The legacy code flattened grants into a
    /// semicolon-delimited string - <c>ModulePermissionController.vb:L239-L252</c> and
    /// <c>TabPermissionController.vb:L214-L227</c> both emitted a leading semicolon followed by each
    /// granted role - and every consumer then had to split it, which is how an empty first element came
    /// to be a thing callers had to guard against. Separate entries remove the parsing step entirely.
    /// </remarks>
    public const string PermissionClaimType = DnnClaimTypes.Permission;

    /// <summary>The failure code reported when refresh material could not be recorded.</summary>
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>The failure code reported when no record matches the presented refresh token.</summary>
    private const string RefreshTokenNotFoundCode = "REFRESH_TOKEN_NOTFOUND";

    /// <summary>The failure code reported when the presented refresh token was already exchanged.</summary>
    private const string RefreshTokenAlreadyUsedCode = "REFRESH_TOKEN_ALREADYUSED";

    /// <summary>The failure code reported when the presented refresh token was explicitly revoked.</summary>
    private const string RefreshTokenRevokedCode = "REFRESH_TOKEN_REVOKED";

    /// <summary>The failure code reported when the presented refresh token has lapsed.</summary>
    private const string RefreshTokenExpiredCode = "REFRESH_TOKEN_EXPIRED";

    /// <summary>
    /// The shortest signing secret this service will accept, in bytes.
    /// </summary>
    /// <remarks>
    /// HMAC-SHA256 requires a key of at least the hash length, and a shorter one makes the signing
    /// library throw at the first sign-in rather than at start-up. Checking it here converts a runtime
    /// authentication outage into a configuration fault raised the first time a token is minted, with a
    /// message that names the setting.
    /// </remarks>
    private const int MinimumSecretBytes = 32;

    private readonly RefreshTokenStore _refreshTokens;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JwtOptions _options;
    private readonly JwtSecurityTokenHandler _handler = new();

    /// <summary>Initialises a new instance of the <see cref="JwtTokenService"/> class.</summary>
    /// <param name="refreshTokens">The refresh-token store; a singleton, like this service.</param>
    /// <param name="clock">The injected clock, so expiry and rotation stay testable.</param>
    /// <param name="scopeFactory">
    /// Used to resolve a scope for the entitlement re-read that renewal performs. A factory rather than
    /// a provider, and used per call rather than held, for the reason given in the type remarks.
    /// </param>
    /// <param name="jwtOptions">The bound JWT configuration.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public JwtTokenService(
        RefreshTokenStore refreshTokens,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);

        _refreshTokens = refreshTokens ?? throw new ArgumentNullException(nameof(refreshTokens));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = jwtOptions.Value ?? throw new ArgumentNullException(nameof(jwtOptions));
    }

    /// <inheritdoc />
    public Task<Result<LoginResponse>> IssueTokensAsync(
        int userId,
        int portalId,
        string userName,
        bool isSuperUser,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissionKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(permissionKeys);

        cancellationToken.ThrowIfCancellationRequested();

        RefreshTokenSubject subject = new(userId, portalId, userName, isSuperUser, roles, permissionKeys);

        RefreshTokenIssueResult issued = _refreshTokens.Issue(subject);

        // A pair is returned only once its refresh half is recorded. Verifying that here rather than
        // assuming it is what keeps the documented failure honest: handing back a refresh token the
        // store never kept would look like success and then fail at the caller's first exchange, which
        // is a fault reported one request too late to explain. The order is the one the store's own
        // contract prescribes - test the outcome, then read the token - and the two member tests that
        // follow it are not redundant belt-and-braces: they are what lets the response be built from
        // values the compiler knows are present, without a single suppression.
        if (issued.Outcome != RefreshTokenOutcome.Succeeded
            || string.IsNullOrEmpty(issued.RefreshToken)
            || issued.ExpiresAtUtc is null)
        {
            return Task.FromResult(StoreUnavailable());
        }

        LoginResponse response = BuildResponse(subject, issued.RefreshToken);

        return Task.FromResult(Result<LoginResponse>.Success(response));
    }

    /// <inheritdoc />
    public async Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Read first, so a token that is already unusable is refused without any database work. The
        // reading is side-effect-free; responding to a replay is a separate, deliberate act below.
        RefreshTokenInspection inspection = _refreshTokens.Inspect(refreshToken);

        if (inspection.Outcome != RefreshTokenOutcome.Succeeded || inspection.Subject is null)
        {
            RespondToReplay(inspection.Outcome, inspection.OwnerUserId);

            return Failure(inspection.Outcome);
        }

        RefreshTokenSubject presented = inspection.Subject;

        // The renewed access token asserts the caller's CURRENT entitlements, re-read now, not the
        // entries the replaced token happened to carry. A role granted or withdrawn since the last
        // exchange therefore takes effect within one access-token lifetime instead of persisting until
        // the caller signs in again. The scope lives exactly as long as this read.
        RefreshTokenSubject renewed = await ReadCurrentEntitlementsAsync(presented, cancellationToken)
            .ConfigureAwait(false);

        RefreshTokenRotationResult rotated = _refreshTokens.Rotate(refreshToken, renewed);

        if (rotated.Outcome != RefreshTokenOutcome.Succeeded)
        {
            // Reached when a concurrent request consumed the same value between the reading above and
            // this exchange. The store has already answered a replay itself, under the lock that
            // detected it, so nothing further is owed here.
            return Failure(rotated.Outcome);
        }

        if (string.IsNullOrEmpty(rotated.RefreshToken) || rotated.ExpiresAtUtc is null)
        {
            return StoreUnavailable();
        }

        LoginResponse response = BuildResponse(renewed, rotated.RefreshToken);

        return Result<LoginResponse>.Success(response);
    }

    /// <inheritdoc />
    public Task<Result> RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately silent about whether anything was there to revoke. Reporting that distinction
        // would turn sign-out into an oracle that tells an unauthenticated caller whether a token
        // string it holds was ever genuine.
        _refreshTokens.Revoke(refreshToken);

        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> RevokeAllRefreshTokensAsync(int userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Idempotent: an account holding nothing succeeds, which is what lets a caller retry a theft
        // response or an administrative reset without having to know whether the first attempt landed.
        _refreshTokens.RevokeAllForUser(userId);

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Answers a replayed refresh token by revoking every token the account holds.
    /// </summary>
    /// <param name="outcome">How the presented token was classified.</param>
    /// <param name="ownerUserId">
    /// The account the presented token was issued to, when the store recognised it. An account key and
    /// nothing more: the entitlement snapshot is discarded at redemption, so it is unavailable here by
    /// design and is not needed to answer a replay.
    /// </param>
    /// <remarks>
    /// Re-presentation of an already-exchanged token is the signature of theft: the legitimate holder
    /// rotated it, so a second presentation came from somewhere else. The response is deliberately
    /// wider than the token presented - the thief may hold material from an unrelated sign-in of the
    /// same account - and it happens before the refusal is reported, so the refusal cannot be used to
    /// probe which of several stolen values is still live.
    /// <para>
    /// No other outcome triggers it. An expired or revoked token is an ordinary, expected refusal, and
    /// treating either as theft would sign users out for the sake of a lapsed token.
    /// </para>
    /// </remarks>
    private void RespondToReplay(RefreshTokenOutcome outcome, int? ownerUserId)
    {
        if (outcome == RefreshTokenOutcome.AlreadyUsed && ownerUserId is { } userId)
        {
            _refreshTokens.RevokeAllForUser(userId);
        }
    }

    /// <summary>
    /// Re-reads the caller's current roles and permission keys for a renewed token.
    /// </summary>
    /// <param name="presented">The snapshot recorded against the token being exchanged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A snapshot carrying the same identity and the caller's present entitlements.</returns>
    /// <exception cref="InvalidOperationException">
    /// The recorded snapshot carries no tenant. This is an internal invariant violation rather than an
    /// authentication outcome: every token this service issues names a tenant, so a snapshot without
    /// one cannot have come from a supported path, and reporting it as a refused token would hide a
    /// fault behind a message about credentials.
    /// </exception>
    /// <remarks>
    /// The identity facts - who the caller is, which tenant, whether a super-user - are carried across
    /// unchanged, because a renewal is the same caller continuing. Only the entitlements are re-read.
    /// <para>
    /// The scope is created here and disposed on return, which is what keeps a singleton free of a
    /// captured scoped instance. Both reads share it, so they observe one consistent view.
    /// </para>
    /// <para>
    /// A permission read that fails is treated as conferring nothing rather than as a reason to refuse
    /// the renewal: the caller's credential is not in question, and a token asserting no permission
    /// keys still authenticates while simply offering no client-side affordances. That fails closed,
    /// which is the safe direction, and the server re-evaluates every permission on every request in
    /// any case - the entries in a token inform the client and never decide access.
    /// </para>
    /// </remarks>
    private async Task<RefreshTokenSubject> ReadCurrentEntitlementsAsync(
        RefreshTokenSubject presented,
        CancellationToken cancellationToken)
    {
        int portalId = presented.PortalId
            ?? throw new InvalidOperationException(
                "The recorded refresh-token snapshot carries no portal identifier, so the caller's "
                + "entitlements cannot be re-read.");

        using IServiceScope scope = _scopeFactory.CreateScope();

        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        IPermissionService permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        IReadOnlyList<string> roles = await users
            .ListRoleNamesAsync(portalId, presented.UserId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        Result<IReadOnlyList<string>> effective = await permissions
            .GetEffectivePermissionKeysAsync(portalId, presented.UserId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> permissionKeys = effective.IsSuccess
            ? effective.Value
            : Array.Empty<string>();

        return new RefreshTokenSubject(
            presented.UserId,
            portalId,
            presented.UserName,
            presented.IsSuperUser,
            roles,
            permissionKeys);
    }

    /// <summary>Builds the response pair for a snapshot and the refresh token just recorded.</summary>
    /// <param name="subject">The facts the access token is to assert.</param>
    /// <param name="refreshToken">The refresh token recorded for this pair.</param>
    /// <returns>The response pair.</returns>
    /// <remarks>
    /// <para>
    /// The user projection carries only what this service was told: the identifier, the tenant, the
    /// sign-in name, the super-user flag and the two entitlement collections. The display name, the
    /// address and the tenant's name are deliberately left at their defaults, because this service is
    /// given none of them and inventing them would put unverified values on a contract. The sign-in
    /// service overwrites the projection with a fully populated one before the response leaves the
    /// application layer; the identifiers set here are what lets it find the account to do so.
    /// </para>
    /// <para>
    /// Only the access token's own expiry is published. The bearer scheme, the access token's remaining
    /// lifetime as a duration and the refresh token's expiry are all deliberately withheld even though
    /// this method holds or could compute each of them: the scheme is fixed by the API contract rather
    /// than restated per response, one expiry representation cannot disagree with itself, and the
    /// refresh token's expiry is rotation state that belongs to the store. The three advisory booleans
    /// on the response are likewise left at their defaults here, because this service is not told them -
    /// the sign-in service sets them alongside the fully populated user projection.
    /// </para>
    /// </remarks>
    private LoginResponse BuildResponse(
        RefreshTokenSubject subject,
        string refreshToken)
    {
        DateTime issuedAtUtc = _clock.UtcNow;
        DateTime expiresAtUtc = issuedAtUtc.AddMinutes(_options.ExpirationMinutes);

        return new LoginResponse
        {
            AccessToken = CreateAccessToken(subject, issuedAtUtc, expiresAtUtc),
            ExpiresAtUtc = expiresAtUtc,
            RefreshToken = refreshToken,
            User = new CurrentUserDto
            {
                UserId = subject.UserId,
                PortalId = subject.PortalId ?? 0,
                Username = subject.UserName ?? string.Empty,
                IsSuperUser = subject.IsSuperUser,
                Roles = subject.Roles,
                Permissions = subject.PermissionKeys,
            },
        };
    }

    /// <summary>Signs one access token asserting the facts in a snapshot.</summary>
    /// <param name="subject">The facts to assert.</param>
    /// <param name="issuedAtUtc">The instant the token is issued.</param>
    /// <param name="expiresAtUtc">The instant the token lapses.</param>
    /// <returns>The compact serialised token.</returns>
    /// <remarks>
    /// Roles are asserted under the standard role claim type so that the framework's own role checks
    /// and the <c>[Authorize(Roles = ...)]</c> attribute work without any mapping, while permissions
    /// use a private type because no standard claim means "granular permission key". Both are emitted
    /// exactly as supplied - the same values, in the same order, with duplicates intact - because this
    /// service is not the authority on what a caller holds and silently normalising them would make
    /// the token disagree with the read that produced it.
    /// </remarks>
    private string CreateAccessToken(RefreshTokenSubject subject, DateTime issuedAtUtc, DateTime expiresAtUtc)
    {
        List<Claim> claims =
        [
            new Claim(
                JwtRegisteredClaimNames.Subject,
                subject.UserId.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer32),
            new Claim(JwtRegisteredClaimNames.JwtId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            new Claim(SuperUserClaimType, subject.IsSuperUser ? "true" : "false", ClaimValueTypes.Boolean),
        ];

        if (subject.PortalId is int portalId)
        {
            claims.Add(new Claim(
                PortalIdClaimType,
                portalId.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer32));
        }

        if (!string.IsNullOrWhiteSpace(subject.UserName))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.UniqueName, subject.UserName));
        }

        foreach (string role in subject.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (string permission in subject.PermissionKeys)
        {
            claims.Add(new Claim(PermissionClaimType, permission));
        }

        SigningCredentials credentials = new(CreateSigningKey(), SecurityAlgorithms.HmacSha256);

        JwtSecurityToken token = new(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAtUtc,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return _handler.WriteToken(token);
    }

    /// <summary>Builds the symmetric signing key from the configured secret.</summary>
    /// <returns>The signing key.</returns>
    /// <exception cref="InvalidOperationException">
    /// The configured secret is missing or too short to sign with.
    /// </exception>
    /// <remarks>
    /// The check names the configuration key, because the alternative is an authentication outage whose
    /// only symptom is a cryptographic exception at the first sign-in. The secret itself never appears
    /// in the message, so a misconfiguration report cannot leak the value it is complaining about.
    /// </remarks>
    private SymmetricSecurityKey CreateSigningKey()
    {
        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            throw new InvalidOperationException(
                "No access-token signing secret is configured. Set Jwt:Secret, or supply it as the "
                + "environment variable Jwt__Secret.");
        }

        byte[] key = Encoding.UTF8.GetBytes(_options.Secret);

        if (key.Length < MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The configured access-token signing secret is too short: HMAC-SHA256 needs at "
                    + "least {0} bytes and Jwt:Secret supplies {1}.",
                    MinimumSecretBytes,
                    key.Length));
        }

        return new SymmetricSecurityKey(key);
    }

    /// <summary>Translates a store outcome into the failure code the contract fixes for it.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    /// <returns>The corresponding failed result.</returns>
    /// <remarks>
    /// The contract fixes both the codes and the order in which they are considered - absent, then
    /// already used, then revoked, then expired - and the store's own classifier applies that same
    /// order, so this is a translation rather than a second decision. An unrecognised outcome is
    /// reported as absent, which is the closed answer: refusing a token whose state cannot be named is
    /// the only safe reading.
    /// </remarks>
    private static Result<LoginResponse> Failure(RefreshTokenOutcome outcome)
    {
        return outcome switch
        {
            RefreshTokenOutcome.AlreadyUsed => Result<LoginResponse>.Failure(
                RefreshTokenAlreadyUsedCode,
                "The refresh token has already been exchanged."),
            RefreshTokenOutcome.Revoked or RefreshTokenOutcome.AlreadyRevoked => Result<LoginResponse>.Failure(
                RefreshTokenRevokedCode,
                "The refresh token has been revoked."),
            RefreshTokenOutcome.Expired => Result<LoginResponse>.Failure(
                RefreshTokenExpiredCode,
                "The refresh token has expired."),
            _ => Result<LoginResponse>.Failure(
                RefreshTokenNotFoundCode,
                "The refresh token is not recognised."),
        };
    }

    /// <summary>Builds the failure reported when refresh material could not be recorded.</summary>
    /// <returns>The failed result.</returns>
    private static Result<LoginResponse> StoreUnavailable()
    {
        return Result<LoginResponse>.Failure(
            TokenStoreUnavailableCode,
            "The refresh token could not be recorded, so no token pair was issued.");
    }
}

/// <summary>
/// The registered claim names this service emits, spelled out so the API edge and this service cannot
/// drift apart over a string literal.
/// </summary>
/// <remarks>
/// The signing library publishes equivalent constants, but naming them here keeps the emitted claim set
/// visible in one place and independent of that library's own naming, which has changed between major
/// versions of it.
/// </remarks>
internal static class JwtRegisteredClaimNames
{
    /// <summary>The token's subject - the authenticated account's identifier.</summary>
    public const string Subject = DnnClaimTypes.Subject;

    /// <summary>A unique identifier for this particular token.</summary>
    public const string JwtId = DnnClaimTypes.JwtId;

    /// <summary>The caller's sign-in name.</summary>
    public const string UniqueName = DnnClaimTypes.UniqueName;
}
