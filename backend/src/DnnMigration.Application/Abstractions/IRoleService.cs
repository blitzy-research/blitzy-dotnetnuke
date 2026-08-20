using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Selects which roles of a portal a listing considers, where the choice cannot be expressed by a group
/// identifier alone.
/// </summary>
/// <remarks>
/// <para>
/// A value outside the enumeration is refused by model binding at the HTTP boundary, and this was MEASURED
/// rather than assumed: MVC's enumeration binder tests DEFINED membership for a non-flags enumeration, so
/// <c>?scope=999</c> is answered <c>400</c> naming the parameter before the action body runs, while
/// <c>?scope=0</c> and <c>?scope=1</c> bind normally.
/// </para>
/// <para>
/// <c>ListRolesAsync</c> nonetheless tests membership itself, and the reason is not belt-and-braces.
/// </para>
/// </remarks>
public enum RoleGroupScope
{
    /// <summary>Every role in the portal, whatever its grouping and including the ungrouped ones.</summary>
    All = 0,

    /// <summary>
    /// Only the roles that belong to no role group - the legacy "&lt; Global Roles &gt;" selection.
    /// </summary>
    /// <remarks>
    /// The legacy <c>-1</c> band, which reaches the store as a null-group test rather than as a comparison
    /// against minus one. This is the branch that had no expression at all before, and restoring it is the
    /// point of this type.
    /// </remarks>
    Ungrouped = 1,
}

/// <summary>
/// Application-layer contract for the role aggregate - DotNetNuke's permission grouping - together with
/// role groups and user-to-role assignment.
/// </summary>
/// <remarks>
/// <para>
/// Tenancy. Every member takes an explicit portal identifier and scopes every read and write to it.
/// </para>
/// <para>
/// Registration. Implemented by <c>Services/RoleService.cs</c> and registered by <c>AddApplication()</c> as
/// one of its seven scoped services.
/// </para>
/// </remarks>
public interface IRoleService
{
    /// <summary>
    /// Lists one page of the roles defined in a portal, optionally narrowed to a single role group.
    /// </summary>
    /// <param name="portalId">Identifier of the portal whose roles are listed.</param>
    /// <param name="request">Paging coordinates and the optional free-text filter for the listing.</param>
    /// <param name="roleGroupId">
    /// Identifier of the role group to restrict the listing to, or <see langword="null"/> to leave the
    /// choice to <paramref name="scope"/>.
    /// </param>
    /// <param name="scope">
    /// Which roles to consider when no group identifier is supplied - every role, or only the ungrouped
    /// ones.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying one page of roles, empty when the portal has none or when the
    /// requested page lies past the end of the set; a failed outcome carrying <c>portal.not_found</c> when
    /// no such portal exists, <c>role_group.not_found</c> when <paramref name="roleGroupId"/> is supplied
    /// but names no group in that portal, or <c>role_group.scope_invalid</c> in either of two cases - when
    /// <paramref name="scope"/> is not a defined member of <see cref="RoleGroupScope"/>, or when the two
    /// narrowing arguments contradict each other.
    /// </returns>
    /// <remarks>
    /// Supplying a group identifier together with <see cref="RoleGroupScope.Ungrouped"/> is a contradiction
    /// - the caller has asked for one group and for the roles in no group in the same breath - and is
    /// refused with <c>role_group.scope_invalid</c> rather than resolved by a precedence rule.
    /// </remarks>
    Task<Result<PagedResult<RoleListItemDto>>> ListRolesAsync(
        int portalId,
        PagedRequest request,
        int? roleGroupId,
        RoleGroupScope scope = RoleGroupScope.All,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one role of a portal in full, including its paid-membership terms.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role to read.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome whose value is the role, or a successful outcome whose value is <see
    /// langword="null"/> when the portal has no such role - absent is not the same answer as failed; or a
    /// failed outcome carrying <c>portal.not_found</c> when no such portal exists.
    /// </returns>
    Task<Result<RoleDetailDto?>> GetRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a role in a portal, including its paid-membership terms, and applies the legacy
    /// auto-assignment rule.
    /// </summary>
    /// <param name="portalId">Identifier of the portal that will own the role.</param>
    /// <param name="request">The role to create.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the created role, with its server-assigned identifier, so the caller
    /// can answer a create request with 201 and a location; or a failed outcome carrying
    /// <c>portal.not_found</c> when no such portal exists, <c>role_group.not_found</c> when a role group is
    /// named but does not exist in that portal, <c>role.name_duplicate</c> when the portal already has a
    /// role of that name, or <c>role.create_failed</c> when the store declines the insert.
    /// </returns>
    /// <remarks>
    /// The auto-assignment rule is preserved: L100 calls its private auto-assign helper immediately after a
    /// successful insert, so creating a role whose auto-assignment flag is set also enrols the portal's
    /// existing users in it. That work happens inside the implementation and is not a separate call.
    /// </remarks>
    Task<Result<RoleDetailDto>> CreateRoleAsync(
        int portalId,
        CreateRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing role of a portal, including its paid-membership terms.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role to update.</param>
    /// <param name="request">The replacement state for the role.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the updated role, so the caller can answer an update request with 200
    /// and the new state; or a failed outcome carrying <c>portal.not_found</c>, <c>role.not_found</c> when
    /// the portal has no such role, <c>role_group.not_found</c> when a role group is named but does not
    /// exist in that portal, or <c>role.name_duplicate</c> when the submitted name already belongs to a
    /// DIFFERENT role in the same portal.
    /// </returns>
    /// <remarks>
    /// The role's NAME is updatable, which is a documented behavioural difference from the legacy EDIT
    /// SCREEN and is itemised in <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    Task<Result<RoleDetailDto>> UpdateRoleAsync(
        int portalId,
        int roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a role from a portal together with its user assignments.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role to delete.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome when the role no longer exists, so the caller can answer a delete request with
    /// 204; or a failed outcome carrying <c>portal.not_found</c>, or <c>role.not_found</c> when the portal
    /// has no such role.
    /// </returns>
    Task<Result> DeleteRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one page of the users assigned to a role.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role whose members are listed.</param>
    /// <param name="request">Paging coordinates and the optional free-text filter.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying one page of members, empty when the role has none; or a failed outcome
    /// carrying <c>portal.not_found</c>, or <c>role.not_found</c> when the portal has no such role.
    /// </returns>
    /// <remarks>
    /// The legacy grid bound five columns, measured at
    /// <c>Website/admin/Security/securityroles.ascx:L71-L86</c>: the account key, the account's display
    /// name, the role name, the effective date and the expiry date.
    /// </remarks>
    Task<Result<PagedResult<RoleMembershipDto>>> ListRoleUsersAsync(
        int portalId,
        int roleId,
        PagedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads ONE account's membership of ONE role, addressed by both identifiers.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role.</param>
    /// <param name="userId">Identifier of the account.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the membership and the terms it runs on; a successful outcome carrying
    /// no value when the account holds no membership of that role, which the HTTP boundary reports as
    /// <c>404</c>; or a failed outcome carrying <c>portal.not_found</c>, <c>role.not_found</c> or
    /// <c>user.not_found</c>.
    /// </returns>
    /// <remarks>
    /// <b>Why an exact-identifier read exists beside the listing.</b> Its consumer asks a one-membership
    /// question — does THIS account hold THIS role, and on what terms — and the only address available for
    /// it was the paged listing narrowed by that listing's free-text filter.
    /// </remarks>
    Task<Result<RoleMembershipDto?>> GetRoleMembershipAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every role a user holds in a portal.</summary>
    /// <param name="portalId">Identifier of the portal the assignments belong to.</param>
    /// <param name="userId">Identifier of the user whose assignments are listed.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying every role the user holds in that portal, empty when the user holds
    /// none; or a failed outcome carrying <c>portal.not_found</c>, or <c>user.not_found</c> when the portal
    /// has no such user.
    /// </returns>
    Task<Result<IReadOnlyList<RoleListItemDto>>> ListUserRolesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns a user to a role, or revises the dates of an assignment the user already holds, computing
    /// the expiry from the role's trial and billing terms when the caller does not state one.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the assignment belongs to.</param>
    /// <param name="roleId">Identifier of the role to assign.</param>
    /// <param name="request">The user to assign, together with the optional effective and expiry dates.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome, which <c>RolesController.AssignUserAsync</c> answers with <c>204 No
    /// Content</c> at <c>POST /api/v1/roles/{roleId}/users</c>; or a failed outcome carrying
    /// <c>portal.not_found</c>, <c>role.not_found</c> or <c>user.not_found</c>.
    /// </returns>
    /// <remarks>
    /// The notification switch the legacy shared member carried is not reproduced, and neither is its
    /// dependency on the ambient per-request composite; see the annotations at the head of this file.
    /// </remarks>
    Task<Result> AssignUserToRoleAsync(
        int portalId,
        int roleId,
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from a role, enforcing the protected-assignment rule, and expiring rather than
    /// deleting an assignment whose paid trial has been used.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the assignment belongs to.</param>
    /// <param name="roleId">Identifier of the role to remove the user from.</param>
    /// <param name="userId">Identifier of the user to remove.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome when the user no longer holds the role, so the caller can answer a delete
    /// request with 204.
    /// </returns>
    Task<Result> RemoveUserFromRoleAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every role group defined in a portal.</summary>
    /// <param name="portalId">Identifier of the portal whose role groups are listed.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying every role group in the portal, empty when it has none; or a failed
    /// outcome carrying <c>portal.not_found</c>.
    /// </returns>
    Task<Result<IReadOnlyList<RoleGroupDto>>> ListRoleGroupsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one role group of a portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role group.</param>
    /// <param name="roleGroupId">Identifier of the role group to read.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome whose value is the role group, or a successful outcome whose value is <see
    /// langword="null"/> when the portal has no such group; or a failed outcome carrying
    /// <c>portal.not_found</c>.
    /// </returns>
    Task<Result<RoleGroupDto?>> GetRoleGroupAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a role group in a portal.</summary>
    /// <param name="portalId">Identifier of the portal that will own the role group.</param>
    /// <param name="request">The role group to create.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the created role group with its server-assigned identifier, so the
    /// caller can answer with 201 and a location; or a failed outcome carrying <c>portal.not_found</c>, or
    /// <c>role_group.name_duplicate</c> when the portal already has a group of that name.
    /// </returns>
    Task<Result<RoleGroupDto>> CreateRoleGroupAsync(
        int portalId,
        CreateRoleGroupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing role group of a portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role group.</param>
    /// <param name="roleGroupId">Identifier of the role group to update.</param>
    /// <param name="request">The replacement state for the role group.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome carrying the updated role group, so the caller can answer with 200 and the new
    /// state; or a failed outcome carrying <c>portal.not_found</c>, <c>role_group.not_found</c>, or
    /// <c>role_group.name_duplicate</c> when the new name is already taken by a different group in the same
    /// portal.
    /// </returns>
    Task<Result<RoleGroupDto>> UpdateRoleGroupAsync(
        int portalId,
        int roleGroupId,
        UpdateRoleGroupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a role group from a portal, refusing while it still classifies roles.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role group.</param>
    /// <param name="roleGroupId">Identifier of the role group to delete.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A successful outcome when the role group no longer exists, so the caller can answer with 204; or a
    /// failed outcome carrying <c>portal.not_found</c>, <c>role_group.not_found</c>, or
    /// <c>role_group.in_use</c> when the group still classifies at least one role.
    /// </returns>
    Task<Result> DeleteRoleGroupAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken = default);
}
