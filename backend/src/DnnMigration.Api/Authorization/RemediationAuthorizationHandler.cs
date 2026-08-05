using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Applies the blocking account-remediation gate to every authenticated authorisation attempt.
/// </summary>
/// <remarks>
/// The handler is intentionally not tied to one requirement type. ASP.NET Core invokes every registered
/// <see cref="IAuthorizationHandler"/> for every policy evaluation, so calling
/// <see cref="AuthorizationHandlerContext.Fail()"/> here gates named policies and the fallback policy
/// alike. That is necessary because a fallback policy is not inherited by endpoints that name a policy of
/// their own.
/// </remarks>
internal sealed class RemediationAuthorizationHandler : IAuthorizationHandler
{
    private readonly IAuthService _auth;
    private readonly PortalAdministrationEvaluator _evaluator;

    /// <summary>Initialises a new instance of the handler.</summary>
    /// <param name="auth">Authentication service that re-reads current remediation state.</param>
    /// <param name="evaluator">Shared route-value and principal reader.</param>
    public RemediationAuthorizationHandler(
        IAuthService auth,
        PortalAdministrationEvaluator evaluator)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    /// <inheritdoc />
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (context.Resource is not HttpContext httpContext)
        {
            // Without the request there is no endpoint to read an allowance from and no remediation state to
            // evaluate, so the only safe answer is to fail.
            context.Fail();
            return;
        }

        AllowDuringRemediationAttribute? allowance = httpContext
            .GetEndpoint()?
            .Metadata
            .GetMetadata<AllowDuringRemediationAttribute>();

        // THE ALLOWANCE IS READ BEFORE THE IDENTITY IS, AND THE ORDER IS THE FIX RATHER THAN A TIDY-UP.
        // Authentication lifecycle endpoints must remain reachable even when the account has been deleted or
        // its remediation state cannot be read: their own action reports that condition precisely - /auth/me
        // answers not found - and converting it into a generic authorisation refusal here hides the more
        // precise lifecycle outcome. That was the documented intent all along, but the identity-readability
        // guard below used to run FIRST, so a token whose subject was absent or unreadable was refused with a
        // blanket 403 before this exemption was ever consulted, and a client could not tell "your token names
        // no account" from "you are not allowed here". Reading the allowance first restores the documented
        // behaviour without loosening anything: every endpoint that is NOT an authentication lifecycle
        // endpoint still falls through to the guard and is still refused when the identity cannot be read.
        if (allowance?.Kind == RemediationEndpointKind.Authentication)
        {
            return;
        }

        if (PortalAdministrationEvaluator.TryGetUserId(context.User) is not int userId
            || AuthorizationClaims.ReadTokenPortalId(context.User) is not int portalId)
        {
            // Fail closed. A remediation decision needs both identifiers, and a token that cannot supply
            // them must not be given the benefit of the doubt on a tenant-scoped or account-scoped route.
            context.Fail();
            return;
        }

        Result<AuthenticationRemediationState> evaluated = await _auth
            .EvaluateRemediationAsync(portalId, userId, httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (evaluated.IsFailure)
        {
            context.Fail();
            return;
        }

        AuthenticationRemediationState remediation = evaluated.Value;
        if (!remediation.IsRequired)
        {
            return;
        }

        bool allowed = allowance?.Kind switch
        {
            RemediationEndpointKind.Password =>
                remediation.MustChangePassword && IsAccountOwner(userId),
            RemediationEndpointKind.Profile =>
                remediation.MustUpdateProfile && IsAccountOwner(userId),
            _ => false,
        };

        if (!allowed)
        {
            context.Fail();
        }
    }

    /// <summary>Reports whether the route addresses the authenticated account itself.</summary>
    /// <param name="userId">Authenticated subject identifier.</param>
    /// <returns><see langword="true"/> when the route's user identifier is the same account.</returns>
    private bool IsAccountOwner(int userId)
    {
        return _evaluator.ReadRouteInt(PortalAdministrationEvaluator.UserRouteKey) == userId;
    }
}
