using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: the two intent flags belong here and nowhere else. The legacy screen declared chkDefault
// and chkAllModules alongside the module's own fields, and the legacy update path treated them as
// instructions rather than as module state: the first wrote two portal site-settings keys naming the
// module as the portal's default, and the second propagated the appearance settings of this placement
// across every non-administrative page. Neither is a column on dbo.Modules or dbo.TabModules, so they
// are carried on the request contract - the sibling detail contract explicitly excludes them - and the
// service performs the propagation as one unit of work.
//
// MIGRATION: PaneName and ModuleOrder are accepted here as well as on the create request, because the
// legacy screen allowed a placement to be moved between panes and reordered from the same form. They
// apply to the placement named by the route, not to every placement of an AllTabs module.
//
// MIGRATION: IsDeleted is not accepted. Recycling a module is a distinct operation with its own
// workflow, and folding it into a general update would let a routine edit delete a module as a side
// effect. ModuleDefId is not accepted either: re-pointing an existing instance at a different
// definition would orphan its stored content, which the legacy screen never permitted - txtFriendlyName
// was bound for display and the definition picker appeared only when adding a module.
//
// MIGRATION: TabId is not accepted. Moving a placement to a different page changes which placement row
// exists rather than editing this one, so it is a create-and-delete rather than an update, and
// accepting it here would silently make the addressed placement disappear.

/// <summary>
/// The editable state of one module placement, submitted to <c>PUT /api/v1/modules/{moduleId}</c>.
/// </summary>
/// <remarks>
/// A complete replacement of the editable subset rather than a patch: every member is applied as
/// supplied, mirroring the legacy screen, which posted its whole form. Declarative validation lives in
/// <c>Application/Validation/UpdateModuleRequestValidator.cs</c>.
/// </remarks>
public sealed class UpdateModuleRequest
{
    /// <summary>The instance's heading, or <see langword="null"/> to fall back to the definition's friendly name. At most 256 characters.</summary>
    public string? ModuleTitle { get; set; }

    /// <summary>Which pane of the page's layout the placement occupies. Required, at most 50 characters.</summary>
    public string PaneName { get; set; } = "ContentPane";

    /// <summary>The placement's position within its pane.</summary>
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Whether the module is placed on every page of the portal.
    /// </summary>
    /// <remarks>
    /// Clearing this on a module that was previously on every page removes the placements other than the
    /// one addressed; setting it creates the missing placements. Both are performed as one unit of work.
    /// </remarks>
    public bool AllTabs { get; set; }

    /// <summary>Whether the module takes its view permissions from its page instead of carrying its own.</summary>
    public bool InheritViewPermissions { get; set; } = true;

    /// <summary>
    /// The placement's horizontal alignment, or <see langword="null"/> to inherit. At most 10 characters.
    /// </summary>
    /// <remarks>
    /// MIGRATION: carried across as the opaque persisted string the legacy renderer consumed
    /// (<c>TabModules.Alignment nvarchar(10) NULL</c>), written by
    /// <c>Website/admin/Modules/ModuleSettings.ascx.vb:L345</c> from the cboAlign selection. The value is
    /// stored and returned verbatim and is never interpolated into a style declaration.
    /// </remarks>
    public string? Alignment { get; set; }

    /// <summary>
    /// The placement's background colour as the legacy renderer recorded it, or <see langword="null"/> to
    /// inherit. At most 20 characters.
    /// </summary>
    /// <remarks>
    /// MIGRATION: an opaque persisted data field, not a style input
    /// (<c>TabModules.Color nvarchar(20) NULL</c>), written by
    /// <c>Website/admin/Modules/ModuleSettings.ascx.vb:L346</c>. Stored and returned verbatim.
    /// </remarks>
    public string? Color { get; set; }

    /// <summary>
    /// The placement's border width flag as the legacy renderer recorded it, or <see langword="null"/> for
    /// none. At most 1 character.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the column is <c>TabModules.Border nvarchar(1) NULL</c> — a single character used as a
    /// flag by the legacy renderer — written by
    /// <c>Website/admin/Modules/ModuleSettings.ascx.vb:L347</c>. Stored and returned verbatim.
    /// </remarks>
    public string? Border { get; set; }

    /// <summary>Whether the placement renders expanded, collapsed or without its chrome. Must be a defined enumeration member.</summary>
    public ModuleVisibility Visibility { get; set; } = ModuleVisibility.Maximized;

    /// <summary>Whether the placement shows its heading.</summary>
    public bool DisplayTitle { get; set; } = true;

    /// <summary>Whether the placement offers a print affordance. The column defaults to set.</summary>
    /// <remarks>
    /// MIGRATION: written by <c>Website/admin/Modules/ModuleSettings.ascx.vb:L381</c> from
    /// chkDisplayPrint. The column is <c>TabModules.DisplayPrint bit NOT NULL DEFAULT (1)</c>, so this
    /// property defaults to <see langword="true"/> to match a legacy post that left the box ticked.
    /// </remarks>
    public bool DisplayPrint { get; set; } = true;

    /// <summary>Whether the placement offers a syndication affordance. The column defaults to set.</summary>
    /// <remarks>
    /// MIGRATION: written by <c>Website/admin/Modules/ModuleSettings.ascx.vb:L382</c> from
    /// chkDisplaySyndicate. The column is <c>TabModules.DisplaySyndicate bit NOT NULL DEFAULT (1)</c>.
    /// The affordance is reported although content syndication itself is deferred, so the stored value
    /// keeps its legacy meaning.
    /// </remarks>
    public bool DisplaySyndicate { get; set; } = true;

    /// <summary>How long the module's output may be cached, in seconds, or <see langword="null"/> to take the definition's default. Zero disables caching.</summary>
    public int? CacheTime { get; set; }

    /// <summary>The placement's icon, or <see langword="null"/> for none. At most 100 characters.</summary>
    public string? IconFile { get; set; }

    /// <summary>The instant the module becomes visible, or <see langword="null"/> for no start restriction.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>The instant the module stops being visible, or <see langword="null"/> for no end restriction. When both are supplied, the end must not precede the start.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Markup to render above the module's content, or <see langword="null"/> for none.</summary>
    public string? Header { get; set; }

    /// <summary>Markup to render below the module's content, or <see langword="null"/> for none.</summary>
    public string? Footer { get; set; }

    /// <summary>
    /// An instruction, not module state: name this module as the portal's default content module.
    /// </summary>
    /// <remarks>Writes the two portal-level keys the legacy handler wrote. Not persisted on the module itself and never echoed on a read contract.</remarks>
    public bool IsDefaultModule { get; set; }

    /// <summary>
    /// An instruction, not module state: copy this placement's appearance settings to every module on
    /// every non-administrative page of the portal.
    /// </summary>
    /// <remarks>
    /// Reproduces the legacy chkAllModules behaviour. Deliberately far-reaching, so the service applies
    /// it transactionally and records it on the audit log. Not persisted and never echoed on a read.
    /// </remarks>
    public bool AllModules { get; set; }
}
