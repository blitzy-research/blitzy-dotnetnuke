using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Mints minimal signed access tokens and coordinates refresh-token issue, rotation and revocation.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy forms-authentication cookie is replaced by a short-lived bearer token and a
/// single-use refresh family held by <see cref="IRefreshTokenStore"/>. Logout revokes the refresh family;
/// an already-issued access token remains valid until its stamped expiry and is discarded by the client.
/// Token issuance depends on no database object of its own, so it cannot be blocked by a schema this
/// migration is forbidden to alter.
/// </para>
/// <para>
/// Access tokens deliberately contain only subject, tenant, token identifier, issuer, audience and
/// time claims. User names, host flags, roles and permission keys are mutable authority or profile
/// data and are therefore re-read from authoritative storage rather than copied into a bearer token.
/// </para>
/// </remarks>
internal sealed class JwtTokenService : ITokenService
{
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";
    private const string RefreshTokenNotFoundCode = "REFRESH_TOKEN_NOTFOUND";
    private const string RefreshTokenAlreadyUsedCode = "REFRESH_TOKEN_ALREADYUSED";
    private const string RefreshTokenRevokedCode = "REFRESH_TOKEN_REVOKED";
    private const string RefreshTokenExpiredCode = "REFRESH_TOKEN_EXPIRED";
    private const string IssuedAtClaimType = "iat";

    private const string TokenStoreUnavailableMessage =
        "The refresh token could not be recorded, so no token pair was issued.";

    private readonly IRefreshTokenStore _refreshTokens;
    private readonly IClock _clock;
    private readonly SigningCredentials _signingCredentials;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _accessTokenLifetime;
    private readonly JwtSecurityTokenHandler _handler = new();

    /// <summary>Initialises a new instance of the <see cref="JwtTokenService"/> class.</summary>
    /// <param name="refreshTokens">The refresh-token store.</param>
    /// <param name="clock">UTC clock used for issued-at, not-before and expiry.</param>
    /// <param name="jwtOptions">The bound JWT configuration.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The token configuration is invalid.</exception>
    public JwtTokenService(
        IRefreshTokenStore refreshTokens,
        IClock clock,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        JwtOptions options = jwtOptions.Value;
        List<string> failures = [.. options.Validate()];
        if (!string.IsNullOrWhiteSpace(options.Secret))
        {
            int byteLength = Encoding.UTF8.GetByteCount(options.Secret);
            if (byteLength < JwtOptions.MinimumSecretByteLength)
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1} supplies {2} UTF-8 bytes and at least {3} are required.",
                    JwtOptions.SectionName,
                    nameof(JwtOptions.Secret),
                    byteLength,
                    JwtOptions.MinimumSecretByteLength));
            }
        }

        if (failures.Count != 0)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(JwtOptions),
                failures);
        }

        _refreshTokens = refreshTokens;
        _clock = clock;
        _issuer = options.Issuer;
        _audience = options.Audience;
        _accessTokenLifetime = TimeSpan.FromMinutes(options.ExpirationMinutes);
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret)),
            SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    public async Task<Result<LoginResponse>> IssueTokensAsync(
        int userId,
        int portalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshTokenSubject subject = new(userId, portalId);
        RefreshTokenIssueResult issued = await _refreshTokens
            .IssueAsync(subject, cancellationToken)
            .ConfigureAwait(false);

        if (issued.Outcome != RefreshTokenOutcome.Succeeded
            || string.IsNullOrEmpty(issued.RefreshToken))
        {
            return StoreUnavailable();
        }

        return Result<LoginResponse>.Success(
            BuildResponse(subject, issued.RefreshToken));
    }

    /// <inheritdoc />
    public async Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBinding);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Refusal(RefreshTokenOutcome.Unknown);
        }

        RefreshTokenRotationResult rotated = await _refreshTokens
            .RotateAsync(refreshToken, clientBinding, cancellationToken)
            .ConfigureAwait(false);

        if (rotated.Outcome != RefreshTokenOutcome.Succeeded
            || string.IsNullOrEmpty(rotated.RefreshToken)
            || rotated.Subject is null)
        {
            return Refusal(rotated.Outcome);
        }

        return Result<LoginResponse>.Success(
            BuildResponse(rotated.Subject, rotated.RefreshToken));
    }

    /// <inheritdoc />
    public async Task<Result> RevokeRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Success();
        }

        RefreshTokenOutcome outcome = await _refreshTokens
            .RevokeAsync(refreshToken, cancellationToken)
            .ConfigureAwait(false);

        return Retired(outcome);
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAllRefreshTokensAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshTokenOutcome outcome = await _refreshTokens
            .RevokeAllForUserAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return Retired(outcome);
    }

    /// <inheritdoc />
    public async Task<Result> PurgeAccountSessionRecordsAsync(
        int userId,
        int? portalId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshTokenPurgeScope scope = portalId is { } tenant
            ? RefreshTokenPurgeScope.ForAccountInPortal(userId, tenant)
            : RefreshTokenPurgeScope.ForAccount(userId);

        return Erased(await _refreshTokens.PurgeSubjectAsync(scope, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result> PurgePortalSessionRecordsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshTokenPurgeResult purged = await _refreshTokens
            .PurgeSubjectAsync(RefreshTokenPurgeScope.ForPortal(portalId), cancellationToken)
            .ConfigureAwait(false);

        return Erased(purged);
    }

    /// <summary>Translates an erasure outcome into the contract's result vocabulary.</summary>
    /// <param name="purged">What the store reported.</param>
    /// <returns>Success once no record remains, or the store-unavailable failure.</returns>
    /// <remarks>
    /// PRIV-02. Only two answers are possible and both are stated here rather than left to the caller.
    /// "Nothing matched" is SUCCESS - an erasure whose subject held no records has achieved exactly what it
    /// was asked to - and it is not conditioned on
    /// <see cref="IRefreshTokenStore.IsAuthoritativeAcrossReplicas"/> the way a revocation's "unknown" is.
    /// The distinction is real: a revocation that finds nothing may have left a live session on another
    /// replica, whereas a process-local store that holds no record of a subject genuinely holds no personal
    /// data about it, which is the whole claim this member makes. A replica that does hold records erases its
    /// own when its own sweep or its own deletion request reaches it.
    /// </remarks>
    private static Result Erased(RefreshTokenPurgeResult purged) => purged.Answered
        ? Result.Success()
        : Result.Failure(TokenStoreUnavailableCode, TokenStoreUnavailableMessage);

    private LoginResponse BuildResponse(
        RefreshTokenSubject subject,
        string refreshToken)
    {
        DateTime issuedAtUtc = Utc(_clock.UtcNow);
        DateTime expiresAtUtc = issuedAtUtc.Add(_accessTokenLifetime);

        return new LoginResponse
        {
            AccessToken = CreateAccessToken(subject, issuedAtUtc, expiresAtUtc),
            ExpiresAtUtc = expiresAtUtc,
            RefreshToken = refreshToken,

            // The remediation flags are deliberately NOT set here. They are authoritative account state
            // rather than token state, so the authentication service re-reads them and stamps them onto
            // this response after rotation - which is what keeps a completed or newly imposed requirement
            // from being frozen into a token for its whole lifetime.
            User = new CurrentUserDto
            {
                UserId = subject.UserId,
                PortalId = subject.PortalId,
            },
        };
    }

    private string CreateAccessToken(
        RefreshTokenSubject subject,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        byte[] tokenIdentifierBytes = RandomNumberGenerator.GetBytes(16);
        string tokenIdentifier;
        try
        {
            tokenIdentifier = Convert.ToHexString(tokenIdentifierBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenIdentifierBytes);
        }

        Claim[] claims =
        [
            new(
                DnnClaimTypes.Subject,
                subject.UserId.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer32),
            new(DnnClaimTypes.JwtId, tokenIdentifier),
            new(
                DnnClaimTypes.PortalId,
                subject.PortalId.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer32),
            new(
                IssuedAtClaimType,
                EpochTime.GetIntDate(issuedAtUtc).ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        ];

        JwtSecurityToken token = new(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: issuedAtUtc,
            expires: expiresAtUtc,
            signingCredentials: _signingCredentials);

        return _handler.WriteToken(token);
    }

    private static Result<LoginResponse> Refusal(RefreshTokenOutcome outcome) => outcome switch
    {
        RefreshTokenOutcome.Revoked => Result<LoginResponse>.Failure(
            RefreshTokenRevokedCode,
            "The refresh token has been revoked."),
        RefreshTokenOutcome.AlreadyUsed or RefreshTokenOutcome.ConcurrentUse
            => Result<LoginResponse>.Failure(
                RefreshTokenAlreadyUsedCode,
                "The refresh token has already been exchanged."),
        RefreshTokenOutcome.Expired => Result<LoginResponse>.Failure(
            RefreshTokenExpiredCode,
            "The refresh token has expired."),
        RefreshTokenOutcome.StoreUnavailable or RefreshTokenOutcome.CapacityExhausted
            => StoreUnavailable(),
        _ => Result<LoginResponse>.Failure(
            RefreshTokenNotFoundCode,
            "The refresh token was not found."),
    };

    private static Result<LoginResponse> StoreUnavailable() =>
        Result<LoginResponse>.Failure(
            TokenStoreUnavailableCode,
            TokenStoreUnavailableMessage);

    /// <summary>
    /// Translates a revocation outcome, reporting success ONLY when the store proved the family was
    /// retired.
    /// </summary>
    /// <param name="outcome">What the store reported.</param>
    /// <returns>A successful result only for a proven retirement.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ <c>Unknown</c> IS A FAILURE HERE, AND IT USED TO BE A SUCCESS. Every outcome except
    /// store-unavailable and capacity-exhausted was mapped to success, on the reading that a token the
    /// store cannot find must already be gone. That reading is only sound for a store that sees ALL of the
    /// state. With families held per process it was demonstrably unsound: replica B, asked to end a session
    /// established on replica A, does not recognise the token, answered <c>Unknown</c>, and reported a
    /// successful sign-out while replica A went on honouring the very token that was presented for
    /// revocation. The client then discarded its only copy, so the live session could not even be retried.
    /// </para>
    /// <para>
    /// So the rule is now the strict one: a retirement is successful when the store retired something
    /// (<c>Succeeded</c>) or when it holds the family and had already retired it
    /// (<c>AlreadyRevoked</c>). Anything else - a token this store never knew, an expired or replayed
    /// presentation, an unreachable store - is reported as an unconfirmed retirement so the caller keeps
    /// the credential and can try again. A deployment that wants "unknown means gone" to be true configures
    /// the shared store, where the store's ignorance really is the whole system's.
    /// </para>
    /// </remarks>
    private Result Retired(RefreshTokenOutcome outcome) => outcome switch
    {
        RefreshTokenOutcome.Succeeded or RefreshTokenOutcome.AlreadyRevoked => Result.Success(),

        // An authoritative store that holds no such family has proved there is nothing left to retire, so
        // this is a completed sign-out. A process-local store has proved only its own ignorance.
        RefreshTokenOutcome.Unknown when _refreshTokens.IsAuthoritativeAcrossReplicas => Result.Success(),
        RefreshTokenOutcome.StoreUnavailable or RefreshTokenOutcome.CapacityExhausted =>
            Result.Failure(TokenStoreUnavailableCode, TokenStoreUnavailableMessage),
        RefreshTokenOutcome.Expired => Result.Failure(
            RefreshTokenExpiredCode,
            "The refresh token has expired, so no session was retired by this request."),
        _ => Result.Failure(
            RefreshTokenNotFoundCode,
            "This instance holds no such refresh-token family, so no session can be confirmed as retired. "
            + "Retain the credential and retry."),
    };

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
