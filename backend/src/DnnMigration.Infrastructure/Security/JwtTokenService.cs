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
using DnnMigration.Domain.Entities;
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
/// membership pipeline registered at <c>Website/release.config</c> line 218, and ended a session by
/// destroying four cookies by name at <c>Library/Components/Security/PortalSecurity.vb:L77</c>:
/// <c>language</c>, <c>authentication</c>, <c>portalaliasid</c> and <c>portalroles</c>, the last two
/// back-dated thirty years at lines 90 and 94 so the browser dropped them at once. Nothing in that
/// arrangement survives: a bearer token is not a cookie and there is no server-side session to end,
/// which is why signing off here means revoking refresh material and letting the access token lapse.
/// The persistent-cookie half - <c>FormsAuthentication.SetAuthCookie</c> at
/// <c>Library/Components/Users/UserController.vb:L1033</c> with the hand-built persistent ticket at
/// lines 1037 to 1047 - becomes the refresh token: outliving the browser session was the whole point
/// of that cookie, and a rotating refresh token does the same job while being single-use and
/// revocable, which the cookie was neither.
/// </para>
/// <para>
/// <strong>Why this type is a singleton, and the trap that comes with it.</strong> Signing is stateless
/// and its configuration is fixed for the process lifetime, so a singleton is the intended
/// registration - see <c>Infrastructure/DependencyInjection.cs</c>, which registers this type and the
/// refresh-token store beside it as singletons sharing one store instance. A singleton must not
/// capture a scoped dependency: no database context, no repository, no unit of work and no tenant
/// context may be held in a field here. Renewal genuinely needs scoped services, because it re-reads
/// the caller's entitlements, so it resolves a scope for the duration of that one call and releases
/// it. Holding a scoped instance in a field would serve whichever request happened to arrive first to
/// every later caller, and neither the compiler nor the container's validation catches it once the
/// instance has been captured through a factory.
/// </para>
/// <para>
/// <strong>What the access token asserts.</strong> The subject identifier, the sign-in name, the
/// portal identifier, the super-user flag, one entry per role and one entry per permission key, plus
/// the three time claims - issued-at, not-before and expiry - all measured from a single reading of
/// the injected clock. That claim set is exactly what <c>Api/Authorization/CurrentUser.cs</c> projects
/// back from a validated principal, and that projection is its only consumer, which is why the names
/// come from <see cref="DnnClaimTypes"/> rather than from this file: the layer that mints a token and
/// the layer that reads one cannot be allowed to drift apart over a string literal. Roles alone are
/// emitted under the framework's own <see cref="ClaimTypes.Role"/> so that
/// <c>[Authorize(Roles = ...)]</c> and every framework role check work with no mapping step.
/// </para>
/// <para>
/// <strong>What the access token never carries.</strong> No credential, credential hash or
/// verification answer; no signing secret or any value derived from one; no refresh token, family
/// identifier, generation number or rotation count; no address, telephone number or other profile
/// detail. Everything in the payload is either an identifier the caller already knows or an
/// entitlement name the client needs in order to decide what to render.
/// </para>
/// <para>
/// <strong>Identifiers are asserted verbatim.</strong> Neither 0 nor -1 is treated as absent anywhere
/// here, and no identifier is range-checked. <c>Portals.PortalID</c> is
/// <c>IDENTITY (-1, 1)</c>, so the seed and first generated value is -1, while the shipped default
/// portal row is inserted explicitly with <c>PortalID</c> 0; <c>Roles.RoleID</c> seeds at 0. All
/// three values identify something real. The legacy sentinel table at
/// <c>Library/Components/Shared/Null.vb:L41</c> happens to use -1 for "no integer", and conflating
/// the two readings would deny a token to a real portal.
/// </para>
/// <para>
/// <strong>No inspection member, by design.</strong> This type never validates an inbound token. The
/// API layer's bearer handler already does that on the request path - signature, algorithm, issuer,
/// audience and lifetime, configured in <c>Api/Extensions/AuthenticationExtensions.cs</c> - and a
/// second validation path could disagree with the first about whether a token is acceptable, which is
/// a security defect rather than a convenience.
/// </para>
/// <para>
/// <strong>Nothing here logs, and no token reaches a failure channel.</strong> Issuing tokens is this
/// type's purpose, so a successful result necessarily returns the minted access token and, on the
/// paths that mint one, the refresh token - that is the return contract and it is the only place
/// token material travels. What is excluded is every other channel: no token, signing secret or
/// stored hash reaches a log, a message or a failure reason, and there is deliberately no logger on
/// this type at all. A failure says which rule refused the request and nothing more.
/// </para>
/// </remarks>
internal sealed class JwtTokenService : ITokenService
{
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
    /// The claim carrying the instant a token was issued, as seconds since the Unix epoch.
    /// </summary>
    /// <remarks>
    /// Fixed by RFC 7519 section 4.1.6 rather than by this application, and spelled here rather than
    /// taken from a library constant for the same reason the rest of the vocabulary is spelled in
    /// <see cref="DnnClaimTypes"/>: the wire format must not shift when a library renames its own
    /// constants between major versions. It is emitted because the token's own account of when it was
    /// minted is what makes an issued token auditable after the fact; nothing in this solution
    /// validates against it, so its presence cannot tighten or loosen any check.
    /// </remarks>
    private const string IssuedAtClaimType = "iat";

    /// <summary>The value written for a true boolean claim.</summary>
    private const string TrueValue = "true";

    /// <summary>The value written for a false boolean claim.</summary>
    private const string FalseValue = "false";

    /// <summary>The message reported with <see cref="TokenStoreUnavailableCode"/>.</summary>
    private const string TokenStoreUnavailableMessage =
        "The refresh token could not be recorded, so no token pair was issued.";

    private readonly RefreshTokenStore _refreshTokens;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SigningCredentials _signingCredentials;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _accessTokenLifetime;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly ISecurityDiagnostics _diagnostics;

    /// <summary>Initialises a new instance of the <see cref="JwtTokenService"/> class.</summary>
    /// <param name="refreshTokens">The refresh-token store; a singleton, like this service.</param>
    /// <param name="clock">The injected clock, so expiry and rotation stay testable.</param>
    /// <param name="scopeFactory">
    /// Used to resolve a scope for the entitlement re-read that renewal performs. A factory rather than
    /// a provider, and used per call rather than held, for the reason given in the type remarks.
    /// </param>
    /// <param name="jwtOptions">The bound JWT configuration.</param>
    /// <param name="diagnostics">
    /// Records the one anomaly this service absorbs rather than reports: a permission read that fails while a
    /// token is being renewed, which yields a token asserting no permission keys instead of refusing the
    /// renewal.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">
    /// The bound configuration cannot sign a token: the secret is missing or shorter than the 256 bits
    /// HMAC-SHA256 requires, the issuer or audience is blank, or a lifetime is not positive.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Configuration is validated here, once, rather than at the first sign-in. The alternative is an
    /// authentication outage whose only symptom is a cryptographic exception on a live request; failing
    /// while the container is still starting is a fault an operator can read, and the API layer's
    /// start-up validation raises the same failures earlier still, so this is the backstop for a
    /// service constructed directly. Every message names the setting that is wrong and reports the
    /// secret's <em>length</em> at most - the value itself is never echoed, logged, truncated or
    /// hashed for display, because a misconfiguration report must not leak the value it complains
    /// about. There is no fallback secret and no substituted issuer, audience or lifetime: a signing
    /// key that ships with the code is a published key.
    /// </para>
    /// <para>
    /// The validated values are copied into readonly fields rather than kept as an options reference.
    /// <see cref="JwtOptions"/> is a mutable class, so retaining it would let a reload change the
    /// issuer or the key under a request that had already begun, and the two halves of a token - the
    /// claims and the signature - would then belong to different configurations. Copying also means
    /// the signing key and its credentials are constructed once, which lets the library cache the
    /// cryptographic provider behind them instead of rebuilding it per token.
    /// </para>
    /// </remarks>
    public JwtTokenService(
        RefreshTokenStore refreshTokens,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        IOptions<JwtOptions> jwtOptions,
        ISecurityDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);

        _refreshTokens = refreshTokens ?? throw new ArgumentNullException(nameof(refreshTokens));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

        JwtOptions options = jwtOptions.Value
            ?? throw new ArgumentNullException(
                nameof(jwtOptions),
                "The JWT configuration section is not bound, so no access token can be signed.");

        List<string> failures = [.. options.Validate()];

        // The character-length rule the options type enforces already implies this one, because no
        // character encodes to fewer than one UTF-8 byte. It is asserted anyway, in the unit the
        // signing library actually measures, so that the guarantee stated in the exception summary -
        // 256 bits of key material - is checked rather than inferred.
        if (!string.IsNullOrWhiteSpace(options.Secret))
        {
            int byteLength = Encoding.UTF8.GetByteCount(options.Secret);

            if (byteLength < JwtOptions.MinimumSecretByteLength)
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1} supplies {2} UTF-8 bytes of key material and at least {3} are required. "
                    + "HMAC-SHA256 signs with a key of at least 256 bits.",
                    JwtOptions.SectionName,
                    nameof(JwtOptions.Secret),
                    byteLength,
                    JwtOptions.MinimumSecretByteLength));
            }
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(JwtOptions), failures);
        }

        _issuer = options.Issuer;
        _audience = options.Audience;
        _accessTokenLifetime = TimeSpan.FromMinutes(options.ExpirationMinutes);
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret)),
            SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Every fact the token asserts arrives as a parameter, which is what keeps this member free of
    /// repository access and therefore safe to run in a singleton. The roles and permission keys were
    /// resolved by the sign-in service, from the <c>Roles</c>, <c>UserRoles</c> and <c>RoleGroups</c>
    /// tables, before it called this method; re-reading them here would duplicate that work and could
    /// disagree with it. They are asserted exactly as handed over - same values, same order,
    /// duplicates intact, casing untouched - because this service is not the authority on what a
    /// caller holds and silently normalising the set would make the token disagree with the read that
    /// produced it. That prohibition is stated on the contract, not chosen here.
    /// </para>
    /// <para>
    /// The refresh half is recorded before the pair is returned. Verifying that rather than assuming it
    /// is what keeps the documented failure honest: handing back a refresh token the store never kept
    /// would look like success and then fail at the caller's first exchange, one request too late to
    /// explain. The method is synchronous inside its asynchronous contract because everything it does
    /// is processor-bound - signing a token and a locked insertion into an in-process store - and
    /// pushing that onto a worker thread would add latency and a context switch for no benefit.
    /// </para>
    /// </remarks>
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

        // The store copies both collections into read-only snapshots of its own, so a caller that
        // keeps mutating the lists it passed cannot alter what a later renewal is measured against.
        RefreshTokenSubject subject = new(userId, portalId, userName, isSuperUser, roles, permissionKeys);

        RefreshTokenIssueResult issued = _refreshTokens.Issue(subject);

        if (issued.Outcome != RefreshTokenOutcome.Succeeded || string.IsNullOrEmpty(issued.RefreshToken))
        {
            return Task.FromResult(StoreUnavailable());
        }

        LoginResponse response = BuildResponse(subject, portalId, issued.RefreshToken);

        return Task.FromResult(Result<LoginResponse>.Success(response));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The presented value is read first, so material that is already unusable is refused without any
    /// database work. That reading changes nothing and authorises nothing: the store repeats every one
    /// of its checks inside the lock that performs the rotation, and it is that re-check which decides
    /// whether a replacement is issued. A value that is blank is refused before the store is consulted
    /// at all, and the refusal never repeats what was presented.
    /// </para>
    /// <para>
    /// Identity is taken from the store's own record - who the caller is, which tenant, which sign-in
    /// name - and never from the request, which carries nothing but the token. Authority is the
    /// opposite case and is re-read for this exchange, so a role granted or withdrawn since the last
    /// rotation takes effect within one access-token lifetime instead of persisting until the caller
    /// signs in again.
    /// </para>
    /// <para>
    /// The replacement access token is minted only after the rotation has succeeded. Minting first
    /// would hand back a token whose refresh half might then be refused, and on a concurrent exchange
    /// it would hand back two.
    /// </para>
    /// </remarks>
    public async Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            // Nothing was presented, so nothing can match. Reported as the not-found case rather than
            // as a distinct one, because inventing a code for "you sent an empty string" would tell an
            // unauthenticated caller how this service classifies its input.
            return Refusal(RefreshTokenOutcome.Unknown);
        }

        RefreshTokenInspection inspection = _refreshTokens.Inspect(refreshToken);

        if (inspection.Outcome != RefreshTokenOutcome.Succeeded || inspection.Subject is null)
        {
            RespondToReplay(inspection.Outcome, inspection.OwnerUserId);

            return Refusal(inspection.Outcome);
        }

        RefreshTokenSubject presented = inspection.Subject;

        // Read from the record, once, and not coalesced to a stand-in value. Every token this service
        // issues names a tenant, so a recorded snapshot without one cannot have come from a supported
        // path: that is an invariant violation rather than an authentication outcome, and reporting it
        // as a refused token would hide a defect behind a message about credentials.
        int portalId = presented.PortalId
            ?? throw new InvalidOperationException(
                "The recorded refresh-token snapshot carries no portal identifier, so the caller's "
                + "entitlements cannot be re-read.");

        RefreshTokenSubject renewed = await ReadCurrentAuthorityAsync(presented, portalId, cancellationToken)
            .ConfigureAwait(false);

        // Marking the presented value used and writing its successor happen inside one lock in the
        // store, so two concurrent presentations of the same value cannot both be honoured.
        RefreshTokenRotationResult rotated = _refreshTokens.Rotate(refreshToken, renewed);

        if (rotated.Outcome != RefreshTokenOutcome.Succeeded)
        {
            // Reached when a concurrent request consumed the same value between the reading above and
            // this exchange. The store answers a replay itself, under the lock that detected it, so
            // the account-wide revocation has already happened and nothing further is owed here.
            return Refusal(rotated.Outcome);
        }

        if (string.IsNullOrEmpty(rotated.RefreshToken))
        {
            return StoreUnavailable();
        }

        LoginResponse response = BuildResponse(renewed, portalId, rotated.RefreshToken);

        return Result<LoginResponse>.Success(response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Revokes the whole family behind the presented value, not the single generation presented:
    /// revoking one generation would leave the replacement handed back at the previous exchange fully
    /// usable, so a logout would skip a generation rather than end the session. Deliberately silent
    /// about whether anything was there to revoke - reporting that distinction would turn signing off
    /// into an oracle telling an unauthenticated caller whether a value it holds was ever genuine - and
    /// deliberately confined to refresh material: there is no register of blocked access tokens here,
    /// because consulting server-side state on every request would give up the statelessness that is
    /// the reason for issuing a bearer token at all.
    /// </remarks>
    public Task<Result> RevokeRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            // Nothing was presented, so nothing is exchangeable that was not already: the
            // post-condition this member promises holds without touching the store.
            return Task.FromResult(Result.Success());
        }

        return Task.FromResult(Retired(_refreshTokens.Revoke(refreshToken)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent, so an account holding nothing succeeds. That is what lets a caller retry a theft
    /// response or an administrative reset without having to discover whether the first attempt landed,
    /// and it is why <see cref="RefreshAsync"/> can answer a replay by calling into the same operation
    /// however many times a stolen value is presented. The identifier is used exactly as supplied: 0
    /// and -1 are legitimate account identifiers, so neither is read as meaning "no account".
    /// </remarks>
    public Task<Result> RevokeAllRefreshTokensAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Retired(_refreshTokens.RevokeAllForUser(userId)));
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
    /// treating either as theft would end every session an account holds for the sake of a lapsed token. Because the
    /// revocation reaches the replayed record too, a third and fourth presentation of the same value
    /// classify as revoked instead: the theft response fires once and then falls quiet.
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
    /// <param name="portalId">The tenant recorded against that token.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A snapshot carrying the same identity and the caller's present entitlements.</returns>
    /// <remarks>
    /// <para>
    /// The identity facts - who the caller is, which tenant, which sign-in name - are carried across
    /// from the record unchanged, because a renewal is the same account continuing and the request
    /// carries nothing that could name a different one. The three authority facts are the opposite
    /// case and are all re-read here: the roles, the permission keys and the host-level flag.
    /// </para>
    /// <para>
    /// The host-level flag is re-read rather than copied forward because copying it would let authority
    /// that has been withdrawn outlive the withdrawal for as long as the caller kept exchanging tokens.
    /// Membership is deliberately ignored on that read - the repository documents a null portal
    /// identifier as the way a host account is resolved, since such an account need hold no membership
    /// row - and being an installation-wide super-user is a property of the account rather than of one
    /// tenant. The account being read is the one recorded against the token, so identity is not in
    /// question; only its present authority is.
    /// </para>
    /// <para>
    /// An account that cannot be read confers no elevation, which fails closed. That value is not
    /// observable in practice either: the sign-in service re-resolves the account itself after this
    /// rotation and refuses the whole exchange when it has gone, so a token asserting the closed value
    /// never reaches a caller. Refusing here instead would mean inventing a failure code the contract
    /// does not define.
    /// </para>
    /// <para>
    /// The scope is created here and disposed on return, which is what keeps a singleton free of a
    /// captured scoped instance; every read below shares that one scope, so they observe a single
    /// consistent view. It is disposed asynchronously because the scoped graph inside it holds a
    /// database context, whose asynchronous disposal is what returns its connection to the pool
    /// without blocking.
    /// </para>
    /// <para>
    /// Role assignments and the tenant's roles are read separately and joined here on the role
    /// identifier rather than through the assignment's navigation property, so this method does not
    /// depend on whether the repository happened to populate one. Nothing here touches a database
    /// context: the role and account reads go through the domain layer's repository abstractions and
    /// the permission read through the application layer's service abstraction.
    /// </para>
    /// <para>
    /// A permission read that fails is treated as conferring nothing rather than as a reason to refuse
    /// the renewal: the caller's credential is not in question, and a token asserting no permission
    /// keys still authenticates while simply offering no client-side affordances. That fails closed,
    /// which is the safe direction, and the server re-evaluates every permission on every request in
    /// any case - the entries in a token inform the client and never decide access.
    /// </para>
    /// <para>
    /// BUT THE SUBSTITUTION IS RECORDED, which an earlier revision did not do. An empty key set is
    /// indistinguishable from a caller who genuinely holds nothing, so silently substituting one turned a
    /// dependency failure into a plausible-looking token: every affordance vanishes from the client, the
    /// caller reports that the application has stopped working, and no log line anywhere says why. Only the
    /// failure CODE is recorded, through a contract that accepts no message and discards anything that is not
    /// code-shaped.
    /// </para>
    /// </remarks>
    private async Task<RefreshTokenSubject> ReadCurrentAuthorityAsync(
        RefreshTokenSubject presented,
        int portalId,
        CancellationToken cancellationToken)
    {
        // One reading of the clock decides every window in this exchange. Two readings could straddle
        // the instant an assignment becomes effective or lapses, and a token would then be able to
        // assert a role the same call had just refused.
        DateTime asOfUtc = _clock.UtcNow;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        IReadOnlyList<UserRole> assignments = await roles
            .GetUserRolesAsync(portalId, presented.UserId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Role> tenantRoles = await roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> roleNames = ResolveEligibleRoleNames(assignments, tenantRoles, asOfUtc);

        IPermissionService permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        Result<IReadOnlyList<string>> effective = await permissions
            .GetEffectivePermissionKeysAsync(portalId, presented.UserId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (effective.IsFailure)
        {
            // Nothing here is an expected absence: no module and no page are named, and the tenant is the one
            // the presented token was minted for, so every documented failure of that contract at this call
            // site is a genuine dependency or consistency fault.
            _diagnostics.Record(
                SecurityDiagnosticEvent.EffectivePermissionResolutionFailed,
                portalId,
                presented.UserId,
                effective.Reason?.Code);
        }

        IReadOnlyList<string> permissionKeys = effective.IsSuccess
            ? effective.Value
            : Array.Empty<string>();

        IUserRepository accounts = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        User? account = await accounts
            .GetAsync(portalId: null, presented.UserId, cancellationToken)
            .ConfigureAwait(false);

        return new RefreshTokenSubject(
            presented.UserId,
            portalId,
            presented.UserName,
            account?.IsSuperUser ?? false,
            roleNames,
            permissionKeys);
    }

    /// <summary>
    /// Reduces a caller's role assignments to the names that apply at one instant.
    /// </summary>
    /// <param name="assignments">The caller's assignments in the tenant.</param>
    /// <param name="tenantRoles">The tenant's roles, supplying each identifier's name.</param>
    /// <param name="asOfUtc">The instant every window is measured against.</param>
    /// <returns>The distinct names of the assignments that apply, in a stable order.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces the terminal <c>GetRolesByUser</c> procedure at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider:L429-L447</c>, whose
    /// predicate is <c>Roles.PortalId = @PortalId AND (EffectiveDate &lt;= getdate() or EffectiveDate is
    /// null) AND (ExpiryDate &gt;= getdate() or ExpiryDate is null)</c>. Both bounds are therefore
    /// <em>inclusive</em>: an assignment effective at this very instant applies, and one expiring at
    /// this very instant still applies. The tenant half of that predicate is applied by the repository,
    /// which scopes assignments through the role - <c>dbo.UserRoles</c> carries no portal column - so a
    /// host-level role, whose portal identifier is null, is excluded by construction.
    /// </para>
    /// <para>
    /// The window test is delegated to <see cref="UserRole.GetStatus(DateTime)"/> rather than restated
    /// here, so the rule has exactly one home. That method also honours what the procedure could not:
    /// the legacy sentinel <c>Null.NullDate</c>, which is <see cref="DateTime.MinValue"/> and means "no
    /// date" rather than "the beginning of time". Reading a minimum-value expiry literally would expire
    /// every unlimited membership the legacy code created, and
    /// <c>Library/Components/Security/Roles/RoleController.vb:L541</c> created exactly those: billing
    /// frequency <c>N</c> set the expiry to that sentinel. The neighbouring cases are honoured by the
    /// same comparison without special-casing - <c>O</c>, one-time, wrote
    /// <c>New System.DateTime(9999, 12, 31)</c> at line 542 and is far in the future, while <c>D</c>,
    /// <c>W</c>, <c>M</c> and <c>Y</c> at lines 543 to 546 wrote a computed day, week, month or year
    /// expiry that is simply compared. No billing period is recalculated here: the stored dates are the
    /// result of that calculation, and recomputing one would be a second opinion on data the store
    /// already holds.
    /// </para>
    /// <para>
    /// MIGRATION: expiry-not-deletion is preserved by the same comparison.
    /// <c>RoleController.vb:L496</c> withdrew a membership by writing an expiry of
    /// <c>DateAdd(DateInterval.Day, -1, Date.Today())</c> - yesterday - rather than deleting the row,
    /// so a withdrawn assignment is a row whose expiry has passed and it confers nothing.
    /// </para>
    /// <para>
    /// No identifier is filtered. <c>Roles.RoleID</c> seeds at 0, so a role identified by zero is live
    /// data rather than an unset value, and the negative identifiers the installation reserves are real
    /// principals; a range test on the identifier would silently drop whichever of them a caller
    /// happened to hold. Names are compared and ordered by exact byte value, so nothing is
    /// case-folded: role names are persisted values that other systems match on. The ordering exists so
    /// that two tokens minted for the same entitlements carry the same claim sequence, which makes them
    /// comparable in a test and in an audit trail.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ResolveEligibleRoleNames(
        IReadOnlyList<UserRole> assignments,
        IReadOnlyList<Role> tenantRoles,
        DateTime asOfUtc)
    {
        if (assignments.Count == 0)
        {
            return Array.Empty<string>();
        }

        Dictionary<int, string> namesByRoleId = new(tenantRoles.Count);

        foreach (Role role in tenantRoles)
        {
            namesByRoleId[role.RoleId] = role.RoleName;
        }

        SortedSet<string> eligible = new(StringComparer.Ordinal);

        foreach (UserRole assignment in assignments)
        {
            if (assignment.GetStatus(asOfUtc) != RoleStatus.Active)
            {
                continue;
            }

            // An assignment whose role is not among the tenant's roles has no name to assert, and a
            // role with a blank name cannot be matched by any authorisation check, so neither becomes
            // a claim. This is not the filtering the issuing contract forbids: there is no value here
            // to carry, rather than a value being judged unworthy of carrying.
            string? roleName = namesByRoleId.GetValueOrDefault(assignment.RoleId);

            if (!string.IsNullOrWhiteSpace(roleName))
            {
                eligible.Add(roleName);
            }
        }

        return eligible.Count == 0 ? Array.Empty<string>() : [.. eligible];
    }

    /// <summary>Builds the response pair for a snapshot and the refresh token just recorded.</summary>
    /// <param name="subject">The facts the access token is to assert.</param>
    /// <param name="portalId">The tenant, passed explicitly so no absent-value stand-in is needed.</param>
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
    /// lifetime as a duration and the refresh token's expiry are all withheld even though this method
    /// holds or could compute each of them: the scheme is fixed by the API contract rather than restated
    /// per response, one expiry representation cannot disagree with itself, and the refresh token's
    /// expiry is rotation state that belongs to the store. Neither the issuer, the audience, the
    /// algorithm, the token identifier nor any family or generation number appears on the response for
    /// the same reason - they are how the token was made, not something its holder needs.
    /// </para>
    /// <para>
    /// The three advisory booleans are left at their defaults here, because this service is not told
    /// them: a forced credential update, an expiring credential and an incomplete profile are all
    /// account facts read from rows this service never sees. The sign-in service overwrites two of
    /// the three alongside the fully populated projection - the forced-update and expiring-credential
    /// advisories (<c>AuthService.cs:L642-L643</c> and <c>L729-L730</c>). The profile advisory has no
    /// assignment anywhere in production code and therefore stays <see langword="false"/> on every
    /// response; the reason is recorded on the member itself in <c>LoginResponse</c>.
    /// </para>
    /// </remarks>
    private LoginResponse BuildResponse(RefreshTokenSubject subject, int portalId, string refreshToken)
    {
        DateTime issuedAtUtc = _clock.UtcNow;
        DateTime expiresAtUtc = issuedAtUtc.Add(_accessTokenLifetime);

        return new LoginResponse
        {
            AccessToken = CreateAccessToken(subject, portalId, issuedAtUtc, expiresAtUtc),
            ExpiresAtUtc = expiresAtUtc,
            RefreshToken = refreshToken,
            User = new CurrentUserDto
            {
                UserId = subject.UserId,
                PortalId = portalId,

                // The projection's own default stands in for a snapshot that carries no name, because
                // the property is not nullable and the sign-in service replaces the whole projection
                // moments later. The token itself omits the claim entirely in that case rather than
                // asserting an empty sign-in name.
                Username = subject.UserName ?? string.Empty,
                IsSuperUser = subject.IsSuperUser,
                Roles = subject.Roles,
                Permissions = subject.PermissionKeys,
            },
        };
    }

    /// <summary>Signs one access token asserting the facts in a snapshot.</summary>
    /// <param name="subject">The facts to assert.</param>
    /// <param name="portalId">The tenant the token is issued for.</param>
    /// <param name="issuedAtUtc">The instant the token is issued, and its not-before bound.</param>
    /// <param name="expiresAtUtc">The instant the token lapses.</param>
    /// <returns>The compact serialised token.</returns>
    /// <remarks>
    /// <para>
    /// Roles are asserted under the standard role claim type so that the framework's own role checks and
    /// the <c>[Authorize(Roles = ...)]</c> attribute work without any mapping, while permissions use the
    /// private type from <see cref="DnnClaimTypes"/> because no standard claim means "granular
    /// permission key". Each is emitted once per entry and never as a delimited list. MIGRATION: the
    /// legacy code flattened grants into a semicolon-delimited string built with a leading delimiter,
    /// which is why every legacy consumer had to guard an empty first element; separate entries remove
    /// the parsing step entirely.
    /// </para>
    /// <para>
    /// Every time claim comes from the single instant passed in, so issued-at and not-before are the
    /// same reading and expiry is that reading plus the configured lifetime. Nothing here consults an
    /// ambient system clock, which is what lets a test move time deliberately.
    /// </para>
    /// <para>
    /// The token identifier is a fresh cryptographic random value per token rather than a counter or a
    /// hash of caller data, so it identifies a token without revealing anything about how many have been
    /// issued or to whom.
    /// </para>
    /// </remarks>
    private string CreateAccessToken(
        RefreshTokenSubject subject,
        int portalId,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        List<Claim> claims =
        [
            new Claim(
                DnnClaimTypes.Subject,
                subject.UserId.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer32),
            new Claim(DnnClaimTypes.JwtId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            new Claim(
                DnnClaimTypes.PortalId,
                portalId.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer32),
            new Claim(
                DnnClaimTypes.SuperUser,
                subject.IsSuperUser ? TrueValue : FalseValue,
                ClaimValueTypes.Boolean),
            new Claim(
                IssuedAtClaimType,
                EpochTime.GetIntDate(issuedAtUtc).ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        ];

        if (!string.IsNullOrWhiteSpace(subject.UserName))
        {
            claims.Add(new Claim(DnnClaimTypes.UniqueName, subject.UserName));
        }

        AddClaimPerValue(claims, ClaimTypes.Role, subject.Roles);
        AddClaimPerValue(claims, DnnClaimTypes.Permission, subject.PermissionKeys);

        JwtSecurityToken token = new(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: issuedAtUtc,
            expires: expiresAtUtc,
            signingCredentials: _signingCredentials);

        return _handler.WriteToken(token);
    }

    /// <summary>Adds one claim of the given type per entry, preserving the entries exactly.</summary>
    /// <param name="claims">The claim set being built.</param>
    /// <param name="claimType">The claim type to write each entry under.</param>
    /// <param name="values">The entries.</param>
    /// <remarks>
    /// Order, casing and duplicates are preserved, because on the issuing path these entries were
    /// resolved by the caller and normalising them would make the token disagree with the read that
    /// produced it. The one entry that cannot be written is a null: a claim has no representation for the
    /// absence of a value and the framework's own constructor refuses one, so skipping it asserts nothing
    /// that was there to assert. The collections this service is handed are declared as holding
    /// non-nullable strings, so the guard is unreachable through a caller that honours its own contract
    /// and exists to keep one that does not from failing a sign-in with an argument exception.
    /// </remarks>
    private static void AddClaimPerValue(
        List<Claim> claims,
        string claimType,
        IReadOnlyList<string> values)
    {
        foreach (string value in values)
        {
            if (value is not null)
            {
                claims.Add(new Claim(claimType, value));
            }
        }
    }

    /// <summary>Translates a store outcome into the failure code the contract fixes for it.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    /// <returns>The corresponding failed result.</returns>
    /// <remarks>
    /// <para>
    /// The contract fixes both the codes and the order in which they are considered - absent, then
    /// revoked, then already used, then expired - and the store's own classifier applies that same
    /// order, so this is a translation rather than a second decision. Revoked deliberately precedes
    /// already-used: the theft response revokes the replayed record along with the rest, so a repeated
    /// presentation lands on the revoked arm and cannot be used as an instrument for ending sessions the
    /// account has opened since.
    /// </para>
    /// <para>
    /// A store that could not record the rotated pair is reported as unavailable rather than as a
    /// rejected token, because the caller's material was good and a retry may well succeed. Any outcome
    /// this method does not recognise is reported as absent, which is the closed answer: refusing a
    /// token whose state cannot be named is the only safe reading. No arm names the presented value or
    /// anything derived from it.
    /// </para>
    /// </remarks>
    private static Result<LoginResponse> Refusal(RefreshTokenOutcome outcome)
    {
        return outcome switch
        {
            RefreshTokenOutcome.Revoked or RefreshTokenOutcome.AlreadyRevoked => Result<LoginResponse>.Failure(
                RefreshTokenRevokedCode,
                "The refresh token has been revoked."),
            RefreshTokenOutcome.AlreadyUsed => Result<LoginResponse>.Failure(
                RefreshTokenAlreadyUsedCode,
                "The refresh token has already been exchanged."),
            RefreshTokenOutcome.Expired => Result<LoginResponse>.Failure(
                RefreshTokenExpiredCode,
                "The refresh token has expired."),
            RefreshTokenOutcome.CapacityExhausted => StoreUnavailable(),
            _ => Result<LoginResponse>.Failure(
                RefreshTokenNotFoundCode,
                "The refresh token is not recognised."),
        };
    }

    /// <summary>Builds the failure reported when refresh material could not be recorded.</summary>
    /// <returns>The failed result.</returns>
    private static Result<LoginResponse> StoreUnavailable()
    {
        return Result<LoginResponse>.Failure(TokenStoreUnavailableCode, TokenStoreUnavailableMessage);
    }

    /// <summary>Reports the outcome of a revocation, which is expected to succeed.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    /// <returns>
    /// Success once the material is known not to be exchangeable, including when it never was; the
    /// store-unavailable failure when the revocation could not be recorded and the material therefore
    /// remains exchangeable.
    /// </returns>
    /// <remarks>
    /// Unknown and already-revoked are successes rather than failures, because both leave the caller in
    /// exactly the state it asked for. The contract requires that idempotence: a logout that fails is
    /// worse than useless, since a client that cannot complete one is likely to keep the token, and a
    /// caller answering a suspected theft must be able to retry without first discovering whether the
    /// previous attempt landed.
    /// </remarks>
    private static Result Retired(RefreshTokenOutcome outcome)
    {
        return outcome == RefreshTokenOutcome.CapacityExhausted
            ? Result.Failure(TokenStoreUnavailableCode, TokenStoreUnavailableMessage)
            : Result.Success();
    }
}
