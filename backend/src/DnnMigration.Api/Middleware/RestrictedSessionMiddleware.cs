using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Restricts an authenticated caller with mandatory credential or profile work to remediation endpoints.
/// </summary>
/// <remarks>
/// The decision is re-read from the stores on every authenticated request rather than trusted from a token
/// snapshot. This makes a newly enabled tenant requirement effective immediately and lifts the restriction
/// immediately after the caller completes the work, without issuing a full-authority token first.
/// </remarks>
public sealed class RestrictedSessionMiddleware
{
    /// <summary>The failure code this refusal is reported under, in the API's own problem-type taxonomy.</summary>
    private const string RestrictedSessionCode = "auth.remediation_required";

    /// <summary>The media type every problem document carries, per RFC 7807 section 3.</summary>
    private const string ProblemContentType = "application/problem+json";

    private readonly RequestDelegate _next;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next request stage.</param>
    public RestrictedSessionMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Applies the store-backed remediation boundary to one request.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="currentUser">The authenticated caller projection.</param>
    /// <param name="auth">
    /// It is the same member the authorisation handler consults, which is what keeps one rule in one place
    /// - see the note at the decision below.
    /// </param>
    /// <param name="problemDetailsFactory">Creates the standard problem response.</param>
    /// <returns>A task that completes after the request or refusal has been written.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser currentUser,
        IAuthService auth,
        ProblemDetailsFactory problemDetailsFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);

        Endpoint? endpoint = context.GetEndpoint();
        if (!currentUser.IsAuthenticated
            || endpoint is null
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<RemediationAllowedAttribute>() is not null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (currentUser.PortalId is not int portalId || currentUser.UserId is not int userId)
        {
            await WriteRefusalAsync(
                context,
                problemDetailsFactory,
                "The authenticated session is missing its tenant or account identity.")
                .ConfigureAwait(false);
            return;
        }

        Result<AuthenticationRemediationState> evaluated = await auth
            .EvaluateRemediationAsync(portalId, userId, context.RequestAborted)
            .ConfigureAwait(false);

        if (evaluated.IsFailure)
        {
            await WriteRefusalAsync(
                context,
                problemDetailsFactory,
                "The session's remediation state could not be verified.")
                .ConfigureAwait(false);
            return;
        }

        AuthenticationRemediationState remediation = evaluated.Value;
        bool mustChangePassword = remediation.MustChangePassword;
        bool mustCompleteProfile = remediation.MustUpdateProfile;

        if (!remediation.IsRequired)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string detail = mustChangePassword && mustCompleteProfile
            ? "Change the account credential and complete the required profile fields before continuing."
            : mustChangePassword
                ? "Change the account credential before continuing."
                : "Complete the required profile fields before continuing.";

        await WriteRefusalAsync(context, problemDetailsFactory, detail).ConfigureAwait(false);
    }

    /// <summary>Writes one bounded RFC 7807 refusal.</summary>
    /// <param name="context">The request being refused.</param>
    /// <param name="factory">The registered problem-details factory.</param>
    /// <param name="detail">The fixed remediation instruction.</param>
    /// <returns>A task that completes after the response is written.</returns>
    private static Task WriteRefusalAsync(
        HttpContext context,
        ProblemDetailsFactory factory,
        string detail)
    {
        ProblemDetails problem = factory.CreateProblemDetails(
            context,
            StatusCodes.Status403Forbidden,
            title: "Mandatory account remediation is required.",
            type: ApiResults.BuildProblemType(RestrictedSessionCode),
            detail: detail);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = ProblemContentType;

        return context.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            options: null,
            contentType: ProblemContentType,
            cancellationToken: context.RequestAborted);
    }
}
