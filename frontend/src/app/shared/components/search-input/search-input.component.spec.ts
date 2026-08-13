import {
  ChangeDetectionStrategy,
  type ChangeDetectorRef,
  Component,
  signal,
} from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { type Subscription } from 'rxjs';

import { SearchInputComponent } from './search-input.component';

const DEFAULT_PLACEHOLDER = 'Search';

const DEFAULT_DEBOUNCE_MS = 300;

const SHORT_DEBOUNCE_MS = 50;

const LONG_DEBOUNCE_MS = 5000;

const LABEL_TEXT = 'Search:';

const SUBMIT_TEXT = 'Search';

const WILDCARD_CHARACTER = '%';

const PLAIN_TERM = 'abc';

const NEXT_TERM = 'abd';

const PADDED_MIXED_CASE_TERM = '  AbC  ';

const MIXED_CASE_TERM = 'AbC';

const PERCENT_BEARING_TERM = '50% off';

const EMPTY_TERM = '';

const MARKUP_BEARING_PLACEHOLDER = '<b>x</b>';

const LABEL_SELECTOR = 'label.search-input__label';
const ANY_LABEL_SELECTOR = 'label';

/** The caption a consuming screen paints when it labels the control itself. */
const EXTERNAL_LABEL_TEXT = 'User Name';
const EXTERNAL_LABEL_SELECTOR = 'label.consumer-owned-label';
const ROW_SELECTOR = 'div.search-input__row';
const FIELD_SELECTOR = 'input.search-input__field';
const SUBMIT_SELECTOR = 'button.search-input__submit';
const ICON_SELECTOR = 'svg.search-input__submit-icon';

const FIELD_ID_PATTERN = /^app-search-input-\d+$/;

/**
 * The term bound the component documents on its own `MAX_TERM_LENGTH` constant. Mirrored rather than
 * imported, because the component keeps it module-private, so an accidental change to either fails these
 * expectations loudly.
 */
const MAX_TERM_LENGTH = 256;

/** A term of exactly the bound. */
const AT_BOUND_TERM = 'a'.repeat(MAX_TERM_LENGTH);

/**
 * A control character that survives a single-line control's own value sanitisation, unlike carriage
 * return and line feed.
 */
const TAB_CHARACTER = '\u0009';

/**
 * The four code points on the edges of the two ranges the production policy removes: `\u0000`-`\u001F`
 * (C0) and `\u007F`-`\u009F` (DEL plus C1). Boundaries rather than samples, because a regression that
 * clipped either range by one code point at either end would leave every other expectation here passing.
 */
const CONTROL_CHARACTER_BOUNDARIES: readonly (readonly [string, string])[] = [
  ['U+0000', '\u0000'],
  ['U+001F', '\u001F'],
  ['U+007F', '\u007F'],
  ['U+009F', '\u009F'],
];

/**
 * A no-break space: the code point immediately ABOVE the high edge of the second removed range. The
 * positive control for the boundary cases above.
 */
const NO_BREAK_SPACE_CHARACTER = '\u00A0';

const ASTRAL_CHARACTER = '\u{1F600}';

// Narrowing helpers

/**
 * Resolves a required element, narrowing away the null the DOM query returns.
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

function textOf(element: Element): string {
  return (element.textContent ?? '').trim();
}

/**
 * @param element The rendered element to inspect.
 * @returns The authored class tokens, in document order.
 */
function authoredClassTokens(element: Element): string[] {
  return Array.from(element.classList).filter(
    (token: string): boolean => !token.startsWith('ng-'),
  );
}

/**
 * Reports the index of a child within its parent's element children. Element children only: comment nodes
 * — which the framework emits, and which this template's own documentation comments become — are not
 * counted, so an index here means "the nth rendered element" exactly as a reader would expect.
 *
 * @param parent The containing element.
 * @param child The child whose position is wanted.
 * @returns The zero-based index, or -1 when the child is not a direct child.
 */
function childIndexOf(parent: Element, child: Element): number {
  return Array.from(parent.children).indexOf(child);
}

function typeInto(field: HTMLInputElement, value: string): void {
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

function pressEnter(field: HTMLInputElement): void {
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
}

/**
 * Presses Enter with a CANCELABLE event and reports whether the default action was prevented.
 *
 * @param field The rendered search field.
 * @returns Whether the handler called `preventDefault()`.
 */
function pressCancelableEnter(field: HTMLInputElement): boolean {
  const event = new KeyboardEvent('keydown', {
    key: 'Enter',
    bubbles: true,
    cancelable: true,
  });
  field.dispatchEvent(event);

  return event.defaultPrevented;
}

/**
 * A minimal consuming host, used only where the expectation is about the component's INPUT and OUTPUT
 * BINDINGS rather than about its internals.
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
  public placeholder = DEFAULT_PLACEHOLDER;

  public debounceMs = SHORT_DEBOUNCE_MS;

  public readonly received: unknown[] = [];

  public record(payload: unknown): void {
    this.received.push(payload);
  }
}

/**
 * A consuming host that labels the control ITSELF, which is the shape the role membership screen uses:
 * the shared field wrapper paints the legacy caption and points it at the lookup's own published field
 * identifier. It exists as a real template because the point at issue is an association BETWEEN two
 * components, and a directly created fixture cannot render the outside half of it.
 */
@Component({
  selector: 'app-search-input-labelled-host',
  standalone: true,
  imports: [SearchInputComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (labelHere()) {
      <label class="consumer-owned-label" [attr.for]="lookup.fieldId">{{ caption }}</label>
    }
    <app-search-input #lookup [labelledExternally]="labelHere()" />
  `,
})
class SearchInputExternallyLabelledHostComponent {
  public readonly caption = EXTERNAL_LABEL_TEXT;

  /** Whether this host is labelling the control itself. */
  public readonly labelHere = signal(true);
}

describe('SearchInputComponent', () => {
  let fixture: ComponentFixture<SearchInputComponent>;
  let component: SearchInputComponent;
  let httpMock: HttpTestingController;
  let emitted: string[];
  let subscription: Subscription;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchInputComponent],
      // The real client is provided first and the testing providers then replace its backend. Reversed, the
      // real backend survives and a stray request escapes to the network instead of failing the run.
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
    // Not a formality here: this is the standing check, applied after every expectation in the suite, that
    // no request escaped.
    httpMock.verify();
  });

  function field(): HTMLInputElement {
    return requireElement<HTMLInputElement>(fixture.nativeElement, FIELD_SELECTOR);
  }

  describe('creation and rendered structure', () => {
    it('renders each part of the contract as the exact element and class it names', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

      const label = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);
      const row = requireElement<HTMLElement>(host, ROW_SELECTOR);
      const control = requireElement<HTMLInputElement>(host, FIELD_SELECTOR);
      const submit = requireElement<HTMLButtonElement>(host, SUBMIT_SELECTOR);

      expect(label.tagName).toBe('LABEL');
      expect(row.tagName).toBe('DIV');
      expect(control.tagName).toBe('INPUT');
      expect(submit.tagName).toBe('BUTTON');

      // Both native types are load-bearing rather than cosmetic. `type="search"` is what makes the
      // browser's own clear affordance available, and it is also the reason the component has to suppress a
      // native `search` event at all.
      expect(control.type).toBe('search');
      expect(submit.type).toBe('button');

      // Exact class lists, not `contains` probes: the vocabulary is closed, so an added class is a
      // design-system regression and has to fail here.
      expect(authoredClassTokens(label)).toEqual(['search-input__label']);
      expect(authoredClassTokens(row)).toEqual(['search-input__row']);
      expect(authoredClassTokens(control)).toEqual(['search-input__field']);
      expect(authoredClassTokens(submit)).toEqual(['search-input__submit']);
    });

    it('nests the field and the submit control inside the row, and the label outside it', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

      const label = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);
      const row = requireElement<HTMLElement>(host, ROW_SELECTOR);
      const control = requireElement<HTMLInputElement>(host, FIELD_SELECTOR);
      const submit = requireElement<HTMLButtonElement>(host, SUBMIT_SELECTOR);

      expect(row.contains(control)).toBeTrue();
      expect(row.contains(submit)).toBeTrue();
      expect(row.contains(label)).toBeFalse();
      expect(host.contains(label)).toBeTrue();
    });

    it('renders the label before the row, and the field before the submit control', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

      const label = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);
      const row = requireElement<HTMLElement>(host, ROW_SELECTOR);
      const control = requireElement<HTMLInputElement>(host, FIELD_SELECTOR);
      const submit = requireElement<HTMLButtonElement>(host, SUBMIT_SELECTOR);

      // Document order IS the tab order here, because nothing in this template is withdrawn from it or
      // reordered by a tab index.
      expect(childIndexOf(host, label)).toBe(0);
      expect(childIndexOf(host, row)).toBe(1);
      expect(childIndexOf(row, control)).toBe(0);
      expect(childIndexOf(row, submit)).toBe(1);
    });

    it('renders exactly one field and one submit control', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

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

      fixture.detectChanges();

      expect(field().placeholder).toBe(supplied);
    });

    it('honours an explicitly empty placeholder instead of substituting its fallback', () => {
      component.placeholder = EMPTY_TERM;
      fixture.detectChanges();

      // A `||` fallback would silently defeat this: the empty string is falsy, so an author reaching for one
      // would substitute the default and break the case.
      expect(field().placeholder).toBe(EMPTY_TERM);
      expect(field().placeholder.length).toBe(0);
    });

    it('renders a placeholder carrying angle brackets as literal text, never as markup', () => {
      const host: HTMLElement = fixture.nativeElement;
      component.placeholder = MARKUP_BEARING_PLACEHOLDER;
      fixture.detectChanges();

      expect(field().getAttribute('placeholder')).toBe(MARKUP_BEARING_PLACEHOLDER);
      expect(field().placeholder).toBe(MARKUP_BEARING_PLACEHOLDER);
      expect(host.querySelector('b')).toBeNull();
      expect(host.querySelector('script')).toBeNull();
    });

    it('adds no element when the placeholder carries markup characters', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const benignElementCount = host.querySelectorAll('*').length;

      component.placeholder = MARKUP_BEARING_PLACEHOLDER;
      fixture.detectChanges();

      expect(host.querySelectorAll('*').length).toBe(benignElementCount);
    });
  });

  describe('emitted term fidelity — verbatim term, no wildcard', () => {
    it('emits the typed term exactly, appending no wildcard', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted).toEqual([PLAIN_TERM]);
      expect(emitted[0]).toBe(PLAIN_TERM);
      expect(emitted[0].length).toBe(PLAIN_TERM.length);
      expect(emitted[0].indexOf(WILDCARD_CHARACTER)).toBe(-1);
      expect(emitted[0].endsWith(WILDCARD_CHARACTER)).toBeFalse();
    }));

    it('preserves leading and trailing whitespace, and the casing, of the typed term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PADDED_MIXED_CASE_TERM);
      tick(SHORT_DEBOUNCE_MS);

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

      expect(emitted).toEqual([PLAIN_TERM]);
    }));
  });

  // CALLING OFF A PENDING EMISSION

  describe('calling off a pending emission', () => {
    it('emits nothing when a pending term is called off before its window elapses', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), 'bl');
      tick(SHORT_DEBOUNCE_MS - 10);

      component.cancelPendingSearch();
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted)
        .withContext('a superseded term must not arrive after the intent that superseded it')
        .toEqual([]);
    }));

    it('adopts a term into the field WITHOUT emitting it', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      component.cancelPendingSearch('C');
      fixture.detectChanges();
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(field().value).withContext('the box shows what is being filtered on').toBe('C');
      expect(component.term.value).toBe('C');
      expect(emitted).withContext('and asks for nothing').toEqual([]);
    }));

    it('lets the newer intent win when a term is pending and a term is adopted', fakeAsync(() => {
      // The whole ordering defect, in one case: the older typed term must never arrive.
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), 'bl');
      tick(SHORT_DEBOUNCE_MS - 10);

      component.cancelPendingSearch('C');
      fixture.detectChanges();
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted).toEqual([]);
      expect(field().value).toBe('C');
    }));

    it('records the adopted term as emitted, so re-typing it is not swallowed', fakeAsync(() => {
      // ⚠ A SUBTLE DEFECT IF OMITTED, AND IT RUNS THE OPPOSITE WAY FROM EVERY OTHER CASE HERE. Duplicate
      // suppression compares against the last term this control emitted.
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      component.cancelPendingSearch('C');
      fixture.detectChanges();

      typeInto(field(), 'C');
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted)
        .withContext('the term the consumer already queried is not queried again')
        .toEqual([]);

      typeInto(field(), 'Ca');
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted)
        .withContext('and a genuinely new term still emits')
        .toEqual(['Ca']);
    }));

    it('leaves the box untouched when no term is supplied', fakeAsync(() => {
      // The argument is optional because a consumer may want only the cancellation — the strip's
      // unfiltered affordance adopts the empty term, but a consumer with its own field would not.
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), 'bl');
      tick(SHORT_DEBOUNCE_MS - 10);

      component.cancelPendingSearch();
      fixture.detectChanges();
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(field().value).withContext('what the operator typed is still there').toBe('bl');
      expect(emitted).toEqual([]);
    }));

    it('keeps working after a cancellation, so the stream is not ended by one', fakeAsync(() => {
      // ⚠ A REGRESSION GUARD FOR THE OBVIOUS WRONG IMPLEMENTATION. Cancelling with `takeUntil` would
      // COMPLETE the stream, so the first cancellation would silently make the control inert for the rest
      // of its life — every later keystroke swallowed with nothing to indicate why.
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), 'bl');
      component.cancelPendingSearch();
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted).toEqual([]);

      typeInto(field(), 'zz');
      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted)
        .withContext('the control still searches after being called off')
        .toEqual(['zz']);
    }));

    it('does not disturb the immediate submit path', fakeAsync(() => {
      // Enter emits at once through a different funnel, and a cancellation must not have left that
      // path broken either.
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      component.cancelPendingSearch('C');
      fixture.detectChanges();

      typeInto(field(), 'Cb');
      component.submit();

      expect(emitted).toEqual(['Cb']);

      tick(SHORT_DEBOUNCE_MS * 4);

      expect(emitted)
        .withContext('and the debounced path does not then emit the same term again')
        .toEqual(['Cb']);
    }));
  });

  describe('the empty term', () => {
    it('emits the empty string when an empty field is submitted', () => {
      fixture.detectChanges();

      pressEnter(field());

      expect(emitted).toEqual([EMPTY_TERM]);
      expect(emitted[0]).toBe(EMPTY_TERM);
      expect(emitted[0].length).toBe(0);
      expect(typeof emitted[0]).toBe('string');
    });

    it('emits a first empty term even though nothing has been emitted before it', () => {
      fixture.detectChanges();

      pressEnter(field());

      // The boundary case for the duplicate guard: this first emission carries the very value an "absent"
      // marker would most naturally have been given.
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

      // A cleared field emits rather than falling silent; silence would leave the previous term applied with
      // no way for the consumer to learn otherwise.
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

      expect(emitted).toEqual([PLAIN_TERM]);

      // Drain the in-flight debounced emission so no timer outlives the test. It carries the SAME term, and
      // the shared guard discards it — which is the whole reason both paths funnel through one place.
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

      expect(emitted).toEqual([]);

      tick(0);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('clamps a negative window to zero rather than rejecting it', fakeAsync(() => {
      component.debounceMs = -1000;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(0);

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

      // Had the window been captured once at initialisation, the second term would still be waiting on the
      // long one and this expectation would fail.
      expect(emitted).toEqual(['a', PLAIN_TERM]);
    }));
  });

  describe('duplicate suppression and explicit re-query', () => {
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

    it('re-emits when Enter repeats a term the debounced path already emitted', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);

      pressEnter(field());

      expect(emitted).toEqual([PLAIN_TERM, PLAIN_TERM]);
      expect(emitted.length).toBe(2);
    }));

    it('re-emits every time the submit control is pressed, even on an unchanged term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      submit.click();
      submit.click();

      expect(emitted).toEqual([PLAIN_TERM, PLAIN_TERM, PLAIN_TERM]);
    }));

    it('re-emits once per Enter press, and the debounced tail is still absorbed', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();
      const control = field();

      typeInto(control, PLAIN_TERM);
      pressEnter(control);
      pressEnter(control);
      pressEnter(control);

      // ⚠ #7 — one emission per explicit press. See the two specifications above for why an explicit
      // submit is never treated as a duplicate.
      expect(emitted).toEqual([PLAIN_TERM, PLAIN_TERM, PLAIN_TERM]);
      expect(emitted.length).toBe(3);

      tick(SHORT_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM, PLAIN_TERM, PLAIN_TERM]);
    }));

    it('re-emits an empty term on every explicit press, because a clear is a command too', () => {
      fixture.detectChanges();

      pressEnter(field());
      pressEnter(field());

      expect(emitted).toEqual([EMPTY_TERM, EMPTY_TERM]);
      expect(emitted.length).toBe(2);
    });

    it('emits again as soon as the term genuinely changes', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), NEXT_TERM);
      tick(SHORT_DEBOUNCE_MS);

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

      expect(emitted).toEqual([PLAIN_TERM, NEXT_TERM, PLAIN_TERM]);
    }));
  });

  describe('term bound and control-character removal', () => {
    it('puts the same bound on the field itself, as a plain attribute', () => {
      fixture.detectChanges();

      expect(field().getAttribute('maxlength')).toBe(String(MAX_TERM_LENGTH));
      expect(component.maxTermLength).toBe(MAX_TERM_LENGTH);
    });

    it('does not attach a length validator to the reactive control', () => {
      fixture.detectChanges();

      component.term.setValue('a'.repeat(MAX_TERM_LENGTH * 2));

      // The reason the template uses `[attr.maxlength]`.
      expect(component.term.errors).toBeNull();
      expect(component.term.valid).toBeTrue();
    });

    it('emits a term of exactly the bound unaltered', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), AT_BOUND_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // The bound is inclusive, so at the limit nothing is removed. That is what keeps every verbatim
      // expectation in this file describing real behaviour.
      expect(emitted).toEqual([AT_BOUND_TERM]);
    }));

    it('emits only the leading bounded prefix of an over-long term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), `${AT_BOUND_TERM}overflow-that-must-not-be-emitted`);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted.length).toBe(1);
      expect(emitted[0]).toBe(AT_BOUND_TERM);
      expect(emitted[0].length).toBe(MAX_TERM_LENGTH);
    }));

    it('bounds the term reached through a programmatic setValue as well', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      component.term.setValue('b'.repeat(MAX_TERM_LENGTH * 3));
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted[0].length).toBe(MAX_TERM_LENGTH);
    }));

    it('bounds the immediate submission path, not only the debounced one', () => {
      component.debounceMs = LONG_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), `${AT_BOUND_TERM}overflow`);
      pressEnter(field());

      // Both paths funnel through one place, so neither can be bounded while the other is not. The long
      // debounce guarantees the emission observed here is the immediate one.
      expect(emitted).toEqual([AT_BOUND_TERM]);
    });

    it('removes a control character before emitting', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), `ab${TAB_CHARACTER}c`);
      tick(SHORT_DEBOUNCE_MS);

      // A tab survives a single-line control's own value sanitisation, unlike a carriage return or line
      // feed, so it is the case worth pinning.
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('removes a control character without disturbing the rest of the term', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), `  Ab${TAB_CHARACTER}C  `);
      tick(SHORT_DEBOUNCE_MS);

      // Surrounding whitespace, interior spacing and mixed case all survive: removal is targeted at control
      // characters alone and is not a general normalisation pass.
      expect(emitted).toEqual([PADDED_MIXED_CASE_TERM]);
    }));

    it('removes the code points on every edge of both control ranges', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      // The two expectations above pin ONE character, the tab.
      for (const [codePointLabel, character] of CONTROL_CHARACTER_BOUNDARIES) {
        const marker = codePointLabel.slice('U+'.length);

        component.term.setValue(`${PLAIN_TERM}${character}${marker}`);
        tick(SHORT_DEBOUNCE_MS);

        expect(emitted.at(-1))
          .withContext(`${codePointLabel} must be stripped before the term is emitted`)
          .toBe(`${PLAIN_TERM}${marker}`);
      }

      // Every edge produced an emission of its own, so none was absorbed by the duplicate guard and silently
      // left unasserted.
      expect(emitted.length).toBe(CONTROL_CHARACTER_BOUNDARIES.length);
    }));

    it('leaves a no-break space intact, one code point above the removed range', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      // The positive control for the four edges above. Each of those proves a character is removed, and a
      // pattern widened far enough to swallow ordinary text would satisfy all four just as happily as the
      // correct one does.
      const termCarryingNoBreakSpace = `${PLAIN_TERM}${NO_BREAK_SPACE_CHARACTER}${NEXT_TERM}`;

      component.term.setValue(termCarryingNoBreakSpace);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted).toEqual([termCarryingNoBreakSpace]);
      expect(emitted[0].length).toBe(termCarryingNoBreakSpace.length);
    }));

    it('leaves a term carrying neither a control character nor excess length untouched', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PERCENT_BEARING_TERM);
      tick(SHORT_DEBOUNCE_MS);

      // The bound must be a strict no-op for ordinary input, including a term carrying a percent character
      // that is emphatically not treated as a wildcard here.
      expect(emitted[0]).toBe(PERCENT_BEARING_TERM);
      expect(emitted[0]).toBe(field().value);
    }));

    it('suppresses the duplicate two over-long terms sharing a bounded prefix produce', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), `${AT_BOUND_TERM}first-tail`);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), `${AT_BOUND_TERM}second-different-tail`);
      tick(SHORT_DEBOUNCE_MS);

      // The reason bounding precedes the duplicate guard. These two control values differ, so a guard
      // comparing unbounded values would pass both and emit the identical bounded term twice - two
      // identical queries.
      expect(emitted).toEqual([AT_BOUND_TERM]);
    }));

    it('does not emit half a surrogate pair when the cut falls inside one', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      // The pair straddles the boundary: its high half at the last retained index and its low half at the
      // first discarded one.
      const filler = 'a'.repeat(MAX_TERM_LENGTH - 1);
      typeInto(field(), `${filler}${ASTRAL_CHARACTER}${'b'.repeat(32)}`);
      tick(SHORT_DEBOUNCE_MS);

      expect(emitted[0]).toBe(filler);
      expect(emitted[0].length).toBe(MAX_TERM_LENGTH - 1);
      // The decisive consequence: a lone surrogate makes this throw a URIError.
      expect(() => encodeURIComponent(emitted[0])).not.toThrow();
    }));

    it('keeps an astral character whole when the whole pair fits inside the bound', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      const filler = 'a'.repeat(MAX_TERM_LENGTH - 2);
      typeInto(field(), `${filler}${ASTRAL_CHARACTER}${'b'.repeat(32)}`);
      tick(SHORT_DEBOUNCE_MS);

      // The complementary case: stepping back is applied only where it is needed, so a pair that fits is
      // retained in full and the bound is reached exactly.
      expect(emitted[0].length).toBe(MAX_TERM_LENGTH);
      expect(emitted[0].endsWith(ASTRAL_CHARACTER)).toBeTrue();
      expect(() => encodeURIComponent(emitted[0])).not.toThrow();
    }));

    it('emits the empty term when a term consists only of control characters', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(SHORT_DEBOUNCE_MS);
      typeInto(field(), `${TAB_CHARACTER}${TAB_CHARACTER}`);
      tick(SHORT_DEBOUNCE_MS);

      // Removal can empty a term, and the empty term is a meaningful value here - it means omit the query
      // parameter and request the unfiltered page. It must therefore be emitted rather than swallowed.
      expect(emitted).toEqual([PLAIN_TERM, EMPTY_TERM]);
    }));

    it('appends no wildcard and performs no encoding while bounding', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), `50%${TAB_CHARACTER} off${'x'.repeat(MAX_TERM_LENGTH)}`);
      tick(SHORT_DEBOUNCE_MS);

      // Bounding must not become a licence to normalise: the percent stays a literal percent, nothing is
      // percent-encoded and no trailing wildcard is introduced.
      expect(emitted[0].startsWith(PERCENT_BEARING_TERM)).toBeTrue();
      expect(emitted[0].indexOf('%25')).toBe(-1);
      expect(emitted[0].endsWith(WILDCARD_CHARACTER)).toBeFalse();
      expect(emitted[0].length).toBe(MAX_TERM_LENGTH);
    }));
  });

  describe('accessible name and keyboard operability', () => {
    it('associates the visible label with the field through matching for and id attributes', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const label = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);
      const control = field();

      expect(control.id.length).toBeGreaterThan(0);
      expect(control.id).toMatch(FIELD_ID_PATTERN);
      expect(label.getAttribute('for')).toBe(control.id);
    });

    it('names the field with the wording carried by the legacy resource entry', () => {
      fixture.detectChanges();
      const label = requireElement<HTMLLabelElement>(fixture.nativeElement, LABEL_SELECTOR);

      expect(textOf(label)).toBe(LABEL_TEXT);
      expect(textOf(label).length).toBeGreaterThan(0);
    });

    it('gives each instance a distinct field id, so two controls cannot share one label', () => {
      fixture.detectChanges();
      const firstId = field().id;

      const second = TestBed.createComponent(SearchInputComponent);
      second.detectChanges();
      const secondId = requireElement<HTMLInputElement>(second.nativeElement, FIELD_SELECTOR).id;

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

      expect(field().placeholder).toBe(EMPTY_TERM);
      expect(label.getAttribute('for')).toBe(field().id);
      expect(textOf(label)).toBe(LABEL_TEXT);
    });

    it('gives the submit control a discernible name carried by visible text', () => {
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      expect(textOf(submit)).toBe(SUBMIT_TEXT);
      expect(textOf(submit).length).toBeGreaterThan(0);
    });

    it('hides the submit glyph from assistive technology so it cannot act as the name', () => {
      fixture.detectChanges();
      const icon = requireElement<SVGElement>(fixture.nativeElement, ICON_SELECTOR);

      expect(icon.getAttribute('aria-hidden')).toBe('true');
      expect(icon.getAttribute('focusable')).toBe('false');
    });

    it('renders the submit affordance as a button that cannot implicitly submit a form', () => {
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const submit = requireElement<HTMLButtonElement>(host, SUBMIT_SELECTOR);

      expect(submit.tagName.toLowerCase()).toBe('button');
      expect(submit.type).toBe('button');
      expect(host.querySelector('form')).toBeNull();
    });

    it('uses the native search-box semantic without a redundant role attribute', () => {
      fixture.detectChanges();

      expect(field().type).toBe('search');
      expect(field().getAttribute('role')).toBeNull();
    });

    it('leaves both interactive controls in the natural tab order', () => {
      fixture.detectChanges();
      const submit = requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);

      expect(field().getAttribute('tabindex')).toBeNull();
      expect(submit.getAttribute('tabindex')).toBeNull();
      expect(field().hasAttribute('disabled')).toBeFalse();
      expect(submit.disabled).toBeFalse();
    });

    it('suppresses browser form-history suggestions over the transient filter field', () => {
      fixture.detectChanges();

      expect(field().getAttribute('autocomplete')).toBe('off');
    });

    it('withholds its own caption entirely when the consumer states it has labelled the control', () => {
      component.labelledExternally = true;
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;

      // Not merely the classed label: ANY label rendered here would be a second one at the
      // consumer, which is the whole defect this mode removes.
      expect(host.querySelector(LABEL_SELECTOR)).toBeNull();
      expect(host.querySelector(ANY_LABEL_SELECTOR)).toBeNull();
      expect(Array.from(field().labels ?? []).length).toBe(0);
    });

    it('keeps publishing the field id when its own caption is withheld, so a consumer can name it', () => {
      component.labelledExternally = true;
      fixture.detectChanges();
      const control = field();

      // Withholding the caption must not withhold the hook the consumer needs; without the id
      // the mode would trade a duplicated name for no name at all.
      expect(control.id).toMatch(FIELD_ID_PATTERN);
      expect(component.fieldId).toBe(control.id);
    });

    it('names itself by default, so a consumer that says nothing gets a labelled control', () => {
      // The default is the safe direction, and it is the default that a consumer relies on
      // implicitly: three of the four screens using this control declare nothing at all.
      expect(component.labelledExternally).toBeFalse();

      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      const label = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);

      expect(textOf(label)).toBe(LABEL_TEXT);
      expect(host.querySelectorAll(ANY_LABEL_SELECTOR).length).toBe(1);
      expect(Array.from(field().labels ?? []).length).toBe(1);
    });

    it('names itself when external labelling is declared and then explicitly denied', () => {
      component.labelledExternally = false;
      fixture.detectChanges();

      expect(textOf(requireElement<HTMLLabelElement>(fixture.nativeElement, LABEL_SELECTOR))).toBe(
        LABEL_TEXT,
      );
      expect(Array.from(field().labels ?? []).length).toBe(1);
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

      expect(httpMock.match((): boolean => true).length).toBe(0);

      expect(emitted).toEqual([PLAIN_TERM, PLAIN_TERM, EMPTY_TERM]);
    }));
  });

  describe('teardown', () => {
    it('emits nothing more once the component has been destroyed', fakeAsync(() => {
      component.debounceMs = SHORT_DEBOUNCE_MS;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      fixture.destroy();
      tick(SHORT_DEBOUNCE_MS);

      // Destroying with an emission still in flight is the only way to reach the case; the clock is advanced
      // afterwards to prove nothing arrives late.
      expect(emitted).toEqual([]);
    }));

    it('stops marking the destroyed view for check when the control status changes', () => {
      fixture.detectChanges();

      // The subscription's only effect is a call to the injected change detector, so that call is the
      // observable. The reference is reached through a cast because the component keeps it private, which
      // is correct — it is an implementation detail of `OnPush` and no consumer should touch it.
      const injectedChangeDetector = (
        component as unknown as { readonly changeDetectorRef: ChangeDetectorRef }
      ).changeDetectorRef;
      const markForCheck = spyOn(injectedChangeDetector, 'markForCheck');

      // Alive first, so the expectation below is known to be capable of failing. Availability changes from
      // outside the binding graph — a consumer calling `term.disable()` — which is exactly why `OnPush`
      // needs the explicit mark.
      component.term.disable();

      expect(markForCheck)
        .withContext('a live component must mark its view when the control status changes')
        .toHaveBeenCalled();

      fixture.destroy();

      // Anything the framework's own teardown did is discarded here. The question is only whether a status
      // change occurring AFTER destruction still reaches the destroyed view, which is the leak
      // `takeUntilDestroyed` exists to close.
      markForCheck.calls.reset();

      component.term.enable();

      expect(markForCheck)
        .withContext('a destroyed component must not mark its view for check')
        .not.toHaveBeenCalled();
    });

    it('removes the native search suppressor from its host element once destroyed', () => {
      fixture.detectChanges();

      const host: HTMLElement = fixture.nativeElement;
      const reachedTheConsumer: string[] = [];
      host.addEventListener('search', (): void => {
        reachedTheConsumer.push('search');
      });

      field().dispatchEvent(new Event('search', { bubbles: true }));

      expect(reachedTheConsumer)
        .withContext('a live component must swallow the native search event')
        .toEqual([]);

      fixture.destroy();

      // Dispatched on the retained HOST rather than on the field, because whether the field is still
      // attached after view destruction is the framework's business and not this component's contract.
      host.dispatchEvent(new Event('search', { bubbles: true }));

      expect(reachedTheConsumer)
        .withContext('a destroyed component must leave no listener on its former host')
        .toEqual(['search']);
    });
  });

  describe('disabled state', () => {
    /**
     * Resolves the rendered submit control for the fixture under test.
     *
     * @returns The submit button element.
     */
    function submitButton(): HTMLButtonElement {
      return requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR);
    }

    it('starts enabled, with one source of truth agreeing with both controls', () => {
      fixture.detectChanges();

      expect(component.isDisabled).toBeFalse();
      expect(field().disabled).toBeFalse();
      expect(submitButton().disabled).toBeFalse();
    });

    it('disables BOTH the field and the submit button', () => {
      fixture.detectChanges();
      component.term.disable();
      fixture.detectChanges();

      expect(component.isDisabled).toBeTrue();
      // The field is form-bound, so the forms directive renders its attribute.
      expect(field().disabled).toBeTrue();
      // The button is NOT form-bound, so this can only come from the component's own binding - the half that
      // is easy to omit.
      expect(submitButton().disabled).toBeTrue();
    });

    it('emits nothing through the debounced stream while disabled', fakeAsync(() => {
      fixture.detectChanges();
      component.term.disable();
      fixture.detectChanges();

      tick(DEFAULT_DEBOUNCE_MS);
      expect(emitted).toEqual([]);

      component.term.setValue(PLAIN_TERM);
      tick(DEFAULT_DEBOUNCE_MS);
      expect(emitted).toEqual([]);
    }));

    it('emits nothing through a programmatic submit while disabled', fakeAsync(() => {
      fixture.detectChanges();
      component.term.setValue(PLAIN_TERM);
      component.term.disable();
      fixture.detectChanges();

      component.submit();
      tick(DEFAULT_DEBOUNCE_MS);

      expect(emitted).toEqual([]);
    }));

    it('restores both controls and emission when re-enabled', fakeAsync(() => {
      fixture.detectChanges();
      component.term.disable();
      fixture.detectChanges();
      tick(DEFAULT_DEBOUNCE_MS);

      component.term.enable();
      fixture.detectChanges();

      expect(field().disabled).toBeFalse();
      expect(submitButton().disabled).toBeFalse();

      component.term.setValue(PLAIN_TERM);
      tick(DEFAULT_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));
  });

  describe('non-finite debounce windows', () => {
    it('falls back to the default window for NaN, not to immediate emission', fakeAsync(() => {
      component.debounceMs = Number.NaN;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);

      tick(DEFAULT_DEBOUNCE_MS - 1);
      expect(emitted).toEqual([]);

      tick(1);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('falls back to the default window for Infinity, not to never emitting', fakeAsync(() => {
      component.debounceMs = Number.POSITIVE_INFINITY;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);

      tick(DEFAULT_DEBOUNCE_MS - 1);
      expect(emitted).toEqual([]);

      tick(1);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('falls back to the default window for negative Infinity', fakeAsync(() => {
      // Caught by the same finite check. Left to the clamp it would have become zero and been
      // indistinguishable from a deliberate zero.
      component.debounceMs = Number.NEGATIVE_INFINITY;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);

      tick(DEFAULT_DEBOUNCE_MS - 1);
      expect(emitted).toEqual([]);

      tick(1);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('still clamps a FINITE negative window rather than falling back', fakeAsync(() => {
      // A finite negative value is a legitimate mis-binding that should degrade to "as soon as possible",
      // and that behaviour is deliberately preserved.
      component.debounceMs = -1000;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(0);

      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('honours an explicit zero window and never treats it as absent', fakeAsync(() => {
      component.debounceMs = 0;
      fixture.detectChanges();

      typeInto(field(), PLAIN_TERM);
      tick(0);

      expect(emitted).toEqual([PLAIN_TERM]);
    }));
  });

  describe('implicit form submission', () => {
    it('prevents the default action when Enter is pressed', () => {
      fixture.detectChanges();
      typeInto(field(), PLAIN_TERM);

      expect(pressCancelableEnter(field())).toBeTrue();
    });

    it('still emits exactly once, with no duplicate from the debounced tail', fakeAsync(() => {
      fixture.detectChanges();
      typeInto(field(), PLAIN_TERM);

      pressCancelableEnter(field());
      // Emission is immediate, without advancing the clock at all.
      expect(emitted).toEqual([PLAIN_TERM]);

      // The pending debounced emission carries the same term and must be absorbed by the duplicate guard
      // rather than firing a second query.
      tick(DEFAULT_DEBOUNCE_MS);
      expect(emitted).toEqual([PLAIN_TERM]);
    }));

    it('leaves the submit control with no default action to cancel', () => {
      fixture.detectChanges();

      // `type="button"` is what makes the click path safe with no event, which is why the handler's event
      // parameter is optional rather than required.
      expect(
        requireElement<HTMLButtonElement>(fixture.nativeElement, SUBMIT_SELECTOR).type,
      ).toBe('button');
    });
  });
});

describe('SearchInputComponent within a consuming host', () => {
  let hostFixture: ComponentFixture<SearchInputHostComponent>;
  let host: SearchInputHostComponent;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchInputComponent, SearchInputHostComponent],
      // Same ordering rule as above: the real client first, then the testing providers that replace its
      // backend.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    hostFixture = TestBed.createComponent(SearchInputHostComponent);
    host = hostFixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function hostField(): HTMLInputElement {
    return requireElement<HTMLInputElement>(hostFixture.nativeElement, FIELD_SELECTOR);
  }

  it('receives a bound placeholder through a real property binding', () => {
    const supplied = 'Search portals';
    host.placeholder = supplied;
    hostFixture.detectChanges();

    // A direct property set on a component fixture bypasses the binding machinery, so only a real template
    // proves that a consumer's binding actually connects.
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

    // Only a real template binding can observe this collision, which is why the case lives in the host
    // suite; a regression delivers a raw event object here instead.
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

    expect(host.received).toEqual([PLAIN_TERM, PLAIN_TERM]);
  }));
});

/**
 * Dispatches `Enter` as a CANCELLABLE event.
 *
 * @param field The search field to press `Enter` in.
 * @returns The dispatched event, so its cancellation state can be inspected.
 */
function pressEnterCancelable(field: HTMLInputElement): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
  field.dispatchEvent(event);

  return event;
}

@Component({
  selector: 'app-search-input-form-host',
  standalone: true,
  imports: [SearchInputComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form (submit)="recordSubmit($event)">
      <app-search-input [debounceMs]="debounceMs" (search)="record($event)" />
    </form>
  `,
})
class SearchInputFormHostComponent {
  /** Debounce window handed down through a real property binding. */
  public debounceMs = SHORT_DEBOUNCE_MS;

  /** Every term the consumer's search handler received, in order. */
  public readonly received: unknown[] = [];

  /** How many times the wrapping form attempted to submit. */
  public submitCount = 0;

  public record(term: unknown): void {
    this.received.push(term);
  }

  public recordSubmit(event: Event): void {
    this.submitCount += 1;
    // A real submission would navigate the Karma runner away and take the whole run with it, so the attempt
    // is recorded and then stopped dead.
    event.preventDefault();
  }
}

describe('SearchInputComponent within a consuming form', () => {
  let hostFixture: ComponentFixture<SearchInputFormHostComponent>;
  let host: SearchInputFormHostComponent;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchInputComponent, SearchInputFormHostComponent],
      // Same ordering rule as the suites above: the real client first, then the testing providers that
      // replace its backend.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    hostFixture = TestBed.createComponent(SearchInputFormHostComponent);
    host = hostFixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    hostFixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** Resolves the field rendered inside the form host. */
  function formField(): HTMLInputElement {
    return requireElement<HTMLInputElement>(hostFixture.nativeElement, FIELD_SELECTOR);
  }

  it('suppresses the default action of an Enter press inside a form', () => {
    // Suppressing the default is the mechanism that defeats implicit submission. It is asserted directly
    // because a synthetic key event cannot itself trigger the user agent's implicit submission — only a
    // trusted one can — so the cancellation state is the honest observable in a test runner.
    const event = pressEnterCancelable(formField());

    expect(event.defaultPrevented)
      .withContext('Enter inside a form must not be left to trigger implicit submission')
      .toBeTrue();
  });

  // DO NOT assert that `submitCount` is zero after dispatching Enter: such an assertion cannot fail.

  it('still emits the term exactly once on Enter inside a form', fakeAsync(() => {
    // The fix must not have cost the feature it was protecting. Typing starts the debounce, Enter emits
    // immediately, and ticking past the window proves the duplicate guard absorbs the debounced tail rather
    // than emitting twice.
    typeInto(formField(), PLAIN_TERM);
    pressEnterCancelable(formField());
    tick(SHORT_DEBOUNCE_MS);

    expect(host.received).toEqual([PLAIN_TERM]);
  }));

  it('keeps the submit affordance non-submitting inside a form', () => {
    // The button type remains part of the contract. It is necessary but, as the expectations above
    // establish, not sufficient on its own.
    const submit = requireElement<HTMLButtonElement>(hostFixture.nativeElement, SUBMIT_SELECTOR);

    expect(submit.type).toBe('button');
  });

  it('emits the term exactly once when the affordance is clicked inside a form', fakeAsync(() => {
    // The click path passes no event to suppress, so this expectation proves the optional parameter left
    // that path intact.
    typeInto(formField(), PLAIN_TERM);
    requireElement<HTMLButtonElement>(hostFixture.nativeElement, SUBMIT_SELECTOR).click();
    tick(SHORT_DEBOUNCE_MS);

    expect(host.received).toEqual([PLAIN_TERM]);
    expect(host.submitCount).toBe(0);
  }));

  it('issues no request of its own from within a form', () => {
    typeInto(formField(), PLAIN_TERM);
    pressEnterCancelable(formField());

    expect(httpMock.match((): boolean => true)).toEqual([]);
  });
});

/** The mode the role membership screen uses. */
describe('SearchInputComponent labelled by its consumer', () => {
  let hostFixture: ComponentFixture<SearchInputExternallyLabelledHostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchInputComponent, SearchInputExternallyLabelledHostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    hostFixture = TestBed.createComponent(SearchInputExternallyLabelledHostComponent);
    hostFixture.detectChanges();
  });

  function labelledHostField(): HTMLInputElement {
    return requireElement<HTMLInputElement>(hostFixture.nativeElement, FIELD_SELECTOR);
  }

  it('leaves the control named exactly once, by the consumer', () => {
    const host: HTMLElement = hostFixture.nativeElement;
    const labels = Array.from(labelledHostField().labels ?? []);

    // ONE is the whole point. Two labels give the control a composite accessible name in which
    // the caption nearest the box is no longer the whole name, which is the SC 2.5.3 mismatch.
    expect(labels.length).toBe(1);
    expect(host.querySelectorAll(ANY_LABEL_SELECTOR).length).toBe(1);
    expect(host.querySelector(LABEL_SELECTOR)).toBeNull();
  });

  it('is named by the consumer wording, which is the caption a reader sees beside the box', () => {
    const label = requireElement<HTMLLabelElement>(
      hostFixture.nativeElement,
      EXTERNAL_LABEL_SELECTOR,
    );

    expect(textOf(label)).toBe(EXTERNAL_LABEL_TEXT);
    expect(textOf(label)).not.toBe(LABEL_TEXT);
    expect(label.getAttribute('for')).toBe(labelledHostField().id);
    expect(label.control).toBe(labelledHostField());
  });

  it('paints the caption before the control it names', () => {
    const host: HTMLElement = hostFixture.nativeElement;
    const label = requireElement<HTMLLabelElement>(host, EXTERNAL_LABEL_SELECTOR);

    // A user reads the nearest preceding caption as the field's name, so the order is part of
    // the claim rather than incidental.
    expect(
      label.compareDocumentPosition(labelledHostField()) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeGreaterThan(0);
  });

  it('keeps the field and its submit affordance fully operable in this mode', () => {
    const submit = requireElement<HTMLButtonElement>(hostFixture.nativeElement, SUBMIT_SELECTOR);

    expect(labelledHostField().type).toBe('search');
    expect(labelledHostField().hasAttribute('disabled')).toBeFalse();
    expect(submit.disabled).toBeFalse();
    expect(textOf(submit)).toBe(SUBMIT_TEXT);
  });

  it('takes its own caption back when the consumer stops labelling it', () => {
    hostFixture.componentInstance.labelHere.set(false);
    hostFixture.detectChanges();
    const host: HTMLElement = hostFixture.nativeElement;

    // The control is never left nameless in either direction: the count stays at one across the
    // change, and the surviving label is this component's own, re-associated with the same field.
    expect(host.querySelector(EXTERNAL_LABEL_SELECTOR)).toBeNull();
    expect(host.querySelectorAll(ANY_LABEL_SELECTOR).length).toBe(1);

    const own = requireElement<HTMLLabelElement>(host, LABEL_SELECTOR);
    expect(textOf(own)).toBe(LABEL_TEXT);
    expect(own.getAttribute('for')).toBe(labelledHostField().id);
    expect(Array.from(labelledHostField().labels ?? []).length).toBe(1);
  });
});
