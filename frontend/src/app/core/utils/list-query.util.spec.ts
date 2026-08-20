import { convertToParamMap } from '@angular/router';

import {
  addressStatesQuery,
  FILTER_PARAM,
  FIRST_PAGE_INDEX,
  firstPageParameter,
  PAGE_PARAM,
  PAGE_SIZE_PARAM,
  parsePageIndex,
  parsePageSize,
  parseSortDirection,
  parseSortKey,
  SORT_BY_PARAM,
  SORT_DIR_PARAM,
} from './list-query.util';

import type { ParamMap, Params } from '@angular/router';

/** The shared address vocabulary for paged listings. */
describe('list-query.util', () => {
  /**
   * Builds a route parameter map from plain parameters.
   *
   * @param params The parameters the address carries.
   * @returns The map the readers consume.
   */
  function address(params: Params): ParamMap {
    return convertToParamMap(params);
  }

  describe('the shared vocabulary', () => {
    it('spells the five parameters the way the legacy addresses did', () => {
      expect(FILTER_PARAM).toBe('filter');
      expect(PAGE_PARAM).toBe('currentpage');
      expect(PAGE_SIZE_PARAM).toBe('pagesize');
      expect(SORT_BY_PARAM).toBe('sortby');
      expect(SORT_DIR_PARAM).toBe('sortdir');
    });

    it('counts the first page from nought', () => {
      expect(FIRST_PAGE_INDEX).toBe(0);
    });
  });

  describe('parsePageIndex', () => {
    it('converts the one-based address value to a nought-based index', () => {
      // The whole point of the parameter: page one in the address is index nought in the store.
      expect(parsePageIndex('1')).toBe(0);
      expect(parsePageIndex('2')).toBe(1);
      expect(parsePageIndex('26')).toBe(25);
    });

    it('resolves an absent value to the first page', () => {
      expect(parsePageIndex(null)).toBe(FIRST_PAGE_INDEX);
    });

    it('resolves a value that names no page at all to the first page', () => {
      // Distinct from an out-of-range page, which IS a real coordinate the server answers with an empty
      // page and a total. None of these names a page, so there is nothing to ask for.
      expect(parsePageIndex('abc')).toBe(FIRST_PAGE_INDEX);
      expect(parsePageIndex('')).toBe(FIRST_PAGE_INDEX);
      expect(parsePageIndex('1.5')).toBe(FIRST_PAGE_INDEX);
      expect(parsePageIndex('0')).toBe(FIRST_PAGE_INDEX);
      expect(parsePageIndex('-3')).toBe(FIRST_PAGE_INDEX);
    });

    it('forwards a page beyond the end rather than clamping it', () => {
      // NEGATIVE CONTROL for the case above: a large page is usable and must survive, because the
      // listings have a surface for "this page lies past the end of the match set".
      expect(parsePageIndex('9999')).toBe(9998);
    });
  });

  describe('parsePageSize', () => {
    it('reads a positive whole size', () => {
      expect(parsePageSize('25')).toBe(25);
    });

    it('expresses no preference for an absent or blank value', () => {
      expect(parsePageSize(null)).toBeNull();
      expect(parsePageSize('')).toBeNull();
      expect(parsePageSize('   ')).toBeNull();
    });

    it('refuses nought rather than forwarding it', () => {
      // Nought means "no preference" to the stores and "every match" to the endpoints. Forwarding it from
      // an address would make one address mean two different things in two places.
      expect(parsePageSize('0')).toBeNull();
    });

    it('refuses a negative or fractional size', () => {
      expect(parsePageSize('-10')).toBeNull();
      expect(parsePageSize('7.5')).toBeNull();
      expect(parsePageSize('lots')).toBeNull();
    });
  });

  describe('parseSortKey', () => {
    const admitted: readonly string[] = Object.freeze(['portalId', 'portalName', 'expiryDate']);

    it("answers in the KEY'S spelling however the address was cased", () => {
      // The grid finds its active heading by an exact comparison, so a match must be normalised to the
      // key rather than echoed back as the address happened to spell it.
      expect(parseSortKey('portalname', admitted)).toBe('portalName');
      expect(parseSortKey('PORTALNAME', admitted)).toBe('portalName');
      expect(parseSortKey('  PortalName  ', admitted)).toBe('portalName');
    });

    it('admits nothing outside the set it was given', () => {
      // A column the endpoint does not order by produces a refused request naming the admitted set, so an
      // address asking for one is treated as asking for nothing.
      expect(parseSortKey('description', admitted)).toBeNull();
      expect(parseSortKey('', admitted)).toBeNull();
      expect(parseSortKey(null, admitted)).toBeNull();
    });

    it('takes the admitted set from its caller rather than holding one', () => {
      // Proves the set is genuinely a parameter: the same raw value resolves against one caller's columns
      // and not against another's. Without this the shared reader could carry one listing's set.
      expect(parseSortKey('title', ['title', 'moduleId'])).toBe('title');
      expect(parseSortKey('title', admitted)).toBeNull();
    });
  });

  describe('parseSortDirection', () => {
    it("reads the server's own spelling", () => {
      expect(parseSortDirection('Ascending')).toBe('Ascending');
      expect(parseSortDirection('descending')).toBe('Descending');
    });

    it('reads the abbreviation an operator is likely to type', () => {
      // This value is as often hand-edited as generated, and the endpoint answers `sortDir=asc` with 400 -
      // so the abbreviation is expanded HERE rather than being forwarded and refused.
      expect(parseSortDirection('asc')).toBe('Ascending');
      expect(parseSortDirection('DESC')).toBe('Descending');
    });

    it("falls back to the server's default for anything else", () => {
      expect(parseSortDirection(null)).toBeNull();
      expect(parseSortDirection('')).toBeNull();
      expect(parseSortDirection('sideways')).toBeNull();
    });
  });

  describe('firstPageParameter', () => {
    it('omits the first page rather than writing it', () => {
      expect(firstPageParameter(FIRST_PAGE_INDEX)).toBeNull();
    });

    it('writes any other page one-based', () => {
      expect(firstPageParameter(1)).toBe('2');
      expect(firstPageParameter(25)).toBe('26');
    });

    it('round-trips with parsePageIndex', () => {
      // The two conversions are the only places the base changes, so they are asserted against each other
      // rather than each against a literal.
      for (const index of [0, 1, 5, 99]) {
        expect(parsePageIndex(firstPageParameter(index))).toBe(index);
      }
    });
  });

  describe('addressStatesQuery', () => {
    it('accepts an address that already carries the canonical form', () => {
      expect(
        addressStatesQuery(address({ currentpage: '3', filter: 'A' }), {
          [PAGE_PARAM]: '3',
          [FILTER_PARAM]: 'A',
        }),
      ).toBeTrue();
    });

    it('rejects an address whose value is not the one that was applied', () => {
      // `currentpage=abc` parses to the first page, whose canonical form omits the parameter - so the
      // address and the screen disagree and the address must be replaced.
      expect(
        addressStatesQuery(address({ currentpage: 'abc' }), { [PAGE_PARAM]: null }),
      ).toBeFalse();
    });

    it('rejects a redundantly stated default', () => {
      expect(addressStatesQuery(address({ currentpage: '1' }), { [PAGE_PARAM]: null })).toBeFalse();
    });

    it('treats an absent parameter and a null canonical value as agreeing', () => {
      expect(addressStatesQuery(address({}), { [PAGE_PARAM]: null, [FILTER_PARAM]: null })).toBeTrue();
    });

    it('examines only the keys the writer produces, leaving foreign parameters alone', () => {
      const canonical: Params = { [PAGE_PARAM]: null };

      expect(
        addressStatesQuery(address({ userId: '7', returnUrl: '/roles' }), canonical),
      ).toBeTrue();
    });

    it('detects a discrepancy in any one key, not merely the first', () => {
      // Guards the reduction: an implementation that returned after the first key would pass the cases
      // above and still miss this.
      expect(
        addressStatesQuery(address({ currentpage: '3', sortdir: 'desc' }), {
          [PAGE_PARAM]: '3',
          [SORT_DIR_PARAM]: 'Descending',
        }),
      ).toBeFalse();
    });
  });
});
