using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
/// MIGRATION: four legacy affordances reachable from these screens deliberately have NO address on this
/// resource, and their absence is a decision rather than an omission. Role subscription and cancellation -
/// <c>MemberServices.ascx.vb:L97</c> and <c>:L106</c> - belong to the role resource, which already accepts
/// and removes an account's membership, and duplicating them here would give one rule two addresses. Bulk
/// removal of unauthorised accounts (<c>Users.ascx.vb:L328</c>), the unauthorised and online listings
/// (<c>:L259</c>, <c>:L262</c>) with their <c>"None"</c> filter sentinel (<c>:L266</c>), the bulk mail
/// screen, and the scheduled purge of the online table are all outside the agreed surface; the scheduler
/// itself is excluded, so no background worker is introduced to replace the purge.
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
/// than the authorisation.</strong> An earlier revision relied on that class attribute for all fourteen
/// routes, which meant any authenticated caller - a plain member of any tenant - could enumerate a tenant's
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
/// <strong>The fourteen routes split into THREE kinds, and the split is the security boundary.</strong>
/// The enumeration below is exhaustive and is stated per route so it can be checked against the attributes
/// rather than trusted.
/// </para>
/// <para>
/// <strong>Tenant administration</strong>, ten routes, requiring administration of the portal the route
/// names: list the accounts; create one; update one; delete one; reset a credential; clear a lockout; set
/// approval; require a change at next sign-in; and read and write the tenant's membership settings. The
/// caller must administer the very tenant addressed, which the policy handler binds to the route's own
/// portal value, so an administrator of one tenant cannot reach another's accounts.
/// </para>
/// <para>
/// <strong>Account access</strong>, three routes, admitting either the account the route names or an
/// administrator of that account's portal: read one account, and read and write its profile. Gating these on
/// administration would leave an ordinary member unable to see or maintain their own record, which the
/// legacy screens plainly allowed - the same nine screens served both an administrator managing somebody
/// else and an account managing itself.
/// </para>
/// <para>
/// <strong>Account ownership alone</strong>, one route, admitting nobody but the account the route names:
/// changing a credential. An administrator is deliberately NOT admitted here, and that is not an oversight -
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
[Route("api/v{version:apiVersion}")]
[Authorize]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
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

    /// <summary>Initialises a new instance of the <see cref="UsersController"/> class.</summary>
    /// <param name="users">The application-layer contract for the account aggregate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="users"/> is <see langword="null"/>.</exception>
    public UsersController(IUserService users)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
    }

    /// <summary>Lists a portal's users.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpGet("portals/{portalId:int}/users")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<UserListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<UserListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] UserPagedRequest request,
        [FromQuery] string? userName,
        [FromQuery] string? email,
        [FromQuery] string? profilePropertyName,
        [FromQuery] string? profilePropertyValue,
        [FromQuery] bool? isApproved,
        CancellationToken cancellationToken)
    {
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

    /// <summary>Retrieves one user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The user.</returns>
    [HttpGet("portals/{portalId:int}/users/{userId:int}")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDetailDto?>>> GetAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result<UserDetailDto?> outcome = await _users
            .GetUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a user in a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpPost("portals/{portalId:int}/users")]
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
        int portalId,
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        // Validated by the globally registered filter; see the note on the constructor.
        Result<UserDetailDto> outcome = await _users
            .CreateUserAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.UserId);
    }

    /// <summary>Updates a user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated user.</returns>
    [HttpPut("portals/{portalId:int}/users/{userId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UserDetailDto>>> UpdateAsync(
        int portalId,
        int userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        // Validated by the globally registered filter; see the note on the constructor.
        Result<UserDetailDto> outcome = await _users
            .UpdateUserAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a user.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpDelete("portals/{portalId:int}/users/{userId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .DeleteUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Changes the calling account's own password.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpPost("portals/{portalId:int}/users/{userId:int}/password")]
    [Authorize(Policy = PolicyNames.AccountOwner)]
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
        int portalId,
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        // Validated by the globally registered filter; see the note on the constructor. The credential
        // itself is forwarded and never inspected, compared, hashed or logged here.
        Result outcome = await _users
            .ChangePasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Resets a user's password administratively, without the current one.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpPost("portals/{portalId:int}/users/{userId:int}/password-reset")]
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
        int portalId,
        int userId,
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        // Validated by the globally registered filter; see the note on the constructor. The replacement
        // credential is forwarded and never inspected, hashed, echoed or logged here - and the new value is
        // not returned to the caller, unlike the legacy reset, which handed it back in clear text.
        Result outcome = await _users
            .ResetPasswordAsync(portalId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Clears a lockout so the account can be used again.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpPost("portals/{portalId:int}/users/{userId:int}/unlock")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UnlockAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .UnlockUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Approves or unapproves a user.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="isApproved">The approval state to set.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the approval state has been set.</returns>
    /// <response code="204">The approval state has been set.</response>
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
    /// meaningful if the caller stated which state they meant.
    /// </para>
    /// <para>
    /// MIGRATION: replaces the authorise and unauthorise commands of
    /// <c>Website/admin/Users/Membership.ascx.vb</c>, which set approval true (<c>:L199-L202</c>) or false
    /// (<c>:L243-L246</c>) on the membership object and then saved the whole account. Two commands whose only
    /// difference was one boolean become one address taking that boolean, and the precondition they each
    /// carried separately is now enforced in one place.
    /// </para>
    /// </remarks>
    [HttpPut("portals/{portalId:int}/users/{userId:int}/approval")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SetApprovalAsync(
        int portalId,
        int userId,
        [FromQuery] bool isApproved,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .SetUserApprovalAsync(portalId, userId, isApproved, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Requires the user to change their password at next sign-in.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpPost("portals/{portalId:int}/users/{userId:int}/require-password-change")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RequirePasswordChangeAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .RequirePasswordChangeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a portal's membership settings.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The membership settings.</returns>
    /// <response code="200">The tenant's membership settings.</response>
    /// <response code="404">No such portal, or it stores no membership settings.</response>
    /// <remarks>
    /// MIGRATION: replaces <c>Website/admin/Users/UserSettings.ascx.vb:L106</c>, which read the settings
    /// through <c>UserController.GetUserSettings(UserPortalID)</c> - a member returning an untyped
    /// <c>Hashtable</c> (<c>Library/Components/Users/UserController.vb:L656</c>) - and then walked it with a
    /// dictionary enumerator, splitting each key on an underscore to recover its grouping. One typed transfer
    /// object replaces the dictionary, so the available settings are declared rather than discovered. The
    /// address is keyed by the PORTAL alone: these are the tenant's settings, not any account's.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/membership-settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<MembershipSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MembershipSettingsDto?>>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<MembershipSettingsDto?> outcome = await _users
            .GetMembershipSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a portal's membership settings.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the settings have been stored.</returns>
    [HttpPut("portals/{portalId:int}/membership-settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateMembershipSettingsAsync(
        int portalId,
        [FromBody] MembershipSettingsDto settings,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .UpdateMembershipSettingsAsync(portalId, settings, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a user's profile.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
    [HttpGet("portals/{portalId:int}/users/{userId:int}/profile")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserProfileDto?>>> GetProfileAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Result<UserProfileDto?> outcome = await _users
            .GetProfileAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a user's profile values.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="profile">The values to store.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the profile has been stored.</returns>
    /// <response code="204">The profile values have been stored.</response>
    /// <response code="400">A required property was left empty, or a value was refused by its definition.</response>
    /// <response code="404">No such account in this portal, or the body named a property that is not defined.</response>
    /// <remarks>
    /// MIGRATION: replaces <c>Website/admin/Users/Profile.ascx.vb:L226</c>, which handed a whole
    /// property-definition collection to the profile controller and took a rebuilt account back. Here the
    /// body carries values only, the definitions stay a separate resource, and the per-property rules the
    /// legacy property editor enforced in the page are enforced in the service - which is why a refused value
    /// answers <c>400</c> while an undefined property answers <c>404</c>.
    /// </remarks>
    [HttpPut("portals/{portalId:int}/users/{userId:int}/profile")]
    [Authorize(Policy = PolicyNames.AccountOwnerOrPortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateProfileAsync(
        int portalId,
        int userId,
        [FromBody] UserProfileDto profile,
        CancellationToken cancellationToken)
    {
        Result outcome = await _users
            .UpdateProfileAsync(portalId, userId, profile, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
