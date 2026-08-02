using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The permission query surface.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the read surface of the three legacy permission controllers -
/// <c>PermissionController.vb</c>, <c>ModulePermissionController.vb</c> and
/// <c>TabPermissionController.vb</c> - which between them exposed forty-two public members and duplicated the
/// same allow-and-deny reasoning three times.
/// </para>
/// <para>
/// <strong>Query only, and answered by one authority.</strong> Every endpoint here asks a question; none
/// decides an answer. The precedence rules - a denial beating an allowance at the same scope, the pseudo-roles
/// that match every caller or only anonymous ones, the superuser short circuit - live in a single evaluator
/// behind the permission service. Re-deriving any of them here would create a second evaluator that could
/// disagree with the first, which is the worst available outcome in this area: the two would agree in testing
/// and diverge on the one case that mattered.
/// </para>
/// <para>
/// <strong>Why the cross-user queries require an administrator.</strong> These endpoints accept an arbitrary
/// user identifier, so answering them for any authenticated caller would tell every signed-in user what every
/// other user can reach. The service does not restrict this itself - it answers what it is asked - so the
/// restriction is applied here declaratively, as a policy, not as a decision written in code. A caller who
/// wants their own permissions does not need these endpoints: their permissions are minted into their token
/// and returned by the current-user endpoint.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize]
public sealed class PermissionsController : ControllerBase
{
    private readonly IPermissionService _permissions;

    /// <summary>Initialises a new instance of the <see cref="PermissionsController"/> class.</summary>
    /// <param name="permissions">The permission service.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="permissions"/> is <see langword="null"/>.
    /// </exception>
    public PermissionsController(IPermissionService permissions)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }

    /// <summary>Lists the permission keys this installation defines.</summary>
    /// <param name="permissionCode">Restricts the result to one permission code.</param>
    /// <param name="moduleDefinitionId">Restricts the result to one module definition's permissions.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The permission keys.</returns>
    /// <remarks>
    /// The catalogue describes what permissions exist, not who holds them, so it carries no information about
    /// any caller and is readable by any authenticated one.
    /// </remarks>
    [HttpGet("permissions")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<string>>> ListKeysAsync(
        [FromQuery] string? permissionCode,
        [FromQuery] int? moduleDefinitionId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<string>> outcome = await _permissions
            .GetPermissionKeysAsync(permissionCode, moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the permission keys a caller effectively holds.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user to evaluate, or omitted to evaluate an anonymous caller.</param>
    /// <param name="moduleId">Narrows the evaluation to one module.</param>
    /// <param name="tabId">Narrows the evaluation to one tab.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The keys the caller holds after denials have been applied.</returns>
    /// <remarks>
    /// Omitting the user identifier is meaningful rather than an error: it evaluates what an anonymous visitor
    /// reaches, which is the question an administrator asks when checking whether a page is public.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/permissions/effective")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<string>>> ListEffectiveAsync(
        int portalId,
        [FromQuery] int? userId,
        [FromQuery] int? moduleId,
        [FromQuery] int? tabId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<string>> outcome = await _permissions
            .GetEffectivePermissionKeysAsync(portalId, userId, moduleId, tabId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Reports whether a caller holds one permission on a module.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="permissionKey">The permission to test.</param>
    /// <param name="userId">The user to evaluate, or omitted to evaluate an anonymous caller.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><see langword="true"/> when the permission is held.</returns>
    [HttpGet("portals/{portalId:int}/permissions/modules/{moduleId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<bool>> HasModulePermissionAsync(
        int portalId,
        int moduleId,
        [FromQuery] PermissionKey permissionKey,
        [FromQuery] int? userId,
        CancellationToken cancellationToken)
    {
        Result<bool> outcome = await _permissions
            .HasModulePermissionAsync(portalId, userId, moduleId, permissionKey, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Reports whether a caller holds one permission on a tab.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="tabId">The tab identifier.</param>
    /// <param name="permissionKey">The permission to test.</param>
    /// <param name="userId">The user to evaluate, or omitted to evaluate an anonymous caller.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><see langword="true"/> when the permission is held.</returns>
    [HttpGet("portals/{portalId:int}/permissions/tabs/{tabId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<bool>> HasTabPermissionAsync(
        int portalId,
        int tabId,
        [FromQuery] PermissionKey permissionKey,
        [FromQuery] int? userId,
        CancellationToken cancellationToken)
    {
        Result<bool> outcome = await _permissions
            .HasTabPermissionAsync(portalId, userId, tabId, permissionKey, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes every permission granted directly to one user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the grants have been removed.</returns>
    /// <remarks>
    /// Only grants made to the user personally are affected. Anything they reach through a role remains,
    /// because a role grant is not theirs to lose - and the legacy cleanup procedures drew the same line.
    /// </remarks>
    [HttpDelete("portals/{portalId:int}/users/{userId:int}/permissions")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _permissions
            .DeleteUserPermissionsAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
