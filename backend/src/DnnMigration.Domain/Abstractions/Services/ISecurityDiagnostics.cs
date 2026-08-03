using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Records a security-relevant anomaly that must not disappear silently, from a layer that has no logger of
/// its own.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS ABSTRACTION EXISTS. The Application layer's package surface is fixed at FluentValidation and
/// nothing else, so <c>ILogger&lt;T&gt;</c> is not resolvable there - the logging assemblies ship in the
/// ASP.NET Core shared framework, which a class library in that layer must never reference, and the attempt
/// fails to compile. That constraint is correct and is not worked around here: what it forbids is a
/// dependency on a logging FRAMEWORK, not the ability to report a fact. This contract is the boundary
/// abstraction that lets a service state what happened while leaving entirely to the layer that owns logging
/// how, where and at what level it is written.
/// </para>
/// <para>
/// WHAT IT DELIBERATELY CANNOT DO, WHICH IS THE POINT OF ITS SHAPE. It accepts no message, no exception, no
/// object and no format arguments. A caller chooses a member of a closed enumeration and may add a tenant, an
/// account and a short stable code. There is consequently NO PARAMETER through which a password, a hash, a
/// connection string, a request body, a file path or an exception's text could travel, so those cannot be
/// logged through this route even by mistake - a guarantee that a signature enforces and a convention does
/// not. The implementation additionally sanitises the code, so a caller that passes something message-shaped
/// has it discarded rather than written.
/// </para>
/// <para>
/// IT IS NOT AN AUDIT TRAIL AND MUST NOT BE USED AS ONE. An audit trail records what callers legitimately
/// did; this records anomalies a caller cannot see and did not cause. Nothing here is on a request's happy
/// path, and no caller's behaviour changes because of a call to it.
/// </para>
/// <para>
/// IMPLEMENTATIONS MUST NOT THROW. Every call site is code that has already decided the anomaly does not
/// warrant failing the request. A recorder that threw would convert precisely those situations into the
/// failures the call sites concluded they were not, which is the one outcome worse than not recording them.
/// </para>
/// </remarks>
public interface ISecurityDiagnostics
{
    /// <summary>
    /// Records one occurrence.
    /// </summary>
    /// <param name="occurrence">Which anomaly occurred. The set is closed by design.</param>
    /// <param name="portalId">
    /// The tenant the occurrence relates to, or <see langword="null"/> when it relates to none. Both
    /// <c>-1</c> and <c>0</c> are real tenant identifiers in this schema, so neither may be used to mean
    /// "absent" (Rule T7).
    /// </param>
    /// <param name="userId">
    /// The account the occurrence relates to, or <see langword="null"/> when it relates to none.
    /// </param>
    /// <param name="reasonCode">
    /// A short, stable, machine-readable code that narrows the occurrence - a failure code from a
    /// <c>Result</c>, or the NAME of an exception type. It must never be an exception's message, a rendered
    /// sentence or anything derived from caller input; implementations discard values that do not have the
    /// shape of a code, so passing one is ineffective rather than dangerous.
    /// </param>
    void Record(
        SecurityDiagnosticEvent occurrence,
        int? portalId = null,
        int? userId = null,
        string? reasonCode = null);
}
