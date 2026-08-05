import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FooterComponent } from './footer.component';

const COPYRIGHT_PREFIX = 'Copyright (c)';

const PRODUCT_DESIGNATION = 'DotNetNuke';

const FOUR_DIGIT_YEAR_ANYWHERE = /\b\d{4}\b/;

/**
 * The WHOLE rendered sentence, anchored at both ends, with the year captured.
 *
 * Anchoring is what makes this oracle mutation-sensitive: a substring check passes
 * against extra text, reordering and a substituted year alike, whereas the anchored
 * form fails on all three. The capture group lets the rendered year be compared
 * against the component's own year without this spec reading a clock of its own.
 * The shape reproduces the legacy copyright resource wording with its second
 * placeholder resolved to the product designation.
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
   * The band's text with every run of whitespace collapsed to one space and the ends
   * trimmed, which is what makes an anchored whole-sentence assertion possible
   * against a template authored across several lines. It deliberately does NOT
   * discard whitespace entirely: a missing space between the year and the product
   * name still fails.
   */
  const normalisedFooterText = (): string => footerText().replace(/\s+/g, ' ').trim();

  it('renders the notice as a single paragraph and emits no other element', () => {
    const footer = footerElement();

    expect(footer).not.toBeNull();
    if (footer === null) {
      return;
    }

    // Asserted as a structure because the paired stylesheet addresses both elements
    // BY TYPE rather than by class: a wrapper `<div>`, a second paragraph or a
    // `<span>` around the year silently stops the rules matching, and the sibling
    // `<p>` rule that zeroes the user-agent margin would leave the band's intrinsic
    // height wrong.
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

    // Anchored, so a reworded, reordered or padded band fails rather than passing on
    // a substring.
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

    // A four-digit pattern alone is satisfied by a template that hard-codes an old
    // year, which is the regression this band is exposed to: the year is resolved
    // once in TypeScript and interpolated in markup, so the two could drift apart
    // silently. Comparing the captured run against the component's own member closes
    // that gap without reading a clock.
    expect(match[1]).toBe(String(component.currentYear));
  });

  it('exposes the year as exactly four digits', () => {
    expect(String(component.currentYear)).toMatch(FOUR_DIGIT_YEAR_EXACT);
  });

  it('renders exactly the year it resolved once at construction', () => {
    // The determinism guard. The component reads the platform clock through one named
    // seam, exactly once per instance, so a second independent clock read introduced
    // anywhere in the band could disagree with the first across a New Year boundary.
    // Comparing against the component's own member catches that and cannot itself
    // become flaky, because both sides come from the same single read.
    expect(footerText()).toContain(String(component.currentYear));
  });

  it('exposes a precomposed copyright line matching the rendered sentence exactly', () => {
    // Held to the SAME anchored sentence as the rendered band, so the whole line and
    // the year on its own cannot drift apart: the band re-states the wording in
    // markup, so the member could otherwise keep it while the template lost it.
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
