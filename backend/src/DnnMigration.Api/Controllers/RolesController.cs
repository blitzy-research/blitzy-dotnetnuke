using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role resource - DotNetNuke's permission grouping - together with the membership that joins an
/// account to a role, exposed at <c>/api/v1/roles</c> and <c>/api/v1/roles/{roleId}/users</c>, and
/// additionally at <c>/api/v1/portals/{portalId}/roles</c> and
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
/// <c>Portals.PortalID</c>, declared <c>IDENTITY (-1, 1)</c>, so it is also a real portal key - the seed
/// and first generated value, alongside the shipped default portal row's explicit 0. -2 is
/// both the "All Roles" selector value (<c>Roles.ascx.vb:L112</c>) and <c>glbRoleSuperUser</c>
/// (<c>Globals.vb:L96</c>). And 0 is not absent either: <c>Roles.RoleID</c> is declared
/// <c>IDENTITY (0, 1)</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115</c>, so the first
/// role a portal ever creates is numbered 0. A test against any of those values would misread a real
/// row. That is also why no range constraint appears on any identifier parameter below.
/// </para>
/// <para>
/// MIGRATION - discovered legacy defect, closed rather than reproduced, and the closure is documented.
/// The editor applied its portal-scoped uniqueness guard on one branch only: the add branch looked the
/// name up first and refused a collision (<c>EditRoles.ascx.vb:L252-L258</c>), while the edit branch
/// updated with no such check (<c>:L259-L261</c>). That asymmetry was coherent only because the legacy
/// edit path could not change a name at all - the screen revealed a read-only label and disabled its
/// only required-field validator (<c>EditRoles.ascx.vb:L131-L134</c>), and the terminal
/// <c>UpdateRole</c> procedure omits the column from its assignment list
/// (<c>04.00.04.SqlDataProvider:L454</c>). The library-level member the service layer replaces did carry
/// the name (<c>RoleController.vb:L254</c>), and the terminal schema constrains
/// <c>(PortalID, RoleName)</c> uniquely (<c>03.00.09.SqlDataProvider:L304</c>), so the update contract
/// carries a name member and the guard applies to BOTH write paths. <c>POST</c> and <c>PUT</c> therefore
/// both advertise the duplicate refusal as <c>409</c>, the <c>PUT</c> comparison excluding the role being
/// edited so that resubmitting a role's own name is a no-op. The rename capability is a deliberate
/// behavioural difference from the legacy screen and is itemised in <c>MIGRATION_NOTES.md</c>.
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
/// MIGRATION: no sentinel is manufactured or erased on this boundary, but absence is expressed by an
/// ABSENT MEMBER rather than by a JSON null. Serialisation is configured once for the whole application
/// with a when-writing-null ignore condition, so a null-valued property is omitted from the response
/// body; an absent date therefore does not appear at all, while a perpetual expiry arrives as
/// <c>9999-12-31</c> rather than being rounded away. A client reads a missing member as "no value" and
/// never as <c>DateTime.MinValue</c>, 0 or -1. That matters because the legacy read path funnelled every value through
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
/// The two are nevertheless RECONCILED, and by the policy rather than by anything written here: a caller
/// that is not a host account and names a portal other than the one its request resolved to is refused
/// before an action body is entered. Being distinct is not the same as being unrelated, and that
/// reconciliation is what keeps a routed identifier from becoming a way to read another tenant's roles.
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
/// <strong>Two addresses, one implementation.</strong> The specified addresses for this resource are the
/// flat <c>/api/v1/roles</c> and <c>/api/v1/roles/{roleId}/users</c>, and each action declares its flat
/// template first because that is the canonical one. The nested
/// <c>/api/v1/portals/{portalId}/roles...</c> forms are retained alongside them, and both are served by the
/// same nine actions - no second controller, no duplicated body, no forwarding action. They differ in
/// exactly one respect, which is where the tenant comes from. On a FLAT address it is the tenant the
/// request resolved to, and a caller cannot name another one because there is no parameter through which
/// to name it, so the isolation is structural. On a NESTED address the routed identifier is reconciled
/// against the resolved tenant by the policy, as described above. The identifier is bound
/// <c>[FromRoute]</c> and never from the query string, and that is load-bearing rather than tidy: a
/// query-bound tenant would travel on the flat addresses too, where the policy has no route value to
/// reconcile it against, and would reopen precisely the hole the reconciliation closes.
/// </para>
/// <para>
/// The nested addresses are also what make a host account able to administer a NAMED tenant's roles. A
/// request resolves to whichever portal's alias it arrived on and an HTTP client cannot forge another
/// tenant's host name, so removing them would leave cross-tenant administration with no address at all.
/// </para>
/// <para>
/// <strong>Why there are nine actions and not eight.</strong> The extra one is the account-side projection
/// of membership - the roles ONE ACCOUNT holds - and it is required rather than additional. The legacy
/// assignment screen offered both directions of the same relation from one page: its <c>BindGrid</c> bound
/// <c>GetUserRolesByRoleName</c> when a role was the subject and <c>GetUserRolesByUsername</c> when an
/// account was (<c>Website/admin/Security/SecurityRoles.ascx.vb:L246</c> and <c>:L253</c>). Serving
/// only the role-side projection would leave the account-side half of that screen unimplementable, which
/// the functional-parity requirement does not permit. Both projections read through the same application
/// contract and neither owns logic of its own.
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
    /// The only service dependency, and it is an application-layer interface. There is no repository, no
    /// persistence context, no unit of work, no cache, no clock and no HTTP context accessor here. The
    /// first three are unreachable by ACCESSIBILITY rather than by the reference graph: this project does
    /// reference <c>DnnMigration.Infrastructure</c>, because composition must register its services, but
    /// <c>DnnDbContext</c>, the unit of work and every repository implementation are
    /// <see langword="internal"/> to that assembly, so naming one here would not compile. The tenant
    /// holder beside it is not a fourth kind of thing: it reads a value the middleware already resolved
    /// and performs no lookup of its own.
    /// The acting user is not injected either: the legacy assignment call passed the operator's
    /// identifier as its sixth argument (<c>SecurityRoles.ascx.vb:L542</c>), and that fact is now read
    /// from the current-user abstraction inside the service, so it cannot be spoofed by a request body.
    /// </remarks>
    /// <summary>
    /// Failure code carried as the problem type when the request reached this action without a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A distinct code from the middleware's generic refusal, so an operator reading a support log can tell
    /// "this host resolves to no portal" apart from "this caller lacks the grant" - while the caller reads
    /// the same fixed wording either way and learns nothing from the difference. The same constant is
    /// declared by <c>PortalAliasResolutionMiddleware</c> and by the other controllers that guard on tenant
    /// resolution, because the two paths must be indistinguishable to a client.
    /// </para>
    /// <para>
    /// MIGRATION: the tenant guards in this controller answered with a bare <c>Forbid()</c>. A controller's
    /// <c>Forbid()</c> does NOT pass through the authorisation middleware's result handler, so it produced a
    /// 403 with an EMPTY BODY while every action here declares a problem document for 403 - the response
    /// contradicted its own declaration, and it was distinguishable from the middleware's refusal for the
    /// identical cause. Routing the refusal through the shared helper closes both gaps at once.
    /// </para>
    /// </remarks>
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    private readonly IRoleService _roles;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    /// <remarks>
    /// Needed only by the flat addresses, which carry no tenant identifier and therefore have to read the
    /// one the alias-resolution middleware already resolved. It is an abstraction over that resolution and
    /// not a reach for the ambient HTTP context: this file still performs no alias lookup and still touches
    /// no request feature bag, so the claim made above - that resolution belongs to the middleware and
    /// scoping belongs to the service - continues to hold.
    /// </remarks>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="RolesController"/> class.</summary>
    /// <param name="roles">The role service.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host, which is the
    /// tenant the flat addresses act on.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public RolesController(IRoleService roles, IPortalContextHolder portalContext)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>
    /// Chooses the tenant an action acts on: the routed identifier when a nested address was used, and
    /// otherwise the tenant the request resolved to.
    /// </summary>
    /// <param name="routedPortalId">
    /// The identifier bound from the route, or <see langword="null"/> when a flat address was used.
    /// </param>
    /// <returns>
    /// The tenant identifier, or <see langword="null"/> when a flat address was used and the request
    /// resolved to no tenant at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The routed value wins when present, because on that address it IS the subject of the request and the
    /// portal-administrator policy has already reconciled it against the resolved tenant. It is forwarded
    /// exactly as bound: no lower bound is imposed and no value is treated as "absent", because the portal
    /// table is <c>IDENTITY (-1, 1)</c> and -1 is therefore a real portal rather than the legacy
    /// missing-integer sentinel.
    /// </para>
    /// <para>
    /// The holder throws rather than yielding a placeholder tenant, so resolution is tested before the
    /// tenant is read, and the null answer here is a precondition rather than a state a caller can steer
    /// into.
    /// </para>
    /// <para>
    /// MIGRATION: WHAT REFUSES FIRST IS THE TENANT-RESOLUTION MIDDLEWARE, NOT THE CLASS-LEVEL POLICY, and an
    /// earlier revision of this block credited the policy. Measured both ways against a running instance: a
    /// portal administrator addressing a host name with no alias row is refused by the policy with
    /// <c>auth.not_permitted</c>, but a superuser passes that policy from any host name whatsoever, because
    /// the policy is anchored to the portal named in the route and the unscoped route names none. That
    /// request is refused by the middleware instead, with <c>portal.tenant_unresolved</c>. Either way the
    /// action never runs, so this guard is defence in depth and is expected to be unreachable; it stays
    /// because the alternative to an unreachable refusal is the holder throwing, and a <c>500</c> is a worse
    /// answer than a <c>403</c> for a condition that is not the caller's fault.
    /// </para>
    /// <para>
    /// MIGRATION: THE REFUSAL IS NO LONGER A BARE <c>403</c>. This block previously said the caller sees the
    /// same bare status either way, which was the justification for answering with <c>Forbid()</c>, and it
    /// described a body that contradicted this action's own declaration: <c>Forbid()</c> does not pass
    /// through the authorisation middleware's result handler, so it produced an EMPTY body while every action
    /// here declares a problem document for <c>403</c>. The guard now answers through the shared
    /// problem-details path carrying the same failure code the middleware uses, so the two refusals are
    /// indistinguishable to a client keying on that code.
    /// </para>
    /// </remarks>
    private int? ResolvePortalId(int? routedPortalId)
    {
        if (routedPortalId is { } portalId)
        {
            return portalId;
        }

        return _portalContext.IsResolved ? _portalContext.Current.PortalId : null;
    }

    /// <summary>Lists one page of the roles a portal defines.</summary>
    /// <param name="portalId">
    /// Identifier of the portal whose roles are listed. Forwarded exactly as bound: no lower bound is
    /// imposed, because <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c>, so -1 is its seed and first
    /// generated value while the shipped default portal row carries an explicit 0. Both are real portal
    /// keys and neither means "absent".
    /// </param>
    /// <param name="request">
    /// Paging, sorting and free-text filtering, bound from the query string. The page index is
    /// zero-based and the filter matches from the start of the value; both are fixed by the request
    /// contract and are not restated per action.
    /// </param>
    /// <param name="roleGroupId">
    /// Restricts the listing to one role group. Omit it to let <paramref name="scope"/> decide. Zero is a
    /// real group key - <c>RoleGroups.RoleGroupID</c> is <c>IDENTITY (0, 1)</c> - so absence is expressed
    /// by omitting the value and never by sending a small number.
    /// </param>
    /// <param name="scope">
    /// Chooses between the two answers a group identifier cannot express: <c>All</c>, every role in the
    /// portal, which is the successor to the legacy "&lt; All Roles &gt;" selector entry and the default
    /// when the parameter is omitted; and <c>Ungrouped</c>, only the roles belonging to no group at all,
    /// which is the successor to its "&lt; Global Roles &gt;" entry. The parameter is a closed
    /// enumeration, so a value outside it is refused by model binding.
    /// <para>
    /// That refusal covers an unrecognised spelling AND an undefined numeric literal, and the second half
    /// is worth stating because it is easy to assume otherwise. MVC's enumeration binder tests DEFINED
    /// membership for a non-flags enumeration, so <c>?scope=999</c> is answered <c>400</c> with
    /// <c>errors["scope"]</c> naming the value before this action runs, while <c>?scope=0</c> and
    /// <c>?scope=1</c> bind normally - measured against this API rather than assumed. The application
    /// contract additionally tests membership itself and would answer <c>role_group.scope_invalid</c>,
    /// which is unreachable over HTTP and exists because that layer is callable without MVC.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>One page of the portal's roles, in the wire envelope: an <c>items</c> array of rows and a <c>meta</c> object carrying the total across every page, the page index and the page size. The domain paging type is not serialised.</returns>
    /// <response code="200">
    /// The page, inside an envelope carrying the total across every page. An empty page is a legitimate
    /// answer and is never reported as a failure.
    /// </response>
    /// <response code="400">
    /// A query value could not be bound - including a scope outside the enumeration, which the binder
    /// refuses by name - or a paging rule was broken, such as a negative page index or a page size above the
    /// ceiling the request contract declares. Those cases are RFC 7807 validation documents naming the
    /// offending member. A fully-bound, fully-validated request can ALSO be refused here when the two
    /// narrowing arguments contradict each other (<c>role_group.scope_invalid</c>), and that refusal carries
    /// the failure code as its problem type with no single member to blame - the contradiction is between
    /// two members, each individually valid - so its body is a plain problem document.
    /// <para>
    /// MIGRATION: the declared schema is therefore the COMMON SUPERTYPE and not the validation document.
    /// Declaring the narrower shape promised a map of refused members on every refusal, which the
    /// contradiction case cannot contain. <c>ResponseDeclarationContractTests</c> names this operation in its
    /// semantic-refusal exemption set and holds it to the supertype, so the declaration cannot quietly drift
    /// back to either extreme.
    /// </para>
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
    /// <para>
    /// MIGRATION: this replaces the branch at <c>Roles.ascx.vb:L72-L76</c>, which chose between
    /// <c>GetPortalRoles</c> and <c>GetRolesByGroup</c> by testing its selector value against -1 and
    /// returned an untyped <c>ArrayList</c> either way. All THREE of the intents that branch served are
    /// expressible here, and the untyped collection becomes a typed page.
    /// </para>
    /// <para>
    /// MIGRATION: an earlier revision of this endpoint offered only two of the three, and recorded the
    /// missing one - the legacy drop-down's "&lt; Global Roles &gt;" entry, the roles belonging to no
    /// group - as "a known difference from the legacy screen". The concern that produced that gap was
    /// sound: letting -1 travel as a magic value would restore the collision in which one integer meant
    /// an absent value, a stored identifier and a filtering intent at once. The gap itself was not
    /// acceptable, because the legacy screen's selector is part of the functional parity this migration
    /// owes. Both are satisfied by separating the two concerns: the identifier stays a plain nullable key
    /// with no magic values, and the branch that no key can name is chosen by a closed enumeration. The
    /// evidence for all three legacy bands, including the stale legacy comment that misdescribes the
    /// middle one, is recorded on <see cref="RoleGroupScope"/>.
    /// </para>
    /// </remarks>
    [HttpGet("roles")]
    [HttpGet("portals/{portalId:int}/roles")]
    [ProducesResponseType(typeof(PagedResponse<RoleListItemDto>), StatusCodes.Status200OK)]
    // BASE ProblemDetails, not ValidationProblemDetails. Both shapes are reachable: the binder and the paging
    // validator name the offending member, while the contradiction between a group identifier and the
    // ungrouped scope is refused with a failure code and no single member to blame. The supertype is the only
    // schema that describes both, and ResponseDeclarationContractTests names this operation in its
    // semantic-refusal exemption set for exactly this reason.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<RoleListItemDto>>> ListAsync(
        [FromRoute] int? portalId,
        [FromQuery] RolePagedRequest request,
        [FromQuery] int? roleGroupId,
        [FromQuery] RoleGroupScope scope,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // An omitted scope binds to the enumeration's zero member, which is All - the same answer this
        // endpoint gave before the parameter existed - so no existing caller changes behaviour. The
        // contradictory pairing is not tested here: the application contract owns that rule and reports it
        // as a reason code, and a second copy of the test in this controller could disagree with it.
        Result<PagedResult<RoleListItemDto>> outcome = await _roles
            .ListRolesAsync(scopedPortalId, request, roleGroupId, scope, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never
        // has its value touched - reading the value of a failed outcome throws by design - and any
        // failure code is mapped to a status through the single table this API uses.
        // Projected onto the wire envelope here rather than returned as the domain page. CompletePage
        // applies PagedResponse<T>.From, so the response carries `items` plus `meta` and the domain
        // paging type never crosses the boundary.
        return this.CompletePage(outcome);
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
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// No portal bears that identifier, or the portal defines no role with that identifier. A role that
    /// exists in a different portal is reported here rather than returned, which is how the legacy
    /// screens treated a cross-tenant identifier.
    /// </response>
    [HttpGet("roles/{roleId:int}")]
    [HttpGet("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RoleDetailDto?>>> GetAsync(
        [FromRoute] int? portalId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleDetailDto?> outcome = await _roles
            .GetRoleAsync(scopedPortalId, roleId, cancellationToken)
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
    [HttpPost("roles")]
    [HttpPost("portals/{portalId:int}/roles")]
    [ProducesResponseType(typeof(ApiResponse<RoleDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<RoleDetailDto>>> CreateAsync(
        [FromRoute] int? portalId,
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleDetailDto> outcome = await _roles
            .CreateRoleAsync(scopedPortalId, request, cancellationToken)
            .ConfigureAwait(false);

        // The location header is built from this request's own path plus the new identifier, which is
        // exactly the member address of the collection just posted to, so the address is spelled once.
        return this.Created(outcome, created => created.RoleId);
    }

    /// <summary>Updates a role's writable state.</summary>
    /// <param name="portalId">Identifier of the portal that owns the role.</param>
    /// <param name="roleId">Identifier of the role to update.</param>
    /// <param name="request">
    /// The new state: name, description, role group, visibility and automatic-enrolment switches,
    /// invitation code, icon, and the billing and trial terms.
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
    /// <response code="409">
    /// The submitted name already belongs to a DIFFERENT role in the same portal. Resubmitting a role's
    /// own current name is not a conflict, because the comparison excludes the role being updated.
    /// </response>
    /// <remarks>
    /// MIGRATION - documented behavioural difference, itemised in <c>MIGRATION_NOTES.md</c>. This path
    /// can rename a role, where the legacy edit screen could not: it revealed the name as a read-only
    /// label and disabled its only required-field validator
    /// (<c>EditRoles.ascx.vb:L131-L134</c>), and the terminal stored procedure omits the column from its
    /// assignment list (<c>04.00.04.SqlDataProvider:L454</c>). The library-level member this replaces did
    /// carry the name (<c>RoleController.vb:L254</c>), and the terminal schema declares
    /// <c>UNIQUE (PortalID, RoleName)</c> (<c>03.00.09.SqlDataProvider:L304</c>), so the guard the legacy
    /// editor applied on its add branch alone (<c>:L252-L258</c>, with no equivalent at
    /// <c>:L259-L261</c>) is applied on this path too and its collision is the <c>409</c> above. The
    /// legacy asymmetry was coherent only while the name could not change; making the name writable
    /// without the guard would have made a rename the one way to manufacture a duplicate. Neither the
    /// check nor the status is decided here - the service reports the outcome and the shared translator
    /// maps it.
    /// </remarks>
    [HttpPut("roles/{roleId:int}")]
    [HttpPut("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RoleDetailDto>>> UpdateAsync(
        [FromRoute] int? portalId,
        int roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleDetailDto> outcome = await _roles
            .UpdateRoleAsync(scopedPortalId, roleId, request, cancellationToken)
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
    [HttpDelete("roles/{roleId:int}")]
    [HttpDelete("portals/{portalId:int}/roles/{roleId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        [FromRoute] int? portalId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .DeleteRoleAsync(scopedPortalId, roleId, cancellationToken)
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
    /// <returns>One page of the accounts holding the role, in the wire envelope: an <c>items</c> array of rows and a <c>meta</c> object carrying the total across every page, the page index and the page size. The domain paging type is not serialised.</returns>
    /// <response code="200">
    /// The page, inside an envelope carrying the total across every page. Each record is a MEMBERSHIP -
    /// the account, the role and the period the assignment runs for - rather than an account. An empty
    /// page means the role has no members and is a successful answer.
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
    /// MIGRATION: all five columns the legacy grid bound are on this read path, including the
    /// membership's effective and expiry dates. An earlier revision of this action projected the ACCOUNT
    /// contract and recorded the two dates as "a functional reduction, stated rather than hidden",
    /// reasoning that restoring them would mean inventing a projection no other layer owned. The
    /// reduction is now closed: the membership projection is owned by the application layer, named in the
    /// role DTO folder AAP 0.4.1.1 enumerates, and derived from what the legacy screen rendered as
    /// AAP 0.5.1.2 requires. The earlier note is quoted here rather than deleted so that the change is
    /// legible to anyone who read it.
    /// </para>
    /// <para>
    /// Consequently the record is a membership and not an account: it carries the account's key, login
    /// name and display name, the role's key and name, and the two dates. The remaining account fields -
    /// electronic mail address, approval, lock-out and the rest - are not here, because the legacy grid
    /// rendered none of them and the account endpoints already publish them.
    /// </para>
    /// <para>
    /// MIGRATION: this action binds <c>RoleUserPagedRequest</c> and not the account listing's
    /// <c>UserPagedRequest</c>, and the difference is a capability rather than a formality. Borrowing the
    /// account type resolved <c>UserPagedRequestValidator</c>, which applies the account collection's seven
    /// sortable names, while the service behind this action enforces the role-membership set of ten and
    /// implements all ten. Ordering by <c>CreatedDate</c>, <c>LastLoginDate</c> or <c>IsApproved</c> was
    /// therefore refused at the boundary as an unknown field even though the service would have honoured
    /// it - the endpoint advertised less than it could do. One request type per collection, each closed over
    /// the one set that collection honours, is what keeps the validator's vocabulary and the service's
    /// vocabulary the same vocabulary.
    /// </para>
    /// </remarks>
    [HttpGet("roles/{roleId:int}/users")]
    [HttpGet("portals/{portalId:int}/roles/{roleId:int}/users")]
    [ProducesResponseType(typeof(PagedResponse<RoleMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<RoleMembershipDto>>> ListUsersAsync(
        [FromRoute] int? portalId,
        int roleId,
        [FromQuery] RoleUserPagedRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<PagedResult<RoleMembershipDto>> outcome = await _roles
            .ListRoleUsersAsync(scopedPortalId, roleId, request, cancellationToken)
            .ConfigureAwait(false);

        // Projected onto the wire envelope here rather than returned as the domain page. CompletePage
        // applies PagedResponse<T>.From, so the response carries `items` plus `meta` and the domain
        // paging type never crosses the boundary.
        return this.CompletePage(outcome);
    }

    /// <summary>Lists the roles one account holds in a portal.</summary>
    /// <param name="portalId">Identifier of the portal the account belongs to.</param>
    /// <param name="userId">Identifier of the account whose roles are listed.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The roles the account holds in this portal.</returns>
    /// <response code="200">
    /// The roles, inside the shared success envelope: the sequence is the envelope's payload rather than
    /// the whole body. An empty payload means the account holds none and is a successful answer.
    /// </response>
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
    [HttpGet("users/{userId:int}/roles")]
    [HttpGet("portals/{portalId:int}/users/{userId:int}/roles")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RoleListItemDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleListItemDto>>>> ListForUserAsync(
        [FromRoute] int? portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<RoleListItemDto>> outcome = await _roles
            .ListUserRolesAsync(scopedPortalId, userId, cancellationToken)
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
    /// text. Here a date omitted from the body is <c>null</c> and stays <c>null</c>. What the service does
    /// with an absent date is unchanged in effect: an expiry is derived from the role's billing or trial
    /// terms, using the six-code frequency table cited at the head of this file, and the code <c>O</c>
    /// still yields the far-future <c>9999-12-31</c>, which reaches the wire exactly as stored. No date
    /// arithmetic is performed in this file.
    /// </para>
    /// <para>
    /// MIGRATION: HOW AN ABSENT DATE LEAVES ON THE WAY BACK OUT - this paragraph used to say the opposite,
    /// so the correction is stated plainly. Serialisation is configured once with
    /// <c>JsonIgnoreCondition.WhenWritingNull</c>, so a <c>null</c> date is NOT written as
    /// <c>"effectiveDate": null</c>; the MEMBER IS OMITTED FROM THE OBJECT ENTIRELY. That is the intended
    /// contract - absence is expressed by absence, never by a sentinel, and writing nulls would add bytes
    /// without adding information - but a client must read it as "member missing means no date", not as
    /// "member present with a null value". A client that indexes the property blindly will find it
    /// undefined rather than null, which is the practical difference the withdrawn sentence got wrong.
    /// The members remain nullable on the response contract, so the published schema marks them optional
    /// and agrees with what the serialiser does.
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
    [HttpPost("roles/{roleId:int}/users")]
    [HttpPost("portals/{portalId:int}/roles/{roleId:int}/users")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AssignUserAsync(
        [FromRoute] int? portalId,
        int roleId,
        [FromBody] RoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .AssignUserToRoleAsync(scopedPortalId, roleId, request, cancellationToken)
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
    [HttpDelete("roles/{roleId:int}/users/{userId:int}")]
    [HttpDelete("portals/{portalId:int}/roles/{roleId:int}/users/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveUserAsync(
        [FromRoute] int? portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .RemoveUserFromRoleAsync(scopedPortalId, roleId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
