using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The full state of one module instance, returned as the response body of <c>GET
/// /api/v1/modules/{moduleId}</c>. A boundary contract and nothing more: no navigation property, no tracked
/// state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// PERMISSIONS ARE NOT CARRIED HERE EITHER. No permission collection and no derived role string appears.
/// Permission evaluation belongs to the infrastructure security layer and the separate read-only
/// permissions catalogue. <see cref="InheritViewPermissions"/> is retained as the only permission-adjacent
/// member because it is a flag on the module row, not a permission entry.
/// </remarks>
public sealed class ModuleDetailDto
{
    // GROUP A - IDENTITY
    // Five non-nullable keys and one genuinely nullable one, drawn from all four tables.

    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c> (<c>int IDENTITY (0, 1) NOT
    /// NULL</c>, the table's primary key, 01.00.00 line 221).
    /// </summary>
    /// <remarks>
    /// THE SEED IS 0, so 0 is a legitimate module identifier held by the very first module ever created; a
    /// test of the form "identifier is less than or equal to zero" rejects a real row, and a test against
    /// -1 rejects nothing while implying a sentinel this contract never carries.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of THIS PLACEMENT of the module on a page, mapped from <c>TabModules.TabModuleID</c>
    /// (<c>int NOT NULL IDENTITY (1, 1)</c>, the placement table's primary key, 03.00.01 line 21).
    /// </summary>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy interface called
    /// this a page rather than a tab: its picker was captioned "Move To Page:".
    /// </summary>
    /// <remarks>
    /// The referenced <c>Tabs.TabID</c> is <c>int IDENTITY (0, 1) NOT NULL</c> (01.00.00 line 140), so 0 is
    /// a legitimate page identifier and must not be read as "no page".
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The portal that owns the module, mapped from <c>Modules.PortalID</c>, or <see langword="null"/> when
    /// the module is not portal-scoped - that is, a host-level module.
    /// </summary>
    // 5.10 - this member is nullable for a schema reason and a semantic reason, and the two must not be
    // conflated.
    public int? PortalId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c> (01.00.00
    /// line 66), so no boundary value on this member carries a second meaning.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The installed module package behind the definition, projected from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// ABSENT MEANS UNRESOLVED, AND ZERO IS NOT A VALUE THIS MEMBER CAN CARRY.
    /// <c>DesktopModules.DesktopModuleID</c> is a plain <c>IDENTITY</c>, so it seeds at one, and the
    /// terminal <c>ModuleDefinitions.DesktopModuleID</c> is declared <c>NOT NULL</c> with a foreign key
    /// onto it. A definition that resolves therefore always names a real package with a positive key.
    /// </remarks>
    public int? DesktopModuleId { get; set; }

    /// <summary>
    /// The administrator-supplied heading of this module instance, mapped from <c>Modules.ModuleTitle</c>
    /// (<c>nvarchar(256) NULL</c>, 01.00.00 line 226). Legacy caption "Title:", whose help text reads:
    /// "Enter a title for the Module.
    /// </summary>
    /// <remarks>
    /// The 256-character bound comes from the schema alone; the legacy input carried no length attribute.
    /// It is documented here and enforced by the validation layer and the database, never by an attribute
    /// on this type.
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Whether the module appears in the same location on every page of the portal, mapped from
    /// <c>Modules.AllTabs</c> (<c>bit</c> with a stored default of 0). Legacy caption "Display Module On
    /// All Pages?", whose help text reads: "Select whether the module should appear in the same location on
    /// all pages of the site".
    /// </summary>
    // MIGRATION: 5.8 - THIS FIELD WAS ADMINISTRATOR-ONLY ON THE LEGACY SCREEN, AND THAT RESTRICTION IS NOT
    // REPRODUCED AS A CONTRACT MEMBER. Website/admin/Modules/ModuleSettings.ascx.vb lines 215-219 disabled
    // four inputs for any caller not in the administrator role - the all-pages checkbox, the two behaviour
    // flags and the page picker - leaving the values visible but not editable.
    public bool AllTabs { get; set; }

    /// <summary>
    /// Free text or markup rendered above the module's content, mapped from <c>Modules.Header</c>. The
    /// legacy help text reads: "Enter the text or HTML that you would like to appear above the module
    /// content.".
    /// </summary>
    /// <remarks>
    /// The legacy input was a six-row multi-line text box with no length attribute, so the only bound is
    /// the column's own. Absence was represented as the EMPTY STRING rather than null, with the same mapper
    /// obligation described on <see cref="ModuleTitle"/>.
    /// </remarks>
    public string? Header { get; set; }

    /// <summary>
    /// Free text or markup rendered below the module's content, mapped from <c>Modules.Footer</c>, the
    /// counterpart of <see cref="Header"/>.
    /// </summary>
    public string? Footer { get; set; }

    /// <summary>
    /// The date from which the module begins to be displayed, mapped from <c>Modules.StartDate</c>, or <see
    /// langword="null"/> when no start date is set. The legacy help text reads: "Enter the start date for
    /// displaying this module.
    /// </summary>
    /// <remarks>
    /// Together with <see cref="EndDate"/> this pair GATES WHETHER THE MODULE RENDERS AT ALL, which is why
    /// the sentinel translation below is stated explicitly instead of being left to a default conversion:
    /// getting it wrong does not distort a displayed value, it makes content appear or vanish.
    /// </remarks>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which the module stops being displayed, mapped from <c>Modules.EndDate</c>, or <see
    /// langword="null"/> when no end date is set. The legacy help text reads: "Enter the end date for
    /// displaying this module.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Whether the module takes its VIEW permission from the page it sits on instead of carrying its own,
    /// mapped from <c>Modules.InheritViewPermissions</c>. The legacy caption, with its emphasis markup
    /// intact, read "Inherit &lt;b&gt;View&lt;/b&gt; permissions from &lt;b&gt;Page&lt;/b&gt;".
    /// </summary>
    /// <remarks>
    /// A FLAG AND NOTHING MORE. The permission entries themselves are deliberately absent from this
    /// contract - see the note on the type - so this member says how view permission is SOURCED, never what
    /// it grants or to whom. It is the only permission-adjacent member here, and it qualifies only because
    /// it is a column on the module row.
    /// </remarks>
    public bool InheritViewPermissions { get; set; }

    /// <summary>
    /// Whether the module is in the soft-deleted state that the legacy recycle bin represented, mapped from
    /// <c>Modules.IsDeleted</c> (<c>bit</c> with a stored default of 0). A row with this flag set still
    /// exists and can be restored; it is not a tombstone.
    /// </summary>
    // 5.6 - THE LEGACY SETTINGS SAVE COULD ONLY EVER CLEAR THIS FLAG, NEVER SET IT.
    // Website/admin/Modules/ModuleSettings.ascx.vb line 364 contains the bare, unconditional assignment
    // "objModule.IsDeleted = False" in the middle of its update handler, so saving a module's settings
    // always un-deleted it as a side effect, whatever its stored state and whether or not the operator
    // intended it.
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The placement's position within its pane on the page, mapped from <c>TabModules.ModuleOrder</c>
    /// (<c>int NOT NULL</c>, 03.00.01 line 25). Lower values render nearer the top of the pane.
    /// </summary>
    public int ModuleOrder { get; set; }

    /// <summary>
    /// How long this placement's rendered output may be cached, in seconds, mapped from
    /// <c>TabModules.CacheTime</c> (<c>int NOT NULL</c>, 03.00.01 line 26). Legacy caption "Cache Time
    /// (secs):".
    /// </summary>
    // MIGRATION: 5.7 - WHETHER THIS FIELD IS APPLICABLE AT ALL IS DEFINITION METADATA, AND IT IS NOT
    // CARRIED HERE. Website/admin/Modules/ModuleSettings.ascx.vb lines 138-142 hid the entire cache row
    // when the DEFINITION's DefaultCacheTime equalled the absent-integer constant: "If
    // objModuleDef.DefaultCacheTime = Null.NullInteger Then rowCache.Visible = False".
    public int CacheTime { get; set; }

    /// <summary>
    /// The icon displayed with the module's title for this placement, mapped from
    /// <c>TabModules.IconFile</c> (<c>nvarchar(100) NULL</c>, 03.00.01 line 29). The legacy help text
    /// reads: "Select an Icon for this Module to display in the Title Bar".
    /// </summary>
    /// <remarks>
    /// The 100-character bound is the column's. Absence was represented as the EMPTY STRING rather than
    /// null, with the same mapper obligation described on <see cref="ModuleTitle"/>.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// How this placement is aligned within its pane, mapped from <c>TabModules.Alignment</c>
    /// (<c>nvarchar(10) NULL</c>, 03.00.01 line 27), or <see langword="null"/> when no alignment is stored.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this member, together with <see cref="Color"/> and <see cref="Border"/>, was ABSENT from
    /// the first port of this contract, so the three columns the legacy screen edited had no route to a
    /// caller at all - the settings screen could store neither, and the values an upgraded installation
    /// already held were invisible. The legacy control was a horizontal radio list with the exact values
    /// <c>left</c>, <c>center</c>, <c>right</c> and the empty string for "Not Specified"
    /// (<c>modulesettings.ascx:L122-L127</c>), and those spellings are LOAD-BEARING DATA: they are what the
    /// column already contains and what the legacy container rendering compared against, so they are
    /// carried through unchanged rather than re-spelled or converted to an enumeration.
    /// </remarks>
    public string? Alignment { get; set; }

    /// <summary>
    /// The background colour of this placement's container, mapped from <c>TabModules.Color</c>
    /// (<c>nvarchar(20) NULL</c>, 03.00.01 line 28), or <see langword="null"/> when none is stored.
    /// </summary>
    /// <remarks>
    /// An OPAQUE legacy token carried through unparsed. The legacy box was a free-text field with no
    /// validator at all, and the column admits anything twenty characters or shorter - a hex triple, a CSS
    /// colour name, or something an installation invented - so narrowing it here would reject data that is
    /// already stored.
    /// </remarks>
    public string? Color { get; set; }

    /// <summary>
    /// The border width of this placement's container, mapped from <c>TabModules.Border</c>
    /// (<c>nvarchar(1) NULL</c>, 03.00.01 line 29), or <see langword="null"/> when none is stored.
    /// </summary>
    /// <remarks>
    /// ONE CHARACTER WIDE, AND A NUMBER. The legacy box carried <c>MaxLength="1"</c> and an integer
    /// data-type validator whose message read "Invalid Border (must be a number between 0 and 9)"
    /// (<c>modulesettings.ascx:L137-L138</c>), so the effective domain is a single digit. It travels as a
    /// string because the column is <c>nvarchar</c> and because the empty string - not a zero - is how the
    /// legacy screen expressed "no border stored".
    /// </remarks>
    public string? Border { get; set; }

    /// <summary>
    /// How this placement is presented on its page, mapped from <c>TabModules.Visibility</c> (<c>int NOT
    /// NULL</c>, 03.00.01 line 31). Legacy caption "Visibility:", whose help text reads: "Choose the
    /// default visibility for this Module".
    /// </summary>
    /// <remarks>
    /// A per-placement fact: the column is declared on the placement table and nowhere else in the upgrade
    /// chain. Not nullable, with <see cref="ModuleVisibility.Maximized"/> (ordinal 0) as the default, which
    /// matches both the column and the legacy object's own initial state.
    /// </remarks>
    // 5.3 (legacy defect) - annotated and deliberately NOT repaired.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether the module's CONTAINER chrome is displayed around this placement, mapped from
    /// <c>TabModules.DisplayTitle</c> (<c>bit NOT NULL</c> with a stored default of <c>(1)</c>, 03.00.08
    /// line 156). Despite the column name, the authoritative legacy wording is "Display Container?".
    /// </summary>
    /// <remarks>
    /// NOTE THE ASYMMETRY: <see langword="true"/> is the default of the COLUMN and of the legacy object,
    /// not of this contract. This member is a plain automatic property, so a default-constructed instance
    /// reports <see langword="false"/>.
    /// </remarks>
    // 5.9 - THE MEMBER NAME AND ITS MEANING DISAGREE, AND THE DISAGREEMENT IS INHERITED. The legacy markup
    // labels the checkbox "Display Title?", but that inline text is only a fallback:
    // Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx overrides it to "Display
    // Container?" with the help text "Select this option if you would like to display the Module
    // container."
    public bool DisplayTitle { get; set; }

    // GROUP D - CATALOGUE PROJECTIONS, READ-ONLY
    // Facts about the module's DEFINITION (dbo.ModuleDefinitions) and its installed PACKAGE
    // (dbo.DesktopModules), carried inline so a client can name and describe the module without a second
    // request.

    /// <summary>
    /// The definition's display name, projected read-only from <c>ModuleDefinitions.FriendlyName</c>
    /// (<c>nvarchar(128)</c>). Legacy caption "Module:", whose help text reads: "Displays the name of the
    /// module.".
    /// </summary>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The installed package's unique programmatic name, projected read-only from
    /// <c>DesktopModules.ModuleName</c>, which carries a <c>UNIQUE NONCLUSTERED</c> index
    /// (<c>IX_{objectQualifier}DesktopModules_ModuleName</c>).
    /// </summary>
    /// <remarks>
    /// LOAD-BEARING FOR TRANSFER, NOT MERELY DESCRIPTIVE. The legacy export composed its filename from a
    /// cleaned form of this name, and the legacy import validated a submitted filename against it, so the
    /// value participates in the export and import contracts rather than being cosmetic.
    /// </remarks>
    public string? ModuleName { get; set; }

    /// <summary>
    /// The human-readable description of the installed package, projected read-only from
    /// <c>DesktopModules.Description</c>.
    /// </summary>
    /// <remarks>
    /// Read-only catalogue metadata. Note carefully WHICH table this comes from: dbo.ModuleDefinitions once
    /// had a Description column of its own and it was DROPPED during the upgrade chain, so the surviving
    /// description belongs to the package and not to the definition.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// The installed package's version string, projected read-only from <c>DesktopModules.Version</c>.
    /// </summary>
    /// <remarks>
    /// Read-only catalogue metadata, and load-bearing for transfer in the same way as <see
    /// cref="ModuleName"/>: the legacy import passed a version string alongside the content and the
    /// operator identifier when handing content to a module's own portability implementation.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Whether this module was created from an ADMINISTRATION package, projected read-only from
    /// <c>DesktopModules.IsAdmin</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY A CLIENT NEEDS THIS: the generic settings surface refuses an administrative module outright with
    /// <c>module.settings_protected</c>, because such a module's settings are owned by the typed screen that
    /// administers them - the User Accounts package's settings are the portal's membership settings - and are
    /// not editable as ordinary module settings. Without this member a client cannot tell which modules those
    /// are, so it offers a settings affordance on every row and one of them always ends in a refusal. The
    /// administrative definitions are also absent from the portal-placeable definition catalogue, so their
    /// nature cannot be inferred from a second read either.
    /// </para>
    /// <para>
    /// NULL IS NOT FALSE. The underlying column is <c>bit NOT NULL</c>, so a resolved package always answers
    /// one or the other; null means the definition or its package could not be resolved and nothing is being
    /// claimed. A client must treat only an explicit <see langword="true"/> as administrative.
    /// </para>
    /// </remarks>
    public bool? IsAdmin { get; set; }
}
