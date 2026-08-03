using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

// MIGRATION: this type is the placement half of the legacy flattened class
//   Library/Components/Modules/ModuleInfo.vb. That single class, declared at line 36 and running to
//   line 936, exposed 58 properties drawn from a four-way join across dbo.Modules, dbo.TabModules,
//   dbo.ModuleDefinitions and dbo.ModuleControls, so a caller holding one instance could not tell
//   which of the four rows any given property came from. Rule T8 splits it along the real table
//   boundaries: this file owns dbo.TabModules and nothing else. Module, ModuleDefinition and
//   ModuleControl own their own tables.
//
// MIGRATION: the legacy decoration is deliberately not carried forward. ModuleInfo.vb declares
//   <XmlRoot("module", IsNullable:=False)> on the type (line 36) and an <XmlElement("...")> on every
//   property, and it declares Implements IPropertyAccess (line 37) to serve the token-replacement
//   subsystem the migration excludes. In the target the wire contract belongs to the DTOs at the API
//   boundary, so this entity carries no attribute of any kind and implements no presentation or
//   token-access interface.
//
// MIGRATION: the placement columns were not always here. The 03.00.01 upgrade script created
//   dbo.TabModules at lines 19-33 and moved PaneName, ModuleOrder, CacheTime, Alignment, Color,
//   Border, IconFile, Visibility and ContainerSrc off dbo.Modules, which is the moment a module
//   instance stopped belonging to exactly one page. Every property below is a fact about the
//   placement, never about the module: the same instance may sit on many pages with a different
//   pane, order, cache window, alignment, colour, border, icon, visibility state and container on
//   each one.
//
// MIGRATION: Visibility belongs here and only here. dbo.Modules has no Visibility column - the
//   88-script upgrade chain never adds one - so no such property exists on Module, and none may be
//   added. The legacy enum DotNetNuke.Entities.Modules.VisibilityState (ModuleInfo.vb lines 30-34)
//   is superseded by DnnMigration.Domain.Enums.ModuleVisibility, whose three ordinals are the values
//   actually stored in the TabModules.Visibility int column. Infrastructure maps the enum onto that
//   existing integer column by its explicit ordinal values 0, 1 and 2; no conversion table, no
//   string storage and no renumbering.
//
// MIGRATION: the legacy object defaults and the store defaults for the three display flags do not
//   agree, and the disagreement is preserved rather than resolved. See the DisplaySyndicate member
//   below and the corresponding entry in the repository-root MIGRATION_NOTES.md.
//
// MIGRATION: obligations this entity places on the Infrastructure layer, which owns all mapping
//   under Rules T3 and T4 - the schema itself is immutable and nothing here may alter it:
//     * bind to dbo.TabModules and to the legacy column names, TabModuleID included.
//     * TabModuleID is IDENTITY (1, 1) and the clustered primary key PK_TabModules
//       (03.00.01 lines 21 and 36-40), so it is store-generated on insert.
//     * keep both foreign keys required and keep their existing delete behaviour:
//       FK_TabModules_Tabs on TabID references dbo.Tabs and FK_TabModules_Modules on ModuleID
//       references dbo.Modules, each declared ON DELETE CASCADE NOT FOR REPLICATION
//       (03.00.01 lines 44-54 and 56-66). Deleting a page or a module instance therefore removes
//       its placements in the database, and that behaviour must not be softened to a no-action or
//       client-side rule.
//     * keep the unique index IX_TabModules over (TabID, ModuleID) added by 03.00.03 lines 11-17,
//       which is what limits an instance to at most one placement per page.
//     * declare the nvarchar widths recorded on each member below, so an over-long value is
//       rejected rather than silently truncated by the provider.
//     * retain the store defaults of 1 on the three display flags; they are the database's answer
//       for a row inserted without them and are not this entity's business.

/// <summary>
/// One placement of a module instance on one page: the row of <c>dbo.TabModules</c> that records
/// where the instance sits and how it is presented there.
/// </summary>
/// <remarks>
/// <para>
/// This is a plain persistence object and nothing more. It holds the fifteen scalars of the terminal
/// <c>dbo.TabModules</c> table, the two references its required foreign keys imply, and the
/// placement-scoped settings that hang off it. It performs no I/O, reaches no repository, context or
/// cache, carries no attribute, and exposes no behaviour beyond the identity-based equality it
/// inherits from <see cref="Entity{TId}"/>. Mapping to the legacy table and column names belongs to
/// the Infrastructure layer, and the schema it maps onto is immutable for this migration.
/// </para>
/// <para>
/// The distinction between this type and <see cref="Entities.Module"/> is the whole point of the
/// split. A module instance - what it is, which definition it realises, which portal owns it, when
/// it is live - is one row in <c>dbo.Modules</c>. Where that instance appears and how it looks there
/// is a row per page in <c>dbo.TabModules</c>. The legacy model flattened the two into a single
/// class, so changing an alignment looked identical to changing the instance itself; here the two
/// concerns cannot be confused because they are different objects backed by different tables.
/// </para>
/// <para>
/// Because the unique index <c>IX_TabModules</c> spans <c>(TabID, ModuleID)</c>, an instance appears
/// at most once on any given page while still appearing on as many pages as required. The
/// <see cref="Tab"/> and <see cref="Entities.Module"/> references are therefore both required, and
/// both cascade on delete in the database.
/// </para>
/// <para>
/// Absence is expressed with nullable CLR types. The five optional presentation columns are
/// <c>NULL</c>-able in the store and nullable here, and none of them is seeded with the legacy empty
/// string: <c>Null.NullString</c> in <c>Library/Components/Shared/Null.vb</c> is the empty string
/// rather than a null, so planting <see cref="string.Empty"/> in any of them would carry that
/// sentinel into the domain and make "not supplied" indistinguishable from "supplied as blank".
/// Sentinel semantics survive only at the DTO and API boundary, where the wire contract is
/// externally observable.
/// </para>
/// </remarks>
public sealed class TabModule : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of the placement: the <c>TabModuleID</c> column,
    /// <c>int NOT NULL IDENTITY (1, 1)</c>, and the clustered primary key <c>PK_TabModules</c>.
    /// </summary>
    /// <remarks>
    /// Settable because the store generates it and the persistence layer assigns it back after the
    /// insert. Unlike the identity columns of the portal, role, tab and module tables, this one seeds
    /// at 1, so zero is not a legitimate value here - but nothing in this type draws any conclusion
    /// from that, and nothing may be added that does. Whether a placement has been written is
    /// declared by the persistence layer through <see cref="Entity{TId}.MarkIdentityPersisted"/>,
    /// never inferred from this value.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabModuleId;

    /// <summary>
    /// Gets or sets the page the module is placed on: the <c>TabID</c> column, <c>int NOT NULL</c>,
    /// and the foreign key <c>FK_TabModules_Tabs</c> into <c>dbo.Tabs</c>.
    /// </summary>
    /// <remarks>
    /// Required, and cascading in the database: deleting a page deletes its placements. The
    /// <see cref="Tab"/> reference is the loaded form of this key, and the pair
    /// <c>(TabID, ModuleID)</c> is unique, so a second placement of the same instance on the same
    /// page is rejected by the store.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// Gets or sets the module instance being placed: the <c>ModuleID</c> column,
    /// <c>int NOT NULL</c>, and the foreign key <c>FK_TabModules_Modules</c> into
    /// <c>dbo.Modules</c>.
    /// </summary>
    /// <remarks>
    /// Required, and cascading in the database: deleting an instance deletes every placement of it.
    /// Zero is a legitimate value, because <c>dbo.Modules.ModuleID</c> is declared
    /// <c>IDENTITY(0, 1)</c> and so identifies a real first row.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// Gets or sets the skin pane that hosts the placement: the <c>PaneName</c> column,
    /// <c>nvarchar(50) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// Non-nullable because the column is, and carrying no initialiser on purpose. The legacy
    /// constructor at <c>Library/Components/Modules/ModuleInfo.vb</c> lines 102-125 leaves the
    /// backing field unset, and the separate initialiser at line 728 assigns the empty string - which
    /// is exactly <c>Null.NullString</c>, the legacy marker for "no value". Seeding this member with
    /// <see cref="string.Empty"/> would plant that sentinel in the domain and make an unsupplied pane
    /// indistinguishable from a pane named with zero characters, so the Application layer supplies a
    /// real pane name instead.
    /// </remarks>
    public string PaneName { get; set; }

    /// <summary>
    /// Gets or sets the ordinal of the placement within its pane: the <c>ModuleOrder</c> column,
    /// <c>int NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// Orders the placement against the others sharing <see cref="PaneName"/> on the same page. It is
    /// scoped to the pane, not to the page, so two placements in different panes may hold the same
    /// value without ambiguity.
    /// </remarks>
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Gets or sets the output cache window for the placement in minutes, zero meaning uncached: the
    /// <c>CacheTime</c> column, <c>int NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// A property of the placement rather than of the instance, so the same module may be cached
    /// aggressively on one page and not at all on another. The definition's own default cache time
    /// lives on the definition and is applied by the Application layer when a caller supplies
    /// nothing.
    /// </remarks>
    public int CacheTime { get; set; }

    /// <summary>
    /// Gets or sets the legacy horizontal alignment token for the placement: the <c>Alignment</c>
    /// column, <c>nvarchar(10) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is. <see langword="null"/> means the placement expresses no
    /// preference and the container decides; it does not mean the empty string, which the legacy
    /// initialiser used as its "no value" marker.
    /// </remarks>
    public string? Alignment { get; set; }

    /// <summary>
    /// Gets or sets the legacy background colour token for the placement: the <c>Color</c> column,
    /// <c>nvarchar(20) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is, and left uninitialised so that "no colour chosen" stays
    /// distinct from "chosen as blank". The value is an opaque legacy token carried through unchanged
    /// rather than parsed into a colour type, because the store admits anything twenty characters or
    /// shorter and this migration does not narrow an existing contract.
    /// </remarks>
    public string? Color { get; set; }

    /// <summary>
    /// Gets or sets the legacy border width token for the placement: the <c>Border</c> column,
    /// <c>nvarchar(1) NULL</c>.
    /// </summary>
    /// <remarks>
    /// A single character, which the legacy renderer read as a border width. The width of one is
    /// declared verbatim in the Infrastructure mapping rather than rounded up, so an over-long value
    /// is rejected there instead of being truncated by the provider. Nullable because the column is.
    /// </remarks>
    public string? Border { get; set; }

    /// <summary>
    /// Gets or sets the icon rendered in the placement's header: the <c>IconFile</c> column,
    /// <c>nvarchar(100) NULL</c>.
    /// </summary>
    /// <remarks>
    /// A portal-relative path held as text, nullable because the column is. It names an icon for this
    /// placement alone; the icon of the underlying definition is a separate, unrelated column on a
    /// different table.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets the initial expand or collapse state of the placement: the <c>Visibility</c>
    /// column, <c>int NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only persisted visibility state in the module aggregate, and it is a fact about
    /// the placement rather than about the instance: the same module may open expanded on one page and
    /// collapsed on another. <c>dbo.Modules</c> has no <c>Visibility</c> column and consequently
    /// <see cref="Entities.Module"/> has no such property.
    /// </para>
    /// <para>
    /// Typed as <see cref="ModuleVisibility"/>, whose ordinals are the values actually stored -
    /// <see cref="ModuleVisibility.Maximized"/> as 0, <see cref="ModuleVisibility.Minimized"/> as 1
    /// and <see cref="ModuleVisibility.None"/> as 2 - so Infrastructure maps it straight onto the
    /// existing integer column. Non-nullable because the column is, and left uninitialised because
    /// the default of the enumeration is already <see cref="ModuleVisibility.Maximized"/>, which is
    /// exactly the value the legacy initialiser assigned at
    /// <c>Library/Components/Modules/ModuleInfo.vb</c> line 738. No explicit initialiser is written,
    /// since one would restate the language default and could drift from it.
    /// </para>
    /// </remarks>
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Gets or sets the container skin applied to the placement: the <c>ContainerSrc</c> column,
    /// <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The container is the chrome drawn around the module - its title bar, borders and action
    /// affordances. Nullable because the column is: <see langword="null"/> means the placement
    /// inherits the container the page or portal specifies, which is why it must not be seeded with
    /// the empty string the legacy initialiser used at
    /// <c>Library/Components/Modules/ModuleInfo.vb</c> line 748.
    /// </remarks>
    public string? ContainerSrc { get; set; }

    /// <summary>
    /// Gets or sets whether the placement renders the module's title: the <c>DisplayTitle</c> column,
    /// <c>bit NOT NULL</c> with a store default of <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Initialised to <see langword="true"/>, which is a real legacy default rather than a null
    /// marker: the constructor at <c>Library/Components/Modules/ModuleInfo.vb</c> line 122 and the
    /// separate initialiser at line 744 both assign <c>True</c>. The store agrees - the column was
    /// added with <c>DEFAULT (1)</c> by <c>03.00.08</c> line 156 and that default was re-asserted by
    /// <c>03.01.01</c> line 1219 - so object construction and the database say the same thing here.
    /// The initialiser is written out anyway, because relying on the CLR default of
    /// <see langword="false"/> would silently hide a title that both the legacy code and the schema
    /// show.
    /// </remarks>
    public bool DisplayTitle { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the placement offers a print affordance: the <c>DisplayPrint</c> column,
    /// <c>bit NOT NULL</c> with a store default of <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Initialised to <see langword="true"/> for the same measured reason as
    /// <see cref="DisplayTitle"/>: the legacy constructor assigns <c>True</c> at
    /// <c>Library/Components/Modules/ModuleInfo.vb</c> line 123 and the initialiser repeats it at
    /// line 745, while the column carries <c>DEFAULT (1)</c> from <c>03.00.08</c> line 157, re-asserted
    /// by <c>03.01.01</c> line 1221. Construction and store agree.
    /// </remarks>
    public bool DisplayPrint { get; set; } = true;

    // MIGRATION: DELIBERATE, DOCUMENTED DIVERGENCE - do not "tidy" this initialiser to true and do
    //   not remove the store default from the Infrastructure mapping. The legacy object default and
    //   the database default genuinely disagree, and both are preserved on the side that owns them:
    //     * object construction says FALSE. Library/Components/Modules/ModuleInfo.vb sets
    //       _DisplaySyndicate = False in the constructor at line 124, and the separate
    //       Initialize(PortalId) routine independently sets it to False again at line 746. Two
    //       independent witnesses, which is what rules out the reading that False is merely
    //       Null.NullBoolean leaking through: DisplayTitle and DisplayPrint sit either side of it in
    //       both routines and are True in both, so these three assignments are deliberate business
    //       defaults rather than null markers.
    //     * the store says 1, that is TRUE. 03.00.08 line 158 adds the column as
    //       "DisplaySyndicate bit NOT NULL CONSTRAINT DF_{objectQualifier}TabModules_DisplaySyndicate
    //       DEFAULT (1)", and 03.01.01 drops that constraint at lines 1206-1213, re-declares the
    //       column NOT NULL at line 1217 and re-adds DEFAULT (1) at line 1223. The default therefore
    //       survives into the terminal schema, twice asserted.
    //   Consequence, and the reason the mismatch is kept rather than settled: a placement created
    //   through this model does not syndicate, matching every placement the legacy application
    //   created; a row inserted by anything that omits the column - a stored procedure, a script, a
    //   hand-written statement - does syndicate, matching the legacy database. Choosing either value
    //   for both sides would change observable behaviour on one of them, which Rule T5 forbids.
    //   Recorded in the repository-root MIGRATION_NOTES.md.

    /// <summary>
    /// Gets or sets whether the placement offers a syndication affordance: the
    /// <c>DisplaySyndicate</c> column, <c>bit NOT NULL</c> with a store default of <c>1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initialised to <see langword="false"/>, which is the legacy object default and deliberately
    /// <b>not</b> the store default. This is the one member of the three display flags where the two
    /// disagree: <c>Library/Components/Modules/ModuleInfo.vb</c> assigns <c>False</c> at line 124 and
    /// again at line 746, while the column carries <c>DEFAULT (1)</c> from <c>03.00.08</c> line 158,
    /// re-asserted by <c>03.01.01</c> line 1223.
    /// </para>
    /// <para>
    /// Both behaviours are preserved on the side that owns them, so neither is silently lost: this
    /// entity reproduces what the legacy code did when it constructed a placement, and the
    /// Infrastructure mapping leaves the column default alone so the database keeps doing what it did
    /// for any writer that omits the column. The divergence is recorded in the repository-root
    /// <c>MIGRATION_NOTES.md</c>; it must not be collapsed to a single value in either direction.
    /// </para>
    /// </remarks>
    public bool DisplaySyndicate { get; set; } = false;

    /// <summary>
    /// Gets or sets the page that hosts this placement, the loaded form of <see cref="TabId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>TabID</c> is <c>NOT NULL</c>: every placement is on
    /// a page. It is nonetheless left <see langword="null"/> by any query that does not load it, so
    /// reading it defensively remains correct. The inverse is
    /// <see cref="Entities.Tab.TabModules"/>.
    /// </remarks>
    public Tab Tab { get; set; }

    /// <summary>
    /// Gets or sets the module instance being placed, the loaded form of <see cref="ModuleId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>ModuleID</c> is <c>NOT NULL</c>: a placement without
    /// an instance is meaningless. As with <see cref="Tab"/>, a query that does not load it leaves it
    /// <see langword="null"/>. The inverse is <see cref="Entities.Module.TabModules"/>, and this is
    /// the reference across which the placement's own presentation state is kept separate from the
    /// instance's identity and lifetime.
    /// </remarks>
    public Module Module { get; set; }

    /// <summary>
    /// Gets the name/value settings recorded against this placement alone, from
    /// <c>dbo.TabModuleSettings</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initialised so that a freshly constructed placement has an empty, usable collection rather
    /// than <see langword="null"/>, and get-only so that the collection instance cannot be swapped
    /// out from under the change tracker. It replaces the untyped <c>Hashtable</c> the legacy model
    /// exposed, per Rule T8.
    /// </para>
    /// <para>
    /// These settings are scoped to the placement and are a different store from the instance-scoped
    /// settings on <see cref="Entities.Module.Settings"/>: <c>dbo.TabModuleSettings</c>, keyed
    /// <c>(TabModuleID, SettingName)</c>, against <c>dbo.ModuleSettings</c>, keyed
    /// <c>(ModuleID, SettingName)</c>. The two are not interchangeable, and a setting recorded here
    /// applies to this placement only - the same instance placed on another page does not see it.
    /// </para>
    /// </remarks>
    public ICollection<TabModuleSetting> Settings { get; } = new List<TabModuleSetting>();
}
