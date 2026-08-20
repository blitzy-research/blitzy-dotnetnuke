namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The payload of <c>PUT /api/v1/modules/{moduleId}/permissions</c>: the complete set of grants the module
/// should hold afterwards, together with the state of its view-inheritance switch.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THIS IS A REPLACE, NOT A MERGE, and it is a replace because the legacy screen was one too.
/// <c>ModuleSettings.ascx.vb:L378-L379</c> assigned <c>objModule.ModulePermissions =
/// dgPermissions.Permissions</c> and <c>objModule.InheritViewPermissions =
/// chkInheritPermissions.Checked</c> in the same commit, and the grid's collection contained exactly the
/// boxes that were ticked - <c>ModulePermissionsGrid.UpdatePermission</c> removed an entry the moment its box
/// was cleared, with the comment "we only keep AllowAccess permissions". A grant absent from <see
/// cref="Grants"/> is therefore a grant that is being withdrawn.
/// </para>
/// <para>
/// The two members are submitted together for the same reason they were saved together: the inheritance
/// switch decides whether the view grants in <see cref="Grants"/> mean anything at all, so accepting one
/// without the other would let a client store a combination the screen it came from could not express.
/// </para>
/// </remarks>
public sealed class ReplaceModulePermissionsRequest
{
    /// <summary>
    /// Whether the module should take its view rights from the page it is placed on, written to
    /// <c>Modules.InheritViewPermissions</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ TURNING THIS ON DISCARDS EXPLICIT VIEW GRANTS, which is legacy behaviour reproduced deliberately
    /// rather than an oversight. The legacy grid reported every view cell as cleared and disabled while
    /// inheritance was on (<c>ModulePermissionsGrid.vb:L296-L300</c>), and the save path then removed every
    /// grant whose box was clear - so the same save that turned inheritance on also deleted the view rows.
    /// The behaviour is recorded in <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    public bool InheritViewPermissions { get; set; }

    /// <summary>
    /// Every grant the module should hold. An empty list withdraws all of them; it is a legitimate
    /// submission and is never treated as an omission.
    /// </summary>
    public IReadOnlyList<ModulePermissionGrantRequest> Grants { get; set; } =
        Array.Empty<ModulePermissionGrantRequest>();
}

/// <summary>One grant in a replacement set: who it is for, which permission, and whether it allows.</summary>
/// <remarks>
/// Exactly one of <see cref="RoleId"/> and <see cref="UserId"/> must be supplied. The pair is not collapsed
/// into a single "principal" member because the stored row does not collapse them either -
/// <c>dbo.ModulePermissions</c> carries both columns, nullable, and the evaluator tests the account column
/// first.
/// </remarks>
public sealed class ModulePermissionGrantRequest
{
    /// <summary>The definition being granted, from <c>Permission.PermissionID</c>.</summary>
    public int PermissionId { get; set; }

    /// <summary>
    /// The role the grant is for, or <see langword="null"/> when the grant is for one named account.
    /// </summary>
    /// <remarks>
    /// May be one of the built-in negative pseudo-role identifiers. <c>0</c> is a real role, so a falsiness
    /// test on this member is not a valid presence check - only a null check is.
    /// </remarks>
    public int? RoleId { get; set; }

    /// <summary>
    /// The account the grant is for, or <see langword="null"/> when the grant is for a role.
    /// </summary>
    public int? UserId { get; set; }

    /// <summary>
    /// <see langword="true"/> to allow, <see langword="false"/> to deny.
    /// </summary>
    /// <remarks>
    /// The grid that submits this only produces allows, matching the legacy two-state checkbox, but the
    /// contract carries the flag because the stored column does and because the evaluator honours a deny as
    /// an override rather than as an absence.
    /// </remarks>
    public bool AllowAccess { get; set; } = true;
}
