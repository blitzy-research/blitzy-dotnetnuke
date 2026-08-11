/**
 * @file The address vocabulary every paged listing shares, and the readers that interpret it.
 *
 * WHY THIS FILE EXISTS. Four listings keep their page, filter and ordering in the address so that a
 * reload, a bookmark and the browser's own back and forward buttons all reproduce what is on screen.
 * Runtime testing measured what its absence costs: two pager clicks advanced a listing to
 * "21-30 of 250" while the address stayed exactly `/portals`, pressing back from page three was not
 * possible because no history entry had ever been created, a typed `/portals?name=X` issued no request
 * at all, and a fresh sidebar click from another screen landed on page three of a filter the operator
 * could not see, because the stores are provided at the application root and OUTLIVE the routes that
 * read them.
 *
 * ⚠ THE PARAMETER NAMES ARE DELIBERATELY SHARED RATHER THAN CHOSEN PER SCREEN. They are the operator's
 * vocabulary, not an implementation detail: once `?currentpage=` means the page on one listing it must
 * mean the page on all of them, or an address hand-edited on one screen fails silently on the next.
 * Holding them here is also what stops the four readers below from drifting apart - four private copies
 * of a shared value is a divergence waiting to happen, and this codebase has already paid for that once.
 *
 * WHAT IS NOT HERE, AND WHY. There is no shared query TYPE and no shared `parseListQuery`. The four
 * listings genuinely differ in what they can be narrowed by - portals by a name prefix, roles by a role
 * group, modules by a search term with two orderable columns, accounts by a search axis that includes
 * every declared profile property and by a third state in which nothing has been requested at all - so a
 * single type would either be a union of everything, which no screen could satisfy, or a lowest common
 * denominator that none of them could use. Each listing therefore composes its own reader and writer out
 * of these primitives, and only the primitives and the vocabulary are shared.
 *
 * MIGRATION: THE ADDRESS IS ONE-BASED WHILE EVERY STORE AND ENDPOINT IS NOUGHT-BASED. The legacy screens
 * were one-based in their addresses - `Users.ascx.vb` L446-L456 built `FilterURL` with a literal page
 * argument of `"1"` - and an operator reading or editing an address expects the first page to be page
 * one, not page nought. The conversion happens in exactly two places per listing, its reader and its
 * writer, and nowhere else; {@link parsePageIndex} and {@link firstPageParameter} are those two places.
 */

import type { ParamMap, Params } from '@angular/router';

import type { SortDirection } from '../models/paged-result.model';

// ---------------------------------------------------------------------------
// THE SHARED VOCABULARY
// ---------------------------------------------------------------------------

/**
 * The parameter carrying the text or prefix a listing is narrowed by.
 *
 * MIGRATION: named for what the legacy screens called it. `Portals.ascx.vb` L142 passed its value
 * straight into `GetPortalsByName(Filter + "%")`, and the alphabet strips on both the portal and account
 * listings fed the same argument, so one name covers a typed term and a pressed letter alike.
 */
export const FILTER_PARAM = 'filter';

/**
 * The parameter carrying the page, counted from ONE.
 *
 * Spelled as the legacy query argument was rather than as `page`, so an address copied from the legacy
 * application keeps working and an operator's habit transfers.
 */
export const PAGE_PARAM = 'currentpage';

/**
 * The parameter carrying the page size.
 *
 * Absent means the listing expresses no preference and takes whatever size the server considers correct,
 * which is not the same as asking for a particular size that happens to equal the default.
 */
export const PAGE_SIZE_PARAM = 'pagesize';

/**
 * The parameter carrying the column to order by.
 */
export const SORT_BY_PARAM = 'sortby';

/**
 * The parameter carrying the direction to order in.
 */
export const SORT_DIR_PARAM = 'sortdir';

/**
 * The page an absent or unusable {@link PAGE_PARAM} resolves to, counted from nought.
 */
export const FIRST_PAGE_INDEX = 0;

/**
 * The ascending direction token, in the server's own spelling.
 */
const ASCENDING: SortDirection = 'Ascending';

/**
 * The descending direction token, in the server's own spelling.
 */
const DESCENDING: SortDirection = 'Descending';

// ---------------------------------------------------------------------------
// READERS
// ---------------------------------------------------------------------------

/**
 * Reads the page from the address, counted from nought.
 *
 * An absent, non-numeric, fractional or below-first value resolves to the first page rather than throwing
 * or being forwarded. That is a deliberate difference from how an out-of-range page is treated: a page
 * BEYOND the end is a real coordinate the server answers with an empty page and a total, and the listings
 * have a surface for it, whereas `currentpage=abc` names no page at all and there is nothing to ask for.
 * The address is then normalised to the value that was actually applied, so the two never disagree on
 * screen.
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

  // Nought is refused rather than forwarded: the stores treat a zero size as "no preference", and the
  // endpoints treat it as "every match", so forwarding it from an address would mean two different things
  // in two places.
  return Number.isInteger(size) && size > 0 ? size : null;
}

/**
 * Reads the ordering field from the address, admitting only what the caller's endpoint accepts.
 *
 * ⚠ THE ADMITTED SET IS THE ENDPOINT'S AND MUST BE PASSED IN. Offering a column the endpoint does not
 * order by produces a refused request with a field-level message, so an address naming one is treated as
 * naming nothing rather than being forwarded hopefully.
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
 * Reads the ordering direction from the address.
 *
 * Both the server's own spelling and the three-letter abbreviation an operator is likely to type are
 * admitted, because this value is as often hand-edited as it is generated.
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
 * The value {@link PAGE_PARAM} should carry for a given page, or `null` to omit it.
 *
 * The first page is written as ABSENCE rather than as `currentpage=1`, so that the address of a listing
 * an operator has not paged is the bare route. Without this a single visit would leave a redundant
 * parameter behind, and the reconciliation below would then correct it away on the next entry, costing a
 * history replacement for nothing.
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
 * Compares what the address LITERALLY carries against what the caller's writer would produce for the
 * query parsed out of it. A mismatch means the address said something unusable - `currentpage=abc`,
 * `sortdir=desc`, `currentpage=1` where the canonical form omits it - and the screen replaces it so that
 * what is on screen and what is in the address agree.
 *
 * ⚠ THE COMPARISON MUST BE AGAINST THE RAW PARAMETERS. Comparing a parsed query against itself would
 * always succeed and would detect nothing, which is the whole reason this takes the {@link ParamMap} and
 * not the parsed value.
 *
 * Only the keys the writer produces are examined, so a parameter belonging to something else on the
 * address - an account carried to a sibling screen, a returnUrl - is left alone rather than being treated
 * as a discrepancy and cleared.
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
