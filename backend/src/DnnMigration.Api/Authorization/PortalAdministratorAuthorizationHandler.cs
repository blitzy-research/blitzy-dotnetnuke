using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Decides <see cref="PortalAdministratorRequirement"/> against the portal the request ACTS ON, by
/// verifying the caller's membership of that portal's own administrator role in the database.
/// </summary>
/// <remarks>
/// <para>
/// THE DECISION IS ANCHORED TO A ROLE KEY AND A PORTAL KEY, NEVER TO A ROLE NAME. The requirement type
/// explains why at length; the short version is that role names are not unique in this schema, so the stock
/// name "Administrators" identifies a different row in every portal and a name-based test is satisfied by
/// an administrator of any of them.
/// </para>
/// <para>
/// ROLE CLAIMS ARE DELIBERATELY NOT TRUSTED, even though claims are the conventional answer and would be
/// cheaper. A claim carries a name, and names are exactly what cannot distinguish tenants here; and legacy
/// membership is time-bounded, so a membership that has lapsed since sign-in must stop conferring
/// administration, which a claim minted once cannot express.
/// </para>
/// </remarks>
internal sealed class PortalAdministratorAuthorizationHandler
    : AuthorizationHandler<PortalAdministratorRequirement>
{
    private readonly PortalAdministrationEvaluator _evaluator;

    /// <summary>Initialises a new handler.</summary>
    /// <param name="evaluator">Answers whether the caller administers the portal the request acts on.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="evaluator"/> is <see langword="null"/>.
    /// </exception>
    public PortalAdministratorAuthorizationHandler(PortalAdministrationEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        _evaluator = evaluator;
    }

    /// <summary>Evaluates the requirement.</summary>
    /// <param name="context">The authorisation context supplied by the framework.</param>
    /// <param name="requirement">The requirement being evaluated.</param>
    /// <returns>A task that completes when evaluation has finished.</returns>
    /// <remarks>
    /// The framework's authorisation context carries no cancellation token, so the token comes from the
    /// evaluator's <see cref="PortalAdministrationEvaluator.RequestAborted"/> instead - the request's own
    /// abort token, reached through the accessor the evaluator already holds.
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
/// identifier.
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

    /// <summary>Initialises a new handler.</summary>
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
/// The portal claim is therefore not "only where the caller signed in": it is the tenant in which the
/// credential was presented and the tenant whose authority the token carries, so it is exactly the right
/// thing to bind.
/// </para>
/// <para>
/// THE ADMINISTRATOR ARM IS THE SAME QUESTION THE PORTAL POLICY ASKS, delegated to the shared evaluator
/// rather than re-implemented, so a caller cannot be an administrator for one family of endpoints and not
/// for another.
/// </para>
/// </remarks>
internal sealed class AccountOwnerAuthorizationHandler : AuthorizationHandler<AccountOwnerRequirement>
{
    private readonly PortalAdministrationEvaluator _evaluator;

    /// <summary>Initialises a new handler.</summary>
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

        if (subject is { } callerId
            && routeUserId is { } targetId
            && callerId == targetId)
        {
            // SEC: AN ACCOUNT KEY CAN BELONG TO SEVERAL PORTALS, so a subject match alone is not ownership
            // of the addressed record.
            if (await _evaluator.IsTenantBoundAsync(context.User, _evaluator.RequestAborted).ConfigureAwait(false))
            {
                context.Succeed(requirement);
                return;
            }
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
