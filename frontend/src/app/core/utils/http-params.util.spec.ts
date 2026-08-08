//
// Specification for the query-parameter serialisation helpers of the dnn-migration
// administration front end.
//
// ---------------------------------------------------------------------------
// WHY THIS SUITE EXISTS, AND WHY IT IS PLAIN JASMINE
// ---------------------------------------------------------------------------
// The module under test is a set of pure functions over immutable values. It takes no
// dependency, performs no request and touches no browser API, so nothing here needs a
// testing module, a fixture or a component harness - and importing one would make the
// suite slower and its failures harder to read for no gain. Every expectation below is
// a direct call.
//
// The suite also serves a second, structural purpose. `tsconfig.app.json` compiles by
// IMPORT GRAPH - it declares `files: ["src/main.ts"]` and includes only declaration
// files - so a utility that no service imports yet is silently NOT type-checked by a
// production build. `tsconfig.spec.json` includes `src/**/*.spec.ts`, so this file is
// what puts the module under test into a gated compile. A clean production build alone
// would prove nothing about it.
//
// ---------------------------------------------------------------------------
// WHAT IS ACTUALLY AT RISK, MEASURED
// ---------------------------------------------------------------------------
// The legacy DotNetNuke 4.9.0 VB.NET tree shipped no automated tests of any kind, so
// there is no assertion to port. What there is, is a null contract that makes the
// obvious implementation of a parameter builder WRONG:
//
//   * `Library/Components/Shared/Null.vb` L41-L45 defines the integer "absent" marker
//     as MINUS ONE and L36-L40 defines the 16-bit one identically; L71-L75 defines the
//     string marker as the EMPTY STRING (its body is literally `Return ""`); L76-L80
//     defines the boolean marker as `False`.
//   * `01.00.00.SqlDataProvider` L77 seeds the portal identity at MINUS ONE - so the
//     first portal really is numbered -1 and the second 0 - and L115, L140 and L221
//     seed the role, page and module identities at ZERO.
//   * `Website/admin/Security/Roles.ascx.vb` L112 and L114 give the role-group filter
//     the values -2 and -1, and L129 defaults it to -2.
//
// A builder written as `if (value) { ... }` drops 0, -1, "" and false while looking
// perfectly idiomatic, and the resulting request returns 200 with the wrong records.
// That single failure mode is what the bulk of this suite pins down.
//
// The other three risks pinned here:
//   * the page index is ZERO-BASED on the wire and must not be shifted by one in
//     either direction (`Website/admin/Users/Users.ascx.vb` L51 counted from one and
//     L265/L269/L271/L274 subtracted one before calling down);
//   * search text must reach the wire UNDECORATED, because the trailing per-cent sign
//     the legacy call sites appended (same lines, and `Portals.ascx.vb` L142) is now
//     composed by the repository, and a second one would change which rows match;
//   * the deliberately unpaged endpoints must receive no page coordinate at all.
//

import { HttpParams } from '@angular/common/http';

import type { PagedRequest } from '../models/paged-result.model';

import {
  QUERY_PARAM,
  appendQueryParams,
  emptyQueryParams,
  identifiesAPerson,
  loginParams,
  moduleListParams,
  modulePlacementParams,
  pagedRequestParams,
  permissionListParams,
  portalListParams,
  roleListParams,
  setPagedRequestParams,
  setPagingParams,
  setQueryParam,
  setSortParams,
  toHttpParams,
  userApprovalParams,
  userListParams,
  userSearchBody,
} from './http-params.util';

/** Every paging-related parameter name, for proving an unpaged request carries none. */
const PAGING_PARAM_NAMES: readonly string[] = [
  QUERY_PARAM.pageIndex,
  QUERY_PARAM.pageSize,
  QUERY_PARAM.sortBy,
  QUERY_PARAM.sortDir,
];

/**
 * Spellings from other paging schemes. None may ever appear: this API is offset-paged,
 * and a cursor or a skip-and-take pair would be an unrequested behavioural change.
 */
const FOREIGN_PAGING_PARAM_NAMES: readonly string[] = [
  'cursor',
  'continuationToken',
  'nextPageUrl',
  'previousPageUrl',
  'skip',
  'take',
  'offset',
  'limit',
  'page',
  'currentpage',
  'filter',
  'filterProperty',
];

/** Asserts that not one paging coordinate reached the request. */
function expectNoPagingParams(params: HttpParams): void {
  for (const name of PAGING_PARAM_NAMES) {
    expect(params.has(name)).withContext(`paging parameter "${name}"`).toBe(false);
  }
}

/** Asserts that no alien paging spelling reached the request. */
function expectOffsetPagingOnly(params: HttpParams): void {
  for (const name of FOREIGN_PAGING_PARAM_NAMES) {
    expect(params.has(name)).withContext(`foreign parameter "${name}"`).toBe(false);
  }
}

describe('http-params.util', () => {
  describe('setQueryParam - sentinel fidelity', () => {
    it('transmits zero, because the role, page and module identities are seeded at zero', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.tabId, 0);

      expect(params.has(QUERY_PARAM.tabId)).toBe(true);
      expect(params.get(QUERY_PARAM.tabId)).toBe('0');
      expect(params.toString()).toBe('tabId=0');
    });

    it('transmits minus one, because the portal identity is seeded at minus one', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.portalId, -1);

      expect(params.get(QUERY_PARAM.portalId)).toBe('-1');
    });

    it('transmits minus two, the role-group choice meaning "all roles"', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.roleGroupId, -2);

      expect(params.get(QUERY_PARAM.roleGroupId)).toBe('-2');
    });

    it('transmits an explicitly supplied empty string rather than treating it as absent', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.query, '');

      expect(params.has(QUERY_PARAM.query)).toBe(true);
      expect(params.get(QUERY_PARAM.query)).toBe('');
    });

    it('transmits false, which is a request for the unapproved accounts and not an absence', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.isApproved, false);

      expect(params.get(QUERY_PARAM.isApproved)).toBe('false');
    });

    it('transmits true as the lower-case token the boolean binder accepts', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.includeDeleted, true);

      expect(params.get(QUERY_PARAM.includeDeleted)).toBe('true');
    });
  });

  describe('setQueryParam - omission', () => {
    it('omits an undefined value', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.tabId, undefined);

      expect(params.has(QUERY_PARAM.tabId)).toBe(false);
      expect(params.keys()).toEqual([]);
      expect(params.toString()).toBe('');
    });

    it('omits a null value, which is what a reset form control yields', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.roleGroupId, null);

      expect(params.has(QUERY_PARAM.roleGroupId)).toBe(false);
      expect(params.toString()).toBe('');
    });

    it('returns the very same instance when the value is absent, so nothing is rebuilt', () => {
      const original = new HttpParams().set(QUERY_PARAM.pageIndex, '0');
      const result = setQueryParam(original, QUERY_PARAM.tabId, undefined);

      expect(result).toBe(original);
    });

    it('never emits the literal text "undefined" as a value', () => {
      const params = appendQueryParams(new HttpParams(), {
        [QUERY_PARAM.name]: undefined,
        [QUERY_PARAM.email]: null,
      });

      expect(params.toString()).not.toContain('undefined');
      expect(params.toString()).not.toContain('null');
    });
  });

  describe('setQueryParam - mechanics', () => {
    it('leaves the supplied parameter set untouched', () => {
      const original = new HttpParams();

      setQueryParam(original, QUERY_PARAM.tabId, 7);

      expect(original.has(QUERY_PARAM.tabId)).toBe(false);
    });

    it('replaces rather than appends, so composing the same name twice yields one pair', () => {
      const once = setQueryParam(new HttpParams(), QUERY_PARAM.pageIndex, 0);
      const twice = setQueryParam(once, QUERY_PARAM.pageIndex, 3);

      expect(twice.getAll(QUERY_PARAM.pageIndex)).toEqual(['3']);
      expect(twice.toString()).toBe('pageIndex=3');
    });

    it('transmits a non-finite number verbatim so the server reports it, rather than dropping it', () => {
      const params = setQueryParam(new HttpParams(), QUERY_PARAM.pageIndex, Number.NaN);

      expect(params.has(QUERY_PARAM.pageIndex)).toBe(true);
      expect(params.get(QUERY_PARAM.pageIndex)).toBe('NaN');
    });

    it('rejects a blank parameter name loudly instead of producing a nameless pair', () => {
      expect(() => setQueryParam(new HttpParams(), '', 1)).toThrowError(RangeError);
      expect(() => setQueryParam(new HttpParams(), '   ', 1)).toThrowError(RangeError);
    });
  });

  describe('appendQueryParams and toHttpParams', () => {
    it('keeps the present members and drops only the absent ones', () => {
      const params = toHttpParams({
        [QUERY_PARAM.pageIndex]: 0,
        [QUERY_PARAM.pageSize]: 10,
        [QUERY_PARAM.sortBy]: undefined,
        [QUERY_PARAM.query]: '',
        [QUERY_PARAM.isApproved]: false,
        [QUERY_PARAM.tabId]: null,
      });

      expect(params.keys().sort()).toEqual(['isApproved', 'pageIndex', 'pageSize', 'query']);
      expect(params.get(QUERY_PARAM.pageIndex)).toBe('0');
      expect(params.get(QUERY_PARAM.query)).toBe('');
      expect(params.get(QUERY_PARAM.isApproved)).toBe('false');
    });

    it('produces an empty set from an empty map', () => {
      expect(toHttpParams({}).keys()).toEqual([]);
    });
  });

  describe('paging - zero-based, never defaulted, offset only', () => {
    it('transmits page index zero, the first page, without shifting it', () => {
      const params = setPagingParams(new HttpParams(), { pageIndex: 0, pageSize: 10 });

      expect(params.get(QUERY_PARAM.pageIndex)).toBe('0');
      expect(params.get(QUERY_PARAM.pageSize)).toBe('10');
    });

    it('does not translate a page index in either direction', () => {
      const params = setPagingParams(new HttpParams(), { pageIndex: 4 });

      expect(params.get(QUERY_PARAM.pageIndex)).toBe('4');
    });

    it('omits an absent page size instead of substituting a literal', () => {
      const params = setPagingParams(new HttpParams(), { pageIndex: 2 });

      expect(params.has(QUERY_PARAM.pageSize)).toBe(false);
      expect(params.toString()).not.toContain('pageSize');
    });

    it('never emits an alien paging spelling', () => {
      const params = pagedRequestParams({ pageIndex: 1, pageSize: 25 });

      expectOffsetPagingOnly(params);
    });
  });

  describe('sorting - the direction token reaches the wire verbatim', () => {
    it('sends the ascending token with its server spelling', () => {
      const params = setSortParams(new HttpParams(), {
        sortBy: 'portalName',
        sortDir: 'Ascending',
      });

      expect(params.get(QUERY_PARAM.sortBy)).toBe('portalName');
      expect(params.get(QUERY_PARAM.sortDir)).toBe('Ascending');
    });

    it('sends the descending token with its server spelling', () => {
      const params = setSortParams(new HttpParams(), { sortDir: 'Descending' });

      expect(params.get(QUERY_PARAM.sortDir)).toBe('Descending');
    });

    it('omits both members when neither is supplied', () => {
      const params = setSortParams(new HttpParams(), {});

      expect(params.keys()).toEqual([]);
    });
  });

  describe('filter text - transmitted raw, never decorated', () => {
    it('appends no per-cent sign to the filter text', () => {
      const params = setPagedRequestParams(new HttpParams(), { query: 'admin' });

      expect(params.get(QUERY_PARAM.query)).toBe('admin');
    });

    it('preserves surrounding white space and letter case exactly as typed', () => {
      const params = pagedRequestParams({ query: '  Admin User  ' });

      expect(params.get(QUERY_PARAM.query)).toBe('  Admin User  ');
    });

    it('carries a per-cent sign the user typed through untouched, neither doubled nor stripped', () => {
      const typed = '50%';
      const params = pagedRequestParams({ query: typed });

      expect(params.get(QUERY_PARAM.query)).toBe(typed);
    });

    it('accepts a fully populated paged request from the shared contract', () => {
      const request: PagedRequest = {
        pageIndex: 0,
        pageSize: 10,
        sortBy: 'userName',
        sortDir: 'Ascending',
        query: 'a',
      };

      const params = pagedRequestParams(request);

      expect(params.keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
        'query',
        'sortBy',
        'sortDir',
      ]);
    });
  });

  describe('portal listing', () => {
    it('adds the dedicated name filter alongside the paging contract', () => {
      const params = portalListParams({ pageIndex: 0, pageSize: 10 }, { name: 'Base' });

      expect(params.get(QUERY_PARAM.name)).toBe('Base');
      expect(params.get(QUERY_PARAM.pageIndex)).toBe('0');
    });

    it('emits no name filter when none is supplied, whether omitted or null', () => {
      expect(portalListParams({ pageIndex: 0 }).has(QUERY_PARAM.name)).toBe(false);
      expect(portalListParams({ pageIndex: 0 }, null).has(QUERY_PARAM.name)).toBe(false);
      expect(portalListParams({ pageIndex: 0 }, undefined).has(QUERY_PARAM.name)).toBe(false);
    });

    it('transmits an explicitly empty name filter', () => {
      const params = portalListParams({}, { name: '' });

      expect(params.get(QUERY_PARAM.name)).toBe('');
    });
  });

  describe('account listing - the three legacy search modes stay distinct', () => {
    it('serialises the account-name mode', () => {
      const params = userListParams({ pageIndex: 0 }, { userName: 'ad' });

      expect(params.get(QUERY_PARAM.userName)).toBe('ad');
      expect(params.has(QUERY_PARAM.email)).toBe(false);
      expect(params.has(QUERY_PARAM.profilePropertyName)).toBe(false);
    });

    it('serialises the address mode', () => {
      const params = userListParams({ pageIndex: 0 }, { email: 'admin@' });

      expect(params.get(QUERY_PARAM.email)).toBe('admin@');
      expect(params.has(QUERY_PARAM.userName)).toBe(false);
    });

    it('serialises the arbitrary profile-property mode as a name and a value', () => {
      const params = userListParams(
        { pageIndex: 0 },
        { profilePropertyName: 'City', profilePropertyValue: 'Lon' },
      );

      expect(params.get(QUERY_PARAM.profilePropertyName)).toBe('City');
      expect(params.get(QUERY_PARAM.profilePropertyValue)).toBe('Lon');
    });

    it('transmits an approval filter of false, and does not mistake it for an absence', () => {
      const params = userListParams({ pageIndex: 0 }, { isApproved: false });

      expect(params.get(QUERY_PARAM.isApproved)).toBe('false');
    });

    it('forwards a property name without a value, leaving the conflict for the server to report', () => {
      const params = userListParams({}, { profilePropertyName: 'City' });

      expect(params.get(QUERY_PARAM.profilePropertyName)).toBe('City');
      expect(params.has(QUERY_PARAM.profilePropertyValue)).toBe(false);
    });

    it('emits only the paging contract when no filter is supplied, whether omitted or null', () => {
      expect(userListParams({ pageIndex: 0, pageSize: 10 }).keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
      ]);
      expect(userListParams({ pageIndex: 0, pageSize: 10 }, null).keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
      ]);
    });
  });

  describe('account approval', () => {
    it('transmits a required false, which is the whole point of the endpoint', () => {
      expect(userApprovalParams(false).toString()).toBe('isApproved=false');
    });

    it('transmits a required true', () => {
      expect(userApprovalParams(true).toString()).toBe('isApproved=true');
    });
  });

  describe('role listing', () => {
    it('transmits the "global roles" group filter of minus one', () => {
      const params = roleListParams({ pageIndex: 0 }, { roleGroupId: -1 });

      expect(params.get(QUERY_PARAM.roleGroupId)).toBe('-1');
    });

    it('transmits the "all roles" group filter of minus two', () => {
      const params = roleListParams({ pageIndex: 0 }, { roleGroupId: -2 });

      expect(params.get(QUERY_PARAM.roleGroupId)).toBe('-2');
    });

    it('transmits a group filter of zero, a real group identifier', () => {
      const params = roleListParams({}, { roleGroupId: 0 });

      expect(params.get(QUERY_PARAM.roleGroupId)).toBe('0');
    });

    it('omits the group filter when it is null', () => {
      const params = roleListParams({ pageIndex: 0 }, { roleGroupId: null });

      expect(params.has(QUERY_PARAM.roleGroupId)).toBe(false);
    });

    it('transmits the ungrouped scope, which is the successor to the legacy "global roles" choice', () => {
      const params = roleListParams({ pageIndex: 0 }, { scope: 'Ungrouped' });

      expect(params.get(QUERY_PARAM.scope)).toBe('Ungrouped');
      expect(params.has(QUERY_PARAM.roleGroupId)).toBe(false);
    });

    it('transmits the all scope by name rather than as the legacy magic integer', () => {
      const params = roleListParams({ pageIndex: 0 }, { scope: 'All' });

      expect(params.get(QUERY_PARAM.scope)).toBe('All');
      expect(params.toString()).not.toContain('-2');
    });

    it('omits the scope when it is null or absent', () => {
      expect(roleListParams({ pageIndex: 0 }, { scope: null }).has(QUERY_PARAM.scope)).toBe(false);
      expect(roleListParams({ pageIndex: 0 }, { roleGroupId: 3 }).has(QUERY_PARAM.scope)).toBe(false);
    });

    it('serialises a group identifier and a scope together, which the server adjudicates', () => {
      const params = roleListParams({ pageIndex: 0 }, { roleGroupId: 3, scope: 'All' });

      expect(params.get(QUERY_PARAM.roleGroupId)).toBe('3');
      expect(params.get(QUERY_PARAM.scope)).toBe('All');
    });

    it('emits only the paging contract when no filter is supplied, whether omitted or null', () => {
      expect(roleListParams({ pageIndex: 0, pageSize: 10 }).keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
      ]);
      expect(roleListParams({ pageIndex: 0, pageSize: 10 }, null).keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
      ]);
      expect(roleListParams({}).keys()).toEqual([]);
    });
  });

  describe('module listing', () => {
    it('transmits a page filter of zero, a real page identifier', () => {
      const params = moduleListParams({ pageIndex: 0 }, { tabId: 0 });

      expect(params.get(QUERY_PARAM.tabId)).toBe('0');
    });

    it('transmits an explicit recycle-bin restriction of false', () => {
      const params = moduleListParams({ pageIndex: 0 }, { includeDeleted: false });

      expect(params.get(QUERY_PARAM.includeDeleted)).toBe('false');
    });

    it('omits both filters when neither is supplied, whether omitted or null', () => {
      expect(moduleListParams({ pageIndex: 0, pageSize: 10 }).keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
      ]);
      expect(moduleListParams({ pageIndex: 0, pageSize: 10 }, null).keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
      ]);
    });
  });

  describe('module placement selection', () => {
    it('serialises the placement identifier', () => {
      expect(modulePlacementParams({ tabModuleId: 12 }).toString()).toBe('tabModuleId=12');
    });

    it('emits nothing when no placement is named, addressing the module itself', () => {
      expect(modulePlacementParams().keys()).toEqual([]);
      expect(modulePlacementParams(null).keys()).toEqual([]);
      expect(modulePlacementParams({}).keys()).toEqual([]);
      expect(modulePlacementParams({ tabModuleId: undefined }).keys()).toEqual([]);
      expect(modulePlacementParams({ tabModuleId: null }).keys()).toEqual([]);
    });

    it('carries no paging coordinate, because the operation addresses one record', () => {
      expectNoPagingParams(modulePlacementParams({ tabModuleId: 3 }));
    });
  });

  describe('permission catalogue - deliberately unpaged', () => {
    it('serialises all three filters', () => {
      const params = permissionListParams({
        permissionCode: 'SYSTEM_MODULE_DEFINITION',
        moduleDefinitionId: 5,
        permissionKey: 'EDIT',
      });

      expect(params.get(QUERY_PARAM.permissionCode)).toBe('SYSTEM_MODULE_DEFINITION');
      expect(params.get(QUERY_PARAM.moduleDefinitionId)).toBe('5');
      expect(params.get(QUERY_PARAM.permissionKey)).toBe('EDIT');
    });

    it('serialises the key filter on its own, which the server answers from the closed key set', () => {
      const params = permissionListParams({ permissionKey: 'VIEW' });

      expect(params.get(QUERY_PARAM.permissionKey)).toBe('VIEW');
      expect(params.keys()).toEqual([QUERY_PARAM.permissionKey]);
    });

    it('emits nothing at all when no filter is supplied', () => {
      expect(permissionListParams().keys()).toEqual([]);
      expect(permissionListParams(null).keys()).toEqual([]);
      expect(permissionListParams({}).keys()).toEqual([]);
      expect(
        permissionListParams({ permissionCode: null, moduleDefinitionId: null, permissionKey: null }).keys(),
      ).toEqual([]);
    });

    it('never emits a paging coordinate', () => {
      expectNoPagingParams(permissionListParams({ permissionCode: 'EDIT' }));
      expectNoPagingParams(permissionListParams());
    });
  });

  describe('sign-in portal selection', () => {
    it('transmits a portal of minus one, the first portal in the seeded identity', () => {
      expect(loginParams({ portalId: -1 }).toString()).toBe('portalId=-1');
    });

    it('transmits a portal of zero, the second portal in the seeded identity', () => {
      expect(loginParams({ portalId: 0 }).toString()).toBe('portalId=0');
    });

    it('emits nothing when the request host is expected to resolve the portal', () => {
      expect(loginParams().keys()).toEqual([]);
      expect(loginParams(null).keys()).toEqual([]);
      expect(loginParams({}).keys()).toEqual([]);
      expect(loginParams({ portalId: null }).keys()).toEqual([]);
      expect(loginParams({ portalId: undefined }).keys()).toEqual([]);
    });
  });

  describe('the deliberately unpaged endpoints receive no parameters', () => {
    it('emptyQueryParams carries nothing', () => {
      const params = emptyQueryParams();

      expect(params.keys()).toEqual([]);
      expect(params.toString()).toBe('');
      expectNoPagingParams(params);
      expectOffsetPagingOnly(params);
    });

    it('returns a fresh instance each time, so no state is shared at import', () => {
      expect(emptyQueryParams()).not.toBe(emptyQueryParams());
    });
  });

  // -------------------------------------------------------------------------
  // WHICH TRANSPORT AN ACCOUNT SEARCH TAKES
  // -------------------------------------------------------------------------
  // This is the CWE-598 compensator, and its correctness is entirely a property of the
  // predicate: a value classified as non-identifying travels in the request target, which
  // is written to browser history, to every proxy access log and to the server's own access
  // log - all of them at an END of the encrypted channel, so transport security does not
  // reach them.
  //
  // The predicate applies two DIFFERENT tests and the asymmetry is the substance of these
  // cases. The four NAMED filters are present only when a search mode naming a person has
  // been chosen, so their mere presence is the signal and testing their content would flip
  // the transport for the single input an operator produces by clearing the box. The paging
  // contract's GENERIC `query` is present on every listing and may legitimately be blank, so
  // presence says nothing and non-blankness is the signal. Using one test for all five
  // breaks one of the two cases, whichever test is chosen.
  describe('identifiesAPerson - which searches must leave the request target', () => {
    it('classifies a listing that names nobody as non-identifying', () => {
      expect(identifiesAPerson()).toBe(false);
      expect(identifiesAPerson(null)).toBe(false);
      expect(identifiesAPerson({})).toBe(false);
    });

    it('leaves an approval-only restriction on the cacheable GET, in both states', () => {
      expect(identifiesAPerson({ isApproved: true })).toBe(false);
      expect(identifiesAPerson({ isApproved: false })).toBe(false);
    });

    it('classifies each named filter as identifying by PRESENCE, not by content', () => {
      expect(identifiesAPerson({ userName: 'ada' })).toBe(true);
      expect(identifiesAPerson({ userName: '' })).toBe(true);
      expect(identifiesAPerson({ email: 'ada@example.test' })).toBe(true);
      expect(identifiesAPerson({ email: '' })).toBe(true);
      expect(identifiesAPerson({ profilePropertyName: 'NationalId' })).toBe(true);
      expect(identifiesAPerson({ profilePropertyName: '' })).toBe(true);
      expect(identifiesAPerson({ profilePropertyValue: '0123456789' })).toBe(true);
      expect(identifiesAPerson({ profilePropertyValue: '' })).toBe(true);
    });

    it('treats an explicitly null named filter as absent, which a reset control produces', () => {
      expect(identifiesAPerson({ userName: null })).toBe(false);
      expect(identifiesAPerson({ email: null })).toBe(false);
      expect(identifiesAPerson({ profilePropertyName: null, profilePropertyValue: null })).toBe(
        false,
      );
    });

    // The finding this pins: the generic member is matched by the server as a SUBSTRING across
    // the login name, the display name AND the electronic-mail address, so a search through it
    // reaches the same rows the named filters reach. It used to be ignored here entirely, so a
    // query-only search stayed on the GET and put the identifier in the request target.
    it('classifies a non-blank generic query as identifying', () => {
      expect(identifiesAPerson({ query: 'ada' })).toBe(true);
      expect(identifiesAPerson({ query: 'ada@example.test' })).toBe(true);
      expect(identifiesAPerson({ query: 'Ada Lovelace' })).toBe(true);
      expect(identifiesAPerson({ query: 'a' })).toBe(true);
    });

    it('leaves a blank or absent generic query on the cacheable GET', () => {
      expect(identifiesAPerson({ query: '' })).toBe(false);
      expect(identifiesAPerson({ query: '   ' })).toBe(false);
      expect(identifiesAPerson({ query: '\t\n' })).toBe(false);
      expect(identifiesAPerson({ query: null })).toBe(false);
      expect(identifiesAPerson({ query: undefined })).toBe(false);
    });

    it('classifies a search carrying both kinds of term as identifying', () => {
      expect(identifiesAPerson({ query: 'ada', isApproved: true })).toBe(true);
      expect(identifiesAPerson({ query: '  ', userName: 'ada' })).toBe(true);
    });
  });

  describe('userSearchBody - the body that carries what the target must not', () => {
    it('carries the paging contract and the filters together, omitting nothing supplied', () => {
      const body = userSearchBody(
        { pageIndex: 2, pageSize: 25, sortBy: 'username', sortDir: 'Descending', query: 'ada' },
        { userName: 'ada', isApproved: false },
      );

      expect(body).toEqual({
        pageIndex: 2,
        pageSize: 25,
        sortBy: 'username',
        sortDir: 'Descending',
        query: 'ada',
        userName: 'ada',
        isApproved: false,
      });
    });

    // The sort direction is sent as the MEMBER NAME, which is the vocabulary the query string
    // accepts. The server binds this body with a converter pinned to the same names, so one
    // contract member has one spelling on both transports; sending the ordinal here instead
    // would give it two.
    it('names the sort direction rather than numbering it', () => {
      expect(userSearchBody({ sortBy: 'username', sortDir: 'Ascending' }).sortDir).toBe('Ascending');
      expect(userSearchBody({ sortBy: 'username', sortDir: 'Descending' }).sortDir).toBe(
        'Descending',
      );
    });

    it('transmits the paging sentinels a truthiness test would drop', () => {
      const body = userSearchBody({ pageIndex: 0, pageSize: 10 }, { isApproved: false, email: '' });

      expect(body.pageIndex).toBe(0);
      expect(body.isApproved).toBe(false);
      expect(body.email).toBe('');
    });

    it('omits an absent member rather than stating it as null', () => {
      const body = userSearchBody({ pageIndex: 0, pageSize: 10 }, { userName: 'ada' });

      expect(Object.keys(body).sort()).toEqual(['pageIndex', 'pageSize', 'userName']);
      expect('email' in body).toBe(false);
      expect('profilePropertyName' in body).toBe(false);
      expect('sortDir' in body).toBe(false);
    });

    it('accepts an omitted or null filter, which an unfiltered search produces', () => {
      expect(userSearchBody({ pageIndex: 0, pageSize: 10 })).toEqual({ pageIndex: 0, pageSize: 10 });
      expect(userSearchBody({ pageIndex: 0, pageSize: 10 }, null)).toEqual({
        pageIndex: 0,
        pageSize: 10,
      });
    });
  });

  describe('QUERY_PARAM registry', () => {
    it('spells every name exactly as the server binds it', () => {
      expect(QUERY_PARAM.pageIndex).toBe('pageIndex');
      expect(QUERY_PARAM.pageSize).toBe('pageSize');
      expect(QUERY_PARAM.sortBy).toBe('sortBy');
      expect(QUERY_PARAM.sortDir).toBe('sortDir');
      expect(QUERY_PARAM.query).toBe('query');
      expect(QUERY_PARAM.name).toBe('name');
      expect(QUERY_PARAM.userName).toBe('userName');
      expect(QUERY_PARAM.email).toBe('email');
      expect(QUERY_PARAM.profilePropertyName).toBe('profilePropertyName');
      expect(QUERY_PARAM.profilePropertyValue).toBe('profilePropertyValue');
      expect(QUERY_PARAM.isApproved).toBe('isApproved');
      expect(QUERY_PARAM.roleGroupId).toBe('roleGroupId');
      expect(QUERY_PARAM.tabId).toBe('tabId');
      expect(QUERY_PARAM.includeDeleted).toBe('includeDeleted');
      expect(QUERY_PARAM.tabModuleId).toBe('tabModuleId');
      expect(QUERY_PARAM.permissionCode).toBe('permissionCode');
      expect(QUERY_PARAM.moduleDefinitionId).toBe('moduleDefinitionId');
      expect(QUERY_PARAM.portalId).toBe('portalId');
    });

    it('is frozen, so the registry cannot be edited at run time', () => {
      expect(Object.isFrozen(QUERY_PARAM)).toBe(true);
    });
  });
});
