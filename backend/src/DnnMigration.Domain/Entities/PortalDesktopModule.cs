using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: this type models the terminal dbo.PortalDesktopModules BASE TABLE, not the legacy
// PortalDesktopModuleInfo class. Those are not the same shape. The legacy class declared five
// properties - PortalDesktopModuleID, DesktopModuleID, FriendlyName, PortalID and PortalName - but
// the table has only three columns, and Website/Providers/DataProviders/SqlDataProvider/
// 02.02.02.SqlDataProvider lines 3028 to 3034 create it with exactly those three:
//
//     CREATE TABLE PortalDesktopModules
//         (
//         PortalDesktopModuleID int NOT NULL IDENTITY (1, 1),
//         PortalID int NOT NULL,
//         DesktopModuleID int NOT NULL
//         )  ON [PRIMARY]
//
// MIGRATION: FriendlyName and PortalName are therefore JOIN PROJECTIONS, and the same script proves
// it. The GetPortalDesktopModules procedure at lines 3148 to 3163 reads
// "select PortalDesktopModules.*, PortalName, FriendlyName from PortalDesktopModules inner join
// Portals ... inner join DesktopModules ...", so the two names arrive from the principal tables, one
// from Portals.PortalName and one from DesktopModules.FriendlyName. The legacy reader then filled all
// five properties indiscriminately through the reflection hydrator - CBO.FillCollection(...,
// GetType(PortalDesktopModuleInfo)) at Library/Components/Modules/DesktopModuleController.vb lines 66
// to 67 - which is precisely why a result-set shape came to be mistaken for an entity shape.
//
// MIGRATION: both names are consequently ABSENT here by design, and neither may be added back as a
// scalar. A scalar would be a column this table does not have. Their only legacy purpose was display
// text: Website/admin/Portal/SiteSettings.ascx.vb lines 363 to 375 binds the hydrated list straight
// to a dual-list picker so an administrator can read module and portal names. That is a presentation
// concern, so the two names belong on an Application-layer DTO composed from this entity's Portal and
// DesktopModule references (or from a projection over them) whenever a caller needs them.
//
// MIGRATION: the legacy class also declared six imports it never used - System, System.Configuration,
// System.Data, System.Globalization, System.IO and System.Xml - together with private backing fields,
// an empty constructor and five Property Get/Set blocks. None of that is carried forward: auto-
// properties express the same contract, and the Domain project takes no dependency at all.
//
// MIGRATION: 02.02.02 is the TERMINAL shape of this table, not merely its first. Across all 88
// upgrade scripts exactly seven ALTER TABLE statements name PortalDesktopModules: the four in this
// script that add the constraints recorded below, and three DROP CONSTRAINT statements in
// UnInstall.SqlDataProvider (lines 59, 219 and 221) that only ever run when DotNetNuke is being
// removed. No later script widens, narrows or re-types a column - 03.02.00, 04.03.06 and 04.05.00
// merely read the table in a join - so the three properties below are the whole persisted row.
//
// MIGRATION: the table itself supersedes PortalModuleDefinitions, whose rows were copied across by
// the INSERT ... SELECT at line 3077 before the old table was dropped at line 3093. Nothing of the
// dropped table survives in the target model; only this successor is mapped.

/// <summary>
/// One grant entitling a portal to use an installed module package: a row of the join table between
/// <see cref="Entities.Portal"/> and <see cref="Entities.DesktopModule"/>.
/// </summary>
/// <remarks>
/// <para>
/// A grant carries no state of its own beyond the pair it joins. Its existence is the fact being
/// recorded, which is what makes <see cref="Entities.DesktopModule.IsPremium"/> enforceable: a premium
/// package may be placed on a portal only when a row exists here for that pair, whereas a non-premium
/// package needs no row at all. Withdrawing the entitlement means deleting the row, never blanking a
/// field on it.
/// </para>
/// <para>
/// The entity carries no attribute and takes no dependency, so nothing here states how it is stored.
/// Table and column naming, the identity column, the unique pair constraint and both cascading
/// relationships are bound explicitly by <c>PortalDesktopModuleConfiguration</c> in the Infrastructure
/// layer, because the legacy installation runs with an empty object qualifier and <c>dbo</c> as its
/// database owner. Every obligation that configuration inherits is recorded against the member it
/// concerns, so that none of them can be lost to a well-meaning convention.
/// </para>
/// <para>
/// Two obligations belong to the type as a whole rather than to any single member, and both are
/// statements about schema that already exists - they are honoured, never invented:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>The pair is unique.</b> <c>IX_PortalDesktopModules</c>, added at 02.02.02 line 3044, is a
///     <c>UNIQUE NONCLUSTERED</c> index over <c>(PortalID, DesktopModuleID)</c> in that column order,
///     so one portal can never be granted the same package twice. That pair is the natural key behind
///     the surrogate identity, and the Infrastructure configuration must declare the index with its
///     legacy name so the constraint keeps being enforced by the database rather than by a service.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Both relationships cascade, exactly as the schema has them.</b>
///     <c>FK_PortalDesktopModules_Portals</c> (line 3065) and
///     <c>FK_PortalDesktopModules_DesktopModules</c> (line 3053) are each declared
///     <c>ON DELETE CASCADE</c> and <c>NOT FOR REPLICATION</c>, so deleting a portal or uninstalling a
///     package withdraws the grants that referenced it. The Infrastructure configuration must preserve
///     both behaviours under their legacy constraint names. Substituting <c>Restrict</c>,
///     <c>SetNull</c> or <c>NoAction</c> would invent schema semantics this migration is forbidden to
///     change, and would strand rows that the live database removes today. The two cascade paths do
///     not converge on one principal, so declaring both is safe.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class PortalDesktopModule : Entity<int>
{
    /// <summary>
    /// Gets or sets the <c>PortalDesktopModuleID</c> column: <c>int NOT NULL IDENTITY (1, 1)</c>, the
    /// clustered primary key named <c>PK_PortalDesktopModules</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database assigns this value, so the Infrastructure configuration must map it as generated on
    /// add with a seed and increment of one; the <c>AddPortalDesktopModule</c> procedure at 02.02.02
    /// line 3111 confirms as much by returning <c>SCOPE_IDENTITY()</c> after its insert.
    /// </para>
    /// <para>
    /// Because the seed is 1, no key value of this table collides with the legacy <c>-1</c> integer
    /// sentinel - unlike <c>dbo.Portals</c>, whose identity seeds at -1. The mapped column name is
    /// <c>PortalDesktopModuleID</c> with a fully upper-cased <c>ID</c> suffix, which no C# naming
    /// convention would produce from this member, so the configuration must name it explicitly.
    /// </para>
    /// </remarks>
    public int PortalDesktopModuleId { get; set; }

    /// <summary>
    /// Gets the value equality is based on, which is always <see cref="PortalDesktopModuleId"/>.
    /// </summary>
    /// <remarks>
    /// Not a column and not part of the natural key, so the entity configuration must ignore it or the
    /// model builder discovers it by convention and the provider then fails on a column the table does
    /// not have.
    /// </remarks>
    public override int Identity => PortalDesktopModuleId;

    /// <summary>
    /// Gets or sets the <c>PortalID</c> column: <c>int NOT NULL</c>, the portal being granted the
    /// entitlement.
    /// </summary>
    /// <remarks>
    /// Required, and required at the database rather than only in code: the column is <c>NOT NULL</c>
    /// and <c>FK_PortalDesktopModules_Portals</c> constrains it to <c>dbo.Portals.PortalID</c>. Read
    /// this member, not <see cref="Portal"/>, when only the portal's identity is needed; -1 and 0 are
    /// both legitimate portal keys here, because <c>dbo.Portals.PortalID</c> is declared
    /// <c>IDENTITY(-1, 1)</c>, so neither value may ever be read as "absent". The mapped column name is
    /// <c>PortalID</c>, and it is the first column of the unique pair.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the <c>DesktopModuleID</c> column: <c>int NOT NULL</c>, the installed module
    /// package the entitlement covers.
    /// </summary>
    /// <remarks>
    /// Required, and required at the database rather than only in code: the column is <c>NOT NULL</c>
    /// and <c>FK_PortalDesktopModules_DesktopModules</c> constrains it to
    /// <c>dbo.DesktopModules.DesktopModuleID</c>. Read this member, not <see cref="DesktopModule"/>,
    /// when only the package's identity is needed. The mapped column name is <c>DesktopModuleID</c>,
    /// and it is the second column of the unique pair.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    // MIGRATION: the two references below replace the flattened join that the legacy model used in
    // place of a relationship. They are declared non-nullable because both relationships are REQUIRED:
    // each foreign-key column is NOT NULL and each foreign key cascades, so a grant without a portal or
    // without a package is not a state the database can hold. Neither carries an initialiser and
    // neither is null-forgiven - CS8618 is suppressed once, centrally, in backend/Directory.Build.props
    // precisely because the persistence layer performs the assignment the compiler cannot see.
    //
    // MIGRATION: required is not the same as loaded, and no caller may confuse the two. A reference is
    // populated only on a read that asked for it, so load state is the repository's decision and is
    // never inferred from a reference: ModuleDefinitionRepository.GetPortalDesktopModulesAsync includes
    // the package deliberately, so that naming what has been granted costs no further round trip. Read
    // the foreign-key property above whenever the identity alone will do.

    /// <summary>
    /// Gets or sets the portal granted the entitlement - the principal identified by
    /// <see cref="PortalId"/>, and the inverse of <see cref="Entities.Portal.PortalDesktopModules"/>.
    /// </summary>
    /// <remarks>
    /// Reach the portal's own members, including its name, through this reference; none of them is
    /// duplicated onto this entity. The legacy <c>PortalName</c> projection is what this reference
    /// replaces, so an Application DTO that needs the name composes it from here.
    /// </remarks>
    public Portal Portal { get; set; }

    /// <summary>
    /// Gets or sets the installed module package the entitlement covers - the principal identified by
    /// <see cref="DesktopModuleId"/>, and the inverse of
    /// <see cref="Entities.DesktopModule.PortalDesktopModules"/>.
    /// </summary>
    /// <remarks>
    /// Reach the package's own members through this reference, including
    /// <see cref="Entities.DesktopModule.IsPremium"/>, which is the flag that makes this grant matter,
    /// and <see cref="Entities.DesktopModule.FriendlyName"/>, which is what the legacy
    /// <c>FriendlyName</c> projection carried. An Application DTO that needs the friendly name composes
    /// it from here.
    /// </remarks>
    public DesktopModule DesktopModule { get; set; }
}
