import { HttpParams } from '@angular/common/http';

import type { PagedRequest, SortDirection } from '../models/paged-result.model';

/** The scalar kinds this module serialises into a query string. */
export type QueryParamValue = string | number | boolean;

/**
 * A parameter value together with the two ways of expressing its ABSENCE. `undefined` and `null` mean "do
 * not send this parameter" and are the ONLY values that mean that.
 */
export type OptionalQueryParamValue = QueryParamValue | null | undefined;

/** A set of parameter names and values to serialise together. */
export type QueryParamMap = Readonly<Record<string, OptionalQueryParamValue>>;

/**
 * The paging, sorting and filtering arguments to serialise, with every member optional so that ABSENCE is
 * expressible.
 */
export type PagedRequestParams = {
  readonly [K in keyof PagedRequest]?: PagedRequest[K] | null;
};

/**
 * The ordering half of {@link PagedRequestParams}, for a caller that composes a sort separately from a
 * page. Projected from {@link PagedRequestParams} rather than restated, for the same anti-drift reason.
 */
export type SortParams = Pick<PagedRequestParams, 'sortBy' | 'sortDir'>;

/** The exact spelling of every query parameter this API binds. */
export const QUERY_PARAM = Object.freeze({
  /** Zero-based page index. Bound from `PagedRequest.PageIndex`. */
  pageIndex: 'pageIndex',

  /** Requested page size. */
  pageSize: 'pageSize',

  /** Name of the field to order by. Bound from `PagedRequest.SortBy`. */
  sortBy: 'sortBy',

  /** Ordering direction. Bound from `PagedRequest.SortDir`. */
  sortDir: 'sortDir',

  /** Free-text SUBSTRING filter. */
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

  /** Grouping scope on the role listing: which roles to consider when no group is named. */
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
 * @param params The parameter set to extend.
 * @param name The parameter name.
 * @param value The value to send, or `undefined` or `null` to send nothing.
 * @returns A parameter set carrying the value, or `params` unchanged when absent.
 * @throws {RangeError} The name is empty or entirely white space.
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

  // Explicit conversion of a value already narrowed to a non-nullish scalar. A string reaches the wire byte
  // for byte, a number as written, a boolean as the lower-case token the server's boolean binder accepts.
  return params.set(name, String(value));
}

/**
 * Sets every named value in a map, skipping the members that are absent. Iteration order follows the
 * map's own key order, which affects only the order the pairs appear in the serialised string and never
 * how the server binds them.
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
 * @param values The names and values to serialise.
 * @returns A parameter set carrying every present value.
 * @throws {RangeError} Any name in the map is empty or entirely white space.
 */
export function toHttpParams(values: QueryParamMap): HttpParams {
  return appendQueryParams(new HttpParams(), values);
}

/**
 * Builds an empty parameter set: the explicit way to say "this endpoint takes no query parameters".
 * Several collections in this API are DELIBERATELY UNPAGED and accept nothing at all — the role groups,
 * the portal aliases, the profile property definitions, the module definitions, the permission catalogue
 * and the page tree.
 *
 * @returns A parameter set carrying nothing.
 */
export function emptyQueryParams(): HttpParams {
  return new HttpParams();
}

/**
 * Sets the two page coordinates, omitting either one that is absent. The page index is transmitted
 * exactly as supplied.
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
 * Sets every member of the paging contract: both page coordinates, the ordering, and the free-text
 * filter. The filter text is passed through UNCHANGED by this module — not trimmed, not case-folded and
 * not decorated.
 *
 * @param params The parameter set to extend.
 * @param request The page of records to return, the size of the page, the ordering and the filter.
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
 * @param request The page of records to return, the size of the page, the ordering and the filter.
 * @returns A parameter set carrying whichever members were supplied.
 */
export function pagedRequestParams(request: PagedRequestParams): HttpParams {
  return setPagedRequestParams(new HttpParams(), request);
}

export interface PortalListFilter {
  /**
   * Restricts the result to portals whose name CONTAINS this text. The server performs a substring match,
   * case-insensitively and after trimming (`PortalRepository.cs` L58-L59).
   */
  readonly name?: string | null;
}

/**
 * Builds the query parameters for the portal listing.
 *
 * @param request The page of records to return, the size of the page, the ordering and the paging
 * contract's own filter.
 * @param filter The portal-name substring filter, or omitted or `null` to list every portal.
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

export interface UserListFilter {
  /** Restricts the result to accounts whose name BEGINS WITH this text. */
  readonly userName?: string | null;

  /** Restricts the result to accounts whose address BEGINS WITH this text. */
  readonly email?: string | null;

  /**
   * The profile property to match on: the legacy arbitrary-property search mode, whose property name the
   * screen carried as its own URL pair.
   */
  readonly profilePropertyName?: string | null;

  /**
   * The value {@link UserListFilter.profilePropertyName} must BEGIN WITH. Supplied raw; no per-cent sign
   * is appended here.
   */
  readonly profilePropertyValue?: string | null;

  /** Restricts the result by approval state. */
  readonly isApproved?: boolean | null;
}

/**
 * Builds the query parameters for a portal's account listing.
 *
 * @param request The page of records to return, the size of the page, the ordering and the paging
 * contract's own filter.
 * @param filter The search mode and approval restriction, or omitted or `null` to list every account in
 * the portal.
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
 * Everything the account listing accepts that could identify the person being looked for: the four named
 * filters plus the paging contract's own free-text member. Composed from the two contracts rather than
 * restated, so a member added to either is carried here automatically and cannot be forgotten.
 */
export type UserSearchTerms = UserListFilter & Pick<PagedRequestParams, 'query'>;

/**
 * Whether a search carries a value that identifies a person, and therefore must not travel in a request
 * target. ⚠ FIVE MEMBERS ARE INSPECTED, AND THEY ARE INSPECTED BY TWO DIFFERENT TESTS. The difference is
 * not an inconsistency; each test is the one that is correct for its member, and conflating them would
 * break one of the two cases below. ── THE FOUR NAMED FILTERS: ABSENCE, NOT EMPTINESS ── `userName`,
 * `email`, `profilePropertyName` and `profilePropertyValue` flip the transport on being SUPPLIED AT ALL.
 * Testing them for non-empty text would make the transport flip on the content of the term, and it would
 * flip for exactly the one input an operator produces by clearing the box — so the cleared-box case, and
 * only that case, would take a different route from every other keystroke.
 *
 * @param terms The search to inspect, or omitted or `null` for an unfiltered listing.
 * @returns True when at least one identifying value was supplied.
 */
export function identifiesAPerson(terms?: UserSearchTerms | null): boolean {
  if (terms === undefined || terms === null) {
    return false;
  }

  return (
    terms.userName !== undefined && terms.userName !== null
    || terms.email !== undefined && terms.email !== null
    || terms.profilePropertyName !== undefined && terms.profilePropertyName !== null
    || terms.profilePropertyValue !== undefined && terms.profilePropertyValue !== null
    || carriesFreeText(terms.query)
  );
}

/**
 * Whether the paging contract's free-text member carries something to match on.
 *
 * @param query The free-text filter, or omitted or `null` when none applies.
 * @returns True when the member carries at least one non-whitespace character.
 */
function carriesFreeText(query: string | null | undefined): boolean {
  if (query === undefined || query === null) {
    return false;
  }

  return query.trim().length > 0;
}

/**
 * The body `POST /api/v1/users/search` binds: the paging contract and the search filters together, as one
 * JSON object.
 */
export type UserSearchBody = PagedRequestParams & UserListFilter;

/**
 * Builds the request body for a portal's account search. ⚠ THE COUNTERPART OF {@link userListParams}, AND
 * THE ONE TO USE WHENEVER {@link identifiesAPerson} HOLDS. The two functions carry the same information
 * to the same server capability; the only difference is that this one puts it somewhere that is not
 * logged.
 *
 * @param request The page of records to return, the size of the page, the ordering and the paging
 * contract's own filter.
 * @param filter The search mode and approval restriction, or omitted or `null` to search every account in
 * the portal.
 * @returns The body to send, carrying only the members that were supplied.
 */
export function userSearchBody(
  request: PagedRequestParams,
  filter?: UserListFilter | null,
): UserSearchBody {
  const supplied = filter ?? {};

  // Built by conditional spread rather than by writing into an index-signature object and asserting its
  // type at the end.
  return {
    ...(request.pageIndex === undefined || request.pageIndex === null
      ? {}
      : { pageIndex: request.pageIndex }),
    ...(request.pageSize === undefined || request.pageSize === null
      ? {}
      : { pageSize: request.pageSize }),
    ...(request.sortBy === undefined || request.sortBy === null ? {} : { sortBy: request.sortBy }),
    ...(request.sortDir === undefined || request.sortDir === null
      ? {}
      : { sortDir: request.sortDir }),
    ...(request.query === undefined || request.query === null ? {} : { query: request.query }),
    ...(supplied.userName === undefined || supplied.userName === null
      ? {}
      : { userName: supplied.userName }),
    ...(supplied.email === undefined || supplied.email === null ? {} : { email: supplied.email }),
    ...(supplied.profilePropertyName === undefined || supplied.profilePropertyName === null
      ? {}
      : { profilePropertyName: supplied.profilePropertyName }),
    ...(supplied.profilePropertyValue === undefined || supplied.profilePropertyValue === null
      ? {}
      : { profilePropertyValue: supplied.profilePropertyValue }),
    ...(supplied.isApproved === undefined || supplied.isApproved === null
      ? {}
      : { isApproved: supplied.isApproved }),
  };
}

/**
 * Builds the query parameter that sets an account's approval state.
 *
 * @param isApproved The approval state to set.
 * @returns The parameters to send.
 */
export function userApprovalParams(isApproved: boolean): HttpParams {
  return setQueryParam(new HttpParams(), QUERY_PARAM.isApproved, isApproved);
}

/** The filter the role listing accepts in addition to the paging contract. */
export interface RoleListFilter {
  /**
   * Restricts the result to one role group. BOTH NEGATIVE VALUES ARE MEANINGFUL and are transmitted when
   * supplied.
   */
  readonly roleGroupId?: number | null;

  /** Which roles to consider when `roleGroupId` names no group. */
  readonly scope?: RoleGroupScope | null;
}

/**
 * The grouping scopes the role listing accepts. Spelled exactly as the API's closed enumeration names
 * them, because the value travels as a name and an unrecognised spelling is refused by model binding
 * before the request runs.
 */
export type RoleGroupScope = 'All' | 'Ungrouped';

/**
 * @param request The page of records to return, the size of the page, the ordering and the paging
 * contract's own filter.
 * @param filter The role-group restriction, or omitted or `null` to list every role in the portal.
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

/** The filters the module listing accepts in addition to the paging contract. */
export interface ModuleListFilter {
  /**
   * Restricts the result to modules placed on one page. The page identity is seeded at ZERO, so `0`
   * addresses a real page and is transmitted.
   */
  readonly tabId?: number | null;

  /** Includes modules that are in the recycle bin. */
  readonly includeDeleted?: boolean | null;
}

/**
 * Builds the query parameters for a portal's module listing.
 *
 * @param request The page of records to return, the size of the page, the ordering and the paging
 * contract's own filter.
 * @param filter The page and recycle-bin restrictions, or omitted or `null` to list the portal's live
 * modules.
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
 * Selects one placement of a module that appears on several pages. Accepted by the module read, the
 * module deletion and both module-settings operations.
 */
export interface ModulePlacementSelector {
  /**
   * The placement to address, or omitted to address the module itself. A placement identity is seeded at
   * 1 in the legacy schema, but no lower bound is assumed here: the value is forwarded exactly as
   * supplied.
   */
  readonly tabModuleId?: number | null;
}

/**
 * Builds the query parameters that select one module placement.
 *
 * @param selector The placement to address, or omitted or `null` to address the module itself.
 * @returns The parameters to send, empty when no placement was named.
 */
export function modulePlacementParams(selector?: ModulePlacementSelector | null): HttpParams {
  if (selector === undefined || selector === null) {
    return new HttpParams();
  }

  return setQueryParam(new HttpParams(), QUERY_PARAM.tabModuleId, selector.tabModuleId);
}

/**
 * The filters the permission catalogue accepts. The catalogue is DELIBERATELY UNPAGED — small, bounded
 * reference data seeded by the upgrade scripts and returned whole — so no page coordinate, ordering or
 * paging filter belongs on this request.
 */
export interface PermissionListFilter {
  /** Restricts the result to one permission code. */
  readonly permissionCode?: string | null;

  /**
   * Restricts the result to the permissions of one module definition. The module-definition identity is
   * seeded at 1, but the value is forwarded exactly as supplied and is never compared against a sentinel.
   */
  readonly moduleDefinitionId?: number | null;

  /**
   * Restricts the result to one permission key. Spelled exactly as the API's closed key enumeration names
   * it, because the value travels as a name and an unrecognised spelling is refused by model binding
   * before the request runs.
   */
  readonly permissionKey?: PermissionKeyName | null;
}

/**
 * The permission keys the catalogue filter accepts. The four members are the whole vocabulary the
 * catalogue's key column holds; the same spellings appear in an access token's permission claims, so one
 * concept never travels two ways.
 */
export type PermissionKeyName = 'VIEW' | 'EDIT' | 'READ' | 'WRITE';

/**
 * Builds the query parameters for the permission catalogue.
 *
 * @param filter The code, module-definition and key restrictions, or omitted or `null` to read the whole
 * catalogue.
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
 * Names the portal a sign-in addresses when the request host matches no configured portal alias. The
 * server resolves the portal from the request host first and consults this parameter only when that
 * resolution produced nothing.
 */
export interface LoginPortalSelector {
  /**
   * The portal being signed in to, or omitted when the host resolves it. The portal identity is seeded at
   * MINUS ONE, so -1 names the first portal and 0 the second.
   */
  readonly portalId?: number | null;
}

/**
 * Builds the query parameters for a sign-in request. The credential travels in the request body; only the
 * portal selection is a parameter.
 *
 * @param selector The portal being signed in to, or omitted or `null` when the request host resolves it.
 * @returns The parameters to send, empty when no portal was named.
 */
export function loginParams(selector?: LoginPortalSelector | null): HttpParams {
  if (selector === undefined || selector === null) {
    return new HttpParams();
  }

  return setQueryParam(new HttpParams(), QUERY_PARAM.portalId, selector.portalId);
}

// WHICH ENDPOINTS TAKE QUERY PARAMETERS, AND WHICH TAKE NONE
// Measured endpoint by endpoint against the eleven controllers on this branch, not inferred from the plan.
// A dedicated function above exists for each entry in the first list; the second list is why several of
// them do not.
