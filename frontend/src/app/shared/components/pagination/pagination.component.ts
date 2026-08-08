import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
} from '@angular/core';

/**
 * The index of the first page.
 *
 * ZERO, because the base carried across this component's boundary is the wire's: the legacy screens kept a
 * one-based counter and subtracted one immediately before every call down to the provider
 * (`Website/admin/Portal/Portals.ascx.vb`), so the one-based number was only ever a presentation detail.
 * Named rather than written as a bare zero because zero is a REAL page here, not the absence of one, so a
 * page index is never tested for truthiness or for its sign anywhere below.
 */
const FIRST_PAGE_INDEX = 0;

/**
 * The page size this component reports when no usable one has been bound.
 *
 * Not a default page size.
 *
 * Zero is the marker because it is not a page size at all: the API's paging validator rejects a size of zero
 * or below, so no response envelope can carry one. It is nevertheless ORDINARY rather than exceptional —
 * `emptyPagedResult()` seeds `pageSize` to zero and both signal stores hold that empty page as their initial
 * state — so a pager with no usable page size renders nothing and stays inert.
 */
const UNRESOLVED_PAGE_SIZE = 0;

/**
 * The pager's visible and assistive-technology wording.
 *
 * MIGRATION: authored fresh. The legacy pager was a server control with no resource keys of its own, so none
 * of the in-scope `App_LocalResources` files names a pager string, and localisation is deliberately not
 * ported.
 *
 * Held as constants and bound rather than written as template text because Angular compiles templates with
 * whitespace preservation disabled, which collapses runs of whitespace inside a text node; an interpolated
 * value is not collapsed. Every one is PLAIN TEXT.
 */
const PAGINATION_LABELS = {
  /**
   * Accessible name for the group the template wraps these controls in. Names a `role="group"`, NOT a
   * landmark: the shell already renders the site's single navigation landmark, and a second one here would
   * be announced on every list screen.
   */
  region: 'Pagination',
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
  firstSymbol: '\u00AB',
  previousSymbol: '\u2039',
  nextSymbol: '\u203A',
  lastSymbol: '\u00BB',
} as const;

/**
 * Reduces a bound page index to a whole number no lower than the first page.
 *
 * Sanitising at the boundary is what lets every getter below divide, add and compare without re-guarding.
 *
 * A page index PAST THE END IS DELIBERATELY PRESERVED — records can be removed between a page being
 * requested and rendered — and is clamped only where it is USED, so the readout cannot contradict the data
 * and no out-of-range index can be emitted. Clamping it here would silently rewrite the consumer's own
 * state.
 *
 * @param value The bound page index.
 * @returns The value as a whole number at or above {@link FIRST_PAGE_INDEX}.
 */
function toPageIndex(value: number): number {
  if (!Number.isFinite(value)) {
    return FIRST_PAGE_INDEX;
  }

  const whole = Math.trunc(value);

  return whole < FIRST_PAGE_INDEX ? FIRST_PAGE_INDEX : whole;
}

/**
 * Reduces a bound page size to a whole number of at least one, or marks it unusable.
 *
 * Zero, a negative, a fraction, a not-a-number and an infinity all resolve to {@link UNRESOLVED_PAGE_SIZE},
 * which renders nothing rather than raising: the only way any of them can arrive is a page size that has not
 * resolved yet, and a screen must not fail because its first paint happened before its first response.
 *
 * A size of zero is NOT read as "every match on one page" — that mode does not exist in this API, and
 * inventing it would render a plausible single page a reader could not distinguish from a real one. The
 * API's page-size MAXIMUM is deliberately not enforced, because that bound constrains what a client may ask
 * for whereas this renders the size a response came back with.
 *
 * @param value The bound page size.
 * @returns a whole number of at least 1, or {@link UNRESOLVED_PAGE_SIZE} if unusable.
 */
function toPageSize(value: number): number {
  if (!Number.isFinite(value)) {
    return UNRESOLVED_PAGE_SIZE;
  }

  const whole = Math.trunc(value);

  return whole < 1 ? UNRESOLVED_PAGE_SIZE : whole;
}

/**
 * Reduces a bound total to a whole number no lower than zero.
 *
 * A total of zero is a REAL, MEANINGFUL COUNT — "no records matched" — and never a missing one, so it is
 * preserved. Only values the API could not have produced are corrected, keeping them out of the division
 * that derives the page count.
 *
 * @param value The bound total across every page.
 * @returns The value as a whole number at or above zero.
 */
function toTotalCount(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  const whole = Math.trunc(value);

  return whole < 0 ? 0 : whole;
}

/**
 * Page navigation for a list screen, driven by the API's paging metadata.
 *
 * Purely presentational: it takes three numbers in and reports one number out, holds no data, fetches
 * nothing and never changes its own {@link page}, so it cannot claim to be on a page whose request failed.
 * The three inputs are exactly three members of the response envelope's paging metadata, and every
 * derivation from them — the page count, the range on show, whether a step is available — is computed here
 * once, so no two screens can compute them differently.
 *
 * MIGRATION: the boundary is ZERO-BASED and the display is ONE-BASED, and the conversion happens here. So
 * {@link page} matches the wire exactly, {@link displayPage} adds the one a person expects, and {@link
 * pageChange} emits a zero-based index that a store passes straight through in both directions without
 * arithmetic.
 *
 * MIGRATION: a NET ADDITION rather than a port. The legacy `DataGrid_Pager` class was referenced in markup
 * and defined in no stylesheet, so there is no legacy appearance to be faithful to; the two grids that did
 * page hosted a `dnn:pagingcontrol` AFTER the closing grid tag, which is why this renders as a SIBLING of
 * the table. The legacy pager posted the whole page back and read its total from a `ByRef totalRecords`
 * out-parameter, whereas this reports an index and the total travels inside the response envelope. Keyboard
 * operability, the disabling of unavailable steps and the accessible names are additions too.
 */
@Component({
  selector: 'app-pagination',
  standalone: true,
  imports: [],
  templateUrl: './pagination.component.html',
  styleUrl: './pagination.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PaginationComponent {
  /**
   * The zero-based index of the page currently shown.
   *
   * Zero-based to match the API, so a consumer binds the index it received and reads back the index it must
   * request. Zero is the FIRST PAGE and never "no page", so it is never tested for truthiness anywhere
   * below. An index past the last page is accepted rather than corrected — records can be removed between a
   * page being requested and rendered — and is constrained where it is used.
   */
  @Input({ required: true, transform: toPageIndex }) page = FIRST_PAGE_INDEX;

  /**
   * The number of items on a full page.
   *
   * Supplied by the consumer and NEVER assumed, because the legacy screens disagreed; see {@link
   * UNRESOLVED_PAGE_SIZE}. A value the API could not have produced is reduced to that marker instead of
   * raising, so a screen whose page size has not resolved renders no pager rather than failing to render at
   * all.
   *
   * A genuinely unpaged resource does not render this component. Several administration resources return
   * every match in one response by design, and the way to express that is for the feature to omit the pager,
   * which is why there is no input for suppression.
   */
  @Input({ required: true, transform: toPageSize }) pageSize = UNRESOLVED_PAGE_SIZE;

  /**
   * The total number of matches across every page.
   *
   * Not the number of items on this page. Divided by {@link pageSize} it gives the page count, and compared
   * against zero it distinguishes "nothing matched" from "past the end of the results", which read
   * differently to a person. Zero is a real count.
   */
  @Input({ required: true, transform: toTotalCount }) totalCount = 0;

  /**
   * Emits the zero-based index of the page a person asked for.
   *
   * ZERO-BASED, never the one-based number on screen. Only ever emits a whole index inside the available
   * range that differs from the current {@link page}, so a consumer may act on it without re-validating —
   * which both signal stores rely on, since neither clamps the index it is given. Every path funnels through
   * one guard for that reason.
   */
  @Output() readonly pageChange = new EventEmitter<number>();

  /**
   * The pager's wording, bound by the template.
   */
  readonly labels = PAGINATION_LABELS;

  /**
   * The number of pages the result set spans.
   *
   * MIGRATION: DERIVED HERE, and only because it cannot be received. The response envelope carries a
   * server-computed page count, and the shared paged-result contract says outright that the count must not
   * be recomputed from the total and the page size — but this component's input surface is fixed at three
   * members by the design system, so the server's count cannot reach it and the division is unavoidable.
   *
   * Both guards below are equality tests against a known marker rather than late attempts to detect a bad
   * number, because the boundary transforms have already removed every unusable input: the divisor is always
   * at least one and the result always a whole number at or above zero.
   *
   * Reports ZERO when there is nothing to page through, matching the shared contract's own
   * `emptyPagedResult()` and `unpagedResult()` factories. Nothing is rendered in that state, so no readout
   * ever shows a page count of zero.
   */
  get totalPages(): number {
    if (this.pageSize === UNRESOLVED_PAGE_SIZE) {
      return 0;
    }

    if (this.totalCount === 0) {
      return 0;
    }

    return Math.ceil(this.totalCount / this.pageSize);
  }

  /**
   * Whether the pager should render at all.
   *
   * MIGRATION: hidden when everything already fits on one page. "Show when `PageSize < TotalRecords`" is
   * exactly "hide when the total is no greater than the page size", which is the rule below.
   *
   * The legacy escapes that hid the pager outright switched to an unpaged query. Those are FEATURE
   * decisions, expressed by a feature not rendering this component, which is why there is no visibility
   * input here.
   */
  get isNavigable(): boolean {
    return this.totalPages > 1;
  }

  /**
   * The index of the last page, or minus one when there are no pages.
   *
   * Minus one is arithmetic here — one below the first index — and carries none of the legacy absent-integer
   * meaning; nothing compares against it for absence.
   */
  private get lastPageIndex(): number {
    return this.totalPages - 1;
  }

  /**
   * {@link page} constrained to the pages that actually exist.
   *
   * Differs from {@link page} only when the bound index is past the end, which happens when records are
   * removed between a page being requested and rendered. Constraining it at the point of use keeps the
   * readout truthful ("the last page of three" rather than "page twelve of three") and keeps the range
   * summary inside the total, while leaving the consumer's own state untouched.
   */
  private get effectivePage(): number {
    if (this.totalPages === 0) {
      return FIRST_PAGE_INDEX;
    }

    return Math.min(this.page, this.lastPageIndex);
  }

  /**
   * The one-based number of the page on show, for display.
   *
   * MIGRATION: this is the `+ 1` that reconciles the two legacy bases, and making it HERE rather than in
   * each feature is the whole reason this component exists — it is the single off-by-one in the migration.
   * Derived from the constrained index, so a page past the end reads as the last real page rather than as a
   * number the data cannot support.
   */
  get displayPage(): number {
    return this.effectivePage + 1;
  }

  get canGoPrevious(): boolean {
    return this.totalPages > 0 && this.effectivePage > FIRST_PAGE_INDEX;
  }

  get canGoNext(): boolean {
    return this.totalPages > 0 && this.effectivePage < this.lastPageIndex;
  }

  /**
   * The one-based number of the first item on the page shown.
   *
   * Zero when nothing matched, so the summary reads "0 to 0 of 0" rather than "1 to 0 of 0".
   */
  get firstItemNumber(): number {
    if (this.totalPages === 0) {
      return 0;
    }

    return this.effectivePage * this.pageSize + 1;
  }

  /**
   * The one-based number of the last item on the page shown, clamped to the total because a final page is
   * usually short and a number beyond the total would be wrong.
   */
  get lastItemNumber(): number {
    if (this.totalPages === 0) {
      return 0;
    }

    return Math.min((this.effectivePage + 1) * this.pageSize, this.totalCount);
  }

  /**
   * Requests the first page, or does nothing when it is already shown.
   *
   * Like every sibling below it names the page it wants and lets {@link requestPage} decide, so the emission
   * rule is stated in one place rather than copied into each of them.
   */
  goFirst(): void {
    this.requestPage(FIRST_PAGE_INDEX);
  }

  /**
   * Requests the previous page, or does nothing when there is none.
   *
   * From a page past the end this returns to the LAST REAL PAGE rather than stepping back from a page that
   * does not exist, which would skip the last real page entirely.
   */
  goPrevious(): void {
    const target = this.page > this.lastPageIndex ? this.lastPageIndex : this.effectivePage - 1;

    this.requestPage(target);
  }

  goNext(): void {
    this.requestPage(this.effectivePage + 1);
  }

  goLast(): void {
    this.requestPage(this.lastPageIndex);
  }

  /**
   * The single point at which a page change is emitted.
   *
   * Every navigation method funnels through here so the emission rule is stated ONCE and cannot drift
   * between them: nothing is emitted unless the target is a whole index inside the pages that exist and
   * differs from the page bound. That guarantee is LOAD-BEARING — both signal stores pass the emitted index
   * straight to the API without clamping it — and this guard is the only thing enforcing it.
   *
   * The comparison that suppresses a redundant emission is made against the RAW {@link page}, not the
   * constrained one, so returning into range from a page past the end is still reported. There is
   * deliberately NO whole-number check on the target: every target is built from the boundary transforms by
   * truncation, a ceiling, a minimum or a step of one, so a fractional target cannot be constructed and a
   * guard against it could never run.
   *
   * @param target The zero-based index being requested.
   */
  private requestPage(target: number): void {
    if (this.totalPages === 0) {
      return;
    }

    if (target < FIRST_PAGE_INDEX || target > this.lastPageIndex) {
      return;
    }

    if (target === this.page) {
      return;
    }

    this.pageChange.emit(target);
  }
}
