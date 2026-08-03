import { Component, TemplateRef, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import {
  DataTableCellContext,
  DataTableColumn,
  DataTableComponent,
  DataTableSortChange,
} from './data-table.component';

/**
 * A row shape carrying one of each value kind the text conversion covers, plus an
 * identifier so the sentinel cases can be exercised.
 *
 * Typed as a real interface rather than an index signature on purpose: that is what makes
 * a mistyped `field` a compile error, which an index-signature row could never deliver
 * under `noPropertyAccessFromIndexSignature`.
 */
interface Row {
  readonly id: number;
  readonly name: string;
  readonly count: number;
  readonly active: boolean;
  readonly note: string | null;
  readonly nested?: { readonly inner: string };
}

const ROWS: readonly Row[] = [
  { id: 0, name: 'Alpha', count: 3, active: true, note: null },
  { id: -1, name: 'Beta', count: 12, active: false, note: 'second' },
];

function baseColumns(): readonly DataTableColumn<Row>[] {
  return [
    { key: 'name', label: 'Name', field: 'name', sortable: true },
    { key: 'count', label: 'Count', field: 'count', sortable: true, bodyAlign: 'end' },
    { key: 'active', label: 'Active', field: 'active' },
    { key: 'note', label: 'Note', field: 'note' },
  ];
}

describe('DataTableComponent', () => {
  let fixture: ComponentFixture<DataTableComponent<Row>>;
  let sorts: DataTableSortChange[];
  let selected: Row[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [DataTableComponent] }).compileComponents();

    fixture = TestBed.createComponent<DataTableComponent<Row>>(DataTableComponent);
    sorts = [];
    selected = [];
    fixture.componentInstance.sortChange.subscribe((change) => sorts.push(change));
    fixture.componentInstance.rowSelect.subscribe((row) => selected.push(row));

    fixture.componentRef.setInput('columns', baseColumns());
    fixture.componentRef.setInput('rows', ROWS);
    fixture.detectChanges();
  });

  function set(name: string, value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  function headers(): readonly HTMLTableCellElement[] {
    return fixture.debugElement
      .queryAll(By.css('th'))
      .map((node) => node.nativeElement as HTMLTableCellElement);
  }

  function bodyRows(): readonly HTMLTableRowElement[] {
    return fixture.debugElement
      .queryAll(By.css('tbody tr'))
      .map((node) => node.nativeElement as HTMLTableRowElement);
  }

  function textOf(element: Element | null): string {
    return (element?.textContent ?? '').trim();
  }

  function cellTexts(rowIndex: number): readonly string[] {
    return Array.from(bodyRows()[rowIndex].querySelectorAll('td')).map((cell) => textOf(cell));
  }

  describe('structure', () => {
    it('renders a real table, so row and column relationships survive', () => {
      expect(fixture.debugElement.query(By.css('table'))).not.toBeNull();
    });

    it('renders a caption element, which the consumer names by projection', () => {
      expect(fixture.debugElement.query(By.css('table > caption'))).not.toBeNull();
    });

    it('marks every column heading with a column scope', () => {
      expect(headers().every((header) => header.getAttribute('scope') === 'col')).toBeTrue();
    });

    it('renders one heading per column and one row per record', () => {
      expect(headers().length).toBe(4);
      expect(bodyRows().length).toBe(2);
    });

    it('renders one col element per column, so no cell rule carries a size', () => {
      expect(fixture.debugElement.queryAll(By.css('colgroup > col')).length).toBe(4);
    });

    it('applies a declared width to the col rather than to a cell', () => {
      set('columns', [{ key: 'name', label: 'Name', field: 'name', width: '30%' }]);

      const col = fixture.debugElement.query(By.css('colgroup > col'))
        .nativeElement as HTMLTableColElement;

      expect(col.style.inlineSize).toBe('30%');
    });

    it('ignores a blank width, letting the column take its share', () => {
      set('columns', [{ key: 'name', label: 'Name', field: 'name', width: '   ' }]);

      const col = fixture.debugElement.query(By.css('colgroup > col'))
        .nativeElement as HTMLTableColElement;

      expect(col.style.inlineSize).toBe('');
    });
  });

  describe('row counting for assistive technology', () => {
    it('counts the heading row in aria-rowcount', () => {
      const table = fixture.debugElement.query(By.css('table')).nativeElement as HTMLElement;

      expect(table.getAttribute('aria-rowcount')).toBe('3');
    });

    it('numbers the heading row first and the body rows after it', () => {
      const headerRow = fixture.debugElement.query(By.css('thead tr')).nativeElement as HTMLElement;

      expect(headerRow.getAttribute('aria-rowindex')).toBe('1');
      expect(bodyRows()[0].getAttribute('aria-rowindex')).toBe('2');
      expect(bodyRows()[1].getAttribute('aria-rowindex')).toBe('3');
    });

    it('counts the waiting row, so the announced size matches what is rendered', () => {
      set('loading', true);
      const table = fixture.debugElement.query(By.css('table')).nativeElement as HTMLElement;

      expect(bodyRows().length).toBe(1);
      expect(table.getAttribute('aria-rowcount')).toBe('2');
      expect(bodyRows()[0].getAttribute('aria-rowindex')).toBe('2');
    });

    it('counts the empty row too, rather than announcing a table with no rows', () => {
      set('rows', []);
      const table = fixture.debugElement.query(By.css('table')).nativeElement as HTMLElement;

      expect(bodyRows().length).toBe(1);
      expect(table.getAttribute('aria-rowcount')).toBe('2');
      expect(bodyRows()[0].getAttribute('aria-rowindex')).toBe('2');
    });
  });

  describe('cell rendering', () => {
    it('renders a bound string and number as written', () => {
      expect(cellTexts(0)[0]).toBe('Alpha');
      expect(cellTexts(0)[1]).toBe('3');
    });

    it('renders an absent value as an empty cell, not as the word null', () => {
      expect(cellTexts(0)[3]).toBe('');
    });

    it('renders a boolean as empty, because such a column states a formatter instead', () => {
      expect(cellTexts(0)[2]).toBe('');
    });

    it('renders a nested object as empty rather than as a stringified object', () => {
      set('columns', [{ key: 'nested', label: 'Nested', field: 'nested' }]);
      set('rows', [{ ...ROWS[0], nested: { inner: 'deep' } }]);

      expect(cellTexts(0)[0]).toBe('');
    });

    it('renders a derived column through its formatter', () => {
      set('columns', [
        { key: 'summary', label: 'Summary', value: (row: Row) => `${row.name}/${row.count}` },
      ]);

      expect(cellTexts(0)[0]).toBe('Alpha/3');
    });

    it('prefers the formatter over a bound member when both are declared', () => {
      set('columns', [
        { key: 'name', label: 'Name', field: 'name', value: () => 'formatted' },
      ]);

      expect(cellTexts(0)[0]).toBe('formatted');
    });

    it('renders a formatter that returns a boolean as empty, not as the word false', () => {
      set('columns', [
        {
          key: 'broken',
          label: 'Broken',
          value: () => false as unknown as string,
        },
      ]);

      expect(cellTexts(0)[0]).toBe('');
    });

    it('invokes a formatter exactly once per row per redraw', () => {
      const format = jasmine.createSpy('format').and.returnValue('x');
      set('columns', [{ key: 'spy', label: 'Spy', value: format }]);

      expect(format).toHaveBeenCalledTimes(ROWS.length);

      // A redraw that changes nothing the projection depends on must not re-run it.
      fixture.detectChanges();

      expect(format).toHaveBeenCalledTimes(ROWS.length);
    });

    it('does not re-run a formatter when a row is selected', () => {
      const format = jasmine.createSpy('format').and.returnValue('x');
      set('columns', [{ key: 'spy', label: 'Spy', value: format }]);
      format.calls.reset();

      bodyRows()[0].click();
      fixture.detectChanges();

      expect(format).not.toHaveBeenCalled();
    });
  });

  describe('sorting', () => {
    it('renders a control only for a sortable column', () => {
      expect(headers()[0].querySelector('button')).not.toBeNull();
      expect(headers()[2].querySelector('button')).toBeNull();
    });

    it('omits aria-sort entirely for a column that offers no sorting', () => {
      expect(headers()[2].hasAttribute('aria-sort')).toBeFalse();
    });

    it('reports none for a sortable column that is not the active sort', () => {
      expect(headers()[0].getAttribute('aria-sort')).toBe('none');
      expect(headers()[1].getAttribute('aria-sort')).toBe('none');
    });

    it('reports the direction for the active sort only', () => {
      set('sortBy', 'name');
      set('sortDir', 'Descending');

      expect(headers()[0].getAttribute('aria-sort')).toBe('descending');
      expect(headers()[1].getAttribute('aria-sort')).toBe('none');
    });

    it('treats a blank sort key as no sort at all', () => {
      set('sortBy', '   ');

      expect(headers()[0].getAttribute('aria-sort')).toBe('none');
    });

    it('asks for ascending in the server spelling when a column is not yet sorted', () => {
      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([{ key: 'name', direction: 'Ascending' }]);
    });

    it('flips to descending on the column already sorted ascending', () => {
      set('sortBy', 'name');
      set('sortDir', 'Ascending');

      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([{ key: 'name', direction: 'Descending' }]);
    });

    it('flips back to ascending on the column already sorted descending', () => {
      set('sortBy', 'name');
      set('sortDir', 'Descending');

      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([{ key: 'name', direction: 'Ascending' }]);
    });

    it('starts ascending when moving to a different column', () => {
      set('sortBy', 'name');
      set('sortDir', 'Descending');

      headers()[1].querySelector('button')?.click();

      expect(sorts).toEqual([{ key: 'count', direction: 'Ascending' }]);
    });

    it('does not change its own sort state, so a heading cannot claim a failed order', () => {
      set('sortBy', 'name');
      set('sortDir', 'Ascending');

      headers()[0].querySelector('button')?.click();
      fixture.detectChanges();

      expect(fixture.componentInstance.sortDir).toBe('Ascending');
      expect(headers()[0].getAttribute('aria-sort')).toBe('ascending');
    });

    it('never reorders the array it was given', () => {
      const original = [...ROWS];
      set('sortBy', 'name');

      headers()[0].querySelector('button')?.click();
      fixture.detectChanges();

      expect(ROWS).toEqual(original);
      expect(cellTexts(0)[0]).toBe('Alpha');
    });

    it('disables the control while a request is in flight', () => {
      set('loading', true);

      expect(headers()[0].querySelector('button')?.disabled).toBeTrue();
    });

    it('emits nothing while a request is in flight, so a second order cannot be queued', () => {
      set('loading', true);

      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([]);
    });

    it('declares an explicit control type so it cannot submit a surrounding form', () => {
      expect(headers()[0].querySelector('button')?.getAttribute('type')).toBe('button');
    });
  });

  describe('waiting and empty states', () => {
    it('shows the shared indicator instead of the previous page while loading', () => {
      set('loading', true);

      expect(fixture.debugElement.query(By.css('app-loading-spinner'))).not.toBeNull();
      expect(bodyRows().length).toBe(1);
    });

    it('spans the waiting cell across every column, so no blank cells are announced', () => {
      set('loading', true);

      const cell = bodyRows()[0].querySelector('td');

      expect(cell?.getAttribute('colspan')).toBe('4');
    });

    it('shows the shared empty state when there is nothing and nothing is loading', () => {
      set('rows', []);

      expect(fixture.debugElement.query(By.css('app-empty-state'))).not.toBeNull();
    });

    it('prefers waiting over empty, so an empty page is not claimed prematurely', () => {
      set('rows', []);
      set('loading', true);

      expect(fixture.debugElement.query(By.css('app-loading-spinner'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('app-empty-state'))).toBeNull();
    });

    it('spans at least one cell even with no columns at all', () => {
      set('columns', []);
      set('rows', []);

      expect(bodyRows()[0].querySelector('td')?.getAttribute('colspan')).toBe('1');
    });

    it('treats an absent rows binding as an empty page rather than failing', () => {
      set('rows', null);

      expect(fixture.debugElement.query(By.css('app-empty-state'))).not.toBeNull();
    });

    it('treats an absent columns binding as no columns rather than failing', () => {
      set('columns', undefined);

      expect(headers().length).toBe(0);
    });
  });

  describe('selection', () => {
    it('emits the row that was activated', () => {
      bodyRows()[1].click();

      expect(selected).toEqual([ROWS[1]]);
    });

    it('publishes the selected row through aria-current, not colour alone', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      expect(bodyRows()[0].hasAttribute('aria-current')).toBeFalse();
      expect(bodyRows()[1].getAttribute('aria-current')).toBe('true');
    });

    it('makes every row reachable from the keyboard', () => {
      expect(bodyRows().every((row) => row.getAttribute('tabindex') === '0')).toBeTrue();
    });

    it('activates a row on Enter', () => {
      bodyRows()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

      expect(selected).toEqual([ROWS[0]]);
    });

    it('activates a row on Space and suppresses the page scroll', () => {
      const event = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });
      bodyRows()[0].dispatchEvent(event);

      expect(selected).toEqual([ROWS[0]]);
      expect(event.defaultPrevented).toBeTrue();
    });

    it('ignores every other key, so caret navigation still works', () => {
      bodyRows()[0].dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }),
      );

      expect(selected).toEqual([]);
    });

    it('drops a selection that is no longer on the page', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      set('rows', [ROWS[0]]);

      expect(bodyRows()[0].hasAttribute('aria-current')).toBeFalse();
    });

    it('keeps a selection that survives into the replacement page', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      set('rows', [ROWS[1]]);

      expect(bodyRows()[0].getAttribute('aria-current')).toBe('true');
    });
  });

  describe('sentinel-safe identity', () => {
    it('renders a row whose identifier is zero and one whose identifier is minus one', () => {
      // Zero seeds the role, page and module identities and minus one seeds the portal
      // identity, so both are legitimate keys and neither may be read as absent.
      expect(bodyRows().length).toBe(2);
      expect(cellTexts(0)[0]).toBe('Alpha');
      expect(cellTexts(1)[0]).toBe('Beta');
    });

    it('selects the row whose identifier is zero without treating it as absent', () => {
      bodyRows()[0].click();
      fixture.detectChanges();

      expect(selected).toEqual([ROWS[0]]);
      expect(bodyRows()[0].getAttribute('aria-current')).toBe('true');
    });

    it('renders a zero-valued numeric cell as a zero rather than as an empty cell', () => {
      set('columns', [{ key: 'count', label: 'Count', field: 'count' }]);
      set('rows', [{ ...ROWS[0], count: 0 }]);

      expect(cellTexts(0)[0]).toBe('0');
    });

    it('renders a non-finite number as empty rather than as an error token', () => {
      set('columns', [{ key: 'count', label: 'Count', field: 'count' }]);
      set('rows', [{ ...ROWS[0], count: Number.NaN }]);

      expect(cellTexts(0)[0]).toBe('');
    });
  });

  describe('alignment', () => {
    it('resolves both sides to start when a column states neither', () => {
      expect(headers()[0].getAttribute('data-align')).toBe('start');
      expect(bodyRows()[0].querySelectorAll('td')[0].getAttribute('data-align')).toBe('start');
    });

    it('applies a body alignment without touching the heading', () => {
      expect(headers()[1].getAttribute('data-align')).toBe('start');
      expect(bodyRows()[0].querySelectorAll('td')[1].getAttribute('data-align')).toBe('end');
    });

    it('applies a heading alignment without touching the body', () => {
      set('columns', [{ key: 'name', label: 'Name', field: 'name', headerAlign: 'center' }]);

      expect(headers()[0].getAttribute('data-align')).toBe('center');
      expect(bodyRows()[0].querySelectorAll('td')[0].getAttribute('data-align')).toBe('start');
    });

    it('applies the two sides independently when both are stated differently', () => {
      set('columns', [
        {
          key: 'name',
          label: 'Name',
          field: 'name',
          headerAlign: 'center',
          bodyAlign: 'end',
        },
      ]);

      expect(headers()[0].getAttribute('data-align')).toBe('center');
      expect(bodyRows()[0].querySelectorAll('td')[0].getAttribute('data-align')).toBe('end');
    });
  });

  describe('heading labels', () => {
    it('keeps a duplicated label on both of the columns that declare it', () => {
      // The role grid really does carry two "Every" headings and two "Period" headings in
      // one grid, which is why the key and the label are separate members.
      set('columns', [
        { key: 'billingPeriod', label: 'Every', field: 'count' },
        { key: 'billingFrequency', label: 'Period', field: 'name' },
        { key: 'trialPeriod', label: 'Every', field: 'count' },
        { key: 'trialFrequency', label: 'Period', field: 'name' },
      ]);

      expect(headers().length).toBe(4);
      expect(headers().map((header) => textOf(header))).toEqual([
        'Every',
        'Period',
        'Every',
        'Period',
      ]);
    });

    it('keeps a hidden label in the accessibility tree while removing it from view', () => {
      set('columns', [
        { key: 'commands', label: 'Commands', kind: 'actions', headerHidden: true },
      ]);

      const label = headers()[0].querySelector('span');

      expect(textOf(label)).toBe('Commands');
      expect(label?.classList.contains('data-table__label--hidden')).toBeTrue();
    });
  });

  describe('change detection', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      const definition = (DataTableComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });
  });
});

/** Host exercising the projection surfaces, which need a real template context. */
@Component({
  standalone: true,
  imports: [DataTableComponent],
  template: `
    <app-data-table [columns]="columns" [rows]="rows" (rowSelect)="selectedRows.push($event)">
      <span dataTableCaption>Portals</span>

      <ng-template #commands let-row>
        <button type="button" class="edit" (click)="edited.push(row)">Edit</button>
        @if (row.active) {
          <button type="button" class="remove">Delete</button>
        }
      </ng-template>

      <ng-template #status let-row="row" let-index="rowIndex">
        <em class="status">{{ row.name }}#{{ index }}</em>
      </ng-template>
    </app-data-table>
  `,
})
class HostComponent {
  /**
   * The command template, read from the host's own view.
   *
   * Declared inside the grid's tag but reachable as a view child, because an
   * `ng-template` a host writes is part of the host's view whether or not the component
   * projects it. This is exactly how a feature hands cell content to a column.
   */
  @ViewChild('commands', { static: true })
  public commandsTemplate?: TemplateRef<DataTableCellContext<Row>>;

  /** The status template, read the same way. */
  @ViewChild('status', { static: true })
  public statusTemplate?: TemplateRef<DataTableCellContext<Row>>;

  public rows: readonly Row[] = ROWS;

  public columns: readonly DataTableColumn<Row>[] = [];

  public readonly selectedRows: Row[] = [];

  public readonly edited: Row[] = [];
}

describe('DataTableComponent projection', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  it('renders the projected caption as the table caption', () => {
    const caption = fixture.debugElement.query(By.css('table > caption'))
      .nativeElement as HTMLElement;

    expect((caption.textContent ?? '').trim()).toBe('Portals');
  });

  it('renders a template column through the caller template with its row context', () => {
    const host = fixture.componentInstance;
    host.columns = [
      { key: 'status', label: 'Status', kind: 'template', cellTemplate: host.statusTemplate },
    ];
    fixture.detectChanges();

    const rendered = fixture.debugElement.queryAll(By.css('td .status'));

    expect(rendered.length).toBe(2);
    expect((rendered[0].nativeElement as HTMLElement).textContent).toBe('Alpha#0');
    expect((rendered[1].nativeElement as HTMLElement).textContent).toBe('Beta#1');
  });

  it('renders an actions column and lets the caller decide per row', () => {
    const host = fixture.componentInstance;
    host.columns = [
      {
        key: 'commands',
        label: 'Commands',
        kind: 'actions',
        headerHidden: true,
        cellTemplate: host.commandsTemplate,
      },
    ];
    fixture.detectChanges();

    // Alpha is active and gets both commands; Beta is not and gets only the first. That
    // per-row conditionality is exactly why there is no single row-action output.
    expect(fixture.debugElement.queryAll(By.css('td .edit')).length).toBe(2);
    expect(fixture.debugElement.queryAll(By.css('td .remove')).length).toBe(1);
  });

  it('does not select the row when a projected command is activated', () => {
    const host = fixture.componentInstance;
    host.columns = [
      { key: 'commands', label: 'Commands', kind: 'actions', cellTemplate: host.commandsTemplate },
    ];
    fixture.detectChanges();

    (fixture.debugElement.query(By.css('td .edit')).nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(host.edited.length).toBe(1);
    expect(host.selectedRows).toEqual([]);
  });

  it('still selects the row when a non-command cell is activated', () => {
    const host = fixture.componentInstance;
    host.columns = [{ key: 'name', label: 'Name', field: 'name' }];
    fixture.detectChanges();

    (
      fixture.debugElement.query(By.css('tbody tr')).nativeElement as HTMLElement
    ).click();
    fixture.detectChanges();

    expect(host.selectedRows).toEqual([ROWS[0]]);
  });
});
