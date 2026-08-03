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

  /**
   * A large integer, present so the text conversion's `bigint` clause is reachable
   * through a real bound member rather than through a cast formatter.
   *
   * Optional and unset on the shared rows, following the same arrangement as
   * {@link Row.nested}: the shape is declared here so that a `field` naming it type
   * checks, and supplied per test by spreading a base row.
   */
  readonly big?: bigint;
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

    it('names the table generically when the consumer projects no caption', () => {
      // The omission case, which is the one that matters: this fixture is built with no
      // projected content at all, so the caption falls back. An empty caption is worse
      // than no caption - it occupies the slot that would have named the table and says
      // nothing - so the assertion is on the caption having TEXT, not merely existing.
      const caption = fixture.debugElement.query(By.css('table > caption'))
        .nativeElement as HTMLElement;

      expect((caption.textContent ?? '').trim()).toBe('Data table');
    });

    it('keeps the fallback name out of the painted output but in the accessibility tree', () => {
      // The fallback must not become visible text. It inherits the same clipping hook the
      // projected caption uses, so a table that falls back looks identical and is merely
      // named; neither `display` nor `visibility` may be used, because either would take
      // the caption out of the accessibility tree along with the page and defeat the whole
      // point of naming it.
      const caption = fixture.debugElement.query(By.css('table > caption'))
        .nativeElement as HTMLElement;

      expect(caption.hasAttribute('data-visually-hidden')).toBeTrue();
      expect(getComputedStyle(caption).display).not.toBe('none');
      expect(getComputedStyle(caption).visibility).not.toBe('hidden');
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

    it('leaves a column with no declared width to take its share', () => {
      set('columns', [{ key: 'name', label: 'Name', field: 'name' }]);

      const col = fixture.debugElement.query(By.css('colgroup > col'))
        .nativeElement as HTMLTableColElement;

      expect(col.style.inlineSize).toBe('');
    });

    it('applies each intrinsic and token width form the descriptor admits', () => {
      // All four are valid CSS for inline-size on a col element, which is the whole
      // point of closing the type: a grid track keyword such as `1fr` or `minmax()` is
      // NOT, and the browser would drop it in silence.
      for (const width of ['min-content', 'max-content', 'var(--space-8)', '12.5%'] as const) {
        set('columns', [{ key: 'name', label: 'Name', field: 'name', width }]);

        const col = fixture.debugElement.query(By.css('colgroup > col'))
          .nativeElement as HTMLTableColElement;

        expect(col.style.inlineSize).toBe(width);
      }
    });
  });

  describe('column-set validation', () => {
    // Each invariant below is rejected when the SET IS BOUND rather than absorbed
    // during rendering, so the defect is reported at the call site that caused it. A
    // width or a key is written through a cast in these tests because the descriptor
    // already rejects the literal at compile time; the cast is what a JavaScript
    // caller does implicitly, and it is the only way to prove the run-time guard.
    function bind(column: unknown): () => void {
      return () => set('columns', [column as DataTableColumn<Row>]);
    }

    it('rejects a duplicate key, which would make two columns share one tracking identity', () => {
      expect(() =>
        set('columns', [
          { key: 'name', label: 'Name', field: 'name' },
          { key: 'name', label: 'Also name', field: 'note' },
        ]),
      ).toThrowError(/must not declare the key "name" twice/);
    });

    it('rejects a blank key, which is neither a sort name nor a tracking identity', () => {
      expect(bind({ key: '   ', label: 'Name', field: 'name' })).toThrowError(
        /non-blank key/,
      );
    });

    it('rejects a pixel width, which the design vocabulary forbids', () => {
      expect(bind({ key: 'name', label: 'Name', field: 'name', width: '100px' })).toThrowError(
        /is not a supported track size/,
      );
    });

    it('rejects a grid track width, which the browser would discard in silence', () => {
      for (const width of ['1fr', 'minmax(4rem, 1fr)', 'calc(50% - 1rem)']) {
        expect(bind({ key: 'name', label: 'Name', field: 'name', width })).toThrowError(
          /is not a supported track size/,
        );
      }
    });

    it('rejects a sortable heading that also hides its label', () => {
      // This pair is unrepresentable in the descriptor as well; the run-time guard
      // exists for the caller a type cannot reach, because the combination renders a
      // focusable control with nothing visible inside it.
      expect(
        bind({ key: 'name', label: 'Name', field: 'name', sortable: true, headerHidden: true }),
      ).toThrowError(/nothing visible in it/);
    });

    it('accepts a valid set, so the guard refuses nothing it should allow', () => {
      expect(() =>
        set('columns', [
          { key: 'name', label: 'Name', field: 'name', sortable: true, width: '40%' },
          { key: 'note', label: 'Note', value: (row: Row) => row.note ?? '', width: 'min-content' },
        ]),
      ).not.toThrow();
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

    it('renders a large integer in full, without exponent or precision loss', () => {
      // The value is deliberately past Number.MAX_SAFE_INTEGER. Every other numeric
      // path in this component goes through the `number` clause, which would render
      // this magnitude only approximately, so a cell that reproduces all nineteen
      // digits is evidence the `bigint` clause ran rather than the `number` one.
      const beyondSafeInteger = 9007199254740993n;
      set('columns', [{ key: 'big', label: 'Big', field: 'big' }]);
      set('rows', [{ ...ROWS[0], big: beyondSafeInteger }]);

      expect(cellTexts(0)[0]).toBe('9007199254740993');

      // The premise: this is a magnitude the ordinary numeric path cannot carry, so
      // the assertion above could not have been satisfied by that path.
      expect(String(Number(beyondSafeInteger))).not.toBe('9007199254740993');
    });

    it('renders an absent large integer as empty rather than as the word undefined', () => {
      // The same column against a row that omits the member, so the optional field
      // cannot make the clause above look total when it is not.
      set('columns', [{ key: 'big', label: 'Big', field: 'big' }]);
      set('rows', [ROWS[0]]);

      expect(cellTexts(0)[0]).toBe('');
    });

    it('renders a derived column through its formatter', () => {
      set('columns', [
        { key: 'summary', label: 'Summary', value: (row: Row) => `${row.name}/${row.count}` },
      ]);

      expect(cellTexts(0)[0]).toBe('Alpha/3');
    });

    it('admits exactly one text source per column, so no precedence rule is needed', () => {
      // A column declaring BOTH a bound member and a formatter no longer compiles: the
      // descriptor's text arms exclude one another, which is why the earlier
      // "formatter wins" precedence rule has no test any more — the ambiguity it
      // resolved is unrepresentable. Both sources still work on their own.
      set('columns', [{ key: 'name', label: 'Name', field: 'name' }]);
      expect(cellTexts(0)[0]).toBe('Alpha');

      set('columns', [{ key: 'name', label: 'Name', value: () => 'formatted' }]);
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

  describe('markup in data is rendered as text, never as markup', () => {
    // WHY THIS BLOCK EXISTS. Every string this component renders reaches it from a
    // caller and ultimately from the database, and the legacy corpus proves that is
    // not a hypothetical concern: a substantial minority of the legacy resource values
    // hold HTML tags and at least one carries a live advertising script sourced from a
    // remote third-party host, stored HTML-escaped so a naive search misses it. Port
    // that corpus into a grid and script-bearing text is ordinary input, not an attack.
    //
    // Interpolation escapes, so these are REGRESSION GUARDS rather than assertions
    // about a defect: they fail the moment any of these three values stops being
    // interpolated - the changes that would do it being a raw-HTML property binding, a
    // sanitiser bypass, a trusted-HTML wrapper or markup smuggled through an attribute
    // binding. Column headings are covered as well as cell values, because a heading is
    // just as caller-supplied as a cell and is rendered by a different template branch.
    //
    // Each case asserts BOTH halves: that no element was created from the payload, and
    // that the text survives verbatim. The second half matters on its own - a fix that
    // stripped the tags instead of escaping them would satisfy the first assertion
    // while silently corrupting a value that was never markup to begin with.
    const SCRIPT_PAYLOAD = '<script>window.__dataTableXss = true;</script>';
    const IMAGE_PAYLOAD = '<img src="x" onerror="window.__dataTableXss = true">';

    function scriptCount(): number {
      return (fixture.nativeElement as HTMLElement).querySelectorAll('script').length;
    }

    function imageCount(): number {
      return (fixture.nativeElement as HTMLElement).querySelectorAll('img').length;
    }

    it('renders a script-bearing cell value as text', () => {
      set('columns', [{ key: 'name', label: 'Name', field: 'name' }]);
      set('rows', [{ ...ROWS[0], name: SCRIPT_PAYLOAD }]);

      expect(scriptCount()).toBe(0);
      expect(cellTexts(0)[0]).toBe(SCRIPT_PAYLOAD);
    });

    it('renders a script-bearing column heading as text', () => {
      set('columns', [{ key: 'name', label: SCRIPT_PAYLOAD, field: 'name' }]);

      expect(scriptCount()).toBe(0);
      expect(textOf(headers()[0])).toBe(SCRIPT_PAYLOAD);
    });

    it('renders a script-bearing formatter result as text', () => {
      // The formatter path normalises its result independently of the bound-member
      // path, so escaping has to hold on both. A caller-supplied function is also the
      // likelier route in practice, since it is where wording gets assembled.
      set('columns', [{ key: 'summary', label: 'Summary', value: () => SCRIPT_PAYLOAD }]);

      expect(scriptCount()).toBe(0);
      expect(cellTexts(0)[0]).toBe(SCRIPT_PAYLOAD);
    });

    it('creates no element from an event-handler payload either', () => {
      // A script element injected after load does not execute in every browser, so an
      // assertion resting on scripts alone could pass for the wrong reason. An image
      // with a failing source and an error handler needs no such caveat: were the
      // markup live, the element would exist and its handler would run.
      set('columns', [{ key: 'name', label: IMAGE_PAYLOAD, field: 'name' }]);
      set('rows', [{ ...ROWS[0], name: IMAGE_PAYLOAD }]);

      expect(imageCount()).toBe(0);
      expect(cellTexts(0)[0]).toBe(IMAGE_PAYLOAD);
      expect(textOf(headers()[0])).toBe(IMAGE_PAYLOAD);
    });

    it('leaves no trace of any payload having executed', () => {
      // The guard behind the guards: if any case above had rendered live markup, the
      // handler it carries would have set this member. Read through an index signature
      // because it is deliberately not a declared global.
      expect((window as unknown as Record<string, unknown>)['__dataTableXss']).toBeUndefined();
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

    it('publishes the selected row through aria-selected, not colour alone', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      expect(bodyRows()[0].getAttribute('aria-selected')).toBe('false');
      expect(bodyRows()[1].getAttribute('aria-selected')).toBe('true');
    });

    it('publishes selection ONLY as aria-selected, never also as aria-current', () => {
      // The current-item state marks a reader's position within a set of related items,
      // which this component does not model separately from selection. Publishing both
      // announced one state twice in two vocabularies, one of them a claim the component
      // could not substantiate.
      bodyRows()[1].click();
      fixture.detectChanges();

      expect(bodyRows().some((row) => row.hasAttribute('aria-current'))).toBeFalse();
    });

    // THE SELECTED STATE IS ANNOUNCED ON EVERY SELECTABLE ROW - false on the others rather
    // than absent - because the shared stylesheet keys both the selected tint and the
    // selectable pointer affordance to that attribute, precisely so that no state can be
    // painted without also being announced. Asserting the announcement alone left the
    // stylesheet's hook free to be removed with the suite still green and the rows
    // silently losing both their tint and their pointer, so the class is asserted beside
    // it: one template comparison drives both, and neither may change without the other.
    //
    // The distinction between "present and false" and "absent" is the assertion that
    // matters here: an attribute-selector rule cannot match an absent attribute, so
    // collapsing the unselected rows to no attribute at all would compile, look correct
    // in the announcement, and break the styling.

    it('marks every selectable row with a selection state, not just the selected one', () => {
      expect(bodyRows().every((row) => row.hasAttribute('aria-selected'))).toBeTrue();
      expect(bodyRows().every((row) => row.getAttribute('aria-selected') === 'false')).toBeTrue();
    });

    it('reads the selection state true on exactly one row once a row is chosen', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      expect(bodyRows()[0].getAttribute('aria-selected')).toBe('false');
      expect(bodyRows()[1].getAttribute('aria-selected')).toBe('true');

      expect(
        bodyRows().filter((row) => row.getAttribute('aria-selected') === 'true').length,
      ).toBe(1);
    });

    it('keeps the announcement consistent with the selected class', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      // Both hooks are driven by the same comparison in the template, so asserting them
      // together is what stops one being changed without the other.
      //
      // The current-item state is deliberately NOT among them: it marks a reader's
      // position within a set of related items, which this component does not model
      // separately from selection, so emitting it alongside the selection state announced
      // one state twice in two vocabularies - one of them a claim the component could not
      // substantiate. Its absence is asserted by the sibling above rather than here.
      const chosen = bodyRows()[1];
      expect(chosen.getAttribute('aria-selected')).toBe('true');
      expect(chosen.classList.contains('data-table__row--selected')).toBeTrue();

      const other = bodyRows()[0];
      expect(other.getAttribute('aria-selected')).toBe('false');
      expect(other.classList.contains('data-table__row--selected')).toBeFalse();
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

      expect(bodyRows()[0].getAttribute('aria-selected')).toBe('false');
    });

    it('keeps a selection that survives into the replacement page', () => {
      bodyRows()[1].click();
      fixture.detectChanges();

      set('rows', [ROWS[1]]);

      expect(bodyRows()[0].getAttribute('aria-selected')).toBe('true');
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
      expect(bodyRows()[0].getAttribute('aria-selected')).toBe('true');
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

      <!--
        An inline-editable cell, which is what the two legacy checkbox columns were: a
        control living inside an ordinary cell whose remaining content is still row
        material a reader may click to select the row.
      -->
      <ng-template #editable let-row>
        <input type="checkbox" class="toggle" [checked]="row.active" />
        <span class="cell-text">{{ row.name }}</span>
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

  /** The inline-editable template, read the same way. */
  @ViewChild('editable', { static: true })
  public editableTemplate?: TemplateRef<DataTableCellContext<Row>>;

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

  /**
   * Narrows a view child from its declared optional type to the template a column
   * requires.
   *
   * The descriptor now REQUIRES a template on both non-text kinds, which is the whole
   * point of the change, and a view child is declared optional because it is not
   * populated until the view exists. Failing loudly here rather than passing an absent
   * value through keeps the assertion honest: a test that silently rendered no template
   * would still report a pass.
   *
   * @param template The view child to narrow.
   * @returns The template.
   */
  function requireTemplate(
    template: TemplateRef<DataTableCellContext<Row>> | undefined,
  ): TemplateRef<DataTableCellContext<Row>> {
    if (template === undefined) {
      throw new Error('The host template was not available on the fixture.');
    }

    return template;
  }

  it('renders the projected caption as the table caption', () => {
    const caption = fixture.debugElement.query(By.css('table > caption'))
      .nativeElement as HTMLElement;

    expect((caption.textContent ?? '').trim()).toBe('Portals');
  });

  it('lets a projected caption replace the generic fallback entirely', () => {
    // The other half of the fallback contract, and the half that keeps it a floor rather
    // than a change of behaviour. A consumer that does its job must see no trace of the
    // default: if the two ever appeared together a reader would hear the generic name
    // alongside the real one on every table in the application.
    const caption = fixture.debugElement.query(By.css('table > caption'))
      .nativeElement as HTMLElement;

    expect(caption.textContent ?? '').not.toContain('Data table');
  });

  it('renders a template column through the caller template with its row context', () => {
    const host = fixture.componentInstance;
    host.columns = [
      {
        key: 'status',
        label: 'Status',
        kind: 'template',
        cellTemplate: requireTemplate(host.statusTemplate),
      },
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
        cellTemplate: requireTemplate(host.commandsTemplate),
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
      {
        key: 'commands',
        label: 'Commands',
        kind: 'actions',
        cellTemplate: requireTemplate(host.commandsTemplate),
      },
    ];
    fixture.detectChanges();

    (fixture.debugElement.query(By.css('td .edit')).nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(host.edited.length).toBe(1);
    expect(host.selectedRows).toEqual([]);
  });

  // KEYBOARD ACTIVATION OF A COMMAND MUST NOT SELECT THE ROW EITHER. The click case
  // above was specified; the key case was not, even though the component stops BOTH
  // event families on the actions cell and its own comment says why - a command
  // activated from the keyboard raises a key event that bubbles exactly as a click
  // does. Without these two, the `(keydown)` binding on that cell could be deleted and
  // the suite would still pass, leaving every keyboard-driven Delete to also select the
  // row it deleted.
  //
  // Enter and Space are covered separately because the row handler treats them
  // separately: it acts on both, and additionally suppresses the default action for
  // Space to stop the page scrolling. Each is dispatched with `bubbles: true`, which is
  // what gives the suppression something real to prevent.

  it('does not select the row when a projected command is activated with Enter', () => {
    const host = fixture.componentInstance;
    host.columns = [
      { key: 'commands', label: 'Commands', kind: 'actions', cellTemplate: requireTemplate(host.commandsTemplate) },
    ];
    fixture.detectChanges();

    const command = fixture.debugElement.query(By.css('td .edit')).nativeElement as HTMLElement;
    command.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();

    expect(host.selectedRows).toEqual([]);
  });

  it('does not select the row when a projected command is activated with Space', () => {
    const host = fixture.componentInstance;
    host.columns = [
      { key: 'commands', label: 'Commands', kind: 'actions', cellTemplate: requireTemplate(host.commandsTemplate) },
    ];
    fixture.detectChanges();

    const command = fixture.debugElement.query(By.css('td .edit')).nativeElement as HTMLElement;
    const event = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });
    command.dispatchEvent(event);
    fixture.detectChanges();

    expect(host.selectedRows).toEqual([]);

    // The row handler is what calls preventDefault for Space, so an unprevented event
    // is independent evidence that the event never reached the row.
    expect(event.defaultPrevented).toBeFalse();
  });

  it('proves a key event from an ordinary cell still reaches the row', () => {
    // The complement, without which the two specs above would also pass if keyboard
    // activation were broken everywhere rather than blocked in the one cell.
    const host = fixture.componentInstance;
    host.columns = [{ key: 'name', label: 'Name', field: 'name' }];
    fixture.detectChanges();

    const cell = fixture.debugElement.query(By.css('tbody tr td')).nativeElement as HTMLElement;
    cell.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();

    expect(host.selectedRows).toEqual([ROWS[0]]);
  });

  it('renders a column that declares no kind as a text column', () => {
    // The cell kind is no longer inferred from the presence of a template: every non-text
    // arm of the column union declares `kind` explicitly and a template column with no
    // template is unrepresentable, so the only column shape that can omit `kind` is a text
    // column bound to a field. This pins that surviving half of the branch.
    const host = fixture.componentInstance;
    host.columns = [{ key: 'name', label: 'Name', field: 'name' }];
    fixture.detectChanges();

    expect(fixture.debugElement.queryAll(By.css('td .status')).length).toBe(0);

    const cell = fixture.debugElement.query(By.css('tbody tr td')).nativeElement as HTMLElement;
    expect((cell.textContent ?? '').trim()).toBe('Alpha');
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

  describe('an interactive control inside an ORDINARY template cell', () => {
    // The commands cell is fenced off wholesale, but an ordinary template cell is not,
    // and it is precisely the cell that legitimately mixes controls with row content -
    // the two legacy inline-editable checkbox columns posted back on change. These are
    // the cases that were previously broken: the row stole the press and, on the space
    // bar, cancelled the control's own default.
    function useEditableColumn(): HostComponent {
      const host = fixture.componentInstance;
      host.columns = [
        {
          key: 'editable',
          label: 'Editable',
          kind: 'template',
          cellTemplate: requireTemplate(host.editableTemplate),
        },
      ];
      fixture.detectChanges();

      return host;
    }

    function firstToggle(): HTMLInputElement {
      return fixture.debugElement.query(By.css('td .toggle')).nativeElement as HTMLInputElement;
    }

    it('does not select the row when the control itself is clicked', () => {
      const host = useEditableColumn();

      firstToggle().click();
      fixture.detectChanges();

      expect(host.selectedRows).toEqual([]);
    });

    it('lets the control keep its own click behaviour', () => {
      useEditableColumn();
      const toggle = firstToggle();
      const before = toggle.checked;

      toggle.click();
      fixture.detectChanges();

      expect(toggle.checked).toBe(!before);
    });

    it('does not cancel the default of a Space pressed on the control', () => {
      // The row suppresses the space bar's page scroll when a row is activated. Applied
      // to a checkbox, that same suppression stops it toggling, so the row must stand
      // aside before it prevents anything.
      const host = useEditableColumn();
      const event = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });

      firstToggle().dispatchEvent(event);
      fixture.detectChanges();

      expect(event.defaultPrevented).toBeFalse();
      expect(host.selectedRows).toEqual([]);
    });

    it('does not select the row when Enter is pressed on the control', () => {
      const host = useEditableColumn();

      firstToggle().dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
      );
      fixture.detectChanges();

      expect(host.selectedRows).toEqual([]);
    });

    it('still selects the row from the non-interactive content of the same cell', () => {
      // The boundary is per-event, not per-cell: the text beside the control is row
      // material, so clicking it must still select the row.
      const host = useEditableColumn();

      (fixture.debugElement.query(By.css('td .cell-text')).nativeElement as HTMLElement).click();
      fixture.detectChanges();

      expect(host.selectedRows).toEqual([ROWS[0]]);
    });

    it('still activates the row from the keyboard when the row itself has focus', () => {
      // The row is excluded from the control test on purpose: it carries a tab index, so
      // without that exclusion every event would look as though it came from a control
      // and no row could ever be selected.
      const host = useEditableColumn();
      const row = fixture.debugElement.query(By.css('tbody tr')).nativeElement as HTMLElement;
      const event = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });

      row.dispatchEvent(event);
      fixture.detectChanges();

      expect(host.selectedRows).toEqual([ROWS[0]]);
      expect(event.defaultPrevented).toBeTrue();
    });
  });
});
