/**
 * The shared, presentational record grid. This is the single replacement for the eight `asp:DataGrid`
 * instances across the five in-scope administration trees.
 *
 * @typeParam TRow The row contract carried on the current page - always a transfer contract off the wire,
 * never a persisted entity.
 */

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  TemplateRef,
  computed,
  inject,
  signal,
} from '@angular/core';

import type { OnInit } from '@angular/core';

// The ONE standalone directive imported here, and the only import that is not a composed sibling.
import { NgTemplateOutlet } from '@angular/common';

import type { SortDirection } from '../../../core/models/paged-result.model';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../loading-spinner/loading-spinner.component';

/**
 * Inline alignment of a column's heading or of its body cells. Logical rather than physical - `start` and
 * `end` follow the writing direction, so a right-to-left reader gets the correct edge without a second
 * rule.
 */
type DataTableAlign = 'start' | 'center' | 'end';

/**
 * What a column puts in its body cells. - `text` - bound or formatted text, covering the legacy
 * `dnn:textcolumn` and `asp:BoundColumn`, including the one that carried `DataFormatString="{0:0.00}"`. -
 * `template` - caller-supplied cell content, covering `asp:TemplateColumn` and the two inline-editable
 * `dnn:checkboxcolumn` cells that posted back on change. - `actions` - projected row controls, covering
 * `dnn:imagecommandcolumn`.
 */
type DataTableColumnKind = 'text' | 'template' | 'actions';

/** The `aria-sort` states a sortable heading can report. */
type DataTableAriaSort = 'ascending' | 'descending' | 'none';

/**
 * A track size a column may declare, closed at the four forms that are BOTH valid CSS for `inline-size`
 * on a `col` element AND permitted by the design system. Two independent defects follow from accepting
 * arbitrary text, and neither produces an error anywhere: - A PIXEL LITERAL is forbidden by the token
 * vocabulary and would be applied faithfully.
 */
export type DataTableWidth =
  | `${number}%`
  | 'min-content'
  | 'max-content'
  | `var(--${string})`;

/**
 * The context a {@link DataTableTemplateColumn.cellTemplate} is rendered against. `$implicit` is the row,
 * so a caller may write `let-row` and receive it without naming a member.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableCellContext<TRow> {
  /** The row, available as `let-row` with no member name. */
  readonly $implicit: TRow;

  /** The row again, for `let-row="row"` callers that prefer to be explicit. */
  readonly row: TRow;

  /** The column being rendered, so one template can serve several columns. */
  readonly column: DataTableColumn<TRow>;

  /** Zero-based position of the row within the CURRENT PAGE, not within the match set. */
  readonly rowIndex: number;
}

/**
 * One column of a {@link DataTableComponent}. Every member is `readonly`: a column set describes a layout
 * the caller has already decided, and a component that could rewrite it would be describing something the
 * caller never asked for.
 *
 * @typeParam TRow The row contract this column reads.
 */
export type DataTableColumn<TRow> = DataTableColumnCommon &
  DataTableColumnHeading &
  DataTableColumnBody<TRow>;

/**
 * The members every column carries, whatever it puts in its body cells. Never a complete column on its
 * own: a column is always the full intersection declared by {@link DataTableColumn}, and this fragment
 * exists so that the shared members are written once rather than repeated in each arm of the union.
 */
export interface DataTableColumnCommon {
  /**
   * Stable identity of the column, unique within one column set. This is the `track` expression of the
   * heading and cell loops, and the value reported as {@link DataTableSortChange.key}, so for a sortable
   * column it must be the sort name the collection endpoint accepts.
   */
  readonly key: string;

  /**
   * Visible heading text. Free to duplicate another column's label, and in the role list it genuinely
   * does.
   */
  readonly label: string;

  readonly rowHeader?: boolean;

  /** Inline alignment of the HEADING. Defaults to `start`. */
  readonly headerAlign?: DataTableAlign;

  /** Inline alignment of the BODY cells. Defaults to `start`. */
  readonly bodyAlign?: DataTableAlign;

  /**
   * Track width of the column, applied through a `col` element in the table's `colgroup` so that no cell
   * rule carries a size. Closed at the four forms {@link DataTableWidth} admits, each of which is valid
   * CSS for `inline-size` on a `col` element.
   */
  readonly width?: DataTableWidth;
}

/** The heading policy of a column: whether it offers sorting, and whether its label is painted. */
export type DataTableColumnHeading =
  | {
      /** Whether the heading offers sorting. Absent or `false` means it does not. */
      readonly sortable?: false;

      /**
       * Whether to hide the heading text visually while keeping it announced. For columns whose heading
       * would be noise - a column of row commands, or an indicator with no meaningful name.
       */
      readonly headerHidden?: boolean;
    }
  | {
      /** Whether the heading offers sorting. */
      readonly sortable: true;

      /**
       * Not available on a sortable column: hiding the label of a sortable heading leaves an empty,
       * unlabelled button. See {@link DataTableColumnHeading}.
       */
      readonly headerHidden?: false;
    };

/**
 * What a column puts in its body cells, and the payload that kind requires. Discriminated on {@link
 * DataTableColumnKind}, with `text` as the default arm so that the overwhelmingly common bound-text
 * column stays terse.
 *
 * @typeParam TRow The row contract this column reads.
 */
export type DataTableColumnBody<TRow> =
  | DataTableTextColumn<TRow>
  | DataTableFormattedColumn<TRow>
  | DataTableTemplateColumn<TRow>
  | DataTableActionsColumn<TRow>;

/**
 * A column rendering one member of the row as plain text - the legacy `dnn:textcolumn` and
 * `asp:BoundColumn`.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableTextColumn<TRow> {
  /** The default kind, so it may be omitted entirely. */
  readonly kind?: 'text';

  /**
   * Row member to render as plain text. Typed as a key of the row, so a mistyped member name is a compile
   * error rather than a blank column.
   */
  readonly field: keyof TRow & string;

  /** Not available on a bound column: state a formatter or a member, never both. */
  readonly value?: never;

  /** Not available on a text column: a text column renders no template. */
  readonly cellTemplate?: never;
}

/**
 * A column whose text is computed from the row - the legacy formatted and derived columns.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableFormattedColumn<TRow> {
  /** The default kind, so it may be omitted entirely. */
  readonly kind?: 'text';

  /** Not available on a formatted column: state a formatter or a member, never both. */
  readonly field?: never;

  /**
   * PURE formatter producing the cell's text. Covers the formatted and derived columns: prices, periods,
   * expiry dates, an alias list composed from an identifier, a postal address composed from six profile
   * members.
   *
   * @param row The row being rendered.
   * @returns The text to display.
   */
  readonly value: (row: TRow) => string;

  /** Not available on a text column: a text column renders no template. */
  readonly cellTemplate?: never;
}

/**
 * A column rendering caller-supplied content - the legacy `asp:TemplateColumn`, including the two
 * inline-editable checkbox cells that posted back on change.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableTemplateColumn<TRow> {
  /** Declared explicitly: a template column is never inferred. */
  readonly kind: 'template';

  /** Not available: a template column renders its template, never bound text. */
  readonly field?: never;

  /** Not available: a template column renders its template, never formatted text. */
  readonly value?: never;

  /**
   * Caller-supplied cell content, for anything richer than text. REQUIRED, because a template column with
   * no template is a blank column on every row.
   */
  readonly cellTemplate: TemplateRef<DataTableCellContext<TRow>>;
}

/**
 * A column of projected row commands - the legacy `dnn:imagecommandcolumn`. Declared as its own kind
 * rather than inferred, because it is a statement about semantics and not about where the content came
 * from: an actions cell suppresses row activation so that pressing Edit never doubles as selecting the
 * row.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableActionsColumn<TRow> {
  /** Declared explicitly: no inference can supply this. */
  readonly kind: 'actions';

  /** Not available: an actions column renders its template, never bound text. */
  readonly field?: never;

  /** Not available: an actions column renders its template, never formatted text. */
  readonly value?: never;

  /**
   * The row commands, as a template the caller supplies. REQUIRED: a commands column with no commands is
   * an empty column, and a per-row command can only be expressed as a template - two legacy grids make a
   * command conditional per row, and one derives both a command's LABEL and its very identity from the
   * row.
   */
  readonly cellTemplate: TemplateRef<DataTableCellContext<TRow>>;
}

/**
 * A reader's request to reorder the match set. Carries the direction as well as the key, so a consumer
 * never has to remember what it last asked for in order to interpret the next request.
 */
export interface DataTableSortChange {
  /**
   * The {@link DataTableColumnCommon.key} the reader activated, which is the endpoint's sort name. ⚠
   * ALWAYS PRESENT, EVEN WHEN THE ORDERING IS BEING CLEARED. It names the heading that was pressed, not
   * the ordering that results, so a consumer never has to reason about a null key; what to do about it is
   * settled by {@link direction} alone.
   */
  readonly key: string;

  /**
   * The direction to order in, in the server's own spelling, or `null` to REMOVE the ordering entirely. ⚠
   * THE NULL IS A THIRD STATE, NOT AN ABSENT VALUE. A listing arrives with no ordering at all - every one
   * of these stores initialises its sort coordinate to null and the request omits both parameters - so
   * "no ordering" is a state the reader is already in when they arrive, and a two-step toggle made it
   * unreachable the moment they left it: the only way back was to reload the page.
   */
  readonly direction: SortDirection | null;
}

/**
 * A heading cell, fully derived so the template evaluates no expression of its own.
 *
 * @typeParam TRow The row contract.
 */
interface DataTableHeaderCell<TRow> {
  /** The column this heading describes, for callers that need the descriptor itself. */
  readonly column: DataTableColumn<TRow>;

  /** {@link DataTableColumnCommon.key}, and the `track` expression of the heading loop. */
  readonly key: string;

  /** {@link DataTableColumnCommon.label}. */
  readonly label: string;

  /** Whether the label is painted, or announced only. */
  readonly labelVisible: boolean;

  /** Whether this heading offers sorting. */
  readonly sortable: boolean;

  /** Whether this heading carries the ACTIVE sort, per `sortBy` alone. */
  readonly sorted: boolean;

  /**
   * The sort control's accessible name, or `null` for a column that offers no sorting. The visible
   * heading text alone names the COLUMN and not the ACTION, so a reader who lands on the control hears
   * "Module Title, button" and is told nothing about what pressing it does.
   */
  readonly sortLabel: string | null;

  /**
   * The `aria-sort` value, or `null` to omit the attribute. Omitted for a column that offers no sorting:
   * `none` means "sortable but not currently sorted", which on an unsortable column would be a false
   * claim.
   */
  readonly ariaSort: DataTableAriaSort | null;

  /** Resolved {@link DataTableColumnCommon.headerAlign}. */
  readonly align: DataTableAlign;
}

/**
 * One body cell, with its text already produced and its template context already built.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableBodyCell<TRow> {
  /** {@link DataTableColumnCommon.key}, and the `track` expression of the cell loop. */
  readonly key: string;

  /** Resolved {@link DataTableActionsColumn.kind}, deciding which branch the template takes. */
  readonly kind: DataTableColumnKind;

  /**
   * The cell's text for a `text` column, already formatted; the empty string otherwise. Never `null`,
   * never `undefined` and never the WORDS `null` or `undefined`.
   */
  readonly text: string;

  /** The caller's cell template for a `template` column, `null` otherwise. */
  readonly template: TemplateRef<DataTableCellContext<TRow>> | null;

  /** The context to render {@link template} against, `null` when there is none. */
  readonly context: DataTableCellContext<TRow> | null;

  /** Resolved {@link DataTableColumnCommon.bodyAlign}. */
  readonly align: DataTableAlign;

  readonly rowHeader: boolean;
}

/**
 * One body row, with every cell projected.
 *
 * @typeParam TRow The row contract.
 */
interface DataTableBodyRow<TRow> {
  /**
   * The row itself, and its own identity. This object reference is the `track` expression of the row loop
   * and the value compared for selection.
   */
  readonly row: TRow;

  /** Zero-based position within the current page. */
  readonly rowIndex: number;

  /**
   * One-based position among ALL rows of the table, counting the heading row as the first. Bound to
   * `aria-rowindex`.
   */
  readonly ariaRowIndex: number;

  /** The projected cells, in column order. */
  readonly cells: readonly DataTableBodyCell<TRow>[];
}

/** A `col` entry sizing one track of the table. */
export interface DataTableColumnWidth {
  /** {@link DataTableColumnCommon.key}, and the `track` expression of the `colgroup` loop. */
  readonly key: string;

  /** The resolved width, or `null` to let the column take its share automatically. */
  readonly width: string | null;
}

/** The ascending member of the wire sort vocabulary. */
const ASCENDING: SortDirection = 'Ascending';

/** The descending member of the wire sort vocabulary. */
const DESCENDING: SortDirection = 'Descending';

/** Inline alignment applied when a column states none. Neutral on purpose. */
const DEFAULT_ALIGN: DataTableAlign = 'start';

/**
 * Rows contributed by the heading section, for `aria-rowcount` and `aria-rowindex`. The template renders
 * exactly one heading row, and ARIA counts it: the heading is row one, so the first body row is row two.
 */
const HEADER_ROW_COUNT = 1;

/**
 * What a sort control's accessible name says before the column's own label. ⚠ THE TRAILING SPACE IS
 * LOAD-BEARING and the label is appended VERBATIM, because WCAG 2.5.3 Label in Name requires the
 * accessible name to contain the visible text as it appears - a voice-control user says the words they
 * can see.
 */
const SORT_LABEL_PREFIX = 'Sort by ';

/** Minimum `colspan` for the waiting and empty rows, so neither can span zero cells. */
const MINIMUM_COLUMN_SPAN = 1;

/**
 * Rows the body contributes while it is waiting or empty. The waiting and empty branches each render
 * exactly ONE spanning row.
 */
const MESSAGE_ROW_COUNT = 1;

/** Activation key that needs no default suppression. */
const ENTER_KEY = 'Enter';

/** Activation key whose default action scrolls the page and must be suppressed. */
const SPACE_KEY = ' ';

/**
 * The sortable, keyboard-operable record grid. Both the `track` expression of the row loop and the
 * selection comparison use the row object itself.
 *
 * @typeParam TRow The row contract carried on the current page.
 */
@Component({
  selector: 'app-data-table',
  standalone: true,
  imports: [NgTemplateOutlet, LoadingSpinnerComponent, EmptyStateComponent],
  templateUrl: './data-table.component.html',
  styleUrl: './data-table.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableComponent<TRow extends object> implements OnInit {
  private readonly columnsSignal = signal<readonly DataTableColumn<TRow>[]>([]);

  private readonly rowsSignal = signal<readonly TRow[]>([]);

  /** How a row identifies itself across redraws, or `null` to key on the row object. */
  private readonly rowKeySignal = signal<((row: TRow) => string | number) | null>(null);

  private readonly sortBySignal = signal<string | undefined>(undefined);

  private readonly sortDirSignal = signal<SortDirection>(ASCENDING);

  private readonly loadingSignal = signal(false);

  private readonly selectedRowSignal = signal<TRow | null>(null);

  private readonly rowsSelectableSignal = signal(false);

  /**
   * Sets the columns to render, in order. Public because the strict input-access check rejects a
   * non-public input at every consuming template; the same applies to every input on this component.
   *
   * @param value The column descriptors, or an absent value for none.
   * @throws Error when two columns share a key, when a key is blank, when a width is not one of the forms
   * {@link DataTableWidth} admits, or when a sortable heading also hides its label.
   */
  @Input()
  public set columns(value: readonly DataTableColumn<TRow>[] | null | undefined) {
    const next = value ?? [];
    assertColumnsAreValid(next);
    this.columnsSignal.set(next);
  }

  public get columns(): readonly DataTableColumn<TRow>[] {
    return this.columnsSignal();
  }

  /**
   * Sets the rows of the current page, already ordered and paged by the server. Read as `readonly` and
   * never mutated: these are the items of a paging envelope whose members are all `readonly`, because a
   * response has already happened and a component that rewrote one would be describing something the
   * server never said.
   *
   * @param value The rows to render, or an absent value for none.
   */
  @Input()
  public set rows(value: readonly TRow[] | null | undefined) {
    const nextRows = value ?? [];
    this.rowsSignal.set(nextRows);

    const selected = this.selectedRowSignal();
    if (selected !== null && nextRows.includes(selected) === false) {
      this.selectedRowSignal.set(null);
    }

    this.scheduleWindowUpdate();
  }

  public get rows(): readonly TRow[] {
    return this.rowsSignal();
  }

  /**
   * Sets how a row identifies itself across redraws, so its rendered row can be REUSED rather than
   * rebuilt. ⚠ WITHOUT THIS, KEYED REUSE CANNOT REACH THE DOM ON A RE-READ, AND THAT WAS MEASURED RATHER
   * THAN SUSPECTED. `@for` keys the body on whatever `track` names, and the only key a generic table can
   * invent for an arbitrary row contract is the row OBJECT itself.
   *
   * @param value A function returning the row's stable identity, or an absent value to key on the object.
   */
  @Input()
  public set rowKey(value: ((row: TRow) => string | number) | null | undefined) {
    this.rowKeySignal.set(value ?? null);
  }

  public get rowKey(): ((row: TRow) => string | number) | null {
    return this.rowKeySignal();
  }

  /**
   * Sets the {@link DataTableColumnCommon.key} the rows are currently ordered by, or an absent value when
   * the server's own ordering applies. Blank text and omission mean the same thing, matching the paging
   * contract, so a feature that clears its sort by binding the empty string gets an unsorted table rather
   * than a heading claiming to be sorted by nothing.
   *
   * @param value The active sort key, or an absent or blank value for none.
   */
  @Input()
  public set sortBy(value: string | null | undefined) {
    const isUsable = typeof value === 'string' && value.trim().length > 0;
    this.sortBySignal.set(isUsable ? value : undefined);
  }

  public get sortBy(): string | undefined {
    return this.sortBySignal();
  }

  /**
   * Sets the direction {@link sortBy} is applied in. Consulted only when a sort key is present.
   *
   * @param value The active direction, or an absent value for the default.
   */
  @Input()
  public set sortDir(value: SortDirection | null | undefined) {
    this.sortDirSignal.set(value ?? ASCENDING);
  }

  public get sortDir(): SortDirection {
    return this.sortDirSignal();
  }

  /**
   * Sets whether the table is waiting for data. While waiting, the body shows the shared progress
   * indicator instead of the previous page, so nobody reads stale rows that are about to be replaced, and
   * sort activation is refused so a second request cannot be queued behind the first.
   *
   * @param value Whether a request is in flight.
   */
  @Input()
  public set loading(value: boolean | null | undefined) {
    this.loadingSignal.set(value === true);
  }

  public get loading(): boolean {
    return this.loadingSignal();
  }

  @Output() public readonly sortChange = new EventEmitter<DataTableSortChange>();

  /** Emits the row that was activated. */
  @Output() public readonly rowSelect = new EventEmitter<TRow>();

  /** Whether a request is in flight, for the template's waiting branch. */
  protected readonly isLoading = this.loadingSignal.asReadonly();

  /** The selected row, or `null`. */
  protected readonly selectedRow = this.selectedRowSignal.asReadonly();

  /** Whether rows carry the selection affordance, for the template's row attributes. */
  protected readonly rowsSelectable = this.rowsSelectableSignal.asReadonly();

  /** Whether there is nothing to show and nothing on the way. */
  /**
   * The result-set size, as a sentence, for a POLITE live region. ⚠ WHY THIS EXISTS. Narrowing a listing
   * to nothing announced NOTHING. The rows were replaced and the empty state appeared, but neither is in
   * a live region, so a screen-reader user who typed a filter received no confirmation that anything had
   * happened at all - and the one place that did announce, the pager's own position readout, WITHDRAWS
   * itself when a filter leaves a single page, so the case most in need of a report was the case with no
   * reporter. ⚠ IT REPORTS THE COUNT AND NOT THE STATE, and reporting the count is what makes every
   * transition audible.
   */
  protected readonly resultSummary = computed<string>(() => {
    if (this.isLoading()) {
      return '';
    }

    const count: number = this.rowsSignal().length;

    if (count === 0) {
      return 'No records found.';
    }

    return count === 1 ? '1 record.' : `${count} records.`;
  });

  protected readonly isEmpty = computed(
    () => this.loadingSignal() === false && this.rowsSignal().length === 0,
  );

  /** Whether the waiting placeholder should REPLACE the rows. */
  protected readonly showWaitingPlaceholder = computed(
    () => this.loadingSignal() && this.rowsSignal().length === 0,
  );

  /** The value bound to the table's `aria-busy` attribute, or `null` when it is idle. */
  protected readonly ariaBusy = computed<'true' | null>(() =>
    this.loadingSignal() ? 'true' : null,
  );

  /** The scrolling container, needed to locate the table relative to the viewport. */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly destroyRef = inject(DestroyRef);

  /** Rows kept on either side of the visible range so a fast scroll does not show a gap. */
  private static readonly Overscan = 12;

  /** The height assumed for a row before any row has been measured. */
  private static readonly EstimatedRowHeight = 40;

  /** Rows below which windowing is not worth its own bookkeeping. */
  private static readonly DefaultVirtualizeThreshold = 100;

  private readonly virtualizeThresholdSignal = signal(
    DataTableComponent.DefaultVirtualizeThreshold,
  );

  private readonly rowHeightSignal = signal(DataTableComponent.EstimatedRowHeight);

  private readonly rowWindowSignal = signal<{ readonly start: number; readonly end: number } | null>(
    null,
  );

  /** Guards against queueing more than one measurement per frame. */
  private windowUpdateQueued = false;

  /** The row count above which only the rows near the viewport are rendered. */
  @Input()
  public set virtualizeThreshold(value: number | null | undefined) {
    this.virtualizeThresholdSignal.set(
      typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : 0,
    );
    this.scheduleWindowUpdate();
  }

  public get virtualizeThreshold(): number {
    return this.virtualizeThresholdSignal();
  }

  /** Whether windowing is engaged for the row set in hand. */
  protected readonly isWindowed = computed(() => {
    const threshold = this.virtualizeThresholdSignal();

    return threshold > 0 && this.rowsSignal().length > threshold;
  });

  /** The rows actually rendered: every row, or the window around the viewport. */
  protected readonly renderedRows = computed<readonly DataTableBodyRow<TRow>[]>(() => {
    const rows = this.bodyRows();

    if (!this.isWindowed()) {
      return rows;
    }

    const bounds = this.rowWindowSignal();

    if (bounds === null) {
      // Before the first measurement, render the overscan from the top. Rendering NOTHING here would leave
      // an empty table for one frame, which is the very teardown flash this component was changed to stop
      // producing.
      return rows.slice(0, DataTableComponent.Overscan * 2);
    }

    return rows.slice(bounds.start, bounds.end);
  });

  /** The height held by the spacer above the window, in pixels. */
  protected readonly leadingSpacerHeight = computed(() => {
    if (!this.isWindowed()) {
      return 0;
    }

    return (this.rowWindowSignal()?.start ?? 0) * this.rowHeightSignal();
  });

  /** The height held by the spacer below the window, in pixels. */
  protected readonly trailingSpacerHeight = computed(() => {
    if (!this.isWindowed()) {
      return 0;
    }

    const total = this.bodyRows().length;
    const end = this.rowWindowSignal()?.end ?? Math.min(total, DataTableComponent.Overscan * 2);

    return Math.max(total - end, 0) * this.rowHeightSignal();
  });

  /** Recomputes the visible window from the table's position in the viewport. */
  protected scheduleWindowUpdate(): void {
    if (this.windowUpdateQueued || typeof window === 'undefined') {
      return;
    }

    this.windowUpdateQueued = true;

    window.requestAnimationFrame(() => {
      this.windowUpdateQueued = false;
      this.updateWindow();
    });
  }

  /** Measures the rendered rows and settles which slice belongs on screen. */
  private updateWindow(): void {
    if (!this.isWindowed()) {
      this.rowWindowSignal.set(null);

      return;
    }

    const body = this.host.nativeElement.querySelector('tbody.data-table__body');
    const table = this.host.nativeElement.querySelector('table.data-table');

    if (body === null || table === null) {
      return;
    }

    const rendered = body.querySelectorAll('tr.data-table__row');
    const renderedHeight = Array.from(rendered).reduce(
      (total, row) => total + row.getBoundingClientRect().height,
      0,
    );

    if (rendered.length > 0 && renderedHeight > 0) {
      this.rowHeightSignal.set(Math.max(renderedHeight / rendered.length, 1));
    }

    const rowHeight = this.rowHeightSignal();
    const total = this.bodyRows().length;

    // Where the body starts relative to the viewport, in the scrolling document's coordinates. The leading
    // spacer is part of the body, so its own height is already included in that offset and must not be
    // subtracted again.
    const bodyTop = body.getBoundingClientRect().top;
    const viewportHeight = window.innerHeight;

    const firstVisible = Math.floor(Math.max(-bodyTop, 0) / rowHeight);
    const visibleCount = Math.ceil(viewportHeight / rowHeight);

    const start = Math.max(firstVisible - DataTableComponent.Overscan, 0);
    const end = Math.min(firstVisible + visibleCount + DataTableComponent.Overscan, total);

    const current = this.rowWindowSignal();

    if (current !== null && current.start === start && current.end === end) {
      return;
    }

    this.rowWindowSignal.set({ start, end });
  }

  /** Registers the scroll and resize listeners the window depends on. */
  private observeViewport(): void {
    if (typeof window === 'undefined') {
      return;
    }

    const onViewportChange = (): void => this.scheduleWindowUpdate();

    window.addEventListener('scroll', onViewportChange, { passive: true });
    window.addEventListener('resize', onViewportChange, { passive: true });

    this.destroyRef.onDestroy(() => {
      window.removeEventListener('scroll', onViewportChange);
      window.removeEventListener('resize', onViewportChange);
    });
  }

  /** Cells spanned by the waiting and empty rows. Floored at one so neither can span zero cells. */
  protected readonly columnSpan = computed(() =>
    Math.max(this.columnsSignal().length, MINIMUM_COLUMN_SPAN),
  );

  /**
   * Total rows the table actually renders, including the heading row, for `aria-rowcount`. The count of
   * the CURRENT PAGE, not of the match set: this component is handed one page and must not claim
   * knowledge of the rest.
   */
  protected readonly ariaRowCount = computed(() => {
    const records = this.rowsSignal();
    const rendersMessage = this.showWaitingPlaceholder() || records.length === 0;

    return (rendersMessage ? MESSAGE_ROW_COUNT : records.length) + HEADER_ROW_COUNT;
  });

  /**
   * The `aria-rowindex` of the waiting or empty row. It occupies the position the first record would have
   * held, immediately after the heading row.
   */
  protected readonly messageRowIndex = HEADER_ROW_COUNT + MESSAGE_ROW_COUNT;

  /**
   * The heading row's `aria-rowindex`. ARIA row indexes are one-based and COUNT the heading row, so the
   * heading is row one and the first body row is row two.
   */
  protected readonly headerRowIndex = HEADER_ROW_COUNT;

  /** Track sizes for the table's `colgroup`. */
  protected readonly columnWidths = computed<readonly DataTableColumnWidth[]>(() =>
    this.columnsSignal().map((column) => ({
      key: column.key,
      width: resolveWidth(column.width),
    })),
  );

  /** The heading cells, fully derived. */
  protected readonly headerCells = computed<readonly DataTableHeaderCell<TRow>[]>(() => {
    const activeKey = this.sortBySignal();
    const activeDirection = this.sortDirSignal();

    return this.columnsSignal().map((column) => {
      const sortable = column.sortable === true;
      const sorted = sortable && activeKey !== undefined && column.key === activeKey;

      return {
        column,
        key: column.key,
        label: column.label,
        labelVisible: column.headerHidden !== true,
        sortable,
        sorted,
        ariaSort: resolveAriaSort(sortable, sorted, activeDirection),
        sortLabel: sortable ? `${SORT_LABEL_PREFIX}${column.label}` : null,
        align: column.headerAlign ?? DEFAULT_ALIGN,
      };
    });
  });

  /**
   * The key `@for` uses to decide whether a rendered row can be reused. A method rather than a field
   * because the answer depends on the row, and a method rather than a computed because it is called per
   * row per redraw and derives nothing that needs caching - it either returns the row object it was
   * handed or calls one supplied function.
   *
   * @param bodyRow The projected row about to be rendered.
   * @returns The row's stable identity, or the row object itself when the feature named none.
   */
  protected trackBodyRow(bodyRow: DataTableBodyRow<TRow>): unknown {
    const identify = this.rowKeySignal();

    return identify === null ? bodyRow.row : identify(bodyRow.row);
  }

  /** The body rows with every cell projected. */
  protected readonly bodyRows = computed<readonly DataTableBodyRow<TRow>[]>(() => {
    const columns = this.columnsSignal();

    return this.rowsSignal().map((row, rowIndex) => ({
      row,
      rowIndex,
      ariaRowIndex: rowIndex + HEADER_ROW_COUNT + 1,
      cells: columns.map((column) => projectCell(column, row, rowIndex)),
    }));
  });

  /**
   * Asks the feature to reorder by a column, or to stop ordering by it. THREE STEPS, NOT TWO, and the
   * third is a state the reader arrives in rather than one invented here.
   *
   * @param cell The heading that was activated.
   */
  protected activateSort(cell: DataTableHeaderCell<TRow>): void {
    if (cell.sortable === false || this.loadingSignal() === true) {
      return;
    }

    this.sortChange.emit({ key: cell.key, direction: nextDirection(cell.sorted, this.sortDirSignal()) });
  }

  /**
   * Selects a row and reports it.
   *
   * @param row The row that was activated.
   */
  protected activateRow(row: TRow): void {
    // ⚠ THE STATE BOUNDARY'S OWN GUARD, and it is deliberately the SECOND line of defence rather than the
    // only one.
    if (this.rowsSelectableSignal() === false) {
      return;
    }

    this.selectedRowSignal.set(row);
    this.rowSelect.emit(row);
  }

  /**
   * Settles whether rows are selectable, once, before the first render. ⚠ THE TIMING IS THE WHOLE POINT.
   * A template output binding is registered while the PARENT view is created, which happens before this
   * component's initialisation hook runs — so by the time this executes, `observed` reports the settled
   * answer for every consumer that binds `(rowSelect)` in its markup.
   */
  public ngOnInit(): void {
    this.rowsSelectableSignal.set(this.rowSelect.observed);
    this.observeViewport();
    this.scheduleWindowUpdate();
  }

  /**
   * Selects a row from a pointer press, unless the press landed on a control.
   *
   * @param row The row that was pressed.
   * @param event The pointer event.
   */
  protected activateRowFromPointer(row: TRow, event: Event): void {
    if (this.rowsSelectableSignal() === false) {
      return;
    }

    if (originatesFromControl(event)) {
      return;
    }

    this.activateRow(row);
  }

  /**
   * Activates a row from the keyboard, unless the key landed on a control. Both activation keys are
   * honoured, and the space bar's default page scroll is suppressed so activating a row does not also
   * jump the viewport.
   *
   * @param row The row the key was pressed on.
   * @param event The keyboard event.
   */
  protected activateRowFromKeyboard(row: TRow, event: KeyboardEvent): void {
    // ⚠⚠ THIS GUARD IS FIRST, AHEAD OF BOTH OTHERS, AND SUPPRESSING THE DEFAULT IS WHY. On a grid nothing
    // listens to, a row activates nothing — so cancelling the space bar's default here would take the
    // reader's PAGE SCROLL away and give nothing back for it.
    if (this.rowsSelectableSignal() === false) {
      return;
    }

    if (originatesFromControl(event)) {
      return;
    }

    if (event.key !== ENTER_KEY && event.key !== SPACE_KEY) {
      return;
    }

    event.preventDefault();
    this.activateRow(row);
  }

  /**
   * Stops any interaction inside an `actions` cell from reaching the row, whether or not it came from a
   * control. Row commands are projected content, so they are interactive elements sitting INSIDE an
   * activatable row.
   *
   * @param event The click or key event raised inside the actions cell.
   */
  protected blockRowActivation(event: Event): void {
    event.stopPropagation();
  }
}

/**
 * Elements that own their own activation, and must therefore never have a press or a key stolen by the
 * row around them.
 */
const CONTROL_SELECTOR = [
  'a[href]',
  'button',
  'input',
  'select',
  'textarea',
  'label',
  'summary',
  'audio[controls]',
  'video[controls]',
  "[contenteditable]:not([contenteditable='false'])",
  "[tabindex]:not([tabindex='-1'])",
].join(',');

/**
 * Whether an event began inside something that owns its own activation. The defect this closes.
 *
 * @param event The click or key event as it reached the row.
 * @returns `true` when the event began inside a control the row must not steal from.
 */
function originatesFromControl(event: Event): boolean {
  const { target, currentTarget } = event;

  if (target instanceof Element === false) {
    return false;
  }

  const control = target.closest(CONTROL_SELECTOR);

  if (control === null) {
    return false;
  }

  return currentTarget instanceof Element === false || control !== currentTarget;
}

/** The width forms {@link DataTableWidth} admits, as a pattern the run time can apply. */
const WIDTH_PATTERN = /^(?:\d+(?:\.\d+)?%|min-content|max-content|var\(--[^\s()]+\))$/;

/**
 * Rejects a column set that breaks an invariant no type can carry alone. Declared as a function so it is
 * hoisted and can therefore be called from the input setter above without depending on declaration order
 * in this module.
 *
 * @typeParam TRow The row contract the columns read.
 * @param columns The set being bound.
 * @throws Error on a blank key, a duplicate key, a malformed width, or a sortable heading that also hides
 * its label.
 */
function assertColumnsAreValid<TRow extends object>(
  columns: readonly DataTableColumn<TRow>[],
): void {
  const seen = new Set<string>();

  // The key of the column that names each row, once one has been seen. Tracked rather than
  // counted so the refusal below can name BOTH offending columns.
  let rowHeaderKey: string | null = null;

  for (const column of columns) {
    const key = column.key;

    if (typeof key !== 'string' || key.trim().length === 0) {
      throw new Error('A data-table column must declare a non-blank key.');
    }

    if (seen.has(key)) {
      throw new Error(
        `A data-table column set must not declare the key "${key}" twice: ` +
          'the key is the tracking identity of the heading and of every cell in the column.',
      );
    }

    seen.add(key);

    if (column.rowHeader === true) {
      if (rowHeaderKey !== null) {
        throw new Error(
          `A data-table column set may declare at most one rowHeader column; "${rowHeaderKey}" and "${key}" both do.`,
        );
      }

      rowHeaderKey = key;
    }

    if (column.width !== undefined && WIDTH_PATTERN.test(column.width) === false) {
      throw new Error(
        `The width "${column.width}" declared by the data-table column "${key}" is not a ` +
          'supported track size. Use a percentage, min-content, max-content, or var(--token).',
      );
    }

    const heading: { readonly sortable?: boolean; readonly headerHidden?: boolean } = column;

    if (heading.sortable === true && heading.headerHidden === true) {
      throw new Error(
        `The data-table column "${key}" offers sorting and hides its label, which would ` +
          'render a focusable control with nothing visible in it.',
      );
    }
  }
}

/**
 * Resolves a column's declared track width into the value bound to its `col` element.
 *
 * @param width The declared width, if any.
 * @returns The width, or `null` when the column declared none, which leaves the column to take its share
 * automatically.
 */
function resolveWidth(width: DataTableWidth | undefined): string | null {
  return width ?? null;
}

/**
 * Resolves the `aria-sort` value for a heading. Derived from the two sort inputs alone, which is what
 * keeps the announced order and the rendered order from ever disagreeing.
 *
 * @param sortable Whether the column offers sorting at all.
 * @param sorted Whether the column carries the active sort.
 * @param direction The active direction.
 * @returns The attribute value, or `null` to omit the attribute entirely.
 */
function resolveAriaSort(
  sortable: boolean,
  sorted: boolean,
  direction: SortDirection,
): DataTableAriaSort | null {
  if (sortable === false) {
    return null;
  }

  if (sorted === false) {
    return 'none';
  }

  return direction === DESCENDING ? 'descending' : 'ascending';
}

/**
 * Returns the next step of the three-state ordering cycle for the heading that was activated. The three
 * states are the ones a reader can actually be in: not ordered by this column, ordered ascending, ordered
 * descending.
 *
 * @param sorted Whether the activated column currently carries the active ordering.
 * @param direction The direction the active ordering is applied in.
 * @returns The direction to ask for next, or `null` to ask for no ordering at all.
 */
function nextDirection(sorted: boolean, direction: SortDirection): SortDirection | null {
  if (sorted === false) {
    return ASCENDING;
  }

  return direction === ASCENDING ? DESCENDING : null;
}

/**
 * Resolves what a column puts in its body cells. A one-line resolution, and it is one line BECAUSE the
 * descriptor is a discriminated union: `text` is the only kind that may be omitted, and both other kinds
 * must declare themselves and must carry a template.
 *
 * @param column The column being projected.
 * @returns The resolved kind.
 */
function resolveKind<TRow extends object>(column: DataTableColumn<TRow>): DataTableColumnKind {
  return column.kind ?? 'text';
}

/**
 * Converts a cell value to text that is safe to interpolate. The conversions are deliberate and narrow: -
 * an absent value yields the empty string, never the WORDS `null` or `undefined`.
 *
 * @param value The raw cell value.
 * @returns Text fit for interpolation, possibly empty, never absent.
 */
function toDisplayText(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'number') {
    return Number.isFinite(value) ? String(value) : '';
  }

  if (typeof value === 'bigint') {
    return String(value);
  }

  return '';
}

/**
 * Reads a column's text for one row. NO PRECEDENCE RULE, because the descriptor admits exactly one text
 * source per column: a formatter or a bound member, never both.
 *
 * @param column The column being projected.
 * @param row The row being projected.
 * @returns The cell's text.
 */
function cellText<TRow extends object>(column: DataTableColumn<TRow>, row: TRow): string {
  const format = column.value;
  if (format !== undefined) {
    return toDisplayText(format(row));
  }

  const field = column.field;
  if (field === undefined) {
    return '';
  }

  // Bracket access, not member access: the row is widened to an index signature to read a member chosen at
  // run time, and `noPropertyAccessFromIndexSignature` mandates the bracket form for exactly that case.
  return toDisplayText((row as Record<string, unknown>)[field]);
}

/**
 * Projects one body cell. Called once per column per row inside the row projection, which is the ONLY
 * place a formatter runs.
 *
 * @param column The column being projected.
 * @param row The row being projected.
 * @param rowIndex Zero-based position of the row within the current page.
 * @returns The projected cell.
 */
function projectCell<TRow extends object>(
  column: DataTableColumn<TRow>,
  row: TRow,
  rowIndex: number,
): DataTableBodyCell<TRow> {
  const kind = resolveKind(column);
  const declaredTemplate = column.cellTemplate ?? null;

  // The template is now REQUIRED on both of those kinds, so the second half of this test can no longer fail
  // for a column that satisfies the type; it is kept because the projected cell's template member is
  // nullable for the text kind and the template guards on it, and because a column bound from JavaScript
  // must still render an empty cell rather than hand a null template to an outlet.
  const rendersTemplate = kind !== 'text' && declaredTemplate !== null;

  return {
    key: column.key,
    kind,
    text: kind === 'text' ? cellText(column, row) : '',
    template: rendersTemplate ? declaredTemplate : null,
    context: rendersTemplate ? { $implicit: row, row, column, rowIndex } : null,
    align: column.bodyAlign ?? DEFAULT_ALIGN,
    // An actions column can never be the row's name - see the note on the column member. The kind is tested
    // here rather than trusted from the declaration so a caller that marks a commands column cannot produce
    // rows named "Edit".
    rowHeader: column.rowHeader === true && kind !== 'actions',
  };
}

// MIGRATION ledger
// Every deliberate divergence from the eight legacy grids, with the measured source evidence for each.
// Recorded inline here as well as in the prose above so that the full inventory is auditable from one
// place.

// The column KEY is separate from the display LABEL, because a label-keyed model is impossible against this
// codebase.

// MIGRATION: header alignment and body alignment are INDEPENDENT descriptor members, never derived from one
// another. A single grid-wide alignment would misrender most of them.

// MIGRATION: the pager is a SIBLING placed by the feature, never rendered by this component and never inside
// a table footer.

// MIGRATION: fixed pixel widths are replaced by intrinsic measures or spacing tokens, applied through a
// colgroup so no cell rule carries a size.

// MIGRATION: action-column heading labels are NORMALISED - every column carries a label and headerHidden
// decides whether it is painted.

// MIGRATION: the users-online column is DROPPED, a documented functional reduction. Users-online is out of
// scope for this migration and no endpoint exists to feed it, so reproducing the column would render a
// permanent, meaningless indicator.

// Row identity is the row's OBJECT REFERENCE, never an identifier member, because the legacy sentinels
// collide with real keys. `Website/admin/Tabs/managetabs.ascx` compares a ModuleID against -1 in markup on
// a column seeded at 0.

// The grid degrades by SCROLLING, and its headings degrade by WRAPPING, neither of which the legacy grids
// did - they simply crushed. Two measured trade-offs are recorded here rather than left to be rediscovered.

// THE COSTS ARE REAL AND ARE RECORDED RATHER THAN GLOSSED. A windowed row is not in the accessibility tree,
// not reachable by find-in-page and not in the tab order, and an estimated row height makes the spacers
// approximate, so a row's scroll position can drift slightly from where a fully rendered table would have
// put it.
