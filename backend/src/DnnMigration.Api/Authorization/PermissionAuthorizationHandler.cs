using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Decides a <see cref="PermissionRequirement"/> by asking the application layer whether the caller holds
/// the permission on the scope the route names.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy check was imperative and repeated. Each administration page consulted the
/// permission controllers itself, in its own order, and redirected on failure - which is why the same rule
/// was expressed slightly differently in dozens of code-behinds. Here the rule is declared once per policy
/// and evaluated in one place.
/// </para>
/// <para>
/// <strong>This handler makes no access decision of its own.</strong> It resolves the scope from the route,
/// asks <see cref="IPermissionService"/>, and reports the answer. Allow-and-deny precedence, pseudo-role
/// handling and the superuser short-circuit all live behind that service, so there is exactly one place
/// where a permission means something. A second interpretation here is the one defect this design exists
/// to prevent.
/// </para>
/// <para>
/// <strong>Anonymous callers are asked, not rejected.</strong> The permission model grants to pseudo-roles
/// as well as to real ones - a grant to the all-users pseudo-role reaches everybody, and a grant to the
/// unauthenticated pseudo-role reaches exactly the callers with no account - so refusing to evaluate an
/// anonymous request would make every legitimately public resource unreachable. A null caller identifier is
/// passed through as null and the service resolves the pseudo-roles that apply.
/// </para>
/// <para>
/// <strong>Failure is silence, never an exception.</strong> A handler that throws turns a denial into a
/// server fault. When the scope cannot be resolved, or the service reports a failure such as an unknown
/// module, this handler simply does not succeed, and the framework produces the appropriate 401 or 403. A
/// controller that wants to distinguish "you may not" from "it does not exist" does so itself, on the
/// service's own outcome, after authorisation has passed.
/// </para>
/// </remarks>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <summary>The route value naming the tenant.</summary>
    public const string PortalRouteKey = "portalId";

    /// <summary>The route value naming a module instance.</summary>
    public const string ModuleRouteKey = "moduleId";

    /// <summary>The route value naming a page.</summary>
    public const string TabRouteKey = "tabId";

    /// <summary>
    /// The query-string field naming a module's placement on a page.
    /// </summary>
    /// <remarks>
    /// A placement is addressed by its own surrogate key rather than by the page it sits on, because a module
    /// may be placed on the same page more than once. The module resource accepts it as a query field rather
    /// than a route segment - see <c>ModulesController.GetAsync</c> - so the placement has to be read from the
    /// query here. That it is a query field makes it no less part of what the request addresses, and reading it
    /// is what lets a caller entitled to one particular placement be decided by that placement.
    /// </remarks>
    public const string TabModuleQueryKey = "tabModuleId";

    private readonly IPermissionService _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly IPortalContextHolder _portalContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="PermissionAuthorizationHandler"/> class.</summary>
    /// <param name="permissions">The only authority on whether a permission is held.</param>
    /// <param name="currentUser">Supplies the caller's identity and tenant.</param>
    /// <param name="portalContext">
    /// Resolves the tenant from the host name for a caller whose request names none. Without it an anonymous
    /// caller has no tenant at all, and a grant to the unauthenticated pseudo-role can never be evaluated.
    /// </param>
    /// <param name="httpContextAccessor">Supplies the matched route, which names the scope.</param>
    /// <param name="logger">Records why a requirement could not be evaluated.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PermissionAuthorizationHandler(
        IPermissionService permissions,
        ICurrentUser currentUser,
        IPortalContextHolder portalContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
        _httpContextAccessor = httpContextAccessor
            ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        HttpContext? httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null)
        {
            _logger.LogWarning(
                "A {Requirement} could not be evaluated because there is no request to read a scope from.",
                nameof(PermissionRequirement));
            return;
        }

        int? portalId = await ResolvePortalIdAsync(httpContext, httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (portalId is null)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared on a route that names no tenant, and the "
                + "caller's token carries none either, so the requirement cannot be evaluated.",
                requirement.Scope);
            return;
        }

        string scopeRouteKey = requirement.Scope == PermissionScope.Module ? ModuleRouteKey : TabRouteKey;
        int? scopeId = ReadRouteInt(httpContext, scopeRouteKey);

        if (scopeId is null)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared on a route that has no '{RouteKey}' value, "
                + "so there is nothing to evaluate the permission against.",
                requirement.Scope,
                scopeRouteKey);
            return;
        }

        // THE PLACEMENT THE REQUEST ADDRESSES, when it addresses one. A module-scoped decision is about a
        // module ON A PAGE whenever the module inherits its view permission, because the answer is then the
        // page's answer and a module may sit on several pages with different grants. The request is the only
        // thing that knows which placement it means, so it is read here and passed down rather than being
        // rediscovered - or, as an earlier revision did, ignored in favour of granting when any placement would.
        //
        // BOTH FORMS OF ADDRESS ARE READ. The module resource identifies a placement by its own surrogate key
        // in the query string, because a module can be placed on one page more than once; a route that names a
        // page identifies it by that page. Reading only one of the two would leave requests that use the other
        // decided as though they had named nothing.
        //
        // Absence is passed through as absence rather than substituted: a request that addresses no placement
        // is asking the module-wide question, which the service answers over every placement at once. A
        // tab-scoped requirement has no placement to carry, because the page IS its scope.
        int? placementTabId = requirement.Scope == PermissionScope.Module
            ? ReadRouteInt(httpContext, TabRouteKey)
            : null;

        int? placementTabModuleId = requirement.Scope == PermissionScope.Module
            ? ReadQueryInt(httpContext, TabModuleQueryKey)
            : null;

        Result<bool> outcome = requirement.Scope == PermissionScope.Module
            ? await _permissions
                .HasModulePermissionAsync(
                    portalId.Value,
                    _currentUser.UserId,
                    scopeId.Value,
                    requirement.Permission,
                    placementTabId,
                    placementTabModuleId,
                    httpContext.RequestAborted)
                .ConfigureAwait(false)
            : await _permissions
                .HasTabPermissionAsync(
                    portalId.Value,
                    _currentUser.UserId,
                    scopeId.Value,
                    requirement.Permission,
                    httpContext.RequestAborted)
                .ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            // The scope does not exist, or the tenant does not. Either way the caller does not hold the
            // permission on it, so the requirement is simply not met. The reason is recorded rather than
            // returned: telling an unauthorised caller which identifiers exist is an enumeration oracle.
            _logger.LogInformation(
                "A {Scope} {Permission} requirement was not met for scope {ScopeId} in portal {PortalId}: "
                + "{FailureCode}.",
                requirement.Scope,
                requirement.Permission,
                scopeId.Value,
                portalId.Value,
                outcome.Error?.Code);
            return;
        }

        if (outcome.Value)
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>Resolves the tenant the requirement applies to.</summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="cancellationToken">Abandons the host lookup with the request.</param>
    /// <returns>The tenant, or <see langword="null"/> when none can be determined.</returns>
    /// <remarks>
    /// <para>
    /// The route wins over the token. A tenant-scoped route states which tenant the request is about, and a
    /// caller whose token names a different one must be denied on that tenant's grants rather than admitted
    /// on their own - which is precisely the cross-tenant escalation this ordering prevents. The token is
    /// consulted only for routes that name no tenant, such as the page resource addressed by its own
    /// identifier.
    /// </para>
    /// <para>
    /// <b>The host name is the last resort, and it exists for the anonymous caller.</b> A route that names no
    /// tenant, reached by a caller carrying no token, previously had no tenant at all, so the requirement was
    /// abandoned unevaluated and the caller was refused. That refusal was indistinguishable from a real denial
    /// but was not one: it deleted precisely the grants the unauthenticated pseudo-role exists to express,
    /// which is the defect the view policies were opened up to fix in the first place. Resolving the tenant
    /// from the host restores the legacy behaviour, where the requested alias identified the tenant for every
    /// visitor including the ones with no account. This fallback can only be reached when neither the route
    /// nor the token names a tenant, so it cannot widen any request that already carries one, and it decides
    /// nothing by itself - the evaluator still has to find a grant that reaches the caller.
    /// </para>
    /// </remarks>
    private async Task<int?> ResolvePortalIdAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        int? addressed = ReadRouteInt(httpContext, PortalRouteKey) ?? _currentUser.PortalId;

        if (addressed is not null)
        {
            return addressed;
        }

        if (_portalContext.IsResolved)
        {
            return _portalContext.Current?.PortalId;
        }

        Result resolution = await _portalContext
            .EnsureResolvedAsync(httpContext.Request.Host.Value ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return resolution.IsSuccess ? _portalContext.Current?.PortalId : null;
    }

    /// <summary>Reads one route value as an integer.</summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="key">The route value name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or unparseable.</returns>
    /// <remarks>
    /// Both -1 and 0 are legitimate identifiers in this schema - portals seed at -1, and modules, tabs and
    /// roles at 0 - so neither is treated as absent. Absence is the route value not being there.
    /// </remarks>
    private static int? ReadRouteInt(HttpContext httpContext, string key)
    {
        if (!httpContext.Request.RouteValues.TryGetValue(key, out object? raw))
        {
            return null;
        }

        string? text = raw as string ?? raw?.ToString();

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>Reads one query-string field as an integer, or <see langword="null"/> when it is absent.</summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="key">The query field name.</param>
    /// <returns>The parsed value, or <see langword="null"/> when absent, empty or unparseable.</returns>
    /// <remarks>
    /// <para>
    /// AN UNPARSEABLE OR REPEATED VALUE IS TREATED AS ABSENT, which is the safe direction here. Absence makes
    /// the service answer the module-wide question, which is decided over every placement at once and is
    /// therefore never laxer than any individual placement's answer - so a caller cannot widen their own
    /// decision by sending a malformed or duplicated field. Being lenient in the other direction, by guessing
    /// which of several supplied values was meant, is what would let one be chosen for its permissions rather
    /// than for its relevance.
    /// </para>
    /// <para>
    /// Parsing is invariant-culture, for the same reason the route reader above is: a caller's locale must not
    /// change which placement an identifier names, and the model binder that later binds the same field
    /// parses it invariantly too, so the two cannot disagree about what the request said.
    /// </para>
    /// </remarks>
    private static int? ReadQueryInt(HttpContext httpContext, string key)
    {
        if (!httpContext.Request.Query.TryGetValue(key, out StringValues raw) || raw.Count != 1)
        {
            return null;
        }

        return int.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }
}
