using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DnnMigration.Api.Controllers;

/// <summary>The user resource: identity, membership, profile and credentials.</summary>
/// <remarks>
/// <para>
/// <b>Routes are flat and the tenant is resolved from the request host, not from the path.</b> Every action
/// hangs off <c>/api/v1/users</c>; none names a portal. The application contract this file delegates to
/// takes the portal as its first argument on every member, and the value comes from the injected
/// <see cref="IPortalContextHolder"/>, which <c>PortalAliasResolutionMiddleware</c> settles from the
/// request's host and alias. An unresolved tenant is refused with <c>portal.tenant_unresolved</c> rather
/// than defaulted, so no action can operate on a tenant nobody named.
/// </para>
/// <para>
/// <b>Twenty-two actions, gated in three bands.</b> Twelve are tenant administration
/// (<see cref="PolicyNames.PortalAdministrator"/>): the listing, the search, the choice list, create,
/// update, delete, password reset, unlock, approval, force-password-change and the two
/// membership-settings verbs. Four are owner-or-administrator
/// (<see cref="PolicyNames.AccountOwnerOrPortalAdministrator"/>): read one account, export its personal
/// data and the two profile verbs. Six are owner-only (<see cref="PolicyNames.AccountOwner"/>): change
/// password, list member services, subscribe, cancel, start a trial and redeem a service code.
/// </para>
/// <para>
/// Paging is ZERO-BASED and search is a STARTS-WITH match, both preserved from the legacy listing rather
/// than modernised. <c>Website/admin/Users/Users.ascx.vb</c> passed <c>CurrentPage - 1</c> as the page
/// index into all four of its readers (<c>:L265</c>, <c>:L269</c>, <c>:L271</c>, <c>:L274</c>) and appended
/// a single trailing wildcard to the search text - <c>SearchText + "%"</c> - so the match was anchored at
/// the start of the value.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
[Authorize]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    // ONE DEPENDENCY, and it is an application-layer contract. Persistence is unreachable from this file:
    // there is no context, no repository and no unit of work, and the project reference graph makes that a
    // compile-time fact rather than a convention.

    /// <summary>The account service this controller delegates to.</summary>
    private readonly IUserService _users;
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="UsersController"/> class.</summary>
    /// <param name="users">The application-layer contract for the account aggregate.</param>
    /// <param name="portalContext">The tenant resolved from the request host.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public UsersController(IUserService users, IPortalContextHolder portalContext)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the tenant resolved for the current request, or <see langword="null"/>.</summary>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>
    /// Returns a filter that was genuinely supplied, or <see langword="null"/> for one that is blank.
    /// </summary>
    /// <param name="value">The filter as it arrived.</param>
    /// <returns>The filter, or <see langword="null"/> when it carries nothing to filter by.</returns>
    /// <remarks>
    /// The service's rule is deliberately left exactly as it is: absence means "do not filter" and a blank
    /// filter is a caller error, because an empty prefix matches every row and would make a filtered search
    /// silently unfiltered. What changes is that this action no longer MANUFACTURES such a filter out of a
    /// value the other action would have discarded.
    /// </remarks>
    private static string? Supplied(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Lists a portal's users.</summary>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="userName">Restricts the result to user names beginning with this text.</param>
    /// <param name="email">Restricts the result to addresses beginning with this text.</param>
    /// <param name="profilePropertyName">Restricts the result by a profile property's value.</param>
    /// <param name="profilePropertyValue">The profile value to match.</param>
    /// <param name="isApproved">Restricts the result by approval state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// One page of users, in the wire envelope: an <c>items</c> array of rows and a <c>meta</c> object
    /// carrying the total across every page, the page index and the page size.
    /// </returns>
    /// <remarks>
    /// The page index this action accepts is ZERO-BASED, matching the legacy <c>CurrentPage - 1</c>
    /// arithmetic, and each text filter matches from the START of the value, matching the legacy
    /// <c>SearchText + "%"</c>. Both are preserved rather than modernised because either change would
    /// silently alter which rows a client receives.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<UserListItemDto>>> ListAsync(
        [FromQuery] UserPagedRequest request,
        [FromQuery] string? userName,
        [FromQuery] string? email,
        [FromQuery] string? profilePropertyName,
        [FromQuery] string? profilePropertyValue,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<PagedResult<UserListItemDto>> outcome = await _users
            .ListUsersAsync(
                portalId,
                request,
                userName,
                email,
                profilePropertyName,
                profilePropertyValue,
                isApproved,
                cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Searches a portal's users, taking every filter from the request body.</summary>
    /// <param name="request">Paging, sorting and the search filters, bound from the body.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>One page of users, in the same wire envelope the listing returns.</returns>
    /// <remarks>
    /// WHY THAT MATTERS, PRECISELY. A request target is written to the browser's history, to every forward
    /// and reverse proxy access log, to the server access log and to any telemetry that samples URLs - all
    /// of which sit at an endpoint of the encrypted channel rather than in the middle of it, so transport
    /// encryption does not address the exposure. That is CWE-598.
    /// </remarks>
    [HttpPost("search")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<UserListItemDto>>> SearchAsync(
        [FromBody] UserSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter, which resolves UserSearchRequestValidator from this
        // parameter's declared type.
        Result<PagedResult<UserListItemDto>> outcome = await _users
            .ListUsersAsync(
                portalId,
                request,
                Supplied(request.UserName),
                Supplied(request.Email),
                Supplied(request.ProfilePropertyName),
                Supplied(request.ProfilePropertyValue),
                request.IsApproved,
                cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Lists a portal's accounts as an account picker needs them, and nothing more.</summary>
    /// <param name="request">Paging, ordering and the optional name filter, bound from the query string.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// One page of choices in the wire envelope: an <c>items</c> array of rows carrying <c>userId</c>,
    /// <c>username</c> and <c>displayName</c> only, plus a <c>meta</c> object carrying the total across
    /// every page, the page index and the page size.
    /// </returns>
    /// <remarks>
    /// ⚠ WHY THIS EXISTS ALONGSIDE <see cref="ListAsync"/>, AND WHY IT MUST NOT BE FOLDED BACK INTO IT. A
    /// performance and privacy review measured the role-assignment screen building its account drop-down,
    /// and its account-count probe, from the account listing.
    /// </remarks>
    [HttpGet("choices")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserChoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<UserChoiceDto>>> ListChoicesAsync(
        [FromQuery] UserChoicePagedRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<PagedResult<UserChoiceDto>> outcome = await _users
            .ListAccountChoicesAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Retrieves one user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The user.</returns>
    [HttpGet("{userId:int}")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDetailDto?>>> GetAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<UserDetailDto?> outcome = await _users
            .GetUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Exports everything held about one account within the addressed portal.</summary>
    /// <param name="userId">The account whose record is exported.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portability document.</returns>
    /// <remarks>
    /// SAME POLICY AS THE ACCOUNT READ IT COMPOSES, DELIBERATELY. <c>AccountOwnerOrPortalAdministrator</c>
    /// is what already governs <c>GET users/{userId}</c> and <c>GET users/{userId}/profile</c>, and this
    /// document contains what those two return plus role assignments that tenant role administration
    /// already shows.
    /// </remarks>
    [HttpGet("{userId:int}/personal-data")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserPersonalDataExportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserPersonalDataExportDto?>>> ExportPersonalDataAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<UserPersonalDataExportDto?> outcome = await _users
            .ExportPersonalDataAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a user in a portal.</summary>
    /// <param name="request">The user to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created user, with their address in the location header.</returns>
    [HttpPost]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    // HASHES A CREDENTIAL, and was completely unbounded: the credential window and the process-wide
    // concurrency bound were applied by matching whole segments of the request path against a word list,
    // and "users" is not on it.
    [CredentialEndpoint]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> CreateAsync(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor.
        Result<UserDetailDto> outcome = await _users
            .CreateUserAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.UserId);
    }

    /// <summary>Updates a user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated user.</returns>
    [HttpPut("{userId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> UpdateAsync(
        int userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor.
        Result<UserDetailDto> outcome = await _users
            .UpdateUserAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the user has been removed.</returns>
    [HttpDelete("{userId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .DeleteUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Changes the calling account's own password.</summary>
    /// <param name="userId">The user identifier, which must be the caller's own.</param>
    /// <param name="request">The current and new passwords.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been changed.</returns>
    [HttpPost("{userId:int}/password")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [RemediationAllowed]
    [AllowDuringRemediation(RemediationEndpointKind.Password)]
    // Verifies the current credential and hashes the replacement.
    [CredentialEndpoint]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ChangePasswordAsync(
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor. The credential
        // itself is forwarded and never inspected, compared, hashed or logged here.
        Result outcome = await _users
            .ChangePasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Resets a user's password administratively, without the current one.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new password.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been reset.</returns>
    [HttpPost("{userId:int}/password-reset")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    // HASHES A CREDENTIAL, and was unbounded for a subtler reason than account creation: the path matcher
    // compares WHOLE segments, and this route's segment is "password-reset", which is equal to neither
    // "password" nor "reset". The address looked covered and was not.
    [CredentialEndpoint]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ResetPasswordAsync(
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor. The replacement
        // credential is forwarded and never inspected, hashed, echoed or logged here - and the new value is
        // not returned to the caller, unlike the legacy reset, which handed it back in clear text.
        Result outcome = await _users
            .ResetPasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Clears a lockout so the account can be used again.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the lockout has been cleared.</returns>
    [HttpPost("{userId:int}/unlock")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UnlockAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .UnlockUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Approves or unapproves a user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="isApproved">The approval state to set.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the approval state has been set.</returns>
    [HttpPut("{userId:int}/approval")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SetApprovalAsync(
        int userId,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }
        if (isApproved is not { } desiredState)
        {
            return this.ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(isApproved)] =
                    [
                        "The approval state is required. Send ?isApproved=true to approve the account or "
                        + "?isApproved=false to withdraw approval; omitting it is refused rather than read "
                        + "as false, because withdrawing approval also ends the account's live sessions.",
                    ],
                }));
        }

        Result outcome = await _users
            .SetUserApprovalAsync(portalId, userId, desiredState, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Requires the user to change their password at next sign-in.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the requirement has been recorded.</returns>
    [HttpPost("{userId:int}/require-password-change")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RequirePasswordChangeAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .RequirePasswordChangeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a portal's membership settings.</summary>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The membership settings.</returns>
    /// <remarks>
    /// ⚠ NO <c>404</c> IS DECLARED, BECAUSE NONE IS REACHABLE. This action used to advertise one for a
    /// tenant that stores no settings, from when the service answered that case with a value-free success
    /// that <see cref="ApiResults"/> mapped onto <c>404</c> by convention.
    /// </remarks>
    [HttpGet("settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<MembershipSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<MembershipSettingsDto?>>> GetMembershipSettingsAsync(
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<MembershipSettingsDto?> outcome = await _users
            .GetMembershipSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces the resolved tenant's membership settings.</summary>
    /// <param name="request">The settings to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// <c>200 OK</c> carrying what the write did beyond storing the policy: whether the display-name format
    /// changed, and how many accounts were consequently rewritten.
    /// </returns>
    [HttpPut("settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<MembershipSettingsUpdateResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    // The conflict branch is DECLARED, not new.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<MembershipSettingsUpdateResultDto>>> UpdateMembershipSettingsAsync(
        [FromBody] UpdateMembershipSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<MembershipSettingsUpdateResultDto> outcome = await _users
            .UpdateMembershipSettingsAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a user's profile.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The profile, including the definition of each property.</returns>
    [HttpGet("{userId:int}/profile")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [RemediationAllowed]
    [AllowDuringRemediation(RemediationEndpointKind.Profile)]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserProfileDto?>>> GetProfileAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<UserProfileDto?> outcome = await _users
            .GetProfileAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a user's profile values.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="profile">The values to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the profile has been stored.</returns>
    [HttpPut("{userId:int}/profile")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [RemediationAllowed]
    [AllowDuringRemediation(RemediationEndpointKind.Profile)]
    [EnableRateLimiting(RateLimitingExtensions.ProfileWritePolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UpdateProfileAsync(
        int userId,
        [FromBody] UserProfileDto profile,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .UpdateProfileAsync(portalId, userId, profile, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the member services this account may subscribe to, with its own subscription state.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The catalogue, in the shared envelope.</returns>
    /// <remarks>
    /// Deliberately UNPAGED, because the read it replaces was: the legacy grid bound the whole result and
    /// hid itself when the count was zero. A tenant's set of public roles is a published price list, not a
    /// data set.
    /// </remarks>
    [HttpGet("{userId:int}/services")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MemberServiceDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MemberServiceDto>>>> ListMemberServicesAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<MemberServiceDto>> outcome = await _users
            .ListMemberServicesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Subscribes this account to a member service, or renews a lapsed subscription.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="roleId">The role the service is expressed as.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the subscription has been written.</returns>
    [HttpPost("{userId:int}/services/{roleId:int}/subscription")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SubscribeToServiceAsync(
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .SubscribeToServiceAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Cancels this account's subscription to a member service.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="roleId">The role the service is expressed as.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the subscription has ended.</returns>
    [HttpDelete("{userId:int}/services/{roleId:int}/subscription")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> CancelServiceAsync(
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .CancelServiceAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Starts this account's free trial of a paid member service.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="roleId">The role the service is expressed as.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the trial subscription has been written.</returns>
    /// <remarks>
    /// The single <c>403</c> code covers every reason the legacy panel would not have rendered the command,
    /// without saying which: the service is free and so has nothing to trial, its trial carries a fee, or
    /// this account has already consumed it.
    /// </remarks>
    [HttpPost("{userId:int}/services/{roleId:int}/trial")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> StartServiceTrialAsync(
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .StartServiceTrialAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Redeems a service invitation code against this account.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="request">The submitted code.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The services the code enrolled the account in, in the shared envelope.</returns>
    /// <remarks>
    /// The search deliberately spans EVERY role of the tenant, published or not, free or not. The legacy
    /// handler read the whole role set and applied neither the public test nor the fee test the grid's own
    /// commands applied, because an invitation code IS the bypass for an unpublished service.
    /// </remarks>
    [HttpPost("{userId:int}/services/redemptions")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [EnableRateLimiting(RateLimitingExtensions.RedemptionPolicyName)]
    [CredentialEndpoint]
    [ProducesResponseType(typeof(ApiResponse<RedeemServiceCodeResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<RedeemServiceCodeResultDto>>> RedeemServiceCodeAsync(
        int userId,
        [FromBody] RedeemServiceCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor. The service repeats
        // the emptiness guard so it holds for a caller that reached it without this pipeline in front.
        Result<RedeemServiceCodeResultDto> outcome = await _users
            .RedeemServiceCodeAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
