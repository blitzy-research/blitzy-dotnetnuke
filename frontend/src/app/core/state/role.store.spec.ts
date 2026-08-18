import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  DEFAULT_ROLE_GROUP_FILTER,
  ROLES_PAGE_SIZE,
  RoleStore,
  billingTermsBound,
} from './role.store';

import { DEFAULT_PAGE_SIZE } from '../models/paged-result.model';

import type { TestRequest } from '@angular/common/http/testing';
import type { Signal } from '@angular/core';
import type { ApiMeta, ApiResponse, PagedResponse } from '../models/paged-result.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import type {
  BillingFrequency,
  CreateRoleGroupRequest,
  CreateRoleRequest,
  Role,
  RoleAssignmentRequest,
  RoleGroup,
  RoleListItem,
  RoleStatus,
  UpdateRoleGroupRequest,
  UpdateRoleRequest,
  UserRole,
} from '../models/role.model';
import type {
  BillingTermsBound,
  RoleGroupFilter,
  RolePageCoordinate,
  RoleStoreFailure,
  RoleStoreOperation,
} from './role.store';

// ---------------------------------------------------------------------------
// THE ADDRESSES UNDER TEST — every one root-relative
// ---------------------------------------------------------------------------

/** The role collection. */
const ROLES_URL = '/api/v1/roles';

/**
 * One role, addressed with the identity seed itself. The role table is declared with an identity seed of
 * zero (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115`), so the FIRST
 * role every tenant creates carries the key zero — and the shipped data seeds a registered-users role at
 * `:L7194`.
 */
const ROLE_ZERO_URL = '/api/v1/roles/0';

/** One role, addressed with an ordinary positive key, for contrast with the seed. */
const ROLE_SEVEN_URL = '/api/v1/roles/7';

/** The accounts holding the seed-keyed role. */
const ROLE_ZERO_MEMBERS_URL = '/api/v1/roles/0/users';

/** The accounts holding an ordinarily-keyed role. */
const ROLE_SEVEN_MEMBERS_URL = '/api/v1/roles/7/users';

/** One account's membership of one role. */
const ROLE_SEVEN_MEMBER_URL = '/api/v1/roles/7/users/42';

/** The role-group collection. */
const ROLE_GROUPS_URL = '/api/v1/role-groups';

/** One role group, addressed with its own identity seed of zero. */
const ROLE_GROUP_ZERO_URL = '/api/v1/role-groups/0';

/** The memberships-of-one-account address, for an ordinary account key. */
const USER_ROLES_URL = '/api/v1/users/42/roles';

/** The same address for the account keyed nought, which no truthiness test may drop. */
const USER_ZERO_ROLES_URL = '/api/v1/users/0/roles';

/** An ordinary account key, for the per-account membership read. */
const HELD_ROLES_USER_ID = 42;

/** A second account key, so a switch of subject is provable. */
const OTHER_HELD_ROLES_USER_ID = 43;

/** The second account's address. */
const OTHER_USER_ROLES_URL = '/api/v1/users/43/roles';

// ---------------------------------------------------------------------------
// WIRE VOCABULARY
// ---------------------------------------------------------------------------

/**
 * The prefix the server wraps a failure code in before publishing it. The code arrives in exactly one
 * place and it is not a member of its own: the server writes it into the problem document's type member.
 */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** The conflict codes the server publishes for this feature, spelled as IT spells them. */
const CONFLICT_CODE = Object.freeze({
  duplicateRoleName: 'role.name_duplicate',
  duplicateRoleGroupName: 'role_group.name_duplicate',
  protectedAssignment: 'role_assignment.protected',
} as const);

/** A fixed correlation value, shaped like the trace parent the server derives one from. */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';

/** A second fixed correlation value, for proving which of the two members wins. */
const CORRELATION_ID = 'e7bf0e34-9c1a-4f2f-9c0e-4a1d5c8b2f10';

const MIN_INSTANT = '0001-01-01T00:00:00Z';

const PERPETUAL_INSTANT = '9999-12-31T00:00:00Z';

/** An instant already in the past, which is what a back-dated expiry bound looks like. */
const PAST_INSTANT = '2019-03-14T00:00:00Z';

/** An instant in the future, for an assignment that has not lapsed. */
const FUTURE_INSTANT = '2099-06-01T00:00:00Z';

/**
 * The six persisted frequency codes, typed as the model's own union. Typing the table this way is what
 * makes a folded case, a word-spelled unit or an integer substitution a COMPILE failure rather than a
 * runtime surprise.
 */
const FREQUENCY_CODES: readonly BillingFrequency[] = Object.freeze([
  'N',
  'O',
  'D',
  'W',
  'M',
  'Y',
] as const);

/** The assignment-status vocabulary the model declares. */
const ROLE_STATUS_VALUES: readonly RoleStatus[] = Object.freeze([
  'Pending',
  'Active',
  'Expired',
] as const);

/**
 * The legacy pseudo-role identifiers, as STRING constants.
 * `Library/Components/Shared/Globals.vb:L95-L98`.
 */
const LEGACY_PSEUDO_ROLE_IDS: readonly string[] = Object.freeze([
  '-1',
  '-2',
  '-3',
  '-4',
] as const);

// FIXTURE FACTORIES
// The member spellings are taken from the contract module, not guessed.

/**
 * One row of the role listing.
 *
 * @param overrides Members to replace on the default row.
 * @returns One listing row.
 */
function aRoleListItem(overrides: Partial<RoleListItem> = {}): RoleListItem {
  return {
    roleId: 0,
    roleName: 'Administrators',
    description: 'Portal Administrators',
    serviceFee: 0,
    billingPeriod: -1,
    billingFrequency: 'M',
    trialFee: 0,
    trialPeriod: -1,
    trialFrequency: 'N',
    isPublic: false,
    autoAssignment: false,
    ...overrides,
  };
}

/**
 * One role in full, as the detail endpoint publishes it.
 *
 * @param overrides Members to replace on the default role.
 * @returns One role.
 */
function aRole(overrides: Partial<Role> = {}): Role {
  return {
    roleId: 0,
    roleGroupId: null,
    roleName: 'Administrators',
    description: 'Portal Administrators',
    billingFrequency: 'M',
    serviceFee: 0,
    trialFrequency: 'N',
    trialPeriod: -1,
    billingPeriod: -1,
    trialFee: 0,
    isPublic: false,
    autoAssignment: false,
    rsvpCode: '',
    iconFile: null,
    // Present on every served role. Declared BEFORE the spread so a case may replace it - the store carries
    // it through a read and a write untouched, and a case that needed two different revisions could not
    // express them otherwise.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * One role group. The tenant key defaults to minus one, which is a REAL tenant: the portal table is
 * declared with an identity seed of minus one (`01.00.00.SqlDataProvider:L77`), so the first tenant ever
 * created carries minus one and the second carries zero — while the absent-integer marker is also minus
 * one.
 *
 * @param overrides Members to replace on the default group.
 * @returns One role group.
 */
function aRoleGroup(overrides: Partial<RoleGroup> = {}): RoleGroup {
  return {
    roleGroupId: 0,
    portalId: -1,
    roleGroupName: 'Site Groups',
    description: '',
    ...overrides,
  };
}

/**
 * One user-to-role assignment.
 *
 * @param overrides Members to replace on the default assignment.
 * @returns One assignment.
 */
function anAssignment(overrides: Partial<UserRole> = {}): UserRole {
  return {
    userRoleId: 1,
    userId: 42,
    username: 'host',
    displayName: 'SuperUser Account',
    roleId: 7,
    roleName: 'Subscribers',
    effectiveDate: MIN_INSTANT,
    expiryDate: FUTURE_INSTANT,
    ...overrides,
  };
}

/**
 * A paged wire envelope.
 *
 * @param items The page's rows.
 * @param totalCount The total across every page.
 * @param pageIndex The zero-based index of this page.
 * @returns The paged envelope.
 */
function pageOf<T>(
  items: readonly T[],
  totalCount: number,
  pageIndex = 0,
): PagedResponse<T> {
  const pageSize = DEFAULT_PAGE_SIZE;
  const meta: ApiMeta = {
    totalCount,
    pageIndex,
    pageSize,
    totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / pageSize),
  };

  return { items, meta };
}

/**
 * A single-payload wire envelope.
 *
 * @param data The payload.
 * @returns The envelope, with no paging metadata.
 */
function envelopeOf<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A problem document, shaped as the server's own exception handler shapes one.
 *
 * @param status The status to report, in the body and expected on the response.
 * @param code The failure code, or absence for a failure the server did not classify.
 * @param detail The human-readable explanation, held verbatim and never laundered.
 * @param extra Further standard members to merge in.
 * @returns The document.
 */
function aProblem(
  status: number,
  code: string | null,
  detail: string,
  extra: Partial<ProblemDetails> = {},
): ProblemDetails {
  const base: ProblemDetails = {
    title: 'Request refused',
    status,
    detail,
    instance: ROLES_URL,
    traceId: TRACE_ID,
  };

  return code === null ? { ...base, ...extra } : { ...base, type: `${FAILURE_TYPE_PREFIX}${code}`, ...extra };
}

/**
 * A model-state refusal, carrying the per-member dictionary. The dictionary's keys are the server's own
 * model-state keys and are NOT lower-camel: it publishes them as the request contract declares them.
 *
 * @param fieldErrors The per-member messages.
 * @param detail The overall explanation.
 * @returns The validation document.
 */
function aValidationProblem(
  fieldErrors: Readonly<Record<string, readonly string[]>>,
  detail: string,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.rejected`,
    title: 'One or more validation errors occurred.',
    status: 400,
    detail,
    instance: ROLES_URL,
    traceId: TRACE_ID,
    errors: fieldErrors,
  };
}

// ---------------------------------------------------------------------------
// ASSERTION HELPERS
// ---------------------------------------------------------------------------

/**
 * Narrows away absence by throwing, so no case needs a cast or a suppression comment.
 *
 * @param value The value to narrow.
 * @param what What was expected, for the failure message.
 * @returns The value, narrowed.
 */
function present<T>(value: T | null | undefined, what: string): T {
  if (value === null || value === undefined) {
    throw new Error(`Expected ${what} to be present, but it was absent.`);
  }

  return value;
}

/**
 * Every member name the store publishes, instance members and methods alike.
 *
 * @param store The store to enumerate.
 * @returns Every own and prototype member name, the constructor excluded.
 */
function publishedMembers(store: RoleStore): readonly string[] {
  const own: readonly string[] = Object.getOwnPropertyNames(store);
  const inherited: readonly string[] = Object.getOwnPropertyNames(
    RoleStore.prototype,
  ).filter((name) => name !== 'constructor');

  return [...own, ...inherited];
}

describe('RoleStore', () => {
  let store: RoleStore;
  let httpMock: HttpTestingController;

  /**
   * Claims the single open request at one PATH, issued with one verb. Always the predicate form, and
   * always matched on the verb and the PATH rather than by whole-string comparison.
   *
   * @param method The verb the request must carry.
   * @param url The path the request must address, without its query.
   * @returns The one matching open request.
   */
  function expectRequest(method: string, url: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
    );
  }

  /**
   * Claims the single open read at one path.
   *
   * @param url The path the read must address.
   * @returns The one matching open read.
   */
  function expectGet(url: string): TestRequest {
    return expectRequest('GET', url);
  }

  /**
   * Claims the single open creation at one path.
   *
   * @param url The path the creation must address.
   * @returns The one matching open creation.
   */
  function expectPost(url: string): TestRequest {
    return expectRequest('POST', url);
  }

  /**
   * Claims the single open replacement at one path.
   *
   * @param url The path the replacement must address.
   * @returns The one matching open replacement.
   */
  function expectPut(url: string): TestRequest {
    return expectRequest('PUT', url);
  }

  /**
   * Claims the single open removal at one path.
   *
   * @param url The path the removal must address.
   * @returns The one matching open removal.
   */
  function expectDelete(url: string): TestRequest {
    return expectRequest('DELETE', url);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // The real client FIRST and the testing backend SECOND. The testing provider overrides the backend
        // the real one installed, so the order is not cosmetic: reversed, the genuine backend survives and
        // these cases attempt live requests.
        provideHttpClient(),
        provideHttpClientTesting(),
        // Listed explicitly so each case runs against a freshly constructed store whose slices start at
        // their initial values, independent of the decorator's own root registration.
        RoleStore,
      ],
    });

    store = TestBed.inject(RoleStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The load-bearing assertion of this file.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // THE PUBLISHED SURFACE, AND ITS READ-ONLY GUARANTEE
  // -------------------------------------------------------------------------

  describe('the published surface and its read-only guarantee', () => {
    it('publishes every state slice as a signal that cannot be written from outside', () => {
      const projections: readonly (readonly [string, Signal<unknown>])[] = [
        ['roles', store.roles],
        ['roleGroups', store.roleGroups],
        ['groupFilter', store.groupFilter],
        ['selectedRole', store.selectedRole],
        ['assignments', store.assignments],
        ['assignmentsRoleId', store.assignmentsRoleId],
        ['rolesPage', store.rolesPage],
        ['assignmentsPage', store.assignmentsPage],
        ['rolesLoading', store.rolesLoading],
        ['roleGroupsLoading', store.roleGroupsLoading],
        ['selectedRoleLoading', store.selectedRoleLoading],
        ['assignmentsLoading', store.assignmentsLoading],
        ['saving', store.saving],
        ['failure', store.failure],
        ['rolesHeldByUser', store.rolesHeldByUser],
        ['heldRolesUserId', store.heldRolesUserId],
        ['heldRolesLoading', store.heldRolesLoading],
      ];

      for (const [name, projection] of projections) {
        expect('set' in projection)
          .withContext(`${name} must not expose a setter to a consumer`)
          .toBeFalse();
        expect('update' in projection)
          .withContext(`${name} must not expose an updater to a consumer`)
          .toBeFalse();
      }
    });

    it('publishes every derived projection as a signal that cannot be written from outside', () => {
      const derived: readonly (readonly [string, Signal<unknown>])[] = [
        ['roleItems', store.roleItems],
        ['rolesMeta', store.rolesMeta],
        ['assignmentItems', store.assignmentItems],
        ['assignmentsMeta', store.assignmentsMeta],
        ['probedAssignment', store.probedAssignment],
        ['probedAssignmentKey', store.probedAssignmentKey],
        ['busy', store.busy],
        ['hasRoleGroups', store.hasRoleGroups],
        ['selectedRoleGroupId', store.selectedRoleGroupId],
        ['selectedRoleGroup', store.selectedRoleGroup],
        ['canDeleteSelectedGroup', store.canDeleteSelectedGroup],
        ['selectedRoleBillingTerms', store.selectedRoleBillingTerms],
        ['selectedRoleTrialTerms', store.selectedRoleTrialTerms],
        ['selectedRoleIsPaid', store.selectedRoleIsPaid],
      ];

      for (const [name, projection] of derived) {
        expect('set' in projection)
          .withContext(`${name} is derived and must not be writable`)
          .toBeFalse();
        expect('update' in projection)
          .withContext(`${name} is derived and must not be writable`)
          .toBeFalse();
      }
    });

    it('publishes every command this specification exercises', () => {
      const commands: readonly string[] = [
        'loadRoleAdministration',
        'loadRoles',
        'loadRoleGroups',
        'selectRole',
        'clearSelectedRole',
        'loadAssignments',
        'reloadAssignments',
        'probeAssignment',
        'clearProbedAssignment',
        'setGroupFilter',
        'setRolesSort',
        'setRolesQuery',
        'setAssignmentsPage',
        'createRole',
        'updateRole',
        'deleteRole',
        'assignUser',
        'removeAssignment',
        'createRoleGroup',
        'updateRoleGroup',
        'deleteRoleGroup',
        'loadRolesHeldByUser',
        'clearRolesHeldByUser',
        'clearError',
        'reset',
      ];
      const published: readonly string[] = publishedMembers(store);

      for (const command of commands) {
        expect(published.includes(command))
          .withContext(`${command} must be part of the store's public contract`)
          .toBeTrue();
      }
    });

    it('starts every slice at its documented initial value', () => {
      expect(store.roleItems()).toEqual([]);
      expect(store.roleGroups()).toEqual([]);
      expect(store.assignmentItems()).toEqual([]);
      expect(store.selectedRole()).toBeNull();
      expect(store.assignmentsRoleId()).toBeNull();
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.hasRoleGroups()).toBeFalse();

      // ⚠ NOT-ASKED IS A DIFFERENT FACT FROM ASKED-AND-EMPTY, and both hold an empty group set. A screen
      // validating an addressed narrowing against the set must be able to tell them apart.
      expect(store.roleGroupsSettled()).toBeFalse();

      // No account is the subject until one is asked for, and `null` is that fact. It is a
      // DIFFERENT fact from an account that holds no roles, which is the empty sequence.
      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });
  });

  // THE ROLES ONE ACCOUNT HOLDS

  describe('the roles one account holds', () => {
    it('reads the memberships at the account address and publishes them in their own slice', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expectGet(USER_ROLES_URL).flush(
        envelopeOf([aRoleListItem({ roleId: 0, roleName: 'Administrators' })]),
      );

      expect(store.rolesHeldByUser()).toEqual([
        aRoleListItem({ roleId: 0, roleName: 'Administrators' }),
      ]);
      expect(store.heldRolesUserId()).toBe(HELD_ROLES_USER_ID);
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('leaves the BROWSABLE listing completely alone, so neither answer overwrites the other', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 7, roleName: 'Subscribers' })], 1));

      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(
        envelopeOf([aRoleListItem({ roleId: 0, roleName: 'Administrators' })]),
      );

      expect(store.roleItems().map((each) => each.roleName)).toEqual(['Subscribers']);
      expect(store.rolesMeta().totalCount).toBe(1);
      expect(present(store.rolesHeldByUser(), 'the narrowed answer').map((e) => e.roleName)).toEqual(
        ['Administrators'],
      );
    });

    it('addresses an account key of NOUGHT rather than dropping it from the path', () => {
      store.loadRolesHeldByUser(0);

      expectGet(USER_ZERO_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));

      expect(store.heldRolesUserId()).toBe(0);
      expect(store.rolesHeldByUser()).toHaveSize(1);
    });

    it('records the account at DISPATCH, so a screen knows who is being waited for', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.heldRolesUserId())
        .withContext('the subject is known before the answer arrives')
        .toBe(HELD_ROLES_USER_ID);
      expect(store.heldRolesLoading()).toBeTrue();

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));

      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('holds an account that belongs to NO role as the empty sequence, not as null', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));

      expect(store.rolesHeldByUser()).toEqual([]);
      expect(store.rolesHeldByUser()).not.toBeNull();
      expect(store.heldRolesUserId()).toBe(HELD_ROLES_USER_ID);
    });

    it('KEEPS the account as the subject when the read fails, while holding no answer for it', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expectGet(USER_ROLES_URL).flush(aProblem(403, null, 'Forbidden'), {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(store.rolesHeldByUser())
        .withContext('an empty set would read as "this account holds nothing"')
        .toBeNull();
      expect(store.heldRolesUserId())
        .withContext('the account is still what the listing is about, and its address still names it')
        .toBe(HELD_ROLES_USER_ID);
      expect(store.heldRolesLoading()).toBeFalse();
      expect(present(store.failure(), 'the reported failure').status).toBe(403);
    });

    it('cancels a read in flight before starting another, so the last subject asked for wins', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      const abandoned = expectGet(USER_ROLES_URL);

      store.loadRolesHeldByUser(OTHER_HELD_ROLES_USER_ID);

      expect(abandoned.cancelled)
        .withContext('the first read is released rather than left to answer later')
        .toBeTrue();
      expect(store.heldRolesUserId()).toBe(OTHER_HELD_ROLES_USER_ID);

      expectGet(OTHER_USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 7 })]));

      expect(present(store.rolesHeldByUser(), 'the answer').map((each) => each.roleId)).toEqual([7]);
      expect(store.heldRolesUserId()).toBe(OTHER_HELD_ROLES_USER_ID);
    });

    it('DISCARDS the previous account\u2019s answer the moment the subject changes', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));
      expect(store.rolesHeldByUser()).toHaveSize(1);

      store.loadRolesHeldByUser(OTHER_HELD_ROLES_USER_ID);

      expect(store.rolesHeldByUser())
        .withContext('no answer is held for the new subject until it answers')
        .toBeNull();
      expect(store.heldRolesUserId()).toBe(OTHER_HELD_ROLES_USER_ID);

      expectGet(OTHER_USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 7 })]));

      expect(present(store.rolesHeldByUser(), 'the new answer').map((each) => each.roleId)).toEqual([
        7,
      ]);
    });

    it('KEEPS the answer when the SAME account is re-read, so a refresh does not blank it', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));

      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.rolesHeldByUser())
        .withContext('the account on screen keeps its rows while they are re-read')
        .toHaveSize(1);

      expectGet(USER_ROLES_URL).flush(
        envelopeOf([aRoleListItem({ roleId: 0 }), aRoleListItem({ roleId: 7 })]),
      );

      expect(store.rolesHeldByUser()).toHaveSize(2);
    });

    it('stops treating any account as the subject on request, without contacting the server', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(envelopeOf([aRoleListItem({ roleId: 0 })]));

      store.clearRolesHeldByUser();

      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('releases the narrowed read\u2019s failure when the narrowing is cleared', () => {
      // ⚠ MEASURED IN A BROWSER, WHICH IS WHY THIS EXISTS. From `/roles?userId=999` - refused with `404`
      // "Portal -1 has no member bearing identifier 999" - pressing the on-screen affordance that returns
      // to the unnarrowed listing left the warning banner standing above a correct three-row listing,
      // unchanged across four seconds of sampling and reproduced twice.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(aProblem(404, null, 'Not Found'), {
        status: 404,
        statusText: 'Not Found',
      });

      expect(present(store.failure(), 'the refusal is recorded').status).toBe(404);

      store.clearRolesHeldByUser();

      expect(store.failure())
        .withContext('a refusal about an account the listing is no longer about is released')
        .toBeNull();
    });

    it('keeps an UNRELATED failure when the narrowing is cleared', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(aProblem(500, null, 'Server Error'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      store.clearRolesHeldByUser();

      expect(present(store.failure(), 'an unrelated refusal survives').status).toBe(500);
      expect(present(store.failure(), 'and it is still the listing read').operation).toBe('loadRoles');
    });

    it('cancels a read in flight when the narrowing is cleared, so no answer re-narrows it', () => {
      // ⚠ A RESPONSE ARRIVING AFTER THE CLEARING WOULD RE-NARROW THE LISTING with no command
      // on screen to explain it. Discarding the answer is not enough; the read is released.
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      const inFlight = expectGet(USER_ROLES_URL);

      store.clearRolesHeldByUser();

      expect(inFlight.cancelled).toBeTrue();
      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('counts itself busy while the membership read is outstanding', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.busy())
        .withContext('a screen must be able to show progress for this read too')
        .toBeTrue();

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));

      expect(store.busy()).toBeFalse();
    });

    it('returns the narrowing to its initial values on a reset, and releases the read', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      const inFlight = expectGet(USER_ROLES_URL);

      store.reset();

      expect(inFlight.cancelled).toBeTrue();
      expect(store.rolesHeldByUser()).toBeNull();
      expect(store.heldRolesUserId()).toBeUndefined();
      expect(store.heldRolesLoading()).toBeFalse();
    });

    it('publishes the narrowed answer read-only, so a consumer cannot write it', () => {
      expect('set' in store.rolesHeldByUser).toBeFalse();
      expect('update' in store.rolesHeldByUser).toBeFalse();
      expect('set' in store.heldRolesUserId).toBeFalse();
      expect('set' in store.heldRolesLoading).toBeFalse();
    });

    it('clears a previously reported failure when a new membership read begins', () => {
      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);
      expectGet(USER_ROLES_URL).flush(aProblem(500, null, 'Server Error'), {
        status: 500,
        statusText: 'Server Error',
      });
      expect(store.failure()).not.toBeNull();

      store.loadRolesHeldByUser(HELD_ROLES_USER_ID);

      expect(store.failure())
        .withContext('a fresh attempt is not presented beside the previous failure')
        .toBeNull();

      expectGet(USER_ROLES_URL).flush(envelopeOf([]));
    });
  });

  // -------------------------------------------------------------------------
  // ROLE IDENTITY — A KEY OF ZERO IS A REAL ROLE
  // -------------------------------------------------------------------------

  describe('role identity: a key of zero is a real role, and absence is a distinct value', () => {
    it('issues a request for the role keyed zero rather than treating the key as absent', () => {
      store.selectRole(0);

      // Matched on verb and PATH rather than by whole-string comparison, because the backend's string
      // matcher compares the address WITH its query and would couple the assertion to parameter order.
      const request = expectGet(ROLE_ZERO_URL);

      expect(request.request.url)
        .withContext('a truthiness guard on the key would issue no request at all')
        .toBe(ROLE_ZERO_URL);

      request.flush(envelopeOf(aRole({ roleId: 0 })));

      expect(present(store.selectedRole(), 'the selected role').roleId).toBe(0);
    });

    it('holds a role keyed zero unchanged, rather than dropping or defaulting it', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, roleName: 'Registered Users' })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleId).toBe(0);
      expect(held.roleName).toBe('Registered Users');
    });

    it('retains the seed key alongside ordinary keys through every derivation', () => {
      store.loadRoles();
      httpMock
        .expectOne((candidate) => candidate.url === ROLES_URL)
        .flush(
          pageOf(
            [
              aRoleListItem({ roleId: 0, roleName: 'Administrators' }),
              aRoleListItem({ roleId: 1, roleName: 'Registered Users' }),
              aRoleListItem({ roleId: 2, roleName: 'Subscribers' }),
            ],
            3,
          ),
        );

      // The derivation must be a projection and not a filter: a row whose key is the seed is
      // indistinguishable from a row whose key is absent to any predicate that tests the key for truth, and
      // the first role of every tenant is exactly that row.
      expect(store.roleItems().map((item) => item.roleId)).toEqual([0, 1, 2]);
      expect(store.roles().items.map((item) => item.roleId)).toEqual([0, 1, 2]);
      expect(store.rolesMeta().totalCount).toBe(3);
    });

    it('reads the assignments of the role keyed zero', () => {
      store.loadAssignments(0);

      const request = expectGet(ROLE_ZERO_MEMBERS_URL);

      request.flush(pageOf([anAssignment({ roleId: 0 })], 1));

      expect(store.assignmentsRoleId()).toBe(0);
      expect(present(store.assignmentItems()[0], 'the first assignment').roleId).toBe(0);
    });

    it('treats a scope of zero as present when re-reading, so the re-read is issued', () => {
      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      store.reloadAssignments();

      // The second request is the whole assertion: the scope in hand is zero, and a
      // truthiness guard would treat it as "no scope" and issue nothing.
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      expect(store.assignmentsRoleId()).toBe(0);
    });

    it('expresses "nothing in scope" as absence, and never as zero or a negative key', () => {
      const scope: number | null = store.assignmentsRoleId();

      expect(scope).toBeNull();
      expect(scope).not.toBe(0);
      expect(scope).not.toBe(-1);
      expect(scope).not.toBe(-2);
      expect(store.selectedRole()).toBeNull();
      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('issues no request when re-reading with nothing in scope', () => {
      store.reloadAssignments();

      // Asserted by the backend verification in the teardown: an unconsumed request would
      // fail it. Stated here as well so the intent is visible at the point of the case.
      httpMock.expectNone(() => true);
      expect(store.assignmentsRoleId()).toBeNull();
    });

    it('retains a tenant key of minus one and a tenant key of zero, both being real tenants', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(
          envelopeOf([
            aRoleGroup({ roleGroupId: 0, portalId: -1 }),
            aRoleGroup({ roleGroupId: 1, portalId: 0 }),
          ]),
        );

      // The tenant is not a query parameter on this feature at all — the server resolves it from the
      // request host and the caller's claims — so the honest place to prove both values survive is the
      // payload member that carries them.
      expect(store.roleGroups().map((group) => group.portalId)).toEqual([-1, 0]);
    });
  });

  // -------------------------------------------------------------------------
  // THE THREE-WAY GROUP NARROWING
  // -------------------------------------------------------------------------

  describe('the three-way group narrowing, and its two separate meanings of minus one', () => {
    it('starts at the ungrouped intent, which is the measured legacy default', () => {
      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.groupFilter().kind).not.toBe('AllRoles');
      expect(DEFAULT_ROLE_GROUP_FILTER.kind).toBe('GlobalRoles');
    });

    it('sends the every-role scope and no grouping key for the every-role intent', () => {
      store.setGroupFilter({ kind: 'AllRoles' });

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('scope')).toBe('All');
      // The pseudo-intent travels as a NAMED scope, never as a magic grouping key, so the
      // legacy every-role value cannot reach a persisted member.
      expect(request.request.params.has('roleGroupId'))
        .withContext('a pseudo-intent must not be encoded as a grouping key')
        .toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('sends the ungrouped scope and no grouping key for the ungrouped intent', () => {
      store.setGroupFilter({ kind: 'GlobalRoles' });

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('scope')).toBe('Ungrouped');
      expect(request.request.params.has('roleGroupId')).toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('sends the grouping key zero as a literal, because the grouping table also seeds at zero', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });

      const request = expectGet(ROLES_URL);

      // The second place on this feature where a truthiness guard would silently break: the
      // first group any tenant creates carries the key zero.
      expect(request.request.params.get('roleGroupId')).toBe('0');
      expect(request.request.params.has('scope'))
        .withContext('a real key and a named scope together is a contradiction the server refuses')
        .toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('publishes the grouping key for a real group and absence for either pseudo-intent', () => {
      expect(store.selectedRoleGroupId()).toBeNull();

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroupId())
        .withContext('the key zero is a real group and must not read as absence')
        .toBe(0);

      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('does not read minus one as absence when it arrives as a grouping key', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: -1 });

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('roleGroupId')).toBe('-1');
      request.flush(pageOf([], 0));

      expect(store.selectedRoleGroupId()).toBe(-1);
      expect(store.selectedRoleGroupId()).not.toBeNull();
    });

    it('resolves the narrowing against the loaded groups, and to absence for an unknown key', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' })]));

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(present(store.selectedRoleGroup(), 'the resolved group').roleGroupName).toBe(
        'Site Groups',
      );

      store.setGroupFilter({ kind: 'Group', roleGroupId: 99 });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroup())
        .withContext('a key the loaded listing does not contain resolves to absence')
        .toBeNull();
    });

    it('does not confuse the ungrouped NARROWING with the ungrouped FIELD on a role row', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0, roleGroupId: -1 })));

      store.setGroupFilter({ kind: 'GlobalRoles' });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleGroupId)
        .withContext('setting the narrowing must not rewrite a role row')
        .toBe(-1);
      expect(store.groupFilter().kind).toBe('GlobalRoles');
      // And the converse: a row whose grouping key is minus one is not the every-role
      // intent, which the narrowing continues to report as its own named value.
      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('retains an absent grouping key as absence rather than manufacturing a negative one', () => {
      // The API publishes absence for a role belonging to no group. The legacy reader manufactured minus
      // one outbound and undid it inbound, and the grouping table is seeded from zero with a foreign key
      // pointing at it, so minus one was never a stored value.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, roleGroupId: null })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleGroupId).toBeNull();
      expect(held.roleGroupId).not.toBe(-1);
      expect(held.roleGroupId).not.toBe(0);
    });

    /**
     * ⚠ THE LISTING HEALS A NARROWING THAT NAMES A GROUP THE SERVER NO LONGER HAS. The address is what issues
     * this read, so a narrowing the operator can no longer satisfy is re-staged on every subsequent emission
     * - a Back, a paging click, a sort - and each one repeats the same 404. Deleting the group currently
     * filtered on is one way to arrive here; a bookmark, a shared link and another administrator removing the
     * group in a second session all reach the same dead end, which is why the guard lives on the READ rather
     * than on the delete.
     */
    it('heals a narrowing whose group the server reports as gone, and re-reads', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: 42 });

      expectGet(ROLES_URL).flush(
        aProblem(404, 'role_group.not_found', 'Portal -1 has no role group bearing that identifier.'),
        { status: 404, statusText: 'Not Found' },
      );

      expect(store.groupFilter())
        .withContext('the unsatisfiable narrowing is discarded for the default')
        .toEqual(DEFAULT_ROLE_GROUP_FILTER);

      // The group set is re-read too, because a stale key means this client's idea of the group set is stale.
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 7 })]));

      const healed = expectGet(ROLES_URL);
      expect(healed.request.params.get('scope'))
        .withContext('the re-read carries the healed narrowing, not the discarded one')
        .toBe('Ungrouped');
      expect(healed.request.params.get('roleGroupId'))
        .withContext('the dead key is gone from the wire')
        .toBeNull();

      healed.flush(pageOf([aRoleListItem()], 1));

      expect(store.roles().items.length)
        .withContext('the operator ends on a populated listing')
        .toBe(1);
    });

    /**
     * THE PAIRED HALF: the withdrawn request must NOT be reported. It describes a read this store has already
     * replaced, so a banner naming it would state a problem the operator can neither see nor act on - and it
     * was the banner, not the empty grid, that made the original defect look like data loss.
     */
    it('reports no failure for the read it withdrew', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: 42 });

      expectGet(ROLES_URL).flush(
        aProblem(404, 'role_group.not_found', 'Portal -1 has no role group bearing that identifier.'),
        { status: 404, statusText: 'Not Found' },
      );

      expect(store.failure()).withContext('a superseded request is not an operator-facing failure').toBeNull();

      // The heal's own two reads are settled so the suite's no-open-requests check still measures this case
      // rather than tripping over the recovery it deliberately triggers.
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 7 })]));
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(store.failure()).withContext('and the recovery reports nothing either').toBeNull();
    });

    /**
     * The heal resets the PAGE as well as the narrowing. Landing on page four of a listing that now has one
     * page would answer empty and read as data loss for the second time.
     */
    it('returns to the first page when it heals', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: 42 });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 90));

      store.setRolesPage(3);
      expectGet(ROLES_URL).flush(
        aProblem(404, 'role_group.not_found', 'Gone.'),
        { status: 404, statusText: 'Not Found' },
      );

      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 7 })]));

      expect(expectGet(ROLES_URL).request.params.get('pageIndex'))
        .withContext('the healed read starts at the first page')
        .toBe('0');
    });

    /**
     * ⚠ BOTH HALVES OF THE PREDICATE ARE REQUIRED. The same code raised while NO narrowing is in force is a
     * genuine failure about something else - discarding a narrowing that is not there would change nothing
     * and re-reading would simply repeat it, so this case must be REPORTED rather than healed. Without the
     * `kind === 'Group'` half this becomes a silent retry loop.
     */
    it('reports the same code as a failure when no narrowing is in force', () => {
      store.setGroupFilter({ kind: 'AllRoles' });

      expectGet(ROLES_URL).flush(
        aProblem(404, 'role_group.not_found', 'Gone.'),
        { status: 404, statusText: 'Not Found' },
      );

      expect(store.failure()).withContext('nothing was withdrawn, so this is the operator\'s to see').not.toBeNull();
      expect(store.groupFilter().kind).withContext('an absent narrowing is not replaced').toBe('AllRoles');
      httpMock.expectNone(ROLE_GROUPS_URL);
    });

    /**
     * A DIFFERENT refusal under a narrowing is still a failure. Healing on any 404 would hide a genuine
     * server fault behind a filter reset.
     */
    it('reports an unrelated refusal raised under a narrowing', () => {
      store.setGroupFilter({ kind: 'Group', roleGroupId: 42 });

      expectGet(ROLES_URL).flush(
        aProblem(500, null, 'Server Error'),
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.failure()).withContext('an unrelated fault is reported').not.toBeNull();
      expect(store.groupFilter())
        .withContext('an unrelated fault does not discard the narrowing')
        .toEqual({ kind: 'Group', roleGroupId: 42 });
    });

    it('reports a group read as settled on success, and on failure', () => {
      expect(store.roleGroupsSettled()).withContext('nothing has been asked yet').toBeFalse();

      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));

      expect(store.roleGroupsSettled()).withContext('an empty answer is still an answer').toBeTrue();

      store.reset();
      expect(store.roleGroupsSettled()).withContext('a reset un-asks the question').toBeFalse();

      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(aProblem(500, null, 'Server Error'), {
        status: 500,
        statusText: 'Server Error',
      });

      expect(store.roleGroupsSettled())
        .withContext('a failure has also settled whether a read happened')
        .toBeTrue();
    });

    it('falls back to the every-role intent when the tenant declares no group', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));

      expect(store.hasRoleGroups()).toBeFalse();
      expect(store.groupFilter().kind).toBe('AllRoles');
    });

    it('applies the no-group fall-back BEFORE reading the roles, not after', () => {
      // The ordering is the assertion. Reading the two listings concurrently would race the
      // override and could read the roles under a narrowing the tenant cannot support.
      store.loadRoleAdministration();

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([]));

      const rolesRequest = expectGet(ROLES_URL);

      expect(rolesRequest.request.params.get('scope'))
        .withContext('the roles must be read under the overridden narrowing')
        .toBe('All');

      rolesRequest.flush(pageOf([aRoleListItem()], 1));

      expect(store.groupFilter().kind).toBe('AllRoles');
    });

    it('leaves an already-every-role narrowing exactly as it is when no group is declared', () => {
      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      const before: RoleGroupFilter = store.groupFilter();

      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));

      expect(store.groupFilter().kind).toBe('AllRoles');
      expect(store.groupFilter())
        .withContext('an idempotent fall-back must not replace an equal narrowing')
        .toBe(before);
    });

    it('JOINS a group read already in flight rather than abandoning and restarting it', () => {
      // ⚠ THE MEASURED DUPLICATE. Two role screens each need the group list on entry and each asks for it,
      // which is correct - neither may render a group selector from data it has not read.
      store.loadRoleGroups();

      const inFlight = expectGet(ROLE_GROUPS_URL);

      store.loadRoleGroups();

      expect(httpMock.match((candidate) => candidate.url === ROLE_GROUPS_URL))
        .withContext('the second call issued no request of its own')
        .toHaveSize(0);
      expect(inFlight.cancelled)
        .withContext('and it did not abandon the one already answering')
        .toBeFalse();

      inFlight.flush(envelopeOf([aRoleGroup({ roleGroupId: 4 })]));

      expect(store.roleGroups().length).withContext('both callers are served by the one read').toBe(1);
      expect(store.roleGroupsLoading()).toBeFalse();
    });

    it('reads again on the next entry, so joining cannot become remembering', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));

      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 7 })]));

      expect(store.roleGroups().length)
        .withContext('the second entry really did read, and the newer answer is held')
        .toBe(1);
    });

    it('resolves no group for either pseudo-intent, even with groups loaded', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.selectedRoleGroup())
        .withContext('a pseudo-intent names no group, so none resolves')
        .toBeNull();

      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.selectedRoleGroup()).toBeNull();
    });

    it('leaves the narrowing alone when the tenant does declare a group', () => {
      store.loadRoleAdministration();

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      const rolesRequest = expectGet(ROLES_URL);

      expect(rolesRequest.request.params.get('scope')).toBe('Ungrouped');
      rolesRequest.flush(pageOf([aRoleListItem()], 1));

      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.hasRoleGroups()).toBeTrue();
    });

    it('re-reads the whole listing from the first page when the narrowing changes', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      store.setGroupFilter({ kind: 'AllRoles' });

      const request = expectGet(ROLES_URL);

      // A different narrowing yields a different result set, and the walk that reads it
      // always starts at the first page.
      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([aRoleListItem()], 1));

      expect(store.rolesPage().pageIndex).toBe(0);
    });
  });

  // -------------------------------------------------------------------------
  // THE PAGING SHAPES — TWO PAGED LISTINGS, ONE UNPAGED
  // -------------------------------------------------------------------------

  describe('paging shapes: the roles and the assignments are paged, the groups are not', () => {
    it('reads the role listing from the first page at the largest legal request size', () => {
      store.loadRoles();

      const request = expectGet(ROLES_URL);

      // Zero-based, which is the base the API both accepts and reports, so an index sent may be compared
      // with an index read back without arithmetic. The legacy screens that did page converted a one-based
      // control index by subtracting one; nothing here is one-based, so nothing here needs that conversion.
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('pageSize')).toBe(String(ROLES_PAGE_SIZE));
      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize', 'scope']);

      request.flush(pageOf([aRoleListItem()], 1));

      expect(store.rolesMeta().pageIndex)
        .withContext('one envelope holding everything reports the unpaged coordinate')
        .toBe(0);
      expect(store.rolesMeta().totalCount)
        .withContext("the SERVER's total, which a completed walk has necessarily satisfied")
        .toBe(1);
      expect(store.roleItems().length).toBe(1);
    });

    it('reads ONE page per read and joins nothing, so a second page costs a second command', () => {
      // ⚠ THIS CASE REPLACED THE COMPLETE-LISTING WALK, AND THE REPLACEMENT IS THE POINT. The walk
      // requested page after page at a hundred records each and joined them into one unpaged envelope,
      // because the screen offered no pager and a single window would have presented the first page AS the
      // whole set.
      store.loadRoles();

      const first = expectGet(ROLES_URL);

      expect(first.request.params.get('pageIndex')).toBe('0');
      first.flush(
        pageOf(
          Array.from({ length: ROLES_PAGE_SIZE }, (_unused, index) =>
            aRoleListItem({ roleId: index, roleName: `Role ${index}` }),
          ),
          ROLES_PAGE_SIZE + 3,
        ),
      );

      // Nothing outstanding: one read, one request.
      httpMock.verify();

      expect(store.roleItems().length).toBe(ROLES_PAGE_SIZE);
      expect(store.rolesMeta().totalCount)
        .withContext("the SERVER'S total is published, which is what lets the pager offer page two")
        .toBe(ROLES_PAGE_SIZE + 3);
      expect(store.rolesLoading()).toBeFalse();

      // And the second page is reached by a command rather than by the store having pre-fetched it.
      store.setRolesPage(1);

      const second = expectGet(ROLES_URL);

      expect(second.request.params.get('pageIndex')).toBe('1');
      expect(second.request.params.get('pageSize')).toBe(String(ROLES_PAGE_SIZE));
      second.flush(pageOf([aRoleListItem({ roleId: 100, roleName: 'Role 100' })], ROLES_PAGE_SIZE + 3, 1));

      expect(store.roleItems().length).toBe(1);
      expect(store.roleItems()[0]?.roleName).toBe('Role 100');
    });

    it('steps back to the last page that exists when a deletion strands the coordinate', () => {
      // ⚠ THE STRANDING THIS PREVENTS. Remove the only role on the last page and the coordinate the
      // operator stands on stops existing: the re-read asks for it again, the server answers an empty
      // window while still reporting the true total, and the grid renders nothing.
      store.setRolesPage(2);
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 20 })], 21, 2));

      store.loadRoles();

      const stranded = expectGet(ROLES_URL);

      expect(stranded.request.params.get('pageIndex'))
        .withContext('the index is sent exactly as held: an index past the end is a real state')
        .toBe('2');

      // The server's own answer: an empty window beyond a positive total, reporting two pages.
      stranded.flush(pageOf([], 20, 2));

      const corrected = expectGet(ROLES_URL);

      expect(corrected.request.params.get('pageIndex'))
        .withContext("the step-back target is the server's own reported page count, not arithmetic")
        .toBe('1');

      corrected.flush(pageOf([aRoleListItem({ roleId: 19 })], 20, 1));

      expect(store.rolesPage().pageIndex).toBe(1);
      expect(store.roleItems().length).toBe(1);
      expect(store.rolesLoading()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('corrects at most once, so a listing shrinking underneath the screen cannot loop', () => {
      store.setRolesPage(3);
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 30 })], 31, 3));

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([], 30, 3));

      expectGet(ROLES_URL).flush(pageOf([], 20, 2));

      httpMock.verify();

      expect(store.roleItems()).toEqual([]);
      expect(store.rolesLoading()).toBeFalse();
    });

    it('leaves an empty first page alone, because no roles is a legitimate answer', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([], 0));

      // A positive total is what separates "past the end" from "this narrowing matches nothing". A
      // tenant with no roles must not provoke a corrective request.
      httpMock.verify();

      expect(store.roleItems()).toEqual([]);
      expect(store.failure()).toBeNull();
    });

    it('carries the ordering members and returns to the first page when they change', () => {
      store.setRolesSort('RoleName', 'Ascending');

      const request = expectGet(ROLES_URL);

      expect(request.request.params.get('sortBy')).toBe('RoleName');
      expect(request.request.params.get('sortDir')).toBe('Ascending');
      expect(request.request.params.get('pageIndex')).toBe('0');

      request.flush(pageOf([aRoleListItem()], 1));

      const coordinate: RolePageCoordinate = store.rolesPage();

      expect(coordinate.sortBy).toBe('RoleName');
      expect(coordinate.sortDir).toBe('Ascending');
    });

    it('omits the ordering members when the caller expresses no opinion', () => {
      store.setRolesSort(null, null);

      const request = expectGet(ROLES_URL);

      // Absence lets the server apply its own ordering rather than a literal chosen in the
      // client, and absence is expressed by OMITTING the parameter.
      expect(request.request.params.has('sortBy')).toBeFalse();
      expect(request.request.params.has('sortDir')).toBeFalse();

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('sends a free-text filter verbatim, with no wildcard appended', () => {
      store.setRolesQuery('admin');

      const request = expectGet(ROLES_URL);
      const filter: string = present(request.request.params.get('query'), 'the filter parameter');

      expect(filter).toBe('admin');
      expect(filter).not.toContain('%');

      request.flush(pageOf([aRoleListItem()], 1));
    });

    it('distinguishes an empty filter from no filter at all', () => {
      store.setRolesQuery('');

      const empty = expectGet(ROLES_URL);

      expect(empty.request.params.has('query')).toBeTrue();
      expect(empty.request.params.get('query')).toBe('');
      empty.flush(pageOf([aRoleListItem()], 1));

      store.setRolesQuery(null);

      const absent = expectGet(ROLES_URL);

      expect(absent.request.params.has('query')).toBeFalse();
      absent.flush(pageOf([aRoleListItem()], 1));
    });

    it('reads the assignment listing with its own page coordinate', () => {
      store.loadAssignments(7);

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize']);
      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([anAssignment()], 1));

      store.setAssignmentsPage(2);

      const second = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(second.request.params.get('pageIndex')).toBe('2');
      second.flush(pageOf([anAssignment()], 25, 2));

      expect(store.assignmentsPage().pageIndex).toBe(2);
      expect(store.assignmentsMeta().totalCount).toBe(25);
    });

    it('issues exactly ONE request per assignment read, following no further page', () => {
      store.loadAssignments(7);

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(request.request.params.get('pageSize')).toBe(String(DEFAULT_PAGE_SIZE));

      // A FULL page against a much larger total, which is the strongest possible invitation to walk.
      request.flush(pageOf([anAssignment()], 137));

      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentItems()).toHaveSize(1);
      // The SERVER's own coordinate and total are held as published, because that is what a pager binds
      // to and what tells an operator there is more to see.
      expect(store.assignmentsMeta().totalCount).toBe(137);
      expect(store.assignmentsMeta().pageSize).toBe(DEFAULT_PAGE_SIZE);
    });

    it('returns the assignment coordinate to the first page when the addressed role changes', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 25));

      store.setAssignmentsPage(2);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 25, 2));

      expect(store.assignmentsPage().pageIndex).toBe(2);

      // A different role, and the page an operator was standing on belongs to the role they were looking
      // at. Carrying the index across would request a window the new role may not have and present an empty
      // grid for a role that has members.
      store.loadAssignments(0);

      const fresh = expectGet('/api/v1/roles/0/users');

      expect(fresh.request.params.get('pageIndex')).toBe('0');
      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentItems())
        .withContext('and the previous role\'s rows are not published under the new role')
        .toEqual([]);

      fresh.flush(pageOf([anAssignment({ roleId: 0 })], 1));
    });

    it('steps back to the last page that exists when a removal leaves the coordinate past the end', () => {
      // ⚠ THE DEAD END THIS PREVENTS, WHICH ONLY PAGING CAN REACH. Eleven members at ten a page; the
      // operator moves to the second page, where the eleventh sits alone, and removes it.
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 11));

      store.setAssignmentsPage(1);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ userRoleId: 11 })], 11, 1));

      expect(store.assignmentsPage().pageIndex).toBe(1);

      // The re-read after a removal, answered as the server would: the window is empty, the total
      // has fallen to ten, and the reported page count no longer reaches the index asked for.
      store.reloadAssignments();

      const stranded = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(stranded.request.params.get('pageIndex')).toBe('1');
      stranded.flush(pageOf([], 10, 1));

      // ONE corrective read, at the last page the server says exists.
      const corrected = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(corrected.request.params.get('pageIndex'))
        .withContext('the step-back target comes from the reported page count, not from arithmetic on rows')
        .toBe('0');
      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentsLoading())
        .withContext('a read is still outstanding, so the grid must not report itself at rest')
        .toBeTrue();

      corrected.flush(pageOf([anAssignment()], 10));

      expect(store.assignmentItems()).toHaveSize(1);
      expect(store.assignmentsMeta().totalCount).toBe(10);
      expect(store.assignmentsLoading()).toBeFalse();
      expect(store.failure())
        .withContext('a corrected coordinate is not a failure')
        .toBeNull();
    });

    it('issues no corrective read when the listing is legitimately empty', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 25));

      store.setAssignmentsPage(2);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0, 2));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentItems()).toEqual([]);
      expect(store.assignmentsPage().pageIndex)
        .withContext('the coordinate is left exactly where the caller put it')
        .toBe(2);
      expect(store.assignmentsLoading()).toBeFalse();
    });

    it('issues no corrective read for an empty FIRST page, whatever the total says', () => {
      // The first page is never past the end. A server reporting an empty first page beside a positive
      // total is inconsistent with itself, and a correction would ask for page nought again — the page it
      // just answered — for ever.
      store.loadAssignments(7);

      const request = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(request.request.params.get('pageIndex')).toBe('0');
      request.flush(pageOf([], 25));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentsLoading()).toBeFalse();
    });

    it('believes a server that reports the window it was asked for does exist', () => {
      // An empty page can be a truthful answer for a window that DOES exist — every row in it removed by
      // another administrator between the count and the window. The reported page count still covers the
      // index, so nothing is corrected.
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 35));

      store.setAssignmentsPage(1);

      // Thirty-five members at ten a page is four pages, so page one is well inside the set.
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 35, 1));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentsPage().pageIndex).toBe(1);
      expect(store.assignmentItems()).toEqual([]);
    });

    it('corrects AT MOST ONCE per read, so a listing shrinking underneath cannot loop', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 35));

      store.setAssignmentsPage(3);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 35, 3));

      store.reloadAssignments();
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 21, 3));

      // Corrected to page two, the last of three that twenty-one members make.
      const corrected = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(corrected.request.params.get('pageIndex')).toBe('2');

      // And the listing has shrunk AGAIN in the meantime, so this answer is past the end too.
      corrected.flush(pageOf([], 10, 2));

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      expect(store.assignmentsPage().pageIndex)
        .withContext('the coordinate the corrective read used is kept; it is not chased further')
        .toBe(2);
      expect(store.assignmentsLoading())
        .withContext('and the slice is at rest rather than waiting on a request nobody issued')
        .toBeFalse();
    });

    it('reads the role-group listing with no query parameter whatsoever', () => {
      store.loadRoleGroups();

      const request = expectGet(ROLE_GROUPS_URL);

      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.params.has('pageIndex')).toBeFalse();
      expect(request.request.params.has('pageSize')).toBeFalse();
      expect(request.request.params.has('portalId')).toBeFalse();

      request.flush(envelopeOf([aRoleGroup({ roleGroupId: 0 }), aRoleGroup({ roleGroupId: 1 })]));

      expect(store.roleGroups().length).toBe(2);
    });

    it('holds the groups as a plain sequence, with no coordinate, size or total beside it', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      expect(Array.isArray(store.roleGroups()))
        .withContext('the groups are a sequence, not a page')
        .toBeTrue();

      // Enumerated rather than merely asserted one name at a time, so a paging slice added
      // to the groups in future fails here rather than passing unnoticed.
      const groupPagingMembers: readonly string[] = publishedMembers(store).filter((name) => {
        const lowered = name.toLowerCase();

        return (
          lowered.includes('group') &&
          (lowered.includes('page') || lowered.includes('total') || lowered.includes('size'))
        );
      });

      expect(groupPagingMembers)
        .withContext('the group listing is unpaged and must publish no paging member')
        .toEqual([]);
    });

    it('publishes a page coordinate for each paged listing and for neither of the others', () => {
      const published: readonly string[] = publishedMembers(store);

      expect(published.includes('rolesPage')).toBeTrue();
      expect(published.includes('assignmentsPage')).toBeTrue();
      expect(published.includes('roleGroupsPage')).toBeFalse();
      expect(published.includes('roleGroupsMeta')).toBeFalse();
      expect(published.includes('setRoleGroupsPage')).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // ASSIGNMENT REMOVAL — THE EMPTY SUCCESS THAT MAY LEAVE THE ROW IN PLACE
  // -------------------------------------------------------------------------

  // -------------------------------------------------------------------------
  // THE SINGLE-ACCOUNT MEMBERSHIP PROBE
  // -------------------------------------------------------------------------

  describe('the keyed membership probe answers about ONE pairing, in ONE request', () => {
    it('addresses the pairing itself, naming nobody in the request target', () => {
      store.probeAssignment(7, 42);

      const request = expectGet(ROLE_SEVEN_MEMBER_URL);

      expect(request.request.params.keys())
        .withContext('no query parameter at all, so nothing identifying can be in the target')
        .toEqual([]);
      expect(request.request.urlWithParams).toBe(ROLE_SEVEN_MEMBER_URL);
      expect(request.request.urlWithParams).not.toContain('host');

      // The listing is deliberately NOT read: the probe asks a different question and one request
      // answers it, so no page of other people's memberships is fetched to learn about one account.
      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === ROLE_SEVEN_MEMBERS_URL,
      );

      // The pairing being asked about is published BEFORE the answer lands, so a screen can see
      // which question is outstanding.
      expect(store.probedAssignmentKey()).toEqual({ roleId: 7, userId: 42 });
      expect(store.assignmentProbeLoading()).toBeTrue();

      const wanted = anAssignment({ userRoleId: 9, userId: 42 });

      request.flush(envelopeOf(wanted));

      expect(store.probedAssignment()).toEqual(wanted);
      expect(store.assignmentProbeLoading()).toBeFalse();
      // And the LISTING slice is untouched by a probe: the two answer different questions and neither
      // may overwrite the other.
      expect(store.assignmentItems()).toEqual([]);
    });

    it('reads a settled NO MEMBERSHIP from the refusal, without recording a failure', () => {
      // Role zero and account zero, because both tables carry a legitimate zero key and a truthiness
      // test on either would make this case pass for the wrong reason.
      store.probeAssignment(0, 0);

      expectGet('/api/v1/roles/0/users/0').flush(
        aProblem(404, 'role_assignment.not_found', 'The account holds no such membership.'),
        { status: 404, statusText: 'Not Found' },
      );

      // A settled "holds nothing", told apart from "nothing has been asked" by the key beside it.
      expect(store.probedAssignment()).toBeNull();
      expect(store.probedAssignmentKey()).toEqual({ roleId: 0, userId: 0 });
      expect(store.failure())
        .withContext('an ordinary negative answer is not a failure')
        .toBeNull();
      expect(store.assignmentProbeLoading()).toBeFalse();
    });

    it('records a failed probe under its own operation, never as a failed listing', () => {
      store.probeAssignment(7, 42);

      expectGet(ROLE_SEVEN_MEMBER_URL).flush(
        aProblem(500, 'server.unexpected_failure', 'Something went wrong.'),
        { status: 500, statusText: 'Internal Server Error' },
      );

      // A screen reports a listing failure by matching the OPERATION, so a probe reported as
      // `loadAssignments` would be announced as the grid having failed to load.
      expect(present(store.failure(), 'the recorded failure').operation).toBe('probeAssignment');
      expect(store.probedAssignment())
        .withContext('a failed probe holds nothing, which is the state an unknown account shows')
        .toBeNull();
      expect(store.assignmentProbeLoading()).toBeFalse();
    });

    it('still records a refusal that is not the negative answer', () => {
      store.probeAssignment(7, 42);

      // 403 is a caller who may not ask, which is a genuine failure and must be reported. Only the
      // 404 is an answer, and this is what keeps that exemption from widening into "swallow refusals".
      expectGet(ROLE_SEVEN_MEMBER_URL).flush(
        aProblem(403, 'auth.not_permitted', 'The caller does not administer this tenant.'),
        { status: 403, statusText: 'Forbidden' },
      );

      expect(present(store.failure(), 'the recorded failure').operation).toBe('probeAssignment');
      expect(store.probedAssignment()).toBeNull();
    });

    it('abandons the probe it replaces, so an older answer cannot land on a newer question', () => {
      store.probeAssignment(7, 42);

      const first = expectGet(ROLE_SEVEN_MEMBER_URL);

      store.probeAssignment(7, 43);

      const second = expectGet('/api/v1/roles/7/users/43');

      expect(first.cancelled).withContext('superseded').toBeTrue();
      expect(second.cancelled).withContext('current').toBeFalse();
      expect(store.probedAssignmentKey()).toEqual({ roleId: 7, userId: 43 });

      second.flush(envelopeOf(anAssignment({ userId: 43 })));

      expect(present(store.probedAssignment(), 'the newer answer').userId).toBe(43);
    });

    it('forgets the answer and the question together, and abandons a probe in flight', () => {
      store.probeAssignment(7, 42);

      const request = expectGet(ROLE_SEVEN_MEMBER_URL);

      store.clearProbedAssignment();

      expect(request.cancelled).withContext('abandoned').toBeTrue();
      // Both are cleared, so the state is "nothing has been asked" rather than "the pairing holds
      // nothing" - two different facts that read differently to an operator.
      expect(store.probedAssignment()).toBeNull();
      expect(store.probedAssignmentKey()).toBeNull();
      expect(store.assignmentProbeLoading()).toBeFalse();
    });
  });

  describe('assignment removal expires the row rather than deleting it', () => {
    /**
     * Puts one assignment in scope so a removal has something to act on.
     *
     * @param row The assignment to hold.
     */
    function givenOneAssignment(row: UserRole): void {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([row], 1));
    }

    it('re-reads the assignment listing after an empty success, because the row may survive', () => {
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);

      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      const reread = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(reread.request.method)
        .withContext('an empty success cannot say whether the row was deleted or expired')
        .toBe('GET');

      reread.flush(pageOf([], 0));

      expect(store.assignmentItems()).toEqual([]);
    });

    it('does not drop the row from held state before the re-read resolves', () => {
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      // The re-read is open and unresolved at this instant. An optimistic removal would be wrong roughly
      // half the time and the mistake would be invisible until the page was next read, so nothing is
      // removed on speculation.
      expect(store.assignmentItems().length)
        .withContext('held state must not be edited on the strength of an empty success')
        .toBe(1);
      expect(present(store.assignmentItems()[0], 'the held assignment').userId).toBe(42);

      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));
    });

    it('keeps an expired-but-present row, with its back-dated bound held verbatim', () => {
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7, expiryDate: FUTURE_INSTANT }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ userId: 42, roleId: 7, expiryDate: PAST_INSTANT })], 1));

      const held = present(store.assignmentItems()[0], 'the expired assignment');

      expect(store.assignmentItems().length)
        .withContext('an expired assignment is retained, not deleted')
        .toBe(1);
      expect(held.userId).toBe(42);
      expect(held.expiryDate)
        .withContext('the bound is an absolute instant, held exactly as the server sent it')
        .toBe(PAST_INSTANT);
      expect(store.failure())
        .withContext('a surviving row is not a failure')
        .toBeNull();
    });

    it('reflects the genuine deletion when the re-read comes back without the row', () => {
      // The other branch: a free assignment, or one whose trial was never used, is genuinely
      // deleted. Same status, different outcome, and the re-read is what tells them apart.
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      expect(store.assignmentItems()).toEqual([]);
      expect(store.assignmentsMeta().totalCount).toBe(0);
    });

    it('holds a recorded fee of zero as zero, because zero means free rather than absent', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, serviceFee: 0 })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.serviceFee).toBe(0);
      expect(held.serviceFee).not.toBeNull();
      expect(store.selectedRoleIsPaid())
        .withContext('a fee of zero is a free role, which is a real classification')
        .toBeFalse();
    });

    it('distinguishes a recorded fee of zero from no recorded fee at all', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, serviceFee: null })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.serviceFee).toBeNull();
      expect(held.serviceFee).not.toBe(0);
      expect(store.selectedRoleIsPaid()).toBeFalse();
    });

    it('classifies a positive fee as paid', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, serviceFee: 12.5 })));

      expect(store.selectedRoleIsPaid()).toBeTrue();
      expect(present(store.selectedRole(), 'the selected role').serviceFee).toBe(12.5);
    });

    it('reports no paid classification while no role is selected', () => {
      expect(store.selectedRoleIsPaid())
        .withContext('absence of a selection is distinct from a free role')
        .toBeNull();
    });

    it('surfaces a protected-assignment conflict verbatim and re-reads nothing', () => {
      givenOneAssignment(anAssignment({ userId: 42, roleId: 7 }));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.protectedAssignment,
            'You Can Not Remove The Portal Administrator Or The Registered Users Role',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('removeAssignment');
      expect(failure.conflict).toBe(CONFLICT_CODE.protectedAssignment);
      expect(store.assignmentItems().length)
        .withContext('a refused removal leaves held state exactly as it was')
        .toBe(1);
      expect(store.saving()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // ASSIGNMENT CREATION — CREATED OR NO CONTENT, BOTH SUCCESSES
  // -------------------------------------------------------------------------

  describe('assignment creation answers created or no content, and both are successes', () => {
    const request: RoleAssignmentRequest = Object.freeze({
      userId: 42,
      effectiveDate: null,
      expiryDate: null,
      notifyUser: false,
    });

    it('handles a created answer and re-reads the listing', () => {
      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      expect(write.request.body).toEqual(request);
      write.flush(null, { status: 201, statusText: 'Created' });

      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ userId: 42, roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
      expect(store.failure()).toBeNull();
    });

    it('handles a no-content answer identically, and does not read a response body', () => {
      store.assignUser(7, request);

      expectPost(ROLE_SEVEN_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ userId: 42, roleId: 7 })], 1));

      expect(store.failure())
        .withContext('a no-content answer is a success, not a failure')
        .toBeNull();
      expect(store.assignmentItems().length).toBe(1);
      expect(store.saving()).toBeFalse();
    });

    it('re-reads the row rather than synthesising it from the request', () => {
      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(
          pageOf(
            [
              anAssignment({
                userId: 42,
                roleId: 7,
                effectiveDate: MIN_INSTANT,
                expiryDate: PERPETUAL_INSTANT,
              }),
            ],
            1,
          ),
        );

      const held = present(store.assignmentItems()[0], 'the held assignment');

      expect(request.expiryDate).toBeNull();
      expect(held.expiryDate)
        .withContext('the bound held is the server\u2019s, never the one asked for')
        .toBe(PERPETUAL_INSTANT);
      expect(held.effectiveDate).toBe(MIN_INSTANT);
    });

    it('does not confuse the absent-row marker with the ungrouped narrowing', () => {
      const before: RoleGroupFilter = store.groupFilter();

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 1));

      expect(store.groupFilter()).toEqual(before);
      expect(store.selectedRoleGroupId()).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // THE SIX PERSISTED FREQUENCY CODES
  // -------------------------------------------------------------------------

  describe('the six persisted frequency codes survive verbatim', () => {
    it('holds each of the six codes on the billing member exactly as sent', () => {
      // Table-driven over the model's own union, so a folded case, a word-spelled unit or an integer
      // substitution could not compile, let alone reach this assertion.
      for (const code of FREQUENCY_CODES) {
        store.selectRole(7);
        expectGet(ROLE_SEVEN_URL)
          .flush(envelopeOf(aRole({ roleId: 7, billingFrequency: code })));

        expect(present(store.selectedRole(), 'the selected role').billingFrequency)
          .withContext(`the billing code ${code} must survive untouched`)
          .toBe(code);

        store.clearSelectedRole();
      }
    });

    it('holds each of the six codes on the trial member, which shares the vocabulary', () => {
      for (const code of FREQUENCY_CODES) {
        store.selectRole(7);
        expectGet(ROLE_SEVEN_URL)
          .flush(envelopeOf(aRole({ roleId: 7, trialFrequency: code })));

        expect(present(store.selectedRole(), 'the selected role').trialFrequency)
          .withContext(`the trial code ${code} must survive untouched`)
          .toBe(code);

        store.clearSelectedRole();
      }
    });

    it('classifies each code by whether it bounds the membership, without producing a date', () => {
      // The classification is a fact about the terms, not an instant and not display text.
      const expected: readonly (readonly [BillingFrequency, BillingTermsBound])[] = [
        ['N', 'Unbounded'],
        ['O', 'Perpetual'],
        ['D', 'Bounded'],
        ['W', 'Bounded'],
        ['M', 'Bounded'],
        ['Y', 'Bounded'],
      ];

      for (const [code, bound] of expected) {
        expect(billingTermsBound(code, 12))
          .withContext(`the code ${code} classifies as ${bound}`)
          .toBe(bound);
      }
    });

    it('treats the absent-integer period as no expiry at all, ahead of the code', () => {
      expect(billingTermsBound('M', -1)).toBe('Unbounded');
      expect(billingTermsBound('Y', -1)).toBe('Unbounded');
      expect(billingTermsBound('D', -1)).toBe('Unbounded');
    });

    it('treats no recorded code as unbounded, which is a different fact from the no-expiry code', () => {
      expect(billingTermsBound(null, 12)).toBe('Unbounded');
      expect(billingTermsBound(null, null)).toBe('Unbounded');
      expect(billingTermsBound('N', 12)).toBe('Unbounded');
    });

    it('reports a stored code outside the supported vocabulary as unsupported', () => {
      expect(billingTermsBound('4', 12)).toBe('Unsupported');
      expect(billingTermsBound('0', 1)).toBe('Unsupported');

      expect(billingTermsBound('m', 12)).toBe('Unsupported');
    });

    it('still lets the absent-integer period win over an unsupported code', () => {
      // The period short-circuit is tested FIRST in the legacy branch and still is here, so the
      // widened vocabulary cannot reorder the two.
      expect(billingTermsBound('4', -1)).toBe('Unbounded');
    });

    it('classifies the terms of a role whose stored code is unsupported, rather than failing', () => {
      // The end-to-end form of the same claim: a role carrying a legacy code is read, held and
      // classified, and the code itself survives on the held role untouched.
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(
        envelopeOf(aRole({ roleId: 7, billingFrequency: '4', billingPeriod: 1 })),
      );

      expect(present(store.selectedRole(), 'the selected role').billingFrequency).toBe('4');
      expect(store.selectedRoleBillingTerms()).toBe('Unsupported');
      expect(store.failure()).withContext('a legacy code is data, not a failure').toBeNull();
    });

    it('retains a period of minus one on the role rather than coalescing it', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL)
        .flush(
          envelopeOf(
            aRole({ roleId: 7, billingFrequency: 'M', billingPeriod: -1, trialPeriod: -1 }),
          ),
        );

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.billingPeriod).toBe(-1);
      expect(held.trialPeriod).toBe(-1);
      expect(store.selectedRoleBillingTerms())
        .withContext('an absent-integer period means no expiry, whatever the code says')
        .toBe('Unbounded');
    });

    it('classifies the billing and the trial terms separately, since the server chooses between them', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL)
        .flush(
          envelopeOf(
            aRole({
              roleId: 7,
              billingFrequency: 'M',
              billingPeriod: 1,
              trialFrequency: 'O',
              trialPeriod: 1,
            }),
          ),
        );

      expect(store.selectedRoleBillingTerms()).toBe('Bounded');
      expect(store.selectedRoleTrialTerms()).toBe('Perpetual');
    });

    it('reports no term classification while no role is selected', () => {
      expect(store.selectedRoleBillingTerms()).toBeNull();
      expect(store.selectedRoleTrialTerms()).toBeNull();
    });

    it('holds the perpetual instant as a real value rather than an error or an absence', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(pageOf([anAssignment({ expiryDate: PERPETUAL_INSTANT })], 1));

      const held = present(store.assignmentItems()[0], 'the held assignment');

      expect(held.expiryDate).toBe(PERPETUAL_INSTANT);
      expect(held.expiryDate).not.toBeNull();
      expect(store.failure()).toBeNull();
    });

    it('holds the minimum instant as a real value meaning no expiry, not as absence', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(
          pageOf([anAssignment({ effectiveDate: MIN_INSTANT, expiryDate: MIN_INSTANT })], 1),
        );

      const held = present(store.assignmentItems()[0], 'the held assignment');

      expect(held.expiryDate).toBe(MIN_INSTANT);
      expect(held.effectiveDate).toBe(MIN_INSTANT);
      expect(held.expiryDate).not.toBeNull();
    });

    it('distinguishes a recorded minimum instant from no recorded instant at all', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL)
        .flush(
          pageOf(
            [
              anAssignment({ userRoleId: 1, effectiveDate: MIN_INSTANT, expiryDate: null }),
              anAssignment({ userRoleId: 2, effectiveDate: null, expiryDate: MIN_INSTANT }),
            ],
            2,
          ),
        );

      const first = present(store.assignmentItems()[0], 'the first assignment');
      const second = present(store.assignmentItems()[1], 'the second assignment');

      expect(first.effectiveDate).toBe(MIN_INSTANT);
      expect(first.expiryDate).toBeNull();
      expect(second.effectiveDate).toBeNull();
      expect(second.expiryDate).toBe(MIN_INSTANT);
    });
  });

  // -------------------------------------------------------------------------
  // THE GROUP-DELETE AFFORDANCE, AND THE AUTHORITATIVE CONFLICT
  // -------------------------------------------------------------------------

  describe('the group-delete affordance, and the conflict that overrules it', () => {
    /**
     * Selects a real group and settles the role listing under it.
     *
     * @param roleGroupId The group to narrow to.
     * @param roles The roles the listing answers with.
     */
    function givenGroupSelected(roleGroupId: number, roles: readonly RoleListItem[]): void {
      store.setGroupFilter({ kind: 'Group', roleGroupId });
      expectGet(ROLES_URL).flush(pageOf(roles, roles.length));
    }

    it('offers the deletion when a real group is selected and its listing is empty', () => {
      givenGroupSelected(0, []);

      expect(store.canDeleteSelectedGroup()).toBeTrue();
    });

    it('withholds the deletion while the selected group still classifies a role', () => {
      givenGroupSelected(0, [aRoleListItem({ roleId: 0 })]);

      expect(store.canDeleteSelectedGroup()).toBeFalse();
    });

    it('withholds the deletion for either pseudo-intent, which names no group to act on', () => {
      // Legacy: `:L79-L81` hides both the edit-group and the delete control whenever the
      // narrowing is negative, because neither pseudo-intent is a group you can act on.
      store.setGroupFilter({ kind: 'AllRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.canDeleteSelectedGroup()).toBeFalse();

      store.setGroupFilter({ kind: 'GlobalRoles' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.canDeleteSelectedGroup()).toBeFalse();
    });

    it('issues the deletion even while the affordance withholds it, because the server decides', () => {
      givenGroupSelected(0, [aRoleListItem({ roleId: 0 })]);

      expect(store.canDeleteSelectedGroup()).toBeFalse();

      store.deleteRoleGroup(0);

      const removal = expectDelete(ROLE_GROUP_ZERO_URL);

      expect(removal.request.url)
        .withContext('the request must not be suppressed by a client-side affordance')
        .toBe(ROLE_GROUP_ZERO_URL);

      removal.flush(null, { status: 204, statusText: 'No Content' });

      // The success path resets the narrowing and re-reads both listings.
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([]));
      expectGet(ROLES_URL).flush(pageOf([], 0));
    });

    it('resets the narrowing to the ungrouped intent after a successful deletion', () => {
      givenGroupSelected(0, []);

      store.deleteRoleGroup(0);
      expectDelete(ROLE_GROUP_ZERO_URL)
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.rolesPage().pageIndex).toBe(0);

      // The re-read then follows, groups first, and the roles are read under the reset
      // narrowing rather than under the group that no longer exists.
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 1, roleGroupName: 'Other Groups' })]));

      const rolesRequest = expectGet(ROLES_URL);

      expect(rolesRequest.request.params.get('scope')).toBe('Ungrouped');
      rolesRequest.flush(pageOf([], 0));

      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('surfaces a conflict on the deletion verbatim, and refreshes the stale affordance', () => {
      givenGroupSelected(0, []);

      expect(store.canDeleteSelectedGroup()).toBeTrue();

      store.deleteRoleGroup(0);
      expectDelete(ROLE_GROUP_ZERO_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.duplicateRoleGroupName,
            'The group still classifies at least one role.',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('deleteRoleGroup');
      expect(failure.conflict)
        .withContext('the server\u2019s code is surfaced exactly as it spells it')
        .toBe(CONFLICT_CODE.duplicateRoleGroupName);
      expect(store.canDeleteSelectedGroup())
        .withContext('the refreshed listing corrects the affordance')
        .toBeFalse();
    });

    it('leaves the narrowing untouched when the deletion is refused', () => {
      givenGroupSelected(0, []);

      store.deleteRoleGroup(0);
      expectDelete(ROLE_GROUP_ZERO_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleGroupName, 'Still in use.'), {
          status: 409,
          statusText: 'Conflict',
        });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      // A refused deletion did not delete, so the narrowing must still name the group.
      expect(store.selectedRoleGroupId()).toBe(0);
      expect(store.groupFilter().kind).toBe('Group');
    });

    it('creates a group and re-reads the unpaged listing', () => {
      const request: CreateRoleGroupRequest = { roleGroupName: 'Site Groups', description: '' };

      store.createRoleGroup(request);

      const write = expectPost(ROLE_GROUPS_URL);

      expect(write.request.body).toEqual(request);
      write.flush(envelopeOf(aRoleGroup({ roleGroupId: 0 })), {
        status: 201,
        statusText: 'Created',
      });

      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      expect(store.roleGroups().length).toBe(1);
      expect(store.failure()).toBeNull();
    });

    it('surfaces a duplicate-group conflict verbatim without re-reading', () => {
      const request: CreateRoleGroupRequest = { roleGroupName: 'Site Groups', description: null };

      store.createRoleGroup(request);
      expectPost(ROLE_GROUPS_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.duplicateRoleGroupName,
            'A role group with the same name already exists. The new group was not added.',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('createRoleGroup');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleGroupName);
      expect(store.roleGroups()).toEqual([]);
    });

    it('patches a renamed group in place, matching the key zero by identity', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(
          envelopeOf([
            aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' }),
            aRoleGroup({ roleGroupId: 1, roleGroupName: 'Other Groups' }),
          ]),
        );

      const request: UpdateRoleGroupRequest = {
        roleGroupName: 'Renamed Groups',
        description: 'Renamed',
      };

      store.updateRoleGroup(0, request);

      const write = expectPut(ROLE_GROUP_ZERO_URL);

      expect(write.request.body).toEqual(request);
      write.flush(
        envelopeOf(
          aRoleGroup({ roleGroupId: 0, roleGroupName: 'Renamed Groups', description: 'Renamed' }),
        ),
      );

      // Matched on identity with a strict comparison, never by truth: the grouping table is seeded from
      // zero, so the row being patched here is exactly the one a truthy test would omit. No re-read
      // follows, because a group's identity does not determine its own membership of the group listing.
      expect(store.roleGroups().map((group) => group.roleGroupName)).toEqual([
        'Renamed Groups',
        'Other Groups',
      ]);
    });
  });

  // -------------------------------------------------------------------------
  // SENTINEL FIDELITY — EVERY AWKWARD VALUE IS DATA
  // -------------------------------------------------------------------------

  describe('sentinel fidelity: zero, minus one, empty and false all survive unchanged', () => {
    it('holds a role carrying every awkward value exactly as the server sent it', () => {
      const wire: Role = aRole({
        roleId: 0,
        roleGroupId: null,
        description: '',
        serviceFee: 0,
        trialFee: 0,
        billingPeriod: -1,
        trialPeriod: -1,
        billingFrequency: 'M',
        trialFrequency: 'N',
        isPublic: false,
        autoAssignment: false,
        rsvpCode: '',
        iconFile: null,
      });

      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(wire));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.roleId).toBe(0);
      expect(held.roleGroupId).toBeNull();
      expect(held.description).toBe('');
      expect(held.serviceFee).toBe(0);
      expect(held.trialFee).toBe(0);
      expect(held.billingPeriod).toBe(-1);
      expect(held.trialPeriod).toBe(-1);
      expect(held.billingFrequency).toBe('M');
      expect(held.trialFrequency).toBe('N');
      expect(held.rsvpCode).toBe('');
      expect(held.iconFile).toBeNull();
      expect(held).toEqual(wire);
    });

    it('holds a false boolean as data, because false was indistinguishable from unset before', () => {
      // The legacy absence test reported true for false, so a false flag and an unset one were one value.
      // Every wire boolean here is non-nullable, so false is DATA — and it matters acutely because the
      // shipped tenant creation passes two false flags positionally.
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, isPublic: false, autoAssignment: false })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.isPublic).toBe(false);
      expect(held.autoAssignment).toBe(false);
      expect(held.isPublic).not.toBeUndefined();
      expect(held.autoAssignment).not.toBeUndefined();
    });

    it('holds a true boolean as data too, so the flag is genuinely carried', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, isPublic: true, autoAssignment: true })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.isPublic).toBe(true);
      expect(held.autoAssignment).toBe(true);
    });

    it('holds an empty string as an empty string, distinct from absence', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, description: '', rsvpCode: '', iconFile: null })));

      const held = present(store.selectedRole(), 'the selected role');

      expect(held.description).toBe('');
      expect(held.description).not.toBeNull();
      expect(held.iconFile).toBeNull();
      expect(held.iconFile).not.toBe('');
    });

    it('holds a listing row carrying every awkward value unchanged', () => {
      const row: RoleListItem = aRoleListItem({
        roleId: 0,
        description: '',
        serviceFee: 0,
        trialFee: 0,
        billingPeriod: -1,
        trialPeriod: -1,
        isPublic: false,
        autoAssignment: false,
      });

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([row], 1));

      expect(present(store.roleItems()[0], 'the listing row')).toEqual(row);
    });

    it('holds a zero total and an empty page as a legitimate answer, not a failure', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.roleItems()).toEqual([]);
      expect(store.rolesMeta().totalCount).toBe(0);
      expect(store.rolesMeta().totalPages).toBe(0);
      expect(store.failure())
        .withContext('an empty page is an answer, not an error')
        .toBeNull();
    });

    it('publishes no general negative-identifier normaliser', () => {
      // Such a helper is precisely what would collapse the four negative vocabularies into
      // one, which is why its ABSENCE is asserted rather than its behaviour tested.
      const suspicious: readonly string[] = publishedMembers(store).filter((name) =>
        /normalis|normaliz|sanitis|sanitiz|coerce|clamp|toIdentifier|fromSentinel/i.test(name),
      );

      expect(suspicious)
        .withContext('nothing in the store may normalise a negative identifier')
        .toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // THE FOUR NEGATIVE VOCABULARIES, KEPT APART PAIRWISE
  // -------------------------------------------------------------------------

  describe('the four negative vocabularies are never conflated', () => {
    it('keeps the pseudo-role STRINGS out of the numeric narrowing space', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(
          envelopeOf([
            aRoleGroup({ roleGroupId: 0, roleGroupName: '-2' }),
            aRoleGroup({ roleGroupId: 1, roleGroupName: '-1' }),
          ]),
        );

      const names: readonly string[] = store.roleGroups().map((group) => group.roleGroupName);

      for (const name of names) {
        expect(typeof name)
          .withContext('a pseudo-role identifier is a string and stays one')
          .toBe('string');
        expect(LEGACY_PSEUDO_ROLE_IDS.includes(name)).toBeTrue();
      }

      // The superuser constant and the every-role intent are spelled with the same digits and
      // are entirely different things. The narrowing did not move.
      expect(store.groupFilter().kind).toBe('GlobalRoles');
      expect(store.selectedRoleGroupId()).toBeNull();
    });

    it('keeps a pseudo-role STRING distinct from absence', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: '-1' })]));

      const group: RoleGroup = present(store.roleGroups()[0], 'the group');

      expect(group.roleGroupName).toBe('-1');
      expect(group.roleGroupName).not.toBeNull();
      expect(group.roleGroupId).toBe(0);
    });

    it('keeps the grouping FIELD distinct from the absent-integer marker', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0, roleGroupId: null })));

      expect(present(store.selectedRole(), 'the selected role').roleGroupId).toBeNull();

      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7, roleGroupId: -1 })));

      const second = present(store.selectedRole(), 'the selected role');

      expect(second.roleGroupId).toBe(-1);
      expect(second.roleGroupId).not.toBeNull();
    });

    it('keeps the narrowing distinct from the grouping field when both name the same digits', () => {
      // Vocabulary (a) against vocabulary (b), stated once more against a real group key so
      // that the pairing is covered in the positive direction as well as the negative.
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0, roleGroupId: 0 })));

      // The narrowing names group zero; the role also belongs to group zero. The two agree
      // here by coincidence of data, and are still read from two separate members.
      expect(store.selectedRoleGroupId()).toBe(0);
      expect(present(store.selectedRole(), 'the selected role').roleGroupId).toBe(0);
      expect(store.roleItems().length).toBe(1);
    });
  });

  // -------------------------------------------------------------------------
  // FAILURES — HELD STRUCTURALLY, NEVER COMPOSED HERE
  // -------------------------------------------------------------------------

  describe('failures are held structurally as the server described them', () => {
    it('holds a forbidden response at warning severity, following the security tree\u2019s precedent', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(403, 'authorization.forbidden', 'You do not administer this tenant.'), {
          status: 403,
          statusText: 'Forbidden',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.summary.severity).toBe('warning');
      expect(failure.summary.status).toBe(403);
      expect(failure.operation).toBe('loadRoles');
    });

    it('holds a conflict at error severity, so the two are not levelled', () => {
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(
          aProblem(
            409,
            CONFLICT_CODE.duplicateRoleName,
            'A role with the same name already exists. The role was not added.',
          ),
          { status: 409, statusText: 'Conflict' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.summary.severity).toBe('error');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleName);
      expect(failure.operation).toBe('createRole');
    });

    it('carries the correlation value through into the held failure', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');

      expect(problem.traceId).toBe(TRACE_ID);
      expect(failure.summary.supportReference).toBe(TRACE_ID);
    });

    it('prefers the correlation member over the trace member when the server sends both', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.', { correlationId: CORRELATION_ID }), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');

      expect(problem.correlationId).toBe(CORRELATION_ID);
      expect(problem.traceId).toBe(TRACE_ID);
      expect(failure.summary.supportReference).toBe(CORRELATION_ID);
    });

    it('holds the per-member dictionary under the server\u2019s own keys, read with bracket access', () => {
      // The dictionary is an index signature and the workspace's compiler settings refuse dotted access to
      // one by design, so every read of it is a bracket read. The keys are the server's model-state keys
      // and are NOT lower-camel.
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(
          aValidationProblem(
            {
              RoleName: ['The role name is required.'],
              BillingFrequency: ['The billing code is not recognised.'],
            },
            'One or more validation errors occurred.',
          ),
          { status: 400, statusText: 'Bad Request' },
        );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');
      const fieldErrors = present(problem.errors, 'the per-member dictionary');

      expect(fieldErrors['RoleName']).toEqual(['The role name is required.']);
      expect(fieldErrors['BillingFrequency']).toEqual(['The billing code is not recognised.']);
      expect(failure.summary.hasFieldMessages).toBeTrue();
      expect(failure.conflict)
        .withContext('a model-state refusal is not a recognised conflict')
        .toBeNull();
    });

    it('holds an unsafe message as an inert string, marking nothing as trusted markup', () => {
      // The legacy resource files carry live markup in dozens of values, script elements included, and the
      // legacy precedent for showing an untrusted message was to encode it first. The document is therefore
      // held as received and nothing is wrapped, blessed or rendered here; a consumer binds it as text.
      const hostile = '<script>alert(1)</script>';

      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, hostile), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the problem document');

      expect(typeof problem.detail)
        .withContext('the message is a plain string and not a trusted-markup wrapper')
        .toBe('string');
      expect(problem.detail).toBe(hostile);
      expect(typeof failure.summary.message).toBe('string');
    });

    it('holds a message prefixed with the unspaced legacy break tag verbatim', () => {
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, '<br>The role was not added.'), {
          status: 409,
          statusText: 'Conflict',
        });

      const problem: ProblemDetails = present(
        present(store.failure(), 'the held failure').problem,
        'the problem document',
      );

      expect(problem.detail).toBe('<br>The role was not added.');
    });

    it('holds a message prefixed with the self-closing legacy break tag verbatim', () => {
      store.createRole(aCreateRequest());
      expectPost(ROLES_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, '<br/>The role was not added.'), {
          status: 409,
          statusText: 'Conflict',
        });

      const problem: ProblemDetails = present(
        present(store.failure(), 'the held failure').problem,
        'the problem document',
      );

      expect(problem.detail).toBe('<br/>The role was not added.');
    });

    it('synthesises a document when the server explained nothing', () => {
      // ⚠ THIS ASSERTION WAS INVERTED, AND THE INVERSION WAS A DEFECT RATHER THAN A CHOICE. It used to
      // require `problem` to be NULL so that "a consumer must be able to tell that the server said
      // nothing" - but every consumer binds this member to the shared error banner, and the banner renders
      // nothing at all from null. A caller that needs to know whether the server spoke has `synthesised`
      // wording to read in the document itself; a caller that needs to SHOW the failure needs a document.
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(null, { status: 503, statusText: 'Service Unavailable' });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the synthesised document');

      expect(problem.status).toBe(503);
      expect(problem.title).withContext('a title the banner can render').toBe('Request failed');
      expect((problem.detail ?? '').length).withContext('and a sentence').toBeGreaterThan(0);
      expect(failure.summary.status)
        .withContext('severity still resolves from the transport status alone')
        .toBe(503);
      expect(failure.summary.severity).toBe('error');
    });

    it('attributes each failure to the command that caused it', () => {
      const operations: readonly RoleStoreOperation[] = ['loadRoles', 'loadRole', 'deleteRole'];

      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), { status: 500, statusText: 'Server Error' });

      expect(present(store.failure(), 'the held failure').operation).toBe(operations[0]);

      store.selectRole(0);
      expectGet(ROLE_ZERO_URL)
        .flush(aProblem(404, null, 'No such role.'), { status: 404, statusText: 'Not Found' });

      expect(present(store.failure(), 'the held failure').operation).toBe(operations[1]);

      store.deleteRole(0, { thenReadListing: true });
      expectDelete(ROLE_ZERO_URL)
        .flush(aProblem(409, null, 'In use.'), { status: 409, statusText: 'Conflict' });

      expect(present(store.failure(), 'the held failure').operation).toBe(operations[2]);
    });

    it('clears the held failure when a fresh command starts', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), { status: 500, statusText: 'Server Error' });

      expect(store.failure()).not.toBeNull();

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(store.failure())
        .withContext('a command a user starts deserves a clean slate')
        .toBeNull();
    });

    it('synthesises an unreachable-server document when the response never arrived at all', () => {
      store.loadRoles();
      expectGet(ROLES_URL).error(new ProgressEvent('error'));

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the synthesised document');

      // A progress event is not a problem document, which is exactly why one has to be composed: the
      // banner has to say something, and "the server could not be reached" is the truthful sentence for a
      // request that never arrived.
      expect(problem.status).toBe(0);
      expect(problem.title).toBe('Network error');
      expect(problem.detail).toContain('could not be reached');
      expect(failure.summary.status).toBe(0);
      expect(failure.operation).toBe('loadRoles');
    });

    it('recovers a document the server sent as text rather than as an object', () => {
      // A document can arrive as a string, so it is parsed rather than discarded — the failure
      // code and the correlation value are carried inside it and would otherwise be lost.
      const document: ProblemDetails = aProblem(
        409,
        CONFLICT_CODE.duplicateRoleName,
        'A role with the same name already exists. The role was not added.',
      );

      store.createRole(aCreateRequest());
      expectPost(ROLES_URL).flush(JSON.stringify(document), {
        status: 409,
        statusText: 'Conflict',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the parsed document');

      expect(problem.status).toBe(409);
      expect(problem.traceId).toBe(TRACE_ID);
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleName);
    });

    it('synthesises a document when a gateway answered with a page instead of one', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush('<html><body>504 Gateway Time-out</body></html>', {
        status: 504,
        statusText: 'Gateway Timeout',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');
      const problem: ProblemDetails = present(failure.problem, 'the synthesised document');

      // The gateway's HTML is not a problem document and none of it is quoted; the status is all that
      // survives, and it is enough to word a truthful failure from.
      expect(problem.status).toBe(504);
      expect(problem.detail).not.toContain('Gateway Time-out');
      expect(failure.summary.status).toBe(504);
      expect(failure.summary.severity).toBe('error');
    });

    it('quotes nothing from a body that parsed but described something else', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(JSON.stringify({ message: 'not a problem document' }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      const problem: ProblemDetails = present(
        present(store.failure(), 'the held failure').problem,
        'the synthesised document',
      );

      // The unrecognised body is discarded rather than rendered - it may hold anything at all - and the
      // document is composed from the status instead.
      expect(problem.detail).not.toContain('not a problem document');
      expect(problem.status).toBe(500);
    });

    it('attributes a failure in the opening sequence to the half that failed', () => {
      // The listing screen opens with two reads in sequence. When the FIRST fails the second is
      // never issued, and the failure names the group read rather than the role read.
      store.loadRoleAdministration();
      expectGet(ROLE_GROUPS_URL).flush(aProblem(403, 'authorization.forbidden', 'Refused.'), {
        status: 403,
        statusText: 'Forbidden',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('loadRoleGroups');
      expect(failure.summary.severity).toBe('warning');
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.rolesLoading()).toBeFalse();
    });

    it('attributes a failure in the second half of the opening sequence to the role read', () => {
      store.loadRoleAdministration();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));
      expectGet(ROLES_URL).flush(aProblem(500, null, 'Unexpected.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('loadRoles');
      expect(store.roleGroups().length)
        .withContext('the half that succeeded keeps what it read')
        .toBe(1);
      expect(store.busy()).toBeFalse();
    });

    it('attributes a failed group read and settles its own indicator', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(aProblem(500, null, 'Unexpected.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(present(store.failure(), 'the held failure').operation).toBe('loadRoleGroups');
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.roleGroups()).toEqual([]);
    });

    it('attributes a failed assignment read and settles its own indicator', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(aProblem(404, null, 'No such role.'), {
        status: 404,
        statusText: 'Not Found',
      });

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('loadAssignments');
      expect(failure.summary.severity)
        .withContext('a not-found response is a warning, as a refusal is')
        .toBe('warning');
      expect(store.assignmentsLoading()).toBeFalse();
      // The scope was recorded before the read was attempted, so a retry knows what to retry.
      expect(store.assignmentsRoleId()).toBe(7);
    });

    it('attributes a refused role update and leaves the listing untouched', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      store.updateRole(0, anUpdateRequest());
      expectPut(ROLE_ZERO_URL).flush(
        aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('updateRole');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleName);
      expect(present(store.roleItems()[0], 'the untouched row').roleName).toBe('Administrators');
      expect(store.saving()).toBeFalse();
    });

    it('attributes a refused assignment write and re-reads nothing', () => {
      store.assignUser(7, {
        userId: 42,
        effectiveDate: null,
        expiryDate: null,
        notifyUser: false,
      });
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(
        aProblem(400, null, 'The account is unknown.'),
        { status: 400, statusText: 'Bad Request' },
      );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('assignUser');
      expect(failure.summary.severity).toBe('error');
      expect(store.assignmentItems()).toEqual([]);
    });

    it('attributes a refused group rename and leaves the held group untouched', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL).flush(
        envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' })]),
      );

      store.updateRoleGroup(0, { roleGroupName: 'Renamed Groups', description: null });
      expectPut(ROLE_GROUP_ZERO_URL).flush(
        aProblem(409, CONFLICT_CODE.duplicateRoleGroupName, 'Already exists.'),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: RoleStoreFailure = present(store.failure(), 'the held failure');

      expect(failure.operation).toBe('updateRoleGroup');
      expect(failure.conflict).toBe(CONFLICT_CODE.duplicateRoleGroupName);
      expect(present(store.roleGroups()[0], 'the untouched group').roleGroupName).toBe(
        'Site Groups',
      );
    });
  });

  // -------------------------------------------------------------------------
  // ROLE WRITES
  // -------------------------------------------------------------------------

  describe('role writes reconcile from the response and leave listing reads to the listing', () => {
    it('creates a role, holds the server\u2019s own payload, then re-reads the listing', () => {
      const request: CreateRoleRequest = aCreateRequest();

      store.createRole(request);

      const write = expectPost(ROLES_URL);

      expect(write.request.body).toEqual(request);
      write.flush(envelopeOf(aRole({ roleId: 0, roleName: 'Subscribers' })), {
        status: 201,
        statusText: 'Created',
      });

      // ⚠ NOT RE-READ HERE, AND THE DUPLICATE THAT WOULD FOLLOW IS MEASURED. The case for re-reading is
      // sound as far as it goes - where the role falls, and whether it falls on the current page at all,
      // depends on the narrowing, the ordering and the page size, none of which this store may
      // re-implement - but the read that answers those questions is the LISTING'S, not this command's.
      expect(httpMock.match((candidate) => candidate.url === ROLES_URL))
        .withContext('a creation must not duplicate the read the listing issues for itself')
        .toHaveSize(0);

      expect(present(store.selectedRole(), 'the created role').roleId).toBe(0);
      expect(store.saving()).toBeFalse();
    });

    it('patches the listing row from the response body, without re-reading', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(
          pageOf(
            [
              aRoleListItem({ roleId: 0, roleName: 'Administrators' }),
              aRoleListItem({ roleId: 1, roleName: 'Registered Users' }),
            ],
            2,
          ),
        );

      const request: UpdateRoleRequest = anUpdateRequest();

      store.updateRole(0, request);

      const write = expectPut(ROLE_ZERO_URL);

      expect(write.request.body).toEqual(request);
      write.flush(envelopeOf(aRole({ roleId: 0, roleName: 'Site Administrators' })));

      // The patch copies the SERVER's values, not the request's, so it cannot show something the server did
      // not accept — and it matches the row keyed zero by identity, which is exactly the row a truthy test
      // would omit.
      expect(store.roleItems().map((item) => item.roleName)).toEqual([
        'Site Administrators',
        'Registered Users',
      ]);

      expect(httpMock.match((candidate) => candidate.url === ROLES_URL))
        .withContext('an update must not duplicate the read the listing issues for itself')
        .toHaveSize(0);
    });

    it('deletes the role keyed zero, discards the matching selection, then re-reads', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0 })));

      store.deleteRole(0, { thenReadListing: true });

      const removal = expectDelete(ROLE_ZERO_URL);

      removal.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.selectedRole()).toBeNull();

      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.roleItems()).toEqual([]);
    });

    it('leaves the listing alone when the caller has not asked for a read', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0 })));

      store.deleteRole(0, { thenReadListing: false });
      expectDelete(ROLE_ZERO_URL).flush(null, { status: 204, statusText: 'No Content' });

      expect(store.selectedRole()).toBeNull();

      // A caller that passes `false` is one that departs for the listing, and the listing reads itself from
      // its own address on arrival.
      expect(httpMock.match((candidate) => candidate.url === ROLES_URL))
        .withContext('a deletion must not read the listing when it was told not to')
        .toHaveSize(0);
    });

    it('keeps a selection that is not the deleted role', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL).flush(envelopeOf(aRole({ roleId: 7 })));

      store.deleteRole(0, { thenReadListing: true });
      expectDelete(ROLE_ZERO_URL)
        .flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(present(store.selectedRole(), 'the surviving selection').roleId).toBe(7);
    });
  });

  // -------------------------------------------------------------------------
  // RESPONSIBILITIES THAT DELIBERATELY LIVE ELSEWHERE
  // -------------------------------------------------------------------------

  describe('the store publishes no presentation and decides no authorisation', () => {
    it('publishes no formatting member of any kind', () => {
      const formatters: readonly string[] = publishedMembers(store).filter((name) =>
        /format|render|display|currency|toFixed|toLocale|label|caption|text$/i.test(name),
      );

      expect(formatters)
        .withContext('presentation must not leak into held state')
        .toEqual([]);
    });

    it('publishes no permission-deciding member', () => {
      // Authorisation is decided server-side and arrives as a refusal or a conflict. The two closed
      // permission vocabularies — the persisted keys and the policy names — are never interchanged, and
      // neither carries a negation prefix in this generation of the product.
      const decidingMembers: readonly string[] = [
        'hasPermission',
        'isInRole',
        'hasRole',
        'isAuthorised',
        'isAuthorized',
        'authorise',
        'authorize',
        'evaluatePermission',
        'isSuperUser',
        'isAdmin',
        'canView',
        'canEdit',
        'canRead',
        'canWrite',
        'canAdminister',
      ];
      const deciders: readonly string[] = publishedMembers(store).filter((name) =>
        decidingMembers.includes(name),
      );

      expect(deciders)
        .withContext('the server is the sole authority on what is permitted')
        .toEqual([]);
    });

    it('publishes no cache, expiry stamp or invalidation member', () => {
      // The legacy code reached a static cache helper from well over a hundred call sites with coarse
      // portal-wide and host-wide invalidation. A second, unsynchronised cache here would answer from stale
      // state after another administrator's change.
      const caching: readonly string[] = publishedMembers(store).filter((name) =>
        /cache|invalidat|expiresAt|staleness|isStale|evict/i.test(name),
      );

      expect(caching).toEqual([]);
    });

    it('derives no assignment status, because the server never sends one', () => {
      store.selectRole(7);
      expectGet(ROLE_SEVEN_URL)
        .flush(envelopeOf(aRole({ roleId: 7, billingFrequency: 'M', billingPeriod: 1 })));

      const classification: BillingTermsBound | null = store.selectedRoleBillingTerms();

      expect(classification).toBe('Bounded');

      // The two vocabularies are disjoint, and the COMPILER enforces it: writing the comparison against a
      // status value directly is rejected outright, because no member of the status union is assignable to
      // the term union.
      const termVocabulary: readonly string[] = [
        'Unbounded',
        'Perpetual',
        'Bounded',
        'Unsupported',
      ];
      const statusVocabulary: readonly string[] = ROLE_STATUS_VALUES;
      const overlap: readonly string[] = termVocabulary.filter((term) =>
        statusVocabulary.includes(term),
      );

      expect(overlap)
        .withContext('a term classification can never be mistaken for an assignment status')
        .toEqual([]);

      const held: string = present(classification, 'the term classification');

      expect(termVocabulary.includes(held)).toBeTrue();
      expect(statusVocabulary.includes(held)).toBeFalse();

      const statusMembers: readonly string[] = publishedMembers(store).filter((name) =>
        /roleStatus|assignmentStatus|isExpired|isActive|isPending/i.test(name),
      );

      expect(statusMembers).toEqual([]);
    });

    it('publishes no member that composes a request address', () => {
      const wireMembers: readonly string[] = publishedMembers(store).filter((name) =>
        /^url|Url$|endpoint|baseAddress|httpClient|apiBase/i.test(name),
      );

      expect(wireMembers).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // IMMUTABILITY AND LIFECYCLE
  // -------------------------------------------------------------------------

  // STALE RESPONSES AND SESSION TEARDOWN
  // Two properties, one mechanism.
  describe('stale responses cannot overwrite newer state', () => {
    it('abandons a superseded listing read, so a late answer can never land', () => {
      // ⚠ THE REGRESSION THIS PINS DOWN. Rapid narrowing, ordering or filter changes issue A then B.
      // Without cancellation, B answering first and A answering second leaves the slice describing A - the
      // narrowing the operator has already moved on from.
      store.setRolesQuery('alpha');
      const first = expectGet(ROLES_URL);

      store.setRolesQuery('beta');
      const second = expectGet(ROLES_URL);

      expect(first.cancelled)
        .withContext('the superseded read is abandoned when the next one is dispatched')
        .toBeTrue();

      // The testing backend refuses to answer a cancelled request at all, which is a stronger statement
      // than any arrival order this specification could stage: the stale read cannot deliver a value to
      // this store under ANY interleaving.
      expect(() => first.flush(pageOf([aRoleListItem({ roleName: 'Alpha' })], 1))).toThrowError(
        /cancelled/i,
      );

      second.flush(pageOf([aRoleListItem({ roleId: 1, roleName: 'Beta' })], 1));

      expect(store.roleItems().length).toBe(1);
      expect(store.roleItems()[0]?.roleName)
        .withContext('the state describes the latest request, never the last response to arrive')
        .toBe('Beta');
    });

    it('abandons a superseded single-role read, including one addressing role zero', () => {
      store.selectRole(0);
      const first = expectGet(`${ROLES_URL}/0`);

      store.selectRole(5);
      const second = expectGet(`${ROLES_URL}/5`);

      expect(first.cancelled).toBeTrue();

      second.flush(envelopeOf(aRole({ roleId: 5, roleName: 'Five' })));

      // Role ZERO is a real role - `dbo.Roles.RoleID` is `IDENTITY(0, 1)` - so the abandoned
      // read addressed a legitimate record rather than an absent one, and it still cannot land.
      expect(() => first.flush(envelopeOf(aRole({ roleId: 0, roleName: 'Zero' })))).toThrowError(
        /cancelled/i,
      );

      expect(store.selectedRole()?.roleId).toBe(5);
    });

    it('does NOT let one read cancel a different slice, because each holds its own handle', () => {
      // The assignment screen legitimately reads a role and its members at the same time. One
      // shared handle would make the second dispatch cancel the first and leave that slice empty.
      store.selectRole(7);
      const roleCall = expectGet(`${ROLES_URL}/7`);

      store.loadAssignments(7);
      const membersCall = expectGet(ROLE_SEVEN_MEMBERS_URL);

      expect(roleCall.cancelled)
        .withContext('an assignment read must not abandon the single-role read')
        .toBeFalse();

      roleCall.flush(envelopeOf(aRole({ roleId: 7 })));
      membersCall.flush(pageOf([anAssignment()], 1));

      expect(store.selectedRole()?.roleId).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
    });
  });

  describe('reset cannot be repopulated by work that was already in flight', () => {
    it('cancels every read, lowers every flag, and leaves nothing outstanding', () => {
      store.loadRoles();
      const rolesCall = expectGet(ROLES_URL);

      store.loadRoleGroups();
      const groupsCall = expectGet(ROLE_GROUPS_URL);

      store.selectRole(3);
      const roleCall = expectGet(`${ROLES_URL}/3`);

      store.loadAssignments(7);
      const membersCall = expectGet(ROLE_SEVEN_MEMBERS_URL);

      store.reset();

      expect(rolesCall.cancelled).toBeTrue();
      expect(groupsCall.cancelled).toBeTrue();
      expect(roleCall.cancelled).toBeTrue();
      expect(membersCall.cancelled).toBeTrue();

      expect(store.rolesLoading()).toBeFalse();
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.selectedRoleLoading()).toBeFalse();
      expect(store.assignmentsLoading()).toBeFalse();
      expect(store.busy()).toBeFalse();

      // Nothing is left outstanding, which the `afterEach` verification would report anyway; it is
      // asserted here so the failure names this behaviour rather than the next specification.
      httpMock.verify();

      expect(store.roleItems()).toEqual([]);
      expect(store.roleGroups()).toEqual([]);
      expect(store.selectedRole()).toBeNull();
      expect(store.assignmentItems()).toEqual([]);
    });

    it('abandons a corrective page read mid-flight rather than letting it land', () => {
      store.setRolesPage(2);
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 20 })], 21, 2));

      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([], 20, 2));

      const corrective = expectGet(ROLES_URL);

      expect(corrective.request.params.get('pageIndex')).toBe('1');

      store.reset();

      expect(corrective.cancelled)
        .withContext('a read dispatched from inside another read must still be abandonable')
        .toBeTrue();
      expect(store.roleItems()).toEqual([]);
      expect(store.rolesLoading()).toBeFalse();
      httpMock.verify();
    });

    it('releases every handle on teardown', () => {
      store.loadRoles();
      const call = expectGet(ROLES_URL);

      store.ngOnDestroy();

      expect(call.cancelled)
        .withContext('a request left listening across an injector boundary reports into a replaced store')
        .toBeTrue();
      httpMock.verify();
    });
  });

  describe('held state is replaced immutably and can be returned to its initial values', () => {
    it('replaces the listing rather than editing the sequence a consumer already read', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(pageOf([aRoleListItem({ roleId: 0, roleName: 'Administrators' })], 1));

      const readEarlier: readonly RoleListItem[] = store.roleItems();

      store.updateRole(0, anUpdateRequest());
      expectPut(ROLE_ZERO_URL)
        .flush(envelopeOf(aRole({ roleId: 0, roleName: 'Site Administrators' })));

      // A consumer that read the sequence before the write still sees what it read. That is possible solely
      // because the update replaced the sequence instead of editing it in place, and it is what lets a
      // reference-identity change-detection strategy work at all.
      expect(present(readEarlier[0], 'the earlier row').roleName).toBe('Administrators');
      expect(present(store.roleItems()[0], 'the current row').roleName).toBe(
        'Site Administrators',
      );
      expect(store.roleItems()).not.toBe(readEarlier);
    });

    it('replaces the group sequence rather than editing it in place', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0, roleGroupName: 'Site Groups' })]));

      const readEarlier: readonly RoleGroup[] = store.roleGroups();

      store.updateRoleGroup(0, { roleGroupName: 'Renamed Groups', description: null });
      expectPut(ROLE_GROUP_ZERO_URL)
        .flush(envelopeOf(aRoleGroup({ roleGroupId: 0, roleGroupName: 'Renamed Groups' })));

      expect(present(readEarlier[0], 'the earlier group').roleGroupName).toBe('Site Groups');
      expect(present(store.roleGroups()[0], 'the current group').roleGroupName).toBe(
        'Renamed Groups',
      );
      expect(store.roleGroups()).not.toBe(readEarlier);
    });

    it('discards the held failure on request, without contacting the server', () => {
      store.loadRoles();
      expectGet(ROLES_URL)
        .flush(aProblem(500, null, 'Unexpected.'), { status: 500, statusText: 'Server Error' });

      expect(store.failure()).not.toBeNull();

      store.clearError();

      expect(store.failure()).toBeNull();
    });

    it('discards the selected role on request, without contacting the server', () => {
      store.selectRole(0);
      expectGet(ROLE_ZERO_URL).flush(envelopeOf(aRole({ roleId: 0 })));

      store.clearSelectedRole();

      expect(store.selectedRole()).toBeNull();
    });

    it('returns every slice to its initial value, the narrowing to the measured default', () => {
      store.loadRoleGroups();
      expectGet(ROLE_GROUPS_URL)
        .flush(envelopeOf([aRoleGroup({ roleGroupId: 0 })]));

      store.setGroupFilter({ kind: 'Group', roleGroupId: 0 });
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem({ roleId: 0 })], 1));

      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment()], 1));

      store.reset();

      // For a sign-out or a tenant change, after which nothing held is still true. Not a cache
      // eviction: there is no cache to evict.
      expect(store.roleItems()).toEqual([]);
      expect(store.roleGroups()).toEqual([]);
      expect(store.assignmentItems()).toEqual([]);
      expect(store.selectedRole()).toBeNull();
      expect(store.assignmentsRoleId()).toBeNull();
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.groupFilter().kind)
        .withContext('the reset target is the ungrouped intent, per the measured legacy default')
        .toBe('GlobalRoles');
      expect(store.rolesPage().pageIndex).toBe(0);
      expect(store.rolesPage().pageSize).toBe(ROLES_PAGE_SIZE);
      expect(store.assignmentsPage().pageIndex).toBe(0);
      expect(store.assignmentsPage().pageSize).toBe(DEFAULT_PAGE_SIZE);
    });

    it('reports itself busy while a read is in flight and idle once it settles', () => {
      store.loadRoles();

      expect(store.rolesLoading()).toBeTrue();
      expect(store.busy()).toBeTrue();

      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(store.rolesLoading()).toBeFalse();
      expect(store.busy()).toBeFalse();
    });

    it('reports itself busy while a write is in flight and idle once it settles', () => {
      store.createRole(aCreateRequest());

      expect(store.saving()).toBeTrue();
      expect(store.busy()).toBeTrue();

      expectPost(ROLES_URL)
        .flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'), {
          status: 409,
          statusText: 'Conflict',
        });

      expect(store.saving())
        .withContext('a refused write must still settle the in-flight indicator')
        .toBeFalse();
      expect(store.busy()).toBeFalse();
    });
  });
  // SESSION ISOLATION AND READ CONCURRENCY
  // No handles meant two reads of the same thing raced, and the winner was whichever response arrived LAST
  // rather than whichever request was issued last.
  describe('session isolation and read concurrency', () => {
    // The shared helper matches on `request.url`, which is the path WITHOUT its query string, so the bare
    // paths are what these cases claim. The coordinates and the narrowing the listing carries are asserted
    // by the cases that exist for that purpose.
    const ROLES_LISTING_URL = ROLES_URL;
    const ZERO_MEMBERS_LISTING_URL = ROLE_ZERO_MEMBERS_URL;
    const SEVEN_MEMBERS_LISTING_URL = ROLE_SEVEN_MEMBERS_URL;

    it('cancels a read in flight on reset, so its answer cannot repopulate the store', () => {
      store.loadRoles();

      const pending = expectGet(ROLES_LISTING_URL);

      store.reset();

      expect(pending.cancelled)
        .withContext('the request is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.rolesLoading()).toBeFalse();
      expect(store.roles().items.length).toBe(0);
    });

    it('cancels a write in flight on reset, so its callback cannot act for the ended session', () => {
      // A write's callback re-reads the listing and records an outcome. Left listening across a
      // boundary it would do both on behalf of the session that ended.
      store.createRole(aCreateRequest());

      const pending = expectPost(ROLES_URL);

      store.reset();

      expect(pending.cancelled).toBeTrue();
      expect(store.saving()).toBeFalse();
    });

    it('abandons the earlier role listing read when a second is issued', () => {
      // ⚠ THE STALE-ANSWER RACE. Both requests answer the same question, so the LAST response to
      // arrive wins — which is not necessarily the one asked for last.
      store.loadRoles();

      const first = expectGet(ROLES_LISTING_URL);

      store.loadRoles();

      const second = expectGet(ROLES_LISTING_URL);

      expect(first.cancelled).toBeTrue();
      expect(second.cancelled).toBeFalse();

      second.flush(pageOf([aRoleListItem({ roleId: 5, roleName: 'Second answer' })], 1));

      expect(store.roles().items.length).toBe(1);
      expect(store.roles().items[0].roleName).toBe('Second answer');
    });

    it('abandons the earlier single-role read when another role is selected', () => {
      store.selectRole(0);

      const first = expectGet(ROLE_ZERO_URL);

      store.selectRole(7);

      const second = expectGet(ROLE_SEVEN_URL);

      expect(first.cancelled).toBeTrue();

      second.flush(envelopeOf(aRole({ roleId: 7, roleName: 'The one asked for' })));

      expect(store.selectedRole()?.roleId).toBe(7);
      expect(store.selectedRoleLoading()).toBeFalse();
    });

    it('abandons the earlier membership read when another role’s members are listed', () => {
      store.loadAssignments(0);

      const first = expectGet(ZERO_MEMBERS_LISTING_URL);

      store.loadAssignments(7);

      const second = expectGet(SEVEN_MEMBERS_LISTING_URL);

      expect(first.cancelled).toBeTrue();

      second.flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignments().items.length).toBe(1);
    });

    it('releases both reads of the combined administration chain', () => {
      // The chain performs BOTH reads, so its handle occupies both slots. Releasing either must
      // release the chain, and releasing an already-released subscription must be a no-op.
      store.loadRoleAdministration();

      const groups = expectGet(ROLE_GROUPS_URL);

      store.reset();

      expect(groups.cancelled).toBeTrue();
      expect(store.roleGroupsLoading()).toBeFalse();
      expect(store.rolesLoading()).toBeFalse();
    });

    it('keeps accepting writes after a reset, which a Subscription container would have broken', () => {
      // ⚠ A REGRESSION GUARD FOR A REAL TRAP. An RxJS `Subscription` used as a container is CLOSED once
      // unsubscribed, and anything added afterwards is unsubscribed the instant it is added — so the FIRST
      // boundary would release the writes correctly and then silently cancel every subsequent write for the
      // rest of the application's life.
      store.reset();

      store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });

      const pending = expectPost(ROLE_GROUPS_URL);

      expect(pending.cancelled)
        .withContext('a write issued after a reset must not be cancelled on arrival')
        .toBeFalse();

      pending.flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });

      // The create re-reads the groups, which proves the callback ran rather than being discarded.
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));

      expect(store.roleGroups().length).toBe(1);
      expect(store.saving()).toBeFalse();
    });
  });

  // WRITE IDENTITY, AND WHY AN AGGREGATE FLAG COULD NOT SETTLE A WRITE
  // (a) TWO WRITES, ONE FLAG. Screen A dispatches, screen B dispatches, A settles -> the flag falls while B
  // is still open, and BOTH screens conclude their own write is done. (b) SOMEBODY ELSE'S FAILURE. A's
  // write succeeds and B's is refused.
  describe('every write is settled by identity rather than by an aggregate flag', () => {
    it('hands back a distinct identifier for every write, and never the absent value', () => {
      // Zero is reserved as "no write awaited" by the screens that hold one of these, so the FIRST
      // identifier must not be zero. The counter therefore pre-increments, and that is asserted here rather
      // than left to a comment.
      const first = store.createRole(aCreateRequest());
      const second = store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });

      expect(first).withContext('the absent marker must never be issued').not.toBe(0);
      expect(second).not.toBe(first);
      expect(second).toBeGreaterThan(first);

      expectPost(ROLES_URL).flush(envelopeOf(aRole({ roleId: 7 })), {
        status: 201,
        statusText: 'Created',
      });
      expectPost(ROLE_GROUPS_URL).flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));
    });

    it('publishes a result naming the write that settled, its operation and its own outcome', () => {
      const issued = store.deleteRole(7, { thenReadListing: true });

      expect(store.mutation())
        .withContext('nothing is published while the write is open')
        .toBeNull();

      expectDelete(ROLE_SEVEN_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      const settled = present(store.mutation(), 'the settled write');

      expect(settled.id).toBe(issued);
      expect(settled.operation).toBe('deleteRole');
      expect(settled.failure)
        .withContext('a success settles with no failure attached')
        .toBeNull();
    });

    it('carries a refusal ON the settled result, so no screen reads it out of shared state', () => {
      const issued = store.updateRole(7, anUpdateRequest());

      expectPut(ROLE_SEVEN_URL).flush(
        aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'),
        { status: 409, statusText: 'Conflict' },
      );

      const settled = present(store.mutation(), 'the settled write');
      const failure = present(settled.failure, 'the refusal on the result');

      expect(settled.id).toBe(issued);
      expect(settled.operation).toBe('updateRole');
      expect(failure.operation).toBe('updateRole');
      expect(failure.status).toBe(409);
    });

    it('settles the first of two open writes without settling the second', () => {
      // ⚠ THIS IS DEFECT (a), AND IT IS THE CASE THE AGGREGATE FLAG COULD NOT EXPRESS. Both writes are
      // open; the second answers first. The result must name the SECOND, and the aggregate must stay raised
      // because the first is still open.
      const firstWrite = store.createRole(aCreateRequest());
      const secondWrite = store.createRoleGroup({
        roleGroupName: 'Paid Services',
        description: null,
      });

      const roleCreate = expectPost(ROLES_URL);

      expectPost(ROLE_GROUPS_URL).flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));

      expect(present(store.mutation(), 'the settled write').id).toBe(secondWrite);
      expect(store.saving())
        .withContext('one write settling must not report the other as settled')
        .toBeTrue();

      roleCreate.flush(envelopeOf(aRole({ roleId: 7 })), { status: 201, statusText: 'Created' });

      expect(present(store.mutation(), 'the settled write').id).toBe(firstWrite);
      expect(store.saving())
        .withContext('the aggregate falls only once every write has settled')
        .toBeFalse();
    });

    it('does not attach one write\u2019s refusal to another write\u2019s result', () => {
      // ⚠ THIS IS DEFECT (b). The refused write and the successful one overlap, and the successful one
      // settles LAST — so the shared failure slot holds a refusal at the very moment the successful write's
      // result is published.
      store.updateRole(7, anUpdateRequest());

      const refused = expectPut(ROLE_SEVEN_URL);
      const succeeding = store.createRoleGroup({
        roleGroupName: 'Paid Services',
        description: null,
      });
      const groupCreate = expectPost(ROLE_GROUPS_URL);

      refused.flush(aProblem(409, CONFLICT_CODE.duplicateRoleName, 'Already exists.'), {
        status: 409,
        statusText: 'Conflict',
      });

      // The refusal IS in the shared slot at this instant, which is what makes the second half of
      // this case meaningful rather than vacuous.
      expect(present(store.failure(), 'the shared slot').operation).toBe('updateRole');

      groupCreate.flush(envelopeOf(aRoleGroup({ roleGroupId: 3 })), {
        status: 201,
        statusText: 'Created',
      });
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup({ roleGroupId: 3 })]));

      const settled = present(store.mutation(), 'the settled write');

      expect(settled.id).toBe(succeeding);
      expect(settled.failure)
        .withContext('a successful write must not inherit the other write\u2019s refusal')
        .toBeNull();

      expect(store.failure())
        .withContext('the shared slot cannot be relied on to still hold the refusal')
        .toBeNull();
    });

    it('clears the published result and the pending count on a session boundary', () => {
      store.deleteRole(7, { thenReadListing: true });
      expectDelete(ROLE_SEVEN_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLES_URL).flush(pageOf([], 0));

      expect(store.mutation()).not.toBeNull();

      store.reset();

      expect(store.mutation())
        .withContext('a result from the ended session must not settle a new one\u2019s write')
        .toBeNull();
      expect(store.saving()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // WHICH ROLE A SETTLED MEMBERSHIP WRITE MAY REFRESH
  // -------------------------------------------------------------------------
  describe('a membership write refreshes only a listing it cannot misattribute', () => {
    const request: RoleAssignmentRequest = Object.freeze({
      userId: 42,
      effectiveDate: null,
      expiryDate: null,
      notifyUser: false,
    });

    it('adopts the role it wrote to when no listing is in scope', () => {
      // Nothing is being viewed, so there is no set to overwrite and no heading to mis-label. The
      // refresh is what makes the enrolment observable at all on a fresh store.
      expect(store.assignmentsRoleId()).toBeNull();

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
    });

    it('refreshes the listing when the write addressed the role already in scope', () => {
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
      expect(store.assignmentItems().length).toBe(1);
    });

    it('refuses to move the scope when a DIFFERENT role is the one being viewed', () => {
      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      store.loadAssignments(0);

      const otherRead = expectGet(ROLE_ZERO_MEMBERS_URL);

      write.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.assignmentsRoleId())
        .withContext('the scope stays where the operator put it')
        .toBe(0);
      expect(otherRead.cancelled)
        .withContext('the read the operator is waiting for must survive')
        .toBeFalse();

      otherRead.flush(pageOf([anAssignment({ roleId: 0, userId: 9 })], 1));

      expect(store.assignmentsRoleId()).toBe(0);
      expect(present(store.assignmentItems()[0], 'the held row').roleId).toBe(0);
    });

    it('applies the same test to a removal, which re-reads for a different reason', () => {
      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      store.removeAssignment(7, 42);
      expectDelete(ROLE_SEVEN_MEMBER_URL).flush(null, { status: 204, statusText: 'No Content' });

      // No second read is opened. `httpMock.verify()` in the shared teardown is what proves it.
      expect(store.assignmentsRoleId()).toBe(0);
      expect(store.assignmentItems().length).toBe(1);
    });

    it('refreshes the listing on screen when the write addressed the role it shows', () => {
      // The ordinary path, now stated through the screen's own announcement rather than through the
      // scope standing in for it.
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentItems().length).toBe(1);
    });

    it('refuses the refresh once the screen that asked has been LEFT', () => {
      // ⚠ THE DEFECT RUNTIME VALIDATION CAUGHT, and the reason the screen's announcement exists at all. The
      // scope test alone cannot see this case: leaving a membership listing does not blank the rows, so the
      // scope STILL names role 7 here and agreed with the write.
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      // The operator returns to the role listing: the membership screen is destroyed, so it closes.
      store.closeAssignmentsView(7);

      write.flush(null, { status: 204, statusText: 'No Content' });

      // No re-read is opened. `httpMock.verify()` in the shared teardown is what proves it.
      expect(store.assignmentsRoleId())
        .withContext('the rows keep their label; only the refresh is refused')
        .toBe(7);
    });

    it('applies the same refusal to a removal, which re-reads for a different reason', () => {
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      store.removeAssignment(7, 42);

      const write = expectDelete(ROLE_SEVEN_MEMBER_URL);

      store.closeAssignmentsView(7);
      write.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.assignmentsRoleId()).toBe(7);
    });

    it('refreshes again for a screen that leaves and comes back to the same role', () => {
      // Returning re-announces, so the refusal is not sticky: the operator is looking at role 7's
      // grid again and must see the change their own write made.
      store.openAssignmentsView(7);
      store.loadAssignments(7);
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(7, request);

      const write = expectPost(ROLE_SEVEN_MEMBERS_URL);

      store.closeAssignmentsView(7);
      store.openAssignmentsView(7);

      write.flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentItems().length).toBe(1);
    });

    it('ignores a departing screen that names a role another screen has already claimed', () => {
      // A screen tears down and a replacement announces itself in an order this store does not control.
      // Were the close unconditional, the predecessor's teardown would un-announce the replacement and the
      // replacement's own legitimate refresh would then be refused.
      store.openAssignmentsView(0);
      store.closeAssignmentsView(7);

      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([], 0));

      store.assignUser(0, {
        ...request,
        userId: 9,
      });
      expectPost(ROLE_ZERO_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0, userId: 9 })], 1));

      expect(store.assignmentItems().length).toBe(1);
    });

    it('adopts again after a session reset has discarded the departed screen', () => {
      // A reset returns the store to "nothing has been on screen", not to "the screen has left", so
      // the adoption contract holds for the next session.
      store.openAssignmentsView(7);
      store.closeAssignmentsView(7);
      store.reset();

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectGet(ROLE_SEVEN_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 7 })], 1));

      expect(store.assignmentsRoleId()).toBe(7);
    });

    it('treats role zero as an occupied scope rather than as an absent one', () => {
      // Role keys are `IDENTITY(0, 1)`, so role 0 is the Administrators role of a fresh tenant. A
      // truthiness test on the scope would read it as "nothing in scope" and take the adoption branch —
      // reintroducing the republication for precisely the most important role.
      store.loadAssignments(0);
      expectGet(ROLE_ZERO_MEMBERS_URL).flush(pageOf([anAssignment({ roleId: 0 })], 1));

      store.assignUser(7, request);
      expectPost(ROLE_SEVEN_MEMBERS_URL).flush(null, { status: 204, statusText: 'No Content' });

      expect(store.assignmentsRoleId()).toBe(0);
    });
  });
  // =========================================================================
  // THE SETTLED LATCH — "NOT ASKED YET" IS NOT "ASKED AND EMPTY"
  // =========================================================================

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. An un-asked listing and a listing that matched nothing are
  // both an empty page with no request in flight, so a grid reading only the rows and the in-flight flag
  // painted "No records found." over a listing nobody had read yet.
  describe('the settled latch', () => {
    it('is DOWN on a fresh store and nothing is in flight, which is what made the two states identical', () => {
      expect(store.listSettled()).toBeFalse();
      expect(store.roles().items).toEqual([]);
      expect(store.rolesLoading()).toBeFalse();
    });

    it('stays DOWN across the opening chain until the ROLES read answers, not merely the groups read', () => {
      store.loadRoleAdministration();

      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup()]));
      expect(store.listSettled())
        .withContext('the groups have answered but the listing has not')
        .toBeFalse();

      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));

      expect(store.listSettled()).toBeTrue();
    });

    it('rises on a FAILED listing read too, so a waiting indicator cannot stand over a reportable failure', () => {
      store.loadRoleAdministration();
      expectGet(ROLE_GROUPS_URL).flush(envelopeOf([aRoleGroup()]));
      expectGet(ROLES_URL).flush(aProblem(500, null, 'Unavailable.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.listSettled()).toBeTrue();
      expect(store.failure()).not.toBeNull();
    });

    it('goes back DOWN on reset, because the page it spoke for is discarded with the session', () => {
      store.loadRoles();
      expectGet(ROLES_URL).flush(pageOf([aRoleListItem()], 1));
      expect(store.listSettled()).toBeTrue();

      store.reset();

      expect(store.listSettled()).toBeFalse();
      expect(store.roles().items).toEqual([]);
    });
  });

});

/**
 * A role creation request carrying the awkward values rather than tidy ones.
 *
 * @returns One creation request.
 */
function aCreateRequest(): CreateRoleRequest {
  return {
    roleName: 'Subscribers',
    description: '',
    serviceFee: 0,
    billingPeriod: -1,
    billingFrequency: 'M',
    trialFee: 0,
    trialPeriod: -1,
    trialFrequency: 'N',
    isPublic: false,
    autoAssignment: false,
    roleGroupId: null,
    rsvpCode: '',
    iconFile: null,
  };
}

/**
 * A role update request carrying the awkward values rather than tidy ones.
 *
 * @returns One update request.
 */
function anUpdateRequest(): UpdateRoleRequest {
  return {
    roleName: 'Site Administrators',
    description: '',
    roleGroupId: null,
    isPublic: false,
    autoAssignment: false,
    serviceFee: 0,
    billingPeriod: -1,
    billingFrequency: 'M',
    trialFee: 0,
    trialPeriod: -1,
    trialFrequency: 'N',
    rsvpCode: '',
    iconFile: null,
    // The awkward-values fixture carries a token too, because a replacement composed against a read
    // has one; sending null would be a different case, not a tidier version of this one.
    concurrencyToken: 'revision-1',
  };
}
