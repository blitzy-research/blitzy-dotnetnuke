using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: the field set is measured from Website/admin/Modules/modulesettings.ascx, whose declared
// controls are cboTab, txtTitle, txtFriendlyName, chkAllTabs, chkAllModules, chkDefault,
// chkInheritPermissions, txtStartDate, txtEndDate, txtHeader, txtFooter, txtCacheTime, cboVisibility,
// chkDisplayTitle, chkDisplayPrint, chkDisplaySyndicate, cboAlign, txtColor, txtBorder, ctlIcon and
// ctlModuleContainer. The subset here is the one that survives the migration's exclusions, and each
// omission is accounted for in the sibling detail contract rather than repeated.
//
// MIGRATION: creating a module creates rows in TWO tables, and this request carries both halves. The
// module row goes to dbo.Modules and the placement row to dbo.TabModules; the legacy screen posted a
// single form for both, and the two writes are committed as one unit of work so a module can never
// exist without the placement that was requested with it.
//
// MIGRATION: the legacy form declared NO required-field validator at all, not even on the title -
// measured: zero RequiredFieldValidator declarations in modulesettings.ascx. A blank title is
// therefore legitimate and the definition's friendly name is used as the heading, which is exactly
// what the legacy code-behind did. The only validators on that form were DataTypeCheck comparisons:
// valtxtStartDate and valtxtEndDate for dates, valCacheTime for an integer cache time, and valBorder
// for an integer border. In a strongly typed contract the date and integer checks are satisfied by the
// member types themselves, so the matching validator asserts what the types cannot: schema lengths,
// enumeration membership and the NOT NULL placement key.
//
// MIGRATION: PaneName is required here even though the sibling list row omits it. TabModules.PaneName
// is nvarchar(50) NOT NULL, so a placement cannot be written without one; the legacy screen supplied
// it implicitly from the skin's pane picker.

/// <summary>
/// The state submitted to <c>POST /api/v1/modules</c> to add a module to a page.
/// </summary>
/// <remarks>
/// Declarative validation lives in <c>Application/Validation/CreateModuleRequestValidator.cs</c> rather
/// than as attributes on this type. The portal is taken from the per-request portal context rather than
/// from the body, so a caller cannot create a module in a portal it did not address.
/// </remarks>
public sealed class CreateModuleRequest
{
    /// <summary>
    /// The module definition to instantiate. Required.
    /// </summary>
    /// <remarks><c>ModuleDefinitions.ModuleDefID</c> is <c>IDENTITY (1, 1)</c>, so a value of zero or less cannot name a real definition.</remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The page to place the module on. Required.
    /// </summary>
    /// <remarks><c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>, so 0 is a legitimate page and only a negative value is rejected.</remarks>
    public int TabId { get; set; }

    /// <summary>The instance's heading, or <see langword="null"/> to fall back to the definition's friendly name. At most 256 characters.</summary>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Which pane of the page's layout to place the module in. Required, at most 50 characters.
    /// </summary>
    /// <remarks>Defaults to the conventional content pane, which is the pane every DotNetNuke skin declares.</remarks>
    public string PaneName { get; set; } = "ContentPane";

    /// <summary>The placement's position within its pane. Zero places the module first.</summary>
    public int ModuleOrder { get; set; }

    /// <summary>Whether to place the module on every page of the portal, which produces one placement row per page.</summary>
    public bool AllTabs { get; set; }

    /// <summary>Whether the module takes its view permissions from its page instead of carrying its own. Defaults to <see langword="true"/>, matching the legacy form's default.</summary>
    public bool InheritViewPermissions { get; set; } = true;

    /// <summary>Whether the placement renders expanded, collapsed or without its chrome. Must be a defined enumeration member.</summary>
    public ModuleVisibility Visibility { get; set; } = ModuleVisibility.Maximized;

    /// <summary>Whether the placement shows its heading. Defaults to <see langword="true"/>, matching the column default of 1.</summary>
    public bool DisplayTitle { get; set; } = true;

    /// <summary>How long the module's output may be cached, in seconds, or <see langword="null"/> to take the definition's default cache time. Zero disables caching.</summary>
    public int? CacheTime { get; set; }

    /// <summary>The placement's icon, or <see langword="null"/> for none. At most 100 characters.</summary>
    public string? IconFile { get; set; }

    /// <summary>The instant the module becomes visible, or <see langword="null"/> for no start restriction.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>The instant the module stops being visible, or <see langword="null"/> for no end restriction. When both are supplied, the end must not precede the start.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Markup to render above the module's content, or <see langword="null"/> for none. Stored in an <c>ntext</c> column, so no length limit applies.</summary>
    public string? Header { get; set; }

    /// <summary>Markup to render below the module's content, or <see langword="null"/> for none. Stored in an <c>ntext</c> column, so no length limit applies.</summary>
    public string? Footer { get; set; }
}
