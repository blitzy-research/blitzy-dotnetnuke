using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;

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

    private readonly IPermissionService _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="PermissionAuthorizationHandler"/> class.</summary>
    /// <param name="permissions">The only authority on whether a permission is held.</param>
    /// <param name="currentUser">Supplies the caller's identity and tenant.</param>
    /// <param name="httpContextAccessor">Supplies the matched route, which names the scope.</param>
    /// <param name="logger">Records why a requirement could not be evaluated.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PermissionAuthorizationHandler(
        IPermissionService permissions,
        ICurrentUser currentUser,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
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

        int? portalId = ResolvePortalId(httpContext);

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

        Result<bool> outcome = requirement.Scope == PermissionScope.Module
            ? await _permissions
                .HasModulePermissionAsync(
                    portalId.Value,
                    _currentUser.UserId,
                    scopeId.Value,
                    requirement.Permission,
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
    /// <returns>The tenant, or <see langword="null"/> when none can be determined.</returns>
    /// <remarks>
    /// The route wins over the token. A tenant-scoped route states which tenant the request is about, and a
    /// caller whose token names a different one must be denied on that tenant's grants rather than admitted
    /// on their own - which is precisely the cross-tenant escalation this ordering prevents. The token is
    /// consulted only for routes that name no tenant, such as the page resource addressed by its own
    /// identifier.
    /// </remarks>
    private int? ResolvePortalId(HttpContext httpContext)
    {
        return ReadRouteInt(httpContext, PortalRouteKey) ?? _currentUser.PortalId;
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
}
