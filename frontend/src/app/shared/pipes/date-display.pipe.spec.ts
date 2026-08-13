// PROVENANCE. The legacy DotNetNuke tree carries no automated tests of any kind, so this file has no
// predecessor to port: it is authored fresh from the legacy behaviour the pipe must reproduce. That
// behaviour was not invented either.

import { Injector, LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { DateDisplayPipe, type DateDisplayMode } from './date-display.pipe';

describe('DateDisplayPipe', () => {
  const TEST_LOCALE = 'en-US';

  // Deliberately fictitious rather than merely unregistered today, so registering
  // a real locale elsewhere later cannot invalidate the degradation case.
  const UNREGISTERED_LOCALE = 'zz-ZZ';

  const DATE_MODE: DateDisplayMode = 'date';
  const DATETIME_MODE: DateDisplayMode = 'datetime';

  // The legacy layer encoded an absent string as the empty string, so an empty
  // cell is the faithful rendering of "nothing to show".
  const EMPTY = '';

  const SENTINEL_AT_MIDNIGHT = '0001-01-01T00:00:00.000Z';

  const SENTINEL_WITH_TIME_OF_DAY = '0001-01-01T13:45:30.000Z';

  const SENTINEL_WITHOUT_ZONE = '0001-01-01T13:45:30';

  const ORDINARY_INSTANT = '2004-09-10T00:00:00.000Z';

  const ORDINARY_INSTANT_WITH_TIME_OF_DAY = '2004-09-10T21:35:09.000Z';

  const PERPETUAL_INSTANT = '9999-12-31T00:00:00.000Z';

  const UNPARSEABLE_INPUT = 'not-a-date';

  /** Values the PLATFORM accepts and silently normalises into a different, real date. */
  const CALENDAR_INVALID_INPUTS = [
    '2024-02-30T00:00:00Z', // platform yields 2024-03-01
    '2023-02-29', // platform yields 2023-03-01 - 2023 is not a leap year
    '2024-09-31', // platform yields 2024-10-01 - September has 30 days
    '2024-13-01', // month 13
    '2024-00-10', // month 0
  ] as const;

  /**
   * Values that are numeric, or otherwise not the wire shape, which the platform nonetheless parses into
   * a confident date.
   */
  const NUMERIC_LIKE_INPUTS = [
    '0', // platform yields 2000-01-01
    '1', // platform yields 2001-01-01
    '2024', // platform yields 2024-01-01
    '20240910', // basic-format ISO, which the platform rejects outright
  ] as const;

  /** Values shaped for a human reader or a different producer, all of which the platform accepts. */
  const NON_WIRE_FORMAT_INPUTS = [
    '12/31/2024', // platform yields 2024-12-31
    'Sep 10 2024', // platform yields 2024-09-10
    '2024-9-10', // unpadded month; platform yields 2024-09-10
    '-2024-09-10', // leading sign; platform silently ignores it
  ] as const;

  /**
   * Values whose DATE portion is a real calendar date and whose overall shape is the wire format, but
   * whose TIME portion is out of range.
   */
  const TIME_OUT_OF_RANGE_INPUTS = [
    '2024-09-10T25:00:00Z', // hour 25
    '2024-09-10T14:60:00Z', // minute 60
    '2024-09-10T14:30:61Z', // second 61
  ] as const;

  /**
   * A real leap day, held alongside the invalid ones so the calendar check is shown to reject only what
   * does not exist rather than everything unusual.
   */
  const LEAP_DAY_INSTANT = '2024-02-29T00:00:00.000Z';

  /** The rendering the leap day must keep producing. */
  const LEAP_DAY_AS_DATE = '2/29/2024';

  /**
   * A legitimate value whose numeric offset places it on a DIFFERENT UTC day from the one its own date
   * field names: 01:00 at +02:00 is 23:00 UTC the previous day. It is held here because it is the value
   * that distinguishes a correct calendar check from a naive one — validating by round-tripping the
   * parsed instant back to a UTC date would reject this, and it must not be rejected.
   */
  const OFFSET_CROSSING_MIDNIGHT = '2024-09-10T01:00:00+02:00';

  /** The rendering that value must produce: the UTC day, 2024-09-09. */
  const OFFSET_CROSSING_MIDNIGHT_AS_DATE = '9/9/2024';

  /**
   * A date in a two-digit year that is NOT the sentinel year. This value exists to pin two things the
   * rest of the suite leaves open.
   */
  const LOW_YEAR_INSTANT = '0050-06-15T00:00:00.000Z';

  /** Four-digit year padding is the `yyyy` pattern's documented behaviour. */
  const LOW_YEAR_AS_DATE = '6/15/0050';

  /** The same low-year value in datetime mode, at UTC midnight. */
  const LOW_YEAR_AS_DATE_TIME = '6/15/0050 12:00:00 AM';

  /** A value that is present on the wire but blank. */
  const BLANK_INPUT = '';

  const WHITESPACE_INPUT = '   ';

  const ORDINARY_AS_DATE = '9/10/2004';

  const ORDINARY_AS_DATE_TIME = '9/10/2004 12:00:00 AM';

  const ORDINARY_WITH_TIME_OF_DAY_AS_DATE_TIME = '9/10/2004 9:35:09 PM';

  const PERPETUAL_AS_DATE = '12/31/9999';

  const PERPETUAL_AS_DATE_TIME = '12/31/9999 12:00:00 AM';

  // What an unguarded formatter would emit for the sentinel, held as a literal so the guard cases can
  // assert against the exact wrong answer rather than against a vague "not this shape".
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
    // Constructed directly, because that is the only way a test can drive the
    // formatter's own failure path.
    const withUnavailableLocale = new DateDisplayPipe(UNREGISTERED_LOCALE);

    expect(() => withUnavailableLocale.transform(ORDINARY_INSTANT)).not.toThrow();
    expect(withUnavailableLocale.transform(ORDINARY_INSTANT)).toBe(EMPTY);
    expect(withUnavailableLocale.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(EMPTY);

    // An absent value short-circuits ahead of the formatter, so the two routes to
    // an empty cell stay indistinguishable to a template.
    expect(withUnavailableLocale.transform(null)).toBe(EMPTY);
  });

  // The sentinel is not dropped in transit: a minimum-value date crosses the wire as a real ISO-8601 string
  // rather than as an omitted property or a null, so the client is what has to recognise it - and in both
  // modes.
  it('renders the minimum-value sentinel as an empty cell in both modes', () => {
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT, DATETIME_MODE)).toBe(EMPTY);
  });

  it('never leaks year one onto the screen in any shape', () => {
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe(SENTINEL_IF_UNGUARDED);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe('01/01/0001');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe('1/1/1');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toContain('0001');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT, DATETIME_MODE)).not.toContain('0001');
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toContain('Invalid');

    // A real string, not a null dressed up as one: a template interpolates the
    // result directly, so its type matters.
    expect(typeof pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe('string');
    expect(typeof pipe.transform(SENTINEL_AT_MIDNIGHT, DATETIME_MODE)).toBe('string');
  });

  // The case a naive full-timestamp equality check would let through, so it is
  // asserted separately from the exact-midnight one.
  it('renders a sentinel carrying a time of day as an empty cell in both modes', () => {
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATETIME_MODE)).toBe(EMPTY);

    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATETIME_MODE)).not.toContain('1:45:30');
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY)).not.toContain('0001');

    // Both sentinel spellings collapse to the same empty cell, proving the date-part
    // comparison really is indifferent to the time of day.
    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY)).toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));
  });

  it('recognises the sentinel when the wire value carries no zone designator', () => {
    // The zone-less spelling reaches the guard by a different parse path, so it is
    // asserted rather than assumed to behave the same way.
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE)).not.toContain('0001');
  });

  it('renders an ordinary date as a short date when no mode is supplied', () => {
    expect(pipe.transform(ORDINARY_INSTANT)).toBe(ORDINARY_AS_DATE);

    expect(pipe.transform(ORDINARY_INSTANT, DATE_MODE)).toBe(ORDINARY_AS_DATE);
    expect(pipe.transform(ORDINARY_INSTANT, DATE_MODE)).toBe(pipe.transform(ORDINARY_INSTANT));

    expect(pipe.transform(ORDINARY_INSTANT)).not.toContain(':');
    expect(pipe.transform(ORDINARY_INSTANT)).toContain('2004');
  });

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

    expect(pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY)).toBe(ORDINARY_AS_DATE);
  });

  // Sentinel-shaped but genuine: the legacy one-off billing frequency used a
  // year-9999 expiry to mean "never expires", so it must render like any other date.
  it('renders the perpetual expiry value normally rather than as absent', () => {
    expect(pipe.transform(PERPETUAL_INSTANT)).not.toBe(EMPTY);
    expect(pipe.transform(PERPETUAL_INSTANT)).toBe(PERPETUAL_AS_DATE);
    expect(pipe.transform(PERPETUAL_INSTANT, DATE_MODE)).toBe(PERPETUAL_AS_DATE);
    expect(pipe.transform(PERPETUAL_INSTANT, DATETIME_MODE)).toBe(PERPETUAL_AS_DATE_TIME);

    // The four-digit year survives: a two-digit-year token would print '12/31/99'
    // and quietly turn "perpetual" into a date in the past.
    expect(pipe.transform(PERPETUAL_INSTANT)).toContain('9999');
    expect(pipe.transform(PERPETUAL_INSTANT)).not.toContain('Invalid');
  });

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

    expect(pipe.transform(undefined)).toBe(pipe.transform(null));
  });

  // Mirrors the legacy failure path, where the formatter seeded an empty result and
  // swallowed the exception rather than surfacing one.
  it('renders an unparseable value as an empty cell rather than an error string', () => {
    expect(pipe.transform(UNPARSEABLE_INPUT)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT, DATETIME_MODE)).toBe(EMPTY);

    expect(pipe.transform(UNPARSEABLE_INPUT)).not.toContain('Invalid');
    expect(pipe.transform(UNPARSEABLE_INPUT, DATETIME_MODE)).not.toContain('Invalid');
    expect(pipe.transform(UNPARSEABLE_INPUT)).not.toContain('NaN');
  });

  it('renders a date that does not exist on the calendar as an empty cell', () => {
    for (const input of CALENDAR_INVALID_INPUTS) {
      expect(pipe.transform(input)).toBe('');
      expect(pipe.transform(input, DATE_MODE)).toBe('');
      expect(pipe.transform(input, DATETIME_MODE)).toBe('');
    }
  });

  it('never renders the month a non-existent day would roll forward into', () => {
    expect(pipe.transform('2024-02-30T00:00:00Z')).not.toBe('3/1/2024');
    expect(pipe.transform('2023-02-29')).not.toBe('3/1/2023');
    expect(pipe.transform('2024-09-31')).not.toBe('10/1/2024');

    // And none of them leaks a partial rendering of the intended month either.
    expect(pipe.transform('2024-02-30T00:00:00Z')).not.toContain('2024');
    expect(pipe.transform('2023-02-29')).not.toContain('2023');
    expect(pipe.transform('2024-09-31')).not.toContain('2024');
  });

  it('renders a numeric-like value as an empty cell rather than a year', () => {
    for (const input of NUMERIC_LIKE_INPUTS) {
      expect(pipe.transform(input)).toBe('');
      expect(pipe.transform(input, DATETIME_MODE)).toBe('');
    }

    expect(pipe.transform('0')).not.toBe('1/1/2000');
    expect(pipe.transform('1')).not.toBe('1/1/2001');
    expect(pipe.transform('2024')).not.toBe('1/1/2024');
  });

  it('renders a value outside the wire format as an empty cell', () => {
    for (const input of NON_WIRE_FORMAT_INPUTS) {
      expect(pipe.transform(input)).toBe('');
      expect(pipe.transform(input, DATETIME_MODE)).toBe('');
    }

    expect(pipe.transform('12/31/2024')).not.toBe('12/31/2024');

    // A leading sign is silently discarded by the platform, so a negative number
    // rendered into a date field would display as a positive year.
    expect(pipe.transform('-2024-09-10')).not.toBe('9/10/2024');
  });

  // The validation must be a scalpel, not a hammer. These two cases are the ones a careless implementation
  // breaks, and they are asserted immediately after the rejections so that tightening the pipe can never
  // quietly cost a valid value.
  it('still renders a genuine leap day', () => {
    expect(pipe.transform(LEAP_DAY_INSTANT)).toBe(LEAP_DAY_AS_DATE);
    expect(pipe.transform(LEAP_DAY_INSTANT)).not.toBe('');

    // February 29 exists in 2024 and does not exist in 2023, and the pipe must
    // distinguish the two rather than treating the date as suspicious in general.
    expect(pipe.transform('2023-02-29')).toBe('');
  });

  it('still renders a low year that is not the sentinel year', () => {
    // The sentinel guard must be a scalpel too. It recognises year ONE, and this expectation is what stops
    // it - or the calendar check in front of it - from degenerating into a filter on small years generally.
    expect(pipe.transform(LOW_YEAR_INSTANT)).toBe(LOW_YEAR_AS_DATE);
    expect(pipe.transform(LOW_YEAR_INSTANT)).not.toBe('');

    // Adjacent by year, opposite in outcome: the sentinel still blanks, and this value still renders, so
    // the two are genuinely distinguished rather than being swept up together.
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(pipe.transform(LOW_YEAR_INSTANT)).not.toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));

    // Also asserted in the other mode, because the sentinel guard sits ahead of
    // the mode branch and a regression there would affect both.
    expect(pipe.transform(LOW_YEAR_INSTANT, DATETIME_MODE)).toBe(LOW_YEAR_AS_DATE_TIME);
  });

  it('renders an out-of-range time of day as an empty cell', () => {
    for (const input of TIME_OUT_OF_RANGE_INPUTS) {
      expect(pipe.transform(input)).toBe('');
      expect(pipe.transform(input, DATE_MODE)).toBe('');
      expect(pipe.transform(input, DATETIME_MODE)).toBe('');
    }

    // And the date portion is not salvaged and rendered on its own, which would be
    // a plausible-looking result assembled from an unusable value.
    for (const input of TIME_OUT_OF_RANGE_INPUTS) {
      expect(pipe.transform(input)).not.toBe('9/10/2024');
      expect(pipe.transform(input)).not.toContain('2024');
      expect(pipe.transform(input, DATETIME_MODE)).not.toContain('Invalid');
      expect(pipe.transform(input, DATETIME_MODE)).not.toContain('NaN');
    }
  });

  it('still renders a value whose offset places it on another UTC day', () => {
    expect(pipe.transform(OFFSET_CROSSING_MIDNIGHT)).toBe(
      OFFSET_CROSSING_MIDNIGHT_AS_DATE,
    );
    expect(pipe.transform(OFFSET_CROSSING_MIDNIGHT)).not.toBe('');
  });

  it('renders a blank value as an empty cell', () => {
    expect(pipe.transform(BLANK_INPUT)).toBe(EMPTY);
    expect(pipe.transform(BLANK_INPUT, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(BLANK_INPUT, DATETIME_MODE)).toBe(EMPTY);

    expect(pipe.transform(WHITESPACE_INPUT)).toBe(EMPTY);
    expect(pipe.transform(WHITESPACE_INPUT, DATETIME_MODE)).toBe(EMPTY);
  });

  it('renders a day that does not exist in its month as an empty cell', () => {
    // February 30 never exists in any year.
    expect(pipe.transform('2024-02-30')).toBe(EMPTY);
    expect(pipe.transform('2024-02-30')).not.toBe('3/1/2024');

    // 2023 is a common year, so February 29 does not exist in it.
    expect(pipe.transform('2023-02-29')).toBe(EMPTY);
    expect(pipe.transform('2023-02-29')).not.toBe('3/1/2023');

    // April and June are thirty-day months.
    expect(pipe.transform('2024-04-31')).toBe(EMPTY);
    expect(pipe.transform('2024-04-31')).not.toBe('5/1/2024');

    // The date part is validated even when a time and a zone follow it.
    expect(pipe.transform('2024-06-31T12:00:00Z', DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform('2024-06-31T12:00:00Z', DATETIME_MODE)).not.toBe(
      '7/1/2024 12:00:00 PM',
    );
  });

  it('renders an out-of-range month or day-of-month as an empty cell', () => {
    expect(pipe.transform('2024-13-01')).toBe(EMPTY);
    expect(pipe.transform('2024-00-10')).toBe(EMPTY);
    expect(pipe.transform('2024-01-32')).toBe(EMPTY);
    expect(pipe.transform('2024-01-00')).toBe(EMPTY);
  });

  it('still renders every legitimate date, including the leap day and both extremes', () => {
    // 2024 is divisible by four and not by one hundred, so February 29 exists.
    expect(pipe.transform('2024-02-29')).toBe('2/29/2024');

    // The century rule and its four-hundred-year exception, which a naive
    // divisible-by-four test would get wrong in opposite directions.
    expect(pipe.transform('2000-02-29')).toBe('2/29/2000');
    expect(pipe.transform('1900-02-29')).toBe(EMPTY);

    // Month-length boundaries that are valid and must not be rejected.
    expect(pipe.transform('2024-01-31')).toBe('1/31/2024');
    expect(pipe.transform('2024-04-30')).toBe('4/30/2024');

    // The perpetual expiry and the null-date sentinel both survive the new validation: the first renders
    // normally, the second is still recognised as the sentinel rather than being mistaken for an impossible
    // date.
    expect(pipe.transform(PERPETUAL_INSTANT)).toBe(PERPETUAL_AS_DATE);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).not.toBe(SENTINEL_IF_UNGUARDED);
  });

  it('does not reject an offset-bearing value whose UTC date differs from its own text', () => {
    expect(pipe.transform('2024-07-04T02:00:00+05:00', DATETIME_MODE)).toBe(
      '7/3/2024 9:00:00 PM',
    );
    expect(pipe.transform('2024-07-04T02:00:00+05:00')).toBe('7/3/2024');

    // The same reasoning across a month boundary, and westward as well as
    // eastward, so the tolerance is not an artefact of one direction.
    expect(pipe.transform('2024-07-01T02:00:00+05:00')).toBe('6/30/2024');
    expect(pipe.transform('2024-06-30T22:00:00-05:00')).toBe('7/1/2024');
  });

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

    expect(pipe.transform(SENTINEL_WITH_TIME_OF_DAY, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(null)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT)).toBe(EMPTY);
    expect(pipe.transform(PERPETUAL_INSTANT, DATETIME_MODE)).toBe(PERPETUAL_AS_DATE_TIME);
    expect(pipe.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(ORDINARY_AS_DATE_TIME);

    const after = pipe.transform(ORDINARY_INSTANT);

    expect(before).toBe(ORDINARY_AS_DATE);
    expect(after).toBe(before);
  });

  it('resolves as a singleton within one injector', () => {
    expect(TestBed.inject(DateDisplayPipe)).toBe(pipe);
    expect(TestBed.inject(DateDisplayPipe)).toBe(TestBed.inject(DateDisplayPipe));
  });

  it('renders the same output from a genuinely independent instance', () => {
    // A SEPARATE injector, so the pipe it produces is a different object rather than the cached one. The
    // locale is pinned identically, which is what makes the output comparison meaningful: the only
    // difference between the two pipes is their identity.
    const separateInjector = Injector.create({
      providers: [
        { provide: LOCALE_ID, useValue: TEST_LOCALE },
        { provide: DateDisplayPipe, useClass: DateDisplayPipe, deps: [LOCALE_ID] },
      ],
    });
    const independent = separateInjector.get(DateDisplayPipe);

    // Instance inequality asserted BEFORE the outputs are compared. Without this, the comparisons below
    // could be satisfied by one object agreeing with itself, which proves nothing about state accumulation.
    expect(independent).not.toBe(pipe);
    expect(independent).toBeInstanceOf(DateDisplayPipe);

    expect(independent.transform(ORDINARY_INSTANT)).toBe(pipe.transform(ORDINARY_INSTANT));
    expect(independent.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(
      pipe.transform(ORDINARY_INSTANT, DATETIME_MODE),
    );
    expect(independent.transform(SENTINEL_AT_MIDNIGHT)).toBe(
      pipe.transform(SENTINEL_AT_MIDNIGHT),
    );
    expect(independent.transform(PERPETUAL_INSTANT)).toBe(pipe.transform(PERPETUAL_INSTANT));

    // Stated as literals as well, so this test cannot pass by two pipes being
    // identically broken.
    expect(independent.transform(ORDINARY_INSTANT)).toBe(ORDINARY_AS_DATE);
    expect(independent.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(independent.transform(PERPETUAL_INSTANT)).toBe(PERPETUAL_AS_DATE);
  });

  it('carries no state from one call into the next across instances', () => {
    // Order-independence, driven through two distinct objects. The first pipe is exercised with the values
    // most likely to leave residue - a sentinel, an absent value and a rejected value - and the second must
    // be unaffected.
    const independent = new DateDisplayPipe(TEST_LOCALE);

    expect(independent).not.toBe(pipe);

    expect(pipe.transform(SENTINEL_AT_MIDNIGHT)).toBe(EMPTY);
    expect(pipe.transform(null)).toBe(EMPTY);
    expect(pipe.transform(UNPARSEABLE_INPUT)).toBe(EMPTY);
    expect(pipe.transform('2024-02-30T00:00:00Z')).toBe(EMPTY);

    expect(independent.transform(ORDINARY_INSTANT)).toBe(ORDINARY_AS_DATE);
    expect(independent.transform(ORDINARY_INSTANT, DATETIME_MODE)).toBe(
      ORDINARY_AS_DATE_TIME,
    );
  });

  /**
   * Calls `transform` the way the runtime can, with the compile-time parameter type erased.
   *
   * @param wireValue The value as it would arrive from a malformed response.
   * @param mode Optional rendering mode, forwarded unchanged.
   * @returns Whatever the pipe returns for that value.
   */
  function transformWireValue(wireValue: unknown, mode?: DateDisplayMode): string {
    const erased = pipe.transform.bind(pipe) as (
      value: unknown,
      mode?: DateDisplayMode,
    ) => string;

    return erased(wireValue, mode);
  }

  /**
   * Every non-string shape a malformed payload realistically delivers where the contract promised an
   * ISO-8601 string.
   */
  const NON_STRING_WIRE_VALUES: readonly unknown[] = [
    0,
    1094774400000,
    Number.NaN,
    Number.POSITIVE_INFINITY,
    true,
    false,
    {},
    { value: ORDINARY_INSTANT },
    [],
    [ORDINARY_INSTANT],
    new Date(ORDINARY_INSTANT),
    Symbol('date'),
    (): string => ORDINARY_INSTANT,
  ];

  it('renders every non-string payload as an empty cell instead of throwing', () => {
    for (const wireValue of NON_STRING_WIRE_VALUES) {
      expect(() => transformWireValue(wireValue)).not.toThrow();
      expect(transformWireValue(wireValue)).toBe(EMPTY);
      expect(transformWireValue(wireValue, DATE_MODE)).toBe(EMPTY);
      expect(transformWireValue(wireValue, DATETIME_MODE)).toBe(EMPTY);
    }
  });

  it('renders a non-string payload indistinguishably from an absent one', () => {
    // The rendering must not invent a new visible state for a malformed payload. An empty cell is what
    // null, undefined, a blank string and the sentinel all produce, so an unusable value of any other shape
    // must produce it too.
    expect(transformWireValue(1094774400000)).toBe(pipe.transform(null));
    expect(transformWireValue({})).toBe(pipe.transform(undefined));
    expect(transformWireValue([ORDINARY_INSTANT])).toBe(pipe.transform(SENTINEL_AT_MIDNIGHT));
  });

  it('renders a Date instance as an empty cell, as its documented contract states', () => {
    const instance = new Date(ORDINARY_INSTANT);

    expect(transformWireValue(instance)).toBe(EMPTY);
    expect(transformWireValue(instance)).not.toBe(ORDINARY_AS_DATE);
  });

  it('renders an over-long candidate as an empty cell, exactly as an unparseable one', () => {
    const overLong = `${ORDINARY_INSTANT}${'0'.repeat(512)}`;

    expect(pipe.transform(overLong)).toBe(EMPTY);
    expect(pipe.transform(overLong, DATE_MODE)).toBe(EMPTY);
    expect(pipe.transform(overLong, DATETIME_MODE)).toBe(EMPTY);

    // Indistinguishable from the unparseable case, which is the claim the bound
    // rests on.
    expect(pipe.transform(overLong)).toBe(pipe.transform(UNPARSEABLE_INPUT));
  });

  it('renders a long repeated payload as an empty cell without emitting any date', () => {
    const repeated = '2004-09-10T00:00:00.000Z '.repeat(64);

    expect(pipe.transform(repeated)).toBe(EMPTY);
    expect(pipe.transform(repeated, DATETIME_MODE)).toBe(EMPTY);
    expect(pipe.transform(repeated)).not.toContain('2004');
  });

  it('still renders a heavily padded but legitimate value normally', () => {
    // THE REASON THE BOUND IS APPLIED AFTER TRIMMING. This value is far longer than the bound as it
    // arrives, and well within it once trimmed. Bounding the raw value would blank a legitimate date, which
    // would be a behavioural change rather than a defence - so this expectation is what pins the order.
    const padded = `${' '.repeat(96)}${ORDINARY_INSTANT}${' '.repeat(96)}`;

    expect(padded.length).toBeGreaterThan(64);
    expect(pipe.transform(padded)).toBe(ORDINARY_AS_DATE);
    expect(pipe.transform(padded, DATETIME_MODE)).toBe(ORDINARY_AS_DATE_TIME);
  });

  it('leaves every ordinary wire shape well inside the bound', () => {
    // Positive control: the bound must be invisible for real data. Each of these
    // renders exactly as the expectations earlier in this file require.
    expect(pipe.transform(ORDINARY_INSTANT)).toBe(ORDINARY_AS_DATE);
    expect(pipe.transform(ORDINARY_INSTANT_WITH_TIME_OF_DAY, DATETIME_MODE)).toBe(
      ORDINARY_WITH_TIME_OF_DAY_AS_DATE_TIME,
    );
    expect(pipe.transform(PERPETUAL_INSTANT)).toBe(PERPETUAL_AS_DATE);
    expect(pipe.transform(SENTINEL_WITHOUT_ZONE)).toBe(EMPTY);
  });

  // The pipe's own `render` takes a two-member union of literal types, so the compiler rejects any other
  // pattern - a widened signature or a derived pattern will not build.
  it('emits only the two documented output shapes, whatever the input', () => {
    const shortDate = /^\d{1,2}\/\d{1,2}\/\d{4}$/;
    const dateAndTime = /^\d{1,2}\/\d{1,2}\/\d{4} \d{1,2}:\d{2}:\d{2} (?:AM|PM)$/;
    const inputs: readonly (string | null | undefined)[] = [
      ORDINARY_INSTANT,
      ORDINARY_INSTANT_WITH_TIME_OF_DAY,
      PERPETUAL_INSTANT,
      SENTINEL_AT_MIDNIGHT,
      SENTINEL_WITHOUT_ZONE,
      UNPARSEABLE_INPUT,
      BLANK_INPUT,
      WHITESPACE_INPUT,
      null,
      undefined,
    ];

    for (const input of inputs) {
      const asDate = pipe.transform(input, DATE_MODE);
      const asDateTime = pipe.transform(input, DATETIME_MODE);

      expect(asDate === EMPTY || shortDate.test(asDate)).toBeTrue();
      expect(asDateTime === EMPTY || dateAndTime.test(asDateTime)).toBeTrue();
    }
  });

  it('accepts no caller influence over the format, only over the mode', () => {
    const erasedMode = pipe.transform.bind(pipe) as (
      value: string | null | undefined,
      mode?: unknown,
    ) => string;

    expect(erasedMode(ORDINARY_INSTANT, 'M/d/yyyy h:mm:ss a')).toBe(ORDINARY_AS_DATE);
    expect(erasedMode(ORDINARY_INSTANT, 'a'.repeat(4096))).toBe(ORDINARY_AS_DATE);
    expect(erasedMode(ORDINARY_INSTANT, {})).toBe(ORDINARY_AS_DATE);
    expect(erasedMode(ORDINARY_INSTANT, 0)).toBe(ORDINARY_AS_DATE);
  });
});
