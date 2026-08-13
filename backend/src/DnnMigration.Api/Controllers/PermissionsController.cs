using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>The permission catalogue: which permission keys this installation defines.</summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and that is a boundary rather than a phase.</strong> The catalogue describes what
/// permissions <em>exist</em>; it never describes who holds them and it is never written through this API.
/// The provenance comments at the head of this file account for all forty-two measured legacy members,
/// including the four that are deliberately not ported and the reason for each.
/// </para>
/// <para>
/// <strong>Asks, never decides.</strong> The two actions here pose questions to the application service and
/// translate the answers into status codes. They apply no filtering, sorting, grouping or precedence of its
/// own, so there is no rule expressed here that could drift out of step with the rule expressed in the
/// evaluator.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/permissions")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class PermissionsController : ControllerBase
{
    /// <summary>The catalogue reader this controller delegates to.</summary>
    private readonly IPermissionService _permissions;

    /// <summary>Initialises a new instance of the <see cref="PermissionsController"/> class.</summary>
    /// <param name="permissions">The permission service that reads the catalogue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="permissions"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// One dependency, and it is a contract. There is no repository, no persistence context and no
    /// evaluator here: the first two are unreachable from this layer by design and the third would
    /// duplicate an authority that already exists.
    /// </remarks>
    public PermissionsController(IPermissionService permissions)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }

    /// <summary>Lists the permission keys this installation defines.</summary>
    /// <param name="permissionCode">
    /// Restricts the result to one permission code - the scope a definition belongs to, matched exactly and
    /// case-insensitively - or omitted to place no restriction.
    /// </param>
    /// <param name="moduleDefinitionId">
    /// Restricts the result to the permissions one module definition declares, or omitted to place no
    /// restriction.
    /// </param>
    /// <param name="permissionKey">Optional.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The distinct keys the catalogue defines, in a stable order.</returns>
    /// <remarks>
    /// Paging is deliberately absent. The catalogue is small, bounded reference data seeded by the upgrade
    /// scripts, so the whole sequence is returned rather than a page of it.
    /// </remarks>
    [TenantOptional(
        "The unscoped permission catalogue is installation-wide reference data keyed by module definition; "
        + "no tenant resource is read.")]
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<string>>), StatusCodes.Status200OK)]
    // BASE ProblemDetails, not ValidationProblemDetails.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<string>>>> ListAsync(
        [FromQuery] string? permissionCode,
        [FromQuery] int? moduleDefinitionId,
        [FromQuery] PermissionKey? permissionKey,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<string>> outcome = await _permissions
            .GetPermissionKeysAsync(permissionCode, moduleDefinitionId, permissionKey, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its value
        // touched, and maps the failure code to a status through the one table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one catalogue definition by its identifier.</summary>
    /// <param name="permissionId">Identifier of the definition wanted, forwarded exactly as bound.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    /// <remarks>
    /// The catalogue carries no portal column, so this read is installation-wide like the listing. The
    /// portal-administrator policy still guards it, which is the same standing the legacy screens required
    /// of a caller reading the catalogue.
    /// </remarks>
    [TenantOptional(
        "A permission definition is installation-wide reference data and carries no tenant-owned resource.")]
    [HttpGet("{permissionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<PermissionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PermissionDto?>>> GetAsync(
        int permissionId,
        CancellationToken cancellationToken)
    {
        Result<PermissionDto?> outcome = await _permissions
            .GetPermissionAsync(permissionId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no value means the definition is absent, which the shared
        // translator answers as 404 - the convention every single-record read in this API follows.
        return this.Complete(outcome);
    }
}
