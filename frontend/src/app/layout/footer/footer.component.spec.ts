/**
 * Specification for `FooterComponent` — the administration shell's single
 * `contentinfo` band.
 *
 * Why this file matters beyond the assertions it makes: `tsconfig.app.json`
 * declares `files: ["src/main.ts"]` together with an empty `types` list, so the
 * production build type-checks by import graph alone and never sees a spec. The
 * spec configuration is glob-included with the Jasmine types and carries no
 * `files` array, which makes it the second, independent route into a gated
 * compile. This file is therefore a real safety net rather than ceremony, and
 * the browser test run is the only gate that proves it.
 *
 * Three contracts are proven here, none of which is proven anywhere else in the
 * workspace:
 *
 * 1. Landmark ownership. The shell allocates exactly one semantic landmark per
 *    layout folder — the header band owns `<header>`, the sidebar owns `<nav>`,
 *    the shell owns `<main>` — and this band owns `<footer>` and nothing else.
 * 2. Copyright wording. The rendered line reproduces the measured legacy
 *    resource value.
 * 3. Year shape. Whatever year is rendered matches a four-digit pattern. It is
 *    never compared against a hard-coded value, so the suite stays correct
 *    across calendar boundaries without ever being rewritten.
 *
 * Measured legacy provenance — the DotNetNuke `Copyright` skin object:
 * - `Website/admin/Skins/App_LocalResources/Copyright.ascx.resx` L42-L43 holds
 *   that file's ONLY resource entry, verified by counting exactly one `<data>`
 *   element in the whole file: `Copyright.Text` = `Copyright (c) {0} {1}`. The
 *   parenthesised letter is plain ASCII in the source, which is why the
 *   assertions below match that exact byte sequence and deliberately do not
 *   match the typographic glyph — matching the glyph would let a silent wording
 *   divergence through unnoticed.
 * - `Website/admin/Skins/Copyright.ascx.vb` L85-L89 supplied the substitutions:
 *   `PortalSettings.FooterText` when non-empty, otherwise the resource string
 *   formatted with `Year(Now())` as `{0}` and `PortalSettings.PortalName` as
 *   `{1}`.
 * - `Website/Default.aspx.vb` L197-L199 independently corroborates the same
 *   wording and the same two substitutions on the separate code path that fed
 *   the copyright meta tag.
 *
 * Negative inventory — three legacy footer affordances are deliberately absent,
 * and the assertions below are the regression guards that keep them absent:
 * - The root-navigation strip. `Website/Portals/_default/Skins/MinimalExtropy/
 *   index.ascx` L109 emitted a root-level links control, registered at L10,
 *   inside the footer band opened at L108. It merely duplicated the primary
 *   navigation, which now lives once in the sidebar — the shell's sole
 *   navigation landmark. That is precisely what the `<nav>` count assertion
 *   guards against re-introducing.
 * - The Privacy Statement and Terms Of Use links, emitted by the same skin at
 *   L114-L116. Their wording is measured — `Privacy.ascx.resx` yields
 *   `Privacy Statement` and `Terms.ascx.resx` yields `Terms Of Use` — but the
 *   target route table declares neither a privacy nor a terms route, so either
 *   link would dangle.
 * - Any image. The notice is text, never a graphic.
 *
 * Escaping. `PortalSettings.FooterText` was an admin-editable, database-stored
 * fragment that the legacy label emitted as live markup, and the in-scope legacy
 * resource files genuinely do carry HTML-bearing values. The paired template
 * must therefore interpolate plain text and must never bind markup, so one spec
 * below asserts that no script element ever reaches the rendered band.
 *
 * Determinism. This suite reads no clock, schedules no timer and installs no
 * fake time source. The component resolves its year once, at construction, from
 * the platform; a spec that recomputed that same value would merely be asserting
 * the implementation against itself and would prove nothing. Pattern matching is
 * therefore both the correct and the sufficient assertion.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FooterComponent } from './footer.component';

/**
 * The literal, ASCII-only opening of the copyright notice, byte-exact with the
 * measured resource value. Asserted as a substring so the surrounding markup
 * structure stays free to evolve without breaking the wording contract.
 */
const COPYRIGHT_PREFIX = 'Copyright (c)';

/**
 * The product designation substituted for the legacy portal-name argument. It is
 * grounded in the repository's own committed catalogue and published
 * documentation rather than invented.
 */
const PRODUCT_DESIGNATION = 'DotNetNuke';

/**
 * Matches a four-digit run anywhere inside a longer string. Used against
 * rendered text, which carries the wording around the year.
 */
const FOUR_DIGIT_YEAR_ANYWHERE = /\b\d{4}\b/;

/**
 * Matches a string that is exactly four digits end to end. Used against the
 * component's own year member, which carries nothing else.
 */
const FOUR_DIGIT_YEAR_EXACT = /^\d{4}$/;

/**
 * Every semantic landmark this band must NOT emit. Each one is owned by a
 * different layout folder, so a hit here means two components are competing for
 * the same landmark and assistive technology would report a duplicate.
 */
const FOREIGN_LANDMARKS: readonly string[] = ['header', 'nav', 'main', 'aside'];

/**
 * Lower-cased wording of the omitted legacy skin objects. Their absence is
 * asserted against the band's lower-cased text so the check is insensitive to
 * how the wording might be capitalised.
 */
const OMITTED_LEGACY_LABELS: readonly string[] = ['privacy', 'terms'];

describe('FooterComponent', () => {
  let fixture: ComponentFixture<FooterComponent>;
  let component: FooterComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // The component is standalone, so it is supplied as an import. No provider
      // is registered anywhere in this suite, deliberately: the class has no
      // constructor and injects nothing, so a provider here would be dead weight
      // that misrepresents its dependency surface.
      imports: [FooterComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(FooterComponent);
    component = fixture.componentInstance;

    // Change detection is driven explicitly. The component opts into the on-push
    // strategy, and automatic detection would obscure exactly when the template
    // is evaluated.
    fixture.detectChanges();
  });

  /** The fixture host element, cast in exactly one place for the whole suite. */
  const hostElement = (): HTMLElement => fixture.nativeElement as HTMLElement;

  /** The rendered band, or `null` when the template emitted none. */
  const footerElement = (): HTMLElement | null => hostElement().querySelector('footer');

  /**
   * The band's text, with a missing band or absent text normalised to the empty
   * string so that string matchers always receive a `string`. This coalescing is
   * the narrowing strategy used throughout, in place of a non-null assertion.
   */
  const footerText = (): string => footerElement()?.textContent ?? '';

  it('creates the component', () => {
    expect(component).toBeTruthy();
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

  it('renders the copyright line with the measured legacy wording', () => {
    expect(footerElement()).not.toBeNull();
    expect(footerText()).toContain(COPYRIGHT_PREFIX);
    expect(footerText()).toContain(PRODUCT_DESIGNATION);
  });

  it('renders a four-digit year', () => {
    expect(footerElement()).not.toBeNull();
    expect(footerText()).toMatch(FOUR_DIGIT_YEAR_ANYWHERE);
  });

  it('exposes the year as exactly four digits', () => {
    expect(String(component.currentYear)).toMatch(FOUR_DIGIT_YEAR_EXACT);
  });

  it('exposes a precomposed copyright line consistent with its own year', () => {
    expect(component.copyrightText).toContain(COPYRIGHT_PREFIX);
    expect(component.copyrightText).toContain(PRODUCT_DESIGNATION);
    expect(component.copyrightText).toMatch(FOUR_DIGIT_YEAR_ANYWHERE);
    expect(component.copyrightText).toContain(String(component.currentYear));
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
