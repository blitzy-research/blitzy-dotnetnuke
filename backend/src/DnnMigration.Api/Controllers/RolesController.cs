using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role resource - DotNetNuke's permission grouping - together with the membership that joins an
/// account to a role, exposed at <c>/api/v1/roles</c>, <c>/api/v1/roles/{roleId}/users</c> and the
/// account-side <c>/api/v1/users/{userId}/roles</c> projection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes are flat and the tenant is resolved from the request host, not from the path.</b> The class
/// route is the version prefix alone and each action names its own collection - <c>roles</c>,
/// <c>roles/{roleId}/users</c>, <c>users/{userId}/roles</c> - so no address carries a portal segment. Every
/// member of <see cref="IRoleService"/> nevertheless takes an explicit portal identifier and scopes every
/// read and write to it; the value comes from the injected <see cref="IPortalContextHolder"/>, which
/// <c>PortalAliasResolutionMiddleware</c> settles from the request's host and alias. An unresolved tenant is
/// refused with <c>portal.tenant_unresolved</c>, and a role belonging to another portal is reported absent
/// rather than returned, which is how the legacy screens treated a cross-tenant identifier.
/// </para>
/// <para>
/// <b>Ten actions</b>, all gated by <see cref="PolicyNames.PortalAdministrator"/> at class level: five on
/// the role collection and its members (list, read, create, update, delete), one listing a role's
/// membership, one projecting the roles an account holds, and three on a single membership (create, read,
/// delete).
/// </para>
/// <para>
/// MIGRATION: the legacy editor decided between inserting and updating by testing its role identifier
/// against -1. That discriminator is deliberately not carried forward: <c>POST</c> to the collection
/// creates and <c>PUT</c> on a member address updates, so the method IS the discriminator.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
public sealed class RolesController : ControllerBase
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

    /// <summary>The application contract that owns roles, role groups and membership.</summary>
    private readonly IRoleService _roles;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    /// <remarks>
    /// Needed only by the flat addresses, which carry no tenant identifier and therefore have to read the
    /// one the alias-resolution middleware already resolved.
    /// </remarks>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="RolesController"/> class.</summary>
    /// <param name="roles">The role service.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host, which is the
    /// tenant the flat addresses act on.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public RolesController(IRoleService roles, IPortalContextHolder portalContext)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
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

    /// <summary>Lists one page of the roles a portal defines.</summary>
    /// <param name="request">Paging, sorting and free-text filtering, bound from the query string.</param>
    /// <param name="roleGroupId">Restricts the listing to one role group.</param>
    /// <param name="scope">
    /// Chooses between the two answers a group identifier cannot express: <c>All</c>, every role in the
    /// portal, which is the successor to the legacy "&lt; All Roles &gt;" selector entry and the default
    /// when the parameter is omitted; and <c>Ungrouped</c>, only the roles belonging to no group at all,
    /// which is the successor to its "&lt; Global Roles &gt;" entry.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// One page of the portal's roles, in the wire envelope: an <c>items</c> array of rows and a
    /// <c>meta</c> object carrying the total across every page, the page index and the page size.
    /// </returns>
    /// <remarks>
    /// The third intent - the legacy drop-down's "&lt; Global Roles &gt;" entry, the roles belonging to no
    /// group - is reachable without letting -1 travel as a magic value, which would restore the collision
    /// in which one integer meant an absent value, a stored identifier and a filtering intent at once.
    /// </remarks>
    [HttpGet("roles")]
    [ProducesResponseType(typeof(PagedResponse<RoleListItemDto>), StatusCodes.Status200OK)]
    // BASE ProblemDetails, not ValidationProblemDetails. Both shapes are reachable: the binder and the
    // paging validator name the offending member, while the contradiction between a group identifier and
    // the ungrouped scope is refused with a failure code and no single member to blame.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<RoleListItemDto>>> ListAsync(
        [FromQuery] RolePagedRequest request,
        [FromQuery] int? roleGroupId,
        [FromQuery] RoleGroupScope scope,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // An omitted scope binds to the enumeration's zero member, which is All - the same answer this
        // endpoint gave before the parameter existed - so no existing caller changes behaviour.
        Result<PagedResult<RoleListItemDto>> outcome = await _roles
            .ListRolesAsync(scopedPortalId, request, roleGroupId, scope, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never has
        // its value touched - reading the value of a failed outcome throws by design - and any failure code
        // is mapped to a status through the single table this API uses.
        return this.CompletePage(outcome);
    }

    /// <summary>Reads one role.</summary>
    /// <param name="roleId">Identifier of the role to read.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The role, or an indication that it does not exist in this portal.</returns>
    [HttpGet("roles/{roleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RoleDetailDto?>>> GetAsync(
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleDetailDto?> outcome = await _roles
            .GetRoleAsync(scopedPortalId, roleId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no value means the role is absent, which is a different answer
        // from a failed lookup; the shared translator turns the first into 404 and maps the second.
        return this.Complete(outcome);
    }

    /// <summary>Creates a role in a portal.</summary>
    /// <param name="request">
    /// The role to create: its name, description, optional role group, visibility and automatic-enrolment
    /// switches, invitation code, icon, and the paid-membership terms - a service fee with a billing period
    /// and frequency, and a trial fee with a trial period and frequency.
    /// </param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>The created role, addressed by the location header.</returns>
    [HttpPost("roles")]
    [ProducesResponseType(typeof(ApiResponse<RoleDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<RoleDetailDto>>> CreateAsync(
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleDetailDto> outcome = await _roles
            .CreateRoleAsync(scopedPortalId, request, cancellationToken)
            .ConfigureAwait(false);

        // The location header is built from this request's own path plus the new identifier, which is
        // exactly the member address of the collection just posted to, so the address is spelled once.
        return this.Created(outcome, created => created.RoleId);
    }

    /// <summary>Updates a role's writable state.</summary>
    /// <param name="roleId">Identifier of the role to update.</param>
    /// <param name="request">
    /// The new state: name, description, role group, visibility and automatic-enrolment switches,
    /// invitation code, icon, and the billing and trial terms.
    /// </param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>The updated role.</returns>
    /// <remarks>
    /// MIGRATION - documented behavioural difference, itemised in <c>MIGRATION_NOTES.md</c>. This path can
    /// rename a role, where the legacy edit screen could not: it revealed the name as a read-only label and
    /// disabled its only required-field validator, and the terminal stored procedure omits the column from
    /// its assignment list (<c>04.00.04.SqlDataProvider:L454</c>).
    /// </remarks>
    [HttpPut("roles/{roleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RoleDetailDto>>> UpdateAsync(
        int roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleDetailDto> outcome = await _roles
            .UpdateRoleAsync(scopedPortalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a role.</summary>
    /// <param name="roleId">Identifier of the role to delete.</param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>An empty response when the role has been removed.</returns>
    [HttpDelete("roles/{roleId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .DeleteRoleAsync(scopedPortalId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists one page of the accounts that hold a role.</summary>
    /// <param name="roleId">Identifier of the role whose members are listed.</param>
    /// <param name="request">
    /// Paging, sorting and free-text filtering, bound from the query string, on the same zero-based,
    /// match-from-the-start terms as every other listing in this API.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// One page of the accounts holding the role, in the wire envelope: an <c>items</c> array of rows and a
    /// <c>meta</c> object carrying the total across every page, the page index and the page size.
    /// </returns>
    /// <remarks>
    /// Consequently the record is a membership and not an account: it carries the account's key, login name
    /// and display name, the role's key and name, and the two dates. The remaining account fields -
    /// electronic mail address, approval, lock-out and the rest - are not here, because the legacy grid
    /// rendered none of them and the account endpoints already publish them.
    /// </remarks>
    [HttpGet("roles/{roleId:int}/users")]
    [ProducesResponseType(typeof(PagedResponse<RoleMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<RoleMembershipDto>>> ListUsersAsync(
        int roleId,
        [FromQuery] RoleUserPagedRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<PagedResult<RoleMembershipDto>> outcome = await _roles
            .ListRoleUsersAsync(scopedPortalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Lists the roles one account holds in a portal.</summary>
    /// <param name="userId">Identifier of the account whose roles are listed.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The roles the account holds in this portal.</returns>
    [HttpGet("users/{userId:int}/roles")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RoleListItemDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleListItemDto>>>> ListForUserAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<RoleListItemDto>> outcome = await _roles
            .ListUserRolesAsync(scopedPortalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Grants an account a role, for a period.</summary>
    /// <param name="roleId">Identifier of the role being granted.</param>
    /// <param name="request">
    /// The account to enrol, with the optional dates the membership takes effect and ceases, and the
    /// caller's request that the account be notified.
    /// </param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>An empty response when the membership has been recorded.</returns>
    /// <remarks>
    /// HOW AN ABSENT DATE LEAVES ON THE WAY BACK OUT. Serialisation is configured once with
    /// <c>JsonIgnoreCondition.Never</c> (<c>ServiceCollectionExtensions.cs</c>, both the minimal-API and
    /// controller surfaces), so a <c>null</c> date IS written, as <c>"effectiveDate": null</c>, and the
    /// member is never omitted.
    /// </remarks>
    [HttpPost("roles/{roleId:int}/users")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AssignUserAsync(
        int roleId,
        [FromBody] RoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .AssignUserToRoleAsync(scopedPortalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Reads one account's membership of one role.</summary>
    /// <param name="roleId">Identifier of the role.</param>
    /// <param name="userId">Identifier of the account.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The membership and the terms it runs on.</returns>
    [HttpGet("roles/{roleId:int}/users/{userId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RoleMembershipDto?>>> GetUserMembershipAsync(
        int roleId,
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleMembershipDto?> outcome = await _roles
            .GetRoleMembershipAsync(scopedPortalId, roleId, userId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no membership is translated to 404 by the shared translator, which
        // is where "there is no such thing" becomes a status code. Reading the value without testing the
        // outcome first would throw on a refusal and turn a clean denial into a server fault.
        return this.Complete(outcome);
    }

    /// <summary>Removes an account's membership of a role.</summary>
    /// <param name="roleId">Identifier of the role the account is being removed from.</param>
    /// <param name="userId">Identifier of the account being removed.</param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>An empty response when the membership has been removed.</returns>
    [HttpDelete("roles/{roleId:int}/users/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveUserAsync(
        int roleId,
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .RemoveUserFromRoleAsync(scopedPortalId, roleId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
