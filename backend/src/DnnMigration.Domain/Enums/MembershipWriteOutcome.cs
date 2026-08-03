namespace DnnMigration.Domain.Enums;

/// <summary>
/// What happened when a write was attempted against the externally installed membership store.
/// </summary>
/// <remarks>
/// <para>
/// WHY A BOOLEAN WAS NOT ENOUGH, WHICH IS THE WHOLE REASON THIS TYPE EXISTS. The credential-bookkeeping
/// members that produce this value each used to return <see langword="true"/> or <see langword="false"/>,
/// and the false branch stood for three unrelated situations at once: the write ran and the account is not
/// locked, the account holds no credential record, and THE STORE COULD NOT BE REACHED AT ALL. The last of
/// those is a failed security control - it means the failed-attempt counter that produces a lock-out was
/// never incremented - and it was indistinguishable from the first, which is the ordinary outcome of a
/// single mistyped password. A caller acting on that boolean therefore could not tell "your credential was
/// wrong, and we counted it" from "your credential was wrong, and we counted nothing", so an attacker
/// against an unreachable store could guess without limit and nothing would ever notice.
/// </para>
/// <para>
/// THE MEMBERS ARE ORDERED FROM WORST TO BEST AND THE ZERO MEMBER IS THE WORST ON PURPOSE. A default value
/// that means "the store could not be reached" fails closed: a caller that forgets to assign one, or a
/// double that is left unconfigured, reports the outcome that forces the loudest handling rather than the
/// one that quietly proceeds.
/// </para>
/// <para>
/// MIGRATION: the store these outcomes describe is the <c>aspnet_*</c> membership schema, which the
/// eighty-eight upgrade scripts only ever ALTER and never CREATE - <c>04.00.00.SqlDataProvider</c> lines 31
/// and 119 graft DotNetNuke's own failed-attempt and lock-out bookkeeping onto procedures it did not write.
/// It is installed externally, so it is mapped alongside and never owned (Rule T4), and its unavailability
/// is a genuine operational state this codebase has to be able to name rather than an impossibility.
/// </para>
/// </remarks>
public enum MembershipWriteOutcome
{
    /// <summary>
    /// The store could not be reached, so nothing was written and no security bookkeeping ran.
    /// </summary>
    /// <remarks>
    /// This is a SERVER FAULT rather than an outcome of the operation the caller asked for, and callers
    /// must treat it as one. It must never be reported to an unauthenticated caller as a credential
    /// outcome: doing so would tell that caller its credential was wrong when nothing was ever checked
    /// against, and - far worse - would let the attempt pass unrecorded.
    /// </remarks>
    StoreUnavailable = 0,

    /// <summary>
    /// The store was reachable but holds no credential record for the account, so nothing was written.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="StoreUnavailable"/> because the control ran and found nothing, rather than
    /// failing to run. On an authentication path this normally means the record was removed between the
    /// read that found it and the write that followed, which is a race rather than a defect and does not
    /// warrant failing the request; it is worth recording, because a persistent occurrence is not a race.
    /// </remarks>
    NoRecord = 1,

    /// <summary>
    /// The write was recorded and the account is not locked.
    /// </summary>
    Recorded = 2,

    /// <summary>
    /// The write was recorded and the account is locked - either this attempt reached the threshold, or it
    /// was already locked when the attempt arrived.
    /// </summary>
    /// <remarks>
    /// The two causes are deliberately not distinguished. The threshold is a count the caller must not be
    /// able to probe, and a caller that could tell "this attempt locked me" from "I was already locked"
    /// could measure exactly how many guesses remain.
    /// </remarks>
    RecordedAndLocked = 3,
}
