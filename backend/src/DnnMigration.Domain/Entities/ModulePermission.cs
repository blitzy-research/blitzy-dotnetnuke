using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: legacy type DotNetNuke.Security.Permissions.ModulePermissionInfo becomes this entity.

/// <summary>
/// One grant or denial of a catalogued permission on one module instance, addressed either to a role or to
/// an individual account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every identity name here is idiomatic and every column name is legacy</b>, so each of the six
/// properties needs an explicit <c>HasColumnName</c>: <c>ModulePermissionID</c>, <c>ModuleID</c>,
/// <c>PermissionID</c>, <c>RoleID</c>, <c>AllowAccess</c> and <c>UserID</c>. The schema is immutable for
/// this migration, so the mapping bends to the column names rather than the reverse.
/// </para>
/// <para>
/// <b>The uniqueness rule spans four columns and must be configured, not re-implemented.</b>
/// <c>04.05.02.SqlDataProvider</c> lines 134 to 142 add <c>IX_ModulePermission</c> as a UNIQUE NONCLUSTERED
/// constraint over <c>(ModuleID, PermissionID, RoleID, UserID)</c> - the whole tuple, both subject columns
/// included.
/// </para>
/// </remarks>
public sealed class ModulePermission : Entity<int>
{
    /// <summary>Gets or sets the surrogate primary key of the grant.</summary>
    /// <value>
    /// The <c>ModulePermissionID</c> column, <c>int IDENTITY(1, 1) NOT NULL</c>
    /// (<c>02.02.00.SqlDataProvider</c> line 676), so the Infrastructure mapping declares it generated on
    /// add with a seed and increment of one.
    /// </value>
    /// <remarks>
    /// The key is the row's, not the subject's. <c>PK_ModulePermission</c> is clustered on this column
    /// alone (<c>02.02.00.SqlDataProvider</c> lines 716 to 721, dropped and re-added under a
    /// qualifier-bearing name by <c>03.00.09.SqlDataProvider</c> lines 461 and 477), while the rule about
    /// which grants may coexist is the four-column unique index described in the class remarks.
    /// </remarks>
    public int ModulePermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => ModulePermissionId;

    /// <summary>Gets or sets the module instance this grant applies to.</summary>
    /// <value>
    /// The <c>ModuleID</c> column, <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 677), mapped
    /// as required.
    /// </value>
    /// <remarks>
    /// Backed by a real, cascading foreign key - <c>FK_ModulePermission_Modules</c> to <c>dbo.Modules</c>
    /// ON DELETE CASCADE (<c>02.02.00.SqlDataProvider</c> lines 762 to 767, re-created with a
    /// qualifier-bearing name by <c>03.00.09.SqlDataProvider</c> line 493).
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>Gets or sets the catalogue entry being granted or refused.</summary>
    /// <value>
    /// The <c>PermissionID</c> column, <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 678),
    /// mapped as required.
    /// </value>
    /// <remarks>
    /// This property is what REPLACES the legacy inheritance. Where <c>ModulePermissionInfo</c> inherited
    /// <c>PermissionInfo</c> and copied its five members into itself, a grant now names its catalogue entry
    /// and nothing more.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <summary>
    /// Gets or sets the role the grant is addressed to, or <see langword="null"/> when the grant is not
    /// addressed to a role.
    /// </summary>
    /// <value>The <c>RoleID</c> column, <c>int NULL</c> in the terminal schema.</value>
    /// <remarks>
    /// This column's nullability was rewritten DESTRUCTIVELY and the property must stay nullable because of
    /// it. It was created <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 679);
    /// <c>04.05.00.SqlDataProvider</c> lines 615 to 635 then copied it to a temporary column, DROPPED it,
    /// re-added it as <c>int NULL</c>, copied the values back and dropped the temporary.
    /// </remarks>
    public int? RoleId { get; set; }

    /// <summary>
    /// Gets or sets whether this row grants the permission (<see langword="true"/>) or explicitly refuses
    /// it (<see langword="false"/>).
    /// </summary>
    /// <value>
    /// The <c>AllowAccess</c> column, <c>bit NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 680),
    /// mapped as required.
    /// </value>
    public bool AllowAccess { get; set; }

    /// <summary>
    /// Gets or sets the individual account the grant is addressed to, or <see langword="null"/> when the
    /// grant is not addressed to an account.
    /// </summary>
    /// <value>The <c>UserID</c> column, <c>int NULL</c>.</value>
    /// <remarks>
    /// This column did not exist in the original table. <c>04.05.00.SqlDataProvider</c> lines 640 to 655
    /// add it as <c>int NULL</c> - guarded by a <c>COLUMNPROPERTY</c> test so the script is re-runnable -
    /// together with a foreign key to <c>dbo.Users</c> declared NOT FOR REPLICATION and carrying no ON
    /// DELETE clause.
    /// </remarks>
    public int? UserId { get; set; }

    /// <summary>Gets or sets the module instance this grant applies to.</summary>
    /// <remarks>
    /// Required, because <see cref="ModuleId"/> is <c>NOT NULL</c> behind an enforced foreign key - a grant
    /// with no module cannot exist in this schema. Left unset on a newly constructed instance and populated
    /// by the persistence layer, which is why the solution suppresses CS8618 centrally rather than propping
    /// the property up with a null-forgiving initializer.
    /// </remarks>
    public Module Module { get; set; }

    /// <summary>Gets or sets the catalogue entry this grant refers to.</summary>
    /// <remarks>
    /// Required for the same reason as <c>Module</c> - <see cref="PermissionId"/> is <c>NOT NULL</c> behind
    /// an enforced foreign key. This navigation, and not a base class, is how the catalogue's permission
    /// key, name and scope code are reached; see the note on <see cref="PermissionId"/>.
    /// </remarks>
    public Permission Permission { get; set; }

    /// <summary>
    /// Gets or sets the role this grant is addressed to, or <see langword="null"/> when <see
    /// cref="RoleId"/> is null or names a pseudo-principal that has no <c>dbo.Roles</c> row.
    /// </summary>
    /// <remarks>
    /// Optional, and optional in a stronger sense than a nullable column usually implies. There is no
    /// foreign key on <c>RoleID</c> at all, so this navigation may be null even when <see cref="RoleId"/>
    /// holds a value - which is exactly the case for the persisted <c>-1</c>, <c>-2</c> and <c>-3</c>
    /// pseudo-principals.
    /// </remarks>
    public Role? Role { get; set; }

    /// <summary>
    /// Gets or sets the individual account this grant is addressed to, or <see langword="null"/> when the
    /// grant is not addressed to an account.
    /// </summary>
    /// <remarks>
    /// Optional, mirroring the nullable <see cref="UserId"/> column added by 04.05.00. Unlike <c>Role</c>
    /// this one does sit behind a real foreign key, so a non-null <see cref="UserId"/> does name an
    /// existing account.
    /// </remarks>
    public User? User { get; set; }
}
