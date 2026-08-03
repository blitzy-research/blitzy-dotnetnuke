using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Decides <see cref="PortalAdministratorRequirement"/> against the portal the request ACTS ON, by
/// verifying the caller's membership of that portal's own administrator role in the database.
/// </summary>
/// <remarks>
/// <para>
/// THE PORTAL IS THE ONE THE ROUTE NAMES, NOT THE ONE THE HOST NAME RESOLVED TO. This is the whole of the
/// decision and it is what this handler previously got wrong. It proved administration of the tenant the
/// caller ARRIVED through, while every controller it gates operates on an identifier taken from the route -
/// so an administrator of portal A, arriving on portal A's host name, passed the policy and then read or
/// mutated portal B's users, roles, role groups, profile definitions or settings. The route segment names
/// the resource; the host name names only the door the caller came in by. The rule applied now - the route
/// wins, falling back to the resolved tenant only where the route names no portal - is the identical rule
/// <see cref="PermissionAuthorizationHandler"/> already applied, so the two components can no longer
/// disagree about which portal a request is about.
/// </para>
/// <para>
/// THE DECISION IS ANCHORED TO A ROLE KEY AND A PORTAL KEY, NEVER TO A ROLE NAME. The requirement type
/// explains why at length; the short version is that role names are not unique in this schema, so the stock
/// name "Administrators" identifies a different row in every portal and a name-based test is satisfied by an
/// administrator of any of them.
/// </para>
/// <para>
/// A HOST ACCOUNT SATISFIES THIS REQUIREMENT. That is a correction to an earlier reading, and the evidence
/// is the rest of the solution rather than a preference: the permission service answers every permission
/// question affirmatively for a host account before reading a grant, and the portal service gates the
/// hosting charge, the quotas, the site-log retention period and the expiry date on the caller BEING a host
/// account - a rule that is only meaningful if a host account can reach a portal it does not itself
/// administer. Refusing one here would also make a host unable to administer the portal it had just created,
/// because a new portal's administrator role is held by the account created alongside it. The evaluator
/// reads the flag from the store rather than from the token, so a revoked host account loses reach
/// immediately rather than at token expiry.
/// </para>
/// <para>
/// ROLE CLAIMS ARE DELIBERATELY NOT TRUSTED, even though claims are the conventional answer and would be
/// cheaper. A claim carries a name, and names are exactly what cannot distinguish tenants here; and legacy
/// membership is time-bounded, so a membership that has lapsed since sign-in must stop conferring
/// administration, which a claim minted once cannot express. Consulting the authoritative table preserves
/// the legacy behaviour; consulting the claim would quietly extend every administrator's reach to the life
/// of their token.
/// </para>
/// <para>
/// EVERY FAILURE PATH DENIES, AND NONE OF THEM FAULTS. Not authenticated, no usable subject claim, no portal
/// to name, a portal that does not exist, a portal that is incompletely configured, no matching assignment -
/// each simply leaves the requirement unsucceeded, which the authorisation middleware turns into a 403
/// carrying the shared problem document. In particular an unresolvable tenant DENIES rather than throwing: a
/// policy that faults on missing tenant context produces a 500 that reads like a server defect and, worse,
/// invites the reflex of removing the check to make the error go away.
/// </para>
/// <para>
/// The decision itself lives in <see cref="PortalAdministrationEvaluator"/> so that the account-owner policy
/// beside this one asks the identical question rather than a second implementation of it. This handler is
/// the adapter between that answer and the framework's requirement protocol, and holds no rule of its own.
/// </para>
/// </remarks>
internal sealed class PortalAdministratorAuthorizationHandler
    : AuthorizationHandler<PortalAdministratorRequirement>
{
    private readonly PortalAdministrationEvaluator _evaluator;

    /// <summary>
    /// Initialises a new handler.
    /// </summary>
    /// <param name="evaluator">Answers whether the caller administers the portal the request acts on.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="evaluator"/> is <see langword="null"/>.
    /// </exception>
    public PortalAdministratorAuthorizationHandler(PortalAdministrationEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

    /// <summary>
    /// Evaluates the requirement.
    /// </summary>
    /// <param name="context">The authorisation context supplied by the framework.</param>
    /// <param name="requirement">The requirement being evaluated.</param>
    /// <returns>A task that completes when evaluation has finished.</returns>
    /// <remarks>
    /// No cancellation token is available on the authorisation context, so none is passed. The reads the
    /// evaluator performs are short and indexed, and abandoning them early would only trade a completed
    /// decision for an abandoned one.
    /// </remarks>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PortalAdministratorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        bool granted = await _evaluator
            .IsPortalAdministratorAsync(context.User, CancellationToken.None)
            .ConfigureAwait(false);

        if (granted)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Decides <see cref="HostAdministratorRequirement"/> for the operations that address no single portal at
/// all.
/// </summary>
/// <remarks>
/// <para>
/// WHY A SEPARATE REQUIREMENT EXISTS. A handful of operations carry no portal binding of any kind: the
/// portal collection itself, portal creation, and the alias resources addressed by their own global
/// identifier. Gating those on portal administration granted an ordinary administrator of ONE tenant the
/// ability to enumerate every tenant, create new ones, and read or delete another tenant's alias by
/// guessing its identifier - because with no portal in the route the portal-administrator requirement fell
/// back to the tenant the caller arrived through, which they do administer. The requirement was satisfied
/// truthfully and answered the wrong question.
/// </para>
/// <para>
/// WHY THIS IS NOT THE EXCLUDED HOST ADMINISTRATION FEATURE. The migration plan excludes the host-level
/// ADMINISTRATION SCREENS - the super-user console and everything reachable only from it. It does not
/// exclude the concept: <c>Users.IsSuperUser</c> is a mapped column on the user entity, the token service
/// already emits the flag as a claim, the current-user abstraction already exposes it, and the permission
/// service and the portal service both already branch on it. This requirement consumes what is already
/// there; it introduces no screen and no new capability.
/// </para>
/// <para>
/// The flag is read from the store rather than from the claim, for the same reason the sibling requirement
/// reads membership from the store: a revoked host account must lose reach immediately rather than at token
/// expiry.
/// </para>
/// </remarks>
internal sealed class HostAdministratorAuthorizationHandler
    : AuthorizationHandler<HostAdministratorRequirement>
{
    private readonly PortalAdministrationEvaluator _evaluator;

    /// <summary>
    /// Initialises a new handler.
    /// </summary>
    /// <param name="evaluator">Answers whether the caller is a host account.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="evaluator"/> is <see langword="null"/>.
    /// </exception>
    public HostAdministratorAuthorizationHandler(PortalAdministrationEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HostAdministratorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        bool granted = await _evaluator
            .IsHostAccountAsync(context.User, CancellationToken.None)
            .ConfigureAwait(false);

        if (granted)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Decides <see cref="AccountOwnerRequirement"/>: the caller is the account the route names, or - where the
/// requirement permits it - an administrator of the portal the route names.
/// </summary>
/// <remarks>
/// <para>
/// WHY SELF-SERVICE NEEDS ITS OWN REQUIREMENT. The account resources are the one family where two entirely
/// different callers are legitimate: the account holder, reaching their own profile or changing their own
/// credential, and an administrator of the account's portal, doing so on their behalf. Gating them on
/// authentication alone - which is what they previously carried - let ANY bearer token name any portal and
/// any account in the route and read that account's personal data or overwrite its credential. Gating them
/// on portal administration alone would remove self-service entirely.
/// </para>
/// <para>
/// THE OWNERSHIP TEST IS SUBJECT AGAINST ROUTE, and it is an equality test on integers rather than anything
/// cleverer. The subject claim is the account key the token was issued for; the route value is the account
/// the request acts on. Nothing else can stand in for either - not the account name, which is not unique
/// across portals, and not the token's portal claim, which says only where the caller signed in.
/// </para>
/// <para>
/// THE ADMINISTRATOR ARM IS THE SAME QUESTION THE PORTAL POLICY ASKS, delegated to the shared evaluator
/// rather than re-implemented, so a caller cannot be an administrator for one family of endpoints and not
/// for another. Where <see cref="AccountOwnerRequirement.AllowPortalAdministrator"/> is false the arm is not
/// consulted at all: a credential change must be made by its owner, presenting the current credential, and
/// an administrator who needs to intervene uses the separate reset operation, which is gated on portal
/// administration and audited as its own act.
/// </para>
/// </remarks>
internal sealed class AccountOwnerAuthorizationHandler : AuthorizationHandler<AccountOwnerRequirement>
{
    private readonly PortalAdministrationEvaluator _evaluator;

    /// <summary>
    /// Initialises a new handler.
    /// </summary>
    /// <param name="evaluator">Reads the route, the subject claim and the administration question.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="evaluator"/> is <see langword="null"/>.
    /// </exception>
    public AccountOwnerAuthorizationHandler(PortalAdministrationEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AccountOwnerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        int? subject = PortalAdministrationEvaluator.TryGetUserId(context.User);
        int? routeUserId = _evaluator.ReadRouteInt(PortalAdministrationEvaluator.UserRouteKey);

        // Both must be present for ownership to be provable. A route that names no account cannot be
        // self-service, and a token with no readable subject cannot own anything.
        if (subject is { } callerId && routeUserId is { } targetId && callerId == targetId)
        {
            context.Succeed(requirement);
            return;
        }

        if (!requirement.AllowPortalAdministrator)
        {
            return;
        }

        bool granted = await _evaluator
            .IsPortalAdministratorAsync(context.User, CancellationToken.None)
            .ConfigureAwait(false);

        if (granted)
        {
            context.Succeed(requirement);
        }
    }
}
