using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The user resource: identity, membership, profile and credentials.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces nine admin screens - <c>ManageUsers</c>, <c>Users</c>, <c>User</c>, <c>Profile</c>,
/// <c>ViewProfile</c>, <c>Password</c>, <c>Membership</c>, <c>UserSettings</c> and <c>MemberServices</c> under
/// <c>Website/admin/Users/</c>. Nine pages collapse into one resource with sub-resources because they were
/// nine views of the same aggregate, not nine things.
/// </para>
/// <para>
/// MIGRATION: every action names the portal in its route. The legacy screens took the tenant from ambient
/// per-request state, and the application contract this file delegates to instead accepts the portal as its
/// first argument on every member without exception. Reading it from the route rather than from the request
/// context is what keeps that contract honest here: this controller injects no context accessor and no
/// portal context, so the tenant an action operates on is the tenant its address states, and the policy
/// handler verifies the caller against that same value.
/// </para>
/// <para>
/// MIGRATION: paging is ZERO-BASED and search is a STARTS-WITH match, both preserved from the legacy
/// listing rather than modernised. <c>Website/admin/Users/Users.ascx.vb</c> passed <c>CurrentPage - 1</c> as
/// the page index into all four of its readers (<c>:L265</c>, <c>:L269</c>, <c>:L271</c>, <c>:L274</c>) and
/// appended a single trailing wildcard to the search text - <c>SearchText + "%"</c> - so the match was
/// anchored at the start of the value. The domain paging envelope documents the same zero-based base and
/// cites the same stored-procedure arithmetic, so the two ends of this round trip agree and nothing is
/// converted anywhere. Widening the match to a contains-match would silently return rows the legacy screen
/// did not, which is the behavioural equivalence the migration is held to.
/// </para>
/// <para>
/// MIGRATION: the page size is not a constant and is not defaulted here. The legacy screen read it per
/// portal from the <c>Records_PerPage</c> setting (<c>Users.ascx.vb:L114-L119</c>) and suppressed its pager
/// when the page already held every row (<c>:L279</c>). Both remain caller and client concerns: this action
/// forwards whatever page the request asks for and returns the grand total beside the rows, which is enough
/// for a client to make the same decision without this file holding a display rule.
/// </para>
/// <para>
/// MIGRATION: no identifier on this boundary is compared against a magic number, and no lower bound is
/// placed on <c>userId</c> or <c>portalId</c>. The legacy absent-integer marker is -1
/// (<c>Library/Components/Shared/Null.vb:L41-L45</c>) and that value is heavily overloaded: the portal table
/// is declared <c>IDENTITY (-1, 1)</c>
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>) so -1 is a real
/// portal key, and -1 is simultaneously the all-users role token <c>glbRoleAllUsers</c>
/// (<c>Library/Components/Shared/Globals.vb:L95</c>). The account table happens to be declared
/// <c>IDENTITY (1, 1)</c> (<c>:L98</c>), so no natural account key is 0 - but that is a fact about the
/// current schema and NOT a licence to treat a low value as meaning "absent". Absence is expressed as a
/// nullable value, never as a number.
/// </para>
/// <para>
/// MIGRATION: no sentinel is manufactured or erased on this boundary. The legacy absent-string marker is
/// the EMPTY STRING rather than null (<c>Null.vb:L71-L75</c>), and the account quota carries the sharpest
/// case of all: <c>PortalSettings.UserQuota = 0</c> means UNLIMITED, not "no accounts permitted"
/// (<c>Website/admin/Users/ManageUsers.ascx.vb:L367</c>). This file therefore reshapes no value on the way
/// out - the transfer objects carry stored values through unchanged, and serialisation is configured once
/// for the whole application so that a preserved sentinel travels as the VALUE it is. An empty string is
/// not turned into null, -1 is not turned into null, and a zero quota is not turned into anything.
/// </para>
/// <para>
/// <strong>Nothing here touches a credential.</strong> The password endpoints forward a request object and
/// return a status; no hash, no salt and no password ever appears in a response, a log line or a failure
/// message produced by this file.
/// </para>
/// <para>
/// MIGRATION: credential RETRIEVAL is deliberately not carried forward, and this is the one place the
/// migration knowingly removes a capability. <c>Website/release.config:L239</c> registered the membership
/// provider with <c>enablePasswordRetrieval="true"</c> over <c>:L245</c> <c>passwordFormat="Encrypted"</c> -
/// a reversible store - and the key that reversed it was committed to source control in plain sight at
/// <c>:L89-L93</c>. The legacy read path (<c>Library/Components/Users/UserController.vb:L433</c>, mailed out
/// by <c>Website/admin/Security/SendPassword.ascx.vb</c>) is reference material only. There is no retrieval
/// address on this resource under any spelling: a RESET is offered, a retrieval is not. Re-hashing a legacy
/// credential on first successful sign-in belongs to the application layer and appears nowhere here.
/// </para>
/// <para>
/// MIGRATION: the legacy password POLICY is preserved verbatim, and deliberately not tightened.
/// <c>release.config:L241-L244</c> required no recovery question and answer, a minimum length of seven, zero
/// non-alphanumeric characters, and did not require a unique address. Hardening any of those during a
/// migration would lock out accounts that already satisfy the old rule, so the policy is carried across
/// unchanged and enforced declaratively outside this file. This controller reads no configuration and
/// applies no rule of its own; the recovery question and answer, which the legacy screen showed only when
/// the provider demanded it (<c>Website/admin/Users/User.ascx.vb:L168</c>), is likewise an options-bound
/// service concern.
/// </para>
/// <para>
/// MIGRATION: the legacy credential outcome enumeration becomes a set of failure CODES, not a status field.
/// <c>PasswordUpdateStatus</c> carried eight members - success, mismatch, invalid, missing, not-different,
/// reset-failed, invalid-answer and invalid-question - and the screens turned them into panel text
/// (<c>Website/admin/Users/Password.ascx.vb:L249</c>, <c>:L300</c>, <c>:L338</c>). It is not a domain
/// enumeration in the target, so each member travels as a stable code on a failed outcome and one shared
/// translator decides the status: a replacement that equals the stored credential is a CONFLICT, and every
/// other refusal is a bad request. Success carries no code at all.
/// </para>
/// <para>
/// MIGRATION: the legacy creation outcome enumeration is preserved exactly, including its two traps.
/// <c>UserCreateStatus.Success</c> is 13 and NOT 0, and <c>AddUser</c> is 0 and is not a failure at all - it
/// is the "no error yet" marker the legacy validation ladder started from and tested against
/// (<c>User.ascx.vb:L149</c>, then mismatch <c>:L154</c>, invalid <c>:L157</c>, invalid question
/// <c>:L172</c>, invalid answer <c>:L179</c>, and finally <c>:L185</c> <c>If createStatus &lt;&gt;
/// UserCreateStatus.AddUser</c>). Neither value is interpreted in this file: creation reports its outcome as
/// a code, so there is no numeric comparison here to get wrong.
/// </para>
/// <para>
/// MIGRATION: refusal text is published exactly as the application layer wrote it. The legacy screen
/// prefixed the creation message with a literal <c>"&lt;br/&gt;"</c> before assigning it to its validator
/// (<c>User.ascx.vb:L187</c>); the shared problem-details factory passes authored message text through
/// unmutated, so this file neither strips, normalises nor re-wraps a message, and no action assembles one.
/// </para>
/// <para>
/// The state-changing membership operations - unlock, approval, require-password-change - are separate
/// endpoints rather than fields on the update request. That mirrors the legacy screens, where each was its
/// own explicit administrative act with its own audit consequence, and it keeps an update of a display name
/// from silently unlocking an account.
/// </para>
/// <para>
/// MIGRATION: those three addresses carry the four legacy membership commands, and they are kept strictly
/// distinct from the portal-level settings pair below. <c>Website/admin/Users/Membership.ascx.vb</c>
/// authorised by setting approval true (<c>:L199-L202</c>), unauthorised by setting it false
/// (<c>:L243-L246</c>), forced a change at next sign-in by setting the update-password flag
/// (<c>:L221-L224</c>), and cleared a lockout through a member returning <c>Boolean</c> (<c>:L262</c>).
/// Authorise and unauthorise become ONE address taking the desired state, because the service reports
/// setting a state the account already holds as a conflict and that answer is only meaningful if the caller
/// said which state they meant. Every one of the four returned <c>Boolean</c> or nothing; each now returns
/// an outcome carrying a code, so a refusal explains itself instead of being indistinguishable from a
/// failure to find the account.
/// </para>
/// <para>
/// MIGRATION: the membership-settings pair is PORTAL-level and not a property of any account. The legacy
/// screen read it through <c>UserController.GetUserSettings(UserPortalID)</c>, which returned an untyped
/// <c>Hashtable</c> (<c>Library/Components/Users/UserController.vb:L656</c>) that the screen then walked
/// with a dictionary enumerator, splitting keys on an underscore to recover their grouping
/// (<c>Website/admin/Users/UserSettings.ascx.vb:L105-L115</c>). It becomes one typed transfer object, so
/// the settings a tenant has are a compile-time fact rather than whatever keys happen to be in the table.
/// Its address is therefore keyed by the portal alone, with no account identifier anywhere in it.
/// </para>
/// <para>
/// MIGRATION: the account quota and the super-user rules are enforced beneath this file and surface as
/// refusals. The legacy quota test was <c>PortalSettings.Users &lt; PortalSettings.UserQuota Or
/// UserInfo.IsSuperUser Or PortalSettings.UserQuota = 0</c> (<c>ManageUsers.ascx.vb:L367</c>) - note the
/// third arm, which is what makes a quota of zero UNLIMITED - and the super-user rules were imperative page
/// checks that ended in a redirect to an access-denied view (<c>:L206</c> gating the host add path,
/// <c>:L220</c> refusing a non-super-user who addressed a super-user account, <c>:L309</c> the redirect
/// itself). No arithmetic and no membership test is performed in this file: the service reports a code and
/// the shared translator answers <c>403</c>, which replaces the redirect. In particular the caller's
/// super-user flag is never consulted here to shortcut a decision.
/// </para>
/// <para>
/// MIGRATION: a profile property's stored value crosses this boundary unchanged. The legacy profile update
/// handed a whole property-definition collection back to a controller
/// (<c>Website/admin/Users/Profile.ascx.vb:L226</c>), and a definition's data type resolved through the
/// list subsystem, which is out of scope. So no data type is resolved, no closed set is validated and no
/// enumeration is invented here; the definitions themselves are a separate resource that a client reads to
/// render the form.
/// </para>
/// <para>
/// MIGRATION: three legacy affordances reachable from these screens deliberately have NO address on this
/// resource, and their absence is a decision rather than an omission. Bulk
/// removal of unauthorised accounts (<c>Users.ascx.vb:L328</c>), the unauthorised and online listings
/// (<c>:L259</c>, <c>:L262</c>) with their <c>"None"</c> filter sentinel (<c>:L266</c>), the bulk mail
/// screen, and the scheduled purge of the online table are all outside the agreed surface; the scheduler
/// itself is excluded, so no background worker is introduced to replace the purge.
/// </para>
/// <para>
/// MIGRATION: SELF-SERVICE ROLE SUBSCRIPTION DOES HAVE AN ADDRESS HERE, and an earlier revision of this
/// file was wrong to say it did not. It recorded that "role subscription and cancellation -
/// <c>MemberServices.ascx.vb:L97</c> and <c>:L106</c> - belong to the role resource, which already accepts
/// and removes an account's membership, and duplicating them here would give one rule two addresses". Two
/// things defeat that reasoning. The role resource is gated on tenant administration at the CLASS level, and
/// authorisation attributes combine rather than override, so no action there can ever admit an account
/// holder acting on itself - the affordance the legacy screen existed to provide was therefore
/// unreachable rather than relocated. And three of the five operations are not membership writes at all:
/// the services catalogue with its three per-row predicates, the free-trial gate, and the redemption of a
/// role's invitation code have no counterpart on that resource. The two that ARE membership writes still
/// have exactly one implementation, because the account service delegates them to the role service rather
/// than reaching persistence itself, so the expiry derivation and the protected-assignment refusals are not
/// duplicated. AAP 0.5.1.4 lists <c>MemberServices.ascx.vb</c> among the nine screens this controller
/// replaces, which is where the five routes below belong.
/// </para>
/// <para>
/// MIGRATION: caching and audit are not presentation concerns and appear nowhere in this file. The legacy
/// account controller was the second-heaviest cache consumer in the codebase and wrote its own audit entries
/// inline; both now sit in the application layer, cache invalidation behind a service that keeps the legacy
/// key names, and audit as structured events. This controller injects neither, and it writes no log line of
/// its own - which is also what keeps a credential out of the logs.
/// </para>
/// <para>
/// <strong>EVERY ACTION CARRIES ITS OWN POLICY, and the class-level attribute is authentication alone rather
/// than the authorisation.</strong> An earlier revision relied on that class attribute for every
/// route, which meant any authenticated caller - a plain member of any tenant - could enumerate a tenant's
/// accounts and their personal data, create, update and delete accounts, rewrite profiles and membership
/// settings, approve, unapprove and unlock accounts, and invoke administrative credential reset against
/// anybody. Authentication is not authorisation, and on this resource the gap between them is the whole
/// attack surface.
/// </para>
/// <para>
/// The class attribute states authentication ONLY and cannot be strengthened into the tenant policy, which
/// is a consequence of the framework rather than a preference: authorisation attributes COMBINE rather than
/// override, so a class-level administration policy would be ANDed onto the self-service routes and would
/// make an account unable to reach its own record or change its own credential. The risk that an action
/// added later declares no policy and silently inherits mere authentication is closed by review of this
/// file's own inventory below, not by an attribute that cannot express the split.
/// </para>
/// <para>
/// <strong>The nineteen routes split into THREE kinds, and the split is the security boundary.</strong>
/// The enumeration below is exhaustive and is stated per route so it can be checked against the attributes
/// rather than trusted.
/// </para>
/// <para>
/// <strong>Tenant administration</strong>, ten routes, requiring administration of the tenant resolved for
/// the request: list the accounts; create one; update one; delete one; reset a credential; clear a lockout;
/// set approval; require a change at next sign-in; and read and write the tenant's membership settings. The
/// flat resource exposes no portal identifier a caller can substitute, so another tenant is reached only
/// through that tenant's own host alias and credentials.
/// </para>
/// <para>
/// <strong>Account access</strong>, three routes, admitting either the account the route names or an
/// administrator of that account's portal: read one account, and read and write its profile. Gating these on
/// administration would leave an ordinary member unable to see or maintain their own record, which the
/// legacy screens plainly allowed - the same nine screens served both an administrator managing somebody
/// else and an account managing itself.
/// </para>
/// <para>
/// <strong>Account ownership alone</strong>, six routes, admitting nobody but the account the route names:
/// changing a credential, and the five member-services routes.
/// </para>
/// <para>
/// For the credential change an administrator is deliberately NOT admitted, and that is not an oversight -
/// a change presents the current credential, which only its holder can present. An administrator who must
/// intervene uses the separate reset route, which is tenant administration and a distinct, reviewable act.
/// Widening this one policy to admit an administrator would collapse the two operations back into one whose
/// effect depended on which fields a body happened to carry, which is exactly the account-takeover shape
/// this split closes. The legacy application drew the same line for the same reason:
/// <c>Website/admin/Users/Password.ascx.vb:L138-L144</c> hid the change panel from an administrator whenever
/// the provider offered no retrieval, with the comment that only the user can change their own password and
/// an administrator must reset.
/// </para>
/// <para>
/// The five member-services routes are here for a MEASURED reason rather than by analogy, and admitting an
/// administrator to them would be a functional ADDITION rather than a convenience. The legacy panel was
/// hosted inside the account container (<c>Website/admin/Users/manageusers.ascx:L77</c>) and the container
/// assigned it the subject account (<c>ManageUsers.ascx.vb:L517</c>) - but the panel never read it. Every
/// one of its operations passes <c>UserInfo.UserID</c> - the grid binding at
/// <c>MemberServices.ascx.vb:L150</c>, the subscription at <c>:L106</c>, the trial at <c>:L125</c> and the
/// code redemption at <c>:L413</c> - and <c>PortalModuleBase.vb:L319-L323</c> resolves that as the
/// SIGNED-IN account. The container agreed: <c>DisplayServices</c> at <c>ManageUsers.ascx.vb:L61-L66</c> is
/// the tenant's own switch AND <c>Not (IsEdit Or User.IsSuperUser)</c>, and <c>IsEdit</c>
/// (<c>UserModuleBase.vb:L329-L340</c>) is the administrative <c>ctl=Edit</c> entry point, so the tab was
/// hidden whenever an administrator reached the screen at all. An administrator who must alter somebody
/// else's membership uses the role resource's own membership actions, which is tenant administration and a
/// distinct, reviewable act - the same separation the two credential routes make.
/// </para>
/// <para>
/// MIGRATION: the legacy equivalent of all of the above was imperative tests inside the page, and they did
/// not refuse - they DEGRADED. Measured across the account screens, the gates disable the form and post a
/// warning rather than deny the request: <c>Website/admin/Users/ManageUsers.ascx.vb</c> calls
/// <c>AddModuleMessage(...)</c> followed by <c>DisableForm()</c> for the host add path (<c>:L206-L208</c>),
/// for an account belonging to another portal (<c>:L213-L215</c>) and for a super-user account addressed by
/// a caller who is not one (<c>:L220-L222</c>), and <c>Password.ascx.vb:L144</c> simply hides the change
/// panel. Exactly ONE gate in these screens redirects - <c>ManageUsers.ascx.vb:L309</c>, the unauthenticated
/// registration path. An HTTP resource has no form to disable and no panel to hide, so every one of these
/// becomes an outright <c>403</c>, and a client that previously saw a greyed-out control now sees a refusal
/// it must handle. That is a deliberate and unavoidable presentation difference, not a change of rule.
/// </para>
/// <para>
/// MIGRATION - discovered legacy defect, recorded rather than repaired. A sibling security screen joined its
/// super-user test to its role test with <c>OrElse</c> where <c>AndAlso</c> was evidently meant, so a super
/// user was redirected AWAY from the screen rather than past it - the inverse of the plain intent. The
/// declarative policies here do not reproduce that inversion: a host account satisfies tenant administration,
/// which is consistent with the layers beneath. The defect is annotated rather than silently absorbed, and
/// rather than "fixed" in the legacy tree, which stays byte-identical.
/// </para>
/// <para>
/// The two credential routes deserve their own note, because on them the policy is necessary but not
/// sufficient. Each names the operation it performs in its own address rather than in a body field, so the
/// authorisation decision is made before the body is read: the change route proves ownership, the reset route
/// proves administration. The service then additionally requires the current credential on a change and
/// refuses a body that names the other operation, so a caller cannot reach one address and be served by the
/// other. Both bounds are needed - the policy alone would not stop a holder submitting a reset, and the
/// service check alone would not stop an administrator submitting a change.
/// </para>
/// <para>
/// FIVE of these actions can answer <c>503</c>, and every one of them declares it: create, delete, change a
/// credential, reset a credential, and set approval. Four of the five reach it the same way - ending an
/// account's live sessions is part of deleting it, of writing either kind of credential change and of
/// withdrawing approval, and the token store that holds those sessions can be unreachable - while deleting
/// can also fail to remove the stored credential and creating can be refused by the membership provider. In
/// every case the operation is ABANDONED rather than half applied, and the outcome carries a
/// store-unavailable code that the one shared translator classifies as <c>503</c>: a fault the caller may
/// retry, not a request they can correct. Declaring it is not decoration - a status the pipeline genuinely
/// produces and the published description omits leaves a generated client with no branch for it, which is
/// the same class of defect as advertising a response that cannot occur. The refusal a rate limiter produces
/// is a different thing and is always <c>429</c>.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
[Authorize]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    //   validated by the globally registered validation filter, which runs before the action, resolves a
    //   validator from each bound argument's declared type and short-circuits with the RFC 7807 validation
    //   document. This controller used to take validators of its own and invoke them by hand as well, which
    //   was a second invocation path for one rule set - and the hand-written path reached the wrong
    //   validator on the paging contract, judging an account filter against the union of every collection's
    //   sortable fields. Adding a validator argument back here would recreate that split, so no action below
    //   inspects model state and none builds a validation document.
    //
    // ONE DEPENDENCY, and it is an application-layer contract. Persistence is unreachable from this file:
    //   there is no context, no repository and no unit of work, and the project reference graph makes that a
    //   compile-time fact rather than a convention. Nothing that decides anything is injected either - no
    //   hasher, no token service, no cache, no clock, no configuration and no HTTP context accessor - so
    //   every rule this resource enforces demonstrably lives beneath it.

    /// <summary>The account service this controller delegates to.</summary>
    private readonly IUserService _users;
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="UsersController"/> class.</summary>
    /// <param name="users">The application-layer contract for the account aggregate.</param>
    /// <param name="portalContext">The tenant resolved from the request host.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public UsersController(IUserService users, IPortalContextHolder portalContext)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the tenant resolved for the current request, or <see langword="null"/>.</summary>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>
    /// Returns a filter that was genuinely supplied, or <see langword="null"/> for one that is blank.
    /// </summary>
    /// <param name="value">The filter as it arrived.</param>
    /// <returns>The filter, or <see langword="null"/> when it carries nothing to filter by.</returns>
    /// <remarks>
    /// <para>
    /// EXISTS SO THAT A BODY-BOUND FILTER MEANS WHAT A QUERY-BOUND ONE MEANS. The framework's query binder
    /// converts a blank query value to <see langword="null"/> before an action sees it - measured for the
    /// empty string and for whitespace alike - so the query-bound listing never presents the application
    /// service with a filter that is present but blank. A JSON body carries no such conversion, so
    /// <c>""</c> arrived as <c>""</c>, the service's blank-filter rule fired, and the same operation
    /// answered <c>400</c> through one action and <c>200</c> through the other.
    /// </para>
    /// <para>
    /// The service's rule is deliberately left exactly as it is: absence means "do not filter" and a blank
    /// filter is a caller error, because an empty prefix matches every row and would make a filtered search
    /// silently unfiltered. What changes is that this action no longer MANUFACTURES such a filter out of a
    /// value the other action would have discarded.
    /// </para>
    /// </remarks>
    private static string? Supplied(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Lists a portal's users.</summary>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="userName">Restricts the result to user names beginning with this text.</param>
    /// <param name="email">Restricts the result to addresses beginning with this text.</param>
    /// <param name="profilePropertyName">Restricts the result by a profile property's value.</param>
    /// <param name="profilePropertyValue">The profile value to match.</param>
    /// <param name="isApproved">Restricts the result by approval state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>One page of users, in the wire envelope: an <c>items</c> array of rows and a <c>meta</c> object carrying the total across every page, the page index and the page size. The domain paging type is not serialised.</returns>
    /// <remarks>
    /// <para>
    /// The filters are passed through exactly as received. Which combinations are legal - and in particular
    /// that a profile property name without a value is a conflict rather than a wildcard - is the service's
    /// rule, and re-implementing it here would give HTTP callers a different answer from every other caller.
    /// </para>
    /// <para>
    /// MIGRATION: consolidates the legacy screen's four readers into one address. <c>Users.ascx.vb</c>
    /// selected between <c>GetUsers</c> (<c>:L265</c>), <c>GetUsersByEmail</c> (<c>:L269</c>),
    /// <c>GetUsersByUserName</c> (<c>:L271</c>) and <c>GetUsersByProfileProperty</c> (<c>:L274</c>) on the
    /// value of a drop-down, and every one of them reported its grand total by mutating an argument passed
    /// by reference. Here the filters are independent optional arguments and the total travels back beside
    /// the rows in the paging envelope, so no argument is mutated and no caller has to declare a variable to
    /// receive a count. The profile-property pair addresses the third legacy search mode, which is a genuine
    /// legacy capability rather than an invention.
    /// </para>
    /// <para>
    /// MIGRATION: the page index this action accepts is ZERO-BASED, matching the legacy
    /// <c>CurrentPage - 1</c> arithmetic, and each text filter matches from the START of the value, matching
    /// the legacy <c>SearchText + "%"</c>. Both are preserved rather than modernised because either change
    /// would silently alter which rows a client receives.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<UserListItemDto>>> ListAsync(
        [FromQuery] UserPagedRequest request,
        [FromQuery] string? userName,
        [FromQuery] string? email,
        [FromQuery] string? profilePropertyName,
        [FromQuery] string? profilePropertyValue,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter. It resolves the account collection's own paging
        // validator from this parameter's DECLARED TYPE, which is why the type is the specialised one: the
        // unspecialised paging contract resolves the general validator, whose sortable set is the union of
        // every collection's, so a portal or role field name would be accepted here and then discarded by
        // the listing.
        Result<PagedResult<UserListItemDto>> outcome = await _users
            .ListUsersAsync(
                portalId,
                request,
                userName,
                email,
                profilePropertyName,
                profilePropertyValue,
                isApproved,
                cancellationToken)
            .ConfigureAwait(false);

        // Projected onto the wire envelope here rather than returned as the domain page. CompletePage
        // applies PagedResponse<T>.From, so the response carries `items` plus `meta` and the domain
        // paging type never crosses the boundary.
        return this.CompletePage(outcome);
    }

    /// <summary>Searches a portal's users, taking every filter from the request body.</summary>
    /// <param name="request">Paging, sorting and the search filters, bound from the body.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>One page of users, in the same wire envelope the listing returns.</returns>
    /// <remarks>
    /// <para>
    /// THE SAME CAPABILITY AS <see cref="ListAsync"/>, REACHED WITHOUT PUTTING PERSONAL DATA IN THE
    /// REQUEST TARGET. Both actions call the identical service member with the identical arguments, so
    /// they cannot answer differently; the only difference is where the filters travelled. A caller
    /// filtering by user name, email address or profile property uses this action, and a caller listing
    /// with paging alone continues to use the <c>GET</c>.
    /// </para>
    /// <para>
    /// WHY THAT MATTERS, PRECISELY. A request target is written to the browser's history, to every
    /// forward and reverse proxy access log, to the server access log and to any telemetry that samples
    /// URLs - all of which sit at an endpoint of the encrypted channel rather than in the middle of it,
    /// so transport encryption does not address the exposure. That is CWE-598. A request body is written
    /// to none of them by default. The profile-property pair is the sharpest case, because a tenant
    /// defines its own properties and the value being matched is therefore arbitrary personal data whose
    /// meaning the server does not know.
    /// </para>
    /// <para>
    /// A <c>POST</c> THAT READS IS NOT A CONTRADICTION HERE. The action mutates nothing and is safe in
    /// every sense except the one HTTP names: it is not idempotent-by-cache, which is the property being
    /// given up on purpose, since a cache entry keyed by a body carrying personal data is the exposure
    /// this action exists to avoid. Nothing is created, so the answer is <c>200</c> with the page and
    /// never <c>201</c> with a location.
    /// </para>
    /// <para>
    /// It carries the SAME authorisation policy as the listing rather than a weaker one. Moving a filter
    /// from the target to the body changes where data travels and nothing about who may ask.
    /// </para>
    /// </remarks>
    [HttpPost("search")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<UserListItemDto>>> SearchAsync(
        [FromBody] UserSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter, which resolves UserSearchRequestValidator from
        // this parameter's declared type. That validator derives from the same base as the listing's, so
        // the two actions apply the identical paging bounds and the identical narrow sortable set - a
        // caller moving a request into the body must not discover that different page sizes are legal.
        //
        // ⚠ A BLANK FILTER MEMBER IS NORMALISED TO ABSENT, AND THAT IS WHAT MAKES THE TWO ACTIONS ONE
        // OPERATION. The service treats absence as "do not filter" and REFUSES a filter that is present
        // but blank, on the stated grounds that an empty prefix matches every row and would make a
        // filtered search silently unfiltered. The query-bound listing never reaches that rule, because
        // the framework's query binder converts a blank query value to null before the action sees it -
        // measured, for the empty string and for whitespace alike. A body carries no such conversion, so
        // JSON "" arrived as "" and the same operation answered 400 where the query form answers 200.
        //
        // That was a real regression rather than a theoretical asymmetry: an operator CLEARING the search
        // box on the account listing produces exactly this request, and it met an error banner. Reproducing
        // the binder's own conversion here is the narrowest fix that keeps one rule in one place - the
        // service's rule is untouched and still refuses a blank filter, and this action simply stops
        // manufacturing one. Which COMBINATIONS are legal, and in particular that a profile-property name
        // without a value is a refusal rather than a wildcard, remains the service's decision; normalising
        // first is what makes that decision identical for both actions.
        Result<PagedResult<UserListItemDto>> outcome = await _users
            .ListUsersAsync(
                portalId,
                request,
                Supplied(request.UserName),
                Supplied(request.Email),
                Supplied(request.ProfilePropertyName),
                Supplied(request.ProfilePropertyValue),
                request.IsApproved,
                cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Lists a portal's accounts as an account picker needs them, and nothing more.</summary>
    /// <param name="request">
    /// Paging, ordering and the optional name filter, bound from the query string. The filter matches a
    /// prefix of the login name or the display name.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// One page of choices in the wire envelope: an <c>items</c> array of rows carrying <c>userId</c>,
    /// <c>username</c> and <c>displayName</c> only, plus a <c>meta</c> object carrying the total across
    /// every page, the page index and the page size.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠ WHY THIS EXISTS ALONGSIDE <see cref="ListAsync"/>, AND WHY IT MUST NOT BE FOLDED BACK INTO IT. A
    /// performance and privacy review measured the role-assignment screen building its account drop-down,
    /// and its account-count probe, from the account listing. Every candidate row carried a postal address,
    /// a telephone number, an electronic-mail address, a creation instant, a last-login instant and the
    /// approval, lockout, online and super-user flags across the wire so that three values could be
    /// rendered - and that screen is permitted to enumerate a tenant of up to a thousand accounts. Being
    /// authorised to read the account grid is not a licence to receive fields the asking screen has no use
    /// for; this address is where a caller that only needs to CHOOSE an account asks for exactly that.
    /// </para>
    /// <para>
    /// THE COUNT PROBE IS THE CHEAPEST CASE AND IS SERVED BY THE SAME ADDRESS. A caller that needs only how
    /// many accounts a tenant holds asks for one row and reads <c>meta.totalCount</c>; the single row it
    /// receives carries no personal detail at all, whereas the same probe against the listing disclosed a
    /// complete account row to read a number.
    /// </para>
    /// <para>
    /// IT CARRIES THE SAME AUTHORISATION POLICY AS THE LISTING, NOT A WEAKER ONE. Narrowing a projection
    /// changes what is disclosed and nothing about who may ask, and the answer still enumerates a tenant's
    /// membership - so the portal-administrator gate stands, and the tenant is the resolved request tenant
    /// rather than anything the caller names.
    /// </para>
    /// <para>
    /// A <c>GET</c> RATHER THAN A <c>POST</c>, unlike <see cref="SearchAsync"/>, and the distinction is the
    /// same one that governs the listing: the filter here is a prefix of a name an operator can already see
    /// in a drop-down on the same screen, and it reaches at most two public captions. The listing's search
    /// moves to a body because it matches an electronic-mail address and arbitrary tenant-defined profile
    /// values, which are neither public nor bounded in what they may contain. Nothing this filter can match
    /// is absent from the response, so a request target recording it discloses nothing the response did not.
    /// </para>
    /// <para>
    /// MIGRATION: this is <c>cboUsers</c> on <c>Website/admin/Security/securityroles.ascx</c>, filled by
    /// <c>UserModuleBase.vb:L178-L186</c>. That code read the tenant's account count first and offered the
    /// drop-down only at or below one thousand accounts, offering a name box above it - so both halves of
    /// the legacy behaviour, the count and the enumeration, are served here.
    /// </para>
    /// </remarks>
    [HttpGet("choices")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserChoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<UserChoiceDto>>> ListChoicesAsync(
        [FromQuery] UserChoicePagedRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter, which resolves UserChoicePagedRequestValidator from
        // this parameter's DECLARED TYPE. That is why the type is the specialised one: the unspecialised
        // paging contract resolves the general validator, whose sortable set is the union of every
        // collection's, so a portal or module field name would be accepted here and then discarded.
        Result<PagedResult<UserChoiceDto>> outcome = await _users
            .ListAccountChoicesAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Retrieves one user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The user.</returns>
    [HttpGet("{userId:int}")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDetailDto?>>> GetAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<UserDetailDto?> outcome = await _users
            .GetUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a user in a portal.</summary>
    /// <param name="request">The user to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created user, with their address in the location header.</returns>
    /// <response code="201">The account has been created; its address is in the location header.</response>
    /// <response code="403">
    /// The tenant's account quota is exhausted, or a super-user account was addressed by a caller who is not
    /// one. Neither test is performed here.
    /// </response>
    /// <response code="409">
    /// The user name is taken, or the account is already registered in this portal.
    /// </response>
    /// <response code="429">The shared credential budget for this window is spent.</response>
    /// <response code="503">The membership provider could not store the credential.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy creation reported an eighteen-member outcome enumeration whose success value is
    /// <c>13</c> and whose zero value, <c>AddUser</c>, is the "no error yet" marker rather than a failure
    /// (<c>Website/admin/Users/User.ascx.vb:L149</c> initialises with it, <c>:L185</c> tests inequality with
    /// it, <c>:L226</c> makes the call and <c>:L229</c> tests for success). This action interprets neither
    /// number: the outcome arrives as a code, so the trap cannot be sprung here.
    /// </para>
    /// <para>
    /// MIGRATION: the quota rule that gates creation is the legacy
    /// <c>PortalSettings.Users &lt; PortalSettings.UserQuota Or UserInfo.IsSuperUser Or
    /// PortalSettings.UserQuota = 0</c> (<c>ManageUsers.ascx.vb:L367</c>), in which a quota of ZERO means
    /// unlimited. It is evaluated in the service and answers <c>403</c> here, which is chosen over
    /// <c>409</c> deliberately and once: the request is well formed and the resource state is not in
    /// conflict with it - the caller is simply not permitted to add another account.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    // HASHES A CREDENTIAL, and was completely unbounded: the credential window and the process-wide
    // concurrency bound were applied by matching whole segments of the request path against a word list, and
    // "users" is not on it. Hashing is deliberately expensive, so an unbounded hashing endpoint lets the
    // caller decide how much of this process's time is spent.
    //
    // The mark alone is the complete measure and no EnableRateLimiting attribute accompanies it. The mark is
    // read by the GLOBAL limiter's classifier, and that limiter chains both bounds - the window and the
    // process-wide concurrency ceiling. A named policy contributes exactly one partition, so it could add
    // only a second window, keyed identically to the first and therefore consumed in lockstep with it: a
    // duplicate limiter instance and a second permit acquisition per request that enforce nothing further.
    [CredentialEndpoint]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> CreateAsync(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor.
        Result<UserDetailDto> outcome = await _users
            .CreateUserAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.UserId);
    }

    /// <summary>Updates a user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated user.</returns>
    [HttpPut("{userId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> UpdateAsync(
        int userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor.
        Result<UserDetailDto> outcome = await _users
            .UpdateUserAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the user has been removed.</returns>
    /// <response code="204">The account, its credential and its live sessions have all been removed.</response>
    /// <response code="403">
    /// The account is protected: the tenant's designated administrator, or a host account addressed by a
    /// caller who is not one. Both refusals are the service's rules, not this file's.
    /// </response>
    /// <response code="404">No such account in this portal.</response>
    /// <response code="503">
    /// The account's live sessions or its stored credential could not be removed. The removal is abandoned
    /// rather than half applied, so the account still exists and the request may be retried.
    /// </response>
    /// <remarks>
    /// MIGRATION: the legacy call returned a bare <c>Boolean</c> -
    /// <c>Website/admin/Users/User.ascx.vb:L346</c> tested <c>If UserController.DeleteUser(User, True,
    /// False) Then</c> and raised a generic error event otherwise, so every distinct cause of failure looked
    /// identical to the screen. The outcome now carries a code, which is what lets a protected account, an
    /// unknown account and an unreachable store answer <c>403</c>, <c>404</c> and <c>503</c> respectively
    /// instead of collapsing into one message.
    /// </remarks>
    [HttpDelete("{userId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .DeleteUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Changes the calling account's own password.</summary>
    /// <param name="userId">The user identifier, which must be the caller's own.</param>
    /// <param name="request">The current and new passwords.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been changed.</returns>
    /// <remarks>
    /// SELF-SERVICE ONLY. The account-owner policy requires the subject claim to equal the account in the
    /// route, and the service always verifies the credential presented. An administrator who must intervene
    /// uses the sibling reset endpoint, which is a distinct authorisation decision rather than a different
    /// shape of body sent to this one - the arrangement that previously let any bearer token overwrite any
    /// account's credential in any tenant simply by naming it in the route.
    /// </remarks>
    [HttpPost("{userId:int}/password")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [RemediationAllowed]
    [AllowDuringRemediation(RemediationEndpointKind.Password)]
    // Verifies the current credential and hashes the replacement.
    [CredentialEndpoint]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ChangePasswordAsync(
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor. The credential
        // itself is forwarded and never inspected, compared, hashed or logged here.
        Result outcome = await _users
            .ChangePasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Resets a user's password administratively, without the current one.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new password. The current password is neither required nor consulted.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the password has been reset.</returns>
    /// <remarks>
    /// <para>
    /// ITS OWN ENDPOINT, AND THAT IS THE POINT. Not verifying the current credential is what a reset is for,
    /// and it is safe only because reaching this address requires administration of the account's own portal.
    /// The operation used to be selectable from inside the change endpoint's request body, so the check was
    /// skipped on the caller's own instruction with no privilege proved at all.
    /// </para>
    /// <para>
    /// MIGRATION: replaces the reset branch of <c>Website/admin/Users/Password.ascx.vb</c>, which the legacy
    /// screen offered only to an administrator viewing another account. The new credential is never returned,
    /// unlike the legacy reset, which handed it back in clear text.
    /// </para>
    /// </remarks>
    [HttpPost("{userId:int}/password-reset")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    // HASHES A CREDENTIAL, and was unbounded for a subtler reason than account creation: the path matcher
    // compares WHOLE segments, and this route's segment is "password-reset", which is equal to neither
    // "password" nor "reset". The address looked covered and was not.
    [CredentialEndpoint]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ResetPasswordAsync(
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor. The replacement
        // credential is forwarded and never inspected, hashed, echoed or logged here - and the new value is
        // not returned to the caller, unlike the legacy reset, which handed it back in clear text.
        Result outcome = await _users
            .ResetPasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Clears a lockout so the account can be used again.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the lockout has been cleared.</returns>
    /// <response code="204">The lockout has been cleared.</response>
    /// <response code="400">The account is not locked, so there is nothing to clear.</response>
    /// <response code="403">An administrator may not unlock their own account.</response>
    /// <response code="404">No such account in this portal, or it holds no credential.</response>
    /// <remarks>
    /// MIGRATION: replaces the unlock command of <c>Website/admin/Users/Membership.ascx.vb:L262</c>, which
    /// called a member returning <c>Boolean</c> and simply did nothing visible when it returned false. The
    /// outcome now distinguishes an account that is not locked from one that does not exist, which is why
    /// this action declares a <c>400</c> at all - and declares it carrying the plain problem document, since
    /// the request has no member that could be named as the offender.
    /// </remarks>
    [HttpPost("{userId:int}/unlock")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UnlockAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .UnlockUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Approves or unapproves a user.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="isApproved">The approval state to set.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the approval state has been set.</returns>
    /// <response code="204">The approval state has been set.</response>
    /// <response code="400">The approval state was not stated. It is required and is never inferred.</response>
    /// <response code="403">
    /// An administrator may not set their own approval state. That rule is the service's.
    /// </response>
    /// <response code="404">No such account in this portal.</response>
    /// <response code="409">The account already holds the requested state.</response>
    /// <response code="503">
    /// Withdrawing approval must also end the account's live sessions, and the token store could not be
    /// reached. The withdrawal is abandoned rather than applied while the sessions remain usable.
    /// </response>
    /// <remarks>
    /// <para>
    /// The desired state is an explicit argument rather than two endpoints named approve and unapprove,
    /// because the service reports setting the state it already holds as a conflict - and that answer is only
    /// meaningful if the caller stated which state they meant. It is bound as a NULLABLE boolean and a null
    /// is refused: a non-nullable binding made the parameter optional, so a caller who omitted it withdrew
    /// approval and ended the account's sessions without ever having asked for either.
    /// </para>
    /// <para>
    /// MIGRATION: replaces the authorise and unauthorise commands of
    /// <c>Website/admin/Users/Membership.ascx.vb</c>, which set approval true (<c>:L199-L202</c>) or false
    /// (<c>:L243-L246</c>) on the membership object and then saved the whole account. Two commands whose only
    /// difference was one boolean become one address taking that boolean, and the precondition they each
    /// carried separately is now enforced in one place.
    /// </para>
    /// </remarks>
    [HttpPut("{userId:int}/approval")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SetApprovalAsync(
        int userId,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }
        // THE STATE MUST BE STATED, AND OMITTING IT IS NOT THE SAME AS SAYING FALSE. Bound as a
        // non-nullable boolean this parameter was OPTIONAL: a caller who addressed
        // .../approval with no query string bound the default, which is false, so a request that named no
        // state silently WITHDREW approval and revoked the account's live sessions. Nothing about that
        // request was malformed enough for the model binder to object, so the 400 this action advertises was
        // unreachable and the destructive outcome was the quiet one. A nullable parameter makes absence
        // representable, and this refusal is what turns it into the advertised answer.
        if (isApproved is not { } desiredState)
        {
            return this.ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(isApproved)] =
                    [
                        "The approval state is required. Send ?isApproved=true to approve the account or "
                        + "?isApproved=false to withdraw approval; omitting it is refused rather than read "
                        + "as false, because withdrawing approval also ends the account's live sessions.",
                    ],
                }));
        }

        Result outcome = await _users
            .SetUserApprovalAsync(portalId, userId, desiredState, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Requires the user to change their password at next sign-in.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the requirement has been recorded.</returns>
    /// <response code="204">The requirement has been recorded.</response>
    /// <response code="403">An administrator may not aim this at their own account.</response>
    /// <response code="404">No such account in this portal.</response>
    /// <response code="409">The account is already required to change its credential.</response>
    /// <remarks>
    /// MIGRATION: replaces the legacy command at <c>Website/admin/Users/Membership.ascx.vb:L221-L224</c>,
    /// which set an update-password flag on the membership object and saved the whole account. It is its own
    /// address rather than a field on the update request because it was its own administrative act on the
    /// legacy screen, and because an update of a display name should not be able to carry it.
    /// </remarks>
    [HttpPost("{userId:int}/require-password-change")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RequirePasswordChangeAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .RequirePasswordChangeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a portal's membership settings.</summary>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The membership settings.</returns>
    /// <response code="200">
    /// The tenant's membership settings. A tenant with no settings source is answered here too, with the
    /// measured legacy defaults and <c>isStored: false</c>, rather than with a failure.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>Website/admin/Users/UserSettings.ascx.vb:L106</c>, which read the settings
    /// through <c>UserController.GetUserSettings(UserPortalID)</c> - a member returning an untyped
    /// <c>Hashtable</c> (<c>Library/Components/Users/UserController.vb:L656</c>) - and then walked it with a
    /// dictionary enumerator, splitting each key on an underscore to recover its grouping. One typed transfer
    /// object replaces the dictionary, so the available settings are declared rather than discovered. The
    /// address is keyed by the PORTAL alone: these are the tenant's settings, not any account's.
    /// </para>
    /// <para>
    /// ⚠ NO <c>404</c> IS DECLARED, BECAUSE NONE IS REACHABLE. This action used to advertise one for a
    /// tenant that stores no settings, from when the service answered that case with a value-free success
    /// that <see cref="ApiResults"/> mapped onto <c>404</c> by convention. It no longer does:
    /// <c>GetMembershipSettingsAsync</c> always returns a document, so the only failures left are the
    /// authentication and tenant-resolution refusals below. A declared response the pipeline cannot produce
    /// is a published contract describing a different API from the one running - see
    /// <c>ResponseDeclarationContractTests</c> - so it is withdrawn rather than left as documentation of a
    /// branch a generated client would carry and never take.
    /// </para>
    /// </remarks>
    [HttpGet("settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<MembershipSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<MembershipSettingsDto?>>> GetMembershipSettingsAsync(
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<MembershipSettingsDto?> outcome = await _users
            .GetMembershipSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces the resolved tenant's membership settings.</summary>
    /// <param name="request">The settings to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// <c>200 OK</c> carrying what the write did beyond storing the policy: whether the display-name format
    /// changed, and how many accounts were consequently rewritten.
    /// </returns>
    /// <response code="409">
    /// The tenant has nowhere to store membership settings, because the installation defines no user-accounts
    /// module instance on any of the tenant's pages. Reported as a conflict rather than as a missing resource
    /// on purpose: the read beside this write answers <c>200</c> for the same address with the legacy
    /// defaults, so the settings resource demonstrably exists and what is absent is the store behind it. The
    /// detail names the module to add, so the operator can resolve the state and re-send the identical
    /// request.
    /// </response>
    /// <remarks>
    /// <para>
    /// ⚠ ANSWERS 200 WITH A BODY WHERE EVERY OTHER SETTINGS WRITE IN THIS API ANSWERS 204, and the
    /// difference is the point rather than an inconsistency. This write has one side effect whose size the
    /// caller cannot predict: changing <c>Security_DisplayNameFormat</c> rewrites the display name of every
    /// account in the tenant. The legacy screen performed that sweep on a background thread and reported
    /// nothing (<c>UserSettings.ascx.vb</c> L175-L182), so an operator had no way to know whether it had
    /// happened, how many accounts it touched, or whether it had failed half way. A 204 here would preserve
    /// exactly that blindness.
    /// </para>
    /// <para>
    /// ⚠ THE DECLARED FAILURE FOR AN ABSENT STORE IS <c>409</c>, NOT <c>404</c>, and the pair with the read
    /// above is the reason. A tenant whose portal holds no
    /// <c>"User Accounts"</c> module instance is answered <c>200</c> by the read for this very address, so
    /// the resource exists and only its store does not - a state the operator can repair, which is a
    /// conflict. <c>UserService</c> reports it as <c>user.membership-settings.storage-conflict</c> and
    /// <see cref="ApiResults"/> resolves that reason onto <c>409</c>. The <c>404</c> this action used to
    /// advertise was unreachable once the read stopped answering absence on the status line, and a declared
    /// response the pipeline cannot produce describes a different API from the one running.
    /// </para>
    /// </remarks>
    /// <response code="200">What the write did beyond storing the policy.</response>
    /// <response code="409">
    /// The portal holds no <c>"User Accounts"</c> module instance, so there is nowhere to store the policy.
    /// The detail names the module the installation needs.
    /// </response>
    [HttpPut("settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<MembershipSettingsUpdateResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    // MIGRATION: the conflict branch is DECLARED, not new. The service already refuses this write when the
    // tenant has no settings store, with a code the shared translator answers 409 for; leaving it out of the
    // published contract meant a generated client had no member for the one refusal an operator can actually
    // act on, and would surface it as an unexpected status instead of the actionable message it carries.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<MembershipSettingsUpdateResultDto>>> UpdateMembershipSettingsAsync(
        [FromBody] UpdateMembershipSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<MembershipSettingsUpdateResultDto> outcome = await _users
            .UpdateMembershipSettingsAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a user's profile.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The profile, including the definition of each property.</returns>
    /// <response code="200">The account's profile values with the definition of each property.</response>
    /// <response code="404">No such account in this portal.</response>
    /// <remarks>
    /// MIGRATION: each property's stored value and declared data type are carried through unchanged. The
    /// legacy data type resolved through the list subsystem
    /// (<c>Website/admin/Users/EditProfileDefinition.ascx.vb</c>), which is out of scope, so nothing here
    /// resolves it, validates against a closed set or converts it. A client renders the form from the
    /// definitions, exactly as the legacy property editor did.
    /// </remarks>
    [HttpGet("{userId:int}/profile")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [RemediationAllowed]
    [AllowDuringRemediation(RemediationEndpointKind.Profile)]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserProfileDto?>>> GetProfileAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<UserProfileDto?> outcome = await _users
            .GetProfileAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a user's profile values.</summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="profile">The values to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the profile has been stored.</returns>
    /// <response code="204">The profile values have been stored.</response>
    /// <response code="400">A required property was left empty, or a value was refused by its definition.</response>
    /// <response code="404">No such account in this portal, or the body named a property that is not defined.</response>
    /// <response code="409">
    /// One submission named the same property definition more than once. The body is well formed and every
    /// value in it is individually acceptable, so the refusal is about the submission as a whole rather than
    /// about any single member - which is why it is a conflict carrying a plain problem document rather than a
    /// validation document keyed to a field. Removing the repeat makes the request succeed.
    /// </response>
    /// <remarks>
    /// MIGRATION: replaces <c>Website/admin/Users/Profile.ascx.vb:L226</c>, which handed a whole
    /// property-definition collection to the profile controller and took a rebuilt account back. Here the
    /// body carries values only, the definitions stay a separate resource, and the per-property rules the
    /// legacy property editor enforced in the page are enforced in the service - which is why a refused value
    /// answers <c>400</c> while an undefined property answers <c>404</c>.
    /// </remarks>
    [HttpPut("{userId:int}/profile")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [RemediationAllowed]
    [AllowDuringRemediation(RemediationEndpointKind.Profile)]
    [EnableRateLimiting(RateLimitingExtensions.ProfileWritePolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    // MIGRATION: the conflict branch is DECLARED, not new. A repeated property definition in one submission
    // is already refused with a code the shared translator answers 409 for, and the declaration is what tells
    // a generated client that this endpoint has a refusal outside the validation document it otherwise
    // advertises.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UpdateProfileAsync(
        int userId,
        [FromBody] UserProfileDto profile,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .UpdateProfileAsync(portalId, userId, profile, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the member services this account may subscribe to, with its own subscription state.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The catalogue, in the shared envelope. Empty when the tenant publishes no services.</returns>
    /// <response code="200">The tenant's published services, annotated with this account's subscription state.</response>
    /// <response code="401">No valid credential was presented.</response>
    /// <response code="403">The route names another account, or the tenant does not offer self-service subscription.</response>
    /// <response code="404">No such account in this portal.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces the grid binding of <c>Website/admin/Users/MemberServices.ascx.vb:L147-L157</c>.
    /// The catalogue is the tenant's PUBLIC roles rather than the account's memberships - the terminal
    /// statement selects <c>where R.PortalId = @PortalId and R.IsPublic = 1</c>
    /// (<c>04.06.00.SqlDataProvider:L993-L1013</c>) - so an offer the account has not taken up appears
    /// exactly as one it has, and each row says which.
    /// </para>
    /// <para>
    /// Deliberately UNPAGED, because the read it replaces was: the legacy grid bound the whole result and
    /// hid itself when the count was zero. A tenant's set of public roles is a published price list, not a
    /// data set.
    /// </para>
    /// <para>
    /// Each row carries the three predicates the legacy markup bound - the command label, whether the
    /// subscription command was rendered and whether the trial command was - because they are business
    /// rules and belong beneath this file. A client renders wording from them and never recomputes them.
    /// </para>
    /// </remarks>
    [HttpGet("{userId:int}/services")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MemberServiceDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MemberServiceDto>>>> ListMemberServicesAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<MemberServiceDto>> outcome = await _users
            .ListMemberServicesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Subscribes this account to a member service, or renews a lapsed subscription.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="roleId">The role the service is expressed as. Zero is a real key.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the subscription has been written.</returns>
    /// <response code="204">The account now holds the service, with an expiry derived from its terms.</response>
    /// <response code="401">No valid credential was presented.</response>
    /// <response code="403">The route names another account, the tenant does not offer self-service subscription, the role is not published for it, or completing it would require taking payment.</response>
    /// <response code="404">No such account or role in this portal.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>Subscribe(roleID, cancel:False)</c>
    /// (<c>Website/admin/Users/MemberServices.ascx.vb:L101-L118</c>). ONE address serves both the subscribe
    /// and the renew command, because the legacy grid reached the same member under both labels
    /// (<c>:L440-L442</c>) - the label differed, the operation did not. Which of the two the catalogue
    /// advertises for a given row is reported on the row itself.
    /// </para>
    /// <para>
    /// Idempotent in the way the legacy member was: subscribing again revises the expiry rather than
    /// failing, which is exactly how a renewal was performed. The body is EMPTY and no date may be
    /// submitted - the two bounds are derived from the role's stored terms beneath this file, and the
    /// administrative endpoint that does accept bounds is the role resource's own membership action.
    /// </para>
    /// <para>
    /// MIGRATION: a service that charges a fee is REFUSED here with <c>403</c> rather than redirected. The
    /// legacy path answered such a role with
    /// <c>Response.Redirect("~/admin/Sales/PayPalSubscription.aspx?…")</c> (<c>:L113</c>), and AAP 0.2.2.4
    /// excludes sales administration, so no payment can be taken. The catalogue reports the same condition
    /// per row, so a client can explain the refusal before provoking it.
    /// </para>
    /// </remarks>
    [HttpPost("{userId:int}/services/{roleId:int}/subscription")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SubscribeToServiceAsync(
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .SubscribeToServiceAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Cancels this account's subscription to a member service.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="roleId">The role the service is expressed as. Zero is a real key.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the subscription has ended.</returns>
    /// <response code="204">The subscription has ended, either by removal or by being back-dated.</response>
    /// <response code="401">No valid credential was presented.</response>
    /// <response code="403">The route names another account, the tenant does not offer self-service subscription, the role is not published for it, the subscription is protected, or settling it would require taking payment.</response>
    /// <response code="404">No such account or role in this portal, or the account does not hold the service.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>Subscribe(roleID, cancel:True)</c>
    /// (<c>Website/admin/Users/MemberServices.ascx.vb:L101-L118</c>), reached under the unsubscribe command
    /// (<c>:L443-L445</c>). The legacy CANCEL path shared the SUBSCRIBE gate and redirected a fee-bearing
    /// role to the payment page too, with <c>&amp;cancel=1</c> appended (<c>:L115</c>), so the payment
    /// refusal applies to this direction as well and is not an oversight.
    /// </para>
    /// <para>
    /// <c>DELETE</c> rather than a second <c>POST</c> because it is the withdrawal of the resource the
    /// sibling <c>POST</c> creates, at the same address. It answers <c>204</c> in both of the two ways a
    /// subscription can end: the row is withdrawn, or - when a paid trial has already been consumed - its
    /// expiry is back-dated so the consumed-trial fact survives (<c>RoleController.vb:L494-L496</c>). Which
    /// happened is reported as a reason on the successful outcome beneath this file; the status is the same
    /// either way, because the subscription no longer holds in both.
    /// </para>
    /// </remarks>
    [HttpDelete("{userId:int}/services/{roleId:int}/subscription")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> CancelServiceAsync(
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .CancelServiceAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Starts this account's free trial of a paid member service.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="roleId">The role the service is expressed as. Zero is a real key.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the trial subscription has been written.</returns>
    /// <response code="204">The account now holds the service on its trial terms.</response>
    /// <response code="401">No valid credential was presented.</response>
    /// <response code="403">The route names another account, the tenant does not offer self-service subscription, or this service offers this account no trial.</response>
    /// <response code="404">No such account or role in this portal.</response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>UseTrial(roleID)</c>
    /// (<c>Website/admin/Users/MemberServices.ascx.vb:L120-L133</c>), which the legacy grid reached through
    /// its own second command column with the fixed command name <c>UseTrial</c> (<c>:L45-L52</c>).
    /// </para>
    /// <para>
    /// ITS OWN ADDRESS, AND THAT IS THE POINT. The two gates genuinely differ - subscribing requires a zero
    /// SERVICE fee while trialling requires a zero TRIAL fee - so a paid service with a free trial can be
    /// trialled here even though it cannot be subscribed to. Folding the two into one endpoint that chose
    /// between them by reading a body field would make the effect depend on the payload rather than on the
    /// address, which is the shape the credential endpoints on this controller were deliberately split to
    /// avoid.
    /// </para>
    /// <para>
    /// The single <c>403</c> code covers every reason the legacy panel would not have rendered the command,
    /// without saying which: the service is free and so has nothing to trial, its trial carries a fee, or
    /// this account has already consumed it. Distinguishing them would tell a caller which of a tenant's
    /// commercial terms it had guessed wrong about, and the catalogue already reports whether the trial is
    /// on offer.
    /// </para>
    /// </remarks>
    [HttpPost("{userId:int}/services/{roleId:int}/trial")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> StartServiceTrialAsync(
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .StartServiceTrialAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Redeems a service invitation code against this account.</summary>
    /// <param name="userId">The account identifier, which must be the caller's own.</param>
    /// <param name="request">The submitted code.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The services the code enrolled the account in, in the shared envelope.</returns>
    /// <response code="200">The code matched, and the response names every service it enrolled the account in.</response>
    /// <response code="400">The submission carries no code, the code is longer than any stored code can be, or no role bears it.</response>
    /// <response code="401">No valid credential was presented.</response>
    /// <response code="403">The route names another account, or the tenant does not offer self-service subscription.</response>
    /// <response code="404">No such account in this portal.</response>
    /// <response code="429">
    /// Too many redemption attempts from this account and client address within the window.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>cmdRSVP_Click</c>
    /// (<c>Website/admin/Users/MemberServices.ascx.vb:L397-L433</c>), the invitation-code box and its
    /// command at <c>MemberServices.ascx:L14-L15</c>.
    /// </para>
    /// <para>
    /// It answers <c>200</c> with a body rather than <c>204</c> because the legacy screen could not say
    /// WHICH services a code had enrolled the account in - it posted one fixed sentence about "the role(s)
    /// associated with the RSVP Code entered" and rebound the grid - and the collection is genuinely
    /// plural: the legacy loop had no early exit, so one code may match several roles.
    /// </para>
    /// <para>
    /// It is not a <c>201</c> and carries no location header: nothing addressable is created at a new
    /// address, and the affected rows are found in the account's own catalogue.
    /// </para>
    /// <para>
    /// MIGRATION: an unmatched code answers <c>400</c> and not <c>404</c>. The addressed resource is this
    /// account's own redemption endpoint, which exists; the code is a submitted VALUE the caller can
    /// correct, which is what the legacy <c>RSVPFailure</c> message told them. An empty submission is
    /// refused by declarative validation rather than silently ignored, which is what the legacy guard
    /// <c>If code &lt;&gt; ""</c> did.
    /// </para>
    /// <para>
    /// MIGRATION: the search deliberately spans EVERY role of the tenant, published or not, free or not.
    /// The legacy handler read the whole role set and applied neither the public test nor the fee test the
    /// grid's own commands applied, because an invitation code IS the bypass for an unpublished service.
    /// This endpoint therefore does not refuse a fee-bearing role: no payment was taken on the legacy path
    /// either.
    /// </para>
    /// <para>
    /// SEC: THIS IS A CREDENTIAL SUBMISSION AND IS BOUNDED AS ONE, WHICH IT WAS NOT. The body carries a
    /// secret the caller either knows or does not; the search spans every role of the tenant, published or
    /// not, free or not; a match grants membership; and the two answers are distinguishable by construction -
    /// <c>200</c> naming what was granted against <c>400</c> for a miss. Unbounded, that is an online
    /// guessing oracle for a role grant, and nothing bounded it: the action declared no limiter policy, and
    /// the fall-back classifier that catches an unmarked credential endpoint matched a closed word list that
    /// named nothing in this route. It now declares
    /// <see cref="RateLimitingExtensions.RedemptionPolicyName"/> - its own window, partitioned by the
    /// ACCOUNT THIS ROUTE NAMES and the caller's observable address together, because neither key alone
    /// bounds a determined guesser - and it carries the credential mark, which brings it under the
    /// process-wide concurrency bound and the body limit as well. The account half is read from the route
    /// rather than from the caller's claims because the limiter runs before authentication, and it is sound
    /// here because <see cref="PolicyNames.AccountOwner"/> admits a caller only to its own account: a caller
    /// varying the segment buys partitions in which every request is refused before a code is compared. The
    /// refusal is <c>429</c>, and it is declared above so the published contract states it.
    /// </para>
    /// </remarks>
    [HttpPost("{userId:int}/services/redemptions")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
    [EnableRateLimiting(RateLimitingExtensions.RedemptionPolicyName)]
    [CredentialEndpoint]
    [ProducesResponseType(typeof(ApiResponse<RedeemServiceCodeResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<RedeemServiceCodeResultDto>>> RedeemServiceCodeAsync(
        int userId,
        [FromBody] RedeemServiceCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // Validated by the globally registered filter; see the note on the constructor. The service repeats
        // the emptiness guard so it holds for a caller that reached it without this pipeline in front.
        Result<RedeemServiceCodeResultDto> outcome = await _users
            .RedeemServiceCodeAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
