/**
 * The shared, presentational record grid.
 *
 * This is the single replacement for the eight `asp:DataGrid` instances across the five in-scope
 * administration trees. It renders a real `table` so the row-and-column relationships assistive technology
 * reads a cell by survive; a grid assembled from `div` elements discards them, and no amount of ARIA restores
 * them as faithfully as the native element supplies them.
 *
 * It is presentational and nothing else. It performs no sorting, no paging and no filtering, and it reaches no
 * data source of any kind: no service, no HTTP client and no store. `columns`, `rows`, `sortBy`, `sortDir` and
 * `loading` in; `sortChange` and `rowSelect` out. Nothing is injected into it, and every further affordance is
 * delivered by content projection or by a {@link DataTableTemplateColumn.cellTemplate} rather than by widening
 * that surface. A consumer needs exactly three names - {@link DataTableColumn}, {@link DataTableCellContext}
 * and {@link DataTableSortChange} - and only those three are exported.
 *
 * A plain `@Input()` field is not a reactive source, so a `computed()` reading one would never recompute and
 * this component would be silently inert under `OnPush` — the defect would show as a grid that renders its
 * first page and then never updates. Each input is therefore a public accessor pair whose setter writes a
 * private `signal()`, and every derivation the template reads is a `computed()` over those signals.
 *
 * A function invoked from a template re-runs on every change-detection pass. With one call per cell across a
 * page of rows that is the single most likely performance defect in a grid, and it defeats the point of
 * `OnPush`. The template therefore reads pre-projected view models — {@link DataTableHeaderCell}, {@link
 * DataTableBodyRow} and {@link DataTableBodyCell} — in which every {@link DataTableFormattedColumn.value}
 * formatter has already been invoked exactly once. The only methods the template calls are event handlers,
 * which run on interaction rather than on redraw.
 *
 * MIGRATION: sorting is EMITTED, never performed. The legacy grids posted the whole page back and re-queried,
 * so ordering belongs to the server here too: a page of ten rows cannot be ordered meaningfully on the client,
 * because reordering the visible ten produces an order that is right within the page and wrong across the
 * match set. {@link DataTableComponent.rows} is `readonly` and is never sorted, reversed or spliced in place.
 *
 * MIGRATION: `sortDir` carries the server's own member names, `'Ascending'` and `'Descending'`, imported from
 * the paging contract rather than restated here. An abbreviated spelling is not a style variant: the value is
 * bound from the query string and the binder answers `sortDir=asc` with `400 Bad Request`, so a locally
 * declared `'asc' | 'desc'` union would compile cleanly and fail every sort at run time.
 *
 * @typeParam TRow The row contract carried on the current page - always a transfer contract off the wire,
 * never a persisted entity. Deliberately unconstrained so a grid of any row shape is expressible. {@link
 * DataTableTextColumn.field} is meaningful only for object-shaped rows, which is every row this application
 * renders.
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

import type { OnInit } from '@angular/core';

// The ONE standalone directive imported here, and the only import that is not a composed
// sibling. It is emphatically NOT the umbrella common-directives module, which is
// imported nowhere in this workspace; it is the single tree-shakeable directive by which a
// caller's
// {@link DataTableTemplateColumn.cellTemplate} is rendered, and there is no other mechanism in
// Angular for rendering a `TemplateRef` from a template. Without it `cellTemplate` and
// the whole `actions` column kind would be declared members that could never render -
// stubs - and the legacy template columns and inline-editable checkbox columns they exist
// to carry would have no target at all. It is imported individually rather
// than as part of a module precisely so nothing unused comes with it.
import { NgTemplateOutlet } from '@angular/common';

import type { SortDirection } from '../../../core/models/paged-result.model';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../loading-spinner/loading-spinner.component';

/**
 * Inline alignment of a column's heading or of its body cells.
 *
 * Logical rather than physical - `start` and `end` follow the writing direction, so a right-to-left reader
 * gets the correct edge without a second rule.
 *
 * A string-literal union rather than an enumeration: it needs no run-time representation, and
 * `isolatedModules` is enabled, which rules out the `const` form of an enumeration that would otherwise
 * avoid the emit.
 */
type DataTableAlign = 'start' | 'center' | 'end';

/**
 * What a column puts in its body cells.
 *
 *  - `text` - bound or formatted text, covering the legacy `dnn:textcolumn` and `asp:BoundColumn`, including
 *    the one that carried `DataFormatString="{0:0.00}"`.
 *  - `template` - caller-supplied cell content, covering `asp:TemplateColumn` and the two inline-editable
 *    `dnn:checkboxcolumn` cells that posted back on change.
 *  - `actions` - projected row controls, covering `dnn:imagecommandcolumn`. Declared explicitly rather than
 *    inferred, because it is a statement about semantics and not about where the content came from: an
 *    actions cell suppresses row activation so that pressing Edit never doubles as selecting the row.
 */
type DataTableColumnKind = 'text' | 'template' | 'actions';

/**
 * The `aria-sort` states a sortable heading can report.
 */
type DataTableAriaSort = 'ascending' | 'descending' | 'none';

/**
 * A track size a column may declare, closed at the four forms that are BOTH valid CSS for `inline-size` on a
 * `col` element AND permitted by the design system.
 *
 * Two independent defects follow from accepting arbitrary text, and neither produces an error anywhere:
 *
 * - A PIXEL LITERAL is forbidden by the token vocabulary and would be applied faithfully. Every legacy width
 *   was a fixed literal — five identical ones in the profile-property listing alone, a hairline command
 *   column, and two unitless numbers — and each is replaced by an intrinsic measure or a token so the grid
 *   reflows and honours a reader's font size. A type that admits `'100px'` re-opens every one of them.
 * - A GRID TRACK KEYWORD is silently DISCARDED. `fr` and `minmax()` are grid track sizing syntax and are not
 *   valid values for `inline-size`, so the browser drops the declaration and the column takes its share as
 *   though nothing had been declared. Documenting either as permitted would be worse than saying nothing: a
 *   caller would follow the guidance, see no error, and get no width.
 *
 * The four admitted forms:
 *
 * - `'42%'` — a percentage of the table's inline size. This is the form six of the eight legacy grids used
 *   at grid level, and it is what a proportional column wants.
 * - `'min-content'` / `'max-content'` — intrinsic measures, and the correct choice for a column of row
 *   commands, which must never be given a track narrower than the controls it carries.
 * - `'var(--…)'` — a design-token reference, for a column sized from the spacing scale. The token itself
 *   resolves to a real length, so the grammar stays valid.
 *
 * `fit-content()`, `calc()`, a bare length and a keyword outside this list are all deliberately absent. Each
 * would either bypass the token vocabulary or need its own validation for a case no legacy grid presents.
 */
export type DataTableWidth =
  | `${number}%`
  | 'min-content'
  | 'max-content'
  | `var(--${string})`;

/**
 * The context a {@link DataTableTemplateColumn.cellTemplate} is rendered against.
 *
 * `$implicit` is the row, so a caller may write `let-row` and receive it without naming a member. The row is
 * repeated under `row` for callers that prefer to be explicit, and the column travels with it so one
 * template can serve several columns.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableCellContext<TRow> {
  /**
   * The row, available as `let-row` with no member name.
   */
  readonly $implicit: TRow;

  /**
   * The row again, for `let-row="row"` callers that prefer to be explicit.
   */
  readonly row: TRow;

  /**
   * The column being rendered, so one template can serve several columns.
   */
  readonly column: DataTableColumn<TRow>;

  /**
   * Zero-based position of the row within the CURRENT PAGE, not within the match set.
   *
   * Suitable for striping and for `let-i="rowIndex"`; never suitable as an identifier. The page offset is
   * the feature's to hold, not this component's.
   */
  readonly rowIndex: number;
}

/**
 * One column of a {@link DataTableComponent}.
 *
 * Every member is `readonly`: a column set describes a layout the caller has already decided, and a component
 * that could rewrite it would be describing something the caller never asked for.
 *
 * The column is keyed by a stable identity rather than by its label, because a label-keyed model is provably
 * impossible against this codebase. The role list declares twelve columns in one grid in which the heading
 * text `"Every"` appears TWICE - over the billing period and the trial period - and `"Period"` appears twice
 * as well; keying on the label would collapse each pair into one column and silently drop the other, with no
 * error anywhere. The legacy application already drew the distinction itself, looking header text up as a
 * LOCALISED RESOURCE keyed by the column's own stable identity: identity was structure and the label was data.
 *
 * The descriptor must not, however, INSIST on a bound member, because derived columns have none at all - one
 * composes a portal's alias list from its identifier, another formats an expiry date, one composes a postal
 * address from six separate profile members. That is why {@link DataTableColumnCommon.key} is a plain `string`
 * and not `keyof TRow`.
 *
 * The type is a discriminated union rather than one interface with independently optional members, because
 * that shape would make every INVALID configuration representable and none of them produces an error: a
 * column with a kind and no payload renders a blank cell on every row, a text column carrying a cell template
 * silently ignores it, and a template column carrying a bound member silently ignores that instead. The union
 * closes all of it at compile time - a text column must state EXACTLY ONE text source, and a template or
 * actions column must state a cell template and may state neither. A sortable heading that hides its label is
 * unrepresentable too, for the reason given on {@link DataTableColumnHeading}. The invariants a type cannot
 * express alone - uniqueness of the key across a set, a non-blank key, and a width composed at run time - are
 * rejected when the set is bound, which is documented on {@link DataTableComponent.columns}.
 *
 * @typeParam TRow The row contract this column reads.
 */
export type DataTableColumn<TRow> = DataTableColumnCommon &
  DataTableColumnHeading &
  DataTableColumnBody<TRow>;

/**
 * The members every column carries, whatever it puts in its body cells.
 *
 * Never a complete column on its own: a column is always the full intersection declared by {@link
 * DataTableColumn}, and this fragment exists so that the shared members are written once rather than
 * repeated in each arm of the union. It is exported only so that a consumer and a documentation reader can
 * reach these members by name.
 */
export interface DataTableColumnCommon {
  /**
   * Stable identity of the column, unique within one column set.
   *
   * This is the `track` expression of the heading and cell loops, and the value reported as {@link
   * DataTableSortChange.key}, so for a sortable column it must be the sort name the collection endpoint
   * accepts. Never the label - see the note on this interface for why that is not a preference.
   *
   * Uniqueness is enforced when the set is bound, because no type can compare two members of an array: a
   * duplicate key would make two headings and two cells share one `track` value, and the framework would
   * reuse one column's DOM for the other with no error anywhere.
   */
  readonly key: string;

  /**
   * Visible heading text.
   *
   * Free to duplicate another column's label, and in the role list it genuinely does. Suppress it with
   * {@link DataTableColumnHeading.headerHidden} without losing it: the heading stays in the accessibility
   * tree either way, so a cell is still announced with its column name.
   */
  readonly label: string;

  /**
   * Inline alignment of the HEADING. Defaults to `start`.
   *
   * Independent of {@link DataTableColumnCommon.bodyAlign} by necessity - see that member.
   */
  readonly headerAlign?: DataTableAlign;

  /**
   * Inline alignment of the BODY cells. Defaults to `start`.
   *
   * Because the legacy markup separates them, at two levels at once, and deriving one from the other would
   * misrender most of the eight grids.
   *
   * At the GRID level the two disagree in three of the eight: the role list, the account list and the
   * profile-property list each centre the heading row and start-align the body. At the COLUMN level the
   * portal list defaults its body to centre and then overrides BOTH sides on three columns - the identifier,
   * the title and the alias list each set a body alignment AND a heading alignment, and the title states
   * them in the opposite order to the identifier, so an order-sensitive reading of the markup would miss it.
   * Four further columns in that same grid set only a body vertical alignment and inherit the centre, and a
   * ninth column overrides the heading STYLE alone, making three distinct heading treatments inside a single
   * grid.
   *
   * So: two members, no derivation, and no single grid-wide alignment.
   *
   * Vertical alignment is deliberately NOT a member. The legacy body cells set it to the top essentially
   * uniformly, so it is normalised once in the stylesheet rather than made configurable. The bare-element
   * rule that would otherwise apply a baseline alignment to headings never took effect at run time, because
   * every one of the eight grids either sets a heading class or suppresses its heading row outright.
   */
  readonly bodyAlign?: DataTableAlign;

  /**
   * Track width of the column, applied through a `col` element in the table's `colgroup` so that no cell
   * rule carries a size.
   *
   * Closed at the four forms {@link DataTableWidth} admits, each of which is valid CSS for `inline-size` on
   * a `col` element. A pixel literal is not expressible and neither is a grid track keyword; the reasoning
   * for both, and what replaced the legacy fixed widths, is on that type.
   *
   * Omission means the column takes its share automatically. A value produced at run time rather than
   * written as a literal is validated when the set is bound, so a malformed one is reported rather than
   * silently discarded by the browser.
   *
   * Sizing the columns here rather than from cell content is also what makes the render-virtualisation
   * strategy safe - see {@link DataTableComponent}.
   *
   * One obligation on the caller, because the component cannot discharge it. An `actions` column must be
   * given `min-content`, `max-content`, or omitted so it sizes itself; it must NOT be given a track narrower
   * than the commands it carries. The commands wrap onto further lines when the track is tight, which is the
   * component's defence, but wrapping cannot shrink a control below its own minimum width: measured with two
   * text commands in a 2.5rem track, the first still overhung its cell by 16.97px and painted over the value
   * in the next column, because a table cell is `overflow: visible` and clipping it would hide a control
   * rather than reveal a layout problem. A content-sized track cannot express that mistake at all, which is
   * why it is the documented contract rather than a suggestion.
   */
  readonly width?: DataTableWidth;
}

/**
 * The heading policy of a column: whether it offers sorting, and whether its label is painted.
 *
 * A UNION rather than two independent optional members, because the two combinations are not independent: a
 * sortable heading whose label is hidden renders an EMPTY BUTTON - the label is clipped out of the painted
 * output and the direction glyph appears only once the column is the active sort, so a reader meets a
 * focusable control with nothing visible in it and no way to guess what activating it would order by. The
 * union makes that combination unrepresentable while leaving every legitimate one available - a sortable
 * column with a visible label, a hidden label on a column that offers no sorting, or neither.
 */
export type DataTableColumnHeading =
  | {
      /**
       * Whether the heading offers sorting. Absent or `false` means it does not.
       *
       * Opt-in per column because the endpoint decides which names it accepts and answers an unrecognised
       * one with a field-level `400`. Offering a control that produces a rejected request is worse than
       * offering none.
       */
      readonly sortable?: false;

      /**
       * Whether to hide the heading text visually while keeping it announced.
       *
       * For columns whose heading would be noise - a column of row commands, or an indicator with no
       * meaningful name. The text stays in the accessibility tree, so a cell is still announced with its
       * column name and nothing is lost.
       *
       * Legacy practice here was inconsistent, which is why this is normalised rather than reproduced: of
       * the eight grids, exactly one labelled its command columns, as `Edit`, `Del`, `Dn` and `Up`; the
       * portal, role and account lists supplied no heading text for theirs at all; and the page-module grid
       * suppressed its entire heading row. Every column here therefore carries a label and this member
       * decides whether it is painted.
       */
      readonly headerHidden?: boolean;
    }
  | {
      /**
       * Whether the heading offers sorting. See the companion arm of this union.
       */
      readonly sortable: true;

      /**
       * Not available on a sortable column: hiding the label of a sortable heading leaves an empty,
       * unlabelled button. See {@link DataTableColumnHeading}.
       */
      readonly headerHidden?: false;
    };

/**
 * What a column puts in its body cells, and the payload that kind requires.
 *
 * Discriminated on {@link DataTableColumnKind}, with `text` as the default arm so that the overwhelmingly
 * common bound-text column stays terse. Every arm names the members the other arms forbid, so a payload
 * declared under the wrong kind is a compile error rather than a value the projection quietly ignores.
 *
 * @typeParam TRow The row contract this column reads.
 */
export type DataTableColumnBody<TRow> =
  | DataTableTextColumn<TRow>
  | DataTableFormattedColumn<TRow>
  | DataTableTemplateColumn<TRow>
  | DataTableActionsColumn<TRow>;

/**
 * A column rendering one member of the row as plain text - the legacy `dnn:textcolumn` and `asp:BoundColumn`.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableTextColumn<TRow> {
  /**
   * The default kind, so it may be omitted entirely.
   */
  readonly kind?: 'text';

  /**
   * Row member to render as plain text.
   *
   * Typed as a key of the row, so a mistyped member name is a compile error rather than a blank column. For
   * TEXT-SHAPED values only - a string, a number or a large integer. A boolean, a date object or a nested
   * object must go through {@link DataTableFormattedColumn.value} or a template column instead, and that is
   * not an arbitrary restriction: no legacy grid ever bound a boolean as text either. All four legacy
   * boolean columns were template columns - two rendered a checked or unchecked image, two rendered a
   * checkbox that posted back on change.
   */
  readonly field: keyof TRow & string;

  /**
   * Not available on a bound column: state a formatter or a member, never both.
   */
  readonly value?: never;

  /**
   * Not available on a text column: a text column renders no template.
   */
  readonly cellTemplate?: never;
}

/**
 * A column whose text is computed from the row - the legacy formatted and derived columns.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableFormattedColumn<TRow> {
  /**
   * The default kind, so it may be omitted entirely.
   */
  readonly kind?: 'text';

  /**
   * Not available on a formatted column: state a formatter or a member, never both.
   */
  readonly field?: never;

  /**
   * PURE formatter producing the cell's text.
   *
   * Covers the formatted and derived columns: prices, periods, expiry dates, an alias list composed from an
   * identifier, a postal address composed from six profile members. Invoked exactly once per row per redraw,
   * inside the projection, and never from the template.
   *
   * Must be pure and must not throw. It runs during a `computed()` evaluation, so a side effect here would
   * fire at an unpredictable point in change detection.
   *
   * Declaring both this and a bound member is a compile error, deliberately. Accepting both and resolving
   * the ambiguity with a precedence rule would mean a column that named the wrong member alongside a
   * formatter looked correct and rendered correctly - until the formatter was removed and the wrong member
   * surfaced. One text source per column removes the ambiguity instead of documenting a way through it.
   *
   * @param row The row being rendered.
   * @returns The text to display. Return the empty string for an absent value; never return `null` or
   *   `undefined`, and never the words `null` or `undefined`.
   */
  readonly value: (row: TRow) => string;

  /**
   * Not available on a text column: a text column renders no template.
   */
  readonly cellTemplate?: never;
}

/**
 * A column rendering caller-supplied content - the legacy `asp:TemplateColumn`, including the two
 * inline-editable checkbox cells that posted back on change.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableTemplateColumn<TRow> {
  /**
   * Declared explicitly: a template column is never inferred.
   */
  readonly kind: 'template';

  /**
   * Not available: a template column renders its template, never bound text.
   */
  readonly field?: never;

  /**
   * Not available: a template column renders its template, never formatted text.
   */
  readonly value?: never;

  /**
   * Caller-supplied cell content, for anything richer than text. REQUIRED, because a template column with no
   * template is a blank column on every row.
   *
   * Compiled in the CALLER's template context, not this component's, which is why this component imports no
   * directive and no pipe: a permission directive or a display pipe used inside the template belongs to the
   * feature that wrote it.
   */
  readonly cellTemplate: TemplateRef<DataTableCellContext<TRow>>;
}

/**
 * A column of projected row commands - the legacy `dnn:imagecommandcolumn`.
 *
 * Declared as its own kind rather than inferred, because it is a statement about semantics and not about
 * where the content came from: an actions cell suppresses row activation so that pressing Edit never doubles
 * as selecting the row.
 *
 * @typeParam TRow The row contract this column reads.
 */
export interface DataTableActionsColumn<TRow> {
  /**
   * Declared explicitly: no inference can supply this.
   */
  readonly kind: 'actions';

  /**
   * Not available: an actions column renders its template, never bound text.
   */
  readonly field?: never;

  /**
   * Not available: an actions column renders its template, never formatted text.
   */
  readonly value?: never;

  /**
   * The row commands, as a template the caller supplies. REQUIRED: a commands column with no commands is an
   * empty column, and a per-row command can only be expressed as a template - two legacy grids make a
   * command conditional per row, and one derives both a command's LABEL and its very identity from the row.
   */
  readonly cellTemplate: TemplateRef<DataTableCellContext<TRow>>;
}

/**
 * A reader's request to reorder the match set.
 *
 * Carries the direction as well as the key, so a consumer never has to remember what it last asked for in
 * order to interpret the next request.
 */
export interface DataTableSortChange {
  /**
   * The {@link DataTableColumnCommon.key} to order by, which is the endpoint's sort name.
   */
  readonly key: string;

  /**
   * The direction to order in, in the server's own spelling.
   */
  readonly direction: SortDirection;
}

/**
 * A heading cell, fully derived so the template evaluates no expression of its own.
 *
 * @typeParam TRow The row contract.
 */
interface DataTableHeaderCell<TRow> {
  /**
   * The column this heading describes, for callers that need the descriptor itself.
   */
  readonly column: DataTableColumn<TRow>;

  /**
   * {@link DataTableColumnCommon.key}, and the `track` expression of the heading loop.
   */
  readonly key: string;

  /**
   * {@link DataTableColumnCommon.label}.
   */
  readonly label: string;

  /**
   * Whether the label is painted, or announced only.
   */
  readonly labelVisible: boolean;

  /**
   * Whether this heading offers sorting.
   */
  readonly sortable: boolean;

  /**
   * Whether this heading carries the ACTIVE sort, per `sortBy` alone.
   */
  readonly sorted: boolean;

  /**
   * The `aria-sort` value, or `null` to omit the attribute.
   *
   * Omitted for a column that offers no sorting: `none` means "sortable but not currently sorted", which on
   * an unsortable column would be a false claim. A table in which every heading claims to be sorted conveys
   * nothing.
   */
  readonly ariaSort: DataTableAriaSort | null;

  /**
   * Resolved {@link DataTableColumnCommon.headerAlign}.
   */
  readonly align: DataTableAlign;
}

/**
 * One body cell, with its text already produced and its template context already built.
 *
 * @typeParam TRow The row contract.
 */
export interface DataTableBodyCell<TRow> {
  /**
   * {@link DataTableColumnCommon.key}, and the `track` expression of the cell loop.
   */
  readonly key: string;

  /**
   * Resolved {@link DataTableActionsColumn.kind}, deciding which branch the template takes.
   */
  readonly kind: DataTableColumnKind;

  /**
   * The cell's text for a `text` column, already formatted; the empty string otherwise.
   *
   * Never `null`, never `undefined` and never the WORDS `null` or `undefined`. An absent value renders as
   * the empty string, which is also the legacy convention: the sentinel module defines its null string as
   * the empty string rather than as a null reference.
   */
  readonly text: string;

  /**
   * The caller's cell template for a `template` column, `null` otherwise.
   */
  readonly template: TemplateRef<DataTableCellContext<TRow>> | null;

  /**
   * The context to render {@link template} against, `null` when there is none.
   */
  readonly context: DataTableCellContext<TRow> | null;

  /**
   * Resolved {@link DataTableColumnCommon.bodyAlign}.
   */
  readonly align: DataTableAlign;
}

/**
 * One body row, with every cell projected.
 *
 * @typeParam TRow The row contract.
 */
interface DataTableBodyRow<TRow> {
  /**
   * The row itself, and its own identity.
   *
   * This object reference is the `track` expression of the row loop and the value compared for selection.
   * See {@link DataTableComponent} for why identity is taken from the reference and never from a member.
   */
  readonly row: TRow;

  /**
   * Zero-based position within the current page. Never an identifier.
   */
  readonly rowIndex: number;

  /**
   * One-based position among ALL rows of the table, counting the heading row as the first. Bound to
   * `aria-rowindex`.
   */
  readonly ariaRowIndex: number;

  /**
   * The projected cells, in column order.
   */
  readonly cells: readonly DataTableBodyCell<TRow>[];
}

/**
 * A `col` entry sizing one track of the table.
 *
 * @see DataTableColumn.width
 */
export interface DataTableColumnWidth {
  /**
   * {@link DataTableColumnCommon.key}, and the `track` expression of the `colgroup` loop.
   */
  readonly key: string;

  /**
   * The resolved width, or `null` to let the column take its share automatically.
   */
  readonly width: string | null;
}

/**
 * The ascending member of the wire sort vocabulary.
 */
const ASCENDING: SortDirection = 'Ascending';

/**
 * The descending member of the wire sort vocabulary.
 */
const DESCENDING: SortDirection = 'Descending';

/**
 * Inline alignment applied when a column states none.
 *
 * Neutral on purpose. Visual continuity with the legacy portal is the FEATURE's to state per column, because
 * the eight legacy grids disagreed with each other: some centred their headings and start-aligned their
 * bodies, one centred both. A component-level guess would silently override whichever of them a feature was
 * reproducing.
 */
const DEFAULT_ALIGN: DataTableAlign = 'start';

/**
 * Rows contributed by the heading section, for `aria-rowcount` and `aria-rowindex`.
 *
 * The template renders exactly one heading row, and ARIA counts it: the heading is row one, so the first
 * body row is row two.
 */
const HEADER_ROW_COUNT = 1;

/**
 * Minimum `colspan` for the waiting and empty rows, so neither can span zero cells.
 */
const MINIMUM_COLUMN_SPAN = 1;

/**
 * Rows the body contributes while it is waiting or empty.
 *
 * The waiting and empty branches each render exactly ONE spanning row. It is counted, not ignored: a row
 * that exists in the table but not in `aria-rowcount` leaves a screen reader being told the table has fewer
 * rows than it will actually encounter.
 */
const MESSAGE_ROW_COUNT = 1;

/**
 * Activation key that needs no default suppression.
 */
const ENTER_KEY = 'Enter';

/**
 * Activation key whose default action scrolls the page and must be suppressed.
 */
const SPACE_KEY = ' ';


/**
 * The sortable, keyboard-operable record grid.
 *
 * Both the `track` expression of the row loop and the selection comparison use the row object itself. No
 * member is inspected, and that is the whole point.
 *
 * Reading an identifier member instead would be unsafe, and quietly so. The legacy identity seeds make BOTH
 * zero and minus one legitimate keys - the portal table seeds its identity at minus one while the role, page
 * and module tables seed theirs at zero - and minus one is SIMULTANEOUSLY the legacy integer null sentinel,
 * which the legacy markup compared against directly. Any test of the form `if (id)`, `id > 0` or `id ?? -1`
 * would mis-key the first role, page or module row, or discard a real portal, with no error at all: the DOM
 * would simply reuse the wrong node. Taking the reference is immune to it, and this component reads no
 * identifier member anywhere.
 *
 * The consequence, stated plainly: when the feature re-queries and replaces the array, every row is a new
 * object, so every row's DOM is discarded and rebuilt rather than patched. That is correct for a server-paged
 * grid, and within a page, while the array is untouched, rows are tracked stably and patched in place. A
 * second consequence is desirable - replacing the array clears a selection that pointed into the old one, so
 * the selected state can never outlive the row it described.
 *
 * There is no `selectedRow` input, so selection is held internally, exposed programmatically through
 * `aria-selected` rather than by styling alone, and reported through `rowSelect`. That one attribute is the
 * whole announcement; the current-item state is deliberately not published alongside it, because it would
 * state one state twice in a vocabulary this component does not model separately. Selection is also kept OUT
 * of the row projection: were it a member of {@link DataTableBodyRow}, selecting a row would re-run every
 * {@link DataTableFormattedColumn.value} formatter on the page to recompute text that had not changed.
 *
 * Virtualisation is achieved in CSS rather than with a scrolling viewport, because the pinned dependency set
 * contains no scrolling package and none may be added. The stylesheet marks body rows so the engine may skip
 * the layout and paint of rows that are off screen and supplies a placeholder size for the rows it skips.
 * That needs zero JavaScript and no change to the public surface, and every row stays in the DOM, so every
 * row stays in the accessibility tree, in find-in-page and in the tab order. Two facts make it work rather
 * than merely compile:
 *
 * - Skipping a row's layout is only safe when column widths do not depend on that row's content, or the
 *   columns would shift as rows entered and left the viewport. {@link DataTableColumnCommon.width} applied
 *   through the `colgroup`, with a fixed table layout, supplies exactly that guarantee: the width member and
 *   the virtualisation strategy are two halves of one design.
 * - The placeholder size is composed ENTIRELY from existing design tokens - the base line height, the base
 *   type size and a spacing step - because the token set declares no row-height token and a pixel literal is
 *   forbidden.
 *
 * Because every row remains rendered this is not windowing, so `aria-rowcount` and `aria-rowindex` are not
 * strictly needed. They are published anyway: they are accurate, they are valid on a native table without an
 * explicit role, and they mean a future switch to true windowing cannot silently start misreporting the row
 * count.
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

  private readonly sortBySignal = signal<string | undefined>(undefined);

  private readonly sortDirSignal = signal<SortDirection>(ASCENDING);

  private readonly loadingSignal = signal(false);

  private readonly selectedRowSignal = signal<TRow | null>(null);

  /**
   * Whether rows carry the selection affordance at all — settled once, at initialisation.
   *
   * ⚠ THIS EXISTS BECAUSE THE AFFORDANCE WAS UNCONDITIONAL AND SIX OF SEVEN CONSUMERS HAVE
   * NO USE FOR IT. Every row used to be given `tabindex="0"`, an `aria-selected` attribute,
   * a pointer cursor, a focus ring, hover and press feedback and both activation handlers,
   * whether or not anything was listening to {@link DataTableComponent.rowSelect}. The cost
   * of that on a non-selecting grid is not cosmetic:
   *
   *   * EVERY ROW BECAME A TAB STOP. A page of twenty accounts put twenty inert stops
   *     between the search control and the first row command, so reaching a command by
   *     keyboard meant pressing Tab past every row above it. That is the single largest
   *     keyboard-operability cost in the console.
   *   * EVERY ROW ANNOUNCED A SELECTION STATE IT DID NOT HAVE. `aria-selected="false"` on a
   *     row that can never be selected tells a screen-reader user there is a selection model
   *     here and that they are outside it — a claim the grid cannot substantiate.
   *   * EVERY ROW SHOWED A POINTER CURSOR AND HOVER FEEDBACK, promising that clicking a row
   *     does something. Clicking did nothing at all, six times out of seven.
   *
   * ## Why the emitter's own subscription state is the right signal
   *
   * The alternative was a sixth input — `selectable` — and the shared component's surface is
   * closed at five inputs and two outputs by the design-system contract, so widening it is
   * not available. It would also be redundant and therefore driftable: a consumer that bound
   * `(rowSelect)` and forgot `[selectable]` would get a dead grid, and one that bound
   * `[selectable]` without `(rowSelect)` would get the exact defect being removed. The
   * emitter's subscription state cannot disagree with itself — a consumer that listens is
   * selectable BY DEFINITION.
   *
   * `EventEmitter` extends RxJS `Subject`, whose `observed` member reports whether anything
   * is subscribed. Template output bindings are registered while the PARENT view is created,
   * which precedes this component's own initialisation hook, so the reading here is the
   * settled one.
   *
   * ⚠ READ ONCE, DELIBERATELY, AND NOT RE-DERIVED PER PASS. A grid whose rows became
   * focusable and unfocusable between passes would move the tab order under a reader's
   * fingers. Nothing in this application subscribes to an output after view creation, and a
   * consumer that needs to withdraw selection dynamically should stop acting on the event
   * rather than have the grid re-shape itself.
   */
  private readonly rowsSelectableSignal = signal(false);

  /**
   * Sets the columns to render, in order.
   *
   * Public because the strict input-access check rejects a non-public input at every consuming template; the
   * same applies to all five inputs. The write type admits absent values because a store's projection is
   * routinely nullable, and an absent set renders as no columns rather than throwing.
   *
   * {@link DataTableColumn} closes every per-column combination at compile time. Four invariants remain that a
   * type cannot carry ALONE, and all four are checked the moment a set is bound rather than later inside a
   * projection, so a defect is reported at the call site that caused it, before a single cell is rendered:
   *
   * - A DUPLICATE KEY. Comparing two members of an array is beyond a type. A duplicate makes two headings and
   *   two cells share one `track` value and the framework reuses one column's DOM for the other, with no error
   *   anywhere - the worst class of defect this component can have, because it looks like a rendering glitch.
   * - A BLANK KEY. The key is a sort name, a `track` value and an accessibility anchor; blank text satisfies
   *   `string` and satisfies none of those.
   * - A MALFORMED WIDTH. {@link DataTableWidth} rejects a literal, but a value composed at run time - a
   *   percentage assembled from a number, a token name read from configuration - satisfies the type and can
   *   still be invalid CSS, which the browser discards in silence.
   * - A SORTABLE HEADING WITH A HIDDEN LABEL, restated at run time even though the type forbids it, because
   *   this one renders a FOCUSABLE CONTROL WITH NOTHING VISIBLE IN IT. A caller reaching this component from
   *   JavaScript, or through a cast, would otherwise plant an unlabelled tab stop in the heading row.
   *
   * Each THROWS rather than being absorbed: a column set is a structure the feature authors rather than data a
   * user supplies, so every one of these is a programming defect, and there is no logging channel here to
   * report it through. Throwing is also the only form a test can assert on.
   *
   * @param value The column descriptors, or an absent value for none.
   * @throws Error when two columns share a key, when a key is blank, when a width is not one of the forms
   *   {@link DataTableWidth} admits, or when a sortable heading also hides its label.
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
   * Sets the rows of the current page, already ordered and paged by the server.
   *
   * Read as `readonly` and never mutated: these are the items of a paging envelope whose members are all
   * `readonly`, because a response has already happened and a component that rewrote one would be describing
   * something the server never said.
   *
   * Replacing the array DROPS a selection that is no longer on the page, so the selected state cannot
   * outlive the row it described. Membership is tested by reference, consistent with how identity is taken
   * everywhere in this component.
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
   * Sets the {@link DataTableColumnCommon.key} the rows are currently ordered by, or an absent value when
   * the server's own ordering applies.
   *
   * Blank text and omission mean the same thing, matching the paging contract, so a feature that clears its
   * sort by binding the empty string gets an unsorted table rather than a heading claiming to be sorted by
   * nothing.
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
   * Consulted only when a sort key is present. Falls back to ascending, which is both the server's default
   * when the member is omitted and the direction a first activation asks for.
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
   * While waiting, the body shows the shared progress indicator instead of the previous page, so nobody
   * reads stale rows that are about to be replaced, and sort activation is refused so a second request
   * cannot be queued behind the first.
   *
   * Compared against `true` rather than tested for truthiness, in keeping with this component's rule against
   * truthiness tests.
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
   * The component does NOT update its own {@link sortBy} or {@link sortDir}. Those two inputs are the sole
   * source of truth for `aria-sort`, and an optimistic local update would make a heading announce an order
   * whose request had failed. It emits and waits: the feature rebinds both once the reordered page has
   * actually arrived.
   *
   * This and {@link rowSelect} are the only event emitters in the component. An emitter is not used to hold
   * state anywhere, and no reactive stream stands in for one either.
   */
  @Output() public readonly sortChange = new EventEmitter<DataTableSortChange>();

  /**
   * Emits the row that was activated.
   */
  @Output() public readonly rowSelect = new EventEmitter<TRow>();

  /**
   * Whether a request is in flight, for the template's waiting branch.
   */
  protected readonly isLoading = this.loadingSignal.asReadonly();

  /**
   * The selected row, or `null`. Compared by reference in the template.
   */
  protected readonly selectedRow = this.selectedRowSignal.asReadonly();

  /**
   * Whether rows carry the selection affordance, for the template's row attributes.
   *
   * See {@link DataTableComponent.rowsSelectableSignal} for why this is derived from the
   * emitter's subscription state rather than from an input.
   */
  protected readonly rowsSelectable = this.rowsSelectableSignal.asReadonly();

  /**
   * Whether there is nothing to show and nothing on the way.
   *
   * An empty page is a legitimate answer, not an error, and it is distinguished from "still loading" so the
   * empty state is never claimed prematurely.
   */
  protected readonly isEmpty = computed(
    () => this.loadingSignal() === false && this.rowsSignal().length === 0,
  );

  /**
   * Cells spanned by the waiting and empty rows.
   *
   * Floored at one so neither can span zero cells. A message spanning fewer cells than the table has would
   * leave real empty cells beside it, which a screen reader announces as blanks.
   */
  protected readonly columnSpan = computed(() =>
    Math.max(this.columnsSignal().length, MINIMUM_COLUMN_SPAN),
  );

  /**
   * Total rows the table actually renders, including the heading row, for `aria-rowcount`.
   *
   * The count of the CURRENT PAGE, not of the match set: this component is handed one page and must not
   * claim knowledge of the rest. The pager reports the whole.
   *
   * The waiting and empty branches replace the records with ONE spanning row, and that row is counted rather
   * than ignored. Counting only the records would announce a one-row table while a screen reader went on to
   * meet a second row, which is precisely the kind of quiet disagreement `aria-rowcount` exists to prevent.
   */
  protected readonly ariaRowCount = computed(() => {
    const records = this.rowsSignal();
    const rendersMessage = this.loadingSignal() === true || records.length === 0;

    return (rendersMessage ? MESSAGE_ROW_COUNT : records.length) + HEADER_ROW_COUNT;
  });

  /**
   * The `aria-rowindex` of the waiting or empty row.
   *
   * It occupies the position the first record would have held, immediately after the heading row.
   */
  protected readonly messageRowIndex = HEADER_ROW_COUNT + MESSAGE_ROW_COUNT;

  /**
   * The heading row's `aria-rowindex`.
   *
   * ARIA row indexes are one-based and COUNT the heading row, so the heading is row one and the first body
   * row is row two. Exposed as a member rather than written into the template as a literal, so the heading
   * index and {@link ariaRowCount} can never drift apart.
   */
  protected readonly headerRowIndex = HEADER_ROW_COUNT;

  /**
   * Track sizes for the table's `colgroup`.
   */
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
   * This is where each {@link DataTableFormattedColumn.value} formatter is invoked, exactly once per row per
   * redraw. It depends on the columns and the rows ONLY: selection and the sort inputs are deliberately not
   * read here, so neither selecting a row nor rebinding a sort re-runs a single formatter.
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
   * Toggles only on the column that already carries the active sort; moving to a different column starts
   * ascending, which is the conventional and least surprising first result. Refused while a request is in
   * flight, so a reader cannot queue a second ordering behind the first and end up looking at the one they
   * abandoned.
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
    // ⚠ THE STATE BOUNDARY'S OWN GUARD, and it is deliberately the SECOND line of defence rather
    // than the only one. The two handlers are declared in the template unconditionally — a
    // template cannot register a listener conditionally without duplicating the entire row,
    // cells and all, which would put the whole body markup in the file twice and let the two
    // copies drift — so each of them refuses first, before touching the event. This refusal
    // guarantees the INVARIANT the attributes depend on: on a grid nothing listens to, no row can
    // hold, be painted as, or announce a selection, however activation was reached.
    if (this.rowsSelectableSignal() === false) {
      return;
    }

    this.selectedRowSignal.set(row);
    this.rowSelect.emit(row);
  }

  /**
   * Settles whether rows are selectable, once, before the first render.
   *
   * ⚠ THE TIMING IS THE WHOLE POINT. A template output binding is registered while the PARENT
   * view is created, which happens before this component's initialisation hook runs — so by
   * the time this executes, `observed` reports the settled answer for every consumer that
   * binds `(rowSelect)` in its markup. Reading it in the constructor instead would be too
   * early and would make every grid non-selectable.
   */
  public ngOnInit(): void {
    this.rowsSelectableSignal.set(this.rowSelect.observed);
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
   * Activates a row from the keyboard, unless the key landed on a control.
   *
   * Both activation keys are honoured, and the space bar's default page scroll is suppressed so activating a
   * row does not also jump the viewport. Every other key is left alone, so type-ahead and caret navigation
   * still work.
   *
   * The order of the two guards is load-bearing. The control test comes FIRST, so a key pressed on a
   * projected control returns before the default is suppressed. Reversed, a space bar pressed on an
   * inline-editable checkbox inside a cell would have its default cancelled here and the checkbox would
   * refuse to toggle - the row would quietly disable the very control the cell exists to offer.
   *
   * @param row The row the key was pressed on.
   * @param event The keyboard event.
   */
  protected activateRowFromKeyboard(row: TRow, event: KeyboardEvent): void {
    // ⚠⚠ THIS GUARD IS FIRST, AHEAD OF BOTH OTHERS, AND SUPPRESSING THE DEFAULT IS WHY. On a grid
    // nothing listens to, a row activates nothing — so cancelling the space bar's default here
    // would take the reader's PAGE SCROLL away and give nothing back for it. Refusing further
    // down, inside `activateRow`, was not sufficient: the default was already suppressed by the
    // time that refusal ran, which is the one observable harm a non-selecting grid could still do
    // from the keyboard.
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
   * control.
   *
   * Row commands are projected content, so they are interactive elements sitting INSIDE an activatable row.
   * Without a boundary, clicking Edit or Delete would bubble to the row handler and fire `rowSelect` as well,
   * so every command would silently double as a selection - and on a delete command that is the worst possible
   * pairing.
   *
   * This is the CELL-WIDE layer and the coarser of the two: it declares the whole commands cell to be outside
   * the row's activation area, so a press on the padding beside a command, or on a wrapper the feature put
   * between its commands, does not select the row either. The finer layer is `originatesFromControl`, applied
   * by the row handlers themselves, and it covers what this one cannot: an ORDINARY template cell, which
   * legitimately hosts interactive content while the rest of the cell is still row content a reader may click
   * to select. A cell-wide boundary there would take row activation away from most of the grid, so neither
   * layer subsumes the other and both are declared.
   *
   * The trade-off is stated rather than hidden. Nesting interactive content inside an activatable row is not
   * ideal in the abstract, but the alternative - forbidding row activation whenever any command exists - would
   * remove a working affordance from every grid because one grid has commands. Suppressing propagation at the
   * boundary is the narrower fix: each command keeps its own accessible name, focus behaviour and native
   * keyboard activation, and only the bubbling to the row is cut. Both event families are stopped, because a
   * command activated by keyboard raises a key event that would bubble just as a click does; propagation is
   * stopped and the default is NOT prevented.
   *
   * @param event The click or key event raised inside the actions cell.
   */
  protected blockRowActivation(event: Event): void {
    event.stopPropagation();
  }
}


/**
 * Elements that own their own activation, and must therefore never have a press or a key stolen by the row
 * around them.
 *
 * A single selector rather than a tag list, because three of these cases are attributes and not elements.
 * Each entry earns its place:
 *
 * - `a[href]` - only a linked anchor is operable; a bare `a` is a text span.
 * - `button`, `input`, `select`, `textarea`, `label` - the form controls a template cell projects. `label`
 *   is included because a press on a label is FORWARDED to the control it names, so treating the label as
 *   inert would let the row swallow a press that was about to toggle a checkbox.
 * - `summary` - the operable part of a disclosure element.
 * - `audio[controls]`, `video[controls]` - media with its own transport controls.
 * - `[contenteditable]:not([contenteditable='false'])` - an editable region, where a key press is text
 *   entry.
 * - `[tabindex]:not([tabindex='-1'])` - the catch-all for anything a feature has made focusable itself. This
 *   is the entry that makes the row exclusion below necessary, because the row is a tab stop and therefore
 *   matches it.
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
 * Whether an event began inside something that owns its own activation.
 *
 * The defect this closes. Every press and every key inside a row bubbles to the row's own handlers, and a
 * row is activatable. So a click on an inline-editable checkbox in an ordinary template cell also emitted a
 * row selection, and a space bar pressed on that same checkbox reached the row handler, which cancelled the
 * default and stopped the checkbox toggling - the row disabling the control the cell exists to offer. The
 * commands cell was already fenced off wholesale, but an ordinary template cell was not, and it is precisely
 * the cell that legitimately mixes controls with row content.
 *
 * The test walks OUTWARD from the element the event started on, using the nearest matching ancestor rather
 * than the target alone, because a press very often lands on something inside a control - the text of a
 * button, an icon within it - rather than on the control itself.
 *
 * The row itself is excluded, and that exclusion is what makes the whole test work rather than disable the
 * feature. The row carries a tab index so selection is reachable from the keyboard, which means it matches
 * the focusable catch-all in the selector; without the exclusion, EVERY event would be reported as coming
 * from a control and no row could ever be selected. The element the handler is attached to is the row, so it
 * is the boundary the walk stops at: a match at or above it does not count.
 *
 * Read synchronously during dispatch, which is the only time the element the handler is attached to is
 * available on the event.
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

/**
 * The width forms {@link DataTableWidth} admits, as a pattern the run time can apply.
 *
 * Written as one expression so the compile-time type and the run-time test cannot drift apart: a percentage
 * of one or more digits with an optional fractional part, either intrinsic keyword, or a reference to a
 * custom property. Anchored at both ends, so a value that merely CONTAINS an admitted form - a calculation,
 * a pair of values, a declaration with a trailing importance flag - is rejected rather than half-matched.
 */
const WIDTH_PATTERN = /^(?:\d+(?:\.\d+)?%|min-content|max-content|var\(--[^\s()]+\))$/;

/**
 * Rejects a column set that breaks an invariant no type can carry alone.
 *
 * Declared as a function so it is hoisted and can therefore be called from the input setter above without
 * depending on declaration order in this module.
 *
 * The message names the offending key, because a set of a dozen columns gives a reader nowhere to start
 * otherwise.
 *
 * @typeParam TRow The row contract the columns read.
 * @param columns The set being bound.
 * @throws Error on a blank key, a duplicate key, a malformed width, or a sortable heading that also hides
 *   its label.
 */
function assertColumnsAreValid<TRow extends object>(
  columns: readonly DataTableColumn<TRow>[],
): void {
  const seen = new Set<string>();

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

    if (column.width !== undefined && WIDTH_PATTERN.test(column.width) === false) {
      throw new Error(
        `The width "${column.width}" declared by the data-table column "${key}" is not a ` +
          'supported track size. Use a percentage, min-content, max-content, or var(--token).',
      );
    }

    // Read through a widened view of the two heading members, and that widening is required rather than
    // stylistic: the descriptor already makes this pair unrepresentable, so reading them directly narrows
    // the second test to a comparison the compiler proves can never be true and rejects. Widening restores
    // the run-time check for the caller the type cannot reach - a JavaScript consumer, or one that has cast
    // - without weakening the compile-time guarantee for everyone else.
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
 * No normalisation happens here and none is wanted: the width has already been proved to be one of the four
 * admitted forms when the set was bound, so trimming or repairing it at this point would be repairing
 * something that cannot be broken - and trimming the text then accepting whatever remained is precisely what
 * would let an invalid value reach the DOM and be discarded there in silence.
 *
 * Declared as a function rather than assigned to a constant so it is hoisted, and can therefore be read by
 * the field initialisers above without depending on the order of declarations in this module.
 *
 * @param width The declared width, if any.
 * @returns The width, or `null` when the column declared none, which leaves the column to take its share
 *   automatically.
 */
function resolveWidth(width: DataTableWidth | undefined): string | null {
  return width ?? null;
}

/**
 * Resolves the `aria-sort` value for a heading.
 *
 * Derived from the two sort inputs alone, which is what keeps the announced order and the rendered order
 * from ever disagreeing.
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
 * A one-line resolution, and it is one line BECAUSE the descriptor is a discriminated union: `text` is the
 * only kind that may be omitted, and both other kinds must declare themselves and must carry a template.
 * Inferring `template` from the presence of a cell template is the mechanism by which a mis-declared column
 * would become a rendering decision instead of a compile error, so nothing is inferred.
 *
 * @param column The column being projected.
 * @returns The resolved kind.
 */
function resolveKind<TRow extends object>(column: DataTableColumn<TRow>): DataTableColumnKind {
  return column.kind ?? 'text';
}

/**
 * Converts a cell value to text that is safe to interpolate.
 *
 * The conversions are deliberate and narrow:
 *
 * - an absent value yields the empty string, never the WORDS `null` or `undefined`. The empty string is also
 *   the legacy convention: the sentinel module defines its null string as the empty string rather than as a
 *   null reference, so an absent string and a blank one were already indistinguishable upstream.
 * - a string passes through untouched, whitespace included, so a value that was deliberately padded arrives
 *   as it was sent.
 * - a number is written out only when finite. A non-finite number yields the empty string rather than
 *   printing an error token into a data cell.
 * - a large integer is written out.
 * - EVERY other shape yields the empty string. A boolean, a date object or a nested object has no single
 *   correct text form, and guessing one would be a rule this component cannot get right for every caller:
 *   descending into an object would render a stringified object, and inventing a yes-or-no vocabulary here
 *   would create a second source of truth for wording that a display pipe already owns. Such a column
 *   states a formatter or a cell template instead.
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
 * NO PRECEDENCE RULE, because the descriptor admits exactly one text source per column: a formatter or a
 * bound member, never both. The two branches below are therefore the two arms of the union rather than a
 * ranking, and the final fallthrough is unreachable for a column that satisfies the type - it is retained
 * because this function is also reached for a column bound from JavaScript, where an empty cell is a better
 * answer than a thrown error in the middle of a projection.
 *
 * The formatter's result is normalised as well as the member's, because a caller can return a non-string at
 * run time whatever the declared type says.
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
 * Projects one body cell.
 *
 * Called once per column per row inside the row projection, which is the ONLY place a formatter runs. A
 * template context is built only for a cell that will actually render one, so a text column allocates
 * nothing extra.
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

  // Both non-text kinds render the caller's template: `template` for rich or editable content, and `actions`
  // for a column of row commands, which is a template too - it must be, because a command is per-row. Only
  // its propagation handling differs. Gating on `kind === 'template'` alone would silently discard every
  // actions column.
  //
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
  };
}

// MIGRATION ledger
//
//  Every deliberate divergence from the eight legacy grids, with the measured source evidence for each.
//  Recorded inline here as well as in the prose above so that the full inventory is auditable from one place.
//  The repository-root migration notes are authored separately and are not edited from here.

// MIGRATION: the column KEY is separate from the display LABEL, because a label-keyed model is impossible
// against this codebase. The legacy design agreed: header text was a localised resource keyed BY column
// identity - `PortalId.Header`, `Title.Header`, `HostingFee.Header` in
// `Website/admin/Portal/App_LocalResources/Portals.ascx.resx`.

// MIGRATION: header alignment and body alignment are INDEPENDENT descriptor members, never derived from one
// another. A single grid-wide alignment would misrender most of them.

// MIGRATION: there is deliberately NO rowAction output; row commands are content-projected. Two legacy grids
// make a command CONDITIONAL PER ROW, which no single output could express.
// `Website/admin/Portal/portalalias.ascx` binds an edit command's visibility to `IsNotCurrent(PortalAliasID)`
// - the alias you are currently browsing through cannot be edited.

// MIGRATION: the pager is a SIBLING placed by the feature, never rendered by this component and never inside
// a table footer.

// MIGRATION: fixed pixel widths are replaced by intrinsic measures or spacing tokens, applied through a
// colgroup so no cell rule carries a size.

// MIGRATION: action-column heading labels are NORMALISED - every column carries a label and headerHidden
// decides whether it is painted.

// MIGRATION: the users-online column is DROPPED, a documented functional reduction. Users-online is out of
// scope for this migration and no endpoint exists to feed it, so reproducing the column would render a
// permanent, meaningless indicator.

// MIGRATION: the Option Strict asymmetry is made EXPLICIT. `Website/release.config` compiles the
// administration pages with strict="false" while the class library builds with Option Strict on, so the
// legacy markup contains implicit coercions that this component's strict typing forbids. Here every
// conversion is explicit and total: toDisplayText enumerates the shapes it accepts and yields the empty
// string for the rest, so no value is ever coerced silently.

// MIGRATION: row identity is the row's OBJECT REFERENCE, never an identifier member, because the legacy
// sentinels collide with real keys. `Website/admin/Tabs/managetabs.ascx` compares a ModuleID against -1 in
// markup on a column seeded at 0. Taking the reference reads no number, so no truthiness or sign test exists
// anywhere in this component to be wrong.

// MIGRATION: sortDir uses the server's own member names, imported from the paging contract rather than
// restated. The abbreviated spelling is rejected at the wire: sortDir=asc is answered with 400 Bad Request.
// A locally declared 'asc' | 'desc' union compiles cleanly and fails every sort at run time.

// MIGRATION: the accessible name arrives by CONTENT PROJECTION, not by a caption input. The public surface
// is closed at five inputs, so a caption input is not available, and the migration plan nonetheless mandates
// a caption element on every table. Projection satisfies both: a feature writes an element carrying the
// dataTableCaption attribute and the component renders it inside a real caption, announced and visually
// hidden.
//
// Projection alone left a consumer able to render the caption element EMPTY, which produces a table with no
// accessible name - strictly worse than the legacy grids' inconsistency, since an empty caption tells a
// reader nothing while occupying the slot that would have told them something. A generic fallback names the
// kind of element reached without pretending to know the screen, and a projected caption replaces it
// entirely, so the consumer's obligation is unchanged and only the failure mode improves.
//
// No development-time assertion accompanies it, unlike the page header's non-blank title check. That
// component validates an INPUT, which it can read; this one would have to inspect projected DOM after render
// to discover an omission, and the fallback removes the defect rather than merely reporting it, so the
// machinery would buy nothing.

// MIGRATION: NgTemplateOutlet is imported as a THIRD entry alongside the two composed siblings, which is a
// deliberate deviation from a strictly two-entry import list. It is NOT the umbrella common-directives
// module - which appears nowhere in this workspace - but the single tree-shakeable standalone directive that
// renders a caller's TemplateRef, and Angular offers no other mechanism for rendering one from a template.
// Omitting it would leave cellTemplate and the entire actions column kind as members that can never render,
// which the zero-placeholder standard forbids outright, and would strand the legacy template,
// inline-editable and image-command columns they exist to carry - for example roles.ascx. Functional parity
// carries rule-force under the Minimal Change Clause; an import count whose stated purpose is avoiding dead
// weight does not, and this import is not dead weight.

// MIGRATION: the grid degrades by SCROLLING, and its headings degrade by WRAPPING, neither of which the
// legacy grids did - they simply crushed. Two measured trade-offs are recorded here rather than left to be
// rediscovered. First, the table carries a width floor composed from the spacing scale so the scroll
// container has something to scroll; the intrinsic `min-content` keyword was tried first and is INERT under
// a fixed table layout with percentage tracks, where it resolves to approximately zero. Second, headings
// wrap instead of being held to one line: held to one line they do not fit, they SPILL, and pairs of
// headings physically overlap at a narrow viewport. The cost of wrapping is that a single-word heading in a
// marginally narrow track can break mid-word, which is cosmetic, leaves the accessible name intact, and is
// strictly preferable to two illegible headings.

// MIGRATION: virtualisation is delivered by CSS render-virtualisation, with zero JavaScript, zero new
// dependency and no change to the public surface. No scrolling package exists in the pinned dependency set
// and none was added, so no viewport component was available. Every row remains in the DOM, hence in the
// accessibility tree, find-in-page and the tab order. The design-token set declares no row-height or
// intrinsic-size token, so the placeholder size is composed from the existing base line-height, base type
// size and spacing tokens rather than being hardcoded to a pixel literal. Because no rows are removed this
// is not windowing, so aria-rowcount and aria-rowindex are not strictly needed; they are published anyway
// because they are accurate, valid on a native table without an explicit role, visually free, and they stop
// a future switch to real windowing from silently misreporting the table's size.
