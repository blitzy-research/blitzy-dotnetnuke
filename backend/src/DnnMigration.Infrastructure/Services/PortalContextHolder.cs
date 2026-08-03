using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Resolves the tenant for one inbound call and holds the resulting snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Registered with a lifetime scoped to one call, which is what makes "resolve once" mean "once per
/// call" rather than "once per process". A longer lifetime would be a cross-tenant defect outright: the
/// first caller's portal would be served to every caller after it.
/// </para>
/// <para>
/// THIS TYPE NEVER TOUCHES THE WEB PIPELINE, and that is a structural constraint rather than a style
/// preference. This assembly's project file forbids a reference to the ASP.NET Core shared framework, so
/// there is no request object, no host object and no ambient accessor available here even in principle.
/// It receives the host name as a plain string. Extracting that string from the transport is the
/// pipeline's concern and stays there, which is what keeps the tenant contract's own promise that exactly
/// one component in the solution reaches into the pipeline.
/// </para>
/// <para>
/// WHY THE SNAPSHOT IS BUILT HERE RATHER THAN BY THE CALLER. The snapshot type is internal to this
/// assembly, so nothing outside it can construct one - which is deliberate, because construction is
/// where the eight tenant facts are checked for completeness. Placing that step here means a caller can
/// obtain a tenant without being able to assemble a partial one.
/// </para>
/// <para>
/// COMPLETENESS IS CHECKED BEFORE CONSTRUCTION, NOT DURING IT. The snapshot's constructor refuses blank
/// names by throwing, which is right for a programming defect but wrong for the case at hand: a portal
/// row with no administrator role designated is ordinary bad configuration in a decades-old database, and
/// it must produce a refusal the boundary can turn into a declined call, not an unhandled fault. Every
/// required fact is therefore verified below and a missing one becomes a failed outcome, so the
/// constructor's guards remain what they were meant to be - a backstop that this method is written never
/// to trip.
/// </para>
/// <para>
/// RESOLUTION IS SERIALISED AND MEMOISED. The outcome of the first attempt is the outcome for the call.
/// The lock is held only while the resolution task is created, never while it is awaited, so two
/// components consulting the tenant concurrently cannot start two database round trips and cannot observe
/// two different tenants.
/// </para>
/// </remarks>
internal sealed class PortalContextHolder : IPortalContextHolder
{
    private readonly IPortalAliasRepository _aliases;

    /// <summary>
    /// Guards creation of the resolution task. Never held across an await.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// The single resolution attempt for this call, or <see langword="null"/> before the first attempt.
    /// Memoising the task rather than the value is what makes concurrent first callers share one attempt.
    /// </summary>
    private Task<Result>? _resolution;

    /// <summary>
    /// The resolved snapshot, assigned exactly once and only on success.
    /// </summary>
    private volatile IPortalContext? _current;

    /// <summary>
    /// Initialises a new holder for one inbound call.
    /// </summary>
    /// <param name="aliases">The repository that performs the exact-match alias lookup.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="aliases"/> is <see langword="null"/>.
    /// </exception>
    public PortalContextHolder(IPortalAliasRepository aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        _aliases = aliases;
    }

    /// <inheritdoc />
    public bool IsResolved => _current is not null;

    /// <inheritdoc />
    public IPortalContext Current =>
        _current ?? throw new InvalidOperationException(
            "The tenant for this request has not been resolved. Resolve it before reading the portal " +
            "context, or test whether it is resolved when running before resolution is possible.");

    /// <inheritdoc />
    public Task<Result> EnsureResolvedAsync(string httpAlias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        lock (_gate)
        {
            // Starting the task inside the gate is what makes the attempt singular; awaiting it inside
            // would serialise the whole request behind the lock and is not done. A second caller finds
            // the field populated and receives the first caller's task, whether or not it has completed.
            return _resolution ??= ResolveAsync(httpAlias, cancellationToken);
        }
    }

    /// <summary>
    /// Performs the single resolution attempt.
    /// </summary>
    /// <param name="httpAlias">The host name to resolve.</param>
    /// <param name="cancellationToken">Propagates abandonment of the operation.</param>
    /// <returns>The outcome of the attempt.</returns>
    private async Task<Result> ResolveAsync(string httpAlias, CancellationToken cancellationToken)
    {
        // A blank host name cannot match a configured alias, and asking the database to prove it would be
        // a round trip spent on a value already known to be unusable. Refused as not-found rather than as
        // an argument fault: the value arrives from the network on every call, so an unusable one is an
        // ordinary refusal and not a programming error.
        if (string.IsNullOrWhiteSpace(httpAlias))
        {
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The request did not carry a host name that identifies a portal.");
        }

        // The repository matches the whole stored value and returns EVERY match rather than choosing one,
        // which is what makes the ambiguous case visible here. Deciding what nought, one, or more than one
        // match means is this type's judgement rather than the repository's, because it is a policy
        // question about serving a call and not a question about stored rows.
        IReadOnlyList<PortalAlias> candidates = await _aliases
            .GetAllByHttpAliasAsync(httpAlias, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The host name in the request does not identify a configured portal.");
        }

        // Two or more. Refused, not resolved: an installation in this state has a data defect an operator
        // must correct, and serving either candidate would cross a tenant boundary. The legacy resolution
        // procedure collapsed this case with min(PortalID) instead, which is the defect being removed.
        if (candidates.Count > 1)
        {
            return Result.Failure(
                IPortalContextHolder.AmbiguousReasonCode,
                "The host name in the request identifies more than one configured portal.");
        }

        PortalAlias alias = candidates[0];

        // The repository loads the owning portal with the alias. Verified rather than assumed, because the
        // alternative to a check here is a null-reference fault below.
        if (alias.Portal is not { } portal)
        {
            return Incomplete("the alias is not attached to a portal");
        }

        // The four facts the snapshot requires that the Portals table may legitimately leave unset. Each
        // is refused individually so that an operator reading the diagnostics learns which one to set.
        if (portal.AdministratorId is not { } administratorId)
        {
            return Incomplete("the portal designates no administrator account");
        }

        if (portal.AdministratorRoleId is not { } administratorRoleId)
        {
            return Incomplete("the portal designates no administrator role");
        }

        if (portal.RegisteredRoleId is not { } registeredRoleId)
        {
            return Incomplete("the portal designates no registered-user role");
        }

        // The two role NAMES are not columns on Portals - the legacy views produced them with correlated
        // sub-queries over Roles - so they are read from the roles the repository loaded alongside the
        // portal. A designated role whose row no longer exists yields no name, and that is a refusal
        // rather than a blank tenant fact: a portal whose administrator role has been deleted cannot have
        // portal-scoped administration decided about it.
        if (FindRoleName(portal, administratorRoleId) is not { } administratorRoleName)
        {
            return Incomplete("the role designated administrator does not exist on the portal");
        }

        if (FindRoleName(portal, registeredRoleId) is not { } registeredRoleName)
        {
            return Incomplete("the role designated registered-user does not exist on the portal");
        }

        if (string.IsNullOrEmpty(portal.PortalName))
        {
            return Incomplete("the portal has no name");
        }

        // The STORED alias, not the value the caller supplied. They are equal by the lookup's own
        // predicate, so this is a statement of which one is authoritative rather than a correction.
        if (string.IsNullOrEmpty(alias.HttpAlias))
        {
            return Incomplete("the matched alias has no stored host name");
        }

        _current = new PortalContextAccessor(
            portal.PortalId,
            portal.PortalName,
            alias.HttpAlias,
            administratorId,
            administratorRoleId,
            administratorRoleName,
            registeredRoleId,
            registeredRoleName);

        return Result.Success();
    }

    /// <summary>
    /// Finds the name of one of the portal's roles by key.
    /// </summary>
    /// <remarks>
    /// By key, never by name, because role names are not unique in this schema. A role whose name is
    /// stored blank is reported as absent rather than as present-and-empty, since a blank name cannot
    /// satisfy the snapshot and the caller's next step is identical either way.
    /// </remarks>
    /// <param name="portal">The portal whose roles were loaded with it.</param>
    /// <param name="roleId">The key of the role to name.</param>
    /// <returns>The role's name, or <see langword="null"/> when no such usable role is present.</returns>
    private static string? FindRoleName(Portal portal, int roleId)
    {
        foreach (Role role in portal.Roles)
        {
            if (role.RoleId == roleId)
            {
                return string.IsNullOrEmpty(role.RoleName) ? null : role.RoleName;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds an incomplete-configuration refusal.
    /// </summary>
    /// <remarks>
    /// The message names the missing FACT and never its value, nor the host name that led here. Field
    /// names are schema facts and are safe to record; the host name is attacker-supplied text and the
    /// stored values are tenant data, so neither belongs in a message that a boundary might surface.
    /// </remarks>
    /// <param name="detail">Which required tenant fact is missing.</param>
    /// <returns>A failed outcome carrying the incomplete-configuration code.</returns>
    private static Result Incomplete(string detail) =>
        Result.Failure(
            IPortalContextHolder.IncompleteReasonCode,
            $"The portal identified by this request cannot be used because {detail}.");
}
