//
// Specification for `EmptyStateComponent` — the shared zero-result state of the
// dnn-migration administration front end, an Angular 19 single-page
// application.
//
// ---------------------------------------------------------------------------
// NO PREDECESSOR SUITE EXISTS
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped NO
// automated test suite of any kind: not a unit test, not an integration test,
// not a fixture, not a test project — nothing anywhere in `Library/` or
// `Website/`. Every test file in this migration is consequently net-new
// coverage, with no predecessor spec to port and no legacy assertion to
// preserve. What the legacy tree does supply is *behavioural* evidence, and
// that evidence is cited inline below against the two reference sources for
// this component: the access-denied code-behind and the site-settings resource
// file.
//
// MIGRATION: net-new coverage. Because there is no legacy suite to mirror,
// every expectation here was derived from three places and nowhere else: the
// public surface of the sibling `empty-state.component.ts`, the two legacy
// reference sources named above, and the routing contract described in the next
// paragraph. No expectation was invented for its own sake, and none asserts an
// implementation detail of a neighbouring file.
//
// ---------------------------------------------------------------------------
// WHY THIS FILE CARRIES REAL WEIGHT
// ---------------------------------------------------------------------------
// The application's catch-all route reaches this component lazily and hands it
// its wording through static route data:
//
//   { path: '**',
//     loadComponent: () => import('./shared/components/empty-state/empty-state.component')
//                            .then((m) => m.EmptyStateComponent),
//     data: { message: 'Page not found' } }
//
// The router is configured with `withComponentInputBinding()`, which merges
// path parameters, query parameters and static route `data` into a single
// object, reflects the routed component's declared inputs, and calls
// `ComponentRef.setInput(name, merged[name])` for each of them.
//
// That binding is silent when it fails. If `message` were renamed, demoted to a
// plain field, or narrowed to a non-public accessor, the router would write
// nothing, the view would fall back to its default wording, and no compiler
// anywhere in this workspace would object — the route file and the component
// file never reference each other by symbol. The expectations grouped under
// "input binding contract" below are the sole automated defence against that
// silent degradation, which is why they use the production wording verbatim.
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
// command invalid. Jasmine spies (`spyOn`, `jasmine.createSpy`) are therefore
// the sanctioned mocking mechanism, and no other test or assertion library is
// installed in this workspace. This spec in fact needs no spy at all: the
// component under test injects nothing, so there is nothing to substitute.
//
// MIGRATION: no request plumbing, and therefore no address of any kind is
// asserted here. The `test` architect target declares no file replacements, so
// specs compile against the production environment, whose `apiBaseUrl` is the
// relative `/api/v1`. An absolute address would be wrong twice over — the SPA
// is served through a reverse proxy that forwards `/api/` on the same origin,
// and the proxy's upstream host name does not resolve in a browser. The rule is
// recorded here so that it holds if request plumbing is ever added; today the
// honest position is that this component performs no input or output at all.
//

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { EmptyStateComponent } from './empty-state.component';

// ---------------------------------------------------------------------------
// Fixtures and constants
// ---------------------------------------------------------------------------

/**
 * The wording the catch-all route supplies through its static `data`, reproduced
 * verbatim. Using the production literal rather than an invented sample means a
 * regression in the route binding contract fails this spec with the exact string
 * a user would have seen.
 */
const ROUTE_DATA_MESSAGE = 'Page not found';

/**
 * A representative in-page zero-result sentence, taken from the usage example
 * documented on the component itself. Held distinct from
 * {@link ROUTE_DATA_MESSAGE} because `ComponentRef.setInput` discards a write
 * whose value is identical to the previous one, so a re-render expectation is
 * meaningful solely when the second value differs from the first.
 */
const LIST_EMPTY_MESSAGE = 'No roles match the current filter.';

/**
 * Wording that carries balanced markup. Chosen to be short enough that the
 * expectation reads unambiguously, and to produce a distinctive element name
 * (`b`) that cannot occur incidentally in the component's own template.
 */
const MARKUP_BEARING_MESSAGE = '<b>x</b>';

/**
 * Wording that carries an executable script element — the worst realistic case,
 * modelled directly on a live legacy resource value (see the escaping group
 * below). No address and no credential appears in it: the element name alone is
 * what the expectation examines.
 */
const SCRIPT_BEARING_MESSAGE = '<script type="text/javascript">document.title = "x";</script>';

/**
 * Label of the action a list screen projects into the content slot.
 */
const PROJECTED_ACTION_LABEL = 'Add New Role';

/**
 * Block name of the component's styling vocabulary. Its element and modifier
 * classes are `empty-state__*` and `empty-state--*` respectively.
 */
const BLOCK_CLASS = 'empty-state';

/**
 * Element selectors that must never be emitted by a shared presentational
 * component.
 *
 * Document landmarks belong exclusively to the application shell under
 * `layout/`, where each occurs at most once per rendered page. A landmark
 * emitted from a component that a list screen can render repeatedly would
 * duplicate it, which degrades screen-reader navigation without producing any
 * visible symptom.
 */
const LANDMARK_SELECTORS = 'header, main, nav, footer';

/**
 * Attribute selectors reserved for the error surface.
 *
 * A live region announces *changes* to a region a user is not looking at. The
 * zero-result state is not a change and not an error: it is the steady-state
 * content of the region the user just navigated to, so announcing it would
 * interrupt without informing. The shared error banner owns that behaviour.
 */
const LIVE_REGION_SELECTORS = '[aria-live], [role="alert"]';

/**
 * Interactive selectors used to prove that the unprojected rendering offers no
 * action affordance of its own.
 */
const ACTION_SELECTORS = 'button, a';

/**
 * Spec-local host that exercises content projection.
 *
 * A host is the sole way to drive `<ng-content>`: `TestBed.createComponent`
 * supplies no projectable nodes, so a directly created fixture always renders
 * the empty slot. The host also delivers a second, independent proof of the
 * input contract — under this workspace's strict template checking a
 * `[message]` binding against a member that is not a declared, public input is
 * a compile error, so this template failing to compile is itself a contract
 * failure surfaced during the test build.
 *
 * The bound fields are immutable and never reassigned, so the host needs no
 * change-detection nudge beyond the single `detectChanges()` its expectations
 * perform. That keeps the host faithful to the workspace convention of pushed
 * change detection without importing any asynchronous test helper.
 */
@Component({
  selector: 'app-empty-state-host',
  standalone: true,
  imports: [EmptyStateComponent],
  template: `
    <app-empty-state [message]="wording">
      <button type="button">{{ actionLabel }}</button>
    </app-empty-state>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class EmptyStateHostComponent {
  /** Wording handed down through a template binding rather than through `setInput`. */
  public readonly wording: string = LIST_EMPTY_MESSAGE;

  /** Label of the projected action. */
  public readonly actionLabel: string = PROJECTED_ACTION_LABEL;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns a fixture's root DOM element with a concrete element type.
 *
 * `ComponentFixture.nativeElement` is loosely typed by the framework. Binding it
 * to a local of an explicit element type restores type safety for every
 * subsequent query without weakening a single compiler flag and without any
 * assertion operator.
 */
function rootElementOf<T>(fixture: ComponentFixture<T>): HTMLElement {
  const root: HTMLElement = fixture.nativeElement;
  return root;
}

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
 * operator is ever needed to read from it. Jasmine reports the thrown error as
 * the spec failure, and the message names the selector so a template regression
 * explains itself without a debugger.
 */
function requireElement(root: ParentNode, selector: string): HTMLElement {
  const found: HTMLElement | null = root.querySelector<HTMLElement>(selector);

  if (found === null) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

/**
 * Collects every distinct CSS class token present on an element and all of its
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

/**
 * Reports whether a class token belongs to this component's styling
 * vocabulary — the block itself, one of its elements, or one of its modifiers.
 */
function belongsToBlock(token: string): boolean {
  return (
    token === BLOCK_CLASS ||
    token.startsWith(`${BLOCK_CLASS}__`) ||
    token.startsWith(`${BLOCK_CLASS}--`)
  );
}

// ---------------------------------------------------------------------------
// Specification
// ---------------------------------------------------------------------------

describe('EmptyStateComponent', () => {
  beforeEach(async () => {
    // A standalone component is registered through `imports`. The array-based
    // component registry that Angular used before standalone APIs appears
    // nowhere in this workspace, and the component under test injects nothing,
    // so no provider is configured either: an unused provider would be noise
    // that future readers would have to disprove.
    await TestBed.configureTestingModule({
      imports: [EmptyStateComponent, EmptyStateHostComponent],
    }).compileComponents();
  });

  /**
   * Creates the component the way the router does — as a root component, with
   * no projected content.
   */
  const createRouted = (): ComponentFixture<EmptyStateComponent> =>
    TestBed.createComponent(EmptyStateComponent);

  /**
   * Applies wording through the very call the router's component input binder
   * makes, then renders, then hands back the root element.
   *
   * `ComponentRef.setInput` is preferred over assigning the instance property
   * because assignment would prove merely that a setter runs, whereas `setInput`
   * resolves the name against the component's declared input map exactly as the
   * router does. A renamed, absent or non-public input therefore fails these
   * expectations rather than passing them silently.
   */
  const renderWith = (
    fixture: ComponentFixture<EmptyStateComponent>,
    message: unknown,
  ): HTMLElement => {
    fixture.componentRef.setInput('message', message);
    fixture.detectChanges();

    return rootElementOf(fixture);
  };

  describe('input binding contract with the catch-all route', () => {
    it('renders the wording the catch-all route supplies through its static data', () => {
      const fixture = createRouted();

      const root = renderWith(fixture, ROUTE_DATA_MESSAGE);

      expect(renderedTextOf(root)).toContain(ROUTE_DATA_MESSAGE);
    });

    it('lands the bound value on the declared public accessor', () => {
      const fixture = createRouted();

      renderWith(fixture, ROUTE_DATA_MESSAGE);

      // Had `message` been renamed, demoted to a plain field, or narrowed below
      // public visibility, the binder's write would have gone nowhere and this
      // accessor would still be reporting the component's default wording.
      expect(fixture.componentInstance.message).toBe(ROUTE_DATA_MESSAGE);
    });

    it('re-renders when a later navigation supplies different wording', () => {
      const fixture = createRouted();
      renderWith(fixture, ROUTE_DATA_MESSAGE);

      const root = renderWith(fixture, LIST_EMPTY_MESSAGE);

      // Pushed change detection refreshes without any manual mark-for-check,
      // because the binder's write marks the view dirty on the caller's behalf.
      expect(renderedTextOf(root)).toContain(LIST_EMPTY_MESSAGE);
      expect(renderedTextOf(root)).not.toContain(ROUTE_DATA_MESSAGE);
    });

    it('preserves a supplied value verbatim, without trimming or reformatting', () => {
      const padded = `  ${ROUTE_DATA_MESSAGE}  `;
      const fixture = createRouted();

      renderWith(fixture, padded);

      // Surrounding whitespace decides whether a value counts as blank, yet an
      // accepted value is stored exactly as supplied. Both halves matter: a
      // caller who deliberately indents wording gets what it asked for.
      expect(fixture.componentInstance.message).toBe(padded);
    });
  });

  // MIGRATION: these escaping expectations exist because `message` is UNTRUSTED,
  // and the legacy corpus proves it rather than merely suggesting it. Across the
  // 37 in-scope `App_LocalResources/*.resx` files there are 1182 `<data>`
  // values, of which 76 embed an HTML tag and 29 open with a line break, with a
  // per-value histogram of br 39, p 24, h1 21, b 10, a 5, li 3, span 2, ul 2,
  // script 1, h4 1, h3 1, strong 1. The single script case is not hypothetical:
  // `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx ->
  // Advertising.Text` stores a live advertising script block that loads a remote
  // resource. It hides from a naive search because the tags are stored
  // HTML-escaped — searching that file for a literal opening script tag matches
  // nothing, while searching for its escaped form matches twice. Escaping is
  // therefore legacy CONTINUITY, not an invention of this migration:
  // `Website/admin/Security/AccessDenied.ascx.vb:43` wraps its externally
  // supplied message in `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))`,
  // and `Website/Default.aspx.vb:232` likewise applies `Server.HtmlEncode`
  // before display. These expectations are the automated proof that the target
  // preserves that guarantee.
  describe('untrusted wording reaches the DOM as plain text', () => {
    it('renders balanced markup as literal characters', () => {
      const fixture = createRouted();

      const root = renderWith(fixture, MARKUP_BEARING_MESSAGE);

      expect(renderedTextOf(root)).toContain(MARKUP_BEARING_MESSAGE);
    });

    it('creates no element from balanced markup', () => {
      const fixture = createRouted();

      const root = renderWith(fixture, MARKUP_BEARING_MESSAGE);

      expect(root.querySelector('b')).toBeNull();
    });

    it('renders a script-bearing value as literal characters', () => {
      const fixture = createRouted();

      const root = renderWith(fixture, SCRIPT_BEARING_MESSAGE);

      expect(renderedTextOf(root)).toContain(SCRIPT_BEARING_MESSAGE);
    });

    it('creates no script element from a script-bearing value', () => {
      const fixture = createRouted();

      const root = renderWith(fixture, SCRIPT_BEARING_MESSAGE);

      expect(root.querySelector('script')).toBeNull();
    });

    it('adds no element of any kind when wording carries markup', () => {
      const benign = createRouted();
      const benignElementCount = renderWith(benign, ROUTE_DATA_MESSAGE).querySelectorAll('*').length;

      const hostile = createRouted();
      const hostileElementCount = renderWith(hostile, SCRIPT_BEARING_MESSAGE).querySelectorAll('*')
        .length;

      // Comparing counts rather than naming tags proves the general case: no
      // fragment of the value was parsed into the document, whatever it held.
      expect(hostileElementCount).toBe(benignElementCount);
    });
  });

  // MIGRATION: one component serves two readings, which is why the projection
  // group below covers both and why this workspace grows no separate not-found
  // component and no eleventh member of its shared component library. Reading
  // one is the in-page zero-result state, rendered in place of a populated table
  // and typically projecting an "Add New ..." affordance. Reading two is the
  // body of the catch-all route, which supplies wording through route data and
  // projects nothing at all. The legacy application expressed neither: its admin
  // grids declared the full data-grid class vocabulary and no empty template, so
  // an empty result set rendered silently, and its access-denied page was a
  // separate Web Forms page rather than a reusable fragment. Covering both
  // readings here is what keeps the consolidation honest.
  describe('content projection', () => {
    it('renders the wording and offers no action affordance of its own', () => {
      const fixture = createRouted();

      const root = renderWith(fixture, ROUTE_DATA_MESSAGE);

      // The catch-all route projects nothing, so the content slot must collapse
      // to nothing rather than leaving an interactive artefact behind.
      expect(renderedTextOf(root)).toContain(ROUTE_DATA_MESSAGE);
      expect(root.querySelector(ACTION_SELECTORS)).toBeNull();
    });

    it('projects a caller-supplied action into its own element subtree', () => {
      const hostFixture = TestBed.createComponent(EmptyStateHostComponent);
      hostFixture.detectChanges();

      const emptyState = requireElement(rootElementOf(hostFixture), 'app-empty-state');
      const action = requireElement(emptyState, 'button');

      // Querying from the component's own element, rather than from the host,
      // proves the node was projected into the slot instead of merely rendered
      // somewhere alongside it.
      expect(renderedTextOf(action).trim()).toBe(PROJECTED_ACTION_LABEL);
      expect(action.getAttribute('type')).toBe('button');
    });

    it('renders bound wording and projected content together', () => {
      const hostFixture = TestBed.createComponent(EmptyStateHostComponent);
      hostFixture.detectChanges();

      const emptyState = requireElement(rootElementOf(hostFixture), 'app-empty-state');
      const text = renderedTextOf(emptyState);

      // The host reaches `message` through a template binding rather than
      // through the binder's programmatic write, so this covers the second route
      // into the same input.
      expect(text).toContain(LIST_EMPTY_MESSAGE);
      expect(text).toContain(PROJECTED_ACTION_LABEL);
    });
  });

  // MIGRATION: the blank-value fallback is behavioural equivalence with
  // `Website/admin/Security/AccessDenied.ascx.vb:41-47`, which tested
  // `If Request.QueryString("message") <> "" Then` and otherwise resolved a
  // localised default caption. The target widens that test from "empty" to
  // "blank", because whitespace collapses in HTML and would render a visually
  // empty state, and it resolves the default in TypeScript rather than through a
  // resource lookup, because the resource mechanism is Web Forms specific and is
  // deliberately not carried across. The default wording below is read back from
  // the component rather than restated, so these expectations lock the
  // *behaviour* without duplicating a caption that the component alone owns.
  describe('blank wording falls back to the documented default', () => {
    it('renders meaningful wording when nothing is bound at all', () => {
      const fixture = createRouted();
      fixture.detectChanges();

      const rendered = renderedTextOf(rootElementOf(fixture)).trim();
      const accessorValue = fixture.componentInstance.message;

      expect(accessorValue.trim().length).toBeGreaterThan(0);
      expect(rendered).toContain(accessorValue);
    });

    it('treats empty, whitespace, absent and null values as a request for the default', () => {
      const reference = createRouted();
      reference.detectChanges();
      const defaultWording = reference.componentInstance.message;

      const blankValues: readonly unknown[] = ['', '   ', undefined, null];

      for (const blankValue of blankValues) {
        const fixture = createRouted();
        renderWith(fixture, blankValue);

        expect(fixture.componentInstance.message)
          .withContext(`blank value ${String(blankValue)} must select the default wording`)
          .toBe(defaultWording);
      }
    });

    it('never renders a placeholder in place of absent wording', () => {
      const fixture = createRouted();

      const rendered = renderedTextOf(renderWith(fixture, undefined));

      expect(rendered).not.toContain('undefined');
      expect(rendered).not.toContain('null');
    });
  });

  describe('rendered markup guarantees', () => {
    it('renders its own block class as the styling root and carries the wording within it', () => {
      const fixture = createRouted();
      const root = renderWith(fixture, ROUTE_DATA_MESSAGE);

      const block = requireElement(root, `.${BLOCK_CLASS}`);

      expect(renderedTextOf(block)).toContain(ROUTE_DATA_MESSAGE);
    });

    it('emits no class outside its own styling vocabulary', () => {
      const fixture = createRouted();
      const root = renderWith(fixture, ROUTE_DATA_MESSAGE);

      // A guard against a global or borrowed class name creeping into a shared
      // presentational component, where it would couple this component to a
      // stylesheet it does not own. It is deliberately tolerant about which
      // element classes exist, because the exact element breakdown belongs to the
      // sibling template and stylesheet.
      const foreignTokens = classTokensWithin(root).filter(
        (token: string): boolean => belongsToBlock(token) === false,
      );

      expect(foreignTokens).toEqual([]);
    });

    it('emits no document landmark', () => {
      const fixture = createRouted();
      const root = renderWith(fixture, ROUTE_DATA_MESSAGE);

      expect(root.querySelector(LANDMARK_SELECTORS)).toBeNull();
    });

    it('emits no live region', () => {
      const fixture = createRouted();
      const root = renderWith(fixture, ROUTE_DATA_MESSAGE);

      expect(root.querySelector(LIVE_REGION_SELECTORS)).toBeNull();
    });
  });
});
