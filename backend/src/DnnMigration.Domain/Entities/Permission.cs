using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One entry in the permission catalogue: a named action that a grant can refer to.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the five-property <c>DotNetNuke.Security.Permissions.PermissionInfo</c>
/// (Library/Components/Security/Permissions/Permission.vb). Bound to <c>dbo.Permission</c> -
/// singular - created by 02.02.00; 04.05.02 added the unique index over
/// <c>(PermissionCode, ModuleDefID, PermissionKey)</c>. The columns are <c>varchar</c>, not
/// <c>nvarchar</c>, because permission codes and keys are ASCII tokens.
/// </remarks>
public sealed class Permission : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>PermissionID</c>, identity from 1).</summary>
    public int PermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => PermissionId;

    /// <summary>
    /// Gets or sets the namespace of the permission, for example <c>SYSTEM_MODULE_DEFINITION</c>
    /// (<c>PermissionCode varchar(50)</c>, required).
    /// </summary>
    public string PermissionCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the module definition that owns the permission (<c>ModuleDefID</c>, required).</summary>
    public int ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the action token, for example <c>VIEW</c> or <c>EDIT</c>
    /// (<c>PermissionKey varchar(20)</c>, required). Stored as text rather than as an enumeration
    /// because module definitions may register their own keys.
    /// </summary>
    public string PermissionKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable name shown in the permission grids (<c>PermissionName varchar(50)</c>, required).</summary>
    public string PermissionName { get; set; } = string.Empty;

    /// <summary>Gets or sets the module definition that owns the permission.</summary>
    public ModuleDefinition? ModuleDefinition { get; set; }

    /// <summary>Gets the module-level grants that refer to this catalogue entry.</summary>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>Gets the page-level grants that refer to this catalogue entry.</summary>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
