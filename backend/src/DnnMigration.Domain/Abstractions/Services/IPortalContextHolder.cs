using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Owns tenant resolution for the lifetime of one inbound call and exposes the resolved
/// <see cref="IPortalContext"/> once it exists.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS SEPARATELY FROM <see cref="IPortalContext"/>. That contract is a finished snapshot:
/// it has eight get-only members, no way to represent an unresolved tenant, and deliberately no
/// population step, because a tenant fact that could change midway through handling a call would defeat
/// the point of snapshotting it. Something still has to perform the resolution and has to be able to say
/// "not yet" and "it failed, and here is why", and neither statement is expressible on a finished
/// snapshot. This is that something. The division keeps the snapshot immutable and total while giving the
/// boundary somewhere to put the transitional states.
/// </para>
/// <para>
/// WHY IT LIVES IN THE DOMAIN LAYER. Its whole signature is domain vocabulary - a snapshot, an outcome,
/// and an alias as a plain string. Nothing here names a web framework, a persistence session or a
/// request object, so the layer that eventually drives it can depend on this abstraction rather than on
/// whatever implements it. That is what allows the resolution to be performed by a middleware and
/// consulted by an authorisation handler without either of them, or this contract, acquiring a
/// dependency on the other.
/// </para>
/// <para>
/// RESOLUTION IS IDEMPOTENT, AND THAT PROPERTY IS LOAD-BEARING. Two different places need a resolved
/// tenant and they cannot be collapsed into one: the pipeline resolves it as a matter of course for
/// every call, while portal-scoped authorisation cannot afford to assume the pipeline already has,
/// because a policy that assumes a resolved tenant and finds none must deny rather than fault.
/// <see cref="EnsureResolvedAsync"/> is therefore written so that calling it a second time is free and
/// yields the identical outcome - including the identical failure - rather than resolving again. Both
/// callers can then simply ensure, and neither has to reason about whether the other ran first.
/// </para>
/// <para>
/// RESOLUTION HAPPENS AT MOST ONCE PER CALL, in the strong sense: the first attempt's outcome is the
/// outcome for the whole call, success or failure alike. A failure is remembered as deliberately as a
/// success. Were failures retried, a later consultation could reach a different answer than an earlier
/// one - two authorisation decisions in one call disagreeing about which tenant they are in - and an
/// unresolvable alias could be re-queried once per consulting component.
/// </para>
/// <para>
/// FAILING TO RESOLVE IS A REFUSAL, NEVER A DEGRADED SUCCESS. There is no partial tenant and no default
/// tenant. A caller that receives a failed outcome must decline the call; continuing would leave
/// portal-scoped authorisation with nothing to scope against, which is exactly how a cross-tenant
/// escalation arises.
/// </para>
/// </remarks>
public interface IPortalContextHolder
{
    /// <summary>
    /// The failure code reported when no configured alias matches the supplied host name exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared here rather than on the alias repository because it names an outcome of
    /// <see cref="EnsureResolvedAsync"/> rather than of a persistence read. The repository answers "which
    /// aliases carry this host name" and returns however many there are; deciding that nought matches is a
    /// refusal - as opposed to an empty result a caller might legitimately tolerate - is this contract's
    /// judgement, so the vocabulary for it belongs to this contract too.
    /// </para>
    /// <para>
    /// Distinct from <see cref="AmbiguousReasonCode"/> so that a caller can tell "this host is not
    /// configured" from "this host is configured more than once". Both are refusals; only their diagnostics
    /// differ, and neither is disclosed to the caller verbatim - the host name is attacker-supplied text
    /// and belongs in structured internal diagnostics rather than in a response body.
    /// </para>
    /// </remarks>
    public const string NotFoundReasonCode = "PORTAL_ALIAS_NOT_FOUND";

    /// <summary>
    /// The failure code reported when more than one configured alias matches the supplied host name
    /// exactly.
    /// </summary>
    /// <remarks>
    /// Reported rather than resolved. The legacy statement collapsed this case with <c>min(PortalID)</c>
    /// and so served one tenant's content under another tenant's host name; refusing is the only outcome
    /// that cannot silently cross a tenant boundary. An installation that provokes this result has a data
    /// defect an operator must correct - the schema's unique constraint on the host-name column should make
    /// it unreachable - and the ambiguity is recorded in the diagnostics so they can.
    /// </remarks>
    public const string AmbiguousReasonCode = "PORTAL_ALIAS_AMBIGUOUS";

    /// <summary>
    /// The failure code reported when an alias resolved to a portal whose stored configuration is
    /// missing a fact that the tenant snapshot requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="NotFoundReasonCode"/> and <see cref="AmbiguousReasonCode"/> because the
    /// cause and the remedy are different: the host name is configured correctly and matched exactly, but
    /// the portal it names is itself incompletely set up - no administrator role designated, or a
    /// designated role that no longer exists, for instance.
    /// </para>
    /// <para>
    /// This is a refusal rather than a snapshot with holes in it, and the reason is the tenant contract's
    /// own: negative one and zero are both legitimate portal, role and account keys in this schema, so
    /// there is no integer available to stand for "absent" and inventing one would make a missing
    /// administrator indistinguishable from the administrator whose key is zero. Refusing the call is the
    /// only outcome that neither fabricates a tenant fact nor silently mis-attributes one.
    /// </para>
    /// </remarks>
    public const string IncompleteReasonCode = "PORTAL_CONTEXT_INCOMPLETE";

    /// <summary>
    /// Gets a value indicating whether this call has a resolved tenant.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> both before any attempt and after a failed one, because in neither state
    /// is there a tenant to act on. A component that needs to distinguish those two cases should call
    /// <see cref="EnsureResolvedAsync"/> and read the outcome, which reports why.
    /// </remarks>
    bool IsResolved { get; }

    /// <summary>
    /// Gets the resolved tenant snapshot for this call.
    /// </summary>
    /// <remarks>
    /// Deliberately not nullable and deliberately throwing rather than returning a placeholder. A
    /// consumer reached through a resolved pipeline always has a tenant, so making every one of them
    /// null-check would spread a concern that belongs at the boundary; and a placeholder tenant is the
    /// one thing that must never exist, since it would be silently wrong rather than loudly absent.
    /// Components that can legitimately run before resolution - an authorisation handler that must deny
    /// rather than fault when no tenant is available - test <see cref="IsResolved"/> or ensure first.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when read before a successful resolution.
    /// </exception>
    IPortalContext Current { get; }

    /// <summary>
    /// Resolves the tenant for this call from the supplied host name, if it has not been resolved
    /// already, and reports the outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe and cheap to call repeatedly. The first call performs the work; every later call returns that
    /// same outcome without repeating it, and the host name supplied to a later call is therefore not
    /// consulted. A caller that wants to know whether resolution has happened yet reads
    /// <see cref="IsResolved"/> instead of inferring it from a return value.
    /// </para>
    /// <para>
    /// Matching is exact and ambiguity is refused, both inherited from the alias repository this delegates
    /// to. A successful outcome guarantees that <see cref="Current"/> is readable and complete; a failed
    /// one guarantees that it is not, and carries a code identifying which refusal occurred - the alias
    /// matched nothing, the alias matched more than one portal, or the matched portal is incompletely
    /// configured.
    /// </para>
    /// </remarks>
    /// <param name="httpAlias">
    /// The host name to resolve, exactly as the caller supplied it. Untrusted data throughout, and never
    /// treated as a pattern.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagates notification that the operation should be abandoned.
    /// </param>
    /// <returns>
    /// A successful outcome when this call has a complete tenant snapshot, or a failure carrying the code
    /// for the refusal that occurred.
    /// </returns>
    Task<Result> EnsureResolvedAsync(string httpAlias, CancellationToken cancellationToken);
}
