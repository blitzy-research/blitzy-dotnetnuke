using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Settles a <see cref="PermissionRequirement"/> for the current request by naming the tenant and the
/// item the request addresses and delegating the decision to the application permission service.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it replaces.</b> The legacy application asked its permission questions imperatively, inside
/// the page that needed the answer, through a pair of static helpers:
/// <c>TabPermissionController.HasTabPermission</c> (<c>TabPermissionController.vb</c> L38-L54) and
/// <c>ModulePermissionController.HasModulePermission</c> (<c>ModulePermissionController.vb</c>
/// L33-L50). Both walked a collection of assignment rows and, for each row whose key matched,
/// consulted <c>PortalSecurity.IsInRoles</c> (<c>PortalSecurity.vb</c> L115-L136), which read the
/// caller from ambient per-request state (<c>HttpContext.Current</c>, L118). Thirty-nine such checks
/// were scattered across the administration pages, and the deprecated single-argument overload at
/// <c>ModulePermissionController.vb</c> L377 is not carried across at all. Here the question is
/// declared by a policy and this handler is the single place that answers it.
/// </para>
/// <para>
/// <b>It delegates; it does not decide.</b> No role name is compared here, no assignment row is
/// iterated, no grant flag is inspected, no deny precedence is applied, no sentinel role is
/// interpreted and nothing is cached. All of that belongs to the application service and, beneath it,
/// to the single evaluator it delegates to. Naming the tenant and the item, and interpreting an
/// outcome wrapper, is plumbing rather than access-control policy; keeping the two apart is what stops
/// a second, divergent evaluator from growing in this layer, which is the worst result available in
/// this area.
/// </para>
/// <para>
/// <b>Authentication is the policy's business, not this handler's.</b> Requirements inside one policy
/// are ANDed, so a policy that demands a verified caller settles that before this handler is
/// consulted - which is how the edit policies are registered. The view policies are deliberately
/// registered without that demand, because the migrated data really holds grants to two sentinel roles
/// that the evaluator really implements: identifier -1 reaches everybody and -3 reaches precisely the
/// callers who hold no account. Re-testing authentication here would delete both classes of grant and
/// make the refusal indistinguishable from a genuine denial - the legacy behaviour recorded at
/// <c>PortalSecurity.vb</c> L124-L125 being silently dropped rather than preserved. The caller's
/// account key is therefore handed over exactly as held, and its absence <em>is</em> the signal that
/// the caller holds no account, which is why it is never coalesced to a stand-in value.
/// </para>
/// <para>
/// <b>Why the tenant is resolved in three steps.</b> The route segment is preferred, because a
/// tenant-scoped route states which tenant the request concerns and a caller whose token names a
/// different one must be judged on the addressed tenant's grants rather than admitted on their own.
/// The caller's own affiliation comes next. The requested host is the last resort, and it is not
/// optional: a page is addressed by page key alone, so that route names no tenant, and a caller
/// holding no account carries none either - which would leave the requirement abandoned unevaluated
/// for exactly the anonymous callers the view policies were opened up for. A refusal of that kind is
/// indistinguishable from a denial while being nothing of the kind. The host is how the legacy
/// application identified the tenant for every visitor, signed in or not.
/// </para>
/// <para>
/// <b>Module placement is passed through when the request names one.</b> A module can sit on several
/// pages, and where it inherits its view permission the answer is the page's answer, so the placement
/// the request addresses changes the decision. Both forms of address are read - the page key from the
/// route and the placement key from the query string - because a module may be placed on one page more
/// than once. Absence is passed through as absence, which asks the module-wide question the service
/// answers across every placement at once.
/// </para>
/// <para>
/// <b>It fails closed, and it never vetoes.</b> Five situations end with the requirement simply not
/// granted: a resource that is not the current request, a tenant that cannot be named, a scope that is
/// not a declared member, an addressed item the route does not name or names unparseably, and an
/// unsuccessful outcome from the service. None of them throws, writes to the reply, sets a status code
/// or produces an error body. The handler also never calls the requirement-level veto (<c>Fail</c>): a
/// veto cannot be overridden by any other handler, which would permanently foreclose composing this
/// requirement with an alternative - a future "may edit this module, or else administers the portal"
/// policy, for instance. Declining to grant is the correct denial, and the framework renders an
/// ungranted requirement as a challenge when the caller was never identified and a refusal once it
/// was.
/// </para>
/// <para>
/// <b>The super-user flag is not consulted.</b> The legacy predicate opened by short-circuiting on it,
/// but that test sat <em>inside</em> the row loop, so a super user matched an assignment row that
/// already existed and conjured none where the loop never ran. Short-circuiting at this layer would
/// grant access to an item carrying no matching assignment at all - a privilege escalation relative to
/// the behaviour being preserved, and a duplication of a decision that belongs one layer down. The
/// flag is neither read nor forwarded here.
/// </para>
/// <para>
/// <b>Divergences from the legacy behaviour</b>, each annotated at the point it applies and recorded
/// in <c>MIGRATION_NOTES.md</c>: the legacy evaluation helpers ignored an assignment row's
/// <c>AllowAccess</c> column while the permission-string builders in the very same two classes
/// filtered on it (<c>TabPermissionController.vb</c> L218, <c>ModulePermissionController.vb</c>
/// L243), and the target settles that contradiction by applying deny precedence beneath this call; the
/// bracketed per-user sentinel-role encoding is dropped in favour of a first-class nullable account
/// key; and a refusal is an HTTP status rather than a browser sent to an access-denied page.
/// </para>
/// <para>
/// <b>Registration.</b> Must be registered as a <em>scoped</em> authorisation handler, because all
/// three of its dependencies are scoped. A singleton registration would capture one request's caller
/// and tenant and serve them to every later request - the classic captive-dependency defect for this
/// component.
/// </para>
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <summary>
    /// The route value naming the module instance a module-scoped decision is about.
    /// </summary>
    /// <remarks>
    /// Spelled exactly as the module routes declare it. Only this one name is accepted: no route in
    /// the application names a module any other way, and admitting a generic fallback such as a bare
    /// identifier segment would let a nested route hand this handler some other entity's key -
    /// deciding a module question from a page's or an account's identifier, which is worse than
    /// refusing.
    /// </remarks>
    private const string ModuleRouteKey = "moduleId";

    /// <summary>
    /// The route value naming the tab - the page abstraction - a tab-scoped decision is about, and the
    /// placement a module-scoped decision is about when the route names one.
    /// </summary>
    private const string TabRouteKey = "tabId";

    /// <summary>
    /// The query value naming one particular placement of a module on a page, used when a module has
    /// been placed on the same page more than once.
    /// </summary>
    private const string TabModuleQueryKey = "tabModuleId";

    private readonly IPermissionService _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly IPortalContextHolder _portalContext;

    /// <summary>
    /// Records why a requirement could not be evaluated, or why an evaluated requirement was not met.
    /// </summary>
    /// <remarks>
    /// A requirement this handler cannot evaluate FAILS CLOSED, and silence about that is the problem the
    /// diagnostics below solve: an unevaluable requirement and a legitimately refused one are the same
    /// 403 to the caller, so without a record the two cannot be told apart from the outside. Nothing here
    /// influences the decision, and no diagnostic names a credential.
    /// </remarks>
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    /// <summary>
    /// Creates the handler over the permission service that decides, the caller it decides about, and
    /// the tenant context used as the last resort for naming the tenant.
    /// </summary>
    /// <param name="permissions">
    /// The application service that answers permission questions. Registered scoped.
    /// </param>
    /// <param name="currentUser">
    /// The caller on whose behalf the current request is being handled. Registered scoped.
    /// </param>
    /// <param name="portalContext">
    /// The tenant context, resolved on demand from the requested host when neither the route nor the
    /// caller names a tenant. Registered scoped.
    /// </param>
    /// <param name="logger">
    /// Records why a requirement could not be evaluated, and why an evaluated requirement was refused.
    /// Diagnostic only: nothing it receives influences the decision.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any dependency is <see langword="null"/>. Failing at activation is deliberate: a
    /// handler missing a collaborator could only ever decline, and a policy that silently declines
    /// every request looks like a permission problem rather than the wiring problem it is.
    /// </exception>
    public PermissionAuthorizationHandler(
        IPermissionService permissions,
        ICurrentUser currentUser,
        IPortalContextHolder portalContext,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(portalContext);
        ArgumentNullException.ThrowIfNull(logger);

        _permissions = permissions;
        _currentUser = currentUser;
        _portalContext = portalContext;
        _logger = logger;
    }

    /// <summary>
    /// Grants the requirement when the caller holds the requested permission on the item the current
    /// request addresses, and otherwise leaves it ungranted.
    /// </summary>
    /// <param name="context">
    /// The authorisation context. Its resource is the current request when the policy was applied to
    /// an endpoint, which is how the addressed item is discovered.
    /// </param>
    /// <param name="requirement">
    /// The requirement being evaluated, naming the permission and the kind of item it is claimed
    /// against.
    /// </param>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // The framework supplies the current request as the resource for a policy applied to an
        // endpoint. Anything else means the addressed item cannot be discovered, so the requirement is
        // left ungranted rather than guessed at. The request is reached this way rather than through
        // an ambient accessor: the framework already hands it over, so taking it from here keeps the
        // handler decidable in isolation and keeps one more piece of ambient state from this layer.
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

        if (ResolveRouteKey(requirement.Scope) is not { } routeKey)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared for a scope this handler can read no route "
                + "key for, so there is nothing to evaluate the permission against.",
                requirement.Scope);
            return;
        }

        // An endpoint carrying an item-scoped policy but exposing no item key is a registration
        // mistake, not a permission decision. Refusing is the safe reading of it: the alternative
        // would be to invent a key and grant against whatever it happened to match.
        if (ReadRouteId(httpContext, routeKey) is not { } scopeId)
        {
            _logger.LogWarning(
                "A {Scope} permission requirement was declared on a route that has no '{RouteKey}' value, "
                + "so there is nothing to evaluate the permission against.",
                requirement.Scope,
                routeKey);
            return;
        }

        // MIGRATION: the legacy evaluation helpers accepted the first assignment row whose key matched
        // and ignored whether that row granted or denied, while the permission-string builders sitting
        // in the same two classes filtered on exactly that flag. The target settles the contradiction
        // beneath this call, where a denial wins over an allowance for the same principal. The
        // divergence is deliberate and lives wholly behind the abstraction - nothing here
        // re-implements, second-guesses or post-filters the answer.
        //
        // MIGRATION: a per-user grant used to be encoded as a bracketed sentinel role built by
        // concatenating the account key into delimited text, then probed through the same role
        // predicate. That encoding is gone. The service takes a first-class nullable account key, so
        // the caller's key is handed over unchanged - and its absence is what reports that the caller
        // holds no account, so it is never coalesced to a stand-in.
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

            // Unreachable: the requirement validates its scope while policies are being registered,
            // and the route key above already declined anything undeclared. It exists because the
            // language requires the switch to be exhaustive, and it denies rather than throwing - a
            // throw at this point would surface as a server fault where a refusal is the honest
            // answer.
            _ => Result<bool>.Success(false)
        };

        // Success is tested before the value is read, and in that order only: reading the value of an
        // unsuccessful outcome throws. An unsuccessful evaluation is a denial and stays silent - its
        // reason is not surfaced to the caller, because a permission probe must not become a channel
        // for describing the permission model, and it is not logged here either, since the request
        // logging middleware already records the outcome against the correlation identifier without
        // putting principal data into a log line.
        //
        // MIGRATION: the legacy pages refused by sending the browser to an access-denied page. Here a
        // refusal is simply the absence of a grant, which the framework renders as a challenge or a
        // refusal depending on whether the caller was identified, and whose body a dedicated result
        // handler owns. Nothing here writes to the reply, sets a status code, adds a header or throws.
        if (decision.IsSuccess && decision.Value)
        {
            context.Succeed(requirement);
            return;
        }

        // Refused, and the reason is recorded because the caller is told nothing beyond the status. A
        // failed evaluation and an honest refusal are separated by the failure code, which is the only
        // thing that distinguishes them once the response has left.
        _logger.LogInformation(
            "A {Scope} {Permission} requirement was not met for scope {ScopeId} in portal {PortalId}: "
            + "{FailureCode}.",
            requirement.Scope,
            requirement.Permission,
            scopeId,
            portalId,
            decision.IsFailure ? decision.Reason?.Code : "not_permitted");
    }

    /// <summary>
    /// Maps a scope onto the single route value that names the item it addresses.
    /// </summary>
    /// <param name="scope">The scope taken from the requirement.</param>
    /// <returns>
    /// The route value name, or <see langword="null"/> when the scope is not a declared member, which
    /// the caller treats as a refusal.
    /// </returns>
    private static string? ResolveRouteKey(PermissionScope scope) => scope switch
    {
        PermissionScope.Module => ModuleRouteKey,
        PermissionScope.Tab => TabRouteKey,
        _ => null
    };

    /// <summary>
    /// Reads one route value from the current request and converts it to an item key.
    /// </summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="routeKey">The route value name to read.</param>
    /// <returns>
    /// The key, or <see langword="null"/> when the route does not carry the value, carries it blank,
    /// or carries something that is not an integer.
    /// </returns>
    /// <remarks>
    /// Every parsed key is returned exactly as it parsed: zero is a legitimate page and module key in
    /// this schema and a negative value is a legitimate portal key, so no value is treated as absent,
    /// clamped or rejected on its magnitude. Absence is reported by <see langword="null"/> and by
    /// nothing else.
    /// </remarks>
    private static int? ReadRouteId(HttpContext httpContext, string routeKey) =>
        ParseId(httpContext.Request.RouteValues[routeKey]?.ToString());

    /// <summary>
    /// Reads one query value from the current request and converts it to an item key.
    /// </summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="queryKey">The query value name to read.</param>
    /// <returns>
    /// The key, or <see langword="null"/> when the query does not carry the value, carries it blank,
    /// carries it more than once, or carries something that is not an integer.
    /// </returns>
    /// <remarks>
    /// A repeated query value is treated as absent rather than resolved by preferring one occurrence:
    /// a request that names two placements has not named one, and picking either would decide the
    /// permission against an item the caller did not unambiguously address.
    /// </remarks>
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
    /// Parsing is culture-invariant because these values are part of a machine-readable address rather
    /// than text a person typed, so a culture that groups digits or writes signs differently must not
    /// change which item is addressed.
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
    /// Names the tenant the decision is about, preferring the addressed tenant over the caller's own
    /// and falling back to the requested host.
    /// </summary>
    /// <param name="httpContext">The current request.</param>
    /// <returns>
    /// The tenant key, or <see langword="null"/> when no tenant can be named, which the caller treats
    /// as a refusal.
    /// </returns>
    /// <remarks>
    /// The host lookup is attempted only when the first two sources are silent, and it is performed
    /// through the tenant context rather than repeated here, so one alias-matching rule serves the
    /// whole application. A failed lookup names no tenant and is reported as such.
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
}
