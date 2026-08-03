using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role resource - DotNetNuke's permission grouping - together with the membership that joins an
/// account to a role, exposed at <c>/api/v1/portals/{portalId}/roles</c> and
/// <c>/api/v1/portals/{portalId}/roles/{roleId}/users</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this type does, and the complete list.</strong> It binds a request, delegates to
/// <see cref="IRoleService"/>, and translates the returned outcome into a status code. Nothing else. No
/// billing or trial expiry date is computed here, no name is checked for uniqueness, no administrator
/// account is shielded from being date-limited, no cache is evicted, no audit record is written and no
/// notification is sent. Every one of those rules is real and every one of them lives in
/// <c>Application/Services/RoleService.cs</c>, because a rule implemented in a controller applies only
/// to callers arriving over HTTP and silently fails to apply to every other caller - a background job, a
/// console tool, a unit test - while a rule in the service applies to all of them and is the thing the
/// unit tests exercise.
/// </para>
/// <para>
/// <strong>Legacy origin.</strong> Three Web Forms screens collapse into this one resource:
/// <c>Website/admin/Security/Roles.ascx.vb</c> (the grid and its role-group selector),
/// <c>Website/admin/Security/EditRoles.ascx.vb</c> (the editor, which was simultaneously the add form,
/// the edit form and the delete command) and <c>Website/admin/Security/SecurityRoles.ascx.vb</c> (the
/// assignment screen, reached both from a role and from an account). Note that the paired markup under
/// that directory is lower-cased - <c>roles.ascx</c>, <c>editroles.ascx</c>, <c>securityroles.ascx</c> -
/// while the code-behind beside it is capitalised. All three reached the domain through the static
/// <c>Library/Components/Security/Roles/RoleController.vb</c>, measured at 892 lines and 42 public
/// members, whose relevant surface is folded into the nine actions below.
/// </para>
/// <para>
/// MIGRATION: the route nests the tenant rather than naming the collection at the API root, and the
/// choice is structural rather than cosmetic. Every member of <see cref="IRoleService"/> takes an
/// explicit portal identifier and scopes every read and write to it, because tenant isolation is a
/// preservation requirement of this migration; putting the tenant in the path means there is no
/// reachable address that omits it and therefore no request whose tenant has to be inferred. A role
/// belonging to another portal is reported as absent rather than returned, which is how the legacy
/// screens treated a cross-tenant identifier. The sibling resources owned the same way - role groups,
/// modules and profile definitions - nest identically, so a flat collection here would be the only
/// address in the API from which the tenant were missing.
/// </para>
/// <para>
/// MIGRATION: the legacy editor decided between inserting and updating by testing its role identifier
/// against -1 (<c>EditRoles.ascx.vb:L251</c>). That discriminator is deliberately not carried forward:
/// <c>POST</c> to the collection creates and <c>PUT</c> on a member address updates, so the method IS
/// the discriminator. No identifier is compared against -1, against 0 or against any lower bound
/// anywhere in this file, and none may be added, because none of those values is free. -1 is
/// simultaneously the legacy absent-integer marker <c>Null.NullInteger</c>
/// (<c>Library/Components/Shared/Null.vb:L41-L45</c>), the "Global Roles" selector value meaning a role
/// belongs to no group (<c>Roles.ascx.vb:L114</c>), the <c>RoleGroupID</c> such a role actually carries,
/// the token <c>glbRoleAllUsers</c> (<c>Library/Components/Shared/Globals.vb:L95</c>) and the seed of
/// <c>Portals.PortalID</c>, declared <c>IDENTITY (-1, 1)</c>, so it is also the first real portal. -2 is
/// both the "All Roles" selector value (<c>Roles.ascx.vb:L112</c>) and <c>glbRoleSuperUser</c>
/// (<c>Globals.vb:L96</c>). And 0 is not absent either: <c>Roles.RoleID</c> is declared
/// <c>IDENTITY (0, 1)</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115</c>, so the first
/// role a portal ever creates is numbered 0. A test against any of those values would misread a real
/// row. That is also why no range constraint appears on any identifier parameter below.
/// </para>
/// <para>
/// MIGRATION - discovered legacy defect, recorded rather than reproduced, and unreachable here in any
/// case. The editor applied its portal-scoped uniqueness guard on one branch only: the add branch looked
/// the name up first and refused a collision (<c>EditRoles.ascx.vb:L252-L258</c>), while the edit branch
/// updated with no such check (<c>:L259-L261</c>). Taken literally that means a rename could manufacture
/// a duplicate. It cannot happen here, and not because the check was tightened: renaming is not a
/// workflow this application ever offered. The edit screen reveals the name as a read-only label and
/// disables its only required-field validator (<c>EditRoles.ascx.vb:L131-L134</c>), and the terminal
/// <c>UpdateRole</c> procedure omits the column from its assignment list
/// (<c>04.00.04.SqlDataProvider:L454</c>). The update contract accordingly carries no name member, so
/// the <c>PUT</c> below cannot report a duplicate and does not advertise that it can; the duplicate
/// refusal belongs to <c>POST</c> alone, exactly as measured.
/// </para>
/// <para>
/// MIGRATION: the two role-group selector sentinels are not tunnelled through this API. The legacy
/// drop-down mixed two pseudo-entries - "All Roles" as -2 and "Global Roles" as -1 - in with the real
/// groups it listed (<c>Roles.ascx.vb:L110-L126</c>), and the grid then branched on them with
/// <c>If RoleGroupId &lt; -1</c> to choose between listing every role and listing one group's roles
/// (<c>:L72-L76</c>). So one integer carried a group identity and a filtering intent at once. The list
/// action below expresses the same two intents with a named, nullable, typed parameter whose absent
/// state means "do not filter", and neither -1 nor -2 is accepted as a magic value by any action here.
/// </para>
/// <para>
/// MIGRATION: the billing and trial frequency codes are preserved verbatim and there are SIX of them,
/// not four. The authority is <c>RoleController.vb:L537-L549</c>, which first short-circuits on a period
/// equal to <c>Null.NullInteger</c> and then selects on the code: <c>N</c> assigns
/// <c>Null.NullDate</c> so the assignment never expires, <c>O</c> assigns the far-future sentinel
/// <c>9999-12-31</c> so it is perpetual, <c>D</c> adds the period in days, <c>W</c> adds the period times
/// seven in days, <c>M</c> adds it in months and <c>Y</c> in years. A reduced four-code copy of that
/// switch also existed in the presentation layer (<c>SecurityRoles.ascx.vb:L291-L296</c>, handling only
/// <c>D</c>, <c>W</c>, <c>M</c> and <c>Y</c>) purely to pre-fill an input; it is not carried forward,
/// because recomputing an expiry above the service is precisely the duplication this layering forbids.
/// The codes are load-bearing stored data - the columns are declared <c>char(1)</c> at
/// <c>01.00.00.SqlDataProvider:L120</c> and <c>:L122</c>, and the legacy screens built resource keys of
/// the form <c>Frequency_&lt;code&gt;</c> from them (<c>MemberServices.ascx.vb:L209</c> and
/// <c>:L238</c>) - so the domain enumeration is explicitly valued by those characters and is never
/// renamed, re-lettered or integer-ised. The arithmetic itself, and the removal of the single
/// <c>Imports Microsoft.VisualBasic</c> in the whole in-scope legacy tree
/// (<c>RoleController.vb:L25</c>) that supplied <c>DateAdd</c>, belong to the service.
/// </para>
/// <para>
/// MIGRATION: no sentinel is manufactured or erased on this boundary. Serialisation is configured once
/// for the whole application to emit every property including nulls, so an absent date arrives as
/// <c>null</c> rather than vanishing, and a perpetual expiry arrives as <c>9999-12-31</c> rather than
/// being rounded away. That matters because the legacy read path funnelled every value through
/// <c>Null.SetNull</c>, whose date sentinel is <c>DateTime.MinValue</c> and whose string sentinel is the
/// empty string rather than <c>null</c> (<c>Null.vb:L66-L75</c>), so a database <c>NULL</c> and an empty
/// value were indistinguishable once loaded. Which of the two now stands for absence is settled once, in
/// the mapper between the contract and the persisted model. This file neither adds a sentinel nor
/// removes one, which is the only way the two can stay consistent.
/// </para>
/// <para>
/// MIGRATION: the write contracts keep all fifteen values the legacy editor posted
/// (<c>EditRoles.ascx.vb:L234-L248</c>), including the two the migration plan's named subset omits -
/// the trial fee and the icon file. Functional parity requires every field the legacy screen actually
/// sent, and dropping a field a user could fill in is a functional reduction rather than a
/// simplification. The measured legacy defaults - a zero fee, a period of one and the <c>N</c> code for
/// both the billing and the trial terms - and the gating that decides when the posted values are taken
/// at all (<c>:L212-L230</c>, where the trial block additionally requires a non-zero service fee) are
/// service-layer rules and are surfaced here only as validation failures and outcome codes.
/// </para>
/// <para>
/// MIGRATION: membership is addressed by identifier rather than by name. The legacy grid queried it by
/// role name and by user name (<c>SecurityRoles.ascx.vb:L246</c> and <c>:L253</c>, the latter passing
/// the empty-string <c>Null.NullString</c> sentinel as a third argument), which made the address of a
/// membership row change whenever a display value changed. Both directions are now keyed by the
/// identifiers in the path, and the two projections the legacy screen offered - the accounts holding a
/// role, and the roles an account holds - survive as the two read actions below.
/// </para>
/// <para>
/// MIGRATION: removing a membership reports an outcome instead of a bare flag. The legacy call returned
/// <c>Boolean</c> and the screen turned <c>False</c> into one undifferentiated message
/// (<c>SecurityRoles.ascx.vb:L569-L576</c>), so a caller could not tell a membership that was not held
/// from one it was not allowed to remove. The service returns a result carrying a stable reason instead,
/// which is what lets the two answer <c>404</c> and <c>403</c> respectively. In the same spirit, the
/// legacy paging idiom of a by-reference total is replaced by an envelope carrying the page and the
/// total together, so no action in this file declares an <c>out</c> or <c>ref</c> parameter.
/// </para>
/// <para>
/// MIGRATION: the portal administrator is still protected from being date-limited out of the
/// administrator role, and the rule is preserved with a correct comparison. The legacy guard read
/// <c>If User.UserID = PortalSettings.AdministratorId And Role.RoleID = PortalSettings.AdministratorRoleId.ToString</c>
/// (<c>SecurityRoles.ascx.vb:L523</c>) and cleared both date boxes. An <c>Integer</c> is compared to a
/// <c>String</c> there, which compiles only because the web pages are built with Option Strict off
/// (<c>Website/release.config:L125</c> sets <c>strict="false"</c>) while the class library is built with
/// it on; the coercion is recorded rather than reproduced, and the rule is enforced in the service with
/// a typed comparison. The service additionally refuses to remove that account from that role, and the
/// refusal reaches the caller as <c>403</c>.
/// </para>
/// <para>
/// MIGRATION: cache invalidation has moved out of the presentation layer. The legacy handlers followed
/// every write by evicting a bare literal key - <c>DataCache.RemoveCache("GetRoles")</c> at
/// <c>EditRoles.ascx.vb:L265</c> and again at <c>:L296</c> - so a caller reaching the domain by any
/// other path left the cache stale, and a single coarse key was dropped for a change to one role.
/// Invalidation now belongs to the service that performs the write, behind the shared cache
/// abstraction, so it happens once per write and for every caller. This controller injects no cache and
/// evicts nothing.
/// </para>
/// <para>
/// MIGRATION: the audit trail has moved too. The legacy handlers wrote event-log entries keyed
/// <c>ROLE_CREATED</c>, <c>ROLE_UPDATED</c> and <c>ROLE_DELETED</c> from inside their button handlers
/// (<c>EditRoles.ascx.vb:L254</c>, <c>:L261</c> and <c>:L293</c>). Those become structured log events
/// emitted by the application service, so the record is written on the operation rather than on the
/// transport, and the request-scoped logging middleware already attaches the correlation identifier
/// that ties the two together. No logger is injected here for that purpose, and no logged event carries
/// a credential or any other value the caller was not entitled to see.
/// </para>
/// <para>
/// MIGRATION: authorisation is declarative. The legacy screens authorised themselves imperatively with
/// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> and answered a refusal with
/// <c>Response.Redirect(NavigateURL("Access Denied"), True)</c> - a redirect to an HTML page that no
/// programmatic caller can interpret, and which conflated "you did not say who you are" with "you may
/// not do this". The class-level policy below separates them: <c>401</c> when no valid credential was
/// presented, <c>403</c> when the credential is valid but does not administer the tenant the request
/// resolves to. The measured legacy role name is <c>Administrators</c> - plural
/// (<c>Library/Components/Portal/PortalController.vb:L1390</c>) - and resolving it belongs to
/// <c>Extensions/AuthenticationExtensions.cs</c>, not to this file.
/// </para>
/// <para>
/// MIGRATION - a second discovered defect, recorded rather than reproduced. The assignment screen
/// guarded itself with
/// <c>If (Not (objUser Is Nothing) AndAlso objUser.IsSuperUser) OrElse PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False Then</c>
/// (<c>SecurityRoles.ascx.vb:L321-L324</c>). The <c>OrElse</c> stands where <c>AndAlso</c> was plainly
/// intended, so the condition is true for a super user and that most privileged of accounts was
/// redirected TO the access-denied page. The declarative policy does not reproduce the inversion. Note
/// also that the policy is the whole of the decision: <c>ICurrentUser.IsSuperUser</c> is informational,
/// is never consulted as a gate in a controller, and accordingly does not appear in this file.
/// </para>
/// <para>
/// MIGRATION: the policy named below is the portal-administrator policy and not one of the module or
/// tab policies, and the choice is load-bearing. Those resolve a module or tab identifier out of route
/// data; a role route carries neither, so one of them would find nothing to evaluate and fail closed,
/// answering <c>403</c> to every request including a legitimate one. The policy catalogue is closed and
/// an unregistered policy name fails at request time rather than at compile time, so the name is taken
/// from the <see cref="PolicyNames"/> constants and never spelled as a literal.
/// </para>
/// <para>
/// A distinction worth stating plainly, because it is easy to misread: the tenant the policy evaluates
/// is the one the REQUEST resolves to, from the host header through the portal alias table, and it is
/// not the <c>portalId</c> in the path. The routed identifier is the subject the application contract
/// scopes its work to; the resolved tenant is who the caller is administering. This file performs
/// neither resolution - the alias lookup belongs to the resolution middleware, which is the one place
/// in the project permitted to reach for the ambient HTTP context, and the scoping belongs to the
/// service. The practical consequence for a caller is that a request must arrive on a host that has an
/// alias row, or the tenant cannot be resolved and the policy denies for want of a portal rather than
/// for want of a role.
/// </para>
/// <para>
/// <strong>Paging and searching.</strong> The page index is zero-based, so index 0 is the first page,
/// and a free-text filter matches from the start of the value rather than anywhere within it. Both are
/// the legacy data layer's own conventions rather than new ones, and both are fixed by the envelope and
/// the request contract this file binds; restating or converting them here would give the API two
/// answers to one question. The listing of the roles an account holds is deliberately unpaged, because
/// the read it replaces was.
/// </para>
/// <para>
/// <strong>What this resource deliberately does not expose.</strong> There is no role-group create,
/// read, update or delete action - that is the role-group resource, even though both are served by the
/// same application contract because a group's every rule is a rule about the roles it classifies.
/// There is no self-service subscription surface: the legacy member-services screen
/// (<c>Website/admin/Users/MemberServices.ascx.vb</c>) subscribed and unsubscribed an account through
/// the same underlying assignment operation, so it maps onto the two membership actions below rather
/// than onto an invented address of its own. There is no redemption of a role's invitation code and no
/// billing-transaction action. There is no cache-invalidation action and no bulk action of any kind.
/// And there is no permission action: a role is the SUBJECT of a permission grant, never its store, so
/// module and page permissions belong to the permission resource, which publishes them as a read-only
/// catalogue. The derived <c>RoleStatus</c> classification is likewise absent by design - it has no
/// legacy ancestor, it is computed from an assignment's dates rather than persisted, and no contract
/// this file binds carries it.
/// </para>
/// <para>
/// <strong>Validation.</strong> Declarative validation is applied by the globally registered validation
/// filter, which runs before any action body and reports failures through the shared problem-details
/// factory as an RFC 7807 validation document. No validator is injected here, no action inspects model
/// state and no action body is wrapped in a <c>try</c> block: an expected failure arrives as a failed
/// outcome and is translated by status code, and an unexpected exception is left to surface and is
/// translated once, at the outermost boundary. A per-action validation call would have a silent failure
/// mode, because an endpoint whose author forgot the call looks exactly like one with no rules declared
/// against it - which is precisely what this file previously did for two of its four write paths.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
public sealed class RolesController : ControllerBase
{
    /// <summary>The application contract that owns roles, role groups and membership.</summary>
    /// <remarks>
    /// One dependency, and it is an application-layer interface. There is no repository, no persistence
    /// context, no unit of work, no cache, no clock and no HTTP context accessor here, and the project
    /// reference graph makes the first three unreachable from this layer rather than merely discouraged.
    /// The acting user is not injected either: the legacy assignment call passed the operator's
    /// identifier as its sixth argument (<c>SecurityRoles.ascx.vb:L542</c>), and that fact is now read
    /// from the current-user abstraction inside the service, so it cannot be spoofed by a request body.
    /// </remarks>
    private readonly IRoleService _roles;

    /// <summary>Initialises a new instance of the <see cref="RolesController"/> class.</summary>
    /// <param name="roles">The role service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <see langword="null"/>.</exception>
    public RolesController(IRoleService roles)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
    }

    /// <summary>Lists one page of the roles a portal defines.</summary>
    /// <param name="portalId">
    /// Identifier of the portal whose roles are listed. Forwarded exactly as bound: no lower bound is
    /// imposed, because the portal table is seeded <c>IDENTITY (-1, 1)</c> and -1 therefore identifies
    /// the first real portal rather than an absent one.
    /// </param>
    /// <param name="request">
    /// Paging, sorting and free-text filtering, bound from the query string. The page index is
    /// zero-based and the filter matches from the start of the value; both are fixed by the request
    /// contract and are not restated per action.
    /// </param>
    /// <param name="roleGroupId">
    /// Restricts the listing to one role group, or is omitted for every role in the portal irrespective
    /// of group. Omitting it is the successor to the legacy "All Roles" selector entry.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>One page of the portal's roles.</returns>
    /// <response code="200">
    /// The page, inside an envelope carrying the total across every page. An empty page is a legitimate
    /// answer and is never reported as a failure.
    /// </response>
    /// <response code="400">
    /// A query value could not be bound, or a paging rule was broken - a negative page index, or a page
    /// size above the ceiling the request contract declares. Reported as an RFC 7807 validation document
    /// naming the offending member.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or a role group was named that the portal does not define. Both
    /// are distinct from an empty page, which is a successful answer.
    /// </response>
    /// <remarks>
    /// MIGRATION: this replaces the branch at <c>Roles.ascx.vb:L72-L76</c>, which chose between
    /// <c>GetPortalRoles</c> and <c>GetRolesByGroup</c> by testing its selector value against -1 and
    /// returned an untyped <c>ArrayList</c> either way. The two intents are now one member with an
    /// optional, nullable, typed group filter, and the untyped collection becomes a typed page. Because
    /// the filter is nullable rather than sentinel-valued, the third intent the legacy drop-down offered
    /// - the roles belonging to NO group, its "Global Roles" entry - is not expressible: the absent
    /// state of the filter already means "do not filter". That is a known difference from the legacy
    /// screen, recorded here rather than reintroduced by letting -1 travel as a magic value, which would
    /// restore the collision where one integer meant an absent value, a stored identifier and a
    /// filtering intent at the same time.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/roles")]
    [ProducesResponseType(typeof(PagedResult<RoleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<RoleListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] PagedRequest request,
        [FromQuery] int? roleGroupId,
        CancellationToken cancellationToken)
    {
        Result<PagedResult<RoleListItemDto>> outcome = await _roles
            .ListRolesAsync(portalId, request, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never
        // has its value touched - reading the value of a failed outcome throws by design - and any
        // failure code is mapped to a status through the single table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one role.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">
    /// Identifier of the role to read. Forwarded exactly as bound and never compared against a
    /// sentinel; the column is seeded <c>IDENTITY (0, 1)</c>, so 0 addresses a portal's first role.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The role, or an indication that it does not exist in this portal.</returns>
    /// <response code="200">
    /// The role, including its paid-membership terms. The frequency members carry the legacy
    /// single-character codes verbatim - <c>N</c> for none, <c>O</c> for one-time, <c>D</c>, <c>W</c>,
    /// <c>M</c> and <c>Y</c> for a period in days, weeks, months and years - because those characters
    /// are the stored values.
    /// </response>
    /// <response code="400">An identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the portal defines no role with that identifier. A role that
    /// exists in a different portal is reported here rather than returned, which is how the legacy
    /// screens treated a cross-tenant identifier.
    /// </response>
    [HttpGet("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDetailDto?>> GetAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken)
    {
        Result<RoleDetailDto?> outcome = await _roles
            .GetRoleAsync(portalId, roleId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no value means the role is absent, which is a different answer
        // from a failed lookup; the shared translator turns the first into 404 and maps the second.
        return this.Complete(outcome);
    }

    /// <summary>Creates a role in a portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that will own the role. Authoritative, and deliberately not restatable
    /// in the body: a value that cannot be supplied cannot contradict the route, so no reconciliation
    /// check is needed and a caller can never assert a tenant other than the one it addressed.
    /// </param>
    /// <param name="request">
    /// The role to create: its name, description, optional role group, visibility and automatic-enrolment
    /// switches, invitation code, icon, and the paid-membership terms - a service fee with a billing
    /// period and frequency, and a trial fee with a trial period and frequency.
    /// </param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>The created role, addressed by the location header.</returns>
    /// <response code="201">
    /// The role was created. The body is the stored representation, including the identifier the database
    /// assigned, and the location header addresses it. Note that the assigned identifier may legitimately
    /// be 0, because the column is seeded <c>IDENTITY (0, 1)</c>.
    /// </response>
    /// <response code="400">
    /// The request breaks a declared rule - an absent or over-long name, a negative fee, a period that is
    /// not positive, a frequency code outside the six the schema permits - or the role could not be
    /// stored. Reported as an RFC 7807 validation document naming each offending member, with the message
    /// text passed through exactly as the rule declared it.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the request names a role group the portal does not define.
    /// </response>
    /// <response code="409">
    /// The portal already defines a role with that name. This is the successor to the legacy editor's
    /// duplicate refusal, which the add branch alone applied.
    /// </response>
    /// <remarks>
    /// MIGRATION: the fifteen values this replaces were assigned one at a time onto a mutable role object
    /// at <c>EditRoles.ascx.vb:L234-L248</c> and the screen then chose between inserting and updating by
    /// testing the identifier against -1. Here the method is the discriminator and the values arrive as
    /// one named contract. Uniqueness of the name within the portal is decided by the service, which is
    /// the only place that can decide it, and reaches the caller as <c>409</c>; the legacy screen's
    /// check-then-insert sequence (<c>:L252-L253</c>) had a race this arrangement removes, since the
    /// service performs both inside one unit of work.
    /// </remarks>
    [HttpPost("portals/{portalId:int}/roles")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDetailDto>> CreateAsync(
        int portalId,
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        Result<RoleDetailDto> outcome = await _roles
            .CreateRoleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        // The location header is built from this request's own path plus the new identifier, which is
        // exactly the member address of the collection just posted to, so the address is spelled once.
        return this.Created(outcome, created => created.RoleId);
    }

    /// <summary>Updates a role's writable state.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role to update.</param>
    /// <param name="request">
    /// The new state: description, role group, visibility and automatic-enrolment switches, invitation
    /// code, icon, and the billing and trial terms. The role's NAME is deliberately absent and a
    /// submitted one is ignored rather than applied.
    /// </param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>The updated role.</returns>
    /// <response code="200">
    /// The role as stored after the update. A representation is returned rather than an empty body
    /// because the service may normalise a submitted value, and a caller should not have to re-read to
    /// discover what was actually kept.
    /// </response>
    /// <response code="400">
    /// The request breaks a declared rule, on the same terms as creation. Reported as an RFC 7807
    /// validation document.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, the portal defines no role with that identifier, or the request
    /// names a role group the portal does not define.
    /// </response>
    /// <remarks>
    /// MIGRATION: there is deliberately no <c>409</c> here, and its absence is measured rather than
    /// assumed. Renaming a role was never a workflow this application offered - the edit screen reveals
    /// the name as a read-only label and disables its only required-field validator
    /// (<c>EditRoles.ascx.vb:L131-L134</c>), and the terminal stored procedure omits the column from its
    /// assignment list (<c>04.00.04.SqlDataProvider:L454</c>) - so the update contract carries no name
    /// member and this path cannot produce a duplicate to refuse. That is also why the legacy editor's
    /// uniqueness guard on the add branch only (<c>:L252-L258</c>, with no equivalent at <c>:L259-L261</c>)
    /// is coherent rather than defective in practice: the value it guarded could not change. The defect
    /// is recorded, not silently "improved", and a name member must not be added here to create one.
    /// </remarks>
    [HttpPut("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDetailDto>> UpdateAsync(
        int portalId,
        int roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        Result<RoleDetailDto> outcome = await _roles
            .UpdateRoleAsync(portalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a role.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role to delete.</param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>An empty response when the role has been removed.</returns>
    /// <response code="204">
    /// The role has been removed, together with every membership that referenced it. An empty body is
    /// correct: there is no remaining representation to return.
    /// </response>
    /// <response code="400">An identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the portal defines no role with that identifier.
    /// </response>
    /// <remarks>
    /// MIGRATION: the legacy handler deleted the role, wrote a <c>ROLE_DELETED</c> event-log entry and
    /// then evicted the coarse literal cache key, all from a button handler
    /// (<c>EditRoles.ascx.vb:L291-L296</c>). Here the deletion, its audit record and its cache
    /// invalidation are one service operation, so a caller reaching the domain by another path cannot
    /// leave the audit trail or the cache inconsistent. Cascading removal of the role's memberships is
    /// likewise the service's, inside a single unit of work; the legacy sequence was not transactional
    /// across statements.
    /// </remarks>
    [HttpDelete("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .DeleteRoleAsync(portalId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }


    /// <summary>Lists one page of the accounts that hold a role.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role whose members are listed.</param>
    /// <param name="request">
    /// Paging, sorting and free-text filtering, bound from the query string, on the same zero-based,
    /// match-from-the-start terms as every other listing in this API.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>One page of the accounts holding the role.</returns>
    /// <response code="200">
    /// The page, inside an envelope carrying the total across every page. An empty page means the role
    /// has no members and is a successful answer.
    /// </response>
    /// <response code="400">
    /// A query value could not be bound, or a paging rule was broken. Reported as an RFC 7807 validation
    /// document.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the portal defines no role with that identifier.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy grid asked this question by role NAME -
    /// <c>GetUserRolesByRoleName(PortalId, Role.RoleName)</c> at <c>SecurityRoles.ascx.vb:L246</c> - so
    /// the address of a membership set changed whenever a display value changed. It is keyed by
    /// identifier here. The legacy read was also duplicated: two byte-identical listings existed on the
    /// static controller, and they collapse into the one member this action calls.
    /// </para>
    /// <para>
    /// MIGRATION - a functional reduction, stated rather than hidden. The legacy grid bound five columns
    /// (<c>securityroles.ascx</c>), among them the membership's effective and expiry dates. This
    /// projection carries the account, not the membership, so those two dates are not on the read path;
    /// they travel on the write path, on the assignment contract. Restoring them would require a named
    /// membership projection, and inventing one here would put a type on the wire that no other layer
    /// owns.
    /// </para>
    /// </remarks>
    [HttpGet("portals/{portalId:int}/roles/{roleId:int}/users")]
    [ProducesResponseType(typeof(PagedResult<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<UserListItemDto>>> ListUsersAsync(
        int portalId,
        int roleId,
        [FromQuery] PagedRequest request,
        CancellationToken cancellationToken)
    {
        Result<PagedResult<UserListItemDto>> outcome = await _roles
            .ListRoleUsersAsync(portalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the roles one account holds in a portal.</summary>
    /// <param name="portalId">Identifier of the portal the account belongs to.</param>
    /// <param name="userId">Identifier of the account whose roles are listed.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The roles the account holds in this portal.</returns>
    /// <response code="200">
    /// The roles, as a JSON array. An empty array means the account holds none and is a successful
    /// answer.
    /// </response>
    /// <response code="400">An identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the portal has no account with that identifier.
    /// </response>
    /// <remarks>
    /// <para>
    /// Addressed under the account because the account is what is being described, and served by this
    /// controller because roles are what is returned. The alternative - hanging it off the account
    /// resource - would give two controllers a reason to depend on the role service for one answer.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy grid asked this by USER NAME -
    /// <c>GetUserRolesByUsername(PortalId, User.Username, Null.NullString)</c> at
    /// <c>SecurityRoles.ascx.vb:L253</c>, whose third argument is the empty-string sentinel rather than a
    /// null - and six near-identical readers of an account's roles existed on the static controller. All
    /// six collapse into the one member this action calls, keyed by identifier, with no sentinel argument
    /// to pass.
    /// </para>
    /// <para>
    /// MIGRATION: deliberately unpaged, and no page, size or sort parameter is accepted. The read it
    /// replaces returned every role an account held with no pager at all, and both of its consumers bound
    /// the whole answer rather than a page of it. Adding paging would introduce a contract the legacy
    /// application never had.
    /// </para>
    /// </remarks>
    [HttpGet("portals/{portalId:int}/users/{userId:int}/roles")]
    [ProducesResponseType(typeof(IReadOnlyList<RoleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RoleListItemDto>>> ListForUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<RoleListItemDto>> outcome = await _roles
            .ListUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Grants an account a role, for a period.</summary>
    /// <param name="portalId">Identifier of the portal that owns both the role and the account.</param>
    /// <param name="roleId">Identifier of the role being granted.</param>
    /// <param name="request">
    /// The account to enrol, with the optional dates the membership takes effect and ceases, and the
    /// caller's request that the account be notified.
    /// </param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>An empty response when the membership has been recorded.</returns>
    /// <response code="204">
    /// The membership has been recorded, or an existing one has been amended. An empty body and this
    /// status are returned in both cases: the operation is idempotent by design, and it produces no
    /// separately addressable resource for a location header to point at, which is why <c>201</c> is
    /// deliberately not used here. Re-posting the same account amends the dates of the membership it
    /// already holds rather than duplicating it, so a repeat is not reported as a conflict.
    /// </response>
    /// <response code="400">
    /// The request breaks a declared rule - no account identified, or an expiry that precedes its
    /// effective date. Reported as an RFC 7807 validation document.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, the portal defines no role with that identifier, or it has no
    /// account with the identifier the body carries.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: this replaces a seven-argument call -
    /// <c>AddUserRole(User, Role, PortalSettings, datEffectiveDate, datExpiryDate, UserId, chkNotify.Checked)</c>
    /// at <c>SecurityRoles.ascx.vb:L542</c> - and three overloads of it on the static controller. Two of
    /// its arguments do not appear on this contract at all. The ambient per-request settings composite
    /// becomes the tenant in the path plus the scoped tenant context the service reads; and the sixth
    /// argument, the operator's own identifier, is read from the current-user abstraction inside the
    /// service, so the acting account is established from the credential rather than from anything a
    /// request body can assert.
    /// </para>
    /// <para>
    /// MIGRATION: the two dates are optional and absent means absent. The legacy screen substituted
    /// <c>Null.NullDate</c> - that is, <c>DateTime.MinValue</c> - for an empty date box
    /// (<c>SecurityRoles.ascx.vb:L528-L539</c>), and the empty string sentinel behaved the same way for
    /// text. Here a date omitted from the body is <c>null</c> and stays <c>null</c>; serialisation is
    /// configured once to emit nulls rather than drop the member, so an absent date is visible as absent
    /// on the way back out. What the service does with an absent date is unchanged in effect: an expiry
    /// is derived from the role's billing or trial terms, using the six-code frequency table cited at the
    /// head of this file, and the code <c>O</c> still yields the far-future <c>9999-12-31</c>, which
    /// reaches the wire exactly as stored. No date arithmetic is performed in this file.
    /// </para>
    /// <para>
    /// MIGRATION: the notification flag survives on the contract because it was a genuine caller choice
    /// the legacy screen presented, but the mail subsystem it drove is out of scope for this migration, so
    /// no notification is sent and a successful response must not be read as implying one was. A
    /// deliberate functional reduction, recorded rather than absorbed. There is no event bus, no domain
    /// event and no notification hub anywhere in this path: inventing one to carry a flag nothing consumes
    /// would be scope creep dressed as fidelity.
    /// </para>
    /// <para>
    /// MIGRATION: the portal administrator's dates are still protected. The legacy screen cleared both
    /// date boxes when the addressed account was the portal's designated administrator and the addressed
    /// role its administrator role (<c>SecurityRoles.ascx.vb:L523-L526</c>), comparing an <c>Integer</c>
    /// to a <c>String</c> to decide it. The rule is preserved in the service with a typed comparison and
    /// the coercion is recorded; the effect a caller sees is that submitted dates are discarded for that
    /// one pairing rather than rejected.
    /// </para>
    /// </remarks>
    [HttpPost("portals/{portalId:int}/roles/{roleId:int}/users")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AssignUserAsync(
        int portalId,
        int roleId,
        [FromBody] RoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .AssignUserToRoleAsync(portalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes an account's membership of a role.</summary>
    /// <param name="portalId">Identifier of the portal that owns both the role and the account.</param>
    /// <param name="roleId">Identifier of the role the account is being removed from.</param>
    /// <param name="userId">Identifier of the account being removed.</param>
    /// <param name="cancellationToken">Abandons the write when the caller disconnects.</param>
    /// <returns>An empty response when the membership has been removed.</returns>
    /// <response code="204">
    /// The membership no longer stands. Note that for a paid role whose trial has been used this means the
    /// membership was expired rather than deleted, which is the legacy behaviour and is preserved: the row
    /// is retained so the trial-used fact is not lost. The service reports which of the two happened as an
    /// informational reason on the successful outcome, and either way the account no longer holds the
    /// role.
    /// </response>
    /// <response code="400">An identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The removal is refused because it is protected: the portal's designated administrator may not be
    /// stripped of that portal's administrator role, and no account may be removed from the portal's
    /// registered-users role. The legacy rule lived in a predicate the source itself flagged as a hack and
    /// duplicated in two bodies; it is enforced once, in the service. This status is also returned when
    /// the caller does not administer the portal the request resolves to - the problem type distinguishes
    /// the two.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the account does not hold that role. The legacy call could not
    /// express this: it returned <c>True</c> when the portal or the membership could not be found and
    /// silently did nothing, so a caller could not distinguish a real removal from a no-op.
    /// </response>
    /// <remarks>
    /// MIGRATION: the nested member address replaces a grid-row command keyed by the membership's own
    /// surrogate identifier (<c>SecurityRoles.ascx.vb:L569</c> and <c>:L574</c> parse it out of the data
    /// key), which was only ever obtainable by first rendering the grid. Addressing the pairing directly
    /// makes the operation reachable without a prior read. The legacy call returned a bare <c>Boolean</c>
    /// and the screen turned <c>False</c> into one undifferentiated message
    /// (<c>:L570</c> and <c>:L575</c>); the service returns an outcome carrying a stable reason instead,
    /// which is what allows "not held" and "not permitted" to answer <c>404</c> and <c>403</c> rather
    /// than sharing one message. Both legacy overloads - the one reached from a role and the one reached
    /// from an account - collapse into this single operation.
    /// </remarks>
    [HttpDelete("portals/{portalId:int}/roles/{roleId:int}/users/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveUserAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _roles
            .RemoveUserFromRoleAsync(portalId, roleId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
