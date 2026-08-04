using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Decides <see cref="AccountAccessRequirement"/> by admitting the addressed account itself, or an
/// administrator of the tenant the route addresses.
/// </summary>
/// <remarks>
/// <para>
/// THE SELF TEST COMPARES BOTH KEYS, NOT JUST THE ACCOUNT KEY. The caller's token names an account and a
/// tenant; the route names an account and a tenant. Self-service requires all four to agree in pairs.
/// Comparing only the account keys would admit a caller whose token was minted for portal A to operate on
/// its own account key inside portal B - and because account keys are installation-wide surrogates while
/// membership is per-portal, that is a real cross-tenant reach rather than a theoretical one.
/// </para>
/// <para>
/// A ROUTE THAT NAMES NO ACCOUNT CANNOT SATISFY THE SELF TEST, and is therefore left to the administrative
/// arm alone. That is the safe direction: a route without an account key is not addressing "the caller's own
/// account", so treating an absent key as a match would turn every such route into a self-service one.
/// </para>
/// <para>
/// THE ADMINISTRATIVE ARM IS THE SAME DECISION THE PORTAL POLICY MAKES, taken through the same evaluator, so
/// the two cannot diverge. A host account is admitted first, because it belongs to no tenant and so satisfies
/// neither the self test nor any tenant comparison.
/// </para>
/// <para>
/// FAILURE IS SILENCE. Every path that does not establish one of the three grounds simply leaves the
/// requirement unsucceeded, which the authorisation middleware renders as a 401 or a 403. Nothing here throws,
/// because a handler that faults reports a server defect where the correct answer was a refusal.
/// </para>
/// </remarks>
internal sealed class AccountAccessAuthorizationHandler : AuthorizationHandler<AccountAccessRequirement>
{
    /// <summary>The route value naming the account a request is about.</summary>
    public const string UserRouteKey = "userId";

    private readonly TenantAdministrationEvaluator _administration;

    /// <summary>
    /// Initialises a new handler.
    /// </summary>
    /// <param name="administration">
    /// Answers the two administrative questions, shared with the portal and host handlers so that all three
    /// agree by construction.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="administration"/> is <see langword="null"/>.
    /// </exception>
    public AccountAccessAuthorizationHandler(TenantAdministrationEvaluator administration)
    {
        ArgumentNullException.ThrowIfNull(administration);

        _administration = administration;
    }

    /// <summary>
    /// Evaluates the requirement.
    /// </summary>
    /// <param name="context">The authorisation context supplied by the framework.</param>
    /// <param name="requirement">The requirement being evaluated.</param>
    /// <returns>A task that completes when evaluation has finished.</returns>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AccountAccessRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (AuthorizationClaims.ReadUserId(context.User) is not { } callerUserId)
        {
            return;
        }

        var request = context.Resource as HttpContext;

        // The self test. Both the account and the tenant must agree between the token and the route, for the
        // reason set out in the type remarks. Note that both -1 and 0 are legitimate identifiers in this
        // schema, so the comparisons are real comparisons rather than sentinel tests.
        if (request is not null
            && AuthorizationClaims.ReadRouteInt(request, UserRouteKey) is { } routeUserId
            && routeUserId == callerUserId
            && AuthorizationClaims.ReadRouteInt(request, AuthorizationClaims.PortalRouteKey)
                is { } routePortalId
            && AuthorizationClaims.ReadTokenPortalId(context.User) is { } tokenPortalId
            && routePortalId == tokenPortalId)
        {
            context.Succeed(requirement);
            return;
        }

        // The cancellation token is not available on the authorisation context, so none is passed.
        if (await _administration.IsHostAccountAsync(callerUserId, CancellationToken.None).ConfigureAwait(false))
        {
            context.Succeed(requirement);
            return;
        }

        bool administers = await _administration
            .AdministersRequestTenantAsync(context.User, callerUserId, request, CancellationToken.None)
            .ConfigureAwait(false);

        if (administers)
        {
            context.Succeed(requirement);
        }
    }
}
