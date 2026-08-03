using System.Globalization;
using System.Security.Claims;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Answers the two questions every tenant-scoped policy in this API is built from: is this caller a host
/// account, and does this caller administer <em>that</em> portal.
/// </summary>
/// <remarks>
/// <para>
/// THE PORTAL BEING ASKED ABOUT IS THE ONE THE REQUEST ACTS ON, NEVER THE ONE THE CALLER ARRIVED THROUGH.
/// This type exists because that distinction was previously lost. The portal-administrator decision was
/// taken against the tenant resolved from the request's <c>Host</c> header, while the controllers it gated
/// operate on an independent identifier taken from the route - so an administrator of portal A could
/// address portal B's users, roles, role groups, profile definitions or settings and pass the policy on the
/// strength of their administration of A. The route segment names the resource; the host name names only
/// how the caller reached the API. Authorisation has to follow the resource.
/// </para>
/// <para>
/// THE RULE IS THE SAME ONE THE PERMISSION HANDLER ALREADY APPLIED, and that is deliberate rather than
/// coincidental. <see cref="PermissionAuthorizationHandler"/> resolves its tenant as "the route value if
/// the route names one, otherwise the token's" and documents the reason at length: a tenant-scoped route
/// states which tenant the request is about, and a caller whose token names a different one must be denied
/// on that tenant's grants rather than admitted on their own. Two authorisation components disagreeing
/// about which portal a request is about is exactly the class of defect this consolidation removes.
/// </para>
/// <para>
/// A HOST ACCOUNT IS ANSWERED YES, AND THE EVIDENCE FOR THAT IS THE REST OF THIS SOLUTION RATHER THAN A
/// PREFERENCE. <c>Application/Services/PermissionService.cs</c> answers every permission question
/// affirmatively for a host account before reading a single grant; <c>PortalService</c> gates the hosting
/// charge, the quotas, the site-log retention and the expiry date on the caller being a host account, which
/// is only meaningful if a host account can reach a portal it does not administer; and the integration suite
/// asserts both behaviours. A portal-administrator requirement that refused a host account would therefore
/// contradict the layer beneath it - and would make a host unable to administer the very portal it had just
/// created, because a new portal's administrator role is held by the account created with it.
/// </para>
/// <para>
/// NEITHER ANSWER IS TAKEN FROM A CLAIM. Claims are cheaper and are the conventional answer, and both are
/// refused here for measured reasons. A role claim carries a NAME, and role names are not unique in this
/// schema - the stock name "Administrators" identifies a different row in every portal, so a name-based test
/// is satisfied by an administrator of any of them. Worse, legacy membership is TIME-BOUNDED: the assignment
/// table carries effective and expiry columns and the legacy membership procedure evaluated both on every
/// call, so a membership that lapsed after sign-in must stop conferring administration, which a claim minted
/// once cannot express. The super-user flag is read from the store for the weaker but still real reason that
/// a revoked host account must lose reach immediately rather than at token expiry.
/// </para>
/// <para>
/// EVERY FAILURE PATH DENIES AND NONE OF THEM FAULTS. No subject claim, no route and no ambient request, a
/// portal that does not exist, a portal that designates no administrator role, no matching assignment - each
/// answers <see langword="false"/>. A policy that threw on missing context would produce a 500 that reads
/// like a server defect and, worse, would invite the reflex of removing the check to make the error go away.
/// Refusing to decide is the safe answer.
/// </para>
/// <para>
/// Registered scoped, because its repository dependencies are scoped and because the answers it gives are
/// only meaningful for one request.
/// </para>
/// </remarks>
internal sealed class PortalAdministrationEvaluator
{
    /// <summary>The route value naming the portal a tenant-scoped request acts on.</summary>
    /// <remarks>
    /// Spelled identically to <see cref="PermissionAuthorizationHandler.PortalRouteKey"/> and taken from it,
    /// so the two cannot drift; a route template that renamed the segment would break both together rather
    /// than silently changing one decision.
    /// </remarks>
    internal const string PortalRouteKey = PermissionAuthorizationHandler.PortalRouteKey;

    /// <summary>The route value naming the account an account-scoped request acts on.</summary>
    internal const string UserRouteKey = "userId";

    /// <summary>
    /// The registered claim name carrying a token's subject, used when it has not been mapped to the
    /// framework-standard name-identifier claim.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than taken from a token-handling library so that this type needs no dependency on
    /// one. Whether the inbound <c>sub</c> claim has been mapped to the framework-standard name-identifier
    /// claim depends on host configuration that an authorisation decision should not have to know about, so
    /// both are consulted.
    /// </remarks>
    private const string JwtRegisteredSubjectClaim = "sub";

    private readonly IPortalContextHolder _portalContext;
    private readonly IPortalRepository _portals;
    private readonly IRoleRepository _roles;
    private readonly IUserRepository _users;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IClock _clock;

    /// <summary>Initialises a new instance of the <see cref="PortalAdministrationEvaluator"/> class.</summary>
    /// <param name="portalContext">Resolves and holds the tenant the caller arrived through.</param>
    /// <param name="portals">Reads the target portal, whose administrator role key is the question.</param>
    /// <param name="roles">Answers the authoritative portal-scoped membership question.</param>
    /// <param name="users">Answers the authoritative host-account question.</param>
    /// <param name="httpContextAccessor">Supplies the matched route, which names the target.</param>
    /// <param name="clock">
    /// Supplies the instant the membership window is evaluated against, so that every check taken while
    /// serving one request is taken against one instant and the boundary condition is testable.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PortalAdministrationEvaluator(
        IPortalContextHolder portalContext,
        IPortalRepository portals,
        IRoleRepository roles,
        IUserRepository users,
        IHttpContextAccessor httpContextAccessor,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(portalContext);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(clock);

        _portalContext = portalContext;
        _portals = portals;
        _roles = roles;
        _users = users;
        _httpContextAccessor = httpContextAccessor;
        _clock = clock;
    }

    /// <summary>Reads the caller's account key from their claims.</summary>
    /// <param name="user">The caller.</param>
    /// <returns>The account key, or <see langword="null"/> when no usable claim is present.</returns>
    /// <remarks>
    /// A value that is not an integer is treated as absent rather than coerced, and nothing beyond the two
    /// subject claims is consulted.
    /// </remarks>
    internal static int? TryGetUserId(ClaimsPrincipal? user)
    {
        string? raw = user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user?.FindFirstValue(JwtRegisteredSubjectClaim);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
            ? userId
            : null;
    }

    /// <summary>Reads one route value of the current request as an integer.</summary>
    /// <param name="key">The route value name.</param>
    /// <returns>The value, or <see langword="null"/> when absent, unparseable or there is no request.</returns>
    /// <remarks>
    /// Both -1 and 0 are legitimate identifiers in this schema - portals seed at -1, and modules, tabs,
    /// roles and role groups at 0 - so neither is treated as absent. Absence is the route value not being
    /// there.
    /// </remarks>
    internal int? ReadRouteInt(string key)
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null || !httpContext.Request.RouteValues.TryGetValue(key, out object? raw))
        {
            return null;
        }

        string? text = raw as string ?? raw?.ToString();

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Determines which portal the current request acts on.
    /// </summary>
    /// <returns>
    /// The route's portal when the matched route names one; otherwise the tenant the caller arrived through,
    /// resolving it first if necessary; otherwise <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The route wins. A route that names no portal - the portal collection itself, or a resource addressed
    /// by its own global identifier - falls back to the resolved tenant, which is the only tenant such a
    /// request can be about. An operation that is genuinely global carries no portal either way and is gated
    /// by the host policy rather than by this one.
    /// </remarks>
    internal async Task<int?> ResolveTargetPortalIdAsync(CancellationToken cancellationToken)
    {
        if (ReadRouteInt(PortalRouteKey) is { } routePortalId)
        {
            return routePortalId;
        }

        IPortalContext? resolved = await TryGetResolvedTenantAsync(cancellationToken).ConfigureAwait(false);

        return resolved?.PortalId;
    }

    /// <summary>
    /// Reports whether the caller is a host account, according to the store rather than the token.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns><see langword="true"/> when the caller's account carries the host flag.</returns>
    /// <remarks>
    /// A host account is looked up with no portal restriction, mirroring
    /// <c>Application/Services/PermissionService.cs</c>: a host account is not a member of any particular
    /// portal, so a portal-scoped read would not find it.
    /// </remarks>
    internal async Task<bool> IsHostAccountAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true || TryGetUserId(user) is not { } userId)
        {
            return false;
        }

        User? account = await _users
            .GetAsync(portalId: null, userId, cancellationToken)
            .ConfigureAwait(false);

        return account?.IsSuperUser == true;
    }

    /// <summary>
    /// Reports whether the caller may administer the portal the request acts on.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>
    /// <see langword="true"/> when the caller is a host account, or holds the target portal's own
    /// administrator role with a currently valid assignment.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the decision is composed from the assignment read and the entity's own status rule rather
    /// than from a predicate inside the repository, because classifying a membership is not persistence.
    /// <c>IRoleRepository.GetUserRolesAsync</c> applies the tenant anchor - <c>UserRoles</c> has no portal
    /// column, so the scope is taken from the role the assignment points at, which means a role with no
    /// owning portal can never satisfy a portal question - and <c>UserRole.GetStatus</c> applies the
    /// validity window. That window is exactly the legacy predicate
    /// <c>( EffectiveDate &lt;= getdate() or EffectiveDate is null )</c> and its expiry counterpart, seen at
    /// <c>03.02.03.SqlDataProvider:L405</c>: an absent bound is unbounded in that direction rather than
    /// "now". <c>GetStatus</c> additionally treats the legacy <c>Null.NullDate</c> marker as unset, which
    /// the SQL predicate could not, so a membership bounded by that marker is correctly read as unbounded.
    /// Every assignment is examined rather than just the first, so a duplicated pair cannot hide a valid
    /// grant behind a lapsed one.
    /// </para>
    /// <para>
    /// The administrator role key is read from the TARGET portal's own row rather than from the resolved
    /// tenant's snapshot. That is the whole of the fix: reading it from the snapshot asked whether the caller
    /// administered the portal they arrived through, which is a different question from the one the request
    /// poses.
    /// </para>
    /// </remarks>
    internal async Task<bool> IsPortalAdministratorAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true || TryGetUserId(user) is not { } userId)
        {
            return false;
        }

        if (await IsHostAccountAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await ResolveTargetPortalIdAsync(cancellationToken).ConfigureAwait(false) is not { } portalId)
        {
            // No route portal and no resolved tenant, so there is no portal whose administration could be
            // granted. Denied.
            return false;
        }

        // SEC: THE TENANT THE TOKEN WAS MINTED FOR MUST BE THE TENANT THE REQUEST IS ABOUT. Three tenants
        // are in play on any request - the one the caller arrived at, the one the credential was presented
        // to, and the one the route names - and administering a portal requires them to agree. The route
        // tenant is bound above; this binds the token tenant. Without it a caller who legitimately holds the
        // administrator role in portal B could present a token minted in portal A and administer B through
        // it, so authority granted in one tenant would travel into another for the whole life of an issued
        // token. A host account is exempt because it belongs to no tenant, which is settled before this
        // point, and this claim decides TENANCY only - the role membership below is still verified against
        // stored state, so a demoted administrator is refused however recently the token was minted.
        //
        // A token carrying no readable portal claim cannot say which tenant it was presented to and is
        // refused rather than given the benefit of the doubt: every token this installation mints carries
        // the claim, so an absent one is either foreign or a defect, and neither should confer authority.
        if (AuthorizationClaims.ReadTokenPortalId(user) != portalId)
        {
            return false;
        }

        int? administratorRoleId = await ReadAdministratorRoleIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (administratorRoleId is not { } roleId)
        {
            // Either the portal does not exist, or it designates no administrator role. Both deny, and both
            // deny identically: distinguishing them in the answer would tell an unauthorised caller which
            // portal identifiers exist. An unset designation is a configuration gap, and a gap must not
            // grant - and it is never widened to any other role.
            return false;
        }

        DateTime asOfUtc = _clock.UtcNow;

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return assignments.Any(assignment =>
            assignment.RoleId == roleId
            && assignment.GetStatus(asOfUtc) == RoleStatus.Active);
    }

    /// <summary>
    /// Reads the administrator role key of one portal.
    /// </summary>
    /// <param name="portalId">The portal the request acts on.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The role key, or <see langword="null"/> when the portal is unknown or designates none.</returns>
    /// <remarks>
    /// The already-resolved tenant snapshot is preferred when it happens to describe the same portal, which
    /// is the common case for a request addressed through its own portal's host name and saves a query
    /// without changing the answer - the snapshot's administrator role key is read from the same column.
    /// </remarks>
    private async Task<int?> ReadAdministratorRoleIdAsync(int portalId, CancellationToken cancellationToken)
    {
        if (_portalContext.IsResolved && _portalContext.Current.PortalId == portalId)
        {
            return _portalContext.Current.AdministratorRoleId;
        }

        // The aliases are deliberately not loaded: the only column this decision reads is the
        // administrator role key, and pulling a collection would cost a join on every protected request.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        return portal?.AdministratorRoleId;
    }

    /// <summary>
    /// Obtains the tenant the caller arrived through, resolving it first if necessary.
    /// </summary>
    /// <param name="cancellationToken">Abandons the resolution when the caller disconnects.</param>
    /// <returns>The resolved tenant, or <see langword="null"/> when none could be resolved.</returns>
    /// <remarks>
    /// Resolution is idempotent, so this may run before or after the alias middleware without either
    /// depending on having gone first. The mandated pipeline order places authorisation ahead of that
    /// middleware while the security property required is that no protected request is authorised without a
    /// tenant it can name; whichever runs first performs the work and the other observes the identical
    /// outcome. A refusal for any of its three reasons - unknown host, ambiguous host, incompletely
    /// configured portal - is reported here only as "no tenant"; diagnosing which is the middleware's job,
    /// and it logs it for every request including this one.
    /// </remarks>
    private async Task<IPortalContext?> TryGetResolvedTenantAsync(CancellationToken cancellationToken)
    {
        if (_portalContext.IsResolved)
        {
            return _portalContext.Current;
        }

        HttpContext? httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null)
        {
            return null;
        }

        Result outcome = await _portalContext
            .EnsureResolvedAsync(httpContext.Request.Host.Value ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return outcome.IsSuccess ? _portalContext.Current : null;
    }
}
