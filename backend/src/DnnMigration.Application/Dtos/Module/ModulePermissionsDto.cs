namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The grant grid of one module: the permission keys that form its columns, the roles and the individually
/// named accounts that form its rows, and the state of every cell. Carried as the response body of <c>GET
/// /api/v1/modules/{moduleId}/permissions</c>.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this replaces the <c>&lt;dnn:modulepermissionsgrid id="dgPermissions"&gt;</c> server control
/// declared at <c>Website/admin/Modules/modulesettings.ascx:L42</c>. That control read its rows through
/// <c>GetModulePermissionsCollectionByModuleID</c> (<c>ModuleSettings.ascx.vb:L89</c>) and computed each
/// cell in <c>ModulePermissionsGrid.GetPermission</c>/<c>GetEnabled</c>
/// (<c>Library/Controls/DataGrids/Permissions Grids/ModulePermissionsGrid.vb:L237-L340</c>). Those two
/// overrides encoded rules a client cannot infer from grant rows alone - the administrator row is always
/// granted and never editable, and the view column collapses while the module inherits its view rights from
/// its page - so this contract carries them as explicit per-cell facts rather than leaving each client to
/// re-derive them and disagree.
/// </para>
/// <para>
/// A boundary contract and nothing more: no navigation property, no tracked state, no behaviour and no
/// domain entity, in either direction.
/// </para>
/// </remarks>
public sealed class ModulePermissionsDto
{
    /// <summary>The module whose grants these are, from <c>Modules.ModuleID</c>.</summary>
    /// <remarks>
    /// The column is an identity seeded at zero, so <c>0</c> is a legitimate module: neither a "less than or
    /// equal to zero" test nor a comparison against the legacy absent-integer sentinel of <c>-1</c> is a
    /// valid emptiness check for this member.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// Whether the module currently takes its view rights from the page it is placed on, from
    /// <c>Modules.InheritViewPermissions</c>.
    /// </summary>
    /// <remarks>
    /// Carried here as well as on the module detail contract because the state of every cell in the view
    /// column depends on it, and a client that had to correlate two responses to render one grid would
    /// render a stale grid every time the two arrived out of order.
    /// </remarks>
    public bool InheritViewPermissions { get; set; }

    /// <summary>
    /// The permission key of the column whose cells the inheritance switch collapses - <c>VIEW</c> - or
    /// <see langword="null"/> when this module's definition declares no such column.
    /// </summary>
    /// <remarks>
    /// The legacy grid held this as <c>_ViewColumnIndex</c>, an ordinal assigned while the columns were
    /// being built (<c>ModulePermissionsGrid.vb:L364</c>). An ordinal is meaningless across a wire whose
    /// column order a client may change, so the KEY travels instead.
    /// </remarks>
    public string? InheritedPermissionKey { get; set; }

    /// <summary>The columns of the grid, in the order they should be rendered.</summary>
    public IReadOnlyList<ModulePermissionDefinitionDto> Definitions { get; set; } =
        Array.Empty<ModulePermissionDefinitionDto>();

    /// <summary>
    /// The role rows of the grid, ordered case-insensitively by name, matching the legacy
    /// <c>RoleComparer</c> at <c>Library/Components/Security/Roles/RoleComparer.vb:L56</c>.
    /// </summary>
    public IReadOnlyList<ModulePermissionRoleDto> Roles { get; set; } =
        Array.Empty<ModulePermissionRoleDto>();

    /// <summary>
    /// The rows for accounts that hold a grant of their own rather than one inherited through a role.
    /// Present only for accounts a grant already names; this contract does not offer an account picker.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy grid could ADD an account to the grid by name
    /// (<c>ModulePermissionsGrid.AddPermission</c>). That affordance is not reproduced, and the reduction is
    /// recorded in <c>MIGRATION_NOTES.md</c>: existing account-scoped grants remain visible and editable
    /// here, so no stored grant becomes unreachable, but a NEW account-scoped grant is made by granting a
    /// role instead.
    /// </remarks>
    public IReadOnlyList<ModulePermissionUserDto> Users { get; set; } =
        Array.Empty<ModulePermissionUserDto>();
}

/// <summary>One column of the grant grid: a permission this module's definition declares.</summary>
public sealed class ModulePermissionDefinitionDto
{
    /// <summary>Identifier of the definition, from <c>Permission.PermissionID</c>.</summary>
    public int PermissionId { get; set; }

    /// <summary>
    /// The key itself - <c>VIEW</c>, <c>EDIT</c>, <c>READ</c> or <c>WRITE</c> - from
    /// <c>Permission.PermissionKey</c>, carried as the member name because that is what the column stores
    /// and what every other permission-shaped value in this API carries.
    /// </summary>
    public string PermissionKey { get; set; } = string.Empty;

    /// <summary>Display name of the definition, from <c>Permission.PermissionName</c>.</summary>
    public string PermissionName { get; set; } = string.Empty;
}

/// <summary>One role row of the grant grid.</summary>
public sealed class ModulePermissionRoleDto
{
    /// <summary>
    /// Identifier of the role, from <c>Roles.RoleID</c>, or one of the built-in negative pseudo-role
    /// identifiers.
    /// </summary>
    /// <remarks>
    /// <c>0</c> is a legitimate role - the column is <c>IDENTITY (0, 1)</c> - and on a default installation
    /// it is the Administrators role, so a falsiness test on this member is never a valid emptiness check.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>Display name of the role, from <c>Roles.RoleName</c>.</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this row is the portal's administrator role, whose grants are implicit and not editable.
    /// </summary>
    /// <remarks>
    /// Reproduces <c>ModulePermissionsGrid.GetEnabled</c>/<c>GetPermission</c>, which disabled the row and
    /// reported every cell as granted when <c>role.RoleID = AdministratorRoleId</c>. Resolved from
    /// <c>Portals.AdministratorRoleId</c> rather than from the role's name, because the name is data an
    /// installation may change.
    /// </remarks>
    public bool IsAdministrator { get; set; }

    /// <summary>
    /// Whether this row is one of the two built-in pseudo-roles the legacy grid appended - <c>All Users</c>
    /// and <c>Unauthenticated Users</c> - rather than a row in <c>dbo.Roles</c>.
    /// </summary>
    public bool IsPseudoRole { get; set; }

    /// <summary>The state of every cell in this row, one entry per declared definition.</summary>
    public IReadOnlyList<ModulePermissionCellDto> Cells { get; set; } =
        Array.Empty<ModulePermissionCellDto>();
}

/// <summary>One account row of the grant grid.</summary>
public sealed class ModulePermissionUserDto
{
    /// <summary>Identifier of the account, from <c>Users.UserID</c>.</summary>
    public int UserId { get; set; }

    /// <summary>
    /// The account's display name, from <c>Users.DisplayName</c>, or its user name when the display name is
    /// blank.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The state of every cell in this row, one entry per declared definition.</summary>
    public IReadOnlyList<ModulePermissionCellDto> Cells { get; set; } =
        Array.Empty<ModulePermissionCellDto>();
}

/// <summary>One cell of the grant grid: whether it is granted, and whether it may be changed.</summary>
/// <remarks>
/// Both facts travel, because they are independent and neither implies the other. An administrator cell is
/// granted and locked; a view cell under inheritance is refused and locked; an ordinary cell is either and
/// unlocked.
/// </remarks>
public sealed class ModulePermissionCellDto
{
    /// <summary>The definition this cell belongs to, from <c>Permission.PermissionID</c>.</summary>
    public int PermissionId { get; set; }

    /// <summary>The key of the definition this cell belongs to, repeated for readability of the payload.</summary>
    public string PermissionKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether the grant is currently in force. <see langword="false"/> covers both "no grant row exists"
    /// and "a grant row exists that denies", which is the same distinction the legacy grid's two-state
    /// checkbox could not draw either.
    /// </summary>
    public bool AllowAccess { get; set; }

    /// <summary>Whether the cell may be changed by the caller.</summary>
    /// <remarks>
    /// <see langword="false"/> for every cell of the administrator row, and for the view column while the
    /// module inherits its view rights from its page.
    /// </remarks>
    public bool Editable { get; set; }
}
