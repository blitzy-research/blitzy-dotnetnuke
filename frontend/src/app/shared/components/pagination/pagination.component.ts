import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
} from '@angular/core';

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
   * The number of items requested per page.
   *
   * The value 0 is a legitimate request for every match in one page, not an empty
   * page. It is handled explicitly throughout rather than guarded at each use, because
   * the naive page-count division by it produces `Infinity`.
   */
  @Input({ required: true }) pageSize!: number;

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
   */
  get totalPages(): number {
    if (this.pageSize <= 0 || this.totalCount <= 0) {
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

    if (this.pageSize <= 0) {
      return 1;
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

    if (this.pageSize <= 0) {
      return this.totalCount;
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
