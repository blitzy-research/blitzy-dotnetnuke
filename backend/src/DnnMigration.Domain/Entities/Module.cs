using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: ==================================================================================
// SOURCE
//   DotNetNuke.Entities.Modules.ModuleInfo - Library/Components/Modules/ModuleInfo.vb (936
//   lines; 57 properties in its "Public Properties" region at lines 131-627 plus Cacheability at
//   line 925, 58 in total).
//
// WHY THAT CLASS IS NOT PORTED PROPERTY-FOR-PROPERTY
//   ModuleInfo was never the shape of a dbo.Modules row. It was the materialised result of a
//   four-way-plus join - Modules against TabModules, against ModuleDefinitions and its parent
//   DesktopModules, against ModuleControls - with a permission collection and per-request
//   presentation state carried alongside. Measured against the terminal schema, only 11 of its 58
//   properties correspond to a column this table still has, so reproducing the rest would declare
//   properties for columns that do not exist:
//     - 03.00.01 lines 197-199 dropped ModuleOrder, PaneName, CacheTime, Alignment, Color,
//       Border, IconFile, Personalize, ShowTitle and ContainerSrc, and lines 204-205 dropped
//       TabID, after lines 19-32 of the same script created dbo.TabModules to hold the facts that
//       vary per placement. Those facts now live on TabModule.
//     - 03.00.01 lines 1401-1405 dropped AuthorizedEditRoles and AuthorizedViewRoles, yet
//       ModuleInfo kept exposing them (lines 545 and 554) alongside AuthorizedRoles, whose own
//       comment at line 626 reads "should be deprecated due to roles being abstracted". Grants
//       are rows in dbo.ModulePermission, reached through ModulePermissions below.
//     - 02.00.00 lines 6559 and 6563 dropped Container and ShowMobile.
//   The definition, desktop-module and control projections describe other tables entirely and
//   belong to ModuleDefinition, DesktopModule and ModuleControl.
//
// TERMINAL COLUMN SET - the 11 scalars declared here, and no others
//   Derived by replaying every CREATE and ALTER TABLE against this table across the 88 upgrade
//   scripts in Website/Providers/DataProviders/SqlDataProvider, then independently confirmed
//   against backend/tests/DnnMigration.IntegrationTests/Schema/DnnSchema.sql lines 205-220:
//     01.00.00:220  CREATE TABLE [dbo].[Modules] - ModuleID, ModuleDefID, ModuleTitle
//     01.00.04:85   ADD AllTabs bit NOT NULL DEFAULT 0
//     02.00.00:6567 ADD IsDeleted bit NOT NULL DEFAULT 0
//     02.02.00:44   ADD InheritViewPermissions bit NULL
//     02.02.00:321  ADD Header ntext NULL, Footer ntext NULL, StartDate datetime NULL,
//                       EndDate datetime NULL
//     03.00.01:11   ADD PortalID int NULL
//     03.01.01:1027 AllTabs and IsDeleted restated bit NOT NULL, their defaults re-established
//                       at 0 by lines 1030 and 1032
//   The table is created exactly once and is never dropped or recreated anywhere in the chain, so
//   that replay is complete rather than merely current. 04.05.00 alters DesktopModules only and
//   leaves this table untouched, and no sp_rename in the chain renames a Modules column.
//
// OBLIGATIONS ON THE INFRASTRUCTURE MAPPING (this layer declares no mapping of its own)
//   - Map this type to dbo.Modules alone. It is not a view over the join described above.
//   - Map ModuleDefinitionId with HasColumnName("ModuleDefID"). The property name is spelled out
//     for readability, the column is not, and convention would otherwise look for a column named
//     ModuleDefinitionId and fail.
//   - Keep Header and Footer as ntext. The legacy column type is part of the immutable schema;
//     converting it to a modern large-object type would be a schema change, which this migration
//     does not make.
//   - PortalID carries no ON DELETE clause (03.00.01 lines 208-215, restated as
//     FK_{objectQualifier}Modules_{objectQualifier}Portals at 03.00.09 lines 295-297), so
//     removing a portal does not remove its modules at the database level.
//
// NEVER RE-ADD - each of these would bind a dropped column or flatten a join back into this row
//   From TabModules: TabId, TabModuleId, ModuleOrder, PaneName, CacheTime, Alignment, Color,
//     Border, IconFile, Visibility, ContainerSrc, DisplayTitle, DisplayPrint, DisplaySyndicate.
//   From ModuleDefinitions, DesktopModules and ModuleControls: DesktopModuleId, FolderName,
//     FriendlyName, Description, Version, IsPremium, IsAdmin, BusinessControllerClass,
//     ModuleName, SupportedFeatures, CompatibleVersions, Dependencies, Permissions,
//     DefaultCacheTime, ModuleControlId, ControlSrc, ControlType, ControlTitle, HelpUrl,
//     SupportsPartialRendering.
//   Role strings and presentation state: AuthorizedEditRoles, AuthorizedViewRoles,
//     AuthorizedRoles, ContainerPath, PaneModuleIndex, PaneModuleCount, IsDefaultModule,
//     AllModules, IsPortable, IsSearchable, IsUpgradeable, Cacheability.
//   Visibility in particular is not, and never was, a Modules column. A scan of every CREATE
//     TABLE body in all 88 scripts finds it only on TabModules (03.00.01 line 31,
//     "Visibility int NOT NULL") and on the unrelated UserProfile table, and no ALTER TABLE in
//     the chain ever adds it. It lives on TabModule.Visibility, typed ModuleVisibility, and
//     belongs nowhere else - least of all here, under that name or any name resembling it.
//
// MEMBERS OF ModuleInfo DELIBERATELY NOT PORTED
//   - Initialize(PortalId), lines 722-788, reads site settings at line 772 and constructs a
//     ModuleController to query the database at lines 774-775. A domain entity performs no I/O,
//     so it has no counterpart here; defaulting a new module is an application service's work.
//   - Clone(), lines 656-720, copied all 58 properties by hand.
//   - IPropertyAccess (line 37), GetProperty (line 794) and Cacheability (line 925) served the
//     token-replacement subsystem, which this migration excludes.
//   - The VisibilityState enum, lines 30-34, is superseded by ModuleVisibility on TabModule.
//   - The XmlRoot, XmlElement, XmlIgnore and XmlArray attributes are gone. The wire contract
//     belongs to the DTOs at the API boundary, so no domain entity carries an attribute of any
//     kind.
//   - The sentinel initialisation in the constructor (lines 105-124) and in Initialize (lines
//     724-769) is gone. Library/Components/Shared/Null.vb defines NullInteger as -1 (line 43),
//     NullDate as Date.MinValue (line 68) and NullString as the empty string (line 73) - the last
//     of which leaves a SQL NULL indistinguishable from a zero-length string once read. Absence
//     is expressed here by a nullable CLR type and by nothing else. Where a sentinel is
//     externally observable it is reinstated at the DTO boundary, never in this model.
// =============================================================================================

/// <summary>
/// One module instance owned by a portal: the content unit that one or more pages display, and the
/// persistence shape of a single row of the legacy <c>dbo.Modules</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This type is deliberately a plain object. It declares 11 scalars, two reference navigations and
/// three collections, and nothing else - no attribute, no mapping instruction, no behaviour and no
/// I/O. Everything that varies per page placement is on <see cref="TabModule"/>; everything that
/// describes the kind of module rather than this instance of it is on
/// <see cref="ModuleDefinition"/> and, above that, <c>DesktopModule</c>; and every grant is a row
/// reached through <see cref="ModulePermissions"/>.
/// </para>
/// <para>
/// The schema is immutable for this migration, so this file describes a table that already exists
/// rather than defining one. Three invariants govern every consumer. Zero is a real
/// <see cref="ModuleId"/>. A null <see cref="InheritViewPermissions"/> is a third state and not a
/// synonym for <see langword="false"/>. A null <see cref="PortalId"/> means the installation owns
/// the module, not that the owner is unknown.
/// </para>
/// </remarks>
public sealed class Module : Entity<int>
{
    /// <summary>
    /// Gets or sets the <c>ModuleID</c> column: <c>int IDENTITY(0, 1) NOT NULL</c>, the primary
    /// key, generated by the database.
    /// </summary>
    /// <remarks>
    /// Zero identifies the first module of a fresh installation and is therefore a real, persisted
    /// key. Whether this entity has been written is answered by
    /// <see cref="Entity{TId}.IdentityIsPersisted"/>, which only a caller that already knows the row
    /// exists may declare through <see cref="Entity{TId}.MarkIdentityPersisted"/> and which nothing
    /// declares automatically - never by inspecting this value.
    /// </remarks>
    // MIGRATION: dbo.Modules.ModuleID is declared IDENTITY(0, 1) - Website/Providers/
    // DataProviders/SqlDataProvider/01.00.00.SqlDataProvider line 221, confirmed by
    // backend/tests/DnnMigration.IntegrationTests/Schema/DnnSchema.sql line 206. Zero is a
    // legitimate persisted identifier. It must never be read as absent, transient, unsaved or
    // "default", and no `ModuleId == 0`, `ModuleId <= 0` or `default(int)` heuristic may be
    // written against it anywhere in this solution.
    public int ModuleId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Never mapped to a column. The entity configuration names <see cref="ModuleId"/> in its
    /// explicit key declaration; this member exists only so the base type can compare entities.
    /// </remarks>
    public override int Identity => ModuleId;

    /// <summary>
    /// Gets or sets the <c>ModuleDefID</c> column: <c>int NOT NULL</c>, the required foreign key to
    /// the definition this instance was created from.
    /// </summary>
    /// <remarks>
    /// Required in the schema since the table was created, with
    /// <c>FK_Modules_ModuleDefinitions ... ON DELETE CASCADE</c>, so removing a definition removes
    /// every instance of it. The property name is expanded; the column name is not.
    /// </remarks>
    // MIGRATION: the column is spelled ModuleDefID, not ModuleDefinitionId. Infrastructure must
    // therefore map this property with HasColumnName("ModuleDefID"); relying on convention would
    // look for a column that does not exist.
    public int ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the <c>ModuleTitle</c> column: <c>nvarchar(256) NULL</c>, the title rendered in
    /// the module's container.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is. An untitled module and a module titled with an empty string
    /// are distinct states here, which they were not in the legacy model.
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Gets or sets the <c>AllTabs</c> column: <c>bit NOT NULL</c> defaulting to <c>0</c>, whether
    /// the instance appears on every page of its portal rather than only where it is placed.
    /// </summary>
    public bool AllTabs { get; set; }

    /// <summary>
    /// Gets or sets the <c>IsDeleted</c> column: <c>bit NOT NULL</c> defaulting to <c>0</c>, the
    /// soft-delete flag that moves the instance into the recycle bin without removing the row.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the <c>InheritViewPermissions</c> column: <c>bit NULL</c>, whether view access
    /// is inherited from the hosting page instead of being granted on the module itself.
    /// </summary>
    /// <remarks>
    /// A tri-state, and the third state carries meaning: <see langword="true"/> defers to the
    /// page's grants, <see langword="false"/> applies this module's own grants, and
    /// <see langword="null"/> is a row the flag was never written on. Test for
    /// <see langword="true"/> explicitly so that only an affirmative value defers to the page.
    /// </remarks>
    // MIGRATION: 02.02.00 line 45 adds this column as `InheritViewPermissions bit NULL` and no
    // later script makes it NOT NULL, so nullable is the terminal shape. ModuleInfo.vb declared it
    // a plain VB Boolean (line 347) backed by a Boolean field (line 70), which cannot represent
    // the SQL null at all: every unwritten row arrived as False, silently merging "never set" with
    // "set to false". Widening it to bool? is a deliberate, documented type correction rather than
    // a transliteration, and it must not be collapsed back to bool.
    public bool? InheritViewPermissions { get; set; }

    /// <summary>
    /// Gets or sets the <c>Header</c> column: <c>ntext NULL</c>, raw markup emitted above the
    /// module's content.
    /// </summary>
    /// <remarks>
    /// Untrusted markup that is stored and returned verbatim. It is neither sanitised nor encoded
    /// here; that is the responsibility of whatever renders it.
    /// </remarks>
    // MIGRATION: 02.02.00 line 322 declares this `ntext NULL`. ntext is the terminal type and the
    // schema is immutable, so Infrastructure must preserve it rather than opportunistically
    // converting the column to nvarchar(max). No maximum length may be declared for it.
    public string? Header { get; set; }

    /// <summary>
    /// Gets or sets the <c>Footer</c> column: <c>ntext NULL</c>, raw markup emitted below the
    /// module's content.
    /// </summary>
    /// <remarks>
    /// Subject to exactly the same handling as <see cref="Header"/>: stored verbatim, declared
    /// <c>ntext</c>, and never length-constrained.
    /// </remarks>
    public string? Footer { get; set; }

    /// <summary>
    /// Gets or sets the <c>StartDate</c> column: <c>datetime NULL</c>, the instant from which the
    /// instance becomes visible.
    /// </summary>
    /// <remarks>
    /// Null means "no start boundary". The legacy constructor seeded this field with
    /// <c>Null.NullDate</c>, which is <c>Date.MinValue</c>, so the earliest representable date was
    /// indistinguishable from an absent boundary; here it is an ordinary date.
    /// </remarks>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// Gets or sets the <c>EndDate</c> column: <c>datetime NULL</c>, the instant after which the
    /// instance stops being visible.
    /// </summary>
    /// <remarks>
    /// Null means "no end boundary", on the same reasoning as <see cref="StartDate"/>.
    /// </remarks>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Gets or sets the <c>PortalID</c> column: <c>int NULL</c>, the optional foreign key to the
    /// portal that owns the instance.
    /// </summary>
    /// <remarks>
    /// Null is meaningful and expected: a module with no portal is owned by the installation
    /// itself. Zero and -1 are both real portal keys, because <c>dbo.Portals.PortalID</c> is
    /// <c>IDENTITY(-1, 1)</c>, so neither may be read as an absence.
    /// </remarks>
    // MIGRATION: 03.00.01 line 12 adds this column as `PortalID int NULL` and the foreign key
    // added at lines 208-215 declares no ON DELETE clause, so it stays nullable to the end of the
    // chain. Do not infer non-nullability from an intermediate or recreated form of the schema,
    // and do not model it as a non-nullable int carrying Null.NullInteger (-1) for "no portal":
    // -1 is a genuine portal key in this schema, so the sentinel and a real owner would collide.
    public int? PortalId { get; set; }

    /// <summary>
    /// Gets or sets the definition this instance was created from.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>ModuleDefID</c> is <c>NOT NULL</c>: every module
    /// has a definition. It is nonetheless left <see langword="null"/> by any query that does not
    /// load it, which is why reading it defensively is still correct.
    /// </remarks>
    public ModuleDefinition ModuleDefinition { get; set; }

    /// <summary>
    /// Gets or sets the portal that owns the instance, or <see langword="null"/> for a module the
    /// installation owns.
    /// </summary>
    public Portal? Portal { get; set; }

    /// <summary>
    /// Gets the settings recorded against the instance itself, from <c>dbo.ModuleSettings</c>.
    /// </summary>
    /// <remarks>
    /// Replaces the untyped <c>Hashtable</c> the legacy model exposed. Settings scoped to a single
    /// placement are a different store and hang off <see cref="TabModule"/> instead.
    /// </remarks>
    public ICollection<ModuleSetting> Settings { get; } = new List<ModuleSetting>();

    /// <summary>
    /// Gets the placements of this instance on pages, from <c>dbo.TabModules</c>.
    /// </summary>
    /// <remarks>
    /// A unique index over <c>(TabID, ModuleID)</c> means the instance appears at most once on any
    /// given page, but it may appear on many.
    /// </remarks>
    public ICollection<TabModule> TabModules { get; } = new List<TabModule>();

    /// <summary>
    /// Gets the permission grants attached to this instance, from <c>dbo.ModulePermission</c>.
    /// </summary>
    /// <remarks>
    /// These rows replace the legacy role-name strings entirely. Grants are only consulted for view
    /// access when <see cref="InheritViewPermissions"/> is not <see langword="true"/>.
    /// </remarks>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();
}
