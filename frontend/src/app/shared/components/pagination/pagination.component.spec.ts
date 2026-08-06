import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { PaginationComponent } from './pagination.component';

describe('PaginationComponent', () => {
  let fixture: ComponentFixture<PaginationComponent>;
  let emitted: number[];

  // No HTTP providers are registered: this component makes no requests and injects no
  // services, so there is no backend to fake and nothing to verify in an afterEach.
  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PaginationComponent] }).compileComponents();

    fixture = TestBed.createComponent(PaginationComponent);
    emitted = [];
    fixture.componentInstance.pageChange.subscribe((page) => emitted.push(page));
  });

  /**
   * Binds the three inputs, which are the whole public surface.
   *
   * There are exactly three, and `page` is the ZERO-BASED index.
   */
  function bind(page: number, pageSize: number, totalCount: number): void {
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.detectChanges();
  }

  /**
   * Narrows a query to an element, failing the spec rather than asserting non-null.
   *
   * `query` returns `null` when nothing matches, and a non-null assertion would turn a
   * missing element into an unhelpful runtime error inside the expectation.
   */
  function element(selector: string): HTMLElement {
    const node = fixture.debugElement.query(By.css(selector));

    if (node === null) {
      throw new Error(`Expected an element matching "${selector}", but none was rendered.`);
    }

    return node.nativeElement as HTMLElement;
  }

  /** Whether anything matches, for the cases that assert absence. */
  function isRendered(selector: string): boolean {
    return fixture.debugElement.query(By.css(selector)) !== null;
  }

  /** The four navigation buttons, in rendered order: first, previous, next, last. */
  function buttons(): readonly HTMLButtonElement[] {
    return fixture.debugElement
      .queryAll(By.css('.pagination__button'))
      .map((node) => node.nativeElement as HTMLButtonElement);
  }

  /** One navigation button, narrowed by its position in the rendered order. */
  function button(index: number): HTMLButtonElement {
    const all = buttons();
    const found = all[index];

    if (found === undefined) {
      throw new Error(`Expected a navigation button at index ${index}, found ${all.length}.`);
    }

    return found;
  }

  function collapsed(text: string | null): string {
    return text === null ? '' : text.replace(/\s+/g, ' ').trim();
  }

  function statusText(): string {
    return collapsed(element('.pagination__status').textContent);
  }

  function positionText(): string {
    return collapsed(element('.pagination__position').textContent);
  }

  describe('visibility', () => {
    // The rule is "hide when the total is no greater than the page size", which is
    // BYTE-EQUIVALENT to the legacy portals guard and a divergence only from users.
    // Both screens ran the identical guard — Portals.ascx.vb L155-L157 and
    // Users.ascx.vb L278-L280 — but portals hard-coded SuppressPager to True
    // (Portals.ascx.vb L108-L114, real read commented out at L110-L111, Return True at
    // L112) so its guard executed and its pager vanished for a single page, while users
    // read the genuine Display_SuppressPager setting (Users.ascx.vb L129-L134) whose
    // default is False (UserModuleBase.vb L131-L133) so its guard never ran and its
    // pager was always visible.

    it('renders nothing when everything fits on one page', () => {
      bind(0, 10, 7);

      expect(isRendered('.pagination')).toBeFalse();
    });

    it('renders nothing when the total exactly equals the page size', () => {
      // The equal case is the boundary the legacy guard turned on: it showed the pager
      // only when the page size was strictly less than the total.
      bind(0, 10, 10);

      expect(isRendered('.pagination')).toBeFalse();
    });

    it('renders nothing for an empty result set, without error', () => {
      // Zero is a real, meaningful count — "nothing matched" — never "unknown".
      bind(0, 10, 0);

      expect(isRendered('.pagination')).toBeFalse();
      expect(buttons().length).toBe(0);
    });

    it('renders once there is more than one page', () => {
      bind(0, 10, 25);

      expect(isRendered('.pagination')).toBeTrue();
      expect(buttons().length).toBe(4);
    });
  });

  describe('a page size that could not have come from the API', () => {
    // A page size of zero, a negative size, a fraction, a not-a-number and an infinity
    // are all reduced to "no usable page size", which renders nothing and stays inert.
    //
    // RENDERING NOTHING RATHER THAN RAISING IS THE POINT. Zero is not exotic: the shared
    // emptyPagedResult() factory seeds pageSize to exactly zero and both signal stores
    // hold that empty page as their initial state, so a feature binding the served page
    // size sees zero until its first response arrives. Raising would take the whole
    // screen down on its first paint, before any request had a chance to complete.
    //
    // Zero is NOT read as "every match on one page" either. That mode does not exist in
    // this API, and inventing it would paint a plausible single page a reader could not
    // tell from a real one.

    it('renders nothing for a page size of zero rather than treating it as unpaged', () => {
      bind(0, 0, 5000);

      expect(isRendered('.pagination')).toBeFalse();
      expect(fixture.componentInstance.totalPages).toBe(0);
    });

    it('renders nothing for a negative page size', () => {
      bind(0, -10, 5000);

      expect(isRendered('.pagination')).toBeFalse();
      expect(fixture.componentInstance.totalPages).toBe(0);
    });

    it('does not survive as a fraction, which would render a fractional page count', () => {
      bind(0, 2.5, 10);

      expect(Number.isInteger(fixture.componentInstance.totalPages)).toBeTrue();
      expect(fixture.componentInstance.pageSize).toBe(2);
    });

    it('keeps a not-a-number out of the arithmetic', () => {
      bind(0, Number.NaN, 10);

      expect(Number.isNaN(fixture.componentInstance.totalPages)).toBeFalse();
      expect(fixture.componentInstance.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('keeps an infinity out of the arithmetic', () => {
      bind(0, Number.POSITIVE_INFINITY, 10);

      expect(Number.isFinite(fixture.componentInstance.totalPages)).toBeTrue();
      expect(fixture.componentInstance.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('emits nothing at all while the page size is unusable', () => {
      bind(3, 0, 5000);

      fixture.componentInstance.goNext();
      fixture.componentInstance.goPrevious();
      fixture.componentInstance.goFirst();
      fixture.componentInstance.goLast();

      expect(emitted).toEqual([]);
    });
  });

  describe('a total that could not have come from the API', () => {
    it('treats a negative total as no records', () => {
      bind(0, 10, -5);

      expect(fixture.componentInstance.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('keeps a not-a-number total out of the arithmetic', () => {
      bind(0, 10, Number.NaN);

      expect(Number.isNaN(fixture.componentInstance.totalPages)).toBeFalse();
      expect(fixture.componentInstance.totalPages).toBe(0);
    });
  });

  describe('page arithmetic', () => {
    it('reports no pages for an empty result set', () => {
      // Zero rather than one, matching the shared contract's own factories:
      // emptyPagedResult() reports zero total pages, and unpagedResult() reports zero
      // for an empty collection. Nothing renders in this state, so no readout ever
      // shows a page count of zero.
      bind(0, 10, 0);

      expect(fixture.componentInstance.totalPages).toBe(0);
    });

    it('rounds a partial final page up', () => {
      bind(0, 10, 21);

      expect(fixture.componentInstance.totalPages).toBe(3);
    });

    it('does not round an exact multiple up', () => {
      // The classic Math.ceil off-by-one: 20 over 10 is 2 pages, never 3.
      bind(0, 10, 20);

      expect(fixture.componentInstance.totalPages).toBe(2);
    });

    it('reads the bound page size rather than assuming one', () => {
      // 120 over 25 is 5 pages. Neither legacy default — the 20 hard-coded at
      // Portals.ascx.vb L96 nor the 10 defaulted at UserModuleBase.vb L134-L136 —
      // could produce this, so the component must be reading the input.
      bind(0, 25, 120);

      expect(fixture.componentInstance.totalPages).toBe(5);
    });

    it('reads an unusual page size too', () => {
      // 30 over 7 is 5 pages (4 full and a short one).
      bind(0, 7, 30);

      expect(fixture.componentInstance.totalPages).toBe(5);
    });

    it('never produces an infinite or negative page count', () => {
      bind(0, 1, 5000);

      expect(fixture.componentInstance.totalPages).toBe(5000);
      expect(Number.isFinite(fixture.componentInstance.totalPages)).toBeTrue();
      expect(fixture.componentInstance.totalPages).toBeGreaterThan(-1);
    });
  });

  describe('the page base', () => {
    // MIGRATION: the boundary is ZERO-BASED and the display is ONE-BASED. Two legacy
    // facts one line apart settle it: Portals.ascx.vb L142 passed CurrentPage - 1 to the
    // provider, so the data base is zero, while L148-L150 handed the pager the
    // unmodified one-based CurrentPage, so the display base is one. Both stores pass the
    // index straight through in both directions and perform no arithmetic on it, so this
    // component owns the single conversion.

    it('renders the first page as page one', () => {
      // Asserted on rendered DOM text, not on the getter alone: a getter-only assertion
      // could not catch a template that applied the + 1 a second time.
      bind(0, 10, 100);

      expect(positionText()).toBe('1 / 10');
    });

    it('renders a later page one-based', () => {
      bind(2, 10, 100);

      expect(positionText()).toBe('3 / 10');
      expect(fixture.componentInstance.displayPage).toBe(3);
    });

    it('emits one when stepping forward from the first page, never two', () => {
      // The single most valuable assertion here: emitting 2 is the double-increment
      // defect, and it is invisible to the compiler.
      bind(0, 10, 100);

      button(2).click();

      expect(emitted).toEqual([1]);
    });

    it('emits a zero-based index when stepping back', () => {
      bind(4, 10, 100);

      button(1).click();

      expect(emitted).toEqual([3]);
    });

    it('emits zero for the first page', () => {
      bind(4, 10, 100);

      button(0).click();

      expect(emitted).toEqual([0]);
    });

    it('emits the zero-based index of the last page', () => {
      // 100 over 10 is 10 pages, so the last index is 9 rather than 10.
      bind(4, 10, 100);

      button(3).click();

      expect(emitted).toEqual([9]);
    });
  });

  describe('the range summary', () => {
    it('names the range shown and the total', () => {
      bind(1, 10, 34);

      expect(statusText()).toBe('11–20 of 34');
    });

    it('clamps the last item to the total on a short final page', () => {
      bind(3, 10, 34);

      expect(statusText()).toBe('31–34 of 34');
    });

    it('reports the range for a page size that is not the legacy default', () => {
      bind(2, 25, 120);

      expect(statusText()).toBe('51–75 of 120');
    });

    it('shows no not-a-number or infinity in any rendered text', () => {
      bind(2, 25, 120);

      expect(statusText()).not.toContain('NaN');
      expect(statusText()).not.toContain('Infinity');
      expect(positionText()).not.toContain('NaN');
      expect(positionText()).not.toContain('Infinity');
    });

    it('is announced politely, because a page change replaces the list without moving focus', () => {
      bind(0, 10, 34);

      expect(element('.pagination__status').getAttribute('aria-live'))
        .withContext('a status update must not interrupt')
        .toBe('polite');
    });
  });

  describe('availability', () => {
    it('disables the backward steps on the first page', () => {
      bind(0, 10, 100);

      expect(button(0).disabled).withContext('already on the first page').toBeTrue();
      expect(button(1).disabled).toBeTrue();
      expect(button(2).disabled).toBeFalse();
      expect(button(3).disabled).toBeFalse();
    });

    it('disables the forward steps on the last page', () => {
      bind(9, 10, 100);

      expect(button(0).disabled).toBeFalse();
      expect(button(1).disabled).toBeFalse();
      expect(button(2).disabled).withContext('already on the last page').toBeTrue();
      expect(button(3).disabled).toBeTrue();
    });

    it('keeps the disabled steps in the document, so the control does not reflow under the pointer', () => {
      bind(0, 10, 100);

      expect(buttons().length).toBe(4);
    });
  });

  describe('emitting a page change', () => {
    it('emits nothing for a step that is unavailable', () => {
      bind(0, 10, 100);

      fixture.componentInstance.goPrevious();
      fixture.componentInstance.goFirst();

      expect(emitted).toEqual([]);
    });

    it('emits nothing for a forward step at the end', () => {
      bind(9, 10, 100);

      fixture.componentInstance.goNext();
      fixture.componentInstance.goLast();

      expect(emitted).toEqual([]);
    });

    it('emits once per step rather than repeating itself', () => {
      bind(3, 10, 100);

      button(2).click();

      expect(emitted.length).toBe(1);
    });

    it('does not change its own page, so the pager cannot claim a page whose request failed', () => {
      bind(3, 10, 100);

      button(2).click();
      fixture.detectChanges();

      expect(fixture.componentInstance.page)
        .withContext('the consumer rebinds this once the page has actually arrived')
        .toBe(3);
    });
  });

  describe('a page index past the end', () => {
    // A real state rather than a fault: records can be removed between a page being
    // requested and rendered, which both signal stores model explicitly. Neither store
    // clamps the index it is given, and both rely on this component emitting only an
    // index inside the available range, so these are the assertions that defend it.

    it('reads as the last real page rather than as a page the data cannot support', () => {
      // Page index 11 with only 3 pages of records.
      bind(11, 10, 30);

      expect(positionText()).toBe('3 / 3');
    });

    it('reports the range of the last real page', () => {
      bind(11, 10, 30);

      expect(statusText()).toBe('21–30 of 30');
    });

    it('offers no forward step beyond the end', () => {
      bind(11, 10, 30);

      expect(button(2).disabled).toBeTrue();
      expect(button(3).disabled).toBeTrue();
    });

    it('returns into range rather than emitting an index that does not exist', () => {
      bind(11, 10, 30);

      fixture.componentInstance.goPrevious();

      expect(emitted).toEqual([2]);
    });

    it('never emits an index outside the available range from any step', () => {
      bind(11, 10, 30);

      fixture.componentInstance.goFirst();
      fixture.componentInstance.goPrevious();
      fixture.componentInstance.goNext();
      fixture.componentInstance.goLast();

      for (const index of emitted) {
        expect(index).toBeGreaterThan(-1);
        expect(index).toBeLessThan(3);
      }
    });

    it('treats a negative bound index as the first page', () => {
      bind(-5, 10, 100);

      expect(fixture.componentInstance.page).toBe(0);
      expect(positionText()).toBe('1 / 10');
    });

    it('treats a not-a-number bound index as the first page', () => {
      bind(Number.NaN, 10, 100);

      expect(fixture.componentInstance.page).toBe(0);
      expect(positionText()).toBe('1 / 10');
    });

    it('truncates a fractional bound index rather than paging by half a page', () => {
      bind(2.7, 10, 100);

      expect(fixture.componentInstance.page).toBe(2);
      expect(positionText()).toBe('3 / 10');
    });
  });

  describe('the derivations with nothing to page through', () => {
    // Nothing renders in this state, so these getters are read directly. They still have
    // to answer coherently: a consumer may read them, and a later template change must
    // not be the moment a not-a-number first appears on screen.

    it('reports the first page and an empty range rather than an undefined position', () => {
      bind(0, 10, 0);

      expect(fixture.componentInstance.displayPage).toBe(1);
      expect(fixture.componentInstance.firstItemNumber).toBe(0);
      expect(fixture.componentInstance.lastItemNumber).toBe(0);
    });

    it('offers no step in either direction', () => {
      bind(0, 10, 0);

      expect(fixture.componentInstance.canGoPrevious).toBeFalse();
      expect(fixture.componentInstance.canGoNext).toBeFalse();
    });

    it('reports an empty range when the page size never resolved', () => {
      bind(4, 0, 120);

      expect(fixture.componentInstance.firstItemNumber).toBe(0);
      expect(fixture.componentInstance.lastItemNumber).toBe(0);
      expect(fixture.componentInstance.displayPage).toBe(1);
    });
  });

  describe('accessibility', () => {
    it('renders no navigation landmark, because the application shell owns the only one', () => {
      // A second nav would announce a duplicate landmark and add a spurious entry to the
      // landmark list on every list screen. A named group is the correct pattern for a
      // labelled cluster of related controls.
      bind(0, 10, 100);

      expect(fixture.nativeElement.querySelector('nav')).toBeNull();
    });

    it('names the group of controls', () => {
      bind(0, 10, 100);

      const group = element('.pagination');
      expect(group.getAttribute('role')).toBe('group');
      expect(group.getAttribute('aria-label')).toBe('Pagination');
    });

    it('names every step, so none is announced as a punctuation character', () => {
      bind(4, 10, 100);

      expect(buttons().map((step) => step.getAttribute('aria-label'))).toEqual([
        'First page',
        'Previous page',
        'Next page',
        'Last page',
      ]);
    });

    it('hides the glyphs from assistive technology', () => {
      bind(4, 10, 100);

      for (const step of buttons()) {
        const glyph = step.querySelector('span');

        if (glyph === null) {
          throw new Error('Expected every step to render its glyph in a span.');
        }

        expect(glyph.getAttribute('aria-hidden')).toBe('true');
      }
    });

    it('declares an explicit button type, so a pager inside a form cannot submit it', () => {
      bind(4, 10, 100);

      expect(buttons().every((step) => step.getAttribute('type') === 'button'))
        .withContext('without it, a button in a form defaults to submit')
        .toBeTrue();
    });

    it('renders the current position as text rather than as a disabled control', () => {
      bind(4, 10, 100);

      expect(element('.pagination__position').tagName)
        .withContext('a disabled button would put an unusable stop in the tab order')
        .toBe('SPAN');
    });
  });

  describe('the public surface', () => {
    it('declares the on-push strategy the migration plan mandates', () => {
      const definition = (PaginationComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });

    it('accepts exactly three inputs and publishes exactly one output', () => {
      // The surface is fixed by the design system at page, pageSize and totalCount in,
      // and pageChange out. A fourth input is a deviation, not a convenience.
      const definition = (
        PaginationComponent as unknown as {
          ɵcmp: { inputs: Record<string, unknown>; outputs: Record<string, unknown> };
        }
      ).ɵcmp;

      expect(Object.keys(definition.inputs).sort()).toEqual(['page', 'pageSize', 'totalCount']);
      expect(Object.keys(definition.outputs)).toEqual(['pageChange']);
    });
  });
});
