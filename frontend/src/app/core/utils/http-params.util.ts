/**
 * Query-parameter serialisation for the dnn-migration administration front end.
 *
 * This module is the SINGLE place a query string is composed in this application.
 * `core/config/api-endpoints.ts` declares paths and nothing else — no template it
 * holds contains a question mark or an ampersand — so a caller that needs a
 * parameter appends it here. Nothing else may hand-roll one.
 *
 * It is a set of pure functions over immutable values. There is no class, no
 * decorator, no dependency injection, no module-level mutable state, and no request
 * is issued from here: `HttpParams` is a value type, and building one is not the
 * same as sending it. Path and base-URL composition belong to
 * `core/config/api-endpoints.ts`; this module never reads configuration.
 *
 * ---------------------------------------------------------------------------
 * THE ONE RULE THAT MATTERS MOST: OMISSION IS `undefined` OR `null`, NOTHING ELSE
 * ---------------------------------------------------------------------------
 * The obvious implementation of a parameter builder is wrong for this domain:
 *
 *     if (value) { params = params.set(name, value); }   // silently drops 0, -1, "" and false
 *
 * Every one of those four values is a legitimate, meaningful value here, and three
 * of them are legitimate PRIMARY KEYS. Measured in the legacy tree:
 *
 *   - `Library/Components/Shared/Null.vb` L41-L45 defines the integer "absent"
 *     marker as MINUS ONE, and L36-L40 defines the same marker for a 16-bit value.
 *     L71-L75 defines the string marker as the EMPTY STRING — its body is literally
 *     `Return ""`, not `Nothing` — and L76-L80 defines the boolean marker as
 *     `False`. That module is the codebase's null contract, not a vestige.
 *   - `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`
 *     L77 seeds the portal identity at MINUS ONE, so the seed and first generated value
 *     is -1, while the shipped default portal row is inserted explicitly with PortalID 0
 *     (L7125) — both are real keys. L115, L140 and L221 seed the role, page and
 *     module identities at ZERO, and seven further tables seed an item identity at
 *     zero (L44, L265, L278, L291, L302, L322, L336).
 *   - `Website/admin/Security/Roles.ascx.vb` L112 and L114 give the role-group
 *     filter the values -2 ("all roles") and -1 ("global roles"), and L129 defaults
 *     it to -2 when a portal declares no groups. L72-L76 branch on `< -1`, so both
 *     negatives steer real behaviour.
 *   - `Library/Components/Shared/Globals.vb` L95-L98 declare four further negative
 *     role identifiers — "-1" through "-4" — as STRING constants, compared through
 *     `Convert.ToString(RoleID)` at L2303 and L2305.
 *
 * So minus one is quintuply loaded, zero is an ordinary key, an empty filter is a
 * request a caller can genuinely make, and `false` is a real answer to a boolean
 * filter. A truthiness test would drop all four while looking idiomatic, and the
 * result would be a request for the wrong records that still returned 200.
 *
 * Every function below therefore decides omission with an explicit
 * `=== undefined` / `=== null` test and NOTHING ELSE, and stringifies what remains
 * explicitly rather than relying on coercion. The response side is close but not
 * identical, and the difference matters when reading a body: the server serialises with
 * camel casing and never elides a DEFAULT value — a zero, a minus one, an empty string
 * and a `false` all reach the wire — but it does elide a NULL one, because it is
 * configured with a when-writing-null ignore condition. A nullable member that is
 * absent therefore arrives as a MISSING PROPERTY rather than as `null`, so read absence
 * from the property not being there. Nothing about that changes the request side, which
 * omits `null` and `undefined` alike.
 *
 * ---------------------------------------------------------------------------
 * PAGING: ZERO-BASED, OFFSET-ONLY, AND NEVER GIVEN A DEFAULT HERE
 * ---------------------------------------------------------------------------
 * MIGRATION: the page index on the wire is ZERO-BASED, and this module performs no
 * arithmetic on it in either direction. The legacy screen counted from one —
 * `Website/admin/Users/Users.ascx.vb` L51 declares
 * `Private _CurrentPage As Integer = 1` — and subtracted one immediately before
 * every call down to the provider, at L265, L269, L271 and L274, exactly as
 * `Website/admin/Portal/Portals.ascx.vb` L142 did. The one-based counter survives
 * only as a presentation concern in `shared/components/pagination`, and the mapping
 * between the two bases is performed in the feature stores. Adding or subtracting a
 * page here would quietly serve the neighbouring page: no compiler and no assertion
 * on a successful status code would catch it.
 *
 * MIGRATION: the legacy URL keys were lower-case and one-based.
 * `Website/admin/Portal/Portals.ascx.vb` L215-L232 (`FilterURL`) emitted
 * `"filter=" & Filter` and `"currentpage=" & CurrentPage`, where `CurrentPage`
 * carried the one-based screen value while L142 handed the provider that value
 * minus one. The target parameters are camel-cased and zero-based — `pageIndex` and
 * `query` — so both the spelling and the base change, and both changes are
 * deliberate.
 *
 * MIGRATION: query strings were previously concatenated by hand.
 * `Website/admin/Users/Users.ascx.vb` L252 seeded `strQuerystring` with the legacy
 * empty-string marker, L254-L256 appended `"filter=" + SearchText`, and L275
 * appended `"&filterProperty=" + SearchField` — with no encoding anywhere, so a
 * search for text containing an ampersand or a per-cent sign produced a malformed
 * URL. `HttpParams` encodes, and it is the only mechanism used here.
 *
 * Paging is OFFSET paging. There is deliberately no cursor, no continuation token,
 * no next-page or previous-page link and no skip-and-take spelling: the server
 * accepts a page index and a page size, and introducing a different scheme would be
 * an unrequested behavioural change.
 *
 * NO PAGE SIZE IS DEFAULTED, SUBSTITUTED OR CLAMPED HERE. The effective size is a
 * per-portal setting: `Website/admin/Users/Users.ascx.vb` L114-L118 read it from the
 * `Records_PerPage` module setting, and `Library/Components/Users/UserModuleBase.vb`
 * L134-L136 seeded that setting with ten only when it was unset. Ten therefore lives
 * in exactly one place on the client, `core/models/paged-result.model.ts`, and the
 * server applies its own default when the parameter is absent. An omitted size is
 * forwarded as an omission.
 *
 * ---------------------------------------------------------------------------
 * TWO MATCH SEMANTICS, AND THIS MODULE DOES NOT DECORATE THE TEXT
 * ---------------------------------------------------------------------------
 * Match semantics are the SERVER's, and they differ by parameter. The general-purpose
 * free-text filters are SUBSTRING (contains) matches: the portal-name filter
 * (`PortalRepository.cs` L59), the account listing's general query across username,
 * display name and email (`UserRepository.cs` L131-L134), and the role and module
 * searches. Only the NAMED filters are PREFIX (starts-with) matches: the username
 * prefix, the email prefix and the profile-value prefix (`UserRepository.cs` L140,
 * L146, L574 and L587). Each member below states which of the two applies to it.
 *
 * The legacy readers appended a trailing per-cent sign AT THE CALL SITE —
 * `Website/admin/Users/Users.ascx.vb` L269, L271 and L274, and
 * `Website/admin/Portal/Portals.ascx.vb` L142 — so composing a pattern is the
 * repository's work, behind the repository interfaces, and never this module's. This
 * module transmits the caller's text exactly as supplied: it appends nothing, trims
 * nothing, changes no letter's case and performs no encoding of its own. Decorating the
 * value here would double the pattern the moment a second caller did the same, and
 * would leave the repository unable to tell a character a user typed from one a caller
 * added. Note that the server itself DOES trim and lower-case the value it received
 * before comparing, so matching is case-insensitive and insensitive to surrounding
 * whitespace without this module altering anything.
 *
 * MIGRATION: the legacy "no search applied" marker was the literal string `"None"`.
 * `Website/admin/Users/Users.ascx.vb` L266 reads `ElseIf SearchText <> "None"`, so
 * the screen carried a magic word through its own state to mean "do not filter".
 * The target sends NO parameter at all instead. A caller that wants no filter omits
 * the member; the literal is never transmitted.
 *
 * MIGRATION: the legacy screens branched on LOCALISED strings.
 * `Website/admin/Users/Users.ascx.vb` L258, L261 and L264 compared the search text
 * against `Localization.GetString("Unauthorized")`, `("OnLine")` and `("All")`, and
 * `Website/admin/Portal/Portals.ascx.vb` L138 compared a filter against
 * `Localization.GetString("Expired", LocalResourceFile)`. Control flow therefore
 * depended on the display language, and translating a resource file changed which
 * query ran. That pattern is not reproduced: every filter below is a named, typed
 * member, so no comparison against user-facing text can decide anything.
 *
 * MIGRATION: three of those localised pseudo-filters have NO target endpoint, so no
 * parameter is built for them and none should be invented.
 * `Website/admin/Users/Users.ascx.vb` L258-L263 served an unauthorised-accounts
 * listing and an accounts-online listing, both hiding the pager outright; the
 * online listing depended on the scheduling subsystem, which is out of the migrated
 * scope along with its purge job. `Website/admin/Portal/Portals.ascx.vb` L138-L140
 * served an expired-portals listing, likewise with the pager hidden, and no endpoint
 * in the API exposes it.
 *
 * MIGRATION: localisation itself is not carried forward. No translation runtime is
 * added to this workspace, so the legacy resource files are consulted only as the
 * authority for English wording in templates. This module holds no user-facing text
 * at all, so the decision has no further consequence here beyond the point above:
 * nothing that a translator can edit may influence a request.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS MODULE DELIBERATELY DOES NOT DO
 * ---------------------------------------------------------------------------
 * It does not decide whether a pager should be visible. The legacy rule was
 * identical on two screens — `Website/admin/Users/Users.ascx.vb` L278-L280 and
 * `Website/admin/Portal/Portals.ascx.vb` L155-L157 both read
 * `ctlPagingControl.Visible = (PageSize < TotalRecords)` — and it is presentation
 * logic belonging to `shared/components/pagination`, which receives the page, the
 * page size and the total and decides for itself.
 *
 * It does not validate. The server bounds the page size and the filter length in a
 * request validator and answers a violation with a field-level 400 carrying an
 * RFC 7807 problem document. Re-implementing those bounds here would give an HTTP
 * caller a different answer from every other caller, and the two copies would
 * eventually disagree. A non-finite number is likewise transmitted verbatim rather
 * than dropped, so the server reports it: dropping it would silently serve a
 * different page than the one asked for.
 */

import { HttpParams } from '@angular/common/http';

import type { PagedRequest, SortDirection } from '../models/paged-result.model';

/**
 * The scalar kinds this module serialises into a query string.
 *
 * Deliberately narrow. A date, an array and an object are all absent because no
 * endpoint in this API accepts one as a query parameter, and admitting them would
 * require this module to choose a format — an instant's representation, a
 * separator — which is a wire decision nobody has taken.
 */
export type QueryParamValue = string | number | boolean;

/**
 * A parameter value together with the two ways of expressing its ABSENCE.
 *
 * `undefined` and `null` mean "do not send this parameter" and are the ONLY values
 * that mean that. Zero, minus one, the empty string and `false` are values, not
 * absences — see the note at the head of this module for the measured reasons.
 */
export type OptionalQueryParamValue = QueryParamValue | null | undefined;

/**
 * A set of parameter names and values to serialise together.
 *
 * Read-only, because a serialiser has no business rewriting what a caller handed
 * it. A member whose value is `undefined` or `null` is omitted from the result; a
 * member whose value is zero, minus one, the empty string or `false` is sent.
 */
export type QueryParamMap = Readonly<Record<string, OptionalQueryParamValue>>;

/**
 * The paging, sorting and filtering arguments to serialise, with every member
 * optional so that ABSENCE is expressible.
 *
 * Derived from `PagedRequest` by a mapped type rather than restated, so it cannot
 * drift from the contract `core/models/paged-result.model.ts` owns. Two deliberate
 * relaxations: every member is optional, because a caller that expresses no page
 * size must be able to omit it and receive the server's own default rather than a
 * literal substituted here; and `null` is admitted alongside `undefined`, because a
 * reset form control yields `null` and both mean the same thing to a query string.
 *
 * A fully-populated `PagedRequest` is assignable to this type, so a caller holding
 * one passes it directly.
 */
export type PagedRequestParams = {
  readonly [K in keyof PagedRequest]?: PagedRequest[K] | null;
};

/**
 * The ordering half of {@link PagedRequestParams}, for a caller that composes a sort
 * separately from a page.
 *
 * Projected from {@link PagedRequestParams} rather than restated, for the same
 * anti-drift reason.
 */
export type SortParams = Pick<PagedRequestParams, 'sortBy' | 'sortDir'>;

/**
 * The exact spelling of every query parameter this API binds.
 *
 * Read off the eleven controllers rather than derived from a convention, because the
 * server binds camel-cased names and a misspelling produces no compilation error and
 * no runtime error either — it produces a parameter the binder ignores and a
 * silently unfiltered result. Use these constants; do not retype the strings.
 *
 * Frozen so the registry cannot be edited at run time, and `as const` so each member
 * is a string literal type rather than a widened `string`.
 *
 * Only QUERY parameters appear here. Route-segment identifiers — the portal, portal
 * alias, module, user, profile property definition, role and page identifiers — are
 * path components, and `core/config/api-endpoints.ts` owns those templates.
 */
export const QUERY_PARAM = Object.freeze({
  /** Zero-based page index. Bound from `PagedRequest.PageIndex`. */
  pageIndex: 'pageIndex',

  /** Requested page size. Bound from `PagedRequest.PageSize`. */
  pageSize: 'pageSize',

  /** Name of the field to order by. Bound from `PagedRequest.SortBy`. */
  sortBy: 'sortBy',

  /** Ordering direction. Bound from `PagedRequest.SortDir`. */
  sortDir: 'sortDir',

  /** Free-text SUBSTRING filter. Bound from `PagedRequest.Query`. */
  query: 'query',

  /** Portal-name SUBSTRING filter on the portal listing. */
  name: 'name',

  /** Account-name PREFIX filter on the account listing. */
  userName: 'userName',

  /** Address PREFIX filter on the account listing. */
  email: 'email',

  /** Profile property to filter the account listing by. */
  profilePropertyName: 'profilePropertyName',

  /** Value the profile property must begin with. */
  profilePropertyValue: 'profilePropertyValue',

  /** Approval-state filter on the account listing, and the state to set on approval. */
  isApproved: 'isApproved',

  /** Role-group filter on the role listing. */
  roleGroupId: 'roleGroupId',

  /**
   * Grouping scope on the role listing: which roles to consider when no group is named.
   *
   * This is the successor to the legacy selector's two NEGATIVE values, which a group
   * identifier cannot express. It is a name rather than a number precisely so that the
   * magic integers do not travel.
   */
  scope: 'scope',

  /** Page filter on the module listing. */
  tabId: 'tabId',

  /** Whether the module listing includes modules in the recycle bin. */
  includeDeleted: 'includeDeleted',

  /** Selects one module placement when a module appears on several pages. */
  tabModuleId: 'tabModuleId',

  /** Permission-code filter on the permission catalogue. */
  permissionCode: 'permissionCode',

  /** Module-definition filter on the permission catalogue. */
  moduleDefinitionId: 'moduleDefinitionId',

  /** Permission-key filter on the permission catalogue. */
  permissionKey: 'permissionKey',

  /** Portal being signed in to, when the request host matches no configured alias. */
  portalId: 'portalId',
} as const);

/**
 * Sets one query parameter, or leaves the set untouched when the value is absent.
 *
 * THE SINGLE OMISSION DECISION POINT of this module. Every other function delegates
 * here, so the rule that only `undefined` and `null` mean "absent" is stated once and
 * cannot be applied inconsistently.
 *
 * `HttpParams` is immutable, so a new instance is returned and the argument is never
 * modified. The parameter is SET rather than appended, which makes the call
 * idempotent: composing the same name twice yields one occurrence carrying the later
 * value, not two occurrences the server would have to choose between.
 *
 * The value is stringified EXPLICITLY. Relying on implicit coercion is what produces
 * the literal text `undefined` in a URL, and relying on truthiness is what silently
 * discards zero, minus one, the empty string and `false` — each of which is a real
 * value in this domain.
 *
 * A number is passed through as written, including a non-finite one. That is
 * deliberate: the server rejects an unbindable value with a field-level 400, whereas
 * dropping it here would send a request for a different page than the caller asked
 * for and report success.
 *
 * @param params The parameter set to extend.
 * @param name The parameter name. Use a member of {@link QUERY_PARAM}.
 * @param value The value to send, or `undefined` or `null` to send nothing.
 * @returns A parameter set carrying the value, or `params` unchanged when absent.
 * @throws {RangeError} The name is empty or entirely white space. Such a name would
 * serialise as a nameless assignment that a server cannot bind, so it fails loudly
 * here rather than producing a malformed URL a caller would have to reverse-engineer.
 *
 * @example
 * ```ts
 * // Sent: the portal identity is seeded at minus one, so -1 is a real portal.
 * setQueryParam(new HttpParams(), QUERY_PARAM.portalId, -1).toString(); // 'portalId=-1'
 *
 * // Sent: the role identity is seeded at zero.
 * setQueryParam(new HttpParams(), QUERY_PARAM.roleGroupId, 0).toString(); // 'roleGroupId=0'
 *
 * // Sent: false is an answer, not an absence.
 * setQueryParam(new HttpParams(), QUERY_PARAM.isApproved, false).toString(); // 'isApproved=false'
 *
 * // Not sent.
 * setQueryParam(new HttpParams(), QUERY_PARAM.tabId, undefined).toString(); // ''
 * ```
 */
export function setQueryParam(
  params: HttpParams,
  name: string,
  value: OptionalQueryParamValue,
): HttpParams {
  if (name.trim().length === 0) {
    throw new RangeError(
      'A query parameter name is required and must not be blank. Use a member of QUERY_PARAM.',
    );
  }

  // The ONLY test for absence. Not truthiness, not a sign test, not a comparison
  // against a sentinel: zero, minus one, the empty string and false all pass through.
  if (value === undefined || value === null) {
    return params;
  }

  // Explicit conversion of a value already narrowed to a non-nullish scalar. A
  // string reaches the wire byte for byte, a number as written, a boolean as the
  // lower-case token the server's boolean binder accepts.
  return params.set(name, String(value));
}

/**
 * Sets every named value in a map, skipping the members that are absent.
 *
 * Iteration order follows the map's own key order, which affects only the order the
 * pairs appear in the serialised string and never how the server binds them.
 *
 * The absence test is inherited from {@link setQueryParam}, so a member holding zero,
 * minus one, the empty string or `false` is sent. There is deliberately no
 * truthiness filter over the entries — that shortcut is exactly the defect this
 * module exists to prevent.
 *
 * @param params The parameter set to extend.
 * @param values The names and values to serialise.
 * @returns A parameter set carrying every present value.
 * @throws {RangeError} Any name in the map is empty or entirely white space.
 */
export function appendQueryParams(params: HttpParams, values: QueryParamMap): HttpParams {
  let result: HttpParams = params;

  for (const [name, value] of Object.entries(values)) {
    result = setQueryParam(result, name, value);
  }

  return result;
}

/**
 * Builds a parameter set from a map, starting from an empty one.
 *
 * The general-purpose entry point, and the one to reach for when serialising a
 * parameter this module does not yet name a dedicated function for.
 *
 * @param values The names and values to serialise.
 * @returns A parameter set carrying every present value.
 * @throws {RangeError} Any name in the map is empty or entirely white space.
 */
export function toHttpParams(values: QueryParamMap): HttpParams {
  return appendQueryParams(new HttpParams(), values);
}

/**
 * Builds an empty parameter set: the explicit way to say "this endpoint takes no
 * query parameters".
 *
 * Several collections in this API are DELIBERATELY UNPAGED and accept nothing at
 * all — the role groups, the portal aliases, the profile property definitions, the
 * module definitions, the permission catalogue and the page tree. None of them may be
 * sent a page index, a page size, a sort field or a sort direction: they are small,
 * bounded reference data returned whole, exactly as the legacy screens returned them.
 * `Website/admin/Security/Roles.ascx.vb` L108 read the role groups with no page
 * coordinate at all, and L72-L77 bound the role grid from a plain untyped list.
 *
 * A new instance is returned on every call rather than a shared constant. `HttpParams`
 * is immutable so sharing would be safe, but a function keeps this module free of
 * any state created at import time.
 *
 * @returns A parameter set carrying nothing.
 */
export function emptyQueryParams(): HttpParams {
  return new HttpParams();
}

/**
 * Sets the two page coordinates, omitting either one that is absent.
 *
 * The page index is transmitted exactly as supplied. It is ZERO-BASED on the wire and
 * no arithmetic is applied in either direction — see the note at the head of this
 * module for why that base is the data layer's and why translating it here would
 * quietly serve the neighbouring page.
 *
 * An absent page size is forwarded as an absence so the server applies its own
 * default. No literal is substituted and no bound is imposed: the effective size is a
 * per-portal setting, and the one place ten appears on the client is
 * `core/models/paged-result.model.ts`.
 *
 * @param params The parameter set to extend.
 * @param request The page of records to return, and the size of the page.
 * @returns A parameter set carrying whichever coordinates were supplied.
 */
export function setPagingParams(params: HttpParams, request: PagedRequestParams): HttpParams {
  const withIndex: HttpParams = setQueryParam(params, QUERY_PARAM.pageIndex, request.pageIndex);

  return setQueryParam(withIndex, QUERY_PARAM.pageSize, request.pageSize);
}

/**
 * Sets the ordering field and direction, omitting either one that is absent.
 *
 * The direction token is transmitted VERBATIM. `SortDirection` holds the server
 * enumeration's own member names, and the query-string binder accepts nothing else: an
 * abbreviated or lower-cased spelling is answered with 400 and
 * `The value 'asc' is not valid for SortDir.` The local annotation below states that
 * what reaches the wire is exactly one of those tokens, which is why no mapping table
 * exists here and why none should be added.
 *
 * A direction without a field is still transmitted when supplied. The server consults
 * it only when a field is present but retains it either way, so discarding it here
 * would lose a caller's setting while they were still choosing a field.
 *
 * @param params The parameter set to extend.
 * @param sort The field to order by and the direction to order it in.
 * @returns A parameter set carrying whichever ordering members were supplied.
 */
export function setSortParams(params: HttpParams, sort: SortParams): HttpParams {
  // Named explicitly so the contract is visible at the point of use: the value handed
  // to the query string is one of the server enumeration's own member names.
  const direction: SortDirection | null | undefined = sort.sortDir;

  const withField: HttpParams = setQueryParam(params, QUERY_PARAM.sortBy, sort.sortBy);

  return setQueryParam(withField, QUERY_PARAM.sortDir, direction);
}

/**
 * Sets every member of the paging contract: both page coordinates, the ordering, and
 * the free-text filter.
 *
 * The filter text is passed through UNCHANGED by this module — not trimmed, not
 * case-folded and not decorated. The server applies a SUBSTRING match for this
 * general-purpose filter, and trims and lower-cases the value before comparing;
 * composing any pattern is the repository's work. See the note at the head of this
 * module for the full split. An explicitly
 * supplied empty filter is therefore transmitted as an empty filter rather than being
 * reinterpreted as no filter, because reinterpreting it would be this module deciding
 * a question the server already answers.
 *
 * This is the whole query surface of the role-users listing, which accepts the paging
 * contract and nothing else.
 *
 * @param params The parameter set to extend.
 * @param request The page of records to return, the size of the page, the ordering and
 * the filter.
 * @returns A parameter set carrying whichever members were supplied.
 */
export function setPagedRequestParams(
  params: HttpParams,
  request: PagedRequestParams,
): HttpParams {
  const withPaging: HttpParams = setPagingParams(params, request);
  const withSort: HttpParams = setSortParams(withPaging, request);

  return setQueryParam(withSort, QUERY_PARAM.query, request.query);
}

/**
 * Builds the paging contract as a parameter set of its own.
 *
 * The convenience form of {@link setPagedRequestParams} for a caller with no existing
 * parameters to extend, and the complete query surface of the role-users listing.
 *
 * @param request The page of records to return, the size of the page, the ordering and
 * the filter.
 * @returns A parameter set carrying whichever members were supplied.
 */
export function pagedRequestParams(request: PagedRequestParams): HttpParams {
  return setPagedRequestParams(new HttpParams(), request);
}

/**
 * The filter the portal listing accepts in addition to the paging contract.
 *
 * MIGRATION: the legacy screen had ONE filter box whose text went straight into a
 * trailing-wildcard match on the portal name (`Website/admin/Portal/Portals.ascx.vb`
 * L142). The endpoint keeps that as a dedicated `name` filter, separate from the paging
 * contract's general-purpose filter, so a caller states which of the two it means
 * instead of relying on the server to guess.
 */
export interface PortalListFilter {
  /**
   * Restricts the result to portals whose name CONTAINS this text.
   *
   * The server performs a substring match, case-insensitively and after trimming
   * (`PortalRepository.cs` L58-L59). Supplied raw from here: no per-cent sign is
   * appended, because the repository composes any pattern it needs.
   */
  readonly name?: string | null;
}

/**
 * Builds the query parameters for the portal listing.
 *
 * @param request The page of records to return, the size of the page, the ordering and
 * the paging contract's own filter.
 * @param filter The portal-name substring filter, or omitted or `null` to list every
 * portal. `null` is admitted because a reset filter control yields it and it means the
 * same thing as omission.
 * @returns The parameters to send.
 */
export function portalListParams(
  request: PagedRequestParams,
  filter?: PortalListFilter | null,
): HttpParams {
  const params: HttpParams = pagedRequestParams(request);

  if (filter === undefined || filter === null) {
    return params;
  }

  return setQueryParam(params, QUERY_PARAM.name, filter.name);
}

/**
 * The filters the account listing accepts in addition to the paging contract.
 *
 * MIGRATION: this models the legacy screen's THREE distinct search modes as three
 * distinct members rather than collapsing them into one opaque filter.
 * `Website/admin/Users/Users.ascx.vb` L267-L276 switched on a search-field value that
 * was either the literal `"Email"`, the literal `"Username"`, or the name of an
 * arbitrary profile property, and called a different reader for each — L269, L271 and
 * L274 respectively. The screen then carried the property name onward as a separate
 * URL pair, appending `"&filterProperty=" + SearchField` at L275; the target spells
 * that pair `profilePropertyName`, and pairs it with `profilePropertyValue` so the
 * property being matched and the value it must match are stated independently.
 *
 * Which combinations are legal is the SERVER'S rule, and in particular a property name
 * supplied without a value is reported by the server as a conflict rather than treated
 * as "any value". This type therefore constrains no combination: re-implementing that
 * rule here would give this caller a different answer from every other caller, and the
 * two copies would eventually disagree.
 */
export interface UserListFilter {
  /**
   * Restricts the result to accounts whose name BEGINS WITH this text. The legacy
   * `"Username"` search mode.
   */
  readonly userName?: string | null;

  /**
   * Restricts the result to accounts whose address BEGINS WITH this text. The legacy
   * `"Email"` search mode.
   */
  readonly email?: string | null;

  /**
   * The profile property to match on: the legacy arbitrary-property search mode, whose
   * property name the screen carried as its own URL pair.
   */
  readonly profilePropertyName?: string | null;

  /**
   * The value {@link UserListFilter.profilePropertyName} must BEGIN WITH. Supplied
   * raw; no per-cent sign is appended here.
   */
  readonly profilePropertyValue?: string | null;

  /**
   * Restricts the result by approval state.
   *
   * `false` is a REQUEST FOR THE UNAPPROVED ACCOUNTS, not an absence, and is
   * transmitted as such. Omit the member to place no approval restriction.
   */
  readonly isApproved?: boolean | null;
}

/**
 * Builds the query parameters for a portal's account listing.
 *
 * The portal is a route segment rather than a parameter, so it does not appear here.
 *
 * @param request The page of records to return, the size of the page, the ordering and
 * the paging contract's own filter.
 * @param filter The search mode and approval restriction, or omitted or `null` to list
 * every account in the portal.
 * @returns The parameters to send.
 */
export function userListParams(
  request: PagedRequestParams,
  filter?: UserListFilter | null,
): HttpParams {
  const params: HttpParams = pagedRequestParams(request);

  if (filter === undefined || filter === null) {
    return params;
  }

  return appendQueryParams(params, {
    [QUERY_PARAM.userName]: filter.userName,
    [QUERY_PARAM.email]: filter.email,
    [QUERY_PARAM.profilePropertyName]: filter.profilePropertyName,
    [QUERY_PARAM.profilePropertyValue]: filter.profilePropertyValue,
    [QUERY_PARAM.isApproved]: filter.isApproved,
  });
}

/**
 * Builds the query parameter that sets an account's approval state.
 *
 * The state is a REQUIRED argument, not an optional one, and `false` is transmitted as
 * `false`. The endpoint takes the desired state explicitly rather than offering two
 * verbs, because the server reports setting the state an account already holds as a
 * conflict — an answer that is only meaningful if the caller said which state it meant.
 *
 * @param isApproved The approval state to set.
 * @returns The parameters to send.
 */
export function userApprovalParams(isApproved: boolean): HttpParams {
  return setQueryParam(new HttpParams(), QUERY_PARAM.isApproved, isApproved);
}

/**
 * The filter the role listing accepts in addition to the paging contract.
 */
export interface RoleListFilter {
  /**
   * Restricts the result to one role group.
   *
   * BOTH NEGATIVE VALUES ARE MEANINGFUL and are transmitted when supplied.
   * `Website/admin/Security/Roles.ascx.vb` L112 gives the "all roles" choice the value
   * -2 and L114 gives the "global roles" choice the value -1; L129 defaults the filter
   * to -2 when a portal declares no groups, and L72-L76 branch on `< -1`, so the two
   * negatives select different queries. Minus one is additionally the only negative
   * group identifier the legacy schema ever persisted. Omit the member to place no
   * group restriction; do not express that by sending a negative number.
   */
  readonly roleGroupId?: number | null;

  /**
   * Which roles to consider when `roleGroupId` names no group.
   *
   * `'All'` lists every role in the portal whatever its grouping - the legacy
   * "&lt; All Roles &gt;" choice, and what the API assumes when the member is omitted.
   * `'Ungrouped'` lists only the roles belonging to no group at all - the legacy
   * "&lt; Global Roles &gt;" choice, which is the intent no group identifier can express.
   *
   * Sending `'Ungrouped'` TOGETHER WITH a `roleGroupId` is a contradiction and the API
   * refuses it as a bad request rather than preferring one of the two. Sending `'All'`
   * together with a group identifier is not a contradiction: the identifier still selects
   * its group.
   */
  readonly scope?: RoleGroupScope | null;
}

/**
 * The grouping scopes the role listing accepts.
 *
 * Spelled exactly as the API's closed enumeration names them, because the value travels as
 * a name and an unrecognised spelling is refused by model binding before the request runs.
 * A string union rather than a numeric one for the same reason: the legacy equivalent was a
 * bare integer whose unrecognised values fell silently into whichever magic band contained
 * them.
 */
export type RoleGroupScope = 'All' | 'Ungrouped';

/**
 * Builds the query parameters for a portal's role listing.
 *
 * MIGRATION: this listing is PAGED, and that is a divergence from the migration plan,
 * which lists roles among the deliberately unpaged collections. The endpoint as built
 * accepts the paging contract, so the parameters are built for it. The legacy screen
 * was genuinely unpaged — `Website/admin/Security/Roles.ascx.vb` L72-L77 bound its grid
 * from a plain untyped list with no page coordinate and no total — so paging is an
 * added capability here rather than a preserved one, and a caller that wants every role
 * asks for a page large enough rather than omitting the coordinates.
 *
 * @param request The page of records to return, the size of the page, the ordering and
 * the paging contract's own filter.
 * @param filter The role-group restriction, or omitted or `null` to list every role in
 * the portal.
 * @returns The parameters to send.
 */
export function roleListParams(
  request: PagedRequestParams,
  filter?: RoleListFilter | null,
): HttpParams {
  const params: HttpParams = pagedRequestParams(request);

  if (filter === undefined || filter === null) {
    return params;
  }

  const scoped: HttpParams = setQueryParam(params, QUERY_PARAM.roleGroupId, filter.roleGroupId);

  return setQueryParam(scoped, QUERY_PARAM.scope, filter.scope);
}

/**
 * The filters the module listing accepts in addition to the paging contract.
 */
export interface ModuleListFilter {
  /**
   * Restricts the result to modules placed on one page.
   *
   * The page identity is seeded at ZERO, so `0` addresses a real page and is
   * transmitted. Omit the member to place no page restriction.
   */
  readonly tabId?: number | null;

  /**
   * Includes modules that are in the recycle bin.
   *
   * `false` is transmitted as `false` when supplied. Omitting the member is equivalent,
   * because the server binds a missing boolean to `false`, but the two are kept
   * distinguishable here so a caller can state the restriction explicitly.
   */
  readonly includeDeleted?: boolean | null;
}

/**
 * Builds the query parameters for a portal's module listing.
 *
 * @param request The page of records to return, the size of the page, the ordering and
 * the paging contract's own filter.
 * @param filter The page and recycle-bin restrictions, or omitted or `null` to list the
 * portal's live modules.
 * @returns The parameters to send.
 */
export function moduleListParams(
  request: PagedRequestParams,
  filter?: ModuleListFilter | null,
): HttpParams {
  const params: HttpParams = pagedRequestParams(request);

  if (filter === undefined || filter === null) {
    return params;
  }

  return appendQueryParams(params, {
    [QUERY_PARAM.tabId]: filter.tabId,
    [QUERY_PARAM.includeDeleted]: filter.includeDeleted,
  });
}

/**
 * Selects one placement of a module that appears on several pages.
 *
 * Accepted by the module read, the module deletion and both module-settings operations.
 * On a deletion it narrows the effect to that single placement, leaving the module on
 * its other pages — so omitting it and supplying it are materially different requests,
 * and the distinction must not be blurred by a default.
 */
export interface ModulePlacementSelector {
  /**
   * The placement to address, or omitted to address the module itself.
   *
   * A placement identity is seeded at 1 in the legacy schema, but no lower bound is
   * assumed here: the value is forwarded exactly as supplied.
   */
  readonly tabModuleId?: number | null;
}

/**
 * Builds the query parameters that select one module placement.
 *
 * @param selector The placement to address, or omitted or `null` to address the module
 * itself.
 * @returns The parameters to send, empty when no placement was named.
 */
export function modulePlacementParams(selector?: ModulePlacementSelector | null): HttpParams {
  if (selector === undefined || selector === null) {
    return new HttpParams();
  }

  return setQueryParam(new HttpParams(), QUERY_PARAM.tabModuleId, selector.tabModuleId);
}

/**
 * The filters the permission catalogue accepts.
 *
 * The catalogue is DELIBERATELY UNPAGED — small, bounded reference data seeded by the
 * upgrade scripts and returned whole — so no page coordinate, ordering or paging
 * filter belongs on this request.
 */
export interface PermissionListFilter {
  /**
   * Restricts the result to one permission code.
   *
   * Supplied raw. The server neither trims nor case-folds it, and neither does this
   * module: a blank code is a filter the server itself adjudicates.
   */
  readonly permissionCode?: string | null;

  /**
   * Restricts the result to the permissions of one module definition.
   *
   * The module-definition identity is seeded at 1, but the value is forwarded exactly
   * as supplied and is never compared against a sentinel.
   */
  readonly moduleDefinitionId?: number | null;

  /**
   * Restricts the result to one permission key.
   *
   * Spelled exactly as the API's closed key enumeration names it, because the value travels
   * as a name and an unrecognised spelling is refused by model binding before the request
   * runs. Combined with `permissionCode` this is the legacy code-and-key lookup.
   */
  readonly permissionKey?: PermissionKeyName | null;
}

/**
 * The permission keys the catalogue filter accepts.
 *
 * The four members are the whole vocabulary the catalogue's key column holds; the same
 * spellings appear in an access token's permission claims, so one concept never travels two
 * ways.
 */
export type PermissionKeyName = 'VIEW' | 'EDIT' | 'READ' | 'WRITE';

/**
 * Builds the query parameters for the permission catalogue.
 *
 * @param filter The code, module-definition and key restrictions, or omitted or `null` to
 * read the whole catalogue.
 * @returns The parameters to send, empty when no restriction was named.
 */
export function permissionListParams(filter?: PermissionListFilter | null): HttpParams {
  if (filter === undefined || filter === null) {
    return new HttpParams();
  }

  return appendQueryParams(new HttpParams(), {
    [QUERY_PARAM.permissionCode]: filter.permissionCode,
    [QUERY_PARAM.moduleDefinitionId]: filter.moduleDefinitionId,
    [QUERY_PARAM.permissionKey]: filter.permissionKey,
  });
}

/**
 * Names the portal a sign-in addresses when the request host matches no configured
 * portal alias.
 *
 * The server resolves the portal from the request host first and consults this
 * parameter only when that resolution produced nothing. The precedence matters: an
 * account exists within one portal, so signing in to the wrong one is refused with the
 * same generic denial as a wrong password.
 */
export interface LoginPortalSelector {
  /**
   * The portal being signed in to, or omitted when the host resolves it.
   *
   * The portal identity is seeded at MINUS ONE, so -1 names the first portal and 0 the
   * second. Both are transmitted; neither is an absence.
   */
  readonly portalId?: number | null;
}

/**
 * Builds the query parameters for a sign-in request.
 *
 * The credential travels in the request body; only the portal selection is a
 * parameter. The remaining authentication endpoints — the refresh, the sign-out and the
 * caller-identity read — accept no query parameters at all, so use
 * {@link emptyQueryParams} or send none.
 *
 * @param selector The portal being signed in to, or omitted or `null` when the request
 * host resolves it.
 * @returns The parameters to send, empty when no portal was named.
 */
export function loginParams(selector?: LoginPortalSelector | null): HttpParams {
  if (selector === undefined || selector === null) {
    return new HttpParams();
  }

  return setQueryParam(new HttpParams(), QUERY_PARAM.portalId, selector.portalId);
}

/*
 * ===========================================================================
 *  WHICH ENDPOINTS TAKE QUERY PARAMETERS, AND WHICH TAKE NONE
 * ===========================================================================
 * Measured endpoint by endpoint against the eleven controllers on this branch, not
 * inferred from the plan. A dedicated function above exists for each entry in the
 * first list; the second list is why several of them do not.
 *
 * Roles, role groups, profile definitions, modules and accounts each have one canonical
 * flat address. Their tenant is the portal the request host resolves to, never a path or
 * query parameter. Portal aliases and the page collection are the deliberate
 * portal-nested exceptions.
 *
 *   ACCEPT QUERY PARAMETERS
 *     GET    /portals ................................. paging contract + name
 *     GET    /users .................................... paging contract + userName,
 *                                                       email, profilePropertyName,
 *                                                       profilePropertyValue, isApproved
 *     PUT    /users/{userId}/approval ................. isApproved (required)
 *     GET    /roles .................................... paging contract + roleGroupId,
 *                                                       scope
 *     GET    /roles/{roleId}/users .................... paging contract only
 *     GET    /modules .................................. paging contract + tabId,
 *                                                       includeDeleted
 *     GET    /modules/{moduleId} ...................... tabModuleId
 *     DELETE /modules/{moduleId} ...................... tabModuleId
 *     GET    /modules/{moduleId}/settings ............. tabModuleId
 *     PUT    /modules/{moduleId}/settings ............. tabModuleId
 *     GET    /permissions ............................. permissionCode,
 *                                                       moduleDefinitionId,
 *                                                       permissionKey
 *     POST   /auth/login .............................. portalId
 *
 *   ACCEPT NO QUERY PARAMETERS AT ALL - send nothing, or emptyQueryParams()
 *     GET    /module-definitions ...... the tenant is resolved from the request host
 *     GET    /module-definitions/{moduleDefinitionId}
 *     GET    /module-definitions/desktop-modules/{desktopModuleId}
 *     GET    /permissions/{permissionId}
 *     GET    /profile-definitions ..................... unpaged reference data
 *     GET    /role-groups ............................. unpaged reference data
 *     GET    /portals/{portalId}/tabs .................... the page tree, unpaged
 *     GET|PUT /tabs/{tabId}
 *     GET    /portals/{portalId}/aliases
 *     POST   /auth/refresh, POST /auth/logout, GET /auth/me
 *     every single-record read, create, update and delete addressed by its identifier
 *
 * DIVERGENCES FROM THE PLANNED SURFACE, recorded rather than silently absorbed. The
 * plan names a handful of query parameters that the API as built does not bind, and
 * omits two that it does. Nothing was invented to close the gap: a function for an
 * endpoint that does not exist would compile perfectly and fail at run time.
 *
 *   - The module-definition catalogue no longer has only ONE read. A definition is now
 *     addressable by its own identifier, and a package's definitions by the package
 *     identifier, both as ROUTE SEGMENTS rather than as query parameters - so neither
 *     needs a function here. All three reads resolve the tenant from the request host and
 *     accept no query parameter of any kind.
 *   - The permission catalogue binds permissionCode, moduleDefinitionId AND permissionKey.
 *     The legacy module- and page-keyed catalogue helpers are not published as HTTP child
 *     resources.
 *   - The profile-definition and role-group listings resolve the portal from the request
 *     host rather than accepting it as a route or query parameter.
 *   - The portal listing has a dedicated name filter over and above the paging
 *     contract's own filter, so a caller states which of the two it means.
 *   - includeDeleted (module listing) and tabModuleId (module read, delete and both
 *     settings operations) are bound by the API and appear above.
 *
 * The remaining identifiers in the planned spelling list - the portal alias, module,
 * account, profile property definition, role and permission identifiers, and the
 * desktop module identifier - are route components or do not exist as parameters.
 * `core/config/api-endpoints.ts` owns route templates; this module owns query strings,
 * and the two do not overlap.
 */
