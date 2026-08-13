import { decodePageStructure } from '../utils/decode.util';

/** The direction in which a sort field is applied. */
export type SortDirection = 'Ascending' | 'Descending';

/**
 * The paging, sorting and filtering arguments a collection endpoint accepts. Bound from the query string,
 * so each member appears as a named parameter —
 * `?pageIndex=0&pageSize=10&sortBy=portalName&sortDir=Ascending&query=text`.
 */
export interface PagedRequest {
  /** The page of records to return. Zero-based: 0 is the first page. */
  readonly pageIndex: number;

  /** The size of the page. */
  readonly pageSize: number;

  /**
   * The name of the field to order by, or omitted when the caller expresses no preference and the
   * server's own ordering applies. A field name, not an expression.
   */
  readonly sortBy?: string;

  /** The direction {@link sortBy} is applied in. */
  readonly sortDir?: SortDirection;

  /**
   * The caller's free-text filter, raw and exactly as typed, or omitted when no filter applies. No
   * wildcard, escape or pattern syntax belongs here — see the at the head of this module.
   */
  readonly query?: string;
}

/**
 * One page of results together with the paging facts that describe where that page sits within the whole
 * match set.
 *
 * @typeParam T The row contract carried on the page.
 */
export interface PagedResult<T> {
  /** The records on this page, in the order the query produced them. A materialised, read-only array. */
  readonly items: readonly T[];

  /**
   * The paging facts locating this page within the whole match set: the total across every page, the
   * zero-based page index, the page size the server applied and the page count derived from them. The
   * paging facts live here and ONLY here — they are never repeated as members of this envelope.
   */
  readonly meta: ApiMeta;
}

export interface ApiMeta {
  /**
   * The total no of records that satisfy the criteria, counted across every page and not only the page
   * returned.
   */
  readonly totalCount: number;

  /** The page of records returned, counted from zero. Zero-based: 0 is the first page, 1 the second. */
  readonly pageIndex: number;

  /**
   * The size of the page that produced the payload: the size the server actually applied, which can
   * differ from the size a caller asked for once the request has been validated.
   */
  readonly pageSize: number;

  /**
   * The number of pages the total divides into at the current page size, or zero when there is nothing to
   * page. SERVER-COMPUTED. Read it; never recompute it from {@link ApiMeta.totalCount} and {@link
   * ApiMeta.pageSize}.
   */
  readonly totalPages: number;
}

/** @typeParam T The transported payload contract. */
export interface ApiResponse<T> {
  /** The payload the endpoint produced. */
  readonly data: T;

  /**
   * The metadata describing the response, or `null` when the response has no page to describe — which is
   * every response that uses this envelope, because the envelope is what a SINGLE-payload endpoint
   * returns. PRESENT AND NULLABLE, NOT OPTIONAL. The member is always on the wire and its value is
   * `null`; it never goes missing.
   */
  readonly meta: ApiMeta | null;
}

export interface EmptyApiResponse {
  /**
   * The metadata describing the response, or `null` when there is no page to describe — the usual case
   * for this form.
   */
  readonly meta: ApiMeta | null;
}

/**
 * The wire contract of every PAGED endpoint, named as the response rather than as the result.
 *
 * @typeParam T The row contract carried on the page.
 */
export type PagedResponse<T> = PagedResult<T>;

/**
 * @param response The response body as received, which is why the parameter is `unknown` rather than the
 * declared page type: the declared type is the claim being checked.
 * @returns The same page, with every coordinate present.
 * @throws ContractViolationError When the body is not a page.
 */
export function toPagedResult<T>(response: unknown): PagedResult<T> {
  const page = decodePageStructure(response, PAGE_ROOT);

  return { items: page.items as readonly T[], meta: page.meta };
}

/** The position name used when describing a malformed page. */
const PAGE_ROOT = 'page';

export const DEFAULT_PAGE_SIZE = 10;

/**
 * The largest page size the server accepts. A larger value is answered with a field-level `400` reading
 * `The page size may not exceed 100.` rather than being silently reduced, so a client offering a
 * page-size selector must not present a larger option.
 */
export const MAX_PAGE_SIZE = 100;

/** The longest filter text the server accepts on {@link PagedRequest.query}. */
export const QUERY_MAX_LENGTH = 256;

/**
 * Builds a zero-record, unpaged envelope. For the local placeholder a store needs before its first
 * response arrives, or for a list operation that legitimately matched nothing and applied no paging.
 *
 * @typeParam T The row contract the empty page would have carried.
 * @returns An envelope carrying no records and a total of zero.
 */
export function emptyPagedResult<T>(): PagedResult<T> {
  return {
    items: [],
    meta: {
      totalCount: 0,
      pageIndex: 0,
      pageSize: 0,
      totalPages: 0,
    },
  };
}

/**
 * Builds an unpaged, all-records envelope around records already in hand, deriving the total from them
 * because no further page exists to account for. This is the named replacement for the legacy all-records
 * call shape, which signalled the same intent by passing the integer null sentinel for the page index,
 * the page size and the total alike (see the at the head of this module).
 *
 * @typeParam T The row contract carried.
 * @param items Every matching record.
 * @returns An unpaged envelope whose total equals the number of records supplied.
 */
export function unpagedResult<T>(items: readonly T[]): PagedResult<T> {
  const snapshot: readonly T[] = [...items];

  return {
    items: snapshot,
    meta: {
      totalCount: snapshot.length,
      pageIndex: 0,
      pageSize: snapshot.length,
      totalPages: snapshot.length > 0 ? 1 : 0,
    },
  };
}
