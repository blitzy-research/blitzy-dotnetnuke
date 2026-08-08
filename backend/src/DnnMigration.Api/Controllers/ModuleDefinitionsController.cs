// MIGRATION: this controller replaces the READ half of two legacy controllers and nothing else.
// Measured against the source rather than estimated, and the paths below are the measured ones:
// Library/Components/Modules/DesktopModuleController.vb is 92 lines with 12 members, and
// Library/Components/Modules/ModuleDefinitionController.vb is 64 lines with 7 - nineteen members, of
// which exactly ONE endpoint survives here. Each group below names where the behaviour went, so that
// nothing looks lost when it is merely elsewhere.
//
// MIGRATION: the plan cites the definition controller under a Definitions subfolder. NO SUCH FOLDER
// EXISTS - `ls Library/Components/Modules/Definitions` reports no such file or directory. The class
// declares `Namespace DotNetNuke.Entities.Modules.Definitions`, which is what the folder name was
// inferred from, but the file itself is flat beside its sibling. Reported and not corrected, because
// the legacy trees are read-only reference inputs: the correct path is
// Library/Components/Modules/ModuleDefinitionController.vb.
//
// MIGRATION: the seven MUTATORS are not ported - DesktopModuleController.vb:L32 (AddDesktopModule),
// :L36 (AddPortalDesktopModule), :L40 (DeleteDesktopModule), :L45 (DeletePortalDesktopModules),
// :L70 (UpdateDesktopModule) and ModuleDefinitionController.vb:L32 (AddModuleDefinition), :L36
// (DeleteModuleDefinition), :L53 and :L57 (UpdateModuleDefinition). This is evidence-based rather
// than an assumption: there is no administration screen anywhere under Website/admin/Modules/ for
// the definition catalogue. The only three screens there are ModuleSettings.ascx.vb (485 lines),
// Export.ascx.vb (227) and Import.ascx.vb (245), and none of them manages a definition - the first
// merely READS one, at L136 to L142, to decide whether to show a cache-timeout field. Definitions
// arrive in the database through module installation, and the installer subsystem is excluded from
// this migration wholesale. Consequently this controller declares no POST, PUT, PATCH or DELETE
// action, and none may be added here: a write endpoint over installer-owned reference data would
// invite a partial reimplementation of the installer. Contrast the profile-definition surface, which
// IS full CRUD precisely because a legacy screen provides add, edit, delete and reorder.
//
// MIGRATION: the grant mutators deserve their own note, because they look like ordinary writes.
// AddPortalDesktopModule and DeletePortalDesktopModules write dbo.PortalDesktopModules, which is the
// table that decides whether a PREMIUM desktop module is offered to one portal. Granting a premium
// module to a tenant is a provisioning act belonging to the same excluded installer surface, so no
// endpoint here adds or removes a grant. The grant is still honoured when reading: the read below
// returns only definitions whose desktop module is either not premium or has been granted.
//
// MIGRATION: the twelve legacy READS collapse into the three actions below. Nine were lookups that
// differed only in which column they filtered on - DesktopModuleController.vb:L50 (by identifier),
// :L54 (by module name), :L58 (all), :L62 (by portal), :L66 (portal grants), :L80 and :L85 (by
// display name) and ModuleDefinitionController.vb:L41 (by identifier), :L45 (by desktop module and
// display name), :L49 (by desktop module) - and each returned an untyped ArrayList hydrated by
// reflection through CBO.FillCollection. Three of the nine survive as addresses of their own because
// the specified surface names them - the catalogue, ONE definition by identifier, and the definitions
// of ONE installed package - and each maps to a member of the application contract, so this controller
// still invents no query. The remaining six differed only by a column this API has no reason to expose
// separately. The two display-name lookups were also both marked <Obsolete> in the source, on the
// stated grounds that a display name "is not guaranteed to be the same as when the module is created",
// so reproducing them would have carried a defect forward.
//
// MIGRATION: the by-identifier and by-package reads are PORTAL-SCOPED, and that is a deliberate
// narrowing of the legacy members they replace. ModuleDefinitionController.vb:L41 and :L49 answered
// from the whole installation with no tenant argument at all, so a caller naming an identifier learnt
// about a definition regardless of whether its portal had been granted the package. Both reads here
// narrow the portal's own catalogue instead, so the premium-grant rule applies to a single-row read
// exactly as it applies to the collection, and a definition the tenant may not instantiate is reported
// as absent. Without that narrowing the pair would be a way to enumerate another tenant's catalogue one
// identifier at a time.
//
// MIGRATION: reflection-based provider access is gone. Every legacy member reached its data through
// DataProvider.Instance(), a reflection-instantiated singleton, and hydrated rows through the
// reflection helper CBO; the two cache-clearing calls at ModuleDefinitionController.vb:L38 and :L59
// invalidated a static cache from inside the data layer. All three are replaced by the
// constructor-injected application service below; this controller performs no data access, holds no
// persistence type and knows nothing about caching.

using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The module definition catalogue: which kinds of module this portal may place on a page.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and that is a boundary rather than a phase.</strong> The catalogue describes
/// what module definitions <em>exist and are available</em>; it is never written through this API.
/// Installing a definition means writing files into the application directory and registering a
/// package - the legacy module-installer path, which this migration excludes - so a write endpoint
/// here would be an invitation to reimplement it. The provenance comments at the head of this file
/// account for all nineteen measured legacy members, including the nine that are deliberately not
/// ported and the reason for each.
/// </para>
/// <para>
/// <strong>Asks, never decides.</strong> Each of the three actions here poses a question to the
/// application service and translates the answer into a status code. None applies filtering, sorting,
/// grouping, name matching or caching of its own, so no rule is expressed here that could drift away
/// from the rule expressed in the service - and in particular the by-identifier and by-package reads
/// narrow nothing themselves, because the narrowing they need is the premium-grant rule the service
/// already owns.
/// </para>
/// <para>
/// <strong>The catalogue is host-level data read through a portal-shaped question.</strong>
/// <c>dbo.ModuleDefinitions</c> carries no portal column at all - its terminal shape is
/// <c>ModuleDefID</c>, <c>DesktopModuleID</c>, <c>FriendlyName</c> and <c>DefaultCacheTime</c> - so
/// the definitions themselves belong to the installation. What is portal-specific is which of them a
/// tenant may use: a premium desktop module is offered only where <c>dbo.PortalDesktopModules</c>
/// grants it. That is why the address is not a portal sub-resource while the answer still differs per
/// tenant, and why the tenant is taken from the request rather than from a caller-supplied value.
/// </para>
/// <para>
/// <strong>Content-editor gated, and it fails closed.</strong> The class-level policy is
/// <see cref="PolicyNames.PortalContentEditor"/>: the tenant's administrators, plus any caller holding
/// EDIT on any one of its pages. It fails closed because a caller holding EDIT nowhere in the tenant is
/// refused outright, which is every caller the stricter reading was protecting this catalogue from.
/// </para>
/// <para>
/// It is deliberately NOT <see cref="PolicyNames.PortalAdministrator"/>, and the reason is measured
/// rather than chosen. The legacy gate on the screen this catalogue backs admitted a PAGE administrator
/// as well as a portal one (<c>ModuleSettings.ascx.vb:L191</c>), and where that screen went on to
/// disable four controls for a caller outside the administrators role
/// (<c>ModuleSettings.ascx.vb:L214-L219</c>), the module-type selector this catalogue fills is
/// conspicuously not among them. Requiring tenant administration here therefore refused a caller the
/// legacy admitted, and refused them in the way hardest to see - the create action carries no policy at
/// all, so the caller reached the placement form and was handed an empty selector by a supporting read
/// they were not permitted to make. The measured detail is recorded at the attribute itself.
/// </para>
/// <para>
/// Neither the module nor the page policy is workable at this address, and that constraint is what
/// <see cref="PermissionScope.Portal"/> exists to satisfy: both resolve their scope from route data,
/// and this route carries neither a module nor a page identifier - a definition identifier is not a
/// module identifier - so either of them could only ever refuse. The portal scope reads no item
/// identifier from the route at all, which is why it can gate an address like this one. It is not a
/// licence to enumerate: an endpoint carrying it answers a capability question, so it remains
/// responsible for narrowing what it returns, and this one already answers per tenant.
/// </para>
/// <para>
/// MIGRATION: the legacy gate answered a refusal with
/// <c>Response.Redirect(NavigateURL("Access Denied"), True)</c>, which rendered a localised message
/// page. A JSON API has no page to redirect to, so the target answers a plain <c>403 Forbidden</c>
/// from the authorisation middleware and the redirect is not reproduced. A caller who has presented
/// no credential at all receives <c>401 Unauthorized</c> instead, a distinction the legacy redirect
/// could not express.
/// </para>
/// <para>
/// MIGRATION: no identifier accepted or emitted by this controller carries a numeric sentinel, and no
/// range constraint is placed on one. The legacy sentinel for a missing integer is -1
/// (<c>Library/Components/Shared/Null.vb:L41-L45</c>) and that value is not free: the portal table is
/// <c>IDENTITY (-1, 1)</c>, so -1 is its seed and first generated value while the shipped default portal
/// row carries an explicit 0 - both real portal keys - and the role, page and module tables all seed at 0,
/// so 0 is a real identifier there too. <c>ModuleDefinitions.ModuleDefID</c> is
/// <c>IDENTITY (1, 1)</c>, so a definition is never numbered 0 or -1, but that is a fact about one
/// table rather than a licence to test identifiers against a lower bound anywhere.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/module-definitions")]
// THE CATALOGUE IS READ BY WHOEVER MAY PLACE A MODULE, NOT ONLY BY THE TENANT'S ADMINISTRATOR, and the
// distinction is measured rather than chosen. ModuleSettings.ascx.vb:L214-L219 disabled four controls for a
// caller outside the administrators role - chkAllTabs, chkDefault, chkAllModules and cboTab - and cboModuleType,
// the module-type selector this catalogue backs, is conspicuously NOT among them. A page administrator picked a
// module type; what they could not do was promote the module across the tenant's pages.
//
// Declaring tenant administration here therefore refused a caller the legacy admitted, and refused them in the
// way hardest to see: the create ACTION carries no policy at all, deliberately, so a page administrator reached
// the placement form and was then handed an empty type selector by a supporting read they were not permitted to
// make. The capability was reachable and unusable. PortalContentEditor admits the callers that action admits -
// the tenant's administrators, plus anyone holding EDIT on any of its pages - and refuses a caller holding EDIT
// nowhere, which is every caller tenant administration was protecting this catalogue from.
[Authorize(Policy = PolicyNames.PortalContentEditor)]
[Produces("application/json")]
public sealed class ModuleDefinitionsController : ControllerBase
{
    /// <summary>
    /// Failure code carried as the problem type when the request reached this action without a tenant.
    /// </summary>
    /// <remarks>
    /// A distinct code from the middleware's generic refusal, so an operator reading a support log can
    /// tell "this host resolves to no portal" apart from "this caller lacks the grant" - while the caller
    /// reads the same fixed wording either way and learns nothing from the difference.
    /// </remarks>
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>The catalogue reader this controller delegates to.</summary>
    private readonly IModuleService _modules;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="ModuleDefinitionsController"/> class.</summary>
    /// <param name="modules">The module service, which owns the definition catalogue.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Two dependencies, both contracts rather than implementations, and neither is a persistence type:
    /// the repositories and the persistence context are unreachable from this layer by design. There is
    /// no separate definition or desktop-module service to inject - the definition catalogue is part of
    /// the module service's surface, because a definition is only ever read in order to place a module.
    /// </remarks>
    public ModuleDefinitionsController(IModuleService modules, IPortalContextHolder portalContext)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Lists the module definitions the addressed portal may instantiate.</summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// The definitions available to the tenant this request addresses, in a stable order, each joined
    /// to the identity and capability facts of its owning desktop module.
    /// </returns>
    /// <response code="200">
    /// The catalogue, inside the shared success envelope: the definitions are the envelope's payload
    /// rather than the whole body. An empty payload is a legitimate answer - it means the portal has
    /// been granted no definitions - and is never reported as a failure.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses, or
    /// the request host is not a configured portal alias and so addresses no portal at all.
    /// </response>
    /// <remarks>
    /// <para>
    /// This action, together with the two reads below it, replaces all twelve legacy catalogue reads; the
    /// head of this file records which, and why the six that survive as no address at all do not.
    /// </para>
    /// <para>
    /// MIGRATION: the portal is taken from the tenant the alias-resolution middleware resolved from the
    /// request host, and from nowhere else. It is deliberately not accepted from the caller. Two
    /// reasons, and the second is the load-bearing one. First, fidelity: the legacy screen ran inside a
    /// portal that the request alias had already fixed, so it could not have been asked about another
    /// tenant's catalogue. Second, safety: the class-level policy is evaluated against the
    /// host-resolved portal, so a caller-supplied portal identifier would be authorised against one
    /// tenant and answered about another. Because the policy already refuses a request whose host
    /// resolves to nothing, no query-string fallback is offered here - one would be accepted and then
    /// ignored, which is worse than not existing.
    /// </para>
    /// <para>
    /// MIGRATION: <c>DefaultCacheTime</c> reaches the wire exactly as stored, including -1. On this
    /// member -1 is real data rather than an absent value: <c>ModuleSettings.ascx.vb:L136-L142</c> reads
    /// <c>If objModuleDef.DefaultCacheTime = Null.NullInteger Then rowCache.Visible = False</c>, so -1
    /// means "caching does not apply to this definition; hide the cache-timeout field". The contract
    /// therefore carries it as a non-nullable integer, and the serialisation decision recorded here is
    /// that it is emitted as <c>-1</c> - never as <c>null</c>, which would erase the distinction, and
    /// never as <c>0</c>, which is a genuine timeout the legacy screen would have displayed. Null
    /// members are omitted from responses by the single JSON configuration this API uses, which is
    /// precisely why a sentinel that means something must not be mapped to null when responding.
    /// </para>
    /// <para>
    /// The outcome is translated by the shared translator rather than by a table written here, so every
    /// endpoint in this API answers the same failure code with the same status. Under that translator a
    /// successful outcome carrying no value at all becomes a <c>404</c>, and a failed one becomes a
    /// problem document whose status is chosen from its failure code.
    /// </para>
    /// <para>
    /// MIGRATION: neither a <c>400</c> nor a <c>404</c> is declared above, and both omissions are
    /// decisions rather than oversights. There is nothing here for a caller to get wrong - the request
    /// carries no route parameter, no query parameter and no body, so no binding or validation failure
    /// can arise - and the application contract specifies a catalogue read that always succeeds with a
    /// sequence, empty where nothing is available and never null. Reporting statuses that cannot occur
    /// would put two responses into the published OpenAPI document that no caller will ever observe.
    /// </para>
    /// <para>
    /// Paging is deliberately absent. The catalogue is small, bounded reference data written only by
    /// module installation, so the whole sequence is returned rather than a page of it - which is also
    /// what the legacy reads did, each returning an entire collection.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ModuleDefinitionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ModuleDefinitionDto>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        // The tenant is read through the holder rather than from the request's feature bag: the
        // middleware publishes it nowhere else, and an abstraction is the one thing a caller cannot
        // reach in to replace. Resolution is tested before the tenant is read because the holder
        // throws rather than returning a placeholder - a placeholder tenant would be silently wrong
        // instead of loudly absent.
        //
        // WHAT REFUSES FIRST DEPENDS ON THE CALLER. The tenant-resolution middleware records an unresolved
        // host and continues. A portal administrator is then refused by the class policy; a host account
        // passes that policy because host authority is installation-wide and is refused by this guard. The
        // guard stays because the alternative is an InvalidOperationException from the holder, and a 500 is
        // a worse answer than a 403 for a condition that is not the caller's fault.
        //
        // The refusal is produced through the shared problem-details path rather than by Forbid(), and with
        // the same failure code the middleware uses. A controller's Forbid() does NOT pass through the
        // authorisation middleware's result handler, so it answered with an empty body while this action
        // declares a problem document for 403 - which made this path distinguishable from the middleware's
        // refusal for the identical cause, the opposite of what the surrounding comment claimed. Both now
        // carry the same status, the same problem type and a trace identifier, so a client keying on the
        // failure code cannot tell them apart.
        //
        // The human-readable detail is NOT byte-identical, and an earlier revision of this comment claimed
        // it was. The middleware states that the request could not be associated with a portal; the shared
        // helper states that the caller is not permitted to perform the operation. Both are fixed,
        // caller-independent sentences that name no host, no alias and no tenant, so neither discloses which
        // layer refused or why - which is the property that actually matters here. The wording is left
        // divergent rather than unified because the helper's sentence is shared with every other 403 in this
        // API, and bending it to this one cause would make it wrong everywhere else.
        if (!_portalContext.IsResolved)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // The portal identifier is forwarded exactly as resolved. Clamping it, or treating any
        // particular value as "absent", would be a second and quieter copy of a rule that belongs to
        // the tenant boundary - and -1 is a genuine portal here, so such a copy would be wrong.
        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await _modules
            .ListModuleDefinitionsAsync(_portalContext.Current.PortalId, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its value
        // touched, and maps any failure code to a status through the one table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one module definition available to the addressed portal.</summary>
    /// <param name="moduleDefinitionId">
    /// Identifier of the definition wanted. Forwarded exactly as bound: no lower bound is imposed here,
    /// for the reason recorded at the head of this file.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    /// <response code="200">The definition.</response>
    /// <response code="400">The identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses, or
    /// the request host is not a configured portal alias and so addresses no portal at all.
    /// </response>
    /// <response code="404">
    /// The addressed portal has no such definition available. That covers both a definition that does not
    /// exist and one whose package the portal has not been granted, and the two are DELIBERATELY
    /// indistinguishable: telling them apart would let one tenant enumerate another's catalogue by
    /// identifier.
    /// </response>
    /// <remarks>
    /// <para>
    /// Replaces the legacy single-definition read. It is scoped to the addressed portal rather than to the
    /// installation, so it answers the same question the catalogue above answers, for one row.
    /// </para>
    /// <para>
    /// Read-only, like everything on this resource: a definition is written by module installation, which
    /// this migration excludes, so there is no create, update or delete action to pair with this read.
    /// </para>
    /// </remarks>
    // The declared success type is the ENVELOPE, which is what this action actually returns. Declaring the
    // bare payload here made the published contract disagree with the response for one endpoint out of the
    // whole API, so a client generated from the document unwrapped every other resource and this one twice.
    // The refusal statuses declare a body for the same reason: a status advertised without one forces a
    // special case for an endpoint that does not have one.
    [HttpGet("{moduleDefinitionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ModuleDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDefinitionDto?>>> GetAsync(
        int moduleDefinitionId,
        CancellationToken cancellationToken)
    {
        // The same precondition the catalogue read states: the holder throws rather than returning a
        // placeholder tenant, and under the class-level policy an unresolved host has already been refused.
        if (!_portalContext.IsResolved)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleDefinitionDto?> outcome = await _modules
            .GetModuleDefinitionAsync(_portalContext.Current.PortalId, moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no value means the definition is not available here, which the
        // shared translator answers as 404 - the convention every single-record read in this API follows.
        return this.Complete(outcome);
    }

    /// <summary>Lists the definitions one installed package declares for the addressed portal.</summary>
    /// <param name="desktopModuleId">
    /// Identifier of the installed package whose definitions are wanted. Forwarded exactly as bound.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definitions that package declares, in the catalogue's own order.</returns>
    /// <response code="200">
    /// The definitions, as a JSON array. An empty array is a legitimate answer - the package may declare
    /// none, or may not be available to this portal - and is never reported as a failure, which is the same
    /// treatment the catalogue gives an empty result.
    /// </response>
    /// <response code="400">The identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses, or
    /// the request host is not a configured portal alias and so addresses no portal at all.
    /// </response>
    /// <remarks>
    /// <para>
    /// Replaces the legacy read that took a desktop-module identifier and returned every definition under
    /// it. One installed package commonly declares several definitions, and the legacy administration
    /// screens grouped them this way when offering the definitions of a chosen package.
    /// </para>
    /// <para>
    /// The address is a sub-resource of this catalogue rather than a <c>desktopModuleId</c> filter on it,
    /// because it answers a different question: the catalogue answers "what may this portal instantiate",
    /// while this answers "what does this package offer it". Paging is absent here for the same reason it
    /// is absent above - a package declares definitions in the single figures.
    /// </para>
    /// </remarks>
    // Every advertised refusal declares a body, and the shape is the general problem document rather than
    // the validation one: this action's request carries no content and no query, so there are no request
    // members a validation document could name - a path segment constrained to an integer refuses a value of
    // the wrong shape before the action is reached at all.
    [HttpGet("desktop-modules/{desktopModuleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ModuleDefinitionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ModuleDefinitionDto>>>> ListForDesktopModuleAsync(
        int desktopModuleId,
        CancellationToken cancellationToken)
    {
        if (!_portalContext.IsResolved)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await _modules
            .ListDesktopModuleDefinitionsAsync(
                _portalContext.Current.PortalId,
                desktopModuleId,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
