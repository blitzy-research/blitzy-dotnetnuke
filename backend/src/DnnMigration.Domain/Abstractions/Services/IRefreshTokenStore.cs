using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: This contract is net-new. The legacy tree had no refresh credential to store: the
// MIGRATION: forms ticket established at Library/Components/Users/UserController.vb:L1024-L1055 was
// MIGRATION: a single bearer value with no rotation and no server-side record, and the sign-off at
// MIGRATION: Library/Components/Security/PortalSecurity.vb:L77-L95 reached only the browser's copy
// MIGRATION: of it. There is consequently nothing here to translate line for line; the interface
// MIGRATION: exists to give the seam a name and to keep the store's implementation invisible.

/// <summary>
/// Stores refresh-token material so that a session can be renewed without re-presenting a password,
/// and so that renewal is single-use, revocable and bounded in total duration.
/// </summary>
/// <remarks>
/// <para>
/// Declared here, in the Domain layer, because the Application layer's token service depends on it
/// while nothing about it is specific to how it is stored. The implementation lives in the
/// Infrastructure layer and is deliberately not visible: it is obtained from the container, never
/// constructed, so a caller cannot come to depend on the storage medium, the locking strategy or the
/// retention policy of whichever implementation is registered.
/// </para>
/// <para>
/// FOUR GUARANTEES AN IMPLEMENTATION MUST HONOUR, because the security of session renewal rests on
/// them rather than on any caller remembering to ask:
/// </para>
/// <para>
/// One — single use. Redeeming a token consumes it. A second presentation of the same material is a
/// replay, is refused, and additionally revokes every family the owning user holds, because a replay
/// proves a token was copied without revealing which session the copy came from.
/// </para>
/// <para>
/// Two — two independent bounds. Each token carries its own sliding expiry, and the family it belongs
/// to carries an absolute ceiling fixed when the family was created. Renewal restarts the sliding
/// window and must never move the ceiling, so a continuously renewed session still ends.
/// </para>
/// <para>
/// Three — revocability, per token family and per user. Signing off ends one session; a detected
/// replay or an administrative credential reset ends all of them.
/// </para>
/// <para>
/// Four — the raw token is returned exactly once, at the moment it is created, and is not recoverable
/// afterwards. An implementation is expected to retain a one-way digest rather than the token, so
/// that a dump of its state cannot be turned back into a usable credential.
/// </para>
/// <para>
/// SYNCHRONOUS BY DECISION, not by omission. Every member below returns its result directly rather
/// than as a task. The rule that I/O-bound work must be asynchronous is not in tension with this: the
/// registered implementation performs no I/O, so presenting these operations as awaitable would
/// misrepresent their cost and invite callers to await something that never yields. The asynchronous
/// boundary is the Application layer's token service, which is where a durable implementation would
/// be adapted — and adopting one would mean revisiting this contract, which is inexpensive while it
/// has a single implementation and is stated here so the choice is not mistaken for an oversight.
/// </para>
/// <para>
/// Implementations must be safe to call concurrently from any thread, because a single instance
/// serves every request in the process.
/// </para>
/// </remarks>
public interface IRefreshTokenStore
{
    /// <summary>
    /// Issues the first token of a new family and records the supplied snapshot against it.
    /// </summary>
    /// <param name="subject">The non-secret facts to re-mint on each later renewal.</param>
    /// <returns>
    /// A successful result carrying the raw token and its expiry, or a refusal whose outcome explains
    /// why none was issued.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="subject"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This is the only operation that may create a family, and therefore the only one entitled to
    /// refuse for a reason unrelated to the caller — a store at capacity declines a new session, which
    /// is recoverable by signing in again. Such a refusal must be translated as a condition of the
    /// service and never as a credential rejection, which would be both untrue and unactionable.
    /// </remarks>
    RefreshTokenIssueResult Issue(RefreshTokenSubject subject);

    /// <summary>
    /// Reads the state of presented material without changing anything.
    /// </summary>
    /// <param name="refreshToken">The material a caller presented. May be malformed or absent.</param>
    /// <returns>The snapshot and expiry when the material is redeemable, or a refusal reason.</returns>
    /// <remarks>
    /// A successful reading is an observation, not a permission: the material may be redeemed or
    /// revoked by another request in between, which is why <see cref="Rotate"/> repeats every check
    /// for itself rather than trusting a prior reading. Provided for diagnosis and for callers that
    /// must report on a session without renewing it — never as a substitute for redemption.
    /// </remarks>
    RefreshTokenInspection Inspect(string refreshToken);

    /// <summary>
    /// Redeems presented material and issues its single replacement within the same family.
    /// </summary>
    /// <param name="refreshToken">The material being redeemed.</param>
    /// <param name="subject">The snapshot to record against the replacement.</param>
    /// <returns>
    /// A successful result carrying the replacement and its expiry, or a refusal whose outcome
    /// explains why no replacement was issued.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="subject"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The snapshot is supplied again rather than carried over, so that a renewal reflects the
    /// caller's roles and permissions as they stand now rather than as they stood when the session
    /// began. Redemption must not refuse on capacity: declining to renew would destroy a live session,
    /// and renewal cannot create a family, so it cannot be used to grow the store without bound.
    /// </remarks>
    RefreshTokenRotationResult Rotate(string refreshToken, RefreshTokenSubject subject);

    /// <summary>
    /// Revokes the whole family the presented material belongs to, ending that one session.
    /// </summary>
    /// <param name="refreshToken">Any generation of the family to revoke.</param>
    /// <returns>
    /// <c>Succeeded</c> when this call revoked something, <c>AlreadyRevoked</c> when the family was
    /// already fully revoked, or <c>Unknown</c> when the material matches nothing.
    /// </returns>
    /// <remarks>
    /// The family rather than the single generation, because the caller signing off holds only the
    /// newest generation while the older ones remain a replay surface. Reporting a repeated sign-off
    /// distinctly, rather than as a fresh success, is what makes the operation idempotent without
    /// making it silent.
    /// </remarks>
    RefreshTokenOutcome Revoke(string refreshToken);

    /// <summary>
    /// Revokes every family one user holds, ending all of that user's sessions at once.
    /// </summary>
    /// <param name="userId">Numeric key of the user whose sessions are to end.</param>
    /// <returns>
    /// <c>Succeeded</c> when this call revoked something, <c>AlreadyRevoked</c> when every family was
    /// already revoked, or <c>Unknown</c> when the user holds no family.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two callers require this and neither is a convenience. A detected replay must end every session
    /// the user holds, for the reason given on <see cref="Rotate"/>. An administrative credential reset
    /// must do the same, or the reset would leave an existing session renewable by whoever prompted it.
    /// </para>
    /// <para>
    /// The key is used exactly as supplied. Both zero and negative one are legitimate account keys in
    /// this schema, so neither may be read as meaning "no user"; a key belonging to nobody simply
    /// matches no family and reports <c>Unknown</c>.
    /// </para>
    /// </remarks>
    RefreshTokenOutcome RevokeAllForUser(int userId);
}

/// <summary>
/// The immutable, non-secret facts recorded against a refresh token, so that redeeming it can
/// re-mint an access token describing the same caller.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a sealed class and not a record. A record synthesises a string representation
/// that prints every member, which would put a caller's identity — and, on the sibling result
/// types, a live token — into any string ever built from one of these objects. A plain class
/// inherits the representation that prints only its type name, so the safe behaviour is the
/// default one and no override is needed to obtain it. That property is worth more here than
/// value equality, which nothing on this contract needs.
/// </para>
/// <para>
/// Constructing an instance copies the two collections, so a caller that keeps and later mutates
/// the list it passed cannot alter a snapshot already recorded against an issued token. The
/// copies are exposed only through a read-only view, so the arrays behind them cannot be reached
/// and mutated either. Immutability is what makes one instance safe to share between the several
/// generations of a family and safe to read on any thread.
/// </para>
/// <para>
/// Values are recorded exactly as supplied. This type validates nothing and normalises nothing:
/// the caller owns what a claim should contain, and a store that silently rewrote its input would
/// make the token it protects disagree with the state it was minted from. A null collection is
/// the single accommodation, read as "none supplied" and recorded as empty.
/// </para>
/// <para>
/// No identifier is range-checked, and none may be. In this schema both -1 and 0 are legitimate
/// identifiers — the portal table seeds its identity at -1 and the role table at 0 — so a guard
/// rejecting them would refuse real callers, and treating either as "absent" would misread real
/// data. Absence is carried by nullability alone.
/// </para>
/// <para>
/// Nothing secret belongs on this type. There is no token, no access token, no credential and no
/// signing material among the members below, and none may be added: an instance is retained for
/// the whole life of a token family, so anything sensitive placed here would be retained with it.
/// </para>
/// </remarks>
public sealed class RefreshTokenSubject
{
    /// <summary>
    /// Records the facts to be re-minted when this token, or a later generation of its family, is
    /// redeemed.
    /// </summary>
    /// <param name="userId">Identifier of the caller the token was issued to.</param>
    /// <param name="portalId">
    /// Identifier of the portal scoping the caller, or <see langword="null"/> when no portal
    /// scope applies.
    /// </param>
    /// <param name="userName">
    /// The caller's login name, or <see langword="null"/> when it is not known. An empty string
    /// is recorded as given rather than converted, so the two remain distinguishable.
    /// </param>
    /// <param name="isSuperUser">
    /// Whether the caller carries the host-level flag. Recorded so the replacement token
    /// describes the caller consistently; it is not, and must never become, an authorisation
    /// shortcut.
    /// </param>
    /// <param name="roles">
    /// The caller's role names. Copied on entry. <see langword="null"/> is recorded as empty.
    /// </param>
    /// <param name="permissionKeys">
    /// The caller's permission keys. Copied on entry. <see langword="null"/> is recorded as
    /// empty.
    /// </param>
    public RefreshTokenSubject(
        int userId,
        int? portalId,
        string? userName,
        bool isSuperUser,
        IEnumerable<string>? roles,
        IEnumerable<string>? permissionKeys)
    {
        UserId = userId;
        PortalId = portalId;
        UserName = userName;
        IsSuperUser = isSuperUser;

        // Materialise first, then wrap. Enumerating into a private array is what severs the
        // link to the caller's collection; the read-only wrapper is what stops the array being
        // reached through the property and mutated afterwards. Either step alone would leave a
        // way in.
        Roles = Array.AsReadOnly(roles?.ToArray() ?? []);
        PermissionKeys = Array.AsReadOnly(permissionKeys?.ToArray() ?? []);
    }

    /// <summary>Gets the identifier of the caller the token was issued to.</summary>
    public int UserId { get; }

    /// <summary>
    /// Gets the identifier of the portal scoping the caller, or <see langword="null"/> when no
    /// portal scope applies.
    /// </summary>
    public int? PortalId { get; }

    /// <summary>
    /// Gets the caller's login name, or <see langword="null"/> when it is not known.
    /// </summary>
    public string? UserName { get; }

    /// <summary>Gets a value indicating whether the caller carries the host-level flag.</summary>
    public bool IsSuperUser { get; }

    /// <summary>Gets the caller's role names. Never <see langword="null"/>.</summary>
    public IReadOnlyList<string> Roles { get; }

    /// <summary>Gets the caller's permission keys. Never <see langword="null"/>.</summary>
    public IReadOnlyList<string> PermissionKeys { get; }
}

/// <summary>
/// What issuing a refresh token produced: the token itself, its absolute expiry, and the snapshot
/// recorded against it.
/// </summary>
/// <remarks>
/// <para>
/// A sealed class rather than a record, for the reason set down on the subject type: one member
/// below holds a live credential, and a synthesised string representation would print it.
/// </para>
/// <para>
/// Issuing carries an outcome, and it did not always. It acquired one when the store gained a
/// capacity bound: a store that refuses to grow past a limit can decline to issue for a reason that
/// has nothing to do with the caller's credentials, and a type that could only describe success
/// would have left that condition to be signalled by an exception or, worse, by a null. The shape
/// now matches the redemption result exactly, so both are read the same way — test the outcome, then
/// read the token.
/// </para>
/// </remarks>
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

    /// <summary>Gets the result of the issue attempt.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>
    /// Gets the raw refresh token, or <see langword="null"/> unless <see cref="Outcome"/> is
    /// <see cref="RefreshTokenOutcome.Succeeded"/>. This is the only time it is available: the store
    /// kept a one-way digest of it and cannot reproduce it. Return it to the caller who
    /// authenticated and retain it nowhere else.
    /// </summary>
    public string? RefreshToken { get; }

    /// <summary>
    /// Gets the absolute instant at and after which the token is expired, in Coordinated Universal
    /// Time, or <see langword="null"/> unless <see cref="Outcome"/> is
    /// <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Gets the snapshot the store recorded, echoed back so that what is minted into a token and
    /// what will be re-minted on redemption are known to be the same facts, or
    /// <see langword="null"/> unless <see cref="Outcome"/> is
    /// <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>
    /// Reports a completed issue, carrying the token it produced.
    /// </summary>
    /// <param name="refreshToken">The raw token, to be handed onward exactly once.</param>
    /// <param name="expiresAtUtc">The absolute expiry instant, in Coordinated Universal
    /// Time.</param>
    /// <param name="subject">The snapshot recorded against the token.</param>
    /// <returns>A successful issue.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="refreshToken"/> or <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    public static RefreshTokenIssueResult Succeeded(
        string refreshToken,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentNullException.ThrowIfNull(subject);

        return new RefreshTokenIssueResult(
            RefreshTokenOutcome.Succeeded,
            refreshToken,
            expiresAtUtc,
            subject);
    }

    /// <summary>
    /// Reports a refused issue, carrying the reason and no token.
    /// </summary>
    /// <param name="outcome">Why no token was issued.</param>
    /// <returns>An unsuccessful issue.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </exception>
    /// <remarks>
    /// The guard makes it impossible to construct a result that claims success while carrying no
    /// token, so a caller reading <see cref="RefreshToken"/> after testing <see cref="Outcome"/> can
    /// rely on finding one.
    /// </remarks>
    public static RefreshTokenIssueResult Failed(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A refused issue cannot carry a successful outcome.");
        }

        return new RefreshTokenIssueResult(
            outcome,
            refreshToken: null,
            expiresAtUtc: null,
            subject: null);
    }
}

/// <summary>
/// What reading the state of presented material found, without changing anything.
/// </summary>
/// <remarks>
/// A successful reading is an observation, not a permission. The material may be redeemed or
/// revoked by another request between this reading and any action taken on it, which is why
/// redemption repeats every check under its own lock and decides for itself.
/// </remarks>
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

    /// <summary>Gets what the reading found.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>
    /// Gets the snapshot recorded against the material, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>
    /// Gets the material's absolute expiry, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Gets the account the material was issued to, or <see langword="null"/> when the material was not
    /// recognised at all.
    /// </summary>
    /// <remarks>
    /// PRESENT EVEN WHEN THE READING REFUSES, AND THAT IS THE POINT. A caller that finds material already
    /// redeemed is looking at a replay, and the only proportionate answer is to revoke every credential the
    /// account holds - which it cannot ask for without knowing whose account it is. This is an account key
    /// and nothing more: no sign-in name, no roles and no permission keys accompany it, so answering a
    /// replay never requires the store to retain the entitlement snapshot it deliberately discards on
    /// redemption.
    /// </remarks>
    public int? OwnerUserId { get; }

    /// <summary>
    /// Reports material that is currently redeemable, carrying its snapshot and expiry.
    /// </summary>
    /// <param name="subject">The snapshot recorded against the material.</param>
    /// <param name="expiresAtUtc">The material's absolute expiry.</param>
    /// <returns>A successful reading.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    public static RefreshTokenInspection Succeeded(
        RefreshTokenSubject subject,
        DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return new RefreshTokenInspection(
            RefreshTokenOutcome.Succeeded,
            subject,
            expiresAtUtc,
            subject.UserId);
    }

    /// <summary>
    /// Reports material that is not redeemable, carrying the reason and, when the material was recognised,
    /// the account it was issued to.
    /// </summary>
    /// <param name="outcome">Why the material is not redeemable.</param>
    /// <param name="ownerUserId">
    /// The account the material was issued to, or <see langword="null"/> when the material was not
    /// recognised. See <see cref="OwnerUserId"/> for why a refusal carries it.
    /// </param>
    /// <returns>An unsuccessful reading.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </exception>
    /// <remarks>
    /// The guard is what keeps the invariant honest rather than merely documented: a successful
    /// outcome can only be produced by the factory that also demands a snapshot, so no reading
    /// can claim success while carrying nothing to act on.
    /// </remarks>
    public static RefreshTokenInspection Failed(RefreshTokenOutcome outcome, int? ownerUserId = null)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "An unsuccessful reading cannot carry a successful outcome.");
        }

        return new RefreshTokenInspection(
            outcome,
            subject: null,
            expiresAtUtc: null,
            ownerUserId);
    }
}

/// <summary>
/// What redeeming presented material produced: on success the single replacement token, otherwise
/// the reason the redemption was refused.
/// </summary>
/// <remarks>
/// A sealed class rather than a record, for the reason set down on the subject type: on success
/// one member holds a live credential. On failure that member is <see langword="null"/> and no
/// token was created — a refused redemption never leaves a usable token behind, and a replay
/// revokes the whole family before reporting.
/// </remarks>
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

    /// <summary>Gets the result of the redemption.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>
    /// Gets the replacement token, or <see langword="null"/> unless <see cref="Outcome"/> is
    /// <see cref="RefreshTokenOutcome.Succeeded"/>. As with issuing, this is the only time the
    /// value is available.
    /// </summary>
    public string? RefreshToken { get; }

    /// <summary>
    /// Gets the replacement's absolute expiry, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Gets the snapshot recorded against the replacement, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>
    /// Reports a completed redemption, carrying the single replacement it produced.
    /// </summary>
    /// <param name="refreshToken">The replacement token, to be handed onward exactly
    /// once.</param>
    /// <param name="expiresAtUtc">The replacement's absolute expiry.</param>
    /// <param name="subject">The snapshot recorded against the replacement.</param>
    /// <returns>A successful redemption.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="refreshToken"/> or <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    public static RefreshTokenRotationResult Succeeded(
        string refreshToken,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentNullException.ThrowIfNull(subject);

        return new RefreshTokenRotationResult(
            RefreshTokenOutcome.Succeeded,
            refreshToken,
            expiresAtUtc,
            subject);
    }

    /// <summary>
    /// Reports a refused redemption, carrying the reason and no token.
    /// </summary>
    /// <param name="outcome">Why the redemption was refused.</param>
    /// <returns>An unsuccessful redemption.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </exception>
    /// <remarks>
    /// The guard makes it impossible to construct a result that claims success while carrying no
    /// replacement, so a caller reading <see cref="RefreshToken"/> after testing
    /// <see cref="Outcome"/> can rely on finding one.
    /// </remarks>
    public static RefreshTokenRotationResult Failed(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A refused redemption cannot carry a successful outcome.");
        }

        return new RefreshTokenRotationResult(
            outcome,
            refreshToken: null,
            expiresAtUtc: null,
            subject: null);
    }
}
