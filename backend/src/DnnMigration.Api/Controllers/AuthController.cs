using Asp.Versioning;
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
/// <para>
/// MIGRATION: replaces <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb</c>. The legacy
/// screen verified a credential during a postback, reported the outcome through a trailing by-reference
/// status argument, raised an event carrying that status, and issued a forms-authentication cookie. Here the
/// outcome IS the return value, the credential is exchanged for a bearer pair, and nothing is raised.
/// </para>
/// <para>
/// MIGRATION: this controller is CLOSED AT FOUR OPERATIONS - sign in, refresh, revoke, and describe the
/// caller. Changing a credential and administratively replacing one both belong to the account resource,
/// where the account being altered is named by the route and the caller's entitlement to alter it can be
/// judged; neither is an authentication concern and neither is exposed here.
/// </para>
/// <para>
/// MIGRATION: the legacy installation could return a stored credential to its owner. The reminder screen
/// under <c>Website/admin/Security</c> read the account's stored value through the membership provider and
/// mailed it, which was possible only because <c>Website/release.config:L245</c> stored credentials under a
/// reversible scheme, <c>:L239</c> enabled their retrieval, and <c>:L89-L93</c> committed the deciphering key
/// to source control in plain sight. Credentials are now held under a one-way scheme, so returning one is not
/// merely disallowed here - it is not computable. That reminder screen therefore informs a RESET flow on the
/// account resource and produces no operation on this controller. <b>No operation, request member, response
/// member or log statement in this file returns, mails, echoes or records a credential.</b>
/// </para>
/// <para>
/// MIGRATION: the legacy screen guarded its click handler with a CAPTCHA at
/// <c>Login.ascx.vb:L162</c>, and the reminder screen carried the same guard. The control that implemented it
/// is on the excluded list, so the guard is DELETED rather than ported - a deliberate reduction in defence
/// against automated guessing. The compensating control is the address-partitioned window declared on this
/// class, which is why that declaration is not decoration and must not be removed.
/// </para>
/// <para>
/// MIGRATION: the legacy session ended server-side. Its sign-out, which cleared five cookies, rested on a
/// framework call with no bearer-token counterpart: an access token that has been issued cannot be recalled.
/// Revocation therefore reaches the refresh token only, and an already-issued access token remains usable
/// until it expires. That window is the reason the access-token lifetime is short, and it is a real,
/// documented reduction rather than an oversight. <b>There is no registry of withdrawn access tokens and none
/// may be introduced</b> - one would reintroduce a per-request store lookup on every authenticated call,
/// which is the property that made the legacy design expensive.
/// </para>
/// <para>
/// MIGRATION: the access-token lifetime of sixty minutes is measured parity rather than a preference. The
/// legacy forms-authentication ticket at <c>Website/release.config:L146-L147</c> declares
/// <c>timeout="60"</c>, so a session that survived an hour of inactivity before survives an hour now. The
/// lifetime, the refresh window and the signing material are read by the application and infrastructure
/// layers; this controller reads no configuration at all.
/// </para>
/// <para>
/// MIGRATION: the legacy credential policy is preserved VERBATIM from
/// <c>Website/release.config:L239-L245</c> - a minimum length of seven, no required non-alphanumeric
/// character, no question-and-answer requirement, and email addresses not required to be unique. Tightening
/// any of those during a migration would lock existing accounts away from an installation that had accepted
/// their credentials for years, so hardening is left as a separate, explicit decision. The rules are declared
/// once in the application layer's request validators; this controller states none of them.
/// </para>
/// <para>
/// MIGRATION: the legacy handler raised a user-authenticated event at <c>Login.ascx.vb:L189-L193</c>, one of
/// seven lifecycle events the base user control declared. Those become outputs and reactive effects in the
/// single-page application. <b>No server-side event bus, dispatcher, notification hub or background consumer
/// is introduced</b>; inventing one would be scope creep wearing the costume of fidelity.
/// </para>
/// <para>
/// <b>Nothing here inspects, transforms, compares, logs or reports a credential.</b> The submitted value
/// travels from the request object into the service and no further. There is deliberately no sign-in logging
/// in this file: the request-logging middleware records the method, the path and the status, and a reason that
/// separated a wrong credential from an unknown account would hand an attacker exactly the distinction the
/// service is written to withhold. Every refusal this controller emits is the stable failure CODE the service
/// returned, translated by the one shared translator, and carries no submitted value.
/// </para>
/// <para>
/// <b>Anonymous access is declared per operation and never on this class.</b> That is not a style preference.
/// An anonymous declaration anywhere in an endpoint's metadata suppresses authorisation for that endpoint
/// outright, and an operation-level authorisation declaration does not win against it - so declaring the class
/// anonymous would silently make the caller-description operation anonymous as well. The symptom would not be
/// an error: that operation would answer, and answer as though nobody were signed in, which reads like a
/// missing account rather than like a missing authorisation check.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
// Every operation here names its tenant by a means other than the host name, and sign-in MUST stay reachable
// from a host that resolves to no portal - it is how an operator obtains the session that repairs the alias
// configuration. Sign-in takes an explicit portal identifier when no alias matches; refresh, revocation and
// the caller projection are bound to the token, whose portal claim was fixed when it was issued.
[TenantOptional(
    "Sign-in accepts an explicit portal identifier and must remain reachable from an unconfigured host so an "
    + "operator can obtain a session; the other three operations take their portal from the bearer token.")]
// THE COMPENSATING CONTROL FOR THE DELETED CAPTCHA, DECLARED ONCE FOR THE WHOLE CONTROLLER. Declared here
// rather than operation by operation so that an operation added to this controller cannot be reachable
// without it: the omission that a per-operation declaration invites is exactly the omission this control
// exists to prevent. The policy is named through its constant, never through its spelling, so renaming the
// policy is a compile error rather than a silently inert attribute.
//
// The window is sized from configuration under RateLimiting:Authentication, partitioned by the caller's
// normalised remote address and never by anything the caller submits, and refuses with 429 plus a wait hint.
// Note what this deliberately is NOT: it is not a global limiter and not a default policy. The health probe
// must never be throttled - the container's readiness check and the end-to-end gate both depend on it
// answering - so the policy is opt-in and is applied here and nowhere else.
//
// WHAT THIS DECLARATION ADDS, MEASURED RATHER THAN ASSUMED, because it is not the same on every operation
// here and a reader who assumes it is will delete it. For the three operations marked as credential
// endpoints the global classifier already applies this window, so the named policy contributes a second,
// identically keyed budget consumed in lockstep with the first: one extra permit acquisition per request
// that enforces nothing further, kept because the declaration is what makes the control visible at the top
// of the file. For the caller-description read the picture is the opposite. That operation carries no
// credential and is therefore exempt from the global classifier by design, so this declaration is the WHOLE
// of its bound. Removing it would silently leave the one operation on this controller that a stolen token
// can be probed against with no limit at all - confirmed by observation: forty consecutive reads answer
// until the window is spent and then answer 429 with a wait hint, which is why that status is declared on it.
[EnableRateLimiting(RateLimitingExtensions.AuthenticationPolicyName)]
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
    private readonly IPortalContextHolder _portalContext;

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    // validated by the globally registered validation filter, which runs before the operation and resolves a
    // validator from each argument's declared type. This controller used to take validators of its own and
    // invoke them by hand as well, which was a second invocation path for one rule set. Adding a validator
    // argument back here would recreate that split.
    //
    // NOTHING ELSE IS INJECTED EITHER. The authentication service is the single seam: comparing a credential,
    // deciding whether a stored value must be replaced at a higher cost, minting and rotating the pair,
    // counting failed attempts, applying the automatic-unlock window, checking a verification code and
    // composing the caller's description all happen behind it. The tenant holder is the one exception and is
    // not a second seam - it carries a fact about the TRANSPORT, described on the sign-in operation below.
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
    /// <param name="portalId">
    /// The portal being signed in to. Required only when the request host is not a configured portal alias.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The token pair and the caller's own details, or a refusal.</returns>
    /// <response code="200">The credential was accepted. The body carries the pair and the caller.</response>
    /// <response code="400">
    /// The submission is unusable, or the portal being signed in to could be determined neither from the
    /// request host nor from the query string.
    /// </response>
    /// <response code="401">
    /// The credential was refused. Every cause answers alike: an unknown account, a wrong credential, a locked
    /// account, and an account the tenant has not yet admitted.
    /// </response>
    /// <response code="429">The shared credential budget for this window is spent.</response>
    /// <response code="503">
    /// The credential was accepted and the session could not be recorded, or a correct verification code could
    /// not be persisted.
    /// </response>
    /// <remarks>
    /// <para>
    /// The portal is taken from the tenant the alias-resolution middleware resolved from the request host, and
    /// from the query string only when the host resolved to nothing. That precedence matters: an account
    /// exists within one portal, so signing in to the wrong one would fail with the same generic denial as a
    /// wrong credential and would be very hard to diagnose.
    /// </para>
    /// <para>
    /// Choosing which value to pass is not a rule about who may sign in. The service owns every one of those,
    /// including the fixed order in which the account gates are applied and the deliberately indistinguishable
    /// denial they all produce.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy call at <c>Login.ascx.vb:L164</c> took eight arguments and reported its outcome
    /// through the last of them, a by-reference status. Four of those arguments do not survive. The
    /// authentication-type argument - always the same provider name at the one call site that mattered -
    /// disappears with the single token path, because there is no provider to select between. The portal name
    /// disappears because the tenant is ambient here rather than passed. The caller's network address
    /// disappears as an argument and becomes what the window above partitions on, which is the one use the
    /// legacy code had for it. And the status argument disappears because the outcome is now the return value.
    /// </para>
    /// </remarks>
    [HttpPost("login")]
    // Verifies a credential and, on a successful verification against a value stored at a superseded cost,
    // replaces it. The path matcher already classifies this address; the mark states the fact rather than
    // inferring it, and is what guarantees the process-wide concurrency bound applies even if the path list
    // ever changes.
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
        // validation answers with the same problem document this method would have built by hand. A shape
        // check written here would be a second invocation path for one rule set.
        //
        // The resolved tenant is read through the holder rather than reached for in the request's feature
        // collection: the middleware publishes it nowhere else, and an abstraction is the one thing a caller
        // cannot reach in and replace.
        int? resolvedPortalId = _portalContext.IsResolved
            ? _portalContext.Current.PortalId
            : portalId;

        // A NULL TEST, NEVER A VALUE TEST. The tenant identity column is seeded at minus one, so minus one
        // and zero are both real portals, and minus one is simultaneously the legacy absent-integer sentinel
        // and the "all users" pseudo-role. Any comparison against either value as though it meant "absent"
        // would sign a caller in to the wrong tenant or refuse a legitimate one.
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
        // the one the transport resolved. A tenant named in the body would let a caller present a credential
        // to a portal it is not addressing.
        request.PortalId = resolvedPortalId.Value;

        Result<LoginResponse> outcome = await _auth
            .LoginAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: the legacy screen decided authentication with `authenticated = (loginStatus <>
        // UserLoginStatus.LOGIN_FAILURE)` at Login.ascx.vb:L187. Read against the seven statuses that
        // expression admits a LOCKED-OUT ACCOUNT: only the failure status is excluded, so status three -
        // user locked out - authenticated exactly as success did. That is a measured legacy defect, and the
        // migration discipline says to annotate a discovered defect rather than to fix it silently. It is
        // annotated here and NOT reproduced: each of the seven statuses is mapped to a deliberate outcome by
        // the service, a locked account is refused, and its credential is never compared - so the account
        // cannot be used as an oracle for whether a guess was right. The two insecure-credential statuses are
        // the opposite case and are the reason a success may carry an advisory code: the credential IS
        // accepted, and the advisory travels with the successful outcome rather than turning it into a
        // refusal.
        //
        // MIGRATION: the verification ladder at Login.ascx.vb:L168-L185 produced three distinct messages -
        // enter a code, the code is wrong, and not authorised - and those survive as three distinct failure
        // codes rather than collapsing into one denial, because an account awaiting admission needs to be
        // told which of the three it is facing. Every one of them answers 401 here, decided once in the
        // shared code-to-status table rather than at this call site.
        //
        // MIGRATION: the legacy handler seeded its outcome from the sentinel module - false for the
        // authenticated flag and the EMPTY STRING, not null, for the message, at Login.ascx.vb:L165-L166. The
        // successful-or-failed distinction is now carried by the outcome itself, and no empty string is
        // manufactured to mean "no message": a refusal always carries a code, and serialisation is configured
        // never to elide an empty or sentinel value from a response.
        return this.Complete(outcome);
    }

    /// <summary>Exchanges a refresh token for a new token pair.</summary>
    /// <param name="request">The refresh token.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A new token pair, or a refusal.</returns>
    /// <response code="200">The token was honoured. The body carries the replacement pair.</response>
    /// <response code="400">The submission is unusable.</response>
    /// <response code="401">
    /// The token is unknown, expired, already exchanged, or belongs to an account that may no longer sign in.
    /// Every cause answers alike.
    /// </response>
    /// <response code="429">The shared credential budget for this window is spent.</response>
    /// <response code="503">The session store could not be reached.</response>
    /// <remarks>
    /// <para>
    /// The refresh token is single use. Presenting one that has already been exchanged is treated as a replay
    /// and withdraws the account's whole set of refresh tokens rather than only the presented one. That is
    /// wider than the legacy sign-out, which cleared one cookie, and it is a deliberate difference: the
    /// alternative leaves a thief holding a working token after the theft has been detected.
    /// </para>
    /// <para>
    /// Anonymous, because the token in the body is the credential being presented. Rotation, lifetimes and the
    /// retention of superseded generations are decided behind the service; this operation carries none of them.
    /// </para>
    /// </remarks>
    [HttpPost("refresh")]
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
        Result<LoginResponse> outcome = await _auth
            .RefreshAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Withdraws a refresh token.</summary>
    /// <param name="request">The refresh token to withdraw.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>An empty success when the session has been ended.</returns>
    /// <response code="204">
    /// The session has been ended. Answered whether or not a matching token was found, so the response cannot
    /// be used to discover which tokens exist.
    /// </response>
    /// <response code="400">The submission is unusable.</response>
    /// <response code="429">The shared credential budget for this window is spent.</response>
    /// <response code="503">The session store could not be reached.</response>
    /// <remarks>
    /// <para>
    /// Anonymous, and deliberately so: the token in the body is what names the session being ended, and
    /// demanding a valid access token as well would leave a caller whose access token had already expired
    /// unable to end its own session - which is precisely the caller most likely to be trying.
    /// </para>
    /// <para>
    /// The empty success is unconditional. Answering differently for a token that was found and one that was
    /// not would turn this operation into a probe for which sessions are live, and an anonymous caller must not
    /// be handed that.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy sign-out (<c>Library/Components/Security/PortalSecurity.vb:L77</c>) cleared five
    /// cookies and took effect at once. A bearer token cannot be recalled, so this withdraws the refresh token
    /// and the already-issued access token stays valid until it expires. There is no registry of withdrawn
    /// access tokens, and that reduction is documented rather than papered over.
    /// </para>
    /// </remarks>
    [HttpPost("logout")]
    // Withdraws a token derived from a credential.
    [CredentialEndpoint]
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
    /// <response code="200">The body carries the caller's own description.</response>
    /// <response code="401">No token was presented, or the one presented is not valid.</response>
    /// <response code="404">The token is valid and the account it names no longer resolves.</response>
    /// <response code="429">The shared credential budget for this window is spent.</response>
    /// <remarks>
    /// <para>
    /// This is how a client learns what it may do. The permission codes returned here are the ones minted into
    /// the caller's token, which is why no client needs the administrative permission-query operations to
    /// describe itself - and why a client must still not treat this as enforcement. Every decision is made
    /// again on the server for every request.
    /// </para>
    /// <para>
    /// The authorisation requirement is a bare one: any authenticated caller may describe itself. No named
    /// policy is applied, and none would fit. The item-scoped policies resolve a module or page identifier
    /// from route data that this address does not carry and would refuse every caller, and an administrative
    /// policy would refuse every ordinary account the operation exists to serve.
    /// </para>
    /// <para>
    /// Bounded by the same window as the rest of this controller. The operation carries no credential, so it
    /// is not marked as a credential endpoint and takes no part in the process-wide concurrency bound that
    /// protects the hashing work; the window still applies, because it is declared for the controller.
    /// </para>
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
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

        // A successful outcome carrying no description means the token named an account that has since gone,
        // which the shared translator answers as a not-found problem document. Reading the value without
        // testing the outcome first would throw on a refusal and turn a clean denial into a server fault.
        return this.Complete(outcome);
    }
}
