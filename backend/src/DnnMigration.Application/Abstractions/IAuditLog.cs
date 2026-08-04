namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Records the administrative facts that the legacy application wrote to its event log: what happened, to
/// which tenant, and at whose hand.
/// </summary>
/// <remarks>
/// <para>
/// Why an abstraction rather than a logger. This contract is declared in the Application layer because that
/// is the layer that KNOWS an audited thing happened - a tenant was installed, a tenant was removed, a sign-in
/// was refused. The legacy application knew the same facts in the same place: the audit calls sat in the admin
/// screens' code-behinds, which is precisely the code this layer replaces. Recording them from the request
/// pipeline instead would be recording the wrong thing, because the pipeline sees an HTTP request and a status
/// code, not which of several failure reasons produced the status.
/// </para>
/// <para>
/// MIGRATION: an earlier revision of the portal service recorded, at length, that this layer "cannot emit it",
/// reasoning that <c>ILogger&lt;T&gt;</c> is unnameable here because AAP 0.6.1 gives the Application project
/// only FluentValidation and Serilog belongs to the Api layer. The premise is correct and is not disputed -
/// naming a logging type in this project does fail to compile. The conclusion did not follow. An interface
/// whose members take a name and a set of name-value pairs needs no logging package, because it names no
/// logging type; the package is needed only by the class that IMPLEMENTS it, and that class lives in
/// Infrastructure, which already carries what it needs. So the frozen package inventory is untouched, the
/// dependency direction still points inward, and the fact is recorded by the layer that knows it. This is the
/// inward-safe shape the review asked for.
/// </para>
/// <para>
/// What may be recorded, and what may not. An audit fact identifies the actor and the subject; it never
/// carries a credential, a token, a password hash, or a value a caller submitted as a secret. The legacy code
/// already drew this line and it is preserved rather than newly imposed: the fourteen properties the legacy
/// installation entry attached (<c>PortalController.vb:L1142-L1155</c>) did NOT include the administrator
/// password argument that the same method had received. Nothing here widens that.
/// </para>
/// <para>
/// Failure policy. An implementer must not throw: an audit record is a by-product of an operation that has
/// already succeeded or already failed, and letting the record's own failure change that outcome would make
/// the audit trail able to break the thing it observes. This is a deliberate divergence in FORM from the
/// legacy code, which wrapped its audit call in an empty <c>Catch</c> (<c>PortalController.vb:L1158-L1160</c>)
/// and so could silently lose the one record proving a tenant had been installed. The behaviour is the same -
/// the operation is unaffected - but the failure is reported through the logging pipeline rather than
/// discarded.
/// </para>
/// <para>
/// Registration. Implemented by <c>Infrastructure/Services/AuditLog.cs</c> and registered by
/// <c>AddInfrastructure()</c>. A singleton lifetime is correct: the implementation holds no request state, and
/// the correlation identifier that ties a record to its request is attached by the logging scope the request
/// pipeline establishes rather than by this contract.
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>
    /// Records one audited fact.
    /// </summary>
    /// <param name="eventName">
    /// The stable event name. Use a member of <see cref="AuditEventNames"/> rather than a literal, so that a
    /// rename cannot silently split one audit trail into two.
    /// </param>
    /// <param name="properties">
    /// The facts to record, as name-value pairs. A null value records that the fact was absent, which is a
    /// different thing from the fact not being recorded at all. Must contain no credential and no secret.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the record. An implementer treats an abandoned record as a lost record rather than as a
    /// failure of the operation being audited.
    /// </param>
    /// <returns>A task that completes when the record has been submitted.</returns>
    Task RecordAsync(
        string eventName,
        IReadOnlyDictionary<string, string?> properties,
        CancellationToken cancellationToken = default);
}
