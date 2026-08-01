// Specification for `DateDisplayPipe`.
//
// PROVENANCE. The legacy DotNetNuke tree carries no automated tests of any kind,
// so this file has no predecessor to port: it is authored fresh from the legacy
// behaviour the pipe must reproduce. That behaviour was not invented either. A
// census of `Website/admin/{Portal,Users,Security,Modules,Tabs}` found exactly
// four private date formatters, and they disagree with one another:
//
//   1. `DisplayDate`      Website/admin/Users/Users.ascx.vb             L396-L408
//                         L400 `_Date = UserDate.ToString`  -> date AND time.
//                         L397 seeds the result with `Null.NullString` and the
//                         handler at L404-L406 swallows the failure, so a value
//                         that cannot be rendered comes out EMPTY, never as a
//                         platform error string.
//                         Bound at users.ascx L64 (CreatedDate) and L70 (LastLoginDate).
//   2. `FormatExpiryDate` Website/admin/Portal/Portals.ascx.vb          L250-L260
//                         L254 `.ToShortDateString`  -> date alone.
//                         L251 seeds `String.Empty`; handler at L256-L258.
//                         Bound at portals.ascx L51 (ExpiryDate).
//   3. `FormatDate`       Website/admin/Security/SecurityRoles.ascx.vb  L377-L383
//                         L379 `.ToShortDateString`  -> date alone.
//                         L381 returns the empty string; this one has no handler.
//                         Bound at securityroles.ascx L79 and L84.
//   4. `FormatExpiryDate` Website/admin/Users/MemberServices.ascx.vb    L172-L186
//                         L177 `.ToShortDateString`  -> date alone.
//
// Three of the four emit a date alone and one emits date plus time, which is the
// whole reason the pipe carries two modes: `'date'` is the majority default and
// `'datetime'` reproduces the single outlier. Both shapes are asserted below.
//
// DELIBERATE EXCLUSION. Formatter 4 is impure. MemberServices.ascx.vb L176 compares
// the value against `Date.Today` and L179 substitutes the localised word "Expired".
// That reads the clock, so it belongs to a screen holding an injected clock and not
// to a pure pipe. No assertion here expects it, and none may be added.
//
// SENTINEL RULE. `Null.NullDate` is `Date.MinValue`
// (Library/Components/Shared/Null.vb L66-L70), and `Null.IsNull` tests for it on
// the DATE PART ALONE: L222-L224 reads `IsNull = objDate.Date.Equals(NullDate.Date)`.
// Its sibling `Null.GetNull` spells out why at L183-L187, in the note
// "compare the Date part of the DateTime with the DatePart of the NullDate
// ( this avoids subtle time differences )". Every instant falling on 0001-01-01
// therefore counts as absent, not merely exact midnight, which is exactly what the
// second critical case below pins down.
//
// PERPETUAL VALUE. 9999-12-31 is NOT a sentinel. It is the one-off billing expiry
// assigned at Library/Components/Security/Roles/RoleController.vb L542,
// `Case "O" : ExpiryDate = New System.DateTime(9999, 12, 31)`, inside the
// `Select Case Frequency` block spanning L540-L547 that also holds L541,
// `Case "N" : ExpiryDate = Null.NullDate`. It is a genuine value meaning
// "perpetual" and must render like any other date.
// DISCREPANCY REPORTED rather than silently corrected: a circulating citation
// places this mapping at L543-L544. That is stale. L543 and L544 are the "D" and
// "W" DateAdd cases; the mapping sits at L542, verified in the working tree.
//
// HARNESS. `DateDisplayPipe` injects `LOCALE_ID`, so the pipe is resolved through a
// TestBed injector rather than constructed directly - that exercises the same
// construction path the templates use. The locale is pinned to a literal value
// because every expected string below is locale-sensitive, and a suite that
// inherited the ambient locale would be flaky by construction. Registration goes
// through `providers`, never through `declarations`: everything in this workspace
// is standalone.
//
// DETERMINISM. Every input is a hard-coded ISO-8601 literal, and nothing here reads
// the clock, so the suite cannot drift across a month boundary, a daylight-saving
// transition or a year change - and no clock stub is needed or wanted. The expected
// strings were measured against the installed framework formatter under the pinned
// locale and the UTC display zone the pipe fixes.

import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { DateDisplayPipe, type DateDisplayMode } from './date-display.pipe';

describe('DateDisplayPipe', () => {
  /**
   * The locale every expectation below is measured under. Pinned explicitly so the
   * asserted separators, field order and day-period wording are the same on every
   * machine.
   */
  const TEST_LOCALE = 'en-US';

  /**
   * A deliberately fictitious language tag, used to prove the pipe degrades rather
   * than throws when locale data is unavailable. A tag that cannot exist in CLDR is
   * chosen over a real-but-currently-unregistered one so the expectation cannot be
   * invalidated by a later locale registration elsewhere in the workspace.
   */
  const UNREGISTERED_LOCALE = 'zz-ZZ';

  /** Both modes named through the pipe's own exported union, not as loose strings. */
  const DATE_MODE: DateDisplayMode = 'date';
  const DATETIME_MODE: DateDisplayMode = 'datetime';

  /**
   * What "nothing to show" looks like. `Null.NullString` returns the empty string
   * (Library/Components/Shared/Null.vb L71-L75), so an empty cell is the faithful
   * legacy rendering of an absent value.
   */
  const EMPTY = '';

  // ---------------------------------------------------------------------------
  // Inputs. Every one is a fixed ISO-8601 literal.
  // ---------------------------------------------------------------------------

  /** The legacy null-date sentinel at exact midnight UTC. */
  const SENTINEL_AT_MIDNIGHT = '0001-01-01T00:00:00.000Z';

  /** The same sentinel calendar day carrying a non-zero time of day. */
  const SENTINEL_WITH_TIME_OF_DAY = '0001-01-01T13:45:30.000Z';

  /** The same sentinel calendar day written without a zone designator. */
  const SENTINEL_WITHOUT_ZONE = '0001-01-01T13:45:30';

  /**
   * An ordinary date. 9/10/2004 is the revision date stamped on the legacy
   * `DisplayDate` history block at Website/admin/Users/Users.ascx.vb L392.
   */
  const ORDINARY_INSTANT = '2004-09-10T00:00:00.000Z';

  /** The same ordinary date carrying an afternoon time of day. */
  const ORDINARY_INSTANT_WITH_TIME_OF_DAY = '2004-09-10T21:35:09.000Z';

  /** The perpetual billing expiry from RoleController.vb L542. */
  const PERPETUAL_INSTANT = '9999-12-31T00:00:00.000Z';

  /** A string no date parser can make sense of. */
  const UNPARSEABLE_INPUT = 'not-a-date';

  /** A value that is present on the wire but blank. */
  const BLANK_INPUT = '';

  /** A value that is blank once surrounding whitespace is discarded. */
  const WHITESPACE_INPUT = '   ';

  // ---------------------------------------------------------------------------
  // Expected renderings, measured under TEST_LOCALE and the pipe's UTC zone.
  // ---------------------------------------------------------------------------

  /** `.ToShortDateString` equivalent: `M/d/yyyy`. */
  const ORDINARY_AS_DATE = '9/10/2004';

  /** `.ToString` equivalent: `M/d/yyyy h:mm:ss tt`. */
  const ORDINARY_AS_DATE_TIME = '9/10/2004 12:00:00 AM';

  /** The afternoon instant rendered with its time of day. */
  const ORDINARY_WITH_TIME_OF_DAY_AS_DATE_TIME = '9/10/2004 9:35:09 PM';

  /** The perpetual value rendered as an ordinary date. */
  const PERPETUAL_AS_DATE = '12/31/9999';

  /** The perpetual value rendered with its time of day. */
  const PERPETUAL_AS_DATE_TIME = '12/31/9999 12:00:00 AM';

  /**
   * What an unguarded formatter would emit for the sentinel. Held as a literal so
   * the critical cases can assert against the exact wrong answer they exist to
   * prevent, rather than against a vague "not this shape".
   */
  const SENTINEL_IF_UNGUARDED = '1/1/0001';

  let pipe: DateDisplayPipe;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        { provide: LOCALE_ID, useValue: TEST_LOCALE },
        DateDisplayPipe,
      ],
    });

    pipe = TestBed.inject(DateDisplayPipe);
  });

  it('resolves through the injector under the pinned locale', () => {
    expect(pipe).toBeInstanceOf(DateDisplayPipe);
    expect(TestBed.inject(LOCALE_ID)).toBe(TEST_LOCALE);
  });

  it('agrees with itself when constructed directly on its documented fallback locale', () => {
    // The constructor parameter carries a default precisely so a test can build the
    // pipe without an injection context. Asserting that the two construction paths
    // produce identical output is what makes the fallback trustworthy: were the
    // default ever to drift away from the locale the application supplies, the same
    // wire value would render two different ways depending on how the pipe was
    // obtained, and that would show up here rather than on a screen.
    const directlyConstructed = new DateDisplayPipe();

    expect(directlyConstructed.transform(ORDINARY_INSTANT)).toBe(ORDINARY_AS_DATE);
    expect(directlyConstructed.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(
      ORDINARY_AS_DATE_TIME,
    );
    expect(directlyConstructed.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(directlyConstructed.transform(PERPETUAL_INSTANT)).toBe(PERPETUAL_AS_DATE);

    expect(directlyConstructed.transform(ORDINARY_INSTANT)).toBe(
      pipe.transform(ORDINARY_INSTANT),
    );
  });

  it('degrades to an empty cell instead of throwing when locale data is unavailable', () => {
    // The framework formatter raises NG0701 for a locale whose data has never been
    // registered, and the pipe catches it. That guard is not padding: an exception
    // escaping a template expression tears down the whole view, whereas all three
    // legacy formatters that wrapped their body in a handler returned their seeded
    // empty string instead - Users.ascx.vb L404-L406, Portals.ascx.vb L256-L258 and
    // MemberServices.ascx.vb L182-L184.
    //
    // The tag below is deliberately fictitious rather than merely absent today, so
    // this expectation cannot be invalidated by anyone registering a real locale
    // later. This is a tighter pin than the suite's own locale, not a looser one.
    const withUnavailableLocale = new DateDisplayPipe(UNREGISTERED_LOCALE);

    expect(() => withUnavailableLocale.transform(ORDINARY_INSTANT)).not.toThrow();
    expect(withUnavailableLocale.transform(ORDINARY_INSTANT)).toBe(EMPTY);
    expect(withUnavailableLocale.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(EMPTY);

    // An absent value still short-circuits ahead of the formatter, so the two ways
    // of arriving at an empty cell stay indistinguishable to a template.
    expect(withUnavailableLocale.transform(null)).toBe(EMPTY);
  });

  // ===========================================================================
  // CRITICAL CASE 1 of 3 - the minimum-value instant renders as an empty cell.
  //
  // `Null.NullDate` is `Date.MinValue` (Null.vb L66-L70). The sentinel is not
  // dropped in transit: the API writes a minimum-value date as a real ISO-8601
  // string rather than omitting the property or emitting null, so the client is the
  // component that has to recognise it. All three legacy formatters that guard the
  // value render it as an empty string - Users.ascx.vb L402, Portals.ascx.vb L251
  // and SecurityRoles.ascx.vb L381 - and this pipe must do the same, in BOTH modes.
  // ===========================================================================
  it('renders the minimum-value sentinel as an empty cell in both modes', () => {
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT, DATETIME_MODE)).toBe(EMPTY);
  });

  it('never leaks year one onto the screen in any shape', () => {
    // The exact wrong answers this guard exists to prevent. An unguarded formatter
    // emits SENTINEL_IF_UNGUARDED, a zero-padded formatter emits '01/01/0001', and
    // an unchecked parse emits the platform's invalid-date wording.
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe(SENTINEL_IF_UNGUARDED);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe('01/01/0001');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe('1/1/1');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toContain('0001');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT, DATETIME_MODE)).not.toContain('0001');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toContain('Invalid');

    // A real string, not a null or an undefined dressed up as one: templates
    // interpolate the result directly, so the type of the result matters.
    expect(typeof pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe('string');
    expect(typeof pipe.transform(SENTINEL_AT_MIDNIGHT, DATETIME_MODE)).toBe('string');
  });

  // ===========================================================================
  // CRITICAL CASE 2 of 3 - the sentinel is recognised on its DATE PART ALONE.
  //
  // This is the case a naive full-timestamp equality check fails. `Null.IsNull`
  // compares `objDate.Date.Equals(NullDate.Date)` at Null.vb L222-L224, and
  // `Null.GetNull` explains the choice at L183-L187: "compare the Date part of the
  // DateTime with the DatePart of the NullDate ( this avoids subtle time
  // differences )". Any instant on 0001-01-01 is absent, whatever the clock reads.
  // ===========================================================================
  it('renders a sentinel carrying a time of day as an empty cell in both modes', () => {
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATETIME_MODE)).toBe(EMPTY);

    // The time of day must not survive into the output either.
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATETIME_MODE)).not.toContain('1:45:30');
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY)).not.toContain('0001');

    // Both sentinel spellings collapse to the same empty cell, so the date-part
    // comparison is genuinely indifferent to the time of day.
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY)).toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));
  });

  it('recognises the sentinel when the wire value carries no zone designator', () => {
    // A serialiser that writes a date of unspecified kind omits the trailing 'Z',
    // and such a string is parsed as LOCAL time. Left alone that could shift the
    // instant off 0001-01-01 and leak a visible date onto the screen. The guard has
    // to hold under both spellings, so this case is asserted rather than assumed.
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE)).not.toContain('0001');
  });

  // ===========================================================================
  // The default mode reproduces the three-to-one legacy majority: a date alone.
  // ===========================================================================
  it('renders an ordinary date as a short date when no mode is supplied', () => {
    expect(pipe.transform(ORDINARY_INSTANT)).toBe(ORDINARY_AS_DATE);

    // Omitting the argument and passing 'date' explicitly must be the same call,
    // which is what makes the default safe to rely on in a template.
    expect(pipe.transform(ORDINARY_INSTANT, DATE_MODE)).toBe(ORDINARY_AS_DATE);
    expect(pipe.transform(ORDINARY_INSTANT, DATE_MODE)).toBe(pipe.transform(ORDINARY_INSTANT));

    // The date alone: no time of day, and the year is not abbreviated. A
    // two-digit-year token would have printed '9/10/04'.
    expect(pipe.transform(ORDINARY_INSTANT)).not.toContain(':');
    expect(pipe.transform(ORDINARY_INSTANT)).toContain('2004');
  });

  // ===========================================================================
  // The two-mode contract. Formatter 1 (Users.ascx.vb L400, `.ToString`) printed a
  // time of day; formatters 2, 3 and 4 (Portals.ascx.vb L254,
  // SecurityRoles.ascx.vb L379, MemberServices.ascx.vb L177, all
  // `.ToShortDateString`) did not. The modes keep both outputs reachable.
  // ===========================================================================
  it('renders a date and a time of day in datetime mode, differing from the default', () => {
    expect(pipe.transform(ORDINARY_INSTANT, DATETIME_MODE)).not.toBe(
      pipe.transform(ORDINARY_INSTANT),
    );
    expect(pipe.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(ORDINARY_AS_DATE_TIME);
    expect(pipe.transform(ORDINARY_INSTANT, DATETIME_MODE)).toContain('12:00:00 AM');
  });

  it('carries an afternoon time of day through datetime mode and discards it in date mode', () => {
    expect(pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE)).not.toBe(
      pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY),
    );
    expect(pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE)).toBe(
      ORDINARY_WITH_TIME_OF_DAY_AS_DATE_TIME,
    );
    expect(pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE)).toContain('PM');

    // The date half is identical either way, so the two modes differ in the time of
    // day alone and never in the calendar date they report.
    expect(pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY)).toBe(ORDINARY_AS_DATE);
  });

  // ===========================================================================
  // CRITICAL CASE 3 of 3 - 9999-12-31 is a real value, not a sentinel.
  //
  // RoleController.vb L542 assigns `New System.DateTime(9999, 12, 31)` for the
  // one-off billing frequency "O", meaning the membership never expires. It is
  // sentinel-shaped but genuine, so it must render normally. Deciding to show it as
  // the word "never" is a screen's decision, not this pipe's.
  // ===========================================================================
  it('renders the perpetual expiry value normally rather than as absent', () => {
    expect(pipe.transform(PERPETUAL_INSTANT)).not.toBe(EMPTY);
    expect(pipe.transform(PERPETUAL_INSTANT)).toBe(PERPETUAL_AS_DATE);
    expect(pipe.transform(PERPETUAL_INSTANT, DATE_MODE)).toBe(PERPETUAL_AS_DATE);
    expect(pipe.transform(PERPETUAL_INSTANT, DATETIME_MODE)).toBe(PERPETUAL_AS_DATE_TIME);

    // The full four-digit year survives: a two-digit-year token would have printed
    // '12/31/99' and quietly turned "perpetual" into a date in the past.
    expect(pipe.transform(PERPETUAL_INSTANT)).toContain('9999');
    expect(pipe.transform(PERPETUAL_INSTANT)).not.toContain('Invalid');
  });

  // ===========================================================================
  // Absent values. The erasure is confined to the display layer: on the wire an
  // omitted date, an explicit null and the minimum-value sentinel remain three
  // distinct things, and only the rendering treats them alike.
  // ===========================================================================
  it('renders null as the same empty cell the sentinel produces', () => {
    expect(pipe.transform(null)).toBe(EMPTY);
    expect(pipe.transform(null, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(null, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(null)).toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));
  });

  it('renders undefined as the same empty cell the sentinel produces', () => {
    expect(pipe.transform(undefined)).toBe(EMPTY);
    expect(pipe.transform(undefined, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(undefined, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(undefined)).toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));

    // And indistinguishable from null once rendered.
    expect(pipe.transform(undefined)).toBe(pipe.transform(null));
  });

  // ===========================================================================
  // An unusable value degrades to an empty cell, never to the platform's own
  // invalid-date wording. This mirrors the legacy failure path: `DisplayDate` seeded
  // its result with `Null.NullString` (Users.ascx.vb L397) and its handler at
  // L404-L406 swallowed the exception, so the seeded empty string was what returned.
  // ===========================================================================
  it('renders an unparseable value as an empty cell rather than an error string', () => {
    expect(pipe.transform(UNPARSEABLE_INPUT)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT, DATETIME_MODE)).toBe(EMPTY);

    expect(pipe.transform(UNPARSEABLE_INPUT)).not.toContain('Invalid');
    expect(pipe.transform(UNPARSEABLE_INPUT, DATETIME_MODE)).not.toContain('Invalid');
    expect(pipe.transform(UNPARSEABLE_INPUT)).not.toContain('NaN');
  });

  // ===========================================================================
  // A blank value. `Null.NullString` is the empty string (Null.vb L71-L75), so an
  // absent string genuinely arrives at the display layer as '' rather than as null,
  // and the blank case is reached in practice rather than theoretically.
  // ===========================================================================
  it('renders a blank value as an empty cell', () => {
    expect(pipe.transform(BLANK_INPUT)).toBe(EMPTY);
    expect(pipe.transform(BLANK_INPUT, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(BLANK_INPUT, DATETIME_MODE)).toBe(EMPTY);

    // Whitespace is discarded before the value is judged, so a padded blank is
    // treated as blank and not parsed into some accidental instant.
    expect(pipe.transform(WHITESPACE_INPUT)).toBe(EMPTY);
    expect(pipe.transform(WHITESPACE_INPUT, DATETIME_MODE)).toBe(EMPTY);
  });

  // ===========================================================================
  // Purity. The pipe is declared without `pure: false`, so it is pure and re-runs
  // only when its arguments change. It reads no clock and holds no state, so the
  // same arguments must always produce the same output - which is what lets a
  // template call it inside an OnPush component without surprises.
  // ===========================================================================
  it('returns the same output for the same input across repeated calls', () => {
    const first = pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE);
    const second = pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE);
    const third = pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE);

    expect(first).toBe(ORDINARY_WITH_TIME_OF_DAY_AS_DATE_TIME);
    expect(second).toBe(first);
    expect(third).toBe(first);
  });

  it('is unaffected by intervening calls with other inputs and other modes', () => {
    const before = pipe.transform(ORDINARY_INSTANT);

    // Interleave every other behaviour the pipe has: a sentinel, an absent value,
    // an unusable value, the perpetual value and the other mode.
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(null)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT)).toBe(EMPTY);
    expect(pipe.transform(PERPETUAL_INSTANT, DATETIME_MODE)).toBe(PERPETUAL_AS_DATE_TIME);
    expect(pipe.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(ORDINARY_AS_DATE_TIME);

    const after = pipe.transform(ORDINARY_INSTANT);

    expect(before).toBe(ORDINARY_AS_DATE);
    expect(after).toBe(before);
  });

  it('renders the same output from a freshly resolved instance', () => {
    // No state accumulates on the instance, so a second pipe resolved from the same
    // injector agrees with the first for every representative input.
    const other = TestBed.inject(DateDisplayPipe);

    expect(other.transform(ORDINARY_INSTANT)).toBe(pipe.transform(ORDINARY_INSTANT));
    expect(other.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(
      pipe.transform(ORDINARY_INSTANT, DATETIME_MODE),
    );
    expect(other.transform(SENTINEL_AT_MIDNIGHT)).toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));
    expect(other.transform(PERPETUAL_INSTANT)).toBe(pipe.transform(PERPETUAL_INSTANT));
  });
});
