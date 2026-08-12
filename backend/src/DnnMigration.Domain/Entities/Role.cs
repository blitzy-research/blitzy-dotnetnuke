using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// MIGRATION: re-authors DotNetNuke.Security.Roles.RoleInfo
// (Library/Components/Security/Roles/RoleInfo.vb lines 42-302) as a persistence POCO. The legacy
// class declares fifteen private backing fields (lines 43-57) and fifteen Property Get/Set blocks
// that do nothing but read and write them, so the target is fifteen auto-properties and the fields
// disappear. Not one of its five Imports survives: the base library, untyped collections,
// configuration and ADO.NET are unused by any member of it, and XML serialisation is used only by
// the attributes dropped below. The two imports here are the base entity and the frequency
// enumeration, and the Domain project references nothing beyond the framework by design.
//
// MIGRATION: every attribute the legacy type carried is dropped and none is replaced. The class was
// decorated <XmlRoot("role", IsNullable:=False)> (line 42); RoleID, PortalID and RoleGroupID each
// carried <XmlIgnore()> (lines 65, 80 and 95); and the remaining twelve properties each carried an
// <XmlElement(...)> naming its portal-template element (lines 110, 125, 149, 164, 188, 203, 218,
// 233, 248, 263, 278 and 293). In the target the wire contract belongs to the Application DTOs and
// the column mapping belongs to the Infrastructure Fluent configuration, so a domain entity carries
// no attribute of any kind - none for serialisation, none for validation, none for persistence.
//
// MIGRATION: ONLY THE TERMINAL SCHEMA IS AUTHORITATIVE. dbo.Roles is rebuilt twice and altered
// repeatedly across the eighty-eight-script upgrade chain, so its baseline CREATE TABLE
// (Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider lines 114-124) declares
// only eight of the fifteen columns, and two of those eight declarations are already dead by
// 01.00.04. Each property below names the script that settled it. A future reader who inspects that
// chain must search case-insensitively and across all four naming forms it uses - bare,
// dbo.-qualified, [dbo].[...]-bracketed and {databaseOwner}{objectQualifier}-templated - because a
// single-form case-sensitive search reports statements as absent when they are present.
//
// MIGRATION: this type carries state and nothing else. The legacy role-expiry arithmetic
// (RoleController.vb lines 537-549: the Null.NullInteger period guard and the Select Case over the
// frequency code, implemented with the Microsoft.VisualBasic DateAdd function imported at line 25)
// belongs to the Application layer, and the trial-versus-billing term selection at lines 521-527
// belongs there with it. No method, no date arithmetic, no clock read, no repository call, no cache
// access and no validation appears here, so nothing in this file performs I/O.

/// <summary>
/// A security role: the named grouping that permissions are granted to and that user accounts are
/// assigned to, optionally carrying the paid-membership terms under which an assignment expires.
/// </summary>
/// <remarks>
/// <para>
/// A role is either a tenant role, owned by one portal, or a host role owned by the installation
/// itself - the distinction is carried by <see cref="PortalId"/> being present or absent, and both
/// forms are normal. Membership of a <see cref="RoleGroup"/> is organisational and optional, so an
/// ungrouped role is normal too. Permissions are granted to a role and never to its group.
/// </para>
/// <para>
/// The paid-membership columns are preserved rather than dropped because the expiry date of a
/// <see cref="UserRole"/> assignment is computed from them: the trial terms govern while the trial
/// is unused, and the billing terms govern afterwards.
/// </para>
/// <para>
/// The mapping contract that the Infrastructure layer binds through its Fluent configuration. Every
/// entry is the cumulative terminal state of the upgrade chain, not the baseline declaration:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Property and column</term>
///     <description>Terminal definition and consequence</description>
///   </listheader>
///   <item>
///     <term><see cref="RoleId"/> maps <c>RoleID</c></term>
///     <description>
///     <c>int IDENTITY(0, 1) NOT NULL</c>, the primary key under <c>PK_Roles</c>. Seeded at zero,
///     so zero is a real key - see the migration note on the property.
///     </description>
///   </item>
///   <item>
///     <term><see cref="PortalId"/> maps <c>PortalID</c></term>
///     <description>
///     <c>int NULL</c>, constrained by <c>FK_Roles_Portals</c> against <c>dbo.Portals(PortalID)</c>
///     <c>ON DELETE CASCADE</c>. Absent for a host role; deleting a portal deletes its roles.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RoleGroupId"/> maps <c>RoleGroupID</c></term>
///     <description>
///     <c>int NULL</c>, constrained by <c>FK_Roles_RoleGroups</c> with <b>no</b> <c>ON DELETE</c>
///     clause, so a group cannot be deleted while a role still points at it.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RoleName"/> maps <c>RoleName</c></term>
///     <description>
///     <c>nvarchar(50) NOT NULL</c>, unique within its portal under <c>IX_RoleName</c>,
///     <c>UNIQUE NONCLUSTERED (PortalID, RoleName)</c>. Two portals may each own a role of the same
///     name.
///     </description>
///   </item>
///   <item>
///     <term><see cref="Description"/> maps <c>Description</c></term>
///     <description><c>nvarchar(1000) NULL</c>, the administrative description.</description>
///   </item>
///   <item>
///     <term><see cref="ServiceFee"/> maps <c>ServiceFee</c></term>
///     <description><c>money NULL</c> with a <c>DEFAULT (0)</c> constraint.</description>
///   </item>
///   <item>
///     <term><see cref="BillingFrequency"/> maps <c>BillingFrequency</c></term>
///     <description><c>char(1) NULL</c>, holding one of six stored letter codes.</description>
///   </item>
///   <item>
///     <term><see cref="TrialPeriod"/> maps <c>TrialPeriod</c></term>
///     <description><c>int NULL</c>, a count of trial-frequency units.</description>
///   </item>
///   <item>
///     <term><see cref="TrialFrequency"/> maps <c>TrialFrequency</c></term>
///     <description><c>char(1) NULL</c>, the same six codes over the same enumeration.</description>
///   </item>
///   <item>
///     <term><see cref="BillingPeriod"/> maps <c>BillingPeriod</c></term>
///     <description><c>int NULL</c>, a count of billing-frequency units.</description>
///   </item>
///   <item>
///     <term><see cref="TrialFee"/> maps <c>TrialFee</c></term>
///     <description><c>money NULL</c>, with no default constraint.</description>
///   </item>
///   <item>
///     <term><see cref="IsPublic"/> maps <c>IsPublic</c></term>
///     <description><c>bit NOT NULL</c> with a <c>DEFAULT (0)</c> constraint.</description>
///   </item>
///   <item>
///     <term><see cref="AutoAssignment"/> maps <c>AutoAssignment</c></term>
///     <description><c>bit NOT NULL</c> with a <c>DEFAULT (0)</c> constraint.</description>
///   </item>
///   <item>
///     <term><see cref="RsvpCode"/> maps <c>RSVPCode</c></term>
///     <description>
///     <c>nvarchar(50) NULL</c>. The column name is fully upper-case in the schema while the
///     property is not - see the migration note on the property.
///     </description>
///   </item>
///   <item>
///     <term><see cref="IconFile"/> maps <c>IconFile</c></term>
///     <description><c>nvarchar(100) NULL</c>, a file reference resolved above this layer.</description>
///   </item>
/// </list>
/// <para>
/// MIGRATION: the legacy <c>Null</c> sentinel table (Library/Components/Shared/Null.vb) is not
/// honoured by any property here. Absence is expressed by a nullable CLR type, so a null fee and a
/// fee of zero stay distinguishable, as do a null description and an empty one. Where a legacy
/// sentinel is externally observable it is reinstated at the DTO and API boundary, which is the only
/// layer whose contract is visible outside this application.
/// </para>
/// <para>
/// MIGRATION: there is no <c>RoleStatus</c> property, and none may be added. Status classifies a
/// user's <i>assignment</i> to a role - it is derived from the effective and expiry dates on
/// <c>dbo.UserRoles</c> - so a role definition has no status and <c>dbo.Roles</c> has no such
/// column. Likewise there is no <c>RoleCollection</c> counterpart: the pre-generics wrappers the
/// legacy tree derived from <c>CollectionBase</c> are subsumed by the framework's own generic
/// collection interfaces.
/// </para>
/// </remarks>
public sealed class Role : Entity<int>
{
    // MIGRATION: ZERO IS A LEGITIMATE RoleID. The column is declared IDENTITY(0, 1) in
    // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider line 115, and both
    // later rebuilds of the table restate that seed (01.00.04 line 1322 and 01.00.05 line 2748), so
    // the first role ever inserted is numbered 0 - and in a freshly provisioned installation that
    // row is the Administrators role, the most privileged one there is. Reading 0 as "absent" would
    // therefore not merely lose a row, it would lose the row that decides who can administer the
    // tenant. Concretely: never write `RoleId == 0`, `RoleId <= 0` or `RoleId == default`, never add
    // an IsNew or IsTransient member to this type, and never infer one from the key. Whether a role
    // has been written to the database is DECLARED through Entity<int>.MarkIdentityPersisted by code
    // that already knows it - nothing declares it automatically, so a materialised role reports
    // IdentityIsPersisted as false - and read back through IdentityIsPersisted; it is never
    // deduced. Note that 0 is simultaneously the CLR default of int, which is exactly why no
    // default-int identity heuristic may be applied to this property.

    /// <summary>
    /// Gets or sets the surrogate key of this role (<c>RoleID</c>).
    /// </summary>
    /// <value>
    /// The database-generated identity, seeded at 0. A value of 0 identifies the first role of the
    /// installation and must never be interpreted as an unset, missing or unsaved key.
    /// </value>
    public int RoleId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Never mapped to a column of its own; the entity configuration names <see cref="RoleId"/> as
    /// the key. Zero is a real identity here, so see the migration note above before comparing it
    /// against any reserved value.
    /// </remarks>
    public override int Identity => RoleId;

    // MIGRATION: THE COLUMN IS NOT NULL; THE PROPERTY IS NULLABLE ON PURPOSE. This comment
    // previously argued the opposite and was wrong on the chronology, so the correction is recorded
    // here rather than quietly swapped in.
    //
    // What it claimed: that the terminal column is nullable, because the baseline declares
    // `[PortalID] [int] NULL` (01.00.00 line 116), the first rebuild restates it as `PortalID int
    // NULL` (01.00.04 line 1323), and DnnSchema.sql agreed. Two of those three were superseded
    // states and the third was circular - DnnSchema.sql was scripted FROM this model, so it could
    // not be evidence about it.
    //
    // What the chain actually does: 01.00.04 rebuilds Roles through Tmp_Roles with PortalID NULL and
    // renames it back, and then 01.00.05 rebuilds it AGAIN - Tmp_Roles at line 2746 declares
    // `PortalID int NOT NULL` (line 2749), dbo.Roles is dropped at line 2779 and Tmp_Roles is
    // renamed over it at line 2781, unguarded. No later script alters the column: a case-insensitive
    // sweep of all four object-naming forms across the 83 versioned scripts finds exactly two
    // `ALTER COLUMN PortalID` statements and both are on ProfilePropertyDefinition. 01.00.05 is
    // LATER than 01.00.04, so NOT NULL is the state the chain terminates in, and the independent Red
    // Gate fresh-install snapshot agrees - `[PortalID] [int] NOT NULL` at
    // DotNetNuke.Schema.SqlDataProvider line 6209. The legacy code agrees too: RoleInfo.vb declares
    // `_PortalID As Integer` (line 44) and every RoleController member takes `PortalId As Integer`,
    // so no legacy path could write a null. All of this is recorded, with citations, in
    // backend/tests/DnnMigration.IntegrationTests/Schema/TerminalSchema.manifest.
    //
    // WHY THE PROPERTY STAYS int? ANYWAY, and why that is not a contradiction: the terminal listing
    // procedure filters on `( R.PortalId = @PortalId OR R.PortalId is null )`
    // (04.08.00.SqlDataProvider line 40) and IRoleRepository.GetByPortalIdAsync reproduces it
    // verbatim under the Minimal Change Clause. That predicate cannot be expressed over a
    // non-nullable property. A nullable property over a NOT NULL column is the permissive direction
    // and is safe: every read succeeds, and a write of null is refused by the database rather than
    // by the model. Tightening to int would delete the predicate and would also throw
    // SqlNullValueException against any installation later upgraded to a DotNetNuke version that
    // relaxed the column.
    //
    // WHAT MUST NOT BE INFERRED FROM IT: that a role with no owning portal exists. Against a
    // faithful installation it cannot, the null branch of that predicate is unsatisfiable, and no
    // test may fabricate such a row to exercise it - two once did, and they passed only because the
    // test schema had drifted. The divergence is asserted by name in LegacySchemaFidelityTests and
    // recorded in MIGRATION_NOTES.md.

    /// <summary>
    /// Gets or sets the portal that owns this role (<c>PortalID</c>, cascade delete).
    /// </summary>
    /// <value>
    /// The identity of the owning portal. The terminal column is <c>int NOT NULL</c>, so a stored row
    /// always carries one; the property is nullable only so that the legacy listing predicate can be
    /// expressed, and <see langword="null"/> is refused by the store. Note that
    /// <c>dbo.Portals.PortalID</c> is itself <c>IDENTITY(-1, 1)</c>, so -1 and 0 are both real portal
    /// identities and neither of them means "no portal".
    /// </value>
    public int? PortalId { get; set; }

    // MIGRATION: THIS FOREIGN KEY IS NULLABLE AND MUST NOT BE TIGHTENED EITHER, for the same reason
    // and against the same two temptations. RoleInfo.vb declares `_RoleGroupID As Integer` (line 45)
    // as a non-nullable VB value type, and the column is nonetheless added as `RoleGroupID int NULL`
    // - 03.02.03.SqlDataProvider line 34, re-issued verbatim under the same IF NOT EXISTS guard at
    // 04.00.04 line 67 for installations that skipped the earlier script - with nothing anywhere in
    // the chain narrowing it afterwards. Group membership is genuinely optional, so the null is the
    // representation of an ungrouped role and not a marker standing in for one.

    /// <summary>
    /// Gets or sets the role group that this role belongs to (<c>RoleGroupID</c>).
    /// </summary>
    /// <value>
    /// The identity of the owning group, or <see langword="null"/> when the role is ungrouped, which
    /// is the ordinary case. Zero is a real group identity - <c>dbo.RoleGroups.RoleGroupID</c> is
    /// also <c>IDENTITY(0, 1)</c> - so it may not be read as "ungrouped".
    /// </value>
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Gets or sets the display name of this role, unique within its portal (<c>RoleName</c>,
    /// required, 50 characters).
    /// </summary>
    /// <remarks>
    /// The initialiser keeps a freshly constructed instance non-null until the caller or the
    /// materialiser assigns the real name. It is not the legacy empty-string null sentinel and not a
    /// marker for absence: the column is <c>NOT NULL</c>, so this entity has no way to represent a
    /// role without a name, and an empty name is rejected by the Application layer rather than
    /// stored. That is why this property alone among the text properties is non-nullable.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrative description of this role (<c>Description</c>, 1000
    /// characters).
    /// </summary>
    /// <value>
    /// The description, or <see langword="null"/> when the column is <c>NULL</c>. Nullability
    /// mirrors the column exactly, so a null description and an empty one stay distinguishable
    /// instead of collapsing onto the legacy empty-string sentinel.
    /// </value>
    public string? Description { get; set; }

    // MIGRATION: legacy Single becomes decimal?, and the baseline decimal(5, 2) is obsolete. This
    // property is the clearest demonstration in the whole aggregate of why only the terminal schema
    // may be trusted. RoleInfo.vb declares it `As Single` (line 164) - binary floating point - and
    // the baseline DDL declares `[ServiceFee] [decimal](5, 2) NULL` (01.00.00 line 119), a
    // declaration that would cap every fee in the installation at 999.99. BOTH ARE DEAD. The table
    // is rebuilt with `ServiceFee money NULL` at 01.00.04 line 1326, whose row copy converts the old
    // column across with CONVERT(money, ServiceFee) at line 1341; rebuilt as money again at 01.00.05
    // line 2752; and the terminal statement is `ALTER TABLE ...Roles ALTER COLUMN [ServiceFee]
    // [money] NULL` at 03.01.01 line 1173, with line 1177 restoring the DEFAULT (0) constraint that
    // the rebuilds had dropped. SQL money is a fixed-point type, so the faithful CLR mapping is
    // decimal and binary floating point is not used for a monetary amount - reproducing the legacy
    // Single would reintroduce representation loss that the column itself does not have. This is an
    // AAP-mandated documented divergence and it is recorded in repository-root MIGRATION_NOTES.md.
    //
    // OBLIGATION ON INFRASTRUCTURE: the Fluent configuration must declare HasColumnType("money") on
    // this property explicitly. Left to convention, decimal maps to decimal(18, 2), which is a
    // different store type from the one the existing column has.

    /// <summary>
    /// Gets or sets the recurring subscription fee for this role (<c>ServiceFee</c>, SQL
    /// <c>money</c>, database default 0).
    /// </summary>
    /// <value>
    /// The fee charged once per <see cref="BillingPeriod"/> units of <see cref="BillingFrequency"/>,
    /// or <see langword="null"/> when the column is <c>NULL</c>. A null fee and a fee of zero are
    /// deliberately different states: the first is a role with no fee recorded, the second a role
    /// explicitly priced at nothing.
    /// </value>
    public decimal? ServiceFee { get; set; }

    // MIGRATION: legacy String becomes the shared BillingFrequency enumeration, and the stored
    // characters are the contract. RoleInfo.vb declares this property `As String` (line 149) over a
    // char(1) column, documenting the six codes N, O, D, W, M and Y in its own remarks (lines
    // 140-145). Those six letters are the literal bytes sitting in this column of every existing
    // database: 01.00.08.SqlDataProvider seeds them into the CodeFrequency lookup table and rewrites
    // the column from the earlier numeric codes '0' to '5' onto them, in lower-case unqualified SQL,
    // and no later script re-seeds that table. Enums.BillingFrequency already encodes each letter as
    // its own underlying ushort value, so a member converts to and from the persisted character
    // without loss.
    //
    // OBLIGATION ON INFRASTRUCTURE: persist through a STRING conversion over those six codes.
    // Persisting the enum ordinal instead would write a number into a char(1) column and silently
    // mis-read every existing row. The legacy guarantee is gone from the database and cannot be
    // relied upon to catch it: FK_Roles_CodeFrequency, the constraint that once confined this column
    // to the lookup table, is dropped at 03.00.01 line 1297 and never restored, so the terminal
    // column carries no foreign key and no check constraint and will accept any single character.

    /// <summary>
    /// Gets or sets the unit in which the billing cycle of this role is counted
    /// (<c>BillingFrequency</c>, SQL <c>char(1)</c>).
    /// </summary>
    /// <value>
    /// One of the six stored codes, or <see langword="null"/> when the column is <c>NULL</c>. Null
    /// and <see cref="Enums.BillingFrequency.None"/> are different states: the first is a role with
    /// no frequency recorded, the second a role explicitly billed under the <c>'N'</c> code, which
    /// the legacy expiry logic tested for by name.
    /// </value>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Gets or sets how many <see cref="TrialFrequency"/> units the trial period of this role runs
    /// for (<c>TrialPeriod</c>, SQL <c>int</c>).
    /// </summary>
    /// <value>
    /// The count of trial units, or <see langword="null"/> when the column is <c>NULL</c>.
    /// </value>
    public int? TrialPeriod { get; set; }

    // MIGRATION: the same enumeration serves this column, and a second trial-specific type is
    // forbidden. RoleInfo.vb declares this property `As String` too (line 188) and documents the
    // identical six codes (lines 179-184); 01.00.08 rewrites this column onto the letters in the
    // same pass that rewrites the billing column, from the same lookup table. One code set, one
    // type. Duplicating Enums.BillingFrequency as a trial-only enumeration would create a second
    // authority on what a stored letter means, and the two would drift.

    /// <summary>
    /// Gets or sets the unit in which the trial period of this role is counted
    /// (<c>TrialFrequency</c>, SQL <c>char(1)</c>).
    /// </summary>
    /// <value>
    /// One of the same six stored codes as <see cref="BillingFrequency"/>, or
    /// <see langword="null"/> when the column is <c>NULL</c>. The legacy assignment path selected
    /// the trial terms over the billing terms precisely when this value was not the <c>'N'</c> code,
    /// so <see cref="Enums.BillingFrequency.None"/> carries real meaning here and is not a stand-in
    /// for the null.
    /// </value>
    public BillingFrequency? TrialFrequency { get; set; }

    // MIGRATION: int?, and the legacy provider's String declaration is a defect that is deliberately
    // NOT reproduced. Library/Providers/MembershipProviders/DataProvider/DataProvider.vb declares
    // `ByVal BillingPeriod As String` in both AddRole (line 95) and UpdateRole (line 97). Everything
    // else disagrees with it: RoleInfo.vb declares the property `As Integer` (line 218); the column
    // is added as `BillingPeriod int NULL` (01.00.08 line 6829); and decisively the stored
    // procedures those provider methods call declare the parameter `@BillingPeriod int` (03.02.03
    // line 182 and 04.00.04 line 218), so the string was converted straight back to an integer at
    // the boundary. The measured entity and schema contract is an integer and that is what this
    // property preserves. Note that the sibling TrialPeriod parameter is correctly declared
    // `As Integer` in the very same signatures, which is what identifies this as a slip rather than
    // an intentional representation.

    /// <summary>
    /// Gets or sets how many <see cref="BillingFrequency"/> units the billing cycle of this role
    /// runs for (<c>BillingPeriod</c>, SQL <c>int</c>).
    /// </summary>
    /// <value>
    /// The count of billing units, or <see langword="null"/> when the column is <c>NULL</c>.
    /// </value>
    public int? BillingPeriod { get; set; }

    // MIGRATION: legacy Single becomes decimal? here too. RoleInfo.vb declares this property
    // `As Single` (line 233), but unlike the service fee this column is money from birth -
    // 01.00.08 line 6830 adds it as `TrialFee money NULL` and no script ever alters it - so the
    // legacy declaration was mismatched with its own column from the moment the column existed.
    // Fixed-point money maps to decimal for the same reason as the service fee, and no default
    // constraint is ever declared for this one. This divergence is recorded in repository-root
    // MIGRATION_NOTES.md alongside the service fee.
    //
    // OBLIGATION ON INFRASTRUCTURE: declare HasColumnType("money") on this property explicitly, as
    // for the service fee. Convention would otherwise map it to decimal(18, 2).

    /// <summary>
    /// Gets or sets the one-off fee charged for the trial period of this role (<c>TrialFee</c>, SQL
    /// <c>money</c>).
    /// </summary>
    /// <value>
    /// The trial fee, or <see langword="null"/> when the column is <c>NULL</c>. As with
    /// <see cref="ServiceFee"/>, a null fee and a fee of zero are different states.
    /// </value>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Gets or sets whether users may subscribe themselves to this role (<c>IsPublic</c>, required,
    /// database default <see langword="false"/>).
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the role is offered for self-subscription. Non-nullable because
    /// the column is: 01.00.08 line 6831 adds it as <c>bit NOT NULL</c> with a <c>DEFAULT 0</c>
    /// constraint and 03.01.01 line 1174 re-asserts the nullability, so there is no third,
    /// unspecified state.
    /// </value>
    public bool IsPublic { get; set; }

    /// <summary>
    /// Gets or sets whether every newly registered user is assigned to this role automatically
    /// (<c>AutoAssignment</c>, required, database default <see langword="false"/>).
    /// </summary>
    /// <value>
    /// <see langword="true"/> when membership is granted on registration. Non-nullable for the same
    /// reason as <see cref="IsPublic"/>: 01.00.08 line 6832 adds it as <c>bit NOT NULL</c> with a
    /// <c>DEFAULT 0</c> constraint and 03.01.01 line 1175 re-asserts it. The same script that
    /// introduced the column set it on the Registered Users role of every existing installation.
    /// </value>
    public bool AutoAssignment { get; set; }

    // MIGRATION: THE COLUMN NAME IS RSVPCode, FULLY UPPER-CASE, AND THE PROPERTY NAME DOES NOT
    // RENAME IT. The property is spelled RsvpCode because C# treats a multi-letter initialism as a
    // word, matching the legacy RoleInfo.vb member RSVPCode (line 278) in meaning but not in
    // spelling. The column it maps is `RSVPCode nvarchar(50) NULL`, added at 03.02.03 line 45 and
    // re-issued at 04.00.04 line 80. This is the one property in the aggregate where ordinary
    // property-name modernisation and the immutable column name disagree, so the Fluent
    // configuration must name the column explicitly rather than let convention derive it from the
    // property. Deriving it would look for a column named RsvpCode, which does not exist.

    /// <summary>
    /// Gets or sets the invitation code a user presents in order to join this role
    /// (<c>RSVPCode</c>, 50 characters).
    /// </summary>
    /// <value>
    /// The code, or <see langword="null"/> when the role is not joinable by invitation.
    /// </value>
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Gets or sets the icon displayed beside this role in the administrative user interface
    /// (<c>IconFile</c>, 100 characters).
    /// </summary>
    /// <value>
    /// The raw stored file reference, or <see langword="null"/> when none is set. Left unresolved
    /// here by design: turning a stored reference into a usable address is a presentation concern
    /// and would require I/O, which this layer does not perform.
    /// </value>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets the portal that owns this role.
    /// </summary>
    /// <value>
    /// The owning portal, or <see langword="null"/> either because the role is a host role with no
    /// portal at all or because the navigation was simply not loaded. The two cases are told apart
    /// by <see cref="PortalId"/>, which is the value the write path sets; this navigation is
    /// populated only by a query that explicitly loads the principal.
    /// </value>
    public Portal? Portal { get; set; }

    /// <summary>
    /// Gets or sets the group that this role belongs to.
    /// </summary>
    /// <value>
    /// The owning group, or <see langword="null"/> either because the role is ungrouped or because
    /// the navigation was not loaded - a distinction carried by <see cref="RoleGroupId"/>. The
    /// inverse of <c>RoleGroup.Roles</c>.
    /// </value>
    public RoleGroup? RoleGroup { get; set; }

    /// <summary>
    /// Gets the assignments that join user accounts to this role.
    /// </summary>
    /// <value>
    /// The inverse of <c>UserRole.Role</c>, empty for a role nobody has been assigned to. Never
    /// <see langword="null"/>: the collection is initialised on construction so that callers and the
    /// materialiser can add to it without a null check, and the reference itself is fixed for the
    /// lifetime of the instance. Each assignment carries its own effective and expiry dates, which
    /// is where a membership status is derived from - never from this role.
    /// </value>
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();

    /// <summary>
    /// Gets the module permission grants made to this role.
    /// </summary>
    /// <value>
    /// The inverse of <c>ModulePermission.Role</c>, empty when the role has been granted nothing on
    /// any module. Never <see langword="null"/>, for the same reason as
    /// <see cref="UserRoles"/>.
    /// </value>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>
    /// Gets the page permission grants made to this role.
    /// </summary>
    /// <value>
    /// The inverse of <c>TabPermission.Role</c>, empty when the role has been granted nothing on any
    /// page. Never <see langword="null"/>, for the same reason as <see cref="UserRoles"/>.
    /// </value>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
