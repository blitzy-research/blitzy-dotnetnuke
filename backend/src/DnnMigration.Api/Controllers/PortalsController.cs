using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The portal resource - the multi-tenant site container - exposed at
/// <c>/api/v1/portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this type does, and the complete list.</strong> It binds a request, delegates to
/// <see cref="IPortalService"/>, and translates the returned outcome into a status code. Nothing else.
/// There is no rule here, no default filled in, no value normalised, no permission re-interpreted and
/// no tally consulted. A rule implemented in a controller applies to callers arriving over HTTP and
/// silently fails to apply to every other caller - a background job, a console tool, a unit test - so
/// every rule lives in the service, which is also the thing the unit tests exercise.
/// </para>
/// <para>
/// <strong>Legacy origin.</strong> Three Web Forms screens collapse into this one resource:
/// <c>Website/admin/Portal/Portals.ascx.vb</c> (446 lines - the grid and the delete command),
/// <c>Signup.ascx.vb</c> (399 lines - creation) and <c>SiteSettings.ascx.vb</c> (896 lines - editing and
/// the configuration projection). Each held its rules inside a button-click handler and reached the
/// domain through a directly constructed <c>PortalController</c> (1,632 lines, 21 public members) whose
/// creation entry point took FIFTEEN positional arguments
/// (<c>Signup.ascx.vb:L274</c>) and whose update entry point took TWENTY-SEVEN
/// (<c>SiteSettings.ascx.vb:L772-L780</c>). Both argument lists become a single named request object, so
/// argument order stops being load-bearing.
/// </para>
/// <para>
/// MIGRATION: the legacy screens authorised themselves imperatively, by calling
/// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> in the page lifecycle and
/// answering a refusal with <c>Response.Redirect(NavigateURL("Access Denied"), True)</c> - a 302 to an
/// HTML page, which no programmatic caller can interpret. That becomes the declarative class-level
/// policy below, which answers <c>401</c> when no credential was presented and <c>403</c> when the
/// credential is valid but does not administer the addressed tenant. The two conditions were
/// indistinguishable to a legacy caller. The measured legacy role name is <c>Administrators</c> -
/// plural (<c>PortalController.vb:L1390</c>) - and resolving it is the concern of
/// <c>Extensions/AuthenticationExtensions.cs</c>, not of this file.
/// </para>
/// <para>
/// MIGRATION: identifiers are never compared against a magic number here, and no lower bound is placed
/// on <c>portalId</c>. The legacy absent-integer marker is -1
/// (<c>Library/Components/Shared/Null.vb:L41-L45</c>), but that value is not free: the portal table is
/// declared <c>IDENTITY (-1, 1)</c>
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>), so -1 is the
/// identifier of the first portal an installation creates and 0 is the shipped default one; -1 is
/// simultaneously the unauthenticated-role token <c>glbRoleAllUsers</c>
/// (<c>Library/Components/Shared/Globals.vb:L95</c>). The role, page and module tables all seed at 0,
/// so 0 is a genuine identifier three more times over. Both values are therefore valid route values and
/// neither may be read as meaning "absent".
/// </para>
/// <para>
/// MIGRATION: no sentinel is manufactured or erased on this boundary. Serialisation is configured once
/// for the whole application to emit every property including nulls, and the transfer objects carry the
/// stored values through unchanged, so a legacy consumer still sees the retention period as -1 and an
/// unset expiry as the minimum date rather than as an omitted field. The individual sentinel decisions
/// belong to the transfer objects that declare the properties; this file adds none and removes none,
/// which is the only way the two can stay consistent.
/// </para>
/// <para>
/// MIGRATION: there is no configuration-writing action, and no key-value configuration action of any
/// kind. The shipped schema defines no portal-settings table - it defines <c>ModuleSettings</c>,
/// <c>HostSettings</c>, <c>TabModuleSettings</c> and <c>ScheduleItemSettings</c>, and none for a portal
/// - and the legacy type of that name was a per-request composite assembled in memory. What the legacy
/// application called site settings were physically module-setting rows hanging off a module instance
/// located by a magic-string definition lookup (<c>PortalSettings.vb:L923-L976</c>), and its seven keys
/// - the inline-editor flag, the three control-panel keys and the three host-only transport-security
/// keys (<c>PortalSettings.vb:L811-L823</c>) - are deliberately not carried forward. The projection
/// this file serves reads stored columns on the portal record, and every settable column is written
/// through the single update action.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class PortalsController : ControllerBase
{
    /// <summary>
    /// The failure code the application layer reports when a removal was refused to keep one portal in
    /// the installation.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is the sole non-empty outcome the legacy delete could produce.
    /// <c>PortalController.DeletePortal</c> (<c>PortalController.vb:L162-L204</c>) reported its result as
    /// a <c>String</c> - empty meaning success - and read a portal tally before doing anything; when the
    /// tally was not greater than one it returned the shared message keyed <c>LastPortal</c>, whose
    /// wording is "You Can Not Delete The Last Portal In Your Database". Every other path returned the
    /// empty string. A caller therefore had to compare display text to learn whether the delete had
    /// happened, and the rule is preserved here as a code rather than as prose.
    /// </remarks>
    private const string LastRemainingFailureCode = "portal.last_remaining";

    /// <summary>
    /// The problem type published for <see cref="LastRemainingFailureCode"/>.
    /// </summary>
    /// <remarks>
    /// Spelled to match exactly what the shared translator in
    /// <c>ErrorHandling/GlobalExceptionHandler.cs</c> publishes for every other failure code, so a client
    /// branching on the problem type sees one consistent scheme across the whole API. It is a relative,
    /// non-dereferenceable identifier for the reason recorded on that translator: inventing an absolute
    /// address for a documentation page that does not exist would be worse than having none.
    /// </remarks>
    private const string LastRemainingProblemType = "urn:dnnmigration:error:portal.last_remaining";

    /// <summary>The portal service this controller delegates to.</summary>
    private readonly IPortalService _portalService;

    /// <summary>Initialises a new instance of the <see cref="PortalsController"/> class.</summary>
    /// <param name="portalService">The application-layer contract for the portal aggregate.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="portalService"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One dependency, and it is an application-layer contract. Persistence is unreachable from this
    /// layer by construction - the project reference graph makes reaching it a compile error rather than
    /// a review finding - and nothing else is needed: the tenant is named explicitly by every route, so
    /// no ambient per-request tenant context is consulted here, and the clock, the cache and the
    /// host-settings reader all belong behind the service.
    /// </para>
    /// <para>
    /// Declarative validation is applied by <c>Filters/FluentValidationActionFilter.cs</c>, which is
    /// registered globally and runs before any action body, so no validator is injected here and no
    /// action inspects model state. That placement is deliberate: a per-action opt-in has a silent
    /// failure mode, because an endpoint whose author forgot the call looks exactly like one with no
    /// rules declared against it.
    /// </para>
    /// </remarks>
    public PortalsController(IPortalService portalService)
    {
        _portalService = portalService ?? throw new ArgumentNullException(nameof(portalService));
    }

    /// <summary>Lists the portals installed on this host as one page of a larger set.</summary>
    /// <param name="request">
    /// The paging, sorting and filtering arguments, bound from the query string. The page index is
    /// zero-based and the page size defaults to ten; bounds are enforced by the registered request
    /// validator, never here.
    /// </param>
    /// <param name="name">
    /// An optional fragment of a portal's name. Passed through exactly as bound.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>One page of portals, together with the total across every page.</returns>
    /// <response code="200">
    /// The page. An empty page is a legitimate answer and means nothing matched the supplied filter; it
    /// is never reported as a failure.
    /// </response>
    /// <response code="400">
    /// A query value could not be bound to its parameter type, or a declared rule refused it - a
    /// negative page index, a page size beyond the permitted maximum, an unrecognised sort field or
    /// sort direction, or a filter longer than the permitted length. The body is an RFC 7807 validation
    /// document naming each offending field.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: consolidates two legacy readers. <c>GetPortals</c>
    /// (<c>PortalController.vb:L1263</c>) returned the non-generic collection type of the era, declaring
    /// no element type and no grand total, and <c>GetPortalsByName</c> (<c>PortalController.vb:L262</c>)
    /// returned that same untyped collection while smuggling the grand total back through a
    /// by-reference argument - one call producing two answers that no type tied together. Both become one
    /// paged envelope carrying the records and the total on a single immutable value. No by-reference and
    /// no output argument appears anywhere on this action.
    /// </para>
    /// <para>
    /// MIGRATION: the page index is zero-based at both ends of this round trip, which is what the legacy
    /// grid did too - <c>Portals.ascx.vb:L142</c> reads
    /// <c>GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)</c>, converting its
    /// one-based display page to a zero-based data page. The request and response contracts agree with
    /// that base, so nothing is converted here; a silent off-by-one would serve the neighbouring page
    /// rather than fail, which is precisely why it is stated rather than assumed.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy filter was a starts-with match, assembled by appending a wildcard to the
    /// caller's text at that same line and interpreted as a pattern by the database, which had added
    /// none of its own. The modern filter is matched as literal text with pattern metacharacters
    /// escaped, so a caller can neither inject matching syntax nor force a scan by leading with a
    /// wildcard. That divergence is owned and documented by <see cref="IPortalService"/>; this action
    /// forwards the value untouched, because appending or trimming a wildcard here would be a second,
    /// quieter copy of a decision that already has one home.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen carried a pager-suppression switch
    /// (<c>Portals.ascx.vb:L108-L114</c>) whose body was a hard-coded <c>Return True</c> standing over a
    /// per-portal setting that had itself been commented away. It configured nothing, so it is not
    /// reproduced.
    /// Expired-portal listing and bulk expiry removal (<c>Portals.ascx.vb:L189</c>) are likewise absent:
    /// they existed to service a background sweep whose scheduling subsystem is beyond the migrated
    /// scope, and no endpoint in the plan invokes them.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<PortalListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<PortalListItemDto>>> ListAsync(
        [FromQuery] PagedRequest request,
        [FromQuery] string? name,
        CancellationToken cancellationToken)
    {
        Result<PagedResult<PortalListItemDto>> outcome = await _portalService
            .ListPortalsAsync(request, name, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never
        // has its value touched - reading the value of a failed outcome throws by design.
        return this.Complete(outcome);
    }

    /// <summary>Reads one portal in full.</summary>
    /// <param name="portalId">
    /// The portal identifier. Every integer is meaningful, including 0 and negative values, so no lower
    /// bound is imposed and the value is forwarded exactly as bound.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal.</returns>
    /// <response code="200">The portal.</response>
    /// <response code="400">The route value could not be bound to an integer.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <remarks>
    /// MIGRATION: replaces <c>GetPortal</c> (<c>PortalController.vb:L1224</c>), which returned nothing
    /// at all for a missing row and so gave a caller no way to distinguish a record that does not exist
    /// from a lookup that could not be performed. The application contract now reports absence as a
    /// successful outcome carrying no value, which the shared translator renders as <c>404</c>, and
    /// reserves a failed outcome for a lookup that genuinely could not be attempted.
    /// </remarks>
    [HttpGet("{portalId:int}")]
    [ProducesResponseType(typeof(PortalDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalDetailDto?>> GetAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto?> outcome = await _portalService
            .GetPortalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a portal together with the records a working tenant requires.</summary>
    /// <param name="request">The portal to create.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>The created portal, with the address of the new resource in the location header.</returns>
    /// <response code="201">
    /// The portal was created. The body is the created portal, including the identifier the database
    /// assigned, and the location header addresses the by-identifier read.
    /// </response>
    /// <response code="400">
    /// The body was absent or malformed, or a declared rule refused it - the alias character whitelist,
    /// the home-folder shape, the password rules or a required field. The body is an RFC 7807 validation
    /// document naming each offending field. Creation that fails after validation for a reason the
    /// caller cannot correct by editing one field also arrives here, carrying the failure code as its
    /// problem type.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="409">
    /// The submitted host name is already bound to a portal, or the requested administrator account name
    /// is already taken. Both are collisions with existing state that the caller can correct by choosing
    /// another value.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces the fifteen-argument <c>CreatePortal</c> call at
    /// <c>Signup.ascx.vb:L274</c>, whose arguments were, in order, the title, first name, last name,
    /// user name, password, e-mail address, description, keywords, host map path, template selection,
    /// home directory, alias, server path, child path and child flag. Three of those - the two host
    /// paths and the server path - describe locations on a file system and have no counterpart, because
    /// file management is beyond the migrated scope. The remainder become named properties on one
    /// request object.
    /// </para>
    /// <para>
    /// MIGRATION - discovered legacy defect, recorded rather than repaired. The legacy screen decided
    /// whether creation had succeeded by testing the returned identifier against -1
    /// (<c>Signup.ascx.vb:L280</c>), a value its own null contract also used to mean "absent" and which
    /// the portal table simultaneously issues as the first real identifier. A portal legitimately
    /// created with identifier -1 was therefore reported as a failure, and a genuine failure was
    /// indistinguishable from that portal. Success here is the outcome's own success flag and never a
    /// comparison against any number, which is why no identifier is compared to -1 or to 0 anywhere in
    /// this file.
    /// </para>
    /// <para>
    /// MIGRATION: the whole creation, which writes across the portal, alias, role, page and module
    /// tables, commits exactly once, so a failure part-way through cannot leave a half-built tenant
    /// behind. The legacy sequence was not transactional across its statements, which is exactly why its
    /// body accumulated a message string as it progressed. Atomicity is the service's responsibility;
    /// this action neither opens nor observes a transaction.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen normalised the submitted alias before validating it - lower-casing
    /// it and stripping a leading scheme (<c>Signup.ascx.vb:L183-L184</c>) - and then accumulated one
    /// copy of the same complaint for every character outside its whitelist
    /// (<c>Signup.ascx.vb:L186-L217</c>). Both behaviours are preserved, and both live behind this
    /// action: the normalisation in the service, the character rules in the registered request
    /// validator. Neither is duplicated here, because a second copy would eventually disagree with the
    /// first about what a valid alias is.
    /// </para>
    /// <para>
    /// MIGRATION: the location header is derived from the request path plus the assigned identifier
    /// rather than from a named route lookup, so the address is spelled in exactly one place. That is the
    /// shared creation translator's documented behaviour and it yields
    /// <c>/api/v1/portals/{identifier}</c> for this endpoint.
    /// </para>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(PortalDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PortalDetailDto>> CreateAsync(
        [FromBody] CreatePortalRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto> outcome = await _portalService
            .CreatePortalAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // The identifier is read from the created representation rather than recomputed, and it is read
        // only on the success path: the translator checks the outcome first.
        return this.Created(outcome, created => created.PortalId);
    }

    /// <summary>Modifies an existing portal.</summary>
    /// <param name="portalId">
    /// The portal identifier, bound from the route. This is the only place the subject is named - the
    /// body carries no identifier of its own - so a caller cannot retarget the write at another tenant
    /// and there is no disagreement for any layer to detect.
    /// </param>
    /// <param name="request">The values to store.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>The stored portal.</returns>
    /// <response code="200">The portal was stored. The body is its current state.</response>
    /// <response code="400">
    /// The body was absent or malformed, or a declared rule refused it. The body is an RFC 7807
    /// validation document naming each offending field.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// Either the caller does not administer the addressed portal, or the caller is a portal
    /// administrator rather than a host account and the submitted values would alter a host-only field.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces the twenty-seven-argument <c>UpdatePortalInfo</c> call at
    /// <c>SiteSettings.ascx.vb:L772-L780</c> and the record-taking overload at
    /// <c>PortalController.vb:L1524</c> that merely unpacked into it. This is the single write path for
    /// every settable column, which is why no separate configuration-writing action exists: two write
    /// paths over one set of columns could diverge. The two selected-index arguments in that list become
    /// the registration-mode and banner-mode enumerations, so a stored integer stops being a magic
    /// number.
    /// </para>
    /// <para>
    /// MIGRATION - the host-only field rule, and why it answers <c>403</c> rather than <c>500</c>. When
    /// the caller was not a super user, the legacy screen compared the submitted hosting charge, disc
    /// quota, page quota, member quota, retention period and expiry date against the stored portal and,
    /// if any differed, executed a bare <c>Throw New System.Exception</c> carrying no message at all
    /// (<c>SiteSettings.ascx.vb:L757-L768</c>). A caller received an unstyled error page and no
    /// indication of which field caused it. The rule is an authorisation test over the request's
    /// contents rather than over the route, so it cannot be expressed as a policy on this endpoint and
    /// it is not attempted here; it lives in the service and reaches the caller as a single, deliberate
    /// <c>403</c>. It is never an unhandled fault.
    /// </para>
    /// <para>
    /// MIGRATION: the sentinel defaults the legacy screen applied when a field was left blank are
    /// preserved by the request contract, not re-derived here. Measured at
    /// <c>SiteSettings.ascx.vb:L705-L751</c>, they are a raw -1 for the retention period, the minimum
    /// date for an unset expiry, the absent-integer marker for each of the four navigation page
    /// references, and zero for the hosting charge and the three quotas - where zero means unlimited
    /// rather than "none permitted". Reproducing any of them in this action would put a second,
    /// divergent copy of the wire contract in the layer least able to test it.
    /// </para>
    /// <para>
    /// MIGRATION - discovered legacy defect, recorded rather than repaired. The same screen decided
    /// whether to refresh the rendered page with
    /// <c>refreshPage = (strBackground = objPortal.BackgroundFile Or strLogo = objPortal.LogoFile)</c>
    /// under a comment reading "Refresh if Background or Logo file have changed"
    /// (<c>SiteSettings.ascx.vb:L701</c>). The condition tests equality, so it was true precisely when
    /// the files had NOT changed - the inverse of the stated intent. The behaviour is not corrected,
    /// because correcting it would be an unrequested change to ported logic; it simply has no successor,
    /// since deciding when to re-render is a client concern in a single-page application and no field on
    /// the response carries it.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy code-behind compiled with Option Strict disabled
    /// (<c>Website/release.config:L125</c> declares <c>strict="false"</c>, while the class library
    /// compiled with it enabled), so its parsing was permissive in ways this boundary is not. It read
    /// the member quota into a <c>Double</c> and then assigned an <c>Integer.Parse</c> result to it, and
    /// it called <c>Double.Parse</c>, <c>Integer.Parse</c> and <c>Convert.ToDateTime</c> with no culture
    /// and no attempt-style parsing, so malformed input became an exception and a decimal separator was
    /// read according to the server's own culture. Here every value arrives already bound to its
    /// declared type by the framework's culture-invariant JSON reader, and a value that cannot be bound
    /// is a validation failure naming the field rather than a fault. That is a deliberate divergence: it
    /// converts a class of unhandled fault into a reportable <c>400</c>, and it removes a
    /// culture-dependent reading of numbers and dates.
    /// </para>
    /// </remarks>
    [HttpPut("{portalId:int}")]
    [ProducesResponseType(typeof(PortalDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalDetailDto?>> UpdateAsync(
        int portalId,
        [FromBody] UpdatePortalRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto?> outcome = await _portalService
            .UpdatePortalAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a portal and the stored records that depend on it.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>No content once the portal has been removed.</returns>
    /// <response code="204">The portal was removed. There is nothing to return.</response>
    /// <response code="400">The route value could not be bound to an integer.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <response code="409">
    /// The removal was refused because an installation must retain at least one portal. The request
    /// conflicts with current state rather than being malformed, so re-sending it unchanged cannot
    /// succeed while that state holds.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>PortalController.DeletePortal</c>
    /// (<c>PortalController.vb:L162-L204</c>), invoked from the grid's delete command at
    /// <c>Portals.ascx.vb:L388-L402</c>. The legacy pair reported success as an empty <c>String</c> and
    /// failure as localised display text, so a programmatic caller had to compare prose; here the
    /// outcome is a status code and the reason travels as a stable code in the problem type.
    /// </para>
    /// <para>
    /// MIGRATION - functional reduction, deliberate and documented. The legacy body deleted files as
    /// well as records: per-portal resource files, a child portal's folder, an upload folder assembled as
    /// <c>serverPath</c> plus a literal segment containing a Windows path separator, and the tenant's
    /// home folder. File management is beyond the migrated scope, and that hard-coded separator cannot
    /// resolve on the Linux images this application ships as. Only the record removal the legacy body
    /// performed last - <c>DeletePortalInfo</c> (<c>PortalController.vb:L1191-L1202</c>) - survives, so
    /// any residue left on a volume is an operator's concern. The endpoint accepts no path of any kind.
    /// </para>
    /// <para>
    /// MIGRATION - discovered legacy defect, corrected here because the correction is the honest
    /// translation of the caller's question. When more than one portal existed but the supplied portal
    /// did not, the legacy function fell through its inner guard and returned the EMPTY string - that is,
    /// it reported success for a delete it had not performed. Only the calling screen's own null check at
    /// <c>Portals.ascx.vb:L390</c> concealed it, and any other caller of that shared function inherited
    /// the defect. This endpoint answers <c>404</c> instead, so "there was no such portal" and "the
    /// portal was removed" are distinguishable.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen wrote an audit entry keyed <c>PortalName</c> with the event type
    /// <c>PORTAL_DELETED</c> on the success path (<c>Portals.ascx.vb:L397-L398</c>). The event-log
    /// provider it used is beyond the migrated scope; the audit intent survives as structured request
    /// logging emitted by <c>Middleware/RequestLoggingMiddleware.cs</c>, correlated by the identifier
    /// that <c>Middleware/CorrelationIdMiddleware.cs</c> attaches, so no logging call is made here.
    /// </para>
    /// </remarks>
    [HttpDelete("{portalId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteAsync(int portalId, CancellationToken cancellationToken)
    {
        Result outcome = await _portalService
            .DeletePortalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // Error is null for a successful outcome and carries the reason for a failed one, so testing it
        // for null is the same test as asking whether the outcome failed - and it never reads the value
        // of a failed outcome, which throws by design.
        ResultReason? failure = outcome.Error;

        // MIGRATION: the last-portal refusal is translated here rather than by the shared table, and
        // this is the one place in this file that names a failure code. The shared translator in
        // ErrorHandling/GlobalExceptionHandler.cs classifies a code by the reason token at the end of it,
        // and this reason - "last_remaining" - matches none of the tokens it recognises as a conflict, so
        // it would otherwise be reported as 400. That is the wrong answer: the request is perfectly well
        // formed and there is no field for the caller to correct, which is exactly the distinction
        // between 400 and 409. Correcting it here keeps the change local to the one endpoint that can
        // produce this code and alters no sibling controller. Should the project prefer this centrally,
        // the single edit is to add the reason token to that translator's conflict set, after which this
        // branch becomes redundant and should be deleted rather than left to disagree with it.
        if (failure is not null
            && string.Equals(failure.Code, LastRemainingFailureCode, StringComparison.Ordinal))
        {
            return Problem(
                detail: failure.Message,
                statusCode: StatusCodes.Status409Conflict,
                type: LastRemainingProblemType);
        }

        // Every other outcome - success, and every other failure code - is translated by the shared
        // table, so this endpoint agrees with the rest of the API about what each code means.
        return this.Complete(outcome);
    }

    /// <summary>Reads a portal's configuration, projected for display.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The configuration projection.</returns>
    /// <response code="200">The configuration.</response>
    /// <response code="400">The route value could not be bound to an integer.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <remarks>
    /// <para>
    /// Serves the dedicated configuration screen, whose legacy counterparts are
    /// <c>Website/admin/Portal/SiteSettings.ascx.vb</c> and the configuration step of
    /// <c>SiteWizard.ascx.vb</c>. It is a projection of stored columns on the portal record, shaped for
    /// that screen, and reading it is separated from writing it: every settable column is written through
    /// the update action above, which is the single successor to the one legacy write path for all of
    /// them.
    /// </para>
    /// <para>
    /// MIGRATION: there is deliberately no companion write action here, and its absence is a decision
    /// rather than an omission. <see cref="IPortalService"/> declares a reader for this projection and no
    /// writer, for the reason just given, and inventing a service member to sit behind a second write
    /// endpoint is not this file's prerogative. The reasoning that there is no portal-settings table at
    /// all, and that the legacy site-setting keys are not carried forward, is recorded once on this type
    /// rather than repeated per action.
    /// </para>
    /// </remarks>
    [HttpGet("{portalId:int}/settings")]
    [ProducesResponseType(typeof(PortalSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalSettingsDto?>> GetSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<PortalSettingsDto?> outcome = await _portalService
            .GetPortalSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
