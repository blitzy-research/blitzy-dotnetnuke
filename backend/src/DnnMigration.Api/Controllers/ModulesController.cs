using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The module resource: the pluggable content component and its placements.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Modules/ModuleSettings.ascx.vb</c>, <c>Export.ascx.vb</c> and
/// <c>Import.ascx.vb</c>. The two file operations were postback pages that wrote to and read from the portal
/// home directory; they are now explicit calls that carry their content in the request and response.
/// </para>
/// <para>
/// <strong>The canonical module resource is flat and tenant-bound.</strong> Every route begins at
/// <c>/api/v1/modules</c>; the portal is the tenant resolved from the request host and authenticated
/// context, not a second resource identity embedded in the path. The controller passes that resolved
/// identifier into every service call, while the module/page permission handlers resolve the same tenant
/// before judging the item key carried by the route.
/// </para>
/// <para>
/// <strong>A module and its placement are different rows, and the identity of a module INSTANCE is the pair
/// <c>(ModuleID, TabID)</c>.</strong> <c>dbo.Modules</c> holds one row per module and <c>dbo.TabModules</c>
/// one row per placement of it on a page, with <c>TabModuleID</c> as that placement's surrogate key. The
/// legacy <c>ModuleInfo</c> flattened the <c>Modules</c> to <c>TabModules</c> to <c>ModuleDefinitions</c> to
/// <c>ModuleControls</c> join into one class, which is why its permission read is keyed by BOTH identifiers
/// (<c>ModuleSettings.ascx.vb</c> L88-L89), and it is split four ways in the target. Every action below that
/// addresses one placement therefore accepts a <c>tabModuleId</c>: a module marked for every page has one
/// module row and many placement rows, so "remove the module" and "remove this placement" are genuinely two
/// operations rather than one with a flag.
/// </para>
/// <para>
/// <strong>Settings come in two stores that are never merged.</strong> <c>dbo.ModuleSettings</c> is keyed
/// <c>(ModuleID, SettingName)</c> and shared by every placement; <c>dbo.TabModuleSettings</c> is keyed
/// <c>(TabModuleID, SettingName)</c> and owned by one placement. They stay two dictionaries all the way to
/// the wire, because collapsing them would destroy an every-page module's per-page configuration.
/// </para>
/// <para>
/// The single-module endpoints carry the module view and module edit policies, which is what those policies
/// exist for; they read the <c>moduleId</c> route value, so that segment name is part of the contract with
/// the handler. The listing carries the portal-administrator policy, because a portal-wide read has no module
/// identifier for a per-module policy to evaluate.
/// </para>
/// <para>
/// Creation and import are the two actions whose target arrives in the request BODY - the page a module is
/// placed on, and the module the content is imported into - so no route-reading PERMISSION policy can reach
/// either, and the per-resource permission check is performed by the service after binding. That statement
/// used to appear here while being untrue: the service verified only that the target belonged to the tenant
/// and never evaluated a permission, so any authenticated caller could place a module on any tenant's page or
/// overwrite any tenant's module content. The service now performs the check it is credited with.
/// </para>
/// <para>
/// <strong>The two are nevertheless gated differently, and the asymmetry is the legacy rule rather than an
/// oversight.</strong> Import carries the portal-administrator policy; creation carries no policy of its own
/// and is decided entirely by the service's edit evaluation against the target page. The reason is the gate
/// measured at <c>ModuleSettings.ascx.vb</c> L191, which admitted a caller in the portal's administrator role
/// <em>or</em> in the active page's own administrator roles - so requiring portal administration to place a
/// module would refuse the page administrator the legacy application admitted, narrowing behaviour rather
/// than preserving it. Import has no such second arm to preserve: its legacy page carried no in-code role
/// check at all, so a single explicit policy is the honest replacement for an ambient one.
/// </para>
/// <para>
/// Creation has no item key in the route for a module policy to inspect, so tenant safety rests on the
/// resolved context plus the service's page-ownership and edit-permission checks. The service receives the
/// same portal identifier resolved for the request and verifies that the target page belongs to it before
/// adding anything.
/// </para>
/// </remarks>
// MIGRATION: H1. THE MODULE INSTANCE IS THE PAIR (ModuleID, TabID), NOT A SINGLE KEY. The legacy read at
//            ModuleSettings.ascx.vb L88-L89 -
//            GetModulePermissionsCollectionByModuleID(ModuleId, TabId) - keys a module's permissions on BOTH
//            identifiers, which is the proof that the flattened 936-line ModuleInfo was a join rather than an
//            entity. It becomes four entities on the real table boundaries (Module, TabModule,
//            ModuleDefinition, ModuleControl), TabModuleID is the placement surrogate, and that is why the
//            single-instance actions below take an optional tabModuleId instead of pretending a module has one
//            address.
//
// MIGRATION: H2. THE DOUBLE ADMINISTRATOR GATE BECOMES TWO DECLARATIVE POLICIES AND A 403. The legacy gate at
//            ModuleSettings.ascx.vb L191 reads
//            "IsInRoles(PortalSettings.AdministratorRoleName) = False And
//             IsInRoles(PortalSettings.ActiveTab.AdministratorRoles.ToString) = False", so a PORTAL
//            administrator OR a PAGE administrator was admitted, and L192 refused everyone else with
//            Response.Redirect(NavigateURL("Access Denied"), True) - a 302 to a page. The target expresses the
//            same admission through the module view and module edit policies, whose handler evaluates the
//            grant, and refuses with 403 and a problem document instead of redirecting. Nothing here
//            re-implements the gate imperatively: two evaluators for one rule is the worst outcome available,
//            so the decision is the handler's alone.
//            The .ToString on a role COLLECTION in that condition is a live Option-Strict-OFF artefact -
//            Website/release.config L125 compiles the pages with strict="false" while the class library sets
//            OptionStrict On - which is exactly why these code-behinds are read for endpoint semantics rather
//            than translated line by line.
//
// MIGRATION: H10. THE MODULE DELETE IS A SOFT DELETE, AND NOTHING HERE UNDOES IT. The legacy path set
//            IsDeleted = True (ModuleSettings.ascx.vb L364 sets it back to False on an update) so an
//            administrator could restore a module from the recycle bin. The delete action answers 204 and the
//            row survives with its marker set; the listing hides such modules unless they are asked for. No
//            restore, purge or recycle-bin endpoint exists on this controller by decision, not omission:
//            Website/admin/Tabs/RecycleBin.ascx.vb deliberately produces no controller at all, so inventing
//            one here would be scope creep wearing the costume of parity.
//
// MIGRATION: RULE T7. NO NUMERIC SENTINEL MEANS "ABSENT" ANYWHERE ON THIS SURFACE. Library/Components/Shared/Null.vb
//            L41-L45 defines NullInteger as -1 and L71-L75 defines NullString as the EMPTY STRING rather than
//            null, and both candidate sentinels are real keys in this schema:
//            01.00.00.SqlDataProvider L221 declares [ModuleID] [int] IDENTITY (0, 1) and L140 declares
//            [TabID] [int] IDENTITY (0, 1), so 0 is the first module and the first page an installation
//            creates, while L77 declares [PortalID] [int] IDENTITY (-1, 1), so -1 is simultaneously a real
//            portal and the legacy sentinel. Optional identifiers are therefore nullable integers and absence
//            is null. No comparison, coalesce or route constraint in this file treats 0 or -1 as missing, and
//            no range constraint excludes them.
//
// MIGRATION: CACHING AND AUDITING ARE NOT PRESENTATION CONCERNS AND ARE ABSENT HERE. ModuleController.vb is the
//            single largest consumer of the legacy static cache - 21 of the 116 in-scope call sites, each
//            computing its expiry as a per-entity timeout multiplied by a global performance setting (L998,
//            L1052, L1264, L1355) and invalidating coarsely. All of it moves behind the domain cache
//            abstraction the service injects, with the legacy key names kept as constants and invalidation made
//            explicit after each successful write, so no cache type is injected here and no cache-invalidation
//            endpoint exists. The legacy audit entries likewise become structured events emitted by the
//            application service, excluding sensitive values, rather than log calls from an action.
//
// MIGRATION: PAGING IS ZERO-BASED, AND THAT IS THE LEGACY DATA LAYER'S BASE RATHER THAN THE LEGACY SCREEN'S.
//            The stored procedures compute SET @PageLowerBound = @PageSize * @PageIndex, whereas the screens
//            passed CurrentPage - 1 to reach it. The request and result contracts both document the
//            zero-based index, so this controller passes the caller's coordinates through untouched; silently
//            re-basing them here would put two conventions in one request path.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/modules")]
[Produces("application/json")]
public sealed class ModulesController : ControllerBase
{
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>The media type an exported module payload is returned as.</summary>
    /// <remarks>
    /// The legacy export wrote an XML document to a file in the portal home directory, and the content a
    /// module produces is unchanged by this migration, so the payload is still XML. Returning it as XML
    /// rather than as a JSON string means a caller can save the response body directly, exactly as the
    /// legacy page produced a saveable file.
    /// </remarks>
    private const string ExportContentType = "application/xml";

    private readonly IModuleService _modules;
    private readonly IPortalContextHolder _portalContext;

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    // validated by FluentValidationActionFilter, which is registered once for the whole API, runs before
    // the action and resolves a validator from each argument's declared type. This controller used to
    // take validators of its own and invoke them by hand as well, which was a second invocation path for
    // one rule set and the reason the paging contract was judged against the wrong sortable vocabulary.
    // Adding a validator argument back here would recreate that split.
    /// <summary>Initialises a new instance of the <see cref="ModulesController"/> class.</summary>
    /// <param name="modules">The module service.</param>
    /// <param name="portalContext">The tenant resolved from the request host.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public ModulesController(IModuleService modules, IPortalContextHolder portalContext)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the tenant resolved for the current request, or <see langword="null"/>.</summary>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>Lists a portal's modules.</summary>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="tabId">Restricts the result to modules placed on one tab.</param>
    /// <param name="includeDeleted">Includes modules that are in the recycle bin.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of modules.</returns>
    // TENANT-BOUND, NOT MERELY AUTHENTICATED. This action inherited only the class-level authentication
    // requirement, so any bearer token could name any portal in the route and enumerate that tenant's
    // modules. The per-module policies below cannot serve a listing, because they evaluate a permission
    // against a module identifier and a listing has none; the portal-administrator policy is the right
    // grain for a portal-wide read and is anchored to the portal this route names.
    [HttpGet]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<ModuleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<ModuleListItemDto>>> ListAsync(
        [FromQuery] ModulePagedRequest request,
        [FromQuery] int? tabId,
        [FromQuery] bool includeDeleted,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // The paging contract is validated by the globally registered validation filter, which now
        // resolves ModulePagedRequestValidator from this parameter's type and applies the module
        // collection's own sortable set. This action used to invoke IValidator<PagedRequest> by hand,
        // which was a second invocation path AND the wrong rules: that contract resolves the
        // unspecialised validator, whose sortable set is the union of every collection's, so a portal
        // or account field name was accepted here and then discarded by the listing.
        Result<PagedResult<ModuleListItemDto>> outcome = await _modules
            .ListModulesAsync(portalId, request, tabId, includeDeleted, cancellationToken)
            .ConfigureAwait(false);

        // Projected onto the wire envelope here rather than returned as the domain page. CompletePage
        // applies PagedResponse<T>.From, so the response carries `items` plus `meta` and the domain
        // paging type never crosses the boundary.
        return this.CompletePage(outcome);
    }

    /// <summary>Retrieves one module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The module, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    [HttpGet("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleView)]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto?>>> GetAsync(
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleDetailDto?> outcome = await _modules
            .GetModuleAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a module and places it.</summary>
    /// <param name="request">The module to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created module, with its address in the location header.</returns>
    // MIGRATION: THIS ACTION CARRIES NO POLICY, AND THE OMISSION IS DELIBERATE RATHER THAN MISSING. The gate at
    //            ModuleSettings.ascx.vb L191 admitted the portal administrator OR an administrator of the page
    //            being edited, so requiring portal administration to place a module would refuse a caller the
    //            legacy application admitted - a narrowing, which the behaviour-preservation obligation forbids
    //            as squarely as it forbids a widening. The page a module is placed on arrives in the BODY, so no
    //            route-reading permission policy can reach it either; a Group A policy here would fail closed and
    //            refuse everyone. The edit grant on the target page is therefore evaluated by the service, after
    //            binding, against the portal resolved from the request host.
    //            Stated as a limitation rather than a reassurance: because no policy applies, this action falls
    //            back to the bare authenticated-user requirement, so it is the service's page-belongs-to-portal
    //            check and its grant evaluation that carry the tenant boundary here on their own. That the
    //            evaluation happens is pinned by tests on all three of its outcomes - refused without a grant and
    //            nothing written, admitted with one, refused again once a deny is recorded beside it - because a
    //            denial alone could not distinguish a consulted grant from a route closed to everybody.
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto>>> CreateAsync(
        [FromBody] CreateModuleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result<ModuleDetailDto> outcome = await _modules
            .CreateModuleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.ModuleId);
    }

    /// <summary>Updates a module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// The updated module, or <c>404 Not Found</c> when it or the selected page does not exist in this
    /// portal.
    /// </returns>
    // MIGRATION: H6. THE FIELD SET IS THE ONE MEASURED AT ModuleSettings.ascx.vb L341-L385, IN THAT ORDER:
    //            title, Alignment (L345), colour, border, icon, CacheTime (L349-L352), TabID, AllTabs,
    //            Visibility (L359-L362), Header, Footer, start and end dates, container,
    //            ModulePermissions (L378), InheritViewPermissions (L379) and the display flags, committed by a
    //            single UpdateModule at L385. Two shapes on that list do not survive translation. Visibility
    //            was a Select Case over the raw combo value mapping 0, 1 and 2 onto Maximized, Minimized and
    //            None; it is an explicitly-valued domain enumeration, consumed as declared and never renumbered.
    //            Permissions were an untyped collection that the legacy permission readers rendered as a
    //            semicolon-delimited string with per-user grants encoded as bracketed pseudo-roles - the
    //            ";Administrators;[42];" form assembled at TabPermissionController.vb L214-L227 - and they are a
    //            typed collection here, with a per-user grant carried as a first-class nullable user identifier.
    //            No string is split or joined on a delimiter anywhere in this file. That same legacy builder
    //            emits only entries whose AllowAccess is True, which is the measurement establishing that this
    //            DotNetNuke generation has NO deny prefix: none is invented here.
    //
    // MIGRATION: H3. THE "ALL TABS" RULE IS FIELD-LEVEL AUTHORISATION AND CANNOT BE AN ATTRIBUTE. The legacy
    //            page disabled chkAllTabs, chkDefault, chkAllModules and cboTab for any caller outside the
    //            portal administrator role, at both L214-L219 and L332-L338, under the comment "tab
    //            administrators can only manage their own tab" - so a page administrator could edit the module
    //            in front of them but could not promote it across the portal, and setting the flag fanned the
    //            change out over every page. That is a rule about one FIELD of this request, not about reaching
    //            this route, so no [Authorize] attribute can express it: the policy on this action admits the
    //            page administrator by design. The service owns the rule and reports a distinct failure reason,
    //            which the shared status table answers with 403 - the status declared below.
    //
    // MIGRATION: request.TabId SELECTS THE PLACEMENT THIS UPDATE ADDRESSES; IT DOES NOT MOVE IT.
    //            ModuleSettings.ascx.vb L398-L408 compared the selected page with the current one and called
    //            ModuleController.MoveModule, whose implementation copied the placement and its scoped
    //            settings before deleting the source. That is not reproduced, because this contract carries a
    //            single page identifier and a move needs two - the placement being edited and its
    //            destination. The service resolves the placement on the named page and answers
    //            module.placement_not_found, which the shared status table maps to the 404 declared below,
    //            when the module does not occupy it. The reduction is recorded in MIGRATION_NOTES.md.
    [HttpPut("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto?>>> UpdateAsync(
        int moduleId,
        [FromBody] UpdateModuleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result<ModuleDetailDto?> outcome = await _modules
            .UpdateModuleAsync(portalId, moduleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a module, or removes one of its placements.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Removes only this placement, leaving the module on its other tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the deletion has been applied.</returns>
    /// <remarks>
    /// Naming a placement removes that placement and leaves the module on its other pages; omitting one
    /// recycles the module itself. The legacy surface expressed the same distinction with three separate
    /// members, one of them taking an explicit flag for whether the underlying module went with it, which is
    /// the evidence that these are two operations rather than one. A nullable placement identifier says which
    /// row is addressed, so no caller has to know what a boolean meant. Recycling is a SOFT delete: the row
    /// survives with its deleted marker set, and the listing hides it unless deleted modules are asked for.
    /// </remarks>
    [HttpDelete("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _modules
            .DeleteModuleAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a module's settings.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The settings, or <c>404 Not Found</c> when the module does not exist in this portal.</returns>
    // MIGRATION: H5. -1 AND 0 ARE DIFFERENT, MEANINGFUL CACHE PERIODS AND BOTH REACH THE WIRE UNCHANGED. The
    //            legacy page tested "If objModuleDef.DefaultCacheTime = Null.NullInteger" at
    //            ModuleSettings.ascx.vb L138 and, on a match, HID the cache row entirely - so -1 on the
    //            definition is the marker for "this module kind offers no default cache period", not a period of
    //            minus one. Independently, L349-L352 stored 0 when the caller left the field blank. Coalescing
    //            either into the other, or into null, would silently change which modules expose a cache
    //            control and how long the rest cache for. Serialisation is configured once for the whole API to
    //            emit null-valued and default-valued members rather than omit them, which is what carries this
    //            distinction across the boundary; nothing is suppressed per action.
    [HttpGet("{moduleId:int}/settings")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(ApiResponse<ModuleSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleSettingsDto?>>> GetSettingsAsync(
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleSettingsDto?> outcome = await _modules
            .GetModuleSettingsAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a module's settings.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the settings have been stored.</returns>
    /// <remarks>
    /// The two dictionaries are lifted out of the request object and passed as the service's own two
    /// arguments. That is unwrapping the transport shape, not a decision: the service takes them separately
    /// because they land in two different tables - one keyed by the module, one by the placement - and
    /// merging them here would lose which is which.
    /// </remarks>
    [HttpPut("{moduleId:int}/settings")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateSettingsAsync(
        int moduleId,
        [FromBody] ModuleSettingsDto settings,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        if (settings is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result outcome = await _modules
            .UpdateModuleSettingsAsync(
                portalId,
                moduleId,
                tabModuleId,
                settings.ModuleSettings,
                settings.TabModuleSettings,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Exports a module's content.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="request">Names the file the caller intends to save the payload as.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The exported payload as a downloadable document.</returns>
    /// <remarks>
    /// A module that declares no content-portability contract exports successfully with an empty payload,
    /// which is the legacy behaviour: the export page produced a file whether or not the module had anything
    /// to put in it. An empty body and a failure are therefore different answers here, and the distinction is
    /// preserved rather than collapsed into a 404.
    /// </remarks>
    // MIGRATION: H7a. THE LATE-BOUND ACTIVATION IS GONE, AND THE COM EXCLUSION IT LOOKED LIKE IS VACUOUS.
    //            Export.ascx.vb L150 gated on "BusinessControllerClass <> "" And IsPortable", L152 handed that
    //            stored type NAME to Framework.Reflection.CreateObject, L155 re-tested the result with
    //            "TypeOf objObject Is IPortable" and L157 called ExportModule to get a string of XML. Those are
    //            two of the only five late-bound activation sites in scope - the others are ModuleController.vb
    //            L231 and L431 and EventMessageProcessor.vb L32, L52 and L77 - and every one is ordinary .NET
    //            reflection, so the exclusion covering COM interop, VB6 and ActiveX removes NOTHING from this
    //            codebase and is reported as vacuous rather than claimed as work. A module's portability
    //            behaviour is now resolved from a closed, dependency-injected set inside the service. The factory
    //            that does it is deliberately NOT injected here: an action that could activate module code would
    //            put business behaviour in the presentation layer, and no type name crosses this boundary to be
    //            activated.
    //
    // MIGRATION: H7b. THE PAYLOAD IS RETURNED; THE FILESYSTEM HALF OF THE LEGACY EXPORT IS DROPPED. Having built
    //            the document, the legacy page resolved PortalSettings.HomeDirectoryMapPath, asked
    //            HasSpaceAvailable (Export.ascx.vb L168-L169) and refused with DiskSpaceExceeded (L189), then
    //            wrote the file with File.CreateText (L172) and registered it through the file and folder
    //            controllers (L184). The file system subsystem is excluded wholesale, so that entire path is one
    //            of this migration's named functional reductions: the document comes back in the response for
    //            the caller to save, no server path is produced, and DiskSpaceExceeded has no counterpart
    //            because nothing is written.
    //            Two legacy outcomes are kept and one is not. Empty content, which L192 reported as NoContent,
    //            remains a distinct answer rather than being collapsed into an absence. The unsupported-module
    //            refusal is kept. The bare "Catch" whose only effect was a generic Error message is NOT kept:
    //            a swallowed failure that reported success is the behaviour this surface exists to stop
    //            reproducing, so a module fault becomes a 500 through the shared status table.
    //
    // The success media type is declared PER RESPONSE rather than with a Produces attribute on the action,
    // and the difference is load-bearing rather than stylistic. Produces rewrites the permitted media types
    // of any ObjectResult the action returns, so declaring application/xml here forced the FAILURE response
    // - a problem document, which is an ObjectResult - to be negotiated as XML. No XML formatter is
    // registered, so every refusal from this one action answered 406 Not Acceptable with no body instead of
    // the problem document it advertises. Stating the media type on the 200 response alone documents the
    // XML payload accurately in the published description while leaving the class-level JSON negotiation to
    // carry the failures, which is what makes this action's errors identical to every other action's. The
    // success path returns a content result, which no negotiation touches.
    [HttpPost("{moduleId:int}/export")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [RequestSizeLimit(ServiceCollectionExtensions.MaximumRequestBodyBytes)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, ExportContentType)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ExportAsync(
        int moduleId,
        [FromBody] ModuleExportRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result<string> outcome = await _modules
            .ExportModuleAsync(portalId, moduleId, request, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            // Routed through the shared translator rather than assembled here. A hand-built payload
            // omitted the problem type and the trace identifier that every other failure in this API
            // carries, and it fixed a title of its own that disagreed with the vocabulary the status code
            // is registered under - so a client parsing this API's errors had to special-case one action.
            // The status code comes from the same table as everywhere else, which is also what promotes a
            // module-execution fault out of the caller-correctable range.
            return this.Failed(outcome);
        }

        return Content(outcome.Value, ExportContentType);
    }

    /// <summary>Imports content into a module.</summary>
    /// <param name="request">The module to import into, and the content to import.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the content has been imported.</returns>
    /// <remarks>
    /// The target module travels in the body rather than in the path because the legacy import page chose its
    /// target from a list on the form, and because an import addressed at a module in the URL would read as
    /// idempotent when it is not.
    /// </remarks>
    // MIGRATION: H8. THE DOCUMENT IS PARSED BY THE SERVICE, NEVER HERE. Import.ascx.vb read the file at L184 and
    //            then, at L188-L192, constructed an XmlDocument and called LoadXml inside a Try whose Catch
    //            produced the NotValidXml message; on success it read the type attribute at L196, compared it
    //            with the module's own name, read the version attribute at L198 and passed the document's
    //            InnerXml to ImportModule at L200. Every part of that is application behaviour: no XML type is
    //            constructed in this file, no attribute is read, and no parse is wrapped in a try/catch to shape
    //            an error. The service reports a malformed document as a failure reason and the shared status
    //            table answers it with 400, which is the caller-correctable classification the code earns by
    //            naming no request member. The mismatched-type and unsupported-module refusals arrive the same
    //            way. One argument disappears entirely rather than moving: the legacy call passed
    //            UserInfo.UserID as the acting account, which the service now takes from the authenticated
    //            caller, because an identifier a request could choose for itself would let one account attribute
    //            an import to another.
    //
    // MIGRATION: H9. THE TARGET ARRIVES IN THE BODY, WHICH IS WHAT DECIDES THIS ACTION'S POLICY. The route
    //            carries no module identifier, so the module view and module edit policies CANNOT be used here:
    //            their handler resolves the module from route data, finds nothing, and fails closed - it would
    //            refuse every import, at request time, with nothing at compile time to catch it. The
    //            portal-administrator policy is the correct grain because it evaluates against the portal the
    //            route does name, and the per-resource check on the module named in the body is the service's.
    //            The legacy page had no in-code role check of its own at all, taking its protection from the
    //            permissions of the administration page hosting it, so an explicit policy replaces an ambient
    //            one here rather than a stated one.
    [HttpPost("import")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [RequestSizeLimit(ServiceCollectionExtensions.MaximumRequestBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ImportAsync(
        [FromBody] ModuleImportRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result outcome = await _modules
            .ImportModuleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
