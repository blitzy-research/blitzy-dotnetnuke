using System.Globalization;
using System.Security.Claims;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Decides <see cref="PortalAdministratorRequirement"/> against the portal the current request resolved
/// to, by verifying the caller's membership of that portal's own administrator role in the database.
/// </summary>
/// <remarks>
/// <para>
/// THE DECISION IS ANCHORED TO A ROLE KEY AND A PORTAL KEY, NEVER TO A ROLE NAME. The requirement type
/// explains why at length; the short version is that role names are not unique in this schema, so the
/// stock name "Administrators" identifies a different row in every portal and a name-based test is
/// satisfied by an administrator of any of them. This handler reads the administrator role's KEY from the
/// resolved tenant and asks the database whether the caller holds THAT role IN THAT PORTAL.
/// </para>
/// <para>
/// ROLE CLAIMS ARE DELIBERATELY NOT TRUSTED FOR THIS DECISION, even though claims are the conventional
/// answer and would be cheaper. Two reasons, and the second is the stronger. First, a claim carries a
/// name, and names are exactly what cannot distinguish tenants here. Second, legacy membership is
/// time-bounded: the assignment table carries effective and expiry columns and the legacy membership
/// procedure evaluated both on every call, so a membership that has lapsed since sign-in must stop
/// conferring administration. A claim minted once at sign-in cannot express that, because the bounds move
/// relative to the clock while the claim does not. Consulting the authoritative table preserves the
/// legacy behaviour; consulting the claim would quietly extend every administrator's reach to the life of
/// their token.
/// </para>
/// <para>
/// EVERY FAILURE PATH DENIES, AND NONE OF THEM FAULTS. Not authenticated, no usable subject claim, a
/// tenant that cannot be resolved, a portal that is incompletely configured, no matching assignment - each
/// simply leaves the requirement unsucceeded, which the authorisation middleware turns into a 403. In
/// particular an unresolvable tenant DENIES rather than throwing: a policy that faults on missing tenant
/// context produces a 500 that reads like a server defect and, worse, invites the reflex of removing the
/// check to make the error go away. Refusing to decide is the safe answer, and it is the answer here.
/// </para>
/// <para>
/// WHY THIS HANDLER RESOLVES THE TENANT ITSELF INSTEAD OF ASSUMING THE PIPELINE HAS. Because in the
/// mandated pipeline order it runs FIRST. The migration plan fixes that order as exception handler,
/// correlation id, request logging, routing, cross-origin, authentication, authorisation, portal-alias
/// resolution, controllers, health checks - authorisation ahead of alias resolution - and describes the
/// order as non-negotiable, while the security property required of this policy is that no protected
/// request is authorised without a resolved tenant. Both hold simultaneously only because resolution is
/// idempotent: this handler ensures the tenant is resolved before deciding, the middleware ensures the
/// same thing at its mandated position, and whichever runs first does the work while the other observes
/// the identical outcome. Neither is redundant - the middleware covers every request including those no
/// policy protects, and this handler covers the ordering gap - and neither can reach a different answer
/// than the other.
/// </para>
/// <para>
/// THE HOST NAME COMES FROM THE RESOURCE THE FRAMEWORK HANDS THIS HANDLER, not from an ambient accessor.
/// The authorisation middleware passes the request as the authorisation resource, so it is available
/// without this type holding a dependency on request-context ambient state. When the resource is not a
/// request - a policy evaluated directly from a test or a background caller - there is no host name to
/// resolve from, and the requirement is left unsucceeded rather than resolved from some substitute.
/// </para>
/// <para>
/// A SUPER-USER FLAG IS NOT A SHORT CUT HERE. The legacy screens joined a super-user test to the role
/// test, and the canonical site did so defectively - the operands were combined such that a super user was
/// redirected AWAY from the screen rather than past the check. That defect is not reproduced, and neither
/// is the bypass: host-level administration lies outside this migration's scope, and this policy decides
/// portal administration and nothing else.
/// </para>
/// </remarks>
internal sealed class PortalAdministratorAuthorizationHandler
    : AuthorizationHandler<PortalAdministratorRequirement>
{
    private readonly IPortalContextHolder _portalContext;
    private readonly IRoleRepository _roles;
    private readonly IClock _clock;

    /// <summary>
    /// Initialises a new handler.
    /// </summary>
    /// <param name="portalContext">Resolves and holds the tenant for the current request.</param>
    /// <param name="roles">Answers the authoritative portal-scoped membership question.</param>
    /// <param name="clock">
    /// Supplies the instant the membership window is evaluated against. Injected rather than read from the
    /// machine clock so that every check taken while serving one request is taken against one instant, and
    /// so that the boundary condition is testable.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any argument is <see langword="null"/>.
    /// </exception>
    public PortalAdministratorAuthorizationHandler(
        IPortalContextHolder portalContext,
        IRoleRepository roles,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(portalContext);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(clock);

        _portalContext = portalContext;
        _roles = roles;
        _clock = clock;
    }

    /// <summary>
    /// Evaluates the requirement.
    /// </summary>
    /// <param name="context">The authorisation context supplied by the framework.</param>
    /// <param name="requirement">The requirement being evaluated.</param>
    /// <returns>A task that completes when evaluation has finished.</returns>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PortalAdministratorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // An unauthenticated caller cannot be a member of anything. Checked first because it is the
        // cheapest disqualification and because everything below assumes an identity.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (TryGetUserId(context.User) is not { } userId)
        {
            // Authenticated but carrying no subject claim this handler can read as an account key. Denied
            // rather than faulted: a token shaped unexpectedly is a caller problem, not a server one.
            return;
        }

        IPortalContext? portal = await TryGetPortalAsync(context).ConfigureAwait(false);
        if (portal is null)
        {
            // No tenant, so there is no portal whose administration could be granted. Denied.
            return;
        }

        if (portal.AdministratorRoleId is not { } administratorRoleId)
        {
            // The tenant designates no administrator role, so no assignment could name one. Denied
            // rather than faulted, and denied rather than widened to any other role: an unset
            // designation is a configuration gap in the portal, and a gap must not grant.
            return;
        }

        // The authoritative question, asked by keys. The cancellation token is not available on the
        // authorisation context, so none is passed; the query is a single indexed existence check.
        bool isAdministrator = await _roles
            .IsUserInPortalRoleAsync(
                userId,
                administratorRoleId,
                portal.PortalId,
                _clock.UtcNow,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (isAdministrator)
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// Reads the caller's account key from their claims.
    /// </summary>
    /// <remarks>
    /// The framework-standard name-identifier claim is preferred, with the registered JWT subject claim as
    /// a fallback, because whether the inbound token's <c>sub</c> claim has been mapped to the former
    /// depends on host configuration this handler should not have to know about. Nothing else is
    /// consulted, and a value that is not an integer is treated as absent rather than coerced.
    /// </remarks>
    /// <param name="user">The caller.</param>
    /// <returns>The account key, or <see langword="null"/> when no usable claim is present.</returns>
    private static int? TryGetUserId(ClaimsPrincipal user)
    {
        string? raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue(JwtRegisteredSubjectClaim);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
            ? userId
            : null;
    }

    /// <summary>
    /// The registered claim name carrying a token's subject, used when it has not been mapped to the
    /// framework-standard name-identifier claim.
    /// </summary>
    /// <remarks>
    /// Spelled out here rather than taken from a token-handling library so that this type needs no
    /// dependency on one.
    /// </remarks>
    private const string JwtRegisteredSubjectClaim = "sub";

    /// <summary>
    /// Obtains the resolved tenant for this request, resolving it first if necessary.
    /// </summary>
    /// <param name="context">The authorisation context, whose resource carries the request.</param>
    /// <returns>The resolved tenant, or <see langword="null"/> when none could be resolved.</returns>
    private async Task<IPortalContext?> TryGetPortalAsync(AuthorizationHandlerContext context)
    {
        // Already resolved - by the middleware on a path where it ran first, or by a previous policy
        // evaluation during this same request. Nothing to do, and deliberately no second query.
        if (_portalContext.IsResolved)
        {
            return _portalContext.Current;
        }

        // Not yet resolved, so resolve it from the host name the caller used. The request arrives as the
        // authorisation resource; when it is absent there is nothing to resolve from and the caller is
        // denied.
        if (context.Resource is not HttpContext httpContext)
        {
            return null;
        }

        Result outcome = await _portalContext
            .EnsureResolvedAsync(httpContext.Request.Host.Value ?? string.Empty, httpContext.RequestAborted)
            .ConfigureAwait(false);

        // A refusal for any of its three reasons - unknown host, ambiguous host, incompletely configured
        // portal - leaves the caller unauthorised. The reason is not surfaced from here: this handler
        // reports only "not authorised", and diagnosing why the tenant could not be resolved is the alias
        // middleware's job, which logs it for every request including this one.
        return outcome.IsSuccess ? _portalContext.Current : null;
    }
}
