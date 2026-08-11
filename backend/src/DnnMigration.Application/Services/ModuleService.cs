// MIGRATION: this service replaces the module half of the legacy ModuleController, whose members were all
// Shared, together with the business rules that lived in the three admin code-behinds
// Website/admin/Modules/{ModuleSettings,Export,Import}.ascx.vb. Every member below is an instance method
// reached through an injected interface, so the legacy static surface and its ambient HttpContext
// dependencies are gone.
//
// MIGRATION: the legacy hand-rolled row hydration is not reproduced. ModuleController.vb instantiated an
// entity and then assigned each column through Convert.ToInt32(Null.SetNull(dr("Column"), currentValue)),
// one line per column.
using System.Globalization;
using System.Text;
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
/// Orchestrates the module aggregate: the catalogue of installed definitions, the module instances
/// a portal has created from them, the pages those instances are placed on, the two key/value
/// settings stores that hang off a module and off a placement, and the portable-content export and
/// import pair.
/// </summary>
/// <remarks>
/// <para>
/// A module and its placement are separate records here, so a module placed on several pages has
/// one module row and one placement row per page.
/// </para>
/// <para>
/// Every multi-table write commits exactly once through <see cref="IUnitOfWork"/>, so a module can
/// never exist without the placement it was created with and a fan-out across pages can never be
/// left half applied.
/// </para>
/// </remarks>
public sealed class ModuleService : IModuleService
{
    private const string PortalNotFoundCode = "module.portal_not_found";

    private const string RequestInvalidCode = "module.request_invalid";

    /// <summary>
    /// Reported when the placement a request addresses does not exist: a placement identifier
    /// naming a row that belongs to another module, or a page the addressed module is not placed
    /// on.
    /// </summary>
    /// <remarks>
    /// One code covers both because they are one condition seen through the two ways a caller can
    /// name a placement - by its own identifier on the read and settings paths, by the page it sits
    /// on for a full replacement.
    /// </remarks>
    private const string PlacementNotFoundCode = "module.placement_not_found";

    private const string NotFoundCode = "module.not_found";

    /// <summary>
    /// Reported when the caller holds no edit grant on the page or module a mutation targets.
    /// </summary>
    /// <remarks>
    /// The <c>forbidden</c> token is what the shared status translator classifies as a 403, so the
    /// code itself - rather than a status written out at a call site - is what decides how this
    /// refusal reaches the caller. The message names the target but never says WHY the grant is
    /// missing, and a target that does not exist produces this same refusal rather than a
    /// not-found: distinguishing the two would tell an unauthorised caller which identifiers exist.
    /// </remarks>
    private const string EditForbiddenCode = "module.edit_forbidden";

    /// <summary>
    /// Reported when a caller asks for one of the four portal-wide effects without administering
    /// the portal.
    /// </summary>
    /// <remarks>
    /// A code of its own rather than a reuse of <see cref="EditForbiddenCode"/>, because the two
    /// refusals mean different things to a client and are corrected differently: the edit refusal
    /// says "you may not touch this module", while this one says "you may edit this module, but not
    /// in a way that reaches pages you do not administer". The <c>forbidden</c> token is what the
    /// shared status translator classifies as a 403, so the code itself decides the status.
    /// </remarks>
    private const string AdministratorForbiddenCode = "module.administrator_forbidden";

    /// <summary>
    /// Reported when the named definition does not exist or is not available to the portal.
    /// </summary>
    private const string DefinitionNotFoundCode = "module.definition_not_found";

    private const string TabNotFoundCode = "module.tab_not_found";

    /// <summary>
    /// Reported when a relocation names a destination this portal cannot move a module onto - a page that
    /// does not exist here, is deleted, is administrative, or is asked for while the module is set to appear
    /// on every page and so has no single page to be moved off.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PlacementNotFoundCode"/> on purpose. That reason answers "the page you named
    /// does not hold this module", which is a statement about the placement being SELECTED; this one answers
    /// "the page you named cannot receive this module", which is a statement about the DESTINATION. Reporting
    /// both through one code is what made the previous behaviour unreadable to a caller.
    /// </remarks>
    private const string MoveDestinationInvalidCode = "module.move_destination_invalid";

    private const string SettingInvalidCode = "module.setting_invalid";

    /// <summary>
    /// Reported when the generic settings surface is asked to read or mutate security-owned
    /// settings.
    /// </summary>
    /// <remarks>
    /// The <c>protected</c> token is intentionally part of the code because the API result
    /// translator maps that token to HTTP 403.
    /// </remarks>
    private const string SettingsProtectedCode = "module.settings_protected";

    private const string NotPortableCode = "module.not_portable";

    private const string ContentInvalidCode = "module.content_invalid";

    /// <summary>
    /// Reported when a submitted content document names a module type that is not the target
    /// module's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: restores the fourth outcome of the legacy import screen, "The import file
    /// specified is not the correct type for this module"
    /// (<c>Website/admin/Modules/Import.ascx.vb</c>, reported from both). Distinct from
    /// <see cref="ContentInvalidCode"/> because the document is perfectly well-formed and perfectly
    /// valid - it is simply somebody else's.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen sourced this refusal from the document's FILE NAME - it tested
    /// whether the name contained the cleaned module name - and additionally from the type
    /// attribute once the file was open.
    /// </para>
    /// </remarks>
    private const string ContentTypeMismatchCode = "module.content_type_mismatch";

    /// <summary>
    /// Reported when a module hands over content that cannot be carried by the export document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="ContentInvalidCode"/> on purpose, and the distinction is about WHO
    /// can put it right.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy export wrapped its whole document assembly in <c>Try</c> with a
    /// <c>Catch</c> body of <c>'ignore errors</c> (ModuleController.vb), so a module whose content
    /// could not be written produced a portal template with the content element silently missing
    /// and reported success.
    /// </para>
    /// </remarks>
    private const string ExportFailedCode = "module.export_failed";

    /// <summary>
    /// Carried on an otherwise successful update whose blast radius exceeded the addressed module.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy screen recorded nothing when a save named a portal default or
    /// propagated appearance, even though both touch records the operator never addressed.
    /// </remarks>
    private const string WideEffectCode = "module.update.wide_effect";

    /// <summary>
    /// Maximum length of a setting name in both <c>dbo.ModuleSettings</c> and
    /// <c>dbo.TabModuleSettings</c>; both declare <c>SettingName nvarchar(50) NOT NULL</c>.
    /// </summary>
    private const int SettingNameMaximumLength = 50;

    /// <summary>
    /// Maximum length of <c>dbo.ModuleSettings.SettingValue</c>, declared <c>nvarchar(2000)</c> by
    /// the terminal schema.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <b>the width is 2000, not 256.</b> The 01.00.00 create script did declare
    /// <c>nvarchar(256)</c>, but the later chain does widen it: <c>01.00.08.SqlDataProvider</c>
    /// destroy and rebuild the whole table through a <c>Tmp_ModuleSettings</c> copy declaring
    /// <c>SettingValue nvarchar(2000) NOT NULL</c>, and no subsequent script narrows it.
    /// </remarks>
    private const int ModuleSettingValueMaximumLength = 2000;

    /// <summary>
    /// Maximum length of <c>dbo.TabModuleSettings.SettingValue</c>, declared <c>nvarchar(2000)</c>
    /// by the 03.00.01 create script.
    /// </summary>
    private const int PlacementSettingValueMaximumLength = 2000;

    /// <summary>
    /// The submitted position that means "put this module at the bottom of its pane" rather than
    /// naming a position.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this value is a COMMAND on the request contract and must never reach a column.
    /// The legacy documentation states it outright - <c>UpdateModuleOrder</c>'s own parameter
    /// comment reads "position within the controls list on page, -1 if to be added at the end"
    /// (ModuleController.vb) - and the legacy code acted on it as a command in two places, both
    /// immediately after writing the row: <c>AddModule</c> tested
    /// <c>If objModule.ModuleOrder = -1 Then UpdateModuleOrder(...)</c> and <c>UpdateModule</c>
    /// called the same resolver unconditionally.
    /// </remarks>
    private const int AppendPositionSentinel = -1;

    /// <summary>
    /// The gap the legacy renumbering pass left between two adjacent positions in one pane.
    /// </summary>
    /// <remarks>MIGRATION: measured, not chosen.</remarks>
    private const int PositionStep = 2;

    /// <summary>Largest number of settings either scope may carry in one submission.</summary>
    /// <remarks>
    /// MIGRATION: a net-new bound with no legacy counterpart, and generous by design: seventy-two
    /// distinct setting names exist across EVERY bundled module in the whole legacy application, so
    /// this admits more than three times that vocabulary for a single module. It bounds row growth
    /// and transaction size, not any real configuration.
    /// </remarks>
    private const int SettingsPerScopeMaximum = 250;

    /// <summary>
    /// Largest number of settings the two scopes may carry between them in one submission.
    /// </summary>
    /// <remarks>
    /// MIGRATION: deliberately lower than twice the per-scope bound, so that it binds when both
    /// maps are large rather than being implied by them. This is the figure that bounds one
    /// transaction's row count, which the per-scope bounds alone do not protect.
    /// </remarks>
    private const int SettingsAggregateMaximum = 400;

    /// <summary>
    /// Friendly name of the module instance that holds a portal's own settings rows.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>PortalSettings.UpdateSiteSetting</c> (PortalSettings.vb) resolved this module
    /// by friendly name and then wrote an ordinary <c>dbo.ModuleSettings</c> row against it, so
    /// what the legacy screen called a "portal level" key has always been a module setting on a
    /// well-known module instance.
    /// </remarks>
    private const string SiteSettingsDefinitionName = "Site Settings";

    private const string DefaultModuleSettingName = "defaultmoduleid";

    private const string DefaultTabSettingName = "defaulttabid";

    /// <summary>Cache key holding a portal's projected definition catalogue.</summary>
    /// <remarks>MIGRATION: this key is new rather than carried over.</remarks>
    private const string DefinitionCatalogueCacheKeyFormat = "ModuleDefinitions{0}";

    /// <summary>
    /// Base expiry of the definition catalogue, matching every module-related legacy timeout.
    /// </summary>
    private const int DefinitionCatalogueCacheTimeOutMinutes = 20;

    private const string ContentElementName = "content";

    private const string ContentTypeAttributeName = "type";

    private const string ContentVersionAttributeName = "version";

    /// <summary>Maximum number of characters accepted in one portable-content XML document.</summary>
    /// <remarks>TAKEN FROM THE PUBLISHED CONTRACT RATHER THAN RESTATED.</remarks>
    private const long ImportDocumentCharacterMaximum = ModuleImportRequest.ContentCharacterMaximum;

    private const int ImportDocumentNodeMaximum = 10_000;

    private const int ImportDocumentDepthMaximum = 64;

    private const int ImportElementAttributeMaximum = 64;

    private const int ImportTextNodeCharacterMaximum = 262_144;

    private const string ImportParseFailureMessage =
        "The submitted document could not be parsed safely as portable module content.";

    /// <summary>
    /// The declaration an exported document opens with, reproduced character for character from the
    /// legacy export screen - including the space before the closing angle bracket pair.
    /// </summary>
    /// <remarks>
    /// <c>Website/admin/Modules/Export.ascx.vb</c> emits
    /// <c>"&lt;?xml version=""1.0"" encoding=""utf-8"" ?&gt;"</c> as a literal. It is a literal
    /// here too, rather than being produced by an XML writer, because a writer normalises the
    /// spacing and the whole point of this member is that a document leaving this endpoint is
    /// byte-comparable with one the legacy application wrote.
    /// </remarks>
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>";

    /// <summary>The punctuation the legacy name sanitiser removes, in its own order.</summary>
    /// <remarks>
    /// Reproduced character for character from <c>Website/admin/Modules/Export.ascx.vb</c>, which
    /// declares <c>". ~`!@#$%^&amp;*-_+={[}]|\:;&lt;,&gt;?/" &amp; Chr(34) &amp; Chr(39)</c> - the
    /// set ending in the double quote and the apostrophe. Note what the set does NOT contain: the
    /// leading character is a full stop and there is no closing angle bracket omission - every
    /// character is removed rather than substituted for, so the sanitiser shortens a name and never
    /// lengthens it.
    /// </remarks>
    private const string NamePunctuation = ". ~`!@#$%^&*()-_+={[}]|\\:;<,>?/\"'";

    /// <summary>
    /// The characters the legacy portability screens stripped out of a module name before writing
    /// it into a document's type attribute or comparing it against one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: measured character for character from <c>CleanName</c>, which both portability
    /// screens declared identically - <c>Website/admin/Modules/Export.ascx.vb</c> and
    /// <c>Website/admin/Modules/Import.ascx.vb</c>, each as
    /// <c>". ~`!@#$%^&amp;*-_+={[}]|\:;&lt;,&gt;?/" &amp; Chr(34) &amp; Chr(39)</c>. The two
    /// appended characters are the double and single quotation marks, spelled as character codes in
    /// the source because the surrounding literal could not carry them.
    /// </para>
    /// <para>
    /// The set is reproduced rather than replaced by a general-purpose rule, because it is part of
    /// the FILE FORMAT: the value it produces is what the legacy exporter wrote into the attribute
    /// and what the legacy importer compared against, so a document written here stays readable by
    /// a legacy installation and vice versa.
    /// </para>
    /// </remarks>
    private const string ContentTypeStrippedCharacters = ". ~`!@#$%^&*()-_+={[}]|\\:;<,>?/\"'";

    /// <summary>
    /// Longest caller-supplied provenance value recorded on an import audit event.
    /// </summary>
    /// <remarks>
    /// The submitted folder and document names are recorded because the contract promises the
    /// provenance, but they are caller-controlled and the request applies no length bound of its
    /// own, so a bound is applied HERE rather than trusted upstream.
    /// </remarks>
    private const int AuditProvenanceMaximumLength = 128;

    /// <summary>Longest declared document version recorded on an import audit event.</summary>
    /// <remarks>
    /// <c>DesktopModules.Version</c> is a six-character column, so a legitimate value is far
    /// shorter than this; the allowance exists only so a mismatched document is described rather
    /// than discarded.
    /// </remarks>
    private const int AuditVersionMaximumLength = 32;

    /// <summary>Marker appended to an audited value that was longer than its bound.</summary>
    /// <remarks>
    /// A truncated value must not read as a complete one, or an operator reading the trail would
    /// draw a conclusion from a value that was never submitted in that form.
    /// </remarks>
    private const string AuditTruncationMarker = "...";

    /// <summary>
    /// Largest content, in characters, that an export will carry out of a module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One mebibyte of characters, which is the API's own request-body allowance restated here:
    /// that figure is the largest content this application is willing to move in either direction,
    /// and an export beyond it could not be handed back to the import endpoint anyway.
    /// </para>
    /// <para>
    /// The ceiling exists because an export payload arrives from MODULE code rather than from the
    /// caller's request, so no request-body limit bounds it, and every step after it - the document
    /// assembly and the well-formedness parse that checks it - is proportional to its length.
    /// </para>
    /// </remarks>
    private const int ExportPayloadMaximumLength = 1024 * 1024;

    private const int UnpagedPageSize = 0;

    /// <summary>Attribution used when an import arrives without an authenticated caller.</summary>
    /// <remarks>
    /// <c>dbo.Users.UserID</c> is <c>IDENTITY(1,1)</c>, so no real account can own this value and
    /// it cannot be mistaken for one. Zero is not used because zero is a legitimate identifier
    /// elsewhere in this schema.
    /// </remarks>
    private const int UnattributedUserId = -1;

    /// <summary>
    /// Greatest length of a version string this service will carry into an audit record.
    /// </summary>
    /// <remarks>
    /// The value is read from an attribute of a caller-supplied document, so it is neither
    /// validated by the request contract nor bounded by the schema. A version is a handful of
    /// characters in every real package, so a ceiling this generous can only be exceeded
    /// deliberately.
    /// </remarks>
    private const int MaximumAuditedVersionLength = 32;

    private const string UnusableVersion = "(unusable)";

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
    /// <param name="tabs">
    /// Page lookups, needed to validate placements and to fan out across pages.
    /// </param>
    /// <param name="portals">Portal existence and the administrative page identifier.</param>
    /// <param name="unitOfWork">The single commit point for every write below.</param>
    /// <param name="cache">Cache reads and invalidation.</param>
    /// <param name="currentUser">The caller.</param>
    /// <param name="permissions">
    /// Evaluates whether the caller may edit the page a module is being created on, or the module
    /// content is being imported into.
    /// </param>
    /// <param name="businessControllers">
    /// Resolution of a module's own portable-content contract.
    /// </param>
    /// <param name="audit">The audit trail.</param>
    /// <param name="caching">
    /// Bound caching configuration supplying the performance multiplier.
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

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// One row is emitted per placement, so a module placed on four pages contributes four rows,
    /// each carrying its own placement identifier.
    /// </para>
    /// <para>
    /// MIGRATION: THE FILTERS, THE ORDERING, THE COUNT AND THE WINDOW ALL MOVED INTO THE STATEMENT,
    /// and the reason is that paging the response is not the same thing as paging the work. This
    /// member used to read every module the tenant owns, read every placement of every module that
    /// survived the filters, expand and order the complete row set, and only then take its window -
    /// so a request for ten rows performed the reads, the allocation and the sort of the entire
    /// tenant, and the eager graph the module read carries meant the definition and package of
    /// every one of those modules was hydrated too.
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

        // One window of placement rows, with its total, both established by the store over the same filtered
        // set - so the two cannot disagree, and neither costs a read of the tenant.
        PagedResult<TabModule> window = await _modules.ListPlacementsAsync(
            portalId,
            tabId,
            includeDeleted,
            request.HasQuery ? request.Query : null,
            request.HasSort ? request.SortBy : null,
            request.SortDir == SortDirection.Descending,
            request.PageIndex,
            request.PageSize,
            cancellationToken).ConfigureAwait(false);

        bool unpaged = request.PageSize == UnpagedPageSize;

        // Read once, and only when there is a row to name, and narrowed to the definitions the WINDOW
        // actually references. A window that lands past the end of the collection projects nothing, so it
        // asks the definition catalogue nothing either.
        IReadOnlyDictionary<int, ModuleCatalogueFacts> catalogue = window.Items.Count == 0
            ? new Dictionary<int, ModuleCatalogueFacts>()
            : await ReadCatalogueFactsAsync(
                portalId,
                window.Items
                    .Select(row => row.Module.ModuleDefinitionId)
                    .Distinct()
                    .ToList(),
                cancellationToken).ConfigureAwait(false);

        var items = new List<ModuleListItemDto>(window.Items.Count);
        foreach (TabModule row in window.Items)
        {
            items.Add(ModuleMappings.ToListItem(
                row.Module,
                row,
                ResolveCatalogue(row.Module, catalogue)));
        }

        // The envelope is handed the figures unaltered: the total is the number of rows in the whole
        // collection and the declared size is the size the caller asked for. Neither is widened to
        // accommodate the projection, because the projection is what the window was taken over.
        return Result<PagedResult<ModuleListItemDto>>.Success(
            unpaged
                ? PagedResult<ModuleListItemDto>.Unpaged(items)
                : PagedResult<ModuleListItemDto>.Create(
                    items,
                    window.TotalCount,
                    request.PageIndex,
                    request.PageSize));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A module the portal does not own, and a module with no placement to address, both read as an
    /// absence rather than a failure. Naming a placement that belongs to another module is a
    /// different matter and is reported, because the caller supplied an identifier that does not
    /// fit.
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

        IReadOnlyDictionary<int, ModuleCatalogueFacts> catalogue =
            await ReadCatalogueFactsAsync(portalId, wantedDefinitionIds: null, cancellationToken).ConfigureAwait(false);

        return Result<ModuleDetailDto?>.Success(
            ModuleMappings.ToDetail(module, placement, ResolveCatalogue(module, catalogue)));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The module and the placement it was created with are written as one unit of work, so neither
    /// can outlive the other.
    /// </para>
    /// <para>
    /// "Every page" means every content page. The legacy screen fanned out over
    /// <c>PortalSettings.DesktopTabs</c> (ModuleSettings.ascx.vb), which is the portal's non
    /// administrative page set, and an administrative page acquiring a content module was never a
    /// reachable outcome.
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

        DesktopModule? package = definition is null
            ? null
            : await _definitions
                .GetDesktopModuleByIdAsync(definition.DesktopModuleId, cancellationToken)
                .ConfigureAwait(false);

        // The repository is the primary enforcement point, but the service independently refuses an
        // administrative package so a stale cache, alternate repository implementation or future query
        // regression cannot turn host administration functionality into portal-placeable content.
        if (definition is null || package is null || package.IsAdmin)
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
        // than by a policy because the page arrives in the BODY, which no route-reading policy can see;
        // tenant ownership alone is not sufficient, or any authenticated caller could place a module on any
        // tenant's page.
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

                // Resolved against the TARGET page's pane rather than copied from the addressed one, because
                // the legacy resolver was keyed on (TabId, PaneName): appending to one pane says nothing
                // about where the bottom of another page's pane is. No two placements created here share a
                // page, so each read sees a settled pane even though none of them is saved yet.
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

        // The definition and its package were both resolved above, before anything was staged, so the
        // creation response projects from the SAME five values a subsequent single read of this module will
        // project. Reading them off the new entity's navigations instead is what previously made the
        // creation response report a fabricated package key of zero and no package name at all: a module
        // just constructed in memory has no navigations loaded, and nothing had populated them.
        return Result<ModuleDetailDto>.Success(
            ModuleMappings.ToDetail(module, placement, ModuleCatalogueFacts.From(definition, package)));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// This member carries the module's own change plus up to four wider effects, and all of them
    /// commit together.
    /// </para>
    /// <para>
    /// Switching the all-pages flag also moves placements, which the legacy screen performed after
    /// the module row was written, with the comment that the controller assumes every module update
    /// has already been carried out.
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

        // THE PLACEMENT IS SELECTED BY THE PAGE THE REQUEST NAMES, and this is the one member of the update
        // contract whose omission cannot be recovered from.
        //
        // MIGRATION: THE SUBMITTED PAGE KEY USED TO BE READ BY NOTHING AT ALL. UpdateModuleRequest.TabId is
        // documented as required and as the key identifying WHICH placement is being updated, and
        // ModuleMappings.ApplyUpdate documents that it deliberately does not assign the value because "the
        // application service resolves the placement before calling here" - yet this method resolved the
        // placement with the lowest TabModuleId and never looked at the request.
        TabModule? placement = await ReadPlacementOnPageAsync(module, request.TabId, cancellationToken)
            .ConfigureAwait(false);

        if (placement is null)
        {
            return Result<ModuleDetailDto?>.Failure(
                PlacementNotFoundCode,
                FormattableString.Invariant(
                    $"Module {moduleId} is not placed on page {request.TabId} in portal {portalId}."));
        }

        bool wasPlacedEverywhere = module.AllTabs;

        // SEC: THE ADMINISTRATOR-ONLY FIELDS ARE AUTHORISED HERE, BEFORE ANY OF THEM IS APPLIED. The legacy
        // settings screen disabled cboTab, chkAllTabs, chkDefault and chkAllModules outright for any caller
        // outside the portal administrator role, at both ModuleSettings.ascx.vb, under the comment that tab
        // administrators can only manage their own tab.
        //
        // Until this gate existed the rule was documented in three places and enforced in none: the DTO said
        // authorisation would decide, the controller said the service owned the rule and reported a distinct
        // 403, and the service applied all four unconditionally. A caller holding only the module edit grant
        // on one page could therefore move a module to a page they do not administer, fan it out across
        // every page of the portal, name it as the portal's default, or rewrite the appearance of every
        // module on every content page - four portal-wide effects from a page-scoped grant.
        // A RELOCATION JOINS THIS GATE, and belongs in it for the same reason the other four do: the legacy
        // screen disabled cboTab alongside chkAllTabs, chkDefault and chkAllModules for any caller outside
        // the portal administrator role, under the comment that tab administrators can only manage their own
        // tab. Moving a module off a page the caller administers and onto one they do not is precisely the
        // page-scoped-grant escalation this gate exists to refuse.
        bool relocationRequested = request.MoveToTabId is int requestedDestination
            && requestedDestination != placement.TabId;

        if (request.AllTabs != module.AllTabs
            || request.SetAsDefaultSettings
            || request.ApplyToAllModules
            || relocationRequested)
        {
            if (await EnsureAdministersPortalAsync(portalId, cancellationToken).ConfigureAwait(false)
                is ResultReason forbidden)
            {
                // Returned BEFORE the projection runs, so not one of the four values has touched the tracked
                // entities and there is nothing staged for a later commit to pick up. A refusal that had
                // already mutated the graph would depend on nobody calling SaveChanges afterwards, which is
                // not a property this method can guarantee for its callers.
                return Result<ModuleDetailDto?>.Failure(forbidden);
            }
        }

        // SEC: THE CALLER MUST HOLD THE EDIT GRANT ON THE PAGE WHOSE PLACEMENT IT IS EDITING, which is the
        // page the request named and the placement was selected by. The revision that read the page as a
        // move destination required this grant on the destination, citing the legacy page picker only ever
        // offering pages the caller could edit; under the selecting contract the destination and the edited
        // page are the same page, so the requirement survives its withdrawn condition and applies to every
        // update rather than only to a move.
        if (await EnsureMayEditPageAsync(portalId, request.TabId, cancellationToken).ConfigureAwait(false)
            is ResultReason pageForbidden)
        {
            // Returned BEFORE the projection runs, for the same reason the field gate above returns early:
            // nothing has touched the tracked entities, so a later commit by any caller cannot pick up a
            // half-applied refusal.
            return Result<ModuleDetailDto?>.Failure(pageForbidden);
        }

        // SEC: A RELOCATION ALSO NEEDS THE GRANT ON THE DESTINATION, and the reason the check above does not
        // already cover it is that the two pages are now different values again. The legacy page picker only
        // ever offered pages the caller could edit, so the grant on the destination was enforced by the list
        // never containing anything else; a JSON contract has no such list, so the check is made explicitly.
        // Resolved and validated here, BEFORE the projection, so every refusal on this path leaves the
        // tracked entities untouched exactly as the two gates above do.
        Tab? destinationTab = null;

        if (relocationRequested)
        {
            int destinationTabId = request.MoveToTabId!.Value;

            // EXISTENCE IS SETTLED BEFORE PERMISSION, and the order is deliberate rather than incidental.
            // Asked to move a module onto a page that is not there, the page-grant check answers "the caller
            // may not place a module on page 99999" - which reads as a permission problem and sends the
            // reader looking for a role to grant, when the real answer is that the page does not exist. That
            // is the same misdirection this report raised against reading an unknown module as forbidden, and
            // it is corrected here for the same reason.
            //
            // The destination must be a page this portal actually shows content on. Reading it from the same
            // content-page set the propagation paths use is deliberate: it excludes the administration page
            // and its children, which is the set the legacy picker was built from, so a caller cannot reach
            // a page through this member that the picker would never have offered.
            //
            // Nothing is disclosed by answering in this order. A relocation has already passed the portal
            // administrator gate above, so by this line the caller administers the very portal whose pages
            // are being named, and the set is scoped to that portal alone.
            destinationTab = (await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.TabId == destinationTabId);

            if (destinationTab is null)
            {
                return Result<ModuleDetailDto?>.Failure(
                    MoveDestinationInvalidCode,
                    FormattableString.Invariant(
                        $"Page {destinationTabId} is not a content page of portal {portalId}, so a module cannot be moved onto it."));
            }

            if (await EnsureMayEditPageAsync(portalId, destinationTabId, cancellationToken).ConfigureAwait(false)
                is ResultReason destinationForbidden)
            {
                return Result<ModuleDetailDto?>.Failure(destinationForbidden);
            }

            // A module shown on EVERY page has no single page to be moved off, so the instruction has no
            // meaning here. The legacy screen expressed the same rule by skipping the move outright whenever
            // chkAllTabs was checked; this refuses instead of skipping, because silently discarding an
            // explicit instruction leaves the caller believing a relocation happened that did not.
            if (request.AllTabs)
            {
                return Result<ModuleDetailDto?>.Failure(
                    MoveDestinationInvalidCode,
                    "A module set to appear on all pages cannot also be moved to one page. "
                        + "Clear the all-pages setting first, or save the move on its own.");
            }
        }

        ModuleMappings.ApplyUpdate(module, placement, request);

        // NO MOVE IS DERIVED FROM THE SUBMITTED PAGE, and none can be: that member SELECTS the placement
        // above, so by the time control reaches this line the placement's page and the submitted page are
        // the same value by construction - a request naming a page the module does not occupy was already
        // refused with the placement-not-found reason. A relocation is carried by its own member and is
        // applied further down, after every other effect, for the ordering reason recorded there.
        //
        // The projection above assigned the submitted position verbatim, which may be the append
        // instruction, so it is resolved against the page the placement sits on.
        placement.ModuleOrder = await ResolvePositionAsync(
            placement.TabId,
            placement.PaneName,
            request.ModuleOrder,
            cancellationToken).ConfigureAwait(false);

        // BOTH pages are invalidated on a move, not just the destination. The placement list of the page the
        // module left is now wrong too, and an entry that keeps answering with a module that is no longer
        // there is the more visible of the two staleness bugs.
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
        // Settings?".
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
        // MIGRATION: the propagation this triggers is NARROWER than the legacy's. The legacy loop copied
        // NINE appearance values - alignment, colour, border, icon, visibility, container source and the
        // title, print and syndicate flags - to every module on every non-administrative page.
        if (request.ApplyToAllModules)
        {
            int copied = await PropagateAppearanceAsync(portalId, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            effects.Add(FormattableString.Invariant($"appearance copied to {copied} placement(s) on content pages"));
        }

        // THE RELOCATION IS LAST, AND THE ORDER IS THE LEGACY'S OWN INSTRUCTION rather than a preference:
        // "These Module Copy/Move statements must be at the end of the Update as the Controller code assumes
        // all the Updates to the Module have been carried out" (ModuleSettings.ascx.vb). Every effect above
        // reads or writes through the placement being edited, so moving it first would leave those effects
        // acting on a row that no longer describes where the module sits.
        //
        // The response placement is rebound because the move REPLACES the row rather than editing it. Left
        // unbound, this method would answer with the page the module has just been moved off - the one fact
        // the caller is least able to detect and most likely to act on.
        TabModule responsePlacement = placement;

        if (destinationTab is not null)
        {
            // Captured BEFORE the call. The relocation stages a delete of the row this variable describes,
            // so reading its page afterwards to build the message would be reading a fact that is on its way
            // out of the database.
            int vacatedTabId = placement.TabId;

            responsePlacement = await RelocatePlacementAsync(
                module,
                placement,
                destinationTab,
                affectedTabIds,
                cancellationToken).ConfigureAwait(false);

            effects.Add(FormattableString.Invariant(
                $"moved from page {vacatedTabId} to page {destinationTab.TabId}"));
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
                AuditEventNames.ModuleUpdated,
                portalId,
                module.ModuleId,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Operation"] = "Update",
                    // The LIVE placement, which after a relocation is the newly created row rather than the
                    // vacated one. Both identities change on a move, and a trail naming the row that was
                    // just deleted would describe a placement no reader could go and look at. Where the
                    // module came from is carried in the effect text alongside where it went.
                    ["TabModuleId"] = responsePlacement.TabModuleId.ToString(CultureInfo.InvariantCulture),
                    ["TabId"] = responsePlacement.TabId.ToString(CultureInfo.InvariantCulture),
                    ["SetAsDefaultSettings"] = request.SetAsDefaultSettings.ToString(),
                    ["ApplyToAllModules"] = request.ApplyToAllModules.ToString(),
                    ["AffectedTabCount"] = affectedTabIds.Count.ToString(CultureInfo.InvariantCulture),
                    ["EffectCount"] = effects.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        IReadOnlyDictionary<int, ModuleCatalogueFacts> catalogue =
            await ReadCatalogueFactsAsync(portalId, wantedDefinitionIds: null, cancellationToken).ConfigureAwait(false);

        ModuleDetailDto detail =
            ModuleMappings.ToDetail(module, responsePlacement, ResolveCatalogue(module, catalogue));

        return effects.Count == 0
            ? Result<ModuleDetailDto?>.Success(detail)
            : Result<ModuleDetailDto?>.Success(
                detail,
                new ResultReason(WideEffectCode, string.Join("; ", effects) + "."));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Recycling the module is a soft delete through <c>dbo.Modules.IsDeleted</c>, a
    /// <c>bit NOT NULL</c> column added by the 02.00.00 upgrade script with a default of zero, so
    /// the module and every placement it has survive and can be restored. Recycling a module that
    /// is already recycled succeeds, because a delete is idempotent.
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

        // MIGRATION: the legacy recycle bin recorded a module removal as EventLogType.MODULE_DELETED
        // (RecycleBin.ascx.vb) and this boundary recorded NOTHING, which left the one operation in this
        // service that destroys a caller's work as the only one with no trail.
        //
        // Emitted after the commit, so no record can describe a removal that was rolled back, and the facts
        // are the blast radius: whether one placement or the whole module went, which placement was
        // addressed when one was, and how many pages were affected. No caller free-text is carried.
        RecordModuleAudit(
            AuditEventNames.ModuleDeleted,
            portalId,
            moduleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = tabModuleId is null ? "Recycle" : "RemovePlacement",
                ["TabModuleId"] = tabModuleId?.ToString(CultureInfo.InvariantCulture),
                ["AffectedTabCount"] = affectedTabIds.Count.ToString(CultureInfo.InvariantCulture),
            });

        return Result.Success();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Both settings stores are carried: the module-scoped rows, which every placement of the
    /// module shares, and the rows scoped to the addressed placement alone. Anything that cannot be
    /// addressed reads as an absence, because no reason code is documented for this member.
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

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package?.IsAdmin == true)
        {
            return Result<ModuleSettingsDto?>.Failure(
                SettingsProtectedCode,
                "Administrative module settings are available only through their typed privileged endpoint.");
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

        // Security-owned names are never projected from the generic open-key contract. They are managed by
        // typed portal-administrator endpoints, whose DTOs intentionally expose only their documented
        // fields.
        IReadOnlyList<ModuleSetting> publicModuleSettings = moduleSettings
            .Where(setting => !IsSecurityOwnedSettingName(setting.SettingName))
            .ToList();
        IReadOnlyList<TabModuleSetting> publicPlacementSettings = placementSettings
            .Where(setting => !IsSecurityOwnedSettingName(setting.SettingName))
            .ToList();

        return Result<ModuleSettingsDto?>.Success(
            ModuleMappings.ToSettings(module, placement, publicModuleSettings, publicPlacementSettings));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// This is a replace-the-set write, not an add, update and delete triad: the caller submits the
    /// whole desired state of each store, the difference against what is stored is computed here,
    /// and the whole difference commits once.
    /// </para>
    /// <para>
    /// Placement-scoped work needs a placement. Submitting no placement-scoped settings and naming
    /// no placement leaves the placement store alone, because there is no way to tell which store
    /// the caller meant to empty.
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
        // MIGRATION: an absent map is REFUSED rather than thrown on. Both members are non-nullable reference
        // types carrying an initialiser on the request contract, which makes null look unreachable - but an
        // initialiser only runs when the deserialiser does not assign, and a body carrying an explicit null
        // assigns over it.
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

        // MIGRATION: cardinality is bounded before anything is read or written. Every setting name and value
        // was already bounded individually, but nothing bounded the COUNT, and the two limits multiply: a
        // body well inside the request size limit can carry tens of thousands of short settings, each
        // becoming a tracked entity and a row in one transaction.
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

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package?.IsAdmin == true)
        {
            return Result.Failure(
                SettingsProtectedCode,
                "Administrative module settings must be changed through their typed privileged endpoint.");
        }

        // MIGRATION: the two stores are normalised and length-checked SEPARATELY, against their own column
        // width and under their own scope word, because they are separate tables - dbo.ModuleSettings keyed
        // (ModuleID, SettingName) and dbo.TabModuleSettings keyed (TabModuleID, SettingName). The legacy
        // readers at ModuleController.vb each remarked that the other store was excluded, and that
        // separation is preserved here rather than collapsed into one map.
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

        string? protectedName = desiredModuleSettings.Keys
            .Concat(desiredPlacementSettings.Keys)
            .FirstOrDefault(IsSecurityOwnedSettingName);
        if (protectedName is not null)
        {
            return Result.Failure(
                SettingsProtectedCode,
                FormattableString.Invariant(
                    $"The setting name \"{protectedName}\" is reserved for a typed privileged endpoint."));
        }

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
            // set, so an empty set has to be able to clear what is stored.
            Result<TabModule?> resolved = await ResolvePlacementAsync(
                module,
                tabModuleId: null,
                mismatchCode: null,
                cancellationToken).ConfigureAwait(false);

            placement = resolved.Value;

            // A module with no placement at all still cannot hold placement-scoped settings, and a caller
            // that asked to store some is told so rather than having the request quietly succeed. A caller
            // that submitted none has nothing to be told: there is simply nothing to reconcile.
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
            // A replace request governs only the generic settings namespace. Omitting a protected setting
            // must not delete it, otherwise an editor could erase administrator-owned security policy simply
            // by submitting an otherwise valid generic settings document.
            if (IsSecurityOwnedSettingName(stored.SettingName))
            {
                continue;
            }

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
                if (IsSecurityOwnedSettingName(stored.SettingName))
                {
                    continue;
                }

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

    /// <inheritdoc/>
    /// <remarks>
    /// The catalogue is installation-time reference data, so it is read through the cache and no
    /// member of this service creates or modifies a definition.
    /// </remarks>
    public async Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListModuleDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = string.Format(CultureInfo.InvariantCulture, DefinitionCatalogueCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(
            DefinitionCatalogueCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: when the configured expiry resolves to zero the legacy callers skipped the database
        // read outright, on the grounds that the query was too expensive to repeat per request. That is not
        // reproduced: disabling caching here disables caching only, and the read still runs, because
        // returning nothing would make a configuration value silently change what the endpoint reports.
        IReadOnlyList<ModuleDefinitionDto> catalogue = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadDefinitionCatalogueAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadDefinitionCatalogueAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<ModuleDefinitionDto>>.Success(catalogue);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answered by NARROWING THE CATALOGUE rather than by reading the definition directly, and the
    /// choice is load-bearing.
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

    /// <inheritdoc/>
    /// <remarks>
    /// Narrows the catalogue for the same reason the single-definition read above does, and
    /// consequently preserves the catalogue's ordering - friendly name, then identifier - rather
    /// than imposing an order of its own.
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

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Portability is decided by the stored capability bit field, which is what the legacy export
    /// path tested: <c>objModule.BusinessControllerClass &lt;&gt; "" And objModule.IsPortable</c>
    /// (Export.ascx.vb). A module whose stored bit says portable but whose controller no
    /// registration covers is reported as not portable too, because in this deployment there is no
    /// way to ask it for content.
    /// </para>
    /// <para>
    /// MIGRATION: the document is returned to the caller instead of being written to a server path.
    /// The requested folder is accepted and deliberately unused for that reason, and the requested
    /// file name is validated so the caller can label what it receives.
    /// </para>
    /// </remarks>
    public async Task<Result<string>> ExportModuleAsync(
        int portalId,
        int moduleId,
        ModuleExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ModuleExportRequestValidator refuses a blank name at the boundary with a field-keyed answer naming
        // `fileName`, which is what the operation's published 400 schema advertises. This guard is retained
        // rather than removed because this service is reachable from callers the MVC validation filter does
        // not sit in front of - the unit suites call it directly, and so would any future in-process
        // consumer.
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

        string payload = exported.Value;

        // BOUNDED BEFORE ANY WORK IS DONE ON IT. What the module hands over is not the caller's own
        // submission - it arrives from module code and its size is decided there - so nothing upstream
        // bounds it, and everything below is document assembly and parsing proportional to it.
        //
        // MIGRATION: the legacy export had no ceiling of any kind - it wrote whatever the module returned to
        // disk - so the ceiling is a deliberate divergence, recorded in MIGRATION_NOTES.md. It is classified
        // as an export failure rather than as a bad request because the content came from the MODULE and not
        // from the caller, which is the same distinction ExportFailedCode already draws.
        if (payload.Length > ExportPayloadMaximumLength)
        {
            return Result<string>.Failure(
                ExportFailedCode,
                FormattableString.Invariant(
                    $"Module {moduleId} returned {payload.Length} characters of content, which exceeds the {ExportPayloadMaximumLength} character ceiling this endpoint will carry."));
        }

        // The caller may already have gone while the module was assembling its content, and what follows is
        // processor and memory work rather than I/O - so the token is observed here, where that work begins.
        // Without this an abandoned request paid for the whole document anyway.
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: Export.ascx.vb - the document is COMPOSED, not serialised from a tree, and the module's
        // payload is placed between the tags exactly as it was handed over. An XML writer cannot express
        // that: given a string it writes an escaped TEXT node, which is why the withdrawn encoding revision
        // could not be repaired by removing the encode alone.
        //
        // The two attribute values are the only parts this layer supplies, and neither can carry a quote
        // that would break out of the attribute: the type is CleanName-sanitised, which strips the double
        // quote and the apostrophe outright, and the version is a package version string. The composed
        // document is parsed below before it is returned, so a value that did break the shape is caught
        // rather than shipped.
        string composed = string.Concat(
            XmlDeclaration,
            FormattableString.Invariant(
                $"<{ContentElementName} {ContentTypeAttributeName}=\"{CleanName(package.ModuleName)}\""),
            FormattableString.Invariant(
                $" {ContentVersionAttributeName}=\"{package.Version ?? string.Empty}\">"),
            payload,
            FormattableString.Invariant($"</{ContentElementName}>"));

        // Parsing is what turns "the module handed back something" into "the module handed back a document
        // this format can carry", and it is the step that still refuses a payload. Two classes of content
        // fail here: markup that is not well formed, and a C0 control character, for which the XML
        // specification provides no escape at all.
        //
        // Only the exception the reader raises for unreadable content is caught. Anything else is left to
        // propagate: a broad catch would convert a genuine defect into a tidy failure code and hide it.
        try
        {
            XDocument.Parse(composed);
        }
        catch (XmlException)
        {
            // The module's own text is deliberately NOT quoted in the message, and neither is the reader's -
            // a parser message quotes the offending fragment. It is module content and may carry the
            // portal's user data, and the message is published verbatim as the problem detail.
            return Result<string>.Failure(
                ExportFailedCode,
                FormattableString.Invariant(
                    $"Module {moduleId} returned content that cannot be represented in an export document."));
        }

        string serialised = composed;

        // The trail records that content left the module, and the SIZE of what left rather than any part of
        // it: an export payload is module content, which may be arbitrarily large and may carry data
        // belonging to the portal's users.
        //
        // MIGRATION: named MODULE_EXPORTED rather than MODULE_UPDATED. An export changes nothing, so
        // recording it as an update stated something untrue in a trail whose entire value is that it is
        // believed.
        RecordModuleAudit(
            AuditEventNames.ModuleExported,
            portalId,
            module.ModuleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Export",
                ["BusinessControllerRegistered"] = bool.TrueString,
                ["PayloadLength"] = payload.Length.ToString(CultureInfo.InvariantCulture),
            });

        return Result<string>.Success(serialised);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The whole import is one unit of work. The target module is named in the body because the
    /// endpoint carries no identifier in its route, and the portal argument is what stops a
    /// body-supplied identifier from reaching another tenant's module.
    /// </para>
    /// <para>
    /// MIGRATION: <c>Import.ascx.vb</c> is the authority for all three steps, and the payload step
    /// is <c>xmlDoc.DocumentElement.InnerXml</c> - markup, not text. That decode belongs to a
    /// format whose writer had encoded the payload first; applied to a module admin document it
    /// corrupted every entity reference the content legitimately carried, and the corruption was
    /// invisible because the matching encode on the export side undid it again.
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
        // distinguishable from a supplied one. Modules.ModuleID is IDENTITY(0,1), which makes ZERO A REAL
        // MODULE, so a non-nullable property would have deserialised a missing moduleId into a live request
        // against module zero and nothing could have told the two apart.
        //
        // ModuleImportRequestValidator now refuses the omission at the boundary with a field-keyed answer,
        // and this guard is retained rather than removed. It is not redundant: this service is reachable
        // from callers the MVC validation filter does not sit in front of - the unit suites call it
        // directly, and so would any future in-process consumer - so the condition is enforced at both
        // points, with the same wording, and the pair is annotated in both places.
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
        // for the same reason as on creation - the target arrives in the BODY - and required at all because
        // without it any authenticated caller could overwrite any tenant's module content.
        if (await EnsureMayEditModuleAsync(portalId, moduleId, cancellationToken).ConfigureAwait(false)
            is ResultReason forbidden)
        {
            return Result.Failure(forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Result.Failure(ContentInvalidCode, "The submitted document is empty.");
        }

        // MIGRATION: THE DEFERRED-IMPORT EVENT-QUEUE BRANCH AT ModuleController.vb IS OMITTED, and what
        // replaces it is the refusal below. The legacy code tested `If objModule.SupportedFeatures =
        // Null.NullInteger` - the integer sentinel -1, meaning "this module's capabilities have not been
        // determined yet because it was installed in this very request" -, when true, called
        // CreateEventQueueMessage to park the payload on a queue for replay after an application restart.
        //
        // Nothing is lost, because the condition the branch existed to wait for cannot arise here. The
        // legacy had to defer only because it discovered a module's capabilities by late-binding its
        // controller class at run time, which was impossible mid-install; this service resolves behaviour
        // from a closed, dependency-injected set that is fixed at start-up, so a capability is either
        // registered or it is not and waiting changes nothing.
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
            document = ParseImportDocument(request.Content);
        }
        catch (XmlException exception)
        {
            RecordModuleImportParseFailure(portalId, moduleId, exception);
            return Result.Failure(ContentInvalidCode, ImportParseFailureMessage);
        }

        XElement? root = document.Root;
        if (root is null
            || root.Name.Namespace != XNamespace.None
            || !string.Equals(root.Name.LocalName, ContentElementName, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(
                ContentInvalidCode,
                FormattableString.Invariant($"The submitted document must have a <{ContentElementName}> root element."));
        }

        // SEC: THE ROOT ELEMENT MAY CARRY ONLY THE TWO ATTRIBUTES THIS CONTRACT DECLARES, and no namespace
        // declaration at all. A caller-supplied document is otherwise free to attach arbitrary attributes
        // and namespaces to the element this service reads, which is the shape a namespace-confusion or
        // attribute-smuggling attempt takes; refusing the document outright is cheaper and safer than
        // deciding, per attribute, whether it could matter.
        if (root.Attributes().Any(attribute =>
                attribute.IsNamespaceDeclaration
                || attribute.Name.Namespace != XNamespace.None
                || (!string.Equals(attribute.Name.LocalName, ContentTypeAttributeName, StringComparison.Ordinal)
                    && !string.Equals(attribute.Name.LocalName, ContentVersionAttributeName, StringComparison.Ordinal))))
        {
            return Result.Failure(
                ContentInvalidCode,
                "The submitted document contains an unsupported root namespace or attribute.");
        }

        // A payload that is itself markup is handed on as markup and is NOT decoded: it was never encoded,
        // so decoding it would corrupt any entity reference it legitimately contains. Only the text form -
        // which is what both this service and the legacy exporter write - carries the encoding to undo.
        //
        // MIGRATION: Import.ascx.vb - the document's type attribute is compared against the module's own
        // sanitised name and its sanitised friendly name, and a mismatch is refused with "The import file
        // specified is not the correct type for this module". Reproduced here, and reproduced at all because
        // it was PROMISED and not delivered: ModuleImportRequest.FileName documents that the legacy
        // name-based check was re-sourced onto this attribute precisely so the refusal survived, and until
        // now nothing performed it - so a document belonging to another module was imported into this one
        // without complaint, handing a module content it could not interpret.
        string declaredType = root.Attribute(ContentTypeAttributeName)?.Value ?? string.Empty;
        string sanitisedType = CleanName(declaredType);

        if (!string.Equals(sanitisedType, CleanName(package.ModuleName), StringComparison.Ordinal)
            && !string.Equals(sanitisedType, CleanName(package.FriendlyName), StringComparison.Ordinal))
        {
            // The module's own names are named in the message but the SUBMITTED value is not echoed back: it
            // is caller-supplied text and the message is published verbatim as the problem detail.
            string expectedName = CleanName(package.ModuleName);
            string expectedFriendlyName = CleanName(package.FriendlyName);

            // The two accepted spellings are frequently the SAME value - a module whose friendly name
            // matches its name sanitises to one string - and offering an operator a choice between a value
            // and itself reads as a defect in the message rather than as a hint. They are therefore named
            // once when they agree, and both only when there is genuinely a second thing to try.
            string accepted = string.Equals(expectedName, expectedFriendlyName, StringComparison.Ordinal)
                ? FormattableString.Invariant($"\"{expectedName}\"")
                : FormattableString.Invariant($"\"{expectedName}\" or \"{expectedFriendlyName}\"");

            return Result.Failure(
                ContentTypeMismatchCode,
                FormattableString.Invariant(
                    $"The submitted document type does not match the target module package: module {moduleId} expects {accepted}."));
        }

        // MIGRATION: Import.ascx.vb - `CType(objObject, IPortable).ImportModule(ModuleId,
        // xmlDoc.DocumentElement.InnerXml, strVersion, UserInfo.UserID)`. The payload is the root element's
        // INNER XML, verbatim, with no decoding of any kind, and the concatenation below is that property's
        // exact semantics: each child node written back as the markup it is, so an element stays an element,
        // an entity reference stays escaped and a CDATA section keeps its delimiters.
        //
        // MIGRATION: THE `HtmlDecode` THIS REPLACES CAME FROM THE PORTAL TEMPLATE READER AND DID NOT BELONG
        // HERE. ModuleController.vb does apply `Server.HtmlDecode`, preceded by the fixed-offset CDATA strip
        // at - but that pair reads the PORTAL TEMPLATE format, whose writer at had encoded and CDATA-wrapped
        // the payload in the first place.
        string payload = string.Concat(
            root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));

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

        // NOTHING BELOW THIS POINT RUNS UNLESS CONTENT WAS ACTUALLY RESTORED. The factory reports every
        // state in which the module was not asked - no controller declared, a stored key no registration
        // covers, a registered controller that cannot restore, or an empty payload - as a failure rather
        // than as a success carrying an advisory, so this one guard now covers all of them as well as a
        // restore that broke.
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

            // The bounded operational facts this contract records: which module received content, which
            // version the document declared, how much arrived and how many placements were invalidated. The
            // caller-authored folder, filename and PAYLOAD are not recorded, because each can carry tenant
            // or user data and belongs under the module content's own access and deletion policy.
            //
            // MIGRATION: DIVERGENCE, and a correction. SourceFolder and SourceFileName were copied verbatim
            // from the request, and ModuleImportRequest documents both as accepted-and-deliberately-unused
            // parity metadata with NO LENGTH BOUND and no interpretation as a path - so nothing validated
            // them, nothing read them, and a caller could put a secret, a personal identifier, control text
            // or an unbounded high-cardinality value into the audit trail simply by naming a file that way.
        RecordModuleAudit(
            AuditEventNames.ModuleUpdated,
            portalId,
            module.ModuleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["Version"] = DescribeDeclaredVersion(version),
                ["PayloadLength"] = payload.Length.ToString(CultureInfo.InvariantCulture),
                ["PlacementCount"] = placements.Count.ToString(CultureInfo.InvariantCulture),
            });

        // A bare success, with no advisory to forward. The factory's four "nothing was restored" states are
        // failures, so reaching this line means the module was asked and answered, and there is nothing left
        // to qualify the answer with.
        return Result.Success();
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
    /// WHY THIS CHECK LIVES HERE AND NOT IN A POLICY. Every other module mutation names its module
    /// in the route, so a route-reading authorisation policy can evaluate a permission against it
    /// before the action runs.
    /// </para>
    /// <para>
    /// EDIT ON THE PAGE IS THE LEGACY RULE. The legacy module-settings screen was reachable only
    /// from a page already in edit mode, which required the edit permission on that page, and the
    /// permission service answers a host account affirmatively before reading a grant - so a host
    /// account is admitted exactly as it is everywhere else.
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
    /// Confirms that the caller administers the portal, which is what the four portal-wide fields
    /// of an update require.
    /// </summary>
    /// <param name="portalId">The tenant whose administration is required.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// <para>
    /// WHY THIS IS SEPARATE FROM THE EDIT GRANT. The legacy screen drew exactly that line by
    /// disabling four controls for anyone outside the portal administrator role while leaving the
    /// rest of the form editable, and this reproduces the line rather than collapsing it into the
    /// module grant - which would either refuse ordinary page administrators everything or admit
    /// them to everything.
    /// </para>
    /// <para>
    /// The decision is delegated to <see cref="IPermissionService.IsPortalAdministratorAsync"/> so
    /// that it is taken from stored state in one place: the portal's own administrator role
    /// identifier and the caller's active assignments, with a host account admitted first. A failed
    /// decision - which that member does not currently produce - is treated as a refusal rather
    /// than as an admission, because a question about authority that could not be answered must
    /// never be answered "yes".
    /// </para>
    /// </remarks>
    private async Task<ResultReason?> EnsureAdministersPortalAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<bool> administers = await _permissions
            .IsPortalAdministratorAsync(portalId, _currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        return administers.IsSuccess && administers.Value
            ? null
            : new ResultReason(
                AdministratorForbiddenCode,
                FormattableString.Invariant(
                    $"Changing a module's page, its portal-wide placement, or either propagation instruction requires administering portal {portalId}."));
    }

    /// <summary>
    /// Confirms that the caller may edit a module, which is what importing content into it
    /// requires.
    /// </summary>
    /// <param name="portalId">The tenant the module belongs to.</param>
    /// <param name="moduleId">The module content is being imported into.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// The import's target arrives in the request body - the legacy import page chose it from a
    /// list on the form - so, exactly as for creation, no route-reading policy can reach it and the
    /// check belongs here. Import replaces a module's stored content, so the permission required is
    /// the module edit key, the same one the update and delete endpoints are gated on.
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

    /// <summary>Rejects a paging request whose bounds no validator can have accepted.</summary>
    /// <param name="request">The submitted paging request.</param>
    /// <returns>The reason the request is unusable, or <see langword="null" /> when it is usable.</returns>
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
        // union of every collection's set - one PagedRequest contract serves every listing and one validator
        // is resolved for it - so a portal-only, role-only or account-only field name would otherwise be
        // accepted here and then silently discarded, which returns a page the caller cannot account for and
        // cannot detect.
        //
        // SortableFields.Modules holds exactly the five names the store's placement ordering has an arm for
        // - ModuleRepository.ApplyPlacementOrder - and that correspondence is the point: a name admitted
        // here without an arm is a field the listing accepts and ignores, and an arm without an admitted
        // name is unreachable. THIS is the layer that refuses an unrecognised name, which is why the store's
        // ordering may safely fall back to its default rather than refusing again: a request that reaches
        // the store has already been checked against this set.
        if (!SortableFields.IsPermittedFor(request.SortBy, SortableFields.Modules))
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant($"Modules cannot be ordered by '{request.SortBy}'."));
        }

        return null;
    }

    // A helper here used to refuse a request whose end date preceded its start date. The legacy screen
    // refuses no such thing.
    //
    // The same reasoning already governs the two neighbouring fields on this contract, whose legacy
    // validators are likewise type-only: a negative cache period and a border outside the range its own
    // error message advertises are both admitted, and both are pinned by tests. Nothing is silently absorbed
    // - the storability bound on each date remains, enforced field by field by the request validator,
    // because SQL Server's datetime column genuinely cannot hold a value below its calendar and that is a
    // property of the store rather than a rule invented here.
    /// <summary>
    /// Outcome of normalising one submitted settings store: either the reason it is unusable, or
    /// the map to write.
    /// </summary>
    /// <param name="Reason">
    /// The reason the submission cannot be stored, or <see langword="null"/> when it can.
    /// </param>
    /// <param name="Values">The case-insensitive map to write.</param>
    /// <remarks>
    /// MIGRATION: this pair exists so that the normalising helper reports its outcome through its
    /// RETURN VALUE rather than through an argument it writes back.
    /// </remarks>
    private readonly record struct NormalisedSettings(
        ResultReason? Reason,
        Dictionary<string, string> Values);

    /// <summary>Normalises a submitted settings map, rejecting anything the columns cannot hold.</summary>
    /// <param name="submitted">The desired state of one settings store.</param>
    /// <param name="valueMaximumLength">Maximum storable value length for that store.</param>
    /// <param name="scope">Word naming the store, used in the reported message.</param>
    /// <returns>The map to write, or the reason the submission is unusable.</returns>
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
            // readers - ModuleController.vb - substituted "" for a DBNull column, so the empty string is the
            // legacy representation of an absent setting value and is preserved as such.
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
    /// Identifies setting names whose authority belongs to typed portal-administration contracts
    /// rather than the generic module-settings surface.
    /// </summary>
    /// <param name="settingName">The stored or submitted setting name.</param>
    /// <returns>
    /// <see langword="true"/> for the legacy security and user-list column namespaces.
    /// </returns>
    private static bool IsSecurityOwnedSettingName(string settingName)
        => settingName.StartsWith("Security_", StringComparison.OrdinalIgnoreCase)
            || settingName.StartsWith("Column_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses one import document with DTD resolution disabled and bounded character, node and
    /// nesting work.
    /// </summary>
    /// <param name="content">The caller-supplied XML document.</param>
    /// <returns>The parsed document with whitespace-only payload nodes preserved.</returns>
    /// <exception cref="XmlException">
    /// The document is malformed, prohibited or exceeds a work budget.
    /// </exception>
    private static XDocument ParseImportDocument(string content)
    {
        if (content.Length > ImportDocumentCharacterMaximum)
        {
            // The refusal NAMES the ceiling, because a caller that is only told it exceeded a budget cannot
            // tell how much smaller a document would be accepted - and the published limit is the whole
            // point of the transfer contract this number belongs to.
            throw new XmlException(FormattableString.Invariant(
                $"Portable-content XML exceeds the {ImportDocumentCharacterMaximum} character maximum."));
        }

        XmlReaderSettings settings = CreateImportXmlReaderSettings();

        // Validate work factors before constructing an object graph. The content string is immutable, so the
        // second pass cannot differ from the first; parsing twice is a bounded cost and prevents a deeply
        // nested document from reaching XDocument's tree builder before its depth has been refused.
        using (var validationInput = new StringReader(content))
        using (XmlReader validationReader = XmlReader.Create(validationInput, settings))
        {
            int nodeCount = 0;
            while (validationReader.Read())
            {
                nodeCount++;
                if (validationReader.NodeType == XmlNodeType.Element)
                {
                    nodeCount += validationReader.AttributeCount;
                    if (validationReader.AttributeCount > ImportElementAttributeMaximum)
                    {
                        throw new XmlException("Portable-content XML exceeds the per-element attribute budget.");
                    }
                }

                if (nodeCount > ImportDocumentNodeMaximum)
                {
                    throw new XmlException("Portable-content XML exceeds the node budget.");
                }

                if (validationReader.Depth > ImportDocumentDepthMaximum)
                {
                    throw new XmlException("Portable-content XML exceeds the nesting-depth budget.");
                }

                if ((validationReader.NodeType is XmlNodeType.Text
                        or XmlNodeType.CDATA
                        or XmlNodeType.Whitespace
                        or XmlNodeType.SignificantWhitespace)
                    && validationReader.Value.Length > ImportTextNodeCharacterMaximum)
                {
                    throw new XmlException("Portable-content XML exceeds the text-node budget.");
                }
            }
        }

        using var input = new StringReader(content);
        using XmlReader reader = XmlReader.Create(input, settings);

        // PreserveWhitespace is required, not cosmetic. Without it a whitespace-only payload becomes empty
        // and the module is handed content the caller did not send.
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Builds the immutable security settings used for both import parsing passes.</summary>
    /// <returns>An XML reader configuration that performs no external resolution.</returns>
    private static XmlReaderSettings CreateImportXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = ImportDocumentCharacterMaximum,
        IgnoreWhitespace = false,
        ConformanceLevel = ConformanceLevel.Document,
        CloseInput = true,
    };

    /// <summary>Records a bounded parser diagnostic without publishing parser text or submitted content.</summary>
    /// <param name="portalId">The tenant in which import was attempted.</param>
    /// <param name="moduleId">The target module.</param>
    /// <param name="exception">The parser exception whose type and position are retained.</param>
    private void RecordModuleImportParseFailure(int portalId, int moduleId, XmlException exception)
    {
        AuditEvent record = new(AuditEventNames.ModuleUpdated)
        {
            Outcome = AuditOutcome.Denied,
            PortalId = portalId,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ResourceType = ModuleResourceType,
            ResourceId = moduleId.ToString(CultureInfo.InvariantCulture),
            FailureCode = ContentInvalidCode,
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["DiagnosticType"] = exception.GetType().Name,
                ["LineNumber"] = exception.LineNumber.ToString(CultureInfo.InvariantCulture),
                ["LinePosition"] = exception.LinePosition.ToString(CultureInfo.InvariantCulture),
            },
        };

        _audit.Record(record);
    }

    /// <summary>Resolves the placement a member addresses.</summary>
    /// <param name="module">The module whose placement is wanted.</param>
    /// <param name="tabModuleId">
    /// The named placement, or <see langword="null"/> for the original one.
    /// </param>
    /// <param name="mismatchCode">
    /// Reason code to report when a named placement belongs to another module, or
    /// <see langword="null"/> when the caller documents no such code and that case reads as an
    /// absence.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// The placement, an absence, or the reason the named placement does not fit.
    /// </returns>
    /// <remarks>
    /// With no placement named, the module's original placement is used, identified as the one with
    /// the lowest placement identifier. That is deterministic and stable, which matters because two
    /// members address a module without carrying a placement identifier at all.
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

    /// <summary>Reads the placement a module has on one named page.</summary>
    /// <param name="module">The module whose placement is wanted.</param>
    /// <param name="tabId">The page the placement must sit on.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// The placement on that page, or <see langword="null"/> when the module is not placed there.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="ResolvePlacementAsync"/> and deliberately so. Folding the two
    /// together is what produced the defect recorded on the update path, because the fallback
    /// silently satisfied a request that had named a page.
    /// </para>
    /// <para>
    /// The page is matched by VALUE and never by sign. <c>dbo.Tabs.TabID</c> is
    /// <c>IDENTITY(0, 1)</c>, so page zero is the first page a portal ever created and a
    /// positive-value test would refuse it; <c>-1</c> was an "any page" wildcard in the legacy
    /// query surface rather than an absence.
    /// </para>
    /// </remarks>
    private async Task<TabModule?> ReadPlacementOnPageAsync(
        Module module,
        int tabId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> placements = module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        return placements
            .Where(candidate => candidate.TabId == tabId)
            .OrderBy(candidate => candidate.TabModuleId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Strips the legacy punctuation set from a name, reproducing the sanitiser both content
    /// workflows used to label and to identify an exported document.
    /// </summary>
    /// <param name="name">The name to sanitise.</param>
    /// <returns>The name with every character of <see cref="NamePunctuation"/> removed.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Website/admin/Modules/Export.ascx.vb</c>, duplicated at
    /// <c>Library/Components/Shared/Globals.vb</c>. The result is identical rather than merely
    /// equivalent because removal is order-independent: no replacement can introduce a character a
    /// later replacement would have removed.
    /// </para>
    /// <para>
    /// It is deliberately NOT a general-purpose sanitiser and must not be reached for as one.
    /// </para>
    /// </remarks>
    private static string CleanName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (!NamePunctuation.Contains(character, StringComparison.Ordinal))
            {
                cleaned.Append(character);
            }
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Reads a portal's definition and package catalogue facts, keyed by definition identifier.
    /// </summary>
    /// <param name="portalId">The portal whose catalogue is read.</param>
    /// <param name="wantedDefinitionIds">
    /// The definitions the caller is about to project, or <see langword="null"/> to key the whole
    /// tenant catalogue.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Catalogue facts by definition identifier.</returns>
    /// <remarks>
    /// <para>One read serves a whole page.</para>
    /// <para>
    /// The narrowing is applied to what is KEYED rather than to what is read, because the tenant's
    /// catalogue is answered by one portal-scoped statement whose cost does not vary with the set
    /// asked for - the grant join is the same join either way - whereas a per-definition read would
    /// be one statement per row.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, ModuleCatalogueFacts>> ReadCatalogueFactsAsync(
        int portalId,
        IReadOnlyCollection<int>? wantedDefinitionIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        // A null set means "key everything", which is what the single-module reads want: they hold one
        // module and its definition may or may not be covered by the tenant's grants, and ResolveCatalogue
        // falls back to the module's own navigations when it is not. An EMPTY set is a different instruction
        // and keys nothing, but no call site issues one - both callers skip this read entirely when they
        // have no row to name.
        HashSet<int>? wanted = wantedDefinitionIds is null ? null : new HashSet<int>(wantedDefinitionIds);

        var facts = new Dictionary<int, ModuleCatalogueFacts>(wanted?.Count ?? definitions.Count);
        foreach (ModuleDefinition definition in definitions)
        {
            if (wanted is not null && !wanted.Contains(definition.ModuleDefinitionId))
            {
                continue;
            }

            facts[definition.ModuleDefinitionId] = ModuleCatalogueFacts.From(definition);
        }

        return facts;
    }

    /// <summary>Picks the catalogue facts to project for a module.</summary>
    /// <param name="module">The module being projected.</param>
    /// <param name="catalogue">Facts read for the portal's catalogue.</param>
    /// <returns>
    /// The facts for the module's definition, resolved from the tenant's catalogue when it holds
    /// the definition and from the module's own navigations otherwise.
    /// </returns>
    /// <remarks>
    /// The catalogue is consulted first because it is portal-scoped and authoritative for what this
    /// tenant may see, and the navigation is the fallback for a module whose definition the
    /// tenant's grant no longer covers - a real state, since revoking a package grant does not
    /// remove the modules already placed from it.
    /// </remarks>
    private static ModuleCatalogueFacts ResolveCatalogue(
        Module module,
        IReadOnlyDictionary<int, ModuleCatalogueFacts> catalogue)
        => catalogue.TryGetValue(module.ModuleDefinitionId, out ModuleCatalogueFacts? facts)
            ? facts
            : ModuleCatalogueFacts.FromNavigation(module);

    /// <summary>Projects a portal's available definitions into the catalogue contract.</summary>
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

    /// <summary>Reads the installed package a module's definition belongs to.</summary>
    /// <param name="module">The module whose package is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The package, or <see langword="null" /> when it cannot be resolved.</returns>
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

    /// <summary>Reads the portal's content pages, that is every page that is not administrative.</summary>
    /// <param name="portalId">The portal whose pages are read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The portal's content pages.</returns>
    private async Task<IReadOnlyList<Tab>> ReadContentTabsAsync(int portalId, CancellationToken cancellationToken)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        // The repository returns recycled pages, because the legacy read applied no predicate to IsDeleted
        // and projected the column instead. A content page offered as a placement target must not be one
        // sitting in the recycle bin, so that exclusion is applied here alongside the administrative one
        // rather than asked of the contract.
        IReadOnlyList<Tab> tabs = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        int? adminTabId = portal?.AdminTabId;
        return tabs
            .Where(tab => !tab.IsDeleted && !IsAdministrative(tab, adminTabId))
            .ToList();
    }

    /// <summary>Classifies a page as administrative.</summary>
    /// <param name="tab">The page to classify.</param>
    /// <param name="adminTabId">The portal's administration page, when it has one.</param>
    /// <returns><see langword="true"/> when the page belongs to the administrative band.</returns>
    /// <remarks>
    /// This reproduces <c>TabInfo.IsAdminTab</c> (TabInfo.vb) for a portal-scoped page: the
    /// administration page itself and its immediate children. The host band the legacy property
    /// also covered cannot arise here, because every page read belongs to one portal.
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
    /// <param name="requested">
    /// The position the caller submitted, which may be the append instruction.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// <paramref name="requested"/> unchanged when it names a position, or the next position at the
    /// bottom of the pane when it is the append instruction.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces <c>ModuleController.UpdateModuleOrder</c> (ModuleController.vb),
    /// which read the pane's occupied positions through <c>GetTabModuleOrder(TabId, PaneName)</c>
    /// and added the step. The same subtraction is reproduced by seeding the running maximum with
    /// the sentinel, so one expression covers both the empty and the occupied pane exactly as the
    /// legacy one did.
    /// </para>
    /// <para>
    /// MIGRATION: the resolution happens BEFORE the row is written, whereas the legacy resolved it
    /// immediately AFTER writing the row and then issued a second statement to correct it. The
    /// outcome stored is identical and the sentinel is never durable in either arrangement; doing
    /// it first removes a write and, more importantly, removes the window in which a concurrent
    /// reader could observe the sentinel as though it were a position.
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

    /// <summary>Places a module on every content page it is missing from.</summary>
    /// <param name="portalId">The portal being fanned out across.</param>
    /// <param name="module">The module being placed.</param>
    /// <param name="template">The placement whose settings the new placements copy.</param>
    /// <param name="requestedPosition">
    /// The position the caller submitted, forwarded unresolved so that an append instruction is
    /// resolved against each target page's own pane rather than reusing the position computed for
    /// the addressed page.
    /// </param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements added.</returns>
    /// <remarks>
    /// MIGRATION - discovered legacy defect, not inherited. The legacy copy-to-another-page path
    /// passed the append instruction straight to the insert under the comment "Add a copy of the
    /// module to the bottom of the Pane for the new Tab" (ModuleController.vb) and then, unlike
    /// both the add and the update paths, never called the resolver - so the sentinel stayed in the
    /// row until some later renumbering of that page happened to overwrite it.
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

    /// <summary>Moves one placement of a module from the page it occupies onto another page.</summary>
    /// <param name="module">The module being relocated.</param>
    /// <param name="source">The placement being vacated.</param>
    /// <param name="destination">The content page the module is moving onto.</param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// The placement the module occupies once the move is staged - the newly created row, or the row that
    /// already existed on the destination.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces <c>ModuleController.MoveModule</c>, which was deliberately NOT a page
    /// reassignment. It called <c>CopyModule(moduleId, fromTabId, toTabId, "", includeSettings:=True)</c> and
    /// then <c>DeleteTabModule(fromTabId, moduleId)</c>, and the copy carried the placement's presentation
    /// columns and its placement-scoped settings onto the new page. Rewriting the page key in place would be
    /// fewer statements and would look equivalent, but it is not: the settings rows hang off the placement
    /// key, so preserving them is the whole reason the legacy went the long way round.
    /// </para>
    /// <para>
    /// The pane and position follow the legacy copy exactly. An empty destination pane meant "the same pane
    /// the module is already in" - <c>If toPaneName = "" Then toPaneName = objModule.PaneName</c> - and the
    /// new row went to the bottom of it, under the comment "Add a copy of the module to the bottom of the
    /// Pane for the new Tab". The append instruction is resolved against the destination rather than stored
    /// raw, which is the discovered legacy defect already recorded on the copy-to-content-pages path: the
    /// legacy copy passed the sentinel straight into the insert and left it in the row.
    /// </para>
    /// <para>
    /// A module already placed on the destination is not duplicated. The legacy copy wrapped its insert in a
    /// bare <c>Try ... Catch</c> whose comment read "module already in the page, ignore error", and then
    /// deleted the source regardless - so the observable legacy outcome was exactly this: one placement, on
    /// the destination. The difference here is that the case is tested for rather than discovered by letting
    /// a unique-key violation escape, because a swallowed exception cannot tell "already there" apart from
    /// any other write failure.
    /// </para>
    /// <para>
    /// Nothing is committed here. The move is staged on the tracked graph and travels in the SAME
    /// transaction as the rest of the update, so a module cannot end up on both pages, or on neither, if a
    /// later step fails. The legacy ran the copy and the delete as two unrelated statements after the update
    /// had already committed, which left all three of those outcomes reachable.
    /// </para>
    /// </remarks>
    private async Task<TabModule> RelocatePlacementAsync(
        Module module,
        TabModule source,
        Tab destination,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        affectedTabIds.Add(source.TabId);
        affectedTabIds.Add(destination.TabId);

        TabModule? existing = await ReadPlacementOnPageAsync(module, destination.TabId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Already there. The source row still goes, so the caller's instruction - "this module should be
            // on that page, not this one" - is honoured, and no duplicate placement is created.
            await RemovePlacementAsync(source, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        // Read BEFORE the source row is staged for deletion. The settings are keyed by the placement, and a
        // cascade configured on that key is entitled to take them with it.
        IReadOnlyList<TabModuleSetting> carried = await _modules
            .GetTabModuleSettingsAsync(source.TabModuleId, cancellationToken)
            .ConfigureAwait(false);

        int position = await ResolvePositionAsync(
            destination.TabId,
            source.PaneName,
            AppendPositionSentinel,
            cancellationToken).ConfigureAwait(false);

        var moved = new TabModule
        {
            TabId = destination.TabId,
            ModuleId = module.ModuleId,
            PaneName = source.PaneName,
            ModuleOrder = position,
            CacheTime = source.CacheTime,
            Alignment = source.Alignment,
            Color = source.Color,
            Border = source.Border,
            IconFile = source.IconFile,
            Visibility = source.Visibility,
            ContainerSrc = source.ContainerSrc,
            DisplayTitle = source.DisplayTitle,
            DisplayPrint = source.DisplayPrint,
            DisplaySyndicate = source.DisplaySyndicate,
        };

        // Attached through the navigation rather than by copying the placement key, because the destination
        // row has no key yet - it is generated on insert. The relationship is configured with this collection
        // as its inverse end, so the generated identifier reaches these rows without a second round trip and
        // without an intermediate commit that would make the move non-atomic.
        foreach (TabModuleSetting setting in carried)
        {
            moved.Settings.Add(new TabModuleSetting
            {
                SettingName = setting.SettingName,
                SettingValue = setting.SettingValue,
            });
        }

        await _modules.AddTabModuleAsync(moved, cancellationToken).ConfigureAwait(false);
        await RemovePlacementAsync(source, cancellationToken).ConfigureAwait(false);

        return moved;
    }

    /// <summary>Removes every placement of a module other than the one being kept.</summary>
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

    /// <summary>Removes one placement together with the settings scoped to it.</summary>
    /// <param name="placement">The placement to remove.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    /// <remarks>
    /// The settings rows are removed explicitly rather than left to the cascade the schema
    /// declares, so the outcome does not depend on which provider the request is served by.
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

    /// <summary>Records a module and its page as the portal's default pair.</summary>
    /// <param name="portalId">The portal whose default is being set.</param>
    /// <param name="moduleId">The module becoming the default.</param>
    /// <param name="tabId">The page the default module sits on.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when the pair was recorded.</returns>
    /// <remarks>
    /// The two rows are written against the portal's own settings module instance, resolved by
    /// friendly name, which is exactly where <c>PortalSettings.UpdateSiteSetting</c> wrote them.
    /// </remarks>
    private async Task<bool> NameAsPortalDefaultAsync(
        int portalId,
        int moduleId,
        int tabId,
        CancellationToken cancellationToken)
    {
        // Site Settings is an administrative package, intentionally excluded from the portal-placeable
        // catalogue and reached only through the explicit privileged lookup used by the two administrative
        // settings paths.
        ModuleDefinition? siteSettings = await _definitions
            .GetAdministrativeDefinitionByFriendlyNameAsync(
                portalId,
                SiteSettingsDefinitionName,
                cancellationToken)
            .ConfigureAwait(false);

        if (siteSettings is null)
        {
            return false;
        }

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every live
        // instance so it could locate one by its definition.
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

    /// <summary>Writes one module setting, updating the stored row when it already exists.</summary>
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
    /// <remarks>Only the appearance members travel.</remarks>
    private async Task<int> PropagateAppearanceAsync(
        int portalId,
        TabModule source,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Tab> contentTabs = await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false);
        var contentTabIds = contentTabs.Select(tab => tab.TabId).ToHashSet();

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every live
        // instance so it could locate one by its definition.
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

    /// <summary>Drops the cached module set of every page a write touched.</summary>
    /// <param name="tabIds">The pages whose caches are stale.</param>
    /// <remarks>
    /// This is the target form of the legacy <c>ClearCache(TabId)</c> call that closed every module
    /// write. The legacy call also dropped that page's module permission cache; nothing here writes
    /// a permission row, so that eviction is deliberately not repeated.
    /// </remarks>
    private void InvalidatePlacements(IEnumerable<int> tabIds)
    {
        foreach (int tabId in tabIds)
        {
            _cache.InvalidateModules(tabId);
        }
    }

    /// <summary>
    /// Bounds and allowlists the version an imported document declares, before it is recorded.
    /// </summary>
    /// <param name="version">
    /// The version read from the document, or resolved from the package.
    /// </param>
    /// <returns>
    /// The declared version when it is a plain version string within
    /// <see cref="MaximumAuditedVersionLength"/>; otherwise <see cref="UnusableVersion"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An ALLOWLIST of digits, dots and hyphens, which is every character a version has ever needed
    /// and nothing that could carry a secret, a personal identifier or a control character. The
    /// alternative - stripping the characters that are unwelcome - reshapes a hostile value into
    /// something that looks authentic, and an audit record must not contain a value that reads as
    /// provenance but is not.
    /// </para>
    /// </remarks>
    private static string DescribeDeclaredVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > MaximumAuditedVersionLength)
        {
            return UnusableVersion;
        }

        foreach (char character in version)
        {
            if (!char.IsAsciiDigit(character) && character != '.' && character != '-')
            {
                return UnusableVersion;
            }
        }

        return version;
    }

    /// <summary>Records one module change on the audit trail.</summary>
    /// <param name="portalId">The tenant the module belongs to.</param>
    /// <param name="moduleId">The module the record is about.</param>
    /// <param name="facts">The facts describing what happened.</param>
    /// <param name="eventName">
    /// The catalogue name describing what happened, from <see cref="AuditEventNames" />.
    /// </param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy module trail was written by <c>EventLogController.AddLog</c> against the
    /// <c>MODULE_*</c> members of <c>EventLogType</c> (<c>EventLogController.vb</c>).
    /// </para>
    /// <para>
    /// MIGRATION: CORRECTION, on two counts.
    /// </para>
    /// </remarks>
    private void RecordModuleAudit(
        string eventName,
        int portalId,
        int moduleId,
        IReadOnlyDictionary<string, string?> facts)
    {
        AuditEvent record = new(eventName)
        {
            PortalId = portalId,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ResourceType = ModuleResourceType,
            ResourceId = moduleId.ToString(CultureInfo.InvariantCulture),
            Properties = facts,
        };

        _audit.Record(record);
    }

}
