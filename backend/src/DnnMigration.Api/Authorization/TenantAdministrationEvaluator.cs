using System.Security.Claims;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Answers the two questions every administrative authorisation handler in this layer needs: whether the
/// caller holds installation-wide authority, and whether the caller administers the one tenant this request
/// is about.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS A SEPARATE TYPE RATHER THAN LOGIC INSIDE ONE HANDLER. Three requirements need the same two
/// answers - portal administration, installation-wide administration, and access to a particular account -
/// and the second of those answers is the delicate one: it compares three independent tenants and then
/// performs a time-bounded membership read. A copy of that comparison per handler would eventually diverge,
/// and a divergence between two authorisation handlers is invisible until the day two endpoints disagree
/// about who may reach them. Stating it once means every handler asks the same question and cannot get a
/// different answer.
/// </para>
/// <para>
/// IT DECIDES NOTHING ABOUT REQUIREMENTS. It reports facts; the handlers combine those facts with the
/// requirement they were asked about and call <c>Succeed</c>. Nothing here touches
/// <c>AuthorizationHandlerContext</c>, which is what keeps it usable from every handler and testable without
/// one.
/// </para>
/// <para>
/// EVERY ANSWER FAILS CLOSED. An unauthenticated caller, an unreadable subject claim, an account that no
/// longer exists, an unresolvable tenant, a tenant that designates no administrator role, a token minted for
/// a different tenant, a route naming a different tenant - each yields <see langword="false"/> rather than an
/// exception. A handler that faults turns a denial into a 500, which reads like a server defect and invites
/// the reflex of removing the check to make the error go away.
/// </para>
/// </remarks>
internal sealed class TenantAdministrationEvaluator
{
    private readonly IPortalContextHolder _portalContext;
    private readonly IRoleRepository _roles;
    private readonly IUserRepository _users;
    private readonly IClock _clock;

    /// <summary>Initialises a new evaluator.</summary>
    /// <param name="portalContext">Resolves and holds the tenant for the current request.</param>
    /// <param name="roles">Answers the authoritative portal-scoped membership question.</param>
    /// <param name="users">Reads the account row whose super-user column decides host authority.</param>
    /// <param name="clock">
    /// Supplies the instant a membership window is judged against, so that every check taken while serving
    /// one request is taken against one instant and the boundary condition is testable.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TenantAdministrationEvaluator(
        IPortalContextHolder portalContext,
        IRoleRepository roles,
        IUserRepository users,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(portalContext);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(clock);

        _portalContext = portalContext;
        _roles = roles;
        _users = users;
        _clock = clock;
    }

    /// <summary>
    /// Reports whether the caller's stored account is an installation-wide host account.
    /// </summary>
    /// <param name="userId">The caller's account key.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns><see langword="true"/> when the account exists and is a host account.</returns>
    /// <remarks>
    /// Read from the stored row and never from the token's <c>is_superuser</c> claim, which is documented as
    /// informational. A claim minted at sign-in cannot observe an account demoted since, so trusting it would
    /// extend installation-wide authority for the remaining life of every issued token. The read passes no
    /// portal, because a host account exists above the tenants and may hold membership of none of them.
    /// </remarks>
    public async Task<bool> IsHostAccountAsync(int userId, CancellationToken cancellationToken)
    {
        User? account = await _users
            .GetAsync(portalId: null, userId, cancellationToken)
            .ConfigureAwait(false);

        return account?.IsSuperUser == true;
    }

    /// <summary>
    /// Reports whether the caller administers the tenant this request is about.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="userId">The caller's account key, already read from their claims.</param>
    /// <param name="request">The current request, or <see langword="null"/> when there is none.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns><see langword="true"/> when the caller administers that tenant.</returns>
    /// <remarks>
    /// <para>
    /// THREE TENANTS MUST AGREE. The tenant the request ARRIVED at is resolved from the host name; the tenant
    /// the token was MINTED for comes from the caller's own claim; the tenant the route is ABOUT comes from
    /// the route template. Administering a portal requires all three to name the same one. Comparing fewer
    /// than three is what allows an administrator of portal A to issue a request whose route names portal B:
    /// the membership test passes against A while every identifier in the request names B.
    /// </para>
    /// <para>
    /// A route that names NO tenant is admitted against the other two. That is bounded deliberately - the
    /// installation-wide operations are guarded by <see cref="HostAdministratorRequirement"/> instead, so what
    /// remains without a tenant in its template is the read-only installation catalogues, which carry no
    /// tenant's data.
    /// </para>
    /// <para>
    /// MEMBERSHIP IS TESTED BY KEY AND BY TIME, never by role name. Role names are not unique in this schema -
    /// no unique constraint or index on <c>Roles.RoleName</c> appears anywhere in the eighty-eight upgrade
    /// scripts - so the stock name "Administrators" identifies a different row in every portal and a
    /// name-based test is satisfied by an administrator of any of them. The administrator role's KEY is read
    /// from the resolved tenant and the question asked is whether the caller holds THAT role IN THAT PORTAL.
    /// Role CLAIMS are not consulted for the same reason, and for a second: legacy membership is
    /// time-bounded, so a membership that has lapsed since sign-in must stop conferring administration, which
    /// a claim minted once cannot express.
    /// </para>
    /// </remarks>
    public async Task<bool> AdministersRequestTenantAsync(
        ClaimsPrincipal user,
        int userId,
        HttpContext? request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        IPortalContext? portal = await TryResolveTenantAsync(request, cancellationToken).ConfigureAwait(false);
        if (portal is null)
        {
            return false;
        }

        // The tenant the credential was presented to must be the tenant the request arrived at. A token
        // minted for another portal carries no administrative authority here, whatever roles it names.
        if (AuthorizationClaims.ReadTokenPortalId(user) is not { } tokenPortalId
            || tokenPortalId != portal.PortalId)
        {
            return false;
        }

        // The tenant the route is ABOUT must be the same tenant again. Absence is admitted; a MISMATCH is
        // refused, because it is precisely the cross-tenant escalation this comparison exists to stop.
        if (request is not null
            && AuthorizationClaims.ReadRouteInt(request, AuthorizationClaims.PortalRouteKey)
                is { } routePortalId
            && routePortalId != portal.PortalId)
        {
            return false;
        }

        if (portal.AdministratorRoleId is not { } administratorRoleId)
        {
            // The tenant designates no administrator role, so no assignment could name one. Refused rather
            // than widened to any other role: an unset designation is a configuration gap, and a gap must
            // not grant.
            return false;
        }

        // MIGRATION: the decision is composed from the assignment read and the entity's own status rule
        // rather than from a predicate inside the repository, because classifying a membership is not
        // persistence. IRoleRepository.GetUserRolesAsync applies the tenant anchor - UserRoles has no portal
        // column, so the scope is taken from the role the assignment points at, which means a role with no
        // owning portal can never satisfy a portal question - and UserRole.GetStatus applies the validity
        // window. That window is exactly the legacy predicate
        // ( EffectiveDate <= getdate() or EffectiveDate is null ) and its expiry counterpart, seen at
        // 03.02.03.SqlDataProvider:L405: an absent bound is unbounded in that direction rather than "now".
        // GetStatus additionally treats the legacy Null.NullDate marker as unset, which the SQL predicate
        // could not, so a membership bounded by that marker is correctly read as unbounded. Every assignment
        // is examined rather than just the first, so a duplicated pair cannot hide a valid grant behind a
        // lapsed one.
        DateTime asOfUtc = _clock.UtcNow;

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portal.PortalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return UserRole.AnyActiveInRole(assignments, administratorRoleId, asOfUtc);
    }

    /// <summary>
    /// Obtains the resolved tenant for this request, resolving it first if necessary.
    /// </summary>
    /// <param name="request">The current request, or <see langword="null"/> when there is none.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>The resolved tenant, or <see langword="null"/> when none could be resolved.</returns>
    /// <remarks>
    /// <para>
    /// WHY RESOLUTION HAPPENS HERE RATHER THAN BEING ASSUMED. In the mandated pipeline order authorisation
    /// runs BEFORE portal-alias resolution - the migration plan fixes that order and describes it as
    /// non-negotiable - while the security property required of these policies is that no protected request is
    /// authorised without a resolved tenant. Both hold simultaneously only because resolution is idempotent:
    /// this evaluator ensures the tenant is resolved before deciding, the middleware ensures the same thing at
    /// its mandated position, and whichever runs first does the work while the other observes the identical
    /// outcome. Neither is redundant - the middleware covers every request including those no policy protects -
    /// and neither can reach a different answer than the other.
    /// </para>
    /// <para>
    /// A refusal for any of its three reasons - unknown host, ambiguous host, incompletely configured portal -
    /// leaves the caller unauthorised. The reason is not surfaced from here: an authorisation handler reports
    /// only "not authorised", and diagnosing why the tenant could not be resolved is the alias middleware's
    /// job, which logs it for every request including this one.
    /// </para>
    /// </remarks>
    private async Task<IPortalContext?> TryResolveTenantAsync(
        HttpContext? request,
        CancellationToken cancellationToken)
    {
        // Already resolved - by the middleware on a path where it ran first, or by a previous policy
        // evaluation during this same request. Nothing to do, and deliberately no second query.
        if (_portalContext.IsResolved)
        {
            return _portalContext.Current;
        }

        // Not yet resolved, so resolve it from the host name the caller used. When there is no request -
        // a policy evaluated directly from a test or a background caller - there is nothing to resolve
        // from and the caller is refused.
        if (request is null)
        {
            return null;
        }

        Result outcome = await _portalContext
            .EnsureResolvedAsync(request.Request.Host.Value ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return outcome.IsSuccess ? _portalContext.Current : null;
    }
}
