import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
} from '@angular/core';

/**
 * Rejects a page size the API could not have produced and this component cannot render.
 *
 * A page size is a whole number of at least 1. The paging request contract says so
 * outright — a size of zero or below is rejected by the API's own validator rather than
 * reinterpreted — so a value outside that range never arrived in a response envelope and
 * can only be a consumer defect: a size that was never resolved, a subtraction that went
 * negative, or a member read from the wrong object.
 *
 * REFUSING IS THE POINT. Treating 0 as "every match on one page" is what this replaces,
 * and it was wrong twice over. It invented a mode the API does not have, and it did so
 * silently, so a screen whose page size failed to resolve rendered a plausible-looking
 * single page of results instead of reporting the fault — the reader had no way to tell
 * that page from a real one. Failing at binding time surfaces it on the first render.
 *
 * Rejecting non-integers is what keeps `NaN` and `Infinity` out of the arithmetic below,
 * since neither is an integer. The page count is therefore always a finite positive whole
 * number, and no guard against a division by zero is needed anywhere: the invariant is
 * established once, here, at the boundary.
 *
 * The API's page-size MAXIMUM is deliberately not enforced here. That bound constrains
 * what a client may ASK FOR, whereas this component only renders the size a response came
 * back with; refusing a large page would break a screen that had legitimately been served
 * one, and the request side already validates it.
 *
 * @param value The bound page size.
 * @returns The same value, once proven usable.
 * @throws Error if the value is not a whole number of at least 1.
 */
function requirePositivePageSize(value: number): number {
  if (!Number.isInteger(value) || value < 1) {
    throw new Error(
      'PaginationComponent: `pageSize` must be a whole number of at least 1, but was ' +
        `${value}. A page size of zero or below is rejected by the API's own paging ` +
        'validator, so no response envelope carries one, and it cannot be rendered: the ' +
        'page count is this size divided into the total. Zero is NOT a request for every ' +
        'match on one page — that mode does not exist. Resolve the page size in the ' +
        'feature before binding it, and if the resource is genuinely unpaged do not ' +
        'render this component at all rather than binding a size of zero to suppress it.',
    );
  }

  return value;
}

/**
 * Page navigation for a list screen, driven by the API's paging envelope.
 *
 * Reports the page a person asked for and renders nothing else — it holds no data, it
 * fetches nothing, and it does not know what is being paged. The consuming feature
 * owns the request.
 *
 * The three inputs are exactly the three members of the API's paging metadata, so a
 * feature binds them straight from the envelope it received rather than deriving
 * anything. That is deliberate: every derivation this component needs — the page
 * count, whether there is a previous page, the range being shown — is computed here,
 * once, from those three numbers, so no two screens can compute them differently.
 *
 * MIGRATION: this replaces the legacy grid's own pager. Two legacy behaviours are
 * deliberately not carried across. The legacy pager posted the whole page back to the
 * server to change page, whereas this reports an index and the feature issues one
 * request. And the legacy total came from a `ByRef totalRecords` out-parameter that
 * the caller had to supply and read back; the total now travels inside the response
 * envelope, so the page and its total cannot be read half-updated.
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
   * Zero-based to match the API. The rendered position is one-based, because a person
   * counts from one — the conversion happens here rather than in every feature, which
   * is precisely the off-by-one this component exists to centralise.
   */
  @Input({ required: true }) page!: number;

  /**
   * The number of items on a full page, as a whole number of at least 1.
   *
   * REFUSED IF IT IS NOT. Zero is not a request for every match on one page; the paging
   * request contract rejects both zero and a negative size, so neither can reach a
   * response envelope, and a value outside the range is a consumer defect that
   * {@link requirePositivePageSize} reports rather than absorbs.
   *
   * A GENUINELY UNPAGED RESOURCE DOES NOT RENDER THIS COMPONENT. Several administration
   * resources return every match in one response by design — role groups, portal aliases,
   * profile property definitions, module definitions and the page tree among them — and
   * the way to express that is for the feature to omit the pager, not to bind a page size
   * of zero and rely on it suppressing itself. There is deliberately no input for
   * suppression, because "there is nothing to page through" is the feature's fact, not
   * this component's.
   *
   * Because the value is validated on assignment, every derivation below may divide by it
   * freely: the page count cannot be `NaN`, `Infinity` or negative.
   */
  @Input({ required: true, transform: requirePositivePageSize }) pageSize!: number;

  /**
   * The total number of matches across every page.
   *
   * Not the number of items on this page. Dividing this by {@link pageSize} gives the
   * page count; comparing it against zero distinguishes "no matches at all" from
   * "past the end of the results", which read differently to a person.
   */
  @Input({ required: true }) totalCount!: number;

  /**
   * Whether the control is unavailable, for instance while a page is loading.
   *
   * Disables every button, so a person cannot queue three page changes while the first
   * is still in flight and leave the screen showing a page they did not ask for last.
   */
  @Input() disabled: boolean = false;

  /**
   * Emits the zero-based index of the page the person asked for.
   *
   * Only ever emits an index that differs from the current one and lies inside the
   * available range, so a consumer can act on it without re-validating. The component
   * does not change its own {@link page}: the consumer owns that state and rebinds it
   * once the requested page has actually been fetched, which is what stops the pager
   * from claiming to be on a page whose request failed.
   */
  @Output() readonly pageChange = new EventEmitter<number>();

  /**
   * The number of pages the result set spans, never less than 1.
   *
   * Returns 1 for an empty result set, because a list screen still shows one page —
   * the page that says there is nothing on it. Returning 0 would render "page 1 of 0"
   * and would make the "next page" test false for the only page that exists.
   *
   * The division needs no guard: {@link pageSize} is validated to be at least 1 when it is
   * bound, so it is never zero here. The page count is derived rather than taken from the
   * response envelope only because the input surface is closed at three members and cannot
   * receive the server's own count; where a consumer has that count, it is the better
   * value.
   */
  get totalPages(): number {
    if (this.totalCount <= 0) {
      return 1;
    }

    return Math.ceil(this.totalCount / this.pageSize);
  }

  /** Whether a previous page exists and can be requested. */
  get canGoPrevious(): boolean {
    return !this.disabled && this.page > 0;
  }

  /** Whether a following page exists and can be requested. */
  get canGoNext(): boolean {
    return !this.disabled && this.page < this.totalPages - 1;
  }

  /**
   * The one-based number of the first item shown on this page.
   *
   * Zero when there are no matches, so the summary reads "0 to 0 of 0" rather than
   * "1 to 0 of 0".
   */
  get firstItemNumber(): number {
    if (this.totalCount <= 0) {
      return 0;
    }

    return this.page * this.pageSize + 1;
  }

  /**
   * The one-based number of the last item shown on this page.
   *
   * Clamped to the total, because the final page is usually short and reporting a
   * number beyond the total would be wrong rather than merely untidy.
   */
  get lastItemNumber(): number {
    if (this.totalCount <= 0) {
      return 0;
    }

    return Math.min((this.page + 1) * this.pageSize, this.totalCount);
  }

  /**
   * Whether the pager should be shown at all.
   *
   * Hidden when everything fits on one page: navigation between one page and itself is
   * not an affordance, and the range summary would restate what the list already shows.
   */
  get isNavigable(): boolean {
    return this.totalPages > 1;
  }

  /** Requests the previous page, or does nothing when there is none. */
  goPrevious(): void {
    if (!this.canGoPrevious) {
      return;
    }

    this.pageChange.emit(this.page - 1);
  }

  /** Requests the following page, or does nothing when there is none. */
  goNext(): void {
    if (!this.canGoNext) {
      return;
    }

    this.pageChange.emit(this.page + 1);
  }

  /** Requests the first page, or does nothing when it is already shown. */
  goFirst(): void {
    if (!this.canGoPrevious) {
      return;
    }

    this.pageChange.emit(0);
  }

  /** Requests the last page, or does nothing when it is already shown. */
  goLast(): void {
    if (!this.canGoNext) {
      return;
    }

    this.pageChange.emit(this.totalPages - 1);
  }

  /**
   * The one-based number of the page currently shown, for display.
   */
  get displayPage(): number {
    return this.page + 1;
  }

  /** Labels, bound from constants so the template compiler cannot collapse them. */
  readonly labels = PAGINATION_LABELS;
}

/**
 * The pager's visible and assistive-technology wording.
 *
 * Authored as constants and bound, never written as template text. Angular compiles
 * templates with whitespace preservation disabled, which collapses every run of
 * whitespace in a text node; interpolated values are not collapsed.
 */
const PAGINATION_LABELS = {
  /** Accessible name for the navigation landmark. */
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
