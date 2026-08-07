/**
 * Specifications for the shared pagination component.
 *
 * MIGRATION: NET-NEW, with no predecessor to port. The legacy tree contains no automated
 * tests of any kind — not a test project, not a fixture, not a single assertion — across
 * either the class library or the web application, so nothing here is a translation of an
 * existing test and none of these expectations was inherited. Every one is derived from the
 * legacy SOURCE it cites.
 *
 * ## Why this file is the component's only type-check
 *
 * `tsconfig.app.json` declares `files: ["src/main.ts"]` and compiles by import graph, so a
 * component no feature has imported yet is never reached by a production build: a clean
 * `ng build` proves nothing whatsoever about this component. `tsconfig.spec.json` includes
 * `src/**\/*.spec.ts`, which makes THIS FILE'S import of `./pagination.component` the only
 * route by which the class and its template are compiled at all, and a green test run the
 * only evidence that the template satisfies `strictTemplates`. That is why this file
 * INSTANTIATES the component rather than merely referencing its type, and why it asserts on
 * rendered DOM rather than on getters alone.
 *
 * ## The one defect no compiler can catch: the two page bases
 *
 * The boundary is ZERO-BASED and the display is ONE-BASED, and the whole reason this
 * component exists is to hold the single `+ 1` between them. Two legacy authorities sitting
 * a few lines apart settle both bases, and both were read directly rather than taken on
 * trust:
 *
 *   * `Website/admin/Portal/Portals.ascx.vb` L142 passes `CurrentPage - 1` down to the
 *     provider — `GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)` —
 *     so the DATA base is zero. `Website/admin/Users/Users.ascx.vb` repeats that same
 *     subtraction four times, at L265, L269, L271 and L274.
 *   * `Portals.ascx.vb` L148-L150 hands the pager `TotalRecords`, `PageSize` and then the
 *     UNMODIFIED one-based `CurrentPage`, so the DISPLAY base is one. (L147 is blank; the
 *     pager block genuinely begins at L148.) Both screens seed that counter to one —
 *     `Portals.ascx.vb` L47 and `Users.ascx.vb` L51, each `Private _CurrentPage As Integer = 1`.
 *
 * A disagreement about which base crosses this boundary would neither fail to compile nor
 * fail an assertion about a successful response: it would quietly serve the neighbouring
 * page. Emitting `2` when stepping forward from page zero is that defect exactly, which is
 * why it is asserted on its own below.
 *
 * ## What is asserted on the DOM, and why
 *
 * A getter can be right while the template is wrong. If the class adds its `+ 1` and the
 * template adds another, `displayPage` still reads correctly and the screen still shows the
 * wrong number, so every page-base expectation here is asserted on rendered text. For the
 * same reason the four steps are located by the ACCESSIBLE LABEL a person perceives rather
 * than by position in a node list: locating by index would keep passing if two controls
 * swapped places, which is precisely the defect worth catching.
 *
 * MIGRATION: one adaptation is deliberate and is recorded rather than absorbed. The
 * component renders NO page-number window — `pagination.component.html` L44-L54 documents
 * that the legacy ten-link window (`Library/Controls/PagingControl.vb`, `PageLinksPerPage`)
 * is not reproduced, because the class exposes no page list to iterate and the public input
 * surface is closed at three members. There is therefore no button labelled with a page
 * number to click. The equivalent coupling is asserted instead: the highest page number a
 * person can READ off the pager is parsed out of the rendered readout, and the control that
 * navigates there must emit exactly that number MINUS ONE. That keeps the label-to-emission
 * coupling genuine instead of restating a literal.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { PaginationComponent } from './pagination.component';

/**
 * The accessible label of each step, as a person hears it.
 *
 * Written out literally rather than imported from the component, so that a change to its
 * wording is reported by these specifications instead of being silently agreed with.
 */
const STEP = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
} as const;

/**
 * The en dash the template renders between the two item numbers, written as `&ndash;`.
 *
 * Escaped rather than pasted so that the expectation cannot be broken by a re-encoding of
 * this file, and so it is unmistakably not a hyphen.
 */
const EN_DASH = '\u2013';

describe('PaginationComponent', () => {
  let fixture: ComponentFixture<PaginationComponent>;
  let component: PaginationComponent;

  /**
   * Every index the component has emitted, in order.
   *
   * A captured array rather than a spy, because the COUNT matters as much as the value: a
   * redundant second emission for one step is a real defect, and `toEqual([1])` catches it
   * while an assertion that the emitter merely fired would not.
   */
  let emitted: number[];

  // No HTTP providers are registered, and none belongs here: this component issues no
  // request and injects no service of any kind, so there is no backend to fake and
  // consequently nothing to verify in an `afterEach`. Registering a testing backend for a
  // component that cannot reach one would assert nothing and would only invite the mistake
  // of leaving it unverified.
  beforeEach(async () => {
    // Imported rather than declared: the component is standalone, so it is brought in like
    // any other standalone building block, and this workspace contains no declaring module
    // anywhere that could hold it instead.
    await TestBed.configureTestingModule({ imports: [PaginationComponent] }).compileComponents();

    fixture = TestBed.createComponent(PaginationComponent);
    component = fixture.componentInstance;
    emitted = [];
    component.pageChange.subscribe((page) => emitted.push(page));
  });

  /**
   * Binds the three inputs, which are the entire public surface, and renders.
   *
   * Bound through `setInput` rather than by assigning to the instance, and the distinction
   * is load-bearing: all three inputs declare a `transform`, and a transform runs only when
   * Angular sets the input — through a template binding or through this call. Assigning
   * `component.pageSize = -10` would bypass the transform entirely and quietly test a state
   * the component can never actually be in, so every specification below binds this way.
   *
   * @param page The ZERO-BASED index of the page shown.
   * @param pageSize The number of items on a full page.
   * @param totalCount The total number of matches across every page.
   */
  function bind(page: number, pageSize: number, totalCount: number): void {
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.detectChanges();
  }

  /**
   * The fixture's host element, narrowed by a runtime check rather than by an assertion.
   *
   * `DebugElement.nativeElement` is typed loosely by the framework, so it is taken as
   * `unknown` and proved to be an element with `instanceof`. That way a surprise is a named
   * failure here instead of a property access on something unexpected inside an
   * expectation.
   */
  function host(): HTMLElement {
    const node: unknown = fixture.debugElement.nativeElement;

    if (node instanceof HTMLElement) {
      return node;
    }

    throw new Error('Expected the fixture host to be an HTML element.');
  }

  /**
   * The single element matching a selector, failing the specification when none is rendered.
   *
   * `query` yields `null` for no match, and a non-null assertion would convert a missing
   * element into an opaque runtime error inside an expectation. Throwing with the selector
   * in the message says which element was expected and never appeared.
   *
   * @param selector The CSS selector to match.
   * @returns The matching element.
   */
  function element(selector: string): HTMLElement {
    const found = fixture.debugElement.query(By.css(selector));

    if (found === null) {
      throw new Error(`Expected an element matching "${selector}", but none was rendered.`);
    }

    const node: unknown = found.nativeElement;

    if (node instanceof HTMLElement) {
      return node;
    }

    throw new Error(`Expected "${selector}" to be an HTML element.`);
  }

  /** Whether anything matches, for the specifications that assert absence. */
  function isRendered(selector: string): boolean {
    return fixture.debugElement.query(By.css(selector)) !== null;
  }

  /** Every rendered step, narrowed to native buttons by a runtime check. */
  function steps(): readonly HTMLButtonElement[] {
    return fixture.debugElement.queryAll(By.css('.pagination__button')).map((found) => {
      const node: unknown = found.nativeElement;

      if (node instanceof HTMLButtonElement) {
        return node;
      }

      throw new Error('Expected every pagination step to be a native button element.');
    });
  }

  /**
   * One step, located by the accessible label a person perceives.
   *
   * Deliberately NOT by position in the node list. An index would keep passing if two
   * controls swapped places, whereas this couples each expectation to the affordance it
   * actually describes. Insisting on exactly one match also catches a duplicated label,
   * which would leave a screen-reader user with two identically named controls.
   *
   * @param label The step's `aria-label`.
   * @returns The matching button.
   */
  function step(label: string): HTMLButtonElement {
    const matching = steps().filter((candidate) => candidate.getAttribute('aria-label') === label);
    const found = matching[0];

    if (found === undefined) {
      const rendered = steps()
        .map((candidate) => candidate.getAttribute('aria-label'))
        .join(', ');

      throw new Error(`Expected a step labelled "${label}". Rendered: [${rendered}].`);
    }

    if (matching.length > 1) {
      throw new Error(
        `Expected exactly one step labelled "${label}", but ${matching.length} were rendered.`,
      );
    }

    return found;
  }

  /** Collapses the runs of whitespace a multi-line template introduces into single spaces. */
  function collapsed(text: string | null): string {
    return text === null ? '' : text.replace(/\s+/g, ' ').trim();
  }

  /** The rendered range summary, whitespace-collapsed. */
  function statusText(): string {
    return collapsed(element('.pagination__status').textContent);
  }

  /** The rendered position readout, whitespace-collapsed. */
  function positionText(): string {
    return collapsed(element('.pagination__position').textContent);
  }

  /**
   * The two ONE-BASED numbers a person can actually read off the pager.
   *
   * Parsed out of rendered text rather than read from a getter, so that an expectation built
   * on them is coupled to what is on screen. The pattern is anchored and admits digits only,
   * which is what makes a `NaN` or an `Infinity` in the readout a parse failure with the
   * offending text quoted rather than a silently passing comparison.
   *
   * @returns The page number shown and the count of pages it sits within.
   */
  function renderedPageNumbers(): { readonly shown: number; readonly total: number } {
    const text = positionText();
    const matched = /^(\d+) \/ (\d+)$/.exec(text);

    if (matched === null) {
      throw new Error(`Expected the readout to read "<page> / <total>", but found "${text}".`);
    }

    const shown = Number(matched[1]);
    const total = Number(matched[2]);

    if (Number.isSafeInteger(shown) === false || Number.isSafeInteger(total) === false) {
      throw new Error(`Expected two whole page numbers, but found "${text}".`);
    }

    return { shown, total };
  }

  describe('whether it renders at all', () => {
    // MIGRATION: the rule is "hide when the total is no greater than the page size", which is
    // BYTE-EQUIVALENT to the legacy PORTALS screen and a documented divergence only from the
    // legacy USERS screen. The two behaved differently while running a character-for-character
    // identical guard, so the divergence is real and is worth stating precisely:
    //
    //   * Both ran `If SuppressPager And ctlPagingControl.Visible Then
    //     ctlPagingControl.Visible = (PageSize < TotalRecords)` — Portals.ascx.vb L155-L157
    //     and Users.ascx.vb L278-L280, identical.
    //   * PORTALS hard-coded `SuppressPager` to `True` (Portals.ascx.vb L108-L114, with the
    //     genuine setting read COMMENTED OUT at L110-L111 and `Return True` at L112), so its
    //     guard RAN and its pager vanished whenever everything fit on one page.
    //   * USERS read the real `Display_SuppressPager` setting (Users.ascx.vb L129-L134,
    //     `CType(setting, Boolean)` at L132) whose default is `False`
    //     (Library/Components/Users/UserModuleBase.vb L131-L133), so its guard NEVER RAN and
    //     its pager stayed visible even for a single page.
    //
    // "Show when PageSize < TotalRecords" is exactly "hide when the total is no greater than
    // the page size", so this reproduces Portals and diverges from Users by design.

    it('renders nothing when everything already fits on one page', () => {
      bind(0, 10, 5);

      expect(isRendered('.pagination')).withContext('a single page needs no pager').toBeFalse();
      expect(steps().length).toBe(0);
    });

    it('renders nothing when the total exactly equals the page size', () => {
      // The equal case is the boundary the legacy guard turned on: it showed the pager only
      // when the page size was STRICTLY LESS than the total, so equality hides it.
      bind(0, 10, 10);

      expect(isRendered('.pagination')).withContext('10 of 10 is one full page').toBeFalse();
      expect(steps().length).toBe(0);
    });

    it('renders nothing for an empty result set, and raises nothing', () => {
      // Zero is a REAL, MEANINGFUL COUNT — "nothing matched" — and never "unknown". Binding it
      // must be as ordinary as any other total.
      expect(() => bind(0, 20, 0)).not.toThrow();

      expect(isRendered('.pagination')).toBeFalse();
      expect(steps().length).toBe(0);
      expect(component.totalPages).toBe(0);
    });

    it('renders the controls once there is more than one page', () => {
      bind(0, 10, 25);

      expect(isRendered('.pagination')).toBeTrue();
      expect(steps().length).withContext('first, previous, next and last').toBe(4);
    });
  });

  describe('a page size the API could not have produced', () => {
    // The component's documented choice for an unusable page size is to RENDER NOTHING and
    // stay INERT — never `Infinity`, never `NaN`, never a throw. Zero is not exotic: the
    // shared empty-page factory seeds `pageSize` to exactly zero and the feature stores hold
    // that empty page as their initial state, so a screen really does bind zero before its
    // first response arrives. Raising would take the screen down on its first paint.
    //
    // Zero is NOT read as "every match on one page" either. That mode does not exist in this
    // API, and inventing it would paint a plausible single page a reader could not tell from
    // a real one.

    it('renders nothing for a page size of zero rather than treating it as unpaged', () => {
      bind(0, 0, 5000);

      expect(component.totalPages).withContext('no usable page size, so no pages').toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('renders nothing for a negative page size', () => {
      bind(0, -25, 5000);

      expect(component.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('keeps a not-a-number page size out of the arithmetic', () => {
      bind(0, Number.NaN, 120);

      expect(Number.isNaN(component.totalPages)).withContext('never NaN').toBeFalse();
      expect(component.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('keeps an infinite page size out of the arithmetic', () => {
      bind(0, Number.POSITIVE_INFINITY, 120);

      expect(Number.isFinite(component.totalPages)).withContext('never Infinity').toBeTrue();
      expect(component.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('truncates a fractional page size instead of paging by a fraction', () => {
      bind(0, 2.5, 10);

      expect(component.pageSize).toBe(2);
      expect(Number.isInteger(component.totalPages)).toBeTrue();
      expect(component.totalPages).toBe(5);
    });

    it('emits nothing at all while the page size is unusable', () => {
      bind(3, 0, 5000);

      component.goFirst();
      component.goPrevious();
      component.goNext();
      component.goLast();

      expect(emitted).withContext('inert, not merely invisible').toEqual([]);
    });
  });

  describe('a total the API could not have produced', () => {
    it('treats a negative total as no records', () => {
      bind(0, 25, -5);

      expect(component.totalCount).toBe(0);
      expect(component.totalPages).toBe(0);
      expect(isRendered('.pagination')).toBeFalse();
    });

    it('keeps a not-a-number total out of the arithmetic', () => {
      bind(0, 25, Number.NaN);

      expect(Number.isNaN(component.totalPages)).toBeFalse();
      expect(component.totalPages).toBe(0);
    });

    it('truncates a fractional total rather than counting part of a record', () => {
      bind(0, 10, 25.9);

      expect(component.totalCount).toBe(25);
      expect(component.totalPages).toBe(3);
    });
  });

  describe('deriving the page count from the bound page size', () => {
    // MIGRATION: the page size is a PER-PORTAL SETTING and is NEVER assumed here, because the
    // two legacy paged screens did not even agree on it. Users.ascx.vb L114-L119 reads the
    // genuine `Records_PerPage` setting (`CType(setting, Integer)` at L117) whose default is 10
    // (UserModuleBase.vb L134-L136), while Portals.ascx.vb L92-L98 has that same read COMMENTED
    // OUT at L94-L95 and returns a hard-coded 20 at L96. A suite that only ever bound 10 could
    // not tell "reads the input" from "hard-codes a default", so several sizes are used below
    // and every expected number is written out literally rather than derived from a constant.

    it('reads a page size of 25, which neither legacy screen used', () => {
      // 120 over 25 is 5 pages: four full pages of 25 and a short fifth of 20. Neither legacy
      // page size could produce 5 from 120 — 20 would give 6 and 10 would give 12 — so this
      // number can only come from the bound input.
      bind(0, 25, 120);

      expect(component.totalPages).toBe(5);
      expect(renderedPageNumbers().total).withContext('and it reaches the screen').toBe(5);
    });

    it('reads a page size of 7', () => {
      // 30 over 7 is 5 pages: four full and a short one of 2.
      bind(0, 7, 30);

      expect(component.totalPages).toBe(5);
    });

    it('reads a page size of 20, the size the portals screen hard-coded', () => {
      // 120 over 20 is 6 pages, against the 5 that a size of 25 gives for the same total.
      // Binding both totals the same and only the size differently is what proves the size is
      // being read rather than assumed.
      bind(0, 20, 120);

      expect(component.totalPages).toBe(6);
    });

    it('reads a page size of 10, the size the account screen defaulted to', () => {
      bind(0, 10, 120);

      expect(component.totalPages).toBe(12);
    });

    it('rounds a partial final page up', () => {
      bind(0, 10, 21);

      expect(component.totalPages).withContext('two full pages and one short').toBe(3);
    });

    it('does not round an exact multiple up', () => {
      // The classic Math.ceil off-by-one: 20 over 10 is 2 pages, never 3.
      bind(0, 10, 20);

      expect(component.totalPages).toBe(2);
      expect(renderedPageNumbers().total).toBe(2);
    });

    it('reports a single page for a total below the page size', () => {
      bind(0, 25, 3);

      expect(component.totalPages).withContext('one short page').toBe(1);
    });

    it('never derives an infinite or negative page count', () => {
      bind(0, 1, 5000);

      expect(component.totalPages).toBe(5000);
      expect(Number.isFinite(component.totalPages)).toBeTrue();
      expect(component.totalPages).toBeGreaterThan(0);
    });
  });

  describe('the page base: zero across the boundary, one on the screen', () => {
    // The specifications in this block are the reason this file exists. Each one defends the
    // single off-by-one the whole migration hangs on, and not one of them can be caught by a
    // compiler: every value involved is a `number`, so both the right answer and the
    // off-by-one type-check identically.

    it('(a) renders the first page as the number one', () => {
      // Asserted on RENDERED TEXT, never on `displayPage` alone. A getter-only expectation
      // cannot catch a template that applies the `+ 1` a second time: the class would still
      // report 1 while the screen showed 2.
      bind(0, 10, 100);

      expect(positionText()).withContext('page index 0 reads as page 1').toBe('1 / 10');
      expect(renderedPageNumbers().shown).toBe(1);
    });

    it('(a) renders a later page one-based too', () => {
      // Index 2 is the THIRD page. Asserting only the first page would pass for a template
      // that ignored the index entirely and printed a constant.
      bind(2, 10, 100);

      expect(positionText()).toBe('3 / 10');
      expect(renderedPageNumbers().shown).toBe(3);
      expect(component.displayPage).toBe(3);
    });

    it('(b) emits ONE when stepping forward from the first page, never TWO', () => {
      // THE single most valuable expectation in this file. Emitting 2 is the double-increment
      // defect: it happens when the one-based display number is emitted instead of the
      // zero-based index, and the result is a pager that silently skips a page.
      bind(0, 10, 100);

      step(STEP.next).click();

      expect(emitted).withContext('the zero-based index of the second page').toEqual([1]);
      expect(emitted).not.toEqual([2]);
    });

    it('(c) emits the zero-based index of the page a person reads as the highest number', () => {
      // MIGRATION: the adaptation recorded in this file's header. There is no page-number
      // window to click — the component deliberately renders none — so the coupling between a
      // RENDERED LABEL and an EMITTED VALUE is asserted through the readout instead.
      //
      // The number 3 here is read OUT OF THE DOM rather than written into the expectation, so
      // the assertion genuinely joins what a person sees to what the component reports. With
      // 25 records at 10 a page a person reads "1 / 3", and the page labelled 3 is index 2.
      bind(0, 10, 25);

      const { total } = renderedPageNumbers();

      expect(total).withContext('the highest page number on screen').toBe(3);

      step(STEP.last).click();

      expect(emitted)
        .withContext('the page a person reads as 3 is index 2, one less than its label')
        .toEqual([total - 1]);
      expect(emitted).toEqual([2]);
    });

    it('(d) disables stepping back on the first page and emits nothing when it is clicked', () => {
      bind(0, 10, 25);

      // Half one: the unavailability is stated PROGRAMMATICALLY through the native `disabled`
      // property, so assistive technology and the pointer agree. Styling alone would leave a
      // keyboard user able to activate it.
      expect(step(STEP.previous).disabled)
        .withContext('already on the first page')
        .toBeTrue();
      expect(step(STEP.first).disabled).toBeTrue();

      // Half two: nothing is emitted. Clicking a disabled button is a no-op by the HTML
      // specification, so this proves the rendered state...
      step(STEP.previous).click();
      step(STEP.first).click();

      expect(emitted).toEqual([]);

      // ...and calling the handlers directly proves the CLASS'S OWN GUARD independently of
      // that, which is what keeps the emission contract safe if the template ever renders
      // these affordances as something other than a disabled button.
      component.goPrevious();
      component.goFirst();

      expect(emitted).withContext('no index below the first may ever be emitted').toEqual([]);
    });

    it('(e) disables stepping forward on the last page and emits nothing when it is clicked', () => {
      // The last index is COMPUTED here rather than written in, and the numbers are chosen so
      // that it is unambiguous: 120 records at 25 a page is 5 pages, so the last index is 4.
      // A size of 25 also means neither legacy default could produce this page count.
      const pageSize = 25;
      const totalCount = 120;
      const lastPageIndex = Math.ceil(totalCount / pageSize) - 1;

      expect(lastPageIndex).withContext('five pages, so index four is the last').toBe(4);

      bind(lastPageIndex, pageSize, totalCount);

      expect(step(STEP.next).disabled).withContext('already on the last page').toBeTrue();
      expect(step(STEP.last).disabled).toBeTrue();
      expect(positionText()).toBe('5 / 5');

      step(STEP.next).click();
      step(STEP.last).click();

      expect(emitted).toEqual([]);

      component.goNext();
      component.goLast();

      expect(emitted).withContext('no index beyond the last may ever be emitted').toEqual([]);
    });

    it('emits a zero-based index when stepping backward', () => {
      bind(4, 10, 100);

      step(STEP.previous).click();

      expect(emitted).withContext('from index 4 back to index 3').toEqual([3]);
    });

    it('emits zero for the first page, which is a real index and not an absence', () => {
      bind(4, 10, 100);

      step(STEP.first).click();

      expect(emitted).withContext('zero is the first page, never "no page"').toEqual([0]);
    });

    it('emits the zero-based index of the last page, one below the page count', () => {
      // 100 records at 10 a page is 10 pages, so the last index is 9 and never 10. This is the
      // same off-by-one as (b), at the other end of the range.
      bind(4, 10, 100);

      step(STEP.last).click();

      expect(emitted).toEqual([9]);
      expect(emitted).not.toEqual([10]);
    });

    it('emits once per step rather than repeating itself', () => {
      // A redundant second emission would make a store issue two requests for one click, so
      // the COUNT is asserted and not merely the value.
      bind(3, 10, 100);

      step(STEP.next).click();

      expect(emitted.length).withContext('exactly one request per click').toBe(1);
      expect(emitted).toEqual([4]);
    });

    it('emits nothing when the step would land on the page already shown', () => {
      // `goFirst` from the first page and `goLast` from the last both target the current page.
      // Emitting would send a store off to re-fetch what it is already displaying.
      bind(0, 20, 120);

      component.goFirst();

      expect(emitted).toEqual([]);
    });

    it('does not change its own page, so it cannot claim a page whose request failed', () => {
      bind(3, 10, 100);

      step(STEP.next).click();
      fixture.detectChanges();

      expect(component.page)
        .withContext('the consumer rebinds this only once the page has actually arrived')
        .toBe(3);
      expect(positionText()).withContext('and the screen still shows page 4').toBe('4 / 10');
    });
  });

  describe('the range summary', () => {
    // MIGRATION: the item range is a NET ADDITION. The legacy pager showed only
    // `Page {0} of {1}` — `Website/App_GlobalResources/SharedResources.resx` defines that
    // wording and `Library/Controls/PagingControl.vb` formatted it with the one-based page
    // number — and never named which records were on show. Both readouts are rendered here.

    it('names the range on show and the total, both one-based and absolute', () => {
      bind(1, 10, 34);

      expect(statusText()).toBe(`11${EN_DASH}20 of 34`);
    });

    it('clamps the last item to the total on a short final page', () => {
      // Index 3 at 10 a page would end at item 40, but only 34 records exist. A number beyond
      // the total would simply be wrong.
      bind(3, 10, 34);

      expect(statusText()).toBe(`31${EN_DASH}34 of 34`);
    });

    it('reports the range for a page size neither legacy screen used', () => {
      bind(2, 25, 120);

      expect(statusText()).toBe(`51${EN_DASH}75 of 120`);
    });

    it('never renders a not-a-number or an infinity, whatever is bound', () => {
      // Swept across the ordinary cases and every unusable one together, because a defect in
      // the guards would surface as one of these two words appearing on screen.
      const cases: readonly { readonly page: number; readonly size: number; readonly total: number }[] = [
        { page: 0, size: 10, total: 25 },
        { page: 2, size: 25, total: 120 },
        { page: 4, size: 7, total: 30 },
        { page: 11, size: 10, total: 30 },
        { page: 0, size: 0, total: 5000 },
        { page: 0, size: -25, total: 5000 },
        { page: 0, size: 20, total: 0 },
        { page: Number.NaN, size: Number.NaN, total: Number.NaN },
        {
          page: Number.POSITIVE_INFINITY,
          size: Number.POSITIVE_INFINITY,
          total: Number.POSITIVE_INFINITY,
        },
      ];

      for (const bound of cases) {
        bind(bound.page, bound.size, bound.total);

        const rendered = collapsed(host().textContent);
        const description = `page ${bound.page}, size ${bound.size}, total ${bound.total}`;

        expect(rendered).withContext(description).not.toContain('NaN');
        expect(rendered).withContext(description).not.toContain('Infinity');
        expect(rendered).withContext(description).not.toContain('undefined');
      }
    });

    it('is announced politely, because a page change replaces the list without moving focus', () => {
      bind(0, 10, 34);

      const status = element('.pagination__status');

      expect(status.getAttribute('aria-live'))
        .withContext('a status update must not interrupt')
        .toBe('polite');
      expect(status.getAttribute('aria-atomic'))
        .withContext('the whole sentence has to be re-read, not just the digits that changed')
        .toBe('true');
    });
  });

  describe('a page index past the end of the results', () => {
    // A REAL STATE rather than a fault: records can be removed between a page being requested
    // and rendered. The component preserves the bound index instead of rewriting the
    // consumer's state, and constrains it only where it is used — so the readout stays
    // truthful and no out-of-range index is ever emitted.

    it('reads as the last real page rather than a page the data cannot support', () => {
      bind(11, 10, 30);

      expect(positionText()).withContext('the last page of three, not page twelve').toBe('3 / 3');
    });

    it('reports the range of the last real page', () => {
      bind(11, 10, 30);

      expect(statusText()).toBe(`21${EN_DASH}30 of 30`);
    });

    it('offers no forward step beyond the end', () => {
      bind(11, 10, 30);

      expect(step(STEP.next).disabled).toBeTrue();
      expect(step(STEP.last).disabled).toBeTrue();
    });

    it('steps back into range rather than emitting an index that does not exist', () => {
      // Stepping back from a page that does not exist must reach the LAST REAL PAGE, not
      // index 10, which would skip the last real page entirely.
      bind(11, 10, 30);

      component.goPrevious();

      expect(emitted).toEqual([2]);
    });

    it('never emits an index outside the available range, from any step', () => {
      bind(11, 10, 30);

      component.goFirst();
      component.goPrevious();
      component.goNext();
      component.goLast();

      expect(emitted.length).withContext('at least one step must be available').toBeGreaterThan(0);

      for (const index of emitted) {
        expect(index).withContext('never below the first page').toBeGreaterThanOrEqual(0);
        expect(index).withContext('never beyond the last page').toBeLessThanOrEqual(2);
      }
    });

    it('treats a negative bound index as the first page', () => {
      bind(-5, 10, 100);

      expect(component.page).toBe(0);
      expect(positionText()).toBe('1 / 10');
    });

    it('treats a not-a-number bound index as the first page', () => {
      bind(Number.NaN, 10, 100);

      expect(component.page).toBe(0);
      expect(positionText()).toBe('1 / 10');
    });

    it('truncates a fractional bound index rather than paging by half a page', () => {
      bind(2.7, 10, 100);

      expect(component.page).toBe(2);
      expect(positionText()).toBe('3 / 10');
    });
  });

  describe('the derivations when there is nothing to page through', () => {
    // Nothing renders in this state, so these are read directly. They still have to answer
    // coherently, because a later template change must not be the moment a not-a-number first
    // reaches the screen.

    it('reports the first page and an empty range rather than an undefined position', () => {
      bind(0, 10, 0);

      expect(component.displayPage).withContext('page one of nothing, not page zero').toBe(1);
      expect(component.firstItemNumber).withContext('"0 to 0 of 0", not "1 to 0 of 0"').toBe(0);
      expect(component.lastItemNumber).toBe(0);
    });

    it('offers no step in either direction', () => {
      bind(0, 10, 0);

      expect(component.canGoPrevious).toBeFalse();
      expect(component.canGoNext).toBeFalse();
    });

    it('reports an empty range when the page size never resolved', () => {
      bind(4, 0, 120);

      expect(component.firstItemNumber).toBe(0);
      expect(component.lastItemNumber).toBe(0);
      expect(component.displayPage).toBe(1);
      expect(component.isNavigable).toBeFalse();
    });
  });

  describe('accessibility', () => {
    it('marks the page on show with aria-current, on exactly one element', () => {
      // Exactly one, because `aria-current` identifies THE current item: a second one would
      // leave a screen-reader user with two "current" positions and no way to choose.
      bind(1, 10, 25);

      const marked = fixture.debugElement.queryAll(By.css('[aria-current]'));

      expect(marked.length).withContext('one current position, never two').toBe(1);
      expect(element('.pagination__position').getAttribute('aria-current')).toBe('page');
    });

    it('marks the position readout, which is what identifies the current page here', () => {
      // With no page-link window to mark — the class exposes none — this readout is the
      // direct successor to the legacy inert bracketed marker `<span>[3]</span>`, which had
      // nothing in the accessibility tree at all.
      bind(2, 25, 120);

      const marked = fixture.debugElement.queryAll(By.css('[aria-current="page"]'));

      expect(marked.length).toBe(1);
      expect(positionText()).withContext('and it carries the human number').toBe('3 / 5');
    });

    it('renders NO navigation landmark, because the application shell owns the only one', () => {
      // A second `nav` would announce a duplicate landmark and add a spurious entry to the
      // landmark list on every list screen. A named group is the correct pattern for a
      // labelled cluster of related controls.
      bind(0, 10, 100);

      expect(host().querySelector('nav')).withContext('landmarks belong to the shell').toBeNull();
    });

    it('names the group of controls without pretending to be a landmark', () => {
      bind(0, 10, 100);

      const group = element('.pagination');

      expect(group.getAttribute('role')).toBe('group');
      expect(group.getAttribute('aria-label')).toBe('Pagination');
    });

    it('names every step, so none is announced as a punctuation character', () => {
      bind(4, 10, 100);

      expect(steps().map((each) => each.getAttribute('aria-label'))).toEqual([
        STEP.first,
        STEP.previous,
        STEP.next,
        STEP.last,
      ]);
    });

    it('hides the glyphs from assistive technology', () => {
      bind(4, 10, 100);

      for (const each of steps()) {
        const glyph = each.querySelector('span');

        if (glyph === null) {
          throw new Error('Expected every step to render its glyph inside a span.');
        }

        expect(glyph.getAttribute('aria-hidden')).toBe('true');
      }
    });

    it('declares an explicit button type, so a pager inside a form cannot submit it', () => {
      // Without it a button defaults to `submit`, and changing page would post the
      // surrounding form.
      bind(4, 10, 100);

      for (const each of steps()) {
        expect(each.getAttribute('type'))
          .withContext(`the ${each.getAttribute('aria-label')} step`)
          .toBe('button');
      }
    });

    it('renders the position as text rather than as a disabled control', () => {
      // A disabled button here would put an unusable stop in the tab order for something
      // there is nothing to activate on.
      bind(4, 10, 100);

      expect(element('.pagination__position').tagName).toBe('SPAN');
    });

    it('keeps the unavailable steps in the document so the control cannot reflow', () => {
      bind(0, 10, 100);

      expect(steps().length).withContext('disabled, not removed').toBe(4);
    });
  });

  describe('the public surface', () => {
    /**
     * The compiled component definition, narrowed by runtime checks at every step.
     *
     * Angular publishes no supported way to read a component's change-detection strategy or
     * its declared inputs, so the compiled definition is read instead. Every member is taken
     * as `unknown` and PROVED before use, so a future framework change fails here with a
     * message naming what was missing rather than throwing from inside an expectation.
     */
    function definition(): {
      readonly onPush: boolean;
      readonly inputNames: readonly string[];
      readonly outputNames: readonly string[];
    } {
      const compiled: unknown = Reflect.get(PaginationComponent, 'ɵcmp');

      if (typeof compiled !== 'object' || compiled === null) {
        throw new Error('Expected the compiled component definition to be readable.');
      }

      const onPush: unknown = Reflect.get(compiled, 'onPush');
      const inputs: unknown = Reflect.get(compiled, 'inputs');
      const outputs: unknown = Reflect.get(compiled, 'outputs');

      if (typeof onPush !== 'boolean') {
        throw new Error('Expected the definition to report a change-detection strategy.');
      }

      if (typeof inputs !== 'object' || inputs === null) {
        throw new Error('Expected the definition to report its inputs.');
      }

      if (typeof outputs !== 'object' || outputs === null) {
        throw new Error('Expected the definition to report its outputs.');
      }

      return {
        onPush,
        inputNames: Object.keys(inputs).sort(),
        outputNames: Object.keys(outputs),
      };
    }

    it('declares the on-push change-detection strategy the migration mandates', () => {
      expect(definition().onPush).toBeTrue();
    });

    it('accepts exactly three inputs and publishes exactly one output', () => {
      // The surface is closed at `page`, `pageSize` and `totalCount` in and `pageChange` out.
      // A fourth input is a deviation rather than a convenience — it is also what would let a
      // feature smuggle the server's own page count in and bypass the derivation this
      // component owns.
      expect(definition().inputNames).toEqual(['page', 'pageSize', 'totalCount']);
      expect(definition().outputNames).toEqual(['pageChange']);
    });

    it('exposes the derivations a consumer reads, and reports them consistently', () => {
      bind(2, 25, 120);

      expect(component.totalPages).toBe(5);
      expect(component.displayPage).toBe(3);
      expect(component.isNavigable).toBeTrue();
      expect(component.canGoPrevious).toBeTrue();
      expect(component.canGoNext).toBeTrue();
      expect(component.firstItemNumber).toBe(51);
      expect(component.lastItemNumber).toBe(75);
    });
  });
});
