// TEST FRAMEWORK: KARMA WITH JASMINE, DELIBERATELY The mandated validation command is

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';

import { LoadingSpinnerComponent, type LoadingSpinnerSize } from './loading-spinner.component';

// Contract values, restated rather than imported

/** The component's published default size. */
const DEFAULT_SIZE: LoadingSpinnerSize = 'medium';

/**
 * The component's published default label. The final character is a single horizontal-ellipsis code
 * point, not three full stops.
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

/** Landmark elements and their role equivalents. */
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
 * Interactive elements and affordance roles. A progress indicator reports state; it never invites
 * activation.
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
 * Attributes the component documents as deliberately absent. `aria-live` is redundant beside the status
 * role, which is already an implicit polite live region, and a second one would compete with it.
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
 * Legacy DotNetNuke stylesheet class tokens, measured from the legacy portal stylesheet and the in-scope
 * admin markup. They appear here as data to assert ABSENCE. The migration re-authors the legacy
 * vocabulary against design tokens rather than porting it, so a legacy token resurfacing in new markup
 * would mean a stylesheet was carried over.
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
 * Wording fixtures that prove the label is interpolated, never trusted as markup. These strings are
 * hostile on purpose, and they model a real input shape rather than a contrived one.
 */
const SCRIPT_BEARING_LABEL = '<script>document.title = "pwned";</script>';
const MARKUP_BEARING_LABEL = '<b>Loading portals…</b>';

// Helpers

/** Finds a required descendant, or fails the spec with a self-describing error. */
function requireElement(root: ParentNode, selector: string): HTMLElement {
  const found: HTMLElement | null = root.querySelector<HTMLElement>(selector);

  if (found === null) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

/** Reads an element's rendered text. */
function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

/**
 * Collects every distinct class token on an element and all of its descendants, sorted for a stable and
 * readable failure message.
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

/**
 * A consumer call site that writes both inputs as STATIC TEMPLATE LITERALS. This host is compile-time
 * proof of two things no runtime expectation can establish. First, that both inputs are PUBLIC: the
 * workspace enables strict input access modifiers, so an input demoted to protected or private would fail
 * this file's own compilation at the line below.
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
    // A standalone component is registered through `imports`. The array-based component registry Angular
    // used before standalone APIs is neither used nor available anywhere in this workspace.
    await TestBed.configureTestingModule({
      imports: [LoadingSpinnerComponent],
    }).compileComponents();
  });

  /**
   * Creates and renders the component, applying inputs first when supplied. `ComponentRef.setInput` is
   * used rather than instance assignment for two reasons.
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
      // THIS EXPECTATION IS INVERTED FROM WHAT INTUITION SUGGESTS, so the reason is recorded rather than
      // assumed.
      const fixture = render();

      expect(hostOf(fixture).getAttribute('aria-busy')).toBeNull();
      expect(hostOf(fixture).hasAttribute('aria-busy')).toBeFalse();
    });

    it('leaves no residual busy state anywhere once loading completes', () => {
      // THE COMPLETION-STATE ASSERTION. For this component "loading completed" means the element is gone:
      // it owns no loading input and is unmounted by its caller rather than switched off.
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
      const reflected = ALL_SIZES.map((size: LoadingSpinnerSize): string | null =>
        hostOf(render({ size })).getAttribute('data-size'),
      );

      expect(new Set(reflected).size).toBe(ALL_SIZES.length);
    });

    it('reflects the size in the exact attribute shape the stylesheet selects on', () => {
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
      // The glyph is pure decoration: the label already says what is loading, so exposing the glyph would
      // only add noise to a region that announces on every change. This is what keeps it out of the
      // accessibility tree.
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);

      expect(indicator.getAttribute('aria-hidden')).toBe('true');
    });

    it('gives the indicator no text of its own', () => {
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);

      expect(renderedTextOf(indicator)).toBe('');
    });

    // ⚠ THE ANIMATION MUST NAME FRAMES THAT ACTUALLY RESOLVE, and this spec exists because for a while it
    // did not. The rule carried a component-LOCAL `animation-name: loading-spinner-rotate`, and Angular's
    // emulated encapsulation rewrites a component's `@keyframes` DEFINITION to a scoped name while leaving
    // an `animation-name` REFERENCE inside an `@media` block untouched - so the reference matched nothing
    // and every spinner in the application rendered as a motionless circle. Measured in a real browser:
    // computed `animation-iteration-count: infinite` with `getAnimations().length === 0` and
    // `transform: none`. The frames therefore live in the GLOBAL stylesheet, where nothing is scoped.
    it('animates the indicator with globally-defined frames, so the name resolves', () => {
      const indicator = requireElement(hostOf(render()), INDICATOR_SELECTOR);
      const animationName: string = getComputedStyle(indicator).animationName;

      expect(animationName)
        .withContext('a component-scoped keyframes name would not resolve from an @media block')
        .toBe('dnn-indeterminate-spin');
      expect(animationName)
        .withContext('the unresolvable local name must not come back')
        .not.toBe('loading-spinner-rotate');

      // The definitive check: the browser reports a real animation object only when the name resolves.
      expect(indicator.getAnimations().length)
        .withContext('a declared animation that names nothing produces no animation at all')
        .toBeGreaterThan(0);
    });

    it('hides nothing else from assistive technology', () => {
      // Scoped so that the glyph is the ONLY element removed from the
      // accessibility tree, and nothing is being quietly hidden alongside it.
      const host = hostOf(render());

      expect(host.querySelectorAll('[aria-hidden="true"]').length).toBe(1);
    });

    it('draws the indicator without any image asset', () => {
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
      // WHY THIS MATTERS. The status role takes its accessible name from its CONTENTS, and the visible
      // label is that content.
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
      const host = hostOf(render());

      expect(renderedTextOf(host).trim()).toBe(DEFAULT_LABEL);
    });

    it('renders wording that merely has surrounding white space, padding included', () => {
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
      // Suppressing the label must not suppress the indicator: a bare indicator is still an indicator, and
      // this is what stops the whole component collapsing to nothing for a caller that wants exactly that.
      const fixture = render({ label: '' });

      expect(hostOf(fixture).querySelector(INDICATOR_SELECTOR)).not.toBeNull();
      expect(hostOf(fixture).children.length).toBe(1);
    });

    it('withdraws the live region entirely rather than publishing a nameless one', () => {
      // The two host modes are mutually exclusive, and this is the second of them.
      const host = hostOf(render({ label: '' }));

      expect(host.getAttribute('role')).toBeNull();
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBe('true');
    });

    it('announces itself as a named live region as soon as wording is supplied', () => {
      const host = hostOf(render({ label: DEFAULT_LABEL }));

      expect(host.getAttribute('role')).toBe('status');
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.getAttribute('aria-hidden')).toBeNull();
    });

    it('treats whitespace-only wording as no wording at all, exactly like the empty string', () => {
      // SENTINEL DISCIPLINE. The legacy null-string sentinel is the empty string, so testing for exact
      // emptiness looks faithful — but the question this component actually has to answer is whether it has
      // an accessible NAME to announce, and three spaces are not a name.
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
      // A progress indicator reports state and emits nothing. Asserting that the instance's own declared
      // surface is EXACTLY the two inputs proves both halves at once: an output would appear here as an
      // emitter field, and an injected dependency would appear as a stored reference.
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

      // Two element children and no more: the stylesheet's class vocabulary is closed, so a third element
      // would render unstyled and unaccounted for. Order matters because the glyph precedes the wording
      // visually.
      expect(host.children.length).toBe(2);
      expect(host.children[0]).toBe(indicator);
      expect(host.children[1]).toBe(label);
    });

    it('renders both parts as generic inline elements', () => {
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

      // OnPush must not stop an input change from reaching the DOM. A consumer that swaps general wording
      // for something specific mid-flight has to see it take effect.
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

      expect(host.getAttribute('data-size')).toBe('large');
      expect(host.matches('[data-size="large"]')).toBeTrue();
      expect(host.matches(`[data-size="${DEFAULT_SIZE}"]`)).toBeFalse();
    });

    it('restores the live region when wording returns after being cleared', () => {
      // The full round trip through both host modes, which neither single-mode expectation covers: a
      // consumer may clear and then restore the wording on one mounted instance, and the accessibility
      // contract has to follow it both ways rather than latching.
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
