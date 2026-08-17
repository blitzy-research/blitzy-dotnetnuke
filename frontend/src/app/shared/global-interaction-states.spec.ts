/**
 * Specifications for the globally declared interaction-state rules in `styles/_reset.scss`.
 *
 * \u26a0 WHY A STYLESHEET IS INSPECTED RATHER THAN A COMPONENT DRIVEN. `:active` cannot be entered from a
 * specification at all - it needs a held pointer press, and a synthetic `mousedown` does not put a browser
 * into the state. The defect being closed is nonetheless a real, measured one: with a genuine trusted press
 * held open, the computed background at `mousedown` was `rgba(0, 0, 0, 0)`, byte-identical to rest, reached
 * 49% at 44ms and only landed on the declared `rgb(193, 210, 238)` at ~150ms - so a 50-100ms human click
 * never saw the confirmation at all. The declaration that fixes it is therefore asserted where it lives, and
 * the assertions are written so that removing it fails them.
 *
 * The test build loads `src/styles.scss`, which is what puts these rules in `document.styleSheets`.
 */

/** One flattened stylesheet rule: its selector, its declarations, and the media query guarding it. */
interface FlatRule {
  readonly selectorText: string;
  readonly style: CSSStyleDeclaration;
  readonly media: string | null;
}

/**
 * Every style rule reachable from the document, including those nested inside media queries.
 *
 * Nested rules are walked unconditionally rather than only when their query matches, so the specification
 * measures what the stylesheet DECLARES rather than what this particular browser happens to be resolving.
 *
 * @returns The flattened rules.
 */
function flattenedRules(): readonly FlatRule[] {
  const collected: FlatRule[] = [];

  const walk = (rules: CSSRuleList, media: string | null): void => {
    Array.from(rules).forEach((rule) => {
      if (rule instanceof CSSMediaRule) {
        walk(rule.cssRules, rule.conditionText);

        return;
      }

      if (rule instanceof CSSStyleRule) {
        collected.push({ selectorText: rule.selectorText, style: rule.style, media });
      }
    });
  };

  Array.from(document.styleSheets).forEach((sheet) => {
    try {
      walk(sheet.cssRules, null);
    } catch {
      // A stylesheet the document cannot read is not one this application authored.
      return;
    }
  });

  return collected;
}

describe('global interaction states', () => {
  let rules: readonly FlatRule[];

  beforeAll(() => {
    rules = flattenedRules();
  });

  /** The rules whose selector list carries the given compound. */
  function rulesFor(compound: string): readonly FlatRule[] {
    return rules.filter((rule) =>
      rule.selectorText.split(',').some((part) => part.trim() === compound),
    );
  }

  it('declares a state transition on the shared controls', () => {
    // The control for the specification below: if this reads zero the stylesheet under test was never loaded,
    // and the absence assertions further down would pass for the wrong reason.
    const resting = rulesFor('button').filter(
      (rule) => rule.style.getPropertyValue('transition-duration').trim().length > 0,
    );

    expect(resting.length).withContext('a resting transition is declared for buttons').toBeGreaterThan(0);
  });

  it('exempts the press itself from that transition, so the tint lands on the frame of the press', () => {
    const pressed = rulesFor('button:active');

    expect(pressed.length).withContext('the press is addressed at all').toBeGreaterThan(0);

    // \u26a0 THE DISCRIMINATING VALUE IS `0s`. Under the defect this rule does not exist and the compound
    // inherits `var(--duration-state)`, which measured 150ms; asserting merely that a rule exists would pass
    // under both. Only an instant duration puts the colour on screen while the button is still held.
    const instant = pressed.filter(
      (rule) => rule.style.getPropertyValue('transition-duration').trim() === '0s',
    );

    expect(instant.length).withContext('the press transition is instant').toBeGreaterThan(0);
  });

  it('keeps the exemption inside the reduced-motion guard, because there is no transition to exempt without one', () => {
    const pressed = rulesFor('button:active').filter(
      (rule) => rule.style.getPropertyValue('transition-duration').trim() === '0s',
    );

    expect(pressed.length).toBeGreaterThan(0);
    pressed.forEach((rule) => {
      expect(rule.media)
        .withContext('the press exemption is guarded by a motion query')
        .toMatch(/prefers-reduced-motion/);
    });
  });

  it('exempts every control the resting transition covers, not only buttons', () => {
    // A partial fix - buttons only - would leave a link and a summary disclosure fading exactly as measured.
    // Naming each compound is what discriminates the complete selector list from a convenient subset.
    ['a:active', 'input:active', 'select:active', 'textarea:active', 'summary:active'].forEach(
      (compound) => {
        const found = rulesFor(compound).filter(
          (rule) => rule.style.getPropertyValue('transition-duration').trim() === '0s',
        );

        expect(found.length).withContext(`${compound} is exempt`).toBeGreaterThan(0);
      },
    );
  });
});
