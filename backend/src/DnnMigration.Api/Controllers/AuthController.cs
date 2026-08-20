using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The authentication surface: sign in, exchange a refresh token, revoke one, and describe the caller.
/// </summary>
/// <remarks>
/// This controller is CLOSED AT FOUR OPERATIONS - sign in, refresh, revoke, and describe the caller.
/// Changing a credential and administratively replacing one both belong to the account resource, where the
/// account being altered is named by the route and the caller's entitlement to alter it can be judged;
/// neither is an authentication concern and neither is exposed here.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[AllowDuringRemediation(RemediationEndpointKind.Authentication)]
// Every operation here names its tenant by a means other than the host name, and sign-in MUST stay
// reachable from a host that resolves to no portal - it is how an operator obtains the session that repairs
// the alias configuration.
[TenantOptional(
    "Sign-in accepts an explicit portal identifier and must remain reachable from an unconfigured host so an "
    + "operator can obtain a session; the other three operations take their portal from the bearer token.")]
// THE COMPENSATING CONTROL FOR THE DELETED CAPTCHA IS DECLARED PER OPERATION, AND THE SPLIT IS A SECURITY
// FIX RATHER THAN A TIDY-UP. One controller-wide declaration put all four operations into ONE budget, and
// three of them handle a credential while the fourth - the caller-description read - does not.
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private const int MaximumUserAgentCharacters = 512;
    /// <summary>The query-string name that names the portal being signed in to.</summary>
    /// <remarks>
    /// A fallback, not the primary source. The request host normally identifies the portal, exactly as it
    /// did in the legacy application, where the alias determined the tenant before the page ever ran.
    /// </remarks>
    public const string PortalQueryParameterName = "portalId";

    private readonly IAuthService _auth;
    private readonly IPortalContextHolder _portalContext;

    // NOTHING ELSE IS INJECTED EITHER. The authentication service is the single seam: comparing a
    // credential, deciding whether a stored value must be replaced at a higher cost, minting and rotating
    // the pair, counting failed attempts, applying the automatic-unlock window, checking a verification
    // code and composing the caller's description all happen behind it.
    /// <summary>Initialises a new instance of the <see cref="AuthController"/> class.</summary>
    /// <param name="auth">The authentication service.</param>
    /// <param name="portalContext">
    /// Holds the tenant the alias-resolution middleware resolved from the request host, if any.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AuthController(IAuthService auth, IPortalContextHolder portalContext)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Exchanges a credential for an access token and a refresh token.</summary>
    /// <param name="request">The credential, and the account-verification code when one is being answered.</param>
    /// <param name="portalId">The portal being signed in to.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The token pair and the caller's own details, or a refusal.</returns>
    /// <remarks>
    /// The reason is that authority is exactly the kind of fact that must not be cached in a client from a
    /// credential exchange.
    /// </remarks>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitingExtensions.AuthenticationPolicyName)]
    [CredentialEndpoint]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> LoginAsync(
        [FromBody] LoginRequest request,
        [FromQuery(Name = PortalQueryParameterName)] int? portalId,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE, and an absent body is refused before this
        // method runs: a body parameter of a non-nullable type is required, and automatic model-state
        // validation answers with the same problem document this method would have built by hand.
        int? resolvedPortalId = _portalContext.IsResolved
            ? _portalContext.Current.PortalId
            : portalId;

        // A NULL TEST, NEVER A VALUE TEST. The tenant identity column is seeded at minus one, so minus one
        // and zero are both real portals, and minus one is simultaneously the legacy absent-integer
        // sentinel and the "all users" pseudo-role.
        if (resolvedPortalId is null)
        {
            ModelState.AddModelError(
                PortalQueryParameterName,
                "The request host does not correspond to a configured portal alias, so the portal being "
                + "signed in to must be stated explicitly.");

            return ValidationProblem(ModelState);
        }

        // Assigned here rather than accepted from the body: the member is excluded from serialisation, so
        // this assignment is the only way the tenant can reach the service, and the value written is always
        // the one the transport resolved.
        request.PortalId = resolvedPortalId.Value;

        Result<LoginResponse> outcome = await _auth
            .LoginAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // The legacy screen decided authentication with `authenticated = (loginStatus <>
        // UserLoginStatus.LOGIN_FAILURE)` at Login.ascx.vb:L187.
        return this.Complete(outcome);
    }

    /// <summary>Exchanges a refresh token for a new token pair.</summary>
    /// <param name="request">The refresh token.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A new token pair, or a refusal.</returns>
    /// <remarks>
    /// The refresh token is single use. Presenting one that has already been exchanged is treated as a
    /// replay and withdraws the account's whole set of refresh tokens rather than only the presented one.
    /// </remarks>
    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitingExtensions.AuthenticationPolicyName)]
    // Exchanges a token derived from a credential, which is a credential-equivalent secret.
    [CredentialEndpoint]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        request.ClientBinding = BuildClientBinding(HttpContext);

        Result<LoginResponse> outcome = await _auth
            .RefreshAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Builds the server-observed client fingerprint used only for refresh concurrency grace.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A fixed-width hexadecimal SHA-256 fingerprint.</returns>
    private static string BuildClientBinding(HttpContext context)
    {
        string address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > MaximumUserAgentCharacters)
        {
            userAgent = userAgent[..MaximumUserAgentCharacters];
        }

        byte[] source = Encoding.UTF8.GetBytes(string.Concat(address, "\n", userAgent));
        try
        {
            return Convert.ToHexString(SHA256.HashData(source));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }

    /// <summary>Withdraws a refresh token.</summary>
    /// <param name="request">The refresh token to withdraw.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>An empty success when the session has been ended.</returns>
    /// <remarks>
    /// Anonymous, and deliberately so: the token in the body is what names the session being ended, and
    /// demanding a valid access token as well would leave a caller whose access token had already expired
    /// unable to end its own session - which is precisely the caller most likely to be trying.
    /// </remarks>
    [HttpPost("logout")]
    // A BUDGET OF ITS OWN, NOT THE ONE SIGN-IN DRAWS ON. While this shared AuthenticationPolicyName, a
    // burst of failed sign-in attempts from any peer sharing the caller's address could spend the window
    // and leave a token that its owner had asked to revoke live until it expired.
    [EnableRateLimiting(RateLimitingExtensions.RevocationPolicyName)]
    // Withdraws a token derived from a credential. RETAINED: this is what keeps the concurrency bound
    // and the request-body limit on this action; only the window it draws on has changed.
    [CredentialEndpoint]
    [RemediationAllowed]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> LogoutAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        Result outcome = await _auth
            .LogoutAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Describes the caller.</summary>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The caller's identity, roles and granted permission codes.</returns>
    /// <remarks>
    /// The authorisation requirement is a bare one: any authenticated caller may describe itself. No named
    /// policy is applied, and none would fit.
    /// </remarks>
    [HttpGet("me")]
    // Bounded on its OWN budget rather than on the credential window. See the note above the controller.
    [EnableRateLimiting(RateLimitingExtensions.SessionReadPolicyName)]
    [Authorize]
    [RemediationAllowed]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<CurrentUserDto?>>> GetCurrentUserAsync(
        CancellationToken cancellationToken)
    {
        Result<CurrentUserDto?> outcome = await _auth
            .GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no description means the token named an account that has since
        // gone, which the shared translator answers as a not-found problem document.
        return this.Complete(outcome);
    }
}
