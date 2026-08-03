using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
/// <para>
/// <strong>EVERY ACTION CARRIES ITS OWN POLICY, and the class-level attribute is authentication alone rather
/// than the authorisation.</strong> An earlier revision relied on that class attribute for all thirteen
/// routes, which meant any authenticated caller - a plain member of any tenant - could enumerate a tenant's
/// accounts and their personal data, create, update and delete accounts, rewrite profiles and membership
/// settings, approve, unapprove and unlock accounts, and invoke administrative credential reset against
/// anybody. Authentication is not authorisation, and on this resource the gap between them is the whole
/// attack surface.
/// </para>
/// <para>
/// The thirteen routes split into two kinds, and the split is not cosmetic. <strong>Administrative</strong> -
/// list, create, delete, unlock, approval, require-password-change, and the tenant's membership settings -
/// carry tenant-bound portal administration, so the caller must administer the very tenant the route names.
/// <strong>Self-service</strong> - read one account, update it, read and write its profile, change its
/// credential - carry account access, which admits the addressed account itself or an administrator of its
/// tenant. Gating the self-service half on administration would make an ordinary member unable to maintain
/// their own record, which the legacy screens plainly allowed; gating the administrative half on account
/// access would let a member administer a tenant.
/// </para>
/// <para>
/// The password route deserves its own note, because it is the one where the policy is necessary but not
/// sufficient. It accepts both a self-service change and an administrative reset, and those are not equally
/// available to both kinds of caller: the policy establishes that the caller may address the account, and the
/// Application service then requires the current credential for a self-service change and administrative
/// authority for a reset. Only the service can make that distinction, because only the service can see which
/// operation the body asked for.
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

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    // validated by FluentValidationActionFilter, which is registered once for the whole API, runs before
    // the action and resolves a validator from each argument's declared type. This controller used to
    // take validators of its own and invoke them by hand as well, which was a second invocation path for
    // one rule set and the reason the paging contract was judged against the wrong sortable vocabulary.
    // Adding a validator argument back here would recreate that split.
    /// <summary>Initialises a new instance of the <see cref="UsersController"/> class.</summary>
    /// <param name="users">The user service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="users"/> is <see langword="null"/>.</exception>
    public UsersController(IUserService users)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
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
    /// <returns>One page of users, in the wire envelope: an <c>items</c> array of rows and a <c>meta</c> object carrying the total across every page, the page index and the page size. The domain paging type is not serialised.</returns>
    /// <remarks>
    /// The filters are passed through exactly as received. Which combinations are legal - and in particular
    /// that a profile property name without a value is a conflict rather than a wildcard - is the service's
    /// rule, and re-implementing it here would give HTTP callers a different answer from every other caller.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/users")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<UserListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] UserPagedRequest request,
        [FromQuery] string? userName,
        [FromQuery] string? email,
        [FromQuery] string? profilePropertyName,
        [FromQuery] string? profilePropertyValue,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
        // The paging contract is validated by the globally registered validation filter, which now
        // resolves UserPagedRequestValidator from this parameter's type and applies the account
        // collection's own sortable set. This action used to invoke IValidator<PagedRequest> by hand,
        // which was a second invocation path AND the wrong rules: that contract resolves the
        // unspecialised validator, whose sortable set is the union of every collection's, so a portal
        // or role field name was accepted here and then discarded by the listing.
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

        // Projected onto the wire envelope here rather than returned as the domain page. CompletePage
        // applies PagedResponse<T>.From, so the response carries `items` plus `meta` and the domain
        // paging type never crosses the boundary.
        return this.CompletePage(outcome);
    }

    /// <summary>Retrieves one user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The user.</returns>
    [HttpGet("portals/{portalId:int}/users/{userId:int}")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDetailDto?>>> GetAsync(
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    // HASHES A CREDENTIAL, and was completely unbounded: the credential window and the process-wide
    // concurrency bound were applied by matching whole segments of the request path against a word list, and
    // "users" is not on it. Hashing is deliberately expensive, so an unbounded hashing endpoint lets the
    // caller decide how much of this process's time is spent.
    //
    // The mark alone is the complete measure and no EnableRateLimiting attribute accompanies it. The mark is
    // read by the GLOBAL limiter's classifier, and that limiter chains both bounds - the window and the
    // process-wide concurrency ceiling. A named policy contributes exactly one partition, so it could add
    // only a second window, keyed identically to the first and therefore consumed in lockstep with it: a
    // duplicate limiter instance and a second permit acquisition per request that enforce nothing further.
    [CredentialEndpoint]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> CreateAsync(
        int portalId,
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> UpdateAsync(
        int portalId,
        int userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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

    /// <summary>Changes the calling account's own password.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier, which must be the caller's own.</param>
    /// <param name="request">The current and new passwords.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been changed.</returns>
    /// <remarks>
    /// SELF-SERVICE ONLY. The account-owner policy requires the subject claim to equal the account in the
    /// route, and the service always verifies the credential presented. An administrator who must intervene
    /// uses the sibling reset endpoint, which is a distinct authorisation decision rather than a different
    /// shape of body sent to this one - the arrangement that previously let any bearer token overwrite any
    /// account's credential in any tenant simply by naming it in the route.
    /// </remarks>
    [HttpPost("portals/{portalId:int}/users/{userId:int}/password")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    // Verifies the current credential and hashes the replacement.
    [CredentialEndpoint]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> ChangePasswordAsync(
        int portalId,
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result outcome = await _users
            .ChangePasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Resets a user's password administratively, without the current one.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new password. The current password is neither required nor consulted.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been reset.</returns>
    /// <remarks>
    /// <para>
    /// ITS OWN ENDPOINT, AND THAT IS THE POINT. Not verifying the current credential is what a reset is for,
    /// and it is safe only because reaching this address requires administration of the account's own portal.
    /// The operation used to be selectable from inside the change endpoint's request body, so the check was
    /// skipped on the caller's own instruction with no privilege proved at all.
    /// </para>
    /// <para>
    /// MIGRATION: replaces the reset branch of <c>Website/admin/Users/Password.ascx.vb</c>, which the legacy
    /// screen offered only to an administrator viewing another account. The new credential is never returned,
    /// unlike the legacy reset, which handed it back in clear text.
    /// </para>
    /// </remarks>
    [HttpPost("portals/{portalId:int}/users/{userId:int}/password-reset")]
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
    public async Task<ActionResult> ResetPasswordAsync(
        int portalId,
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result outcome = await _users
            .ResetPasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Clears a lockout so the account can be used again.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the lockout has been cleared.</returns>
    [HttpPost("portals/{portalId:int}/users/{userId:int}/unlock")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<MembershipSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MembershipSettingsDto?>>> GetMembershipSettingsAsync(
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
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserProfileDto?>>> GetProfileAsync(
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
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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
