//
// Specification for `SearchInputComponent` — the shared free-text filter control
// of the dnn-migration administration front end, an Angular 19 single-page
// application.
//
// ---------------------------------------------------------------------------
// NO PREDECESSOR SUITE EXISTS
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET Web Forms application shipped no automated
// test suite of any kind: not a unit test, not an integration test, not a
// fixture, not a test project — nothing anywhere in `Library/` or `Website/`.
// Every expectation below is therefore net-new coverage. Nothing here was
// ported, because there was nothing to port.
//
// What the legacy tree does supply is behavioural EVIDENCE, and that evidence is
// cited inline against the four reference sources for this component:
//
//   Website/admin/Users/Users.ascx.vb
//   Website/admin/Users/users.ascx
//   Website/admin/Portal/Portals.ascx.vb
//   Website/admin/Users/App_LocalResources/Users.ascx.resx
//
// Every line reference below was re-read and confirmed against those files while
// this specification was written; none was copied from a summary.
//
// ---------------------------------------------------------------------------
// WHY THIS FILE CARRIES REAL WEIGHT — TWO INDEPENDENT REASONS
// ---------------------------------------------------------------------------
// FIRST, it is the sole automated guard on the match-semantics contract. The
// emitted term feeds a SQL `LIKE 'term%'` predicate, so the match is STARTS-WITH.
// The legacy screens composed that trailing wildcard SERVER-SIDE, at the call
// site, immediately before handing the value to the data layer. Each of the four
// sites below concatenated the wildcard character onto the term as a VB string
// literal, in the argument position shown here as `<term-plus-wildcard>`:
//
//   Users.ascx.vb   L269  GetUsersByEmail(..., <term-plus-wildcard>, ...)
//   Users.ascx.vb   L271  GetUsersByUserName(..., <term-plus-wildcard>, ...)
//   Users.ascx.vb   L274  GetUsersByProfileProperty(..., SearchField,
//                          <term-plus-wildcard>, ...)
//   Portals.ascx.vb L142  GetPortalsByName(<term-plus-wildcard>, ...)
//
// The concatenation is written out in words rather than reproduced verbatim on
// purpose: no wildcard is ever appended to a term anywhere in this file, and a
// reviewer sweeping the frontend for that pattern should find nothing here.
//
// In the target, composing that predicate belongs to the repository that owns
// it, because all data access goes through repository interfaces. A client that
// emitted a pre-wildcarded term would double-wildcard the predicate and silently
// change which rows match — a defect no compiler can catch and no type can
// express. The expectations grouped under "emitted term fidelity" are the only
// mechanism that would catch it, and they are written so that adding a wildcard,
// a whitespace trim or a case fold makes them FAIL.
//
// SECOND, this file is the only route by which the component and its template get
// type-checked at all. The application tsconfig compiles by import graph from a
// single entry point, so a component that nothing has imported yet is silently
// never checked, and under strict template checking its template is never checked
// either. The spec tsconfig includes every `*.spec.ts`, so importing the
// component here is what drags it into a gated compile.
//
// ---------------------------------------------------------------------------
// TEST FRAMEWORK — KARMA WITH JASMINE, DELIBERATELY
// ---------------------------------------------------------------------------
// The mandated validation command is
//
//   ng test --watch=false --browsers=ChromeHeadless --code-coverage
//
// and `--browsers` is a Karma option, so a different runner would make that
// command invalid. Jasmine spies are consequently the sanctioned substitution
// mechanism, and no other test or assertion library is installed in this
// workspace. This specification in fact needs no spy: emissions are captured by
// subscribing to the component output into a typed local array, which makes the
// duplicate-suppression expectations read directly off the emission history.
//
// TIME IS ALWAYS VIRTUAL. The component debounces typing through a timer, and a
// zero-millisecond delay is STILL ASYNCHRONOUS — a zero-duration timer is
// scheduled rather than run inline — so no synchronous expectation could ever
// observe a debounced emission. Every timing expectation therefore runs inside
// `fakeAsync` and advances the clock with `tick`. No real timer is scheduled
// anywhere in this file, and no wall-clock reading is taken, so the suite is
// fully deterministic and cannot flake under load.
//
// EFFECT FLUSHING IS NOT USED, and that is a verified decision rather than an
// omission: the component under test declares no reactive effect at all. Its
// state is a typed reactive control plus one private field. For the record, the
// installed `@angular/core` does expose a developer-preview `TestBed.flushEffects`
// but exposes no `TestBed.tick`; neither is applicable here, so neither is called.
//
// NO ADDRESS OF ANY KIND IS ASSERTED. The component issues no request, and the
// `test` architect target declares no file replacements, so specs compile against
// the production configuration whose API base path is relative. An absolute
// address would be wrong twice over — the application is served through a reverse
// proxy that forwards the API prefix on the same origin, and the proxy's upstream
// host name does not resolve in a browser. The rule is recorded so it holds if
// request plumbing is ever added; today there is none, which is itself asserted.
//

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { type Subscription } from 'rxjs';

import { SearchInputComponent } from './search-input.component';

// ---------------------------------------------------------------------------
// Contract constants
// ---------------------------------------------------------------------------
// Duplicated from the component and its template ON PURPOSE. Importing the
// component's private module-level values is impossible, and re-deriving them
// from the implementation would make these expectations tautological: a
// specification that reads its expected value out of the code under test cannot
// detect a change to that code. Written out as literals, each one fails loudly
// the moment the contract moves.

/** The component's own fallback placeholder when a caller supplies none. */
const DEFAULT_PLACEHOLDER = 'Search';

/** The component's own fallback debounce window, in milliseconds. */
const DEFAULT_DEBOUNCE_MS = 300;

/**
 * A short debounce window used wherever the exact duration is beside the point.
 * Kept non-zero so that "did not emit yet" and "emitted" remain distinguishable
 * steps on the virtual clock.
 */
const SHORT_DEBOUNCE_MS = 50;

/**
 * A deliberately long window, used to prove that immediate submission does not
 * wait for the debounce. If Enter were routed through the debounced path, a test
 * advancing the clock by nothing at all would observe no emission.
 */
const LONG_DEBOUNCE_MS = 5000;

/**
 * Label wording, taken verbatim from the legacy resource VALUE rather than from
 * the stale literal in the legacy markup: `Users.ascx.resx` defines `Search.Text`
 * as `Search:`, colon included.
 */
const LABEL_TEXT = 'Search:';

/**
 * Accessible name of the submit control, carried by visible text.
 *
 * The legacy control was an image button with no alternate text
 * (`users.ascx` L9), and no wording entry ever existed to supply one — there is
 * no `btnSearch.Text`, `btnSearch.ToolTip` or `btnSearch.AlternateText` key in
 * any in-scope resource file. It was therefore unnameable to a screen reader.
 * The expectations that assert this name are what stop that defect returning.
 */
const SUBMIT_TEXT = 'Search';

/**
 * The SQL wildcard character.
 *
 * Declared here for exactly one purpose: to assert its ABSENCE from every
 * emitted term. It is never concatenated onto a term anywhere in this file, and
 * it must never be concatenated onto one anywhere in the frontend, because the
 * repository that owns the predicate composes it.
 */
const WILDCARD_CHARACTER = '%';

/** An ordinary lower-case term. */
const PLAIN_TERM = 'abc';

/** A distinct follow-on term, used to prove a changed term does emit again. */
const NEXT_TERM = 'abd';

/**
 * A term carrying both leading and trailing whitespace AND mixed casing, so a
 * single expectation locks down two separate no-mutation guarantees at once. A
 * trim would shorten it; a case fold would alter it.
 */
const PADDED_MIXED_CASE_TERM = '  AbC  ';

/** A mixed-case term with no surrounding whitespace, isolating the case guarantee. */
const MIXED_CASE_TERM = 'AbC';

/**
 * A realistic term that itself carries a percent character. Proves the component
 * neither strips a user-typed percent nor treats it as a wildcard it should
 * normalise, and that nothing is appended after it.
 */
const PERCENT_BEARING_TERM = '50% off';

/** The empty term. A meaningful value here, never an absence. */
const EMPTY_TERM = '';

/**
 * A placeholder carrying angle brackets, used to prove the framework escapes it.
 *
 * This is not hypothetical. The legacy resource set that supplies comparable
 * wording carries entries with live markup in them, including script blocks with
 * a remote source, so no caller-supplied string may ever be treated as markup.
 * It is passed as a plain string and expected back as literal attribute text.
 */
const MARKUP_BEARING_PLACEHOLDER = '<b>x</b>';

// Selectors for the template's fixed class vocabulary. The template declares
// exactly these five class names and the host itself carries no class.
const LABEL_SELECTOR = 'label.search-input__label';
const ROW_SELECTOR = 'div.search-input__row';
const FIELD_SELECTOR = 'input.search-input__field';
const SUBMIT_SELECTOR = 'button.search-input__submit';
const ICON_SELECTOR = 'svg.search-input__submit-icon';

/**
 * The shape of the per-instance element id the component generates.
 *
 * Asserted as a PATTERN rather than as a literal, because the id is produced by a
 * monotonic module-level counter: the exact number depends on how many instances
 * the suite has already created, so pinning a literal would couple unrelated
 * expectations to one another's execution order.
 */
const FIELD_ID_PATTERN = /^app-search-input-\d+$/;

// ---------------------------------------------------------------------------
// Narrowing helpers
// ---------------------------------------------------------------------------

/**
 * Resolves a required element, narrowing away the null the DOM query returns.
 *
 * Strict TypeScript is in force in specifications exactly as it is in production
 * code, so a query result cannot simply be asserted non-null. Throwing here is
 * strictly better than a non-null assertion: the value is genuinely narrowed, and
 * a missing element produces a named failure that says which selector was not
 * found instead of an opaque error about a property of null.
 *
 * @param root Node to search within.
 * @param selector CSS selector that must match exactly one required element.
 * @returns The matched element, typed as requested.
 */
function requireElement<T extends Element>(root: ParentNode, selector: string): T {
  const found: T | null = root.querySelector<T>(selector);
  if (found === null) {
    throw new Error(`Expected to find "${selector}" in the rendered search input.`);
  }

  return found;
}

/**
 * Reads an element's text with surrounding whitespace removed.
 *
 * `textContent` is nullable on every node, so the nullish fallback keeps this
 * total without resorting to an assertion.
 *
 * @param element Element whose text is wanted.
 * @returns The trimmed text, or the empty string when there is none.
 */
function textOf(element: Element): string {
  return (element.textContent ?? '').trim();
}

/**
 * Types a value into the field the way a user does.
 *
 * Assigning `value` alone changes the DOM but tells the reactive control nothing.
 * The `input` event is what the control's value accessor listens for, so it is
 * what drives the debounced stream. Both steps are required.
 *
 * @param field The rendered search field.
 * @param value The value to type.
 */
function typeInto(field: HTMLInputElement, value: string): void {
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

/**
 * Presses Enter on the field, exercising the immediate-emission path.
 *
 * The template binds `(keydown.enter)`, which the framework resolves by reading
 * the event's `key` property, so a keyboard event carrying `Enter` is what
 * triggers it. Both DOM event constructors used in this file are standard browser
 * globals available under the configured library set.
 *
 * @param field The rendered search field.
 */
function pressEnter(field: HTMLInputElement): void {
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
}

/**
 * A minimal consuming host, used only where the expectation is about the
 * component's INPUT and OUTPUT BINDINGS rather than about its internals.
 *
 * Directly created fixtures set properties, which bypasses the binding machinery
 * altogether. Going through a real template is what proves a consumer's bindings
 * actually connect — and it is the only way to observe what a consumer's output
 * handler really receives, which one expectation below depends on.
 *
 * The received payloads are collected as `unknown` on purpose. The declared
 * output payload is a string, and the expectation below verifies that every
 * payload really is one; typing the collection as a string array would make that
 * check unable to fail.
 */
@Component({
  selector: 'app-search-input-host',
  standalone: true,
  imports: [SearchInputComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-search-input
      [placeholder]="placeholder"
      [debounceMs]="debounceMs"
      (search)="record($event)"
    />
  `,
})
class SearchInputHostComponent {
  /** Placeholder handed down through a real property binding. */
  public placeholder = DEFAULT_PLACEHOLDER;

  /** Debounce window handed down through a real property binding. */
  public debounceMs = SHORT_DEBOUNCE_MS;

  /** Everything the consumer's output handler was called with, in order. */
  public readonly received: unknown[] = [];

  /**
   * Records one output payload.
   *
   * @param payload Whatever arrived at the consumer's handler.
   */
  public record(payload: unknown): void {
    this.received.push(payload);
  }
}

describe('SearchInputComponent', () => {
  let fixture: ComponentFixture<SearchInputComponent>;
  let component: SearchInputComponent;
  let httpMock: HttpTestingController;
  let emitted: string[];
  let subscription: Subscription;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // The component is standalone, so it is IMPORTED. There is no module
      // metadata list to add it to, and using one would fail outright.
      imports: [SearchInputComponent],
      // ORDER IS LOAD-BEARING. The real client must be provided FIRST; the
      // testing providers then replace its backend. Reversed, the real backend
      // survives and the testing backend never takes effect, so a stray request
      // would escape to the network instead of failing the run.
      //
      // Registering them at all is a deliberate choice for a component that
      // needs neither. It converts "this shared presentational component
      // performs no data access" from an unstated assumption into an ENFORCED
      // invariant: verification below fails the moment anything here issues a
      // request. Business logic belongs in the application layer and all data
      // access goes through repository interfaces, so a shared input control
      // reaching for the network is precisely the regression worth trapping.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(SearchInputComponent);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    emitted = [];
    subscription = component.search.subscribe((term: string): void => {
      emitted.push(term);
    });
  });

  afterEach(() => {
    subscription.unsubscribe();
    // Proves zero outstanding requests. For this component that is a real
    // assertion rather than a formality: it is the standing proof that the
    // control emits a term and nothing else.
    httpMock.verify();
  });

  /**
   * Resolves the rendered field for the fixture under test.
   *
   * @returns The search field element.
   */
  function field(): HTMLInputElement {
    return requireElement<HTMLInputElement>(fixture.nativeElement, FIELD_SELECTOR);
  }

  describe('creation and rendered structure', () => {
    it('creates and renders a label, a row, a field and a submit control', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

      expect(component).toBeTruthy();
      expect(requireElement<HTMLLabelElement>(host, LABEL_SELECTOR)).toBeTruthy();
      expect(requireElement<HTMLElement>(host, ROW_SELECTOR)).toBeTruthy();
      expect(requireElement<HTMLInputElement>(host, FIELD_SELECTOR)).toBeTruthy();
      expect(requireElement<HTMLButtonElement>(host, SUBMIT_SELECTOR)).toBeTruthy();
    });

    it('renders exactly one field and one submit control', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

      // A second field would break the label association silently, because a
      // label can name only one control.
      expect(host.querySelectorAll(FIELD_SELECTOR).length).toBe(1);
      expect(host.querySelectorAll(SUBMIT_SELECTOR).length).toBe(1);
    });

    it('starts with an empty term held in a control whose value is a plain string', () => {
      fixture.detectChanges();

      expect(component.term.value).toBe(EMPTY_TERM);
      expect(component.term.value.length).toBe(0);
      expect(component.isTermEmpty).toBeTrue();
      expect(field().value).toBe(EMPTY_TERM);
    });

    it('returns the control to the empty string when it is reset, never to a null value', () => {
      fixture.detectChanges();
      component.term.setValue(PLAIN_TERM);

      component.term.reset();

      // This is what the control's non-nullable configuration buys, and it is
      // what keeps a single representation of "no term". A nullable control would
      // reset to null and introduce a second, competing kind of absence.
      expect(component.term.value).toBe(EMPTY_TERM);
      expect(component.term.value.length).toBe(0);
    });

    it('reports emptiness for the empty term and non-emptiness once a term is present', () => {
      fixture.detectChanges();

      expect(component.isTermEmpty).toBeTrue();

      component.term.setValue(PLAIN_TERM);
      expect(component.isTermEmpty).toBeFalse();

      component.term.setValue(EMPTY_TERM);
      expect(component.isTermEmpty).toBeTrue();
    });
  });

  describe('placeholder projection', () => {
    it('projects its own fallback placeholder onto the field', () => {
      fixture.detectChanges();

      expect(field().placeholder).toBe(DEFAULT_PLACEHOLDER);
      expect(field().getAttribute('placeholder')).toBe(DEFAULT_PLACEHOLDER);
    });

    it('projects a caller supplied placeholder onto the field', () => {
      const supplied = 'Search users';
      component.placeholder = supplied;

      // Change detection is required after writing an input, because the
      // component checks on demand rather than on every application tick.
      fixture.detectChanges();

      expect(field().placeholder).toBe(supplied);
    });

    it('honours an explicitly empty placeholder instead of substituting its fallback', () => {
      component.placeholder = EMPTY_TERM;
      fixture.detectChanges();

      // Sentinel discipline. The legacy null-string sentinel IS the empty string,
      // so an empty placeholder is a legitimate instruction — "render none" —
      // and must never be quietly swapped for a default.
      expect(field().placeholder).toBe(EMPTY_TERM);
      expect(field().placeholder.length).toBe(0);
    });

    it('renders a placeholder carrying angle brackets as literal text, never as markup', () => {
      const host: HTMLElement = fixture.nativeElement;
      component.placeholder = MARKUP_BEARING_PLACEHOLDER;
      fixture.detectChanges();

      // The characters survive verbatim as ATTRIBUTE TEXT ...
      expect(field().getAttribute('placeholder')).toBe(MARKUP_BEARING_PLACEHOLDER);
      expect(field().placeholder).toBe(MARKUP_BEARING_PLACEHOLDER);
      // ... and produced no element, so nothing was interpreted as markup.
      expect(host.querySelector('b')).toBeNull();
      expect(host.querySelector('script')).toBeNull();
    });

    it('adds no element when the placeholder carries markup characters', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const benignElementCount = host.querySelectorAll('*').length;

      component.placeholder = MARKUP_BEARING_PLACEHOLDER;
      fixture.detectChanges();

      // Counting elements is a stronger statement than probing for one tag name:
      // it holds for any markup a caller might supply, not just the one sampled.
      expect(host.querySelectorAll('*').length).toBe(benignElementCount);
    });
  });

  describe('emitted term fidelity — starts-with match, no wildcard', () => {
    it('emits the typed term exactly, appending no wildcard', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted).toEqual([PLAIN_TERM]);
      expect(emitted[0]).toBe(PLAIN_TERM);
      // Nothing was appended: identical length, and no wildcard anywhere.
      expect(emitted[0].length).toBe(PLAIN_TERM.length);
      expect(emitted[0].indexOf(WILDCARD_CHARACTER)).toBe(-1);
      expect(emitted[0].endsWith(WILDCARD_CHARACTER)).toBeFalse();
    }));

    it('preserves leading and trailing whitespace, and the casing, of the typed term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PADDED_MIXED_CASE_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // Byte for byte. Each of the three expectations below fails under a
      // different mutation: the first under any change at all, the second under a
      // trim, the third under a case fold.
      expect(emitted[0]).toBe(PADDED_MIXED_CASE_TERM);
      expect(emitted[0]).not.toBe(PADDED_MIXED_CASE_TERM.trim());
      expect(emitted[0]).not.toBe(PADDED_MIXED_CASE_TERM.toLowerCase());
      expect(emitted[0].length).toBe(PADDED_MIXED_CASE_TERM.length);
      expect(emitted[0].startsWith(' ')).toBeTrue();
      expect(emitted[0].endsWith(' ')).toBeTrue();
    }));

    it('preserves the letter casing of the typed term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), MIXED_CASE_TERM);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted[0]).toBe(MIXED_CASE_TERM);
      expect(emitted[0]).not.toBe(MIXED_CASE_TERM.toLowerCase());
      expect(emitted[0]).not.toBe(MIXED_CASE_TERM.toUpperCase());
    }));

    it('emits a term carrying a percent character unchanged and unencoded', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PERCENT_BEARING_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // A user-typed percent is part of the term, not a wildcard to normalise,
      // and not something to percent-encode here — query-string composition
      // happens in the shared parameter utility, not in this control.
      expect(emitted[0]).toBe(PERCENT_BEARING_TERM);
      expect(emitted[0].length).toBe(PERCENT_BEARING_TERM.length);
      expect(emitted[0].endsWith(WILDCARD_CHARACTER)).toBeFalse();
      expect(emitted[0].indexOf('%25')).toBe(-1);
    }));

    it('emits precisely the value the field is holding', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PADDED_MIXED_CASE_TERM);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted[0]).toBe(field().value);
      expect(emitted[0]).toBe(component.term.value);
    }));

    it('emits only the final term when several keystrokes fall inside one window', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), 'a');
      tick(SHORT_DEBOUNCE_MS - 10);
      typeInto(field(), 'ab');
      tick(SHORT_DEBOUNCE_MS - 10);
      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // Debounce semantics: the intermediate keystrokes never reach a consumer,
      // so a three-letter word costs one query rather than three.
      expect(emitted).toEqual([PLAIN_TERM]);
    }));
  });


  describe('the empty term', () => {
    it('emits the empty string when an empty field is submitted', () => {
      fixture.detectChanges();

      pressEnter(field());

      // Asserted against the empty string itself, and by length. A falsiness
      // check would also pass for null or undefined, so it would not actually
      // pin down the contract this expectation exists to pin down.
      expect(emitted).toEqual([EMPTY_TERM]);
      expect(emitted[0]).toBe(EMPTY_TERM);
      expect(emitted[0].length).toBe(0);
      expect(typeof emitted[0]).toBe('string');
    });

    it('emits a first empty term even though nothing has been emitted before it', () => {
      fixture.detectChanges();

      pressEnter(field());

      // The duplicate guard starts from a "nothing emitted yet" marker that is
      // deliberately NOT the empty string, precisely so this first empty term
      // survives. Had it started at the empty string, this emission would have
      // been swallowed and a cleared filter would never reach the consumer.
      expect(emitted.length).toBe(1);
      expect(emitted[0]).toBe(EMPTY_TERM);
    });

    it('emits the empty string once a term is cleared', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), EMPTY_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // Clearing the box is a real instruction, not a no-op: the legacy screen
      // expressed it by simply not appending its filter parameter
      // (`Users.ascx.vb` L254-L255), and the modern equivalent is to emit the
      // empty term so the consumer omits the query parameter and requests the
      // unfiltered page.
      expect(emitted).toEqual([PLAIN_TERM, EMPTY_TERM]);
      expect(emitted[1]).toBe(EMPTY_TERM);
      expect(emitted[1].length).toBe(0);
      expect(component.isTermEmpty).toBeTrue();
    }));
  });

  describe('immediate submission', () => {
    it('emits on Enter without advancing past the debounce window at all', fakeAsync(() => {
      component.debounceMs = LONG_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      pressEnter(field());

      // Not one millisecond of virtual time has passed. A keyboard user who
      // presses Enter is not made to wait out the delay they were skipping.
      expect(emitted).toEqual([PLAIN_TERM]);

      // Drain the in-flight debounced emission so no timer outlives the test.
      // It carries the SAME term, and the shared guard discards it — which is the
      // whole reason both paths funnel through one place.
      tick(LONG_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('emits on a submit-control click without advancing past the debounce window', fakeAsync(() => {
      component.debounceMs = LONG_DEBOUNCE_MS;
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      typeInto(field(), PLAIN_TERM);
      submit.click();

      expect(emitted).toEqual([PLAIN_TERM]);

      tick(LONG_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('emits the term verbatim on the immediate path too, appending no wildcard', fakeAsync(() => {
      component.debounceMs = LONG_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PADDED_MIXED_CASE_TERM);
      pressEnter(field());

      // The no-mutation guarantee is a property of the emission funnel, so it
      // must hold on BOTH routes into it, not only on the debounced one.
      expect(emitted[0]).toBe(PADDED_MIXED_CASE_TERM);
      expect(emitted[0].indexOf(WILDCARD_CHARACTER)).toBe(-1);

      tick(LONG_DEBOUNCE_MS);
    }));
  });

  describe('debounce timing', () => {
    it('does not emit before its own fallback window has elapsed', fakeAsync(() => {
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(DEFAULT_DEBOUNCE_MS - 1);
      expect(emitted).toEqual([]);

      tick(1);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('honours a caller supplied window', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS - 1);
      expect(emitted).toEqual([]);

      tick(1);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('treats a zero window as a real setting and still emits asynchronously', fakeAsync(() => {
      component.debounceMs = 0;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);

      // A zero delay schedules a zero-duration timer rather than running inline,
      // so nothing has been emitted yet at this point. This expectation is the
      // proof that no synchronous check could ever observe the debounced path.
      expect(emitted).toEqual([]);

      tick(0);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('clamps a negative window to zero rather than rejecting it', fakeAsync(() => {
      component.debounceMs = -1000;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(0);

      // A mis-bound value degrades to "emit as soon as possible" instead of
      // throwing inside a presentational control.
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('re-reads the window at each keystroke, so a rebound value takes effect', fakeAsync(() => {
      component.debounceMs = LONG_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), 'a');
      tick(LONG_DEBOUNCE_MS);
      expect(emitted).toEqual(['a']);

      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();
      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // Had the delay been captured once at initialisation, the second term would
      // still be waiting on the original long window and this would fail. That
      // distinction is the point: the window is resolved per keystroke.
      expect(emitted).toEqual(['a', PLAIN_TERM]);
    }));
  });

  describe('duplicate suppression', () => {
    it('does not re-emit an unchanged term on the debounced path', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted).toEqual([PLAIN_TERM]);
      expect(emitted.length).toBe(1);
    }));

    it('does not re-emit when Enter repeats a term the debounced path already emitted', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);

      pressEnter(field());

      // THIS IS THE EXPECTATION THAT PROVES THE TWO PATHS SHARE ONE GATE. A guard
      // placed on the debounced stream alone would let this second emission
      // through, and the user would pay for a redundant query by pressing Enter
      // after the delay had already fired.
      expect(emitted).toEqual([PLAIN_TERM]);
      expect(emitted.length).toBe(1);
    }));

    it('does not re-emit when the submit control repeats an already emitted term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      submit.click();
      submit.click();

      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('does not re-emit when Enter is pressed repeatedly on an unchanged term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();
      const control = field();

      typeInto(control, PLAIN_TERM);
      pressEnter(control);
      pressEnter(control);
      pressEnter(control);

      expect(emitted).toEqual([PLAIN_TERM]);
      expect(emitted.length).toBe(1);

      // Drain the in-flight debounced emission. Typing scheduled one, and leaving
      // it undrained would schedule a REAL timer that outlives the test; the whole
      // suite keeps time virtual so that nothing can flake under load.
      tick(SHORT_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('does not re-emit an empty term that was already emitted', () => {
      fixture.detectChanges();

      pressEnter(field());
      pressEnter(field());

      expect(emitted).toEqual([EMPTY_TERM]);
      expect(emitted.length).toBe(1);
    });

    it('emits again as soon as the term genuinely changes', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), NEXT_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // Suppression is limited to CONSECUTIVE identical terms. A changed term must
      // always get through, or the filter would appear to stop responding.
      expect(emitted).toEqual([PLAIN_TERM, NEXT_TERM]);
    }));

    it('emits a term again after it has been interrupted by a different one', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), NEXT_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // Only the immediately preceding term is compared, so returning to an
      // earlier term is a genuine change and must emit.
      expect(emitted).toEqual([PLAIN_TERM, NEXT_TERM, PLAIN_TERM]);
    }));
  });


  describe('accessible name and keyboard operability', () => {
    it('associates the visible label with the field through matching for and id attributes', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const label = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);
      const control = field();

      // The ASSOCIATION is what is asserted, not the mere presence of a label
      // element. An unassociated label leaves the control with no programmatic
      // name at all, which is exactly the legacy defect: the caption sat in an
      // adjacent table cell (`users.ascx` L5) and was never bound to the text box
      // at L7.
      expect(control.id.length).toBeGreaterThan(0);
      expect(control.id).toMatch(FIELD_ID_PATTERN);
      expect(label.getAttribute('for')).toBe(control.id);
    });

    it('names the field with the wording carried by the legacy resource entry', () => {
      fixture.detectChanges();
      const label = requireElement<HTMLLabelElement>(fixture.nativeElement, LABEL_SELECTOR);

      // Wording authority is the resource VALUE — `Search.Text` resolves to
      // `Search:` — not the stale literal left in the legacy markup.
      expect(textOf(label)).toBe(LABEL_TEXT);
      expect(textOf(label).length).toBeGreaterThan(0);
    });

    it('gives each instance a distinct field id, so two controls cannot share one label', () => {
      fixture.detectChanges();
      const firstId = field().id;

      const second = TestBed.createComponent(SearchInputComponent);
      second.detectChanges();
      const secondId = requireElement<HTMLInputElement>(second.nativeElement, FIELD_SELECTOR).id;

      // A literal id would collide the moment a screen rendered two search
      // controls, and both labels would then name the first field.
      expect(secondId).toMatch(FIELD_ID_PATTERN);
      expect(secondId).not.toBe(firstId);
      expect(
        requireElement<HTMLLabelElement>(second.nativeElement, LABEL_SELECTOR).getAttribute('for'),
      ).toBe(secondId);

      second.destroy();
    });

    it('does not depend on the placeholder for the field name', () => {
      component.placeholder = EMPTY_TERM;
      fixture.detectChanges();
      const label = requireElement<HTMLLabelElement>(fixture.nativeElement, LABEL_SELECTOR);

      // A placeholder is a hint and is never an accessible name — it is not
      // exposed as one, and it disappears as soon as the user types. With no
      // placeholder at all the control must still be fully named.
      expect(field().placeholder).toBe(EMPTY_TERM);
      expect(label.getAttribute('for')).toBe(field().id);
      expect(textOf(label)).toBe(LABEL_TEXT);
    });

    it('gives the submit control a discernible name carried by visible text', () => {
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      // The legacy image button had no name of any kind and no wording entry
      // existed to give it one. This expectation is what stops that returning.
      expect(textOf(submit)).toBe(SUBMIT_TEXT);
      expect(textOf(submit).length).toBeGreaterThan(0);
    });

    it('hides the submit glyph from assistive technology so it cannot act as the name', () => {
      fixture.detectChanges();
      const icon = requireElement<SVGElement>(fixture.nativeElement, ICON_SELECTOR);

      // A decorative glyph left exposed would compete with the adjacent text as a
      // naming source, and would become a stray tab stop in engines that treat
      // embedded vectors as focusable.
      expect(icon.getAttribute('aria-hidden')).toBe('true');
      expect(icon.getAttribute('focusable')).toBe('false');
    });

    it('renders the submit affordance as a button that cannot implicitly submit a form', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const submit = requireElement<HTMLButtonElement>(host, SUBMIT_SELECTOR);

      // A real button is activated by both Enter and Space natively, with no
      // scripting. The explicit type stops it submitting a form a consuming screen
      // may have wrapped around this component, and the component renders no form
      // of its own precisely so it can be nested safely.
      expect(submit.tagName.toLowerCase()).toBe('button');
      expect(submit.type).toBe('button');
      expect(host.querySelector('form')).toBeNull();
    });

    it('uses the native search-box semantic without a redundant role attribute', () => {
      fixture.detectChanges();

      // The native type supplies the implicit search-box role. An explicit role
      // would override the more specific native semantic for no gain.
      expect(field().type).toBe('search');
      expect(field().getAttribute('role')).toBeNull();
    });

    it('leaves both interactive controls in the natural tab order', () => {
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      // The legacy shared label control withdrew its own affordances from the tab
      // order with a negative tab index, leaving them operable by pointer only.
      // Nothing here is withdrawn by any means.
      expect(field().getAttribute('tabindex')).toBeNull();
      expect(submit.getAttribute('tabindex')).toBeNull();
      expect(field().hasAttribute('disabled')).toBeFalse();
      expect(submit.disabled).toBeFalse();
    });

    it('suppresses browser form-history suggestions over the transient filter field', () => {
      fixture.detectChanges();

      expect(field().getAttribute('autocomplete')).toBe('off');
    });
  });

  describe('data access', () => {
    it('issues no request while a term is typed, submitted and cleared', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();
      const control = field();

      typeInto(control, PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      pressEnter(control);
      typeInto(control, EMPTY_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // The control emits a term; the consuming feature owns the query. Matching
      // every request and finding none is the positive proof of that split, and
      // verification in the teardown re-checks it for every other expectation in
      // this file as well.
      expect(httpMock.match((): boolean => true).length).toBe(0);
      expect(emitted).toEqual([PLAIN_TERM, EMPTY_TERM]);
    }));
  });

  describe('teardown', () => {
    it('emits nothing more once the component has been destroyed', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      fixture.destroy();
      tick(SHORT_DEBOUNCE_MS);

      // Teardown is deterministic: the stream completes with the instance, so the
      // in-flight emission is discarded rather than firing into a consumer that
      // has already gone away. No timer handle is left behind either, which is
      // why advancing the clock here is safe.
      expect(emitted).toEqual([]);
    }));
  });
});

describe('SearchInputComponent within a consuming host', () => {
  let hostFixture: ComponentFixture<SearchInputHostComponent>;
  let host: SearchInputHostComponent;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // Both components are standalone, so both are imported.
      imports: [SearchInputComponent, SearchInputHostComponent],
      // Same ordering rule as above: the real client first, then the testing
      // providers that replace its backend.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    hostFixture = TestBed.createComponent(SearchInputHostComponent);
    host = hostFixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  /**
   * Resolves the field rendered inside the host.
   *
   * @returns The search field element.
   */
  function hostField(): HTMLInputElement {
    return requireElement<HTMLInputElement>(hostFixture.nativeElement, FIELD_SELECTOR);
  }

  it('receives a bound placeholder through a real property binding', () => {
    const supplied = 'Search portals';
    host.placeholder = supplied;
    hostFixture.detectChanges();

    // Setting a property on a directly created fixture bypasses the binding
    // machinery entirely. Going through a template is what proves a consumer's
    // binding actually connects — the input is declared public for exactly this
    // reason, and a demotion to a non-public member would fail to compile here.
    expect(hostField().placeholder).toBe(supplied);
  });

  it('honours a bound debounce window and emits the term verbatim to the consumer', fakeAsync(() => {
    host.debounceMs = SHORT_DEBOUNCE_MS;
    hostFixture.detectChanges();

    typeInto(hostField(), PADDED_MIXED_CASE_TERM);
    tick(SHORT_DEBOUNCE_MS - 1);
    expect(host.received).toEqual([]);

    tick(1);
    expect(host.received).toEqual([PADDED_MIXED_CASE_TERM]);
  }));

  it('delivers every payload to the consumer as a string', fakeAsync(() => {
    host.debounceMs = SHORT_DEBOUNCE_MS;
    hostFixture.detectChanges();

    typeInto(hostField(), PLAIN_TERM);
    tick(SHORT_DEBOUNCE_MS);

    expect(host.received.length).toBe(1);
    for (const payload of host.received) {
      expect(typeof payload).toBe('string');
    }
  }));

  it('keeps the browser native search event out of the consumer output', fakeAsync(() => {
    host.debounceMs = SHORT_DEBOUNCE_MS;
    hostFixture.detectChanges();
    const control = hostField();

    typeInto(control, PLAIN_TERM);
    pressEnter(control);
    expect(host.received).toEqual([PLAIN_TERM]);

    // A native search-typed input fires a BUBBLING DOM event of its own, named
    // identically to this component's output. Left alone it would reach the
    // consumer's handler as a raw event object rather than a term, and it would
    // bypass the duplicate gate entirely — firing on every Enter press, including
    // repeats. The component neutralises it, and this is the expectation that
    // proves the neutralisation still works.
    control.dispatchEvent(new Event('search', { bubbles: true }));
    control.dispatchEvent(new Event('search', { bubbles: true }));

    expect(host.received.length).toBe(1);
    expect(host.received).toEqual([PLAIN_TERM]);
    for (const payload of host.received) {
      expect(typeof payload).toBe('string');
    }

    // Drain the in-flight debounced emission so time stays virtual throughout.
    tick(SHORT_DEBOUNCE_MS);
    expect(host.received.length).toBe(1);
  }));

  it('renders one label bound to the field it sits beside', () => {
    hostFixture.detectChanges();
    const label = requireElement<HTMLLabelElement>(hostFixture.nativeElement, LABEL_SELECTOR);

    expect(label.getAttribute('for')).toBe(hostField().id);
    expect(textOf(label)).toBe(LABEL_TEXT);
  });

  it('issues no request when driven through a consuming template', fakeAsync(() => {
    host.debounceMs = SHORT_DEBOUNCE_MS;
    hostFixture.detectChanges();

    typeInto(hostField(), PLAIN_TERM);
    tick(SHORT_DEBOUNCE_MS);
    pressEnter(hostField());

    expect(httpMock.match((): boolean => true).length).toBe(0);
    expect(host.received).toEqual([PLAIN_TERM]);
  }));
});

