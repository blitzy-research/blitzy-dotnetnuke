import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DefaultUrlSerializer } from '@angular/router';

import { EmptyStateComponent } from './empty-state.component';

const NOT_FOUND_MESSAGE = 'Page not found';

/**
 * The component's documented default wording, RESTATED INDEPENDENTLY. Deliberately a literal rather than
 * a read of `EmptyStateComponent`'s own default.
 */
const DEFAULT_MESSAGE = 'No records found.';

const STATIC_TITLE = 'Nothing to Display';

const TITLE_CLASS = 'empty-state__title';

const MESSAGE_CLASS = 'empty-state__message';

const GLYPH_CLASS = 'empty-state__glyph';

/**
 * A representative in-page zero-result sentence, taken from the usage example documented on the component
 * itself. Held distinct from {@link NOT_FOUND_MESSAGE} because `ComponentRef.setInput` discards a write
 * whose value is identical to the previous one, so a re-render expectation is meaningful solely when the
 * second value differs from the first.
 */
const LIST_EMPTY_MESSAGE = 'No roles match the current filter.';

const MARKUP_BEARING_MESSAGE = '<b>x</b>';

const SCRIPT_BEARING_MESSAGE = '<script type="text/javascript">document.title = "x";</script>';

const PROJECTED_ACTION_LABEL = 'Add New Role';

const BLOCK_CLASS = 'empty-state';

/** Landmark selectors this component must never emit. */
const LANDMARK_SELECTORS = 'header, main, nav, footer';

/**
 * Announcement selectors this component must never emit, asserted for the same reason as the landmarks
 * above.
 */
const LIVE_REGION_SELECTORS = '[aria-live], [role="alert"]';

const ACTION_SELECTORS = 'button, a';

/** Spec-local host that exercises content projection. */
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
  public readonly wording: string = LIST_EMPTY_MESSAGE;

  public readonly actionLabel: string = PROJECTED_ACTION_LABEL;
}

function rootElementOf<T>(fixture: ComponentFixture<T>): HTMLElement {
  const root: HTMLElement = fixture.nativeElement;
  return root;
}

function renderedTextOf(element: Element): string {
  return element.textContent ?? '';
}

function requireElement(root: ParentNode, selector: string): HTMLElement {
  const found: HTMLElement | null = root.querySelector<HTMLElement>(selector);

  if (found === null) {
    throw new Error(`Expected the rendered output to contain an element matching "${selector}".`);
  }

  return found;
}

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

function belongsToBlock(token: string): boolean {
  return (
    token === BLOCK_CLASS ||
    token.startsWith(`${BLOCK_CLASS}__`) ||
    token.startsWith(`${BLOCK_CLASS}--`)
  );
}

describe('EmptyStateComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EmptyStateComponent, EmptyStateHostComponent],
    }).compileComponents();
  });

  /**
   * Creates the component as a ROOT component with no projected content. WHAT THIS DOES AND DOES NOT
   * ESTABLISH. It reproduces the *mechanism* a router uses - a root component whose inputs are written
   * through the component reference - and nothing more.
   */
  const createAsRootComponent = (): ComponentFixture<EmptyStateComponent> =>
    TestBed.createComponent(EmptyStateComponent);

  const renderWith = (
    fixture: ComponentFixture<EmptyStateComponent>,
    message: unknown,
  ): HTMLElement => {
    fixture.componentRef.setInput('message', message);
    fixture.detectChanges();

    return rootElementOf(fixture);
  };

  describe('public message input contract', () => {
    it('renders the wording a caller binds to its message input', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      expect(renderedTextOf(root)).toContain(NOT_FOUND_MESSAGE);
    });

    it('lands the bound value on the declared public accessor', () => {
      const fixture = createAsRootComponent();

      renderWith(fixture, NOT_FOUND_MESSAGE);

      expect(fixture.componentInstance.message).toBe(NOT_FOUND_MESSAGE);
    });

    it('re-renders when a later navigation supplies different wording', () => {
      const fixture = createAsRootComponent();
      renderWith(fixture, NOT_FOUND_MESSAGE);

      const root = renderWith(fixture, LIST_EMPTY_MESSAGE);

      expect(renderedTextOf(root)).toContain(LIST_EMPTY_MESSAGE);
      expect(renderedTextOf(root)).not.toContain(NOT_FOUND_MESSAGE);
    });

    it('preserves a supplied value verbatim, without trimming or reformatting', () => {
      const padded = `  ${NOT_FOUND_MESSAGE}  `;
      const fixture = createAsRootComponent();

      renderWith(fixture, padded);

      expect(fixture.componentInstance.message).toBe(padded);
    });
  });

  // Automated proof that `message` reaches the DOM as text, since wording can
  // arrive already carrying markup.
  describe('untrusted wording reaches the DOM as plain text', () => {
    it('renders balanced markup as literal characters', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, MARKUP_BEARING_MESSAGE);

      expect(renderedTextOf(root)).toContain(MARKUP_BEARING_MESSAGE);
    });

    it('creates no element from balanced markup', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, MARKUP_BEARING_MESSAGE);

      expect(root.querySelector('b')).toBeNull();
    });

    it('renders a script-bearing value as literal characters', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, SCRIPT_BEARING_MESSAGE);

      expect(renderedTextOf(root)).toContain(SCRIPT_BEARING_MESSAGE);
    });

    it('creates no script element from a script-bearing value', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, SCRIPT_BEARING_MESSAGE);

      expect(root.querySelector('script')).toBeNull();
    });

    it('adds no element of any kind when wording carries markup', () => {
      const benign = createAsRootComponent();
      const benignElementCount = renderWith(benign, NOT_FOUND_MESSAGE).querySelectorAll('*').length;

      const hostile = createAsRootComponent();
      const hostileElementCount = renderWith(hostile, SCRIPT_BEARING_MESSAGE).querySelectorAll('*')
        .length;

      expect(hostileElementCount).toBe(benignElementCount);
    });
  });

  describe('content projection', () => {
    it('renders the wording and offers no action affordance of its own', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      expect(renderedTextOf(root)).toContain(NOT_FOUND_MESSAGE);
      expect(root.querySelector(ACTION_SELECTORS)).toBeNull();
    });

    it('projects a caller-supplied action into its own element subtree', () => {
      const hostFixture = TestBed.createComponent(EmptyStateHostComponent);
      hostFixture.detectChanges();

      const emptyState = requireElement(rootElementOf(hostFixture), 'app-empty-state');
      const action = requireElement(emptyState, 'button');

      expect(renderedTextOf(action).trim()).toBe(PROJECTED_ACTION_LABEL);
      expect(action.getAttribute('type')).toBe('button');
    });

    it('renders bound wording and projected content together', () => {
      const hostFixture = TestBed.createComponent(EmptyStateHostComponent);
      hostFixture.detectChanges();

      const emptyState = requireElement(rootElementOf(hostFixture), 'app-empty-state');
      const text = renderedTextOf(emptyState);

      expect(text).toContain(LIST_EMPTY_MESSAGE);
      expect(text).toContain(PROJECTED_ACTION_LABEL);
    });
  });

  describe('blank wording falls back to the documented default', () => {
    it('renders the documented default wording when nothing is bound at all', () => {
      const fixture = createAsRootComponent();
      fixture.detectChanges();

      const root = rootElementOf(fixture);

      expect(fixture.componentInstance.message).toBe(DEFAULT_MESSAGE);
      expect(renderedTextOf(requireElement(root, `.${MESSAGE_CLASS}`))).toBe(DEFAULT_MESSAGE);
    });

    it('treats empty, whitespace, absent and null values as a request for the default', () => {
      const blankValues: readonly unknown[] = ['', '   ', '\t\n ', undefined, null];

      for (const blankValue of blankValues) {
        const fixture = createAsRootComponent();
        const root = renderWith(fixture, blankValue);

        expect(fixture.componentInstance.message)
          .withContext(`blank value ${JSON.stringify(blankValue)} must select the default wording`)
          .toBe(DEFAULT_MESSAGE);
        expect(renderedTextOf(requireElement(root, `.${MESSAGE_CLASS}`)))
          .withContext(`blank value ${JSON.stringify(blankValue)} must render the default wording`)
          .toBe(DEFAULT_MESSAGE);

        fixture.destroy();
      }
    });

    it('does not substitute the default for wording that merely surrounds itself with spaces', () => {
      const padded = `  ${NOT_FOUND_MESSAGE}  `;
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, padded);

      expect(fixture.componentInstance.message).toBe(padded);
      expect(fixture.componentInstance.message).not.toBe(DEFAULT_MESSAGE);

      expect(renderedTextOf(requireElement(root, `.${MESSAGE_CLASS}`))).toBe(padded);
      expect(renderedTextOf(requireElement(root, `.${MESSAGE_CLASS}`)).trim()).toBe(
        NOT_FOUND_MESSAGE,
      );
    });

    it('never renders a placeholder in place of absent wording', () => {
      const fixture = createAsRootComponent();

      const rendered = renderedTextOf(renderWith(fixture, undefined));

      expect(rendered).not.toContain('undefined');
      expect(rendered).not.toContain('null');
    });
  });

  // THE STATIC SEMANTICS OF THIS COMPONENT ARE ASSERTED HERE. The heading text, the heading level, the
  // glyph's removal from the accessibility tree and the placement of the bound wording inside its own
  // paragraph are all fixed by the template rather than by an input, so no consumer can vary them and no
  // other expectation in this file touches them.
  describe('shipped static semantics', () => {
    it('renders exactly one heading, at level two, carrying the shipped caption', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      const headings = root.querySelectorAll('h1, h2, h3, h4, h5, h6');

      // EXACTLY ONE, AT LEVEL TWO. The level is a real decision, not an accident: the page title owns level
      // one inside the main region, so this caption sits beneath it.
      expect(headings.length).toBe(1);

      const heading = requireElement(root, `.${TITLE_CLASS}`);

      expect(heading.tagName).toBe('H2');
      expect(renderedTextOf(heading)).toBe(STATIC_TITLE);

      // And it is genuinely a heading element rather than a styled div, which is
      // what makes it reachable by heading navigation.
      expect(root.querySelectorAll('h2').length).toBe(1);
    });

    it('hides the decorative glyph from assistive technology', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      const glyph = requireElement(root, `.${GLYPH_CLASS}`);

      // The glyph restates what the caption already says, so exposing it would make a screen reader
      // announce the same idea twice - and the artwork is an inline vector with no text alternative to
      // announce in the first place.
      expect(glyph.getAttribute('aria-hidden')).toBe('true');

      const vector = glyph.querySelector('svg');
      expect(vector).not.toBeNull();
      if (vector === null) {
        return;
      }

      expect(glyph.contains(vector)).toBeTrue();
      expect(renderedTextOf(glyph)).toBe('');

      // Exactly one hidden element, so nothing else is being quietly removed from the accessibility tree -
      // the caption and the wording must both remain exposed.
      expect(root.querySelectorAll('[aria-hidden="true"]').length).toBe(1);
      expect(glyph.contains(requireElement(root, `.${MESSAGE_CLASS}`))).toBeFalse();
      expect(glyph.contains(requireElement(root, `.${TITLE_CLASS}`))).toBeFalse();
    });

    it('places the bound wording in its own paragraph, distinct from the caption', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      const paragraph = requireElement(root, `.${MESSAGE_CLASS}`);

      // A paragraph rather than a second heading: supporting text must stay out of the document outline.
      // Asserted exactly rather than with a containment check, so stray wording concatenated into the same
      // element fails.
      expect(paragraph.tagName).toBe('P');
      expect(renderedTextOf(paragraph)).toBe(NOT_FOUND_MESSAGE);
      expect(root.querySelectorAll(`.${MESSAGE_CLASS}`).length).toBe(1);

      const heading = requireElement(root, `.${TITLE_CLASS}`);

      expect(renderedTextOf(heading)).toBe(STATIC_TITLE);
      expect(renderedTextOf(heading)).not.toContain(NOT_FOUND_MESSAGE);
      expect(heading.contains(paragraph)).toBeFalse();
      expect(paragraph.contains(heading)).toBeFalse();
    });

    it('keeps the shipped caption fixed while the wording varies', () => {
      const fixture = createAsRootComponent();

      renderWith(fixture, NOT_FOUND_MESSAGE);
      const firstCaption = renderedTextOf(requireElement(rootElementOf(fixture), `.${TITLE_CLASS}`));

      const root = renderWith(fixture, LIST_EMPTY_MESSAGE);

      expect(firstCaption).toBe(STATIC_TITLE);
      expect(renderedTextOf(requireElement(root, `.${TITLE_CLASS}`))).toBe(STATIC_TITLE);
      expect(renderedTextOf(requireElement(root, `.${MESSAGE_CLASS}`))).toBe(LIST_EMPTY_MESSAGE);
    });
  });

  describe('rendered markup guarantees', () => {
    it('renders its own block class as the styling root and carries the wording within it', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      const block = requireElement(root, `.${BLOCK_CLASS}`);

      expect(renderedTextOf(block)).toContain(NOT_FOUND_MESSAGE);
    });

    it('emits no class outside its own styling vocabulary', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      const foreignTokens = classTokensWithin(root).filter(
        (token: string): boolean => belongsToBlock(token) === false,
      );

      expect(foreignTokens).toEqual([]);
    });

    it('emits no document landmark', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      expect(root.querySelector(LANDMARK_SELECTORS)).toBeNull();
    });

    it('emits no live region', () => {
      const fixture = createAsRootComponent();
      const root = renderWith(fixture, NOT_FOUND_MESSAGE);

      expect(root.querySelector(LIVE_REGION_SELECTORS)).toBeNull();
    });
  });

  // This group exists because `message` is intended to be reachable from the catch-all route, making its
  // value external input rather than a value a template author chose. Two facts make a runtime guard
  // mandatory rather than defensive decoration.
  describe('bounded and normalised external input', () => {
    const MAX_RETAINED_LENGTH = 1024;
    const FIRST_DUPLICATE = 'Access denied';
    const SECOND_DUPLICATE = 'Your session has expired';
    const JOINED_DUPLICATES = `${FIRST_DUPLICATE},${SECOND_DUPLICATE}`;

    /** Reads the documented default wording from a freshly created instance. */
    const defaultWording = (): string => {
      const reference = createAsRootComponent();
      reference.detectChanges();

      return reference.componentInstance.message;
    };

    it('proves the premise: a repeated query key parses to an array', () => {
      const serializer = new DefaultUrlSerializer();

      const repeated: unknown = serializer.parse('/not-found?message=first&message=second')
        .queryParams['message'];
      const single: unknown = serializer.parse('/not-found?message=first').queryParams['message'];

      expect(repeated).toEqual(['first', 'second']);
      expect(single).toBe('first');
    });

    it('folds a repeated route parameter into one comma-delimited sentence', () => {
      const fixture = createAsRootComponent();

      const root = renderWith(fixture, [FIRST_DUPLICATE, SECOND_DUPLICATE]);

      // A bare comma with no padding is the legacy behaviour: the legacy analogue read its query value from
      // a `NameValueCollection`, whose `Get` folds duplicates exactly this way. No supplied value is
      // discarded, which a first-value-wins policy would do.
      expect(fixture.componentInstance.message).toBe(JOINED_DUPLICATES);
      expect(renderedTextOf(root)).toContain(JOINED_DUPLICATES);
    });

    it('stores a single-element array as that element alone', () => {
      const fixture = createAsRootComponent();

      renderWith(fixture, [FIRST_DUPLICATE]);

      expect(fixture.componentInstance.message).toBe(FIRST_DUPLICATE);
    });

    it('keeps only the string elements of a mixed array', () => {
      const fixture = createAsRootComponent();

      // Filtering before joining is what makes the fold total rather than merely usually correct:
      // `Array.prototype.join` coerces its elements, and coercion throws on a symbol and on any object
      // whose `toString` throws.
      renderWith(fixture, [FIRST_DUPLICATE, 7, null, SECOND_DUPLICATE]);

      expect(fixture.componentInstance.message).toBe(JOINED_DUPLICATES);
    });

    it('treats an array carrying no usable wording as a request for the default', () => {
      const expected = defaultWording();
      const emptyArray: readonly unknown[] = [];
      const blankBearingArrays: readonly unknown[] = [emptyArray, [''], ['   '], [1, 2]];

      blankBearingArrays.forEach((payload: unknown, index: number): void => {
        const fixture = createAsRootComponent();
        renderWith(fixture, payload);

        expect(fixture.componentInstance.message)
          .withContext(`array payload at index ${index} must select the default wording`)
          .toBe(expected);
      });
    });

    it('falls back to the default for every payload shape the declared type forbids', () => {
      const expected = defaultWording();
      const hostilePayloads: readonly unknown[] = [
        0,
        1,
        -1,
        Number.NaN,
        true,
        false,
        {},
        { message: FIRST_DUPLICATE },
        new Date(0),
        (): string => FIRST_DUPLICATE,
        Symbol('hostile'),
        {
          toString: (): string => {
            throw new Error('hostile toString');
          },
        },
      ];

      hostilePayloads.forEach((payload: unknown, index: number): void => {
        const fixture = createAsRootComponent();

        expect((): void => {
          renderWith(fixture, payload);
        })
          .withContext(`payload at index ${index} must not throw`)
          .not.toThrow();

        expect(fixture.componentInstance.message)
          .withContext(`payload at index ${index} must select the default wording`)
          .toBe(expected);
      });
    });

    it('folds blank duplicate values verbatim rather than discarding them', () => {
      const fixture = createAsRootComponent();
      const blankFirst = '';
      const blankSecond = '  ';

      renderWith(fixture, [blankFirst, blankSecond]);

      // Deliberately NOT the default wording. The legacy fold is total: a `NameValueCollection` joins every
      // duplicate value, blank ones included, so `?message=&message=%20%20` produced a lone separator on
      // the legacy page too.
      expect(fixture.componentInstance.message).toBe(`${blankFirst},${blankSecond}`);
    });

    it('retains a message that sits exactly on the bound unchanged', () => {
      const fixture = createAsRootComponent();
      const atBound = 'a'.repeat(MAX_RETAINED_LENGTH);

      renderWith(fixture, atBound);

      expect(fixture.componentInstance.message).toBe(atBound);
      expect(fixture.componentInstance.message.length).toBe(MAX_RETAINED_LENGTH);
    });

    it('truncates an oversized message to the bound and keeps its opening', () => {
      const fixture = createAsRootComponent();
      const oversized = `${FIRST_DUPLICATE}${'b'.repeat(MAX_RETAINED_LENGTH * 4)}`;

      const root = renderWith(fixture, oversized);

      expect(fixture.componentInstance.message.length).toBe(MAX_RETAINED_LENGTH);
      // Truncation keeps the beginning because that is where the explanation is.
      expect(fixture.componentInstance.message.startsWith(FIRST_DUPLICATE)).toBeTrue();
      expect(renderedTextOf(root)).toContain(FIRST_DUPLICATE);
    });

    it('bounds a folded array as well as a supplied string', () => {
      const fixture = createAsRootComponent();
      const half = 'c'.repeat(MAX_RETAINED_LENGTH);

      renderWith(fixture, [half, half]);

      expect(fixture.componentInstance.message.length).toBe(MAX_RETAINED_LENGTH);
    });

    it('never truncates in the middle of a surrogate pair', () => {
      // One astral code point occupies two UTF-16 code units, so whether the cut lands inside a pair
      // depends on the alignment of the text before it. Both alignments are exercised, because only the odd
      // one reaches the step-back branch.
      const astral = String.fromCodePoint(0x1f600);
      const endsOnHighSurrogate = (text: string): boolean => {
        const lastUnit = text.charCodeAt(text.length - 1);

        return lastUnit >= 0xd800 && lastUnit <= 0xdbff;
      };

      // Even alignment: every pair straddles an even-then-odd index, so the cut
      // falls after a complete pair and nothing is stepped back.
      const evenFixture = createAsRootComponent();
      renderWith(evenFixture, astral.repeat(MAX_RETAINED_LENGTH));
      const evenStored = evenFixture.componentInstance.message;

      expect(evenStored.length).toBe(MAX_RETAINED_LENGTH);
      expect(endsOnHighSurrogate(evenStored)).toBeFalse();
      expect(evenStored).toBe(astral.repeat(MAX_RETAINED_LENGTH / 2));

      // Odd alignment: a single leading unit shifts every pair by one, so the naive cut lands on a high
      // surrogate. Retaining it would render as the U+FFFD replacement character, so one unit is dropped
      // and the bound is undershot by one rather than met exactly.
      const oddFixture = createAsRootComponent();
      renderWith(oddFixture, `x${astral.repeat(MAX_RETAINED_LENGTH)}`);
      const oddStored = oddFixture.componentInstance.message;

      expect(oddStored.length).toBe(MAX_RETAINED_LENGTH - 1);
      expect(endsOnHighSurrogate(oddStored)).toBeFalse();
      expect(oddStored).toBe(`x${astral.repeat((MAX_RETAINED_LENGTH - 2) / 2)}`);
    });

    it('still selects the default when an oversized payload is whitespace only', () => {
      const fixture = createAsRootComponent();

      // Bounding runs before the blank test, so this bounds to whitespace and is then recognised as blank.
      // Testing for blankness first would store an unusable message of spaces.
      renderWith(fixture, ' '.repeat(MAX_RETAINED_LENGTH * 3));

      expect(fixture.componentInstance.message).toBe(defaultWording());
    });
  });
});
