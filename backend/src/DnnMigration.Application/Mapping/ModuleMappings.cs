using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Module"/> aggregate, its <see cref="TabModule"/>
/// page placement, its <see cref="ModuleDefinition"/> catalogue entry and the module transfer
/// contracts.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy <c>ModuleInfo</c> class was a single flattened join over four tables -
/// <c>Modules</c>, <c>TabModules</c>, <c>ModuleDefinitions</c> and <c>ModuleControls</c> - carrying
/// fifty-eight properties. The domain model follows the real table boundaries instead, so a projection
/// here takes the module and its placement as two arguments and reassembles the flat wire shape the
/// screens expect. That is the whole reason these projections take more than one entity.
/// </para>
/// <para>
/// Row identity on the wire is the placement identifier, not the module identifier. One module can be
/// placed on many pages - that is what the all-pages flag means - and each placement carries its own
/// pane, order, cache period, appearance and settings. A contract keyed only by module could not
/// address a single placement, which is exactly the ambiguity the legacy flattened class hid.
/// </para>
/// <para>
/// The definition's friendly name is supplied as an argument rather than read through the module's own
/// navigation, so a caller that has already resolved a whole page of names in one read does not force a
/// second read per row.
/// </para>
/// </remarks>
public static class ModuleMappings
{
    /// <summary>
    /// Projects a module and one of its placements onto the row shape the module list renders.
    /// </summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">The placement whose page, order and appearance are reported.</param>
    /// <param name="friendlyName">The definition's display name, or <see langword="null"/> when it could not be resolved.</param>
    /// <returns>The list row.</returns>
    public static ModuleListItemDto ToListItem(Module module, TabModule placement, string? friendlyName)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);

        return new ModuleListItemDto
        {
            ModuleId = module.ModuleId,
            TabModuleId = placement.TabModuleId,
            TabId = placement.TabId,
            ModuleDefId = module.ModuleDefinitionId,
            ModuleTitle = module.ModuleTitle,
            FriendlyName = friendlyName ?? module.ModuleDefinition?.FriendlyName,
            ModuleOrder = placement.ModuleOrder,
            AllTabs = module.AllTabs,
            Visibility = placement.Visibility,
            IsDeleted = module.IsDeleted,
            DisplayTitle = placement.DisplayTitle,
            StartDate = module.StartDate,
            EndDate = module.EndDate,
        };
    }

    /// <summary>
    /// Projects a module and one of its placements onto the full detail contract.
    /// </summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">The placement whose page, order and appearance are reported.</param>
    /// <param name="friendlyName">The definition's display name, or <see langword="null"/> when it could not be resolved.</param>
    /// <returns>The detail contract.</returns>
    public static ModuleDetailDto ToDetail(Module module, TabModule placement, string? friendlyName)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);

        return new ModuleDetailDto
        {
            ModuleId = module.ModuleId,
            TabModuleId = placement.TabModuleId,
            TabId = placement.TabId,
            PortalId = module.PortalId,
            ModuleDefId = module.ModuleDefinitionId,
            FriendlyName = friendlyName ?? module.ModuleDefinition?.FriendlyName ?? string.Empty,
            ModuleTitle = module.ModuleTitle,
            PaneName = placement.PaneName,
            ModuleOrder = placement.ModuleOrder,
            AllTabs = module.AllTabs,
            IsDeleted = module.IsDeleted,
            InheritViewPermissions = module.InheritViewPermissions,
            Header = module.Header,
            Footer = module.Footer,
            StartDate = module.StartDate,
            EndDate = module.EndDate,
            CacheTime = placement.CacheTime,
            IconFile = placement.IconFile,
            Alignment = placement.Alignment,
            Color = placement.Color,
            Border = placement.Border,
            Visibility = placement.Visibility,
            DisplayTitle = placement.DisplayTitle,
            DisplayPrint = placement.DisplayPrint,
            DisplaySyndicate = placement.DisplaySyndicate,
        };
    }

    /// <summary>
    /// Projects a module, its placement and both settings collections onto the configuration contract.
    /// </summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">The placement whose appearance and settings are reported.</param>
    /// <param name="moduleSettings">The module-scoped settings.</param>
    /// <param name="placementSettings">The placement-scoped settings.</param>
    /// <returns>The configuration contract.</returns>
    /// <remarks>
    /// Both settings collections are genuine key-value tables, unlike portal configuration, so they are
    /// projected as read-only maps compared without regard to case - which is how the legacy screens
    /// read them, having stored them in a case-insensitive hash table.
    /// </remarks>
    public static ModuleSettingsDto ToSettings(
        Module module,
        TabModule placement,
        IReadOnlyList<ModuleSetting> moduleSettings,
        IReadOnlyList<TabModuleSetting> placementSettings)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(moduleSettings);
        ArgumentNullException.ThrowIfNull(placementSettings);

        return new ModuleSettingsDto
        {
            ModuleId = module.ModuleId,
            TabModuleId = placement.TabModuleId,
            ModuleTitle = module.ModuleTitle,
            AllTabs = module.AllTabs,
            InheritViewPermissions = module.InheritViewPermissions,
            StartDate = module.StartDate,
            EndDate = module.EndDate,
            Header = module.Header,
            Footer = module.Footer,
            CacheTime = placement.CacheTime,
            IconFile = placement.IconFile,
            Alignment = placement.Alignment,
            Color = placement.Color,
            Border = placement.Border,
            Visibility = placement.Visibility,
            DisplayTitle = placement.DisplayTitle,
            DisplayPrint = placement.DisplayPrint,
            DisplaySyndicate = placement.DisplaySyndicate,
            ModuleSettings = ToMap(moduleSettings.Select(setting => (setting.SettingName, setting.SettingValue))),
            TabModuleSettings = ToMap(placementSettings.Select(setting => (setting.SettingName, setting.SettingValue))),
        };
    }

    /// <summary>
    /// Projects a catalogue entry onto its transfer contract.
    /// </summary>
    /// <param name="definition">The module definition to project.</param>
    /// <param name="desktopModule">The installed package the definition belongs to, or <see langword="null"/> when it was not loaded.</param>
    /// <returns>The definition contract.</returns>
    /// <remarks>
    /// The portability flag is read from the package's capability bitmask rather than recomputed here:
    /// the entity already decodes the mask, so there is exactly one place that knows which bit means
    /// what.
    /// </remarks>
    public static ModuleDefinitionDto ToDto(ModuleDefinition definition, DesktopModule? desktopModule)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var package = desktopModule ?? definition.DesktopModule;

        return new ModuleDefinitionDto
        {
            ModuleDefId = definition.ModuleDefinitionId,
            FriendlyName = definition.FriendlyName,
            DesktopModuleId = definition.DesktopModuleId,
            DefaultCacheTime = definition.DefaultCacheTime,
            ModuleName = package?.ModuleName ?? string.Empty,
            Description = package?.Description,
            Version = package?.Version,
            IsPremium = package?.IsPremium ?? false,
            IsAdmin = package?.IsAdmin ?? false,
            IsPortable = package?.IsPortable ?? false,
        };
    }

    /// <summary>
    /// Builds a new module aggregate from a creation request.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the module belongs to.</param>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved module aggregate, without its placement.</returns>
    public static Module ToNewModule(int portalId, CreateModuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new Module
        {
            PortalId = portalId,
            ModuleDefinitionId = request.ModuleDefId,
            ModuleTitle = request.ModuleTitle,
            AllTabs = request.AllTabs,
            IsDeleted = false,
            InheritViewPermissions = request.InheritViewPermissions,
            Header = request.Header,
            Footer = request.Footer,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
        };
    }

    /// <summary>
    /// Builds the page placement that a creation request asks for.
    /// </summary>
    /// <param name="request">The submitted creation request.</param>
    /// <param name="defaultCacheTime">The definition's default cache period, used when the request names none.</param>
    /// <returns>An unsaved placement, whose module reference the write path attaches.</returns>
    /// <remarks>
    /// A caching period the caller did not supply falls back to the definition's own default rather
    /// than to zero, because zero means "do not cache" and would silently change a module's behaviour.
    /// A negative period is clamped away for the same reason.
    /// </remarks>
    public static TabModule ToNewPlacement(CreateModuleRequest request, int defaultCacheTime)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new TabModule
        {
            TabId = request.TabId,
            PaneName = string.IsNullOrWhiteSpace(request.PaneName) ? DefaultPaneName : request.PaneName,
            ModuleOrder = request.ModuleOrder,
            CacheTime = Math.Max(request.CacheTime ?? defaultCacheTime, 0),
            IconFile = request.IconFile,
            Visibility = request.Visibility,
            DisplayTitle = request.DisplayTitle,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked module and one of its placements.
    /// </summary>
    /// <param name="module">The tracked module to modify.</param>
    /// <param name="placement">The tracked placement to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// The two intent flags the request also carries - "make this the portal default" and "apply to
    /// every module" - are not stored members and are therefore not written here. They describe work
    /// the service performs after this projection has run, and mapping them onto columns would silently
    /// invent state the schema does not have.
    /// </remarks>
    public static void ApplyUpdate(Module module, TabModule placement, UpdateModuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(request);

        module.ModuleTitle = request.ModuleTitle;
        module.AllTabs = request.AllTabs;
        module.InheritViewPermissions = request.InheritViewPermissions;
        module.Header = request.Header;
        module.Footer = request.Footer;
        module.StartDate = request.StartDate;
        module.EndDate = request.EndDate;

        placement.PaneName = string.IsNullOrWhiteSpace(request.PaneName) ? DefaultPaneName : request.PaneName;
        placement.ModuleOrder = request.ModuleOrder;
        placement.CacheTime = Math.Max(request.CacheTime ?? placement.CacheTime, 0);
        placement.IconFile = request.IconFile;
        placement.Alignment = request.Alignment;
        placement.Color = request.Color;
        placement.Border = request.Border;
        placement.Visibility = request.Visibility;
        placement.DisplayTitle = request.DisplayTitle;
        placement.DisplayPrint = request.DisplayPrint;
        placement.DisplaySyndicate = request.DisplaySyndicate;

        // MIGRATION: placement.ContainerSrc is deliberately left alone rather than being cleared. A module
        // container is a skin object and skinning is excluded by AAP 0.2.2.4, so the request carries no
        // container field; assigning the absent value would silently wipe a stored container on every
        // update. Not assigning it preserves the stored value exactly, which is what the exclusion means.
    }

    /// <summary>
    /// Collapses a settings sequence into a case-insensitive read-only map, keeping the last value when
    /// a name repeats.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ToMap(IEnumerable<(string Name, string Value)> settings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in settings)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[name] = value;
        }

        return map;
    }

    /// <summary>
    /// The pane a module is placed into when the caller names none, spelled as the legacy skins spell
    /// it.
    /// </summary>
    private const string DefaultPaneName = "ContentPane";
}
