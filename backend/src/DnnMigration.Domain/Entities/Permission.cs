using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// Legacy type DotNetNuke.Security.Permissions.PermissionInfo becomes this entity.

/// <summary>
/// One entry in the permission catalogue: a single named action, scoped by a permission code and owned by a
/// module definition, that a grant row is allowed to refer to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every text column is ANSI, not Unicode.</b> <c>PermissionCode</c>, <c>PermissionKey</c> and
/// <c>PermissionName</c> are each declared <c>varchar(50)</c> in the terminal schema - never
/// <c>nvarchar</c> - so each mapping must say so explicitly with <c>IsUnicode(false)</c>.
/// </para>
/// <para>
/// <b>The uniqueness rule spans three columns.</b> <c>IX_Permission</c> is unique over <c>(PermissionCode,
/// ModuleDefID, PermissionKey)</c> - see <see cref="PermissionCode"/> - so the same key may exist once per
/// scope code per definition, and a second row naming the same triple is rejected by the database rather
/// than by any check in this layer.
/// </para>
/// </remarks>
public sealed class Permission : Entity<int>
{
    /// <summary>Gets or sets the surrogate primary key of the catalogue entry.</summary>
    /// <value>
    /// The <c>PermissionID</c> column, <c>int IDENTITY(1, 1) NOT NULL</c> (<c>02.02.00.SqlDataProvider</c>
    /// line 685), so the Infrastructure mapping declares it generated on add with a seed and increment of
    /// one.
    /// </value>
    /// <remarks>
    /// This table is one of the few whose identity seed is 1 rather than 0 or -1, so zero happens never to
    /// identify a real row here.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => PermissionId;

    /// <summary>Gets or sets the code that scopes the key to a subsystem.</summary>
    /// <value>
    /// The <c>PermissionCode</c> column, <c>varchar(50) NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line
    /// 686).
    /// </value>
    /// <remarks>
    /// Deliberately a plain <see cref="string"/> and NOT an enumeration, which is the opposite of the
    /// decision taken for <see cref="PermissionKey"/>. Nothing constrains this column - no check
    /// constraint, no lookup foreign key - so an installation carrying a scope this migration has never
    /// seen must round-trip it intact rather than fail to materialise.
    /// </remarks>
    public string PermissionCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the identifier of the module definition that owns this catalogue entry.</summary>
    /// <value>The <c>ModuleDefID</c> column, <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 687).</value>
    /// <remarks>
    /// Required, and a foreign key in intent only - which is why NO relationship is modelled over it. The
    /// upgrade chain declares no <c>FOREIGN KEY</c> on this column anywhere; the only constraints naming
    /// this table point the other way, from the grant tables to <c>Permission.PermissionID</c> (rebuilt
    /// with cascade delete by <c>03.00.09.SqlDataProvider</c> lines 482, 488 and 492).
    /// </remarks>
    public int ModuleDefinitionId { get; set; }

    /// <summary>Gets or sets the action this catalogue entry names.</summary>
    /// <value>The <c>PermissionKey</c> column, whose terminal declaration is <c>varchar(50) NOT NULL</c>.</value>
    /// <remarks>
    /// <para>
    /// MIGRATION: <b>a plain <see cref="string"/> and NOT the <see cref="Enums.PermissionKey"/>
    /// enumeration</b> - the same decision already taken for <see cref="PermissionCode"/> above, and taken
    /// here for the same reason. The column is <c>varchar(50) NOT NULL</c> with NO check constraint and no
    /// lookup foreign key: it was created <c>varchar(20)</c> (<c>02.02.00.SqlDataProvider</c> line 688) and
    /// deliberately WIDENED to fifty characters (<c>04.06.00.SqlDataProvider</c> line 398, comment
    /// "enlarge permission key field"), with <c>AddPermission</c> accepting <c>@PermissionKey varchar(50)</c>
    /// from line 407 onward. The legacy property this replaces is likewise declared
    /// <c>Public Property PermissionKey() As String</c>
    /// (<c>Library/Components/Security/Permissions/Permission.vb</c> line 69). DotNetNuke's extensibility
    /// model has a third-party module register its own keys at install time through that very procedure, so
    /// an installation this migration has never seen can already hold a key outside the four the upgrade
    /// chain seeds - and it must round-trip intact rather than fail to materialise.
    /// </para>
    /// <para>
    /// The four keys the upgrade chain does seed - <c>VIEW</c>, <c>EDIT</c>, <c>READ</c> and <c>WRITE</c> -
    /// remain named members of <see cref="Enums.PermissionKey"/>, which stays the closed vocabulary that
    /// authorisation policies, permission requirements and service parameters are written against. The
    /// enumeration is therefore the set of keys THIS SOLUTION can ask about, never a claim about the set of
    /// keys the column can HOLD. Match a stored spelling against a member with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>, because the column's collation is
    /// case-insensitive.
    /// </para>
    /// </remarks>
    public string PermissionKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable name of the action, as the legacy permission grids showed it.
    /// </summary>
    /// <value>
    /// The <c>PermissionName</c> column, <c>varchar(50) NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line
    /// 689) - for example <c>View Module</c> for the <see cref="Enums.PermissionKey.VIEW"/> entry.
    /// </value>
    public string PermissionName { get; set; } = string.Empty;

    // THERE IS DELIBERATELY NO ModuleDefinition NAVIGATION ON THIS ENTITY, and adding one would be a defect
    // rather than a convenience. Three facts compound.

    /// <summary>Gets the module-level grants that refer to this catalogue entry.</summary>
    /// <value>The <c>dbo.ModulePermission</c> rows whose <c>PermissionID</c> names this entry.</value>
    /// <remarks>
    /// <see cref="ModulePermission"/> REFERENCES this entity by <see
    /// cref="ModulePermission.PermissionId"/>; it does not and must not derive from it. The legacy model
    /// had <c>ModulePermissionInfo</c> inherit <c>PermissionInfo</c> and flatten the joined catalogue
    /// columns onto the grant, making a grant indistinguishable from the action it grants.
    /// </remarks>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>Gets the page-level grants that refer to this catalogue entry.</summary>
    /// <value>The <c>dbo.TabPermission</c> rows whose <c>PermissionID</c> names this entry.</value>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
