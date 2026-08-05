using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Persists refresh-token digests and their family lifecycle so rotation and revocation remain
/// consistent across process restarts and replicas.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy forms-authentication ticket had no server-side rotation record. The target
/// stores only a one-way token digest plus minimal identity, expiry and revocation state. No raw
/// refresh token, access token, role, permission, password or key may be persisted by this contract.
/// </para>
/// <para>
/// Every operation is asynchronous so that an implementation backed by shared storage satisfies this
/// contract without changing it; the shipped implementation is process-local and completes
/// synchronously. Rotation and revocation are atomic at the store boundary, and cancellation is
/// observed before any state is examined.
/// </para>
/// </remarks>
public interface IRefreshTokenStore
{
    /// <summary>Issues the first token in a new refresh family.</summary>
    /// <param name="subject">The minimal identity the family represents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The issued token or the store refusal.</returns>
    Task<RefreshTokenIssueResult> IssueAsync(
        RefreshTokenSubject subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects presented material before fallible account and tenant eligibility reads are performed.
    /// </summary>
    /// <param name="refreshToken">The opaque material presented by the caller.</param>
    /// <param name="clientBinding">
    /// A bounded server-observed client fingerprint used only to distinguish a near-simultaneous retry
    /// from a replay arriving from another client.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored subject when redeemable, or a refusal outcome.</returns>
    Task<RefreshTokenInspection> InspectAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically consumes presented material and issues its single successor.</summary>
    /// <param name="refreshToken">The opaque material being redeemed.</param>
    /// <param name="clientBinding">
    /// The same bounded server-observed fingerprint used by <see cref="InspectAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replacement token or the store refusal.</returns>
    Task<RefreshTokenRotationResult> RotateAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes the whole family addressed by any one of its token generations.</summary>
    /// <param name="refreshToken">The opaque material addressing the family.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revocation outcome.</returns>
    Task<RefreshTokenOutcome> RevokeAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes every refresh family belonging to one account.</summary>
    /// <param name="userId">Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revocation outcome.</returns>
    Task<RefreshTokenOutcome> RevokeAllForUserAsync(
        int userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The minimal non-secret identity persisted with a refresh family.
/// </summary>
/// <remarks>
/// Roles, permission keys, display fields and host authority are deliberately absent. Authorization
/// is re-read from authoritative stores, and access tokens carry only the identity needed to perform
/// those reads.
/// </remarks>
public sealed class RefreshTokenSubject
{
    /// <summary>Initialises a new instance of the <see cref="RefreshTokenSubject"/> class.</summary>
    /// <param name="userId">Account identifier.</param>
    /// <param name="portalId">Tenant identifier.</param>
    public RefreshTokenSubject(int userId, int portalId)
    {
        UserId = userId;
        PortalId = portalId;
    }

    /// <summary>Gets the account identifier.</summary>
    public int UserId { get; }

    /// <summary>Gets the tenant identifier.</summary>
    public int PortalId { get; }
}

/// <summary>The result of issuing the first token in a family.</summary>
public sealed class RefreshTokenIssueResult
{
    private RefreshTokenIssueResult(
        RefreshTokenOutcome outcome,
        string? refreshToken,
        DateTime? expiresAtUtc,
        RefreshTokenSubject? subject)
    {
        Outcome = outcome;
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        Subject = subject;
    }

    /// <summary>Gets the outcome.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>Gets the raw issued token on success.</summary>
    public string? RefreshToken { get; }

    /// <summary>Gets the token expiry on success.</summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>Gets the persisted subject on success.</summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>Creates a successful issue result.</summary>
    /// <param name="refreshToken">The raw token returned exactly once.</param>
    /// <param name="expiresAtUtc">The token expiry.</param>
    /// <param name="subject">The persisted subject.</param>
    /// <returns>The successful result.</returns>
    public static RefreshTokenIssueResult Succeeded(
        string refreshToken,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentNullException.ThrowIfNull(subject);

        return new(
            RefreshTokenOutcome.Succeeded,
            refreshToken,
            expiresAtUtc,
            subject);
    }

    /// <summary>Creates a failed issue result.</summary>
    /// <param name="outcome">The refusal outcome.</param>
    /// <returns>The failed result.</returns>
    public static RefreshTokenIssueResult Failed(RefreshTokenOutcome outcome)
    {
        EnsureFailure(outcome);
        return new(outcome, null, null, null);
    }

    private static void EnsureFailure(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A failed refresh-token result cannot carry the successful outcome.");
        }
    }
}

/// <summary>The result of inspecting presented refresh material.</summary>
public sealed class RefreshTokenInspection
{
    private RefreshTokenInspection(
        RefreshTokenOutcome outcome,
        RefreshTokenSubject? subject,
        DateTime? expiresAtUtc,
        int? ownerUserId)
    {
        Outcome = outcome;
        Subject = subject;
        ExpiresAtUtc = expiresAtUtc;
        OwnerUserId = ownerUserId;
    }

    /// <summary>Gets the outcome.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>Gets the stored subject when redeemable.</summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>Gets the current token expiry when redeemable.</summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>Gets the owning account when the material was recognised.</summary>
    public int? OwnerUserId { get; }

    /// <summary>Creates a successful inspection.</summary>
    /// <param name="subject">The stored subject.</param>
    /// <param name="expiresAtUtc">The current token expiry.</param>
    /// <returns>The successful inspection.</returns>
    public static RefreshTokenInspection Succeeded(
        RefreshTokenSubject subject,
        DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new(
            RefreshTokenOutcome.Succeeded,
            subject,
            expiresAtUtc,
            subject.UserId);
    }

    /// <summary>Creates a failed inspection.</summary>
    /// <param name="outcome">The refusal outcome.</param>
    /// <param name="ownerUserId">The owning account when recognised.</param>
    /// <returns>The failed inspection.</returns>
    public static RefreshTokenInspection Failed(
        RefreshTokenOutcome outcome,
        int? ownerUserId = null)
    {
        EnsureFailure(outcome);
        return new(outcome, null, null, ownerUserId);
    }

    private static void EnsureFailure(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A failed refresh-token inspection cannot carry the successful outcome.");
        }
    }
}

/// <summary>The result of atomically rotating a refresh token.</summary>
public sealed class RefreshTokenRotationResult
{
    private RefreshTokenRotationResult(
        RefreshTokenOutcome outcome,
        string? refreshToken,
        DateTime? expiresAtUtc,
        RefreshTokenSubject? subject)
    {
        Outcome = outcome;
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        Subject = subject;
    }

    /// <summary>Gets the outcome.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>Gets the single replacement token on success.</summary>
    public string? RefreshToken { get; }

    /// <summary>Gets the replacement expiry on success.</summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>Gets the persisted subject on success.</summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>Creates a successful rotation result.</summary>
    /// <param name="refreshToken">The replacement raw token returned exactly once.</param>
    /// <param name="expiresAtUtc">The replacement expiry.</param>
    /// <param name="subject">The persisted subject.</param>
    /// <returns>The successful result.</returns>
    public static RefreshTokenRotationResult Succeeded(
        string refreshToken,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentNullException.ThrowIfNull(subject);
        return new(
            RefreshTokenOutcome.Succeeded,
            refreshToken,
            expiresAtUtc,
            subject);
    }

    /// <summary>Creates a failed rotation result.</summary>
    /// <param name="outcome">The refusal outcome.</param>
    /// <returns>The failed result.</returns>
    public static RefreshTokenRotationResult Failed(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A failed refresh-token rotation cannot carry the successful outcome.");
        }

        return new(outcome, null, null, null);
    }
}
