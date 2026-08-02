import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { DataTableColumn, DataTableComponent, SortChange } from './data-table.component';

/** A row shape with one of each cell type the conversion rules cover. */
interface Row {
  readonly name: string;
  readonly count: number;
  readonly active: boolean;
  readonly note: string | null;
  readonly nested?: { readonly inner: string };
}

const COLUMNS: readonly DataTableColumn[] = [
  { key: 'name', header: 'Name', sortable: true },
  { key: 'count', header: 'Count', sortable: true, align: 'end' },
  { key: 'active', header: 'Active' },
  { key: 'note', header: 'Note' },
];

const ROWS: readonly Row[] = [
  { name: 'Alpha', count: 3, active: true, note: null },
  { name: 'Beta', count: 12, active: false, note: 'second' },
];

describe('DataTableComponent', () => {
  let fixture: ComponentFixture<DataTableComponent<Row>>;
  let sorts: SortChange[];
  let selected: Row[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [DataTableComponent] }).compileComponents();

    fixture = TestBed.createComponent<DataTableComponent<Row>>(DataTableComponent);
    sorts = [];
    selected = [];
    fixture.componentInstance.sortChange.subscribe((change) => sorts.push(change));
    fixture.componentInstance.rowSelect.subscribe((row) => selected.push(row));

    fixture.componentRef.setInput('caption', 'Portals');
    fixture.componentRef.setInput('columns', COLUMNS);
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

  function cellTexts(rowIndex: number): readonly string[] {
    return Array.from(bodyRows()[rowIndex]!.querySelectorAll('td')).map((cell) =>
      cell.textContent!.trim(),
    );
  }

  describe('structure', () => {
    it('renders a real table, so row and column relationships survive', () => {
      // A grid built from generic elements loses the relationships assistive technology
      // uses to read a cell in context, and no amount of ARIA restores them as well.
      expect(fixture.debugElement.query(By.css('table'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('thead'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('tbody'))).not.toBeNull();
    });

    it('renders the caption, so the table has an accessible name', () => {
      const caption = fixture.debugElement.query(By.css('caption')).nativeElement as HTMLElement;

      expect(caption.textContent!.trim()).toBe('Portals');
    });

    it('hides the caption visually by default, while keeping it in the accessibility tree', () => {
      const caption = fixture.debugElement.query(By.css('caption')).nativeElement as HTMLElement;

      expect(caption.classList.contains('data-table__caption--hidden'))
        .withContext('the page heading already names the records')
        .toBeTrue();
    });

    it('shows the caption when asked', () => {
      set('captionVisible', true);

      const caption = fixture.debugElement.query(By.css('caption')).nativeElement as HTMLElement;
      expect(caption.classList.contains('data-table__caption--hidden')).toBeFalse();
    });

    it('marks every column heading with a column scope', () => {
      expect(headers().every((header) => header.getAttribute('scope') === 'col'))
        .withContext('scope is what lets a cell be named by its column')
        .toBeTrue();
    });

    it('renders one heading per column and one row per record', () => {
      expect(headers().length).toBe(4);
      expect(bodyRows().length).toBe(2);
    });
  });

  describe('cell rendering', () => {
    it('renders text and numbers as written', () => {
      expect(cellTexts(0)[0]).toBe('Alpha');
      expect(cellTexts(0)[1]).toBe('3');
    });

    it('renders a boolean through the shared yes/no vocabulary', () => {
      // The legacy grids rendered these as a raster image with no alternative text, so
      // the value was invisible to assistive technology entirely.
      expect(cellTexts(0)[2]).toBe('Yes');
      expect(cellTexts(1)[2]).toBe('No');
    });

    it('renders an absent value as an empty cell, not as the word null', () => {
      expect(cellTexts(0)[3]).toBe('');
    });

    it('renders an object-valued cell empty rather than as a stringified object', () => {
      // `[object Object]` tells a person nothing and looks like a defect; the fix belongs
      // in the feature's column projection.
      set('columns', [...COLUMNS, { key: 'nested', header: 'Nested' }]);
      set('rows', [{ name: 'A', count: 1, active: true, note: null, nested: { inner: 'x' } }]);

      expect(cellTexts(0)[4]).toBe('');
    });
  });

  describe('sorting', () => {
    it('renders a button only for a sortable column', () => {
      const sortButtons = fixture.debugElement.queryAll(By.css('.data-table__sort'));

      expect(sortButtons.length).withContext('two of the four columns are sortable').toBe(2);
    });

    it('omits aria-sort entirely for a column that offers no sorting', () => {
      // Reporting `none` there would claim the column is sortable but unsorted.
      expect(headers()[2]!.hasAttribute('aria-sort')).toBeFalse();
    });

    it('reports none for a sortable column that is not the active sort', () => {
      set('sortBy', 'name');

      expect(headers()[1]!.getAttribute('aria-sort')).toBe('none');
    });

    it('reports the direction for the active sort only', () => {
      set('sortBy', 'name');
      set('sortDir', 'desc');

      expect(headers()[0]!.getAttribute('aria-sort')).toBe('descending');
      expect(headers()[1]!.getAttribute('aria-sort')).toBe('none');
    });

    it('asks for ascending when a column is not yet sorted', () => {
      fixture.debugElement.queryAll(By.css('.data-table__sort'))[0]!.nativeElement.click();

      expect(sorts).toEqual([{ key: 'name', direction: 'asc' }]);
    });

    it('toggles to descending on the column already sorted ascending', () => {
      set('sortBy', 'name');
      set('sortDir', 'asc');

      fixture.debugElement.queryAll(By.css('.data-table__sort'))[0]!.nativeElement.click();

      expect(sorts).toEqual([{ key: 'name', direction: 'desc' }]);
    });

    it('starts ascending when moving to a different column', () => {
      set('sortBy', 'name');
      set('sortDir', 'desc');

      fixture.debugElement.queryAll(By.css('.data-table__sort'))[1]!.nativeElement.click();

      expect(sorts).toEqual([{ key: 'count', direction: 'asc' }]);
    });

    it('emits nothing while loading, so a second sort cannot be queued behind the first', () => {
      set('loading', true);

      fixture.componentInstance.requestSort(COLUMNS[0]!);

      expect(sorts).toEqual([]);
    });

    it('emits nothing for a column that offers no sorting', () => {
      fixture.componentInstance.requestSort(COLUMNS[2]!);

      expect(sorts).toEqual([]);
    });

    it('does not change its own sort state, so a heading cannot claim an order whose request failed', () => {
      fixture.debugElement.queryAll(By.css('.data-table__sort'))[0]!.nativeElement.click();
      fixture.detectChanges();

      expect(fixture.componentInstance.sortBy).toBeUndefined();
    });

    it('declares an explicit button type on the sort control', () => {
      const control = fixture.debugElement.query(By.css('.data-table__sort'))
        .nativeElement as HTMLButtonElement;

      expect(control.getAttribute('type')).toBe('button');
    });

    it('disables the sort control while loading', () => {
      set('loading', true);
      set('rows', []);

      // The header is still rendered while the body shows the waiting row.
      const control = fixture.debugElement.query(By.css('.data-table__sort'))
        .nativeElement as HTMLButtonElement;

      expect(control.disabled).toBeTrue();
    });
  });

  describe('waiting and empty states', () => {
    it('shows a waiting row instead of the previous page while loading', () => {
      set('loading', true);

      expect(bodyRows().length).toBe(1);
      expect(cellTexts(0)[0]).toBe('Loading…');
    });

    it('spans the waiting cell across every column, so no blank cells are announced', () => {
      set('loading', true);

      expect(bodyRows()[0]!.querySelector('td')!.getAttribute('colspan')).toBe('4');
    });

    it('shows the empty message when there is nothing and nothing is loading', () => {
      set('rows', []);

      expect(cellTexts(0)[0]).toBe('There is nothing to show.');
    });

    it('uses the supplied empty message', () => {
      set('rows', []);
      set('emptyMessage', 'No portals match that search.');

      expect(cellTexts(0)[0]).toBe('No portals match that search.');
    });

    it('prefers the waiting state over the empty state, so an empty page is not claimed prematurely', () => {
      set('rows', []);
      set('loading', true);

      expect(cellTexts(0)[0]).toBe('Loading…');
      expect(fixture.componentInstance.isEmpty).toBeFalse();
    });
  });

  describe('row selection', () => {
    it('renders no interactive element when selection is off', () => {
      expect(fixture.debugElement.query(By.css('.data-table__row-action'))).toBeNull();
    });

    it('renders a real button in the first cell when selection is on', () => {
      // A clickable row cannot be reached from the keyboard without a tab stop on an
      // element assistive technology does not announce as actionable.
      set('selectable', true);

      const actions = fixture.debugElement.queryAll(By.css('.data-table__row-action'));
      expect(actions.length).withContext('one per row').toBe(2);
      expect((actions[0]!.nativeElement as HTMLButtonElement).tagName).toBe('BUTTON');
    });

    it('names the button with the record identifying value', () => {
      set('selectable', true);

      expect(
        (fixture.debugElement.query(By.css('.data-table__row-action')).nativeElement as HTMLElement)
          .textContent!.trim(),
      ).toBe('Alpha');
    });

    it('emits the row it was activated on', () => {
      set('selectable', true);

      fixture.debugElement.queryAll(By.css('.data-table__row-action'))[1]!.nativeElement.click();

      expect(selected).toEqual([ROWS[1]!]);
    });

    it('emits nothing when selection is off', () => {
      fixture.componentInstance.selectRow(ROWS[0]!);

      expect(selected).toEqual([]);
    });

    it('declares an explicit button type on the row action', () => {
      set('selectable', true);

      expect(
        (fixture.debugElement.query(By.css('.data-table__row-action'))
          .nativeElement as HTMLButtonElement).getAttribute('type'),
      ).toBe('button');
    });
  });

  describe('alignment', () => {
    it('end-aligns a column that asks for it and start-aligns the rest', () => {
      const cells = bodyRows()[0]!.querySelectorAll('td');

      expect(cells[0]!.classList.contains('data-table__cell--start')).toBeTrue();
      expect(cells[1]!.classList.contains('data-table__cell--end')).toBeTrue();
    });

    it('centres a column that asks for it', () => {
      set('columns', [{ key: 'name', header: 'Name', align: 'center' }]);

      expect(bodyRows()[0]!.querySelector('td')!.classList.contains('data-table__cell--center'))
        .toBeTrue();
    });
  });

  describe('change detection', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      const definition = (DataTableComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });
  });
});
