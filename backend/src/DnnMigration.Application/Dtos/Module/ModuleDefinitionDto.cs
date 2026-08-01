namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: §5.2 - the four-entity split. The legacy
// Library/Components/Modules/ModuleInfo.vb is one 936-line, 58-property class that flattens the
// Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join into a single object. The
// target splits that join along the real table boundaries. This contract projects the
// ModuleDefinitions slice plus the identity-and-capability slice of DesktopModules, and
// deliberately carries nothing from Modules, TabModules or ModuleControls. The sharpest
// consequence is the pair of similarly named cache members on the legacy class: it exposes both
// CacheTime (the per-placement value stored on Modules, ModuleInfo.vb line 203) and
// DefaultCacheTime (the definition-level value, ModuleInfo.vb line 482). Only the latter is a
// property of a definition, so only the latter appears below.
//
// MIGRATION: §5.8 - XML serialisation attributes and IPropertyAccess are dropped. The legacy
// class was decorated for XML round-tripping at ModuleInfo.vb line 36 and every one of its
// properties carried a matching element attribute; it also implemented the token-replacement
// accessor contract IPropertyAccess (declared at line 37, with its members at lines 794 and
// 925). The token-replacement subsystem is excluded, and the wire shape belongs to the DTO
// layer and to one central System.Text.Json naming policy configured at the Api edge. No
// serialisation, data-contract or property-naming attribute therefore appears on this type, and
// neither does any validation attribute - declarative validation lives in Application/Validation
// and there is no validator for module definitions, because they are not writable here.
//
// MIGRATION: §5.9 - terminal-schema derivation. Only the cumulative terminal state of the
// 88-script DDL chain is meaningful; deriving from the baseline alone is actively misleading.
// Measured trace for ModuleDefinitions across every script, case-insensitively and across all
// four object-naming forms: the 01.00.00 baseline at line 65 creates seven columns - ModuleDefID,
// FriendlyName, DesktopSrc, MobileSrc, AdminOrder, EditSrc and Secure. Then 01.00.05 line 442
// adds Description and HostFee; 01.00.07 line 169 adds AdminTabIcon and EditModuleIcon; 01.00.08
// line 5874 adds IsPremium and line 5891 drops HostFee; 02.00.00 line 5173 adds DesktopModuleID
// and line 5254 drops nine columns in one statement - DesktopSrc, MobileSrc, EditSrc, Secure,
// EditModuleIcon, AdminTabIcon, AdminOrder, Description and IsPremium; finally 03.01.00 line 297
// adds DefaultCacheTime. The terminal table therefore holds exactly four columns, which is
// corroborated independently by ModuleDefinitionController: AddModuleDefinition passes only
// DesktopModuleID, FriendlyName and DefaultCacheTime, and UpdateModuleDefinition passes only
// ModuleDefID, FriendlyName and DefaultCacheTime. Nothing else about a definition is persisted.

/// <summary>
/// The read-only lookup contract returned by <c>GET /api/v1/module-definitions</c>, describing one
/// installable module definition that an administrator may choose when placing a module on a page.
/// </summary>
/// <remarks>
/// <para>
/// Each instance is a projection of the terminal <c>dbo.ModuleDefinitions</c> table joined to its
/// owning <c>dbo.DesktopModules</c> row: the first four members are the whole of
/// <c>ModuleDefinitions</c>, and the remaining six are the identity and capability facts from
/// <c>DesktopModules</c> that the administration screens actually need. Shapes in this layer are
/// derived from what the legacy screens posted and rendered rather than from the entity graph, so
/// this type is a boundary contract and never an entity: it carries no navigation property, no
/// tracked state and no behaviour, and no domain entity is exposed through it in either direction.
/// </para>
/// <para>
/// Module definitions are <em>not writable</em> through this API. The endpoint is a lookup surface
/// only - there is no create or update request shape for a definition, and none is implied by this
/// type. Definitions arrive in the database through module installation, which is a subsystem that
/// this migration excludes. The consumer is the module administration screen, whose definition
/// picker binds to this contract and whose selection is then carried as the module definition
/// identifier on the module create and detail contracts.
/// </para>
/// <para>
/// Deliberate exclusions, each annotated inline below with its measured evidence: the transient
/// <c>TempModuleID</c> field; every module-control member, because those describe Web Forms
/// control loading; the module folder path; the two capability flags this migration does not
/// need; the raw capability bit field; the business controller type name; and the installer
/// manifest metadata. The type also carries no audit member - a census of all 88 DDL scripts found
/// zero occurrences of any of the four DotNetNuke audit column names, and neither
/// <c>ModuleDefinitions</c> nor <c>DesktopModules</c> declares one - no paging metadata, since a
/// value of this type is a single lookup row and the controller owns the envelope that wraps a
/// collection of them, and no permission member, because permission evaluation belongs to the
/// infrastructure security layer and to the separate read-only permissions catalogue.
/// </para>
/// </remarks>
public sealed class ModuleDefinitionDto
{
    /// <summary>
    /// The identity of this module definition, mapped from
    /// <c>ModuleDefinitions.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The column is <c>int IDENTITY (1, 1) NOT NULL</c> and is the table's primary key
    /// (01.00.00 baseline, line 66). The seed is <c>1</c>, so no sentinel and no boundary value
    /// carries a second meaning on this member. Callers must not test this value against zero or
    /// against minus one to decide whether a definition is present: across this schema zero is a
    /// genuine identifier for modules, pages and roles, and minus one is a genuine portal
    /// identifier, so both idioms are unsafe as absence checks anywhere in the migrated code.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The display name of this definition - the value the legacy settings screen showed for
    /// <c>Module:</c>, described there as "Displays the name of the module."
    /// </summary>
    /// <remarks>
    /// Mapped from <c>ModuleDefinitions.FriendlyName</c>, measured as <c>nvarchar(128) NOT NULL</c>,
    /// so the effective maximum length is 128 characters. The legacy screen rendered this into a
    /// text box explicitly marked <c>Enabled="False"</c> (modulesettings.ascx line 28) and assigned
    /// it without ever reading it back (ModuleSettings.ascx.vb line 125), which is the original
    /// evidence that a definition's name is presentational rather than editable.
    /// Non-nullable because the column is; the maximum length is documented here only, since
    /// constraint enforcement is the responsibility of the validation layer and of the database.
    /// </remarks>
    public string FriendlyName { get; set; }

    /// <summary>
    /// The identifier of the desktop module that owns this definition, mapped from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// Added by 02.00.00 line 5173 as <c>int NOT NULL</c> with a default of <c>0</c>, and made a
    /// foreign key to <c>DesktopModules</c> at line 5258. Because the column is not nullable and
    /// defaults to zero, <b>zero is a legitimate stored value</b> and must never be interpreted as
    /// "unset". The remaining six members of this contract are projected from the row this
    /// identifier selects.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    /// <summary>
    /// The definition's default cache timeout in seconds - the legacy "Cache Time (secs):" field,
    /// described there as "Enter the time this object is kept in the Cache".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapped from <c>ModuleDefinitions.DefaultCacheTime</c>, added by 03.01.00 line 297 as
    /// <c>int NOT NULL</c> with a default of <c>0</c> and re-asserted as <c>int NOT NULL</c> with
    /// the same default by 03.01.01 line 1001.
    /// </para>
    /// <para>
    /// This is the definition-level default that governs whether caching applies at all. It is not
    /// the configured timeout of a placed module: that value lives on the module placement itself
    /// and is deliberately absent from this contract, per the split annotated at the top of this
    /// file.
    /// </para>
    /// </remarks>
    // MIGRATION: §5.1 - minus one is a REAL STORED VALUE on this member, not an absent value, so
    // it stays a non-nullable integer and no mapper may translate it to null. Evidence: the legacy
    // Website/admin/Modules/ModuleSettings.ascx.vb BindData block at lines 138-142 reads
    //     If objModuleDef.DefaultCacheTime = Null.NullInteger Then
    //         rowCache.Visible = False
    //     Else
    //         txtCacheTime.Text = objModule.CacheTime.ToString
    //     End If
    // and Null.NullInteger returns -1 (Library/Components/Shared/Null.vb line 43). Because the
    // column is NOT NULL with a default of 0, a stored -1 cannot be an artefact of a database
    // null - it must have been written deliberately. Its meaning is "caching is not applicable to
    // this definition; hide the cache-timeout field". Widening this member to a nullable integer
    // would destroy that distinction, because a genuine -1 and a genuine 0 would both have to
    // survive alongside it.
    //
    // MIGRATION: legacy defect, annotated and deliberately NOT repaired. The legacy screen
    // overloads a numeric sentinel as a user-interface visibility switch, conflating "no cache
    // timeout" with "caching does not apply". The behaviour is preserved exactly as measured and no
    // separate boolean is invented to tidy it up, because inventing one would change an externally
    // observable contract that existing callers may depend on.
    public int DefaultCacheTime { get; set; }

    // MIGRATION: §5.3 - the definition members end here, and TempModuleID is dropped. The legacy
    // Library/Components/Modules/ModuleDefinitionInfo.vb declares five properties, but the fifth,
    // TempModuleID (line 66), is a transient artefact used while parsing an incoming module
    // manifest. It has no column in the terminal four-column table proven above, and neither
    // AddModuleDefinition nor UpdateModuleDefinition passes it, so it is never persisted. A field
    // with request-scoped lifetime living on a persistence-shaped class is a legacy defect; it is
    // annotated here and not modelled.
    //
    // The six members below are projected from the owning DesktopModules row.

    /// <summary>
    /// The unique programmatic name of the owning desktop module, mapped from
    /// <c>DesktopModules.ModuleName</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured as <c>nvarchar(128)</c>, promoted to <c>NOT NULL</c> by 03.01.00 line 26 and made
    /// unique by line 30 through <c>CONSTRAINT IX_{objectQualifier}DesktopModules_ModuleName UNIQUE
    /// NONCLUSTERED (ModuleName)</c>. It is therefore unique across the whole installation, and it
    /// is the stable key by which a module should be identified.
    /// </para>
    /// <para>
    /// Three independent findings agree that the display name is <em>not</em> a safe substitute.
    /// The same script drops the older uniqueness constraint that had covered the display name
    /// (line 34) and replaces it with a plain non-unique index (line 38); the legacy lookup helpers
    /// that resolved a desktop module by display name are both marked obsolete precisely because
    /// that name "is not guaranteed to be the same as when the module is created"; and the legacy
    /// content export and import routines built and matched their payload file names from this
    /// value, in the form <c>content.&lt;cleaned module name&gt;.&lt;extension&gt;</c>.
    /// </para>
    /// </remarks>
    public string ModuleName { get; set; }

    /// <summary>
    /// A human-readable description of the owning desktop module, mapped from
    /// <c>DesktopModules.Description</c>. May be absent.
    /// </summary>
    /// <remarks>
    /// Measured as <c>nvarchar(2000) NULL</c>, so the effective maximum length is 2000 characters
    /// and the value is genuinely optional. Nullable here for that reason, but note the sentinel
    /// asymmetry this creates at the boundary: the legacy data layer never surfaced a null string.
    /// Its translation helper mapped a database null to <c>Null.NullString</c>, which returns the
    /// <b>empty string</b> rather than a null reference (Library/Components/Shared/Null.vb line 73),
    /// so a legacy caller observed <c>""</c> exactly where this contract now presents <c>null</c>.
    /// Converting one into the other is a behavioural change a consumer can detect. The convention
    /// this contract adopts is explicit: <c>null</c> means the column was null, and an empty string
    /// means the column held an empty string; the two are distinct and are never folded together.
    /// The mapper in <c>Application/Mapping/ModuleMappings.cs</c> owns that translation and is the
    /// single place it may happen.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// The installed version of the owning desktop module, mapped from
    /// <c>DesktopModules.Version</c>. May be absent.
    /// </summary>
    /// <remarks>
    /// Measured as <c>nvarchar(8) NULL</c> - a deliberately narrow column, so the effective maximum
    /// length is only 8 characters and the value is optional. This is the version the legacy content
    /// export wrote into its payload and that the matching import read back before handing the
    /// content to the module's own portability routine. The same empty-string-versus-null sentinel
    /// asymmetry documented on the description member applies here unchanged, with the same
    /// convention and the same mapper owning the translation.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Whether the owning desktop module is a premium module, mapped from
    /// <c>DesktopModules.IsPremium</c>.
    /// </summary>
    /// <remarks>
    /// Measured as <c>bit NOT NULL</c>, hence non-nullable. A premium module is not automatically
    /// available to every portal: its availability is granted per portal through the
    /// portal-to-desktop-module join, which is a separate concern and contributes no member to this
    /// lookup contract.
    /// </remarks>
    public bool IsPremium { get; set; }

    /// <summary>
    /// Whether the owning desktop module is an administration module, mapped from
    /// <c>DesktopModules.IsAdmin</c>.
    /// </summary>
    /// <remarks>
    /// Measured as <c>bit NOT NULL</c>, hence non-nullable. Administration modules are the ones
    /// surfaced through the administrative areas of a portal rather than placed as ordinary page
    /// content, so a definition picker will normally present them separately or filter them out.
    /// </remarks>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Whether the owning desktop module supports content export and import.
    /// </summary>
    /// <remarks>
    /// Exposed as a <b>read-only projection</b>: it is computed when the row is read and is not
    /// writable through this contract. The administration screens need it in order to enable or
    /// disable the export and import affordances, which is the only reason it survives the
    /// migration while its two sibling capability flags do not.
    /// </remarks>
    // MIGRATION: §5.6 - this boolean is derived, not stored. The legacy
    // Library/Components/Modules/DesktopModuleInfo.vb keeps one integer bit field, SupportedFeatures
    // (column measured as int NOT NULL default 0, added by 03.01.00 line 14 and re-asserted by
    // 03.01.01 line 912), and exposes three boolean facades over it via the enumeration
    // DesktopModuleSupportedFeature { IsPortable = 1, IsSearchable = 2, IsUpgradeable = 4 }
    // declared at lines 30-34. Its private reader at lines 220-229 is
    //     If SupportedFeatures > Null.NullInteger AndAlso (SupportedFeatures And Feature) = Feature
    // so the mask test is additionally guarded by a sentinel check against -1, which means a
    // sentinel-valued bit field reports every capability as false. That measured guard, not the
    // bare mask, is the behaviour a mapper must reproduce.
    //   - The raw bit field itself is NOT exposed. Publishing a bit field on the wire is exactly
    //     the magic integer this migration replaces with named members, and it would oblige every
    //     client to re-implement the masking rule above.
    //   - IsSearchable and IsUpgradeable are dropped: search is deferred in its entirety and the
    //     upgrade subsystem is excluded, so neither flag has a consumer.
    //   - Legacy defect, annotated and deliberately NOT repaired: in the legacy class all three
    //     flags are read/write facades that mutate the one shared bit field through a
    //     read-modify-write, so two concurrent writes can silently lose a flag. Exposing this
    //     member as a read-only projection removes the hazard from this contract without altering
    //     legacy behaviour, because the bit field is not writable here at all.
    public bool IsPortable { get; set; }

    // MIGRATION: §5.4 - every module-control member is dropped: ModuleControlID, ControlKey,
    // ControlSrc, ControlTitle, ControlType, IconFile, ViewOrder, HelpURL and
    // SupportsPartialRendering, all declared on Library/Components/Modules/ModuleControlInfo.vb.
    // These describe how to load an .ascx user control, and Web Forms control loading has no
    // ASP.NET Core equivalent: the legacy path resolved a control for a definition and then loaded
    // it from a virtual path at run time. The legacy module loader and the postback infrastructure
    // are both excluded, so a definition lookup must expose an identity and a display name and must
    // not promise a loadable control path. Module registration and lifecycle do survive, as a
    // domain concern rather than a rendering one. Two consequences worth stating: dropping
    // ControlType also dissolves the question of how to type the security-access-level value it
    // carried, since no member here needs it; and the legacy pair spells the same help-link concept
    // with different casing on two different classes, which is a naming defect this contract simply
    // does not inherit.
    //
    // MIGRATION: §5.5 - BusinessControllerClass is dropped. On the legacy class it held the name of
    // a type that was activated by reflection, and the export and import guards read
    // "If objModule.BusinessControllerClass <> "" And objModule.IsPortable" immediately before
    // calling the framework's reflection-based activator (Export.ascx.vb lines 150-152 and
    // Import.ascx.vb lines 177-179). The target resolves module behaviour from a closed,
    // dependency-injected set through a factory instead of probing assemblies, so the workaround is
    // deleted rather than translated. Publishing a type name on the wire would invite arbitrary
    // activation, which is why the capability boolean above is exposed in its place: it answers the
    // only question the screens actually asked of it.
    //
    // MIGRATION: §5.7 - the module folder path and the installer manifest metadata are dropped,
    // namely FolderName, CompatibleVersions, Dependencies and Permissions. The folder name
    // (measured as nvarchar(128), promoted to NOT NULL by 03.01.00 line 22) is a physical Web Forms
    // module directory, used by the legacy settings screen only to compose a resource-file path from
    // the application path; both the file-system component and the global path helpers it relied on
    // are excluded. The remaining three (measured as nvarchar(500), nvarchar(400) and nvarchar(400),
    // added by 04.03.06 line 54 and 04.05.00 lines 966 and 971) are module installer and packaging
    // metadata, and the installer and packaging subsystems are excluded as well. None of the four
    // has a consumer in a definition lookup.
}
