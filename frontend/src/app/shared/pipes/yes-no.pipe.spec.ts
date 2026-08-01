import { YesNoPipe } from './yes-no.pipe';

describe('YesNoPipe', () => {
  let pipe: YesNoPipe;

  beforeEach(() => {
    pipe = new YesNoPipe();
  });

  it('renders the affirmative text for true', () => {
    expect(pipe.transform(true)).toBe('Yes');

    expect(pipe.transform(true).trim().length).toBeGreaterThan(0);
  });

  // The one case here whose loss would be a behavioural regression rather than a
  // cosmetic one: `false` is data, not an absent value. The legacy cell drew a
  // glyph in both states and was never blank, so emitting empty output for
  // `false` would turn a definite "no" into an apparent "no data". Emptiness is
  // asserted positively, and more than once, because a bare equality check would
  // still pass if the expected word itself were weakened to blank text.
  it('renders the negative text for false, and never empty output', () => {
    const rendered = pipe.transform(false);

    expect(rendered).toBe('No');

    expect(rendered.length).toBeGreaterThan(0);
    expect(rendered.trim().length).toBeGreaterThan(0);
    expect(rendered).not.toBe('');
  });

  it('maps the two states onto distinct text', () => {
    expect(pipe.transform(true)).not.toBe(pipe.transform(false));
  });

  it('is pure: repeated calls agree and no state carries between them', () => {
    expect(pipe.transform(true)).toBe(pipe.transform(true));
    expect(pipe.transform(false)).toBe(pipe.transform(false));

    const first = pipe.transform(true);
    const second = pipe.transform(false);
    const third = pipe.transform(true);

    expect(first).toBe('Yes');
    expect(second).toBe('No');
    expect(third).toBe(first);

    const secondInstance = new YesNoPipe();

    expect(secondInstance.transform(true)).toBe(pipe.transform(true));
    expect(secondInstance.transform(false)).toBe(pipe.transform(false));
  });

  it('maps both members of its declared domain, exhaustively and distinctly', () => {
    // WHAT THIS TEST DOES NOT CLAIM, corrected on review. It previously asserted
    // that the pipe "branches on the boolean itself, never on truthiness", and
    // that claim was not provable by these expectations. The declared parameter
    // type is `boolean`, so its domain has exactly two members, and for both of
    // them `value === true` and a plain truthiness test return the same wording.
    // The two formulations are indistinguishable to any test that respects the
    // declared type - an equivalent mutant, which by definition no assertion can
    // kill - so claiming a distinction here overstated what the code below shows.
    //
    // The strict comparison in the implementation is retained as defence in depth
    // and is documented there, not here. Proving it observable would require
    // accepting a runtime-invalid input, and non-booleans are deliberately NOT
    // supported: the signature is `transform(value: boolean): string`, so
    // `pipe.transform('true')` fails type checking, and that failure is the
    // intended mechanism. The legacy `roles.ascx` L68-L69 and L74-L75 string
    // comparison is not reproduced, and no cast and no compiler suppression is
    // used to smuggle a string in here - either device would manufacture a
    // scenario the application cannot produce and would weaken the type guarantee
    // that actually protects this pipe. The workspace compiles with `strict`
    // enabled, and `strictTemplates` extends the same check to every binding that
    // reaches this pipe from a template.
    //
    // WHAT IT DOES CLAIM, asserted below: the domain is covered exhaustively, each
    // member maps to one specific wording, and the two wordings are different. A
    // regression that collapsed the branch - returning one wording for both
    // inputs, or swapping them - fails here.
    expect(pipe.transform(false)).toBe('No');
    expect(pipe.transform(false)).not.toBe('Yes');

    expect(pipe.transform(true)).toBe('Yes');
    expect(pipe.transform(true)).not.toBe('No');

    // The mapping is injective across the whole domain: no two inputs share a
    // rendering, so neither wording can be reached by the wrong value.
    expect(pipe.transform(true)).not.toBe(pipe.transform(false));
  });
});
