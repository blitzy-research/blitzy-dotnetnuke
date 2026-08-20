using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the permission aggregate: the catalogue of permissions, the grants recorded against a
/// module instance, and the grants recorded against a page.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three entities, no inheritance.</strong> <see cref="ModulePermission"/> and <see
/// cref="TabPermission"/> do not derive from <see cref="Permission"/>. Each is an independent entity
/// carrying a permission identifier as a foreign key, which is why no member below is generic over a shared
/// base: a grant is not a kind of permission, it is a reference to one.
/// </para>
/// <para>
/// <strong>What this contract does not do.</strong> It answers no access question. Whether a principal
/// holds a permission is decided in <c>Infrastructure/Security/PermissionEvaluator.cs</c> and enforced by
/// the API authorisation handler, so no member here takes a principal, a claims set or a list of role
/// names, and none returns a boolean verdict.
/// </para>
/// </remarks>
public interface IPermissionRepository
{
    /// <summary>Returns every catalogue entry this installation declares.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every entry, ordered by identifier so the sequence is stable between calls.</returns>
    /// <remarks>
    /// <para>
    /// <b>THIS IS THE ONE TABLE-WIDE READ ON THIS CONTRACT, and it exists because the API publishes the
    /// catalogue.</b> Every other member here narrows by a scope the legacy provider also narrowed by -
    /// identifier, module definition, module, folder path, scope-code-and-key, page - because the legacy
    /// screens only ever asked scoped questions. Nothing in the legacy application listed the catalogue,
    /// so no legacy reader answered "what does this installation declare"; <c>GET /api/v1/permissions</c>
    /// does, and it cannot be answered by composing scoped reads without either enumerating every module
    /// definition or fabricating the answer from the key enumeration this solution happens to name. The
    /// second of those was measured returning four keys while the table held a fifth, and omitting an entry
    /// the module permission matrices display is worse than adding a read.
    /// </para>
    /// <para>
    /// <b>Unpaged by design.</b> <c>dbo.Permission</c> is bounded reference data seeded by the upgrade
    /// scripts and extended only when a module package registers a key, so it is measured in rows rather
    /// than pages. A caller wanting less asks a scoped member.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Permission>> GetCatalogueAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns one catalogue entry by key, or <see langword="null"/> when none exists.</summary>
    /// <param name="permissionId">Permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Permission?> GetByIdAsync(int permissionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries for a set of keys, in one read.</summary>
    /// <param name="permissionIds">The identifiers to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries that exist, ordered by identifier so the sequence is stable between calls.</returns>
    Task<IReadOnlyList<Permission>> GetByIdsAsync(
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries declared by one module definition.</summary>
    /// <param name="moduleDefinitionId">Module definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Permission>> GetByModuleDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries that apply to one module instance.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Permission>> GetByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries matching one scope code and one permission key.</summary>
    /// <param name="permissionCode">Scope code, matched exactly.</param>
    /// <param name="permissionKey">Permission key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Permission>> GetByCodeAndKeyAsync(
        string permissionCode,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries that apply to one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Permission>> GetByTabIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Removes one catalogue entry.</summary>
    /// <param name="permissionId">Permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(int permissionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new catalogue entry for insertion.</summary>
    /// <param name="permission">The entry to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(Permission permission, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing catalogue entry for update.</summary>
    /// <param name="permission">The entry to update, carrying its own identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateAsync(Permission permission, CancellationToken cancellationToken = default);

    // Legacy AddModulePermission and AddTabPermission passed -1 in two positions with two different
    // meanings. In roleID, -1 is the real All Users pseudo-principal (alongside -2 Superuser and -3
    // Unauthenticated Users); in UserID, -1 was Null.NullInteger meaning absent.

    /// <summary>Returns one module grant by key, or <see langword="null"/> when none exists.</summary>
    /// <param name="modulePermissionId">Module permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L291 <c>GetModulePermission(modulePermissionID)</c>, whose
    /// terminal body selects one row of the module-grant view by primary key. The page block declares no
    /// counterpart to this member - see the note above the page section.
    /// </remarks>
    Task<ModulePermission?> GetModulePermissionByIdAsync(int modulePermissionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the grants recorded against one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="permissionId">Permission identifier to narrow to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByModuleIdAsync(
        int moduleId,
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every module grant within one portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the module grants for every module placed on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Removes every grant recorded against one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteModulePermissionsByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Removes the module grants held directly by one account within one portal.</summary>
    /// <param name="portalId">Portal identifier, which bounds the removal to one tenant.</param>
    /// <param name="userId">Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L296 <c>DeleteModulePermissionsByUserID(PortalID,
    /// UserID)</c>, whose terminal body joins the grant table to the modules table so that only the named
    /// tenant's grants are removed. Only grants naming the account itself go: a grant the account receives
    /// through a role belongs to the role, and removing it would strip every other holder of that role.
    /// </remarks>
    Task DeleteModulePermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Removes every module grant addressed to one role.</summary>
    /// <param name="roleId">Role identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The first of the three cleanups the terminal <c>DeleteRole</c> procedure performed before removing
    /// the role row - <c>03.00.10.SqlDataProvider</c> reads <c>delete from
    /// {objectQualifier}ModulePermission where RoleId = @RoleId</c>.
    /// </remarks>
    Task DeleteModulePermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>Removes one module grant.</summary>
    /// <param name="modulePermissionId">Module permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteModulePermissionAsync(int modulePermissionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new module grant for insertion.</summary>
    /// <param name="modulePermission">The grant to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing module grant for update.</summary>
    /// <param name="modulePermission">The grant to update, carrying its own identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default);

    /// <summary>Returns every page grant within one portal.</summary>
    /// <param name="portalId">Portal identifier, which bounds the answer to one tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TabPermission>> GetTabPermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the grants recorded against one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="permissionId">Permission identifier to narrow to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdAsync(
        int tabId,
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the grants recorded against any of several pages, in one read.</summary>
    /// <param name="tabIds">The pages whose grants are wanted.</param>
    /// <param name="permissionId">
    /// Permission identifier to narrow to, with the same wildcard convention the single-page member
    /// carries: -1 requests every permission.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The matching grants, FLAT rather than grouped, ordered by page and then exactly as the single-page
    /// member orders them, so a caller grouping by <see cref="TabPermission.TabId"/> sees each page's
    /// grants in the same sequence either member would produce.
    /// </returns>
    /// <remarks>
    /// The set-based form of <see cref="GetTabPermissionsByTabIdAsync"/>. It exists because a decision
    /// taken over several pages at once - "does the caller hold this grant on ANY of the pages this module
    /// sits on" - otherwise costs one grant read per page, which makes an authorisation check on an
    /// all-pages module proportional to the size of the tenant's page tree.
    /// </remarks>
    Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdsAsync(
        IReadOnlyCollection<int> tabIds,
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every grant recorded against one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteTabPermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Removes the page grants held directly by one account within one portal.</summary>
    /// <param name="portalId">Portal identifier, which bounds the removal to one tenant.</param>
    /// <param name="userId">Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L305 <c>DeleteTabPermissionsByUserID(PortalID, UserID)</c>,
    /// whose terminal body joins the grant table to the pages table so that only the named tenant's grants
    /// are removed. As with its module counterpart, only grants naming the account itself are removed.
    /// </remarks>
    Task DeleteTabPermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Removes every page grant addressed to one role.</summary>
    /// <param name="roleId">Role identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The second of the three cleanups the terminal <c>DeleteRole</c> procedure performed -
    /// <c>03.00.10.SqlDataProvider</c> reads <c>delete from {objectQualifier}TabPermission where RoleId =
    /// @RoleId</c>. A bulk removal, so nothing is returned.
    /// </remarks>
    Task DeleteTabPermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>Removes every folder grant addressed to one role, where the legacy folder table exists.</summary>
    /// <param name="roleId">Role identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The third cleanup the terminal <c>DeleteRole</c> procedure performed, and the FIRST statement in its
    /// body - <c>03.00.10.SqlDataProvider</c> reads <c>delete from {objectQualifier}FolderPermission where
    /// RoleId = @RoleId</c>.
    /// </remarks>
    Task DeleteFolderPermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>Removes one page grant.</summary>
    /// <param name="tabPermissionId">Page permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteTabPermissionAsync(int tabPermissionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new page grant for insertion.</summary>
    /// <param name="tabPermission">The grant to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing page grant for update.</summary>
    /// <param name="tabPermission">The grant to update, carrying its own identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default);
}
