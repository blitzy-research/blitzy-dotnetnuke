using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>Applies the blocking account-remediation gate to every authenticated authorisation attempt.</summary>
/// <remarks>
/// The handler is intentionally not tied to one requirement type. ASP.NET Core invokes every registered
/// <see cref="IAuthorizationHandler"/> for every policy evaluation, so calling <see
/// cref="AuthorizationHandlerContext.Fail()"/> here gates named policies and the fallback policy alike.
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
            context.Fail();
            return;
        }

        AllowDuringRemediationAttribute? allowance = httpContext
            .GetEndpoint()?
            .Metadata
            .GetMetadata<AllowDuringRemediationAttribute>();

        if (allowance?.Kind == RemediationEndpointKind.Authentication)
        {
            return;
        }

        if (PortalAdministrationEvaluator.TryGetUserId(context.User) is not int userId
            || AuthorizationClaims.ReadTokenPortalId(context.User) is not int portalId)
        {
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
