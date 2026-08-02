using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the permission catalogue and the module- and page-scoped access-control entries
/// granted against it.
/// </summary>
/// <remarks>
/// MIGRATION: consolidates the data-access halves of <c>PermissionController.vb</c>,
/// <c>ModulePermissionController.vb</c> and <c>TabPermissionController.vb</c>, which between them
/// duplicated the same access-control queries three times over.
/// </remarks>
public interface IPermissionRepository
{
    /// <summary>Returns the permission catalogue, optionally narrowed.</summary>
    /// <param name="permissionCode">Restrict to one permission code, such as the system module or tab code, or <see langword="null"/> for all.</param>
    /// <param name="moduleDefinitionId">Restrict to the permissions a module definition declares, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Permission>> ListAsync(
        string? permissionCode,
        int? moduleDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one catalogue entry by key, or <see langword="null"/>.</summary>
    /// <param name="permissionId">Permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Permission?> GetAsync(int permissionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the access-control entries granted on one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ModulePermission>> ListModulePermissionsAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the access-control entries granted on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TabPermission>> ListTabPermissionsAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Returns the permission keys a caller holds on one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="userId">User identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; entries granted to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The distinct, upper-cased keys of the entries that allow access. An entry that denies access
    /// suppresses the key even when another entry allows it, which is the legacy precedence.
    /// </returns>
    Task<IReadOnlyList<string>> ListEffectiveModulePermissionKeysAsync(
        int moduleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the permission keys a caller holds on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="userId">User identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; entries granted to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct, upper-cased keys of the entries that allow access.</returns>
    Task<IReadOnlyList<string>> ListEffectiveTabPermissionKeysAsync(
        int tabId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every permission key a caller holds anywhere within one portal.</summary>
    /// <param name="portalId">Portal identifier. Grants in other portals are not considered.</param>
    /// <param name="userId">User identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds; entries granted to any of them apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The distinct, upper-cased keys of the allowing entries across every module and page of the portal,
    /// after denying entries have suppressed the keys they deny, which is the same precedence the two
    /// scoped members apply.
    /// </returns>
    /// <remarks>
    /// This is the portal-level union an administration shell needs in order to decide which sections to
    /// offer, and it is what populates the permission claims of an issued access token. It exists as one
    /// member rather than as a loop over
    /// <see cref="ListEffectiveModulePermissionKeysAsync(int, int?, IReadOnlyCollection{string}, CancellationToken)"/>
    /// and
    /// <see cref="ListEffectiveTabPermissionKeysAsync(int, int?, IReadOnlyCollection{string}, CancellationToken)"/>
    /// because a portal carrying hundreds of pages and modules would otherwise cost hundreds of round
    /// trips to answer one question that the store can answer as a single set operation.
    /// </remarks>
    Task<IReadOnlyList<string>> ListEffectivePortalPermissionKeysAsync(
        int portalId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Determines whether a caller holds one specific key on a module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">User identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> HasModulePermissionAsync(
        int moduleId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Determines whether a caller holds one specific key on a page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">User identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> HasTabPermissionAsync(
        int tabId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every module- and page-scoped entry granted directly to one user in a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries removed.</returns>
    /// <remarks>
    /// MIGRATION: replaces the three user-keyed permission-cleanup procedures that the core data
    /// provider invoked when a user was deleted, so a removed account leaves no orphaned grants.
    /// </remarks>
    Task<int> DeleteUserPermissionsAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new module access-control entry for insertion.</summary>
    /// <param name="permission">The entry to insert.</param>
    void AddModulePermission(ModulePermission permission);

    /// <summary>Stages a module access-control entry for deletion.</summary>
    /// <param name="permission">The entry to delete.</param>
    void RemoveModulePermission(ModulePermission permission);

    /// <summary>Stages a new page access-control entry for insertion.</summary>
    /// <param name="permission">The entry to insert.</param>
    void AddTabPermission(TabPermission permission);

    /// <summary>Stages a page access-control entry for deletion.</summary>
    /// <param name="permission">The entry to delete.</param>
    void RemoveTabPermission(TabPermission permission);
}
