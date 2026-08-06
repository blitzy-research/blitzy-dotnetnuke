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
    /// <summary>
    /// Longest address that could ever match, being the width of <c>dbo.PortalAlias.HTTPAlias</c>.
    /// </summary>
    /// <remarks>
    /// Pinned to the column, not chosen: <c>nvarchar(200)</c> at
    /// <c>PortalAliasConfiguration.HasMaxLength(200)</c>, so a longer candidate could never have been
    /// stored and generating it would put an unmatchable value into the query.
    /// </remarks>
    private const int MaximumAliasLength = 200;

    /// <summary>
    /// Most path segments beneath the authority that are considered when building the candidate chain.
    /// </summary>
    /// <remarks>
    /// A bound on work an anonymous caller can ask for. Every candidate becomes an element of one IN list
    /// sent to the store on every request, so an uncapped chain turns a deliberately long URL into a large
    /// query. Four is generous: the legacy signup screen composed exactly one segment beneath the authority
    /// (<c>Signup.ascx.vb</c> L232-L236), so a deeper alias can only arise from a host account typing one
    /// by hand.
    /// </remarks>
    private const int MaximumAliasPathSegments = 4;

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

        // THE ADDRESS IS A CHAIN, NOT A SINGLE VALUE, and this is what makes a child portal reachable. The
        // legacy product let a child portal be addressed by a path segment beneath a shared host - the
        // signup screen composed exactly "domain/segment" at Signup.ascx.vb:L232-L236 - so the stored alias
        // may carry path segments and the request-side counterpart, Globals.GetDomainName (L563 onward),
        // walked the request path building the value it compared. Reproducing that means generating the
        // candidates most-specific-first and preferring the longest that matches, which is the same
        // preference the legacy walk expressed by stopping at the first recognised directory.
        IReadOnlyList<string> chain = BuildAddressChain(httpAlias);

        // One round trip for the whole chain. Asking per candidate would be a query per path segment on
        // every request, which is why the repository member takes the collection.
        IReadOnlyList<PortalAlias> matches = await _aliases
            .GetAllByHttpAliasAsync(chain, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The host name in the request does not identify a configured portal.");
        }

        // The most specific candidate that matched anything wins. A parent and its child are BOTH expected
        // to match - "host" and "host/child" are two rows and both are legitimate - so the presence of more
        // than one match across DIFFERENT candidates is the ordinary case rather than an ambiguity, and
        // collapsing it would serve the parent's content under the child's address.
        string? resolvedAddress = null;
        foreach (string candidate in chain)
        {
            if (matches.Any(match => string.Equals(match.HttpAlias, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedAddress = candidate;
                break;
            }
        }

        if (resolvedAddress is null)
        {
            // The store answered with rows whose value is in the chain by the repository's comparison but
            // not by this one. Treated as no match rather than guessing which row was meant, because the two
            // comparisons disagreeing is a defect and resolving a tenant on a defect is how a request ends
            // up served by the wrong tenant.
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The host name in the request does not identify a configured portal.");
        }

        List<PortalAlias> candidates = matches
            .Where(match => string.Equals(match.HttpAlias, resolvedAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Two or more rows for THE SAME address. Refused, not resolved: an installation in this state has a
        // data defect an operator must correct, and serving either candidate would cross a tenant boundary.
        // The legacy resolution procedure collapsed this case with min(PortalID) instead, which is the
        // defect being removed.
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

        // The alias KEY travels with the alias value, taken from the row the lookup matched. It is
        // what lets an alias administration screen identify the row the request arrived through, which
        // the legacy screen did with the same fact: IsNotCurrent at
        // Website/admin/Portal/PortalAlias.ascx.vb L51-L60 compared each row's key against
        // Me.PortalAlias.PortalAliasID() and hid the edit affordance on a match. Carrying the key
        // rather than re-deriving the answer from the host name matters because stored casing need not
        // match what a caller submitted - the legacy write path lower-cased while its reader did not -
        // so a string comparison would need a casing rule of its own and would become a second,
        // independent answer to a question this resolver has already settled exactly.
        _current = new PortalContextAccessor(
            portal.PortalId,
            portal.PortalName,
            alias.HttpAlias,
            alias.PortalAliasId,
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

    /// <summary>
    /// Builds the chain of addresses a request could be matched by, most specific first.
    /// </summary>
    /// <param name="address">
    /// The address as the caller supplied it: a host name, optionally followed by the request's path.
    /// </param>
    /// <returns>The candidate addresses, longest first, and never empty for a non-blank input.</returns>
    /// <remarks>
    /// <para>
    /// The chain for <c>host/child/api/v1</c> is <c>host/child/api/v1</c>, <c>host/child/api</c>,
    /// <c>host/child</c>, <c>host</c> - progressively fewer path segments, with the bare authority last.
    /// Empty segments collapse, so a doubled or trailing slash produces no duplicate candidate and no
    /// candidate ending in a slash, neither of which the alias column ever holds.
    /// </para>
    /// <para>
    /// TWO BOUNDS, BOTH DELIBERATE. Candidates longer than the alias column CANNOT match, so they are not
    /// generated: <c>dbo.PortalAlias.HTTPAlias</c> is <c>nvarchar(200)</c> and a longer value could never
    /// have been stored. And the number of path segments considered is capped, because the chain becomes an
    /// IN list sent to the store on every request and an uncapped one turns a long URL into a large query -
    /// a denial-of-service vector reachable by any anonymous caller. The cap is generous relative to what
    /// the legacy product could produce: its signup screen composed exactly ONE path segment beneath the
    /// authority (<c>Signup.ascx.vb</c> L232-L236), and a host account typing a deeper value by hand is the
    /// only way more than one arises.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> BuildAddressChain(string address)
    {
        string trimmed = address.Trim();

        string[] parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return Array.Empty<string>();
        }

        string authority = parts[0];

        int segments = Math.Min(parts.Length - 1, MaximumAliasPathSegments);

        List<string> chain = new(segments + 1);

        for (int depth = segments; depth >= 1; depth--)
        {
            string candidate = string.Join('/', parts.Take(depth + 1));

            if (candidate.Length <= MaximumAliasLength)
            {
                chain.Add(candidate);
            }
        }

        chain.Add(authority);

        return chain;
    }
}
