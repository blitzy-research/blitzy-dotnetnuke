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
    /// <param name="placement">The placement whose page, order, cache period, icon and visibility are reported.</param>
    /// <param name="friendlyName">The definition's display name, or <see langword="null"/> when it could not be resolved.</param>
    /// <returns>The detail contract.</returns>
    /// <remarks>
    /// <para>
    /// The assignments below are grouped exactly as <see cref="ModuleDetailDto"/> groups its members, so
    /// the owning table of every value is visible at the point of projection: the module row, then the
    /// placement row, then the read-only catalogue projections.
    /// </para>
    /// <para>
    /// MIGRATION: the placement's appearance columns - pane, alignment, colour, border and the print and
    /// syndication flags - are deliberately NOT projected here, and no other RESPONSE contract carries
    /// them either. They exist to drive server-side markup generation, which this migration excludes, so
    /// nothing reads them back; they remain settable through <see cref="UpdateModuleRequest"/>, making
    /// them write-only in the target, and the stored columns are untouched. The container column is
    /// likewise preserved in the store but never surfaced, a module container being a skin object.
    /// <see cref="ModuleSettingsDto"/> is NOT their home: it carries the two identifiers and the two
    /// key-value settings maps only.
    /// </para>
    /// </remarks>
    public static ModuleDetailDto ToDetail(Module module, TabModule placement, string? friendlyName)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(placement);

        ModuleDefinition? definition = module.ModuleDefinition;
        DesktopModule? package = definition?.DesktopModule;

        return new ModuleDetailDto
        {
            // Identity, drawn from across all four tables.
            ModuleId = module.ModuleId,
            TabModuleId = placement.TabModuleId,
            TabId = placement.TabId,
            PortalId = module.PortalId,
            ModuleDefId = module.ModuleDefinitionId,

            // MIGRATION: the definition's package key defaults to 0 in the schema, so an unresolved
            // definition and a definition whose package was never linked both report 0 - which is a
            // legitimate stored value here and must not be read as "no package".
            DesktopModuleId = definition?.DesktopModuleId ?? 0,

            // Module scope: identical on every page the module appears on.
            ModuleTitle = module.ModuleTitle,
            AllTabs = module.AllTabs,
            Header = module.Header,
            Footer = module.Footer,
            StartDate = module.StartDate,
            EndDate = module.EndDate,

            // MIGRATION: the stored column permits a null, but the legacy contract could not observe the
            // difference - the absent-boolean sentinel is itself false, and the legacy object left this
            // field at its type default rather than sentinel-initialising it. Coalescing to false is
            // therefore faithful to the legacy reading rather than a loss of information.
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
            FriendlyName = friendlyName ?? definition?.FriendlyName,
            ModuleName = package?.ModuleName,
            Description = package?.Description,
            Version = package?.Version,
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
    /// <para>
    /// Both settings collections are genuine key-value tables, unlike portal configuration, so they are
    /// projected as read-only maps rather than reduced to named members.
    /// </para>
    /// <para>
    /// Names are matched without regard to case. That is faithful to the STORE rather than to the legacy
    /// in-memory collection: the setting-name columns are declared under a case-insensitive collation and
    /// participate in each table's primary key, so two names differing only in case cannot coexist as
    /// rows and a case-sensitive projection would draw a distinction the database cannot express. The
    /// legacy in-memory collection was, by contrast, case-sensitive - a divergence recorded on
    /// <see cref="ModuleSettingsDto"/> rather than absorbed here.
    /// </para>
    /// <para>
    /// MIGRATION: this projection carries the two identifiers and the two maps, and nothing else.
    /// Copying the module and placement columns onto the settings contract as well would duplicate
    /// <see cref="ModuleDetailDto"/> and reintroduce the very ambiguity about which
    /// scope a value belongs to that separating the two maps exists to remove. The placement's appearance
    /// columns - pane, alignment, colour, border and the print and syndication flags - are consequently
    /// WRITE-ONLY in the target: <see cref="UpdateModuleRequest"/> still accepts every one of them, so an
    /// operator can still set them and <c>ApplyUpdate</c> still stores them, but no response contract
    /// reads them back, because they exist solely to drive server-side markup generation and that is
    /// excluded from this migration. The asymmetry is deliberate and the stored columns are untouched.
    /// </para>
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
    /// <returns>An unsaved placement, whose module reference the write path attaches.</returns>
    /// <remarks>
    /// The pane is supplied here rather than taken from the request, because the creation contract
    /// deliberately does not accept one: the pane is part of the excluded Web Forms pane-layout and
    /// skinning surface, while the column behind it is nevertheless not nullable. Every created
    /// placement therefore lands in the conventional content pane, which is the pane every legacy
    /// skin declares. A negative caching period is clamped to zero; zero itself is a real value
    /// meaning "do not cache" and is passed through untouched.
    /// </remarks>
    // MIGRATION: THE PANE IS NO LONGER CALLER-SUPPLIED, AND THE CACHE PERIOD NO LONGER FALLS BACK TO
    //   THE DEFINITION'S DEFAULT. Both follow from the creation contract's measured member set. The
    //   legacy pane value came from the skin's pane picker rather than from a field a user typed, so
    //   it belongs to the excluded skinning surface; the column is nevertheless NOT NULL, which is
    //   why a value is supplied here rather than omitted. The cache period is now a non-nullable
    //   integer because the legacy screen stored LITERALLY ZERO for a blank box rather than a
    //   sentinel, so there is no "unspecified" state to fall back FROM - substituting the
    //   definition's default for a submitted zero would silently enable caching on a module the
    //   caller asked not to cache. A definition whose default cache period is -1 means "caching not
    //   applicable"; that is metadata a client reads from the definition contract, never a
    //   server-side substitution applied here.
    //
    // MIGRATION: THE POSITION IS CARRIED THROUGH UNRESOLVED, AND THAT IS DELIBERATE. The submitted value
    //   may be the append instruction rather than a position, and resolving it requires reading the pane
    //   it is being appended to - a database read this layer neither has nor should acquire. The
    //   application service overwrites this member immediately after calling here, so the instruction
    //   never reaches a column; a projection that tried to be helpful about it would either need a
    //   repository or would have to guess.
    public static TabModule ToNewPlacement(CreateModuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new TabModule
        {
            TabId = request.TabId,
            PaneName = DefaultPaneName,
            ModuleOrder = request.ModuleOrder,

            // MIGRATION: 5.5 - THE SUBMITTED CACHE PERIOD IS STORED VERBATIM AND IS NOT CLAMPED. An earlier
            // revision wrote Math.Max(request.CacheTime, 0) here on the stated ground that the store would
            // refuse to interpret a negative; that ground is false. The column is a plain int NOT NULL
            // across the whole DDL chain with NO check constraint - the only constraint bearing a cache name
            // anywhere is DF_ModuleDefinitions_DefaultCacheTime, a DEFAULT on a different table's column -
            // and the legacy screen stored whatever parsed, Int32.Parse(txtCacheTime.Text) at
            // ModuleSettings.ascx.vb L349-L350, with no comparison of any kind. Clamping therefore accepted
            // the caller's value and then silently rewrote it, so the record read back afterwards was not
            // the record submitted and the caller had no way to detect the substitution. Zero remains the
            // real value "do not cache", reached both by an explicit zero and by an omitted property.
            CacheTime = request.CacheTime,
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

        // Placement scope: the six surviving value arguments of the legacy second provider call.
        //
        // The position is assigned unresolved, exactly as on the creation projection above: the submitted
        // value may be the append instruction, and the service overwrites this member immediately after
        // this call returns so that the instruction never reaches a column.
        placement.ModuleOrder = request.ModuleOrder;
        placement.CacheTime = request.CacheTime;
        placement.IconFile = request.IconFile;
        placement.Visibility = request.Visibility;
        placement.DisplayTitle = request.DisplayTitle;

        // MIGRATION: the placement's pane-layout and rendering columns - PaneName, Alignment, Color, Border,
        // DisplayPrint and DisplaySyndicate - together with ContainerSrc are deliberately left ALONE rather
        // than being assigned. All seven are excluded from UpdateModuleRequest as Web Forms pane-layout,
        // server-side rendering or skinning concerns (AAP 0.2.2.1 and 0.2.2.4), so the request carries no
        // field for any of them; assigning an absent value would silently wipe a stored pane, alignment,
        // colour, border or container on every update, and PaneName is NOT NULL so clearing it would fail
        // the write outright. Not assigning them preserves the stored values exactly, which is what the
        // exclusion means. They are consequently write-once at create for pane and read-only thereafter.
        //
        // MIGRATION: 5.6 - IsDeleted IS assigned here, which is a deliberate widening of the legacy settings
        // screen. That screen held the bare unconditional assignment of False, so every save silently
        // un-deleted the module; the flag was actually toggled by the placement-delete and recycle-bin paths
        // instead. The column is real and is genuinely the ninth argument of the legacy first provider call,
        // so consolidating soft delete and restore onto this contract is justified - but it means this
        // endpoint can now SET a flag the legacy screen could only clear.
        //
        // MIGRATION: 5.5 - CacheTime is a non-nullable integer and zero is a REAL value meaning "do not
        // cache", so there is no coalesce to a stored value here: a blank legacy field wrote literally zero
        // rather than a sentinel. The submitted value is stored VERBATIM, with no clamp - the reasoning is
        // recorded in full on the creation projection above, and the same removal is annotated on both
        // module request validators.
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
