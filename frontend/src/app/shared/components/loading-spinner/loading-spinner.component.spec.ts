//
// Specification for `LoadingSpinnerComponent` — the shared asynchronous progress
// indicator of the dnn-migration administration front end, an Angular 19
// single-page application.
//
// ---------------------------------------------------------------------------
// NO PREDECESSOR SUITE EXISTS
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated
// test suite of any kind — not a unit test, not a fixture, not a test project,
// nothing anywhere under `Library/` or `Website/`. Every expectation below is
// therefore net-new coverage with no legacy assertion to port.
//
// MIGRATION: net-new coverage of a net-new affordance. The legacy application had
// no reusable progress indicator at all: work happened during a synchronous
// postback, so the browser's own navigation spinner was the only feedback a user
// ever received. Nothing in the legacy stylesheets declares a spinner, progress,
// loading, throbber or busy selector, so there is no legacy appearance to
// preserve and no legacy behaviour to mirror. What IS asserted here is the
// contract the sibling component publishes, and nothing beyond it.
//
// ---------------------------------------------------------------------------
// TEST FRAMEWORK
// ---------------------------------------------------------------------------
// Karma with Jasmine, deliberately and not interchangeably. The mandated
// validation command is
//
//   ng test --watch=false --browsers=ChromeHeadless --code-coverage
//
// and `--browsers` is a Karma option, so a different runner would make that
// command invalid.
//
// ---------------------------------------------------------------------------
// WHY NO PROVIDERS AND NO COMPUTED STYLE
// ---------------------------------------------------------------------------
// The component under test injects nothing — it declares two inputs and three
// host bindings and that is the whole of it. Configuring an HTTP provider, a
// router provider or an animations provider would be noise a future reader would
// have to disprove, so none is configured.
//
// Nor does anything here read resolved computed styling. The size contract is expressed
// as a reflected `data-size` ATTRIBUTE precisely so it can be asserted without
// depending on whether a stylesheet was bundled into the test run. Asserting
// rendered pixel dimensions would couple this suite to the global style pipeline
// and make it fail for reasons that have nothing to do with this component.
//

import { Component } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';

import { LoadingSpinnerComponent, type LoadingSpinnerSize } from './loading-spinner.component';

// ---------------------------------------------------------------------------
// Measured contract values
// ---------------------------------------------------------------------------

/**
 * The component's declared default size, restated here rather than imported.
 *
 * The sibling component keeps its defaults as literals on the input declarations,
 * so there is no exported symbol to import. Restating the value is what gives the
 * expectation teeth: a silent change to the default breaks this suite instead of
 * travelling with it.
 */
const DEFAULT_SIZE: LoadingSpinnerSize = 'medium';

/**
 * The component's declared default label.
 *
 * The trailing character is a single horizontal-ellipsis code point, not three
 * full stops. That distinction is asserted deliberately: a screen reader
 * announces the two differently, and a well-meaning substitution would change
 * what a user hears.
 */
const DEFAULT_LABEL = 'Loading…';

/** Every member of the published size union, in ascending visual order. */
const ALL_SIZES: readonly LoadingSpinnerSize[] = ['small', 'medium', 'large'];

/** The decorative element that carries the animation. */
const INDICATOR_SELECTOR = '.indicator';

/** The element that carries the accessible, visible wording. */
const LABEL_SELECTOR = '.label';

/**
 * Landmark elements and their ARIA role equivalents.
 *
 * A shared presentational component must contribute no landmark, because a
 * landmark inside a page region fragments the document outline that assistive
 * technology navigates by. The application shell owns every landmark; this
 * component owns none.
 */
const LANDMARK_SELECTORS: readonly string[] = [
  'header',
  'main',
  'nav',
  'footer',
  'aside',
  '[role="banner"]',
  '[role="main"]',
  '[role="navigation"]',
  '[role="contentinfo"]',
  '[role="complementary"]',
];

/**
 * Interactive elements and affordance roles.
 *
 * A progress indicator reports state; it never invites activation. Anything
 * focusable here would put a tab stop in the middle of a busy region, which is
 * exactly the sort of keyboard trap the accessibility requirements forbid.
 */
const INTERACTIVE_SELECTORS: readonly string[] = [
  'button',
  'a',
  'input',
  'select',
  'textarea',
  '[tabindex]',
  '[role="button"]',
  '[role="link"]',
];

/**
 * Legacy DotNetNuke stylesheet class tokens.
 *
 * These are the class names the legacy skin vocabulary used, measured from
 * `Website/Portals/_default/default.css`. They appear here as data to assert
 * ABSENCE: the migration replaces the legacy vocabulary with a token-based one,
 * so a legacy token reappearing in new markup would mean a stylesheet was ported
 * rather than re-authored.
 */
const LEGACY_CLASS_TOKENS: readonly string[] = [
  'Normal',
  'SubHead',
  'Head',
  'Help',
  'CommandButton',
  'DataGrid_',
  'WorkPanel',
  'Settings',
];

/**
 * Markup fixtures that prove wording is interpolated rather than trusted as
 * markup.
 *
 * These strings are hostile on purpose. Legacy resource values are untrusted
 * input — at least one in-scope resource entry holds live remote script markup —
 * and this component's label may originate from exactly such a value, so the
 * escaping guarantee has to be asserted rather than assumed.
 */
const SCRIPT_INJECTION_LABEL = '<script>alert(1)</script>';
const MARKUP_INJECTION_LABEL = '<b>Loading</b>';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Reads an element's rendered text, normalising the absent case to the empty
 * string. `Node.textContent` is nullable by specification; narrowing it here
 * keeps every call site free of nullability noise.
 */
function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

/**
 * Finds a required descendant, or fails the spec with a self-describing error.
 *
 * Throwing rather than merely expecting is what narrows the result type for the
 * caller: a returned element is statically non-nullable, so no assertion
 * operator is ever needed to read from it.
 */
function requireElement(root: ParentNode, selector: string): HTMLElement {
  const found: HTMLElement | null = root.querySelector<HTMLElement>(selector);

  if (found === null) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

/**
 * Collects every distinct CSS class token on an element and all of its
 * descendants, sorted for a stable, readable failure message.
 */
function classTokensWithin(root: HTMLElement): readonly string[] {
  const tokens = new Set<string>();

  const collect = (element: Element): void => {
    element.classList.forEach((token: string): void => {
      tokens.add(token);
    });
  };

  collect(root);
  root.querySelectorAll('*').forEach(collect);

  return [...tokens].sort();
}

/** The inputs a single render may configure. */
interface SpinnerInputs {
  readonly size?: LoadingSpinnerSize;
  readonly label?: string;
}

// ---------------------------------------------------------------------------
// Specification
// ---------------------------------------------------------------------------

describe('LoadingSpinnerComponent', () => {
  beforeEach(async () => {
    // A standalone component is registered through `imports`. The array-based
    // component registry Angular used before standalone APIs appears nowhere in
    // this workspace.
    await TestBed.configureTestingModule({
      imports: [LoadingSpinnerComponent],
    }).compileComponents();
  });

  /**
   * Creates and renders the component, optionally applying inputs first.
   *
   * `ComponentRef.setInput` is used rather than instance assignment for two
   * reasons. It resolves the name against the component's declared input map, so
   * a renamed or demoted input fails these expectations instead of passing them
   * silently. And it marks the component dirty, which is what makes the
   * assertions valid under the `OnPush` change-detection strategy the component
   * declares — a plain field write would leave the view unrendered.
   */
  const render = (inputs: SpinnerInputs = {}): ComponentFixture<LoadingSpinnerComponent> => {
    const fixture = TestBed.createComponent(LoadingSpinnerComponent);

    if (inputs.size !== undefined) {
      fixture.componentRef.setInput('size', inputs.size);
    }

    if (inputs.label !== undefined) {
      fixture.componentRef.setInput('label', inputs.label);
    }

    fixture.detectChanges();

    return fixture;
  };

  /** The component's own host element, which carries the accessibility contract. */
  const hostOf = (fixture: ComponentFixture<LoadingSpinnerComponent>): HTMLElement =>
    fixture.nativeElement as HTMLElement;

  // -- 1 -------------------------------------------------------------------
  describe('construction', () => {
    it('creates as a standalone component with no providers configured', () => {
      const fixture = render();

      expect(fixture.componentInstance).toBeInstanceOf(LoadingSpinnerComponent);
    });
  });

  // -- 2 -------------------------------------------------------------------
  describe('declared input defaults', () => {
    it('defaults `size` to the medium variant on the instance', () => {
      const fixture = render();

      expect(fixture.componentInstance.size).toBe(DEFAULT_SIZE);
    });

    it('defaults `label` to the measured default wording on the instance', () => {
      const fixture = render();

      expect(fixture.componentInstance.label).toBe(DEFAULT_LABEL);
    });

    it('reflects the defaulted size onto the host without any input being supplied', () => {
      // Asserting the instance field alone would not prove the host binding is
      // wired, so the rendered attribute is asserted as well.
      const fixture = render();

      expect(hostOf(fixture).getAttribute('data-size')).toBe(DEFAULT_SIZE);
    });

    it('renders the default wording as visible text without any input being supplied', () => {
      const fixture = render();

      expect(renderedTextOf(requireElement(hostOf(fixture), LABEL_SELECTOR)).trim()).toBe(DEFAULT_LABEL);
    });
  });

  // -- 3 -------------------------------------------------------------------
  describe('host accessibility contract', () => {
    it('announces itself as a polite status region', () => {
      const fixture = render();

      expect(hostOf(fixture).getAttribute('role')).toBe('status');
    });

    it('reports a busy state for as long as it is mounted', () => {
      const fixture = render();

      expect(hostOf(fixture).getAttribute('aria-busy')).toBe('true');
    });

    it('does not declare a redundant live region alongside the status role', () => {
      // `role="status"` is already an implicit polite live region. Declaring
      // `aria-live` as well is redundant and can cause a duplicate announcement.
      const fixture = render();

      expect(hostOf(fixture).getAttribute('aria-live')).toBeNull();
    });

    it('does not model itself as a determinate progress bar', () => {
      // The indicator is indeterminate: it has no value to report, so neither the
      // progressbar role nor `aria-valuenow` would be truthful.
      const host = hostOf(render());

      expect(host.getAttribute('role')).not.toBe('progressbar');
      expect(host.getAttribute('aria-valuenow')).toBeNull();
    });
  });

  // -- 4 -------------------------------------------------------------------
  describe('size variants', () => {
    ALL_SIZES.forEach((size: LoadingSpinnerSize): void => {
      it(`reflects the "${size}" size onto the host as a data attribute`, () => {
        const fixture = render({ size });

        expect(hostOf(fixture).getAttribute('data-size'))
          .withContext(`the "${size}" variant must reflect onto the host`)
          .toBe(size);
      });
    });

    it('produces a mutually distinct attribute value for every declared size', () => {
      // Distinctness is the property the stylesheet depends on: each variant
      // matches its own `:host([data-size='…'])` rule, so two sizes collapsing to
      // one value would silently make two variants render identically.
      const reflected = ALL_SIZES.map((size: LoadingSpinnerSize): string | null =>
        hostOf(render({ size })).getAttribute('data-size'),
      );

      expect(new Set(reflected).size).toBe(ALL_SIZES.length);
    });

    it('keeps the indicator present in every size variant', () => {
      ALL_SIZES.forEach((size: LoadingSpinnerSize): void => {
        const indicator = requireElement(hostOf(render({ size })), INDICATOR_SELECTOR);

        expect(indicator.tagName.toLowerCase())
          .withContext(`the "${size}" variant must still render an indicator`)
          .toBe('span');
      });
    });
  });

  // -- 5 -------------------------------------------------------------------
  describe('decorative indicator', () => {
    it('renders an indicator element', () => {
      const fixture = render();

      expect(hostOf(fixture).querySelector(INDICATOR_SELECTOR)).not.toBeNull();
    });

    it('hides the indicator from assistive technology', () => {
      // The indicator is pure decoration. Exposing it would either announce
      // nothing useful or compete with the label as a naming source.
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);

      expect(indicator.getAttribute('aria-hidden')).toBe('true');
    });

    it('gives the indicator no text of its own', () => {
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);

      expect(renderedTextOf(indicator).trim()).toBe('');
    });
  });

  // -- 6 -------------------------------------------------------------------
  describe('accessible visible label', () => {
    it('renders the supplied wording as visible text', () => {
      const fixture = render({ label: 'Saving portal' });

      expect(renderedTextOf(requireElement(hostOf(fixture), LABEL_SELECTOR)).trim()).toBe('Saving portal');
    });

    it('names the region through the visible label rather than a duplicate `aria-label`', () => {
      // A separate `aria-label` would introduce a second name that can diverge
      // from what is on screen, which is the defect this expectation forbids.
      const fixture = render({ label: 'Saving portal' });

      expect(hostOf(fixture).getAttribute('aria-label')).toBeNull();
    });

    it('does not hide the label from assistive technology', () => {
      const label = requireElement(hostOf(render({ label: 'Saving portal' })), LABEL_SELECTOR);

      expect(label.getAttribute('aria-hidden')).toBeNull();
    });
  });

  // -- 7 -------------------------------------------------------------------
  describe('empty label suppression', () => {
    it('renders no label element when the wording is empty', () => {
      // An empty element would still occupy layout and would announce an empty
      // name, so the control-flow block omits it entirely rather than rendering
      // it blank.
      const fixture = render({ label: '' });

      expect(hostOf(fixture).querySelector(LABEL_SELECTOR)).toBeNull();
    });

    it('still renders the decorative indicator when the wording is empty', () => {
      const fixture = render({ label: '' });

      expect(hostOf(fixture).querySelector(INDICATOR_SELECTOR)).not.toBeNull();
    });

    it('withdraws the live region entirely rather than announcing a nameless one', () => {
      // The two host modes are mutually exclusive, and this is the second of them.
      // A `role="status"` region with no accessible name announces a change with
      // nothing to announce, which is worse than silence: a screen reader reports
      // that something updated and cannot say what. So when there is no visible
      // wording the component stops claiming to be a live region at all and hides
      // itself from the accessibility tree instead, leaving the indicator as the
      // purely visual affordance it already is.
      const host = hostOf(render({ label: '' }));

      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');
    });

    it('announces itself as a named live region as soon as wording is supplied', () => {
      // The positive control for the rule above, and the reason it is not simply a
      // withdrawal of function: the moment there is something to announce, the
      // region and its busy state are both declared and the hiding is lifted.
      const host = hostOf(render({ label: DEFAULT_LABEL }));

      expect(host.getAttribute('role')).toBe('status');
      expect(host.getAttribute('aria-busy')).toBe('true');
      expect(host.getAttribute('aria-hidden')).toBeNull();
    });
  });

  // -- 8 -------------------------------------------------------------------
  describe('wording is interpolated, never trusted as markup', () => {
    it('renders a script payload as escaped plain text', () => {
      const label = requireElement(hostOf(render({ label: SCRIPT_INJECTION_LABEL })), LABEL_SELECTOR);

      expect(renderedTextOf(label)).toContain(SCRIPT_INJECTION_LABEL);
    });

    it('creates no script element from a script payload', () => {
      const label = requireElement(hostOf(render({ label: SCRIPT_INJECTION_LABEL })), LABEL_SELECTOR);

      expect(label.querySelector('script')).toBeNull();
    });

    it('renders inline markup as escaped plain text', () => {
      const label = requireElement(hostOf(render({ label: MARKUP_INJECTION_LABEL })), LABEL_SELECTOR);

      expect(renderedTextOf(label)).toContain(MARKUP_INJECTION_LABEL);
    });

    it('creates no element from inline markup', () => {
      const label = requireElement(hostOf(render({ label: MARKUP_INJECTION_LABEL })), LABEL_SELECTOR);

      expect(label.querySelector('b')).toBeNull();
    });
  });

  // -- 9 -------------------------------------------------------------------
  describe('document outline', () => {
    LANDMARK_SELECTORS.forEach((selector: string): void => {
      it(`contributes no "${selector}" landmark to the page outline`, () => {
        const fixture = render();

        expect(hostOf(fixture).querySelector(selector))
          .withContext(`a shared component must not contribute a "${selector}" landmark`)
          .toBeNull();
      });
    });
  });

  // -- 10 ------------------------------------------------------------------
  describe('non-interactive by construction', () => {
    INTERACTIVE_SELECTORS.forEach((selector: string): void => {
      it(`renders nothing matching "${selector}"`, () => {
        const fixture = render();

        expect(hostOf(fixture).querySelector(selector))
          .withContext(`a progress indicator must not render "${selector}"`)
          .toBeNull();
      });
    });

    it('puts no tab stop on its own host element', () => {
      const fixture = render();

      expect(hostOf(fixture).getAttribute('tabindex')).toBeNull();
    });
  });

  // -- 11 ------------------------------------------------------------------
  describe('styling vocabulary', () => {
    it('uses only this component\u2019s own class tokens', () => {
      const tokens = classTokensWithin(hostOf(render()));

      expect([...tokens].sort()).toEqual(['indicator', 'label']);
    });

    LEGACY_CLASS_TOKENS.forEach((legacyToken: string): void => {
      it(`emits no legacy "${legacyToken}" class token`, () => {
        const tokens = classTokensWithin(hostOf(render()));

        const offending = tokens.filter((token: string): boolean => token.includes(legacyToken));

        expect(offending)
          .withContext(`the legacy "${legacyToken}" vocabulary must not survive the migration`)
          .toEqual([]);
      });
    });
  });
});


// ===========================================================================
// SECOND SUITE - structural ordering, the attribute shape the stylesheet selects
// on, a static-template call site, sentinel discipline around a blank label, and
// re-render behaviour after first paint under OnPush change detection. Paths the
// suite above does not reach. The three wording and size constants declared above
// are shared rather than restated.
// ===========================================================================

/** Class of the decorative glyph. */
const INDICATOR_CLASS = 'indicator';

/** Class of the visible, announced label. */
const LABEL_CLASS = 'label';

/** A label carrying ordinary markup, which must render as visible text. */
const MARKUP_BEARING_LABEL = '<b>Loading portals…</b>';

/**
 * A label carrying a live script block.
 *
 * Legacy resource values are untrusted markup — a measured 76 of roughly 1,111
 * in-scope entries carry a tag and four carry script blocks — and the legacy
 * application encoded on output rather than trusting the stored value. This is
 * therefore a real input shape, not a contrived one. The publisher identifier
 * from the real value is deliberately not reproduced.
 */
const SCRIPT_BEARING_LABEL = '<script>document.title = "pwned";</script>';

/** Every landmark element a shared leaf component must never emit. */
const FORBIDDEN_LANDMARKS: readonly string[] = ['header', 'main', 'nav', 'footer'];

/** Anything that could take keyboard focus. This component must contain none. */
const FOCUSABLE_SELECTORS =
  'a[href],area[href],button,input,select,textarea,details,iframe,[contenteditable],[tabindex]';

/**
 * Attributes the component documents as deliberately absent.
 *
 * `aria-live` is redundant beside the status role and a second one would compete
 * with it. `aria-valuenow`, `aria-valuemin` and `aria-valuemax` belong to a
 * determinate progress bar and this indicator has no value semantics. `tabindex`
 * would put a non-interactive element in the tab order.
 */
const FORBIDDEN_ARIA_ATTRIBUTES: readonly string[] = [
  'aria-live',
  'aria-valuenow',
  'aria-valuemin',
  'aria-valuemax',
  'aria-atomic',
  'aria-relevant',
  'tabindex',
];

/**
 * A consumer call site writing both inputs as STATIC TEMPLATE LITERALS.
 *
 * This host is compile-time proof of two separate things that no runtime
 * assertion can establish. First, that both inputs are PUBLIC: the workspace
 * enables strict input access modifiers, so a non-public input would fail this
 * file's own compilation. Second, that the size union genuinely accepts an
 * unquoted template literal — writing `size="huge"` here would be a template type
 * error under strict template checking, which is the mechanism that makes the
 * closed union worth having.
 */
@Component({
  standalone: true,
  imports: [LoadingSpinnerComponent],
  template: ` <app-loading-spinner size="small" label="Loading portals…" /> `,
})
class LiteralCallSiteHostComponent {}

describe('LoadingSpinnerComponent — structural, sentinel and re-render guards', () => {
  beforeEach(async () => {
    // MANDATED HARNESS SHAPE: the component and the standalone test host are
    // registered through `imports`. A `declarations` array is neither used nor
    // available for standalone components, and a host omitted from `imports`
    // would be silently unresolvable at the moment it was created.
    await TestBed.configureTestingModule({
      imports: [LoadingSpinnerComponent, LiteralCallSiteHostComponent],
    }).compileComponents();
  });

  // -------------------------------------------------------------------------
  //  Harness helpers. Narrowing discipline applied without exception: every
  //  lookup is narrowed with `instanceof` or an explicit null guard, so no
  //  non-null assertion and no cast appears anywhere below.
  // -------------------------------------------------------------------------

  /** Creates the component and initialises its view. */
  const createSpinner = (): ComponentFixture<LoadingSpinnerComponent> => {
    const fixture = TestBed.createComponent(LoadingSpinnerComponent);
    fixture.detectChanges();

    return fixture;
  };

  /** The component's own host element. */
  const hostOf = (fixture: ComponentFixture<unknown>): HTMLElement => fixture.nativeElement;

  /** The single element matching a selector, or `null` when absent. */
  const elementIn = (root: ParentNode, selector: string): HTMLElement | null => {
    const found = root.querySelector(selector);

    return found instanceof HTMLElement ? found : null;
  };

  /** Trimmed text of the whole rendered subtree. */
  const renderedTextOf = (root: HTMLElement): string => (root.textContent ?? '').trim();

  // =========================================================================
  //  1. RENDERED STRUCTURE
  // =========================================================================
  describe('rendered structure', () => {
    it('renders one decorative glyph and one label, in that order, and nothing else', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      const indicator = elementIn(host, `.${INDICATOR_CLASS}`);
      const label = elementIn(host, `.${LABEL_CLASS}`);

      expect(indicator).not.toBeNull();
      expect(label).not.toBeNull();
      if (indicator === null || label === null) {
        return;
      }

      // Exactly one of each, so no duplicate glyph and no duplicate label can be
      // introduced without failing here.
      expect(host.querySelectorAll(`.${INDICATOR_CLASS}`).length).toBe(1);
      expect(host.querySelectorAll(`.${LABEL_CLASS}`).length).toBe(1);

      // Two element children and no more: the stylesheet's own class vocabulary
      // is closed, so a third element would be unstyled and unaccounted for.
      expect(host.children.length).toBe(2);
      expect(host.children[0]).toBe(indicator);
      expect(host.children[1]).toBe(label);
    });

    it('hides the glyph from assistive technology and gives it no text of its own', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      const indicator = elementIn(host, `.${INDICATOR_CLASS}`);
      expect(indicator).not.toBeNull();
      if (indicator === null) {
        return;
      }

      // The glyph conveys nothing a screen-reader user needs — the label already
      // says what is loading — so exposing it would add noise to a live region
      // that announces on every change.
      expect(indicator.getAttribute('aria-hidden')).toBe('true');
      expect(indicator.textContent).toBe('');

      // And it must be the ONLY hidden element, so nothing else is being quietly
      // removed from the accessibility tree.
      expect(host.querySelectorAll('[aria-hidden="true"]').length).toBe(1);
    });

    it('emits no landmark and no image asset', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      for (const landmark of FORBIDDEN_LANDMARKS) {
        expect(host.querySelectorAll(landmark).length)
          .withContext(`a shared leaf component must not emit a <${landmark}> landmark`)
          .toBe(0);
      }

      // The glyph is drawn entirely in CSS. The one legacy progress image in the
      // repository is reachable only from out-of-scope trees, and the workspace
      // ships no raster artwork, so no image reference may appear here.
      expect(host.querySelectorAll('img').length).toBe(0);
      expect(host.querySelectorAll('svg').length).toBe(0);
    });

    it('exposes nothing that can take keyboard focus', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // A progress indicator is not interactive. Anything focusable here would
      // insert a dead stop into the tab order of whatever screen renders it.
      expect(host.querySelectorAll(FOCUSABLE_SELECTORS).length).toBe(0);
      expect(host.hasAttribute('tabindex')).toBeFalse();
    });
  });

  // =========================================================================
  //  2. THE LIVE REGION ON THE HOST
  // =========================================================================
  describe('live region semantics', () => {
    it('declares the polite live region on the host element', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // `role="status"` IS an implicit polite live region. Declaring it on the
      // host rather than on a child is what makes the label the region's content.
      expect(host.getAttribute('role')).toBe('status');
    });

    it('never repeats the live region on a descendant', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // A nested region would leave two live regions competing to announce the
      // same text, which is heard as a duplicate announcement rather than as
      // extra care.
      expect(host.querySelectorAll('[role]').length).toBe(0);
      expect(host.querySelectorAll('[role="status"]').length).toBe(0);
    });

    it('declares a static busy state', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // The element is only in the DOM while something is in flight, so there is
      // nothing to toggle and nothing to lie about. A `false` here would state
      // the opposite of the component's entire purpose.
      expect(host.getAttribute('aria-busy')).toBe('true');
      expect(host.querySelectorAll('[aria-busy]').length).toBe(0);
    });

    it('declares no redundant or contradictory progress semantics', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      for (const attribute of FORBIDDEN_ARIA_ATTRIBUTES) {
        expect(host.hasAttribute(attribute))
          .withContext(`${attribute} is documented as deliberately absent from the host`)
          .toBeFalse();
        expect(host.querySelectorAll(`[${attribute}]`).length)
          .withContext(`${attribute} is documented as deliberately absent from every descendant`)
          .toBe(0);
      }

      // This indicator is INDETERMINATE, so the progressbar role would promise a
      // value it can never supply.
      expect(host.getAttribute('role')).not.toBe('progressbar');
    });

    it('gives the region no accessible name, so the visible text is what is announced', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // THE REASON THIS MATTERS. The status role takes its name from the author,
      // not from its contents, and does not require a name at all. An
      // `aria-label` here would introduce a SECOND name that can drift out of
      // step with the visible label — the screen reader would announce one thing
      // while the screen showed another. Leaving it unnamed is what keeps the two
      // in permanent parity.
      expect(host.hasAttribute('aria-label')).toBeFalse();
      expect(host.hasAttribute('aria-labelledby')).toBeFalse();
      expect(host.hasAttribute('aria-describedby')).toBeFalse();
    });

    it('announces exactly the text it displays', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // Parity, asserted directly: because the glyph carries no text and the host
      // carries no name, the whole announced content is the visible label. Any
      // hidden extra wording would break the guarantee above.
      expect(renderedTextOf(host)).toBe(DEFAULT_LABEL);
    });
  });

  // =========================================================================
  //  3. SIZE
  // =========================================================================
  describe('size reflection', () => {
    it('reflects the documented default size', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      // An independently restated literal, never read back from the instance, so
      // a silent change to the default fails here.
      expect(host.getAttribute('data-size')).toBe(DEFAULT_SIZE);
    });

    it('reflects every member of the closed size union', () => {
      for (const size of ALL_SIZES) {
        const fixture = createSpinner();
        fixture.componentRef.setInput('size', size);
        fixture.detectChanges();

        const host = hostOf(fixture);

        expect(host.getAttribute('data-size'))
          .withContext(`size="${size}" must reach the host as data-size`)
          .toBe(size);

        fixture.destroy();
      }
    });

    it('reflects the size as an attribute in the exact shape the stylesheet selects on', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('size', 'small');
      fixture.detectChanges();

      const host = hostOf(fixture);

      // THE REAL INTEGRATION RISK. The stylesheet keys every dimension off
      // `:host([data-size='…'])`. A size reflected as a class — or under any other
      // attribute name — would leave those rules permanently dead while the DOM
      // still looked perfectly reasonable, and every size would silently render
      // at the base diameter. Matching the selector directly is what catches that.
      expect(host.matches('[data-size="small"]')).toBeTrue();
      expect(host.matches('[data-size="medium"]')).toBeFalse();
      expect(host.matches('[data-size="large"]')).toBeFalse();

      // And the size must NOT also arrive as a class, which would invite the
      // stylesheet to be rewritten against the wrong hook later.
      expect(host.classList.contains('small')).toBeFalse();
      expect(host.classList.length).toBe(0);
    });

    it('carries the size supplied at a static template call site', () => {
      const fixture = TestBed.createComponent(LiteralCallSiteHostComponent);
      fixture.detectChanges();

      const host = elementIn(fixture.nativeElement, 'app-loading-spinner');
      expect(host).not.toBeNull();
      if (host === null) {
        return;
      }

      expect(host.getAttribute('data-size')).toBe('small');
      expect(renderedTextOf(host)).toBe('Loading portals…');
    });
  });

  // =========================================================================
  //  4. LABEL
  // =========================================================================
  describe('label rendering', () => {
    it('renders the documented default label', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      const label = elementIn(host, `.${LABEL_CLASS}`);
      expect(label).not.toBeNull();
      if (label === null) {
        return;
      }

      // The default is load-bearing: an indicator that announces nothing is an
      // accessibility defect, and that state must never be reachable by simply
      // omitting the input.
      expect((label.textContent ?? '').trim()).toBe(DEFAULT_LABEL);
    });

    it('renders a caller-supplied label', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('label', 'Loading roles…');
      fixture.detectChanges();

      const host = hostOf(fixture);
      const label = elementIn(host, `.${LABEL_CLASS}`);

      expect(label).not.toBeNull();
      if (label === null) {
        return;
      }

      expect((label.textContent ?? '').trim()).toBe('Loading roles…');
    });

    it('omits the label element entirely for an explicitly empty label', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('label', '');
      fixture.detectChanges();

      const host = hostOf(fixture);

      // A caller that genuinely wants a bare indicator passes an empty string,
      // and the element is removed rather than rendered blank — an empty element
      // inside a live region would still be announced as a change.
      expect(host.querySelectorAll(`.${LABEL_CLASS}`).length).toBe(0);
      expect(renderedTextOf(host)).toBe('');
    });

    it('still renders the glyph when the label is suppressed', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('label', '');
      fixture.detectChanges();

      const host = hostOf(fixture);

      // Suppressing the label must not suppress the indicator: a bare indicator
      // is still an indicator, and this is what stops the whole component
      // collapsing to nothing for that caller.
      expect(host.querySelectorAll(`.${INDICATOR_CLASS}`).length).toBe(1);
      expect(host.children.length).toBe(1);

      // What DOES change is the host's accessibility contract. Without wording
      // there is no accessible name, and a nameless live region announces a
      // change it cannot describe - so the region is withdrawn and the host is
      // hidden from assistive technology, leaving the indicator visual-only.
      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');
    });

    it('treats a whitespace-only label as no label at all, exactly like the empty string', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('label', '   ');
      fixture.detectChanges();

      const host = hostOf(fixture);

      // SENTINEL DISCIPLINE, corrected. The legacy null-string sentinel is the
      // empty string, so an exact emptiness test looks faithful - but the question
      // this component actually has to answer is whether it has an accessible NAME
      // to announce, and three spaces are not a name. Rendering the element for
      // them produced the exact defect the empty-string branch exists to avoid: a
      // live region announcing whitespace. Emptiness is therefore decided on the
      // trimmed value, and the two inputs collapse to one behaviour.
      expect(host.querySelectorAll(`.${LABEL_CLASS}`).length).toBe(0);
      expect(host.children.length).toBe(1);
      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');
    });

    it('renders the label for wording that merely has surrounding white space', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('label', '  Loading portals  ');
      fixture.detectChanges();

      const host = hostOf(fixture);

      // The boundary on the other side: trimming decides PRESENCE only. A label
      // with real characters in it is rendered exactly as the caller supplied it,
      // padding included, because the component escapes and displays wording
      // rather than reformatting it.
      expect(host.querySelectorAll(`.${LABEL_CLASS}`).length).toBe(1);
      expect(host.getAttribute('role')).toBe('status');
      expect(host.querySelector(`.${LABEL_CLASS}`)?.textContent).toBe('  Loading portals  ');
    });

    it('renders a markup-bearing label as visible text', () => {
      const fixture = createSpinner();
      fixture.componentRef.setInput('label', MARKUP_BEARING_LABEL);
      fixture.detectChanges();

      const host = hostOf(fixture);
      const label = elementIn(host, `.${LABEL_CLASS}`);

      expect(label).not.toBeNull();
      if (label === null) {
        return;
      }

      expect((label.textContent ?? '').trim()).toBe(MARKUP_BEARING_LABEL);
      expect(host.querySelectorAll('b').length).toBe(0);
    });

    it('renders a script-bearing label as visible text and executes nothing', () => {
      const originalDocumentTitle = document.title;
      const fixture = createSpinner();

      fixture.componentRef.setInput('label', SCRIPT_BEARING_LABEL);
      fixture.detectChanges();

      const host = hostOf(fixture);
      const label = elementIn(host, `.${LABEL_CLASS}`);

      expect(label).not.toBeNull();
      if (label === null) {
        return;
      }

      // Interpolation escapes markup, which is exactly the behaviour required for
      // a value that may have come from an untrusted legacy resource. A raw-markup
      // binding would execute this.
      expect((label.textContent ?? '').trim()).toBe(SCRIPT_BEARING_LABEL);
      expect(host.querySelectorAll('script').length).toBe(0);
      expect(document.title).toBe(originalDocumentTitle);
    });

    it('re-renders when the label changes after first paint', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      expect(renderedTextOf(host)).toBe(DEFAULT_LABEL);

      fixture.componentRef.setInput('label', 'Loading users…');
      fixture.detectChanges();

      // OnPush change detection must not stop an input change from reaching the
      // DOM. A consumer that swaps the wording mid-flight — from a general
      // message to a specific one — has to see it take effect.
      expect(renderedTextOf(host)).toBe('Loading users…');

      fixture.componentRef.setInput('label', '');
      fixture.detectChanges();

      // And it must be able to go the other way too, removing the element rather
      // than leaving stale wording behind.
      expect(renderedTextOf(host)).toBe('');
      expect(host.querySelectorAll(`.${LABEL_CLASS}`).length).toBe(0);
    });

    it('re-renders when the size changes after first paint', () => {
      const fixture = createSpinner();
      const host = hostOf(fixture);

      expect(host.getAttribute('data-size')).toBe(DEFAULT_SIZE);

      fixture.componentRef.setInput('size', 'large');
      fixture.detectChanges();

      // The attribute must be REPLACED, not accumulated: an element cannot carry
      // two values of one attribute, so a stale value would win the stylesheet
      // match and the new size would never apply.
      expect(host.getAttribute('data-size')).toBe('large');
      expect(host.matches('[data-size="large"]')).toBeTrue();
      expect(host.matches(`[data-size="${DEFAULT_SIZE}"]`)).toBeFalse();
    });
  });
});
