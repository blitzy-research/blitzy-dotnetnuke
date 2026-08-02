using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <see cref="Role"/> aggregate, its groups and its user assignments.
/// </summary>
/// <remarks>
/// MIGRATION: the twenty-two role procedures this contract replaces live in the membership provider
/// stack; the core data provider contains none. Assignment effective and expiry dates are preserved
/// because the legacy paid-membership rules depend on them.
/// </remarks>
public interface IRoleRepository
{
    /// <summary>Returns one page of a portal's roles.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="roleGroupId">Restrict to one role group, or <see langword="null"/> for every group.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pageSize">Page size; 0 requests every match unpaged.</param>
    /// <param name="nameFilter">Case-insensitive substring of the role name, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PagedResult<Role>> ListAsync(
        int portalId,
        int? roleGroupId,
        int pageIndex,
        int pageSize,
        string? nameFilter,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one role by key, or <see langword="null"/>.</summary>
    /// <param name="roleId">Role identifier. 0 is legitimate: <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Role?> GetAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>Returns one role by name within a portal, or <see langword="null"/>.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="roleName">Role name, matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Role?> GetByNameAsync(int portalId, string roleName, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a role name is already used within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="roleName">The role name to test.</param>
    /// <param name="excludingRoleId">A role to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> RoleNameExistsAsync(int portalId, string roleName, int? excludingRoleId = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the roles that are automatically granted to every new member of a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Role>> ListAutoAssignedAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new role for insertion.</summary>
    /// <param name="role">The role to insert.</param>
    void Add(Role role);

    /// <summary>Stages a role for deletion.</summary>
    /// <param name="role">The role to delete.</param>
    void Remove(Role role);

    /// <summary>Returns one page of the users assigned to a role.</summary>
    /// <param name="roleId">Role identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pageSize">Page size; 0 requests every match unpaged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PagedResult<UserRole>> ListAssignmentsAsync(
        int roleId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every role assignment held by one user within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<UserRole>> ListUserAssignmentsAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns one role assignment, or <see langword="null"/> when the user does not hold the role.</summary>
    /// <param name="roleId">Role identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserRole?> GetAssignmentAsync(int roleId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new role assignment for insertion.</summary>
    /// <param name="assignment">The assignment to insert.</param>
    void AddAssignment(UserRole assignment);

    /// <summary>Stages a role assignment for deletion.</summary>
    /// <param name="assignment">The assignment to delete.</param>
    void RemoveAssignment(UserRole assignment);

    /// <summary>Returns a portal's role groups, in name order.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RoleGroup>> ListGroupsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns one role group by key, or <see langword="null"/>.</summary>
    /// <param name="roleGroupId">Role group identifier. 0 is legitimate: <c>RoleGroups.RoleGroupID</c> is <c>IDENTITY(0, 1)</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RoleGroup?> GetGroupAsync(int roleGroupId, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a role group name is already used within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="roleGroupName">The role group name to test.</param>
    /// <param name="excludingRoleGroupId">A group to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> GroupNameExistsAsync(
        int portalId,
        string roleGroupName,
        int? excludingRoleGroupId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new role group for insertion.</summary>
    /// <param name="roleGroup">The role group to insert.</param>
    void AddGroup(RoleGroup roleGroup);

    /// <summary>Stages a role group for deletion.</summary>
    /// <param name="roleGroup">The role group to delete.</param>
    void RemoveGroup(RoleGroup roleGroup);

    /// <summary>
    /// Reports whether an account currently holds one specific role of one specific portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both identifiers are required, and requiring both is what makes the answer tenant-safe. Role
    /// names are not unique in this schema - <c>Roles</c> carries a primary key over <c>RoleID</c>
    /// alone, with no unique constraint or unique index on <c>RoleName</c> anywhere in the
    /// eighty-eight upgrade scripts - so every portal may legitimately hold a role called
    /// "Administrators". Asking by name would therefore let one tenant's membership answer another
    /// tenant's question. Asking by role identifier, and additionally requiring that the role belong
    /// to the named portal, cannot.
    /// </para>
    /// <para>
    /// The portal condition is an equality on the role's owning portal, which is what the legacy
    /// procedure wrote (<c>Roles.PortalId = @PortalId</c>). A role whose owning portal is absent is
    /// therefore never a match here, exactly as it was never returned by the legacy procedure. Note
    /// that the sibling legacy procedure <c>GetPortalRoles</c> deliberately differs - its terminal
    /// form at <c>04.08.00.SqlDataProvider</c> line 40 admits a role with no owning portal - so the
    /// two must not be conflated.
    /// </para>
    /// <para>
    /// The validity window is applied exactly as the legacy procedure applied it: membership counts
    /// when its start is absent or has passed, and when its end is absent or has not passed. An
    /// absent bound means unbounded in that direction, never "now".
    /// </para>
    /// <para>
    /// The instant to evaluate against is supplied by the caller rather than read inside an
    /// implementation, so that a decision is reproducible and can be tested at a chosen moment.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy procedure compared against <c>getdate()</c>, which is the database
    /// server's local time, and legacy rows were written in local time to match. This contract is
    /// specified in coordinated universal time, because every date this solution writes comes from
    /// its own clock abstraction in that form. Where a legacy database was written on a server whose
    /// local time is not universal time, the window shifts by that server's offset. The divergence
    /// is recorded in MIGRATION_NOTES.md rather than absorbed silently.
    /// </para>
    /// </remarks>
    /// <param name="userId">
    /// Numeric key of the account whose membership is in question.
    /// </param>
    /// <param name="roleId">
    /// Numeric key of the role the account must hold. Role keys are seeded at zero in this schema
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 115), so
    /// zero denotes a real role and must not be read as an absence marker.
    /// </param>
    /// <param name="portalId">
    /// Numeric key of the portal the role must belong to. Portal keys are seeded at minus one in this
    /// schema, so both minus one and zero denote real portals and neither may be read as absence.
    /// </param>
    /// <param name="asOfUtc">
    /// The instant, in coordinated universal time, at which membership is being asked about.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagates notification that the operation should be abandoned.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a stored assignment matches the account, the role and the owning
    /// portal and is valid at the supplied instant; otherwise <see langword="false"/>. There is no
    /// third answer: an implementation reports absence of a matching assignment rather than failing,
    /// because "not a member" is an ordinary outcome and not an error.
    /// </returns>
    Task<bool> IsUserInPortalRoleAsync(
        int userId,
        int roleId,
        int portalId,
        DateTime asOfUtc,
        CancellationToken cancellationToken);
}
