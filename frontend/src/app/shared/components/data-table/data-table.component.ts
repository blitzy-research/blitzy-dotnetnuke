import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
} from '@angular/core';

/** The direction a column is sorted in. */
export type SortDirection = 'asc' | 'desc';

/**
 * A request to sort by a column.
 *
 * Carries both members rather than only the key, so a consumer never has to remember
 * which direction it last asked for in order to interpret the next request.
 */
export interface SortChange {
  /** The {@link DataTableColumn.key} of the column to sort by. */
  readonly key: string;

  /** The direction to sort in. */
  readonly direction: SortDirection;
}

/**
 * One column of a data table.
 */
export interface DataTableColumn {
  /**
   * The row property this column displays, and the column's identity.
   *
   * Used both to read the cell value and as the value reported in a
   * {@link SortChange}, so it must match the property name the API returns and the
   * sort key the API accepts. Those coincide throughout this application.
   */
  readonly key: string;

  /** The visible column heading. */
  readonly header: string;

  /**
   * Whether the column offers sorting.
   *
   * Defaults to absent, meaning not sortable. Sorting is opt-in per column because
   * the API does not accept every property as a sort key, and offering a control that
   * produces a rejected request is worse than offering none.
   */
  readonly sortable?: boolean;

  /**
   * Horizontal alignment of the column's cells and heading.
   *
   * Defaults to absent, meaning start-aligned. Numeric columns are usually
   * end-aligned so their digits line up.
   */
  readonly align?: 'start' | 'end' | 'center';
}

/**
 * A sortable, keyboard-operable record grid.
 *
 * Renders a real `table` with a `caption`, `th` elements carrying `scope`, and
 * `aria-sort` on the sorted column. That structure is the point of the component: a
 * grid built from `div` elements loses the row-and-column relationships that assistive
 * technology uses to read a cell in context, and no amount of ARIA restores them as
 * well as the native element provides them.
 *
 * The component is presentational. It does not sort, does not page, does not fetch and
 * does not filter — it reports what the person asked for and renders what it was
 * given. Sorting in particular is SERVER-side throughout this application, because a
 * page of ten rows cannot be sorted meaningfully on the client: reordering the visible
 * ten would produce an order that is correct within the page and wrong across the
 * result set.
 *
 * MIGRATION: this replaces the legacy grid control and its
 * `.DataGrid_Header`, `.DataGrid_Item`, `.DataGrid_AlternatingItem`,
 * `.DataGrid_SelectedItem`, `.DataGrid_Footer` and `.DataGrid_Container` styling
 * vocabulary; the alternating-row and header treatments are carried across as tokens.
 * Three legacy behaviours are deliberately not reproduced:
 *
 * - Sorting posted the entire page back to the server and re-rendered it. Here it
 *   emits an event and the feature issues one request.
 * - Per-row commands were raster image buttons, several of which carried no
 *   alternative text at all. No component may reference an image asset in this
 *   workspace, so row commands are projected by the feature as text controls that
 *   carry their own accessible names.
 * - Row selection was a postback on the row itself. A clickable table row cannot be
 *   reached from the keyboard without either putting a tab stop on a `tr` — which
 *   assistive technology does not announce as actionable — or overriding the table's
 *   own roles. The interactive element is therefore a real `button` inside the first
 *   cell, which is announced, focusable and activatable by both Enter and Space
 *   without any script.
 *
 * @typeParam TRow The row type. Constrained to `object` so cell values can be read by
 *   key without permitting a primitive row that has no properties to read.
 */
@Component({
  selector: 'app-data-table',
  standalone: true,
  imports: [],
  templateUrl: './data-table.component.html',
  styleUrl: './data-table.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableComponent<TRow extends object> {
  /**
   * The table's accessible name, rendered as its `caption`.
   *
   * Required. A table without a caption forces a person to infer what they are reading
   * from the surrounding page, which assistive technology does not convey when the
   * table is reached directly. Hidden visually by default — see
   * {@link captionVisible} — so requiring it costs no pixels.
   */
  @Input({ required: true }) caption!: string;

  /**
   * Whether the caption is shown as well as announced.
   *
   * Defaults to false, because the administration screens already carry a page heading
   * that names the records being listed, and a visible caption would repeat it. The
   * caption is always present in the accessibility tree either way; this only controls
   * whether it is painted.
   */
  @Input() captionVisible: boolean = false;

  /** The columns to render, in order. */
  @Input({ required: true }) columns: readonly DataTableColumn[] = [];

  /** The rows to render, already sorted and paged by the server. */
  @Input({ required: true }) rows: readonly TRow[] = [];

  /**
   * The key of the column currently sorted by, or absent when unsorted.
   */
  @Input() sortBy?: string;

  /**
   * The direction the sorted column is sorted in.
   *
   * Only meaningful when {@link sortBy} names a column. Defaults to ascending, matching
   * the direction a first click requests.
   */
  @Input() sortDir: SortDirection = 'asc';

  /**
   * Whether the table is waiting for data.
   *
   * While true the body shows a single waiting row rather than the previous page's
   * rows, so a person is never reading stale data that is about to be replaced, and the
   * sort controls are disabled so a second request cannot be queued behind the first.
   */
  @Input() loading: boolean = false;

  /**
   * The message shown when there are no rows and nothing is loading.
   *
   * Bound from the consumer so the wording can name what was searched for. A default is
   * supplied because a blank cell would leave a person unsure whether the table had
   * failed or was simply empty.
   */
  @Input() emptyMessage: string = DEFAULT_EMPTY_MESSAGE;

  /**
   * Whether each row offers a primary action.
   *
   * When true the first cell of every row renders a button carrying that cell's text,
   * which emits {@link rowSelect}. When false the first cell is plain text and no row
   * is interactive.
   */
  @Input() selectable: boolean = false;

  /**
   * Emits the sort the person asked for.
   *
   * A first request on an unsorted column asks for ascending; a request on the column
   * already sorted ascending asks for descending, and vice versa. The component does
   * NOT change its own {@link sortBy} or {@link sortDir}: the consumer rebinds them once
   * the sorted data has actually arrived, which is what stops a heading from claiming an
   * order whose request failed.
   */
  @Output() readonly sortChange = new EventEmitter<SortChange>();

  /** Emits the row whose primary action was activated. */
  @Output() readonly rowSelect = new EventEmitter<TRow>();

  /** Whether there is nothing to show and nothing on the way. */
  get isEmpty(): boolean {
    return !this.loading && this.rows.length === 0;
  }

  /**
   * The number of columns, used to span the waiting and empty rows.
   *
   * A message cell that spanned fewer columns than the table has would leave empty
   * cells beside it, which assistive technology announces as blank cells in a row.
   */
  get columnCount(): number {
    return Math.max(this.columns.length, 1);
  }

  /**
   * The value of `aria-sort` for a column heading.
   *
   * Returns the sorted state only for the column actually sorted by. Reporting `none`
   * for the others is required rather than optional: a table where every heading claims
   * to be sorted conveys nothing.
   *
   * @param column The column being rendered.
   * @returns An `aria-sort` value, or null to omit the attribute entirely.
   */
  ariaSortFor(column: DataTableColumn): 'ascending' | 'descending' | 'none' | null {
    if (column.sortable !== true) {
      // Omitted rather than set to `none` for a column that offers no sorting at all.
      // `none` means "sortable but not currently sorted", which would be a false claim.
      return null;
    }

    if (this.sortBy !== column.key) {
      return 'none';
    }

    return this.sortDir === 'asc' ? 'ascending' : 'descending';
  }

  /**
   * Whether a column is the one currently sorted by.
   *
   * @param column The column being rendered.
   * @returns True when this column carries the active sort.
   */
  isSorted(column: DataTableColumn): boolean {
    return column.sortable === true && this.sortBy === column.key;
  }

  /**
   * The direction indicator shown beside an active sort heading.
   *
   * Hidden from assistive technology in the template, because `aria-sort` already
   * conveys the direction and announcing both would say it twice.
   *
   * @param column The column being rendered.
   * @returns A glyph, or the empty string when the column is not sorted.
   */
  sortIndicatorFor(column: DataTableColumn): string {
    if (!this.isSorted(column)) {
      return '';
    }

    return this.sortDir === 'asc' ? SORT_ASCENDING_GLYPH : SORT_DESCENDING_GLYPH;
  }

  /**
   * Requests a sort on a column.
   *
   * Ignores columns that do not offer sorting, and ignores every request while loading,
   * so a person cannot queue a second sort behind the first and end up looking at the
   * result of the one they abandoned.
   *
   * @param column The column whose heading was activated.
   */
  requestSort(column: DataTableColumn): void {
    if (column.sortable !== true || this.loading) {
      return;
    }

    // Toggling only applies to the column already sorted. Moving to a different column
    // starts ascending, which is the conventional and least surprising first result.
    const direction: SortDirection =
      this.sortBy === column.key && this.sortDir === 'asc' ? 'desc' : 'asc';

    this.sortChange.emit({ key: column.key, direction });
  }

  /**
   * Emits a row's primary action.
   *
   * @param row The row whose action was activated.
   */
  selectRow(row: TRow): void {
    if (!this.selectable) {
      return;
    }

    this.rowSelect.emit(row);
  }

  /**
   * Reads a cell's display text.
   *
   * Returns text rather than the raw value, so the template interpolates a string it
   * can render and never stringifies an object into `[object Object]`. The conversions
   * are deliberate and narrow:
   *
   * - null and undefined render as the empty string, not as the words "null" or
   *   "undefined". An absent value is absent, and the API omits null members entirely.
   * - a boolean renders through the shared yes/no vocabulary, matching the pipe the
   *   feature screens use, so a flag reads the same wherever it appears.
   * - everything else is converted with the standard string conversion, which is
   *   correct for the numbers, strings and ISO instants the API returns.
   *
   * An object-valued cell is NOT descended into. A column that needs a nested value
   * projects it in the feature before binding, because guessing which member of a
   * nested object to show would be a rule this component cannot get right.
   *
   * @param row The row being rendered.
   * @param column The column being rendered.
   * @returns The text to display.
   */
  cellText(row: TRow, column: DataTableColumn): string {
    const value: unknown = (row as Record<string, unknown>)[column.key];

    if (value === null || value === undefined) {
      return '';
    }

    if (typeof value === 'boolean') {
      return value ? BOOLEAN_TRUE_TEXT : BOOLEAN_FALSE_TEXT;
    }

    if (typeof value === 'object') {
      // Rendering an object would produce `[object Object]`, which tells a person
      // nothing and looks like a defect. An empty cell is the honest result, and the
      // fix belongs in the feature's column projection.
      return '';
    }

    return String(value);
  }

  /**
   * The inline alignment class for a column.
   *
   * @param column The column being rendered.
   * @returns A modifier class name.
   */
  alignClassFor(column: DataTableColumn): string {
    if (column.align === 'end') {
      return 'data-table__cell--end';
    }

    if (column.align === 'center') {
      return 'data-table__cell--center';
    }

    return 'data-table__cell--start';
  }

  /**
   * Identity for the column iteration.
   *
   * @param _index Unused positional index.
   * @param column The column being tracked.
   * @returns The column key, which is unique within one table.
   */
  trackByColumnKey(_index: number, column: DataTableColumn): string {
    return column.key;
  }

  /** Labels bound from constants so the template compiler cannot collapse them. */
  readonly labels = DATA_TABLE_LABELS;
}

/** Shown in the body while data is on the way. */
const DEFAULT_EMPTY_MESSAGE = 'There is nothing to show.';

/** Indicator for an ascending sort. */
const SORT_ASCENDING_GLYPH = '\u25B2';

/** Indicator for a descending sort. */
const SORT_DESCENDING_GLYPH = '\u25BC';

/**
 * Rendering of a true boolean cell.
 *
 * MIGRATION: the legacy grids rendered boolean columns as a checked or unchecked
 * raster image with no alternative text, so the value was invisible to assistive
 * technology entirely. Text carries the same meaning and is announced.
 */
const BOOLEAN_TRUE_TEXT = 'Yes';

/** Rendering of a false boolean cell. */
const BOOLEAN_FALSE_TEXT = 'No';

/** The table's fixed wording. */
const DATA_TABLE_LABELS = {
  loading: 'Loading\u2026',
} as const;
