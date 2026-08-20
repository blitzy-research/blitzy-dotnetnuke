using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role group resource - the tenant-owned classification a role may be filed under - exposed at
/// <c>/api/v1/role-groups</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this type does, and the complete list.</strong> It binds a request, delegates to <see
/// cref="IRoleService"/>, and translates the returned outcome into a status code. Nothing else.
/// </para>
/// <para>
/// <strong>Why the flat route remains tenant-bound.</strong> A role group is unconditionally tenant-owned:
/// <c>RoleGroups.PortalID</c> is declared <c>NOT NULL</c> and the uniqueness constraint on the table is
/// <c>UNIQUE ([PortalID] ASC, [RoleGroupName] ASC)</c> (measured at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider:L49-L62</c>, where the table
/// reaches its terminal shape; it first appears at <c>03.02.03.SqlDataProvider:L16</c> and not, as might be
/// assumed, in the baseline script).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/role-groups")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class RoleGroupsController : ControllerBase
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

    /// <summary>The application contract that owns role groups.</summary>
    /// <remarks>
    /// Role groups live on the role contract rather than on one of their own, and deliberately so: every
    /// rule about a group is a rule about the roles it classifies - the per-portal name uniqueness the
    /// table enforces, and the refusal to remove a group while it still classifies a role. There is no
    /// <c>IRoleGroupService</c> to inject.
    /// </remarks>
    private readonly IRoleService _roles;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    /// <remarks>
    /// Needed only by the flat address, which carries no tenant identifier and therefore has to read the
    /// one the alias-resolution middleware already resolved. It is an abstraction over that resolution
    /// rather than a reach for the ambient HTTP context: this file still performs no alias lookup and still
    /// touches no request feature bag.
    /// </remarks>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="RoleGroupsController"/> class.</summary>
    /// <param name="roles">The role service, which owns role groups.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host, which is the
    /// tenant the flat address acts on.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public RoleGroupsController(IRoleService roles, IPortalContextHolder portalContext)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the tenant the request resolved to.</summary>
    /// <returns>The tenant identifier, or <see langword="null"/> when the request resolved to no tenant.</returns>
    /// <remarks>
    /// The holder is read rather than a placeholder returned, and it throws instead of yielding one, so
    /// resolution is tested first, and the null answer here is a precondition rather than a state a caller
    /// can steer into.
    /// </remarks>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>Lists every role group a portal defines.</summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal's role groups.</returns>
    /// <remarks>
    /// The <c>404</c> above reports an unknown PORTAL, not an empty result. Tenant scoping is part of the
    /// question the contract answers, which is what makes asking about a portal that does not exist
    /// different from asking about a portal that has no groups.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RoleGroupDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleGroupDto>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<RoleGroupDto>> outcome = await _roles
            .ListRoleGroupsAsync(scopedPortalId, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never has
        // its value touched - reading the value of a failed outcome throws by design - and any failure code
        // is mapped through the single table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one role group.</summary>
    /// <param name="roleGroupId">Identifier of the role group to read.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The role group.</returns>
    [HttpGet("{roleGroupId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RoleGroupDto?>>> GetAsync(
        int roleGroupId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleGroupDto?> outcome = await _roles
            .GetRoleGroupAsync(scopedPortalId, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a role group in a portal.</summary>
    /// <param name="request">
    /// The role group to create: its name and an optional description. Those are the only two values the
    /// legacy editor's inputs supplied (<c>EditGroups.ascx:L11</c> and <c>:L17</c>) and the only two this
    /// path honours, so they are the only two the contract declares. The group's identifier is assigned by
    /// the store - which is precisely why the method rather than a sentinel distinguishes this from an
    /// update - and the owning portal arrives through the resolved request context, so neither is
    /// expressible in the body.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created role group, addressed by the location header.</returns>
    /// <response code="201">
    /// The role group as persisted, including the identifier the database assigned, with its address in
    /// the location header.
    /// </response>
    /// <response code="400">
    /// The body was absent or malformed, or a rule refused it - a group name is required and may not
    /// exceed 50 characters, matching the required-field validator and the <c>maxlength="50"</c> on the
    /// legacy editor's name box (<c>EditGroups.ascx:L11-L12</c>), and a description may not exceed 1000
    /// (<c>:L17</c>). Those rules are declared by <c>CreateRoleGroupRequestValidator</c>, which the
    /// globally registered validation filter resolves and applies before this action body runs, so the
    /// answer is an RFC 7807 validation document naming each offending member. A model-binding failure
    /// takes the same shape through the automatic model-state check. The application service re-asserts
    /// the name rules for callers that do not arrive over HTTP.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <response code="409">
    /// The portal already has a role group of that name. Names are unique within a portal, which the
    /// schema itself enforces through <c>UNIQUE ([PortalID] ASC, [RoleGroupName] ASC)</c>, so this is a
    /// collision with existing state that the caller can correct by choosing another name.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: this action is the whole of the legacy add path. The editor chose to add by testing its
    /// identifier field against -1 (<c>EditGroups.ascx.vb:L113</c>); here POST expresses the intent, and
    /// the contract's duplicate-name code expresses the collision, which the shared translator answers
    /// with <c>409</c>. Neither the intent nor the failure travels inside an integer any more.
    /// </para>
    /// <para>
    /// MIGRATION: the duplicate is reported as a reason code rather than discovered by catching an
    /// exception. The legacy screen wrapped the call in a bare <c>Catch</c> and mapped ANY thrown error
    /// onto its localised message, "A role group with the same name already exists. The new group was not
    /// added." (<c>EditGroups.ascx.vb:L114-L119</c> with the wording from
    /// <c>App_LocalResources/EditGroups.ascx.resx</c>). That conflated a name collision with every other
    /// possible failure, including a database being unreachable. The two are now distinct: a collision is
    /// <c>409</c> carrying the collision's own problem type, and an unanticipated failure reaches the
    /// global exception handler instead.
    /// </para>
    /// <para>
    /// MIGRATION: the location header is derived from this request's own path plus the assigned
    /// identifier rather than from a named-route lookup, so the address is spelled in exactly one place.
    /// That is the shared creation translator's documented behaviour and it yields
    /// <c>/api/v1/role-groups/{roleGroupId}</c> for this endpoint - the address of the by-identifier read
    /// above. The legacy screen had no equivalent: it answered a successful add with a
    /// redirect back to the grid (<c>EditGroups.ascx.vb:L120</c>), discarding the new identifier.
    /// </para>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<RoleGroupDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RoleGroupDto>>> CreateAsync(
        [FromBody] CreateRoleGroupRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleGroupDto> outcome = await _roles
            .CreateRoleGroupAsync(scopedPortalId, request, cancellationToken)
            .ConfigureAwait(false);

        // The identifier is read from the persisted representation rather than from the submitted body,
        // because the store assigns it, and it is read only on the success path: the translator checks the
        // outcome first.
        return this.Created(outcome, created => created.RoleGroupId);
    }

    /// <summary>Updates an existing role group.</summary>
    /// <param name="roleGroupId">Identifier of the role group to update.</param>
    /// <param name="request">The replacement state for the role group: its name and an optional description.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The role group as persisted.</returns>
    /// <remarks>
    /// MIGRATION - behavioural difference, deliberately introduced. The legacy save wrapped ONLY its add
    /// branch in a <c>Try</c>; the update branch at <c>EditGroups.ascx.vb:L122</c> had no duplicate
    /// handling whatsoever, so renaming a group onto an existing name violated the table's unique
    /// constraint and surfaced as an unhandled provider error through the module's load-exception path.
    /// </remarks>
    [HttpPut("{roleGroupId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RoleGroupDto>>> UpdateAsync(
        int roleGroupId,
        [FromBody] UpdateRoleGroupRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleGroupDto> outcome = await _roles
            .UpdateRoleGroupAsync(scopedPortalId, roleGroupId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a role group from a portal.</summary>
    /// <param name="roleGroupId">Identifier of the role group to remove.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> once the role group has been removed.</returns>
    /// <remarks>
    /// The valueless overload of the shared translator answers <c>204</c> on success, which is the
    /// documented contract for a removal: the resource is gone, so there is nothing to return in its place.
    /// </remarks>
    [HttpDelete("{roleGroupId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteAsync(
        int roleGroupId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .DeleteRoleGroupAsync(scopedPortalId, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
