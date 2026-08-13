using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// The persistence contract for the Role aggregate: security roles, the groups that organise them and the
/// assignments that place user accounts into them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are staged, never committed.</b> No member of this contract writes to the database. Each add,
/// update and delete records an intention, and the transaction is closed exactly once by <see
/// cref="IUnitOfWork.SaveChangesAsync"/>.
/// </para>
/// <para>
/// <b>Identity keys are opaque.</b> Three different identity seeds meet in this contract and none of the
/// reserved-looking values means "absent": <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>
/// (01.00.00.SqlDataProvider:L115), <c>RoleGroups.RoleGroupID</c> is <c>IDENTITY(0, 1)</c>
/// (04.00.04.SqlDataProvider:L51), <c>UserRoles.UserRoleID</c> is <c>IDENTITY(1, 1)</c>
/// (01.00.00.SqlDataProvider:L239) and <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
/// (01.00.00.SqlDataProvider:L77).
/// </para>
/// </remarks>
public interface IRoleRepository
{
    /// <summary>Returns every role visible to one portal, in name order.</summary>
    /// <param name="portalId">The portal whose roles are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The matching roles, ordered by name; an empty list when the portal owns none and the installation
    /// defines none.
    /// </returns>
    Task<IReadOnlyList<Role>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of the roles a portal OWNS, filtered, ordered, counted and windowed by the store.
    /// </summary>
    /// <param name="portalId">The owning portal, matched by STRICT equality.</param>
    /// <param name="roleGroupId">
    /// Restrict to one role group, or <see langword="null"/> for no group restriction. <c>RoleGroupID</c>
    /// is <c>IDENTITY(0, 1)</c>, so the PRESENCE of a value selects the filter and its magnitude never
    /// does.
    /// </param>
    /// <param name="ungroupedOnly">
    /// Restrict to the roles belonging to no group at all - the legacy "global roles" selection.
    /// </param>
    /// <param name="nameQuery">
    /// A fragment the role name must contain, case-insensitively, or <see langword="null"/> for no name
    /// restriction.
    /// </param>
    /// <param name="sortBy">The role property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <param name="pageIndex">The page to return, counted from zero.</param>
    /// <param name="pageSize">The page width, or zero for every matching row.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The requested window together with the total number of roles the whole filtered set holds.</returns>
    /// <remarks>
    /// MIGRATION: net-new, and it replaces an Application-layer composition rather than a legacy procedure
    /// - the legacy membership provider's entire role-listing surface was <c>GetPortalRoles(PortalId)</c>,
    /// which returned every row, and the group restriction, the name search and the page were the admin
    /// screen's own work.
    /// </remarks>
    Task<PagedResult<Role>> ListAsync(
        int portalId,
        int? roleGroupId,
        bool ungroupedOnly,
        string? nameQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every role in the installation, across all portals.</summary>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>Every stored role; an empty list when the installation defines none.</returns>
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one role by key within a portal, or <see langword="null"/> when the portal has no such role.
    /// </summary>
    /// <param name="roleId">The role key.</param>
    /// <param name="portalId">The portal the role must belong to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The role, or <see langword="null"/> when no stored role has that key within that portal.</returns>
    Task<Role?> GetByIdAsync(int roleId, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one role by name within a portal, or <see langword="null"/> when the portal has no role of
    /// that name.
    /// </summary>
    /// <remarks>
    /// Because the answer is at most one row, this member also settles the uniqueness question a caller
    /// must ask before storing a name: a non-null answer means the name is taken, and an answer whose key
    /// differs from the role being edited means it is taken by somebody else. No separate existence member
    /// is provided, because that is the same query with the row discarded.
    /// </remarks>
    /// <param name="portalId">The portal to search within.</param>
    /// <param name="roleName">The name to match.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The role, or <see langword="null"/> when that portal has no role of that name.</returns>
    Task<Role?> GetByNameAsync(int portalId, string roleName, CancellationToken cancellationToken = default);

    /// <summary>Stages a new role for insertion.</summary>
    /// <param name="role">The role to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing role's modifications for update.</summary>
    /// <param name="role">The role whose stored row is to be brought into line with it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>Stages a role for deletion by key.</summary>
    /// <param name="roleId">The key of the role to delete.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the deletion has been staged.</returns>
    Task DeleteAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the roles that one user account holds within one portal.</summary>
    /// <param name="userId">The account whose roles are wanted.</param>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The roles held within that portal; an empty list when the account holds none there.</returns>
    Task<IReadOnlyList<Role>> GetRolesByUserIdAsync(int userId, int portalId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new role group for insertion.</summary>
    /// <param name="roleGroup">The group to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing role group's modifications for update.</summary>
    /// <param name="roleGroup">The group whose stored row is to be brought into line with it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default);

    /// <summary>Stages a role group for deletion by key.</summary>
    /// <remarks>
    /// A group cannot be discarded while a role still points at it. <c>FK_Roles_RoleGroups</c> carries no
    /// cascade clause, unlike <c>FK_Roles_Portals</c>, so the store itself refuses the deletion and the
    /// caller is expected to clear or reassign the group's roles first. That constraint is part of the
    /// schema this migration binds to and is deliberately not worked around here.
    /// </remarks>
    /// <param name="roleGroupId">The key of the group to delete.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the deletion has been staged.</returns>
    Task DeleteRoleGroupAsync(int roleGroupId, CancellationToken cancellationToken = default);

    /// <summary>Returns one role group by key within a portal, or <see langword="null"/>.</summary>
    /// <param name="portalId">The portal the group must belong to.</param>
    /// <param name="roleGroupId">The group key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The group, or <see langword="null"/> when that portal has no group with that key.</returns>
    Task<RoleGroup?> GetRoleGroupAsync(int portalId, int roleGroupId, CancellationToken cancellationToken = default);

    /// <summary>Returns every role group belonging to one portal.</summary>
    /// <param name="portalId">The portal whose groups are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The portal's groups; an empty list when it defines none, which is the common case.</returns>
    Task<IReadOnlyList<RoleGroup>> GetRoleGroupsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the roles that belong to one role group within one portal.</summary>
    /// <param name="roleGroupId">The group whose roles are wanted.</param>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The roles in that group; an empty list when the group is empty or unknown.</returns>
    Task<IReadOnlyList<Role>> GetRolesByGroupAsync(int roleGroupId, int portalId, CancellationToken cancellationToken = default);

    // Dbo.UserRoles CARRIES NO PORTAL COLUMN. Its terminal columns are UserRoleID (IDENTITY(1,1)), UserID,
    // RoleID, ExpiryDate, IsTrialUsed and the later EffectiveDate added at 03.02.03.SqlDataProvider:L380 -
    // and no PortalID among them.

    /// <summary>
    /// Returns one user account's assignment to one role within one portal, or <see langword="null"/> when
    /// the account does not hold that role.
    /// </summary>
    /// <param name="portalId">The portal whose scope the assignment's role must fall within.</param>
    /// <param name="userId">The account whose assignment is wanted.</param>
    /// <param name="roleId">The role the assignment must be to.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The assignment, or <see langword="null"/> when no stored assignment matches.</returns>
    Task<UserRole?> GetUserRoleAsync(int portalId, int userId, int roleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every role assignment one user account holds within one portal.</summary>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="userId">The account whose assignments are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The account's assignments within that portal; an empty list when it holds none there.</returns>
    Task<IReadOnlyList<UserRole>> GetUserRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns role assignments within one portal, narrowed by account login name, by role name, by both,
    /// or by neither.
    /// </summary>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="username">
    /// The login name of the account whose assignments are wanted, or <see langword="null"/> for the
    /// assignments of every account in the portal.
    /// </param>
    /// <param name="roleName">The single role to narrow to, or <see langword="null"/> for every role.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The matching assignments; an empty list when there are none.</returns>
    Task<IReadOnlyList<UserRole>> GetUserRolesByUsernameAsync(int portalId, string? username, string? roleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of one role's assignments, filtered, ordered, counted and windowed by the store.
    /// </summary>
    /// <param name="portalId">The portal to confine the answer to.</param>
    /// <param name="roleName">The single role whose assignments are wanted, matched case-insensitively.</param>
    /// <param name="accountQuery">
    /// A fragment that the account's display name OR its login name must contain, case-insensitively, or
    /// <see langword="null"/> for no account restriction.
    /// </param>
    /// <param name="sortBy">The account property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <param name="pageIndex">The page to return, counted from zero.</param>
    /// <param name="pageSize">The page width, or zero for every matching row.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The requested window together with the total number of assignments the whole filtered set holds.
    /// </returns>
    /// <remarks>
    /// MIGRATION: net-new, for the same reason as <see cref="ListAsync"/>.
    /// </remarks>
    Task<PagedResult<UserRole>> ListRoleMembershipsAsync(
        int portalId,
        string roleName,
        string? accountQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new role assignment for insertion.</summary>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L112 AddUserRole(PortalID, UserId, RoleId, EffectiveDate,
    /// ExpiryDate)</c>. The portal argument is dropped for the reason given in the migration note above,
    /// and the remaining four values are carried by the entity.
    /// </remarks>
    /// <param name="userRole">The assignment to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing role assignment's modifications for update.</summary>
    /// <remarks>
    /// As on <see cref="AddUserRoleAsync"/>, every value is staged exactly as supplied.
    /// </remarks>
    /// <param name="userRole">The assignment whose stored row is to be brought into line with it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of one user account's assignment to one role.</summary>
    /// <remarks>
    /// Removal is not the only way a membership ends, and the two are not interchangeable. The legacy
    /// cancellation path deliberately expired an assignment instead of deleting it whenever a trial had
    /// been consumed, so that the consumed-trial fact survived; a caller reproducing that behaviour uses
    /// <see cref="UpdateUserRoleAsync"/>.
    /// </remarks>
    /// <param name="userId">The account whose assignment is to be removed.</param>
    /// <param name="roleId">The role to remove it from.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteUserRoleAsync(int userId, int roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal's publicly available roles - those a user account may subscribe itself to.
    /// </summary>
    /// <param name="portalId">The portal whose subscribable roles are wanted.</param>
    /// <param name="userId">The account the offers are being listed for.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The portal's public roles; an empty list when it publishes none.</returns>
    Task<IReadOnlyList<Role>> GetSubscribableRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default);
}
