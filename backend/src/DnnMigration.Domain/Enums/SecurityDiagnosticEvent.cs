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
/// cannot carry any of those, because the caller chooses a member rather than composing a string. What the
/// caller may add alongside it is bounded by the recorder's own signature and is sanitised there.
/// </para>
/// <para>
/// Membership of this set is a deliberate judgement, not a catch-all. A condition belongs here when it is
/// (a) security-relevant, (b) NOT worth failing the caller's request over, and (c) invisible to every other
/// control - so that leaving it unrecorded would mean nobody could ever discover it. A condition that fails
/// the request needs no member: the failure is already visible. A condition the request log already captures
/// needs no member either.
/// </para>
/// </remarks>
public enum SecurityDiagnosticEvent
{
    /// <summary>
    /// A stored credential representation was due to be regenerated at the current cost after a successful
    /// verification, and the replacement could not be stored.
    /// </summary>
    /// <remarks>
    /// Not a failure of the sign-in: the credential was correct and the account keeps a still-valid
    /// representation at the superseded cost, so the only correct response is to proceed and try again next
    /// time. It is recorded because a persistent occurrence means an installation's stored credentials are
    /// silently stuck below the cost the deployment believes it enforces, and nothing else would ever say so.
    /// </remarks>
    CredentialWorkFactorUpgradeFailed = 0,

    /// <summary>
    /// The effective permission keys for a caller could not be resolved, so the caller's snapshot and any
    /// token minted from it carry no permission keys.
    /// </summary>
    /// <remarks>
    /// The keys tell a client which affordances to offer and never stand in for the server-side policy, which
    /// re-evaluates on every request - so an empty set is safe rather than dangerous, and refusing a sign-in
    /// whose credential was already accepted would be the worse outcome. What is NOT safe is that an empty
    /// set is indistinguishable from a caller who genuinely holds nothing, which is precisely why the
    /// distinction has to be recorded somewhere.
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
}
