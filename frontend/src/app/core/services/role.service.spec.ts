import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import type { Observable } from 'rxjs';

import { RoleService } from './role.service';
import { PRESENTED_IN_CONTEXT } from './notification.service';
import { isContractViolation } from '../utils/decode.util';

import type {
  ApiResponse,
  PagedResponse,
} from '../models/paged-result.model';
import type {
  BillingFrequency,
  CreateRoleGroupRequest,
  CreateRoleRequest,
  Role,
  RoleAssignmentRequest,
  RoleGroup,
  RoleListItem,
  UpdateRoleGroupRequest,
  UpdateRoleRequest,
  UserRole,
} from '../models/role.model';

/**
 * Specification for `core/services/role.service.ts`.
 *
 * The service wraps thirteen endpoints across two server resources — roles, and the groupings above them —
 * and its whole contract is that each method issues ONE request to ONE address with ONE unmodified body, and
 * returns whatever the HTTP client produced. That contract is almost entirely invisible to the compiler: a
 * method that quietly dropped a falsy query value, lower-cased a persisted code, computed a date, or fetched
 * something first to decide whether to proceed would type-check, bundle and deploy without complaint.
 * Everything below exists to make one of those failures loud.
 *
 * Three construction rules, each of which is easy to get wrong when adding a case:
 *
 * 1. `httpMock.verify()` runs after every case and is the load-bearing assertion of the file rather than a
 *    tidy-up step. It fails on any request opened and never asserted, and the cases asserting that something
 *    did NOT happen — no pre-emptive read before a delete, no second call anywhere — rely on it alone.
 * 2. Every URL is a literal root-relative string beginning `/api/v1/`, never a call into the endpoint module
 *    the service itself uses: asserting against that module would compare the service's output with its own
 *    input and would accept a template addressing `/api/v1/api/v1/roles`. The relative base is also what the
 *    `test` target compiles against, since it declares no environment replacement and the base module is the
 *    production one.
 * 3. Param-carrying requests are matched by predicate, not by string. The testing backend's string matcher
 *    compares url-with-params, so a string matcher would couple the assertion to parameter ORDER and would
 *    stop matching as soon as a parameter was added. The two listings match on method and PATH and inspect
 *    parameters by name; the eleven param-free operations match by exact string.
 *
 * Deliberately not asserted: validation, derived state, ordering, filtering, permission outcomes and date
 * arithmetic. None of that lives in the service, and a test asserting it would describe behaviour the file is
 * forbidden to have. Failures are asserted to PROPAGATE and nothing more. The bearer token, the correlation
 * identifier and the translation of a problem document belong to handlers this test bed deliberately does not
 * register, and two cases assert the ABSENCE of those headers.
 *
 * Three contract facts a reader may expect to be otherwise:
 *
 * - the group listing sends NO portal identifier. `listRoleGroups()` takes no arguments and its route
 *   template is parameterless, because the tenant is resolved server-side, so the case below asserts the
 *   query string is EMPTY.
 * - the two negative filter values are joined by a NAMED scope. The legacy selector encoded three intents in
 *   one integer; the endpoint splits them into a plain nullable group key plus a closed enumeration for the
 *   intent no key can express, and both halves are asserted.
 * - the assignment write answers 204 for both an add and an update, and the service types the success as
 *   nothing at all. Both statuses are exercised, which proves the service does not branch on the status to
 *   re-derive which write occurred.
 *
 * Legacy facts the assertions depend on: `RoleID` and `RoleGroupID` are `IDENTITY (0, 1)` and `PortalID` is
 * `IDENTITY (-1, 1)`, so zero and minus one are REAL keys; the legacy sentinel helper returned -1 for an
 * absent integer and the EMPTY STRING for an absent string, so one value meant both "the first row" and "no
 * row", which is why no falsy value may be dropped; the negative pseudo-role identifiers were declared as
 * STRING constants; and the legacy billing switch covers SIX persisted frequency codes, not four.
 */

// The wire addresses, written out rather than composed.
//
//  Stated as whole literals on purpose. Building them from a shared prefix would make a wrong prefix agree
//  with itself across every case at once, which is precisely the failure the double-prefix hazard produces,
//  and it would also stop these strings being greppable as the wire contract they are.

/**
 * The role collection. Reads a page; accepts a creation.
 */
const ROLES_URL = '/api/v1/roles';

/**
 * One role, addressed with the identity seed itself so a truthiness guard cannot hide.
 */
const ROLE_ZERO_URL = '/api/v1/roles/0';

/**
 * One role, addressed with an ordinary positive key.
 */
const ROLE_URL = '/api/v1/roles/7';

/**
 * The accounts holding one role. Reads a page; accepts an assignment.
 */
const ROLE_MEMBERS_URL = '/api/v1/roles/7/users';

/**
 * One account's membership of one role.
 */
const ROLE_MEMBER_URL = '/api/v1/roles/7/users/42';

/**
 * The role-group collection. Unpaged, and parameterless.
 */
const ROLE_GROUPS_URL = '/api/v1/role-groups';

/**
 * One role group, addressed with the identity seed itself.
 */
const ROLE_GROUP_ZERO_URL = '/api/v1/role-groups/0';

/**
 * One role group, addressed with an ordinary positive key.
 */
const ROLE_GROUP_URL = '/api/v1/role-groups/4';

/**
 * The role whose identity is the seed value, used wherever a zero key is under test.
 */
const ROLE_ID_ZERO = 0;

/**
 * An ordinary role key, used wherever the key itself is not the subject of the case.
 */
const ROLE_ID = 7;

/**
 * An ordinary account key.
 */
const USER_ID = 42;

/**
 * An ordinary role-group key.
 */
const ROLE_GROUP_ID = 4;

/**
 * The roles-held-by-one-account address, for the ordinary account key.
 *
 * Nested under the ACCOUNT rather than under the role, because the answer is one person's
 * memberships rather than one role's members — `RolesController.cs:839` routes it at
 * `users/{userId:int}/roles`.
 */
const USER_ROLES_URL = '/api/v1/users/42/roles';

/**
 * The same address for the account whose key is nought.
 *
 * `01.00.00.SqlDataProvider` seeds the user key from one, so nought is not an account the
 * seeded schema issues — but the client must not be the thing that decides that. A caller
 * that tested the key for truthiness would send `/api/v1/users//roles`, and this constant is
 * what makes that provable.
 */
const USER_ZERO_ROLES_URL = '/api/v1/users/0/roles';

// ---------------------------------------------------------------------------
// REQUEST FIXTURES
//
//  Written as fresh literals so contextual typing supplies the exact member types the request contracts
//  declare. The filter and paging shapes are `as const` because their string members are closed unions on the
//  wire and a widened `string` would neither compile against the contract nor describe what actually travels.

/**
 * A fully populated paging request.
 *
 * The page index is deliberately NOT zero here so that the separate zero-index case proves something this
 * one cannot: that a supplied zero survives. The ordering direction is spelled as the server enumeration
 * names it, because the query-string binder accepts nothing else.
 */
const PAGED_REQUEST = {
  pageIndex: 2,
  pageSize: 25,
  sortBy: 'RoleName',
  sortDir: 'Ascending',
  query: 'admin',
} as const;

/**
 * A creation request carrying one of every awkward value at once.
 *
 * `null`, `0`, `-1`, the empty string and `false` all appear, and every one of them is a value the caller
 * chose rather than an absence. A member dropped for being falsy is the exact failure this fixture exists to
 * catch, and it is silent otherwise: the server would store a default and nobody would see a stack trace.
 */
const CREATE_ROLE_REQUEST: CreateRoleRequest = {
  roleName: 'Subscribers',
  // An empty description is a description the caller cleared, and the legacy absent-string marker WAS the
  // empty string, so this must arrive as an empty string and not as null.
  description: '',
  serviceFee: 0,
  billingPeriod: 0,
  billingFrequency: 'N',
  trialFee: 0,
  trialPeriod: -1,
  trialFrequency: 'N',
  isPublic: false,
  autoAssignment: false,
  // A real grouping key: the grouping identity is seeded from zero.
  roleGroupId: 0,
  rsvpCode: null,
  iconFile: null,
};

/**
 * A replacement request. Every editable member is declared, because this is not a patch.
 */
const UPDATE_ROLE_REQUEST: UpdateRoleRequest = {
  roleName: 'Subscribers',
  description: null,
  roleGroupId: -1,
  isPublic: true,
  autoAssignment: false,
  serviceFee: 9.99,
  billingPeriod: 1,
  billingFrequency: 'M',
  trialFee: 0,
  trialPeriod: 14,
  trialFrequency: 'D',
  rsvpCode: '',
  iconFile: null,
};

/**
 * An assignment request with BOTH date bounds absent.
 *
 * Absent means absent: `null` says "no bound" and the server derives one from the role's own frequency
 * terms. The case built on this fixture asserts the body arrives with the two members still null, which is
 * how a client-side derivation would be caught.
 */
const ASSIGNMENT_REQUEST: RoleAssignmentRequest = {
  userId: USER_ID,
  effectiveDate: null,
  expiryDate: null,
  notifyUser: false,
};

/**
 * A role-group creation request.
 */
const CREATE_ROLE_GROUP_REQUEST: CreateRoleGroupRequest = {
  roleGroupName: 'Paid Services',
  description: '',
};

/**
 * A role-group replacement request.
 */
const UPDATE_ROLE_GROUP_REQUEST: UpdateRoleGroupRequest = {
  roleGroupName: 'Paid Services',
  description: null,
};

/**
 * The six persisted frequency codes, in the order the legacy selection declared them.
 *
 * They are the bytes held in two single-character columns on the role table, so a case fold or a rename
 * would not fail a compilation — it would mis-address live rows.
 */
const BILLING_FREQUENCIES: readonly BillingFrequency[] = ['N', 'O', 'D', 'W', 'M', 'Y'];

// Response fixtures and envelope helpers.
//
//  The server publishes three shapes across these thirteen operations and this file reproduces each one
//  exactly. Flushing a bare payload where the server sends an envelope would test a body that is never sent,
//  and the failure would be silent rather than loud: a shape-correct blank reads as success.

/**
 * One row of the role listing.
 */
const ROLE_LIST_ITEM: RoleListItem = {
  roleId: ROLE_ID_ZERO,
  roleName: 'Administrators',
  description: null,
  serviceFee: null,
  billingPeriod: null,
  billingFrequency: null,
  trialFee: null,
  trialPeriod: null,
  trialFrequency: null,
  isPublic: false,
  autoAssignment: false,
};

/**
 * One role in full, including both sets of paid-membership terms.
 */
const ROLE: Role = {
  roleId: ROLE_ID_ZERO,
  roleGroupId: null,
  roleName: 'Administrators',
  description: 'Portal Administration',
  billingFrequency: 'M',
  serviceFee: 9.99,
  trialFrequency: 'N',
  trialPeriod: 0,
  billingPeriod: 1,
  trialFee: 0,
  isPublic: false,
  autoAssignment: false,
  rsvpCode: null,
  iconFile: null,
};

/**
 * One membership row.
 */
const USER_ROLE: UserRole = {
  userRoleId: 11,
  userId: USER_ID,
  username: 'admin',
  displayName: 'Administrator',
  roleId: ROLE_ID,
  roleName: 'Administrators',
  effectiveDate: null,
  expiryDate: null,
};

/**
 * Builds a role group whose tenant key is supplied by the caller.
 *
 * Parameterised precisely so the two legitimate tenant keys can be round-tripped: the portal identity is
 * seeded at minus one, so the first tenant carries -1 and the second carries 0, while the legacy
 * absent-integer marker was also -1.
 */
function roleGroup(portalId: number, roleGroupId = ROLE_GROUP_ID): RoleGroup {
  return {
    roleGroupId,
    portalId,
    roleGroupName: 'Paid Services',
    description: null,
  };
}

/**
 * Wraps a payload in the single-payload envelope the server writes.
 *
 * The metadata companion is PRESENT AND NULL rather than missing, because the API serialises with its ignore
 * condition set to never: an operation with no page to describe writes the key with a null value instead of
 * omitting it.
 */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * Wraps rows in the paged envelope, whose metadata carries the total across every page rather than the
 * length of the page in hand.
 */
function pageOf<T>(items: readonly T[], totalCount: number): PagedResponse<T> {
  return {
    items,
    meta: {
      totalCount,
      pageIndex: PAGED_REQUEST.pageIndex,
      pageSize: PAGED_REQUEST.pageSize,
      totalPages: Math.ceil(totalCount / PAGED_REQUEST.pageSize),
    },
  };
}

/**
 * An obviously synthetic trace-context value and correlation identifier.
 *
 * Neither is a credential — the server emits the correlation identifier back on a failure precisely so it
 * can be quoted in a bug report — but the trace value is written as all-but-zero so no reader mistakes it
 * for a real one. Both are attached to EVERY problem document by `ValidationProblemDetailsFactory`, so a
 * fixture without them describes a response this API does not send.
 */
const TRACE_ID = '00-00000000000000000000000000000001-0000000000000001-00';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * The refusal a protected membership removal earns, as a COMPLETE and INTERNALLY CONSISTENT document.
 *
 * The body's status and the transport status must agree. A real response cannot disagree with itself —
 * `ApiResults.Problem` writes the body's `status` from the same `MapStatusCode` call that sets the transport
 * status — and a consumer that read the body's member rather than the transport's would have been specified
 * against a contradiction, passing here and misclassifying in production.
 *
 * The status is 403 because the reason token is `protected`. `role_assignment.protected` refuses the removal
 * of the portal administrator or the registered-users membership, and the server's `ForbiddenTokens` table
 * maps `protected` to 403 — so this is NOT a conflict, and nothing the caller does to the request will
 * change the answer. That is the substantive difference from `role_group.in_use`, which IS a conflict at 409
 * because emptying the group makes the identical request succeed.
 *
 * MIGRATION: the wording descends from the legacy resource key `RoleRemoveError`. `RoleController.vb` shows
 * why the removal is refused rather than performed: the legacy member set an expiry of yesterday and UPDATED
 * the assignment for an expired membership, so the two protected memberships were never actually deletable.
 */
const PROTECTED_ASSIGNMENT_PROBLEM = {
  type: 'urn:dnnmigration:error:role_assignment.protected',
  title: 'Forbidden',
  status: 403,
  detail: 'You Can Not Remove The Portal Administrator Or The Registered Users Role',
  traceId: TRACE_ID,
  correlationId: CORRELATION_ID,
} as const;

/**
 * The refusal a still-populated role group's removal earns.
 *
 * `role_group.in_use` carries the `in_use` token, which the server's `ConflictTokens` table maps to 409: a
 * removal refused because the thing is still referenced is a perfectly well formed request that the STATE of
 * the resource declines, and releasing the references makes the identical request succeed.
 */
const GROUP_IN_USE_PROBLEM = {
  type: 'urn:dnnmigration:error:role_group.in_use',
  title: 'Conflict',
  status: 409,
  detail: 'The role group still classifies at least one role.',
  traceId: TRACE_ID,
  correlationId: CORRELATION_ID,
} as const;

/**
 * The methods the surface is closed at, in alphabetical order so the comparison below is
 * order-independent without needing a set.
 */
const EXPECTED_METHODS: readonly string[] = [
  'assignUser',
  'createRole',
  'createRoleGroup',
  'deleteRole',
  'deleteRoleGroup',
  /**
   * ONE ACCOUNT'S MEMBERSHIP OF ONE ROLE, addressed by both identifiers.
   *
   * ⚠ IT EXISTS SO THAT A LOGIN NAME NEVER TRAVELS IN A REQUEST TARGET. The same question was
   * previously asked by narrowing `listUsers` with the account's login name in the paging
   * contract's free-text filter, which the server matches against the login name and the display
   * name - so asking it wrote the name into a query string, and a query string is kept in browser
   * history and written in full to every proxy and server access log. That is CWE-598, and it stood
   * beside an account search already moved to a request body to avoid exactly it.
   */
  'getMembership',
  'getRole',
  'getRoleGroup',
  'listRoleGroups',
  'listRoles',
  /**
   * The roles ONE ACCOUNT holds.
   *
   * ⚠ THIS NAME WAS ONCE ABSENT ON PURPOSE, and it is present now because the condition its
   * absence depended on has changed. The endpoints registry withheld the template on the grounds
   * that "no screen in this application reads it … Declaring it belongs with the screen that needs
   * it, on the day one does." The account listing's roles command is that screen: it used to
   * navigate to the bare role listing and DISCARD the row's account, and it now carries the account
   * so the listing can narrow to that person's memberships.
   */
  'listRolesHeldByUser',
  'listUsers',
  'removeUser',
  'updateRole',
  'updateRoleGroup',
];

/**
 * Names that must NOT appear, each standing for a capability deliberately left out.
 *
 * Enumerated rather than merely implied by the exact-set comparison, because a name says what the exclusion
 * means in a way a count cannot. Permission mutation is absent because the permission resource is a
 * read-only catalogue and grants are enforced server-side on every request. The subscription and
 * member-services family is absent because a subscription WAS a row joining an account to a role between two
 * dates, which is what the assignment and removal methods already write — a parallel resource would have
 * been a second name for one table. Bulk operations and group reordering were never part of the contract.
 * The health endpoint sits outside the versioned API at the host root and belongs to the container probe.
 * Nothing under the authentication family belongs here.
 */
const ABSENT_METHODS: readonly string[] = [
  'grantPermission',
  'revokePermission',
  'setPermissions',
  'updatePermissions',
  'listPermissions',
  'listServices',
  'listSubscriptions',
  'subscribe',
  'unsubscribe',
  'assignUsers',
  'removeUsers',
  'bulkAssign',
  'bulkRemove',
  'reorderRoleGroups',
  'moveRoleGroup',
  'checkHealth',
  'login',
  'refresh',
  'logout',
];

describe('RoleService', () => {
  let service: RoleService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client FIRST and the testing backend SECOND: the testing provider overrides the backend the
      // real one installed, so the order is not cosmetic. No interceptors are registered, because this
      // specification is about the service's own behaviour and running the chain here would assert two units
      // at once.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    // Resolved from the root injector rather than listed as a provider: the service declares itself
    // root-provided, and re-declaring it here would silently test a second instance instead of the one the
    // application uses.
    service = TestBed.inject(RoleService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The load-bearing assertion of this file. Fails on any request that was opened and never consumed,
    // which is what proves each method issued EXACTLY ONE request and is the entire mechanism behind the
    // no-pre-emptive-read cases below.
    httpMock.verify();
  });

  // THE CLOSED SURFACE

  describe('the published surface', () => {
    it('declares exactly the fifteen expected methods and no sixteenth', () => {
      const declared: string[] = Object.getOwnPropertyNames(RoleService.prototype)
        .filter((name) => name !== 'constructor')
        .sort();

      expect(declared)
        .withContext('the surface is closed at fifteen operations across two resources')
        .toEqual([...EXPECTED_METHODS]);
    });

    it('declares none of the deliberately absent operations', () => {
      const declared: readonly string[] = Object.getOwnPropertyNames(RoleService.prototype);

      for (const absent of ABSENT_METHODS) {
        expect(declared.includes(absent))
          .withContext(`${absent} is deliberately not part of this service's contract`)
          .toBeFalse();
      }
    });
  });

  // ROLES

  describe('listRoles', () => {
    it('reads the role collection at the exact relative path', async () => {
      const pending = firstValueFrom(service.listRoles(PAGED_REQUEST));

      // Matched on method and PATH rather than by string, because the testing backend's string matcher
      // compares the url-with-params and would couple this assertion to parameter order.
      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.url).toBe(ROLES_URL);
      request.flush(pageOf([ROLE_LIST_ITEM], 1));

      await expectAsync(pending).toBeResolved();
    });

    it('returns the paged envelope untouched', async () => {
      const body = pageOf([ROLE_LIST_ITEM], 137);
      const pending = firstValueFrom(service.listRoles(PAGED_REQUEST));

      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL)
        .flush(body);

      // The total across every page, not the length of the page in hand: a consumer that read the array's
      // length would page wrongly the moment a second page existed.
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('emits exactly the five paging parameters and nothing else when no filter is given', async () => {
      const pending = firstValueFrom(service.listRoles(PAGED_REQUEST));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
        'query',
        'sortBy',
        'sortDir',
      ]);
      expect(request.request.params.get('pageIndex')).toBe('2');
      expect(request.request.params.get('pageSize')).toBe('25');
      expect(request.request.params.get('sortBy')).toBe('RoleName');
      // Spelled as the server enumeration names it. An abbreviated or lower-cased token is refused by model
      // binding, so a mapping table here would be a defect.
      expect(request.request.params.get('sortDir')).toBe('Ascending');

      request.flush(pageOf([ROLE_LIST_ITEM], 1));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits a page index of zero as zero, applying no offset arithmetic', async () => {
      // The legacy screens held a ONE-based page number in the UI and subtracted one on the way to the data
      // layer. The wire coordinate here is already zero-based, so any adjustment in either direction would
      // silently serve the neighbouring page.
      const pending = firstValueFrom(service.listRoles({ pageIndex: 0, pageSize: 10 }));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.get('pageIndex'))
        .withContext('zero is the first page and must travel as zero')
        .toBe('0');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits a page index of one as one, applying no offset arithmetic', async () => {
      const pending = firstValueFrom(service.listRoles({ pageIndex: 1, pageSize: 10 }));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.get('pageIndex')).toBe('1');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('sends the filter text with no wildcard character appended', async () => {
      const pending = firstValueFrom(service.listRoles({ pageIndex: 0, pageSize: 10, query: 'adm' }));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );
      const query: string | null = request.request.params.get('query');

      // The legacy screens composed the pattern themselves, appending a trailing wildcard before the query
      // ran. Composing a pattern is the repository's work now, so a wildcard arriving from the client would
      // be matched literally or doubled.
      expect(query).toBe('adm');
      // Coerced rather than optionally chained, so the matcher receives a genuine boolean instead of an
      // `undefined` that would read as a failure for the wrong reason.
      expect(String(query).includes('%'))
        .withContext('the trailing wildcard is composed server-side, never here')
        .toBeFalse();

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits an explicitly empty filter as an empty value rather than dropping it', async () => {
      // The legacy absent-string marker WAS the empty string, so an empty filter and no filter are different
      // requests and only the server may decide what the first means.
      const pending = firstValueFrom(service.listRoles({ pageIndex: 0, pageSize: 10, query: '' }));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.has('query'))
        .withContext('an empty string is a value the caller chose, not an absence')
        .toBeTrue();
      expect(request.request.params.get('query')).toBe('');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('omits a parameter supplied as undefined while keeping one supplied as zero', async () => {
      // The single absence test in the request path is an identity comparison against undefined and null. A
      // truthiness test in its place would drop the zero below, and zero is the first page.
      const pending = firstValueFrom(
        service.listRoles({ pageIndex: 0, pageSize: undefined, sortBy: undefined }),
      );

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.keys().sort()).toEqual(['pageIndex']);
      expect(request.request.params.get('pageIndex')).toBe('0');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('omits a parameter supplied as null', async () => {
      const pending = firstValueFrom(
        service.listRoles({ pageIndex: 0, pageSize: 10, sortBy: null, sortDir: null, query: null }),
      );

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize']);

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits the legacy all-roles sentinel of minus two verbatim', async () => {
      // `Website/admin/Security/Roles.ascx.vb` gave the all-roles choice the value "-2" and defaulted the
      // selector to it for a portal declaring no groups. then branched on the value being below -1. Whatever
      // the endpoint chooses to make of a negative key is its own business; what is asserted here is that
      // this client neither inspects it, coalesces it, nor rewrites it.
      const pending = firstValueFrom(
        service.listRoles({ pageIndex: 0, pageSize: 10 }, { roleGroupId: -2 }),
      );

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.has('roleGroupId'))
        .withContext('a negative filter value is never mapped to an omission')
        .toBeTrue();
      expect(request.request.params.get('roleGroupId')).toBe('-2');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits the legacy global-roles sentinel of minus one verbatim', async () => {
      // Minus one was simultaneously a persisted grouping key and the legacy absent-integer marker, which is
      // exactly why it must never be coalesced away on the client.
      const pending = firstValueFrom(
        service.listRoles({ pageIndex: 0, pageSize: 10 }, { roleGroupId: -1 }),
      );

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.get('roleGroupId')).toBe('-1');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits a group key of zero, which the identity seed makes a real group', async () => {
      const pending = firstValueFrom(
        service.listRoles({ pageIndex: 0, pageSize: 10 }, { roleGroupId: 0 }),
      );

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.get('roleGroupId')).toBe('0');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('transmits the named grouping scope as its own parameter', async () => {
      // The named successor to the legacy selector's negative band. It travels as a name so that the magic
      // integers do not, and an unrecognised spelling is refused by model binding rather than falling
      // silently into whichever numeric band contained it.
      const pending = firstValueFrom(
        service.listRoles({ pageIndex: 0, pageSize: 10 }, { scope: 'Ungrouped' }),
      );

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize', 'scope']);
      expect(request.request.params.get('scope')).toBe('Ungrouped');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('emits no filter parameters at all when the filter is omitted or null', async () => {
      const withoutFilter = firstValueFrom(service.listRoles({ pageIndex: 0, pageSize: 10 }));
      const first = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );
      expect(first.request.params.has('roleGroupId')).toBeFalse();
      expect(first.request.params.has('scope')).toBeFalse();
      first.flush(pageOf([], 0));
      await expectAsync(withoutFilter).toBeResolved();

      const withNullFilter = firstValueFrom(service.listRoles({ pageIndex: 0, pageSize: 10 }, null));
      const second = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );
      expect(second.request.params.has('roleGroupId')).toBeFalse();
      expect(second.request.params.has('scope')).toBeFalse();
      second.flush(pageOf([], 0));
      await expectAsync(withNullFilter).toBeResolved();
    });

    it('carries no request body', async () => {
      const pending = firstValueFrom(service.listRoles(PAGED_REQUEST));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLES_URL,
      );

      expect(request.request.body).toBeNull();

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('getRole', () => {
    it('addresses a role whose identifier is zero, proving no truthiness guard', async () => {
      // `01.00.00.SqlDataProvider` declares the role key `IDENTITY (0, 1)`, so a role key of zero is the
      // tenant's FIRST role rather than a missing one. A guard on the identifier being truthy would suppress
      // this request entirely, and the failure would read as an empty screen rather than as an error.
      const pending = firstValueFrom(service.getRole(ROLE_ID_ZERO));

      const request = httpMock.expectOne(ROLE_ZERO_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url).toBe(ROLE_ZERO_URL);
      expect(request.request.body).toBeNull();

      request.flush(envelope(ROLE));
      await expectAsync(pending).toBeResolved();
    });

    it('returns the single-payload envelope untouched', async () => {
      const body = envelope(ROLE);
      const pending = firstValueFrom(service.getRole(ROLE_ID_ZERO));

      httpMock.expectOne(ROLE_ZERO_URL).flush(body);

      // Including the metadata companion, which is present and null for an operation with no page to
      // describe. Flattening the envelope to its payload would make every consumer read a member the server
      // does send.
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('addresses an ordinary positive identifier unchanged', async () => {
      const pending = firstValueFrom(service.getRole(ROLE_ID));

      const request = httpMock.expectOne(ROLE_URL);
      expect(request.request.method).toBe('GET');

      request.flush(envelope({ ...ROLE, roleId: ROLE_ID }));
      await expectAsync(pending).toBeResolved();
    });

    it('emits no query parameters', async () => {
      const pending = firstValueFrom(service.getRole(ROLE_ID));

      const request = httpMock.expectOne(ROLE_URL);

      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.urlWithParams).toBe(ROLE_URL);

      request.flush(envelope(ROLE));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('createRole', () => {
    it('posts to the collection and answers the created role on 201', async () => {
      const body = envelope(ROLE);
      const pending = firstValueFrom(service.createRole(CREATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLES_URL);

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe(ROLES_URL);

      request.flush(body, { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('passes the request body through byte for byte, falsy members included', async () => {
      const pending = firstValueFrom(service.createRole(CREATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLES_URL);

      // A deep comparison against the very object handed in. Every awkward value is present in that fixture
      // on purpose: a null, two zeroes, a minus one, an empty string and two falses. A member stripped for
      // being falsy would make the server store its own default, and nothing would report it.
      expect(request.request.body).toEqual(CREATE_ROLE_REQUEST);

      request.flush(envelope(ROLE), { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolved();
    });

    it('preserves an empty description rather than converting it to null', async () => {
      const pending = firstValueFrom(service.createRole(CREATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLES_URL);
      const sent: unknown = request.request.body;

      // `Library/Components/Shared/Null.vb` returns the EMPTY STRING for an absent string, so the two are
      // indistinguishable in the legacy store and only the server may decide what an empty one means. Read
      // through the contract's own type rather than by property access, so the comparison is exact.
      expect(sent).toEqual({ ...CREATE_ROLE_REQUEST, description: '' });

      request.flush(envelope(ROLE), { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolved();
    });

    it('emits no query parameters and adds no headers of its own', async () => {
      const pending = firstValueFrom(service.createRole(CREATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLES_URL);

      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.headers.has('Authorization')).toBeFalse();
      expect(request.request.headers.has('X-Correlation-Id')).toBeFalse();

      request.flush(envelope(ROLE), { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolved();
    });
  });


  describe('the six persisted frequency codes', () => {
    // Every one of those decisions is SERVER-SIDE. What travels from here is one upper-case character held
    // in a single-character column, so a case fold, a rename, an integer substitution or an expansion to a
    // display label would not fail a compilation — it would mis-address live rows.
    for (const code of BILLING_FREQUENCIES) {
      it(`sends the billing frequency ${code} as that exact upper-case character on create`, async () => {
        const request: CreateRoleRequest = { ...CREATE_ROLE_REQUEST, billingFrequency: code };
        const pending = firstValueFrom(service.createRole(request));

        const pendingRequest = httpMock.expectOne(ROLES_URL);

        expect(pendingRequest.request.body).toEqual(request);

        pendingRequest.flush(envelope(ROLE), { status: 201, statusText: 'Created' });
        await expectAsync(pending).toBeResolved();
      });

      it(`sends the trial frequency ${code} as that exact upper-case character on update`, async () => {
        const request: UpdateRoleRequest = { ...UPDATE_ROLE_REQUEST, trialFrequency: code };
        const pending = firstValueFrom(service.updateRole(ROLE_ID, request));

        const pendingRequest = httpMock.expectOne(ROLE_URL);

        expect(pendingRequest.request.body).toEqual(request);

        pendingRequest.flush(envelope(ROLE));
        await expectAsync(pending).toBeResolved();
      });
    }

    it('adds no derived expiry member to a create body that did not supply one', async () => {
      const pending = firstValueFrom(service.createRole(CREATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLES_URL);

      // The exact-equality comparison is what proves the absence: an extra member computed from a frequency
      // and a period would make this fail. The server is the sole authority for a derived bound, because a
      // client computing one from its own wall clock would disagree across a clock skew and could not be
      // tested deterministically.
      expect(request.request.body).toEqual(CREATE_ROLE_REQUEST);

      request.flush(envelope(ROLE), { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('updateRole', () => {
    it('replaces a role at its own address and answers 200 with the updated role', async () => {
      const body = envelope({ ...ROLE, roleId: ROLE_ID });
      const pending = firstValueFrom(service.updateRole(ROLE_ID, UPDATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLE_URL);

      expect(request.request.method).toBe('PUT');
      expect(request.request.url).toBe(ROLE_URL);

      request.flush(body, { status: 200, statusText: 'OK' });
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('passes the replacement body through unmodified', async () => {
      const pending = firstValueFrom(service.updateRole(ROLE_ID, UPDATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLE_URL);

      // A replacement rather than a patch: the contract declares every editable member, so a member the
      // client dropped would be a member the server CLEARED. The fixture holds a null, a minus one, an empty
      // string, a zero and a false for that reason.
      expect(request.request.body).toEqual(UPDATE_ROLE_REQUEST);

      request.flush(envelope(ROLE));
      await expectAsync(pending).toBeResolved();
    });

    it('replaces the role whose identifier is zero', async () => {
      const pending = firstValueFrom(service.updateRole(ROLE_ID_ZERO, UPDATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLE_ZERO_URL);
      expect(request.request.method).toBe('PUT');

      request.flush(envelope(ROLE));
      await expectAsync(pending).toBeResolved();
    });

    it('emits no query parameters', async () => {
      const pending = firstValueFrom(service.updateRole(ROLE_ID, UPDATE_ROLE_REQUEST));

      const request = httpMock.expectOne(ROLE_URL);
      expect(request.request.urlWithParams).toBe(ROLE_URL);

      request.flush(envelope(ROLE));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('deleteRole', () => {
    it('deletes a role at its own address and completes on an empty 204', async () => {
      const pending = firstValueFrom(service.deleteRole(ROLE_ID));

      const request = httpMock.expectOne(ROLE_URL);

      expect(request.request.method).toBe('DELETE');
      expect(request.request.url).toBe(ROLE_URL);
      expect(request.request.body).toBeNull();

      // No content, which forbids a body. The observable must still complete: a method that waited for a
      // payload here would hang forever on a perfectly successful delete.
      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('deletes the role whose identifier is zero', async () => {
      const pending = firstValueFrom(service.deleteRole(ROLE_ID_ZERO));

      const request = httpMock.expectOne(ROLE_ZERO_URL);
      expect(request.request.method).toBe('DELETE');

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('lets a protected-role refusal propagate without translating it', async () => {
      // `role.protected` carries the `protected` token, so the server maps it to 403. The removal declares
      // 204/401/403/404 and no 409 at all — a role the product created and depends on is refused on
      // permission grounds, not because of a state collision the caller could clear.
      const document = {
        type: 'urn:dnnmigration:error:role.protected',
        title: 'Forbidden',
        status: 403,
        detail: 'This role is required by the product and cannot be removed.',
        traceId: TRACE_ID,
        correlationId: CORRELATION_ID,
      } as const;

      const settled = firstValueFrom(service.deleteRole(ROLE_ID)).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );

      httpMock.expectOne(ROLE_URL).flush(document, { status: 403, statusText: 'Forbidden' });

      const outcome: unknown = await settled;

      if (!(outcome instanceof HttpErrorResponse)) {
        throw new Error(`Expected the refusal to propagate as a failure, got ${String(outcome)}`);
      }
      expect(outcome.status).toBe(403);
      expect(outcome.error).toEqual(document);
    });
  });

  // ROLE MEMBERSHIP

  describe('listUsers', () => {
    it('reads the members of one role at the exact nested path', async () => {
      const pending = firstValueFrom(service.listUsers(ROLE_ID, PAGED_REQUEST));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_MEMBERS_URL,
      );

      expect(request.request.url).toBe(ROLE_MEMBERS_URL);
      expect(request.request.body).toBeNull();

      request.flush(pageOf([USER_ROLE], 1));
      await expectAsync(pending).toBeResolved();
    });

    it('returns the paged envelope of memberships untouched', async () => {
      const body = pageOf([USER_ROLE], 3);
      const pending = firstValueFrom(service.listUsers(ROLE_ID, PAGED_REQUEST));

      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ROLE_MEMBERS_URL)
        .flush(body);

      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('emits the paging contract as its whole query surface', async () => {
      const pending = firstValueFrom(service.listUsers(ROLE_ID, PAGED_REQUEST));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_MEMBERS_URL,
      );

      // No filter of its own beyond the paging contract's free-text one. A role-group or scope parameter
      // appearing here would be a listing that had grown a surface the server does not bind.
      expect(request.request.params.keys().sort()).toEqual([
        'pageIndex',
        'pageSize',
        'query',
        'sortBy',
        'sortDir',
      ]);
      expect(request.request.params.has('roleGroupId')).toBeFalse();
      expect(request.request.params.has('scope')).toBeFalse();

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });

    it('reads the members of the role whose identifier is zero', async () => {
      const pending = firstValueFrom(service.listUsers(ROLE_ID_ZERO, { pageIndex: 0, pageSize: 10 }));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/roles/0/users',
      );

      expect(request.request.url).toBe('/api/v1/roles/0/users');

      request.flush(pageOf([], 0));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('getMembership', () => {
    // ⚠ THE ADDRESS IS THE POINT. This read replaces asking the same question through `listUsers`
    // narrowed by the account's LOGIN NAME in the paging contract's free-text filter, which the
    // server matches against the login name and the display name - so the name had to be in the
    // query string for the request to work. A query string is kept in browser history and written
    // in full to every forward and reverse proxy access log and to the server's own, none of which
    // is on the wire, so transport encryption does not address it: CWE-598.
    it('addresses the pairing itself and emits no query parameter at all', async () => {
      const pending = firstValueFrom(service.getMembership(ROLE_ID, USER_ID));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_MEMBER_URL,
      );

      expect(request.request.url).toBe(ROLE_MEMBER_URL);
      expect(request.request.urlWithParams).toBe(ROLE_MEMBER_URL);
      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.body).toBeNull();
      expect(request.request.urlWithParams).not.toContain(USER_ROLE.username);
      expect(request.request.urlWithParams).not.toContain(USER_ROLE.displayName);

      request.flush(envelope(USER_ROLE));
      await expectAsync(pending).toBeResolved();
    });

    it('returns the single-payload envelope, decoded through the membership row contract', async () => {
      const body = envelope(USER_ROLE);
      const pending = firstValueFrom(service.getMembership(ROLE_ID, USER_ID));

      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ROLE_MEMBER_URL)
        .flush(body);

      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('propagates the refusal that means the account holds no such membership', async () => {
      // The transport does NOT translate the 404 into a successful absence. Whether "holds nothing"
      // is an acceptable outcome depends on the question being asked, and only the caller knows
      // that - `RoleStore.probeAssignment` reads it as the negative answer, and a screen that
      // required the membership to exist would report it.
      const pending = firstValueFrom(service.getMembership(ROLE_ID, USER_ID));

      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ROLE_MEMBER_URL)
        .flush(
          {
            type: 'urn:dnnmigration:error:role_assignment.not_found',
            title: 'Not Found',
            status: 404,
            detail: 'The account holds no such membership.',
          },
          { status: 404, statusText: 'Not Found' },
        );

      await expectAsync(pending).toBeRejected();
    });

    it('addresses the pairing whose keys are both zero, because both tables seed there', async () => {
      // `dbo.Roles.RoleID` is IDENTITY(0, 1), so role zero is real. Neither identifier is tested
      // for truthiness anywhere on this path.
      const pending = firstValueFrom(service.getMembership(ROLE_ID_ZERO, 0));

      const request = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/roles/0/users/0',
      );

      expect(request.request.url).toBe('/api/v1/roles/0/users/0');

      request.flush(envelope(USER_ROLE));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('assignUser', () => {
    it('posts the assignment to the nested membership collection', async () => {
      const pending = firstValueFrom(service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST));

      const request = httpMock.expectOne(ROLE_MEMBERS_URL);

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe(ROLE_MEMBERS_URL);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('completes on a 204, which is what the endpoint as built declares', async () => {
      const pending = firstValueFrom(service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST));

      httpMock
        .expectOne(ROLE_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      await expectAsync(pending).toBeResolved();
    });

    it('emits no response DTO, because a 204 carries no body at all', async () => {
      // 204 IS THE ONLY SUCCESS STATUS THIS ACTION DECLARES. `POST /api/v1/roles/{roleId}/users` reports
      // through `ApiResults.Complete(Result)`, whose success arm is `NoContent`, and `RolesController`
      // declares exactly 204/400/401/403/404 for it. It does not: a 204 is forbidden from carrying a body.
      //
      // The property genuinely worth pinning is the one below. `RoleController.vb` branched on the
      // assignment's own identifier still holding the absent-integer marker it was given, UPDATING an
      // existing row when it did not and ADDING one when it did. The API deliberately does not publish which
      // of the two happened, so no representation comes back and a client cannot re-derive the distinction.
      // That is why the observable is `void`-typed and why nothing is emitted here but the empty body.
      const emitted: unknown[] = [];
      let completed = false;

      service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST).subscribe({
        next: (value) => emitted.push(value),
        complete: () => {
          completed = true;
        },
      });

      const request = httpMock.expectOne(ROLE_MEMBERS_URL);

      expect(request.request.method).toBe('POST');
      expect(request.request.body)
        .withContext('the assignment travels exactly as the caller composed it')
        .toEqual(ASSIGNMENT_REQUEST);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(completed).toBeTrue();
      expect(emitted)
        .withContext('no membership representation comes back, so none is emitted')
        .toEqual([null]);
    });

    it('passes the assignment body through unmodified', async () => {
      const pending = firstValueFrom(service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST));

      const request = httpMock.expectOne(ROLE_MEMBERS_URL);

      expect(request.request.body).toEqual(ASSIGNMENT_REQUEST);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('leaves both absent date bounds null and derives neither on the client', async () => {
      const pending = firstValueFrom(service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST));

      const request = httpMock.expectOne(ROLE_MEMBERS_URL);

      // Absent means absent. `null` says "no bound" and the server derives one from the role's frequency
      // terms; the legacy in-memory spelling of an absent date was a minimum-value instant, which is not
      // something the column can hold. The exact-equality comparison is again what proves the negative: a
      // computed bound would add or replace a member and make this fail.
      expect(request.request.body).toEqual({
        userId: USER_ID,
        effectiveDate: null,
        expiryDate: null,
        notifyUser: false,
      });

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('transmits caller-supplied date bounds exactly as given', async () => {
      const withBounds: RoleAssignmentRequest = {
        userId: USER_ID,
        effectiveDate: '2024-01-01T00:00:00.000Z',
        expiryDate: '9999-12-31T00:00:00.000Z',
        notifyUser: true,
      };
      const pending = firstValueFrom(service.assignUser(ROLE_ID, withBounds));

      const request = httpMock.expectOne(ROLE_MEMBERS_URL);

      // The far-future instant is the perpetual bound the legacy selection produced for the O code at
      // `RoleController.vb`. It is transmitted, never re-derived.
      expect(request.request.body).toEqual(withBounds);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('emits no query parameters', async () => {
      const pending = firstValueFrom(service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST));

      const request = httpMock.expectOne(ROLE_MEMBERS_URL);
      expect(request.request.urlWithParams).toBe(ROLE_MEMBERS_URL);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('removeUser', () => {
    it('addresses the pairing directly at the doubly nested path', async () => {
      // Addressed by role and account rather than through the assignment's own surrogate key, which the
      // legacy grid could only supply after rendering itself. The operation is therefore reachable without a
      // prior read, and `httpMock.verify()` is what proves no prior read is taken.
      const pending = firstValueFrom(service.removeUser(ROLE_ID, USER_ID));

      const request = httpMock.expectOne(ROLE_MEMBER_URL);

      expect(request.request.method).toBe('DELETE');
      expect(request.request.url).toBe(ROLE_MEMBER_URL);
      expect(request.request.body).toBeNull();

      // MIGRATION: A 204 HERE DOES NOT PROMISE THE ROW IS GONE.
      // `Library/Components/Security/Roles/RoleController.vb` tests the cancel branch and then, whether the
      // membership exists, carries a service fee above zero and has had its trial used. The wire status is
      // 204 either way and carries no body to tell the two apart, so a consumer must re-read the listing
      // rather than dropping the row locally: an expired assignment is a retained row a listing may still
      // return.
      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('addresses a pairing whose role identifier is zero', async () => {
      const pending = firstValueFrom(service.removeUser(ROLE_ID_ZERO, USER_ID));

      const request = httpMock.expectOne('/api/v1/roles/0/users/42');
      expect(request.request.method).toBe('DELETE');

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('emits no query parameters', async () => {
      const pending = firstValueFrom(service.removeUser(ROLE_ID, USER_ID));

      const request = httpMock.expectOne(ROLE_MEMBER_URL);
      expect(request.request.urlWithParams).toBe(ROLE_MEMBER_URL);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('lets a protected removal propagate as a failure', async () => {
      const settled = firstValueFrom(service.removeUser(ROLE_ID, USER_ID)).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );

      httpMock
        .expectOne(ROLE_MEMBER_URL)
        .flush(PROTECTED_ASSIGNMENT_PROBLEM, { status: 403, statusText: 'Forbidden' });

      const outcome: unknown = await settled;

      if (!(outcome instanceof HttpErrorResponse)) {
        throw new Error(`Expected the refusal to propagate as a failure, got ${String(outcome)}`);
      }
      expect(outcome.status).toBe(403);
      // The body agrees with the transport. That is the property an internally inconsistent fixture cannot
      // assert, and the one a consumer reading `problem.status` depends on.
      expect(outcome.error).toEqual(PROTECTED_ASSIGNMENT_PROBLEM);
      expect((outcome.error as { readonly status: number }).status).toBe(outcome.status);
    });
  });


  // ROLE GROUPS

  describe('listRoleGroups', () => {
    it('reads the group collection at the exact relative path', async () => {
      const pending = firstValueFrom(service.listRoleGroups());

      const request = httpMock.expectOne(ROLE_GROUPS_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url).toBe(ROLE_GROUPS_URL);
      expect(request.request.body).toBeNull();

      request.flush(envelope([roleGroup(0)]));
      await expectAsync(pending).toBeResolved();
    });

    it('emits NO query parameters at all, paging coordinates included', async () => {
      const pending = firstValueFrom(service.listRoleGroups());

      const request = httpMock.expectOne(ROLE_GROUPS_URL);

      // MIGRATION: this listing is deliberately UNPAGED, and the method takes no arguments at all.
      // `Website/admin/Security/Roles.ascx.vb` read every group in the portal into an untyped list and bound
      // the whole answer to a drop-down, with no page coordinate, no size and no total anywhere on the
      // screen; a tenant defines groups in the tens. Introducing paging would add a contract the legacy
      // application never had.
      //
      // No portal identifier travels here: the route template is parameterless and the controller binds
      // nothing, because the tenant is resolved server-side from the request host and the caller's claims.
      // Asserting the whole query string is empty is the strongest statement available and it discharges the
      // no-paging requirement outright.
      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.urlWithParams).toBe(ROLE_GROUPS_URL);
      expect(request.request.params.has('pageIndex')).toBeFalse();
      expect(request.request.params.has('pageSize')).toBeFalse();
      expect(request.request.params.has('sortBy')).toBeFalse();
      expect(request.request.params.has('sortDir')).toBeFalse();
      expect(request.request.params.has('query')).toBeFalse();
      expect(request.request.params.has('portalId')).toBeFalse();

      request.flush(envelope([roleGroup(0)]));
      await expectAsync(pending).toBeResolved();
    });

    it('round-trips a tenant key of minus one, which the identity seed makes the first tenant', async () => {
      // `01.00.00.SqlDataProvider` declares the portal key `IDENTITY (-1, 1)`, so the first tenant ever
      // created carries -1 — while `Null.vb` simultaneously made -1 the absent-integer marker. On this
      // operation the tenant key travels on the PAYLOAD rather than the query, so this is where its fidelity
      // is provable: a consumer that coalesced -1 to null would lose the first tenant entirely.
      const body = envelope([roleGroup(-1)]);
      const pending = firstValueFrom(service.listRoleGroups());

      httpMock.expectOne(ROLE_GROUPS_URL).flush(body);

      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('round-trips a tenant key of zero, which is the second tenant', async () => {
      const body = envelope([roleGroup(0)]);
      const pending = firstValueFrom(service.listRoleGroups());

      httpMock.expectOne(ROLE_GROUPS_URL).flush(body);

      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('returns an empty payload as a legitimate answer rather than a failure', async () => {
      // A tenant that defines no groups. The legacy grid hid its group row entirely in that case, and that
      // presentation choice belongs to a component; here it is simply an empty sequence inside a well-formed
      // envelope.
      const body = envelope<readonly RoleGroup[]>([]);
      const pending = firstValueFrom(service.listRoleGroups());

      httpMock.expectOne(ROLE_GROUPS_URL).flush(body);

      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('answers the sequence as the payload of the single-payload envelope', async () => {
      // The sequence is the PAYLOAD, not the whole body, and the metadata companion is present and null. A
      // method that returned the bare array would make every consumer read a member that is never there.
      const body = envelope([roleGroup(0, 0), roleGroup(0, ROLE_GROUP_ID)]);
      const pending = firstValueFrom(service.listRoleGroups());

      httpMock.expectOne(ROLE_GROUPS_URL).flush(body);

      await expectAsync(pending).toBeResolvedTo(body);
    });
  });

  // -------------------------------------------------------------------------
  // THE ROLES ONE ACCOUNT HOLDS
  //
  // MIGRATION: `Website/admin/Security/SecurityRoles.ascx.vb:L253` read one account's role
  // memberships directly, which is what let the legacy screen open already narrowed to the
  // person the operator had just been looking at. The account listing's roles command used to
  // navigate to the bare role listing and discard the row's account, so the operator arrived at
  // every role in the tenant and had to find the person again.
  // -------------------------------------------------------------------------

  describe('listRolesHeldByUser', () => {
    it('reads the memberships at the exact relative path, nested under the ACCOUNT', async () => {
      const pending = firstValueFrom(service.listRolesHeldByUser(USER_ID));

      const request = httpMock.expectOne(USER_ROLES_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url).toBe(USER_ROLES_URL);
      expect(request.request.body).toBeNull();

      request.flush(envelope([ROLE_LIST_ITEM]));
      await expectAsync(pending).toBeResolved();
    });

    it('emits NO query parameters, this listing being unpaged like the controller serves it', async () => {
      // The controller answers `ApiResponse<IReadOnlyList<RoleListItemDto>>` — a plain
      // collection rather than a page — because one account holds roles in the tens. Sending a
      // page coordinate would describe a contract the server does not serve, and the empty
      // query string is the strongest available statement of that.
      const pending = firstValueFrom(service.listRolesHeldByUser(USER_ID));

      const request = httpMock.expectOne(USER_ROLES_URL);

      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.urlWithParams).toBe(USER_ROLES_URL);
      expect(request.request.params.has('pageIndex')).toBeFalse();
      expect(request.request.params.has('pageSize')).toBeFalse();
      expect(request.request.params.has('portalId')).toBeFalse();

      request.flush(envelope([ROLE_LIST_ITEM]));
      await expectAsync(pending).toBeResolved();
    });

    it('addresses an account key of NOUGHT rather than dropping it from the path', async () => {
      // ⚠ A truthiness test on the key would produce `/api/v1/users//roles`, which no route
      // matches. Nought is the one key where that mistake is invisible in every other case.
      const pending = firstValueFrom(service.listRolesHeldByUser(0));

      const request = httpMock.expectOne(USER_ZERO_ROLES_URL);

      expect(request.request.url).toBe(USER_ZERO_ROLES_URL);

      request.flush(envelope([ROLE_LIST_ITEM]));
      await expectAsync(pending).toBeResolved();
    });

    it('carries an account holding NO roles through as an empty collection', async () => {
      // An account may legitimately hold nothing, and that is a different answer from a failed
      // read. The envelope is well formed and its payload is empty.
      const pending = firstValueFrom(service.listRolesHeldByUser(USER_ID));

      httpMock.expectOne(USER_ROLES_URL).flush(envelope([]));

      const answered = await pending;

      expect(answered.data).toEqual([]);
    });

    it('publishes every role member the listing declares, sentinels included', async () => {
      const pending = firstValueFrom(service.listRolesHeldByUser(USER_ID));

      httpMock.expectOne(USER_ROLES_URL).flush(envelope([ROLE_LIST_ITEM]));

      const answered = await pending;

      // Decoded through the same contract the tenant-wide listing uses, so a role read this way
      // is indistinguishable from the same role read the other way — which is what lets one
      // screen render either answer.
      expect(answered.data).toEqual([ROLE_LIST_ITEM]);
      expect(answered.data[0].roleId).toBe(ROLE_ID_ZERO);
      expect(answered.data[0].description).toBeNull();
      expect(answered.data[0].billingFrequency).toBeNull();
    });

    it('REFUSES a payload that is not a collection, rather than passing it on', () => {
      // The response contract is checked at the boundary. A server that answered with a single
      // role instead of a collection would otherwise reach a template that iterates it.
      const values: unknown[] = [];
      const failures: unknown[] = [];

      service.listRolesHeldByUser(USER_ID).subscribe({
        next: (value: unknown) => values.push(value),
        error: (failure: unknown) => failures.push(failure),
      });

      httpMock.expectOne(USER_ROLES_URL).flush(envelope(ROLE_LIST_ITEM));

      expect(values).toEqual([]);
      expect(failures.length).toBe(1);
      expect(isContractViolation(failures[0])).toBeTrue();
    });

    it('REFUSES a member whose role key is missing', () => {
      const values: unknown[] = [];
      const failures: unknown[] = [];

      service.listRolesHeldByUser(USER_ID).subscribe({
        next: (value: unknown) => values.push(value),
        error: (failure: unknown) => failures.push(failure),
      });

      const { roleId: _omitted, ...withoutKey } = ROLE_LIST_ITEM;

      httpMock.expectOne(USER_ROLES_URL).flush(envelope([withoutKey]));

      expect(values).toEqual([]);
      expect(failures.length).toBe(1);
      expect(isContractViolation(failures[0])).toBeTrue();
    });
  });

  describe('createRoleGroup', () => {
    it('posts to the group collection and answers the created group on 201', async () => {
      const body = envelope(roleGroup(0));
      const pending = firstValueFrom(service.createRoleGroup(CREATE_ROLE_GROUP_REQUEST));

      const request = httpMock.expectOne(ROLE_GROUPS_URL);

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe(ROLE_GROUPS_URL);

      request.flush(body, { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('passes the creation body through unmodified, empty description included', async () => {
      const pending = firstValueFrom(service.createRoleGroup(CREATE_ROLE_GROUP_REQUEST));

      const request = httpMock.expectOne(ROLE_GROUPS_URL);

      // The contract carries the group's two editable facts and nothing else: its key is assigned by the
      // server and its tenant comes from the resolved context, so neither is sent even though both are
      // published on the response.
      expect(request.request.body).toEqual({ roleGroupName: 'Paid Services', description: '' });
      expect(request.request.params.keys()).toEqual([]);

      request.flush(envelope(roleGroup(0)), { status: 201, statusText: 'Created' });
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('getRoleGroup', () => {
    it('reads one group at its own address', async () => {
      const body = envelope(roleGroup(0));
      const pending = firstValueFrom(service.getRoleGroup(ROLE_GROUP_ID));

      const request = httpMock.expectOne(ROLE_GROUP_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url).toBe(ROLE_GROUP_URL);
      expect(request.request.body).toBeNull();
      expect(request.request.params.keys()).toEqual([]);

      request.flush(body);
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('reads the group whose identifier is zero', async () => {
      // `03.02.03.SqlDataProvider` declares the grouping key `IDENTITY(0,1)`, so zero is a real group and
      // never an absence.
      const pending = firstValueFrom(service.getRoleGroup(0));

      const request = httpMock.expectOne(ROLE_GROUP_ZERO_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url).toBe(ROLE_GROUP_ZERO_URL);

      request.flush(envelope(roleGroup(0, 0)));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('updateRoleGroup', () => {
    it('replaces a group at its own address and answers 200 with the updated group', async () => {
      const body = envelope(roleGroup(0));
      const pending = firstValueFrom(
        service.updateRoleGroup(ROLE_GROUP_ID, UPDATE_ROLE_GROUP_REQUEST),
      );

      const request = httpMock.expectOne(ROLE_GROUP_URL);

      expect(request.request.method).toBe('PUT');
      expect(request.request.url).toBe(ROLE_GROUP_URL);
      expect(request.request.body).toEqual(UPDATE_ROLE_GROUP_REQUEST);

      request.flush(body, { status: 200, statusText: 'OK' });
      await expectAsync(pending).toBeResolvedTo(body);
    });

    it('replaces the group whose identifier is zero', async () => {
      const pending = firstValueFrom(service.updateRoleGroup(0, UPDATE_ROLE_GROUP_REQUEST));

      const request = httpMock.expectOne(ROLE_GROUP_ZERO_URL);
      expect(request.request.method).toBe('PUT');

      request.flush(envelope(roleGroup(0, 0)));
      await expectAsync(pending).toBeResolved();
    });
  });

  describe('deleteRoleGroup', () => {
    it('deletes a group at its own address and completes on an empty 204', async () => {
      const pending = firstValueFrom(service.deleteRoleGroup(ROLE_GROUP_ID));

      const request = httpMock.expectOne(ROLE_GROUP_URL);

      expect(request.request.method).toBe('DELETE');
      expect(request.request.url).toBe(ROLE_GROUP_URL);
      expect(request.request.body).toBeNull();
      expect(request.request.params.keys()).toEqual([]);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('issues exactly one request, taking no pre-emptive read of the group members', async () => {
      //  MIGRATION: the conflict is surfaced, never pre-empted. The legacy screen hid its delete control
      //  instead of refusing the operation: `Website/admin/Security/Roles.ascx.vb` sets that control's
      //  visibility to the negation of the bound role list being non-empty, inside the branch opened that
      //  also hides the edit link when the selector holds a negative value. Its enclosing read is the unpaged
      //  bind.
      //
      // Measured first-hand against the file in this repository.
      //
      //  That guard becomes the server's refusal. A client-side pre-check would be business logic in a client
      //  restricted to API communication, and a client-side count is a race against any other administrator.
      //  `expectOne` plus the `verify()` in `afterEach` is the whole proof: a preliminary read would leave a
      //  second request unconsumed and fail the run.
      const pending = firstValueFrom(service.deleteRoleGroup(ROLE_GROUP_ID));

      const request = httpMock.expectOne(ROLE_GROUP_URL);
      expect(request.request.method).toBe('DELETE');

      request.flush(null, { status: 204, statusText: 'No Content' });
      await expectAsync(pending).toBeResolved();
    });

    it('lets a 409 problem document propagate byte for byte, neither swallowed nor retried', async () => {
      const settled = firstValueFrom(service.deleteRoleGroup(ROLE_GROUP_ID)).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );

      const request = httpMock.expectOne(ROLE_GROUP_URL);
      expect(request.request.method).toBe('DELETE');

      request.flush(GROUP_IN_USE_PROBLEM, {
        status: 409,
        statusText: 'Conflict',
        headers: { 'Content-Type': 'application/problem+json' },
      });

      const outcome: unknown = await settled;

      // Narrowed by a guard rather than by a cast, so nothing here is asserted about a type the runtime has
      // not confirmed.
      if (!(outcome instanceof HttpErrorResponse)) {
        throw new Error(`Expected the 409 to propagate as a failure, got ${String(outcome)}`);
      }

      expect(outcome.status).toBe(409);
      // The WHOLE document, compared as one value. Turning a status into words belongs to the error
      // interceptor and the form-error helper, neither of which this test bed registers, so no individual
      // member is read and no message is asserted. That also sidesteps the workspace's ban on dotted access
      // into an index signature: there is no property access to get wrong.
      expect(outcome.error).toEqual(GROUP_IN_USE_PROBLEM);
      expect((outcome.error as { readonly status: number }).status)
        .withContext('the body agrees with the transport, as a real response does')
        .toBe(409);
    });

    it('does not retry a refusal', async () => {
      const settled = firstValueFrom(service.deleteRoleGroup(ROLE_GROUP_ID)).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );

      httpMock
        .expectOne(ROLE_GROUP_URL)
        .flush(GROUP_IN_USE_PROBLEM, { status: 409, statusText: 'Conflict' });

      await settled;

      // A retry would open a second request, which `verify()` in `afterEach` would fail. The explicit
      // assertion states the intent for a reader who has not internalised that.
      expect(httpMock.match(ROLE_GROUP_URL).length)
        .withContext('a refusal is reported to the caller, never retried')
        .toBe(0);
    });
  });

  // Cross-cutting: what the service must not add.

  /**
   * The five operations of the eleven below whose endpoint answers with a payload.
   */
  const RETURNS_A_PAYLOAD: ReadonlySet<string> = new Set([
    `GET ${ROLE_ZERO_URL}`,
    `POST ${ROLES_URL}`,
    `PUT ${ROLE_URL}`,
    `GET ${ROLE_GROUPS_URL}`,
    `POST ${ROLE_GROUPS_URL}`,
    `GET ${ROLE_GROUP_URL}`,
    `PUT ${ROLE_GROUP_URL}`,
    `GET ${USER_ROLES_URL}`,
  ]);

  /**
   * The body the named endpoint answers with, so a fixture is as demanding as the server.
   *
   * @param method The request method.
   * @param url The request url, without its query string.
   * @returns The response body, or null for the operations that answer with none.
   */
  function bodyFor(method: string, url: string): object | null {
    const operation = `${method} ${url}`;

    if (operation === `GET ${ROLE_GROUPS_URL}`) {
      return envelope([roleGroup(0)]);
    }

    if (operation === `GET ${USER_ROLES_URL}`) {
      return envelope([ROLE_LIST_ITEM]);
    }

    if (operation.endsWith(ROLE_GROUPS_URL) || url.startsWith(ROLE_GROUPS_URL)) {
      return RETURNS_A_PAYLOAD.has(operation) ? envelope(roleGroup(0)) : null;
    }

    return RETURNS_A_PAYLOAD.has(operation) ? envelope(ROLE) : null;
  }

  describe('header discipline', () => {
    it('sets neither a bearer token nor a correlation identifier on any operation', async () => {
      // The bearer token and the correlation identifier are applied by two of the three interceptors
      // `app.config.ts` registers, and this test bed deliberately registers none of them. Their ABSENCE here
      // is therefore the assertion: a service that had started setting either header itself would be
      // duplicating an interceptor's work and would keep passing every other case in this file.
      const settled: Promise<unknown>[] = [];

      settled.push(firstValueFrom(service.getRole(ROLE_ID_ZERO)));
      settled.push(firstValueFrom(service.createRole(CREATE_ROLE_REQUEST)));
      settled.push(firstValueFrom(service.updateRole(ROLE_ID, UPDATE_ROLE_REQUEST)));
      settled.push(firstValueFrom(service.deleteRole(ROLE_ID)));
      settled.push(firstValueFrom(service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST)));
      settled.push(firstValueFrom(service.removeUser(ROLE_ID, USER_ID)));
      settled.push(firstValueFrom(service.listRoleGroups()));
      settled.push(firstValueFrom(service.createRoleGroup(CREATE_ROLE_GROUP_REQUEST)));
      settled.push(firstValueFrom(service.getRoleGroup(ROLE_GROUP_ID)));
      settled.push(firstValueFrom(service.updateRoleGroup(ROLE_GROUP_ID, UPDATE_ROLE_GROUP_REQUEST)));
      settled.push(firstValueFrom(service.deleteRoleGroup(ROLE_GROUP_ID)));
      settled.push(firstValueFrom(service.listRolesHeldByUser(USER_ID)));

      const opened = httpMock.match(() => true);

      expect(opened.length)
        .withContext('twelve param-free operations, one request each')
        .toBe(12);

      for (const request of opened) {
        expect(request.request.headers.has('Authorization'))
          .withContext(`${request.request.method} ${request.request.url} must not set a token`)
          .toBeFalse();
        expect(request.request.headers.has('X-Correlation-Id'))
          .withContext(`${request.request.method} ${request.request.url} must not set a trace id`)
          .toBeFalse();

        // Answered as each endpoint really answers, rather than with one 204 for all eleven. The shortcut of
        // flushing an empty body everywhere was only viable while nothing inspected it; the transport now
        // DECODES every payload it declares, so a read answered with `null` is refused at the boundary
        // exactly as a drifted server response would be — and this case would then fail for a reason that
        // has nothing to do with the headers it exists to assert.
        request.flush(bodyFor(request.request.method, request.request.url), {
          status: RETURNS_A_PAYLOAD.has(request.request.method + ' ' + request.request.url)
            ? 200
            : 204,
          statusText: 'OK',
        });
      }

      await Promise.all(settled);
    });

    it('sets neither header on the two paged listings either', async () => {
      const roles = firstValueFrom(service.listRoles(PAGED_REQUEST));
      const members = firstValueFrom(service.listUsers(ROLE_ID, PAGED_REQUEST));

      const opened = httpMock.match(() => true);
      expect(opened.length).toBe(2);

      for (const request of opened) {
        expect(request.request.headers.has('Authorization')).toBeFalse();
        expect(request.request.headers.has('X-Correlation-Id')).toBeFalse();
        request.flush(pageOf([], 0));
      }

      await expectAsync(roles).toBeResolved();
      await expectAsync(members).toBeResolved();
    });
  });
  // The response contract is checked, not asserted.
  //
  //  The most consequential member of this contract is a SINGLE CHARACTER. `"m"`, `"Monthly"` and the number
  //  `2` would every one of them read as a `BillingFrequency` to the compiler — the interface is erased — and
  //  then fall through the fee-schedule switch to its default branch, charging a paid role on the wrong cycle
  //  or on none at all. Nothing downstream could ever reveal it. These cases require the OBSERVABLE TO FAIL
  //  instead.
  describe('refuses a response that does not match its contract', () => {
    // The shared paged request carries an ordering and a search as well as its two coordinates, so the url
    // the backend receives carries all five. Matching on a partial query string finds nothing.
    const PAGED_QUERY =
      `?pageIndex=${PAGED_REQUEST.pageIndex}&pageSize=${PAGED_REQUEST.pageSize}` +
      `&sortBy=${PAGED_REQUEST.sortBy}&sortDir=${PAGED_REQUEST.sortDir}` +
      `&query=${PAGED_REQUEST.query}`;
    const PAGED_ROLES_URL = `${ROLES_URL}${PAGED_QUERY}`;
    const PAGED_MEMBERS_URL = `${ROLE_MEMBERS_URL}${PAGED_QUERY}`;

    /**
     * Asserts that answering the one pending request with `body` fails at `path`.
     *
     * @param source The call under test.
     * @param url The url the call addresses.
     * @param body The malformed body to answer with.
     * @param path The member path the violation must name.
     */
    function expectViolationAt(
      source: Observable<unknown>,
      url: string,
      body: object,
      path: string,
    ): void {
      const values: unknown[] = [];
      const failures: unknown[] = [];

      source.subscribe({
        next: (value: unknown) => values.push(value),
        error: (failure: unknown) => failures.push(failure),
      });

      httpMock.expectOne(url).flush(body);

      expect(values).toEqual([]);
      expect(failures.length).toBe(1);
      expect(isContractViolation(failures[0])).toBeTrue();

      if (isContractViolation(failures[0])) {
        expect(failures[0].path).toBe(path);
      }
    }

    it('refuses a billing frequency spelled as a word', () => {
      expectViolationAt(
        service.getRole(ROLE_ID_ZERO),
        ROLE_ZERO_URL,
        envelope({ ...ROLE, billingFrequency: 'Monthly' }),
        'response.data.billingFrequency',
      );
    });

    it('carries a lower-case stored code through verbatim rather than folding its case', () => {
      // The subtlest case in the file, and it is an admission rather than a refusal.
      //
      //    no arm of the fee schedule. That reasoning inverted the server's contract. The API's persistence
      //    read is deliberately CASE-SENSITIVE and its response converter is lossless, and the converter's
      //    own note records why: the inbound path up-cases what a CALLER sends, while up-casing a STORED
      //    `'m'` would change how an existing row reads and would rewrite its byte on the next update of that
      //    row. So a stored `'m'` really does travel, and the only honest thing a client can do is carry it
      //    as the character it is — refusing it would make the role unreadable, and up-casing it here would
      //    silently reinterpret stored data.
      //
      //  What a client may NOT do is send it back: the write vocabulary stays closed, which the write cases
      //  in this file assert separately.
      const values: unknown[] = [];

      service.getRole(ROLE_ID_ZERO).subscribe({ next: (value: unknown) => values.push(value) });

      httpMock
        .expectOne(ROLE_ZERO_URL)
        .flush(envelope({ ...ROLE, billingFrequency: 'm', trialFrequency: 'y' }));

      expect(values.length).toBe(1);

      const role = (values[0] as ApiResponse<Role>).data;

      expect(role.billingFrequency)
        .withContext('the stored byte, not the upper-case code it resembles')
        .toBe('m');
      expect(role.trialFrequency).toBe('y');
    });

    it('admits the shipped legacy codes that fall outside the published vocabulary', () => {
      // The case this whole split exists for, and it is not hypothetical. A decoder closed to the six
      // published codes therefore refused a perfectly valid response, and one such row made the role
      // unreadable for good: retrying reproduced the refusal deterministically, because the stored data was
      // the cause.
      for (const code of ['4', '0']) {
        const values: unknown[] = [];

        service.getRole(ROLE_ID_ZERO).subscribe({ next: (value: unknown) => values.push(value) });

        httpMock
          .expectOne(ROLE_ZERO_URL)
          .flush(envelope({ ...ROLE, billingFrequency: code, trialFrequency: code }));

        expect(values.length).withContext(`the stored code ${code} must be admitted`).toBe(1);

        const role = (values[0] as ApiResponse<Role>).data;

        expect(role.billingFrequency).toBe(code);
        expect(role.trialFrequency).toBe(code);
      }
    });

    it('does not fail a whole page of roles because one row holds a legacy code', () => {
      // The consequence at the scale that matters. The listing decodes every row, so a refusal on one row is
      // a refusal of the page: an administrator could not see ANY role because one legacy row existed. Both
      // rows must arrive, each carrying its own stored character.
      const values: unknown[] = [];

      service.listRoles(PAGED_REQUEST).subscribe({ next: (value: unknown) => values.push(value) });

      httpMock.expectOne(PAGED_ROLES_URL).flush({
        items: [
          { ...ROLE_LIST_ITEM, roleId: 0, billingFrequency: '4' },
          { ...ROLE_LIST_ITEM, roleId: 1, billingFrequency: 'M' },
        ],
        meta: { totalCount: 2, pageIndex: 0, pageSize: PAGED_REQUEST.pageSize, totalPages: 1 },
      });

      expect(values.length).toBe(1);

      const page = values[0] as PagedResponse<RoleListItem>;

      expect(page.items.length).toBe(2);
      expect(page.items.map((row) => row.billingFrequency)).toEqual(['4', 'M']);
    });

    it('refuses a frequency of more than one character even when it starts with a code', () => {
      // The length is what the contract fixes, because the column is `char(1)`. Accepting a longer value on
      // its first character would read `"Monthly"` as the month code, which is the one wrong answer
      // indistinguishable from a right one.
      expectViolationAt(
        service.getRole(ROLE_ID_ZERO),
        ROLE_ZERO_URL,
        envelope({ ...ROLE, billingFrequency: 'M onth' }),
        'response.data.billingFrequency',
      );
    });

    it('refuses an empty frequency, which is a present member carrying no code', () => {
      // Distinct from `null`, which is how the contract states "no billing terms" and is admitted by the
      // case below. An empty string is a member that arrived and said nothing.
      expectViolationAt(
        service.getRole(ROLE_ID_ZERO),
        ROLE_ZERO_URL,
        envelope({ ...ROLE, trialFrequency: '' }),
        'response.data.trialFrequency',
      );
    });

    it('refuses a billing frequency sent as its ordinal', () => {
      expectViolationAt(
        service.getRole(ROLE_ID_ZERO),
        ROLE_ZERO_URL,
        envelope({ ...ROLE, trialFrequency: 2 }),
        'response.data.trialFrequency',
      );
    });

    it('admits every published code, and a null for an unpaid role', () => {
      // The counterpart case: the decoder must admit the whole published vocabulary, not merely reject
      // outside it. `N` is the unpaid code and `null` means no term at all.
      for (const code of ['N', 'O', 'D', 'W', 'M', 'Y', null]) {
        const values: unknown[] = [];

        service.getRole(ROLE_ID_ZERO).subscribe({
          next: (value: unknown) => values.push(value),
        });

        httpMock
          .expectOne(ROLE_ZERO_URL)
          .flush(envelope({ ...ROLE, billingFrequency: code, trialFrequency: code }));

        expect(values.length)
          .withContext(`the code ${String(code)} must be admitted`)
          .toBe(1);
      }
    });

    it('admits role zero, which is the first role the schema ever creates', () => {
      // `Roles.RoleID` is `IDENTITY(0, 1)`, so zero is an ordinary identifier and no decoder may treat it as
      // absent.
      const values: unknown[] = [];

      service.getRole(ROLE_ID_ZERO).subscribe({ next: (v: unknown) => values.push(v) });

      httpMock.expectOne(ROLE_ZERO_URL).flush(envelope({ ...ROLE, roleId: 0 }));

      expect(values.length).toBe(1);
    });

    it('refuses a membership whose expiry is not a date', () => {
      // An assignment's status is derived from its two bounds. A malformed date would be compared against
      // `Invalid Date`, whose every comparison is false — so an EXPIRED membership would be presented as
      // active and the person would keep an entitlement they had lost.
      expectViolationAt(
        service.listUsers(ROLE_ID, PAGED_REQUEST),
        PAGED_MEMBERS_URL,
        {
          items: [{ ...USER_ROLE, expiryDate: 'whenever' }],
          meta: { totalCount: 1, pageIndex: 0, pageSize: PAGED_REQUEST.pageSize, totalPages: 1 },
        },
        'response.items[0].expiryDate',
      );
    });

    it('refuses a role page with no metadata', () => {
      expectViolationAt(
        service.listRoles(PAGED_REQUEST),
        PAGED_ROLES_URL,
        { items: [ROLE_LIST_ITEM] },
        'response.meta',
      );
    });

    it('refuses a role-group list that is not an array', () => {
      expectViolationAt(
        service.listRoleGroups(),
        ROLE_GROUPS_URL,
        envelope(roleGroup(0)),
        'response.data',
      );
    });
  });

  // Who announces a failure.
  describe('marks every request as presented by its caller', () => {
    it('marks every one of the fourteen operations', () => {
      const swallow = { error: () => undefined };

      service.listRoles(PAGED_REQUEST).subscribe(swallow);
      service.getRole(ROLE_ID).subscribe(swallow);
      service.createRole(CREATE_ROLE_REQUEST).subscribe(swallow);
      service.updateRole(ROLE_ID, UPDATE_ROLE_REQUEST).subscribe(swallow);
      service.deleteRole(ROLE_ID).subscribe(swallow);
      service.listUsers(ROLE_ID, PAGED_REQUEST).subscribe(swallow);
      service.assignUser(ROLE_ID, ASSIGNMENT_REQUEST).subscribe(swallow);
      service.removeUser(ROLE_ID, USER_ID).subscribe(swallow);
      service.listRoleGroups().subscribe(swallow);
      service.createRoleGroup(CREATE_ROLE_GROUP_REQUEST).subscribe(swallow);
      service.getRoleGroup(ROLE_GROUP_ID).subscribe(swallow);
      service.updateRoleGroup(ROLE_GROUP_ID, UPDATE_ROLE_GROUP_REQUEST).subscribe(swallow);
      service.deleteRoleGroup(ROLE_GROUP_ID).subscribe(swallow);
      service.listRolesHeldByUser(USER_ID).subscribe(swallow);

      const issued = httpMock.match(() => true);

      expect(issued.length).toBe(14);

      for (const pending of issued) {
        expect(pending.request.context.get(PRESENTED_IN_CONTEXT))
          .withContext(`${pending.request.method} ${pending.request.urlWithParams} is unmarked`)
          .toBeTrue();
      }

      for (const pending of issued) {
        pending.flush(null, { status: 500, statusText: 'Server Error' });
      }
    });
  });
});
