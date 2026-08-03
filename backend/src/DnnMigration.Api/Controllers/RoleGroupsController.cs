using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The role group resource - the tenant-owned classification a role may be filed under - exposed at
/// <c>/api/v1/role-groups</c>, and additionally at <c>/api/v1/portals/{portalId}/role-groups</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this type does, and the complete list.</strong> It binds a request, delegates to
/// <see cref="IRoleService"/>, and translates the returned outcome into a status code. Nothing else. No
/// name is checked for uniqueness here, no tally of the roles a group classifies is taken, no cache is
/// evicted and no audit record is written. A rule implemented in a controller applies only to callers
/// arriving over HTTP and silently fails to apply to every other caller - a background job, a console
/// tool, a unit test - so every rule lives in the service, which is also the thing the unit tests
/// exercise.
/// </para>
/// <para>
/// <strong>Legacy origin.</strong> One Web Forms editor and one grid collapse into this resource:
/// <c>Website/admin/Security/EditGroups.ascx.vb</c> (the single-group editor, which was simultaneously
/// the add form, the edit form and the delete command) and the group selector plus delete button hosted
/// by <c>Website/admin/Security/Roles.ascx.vb</c> (L108 for the list, L294 for the delete). Both reached
/// the domain through the static <c>RoleController</c> (892 lines, 42 public members), whose role group
/// surface was six declarations - <c>AddRoleGroup</c> (L626), the two <c>DeleteRoleGroup</c> overloads
/// (L779, L794), <c>GetRoleGroup</c> (L811), <c>GetRoleGroups</c> (L825) and <c>UpdateRoleGroup</c>
/// (L838). They collapse into the five actions below, because the two delete overloads differed only in
/// whether the caller had already loaded the group and both addressed the same row.
/// </para>
/// <para>
/// <strong>Why the route nests under the portal.</strong> A role group is unconditionally tenant-owned:
/// <c>RoleGroups.PortalID</c> is declared <c>NOT NULL</c> and the uniqueness constraint on the table is
/// <c>UNIQUE ([PortalID] ASC, [RoleGroupName] ASC)</c> (measured at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider:L49-L62</c>, where the
/// table reaches its terminal shape; it first appears at <c>03.02.03.SqlDataProvider:L16</c> and not, as
/// might be assumed, in the baseline script). Every member of the application contract accordingly takes
/// the portal identifier, so naming the tenant in the path rather than in a query string makes the
/// scoping structural: there is no reachable address that omits it and therefore no request whose tenant
/// has to be guessed. This also matches the two sibling resources that are owned the same way, modules
/// and profile definitions.
/// </para>
/// <para>
/// MIGRATION: the legacy editor decided between inserting and updating by testing a private field
/// against -1 (<c>EditGroups.ascx.vb:L42</c> initialises it, <c>L68</c> branches the load on it,
/// <c>L113</c> branches the save on it and <c>L165</c> branches the cancel on it). That discriminator is
/// deliberately not carried forward: POST creates and PUT on a member address updates, so the method IS
/// the discriminator. No identifier is compared against -1, against 0, or against any lower bound
/// anywhere in this file, and none may be added. The reason is that the value is not free. -1 is
/// simultaneously the legacy absent-integer marker <c>Null.NullInteger</c>
/// (<c>Library/Components/Shared/Null.vb:L41-L45</c>), the "Global Roles" selector value meaning a role
/// belongs to no group (<c>Roles.ascx.vb:L114</c> and <c>EditRoles.ascx.vb:L75</c>), the persisted
/// <c>RoleGroupID</c> such a role carries, and the unauthenticated-role token <c>glbRoleAllUsers</c>
/// (<c>Library/Components/Shared/Globals.vb:L95</c>); -2 is both the "All Roles" selector value
/// (<c>Roles.ascx.vb:L112</c>) and <c>glbRoleSuperUser</c> (<c>Globals.vb:L96</c>). And 0 is not absent
/// either: <c>RoleGroups.RoleGroupID</c> is seeded <c>IDENTITY(0,1)</c>, so the first role group a portal
/// ever creates is numbered 0. A test against any of those three values would misread a real row.
/// </para>
/// <para>
/// MIGRATION: the two selector sentinels are not tunnelled through this API. The legacy drop-down mixed
/// two pseudo-entries - "All Roles" as -2 and "Global Roles" as -1 - in with the real groups it listed
/// (<c>Roles.ascx.vb:L110-L126</c>), so one integer carried both a group identity and a filtering intent
/// and the reader could not tell which it held. The list action below returns real groups only, and the
/// filtering intent belongs to the collection it actually filters, which is the role collection: the
/// "All Roles" intent is expressed there by simply omitting the optional, nullable group filter. Neither
/// -1 nor -2 is accepted or emitted as an identifier by any action in this file, and addressing one
/// yields <c>404</c> naturally, because the pseudo-entries are not resources. That also subsumes the
/// legacy delete guard at <c>Roles.ascx.vb:L293</c>, whose <c>If RoleGroupId &gt; -1</c> test existed
/// solely to stop a user deleting one of those two pseudo-entries.
/// </para>
/// <para>
/// MIGRATION - a gap stated rather than papered over. The "Global Roles" intent, meaning the roles that
/// belong to NO group, has no representation in the current role-listing contract: its group filter is a
/// nullable identifier whose absent state already means "do not filter", so the third state the legacy
/// drop-down offered is not expressible. It is deliberately NOT reintroduced by letting -1 travel through
/// this resource, which is precisely the collision described above - the same integer would once again
/// mean an absent value, a real stored identifier and a filtering intent at the same time. Serving the
/// intent properly would mean adding a named, typed member to the role-listing contract, which is that
/// resource's surface and not this one's, so it is recorded here as a known difference from the legacy
/// screen rather than approximated.
/// </para>
/// <para>
/// MIGRATION: no sentinel is manufactured or erased on this boundary, and the wire form of an absent
/// value is an ABSENT MEMBER rather than a JSON null. Serialisation is configured once for the whole
/// application with a when-writing-null ignore condition, so a null-valued property is omitted from the
/// response body altogether; the transfer object carries stored values through unchanged either way.
/// That matters for the description in particular: the legacy read path funnelled every string through
/// <c>Null.SetNull</c>, whose string sentinel is the empty string rather than null
/// (<c>Null.vb:L71-L75</c>), so a database NULL and an empty description were indistinguishable once
/// loaded. Which of the two now stands for an absent description is settled once, in the mapper that
/// translates between the contract and the persisted model, and this file neither adds a sentinel nor
/// removes one - the only way the two can stay consistent. A client must therefore read a missing
/// member as "no value", and must never read it as -1, 0 or the empty string.
/// </para>
/// <para>
/// MIGRATION: the legacy screens authorised themselves imperatively, calling
/// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> during the page lifecycle and
/// answering a refusal with <c>Response.Redirect(NavigateURL("Access Denied"), True)</c> - a 302 to an
/// HTML page, which no programmatic caller can interpret, and which conflated "you did not say who you
/// are" with "you may not do this". That becomes the declarative class-level policy below, which answers
/// <c>401</c> when no valid credential was presented and <c>403</c> when the credential is valid but does
/// not administer the addressed tenant. The measured legacy role name is <c>Administrators</c> - plural
/// (<c>Library/Components/Portal/PortalController.vb:L1390</c>) - and resolving it belongs to
/// <c>Extensions/AuthenticationExtensions.cs</c>, not to this file.
/// </para>
/// <para>
/// MIGRATION - discovered legacy defect, recorded rather than reproduced. The sibling security screen
/// guarded itself with
/// <c>If (Not (objUser Is Nothing) AndAlso objUser.IsSuperUser) OrElse PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False Then</c>
/// (<c>Website/admin/Security/SecurityRoles.ascx.vb:L318-L324</c>). The <c>OrElse</c> stands where
/// <c>AndAlso</c> was plainly intended, so the condition is true for a super user and that most
/// privileged of accounts was redirected TO the access-denied page. The declarative policy used here does
/// not reproduce the inversion, and it is recorded rather than silently absorbed. Note also that the
/// policy is the whole of the decision: <c>ICurrentUser.IsSuperUser</c> is informational and is never
/// consulted as a gate in a controller, which is why it does not appear in this file.
/// </para>
/// <para>
/// MIGRATION: the policy named below is the portal-administrator policy and not one of the module or tab
/// policies, and the choice is load-bearing rather than stylistic. Those policies resolve a module or tab
/// identifier out of route data; this route carries neither, so one of them would find nothing to
/// evaluate and fail closed, answering <c>403</c> to every request including a legitimate one. The policy
/// catalogue is closed, and an unregistered policy name fails at request time rather than at compile
/// time, so the name is taken from the <see cref="PolicyNames"/> constants and never spelled as a
/// literal.
/// </para>
/// <para>
/// A distinction worth stating plainly, because it is easy to misread: the tenant the policy evaluates is
/// the one the REQUEST resolves to, from the host header via the portal alias table, and it is not the
/// <c>portalId</c> in the path. The routed identifier is the subject the application contract scopes its
/// work to; the resolved tenant is who the caller is administering. This file neither performs nor
/// duplicates either resolution - the alias lookup belongs to the resolution middleware, which is the one
/// place in the project permitted to reach for the ambient HTTP context, and the scoping belongs to the
/// service. The practical consequence for a caller is that a request must arrive on a host that has an
/// alias row, or the tenant cannot be resolved and the policy denies for want of a portal rather than for
/// want of a role.
/// </para>
/// <para>
/// MIGRATION: cache invalidation has moved out of the presentation layer. Both legacy call sites
/// followed a write by evicting a bare literal key - <c>DataCache.RemoveCache("GetRoles")</c> at
/// <c>Roles.ascx.vb:L263</c> and again at <c>:L294</c> - which meant a caller that reached the domain by
/// any other path left the cache stale. Invalidation now belongs to the service that performs the write,
/// behind the shared cache abstraction, so it happens exactly once per write and for every caller. This
/// controller injects no cache and evicts nothing.
/// </para>
/// <para>
/// MIGRATION - business audit parity is NOT yet achieved, and that is recorded rather than implied away.
/// The legacy grid wrote event-log entries keyed <c>ROLE_CREATED</c>, <c>ROLE_UPDATED</c> and
/// <c>ROLE_DELETED</c> from inside its button handlers. No equivalent business-event record is written
/// anywhere in the target today: <c>RoleService</c> emits no audit log, and the request-logging middleware
/// says explicitly that it is not a substitute - it records that a request happened, with its correlation
/// identifier, not that a role group was created and by whom. The correct home for the equivalent record
/// is the application service that performs the write, so that every caller produces it and not only an
/// HTTP one; no logger is injected here for that purpose, and adding one would reintroduce the transport
/// coupling the legacy screens had.
/// </para>
/// <para>
/// <strong>What this resource deliberately does not expose.</strong> There is no action that lists or
/// changes the roles filed under a group: a role's group membership is a field of the role, changed
/// through the role resource, exactly as the legacy application changed it on the role editor rather than
/// on the group editor. There is no membership sub-collection, because a group confers no permission of
/// its own; there is no user-to-role assignment action, which belongs to the role resource; and there is
/// no cache-invalidation or bulk action of any kind.
/// </para>
/// <para>
/// <strong>Two addresses, one implementation.</strong> The specified address for this resource is the flat
/// <c>/api/v1/role-groups</c>, and it is declared first below because it is the canonical one. The nested
/// <c>/api/v1/portals/{portalId}/role-groups</c> is retained alongside it, and both are served by the same
/// five actions - there is no second controller, no duplicated body and no forwarding action. The two forms
/// differ in exactly one respect, which is where the tenant comes from.
/// </para>
/// <para>
/// On the FLAT form the tenant is the one the request resolved to, read from
/// <see cref="IPortalContextHolder"/>. A caller cannot name another tenant on that form because there is no
/// parameter through which to name one, so the isolation is structural rather than checked. On the NESTED
/// form the routed identifier is reconciled against the resolved tenant by the portal-administrator policy
/// before this file is entered, so a caller that is not a host account cannot address another tenant there
/// either. The identifier is bound <c>[FromRoute]</c> and never from the query string, and that is what
/// keeps the reconciliation unavoidable: a query-bound tenant would travel on the flat form too, where the
/// policy has no route value to reconcile it against, and would reopen the very hole the nested form's
/// reconciliation closes.
/// </para>
/// <para>
/// The nested form is also what makes a host account able to administer a NAMED tenant at all. A request
/// resolves to whichever portal's alias it arrived on and an HTTP client cannot forge another tenant's host
/// name, so removing the routed form would leave cross-tenant administration with no address. It is
/// therefore retained rather than replaced.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/role-groups")]
[Route("api/v{version:apiVersion}/portals/{portalId:int}/role-groups")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class RoleGroupsController : ControllerBase
{
    /// <summary>The application contract that owns role groups.</summary>
    /// <remarks>
    /// Role groups live on the role contract rather than on one of their own, and deliberately so: every
    /// rule about a group is a rule about the roles it classifies - the per-portal name uniqueness the
    /// table enforces, and the refusal to remove a group while it still classifies a role. There is no
    /// <c>IRoleGroupService</c> to inject. One dependency, and it is an application-layer interface:
    /// there is no repository, no persistence context, no cache and no clock here. What keeps them out is
    /// not the project reference graph - this project references the Infrastructure assembly, because it
    /// has to compose the container at start-up - but two other things: the persistence context and the
    /// repositories are declared internal to that assembly and so are not nameable here at all, and this
    /// controller depends only on the application abstraction. A type that IS visible and reachable, such
    /// as a cache or a clock, is kept out by that discipline rather than by the compiler, so injecting one
    /// would be a review finding rather than a build failure.
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
    /// Needed only by the flat address, which carries no tenant identifier and therefore has to read the
    /// one the alias-resolution middleware already resolved. It is an abstraction over that resolution
    /// rather than a reach for the ambient HTTP context: this file still performs no alias lookup and
    /// still touches no request feature bag.
    /// </remarks>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="RoleGroupsController"/> class.</summary>
    /// <param name="roles">The role service, which owns role groups.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host, which is the
    /// tenant the flat address acts on.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Declarative validation is applied by the globally registered validation filter, which resolves a
    /// validator for each action argument's type and runs it before any action body, so no validator is
    /// injected here and no action inspects model state. That placement is deliberate: a per-action opt-in
    /// has a silent failure mode, because an endpoint whose author forgot the call looks exactly like one
    /// with no rules declared against it.
    /// <para>
    /// MIGRATION: this remark previously stated that the filter found nothing to run for this resource,
    /// because no validator was registered for the role-group contract. That is no longer true, and the
    /// reason it changed matters. Both write verbs used to bind the <c>RoleGroupDto</c> response
    /// projection; they now bind <c>CreateRoleGroupRequest</c> and <c>UpdateRoleGroupRequest</c>, and the
    /// assembly scan registers a validator for each, so the filter resolves one on every write and a
    /// field failure is a declarative <c>400</c> rather than a service refusal. The service still
    /// re-asserts the name rules, because it is reachable by callers that do not arrive over HTTP.
    /// </para>
    /// </remarks>
    public RoleGroupsController(IRoleService roles, IPortalContextHolder portalContext)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>
    /// Chooses the tenant an action acts on: the routed identifier when the nested address was used, and
    /// otherwise the tenant the request resolved to.
    /// </summary>
    /// <param name="routedPortalId">
    /// The identifier bound from the route, or <see langword="null"/> when the flat address was used.
    /// </param>
    /// <returns>
    /// The tenant identifier, or <see langword="null"/> when the flat address was used and the request
    /// resolved to no tenant at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The routed value wins when it is present, because on that address it IS the subject of the request
    /// and the policy has already reconciled it against the resolved tenant. It is forwarded exactly as
    /// bound: no lower bound is imposed and no value is treated as "absent", because the portal table is
    /// <c>IDENTITY (-1, 1)</c> and -1 is therefore a real portal rather than the legacy missing-integer
    /// sentinel.
    /// </para>
    /// <para>
    /// The holder is read rather than a placeholder returned, and it throws instead of yielding one, so
    /// resolution is tested first, and the null answer here is a precondition rather than a state a caller
    /// can steer into.
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

    /// <summary>Lists every role group a portal defines.</summary>
    /// <param name="portalId">
    /// Identifier of the portal whose role groups are listed, bound from the route on the nested address
    /// and absent on the flat one, where the tenant the request resolved to is used instead. Forwarded
    /// exactly as bound: no lower bound is imposed, because <c>Portals.PortalID</c> is
    /// <c>IDENTITY (-1, 1)</c>, so -1 is the seed and first generated value while the shipped default
    /// portal row carries an explicit 0. Both are real portal keys, and neither means "absent".
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal's role groups.</returns>
    /// <response code="200">
    /// The role groups, inside the shared success envelope: the sequence is the envelope's payload rather
    /// than the whole body. An empty payload is a legitimate answer and means the portal defines none; it
    /// is never reported as a failure. The legacy grid distinguished the two - it hid its group row
    /// entirely when the list came back empty (<c>Roles.ascx.vb:L128-L131</c>) - and that presentation
    /// choice is the client's to make from an empty payload.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: deliberately unpaged, and no page, size or sort parameter is accepted. The legacy read
    /// it replaces returned an untyped <c>ArrayList</c> of every group in the portal with no pager at all
    /// (<c>RoleController.vb:L825</c>, consumed at <c>Roles.ascx.vb:L108</c> and
    /// <c>EditRoles.ascx.vb:L73</c>), both consumers bound the whole answer to a drop-down rather than to
    /// a grid, and a portal defines groups in the tens. Introducing paging would add a contract the
    /// legacy application never had, and the untyped collection becomes a typed read-only sequence rather
    /// than acquiring a pager on the way.
    /// </para>
    /// <para>
    /// The <c>404</c> above reports an unknown PORTAL, not an empty result. Tenant scoping is part of the
    /// question the contract answers, which is what makes asking about a portal that does not exist
    /// different from asking about a portal that has no groups.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RoleGroupDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleGroupDto>>>> ListAsync(
        [FromRoute] int? portalId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<RoleGroupDto>> outcome = await _roles
            .ListRoleGroupsAsync(scopedPortalId, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never has
        // its value touched - reading the value of a failed outcome throws by design - and any failure
        // code is mapped through the single table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one role group.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role group, bound from the route on the nested address and
    /// absent on the flat one, where the tenant the request resolved to is used instead.
    /// </param>
    /// <param name="roleGroupId">
    /// Identifier of the role group to read. Forwarded exactly as bound and never compared against a
    /// sentinel; see the note at the head of this file on why no range constraint appears here. The
    /// column is seeded <c>IDENTITY(0,1)</c>, so 0 addresses the portal's first role group.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The role group.</returns>
    /// <response code="200">The role group.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// The portal owns no role group with that identifier. Tenant scoping is part of the question, so a
    /// group that exists in another portal is reported as absent here rather than returned.
    /// </response>
    /// <remarks>
    /// <para>
    /// The contract reports an unknown group as a SUCCESSFUL outcome carrying no value rather than as a
    /// failure, because being asked for something that is not there is not an error on the server's part.
    /// The shared translator reads that absent value as <c>404 Not Found</c>, which is the whole of how
    /// this action produces that status - there is no null test written here, and none is needed.
    /// </para>
    /// <para>
    /// MIGRATION: replaces <c>RoleController.vb:L811</c>. The legacy editor treated an empty answer as an
    /// attempted security violation - "attempt to access item not related to this Module" - and answered
    /// it with a redirect to another page (<c>EditGroups.ascx.vb:L80-L82</c>). Answering <c>404</c>
    /// instead is both truthful and no more revealing: the cross-portal case and the does-not-exist case
    /// are reported identically, so the response discloses nothing about groups in other tenants.
    /// </para>
    /// </remarks>
    [HttpGet("{roleGroupId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RoleGroupDto?>>> GetAsync(
        [FromRoute] int? portalId,
        int roleGroupId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleGroupDto?> outcome = await _roles
            .GetRoleGroupAsync(scopedPortalId, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a role group in a portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that will own the role group, bound from the route on the nested address
    /// and absent on the flat one, where the tenant the request resolved to is used instead.
    /// </param>
    /// <param name="request">
    /// The role group to create: its name and an optional description. Those are the only two values the
    /// legacy editor's inputs supplied (<c>EditGroups.ascx:L11</c> and <c>:L17</c>) and the only two this
    /// path honours, so they are the only two the contract declares. The group's identifier is assigned by
    /// the store - which is precisely why the method rather than a sentinel distinguishes this from an
    /// update - and the owning portal arrives in the route, so neither is expressible in the body.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created role group, addressed by the location header.</returns>
    /// <response code="201">
    /// The role group as persisted, including the identifier the database assigned, with its address in
    /// the location header.
    /// </response>
    /// <response code="400">
    /// The body was absent or malformed, or a rule refused it - a group name is required and may not
    /// exceed 50 characters, matching the required-field validator and the <c>maxlength="50"</c> on the
    /// legacy editor's name box (<c>EditGroups.ascx:L11-L12</c>), and a description may not exceed 1000
    /// (<c>:L17</c>). Those rules are declared by <c>CreateRoleGroupRequestValidator</c>, which the
    /// globally registered validation filter resolves and applies before this action body runs, so the
    /// answer is an RFC 7807 validation document naming each offending member. A model-binding failure
    /// takes the same shape through the automatic model-state check. The application service re-asserts
    /// the name rules for callers that do not arrive over HTTP.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">No portal bears that identifier.</response>
    /// <response code="409">
    /// The portal already has a role group of that name. Names are unique within a portal, which the
    /// schema itself enforces through <c>UNIQUE ([PortalID] ASC, [RoleGroupName] ASC)</c>, so this is a
    /// collision with existing state that the caller can correct by choosing another name.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: this action is the whole of the legacy add path. The editor chose to add by testing its
    /// identifier field against -1 (<c>EditGroups.ascx.vb:L113</c>); here POST expresses the intent, and
    /// the contract's duplicate-name code expresses the collision, which the shared translator answers
    /// with <c>409</c>. Neither the intent nor the failure travels inside an integer any more.
    /// </para>
    /// <para>
    /// MIGRATION: the duplicate is reported as a reason code rather than discovered by catching an
    /// exception. The legacy screen wrapped the call in a bare <c>Catch</c> and mapped ANY thrown error
    /// onto its localised message, "A role group with the same name already exists. The new group was not
    /// added." (<c>EditGroups.ascx.vb:L114-L119</c> with the wording from
    /// <c>App_LocalResources/EditGroups.ascx.resx</c>). That conflated a name collision with every other
    /// possible failure, including a database being unreachable. The two are now distinct: a collision is
    /// <c>409</c> carrying the collision's own problem type, and an unanticipated failure reaches the
    /// global exception handler instead.
    /// </para>
    /// <para>
    /// MIGRATION: the location header is derived from this request's own path plus the assigned
    /// identifier rather than from a named-route lookup, so the address is spelled in exactly one place.
    /// That is the shared creation translator's documented behaviour and it yields
    /// <c>/api/v1/portals/{portalId}/role-groups/{roleGroupId}</c> for this endpoint - the address of the
    /// by-identifier read above. The legacy screen had no equivalent: it answered a successful add with a
    /// redirect back to the grid (<c>EditGroups.ascx.vb:L120</c>), discarding the new identifier.
    /// </para>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<RoleGroupDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RoleGroupDto>>> CreateAsync(
        [FromRoute] int? portalId,
        [FromBody] CreateRoleGroupRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<RoleGroupDto> outcome = await _roles
            .CreateRoleGroupAsync(scopedPortalId, request, cancellationToken)
            .ConfigureAwait(false);

        // The identifier is read from the persisted representation rather than from the submitted body,
        // because the store assigns it, and it is read only on the success path: the translator checks
        // the outcome first.
        return this.Created(outcome, created => created.RoleGroupId);
    }

    /// <summary>Updates an existing role group.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role group, bound from the route on the nested address and
    /// absent on the flat one, where the tenant the request resolved to is used instead.
    /// </param>
    /// <param name="roleGroupId">
    /// Identifier of the role group to update. This is the authoritative subject of the request.
    /// </param>
    /// <param name="request">
    /// The replacement state for the role group: its name and an optional description. Omitting the
    /// description clears it, because this is a replacement rather than a partial edit. Neither the
    /// group's identifier nor its portal is expressible in the body - both arrive in the route - so there
    /// is nothing here for the two to disagree about.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The role group as persisted.</returns>
    /// <response code="200">The role group as persisted.</response>
    /// <response code="400">The body failed validation, or a value could not be bound.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">The portal owns no role group with that identifier.</response>
    /// <response code="409">
    /// A different role group in the same portal already has the submitted name.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>RoleController.vb:L838</c>, which returned nothing at all and so left a
    /// caller unable to tell an applied update from a discarded one - the legacy screen simply redirected
    /// afterwards and assumed the best (<c>EditGroups.ascx.vb:L122-L123</c>). The persisted state is
    /// returned here, so the caller sees what was actually stored.
    /// </para>
    /// <para>
    /// MIGRATION - behavioural difference, deliberately introduced. The legacy save wrapped ONLY its add
    /// branch in a <c>Try</c>; the update branch at <c>EditGroups.ascx.vb:L122</c> had no duplicate
    /// handling whatsoever, so renaming a group onto an existing name violated the table's unique
    /// constraint and surfaced as an unhandled provider error through the module's load-exception path.
    /// The collision is now an expected outcome on this path too, reported as the same code the create
    /// action reports and answered with the same <c>409</c>.
    /// </para>
    /// <para>
    /// Re-classifying a group moves no role between groups. A role's group membership is a field of the
    /// role, changed through the role resource, which is why no action here touches the roles a group
    /// classifies.
    /// </para>
    /// </remarks>
    [HttpPut("{roleGroupId:int}")]
    [ProducesResponseType(typeof(ApiResponse<RoleGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RoleGroupDto>>> UpdateAsync(
        [FromRoute] int? portalId,
        int roleGroupId,
        [FromBody] UpdateRoleGroupRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // The subject travels as a route value and the state travels in the body, and the two cannot
        // disagree: the request contract declares no identifier of its own, so there is no reconciliation
        // for any layer to perform. Nothing here rewrites a member of the body.
        Result<RoleGroupDto> outcome = await _roles
            .UpdateRoleGroupAsync(scopedPortalId, roleGroupId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a role group from a portal.</summary>
    /// <param name="portalId">
    /// Identifier of the portal that owns the role group, bound from the route on the nested address and
    /// absent on the flat one, where the tenant the request resolved to is used instead.
    /// </param>
    /// <param name="roleGroupId">Identifier of the role group to remove.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> once the role group has been removed.</returns>
    /// <response code="204">The role group has been removed.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">The portal owns no role group with that identifier.</response>
    /// <response code="409">
    /// The role group still classifies at least one role, so removing it would leave those roles pointing
    /// at a group that no longer exists. The request is well formed and the state of the resource is what
    /// refuses it, which is why this is a conflict rather than a bad request. Releasing the roles first
    /// makes the same request succeed, so the refusal is a guard and not a permanent obstacle.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: the in-use rule is enforced behind this action, not in front of it. The legacy editor
    /// counted the group's roles and merely HID its delete button when the count was positive -
    /// <c>roleCount = objRoles.GetRolesByGroup(PortalId, RoleGroupID).Count</c> followed by
    /// <c>cmdDelete.Visible = False</c> (<c>EditGroups.ascx.vb:L75-L79</c>). Hiding a control is not
    /// enforcement: any caller that issued the post-back directly walked straight through it. The rule now
    /// lives in the operation and is reported as a reason code, which closes that gap. No tally is taken
    /// in this file, and no separate "can this be deleted" question is exposed for a client to ask and
    /// then ignore - a client that wants to hide the affordance uses the permission directive, exactly as
    /// the legacy screen used the count for button visibility.
    /// </para>
    /// <para>
    /// MIGRATION: the two legacy overloads collapse into one action. <c>RoleController.vb:L779</c> took a
    /// portal and an identifier and immediately delegated to <c>:L794</c>, which took the whole object -
    /// so the object-taking overload only ever received a group the first had just fetched. One address
    /// with two identifiers expresses the same thing without the round trip.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy delete was reachable from two places - the editor's delete button
    /// (<c>EditGroups.ascx.vb:L142-L150</c>) and the grid's delete command
    /// (<c>Roles.ascx.vb:L290-L299</c>) - both calling the same underlying member. One endpoint serves
    /// both, and the client's confirmation dialog replaces the post-back confirmation the legacy editor
    /// attached to its button. The grid's <c>If RoleGroupId &gt; -1</c> pre-test is not reproduced: as
    /// noted at the head of this file, it guarded against deleting the two selector pseudo-entries, which
    /// are not resources here, so addressing one is simply a <c>404</c>.
    /// </para>
    /// <para>
    /// The valueless overload of the shared translator answers <c>204</c> on success, which is the
    /// documented contract for a removal: the resource is gone, so there is nothing to return in its
    /// place.
    /// </para>
    /// </remarks>
    [HttpDelete("{roleGroupId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteAsync(
        [FromRoute] int? portalId,
        int roleGroupId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId(portalId) is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _roles
            .DeleteRoleGroupAsync(scopedPortalId, roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
