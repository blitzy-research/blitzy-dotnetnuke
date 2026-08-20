using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: the three feature setters are deliberately not carried forward. Each performed a
// read-modify-write over one shared bit field, so setting two capabilities from separately read copies
// silently discarded one.

/// <summary>
/// A module package installed on the DotNetNuke host: the once-per-installation registration of a
/// deployable module, as distinct from the per-page instances placed from it.
/// </summary>
/// <remarks>
/// <para>
/// This is the host-level half of the module model. Two relationships radiate from it, each backed by a
/// real foreign key: the module definitions the package publishes, and the per-portal grants that decide
/// which portals may use it.
/// </para>
/// <para>
/// The entity carries no attribute and takes no dependency, so nothing here states how it is stored.
/// </para>
/// </remarks>
public sealed class DesktopModule : Entity<int>
{
    // The legacy nested DesktopModuleSupportedFeature enumeration is not recreated. It was a flags
    // enumeration in everything but declaration and its members were only ever used as bit masks over one
    // integer column, so promoting it to a domain type would publish a persistence detail as vocabulary.

    /// <summary>
    /// Bit that records support for content export and import, matching the legacy
    /// <c>DesktopModuleSupportedFeature.IsPortable</c> member value.
    /// </summary>
    private const int PortableFeatureMask = 1;

    /// <summary>
    /// Bit that records support for content indexing, matching the legacy
    /// <c>DesktopModuleSupportedFeature.IsSearchable</c> member value.
    /// </summary>
    private const int SearchableFeatureMask = 2;

    /// <summary>
    /// Bit that records support for version-driven upgrade handling, matching the legacy
    /// <c>DesktopModuleSupportedFeature.IsUpgradeable</c> member value.
    /// </summary>
    private const int UpgradeableFeatureMask = 4;

    /// <summary>
    /// The <c>DesktopModuleID</c> column: <c>int IDENTITY(1, 1) NOT NULL</c>, the clustered primary key.
    /// </summary>
    /// <remarks>
    /// This identity seeds at 1, so no key value collides with the legacy integer sentinel.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    /// <summary>The value equality is based on, which is always <see cref="DesktopModuleId"/>.</summary>
    /// <remarks>
    /// Not a column, so the entity configuration must ignore it.
    /// </remarks>
    public override int Identity => DesktopModuleId;

    /// <summary>
    /// The <c>FriendlyName</c> column: <c>nvarchar(128) NOT NULL</c>, the name an administrator sees.
    /// </summary>
    /// <remarks>
    /// Once unique, but the upgrade chain dropped that constraint, so duplicates are legal.
    /// </remarks>
    public string FriendlyName { get; set; }

    /// <summary>The <c>Description</c> column: <c>nvarchar(2000) NULL</c>.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The <c>Version</c> column: <c>nvarchar(8) NULL</c>, the package version in legacy dotted form.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// The <c>IsPremium</c> column: <c>bit NOT NULL</c>. When set, a portal needs an explicit grant before
    /// the package may be used.
    /// </summary>
    public bool IsPremium { get; set; }

    /// <summary>
    /// The <c>IsAdmin</c> column: <c>bit NOT NULL</c>. When set, the package belongs to the administration
    /// experience rather than to portal content.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// The <c>BusinessControllerClass</c> column: <c>nvarchar(200) NULL</c>, the assembly-qualified name of
    /// the type implementing the module's own behaviour.
    /// </summary>
    /// <remarks>
    /// Inert data here, never an activation instruction. The legacy code handed this string to a
    /// reflection-based activator that probed deployed assemblies; module behaviour is now resolved from a
    /// closed, dependency-injected set by an Application-layer factory.
    /// </remarks>
    public string? BusinessControllerClass { get; set; }

    /// <summary>
    /// The <c>FolderName</c> column: <c>nvarchar(128) NOT NULL</c>, the package's deployment folder.
    /// </summary>
    public string FolderName { get; set; }

    /// <summary>
    /// The <c>ModuleName</c> column: <c>nvarchar(128) NOT NULL</c>, the package's stable unique name.
    /// </summary>
    public string ModuleName { get; set; }

    /// <summary>
    /// The <c>SupportedFeatures</c> column: <c>int NOT NULL</c> defaulting to <c>0</c>, a bit field
    /// recording which optional capabilities the module's behaviour type implements.
    /// </summary>
    public int SupportedFeatures { get; set; }

    /// <summary>
    /// The <c>CompatibleVersions</c> column: <c>nvarchar(500) NULL</c>, the host versions the package
    /// declares itself compatible with.
    /// </summary>
    public string? CompatibleVersions { get; set; }

    /// <summary>The <c>Dependencies</c> column: <c>nvarchar(400) NULL</c>, the packages this one requires.</summary>
    public string? Dependencies { get; set; }

    /// <summary>
    /// The <c>Permissions</c> column: <c>nvarchar(400) NULL</c>, the host permissions the package declares
    /// it needs.
    /// </summary>
    public string? Permissions { get; set; }

    // The three properties below are read-only projections over SupportedFeatures rather than the legacy
    // read/write facades, because three read-modify-write facades over one field lose updates.

    /// <summary>Whether the module supports content export and import.</summary>
    /// <remarks>
    /// The negative-value guard reproduces the legacy mask test, which read a sentinel field as no
    /// capabilities.
    /// </remarks>
    public bool IsPortable =>
        SupportedFeatures > -1 && (SupportedFeatures & PortableFeatureMask) == PortableFeatureMask;

    /// <summary>Whether the module supports content indexing.</summary>
    /// <remarks>
    /// The negative-value guard reproduces the legacy mask test, which read a sentinel field as no
    /// capabilities.
    /// </remarks>
    public bool IsSearchable =>
        SupportedFeatures > -1 && (SupportedFeatures & SearchableFeatureMask) == SearchableFeatureMask;

    /// <summary>Whether the module supports version-driven upgrade handling.</summary>
    /// <remarks>
    /// The negative-value guard reproduces the legacy mask test, which read a sentinel field as no
    /// capabilities.
    /// </remarks>
    public bool IsUpgradeable =>
        SupportedFeatures > -1 && (SupportedFeatures & UpgradeableFeatureMask) == UpgradeableFeatureMask;

    /// <summary>
    /// The <c>ModuleDefinitions</c> rows this package publishes. Empty means none; load state is the
    /// repository's decision, so never infer it from a count.
    /// </summary>
    public ICollection<ModuleDefinition> ModuleDefinitions { get; set; } = [];

    /// <summary>
    /// The <c>PortalDesktopModules</c> rows granting portals the use of this package, which is what <see
    /// cref="IsPremium"/> gates. Empty means none.
    /// </summary>
    public ICollection<PortalDesktopModule> PortalDesktopModules { get; set; } = [];
}
