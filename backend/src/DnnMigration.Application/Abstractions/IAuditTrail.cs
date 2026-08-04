namespace DnnMigration.Application.Abstractions;

// MIGRATION: this contract exists because the legacy application HAD a business audit trail and the
// migration had left it unemitted. Two legacy sites are its whole justification, and both are cited on
// the members below: a sign-in outcome written through EventLogController.AddLog with its log type key
// set to loginStatus.ToString (Library/Components/Users/UserController.vb L66-L82), and a portal
// installation written as a HOST_ALERT carrying fourteen named properties
// (Library/Components/Portal/PortalController.vb L1137-L1160).
//
// MIGRATION: it is declared HERE rather than in the layer that writes the entries, and that direction is
// the point. The application services own the events - they are the only code that knows a sign-in was
// refused or a tenant installed - while the logging technology belongs above them: this project declares
// FluentValidation and nothing else, so Microsoft.Extensions.Logging cannot be named in it at all, and
// the attempt fails with CS0234 and CS0246 rather than merely being discouraged. That constraint is what
// makes the audit trail an abstraction instead of a logger call, and the constraint has already been
// tested once in this project's own manifest, where an attempt to import the options package was
// reverted with the ruling that the consumer changes rather than the manifest.
//
// MIGRATION: the signatures are BCL-only by construction, so nothing about the logging pipeline leaks
// into this layer - no level, no event identifier, no message template, no scope, no structured-property
// bag. The implementation chooses all of those, which is why an event's identifier is stable without
// this layer ever naming one.

/// <summary>
/// Records the business events the legacy application audited, without naming the technology that
/// writes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The method set is CLOSED, and that is a security property rather than a stylistic preference.</b>
/// There is deliberately no general-purpose member - no <c>Record(string, object)</c>, no
/// <c>Write(string message)</c>, no property bag and no format string - so the set of things this
/// application can audit is fixed by this file and reviewable in one place. The consequence that matters
/// is that a credential is UNEXPRESSIBLE: every member takes either a named payload whose members are
/// declared below or a small number of scalars, and no member of any of them can carry a password, a
/// hash, a token or a verification code. An open member would make that guarantee unenforceable, because
/// the next caller could pass anything and no reviewer would see it.
/// </para>
/// <para>
/// <b>The members return nothing, deliberately, and do not return a task.</b> Emission performs no I/O
/// of its own: it hands an event to a logging pipeline whose own contract is synchronous, and whose
/// providers are responsible for any buffering or transport. A task-returning member would therefore be
/// asynchronous over nothing, and worse, it would invite a caller to await, time out, retry or fail on an
/// audit write. An audit entry must never be able to fail the operation it describes, so the shape of the
/// contract is what makes that mistake unavailable.
/// </para>
/// <para>
/// <b>An implementation must not throw and must not swallow.</b> Those are not in tension. Logging is
/// contractually non-throwing, so no defensive handler is needed here; adding one would recreate a defect
/// this migration has already annotated, because the legacy audit block was wrapped in an EMPTY
/// <c>Catch ex As Exception</c> (PortalController.vb L1158-L1160) and therefore lost the one record
/// proving a portal had been installed without anyone learning that it had.
/// </para>
/// <para>
/// This contract does not replace request logging and is not replaced by it. Request logging answers
/// "what was called, how did it end and how long did it take"; these events answer "what happened to the
/// business", which no amount of request timing can reconstruct - a refused sign-in and a refused
/// authorisation are the same status code, and a successful portal installation is indistinguishable from
/// any other successful request.
/// </para>
/// </remarks>
public interface IAuditTrail
{
    /// <summary>Records the outcome of an attempt to sign in.</summary>
    /// <param name="audit">The outcome and the facts describing the attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="audit"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// MIGRATION: replaces the audit entry at <c>UserController.vb</c> L66-L82, whose log type key was
    /// <c>loginStatus.ToString</c> (L80) - which is why <see cref="SignInAudit.Outcome"/> carries the
    /// name of the outcome member rather than a number or a message.
    /// </remarks>
    void RecordSignInOutcome(SignInAudit audit);

    /// <summary>Records that a tenant has been installed.</summary>
    /// <param name="audit">The identity of the new tenant and the facts it was installed with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="audit"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// MIGRATION: replaces the <c>HOST_ALERT</c> entry at <c>PortalController.vb</c> L1137-L1160, which
    /// was written with <c>BypassBuffering</c> set to <see langword="true"/> because a failed
    /// installation had to leave a trace. The equivalent guarantee here is that the entry is emitted at
    /// the point the installation is known to have succeeded and is not deferred to the caller.
    /// </remarks>
    void RecordPortalInstallation(PortalInstallationAudit audit);

    /// <summary>
    /// Records that a stored credential could not be re-hashed at the current cost, on a sign-in that
    /// nevertheless succeeded.
    /// </summary>
    /// <param name="userId">The account whose stored representation was left at the superseded cost.</param>
    /// <param name="failure">The failure that was contained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// This member exists because the containment it describes was previously invisible. Leaving a
    /// working credential alone when its cost upgrade fails is the correct behaviour and must not fail
    /// the sign-in - but an upgrade that fails on every attempt, for every account, leaves an
    /// installation permanently at a cost it believes it has moved off, and nothing would have said so.
    /// </para>
    /// <para>
    /// The credential itself is not a parameter and cannot become one. The failure is passed as an
    /// exception rather than as text so that the implementation can attach it as an exception - which is
    /// what keeps a hostile message out of a message template - and the caller does not have to decide
    /// how much of it is safe to render.
    /// </para>
    /// </remarks>
    void RecordCredentialCostUpgradeFailure(int userId, Exception failure);
}

/// <summary>
/// The facts describing one attempt to sign in.
/// </summary>
/// <remarks>
/// <para>
/// Every member here was a property of the legacy entry, and the two the legacy carried that are absent
/// are absent for stated reasons rather than by omission.
/// </para>
/// <para>
/// The caller's network address is absent because this layer does not have it: it was the seventh
/// argument at <c>Login.ascx.vb</c> L164, the sign-in contract carries no address property, and reaching
/// for the request context is forbidden outside the tenant-resolution middleware. The request log records
/// the address, so an entry here is correlated to it by the correlation identifier rather than by
/// repeating the value.
/// </para>
/// <para>
/// The legacy input filter is likewise absent. The legacy passed the submitted account name through it
/// with scripting, angle brackets and markup all stripped, because the name was concatenated into a
/// record that was later rendered. The concern - log forging by way of a hostile value - is answered
/// structurally instead: these members become structured properties of an event, never part of its
/// message template, so a value cannot alter the shape of what is written.
/// </para>
/// </remarks>
public sealed record SignInAudit
{
    /// <summary>
    /// Gets the name of the outcome the attempt produced, which is the stable name of this event.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is <c>loginStatus.ToString</c> from <c>UserController.vb</c> L80, carried across
    /// unchanged. It is the member NAME and never its number, because the numbers are not stable across a
    /// reordering of the enumeration and a trail is read by people.
    /// </remarks>
    public required string Outcome { get; init; }

    /// <summary>Gets the tenant the credential was presented to.</summary>
    /// <remarks>
    /// Not nullable, and no value stands for "unknown". <c>Portals.PortalID</c> is declared
    /// <c>IDENTITY(-1, 1)</c>, so both zero and minus one are real tenants; an attempt whose tenant could
    /// not be determined is a malformed request rather than a sign-in, and is not audited at all.
    /// </remarks>
    public required int PortalId { get; init; }

    /// <summary>
    /// Gets the tenant's name, or an empty string when the attempt named a tenant that does not exist.
    /// </summary>
    public required string PortalName { get; init; }

    /// <summary>Gets the account name that was submitted, exactly as submitted.</summary>
    /// <remarks>
    /// Carried unfiltered and untrimmed on purpose: the value that was actually presented is the value an
    /// investigator needs, and normalising it here would hide the difference between a typo and a probe.
    /// </remarks>
    public required string Username { get; init; }

    /// <summary>
    /// Gets the account the attempt resolved to, or <see langword="null"/> when it resolved to none.
    /// </summary>
    /// <remarks>
    /// MIGRATION - deliberate improvement on the legacy, recorded rather than absorbed. The legacy
    /// ALWAYS passed <c>Null.NullInteger</c> here (UserController.vb L1138-L1141), so its trail could
    /// never say WHICH account an attempt was against - which makes a refusal trail nearly unusable, since
    /// a run of failures against one account and a run against many look identical. The real identifier is
    /// carried where it is known, and <see langword="null"/> means the submitted name matched no account
    /// rather than standing for a legacy sentinel. The identifier is a surrogate key rather than anything
    /// sensitive; no credential, hash or token accompanies it.
    /// </remarks>
    public int? UserId { get; init; }
}

/// <summary>
/// The facts describing one tenant installation.
/// </summary>
/// <remarks>
/// <para>
/// The members reproduce the legacy entry's fourteen properties (<c>PortalController.vb</c>
/// L1142-L1155), less the three that have no counterpart in this migration and plus the new tenant's
/// identifier.
/// </para>
/// <para>
/// The three absences are <c>TemplatePath</c>, <c>ServerPath</c> and <c>ChildPath</c>. All three were
/// physical server paths derived from the excluded global utility module, none is carried by the creation
/// contract, and manufacturing them here would put a guess in an audit record - which is worse than a gap,
/// because a reader cannot tell a guess from a fact.
/// </para>
/// <para>
/// The addition is <see cref="PortalId"/>. The legacy entry named the portal but not its identifier, so an
/// entry could not be joined to the tenant it described. Adding it costs nothing and is what makes the
/// record usable.
/// </para>
/// <para>
/// <b>The administrator's credential is absent, and its absence is inherited rather than newly imposed.</b>
/// The legacy already declined to record it: the password argument was not among the fourteen properties,
/// even though the legacy held it in cleartext at that point in the flow. There is no member here that
/// could carry it.
/// </para>
/// </remarks>
public sealed record PortalInstallationAudit
{
    /// <summary>Gets the identifier the database assigned to the new tenant.</summary>
    public required int PortalId { get; init; }

    /// <summary>Gets the new tenant's name.</summary>
    /// <remarks>
    /// MIGRATION: the legacy carried this behind the label "Install Portal:" (L1142). The label is not
    /// reproduced, because a structured property has a name of its own and does not need one embedded in
    /// its value.
    /// </remarks>
    public required string PortalName { get; init; }

    /// <summary>Gets the host name the new tenant is reached by.</summary>
    public string? PortalAlias { get; init; }

    /// <summary>Gets the new tenant's description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the new tenant's keywords.</summary>
    /// <remarks>
    /// MIGRATION: the legacy label read "Keywords:" with a lower-case w while the argument it read was
    /// spelt <c>KeyWords</c> (L1148). The property name here follows ordinary casing; the discrepancy was
    /// in a display label rather than in data.
    /// </remarks>
    public string? Keywords { get; init; }

    /// <summary>Gets the directory the new tenant's content is rooted at.</summary>
    public string? HomeDirectory { get; init; }

    /// <summary>Gets the template the tenant was installed from, when one was named.</summary>
    public string? TemplateFile { get; init; }

    /// <summary>Gets whether the tenant is reached beneath another tenant's host name.</summary>
    public bool IsChildPortal { get; init; }

    /// <summary>Gets the administrator's given name.</summary>
    public string? AdministratorFirstName { get; init; }

    /// <summary>Gets the administrator's family name.</summary>
    public string? AdministratorLastName { get; init; }

    /// <summary>Gets the administrator's account name.</summary>
    public string? AdministratorUsername { get; init; }

    /// <summary>Gets the administrator's electronic mail address.</summary>
    public string? AdministratorEmail { get; init; }
}
