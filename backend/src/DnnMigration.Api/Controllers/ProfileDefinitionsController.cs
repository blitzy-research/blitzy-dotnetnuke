using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The profile property definition resource - which profile fields a portal collects, and how - exposed at
/// <c>/api/v1/profile-definitions</c>.
/// </summary>
/// <remarks>
/// <strong>Definitions are schema, not data.</strong> A user's profile is a set of values keyed by
/// definition, so changing a definition changes the shape of every profile in the portal and removing one
/// discards what every account recorded against it.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/profile-definitions")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class ProfileDefinitionsController : ControllerBase
{
    /// <summary>
    /// Failure code carried as the problem type when the request reached this action without a tenant.
    /// </summary>
    /// <remarks>
    /// A distinct code from the middleware's generic refusal, so an operator reading a support log can tell
    /// "this host resolves to no portal" apart from "this caller lacks the grant" - while the caller reads
    /// the same fixed wording either way and learns nothing from the difference.
    /// </remarks>
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>The application contract that owns profile definitions.</summary>
    /// <remarks>
    /// Definitions live on the account contract rather than on one of their own, because they are
    /// meaningless apart from the profiles they shape and every rule about them - name uniqueness, length,
    /// the cascade that discards values when a definition is removed - is a rule about accounts.
    /// </remarks>
    private readonly IUserService _users;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    /// <remarks>
    /// The flat address carries no tenant identifier and therefore reads the one the alias-resolution
    /// middleware already resolved.
    /// </remarks>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="ProfileDefinitionsController"/> class.</summary>
    /// <param name="users">The account service, which owns profile definitions.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host, which is the
    /// tenant the resource acts on.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public ProfileDefinitionsController(IUserService users, IPortalContextHolder portalContext)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the tenant the request resolved to.</summary>
    /// <returns>The tenant identifier, or <see langword="null"/> when the request resolved to no tenant.</returns>
    /// <remarks>
    /// The holder throws rather than yielding a placeholder tenant, so resolution is tested before the
    /// tenant is read, and the null answer here is a precondition rather than a state a caller can steer
    /// into.
    /// </remarks>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>Lists a portal's profile property definitions, in display order.</summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal's definitions, ordered by display order.</returns>
    /// <remarks>
    /// The sequence is ordered by the display-order column on the way out, so the client never sorts it and
    /// the order shown always matches the order stored.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProfilePropertyDefinitionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProfilePropertyDefinitionDto>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await _users
            .ListProfilePropertyDefinitionsAsync(scopedPortalId, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its value
        // touched, and maps any failure code through the one table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Retrieves one profile property definition.</summary>
    /// <param name="propertyDefinitionId">Identifier of the definition to read.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    [HttpGet("{propertyDefinitionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ProfilePropertyDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ProfilePropertyDefinitionDto?>>> GetAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ProfilePropertyDefinitionDto?> outcome = await _users
            .GetProfilePropertyDefinitionAsync(scopedPortalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Declares a new profile property definition for a portal.</summary>
    /// <param name="request">The definition to declare.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created definition, addressed by the location header.</returns>
    /// <remarks>
    /// This action binds a REQUEST contract rather than the response projection it returns. The response
    /// projection is not bound here, because it advertises an identifier, an owning portal and a visibility
    /// hint that this action does not read - members better absent from the request schema than present and
    /// ignored.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ProfilePropertyDefinitionDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<ProfilePropertyDefinitionDto>>> CreateAsync(
        [FromBody] CreateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ProfilePropertyDefinitionDto> outcome = await _users
            .CreateProfilePropertyDefinitionAsync(scopedPortalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.PropertyDefinitionId);
    }

    /// <summary>Updates an existing profile property definition, including its display order.</summary>
    /// <param name="propertyDefinitionId">Identifier of the definition to update.</param>
    /// <param name="request">The new state of the definition.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The definition as persisted.</returns>
    [HttpPut("{propertyDefinitionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ProfilePropertyDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<ProfilePropertyDefinitionDto>>> UpdateAsync(
        int propertyDefinitionId,
        [FromBody] UpdateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ProfilePropertyDefinitionDto> outcome = await _users
            .UpdateProfilePropertyDefinitionAsync(
                scopedPortalId,
                propertyDefinitionId,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a profile property definition from a portal.</summary>
    /// <param name="propertyDefinitionId">Identifier of the definition to remove.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> once the definition has been removed.</returns>
    /// <remarks>
    /// Removal discards the values accounts hold against the definition in the same unit of work, so no
    /// value is left referencing a definition that no longer exists. That cascade is the contract's
    /// responsibility and is deliberately not staged from here as a sequence of calls, which could leave
    /// the two halves apart if the second failed.
    /// </remarks>
    [HttpDelete("{propertyDefinitionId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    // The guarded commit in the application contract returns persistence.conflict when a concurrent request
    // changed or removed the definition first, and the shared translator answers that code as 409.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .DeleteProfilePropertyDefinitionAsync(scopedPortalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // The valueless overload answers 204 on success, which is the documented contract for a removal: the
        // resource is gone, so there is nothing to return in its place.
        return this.Complete(outcome);
    }
}
