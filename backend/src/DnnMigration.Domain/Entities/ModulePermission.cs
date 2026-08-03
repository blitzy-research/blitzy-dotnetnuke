using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: legacy type DotNetNuke.Security.Permissions.ModulePermissionInfo becomes this entity.
//   The "...Info" suffix is dropped for the same reason it is dropped on the Permission entity: it
//   existed only to separate a data carrier from its static "...Controller" companion - here
//   Library/Components/Security/Permissions/ModulePermissionController.vb - and the target draws that
//   distinction with the layer boundary instead.
//
// MIGRATION: THE INHERITANCE IS DELIBERATELY GONE, and this is the single most important fact about
//   this file. Library/Components/Security/Permissions/ModulePermission.vb line 29 declares
//   "Inherits PermissionInfo", and the second constructor at lines 55 to 63 copies ModuleDefID,
//   PermissionCode, PermissionID, PermissionKey and PermissionName off the supplied base instance -
//   so a legacy grant carried a private, already-stale copy of the catalogue entry it referred to.
//   The terminal table stores none of those columns. It stores a PermissionID and nothing else about
//   the catalogue, so this entity REFERENCES the catalogue through PermissionId plus a Permission
//   navigation, and derives from nothing but Entity<int>.
//
//   The obligation that places on the Infrastructure layer is explicit: map the six columns of
//   dbo.ModulePermission and NOTHING ELSE, and do not read the pairing of this entity with Permission
//   as an entity hierarchy. There is no table-per-hierarchy discriminator and no table-per-type split
//   to discover here, because there was never a base TABLE - only a base CLASS, which is a different
//   thing and is exactly the conflation this migration removes.
//
// MIGRATION: the eight <XmlElement> attributes on the legacy properties (lines 66, 75, 84, 93, 102,
//   111, 120 and 129) are not carried across. That one class doubled as its own wire format; in the
//   target the wire contract belongs to the DTOs at the API boundary, so this entity carries no
//   attribute of any kind - no serialisation attribute and no data annotation.
//
// MIGRATION: the legacy constructor's sentinel assignments (lines 43 to 53) are NOT ported, and no
//   property below carries a negative initializer. That constructor set _modulePermissionID and
//   _moduleID to Null.NullInteger (Library/Components/Shared/Null.vb lines 41 to 44 return -1),
//   _userID likewise to -1, and _roleID to Integer.Parse(glbRoleNothing) - "-4" per
//   Library/Components/Shared/Globals.vb line 98. Those values were in-memory markers for "nothing
//   chosen yet" in a language without nullable value types. They are NOT database defaults: the
//   terminal DDL declares no DEFAULT clause on any of the six columns. Reproducing them would write
//   -1 and -4 into rows the legacy application would have left null.
//
// MIGRATION: the "Overloads Overrides Function Equals" at lines 158 to 165 is not ported either. Its
//   own remarks at lines 149 to 153 state why it existed - "to prevent adding duplicates to the
//   ModulePermissionCollection", whose Contains used it - so it was a pre-generics collection
//   workaround, not a domain rule. Rule T8 replaces it on both counts: uniqueness is enforced by the
//   database's own unique index (see the class remarks) and identity equality comes from
//   Entity<int>. Overriding Equals here would silently override that inherited contract.

/// <summary>
/// One grant or denial of a catalogued permission on one module instance, addressed either to a role
/// or to an individual account.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a link row, not a permission.</b> What a permission IS lives in the
/// <c>Permission</c> catalogue; this entity records that some subject has been granted or refused one
/// of those catalogue entries on one module. It therefore holds no permission key, no permission name
/// and no scope code - reaching those means following <see cref="PermissionId"/>.
/// </para>
/// <para>
/// <b>Mapping brief for the Infrastructure layer.</b> The entity binds to the singular table
/// <c>dbo.ModulePermission</c>, created by
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider</c> at lines 675 to
/// 681 with five columns, its primary key added at lines 716 to 721 and its two cascading foreign
/// keys at lines 761 to 773. A sweep of all eighty-eight upgrade scripts - case-insensitively and
/// across all four naming forms the chain uses: bare, <c>dbo.</c>-qualified,
/// <c>[dbo].[…]</c>-bracketed and <c>{databaseOwner}{objectQualifier}</c>-templated - finds only five
/// scripts that touch this table structurally, and every one of them is recorded below. Per AAP
/// Rule T4 it is the TERMINAL state of that chain, six columns wide, that the Fluent configuration
/// must reproduce; the baseline declaration alone is not the schema.
/// </para>
/// <para>
/// <b>Every identity name here is idiomatic and every column name is legacy</b>, so each of the six
/// properties needs an explicit <c>HasColumnName</c>: <c>ModulePermissionID</c>, <c>ModuleID</c>,
/// <c>PermissionID</c>, <c>RoleID</c>, <c>AllowAccess</c> and <c>UserID</c>. The schema is immutable
/// for this migration, so the mapping bends to the column names rather than the reverse.
/// </para>
/// <para>
/// <b>The uniqueness rule spans four columns and must be configured, not re-implemented.</b>
/// <c>04.05.02.SqlDataProvider</c> lines 134 to 142 add <c>IX_ModulePermission</c> as a UNIQUE
/// NONCLUSTERED constraint over <c>(ModuleID, PermissionID, RoleID, UserID)</c> - the whole tuple,
/// both subject columns included. Because this engine treats nulls as equal for uniqueness, that
/// admits exactly one subject-less row per module and permission pair. The database is the enforcer;
/// no check in this layer duplicates it.
/// </para>
/// <para>
/// <b>Joined names are not state and are not here.</b> The legacy class carried <c>RoleName</c>,
/// <c>Username</c> and <c>DisplayName</c> beside the real columns, but none of the three is a column
/// of this table: they are projections of the view <c>vw_ModulePermissions</c>
/// (<c>04.05.00.SqlDataProvider</c> line 662 onward), which reaches them by outer-joining
/// <c>Roles</c> and <c>Users</c>. Presenting them is the Application layer's job, on a DTO. Adding
/// them here would put a value in the domain that no write path can persist and no read path can keep
/// current.
/// </para>
/// <para>
/// <b>No behaviour lives here.</b> Rule T6: this type performs no I/O, holds no service and reads no
/// clock. Rule T7: it uses honest CLR types and represents no value with a sentinel. Rule T1: it
/// references nothing outside <c>DnnMigration.Domain</c>, which is what keeps the persistence and
/// serialisation decisions described above in the layers that own them.
/// </para>
/// </remarks>
public sealed class ModulePermission : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate primary key of the grant.
    /// </summary>
    /// <value>
    /// The <c>ModulePermissionID</c> column, <c>int IDENTITY(1, 1) NOT NULL</c>
    /// (<c>02.02.00.SqlDataProvider</c> line 676), so the Infrastructure mapping declares it
    /// generated on add with a seed and increment of one.
    /// </value>
    /// <remarks>
    /// MIGRATION: the key is the row's, not the subject's. <c>PK_ModulePermission</c> is clustered on
    /// this column alone (<c>02.02.00.SqlDataProvider</c> lines 716 to 721, dropped and re-added
    /// under a qualifier-bearing name by <c>03.00.09.SqlDataProvider</c> lines 461 and 477), while
    /// the rule about which grants may coexist is the four-column unique index described in the class
    /// remarks. The two are independent and both must be mapped.
    /// </remarks>
    public int ModulePermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => ModulePermissionId;

    /// <summary>
    /// Gets or sets the module instance this grant applies to.
    /// </summary>
    /// <value>
    /// The <c>ModuleID</c> column, <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 677),
    /// mapped as required.
    /// </value>
    /// <remarks>
    /// MIGRATION: backed by a real, cascading foreign key -
    /// <c>FK_ModulePermission_Modules</c> to <c>dbo.Modules</c> ON DELETE CASCADE
    /// (<c>02.02.00.SqlDataProvider</c> lines 762 to 767, re-created with a qualifier-bearing name by
    /// <c>03.00.09.SqlDataProvider</c> line 493). Deleting a module therefore withdraws its grants in
    /// the database, and the mapping must say <c>Cascade</c> so the model agrees with what the
    /// database will do anyway. <c>04.06.00.SqlDataProvider</c> line 1214 indexes this column.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// Gets or sets the catalogue entry being granted or refused.
    /// </summary>
    /// <value>
    /// The <c>PermissionID</c> column, <c>int NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line
    /// 678), mapped as required.
    /// </value>
    /// <remarks>
    /// MIGRATION: this property is what REPLACES the legacy inheritance. Where
    /// <c>ModulePermissionInfo</c> inherited <c>PermissionInfo</c> and copied its five members into
    /// itself, a grant now names its catalogue entry and nothing more. Consequently
    /// <c>PermissionCode</c>, <c>ModuleDefID</c>, <c>PermissionKey</c> and <c>PermissionName</c> are
    /// deliberately ABSENT from this type; they are reached through <c>Permission</c> and duplicating
    /// any of them here would create a second, divergent copy of catalogue data.
    /// <c>FK_ModulePermission_Permission</c> cascades from <c>dbo.Permission</c>
    /// (<c>02.02.00.SqlDataProvider</c> lines 768 to 773), so retiring a catalogue entry withdraws
    /// the grants that named it. <c>04.06.00.SqlDataProvider</c> line 1208 indexes this column.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <summary>
    /// Gets or sets the role the grant is addressed to, or <see langword="null"/> when the grant is
    /// not addressed to a role.
    /// </summary>
    /// <value>
    /// The <c>RoleID</c> column, <c>int NULL</c> in the terminal schema.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: this column's nullability was rewritten DESTRUCTIVELY and the property must stay
    /// nullable because of it. It was created <c>int NOT NULL</c>
    /// (<c>02.02.00.SqlDataProvider</c> line 679); <c>04.05.00.SqlDataProvider</c> lines 615 to 635
    /// then copied it to a temporary column, DROPPED it, re-added it as <c>int NULL</c>, copied the
    /// values back and dropped the temporary. Mapping it as required would reject rows the legacy
    /// application writes routinely, so the honest CLR type is <see cref="int"/>?.
    /// </para>
    /// <para>
    /// MIGRATION: a non-null value here DOES NOT GUARANTEE a matching <c>dbo.Roles</c> row, and no
    /// code may assume otherwise. The proof is structural: in the legacy upgrade chain this column
    /// never acquires a foreign key to <c>Roles</c> across any of the eighty-eight scripts - it gets
    /// an index only (<c>04.06.00.SqlDataProvider</c> line 1226) - which is precisely why the view
    /// <c>vw_ModulePermissions</c> reaches roles through a LEFT OUTER JOIN
    /// (<c>04.05.00.SqlDataProvider</c> line 685) and synthesises a name for the values that resolve
    /// to nothing: <c>-1</c> reads as "All Users", <c>-2</c> as "Superuser" and <c>-3</c> as
    /// "Unauthenticated Users" (lines 670 to 672), matching the constants at
    /// <c>Library/Components/Shared/Globals.vb</c> lines 95 to 97 and the same three-way special case
    /// in that file's <c>GetRoleName</c>. Those pseudo-principals are persisted, externally
    /// observable data. The optional <c>Role</c> navigation below is optional for this reason, and
    /// must be mapped with no delete behaviour to match a legacy database that enforces nothing here.
    /// </para>
    /// <para>
    /// MIGRATION: the absence of that foreign key is a statement about the LEGACY terminal schema,
    /// not a promise about every database this model is pointed at. A bootstrap script that creates a
    /// greenfield or test database from the model may well add a <c>RoleID</c> foreign key, and one
    /// consequence is worth knowing before it is diagnosed the hard way: such a database cannot hold
    /// the <c>-1</c>, <c>-2</c> and <c>-3</c> pseudo-principals unless it also seeds <c>Roles</c> rows
    /// for them. That is a constraint on the bootstrap, not a licence to model the relationship as
    /// required - doing so would reject rows a real DotNetNuke database contains.
    /// </para>
    /// <para>
    /// MIGRATION: <c>-4</c> is a different kind of value and is NOT expected in a row. It is
    /// <c>glbRoleNothing</c> (<c>Globals.vb</c> line 98), which the legacy constructor assigned to
    /// mean "no role chosen" in memory; <see langword="null"/> expresses that here. This property is
    /// deliberately a raw <see cref="int"/>? and NOT an enumeration: an enumeration would have to
    /// choose between admitting only the four known negatives - breaking every real role identity -
    /// or admitting everything, which is what <see cref="int"/>? already does honestly. Nothing in
    /// this layer rewrites, clamps or normalises the value; it round-trips exactly as stored.
    /// </para>
    /// </remarks>
    public int? RoleId { get; set; }

    /// <summary>
    /// Gets or sets whether this row grants the permission (<see langword="true"/>) or explicitly
    /// refuses it (<see langword="false"/>).
    /// </summary>
    /// <value>
    /// The <c>AllowAccess</c> column, <c>bit NOT NULL</c> (<c>02.02.00.SqlDataProvider</c> line 680),
    /// mapped as required.
    /// </value>
    /// <remarks>
    /// MIGRATION: this flag states what ONE row says, and this entity draws no conclusion from it.
    /// How a set of rows combines into an answer - which of an allow and a refusal wins, and what an
    /// absent grant means - is an evaluation rule, and Rule T2 puts evaluation in the layer that owns
    /// it rather than on the data it reads. The distinction matters because the legacy rule is not
    /// the obvious one: <c>ModulePermissionController.HasModulePermission</c>
    /// (<c>ModulePermissionController.vb</c> lines 33 to 49) matches on the permission key and the
    /// subject and never inspects this flag at all, while
    /// <c>PortalSecurity.IsInRoles</c> decides membership by comparing role NAMES. Restating a
    /// precedence rule here would risk contradicting the component that actually applies it.
    /// </remarks>
    public bool AllowAccess { get; set; }

    /// <summary>
    /// Gets or sets the individual account the grant is addressed to, or <see langword="null"/> when
    /// the grant is not addressed to an account.
    /// </summary>
    /// <value>
    /// The <c>UserID</c> column, <c>int NULL</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: this column did not exist in the original table. <c>04.05.00.SqlDataProvider</c>
    /// lines 640 to 655 add it as <c>int NULL</c> - guarded by a <c>COLUMNPROPERTY</c> test so the
    /// script is re-runnable - together with a foreign key to <c>dbo.Users</c> declared NOT FOR
    /// REPLICATION and carrying no ON DELETE clause. Its arrival is what makes a grant to a single
    /// account possible, and it must be mapped nullable with no delete behaviour, matching what the
    /// database enforces. <c>04.06.00.SqlDataProvider</c> line 1220 indexes it.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy code distinguished a role grant from an account grant by testing this
    /// value against the <c>-1</c> sentinel - <c>Null.IsNull(objModulePermission.UserID)</c> at
    /// <c>ModulePermissionController.vb</c> line 244 - so <c>-1</c> meant "no account" rather than
    /// "account number minus one". Under Rule T7 that test becomes a plain <see langword="null"/>
    /// check and <c>-1</c> is never written here.
    /// </para>
    /// </remarks>
    public int? UserId { get; set; }

    /// <summary>
    /// Gets or sets the module instance this grant applies to.
    /// </summary>
    /// <remarks>
    /// MIGRATION: required, because <see cref="ModuleId"/> is <c>NOT NULL</c> behind an enforced
    /// foreign key - a grant with no module cannot exist in this schema. Left unset on a newly
    /// constructed instance and populated by the persistence layer, which is why the solution
    /// suppresses CS8618 centrally rather than propping the property up with a null-forgiving
    /// initializer.
    /// </remarks>
    public Module Module { get; set; }

    /// <summary>
    /// Gets or sets the catalogue entry this grant refers to.
    /// </summary>
    /// <remarks>
    /// MIGRATION: required for the same reason as <c>Module</c> - <see cref="PermissionId"/> is
    /// <c>NOT NULL</c> behind an enforced foreign key. This navigation, and not a base class, is how
    /// the catalogue's permission key, name and scope code are reached; see the note on
    /// <see cref="PermissionId"/>.
    /// </remarks>
    public Permission Permission { get; set; }

    /// <summary>
    /// Gets or sets the role this grant is addressed to, or <see langword="null"/> when
    /// <see cref="RoleId"/> is null or names a pseudo-principal that has no <c>dbo.Roles</c> row.
    /// </summary>
    /// <remarks>
    /// MIGRATION: optional, and optional in a stronger sense than a nullable column usually implies.
    /// There is no foreign key on <c>RoleID</c> at all, so this navigation may be null even when
    /// <see cref="RoleId"/> holds a value - which is exactly the case for the persisted
    /// <c>-1</c>, <c>-2</c> and <c>-3</c> pseudo-principals. Read <see cref="RoleId"/> for the fact
    /// and treat this navigation as a convenience that may legitimately be absent.
    /// </remarks>
    public Role? Role { get; set; }

    /// <summary>
    /// Gets or sets the individual account this grant is addressed to, or <see langword="null"/> when
    /// the grant is not addressed to an account.
    /// </summary>
    /// <remarks>
    /// MIGRATION: optional, mirroring the nullable <see cref="UserId"/> column added by 04.05.00.
    /// Unlike <c>Role</c> this one does sit behind a real foreign key, so a non-null
    /// <see cref="UserId"/> does name an existing account.
    /// </remarks>
    public User? User { get; set; }
}
