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

import type { OnChanges, OnInit, SimpleChanges } from '@angular/core';

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
   *
   * ⚠ A WIDTH MUST HOLD ITS OWN HEADING, AND THE ARITHMETIC IS NOT OBVIOUS. A heading cell spends a fixed
   * 24px before any text is drawn — 8px of cell padding, the 4px gap of the sort control, and the 12px the
   * sort indicator reserves whether or not the column is the sorted one — so the text of a heading only
   * ever receives `columnWidth - 24`. Because the table is `table-layout: fixed` and floors at
   * `--table-min-inline-size`, a percentage column is at its NARROWEST when the table is at that floor, so
   * that floor is the only width a declaration has to be checked against: satisfy it there and every wider
   * viewport follows, since a percentage of a larger table is larger.
   *
   * WHAT MUST FIT IS THE WIDEST UNBREAKABLE RUN, NOT THE LABEL. Measurement of the shipped grids established
   * two distinct ways a heading is lost, and word count predicts neither:
   *   • a column marked {@link atomic} computes `white-space: nowrap`, which makes the WHOLE label one
   *     unbreakable run — this is why the two-word "Portal Id" and "Disk Space" were being clipped;
   *   • otherwise the label may wrap between words but never inside one, so its LONGEST WORD is the run that
   *     must fit — this is why the single-word "Public", "Auto" and "Authorized" were clipped, and why the
   *     two-word "Billing Period" was clipped on BOTH of its wrapped lines.
   * A heading that does not fit is ELLIPSISED, and an ellipsised heading loses information outright: the
   * accessible name survives, the visible name does not. A body value in the same position merely wraps and
   * stays wholly readable, which is why width is taken from a value column to pay for a heading rather than
   * the other way round.
   *
   * Headings are also two type steps larger than the body — the section carries `--font-size-lg` while the
   * table carries `--font-size-sm` — so a heading needs materially more room than its character count
   * suggests against body text. Sizing every heading to fit at the floor with a few pixels to spare is the
   * whole of the rule; there is no truncation hook to reach for instead, by the deliberate decision the
   * shared table partial records.
   */
  readonly width?: DataTableWidth;

  /**
   * Whether this column's value is INDIVISIBLE — a figure, a date, an identifier — and must never be broken
   * across lines.
   *
   * ⚠ THE DEFAULT WRAPPING IS WRONG FOR SUCH A VALUE, WHICH IS WHY THIS EXISTS. The shared table breaks body
   * text anywhere so an unbreakable run cannot escape its column and overprint its neighbour; applied to a
   * number that is exactly what must not happen. Measured at 375px, `1234567.89` broke after the decimal
   * point and painted `1234567.` on its own line — a complete, plausible and WRONG amount — and `6/30/2021`
   * broke into `6/30/202` and `1`. Declaring the column atomic keeps the value whole and, if the column is
   * too narrow for it, clips with an ellipsis, which a reader can see is a truncation rather than a value.
   */
  readonly atomic?: boolean;
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

  /**
   * Resolved {@link DataTableColumnCommon.atomic}. A heading carries it too, so that an atomic column's own
   * label cannot be the thing that widens the track the value was sized for.
   */
  readonly atomic: boolean;

  /**
   * Resolved {@link DataTableColumnCommon.rowHeader}. A HEADING carries it as well as the body cells so that
   * the pinned identity column pins as one piece: a sticky body cell under a static heading leaves the
   * heading sliding out from above its own values.
   */
  readonly rowHeader: boolean;
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

  /** Resolved {@link DataTableColumnCommon.atomic}, published to the cell as `data-atomic`. */
  readonly atomic: boolean;

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

/**
 * Hidden width, in pixels, above which the container is treated as genuinely scrollable. One pixel rather
 * than zero because a fixed table layout resolving fractional track widths leaves sub-pixel differences
 * between the content and the box on widths where nothing is actually clipped, and a region that announces
 * itself as scrollable when it is not is worse than none: it puts a permanent, unusable focus stop in the
 * tab order of every listing.
 */
const OVERFLOW_TOLERANCE_PX = 1;

/**
 * The tolerance for comparing a measured TEXT width against the box that has to hold it.
 *
 * ⚠ IT IS DELIBERATELY MUCH SMALLER THAN THE TOLERANCE ABOVE, AND REUSING THAT ONE HID A REAL DEFECT. A whole
 * pixel is the right slack for a scroll measurement, where both operands are integers and a rounding artefact
 * is worth a pixel. It is far too coarse for a text comparison, where both operands are fractional: the `Auto`
 * heading overflowed its box by 0.559px, painted `A…` on screen, and a one-pixel tolerance discarded it.
 *
 * A tenth of a pixel is safe in the other direction as well, because the two measurements agree closely: the
 * canvas figure and the browser's own layout matched to within 0.02px on every heading measured, and the
 * tightest FITTING heading measured had 0.001px to spare on the correct side.
 */
const TEXT_OVERFLOW_TOLERANCE_PX = 0.1;

/** The attribute that marks a run of text as present for assistive technology and painted nowhere. */
const VISUALLY_HIDDEN_ATTRIBUTE = 'data-visually-hidden';

/**
 * The class a command column's heading label carries.
 *
 * A command column needs an accessible name without a painted heading, so its label is clipped to a single
 * pixel ON PURPOSE. That is not truncation, and treating it as such would put a tooltip on every icon column in
 * the application.
 */
const HIDDEN_LABEL_CLASS = 'data-table__label--hidden';

/** The token {@link OVERFLOW_CUE_PLURAL} substitutes the hidden-column count into. */
const OVERFLOW_CUE_COUNT_TOKEN = '{count}';

/** The painted overflow cue when exactly one column is hidden. */
const OVERFLOW_CUE_SINGULAR = 'Scroll sideways for 1 more column.';

/** The painted overflow cue when more than one column is hidden, or none has been counted yet. */
const OVERFLOW_CUE_PLURAL = `Scroll sideways for ${OVERFLOW_CUE_COUNT_TOKEN} more columns.`;

/**
 * How many focusable controls the body must hold before a skip affordance is offered.
 *
 * ⚠ A THRESHOLD RATHER THAN ALWAYS, BECAUSE THE AFFORDANCE COSTS A STOP OF ITS OWN. Below this the run being
 * skipped is short enough that the skip is not worth what it costs, and on a listing whose rows carry no
 * controls at all - a read-only lookup table - it would cost a stop and save nothing. Six is two rows of
 * three commands, which is where a measured account listing's run began to dominate its traversal: thirty
 * consecutive row-command stops out of eighty-two, uninterrupted, because a row's own identifier is plain
 * text rather than a link.
 */
const ROW_SKIP_THRESHOLD = 6;

/**
 * The controls a projected cell may put in the tab order.
 *
 * Two callers, for two different questions: counting what a row-command skip would pass over, and asking
 * whether a cut heading already holds a stop of its own before one is added to the cell around it.
 */
const FOCUSABLE_SELECTOR = 'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])';

/** The wording of the affordance that passes over a run of row commands. */
const ROW_SKIP_LABEL = 'Skip past the record commands';

/** The wording of the landing point the affordance moves focus to. */
const ROW_SKIP_TARGET_LABEL = 'End of table';

/** Instance counter behind the ids this component publishes, so two tables on one screen cannot collide. */
let nextInstanceId = 0;

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
 * What the progress indicator says when a caller names no collection. The shared indicator's own default,
 * restated here so that passing an empty string falls back to it rather than to a bare glyph.
 */
const DEFAULT_LOADING_LABEL = 'Loading…';

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
 * What the polite live region announces when a read failed and left nothing on screen. Deliberately states
 * that nothing is KNOWN rather than that nothing EXISTS.
 */
const RECORDS_UNREAD_ANNOUNCEMENT = 'The records could not be read.';

/** What the polite live region announces when a read failed while rows were already on screen. */
const RECORDS_STALE_ANNOUNCEMENT =
  'The records could not be refreshed. What is shown may be out of date.';

/** The sentence rendered in the table body in place of the empty state when a read failed. */
const RECORDS_UNREAD_MESSAGE = 'The records could not be read.';

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
export class DataTableComponent<TRow extends object> implements OnInit, OnChanges {
  private readonly columnsSignal = signal<readonly DataTableColumn<TRow>[]>([]);

  private readonly rowsSignal = signal<readonly TRow[]>([]);

  /** How a row identifies itself across redraws, or `null` to key on the row object. */
  private readonly rowKeySignal = signal<((row: TRow) => string | number) | null>(null);

  private readonly sortBySignal = signal<string | undefined>(undefined);

  /**
   * The sort the rows ON SCREEN are actually in, as distinct from the sort most recently REQUESTED.
   *
   * ⚠ #32 — THE INDICATOR USED TO REPORT THE REQUEST, WHICH IS NOT THE SAME CLAIM. A screen binds this table
   * from its own query state, and that state changes the instant a heading is pressed - before the read it
   * triggers has returned anything. When the read then FAILED, the rows stayed exactly as they were while the
   * heading arrow and `aria-sort` had already moved to the new column, so the table asserted an order its
   * contents did not have, in the one situation where a reader most needs to trust it. Latching the reported
   * sort to the arrival of rows makes the indicator a statement about what is displayed: it moves when the
   * new order does, and a failed read leaves both alone.
   */
  private readonly displayedSortBySignal = signal<string | undefined>(undefined);

  /** The direction {@link displayedSortBySignal} is displayed in. @see displayedSortBySignal */
  private readonly displayedSortDirSignal = signal<SortDirection>(ASCENDING);

  /** Whether any rows have been bound yet, so the first binding can adopt its sort immediately. */
  private rowsEverBound = false;

  private readonly sortDirSignal = signal<SortDirection>(ASCENDING);

  private readonly loadingSignal = signal(false);

  private readonly loadingLabelSignal = signal(DEFAULT_LOADING_LABEL);

  private readonly emptyMessageSignal = signal('');

  /** Whether the read that should have produced {@link rowsSignal} failed. */
  private readonly failedSignal = signal(false);

  /** The match-set size a paging consumer stated, or null when it stated none. */
  private readonly totalCountSignal = signal<number | null>(null);

  /** Where in the match set the rendered body begins. Zero for a listing that does not page. */
  private readonly rowOffsetSignal = signal(0);

  /**
   * The caption's identifier, published so the scrolling region can borrow it as its accessible name.
   * Per instance, because two tables on one screen would otherwise name each other.
   */
  protected readonly captionId = `app-data-table-${++nextInstanceId}-caption`;

  /**
   * Whether the container is currently clipping content horizontally. MEASURED, not assumed from the
   * viewport width, because whether a table overflows depends on its own columns.
   */
  private readonly horizontallyScrollableSignal = signal(false);

  /**
   * The scrollport's visible inline size in pixels, or null before it has been measured.
   *
   * It exists for the message row and nothing else - see {@link messageViewportInlineSize}.
   */
  private readonly visibleInlineSizeSignal = signal<number | null>(null);

  /**
   * How many heading cells are wholly or partly outside the scrollport, measured in the same pass that
   * decides whether the region clips at all. A count rather than a boolean because the cue reports the size
   * of what is missing, which is the part a reader cannot infer from the clipped edge.
   */
  private readonly hiddenColumnCountSignal = signal(0);

  /** Whether the container is a scrollable region right now, for the template's conditional attributes. */
  protected readonly isHorizontallyScrollable = this.horizontallyScrollableSignal.asReadonly();

  /** Whether the body currently holds enough controls to be worth passing over. MEASURED, not estimated. */
  private readonly offersRowSkipSignal = signal(false);

  /** Whether the skip affordance is offered right now. */
  protected readonly offersRowSkip = this.offersRowSkipSignal.asReadonly();

  /** The landing point the skip affordance moves focus to, published so the affordance can name it. */
  protected readonly skipTargetId = `app-data-table-${nextInstanceId}-end`;

  /** The skip affordance's own wording. */
  protected readonly rowSkipLabel = ROW_SKIP_LABEL;

  /** The landing point's wording, so focus arrives somewhere that says where it is. */
  protected readonly rowSkipTargetLabel = ROW_SKIP_TARGET_LABEL;

  private readonly selectedRowSignal = signal<TRow | null>(null);

  private readonly rowsSelectableSignal = signal(false);


  private readonly minInlineSizeSignal = signal<string | null>(null);

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
   * Decides, once per change pass, whether the reported sort may move up to the requested one.
   *
   * ⚠ THE DECISION BELONGS HERE AND NOT IN THE SETTERS. Within one pass Angular writes inputs in the order
   * the template lists them, so a setter reading `loading` may run before `loading` has been written for that
   * pass - and the answer would then depend on the order of two attributes in a caller's markup. This hook
   * runs after every input for the pass has been written, so the decision sees a consistent picture.
   *
   * Two things commit a sort. ROWS ARRIVING commits it, because a screen re-binds its collection on a settled
   * read and on nothing else. A SORT CHANGING WHILE NOTHING IS IN FLIGHT commits it too: a table that is not
   * waiting for anything holds the rows that answer the sort it is being told about, which is the ordinary
   * case of a caller binding a restored sort alongside rows it already has.
   *
   * What is deliberately NOT committed is a sort that changes while a read is outstanding. That is the
   * failure case: the heading would otherwise move to the new column immediately and stay there when the read
   * failed, leaving the table asserting an order its rows do not have.
   *
   * @param changes The inputs written in this pass.
   */
  ngOnChanges(changes: SimpleChanges): void {
    const rowsArrived = changes['rows'] !== undefined;
    const sortChanged = changes['sortBy'] !== undefined || changes['sortDir'] !== undefined;

    if (rowsArrived) {
      this.rowsEverBound = true;
    }

    if (rowsArrived || !this.rowsEverBound || (sortChanged && this.loadingSignal() === false)) {
      this.displayedSortBySignal.set(this.sortBySignal());
      this.displayedSortDirSignal.set(this.sortDirSignal());
    }
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

  /**
   * What the progress indicator says while a read is in flight, both in the placeholder that replaces the
   * rows on a first read and in the strip that reports a refetch over rows already on screen.
   *
   * ⚠ IT IS THE CALLER'S NOUN, NOT A DEFAULT. Measured across the four listings, three of them announced a
   * bare "Loading…" while one named its collection, so a screen reader heard a different sentence on
   * otherwise identical screens and, on three of the four, was not told WHAT was loading. The default below
   * keeps a caller that passes nothing working exactly as before.
   */
  @Input()
  public set loadingLabel(value: string | null | undefined) {
    const trimmed = (value ?? '').trim();

    this.loadingLabelSignal.set(trimmed.length === 0 ? DEFAULT_LOADING_LABEL : trimmed);
  }

  public get loadingLabel(): string {
    return this.loadingLabelSignal();
  }

  /**
   * The sentence the zero-result surface explains itself with. Left blank, the shared empty state falls back to
   * its own generic default, which is what every listing whose empty state comes from this grid was getting.
   *
   * ⚠ IT EXISTS SO A CONSUMER NO LONGER HAS TO DESTROY THE TABLE TO SAY SOMETHING SPECIFIC. Screens with their
   * own empty wording rendered a panel in place of the table, which took the polite status region down with it
   * and left the narrowing-to-nothing transition silent.
   */
  @Input()
  public set emptyMessage(value: string | null | undefined) {
    this.emptyMessageSignal.set((value ?? '').trim());
  }

  public get emptyMessage(): string {
    return this.emptyMessageSignal();
  }

  /**
   * Sets whether the LAST READ OF THESE ROWS FAILED, which is a different fact from having no rows and
   * must be presented differently.
   *
   * ⚠ THE ONE INPUT THAT STOPS A FAILURE READING AS AN EMPTY DATABASE. Zero rows has two causes that look
   * identical from inside this component — nothing matched, or nothing is known — and it used to present
   * both as "Nothing to Display / No records found." Measured consequences of that conflation, all on real
   * screens: a swallowed HTTP 500 rendered pixel-identically to a legitimately empty permission list; a
   * Forbidden banner sat above "No records found." on a portal's aliases, asserting the portal has no
   * aliases when none had been retrieved; a 403 on an account's own profile read as "This site declares no
   * profile properties"; and the polite live region announced "No records found." while thirty roles
   * existed. When this is set, the empty state is NOT rendered and the live region reports that the records
   * could not be read, leaving the caller's own error banner to carry the reason.
   *
   * @param value Whether the read that should have produced these rows failed.
   */
  @Input()
  public set failed(value: boolean | null | undefined) {
    this.failedSignal.set(value === true);
  }

  public get failed(): boolean {
    return this.failedSignal();
  }

  /**
   * The size of the whole match set, for the announcement only. Absent for a listing that does not page.
   *
   * ⚠ IT IS NOW READ IN TWO PLACES, AND THE SECOND ONE IS THE CORRECTION THIS NOTE USED TO DENY. It was
   * written here that the total is used for the polite summary and "never for `aria-rowcount`, row indices
   * or rendering", on the ground that the table must not claim rows it was not handed. That ground was
   * right about RENDERING and wrong about the two ARIA attributes, which exist precisely so a table can
   * describe a set larger than its own body - and reporting only the page made them state something untrue:
   * measured on the second page of a seventeen-record listing, the first body row announced as "row 2 of 8"
   * while the pager beside it called the same record 11 of 17.
   *
   * It is still never used for rendering, and never for the row count while a message row stands in for the
   * records, so an empty or waiting table describes exactly itself.
   *
   * The summary reading exists because this component's status region became the SINGLE announcing region:
   * the pager used to announce the total from a live region of its own, which meant one action produced two
   * polite announcements with a blank between them.
   *
   * @param value The match-set size, or nothing when the consumer does not page.
   */
  @Input()
  public set totalCount(value: number | null | undefined) {
    this.totalCountSignal.set(typeof value === 'number' ? value : null);
  }

  /**
   * The zero-based position, within the whole match set, of the first row in {@link rows}. Zero for a
   * listing that does not page, and `pageIndex * pageSize` for one that does.
   *
   * ⚠ IT EXISTS BECAUSE `aria-rowindex` CANNOT BE DERIVED FROM ANYTHING ELSE THIS COMPONENT RECEIVES.
   * The total says how large the set is; only the offset says where in that set the body begins, and
   * without it the indices restart at two on every page - which was measured, and which tells a screen
   * reader the position of a row within the window instead of within the set.
   *
   * A NEW PUBLIC INPUT IS A COST, AND IT IS RECORDED RATHER THAN GLOSSED. The frozen contract for this
   * component closes its surface at five inputs; this is the eleventh, and the divergence is already
   * documented for `virtualizeThreshold`. The alternative was to accept indices that contradict the pager
   * on every screen that pages, which the accessibility requirement does not allow, or to publish
   * `aria-rowcount="-1"` for an unknown total when the total is in fact known.
   *
   * A value that is not a finite whole number of at least zero is treated as zero rather than raising:
   * the only way one can arrive is a page coordinate that has not resolved yet, and a first page is the
   * truthful reading of "not yet known".
   *
   * @param value The dataset position of the first rendered row.
   */
  @Input()
  public set rowOffset(value: number | null | undefined) {
    const usable =
      typeof value === 'number' && Number.isFinite(value) && value >= 0 ? Math.trunc(value) : 0;

    this.rowOffsetSignal.set(usable);
  }

  public get totalCount(): number | null {
    return this.totalCountSignal();
  }

  /**
   * An override for the width below which this grid scrolls sideways instead of narrowing further, as a CSS
   * length. Null — the default — leaves every grid on the shared `--table-min-inline-size` floor, so this
   * input changes nothing for a listing that does not set it.
   *
   * ⚠ WHY A GRID NEEDS TO BE ABLE TO SAY THIS, AND THE MEASUREMENT THAT FORCED IT. The shared floor is
   * 60rem, and it is right for the grids it was solved against — nine to thirteen columns, where anything
   * narrower crushes some track below its own content. It is badly wrong for a SMALL grid. The portal alias
   * listing has TWO columns whose content needs 207px between them, and the floor made its table 960px wide:
   * at a 320 viewport that put the host name — the only data the screen carries — 214px beyond the right
   * edge of a 271px scrollport, so the visible table was three command buttons and nothing else. A reader had
   * to discover a scroll region to see any data at all.
   *
   * ⚠ IT IS A FLOOR AND NOT A WIDTH. The table still fills its container whenever the container is wider,
   * so nothing about the wide rendering changes; this only stops a small grid being inflated past what its
   * own columns need.
   *
   * @param value A CSS length for this grid's floor, or null to use the shared token.
   */
  @Input()
  public set minInlineSize(value: string | null | undefined) {
    const usable = typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;

    this.minInlineSizeSignal.set(usable);
  }

  public get minInlineSize(): string | null {
    return this.minInlineSizeSignal();
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

  /** This grid's own scroll floor, or null to defer to the shared token. */
  protected readonly tableMinInlineSize = this.minInlineSizeSignal.asReadonly();

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

    // ⚠ THE FAILED READING COMES FIRST, and it is what keeps this region truthful. Announcing "No records
    // found." after a read that failed states as fact something nobody knows, and it is the announcement a
    // screen-reader user hears INSTEAD of the failure - the banner carrying the reason is not in a live
    // region on every screen.
    if (this.failedSignal()) {
      return count === 0 ? RECORDS_UNREAD_ANNOUNCEMENT : RECORDS_STALE_ANNOUNCEMENT;
    }

    if (count === 0) {
      return `No records found.${this.sortClause()}`;
    }

    // ⚠ THE DATASET TOTAL IS STATED HERE BECAUSE THIS IS NOW THE ONLY ANNOUNCING REGION. It used to report
    // the current page while the total lived in the pager's own live region, so one action produced TWO
    // polite announcements with a blank between them, and a screen reader read the page count and the total
    // as if they were unrelated events. The pager's region is now visible-only and this one carries both
    // facts in a single announcement.
    const total: number | null = this.resolvedTotalCount();

    // ⚠ THE ANNOUNCED SENTENCE STATES THE RANGE, NOT JUST A QUANTITY, AND THAT IS THE CORRECTION. It
    // used to read "Showing 3 of 13 records." on the second page of a thirteen-record set - literally true
    // and materially misleading, because the only OTHER report of position is the pager, whose range and
    // "2 / 2" readout are deliberately visible-but-not-announced so that one action produces one
    // announcement. A screen-reader user therefore heard a sentence describing a page without being told it
    // was a page, and "3 of 13" is indistinguishable from a filter that had narrowed the set to three.
    //
    // `rowOffset` is the dataset position of the first rendered row and is already supplied by every
    // listing that pages, for `aria-rowindex`; reusing it costs no new input and makes this sentence agree
    // with the pager the sighted operator is reading.
    const offset: number = this.rowOffsetSignal();
    const records: string =
      total !== null && total > count
        ? `Showing records ${offset + 1} to ${offset + count} of ${total}.`
        : count === 1
          ? '1 record.'
          : `${count} records.`;

    // ⚠ THE ORDER IS STATED HERE RATHER THAN FROM A REGION OF ITS OWN, and that placement is the decision.
    // Activating a column heading reordered the rows and announced NOTHING: measured across two sort toggles
    // with an observer over every live region on the page, the only region that changed was this one, and it
    // changed to text byte-identical to what it already held - because sorting moves rows without changing how
    // many there are, and an unchanged live region announces nothing. A second live region for the order was
    // rejected on the evidence that produced this one: the pager used to announce from a region of its own, so
    // one action yielded two polite announcements with a blank between them, and this region became the single
    // announcer precisely to end that.
    return `${records}${this.sortClause()}`;
  });

  /**
   * The ordering, as a sentence fragment appended to the row count, or an empty string when the listing
   * carries no sort.
   *
   * ⚠ #26 — WITHOUT THIS, SORTING IS THE ONE INTERACTION THAT ANNOUNCES NOTHING. Paging and filtering both
   * change the numbers in the status above, so the live region re-reads and a screen-reader user hears the
   * result. Sorting changes the ORDER and never the count, so the text it produced was identical to the text
   * already there - and a live region whose contents do not change says nothing at all. Naming the column and
   * the direction makes the one silent interaction audible, using the column's own visible heading so what is
   * heard matches what is seen. `aria-sort` on the heading reports the same fact, but only to a reader who
   * goes looking for the heading; this reports it where the reader already is.
   *
   * Silent unless the ordered column is one this table actually offers: a consumer may hold a sort name the
   * current column set does not publish - a saved query, or a name the wire accepts and the screen does not
   * show - and naming a column that is not on screen would describe something a person cannot see or change.
   */
  private readonly sortClause = computed<string>(() => {
    const key: string | undefined = this.displayedSortBySignal();

    if (key === undefined) {
      return '';
    }

    // SILENT UNLESS THE ORDERED COLUMN IS ONE THIS TABLE OFFERS AS SORTABLE. A consumer may hold a sort name
    // the current column set does not publish - a saved query, or a name the wire accepts and the screen does
    // not show - and a column that exists but carries no sort control is the same case: naming either would
    // describe an order a person can neither see stated on a heading nor change.
    const column = this.columnsSignal().find(
      (candidate) => candidate.key === key && candidate.sortable === true,
    );

    if (column === undefined) {
      return '';
    }

    return ` Sorted by ${column.label}, ${describeDirection(this.displayedSortDirSignal())}.`;
  });

  /**
   * The size of the whole match set, when the consumer stated one and it is usable.
   *
   * A consumer that pages supplies this; one that does not leaves it absent, and the summary then speaks
   * only of the rows it was handed. A negative or non-finite value is treated as absent rather than
   * rendered, so a sentinel cannot reach the sentence.
   */
  private readonly resolvedTotalCount = computed<number | null>(() => {
    const stated: number | null = this.totalCountSignal();

    if (stated === null || !Number.isFinite(stated) || stated < 0) {
      return null;
    }

    return Math.trunc(stated);
  });

  protected readonly isEmpty = computed(
    () =>
      this.loadingSignal() === false &&
      this.failedSignal() === false &&
      this.rowsSignal().length === 0,
  );

  /**
   * Whether the body should say the rows could not be read, in place of the empty state.
   *
   * Rendered only when there is genuinely nothing on screen: a failure that arrives while rows are still
   * shown leaves them alone, exactly as a read in flight does, because withdrawing readable rows in favour
   * of a message loses information the reader already had.
   */
  protected readonly showFailurePlaceholder = computed(
    () =>
      this.loadingSignal() === false &&
      this.failedSignal() &&
      this.rowsSignal().length === 0,
  );

  /** The sentence rendered in place of the empty state when the read failed. */
  protected readonly recordsUnreadMessage = RECORDS_UNREAD_MESSAGE;

  /** Whether the waiting placeholder should REPLACE the rows. */
  protected readonly showWaitingPlaceholder = computed(
    () => this.loadingSignal() && this.rowsSignal().length === 0,
  );

  /**
   * Whether to report a read that is running WHILE rows are on screen. The complement of
   * {@link showWaitingPlaceholder} within the loading state: exactly one of the two is ever true, so the
   * screen shows one indicator and never both.
   *
   * The strip it drives is `aria-hidden`, deliberately: the table's own `aria-busy` already reports the same
   * fact to assistive technology, and announcing it twice would be worse than announcing it once.
   */
  protected readonly showRefetchIndicator = computed(
    () => this.loadingSignal() && this.rowsSignal().length > 0,
  );

  /**
   * Whether ordering is currently unavailable. True while a read is in flight, and ALSO over a settled empty
   * result: a sort control above no rows can reorder nothing, so leaving it live invited an operator to press
   * a control that could not answer.
   */
  protected readonly sortUnavailable = computed(() => this.loadingSignal() || this.isEmpty());

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

  /** Whether a truncation re-measure is already queued for the next frame. @see scheduleTruncationUpdate */
  private truncationUpdateQueued = false;

  /** The reused canvas context text measurement runs through. @see measureText */
  private textMeasurement: CanvasRenderingContext2D | null = null;

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
      this.measureHorizontalOverflow();
      this.measureRowSkip();
      this.measureTruncatedCells();
      this.updateWindow();
    });
  }

  /**
   * Re-measures truncation alone, on the next frame.
   *
   * ⚠ IT EXISTS BECAUSE THE RESIZE OBSERVER COULD NOT SAFELY CALL {@link scheduleWindowUpdate}, AND CALLING
   * NOTHING WAS A REAL DEFECT. The observer watches the container and the table precisely because the
   * things that change their width - a column set arriving, a sort indicator appearing, the sidebar
   * collapsing, a column's declared weight changing - do not move the window, so the window listeners never
   * fire. Measured before this method existed: forcing a heading to clip WITHOUT resizing the window left it
   * unmarked, and firing a window resize immediately marked it correctly. Truncation marks were therefore
   * stale or absent after every non-window width change, while the overflow measurement beside them stayed
   * correct - which is exactly why the omission was easy to miss.
   *
   * ⚠ AND IT IS DELIBERATELY NOT `scheduleWindowUpdate`. That method also runs the row-window measurement,
   * which changes how many rows are rendered, which changes the table's height, which re-notifies this very
   * observer - a feedback loop. Truncation marking writes only `data-truncated`, `title` and `tabindex`, none
   * of which affects layout (the reveal applies on focus only), so it cannot re-trigger the observer and is
   * safe to run from it.
   */
  private scheduleTruncationUpdate(): void {
    if (this.truncationUpdateQueued || typeof window === 'undefined') {
      return;
    }

    this.truncationUpdateQueued = true;

    window.requestAnimationFrame(() => {
      this.truncationUpdateQueued = false;
      this.measureTruncatedCells();
    });
  }

  /**
   * Settles whether the container is clipping content horizontally.
   *
   * ⚠ THE MEASUREMENT IS WHAT MAKES THE FOCUS STOP CONDITIONAL, AND CONDITIONAL IS THE WHOLE POINT. A
   * scrollable region needs to be focusable so a keyboard user can scroll it, but this container is only
   * scrollable at some widths: measured on the account listing, 1280 gave a scroll width of 1046 against a
   * client width of 1046 - nothing hidden at all - while 375 gave 648 against 326, hiding 322 pixels and
   * four whole columns. Making the container permanently focusable would therefore add an unusable stop to
   * every listing at every width to serve the narrow ones, which is a regression rather than a fix.
   *
   * It runs on the same frame as the row-window measurement rather than on an observer of its own: both
   * read layout, both are already driven by the scroll and resize listeners plus every `rows` change, and
   * sharing the frame keeps them from reading a box the other has just invalidated.
   */
  private measureHorizontalOverflow(): void {
    const container = this.host.nativeElement.querySelector('.data-table__container');

    if (container === null) {
      return;
    }

    const hidden = container.scrollWidth - container.clientWidth;

    this.horizontallyScrollableSignal.set(hidden > OVERFLOW_TOLERANCE_PX);

    // ⚠ THE CONTENT WIDTH, NOT `clientWidth`, AND A MEASUREMENT CAUGHT THE DIFFERENCE. `clientWidth`
    // INCLUDES the container's own inline padding, while the message wrapper begins after it - so handing the
    // wrapper the raw value overhung the visible padding box by exactly 4 pixels at each edge when measured at
    // 320 and 375. Nothing readable was lost, because the overhang was the wrapper's own padding, but a box
    // asked to be exactly as wide as the visible area should be exactly that.
    const padding = getComputedStyle(container);
    const inlinePadding =
      Number.parseFloat(padding.paddingLeft) + Number.parseFloat(padding.paddingRight);

    this.visibleInlineSizeSignal.set(
      container.clientWidth - (Number.isFinite(inlinePadding) ? inlinePadding : 0),
    );

    // ⚠ COUNTED FROM THE RENDERED HEADING CELLS, NOT FROM THE DECLARED TRACK WIDTHS. A declared width may be
    // a percentage, `min-content` or absent, so only the browser knows what any of them resolved to — and on
    // the portal-alias grid the two declarations resolved to 480px each, which no reading of the source
    // predicts. A heading is counted as hidden when ANY part of it lies past the scrollport's trailing edge,
    // because a half-visible column is not a column a reader can use.
    const scrollportEnd = container.getBoundingClientRect().right;
    const headings = container.querySelectorAll('thead th');

    let hiddenColumns = 0;

    for (const heading of Array.from(headings)) {
      if (heading.getBoundingClientRect().right > scrollportEnd + OVERFLOW_TOLERANCE_PX) {
        hiddenColumns += 1;
      }
    }

    this.hiddenColumnCountSignal.set(hiddenColumns);
  }

  /**
   * Marks the atomic body cells whose value is actually being cut off, so each one gains a recovery
   * affordance and no other cell pays for it.
   *
   * ⚠ THE MEASURED DEFECT THIS CLOSES, AND IT WAS A LOSS TO SIGHTED READERS ONLY. An atomic column keeps its
   * value on one line and ellipsises what does not fit, which is right for a figure or a date — a date broken
   * across lines reads as a different, plausible date. But the ellipsis was the END of the story: measured at
   * 1440 on the account listing, twelve cells were cut, and **none of the twelve carried a `title`, a
   * `tabindex`, an `aria-label`, or the full text anywhere else on its row.** A hundred-character sign-in name
   * rendered eleven characters; a fifty-character role name rendered about thirteen. The complete value was in
   * the accessibility tree the whole time, so a screen-reader user could read what a sighted user could not.
   *
   * ⚠ WHY THIS IS MEASURED RATHER THAN APPLIED TO EVERY ATOMIC CELL. The affordance includes a focus stop, and
   * an unconditional one would have put six extra stops on every row of the account listing — sixty on a
   * ten-row page — most of them on cells showing their value in full. Marking only the cells that are actually
   * cut keeps the tab order proportional to the information actually missing.
   *
   * ⚠ ITS ONE KNOWN BLIND SPOT, STATED BECAUSE IT IS A REAL ONE. `scrollWidth` and `clientWidth` are integers
   * and this test carries the same one-pixel tolerance as the region measurement above, so an overflow of a
   * pixel or less does not register: `setup_member` measures 100.1px of text in a 100px content box, paints a
   * visible ellipsis, and reports the two widths as equal. That tolerance is deliberate — it keeps a rounding
   * artefact from putting a focus stop on a cell showing its value in full — and it is affordable because a
   * cell overflowing by a pixel loses at most its last character. The remedy for THAT is the column width
   * rather than an affordance, which is why the widths of the columns where it was observed were corrected as
   * well; every overflow large enough to cost a reader a word is far above this threshold (the shortest one
   * measured was 15px).
   */
  private measureTruncatedCells(): void {
    const body = this.host.nativeElement.querySelector('tbody.data-table__body');

    if (body === null) {
      return;
    }

    // ⚠ HEADINGS FIRST, AND LEAVING THEM OUT WAS THE OTHER HALF OF THE SAME DEFECT. A heading clips on exactly
    // the same terms as its cells, and measured on the account listing the `Telephone` heading painted
    // `Teleph…` in a 77.86px track that its text needed 81.91px of — so the column's own identity was the
    // truncated thing, which is worse than a truncated value. A heading gets the tooltip but NOT a focus stop:
    // a sortable heading already holds a focusable button whose accessible name carries the full column name,
    // and adding a stop on the cell around it would put two stops on one heading.
    //
    // ⚠ EVERY HEADING IS EXAMINED, NOT ONLY AN ATOMIC COLUMN'S. That restriction was wrong and measurement
    // proved it: the `Public` and `Auto` headings belong to columns that are NOT atomic, and at 320 they
    // painted `Pu…` and `A…` — four of six characters and three of four gone — because a single word that does
    // not fit is ellipsised whether or not the column asked for atomic treatment. Whether a label is cut is a
    // harder question than it looks, and {@link isHeadingTruncated} carries the two measurements that settled
    // it.
    for (const heading of Array.from(
      this.host.nativeElement.querySelectorAll<HTMLElement>('thead th'),
    )) {
      if (this.isHeadingTruncated(heading)) {
        heading.setAttribute('data-truncated', 'true');

        // The VISIBLE text, not `textContent`: see {@link visibleText}. A heading that carries hidden companion
        // wording would otherwise offer a tooltip containing text the reader cannot see on screen.
        heading.setAttribute('title', this.visibleText(heading));

        // ⚠ A CUT HEADING NEEDS A KEYBOARD ROUTE TO ITS OWN FULL TEXT, AND THE TOOLTIP ALONE IS NOT ONE.
        // `title` is a pointer affordance: a sighted keyboard user cannot hover, so before this branch the
        // in-place reveal was reachable on a cut VALUE and unreachable on a cut COLUMN NAME - measured on the
        // profile-property listing, where the `Validation Expression` heading painted an ellipsis while
        // sitting in neither the focusable set nor holding a focusable descendant.
        //
        // The stop is still CONDITIONAL, because the original reason for withholding it was sound: a sortable
        // heading already holds a focusable button, and a stop on the cell around it would put two stops on
        // one heading. So the cell takes a stop only when nothing inside it can take one, and the stylesheet
        // reveals on `:focus-within` as well as `:focus-visible` so that focusing a sort button reveals the
        // heading it belongs to. Every cut heading is reachable exactly once, either way.
        if (heading.querySelector(FOCUSABLE_SELECTOR) === null) {
          heading.setAttribute('tabindex', '0');
        } else {
          heading.removeAttribute('tabindex');
        }

        continue;
      }

      heading.removeAttribute('data-truncated');
      heading.removeAttribute('title');
      heading.removeAttribute('tabindex');
    }

    // ⚠ EVERY BODY CELL IS VISITED, NOT ONLY THE ATOMIC ONES, AND THE NARROWER QUERY ORPHANED MARKS. The
    // clearing branch below used to sit inside a `[data-atomic="true"]` loop, so a cell that STOPPED being
    // atomic - a column set replaced, a value re-projected through a different column - was never visited
    // again and kept its `data-truncated`, its `title` and, worst of all, its `tabindex`: a permanent phantom
    // tab stop on a value that is no longer cut. A cell is now cleared precisely because it is no longer a
    // candidate, which is the case the old query could not express.
    for (const cell of Array.from(body.querySelectorAll<HTMLElement>('th, td'))) {
      // The same reasoning as the headings above: a cell whose value is wrapped in an element that owns the
      // clipping reports no overflow of its own, so whichever box is actually cut is the one measured.
      const inner = cell.querySelector<HTMLElement>('[data-atomic-value]');
      const measured = inner !== null && inner.scrollWidth > inner.clientWidth ? inner : cell;

      // ⚠ THE FRACTIONAL TEST RUNS HERE TOO, AND OMITTING IT LEFT A CUT VALUE UNRECOVERABLE. The integer test
      // below is a fast positive signal, but it cannot see an overflow smaller than a pixel: measured on the
      // role listing at 768 and 1024, `QA Annual Patrons` needed 126.77px in a 125.39px content box - 1.38px
      // over - and reported `scrollWidth 135` against `clientWidth 134`, exactly 1, which this tolerance
      // discards. Chrome painted `QA Annual Patr…` and the cell carried no tooltip and no focus stop. The
      // heading path had already been corrected this way; the body path had not, and the two must agree.
      const cut =
        cell.getAttribute('data-atomic') === 'true' &&
        (measured.scrollWidth - measured.clientWidth > OVERFLOW_TOLERANCE_PX ||
          this.textExceedsBox(measured));

      if (cut) {
        cell.setAttribute('data-truncated', 'true');
        cell.setAttribute('tabindex', '0');

        // The pointer affordance. Set from the rendered text rather than from the projected cell, so a
        // template column — whose content this component never composes — is covered on the same terms as a
        // text column. VISIBLE text only: `textContent` produced a tooltip reading "—not recorded" over an
        // absent value, which is companion wording for assistive technology and is painted nowhere.
        cell.setAttribute('title', this.visibleText(cell));

        continue;
      }

      // Cleared rather than left behind: a column that widens, a row that is replaced, or a value that
      // shortens must give the stop and the tooltip back, or the tab order accumulates stops for values that
      // are no longer cut.
      cell.removeAttribute('data-truncated');
      cell.removeAttribute('tabindex');
      cell.removeAttribute('title');
    }
  }

  /**
   * Whether a heading's own label is being cut off.
   *
   * ⚠ `scrollWidth` ALONE IS A BLIND SIGNAL HERE, AND TRUSTING IT MISSED TWO VISIBLY CUT HEADINGS. Two separate
   * measurements established the shape of the problem:
   *
   * - The cell is the wrong element to measure. A heading's text lives in a `.data-table__label` span, and when
   *   that span owns the `overflow: hidden` the `th` around it reports equal widths however badly the label is
   *   cut — at 320 the `Username` heading painted `Userna…` with its label at 67/73 while its cell reported
   *   91/91.
   * - For a label allowed to WRAP, the browser lays out the already-ellipsised line, so the overflow collapses:
   *   `Auto` reported `scrollWidth - clientWidth = 0` while painting `A…`, and `Public` reported 2 while
   *   painting `Pu…`.
   *
   * So the question is asked the other way round — how much room does the text NEED — and which text has to fit
   * depends on whether the label may wrap:
   *
   * - A label held on one line must fit ENTIRELY, so the whole string is measured.
   * - A label allowed to wrap only needs its LONGEST WORD to fit; anything longer wraps to another line and
   *   loses nothing. Measuring the whole string here would have flagged `Billing Every` and `Trial Period`,
   *   which sit on two complete lines and are not cut at all.
   *
   * @param heading The heading cell to judge.
   * @returns True when the heading's label cannot show its text in full.
   */
  private isHeadingTruncated(heading: HTMLElement): boolean {
    const label = heading.querySelector<HTMLElement>('.data-table__label');

    // A command column's label is deliberately clipped to a single pixel so the column has an accessible name
    // without a painted heading. It is not truncated; it is hidden, and marking it would put a tooltip on every
    // icon column in the application.
    if (label === null || label.classList.contains(HIDDEN_LABEL_CLASS)) {
      return false;
    }

    return (
      label.scrollWidth - label.clientWidth > OVERFLOW_TOLERANCE_PX || this.textExceedsBox(label)
    );
  }

  /**
   * Whether an element's own text needs more room than its content box gives it, measured in fractions of a
   * pixel.
   *
   * ⚠ IT IS ASKED THE OTHER WAY ROUND FROM `scrollWidth`, AND THAT IS THE WHOLE POINT. `scrollWidth` and
   * `clientWidth` are INTEGERS, and a fixed table layout resolves fractional track widths - so a real overflow
   * smaller than a pixel is rounded into invisibility. Two separate values proved it: the `Auto` heading
   * overflowed its 33.594px label box by 0.559px and reported 0.153px, and the `QA Annual Patrons` role name
   * overflowed its 125.39px content box by 1.38px while reporting exactly 1, which a one-pixel tolerance
   * discards. Both painted an ellipsis on screen with no tooltip and no focus stop.
   *
   * Which text has to fit depends on whether the element may wrap:
   *
   * - Held on one line, the whole string must fit.
   * - Allowed to wrap, only the LONGEST WORD must fit; anything longer wraps to another line and loses nothing.
   *   Measuring the whole string here would flag `Billing Every` and `Trial Period`, which sit on two complete
   *   lines and are not cut at all.
   *
   * @param element The element whose own text is judged - a heading's label, or an atomic cell.
   * @returns True when the text cannot be shown in full.
   */
  private textExceedsBox(element: HTMLElement): boolean {
    const text = this.visibleText(element);

    if (text.length === 0) {
      return false;
    }

    const style = getComputedStyle(element);
    const holdsOneLine = style.whiteSpace === 'nowrap' || style.whiteSpace === 'pre';
    const parts = holdsOneLine ? [text] : text.split(/\s+/);
    let needs = 0;

    for (const part of parts) {
      needs = Math.max(needs, this.measureText(part, style));
    }

    // The rect is fractional, and the element's own padding and border are subtracted from it so the comparison
    // is against the space the text actually gets.
    return needs - this.contentWidth(element, style) > TEXT_OVERFLOW_TOLERANCE_PX;
  }

  /**
   * An element's text as a reader SEES it, with the text that is deliberately hidden from sight left out.
   *
   * ⚠ `textContent` IS THE WRONG STRING FOR BOTH OF THIS COMPONENT'S USES OF IT, AND IT PRODUCED SEVEN FALSE
   * POSITIVES. Cells carry companion text that is painted 1x1px under `clip-path: inset(50%)` so it reaches
   * assistive technology and nothing else - most visibly the shared absent-value marker, which pairs a painted
   * em-dash with a hidden "not recorded". Measured on the account listing, an absent telephone painted 13px of
   * em-dash inside a 64.66px box, so it had 51.66px to spare, yet `textContent` measured the whole
   * "—not recorded" string at about 85px and the cell was marked as cut. The visible consequences were a
   * tooltip reading "—not recorded" over a cell that was not truncated and a keyboard stop on a cell with
   * nothing to reveal.
   *
   * A decorative glyph marked `aria-hidden` is deliberately KEPT: it is hidden from assistive technology but it
   * is painted, so it occupies room and belongs in a measurement of what has to fit.
   *
   * @param element The element to read.
   * @returns The element's visible text, whitespace collapsed the way CSS collapses it.
   */
  private visibleText(element: HTMLElement): string {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT, {
      acceptNode: (node: Node): number => {
        for (
          let ancestor = node.parentElement;
          ancestor !== null;
          ancestor = ancestor.parentElement
        ) {
          if (
            ancestor.hasAttribute(VISUALLY_HIDDEN_ATTRIBUTE) ||
            ancestor.classList.contains(HIDDEN_LABEL_CLASS)
          ) {
            return NodeFilter.FILTER_REJECT;
          }

          if (ancestor === element) {
            break;
          }
        }

        return NodeFilter.FILTER_ACCEPT;
      },
    });

    let text = '';

    while (walker.nextNode() !== null) {
      text += walker.currentNode.nodeValue ?? '';
    }

    // Collapsed rather than merely trimmed, because a template's own newline indentation is inside the text node
    // and would otherwise be measured as spaces.
    return text.replace(/\s+/g, ' ').trim();
  }

  /**
   * The fractional width available to an element's own content.
   *
   * @param element The element to measure.
   * @param style Its computed style, already read by the caller.
   * @returns The content-box width in pixels.
   */
  private contentWidth(element: HTMLElement, style: CSSStyleDeclaration): number {
    const inset =
      Number.parseFloat(style.paddingLeft) +
      Number.parseFloat(style.paddingRight) +
      Number.parseFloat(style.borderLeftWidth) +
      Number.parseFloat(style.borderRightWidth);

    return element.getBoundingClientRect().width - (Number.isFinite(inset) ? inset : 0);
  }

  /**
   * The width a run of text wants in a given element's font, measured off the layout so nothing reflows.
   *
   * A canvas is used rather than a hidden element because measuring through the DOM means writing to it: a probe
   * element has to be inserted, laid out and removed inside the same frame as the reads around it, and that is
   * precisely the read-write interleaving this component's single measurement pass exists to avoid. The context
   * is created once and reused.
   *
   * @param text The run to measure.
   * @param style The computed style whose font it should be measured in.
   * @returns The width in pixels, or zero when no measurement context is available.
   */
  private measureText(text: string, style: CSSStyleDeclaration): number {
    if (this.textMeasurement === null) {
      if (typeof document === 'undefined') {
        return 0;
      }

      this.textMeasurement = document.createElement('canvas').getContext('2d');
    }

    if (this.textMeasurement === null) {
      return 0;
    }

    // The shorthand is assembled from four longhands only. A malformed value is silently IGNORED by the canvas,
    // which would leave the previous font in place and answer for the wrong typeface - so nothing that cannot
    // appear in the shorthand goes in, `font-variant-numeric` included.
    this.textMeasurement.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;

    return this.textMeasurement.measureText(text).width;
  }

  /**
   * The width to give the message row's content, or null to leave it to the cell.
   *
   * ⚠ THE DEFECT THIS CLOSES IS THAT AN EMPTY LISTING WAS UNREADABLE ON A PHONE, and the cause is a
   * deliberate decision elsewhere in this file rather than a mistake. The table is floored at
   * `--table-min-inline-size` so that columns SCROLL rather than crush, which is right for rows of data - but
   * a message row has no data and no columns, and it inherited the floor anyway. Measured at 320 and 375: the
   * stand-in panel was laid out 608 pixels wide inside a scrollport of 271 and 326, and because the panel
   * centres its own contents the heading began beyond the right edge and rendered as "Nothin" and "No". The
   * operator was told nothing at all, on the one screen state that exists purely to tell them something.
   *
   * Pinning the content to the scrollport fixes it without touching the floor, so nothing about how rows of
   * data lay out changes. The width is only imposed while the container is ACTUALLY clipping: at 1280 the
   * scrollport and the table are the same width, so returning null there leaves the cell to size its own
   * content exactly as before and the wide-viewport rendering is untouched.
   *
   * @returns The pixel width to apply, or null to impose none.
   */
  protected readonly messageViewportInlineSize = computed<number | null>(() =>
    this.horizontallyScrollableSignal() ? this.visibleInlineSizeSignal() : null,
  );

  /**
   * Settles whether a skip affordance is worth offering, by counting the controls a person would otherwise
   * have to pass through.
   *
   * ⚠ COUNTED FROM THE RENDERED BODY RATHER THAN INFERRED FROM THE ROW COUNT, because this component does
   * not know what its consumers project into a cell: one listing puts three commands in every row, another
   * puts none, and a third makes the row's own title a link. Measured on the account listing, the run was
   * THIRTY consecutive stops - ten rows of three - with nothing between them, because the username is plain
   * text rather than a link, and the first stop after them was the pager.
   *
   * It shares the row-window measurement's frame for the same reason the overflow measurement does.
   */
  private measureRowSkip(): void {
    const body = this.host.nativeElement.querySelector('tbody.data-table__body');

    if (body === null) {
      return;
    }

    const controls = body.querySelectorAll(FOCUSABLE_SELECTOR).length;

    this.offersRowSkipSignal.set(controls > ROW_SKIP_THRESHOLD);
  }

  /**
   * Moves focus past the table's rows, to the landing point rendered after it.
   *
   * The landing point carries its own wording rather than being an empty marker, so a reader who uses the
   * affordance is told where they arrived instead of hearing nothing; the next press of Tab then continues
   * into whatever follows the table, which is the pager on every listing that offers one.
   */
  protected skipPastRows(): void {
    const target = this.host.nativeElement.querySelector(`#${this.skipTargetId}`);

    if (target instanceof HTMLElement) {
      target.focus();
    }
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

  /**
   * Recomputes whether the container overflows, and publishes the answer.
   *
   * ⚠ ONE MEASUREMENT SERVES BOTH CONSUMERS, and this method is the viewport listeners' entry point into it.
   * The scroll region's conditional focus stop and the message row's viewport width are two readings of the
   * same geometry, so they are taken together in {@link measureHorizontalOverflow} rather than by two
   * observers that could read a box the other has just invalidated.
   */
  private measureScrollable(): void {
    this.measureHorizontalOverflow();
  }

  /** Registers the scroll and resize listeners the window depends on. */
  private observeViewport(): void {
    if (typeof window === 'undefined') {
      return;
    }

    const onViewportChange = (): void => {
      this.scheduleWindowUpdate();
      this.measureScrollable();
    };

    window.addEventListener('scroll', onViewportChange, { passive: true });
    window.addEventListener('resize', onViewportChange, { passive: true });

    this.destroyRef.onDestroy(() => {
      window.removeEventListener('scroll', onViewportChange);
      window.removeEventListener('resize', onViewportChange);
    });

    // A window resize is not the only thing that changes the answer: a column set arriving, rows arriving,
    // a sort indicator appearing or the sidebar collapsing all change the table's width or its container's
    // without the window moving at all. The observer covers every one of those; the listeners above remain
    // because a scroll can bring a windowed row into view and change the rendered width.
    if (typeof ResizeObserver === 'undefined') {
      return;
    }

    const observer = new ResizeObserver(() => {
      this.measureScrollable();

      // Truncation is measured here too, and leaving it out was a defect rather than an omission of
      // convenience: every width change this observer exists to catch is a width change that can start or
      // stop cutting a value. See {@link scheduleTruncationUpdate} for why the narrow scheduler is used
      // rather than the full window update.
      this.scheduleTruncationUpdate();
    });

    const container = this.host.nativeElement.querySelector<HTMLElement>('.data-table__container');
    const table = this.host.nativeElement.querySelector<HTMLElement>('table.data-table');

    if (container !== null) {
      observer.observe(container);
    }

    if (table !== null) {
      observer.observe(table);
    }

    this.destroyRef.onDestroy(() => {
      observer.disconnect();
    });
  }

  /** Cells spanned by the waiting and empty rows. Floored at one so neither can span zero cells. */
  protected readonly columnSpan = computed(() =>
    Math.max(this.columnsSignal().length, MINIMUM_COLUMN_SPAN),
  );

  /**
   * The size of the row set this table describes, including the heading row, for `aria-rowcount`.
   *
   * ⚠ THE MATCH SET, NOT THE PAGE, AND THE REVERSAL IS DELIBERATE. This reported the current page on the
   * stated ground that the component is handed one page and must not claim knowledge of the rest. That
   * reasoning holds for rendering and fails for this attribute: `aria-rowcount` exists so that a table
   * whose body holds a WINDOW onto a larger set can state the size of the set, and a page is such a window.
   * Reporting the page made it restate a number assistive technology can already count, and made it
   * actively wrong on any page but the first - measured on the second page of a seventeen-record listing,
   * where it reported eight while the pager beside it reported seventeen.
   *
   * TWO CASES STILL REPORT THE BODY, and both are cases where the body is not a window at all. A listing
   * that states no total is not paging, so its page IS the set. And a table standing a message row in
   * place of records describes exactly that row, because announcing a set size beside "no records found"
   * would offer a total with nothing to index into.
   */
  protected readonly ariaRowCount = computed(() => {
    const records = this.rowsSignal();
    const rendersMessage = this.showWaitingPlaceholder() || records.length === 0;

    if (rendersMessage) {
      return MESSAGE_ROW_COUNT + HEADER_ROW_COUNT;
    }

    const stated: number | null = this.resolvedTotalCount();
    const described = stated !== null && stated >= records.length ? stated : records.length;

    return described + HEADER_ROW_COUNT;
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

  /**
   * The columns in the order they are PRESENTED, which is the caller's declared order except while the
   * region is actually clipping, when the row-identity column is moved to the front.
   *
   * ⚠ WHY A COMPONENT MAY REORDER A LAYOUT THE CALLER DECIDED, WHEN {@link DataTableColumn} SAYS IT MAY NOT.
   * The type's `readonly` members stop a column being REWRITTEN — a caller's width, label or sort key is
   * never altered here, and still is not. What moves is which track a column occupies, and only in the one
   * situation the caller cannot see from where it sits: whether the viewport is currently hiding part of the
   * table. That is measured here and nowhere else, so this is the only place the decision can be made.
   *
   * ⚠ THE MEASURED DEFECT. At a 320px viewport every listing in the application resolves to a 271px
   * scrollport against a 968px table, hiding 697px. Four of the six listings put their commands first, so
   * the initial view was command columns and blank icon-only headings with the record's identity off screen
   * entirely: the member-services grid's name began 8px past the right edge, profile definitions 54px past,
   * modules 108px past and the portal-alias grid 214px past — that last one showing nothing but a column of
   * identical "Edit" buttons, with no indication of WHICH host name each would edit. The two listings whose
   * identity did fall inside the scrollport did so with 24px and 9px to spare, i.e. by accident of column
   * arithmetic rather than by design.
   *
   * ⚠ IT CANNOT OSCILLATE, AND THAT IS WHY IT IS SAFE TO DRIVE FROM A MEASUREMENT. Reordering moves the same
   * columns at the same widths, so the table's total width is unchanged and the very measurement that
   * triggered the reorder is unaffected by it. A reorder therefore cannot make the region stop clipping and
   * so cannot undo itself.
   *
   * The declared order is restored the moment nothing is hidden, so a desktop reader sees the arrangement
   * the feature authored — which for several of these grids is documented legacy parity — and only a reader
   * who would otherwise have lost the identity sees it hoisted.
   */
  private readonly presentedColumns = computed<readonly DataTableColumn<TRow>[]>(() => {
    const declared = this.columnsSignal();

    if (this.horizontallyScrollableSignal() === false) {
      return declared;
    }

    const identityIndex = declared.findIndex((column) => column.rowHeader === true);

    // `0` is as much a no-op as `-1`: already first needs no move, and a column set with no declared row
    // header has no identity to hoist. Neither case is a fault, so neither is reported.
    if (identityIndex <= 0) {
      return declared;
    }

    return [
      declared[identityIndex],
      ...declared.slice(0, identityIndex),
      ...declared.slice(identityIndex + 1),
    ];
  });

  /**
   * Whether the identity column is currently pinned to the inline start — true exactly when the region is
   * clipping AND the column set declares a row header. Bound to a container attribute so the stylesheet can
   * make that one column sticky without needing to know which track it is in.
   */
  protected readonly identityPinned = computed<boolean>(
    () =>
      this.horizontallyScrollableSignal() &&
      this.columnsSignal().some((column) => column.rowHeader === true),
  );

  /**
   * How many columns are currently outside the scrollport, for the painted overflow cue.
   *
   * ⚠ THE CUE EXISTS BECAUSE THE REGION ANNOUNCED ITSELF TO ASSISTIVE TECHNOLOGY ONLY. The container already
   * takes `role="region"`, a name borrowed from the caption and a tab stop while it clips, so a screen-reader
   * user is told there is a scrollable region and a keyboard user can reach it. A SIGHTED reader was told
   * nothing at all: measured across all six listings at 320px and 375px, no painted text anywhere inside any
   * table container mentioned scrolling, and no scrollbar was rendered either, so the grid simply appeared to
   * end at the container edge. Counting the columns rather than saying "scroll for more" reports the size of
   * what is missing, which is the part a reader cannot infer.
   */
  protected readonly hiddenColumnCount = this.hiddenColumnCountSignal.asReadonly();

  /**
   * The cue's wording. Singular and plural are written out rather than assembled with a conditional `s`,
   * because the two sentences differ in their verb as well as their noun.
   */
  protected readonly overflowCueText = computed<string>(() => {
    const hidden = this.hiddenColumnCountSignal();

    return hidden === 1
      ? OVERFLOW_CUE_SINGULAR
      : OVERFLOW_CUE_PLURAL.replace(OVERFLOW_CUE_COUNT_TOKEN, String(hidden));
  });

  /** Track sizes for the table's `colgroup`. */
  protected readonly columnWidths = computed<readonly DataTableColumnWidth[]>(() =>
    this.presentedColumns().map((column) => ({
      key: column.key,
      width: resolveWidth(column.width),
    })),
  );

  /** The heading cells, fully derived. */
  protected readonly headerCells = computed<readonly DataTableHeaderCell<TRow>[]>(() => {
    // The DISPLAYED pair, not the requested one: a heading reports the order of the rows beneath it.
    const activeKey = this.displayedSortBySignal();
    const activeDirection = this.displayedSortDirSignal();

    return this.presentedColumns().map((column) => {
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
        atomic: column.atomic === true,
        rowHeader: column.rowHeader === true,
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
    const columns = this.presentedColumns();
    // ⚠ THE OFFSET IS WHAT MAKES THE INDEX A POSITION IN THE SET RATHER THAN IN THE WINDOW. Without it
    // the indices restart at two on every page, so the eleventh record of seventeen announced as row two -
    // a number that contradicts both the pager and the row count above.
    //
    // `rowIndex` stays window-relative on purpose and is a different quantity: it addresses the projected
    // cell templates and the selection state, both of which are indexed by position within the body.
    const offset = this.rowOffsetSignal();

    return this.rowsSignal().map((row, rowIndex) => ({
      row,
      rowIndex,
      ariaRowIndex: offset + rowIndex + HEADER_ROW_COUNT + 1,
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
    // The guard follows the announced state exactly, so what the control says about itself and what it does
    // cannot diverge: `aria-disabled` is bound to the same expression.
    if (cell.sortable === false || this.sortUnavailable()) {
      return;
    }

    this.sortChange.emit({
      key: cell.key,
      direction: nextDirection(cell.sorted, this.displayedSortDirSignal()),
    });
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
    this.measureScrollable();
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
 * The direction, in the words a person hears rather than the wire's.
 *
 * @param direction The direction the listing is sorted in.
 * @returns `ascending` or `descending`.
 */
function describeDirection(direction: SortDirection): string {
  return direction === DESCENDING ? 'descending' : 'ascending';
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
    atomic: column.atomic === true,
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
