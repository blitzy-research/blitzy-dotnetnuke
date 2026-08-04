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
// an assembly, resolves a type from a string or constructs a type dynamically. These are ORDINARY
// .NET REFLECTION sites, and the three further sites in EventMessageProcessor.vb (L32, L52, L77) are
// too: the exclusion covering VB6 and ActiveX activation is vacuous against this codebase, so nothing
// here should be read as removing that kind of interop, because there was never any to remove.
//
// MIGRATION: the two - not one - ambient System.Web calls this file replaces are named here together,
// because they are one pipeline seen from its two ends and were measured as the only System.Web
// dependencies in the 1,456-line source (`grep -n HttpContext` returns exactly L244 and L428):
//
//     L244  Content    = HttpContext.Current.Server.HtmlEncode(Content)     ' EXPORT
//     L428  strcontent = HttpContext.Current.Server.HtmlDecode(strcontent)  ' IMPORT
//
// Both become System.Net.WebUtility calls at the two sites below, so this layer takes no ASP.NET
// dependency while the escaping behaviour a stored document was produced under is preserved exactly.
// WebUtility is the framework's own successor to HttpServerUtility for this pair and is reachable from
// a plain class library, which HttpUtility historically was not.
//
// MIGRATION: reported and not corrected - the migration plan describes ModuleInfo.vb as 58 properties,
// whereas the measured count of `Public Property` declarations in that 936-line class is 54. The
// measurement stands; nothing in this file depends on the figure, because the flattened class is
// consumed here only through the four entities it was split into.
//
// MIGRATION: two collaborators the plan names are deliberately NOT injected, and the reasons are
// recorded so their absence is not read as an oversight.
//   - ILogger<ModuleService> and IOptions<T> CANNOT be named in this project. Its manifest declares
//     exactly two packages, both FluentValidation, and neither Microsoft.Extensions.Logging.Abstractions
//     nor Microsoft.Extensions.Options is in the reference pack a class library targets or is supplied
//     transitively, so either generic fails to compile with CS0234 and CS0246. The dependency is
//     therefore inverted rather than imported: IAuditSink is declared in this layer and implemented in
//     Infrastructure over Serilog, which is exactly where the structured-logging obligation is assigned,
//     and the bound CachingOptions instance is taken directly instead of a wrapper around it.
//   - IClock is not injected because this service never reads a clock. Every date it handles is
//     supplied by the caller on a request DTO and is only ever compared against another supplied date,
//     and audit records are stamped by the sink. Injecting a clock to leave it unused would be a
//     dependency the constructor could not justify.
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
using System.Net;
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
using DnnMigration.Domain.Enums;

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
    /// Reported when the caller holds no edit grant on the page or module a mutation targets.
    /// </summary>
    /// <remarks>
    /// The <c>forbidden</c> token is what the shared status translator classifies as a 403, so the code
    /// itself - rather than a status written out at a call site - is what decides how this refusal reaches
    /// the caller. The message names the target but never says WHY the grant is missing, and a target that
    /// does not exist produces this same refusal rather than a not-found: distinguishing the two would tell
    /// an unauthorised caller which identifiers exist.
    /// </remarks>
    private const string EditForbiddenCode = "module.edit_forbidden";

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
    /// Reported when a module hands over content that cannot be carried by the export document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="ContentInvalidCode"/> on purpose, and the distinction is about WHO can put
    /// it right. A document the CALLER submitted and that cannot be interpreted is a request to correct, so
    /// it is reported as invalid content. Content the MODULE produced and that XML cannot represent - a
    /// control character, for instance, which no escaping in the specification can encode - is a fault in
    /// the module, and the caller can do nothing about it. The reason token <c>export_failed</c> is
    /// classified as a server fault by the API's failure-code table, so this surfaces as a 500 rather than
    /// telling the caller to fix a request that was correct.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy export wrapped its whole document assembly in <c>Try</c> with a <c>Catch</c>
    /// body of <c>'ignore errors</c> (ModuleController.vb L250-L252), so a module whose content could not
    /// be written produced a portal template with the content element silently missing and reported
    /// success. That swallow is a defect and is not reproduced: the failure is surfaced with this code. The
    /// divergence is recorded rather than absorbed, per the rule that a discovered defect is annotated and
    /// the swallow is resolved in favour of surfacing.
    /// </para>
    /// </remarks>
    private const string ExportFailedCode = "module.export_failed";

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
    /// MIGRATION: <b>the width is 2000, not 256.</b> The
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
    /// The submitted position that means "put this module at the bottom of its pane" rather than
    /// naming a position.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this value is a COMMAND on the request contract and must never reach a column. The
    /// legacy documentation states it outright - <c>UpdateModuleOrder</c>'s own parameter comment reads
    /// "position within the controls list on page, -1 if to be added at the end"
    /// (ModuleController.vb:L1155) - and the legacy code acted on it as a command in two places, both
    /// immediately after writing the row: <c>AddModule</c> tested
    /// <c>If objModule.ModuleOrder = -1 Then UpdateModuleOrder(...)</c> (L667-L669) and
    /// <c>UpdateModule</c> called the same resolver unconditionally (L1124). The number is the integer
    /// absence sentinel from <c>Library/Components/Shared/Null.vb</c> L41 being reused as an
    /// instruction, which is why it must be consumed by this layer rather than mapped onto the column
    /// like an ordinary value: persisted literally it would sort every appended module ahead of every
    /// deliberately positioned one, since the stored positions are non-negative.
    /// </remarks>
    private const int AppendPositionSentinel = -1;

    /// <summary>
    /// The gap the legacy renumbering pass left between two adjacent positions in one pane.
    /// </summary>
    /// <remarks>
    /// MIGRATION: measured, not chosen. <c>UpdateModuleOrder</c> resolved an append by reading the
    /// pane's occupied positions and then adding exactly two (ModuleController.vb:L1170), and the
    /// renumbering pass that normalises a pane assigns <c>(counter * 2) - 1</c> (L1205 and L1441). The
    /// step of two is therefore what keeps an appended module's position on the same odd sequence a
    /// renumbering pass produces, leaving the even numbers between them free for an insertion. A step
    /// of one would appear to work and would quietly collide with the next renumbering.
    /// </remarks>
    private const int PositionStep = 2;

    /// <summary>
    /// Largest number of settings either scope may carry in one submission.
    /// </summary>
    /// <remarks>
    /// MIGRATION: a net-new bound with no legacy counterpart, and generous by design: seventy-two distinct
    /// setting names exist across EVERY bundled module in the whole legacy application, so this admits more
    /// than three times that vocabulary for a single module. It bounds row growth and transaction size, not
    /// any real configuration. The request validator states the same figure so a caller reached through the
    /// API gets a field-level answer; this copy covers every other caller, and the two must agree.
    /// </remarks>
    private const int SettingsPerScopeMaximum = 250;

    /// <summary>
    /// Largest number of settings the two scopes may carry between them in one submission.
    /// </summary>
    /// <remarks>
    /// MIGRATION: deliberately lower than twice the per-scope bound, so that it binds when both maps are
    /// large rather than being implied by them. This is the figure that bounds one transaction's row count,
    /// which the per-scope bounds alone do not protect. Mirrored by the request validator.
    /// </remarks>
    private const int SettingsAggregateMaximum = 400;

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
    /// Property the module listing orders by when a caller names none, reproducing the order the legacy
    /// module settings screen presented.
    /// </summary>
    private const string DefaultModuleSortProperty = "ModuleTitle";

    /// <summary>
    /// Attribution used when an import arrives without an authenticated caller.
    /// </summary>
    /// <remarks>
    /// <c>dbo.Users.UserID</c> is <c>IDENTITY(1,1)</c>, so no real account can own this value and it
    /// cannot be mistaken for one. Zero is not used because zero is a legitimate identifier elsewhere
    /// in this schema.
    /// </remarks>
    private const int UnattributedUserId = -1;

    /// <summary>
    /// Stable audit event name for every module change this service records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy module audit trail used one event type,
    /// <c>EventLogType.MODULE_UPDATED</c>, written through
    /// <c>EventLogController.AddLog</c> at <c>EventMessageProcessor.vb</c> L69 - the only
    /// <c>AddLog(</c> call site anywhere in the Modules tree. The string is reproduced verbatim so an
    /// operator reading the new trail recognises the events from the old one, and so a log query written
    /// against the legacy event type keeps working.
    /// </para>
    /// <para>
    /// MIGRATION: this is a local constant rather than a member of <see cref="AuditEventNames"/>, and
    /// that is a scope boundary rather than a preference. That type is a sibling this file may not
    /// extend; it declares names for the user, portal, page, role and session domains and none for
    /// modules. The gap is recorded here instead of being closed by editing a file outside this unit of
    /// work, and the value is identical to what the shared constant would hold.
    /// </para>
    /// </remarks>
    private const string ModuleUpdatedEventName = "MODULE_UPDATED";

    /// <summary>
    /// Resource type recorded on every module audit event, naming what the identifier identifies.
    /// </summary>
    private const string ModuleResourceType = "Module";

    private readonly IModuleRepository _modules;
    private readonly IModuleDefinitionRepository _definitions;
    private readonly ITabRepository _tabs;
    private readonly IPortalRepository _portals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionService _permissions;
    private readonly IModuleBusinessControllerFactory _businessControllers;
    private readonly IAuditSink _audit;
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
    /// <param name="currentUser">
    /// The caller. Used for attribution on a content import and, on the two mutations whose target is named
    /// in the request body rather than in the route, as the subject of the permission question below.
    /// </param>
    /// <param name="permissions">
    /// Evaluates whether the caller may edit the page a module is being created on, or the module content is
    /// being imported into. Those two targets arrive in the request BODY, so no route-based authorisation
    /// policy can reach them and the check has to happen here, after binding.
    /// </param>
    /// <param name="businessControllers">Resolution of a module's own portable-content contract.</param>
    /// <param name="audit">
    /// The audit trail. Written to for the changes whose effect reaches beyond the addressed module and
    /// for both directions of content movement, which is where the legacy application kept a record and
    /// where this contract promises one.
    /// </param>
    /// <param name="caching">
    /// Bound caching configuration supplying the performance multiplier. Taken as the bound instance
    /// rather than through an options wrapper because this project's package surface does not include
    /// one, as recorded at the head of this file.
    /// </param>
    public ModuleService(
        IModuleRepository modules,
        IModuleDefinitionRepository definitions,
        ITabRepository tabs,
        IPortalRepository portals,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        ICurrentUser currentUser,
        IPermissionService permissions,
        IModuleBusinessControllerFactory businessControllers,
        IAuditSink audit,
        CachingOptions caching)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _businessControllers = businessControllers ?? throw new ArgumentNullException(nameof(businessControllers));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
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
    /// The total count needs an explicit reconciliation. <see cref="ReadModulePageAsync"/>
    /// pages over modules, so its total is a module count. When a page is named the two agree, because
    /// a module has at most one placement on any one page. When no page is named an unpaged read
    /// reports the exact placement count, because every row is present; a paged read reports the
    /// module total, which is the only figure obtainable without an unbounded read of every placement
    /// in the portal. That trade is recorded here rather than hidden.
    /// </para>
    /// <para>
    /// Ordering is fixed: modules by title then key, and within each module its placements by page,
    /// then position, then placement identifier. It is not caller-selectable, and a request that names
    /// an ordering is REFUSED with <c>module.request_invalid</c> rather than accepted and ignored -
    /// accepting it would return a page the caller believes was ordered and cannot tell was not. The
    /// refusal is structural rather than a limitation of the read: the page window is taken over
    /// modules while each module contributes one row per placement, so any ordering could only apply to
    /// the modules behind the rows, never to the rows returned. <c>SortableFields.Modules</c> records
    /// the measurement and enumerates every excluded member of the projection.
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

        PagedResult<Module> page = await ReadModulePageAsync(
            portalId,
            tabId,
            includeDeleted,
            request,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<int, string> friendlyNames = page.Items.Count == 0
            ? new Dictionary<int, string>()
            : await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        var rows = new List<ModuleListItemDto>(page.Items.Count);
        foreach (Module module in page.Items)
        {
            IReadOnlyList<TabModule> placements =
                await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule placement in OrderPlacements(placements, tabId))
            {
                rows.Add(ModuleMappings.ToListItem(module, placement, ResolveFriendlyName(module, friendlyNames)));
            }
        }

        if (request.PageSize == UnpagedPageSize)
        {
            return Result<PagedResult<ModuleListItemDto>>.Success(
                PagedResult<ModuleListItemDto>.Unpaged(rows));
        }

        // The window is taken over MODULES while the rows carry PLACEMENTS, so the two figures the envelope
        // needs are not in the same unit and cannot be handed over unreconciled.
        //
        // The remarks above note that a module has at most one placement on any one page, and therefore
        // that the module total and the row count agree for a paged read. That holds only while a page is
        // NAMED. When tabId is null - which is the default listing of a portal's modules - no page filter is
        // applied, so a module contributes EVERY placement it has and the expansion can carry more rows than
        // there are modules behind them. Both of the envelope's guards then reject the arguments: the row
        // count exceeds the module total, and it can exceed the requested window size as well. That threw
        // ArgumentOutOfRangeException on a listing whose only unusual feature was a module placed on two
        // pages, which is an entirely ordinary configuration and the norm for a module marked AllTabs.
        //
        // Both figures are therefore raised to the number of rows actually being carried. Neither becomes
        // less truthful by it: the module total was already a LOWER BOUND on the placement total - the
        // documented trade, taken because the exact figure needs an unbounded read of every placement in the
        // portal - and the row count is a better lower bound from the same information. The declared window
        // widens only when the expansion overflowed it, so a page that fits reports the size that was asked
        // for, unchanged. No extra repository read is introduced.
        int carriedRows = rows.Count;

        return Result<PagedResult<ModuleListItemDto>>.Success(
            PagedResult<ModuleListItemDto>.Create(
                rows,
                Math.Max(page.TotalCount, carriedRows),
                page.PageIndex,
                Math.Max(page.PageSize, carriedRows)));
    }

    /// <summary>
    /// Reads one page of a tenant's modules, applying the recycle-bin, page and title filters.
    /// </summary>
    /// <param name="portalId">The tenant whose modules are read.</param>
    /// <param name="tabId">Restrict to the modules placed on one page, or <see langword="null"/> for the whole tenant.</param>
    /// <param name="includeDeleted">Whether modules already in the recycle bin are included.</param>
    /// <param name="request">The paging request supplying the page window and the optional title query.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The requested page of modules together with the unpaged total.</returns>
    /// <remarks>
    /// MIGRATION: the legacy module block of the data provider carries no paging member of any kind, so
    /// none is invented on the repository contract; the page window and the filters are composed here,
    /// in the layer that owns the paging request. The predicates are applied in the same order and with
    /// the same meaning the single legacy query had.
    /// <para>
    /// The page filter is answered by the page repository rather than by reading each candidate module's
    /// placements in turn. "Which modules sit on this page" is a page-centric question, it belongs to
    /// that contract by the same ownership split that keeps placement mutation on the module contract,
    /// and it resolves in one read instead of one per candidate.
    /// </para>
    /// </remarks>
    private async Task<PagedResult<Module>> ReadModulePageAsync(
        int portalId,
        int? tabId,
        bool includeDeleted,
        PagedRequest request,
        CancellationToken cancellationToken)
    {
        IEnumerable<Module> candidates =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        if (!includeDeleted)
        {
            candidates = candidates.Where(module => !module.IsDeleted);
        }

        if (tabId is int addressedTab)
        {
            // The presence of a value selects the filter, never its magnitude: TabID is IDENTITY(0, 1),
            // so a page identifier of zero is a real page and must not be read as "unspecified".
            IReadOnlyList<TabModule> onPage =
                await _tabs.GetTabModulesAsync(addressedTab, cancellationToken).ConfigureAwait(false);

            HashSet<int> placedModuleIds = onPage.Select(placement => placement.ModuleId).ToHashSet();
            candidates = candidates.Where(module => placedModuleIds.Contains(module.ModuleId));
        }

        if (request.HasQuery)
        {
            // ModuleTitle is nullable, so the null test precedes the comparison. The match stays
            // case-insensitive, as the lower-cased legacy comparison was.
            string wanted = request.Query!.Trim();
            candidates = candidates.Where(module =>
                module.ModuleTitle is not null
                && module.ModuleTitle.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        List<Module> ordered = ApplyOrder(candidates, request).ToList();

        if (request.PageSize == UnpagedPageSize)
        {
            return PagedResult<Module>.Unpaged(ordered);
        }

        List<Module> window = ordered
            .Skip(Paging.SkipCount(request.PageIndex, request.PageSize))
            .Take(request.PageSize)
            .ToList();

        return PagedResult<Module>.Create(window, ordered.Count, request.PageIndex, request.PageSize);
    }

    /// <summary>
    /// Applies the caller's chosen ordering to the narrowed module set, before the page is taken.
    /// </summary>
    /// <param name="candidates">The modules that survived the deletion, page and title filters.</param>
    /// <param name="request">The paging request carrying the sort field and its direction.</param>
    /// <returns>The ordered module set.</returns>
    /// <remarks>
    /// An ordering is applied unconditionally, including when the caller names nothing and when the
    /// caller names something this listing does not recognise, and every ordering ends on the primary key
    /// so the order is total. Ordering happens here rather than after the page has been taken, because a
    /// listing that ordered a page it had already cut would only be re-ordering the rows that one
    /// arbitrary page happened to contain.
    /// </remarks>
    // MIGRATION: the arms below are exactly the five names the boundary admits for this collection,
    // declared as Modules in Application/Validation/SortableFields.cs and enforced by the sealed
    // ModulePagedRequestValidator, and each one is a projected member of ModuleListItemDto. That
    // correspondence has to hold in both directions: a name the boundary admits without an arm here is a
    // field the listing accepts and then ignores, and an arm without a permitted name is unreachable.
    //
    // Two members of the projection are deliberately NOT sortable, and their absence is measured rather
    // than accidental. ModuleOrder is a per-pane placement position rather than a listing order: it is a
    // column of dbo.TabModules and not of dbo.Modules, it is only meaningful within one pane of one page,
    // and it carries the append sentinel -1 - so ordering a cross-page module listing by it would sort
    // unrelated positions against each other and put every pending append first. DisplayTitle is derived
    // at projection time from the module title and its definition's friendly name, so it exists only
    // after this ordering has run; sorting by it would require the derivation to move into the read.
    //
    // The default arm reproduces the order this listing has always had, the module title compared
    // case-insensitively. Note that ModuleTitle is nullable, so an untitled module sorts first ascending
    // and last descending; that is the framework comparer's own behaviour and is left as it is, because
    // grouping the untitled modules together at one end is the only ordering that carries information.
    private static IEnumerable<Module> ApplyOrder(IEnumerable<Module> candidates, PagedRequest request)
    {
        bool descending = request.SortDir == SortDirection.Descending;
        string property = request.HasSort ? request.SortBy!.Trim() : DefaultModuleSortProperty;

        return property.ToUpperInvariant() switch
        {
            "MODULEID" => descending
                ? candidates.OrderByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.ModuleId),
            "ISDELETED" => descending
                ? candidates.OrderByDescending(module => module.IsDeleted)
                    .ThenByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.IsDeleted).ThenBy(module => module.ModuleId),
            "STARTDATE" => descending
                ? candidates.OrderByDescending(module => module.StartDate)
                    .ThenByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.StartDate).ThenBy(module => module.ModuleId),
            "ENDDATE" => descending
                ? candidates.OrderByDescending(module => module.EndDate)
                    .ThenByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.EndDate).ThenBy(module => module.ModuleId),
            _ => descending
                ? candidates
                    .OrderByDescending(module => module.ModuleTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(module => module.ModuleId)
                : candidates
                    .OrderBy(module => module.ModuleTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(module => module.ModuleId),
        };
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
            .GetByIdAsync(moduleId, cancellationToken)
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

        Tab? tab = await _tabs.GetByIdAsync(request.TabId, cancellationToken).ConfigureAwait(false);
        if (tab is null || tab.PortalId != portalId)
        {
            return Result<ModuleDetailDto>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {request.TabId} does not belong to portal {portalId}."));
        }

        // The caller must hold the edit grant on the page they are placing a module on. Verified here rather
        // than by a policy because the page arrives in the BODY, which no route-reading policy can see - and
        // verified at all because it previously was not: tenant ownership was checked and the permission was
        // not, so any authenticated caller could place a module on any tenant's page. When the request also
        // asks for every page, the grant on the addressed page is what authorises the fan-out, exactly as the
        // legacy screen's "all pages" switch was reached from one page already in edit mode.
        if (await EnsureMayEditPageAsync(portalId, request.TabId, cancellationToken).ConfigureAwait(false)
            is ResultReason forbidden)
        {
            return Result<ModuleDetailDto>.Failure(forbidden);
        }

        Module module = ModuleMappings.ToNewModule(portalId, request);
        TabModule placement = ModuleMappings.ToNewPlacement(request);

        // The submitted position may be the append instruction rather than a position, and the mapping
        // carries it through verbatim because a mapping cannot read the pane it would have to resolve
        // against. It is resolved here, before anything is staged, so the instruction is consumed by this
        // layer and never reaches the column.
        placement.ModuleOrder = await ResolvePositionAsync(
            placement.TabId,
            placement.PaneName,
            request.ModuleOrder,
            cancellationToken).ConfigureAwait(false);

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

                TabModule additional = ModuleMappings.ToNewPlacement(request);
                additional.TabId = target.TabId;

                // Resolved against the TARGET page's pane rather than copied from the addressed one,
                // because the legacy resolver was keyed on (TabId, PaneName): appending to one pane says
                // nothing about where the bottom of another page's pane is. No two placements created
                // here share a page, so each read sees a settled pane even though none of them is saved
                // yet.
                additional.ModuleOrder = await ResolvePositionAsync(
                    target.TabId,
                    additional.PaneName,
                    request.ModuleOrder,
                    cancellationToken).ConfigureAwait(false);

                module.TabModules.Add(additional);
                affectedTabIds.Add(target.TabId);
            }
        }

        await _modules.AddAsync(module, cancellationToken).ConfigureAwait(false);
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

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
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

        // The projection above assigned the submitted position verbatim, which may be the append
        // instruction. Resolving it here - after the projection and before the fan-out below - is what
        // stops the instruction reaching the column and stops it being copied onto every other page as
        // though it were a position. The stored pane is used, because the update contract carries no
        // pane and the projection deliberately leaves the column alone.
        placement.ModuleOrder = await ResolvePositionAsync(
            placement.TabId,
            placement.PaneName,
            request.ModuleOrder,
            cancellationToken).ConfigureAwait(false);

        var affectedTabIds = new HashSet<int> { placement.TabId };
        var effects = new List<string>();

        if (!wasPlacedEverywhere && module.AllTabs)
        {
            int added = await PlaceOnContentTabsAsync(
                portalId,
                module,
                placement,
                request.ModuleOrder,
                affectedTabIds,
                cancellationToken).ConfigureAwait(false);
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

        // MIGRATION: renamed from the legacy IsDefaultModule, whose name read as persisted state although it
        // was excluded from the legacy object's serialisation and carried no column. The target name follows
        // the wording the product showed its users, ModuleSettings.ascx.resx plDefault.Text, "Set As Default
        // Settings?". It remains intent rather than state: the write below targets PORTAL configuration, and
        // the portal comes from the per-request context rather than from the request body.
        if (request.SetAsDefaultSettings)
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

        // MIGRATION: renamed from the legacy AllModules, whose name read as a COLLECTION of modules rather
        // than as an instruction about them, following ModuleSettings.ascx.resx plAllModules.Text, "Apply To
        // All Modules?".
        //
        // MIGRATION: 5.9 - the propagation this triggers is NARROWER than the legacy's. The legacy loop
        // copied NINE appearance values - alignment, colour, border, icon, visibility, container source and
        // the title, print and syndicate flags - to every module on every non-administrative page. SIX of
        // those nine are excluded from UpdateModuleRequest as pane-layout, rendering or skinning concerns,
        // leaving only the icon, the visibility and the container-display flag propagable. That is a
        // documented functional reduction rather than an oversight, and it follows mechanically from the
        // exclusions. This is still the only path in the module API that writes rows outside the addressed
        // module, so its portal-wide reach is why it is administrator-gated by policy.
        if (request.ApplyToAllModules)
        {
            int copied = await PropagateAppearanceAsync(portalId, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            effects.Add(FormattableString.Invariant($"appearance copied to {copied} placement(s) on content pages"));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        // The record is written only when the change reached beyond the addressed module, which is the
        // condition this contract documents: naming the module as the portal default writes portal-level
        // keys, and propagating appearance rewrites placements the caller never named. An ordinary edit of
        // one module is not recorded, because a trail that logs every field edit stops being a record of
        // consequential change and becomes a change log - and the legacy application logged neither.
        //
        // Emitted after the commit, so nothing here can describe a change that was rolled back. The facts
        // are the blast radius and the identifiers needed to recognise what moved; no free-text the caller
        // supplied - the title, header and footer - is carried.
        if (effects.Count > 0)
        {
            RecordModuleAudit(
                portalId,
                module.ModuleId,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Operation"] = "Update",
                    ["TabModuleId"] = placement.TabModuleId.ToString(CultureInfo.InvariantCulture),
                    ["TabId"] = placement.TabId.ToString(CultureInfo.InvariantCulture),
                    ["SetAsDefaultSettings"] = request.SetAsDefaultSettings.ToString(),
                    ["ApplyToAllModules"] = request.ApplyToAllModules.ToString(),
                    ["AffectedTabCount"] = affectedTabIds.Count.ToString(CultureInfo.InvariantCulture),
                    ["Effects"] = string.Join("; ", effects),
                });
        }

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
            .GetByIdAsync(moduleId, cancellationToken)
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
            TabModule? placement = await _modules.GetTabModuleByIdAsync(addressed, cancellationToken).ConfigureAwait(false);
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
                await _modules.GetTabModulesByModuleIdAsync(moduleId, cancellationToken).ConfigureAwait(false);

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
            .GetByIdAsync(moduleId, cancellationToken)
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
            await _modules.GetModuleSettingsAsync(moduleId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TabModuleSetting> placementSettings =
            await _modules.GetTabModuleSettingsAsync(placement.TabModuleId, cancellationToken).ConfigureAwait(false);

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
        // MIGRATION: an absent map is REFUSED rather than thrown on. Both members are non-nullable
        // reference types carrying an initialiser on the request contract, which makes null look
        // unreachable - but an initialiser only runs when the deserialiser does not assign, and a body
        // carrying an explicit null assigns over it. Throwing here turned a syntactically valid body into
        // a server fault; the caller is now told which map is missing. The request validator states the
        // same rule at the edge, and this remains as defence in depth for callers it does not front.
        if (moduleSettings is null)
        {
            return Result.Failure(
                SettingInvalidCode,
                "The module settings map is required. Send an empty map to clear every module setting.");
        }

        if (tabModuleSettings is null)
        {
            return Result.Failure(
                SettingInvalidCode,
                "The placement settings map is required. Send an empty map to clear every placement "
                + "setting.");
        }

        // MIGRATION: cardinality is bounded before anything is read or written. Every setting name and
        // value was already bounded individually, but nothing bounded the COUNT, and the two limits
        // multiply: a body well inside the request size limit can carry tens of thousands of short
        // settings, each becoming a tracked entity and a row in one transaction. The bound is checked
        // against the two maps together as well as separately, because the transaction spans both.
        if (moduleSettings.Count > SettingsPerScopeMaximum
            || tabModuleSettings.Count > SettingsPerScopeMaximum)
        {
            return Result.Failure(
                SettingInvalidCode,
                FormattableString.Invariant(
                    $"A settings map must carry no more than {SettingsPerScopeMaximum} entries."));
        }

        if (moduleSettings.Count + tabModuleSettings.Count > SettingsAggregateMaximum)
        {
            return Result.Failure(
                SettingInvalidCode,
                FormattableString.Invariant(
                    $"The two settings maps must carry no more than {SettingsAggregateMaximum} entries")
                + " between them.");
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // MIGRATION: the two stores are normalised and length-checked SEPARATELY, against their own column
        // width and under their own scope word, because they are separate tables - dbo.ModuleSettings keyed
        // (ModuleID, SettingName) and dbo.TabModuleSettings keyed (TabModuleID, SettingName). The legacy
        // readers at ModuleController.vb L1237 and L1336 each remarked that the other store was excluded,
        // and that separation is preserved here rather than collapsed into one map.
        NormalisedSettings desiredModule =
            NormaliseSettings(moduleSettings, ModuleSettingValueMaximumLength, "module");

        if (desiredModule.Reason is ResultReason moduleSettingInvalid)
        {
            return Result.Failure(moduleSettingInvalid);
        }

        NormalisedSettings desiredPlacement =
            NormaliseSettings(tabModuleSettings, PlacementSettingValueMaximumLength, "placement");

        if (desiredPlacement.Reason is ResultReason placementSettingInvalid)
        {
            return Result.Failure(placementSettingInvalid);
        }

        Dictionary<string, string> desiredModuleSettings = desiredModule.Values;
        Dictionary<string, string> desiredPlacementSettings = desiredPlacement.Values;

        TabModule? placement = null;
        if (tabModuleId is int addressed)
        {
            placement = await _modules.GetTabModuleByIdAsync(addressed, cancellationToken).ConfigureAwait(false);
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
            await _modules.GetModuleSettingsAsync(moduleId, cancellationToken).ConfigureAwait(false);

        var survivingModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleSetting stored in storedModuleSettings)
        {
            if (!desiredModuleSettings.TryGetValue(stored.SettingName, out string? desired))
            {
                await _modules
                    .DeleteModuleSettingAsync(stored.ModuleId, stored.SettingName, cancellationToken)
                    .ConfigureAwait(false);
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

            await _modules
                .AddModuleSettingAsync(
                    new ModuleSetting
                    {
                        ModuleId = moduleId,
                        SettingName = desired.Key,
                        SettingValue = desired.Value,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (placement is not null)
        {
            IReadOnlyList<TabModuleSetting> storedPlacementSettings = await _modules
                .GetTabModuleSettingsAsync(placement.TabModuleId, cancellationToken)
                .ConfigureAwait(false);

            var survivingPlacementNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TabModuleSetting stored in storedPlacementSettings)
            {
                if (!desiredPlacementSettings.TryGetValue(stored.SettingName, out string? desired))
                {
                    await _modules
                        .DeleteTabModuleSettingAsync(stored.TabModuleId, stored.SettingName, cancellationToken)
                        .ConfigureAwait(false);
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

                await _modules
                    .AddTabModuleSettingAsync(
                        new TabModuleSetting
                        {
                            TabModuleId = placement.TabModuleId,
                            SettingName = desired.Key,
                            SettingValue = desired.Value,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
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
                await _modules.GetTabModulesByModuleIdAsync(moduleId, cancellationToken).ConfigureAwait(false);

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
    /// Answered by NARROWING THE CATALOGUE rather than by reading the definition directly, and the choice
    /// is load-bearing. The catalogue read applies the premium-module rule - a definition is available to a
    /// portal only when its package is not premium or has been granted to that portal - and a direct
    /// repository read by identifier applies nothing at all. Re-implementing the rule here would place a
    /// second copy of it beside the first, and the two copies would eventually disagree about which
    /// definitions a tenant may see, which is a tenant-isolation defect rather than a cosmetic one. It also
    /// reuses the catalogue's cache entry, so a by-identifier read costs no database round trip once the
    /// catalogue is warm; the catalogue is small, bounded reference data written only by installation.
    /// </remarks>
    public async Task<Result<ModuleDefinitionDto?>> GetModuleDefinitionAsync(
        int portalId,
        int moduleDefinitionId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<ModuleDefinitionDto>> catalogue = await this
            .ListModuleDefinitionsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (catalogue.IsFailure)
        {
            return Result<ModuleDefinitionDto?>.Failure(catalogue.Error!);
        }

        // A definition the portal cannot instantiate is reported as ABSENT rather than refused, so the
        // endpoint cannot be used to discover which definitions exist elsewhere in the installation: a
        // caller cannot tell "no such definition" from "not yours" and therefore learns nothing either way.
        ModuleDefinitionDto? definition = catalogue.Value
            .FirstOrDefault(candidate => candidate.ModuleDefId == moduleDefinitionId);

        return Result<ModuleDefinitionDto?>.Success(definition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Narrows the catalogue for the same reason the single-definition read above does, and consequently
    /// preserves the catalogue's ordering - friendly name, then identifier - rather than imposing an order
    /// of its own. A package that declares nothing, or that the portal has not been granted, yields an
    /// empty sequence, which is a legitimate answer: the caller asked a question with a negative answer
    /// rather than asking an invalid question.
    /// </remarks>
    public async Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListDesktopModuleDefinitionsAsync(
        int portalId,
        int desktopModuleId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<ModuleDefinitionDto>> catalogue = await this
            .ListModuleDefinitionsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (catalogue.IsFailure)
        {
            return catalogue;
        }

        IReadOnlyList<ModuleDefinitionDto> declared = catalogue.Value
            .Where(candidate => candidate.DesktopModuleId == desktopModuleId)
            .ToList();

        return Result<IReadOnlyList<ModuleDefinitionDto>>.Success(declared);
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
    /// MIGRATION: the payload is HTML-encoded before it is written, which is the first half of the
    /// legacy escaping pipeline and is preserved deliberately. <c>ModuleController.vb</c> L244 read
    /// <c>Content = HttpContext.Current.Server.HtmlEncode(Content)</c> and the following line wrapped
    /// the encoded text in a CDATA section; the import path at L428 unwrapped the section and applied
    /// the matching <c>Server.HtmlDecode</c>. Dropping the encode would still round-trip documents this
    /// service produced, but it would silently corrupt a document produced by the legacy application -
    /// its payload would come back with one layer of HTML escaping still on it - and interoperating with
    /// those documents is the entire purpose of a migrated export format. The ambient
    /// <c>HttpContext.Current.Server</c> accessor is replaced by <see cref="WebUtility"/>, so the
    /// escaping is identical while this layer takes no ASP.NET dependency. The CDATA wrap itself is not
    /// reproduced: an XML writer escapes the element's text content correctly on its own, and the
    /// reader below accepts either form.
    /// </para>
    /// <para>
    /// An empty payload still yields a document, because "asked and given nothing" is a real answer that
    /// the caller is entitled to see. This is a documented divergence from the legacy guard at L233,
    /// which wrote a content element only for non-empty content and therefore left the caller unable to
    /// distinguish "no content" from "never asked".
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
            .GetByIdAsync(moduleId, cancellationToken)
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

        // MIGRATION: ModuleController.vb:L244 - `Content = HttpContext.Current.Server.HtmlEncode(Content)`.
        // The ambient HttpContext accessor is replaced by System.Net.WebUtility, which performs the same
        // escaping and is reachable from a class library that references no web framework. The encode is
        // kept rather than dropped so that a document this service writes is escaped the way the legacy
        // application escaped one, which is what lets ImportModuleAsync accept both interchangeably.
        string encodedPayload = WebUtility.HtmlEncode(exported.Value);

        var document = new XElement(
            ContentElementName,
            new XAttribute(ContentTypeAttributeName, package.ModuleName),
            new XAttribute(ContentVersionAttributeName, package.Version ?? string.Empty),
            encodedPayload);

        // Serialising is the step that can still refuse the payload, so it is guarded rather than assumed
        // to succeed. HTML encoding escapes markup but the XML specification has no encoding at all for a
        // C0 control character, so a module that returns one produces content this format cannot carry. The
        // writer signals that by raising, and an unhandled raise here would surface as an unexplained
        // server error rather than as the documented outcome this contract owes the caller.
        //
        // Only the two exception types the writer raises for unrepresentable content are caught. Anything
        // else is left to propagate: a broad catch would convert a genuine defect into a tidy failure code
        // and hide it.
        string serialised;
        try
        {
            serialised = document.ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception exception) when (exception is ArgumentException or XmlException)
        {
            // The module's own text is deliberately NOT quoted in the message. It is module content and may
            // carry the portal's user data, and the message is published verbatim as the problem detail.
            return Result<string>.Failure(
                ExportFailedCode,
                FormattableString.Invariant(
                    $"Module {moduleId} returned content that cannot be represented in an export document."));
        }

        // The trail records that content left the module, and the SIZE of what left rather than any part
        // of it: an export payload is module content, which may be arbitrarily large and may carry data
        // belonging to the portal's users.
        RecordModuleAudit(
            portalId,
            module.ModuleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Export",
                ["BusinessControllerRegistered"] = bool.TrueString,
                ["PayloadLength"] = exported.Value.Length.ToString(CultureInfo.InvariantCulture),
            });

        return Result<string>.Success(serialised);
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
    /// MIGRATION: the extracted payload is HTML-decoded, which is the second half of the legacy escaping
    /// pipeline. <c>ModuleController.vb</c> L428 read
    /// <c>strcontent = HttpContext.Current.Server.HtmlDecode(strcontent)</c>, and the line before it
    /// stripped the CDATA section the export had wrapped the encoded text in - literally
    /// <c>strcontent.Substring(9, strcontent.Length - 12)</c>, the 9 characters of the opening delimiter
    /// and the 3 of the closing one. The ambient accessor is replaced by <see cref="WebUtility"/> so this
    /// layer takes no ASP.NET dependency, and the fixed-offset substring is replaced by reading the
    /// element's value: an XML reader resolves a CDATA section to its text automatically, so a legacy
    /// document and one written by this service both arrive here as the same encoded string and are
    /// decoded identically. The fixed offsets were also a latent defect - a document whose content
    /// element was not exactly a CDATA section had 9 characters of real payload removed - and replacing
    /// them removes that failure mode rather than reproducing it.
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

        // MIGRATION: the target module arrives in the BODY, because this endpoint carries no identifier in
        // its route, and ModuleImportRequest.ModuleId is consequently NULLABLE so that an omitted value is
        // distinguishable from a supplied one. That distinction has to be resolved right here and cannot be
        // deferred: this request has no FluentValidation validator - the planned validator set contains no
        // entry for it - so nothing upstream refuses the omission, and Modules.ModuleID is IDENTITY(0,1),
        // which makes ZERO A REAL MODULE. A non-nullable property would therefore have deserialised a
        // missing moduleId into a live request against module zero, and this service could not have told
        // the two apart.
        //
        // Refused by ABSENCE alone, never by sign. Both 0 and -1 are legitimate identifier values in this
        // schema - 0 is the module identity seed and -1 is a real portal identifier as well as the legacy
        // integer sentinel - so a caller that sends either has made a lookup, and it must be allowed to
        // fail as a lookup below rather than be misreported as a malformed request.
        if (request.ModuleId is not int moduleId)
        {
            return Result.Failure(
                RequestInvalidCode,
                "The module to import into must be supplied.");
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // The caller must hold the edit grant on the module whose content they are replacing. Verified here
        // for the same reason as on creation - the target arrives in the BODY - and verified at all because it
        // previously was not, so any authenticated caller could overwrite any tenant's module content.
        if (await EnsureMayEditModuleAsync(portalId, moduleId, cancellationToken).ConfigureAwait(false)
            is ResultReason forbidden)
        {
            return Result.Failure(forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Result.Failure(ContentInvalidCode, "The submitted document is empty.");
        }

        // MIGRATION: THE DEFERRED-IMPORT EVENT-QUEUE BRANCH AT ModuleController.vb:L422 IS OMITTED, and what
        // replaces it is the refusal below. The legacy code tested
        // `If objModule.SupportedFeatures = Null.NullInteger` - the integer sentinel -1, meaning "this
        // module's capabilities have not been determined yet because it was installed in this very request"
        // - and, when true, called CreateEventQueueMessage to park the payload on a queue for replay after
        // an application restart. That queue subsystem is excluded from this migration wholesale, so there
        // is nowhere to park anything.
        //
        // Nothing is lost, because the condition the branch existed to wait for cannot arise here. The
        // legacy had to defer only because it discovered a module's capabilities by late-binding its
        // controller class at run time, which was impossible mid-install; this service resolves behaviour
        // from a closed, dependency-injected set that is fixed at start-up, so a capability is either
        // registered or it is not and waiting changes nothing.
        //
        // The sentinel is nonetheless handled rather than ignored: DesktopModule.IsPortable guards
        // `SupportedFeatures > -1`, so an undetermined capability field reports NOT portable and this
        // request is refused with a reason the caller can act on - retry once the package is fully
        // installed - instead of succeeding silently having parked the document somewhere the caller
        // cannot observe. That is the documented divergence: a clear failure in place of a deferral.
        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package is null || string.IsNullOrWhiteSpace(package.BusinessControllerClass) || !package.IsPortable)
        {
            return Result.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {moduleId} does not support content import."));
        }

        XDocument document;
        try
        {
            // PreserveWhitespace is required, not cosmetic. Without it the reader discards a text node that
            // is entirely whitespace as insignificant, so a payload of nothing but spaces or tabs arrived as
            // the empty string and the module was handed content the caller had not sent. Measured, not
            // theorised: a three-space payload round-tripped to "" until this option was supplied. Content
            // whose significance the caller decides must not be filtered by an XML reader's default.
            document = XDocument.Parse(request.Content, LoadOptions.PreserveWhitespace);
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

        // A payload that is itself markup is handed on as markup and is NOT decoded: it was never encoded,
        // so decoding it would corrupt any entity reference it legitimately contains. Only the text form -
        // which is what both this service and the legacy exporter write - carries the encoding to undo.
        //
        // MIGRATION: ModuleController.vb:L428 - `strcontent = HttpContext.Current.Server.HtmlDecode(strcontent)`,
        // preceded by the fixed-offset CDATA strip at L414. WebUtility.HtmlDecode replaces the ambient
        // HttpContext accessor, and XElement.Value replaces the substring arithmetic because an XML reader
        // already resolves a CDATA section to its text. This is what makes a legacy-produced export file
        // importable unchanged.
        //
        // MIGRATION: A CARRIAGE RETURN IN THE PAYLOAD BECOMES A LINE FEED, and that is PRESERVED legacy
        // behaviour rather than a new loss. The XML specification requires a reader to normalise every
        // line ending to a single line feed, and it applies that inside a CDATA section too, so the legacy
        // path lost the carriage return at exactly the same point. Verified by running both forms rather
        // than reasoned about: a payload of "a\r\nb" returns as "a\nb" through the CDATA shape the legacy
        // exporter wrote and through the shape this service writes, identically. Preserving it would need
        // the return escaped as a character reference, which would make documents this service writes
        // unreadable by the legacy importer - so the behaviour is annotated and left alone, per the rule
        // that a discovered legacy defect is recorded rather than quietly improved.
        string payload = root.HasElements
            ? string.Concat(root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
            : WebUtility.HtmlDecode(root.Value);

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
            await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        foreach (TabModule placement in placements)
        {
            _cache.InvalidateModules(placement.TabId);
        }

        // The provenance this contract promises to record: which module received content, where the caller
        // said it came from, which version the document declared, and how much arrived. The PAYLOAD IS NOT
        // RECORDED - only its length - because it is module content and may carry the portal's user data.
        // Emitted after the commit, so a failed import leaves no record claiming success.
        RecordModuleAudit(
            portalId,
            module.ModuleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["Version"] = version,
                ["SourceFolder"] = request.Folder,
                ["SourceFileName"] = request.FileName,
                ["PayloadLength"] = payload.Length.ToString(CultureInfo.InvariantCulture),
                ["PlacementCount"] = placements.Count.ToString(CultureInfo.InvariantCulture),
            });

        return imported.Reason is ResultReason advisory ? Result.Success(advisory) : Result.Success();
    }

    /// <summary>
    /// Confirms that the caller may edit a page, which is what placing a module on it requires.
    /// </summary>
    /// <param name="portalId">The tenant the page belongs to.</param>
    /// <param name="tabId">The page a module is being created on.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// <para>
    /// WHY THIS CHECK LIVES HERE AND NOT IN A POLICY. Every other module mutation names its module in the
    /// route, so a route-reading authorisation policy can evaluate a permission against it before the action
    /// runs. Creation does not: the page it targets arrives in the request BODY, which no policy can read,
    /// and there is no module yet to evaluate against. The check therefore has to be performed after binding,
    /// and the only layer holding both the caller and the permission evaluator is this one.
    /// </para>
    /// <para>
    /// WHAT IT WAS BEFORE. Creation carried the bare authentication requirement, and this service's own
    /// commentary asserted that the service performed the check. It did not - it verified only that the page
    /// belonged to the tenant - so any authenticated caller could place a module on any page of any tenant.
    /// The assertion is now true.
    /// </para>
    /// <para>
    /// EDIT ON THE PAGE IS THE LEGACY RULE. The legacy module-settings screen was reachable only from a page
    /// already in edit mode, which required the edit permission on that page, and the permission service
    /// answers a host account affirmatively before reading a grant - so a host account is admitted exactly as
    /// it is everywhere else. A refusal and a page that does not exist produce the SAME answer, because
    /// telling an unauthorised caller which page identifiers exist is an enumeration oracle.
    /// </para>
    /// <para>
    /// NO IMPLICIT PORTAL-ADMINISTRATOR ARM, DELIBERATELY. Admitting a portal administrator here regardless of
    /// grants was considered and rejected on two grounds. Legacy
    /// <c>Library/Components/Security/PortalSecurity.vb</c> L123 short-circuits on <c>IsSuperUser</c> ALONE and
    /// admits every other caller only through a real grant or a pseudo-role, so an implicit administrator arm
    /// would be a widening rather than a port; and the sibling update, delete and settings endpoints are gated
    /// on the module edit policy, which likewise delegates wholly to <see cref="IPermissionService"/>. Adding
    /// the arm on creation alone would make placing a module easier than editing the one just placed. A portal
    /// administrator that should be able to place modules is granted the page edit permission, which is what
    /// the permissions resource exists to do.
    /// </para>
    /// </remarks>
    private async Task<ResultReason?> EnsureMayEditPageAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken)
    {
        Result<bool> granted = await _permissions
            .HasTabPermissionAsync(portalId, _currentUser.UserId, tabId, PermissionKey.EDIT, cancellationToken)
            .ConfigureAwait(false);

        return granted.IsSuccess && granted.Value
            ? null
            : new ResultReason(
                EditForbiddenCode,
                FormattableString.Invariant(
                    $"The caller may not place a module on page {tabId} in portal {portalId}."));
    }

    /// <summary>
    /// Confirms that the caller may edit a module, which is what importing content into it requires.
    /// </summary>
    /// <param name="portalId">The tenant the module belongs to.</param>
    /// <param name="moduleId">The module content is being imported into.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// The import's target arrives in the request body - the legacy import page chose it from a list on the
    /// form - so, exactly as for creation, no route-reading policy can reach it and the check belongs here.
    /// Import replaces a module's stored content, so the permission required is the module edit key, the same
    /// one the update and delete endpoints are gated on.
    /// </remarks>
    private async Task<ResultReason?> EnsureMayEditModuleAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken)
    {
        Result<bool> granted = await _permissions
            .HasModulePermissionAsync(
                portalId,
                _currentUser.UserId,
                moduleId,
                PermissionKey.EDIT,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return granted.IsSuccess && granted.Value
            ? null
            : new ResultReason(
                EditForbiddenCode,
                FormattableString.Invariant(
                    $"The caller may not import content into module {moduleId} in portal {portalId}."));
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

        // MIGRATION: the PER-COLLECTION ordering set is enforced HERE, against this collection's own set
        // rather than against the union. The shared request validator applies nothing narrower than the
        // union of every collection's set - one PagedRequest contract serves every listing and one
        // validator is resolved for it - so a portal-only, role-only or account-only field name would
        // otherwise be accepted here and then silently discarded, which returns a page the caller cannot
        // account for and cannot detect. Refusing states that plainly.
        //
        // SortableFields.Modules holds exactly the five names ApplyOrder has an arm for, and that
        // correspondence is the point: a name admitted here without an arm is a field the listing accepts
        // and ignores, and an arm without an admitted name is unreachable. The ordering is applied to the
        // modules BEFORE the page window is taken, so it orders the collection rather than re-sorting one
        // arbitrary page - and because each module contributes its placement rows together, the rows are
        // returned in the module order the caller asked for. The projection's two remaining members are
        // refused rather than faked, for the reasons recorded on ApplyOrder: a placement position is not a
        // listing order, and a title derived at projection time does not exist until after the ordering
        // has run.
        if (!SortableFields.IsPermittedFor(request.SortBy, SortableFields.Modules))
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant($"Modules cannot be ordered by '{request.SortBy}'."));
        }

        return null;
    }

    // MIGRATION: THERE IS NO ORDERING RULE ON THE DISPLAY WINDOW, AND THAT ABSENCE IS MEASURED RATHER THAN
    //            ASSUMED. A helper here used to refuse a request whose end date preceded its start date. The
    //            legacy screen refuses no such thing. Website/admin/Modules/modulesettings.ascx declares
    //            EXACTLY FOUR validators - valtxtStartDate (L78-L79), valtxtEndDate (L88-L89), valBorder
    //            (L137-L138) and valCacheTime (L172-L173) - every one of them a CompareValidator with
    //            Operator="DataTypeCheck", which asserts only that the submitted text parses as its declared
    //            type. There is no RangeValidator, no CompareValidator comparing one control to another, and
    //            no RequiredFieldValidator anywhere in that file. The code-behind is the same story:
    //            ModuleSettings.ascx.vb L367-L375 parses each field on its own - "If txtStartDate.Text <> ''
    //            Then objModule.StartDate = Convert.ToDateTime(txtStartDate.Text) Else objModule.StartDate =
    //            Null.NullDate", and the identical block for the end date - and never compares the two. A
    //            window ending before it begins was therefore ACCEPTED and STORED verbatim by the legacy
    //            application, which merely rendered the module in no period at all.
    //
    //            Reinstating the check would be a NARROWING: identical input that the legacy application
    //            accepted would be refused, which the behaviour-preservation obligation (AAP Rule T5, MC3)
    //            forbids as squarely as it forbids a widening, and MC4 requires the validation rules to match
    //            rather than to improve on what was measured. The same reasoning already governs the two
    //            neighbouring fields on this contract, whose legacy validators are likewise type-only: a
    //            negative cache period and a border outside the range its own error message advertises are
    //            both admitted, and both are pinned by tests. Nothing is silently absorbed - the storability
    //            bound on each date remains, enforced field by field by the request validator, because
    //            SQL Server's datetime column genuinely cannot hold a value below its calendar and that is a
    //            property of the store rather than a rule invented here.
    /// <summary>
    /// Outcome of normalising one submitted settings store: either the reason it is unusable, or the
    /// map to write.
    /// </summary>
    /// <param name="Reason">
    /// The reason the submission cannot be stored, or <see langword="null"/> when it can.
    /// </param>
    /// <param name="Values">
    /// The case-insensitive map to write. Empty whenever <paramref name="Reason"/> is present, because a
    /// refused submission contributes nothing.
    /// </param>
    /// <remarks>
    /// MIGRATION: this pair exists so that the normalising helper reports its outcome through its RETURN
    /// VALUE rather than through an argument it writes back. AAP 0.7.4 states that no out or by-reference
    /// parameter appears in the target, and the rule is honoured here rather than being treated as
    /// applying only to the public surface: an argument that is really a second return value is the exact
    /// idiom the migration set out to remove, and a private member is not a licence to keep it.
    /// </remarks>
    private readonly record struct NormalisedSettings(
        ResultReason? Reason,
        Dictionary<string, string> Values);

    /// <summary>
    /// Normalises a submitted settings map, rejecting anything the columns cannot hold.
    /// </summary>
    /// <param name="submitted">The desired state of one settings store.</param>
    /// <param name="valueMaximumLength">Maximum storable value length for that store.</param>
    /// <param name="scope">Word naming the store, used in the reported message.</param>
    /// <returns>
    /// The map to write, or the reason the submission is unusable. A refused submission yields an empty
    /// map, so a caller that ignores the reason cannot write a partially validated set.
    /// </returns>
    private static NormalisedSettings NormaliseSettings(
        IReadOnlyDictionary<string, string> submitted,
        int valueMaximumLength,
        string scope)
    {
        // Names are compared without regard to case, matching the legacy Hashtable's behaviour and the
        // case-insensitive collation the settings tables use, so two submitted keys differing only in case
        // resolve to the one stored row rather than racing to overwrite each other.
        var normalised = new Dictionary<string, string>(submitted.Count, StringComparer.OrdinalIgnoreCase);
        var refused = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> pair in submitted)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return new NormalisedSettings(
                    new ResultReason(
                        SettingInvalidCode,
                        FormattableString.Invariant($"A {scope} setting name must not be blank.")),
                    refused);
            }

            if (pair.Key.Length > SettingNameMaximumLength)
            {
                return new NormalisedSettings(
                    new ResultReason(
                        SettingInvalidCode,
                        FormattableString.Invariant(
                            $"The {scope} setting name \"{pair.Key}\" exceeds {SettingNameMaximumLength} characters.")),
                    refused);
            }

            // MIGRATION: a null value becomes the empty string rather than being rejected or stored as null.
            // Null.vb defines NullString as "" rather than as a null reference, and both legacy settings
            // readers - ModuleController.vb L1237 and L1336 - substituted "" for a DBNull column, so the
            // empty string is the legacy representation of an absent setting value and is preserved as such.
            string value = pair.Value ?? string.Empty;
            if (value.Length > valueMaximumLength)
            {
                return new NormalisedSettings(
                    new ResultReason(
                        SettingInvalidCode,
                        FormattableString.Invariant(
                            $"The value of the {scope} setting \"{pair.Key}\" exceeds {valueMaximumLength} characters.")),
                    refused);
            }

            normalised[pair.Key] = value;
        }

        return new NormalisedSettings(null, normalised);
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
            TabModule? named = await _modules.GetTabModuleByIdAsync(addressed, cancellationToken).ConfigureAwait(false);
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
            : await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

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

        // The repository returns recycled pages, because the legacy read applied no predicate to
        // IsDeleted and projected the column instead. A content page offered as a placement target must
        // not be one sitting in the recycle bin, so that exclusion is applied here alongside the
        // administrative one rather than asked of the contract.
        IReadOnlyList<Tab> tabs = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        int? adminTabId = portal?.AdminTabId;
        return tabs
            .Where(tab => !tab.IsDeleted && !IsAdministrative(tab, adminTabId))
            .ToList();
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
    /// Turns a submitted position into the position that is actually stored, resolving the append
    /// instruction against the pane it is being appended to.
    /// </summary>
    /// <param name="tabId">The page whose pane is being positioned within.</param>
    /// <param name="paneName">The pane being positioned within.</param>
    /// <param name="requested">The position the caller submitted, which may be the append instruction.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// <paramref name="requested"/> unchanged when it names a position, or the next position at the
    /// bottom of the pane when it is the append instruction.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces <c>ModuleController.UpdateModuleOrder</c> (ModuleController.vb:L1160-L1173),
    /// which read the pane's occupied positions through <c>GetTabModuleOrder(TabId, PaneName)</c> and
    /// added the step. An EMPTY pane yields position one, and that is the legacy arithmetic rather than a
    /// special case invented here: the legacy loop left its variable at the incoming -1 when the reader
    /// returned no rows and then added two, so -1 + 2 = 1. The same subtraction is reproduced by seeding
    /// the running maximum with the sentinel, so one expression covers both the empty and the occupied
    /// pane exactly as the legacy one did.
    /// </para>
    /// <para>
    /// MIGRATION: the resolution happens BEFORE the row is written, whereas the legacy resolved it
    /// immediately AFTER writing the row and then issued a second statement to correct it. The outcome
    /// stored is identical and the sentinel is never durable in either arrangement; doing it first
    /// removes a write and, more importantly, removes the window in which a concurrent reader could
    /// observe the sentinel as though it were a position.
    /// </para>
    /// <para>
    /// MIGRATION: the running maximum is taken with <c>Max</c> rather than by reading the last row the
    /// repository returns. The legacy loop took the last row read and relied entirely on the
    /// procedure's <c>order by ModuleOrder</c> (03.00.01.SqlDataProvider:L495-L507) to make that row the
    /// greatest, which the repository's own ordering reproduces - so the two agree today. Taking the
    /// maximum explicitly states what the value has to be, and cannot be broken later by a change to an
    /// ordering that no longer looks load-bearing.
    /// </para>
    /// <para>
    /// The read is per pane and per page, because that is the key the legacy resolver took. Two
    /// placements of one module on two different pages therefore each land at the bottom of their own
    /// pane rather than sharing a position computed for one of them.
    /// </para>
    /// </remarks>
    private async Task<int> ResolvePositionAsync(
        int tabId,
        string paneName,
        int requested,
        CancellationToken cancellationToken)
    {
        if (requested != AppendPositionSentinel)
        {
            return requested;
        }

        IReadOnlyList<TabModule> occupied = await _modules
            .GetTabModuleOrderAsync(tabId, paneName, cancellationToken)
            .ConfigureAwait(false);

        int highest = occupied.Count == 0
            ? AppendPositionSentinel
            : occupied.Max(placement => placement.ModuleOrder);

        return highest + PositionStep;
    }

    /// <summary>
    /// Places a module on every content page it is missing from.
    /// </summary>
    /// <param name="portalId">The portal being fanned out across.</param>
    /// <param name="module">The module being placed.</param>
    /// <param name="template">The placement whose settings the new placements copy.</param>
    /// <param name="requestedPosition">
    /// The position the caller submitted, forwarded unresolved so that an append instruction is resolved
    /// against each target page's own pane rather than reusing the position computed for the addressed
    /// page. When the caller named a position instead, every new placement takes that position, which is
    /// what copying a template means.
    /// </param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements added.</returns>
    /// <remarks>
    /// MIGRATION - discovered legacy defect, not inherited. The legacy copy-to-another-page path passed
    /// the append instruction straight to the insert under the comment "Add a copy of the module to the
    /// bottom of the Pane for the new Tab" (ModuleController.vb:L712) and then, unlike both the add and
    /// the update paths, never called the resolver - so the sentinel stayed in the row until some later
    /// renumbering of that page happened to overwrite it. The stated intent is honoured here and the
    /// omission is not reproduced.
    /// </remarks>
    private async Task<int> PlaceOnContentTabsAsync(
        int portalId,
        Module module,
        TabModule template,
        int requestedPosition,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> existing =
            await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        var placed = existing.Select(placement => placement.TabId).ToHashSet();
        int added = 0;

        foreach (Tab target in await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            if (!placed.Add(target.TabId))
            {
                continue;
            }

            int position = await ResolvePositionAsync(
                target.TabId,
                template.PaneName,
                requestedPosition,
                cancellationToken).ConfigureAwait(false);

            await _modules
                .AddTabModuleAsync(
                    new TabModule
                    {
                        TabId = target.TabId,
                        ModuleId = module.ModuleId,
                        PaneName = template.PaneName,
                        ModuleOrder = position,
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
                    },
                    cancellationToken)
                .ConfigureAwait(false);

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
            await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

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
        // The whole placement-scoped collection goes, so this is the bulk removal the legacy provider
        // exposed for exactly this case rather than a read followed by a row-at-a-time loop.
        await _modules
            .DeleteTabModuleSettingsAsync(placement.TabModuleId, cancellationToken)
            .ConfigureAwait(false);

        await _modules
            .DeleteTabModuleAsync(placement.TabId, placement.ModuleId, cancellationToken)
            .ConfigureAwait(false);
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

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every
        // live instance so it could locate one by its definition.
        IReadOnlyList<Module> instances =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        Module? host = instances.FirstOrDefault(candidate =>
            !candidate.IsDeleted
            && candidate.ModuleDefinitionId == siteSettings.ModuleDefinitionId);

        if (host is null)
        {
            return false;
        }

        IReadOnlyList<ModuleSetting> stored =
            await _modules.GetModuleSettingsAsync(host.ModuleId, cancellationToken).ConfigureAwait(false);

        await UpsertSettingAsync(stored, host.ModuleId, DefaultModuleSettingName, moduleId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
        await UpsertSettingAsync(stored, host.ModuleId, DefaultTabSettingName, tabId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Writes one module setting, updating the stored row when it already exists.
    /// </summary>
    /// <param name="stored">The rows already held against the module.</param>
    /// <param name="moduleId">The module the setting belongs to.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="settingValue">The value to store.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the write is staged.</returns>
    private async Task UpsertSettingAsync(
        IReadOnlyList<ModuleSetting> stored,
        int moduleId,
        string settingName,
        string settingValue,
        CancellationToken cancellationToken)
    {
        ModuleSetting? existing = stored.FirstOrDefault(candidate =>
            string.Equals(candidate.SettingName, settingName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.SettingValue = settingValue;
            return;
        }

        await _modules
            .AddModuleSettingAsync(
                new ModuleSetting
                {
                    ModuleId = moduleId,
                    SettingName = settingName,
                    SettingValue = settingValue,
                },
                cancellationToken)
            .ConfigureAwait(false);
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

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every
        // live instance so it could locate one by its definition.
        IReadOnlyList<Module> instances =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        int copied = 0;
        foreach (Module candidate in instances.Where(module => !module.IsDeleted))
        {
            IReadOnlyList<TabModule> placements =
                await _modules.GetTabModulesByModuleIdAsync(candidate.ModuleId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Records one module change on the audit trail.
    /// </summary>
    /// <param name="portalId">The tenant the module belongs to.</param>
    /// <param name="moduleId">The module the record is about.</param>
    /// <param name="facts">
    /// The facts describing what happened. Callers pass identifiers, counts and sizes only.
    /// </param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy trail was written by <c>EventLogController.AddLog</c> against
    /// <c>EventLogType.MODULE_UPDATED</c> - the single <c>AddLog(</c> site in the Modules tree, at
    /// <c>EventMessageProcessor.vb</c> L69, which recorded the business controller class, the version and
    /// the upgrade results as named properties. The event-log storage provider is out of scope, so the
    /// record is emitted through <see cref="IAuditSink"/>, whose Infrastructure implementation writes it
    /// as a structured Serilog event. The event name is preserved verbatim, so the trail remains
    /// queryable by the identifier an operator already knows.
    /// </para>
    /// <para>
    /// <b>WHAT IS NEVER RECORDED.</b> No caller passes content. An export or import payload is module
    /// content: it may be arbitrarily large and it may carry data belonging to the portal's users, so the
    /// trail carries its LENGTH and never any part of it. That is the one rule this helper exists to make
    /// unmissable - a record naming a payload would move user data into the log store, where it is
    /// neither access-controlled as module content nor removable with it. Credentials, hashes, tokens and
    /// connection strings never reach this service at all, so there is nothing here that could leak one.
    /// </para>
    /// <para>
    /// <b>CALLED ONLY AFTER THE COMMIT.</b> A record written before the commit could describe a change
    /// that was then rolled back, which is worse than no record: it is a trail that disagrees with the
    /// database. Every call site below sits after its unit of work has been saved.
    /// </para>
    /// <para>
    /// The actor is carried only when the caller is authenticated. An unauthenticated caller reaching a
    /// write is already refused by the permission checks above, so a null actor here means a genuinely
    /// anonymous path rather than a missing lookup, and recording it as absent is more honest than
    /// recording a placeholder identifier.
    /// </para>
    /// </remarks>
    private void RecordModuleAudit(int portalId, int moduleId, IReadOnlyDictionary<string, string?> facts)
    {
        AuditEvent record = new(ModuleUpdatedEventName)
        {
            PortalId = portalId,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ActorUserName = _currentUser.IsAuthenticated ? _currentUser.UserName : null,
            ResourceType = ModuleResourceType,
            ResourceId = moduleId.ToString(CultureInfo.InvariantCulture),
            Properties = facts,
        };

        _audit.Record(record);
    }
}
