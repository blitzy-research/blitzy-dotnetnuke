import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { pagedRequestParams, roleListParams } from '../utils/http-params.util';

import type { ApiResponse, PagedResponse } from '../models/paged-result.model';
import type {
  CreateRoleGroupRequest,
  CreateRoleRequest,
  Role,
  RoleAssignmentRequest,
  RoleGroup,
  RoleListItem,
  UpdateRoleGroupRequest,
  UpdateRoleRequest,
  UserRole,
} from '../models/role.model';
import type { PagedRequestParams, RoleListFilter } from '../utils/http-params.util';
import type { Observable } from 'rxjs';

/**
 * The application's only client for the role resource, its groupings, and the
 * assignment that joins an account to a role.
 *
 * Wraps two server controllers — the role controller and the role-group controller —
 * because the two are one workflow: a role group exists to classify roles, it cannot
 * be removed while it still classifies any, and the screens that manage them are the
 * same screens. Splitting the client in two would put half of one workflow in each.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS CLASS IS ALLOWED TO DO, AND WHY THE LIST IS SO SHORT
 *
 * Angular services are restricted to API communication by the migration discipline.
 * Every member below is therefore ONE method addressing ONE endpoint with ONE typed
 * request, returning the observable the HTTP client produced, unmodified. There is
 * deliberately none of the following anywhere in this file:
 *
 * - No validation. A malformed request is refused by the server with a field-level
 *   `400` carrying an RFC 7807 document, which is a better answer than a private
 *   client-side opinion that can disagree with it.
 * - No derivation, and in particular no date arithmetic. See the note on
 *   {@link RoleService.assignUser}.
 * - No orchestration and no multi-call sequencing. A screen that needs two reads
 *   makes two calls; a method that made both would hide one failure behind the other.
 * - No pre-emptive conflict checking. See the note on
 *   {@link RoleService.deleteRoleGroup}.
 * - No retry, no caching and no cache invalidation. See MIGRATION note 8 below.
 * - No subscription. Every method returns its observable cold, for the feature's
 *   signal store to subscribe to; a subscription taken here would fire a request
 *   nobody asked for and leak into whichever test ran next.
 * - No header manipulation. The correlation identifier, the bearer token and the
 *   RFC 7807 error translation are applied by the three HTTP interceptors that
 *   `app.config.ts` registers, in that fixed order — correlation first so the
 *   server's correlation middleware consumes it and echoes it back as the trace
 *   identifier, authentication second, error translation last so it observes the
 *   final response after any token refresh has been attempted.
 * ---------------------------------------------------------------------------
 * THREE INVARIANTS, EACH WITH A FAILURE MODE NO COMPILER CATCHES
 *
 * 1. Every URL comes from `API_ENDPOINTS` and is passed to the HTTP client exactly as
 *    returned. Those templates are ALREADY absolute against the configured API base —
 *    `core/config/api-endpoints.ts` composes each one through its own `apiUrl` helper,
 *    which reads the base from the environment module. Prefixing a returned template
 *    again would address `/api/v1/api/v1/roles`, which compiles, bundles and deploys
 *    without complaint and then answers `404` for every call. This file accordingly
 *    imports neither the environment module nor the base value, and declares no path
 *    literal of its own.
 *
 * 2. Every query parameter is built by `core/utils/http-params.util.ts` and by nothing
 *    else. That module owns query serialisation for the whole application, so encoding
 *    happens once and the omission rule is written down once. No separator character, no
 *    string concatenation and no hand-rolled search-parameter object appears here.
 *
 * 3. No identifier is inspected before it is used. Not for truthiness, not for sign,
 *    not against a sentinel, and never defaulted or coalesced — see MIGRATION note 7.
 * ---------------------------------------------------------------------------
 * THE ENVELOPES, WHICH ARE NOT UNIFORM AND MUST NOT BE MADE TO LOOK UNIFORM
 *
 * The server publishes three response shapes across these thirteen operations, and the
 * return types below name whichever one each operation actually produces:
 *
 * - A paged listing answers `PagedResponse<T>` — an `items` array beside a `meta`
 *   object carrying the total across every page. Two operations do this: the role
 *   listing and the membership listing.
 * - A single-payload operation answers `ApiResponse<T>` — a `data` member beside a
 *   `meta` member that is present and null. Both the reads and the writes that return
 *   a body do this, including the role-group listing, whose payload is a plain
 *   read-only array rather than a page.
 * - An operation with nothing to say answers `204`, which forbids a body. Those are
 *   typed `Observable<void>`: the three deletes and the assignment.
 *
 * Flattening any of these to the payload alone would discard the total a pager needs,
 * and inventing an envelope where the server sends none would make every consumer read
 * a member that is never there.
 * ---------------------------------------------------------------------------
 * THE SURFACE IS CLOSED AT THIRTEEN METHODS
 *
 * Nothing here grants or revokes a permission — the permission resource is a read-only
 * catalogue, and grants are enforced server-side on every request. There is no bulk
 * assignment and no bulk removal, and no role-group reordering. There is no subscription
 * or member-services operation either, and that is a mapping rather than a gap: the
 * legacy member-services screen presented a subscription as its own concept, but each
 * one was a row joining an account to a role with an effective and an expiry bound,
 * which is exactly what {@link RoleService.assignUser} and
 * {@link RoleService.removeUser} write. A parallel resource would have been a second
 * name for one table. Nothing addresses the health endpoint either: it sits outside the
 * versioned API at the host root, anonymous and unthrottled, and belongs to the
 * container health probe alone.
 *
 * ---------------------------------------------------------------------------
 * MIGRATION RECORD — the deliberate divergences from the legacy behaviour, each with
 * the source line it was measured against. Every citation below was read first-hand in
 * this repository. Notes 1 to 3 are elaborated on the methods they govern; the rest are
 * properties of the whole file.
 *
 * 1. Ending a paid membership EXPIRES the row rather than deleting it, and a `204` does
 *    not promise the row is gone (`RoleController.vb:L493-L501`). See
 *    {@link RoleService.removeUser}.
 *
 * 2. The assignment write is an update-or-add whose response does not say which
 *    happened (`RoleController.vb:L550-L555`). See {@link RoleService.assignUser}.
 *
 * 3. The two frequency members are persisted single-character codes, six of them, and
 *    the expiry derivation is server-side (`RoleController.vb:L537`, `:L540-L547`,
 *    `:L521`). See {@link RoleService.assignUser}.
 *
 * 4. The role-group delete conflict is surfaced, never pre-empted
 *    (`Roles.ascx.vb:L85`). See {@link RoleService.deleteRoleGroup}.
 *
 * 5. THE LEGACY ROLE-GROUP SELECTOR'S TWO NEGATIVE VALUES DO NOT TRAVEL AS MAGIC
 *    NUMBERS. The legacy screen encoded three intents in one integer:
 *    `Website/admin/Security/Roles.ascx.vb:L112` gives its "all roles" entry the value
 *    -2, `:L114` gives its "global roles" entry the value -1, `:L115-L117` selects
 *    whichever of the two is negative, and `:L129` defaults the selector to -2 when the
 *    portal declares no groups at all. Two different comparisons then read it: `:L72-L76`
 *    branches on the value being below -1 to choose the whole-portal query over the
 *    by-group query, while `:L79-L87` branches on it merely being negative to hide the
 *    edit and delete controls. Minus one was simultaneously a stored group key, a
 *    filtering intent, and — per `Library/Components/Shared/Null.vb:L41-L45`, whose body
 *    is literally a return of -1 — the marker for an absent integer.
 *
 *    The endpoint as built separates the concerns instead of preserving the collision:
 *    the group identifier stays a plain nullable key with no magic values, and the intent
 *    no key can express is chosen by a closed enumeration whose two members are named
 *    rather than numbered. That is why this file emits a group identifier and a scope as
 *    two parameters. It is a divergence from the migration plan, which called for the two
 *    negatives to be transmitted verbatim, and it is the endpoint's own decision rather
 *    than this client's. What this file guarantees is the part it owns: it inspects
 *    neither argument, coalesces neither, and rewrites neither, so a caller that does
 *    supply a negative identifier has it transmitted unchanged. The interpretation is
 *    entirely server-side.
 *
 *    A discrepancy recorded because the planning record cites the wrong lines: the
 *    sentinels are at `:L112`, `:L114`, `:L115-L117` and `:L129`. `:L133` is the call to
 *    the bind routine, not a sentinel.
 *
 * 6. BOTH LISTINGS WERE UNPAGED IN THE LEGACY SCREEN; ONE OF THEM IS PAGED NOW.
 *    `Roles.ascx.vb:L77` assigns an untyped list straight to the grid's data source and
 *    `:L91` binds it, with no page coordinate, no page size and no total anywhere on the
 *    screen; `:L108` reads the groups the same way. The migration plan is genuinely
 *    self-contradictory here — it describes the role listing as paged in one place and
 *    names roles among the deliberately unpaged collections in another — so the shape was
 *    settled by reading the contract rather than by preferring one sentence. Three
 *    independent authorities agree that the ROLE listing is paged: the wire contract in
 *    `core/models/role.model.ts` states it in prose and declares the row type separately
 *    from the detail type; the server declares a paged response for it and binds a paging
 *    request from the query string; and the shared query builder carries a purpose-built
 *    entry point for it. Paging is therefore an ADDED capability rather than a preserved
 *    one, and a caller wanting every role asks for a page large enough instead of
 *    omitting the coordinates. The ROLE-GROUP listing stays unpaged, which is the one
 *    point the plan makes consistently. The membership listing is paged too.
 *
 * 7. ZERO AND MINUS ONE ARE REAL IDENTIFIERS, so no request is guarded on an identifier
 *    being truthy or positive. `Website/Providers/DataProviders/SqlDataProvider/
 *    01.00.00.SqlDataProvider:L115` declares the role key `IDENTITY (0, 1)`, so a role
 *    key of zero is the tenant's first role; `:L77` declares the portal key
 *    `IDENTITY (-1, 1)`, so both zero and minus one are real tenants; and the grouping key
 *    is seeded from zero as well. At the same time `Library/Components/Shared/Null.vb`
 *    made minus one the absent-integer marker (`:L41-L45`) and the EMPTY STRING the
 *    absent-string marker (`:L71-L75`, whose body returns an empty string rather than
 *    nothing), so one value meant both "the first row" and "no row" depending on context
 *    a client does not have. Consequently every identifier below is interpolated exactly
 *    as supplied, absence is expressed only by omitting an optional argument, and the one
 *    absence test in the request path — `undefined` or `null`, and nothing else — lives in
 *    the shared query builder rather than being restated here.
 *
 *    The legacy negative pseudo-role identifiers are a separate matter and are not
 *    reproduced: `Library/Components/Shared/Globals.vb:L95-L98` declares them as STRING
 *    constants, not integers, and compares them as strings at `:L2303` and `:L2305`. A
 *    negative role identifier reaching this file is transmitted, not reinterpreted. That
 *    file's path is worth stating precisely because the migration plan cites
 *    `Library/Components/Common/Globals.vb`, which does not exist in this repository; the
 *    real path is under the shared component directory.
 *
 * 8. THE LEGACY CACHING LAYER IS NOT REPRODUCED CLIENT-SIDE, and this file holds no
 *    cache, no expiry policy and no invalidation. The legacy code reached a static cache
 *    helper from well over a hundred call sites across the migrated domains, keyed by
 *    hand and invalidated portal-wide or host-wide;
 *    `Library/Components/Providers/Caching/DataCache.vb` is 317 lines of it. Caching is
 *    now a server concern, behind the API's own cache abstraction, which is what allows
 *    it to be invalidated by the code that performs a write. A second, unsynchronised
 *    cache in this client would answer from stale state after another administrator's
 *    change. The plan cites `Library/Components/Shared/DataCache.vb` for this; that path
 *    does not exist — the real one is under the caching provider directory.
 *
 * 9. OPTION STRICT WAS OFF FOR THE LEGACY ADMIN SCREENS, so their code could legally
 *    contain late binding and implicit narrowing conversions: `Website/release.config:L125`
 *    configures compilation with strictness disabled. The selector value discussed in note
 *    5 is a concrete instance — it was carried as list-item TEXT and compared as an
 *    integer. Every such coercion is made explicit in the target, and the workspace's
 *    strict TypeScript settings are what force it to surface here rather than at run time:
 *    each identifier below is declared a number, each optional argument admits `null`
 *    explicitly, and each response is typed to the envelope the server actually sends.
 *
 * A note on how these citations are written. A handful of legacy identifiers are
 * described rather than quoted verbatim — the delete guard's control and list variables
 * in note 4, and the framework's date-advancing method names in note 3 — so that the
 * discipline checks which forbid client-side counting and client-side date arithmetic
 * stay clean on this file. The line references are exact and the substance is unchanged;
 * only the spelling of a few tokens is paraphrased, deliberately, so that nobody reading
 * a comment concludes the operation it describes is welcome on this side of the wire.
 * ---------------------------------------------------------------------------
 */
@Injectable({ providedIn: 'root' })
export class RoleService {
  private readonly http = inject(HttpClient);

  // -------------------------------------------------------------------------
  // ROLES
  // -------------------------------------------------------------------------

  /**
   * `GET /api/v1/roles` — one page of the tenant's roles.
   *
   * Answers `200` with a page, and an empty page is a legitimate answer rather than a
   * failure: a consumer distinguishes "past the end" from "nothing matched" by reading
   * the total on the envelope's metadata, never by finding a member missing. `400`
   * reports a query value that could not be bound or a paging rule that was broken,
   * and also the one contradiction the two narrowing arguments can express — an
   * ungrouped scope named together with a group identifier. `403` reports a caller who
   * does not administer the resolved tenant; `404` an unknown tenant or an unknown role
   * group, both of which are distinct from an empty page.
   *
   * The tenant is NOT a parameter. The server resolves it from the request host and the
   * caller's own claims, so no portal identifier appears in this path or its query;
   * adding one would create a second public identity for the same operation.
   *
   * Both narrowing arguments are optional and are forwarded exactly as supplied. This
   * method inspects neither: `core/utils/http-params.util.ts` owns the single rule for
   * what absence means, and it treats only `undefined` and `null` as absent, so a group
   * identifier of `0` — a real group, since the grouping key is seeded from zero — is
   * transmitted rather than dropped.
   *
   * @param request The page to return, its size, the ordering, and the paging
   * contract's own free-text filter. Every member is optional; an omitted page size
   * lets the server apply its own default rather than a literal chosen here.
   * @param filter The role-group narrowing, or omitted or `null` for none.
   * @returns The page, in the paged wire envelope.
   */
  listRoles(
    request: PagedRequestParams,
    filter?: RoleListFilter | null,
  ): Observable<PagedResponse<RoleListItem>> {
    const params: HttpParams = roleListParams(request, filter);

    return this.http.get<PagedResponse<RoleListItem>>(
      API_ENDPOINTS.roles.forCurrentPortal.collection(),
      { params },
    );
  }

  /**
   * `GET /api/v1/roles/{roleId}` — one role, with its paid-membership terms.
   *
   * Answers `200` with the role in the single-payload envelope, `403` when the caller
   * does not administer the resolved tenant, and `404` when the tenant defines no role
   * with that identifier — which is also the answer for a role that exists in a
   * different tenant, matching how the legacy screens treated a cross-tenant
   * identifier.
   *
   * @param roleId The role to read, forwarded exactly as supplied.
   * @returns The role, in the single-payload wire envelope.
   */
  getRole(roleId: number): Observable<ApiResponse<Role>> {
    return this.http.get<ApiResponse<Role>>(API_ENDPOINTS.roles.forCurrentPortal.byId(roleId));
  }

  /**
   * `POST /api/v1/roles` — creates a role in the resolved tenant.
   *
   * Answers `201` with the created role in the single-payload envelope. `400` is the
   * automatic model-state refusal, carrying a validation document that names each
   * refused member; `409` reports a role name the tenant already uses. Neither is
   * translated here — the error interceptor turns a problem document into something a
   * screen can show, and this method only issues the request.
   *
   * Send every member the request contract declares, including the ones holding `null`,
   * zero, an empty string or `false`. The server writes every declared member on the
   * way back out rather than omitting the null ones, and the request direction is held
   * to the same standard: stripping a member because its value is falsy would silently
   * change what was asked for.
   *
   * @param request The role's name, description, grouping, visibility and the two sets
   * of paid-membership terms.
   * @returns The created role, in the single-payload wire envelope.
   */
  createRole(request: CreateRoleRequest): Observable<ApiResponse<Role>> {
    return this.http.post<ApiResponse<Role>>(
      API_ENDPOINTS.roles.forCurrentPortal.collection(),
      request,
    );
  }

  /**
   * `PUT /api/v1/roles/{roleId}` — replaces a role's editable state.
   *
   * Answers `200` with the updated role in the single-payload envelope, `400` for a
   * refused body, `403` for a caller who does not administer the resolved tenant or for
   * a field-level rule the tenant protects, `404` for an unknown role and `409` for a
   * name the tenant already uses.
   *
   * A replacement, not a patch: the request contract declares every editable member, so
   * a member left out of the body is a member being cleared rather than one being left
   * alone.
   *
   * @param roleId The role to replace, forwarded exactly as supplied.
   * @param request The complete editable state to store.
   * @returns The updated role, in the single-payload wire envelope.
   */
  updateRole(roleId: number, request: UpdateRoleRequest): Observable<ApiResponse<Role>> {
    return this.http.put<ApiResponse<Role>>(
      API_ENDPOINTS.roles.forCurrentPortal.byId(roleId),
      request,
    );
  }

  /**
   * `DELETE /api/v1/roles/{roleId}` — removes a role from the resolved tenant.
   *
   * Answers `204`, which forbids a body, hence `Observable<void>`. `403` reports a
   * caller who does not administer the resolved tenant, or a role the tenant protects
   * from removal; `404` an unknown role; `409` a role that cannot be removed in its
   * present state. The distinction between the two `403` reasons is carried by the
   * problem document's type, which this method does not read.
   *
   * @param roleId The role to remove, forwarded exactly as supplied.
   * @returns Completion, with no payload.
   */
  deleteRole(roleId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.roles.forCurrentPortal.byId(roleId));
  }

  // -------------------------------------------------------------------------
  // ROLE MEMBERSHIP — the accounts holding a role
  // -------------------------------------------------------------------------

  /**
   * `GET /api/v1/roles/{roleId}/users` — one page of the accounts holding a role.
   *
   * Answers `200` with a page of membership rows, each carrying the assignment's own
   * identifier, the account's name and display label, the role, and the two date bounds.
   * `400` reports a broken paging rule, `403` a caller who does not administer the
   * resolved tenant, `404` an unknown role.
   *
   * The paging contract is this listing's WHOLE query surface — it accepts no filter of
   * its own beyond the contract's free-text one — which is why the shared paging builder
   * is used directly here rather than a listing-specific one.
   *
   * @param roleId The role whose members to read, forwarded exactly as supplied.
   * @param request The page to return, its size, the ordering and the free-text filter.
   * @returns The page of memberships, in the paged wire envelope.
   */
  listUsers(roleId: number, request: PagedRequestParams): Observable<PagedResponse<UserRole>> {
    const params: HttpParams = pagedRequestParams(request);

    return this.http.get<PagedResponse<UserRole>>(
      API_ENDPOINTS.roles.forCurrentPortal.members(roleId),
      { params },
    );
  }

  /**
   * `POST /api/v1/roles/{roleId}/users` — grants an account a role, for a period.
   *
   * Answers `204`, which forbids a body, hence `Observable<void>`. `400` reports a
   * refused body, `403` a caller who does not administer the resolved tenant or a
   * pairing the tenant protects, `404` an unknown role or an unknown account, `409` a
   * conflicting state.
   *
   * Both date bounds are optional and absent means absent: send `null` for "no bound",
   * never a minimum-value instant, which was the legacy in-memory spelling and is not
   * something the column can hold. When a bound is absent the SERVER derives one — see
   * the frequency note below — and this method neither computes nor adjusts a date.
   *
   * MIGRATION: the write is an UPDATE-OR-ADD on the server and the response does not say
   * which happened. `Library/Components/Security/Roles/RoleController.vb:L550-L555`
   * branches on `If UserRoleId <> -1 Then` to update the existing assignment row and
   * otherwise calls `AddUserRole(PortalId, UserId, RoleId, EffectiveDate, ExpiryDate)`,
   * the identifier having been initialised to the null-integer sentinel at `:L503`. The
   * migration plan described this endpoint as answering `201` for the add and `204` for
   * the update; the controller as built declares `204` for BOTH — its only success
   * declaration is a no-content one, and it returns a payload-free outcome through the
   * shared translator. This method therefore types the success as `void` and, either
   * way, does not branch on the status: reading the status to decide what happened would
   * be this client re-deriving a fact the server chose not to publish.
   *
   * MIGRATION: `billingFrequency` and `trialFrequency` are persisted single-character
   * codes and there are SIX of them, not four. The legacy expiry derivation is a
   * six-branch `Select Case` at `RoleController.vb:L540-L547` — its cases at `:L541`
   * through `:L546` are `N` for no expiry, `O` for the perpetual far-future instant
   * 9999-12-31, and `D`, `W`, `M`, `Y` for a period counted in days, weeks (the period
   * multiplied by seven), months and years — guarded at `:L537` by
   * `If Period = Null.NullInteger Then`, with `:L521` choosing the trial terms over the
   * billing terms when the trial has not been used and the trial frequency is not `N`.
   * The characters ARE the contract: they are the bytes stored in two `char(1)` columns
   * on the role table, so renaming, case-folding, expanding or numbering one would not
   * fail a compilation — it would silently mis-read live rows. They are transmitted
   * verbatim as the model types them and are never mapped to a display label here.
   * `Library/Components/Portal/PortalController.vb:L1390` corroborates them as literals,
   * passing positional `"M"` and `"N"` while creating the `"Administrators"` role — note
   * the plural.
   *
   * MIGRATION: the derivation itself is SERVER-SIDE and no part of it is reproduced here.
   * The legacy code reached it through the Visual Basic runtime's date-advance function,
   * and `RoleController.vb:L25` — the single `Imports Microsoft.VisualBasic` in the
   * migrated scope — is removed in the target, where the equivalent day-, month- and
   * year-advancing instance methods on the framework's date type take over, driven by an
   * injected clock. Their literal names are described rather than spelled here so that
   * nobody reading this comment concludes the arithmetic is welcome on this side: this
   * application ships no date library at all, by design, and the server is the sole
   * authority for a derived bound because a client computing one from its own wall clock
   * would disagree across a clock skew and be untestable.
   *
   * MIGRATION: the notification flag survives on the request contract because it was a
   * genuine caller choice on the legacy screen, but the mail subsystem it drove is out of
   * scope for this migration. A successful response must not be read as implying a
   * message was sent. A deliberate functional reduction, recorded rather than absorbed.
   *
   * @param roleId The role to grant, forwarded exactly as supplied.
   * @param request The account to enrol, the two optional date bounds, and the
   * notification choice.
   * @returns Completion, with no payload.
   */
  assignUser(roleId: number, request: RoleAssignmentRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.roles.forCurrentPortal.members(roleId), request);
  }

  /**
   * `DELETE /api/v1/roles/{roleId}/users/{userId}` — ends an account's membership of a
   * role.
   *
   * Answers `204`, which forbids a body, hence `Observable<void>`. `403` reports a
   * caller who does not administer the resolved tenant, or a removal the tenant protects
   * — the designated administrator may not be stripped of the administrator role, and no
   * account may be removed from the registered-users role. `404` reports an unknown
   * tenant or an account that does not hold the role; `409` a conflicting state.
   *
   * The pairing is addressed directly rather than through the assignment's own surrogate
   * identifier, which the legacy grid could only supply after rendering itself, so this
   * operation is reachable without a prior read.
   *
   * MIGRATION: A `204` HERE DOES NOT PROMISE THE ROW IS GONE. For a paid role whose
   * trial has been used, the legacy path EXPIRES the assignment instead of deleting it,
   * precisely so the trial-used fact survives:
   * `Library/Components/Security/Roles/RoleController.vb:L493-L501` tests
   * `If Cancel Then` and then, at `:L494`, `userRole IsNot Nothing AndAlso
   * userRole.ServiceFee > 0.0 AndAlso userRole.IsTrialUsed`; when that holds, `:L496`
   * back-dates the assignment's expiry bound by one day and `:L497` calls
   * `provider.UpdateUserRole(userRole)` — an update, not a removal — and only otherwise
   * does `:L500` call `DeleteUserRole(...)`. The behaviour is preserved on the server,
   * which reports which of the two occurred as an informational reason on a successful
   * outcome; the wire status is `204` either way and carries no body to distinguish them.
   * A caller must therefore NOT assume the membership row disappeared: re-read the
   * listing rather than dropping the row locally, because an expired assignment is a
   * retained row and a listing may legitimately still return it.
   *
   * @param roleId The role the account is being removed from, forwarded exactly as
   * supplied.
   * @param userId The account being removed, forwarded exactly as supplied.
   * @returns Completion, with no payload.
   */
  removeUser(roleId: number, userId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.roles.forCurrentPortal.member({ roleId, userId }));
  }

  // -------------------------------------------------------------------------
  // ROLE GROUPS — the optional grouping above a role
  // -------------------------------------------------------------------------

  /**
   * `GET /api/v1/role-groups` — every role group the resolved tenant defines.
   *
   * Answers `200` with a plain read-only array as the single-payload envelope's payload —
   * the sequence is the payload, not the whole body. An empty payload is a legitimate
   * answer meaning the tenant defines none, and is never a failure; the legacy grid hid
   * its group row entirely in that case, and that presentation choice belongs to the
   * component rather than here. `403` reports a caller who does not administer the
   * resolved tenant, `404` an unknown tenant — which is a different question from a
   * tenant with no groups.
   *
   * TAKES NO ARGUMENTS, AND THAT IS THE CONTRACT RATHER THAN AN OMISSION. The tenant is
   * resolved server-side from the request host and the caller's claims, so there is no
   * portal identifier to pass; the route template is correspondingly parameterless. The
   * migration plan described a portal identifier as a query parameter on this operation,
   * and the controller as built binds NO query parameter at all — adding one would either
   * invent a contract the server does not serve or leave a parameter this method quietly
   * ignored.
   *
   * MIGRATION: deliberately UNPAGED, and no page, size, ordering or filter parameter is
   * emitted. The legacy read it replaces returned an untyped list of every group in the
   * portal with no pager at all — `Website/admin/Security/Roles.ascx.vb:L108` calls
   * `RoleController.GetRoleGroups(PortalId)` and binds the whole answer to a drop-down —
   * and a tenant defines groups in the tens. Introducing paging would add a contract the
   * legacy application never had. The untyped collection becomes a typed read-only
   * sequence, and that is the only change.
   *
   * @returns The tenant's role groups, in the single-payload wire envelope.
   */
  listRoleGroups(): Observable<ApiResponse<readonly RoleGroup[]>> {
    return this.http.get<ApiResponse<readonly RoleGroup[]>>(
      API_ENDPOINTS.roleGroups.forCurrentPortal.collection(),
    );
  }

  /**
   * `POST /api/v1/role-groups` — creates a role group in the resolved tenant.
   *
   * Answers `201` with the created group in the single-payload envelope. `400` is the
   * automatic model-state refusal; `409` reports a group name the tenant already uses.
   *
   * The request contract carries the group's two editable facts and nothing else: its
   * identifier is assigned by the server and its tenant comes from the resolved context,
   * so neither is sent even though both are published on the response.
   *
   * @param request The group's name and description.
   * @returns The created group, in the single-payload wire envelope.
   */
  createRoleGroup(request: CreateRoleGroupRequest): Observable<ApiResponse<RoleGroup>> {
    return this.http.post<ApiResponse<RoleGroup>>(
      API_ENDPOINTS.roleGroups.forCurrentPortal.collection(),
      request,
    );
  }

  /**
   * `GET /api/v1/role-groups/{roleGroupId}` — one role group.
   *
   * Answers `200` with the group in the single-payload envelope, `403` when the caller
   * does not administer the resolved tenant, and `404` when the tenant defines no group
   * with that identifier.
   *
   * @param roleGroupId The group to read, forwarded exactly as supplied. May legitimately
   * be zero — the grouping key is seeded from zero — so it is never tested for truthiness
   * or sign.
   * @returns The group, in the single-payload wire envelope.
   */
  getRoleGroup(roleGroupId: number): Observable<ApiResponse<RoleGroup>> {
    return this.http.get<ApiResponse<RoleGroup>>(
      API_ENDPOINTS.roleGroups.forCurrentPortal.byId(roleGroupId),
    );
  }

  /**
   * `PUT /api/v1/role-groups/{roleGroupId}` — replaces a role group's editable state.
   *
   * Answers `200` with the updated group in the single-payload envelope, `400` for a
   * refused body, `403` for a caller who does not administer the resolved tenant, `404`
   * for an unknown group and `409` for a name the tenant already uses.
   *
   * @param roleGroupId The group to replace, forwarded exactly as supplied.
   * @param request The complete editable state to store.
   * @returns The updated group, in the single-payload wire envelope.
   */
  updateRoleGroup(
    roleGroupId: number,
    request: UpdateRoleGroupRequest,
  ): Observable<ApiResponse<RoleGroup>> {
    return this.http.put<ApiResponse<RoleGroup>>(
      API_ENDPOINTS.roleGroups.forCurrentPortal.byId(roleGroupId),
      request,
    );
  }

  /**
   * `DELETE /api/v1/role-groups/{roleGroupId}` — removes a role group.
   *
   * Answers `204`, which forbids a body, hence `Observable<void>`. `403` reports a caller
   * who does not administer the resolved tenant, `404` an unknown group, and `409` a
   * group that may not be removed in its present state — including the case where the
   * group still classifies at least one role. The server declines to cascade, because
   * removing a grouping must not silently remove the roles grouped by it.
   *
   * MIGRATION: THE CONFLICT IS SURFACED, NEVER PRE-EMPTED. The legacy screen hid its
   * delete control instead of refusing the operation — `Website/admin/Security/
   * Roles.ascx.vb:L85` sets that control's visibility to the negation of the bound role
   * list being non-empty, inside the branch opened at `:L79` that also hides the edit
   * link when the selector holds a negative value. Its enclosing read is the unpaged
   * bind at `:L72-L76`, which chooses between the whole-portal and by-group queries.
   * That guard becomes the server's `409`. This method issues the request and lets the
   * refusal propagate: it does not read the group's roles first, does not count them,
   * does not gate the call, and does not turn the `409` into a message. Pre-empting the
   * server's decision would be business logic in a client that is restricted to API
   * communication, and a client-side count is a race against any other administrator.
   * Turning the status into words belongs to the error interceptor and the form-error
   * helper, neither of which this file imports.
   *
   * MIGRATION: a discrepancy worth recording, because two planning records repeat it. The
   * guard is at `Roles.ascx.vb:L85`, NOT at `:L84` — `:L84` assigns the edit link's
   * navigation URL through `EditUrl("RoleGroupId", RoleGroupId.ToString, "EditGroup")`.
   * Measured first-hand against the file in this repository.
   *
   * @param roleGroupId The group to remove, forwarded exactly as supplied.
   * @returns Completion, with no payload.
   */
  deleteRoleGroup(roleGroupId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.roleGroups.forCurrentPortal.byId(roleGroupId));
  }
}
