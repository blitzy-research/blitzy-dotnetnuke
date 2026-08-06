/**
 * Specification for `core/utils/decode.util.ts` — the runtime contract checker that every
 * transport in this application now reads its responses through.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS FILE EXISTS
 * ---------------------------------------------------------------------------
 * A TypeScript interface is erased at compile time. `http.get<PortalDetail>(…)` compiles to
 * `http.get(…)`: nothing inspects the body, and the value a caller receives is trusted purely
 * because a developer wrote a type where a value was expected. A renamed member then arrives
 * as `undefined` behind a 200 and surfaces as a blank field, a `NaN` or a silently empty grid
 * several layers away from the response that caused it — with no way back to the seam.
 *
 * This module is the seam. Because every service depends on it, a defect here is a defect in
 * every boundary at once, which is what makes its own coverage worth more than the sum of the
 * per-service cases: those prove that each service USES a decoder, and these prove the
 * decoders decide correctly.
 *
 * ---------------------------------------------------------------------------
 * THE THREE PROPERTIES WORTH ASSERTING, AND WHY EACH ONE IS HERE
 * ---------------------------------------------------------------------------
 * 1. IT REFUSES WHAT IT SHOULD. The obvious half, and the cheap half.
 *
 * 2. IT ADMITS WHAT IT SHOULD — which is the half that goes wrong. A validator that is
 *    slightly too strict is worse than none: it refuses conforming responses, and it does so
 *    in production against real data rather than in a test. Every legitimate shape in this
 *    schema is therefore asserted positively, and the sentinel values get their own cases,
 *    because `0`, `-1` and `""` are all REAL VALUES here rather than absences and the
 *    intuitive implementation of each decoder would reject or coalesce them.
 *
 * 3. IT DISCLOSES NOTHING. A violation report names the member and the expected type. It
 *    never names the value — because the values crossing these boundaries include passwords,
 *    bearer tokens, profile answers and module content, and a report is destined for a log.
 *
 * ---------------------------------------------------------------------------
 * SENTINELS, WHICH ARE THE REASON HALF THESE CASES LOOK ODD
 * ---------------------------------------------------------------------------
 * The legacy schema makes three values simultaneously meaningful and "absent":
 *
 * - `-1` is `Null.NullInteger` (`Library/Components/Shared/Null.vb:L41-L45`, whose body is
 *   literally `Return -1`) AND the identity seed of `Portals.PortalID`, so the first portal
 *   ever created carries `-1` and the second carries `0`.
 * - `0` is the identity seed of `Roles.RoleID`, `Tabs.TabID` and `Modules.ModuleID`, so it is
 *   an ordinary identifier in three tables.
 * - `""` is `Null.NullString` (`:L71-L75`, body literally `Return ""`), so an empty string is
 *   the legacy spelling of an absent string rather than a null reference.
 *
 * A decoder that guarded an identifier on being truthy, positive or unequal to `-1` would
 * therefore drop real rows, and one that coalesced `""` to `null` would make a value the
 * operator CLEARED indistinguishable from one never filled in. Several cases below exist for
 * no other purpose than to hold that line.
 */

import {
  ContractViolationError,
  arrayOf,
  decodeApiMeta,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeNumber,
  decodeObject,
  decodePageStructure,
  decodeResponse,
  decodeString,
  envelopeOf,
  isContractViolation,
  nonEmptyString,
  nullable,
  objectOf,
  oneOf,
  oneOfNumber,
  optional,
  pageOf,
  recordOf,
  responseOf,
} from './decode.util';

import type { Decoder } from './decode.util';

/** A member set standing in for a wire contract, so the object cases assert something real. */
interface Row {
  readonly id: number;
  readonly name: string;
  readonly retired: boolean;
  readonly parentId: number | null;
}

const decodeRow: Decoder<Row> = objectOf<Row>({
  id: decodeInteger,
  name: decodeString,
  retired: decodeBoolean,
  parentId: nullable(decodeInteger),
});

/** A conforming row, used as the base every malformed variant is derived from. */
const ROW: Row = { id: 0, name: 'Announcements', retired: false, parentId: null };

/**
 * Runs a decoder and returns the violation it raised.
 *
 * @param decoder The decoder under test.
 * @param value The value to decode.
 * @param path The path to decode it at.
 * @returns The violation.
 * @throws Error When the decoder did not raise a contract violation, since a case that
 *   expected a refusal and got a value must fail rather than silently assert nothing.
 */
function violationFrom(decoder: Decoder<unknown>, value: unknown, path = 'v'): ContractViolationError {
  try {
    decoder(value, path);
  } catch (raised: unknown) {
    if (isContractViolation(raised)) {
      return raised;
    }

    throw new Error(`expected a contract violation, got ${String(raised)}`);
  }

  throw new Error('expected a contract violation, the decoder returned a value');
}

describe('decode.util', () => {
  describe('ContractViolationError', () => {
    it('carries the member path, the requirement and the received TYPE', () => {
      const violation = new ContractViolationError('response.data.users', 'an integer', 'twelve');

      expect(violation.path).toBe('response.data.users');
      expect(violation.expected).toBe('an integer');
      expect(violation.received).toBe('a string');
      expect(violation.name).toBe('ContractViolationError');
      expect(violation instanceof Error).toBeTrue();
    });

    it('never names the offending value, in the message or on any member', () => {
      // ⚠ A PRIVACY BOUNDARY, NOT A STYLE CHOICE. The values crossing these boundaries include
      // passwords, bearer tokens, profile answers and module content, and a violation report is
      // destined for a log. Naming the value would copy the secret into the diagnostic.
      const secret = 'not-a-real-password-but-treat-it-as-one';
      const violation = new ContractViolationError('response.data.token', 'an integer', secret);

      expect(violation.message).not.toContain(secret);
      expect(violation.received).not.toContain(secret);
      expect(violation.message).toContain('response.data.token');
      expect(violation.message).toContain('an integer');
    });

    it('describes each received type without inspecting it', () => {
      // The descriptions read as English because they are substituted into a sentence, and
      // `undefined` is described as "nothing" so that an ABSENT member reports as absent rather
      // than as a value that happened to be undefined.
      const cases: readonly (readonly [unknown, string])[] = [
        [null, 'null'],
        [undefined, 'nothing'],
        [[], 'an array'],
        [{}, 'an object'],
        ['x', 'a string'],
        [1, 'a number'],
        [true, 'a boolean'],
      ];

      for (const [value, described] of cases) {
        expect(new ContractViolationError('v', 'anything', value).received)
          .withContext(`the type of ${String(value)}`)
          .toBe(described);
      }
    });

    it('recognises only its own violations', () => {
      expect(isContractViolation(new ContractViolationError('v', 'a string', 1))).toBeTrue();
      expect(isContractViolation(new Error('a plain failure'))).toBeFalse();
      expect(isContractViolation(new TypeError('a type error'))).toBeFalse();
      expect(isContractViolation('a string')).toBeFalse();
      expect(isContractViolation(null)).toBeFalse();
    });
  });

  describe('decodeString', () => {
    it('admits the empty string, which is the legacy spelling of an absent string', () => {
      // ⚠ `Null.vb:L71-L75` returns `""` for a missing string, so an operator who cleared a
      // field has an empty value rather than a missing one. Refusing it would refuse a
      // conforming response.
      expect(decodeString('', 'v')).toBe('');
      expect(decodeString('   ', 'v')).toBe('   ');
    });

    it('returns the value unchanged, trimming nothing', () => {
      expect(decodeString('  Announcements  ', 'v')).toBe('  Announcements  ');
    });

    it('refuses every non-string, including ones that would coerce cleanly', () => {
      for (const value of [1, true, null, undefined, {}, []]) {
        expect(violationFrom(decodeString, value).expected).toBe('a string');
      }
    });
  });

  describe('nonEmptyString', () => {
    it('refuses the empty and the blank string', () => {
      expect(violationFrom(nonEmptyString, '').expected).toBe('a non-blank string');
      expect(violationFrom(nonEmptyString, '   \t\n').expected).toBe('a non-blank string');
    });

    it('returns a padded value unchanged rather than trimming it', () => {
      // Trimming would REWRITE a wire value under the guise of validating it.
      expect(nonEmptyString('  token  ', 'v')).toBe('  token  ');
    });
  });

  describe('decodeNumber', () => {
    it('admits zero and negatives', () => {
      expect(decodeNumber(0, 'v')).toBe(0);
      expect(decodeNumber(-1, 'v')).toBe(-1);
      expect(decodeNumber(-0.5, 'v')).toBe(-0.5);
    });

    it('admits a fraction, because a service fee is money', () => {
      expect(decodeNumber(19.99, 'v')).toBe(19.99);
    });

    it('refuses NaN and the infinities', () => {
      // They cannot appear in conforming JSON at all — `JSON.stringify` writes them as `null` —
      // so their presence means the body came from something other than a JSON serialiser, and
      // admitting one would poison every arithmetic downstream of it.
      for (const value of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) {
        expect(violationFrom(decodeNumber, value).expected).toBe('a finite number');
      }
    });

    it('refuses a numeric string', () => {
      expect(violationFrom(decodeNumber, '12').received).toBe('a string');
    });
  });

  describe('decodeInteger', () => {
    it('admits the sentinel-colliding identifiers, which are real rows', () => {
      // ⚠ THE SINGLE MOST IMPORTANT CASE IN THIS FILE. `Portals.PortalID` is `IDENTITY(-1,1)`
      // so `-1` is the FIRST portal as well as the legacy absent-marker, and `Roles.RoleID`,
      // `Tabs.TabID` and `Modules.ModuleID` all seed at `0`. A truthiness test would drop the
      // second portal and every first role, page and module; a `!== -1` test would drop the
      // first portal.
      expect(decodeInteger(-1, 'v')).toBe(-1);
      expect(decodeInteger(0, 'v')).toBe(0);
    });

    it('refuses a fraction', () => {
      expect(violationFrom(decodeInteger, 1.5).expected).toBe('an integer');
    });

    it('refuses an integer sent as text', () => {
      expect(violationFrom(decodeInteger, '7').received).toBe('a string');
    });
  });

  describe('decodeBoolean', () => {
    it('admits false, which is a choice rather than an absence', () => {
      expect(decodeBoolean(false, 'v')).toBeFalse();
      expect(decodeBoolean(true, 'v')).toBeTrue();
    });

    it('refuses the values that would coerce to the WRONG answer', () => {
      // `'false'` is a truthy string. Coercing it would report `true` for a response that said
      // the opposite — an approval flag inverted with no symptom anywhere.
      expect(violationFrom(decodeBoolean, 'false').expected).toBe('a boolean');
      expect(violationFrom(decodeBoolean, 0).expected).toBe('a boolean');
      expect(violationFrom(decodeBoolean, 1).expected).toBe('a boolean');
    });
  });

  describe('decodeObject', () => {
    it('refuses null and an array, both of which are `typeof "object"`', () => {
      // Without these two exclusions a null member or a collection arriving where an object was
      // declared would pass here and fault on the first member read instead.
      expect(violationFrom(decodeObject, null).received).toBe('null');
      expect(violationFrom(decodeObject, []).received).toBe('an array');
    });

    it('admits an empty object', () => {
      expect(decodeObject({}, 'v')).toEqual({});
    });
  });

  describe('decodeDateString', () => {
    it('admits the round-trippable form the server writes', () => {
      expect(decodeDateString('2024-03-02T11:40:00Z', 'v')).toBe('2024-03-02T11:40:00Z');
    });

    it('returns the string rather than a Date', () => {
      // Converting here would move a timezone decision into the transport layer.
      expect(typeof decodeDateString('2024-03-02T11:40:00Z', 'v')).toBe('string');
    });

    it('refuses a string the platform cannot parse', () => {
      // ⚠ AN UNPARSEABLE DATE IS WORSE THAN A MISSING ONE. It becomes an `Invalid Date`, whose
      // every comparison is FALSE — so an expired role membership would be classified as active
      // and the person would keep an entitlement they had lost.
      expect(violationFrom(decodeDateString, 'whenever').expected).toBe(
        'an ISO-8601 date-time string',
      );
      expect(violationFrom(decodeDateString, '').expected).toBe('an ISO-8601 date-time string');
    });
  });

  describe('nullable and optional', () => {
    it('nullable admits null but NOT an absent member', () => {
      // The API writes every declared member, expressing absence as the VALUE `null`, so a
      // missing member is contract drift and must be reported rather than tolerated.
      expect(nullable(decodeInteger)(null, 'v')).toBeNull();
      expect(violationFrom(nullable(decodeInteger), undefined).received).toBe('nothing');
    });

    it('optional admits an absent member but still enforces the type when present', () => {
      expect(optional(decodeInteger)(undefined, 'v')).toBeUndefined();
      expect(optional(decodeInteger)(3, 'v')).toBe(3);
      expect(violationFrom(optional(decodeInteger), 'x').expected).toBe('an integer');
    });

    it('nullable still enforces the inner type on a present value', () => {
      expect(violationFrom(nullable(decodeInteger), 'x').expected).toBe('an integer');
    });
  });

  describe('arrayOf', () => {
    it('admits an empty array, which is a real empty collection', () => {
      expect(arrayOf(decodeString)([], 'v')).toEqual([]);
    });

    it('names the offending element by INDEX', () => {
      // A violation in the fortieth record of a page must name that record rather than the page,
      // or the report says only that something somewhere was wrong.
      expect(violationFrom(arrayOf(decodeString), ['a', 'b', 3]).path).toBe('v[2]');
    });

    it('refuses a non-array, including an object', () => {
      expect(violationFrom(arrayOf(decodeString), { 0: 'a' }).received).toBe('an object');
      expect(violationFrom(arrayOf(decodeString), null).received).toBe('null');
    });
  });

  describe('recordOf', () => {
    it('admits an empty map and preserves an empty VALUE', () => {
      // ⚠ THE CLEARED-SETTING CASE. A settings value the operator cleared is the empty string,
      // and filtering it out — the obvious and wrong implementation — would silently delete
      // every setting anyone had cleared, behind a successful 204.
      expect(recordOf(decodeString)({}, 'v')).toEqual({});
      expect(recordOf(decodeString)({ announcementLength: '' }, 'v')).toEqual({
        announcementLength: '',
      });
    });

    it('names the offending entry by KEY', () => {
      expect(violationFrom(recordOf(decodeString), { cacheTime: 3600 }).path).toBe('v.cacheTime');
    });

    it('refuses an array, which would otherwise index as a record', () => {
      expect(violationFrom(recordOf(decodeString), ['a']).received).toBe('an array');
    });
  });

  describe('oneOf', () => {
    it('admits every code in the set', () => {
      const decode = oneOf(['N', 'O', 'D', 'W', 'M', 'Y'] as const);

      for (const code of ['N', 'O', 'D', 'W', 'M', 'Y'] as const) {
        expect(decode(code, 'v')).toBe(code);
      }
    });

    it('refuses a code outside the set, including one differing only in case', () => {
      // ⚠ THE BILLING-FREQUENCY CASE. These codes are SINGLE CHARACTERS stored in
      // `Roles.BillingFrequency char(1)`. A lower-case `m` satisfies every type assertion and
      // matches no arm of the fee-schedule switch, so a paid role would be charged on the wrong
      // cycle with nothing downstream to reveal it.
      const decode = oneOf(['D', 'W', 'M', 'Y'] as const);

      expect(violationFrom(decode, 'm').expected).toContain("'M'");
      expect(violationFrom(decode, 'Monthly').path).toBe('v');
      expect(violationFrom(decode, 2).received).toBe('a number');
    });

    it('names the permitted codes, which are contract vocabulary rather than data', () => {
      const violation = violationFrom(oneOf(['Ascending', 'Descending'] as const), 'up');

      expect(violation.expected).toBe("one of 'Ascending', 'Descending'");
    });
  });

  describe('oneOfNumber', () => {
    it('admits every code in the set, zero included', () => {
      const decode = oneOfNumber([0, 1, 2]);

      expect(decode(0, 'v')).toBe(0);
      expect(decode(2, 'v')).toBe(2);
    });

    it('refuses an unrecognised code rather than coercing it to the zero member', () => {
      // ⚠ ZERO IS THE PERMISSIVE MEMBER of two of the three enumerations this serves —
      // `NoRegistration` and `None` — so coercing would present a portal as accepting no
      // registrations, or a module as unrestricted, on the strength of a code this client simply
      // did not know.
      const decode = oneOfNumber([0, 1, 2]);

      expect(violationFrom(decode, 7).expected).toBe('one of 0, 1, 2');
      expect(violationFrom(decode, '0').received).toBe('a string');
      expect(violationFrom(decode, 1.5).expected).toBe('an integer');
    });
  });

  describe('objectOf', () => {
    it('decodes every declared member and names a violation by member', () => {
      expect(decodeRow(ROW, 'v')).toEqual(ROW);
      expect(violationFrom(decodeRow, { ...ROW, id: 'x' }).path).toBe('v.id');
      expect(violationFrom(decodeRow, { ...ROW, parentId: '' }).path).toBe('v.parentId');
    });

    it('refuses an ABSENT member, because the server omits nothing', () => {
      const malformed: Record<string, unknown> = { ...ROW };

      delete malformed['name'];

      const violation = violationFrom(decodeRow, malformed);

      expect(violation.path).toBe('v.name');
      expect(violation.received).toBe('nothing');
    });

    it('IGNORES a member the server added that the client does not declare', () => {
      // Additive server changes must not turn every client into a release blocker. What matters
      // is that everything this client READS is what this client declared.
      expect(decodeRow({ ...ROW, addedLater: 'ignored' }, 'v')).toEqual(ROW);
    });

    it('nests member paths, so a violation deep in a graph names its own position', () => {
      const decodeParent = objectOf<{ readonly child: Row }>({ child: decodeRow });

      expect(violationFrom(decodeParent, { child: { ...ROW, retired: 'no' } }).path).toBe(
        'v.child.retired',
      );
    });
  });

  describe('decodeApiMeta', () => {
    it('admits an empty first page, whose every coordinate is zero', () => {
      // ⚠ NO COORDINATE MAY BE VALIDATED BY TRUTHINESS. `totalCount: 0` and `pageIndex: 0` are
      // exactly what a real empty first page looks like.
      expect(decodeApiMeta({ totalCount: 0, pageIndex: 0, pageSize: 25, totalPages: 0 }, 'm')).toEqual(
        { totalCount: 0, pageIndex: 0, pageSize: 25, totalPages: 0 },
      );
    });

    it('derives the page count when the server omits it', () => {
      expect(decodeApiMeta({ totalCount: 21, pageIndex: 0, pageSize: 10 }, 'm').totalPages).toBe(3);
      expect(
        decodeApiMeta({ totalCount: 21, pageIndex: 0, pageSize: 10, totalPages: null }, 'm')
          .totalPages,
      ).toBe(3);
    });

    it('yields zero pages for a page size of zero rather than dividing by it', () => {
      // A page size of zero is a legitimate reply to an unpaged query, so it is handled rather
      // than refused — and it must not produce `Infinity` for a pager to count up to.
      expect(decodeApiMeta({ totalCount: 5, pageIndex: 0, pageSize: 0 }, 'm').totalPages).toBe(0);
    });

    it('refuses an absent coordinate', () => {
      expect(violationFrom(decodeApiMeta, { pageIndex: 0, pageSize: 25 }).path).toBe('v.totalCount');
    });
  });

  describe('decodePageStructure', () => {
    it('admits a real empty page', () => {
      const page = decodePageStructure(
        { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize: 25, totalPages: 0 } },
        'p',
      );

      expect(page.items).toEqual([]);
      expect(page.meta.totalCount).toBe(0);
    });

    it('refuses a page with NO metadata rather than treating it as an empty one', () => {
      // ⚠ THE DEFECT THIS EXISTS TO PREVENT, AND THE WORST FAILURE MODE IN THE WORKSPACE. A
      // page whose metadata was absent used to normalise to a total of zero and no rows: a
      // SUCCESSFUL response stating that the installation has no portals, no accounts and no
      // roles. An operator would conclude the records were gone.
      expect(violationFrom((v, p) => decodePageStructure(v, p), { items: [] }).path).toBe('v.meta');
    });

    it('refuses a page with no items member, which is not the same as an empty page', () => {
      expect(
        violationFrom((v, p) => decodePageStructure(v, p), {
          meta: { totalCount: 0, pageIndex: 0, pageSize: 25, totalPages: 0 },
        }).path,
      ).toBe('v.items');
    });
  });

  describe('pageOf', () => {
    it('decodes every row and names a violation by row index and member', () => {
      const decode = pageOf(decodeRow);
      const meta = { totalCount: 2, pageIndex: 0, pageSize: 25, totalPages: 1 };

      expect(decode({ items: [ROW], meta }, 'p').items).toEqual([ROW]);
      expect(violationFrom(decode, { items: [ROW, { ...ROW, id: null }], meta }).path).toBe(
        'v.items[1].id',
      );
    });
  });

  describe('envelopeOf', () => {
    it('returns the payload with the envelope stripped', () => {
      expect(envelopeOf(decodeRow)({ data: ROW, meta: null }, 'r')).toEqual(ROW);
    });

    it('refuses a body carrying the payload at the TOP LEVEL rather than inside `data`', () => {
      // The quietest defect this catches: a body that is a valid row but is not wrapped. Every
      // member would have read as `undefined` behind a 200.
      expect(violationFrom(envelopeOf(decodeRow), ROW).path).toBe('v.data');
    });

    it('tolerates an ABSENT envelope metadata member', () => {
      // Asymmetric with a page on purpose. This member is pure framing that the caller never
      // sees — it is read only as a drift signal — so refusing an otherwise perfect payload over
      // a discarded member would make this client a blocker on a legitimate serialiser change,
      // for no detection gained: an unwrapped body is caught by `data`, above, which reports the
      // more precise diagnosis anyway. A PAGE's metadata stays required, because there the total
      // and the coordinates ARE the page.
      expect(envelopeOf(decodeRow)({ data: ROW }, 'r')).toEqual(ROW);
    });

    it('still refuses MALFORMED envelope metadata, which signals the wrong envelope', () => {
      expect(violationFrom(envelopeOf(decodeRow), { data: ROW, meta: { totalCount: 'x' } }).path).toBe(
        'v.meta.totalCount',
      );
    });
  });

  describe('responseOf', () => {
    it('returns the envelope WHOLE, for the transports whose signature is the envelope', () => {
      expect(responseOf(decodeRow)({ data: ROW, meta: null }, 'r')).toEqual({
        data: ROW,
        meta: null,
      });
    });

    it('normalises an absent metadata member to null rather than leaving it undefined', () => {
      expect(responseOf(decodeRow)({ data: ROW }, 'r')).toEqual({ data: ROW, meta: null });
    });

    it('carries decoded metadata through rather than discarding it', () => {
      const meta = { totalCount: 3, pageIndex: 0, pageSize: 25, totalPages: 1 };

      expect(responseOf(decodeRow)({ data: ROW, meta }, 'r').meta).toEqual(meta);
    });

    it('validates the payload exactly as the stripping form does', () => {
      expect(violationFrom(responseOf(decodeRow), { data: { ...ROW, id: 'x' }, meta: null }).path).toBe(
        'v.data.id',
      );
    });
  });

  describe('decodeResponse', () => {
    it('applies the decoder under one agreed root name', () => {
      // So that every service reads the same way and every message names the same root, rather
      // than each call site inventing one.
      expect(decodeResponse(decodeRow, ROW)).toEqual(ROW);
      expect(violationFrom((value) => decodeResponse(decodeRow, value), { ...ROW, id: 'x' }).path).toBe(
        'response.id',
      );
    });
  });
});
