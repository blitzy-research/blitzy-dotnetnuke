/**
 * Client-side mirror of the API's paging envelope.
 *
 * Every list endpoint returns `{ items, meta }` rather than a bare array, so the
 * total count travels with the page instead of being inferred from it. That is
 * what makes a pager possible at all: a page of ten items says nothing about
 * whether there are eleven matches or eleven thousand.
 *
 * MIGRATION: this envelope replaces the legacy `ByRef totalRecords`
 * out-parameter idiom. Three user-listing members reported their total by mutating
 * a caller-supplied variable — `UserController.GetUsers`, `GetUsersByPortal` and
 * the search member — which is not expressible over HTTP and was not expressible
 * in a typed client either. Returning the count inside the envelope makes the
 * page and its total a single value that cannot be read half-updated.
 */

/**
 * A single page of results together with the paging facts that describe it.
 *
 * @typeParam T The item type carried by the page.
 */
export interface PagedResult<T> {
  /**
   * The items on this page.
   *
   * Empty for a page beyond the end of the result set, which is a successful
   * response rather than an error — the caller asked a legitimate question and the
   * answer is "nothing here".
   */
  readonly items: readonly T[];

  /** The paging facts for this page. */
  readonly meta: PagedMeta;
}

/**
 * The paging facts returned alongside a page.
 *
 * Mirrors the API's `ApiMeta` member for member.
 */
export interface PagedMeta {
  /**
   * The total number of matches across every page, not the number on this page.
   *
   * This is the value a pager divides to obtain the page count, and the value an
   * empty-state check must consult — `items.length === 0` on page four of three
   * means "past the end", whereas a total of zero means "no matches at all", and
   * those deserve different wording.
   */
  readonly totalCount: number;

  /** The zero-based index of this page. */
  readonly pageIndex: number;

  /**
   * The requested page size.
   *
   * The API's ceiling is 500, and the value 0 is a deliberate request for every
   * match in one page rather than an empty page. A pager must therefore not divide
   * by this value without first checking it, which {@link totalPageCount} does.
   */
  readonly pageSize: number;
}

/**
 * Computes the number of pages a result set spans.
 *
 * Returns 1 rather than 0 for an empty result set, because a list screen still
 * shows one page — the page that says there is nothing on it. Returning 0 would
 * make "page 1 of 0" the rendered text, and would make a "next page" test read
 * `pageIndex < totalPages - 1`, which is false for the only page that exists.
 *
 * A page size of 0 means "every match in one page", so it also yields 1. Guarding
 * that case here rather than at each call site is the whole reason this helper
 * exists: the naive `ceil(total / size)` divides by zero and produces `Infinity`.
 *
 * @param meta The paging facts returned with a page.
 * @returns The page count, never less than 1.
 */
export function totalPageCount(meta: PagedMeta): number {
  if (meta.pageSize <= 0 || meta.totalCount <= 0) {
    return 1;
  }

  return Math.ceil(meta.totalCount / meta.pageSize);
}
