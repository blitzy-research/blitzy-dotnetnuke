using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One placement of a module instance on one page: the row of <c>dbo.TabModules</c> that records where the
/// instance sits and how it is presented there.
/// </summary>
/// <remarks>
/// <para>
/// This is a plain persistence object and nothing more. It holds the fifteen scalars of the terminal
/// <c>dbo.TabModules</c> table, the two references its required foreign keys imply, and the
/// placement-scoped settings that hang off it.
/// </para>
/// <para>
/// Because the unique index <c>IX_TabModules</c> spans <c>(TabID, ModuleID)</c>, an instance appears at
/// most once on any given page while still appearing on as many pages as required. The <see cref="Tab"/>
/// and <see cref="Entities.Module"/> references are therefore both required, and both cascade on delete in
/// the database.
/// </para>
/// </remarks>
public sealed class TabModule : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of the placement: the <c>TabModuleID</c> column, <c>int NOT NULL
    /// IDENTITY (1, 1)</c>, and the clustered primary key <c>PK_TabModules</c>.
    /// </summary>
    /// <remarks>
    /// Settable because the store generates it and the persistence layer assigns it back after the insert.
    /// Unlike the identity columns of the portal, role, tab and module tables, this one seeds at 1, so zero
    /// is not a legitimate value here - but nothing in this type draws any conclusion from that, and
    /// nothing may be added that does.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabModuleId;

    /// <summary>
    /// Gets or sets the page the module is placed on: the <c>TabID</c> column, <c>int NOT NULL</c>, and the
    /// foreign key <c>FK_TabModules_Tabs</c> into <c>dbo.Tabs</c>.
    /// </summary>
    /// <remarks>
    /// Required, and cascading in the database: deleting a page deletes its placements. The <see
    /// cref="Tab"/> reference is the loaded form of this key, and the pair <c>(TabID, ModuleID)</c> is
    /// unique, so a second placement of the same instance on the same page is rejected by the store.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// Gets or sets the module instance being placed: the <c>ModuleID</c> column, <c>int NOT NULL</c>, and
    /// the foreign key <c>FK_TabModules_Modules</c> into <c>dbo.Modules</c>.
    /// </summary>
    /// <remarks>
    /// Required, and cascading in the database: deleting an instance deletes every placement of it. Zero is
    /// a legitimate value, because <c>dbo.Modules.ModuleID</c> is declared <c>IDENTITY(0, 1)</c> and so
    /// identifies a real first row.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// Gets or sets the skin pane that hosts the placement: the <c>PaneName</c> column, <c>nvarchar(50) NOT
    /// NULL</c>.
    /// </summary>
    public string PaneName { get; set; }

    /// <summary>
    /// Gets or sets the ordinal of the placement within its pane: the <c>ModuleOrder</c> column, <c>int NOT
    /// NULL</c>.
    /// </summary>
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Gets or sets the output cache window for the placement in minutes, zero meaning uncached: the
    /// <c>CacheTime</c> column, <c>int NOT NULL</c>.
    /// </summary>
    public int CacheTime { get; set; }

    /// <summary>
    /// Gets or sets the legacy horizontal alignment token for the placement: the <c>Alignment</c> column,
    /// <c>nvarchar(10) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is. <see langword="null"/> means the placement expresses no preference
    /// and the container decides; it does not mean the empty string, which the legacy initialiser used as
    /// its "no value" marker.
    /// </remarks>
    public string? Alignment { get; set; }

    /// <summary>
    /// Gets or sets the legacy background colour token for the placement: the <c>Color</c> column,
    /// <c>nvarchar(20) NULL</c>.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is, and left uninitialised so that "no colour chosen" stays distinct
    /// from "chosen as blank". The value is an opaque legacy token carried through unchanged rather than
    /// parsed into a colour type, because the store admits anything twenty characters or shorter and this
    /// migration does not narrow an existing contract.
    /// </remarks>
    public string? Color { get; set; }

    /// <summary>
    /// Gets or sets the legacy border width token for the placement: the <c>Border</c> column,
    /// <c>nvarchar(1) NULL</c>.
    /// </summary>
    /// <remarks>
    /// A single character, which the legacy renderer read as a border width. The width of one is declared
    /// verbatim in the Infrastructure mapping rather than rounded up, so an over-long value is rejected
    /// there instead of being truncated by the provider.
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
    /// Gets or sets the initial expand or collapse state of the placement: the <c>Visibility</c> column,
    /// <c>int NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// This is the only persisted visibility state in the module aggregate, and it is a fact about the
    /// placement rather than about the instance: the same module may open expanded on one page and
    /// collapsed on another. <c>dbo.Modules</c> has no <c>Visibility</c> column and consequently <see
    /// cref="Entities.Module"/> has no such property.
    /// </remarks>
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Gets or sets the container skin applied to the placement: the <c>ContainerSrc</c> column,
    /// <c>nvarchar(200) NULL</c>.
    /// </summary>
    public string? ContainerSrc { get; set; }

    /// <summary>
    /// Gets or sets whether the placement renders the module's title: the <c>DisplayTitle</c> column,
    /// <c>bit NOT NULL</c> with a store default of <c>1</c>.
    /// </summary>
    public bool DisplayTitle { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the placement offers a print affordance: the <c>DisplayPrint</c> column, <c>bit
    /// NOT NULL</c> with a store default of <c>1</c>.
    /// </summary>
    public bool DisplayPrint { get; set; } = true;

    // MIGRATION: DELIBERATE, DOCUMENTED DIVERGENCE - do not "tidy" this initialiser to true and do not
    // remove the store default from the Infrastructure mapping.

    /// <summary>
    /// Gets or sets whether the placement offers a syndication affordance: the <c>DisplaySyndicate</c>
    /// column, <c>bit NOT NULL</c> with a store default of <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Both behaviours are preserved on the side that owns them, so neither is silently lost: this entity
    /// reproduces what the legacy code did when it constructed a placement, and the Infrastructure mapping
    /// leaves the column default alone so the database keeps doing what it did for any writer that omits
    /// the column.
    /// </remarks>
    public bool DisplaySyndicate { get; set; } = false;

    /// <summary>Gets or sets the page that hosts this placement, the loaded form of <see cref="TabId"/>.</summary>
    /// <remarks>
    /// Required rather than optional, because <c>TabID</c> is <c>NOT NULL</c>: every placement is on a
    /// page. It is nonetheless left <see langword="null"/> by any query that does not load it, so reading
    /// it defensively remains correct.
    /// </remarks>
    public Tab Tab { get; set; }

    /// <summary>Gets or sets the module instance being placed, the loaded form of <see cref="ModuleId"/>.</summary>
    /// <remarks>
    /// Required rather than optional, because <c>ModuleID</c> is <c>NOT NULL</c>: a placement without an
    /// instance is meaningless. As with <see cref="Tab"/>, a query that does not load it leaves it <see
    /// langword="null"/>.
    /// </remarks>
    public Module Module { get; set; }

    /// <summary>
    /// Gets the name/value settings recorded against this placement alone, from
    /// <c>dbo.TabModuleSettings</c>.
    /// </summary>
    /// <remarks>
    /// Initialised so that a freshly constructed placement has an empty, usable collection rather than <see
    /// langword="null"/>, and get-only so that the collection instance cannot be swapped out from under the
    /// change tracker. It replaces the untyped <c>Hashtable</c> the legacy model exposed, per Rule T8.
    /// </remarks>
    public ICollection<TabModuleSetting> Settings { get; } = new List<TabModuleSetting>();
}
