using DnnMigration.Api.Authorization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
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
    private const string RestrictedSessionType = "https://httpstatuses.com/403";
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
    /// <param name="users">The account service that owns remediation facts.</param>
    /// <param name="problemDetailsFactory">Creates the standard problem response.</param>
    /// <returns>A task that completes after the request or refusal has been written.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser currentUser,
        IUserService users,
        ProblemDetailsFactory problemDetailsFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);

        Endpoint? endpoint = context.GetEndpoint();
        if (!currentUser.IsAuthenticated
            || endpoint is null
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

        Result<UserDetailDto?> account = await users
            .GetUserAsync(portalId, userId, context.RequestAborted)
            .ConfigureAwait(false);
        Result<bool> profile = await users
            .RequiresProfileCompletionAsync(portalId, userId, context.RequestAborted)
            .ConfigureAwait(false);

        bool mustChangePassword = account.IsSuccess && account.Value?.MustChangePassword == true;
        bool mustCompleteProfile = profile.IsSuccess && profile.Value;

        if (account.IsFailure || profile.IsFailure || account.Value is null)
        {
            await WriteRefusalAsync(
                context,
                problemDetailsFactory,
                "The session's remediation state could not be verified.")
                .ConfigureAwait(false);
            return;
        }

        if (!mustChangePassword && !mustCompleteProfile)
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
            type: RestrictedSessionType,
            detail: detail);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }
}
