using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Common;
using DnnMigration.Application.Dtos.Role;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role group resource: the organisational grouping of roles.
/// </summary>
/// <remarks>
/// MIGRATION: replaces <c>Website/admin/Security/EditGroups.ascx.vb</c>. A role group is presentation
/// grouping for the role administration screens - it confers no permission of its own - which is why it has
/// no membership sub-collection and why deleting one is not a permission change.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals/{portalId:int}/role-groups")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
public sealed class RoleGroupsController : ControllerBase
{
    private readonly IRoleService _roles;

    /// <summary>Initialises a new instance of the <see cref="RoleGroupsController"/> class.</summary>
    /// <param name="roles">The role service, which owns role groups.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <see langword="null"/>.</exception>
    public RoleGroupsController(IRoleService roles)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
    }

    /// <summary>Lists a portal's role groups.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portal's role groups.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoleGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<RoleGroupDto>>> ListAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<RoleGroupDto>> outcome = await _roles
            .ListRoleGroupsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one role group.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleGroupId">The role group identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The role group, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    [HttpGet("{roleGroupId:int}")]
    [ProducesResponseType(typeof(RoleGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleGroupDto?>> GetAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken)
    {
        Result<RoleGroupDto?> outcome = await _roles
            .GetRoleGroupAsync(portalId, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a role group.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The role group to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created role group, with its address in the location header.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(RoleGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleGroupDto>> CreateAsync(
        int portalId,
        [FromBody] RoleGroupDto request,
        CancellationToken cancellationToken)
    {
        Result<RoleGroupDto> outcome = await _roles
            .CreateRoleGroupAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.RoleGroupId);
    }

    /// <summary>Updates a role group.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleGroupId">The role group identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated role group.</returns>
    [HttpPut("{roleGroupId:int}")]
    [ProducesResponseType(typeof(RoleGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleGroupDto>> UpdateAsync(
        int portalId,
        int roleGroupId,
        [FromBody] RoleGroupDto request,
        CancellationToken cancellationToken)
    {
        Result<RoleGroupDto> outcome = await _roles
            .UpdateRoleGroupAsync(portalId, roleGroupId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a role group.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="roleGroupId">The role group identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the role group has been removed.</returns>
    [HttpDelete("{roleGroupId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .DeleteRoleGroupAsync(portalId, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
