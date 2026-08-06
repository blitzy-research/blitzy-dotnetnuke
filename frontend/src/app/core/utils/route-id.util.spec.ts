import { isRouteId, parseRouteId } from './route-id.util';

/**
 * Specification for the shared strict route-identifier parser.
 *
 * No `TestBed`, because the module under test is a pair of pure functions over immutable
 * values with no injector, no router and no DOM involvement. Constructing a test bed would
 * add a fixture and an async boundary to assertions that need neither.
 *
 * The suite is organised around the three reasons a value is refused — grammar, magnitude
 * and range — plus the one reason a value must be ACCEPTED that every naive parser gets
 * wrong: `-1` and `0` are real primary keys in this schema and must survive.
 */
describe('parseRouteId', () => {
  describe('the identifiers that must survive', () => {
    // The single most important case in this file. `Portals.PortalID` is IDENTITY(-1, 1),
    // so -1 is simultaneously the first real portal key AND the legacy absent-marker value.
    // A parser that treated it as absent would make the seeded baseline portal unaddressable.
    it('accepts -1, which is a real portal identifier and not an absent marker', () => {
      expect(parseRouteId('-1')).toBe(-1);
    });

    // Roles.RoleID is IDENTITY(0, 1), so zero is the first real role. A falsy-check parser
    // rejects this; this one must not.
    it('accepts 0, which is a real role identifier', () => {
      expect(parseRouteId('0')).toBe(0);
    });

    it('accepts an ordinary positive identifier', () => {
      expect(parseRouteId('42')).toBe(42);
    });

    it('accepts an ordinary negative identifier below the sentinel', () => {
      expect(parseRouteId('-7')).toBe(-7);
    });

    it('accepts a leading zero, which names exactly one record', () => {
      expect(parseRouteId('007')).toBe(7);
    });

    it('accepts the signed 32-bit bounds, which the schema columns permit', () => {
      expect(parseRouteId('2147483647')).toBe(2147483647);
      expect(parseRouteId('-2147483648')).toBe(-2147483648);
    });
  });

  describe('grammar refusals', () => {
    // The defect the review recorded at five call sites: Number.parseInt('12abc', 10) is 12,
    // so /users/12abc silently addressed account 12.
    it('refuses a numeric prefix followed by a tail', () => {
      expect(parseRouteId('12abc')).toBeNull();
    });

    it('refuses a tail before the digits', () => {
      expect(parseRouteId('abc12')).toBeNull();
    });

    // Number('') is 0, and 0 is a real key - so this is not a harmless refusal, it is the
    // difference between "no identifier" and "the first role".
    it('refuses the empty string rather than yielding the real key 0', () => {
      expect(parseRouteId('')).toBeNull();
    });

    it('refuses surrounding whitespace', () => {
      expect(parseRouteId(' 12')).toBeNull();
      expect(parseRouteId('12 ')).toBeNull();
      expect(parseRouteId(' 12 ')).toBeNull();
      expect(parseRouteId('\t12\n')).toBeNull();
    });

    it('refuses a leading plus, so two strings cannot name one record', () => {
      expect(parseRouteId('+5')).toBeNull();
    });

    it('refuses a sign with no digits', () => {
      expect(parseRouteId('-')).toBeNull();
    });

    it('refuses a doubled sign', () => {
      expect(parseRouteId('--5')).toBeNull();
    });

    it('refuses a trailing sign', () => {
      expect(parseRouteId('5-')).toBeNull();
    });

    it('refuses exponent notation', () => {
      expect(parseRouteId('1e3')).toBeNull();
    });

    it('refuses a fractional literal even when it is integral in value', () => {
      expect(parseRouteId('12.0')).toBeNull();
      expect(parseRouteId('12.5')).toBeNull();
    });

    it('refuses a radix prefix', () => {
      expect(parseRouteId('0x10')).toBeNull();
      expect(parseRouteId('0b101')).toBeNull();
      expect(parseRouteId('0o17')).toBeNull();
    });

    it('refuses the non-finite spellings', () => {
      expect(parseRouteId('Infinity')).toBeNull();
      expect(parseRouteId('-Infinity')).toBeNull();
      expect(parseRouteId('NaN')).toBeNull();
    });

    it('refuses digit separators', () => {
      expect(parseRouteId('1_000')).toBeNull();
    });

    // \d in a non-unicode regular expression matches ASCII 0-9 only, so a full-width or
    // Devanagari digit is refused. Accepting one would let a visually similar address
    // resolve to a record.
    it('refuses non-ASCII digits', () => {
      expect(parseRouteId('１２')).toBeNull();
      expect(parseRouteId('१२')).toBeNull();
    });
  });

  describe('magnitude and range refusals', () => {
    // Number.parseInt('9007199254740993', 10) rounds to ...992, a DIFFERENT integer that
    // names a different row. This is the rounding defect the finding named.
    it('refuses a value beyond the exactly-representable integers', () => {
      expect(parseRouteId('9007199254740993')).toBeNull();
      expect(parseRouteId('-9007199254740993')).toBeNull();
    });

    it('refuses an absurdly long run of digits that passes the grammar', () => {
      expect(parseRouteId('9'.repeat(400))).toBeNull();
    });

    it('refuses a value outside the signed 32-bit range the schema columns hold', () => {
      expect(parseRouteId('2147483648')).toBeNull();
      expect(parseRouteId('-2147483649')).toBeNull();
    });
  });

  describe('absent input', () => {
    it('treats a missing route parameter as absent', () => {
      expect(parseRouteId(null)).toBeNull();
    });

    it('treats an undefined route parameter as absent', () => {
      expect(parseRouteId(undefined)).toBeNull();
    });
  });

  describe('purity', () => {
    // The shared grammar carries no `g` flag, so it holds no `lastIndex` between calls. This
    // asserts that property behaviourally: a stateful instance would answer differently on
    // the second identical call.
    it('answers identically on repeated calls, so no state leaks between them', () => {
      expect(parseRouteId('12')).toBe(12);
      expect(parseRouteId('12')).toBe(12);
      expect(parseRouteId('12abc')).toBeNull();
      expect(parseRouteId('12')).toBe(12);
    });

    it('never throws, whatever it is handed', () => {
      const inputs: readonly (string | null | undefined)[] = [
        '',
        '-',
        'abc',
        '9'.repeat(400),
        null,
        undefined,
      ];

      for (const input of inputs) {
        expect(() => parseRouteId(input)).not.toThrow();
      }
    });
  });
});

describe('isRouteId', () => {
  it('accepts the identifiers the schema seeds first', () => {
    expect(isRouteId(-1)).toBeTrue();
    expect(isRouteId(0)).toBeTrue();
  });

  it('accepts an ordinary identifier and the signed 32-bit bounds', () => {
    expect(isRouteId(42)).toBeTrue();
    expect(isRouteId(2147483647)).toBeTrue();
    expect(isRouteId(-2147483648)).toBeTrue();
  });

  it('refuses a value outside the signed 32-bit range', () => {
    expect(isRouteId(2147483648)).toBeFalse();
    expect(isRouteId(-2147483649)).toBeFalse();
  });

  it('refuses a non-integer', () => {
    expect(isRouteId(1.5)).toBeFalse();
  });

  it('refuses the non-finite values, including NaN', () => {
    expect(isRouteId(Number.NaN)).toBeFalse();
    expect(isRouteId(Number.POSITIVE_INFINITY)).toBeFalse();
    expect(isRouteId(Number.NEGATIVE_INFINITY)).toBeFalse();
  });

  it('refuses a value beyond the exactly-representable integers', () => {
    expect(isRouteId(2 ** 53)).toBeFalse();
  });

  it('refuses a non-number, including a numeric string', () => {
    expect(isRouteId('12')).toBeFalse();
    expect(isRouteId(null)).toBeFalse();
    expect(isRouteId(undefined)).toBeFalse();
    expect(isRouteId({})).toBeFalse();
  });
});
