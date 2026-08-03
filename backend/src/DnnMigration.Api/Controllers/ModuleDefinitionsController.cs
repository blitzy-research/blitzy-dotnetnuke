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
// MIGRATION: the twelve legacy READS collapse into the single action below. Nine were lookups that
// differed only in which column they filtered on - DesktopModuleController.vb:L50 (by identifier),
// :L54 (by module name), :L58 (all), :L62 (by portal), :L66 (portal grants), :L80 and :L85 (by
// display name) and ModuleDefinitionController.vb:L41 (by identifier), :L45 (by desktop module and
// display name), :L49 (by desktop module) - and each returned an untyped ArrayList hydrated by
// reflection through CBO.FillCollection. The application contract exposes one portal-scoped read
// that already joins a definition to its owning desktop module, so one endpoint covers the picker's
// whole need without inventing behaviour. The two display-name lookups were also both marked
// <Obsolete> in the source, on the stated grounds that a display name "is not guaranteed to be the
// same as when the module is created", so reproducing them would have carried a defect forward.
//
// MIGRATION: no per-definition endpoint exists, and its absence is a decision. The legacy single
// lookups at ModuleDefinitionController.vb:L41 and DesktopModuleController.vb:L50 have no counterpart
// on the application contract, whose only definition member returns the whole catalogue for a portal.
// Adding an address for one definition would mean inventing a service member, and a controller that
// invents its own query is a controller that has started holding business logic.
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
/// <strong>Asks, never decides.</strong> The single action here poses a question to the application
/// service and translates the answer into a status code. It applies no filtering, sorting, grouping,
/// name matching or caching of its own, so no rule is expressed here that could drift away from the
/// rule expressed in the service.
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
/// <strong>Administrator-gated, and it fails closed.</strong> The class-level policy is
/// <see cref="PolicyNames.PortalAdministrator"/>, which is the declarative equivalent of the legacy
/// gate <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> that guarded the module
/// administration screens. It is also the only workable policy for this address: the module and page
/// policies resolve their scope from route data, and this route carries neither a module nor a page
/// identifier - a definition identifier is not a module identifier - so either of them could only
/// ever refuse.
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
/// <c>IDENTITY (-1, 1)</c>, so -1 identifies the first real portal, and the role, page and module
/// tables all seed at 0, so 0 is a real identifier too. <c>ModuleDefinitions.ModuleDefID</c> is
/// <c>IDENTITY (1, 1)</c>, so a definition is never numbered 0 or -1, but that is a fact about one
/// table rather than a licence to test identifiers against a lower bound anywhere.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/module-definitions")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class ModuleDefinitionsController : ControllerBase
{
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
    /// The catalogue, as a JSON array. An empty array is a legitimate answer - it means the portal has
    /// been granted no definitions - and is never reported as a failure.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses, or
    /// the request host is not a configured portal alias and so addresses no portal at all.
    /// </response>
    /// <remarks>
    /// <para>
    /// This one action replaces all twelve legacy catalogue reads; the head of this file records which,
    /// and why no per-definition address exists.
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
    [ProducesResponseType(typeof(IReadOnlyList<ModuleDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ModuleDefinitionDto>>> ListAsync(
        CancellationToken cancellationToken)
    {
        // The tenant is read through the holder rather than from the request's feature bag: the
        // middleware publishes it nowhere else, and an abstraction is the one thing a caller cannot
        // reach in to replace. Resolution is tested before the tenant is read because the holder
        // throws rather than returning a placeholder - a placeholder tenant would be silently wrong
        // instead of loudly absent. Under the class-level policy an unresolved host has already been
        // refused, so this is a precondition rather than a branch a caller can steer into; it answers
        // with the same bare 403 the authorisation middleware would have produced for that very cause,
        // so the two paths are indistinguishable to a client.
        if (!_portalContext.IsResolved)
        {
            return Forbid();
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
}
