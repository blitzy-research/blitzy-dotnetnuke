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
/// <para>
/// These constants exist so that the three places a refusal can originate - the authorisation middleware,
/// an action that establishes the refusal for itself, and the outcome translator in
/// <see cref="ApiResults"/> - cannot drift apart. Before they existed, the middleware answered with an
/// empty body while an action answered with a problem document, so a client could tell the two apart and
/// had to be written to parse both.
/// </para>
/// <para>
/// <b>Neither detail explains itself, and that is the point.</b> A refusal that named the missing grant,
/// the resource, the policy or the tenant would tell an unauthorised caller which identifiers exist and
/// which privilege to go and acquire. The status code carries everything a legitimate caller needs: 401
/// says "prove who you are", 403 says "you are known and this is not yours". Anything more is an
/// enumeration oracle.
/// </para>
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
/// header and writes nothing; a refusal sets the status code and writes nothing at all. Every action in
/// this API declares <c>ProducesResponseType</c> for 401 and 403, and the interactive description
/// published from those declarations promises a problem document, so an empty body was a documented
/// contract the API did not honour - on the two responses a client is most likely to have to handle
/// programmatically, and the two that carry no other diagnostic.
/// </para>
/// <para>
/// WHY IT WRAPS THE DEFAULT HANDLER RATHER THAN REPLACING IT. The framework's handler does work that must
/// not be lost: it invokes the authentication scheme's challenge, which is what emits
/// <c>WWW-Authenticate: Bearer</c> together with the error and error-description parameters a bearer
/// client reads to tell an expired token from a malformed one; and it honours a policy's own
/// authentication-scheme list. This type therefore lets the default handler decide and respond first, and
/// then, only if nothing has been written yet, adds the body it left out. Re-implementing the challenge
/// would drop the header, and dropping it would break the very clients the body is meant to help.
/// </para>
/// <para>
/// WHY THE STATUS CODE IS READ BACK RATHER THAN INFERRED. <see cref="PolicyAuthorizationResult"/> reports
/// challenged or forbidden, but the status code that reaches the wire is the authentication handler's
/// choice - a challenge can legitimately answer 403 rather than 401, and a handler may answer with a
/// redirect. Reading the code the response actually carries means this type describes what was sent
/// instead of what it assumed would be sent, and leaves anything outside the two codes it knows about
/// completely alone.
/// </para>
/// <para>
/// THE BODY IS BUILT BY THE SHARED FACTORY. <see cref="ProblemDetailsFactory"/> resolves to this
/// solution's own factory, which owns the status vocabulary and attaches the trace identifier, so a
/// refusal produced here is indistinguishable from one produced by the exception handler or by an action.
/// The correlation identifier is not repeated in the body: it travels in its own response header, written
/// by the correlation middleware, which runs before authorisation and has therefore already set it on
/// every response this type touches.
/// </para>
/// <para>
/// Registered as a singleton, holding no per-request state. Both dependencies are themselves singletons.
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
    /// Builds the payload, so that the vocabulary and the trace identifier are this API's own rather than
    /// a second copy declared here.
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
            // A status code this type does not describe. Deliberately untouched rather than coerced into
            // one of the two it knows: guessing a body for an unfamiliar refusal would publish wording
            // that does not match what happened.
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
