using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The portal alias resource: the host names by which a portal is reached.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the two legacy administration screens for this resource -
/// <c>Website/admin/Portal/PortalAlias.ascx.vb</c>, which listed a portal's aliases through
/// <c>GetPortalAliasArrayByPortalID</c> (<c>:L42</c>), and
/// <c>Website/admin/Portal/EditPortalAlias.ascx.vb</c>, which added, renamed and unbound one
/// (<c>:L206-L248</c>, <c>:L173-L193</c>). The nine public members of
/// <c>Library/Components/Portal/PortalAliasController.vb</c> reduce to the five alias members of
/// <see cref="IPortalService"/>: the two readers that differed only in the collection type they built
/// collapse into one, and the pre-generics collection wrappers they returned have no successor type at
/// all - a sequence is an <c>IReadOnlyList</c> here, and nothing in this file names a legacy collection
/// base class.
/// </para>
/// <para>
/// <b>This controller contains no business logic, and every action is the same three steps.</b> Bind the
/// request, delegate to <see cref="IPortalService"/>, and hand the outcome to the shared translator in
/// <see cref="ApiResults"/>. Alias normalisation, duplicate detection, ownership comparison and tenant
/// resolution all happen behind that contract. MIGRATION: the legacy screen did the opposite - it
/// normalised the submitted value in the page (<c>EditPortalAlias.ascx.vb:L210-L215</c> stripped
/// everything through a scheme separator and then everything through a network-share prefix), then
/// queried for a duplicate in the page (<c>:L231</c>), then wrote in the page (<c>:L236</c>). All three
/// now live in <c>Application/Services/PortalService.cs</c>, so the rules are the same for every caller
/// of the service rather than for every screen that remembers to apply them.
/// </para>
/// <para>
/// <b>Two addressing families, and both are deliberate.</b> Five actions address one tenant's aliases
/// beneath the portal that owns them, at <c>portals/{portalId}/aliases</c>. Four address an alias - or the
/// whole alias table - without naming a tenant, at <c>portal-aliases</c>. The second family cannot be
/// nested: an installation-wide listing exists precisely to show every alias across every portal, and the
/// repair path for a wrong or missing alias must not require an alias to resolve before it can be reached.
/// </para>
/// <para>
/// <b>The individual alias is reachable beneath its owning portal, and that nesting is a security
/// property rather than a naming preference.</b> An earlier revision addressed it only at a top-level
/// path, reasoning that the alias key is a surrogate unique across the installation and so needs no
/// portal beside it to find the row. That reasoning was exactly backwards. Uniqueness is what makes the
/// key guessable across tenants, and a route with no tenant segment gives a tenant-bound policy nothing
/// to bind, so a caller authorised over one portal could read, rename or unbind any alias in the
/// installation by counting upwards. Because tenant resolution matches on the alias, renaming one
/// re-points another tenant's traffic and unbinding one makes that tenant unreachable at the host name
/// its users hold. The nested routes give the policy a <c>portalId</c> to bind, and the service compares
/// that portal against the stored row's owner, so the segment is validated rather than decorative. Both
/// layers check, because neither is sufficient alone: the policy cannot see which portal owns the row,
/// and the service cannot see which credential asked.
/// </para>
/// <para>
/// <b>Authority is declared per action, not on the class.</b> The class attribute carries authentication
/// only. Five actions name <see cref="PolicyNames.PortalAdministrator"/>, whose handler binds the
/// tenant named in the route to the tenant the caller administers; four name
/// <see cref="PolicyNames.HostAdministrator"/>, because they span every tenant by definition and so carry
/// no tenant to bind. The split is unavoidable rather than stylistic: authorisation attributes COMBINE
/// rather than override, so a class-level tenant policy would be ANDed onto the installation-wide actions
/// and would make them unreachable by the only credential entitled to them, since a host account holds no
/// administrator role assignment in any tenant. The cost of the split is that an action added later would
/// inherit mere authentication if its author forgot the attribute, and an attribute cannot express "every
/// action must name a policy, but not the same one" - so that guard is a test over this type's metadata
/// rather than a declaration here.
/// </para>
/// <para>
/// MIGRATION: authority is therefore WIDER than the legacy screens in one direction and NARROWER in
/// another, and both halves are stated so neither is absorbed. The legacy delete and edit affordances were
/// super-user gated (<c>EditPortalAlias.ascx.vb:L181-L186</c> refused with the <c>AccessDenied</c>
/// message, <c>:L65-L70</c> with "You do not have access to view this Portal Alias", and
/// <c>PortalAlias.ascx.vb:L84</c> honoured a caller-supplied portal only under the host navigation tree
/// or for a super-user); the tenant-scoped routes here admit a portal administrator, which is a widening,
/// because the policy catalogue is closed and holds no per-screen super-user gate. The legacy non-host arm
/// nevertheless enforced ownership against the LOADED RECORD, comparing
/// <c>objPortalAliasInfo.PortalID</c> against the ambient portal, and that check survives intact inside
/// the service. The installation-wide routes are narrower than the widening implies: they are refused to a
/// portal administrator outright and require host authority, which is the legacy super-user arm preserved
/// exactly. Refusal is <c>403</c> rather than the legacy redirect to an access-denied page.
/// </para>
/// <para>
/// MIGRATION: alias lookup and duplicate detection are EXACT-MATCH throughout. The legacy tenant-resolution
/// procedure matched a host name with
/// <c>where PortalAlias like '%' + @PortalAlias + '%'</c>
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600</c>) and then
/// took <c>min(PortalID)</c> of whatever matched, so an alias that was a substring of another portal's
/// alias resolved to the wrong tenant - a latent multi-tenant mis-resolution. The exact-match replacement
/// lives in <see cref="PortalAliasResolutionMiddleware"/> and in the service; the consequence for this
/// file is a prohibition rather than a behaviour, namely that nothing here reintroduces a substring or
/// wildcard notion of matching an alias. No action normalises, folds case or compares host names at all.
/// </para>
/// <para>
/// MIGRATION: <b>every integer is a meaningful portal identifier, including <c>0</c> and negative
/// values.</b> <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c>
/// (<c>01.00.00.SqlDataProvider:L77</c>), so the first portal ever created bears <c>-1</c> and the second
/// bears <c>0</c> - and <c>-1</c> is simultaneously the legacy <c>Null.NullInteger</c> sentinel
/// (<c>Library/Components/Shared/Null.vb:L41-L45</c>) and the <c>glbRoleAllUsers</c> role token
/// (<c>Globals.vb:L95</c>). No route here imposes a lower bound, tests an identifier against <c>0</c> or
/// <c>-1</c>, or substitutes a default for a value it failed to bind: the segment is constrained to
/// <c>:int</c> and forwarded exactly as bound. An installation-wide read is expressed by NOT naming a
/// portal - the service's parameter is nullable for that purpose - rather than by the legacy wildcard,
/// which passed <c>-1</c> as "match every row" and so could not be told apart from a genuine request for
/// the first portal.
/// </para>
/// <para>
/// MIGRATION: the string sentinel is honoured the same way. <c>Null.NullString</c> is the EMPTY STRING and
/// not <see langword="null"/> (<c>Null.vb:L71-L75</c>), and <c>PortalAlias.HTTPAlias</c> is a nullable
/// column, so a stored host name may legitimately read as <c>""</c>. No action here rewrites one form as
/// the other in either direction: the projection reports what it found, and the serialiser is configured
/// once for the whole API to write null members rather than omit them, so an empty alias arrives at a
/// client as <c>""</c> and an absent one as <c>null</c>. Nothing in this file declares
/// <c>JsonIgnore</c> or per-action serialiser options, which is what keeps that single configuration
/// authoritative.
/// </para>
/// <para>
/// MIGRATION: the write contracts are dedicated request types and the read projection is never accepted as
/// input. Accepting the projection let a caller supply an alias key that a create ignores and a portal key
/// that an update refuses to re-bind, so a request could name a tenant it was silently not given. Shape,
/// length and canonical form are settled declaratively by the validators registered for those contracts,
/// which the globally installed validation filter runs before any action body; uniqueness remains a
/// question about stored state and is answered by the service as <c>portal.alias_duplicate</c>.
/// </para>
/// <para>
/// <b>A create answers <c>201</c> with the created representation; an update and an unbind answer
/// <c>204</c> with no body.</b> That asymmetry follows the service contract rather than a preference:
/// the create member reports the stored alias, because only the database can supply the identifier it
/// assigned, whereas the update and delete members report a payload-free outcome, and <c>204</c> is what
/// the shared translator answers for one. Answering <c>200</c> instead would mean either an extra read
/// this API does not need or an empty envelope, and HTTP forbids a body on <c>204</c> in any case, so the
/// two cannot be reconciled by wrapping. A caller that has just submitted the only value this resource
/// holds already knows what it now says; one that wants confirmation from the store re-reads it.
/// </para>
/// <para>
/// MIGRATION: the legacy screen hid its Delete button when a portal had a single alias
/// (<c>EditPortalAlias.ascx.vb:L102-L110</c>) but never enforced the rule on the write path - the button
/// was merely invisible, and <c>DeletePortalAlias</c> removed the row whether or not it was the last one.
/// No refusal is invented here: <see cref="IPortalService.DeletePortalAliasAsync"/> declares no
/// last-alias reason code, so no action below declares a conflict it cannot produce. Recorded as a
/// decision rather than an omission; a caller that unbinds a portal's only alias is answered
/// <c>204</c>, exactly as the legacy write path answered.
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

    /// <summary>The application-layer contract this controller delegates every decision to.</summary>
    private readonly IPortalService _portals;

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    // validated by FluentValidationActionFilter, which is registered once for the whole API, runs before
    // the action and resolves a validator from each argument's declared type. This controller used to
    // take validators of its own and invoke them by hand as well, which was a second invocation path for
    // one rule set and the reason the paging contract was judged against the wrong sortable vocabulary.
    // Adding a validator argument back here would recreate that split.
    //
    // NOTHING ELSE IS INJECTED EITHER. No repository, no unit of work, no database context, no cache, no
    // clock, and no ambient request-context accessor. Which tenant a request concerns is decided in two
    // places that own that concern - the alias-resolution middleware, which resolves the host name, and the
    // authorisation handlers, which bind it to the route - and a controller reaching for the ambient context
    // would be a third. What a caller is entitled to therefore arrives here as a satisfied authorisation
    // policy and as the claims on User; what a caller asked for arrives as route values and a request body.
    // Those are the only inputs an action has.
    /// <summary>Initialises a new instance of the <see cref="PortalAliasesController"/> class.</summary>
    /// <param name="portals">The application-layer contract for the portal aggregate and its aliases.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portals"/> is <see langword="null"/>.</exception>
    public PortalAliasesController(IPortalService portals)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
    }

    /// <summary>Lists the aliases bound to one portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal whose aliases are wanted. Every integer is meaningful, including <c>0</c> and
    /// negative values, so no lower bound is imposed and the value is forwarded exactly as bound.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal's aliases, in the shared success envelope.</returns>
    /// <response code="200">
    /// The portal's aliases. A portal with no aliases yields an empty collection rather than an absent one,
    /// so a caller never has to distinguish "none" from "not answered".
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <remarks>
    /// MIGRATION: replaces the grid binding at <c>Website/admin/Portal/PortalAlias.ascx.vb:L42</c>, which
    /// called <c>GetPortalAliasArrayByPortalID</c> and bound the untyped <c>ArrayList</c> it returned
    /// straight to a <c>DataGrid</c>. The legacy screen was UNPAGED and this action is unpaged with it:
    /// a portal holds a handful of host names, the service exposes no paged alias reader, and inventing
    /// paging here would change a result set the legacy screen returned whole.
    /// </remarks>
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

    /// <summary>Lists every alias in the installation, across all portals.</summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>Every alias, in the shared success envelope.</returns>
    /// <response code="200">Every alias bound anywhere in the installation.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not hold installation-wide authority. A portal administrator is
    /// refused here and reads its own portal's aliases through the nested collection instead.
    /// </response>
    /// <remarks>
    /// <para>
    /// The host-wide view exists because alias collisions are a host-level problem: an operator diagnosing
    /// why one portal is answering for another needs to see every alias at once, which no per-portal view
    /// can show.
    /// </para>
    /// <para>
    /// <b>And that is exactly why it requires HOST authority rather than tenant authority.</b> The
    /// projection spans every tenant by definition, so there is no tenant it could be scoped to and no
    /// route value the tenant-binding handler could check; served under the tenant policy it would hand an
    /// administrator of any one portal the complete address book of every other. The legacy screens drew
    /// the same line: <c>PortalAlias.ascx.vb:L84</c> honoured a caller-supplied portal only under the host
    /// navigation tree or for a super-user, and <c>EditPortalAlias.ascx.vb:L65-L70</c> refused outright
    /// when the addressed record belonged to another tenant and the caller was not a host account.
    /// </para>
    /// <para>
    /// MIGRATION: replaces <c>GetPortalAliases</c> (<c>PortalAliasController.vb:L86</c>), which asked for
    /// every alias by calling the by-portal reader with <c>-1</c> - a value the underlying procedure
    /// treated as "match every row", and which is also the identifier of the first portal an installation
    /// creates. The unfiltered case is expressed here by naming no portal at all, so it cannot be confused
    /// with a genuine request for that portal.
    /// </para>
    /// </remarks>
    [HttpGet("portal-aliases")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PortalAliasDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PortalAliasDto>>>> ListAllAsync(
        CancellationToken cancellationToken)
    {
        // The unfiltered case is a null portal rather than a sentinel. See the class remarks: -1 is a real
        // portal identifier here, so the legacy wildcard value cannot serve as "every portal".
        Result<IReadOnlyList<PortalAliasDto>> outcome = await _portals
            .ListPortalAliasesAsync(null, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Reads one alias by its own identifier, without naming a portal.</summary>
    /// <param name="portalAliasId">
    /// Identifier of the alias to read. Constrained to an integer and nothing further: no bound is imposed
    /// and no default is substituted, for the reason given in the class remarks.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The alias, in the shared success envelope.</returns>
    /// <response code="200">The alias, together with the portal it is bound to.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not hold installation-wide authority. A portal administrator
    /// reads its own portal's aliases through the nested route instead.
    /// </response>
    /// <response code="404">No alias bears that identifier.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>GetPortalAliasByPortalAliasID</c> (<c>PortalAliasController.vb:L63</c>), read by
    /// the legacy edit screen at <c>EditPortalAlias.ascx.vb:L60</c> from the <c>paid</c> query-string value.
    /// Absence was expressed there by returning nothing, which the screen then dereferenced at <c>:L63</c>
    /// before testing it; here absence is a successful outcome carrying no value, which the shared
    /// translator renders as a well-formed <c>404</c> problem document.
    /// </para>
    /// <para>
    /// This is the installation-wide arm of the alias-repair surface, so it names no portal - see
    /// <see cref="AliasRepairJustification"/> for why it must not require one - and it is reached only by a
    /// host account. The nested twin, <see cref="GetForPortalAsync"/>, is the route a portal administrator
    /// uses, and it names the owning portal so the tenant-binding policy has something to compare.
    /// </para>
    /// </remarks>
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

    /// <summary>Binds a new alias to a portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal to bind the host name to. It is the only place a tenant is named on this
    /// call, so a caller cannot bind a host name to a portal other than the one addressed. Every integer is
    /// meaningful, including <c>0</c> and negative values.
    /// </param>
    /// <param name="request">The host name to bind, and nothing else.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>The created alias, with the address of the new resource in the location header.</returns>
    /// <response code="201">
    /// The alias was bound. The body is the created alias, including the identifier the database assigned,
    /// and the location header addresses the nested by-identifier read.
    /// </response>
    /// <response code="400">
    /// The body was absent or malformed, or a declared rule refused the host name - it was empty, overlong
    /// for the <c>nvarchar (200)</c> column, or carried a protocol prefix - in which case the body is an RFC
    /// 7807 validation document naming the offending field.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <response code="409">
    /// The host name is already bound. The body carries <c>portal.alias_duplicate</c> as its problem type.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces the add arm at <c>EditPortalAlias.ascx.vb:L229-L241</c>, which looked the alias up
    /// and inserted only when the lookup found nothing, reporting the <c>DuplicateAlias</c> message
    /// otherwise. Two legacy entry points collapse behind the one service member: that screen's path, and
    /// <c>PortalController.AddPortalAlias</c>, which silently did nothing at all when the alias already
    /// existed. The silent skip is not carried forward - a caller that asks to bind a host name and is
    /// answered success is entitled to conclude that its own request took effect - so a duplicate is
    /// reported as <c>409</c> rather than absorbed as a success.
    /// </para>
    /// <para>
    /// MIGRATION: LEGACY DEFECT, reproduced in intent and corrected in scope, annotated rather than silently
    /// fixed. The legacy duplicate check was
    /// <c>GetPortalAlias(strAlias, Convert.ToInt32(ViewState("PortalAliasID")))</c> at <c>:L231</c>, inside
    /// the branch entered precisely BECAUSE that view-state value is <c>Nothing</c> (<c>:L218</c>). The web
    /// pages compiled with Option Strict off (<c>Website/release.config:L125</c>,
    /// <c>development.config:L123</c>), so the coercion produced <c>0</c> - a legitimate tenant, being the
    /// second portal an installation creates - and the check therefore asked whether the host name was
    /// already bound to portal <c>0</c> rather than whether it was bound anywhere. Against the shipped
    /// schema that check could pass while the insert could not, because <c>HTTPAlias</c> is unique across the
    /// whole table: <c>03.00.07.SqlDataProvider:L13-L18</c> adds
    /// <c>IX_{objectQualifier}PortalAlias UNIQUE NONCLUSTERED (HTTPAlias)</c> - note that the constraint name
    /// itself is templated, so a search for the literal index name finds nothing. The service answers the
    /// question the constraint actually asks, namely whether this host name is bound to ANY alias, which is
    /// why a duplicate now surfaces as a reported conflict instead of as a caught exception.
    /// </para>
    /// </remarks>
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

        // The location is the request path plus the assigned identifier, which is the nested by-identifier
        // read declared below. It is derived from the path rather than from a named route so the address is
        // spelled once: a named-route lookup that drifted from the route template would fail only at run
        // time and only in the header.
        return this.Created(outcome, created => created.PortalAliasId);
    }

    /// <summary>Changes the host name of one alias, addressed by its own identifier.</summary>
    /// <param name="portalAliasId">
    /// Identifier of the alias to change. It is the only place an alias is named on this call, so a caller
    /// cannot redirect the write onto a different alias by editing the body.
    /// </param>
    /// <param name="request">The host name to store in place of the current one.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been changed.</returns>
    /// <response code="204">
    /// The alias was changed. There is no body: the request carried the only value this resource holds
    /// besides its identifiers, so a caller already knows what it now says.
    /// </response>
    /// <response code="400">
    /// The body was absent or malformed, or a declared rule refused the host name, in which case the body is
    /// an RFC 7807 validation document naming the offending field.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not hold installation-wide authority.
    /// </response>
    /// <response code="404">No alias bears that identifier.</response>
    /// <response code="409">
    /// The new host name is already bound to another alias. The body carries
    /// <c>portal.alias_duplicate</c> as its problem type.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>UpdatePortalAliasInfo</c> (<c>PortalAliasController.vb:L94</c>) as driven by
    /// the update arm at <c>EditPortalAlias.ascx.vb:L218-L228</c>. The alias cannot be moved between
    /// portals through this route, matching the legacy screen, which re-supplied the owning portal from page
    /// state at <c>:L221</c> rather than from the operator and offered only the host name for editing.
    /// </para>
    /// <para>
    /// MIGRATION: LEGACY DEFECT, annotated rather than inherited. The legacy update wrapped the write in a
    /// BARE <c>Catch</c> and reported the <c>DuplicateAlias</c> message for EVERY exception it caught
    /// (<c>:L223-L228</c>) - a lost connection, a timeout and a genuine unique-constraint violation were
    /// indistinguishable to the operator, and the screen then returned as though the request had merely
    /// been refused. Here the two are separated by construction: a duplicate is a reported outcome that
    /// becomes <c>409</c>, and anything unexpected is an exception that reaches
    /// <see cref="GlobalExceptionHandler"/> and becomes a <c>500</c> RFC 7807 document carrying a trace
    /// identifier. No action in this file contains a <c>try</c> or a <c>catch</c>, which is what makes that
    /// separation total rather than conventional.
    /// </para>
    /// </remarks>
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
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE, for the reason recorded on the create above.
        Result outcome = await _portals
            .UpdatePortalAliasAsync(portalId: null, portalAliasId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Unbinds one alias, addressed by its own identifier.</summary>
    /// <param name="portalAliasId">Identifier of the alias to unbind.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been unbound.</returns>
    /// <response code="204">The alias was unbound.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not hold installation-wide authority.
    /// </response>
    /// <response code="404">No alias bears that identifier.</response>
    /// <remarks>
    /// MIGRATION: replaces <c>DeletePortalAlias</c> (<c>PortalAliasController.vb:L34</c>) as driven by
    /// <c>EditPortalAlias.ascx.vb:L173-L193</c>, which removed the row whether or not it existed and
    /// reported nothing either way. Reporting <c>portal.alias_not_found</c> lets a caller distinguish a
    /// removal it caused from one that had already happened. The legacy screen's own super-user check at
    /// <c>:L181-L186</c> is preserved here as the host-authority policy rather than widened.
    /// </remarks>
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
    // reports or writes anything. Both addressing families exist deliberately and neither is redundant:
    // an administrator of one tenant reaches its own aliases here and nowhere else, while the host-scoped
    // routes above answer the installation-wide case, whose caller names no tenant because its authority
    // is not scoped to one. A tenant mismatch below reads as NOT FOUND rather than as a refusal, so the
    // route cannot become an oracle for which alias identifiers exist in other tenants.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Reads one alias of one portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the alias. Compared against the stored row's owner by the service,
    /// so the segment is validated rather than decorative. Every integer is meaningful, including <c>0</c>
    /// and negative values.
    /// </param>
    /// <param name="portalAliasId">Identifier of the alias to read.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The alias, in the shared success envelope.</returns>
    /// <response code="200">The alias.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">
    /// No alias bears that identifier within that portal. An alias that exists but belongs to another
    /// portal is reported identically, with the same code and the same wording, so the answer carries no
    /// information about the other tenant.
    /// </response>
    /// <remarks>
    /// MIGRATION: this is the tenant-scoped half of the legacy edit screen's load path. Where
    /// <c>EditPortalAlias.ascx.vb:L65-L70</c> read the alias first and then compared
    /// <c>objPortalAliasInfo.PortalID</c> against the ambient portal to decide whether the operator was
    /// allowed to see it, the comparison here happens twice and earlier: the policy binds the portal named
    /// in the route to the portal the caller administers, and the service compares that same portal against
    /// the stored row. The legacy refusal was a red message rendered into the page it was refusing;
    /// this one is a status code.
    /// </remarks>
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

    /// <summary>Changes the host name of one alias of one portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the alias. Compared against the stored row's owner before the
    /// write, which matters most on this action: an alias is what tenant resolution matches on, so renaming
    /// another tenant's alias would re-point its traffic.
    /// </param>
    /// <param name="portalAliasId">Identifier of the alias to change.</param>
    /// <param name="request">The host name to store in place of the current one.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been changed.</returns>
    /// <response code="204">The alias was changed.</response>
    /// <response code="400">
    /// The body was absent or malformed, or a declared rule refused the host name, in which case the body is
    /// an RFC 7807 validation document naming the offending field.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No alias bears that identifier within that portal.</response>
    /// <response code="409">
    /// The new host name is already bound to another alias. The body carries
    /// <c>portal.alias_duplicate</c> as its problem type.
    /// </response>
    /// <remarks>
    /// MIGRATION: the tenant-scoped twin of <see cref="UpdateAsync"/>, and the same legacy defect is
    /// recorded there rather than repeated here: the legacy update reported every caught exception as a
    /// duplicate alias. The owning portal is not re-bound by this action - the request contract carries the
    /// host name alone, so there is no portal key on the wire for it to be re-bound to.
    /// </remarks>
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
    /// <param name="portalId">
    /// Identifier of the portal that owns the alias. Compared against the stored row's owner before the
    /// removal: unbinding another tenant's alias would make that tenant unreachable at the host name its
    /// users hold, which is a denial of service reached from a grant over an unrelated portal.
    /// </param>
    /// <param name="portalAliasId">Identifier of the alias to unbind.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been unbound.</returns>
    /// <response code="204">
    /// The alias was unbound. This is also the answer when the alias was the portal's last, for the reason
    /// recorded in the class remarks: the legacy screen hid the button but never refused the write, and no
    /// refusal is invented here.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No alias bears that identifier within that portal.</response>
    /// <remarks>
    /// MIGRATION: the tenant-scoped twin of <see cref="DeleteAsync"/>. The legacy delete affordance was
    /// super-user gated (<c>EditPortalAlias.ascx.vb:L181-L186</c>); a portal administrator reaches it here
    /// for its own portal only, which is the widening recorded in the class remarks, and the ownership check
    /// the legacy screen performed against the loaded record survives inside the service.
    /// </remarks>
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
