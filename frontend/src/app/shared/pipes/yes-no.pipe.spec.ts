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
    expect(pipe.transform(false)).toBe('No');
    expect(pipe.transform(false)).not.toBe('Yes');

    expect(pipe.transform(true)).toBe('Yes');
    expect(pipe.transform(true)).not.toBe('No');

    // The mapping is injective across the whole domain: no two inputs share a
    // rendering, so neither wording can be reached by the wrong value.
    expect(pipe.transform(true)).not.toBe(pipe.transform(false));
  });
});
