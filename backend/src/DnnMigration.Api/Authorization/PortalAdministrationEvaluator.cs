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
/// TENANT RESOLUTION IS OWNED HERE, and the division of labour with the permission handler is deliberate
/// rather than coincidental.
/// </para>
/// <para>
/// A HOST ACCOUNT IS ANSWERED YES, AND THE EVIDENCE FOR THAT IS THE REST OF THIS SOLUTION RATHER THAN A
/// PREFERENCE. <c>Application/Services/PermissionService.cs</c> answers every permission question
/// affirmatively for a host account before reading a single grant; <c>PortalService</c> gates the hosting
/// charge, the quotas, the site-log retention and the expiry date on the caller being a host account, which
/// is only meaningful if a host account can reach a portal it does not administer; and the integration
/// suite asserts both behaviours.
/// </para>
/// </remarks>
internal sealed class PortalAdministrationEvaluator
{
    /// <summary>The route value naming the portal a tenant-scoped request acts on.</summary>
    /// <remarks>
    /// Taken from <see cref="AuthorizationClaims.PortalRouteKey"/> rather than re-spelled, so the two
    /// cannot drift; a route template that renamed the segment would break every reader together rather
    /// than silently changing one decision.
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
    internal static int? TryGetUserId(ClaimsPrincipal? user)
    {
        string? raw = user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user?.FindFirstValue(JwtRegisteredSubjectClaim);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
            ? userId
            : null;
    }

    /// <summary>The token that is cancelled when the caller abandons the request being authorised.</summary>
    /// <value>
    /// The current request's abort token, or <see cref="CancellationToken.None"/> when this evaluator is
    /// reached outside a request.
    /// </value>
    /// <remarks>
    /// A missing request yields <see cref="CancellationToken.None"/> rather than throwing, because that is
    /// the state a unit test evaluating a requirement in isolation is in, and it is the same state <see
    /// cref="ReadRouteInt(string)"/> already treats as "no route values".
    /// </remarks>
    internal CancellationToken RequestAborted =>
        _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

    /// <summary>Reads one route value of the current request as an integer.</summary>
    /// <param name="key">The route value name.</param>
    /// <returns>The value, or <see langword="null"/> when absent, unparseable or there is no request.</returns>
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

    /// <summary>Determines which portal the current request acts on.</summary>
    /// <returns>
    /// The route's portal when the matched route names one; otherwise the tenant the caller arrived
    /// through, resolving it first if necessary; otherwise <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The route wins. A route that names no portal - the portal collection itself, or a resource addressed
    /// by its own global identifier - falls back to the resolved tenant, which is the only tenant such a
    /// request can be about.
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

    /// <summary>Reports whether the caller is a host account, according to the store rather than the token.</summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns><see langword="true"/> when the caller's account carries the host flag.</returns>
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

    /// <summary>Reports whether the caller's token was issued for the tenant the request acts on.</summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>
    /// <see langword="true"/> when the caller presents no token at all, when the caller is an authoritative
    /// host account, or when the token's portal claim and the arrival tenant both name the same portal the
    /// request acts on; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// SEC: THIS IS THE ONE PLACE THE THREE TENANT IDENTITIES ARE RECONCILED, and every policy that needs
    /// the reconciliation asks here rather than repeating it.
    /// </remarks>
    internal async Task<bool> IsTenantBoundAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            // A CALLER WITH NO TOKEN CANNOT CONTRADICT THE TARGET TENANT, so there is nothing here to
            // refuse.
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

        // SEC: AND THE TENANT THE CALLER ARRIVED THROUGH MUST BE THE SAME ONE. This is the third identity,
        // and binding it is what closes the arrival-tenant bypass: a route naming portal B, reached under
        // portal A's host name or under a host name that names no tenant at all, is not a request about B
        // that happens to have taken an unusual path - it is a request whose only claim to B is the
        // identifier it put in the route.
        IPortalContext? arrival = await TryGetResolvedTenantAsync(cancellationToken).ConfigureAwait(false);

        return arrival is not null && arrival.PortalId == portalId;
    }

    /// <summary>Reports whether the caller may administer the portal the request acts on.</summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>
    /// <see langword="true"/> when the caller is a host account, or holds the target portal's own
    /// administrator role with a currently valid assignment.
    /// </returns>
    /// <remarks>
    /// The decision is composed from the assignment read and the entity's own status rule rather than from
    /// a predicate inside the repository, because classifying a membership is not persistence.
    /// <c>IRoleRepository.GetUserRolesAsync</c> applies the tenant anchor - <c>UserRoles</c> has no portal
    /// column, so the scope is taken from the role the assignment points at, which means a role with no
    /// owning portal can never satisfy a portal question - and <c>UserRole.GetStatus</c> applies the
    /// validity window.
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
        // components.
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
            // portal identifiers exist.
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

    /// <summary>Reads the administrator role key of one portal.</summary>
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

    /// <summary>Obtains the tenant the caller arrived through, resolving it first if necessary.</summary>
    /// <param name="cancellationToken">Abandons the resolution when the caller disconnects.</param>
    /// <returns>The resolved tenant, or <see langword="null"/> when none could be resolved.</returns>
    /// <remarks>
    /// Resolution is idempotent, so this may run before or after the alias middleware without either
    /// depending on having gone first.
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
