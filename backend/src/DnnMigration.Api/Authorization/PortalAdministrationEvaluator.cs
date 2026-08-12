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
/// TENANT RESOLUTION IS OWNED HERE, and the division of labour with the permission handler is deliberate
/// rather than coincidental. This evaluator answers tenant-wide administration questions, so it has to
/// establish which tenant a request is about, and it reads that from the route segment: a tenant-scoped
/// route states which tenant the request concerns, and a caller whose token names a different one must be
/// judged on that tenant's grants rather than admitted on their own.
/// <see cref="PermissionAuthorizationHandler"/> answers a different question - whether a caller holds a
/// named permission on one module or one page - so it identifies the item from that item's own route
/// segment, and it prefers this same portal segment when naming the tenant before falling back to the
/// caller's affiliation and then to the requested host. Neither component decides the other's question,
/// which is what keeps two authorisation components from disagreeing about a request; that class of
/// defect is exactly what this consolidation removes.
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
    /// Taken from <see cref="AuthorizationClaims.PortalRouteKey"/> rather than re-spelled, so the two cannot
    /// drift; a route template that renamed the segment would break every reader together rather than
    /// silently changing one decision. The shared constant is the right home for it because the segment name
    /// is a property of the route templates, not of any one component that reads them - an evaluator
    /// borrowing the name from an authorisation handler had the dependency the wrong way round.
    /// </remarks>
    internal const string PortalRouteKey = AuthorizationClaims.PortalRouteKey;

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

    /// <summary>
    /// The token that is cancelled when the caller abandons the request being authorised.
    /// </summary>
    /// <value>
    /// The current request's abort token, or <see cref="CancellationToken.None"/> when this evaluator is
    /// reached outside a request.
    /// </value>
    /// <remarks>
    /// <para>
    /// WHY THIS IS EXPOSED HERE RATHER THAN OBTAINED BY THE HANDLERS. The framework's authorisation context
    /// carries no cancellation token of its own, so a handler has no token to pass and previously passed
    /// <see cref="CancellationToken.None"/> - which quietly opted every store read an authorisation decision
    /// makes out of cancellation, in a solution whose rule is that every I/O-bound path is cancellable
    /// (AAP rule T6). This evaluator already holds the request accessor it needs in order to read route
    /// values, so the token is available here and nowhere else, and surfacing it costs the handlers no new
    /// dependency.
    /// </para>
    /// <para>
    /// A missing request yields <see cref="CancellationToken.None"/> rather than throwing, because that is the
    /// state a unit test evaluating a requirement in isolation is in, and it is the same state
    /// <see cref="ReadRouteInt(string)"/> already treats as "no route values". Answering with an uncancellable
    /// token there preserves exactly the behaviour that existed before, so nothing outside a live request
    /// changes.
    /// </para>
    /// </remarks>
    internal CancellationToken RequestAborted =>
        _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

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
    /// Reports whether the caller's token was issued for the tenant the request acts on.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>
    /// <see langword="true"/> when the caller presents no token at all, when the caller is an authoritative
    /// host account, or when the token's portal claim and the arrival tenant both name the same portal the
    /// request acts on; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// SEC: THIS IS THE ONE PLACE THE THREE TENANT IDENTITIES ARE RECONCILED, and every policy that needs the
    /// reconciliation asks here rather than repeating it. A request carries as many as three: the tenant the
    /// caller ARRIVED through (the host name, resolved against the alias table), the tenant the route NAMES,
    /// and the tenant the token was ISSUED for. <see cref="ResolveTargetPortalIdAsync"/> collapses the first
    /// two - the route wins where it names one, because the route names the resource - and this member binds
    /// the third to the result.
    /// </para>
    /// <para>
    /// WITHOUT THIS BINDING, AUTHORITY TRAVELS BETWEEN TENANTS. An account may belong to several portals
    /// (<c>dbo.Users</c> is installation-wide; <c>dbo.UserPortals</c> associates it with tenants), and roles
    /// and permissions are per portal. A caller could therefore present a token minted in portal A - carrying
    /// A's roles, A's permissions and A's grants - against a route naming portal B, and be judged on the
    /// authority it holds in A. Binding the claim closes that for the whole life of the token rather than
    /// only at issue time.
    /// </para>
    /// <para>
    /// A HOST ACCOUNT IS THE ONE EXEMPTION, and it is read from the store, not from the token's super-user
    /// claim, so an account demoted since sign-in loses the exemption at once. It has to be exempt: a host
    /// account belongs to no tenant, so it has no portal claim that could ever equal a route's portal, and
    /// every other layer of this solution already answers a host account affirmatively.
    /// </para>
    /// <para>
    /// AN ABSENT CLAIM IS A REFUSAL, BUT AN ABSENT TOKEN IS NOT. Every token this installation mints carries
    /// the portal claim, so an AUTHENTICATED caller without one is either foreign or a defect and neither
    /// should confer authority; a request that names no target portal at all - no route segment and no
    /// resolvable arrival tenant - is likewise refused, because there is then nothing for the claim to agree
    /// with. An ANONYMOUS caller is the opposite case and is answered affirmatively: it holds no authority in
    /// any tenant, so none can travel, and refusing it would withdraw the anonymous access the target tenant
    /// itself published rather than close a hole.
    /// </para>
    /// </remarks>
    internal async Task<bool> IsTenantBoundAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            // A CALLER WITH NO TOKEN CANNOT CONTRADICT THE TARGET TENANT, so there is nothing here to refuse.
            // This member reconciles a token's portal against the tenant a request acts on; an anonymous
            // caller presents no portal claim, no roles and no permissions, so it carries no authority from
            // any tenant that could travel to another one - which is the entire harm this member exists to
            // prevent. Whether such a caller may proceed is decided wholly by the grants the TARGET portal
            // publishes to the pseudo-roles, which is how the legacy application served anonymous readers:
            // Website/release.config registered the unauthenticated role name, and a page or module granting
            // view to it, or to All Users, was readable without an account.
            //
            // ANSWERING false HERE WOULD BE A DENIAL OF SERVICE, NOT A HARDENING. It would make every
            // permission-protected endpoint unreachable without a token regardless of what the tenant
            // published, which is a behaviour change no finding asked for and which contradicts Rule T5. It is
            // asserted against by the anonymous pseudo-role facts in the module and page suites.
            return true;
        }

        if (await IsHostAccountAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await ResolveTargetPortalIdAsync(cancellationToken).ConfigureAwait(false) is not { } portalId)
        {
            return false;
        }

        if (AuthorizationClaims.ReadTokenPortalId(user) != portalId)
        {
            return false;
        }

        // SEC: AND THE TENANT THE CALLER ARRIVED THROUGH MUST BE THE SAME ONE. This is the third identity, and
        // binding it is what closes the arrival-tenant bypass: a route naming portal B, reached under portal
        // A's host name or under a host name that names no tenant at all, is not a request about B that
        // happens to have taken an unusual path - it is a request whose only claim to B is the identifier it
        // put in the route. Every tenant-scoped decision this API takes is therefore about one tenant reached
        // one way.
        //
        // AN UNRESOLVED ARRIVAL TENANT IS A REFUSAL, not a pass. A host name that resolves to no portal cannot
        // agree with anything, and treating "unknown" as "whatever the route said" is precisely the bypass.
        // The host account exemption settled above is the deliberate escape hatch: an operator whose alias
        // table is misconfigured signs in as a host account and repairs it, which is the one scenario that
        // genuinely needs to work from an unconfigured host.
        IPortalContext? arrival = await TryGetResolvedTenantAsync(cancellationToken).ConfigureAwait(false);

        return arrival is not null && arrival.PortalId == portalId;
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

        // SEC: THE TENANT THE TOKEN WAS MINTED FOR MUST BE THE TENANT THE REQUEST IS ABOUT. The rule itself
        // lives on IsTenantBoundAsync, which every tenant-scoped policy in this API asks, so that the three
        // tenant identities are reconciled in exactly one place and cannot be reconciled differently by two
        // components. The host-account arm inside it is already settled above, so reaching it here costs one
        // claim comparison. This decides TENANCY only - the role membership below is still verified against
        // stored state, so a demoted administrator is refused however recently the token was minted.
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
