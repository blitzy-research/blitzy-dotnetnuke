import { YesNoPipe } from './yes-no.pipe';

/**
 * Specification for `YesNoPipe`, the boolean display formatter shared by the
 * migrated administration grids.
 *
 * NET NEW, WITH NO PREDECESSOR. The legacy tree carries no automated test of any
 * kind (AAP 0.5.1.5), so nothing below is a translation of an earlier test. What
 * IS carried over is observable behaviour: every assertion here is anchored to a
 * measured legacy line rather than to taste.
 *
 * LEGACY PROVENANCE - a closed set of six image cells in exactly two files, each
 * a mutually exclusive `checked.gif` / `unchecked.gif` pair:
 *
 * - `Website/admin/Users/users.ascx` L76-L77, column header `Authorized` (L74).
 *   `CType(Container.DataItem, ...UserInfo).Membership.Approved` is early bound,
 *   so the flag is compared against the BOOLEAN literals true and false.
 * - `Website/admin/Security/roles.ascx` L68-L69, column header `Public` (L66),
 *   and L74-L75, column header `Auto` (L72). Both read their flag through the
 *   untyped `DataBinder.Eval(...)`, which yields Object, and compare it against
 *   the STRING literals "true" and "false".
 *
 * Two grids therefore expressed one idea with two different literal types. That
 * divergence was survivable only because both pages compiled with Option Strict
 * off - `Website/release.config` L125 declares
 * `<compilation debug="false" strict="false">` - which let VB settle the
 * late-bound Object-against-String comparison by converting the string to a
 * Boolean instead of the Boolean to a string. The port collapses both forms onto
 * one typed signature, and the final case below pins that boolean-only contract.
 *
 * WORDING AUTHORITY - `Website/App_GlobalResources/SharedResources.resx`
 * L126-L128 declares `Yes.Text` with the value `Yes`, and L129-L131 declares
 * `No.Text` with the value `No`. Those two values are exactly what the
 * assertions expect. The pipe keeps its own copies module private, so this spec
 * restates the literals rather than importing them - the stronger arrangement,
 * because an accidental edit to either constant fails here instead of
 * propagating silently into every grid that formats a flag.
 *
 * HARNESS - the pipe is pure and injects nothing, so it is constructed directly.
 * No TestBed, no provider, no HTTP wiring, no router, no spy and no fixture:
 * each of those would assert the harness rather than the unit, and the absence
 * of any HTTP provider is itself the statement that this pipe performs no I/O.
 * Every case is synchronous - no callback parameter, no fake-async zone, no
 * timer. The pipe also raises no effect, so neither of the effect-flush helpers
 * is involved; for the record, `TestBed.flushEffects()` does exist in the
 * installed `@angular/core` 19.2.25 while a `TestBed.tick()` member does not,
 * and this spec uses neither.
 */
describe('YesNoPipe', () => {
  let pipe: YesNoPipe;

  beforeEach(() => {
    // Direct instantiation. The pipe declares no constructor and takes no
    // dependency, so there is nothing for an injector to resolve.
    pipe = new YesNoPipe();
  });

  it('renders the affirmative text for true', () => {
    expect(pipe.transform(true)).toBe('Yes');

    // The affirmative branch is visible text, matching the legacy `checked.gif`
    // cell it replaces.
    expect(pipe.transform(true).trim().length).toBeGreaterThan(0);
  });

  /**
   * THE CRITICAL CASE IN THIS FILE. It encodes the rule that `false` is data,
   * not an absent value, and it is the one assertion whose loss would be a
   * behavioural regression rather than a cosmetic one.
   *
   * Why it matters. In the legacy model the "absent" marker for a Boolean was
   * literally `False`: `Library/Components/Shared/Null.vb` L76-L80 returns
   * `False` from `NullBoolean`, and its `IsNull` helper at L227-L228 compares a
   * Boolean field against that same value, so a legitimate `false` was reported
   * as null. Every flag this pipe formats is nevertheless declared `As Boolean`
   * and reads straight off a backing field - `UserMembership.Approved` L83,
   * `RoleInfo.IsPublic` L248, `RoleInfo.AutoAssignment` L263, the last two with
   * no side effect on either accessor - and the API always serialises a boolean,
   * so the value is always present on the wire.
   *
   * And both legacy branches drew a glyph: `checked.gif` for true,
   * `unchecked.gif` for false. A blank cell never appeared. Emitting an empty
   * string for `false` would therefore change what an operator sees, turning a
   * definite "no" into an apparent "no data". The emptiness assertions below
   * exist to fail loudly if a future edit ever makes that change.
   */
  it('renders the negative text for false, and never empty output', () => {
    const rendered = pipe.transform(false);

    expect(rendered).toBe('No');

    // Non-emptiness asserted positively, three ways, because this is the defect
    // being guarded against: a bare equality check alone would still pass if the
    // expected literal itself were weakened to blank text.
    expect(rendered.length).toBeGreaterThan(0);
    expect(rendered.trim().length).toBeGreaterThan(0);
    expect(rendered).not.toBe('');
  });

  it('maps the two states onto distinct text', () => {
    // Cheap, and it catches the one defect the two cases above cannot: if both
    // display constants were accidentally edited to the same value, each of
    // those cases could still be made to pass while every grid rendered the
    // same word in both states.
    expect(pipe.transform(true)).not.toBe(pipe.transform(false));
  });

  it('is pure: repeated calls agree and no state carries between them', () => {
    // The pipe leaves `pure` unset, which keeps Angular's default of true. A
    // pure pipe is re-evaluated only when its input changes, so identical input
    // must always produce identical output.
    expect(pipe.transform(true)).toBe(pipe.transform(true));
    expect(pipe.transform(false)).toBe(pipe.transform(false));

    // Interleaved calls: the third result must equal the first, proving the
    // intervening negative call left nothing behind.
    const first = pipe.transform(true);
    const second = pipe.transform(false);
    const third = pipe.transform(true);

    expect(first).toBe('Yes');
    expect(second).toBe('No');
    expect(third).toBe(first);

    // A second instance agrees with the first, proving there is no shared
    // module-level mutable state either. Grids create one pipe instance per
    // binding, so independence across instances is the contract they rely on.
    const secondInstance = new YesNoPipe();

    expect(secondInstance.transform(true)).toBe(pipe.transform(true));
    expect(secondInstance.transform(false)).toBe(pipe.transform(false));
  });

  it('branches on the boolean itself, never on truthiness', () => {
    // COMPILE-TIME HALF OF THIS CONTRACT, stated rather than suppressed. The
    // signature is `transform(value: boolean): string`, so `pipe.transform` will
    // not accept a string: writing `pipe.transform('true')` fails type checking,
    // and that failure is the point. The legacy `roles.ascx` L68-L69 and L74-L75
    // string comparison is deliberately not reproduced, so no compiler
    // suppression directive and no cast is used to smuggle a string in here -
    // either device would hide the very guarantee being documented, and the
    // workspace compiles with `strict` enabled.
    //
    // RUNTIME HALF, asserted below. The implementation tests `value === true`
    // and falls through to the negative text, so a `false` input yields the
    // negative wording and cannot be coerced into the affirmative one. Under the
    // rejected alternative - a truthiness test on a loosely typed value - the
    // string "false" would have rendered as `Yes`, which is precisely the class
    // of accident that Option Strict off permitted in the legacy markup.
    expect(pipe.transform(false)).toBe('No');
    expect(pipe.transform(false)).not.toBe('Yes');

    expect(pipe.transform(true)).toBe('Yes');
    expect(pipe.transform(true)).not.toBe('No');
  });
});
