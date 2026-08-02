using Asp.Versioning;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The authentication endpoints: sign in, refresh, sign out, and who am I.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb</c>. The legacy
/// page validated credentials on a postback, reported the outcome through a by-reference status argument, and
/// issued a forms-authentication cookie. Here the outcome is the return value and the credential is exchanged
/// for a bearer token pair.
/// </para>
/// <para>
/// Two legacy arguments are deliberately gone. The authentication-type argument - always the literal
/// <c>"DNN"</c> at the one call site that mattered - disappears with the single token path, and the CAPTCHA
/// argument disappears with the excluded control. Both are recorded in the migration notes rather than
/// silently dropped.
/// </para>
/// <para>
/// <strong>Nothing in this file inspects, transforms, logs or reports a credential.</strong> The password
/// travels from the request object into the service and no further. There is no sign-in logging here at all:
/// the request-logging middleware records the method, the path and the status, and a failure reason that
/// distinguished a wrong password from an unknown account would hand an attacker exactly the distinction the
/// service refuses to make.
/// </para>
/// <para>
/// <strong>Every endpoint that accepts a credential or a refresh token is rate limited.</strong> The named
/// policy partitions callers by remote address and queues nothing, so a caller who exceeds it is told to come
/// back rather than being made to wait.
/// </para>
/// <para>
/// <strong>Anonymous access is declared per action, never on this class.</strong> That is not a style
/// preference. <see cref="AllowAnonymousAttribute"/> anywhere in an endpoint's metadata suppresses
/// authorisation for that endpoint entirely, and an action-level <see cref="AuthorizeAttribute"/> does not
/// override it - so declaring the class anonymous silently makes the current-user endpoint anonymous too. The
/// symptom is not an error: that endpoint answers, and answers as though nobody were signed in, which reads
/// like a missing account rather than like a missing authorisation check. Verified by observing exactly that
/// behaviour before this was corrected.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    /// <summary>The query-string name that names the portal being signed in to.</summary>
    /// <remarks>
    /// A fallback, not the primary source. The request host normally identifies the portal, exactly as it did
    /// in the legacy application, where the alias determined the tenant before the page ever ran.
    /// </remarks>
    public const string PortalQueryParameterName = "portalId";

    private readonly IAuthService _auth;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="AuthController"/> class.</summary>
    /// <param name="auth">The authentication service.</param>
    /// <param name="loginValidator">Validates a sign-in request.</param>
    /// <param name="portalContext">
    /// Holds the tenant the alias-resolution middleware resolved from the request host, if any.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AuthController(
        IAuthService auth,
        IValidator<LoginRequest> loginValidator,
        IPortalContextHolder portalContext)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _loginValidator = loginValidator ?? throw new ArgumentNullException(nameof(loginValidator));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Exchanges a credential for an access token and a refresh token.</summary>
    /// <param name="request">The credential.</param>
    /// <param name="portalId">
    /// The portal being signed in to. Required only when the request host is not a configured portal alias.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The token pair and the caller's own details, or <c>401 Unauthorized</c>.</returns>
    /// <remarks>
    /// <para>
    /// The portal is taken from the tenant that the alias-resolution middleware resolved from the request
    /// host, and only from the query string when the host resolved to nothing. That precedence matters: an
    /// account exists within one portal, so signing in to the wrong one would fail with the same generic
    /// denial as a wrong password and would be extremely hard to diagnose.
    /// </para>
    /// <para>
    /// Choosing which value to pass is not a rule about who may sign in - the service owns every one of
    /// those, including the fixed order in which the account gates are applied and the deliberately
    /// indistinguishable denial they all produce.
    /// </para>
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthenticationPolicyName)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoginResponse>> LoginAsync(
        [FromBody] LoginRequest request,
        [FromQuery(Name = PortalQueryParameterName)] int? portalId,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_loginValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        // The resolved tenant is read through the holder rather than out of the request's feature bag:
        // the middleware publishes it nowhere else, and an abstraction is the one thing a caller cannot
        // reach in to replace.
        int? resolvedPortalId = _portalContext.IsResolved
            ? _portalContext.Current.PortalId
            : portalId;

        if (resolvedPortalId is null)
        {
            ModelState.AddModelError(
                PortalQueryParameterName,
                "The request host does not correspond to a configured portal alias, so the portal being "
                + "signed in to must be stated explicitly.");

            return ValidationProblem(ModelState);
        }

        Result<LoginResponse> outcome = await _auth
            .LoginAsync(
                resolvedPortalId.Value,
                request,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Exchanges a refresh token for a new token pair.</summary>
    /// <param name="request">The refresh token.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A new token pair, or <c>401 Unauthorized</c> when the token cannot be honoured.</returns>
    /// <remarks>
    /// The refresh token is single use: presenting one that has already been exchanged is treated as a replay
    /// and revokes the account's entire set of refresh tokens rather than only the presented one. That is
    /// wider than the legacy sign-out, which cleared one cookie, and it is recorded as a deliberate
    /// difference - the alternative leaves a thief holding a working token after their theft has been
    /// detected.
    /// </remarks>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthenticationPolicyName)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoginResponse>> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result<LoginResponse> outcome = await _auth
            .RefreshAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Revokes a refresh token.</summary>
    /// <param name="request">The refresh token to revoke.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the token has been revoked.</returns>
    /// <remarks>
    /// <para>
    /// Anonymous, because the token in the body is what identifies the session being ended - requiring a valid
    /// access token as well would mean a caller whose access token had already expired could not sign out.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy sign-out cleared a forms-authentication cookie, which had an immediate
    /// server-side effect. A bearer token cannot be recalled once issued, so this revokes the refresh token
    /// and the already-issued access token remains valid until it expires. That window is why the access token
    /// lifetime is short, and the difference is recorded in the migration notes.
    /// </para>
    /// </remarks>
    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthenticationPolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> LogoutAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result outcome = await _auth
            .LogoutAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Describes the caller.</summary>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The caller's identity, roles and permission keys.</returns>
    /// <remarks>
    /// This is how a client learns what it may do. The permission keys returned here are the ones minted into
    /// the caller's token, which is why no client needs the administrative permission-query endpoints to
    /// describe itself - and why a client must still not treat this as enforcement. Every decision is made
    /// again on the server for every request.
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentUserDto?>> GetCurrentUserAsync(
        CancellationToken cancellationToken)
    {
        Result<CurrentUserDto?> outcome = await _auth
            .GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
