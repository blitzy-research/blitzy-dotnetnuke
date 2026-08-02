//
// Specification for `LoadingSpinnerComponent` — the shared asynchronous progress
// indicator of the dnn-migration administration front end.
//
// MIGRATION: THERE IS NO PREDECESSOR SUITE, AND NO PREDECESSOR COMPONENT
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated
// test of any kind — not a unit test, not a fixture, not a test project, no
// dependency manifest and no test runner anywhere in the checkout. Every
// expectation below is therefore added coverage with no legacy assertion to
// port, and that is reported rather than dressed up as a translation.
//
// MIGRATION: the affordance under test has no legacy counterpart, established by
// measurement rather than assumed. The five in-scope admin trees are pure
// full-page-postback Web Forms — `UpdatePanel`, `UpdateProgress`,
// `ScriptManager` and `AsyncPostBack` measure zero occurrences across all of
// them — so the browser's own page-load indicator was the only progress
// feedback the legacy application ever gave. Neither legacy stylesheet declares
// a spinner, progress, loading, throbber or busy selector, and the one legacy
// progress image in the repository is reachable only from out-of-scope trees.
// There is consequently no legacy appearance to preserve and no legacy
// behaviour to mirror. What is asserted here is the contract the sibling
// component publishes, and nothing beyond it.
//
// TEST FRAMEWORK: KARMA WITH JASMINE, DELIBERATELY
// The mandated validation command is
//
//   ng test --watch=false --browsers=ChromeHeadless --code-coverage
//
// and `--browsers` is a Karma option, so adopting a different runner would make
// that command invalid. Jasmine spies and matchers only; no other test library
// is present in the pinned dependency set.
//
// WHY THIS FILE MATTERS MORE THAN A TYPICAL COMPONENT SPEC
// Two reasons, both structural.
//
// FIRST, this is the only route by which the component is type-checked at all.
// The application tsconfig compiles from `src/main.ts` by import graph, and
// nothing in the application imports this component yet, so a clean production
// build never touches it. The spec tsconfig includes every `*.spec.ts`, so this
// file importing the component is what pulls the component and its template
// into a type-checked program.
//
// SECOND, the component's TypeScript, template and stylesheet are authored
// independently. This suite is the mechanical guard against the three drifting
// apart: it asserts the rendered DOM and accessibility contract, which is the
// only surface all three must agree on.
//
// WHAT IS DELIBERATELY NOT CONFIGURED, AND NOT ASSERTED
// No provider is configured, because the component injects nothing: it has two
// inputs, four host bindings and one getter, and performs no I/O whatsoever.
// Configuring an HTTP, router or animation provider would be noise a later
// reader would have to disprove. The animation package is not even installed.
//
// Nothing here reads resolved computed styling either. The size contract is
// published as a reflected `data-size` ATTRIBUTE precisely so it can be
// asserted without depending on whether a global stylesheet reached the test
// bundle. Asserting rendered pixels, colours or animation timing would couple
// this suite to the style pipeline and let it fail for reasons that have
// nothing to do with this component — and the indicator is deliberately static,
// a reported design-system gap, so asserting motion would fail for the wrong
// reason entirely. Semantics and structure are asserted here; appearance is the
// stylesheet's own concern.
//

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';

import { LoadingSpinnerComponent, type LoadingSpinnerSize } from './loading-spinner.component';

// Contract values, restated rather than imported

/**
 * The component's published default size.
 *
 * Restated here rather than read back off the instance, which is what gives the
 * expectation teeth: reading the value from the object under test would make the
 * assertion travel with any change instead of catching it.
 */
const DEFAULT_SIZE: LoadingSpinnerSize = 'medium';

/**
 * The component's published default label.
 *
 * The final character is a single horizontal-ellipsis code point, not three full
 * stops. The distinction is asserted on purpose: a screen reader announces the
 * two differently, so a well-meaning substitution would change what a user
 * hears.
 */
const DEFAULT_LABEL = 'Loading…';

/** Every member of the published size union, in ascending visual order. */
const ALL_SIZES: readonly LoadingSpinnerSize[] = ['small', 'medium', 'large'];

/** The decorative glyph. */
const INDICATOR_SELECTOR = '.indicator';

/** The visible, announced wording. */
const LABEL_SELECTOR = '.label';

/** The complete class vocabulary the component is allowed to emit. */
const PERMITTED_CLASS_TOKENS: readonly string[] = ['indicator', 'label'];

/**
 * Landmark elements and their role equivalents.
 *
 * A shared leaf component must contribute no landmark: a landmark nested inside
 * a page region fragments the document outline that assistive technology
 * navigates by. The application shell owns every landmark; this component owns
 * none.
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
 * focusable here would plant a dead stop in the tab order of whatever screen
 * renders it.
 */
const INTERACTIVE_SELECTORS: readonly string[] = [
  'a[href]',
  'area[href]',
  'button',
  'input',
  'select',
  'textarea',
  'details',
  'iframe',
  '[contenteditable]',
  '[tabindex]',
  '[role="button"]',
  '[role="link"]',
];

/**
 * Attributes the component documents as deliberately absent.
 *
 * `aria-live` is redundant beside the status role, which is already an implicit
 * polite live region, and a second one would compete with it. The value
 * attributes belong to a determinate progress bar, and this indicator has no
 * value semantics to report. `tabindex` would put a non-interactive element in
 * the tab order.
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
 * Legacy DotNetNuke stylesheet class tokens, measured from the legacy portal
 * stylesheet and the in-scope admin markup.
 *
 * They appear here as data to assert ABSENCE. The migration re-authors the
 * legacy vocabulary against design tokens rather than porting it, so a legacy
 * token resurfacing in new markup would mean a stylesheet was carried over.
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
 * Wording fixtures that prove the label is interpolated, never trusted as
 * markup.
 *
 * These strings are hostile on purpose, and they model a real input shape
 * rather than a contrived one. Legacy resource values are untrusted markup: a
 * measured 76 of roughly 1,111 in-scope entries carry a tag and four carry
 * script blocks, one of them a live third-party advertising block that a naive
 * search misses because the tags are stored escaped. A label sourced from
 * legacy wording is therefore untrusted HTML, and the escaping guarantee has to
 * be asserted rather than assumed. The publisher identifier from the real value
 * is deliberately not reproduced.
 */
const SCRIPT_BEARING_LABEL = '<script>document.title = "pwned";</script>';
const MARKUP_BEARING_LABEL = '<b>Loading portals…</b>';

// Helpers

/**
 * Finds a required descendant, or fails the spec with a self-describing error.
 *
 * Throwing rather than merely expecting is what narrows the result for every
 * caller: the returned element is statically non-nullable, so no assertion
 * operator and no cast is ever needed to read from it, and a contract break
 * surfaces as a named, readable failure instead of a type error on a null
 * value. Where absence is the point, callers keep the raw nullable result of
 * `querySelector` instead and assert that it is null.
 */
function requireElement(root: ParentNode, selector: string): HTMLElement {
  const found: HTMLElement | null = root.querySelector<HTMLElement>(selector);

  if (found === null) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

/**
 * Reads an element's rendered text.
 *
 * `Node.textContent` is nullable by specification; normalising the absent case
 * to the empty string here keeps every call site free of nullability noise.
 */
function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

/**
 * Collects every distinct class token on an element and all of its descendants,
 * sorted for a stable and readable failure message.
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

/** The inputs a single render may configure. Both are optional, as on the component. */
interface SpinnerInputs {
  readonly size?: LoadingSpinnerSize;
  readonly label?: string;
}

/**
 * A consumer call site that writes both inputs as STATIC TEMPLATE LITERALS.
 *
 * This host is compile-time proof of two things no runtime expectation can
 * establish. First, that both inputs are PUBLIC: the workspace enables strict
 * input access modifiers, so an input demoted to protected or private would fail
 * this file's own compilation at the line below. Second, that the size union
 * genuinely accepts an unquoted attribute literal — writing `size="huge"` here
 * would be a template type error under strict template checking, which is the
 * mechanism that makes a closed union worth having in the first place.
 *
 * Its dependency array is written across several lines on purpose, so that the
 * single-line registration form appears exactly once in this file: in the test
 * module below, which is the one place a reader should look for it.
 */
@Component({
  selector: 'app-literal-call-site-host',
  standalone: true,
  imports: [
    LoadingSpinnerComponent,
  ],
  template: '<app-loading-spinner size="small" label="Loading portals…" />',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class LiteralCallSiteHostComponent {}

// Specification

describe('LoadingSpinnerComponent', () => {
  beforeEach(async () => {
    // A standalone component is registered through `imports`. The array-based
    // component registry Angular used before standalone APIs is neither used nor
    // available anywhere in this workspace.
    await TestBed.configureTestingModule({
      imports: [LoadingSpinnerComponent],
    }).compileComponents();
  });

  /**
   * Creates and renders the component, applying inputs first when supplied.
   *
   * `ComponentRef.setInput` is used rather than instance assignment for two
   * reasons. It resolves each name against the component's published input map,
   * so a renamed or demoted input fails these expectations instead of passing
   * them silently. And it marks the component dirty, which is what makes every
   * assertion below valid under the OnPush change-detection strategy the
   * component declares — a plain field write would leave the view unrendered and
   * the suite would pass against stale DOM.
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
  const hostOf = (fixture: ComponentFixture<unknown>): HTMLElement => fixture.nativeElement;

  // == 1. CONSTRUCTION ======================================================
  describe('construction', () => {
    it('creates as a standalone component with no provider configured', () => {
      const fixture = render();

      expect(fixture.componentInstance).toBeInstanceOf(LoadingSpinnerComponent);
    });

    it('renders its view on first change detection without any input being supplied', () => {
      const fixture = render();

      expect(hostOf(fixture).children.length).toBeGreaterThan(0);
    });
  });

  // == 2. PUBLISHED INPUT DEFAULTS ==========================================
  describe('published input defaults', () => {
    it('defaults the size to the medium variant on the instance', () => {
      const fixture = render();

      expect(fixture.componentInstance.size).toBe(DEFAULT_SIZE);
    });

    it('defaults the label to the published wording on the instance', () => {
      const fixture = render();

      expect(fixture.componentInstance.label).toBe(DEFAULT_LABEL);
    });

    it('reflects the defaulted size onto the host with no input supplied', () => {
      // Asserting the instance field alone would not prove the host binding is
      // wired, so the rendered attribute is asserted as well. A broken binding
      // cannot pass both.
      const fixture = render();

      expect(hostOf(fixture).getAttribute('data-size')).toBe(DEFAULT_SIZE);
    });

    it('renders the default wording as visible text with no input supplied', () => {
      const fixture = render();

      expect(renderedTextOf(requireElement(hostOf(fixture), LABEL_SELECTOR)).trim()).toBe(DEFAULT_LABEL);
    });
  });

  // == 3. HOST ACCESSIBILITY CONTRACT =======================================
  describe('host accessibility contract', () => {
    it('announces itself as a polite status region', () => {
      const fixture = render();

      expect(hostOf(fixture).getAttribute('role')).toBe('status');
    });

    it('never declares a busy state that could defer its own announcement', () => {
      // THIS EXPECTATION IS INVERTED FROM WHAT INTUITION SUGGESTS, so the reason
      // is recorded rather than assumed. `aria-busy="true"` on a live region is
      // not an extra hint that work is happening; it is an instruction to
      // assistive technology to WITHHOLD the region's contents until busy turns
      // false, so a half-built region is never read out mid-construction. Honouring
      // that contract requires someone to clear the flag. This component cannot:
      // the caller renders it only while work is in flight and REMOVES it when the
      // work finishes, so there is no later moment at which busy could become
      // false. A permanent busy state therefore defers an announcement that is
      // never released, suppressing the very message the status role exists to
      // deliver. The region is mounted with its label already in place, so it is
      // complete from its first frame and has nothing to defer.
      const fixture = render();

      expect(hostOf(fixture).getAttribute('aria-busy')).toBeNull();
      expect(hostOf(fixture).hasAttribute('aria-busy')).toBeFalse();
    });

    it('leaves no residual busy state anywhere once loading completes', () => {
      // THE COMPLETION-STATE ASSERTION. For this component "loading completed"
      // means the element is gone: it owns no loading input and is unmounted by
      // its caller rather than switched off. So the only way it CAN satisfy a
      // clear-busy-on-completion contract is to never publish a busy state in the
      // first place, and this proves that holds across the whole lifecycle - at
      // mount, and after the destruction that represents completion. The host
      // reference is captured BEFORE destroy so the detached element can still be
      // inspected afterwards; a latched attribute would survive on it.
      const fixture = render();
      const host = hostOf(fixture);

      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('role')).toBe('status');
      expect(host.querySelectorAll('[aria-busy]').length).toBe(0);

      fixture.destroy();

      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.querySelectorAll('[aria-busy]').length).toBe(0);
    });

    it('declares the live region on the host and never repeats it on a descendant', () => {
      // A nested region would leave two live regions competing to announce the
      // same text, which is heard as a duplicate announcement rather than as
      // extra care. `querySelectorAll` excludes the host itself, so this counts
      // descendants only.
      const host = hostOf(render());

      expect(host.querySelectorAll('[role]').length).toBe(0);
      expect(host.querySelectorAll('[aria-busy]').length).toBe(0);
    });

    it('declares no redundant or contradictory progress semantics', () => {
      const host = hostOf(render());

      for (const attribute of FORBIDDEN_ARIA_ATTRIBUTES) {
        expect(host.hasAttribute(attribute))
          .withContext(`${attribute} is documented as deliberately absent from the host`)
          .toBeFalse();
        expect(host.querySelectorAll(`[${attribute}]`).length)
          .withContext(`${attribute} is documented as deliberately absent from every descendant`)
          .toBe(0);
      }
    });

    it('does not model itself as a determinate progress bar', () => {
      // The indicator is indeterminate, so the progressbar role would promise a
      // value it can never supply.
      const host = hostOf(render());

      expect(host.getAttribute('role')).not.toBe('progressbar');
      expect(host.getAttribute('aria-valuenow')).toBeNull();
    });
  });

  // == 4. SIZE REFLECTION ===================================================
  describe('size reflection', () => {
    ALL_SIZES.forEach((size: LoadingSpinnerSize): void => {
      it(`reflects the "${size}" size onto the host as a data attribute`, () => {
        const fixture = render({ size });

        expect(hostOf(fixture).getAttribute('data-size'))
          .withContext(`the "${size}" variant must reflect onto the host`)
          .toBe(size);
      });
    });

    it('produces a mutually distinct attribute value for every published size', () => {
      // Distinctness is the property the stylesheet depends on: each variant
      // matches its own host-attribute rule, so two sizes collapsing to one
      // value would silently render two variants identically while the DOM still
      // looked reasonable.
      const reflected = ALL_SIZES.map((size: LoadingSpinnerSize): string | null =>
        hostOf(render({ size })).getAttribute('data-size'),
      );

      expect(new Set(reflected).size).toBe(ALL_SIZES.length);
    });

    it('reflects the size in the exact attribute shape the stylesheet selects on', () => {
      // THE REAL INTEGRATION RISK. The stylesheet keys every dimension off a host
      // attribute selector. A size reflected as a class instead — or under any
      // other attribute name — would leave those rules permanently dead and every
      // variant would render at the base diameter, with nothing in the DOM
      // looking wrong. Matching the selector directly is what catches that.
      const host = hostOf(render({ size: 'small' }));

      expect(host.matches('[data-size="small"]')).toBeTrue();
      expect(host.matches('[data-size="medium"]')).toBeFalse();
      expect(host.matches('[data-size="large"]')).toBeFalse();

      // And the size must not ALSO arrive as a class, which would invite the
      // stylesheet to be rewritten against the wrong hook later.
      expect(host.classList.contains('small')).toBeFalse();
      expect(host.classList.length).toBe(0);
    });

    it('keeps the indicator present in every size variant', () => {
      ALL_SIZES.forEach((size: LoadingSpinnerSize): void => {
        const indicator = requireElement(hostOf(render({ size })), INDICATOR_SELECTOR);

        expect(indicator.tagName.toLowerCase())
          .withContext(`the "${size}" variant must still render an indicator`)
          .toBe('span');
      });
    });

    it('carries the size supplied at a static template call site', () => {
      // Exercises the component the way a feature template will, rather than
      // through the programmatic input API, so a binding that works only for
      // `setInput` cannot pass.
      const fixture = TestBed.createComponent(LiteralCallSiteHostComponent);
      fixture.detectChanges();

      const spinnerHost = requireElement(fixture.nativeElement, 'app-loading-spinner');

      expect(spinnerHost.getAttribute('data-size')).toBe('small');
      expect(renderedTextOf(spinnerHost).trim()).toBe('Loading portals…');
    });
  });

  // == 5. DECORATIVE INDICATOR ==============================================
  describe('decorative indicator', () => {
    it('renders an indicator element', () => {
      const fixture = render();

      expect(hostOf(fixture).querySelector(INDICATOR_SELECTOR)).not.toBeNull();
    });

    it('hides the indicator from assistive technology', () => {
      // The glyph is pure decoration: the label already says what is loading, so
      // exposing the glyph would only add noise to a region that announces on
      // every change. This is what keeps it out of the accessibility tree.
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);

      expect(indicator.getAttribute('aria-hidden')).toBe('true');
    });

    it('gives the indicator no text of its own', () => {
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);

      expect(renderedTextOf(indicator)).toBe('');
    });

    it('hides nothing else from assistive technology', () => {
      // Scoped so that the glyph is the ONLY element removed from the
      // accessibility tree, and nothing is being quietly hidden alongside it.
      const host = hostOf(render());

      expect(host.querySelectorAll('[aria-hidden="true"]').length).toBe(1);
    });

    it('draws the indicator without any image asset', () => {
      // The glyph is drawn entirely in CSS. The workspace ships no raster
      // artwork, and the one legacy progress image in the repository is reachable
      // only from out-of-scope trees, so no image reference may appear here.
      const host = hostOf(render());

      expect(host.querySelectorAll('img').length).toBe(0);
      expect(host.querySelectorAll('svg').length).toBe(0);
    });
  });

  // == 6. ACCESSIBLE NAME COMES FROM THE VISIBLE LABEL ======================
  describe('accessible visible label', () => {
    it('renders caller-supplied wording as visible text', () => {
      const fixture = render({ label: 'Saving portal' });

      expect(renderedTextOf(requireElement(hostOf(fixture), LABEL_SELECTOR)).trim()).toBe('Saving portal');
    });

    it('names the region through the visible label rather than a competing accessible name', () => {
      // WHY THIS MATTERS. The status role takes its accessible name from its
      // CONTENTS, and the visible label is that content. An `aria-label` here
      // would introduce a SECOND name that can drift out of step with what is on
      // screen — a screen reader would announce one thing while the screen showed
      // another. Leaving the host unnamed is what keeps the two in permanent
      // parity, so its absence is asserted rather than left to chance.
      const host = hostOf(render({ label: 'Saving portal' }));

      expect(host.getAttribute('aria-label')).toBeNull();
      expect(host.hasAttribute('aria-labelledby')).toBeFalse();
      expect(host.hasAttribute('aria-describedby')).toBeFalse();
    });

    it('does not hide the label from assistive technology', () => {
      const label = requireElement(hostOf(render({ label: 'Saving portal' })), LABEL_SELECTOR);

      expect(label.getAttribute('aria-hidden')).toBeNull();
    });

    it('announces exactly the text it displays', () => {
      // Parity asserted directly: because the glyph carries no text and the host
      // carries no name of its own, the whole announced content of the region is
      // the visible label. Any hidden extra wording would break the guarantee
      // above, and this is the expectation that would catch it.
      const host = hostOf(render());

      expect(renderedTextOf(host).trim()).toBe(DEFAULT_LABEL);
    });

    it('renders wording that merely has surrounding white space, padding included', () => {
      // The boundary on the presence side: emptiness is decided on the trimmed
      // value, but wording with real characters in it is rendered exactly as the
      // caller supplied it. The component displays and escapes wording; it does
      // not reformat it.
      const fixture = render({ label: '  Loading portals  ' });

      expect(renderedTextOf(requireElement(hostOf(fixture), LABEL_SELECTOR))).toBe('  Loading portals  ');
      expect(hostOf(fixture).getAttribute('role')).toBe('status');
    });
  });

  // == 7. BLANK LABEL SUPPRESSES THE LABEL, AND THE REGION WITH IT ==========
  describe('blank label suppression', () => {
    it('renders no label element when the wording is empty', () => {
      // Absence is the point here, so the raw nullable lookup is kept rather
      // than narrowed, and asserted to be null.
      const fixture = render({ label: '' });

      expect(hostOf(fixture).querySelector(LABEL_SELECTOR)).toBeNull();
    });

    it('still renders the decorative indicator when the wording is empty', () => {
      // Suppressing the label must not suppress the indicator: a bare indicator
      // is still an indicator, and this is what stops the whole component
      // collapsing to nothing for a caller that wants exactly that.
      const fixture = render({ label: '' });

      expect(hostOf(fixture).querySelector(INDICATOR_SELECTOR)).not.toBeNull();
      expect(hostOf(fixture).children.length).toBe(1);
    });

    it('withdraws the live region entirely rather than publishing a nameless one', () => {
      // The two host modes are mutually exclusive, and this is the second of
      // them. A status region takes its accessible name from its contents, and
      // with no wording its only child is the hidden glyph — so retaining the
      // role would publish a live region with neither content nor name, which can
      // never announce anything and merely occupies the accessibility tree. The
      // component therefore stops claiming to be a live region at all and hides
      // itself instead, leaving the glyph as the purely visual affordance it
      // already is. No busy state appears in this mode either, consistent with the
      // labelled mode, which does not declare one at all.
      const host = hostOf(render({ label: '' }));

      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');
    });

    it('announces itself as a named live region as soon as wording is supplied', () => {
      // The positive control for the rule above, and the reason it is not simply
      // a withdrawal of function: the moment there is something to announce, the
      // region is declared and the hiding is lifted. The region is declared
      // WITHOUT a busy state, so the announcement is deliverable immediately
      // rather than deferred behind a flag nothing will ever clear.
      const host = hostOf(render({ label: DEFAULT_LABEL }));

      expect(host.getAttribute('role')).toBe('status');
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBeNull();
    });

    it('treats whitespace-only wording as no wording at all, exactly like the empty string', () => {
      // SENTINEL DISCIPLINE. The legacy null-string sentinel is the empty string,
      // so testing for exact emptiness looks faithful — but the question this
      // component actually has to answer is whether it has an accessible NAME to
      // announce, and three spaces are not a name. Rendering the element for them
      // would reproduce the very defect the empty branch exists to avoid: a live
      // region announcing whitespace. Both spellings of "no wording" therefore
      // collapse to one behaviour.
      const fixture = render({ label: '   ' });
      const host = hostOf(fixture);

      expect(host.querySelector(LABEL_SELECTOR)).toBeNull();
      expect(host.children.length).toBe(1);
      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');
    });
  });

  // == 8. WORDING IS INTERPOLATED, NEVER TRUSTED AS MARKUP ==================
  describe('wording is interpolated, never trusted as markup', () => {
    it('renders a script payload as escaped plain text', () => {
      const label = requireElement(hostOf(render({ label: SCRIPT_BEARING_LABEL })), LABEL_SELECTOR);

      expect(renderedTextOf(label)).toContain(SCRIPT_BEARING_LABEL);
    });

    it('constructs no script element from a script payload', () => {
      const fixture = render({ label: SCRIPT_BEARING_LABEL });
      const label = requireElement(hostOf(fixture), LABEL_SELECTOR);

      // Absence proves no real element was built: the payload reached the DOM as
      // text, not as markup. A raw-markup binding would have constructed this.
      expect(label.querySelector('script')).toBeNull();
      expect(hostOf(fixture).querySelectorAll('script').length).toBe(0);
    });

    it('executes nothing from a script payload', () => {
      // The observable consequence, asserted rather than inferred: the payload
      // would rewrite the document title if it ever ran.
      const titleBeforeRender = document.title;

      const label = requireElement(hostOf(render({ label: SCRIPT_BEARING_LABEL })), LABEL_SELECTOR);

      expect(renderedTextOf(label).trim()).toBe(SCRIPT_BEARING_LABEL);
      expect(document.title).toBe(titleBeforeRender);
    });

    it('renders inline markup as escaped plain text', () => {
      const label = requireElement(hostOf(render({ label: MARKUP_BEARING_LABEL })), LABEL_SELECTOR);

      expect(renderedTextOf(label)).toContain(MARKUP_BEARING_LABEL);
    });

    it('constructs no element from inline markup', () => {
      const fixture = render({ label: MARKUP_BEARING_LABEL });
      const label = requireElement(hostOf(fixture), LABEL_SELECTOR);

      expect(label.querySelector('b')).toBeNull();
      expect(hostOf(fixture).querySelectorAll('b').length).toBe(0);
    });
  });

  // == 9. DOCUMENT OUTLINE ==================================================
  describe('document outline', () => {
    LANDMARK_SELECTORS.forEach((selector: string): void => {
      it(`contributes no "${selector}" landmark to the page outline`, () => {
        const fixture = render();

        expect(hostOf(fixture).querySelector(selector))
          .withContext(`a shared leaf component must not contribute a "${selector}" landmark`)
          .toBeNull();
      });
    });
  });

  // == 10. NON-INTERACTIVE BY CONSTRUCTION ==================================
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

    it('holds no state beyond its two inputs, so it emits nothing and injects nothing', () => {
      // A progress indicator reports state and emits nothing. Asserting that the
      // instance's own declared surface is EXACTLY the two inputs proves both
      // halves at once: an output would appear here as an emitter field, and an
      // injected dependency would appear as a stored reference. Either would fail
      // this expectation rather than passing unnoticed.
      //
      // Angular attaches internal bookkeeping to every component instance under a
      // double-underscore key, so those keys are filtered out before the
      // component's own surface is judged. Filtering by that prefix rather than by
      // an exact internal name keeps the expectation stable across framework
      // patch versions without weakening what it actually checks.
      const fixture = render();

      const declaredSurface = Object.keys(fixture.componentInstance)
        .filter((key: string): boolean => !key.startsWith('__'))
        .sort();

      expect(declaredSurface).toEqual(['label', 'size']);
    });
  });

  // == 11. STYLING VOCABULARY ===============================================
  describe('styling vocabulary', () => {
    it('emits only its own class tokens', () => {
      const tokens = classTokensWithin(hostOf(render()));

      expect([...tokens]).toEqual([...PERMITTED_CLASS_TOKENS]);
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

  // == RENDERED STRUCTURE ===================================================
  describe('rendered structure', () => {
    it('renders one glyph and one label, in that order, and nothing else', () => {
      const host = hostOf(render());

      const indicator = requireElement(host, INDICATOR_SELECTOR);
      const label = requireElement(host, LABEL_SELECTOR);

      // Exactly one of each, so no duplicate glyph and no duplicate label can be
      // introduced without failing here.
      expect(host.querySelectorAll(INDICATOR_SELECTOR).length).toBe(1);
      expect(host.querySelectorAll(LABEL_SELECTOR).length).toBe(1);

      // Two element children and no more: the stylesheet's class vocabulary is
      // closed, so a third element would render unstyled and unaccounted for.
      // Order matters because the glyph precedes the wording visually.
      expect(host.children.length).toBe(2);
      expect(host.children[0]).toBe(indicator);
      expect(host.children[1]).toBe(label);
    });

    it('renders both parts as generic inline elements', () => {
      // Neither part carries meaning of its own: the glyph is decorative and the
      // wording is the region's content. A semantic element here would add an
      // implicit role that competes with the host's.
      const host = hostOf(render());

      expect(requireElement(host, INDICATOR_SELECTOR).tagName.toLowerCase()).toBe('span');
      expect(requireElement(host, LABEL_SELECTOR).tagName.toLowerCase()).toBe('span');
    });
  });

  // == RE-RENDER AFTER FIRST PAINT UNDER ONPUSH =============================
  describe('re-render after first paint', () => {
    it('re-renders when the label changes after first paint', () => {
      const fixture = render();
      const host = hostOf(fixture);

      expect(renderedTextOf(host).trim()).toBe(DEFAULT_LABEL);

      fixture.componentRef.setInput('label', 'Loading users…');
      fixture.detectChanges();

      // OnPush must not stop an input change from reaching the DOM. A consumer
      // that swaps general wording for something specific mid-flight has to see
      // it take effect.
      expect(renderedTextOf(host).trim()).toBe('Loading users…');

      fixture.componentRef.setInput('label', '');
      fixture.detectChanges();

      // And it must go the other way too, removing the element rather than
      // leaving stale wording behind.
      expect(renderedTextOf(host)).toBe('');
      expect(host.querySelector(LABEL_SELECTOR)).toBeNull();
    });

    it('re-renders when the size changes after first paint', () => {
      const fixture = render();
      const host = hostOf(fixture);

      expect(host.getAttribute('data-size')).toBe(DEFAULT_SIZE);

      fixture.componentRef.setInput('size', 'large');
      fixture.detectChanges();

      // The attribute must be REPLACED, not accumulated: an element cannot carry
      // two values of one attribute, so a stale value left behind would win the
      // stylesheet match and the new size would never apply.
      expect(host.getAttribute('data-size')).toBe('large');
      expect(host.matches('[data-size="large"]')).toBeTrue();
      expect(host.matches(`[data-size="${DEFAULT_SIZE}"]`)).toBeFalse();
    });

    it('restores the live region when wording returns after being cleared', () => {
      // The full round trip through both host modes, which neither single-mode
      // expectation covers: a consumer may clear and then restore the wording on
      // one mounted instance, and the accessibility contract has to follow it
      // both ways rather than latching.
      const fixture = render();
      const host = hostOf(fixture);

      fixture.componentRef.setInput('label', '');
      fixture.detectChanges();

      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');

      fixture.componentRef.setInput('label', 'Loading roles…');
      fixture.detectChanges();

      expect(host.getAttribute('role')).toBe('status');
      expect(host.getAttribute('aria-hidden')).toBeNull();
      expect(renderedTextOf(requireElement(host, LABEL_SELECTOR)).trim()).toBe('Loading roles…');

      // No busy state is acquired on the way back either, so restoring the wording
      // cannot reintroduce the deferral this contract exists to avoid.
      expect(host.getAttribute('aria-busy')).toBeNull();
    });
  });
});
