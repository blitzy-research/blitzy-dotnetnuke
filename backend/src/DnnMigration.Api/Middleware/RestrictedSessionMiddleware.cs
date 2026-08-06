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
    /// <summary>
    /// The failure code this refusal is reported under, in the API's own problem-type taxonomy.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this replaces a hard-coded <c>https://httpstatuses.com/403</c>. That URI named a
    /// THIRD-PARTY host, did not resolve, and said only what the status line already said, so a client
    /// could not distinguish a mandatory-remediation refusal from any other 403 without parsing prose -
    /// while the ordinary authorisation refusal on the very same status already carried this API's own
    /// <c>urn:dnnmigration:error:auth.not_permitted</c>. One taxonomy, built by one method, for every
    /// problem document.
    /// </remarks>
    private const string RestrictedSessionCode = "auth.remediation_required";

    /// <summary>
    /// The media type every problem document carries, per RFC 7807 section 3.
    /// </summary>
    /// <remarks>
    /// MIGRATION: previously this refusal was written with <c>WriteAsJsonAsync</c>, which labels the body
    /// <c>application/json</c>. A client cannot then select a problem parser from the media type, which is
    /// the reason the specification registers one.
    /// </remarks>
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
    /// The authentication service that owns the remediation rule. It is the same member the authorisation
    /// handler consults, which is what keeps one rule in one place - see the note at the decision below.
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

        // MIGRATION: AN ANONYMOUSLY REACHABLE ENDPOINT IS EXEMPT, and this is a correction rather than a
        // convenience. An endpoint that answers with no credential at all cannot meaningfully be
        // restricted by the PRESENCE of one: doing so makes the response depend on a credential the
        // endpoint does not consult, so the same request succeeds or is refused according to whether the
        // caller happened to attach a bearer token. The liveness, readiness and aggregate health probes
        // are exactly that kind of endpoint - each is mapped with AllowAnonymous, and the pipeline
        // comment on their stage states that they are anonymous and must remain so - yet a caller holding
        // a token that required credential or profile remediation received 403 from all three. A container
        // orchestrator that forwards a token, or an operator probing from an authenticated session, would
        // read the service as unhealthy while it was serving every other request correctly, and the
        // compose health condition that gates the frontend container depends on that probe.
        //
        // Tested through IAllowAnonymous metadata rather than by matching paths, so the exemption follows
        // the endpoint's own declaration and cannot fall out of step with it: an endpoint that stops being
        // anonymous stops being exempt in the same edit. This covers the credential endpoints uniformly
        // for the same reason - signing in cannot require an unrestricted session. Endpoints that are
        // authenticated but must stay reachable while remediation is outstanding continue to declare
        // RemediationAllowedAttribute, which is a different statement and remains necessary.
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

        // MIGRATION: SEC-F2. ONE REMEDIATION RULE, ONE HOME, AND THIS IS THE CALL THAT MAKES IT SO.
        // This stage used to compose the decision itself from two account-service reads - the portal-scoped
        // account projection for the forced-credential flag, and the profile-completion probe - which was a
        // SECOND implementation of a rule the authorisation handler already asked
        // IAuthService.EvaluateRemediationAsync for. The two agreed until they did not, and the case where
        // they disagreed was a host account: the account read is scoped to the addressed tenant, and a host
        // account belongs to no tenant, so for any portal in which it holds no dbo.UserPortals row the read
        // answered nothing, the guard below read that as "state could not be verified", and the installation
        // operator was refused 403 on EVERY authenticated endpoint of that tenant - including the tenants
        // this API had just created, since portal creation provisions no host membership row. The legacy host
        // account was installation-wide and administered every portal without a membership row, so refusing
        // it was a parity break as well as an operability one.
        //
        // EvaluateRemediationAsync is the rule: it resolves a host account without a portal scope, exempts a
        // host account from profile completion, and still honours an explicit forced-credential flag for one.
        // Delegating to it removes the duplicate rather than patching it, so the middleware and the
        // authorisation handler cannot drift apart again.
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
