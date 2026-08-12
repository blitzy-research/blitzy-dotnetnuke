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
    /// <para>
    /// The framework's authorisation context carries no cancellation token, so the token comes from the
    /// evaluator's <see cref="PortalAdministrationEvaluator.RequestAborted"/> instead - the request's own abort
    /// token, reached through the accessor the evaluator already holds. Every read this decision makes is
    /// therefore abandoned when the caller abandons the request, which is the rule the rest of this solution's
    /// I/O follows; passing no token meant a disconnected caller still paid for a completed authorisation
    /// decision that nothing would ever read.
    /// </para>
    /// </remarks>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PortalAdministratorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // MIGRATION: the request's abort token, not CancellationToken.None. See the remarks above.
        bool granted = await _evaluator
            .IsPortalAdministratorAsync(context.User, _evaluator.RequestAborted)
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

        // MIGRATION: the request's abort token, not CancellationToken.None - see
        // PortalAdministrationEvaluator.RequestAborted for why the token is reached through the evaluator.
        bool granted = await _evaluator
            .IsHostAccountAsync(context.User, _evaluator.RequestAborted)
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
/// SEC: OWNERSHIP IS FOUR IDENTITIES AGREEING, NOT TWO. The subject claim must name the route's account AND
/// the token's portal claim must name the route's portal. Testing subject against route alone - which is what
/// this handler previously did - is not ownership of a resource, it is ownership of an ACCOUNT KEY, and in
/// this schema one account key can belong to several portals: <c>dbo.Users</c> is installation-wide and
/// <c>dbo.UserPortals</c> is what associates it with a tenant. A caller who belongs to portals A and B could
/// therefore sign in to A - obtaining a token whose portal claim is A, with A's roles and A's permissions -
/// and then read or update its portal-B profile through a route naming B, because its subject matched. The
/// two tenants' data are not the same data: profile values are keyed by definitions that belong to a portal,
/// and the response is composed from the portal named in the route.
/// </para>
/// <para>
/// The portal claim is therefore not "only where the caller signed in": it is the tenant in which the
/// credential was presented and the tenant whose authority the token carries, so it is exactly the right
/// thing to bind. A token carrying no readable portal claim is refused rather than given the benefit of the
/// doubt - every token this installation mints carries one, so an absent claim is either foreign or a defect.
/// A host account is exempt, because a host account belongs to no tenant; that arm is answered from stored
/// state by the shared evaluator, never from a claim.
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

        // BOTH VALUES MUST BE PRESENT AND MUST AGREE. A route that names no account cannot be self-service and
        // a token with no readable subject cannot own anything, so either absent leaves the requirement to the
        // administrator arm below rather than granting.
        //
        // Note that both -1 and 0 are legitimate identifiers in this schema - portals seed at -1, pages and
        // roles at 0 - so this is a real comparison and not a sentinel test, and absence is the value not being
        // there at all.
        //
        // MIGRATION: THE TENANT COMPARISON IS DELEGATED, AND THAT IS THE FIX RATHER THAN A RELAXATION. This
        // arm used to additionally demand a portalId ROUTE VALUE and compare it with the token's claim itself.
        // The account routes are mounted flat - api/v1/users/{userId} - and name no portal segment at all, so
        // that demand could never be satisfied: every account owner was refused its own self-service routes,
        // including the credential change and the profile completion that a blocking remediation requirement
        // exists to send it to, which left an account that MUST change its password unable to. Reconciling the
        // tenants is exactly what IsTenantBoundAsync does, and it is stricter than the withdrawn check rather
        // than looser: it resolves the target tenant from the route WHEN THE ROUTE NAMES ONE and from the
        // tenant the caller arrived through otherwise, then requires the token's claim to equal it AND the
        // arrival tenant to equal it. An A-issued token therefore still cannot act on the same account's
        // portal-B record - it would have to arrive at B's host, whereupon its own claim disagrees.
        if (subject is { } callerId
            && routeUserId is { } targetId
            && callerId == targetId)
        {
            // SEC: AN ACCOUNT KEY CAN BELONG TO SEVERAL PORTALS, so a subject match alone is not ownership of
            // the addressed record. A host account is exempt and is answered from the store rather than from
            // any claim the token carries, so an account demoted since sign-in loses the exemption
            // immediately.
            // MIGRATION: the request's abort token, not CancellationToken.None - see
            // PortalAdministrationEvaluator.RequestAborted.
            if (await _evaluator.IsTenantBoundAsync(context.User, _evaluator.RequestAborted).ConfigureAwait(false))
            {
                context.Succeed(requirement);
                return;
            }

            // Deliberately NOT returning here. Ownership has been refused for this request, but the
            // administrator arm below is a different question and may still admit the caller - an
            // administrator of the route's portal reaching an account that happens to be their own.
        }

        if (!requirement.AllowPortalAdministrator)
        {
            return;
        }

        // MIGRATION: the request's abort token, not CancellationToken.None - see
        // PortalAdministrationEvaluator.RequestAborted.
        bool granted = await _evaluator
            .IsPortalAdministratorAsync(context.User, _evaluator.RequestAborted)
            .ConfigureAwait(false);

        if (granted)
        {
            context.Succeed(requirement);
        }
    }
}
