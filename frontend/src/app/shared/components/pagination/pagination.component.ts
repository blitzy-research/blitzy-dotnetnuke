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
 * ZERO, because the base carried across this component's boundary is the wire's. The
 * legacy screens kept a one-based counter and subtracted one immediately before every
 * call down to the provider — `Website/admin/Portal/Portals.ascx.vb` L142 reads
 * `PortalController.GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)`
 * and `Website/admin/Users/Users.ascx.vb` repeats the same subtraction at L265, L269,
 * L271 and L274 — so the one-based number was never anything but a presentation detail.
 *
 * Named rather than written as a bare zero because zero is a REAL page here, not the
 * absence of one. `Library/Components/Shared/Null.vb` L41 defines the legacy
 * absent-integer marker as minus one, and the identity seeds make both zero and minus
 * one legitimate values elsewhere in this schema, so a page index is never tested for
 * truthiness or for its sign anywhere below.
 */
const FIRST_PAGE_INDEX = 0;

/**
 * The page size this component reports when no usable one has been bound.
 *
 * NOT A DEFAULT PAGE SIZE.
 *
 * MIGRATION: the page size is a PER-PORTAL SETTING rather than a constant, so this
 * component never assumes one — the two legacy paged screens did not even agree with each
 * other. `Website/admin/Users/Users.ascx.vb` L114-L119 reads the genuine
 * `UserModuleBase.GetSetting(UsersPortalId, "Records_PerPage")`, coercing it with an
 * Option-Strict-off `CType(setting, Integer)` at L117 that has to become an explicit
 * conversion here, and its default is 10
 * (`Library/Components/Users/UserModuleBase.vb` L134-L136). But
 * `Website/admin/Portal/Portals.ascx.vb` L92-L98 has that same read COMMENTED OUT at
 * L94-L95 and hard-codes 20 at L96. A component that picked either number would be wrong
 * on the other screen, so the consumer supplies it and this value marks only "nothing
 * usable has arrived yet".
 *
 * Zero is the natural marker because it is not a page size at all: the API's own paging
 * validator rejects a size of zero or below, so no response envelope can carry one. It
 * is nevertheless REACHABLE and ORDINARY rather than exceptional — the shared
 * `emptyPagedResult()` factory in `core/models/paged-result.model.ts` seeds `pageSize`
 * to exactly zero, and both signal stores hold that empty page as their initial state,
 * so a feature that binds the served page size sees zero until its first response
 * arrives. A pager with no usable page size renders nothing and stays inert.
 */
const UNRESOLVED_PAGE_SIZE = 0;

/**
 * The pager's visible and assistive-technology wording.
 *
 * MIGRATION: authored fresh. There is NO legacy wording to carry across — the legacy
 * pager was a server control with no resource keys of its own, so none of the 37 in-scope
 * `App_LocalResources` resource files names a pager string. Localisation is deliberately
 * not ported, so these are plain literals rather than translated lookups.
 *
 * Held as constants and bound rather than written as template text because Angular
 * compiles templates with whitespace preservation disabled, which collapses runs of
 * whitespace inside a text node; an interpolated value is not collapsed. Every one is
 * PLAIN TEXT and is rendered through interpolation, never as markup.
 *
 * Declared above the component so it is initialised before the class body that binds it.
 */
const PAGINATION_LABELS = {
  /**
   * Accessible name for the group the template wraps these controls in.
   *
   * Names a `role="group"`, NOT a landmark. Semantic landmarks belong to the application
   * shell in `layout/**`, which already renders the single navigation landmark for the
   * site; a second one here would announce a duplicate landmark on every list screen.
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
 * Sanitising at the boundary rather than in each derivation is what lets every getter
 * below divide, add and compare without re-guarding: after this runs, `page` is always a
 * finite non-negative integer, so no arithmetic in this component can produce a
 * not-a-number or an infinite result.
 *
 * A page index PAST THE END IS DELIBERATELY PRESERVED. It is a real state rather than a
 * fault — records can be removed between a page being requested and rendered, which both
 * signal stores model explicitly — so it is not clamped here. It is clamped only where it
 * is USED, so the readout cannot contradict the data and no out-of-range index can be
 * emitted. Clamping it here instead would silently rewrite the consumer's own state.
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
 * A size of zero, a negative size, a fraction, a not-a-number and an infinity all resolve
 * to {@link UNRESOLVED_PAGE_SIZE}, which renders nothing rather than raising. Rendering
 * nothing is the right outcome for all five, because the only way any of them can arrive
 * is a page size that has not resolved yet — most commonly the zero that
 * `emptyPagedResult()` seeds — and a screen must not fail because its first paint
 * happened before its first response.
 *
 * A size of zero is NOT read as "every match on one page". That mode does not exist in
 * this API, and inventing it would render a plausible-looking single page that a reader
 * could not distinguish from a real one. Nothing is rendered instead, which is
 * indistinguishable from the correct treatment of a genuinely unpaged resource.
 *
 * The API's page-size MAXIMUM is deliberately not enforced. That bound constrains what a
 * client may ask for, whereas this component only renders the size a response came back
 * with; refusing a large page would blank a screen that had legitimately been served one.
 *
 * @param value The bound page size.
 * @returns A whole number of at least 1, or {@link UNRESOLVED_PAGE_SIZE} if unusable.
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
 * A total of zero is a REAL, MEANINGFUL COUNT — "no records matched" — and never a
 * missing one, which is why it is preserved rather than treated as absent. Only values
 * the API could not have produced are corrected: a negative total, a fraction, a
 * not-a-number and an infinity all become zero, keeping them out of the division that
 * derives the page count.
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
 * Purely presentational. It takes three numbers in and reports one number out: it holds
 * no data, fetches nothing, injects nothing and does not know what is being paged. The
 * consuming feature owns the request and owns the page state; this component never
 * changes its own {@link page}, so it cannot claim to be on a page whose request failed.
 *
 * The three inputs are exactly three members of the API's paging metadata, so a feature
 * binds them straight from the envelope it received rather than deriving anything. Every
 * derivation this component needs — the page count, the range on show, whether a step is
 * available — is computed here, once, so no two screens can compute them differently.
 *
 * ## The page base
 *
 * MIGRATION: the boundary is ZERO-BASED and the display is ONE-BASED, and the conversion
 * happens here. Two legacy facts sit one line apart and settle it together:
 * `Website/admin/Portal/Portals.ascx.vb` L142 passes `CurrentPage - 1` to the provider,
 * so the DATA base is zero; L148-L150 then hand the pager `TotalRecords`, `PageSize` and
 * the unmodified one-based `CurrentPage`, so the DISPLAY base is one. Both bases are
 * therefore reproduced faithfully: {@link page} matches the wire exactly, which removes
 * any mapping at the boundary, and {@link displayPage} adds the one a person expects.
 * {@link pageChange} emits a zero-based index, so a store passes the value straight
 * through in both directions and performs no arithmetic on it whatsoever.
 *
 * ## What this replaces
 *
 * MIGRATION: this component is a PURE NET ADDITION rather than a port. The legacy grids
 * styled a pager they did not have: the `DataGrid_Pager` class is referenced four times in
 * markup — `portals.ascx` L19, `users.ascx` L30, `roles.ascx` L32 and
 * `ProfileDefinitions.ascx` L15 — and defined in ZERO stylesheets anywhere in the
 * repository, so there is no legacy appearance to be faithful to. Only two of the eight
 * in-scope grids actually paged at all, each hosting a `dnn:pagingcontrol` server control
 * (`portals.ascx` L58 and `users.ascx` L83), and in both cases the control sat AFTER the
 * closing grid tag, separated by a `<br><br>`. It is therefore reproduced as a SIBLING of
 * the table with spacing carried by the stylesheet, never as a row inside it.
 *
 * MIGRATION: the legacy pager posted the whole page back to the server to change page,
 * and the legacy total arrived through a `ByRef totalRecords` out-parameter the caller had
 * to supply and read back. This reports an index and the feature issues one request, and
 * the total travels inside the response envelope, so a page and its total can no longer be
 * read half-updated.
 *
 * MIGRATION: keyboard operability, the programmatic disabling of unavailable steps, the
 * accessible names on every control and the focus ring the stylesheet supplies are all NET
 * ADDITIONS. The legacy stylesheets declared no focus styling whatsoever, so there is no
 * legacy behaviour being replaced here.
 */
@Component({
  selector: 'app-pagination',
  standalone: true,
  // Nothing to import: the template uses only native elements and the built-in control
  // flow blocks, which need no import in this Angular version.
  imports: [],
  templateUrl: './pagination.component.html',
  styleUrl: './pagination.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PaginationComponent {
  /**
   * The zero-based index of the page currently shown.
   *
   * Zero-based to match the API, so a consumer binds the index it received and reads back
   * the index it must request. Zero is the FIRST PAGE and never "no page": it is never
   * tested for truthiness anywhere below.
   *
   * An index past the last page is accepted rather than corrected, because records can be
   * removed between a page being requested and rendered. It is constrained where it is
   * used, so neither the readout nor an emitted index can fall outside the real range.
   */
  @Input({ required: true, transform: toPageIndex }) page = FIRST_PAGE_INDEX;

  /**
   * The number of items on a full page.
   *
   * Supplied by the consumer and NEVER assumed. The legacy screens disagreed — 20
   * hard-coded on portals, a per-portal setting defaulting to 10 on users — so no default
   * of any kind is applied here; see {@link UNRESOLVED_PAGE_SIZE}.
   *
   * A value the API could not have produced is reduced to {@link UNRESOLVED_PAGE_SIZE} by
   * {@link toPageSize} instead of raising, so a screen whose page size has not resolved
   * renders no pager rather than failing to render at all.
   *
   * A GENUINELY UNPAGED RESOURCE DOES NOT RENDER THIS COMPONENT. Several administration
   * resources return every match in one response by design — roles, role groups, portal
   * aliases, profile property definitions, module definitions, desktop modules and the
   * page tree among them — and the way to express that is for the feature to omit the
   * pager. There is deliberately no input for suppression, because "there is nothing to
   * page through" is the feature's fact rather than this component's.
   */
  @Input({ required: true, transform: toPageSize }) pageSize = UNRESOLVED_PAGE_SIZE;

  /**
   * The total number of matches across every page.
   *
   * Not the number of items on this page. Divided by {@link pageSize} it gives the page
   * count, and compared against zero it distinguishes "nothing matched" from "past the end
   * of the results", which read differently to a person. Zero is a real count.
   */
  @Input({ required: true, transform: toTotalCount }) totalCount = 0;

  /**
   * Emits the zero-based index of the page a person asked for.
   *
   * ZERO-BASED, never the one-based number on screen. Only ever emits a whole index that
   * lies inside the available range and differs from the current {@link page}, so a
   * consumer may act on it without re-validating it — which is exactly what both signal
   * stores rely on, since neither clamps the index it is given. Every path funnels through
   * one guard for that reason.
   */
  @Output() readonly pageChange = new EventEmitter<number>();

  /** The pager's wording, bound by the template. */
  readonly labels = PAGINATION_LABELS;

  /**
   * The number of pages the result set spans.
   *
   * MIGRATION: this count is DERIVED HERE, and only because it cannot be received. The
   * response envelope carries a server-computed page count on its paging metadata, and
   * that value is the better one wherever a consumer holds it — the shared paged-result
   * contract says outright that the count must not be recomputed from the total and the
   * page size. This component's input surface is fixed at three members by the design
   * system, so the server's count cannot reach it and the division is unavoidable. It is
   * therefore performed defensively rather than casually.
   *
   * Every unsafe input is already gone by the time this runs: {@link toPageSize} has
   * reduced an unusable size to {@link UNRESOLVED_PAGE_SIZE} and {@link toTotalCount} has
   * reduced an unusable total to zero, so both guards below are equality tests against a
   * known marker rather than attempts to detect a bad number late. The result is always a
   * whole number at or above zero: it can never be negative, never infinite and never a
   * not-a-number, and the division is only ever reached with a divisor of at least one.
   *
   * Reports ZERO when there is nothing to page through. Zero rather than one, to match the
   * shared contract's own factories: `emptyPagedResult()` reports zero total pages for an
   * empty set, and `unpagedResult()` reports zero for an empty collection. Nothing is
   * rendered in that state, so no readout ever shows a page count of zero.
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
   * MIGRATION: hidden when everything already fits on one page. This is BYTE-EQUIVALENT to
   * the legacy portals screen and a documented divergence only from the legacy users
   * screen, because the two behaved differently despite sharing an identical guard.
   *
   * Both screens ran `If SuppressPager And ctlPagingControl.Visible Then
   * ctlPagingControl.Visible = (PageSize < TotalRecords)` — `Portals.ascx.vb` L155-L157 and
   * `Users.ascx.vb` L278-L280, character for character the same. What differed was
   * `SuppressPager`. The portals screen declared it at `Portals.ascx.vb` L108-L114 with its
   * real setting read commented out at L110-L111 and a hard-coded `True` returned at L112,
   * so its guard RAN and its pager disappeared for a single page. The users screen read the
   * genuine `Display_SuppressPager` setting at `Users.ascx.vb` L129-L134, whose default is
   * `False` (`Library/Components/Users/UserModuleBase.vb` L131-L133), so its guard NEVER
   * RAN and its pager was always visible. The two page-size properties diverged for the
   * same reason, at `Portals.ascx.vb` L92-L98 and `Users.ascx.vb` L114-L119.
   *
   * Because "show when `PageSize < TotalRecords`" is exactly "hide when the total is no
   * greater than the page size", the rule below reproduces the portals guard precisely:
   * more than one page exists if and only if the total exceeds the page size.
   *
   * The two legacy escapes that hid the pager outright — the expired-portals filter at
   * `Portals.ascx.vb` L138-L140 and the unauthorised and on-line user filters at
   * `Users.ascx.vb` L258-L263 — switched to an unpaged query. Those are FEATURE decisions,
   * expressed by a feature not rendering this component, which is why there is no
   * visibility input here. The on-line filter is dropped altogether, users-online being out
   * of scope.
   */
  get isNavigable(): boolean {
    return this.totalPages > 1;
  }

  /**
   * The index of the last page, or minus one when there are no pages.
   *
   * Minus one is arithmetic here — one below the first index — and carries none of the
   * legacy absent-integer meaning. Nothing compares against it for absence; the callers
   * below test the page count instead.
   */
  private get lastPageIndex(): number {
    return this.totalPages - 1;
  }

  /**
   * {@link page} constrained to the pages that actually exist.
   *
   * Equal to {@link page} in every ordinary case. It differs only when the bound index is
   * past the end, which happens when records are removed between a page being requested
   * and rendered — a state both signal stores model explicitly. Constraining it at the
   * point of use keeps the readout truthful ("the last page of three" rather than "page
   * twelve of three") and keeps the range summary from reporting items beyond the total,
   * while leaving the consumer's own state untouched.
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
   * MIGRATION: this is the `+ 1` that reconciles the two legacy bases. The legacy pager was
   * handed the one-based `CurrentPage` at `Portals.ascx.vb` L150 while the provider was
   * handed `CurrentPage - 1` at L142, so a person saw one-based numbering over zero-based
   * data. Adding the one HERE rather than in each feature is the whole reason this
   * component exists: it is the single off-by-one in the migration, and it is made once.
   *
   * Derived from the constrained index, so a page past the end reads as the last real page
   * rather than as a number the data cannot support.
   */
  get displayPage(): number {
    return this.effectivePage + 1;
  }

  /** Whether a previous page exists and can be requested. */
  get canGoPrevious(): boolean {
    return this.totalPages > 0 && this.effectivePage > FIRST_PAGE_INDEX;
  }

  /** Whether a following page exists and can be requested. */
  get canGoNext(): boolean {
    return this.totalPages > 0 && this.effectivePage < this.lastPageIndex;
  }

  /**
   * The one-based number of the first item on the page shown.
   *
   * Zero when nothing matched, so the summary reads "0 to 0 of 0" rather than "1 to 0 of
   * 0". One-based for the same reason {@link displayPage} is: a person counts from one.
   */
  get firstItemNumber(): number {
    if (this.totalPages === 0) {
      return 0;
    }

    return this.effectivePage * this.pageSize + 1;
  }

  /**
   * The one-based number of the last item on the page shown.
   *
   * Clamped to the total, because a final page is usually short and reporting a number
   * beyond the total would be wrong rather than merely untidy.
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
   * Neither this nor any sibling below repeats the availability test that
   * {@link canGoPrevious} and {@link canGoNext} perform for the template. Each simply
   * names the page it wants and lets {@link requestPage} decide, which is what makes that
   * guard the single statement of the emission rule rather than one copy of several.
   * Asking for the first page while already on it is refused there, because the target
   * equals the current page.
   */
  goFirst(): void {
    this.requestPage(FIRST_PAGE_INDEX);
  }

  /**
   * Requests the previous page, or does nothing when there is none.
   *
   * From a page past the end, this returns to the LAST REAL PAGE rather than stepping back
   * from a page that does not exist, which would skip the last real page entirely. On the
   * first page the target is one below the first index, which {@link requestPage} refuses
   * as out of range.
   */
  goPrevious(): void {
    const target = this.page > this.lastPageIndex ? this.lastPageIndex : this.effectivePage - 1;

    this.requestPage(target);
  }

  /**
   * Requests the following page, or does nothing when there is none.
   *
   * On the last page the target is one beyond the last index, which {@link requestPage}
   * refuses as out of range.
   */
  goNext(): void {
    this.requestPage(this.effectivePage + 1);
  }

  /**
   * Requests the last page, or does nothing when it is already shown.
   *
   * Refused by {@link requestPage} when already there, because the target equals the
   * current page.
   */
  goLast(): void {
    this.requestPage(this.lastPageIndex);
  }

  /**
   * The single point at which a page change is emitted.
   *
   * Every navigation method funnels through here so the emission guarantee is stated ONCE
   * and cannot drift between them: none of them pre-checks availability, because a second
   * copy of this rule is a second place for it to go wrong. Nothing is emitted unless the
   * target is a whole index inside the pages that exist and differs from the page bound.
   *
   * That guarantee is LOAD-BEARING rather than merely tidy: both signal stores pass the
   * emitted index straight to the API without clamping it, on the documented understanding
   * that this component emits only an index inside the available range. This guard is the
   * only thing enforcing it.
   *
   * The comparison that suppresses a redundant emission is made against the RAW
   * {@link page}, not the constrained one, so returning into range from a page past the end
   * is still reported.
   *
   * There is deliberately NO whole-number check on the target. It would be unreachable
   * code: the boundary transforms admit only whole numbers, and every target is built from
   * them by truncation, a ceiling, a minimum or a step of one, so a fractional target
   * cannot be constructed. Guarding against it anyway would add a line that can never run
   * and can never be tested, which reads as a real possibility to the next person.
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
