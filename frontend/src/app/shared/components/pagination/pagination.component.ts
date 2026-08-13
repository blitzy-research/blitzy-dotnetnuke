import { DOCUMENT } from '@angular/common';
import {
  AfterViewChecked,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  inject,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  ViewChild,
} from '@angular/core';

/**
 * The index of the first page. ZERO, because the base carried across this component's boundary is the
 * wire's: the legacy screens kept a one-based counter and subtracted one immediately before every call
 * down to the provider, so the one-based number was only ever a presentation detail.
 */
const FIRST_PAGE_INDEX = 0;

/** The page size this component reports when no usable one has been bound. */
const UNRESOLVED_PAGE_SIZE = 0;

/** The pager's visible and assistive-technology wording. authored fresh. */
const PAGINATION_LABELS = {
  /**
   * Accessible name for the group the template wraps these controls in. Names a `role="group"`, NOT a
   * landmark: the shell already renders the site's single navigation landmark, and a second one here
   * would be announced on every list screen.
   */
  region: 'Pagination',
  /**
   * Accessible name for the step controls within the group. A SECOND name is needed because the group now
   * renders in two shapes: a result count alone when everything fits one page, and a result count plus
   * the steps when it does not.
   */
  steps: 'Pages',
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
 * Reduces a bound page index to a whole number no lower than the first page. Sanitising at the boundary
 * is what lets every getter below divide, add and compare without re-guarding.
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
 * Reduces a bound page size to a whole number of at least one, or marks it unusable. Zero, a negative, a
 * fraction, a not-a-number and an infinity all resolve to {@link UNRESOLVED_PAGE_SIZE}, which renders
 * nothing rather than raising: the only way any of them can arrive is a page size that has not resolved
 * yet, and a screen must not fail because its first paint happened before its first response.
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
 * Reduces a bound in-flight flag to a definite boolean. Compared against `true` rather than tested for
 * truthiness, in keeping with this component's rule against truthiness tests, so an absent, null or
 * undefined binding reads as settled rather than as busy.
 *
 * @param value The bound flag, which a caller may leave unbound.
 * @returns True only when the caller genuinely said a request is open.
 */
function toPending(value: boolean | null | undefined): boolean {
  return value === true;
}

/**
 * Page navigation for a list screen, driven by the API's paging metadata. Purely presentational: it takes
 * three numbers and one flag in, reports one number out, holds no data, fetches nothing and never changes
 * its own {@link page}, so it cannot claim to be on a page whose request failed.
 */
@Component({
  selector: 'app-pagination',
  standalone: true,
  imports: [],
  templateUrl: './pagination.component.html',
  styleUrl: './pagination.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PaginationComponent implements OnChanges, AfterViewChecked {
  /**
   * The zero-based index of the page currently shown. Zero-based to match the API, so a consumer binds
   * the index it received and reads back the index it must request.
   */
  @Input({ required: true, transform: toPageIndex }) page = FIRST_PAGE_INDEX;

  /** The number of items on a full page. */
  @Input({ required: true, transform: toPageSize }) pageSize = UNRESOLVED_PAGE_SIZE;

  /** The total number of matches across every page. */
  @Input({ required: true, transform: toTotalCount }) totalCount = 0;

  /**
   * Whether the page these numbers describe is still being fetched. ⚠ WITHOUT IT THE COMPONENT CANNOT
   * TELL THE TRUTH: the range summary is a live region, so it is announced rather than merely displayed,
   * and it is composed from {@link page} — the coordinate that was asked for rather than the one that
   * arrived. `aria-busy` holds the region's changes while a request is open and processes it once, in its
   * settled state, so the wording updates immediately for the eye and is announced once for the ear.
   *
   * Optional, defaulting to false, so a caller with nothing asynchronous behind it keeps its behaviour.
   */
  @Input({ transform: toPending }) loading = false;

  /**
   * Emits the zero-based index of the page a person asked for. ZERO-BASED, never the one-based number on
   * screen.
   */
  @Output() readonly pageChange = new EventEmitter<number>();

  /** The pager's wording, bound by the template. */
  readonly labels = PAGINATION_LABELS;

  /**
   * The four step controls, read from the view so focus can be repaired after a terminal step. Read as
   * element references rather than looked up by selector at the document level so that a screen mounting
   * two pagers - a listing and a picker within it - can never move focus into the other one.
   */
  @ViewChild('firstStep') private firstStep?: ElementRef<HTMLButtonElement>;

  @ViewChild('previousStep') private previousStep?: ElementRef<HTMLButtonElement>;

  @ViewChild('nextStep') private nextStep?: ElementRef<HTMLButtonElement>;

  @ViewChild('lastStep') private lastStep?: ElementRef<HTMLButtonElement>;

  /**
   * The document, for reading which element holds focus. Injected rather than referenced as the global so
   * the component stays testable and renderer-agnostic; nothing here writes to the document beyond
   * calling `focus()` on its own button.
   */
  private readonly document = inject(DOCUMENT);

  /**
   * Which terminal step a person just activated, while its effect is still pending. ⚠ THIS EXISTS BECAUSE
   * FIRST AND LAST DISABLE THEMSELVES. Activating "Last page" moves to the last page, at which point
   * "Last page" is unavailable and becomes genuinely `disabled` - and a browser discards focus on an
   * element that becomes disabled, dropping it to `body`.
   */
  private pendingStep: 'first' | 'last' | null = null;

  /**
   * Whether the bound page has changed since a terminal step was activated. Separating "asked for" from
   * "arrived" is what bounds the repair: without it a request that never completes would leave focus
   * movement armed indefinitely, and a later unrelated render could then move focus with no person having
   * asked for it.
   */
  private stepApplied = false;

  /** The number of pages the result set spans. DERIVED HERE, and only because it cannot be received. */
  get totalPages(): number {
    if (this.pageSize === UNRESOLVED_PAGE_SIZE) {
      return 0;
    }

    if (this.totalCount === 0) {
      return 0;
    }

    return Math.ceil(this.totalCount / this.pageSize);
  }

  /** Whether the pager should render at all. */
  get isNavigable(): boolean {
    return this.totalPages > 1;
  }

  /**
   * Whether the pager renders anything at all. TRUE AS SOON AS THERE IS A RESULT TO COUNT, which is a
   * wider condition than {@link isNavigable} on purpose: the group carries a range summary as well as the
   * steps, and the summary is the only on-screen confirmation of how many records a filter matched.
   */
  get isRendered(): boolean {
    return this.totalPages > 0;
  }

  /**
   * The index of the last page, or minus one when there are no pages. Minus one is arithmetic here — one
   * below the first index — and carries none of the legacy absent-integer meaning; nothing compares
   * against it for absence.
   */
  private get lastPageIndex(): number {
    return this.totalPages - 1;
  }

  /**
   * {@link page} constrained to the pages that actually exist. Differs from {@link page} only when the
   * bound index is past the end, which happens when records are removed between a page being requested
   * and rendered.
   */
  private get effectivePage(): number {
    if (this.totalPages === 0) {
      return FIRST_PAGE_INDEX;
    }

    return Math.min(this.page, this.lastPageIndex);
  }

  /**
   * The value bound to the range summary's `aria-busy`, or `null` at rest. A getter rather than a stored
   * value so it cannot fall out of step with {@link loading}, and `null` rather than the string "false"
   * when settled so the attribute is ABSENT at rest — which is exactly how the shared grid reports the
   * same state, and the two are read together by anyone auditing how this application reports progress.
   */
  get ariaBusy(): 'true' | null {
    return this.loading ? 'true' : null;
  }

  /**
   * The one-based number of the page on show, for display. this is the `+ 1` that reconciles the two
   * legacy bases, and making it HERE rather than in each feature is the whole reason this component
   * exists — it is the single off-by-one in the migration.
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
   * The one-based number of the first item on the page shown. Zero when nothing matched, so the summary
   * reads "0 to 0 of 0" rather than "1 to 0 of 0".
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
   * Requests the first page, or does nothing when it is already shown. Like every sibling below it names
   * the page it wants and lets {@link requestPage} decide, so the emission rule is stated in one place
   * rather than copied into each of them.
   */
  goFirst(): void {
    this.armStepRepair('first');
    this.requestPage(FIRST_PAGE_INDEX);
  }

  /**
   * Requests the previous page, or does nothing when there is none. From a page past the end this returns
   * to the LAST REAL PAGE rather than stepping back from a page that does not exist, which would skip the
   * last real page entirely.
   */
  goPrevious(): void {
    const target = this.page > this.lastPageIndex ? this.lastPageIndex : this.effectivePage - 1;

    this.requestPage(target);
  }

  goNext(): void {
    this.requestPage(this.effectivePage + 1);
  }

  goLast(): void {
    this.armStepRepair('last');
    this.requestPage(this.lastPageIndex);
  }

  /**
   * The single point at which a page change is emitted. Every navigation method funnels through here so
   * the emission rule is stated ONCE and cannot drift between them: nothing is emitted unless the target
   * is a whole index inside the pages that exist and differs from the page bound.
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

  // -------------------------------------------------------------------------
  // FOCUS REPAIR AFTER A TERMINAL STEP
  // -------------------------------------------------------------------------

  /**
   * Notes that the page a person stepped to has arrived.
   *
   * @param changes The bindings that changed.
   */
  ngOnChanges(changes: SimpleChanges): void {
    if (this.pendingStep !== null && changes['page'] !== undefined) {
      this.stepApplied = true;
    }
  }

  /**
   * Returns focus to a usable step once the activated one has disabled itself. Runs as a view-checked
   * hook because the repair depends on the DISABLED PROPERTY HAVING BEEN WRITTEN, which happens during
   * the same change-detection pass that binds the new page - not when the page arrives in the model.
   */
  ngAfterViewChecked(): void {
    const step = this.pendingStep;

    if (step === null || !this.stepApplied) {
      return;
    }

    const source = step === 'first' ? this.firstStep : this.lastStep;
    const target = step === 'first' ? this.nextStep : this.previousStep;
    const sourceElement = source?.nativeElement;
    const targetElement = target?.nativeElement;

    this.pendingStep = null;
    this.stepApplied = false;

    if (sourceElement === undefined || targetElement === undefined) {
      return;
    }

    if (!sourceElement.disabled || targetElement.disabled) {
      return;
    }

    const active = this.document.activeElement;

    if (active === null || active === sourceElement || active === this.document.body) {
      targetElement.focus();
    }
  }

  /**
   * Arms the focus repair for a step that will disable itself, if focus is on it. The check that focus is
   * ON THE BUTTON is what stops a programmatic call to {@link goLast} from pulling focus out of whatever
   * a person was using.
   *
   * @param step Which terminal step was activated.
   */
  private armStepRepair(step: 'first' | 'last'): void {
    const element = step === 'first' ? this.firstStep?.nativeElement : this.lastStep?.nativeElement;

    this.pendingStep =
      element !== undefined && this.document.activeElement === element ? step : null;
    this.stepApplied = false;
  }
}
