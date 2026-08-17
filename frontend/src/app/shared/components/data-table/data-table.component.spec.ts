import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, TemplateRef, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

// The two shared contracts this spec asserts against. `SortDirection` is the wire sort vocabulary the
// component reports; `ProfilePropertyDefinition` is a REAL transfer contract used as a row shape, so that a
// mistyped column `field` is a compile error rather than a blank column found in a browser.
import { SortDirection } from '../../../core/models/paged-result.model';
import { ProfilePropertyDefinition } from '../../../core/models/profile.model';
import {
  DataTableCellContext,
  DataTableColumn,
  DataTableComponent,
  DataTableSortChange,
} from './data-table.component';

/**
 * Narrows a `querySelector` result to a present element, throwing when it is absent.
 *
 * @param root The element to search within.
 * @param selector The CSS selector to find.
 * @returns The matched element, guaranteed present.
 * @throws Error when the selector matches nothing, naming the selector that failed.
 */
function requireElement(root: Element, selector: string): Element {
  const found = root.querySelector(selector);
  if (found === null) {
    throw new Error(`Expected to find "${selector}" in the rendered template.`);
  }
  return found;
}

/**
 * The direction spellings this spec asserts against, taken from the WIRE CONTRACT rather than restated as
 * literals. Declared as typed constants so that a casing drift in the shared contract - `asc` for
 * `Ascending`, say - becomes a compile error here instead of a run-time `400` discovered in a browser.
 */
const ASCENDING: SortDirection = 'Ascending';

/** The descending member of the wire sort vocabulary. @see ASCENDING. */
const DESCENDING: SortDirection = 'Descending';

/**
 * A row shape carrying one of each value kind the text conversion covers, plus an identifier so the
 * sentinel cases can be exercised. Typed as a real interface rather than an index signature on purpose:
 * that is what makes a mistyped `field` a compile error, which an index-signature row could never deliver
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
   * A large integer, present so the text conversion's `bigint` clause is reachable through a real bound
   * member rather than through a cast formatter.
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
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // Standalone component, so it is IMPORTED. No declaration list and no module appear
      // anywhere in this workspace.
      imports: [DataTableComponent],

      // ORDER IS LOAD-BEARING: the real client is provided FIRST and the testing backend second, so the
      // testing backend overrides the live one. Reversed, the real `HttpBackend` survives and a spec that
      // made a request would attempt a live call.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent<DataTableComponent<Row>>(DataTableComponent);
    sorts = [];
    selected = [];
    fixture.componentInstance.sortChange.subscribe((change) => sorts.push(change));
    fixture.componentInstance.rowSelect.subscribe((row) => selected.push(row));

    fixture.componentRef.setInput('columns', baseColumns());
    fixture.componentRef.setInput('rows', ROWS);
    fixture.detectChanges();
  });

  // MANDATORY, and it applies to EVERY test in this describe rather than to the HTTP test alone.
  afterEach(() => {
    httpMock.verify();
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

  /** The component's rendered root, as an element the query helpers can search. */
  function host(): Element {
    return fixture.nativeElement as Element;
  }

  // ---------------------------------------------------------------------------
  // THE ROW SET THE TABLE DESCRIBES, THE ORDER IT ANNOUNCES, AND THE REGION IT SCROLLS
  // ---------------------------------------------------------------------------

  describe('the row set it describes to assistive technology', () => {
    function tableElement(): Element {
      return requireElement(host(), 'table.data-table');
    }

    function rowIndices(): readonly (string | null)[] {
      return bodyRows().map((row) => row.getAttribute('aria-rowindex'));
    }

    it('reports the match-set size, not the page, when a total is stated', () => {
      // ⚠ THE MEASURED DEFECT: on the second page of a seventeen-record listing this reported eight while
      // the pager beside it reported seventeen, so a screen reader was told the size of the window instead
      // of the size of the set - which is the one thing `aria-rowcount` exists to prevent.
      set('totalCount', 34);

      expect(tableElement().getAttribute('aria-rowcount'))
        .withContext('the match set plus the heading row')
        .toBe('35');
    });

    it('numbers rows from their position in the match set, not from the top of the page', () => {
      // The second page of a two-row page size: the first row rendered is the eleventh record, so it is row
      // twelve once the heading row is counted. Before this it announced as row two on every page.
      set('totalCount', 34);
      set('rowOffset', 10);

      expect(rowIndices()).toEqual(['12', '13']);
    });

    it('keeps describing its own body when the consumer states no total', () => {
      // The narrowing case that keeps the change honest: a listing that does not page is not a window onto
      // anything, so its page IS the set and the previous behaviour is the correct behaviour.
      set('totalCount', null);

      expect(tableElement().getAttribute('aria-rowcount')).toBe('3');
      expect(rowIndices()).toEqual(['2', '3']);
    });

    it('describes only the message row while a message stands in for the records', () => {
      // Announcing a set size beside "no records found" would offer a total with nothing to index into.
      set('totalCount', 34);
      set('rows', []);

      expect(tableElement().getAttribute('aria-rowcount')).toBe('2');
    });

    it('treats an unusable offset as the first page rather than raising', () => {
      // The only way one can arrive is a page coordinate that has not resolved, and a first page is the
      // truthful reading of "not yet known".
      set('totalCount', 34);
      set('rowOffset', -5);

      expect(rowIndices()).withContext('negative').toEqual(['2', '3']);

      set('rowOffset', Number.NaN);

      expect(rowIndices()).withContext('not a number').toEqual(['2', '3']);
    });
  });

  describe('the order it announces', () => {
    function announcementText(): string {
      return (host().querySelector('.data-table__result-status')?.textContent ?? '').trim();
    }

    it('announces the active order alongside the count', () => {
      set('sortBy', 'name');
      set('sortDir', 'Ascending');

      expect(announcementText()).toBe('2 records. Sorted by Name, ascending.');
    });

    it('changes the announcement when only the direction changes', () => {
      // ⚠ THIS IS THE DISCRIMINATING CASE, AND IT IS THE DEFECT ITSELF. Sorting moves rows without changing
      // how many there are, so before the order was folded in, this region's text was byte-identical before
      // and after a sort - and an unchanged live region announces NOTHING. Measured with an observer over
      // every live region on the page across two sort toggles: the only region that changed was this one,
      // and it changed to the same text it already held.
      set('sortBy', 'name');
      set('sortDir', 'Ascending');

      const ascending = announcementText();

      set('sortDir', 'Descending');

      const descending = announcementText();

      expect(descending).not.toBe(ascending);
      expect(descending).toBe('2 records. Sorted by Name, descending.');
    });

    it('changes the announcement when only the ordered column changes', () => {
      set('sortBy', 'name');

      const byName = announcementText();

      set('sortBy', 'count');

      expect(announcementText()).not.toBe(byName);
      expect(announcementText()).toBe('2 records. Sorted by Count, ascending.');
    });

    it('says nothing about order when the rows are in no stated order', () => {
      expect(announcementText()).toBe('2 records.');
    });

    it('says nothing about order when the ordered column is not one this table offers', () => {
      // A consumer may hold a sort name this column set does not publish - a saved query, or a name the
      // wire accepts and the screen does not show. Naming it would describe something a person cannot see.
      set('sortBy', 'somethingElse');

      expect(announcementText()).toBe('2 records.');

      // And a column that exists but is not sortable is the same case.
      set('sortBy', 'active');

      expect(announcementText()).toBe('2 records.');
    });
  });

  describe('the scrolling region', () => {
    /** The scrolling container, narrowed by a runtime check rather than by an assertion. */
    function container(): HTMLElement {
      const found: Element = requireElement(host(), '.data-table__container');

      if (found instanceof HTMLElement) {
        return found;
      }

      throw new Error('Expected the data-table container to be an HTML element.');
    }

    /**
     * Forces the container's measured widths and lets the component's own measurement frame run.
     *
     * ⚠ THE WIDTHS ARE STUBBED RATHER THAN PRODUCED BY LAYOUT, and deliberately so: whether a real table
     * clips depends on the harness's viewport, which a specification must not depend on. What is under test
     * is the DECISION the component makes from a measurement, so the measurement is supplied and the
     * decision is observed.
     */
    async function settleWithHiddenWidth(hidden: number): Promise<void> {
      const element = container();

      Object.defineProperty(element, 'clientWidth', { configurable: true, value: 300 });
      Object.defineProperty(element, 'scrollWidth', { configurable: true, value: 300 + hidden });

      // Any `rows` write schedules the component's measurement frame, which is the same frame the row
      // window is measured on.
      set('rows', [...ROWS]);

      await new Promise<void>((resolve) => {
        window.requestAnimationFrame(() => resolve());
      });

      fixture.detectChanges();
    }

    it('is not focusable and not a region while nothing is clipped', async () => {
      // ⚠ THE REGRESSION THIS GUARDS. Measured at 1280 the container's scroll width and client width were
      // both 1046 - nothing hidden - so a permanent focus stop would add an unusable stop to every listing
      // at every width in order to serve the narrow ones.
      await settleWithHiddenWidth(0);

      expect(container().getAttribute('tabindex')).toBeNull();
      expect(container().getAttribute('role')).toBeNull();
      expect(container().getAttribute('aria-labelledby')).toBeNull();
    });

    it('is not focusable for a sub-pixel difference either', async () => {
      // A fixed table layout resolving fractional track widths leaves sub-pixel differences on widths where
      // nothing is actually clipped.
      await settleWithHiddenWidth(1);

      expect(container().getAttribute('tabindex')).toBeNull();
    });

    it('becomes a focusable named region once content is clipped', async () => {
      // Measured at 375 on the account listing: 648 against 326, hiding 322 pixels and four whole columns,
      // with no `tabindex`, no `role` and no accessible name - so a keyboard user could not scroll it at
      // all, although arrow keys worked once focus was forced into it.
      await settleWithHiddenWidth(322);

      expect(container().getAttribute('tabindex')).withContext('reachable by Tab').toBe('0');
      expect(container().getAttribute('role')).toBe('region');

      const named = container().getAttribute('aria-labelledby');
      const caption: Element = requireElement(host(), 'caption.data-table__caption');

      expect(named).withContext('named by its own caption').toBe(caption.id);
      expect(caption.id.length).withContext('and that caption really carries an id').toBeGreaterThan(0);
    });

    it('actually accepts focus once it is a region', async () => {
      await settleWithHiddenWidth(322);

      container().focus();

      expect(document.activeElement).toBe(container());
    });
  });

  describe('the component takes no data dependency of any kind', () => {
    it('performs no HTTP requests while rendering, sorting or selecting', () => {
      // Exercise every interactive path the component has, so the claim covers the whole
      // surface rather than the initial render alone.
      const sortControl = requireElement(headers()[0], 'button');
      (sortControl as HTMLButtonElement).click();
      bodyRows()[0].click();
      bodyRows()[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      set('loading', true);
      set('loading', false);
      fixture.detectChanges();

      expect(httpMock.match(() => true))
        .withContext('no request of any kind, through any interaction')
        .toEqual([]);
    });

    it('injects no service, so it can be created with no provider but the HTTP pair', () => {
      // The component was constructed in `beforeEach` from a testing module registering nothing except the
      // HTTP backend - no store, no service, no router. Reaching a rendered table at all is therefore the
      // proof: an unmet dependency would have thrown during `createComponent`.
      expect(fixture.debugElement.query(By.css('table.data-table'))).not.toBeNull();
    });
  });

  describe('the surface is closed: no paging, no dialogue, no foot row', () => {
    it('renders no pagination component, because the pager is a sibling of the table', () => {
      expect(host().querySelector('app-pagination')).toBeNull();
    });

    it('renders no pagination in any state, including waiting and empty', () => {
      set('loading', true);
      expect(host().querySelector('app-pagination')).toBeNull();

      set('loading', false);
      set('rows', []);
      expect(host().querySelector('app-pagination')).toBeNull();
    });

    it('emits no foot row at all, so no pager can hide in one', () => {
      // The stronger of the two available forms: rather than proving a `tfoot` is free of a
      // pager, prove no `tfoot` is emitted in the first place.
      expect(host().querySelector('tfoot')).toBeNull();

      set('loading', true);
      expect(host().querySelector('tfoot')).toBeNull();

      set('loading', false);
      set('rows', []);
      expect(host().querySelector('tfoot')).toBeNull();
    });

    it('renders no confirmation dialogue, because the feature owns the delete flow', () => {
      expect(host().querySelector('app-confirm-dialog')).toBeNull();
    });

    it('borrows no legacy grid class name, so the token vocabulary is the only source', () => {
      const legacy = [
        '.DataGrid_Header',
        '.DataGrid_Item',
        '.DataGrid_AlternatingItem',
        '.DataGrid_SelectedItem',
        '.DataGrid_Footer',
        '.DataGrid_Container',
      ];

      for (const selector of legacy) {
        expect(host().querySelector(selector)).toBeNull();
      }
    });
  });

  describe('structure', () => {
    it('renders a real table, so row and column relationships survive', () => {
      expect(fixture.debugElement.query(By.css('table'))).not.toBeNull();
    });

    it('renders a caption element, which the consumer names by projection', () => {
      expect(fixture.debugElement.query(By.css('table > caption'))).not.toBeNull();
    });

    it('names the table generically when the consumer projects no caption', () => {
      // The omission case, which is the one that matters: this fixture is built with no projected content
      // at all, so the caption falls back.
      const caption = fixture.debugElement.query(By.css('table > caption'))
        .nativeElement as HTMLElement;

      expect((caption.textContent ?? '').trim()).toBe('Data table');
    });

    it('keeps the fallback name out of the painted output but in the accessibility tree', () => {
      // The fallback must not become visible text, yet must still NAME the table.
      const caption = fixture.debugElement.query(By.css('table > caption'))
        .nativeElement as HTMLElement;

      // The documented hook is present, so the caption is clipped rather than removed.
      expect(caption.hasAttribute('data-visually-hidden')).toBeTrue();

      // And it still carries the accessible name, which is the point of keeping it at all.
      expect((caption.textContent ?? '').trim()).toBe('Data table');

      // None of the mechanisms that would strip it from the accessibility tree is declared.
      expect(caption.hasAttribute('hidden')).toBeFalse();
      expect(caption.getAttribute('aria-hidden')).toBeNull();
      expect(caption.style.display).toBe('');
      expect(caption.style.visibility).toBe('');
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
      for (const width of ['min-content', 'max-content', 'var(--space-8)', '12.5%'] as const) {
        set('columns', [{ key: 'name', label: 'Name', field: 'name', width }]);

        const col = fixture.debugElement.query(By.css('colgroup > col'))
          .nativeElement as HTMLTableColElement;

        expect(col.style.inlineSize).toBe(width);
      }
    });
  });

  describe('column-set validation', () => {
    // Each invariant below is rejected when the SET IS BOUND rather than absorbed during rendering, so the
    // defect is reported at the call site that caused it.
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

    it('counts the waiting row when it is shown, so the announced size matches what is rendered', () => {
      // THE PLACEHOLDER ONLY REPLACES THE ROWS WHEN THERE ARE NO ROWS. A read that arrives while rows are
      // on screen keeps them and reports itself through `aria-busy`, so the waiting row has to be provoked
      // with an empty set.
      set('rows', []);
      set('loading', true);
      const table = fixture.debugElement.query(By.css('table')).nativeElement as HTMLElement;

      expect(bodyRows().length).toBe(1);
      expect(table.getAttribute('aria-rowcount')).toBe('2');
      expect(bodyRows()[0].getAttribute('aria-rowindex')).toBe('2');
    });

    it('keeps the rows and marks the table busy when a read arrives over existing rows', () => {
      const before = bodyRows().length;

      set('loading', true);
      const table = fixture.debugElement.query(By.css('table')).nativeElement as HTMLElement;

      expect(bodyRows().length)
        .withContext('the rows an operator is reading are never blanked out from under them')
        .toBe(before);
      expect(table.getAttribute('aria-busy'))
        .withContext('assistive technology is told the region is updating instead')
        .toBe('true');

      set('loading', false);

      expect(table.getAttribute('aria-busy'))
        .withContext('absent rather than present-and-negative when idle')
        .toBeNull();
    });

    it('still counts the RETAINED rows while a read is in flight over them', () => {
      // ⚠ THE REGRESSION THIS PINS DOWN, MEASURED IN A BROWSER. Once a subsequent read began keeping the
      // previous rows, this count still assumed the placeholder had replaced them - so for the whole two
      // seconds of a page turn the table announced `aria-rowcount="2"` while eleven rows were rendered, and
      // announced it at exactly the moment `aria-busy="true"` invites assistive technology to re-read the
      // grid.
      const table = fixture.debugElement.query(By.css('table')).nativeElement as HTMLElement;

      expect(table.getAttribute('aria-rowcount')).toBe('3');

      set('loading', true);

      expect(bodyRows().length).withContext('two records plus no placeholder').toBe(2);
      expect(table.getAttribute('aria-rowcount'))
        .withContext('what is announced is what is rendered')
        .toBe('3');
      expect(table.getAttribute('aria-busy')).toBe('true');
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
      // A column declaring BOTH a bound member and a formatter no longer compiles: the descriptor's text
      // arms exclude one another, which is why the earlier "formatter wins" precedence rule has no test any
      // more — the ambiguity it resolved is unrepresentable. Both sources still work on their own.
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
    // Interpolation escapes, so these are REGRESSION GUARDS rather than assertions about a defect: they
    // fail as soon as any of these three values stops being interpolated - the changes that would do it
    // being a raw-HTML property binding, a sanitiser bypass, a trusted-HTML wrapper or markup smuggled
    // through an attribute binding.
    const SCRIPT_PAYLOAD = '<script>window.__dataTableXss = true;</script>';
    const IMAGE_PAYLOAD = '<img src="x" onerror="window.__dataTableXss = true">';

    /** Benign-looking markup, and the most instructive of the three payloads. */
    const MARKUP_PAYLOAD = '<b>x</b>';

    function scriptCount(): number {
      return (fixture.nativeElement as HTMLElement).querySelectorAll('script').length;
    }

    function imageCount(): number {
      return (fixture.nativeElement as HTMLElement).querySelectorAll('img').length;
    }

    it('renders a markup-bearing cell value verbatim and creates no element in that cell', () => {
      set('columns', [{ key: 'name', label: 'Name', field: 'name' }]);
      set('rows', [{ ...ROWS[0], name: MARKUP_PAYLOAD }]);

      // Scoped to the CELL rather than to the document, which is the stricter claim: a
      // document-wide query would also pass if the element had been created somewhere else.
      const cell = requireElement(bodyRows()[0], 'td');

      expect((cell.textContent ?? '').trim()).toBe(MARKUP_PAYLOAD);
      expect(cell.querySelector('b')).toBeNull();
    });

    it('renders a markup-bearing column heading verbatim and creates no element in it', () => {
      // A heading is rendered by a DIFFERENT template branch from a cell and is just as
      // caller-supplied, so escaping has to be proved on both paths independently.
      set('columns', [{ key: 'name', label: MARKUP_PAYLOAD, field: 'name' }]);

      expect(textOf(headers()[0])).toBe(MARKUP_PAYLOAD);
      expect(headers()[0].querySelector('b')).toBeNull();
    });

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
      set('columns', [{ key: 'summary', label: 'Summary', value: () => SCRIPT_PAYLOAD }]);

      expect(scriptCount()).toBe(0);
      expect(cellTexts(0)[0]).toBe(SCRIPT_PAYLOAD);
    });

    it('creates no element from an event-handler payload either', () => {
      // A script element injected after load does not execute in every browser, so an assertion resting on
      // scripts alone could pass for the wrong reason. An image with a failing source and an error handler
      // needs no such caveat: were the markup live, the element would exist and its handler would run.
      set('columns', [{ key: 'name', label: IMAGE_PAYLOAD, field: 'name' }]);
      set('rows', [{ ...ROWS[0], name: IMAGE_PAYLOAD }]);

      expect(imageCount()).toBe(0);
      expect(cellTexts(0)[0]).toBe(IMAGE_PAYLOAD);
      expect(textOf(headers()[0])).toBe(IMAGE_PAYLOAD);
    });

    it('leaves no trace of any payload having executed', () => {
      // The guard behind the guards: if any case above had rendered live markup, the handler it carries
      // would have set this member. Read through an index signature because it is deliberately not a
      // declared global.
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

    /**
     * THE THIRD STEP CLEARS THE ORDERING, AND THIS SPEC USED TO PIN A TWO-STEP CYCLE THAT MADE THE
     * ARRIVAL STATE UNREACHABLE. Every listing in this application starts with no ordering at all - each
     * store initialises its sort coordinate to null and the request omits both parameters - so "no
     * ordering" is a state the reader is already in when they arrive.
     */
    it('clears the ordering on the column already sorted descending', () => {
      set('sortBy', 'name');
      set('sortDir', 'Descending');

      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([{ key: 'name', direction: null }]);
    });

    it('re-enters the cycle at ascending once the ordering has been cleared', () => {
      // A cleared column is no longer the active one, so the full round trip is ascending -> descending ->
      // cleared -> ascending, and the reader can reach all three states from the keyboard with repeated
      // presses of one heading.
      set('sortBy', 'name');
      set('sortDir', 'Ascending');
      headers()[0].querySelector('button')?.click();

      set('sortDir', 'Descending');
      headers()[0].querySelector('button')?.click();

      // The consumer has cleared the coordinate, which is what the null direction asked for.
      set('sortBy', null);
      set('sortDir', null);
      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([
        { key: 'name', direction: 'Descending' },
        { key: 'name', direction: null },
        { key: 'name', direction: 'Ascending' },
      ]);
      expect(headers()[0].getAttribute('aria-sort'))
        .withContext('a cleared column announces "sortable but not sorted", never a direction')
        .toBe('none');
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

    // ⚠ #32 — THE INDICATOR REPORTS THE ROWS ON SCREEN, NOT THE REQUEST. A screen moves its own query state the
    // instant a heading is pressed, before the read it triggers has returned anything, so the arrow and
    // `aria-sort` used to move immediately and STAY there when the read failed - the table asserting an order
    // its contents did not have, in the one situation where a reader most needs to trust it.
    it('holds its reported order until the rows that answer a new sort arrive', () => {
      set('rows', ROWS);
      set('sortBy', 'name');
      set('sortDir', 'Ascending');

      expect(headers()[0].getAttribute('aria-sort'))
        .withContext('the settled state: rows in place, name ascending')
        .toBe('ascending');

      // The press. A screen marks itself busy and moves its query state in the SAME change pass.
      fixture.componentRef.setInput('loading', true);
      fixture.componentRef.setInput('sortBy', 'count');
      fixture.detectChanges();

      expect(headers()[0].getAttribute('aria-sort'))
        .withContext('the old column keeps the claim while the answer is outstanding')
        .toBe('ascending');
      expect(headers()[1].getAttribute('aria-sort'))
        .withContext('and the requested column does not claim an order it has not delivered')
        .toBe('none');

      // The read FAILS: the screen stops waiting and re-binds nothing, because nothing arrived.
      fixture.componentRef.setInput('loading', false);
      fixture.detectChanges();

      expect(headers()[0].getAttribute('aria-sort'))
        .withContext('a failed read leaves the indicator exactly where the rows are')
        .toBe('ascending');
      expect(headers()[1].getAttribute('aria-sort')).toBe('none');

      // The retry SUCCEEDS: rows arrive, and arriving rows are what commits a sort.
      set('rows', [...ROWS].reverse());

      expect(headers()[1].getAttribute('aria-sort'))
        .withContext('the new order is reported once it is the order on screen')
        .toBe('ascending');
      expect(headers()[0].getAttribute('aria-sort')).toBe('none');
    });

    // ⚠ #26 — SORTING WAS THE ONE INTERACTION THAT ANNOUNCED NOTHING. Paging and filtering change the numbers
    // in the row-count status, so its live region re-reads them; sorting changes only the ORDER, so the text it
    // produced was identical to the text already there and a live region whose contents do not change is
    // silent. The ordering is named in the same status, so the count and the order are announced together.
    it('names the ordering in its live status, so a sort is announced at all', () => {
      set('rows', ROWS);

      const status = (): string =>
        (
          fixture.debugElement.query(By.css('.data-table__result-status'))?.nativeElement as HTMLElement
        ).textContent?.trim() ?? '';

      expect(status())
        .withContext('an unsorted listing reports its count and claims no order')
        .toBe('2 records.');

      set('sortBy', 'name');
      set('sortDir', 'Descending');

      expect(status())
        .withContext('the column is named by its own visible heading, and the direction in words')
        .toBe('2 records. Sorted by Name, descending.');

      set('sortDir', 'Ascending');

      expect(status())
        .withContext('flipping the direction changes the text, which is what makes it re-announce')
        .toBe('2 records. Sorted by Name, ascending.');
    });

    it('never reorders the array it was given', () => {
      const original = [...ROWS];
      set('sortBy', 'name');

      headers()[0].querySelector('button')?.click();
      fixture.detectChanges();

      expect(ROWS).toEqual(original);
      expect(cellTexts(0)[0]).toBe('Alpha');
    });

    /**
     * THE CONTROL IS MARKED UNAVAILABLE AND STAYS FOCUSABLE, and this spec used to pin the native
     * property that caused a measured focus defect: the platform blurs an element the instant it becomes
     * disabled, so pressing a heading reordered the table and then dropped focus to the document body,
     * leaving a keyboard reader with no position in the table they had just reordered - and nothing to
     * restore, because the focus was already gone by the time anything could observe it.
     */
    it('marks the control unavailable while a request is in flight, without disabling it', () => {
      set('loading', true);

      const control = headers()[0].querySelector('button');

      expect(control?.getAttribute('aria-disabled'))
        .withContext('the state is announced')
        .toBe('true');
      expect(control?.disabled)
        .withContext('but the native property is NOT used — it would destroy focus')
        .toBeFalse();
      expect(control?.hasAttribute('tabindex'))
        .withContext('and nothing removes it from the natural tab order')
        .toBeFalse();
      });

    it('emits no attribute at all when no request is in flight', () => {
      // `aria-disabled="false"` on every heading of every table would be noise; the attribute is bound
      // to null and Angular omits it.
      expect(headers()[0].querySelector('button')?.hasAttribute('aria-disabled')).toBeFalse();
    });

    it('withdraws the busy state once the request settles', () => {
      set('loading', true);
      set('loading', false);

      expect(headers()[0].querySelector('button')?.hasAttribute('aria-disabled')).toBeFalse();
    });

    /**
     * The geometry half of the same family of defect. Measured with the direction indicator rendered only
     * once a column becomes sorted: activating a heading grew its button under the pointer — Title
     * 44.000 -> 46.469 px and Start Date 70.859 -> 86.750 px.
     */
    it('keeps the indicator element present when unsorted, so the control cannot change size', () => {
      const indicatorOf = (index: number): Element | null =>
        headers()[index].querySelector('.data-table__sort-indicator');

      expect(indicatorOf(0)).withContext('present while unsorted').not.toBeNull();
      expect((indicatorOf(0)?.textContent ?? '').trim())
        .withContext('but carrying no glyph')
        .toBe('');

      set('sortBy', 'name');
      set('sortDir', 'Ascending');

      expect(indicatorOf(0)).withContext('still one element once sorted').not.toBeNull();
      expect((indicatorOf(0)?.textContent ?? '').trim())
        .withContext('now carrying the ascending glyph')
        .toBe('\u25B2');
      expect(indicatorOf(0)?.getAttribute('aria-hidden'))
        .withContext('and never announced — the heading already states the direction')
        .toBe('true');
    });

    /** The whole point of the change above: focus SURVIVES the request that a sort press starts. */
    it('keeps focus on the heading the reader pressed while the reorder is in flight', () => {
      const control = headers()[0].querySelector('button');

      control?.focus();
      control?.click();

      // The consumer answers by reporting a request in flight, exactly as a feature does.
      set('loading', true);

      expect(document.activeElement)
        .withContext('focus must not fall to the document body')
        .toBe(control);
    });

    it('emits nothing while a request is in flight, so a second order cannot be queued', () => {
      set('loading', true);

      headers()[0].querySelector('button')?.click();

      expect(sorts).toEqual([]);
    });

    describe('keyboard operability of the sort control', () => {
      // WHY THIS IS ASSERTED SEPARATELY FROM THE CLICK TESTS ABOVE. Those prove the sort CONTRACT - which
      // key, which direction, which column.

      /** The sortable heading control for a column index. */
      function sortControl(columnIndex: number): HTMLButtonElement {
        const element = requireElement(headers()[columnIndex], 'button');

        if (element instanceof HTMLButtonElement === false) {
          throw new Error('The sortable heading control is not a button element.');
        }

        return element;
      }

      /**
       * Activates a native button the way a keyboard user does. A synthetic `KeyboardEvent` dispatched
       * from script is untrusted, so the user agent performs NO default action for it - the
       * Enter-to-click translation a real key press gets is simply not applied.
       *
       * @param button The control to activate.
       * @param key The activation key to press.
       * @returns Whether the key event survived uncancelled and the activation was performed.
       */
      function pressKey(button: HTMLButtonElement, key: string): boolean {
        button.focus();

        const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
        const survived = button.dispatchEvent(event);

        if (survived) {
          button.click();
        }

        fixture.detectChanges();

        return survived;
      }

      it('exposes the sort affordance as a real button, which the platform makes operable', () => {
        const control = sortControl(0);

        // A native button is focusable and answers both Enter and Space with no key handler written
        // anywhere. An activatable `th` would need a tab index AND a hand-rolled key handler to reach the
        // same place, and would still not be announced as a control.
        expect(control.tagName).toBe('BUTTON');
        expect(control.type).toBe('button');
      });

      it('places the sort control in the tab order without an author-supplied tab index', () => {
        const control = sortControl(0);

        expect(control.hasAttribute('tabindex')).toBeFalse();

        control.focus();
        expect(document.activeElement).toBe(control);
      });

      it('emits the ascending order from an Enter press on the focused heading', () => {
        const survived = pressKey(sortControl(0), 'Enter');

        expect(survived).toBeTrue();
        expect(sorts).toEqual([{ key: 'name', direction: ASCENDING }]);
      });

      it('emits the ascending order from a Space press on the focused heading', () => {
        const survived = pressKey(sortControl(0), ' ');

        expect(survived).toBeTrue();
        expect(sorts).toEqual([{ key: 'name', direction: ASCENDING }]);
      });

      it('flips the active column to descending from the keyboard', () => {
        set('sortBy', 'name');
        set('sortDir', ASCENDING);

        pressKey(sortControl(0), 'Enter');

        expect(sorts).toEqual([{ key: 'name', direction: DESCENDING }]);
      });

      it('never mutates the rows it was given when sorted from the keyboard', () => {
        // Captured BEFORE activation: the array identity, and a copy of its element order.
        // Sorting is EMITTED, never performed, so all three must be unchanged afterwards.
        const boundRows = fixture.componentInstance.rows;
        const orderBefore = [...boundRows];

        pressKey(sortControl(0), 'Enter');

        expect(fixture.componentInstance.rows).toBe(boundRows);
        expect([...fixture.componentInstance.rows]).toEqual(orderBefore);
        expect(ROWS[0].id).toBe(0);
        expect(ROWS[1].id).toBe(-1);
      });

      it('does not update aria-sort optimistically after a keyboard activation', () => {
        // The two sort inputs are the SOLE source of truth for the announcement. With them unchanged, the
        // heading must still announce what it was told, not what was asked for - otherwise a heading would
        // announce an order whose request had failed.
        set('sortBy', 'name');
        set('sortDir', ASCENDING);

        pressKey(sortControl(0), 'Enter');

        expect(sorts).toEqual([{ key: 'name', direction: DESCENDING }]);
        expect(headers()[0].getAttribute('aria-sort')).toBe('ascending');
      });

      /**
       * ⚠ REWRITTEN alongside its sibling above. The control no longer takes the native `disabled`
       * property, because setting it on the focused heading destroyed keyboard focus.
       */
      it('refuses keyboard activation while a request is in flight, and keeps focus', () => {
        set('loading', true);

        // ⚠ THE GUARD IS NOW ENTIRELY IN THE COMPONENT, and the assertion had to move with it.
        const control = sortControl(0);

        expect(control.getAttribute('aria-disabled'))
          .withContext('the state is announced without the native property')
          .toBe('true');
        expect(control.disabled)
          .withContext('while the native property stays off, so focus survives')
          .toBeFalse();

        control.focus();
        expect(document.activeElement).withContext('focus starts on the heading').toBe(control);

        control.focus();
        pressKey(control, 'Enter');

        expect(sorts).withContext('and the ordering is still refused').toEqual([]);
        expect(document.activeElement)
          .withContext('focus is NOT ejected to the document body')
          .toBe(control);
      });

      /**
       * The successful path's focus behaviour, which is where the reported defect actually bit: a reader
       * pressed Enter, the sort succeeded, and focus was gone. Asserted through the component rather than
       * through a live request, by driving the busy state the way the feature does.
       */
      it('keeps focus on the heading across a completed ordering', () => {
        const control = sortControl(0);

        control.focus();
        pressKey(control, 'Enter');

        set('loading', true);
        set('loading', false);

        expect(sorts.length).withContext('the ordering was requested').toBe(1);
        expect(document.activeElement)
          .withContext('and the heading still has focus afterwards')
          .toBe(control);
      });

      /**
       * The accessible name states the ACTION and CONTAINS the visible label verbatim. WCAG 2.5.3 Label
       * in Name: a voice-control user says the words they can see, so a name that replaced the visible
       * heading text rather than extending it would break them.
       */
      it('names the control by its action while still containing the visible column label', () => {
        set('sortBy', 'name');
        set('sortDir', 'Descending');

        const control = sortControl(0);
        const accessibleName = control.getAttribute('aria-label') ?? '';
        const visibleLabel = (control.textContent ?? '').trim();

        expect(accessibleName).toBe('Sort by Name');
        expect(visibleLabel.startsWith('Name')).withContext('the visible text is the label').toBeTrue();
        expect(accessibleName)
          .withContext('WCAG 2.5.3: the accessible name contains the visible label verbatim')
          .toContain('Name');
        expect(accessibleName)
          .withContext('the direction is announced by aria-sort, not restated in the name')
          .not.toContain('escending');
      });

      it('gives an unsortable column no sort control and therefore no such name', () => {
        // The name exists only where the control does; the plain heading branch renders a span.
        const plain = headers()[2].querySelector('button');

        expect(plain).toBeNull();
      });
    });

    it('declares an explicit control type so it cannot submit a surrounding form', () => {
      expect(headers()[0].querySelector('button')?.getAttribute('type')).toBe('button');
    });
  });

  describe('waiting and empty states', () => {
    it('shows the shared indicator on the FIRST read, when there is no previous page', () => {
      set('rows', []);
      set('loading', true);

      expect(fixture.debugElement.query(By.css('app-loading-spinner'))).not.toBeNull();
      expect(bodyRows().length).toBe(1);
    });

    it('spans the waiting cell across every column, so no blank cells are announced', () => {
      // Provoked with an empty set: a read over existing rows keeps them - see the busy test.
      set('rows', []);
      set('loading', true);

      const cell = bodyRows()[0].querySelector('td');

      expect(cell?.getAttribute('colspan')).toBe('4');
    });

    it('shows the shared empty state when there is nothing and nothing is loading', () => {
      set('rows', []);

      expect(fixture.debugElement.query(By.css('app-empty-state'))).not.toBeNull();
    });

    // THE ASSERTIONS BELOW PROVE THE CHILDREN ARE THE REAL SHARED COMPONENTS, not merely that an element
    // with the right tag name is in the DOM. That distinction is worth asserting: a fake declared in a
    // spec, or an unrecognised element admitted by a permissive schema, satisfies a tag-name query
    // perfectly while rendering nothing at all.

    it('renders the REAL shared indicator, with its own default wording and status role', () => {
      set('rows', []);
      set('loading', true);

      const spinner = requireElement(host(), 'app-loading-spinner');

      // `role="status"` is the real component's own implicit polite live region, and the
      // default label is its own text. A stub would carry neither.
      expect(spinner.getAttribute('role')).toBe('status');
      expect(spinner.textContent ?? '').toContain('Loading');
    });

    it('renders the REAL shared empty state, with its own default message', () => {
      set('rows', []);

      const empty = requireElement(host(), 'app-empty-state');

      // The real component's documented fallback wording, which it supplies itself because
      // the table binds no message: it declares no message input.
      expect(empty.textContent ?? '').toContain('No records found.');
    });

    it('places the empty state in a body row spanning every column, never in a foot row', () => {
      set('columns', baseColumns());
      set('rows', []);

      const empty = requireElement(host(), 'app-empty-state');
      const cell = empty.closest('td');
      const section = empty.closest('tbody');

      if (cell === null || section === null) {
        throw new Error('The empty state is not inside a body cell.');
      }

      // Spans all four columns, so no real empty cells are left beside the message for a
      // screen reader to announce as blanks.
      expect(cell.getAttribute('colspan')).toBe('4');
      expect(section.tagName).toBe('TBODY');
      expect(empty.closest('tfoot')).toBeNull();
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

  // A FAILED READ IS NOT AN EMPTY PAGE
  // Zero rows has two causes that are indistinguishable from inside this component - nothing matched, or
  // nothing is known - and it used to present both as "Nothing to Display / No records found." Measured
  // consequences of that conflation, all on real screens: a swallowed HTTP 500 rendered pixel-identically
  // to a legitimately empty permission list; a Forbidden banner sat above "No records found." on a portal's
  // host names, asserting the portal has none when none had been retrieved; a 403 on an account's own
  // profile read as "This site declares no profile properties"; and the polite region announced "No records
  // found." while thirty roles existed.
  describe('a failed read, told apart from an empty page', () => {
    /** The polite live region's current sentence. */
    function announcement(): string {
      return (host().querySelector('.data-table__result-status')?.textContent ?? '').trim();
    }

    // ⚠ THE SINGLE-ANNOUNCEMENT CONTRACT. This region used to report only the CURRENT PAGE while the pager
    // announced the dataset total from a live region of its own, so one action produced two polite
    // announcements with a blank between them. The pager's region is now visible-only, which makes this the
    // one announcer - so it has to carry both facts.
    it('states the dataset total alongside the page count when a total is supplied', () => {
      set('rows', ROWS);
      set('totalCount', 34);

      expect(announcement()).toBe(`Showing ${ROWS.length} of 34 records.`);
    });

    it('states the plain count when the whole match set is on the page', () => {
      set('rows', ROWS);
      set('totalCount', ROWS.length);

      expect(announcement()).toBe(`${ROWS.length} records.`);
    });

    it('states the plain count for a listing that does not page at all', () => {
      set('rows', ROWS);

      // No total supplied: the table must speak only of the rows it was handed rather than inventing one.
      expect(announcement()).toBe(`${ROWS.length} records.`);
    });

    it('treats a sentinel total as absent rather than rendering it', () => {
      set('rows', ROWS);
      set('totalCount', -1);

      expect(announcement())
        .withContext('the legacy absent-integer marker must never reach a sentence')
        .toBe(`${ROWS.length} records.`);
    });

    it('withholds the empty state, because nobody may claim there is nothing', () => {
      set('rows', []);
      set('failed', true);

      expect(fixture.debugElement.query(By.css('app-empty-state')))
        .withContext('the panel that asserts "Nothing to Display" is not rendered')
        .toBeNull();
      expect(textOf(host().querySelector('.data-table__unread'))).toContain(
        'could not be read',
      );
    });

    it('announces that the records could not be read, and never that none were found', () => {
      set('rows', []);
      set('failed', true);

      expect(announcement()).toBe('The records could not be read.');
      expect(announcement()).not.toContain('No records found.');
    });

    it('keeps readable rows on screen and says they may be out of date', () => {
      // A failure that arrives while rows are shown must not withdraw information the reader already had,
      // exactly as a read in flight does not.
      set('failed', true);

      expect(bodyRows().length).withContext('the rows survive').toBe(ROWS.length);
      expect(host().querySelector('.data-table__unread'))
        .withContext('and no placeholder replaces them')
        .toBeNull();
      expect(announcement()).toContain('out of date');
    });

    it('prefers waiting over failed, so a re-read is not reported as a failure', () => {
      set('rows', []);
      set('failed', true);
      set('loading', true);

      expect(fixture.debugElement.query(By.css('app-loading-spinner'))).not.toBeNull();
      expect(host().querySelector('.data-table__unread')).toBeNull();
      expect(announcement()).withContext('and nothing is announced mid-read').toBe('');
    });

    it('returns to the empty state once the failure is cleared', () => {
      set('rows', []);
      set('failed', true);
      set('failed', false);

      expect(fixture.debugElement.query(By.css('app-empty-state'))).not.toBeNull();
      expect(announcement()).toBe('No records found.');
    });

    it('treats an absent binding as not failed, so no consumer has to pass one', () => {
      set('rows', []);
      set('failed', null);

      expect(fixture.debugElement.query(By.css('app-empty-state'))).not.toBeNull();
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
      // The current-item state marks a reader's position within a set of related items, which this
      // component does not model separately from selection. Publishing both announced one state twice in
      // two vocabularies, one of them a claim the component could not substantiate.
      bodyRows()[1].click();
      fixture.detectChanges();

      expect(bodyRows().some((row) => row.hasAttribute('aria-current'))).toBeFalse();
    });

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

      // The current-item state is deliberately NOT among them: it marks a reader's position within a set of
      // related items, which this component does not model separately from selection, so emitting it
      // alongside the selection state announced one state twice in two vocabularies - one of them a claim
      // the component could not substantiate.
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

  // ---------------------------------------------------------------------------
  // KEYED REUSE THAT REACHES THE DOM
  // ---------------------------------------------------------------------------

  describe('keyed reuse across a re-read', () => {
    // ⚠ THE PROPERTY UNDER TEST IS DOM-NODE IDENTITY, NOT RENDERED TEXT, and that is the whole point: the
    // rendered text was always right.

    /** A fresh object per row, decoded as an HTTP response would be, carrying the same identities. */
    function reDecoded(rows: readonly Row[]): readonly Row[] {
      return rows.map((row) => ({ ...row }));
    }

    /** The identity a listing would supply — the record's own key. */
    const identify = (row: Row): number => row.id;

    it('keeps every row element when the same records are re-read', () => {
      set('rowKey', identify);

      const before = bodyRows();

      expect(before.length).toBe(2);

      set('rows', reDecoded(ROWS));

      const after = bodyRows();

      // Compared BY OBJECT IDENTITY. Equal text would pass even against a full rebuild, which is the
      // false green this case exists to exclude.
      expect(after.length).toBe(2);
      expect(after[0]).toBe(before[0]);
      expect(after[1]).toBe(before[1]);
    });

    it('keeps the surviving rows when one record is removed, and only loses the removed one', () => {
      set('rowKey', identify);

      const before = bodyRows();
      const survivor = before[1];

      // The refetch after a removal: the same records come back minus one, as new objects.
      set('rows', reDecoded([ROWS[1]]));

      const after = bodyRows();

      expect(after.length).toBe(1);
      expect(after[0]).withContext('the record that remained kept its row element').toBe(survivor);
    });

    it('updates a reused row content rather than leaving it stale', () => {
      set('rowKey', identify);

      const before = bodyRows()[0];

      set('rows', [{ ...ROWS[0], name: 'Renamed' }, { ...ROWS[1] }]);

      // Reuse is only correct if the reused element shows the NEW values. A reused row that kept the old
      // text would be a worse defect than rebuilding.
      expect(bodyRows()[0]).toBe(before);
      expect(cellTexts(0)[0]).toBe('Renamed');
    });

    it('tolerates the sentinel identities as keys', () => {
      // Zero seeds the role, page and module identities and minus one seeds the portal identity, so both
      // are legitimate keys. A key implementation that treated either as absent would collapse the two rows
      // onto one key, which `@for` reports as a duplicate.
      set('rowKey', identify);

      const before = bodyRows();

      set('rows', reDecoded(ROWS));

      expect(bodyRows().length).toBe(2);
      expect(bodyRows()[0]).toBe(before[0]);
      expect(bodyRows()[1]).toBe(before[1]);
    });

    it('THE NEGATIVE CONTROL: rebuilds when no identity is supplied', () => {
      // Without this case the four above would pass against a table that reused rows for some unrelated
      // reason. The fallback is deliberately the previous behaviour, so a consumer that supplies nothing is
      // unaffected by the new input - and that is a property worth pinning, not an accident.
      const before = bodyRows();

      set('rows', reDecoded(ROWS));

      const after = bodyRows();

      expect(after.length).toBe(2);
      expect(after[0]).not.toBe(before[0]);
      expect(after[1]).not.toBe(before[1]);
      expect(cellTexts(0)[0]).withContext('and still renders correctly').toBe('Alpha');
    });

    it('publishes the identity it was given, and reports none as null', () => {
      expect(fixture.componentInstance.rowKey).toBeNull();

      set('rowKey', identify);
      expect(fixture.componentInstance.rowKey).toBe(identify);

      set('rowKey', undefined);
      expect(fixture.componentInstance.rowKey)
        .withContext('an absent value means key on the object, not a function that throws')
        .toBeNull();
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

    it('sorts each duplicated label by its OWN key, not by the label they share', () => {
      // THE DEFECT THIS CATCHES, restated because it is the whole reason the descriptor separates key from
      // label: `Website/admin/Security/roles.ascx` carries HeaderText "Every" at BOTH L45 (over
      // BillingPeriod) and L58 (over TrialPeriod), and HeaderText "Period" at BOTH L50 (over
      // BillingFrequency) and L63 (over TrialFrequency) - four columns, two labels, inside ONE grid.
      set('columns', [
        { key: 'billingPeriod', label: 'Every', field: 'count', sortable: true },
        { key: 'billingFrequency', label: 'Period', field: 'name', sortable: true },
        { key: 'trialPeriod', label: 'Every', field: 'count', sortable: true },
        { key: 'trialFrequency', label: 'Period', field: 'name', sortable: true },
      ]);

      expect(headers().length).toBe(4);

      for (const header of headers()) {
        (requireElement(header, 'button') as HTMLButtonElement).click();
      }

      fixture.detectChanges();

      expect(sorts).toEqual([
        { key: 'billingPeriod', direction: ASCENDING },
        { key: 'billingFrequency', direction: ASCENDING },
        { key: 'trialPeriod', direction: ASCENDING },
        { key: 'trialFrequency', direction: ASCENDING },
      ]);

      // Four activations, four DISTINCT keys. A label-keyed implementation would repeat a
      // key here even if it somehow rendered four headings.
      expect(new Set(sorts.map((change) => change.key)).size).toBe(4);
    });

    it('announces the active sort on only one of two columns sharing a label', () => {
      // With the label ambiguous, `sortBy` can only be resolved through the key. If the active-sort test
      // consulted the label, BOTH "Every" columns would announce themselves as sorted and a screen reader
      // would be told the table is ordered two ways at once.
      set('columns', [
        { key: 'billingPeriod', label: 'Every', field: 'count', sortable: true },
        { key: 'trialPeriod', label: 'Every', field: 'count', sortable: true },
      ]);
      set('sortBy', 'trialPeriod');
      set('sortDir', DESCENDING);

      expect(headers().map((header) => header.getAttribute('aria-sort'))).toEqual([
        'none',
        'descending',
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

  // ===================================================================================================
  // THE SCROLLING REGION — QA-10
  // ===================================================================================================

  describe('the scrolling region declares itself', () => {
    /** The container that scrolls, addressed through the contract attribute rather than the class. */
    function scrollHost(): HTMLElement {
      return requireElement(host(), '[data-table-scroll]') as HTMLElement;
    }

    /**
     * Forces the container to report an overflow and lets the component re-measure.
     *
     * The measurement is a real geometric one, so a spec cannot simply set a flag: the element's own
     * `scrollWidth` and `clientWidth` are read. Both are overridden here rather than sized with CSS,
     * because a headless layout at an arbitrary width is not a stable thing to assert against.
     *
     * @param overflowing Whether the container should report content wider than its box.
     */
    function reportOverflow(overflowing: boolean): void {
      const container = scrollHost();

      Object.defineProperty(container, 'clientWidth', { value: 400, configurable: true });
      Object.defineProperty(container, 'scrollWidth', {
        value: overflowing ? 900 : 400,
        configurable: true,
      });

      // The same entry point the resize observer uses, so the spec exercises the production path.
      (fixture.componentInstance as unknown as { measureScrollable(): void }).measureScrollable();
      fixture.detectChanges();
    }

    it('emits the contract attribute the shared table stylesheet publishes', () => {
      // ⚠ THE ATTRIBUTE IS UNCONDITIONAL AND THE ROLE IS NOT, WHICH IS THE WHOLE DESIGN. `_tables.scss`
      // declares a `[data-table-scroll]` block carrying the focus ring, and NO template in the application
      // emitted the attribute, so every line of it was dead CSS. It is emitted always so the styling always
      // applies; the landmark, the name and the tab stop are emitted only when there is something to scroll.
      expect(scrollHost().hasAttribute('data-table-scroll')).toBeTrue();
    });

    it('contributes no landmark and no tab stop while the table fits', () => {
      reportOverflow(false);

      const container = scrollHost();

      expect(container.hasAttribute('role')).withContext('no region to announce').toBeFalse();
      expect(container.hasAttribute('tabindex'))
        .withContext('and no tab stop, because there is nothing to scroll')
        .toBeFalse();
      expect(container.hasAttribute('aria-labelledby')).toBeFalse();
    });

    it('becomes a named, keyboard-reachable region once it overflows', () => {
      reportOverflow(true);

      const container = scrollHost();

      expect(container.getAttribute('role')).toBe('region');
      expect(container.getAttribute('tabindex'))
        .withContext('reachable by keyboard, not by pointer alone')
        .toBe('0');

      // The NAME is the table's own caption, resolved through the identifier rather than duplicated as a
      // string — so the region is named "Portals" on the portal listing without the shared component
      // knowing anything about portals.
      const caption = requireElement(host(), 'caption.data-table__caption');
      const labelledBy: string = container.getAttribute('aria-labelledby') ?? '';

      expect(labelledBy.length).withContext('the region names itself').toBeGreaterThan(0);
      expect(caption.id).toBe(labelledBy);
    });

    it('withdraws the region when the overflow goes away', () => {
      reportOverflow(true);
      reportOverflow(false);

      expect(scrollHost().hasAttribute('role')).toBeFalse();
      expect(scrollHost().hasAttribute('tabindex')).toBeFalse();
    });

    it('mints a caption identifier unique to each instance', () => {
      // Two tables on one screen — the role listing renders one and its group editor another — must not
      // point their regions at the same caption, which is what a constant identifier would do.
      const second = TestBed.createComponent<DataTableComponent<Row>>(DataTableComponent);
      second.componentRef.setInput('columns', baseColumns());
      second.componentRef.setInput('rows', ROWS);
      second.detectChanges();

      const first = requireElement(host(), 'caption.data-table__caption').id;
      const other = requireElement(
        second.nativeElement as Element,
        'caption.data-table__caption',
      ).id;

      expect(first).not.toBe(other);
      second.destroy();
    });
  });

  // ===================================================================================================
  // ATOMIC VALUES — QA-16
  // ===================================================================================================

  describe('an atomic column keeps its values whole', () => {
    /** The column set with the numeric column declared atomic. */
    function withAtomicCount(): void {
      set('columns', [
        { key: 'name', label: 'Name', field: 'name', sortable: true },
        { key: 'count', label: 'Count', field: 'count', sortable: true, atomic: true },
        { key: 'active', label: 'Active', field: 'active' },
        { key: 'note', label: 'Note', field: 'note' },
      ] satisfies readonly DataTableColumn<Row>[]);
    }

    it('publishes the state on the body cell, so the stylesheet can hold the value on one line', () => {
      // ⚠ WHY AN ATTRIBUTE RATHER THAN A CLASS. The rule it drives is in the SHARED table stylesheet, which
      // has no access to a component's encapsulated class names; an attribute contract crosses that boundary
      // and is the same mechanism `data-table-scroll` uses.
      withAtomicCount();

      const cells = Array.from(bodyRows()[0].querySelectorAll('td,th'));

      expect(cells[1]?.getAttribute('data-atomic')).toBe('true');
      expect(cells[0]?.hasAttribute('data-atomic'))
        .withContext('a column that did not ask for it carries nothing')
        .toBeFalse();
    });

    it('publishes it on the heading too, so a heading cannot break where its values cannot', () => {
      withAtomicCount();

      expect(headers()[1].getAttribute('data-atomic')).toBe('true');
      expect(headers()[0].hasAttribute('data-atomic')).toBeFalse();
    });

    it('emits nothing at all for an ordinary column, on every row', () => {
      // The default must be absence rather than `data-atomic="false"`: a false attribute on every cell of
      // every row of every table is pure weight, and the rule tests for presence.
      expect(host().querySelectorAll('[data-atomic]')).toHaveSize(0);
    });
  });

  // ===================================================================================================
  // THE ZERO-RESULT SURFACE — QA-28
  // ===================================================================================================

  describe('the empty surface carries the screen own wording and its own way forward', () => {
    it('passes a supplied message through to the shared empty state', () => {
      set('rows', []);
      set('emptyMessage', 'No accounts match the current filter.');

      expect(textOf(host().querySelector('app-empty-state'))).toContain(
        'No accounts match the current filter.',
      );
    });

    it('falls back to the shared default when a screen supplies nothing', () => {
      // Trimmed to empty is the same as absent, so a screen binding an unresolved signal does not blank the
      // sentence entirely.
      set('rows', []);
      set('emptyMessage', '   ');

      expect(fixture.componentInstance.emptyMessage).toBe('');
      expect(textOf(host().querySelector('app-empty-state')).length).toBeGreaterThan(0);
    });

    it('marks every sort control unavailable over an empty table', () => {
      // ⚠ SORTING NOTHING IS A REQUEST WITH NO ANSWER, AND IT USED TO BE OFFERED. Measured: the headings of an
      // empty grid were fully operable, so pressing one issued a re-read for an ordering of the empty set the
      // reader was already looking at — and announced a sort state for a table with no rows in it. The native
      // property is deliberately NOT used, for the reason the loading case documents: it destroys focus.
      set('rows', []);

      for (const header of headers()) {
        const control = header.querySelector('button');
        if (control === null) {
          continue;
        }

        expect(control.getAttribute('aria-disabled')).toBe('true');
        expect(control.disabled).withContext('focus must survive').toBeFalse();
      }
    });

    it('emits no sort and no request when a heading is pressed over an empty table', () => {
      set('rows', []);

      (headers()[0].querySelector('button') as HTMLButtonElement | null)?.click();
      fixture.detectChanges();

      expect(sorts).withContext('nothing to order, so nothing is asked for').toEqual([]);
    });

    it('restores the sort controls the moment rows arrive', () => {
      set('rows', []);
      set('rows', ROWS);

      expect(headers()[0].querySelector('button')?.hasAttribute('aria-disabled')).toBeFalse();
    });
  });

  // ⚠ MAJOR (Issue 22) — an empty listing was unreadable on a phone.
  describe('the message row on a viewport narrower than the table', () => {
    function container(): HTMLElement {
      const found: Element = requireElement(host(), '.data-table__container');

      if (found instanceof HTMLElement) {
        return found;
      }

      throw new Error('the scroll container is not an HTMLElement');
    }

    function messageViewport(): HTMLElement | null {
      const found = host().querySelector('.data-table__message-viewport');

      return found instanceof HTMLElement ? found : null;
    }

    /**
     * Renders with a stated amount of the table hidden, then settles the component's measurement frame.
     *
     * The two width properties are stubbed because a fixture cannot be given a 320-pixel viewport: what is
     * under test is the DECISION the component makes from a measurement, so the measurement is supplied and the
     * decision is observed.
     *
     * @param hidden How many pixels of the table are outside the scrollport.
     */
    async function settleWithHiddenWidth(hidden: number): Promise<void> {
      const element = container();

      Object.defineProperty(element, 'clientWidth', { configurable: true, value: 300 });
      Object.defineProperty(element, 'scrollWidth', { configurable: true, value: 300 + hidden });

      set('rows', []);

      await new Promise<void>((resolve) => {
        window.requestAnimationFrame(() => resolve());
      });

      fixture.detectChanges();
    }

    /** The container's own inline padding, which the wrapper's width has to exclude. */
    function inlinePadding(): number {
      const resolved: CSSStyleDeclaration = getComputedStyle(container());

      return Number.parseFloat(resolved.paddingLeft) + Number.parseFloat(resolved.paddingRight);
    }

    it('pins the message to the scrollport when the table is wider than it', async () => {
      await settleWithHiddenWidth(340);

      const viewport = messageViewport();

      if (viewport === null) {
        throw new Error('the message wrapper did not render');
      }

      const resolved: CSSStyleDeclaration = getComputedStyle(viewport);

      // ⚠ STICKY IS THE WHOLE MECHANISM. Measured at 320 and 375 before the fix, the stand-in panel was laid
      // out 608 pixels wide inside a scrollport of 271 and 326 - because the table is deliberately floored at
      // `--table-min-inline-size` so that columns SCROLL rather than crush - and because the panel centres its
      // own contents the heading began beyond the right edge and rendered as "Nothin" and "No". Pinning the
      // wrapper to the scrollport keeps the sentence on screen at any scroll position without touching that
      // floor, so nothing about how rows of data lay out changes.
      expect(resolved.position).toBe('sticky');
      expect(Number.parseFloat(resolved.insetInlineStart)).toBe(0);
    });

    it('gives the message exactly the visible width, excluding the container padding', async () => {
      await settleWithHiddenWidth(340);

      const viewport = messageViewport();

      if (viewport === null) {
        throw new Error('the message wrapper did not render');
      }

      // Self-validating rather than hardcoded: the expectation is derived from the container's own padding, so
      // the specification cannot drift from the token if the padding is ever retuned.
      const padding: number = inlinePadding();

      expect(padding).withContext('the container carries inline padding').toBeGreaterThan(0);

      // ⚠ THE SUBTRACTION IS THE DISCRIMINATING PART, and a browser measurement is what put it here.
      // `clientWidth` INCLUDES the container's padding while the wrapper begins after it, so handing over the
      // raw value overhung the visible padding box by exactly 4 pixels at each edge. Asserting only "a width is
      // applied" would pass under that.
      expect(viewport.style.inlineSize).toBe(`${300 - padding}px`);
    });

    it('imposes no width at all when the table already fits', async () => {
      // The narrowing, and the reason the width is conditional. At 1280 the scrollport and the table are the
      // same width; imposing one there would pin a box to a scrollport that never scrolls and would freeze the
      // panel at a width the cell could otherwise fill. Measured on the live page at 1280 the wrapper's `style`
      // attribute is null, and that is what this asserts.
      await settleWithHiddenWidth(0);

      const viewport = messageViewport();

      if (viewport === null) {
        throw new Error('the message wrapper did not render');
      }

      expect(viewport.style.inlineSize).toBe('');
    });

    it('wraps all three message states, not just the empty one', async () => {
      // One wrapper serves the loading spinner, the failed-read sentence and the empty state. A fix applied to
      // only the empty state would leave the other two unreadable in exactly the same way.
      await settleWithHiddenWidth(340);
      expect(messageViewport()).withContext('the empty state is wrapped').not.toBeNull();
      expect(host().querySelector('.data-table__message-viewport app-empty-state')).not.toBeNull();

      set('loading', true);
      await new Promise<void>((resolve) => {
        window.requestAnimationFrame(() => resolve());
      });
      fixture.detectChanges();
      expect(messageViewport()).withContext('the loading state is wrapped').not.toBeNull();
      expect(host().querySelector('.data-table__message-viewport app-loading-spinner')).not.toBeNull();
      set('loading', false);
    });
  });
});


// THE NON-SELECTING GRID — SIX OF THE SEVEN CONSUMERS
// * A TAB STOP PER ROW that did nothing when activated. On a page of twenty accounts, reaching the first
// row command by keyboard meant pressing Tab past twenty rows. * AN ANNOUNCED SELECTION STATE the grid
// could not enter.
describe('DataTableComponent when nothing listens for a row selection', () => {
  let fixture: ComponentFixture<DataTableComponent<Row>>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DataTableComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent<DataTableComponent<Row>>(DataTableComponent);

    // ⚠ NOTHING IS SUBSCRIBED HERE, DELIBERATELY, AND NOT EVEN `sortChange`.
    fixture.componentRef.setInput('columns', baseColumns());
    fixture.componentRef.setInput('rows', ROWS);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  function bodyRows(): readonly HTMLTableRowElement[] {
    return fixture.debugElement
      .queryAll(By.css('tbody tr'))
      .map((node) => node.nativeElement as HTMLTableRowElement);
  }

  it('renders every row, the withdrawal being of the affordance and not of the data', () => {
    expect(bodyRows().length).toBe(ROWS.length);
  });

  it('puts no row in the tab order', () => {
    // The headline cost of the defect. Asserted as the ABSENCE of the attribute rather than as a negative
    // value: `tabindex="-1"` would keep the row focusable programmatically and would still match the global
    // reset's focus rule, so only removing it entirely is correct.
    for (const row of bodyRows()) {
      expect(row.getAttribute('tabindex')).toBeNull();
    }
  });

  it('announces no selection state on any row', () => {
    // ⚠ ABSENT, NOT FALSE, AND THE DIFFERENCE IS THE WHOLE MECHANISM. Both stylesheets key the cursor, the
    // focus ring, the hover ink, the press fill and the selected tint to `[aria-selected]` being PRESENT —
    // an attribute selector cannot match an absent attribute — so removing it withdraws the entire visual
    // affordance with the announcement, in one move.
    for (const row of bodyRows()) {
      expect(row.hasAttribute('aria-selected')).toBeFalse();
    }

    expect(fixture.nativeElement.querySelectorAll('[aria-selected]').length).toBe(0);
  });

  it('marks no row as selected, by class or otherwise', () => {
    for (const row of bodyRows()) {
      expect(row.classList.contains('data-table__row--selected')).toBeFalse();
    }
  });

  it('moves no state when a row is pressed', () => {
    // The handlers are still declared — a template cannot register a listener conditionally without
    // duplicating the whole row — so the component refuses inside them. Observable as the absence of any
    // announced or painted selection after a press.
    bodyRows()[1].click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('[aria-selected]').length).toBe(0);
    expect(bodyRows()[1].classList.contains('data-table__row--selected')).toBeFalse();
  });

  it('moves no state on Enter or Space, and suppresses neither key', () => {
    const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    const space = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });

    bodyRows()[0].dispatchEvent(enter);
    bodyRows()[0].dispatchEvent(space);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('[aria-selected]').length).toBe(0);
    expect(enter.defaultPrevented).toBeFalse();
    expect(space.defaultPrevented).toBeFalse();
  });

  it('still renders the row index for assistive technology', () => {
    // Withdrawing selection must not withdraw the grid semantics that have nothing to do with
    // it: the row index tells a reader where they are in a paged set and is unrelated.
    for (const row of bodyRows()) {
      expect(row.hasAttribute('aria-rowindex')).toBeTrue();
    }
  });
});

describe('DataTableComponent with a real wire contract as its row', () => {
  // WHY A REAL MODEL AND NOT THE LOCAL FIXTURE SHAPE. `ProfilePropertyDefinition` is the actual transfer
  // contract this application receives, and it is the row of the grid that
  // `Website/admin/Users/ProfileDefinitions.ascx` renders - the same screen whose four command columns at
  // L17-L20 (Edit, Delete, MoveDown, MoveUp, all keyed `PropertyDefinitionID`) set the upper bound on
  // projected row actions.

  /** A definition seeded at the zero identity, as the role, page and module tables are. */
  const ZERO_SEEDED: ProfilePropertyDefinition = {
    propertyDefinitionId: 0,
    portalId: -1,
    moduleDefId: null,
    dataType: 349,
    defaultValue: null,
    propertyCategory: 'Name',
    propertyName: 'Prefix',
    length: 0,
    required: false,
    validationExpression: null,
    viewOrder: 0,
    visible: true,
    visibility: 2,
  };

  /** A definition carrying the sentinel value as a real identifier. */
  const SENTINEL_SEEDED: ProfilePropertyDefinition = {
    propertyDefinitionId: -1,
    portalId: -1,
    moduleDefId: null,
    dataType: 349,
    defaultValue: '',
    propertyCategory: 'Contact',
    propertyName: 'Telephone',
    length: 0,
    required: true,
    validationExpression: '',
    viewOrder: 1,
    visible: false,
    visibility: 0,
  };

  const DEFINITIONS: readonly ProfilePropertyDefinition[] = [ZERO_SEEDED, SENTINEL_SEEDED];

  let fixture: ComponentFixture<DataTableComponent<ProfilePropertyDefinition>>;
  let httpMock: HttpTestingController;
  let chosen: ProfilePropertyDefinition[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DataTableComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent<DataTableComponent<ProfilePropertyDefinition>>(
      DataTableComponent,
    );
    chosen = [];
    fixture.componentInstance.rowSelect.subscribe((row) => chosen.push(row));

    // Every `field` below is checked against the real contract, so a typo such as
    // `propertyNam` would fail to compile rather than render an empty column.
    const columns: readonly DataTableColumn<ProfilePropertyDefinition>[] = [
      { key: 'PropertyName', label: 'Property Name', field: 'propertyName', sortable: true },
      { key: 'PropertyCategory', label: 'Category', field: 'propertyCategory' },
      { key: 'ViewOrder', label: 'Order', field: 'viewOrder', bodyAlign: 'end' },

      // A boolean has no single correct text form, so it states a formatter - exactly as the
      // legacy boolean columns were template columns rather than bound columns.
      { key: 'Required', label: 'Required', value: (row) => (row.required ? 'Yes' : 'No') },
    ];

    fixture.componentRef.setInput('columns', columns);
    fixture.componentRef.setInput('rows', DEFINITIONS);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** The rendered body rows. */
  function rows(): readonly HTMLTableRowElement[] {
    return fixture.debugElement
      .queryAll(By.css('tbody tr'))
      .map((node) => node.nativeElement as HTMLTableRowElement);
  }

  it('renders a row seeded at zero and a row carrying the sentinel identifier', () => {
    expect(rows().length).toBe(2);

    const firstCell = requireElement(rows()[0], 'td');
    const secondCell = requireElement(rows()[1], 'td');

    expect((firstCell.textContent ?? '').trim()).toBe('Prefix');
    expect((secondCell.textContent ?? '').trim()).toBe('Telephone');
  });

  it('selects the zero-seeded row without treating its identifier as absent', () => {
    rows()[0].click();
    fixture.detectChanges();

    // Identity is the object reference, so the row arrives whole and its zero identifier is simply carried
    // along. A `track` or comparison written as `if (id)` would have mis-keyed this row against the
    // sentinel row and selected the wrong one, with no error anywhere.
    expect(chosen).toEqual([ZERO_SEEDED]);
    expect(chosen[0].propertyDefinitionId).toBe(0);
  });

  it('selects the sentinel-identified row as a real row rather than discarding it', () => {
    rows()[1].click();
    fixture.detectChanges();

    expect(chosen).toEqual([SENTINEL_SEEDED]);
    expect(chosen[0].propertyDefinitionId).toBe(-1);
  });

  it('renders the zero view order as a zero rather than as an empty cell', () => {
    // Zero is a value, not an absence. A truthiness test in the text conversion would blank
    // this cell and the grid would appear to be missing data.
    const orderCells = Array.from(rows()[0].querySelectorAll('td'));

    expect((orderCells[2].textContent ?? '').trim()).toBe('0');
  });

  it('renders the legacy empty-string null as an empty cell, matching the sentinel', () => {
    const columns: readonly DataTableColumn<ProfilePropertyDefinition>[] = [
      { key: 'DefaultValue', label: 'Default', field: 'defaultValue' },
    ];
    fixture.componentRef.setInput('columns', columns);
    fixture.detectChanges();

    const nullDefault = requireElement(rows()[0], 'td');
    const blankDefault = requireElement(rows()[1], 'td');

    expect((nullDefault.textContent ?? '').trim()).toBe('');
    expect((blankDefault.textContent ?? '').trim()).toBe('');
  });

  it('renders a boolean through its formatter rather than as a stringified value', () => {
    const firstRowCells = Array.from(rows()[0].querySelectorAll('td'));
    const secondRowCells = Array.from(rows()[1].querySelectorAll('td'));

    expect((firstRowCells[3].textContent ?? '').trim()).toBe('No');
    expect((secondRowCells[3].textContent ?? '').trim()).toBe('Yes');
  });
});

/** Host exercising the projection surfaces, which need a real template context. */
@Component({
  standalone: true,
  imports: [DataTableComponent],
  template: `
    <app-data-table [columns]="columns" [rows]="rows" (rowSelect)="selectedRows.push($event)">
      <!--
        INTERPOLATED rather than written as a literal, because caption wording is
        resx-sourced in this migration and must therefore be treated as untrusted text like
        any other. Interpolation is what escapes it; a literal would prove nothing about a
        value that arrives from a resource file at run time.
      -->
      <span dataTableCaption>{{ captionText }}</span>

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
   * The command template, read from the host's own view. Declared inside the grid's tag but reachable as
   * a view child, because an `ng-template` a host writes is part of the host's view whether or not the
   * component projects it.
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

  /** The projected caption wording. */
  public captionText = 'Portals';

  public readonly selectedRows: Row[] = [];

  public readonly edited: Row[] = [];
}

describe('DataTableComponent projection', () => {
  let fixture: ComponentFixture<HostComponent>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],

      // Real client FIRST, testing backend second - see the note on the main suite. The pair is registered
      // here too so that the projection surface, which is where a feature's own templates and controls
      // enter the component, is held to the same zero-request standard.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  /**
   * Narrows a view child from its declared optional type to the template a column requires. The
   * descriptor now REQUIRES a template on both non-text kinds, which is the whole point of the change,
   * and a view child is declared optional because it is not populated until the view exists.
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

  describe('markup in the projected caption is rendered as text, never as markup', () => {
    // So this is parity, not paranoia: the legacy application escaped too, wrapping an externally supplied
    // message in an HTML encoder before display.

    const SCRIPT_PAYLOAD = '<script>window.__dataTableCaptionXss = true;</script>';
    const MARKUP_PAYLOAD = '<b>x</b>';
    const IMAGE_PAYLOAD = '<img src="x" onerror="window.__dataTableCaptionXss = true">';

    /** The rendered caption element. */
    function caption(): Element {
      return requireElement(fixture.nativeElement as Element, 'table > caption');
    }

    /** Projects a caption value and renders it. */
    function projectCaption(value: string): void {
      fixture.componentInstance.captionText = value;
      fixture.detectChanges();
    }

    it('renders a markup-bearing caption verbatim and creates no element from it', () => {
      projectCaption(MARKUP_PAYLOAD);

      expect((caption().textContent ?? '').trim()).toBe(MARKUP_PAYLOAD);
      expect(caption().querySelector('b')).toBeNull();
    });

    it('renders a script-bearing caption as text and creates no script element', () => {
      projectCaption(SCRIPT_PAYLOAD);

      expect((caption().textContent ?? '').trim()).toBe(SCRIPT_PAYLOAD);
      expect(caption().querySelector('script')).toBeNull();
      expect((fixture.nativeElement as Element).querySelectorAll('script').length).toBe(0);
    });

    it('creates no element from an event-handler payload in the caption either', () => {
      // A script element inserted after load does not execute in every browser, so an assertion resting on
      // scripts alone could pass for the wrong reason.
      projectCaption(IMAGE_PAYLOAD);

      expect((caption().textContent ?? '').trim()).toBe(IMAGE_PAYLOAD);
      expect(caption().querySelector('img')).toBeNull();
    });

    it('leaves no trace of any caption payload having executed', () => {
      const globals: Record<string, unknown> = window as unknown as Record<string, unknown>;

      expect(globals['__dataTableCaptionXss']).toBeUndefined();
    });
  });

  it('lets a projected caption replace the generic fallback entirely', () => {
    // The other half of the fallback contract, and the half that keeps it a floor rather than a change of
    // behaviour.
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

  // KEYBOARD ACTIVATION OF A COMMAND MUST NOT SELECT THE ROW EITHER. The click case above was specified;
  // the key case was not, even though the component stops BOTH event families on the actions cell and its
  // own comment says why - a command activated from the keyboard raises a key event that bubbles exactly as
  // a click does.

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

  it('offers the row affordance because the HOST TEMPLATE binds the output', () => {
    // ⚠⚠ THE TIMING CLAIM, ASSERTED RATHER THAN ASSUMED, and this is the case that makes the whole
    // conditional affordance trustworthy. The component decides selectability ONCE, in its initialisation
    // hook, by reading whether anything is subscribed to its row-selection emitter.
    const rows: readonly HTMLTableRowElement[] = fixture.debugElement
      .queryAll(By.css('tbody tr'))
      .map((node) => node.nativeElement as HTMLTableRowElement);

    expect(rows.length).toBeGreaterThan(0);

    for (const row of rows) {
      expect(row.getAttribute('tabindex'))
        .withContext('a listening consumer must get focusable rows')
        .toBe('0');
      expect(row.getAttribute('aria-selected'))
        .withContext('and rows that announce a selection state they can enter')
        .toBe('false');
    }
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
    // The cell kind is no longer inferred from the presence of a template: every non-text arm of the column
    // union declares `kind` explicitly and a template column with no template is unrepresentable, so the
    // only column shape that can omit `kind` is a text column bound to a field.
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
      // The row suppresses the space bar's page scroll when a row is activated. Applied to a checkbox, that
      // same suppression stops it toggling, so the row must stand aside before it prevents anything.
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
      // The row is excluded from the control test on purpose: it carries a tab index, so without that
      // exclusion every event would look as though it came from a control and no row could ever be
      // selected.
      const host = useEditableColumn();
      const row = fixture.debugElement.query(By.css('tbody tr')).nativeElement as HTMLElement;
      const event = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });

      row.dispatchEvent(event);
      fixture.detectChanges();

      expect(host.selectedRows).toEqual([ROWS[0]]);
      expect(event.defaultPrevented).toBeTrue();
    });
  });

  // ---------------------------------------------------------------------------
  // PASSING OVER A RUN OF ROW COMMANDS
  // ---------------------------------------------------------------------------

  describe('passing over a run of row commands', () => {
    /** Lets the component's measurement frame run, then repaints from what it settled. */
    async function settle(): Promise<void> {
      fixture.detectChanges();

      await new Promise<void>((resolve) => {
        window.requestAnimationFrame(() => resolve());
      });

      fixture.detectChanges();
    }

    /** Gives the host a command column and as many rows as asked for. */
    function withCommandRows(count: number): void {
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
      host.rows = Array.from({ length: count }, (_unused, index) => ({
        id: index,
        name: `Row ${index}`,
        count: index,
        active: true,
        note: null,
      }));
    }

    function skipAffordance(): Element | null {
      return (fixture.nativeElement as HTMLElement).querySelector('.data-table__skip');
    }

    function landingPoint(): HTMLElement | null {
      const found = (fixture.nativeElement as HTMLElement).querySelector('.data-table__skip-target');

      return found instanceof HTMLElement ? found : null;
    }

    /**
     * Every stop the Tab key would land on inside the body.
     *
     * ⚠ THE ROW ITSELF COUNTS, and getting that wrong is what made the first version of this helper
     * disagree with the component. A grid whose consumer listens for row selection puts `tabindex="0"` on
     * every row so a row can be activated from the keyboard - so a twenty-row read-only listing still costs
     * twenty presses to walk, and a skip affordance is worth exactly as much there as it is over a run of
     * commands. The selector therefore matches the component's own.
     */
    function stopsInBody(): number {
      const body = (fixture.nativeElement as HTMLElement).querySelector('tbody.data-table__body');

      return body === null
        ? 0
        : body.querySelectorAll(
            'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])',
          ).length;
    }

    it('offers nothing while the run is short enough to walk', async () => {
      // ⚠ THE COST THIS GUARDS. The affordance is itself a tab stop, so offering it over a two-row table
      // would charge a keyboard user a press to save one - and on a listing whose rows carry no controls at
      // all it would charge a press to save nothing.
      withCommandRows(2);
      await settle();

      expect(stopsInBody()).withContext('a short run, by measurement').toBeLessThan(7);
      expect(skipAffordance()).toBeNull();
      expect(landingPoint()).toBeNull();
    });

    it('counts the rows themselves, not only the commands inside them', async () => {
      // ⚠ THIS SPEC EXISTS BECAUSE ITS FIRST VERSION ASSERTED THE OPPOSITE AND WAS WRONG. It claimed a
      // twenty-row body with a plain text column carried "no controls" and should offer nothing - and it
      // failed, because a grid whose consumer listens for row selection puts `tabindex="0"` on every row so a
      // row can be activated from the keyboard. Twenty such rows are twenty presses to walk whether or not any
      // command sits inside them, so the affordance is worth exactly as much there. The component was right
      // and the specification was wrong; this is the invariant that actually holds.
      const host = fixture.componentInstance;

      host.columns = [{ key: 'name', label: 'Name', field: 'name' }];
      host.rows = Array.from({ length: 20 }, (_unused, index) => ({
        id: index,
        name: `Row ${index}`,
        count: index,
        active: true,
        note: null,
      }));
      await settle();

      const rows = (fixture.nativeElement as HTMLElement).querySelectorAll(
        'tbody.data-table__body tr[tabindex="0"]',
      ).length;

      expect(rows).withContext('every row is its own stop').toBe(20);
      expect((fixture.nativeElement as HTMLElement).querySelectorAll('tbody button').length)
        .withContext('and not one command among them')
        .toBe(0);
      expect(skipAffordance()).withContext('twenty presses is still worth passing over').not.toBeNull();
    });

    it('offers a way past once the run is long enough to dominate the traversal', async () => {
      // Measured on the account listing: THIRTY consecutive row-command stops out of eighty-two on the page,
      // uninterrupted because a row's own username is plain text rather than a link, and the first stop after
      // them was the pager. The page's only skip affordance bypasses the header and sidebar and none of that.
      withCommandRows(8);
      await settle();

      expect(stopsInBody()).withContext('a long run, by measurement').toBeGreaterThan(6);

      const affordance = skipAffordance();

      expect(affordance).not.toBeNull();
      expect((affordance?.textContent ?? '').trim()).toBe('Skip past the record commands');
    });

    it('moves focus to a landing point that follows the whole table', async () => {
      withCommandRows(8);
      await settle();

      const affordance = skipAffordance();

      if (!(affordance instanceof HTMLElement)) {
        throw new Error('Expected the skip affordance to be rendered.');
      }

      affordance.click();
      fixture.detectChanges();

      const target = landingPoint();
      const table = (fixture.nativeElement as HTMLElement).querySelector('table.data-table');

      expect(target).not.toBeNull();
      expect(document.activeElement).withContext('focus arrived at the landing point').toBe(target);

      // The point of the affordance is that the landing point is BEYOND the rows, not merely elsewhere.
      expect(table?.compareDocumentPosition(target as Node) ?? 0)
        .withContext('the landing point follows the table in the document')
        .toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    });

    it('keeps the landing point out of the tab order and gives it something to say', async () => {
      // A landing point in the tab order would add the stop back that the affordance removes, and an empty
      // one would drop a reader somewhere silent.
      withCommandRows(8);
      await settle();

      const target = landingPoint();

      expect(target?.getAttribute('tabindex')).toBe('-1');
      expect((target?.textContent ?? '').trim()).toBe('End of table');
    });
  });
});
