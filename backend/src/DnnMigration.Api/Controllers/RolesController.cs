using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role resource: the permission grouping, and who belongs to it.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Security/Roles.ascx.vb</c>, <c>EditRoles.ascx.vb</c> and
/// <c>SecurityRoles.ascx.vb</c>. The paid-membership fields those screens carried - billing frequency, service
/// fee, trial frequency and period - are preserved rather than dropped, because they are stored data with
/// meaning: the billing frequency codes match a single-character column and renaming them would orphan every
/// existing row.
/// </para>
/// <para>
/// Membership is a sub-collection rather than a field on the role, which is what the legacy assignment screen
/// was: assigning a user carries its own effective and expiry dates, so it is a thing with state rather than
/// an entry in a list.
/// </para>
/// <para>
/// There is deliberately no declarative validator for the update request. The role update rules that matter
/// are relational - whether the group exists, whether the name collides within the portal, whether a trial
/// period is coherent with the billing frequency - and none of them can be decided without the database, so
/// they live in the service where the database is. A validator here would only be able to re-check what the
/// creation validator already checks, and having one would suggest the update path was validated more
/// thoroughly than it is.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
public sealed class RolesController : ControllerBase
{
    private readonly IRoleService _roles;
    private readonly IValidator<PagedRequest> _pageValidator;
    private readonly IValidator<CreateRoleRequest> _createValidator;

    /// <summary>Initialises a new instance of the <see cref="RolesController"/> class.</summary>
    /// <param name="roles">The role service.</param>
    /// <param name="pageValidator">Validates paging arguments.</param>
    /// <param name="createValidator">Validates a creation request.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RolesController(
        IRoleService roles,
        IValidator<PagedRequest> pageValidator,
        IValidator<CreateRoleRequest> createValidator)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _pageValidator = pageValidator ?? throw new ArgumentNullException(nameof(pageValidator));
        _createValidator = createValidator ?? throw new ArgumentNullException(nameof(createValidator));
    }

    /// <summary>Lists a portal's roles.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="roleGroupId">Restricts the result to one role group.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of roles.</returns>
    [HttpGet("portals/{portalId:int}/roles")]
    [ProducesResponseType(typeof(PagedResult<RoleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<RoleListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] PagedRequest request,
        [FromQuery] int? roleGroupId,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_pageValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PagedResult<RoleListItemDto>> outcome = await _roles
            .ListRolesAsync(portalId, request, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The role, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    [HttpGet("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDetailDto?>> GetAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken)
    {
        Result<RoleDetailDto?> outcome = await _roles
            .GetRoleAsync(portalId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The role to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created role, with its address in the location header.</returns>
    [HttpPost("portals/{portalId:int}/roles")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDetailDto>> CreateAsync(
        int portalId,
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_createValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<RoleDetailDto> outcome = await _roles
            .CreateRoleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.RoleId);
    }

    /// <summary>Updates a role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated role.</returns>
    [HttpPut("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDetailDto>> UpdateAsync(
        int portalId,
        int roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        Result<RoleDetailDto> outcome = await _roles
            .UpdateRoleAsync(portalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the role has been removed.</returns>
    [HttpDelete("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .DeleteRoleAsync(portalId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the users who hold a role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of users holding the role.</returns>
    [HttpGet("portals/{portalId:int}/roles/{roleId:int}/users")]
    [ProducesResponseType(typeof(PagedResult<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<UserListItemDto>>> ListUsersAsync(
        int portalId,
        int roleId,
        [FromQuery] PagedRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_pageValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PagedResult<UserListItemDto>> outcome = await _roles
            .ListRoleUsersAsync(portalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the roles one user holds.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The roles the user holds in this portal.</returns>
    /// <remarks>
    /// Addressed under the user because that is the resource being described, and served by this controller
    /// because roles are what is returned. The alternative - putting it on the user controller - would give
    /// two controllers a reason to depend on the role service.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/users/{userId:int}/roles")]
    [ProducesResponseType(typeof(IReadOnlyList<RoleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RoleListItemDto>>> ListForUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<RoleListItemDto>> outcome = await _roles
            .ListUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Assigns a user to a role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="request">The user to assign, with any effective and expiry dates.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the assignment has been recorded.</returns>
    [HttpPost("portals/{portalId:int}/roles/{roleId:int}/users")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> AssignUserAsync(
        int portalId,
        int roleId,
        [FromBody] RoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .AssignUserToRoleAsync(portalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a user from a role.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the assignment has been removed.</returns>
    [HttpDelete("portals/{portalId:int}/roles/{roleId:int}/users/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveUserAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .RemoveUserFromRoleAsync(portalId, roleId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
