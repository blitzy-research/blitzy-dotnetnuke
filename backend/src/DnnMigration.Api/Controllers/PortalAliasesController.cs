using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The portal alias resource: the host names that resolve to a portal.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Portal/PortalAlias.ascx.vb</c> and
/// <c>EditPortalAlias.ascx.vb</c>.
/// </para>
/// <para>
/// <strong>Every alias, collection and individual alike, is addressed beneath its owning portal.</strong>
/// An earlier revision addressed the individual alias at its own top-level path, on the reasoning that the
/// alias key is a surrogate unique across the installation and so needs no portal beside it to find the row.
/// That reasoning was exactly backwards. Uniqueness makes the key guessable across tenants, and the routes
/// carried no tenant segment for a policy to bind, so a caller authorised over one portal could read, rename
/// or unbind any alias in the installation by counting upwards - and because tenant resolution matches on the
/// alias, renaming one re-points another tenant's traffic while unbinding one makes that tenant unreachable.
/// Nesting the individual alias gives the tenant-bound policy a <c>portalId</c> to bind, and the service
/// members now take the owning portal and compare it against the stored row, so the segment is validated
/// rather than decorative. Both layers check, because neither is sufficient alone.
/// </para>
/// <para>
/// The one route that is not nested is the installation-wide listing, which cannot be: it exists to show
/// every alias across every portal. It is therefore guarded by installation-wide host authority instead of
/// by tenant administration.
/// </para>
/// <para>
/// <strong>Authority is declared per action, not on the class.</strong> Five of the six actions address one
/// tenant's aliases and name <see cref="PolicyNames.PortalAdministrator"/>, whose handler binds a route's
/// tenant to the tenant the request resolved to. The sixth - the host-wide enumeration - spans every tenant
/// by definition, so it can carry no tenant to bind and names
/// <see cref="PolicyNames.HostAdministrator"/> instead. The class-level attribute is therefore
/// authentication only: authorisation attributes COMBINE rather than override, so a class-level tenant
/// policy would be ANDed onto the host-wide action and would make it unreachable by the only credential
/// entitled to it.
/// </para>
/// <para>
/// REPORTED GAP, not introduced here and not closed here. The three by-key actions carry no portal segment,
/// so the tenant-binding handler has nothing to compare and an administrator of one tenant can still
/// address another tenant's alias by its key. The legacy screens enforced that rule against the LOADED
/// RECORD rather than against the URL - <c>EditPortalAlias.ascx.vb:L65-L70</c> and L181-L186 both read the
/// alias, compare <c>objPortalAliasInfo.PortalID</c> against the ambient portal, and refuse with "You do
/// not have access to view this Portal Alias" unless the caller is a host account - so closing it properly
/// means an ownership check inside the service, which owns the loaded record, and a change to three
/// service members that this checkpoint's findings do not cover. Recorded so the omission is a decision
/// rather than an oversight.
/// </para>
/// <para>
/// The write actions take dedicated request contracts carrying the host name alone, each with its own
/// declarative validator, and the read representation is never accepted as input. MIGRATION: accepting the
/// read DTO on a write let a caller supply an alias key that a create ignores and a portal key that an
/// update refuses to re-bind, so a request could name a tenant it was silently not given. Shape, length and
/// canonical form are settled declaratively here; uniqueness remains a database question and is answered by
/// the service, which reports it as a conflict.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize]
public sealed class PortalAliasesController : ControllerBase
{
    /// <summary>
    /// Why the identifier-only alias actions may be served without a resolved tenant.
    /// </summary>
    /// <remarks>
    /// THE REPAIR PATH MUST NOT DEPEND ON WHAT IT REPAIRS. These four actions are how an operator inspects and
    /// corrects the alias table, so requiring the host name to resolve against that table before they could be
    /// reached would make a mistyped alias unrecoverable: every route capable of fixing it would be refused for
    /// precisely the reason it needed fixing. Each is restricted to a host account, and each resolves the portal
    /// it concerns from the stored alias row rather than from the host name.
    /// </remarks>
    private const string AliasRepairJustification =
        "Host-only alias administration: the path by which a wrong or missing alias is repaired, so it must "
        + "not itself require an alias to resolve. The portal is read from the stored alias row.";

    private readonly IPortalService _portals;

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    // validated by FluentValidationActionFilter, which is registered once for the whole API, runs before
    // the action and resolves a validator from each argument's declared type. This controller used to
    // take validators of its own and invoke them by hand as well, which was a second invocation path for
    // one rule set and the reason the paging contract was judged against the wrong sortable vocabulary.
    // Adding a validator argument back here would recreate that split.
    /// <summary>Initialises a new instance of the <see cref="PortalAliasesController"/> class.</summary>
    /// <param name="portals">The portal service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portals"/> is <see langword="null"/>.</exception>
    public PortalAliasesController(IPortalService portals)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
    }

    /// <summary>Lists the aliases of one portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portal's aliases.</returns>
    [HttpGet("portals/{portalId:int}/aliases")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PortalAliasDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PortalAliasDto>>>> ListForPortalAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PortalAliasDto>> outcome = await _portals
            .ListPortalAliasesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists every alias on this host.</summary>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>Every alias, across all portals.</returns>
    /// <remarks>
    /// <para>
    /// The host-wide view exists because alias collisions are a host-level problem: an operator diagnosing
    /// why one portal is answering for another needs to see every alias at once, which no per-portal view
    /// can show.
    /// </para>
    /// <para>
    /// <strong>And that is exactly why it requires HOST authority rather than tenant authority.</strong>
    /// The projection spans every tenant by definition, so there is no tenant it could be scoped to and no
    /// route value the tenant-binding handler could check; served under the tenant policy it would hand an
    /// administrator of any one portal the complete address book of every other. The legacy screen drew the
    /// same line: <c>Website/admin/Portal/PortalAlias.ascx.vb:L84</c> honours a caller-supplied portal only
    /// under the host navigation tree or for a host account, and <c>EditPortalAlias.ascx.vb:L65-L70</c>
    /// refuses outright with "You do not have access to view this Portal Alias" when the addressed record
    /// belongs to another tenant and the caller is not a host account. The per-tenant view above remains
    /// available to a tenant administrator for its own portal, which is the legacy non-host arm.
    /// </para>
    /// </remarks>
    // HOST-SCOPED, NOT PORTAL-SCOPED. An alias addressed by its own global identifier - or the whole alias
    // collection - carries no portal binding, so the portal-administrator policy fell back to the tenant the
    // caller arrived through and granted an administrator of ONE portal the ability to read, retarget or
    // delete ANY portal's alias by guessing its identifier. Retargeting an alias moves which tenant a host
    // name serves, so that is the strongest cross-tenant reach in this API. The portal-nested siblings below
    // keep the portal policy, which is now anchored to the portal their route names.
    [HttpGet("portal-aliases")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PortalAliasDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PortalAliasDto>>>> ListAllAsync(
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PortalAliasDto>> outcome = await _portals
            .ListPortalAliasesAsync(null, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one alias of one portal.</summary>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The alias, or <c>404 Not Found</c> when it does not exist.</returns>
    // HOST-SCOPED, NOT PORTAL-SCOPED. An alias addressed by its own global identifier - or the whole alias
    // collection - carries no portal binding, so the portal-administrator policy fell back to the tenant the
    // caller arrived through and granted an administrator of ONE portal the ability to read, retarget or
    // delete ANY portal's alias by guessing its identifier. Retargeting an alias moves which tenant a host
    // name serves, so that is the strongest cross-tenant reach in this API. The portal-nested siblings below
    // keep the portal policy, which is now anchored to the portal their route names.
    [HttpGet("portal-aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(typeof(ApiResponse<PortalAliasDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PortalAliasDto?>>> GetAsync(
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        // No tenant is named, and none may be inferred. This route is reached only by a host administrator,
        // whose authority is installation-wide, and binding it to a portal it did not name would have meant
        // binding it to the model binder's default of zero - which is a REAL portal in this schema, because
        // Portals.PortalID is IDENTITY(-1, 1). Every alias outside that one portal would then have read as
        // absent on the one surface that exists to repair a broken binding.
        Result<PortalAliasDto?> outcome = await _portals
            .GetPortalAliasAsync(portalId: null, portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Adds an alias to a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The host name to bind.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created alias, with its address in the location header.</returns>
    [HttpPost("portals/{portalId:int}/aliases")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalAliasDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PortalAliasDto>>> AddAsync(
        int portalId,
        [FromBody] CreatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result<PortalAliasDto> outcome = await _portals
            .AddPortalAliasAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.PortalAliasId);
    }

    /// <summary>Updates an alias of one portal.</summary>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="request">The host name to bind in place of the current one.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been updated.</returns>
    // HOST-SCOPED, NOT PORTAL-SCOPED. An alias addressed by its own global identifier - or the whole alias
    // collection - carries no portal binding, so the portal-administrator policy fell back to the tenant the
    // caller arrived through and granted an administrator of ONE portal the ability to read, retarget or
    // delete ANY portal's alias by guessing its identifier. Retargeting an alias moves which tenant a host
    // name serves, so that is the strongest cross-tenant reach in this API. The portal-nested siblings below
    // keep the portal policy, which is now anchored to the portal their route names.
    [HttpPut("portal-aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UpdateAsync(
        int portalAliasId,
        [FromBody] UpdatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result outcome = await _portals
            .UpdatePortalAliasAsync(portalId: null, portalAliasId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes an alias of one portal.</summary>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been removed.</returns>
    // HOST-SCOPED, NOT PORTAL-SCOPED. An alias addressed by its own global identifier - or the whole alias
    // collection - carries no portal binding, so the portal-administrator policy fell back to the tenant the
    // caller arrived through and granted an administrator of ONE portal the ability to read, retarget or
    // delete ANY portal's alias by guessing its identifier. Retargeting an alias moves which tenant a host
    // name serves, so that is the strongest cross-tenant reach in this API. The portal-nested siblings below
    // keep the portal policy, which is now anchored to the portal their route names.
    [HttpDelete("portal-aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _portals
            .DeletePortalAliasAsync(portalId: null, portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    // -------------------------------------------------------------------------------------------------
    // THE PORTAL-NESTED SIBLINGS. Each names the owning tenant in its route, so the portal-administrator
    // policy has a portal to bind to instead of falling back to whichever tenant the caller arrived
    // through - and the service compares the named tenant against the alias's stored owner before it
    // reports or writes anything. Both addresses exist deliberately and neither is redundant: an
    // administrator of one tenant reaches its own aliases here and nowhere else, while the flat siblings
    // above answer the installation-wide case, whose caller names no tenant because its authority is not
    // scoped to one. A mismatch below reads as NOT FOUND rather than as a refusal, so the route cannot
    // become an oracle for which alias identifiers exist in other tenants.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Reads one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the alias.</param>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>200 OK</c> with the alias, or <c>404 Not Found</c>.</returns>
    [HttpGet("portals/{portalId:int}/aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalAliasDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PortalAliasDto?>>> GetForPortalAsync(
        int portalId,
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result<PortalAliasDto?> outcome = await _portals
            .GetPortalAliasAsync(portalId, portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retargets one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the alias.</param>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="request">The submitted alias.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been updated.</returns>
    [HttpPut("portals/{portalId:int}/aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UpdateForPortalAsync(
        int portalId,
        int portalAliasId,
        [FromBody] UpdatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        Result outcome = await _portals
            .UpdatePortalAliasAsync(portalId, portalAliasId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Unbinds one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the alias.</param>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been removed.</returns>
    [HttpDelete("portals/{portalId:int}/aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteForPortalAsync(
        int portalId,
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _portals
            .DeletePortalAliasAsync(portalId, portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
