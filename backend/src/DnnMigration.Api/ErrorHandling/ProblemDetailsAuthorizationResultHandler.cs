using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DnnMigration.Api.ErrorHandling;

/// <summary>
/// The fixed wording and problem types used by every authentication and authorisation refusal this API
/// produces, wherever that refusal is decided.
/// </summary>
/// <remarks>
/// These constants exist so that the three places a refusal can originate - the authorisation middleware,
/// an action that establishes the refusal for itself, and the outcome translator in <see
/// cref="ApiResults"/> - cannot drift apart.
/// </remarks>
internal static class AuthorizationProblemDetails
{
    /// <summary>Detail reported when the caller has not proved who they are.</summary>
    internal const string UnauthorizedDetail = "Authentication is required to reach this resource.";

    /// <summary>Detail reported when a known caller may not perform the operation.</summary>
    internal const string ForbiddenDetail =
        "The authenticated caller is not permitted to perform this operation.";

    /// <summary>Failure code carried as the problem type by an authentication refusal.</summary>
    internal const string UnauthenticatedCode = "auth.unauthenticated";

    /// <summary>Failure code carried as the problem type by an authorisation refusal.</summary>
    internal const string UnauthorizedCode = "auth.not_permitted";
}

/// <summary>
/// Gives the authorisation middleware's own 401 and 403 responses the same RFC 7807 body every other
/// failure in this API carries.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE HAS TO EXIST. The authorisation middleware does not produce a response body. A challenge
/// is delegated to the authentication scheme, which sets the status code and the <c>WWW-Authenticate</c>
/// header and writes nothing; a refusal sets the status code and writes nothing at all.
/// </para>
/// <para>
/// WHY IT WRAPS THE DEFAULT HANDLER RATHER THAN REPLACING IT. The framework's handler does work that must
/// not be lost: it invokes the authentication scheme's challenge, which is what emits <c>WWW-Authenticate:
/// Bearer</c> together with the error and error-description parameters a bearer client reads to tell an
/// expired token from a malformed one; and it honours a policy's own authentication-scheme list.
/// </para>
/// </remarks>
internal sealed class ProblemDetailsAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>The media type an RFC 7807 payload is served as.</summary>
    private const string ProblemContentType = "application/problem+json";

    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();
    private readonly ProblemDetailsFactory _problemDetailsFactory;

    /// <summary>
    /// Initialises a new instance of the <see cref="ProblemDetailsAuthorizationResultHandler"/> class.
    /// </summary>
    /// <param name="problemDetailsFactory">
    /// Builds the payload, so that the vocabulary and the trace identifier are this API's own rather than a
    /// second copy declared here.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="problemDetailsFactory"/> is <see langword="null"/>.
    /// </exception>
    public ProblemDetailsAuthorizationResultHandler(ProblemDetailsFactory problemDetailsFactory)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);

        _problemDetailsFactory = problemDetailsFactory;
    }

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        await _defaultHandler
            .HandleAsync(next, context, policy, authorizeResult)
            .ConfigureAwait(false);

        if (authorizeResult.Succeeded)
        {
            // The request was authorised and has been served by the rest of the pipeline. There is no
            // refusal to describe, and the response belongs to the endpoint.
            return;
        }

        if (context.Response.HasStarted)
        {
            // Something has already begun writing - a custom authentication event, or a redirect - so the
            // headers are gone and a body cannot be added. Leaving it alone is the only safe answer.
            return;
        }

        (string detail, string code)? vocabulary = context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => (
                AuthorizationProblemDetails.UnauthorizedDetail,
                AuthorizationProblemDetails.UnauthenticatedCode),
            StatusCodes.Status403Forbidden => (
                AuthorizationProblemDetails.ForbiddenDetail,
                AuthorizationProblemDetails.UnauthorizedCode),
            _ => null,
        };

        if (vocabulary is not { } refusal)
        {
            return;
        }

        ProblemDetails problem = _problemDetailsFactory.CreateProblemDetails(
            context,
            statusCode: context.Response.StatusCode,
            detail: refusal.detail,
            type: ApiResults.BuildProblemType(refusal.code));

        context.Response.ContentType = ProblemContentType;

        await context.Response
            .WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: ProblemContentType)
            .ConfigureAwait(false);
    }
}
