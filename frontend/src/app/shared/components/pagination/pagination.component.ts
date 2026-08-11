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
  /**
   * Accessible name for the step controls within the group.
   *
   * A SECOND name is needed because the group now renders in two shapes: a result count alone when
   * everything fits one page, and a result count plus the steps when it does not. Naming the inner
   * cluster keeps "Pagination" attached to the whole, so the count is inside the named region in both
   * shapes rather than only in the navigable one.
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
 * Reduces a bound in-flight flag to a definite boolean.
 *
 * Compared against `true` rather than tested for truthiness, in keeping with this component's rule against
 * truthiness tests, so an absent, null or undefined binding reads as settled rather than as busy. Settled is
 * the safe default: a component told nothing about a request announces its numbers immediately, which is the
 * behaviour every caller had before this input existed.
 *
 * @param value The bound flag, which a caller may leave unbound.
 * @returns True only when the caller genuinely said a request is open.
 */
function toPending(value: boolean | null | undefined): boolean {
  return value === true;
}

/**
 * Page navigation for a list screen, driven by the API's paging metadata.
 *
 * Purely presentational: it takes three numbers and one flag in, reports one number out, holds no data,
 * fetches nothing and never changes its own {@link page}, so it cannot claim to be on a page whose request
 * failed. The three numbers are exactly three members of the response envelope's paging metadata, and every
 * derivation from them — the page count, the range on show, whether a step is available — is computed here
 * once, so no two screens can compute them differently. The flag, {@link loading}, exists for one reason
 * only: the range summary is a LIVE REGION, so it is announced rather than merely shown, and a component
 * told nothing about an outstanding request will announce a page's contents before that page arrives.
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
export class PaginationComponent implements OnChanges, AfterViewChecked {
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
   * Whether the page these numbers describe is still being fetched.
   *
   * ⚠ THIS IS THE FOURTH INPUT ON A SURFACE THIS FILE ELSEWHERE DESCRIBES AS CLOSED AT THREE, and the
   * reason it earns its place is that without it the component CANNOT tell the truth. The range summary
   * is a live region, so it is not merely displayed - it is ANNOUNCED. Its wording is composed from
   * {@link page}, which every list screen binds to the coordinate that was ASKED for rather than the one
   * that arrived, deliberately, so the visible position readout does not snap back to the previous page
   * for the duration of every request. The consequence for the announcement is not benign: runtime
   * measurement under deliberate network throttling caught the summary reading "21-30 of 145" while the
   * grid beside it still held rows eleven to twenty. A screen-reader user was being told the contents of
   * a page that did not exist yet.
   *
   * ⚠ THE FIX IS TO DEFER THE ANNOUNCEMENT, NOT TO CHANGE THE NUMBERS. Deriving the summary from the
   * served metadata instead would fix the announcement and break the readout, and suppressing the
   * summary while a request is open would announce its disappearance and reappearance on every page
   * turn. `aria-busy` is the mechanism ARIA defines for exactly this: while it is true on a live region
   * the region's changes are held, and when it clears the region is processed once, in its settled
   * state. So the wording updates immediately for the eye and is announced once for the ear, and the
   * two need no longer disagree.
   *
   * Optional, defaulting to false, so a caller with nothing asynchronous behind it - or one written
   * before this input existed - keeps its present behaviour exactly.
   *
   * Compared against `true` rather than tested for truthiness, in keeping with this component's rule
   * against truthiness tests: zero and minus one are legitimate values throughout this data.
   *
   * @param value Whether a request for this page is in flight.
   */
  @Input({ transform: toPending }) loading = false;

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
   * The four step controls, read from the view so focus can be repaired after a terminal step.
   *
   * Read as element references rather than looked up by selector at the document level so that a screen
   * mounting two pagers - a listing and a picker within it - can never move focus into the other one.
   */
  @ViewChild('firstStep') private firstStep?: ElementRef<HTMLButtonElement>;

  @ViewChild('previousStep') private previousStep?: ElementRef<HTMLButtonElement>;

  @ViewChild('nextStep') private nextStep?: ElementRef<HTMLButtonElement>;

  @ViewChild('lastStep') private lastStep?: ElementRef<HTMLButtonElement>;

  /**
   * The document, for reading which element holds focus.
   *
   * Injected rather than referenced as the global so the component stays testable and renderer-agnostic;
   * nothing here writes to the document beyond calling `focus()` on its own button.
   */
  private readonly document = inject(DOCUMENT);

  /**
   * Which terminal step a person just activated, while its effect is still pending.
   *
   * ⚠ THIS EXISTS BECAUSE FIRST AND LAST DISABLE THEMSELVES. Activating "Last page" moves to the last
   * page, at which point "Last page" is unavailable and becomes genuinely `disabled` - and a browser
   * discards focus on an element that becomes disabled, dropping it to `body`. Runtime testing measured
   * exactly that: focus reached `body` after First and after Last, while Next and Previous retained it
   * because they stay enabled mid-range. A keyboard user was returned to the top of the document and had
   * to tab back through the entire page to reach the pager again.
   *
   * Held as pending rather than acted on at once because the page has NOT changed yet when the click is
   * handled: the emission asks the consumer for a page, the consumer fetches it, and the disabled state
   * only materialises when the new index is bound back. The repair therefore has to wait for that bind,
   * which is what {@link ngOnChanges} and {@link ngAfterViewChecked} between them do.
   */
  private pendingStep: 'first' | 'last' | null = null;

  /**
   * Whether the bound page has changed since a terminal step was activated.
   *
   * Separating "asked for" from "arrived" is what bounds the repair: without it a request that never
   * completes would leave focus movement armed indefinitely, and a later unrelated render could then
   * move focus with no person having asked for it.
   */
  private stepApplied = false;

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
   * Whether the pager renders anything at all.
   *
   * TRUE AS SOON AS THERE IS A RESULT TO COUNT, which is a wider condition than {@link isNavigable} on
   * purpose: the group carries a range summary as well as the steps, and the summary is the only
   * on-screen confirmation of how many records a filter matched. Runtime testing measured what tying
   * the two together cost - a filter that narrowed 250 portals to seven removed the whole group, so the
   * screen showed seven rows and no statement anywhere that seven was the entire match set, on every
   * list. The steps themselves remain gated on {@link isNavigable}, because four permanently disabled
   * buttons would state the opposite of the truth about what can be reached.
   *
   * MIGRATION: a documented divergence from the legacy portals screen and a return to the legacy USERS
   * screen. Both ran the identical guard `If SuppressPager And ctlPagingControl.Visible Then
   * ctlPagingControl.Visible = (PageSize < TotalRecords)` - `Website/admin/Portal/Portals.ascx.vb`
   * L155-L157 and `Website/admin/Users/Users.ascx.vb` L278-L280 - and differed only in
   * `SuppressPager`: portals hard-coded `Return True` at `Portals.ascx.vb` L112 with the real setting
   * commented out at L110-L111, so its guard ran and its pager vanished for a single page, while users
   * read the genuine `Display_SuppressPager` setting whose default is `False`
   * (`Library/Components/Users/UserModuleBase.vb` L131-L133), so its guard never ran and ITS PAGER
   * STAYED VISIBLE FOR A SINGLE PAGE. The legacy pager rendered a status cell on every visible pass
   * (`Library/Controls/PagingControl.vb` L153-L161), so a visible single-page pager did show a
   * position readout. Keeping the group is therefore the legacy users behaviour, and the divergence is
   * confined to the portals screen, whose own guard was disabled code.
   *
   * Nothing renders while there is nothing to count: a total of zero and an unresolved page size both
   * report no pages, and a zero-result state belongs to the empty-state component, which says what
   * happened in words rather than as "0-0 of 0".
   */
  get isRendered(): boolean {
    return this.totalPages > 0;
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
   * The value bound to the range summary's `aria-busy`, or `null` at rest.
   *
   * A getter rather than a stored value so it cannot fall out of step with {@link loading}, and `null`
   * rather than the string "false" when settled so the attribute is ABSENT at rest — which is exactly how
   * the shared grid reports the same state, and the two are read together by anyone auditing how this
   * application reports progress. The input transform has already settled the flag to a definite boolean,
   * so no truthiness test appears here either.
   */
  get ariaBusy(): 'true' | null {
    return this.loading ? 'true' : null;
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
    this.armStepRepair('first');
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
    this.armStepRepair('last');
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
   * Returns focus to a usable step once the activated one has disabled itself.
   *
   * Runs as a view-checked hook because the repair depends on the DISABLED PROPERTY HAVING BEEN WRITTEN,
   * which happens during the same change-detection pass that binds the new page - not when the page
   * arrives in the model. Every early return below is a plain identity or null test, so the common case
   * where nothing is pending costs one comparison.
   *
   * Focus is only taken when it is not already somewhere a person put it: the activated button or the
   * document body, which are the only two places a browser leaves it after disabling the element under
   * the pointer or the caret. Anything else means focus has moved on for another reason and must be left
   * alone.
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
   * Arms the focus repair for a step that will disable itself, if focus is on it.
   *
   * The check that focus is ON THE BUTTON is what stops a programmatic call to {@link goLast} from
   * pulling focus out of whatever a person was using. A pointer click focuses the button in every browser
   * this application targets, and a keyboard activation necessarily has focus on it, so the affordance is
   * repaired in both cases a person can actually produce.
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
