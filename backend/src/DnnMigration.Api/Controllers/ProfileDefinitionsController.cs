using Asp.Versioning;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
using DnnMigration.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The profile property definition resource: which profile fields a portal collects.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> and
/// <c>EditProfileDefinition.ascx.vb</c>.
/// </para>
/// <para>
/// These definitions are what makes the profile screen dynamic: a user's profile is a set of values keyed by
/// definition, so changing a definition changes the shape of every user's profile in the portal. That is why
/// this is an administrative resource distinct from the profile itself, and why deleting a definition is a
/// separate act from clearing a value.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals/{portalId:int}/profile-definitions")]
[Produces("application/json")]
[Authorize]
public sealed class ProfileDefinitionsController : ControllerBase
{
    private readonly IUserService _users;

    /// <summary>Initialises a new instance of the <see cref="ProfileDefinitionsController"/> class.</summary>
    /// <param name="users">The user service, which owns profile definitions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="users"/> is <see langword="null"/>.</exception>
    public ProfileDefinitionsController(IUserService users)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
    }

    /// <summary>Lists a portal's profile property definitions.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The definitions, in display order.</returns>
    /// <remarks>
    /// Readable by any authenticated caller because the profile screen needs the definitions to render at
    /// all; only the writes are restricted.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProfilePropertyDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProfilePropertyDefinitionDto>>> ListAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await _users
            .ListProfilePropertyDefinitionsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one profile property definition.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="propertyDefinitionId">The definition identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    [HttpGet("{propertyDefinitionId:int}")]
    [ProducesResponseType(typeof(ProfilePropertyDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfilePropertyDefinitionDto?>> GetAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        Result<ProfilePropertyDefinitionDto?> outcome = await _users
            .GetProfilePropertyDefinitionAsync(portalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a profile property definition.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="definition">The definition to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created definition, with its address in the location header.</returns>
    [HttpPost]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ProfilePropertyDefinitionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfilePropertyDefinitionDto>> CreateAsync(
        int portalId,
        [FromBody] ProfilePropertyDefinitionDto definition,
        CancellationToken cancellationToken)
    {
        Result<ProfilePropertyDefinitionDto> outcome = await _users
            .CreateProfilePropertyDefinitionAsync(portalId, definition, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.PropertyDefinitionId);
    }

    /// <summary>Updates a profile property definition.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="propertyDefinitionId">The definition identifier.</param>
    /// <param name="definition">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated definition.</returns>
    [HttpPut("{propertyDefinitionId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ProfilePropertyDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfilePropertyDefinitionDto>> UpdateAsync(
        int portalId,
        int propertyDefinitionId,
        [FromBody] ProfilePropertyDefinitionDto definition,
        CancellationToken cancellationToken)
    {
        Result<ProfilePropertyDefinitionDto> outcome = await _users
            .UpdateProfilePropertyDefinitionAsync(
                portalId,
                propertyDefinitionId,
                definition,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a profile property definition.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="propertyDefinitionId">The definition identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the definition has been removed.</returns>
    [HttpDelete("{propertyDefinitionId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .DeleteProfilePropertyDefinitionAsync(portalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
