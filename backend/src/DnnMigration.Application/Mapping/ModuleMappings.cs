using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Module"/> aggregate, its <see cref="TabModule"/> page
/// placement, its <see cref="ModuleDefinition"/> catalogue entry, the installed <see cref="DesktopModule"/>
/// package and the module transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// ROW IDENTITY ON THE WIRE IS THE PLACEMENT IDENTIFIER, NOT THE MODULE IDENTIFIER. One module can be
/// placed on many pages - that is what the all-pages flag means - and each placement carries its own pane,
/// order, cache period, appearance and settings. A contract keyed only by module could not address a single
/// placement, which is exactly the ambiguity the legacy flattened class hid.
/// </para>
/// <para>
/// None of the three identifiers crossing this boundary may be tested for positivity. The module identity
/// seeds at ZERO, the page identity at ZERO and the portal identity at MINUS ONE (01.00.00.SqlDataProvider
/// L221, L140 and L77), so zero is a real module and a real page, and minus one is the host portal.
/// </para>
/// </remarks>
public static class ModuleMappings
{
    /// <summary>Projects a module and one of its placements onto the row shape the module list renders.</summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">The placement whose page, order and appearance are reported.</param>
    /// <param name="catalogue">
    /// The definition and package facts the caller resolved, or <see langword="null"/> to resolve them from
    /// the module's own navigations.
    /// </param>
    /// <returns>The list row.</returns>
    public static ModuleListItemDto ToListItem(Module module, TabModule placement, ModuleCatalogueFacts? catalogue)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);

        ModuleCatalogueFacts facts = catalogue ?? ModuleCatalogueFacts.FromNavigation(module);

        return new ModuleListItemDto
        {
            ModuleId = module.ModuleId,
            TabModuleId = placement.TabModuleId,
            TabId = placement.TabId,
            ModuleDefId = module.ModuleDefinitionId,
            ModuleTitle = module.ModuleTitle,
            ModuleOrder = placement.ModuleOrder,
            AllTabs = module.AllTabs,
            Visibility = placement.Visibility,
            IsDeleted = module.IsDeleted,
            DisplayTitle = placement.DisplayTitle,
            StartDate = module.StartDate,
            EndDate = module.EndDate,

            DesktopModuleId = facts.DesktopModuleId,
            FriendlyName = facts.FriendlyName,
            ModuleName = facts.ModuleName,
            Description = facts.Description,
            Version = facts.Version,
        };
    }

    /// <summary>Projects a module and one of its placements onto the full detail contract.</summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">
    /// The placement whose page, order, cache period, icon and visibility are reported.
    /// </param>
    /// <param name="catalogue">
    /// The definition and package facts the caller resolved, or <see langword="null"/> to resolve them from
    /// the module's own navigations.
    /// </param>
    /// <returns>The detail contract.</returns>
    // NO PERMISSION MEMBER IS PROJECTED - not one of the eight module contracts exposes one, so writing one
    // would invent a surface no endpoint serves.
    public static ModuleDetailDto ToDetail(Module module, TabModule placement, ModuleCatalogueFacts? catalogue)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);

        ModuleCatalogueFacts facts = catalogue ?? ModuleCatalogueFacts.FromNavigation(module);

        return new ModuleDetailDto
        {
            // Identity, drawn from across all four tables.
            ModuleId = module.ModuleId,
            TabModuleId = placement.TabModuleId,
            TabId = placement.TabId,
            PortalId = module.PortalId,
            ModuleDefId = module.ModuleDefinitionId,

            DesktopModuleId = facts.DesktopModuleId,

            // Module scope: identical on every page the module appears on.
            ModuleTitle = module.ModuleTitle,
            AllTabs = module.AllTabs,
            Header = module.Header,
            Footer = module.Footer,
            StartDate = module.StartDate,
            EndDate = module.EndDate,

            // MIGRATION: the stored column permits a null, but the legacy contract could not observe the
            // difference - the absent-boolean sentinel is itself false, and the legacy object left this
            // field at its type default rather than sentinel-initialising it.
            InheritViewPermissions = module.InheritViewPermissions ?? false,
            IsDeleted = module.IsDeleted,

            // Placement scope: specific to this one occurrence of the module on this one page.
            ModuleOrder = placement.ModuleOrder,
            CacheTime = placement.CacheTime,
            IconFile = placement.IconFile,
            Visibility = placement.Visibility,
            DisplayTitle = placement.DisplayTitle,

            // Read-only catalogue projections. Each is nullable because the join may not resolve, even
            // where the underlying column is declared NOT NULL in its own table.
            FriendlyName = facts.FriendlyName,
            ModuleName = facts.ModuleName,
            Description = facts.Description,
            Version = facts.Version,
        };
    }

    /// <summary>
    /// Projects a module, its placement and both settings collections onto the configuration contract.
    /// </summary>
    /// <param name="module">The module supplying the module-scoped identifier.</param>
    /// <param name="placement">The placement supplying the placement-scoped identifier.</param>
    /// <param name="moduleSettings">The module-scoped settings.</param>
    /// <param name="placementSettings">The placement-scoped settings.</param>
    /// <returns>The configuration contract.</returns>
    /// <remarks>
    /// Both settings collections are genuine key-value tables, unlike portal configuration, so they are
    /// projected as read-only maps rather than reduced to named members.
    /// </remarks>
    // The contract's placement identifier is NULLABLE while this projection always supplies one. Null means
    // "no placement was addressed", which is reachable on the INBOUND write payload only - the same
    // contract is submitted to replace a settings set and a caller may address the module store alone.
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
            ModuleSettings = ToMap(moduleSettings.Select(setting => (setting.SettingName, setting.SettingValue))),
            TabModuleSettings = ToMap(placementSettings.Select(setting => (setting.SettingName, setting.SettingValue))),
        };
    }

    /// <summary>Projects a catalogue entry onto its transfer contract.</summary>
    /// <param name="definition">The module definition to project.</param>
    /// <param name="desktopModule">
    /// The installed package the definition belongs to, or <see langword="null"/> when it was not loaded.
    /// </param>
    /// <returns>The definition contract.</returns>
    // The package's business-controller name is carried as an OPAQUE STRING and is never resolved to a type
    // here. The legacy code late-bound a class from exactly this string at five sites, which the target
    // replaces with an injected factory over a closed, registered set.
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

    /// <summary>Builds a new module aggregate from a creation request.</summary>
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

    /// <summary>Builds the page placement that a creation request asks for.</summary>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved placement, whose module reference the write path attaches.</returns>
    /// <remarks>
    /// The pane is supplied here rather than taken from the request, because the creation contract
    /// deliberately does not accept one: the pane is part of the excluded Web Forms pane-layout and
    /// skinning surface, while the column behind it is nevertheless not nullable. Every created placement
    /// therefore lands in the conventional content pane, which is the pane every legacy skin declares.
    /// </remarks>
    // MIGRATION: the pane is no longer caller-supplied and the cache period no longer falls back to the
    // definition's default.
    public static TabModule ToNewPlacement(CreateModuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new TabModule
        {
            TabId = request.TabId,
            PaneName = DefaultPaneName,
            ModuleOrder = request.ModuleOrder,

            CacheTime = request.CacheTime,
            IconFile = request.IconFile,
            Visibility = request.Visibility,
            DisplayTitle = request.DisplayTitle,
        };
    }

    /// <summary>Applies a submitted update to a tracked module and one of its placements.</summary>
    /// <param name="module">The tracked module to modify.</param>
    /// <param name="placement">The tracked placement to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// The two intent flags the request also carries - "make this the portal default" and "apply to every
    /// module" - are not stored members and are not written here: they describe work the service performs
    /// after this projection has run.
    /// </remarks>
    public static void ApplyUpdate(Module module, TabModule placement, UpdateModuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(request);

        // Module scope: the eight value arguments of the legacy first provider call, less the identity the
        // route supplies.
        module.ModuleTitle = request.ModuleTitle;
        module.AllTabs = request.AllTabs;
        module.InheritViewPermissions = request.InheritViewPermissions;
        module.Header = request.Header;
        module.Footer = request.Footer;
        module.StartDate = request.StartDate;
        module.EndDate = request.EndDate;
        module.IsDeleted = request.IsDeleted;

        // THE SUBMITTED PAGE IS DELIBERATELY NOT ASSIGNED. It SELECTS the placement: the service reads it,
        // finds the placement the module has on that page, refuses the request when the module is not on
        // it, and passes that placement here - so placement.TabId already equals request.TabId.

        // The position is assigned unresolved, exactly as on the creation projection above: the submitted
        // value may be the append instruction, and the service overwrites this member immediately after
        // this call returns so that the instruction never reaches a column.
        placement.ModuleOrder = request.ModuleOrder;
        placement.CacheTime = request.CacheTime;
        placement.IconFile = request.IconFile;
        placement.Visibility = request.Visibility;
        placement.DisplayTitle = request.DisplayTitle;
    }

    /// <summary>
    /// Collapses a settings sequence into a case-insensitive read-only map, keeping the last value when a
    /// name repeats and skipping a row whose name is blank.
    /// </summary>
    // Names are matched WITHOUT REGARD TO CASE, which is faithful to the store rather than to the legacy
    // in-memory collection: the name columns sit under a case-insensitive collation and form half of each
    // primary key, so two names differing only in case cannot coexist as rows.
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
    /// The pane a module is placed into when the caller names none, spelled as the legacy skins spell it.
    /// </summary>
    private const string DefaultPaneName = "ContentPane";
}
