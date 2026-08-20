using System.Globalization;
using System.Security.Claims;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Settles a <see cref="PermissionRequirement"/> for the current request by naming the tenant and the item
/// the request addresses and delegating the decision to the application permission service.
/// </summary>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <summary>The route value naming the module instance a module-scoped decision is about.</summary>
    private const string ModuleRouteKey = "moduleId";

    /// <summary>
    /// The route value naming the tab - the page abstraction - a tab-scoped decision is about, and the
    /// placement a module-scoped decision is about when the route names one.
    /// </summary>
    private const string TabRouteKey = "tabId";

    /// <summary>
    /// The query value naming one particular placement of a module on a page, used when a module has been
    /// placed on the same page more than once.
    /// </summary>
    private const string TabModuleQueryKey = "tabModuleId";

    /// <summary>
    /// The reason codes the permission service reports when the item a scoped decision addresses does not
    /// exist in the resolved tenant at all.
    /// </summary>
    /// <remarks>
    /// Spelled as literals rather than shared with the application layer deliberately: these strings are
    /// part of that layer's published reason vocabulary, and a compile-time coupling from the API's
    /// authorisation to its internal constants would make either side harder to change than the other.
    /// </remarks>
    private static readonly string[] UnknownScopeItemCodes =
    {
        "permission.module_not_found",
        "permission.tab_not_found",
    };

    private readonly IPermissionService _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly IPortalContextHolder _portalContext;
    private readonly PortalAdministrationEvaluator _tenantBinding;

    /// <summary>
    /// Records why a requirement could not be evaluated, or why an evaluated requirement was not met.
    /// </summary>
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    /// <summary>
    /// Creates the handler over the permission service that decides, the caller it decides about, and the
    /// tenant context used as the last resort for naming the tenant.
    /// </summary>
    /// <param name="permissions">The application service that answers permission questions.</param>
    /// <param name="currentUser">The caller on whose behalf the current request is being handled.</param>
    /// <param name="portalContext">
    /// The tenant context, resolved on demand from the requested host when neither the route nor the caller
    /// names a tenant.
    /// </param>
    /// <param name="tenantBinding">
    /// Reconciles the three tenant identities a request can carry - arrival, route and token - and answers
    /// the host-account question from stored state.
    /// </param>
    /// <param name="logger">
    /// Records why a requirement could not be evaluated, and why an evaluated requirement was refused.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is <see langword="null"/>.</exception>
    public PermissionAuthorizationHandler(
        IPermissionService permissions,
        ICurrentUser currentUser,
        IPortalContextHolder portalContext,
        PortalAdministrationEvaluator tenantBinding,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(portalContext);
        ArgumentNullException.ThrowIfNull(tenantBinding);
        ArgumentNullException.ThrowIfNull(logger);

        _permissions = permissions;
        _currentUser = currentUser;
        _portalContext = portalContext;
        _tenantBinding = tenantBinding;
        _logger = logger;
    }

    /// <summary>
    /// Grants the requirement when the caller holds the requested permission on the item the current
    /// request addresses, and otherwise leaves it ungranted.
    /// </summary>
    /// <param name="context">The authorisation context.</param>
    /// <param name="requirement">
    /// The requirement being evaluated, naming the permission and the kind of item it is claimed against.
    /// </param>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // The framework supplies the current request as the resource for a policy applied to an endpoint.
        // Anything else means the addressed item cannot be discovered, so the requirement is left ungranted
        // rather than guessed at.
        if (context.Resource is not HttpContext httpContext)
        {
            _logger.LogWarning(
                "A {Requirement} could not be evaluated because there is no request to read a scope from.",
                nameof(PermissionRequirement));
            return;
        }

        if (await ResolvePortalIdAsync(httpContext).ConfigureAwait(false) is not { } portalId)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared on a route that names no tenant, and the "
                + "caller's token carries none either, so the requirement cannot be evaluated.",
                requirement.Scope);
            return;
        }

        // SEC: THE TOKEN'S TENANT MUST BE THE TENANT BEING EVALUATED, and this test has to come before any
        // grant is read.
        if (!await _tenantBinding.IsTenantBoundAsync(context.User, httpContext.RequestAborted)
            .ConfigureAwait(false))
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was refused because the caller's token was issued for a "
                + "different tenant than the one the request acts on.",
                requirement.Scope);
            return;
        }

        // PORTAL SCOPE IS DECIDED HERE, BEFORE ANY ROUTE KEY IS SOUGHT, because it is the one scope that
        // names no item.
        if (requirement.Scope == PermissionScope.Portal)
        {
            Result<bool> capability = await _permissions
                .HasAnyTabPermissionInPortalAsync(
                    portalId,
                    _currentUser.UserId,
                    requirement.Permission,
                    httpContext.RequestAborted)
                .ConfigureAwait(false);

            if (capability.IsSuccess && capability.Value)
            {
                context.Succeed(requirement);
                return;
            }

            _logger.LogWarning(
                "A tenant-wide {Permission} capability requirement was refused for portal {PortalId}: the "
                + "caller neither administers the tenant nor holds the permission on any of its pages.",
                requirement.Permission,
                portalId);
            return;
        }

        if (ResolveRouteKey(requirement.Scope) is not { } routeKey)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared for a scope this handler can read no route "
                + "key for, so there is nothing to evaluate the permission against.",
                requirement.Scope);
            return;
        }

        // An endpoint carrying an item-scoped policy but exposing no item key is a registration mistake,
        // not a permission decision. Refusing is the safe reading of it: the alternative would be to invent
        // a key and grant against whatever it happened to match.
        if (ReadRouteId(httpContext, routeKey) is not { } scopeId)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared on a route that has no '{RouteKey}' value, "
                + "so there is nothing to evaluate the permission against.",
                requirement.Scope,
                routeKey);
            return;
        }

        Result<bool> decision = requirement.Scope switch
        {
            PermissionScope.Module => await _permissions
                .HasModulePermissionAsync(
                    portalId,
                    _currentUser.UserId,
                    scopeId,
                    requirement.Permission,
                    ReadRouteId(httpContext, TabRouteKey),
                    ReadQueryId(httpContext, TabModuleQueryKey),
                    httpContext.RequestAborted)
                .ConfigureAwait(false),
            PermissionScope.Tab => await _permissions
                .HasTabPermissionAsync(
                    portalId,
                    _currentUser.UserId,
                    scopeId,
                    requirement.Permission,
                    httpContext.RequestAborted)
                .ConfigureAwait(false),

            // Unreachable: the requirement validates its scope while policies are being registered, and the
            // route key above already declined anything undeclared.
            _ => Result<bool>.Success(false)
        };

        // Success is tested before the value is read, and in that order only: reading the value of an
        // unsuccessful outcome throws.
        if (decision.IsSuccess && decision.Value)
        {
            context.Succeed(requirement);
            return;
        }

        // THE ITEM DOES NOT EXIST, WHICH IS NOT A DENIAL. The permission service proves the item's
        // existence before it resolves the caller, so this point is reached for an identifier that names
        // nothing - and for a caller who administers the tenant, "nothing is there" is the endpoint's own
        // answer to give, as a 404, exactly as the portal, user and role endpoints already answer it.
        if (decision.IsFailure
            && decision.Reason?.Code is { } unknownItemCode
            && UnknownScopeItemCodes.Contains(unknownItemCode, StringComparer.Ordinal)
            && await AdministersTenantAsync(context.User, httpContext.RequestAborted)
                .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "A {Scope} {Permission} requirement was satisfied for a tenant administrator so that the "
                + "endpoint can report that {ScopeId} does not exist in portal {PortalId}.",
                requirement.Scope,
                requirement.Permission,
                scopeId,
                portalId);

            context.Succeed(requirement);
            return;
        }

        _logger.LogInformation(
            "A {Scope} {Permission} requirement was not met for scope {ScopeId} in portal {PortalId}: "
            + "{FailureCode}.",
            requirement.Scope,
            requirement.Permission,
            scopeId,
            portalId,
            decision.IsFailure ? decision.Reason?.Code : "not_permitted");
    }

    /// <summary>Maps a scope onto the single route value that names the item it addresses.</summary>
    /// <param name="scope">The scope taken from the requirement.</param>
    /// <returns>
    /// The route value name, or <see langword="null"/> when the scope is not a declared member, which the
    /// caller treats as a refusal.
    /// </returns>
    private static string? ResolveRouteKey(PermissionScope scope) => scope switch
    {
        PermissionScope.Module => ModuleRouteKey,
        PermissionScope.Tab => TabRouteKey,

        // Deliberately keyless, and reached only if the tenant-wide branch above is ever removed. Answering
        // null here means such a requirement would be refused rather than evaluated against an invented
        // key, which is the safe reading of a registration that no longer matches this handler.
        PermissionScope.Portal => null,
        _ => null
    };

    /// <summary>Reads one route value from the current request and converts it to an item key.</summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="routeKey">The route value name to read.</param>
    /// <returns>
    /// The key, or <see langword="null"/> when the route does not carry the value, carries it blank, or
    /// carries something that is not an integer.
    /// </returns>
    /// <remarks>
    /// Every parsed key is returned exactly as it parsed: zero is a legitimate page and module key in this
    /// schema and a negative value is a legitimate portal key, so no value is treated as absent, clamped or
    /// rejected on its magnitude.
    /// </remarks>
    private static int? ReadRouteId(HttpContext httpContext, string routeKey) =>
        ParseId(httpContext.Request.RouteValues[routeKey]?.ToString());

    /// <summary>Reads one query value from the current request and converts it to an item key.</summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="queryKey">The query value name to read.</param>
    /// <returns>
    /// The key, or <see langword="null"/> when the query does not carry the value, carries it blank,
    /// carries it more than once, or carries something that is not an integer.
    /// </returns>
    private static int? ReadQueryId(HttpContext httpContext, string queryKey)
    {
        StringValues values = httpContext.Request.Query[queryKey];

        return values.Count == 1 ? ParseId(values[0]) : null;
    }

    /// <summary>
    /// Converts request text to an item key without applying any validity rule to the value itself.
    /// </summary>
    /// <param name="rawValue">The text read from the route or the query.</param>
    /// <returns>The key, or <see langword="null"/> when the text is absent, blank or not an integer.</returns>
    /// <remarks>
    /// Parsing is culture-invariant because these values are part of a machine-readable address rather than
    /// text a person typed, so a culture that groups digits or writes signs differently must not change
    /// which item is addressed.
    /// </remarks>
    private static int? ParseId(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Names the tenant the decision is about, preferring the addressed tenant over the caller's own and
    /// falling back to the requested host.
    /// </summary>
    /// <param name="httpContext">The current request.</param>
    /// <returns>
    /// The tenant key, or <see langword="null"/> when no tenant can be named, which the caller treats as a
    /// refusal.
    /// </returns>
    /// <remarks>
    /// The host lookup is attempted only when the first two sources are silent, and it is performed through
    /// the tenant context rather than repeated here, so one alias-matching rule serves the whole
    /// application.
    /// </remarks>
    private async Task<int?> ResolvePortalIdAsync(HttpContext httpContext)
    {
        if ((ReadRouteId(httpContext, AuthorizationClaims.PortalRouteKey) ?? _currentUser.PortalId)
            is { } addressed)
        {
            return addressed;
        }

        if (_portalContext.IsResolved)
        {
            return _portalContext.Current?.PortalId;
        }

        Result resolution = await _portalContext
            .EnsureResolvedAsync(httpContext.Request.Host.Value ?? string.Empty, httpContext.RequestAborted)
            .ConfigureAwait(false);

        return resolution.IsSuccess ? _portalContext.Current?.PortalId : null;
    }

    /// <summary>
    /// Answers whether the caller administers the tenant the request acts on, and may therefore be told
    /// that an item does not exist rather than that it is not theirs.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">Abandons the probe when the caller disconnects.</param>
    /// <returns><see langword="true"/> for a host account or for an administrator of the resolved tenant.</returns>
    /// <remarks>
    /// Delegates to the shared evaluator rather than re-deriving the answer, which is what keeps two
    /// authorisation components from disagreeing about one request: the evaluator resolves the target
    /// tenant itself, reconciles it against the token and admits host accounts, and it is the same instance
    /// the tenant-binding check above already used.
    /// </remarks>
    private Task<bool> AdministersTenantAsync(ClaimsPrincipal? user, CancellationToken cancellationToken) =>
        _tenantBinding.IsPortalAdministratorAsync(user, cancellationToken);
}
