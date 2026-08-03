/**
 * The shared, presentational record grid.
 *
 * This is the single replacement for the eight `asp:DataGrid` instances in the five
 * in-scope administration trees: the portal list, the portal-alias list, the account
 * list, the profile-property list, the role list, the role-membership list, the
 * member-services list and the page-module list. It renders a real `table` so the
 * row-and-column relationships assistive technology reads a cell by survive; a grid
 * assembled from `div` elements discards them and no amount of ARIA restores them as
 * faithfully as the native element supplies them.
 *
 * It is presentational and nothing else. It performs no sorting, no paging, no
 * filtering and no input or output of its own: it renders what it was handed and
 * reports what the reader asked for. Nothing is injected into it.
 *
 * ## The five inputs and two outputs are closed
 *
 * `columns`, `rows`, `sortBy`, `sortDir` and `loading` in; `sortChange` and
 * `rowSelect` out. Every further affordance is delivered by content projection or by
 * a {@link DataTableColumn.cellTemplate}, never by widening this surface. The reason
 * is measured rather than stylistic and is set out on {@link DataTableColumn} and in
 * the migration notes below.
 *
 * ## Why the inputs are accessors over signals
 *
 * A plain `@Input()` field is not a reactive source, so a `computed()` reading one
 * would never recompute and this component would be silently inert under
 * `OnPush` — the defect would show as a grid that renders its first page and then
 * never updates. Each input is therefore a public accessor pair whose setter writes a
 * private `signal()`, and every derivation the template reads is a `computed()` over
 * those signals.
 *
 * ## Why the template calls no functions
 *
 * A function invoked from a template re-runs on every change-detection pass. With one
 * call per cell across a page of rows that is the single most likely performance
 * defect in a grid, and it defeats the point of `OnPush`. The template therefore reads
 * pre-projected view models — {@link DataTableHeaderCell}, {@link DataTableBodyRow}
 * and {@link DataTableBodyCell} — in which every {@link DataTableColumn.value}
 * formatter has already been invoked exactly once. The only methods the template
 * calls are event handlers, which run on interaction rather than on redraw.
 *
 * MIGRATION: sorting is EMITTED, never performed. The legacy grids posted the whole
 * page back and re-queried, and paging was likewise a server round trip - the account
 * screen held `Private _CurrentPage As Integer = 1` and subtracted one on every call
 * down to the provider. Ordering therefore belongs to the server here too: a page of
 * ten rows cannot be ordered meaningfully on the client, because reordering the
 * visible ten produces an order that is right within the page and wrong across the
 * match set. {@link DataTableComponent.rows} is `readonly` and is never sorted,
 * reversed or spliced in place.
 *
 * MIGRATION: `sortDir` carries the server's own member names, `'Ascending'` and
 * `'Descending'`, imported from the paging contract rather than restated here. An
 * abbreviated spelling is not a style variant: the value is bound from the query
 * string and the binder answers `sortDir=asc` with `400 Bad Request` and
 * `The value 'asc' is not valid for SortDir.` A locally declared `'asc' | 'desc'`
 * union would compile cleanly and fail every sort at run time.
 *
 * MIGRATION: the legacy per-row commands were raster image buttons, several carrying
 * no alternative text at all, and one carried none and no visibility condition
 * either. Row commands are projected by the consuming feature as controls that own
 * their own accessible names; this component references no image asset.
 *
 * @typeParam TRow The row contract carried on the current page - always a transfer
 *   contract off the wire, never a persisted entity. Deliberately unconstrained so a
 *   grid of any row shape is expressible. {@link DataTableColumn.field} is meaningful
 *   only for object-shaped rows, which is every row this application renders.
 */

import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  TemplateRef,
  computed,
  signal,
} from '@angular/core';

// The ONE standalone directive imported here, and the only import that is not a composed
// sibling. It is emphatically NOT the umbrella common-directives module, which is
// imported nowhere in this workspace; it is the single tree-shakeable directive by which a
// caller's
// {@link DataTableColumn.cellTemplate} is rendered, and there is no other mechanism in
// Angular for rendering a `TemplateRef` from a template. Without it `cellTemplate` and
// the whole `actions` column kind would be declared members that could never render -
// stubs - and the four legacy template columns and two inline-editable checkbox columns
// they exist to carry would have no target at all. It is imported individually rather
// than as part of a module precisely so nothing unused comes with it.
import { NgTemplateOutlet } from '@angular/common';

import type { SortDirection } from '../../../core/models/paged-result.model';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../loading-spinner/loading-spinner.component';

/**
 * Inline alignment of a column's heading or of its body cells.
 *
 * Logical rather than physical - `start` and `end` follow the writing direction, so a
 * right-to-left reader gets the correct edge without a second rule.
 *
 * A string-literal union rather than an enumeration: it needs no run-time
 * representation, and `isolatedModules` is enabled, which rules out the `const` form
 * of an enumeration that would otherwise avoid the emit.
 */
export type DataTableAlign = 'start' | 'center' | 'end';

/**
 * What a column puts in its body cells.
 *
 * - `text` - bound or formatted text, covering the legacy `dnn:textcolumn` and
 *   `asp:BoundColumn`, including the one that carried `DataFormatString="{0:0.00}"`.
 * - `template` - caller-supplied cell content, covering `asp:TemplateColumn` and the
 *   two inline-editable `dnn:checkboxcolumn` cells that posted back on change.
 * - `actions` - projected row controls, covering `dnn:imagecommandcolumn`. Declared
 *   explicitly rather than inferred, because it is a statement about semantics and
 *   not about where the content came from: an actions cell suppresses row activation
 *   so that pressing Edit never doubles as selecting the row.
 */
export type DataTableColumnKind = 'text' | 'template' | 'actions';

/** The `aria-sort` states a sortable heading can report. */
export type DataTableAriaSort = 'ascending' | 'descending' | 'none';

/**
 * The context a {@link DataTableColumn.cellTemplate} is rendered against.
 *
 * `$implicit` is the row, so a caller may write `let-row` and receive it without
 * naming a member. The row is repeated under `row` for callers that prefer to be
 * explicit, and the column travels with it so one template can serve several columns.
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

  /**
   * Zero-based position of the row within the CURRENT PAGE, not within the match set.
   *
   * Suitable for striping and for `let-i="rowIndex"`; never suitable as an
   * identifier. The page offset is the feature's to hold, not this component's.
   */
  readonly rowIndex: number;
}

/**
 * One column of a {@link DataTableComponent}.
 *
 * Every member is `readonly`: a column set describes a layout the caller has already
 * decided, and a component that could rewrite it would be describing something the
 * caller never asked for.
 *
 * ## Why {@link key} is separate from {@link label}
 *
 * Because a label-keyed column model is provably impossible against this codebase.
 * The role list declares twelve columns in one grid, and among them `HeaderText`
 * `"Every"` appears TWICE - once over the billing period and once over the trial
 * period - and `HeaderText` `"Period"` appears TWICE as well, over the billing
 * frequency and the trial frequency. Keying on the label would collapse each pair
 * into one column and silently drop the other, with no error anywhere.
 *
 * Three further independent proofs that the two are different things: the account
 * list binds `UserName` under the heading `Username`, a casing mismatch; the
 * role-membership list binds `RoleName` under the heading `SecurityRole`, wholly
 * different words; and the portal-alias list binds `HTTPAlias` under `HTTP Alias`,
 * differing by a space.
 *
 * The legacy application already drew this distinction, which is the strongest
 * argument of all: header text was a LOCALISED RESOURCE looked up BY the column's own
 * stable identity, keyed `PortalId.Header`, `Title.Header`, `HostingFee.Header` and so
 * on. Identity was structure and the label was data. This descriptor keeps it that
 * way.
 *
 * Note the refinement that makes the model workable: even where two labels collide,
 * the underlying field differs - billing period against trial period, billing
 * frequency against trial frequency - so a field name remains a serviceable unique
 * key. The descriptor must not therefore INSIST on a field, because derived columns
 * have none at all: one composes a portal's alias list from its identifier, another
 * formats an expiry date, and one composes a postal address from SIX separate profile
 * members. That is why {@link key} is a plain `string` and not `keyof TRow`.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableColumn<TRow> {
  /**
   * Stable identity of the column, unique within one column set.
   *
   * This is the `track` expression of the heading and cell loops, and the value
   * reported as {@link DataTableSortChange.key}, so for a sortable column it must be
   * the sort name the collection endpoint accepts. Never the label - see the note on
   * this interface for why that is not a preference.
   */
  readonly key: string;

  /**
   * Visible heading text.
   *
   * Free to duplicate another column's label, and in the role list it genuinely does.
   * Suppress it with {@link headerHidden} without losing it: the heading stays in the
   * accessibility tree either way, so a cell is still announced with its column name.
   */
  readonly label: string;

  /**
   * What the body cells contain. Defaults to `template` when a
   * {@link cellTemplate} is supplied and to `text` otherwise.
   *
   * Set `actions` explicitly for a column of projected row controls. Only that value
   * makes the cell suppress row activation, and no inference can supply it.
   */
  readonly kind?: DataTableColumnKind;

  /**
   * Row member to render as plain text.
   *
   * Typed as a key of the row, so a mistyped member name is a compile error rather
   * than a blank column. For TEXT-SHAPED values only - a string, a number or a large
   * integer. A boolean, a date object or a nested object must go through
   * {@link value} or {@link cellTemplate} instead, and that is not an arbitrary
   * restriction: no legacy grid ever bound a boolean as text either. All four legacy
   * boolean columns were template columns - two rendered a checked or unchecked
   * image, two rendered a checkbox that posted back on change.
   *
   * Ignored when {@link value} is also supplied, which takes precedence.
   */
  readonly field?: keyof TRow & string;

  /**
   * PURE formatter producing the cell's text.
   *
   * Covers the formatted and derived columns: prices, periods, expiry dates, an
   * alias list composed from an identifier, a postal address composed from six
   * profile members. Invoked exactly once per row per redraw, inside the projection,
   * and never from the template.
   *
   * Must be pure and must not throw. It runs during a `computed()` evaluation, so a
   * side effect here would fire at an unpredictable point in change detection.
   *
   * @param row The row being rendered.
   * @returns The text to display. Return the empty string for an absent value; never
   *   return `null` or `undefined`, and never the words `null` or `undefined`.
   */
  readonly value?: (row: TRow) => string;

  /**
   * Caller-supplied cell content, for anything richer than text.
   *
   * Compiled in the CALLER's template context, not this component's, which is why
   * this component imports no directive and no pipe: a permission directive or a
   * display pipe used inside the template belongs to the feature that wrote it.
   */
  readonly cellTemplate?: TemplateRef<DataTableCellContext<TRow>>;

  /**
   * Inline alignment of the HEADING. Defaults to `start`.
   *
   * Independent of {@link bodyAlign} by necessity - see that member.
   */
  readonly headerAlign?: DataTableAlign;

  /**
   * Inline alignment of the BODY cells. Defaults to `start`.
   *
   * ## Why this is independent of {@link headerAlign}
   *
   * Because the legacy markup separates them, at two levels at once, and deriving one
   * from the other would misrender most of the eight grids.
   *
   * At the GRID level the two disagree in three of the eight: the role list, the
   * account list and the profile-property list each centre the heading row and
   * start-align the body. At the COLUMN level the portal list defaults its body to
   * centre and then overrides BOTH sides on three columns - the identifier, the title
   * and the alias list each set a body alignment AND a heading alignment, and the
   * title states them in the opposite order to the identifier, so an order-sensitive
   * reading of the markup would miss it. Four further columns in that same grid set
   * only a body vertical alignment and inherit the centre, and a ninth column
   * overrides the heading STYLE alone, making three distinct heading treatments
   * inside a single grid.
   *
   * So: two members, no derivation, and no single grid-wide alignment.
   *
   * Vertical alignment is deliberately NOT a member. The legacy body cells set it to
   * the top essentially uniformly, so it is normalised once in the stylesheet rather
   * than made configurable. The bare-element rule that would otherwise apply a
   * baseline alignment to headings never took effect at run time, because every one
   * of the eight grids either sets a heading class or suppresses its heading row
   * outright.
   */
  readonly bodyAlign?: DataTableAlign;

  /**
   * Whether the heading offers sorting. Defaults to absent, meaning not sortable.
   *
   * Opt-in per column because the endpoint decides which names it accepts and answers
   * an unrecognised one with a field-level `400`. Offering a control that produces a
   * rejected request is worse than offering none.
   */
  readonly sortable?: boolean;

  /**
   * Track width of the column, applied through a `col` element in the table's
   * `colgroup` so that no cell rule carries a size.
   *
   * INTRINSIC UNITS OR A TOKEN ONLY - a percentage, a fraction, `minmax()`,
   * `min-content`, `max-content`, or `var(--…)`. NEVER a pixel literal. Every legacy
   * width was a fixed literal - five of them in the profile-property list alone, plus
   * a fifteen-pixel command column and two unitless numbers - and each is replaced by
   * an intrinsic measure or a spacing token so the grid reflows and honours a
   * reader's font size.
   *
   * Blank text and omission mean the same thing: the column takes its share
   * automatically.
   *
   * Sizing the columns here rather than from cell content is also what makes the
   * render-virtualisation strategy safe - see {@link DataTableComponent}.
   *
   * ONE OBLIGATION ON THE CALLER, because the component cannot discharge it. An `actions`
   * column must be given `min-content`, `max-content`, or omitted so it sizes itself; it
   * must NOT be given a track narrower than the commands it carries. The commands wrap onto
   * further lines when the track is tight, which is the component's defence, but wrapping
   * cannot shrink a control below its own minimum width: measured with two text commands in
   * a 2.5rem track, the first still overhung its cell by 16.97px and painted over the value
   * in the next column, because a table cell is `overflow: visible` and clipping it would
   * hide a control rather than reveal a layout problem. A content-sized track cannot express
   * that mistake at all, which is why it is the documented contract rather than a suggestion.
   */
  readonly width?: string;

  /**
   * Whether to hide the heading text visually while keeping it announced.
   *
   * For columns whose heading would be noise - a column of row commands, or an
   * indicator with no meaningful name. The text stays in the accessibility tree, so a
   * cell is still announced with its column name and nothing is lost.
   *
   * Legacy practice here was inconsistent, which is why this is normalised rather
   * than reproduced: of the eight grids, exactly one labelled its command columns, as
   * `Edit`, `Del`, `Dn` and `Up`; the portal, role and account lists supplied no
   * heading text for theirs at all; and the page-module grid suppressed its entire
   * heading row. Every column here therefore carries a label and this member decides
   * whether it is painted.
   */
  readonly headerHidden?: boolean;
}

/**
 * A reader's request to reorder the match set.
 *
 * Carries the direction as well as the key, so a consumer never has to remember what
 * it last asked for in order to interpret the next request.
 */
export interface DataTableSortChange {
  /** The {@link DataTableColumn.key} to order by, which is the endpoint's sort name. */
  readonly key: string;

  /** The direction to order in, in the server's own spelling. */
  readonly direction: SortDirection;
}

/**
 * A heading cell, fully derived so the template evaluates no expression of its own.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableHeaderCell<TRow> {
  /** The column this heading describes, for callers that need the descriptor itself. */
  readonly column: DataTableColumn<TRow>;

  /** {@link DataTableColumn.key}, and the `track` expression of the heading loop. */
  readonly key: string;

  /** {@link DataTableColumn.label}. */
  readonly label: string;

  /** Whether the label is painted, or announced only. */
  readonly labelVisible: boolean;

  /** Whether this heading offers sorting. */
  readonly sortable: boolean;

  /** Whether this heading carries the ACTIVE sort, per `sortBy` alone. */
  readonly sorted: boolean;

  /**
   * The `aria-sort` value, or `null` to omit the attribute.
   *
   * Omitted for a column that offers no sorting: `none` means "sortable but not
   * currently sorted", which on an unsortable column would be a false claim. A table
   * in which every heading claims to be sorted conveys nothing.
   */
  readonly ariaSort: DataTableAriaSort | null;

  /** Resolved {@link DataTableColumn.headerAlign}. */
  readonly align: DataTableAlign;
}

/**
 * One body cell, with its text already produced and its template context already
 * built.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableBodyCell<TRow> {
  /** {@link DataTableColumn.key}, and the `track` expression of the cell loop. */
  readonly key: string;

  /** Resolved {@link DataTableColumn.kind}, deciding which branch the template takes. */
  readonly kind: DataTableColumnKind;

  /**
   * The cell's text for a `text` column, already formatted; the empty string
   * otherwise.
   *
   * Never `null`, never `undefined` and never the WORDS `null` or `undefined`. An
   * absent value renders as the empty string, which is also the legacy convention:
   * the sentinel module defines its null string as the empty string rather than as a
   * null reference.
   */
  readonly text: string;

  /** The caller's cell template for a `template` column, `null` otherwise. */
  readonly template: TemplateRef<DataTableCellContext<TRow>> | null;

  /** The context to render {@link template} against, `null` when there is none. */
  readonly context: DataTableCellContext<TRow> | null;

  /** Resolved {@link DataTableColumn.bodyAlign}. */
  readonly align: DataTableAlign;
}

/**
 * One body row, with every cell projected.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableBodyRow<TRow> {
  /**
   * The row itself, and its own identity.
   *
   * This object reference is the `track` expression of the row loop and the value
   * compared for selection. See {@link DataTableComponent} for why identity is taken
   * from the reference and never from a member.
   */
  readonly row: TRow;

  /** Zero-based position within the current page. Never an identifier. */
  readonly rowIndex: number;

  /**
   * One-based position among ALL rows of the table, counting the heading row as the
   * first. Bound to `aria-rowindex`.
   */
  readonly ariaRowIndex: number;

  /** The projected cells, in column order. */
  readonly cells: readonly DataTableBodyCell<TRow>[];
}

/**
 * A `col` entry sizing one track of the table.
 *
 * @see DataTableColumn.width
 */
export interface DataTableColumnWidth {
  /** {@link DataTableColumn.key}, and the `track` expression of the `colgroup` loop. */
  readonly key: string;

  /** The resolved width, or `null` to let the column take its share automatically. */
  readonly width: string | null;
}

/** The ascending member of the wire sort vocabulary. */
const ASCENDING: SortDirection = 'Ascending';

/** The descending member of the wire sort vocabulary. */
const DESCENDING: SortDirection = 'Descending';

/**
 * Inline alignment applied when a column states none.
 *
 * Neutral on purpose. Visual continuity with the legacy portal is the FEATURE's to
 * state per column, because the eight legacy grids disagreed with each other: some
 * centred their headings and start-aligned their bodies, one centred both. A
 * component-level guess would silently override whichever of them a feature was
 * reproducing.
 */
const DEFAULT_ALIGN: DataTableAlign = 'start';

/**
 * Rows contributed by the heading section, for `aria-rowcount` and `aria-rowindex`.
 *
 * The template renders exactly one heading row, and ARIA counts it: the heading is row
 * one, so the first body row is row two.
 */
const HEADER_ROW_COUNT = 1;

/** Minimum `colspan` for the waiting and empty rows, so neither can span zero cells. */
const MINIMUM_COLUMN_SPAN = 1;

/**
 * Rows the body contributes while it is waiting or empty.
 *
 * The waiting and empty branches each render exactly ONE spanning row. It is counted, not
 * ignored: a row that exists in the table but not in `aria-rowcount` leaves a screen reader
 * being told the table has fewer rows than it will actually encounter.
 */
const MESSAGE_ROW_COUNT = 1;

/** Activation key that needs no default suppression. */
const ENTER_KEY = 'Enter';

/** Activation key whose default action scrolls the page and must be suppressed. */
const SPACE_KEY = ' ';


/**
 * The sortable, keyboard-operable record grid.
 *
 * ## Row identity is the row's OBJECT REFERENCE
 *
 * Both the `track` expression of the row loop and the selection comparison use the row
 * object itself. No member is inspected, and that is the whole point.
 *
 * The alternative - reading an identifier member - is unsafe here and quietly so. The
 * legacy identity seeds make BOTH zero and minus one legitimate keys: the portal table
 * seeds its identity at minus one, while the role, page and module tables seed theirs
 * at zero. So a live listing really does contain a portal whose identifier is minus
 * one and a role whose identifier is zero. Minus one is SIMULTANEOUSLY the legacy
 * integer null sentinel, and the legacy markup compares against it directly - the
 * page-module grid enables a control only when a module identifier is not minus one,
 * on a column seeded at zero. Any test of the form `if (id)`, `id > 0` or `id ?? -1`
 * would therefore mis-key the first role, page or module row, or discard a real
 * portal. There would be no error: the DOM would simply reuse the wrong node.
 *
 * Taking the reference is immune to all of it, because there is no number to misread.
 * This component reads no identifier member anywhere, and it cannot be asked to - the
 * descriptor names no key field, and adding one would be a sixth input.
 *
 * CONSEQUENCE, stated plainly: when the feature re-queries and replaces the array,
 * every row is a new object, so every row's DOM is discarded and rebuilt rather than
 * patched. That is correct for a server-paged grid - the whole page changed - and it is
 * strictly less work than the legacy full-page postback that re-rendered the entire
 * screen. Within a page, while the array is untouched, rows are tracked stably and
 * patched in place. A second consequence is desirable: replacing the array clears a
 * selection that pointed into the old one, so the selected state can never outlive the
 * row it described.
 *
 * ## Selection
 *
 * There is no `selectedRow` input, so selection is held internally, exposed
 * programmatically through `aria-current` rather than by styling alone, and reported
 * through `rowSelect`. It is deliberately kept OUT of the row projection: were it a
 * member of {@link DataTableBodyRow}, selecting a row would re-run every
 * {@link DataTableColumn.value} formatter on the page to recompute text that had not
 * changed. The template compares the reference instead, which is a pointer test.
 *
 * ## Render virtualisation, without a scrolling package
 *
 * The migration plan mandates virtualised rendering for large lists here, and the
 * pinned dependency set contains no scrolling package - twenty-one packages, none of
 * them the component-development kit - so no viewport component is available and none
 * may be added.
 *
 * The strategy adopted is therefore CSS render-virtualisation: the stylesheet marks
 * body rows so the engine may skip the layout and paint of rows that are off screen,
 * and supplies a placeholder size for the rows it skips. This needs zero JavaScript,
 * zero dependency and no change to the public surface; every row stays in the DOM, so
 * every row stays in the accessibility tree, in find-in-page and in the tab order.
 *
 * Two supporting facts make it work rather than merely compile:
 *
 * - Skipping a row's layout is only safe when column widths do not depend on that
 *   row's content, because otherwise the columns would shift as rows entered and left
 *   the viewport. {@link DataTableColumn.width} applied through the `colgroup`, with a
 *   fixed table layout, supplies exactly that guarantee. The width member and the
 *   virtualisation strategy are two halves of one design.
 * - The placeholder size is composed ENTIRELY from existing design tokens - the base
 *   line height, the base type size and a spacing step - because the token set
 *   declares no row-height or intrinsic-size token. That gap is reported rather than
 *   papered over with a pixel literal.
 *
 * Because every row remains rendered, this strategy is not windowing, and the
 * `aria-rowcount` and `aria-rowindex` values below are not strictly needed to keep a
 * screen reader honest about the table's size. They are published anyway: they are
 * accurate, they are valid on a native table without adding an explicit role, they
 * cost nothing visually, and they mean a future switch to true windowing cannot
 * silently start misreporting the row count.
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
export class DataTableComponent<TRow> {
  private readonly columnsSignal = signal<readonly DataTableColumn<TRow>[]>([]);

  private readonly rowsSignal = signal<readonly TRow[]>([]);

  private readonly sortBySignal = signal<string | undefined>(undefined);

  private readonly sortDirSignal = signal<SortDirection>(ASCENDING);

  private readonly loadingSignal = signal(false);

  private readonly selectedRowSignal = signal<TRow | null>(null);

  /**
   * Sets the columns to render, in order.
   *
   * Public because the strict input-access check rejects a non-public input at every
   * consuming template; the same applies to all five inputs. The write type admits
   * absent values because a store's projection is routinely nullable, and widening
   * here keeps those call sites honest instead of pushing an assertion onto the
   * caller. An absent set renders as no columns rather than throwing.
   *
   * @param value The column descriptors, or an absent value for none.
   */
  @Input()
  public set columns(value: readonly DataTableColumn<TRow>[] | null | undefined) {
    this.columnsSignal.set(value ?? []);
  }

  public get columns(): readonly DataTableColumn<TRow>[] {
    return this.columnsSignal();
  }

  /**
   * Sets the rows of the current page, already ordered and paged by the server.
   *
   * Read as `readonly` and never mutated: these are the items of a paging envelope
   * whose members are all `readonly`, because a response has already happened and a
   * component that rewrote one would be describing something the server never said.
   *
   * Replacing the array DROPS a selection that is no longer on the page, so the
   * selected state cannot outlive the row it described. Membership is tested by
   * reference, consistent with how identity is taken everywhere in this component.
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
  }

  public get rows(): readonly TRow[] {
    return this.rowsSignal();
  }

  /**
   * Sets the {@link DataTableColumn.key} the rows are currently ordered by, or an
   * absent value when the server's own ordering applies.
   *
   * Blank text and omission mean the same thing, matching the paging contract, so a
   * feature that clears its sort by binding the empty string gets an unsorted table
   * rather than a heading claiming to be sorted by nothing.
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
   * Sets the direction {@link sortBy} is applied in.
   *
   * Consulted only when a sort key is present. Falls back to ascending, which is both
   * the server's default when the member is omitted and the direction a first
   * activation asks for.
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
   * Sets whether the table is waiting for data.
   *
   * While waiting, the body shows the shared progress indicator instead of the
   * previous page, so nobody reads stale rows that are about to be replaced, and sort
   * activation is refused so a second request cannot be queued behind the first.
   *
   * Compared against `true` rather than tested for truthiness, in keeping with this
   * component's rule against truthiness tests.
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
   * Emits the ordering the reader asked for.
   *
   * The component does NOT update its own {@link sortBy} or {@link sortDir}. Those two
   * inputs are the sole source of truth for `aria-sort`, and an optimistic local
   * update would make a heading announce an order whose request had failed. It emits
   * and waits: the feature rebinds both once the reordered page has actually arrived.
   *
   * This and {@link rowSelect} are the only event emitters in the component. An
   * emitter is not used to hold state anywhere, and no reactive stream stands in for
   * one either.
   */
  @Output() public readonly sortChange = new EventEmitter<DataTableSortChange>();

  /** Emits the row that was activated. */
  @Output() public readonly rowSelect = new EventEmitter<TRow>();

  /** Whether a request is in flight, for the template's waiting branch. */
  protected readonly isLoading = this.loadingSignal.asReadonly();

  /** The selected row, or `null`. Compared by reference in the template. */
  protected readonly selectedRow = this.selectedRowSignal.asReadonly();

  /**
   * Whether there is nothing to show and nothing on the way.
   *
   * An empty page is a legitimate answer, not an error, and it is distinguished from
   * "still loading" so the empty state is never claimed prematurely.
   */
  protected readonly isEmpty = computed(
    () => this.loadingSignal() === false && this.rowsSignal().length === 0,
  );

  /**
   * Cells spanned by the waiting and empty rows.
   *
   * Floored at one so neither can span zero cells. A message spanning fewer cells than
   * the table has would leave real empty cells beside it, which a screen reader
   * announces as blanks.
   */
  protected readonly columnSpan = computed(() =>
    Math.max(this.columnsSignal().length, MINIMUM_COLUMN_SPAN),
  );

  /**
   * Total rows the table actually renders, including the heading row, for `aria-rowcount`.
   *
   * The count of the CURRENT PAGE, not of the match set: this component is handed one
   * page and must not claim knowledge of the rest. The pager reports the whole.
   *
   * The waiting and empty branches replace the records with ONE spanning row, and that row
   * is counted rather than ignored. Counting only the records would announce a one-row
   * table while a screen reader went on to meet a second row, which is precisely the kind
   * of quiet disagreement `aria-rowcount` exists to prevent.
   */
  protected readonly ariaRowCount = computed(() => {
    const records = this.rowsSignal();
    const rendersMessage = this.loadingSignal() === true || records.length === 0;

    return (rendersMessage ? MESSAGE_ROW_COUNT : records.length) + HEADER_ROW_COUNT;
  });

  /**
   * The `aria-rowindex` of the waiting or empty row.
   *
   * It occupies the position the first record would have held, immediately after the
   * heading row.
   */
  protected readonly messageRowIndex = HEADER_ROW_COUNT + MESSAGE_ROW_COUNT;

  /**
   * The heading row's `aria-rowindex`.
   *
   * ARIA row indexes are one-based and COUNT the heading row, so the heading is row one
   * and the first body row is row two. Exposed as a member rather than written into the
   * template as a literal, so the heading index and {@link ariaRowCount} can never drift
   * apart.
   */
  protected readonly headerRowIndex = HEADER_ROW_COUNT;

  /** Track sizes for the table's `colgroup`. */
  protected readonly columnWidths = computed<readonly DataTableColumnWidth[]>(() =>
    this.columnsSignal().map((column) => ({
      key: column.key,
      width: resolveWidth(column.width),
    })),
  );

  /**
   * The heading cells, fully derived.
   *
   * Recomputes when the columns or either sort input changes - and only then.
   */
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
        align: column.headerAlign ?? DEFAULT_ALIGN,
      };
    });
  });

  /**
   * The body rows with every cell projected.
   *
   * This is where each {@link DataTableColumn.value} formatter is invoked, exactly
   * once per row per redraw. It depends on the columns and the rows ONLY: selection and
   * the sort inputs are deliberately not read here, so neither selecting a row nor
   * rebinding a sort re-runs a single formatter.
   */
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
   * Asks the feature to reorder by a column.
   *
   * Toggles only on the column that already carries the active sort; moving to a
   * different column starts ascending, which is the conventional and least surprising
   * first result. Refused while a request is in flight, so a reader cannot queue a
   * second ordering behind the first and end up looking at the one they abandoned.
   *
   * @param cell The heading that was activated.
   */
  protected activateSort(cell: DataTableHeaderCell<TRow>): void {
    if (cell.sortable === false || this.loadingSignal() === true) {
      return;
    }

    const direction = cell.sorted ? flipDirection(this.sortDirSignal()) : ASCENDING;
    this.sortChange.emit({ key: cell.key, direction });
  }

  /**
   * Selects a row and reports it.
   *
   * @param row The row that was activated.
   */
  protected activateRow(row: TRow): void {
    this.selectedRowSignal.set(row);
    this.rowSelect.emit(row);
  }

  /**
   * Activates a row from the keyboard.
   *
   * Both activation keys are honoured, and the space bar's default page scroll is
   * suppressed so activating a row does not also jump the viewport. Every other key is
   * left alone, so type-ahead and caret navigation still work.
   *
   * @param row The row the key was pressed on.
   * @param event The keyboard event.
   */
  protected activateRowFromKeyboard(row: TRow, event: KeyboardEvent): void {
    if (event.key !== ENTER_KEY && event.key !== SPACE_KEY) {
      return;
    }

    event.preventDefault();
    this.activateRow(row);
  }

  /**
   * Stops an interaction inside an `actions` cell from also selecting the row.
   *
   * THE HAZARD THIS EXISTS FOR. Row commands are projected content, so they are
   * interactive elements sitting INSIDE an activatable row. Without this, clicking
   * Edit or Delete would bubble to the row handler and fire `rowSelect` as well, so
   * every command would silently double as a selection - and on a delete command that
   * is the worst possible pairing.
   *
   * THE TRADE-OFF, stated rather than hidden. Nesting interactive content inside an
   * activatable row is not ideal in the abstract; the alternative is to forbid row
   * activation whenever any command exists, which would remove a working affordance
   * from every grid because one grid has commands. Suppressing propagation at the
   * boundary is the narrower fix: it changes nothing about the commands themselves,
   * each of which keeps its own accessible name, its own focus behaviour and its own
   * native keyboard activation. Only the bubbling to the row is cut, and only from
   * this one cell.
   *
   * Both event families are stopped, because a command activated by keyboard raises a
   * key event that would bubble just as a click does.
   *
   * @param event The click or key event raised inside the actions cell.
   */
  protected blockRowActivation(event: Event): void {
    event.stopPropagation();
  }
}


/**
 * Normalises a column's declared track width.
 *
 * Declared as a function rather than assigned to a constant so it is hoisted, and can
 * therefore be read by the field initialisers above without depending on the order of
 * declarations in this module.
 *
 * @param width The declared width, if any.
 * @returns The trimmed width, or `null` when absent or blank.
 */
function resolveWidth(width: string | undefined): string | null {
  if (typeof width !== 'string') {
    return null;
  }

  const trimmed = width.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Resolves the `aria-sort` value for a heading.
 *
 * Derived from the two sort inputs alone, which is what keeps the announced order and
 * the rendered order from ever disagreeing.
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
 * Returns the opposite ordering direction.
 *
 * @param direction The current direction.
 * @returns The direction to ask for next.
 */
function flipDirection(direction: SortDirection): SortDirection {
  return direction === ASCENDING ? DESCENDING : ASCENDING;
}

/**
 * Resolves what a column puts in its body cells.
 *
 * An explicit declaration always wins; otherwise the presence of a cell template
 * decides. `actions` is never inferred - see {@link DataTableColumn.kind}.
 *
 * @param column The column being projected.
 * @returns The resolved kind.
 */
function resolveKind<TRow>(column: DataTableColumn<TRow>): DataTableColumnKind {
  const declared = column.kind;
  if (declared !== undefined) {
    return declared;
  }

  return column.cellTemplate === undefined ? 'text' : 'template';
}

/**
 * Converts a cell value to text that is safe to interpolate.
 *
 * The conversions are deliberate and narrow:
 *
 * - an absent value yields the empty string, never the WORDS `null` or `undefined`.
 *   The empty string is also the legacy convention: the sentinel module defines its
 *   null string as the empty string rather than as a null reference, so an absent
 *   string and a blank one were already indistinguishable upstream.
 * - a string passes through untouched, whitespace included, so a value that was
 *   deliberately padded arrives as it was sent.
 * - a number is written out only when finite. A non-finite number yields the empty
 *   string rather than printing an error token into a data cell.
 * - a large integer is written out.
 * - EVERY other shape yields the empty string. A boolean, a date object or a nested
 *   object has no single correct text form, and guessing one would be a rule this
 *   component cannot get right for every caller: descending into an object would
 *   render a stringified object, and inventing a yes-or-no vocabulary here would
 *   create a second source of truth for wording that a display pipe already owns.
 *   Such a column states a formatter or a cell template instead.
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
 * Reads a column's text for one row.
 *
 * A formatter takes precedence over a bound member, so a column may declare both and
 * have the formatter win. The formatter's result is normalised as well as the member's,
 * because a caller can return a non-string at run time whatever the declared type says.
 *
 * @param column The column being projected.
 * @param row The row being projected.
 * @returns The cell's text.
 */
function cellText<TRow>(column: DataTableColumn<TRow>, row: TRow): string {
  const format = column.value;
  if (format !== undefined) {
    return toDisplayText(format(row));
  }

  const field = column.field;
  if (field === undefined) {
    return '';
  }

  // Bracket access, not member access: the row is widened to an index signature to
  // read a member chosen at run time, and `noPropertyAccessFromIndexSignature`
  // mandates the bracket form for exactly that case.
  return toDisplayText((row as Record<string, unknown>)[field]);
}

/**
 * Projects one body cell.
 *
 * Called once per column per row inside the row projection, which is the ONLY place a
 * formatter runs. A template context is built only for a cell that will actually render
 * one, so a text column allocates nothing extra.
 *
 * @param column The column being projected.
 * @param row The row being projected.
 * @param rowIndex Zero-based position of the row within the current page.
 * @returns The projected cell.
 */
function projectCell<TRow>(
  column: DataTableColumn<TRow>,
  row: TRow,
  rowIndex: number,
): DataTableBodyCell<TRow> {
  const kind = resolveKind(column);
  const declaredTemplate = column.cellTemplate ?? null;

  // Both non-text kinds render the caller's template: `template` for rich or editable
  // content, and `actions` for a column of row commands, which is a template too - it
  // must be, because a command is per-row. Only its propagation handling differs.
  // Gating on `kind === 'template'` alone would silently discard every actions column.
  const rendersTemplate = kind !== 'text' && declaredTemplate !== null;

  return {
    key: column.key,
    kind,
    text: kind === 'text' ? cellText(column, row) : '',
    template: rendersTemplate ? declaredTemplate : null,
    context: rendersTemplate ? { $implicit: row, row, column, rowIndex } : null,
    align: column.bodyAlign ?? DEFAULT_ALIGN,
  };
}

// ---------------------------------------------------------------------------------
// MIGRATION ledger
//
// Every deliberate divergence from the eight legacy grids, with the measured source
// evidence for each. Recorded inline here as well as in the prose above so that the
// full inventory is auditable from one place. The repository-root migration notes are
// authored separately and are not edited from here.
// ---------------------------------------------------------------------------------

// MIGRATION: the column KEY is separate from the display LABEL, because a label-keyed
// model is impossible against this codebase. `Website/admin/Security/roles.ascx`
// declares HeaderText "Every" at BOTH L45 and L58 (over BillingPeriod and TrialPeriod)
// and HeaderText "Period" at BOTH L50 and L63 (over BillingFrequency and
// TrialFrequency) inside ONE grid, so keying on the label would collapse each pair and
// drop a column with no error. Three further key-not-label proofs:
// `Website/admin/Users/users.ascx` L40 binds UserName under "Username" (casing);
// `Website/admin/Security/securityroles.ascx` L76 binds RoleName under "SecurityRole"
// (wholly different - note this column is at L76, not the L27 sometimes cited, which is
// a dropdown list); `Website/admin/Portal/portalalias.ascx` L14 binds HTTPAlias under
// "HTTP Alias" (a space). The legacy design agreed: header text was a localised
// resource keyed BY column identity - `PortalId.Header`, `Title.Header`,
// `HostingFee.Header` in
// `Website/admin/Portal/App_LocalResources/Portals.ascx.resx`.

// MIGRATION: header alignment and body alignment are INDEPENDENT descriptor members,
// never derived from one another. At column level `Website/admin/Portal/portals.ascx`
// defaults its body to Center (L16) under a Center heading (L15), then overrides BOTH
// sides on three columns - PortalId L24 ItemStyle plus L25 HeaderStyle, Title L31
// HeaderStyle then L32 ItemStyle in the OPPOSITE order, Portal Aliases L38 and L39 -
// while L44, L45, L46 and L47 set only a body vertical alignment and inherit Center,
// and L49 overrides the heading STYLE alone, giving three heading treatments in one
// grid. At GRID level the two disagree in three of the eight grids: roles.ascx L26
// Center heading against L27 Left body, users.ascx L24 against L25, and
// ProfileDefinitions.ascx L9 against L10. A single grid-wide alignment would misrender
// most of them.

// MIGRATION: there is deliberately NO rowAction output; row commands are
// content-projected. Two legacy grids make a command CONDITIONAL PER ROW, which no
// single output could express. `Website/admin/Portal/portalalias.ascx` L8 binds
// Visible to IsNotCurrent(PortalAliasID) - the alias you are currently browsing
// through cannot be edited. `Website/admin/Users/MemberServices.ascx` L33-L40 binds
// BOTH text= (L34) AND CommandName= (L35) to the same per-row
// ServiceText(Subscribed, ExpiryDate) expression, so the command's LABEL and its very
// IDENTITY are data-derived (Subscribe, Unsubscribe or Renew), and L39 adds a per-row
// visibility condition on top. Command counts also vary per grid: portals 2
// (portals.ascx L21-L22), users 3 (users.ascx L32-L34), roles 2 with no delete
// (roles.ascx L34-L35), profile properties 4 (ProfileDefinitions.ascx L17-L20).

// MIGRATION: the pager is a SIBLING placed by the feature, never rendered by this
// component and never inside a table footer. `Website/admin/Portal/portals.ascx` closes
// the grid at L56 and only then emits L57 <br><br> and L58 dnn:pagingcontrol;
// `Website/admin/Users/users.ascx` does the same at L81-L83. Measured: dnn:pagingcontrol
// appears in exactly TWO files across all five in-scope admin folders, so only 2 of the
// 8 grids were ever paged, while roles.ascx L32 and ProfileDefinitions.ascx L15 declare
// a DataGrid_Pager style and render no pager at all - and DataGrid_Pager is defined in
// ZERO stylesheets repository-wide.

// MIGRATION: fixed pixel widths are replaced by intrinsic measures or spacing tokens,
// applied through a colgroup so no cell rule carries a size. Every measured legacy
// width literal: `Website/admin/Users/ProfileDefinitions.ascx` L21, L22, L30 and L31
// Width="100px" plus L24 <ItemStyle Width="100px">;
// `Website/admin/Portal/portalalias.ascx` L6 <ItemStyle Width="15px"> and L2
// Width="500" (unitless); `Website/admin/Tabs/managetabs.ascx` L151 Width="150"
// (unitless).

// MIGRATION: action-column heading labels are NORMALISED - every column carries a
// label and headerHidden decides whether it is painted. Legacy practice was
// inconsistent: only `Website/admin/Users/ProfileDefinitions.ascx` labelled its command
// columns, as "Edit" (L17), "Del" (L18), "Dn" (L19) and "Up" (L20); portals.ascx,
// roles.ascx and users.ascx supplied no heading text for theirs; and
// `Website/admin/Tabs/managetabs.ascx` L140 suppressed its heading row entirely with
// ShowHeader="False".

// MIGRATION: the users-online column is DROPPED, a documented functional reduction.
// `Website/admin/Users/users.ascx` L35-L39 is an asp:templatecolumn with NO HeaderText
// at all, containing an unconditional, alternative-text-free image at L37
// (~/images/userOnline.gif). Users-online is out of scope for this migration and no
// endpoint exists to feed it, so reproducing the column would render a permanent,
// meaningless indicator.

// MIGRATION: the Option Strict asymmetry is made EXPLICIT. `Website/release.config`
// L125 compiles the administration pages with strict="false" while the class library
// builds with Option Strict on, so the legacy markup contains implicit coercions that
// this component's strict typing forbids. The measured ones:
// `Website/admin/Portal/portals.ascx` L41 calls Convert.toInt32 with a LOWER-CASE t,
// which only resolves because the pages are case-insensitively late-bound;
// `Website/admin/Security/roles.ascx` L68, L69, L74 and L75 compare
// DataBinder.Eval(...) against the string literals "true" and "false" to pick between
// a checked and an unchecked image - that works only because the late-bound Object =
// String comparison converts the string to a Boolean, whereas a genuine binary string
// comparison of "True" = "true" is False and would hide BOTH images, blanking the
// Public and Auto columns; `Website/admin/Tabs/managetabs.ascx` L152 writes
// Databinder.eval(Container.Dataitem, ...) with three separate casing deviations.
// Here every conversion is explicit and total: toDisplayText enumerates the shapes it
// accepts and yields the empty string for the rest, so no value is ever coerced
// silently.

// MIGRATION: row identity is the row's OBJECT REFERENCE, never an identifier member,
// because the legacy sentinels collide with real keys.
// `Library/Components/Shared/Null.vb` L41-L45 defines the integer null as -1 and
// L71-L75 defines the null string as the EMPTY STRING rather than a null reference,
// with L46-L50 (byte 255), L66-L70 (date minimum) and L76-L80 (boolean False)
// completing the table. Meanwhile
// `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` seeds
// PortalID at IDENTITY(-1,1) (L77) and RoleID, TabID and ModuleID at IDENTITY(0,1)
// (L115, L140, L221), so 0 is a legitimate key and -1 is BOTH a legitimate PortalID and
// the "absent" marker. `Website/admin/Tabs/managetabs.ascx` L166 compares a ModuleID
// against -1 in markup on a column seeded at 0. Taking the reference reads no number,
// so no truthiness or sign test exists anywhere in this component to be wrong.

// MIGRATION: sortDir uses the server's own member names, imported from the paging
// contract rather than restated. The abbreviated spelling is rejected at the wire:
// sortDir=asc is answered with 400 Bad Request. A locally declared 'asc' | 'desc' union
// compiles cleanly and fails every sort at run time.

// MIGRATION: the accessible name arrives by CONTENT PROJECTION, not by a caption input.
// The public surface is closed at five inputs, so a caption input is not available, and
// the migration plan nonetheless mandates a caption element on every table. Projection
// satisfies both: a feature writes an element carrying the dataTableCaption attribute and
// the component renders it inside a real caption, announced and visually hidden.

// MIGRATION: NgTemplateOutlet is imported as a THIRD entry alongside the two composed
// siblings, and that is a deliberate, reported deviation from a strictly two-entry import
// list. It is NOT the umbrella common-directives module - which appears nowhere in this
// workspace - but the single
// tree-shakeable standalone directive that renders a caller's TemplateRef, and Angular
// offers no other mechanism for rendering one from a template. Omitting it would leave
// cellTemplate and the entire actions column kind as members that can never render, which
// the zero-placeholder standard forbids outright, and would strand the legacy columns they
// exist to carry: the template columns of roles.ascx L40-L77, portals.ascx L23-L54,
// users.ascx L44-L79 and managetabs.ascx L144-L170, the two inline-editable
// dnn:checkboxcolumn cells of ProfileDefinitions.ascx L32-L33, and every
// dnn:imagecommandcolumn command in all eight grids. Functional parity carries rule-force
// under the Minimal Change Clause; an import count whose stated purpose is avoiding dead
// weight does not, and this import is not dead weight.

// MIGRATION: the grid degrades by SCROLLING, and its headings degrade by WRAPPING, neither
// of which the legacy grids did - they simply crushed. Two measured trade-offs are recorded
// here rather than left to be rediscovered. First, the table carries a width floor composed
// from the spacing scale so the scroll container has something to scroll; the intrinsic
// `min-content` keyword was tried first and is INERT under a fixed table layout with
// percentage tracks, where it resolves to approximately zero. Second, headings wrap instead
// of being held to one line: held to one line they do not fit, they SPILL, and four pairs of
// headings were measured physically overlapping at a 480px viewport, the worst by 34.89px.
// The cost of wrapping is that a single-word heading in a marginally narrow track breaks
// mid-word - measured as an orphaned final letter on a 1.30px shortfall - which is cosmetic,
// leaves the accessible name intact, and is strictly preferable to two illegible headings.

// MIGRATION: virtualisation is delivered by CSS render-virtualisation - graded option
// ONE - with zero JavaScript, zero new dependency and no change to the public surface.
// No scrolling package exists in the pinned twenty-one and none was added, so no
// viewport component was available. Every row remains in the DOM, hence in the
// accessibility tree, find-in-page and the tab order. REPORTED GAP: the design-token
// set declares no row-height or intrinsic-size token, so the placeholder size is
// composed from the existing base line-height, base type size and spacing tokens rather
// than being hardcoded to a pixel literal. Because no rows are removed this is not
// windowing, so aria-rowcount and aria-rowindex are not strictly needed; they are
// published anyway because they are accurate, valid on a native table without an
// explicit role, visually free, and they stop a future switch to real windowing from
// silently misreporting the table's size.

