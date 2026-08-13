namespace DnnMigration.Domain.Enums;

/// <summary>
/// The closed set of security-relevant anomalies that a layer with no logger of its own may report through
/// <see cref="Abstractions.Services.ISecurityDiagnostics"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHY A CLOSED ENUMERATION RATHER THAN A MESSAGE. Every member of this set is a condition that must not
/// disappear silently and must not be described in prose supplied at the call site. A free-form message is
/// how payloads, connection strings, file paths and exception text end up in a log; an enumeration member
/// cannot carry any of those, because the caller chooses a member rather than composing a string.
/// </para>
/// <para>
/// Membership of this set is a deliberate judgement, not a catch-all. A condition belongs here when it is
/// (a) security-relevant, (b) NOT worth failing the caller's request over, and (c) invisible to every other
/// control - so that leaving it unrecorded would mean nobody could ever discover it.
/// </para>
/// </remarks>
public enum SecurityDiagnosticEvent
{
    /// <summary>
    /// A stored credential representation was due to be regenerated at the current cost after a successful
    /// verification, and the replacement could not be stored.
    /// </summary>
    CredentialWorkFactorUpgradeFailed = 0,

    /// <summary>
    /// The effective permission keys for a caller could not be resolved, so the caller's snapshot and any
    /// token minted from it carry no permission keys.
    /// </summary>
    /// <remarks>
    /// The keys tell a client which affordances to offer and never stand in for the server-side policy,
    /// which re-evaluates on every request - so an empty set is safe rather than dangerous, and refusing a
    /// sign-in whose credential was already accepted would be the worse outcome.
    /// </remarks>
    EffectivePermissionResolutionFailed = 1,

    /// <summary>
    /// A credential-bookkeeping write found no credential record for an account whose record had just been
    /// read.
    /// </summary>
    /// <remarks>
    /// Ordinarily a race - the record was removed between the read and the write - which is why it does not
    /// fail the request. Recorded because a race that recurs is not a race, and because the write that did
    /// not happen is part of the lock-out control.
    /// </remarks>
    MembershipRecordMissingDuringSignIn = 2,

    /// <summary>
    /// A committed business operation could not be written to the configured audit logging pipeline.
    /// </summary>
    /// <remarks>
    /// The operation remains successful because failing after its transaction committed would report a
    /// false failure to the caller and could prompt a duplicate retry. The loss is nevertheless
    /// security-relevant and otherwise invisible, so the audit sink reports this bounded occurrence and
    /// independently degrades the audit-pipeline health check.
    /// </remarks>
    AuditRecordNotWritten = 3,

    /// <summary>
    /// A legacy credential was verified during the bounded compatibility window, but its immediate BCrypt
    /// replacement could not be stored.
    /// </summary>
    /// <remarks>
    /// The accepted sign-in proceeds because the submitted credential was correct and a transient
    /// persistence fault must not create a new authentication failure. The occurrence is nevertheless
    /// distinct from a work-factor upgrade failure: after the absolute migration deadline, the account
    /// requires administrative reset unless a later successful sign-in completes the replacement.
    /// </remarks>
    LegacyCredentialMigrationFailed = 4,

    /// <summary>
    /// A credential could not be written to the external credential store while an account was being
    /// created, and the account creation was abandoned.
    /// </summary>
    /// <remarks>
    /// The credential store is EXTERNAL to the transaction that creates the account row - the membership
    /// objects are installed by the ASP.NET registration tool and are mapped alongside rather than owned -
    /// so a failure here abandons the whole creation rather than leaving an account that nobody can sign in
    /// to. The caller is told that plainly and asked to retry.
    /// </remarks>
    CredentialStoreWriteFailed = 5,

    /// <summary>
    /// An account's credential changed between a sign-in reading it and that sign-in completing, so the
    /// sign-in was refused rather than completed against a credential that had been retired.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS A SECURITY OCCURRENCE AND NOT A CURIOSITY. The window between reading a credential and
    /// issuing a session is not short - it contains one deliberately expensive comparison - and the change
    /// that lands inside it is very often the remedy for a compromise: an administrator resetting the
    /// credential of an account they believe is in the wrong hands.
    /// </remarks>
    CredentialChangedDuringSignIn = 6,
}
