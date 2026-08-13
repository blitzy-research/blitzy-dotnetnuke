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
    /// <summary>
    /// Reports whether this store sees every replica's refresh-token families, so that "no such family
    /// here" is the same statement as "no such family anywhere".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one fact a caller cannot deduce and must not assume. A revocation that finds nothing is
    /// a COMPLETED retirement when the store is authoritative and an UNCONFIRMED one when it is not: a
    /// process-local store asked to end a session established on another instance recognises nothing, and
    /// reporting that as a successful sign-out leaves the session live somewhere else while the caller
    /// discards the only credential that could have retried.
    /// </para>
    /// <para>
    /// An implementation returns <see langword="true"/> only when its state is genuinely shared and
    /// durable - a store every replica reads and writes, surviving a restart.
    /// </para>
    /// </remarks>
    bool IsAuthoritativeAcrossReplicas { get; }

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

    /// <summary>
    /// DELETES every record this store holds about one subject, rather than marking it revoked.
    /// </summary>
    /// <param name="scope">The account, the tenant, or the account within one tenant to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many records were removed, or the store refusal.</returns>
    /// <remarks>
    /// <para>
    /// PRIV-02. REVOCATION IS NOT DELETION, AND THIS CONTRACT USED TO OFFER ONLY THE FIRST.
    /// <see cref="RevokeAsync"/> and <see cref="RevokeAllForUserAsync"/> STAMP a record so that a family
    /// can no longer be redeemed and a replay of one of its generations is still recognisable - which is
    /// exactly right for a sign-out and exactly wrong for a deletion. Every stamped row keeps its token
    /// digest and its subject identifiers, so an account or a tenant that had been deleted from the
    /// application went on being described here until its family ceiling elapsed, with no member on this
    /// contract capable of removing the description.
    /// </para>
    /// <para>
    /// The deletion paths call this AFTER their own removal has been committed, and the reason is the
    /// asymmetry between the two failures: revoking first and deleting the rows afterwards leaves, in the
    /// worst case, revoked rows describing a subject that no longer exists - reclaimed by
    /// <see cref="PurgeRetiredAsync"/> in any case - whereas deleting the rows first and then failing to
    /// remove the account would leave an account whose sessions were no longer even recorded.
    /// </para>
    /// <para>
    /// An implementation MUST NOT interpret a scope naming neither an account nor a tenant;
    /// <see cref="RefreshTokenPurgeScope"/> makes that unrepresentable rather than trusting each
    /// implementation to check.
    /// </para>
    /// </remarks>
    Task<RefreshTokenPurgeResult> PurgeSubjectAsync(
        RefreshTokenPurgeScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclaims records that can no longer serve any purpose: families past their absolute ceiling, and
    /// revoked records held beyond the configured retention.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many records were removed, or the store refusal.</returns>
    /// <remarks>
    /// <para>
    /// PRIV-02. IT EXISTS SO THAT RECLAMATION IS NOT A SIDE EFFECT OF TRAFFIC. Both shipped stores
    /// reclaimed expired records only while issuing or rotating a token, which means an installation that
    /// nobody signs in to retains every expired and revoked record it ever wrote - indefinitely, and
    /// precisely in the case where there is no operator activity to notice. A caller independent of any
    /// operation drives this member on a schedule.
    /// </para>
    /// <para>
    /// It is idempotent and safe to call concurrently with any other member. It never removes a record that
    /// is still redeemable, and it never removes a revoked record before the retention window has elapsed:
    /// a revoked digest is what makes a replay of that family recognisable, so the window is the documented
    /// minimum period for which that signal is kept.
    /// </para>
    /// </remarks>
    Task<RefreshTokenPurgeResult> PurgeRetiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Names the subject whose refresh-token records are to be erased.
/// </summary>
/// <remarks>
/// PRIV-02. A closed set of three factory methods rather than a pair of nullable properties a caller
/// assembles, because the fourth combination - neither identifier supplied - would instruct a store to
/// erase every record it holds. Making it unrepresentable is stronger than validating for it in three
/// implementations independently.
/// </remarks>
public sealed class RefreshTokenPurgeScope
{
    private RefreshTokenPurgeScope(int? userId, int? portalId)
    {
        UserId = userId;
        PortalId = portalId;
    }

    /// <summary>Gets the account whose records are erased, or <see langword="null"/> for every account.</summary>
    public int? UserId { get; }

    /// <summary>Gets the tenant whose records are erased, or <see langword="null"/> for every tenant.</summary>
    public int? PortalId { get; }

    /// <summary>
    /// Erases every record belonging to one account, in every tenant it was a member of.
    /// </summary>
    /// <param name="userId">The account being deleted outright.</param>
    /// <returns>The scope.</returns>
    /// <remarks>
    /// For an account row that has been removed. An account retained because it still holds a membership of
    /// another tenant must use <see cref="ForAccountInPortal"/> instead, or the sessions it holds elsewhere
    /// would be erased along with the tenant it left.
    /// </remarks>
    public static RefreshTokenPurgeScope ForAccount(int userId) => new(userId, null);

    /// <summary>Erases the records one account holds in one tenant.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="portalId">The tenant whose membership was removed.</param>
    /// <returns>The scope.</returns>
    public static RefreshTokenPurgeScope ForAccountInPortal(int userId, int portalId) => new(userId, portalId);

    /// <summary>Erases every record belonging to one tenant, whichever account holds it.</summary>
    /// <param name="portalId">The tenant being deleted.</param>
    /// <returns>The scope.</returns>
    /// <remarks>
    /// A tenant's records outlive its members: an account retained because it belongs to another tenant
    /// still holds records scoped to the deleted one, and those name a tenant that no longer exists.
    /// </remarks>
    public static RefreshTokenPurgeScope ForPortal(int portalId) => new(null, portalId);

    /// <summary>Reports whether a stored record falls within this scope.</summary>
    /// <param name="recordUserId">The account the record names.</param>
    /// <param name="recordPortalId">The tenant the record names.</param>
    /// <returns><see langword="true"/> when the record is to be erased.</returns>
    /// <remarks>
    /// Declared here rather than duplicated by each store, so the three scopes cannot mean one thing in the
    /// process-local store and another in the durable one. Both identifiers are compared when both are
    /// supplied, which is what keeps a tenant-scoped purge from reaching an account's other memberships.
    /// </remarks>
    public bool Includes(int recordUserId, int recordPortalId) =>
        (UserId is null || UserId.Value == recordUserId)
        && (PortalId is null || PortalId.Value == recordPortalId);
}

/// <summary>The result of erasing or reclaiming refresh-token records.</summary>
public sealed class RefreshTokenPurgeResult
{
    private RefreshTokenPurgeResult(RefreshTokenOutcome outcome, int removedRecords)
    {
        Outcome = outcome;
        RemovedRecords = removedRecords;
    }

    /// <summary>Gets the outcome the caller must act on.</summary>
    /// <remarks>
    /// <see cref="RefreshTokenOutcome.Succeeded"/> when records were removed,
    /// <see cref="RefreshTokenOutcome.Unknown"/> when the store held none matching, and
    /// <see cref="RefreshTokenOutcome.StoreUnavailable"/> when it could not be asked. Nothing else is
    /// produced: a purge cannot be expired, replayed or over capacity.
    /// </remarks>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>Gets how many records were removed.</summary>
    public int RemovedRecords { get; }

    /// <summary>Gets a value indicating whether the store answered at all.</summary>
    public bool Answered => Outcome != RefreshTokenOutcome.StoreUnavailable;

    /// <summary>Records that rows were removed.</summary>
    /// <param name="removedRecords">How many.</param>
    /// <returns>The result.</returns>
    public static RefreshTokenPurgeResult Removed(int removedRecords) =>
        removedRecords <= 0
            ? NothingHeld()
            : new RefreshTokenPurgeResult(RefreshTokenOutcome.Succeeded, removedRecords);

    /// <summary>Records that the store held nothing matching, which is a completed purge.</summary>
    /// <returns>The result.</returns>
    public static RefreshTokenPurgeResult NothingHeld() =>
        new(RefreshTokenOutcome.Unknown, 0);

    /// <summary>Records that the store could not be asked.</summary>
    /// <returns>The result.</returns>
    public static RefreshTokenPurgeResult Unavailable() =>
        new(RefreshTokenOutcome.StoreUnavailable, 0);
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
