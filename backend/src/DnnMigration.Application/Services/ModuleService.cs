// MIGRATION: this service replaces the module half of Library/Components/Modules/ModuleController.vb
// (1,456 lines of Shared members) together with the business rules that lived in the three admin
// code-behinds Website/admin/Modules/{ModuleSettings,Export,Import}.ascx.vb. Every member below is an
// instance method reached through an injected interface, so the legacy static surface and its ambient
// HttpContext dependencies are gone.
//
// MIGRATION: the legacy hand-rolled row hydration is not reproduced. ModuleController.vb:L54 and
// L66-L72 instantiated an entity and then assigned each column through
// Convert.ToInt32(Null.SetNull(dr("Column"), currentValue)), one line per column. The EF Core
// materialiser behind the repository abstractions replaces that entirely, which is why no Fill,
// FillObject or CBO equivalent appears anywhere in this file.
//
// MIGRATION: the two late-bound activation sites at ModuleController.vb:L231 (content export) and
// L431 (content import) are replaced by IModuleBusinessControllerFactory. Nothing in this file loads
// an assembly, resolves a type from a string or constructs a type dynamically.
//
// MIGRATION: the legacy caching of a module's settings under the keys "GetModuleSettings<id>" and
// "GetTabModuleSettings<id>" (ModuleController.vb:L1241 and L1338, both expiring after
// 20 * PerformanceSetting minutes) is deliberately NOT reproduced, and the omission is recorded here
// rather than left to be discovered. The legacy cached a name/value Hashtable per store, whereas this
// service composes one ModuleSettingsDto that also carries mutable module and placement state; the
// two cannot be cached under the legacy keys without either caching a tracked entity graph belonging
// to a scoped unit of work or reading the rows twice. The definition catalogue, which is
// installation-time reference data, is cached instead.
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Services;

/// <summary>
/// Orchestrates the module aggregate: the catalogue of installed definitions, the module instances a
/// portal has created from them, the pages those instances are placed on, the two key/value settings
/// stores that hang off a module and off a placement, and the portable-content export and import pair.
/// </summary>
/// <remarks>
/// <para>
/// A module and its placement are separate records here. The legacy <c>ModuleInfo</c> class was a
/// flattened join over <c>dbo.Modules</c>, <c>dbo.TabModules</c>, <c>dbo.ModuleDefinitions</c> and
/// <c>dbo.ModuleControls</c>, which made "the module" and "the module on this page" indistinguishable.
/// Members that address a single placement therefore take a placement identifier, and members that
/// address the module itself do not.
/// </para>
/// <para>
/// Every multi-table write commits exactly once through <see cref="IUnitOfWork"/>, so a module can
/// never exist without the placement it was created with and a fan-out across pages can never be left
/// half applied.
/// </para>
/// </remarks>
public sealed class ModuleService : IModuleService
{
    /// <summary>
    /// Reported when the addressed portal does not exist.
    /// </summary>
    private const string PortalNotFoundCode = "module.portal_not_found";

    /// <summary>
    /// Reported when the submitted request is malformed in a way no validator caught.
    /// </summary>
    private const string RequestInvalidCode = "module.request_invalid";

    /// <summary>
    /// Reported when a placement is named that does not belong to the addressed module.
    /// </summary>
    private const string PlacementNotFoundCode = "module.placement_not_found";

    /// <summary>
    /// Reported when the addressed module does not exist in the portal.
    /// </summary>
    private const string NotFoundCode = "module.not_found";

    /// <summary>
    /// Reported when the named definition does not exist or is not available to the portal.
    /// </summary>
    private const string DefinitionNotFoundCode = "module.definition_not_found";

    /// <summary>
    /// Reported when the named page does not belong to the portal.
    /// </summary>
    private const string TabNotFoundCode = "module.tab_not_found";

    /// <summary>
    /// Reported when a submitted setting name or value cannot be stored.
    /// </summary>
    private const string SettingInvalidCode = "module.setting_invalid";

    /// <summary>
    /// Reported when the module cannot take part in a content export or import.
    /// </summary>
    private const string NotPortableCode = "module.not_portable";

    /// <summary>
    /// Reported when a submitted content document cannot be read.
    /// </summary>
    private const string ContentInvalidCode = "module.content_invalid";

    /// <summary>
    /// Carried on an otherwise successful update whose blast radius exceeded the addressed module.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy screen recorded nothing when a save named a portal default or propagated
    /// appearance, even though both touch records the operator never addressed. This project's
    /// Application layer declares no logging package - <c>DnnMigration.Application.csproj</c> carries
    /// FluentValidation and nothing else - so the audit record is this reason, which the API layer's
    /// request logging emits, rather than a log call made from here. Adding a logging dependency to
    /// this project would be the manifest drift the review already faulted once.
    /// </remarks>
    private const string WideEffectCode = "module.update.wide_effect";

    /// <summary>
    /// Maximum length of a setting name in both <c>dbo.ModuleSettings</c> and
    /// <c>dbo.TabModuleSettings</c>; both declare <c>SettingName nvarchar(50) NOT NULL</c>.
    /// </summary>
    private const int SettingNameMaximumLength = 50;

    /// <summary>
    /// Maximum length of <c>dbo.ModuleSettings.SettingValue</c>, declared <c>nvarchar(2000)</c> by the
    /// terminal schema.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <b>the width is 2000, and an earlier revision of this constant said 256.</b> The
    /// 01.00.00 create script did declare <c>nvarchar(256)</c> at line 353, but the later chain does
    /// widen it: <c>01.00.08.SqlDataProvider</c> lines 6248-6286 destroy and rebuild the whole table
    /// through a <c>Tmp_ModuleSettings</c> copy declaring <c>SettingValue nvarchar(2000) NOT NULL</c>
    /// at line 6256, and no subsequent script narrows it. The terminal writers agree - both
    /// <c>UpdateModuleSetting</c> (<c>01.00.08</c> line 6295) and, in templated form,
    /// <c>AddModuleSetting</c> and <c>UpdateModuleSetting</c> (<c>02.00.00</c> lines 4147 and 4171)
    /// declare <c>@SettingValue nvarchar(2000)</c>. Validating at 256 refused values the legacy
    /// application accepted and stored, which Minimal Change Clause item 3 forbids, so the two stores
    /// turn out to permit the same width rather than differing ones.
    /// </remarks>
    private const int ModuleSettingValueMaximumLength = 2000;

    /// <summary>
    /// Maximum length of <c>dbo.TabModuleSettings.SettingValue</c>, declared <c>nvarchar(2000)</c> by
    /// the 03.00.01 create script.
    /// </summary>
    private const int PlacementSettingValueMaximumLength = 2000;

    /// <summary>
    /// Friendly name of the module instance that holds a portal's own settings rows.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>PortalSettings.UpdateSiteSetting</c> (PortalSettings.vb:L970-L978) resolved this
    /// module by friendly name and then wrote an ordinary <c>dbo.ModuleSettings</c> row against it, so
    /// what the legacy screen called a "portal level" key has always been a module setting on a
    /// well-known module instance. There is no portal settings table to write to.
    /// </remarks>
    private const string SiteSettingsDefinitionName = "Site Settings";

    /// <summary>
    /// Setting name that records which module a portal opens by default.
    /// </summary>
    private const string DefaultModuleSettingName = "defaultmoduleid";

    /// <summary>
    /// Setting name that records which page a portal opens by default.
    /// </summary>
    private const string DefaultTabSettingName = "defaulttabid";

    /// <summary>
    /// Cache key holding a portal's projected definition catalogue.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this key is new rather than carried over. The legacy
    /// <c>DataCache.ModuleCacheKey</c> ("Modules{0}", DataCache.vb:L63) held a dictionary of module
    /// instances keyed by friendly name, which is a different payload from the definition catalogue
    /// projected here, so reusing that name would make two unrelated payloads collide.
    /// </remarks>
    private const string DefinitionCatalogueCacheKeyFormat = "ModuleDefinitions{0}";

    /// <summary>
    /// Base expiry of the definition catalogue, matching every module-related legacy timeout.
    /// </summary>
    private const int DefinitionCatalogueCacheTimeOutMinutes = 20;

    /// <summary>
    /// Element name of an exported content document.
    /// </summary>
    private const string ContentElementName = "content";

    /// <summary>
    /// Attribute naming the module type an exported document came from.
    /// </summary>
    private const string ContentTypeAttributeName = "type";

    /// <summary>
    /// Attribute naming the module version an exported document was produced by.
    /// </summary>
    private const string ContentVersionAttributeName = "version";

    /// <summary>
    /// Page size that requests every match unpaged, per the repository contracts.
    /// </summary>
    private const int UnpagedPageSize = 0;

    /// <summary>
    /// Attribution used when an import arrives without an authenticated caller.
    /// </summary>
    /// <remarks>
    /// <c>dbo.Users.UserID</c> is <c>IDENTITY(1,1)</c>, so no real account can own this value and it
    /// cannot be mistaken for one. Zero is not used because zero is a legitimate identifier elsewhere
    /// in this schema.
    /// </remarks>
    private const int UnattributedUserId = -1;

    private readonly IModuleRepository _modules;
    private readonly IModuleDefinitionRepository _definitions;
    private readonly ITabRepository _tabs;
    private readonly IPortalRepository _portals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IModuleBusinessControllerFactory _businessControllers;
    private readonly CachingOptions _caching;

    /// <summary>
    /// Initialises the service with the collaborators it reaches the store and the cache through.
    /// </summary>
    /// <param name="modules">Module, placement and settings persistence.</param>
    /// <param name="definitions">Definition catalogue and per-portal availability.</param>
    /// <param name="tabs">Page lookups, needed to validate placements and to fan out across pages.</param>
    /// <param name="portals">Portal existence and the administrative page identifier.</param>
    /// <param name="unitOfWork">The single commit point for every write below.</param>
    /// <param name="cache">Cache reads and invalidation.</param>
    /// <param name="currentUser">Attribution for a content import.</param>
    /// <param name="businessControllers">Resolution of a module's own portable-content contract.</param>
    /// <param name="caching">Bound caching configuration supplying the performance multiplier.</param>
    public ModuleService(
        IModuleRepository modules,
        IModuleDefinitionRepository definitions,
        ITabRepository tabs,
        IPortalRepository portals,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        ICurrentUser currentUser,
        IModuleBusinessControllerFactory businessControllers,
        CachingOptions caching)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _businessControllers = businessControllers ?? throw new ArgumentNullException(nameof(businessControllers));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One row is emitted per placement, so a module placed on four pages contributes four rows, each
    /// carrying its own placement identifier. A module with no placement at all contributes no row,
    /// which is what the legacy join-based read did too.
    /// </para>
    /// <para>
    /// The total count needs an explicit reconciliation. <see cref="IModuleRepository.ListAsync"/>
    /// pages over modules, so its total is a module count. When a page is named the two agree, because
    /// a module has at most one placement on any one page. When no page is named an unpaged read
    /// reports the exact placement count, because every row is present; a paged read reports the
    /// module total, which is the only figure obtainable without an unbounded read of every placement
    /// in the portal. That trade is recorded here rather than hidden.
    /// </para>
    /// <para>
    /// Ordering is fixed at page, then position within the page, then placement identifier. The
    /// repository contract declares no sort parameter, so a sort field on the request cannot be
    /// honoured; sorting the returned page in memory would order only the rows in hand and misreport
    /// the sequence, so it is deliberately not done.
    /// </para>
    /// </remarks>
    public async Task<Result<PagedResult<ModuleListItemDto>>> ListModulesAsync(
        int portalId,
        PagedRequest request,
        int? tabId = null,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidatePagedRequest(request) is ResultReason invalid)
        {
            return Result<PagedResult<ModuleListItemDto>>.Failure(invalid);
        }

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PagedResult<ModuleListItemDto>>.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        PagedResult<Module> page = await _modules.ListAsync(
            portalId,
            tabId,
            includeDeleted,
            request.PageIndex,
            request.PageSize,
            request.HasQuery ? request.Query : null,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<int, string> friendlyNames = page.Items.Count == 0
            ? new Dictionary<int, string>()
            : await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        var rows = new List<ModuleListItemDto>(page.Items.Count);
        foreach (Module module in page.Items)
        {
            IReadOnlyList<TabModule> placements =
                await _modules.ListPlacementsAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule placement in OrderPlacements(placements, tabId))
            {
                rows.Add(ModuleMappings.ToListItem(module, placement, ResolveFriendlyName(module, friendlyNames)));
            }
        }

        return Result<PagedResult<ModuleListItemDto>>.Success(
            request.PageSize == UnpagedPageSize
                ? PagedResult<ModuleListItemDto>.Unpaged(rows)
                : PagedResult<ModuleListItemDto>.Create(rows, page.TotalCount, page.PageIndex, page.PageSize));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A module the portal does not own, and a module with no placement to address, both read as an
    /// absence rather than a failure. Naming a placement that belongs to another module is a different
    /// matter and is reported, because the caller supplied an identifier that does not fit.
    /// </remarks>
    public async Task<Result<ModuleDetailDto?>> GetModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: true, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        Result<TabModule?> resolved = await ResolvePlacementAsync(
            module,
            tabModuleId,
            PlacementNotFoundCode,
            cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return Result<ModuleDetailDto?>.Failure(resolved.Error!);
        }

        if (resolved.Value is not TabModule placement)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        IReadOnlyDictionary<int, string> friendlyNames =
            await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<ModuleDetailDto?>.Success(
            ModuleMappings.ToDetail(module, placement, ResolveFriendlyName(module, friendlyNames)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The module and the placement it was created with are written as one unit of work, so neither can
    /// outlive the other. When the request asks for every page, one further placement is written per
    /// page in the same commit.
    /// </para>
    /// <para>
    /// "Every page" means every content page. The legacy screen fanned out over
    /// <c>PortalSettings.DesktopTabs</c> (ModuleSettings.ascx.vb:L410), which is the portal's non
    /// administrative page set, and an administrative page acquiring a content module was never a
    /// reachable outcome. The same classification the page service uses is applied here: a page is
    /// administrative when it is the portal's administration page or a child of it.
    /// </para>
    /// <para>
    /// A definition the portal cannot use is indistinguishable from one that does not exist, because
    /// the definition catalogue is already restricted to the definitions granted to the portal. That
    /// keeps a premium module in another tenant's grant list from being discovered by probing.
    /// </para>
    /// </remarks>
    public async Task<Result<ModuleDetailDto>> CreateModuleAsync(
        int portalId,
        CreateModuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateSchedule(request.StartDate, request.EndDate) is ResultReason invalidSchedule)
        {
            return Result<ModuleDetailDto>.Failure(invalidSchedule);
        }

        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        ModuleDefinition? definition = definitions
            .FirstOrDefault(candidate => candidate.ModuleDefinitionId == request.ModuleDefId);

        if (definition is null)
        {
            return Result<ModuleDetailDto>.Failure(
                DefinitionNotFoundCode,
                FormattableString.Invariant(
                    $"Module definition {request.ModuleDefId} does not exist or is not available to portal {portalId}."));
        }

        Tab? tab = await _tabs.GetAsync(request.TabId, cancellationToken).ConfigureAwait(false);
        if (tab is null || tab.PortalId != portalId)
        {
            return Result<ModuleDetailDto>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {request.TabId} does not belong to portal {portalId}."));
        }

        Module module = ModuleMappings.ToNewModule(portalId, request);
        TabModule placement = ModuleMappings.ToNewPlacement(request, definition.DefaultCacheTime);
        module.TabModules.Add(placement);

        var affectedTabIds = new HashSet<int> { placement.TabId };

        if (request.AllTabs)
        {
            foreach (Tab target in await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false))
            {
                if (target.TabId == placement.TabId)
                {
                    continue;
                }

                TabModule additional = ModuleMappings.ToNewPlacement(request, definition.DefaultCacheTime);
                additional.TabId = target.TabId;
                module.TabModules.Add(additional);
                affectedTabIds.Add(target.TabId);
            }
        }

        _modules.Add(module);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        return Result<ModuleDetailDto>.Success(
            ModuleMappings.ToDetail(module, placement, definition.FriendlyName));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This member carries the module's own change plus up to three wider effects, and all of them
    /// commit together.
    /// </para>
    /// <para>
    /// Naming the module as the portal default writes the two settings rows the legacy
    /// <c>UpdateSiteSetting</c> wrote, against the portal's own settings module instance. A portal
    /// without that instance is not a failure - no reason code is documented for it and refusing the
    /// whole save would be disproportionate - so the update succeeds and says so on the result.
    /// </para>
    /// <para>
    /// Propagating appearance copies alignment, colour, border, icon, visibility, container, and the
    /// three display switches onto every other module placement on a content page, exactly as
    /// ModuleController.UpdateModule did, while leaving each target's own position, pane and caching
    /// period alone.
    /// </para>
    /// <para>
    /// Switching the all-pages flag also moves placements, which the legacy screen performed after the
    /// module row was written, with the comment that the controller assumes every module update has
    /// already been carried out. Turning it on places the module on the content pages it is missing
    /// from; turning it off removes every placement other than the addressed one, together with that
    /// placement's own settings.
    /// </para>
    /// </remarks>
    public async Task<Result<ModuleDetailDto?>> UpdateModuleAsync(
        int portalId,
        int moduleId,
        UpdateModuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateSchedule(request.StartDate, request.EndDate) is ResultReason invalidSchedule)
        {
            return Result<ModuleDetailDto?>.Failure(invalidSchedule);
        }

        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: true, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        Result<TabModule?> resolved = await ResolvePlacementAsync(
            module,
            tabModuleId: null,
            mismatchCode: null,
            cancellationToken).ConfigureAwait(false);

        if (resolved.Value is not TabModule placement)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        bool wasPlacedEverywhere = module.AllTabs;
        ModuleMappings.ApplyUpdate(module, placement, request);

        var affectedTabIds = new HashSet<int> { placement.TabId };
        var effects = new List<string>();

        if (!wasPlacedEverywhere && module.AllTabs)
        {
            int added = await PlaceOnContentTabsAsync(portalId, module, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            if (added > 0)
            {
                effects.Add(FormattableString.Invariant($"placed on {added} further page(s)"));
            }
        }
        else if (wasPlacedEverywhere && !module.AllTabs)
        {
            int removed = await WithdrawFromOtherTabsAsync(module, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            if (removed > 0)
            {
                effects.Add(FormattableString.Invariant($"withdrawn from {removed} further page(s)"));
            }
        }

        if (request.IsDefaultModule)
        {
            bool recorded = await NameAsPortalDefaultAsync(
                portalId,
                module.ModuleId,
                placement.TabId,
                cancellationToken).ConfigureAwait(false);

            effects.Add(recorded
                ? "named as the portal default module"
                : FormattableString.Invariant(
                    $"could not be named as the portal default because portal {portalId} has no \"{SiteSettingsDefinitionName}\" module instance"));
        }

        if (request.AllModules)
        {
            int copied = await PropagateAppearanceAsync(portalId, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            effects.Add(FormattableString.Invariant($"appearance copied to {copied} placement(s) on content pages"));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        IReadOnlyDictionary<int, string> friendlyNames =
            await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        ModuleDetailDto detail =
            ModuleMappings.ToDetail(module, placement, ResolveFriendlyName(module, friendlyNames));

        return effects.Count == 0
            ? Result<ModuleDetailDto?>.Success(detail)
            : Result<ModuleDetailDto?>.Success(
                detail,
                new ResultReason(WideEffectCode, string.Join("; ", effects) + "."));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Recycling the module is a soft delete through <c>dbo.Modules.IsDeleted</c>, a
    /// <c>bit NOT NULL</c> column added by the 02.00.00 upgrade script with a default of zero, so the
    /// module and every placement it has survive and can be restored. Removing one placement is a hard
    /// delete of that placement row and of its placement-scoped settings, and leaves the module and its
    /// other placements untouched. Recycling a module that is already recycled succeeds, because a
    /// delete is idempotent.
    /// </remarks>
    public async Task<Result> DeleteModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: true, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        var affectedTabIds = new HashSet<int>();

        if (tabModuleId is int addressed)
        {
            TabModule? placement = await _modules.GetPlacementAsync(addressed, cancellationToken).ConfigureAwait(false);
            if (placement is null || placement.ModuleId != moduleId)
            {
                return Result.Failure(
                    PlacementNotFoundCode,
                    FormattableString.Invariant($"Placement {addressed} does not belong to module {moduleId}."));
            }

            await RemovePlacementAsync(placement, cancellationToken).ConfigureAwait(false);
            affectedTabIds.Add(placement.TabId);
        }
        else
        {
            module.IsDeleted = true;

            IReadOnlyList<TabModule> placements =
                await _modules.ListPlacementsAsync(moduleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule placement in placements)
            {
                affectedTabIds.Add(placement.TabId);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both settings stores are carried: the module-scoped rows, which every placement of the module
    /// shares, and the rows scoped to the addressed placement alone. Anything that cannot be addressed
    /// reads as an absence, because no reason code is documented for this member.
    /// </remarks>
    public async Task<Result<ModuleSettingsDto?>> GetModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: true, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<ModuleSettingsDto?>.Success(null);
        }

        Result<TabModule?> resolved = await ResolvePlacementAsync(
            module,
            tabModuleId,
            mismatchCode: null,
            cancellationToken).ConfigureAwait(false);

        if (resolved.Value is not TabModule placement)
        {
            return Result<ModuleSettingsDto?>.Success(null);
        }

        IReadOnlyList<ModuleSetting> moduleSettings =
            await _modules.ListSettingsAsync(moduleId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TabModuleSetting> placementSettings =
            await _modules.ListPlacementSettingsAsync(placement.TabModuleId, cancellationToken).ConfigureAwait(false);

        return Result<ModuleSettingsDto?>.Success(
            ModuleMappings.ToSettings(module, placement, moduleSettings, placementSettings));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is a replace-the-set write, not an add, update and delete triad: the caller submits the
    /// whole desired state of each store, the difference against what is stored is computed here, and
    /// the whole difference commits once. A name the caller omitted is therefore deleted.
    /// </para>
    /// <para>
    /// Names are matched without regard to case, which is how the legacy <c>Hashtable</c> behaved and
    /// how the settings projection behaves. When two submitted names differ only in case the last one
    /// wins, for the same reason.
    /// </para>
    /// <para>
    /// Placement-scoped work needs a placement. When none is addressed the module's original placement
    /// is used; when the module has no placement at all and placement-scoped settings were nevertheless
    /// submitted, that is reported rather than silently dropped. Submitting no placement-scoped
    /// settings and naming no placement leaves the placement store alone, because there is no way to
    /// tell which store the caller meant to empty.
    /// </para>
    /// </remarks>
    public async Task<Result> UpdateModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId,
        IReadOnlyDictionary<string, string> moduleSettings,
        IReadOnlyDictionary<string, string> tabModuleSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSettings);
        ArgumentNullException.ThrowIfNull(tabModuleSettings);

        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: true, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        if (TryNormaliseSettings(
                moduleSettings,
                ModuleSettingValueMaximumLength,
                "module",
                out Dictionary<string, string> desiredModuleSettings) is ResultReason moduleSettingInvalid)
        {
            return Result.Failure(moduleSettingInvalid);
        }

        if (TryNormaliseSettings(
                tabModuleSettings,
                PlacementSettingValueMaximumLength,
                "placement",
                out Dictionary<string, string> desiredPlacementSettings) is ResultReason placementSettingInvalid)
        {
            return Result.Failure(placementSettingInvalid);
        }

        TabModule? placement = null;
        if (tabModuleId is int addressed)
        {
            placement = await _modules.GetPlacementAsync(addressed, cancellationToken).ConfigureAwait(false);
            if (placement is null || placement.ModuleId != moduleId)
            {
                return Result.Failure(
                    NotFoundCode,
                    FormattableString.Invariant($"Placement {addressed} does not belong to module {moduleId}."));
            }
        }
        else
        {
            // No placement was addressed, so the default placement is resolved - the same one a read without
            // an addressed placement projects. Resolving it unconditionally rather than only when the caller
            // submitted something matters: this endpoint's contract is that the submitted set IS the whole
            // set, so an empty set has to be able to clear what is stored. Resolving only when the set was
            // non-empty made a clearing request silently do nothing at the placement scope while doing exactly
            // what it said at the module scope, which is the one shape of request under which the two scopes
            // disagreed.
            Result<TabModule?> resolved = await ResolvePlacementAsync(
                module,
                tabModuleId: null,
                mismatchCode: null,
                cancellationToken).ConfigureAwait(false);

            placement = resolved.Value;

            // A module with no placement at all still cannot hold placement-scoped settings, and a caller that
            // asked to store some is told so rather than having the request quietly succeed. A caller that
            // submitted none has nothing to be told: there is simply nothing to reconcile.
            if (placement is null && desiredPlacementSettings.Count > 0)
            {
                return Result.Failure(
                    SettingInvalidCode,
                    FormattableString.Invariant(
                        $"Module {moduleId} is not placed on any page, so placement-scoped settings cannot be stored."));
            }
        }

        IReadOnlyList<ModuleSetting> storedModuleSettings =
            await _modules.ListSettingsAsync(moduleId, cancellationToken).ConfigureAwait(false);

        var survivingModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleSetting stored in storedModuleSettings)
        {
            if (!desiredModuleSettings.TryGetValue(stored.SettingName, out string? desired))
            {
                _modules.RemoveSetting(stored);
                continue;
            }

            survivingModuleNames.Add(stored.SettingName);
            if (!string.Equals(stored.SettingValue, desired, StringComparison.Ordinal))
            {
                stored.SettingValue = desired;
            }
        }

        foreach (KeyValuePair<string, string> desired in desiredModuleSettings)
        {
            if (survivingModuleNames.Contains(desired.Key))
            {
                continue;
            }

            _modules.AddSetting(new ModuleSetting
            {
                ModuleId = moduleId,
                SettingName = desired.Key,
                SettingValue = desired.Value,
            });
        }

        if (placement is not null)
        {
            IReadOnlyList<TabModuleSetting> storedPlacementSettings = await _modules
                .ListPlacementSettingsAsync(placement.TabModuleId, cancellationToken)
                .ConfigureAwait(false);

            var survivingPlacementNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TabModuleSetting stored in storedPlacementSettings)
            {
                if (!desiredPlacementSettings.TryGetValue(stored.SettingName, out string? desired))
                {
                    _modules.RemovePlacementSetting(stored);
                    continue;
                }

                survivingPlacementNames.Add(stored.SettingName);
                if (!string.Equals(stored.SettingValue, desired, StringComparison.Ordinal))
                {
                    stored.SettingValue = desired;
                }
            }

            foreach (KeyValuePair<string, string> desired in desiredPlacementSettings)
            {
                if (survivingPlacementNames.Contains(desired.Key))
                {
                    continue;
                }

                _modules.AddPlacementSetting(new TabModuleSetting
                {
                    TabModuleId = placement.TabModuleId,
                    SettingName = desired.Key,
                    SettingValue = desired.Value,
                });
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (placement is not null)
        {
            _cache.InvalidateModules(placement.TabId);
        }
        else
        {
            IReadOnlyList<TabModule> placements =
                await _modules.ListPlacementsAsync(moduleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule affected in placements)
            {
                _cache.InvalidateModules(affected.TabId);
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The catalogue is installation-time reference data, so it is read through the cache and no member
    /// of this service creates or modifies a definition. A portal with no definitions available to it
    /// reads as an empty catalogue rather than a failure.
    /// </remarks>
    public async Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListModuleDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = string.Format(CultureInfo.InvariantCulture, DefinitionCatalogueCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(
            DefinitionCatalogueCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: when the configured expiry resolves to zero the legacy callers skipped the
        // database read outright, on the grounds that the query was too expensive to repeat per
        // request. That is not reproduced: disabling caching here disables caching only, and the read
        // still runs, because returning nothing would make a configuration value silently change what
        // the endpoint reports.
        IReadOnlyList<ModuleDefinitionDto> catalogue = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadDefinitionCatalogueAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadDefinitionCatalogueAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<ModuleDefinitionDto>>.Success(catalogue);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Portability is decided by the stored capability bit field, which is what the legacy export path
    /// tested: <c>objModule.BusinessControllerClass &lt;&gt; "" And objModule.IsPortable</c>
    /// (Export.ascx.vb:L150). A module whose stored bit says portable but whose controller no
    /// registration covers is reported as not portable too, because in this deployment there is no way
    /// to ask it for content.
    /// </para>
    /// <para>
    /// MIGRATION: the document is returned to the caller instead of being written to a server path.
    /// The legacy screen wrote it beneath the portal home directory under the web root and then
    /// registered it as a portal file; neither the web root nor that file registry exists in the target
    /// container topology. The requested folder is accepted and deliberately unused for that reason,
    /// and the requested file name is validated so the caller can label what it receives.
    /// </para>
    /// <para>
    /// MIGRATION: the payload is escaped by an XML writer rather than by the legacy pair of
    /// <c>Server.HtmlEncode</c> followed by a CDATA wrap. The business-controller contract states that
    /// the payload crosses it as an opaque string and that escaping is the caller's concern, and this
    /// service is that caller. An empty payload still yields a document, because "asked and given
    /// nothing" is a real answer that the caller is entitled to see.
    /// </para>
    /// </remarks>
    public async Task<Result<string>> ExportModuleAsync(
        int portalId,
        int moduleId,
        ModuleExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Result<string>.Failure(
                RequestInvalidCode,
                "A file name is required so the returned document can be labelled.");
        }

        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: false, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<string>.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package is null || string.IsNullOrWhiteSpace(package.BusinessControllerClass) || !package.IsPortable)
        {
            return Result<string>.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {moduleId} does not support content export."));
        }

        Result<string?> exported = await _businessControllers
            .ExportModuleContentAsync(package.BusinessControllerClass, moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (exported.IsFailure)
        {
            return Result<string>.Failure(exported.Error!);
        }

        if (exported.Value is null)
        {
            string advisory = exported.Reason?.Message
                ?? "no registered business controller covers this module.";

            return Result<string>.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {moduleId} could not be asked for content: {advisory}"));
        }

        var document = new XElement(
            ContentElementName,
            new XAttribute(ContentTypeAttributeName, package.ModuleName),
            new XAttribute(ContentVersionAttributeName, package.Version ?? string.Empty),
            exported.Value);

        return Result<string>.Success(document.ToString(SaveOptions.DisableFormatting));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The whole import is one unit of work. The target module is named in the body because the
    /// endpoint carries no identifier in its route, and the portal argument is what stops a
    /// body-supplied identifier from reaching another tenant's module.
    /// </para>
    /// <para>
    /// The document is read the way this service writes it: the version attribute names the version the
    /// content was produced by, falling back to the installed version when the document omits it, and
    /// the element's content is the payload. A payload that is itself markup is handed on as markup; a
    /// payload that is text is unescaped once, which round-trips an export exactly.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy path swallowed every exception the module's own import code raised and
    /// carried on as though the content had been stored. That is a defect rather than a rule, so a
    /// failed import is reported here and nothing is committed.
    /// </para>
    /// </remarks>
    public async Task<Result> ImportModuleAsync(
        int portalId,
        ModuleImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Module? module = await _modules
            .GetAsync(request.ModuleId, includePlacements: false, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {request.ModuleId} does not exist in portal {portalId}."));
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Result.Failure(ContentInvalidCode, "The submitted document is empty.");
        }

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package is null || string.IsNullOrWhiteSpace(package.BusinessControllerClass) || !package.IsPortable)
        {
            return Result.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {request.ModuleId} does not support content import."));
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(request.Content);
        }
        catch (XmlException exception)
        {
            return Result.Failure(
                ContentInvalidCode,
                FormattableString.Invariant($"The submitted document is not well-formed XML: {exception.Message}"));
        }

        XElement? root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, ContentElementName, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(
                ContentInvalidCode,
                FormattableString.Invariant($"The submitted document must have a <{ContentElementName}> root element."));
        }

        string payload = root.HasElements
            ? string.Concat(root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
            : root.Value;

        string? version = root.Attribute(ContentVersionAttributeName)?.Value;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = package.Version;
        }

        Result imported = await _businessControllers.ImportModuleContentAsync(
            package.BusinessControllerClass,
            module.ModuleId,
            payload,
            version,
            _currentUser.UserId ?? UnattributedUserId,
            cancellationToken).ConfigureAwait(false);

        if (imported.IsFailure)
        {
            return Result.Failure(imported.Error!);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TabModule> placements =
            await _modules.ListPlacementsAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        foreach (TabModule placement in placements)
        {
            _cache.InvalidateModules(placement.TabId);
        }

        return imported.Reason is ResultReason advisory ? Result.Success(advisory) : Result.Success();
    }

    /// <summary>
    /// Rejects a paging request whose bounds no validator can have accepted.
    /// </summary>
    /// <param name="request">The submitted paging request.</param>
    /// <returns>The reason the request is unusable, or <see langword="null"/> when it is usable.</returns>
    private static ResultReason? ValidatePagedRequest(PagedRequest request)
    {
        if (request.PageIndex < 0)
        {
            return new ResultReason(RequestInvalidCode, "The page index must not be negative.");
        }

        if (request.PageSize < 0)
        {
            return new ResultReason(RequestInvalidCode, "The page size must not be negative.");
        }

        if (request.PageSize > PagedRequestValidator.MaximumPageSize)
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant(
                    $"The page size must not exceed {PagedRequestValidator.MaximumPageSize}."));
        }

        if (request.Query is not null && request.Query.Length > PagedRequestValidator.QueryMaximumLength)
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant(
                    $"The search text must not exceed {PagedRequestValidator.QueryMaximumLength} characters."));
        }

        return null;
    }

    /// <summary>
    /// Rejects a schedule whose end precedes its start.
    /// </summary>
    /// <param name="startDate">The submitted start of the display window.</param>
    /// <param name="endDate">The submitted end of the display window.</param>
    /// <returns>The reason the schedule is unusable, or <see langword="null"/> when it is usable.</returns>
    private static ResultReason? ValidateSchedule(DateTime? startDate, DateTime? endDate)
        => startDate is not null && endDate is not null && startDate > endDate
            ? new ResultReason(RequestInvalidCode, "The start date must not be later than the end date.")
            : null;

    /// <summary>
    /// Normalises a submitted settings map, rejecting anything the columns cannot hold.
    /// </summary>
    /// <param name="submitted">The desired state of one settings store.</param>
    /// <param name="valueMaximumLength">Maximum storable value length for that store.</param>
    /// <param name="scope">Word naming the store, used in the reported message.</param>
    /// <param name="normalised">The case-insensitive map to write, when the submission is usable.</param>
    /// <returns>The reason the submission is unusable, or <see langword="null"/> when it is usable.</returns>
    private static ResultReason? TryNormaliseSettings(
        IReadOnlyDictionary<string, string> submitted,
        int valueMaximumLength,
        string scope,
        out Dictionary<string, string> normalised)
    {
        normalised = new Dictionary<string, string>(submitted.Count, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> pair in submitted)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                normalised.Clear();
                return new ResultReason(
                    SettingInvalidCode,
                    FormattableString.Invariant($"A {scope} setting name must not be blank."));
            }

            if (pair.Key.Length > SettingNameMaximumLength)
            {
                normalised.Clear();
                return new ResultReason(
                    SettingInvalidCode,
                    FormattableString.Invariant(
                        $"The {scope} setting name \"{pair.Key}\" exceeds {SettingNameMaximumLength} characters."));
            }

            string value = pair.Value ?? string.Empty;
            if (value.Length > valueMaximumLength)
            {
                normalised.Clear();
                return new ResultReason(
                    SettingInvalidCode,
                    FormattableString.Invariant(
                        $"The value of the {scope} setting \"{pair.Key}\" exceeds {valueMaximumLength} characters."));
            }

            normalised[pair.Key] = value;
        }

        return null;
    }

    /// <summary>
    /// Resolves the placement a member addresses.
    /// </summary>
    /// <param name="module">The module whose placement is wanted.</param>
    /// <param name="tabModuleId">The named placement, or <see langword="null"/> for the original one.</param>
    /// <param name="mismatchCode">
    /// Reason code to report when a named placement belongs to another module, or
    /// <see langword="null"/> when the caller documents no such code and that case reads as an absence.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The placement, an absence, or the reason the named placement does not fit.</returns>
    /// <remarks>
    /// With no placement named, the module's original placement is used, identified as the one with the
    /// lowest placement identifier. That is deterministic and stable, which matters because two members
    /// address a module without carrying a placement identifier at all.
    /// </remarks>
    private async Task<Result<TabModule?>> ResolvePlacementAsync(
        Module module,
        int? tabModuleId,
        string? mismatchCode,
        CancellationToken cancellationToken)
    {
        if (tabModuleId is int addressed)
        {
            TabModule? named = await _modules.GetPlacementAsync(addressed, cancellationToken).ConfigureAwait(false);
            if (named is not null && named.ModuleId == module.ModuleId)
            {
                return Result<TabModule?>.Success(named);
            }

            return mismatchCode is null
                ? Result<TabModule?>.Success(null)
                : Result<TabModule?>.Failure(
                    mismatchCode,
                    FormattableString.Invariant(
                        $"Placement {addressed} does not belong to module {module.ModuleId}."));
        }

        IReadOnlyList<TabModule> placements = module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules.ListPlacementsAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        return Result<TabModule?>.Success(
            placements.OrderBy(candidate => candidate.TabModuleId).FirstOrDefault());
    }

    /// <summary>
    /// Orders a module's placements deterministically, optionally narrowed to one page.
    /// </summary>
    /// <param name="placements">The module's placements.</param>
    /// <param name="tabId">The page to narrow to, or <see langword="null"/> for every page.</param>
    /// <returns>The placements in page, position and identifier order.</returns>
    private static IEnumerable<TabModule> OrderPlacements(IReadOnlyList<TabModule> placements, int? tabId)
        => placements
            .Where(candidate => tabId is null || candidate.TabId == tabId.Value)
            .OrderBy(candidate => candidate.TabId)
            .ThenBy(candidate => candidate.ModuleOrder)
            .ThenBy(candidate => candidate.TabModuleId);

    /// <summary>
    /// Reads a portal's definition friendly names, keyed by definition identifier.
    /// </summary>
    /// <param name="portalId">The portal whose catalogue is read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Friendly names by definition identifier.</returns>
    private async Task<IReadOnlyDictionary<int, string>> ReadDefinitionNamesAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        var names = new Dictionary<int, string>(definitions.Count);
        foreach (ModuleDefinition definition in definitions)
        {
            names[definition.ModuleDefinitionId] = definition.FriendlyName;
        }

        return names;
    }

    /// <summary>
    /// Picks the friendly name to project for a module.
    /// </summary>
    /// <param name="module">The module being projected.</param>
    /// <param name="friendlyNames">Names read for the portal's catalogue.</param>
    /// <returns>The definition's friendly name, or <see langword="null"/> when it cannot be resolved.</returns>
    /// <remarks>
    /// The module repository documents no eager loading of the definition, which is why the name is
    /// looked up from the catalogue first and the navigation is only a fallback.
    /// </remarks>
    private static string? ResolveFriendlyName(Module module, IReadOnlyDictionary<int, string> friendlyNames)
        => friendlyNames.TryGetValue(module.ModuleDefinitionId, out string? name)
            ? name
            : module.ModuleDefinition?.FriendlyName;

    /// <summary>
    /// Projects a portal's available definitions into the catalogue contract.
    /// </summary>
    /// <param name="portalId">The portal whose grants restrict the catalogue.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The catalogue in a stable order.</returns>
    private async Task<IReadOnlyList<ModuleDefinitionDto>> ReadDefinitionCatalogueAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        var packages = new Dictionary<int, DesktopModule?>();
        var catalogue = new List<ModuleDefinitionDto>(definitions.Count);

        foreach (ModuleDefinition definition in definitions
            .OrderBy(candidate => candidate.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ModuleDefinitionId))
        {
            if (!packages.TryGetValue(definition.DesktopModuleId, out DesktopModule? package))
            {
                package = await _definitions
                    .GetDesktopModuleByIdAsync(definition.DesktopModuleId, cancellationToken)
                    .ConfigureAwait(false);

                packages[definition.DesktopModuleId] = package;
            }

            catalogue.Add(ModuleMappings.ToDto(definition, package));
        }

        return catalogue;
    }

    /// <summary>
    /// Reads the installed package a module's definition belongs to.
    /// </summary>
    /// <param name="module">The module whose package is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The package, or <see langword="null"/> when it cannot be resolved.</returns>
    private async Task<DesktopModule?> ReadPackageAsync(Module module, CancellationToken cancellationToken)
    {
        ModuleDefinition? definition = await _definitions
            .GetModuleDefinitionByIdAsync(module.ModuleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (definition is null)
        {
            return null;
        }

        return await _definitions
            .GetDesktopModuleByIdAsync(definition.DesktopModuleId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the portal's content pages, that is every page that is not administrative.
    /// </summary>
    /// <param name="portalId">The portal whose pages are read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The portal's content pages.</returns>
    private async Task<IReadOnlyList<Tab>> ReadContentTabsAsync(int portalId, CancellationToken cancellationToken)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Tab> tabs = await _tabs
            .ListAsync(portalId, includeDeleted: false, cancellationToken)
            .ConfigureAwait(false);

        int? adminTabId = portal?.AdminTabId;
        return tabs.Where(tab => !IsAdministrative(tab, adminTabId)).ToList();
    }

    /// <summary>
    /// Classifies a page as administrative.
    /// </summary>
    /// <param name="tab">The page to classify.</param>
    /// <param name="adminTabId">The portal's administration page, when it has one.</param>
    /// <returns><see langword="true"/> when the page belongs to the administrative band.</returns>
    /// <remarks>
    /// This reproduces <c>TabInfo.IsAdminTab</c> (TabInfo.vb:L434-L464) for a portal-scoped page: the
    /// administration page itself and its immediate children. The host band the legacy property also
    /// covered cannot arise here, because every page read belongs to one portal.
    /// </remarks>
    private static bool IsAdministrative(Tab tab, int? adminTabId)
        => adminTabId is int administrationTabId
            && (tab.TabId == administrationTabId || tab.ParentId == administrationTabId);

    /// <summary>
    /// Places a module on every content page it is missing from.
    /// </summary>
    /// <param name="portalId">The portal being fanned out across.</param>
    /// <param name="module">The module being placed.</param>
    /// <param name="template">The placement whose settings the new placements copy.</param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements added.</returns>
    private async Task<int> PlaceOnContentTabsAsync(
        int portalId,
        Module module,
        TabModule template,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> existing =
            await _modules.ListPlacementsAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        var placed = existing.Select(placement => placement.TabId).ToHashSet();
        int added = 0;

        foreach (Tab target in await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            if (!placed.Add(target.TabId))
            {
                continue;
            }

            _modules.AddPlacement(new TabModule
            {
                TabId = target.TabId,
                ModuleId = module.ModuleId,
                PaneName = template.PaneName,
                ModuleOrder = template.ModuleOrder,
                CacheTime = template.CacheTime,
                Alignment = template.Alignment,
                Color = template.Color,
                Border = template.Border,
                IconFile = template.IconFile,
                Visibility = template.Visibility,
                ContainerSrc = template.ContainerSrc,
                DisplayTitle = template.DisplayTitle,
                DisplayPrint = template.DisplayPrint,
                DisplaySyndicate = template.DisplaySyndicate,
            });

            affectedTabIds.Add(target.TabId);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Removes every placement of a module other than the one being kept.
    /// </summary>
    /// <param name="module">The module being withdrawn.</param>
    /// <param name="kept">The placement that survives.</param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements removed.</returns>
    private async Task<int> WithdrawFromOtherTabsAsync(
        Module module,
        TabModule kept,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> existing =
            await _modules.ListPlacementsAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        int removed = 0;
        foreach (TabModule stale in existing.Where(placement => placement.TabModuleId != kept.TabModuleId))
        {
            await RemovePlacementAsync(stale, cancellationToken).ConfigureAwait(false);
            affectedTabIds.Add(stale.TabId);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Removes one placement together with the settings scoped to it.
    /// </summary>
    /// <param name="placement">The placement to remove.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    /// <remarks>
    /// The settings rows are removed explicitly rather than left to the cascade the schema declares,
    /// so the outcome does not depend on which provider the request is served by.
    /// </remarks>
    private async Task RemovePlacementAsync(TabModule placement, CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModuleSetting> settings = await _modules
            .ListPlacementSettingsAsync(placement.TabModuleId, cancellationToken)
            .ConfigureAwait(false);

        foreach (TabModuleSetting setting in settings)
        {
            _modules.RemovePlacementSetting(setting);
        }

        _modules.RemovePlacement(placement);
    }

    /// <summary>
    /// Records a module and its page as the portal's default pair.
    /// </summary>
    /// <param name="portalId">The portal whose default is being set.</param>
    /// <param name="moduleId">The module becoming the default.</param>
    /// <param name="tabId">The page the default module sits on.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when the pair was recorded.</returns>
    /// <remarks>
    /// The two rows are written against the portal's own settings module instance, resolved by friendly
    /// name, which is exactly where <c>PortalSettings.UpdateSiteSetting</c> wrote them. A portal that
    /// has no such instance cannot hold the keys, and the caller reports that on the result rather than
    /// failing the whole save.
    /// </remarks>
    private async Task<bool> NameAsPortalDefaultAsync(
        int portalId,
        int moduleId,
        int tabId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        ModuleDefinition? siteSettings = definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.FriendlyName, SiteSettingsDefinitionName, StringComparison.OrdinalIgnoreCase));

        if (siteSettings is null)
        {
            return false;
        }

        PagedResult<Module> instances = await _modules.ListAsync(
            portalId,
            tabId: null,
            includeDeleted: false,
            pageIndex: 0,
            pageSize: UnpagedPageSize,
            titleFilter: null,
            cancellationToken).ConfigureAwait(false);

        Module? host = instances.Items.FirstOrDefault(candidate =>
            candidate.ModuleDefinitionId == siteSettings.ModuleDefinitionId);

        if (host is null)
        {
            return false;
        }

        IReadOnlyList<ModuleSetting> stored =
            await _modules.ListSettingsAsync(host.ModuleId, cancellationToken).ConfigureAwait(false);

        UpsertSetting(stored, host.ModuleId, DefaultModuleSettingName, moduleId.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(stored, host.ModuleId, DefaultTabSettingName, tabId.ToString(CultureInfo.InvariantCulture));

        return true;
    }

    /// <summary>
    /// Writes one module setting, updating the stored row when it already exists.
    /// </summary>
    /// <param name="stored">The rows already held against the module.</param>
    /// <param name="moduleId">The module the setting belongs to.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="settingValue">The value to store.</param>
    private void UpsertSetting(
        IReadOnlyList<ModuleSetting> stored,
        int moduleId,
        string settingName,
        string settingValue)
    {
        ModuleSetting? existing = stored.FirstOrDefault(candidate =>
            string.Equals(candidate.SettingName, settingName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.SettingValue = settingValue;
            return;
        }

        _modules.AddSetting(new ModuleSetting
        {
            ModuleId = moduleId,
            SettingName = settingName,
            SettingValue = settingValue,
        });
    }

    /// <summary>
    /// Copies one placement's appearance onto every other placement on the portal's content pages.
    /// </summary>
    /// <param name="portalId">The portal being propagated across.</param>
    /// <param name="source">The placement whose appearance is authoritative.</param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements changed.</returns>
    /// <remarks>
    /// Only the appearance members travel. Each target keeps its own position within its pane, its pane
    /// and its caching period, which is what ModuleController.UpdateModule preserved when it fanned the
    /// same nine members out.
    /// </remarks>
    private async Task<int> PropagateAppearanceAsync(
        int portalId,
        TabModule source,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Tab> contentTabs = await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false);
        var contentTabIds = contentTabs.Select(tab => tab.TabId).ToHashSet();

        PagedResult<Module> instances = await _modules.ListAsync(
            portalId,
            tabId: null,
            includeDeleted: false,
            pageIndex: 0,
            pageSize: UnpagedPageSize,
            titleFilter: null,
            cancellationToken).ConfigureAwait(false);

        int copied = 0;
        foreach (Module candidate in instances.Items)
        {
            IReadOnlyList<TabModule> placements =
                await _modules.ListPlacementsAsync(candidate.ModuleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule target in placements)
            {
                if (target.TabModuleId == source.TabModuleId || !contentTabIds.Contains(target.TabId))
                {
                    continue;
                }

                target.Alignment = source.Alignment;
                target.Color = source.Color;
                target.Border = source.Border;
                target.IconFile = source.IconFile;
                target.Visibility = source.Visibility;
                target.ContainerSrc = source.ContainerSrc;
                target.DisplayTitle = source.DisplayTitle;
                target.DisplayPrint = source.DisplayPrint;
                target.DisplaySyndicate = source.DisplaySyndicate;

                affectedTabIds.Add(target.TabId);
                copied++;
            }
        }

        return copied;
    }

    /// <summary>
    /// Drops the cached module set of every page a write touched.
    /// </summary>
    /// <param name="tabIds">The pages whose caches are stale.</param>
    /// <remarks>
    /// This is the target form of the legacy <c>ClearCache(TabId)</c> call that closed every module
    /// write. The legacy call also dropped that page's module permission cache; nothing here writes a
    /// permission row, so that eviction is deliberately not repeated.
    /// </remarks>
    private void InvalidatePlacements(IEnumerable<int> tabIds)
    {
        foreach (int tabId in tabIds)
        {
            _cache.InvalidateModules(tabId);
        }
    }
}
