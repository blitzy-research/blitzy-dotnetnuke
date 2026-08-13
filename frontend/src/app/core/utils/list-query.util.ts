import type { ParamMap, Params } from '@angular/router';

import type { SortDirection } from '../models/paged-result.model';

// ---------------------------------------------------------------------------
// THE SHARED VOCABULARY
// ---------------------------------------------------------------------------

export const FILTER_PARAM = 'filter';

/**
 * The parameter carrying the page, counted from ONE. Spelled as the legacy query argument was rather than
 * as `page`, so an address copied from the legacy application keeps working and an operator's habit
 * transfers.
 */
export const PAGE_PARAM = 'currentpage';

/** The parameter carrying the page size. */
export const PAGE_SIZE_PARAM = 'pagesize';

/** The parameter carrying the column to order by. */
export const SORT_BY_PARAM = 'sortby';

/** The parameter carrying the direction to order in. */
export const SORT_DIR_PARAM = 'sortdir';

/** The page an absent or unusable {@link PAGE_PARAM} resolves to, counted from nought. */
export const FIRST_PAGE_INDEX = 0;

/** The ascending direction token, in the server's own spelling. */
const ASCENDING: SortDirection = 'Ascending';

/** The descending direction token, in the server's own spelling. */
const DESCENDING: SortDirection = 'Descending';

// ---------------------------------------------------------------------------
// READERS
// ---------------------------------------------------------------------------

/**
 * Reads the page from the address, counted from nought. An absent, non-numeric, fractional or below-first
 * value resolves to the first page rather than throwing or being forwarded.
 *
 * @param raw The parameter as it appears in the address, or `null` when absent.
 * @returns The zero-based page index.
 */
export function parsePageIndex(raw: string | null): number {
  if (raw === null) {
    return FIRST_PAGE_INDEX;
  }

  const oneBased = Number(raw);

  if (!Number.isInteger(oneBased) || oneBased < 1) {
    return FIRST_PAGE_INDEX;
  }

  return oneBased - 1;
}

/**
 * Reads the page size from the address.
 *
 * @param raw The parameter as it appears in the address, or `null` when absent.
 * @returns The size to ask for, or `null` to express no preference.
 */
export function parsePageSize(raw: string | null): number | null {
  if (raw === null || raw.trim().length === 0) {
    return null;
  }

  const size = Number(raw);

  return Number.isInteger(size) && size > 0 ? size : null;
}

/**
 * Reads the ordering field from the address, admitting only what the caller's endpoint accepts. ⚠ THE
 * ADMITTED SET IS THE ENDPOINT'S AND MUST BE PASSED IN. Offering a column the endpoint does not order by
 * produces a refused request with a field-level message, so an address naming one is treated as naming
 * nothing rather than being forwarded hopefully.
 *
 * @param raw The parameter as it appears in the address, or `null` when absent.
 * @param admitted The column keys the endpoint will order by, in its own spelling.
 * @returns The column key to order by, or `null` for the server's own ordering.
 */
export function parseSortKey(raw: string | null, admitted: readonly string[]): string | null {
  if (raw === null) {
    return null;
  }

  // Matched without regard to case, then answered with the KEY'S OWN spelling, so the heading a grid marks
  // as active is found by an exact comparison however the address happened to be cased.
  const wanted = raw.trim().toLowerCase();

  return admitted.find((key: string): boolean => key.toLowerCase() === wanted) ?? null;
}

/**
 * Reads the ordering direction from the address. Both the server's own spelling and the three-letter
 * abbreviation an operator is likely to type are admitted, because this value is as often hand-edited as
 * it is generated.
 *
 * @param raw The parameter as it appears in the address, or `null` when absent.
 * @returns The direction, or `null` for the server's default.
 */
export function parseSortDirection(raw: string | null): SortDirection | null {
  if (raw === null) {
    return null;
  }

  switch (raw.trim().toLowerCase()) {
    case 'ascending':
    case 'asc':
      return ASCENDING;
    case 'descending':
    case 'desc':
      return DESCENDING;
    default:
      return null;
  }
}

// ---------------------------------------------------------------------------
// WRITERS AND RECONCILIATION
// ---------------------------------------------------------------------------

/**
 * The value {@link PAGE_PARAM} should carry for a given page, or `null` to omit it. The first page is
 * written as ABSENCE rather than as `currentpage=1`, so that the address of a listing an operator has not
 * paged is the bare route.
 *
 * @param pageIndex The page, counted from nought.
 * @returns The one-based parameter value, or `null` when the page is the first.
 */
export function firstPageParameter(pageIndex: number): string | null {
  return pageIndex === FIRST_PAGE_INDEX ? null : String(pageIndex + 1);
}

/**
 * Whether an address already states a query in its canonical form.
 *
 * @param address The route's query parameters.
 * @param canonical What the caller's writer produces for the query parsed out of that address.
 * @returns True when the address needs no correction.
 */
export function addressStatesQuery(address: ParamMap, canonical: Params): boolean {
  return Object.keys(canonical).every(
    (key: string): boolean => (address.get(key) ?? null) === (canonical[key] ?? null),
  );
}
