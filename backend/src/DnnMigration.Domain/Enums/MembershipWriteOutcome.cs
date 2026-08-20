namespace DnnMigration.Domain.Enums;

/// <summary>What happened when a write was attempted against the externally installed membership store.</summary>
/// <remarks>
/// The store these outcomes describe is the <c>aspnet_*</c> membership schema, which the eighty-eight
/// upgrade scripts only ever ALTER and never CREATE - <c>04.00.00.SqlDataProvider</c> lines 31 and 119
/// graft DotNetNuke's own failed-attempt and lock-out bookkeeping onto procedures it did not write.
/// </remarks>
public enum MembershipWriteOutcome
{
    /// <summary>The store could not be reached, so nothing was written and no security bookkeeping ran.</summary>
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

    /// <summary>The write was recorded and the account is not locked.</summary>
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
