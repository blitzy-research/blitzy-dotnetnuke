import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FooterComponent } from './footer.component';

const COPYRIGHT_PREFIX = 'Copyright (c)';

const PRODUCT_DESIGNATION = 'DotNetNuke';

const FOUR_DIGIT_YEAR_ANYWHERE = /\b\d{4}\b/;

/**
 * The WHOLE rendered sentence, anchored at both ends, with the year captured.
 *
 * Anchoring is what makes this oracle mutation-sensitive. A substring check on the
 * opening words and the product name passes against any amount of extra text, any
 * reordering and any substituted year, so it cannot distinguish the correct band
 * from one whose year was hard-coded to a past value or whose wording gained a
 * stray word; the anchored form fails on all three. The single capture group is
 * what lets the rendered year be compared against the component's own year
 * without this specification reading a clock of its own.
 *
 * The shape reproduces the legacy copyright resource wording with its second
 * placeholder resolved to the product designation. The parenthesised letter is
 * escaped because it is a literal in that wording, not a group.
 */
const COPYRIGHT_SENTENCE = /^Copyright \(c\) (\d{4}) DotNetNuke$/;

/**
 * Matches a string that is exactly four digits end to end. Used against the
 * component's own year member, which carries nothing else.
 */
const FOUR_DIGIT_YEAR_EXACT = /^\d{4}$/;

const FOREIGN_LANDMARKS: readonly string[] = ['header', 'nav', 'main', 'aside'];

const OMITTED_LEGACY_LABELS: readonly string[] = ['privacy', 'terms'];

describe('FooterComponent', () => {
  let fixture: ComponentFixture<FooterComponent>;
  let component: FooterComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FooterComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(FooterComponent);
    component = fixture.componentInstance;

    fixture.detectChanges();
  });

  const hostElement = (): HTMLElement => fixture.nativeElement as HTMLElement;

  const footerElement = (): HTMLElement | null => hostElement().querySelector('footer');

  const footerText = (): string => footerElement()?.textContent ?? '';

  /**
   * The band's text with every run of whitespace collapsed to one space and the
   * ends trimmed.
   *
   * Normalisation is what makes an anchored, whole-sentence assertion possible:
   * the template is authored across several lines for readability, so the raw text
   * node carries the indentation around the interpolation. Collapsing it compares
   * the words the reader actually sees, and it deliberately does NOT discard
   * whitespace entirely - a missing space between the year and the product name
   * would still fail.
   */
  const normalisedFooterText = (): string => footerText().replace(/\s+/g, ' ').trim();

  it('renders the notice as a single paragraph and emits no other element', () => {
    const footer = footerElement();

    expect(footer).not.toBeNull();
    if (footer === null) {
      return;
    }

    // The band's whole structure, asserted as a structure rather than as a
    // truthiness check on the fixture. A wrapper `<div>`, a second paragraph, a
    // `<span>` around the year or a nested element inside the paragraph would each
    // fail here, and each would be a real change: the paired stylesheet addresses
    // both elements BY TYPE rather than by class, so it silently stops matching the
    // moment an intervening element appears, and the sibling `<p>` rule that zeroes
    // the user-agent margin would leave the band's intrinsic height wrong.
    const descendants = Array.from(footer.querySelectorAll('*'));

    expect(descendants.map((element: Element): string => element.tagName)).toEqual(['P']);
    expect(descendants[0].children.length).toBe(0);
  });

  it('emits exactly one footer contentinfo landmark', () => {
    expect(hostElement().querySelectorAll('footer').length).toBe(1);
  });

  it('emits no other semantic landmark', () => {
    for (const landmark of FOREIGN_LANDMARKS) {
      expect(hostElement().querySelectorAll(landmark).length)
        .withContext(`the footer band must not emit a <${landmark}> landmark`)
        .toBe(0);
    }
  });

  it('renders the exact copyright sentence and nothing besides', () => {
    expect(footerElement()).not.toBeNull();

    // ANCHORED, WHOLE-SENTENCE assertion: the wording, the word order, the spacing
    // and the absence of any extra text are all proven in one expectation, so a
    // reworded, reordered or padded band fails rather than passing on a substring.
    expect(normalisedFooterText()).toMatch(COPYRIGHT_SENTENCE);

    expect(normalisedFooterText()).toContain(COPYRIGHT_PREFIX);
    expect(normalisedFooterText()).toContain(PRODUCT_DESIGNATION);
  });

  it('renders the year the component resolved, not some other four-digit run', () => {
    expect(footerElement()).not.toBeNull();

    const match = COPYRIGHT_SENTENCE.exec(normalisedFooterText());

    expect(match).not.toBeNull();
    if (match === null) {
      return;
    }

    // THE POINT OF THIS TEST. A four-digit pattern alone is satisfied by a template
    // that hard-codes an old year, which is exactly the regression this band is
    // exposed to - the year is resolved once in TypeScript and interpolated in
    // markup, so the two could drift apart silently. Comparing the captured run
    // against the component's own member closes that gap without reading a clock:
    // whatever year the platform supplied, the rendered band has to agree with it.
    expect(match[1]).toBe(String(component.currentYear));
  });

  it('exposes the year as exactly four digits', () => {
    expect(String(component.currentYear)).toMatch(FOUR_DIGIT_YEAR_EXACT);
  });

  it('renders exactly the year it resolved once at construction', () => {
    // The determinism guard. The component reads the platform clock through one
    // named seam, exactly once per instance, and every other member derives from
    // that value - so the rendered year is compared against the component's own
    // member rather than against a freshly computed year. A second, independent
    // clock read introduced anywhere in the band could disagree with the first
    // across a New Year boundary; this expectation is what would catch it, and it
    // cannot itself become flaky because both sides come from the same single read.
    expect(footerText()).toContain(String(component.currentYear));
  });

  it('exposes a precomposed copyright line matching the rendered sentence exactly', () => {
    // The precomposed member is held to the SAME anchored sentence as the rendered
    // band, so the two published granularities - the whole line, and the year on
    // its own - cannot drift apart. The band interpolates the year and re-states
    // the wording in markup, so without this the member could keep the correct
    // wording while the template lost it, or vice versa.
    expect(component.copyrightText).toMatch(COPYRIGHT_SENTENCE);
    expect(component.copyrightText).toContain(COPYRIGHT_PREFIX);
    expect(component.copyrightText).toContain(PRODUCT_DESIGNATION);

    const match = COPYRIGHT_SENTENCE.exec(component.copyrightText);

    expect(match).not.toBeNull();
    if (match === null) {
      return;
    }

    expect(match[1]).toBe(String(component.currentYear));
  });

  it('composes the precomposed line from the same year the band renders', () => {
    // Cross-checks the two published members against each other through the
    // RENDERED output, which is the only place a divergence would actually be
    // visible to a user. Asserting the member against itself would prove nothing;
    // asserting it against the band proves the single-source-of-truth claim.
    expect(normalisedFooterText()).toBe(component.copyrightText);
    expect(normalisedFooterText()).toMatch(FOUR_DIGIT_YEAR_ANYWHERE);
  });

  it('does not reproduce the legacy footer links, privacy or terms affordances', () => {
    const host = hostElement();

    expect(host.querySelectorAll('a').length).toBe(0);
    expect(host.querySelectorAll('img').length).toBe(0);

    const renderedText = (host.textContent ?? '').toLowerCase();
    for (const label of OMITTED_LEGACY_LABELS) {
      expect(renderedText)
        .withContext(`the omitted legacy affordance "${label}" leaked into the band`)
        .not.toContain(label);
    }
  });

  it('renders the copyright as plain text rather than markup', () => {
    const footer = footerElement();

    expect(footer).not.toBeNull();
    expect(footer?.querySelectorAll('script').length ?? 0).toBe(0);
    expect(footerText().trim()).not.toBe('');
  });
});
