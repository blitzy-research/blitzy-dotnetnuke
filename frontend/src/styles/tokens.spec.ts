/**
 * THE CONTRAST OBLIGATIONS OF THE COLOUR VOCABULARY, ASSERTED AGAINST THE REAL STYLESHEET.
 *
 * ⚠ WHY THIS FILE EXISTS AT ALL. The danger and boundary values were, for a period, KNOWINGLY below the
 * contrast minima, and the stylesheet said so in its own comments while shipping them anyway - the comment
 * beside `--color-danger` recorded 4.00:1 and 3.45:1 and then declined to act, and a component specification
 * went further and pinned `rgb(255, 0, 0)` with a note explaining why a darker sibling had been refused. So
 * the values were not drifting unnoticed; they were documented, defended and wrong. A prose comment cannot
 * fail a build, and a specification that pins a literal without stating what the literal has to ACHIEVE will
 * happily preserve a failure forever. This file states the achievement.
 *
 * WHAT IT MEASURES, AND WHY IT MEASURES RATHER THAN COMPARES. `angular.json` loads `src/styles.scss` into the
 * Karma run, so `getComputedStyle(document.documentElement)` here reads the SAME custom properties the browser
 * resolves in production - not a copy of them in a fixture, and not a literal repeated from the stylesheet.
 * Every ratio below is computed from those resolved values with the WCAG 2.x relative-luminance formula, so a
 * later edit to any token is checked against the requirement instead of against a second copy of itself.
 *
 * THE TWO THRESHOLDS, AND WHICH TOKEN OWES WHICH.
 *
 *   * 4.5:1 - WCAG 1.4.3, normal-size text. `--color-danger` owes this, because 32 of its 37 consuming
 *     declarations set `color`: validation messages, required markers, destructive row commands, negative
 *     fees, expired qualifiers, refusal prose.
 *   * 3:1 - WCAG 1.4.11, the visual information needed to identify a user-interface component and its state.
 *     `--color-border-control` owes this, because it draws the edges of fieldsets, disabled and read-only
 *     controls and opened help panels. Purely decorative rules are exempt, which is exactly why
 *     `--color-border` and `--color-border-strong` are still permitted to sit at their faint legacy values
 *     and are asserted below to have STAYED there.
 *
 * WHICH BACKGROUNDS EACH TOKEN IS TESTED AGAINST IS PART OF THE REQUIREMENT, NOT AN APPROXIMATION OF IT. The
 * danger ink is tested on three surfaces including `--color-selected`, because the four listing screens each
 * paint a destructive row command in that ink ON that tint on hover - deliberately, each with a comment
 * explaining that the selected tint is the only hover perceptible on both row surfaces. The boundary token is
 * tested on two, because all five of its consumers sit on the page background or the secondary surface and
 * none of them on the selected tint.
 */

/** The 4.5:1 minimum WCAG 1.4.3 sets for normal-size text. */
const NORMAL_TEXT_MINIMUM = 4.5;

/** The 3:1 minimum WCAG 1.4.11 sets for the boundary of a user-interface component. */
const NON_TEXT_MINIMUM = 3;

/**
 * Reads one custom property from the document root, exactly as the browser resolved it.
 *
 * @param name The custom property name, including its leading double hyphen.
 * @returns The resolved value, trimmed.
 */
function token(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/**
 * Parses a colour written as `#RGB` or `#RRGGBB` into its three 8-bit channels.
 *
 * Only the hexadecimal forms are accepted, and deliberately so: every colour in the vocabulary is authored
 * as a hexadecimal literal, so a value arriving in any other notation means the token has been changed to
 * something this file cannot judge - and failing loudly is the correct answer to that, rather than silently
 * measuring a default.
 *
 * @param value A colour token's resolved value.
 * @returns The three channels, each 0-255.
 */
function channels(value: string): readonly [number, number, number] {
  const hex = value.replace('#', '');
  const expanded =
    hex.length === 3
      ? hex
          .split('')
          .map((digit) => `${digit}${digit}`)
          .join('')
      : hex;

  if (/^[0-9a-fA-F]{6}$/.test(expanded) === false) {
    throw new Error(`The colour token "${value}" is not a hexadecimal colour and cannot be measured.`);
  }

  return [
    Number.parseInt(expanded.slice(0, 2), 16),
    Number.parseInt(expanded.slice(2, 4), 16),
    Number.parseInt(expanded.slice(4, 6), 16),
  ];
}

/**
 * The WCAG 2.x relative luminance of a colour.
 *
 * @param value A colour token's resolved value.
 * @returns The luminance, 0 for black through 1 for white.
 */
function relativeLuminance(value: string): number {
  const linear = channels(value)
    .map((channel) => channel / 255)
    .map((channel) => (channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4));

  return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
}

/**
 * The WCAG 2.x contrast ratio between two colours, which is symmetric in its arguments.
 *
 * @param first One colour token's resolved value.
 * @param second The other colour token's resolved value.
 * @returns The ratio, 1 for identical colours through 21 for black on white.
 */
function contrastRatio(first: string, second: string): number {
  const a = relativeLuminance(first);
  const b = relativeLuminance(second);

  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

describe('the colour vocabulary', () => {
  it('resolves every colour token this file judges', () => {
    // A missing custom property resolves to the empty string rather than throwing, so without this case a
    // renamed token would make every ratio below unmeasurable and the suite would report the parse failure
    // instead of the rename.
    for (const name of [
      '--color-background',
      '--color-surface',
      '--color-selected',
      '--color-danger',
      '--color-border',
      '--color-border-strong',
      '--color-border-control',
    ]) {
      expect(token(name)).withContext(`token ${name}`).not.toBe('');
    }
  });

  describe('the danger ink', () => {
    // The three surfaces it is painted on, and all three are real: the page background, the grid's
    // alternating stripe, and the selected tint the four listing screens use for a destructive row command's
    // hover.
    const surfaces = ['--color-background', '--color-surface', '--color-selected'] as const;

    it('MEETS THE NORMAL-TEXT MINIMUM ON EVERY SURFACE IT IS PAINTED ON', () => {
      // ⚠ THE MEASURED FAILURE THIS CLOSES: 3.9985:1, 3.4463:1 and 2.6125:1 respectively, every one of them
      // short of 4.5:1 - and the third short of 3:1 as well, so the token did not even clear the non-text
      // threshold on the surface where it is hovered.
      for (const surface of surfaces) {
        const ratio = contrastRatio(token('--color-danger'), token(surface));

        expect(ratio)
          .withContext(`--color-danger on ${surface} measured ${ratio.toFixed(4)}:1`)
          .toBeGreaterThanOrEqual(NORMAL_TEXT_MINIMUM);
      }
    });

    it('keeps a margin above the minimum, so a surface adjustment cannot quietly tip it back under', () => {
      // Five per cent. A token that sits exactly on a threshold is one rounding away from failing it, and the
      // thing most likely to move is not this token but a background beneath it.
      for (const surface of surfaces) {
        expect(contrastRatio(token('--color-danger'), token(surface)))
          .withContext(`--color-danger on ${surface}`)
          .toBeGreaterThanOrEqual(NORMAL_TEXT_MINIMUM * 1.05);
      }
    });

    it('stays a PURE RED, so legacy continuity is kept through hue', () => {
      // The legacy value was #FF0000 and the legacy dark red beside it was #C00, both hue 0. A darker red is
      // a continuation of that palette; an orange, a maroon or a brown would be a different design decision
      // wearing an accessibility justification.
      const [red, green, blue] = channels(token('--color-danger'));

      expect(green).withContext('no green channel').toBe(0);
      expect(blue).withContext('no blue channel').toBe(0);
      expect(red).withContext('and still unmistakably red').toBeGreaterThan(0x80);
    });

    it('remains distinguishable from the brand ink beside it', () => {
      // Darkening the red far enough would make a destructive command read as an ordinary one. The two must
      // stay apart from each other as well as from their background.
      expect(contrastRatio(token('--color-danger'), token('--color-primary')))
        .withContext('danger against primary')
        .toBeGreaterThan(1.5);
    });
  });

  describe('the component-boundary token', () => {
    // The two fills its five consumers sit on. It is NOT tested against `--color-selected`, because no
    // fieldset, disabled control, read-only control or help panel is drawn on that tint - and asserting a
    // requirement a token does not owe would force it darker than the design needs.
    const fills = ['--color-background', '--color-surface'] as const;

    it('MEETS THE NON-TEXT MINIMUM ON BOTH FILLS ITS CONSUMERS SIT ON', () => {
      for (const fill of fills) {
        const ratio = contrastRatio(token('--color-border-control'), token(fill));

        expect(ratio)
          .withContext(`--color-border-control on ${fill} measured ${ratio.toFixed(4)}:1`)
          .toBeGreaterThanOrEqual(NON_TEXT_MINIMUM);
      }
    });

    it('keeps a margin above the minimum', () => {
      for (const fill of fills) {
        expect(contrastRatio(token('--color-border-control'), token(fill)))
          .withContext(`--color-border-control on ${fill}`)
          .toBeGreaterThanOrEqual(NON_TEXT_MINIMUM * 1.05);
      }
    });

    it('is a PURE NEUTRAL GREY, so the legacy neutral hue is preserved exactly', () => {
      // The legacy borders are #DCDCDC and #CCCCCC, both perfectly neutral. Only the weight moves.
      const [red, green, blue] = channels(token('--color-border-control'));

      expect(green).withContext('neutral: red equals green').toBe(red);
      expect(blue).withContext('neutral: red equals blue').toBe(red);
    });

    it('is DARKER than both legacy border values, which is the whole of the change', () => {
      const control = relativeLuminance(token('--color-border-control'));

      expect(control).toBeLessThan(relativeLuminance(token('--color-border')));
      expect(control).toBeLessThan(relativeLuminance(token('--color-border-strong')));
    });
  });

  describe('the two legacy border values', () => {
    it('ARE STILL THE MEASURED LEGACY VALUES, because decoration owes no contrast', () => {
      // ⚠ THIS IS THE OTHER HALF OF THE FIX, AND IT IS AS DELIBERATE AS THE FIRST. Darkening these two would
      // have made every decorative rule in the product heavier than the legacy portal ever drew it - the
      // grid's row rule, the empty-state panel edge, the spinner track, the notification panel edge - which
      // trades one fidelity failure for another. Splitting the boundary role out into its own token is what
      // lets the decoration stay exactly as measured. If a later edit darkens these, the visual regression is
      // silent and wide, so it is pinned here.
      expect(token('--color-border')).toBe('#DCDCDC');
      expect(token('--color-border-strong')).toBe('#CCCCCC');
    });

    it('are BELOW the non-text minimum, which is the fact that made a third token necessary', () => {
      // Stated as an assertion rather than left in a comment: it is the premise of the split, and if it ever
      // stopped being true the third token would be redundant and should be removed rather than kept.
      for (const decorative of ['--color-border', '--color-border-strong'] as const) {
        expect(contrastRatio(token(decorative), token('--color-background')))
          .withContext(`${decorative} on the page background`)
          .toBeLessThan(NON_TEXT_MINIMUM);
      }
    });
  });

  describe('the measuring apparatus itself', () => {
    // A contrast helper that always returned a large number would make every case above pass. These three
    // anchor it against values whose ratios are fixed by the specification.
    it('reports the known extremes correctly', () => {
      expect(contrastRatio('#000000', '#FFFFFF')).toBeCloseTo(21, 2);
      expect(contrastRatio('#FFFFFF', '#FFFFFF')).toBeCloseTo(1, 6);
      expect(contrastRatio('#767676', '#FFFFFF'))
        .withContext('the canonical lightest grey meeting 4.5:1 on white')
        .toBeCloseTo(4.5422, 3);
    });

    it('is symmetric in its arguments', () => {
      expect(contrastRatio('#003366', '#EEEEEE')).toBeCloseTo(contrastRatio('#EEEEEE', '#003366'), 9);
    });

    it('refuses a value it cannot measure rather than guessing at one', () => {
      expect(() => contrastRatio('rgb(0, 0, 0)', '#FFFFFF')).toThrowError(
        /not a hexadecimal colour/,
      );
    });
  });
});
