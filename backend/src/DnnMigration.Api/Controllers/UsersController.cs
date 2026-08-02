using Asp.Versioning;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The user resource: identity, membership, profile and credentials.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces nine admin screens - <c>ManageUsers</c>, <c>Users</c>, <c>User</c>, <c>Profile</c>,
/// <c>ViewProfile</c>, <c>Password</c>, <c>Membership</c>, <c>UserSettings</c> and <c>MemberServices</c> under
/// <c>Website/admin/Users/</c>. Nine pages collapse into one resource with sub-resources because they were
/// nine views of the same aggregate, not nine things.
/// </para>
/// <para>
/// <strong>Nothing here touches a credential.</strong> The password endpoints forward a request object and
/// return a status; no hash, no salt and no password ever appears in a response, a log line or a failure
/// message produced by this file. The legacy application stored passwords reversibly and offered retrieval;
/// there is deliberately no retrieval endpoint, and its absence is a security decision recorded in the
/// migration notes rather than an oversight.
/// </para>
/// <para>
/// The state-changing membership operations - unlock, approval, require-password-change - are separate
/// endpoints rather than fields on the update request. That mirrors the legacy screens, where each was its
/// own explicit administrative act with its own audit consequence, and it keeps an update of a display name
/// from silently unlocking an account.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly IUserService _users;
    private readonly IValidator<PagedRequest> _pageValidator;
    private readonly IValidator<CreateUserRequest> _createValidator;
    private readonly IValidator<UpdateUserRequest> _updateValidator;
    private readonly IValidator<ChangePasswordRequest> _passwordValidator;

    /// <summary>Initialises a new instance of the <see cref="UsersController"/> class.</summary>
    /// <param name="users">The user service.</param>
    /// <param name="pageValidator">Validates paging arguments.</param>
    /// <param name="createValidator">Validates a creation request.</param>
    /// <param name="updateValidator">Validates an update request.</param>
    /// <param name="passwordValidator">Validates a password change request.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public UsersController(
        IUserService users,
        IValidator<PagedRequest> pageValidator,
        IValidator<CreateUserRequest> createValidator,
        IValidator<UpdateUserRequest> updateValidator,
        IValidator<ChangePasswordRequest> passwordValidator)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _pageValidator = pageValidator ?? throw new ArgumentNullException(nameof(pageValidator));
        _createValidator = createValidator ?? throw new ArgumentNullException(nameof(createValidator));
        _updateValidator = updateValidator ?? throw new ArgumentNullException(nameof(updateValidator));
        _passwordValidator = passwordValidator
            ?? throw new ArgumentNullException(nameof(passwordValidator));
    }

    /// <summary>Lists a portal's users.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="userName">Restricts the result to user names beginning with this text.</param>
    /// <param name="email">Restricts the result to addresses beginning with this text.</param>
    /// <param name="profilePropertyName">Restricts the result by a profile property's value.</param>
    /// <param name="profilePropertyValue">The profile value to match.</param>
    /// <param name="isApproved">Restricts the result by approval state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of users.</returns>
    /// <remarks>
    /// The filters are passed through exactly as received. Which combinations are legal - and in particular
    /// that a profile property name without a value is a conflict rather than a wildcard - is the service's
    /// rule, and re-implementing it here would give HTTP callers a different answer from every other caller.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/users")]
    [ProducesResponseType(typeof(PagedResult<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<UserListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] PagedRequest request,
        [FromQuery] string? userName,
        [FromQuery] string? email,
        [FromQuery] string? profilePropertyName,
        [FromQuery] string? profilePropertyValue,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_pageValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
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

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The user.</returns>
    [HttpGet("portals/{portalId:int}/users/{userId:int}")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDetailDto?>> GetAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result<UserDetailDto?> outcome = await _users
            .GetUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a user in a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The user to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created user, with their address in the location header.</returns>
    [HttpPost("portals/{portalId:int}/users")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDetailDto>> CreateAsync(
        int portalId,
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_createValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<UserDetailDto> outcome = await _users
            .CreateUserAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.UserId);
    }

    /// <summary>Updates a user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated user.</returns>
    [HttpPut("portals/{portalId:int}/users/{userId:int}")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDetailDto>> UpdateAsync(
        int portalId,
        int userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_updateValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<UserDetailDto> outcome = await _users
            .UpdateUserAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the user has been removed.</returns>
    [HttpDelete("portals/{portalId:int}/users/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .DeleteUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Changes a user's password.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The current and new passwords.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been changed.</returns>
    [HttpPost("portals/{portalId:int}/users/{userId:int}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ChangePasswordAsync(
        int portalId,
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_passwordValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result outcome = await _users
            .ChangePasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Clears a lockout so the account can be used again.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the lockout has been cleared.</returns>
    [HttpPost("portals/{portalId:int}/users/{userId:int}/unlock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UnlockAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .UnlockUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Approves or unapproves a user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="isApproved">The approval state to set.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the approval state has been set.</returns>
    /// <remarks>
    /// The desired state is an explicit argument rather than two endpoints named approve and unapprove,
    /// because the service reports setting the state it already holds as a conflict - and that answer is only
    /// meaningful if the caller stated which state they meant.
    /// </remarks>
    [HttpPut("portals/{portalId:int}/users/{userId:int}/approval")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> SetApprovalAsync(
        int portalId,
        int userId,
        [FromQuery] bool isApproved,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .SetUserApprovalAsync(portalId, userId, isApproved, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Requires the user to change their password at next sign-in.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the requirement has been recorded.</returns>
    [HttpPost("portals/{portalId:int}/users/{userId:int}/require-password-change")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RequirePasswordChangeAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .RequirePasswordChangeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a portal's membership settings.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The membership settings.</returns>
    [HttpGet("portals/{portalId:int}/membership-settings")]
    [ProducesResponseType(typeof(MembershipSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MembershipSettingsDto?>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<MembershipSettingsDto?> outcome = await _users
            .GetMembershipSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a portal's membership settings.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the settings have been stored.</returns>
    [HttpPut("portals/{portalId:int}/membership-settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateMembershipSettingsAsync(
        int portalId,
        [FromBody] MembershipSettingsDto settings,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .UpdateMembershipSettingsAsync(portalId, settings, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a user's profile.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The profile, including the definition of each property.</returns>
    [HttpGet("portals/{portalId:int}/users/{userId:int}/profile")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserProfileDto?>> GetProfileAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result<UserProfileDto?> outcome = await _users
            .GetProfileAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a user's profile values.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="profile">The values to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the profile has been stored.</returns>
    [HttpPut("portals/{portalId:int}/users/{userId:int}/profile")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateProfileAsync(
        int portalId,
        int userId,
        [FromBody] UserProfileDto profile,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .UpdateProfileAsync(portalId, userId, profile, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
