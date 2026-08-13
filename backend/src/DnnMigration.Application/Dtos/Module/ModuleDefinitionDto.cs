namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: §5.8 - XML serialisation attributes and IPropertyAccess are dropped.

/// <summary>
/// The read-only lookup contract returned by <c>GET /api/v1/module-definitions</c>, describing one
/// installable module definition that an administrator may choose when placing a module on a page.
/// </summary>
/// <remarks>
/// <para>
/// Each instance is a projection of the terminal <c>dbo.ModuleDefinitions</c> table joined to its owning
/// <c>dbo.DesktopModules</c> row: the first four members are the whole of <c>ModuleDefinitions</c>, and the
/// remaining six are the identity and capability facts from <c>DesktopModules</c> that the administration
/// screens actually need.
/// </para>
/// <para>
/// Deliberate exclusions, each annotated inline below with its measured evidence: the transient
/// <c>TempModuleID</c> field; every module-control member, because those describe Web Forms control
/// loading; the module folder path; the two capability flags this migration does not need; the raw
/// capability bit field; the business controller type name; and the installer manifest metadata.
/// </para>
/// </remarks>
public sealed class ModuleDefinitionDto
{
    /// <summary>The identity of this module definition, mapped from <c>ModuleDefinitions.ModuleDefID</c>.</summary>
    /// <remarks>
    /// The column is <c>int IDENTITY (1, 1) NOT NULL</c> and is the table's primary key (01.00.00 baseline,
    /// line 66). The seed is <c>1</c>, so no sentinel and no boundary value carries a second meaning on
    /// this member.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The display name of this definition - the value the legacy settings screen showed for
    /// <c>Module:</c>, described there as "Displays the name of the module.".
    /// </summary>
    // Initialised rather than left to the global CS8618 suppression. That suppression exists for
    // ORM-materialised entities, which the database populates and whose required columns reject a null
    // loudly if it does not.
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// The identifier of the desktop module that owns this definition, mapped from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// Added by 02.00.00 line 5173 as <c>int NOT NULL</c> with a default of <c>0</c>, and made a foreign
    /// key to <c>DesktopModules</c> at line 5258. Because the column is not nullable and defaults to zero,
    /// <b>zero is a legitimate stored value</b> and must never be interpreted as "unset".
    /// </remarks>
    public int DesktopModuleId { get; set; }

    /// <summary>
    /// The definition's default cache timeout in seconds - the legacy "Cache Time (secs):" field, described
    /// there as "Enter the time this object is kept in the Cache".
    /// </summary>
    /// <remarks>
    /// Mapped from <c>ModuleDefinitions.DefaultCacheTime</c>, added by 03.01.00 line 297 as <c>int NOT
    /// NULL</c> with a default of <c>0</c> and re-asserted as <c>int NOT NULL</c> with the same default by
    /// 03.01.01 line 1001.
    /// </remarks>
    public int DefaultCacheTime { get; set; }

    // MIGRATION: §5.3 - the definition members end here, and TempModuleID is dropped. The legacy
    // Library/Components/Modules/ModuleDefinitionInfo.vb declares five properties, but the fifth,
    // TempModuleID (line 66), is a transient artefact used while parsing an incoming module manifest.

    /// <summary>
    /// The unique programmatic name of the owning desktop module, mapped from
    /// <c>DesktopModules.ModuleName</c>.
    /// </summary>
    /// <remarks>
    /// Measured as <c>nvarchar(128)</c>, promoted to <c>NOT NULL</c> by 03.01.00 line 26 and made unique by
    /// line 30 through <c>CONSTRAINT IX_{objectQualifier}DesktopModules_ModuleName UNIQUE NONCLUSTERED
    /// (ModuleName)</c>. It is therefore unique across the whole installation, and it is the stable key by
    /// which a module should be identified.
    /// </remarks>
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// A human-readable description of the owning desktop module, mapped from
    /// <c>DesktopModules.Description</c>. May be absent.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The installed version of the owning desktop module, mapped from <c>DesktopModules.Version</c>. May
    /// be absent.
    /// </summary>
    /// <remarks>
    /// Measured as <c>nvarchar(8) NULL</c> - a deliberately narrow column, so the effective maximum length
    /// is only 8 characters and the value is optional. This is the version the legacy content export wrote
    /// into its payload and that the matching import read back before handing the content to the module's
    /// own portability routine.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Whether the owning desktop module is a premium module, mapped from <c>DesktopModules.IsPremium</c>.
    /// </summary>
    /// <remarks>
    /// Measured as <c>bit NOT NULL</c>, hence non-nullable. A premium module is not automatically available
    /// to every portal: its availability is granted per portal through the portal-to-desktop-module join,
    /// which is a separate concern and contributes no member to this lookup contract.
    /// </remarks>
    public bool IsPremium { get; set; }

    /// <summary>
    /// Whether the owning desktop module is an administration module, mapped from
    /// <c>DesktopModules.IsAdmin</c>.
    /// </summary>
    /// <remarks>
    /// Measured as <c>bit NOT NULL</c>, hence non-nullable. Administration modules are the ones surfaced
    /// through the administrative areas of a portal rather than placed as ordinary page content, so a
    /// definition picker will normally present them separately or filter them out.
    /// </remarks>
    public bool IsAdmin { get; set; }

    /// <summary>Whether the owning desktop module supports content export and import.</summary>
    /// <remarks>
    /// Exposed as a <b>read-only projection</b>: it is computed when the row is read and is not writable
    /// through this contract. The administration screens need it in order to enable or disable the export
    /// and import affordances, which is the only reason it survives the migration while its two sibling
    /// capability flags do not.
    /// </remarks>
    // §5.6 - this boolean is derived, not stored.
    public bool IsPortable { get; set; }

    // MIGRATION: §5.7 - the module folder path and the installer manifest metadata are dropped, namely
    // FolderName, CompatibleVersions, Dependencies and Permissions.
}
