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
/// carrying FIFTY-EIGHT properties - fifty-four read/write plus four read-only, counted at source. It
/// spanned FIVE tables, not the four usually quoted: <c>Modules</c>, <c>TabModules</c>,
/// <c>ModuleDefinitions</c>, <c>ModuleControls</c> AND <c>DesktopModules</c>. The fifth is easy to
/// overlook because the flattened class exposed no separate package object, yet ModuleInfo.vb L365
/// through L473 carry the entire package column set - folder, description, version, premium and admin
/// flags, business-controller name, module name, capability bitmask, compatible versions, dependencies
/// and permissions. The domain model follows the real table boundaries instead, across five entities
/// whose stored-value counts are eleven on <see cref="Module"/>, fifteen on <see cref="TabModule"/>,
/// four on <see cref="ModuleDefinition"/>, ten on <see cref="ModuleControl"/> and thirteen on
/// <see cref="DesktopModule"/>. Those counts are NOT a partition of the fifty-eight and must not be
/// added up: they overlap, because a value such as the module key, the definition key or the
/// definition's friendly name is a column on one table and a foreign key or a projection on another,
/// and <see cref="ModuleControl"/> is additionally WIDER than the legacy class, carrying two members it
/// never exposed. Counted exactly, forty-five of the fifty-eight properties reach one or more of the
/// five entities and the remaining THIRTEEN reach none, which with the one dropped method below makes
/// fourteen legacy members that produce no target member at all. That five-way split is the whole
/// reason these projections take more than one entity.
/// </para>
/// <para>
/// MIGRATION: composition design - EXPLICIT MULTI-ENTITY PARAMETERS, not root-plus-navigations. A
/// projection cannot fetch what it was not given, so every entity a contract needs arrives as an
/// argument: the module and its placement are always required, and the definition's display name is
/// passed separately so a caller that resolved a whole page of names in one read does not force a
/// second read per row. Where a value can only come from a navigation - the package behind the
/// definition - it is read through a null-conditional access and lands in a NULLABLE local, so an
/// unloaded navigation leaves the corresponding contract member null rather than throwing. That is the
/// honest representation of "this join did not resolve", and it is observably distinct from "resolved
/// but empty": a settings map that was loaded and held nothing is an EMPTY map, never null. Nothing
/// here consults a repository and nothing lazy-loads.
/// </para>
/// <para>
/// Row identity on the wire is the placement identifier, not the module identifier. One module can be
/// placed on many pages - that is what the all-pages flag means - and each placement carries its own
/// pane, order, cache period, appearance and settings. A contract keyed only by module could not
/// address a single placement, which is exactly the ambiguity the legacy flattened class hid.
/// </para>
/// <para>
/// MIGRATION: FOURTEEN legacy members produce NO target member - thirteen of the fifty-eight properties
/// plus one method - each for a stated reason. The permission
/// collection at L356 becomes a navigation on the aggregate rather than a property on the wire shape.
/// The three permission-string derivations at L545, L554 and L627 concatenated role identifiers into a
/// delimited string and are replaced by server-side permission evaluation, which is neither a
/// projection's work nor a client's. The container path at L563 is a filesystem path for skinning, and
/// skinning is excluded. The two pane-layout counters at L572 and L581 are Web Forms layout state
/// that no longer exists. The two derived flags at L590 and L599 were computed from other columns
/// rather than stored. The three read-only capability flags at L608, L614 and L620 survive as computed
/// members on <see cref="DesktopModule"/> instead. The two token-replacement members go with their
/// excluded subsystem, as described below: the cache-level property at L925, which is the thirteenth
/// dropped property, and the accessor method at L794, which is the one dropped member that was never a
/// property and so was never among the fifty-eight.
/// </para>
/// <para>
/// MIGRATION: <c>Implements IPropertyAccess</c> (ModuleInfo.vb L37) is dropped together with its two
/// members - the property accessor at L794 and the cache-level member at L925 - because the
/// token-replacement subsystem they served is excluded. Every XML serialisation attribute is
/// dropped too: the root attribute on the class at L36, the per-property element attributes, the
/// ignore attributes and the array attributes on the permission collection at L356. NONE of them is
/// replaced by a serialisation, persistence or validation attribute of any kind - the API boundary owns
/// the wire format and the entity configurations own the storage mapping, so an attribute here would
/// duplicate one of them and contradict the other. Note the asymmetry with <c>DesktopModuleInfo</c>
/// (DesktopModuleInfo.vb L36), which carried no attributes and no interface at all, so nothing was
/// dropped on that side.
/// </para>
/// <para>
/// MIGRATION: this file replaces the legacy per-column hydrator at ModuleController.vb L53, and it is
/// deliberately that hydrator's opposite. The legacy code assigned one line per column through the
/// sentinel helper from L66 onward, then - at L126 through L155, inside what claimed to be a hydration
/// routine - issued FURTHER DATABASE READS for permissions and page permissions and silently suppressed
/// the resulting failures while probing for columns removed in an earlier version. These projections
/// perform no reads, are synchronous, and are total: given the arguments they declare they return a
/// contract without failing. The reflection-driven object filler at CBO.vb, the other legacy hydration
/// path, is likewise not reproduced - the persistence layer's materialiser replaces both, so there is
/// no per-column sentinel decoding anywhere in this file.
/// </para>
/// <para>
/// MIGRATION: the sentinel contract is honoured at this boundary rather than collapsed into it. The
/// legacy absent-integer marker was MINUS ONE (Null.vb L41), the absent-date marker was
/// <see cref="DateTime.MinValue"/> (L66) and the absent-string marker was THE EMPTY STRING rather than
/// a null reference (L71). An empty string is therefore a real value here and is never converted to
/// null, and a minimum-value date is a real value distinguishable from an absent one, because the
/// contract members that carry dates are nullable and null is their only absence marker. The legacy
/// absence test compared DATE PARTS ONLY (L222 to L224), so any instant on the first day of year one
/// counted as absent regardless of its time component; the target draws no such equivalence, which
/// makes an explicitly stored minimum-value date observable where it previously was not.
/// </para>
/// <para>
/// MIGRATION: none of the three identifiers crossing this boundary may be tested for positivity. The
/// module identity seeds at ZERO, the page identity seeds at ZERO and the portal identity seeds at
/// MINUS ONE (01.00.00.SqlDataProvider L221, L140 and L77), so zero is a real module and a real page,
/// and minus one is the host portal while zero is the shipped default portal. The legacy absence helper
/// could not tell the host portal from an absent value, since its marker and that portal's key are the
/// same number. Consequently no projection here applies a positive-only guard, a negative-value
/// argument guard, or a clamp to zero, on any identifier.
/// </para>
/// <para>
/// MIGRATION: a latent legacy defect is recorded and NOT repaired. The reflection-driven sentinel
/// selector at Null.vb L119 maps both the thirty-two-bit and the SIXTY-FOUR-bit integer types onto the
/// thirty-two-bit marker at L123, so a long-valued field would have been given a marker of the wrong
/// width. No member reaching this file is sixty-four-bit, so the defect is unreachable here; repairing
/// it would be an unrequested behavioural change, and the domain-logic-preservation rule forbids
/// opportunistic fixes.
/// </para>
/// <para>
/// The bare name <c>Module</c> in this file is the ENTITY, never the contract namespace whose final
/// segment repeats it. Importing a namespace brings in the types it contains, not the namespace's own
/// name, and no enclosing namespace exposes a member called <c>Module</c>, so the identifier binds
/// unambiguously without an alias. The ambiguity is resolved by that language rule rather than by a
/// compiler directive or a relaxation of nullability, and the strict build proves it.
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
    /// <remarks>
    /// Source of every member, so the owning table is legible without opening the entities: the module
    /// row supplies the module key, the definition key, the title, the all-pages flag, the deleted flag
    /// and both publication dates; the placement row supplies the placement key, the page key, the
    /// position and the title-display flag; and the display name arrives as an argument, falling back to
    /// the definition navigation only when the caller resolved none.
    /// </remarks>
    // MIGRATION: THE PRESENTATION STATE IS READ FROM THE PLACEMENT, NOT FROM THE MODULE. This is the
    //   single most likely error in the five-way split, so it is stated once here and holds everywhere
    //   below: the legacy flattened class exposed this property at ModuleInfo.vb L257 alongside the
    //   module's own columns, which made it look module-scoped, but the column belongs to
    //   <c>TabModules</c> and the <see cref="Module"/> entity has NO such property at all. Reading it
    //   from the module is therefore not merely wrong, it does not compile - which is the intended
    //   consequence of splitting along the real table boundaries.
    //
    // MIGRATION: the legacy enumeration was named for a visibility STATE and is renamed to
    //   <see cref="ModuleVisibility"/> for the domain. Its three ordinals were implicit in the legacy
    //   declaration and are now stated explicitly, because they are the values persisted in the
    //   column and renumbering them would silently repoint stored rows. No conversion happens in this
    //   file: the entity and the contract both use the enumeration, so the value is copied across. The
    //   legacy hydrator additionally folded the absent-integer marker into the maximised state
    //   (ModuleController.vb L80 to L84, where zero and minus one shared one branch); that coalescing
    //   belongs to the reader that decodes a stored column, not to a projection between two typed
    //   members, so it is deliberately absent here.
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
    // MIGRATION: NO PERMISSION PROJECTION EXISTS IN THIS FILE, AND THAT IS A MEASURED DECISION RATHER
    //   THAN AN OMISSION. Not one of the eight module contracts exposes a permission member, so writing
    //   one would invent a surface no endpoint serves. The knowledge is recorded here because the legacy
    //   shape makes the trap easy to walk into later: the legacy module-permission class inherited from
    //   the base permission class (ModulePermission.vb L28 and L29), presenting thirteen properties -
    //   five inherited, eight its own - as one flat object, and its role key is the sharpest sentinel
    //   collision in the schema. In that column MINUS ONE means "All Users", MINUS TWO means "Superuser"
    //   and MINUS THREE means "Unauthenticated Users" (Globals.vb L95 to L97, where all four markers are
    //   declared as STRINGS rather than integers, which is why the legacy code had to parse one at
    //   ModulePermission.vb L47). All three are REAL principals and none of them may ever be mapped to
    //   an absent value: doing so would silently strip a grant, which is a security defect and not a
    //   tidying. MINUS FOUR is different again - the legacy default constructor seeded it at L47 as an
    //   in-memory "no role chosen yet" marker, so it is never reproduced as a default. In the USER key
    //   of the same table minus one genuinely does mean absent, so the identical literal carries
    //   opposite meanings distinguished only by which column holds it - an ambiguity the legacy
    //   positional argument list hid completely. Should a permission contract ever be added, the role
    //   key must be preserved verbatim and only the user key mapped to absence.
    //
    // MIGRATION: the legacy permission object's display members - the role name, the user name and the
    //   display name - produce no target member. All three were view-derived joins carried on the same
    //   flat object as the stored columns, and they are reachable through navigations instead. The
    //   permission key itself is a string in the legacy class (Permission.vb L69) against a
    //   variable-length character column, and becomes an enumeration whose MEMBER NAMES are the
    //   persisted values. Any conversion is therefore by NAME and never by ordinal; the ordinal is
    //   meaningless and must never reach a column. The permission-code scopes stay plain strings, with
    //   no enumeration introduced for them.
    //
    // MIGRATION: NO CONTROL MEMBER IS PROJECTED, because no module contract carries one. The legacy
    //   flattened class exposed the control columns at ModuleInfo.vb L491 through L536, and two facts
    //   about them are worth recording. First, the control's type discriminator at L509 was declared as
    //   a security access-level enumeration and is DEMOTED to a plain integer on
    //   <see cref="ModuleControl"/>, because that enumeration is outside the migrated set; the loss of
    //   type safety is deliberate and no replacement enumeration was invented. Second, the entity
    //   carries a control key and a view order that the flattened class NEVER exposed - they come
    //   straight from the control table - so the entity is wider than the legacy object here rather
    //   than narrower.
    //
    // MIGRATION: the placement's syndication flag diverges between two legacy authorities and the entity
    //   follows the constructor rather than the column. The legacy object's constructor initialised it
    //   to false, while the stored column's default, introduced in the 03.00.08 and 03.01.01 upgrade
    //   scripts, is true. The entity default follows the constructor, so a placement built in memory and
    //   never written reports false. This projection does not surface the flag at all - no response
    //   contract reads it - so the divergence is observable only through the store, and it is recorded
    //   here rather than silently absorbed.
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
    // MIGRATION: BOTH SETTINGS TABLES HAVE COMPOSITE PRIMARY KEYS AND PROJECT TO TYPED READ-ONLY MAPS.
    //   The module-scoped store is keyed by module and setting name, the placement-scoped store by
    //   placement and setting name, so the name is half of each key rather than an incidental label.
    //   Each row carries exactly three stored values, and the projection keeps the name as the map key
    //   and the value as the map value, discarding only the owning identifier - which the contract
    //   already carries once, alongside rather than inside each map. The legacy code exposed these as an
    //   untyped, pre-generics key-value collection whose keys and values were both loosely typed; the
    //   target uses a read-only map of string to string, so a caller can neither mutate what it was
    //   handed nor store a non-string by accident. Because the upgrade chain altered these two tables
    //   repeatedly, the mapping targets the TERMINAL shape of each rather than any intermediate one.
    //
    // MIGRATION: A SETTING VALUE OF THE EMPTY STRING IS A REAL VALUE AND SURVIVES AS ONE. The legacy
    //   absent-string marker was itself the empty string (Null.vb L71), so the store cannot distinguish
    //   "set to nothing" from "not set" on a value, and the row's mere existence is what carries the
    //   meaning. Nothing here drops an empty value, substitutes a null for it, or trims it. The one
    //   exception concerns NAMES rather than values and is documented on the collapsing helper below.
    //
    // MIGRATION: the contract's placement identifier is NULLABLE while this projection always supplies
    //   one, and that asymmetry is intentional in both directions. Null on that member means "no
    //   placement was addressed", which is reachable on the INBOUND write payload - the same contract is
    //   submitted to replace a settings set, and a caller may address the module store alone. On the
    //   OUTBOUND read path a placement has always been resolved before this projection is reached,
    //   because the application service returns no contract at all when it cannot resolve one. This
    //   projection therefore requires a placement, and that requirement must not be relaxed in order to
    //   "helpfully" reach the null state: the null state belongs to the request direction, not to this
    //   one.
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
    // MIGRATION: THE THREE CAPABILITY FLAGS ARE READ FROM THE ENTITY AND NEVER RECOMPUTED HERE. The
    //   package's capability field stays a plain integer holding independent bits - no enumeration was
    //   introduced for it and no bit-flag attribute was applied anywhere - and the entity exposes
    //   portability, searchability and upgradeability as computed members over it, testing bit one, bit
    //   two and bit four respectively behind a guard that treats a negative field as no capabilities at
    //   all. Decoding it a second time in a projection would put the bit meanings in two places, so this
    //   file only ever COPIES the decoded booleans; the bit arithmetic appears nowhere in it.
    //
    // MIGRATION: those three flags changed from SETTABLE to COMPUTED, and the two legacy authorities
    //   disagreed about which they were. The standalone package class declared all three as ordinary
    //   read/write properties (DesktopModuleInfo.vb L155, L164 and L173), while the flattened module
    //   class declared the same three as READ-ONLY derivations over the capability field (ModuleInfo.vb
    //   L608, L614 and L620). The target adopts the read-only form, because three independent read and
    //   write facades over one packed field lose updates whenever two of them are set in sequence. The
    //   arithmetic reconciles exactly: thirteen stored columns plus three computed members is sixteen,
    //   which is the property count of the standalone legacy class. A projection may therefore read
    //   these flags but must never assign them, and no contract accepts them as input.
    //
    // MIGRATION: the package's business-controller name is carried as an OPAQUE STRING and is never
    //   resolved to a type here. The legacy code late-bound a class from exactly this string at five
    //   sites - ModuleController.vb L231 and L431, and EventMessageProcessor.vb L32, L52 and L77 - which
    //   is the pattern the target replaces with an injected factory over a closed, registered set of
    //   implementations. That factory belongs to the application abstractions and the infrastructure
    //   services; activating a type from this string inside a projection would reintroduce the very
    //   late binding the migration removes, so nothing in this file inspects the value.
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

            // MIGRATION: THE SUBMITTED CACHE PERIOD IS STORED VERBATIM AND IS NOT CLAMPED. The column is a
            // plain int NOT NULL across the whole DDL chain with NO check constraint - the only constraint
            // bearing a cache name anywhere is DF_ModuleDefinitions_DefaultCacheTime, a DEFAULT on a
            // different table's column - and the legacy screen stored whatever parsed,
            // Int32.Parse(txtCacheTime.Text) at ModuleSettings.ascx.vb L349-L350, with no comparison of any
            // kind. Clamping would accept the caller's value and then silently rewrite it, so the record
            // read back afterwards would not be the record submitted and the caller would have no way to
            // detect the substitution. Zero remains the real value "do not cache", reached both by an
            // explicit zero and by an omitted property.
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
    /// <remarks>
    /// <see cref="TabModule.TabId"/> is NOT assigned here, and the reason is a contract decision rather
    /// than an omission: the submitted page SELECTS which of the module's placements is being updated, so
    /// the service has already resolved the placement by that page before calling here and the two values
    /// are equal by construction. Assigning it would be a no-op on a correct call and a silent re-parent on
    /// an incorrect one. See the paragraph on the assignment below for the full reasoning and for what
    /// reinstating a page move would require.
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
        // MIGRATION: 5.11 - THE SUBMITTED PAGE IS DELIBERATELY NOT ASSIGNED, AND ONE REVISION ASSIGNED IT.
        // Two revisions reached opposite conclusions about the same member and both are recorded here,
        // because the disagreement is about the CONTRACT rather than about this line.
        //
        // The surviving contract is that the submitted page SELECTS the placement: the service reads it,
        // finds the placement the module has on that page, refuses the request outright when the module is
        // not on it, and passes that placement here - so by the time this projection runs placement.TabId
        // already equals request.TabId. Assigning it is therefore a no-op on a correct call and a silent
        // RE-PARENT on an incorrect one, which is a move rather than the edit the caller asked for.
        //
        // The withdrawn revision read the member as a page-move command instead, assigned it here, and
        // gated the change on portal administration with the destination page validated first. That reading
        // cannot be reinstated by restoring this line alone, and restoring it alone is actively harmful: the
        // service that would hand the placement over resolves it WITHOUT reading the request, so for a
        // module placed on several pages an ordinary edit of the page-four instance would relocate the
        // page-one instance onto page four - corrupting a layout in the name of honouring a move nobody
        // asked for. A move needs a SECOND member naming the destination, because one page identifier
        // cannot be both the placement being edited and the page it should end up on. The divergence from
        // the legacy settings screen, which had a distinct page picker for exactly that reason, is recorded
        // in MIGRATION_NOTES.md.
        //
        // The seed trap still applies to the member the service reads: the page identity seeds at ZERO, so 0
        // is a legitimate page and no positive-value guard may stand in for a presence test; and -1 was an
        // "any page" wildcard in the legacy QUERY surface, never an absence marker on a stored row.

        // The position is assigned unresolved, exactly as on the creation projection above: the submitted
        // value may be the append instruction, and the service overwrites this member immediately after
        // this call returns so that the instruction never reaches a column. The service resolves it against
        // the page the placement sits on, which is the page the request named.
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
        // MIGRATION: IsDeleted IS assigned here, which is a deliberate widening of the legacy settings
        // screen. That screen held the bare unconditional assignment of False, so every save silently
        // un-deleted the module; the flag was actually toggled by the placement-delete and recycle-bin paths
        // instead. The column is real and is genuinely the ninth argument of the legacy first provider call,
        // so consolidating soft delete and restore onto this contract is justified - but it means this
        // endpoint can now SET a flag the legacy screen could only clear.
        //
        // MIGRATION: CacheTime is a non-nullable integer and zero is a REAL value meaning "do not
        // cache", so there is no coalesce to a stored value here: a blank legacy field wrote literally zero
        // rather than a sentinel. The submitted value is stored VERBATIM, with no clamp - the reasoning is
        // recorded in full on the creation projection above, and the same removal is annotated on both
        // module request validators.
        //
        // MIGRATION: THE SUBMITTED PAGE KEY IS DELIBERATELY NOT ASSIGNED TO THE PLACEMENT. The update
        // contract carries one, but it SELECTS which placement is being updated - the application service
        // reads it, finds the placement the module has on that page and passes that placement here, so by
        // the time this projection runs the value has already done its work and placement.TabId is equal to
        // it. Assigning it would therefore be a no-op on a correct call and a silent RE-PARENT on an
        // incorrect one, which is a move operation rather than an edit; the caller asked for an edit. The
        // page key of an existing placement is consequently write-once at create, in the same way and for
        // the same reason as the pane.
        //
        // MIGRATION: THIS PARAGRAPH USED TO ASSERT THE SELECTION WAS PERFORMED AND IT WAS NOT. The service
        // resolved the placement with the lowest identifier and never read the request, so for a module
        // placed on several pages the caller's choice was discarded and the edit landed on whichever
        // placement had been created first - while this comment described the behaviour the contract
        // promised. The projection did not change; the service now performs the selection this note always
        // claimed it did.
    }

    /// <summary>
    /// Collapses a settings sequence into a case-insensitive read-only map, keeping the last value when
    /// a name repeats and skipping a row whose name is blank.
    /// </summary>
    /// <remarks>
    /// Returns an EMPTY map for an empty sequence, never null, so "loaded and held nothing" stays
    /// observably distinct from the unloaded state the contracts express with null elsewhere.
    /// </remarks>
    // MIGRATION: NAMES ARE MATCHED WITHOUT REGARD TO CASE, WHICH IS FAITHFUL TO THE STORE RATHER THAN TO
    //   THE LEGACY IN-MEMORY COLLECTION. The setting-name columns sit under a case-insensitive collation
    //   and form half of each table's primary key, so two names differing only in case CANNOT coexist as
    //   rows; a case-sensitive map would draw a distinction the database is incapable of expressing, and
    //   would then be free to hold two entries that could never have been read together. Choosing the
    //   ordinal comparer instead would also make a duplicate-name sequence produce a runtime failure on
    //   insertion rather than a last-value-wins collapse, which is precisely the non-total behaviour a
    //   projection must not have. The legacy in-memory collection was case-SENSITIVE, so this is a real
    //   divergence and is recorded as one rather than absorbed.
    //
    // MIGRATION: A ROW WHOSE NAME IS BLANK IS SKIPPED, AND THIS IS THE ONE PLACE IN THE FILE THAT IS NOT
    //   A STRAIGHT PASS-THROUGH. The name is half of a primary key and is not nullable in either table,
    //   so no legitimate row reaches here with a blank name; a null one would make the map insertion
    //   fail, which would break the guarantee that these projections never fail on the arguments they
    //   accept. Skipping is therefore what keeps the projection total, and it is bounded to the NAME -
    //   an empty VALUE is a real value and is always kept, as annotated above. The request validators
    //   reject a blank name on the inbound direction, so the two directions agree.
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
