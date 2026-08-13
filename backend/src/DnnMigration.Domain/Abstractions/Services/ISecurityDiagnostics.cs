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
/// fails to compile.
/// </para>
/// <para>
/// WHAT IT DELIBERATELY CANNOT DO, WHICH IS THE POINT OF ITS SHAPE. It accepts no message, no exception, no
/// object and no format arguments. A caller chooses a member of a closed enumeration and may add a tenant,
/// an account and a short stable code.
/// </para>
/// </remarks>
public interface ISecurityDiagnostics
{
    /// <summary>Records one occurrence.</summary>
    /// <param name="occurrence">Which anomaly occurred.</param>
    /// <param name="portalId">
    /// The tenant the occurrence relates to, or <see langword="null"/> when it relates to none.
    /// </param>
    /// <param name="userId">
    /// The account the occurrence relates to, or <see langword="null"/> when it relates to none.
    /// </param>
    /// <param name="reasonCode">
    /// A short, stable, machine-readable code that narrows the occurrence - a failure code from a
    /// <c>Result</c>, or the NAME of an exception type.
    /// </param>
    void Record(
        SecurityDiagnosticEvent occurrence,
        int? portalId = null,
        int? userId = null,
        string? reasonCode = null);
}
