using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Owns tenant resolution for the lifetime of one inbound call and exposes the resolved <see
/// cref="IPortalContext"/> once it exists.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS SEPARATELY FROM <see cref="IPortalContext"/>. That contract is a finished snapshot: it
/// has eight get-only members, no way to represent an unresolved tenant, and deliberately no population
/// step, because a tenant fact that could change midway through handling a call would defeat the point of
/// snapshotting it.
/// </para>
/// <para>
/// WHY IT LIVES IN THE DOMAIN LAYER. Its whole signature is domain vocabulary - a snapshot, an outcome, and
/// an alias as a plain string. Nothing here names a web framework, a persistence session or a request
/// object, so the layer that eventually drives it can depend on this abstraction rather than on whatever
/// implements it.
/// </para>
/// </remarks>
public interface IPortalContextHolder
{
    /// <summary>The failure code reported when no configured alias matches the supplied host name exactly.</summary>
    public const string NotFoundReasonCode = "PORTAL_ALIAS_NOT_FOUND";

    /// <summary>
    /// The failure code reported when more than one configured alias matches the supplied host name
    /// exactly.
    /// </summary>
    /// <remarks>
    /// Reported rather than resolved. The legacy statement collapsed this case with <c>min(PortalID)</c>
    /// and so served one tenant's content under another tenant's host name; refusing is the only outcome
    /// that cannot silently cross a tenant boundary.
    /// </remarks>
    public const string AmbiguousReasonCode = "PORTAL_ALIAS_AMBIGUOUS";

    /// <summary>
    /// The failure code reported when an alias resolved to a portal whose stored configuration is missing a
    /// fact that the tenant snapshot requires.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NotFoundReasonCode"/> and <see cref="AmbiguousReasonCode"/> because the
    /// cause and the remedy are different: the host name is configured correctly and matched exactly, but
    /// the portal it names is itself incompletely set up - no administrator role designated, or a
    /// designated role that no longer exists, for instance.
    /// </remarks>
    public const string IncompleteReasonCode = "PORTAL_CONTEXT_INCOMPLETE";

    /// <summary>Gets a value indicating whether this call has a resolved tenant.</summary>
    /// <remarks>
    /// <see langword="false"/> both before any attempt and after a failed one, because in neither state is
    /// there a tenant to act on. A component that needs to distinguish those two cases should call <see
    /// cref="EnsureResolvedAsync"/> and read the outcome, which reports why.
    /// </remarks>
    bool IsResolved { get; }

    /// <summary>Gets the resolved tenant snapshot for this call.</summary>
    /// <remarks>
    /// Deliberately not nullable and deliberately throwing rather than returning a placeholder. A consumer
    /// reached through a resolved pipeline always has a tenant, so making every one of them null-check
    /// would spread a concern that belongs at the boundary; and a placeholder tenant is the one thing that
    /// must never exist, since it would be silently wrong rather than loudly absent.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when read before a successful resolution.</exception>
    IPortalContext Current { get; }

    /// <summary>
    /// Resolves the tenant for this call from the supplied host name, if it has not been resolved already,
    /// and reports the outcome.
    /// </summary>
    /// <remarks>
    /// Safe and cheap to call repeatedly. The first call performs the work; every later call returns that
    /// same outcome without repeating it, and the host name supplied to a later call is therefore not
    /// consulted.
    /// </remarks>
    /// <param name="httpAlias">The host name to resolve, exactly as the caller supplied it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// A successful outcome when this call has a complete tenant snapshot, or a failure carrying the code
    /// for the refusal that occurred.
    /// </returns>
    Task<Result> EnsureResolvedAsync(string httpAlias, CancellationToken cancellationToken);
}
