using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Module"/> aggregate, its <see cref="TabModule"/>
/// page placement, its <see cref="ModuleDefinition"/> catalogue entry, the installed
/// <see cref="DesktopModule"/> package and the module transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy <c>ModuleInfo</c> class (ModuleInfo.vb L36) was a single flattened join
/// spanning FIVE tables - <c>Modules</c>, <c>TabModules</c>, <c>ModuleDefinitions</c>,
/// <c>ModuleControls</c> and <c>DesktopModules</c>. The domain model follows the real table
/// boundaries instead, which is the whole reason these projections take more than one entity.
/// </para>
/// <para>
/// Composition is by EXPLICIT MULTI-ENTITY PARAMETERS rather than root-plus-navigations: a projection
/// cannot fetch what it was not given, so every entity a contract needs arrives as an argument. Where a
/// value can only come from a navigation - the package behind the definition - it is read through a
/// null-conditional access into a NULLABLE local, so an unloaded navigation leaves the contract member
/// null rather than throwing. That is the honest representation of "this join did not resolve", and it is
/// observably distinct from "resolved but empty": a settings map that was loaded and held nothing is an
/// EMPTY map, never null. Nothing here consults a repository, nothing lazy-loads, and every projection
/// is synchronous and total.
/// </para>
/// <para>
/// ROW IDENTITY ON THE WIRE IS THE PLACEMENT IDENTIFIER, NOT THE MODULE IDENTIFIER. One module can be
/// placed on many pages - that is what the all-pages flag means - and each placement carries its own
/// pane, order, cache period, appearance and settings. A contract keyed only by module could not address
/// a single placement, which is exactly the ambiguity the legacy flattened class hid.
/// </para>
/// <para>
/// MIGRATION: the sentinel contract is honoured at this boundary rather than collapsed into it. The
/// legacy absent-integer marker was MINUS ONE (Null.vb L41), the absent-date marker was
/// <see cref="DateTime.MinValue"/> and the absent-string marker was THE EMPTY STRING rather than a null
/// reference. An empty string is therefore a real value here and is never converted to null, and a
/// minimum-value date is a real value distinguishable from an absent one, because the contract members
/// carrying dates are nullable and null is their only absence marker.
/// </para>
/// <para>
/// MIGRATION: none of the three identifiers crossing this boundary may be tested for positivity. The
/// module identity seeds at ZERO, the page identity at ZERO and the portal identity at MINUS ONE
/// (01.00.00.SqlDataProvider L221, L140 and L77), so zero is a real module and a real page, and minus
/// one is the host portal. No projection here applies a positive-only guard, a negative-value argument
/// guard, or a clamp to zero, on any identifier.
/// </para>
/// </remarks>
public static class ModuleMappings
{
    /// <summary>
    /// Projects a module and one of its placements onto the row shape the module list renders.
    /// </summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">The placement whose page, order and appearance are reported.</param>
    /// <param name="catalogue">
    /// The definition and package facts the caller resolved, or <see langword="null"/> to resolve them
    /// from the module's own navigations.
    /// </param>
    /// <returns>The list row.</returns>
    /// <remarks>
    /// <para>
    /// The five catalogue values arrive as one argument, falling back to the definition and package
    /// navigations only when the caller resolved none - so a listing and a single read project them from
    /// the same resolved facts and cannot describe a module's package differently.
    /// </para>
    /// </remarks>
    // MIGRATION: THE PRESENTATION STATE IS READ FROM THE PLACEMENT, NOT FROM THE MODULE. This is the
    //   single most likely error in the five-way split, so it is stated once here and holds everywhere
    //   below: the legacy flattened class exposed it at ModuleInfo.vb L257 alongside the module's own
    //   columns, which made it look module-scoped, but the column belongs to <c>TabModules</c> and the
    //   <see cref="Module"/> entity has NO such property. Reading it from the module does not compile,
    //   which is the intended consequence of splitting along the real table boundaries.
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

            // Read-only catalogue projections, taken from the SAME five resolved facts that
            // ToDetail projects, so a row in this listing and the single read of the module it
            // names cannot describe the module's definition or package differently.
            DesktopModuleId = facts.DesktopModuleId,
            FriendlyName = facts.FriendlyName,
            ModuleName = facts.ModuleName,
            Description = facts.Description,
            Version = facts.Version,
        };
    }

    /// <summary>
    /// Projects a module and one of its placements onto the full detail contract.
    /// </summary>
    /// <param name="module">The module to project.</param>
    /// <param name="placement">The placement whose page, order, cache period, icon and visibility are reported.</param>
    /// <param name="catalogue">
    /// The definition and package facts the caller resolved, or <see langword="null"/> to resolve them
    /// from the module's own navigations.
    /// </param>
    /// <returns>The detail contract.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the placement's appearance columns - pane, alignment, colour, border and the print and
    /// syndication flags - are deliberately NOT projected here, and no other response contract carries
    /// them either: they exist to drive server-side markup generation, which this migration excludes.
    /// They remain settable through <see cref="UpdateModuleRequest"/>, making them write-only in the
    /// target, and the stored columns are untouched. <see cref="ModuleSettingsDto"/> is NOT their home.
    /// </para>
    /// </remarks>
    // MIGRATION: NO PERMISSION MEMBER IS PROJECTED - not one of the eight module contracts exposes one,
    //   so writing one would invent a surface no endpoint serves. The key invariant is recorded here
    //   because the legacy shape makes the trap easy to walk into later: in the module-permission table's
    //   ROLE column, MINUS ONE means "All Users", MINUS TWO "Superuser" and MINUS THREE "Unauthenticated
    //   Users" (Globals.vb L95-L97, all declared as STRINGS). All three are REAL principals and none may
    //   ever be mapped to an absent value, because doing so silently strips a grant. In the USER key of
    //   the same table minus one genuinely does mean absent, so the identical literal carries opposite
    //   meanings depending on which column holds it. Should a permission contract ever be added, the role
    //   key must be preserved verbatim and only the user key mapped to absence.
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

            // MIGRATION: an unresolved definition reports an ABSENT package key, never zero. An earlier
            // reading of the schema had it defaulting to zero and treated zero as a legitimate stored
            // value; that is wrong. dbo.DesktopModules.DesktopModuleID is a plain IDENTITY, so it seeds
            // at one, and dbo.ModuleDefinitions.DesktopModuleID is declared NOT NULL with a foreign key
            // onto it - a resolved definition therefore always carries a positive key and zero cannot be
            // one. Reporting zero fabricated a key that identifies no package, and it did so on exactly
            // the path that had the real one in hand.
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
            // where the underlying column is declared NOT NULL in its own table. All four come from the
            // one resolved fact set, so the listing row for this module reports exactly the same values.
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
    /// <para>
    /// Both settings collections are genuine key-value tables, unlike portal configuration, so they are
    /// projected as read-only maps rather than reduced to named members. Names are matched WITHOUT REGARD
    /// TO CASE, which is faithful to the store rather than to the legacy in-memory collection: the
    /// setting-name columns sit under a case-insensitive collation and form half of each table's primary
    /// key, so two names differing only in case cannot coexist as rows. The legacy in-memory collection
    /// was case-sensitive - a divergence recorded on <see cref="ModuleSettingsDto"/>.
    /// </para>
    /// <para>
    /// This projection carries the two identifiers and the two maps and nothing else. Copying the module
    /// and placement columns here as well would duplicate <see cref="ModuleDetailDto"/> and reintroduce
    /// the very ambiguity about which scope a value belongs to that separating the two maps removes.
    /// </para>
    /// </remarks>
    // MIGRATION: both settings tables have COMPOSITE PRIMARY KEYS, so the setting name is half of each
    //   key rather than an incidental label. The projection keeps the name as the map key and the value as
    //   the map value, discarding only the owning identifier, which the contract already carries once
    //   alongside rather than inside each map. A VALUE OF THE EMPTY STRING IS A REAL VALUE and survives
    //   as one - the legacy absent-string marker was itself the empty string (Null.vb L71), so the row's
    //   mere existence is what carries the meaning, and nothing here drops, nulls or trims an empty value.
    //
    // MIGRATION: the contract's placement identifier is NULLABLE while this projection always supplies
    //   one. Null means "no placement was addressed", which is reachable on the INBOUND write payload
    //   only - the same contract is submitted to replace a settings set and a caller may address the
    //   module store alone. This projection therefore requires a placement, and that requirement must not
    //   be relaxed in order to reach the null state: that state belongs to the request direction.
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
    // MIGRATION: the three capability flags are READ FROM THE ENTITY AND NEVER RECOMPUTED HERE. The
    //   package's capability field stays a plain integer of independent bits and the entity exposes
    //   portability, searchability and upgradeability as computed members over it, so decoding it again
    //   would put the bit meanings in two places. They changed from SETTABLE to COMPUTED because three
    //   read-write facades over one packed field lose updates whenever two are set in sequence; a
    //   projection may read them but must never assign them, and no contract accepts them as input.
    //
    // MIGRATION: the package's business-controller name is carried as an OPAQUE STRING and is never
    //   resolved to a type here. The legacy code late-bound a class from exactly this string at five
    //   sites, which the target replaces with an injected factory over a closed, registered set.
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
    /// skin declares. The submitted caching period is stored verbatim, negative values included; zero
    /// means "do not cache" and is likewise passed through untouched.
    /// </remarks>
    // MIGRATION: the pane is no longer caller-supplied and the cache period no longer falls back to the
    //   definition's default. The legacy pane value came from the skin's pane picker, so it belongs to the
    //   excluded skinning surface; the column is nevertheless NOT NULL, which is why a value is supplied
    //   here rather than omitted. The cache period is non-nullable because the legacy screen stored
    //   LITERALLY ZERO for a blank box rather than a sentinel, so there is no "unspecified" state to fall
    //   back from - substituting the definition's default for a submitted zero would silently enable
    //   caching on a module the caller asked not to cache.
    //
    // MIGRATION: the position is carried through UNRESOLVED. The submitted value may be the append
    //   instruction, and resolving it requires reading the pane it is appended to - a database read this
    //   layer neither has nor should acquire. The service overwrites the member immediately after calling
    //   here, so the instruction never reaches a column.
    public static TabModule ToNewPlacement(CreateModuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new TabModule
        {
            TabId = request.TabId,
            PaneName = DefaultPaneName,
            ModuleOrder = request.ModuleOrder,

            // MIGRATION: the submitted cache period is stored VERBATIM and is not clamped. The column is
            // a plain int NOT NULL across the whole DDL chain with no check constraint, and the legacy
            // screen stored whatever parsed. Clamping would accept the caller's value and then silently
            // rewrite it, so the record read back would not be the record submitted. Zero remains the real
            // value "do not cache".
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

        // Placement scope: the seven surviving value arguments of the legacy second provider call.
        //
        // MIGRATION: THE SUBMITTED PAGE IS DELIBERATELY NOT ASSIGNED. It SELECTS the placement: the
        // service reads it, finds the placement the module has on that page, refuses the request when the
        // module is not on it, and passes that placement here - so placement.TabId already equals
        // request.TabId. Assigning it would be a no-op on a correct call and a silent RE-PARENT on an
        // incorrect one, which is a move rather than the edit the caller asked for. A move needs a SECOND
        // member naming the destination, because one page identifier cannot be both the placement being
        // edited and the page it should end up on; the divergence from the legacy settings screen, which
        // had a distinct page picker for that reason, is recorded in MIGRATION_NOTES.md.
        //
        // The seed trap applies to the member the service reads: the page identity seeds at ZERO, so 0 is
        // a legitimate page and no positive-value guard may stand in for a presence test.

        // The position is assigned unresolved, exactly as on the creation projection above: the submitted
        // value may be the append instruction, and the service overwrites this member immediately after
        // this call returns so that the instruction never reaches a column. The service resolves it against
        // the page the placement sits on, which is the page the request named.
        placement.ModuleOrder = request.ModuleOrder;
        placement.CacheTime = request.CacheTime;
        placement.IconFile = request.IconFile;
        placement.Visibility = request.Visibility;
        placement.DisplayTitle = request.DisplayTitle;

        // MIGRATION: the placement's pane-layout and rendering columns - PaneName, Alignment, Color,
        // Border, DisplayPrint and DisplaySyndicate - together with ContainerSrc are deliberately left
        // ALONE rather than assigned. All seven are excluded from UpdateModuleRequest as Web Forms
        // pane-layout, server-side rendering or skinning concerns (AAP 0.2.2.1 and 0.2.2.4), so the
        // request carries no field for any of them; assigning an absent value would silently wipe a stored
        // value on every update, and PaneName is NOT NULL so clearing it would fail the write outright.
        //
        // MIGRATION: IsDeleted IS assigned here, which widens the legacy settings screen. That screen held
        // a bare unconditional assignment of False, so every save silently un-deleted the module; the flag
        // was actually toggled by the placement-delete and recycle-bin paths. Consolidating soft delete
        // and restore onto this contract means this endpoint can now SET a flag the legacy screen could
        // only clear.
        //
        // MIGRATION: CacheTime is a non-nullable integer and zero is a REAL value meaning "do not cache",
        // so there is no coalesce to a stored value here. The submitted value is stored verbatim, with no
        // clamp - the reasoning is on the creation projection above.
    }

    /// <summary>
    /// Collapses a settings sequence into a case-insensitive read-only map, keeping the last value when
    /// a name repeats and skipping a row whose name is blank.
    /// </summary>
    /// <remarks>
    /// Returns an EMPTY map for an empty sequence, never null, so "loaded and held nothing" stays
    /// observably distinct from the unloaded state the contracts express with null elsewhere.
    /// </remarks>
    // MIGRATION: names are matched WITHOUT REGARD TO CASE, which is faithful to the store rather than to
    //   the legacy in-memory collection: the name columns sit under a case-insensitive collation and form
    //   half of each primary key, so two names differing only in case cannot coexist as rows. Choosing the
    //   ordinal comparer would also make a duplicate-name sequence fail on insertion rather than collapse
    //   last-value-wins, which is the non-total behaviour a projection must not have. The legacy
    //   collection was case-SENSITIVE, so this is a real divergence and is recorded as one.
    //
    // MIGRATION: a row whose NAME is blank is skipped, and this is the one place in the file that is not a
    //   straight pass-through. The name is half of a primary key and is not nullable, so no legitimate row
    //   reaches here blank; a null one would make the map insertion fail and break the guarantee that
    //   these projections never fail on the arguments they accept. The skip is bounded to the NAME - an
    //   empty VALUE is a real value and is always kept.
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
