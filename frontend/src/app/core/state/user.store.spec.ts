import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  DEFAULT_PAGE_SIZE,
  type ApiMeta,
  type ApiResponse,
  type PagedResult,
} from '../models/paged-result.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type UpdateProfilePropertyDefinitionRequest,
  type UserProfile,
  type UserProfileSubmission,
} from '../models/profile.model';
import {
  PasswordFormat,
  UserCreateStatus,
  type ChangePasswordRequest,
  type CreateUserRequest,
  type MemberService,
  type MembershipSettings,
  type MembershipSettingsUpdateResult,
  type RedeemServiceCodeResult,
  type UpdateUserRequest,
  type UserDetail,
  type UserListItem,
} from '../models/user.model';
import { UserService } from '../services/user.service';
import {
  UserStore,
  type ProfileDefinitionEdit,
  type UserFailure,
  type UserSearchMode,
} from './user.store';

// ---------------------------------------------------------------------------
// THE EXPECTED ADDRESSES, SPELLED OUT INDEPENDENTLY
// ---------------------------------------------------------------------------

/** The account collection. */
const USERS_URL = '/api/v1/users';

/**
 * The body-bound account search. ⚠ A SEARCH BY NAME, ADDRESS OR PROFILE PROPERTY GOES HERE, NOT TO {@link
 * USERS_URL}, AND THE REASON IS PRIVACY RATHER THAN ROUTING. All four of those filters identify a person,
 * and a query parameter travels in the REQUEST TARGET — which the browser writes to its history, every
 * forward and reverse proxy writes to an access log, the server writes to another, and URL-sampling
 * telemetry writes to a third.
 */
const USERS_SEARCH_URL = '/api/v1/users/search';

/** The tenant's account policy. The `settings` child of the account collection. */
const SETTINGS_URL = '/api/v1/users/settings';

/** The tenant's profile declarations. Unpaged, and scoped by the resolved tenant. */
const DEFINITIONS_URL = '/api/v1/profile-definitions';

/** The member-services catalogue of account 7 — the account every fixture in this file uses. */
const SERVICES_URL = '/api/v1/users/7/services';

/**
 * The subscription of account 7 to service ZERO. ⚠ THE SERVICE IDENTIFIER IS ZERO ON PURPOSE.
 * `Roles.RoleID` seeds `IDENTITY(0, 1)`, so role zero is the administrator role of every shipped
 * installation — and it is exactly the value a truthiness test drops. Every address here uses it.
 */
const SERVICE_SUBSCRIPTION_URL = '/api/v1/users/7/services/0/subscription';

/** The trial of service zero, taken by account 7. */
const SERVICE_TRIAL_URL = '/api/v1/users/7/services/0/trial';

/** The invitation-code redemptions of account 7. */
const SERVICE_REDEMPTIONS_URL = '/api/v1/users/7/services/redemptions';

/**
 * The legacy reserved word that meant "no search". `Users.ascx.vb` L266 guarded the whole search block by
 * comparing the typed text against this bare magic string, so a person could never search for it even
 * though it is a perfectly ordinary thing to type.
 */
const NO_SEARCH_RESERVED_WORD = 'None';

/** A synthetic trace-context value. */
const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00';

/** A synthetic correlation value. */
const CORRELATION_ID = 'c0rr-3l4t10n-0000-0000-000000000001';

/** The prefix the server wraps a machine-readable failure code in. */
const FAILURE_TYPE = 'urn:dnnmigration:error:';

// FIXTURES
// Every fixture is a factory returning a fresh object, and every one is typed as the real contract. Both
// properties matter: a shared mutable fixture would let one specification's edit change another's subject,
// and an untyped literal would let a mis-spelled member compile and arrive as `undefined`.

/** Paging facts. Defaults describe an empty first page at the shared fallback size. */
const metaFixture = (overrides: Partial<ApiMeta> = {}): ApiMeta => ({
  totalCount: 0,
  pageIndex: 0,
  pageSize: DEFAULT_PAGE_SIZE,
  totalPages: 0,
  ...overrides,
});

/** One account row. Sentinel-bearing by default rather than tidied. */
const listItemFixture = (overrides: Partial<UserListItem> = {}): UserListItem => ({
  userId: 7,
  portalId: 0,
  username: 'ann.admin',
  firstName: 'Ann',
  lastName: 'Admin',
  displayName: 'Ann Admin',
  address: null,
  telephone: null,
  email: 'ann.admin@example.invalid',
  createdDate: '2024-01-05T09:15:00Z',
  lastLoginDate: null,
  isApproved: true,
  isOnline: false,
  isSuperUser: false,
  isLockedOut: false,
  canDelete: true,
  ...overrides,
});

/** One page of accounts, with coordinates derived from the rows unless overridden. */
const pageFixture = (
  items: readonly UserListItem[],
  overrides: Partial<ApiMeta> = {},
): PagedResult<UserListItem> => ({
  items,
  meta: metaFixture({
    totalCount: items.length,
    totalPages: items.length > 0 ? 1 : 0,
    ...overrides,
  }),
});

/** One account in full. */
const detailFixture = (overrides: Partial<UserDetail> = {}): UserDetail => ({
  userId: 7,
  portalId: 0,
  username: 'ann.admin',
  firstName: 'Ann',
  lastName: 'Admin',
  displayName: 'Ann Admin',
  email: 'ann.admin@example.invalid',
  isSuperUser: false,
  affiliateId: null,
  isApproved: true,
  isLockedOut: false,
  isOnline: false,
  mustChangePassword: false,
  createdDate: '2024-01-05T09:15:00Z',
  lastLoginDate: null,
  lastActivityDate: null,
  lastLockoutDate: null,
  lastPasswordChangeDate: null,
  roles: ['Registered Users'],
  canDelete: true,
  // Opaque and never interpreted here: a fixture only has to carry one for the round trip to close.
  concurrencyToken: 'user-revision-token',
  ...overrides,
});

/**
 * The tenant's account policy. Every member is stated, because the contract is a replace rather than a
 * merge and a partial literal would not compile.
 */
const storedSettingsFixture = (overrides: Partial<MembershipSettings> = {}): MembershipSettings => ({
  // ⚠ #5/#6 — stated rather than left to the override, so a specification that says nothing about
  // provenance still receives a policy that claims to be stored.
  isStored: true,
  columnFirstName: true,
  columnLastName: true,
  columnDisplayName: true,
  columnAddress: false,
  columnTelephone: false,
  columnEmail: true,
  columnCreatedDate: true,
  columnLastLogin: false,
  columnAuthorized: true,
  displayMode: 0,
  displaySuppressPager: false,
  recordsPerPage: 25,
  profileDefaultVisibility: PROFILE_VISIBILITY.adminOnly,
  profileDisplayVisibility: true,
  profileManageServices: false,
  redirectAfterLogin: null,
  redirectAfterRegistration: null,
  redirectAfterLogout: null,
  securityEmailValidation: '^[^@]+@[^@]+$',
  securityRequireValidProfile: false,
  securityRequireValidProfileAtLogin: false,
  securityUsersControl: 0,
  securityDisplayNameFormat: '[FIRSTNAME] [LASTNAME]',
  ...overrides,
});

/**
 * The policy a tenant with NO SETTINGS SOURCE is answered with. ⚠ #5/#6 — A SUCCESSFUL DOCUMENT THAT
 * REPORTS THE ABSENCE OF A STORE, which is the shape the store under test has to read provenance from.
 * The server answers a portal holding no "User Accounts" module instance `200` with the measured legacy
 * defaults and `isStored: false`; the write for the same address answers `409`.
 */
const unstoredSettingsFixture = (
  overrides: Partial<MembershipSettings> = {},
): MembershipSettings =>
  storedSettingsFixture({
    isStored: false,
    columnFirstName: false,
    columnLastName: false,
    columnDisplayName: true,
    columnAddress: true,
    columnTelephone: true,
    columnEmail: false,
    columnCreatedDate: true,
    columnLastLogin: false,
    columnAuthorized: true,
    displayMode: 2,
    recordsPerPage: 10,
    profileDefaultVisibility: PROFILE_VISIBILITY.adminOnly,
    profileDisplayVisibility: true,
    profileManageServices: true,
    redirectAfterLogin: null,
    redirectAfterRegistration: null,
    redirectAfterLogout: null,
    securityRequireValidProfileAtLogin: true,
    securityDisplayNameFormat: '',
    ...overrides,
  });

/** One profile declaration. */
const definitionFixture = (
  overrides: Partial<ProfilePropertyDefinition> = {},
): ProfilePropertyDefinition => ({
  propertyDefinitionId: 0,
  portalId: 0,
  moduleDefId: 0,
  dataType: 0,
  defaultValue: null,
  propertyCategory: 'Contact',
  propertyName: 'Nickname',
  length: 0,
  required: false,
  validationExpression: null,
  viewOrder: 0,
  visible: true,
  visibility: PROFILE_VISIBILITY.allUsers,
  ...overrides,
});

/**
 * One account's whole profile. `displayVisibilityEnabled` is the tenant's decision on whether the
 * per-property visibility affordance is offered; it rides the profile projection because the settings
 * endpoint that declares it admits only portal administrators.
 */
const profileFixture = (userId = 7, displayVisibilityEnabled = true): UserProfile => ({
  userId,
  displayVisibilityEnabled,
  properties: [
    {
      propertyDefinitionId: 0,
      propertyValue: '',
      visibility: PROFILE_VISIBILITY.allUsers,
      lastUpdatedDate: null,
      definition: definitionFixture(),
    },
    {
      propertyDefinitionId: 4,
      propertyValue: 'Anywhere',
      visibility: PROFILE_VISIBILITY.adminOnly,
      lastUpdatedDate: '2024-02-01T00:00:00Z',
      definition: definitionFixture({ propertyDefinitionId: 4, propertyName: 'City' }),
    },
  ],
});

/** A whole profile submission. */
const submissionFixture = (userId = 7): UserProfileSubmission => ({
  userId,
  properties: [
    {
      propertyDefinitionId: 0,
      propertyValue: '',
      visibility: PROFILE_VISIBILITY.allUsers,
    },
  ],
});

/** A request to create an account. The credential value is obviously synthetic. */
const createRequestFixture = (overrides: Partial<CreateUserRequest> = {}): CreateUserRequest => ({
  username: 'new.account',
  firstName: 'New',
  lastName: 'Account',
  displayName: 'New Account',
  email: 'new.account@example.invalid',
  password: 'fake-placeholder-not-a-credential',
  confirmPassword: 'fake-placeholder-not-a-credential',
  authorize: true,
  ...overrides,
});

/** A request to update an account's own details. */
const updateRequestFixture = (overrides: Partial<UpdateUserRequest> = {}): UpdateUserRequest => ({
  firstName: 'Ann',
  lastName: 'Administrator',
  displayName: 'Ann Administrator',
  email: 'ann.admin@example.invalid',
  // The revision the submission was composed against, echoed back verbatim.
  concurrencyToken: 'user-revision-token',
  ...overrides,
});

/** A request to replace one profile declaration. */
const definitionWriteFixture = (
  overrides: Partial<UpdateProfilePropertyDefinitionRequest> = {},
): UpdateProfilePropertyDefinitionRequest => ({
  propertyName: 'Nickname',
  propertyCategory: 'Contact',
  dataType: 0,
  defaultValue: null,
  length: 0,
  required: false,
  validationExpression: null,
  viewOrder: 0,
  visible: true,
  ...overrides,
});

/** The single-payload success envelope every non-collection endpoint answers with. */
const envelope = <T>(data: T): ApiResponse<T> => ({ data, meta: null });

/**
 * The page size the whole-catalogue reader asks for. Mirrors `WHOLE_CATALOGUE_PAGE_SIZE` in `UserService`.
 */
const CATALOGUE_PAGE_SIZE = 100;

/**
 * The PAGED wire envelope the member-services endpoint answers with. The catalogue used to arrive in one
 * unbounded response; it is now read a bounded page at a time, so its body carries populated metadata where
 * the single-payload envelope carries none.
 *
 * @param items The rows of this page.
 * @param totalCount The total across every page. Defaults to a single complete page.
 * @returns The body to flush.
 */
const cataloguePage = <TRow>(
  items: readonly TRow[],
  totalCount: number = items.length,
): {
  readonly items: readonly TRow[];
  readonly meta: {
    readonly totalCount: number;
    readonly pageIndex: number;
    readonly pageSize: number;
    readonly totalPages: number;
  };
} => ({
  items,
  meta: {
    totalCount,
    pageIndex: 0,
    pageSize: CATALOGUE_PAGE_SIZE,
    totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / CATALOGUE_PAGE_SIZE),
  },
});

/**
 * The report the account-policy write answers with, wrapped in the shared envelope. Defaults to "nothing
 * was swept", which is what an ordinary settings save produces, so a fact that merely needs the write to
 * succeed does not have to describe a rewrite it never asked for.
 */
const settingsWriteEnvelope = (
  overrides: Partial<MembershipSettingsUpdateResult> = {},
): ApiResponse<MembershipSettingsUpdateResult> =>
  envelope<MembershipSettingsUpdateResult>({
    displayNameFormatChanged: false,
    displayNamesRewritten: 0,
    ...overrides,
  });

/**
 * One row of the member-services catalogue. Defaults to a paid service the account already holds whose
 * subscription has LAPSED, which is the row that exercises the most contract at once: service identifier
 * zero, a fifty-cent fee the legacy projection could not express, and the `Renew` command the legacy
 * screen derived from an expiry earlier than today.
 */
const serviceFixture = (overrides: Partial<MemberService> = {}): MemberService => ({
  roleId: 0,
  roleName: 'Premium Members',
  description: 'Access to the subscriber area',
  serviceFee: 0.5,
  billingPeriod: 1,
  billingFrequency: 'M',
  trialFee: 0,
  trialPeriod: 14,
  trialFrequency: 'D',
  effectiveDate: '2026-01-01T00:00:00Z',
  expiryDate: '2026-02-01T00:00:00Z',
  isSubscribed: true,
  isTrialUsed: false,
  isExpired: true,
  subscriptionAction: 'Renew',
  subscriptionOffered: true,
  subscriptionRequiresPayment: true,
  trialOffered: true,
  ...overrides,
});

/** A problem document, defaulting to a refusal that carries both identifiers. */
const problemFixture = (overrides: Partial<ProblemDetails> = {}): ProblemDetails => ({
  type: `${FAILURE_TYPE}user.membership.self-forbidden`,
  title: 'Forbidden',
  status: 403,
  detail: 'The caller may not perform this action.',
  traceId: TRACE_ID,
  correlationId: CORRELATION_ID,
  ...overrides,
});

/** A validation problem document, whose per-field dictionary is required. */
const validationProblemFixture = (
  overrides: Partial<ValidationProblemDetails> = {},
): ValidationProblemDetails => ({
  type: `${FAILURE_TYPE}user.create.duplicate-username`,
  title: 'One or more validation errors occurred.',
  status: 422,
  detail: 'The request was understood but could not be processed.',
  traceId: TRACE_ID,
  correlationId: CORRELATION_ID,
  // The keys are the server's model-state keys, reproduced as it writes them: they name model members
  // rather than JSON members, so the camel-case body policy does not apply to them and they stay
  // Pascal-cased.
  errors: {
    UserName: ['The user name is already taken.'],
    Email: ['The address is malformed.'],
  },
  ...overrides,
});

// ---------------------------------------------------------------------------
// THE SPECIFICATION
// ---------------------------------------------------------------------------

describe('UserStore', () => {
  let store: UserStore;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // The real client FIRST, then the testing backend that displaces it. The order is load-bearing: the
        // second provider overrides the first, so reversing them leaves the live backend in place and every
        // expectation below times out against a request that was never intercepted.
        provideHttpClient(),
        provideHttpClientTesting(),
        UserStore,
        // NO INTERCEPTOR IS REGISTERED. The correlation identifier, the bearer token and the translation of
        // a failure into a problem document are three separately specified units; running them here would
        // assert several units at once.
      ],
    });

    store = TestBed.inject(UserStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // THE LOAD-BEARING ASSERTION OF THIS ENTIRE FILE. It fails if a command issued a request nothing
    // expected, and it is the only automated proof that reading the account policy does not also warm a
    // cache, that the no-search state really dispatches nothing, and that a failure path does not retry.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HELPERS
  // -------------------------------------------------------------------------

  /**
   * Expects exactly one outstanding request with the given verb and PATH. Matches on the path rather than
   * on the path-and-query, because the string overload compares the full URL including its query string -
   * which would couple every paged expectation to the order in which parameters happen to be composed.
   * Query parameters are asserted separately, by name.
   */
  const expectRequest = (method: string, path: string): TestRequest =>
    httpMock.expectOne(
      (request) => request.method === method && request.url === path,
      `${method} ${path}`,
    );

  /** Reads one query parameter, narrowing it explicitly. */
  const parameter = (request: TestRequest, name: string): string => {
    const value = request.request.params.get(name);

    if (value === null) {
      throw new Error(`expected the query parameter "${name}" to be present`);
    }

    return value;
  };

  /** The one outstanding body-bound account search. */
  const expectSearch = (): TestRequest => expectRequest('POST', USERS_SEARCH_URL);

  /**
   * The body a search transmitted, narrowed by throwing rather than cast.
   *
   * @param request The search whose body to read.
   * @returns The body as a keyed record.
   */
  const body = (request: TestRequest): Readonly<Record<string, unknown>> => {
    const sent: unknown = request.request.body;

    if (typeof sent !== 'object' || sent === null || Array.isArray(sent)) {
      throw new Error('the search did not transmit a JSON object body');
    }

    return { ...sent };
  };

  /**
   * One string member of a search body, narrowed by throwing.
   *
   * @param request The search to read.
   * @param name The member to read.
   * @returns The member's value.
   */
  const member = (request: TestRequest, name: string): string => {
    const value: unknown = body(request)[name];

    if (typeof value !== 'string') {
      throw new Error(`expected the body member "${name}" to be present as text`);
    }

    return value;
  };

  /** Asserts that none of the named body members was emitted at all. */
  const expectMembersOmitted = (request: TestRequest, names: readonly string[]): void => {
    const sent = body(request);

    for (const name of names) {
      expect(Object.prototype.hasOwnProperty.call(sent, name))
        .withContext(`the body member "${name}" must be omitted, not sent empty`)
        .toBe(false);
    }
  };

  /**
   * Asserts that a request's TARGET carries none of the given values. The load-bearing assertion of the
   * privacy cases: it is not enough that a searched value reached the server in the body, it must be
   * ABSENT from the string that gets logged.
   *
   * @param request The request to inspect.
   * @param values The values that must not appear in the target.
   */
  const expectTargetCarriesNoneOf = (request: TestRequest, values: readonly string[]): void => {
    for (const value of values) {
      if (value.length === 0) {
        continue;
      }

      expect(request.request.urlWithParams)
        .withContext(`"${value}" must not appear in the request target`)
        .not.toContain(value);
    }
  };

  /** Asserts that none of the named query parameters was emitted at all. */
  const expectOmitted = (request: TestRequest, names: readonly string[]): void => {
    for (const name of names) {
      // Absence is proved by asking whether the parameter is THERE. A reader answering null is a weaker
      // claim: it is also what a present-but-empty parameter answers, and empty text is a legitimate value
      // on this contract.
      expect(request.request.params.has(name))
        .withContext(`the parameter "${name}" must be omitted, not sent empty`)
        .toBe(false);
    }
  };

  /** The filter parameters, named once so every omission proof stays in step. */
  const FILTER_PARAMETERS: readonly string[] = [
    'userName',
    'email',
    'profilePropertyName',
    'profilePropertyValue',
  ];

  /** The paging and ordering parameters, for the unpaged proofs. */
  const PAGING_PARAMETERS: readonly string[] = [
    'pageIndex',
    'pageSize',
    'sortBy',
    'sortDir',
    'query',
  ];

  /** Recovers the recorded failure, narrowing it explicitly. */
  const recordedFailure = (): UserFailure => {
    const failure = store.failure();

    if (failure === null) {
      throw new Error('expected a recorded failure, but the failure slot was empty');
    }

    return failure;
  };

  /** Recovers the problem document off a recorded failure, narrowing it explicitly. */
  const recordedProblem = (): ProblemDetails => {
    const { problem } = recordedFailure();

    if (problem === null) {
      throw new Error('expected the recorded failure to carry a problem document');
    }

    return problem;
  };

  /**
   * Brings the store up the way a listing screen does, at a stated page size. Reads the account policy,
   * flushes it, then flushes the listing the store dispatches once the policy is in hand.
   */
  const openListingAtPageSize = (recordsPerPage: number): void => {
    store.initialise();
    expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage })));
    expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));
  };

  // =========================================================================
  // LIST PAGING
  // =========================================================================

  describe('list paging', () => {
    it('requests the first page as index 0, because the wire index is zero-based', () => {
      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageIndex'))
        .withContext('the first page is index 0 on the wire')
        .toBe('0');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('requests the third page as index 2', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.goToPage(2);

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageIndex'))
        .withContext('the third page is index 2, not 3')
        .toBe('2');

      request.flush(pageFixture([listItemFixture()], { pageIndex: 2, totalCount: 30 }));
    });

    it('never sends 1 for the first page', () => {
      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);
      const transmitted = parameter(request, 'pageIndex');

      expect(transmitted)
        .withContext('1 is the SECOND page; sending it for the first is the off-by-one')
        .not.toBe('1');
      expect(transmitted).toBe('0');

      request.flush(pageFixture([]));
    });

    it('passes a page index through unchanged rather than shifting it by one', () => {
      // Proves the absence of arithmetic in both directions: index 1 addresses the second page and arrives
      // as 1, so the value is neither incremented nor decremented anywhere.
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.goToPage(1);

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageIndex')).toBe('1');
      expect(store.requestedPageIndex()).toBe(1);

      request.flush(pageFixture([listItemFixture()], { pageIndex: 1, totalCount: 30 }));
    });

    it('returns to the first page when a new search is applied', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.goToPage(4);
      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture()], { pageIndex: 4, totalCount: 90 }),
      );

      store.searchByUsername('ann');

      // A search, so the page coordinates travel in the body alongside the term rather than in a
      // query string. The coordinate is a NUMBER there, not the string a query would have carried.
      const request = expectSearch();

      expect(body(request)['pageIndex'])
        .withContext('a new match set is a new first page')
        .toBe(0);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('holds the total across every page exactly as the envelope reported it', () => {
      store.showAllAccounts();

      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture()], {
          totalCount: 4211,
          pageIndex: 0,
          pageSize: DEFAULT_PAGE_SIZE,
          totalPages: 422,
        }),
      );

      expect(store.totalCount())
        .withContext('the total is the count across every page, not the rows in hand')
        .toBe(4211);
      expect(store.userRows().length).toBe(1);
    });

    it('reads the page count as the server computed it and does not divide it out', () => {
      // A deliberately inconsistent envelope: the quotient of the total and the page
      // size is not the reported count. A store that recomputed would answer 10 here.
      store.showAllAccounts();

      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture()], { totalCount: 100, pageSize: 10, totalPages: 7 }),
      );

      expect(store.totalPages())
        .withContext('the page count is a server fact, read as given')
        .toBe(7);
    });

    it('does not coerce away a page count of -1', () => {
      store.showAllAccounts();

      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture()], { totalCount: 30, totalPages: -1 }),
      );

      expect(store.totalPages()).toBe(-1);
      expect(store.totalPages()).not.toBe(0);
    });

    it('reports the page the server returned rather than the one last requested', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.goToPage(3);

      const request = expectRequest('GET', USERS_URL);

      // The requested index moves immediately; the reported index only once an answer has arrived. That gap
      // is what stops a pager claiming to be on a page whose request is still outstanding.
      expect(store.requestedPageIndex()).toBe(3);
      expect(store.currentPageIndex()).toBe(0);

      request.flush(pageFixture([listItemFixture()], { pageIndex: 3, totalCount: 40 }));

      expect(store.currentPageIndex()).toBe(3);
    });

    it('distinguishes an empty match set from a page beyond the end of one', () => {
      store.showAllAccounts();

      expectRequest('GET', USERS_URL).flush(pageFixture([], { totalCount: 0 }));

      expect(store.isEmptyResult())
        .withContext('nothing matched at all')
        .toBeTrue();
      expect(store.isPastEnd()).toBeFalse();

      store.goToPage(9);
      expectRequest('GET', USERS_URL).flush(
        pageFixture([], { pageIndex: 9, totalCount: 12, totalPages: 2 }),
      );

      expect(store.isPastEnd())
        .withContext('the set is not empty; this page lies past its end')
        .toBeTrue();
      expect(store.isEmptyResult()).toBeFalse();
    });

    it('advises a pager only when the tenant asked for suppression and a page is short', () => {
      openListingAtPageSize(25);

      store.saveMembershipSettings(
        storedSettingsFixture({ recordsPerPage: 25, displaySuppressPager: true }),
      );
      expectRequest('PUT', SETTINGS_URL).flush(settingsWriteEnvelope());
      expectRequest('GET', SETTINGS_URL).flush(
        envelope(storedSettingsFixture({ recordsPerPage: 25, displaySuppressPager: true })),
      );
      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture()], { pageSize: 25, totalCount: 4 }),
      );

      expect(store.pagerWarranted())
        .withContext('suppression requested and the whole set fits on one page')
        .toBeFalse();
    });
  });

  // =========================================================================
  // PAGE SIZE FROM THE TENANT'S ACCOUNT POLICY
  // =========================================================================

  describe('page size from the account policy', () => {
    it('reads the account policy first and only then requests the listing', () => {
      // THIS SEQUENCE IS THE WHOLE REASON COMPOSITION LIVES IN A STORE. The size of a page is a per-tenant
      // setting, so the listing cannot be requested correctly until the policy that declares it is in hand.
      // A transport cannot sequence the two without deciding it for every screen.
      store.initialise();

      // Only the policy is outstanding at this point. If the listing had been dispatched
      // in parallel it could not have carried the size, and this expectation fails.
      const settings = expectRequest('GET', SETTINGS_URL);

      httpMock.expectNone((request) => request.url === USERS_URL);

      settings.flush(envelope(storedSettingsFixture({ recordsPerPage: 25 })));

      const listing = expectRequest('GET', USERS_URL);

      // The counted proof of the ORDER, not merely of the absence above: the listing could only carry
      // the tenant's size because the policy that declares it had already been answered.
      expect(parameter(listing, 'pageSize'))
        .withContext('the listing carried the size the policy declared')
        .toBe('25');

      listing.flush(pageFixture([listItemFixture()], { pageSize: 25 }));
    });

    it('reuses a policy already in hand and still requests the listing', () => {
      // ⚠ THE DEFECT THIS GUARDS: the policy was re-read on every entry to the listing, which is a
      // tenant-wide constant being fetched again to learn a page size that had not changed. Reusing it must
      // NOT cost the listing read, which is the whole purpose of bringing the screen up.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 25 })));
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()], { pageSize: 25 }));

      store.initialise();

      httpMock.expectNone(SETTINGS_URL);

      const listing = expectRequest('GET', USERS_URL);

      expect(parameter(listing, 'pageSize'))
        .withContext('and the reused policy still supplies the size')
        .toBe('25');

      listing.flush(pageFixture([listItemFixture()], { pageSize: 25 }));
    });

    it('re-reads the policy for the EDITOR even when the listing already holds one', () => {
      // The editor's read is deliberately not guarded: an editor must show what the server holds now, and it
      // is the one screen whose whole purpose is to change the policy.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 25 })));
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()], { pageSize: 25 }));

      store.loadMembershipSettings();

      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 50 })));
      expect(store.membershipSettings()?.recordsPerPage).toBe(50);
    });

    it('requests the page size the account policy declared, not a hard-coded 10', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 25 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize'))
        .withContext('the size the tenant configured')
        .toBe('25');
      // The point of the specification, stated as an assertion rather than a comment: a
      // store carrying a constant would agree with the shared fallback here.
      expect(parameter(request, 'pageSize'))
        .withContext('a hard-coded size would equal the shared fallback')
        .not.toBe(String(DEFAULT_PAGE_SIZE));

      request.flush(pageFixture([listItemFixture()], { pageSize: 25 }));
    });

    it('requests a different declared page size, proving the value is genuinely read', () => {
      // The second size is what separates reading the value from pattern-matching one
      // particular number.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 50 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe('50');
      expect(store.effectivePageSize()).toBe(50);

      request.flush(pageFixture([listItemFixture()], { pageSize: 50 }));
    });

    it('falls back to the shared default size while no policy has been read', () => {
      expect(store.membershipSettings()).toBeNull();

      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe(String(DEFAULT_PAGE_SIZE));
      expect(store.effectivePageSize()).toBe(DEFAULT_PAGE_SIZE);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('still lists the accounts at the fallback size when the policy cannot be read', () => {
      // A tenant whose policy is unavailable still has accounts. The failure is recorded rather than
      // swallowed, and the listing goes out regardless - refusing to list would be a worse answer than
      // listing at the default beside a reported failure.
      store.initialise();

      expectRequest('GET', SETTINGS_URL).flush(problemFixture({ status: 500, title: 'Server' }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe(String(DEFAULT_PAGE_SIZE));
      expect(recordedFailure().operation)
        .withContext('the policy failure survives the listing that followed it')
        .toBe('loadMembershipSettings');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('passes a declared size on unclamped, leaving the bound to the server', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 500 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe('500');

      request.flush(pageFixture([]));
    });

    it('reports the size the server applied separately from the size it asked for', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 25 })));
      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture()], { pageSize: 20, totalCount: 40 }),
      );

      expect(store.effectivePageSize())
        .withContext('what the tenant configured, and what was asked for')
        .toBe(25);
      expect(store.appliedPageSize())
        .withContext('what the server actually applied')
        .toBe(20);
    });

    it('re-requests the listing after the policy is written, at the new size', () => {
      openListingAtPageSize(25);

      store.saveMembershipSettings(storedSettingsFixture({ recordsPerPage: 50 }));

      const write = expectRequest('PUT', SETTINGS_URL);
      expect(write.request.body).toEqual(storedSettingsFixture({ recordsPerPage: 50 }));
      write.flush(settingsWriteEnvelope());

      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 50 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize'))
        .withContext('the page in hand was fetched at the previous size')
        .toBe('50');

      request.flush(pageFixture([listItemFixture()], { pageSize: 50 }));
    });

    it('publishes what the policy write did to the tenant\'s display names', () => {
      // ⚠ THE ONE SETTINGS WRITE IN THIS WORKSPACE WITH A TENANT-WIDE SIDE EFFECT. Adopting a new
      // display-name format recomposes every account's stored display name, and the caller cannot infer
      // from its own request that it happened or to how many accounts - so the report travels back on the
      // response and is kept here.
      openListingAtPageSize(25);

      expect(store.lastSettingsWrite())
        .withContext('nothing has been written yet')
        .toBeNull();

      store.saveMembershipSettings(storedSettingsFixture({ securityDisplayNameFormat: '[LASTNAME]' }));

      expectRequest('PUT', SETTINGS_URL).flush(
        settingsWriteEnvelope({ displayNameFormatChanged: true, displayNamesRewritten: 12 }),
      );
      expectRequest('GET', SETTINGS_URL).flush(
        envelope(storedSettingsFixture({ securityDisplayNameFormat: '[LASTNAME]' })),
      );
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()], { pageSize: 25 }));

      // ⚠ SURVIVES THE RE-READ THAT FOLLOWS THE WRITE. The re-read clears state belonging to a previous
      // answer, so the command publishes the report AFTER dispatching it; publishing first would discard
      // the very report it was meant to accompany.
      expect(store.lastSettingsWrite()).toEqual({
        displayNameFormatChanged: true,
        displayNamesRewritten: 12,
      });
      expect(store.failure()).toBeNull();
    });

    it('keeps a swept-but-unchanged report distinct from no sweep at all', () => {
      // The two are different answers and an operator is looking for the difference: a format left alone
      // reports false and zero because no sweep ran, while a format that changed on a tenant whose accounts
      // already read that way reports true and zero because the sweep ran and found nothing to alter.
      openListingAtPageSize(25);

      store.saveMembershipSettings(storedSettingsFixture({ securityDisplayNameFormat: '[LASTNAME]' }));
      expectRequest('PUT', SETTINGS_URL).flush(
        settingsWriteEnvelope({ displayNameFormatChanged: true, displayNamesRewritten: 0 }),
      );
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([], { pageSize: 25 }));

      expect(store.lastSettingsWrite()).toEqual({
        displayNameFormatChanged: true,
        displayNamesRewritten: 0,
      });

      store.saveMembershipSettings(storedSettingsFixture());
      expectRequest('PUT', SETTINGS_URL).flush(settingsWriteEnvelope());
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([], { pageSize: 25 }));

      expect(store.lastSettingsWrite()).toEqual({
        displayNameFormatChanged: false,
        displayNamesRewritten: 0,
      });
    });

    it('discards the report on request and on reset, and never publishes one for a refusal', () => {
      openListingAtPageSize(25);

      store.saveMembershipSettings(storedSettingsFixture({ securityDisplayNameFormat: '[LASTNAME]' }));
      expectRequest('PUT', SETTINGS_URL).flush(
        settingsWriteEnvelope({ displayNameFormatChanged: true, displayNamesRewritten: 4 }),
      );
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([], { pageSize: 25 }));

      store.clearSettingsWriteReport();
      expect(store.lastSettingsWrite())
        .withContext('dismissing a notice is not abandoning the screen, so nothing is re-read')
        .toBeNull();
      httpMock.expectNone(() => true);

      // A refused write publishes a failure and NO report.
      store.saveMembershipSettings(storedSettingsFixture({ securityDisplayNameFormat: '[USERNAME]' }));
      expectRequest('PUT', SETTINGS_URL).flush(
        { title: 'Bad Request', status: 400, type: 'urn:dnnmigration:error:user.display-name.too-long' },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(store.lastSettingsWrite()).toBeNull();
      expect(store.failure()).not.toBeNull();
      expect(store.saving()).toBeFalse();
    });

    it('publishes no credential policy of its own', () => {
      // Minimum length, the non-alphanumeric requirement and the address-uniqueness rule are server-side
      // options that never cross the boundary.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([]));

      const policy = store.membershipSettings();

      if (policy === null) {
        throw new Error('expected the account policy to have been read');
      }

      const members = Object.keys(policy);

      for (const member of members) {
        expect(/password|nonalphanumeric|uniqueemail|minrequired/i.test(member))
          .withContext(`"${member}" would put a credential rule on the client`)
          .toBe(false);
      }
    });
  });

  // =========================================================================
  // THE THREE PREFIX SEARCHES
  // =========================================================================

  // =========================================================================
  // THE TENANT'S OPENING-VIEW POLICY
  // =========================================================================

  describe('the opening view the tenant configured', () => {
    it('opens on every account when the tenant chose the unfiltered view', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 0 })));

      const listing = expectRequest('GET', USERS_URL);

      expectOmitted(listing, ['userName', 'email', 'profilePropertyName', 'profilePropertyValue']);
      expect(parameter(listing, 'pageIndex')).toBe('0');

      listing.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'all' });
    });

    it('opens on the first letter of the alphabet strip when the tenant chose that view', () => {
      // The letter is `A`, and its provenance is the resource value the legacy read the first character of:
      // `Users.ascx.resx` `Filter.Text` is "A,B,C,…,Z" and L502 kept `Substring(0, 1)`.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 1 })));

      const listing = expectSearch();

      expect(member(listing, 'userName')).toBe('A');
      expectMembersOmitted(listing, ['email', 'profilePropertyName', 'profilePropertyValue']);
      // A body carries a NUMBER where a query string could only carry text, so the page coordinate
      // is read as one rather than through the text-only member reader.
      expect(body(listing)['pageIndex']).toBe(0);

      listing.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'username', text: 'A' });
    });

    it('issues no listing query at all when the tenant chose the no-query view', () => {
      // ⚠ NOT A DEFECT AND NOT WORKED AROUND. This is the deliberate choice a large tenant makes so that
      // opening the screen does not page a hundred thousand accounts, and it is also the DEFAULT the legacy
      // applied when the setting was absent, which the server reproduces.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 2 })));

      httpMock.expectNone(() => true);

      expect(store.search()).toEqual({ mode: 'none' });
      expect(store.usersLoading())
        .withContext('nothing may spin for a request that will never be made')
        .toBeFalse();
      expect(store.userRows()).toEqual([]);

      // And the operator's own action still works from that state.
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));
      expect(store.userRows().length).toBe(1);
    });

    it('opens on every account for a mode it does not recognise, and for an unreadable policy', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 99 })));

      const unrecognised = expectRequest('GET', USERS_URL);

      expectOmitted(unrecognised, ['userName', 'email', 'profilePropertyName', 'profilePropertyValue']);
      unrecognised.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'all' });

      // An UNREADABLE policy is a different situation from an absent key, and is answered differently on
      // purpose.
      store.reset();
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(
        { title: 'Server Error', status: 500, type: 'urn:dnnmigration:error:server.error' },
        { status: 500, statusText: 'Internal Server Error' },
      );

      const fallback = expectRequest('GET', USERS_URL);

      expectOmitted(fallback, ['userName', 'email', 'profilePropertyName', 'profilePropertyValue']);
      fallback.flush(pageFixture([listItemFixture()]));

      expect(store.search()).toEqual({ mode: 'all' });
      expect(store.failure())
        .withContext('the policy failure is still recorded, so nothing is concealed')
        .not.toBeNull();
      expect(store.membershipSettingsUnconfigured())
        .withContext('a failed read is not an absent policy')
        .toBeFalse();
    });

    it('publishes an unstored policy from a SUCCESSFUL read, without recording a failure', () => {
      // ⚠ #5/#6 — THE ANSWER ARRIVES AS A SUCCESS AND THE ABSENCE IS INSIDE IT. A tenant with no "User
      // Accounts" module instance is answered `200` carrying the measured legacy defaults with `isStored:
      // false`; it is NOT answered `404`, and it has not been since the read stopped reporting absence on
      // the status line.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(unstoredSettingsFixture()));

      expect(store.membershipSettingsUnconfigured())
        .withContext('the provenance is published, so a screen can explain it')
        .toBeTrue();
      expect(store.failure())
        .withContext('and it is NOT a failure, so no screen raises an error over it')
        .toBeNull();
      const policy = store.membershipSettings();

      expect(policy).not.toBeNull();
      expect(policy!.isStored).toBeFalse();
      expect(policy!.recordsPerPage)
        .withContext('the measured legacy default for an absent key')
        .toBe(10);
      expect(store.users().items.length)
        .withContext('the no-query opening view issues no listing request at all')
        .toBe(0);
      expect(store.search()).toEqual({ mode: 'none' });
    });

    it('clears the unstored state once a later read carries a stored policy', () => {
      // A tenant can GAIN an account module, so the flag must not latch. Asserted because the reset
      // path alone would leave it standing for the life of a mounted session.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(unstoredSettingsFixture()));

      expect(store.membershipSettingsUnconfigured()).toBeTrue();

      store.loadMembershipSettings();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({})));

      expect(store.membershipSettingsUnconfigured()).toBeFalse();
      expect(store.membershipSettings()).not.toBeNull();
      expect(store.membershipSettings()!.isStored).toBeTrue();
      // A standalone policy re-read does not chain the listing - only arrival does - so the opening view
      // the first read settled on is left exactly as it was. `httpMock.verify()` in the teardown proves no
      // listing was requested by either read.
      expect(store.search()).toEqual({ mode: 'none' });
    });

    it('reports a refused policy read as a failure, and never as an unstored policy', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(
        { title: 'Not Found', status: 404, type: 'urn:dnnmigration:error:resource.not_found' },
        { status: 404, statusText: 'Not Found' },
      );

      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      expect(store.membershipSettingsUnconfigured())
        .withContext('a refused read is not an unstored policy')
        .toBeFalse();

      const failure = store.failure();

      expect(failure).not.toBeNull();
      expect(failure!.operation).toBe('loadMembershipSettings');
      expect(failure!.problem?.status).toBe(404);
      expect(store.membershipSettings())
        .withContext('nothing is invented for a policy that could not be read')
        .toBeNull();
      expect(store.users().items.length).toBe(1);
      expect(store.search()).toEqual({ mode: 'all' });
    });

    it('leaves a search already chosen alone when the policy arrives', () => {
      // The policy decides how the screen OPENS, not what it shows after an operator has asked for
      // something. A policy read that landed after a search would otherwise discard the search.
      store.searchByEmail('ada');
      expectSearch().flush(pageFixture([listItemFixture()]));

      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 0 })));

      const listing = expectSearch();

      expect(member(listing, 'email')).toBe('ada');
      listing.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'email', text: 'ada' });
    });
  });

  describe('search modes', () => {
    it('transmits an account-name search verbatim, with no wildcard of its own', () => {
      store.searchByUsername('abc');

      // ⚠ A BODY, NOT A QUERY STRING — see {@link USERS_SEARCH_URL}. A searched account name names
      // a person, and a request target is recorded by the browser, by every proxy and by the server.
      const request = expectSearch();
      const transmitted = member(request, 'userName');

      expect(transmitted).toBe('abc');
      expect(transmitted).not.toContain('%');
      expectMembersOmitted(request, ['email', 'profilePropertyName', 'profilePropertyValue']);
      expectTargetCarriesNoneOf(request, ['abc']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('transmits an address search verbatim, with no wildcard of its own', () => {
      store.searchByEmail('ann@');

      const request = expectSearch();
      const transmitted = member(request, 'email');

      expect(transmitted).toBe('ann@');
      expect(transmitted).not.toContain('%');
      expectMembersOmitted(request, ['userName', 'profilePropertyName', 'profilePropertyValue']);
      expectTargetCarriesNoneOf(request, ['ann@', 'ann']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('transmits a named profile property alongside its text', () => {
      store.searchByProfileProperty('Nickname', 'Ann');

      const request = expectSearch();

      expect(member(request, 'profilePropertyName')).toBe('Nickname');
      expect(member(request, 'profilePropertyValue')).toBe('Ann');
      expect(member(request, 'profilePropertyValue')).not.toContain('%');
      expectMembersOmitted(request, ['userName', 'email']);

      // ⚠ BOTH HALVES ARE ARBITRARY TENANT DATA, which is what makes this the sharpest case: the tenant
      // declares its own properties, so the name discloses what it collects about its members and the value
      // may be anything at all, up to a national identifier.
      expectTargetCarriesNoneOf(request, ['Nickname', 'Ann']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('accepts an unfamiliar profile property name without validating it', () => {
      // The property name is an OPEN SET. A tenant declares whatever properties it likes, so an
      // unrecognised name is the server's to refuse - not this store's to reject, normalise or check
      // against a fixed list.
      const unusual = 'Preferred Pronoun (optional)';

      store.searchByProfileProperty(unusual, 'they');

      const request = expectSearch();

      expect(member(request, 'profilePropertyName')).toBe(unusual);
      expectTargetCarriesNoneOf(request, [unusual]);

      request.flush(pageFixture([]));
    });

    it('does not case-fold a profile property name', () => {
      // Two declared names are free to differ from one another only in case, so folding
      // would make one of them unreachable.
      const mixedCase = 'nIcKnAmE';

      store.searchByProfileProperty(mixedCase, 'x');

      const request = expectSearch();

      expect(member(request, 'profilePropertyName')).toBe(mixedCase);
      expect(member(request, 'profilePropertyName')).not.toBe(mixedCase.toLowerCase());

      request.flush(pageFixture([]));
    });

    it('neither trims nor case-folds the searched text', () => {
      // Trimming would make a leading space unsearchable and case-folding would presume
      // a collation this side does not know.
      const typed = '  MiXeD Case  ';

      store.searchByUsername(typed);

      const request = expectSearch();
      const transmitted = member(request, 'userName');

      expect(transmitted).toBe(typed);
      expect(transmitted).not.toBe(typed.trim());
      expect(transmitted).not.toBe(typed.toLowerCase());
      expect(transmitted).not.toContain('%');

      request.flush(pageFixture([]));
    });

    it('transmits empty search text as a value rather than dropping it', () => {
      store.searchByUsername('');

      const request = expectSearch();

      expect(Object.prototype.hasOwnProperty.call(body(request), 'userName'))
        .withContext('empty text is a value, not an absence')
        .toBe(true);
      expect(member(request, 'userName')).toBe('');

      request.flush(pageFixture([]));
    });

    it('publishes the search it applied as a typed discriminator', () => {
      store.searchByProfileProperty('Nickname', 'Ann');
      expectSearch().flush(pageFixture([]));

      expect(store.searchMode()).toBe('profileProperty');

      const applied = store.search();

      if (applied.mode !== 'profileProperty') {
        throw new Error('expected the profile-property search to have been applied');
      }

      expect(applied.propertyName).toBe('Nickname');
      expect(applied.text).toBe('Ann');
    });

    it('offers the declared property names of the tenant as the open set', () => {
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([
          definitionFixture({ propertyDefinitionId: 0, propertyName: 'Nickname' }),
          definitionFixture({ propertyDefinitionId: 4, propertyName: 'City' }),
        ]),
      );

      expect(store.profilePropertyNames()).toEqual(['Nickname', 'City']);
      expect(store.hasProfileDefinitions()).toBeTrue();
    });
  });

  // =========================================================================
  // ABSENCE IS OMISSION, AND THE FOURTH LEGACY BRANCH
  // =========================================================================

  describe('search omission and the unfiltered listing', () => {
    it('issues no request at all while no search has been chosen', () => {
      expect(store.searchMode()).toBe('none');

      store.loadUsers();

      httpMock.expectNone((request) => request.url === USERS_URL);
      expect(store.usersLoading())
        .withContext('nothing is in flight, so nothing should be reported as loading')
        .toBeFalse();
    });

    it('omits every filter parameter for the unfiltered listing', () => {
      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expectOmitted(request, FILTER_PARAMETERS);
      // The page coordinates are still sent: this branch is paged, unlike the two that
      // were dropped.
      expect(request.request.params.has('pageIndex')).toBeTrue();
      expect(request.request.params.has('pageSize')).toBeTrue();

      request.flush(pageFixture([listItemFixture()]));
    });

    it('never transmits the legacy no-search reserved word', () => {
      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expect(request.request.urlWithParams)
        .withContext('the reserved word is a legacy sentinel, never a transmitted value')
        .not.toContain(NO_SEARCH_RESERVED_WORD);

      request.flush(pageFixture([]));
    });

    it('never transmits the reserved word as a search term either', () => {
      store.searchByUsername(NO_SEARCH_RESERVED_WORD);

      const request = expectSearch();

      expect(member(request, 'userName'))
        .withContext('a person may legitimately search for this word')
        .toBe(NO_SEARCH_RESERVED_WORD);

      request.flush(pageFixture([]));
    });

    it('distinguishes the unfiltered listing from the no-query state', () => {
      // The distinction is real and is preserved: one dispatches a request that matches
      // everything, the other dispatches nothing at all.
      expect(store.searchMode()).toBe('none');
      store.loadUsers();
      httpMock.expectNone((request) => request.url === USERS_URL);

      store.showAllAccounts();
      expect(store.searchMode()).toBe('all');
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.reset();
      expect(store.searchMode())
        .withContext('the no-query state is reachable deliberately, through a reset')
        .toBe('none');

      store.loadUsers();
      httpMock.expectNone((request) => request.url === USERS_URL);
    });

    it('resolves a cleared search to the unfiltered listing rather than to silence', () => {
      // Clearing a filter on a listing screen means "show me everything", not "show me
      // nothing".
      store.searchByUsername('ann');
      expectSearch().flush(pageFixture([listItemFixture()]));

      store.clearSearch();

      const request = expectRequest('GET', USERS_URL);

      expect(store.searchMode()).toBe('all');
      expectOmitted(request, FILTER_PARAMETERS);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('omits the approval restriction until one is chosen, and sends false as false', () => {
      store.showAllAccounts();

      const unrestricted = expectRequest('GET', USERS_URL);
      expectOmitted(unrestricted, ['isApproved']);
      unrestricted.flush(pageFixture([listItemFixture()]));

      store.setApprovalFilter(false);

      const restricted = expectRequest('GET', USERS_URL);
      expect(parameter(restricted, 'isApproved')).toBe('false');
      restricted.flush(pageFixture([]));
    });

    it('omits the ordering until one is chosen, and omits a direction without a field', () => {
      store.showAllAccounts();

      const unordered = expectRequest('GET', USERS_URL);
      expectOmitted(unordered, ['sortBy', 'sortDir']);
      unordered.flush(pageFixture([listItemFixture()]));

      // A direction without a field is meaningless, so it is not carried alone.
      store.setSortDirection('Descending');

      const directionOnly = expectRequest('GET', USERS_URL);
      expectOmitted(directionOnly, ['sortBy', 'sortDir']);
      directionOnly.flush(pageFixture([listItemFixture()]));

      store.setSortField('Username');

      const ordered = expectRequest('GET', USERS_URL);
      expect(parameter(ordered, 'sortBy')).toBe('Username');
      expect(parameter(ordered, 'sortDir')).toBe('Descending');
      ordered.flush(pageFixture([listItemFixture()]));
    });
  });

  // =========================================================================
  // THE TWO DROPPED LIST MODES
  // =========================================================================

  describe('dropped list modes', () => {
    /** Every search the store can apply, enumerated exhaustively. */
    const EVERY_SEARCH_MODE: Readonly<Record<UserSearchMode, true>> = {
      none: true,
      all: true,
      username: true,
      email: true,
      profileProperty: true,
    };

    /** The two legacy modes that are deliberately not carried forward. */
    const DROPPED = /unauthor|online/i;

    it('carries exactly five searches, and neither dropped mode is among them', () => {
      const names = Object.keys(EVERY_SEARCH_MODE);

      expect(names.length).toBe(5);

      for (const name of names) {
        expect(DROPPED.test(name))
          .withContext(`"${name}" would restore a dropped legacy list mode`)
          .toBe(false);
      }
    });

    it('exposes no command that would fetch either dropped mode', () => {
      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(DROPPED.test(name))
          .withContext(`the command "${name}" would restore a dropped legacy list mode`)
          .toBe(false);
      }
    });

    it('holds no slice of currently-signed-in accounts', () => {
      for (const name of Object.keys(store)) {
        expect(DROPPED.test(name))
          .withContext(`the slice "${name}" would restore a dropped legacy list mode`)
          .toBe(false);
      }
    });

    it('requests neither dropped mode from any endpoint', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.setApprovalFilter(false);
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture({ isApproved: false })]));

      // Counted rather than asserted through `expectNone`, which throws and therefore records no
      // expectation: the emptiness of what `match` returns is the claim itself. The predicate is scoped to
      // the dropped addresses alone, so the `verify()` in the teardown still guards the rest.
      expect(httpMock.match((request) => DROPPED.test(request.url)))
        .withContext('neither dropped mode is reached, on any endpoint')
        .toEqual([]);
    });

    it('answers the unauthorised view as a PAGED filter, not as the dropped mode', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.setApprovalFilter(false);

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'isApproved')).toBe('false');
      expect(request.request.params.has('pageIndex'))
        .withContext('the replacement is paged; the mode it replaces was not')
        .toBeTrue();
      expect(parameter(request, 'pageIndex'))
        .withContext('a narrowed match set is a new first page')
        .toBe('0');
      expect(request.request.params.has('pageSize')).toBeTrue();

      request.flush(pageFixture([listItemFixture({ isApproved: false })]));
    });

    it('dispatches nothing for an approval filter while no listing has been chosen', () => {
      expect(store.searchMode()).toBe('none');

      store.setApprovalFilter(false);

      httpMock.expectNone((request) => request.url === USERS_URL);
      expect(store.approvalFilter())
        .withContext('the choice is recorded even though nothing was dispatched for it')
        .toBeFalse();
    });
  });

  // =========================================================================
  // SENTINELS ARE DATA
  // =========================================================================

  describe('sentinels', () => {
    it('retains a tenant identifier of -1 exactly as it arrived', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture({ portalId: -1 })]));

      const [first] = store.userRows();

      expect(first.portalId).toBe(-1);
      expect(store.observedPortalId()).toBe(-1);
      expect(store.observedPortalId()).not.toBeUndefined();
    });

    it('retains a tenant identifier of 0 exactly as it arrived', () => {
      // The SECOND tenant carries zero, for the same reason.
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture({ portalId: 0 })]));

      const [first] = store.userRows();

      expect(first.portalId).toBe(0);
      expect(store.observedPortalId()).toBe(0);
      expect(store.observedPortalId()).not.toBeUndefined();
    });

    it('transmits no tenant identifier of its own', () => {
      // The API resolves one tenant per request from the host it was reached on, reconciled against the
      // alias table, so a tenant identifier sent from here would either be redundant or be a second,
      // disagreeing opinion about which tenant was meant.
      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expectOmitted(request, ['portalId']);
      expect(request.request.urlWithParams).not.toContain('portalId');

      request.flush(pageFixture([listItemFixture({ portalId: -1 })]));
    });

    it('reads one account ONCE when the same account is selected again', () => {
      // ⚠ THE DEFECT THIS GUARDS: three screens select the same account from their own route effects, and an
      // edit load was measured issuing the detail read TWICE. The second dispatch cancelled the first
      // mid-flight and then asked the server the identical question, so the work was doubled and the answer
      // was not improved.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.selectUser(7);
      store.selectUser(7);

      httpMock.expectNone(`${USERS_URL}/7`);
      expect(store.selectedUser()?.userId).toBe(7);
    });

    it('does not re-ask while the first read for that account is still outstanding', () => {
      // The in-flight case is the one that actually occurred: two effects ran before the first answer
      // arrived, so a held-value test alone would not have suppressed the second dispatch.
      store.selectUser(7);

      const first = httpMock.expectOne(`${USERS_URL}/7`);

      store.selectUser(7);

      httpMock.expectNone(`${USERS_URL}/7`);

      first.flush(envelope(detailFixture({ userId: 7 })));
      expect(store.selectedUser()?.userId).toBe(7);
    });

    it('reads the newly named account when the selection genuinely moves', () => {
      // The guard must suppress a REPEAT, never a change. Without this the previous two specifications could
      // be satisfied by a store that never read a second account at all.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.selectUser(8);
      expectRequest('GET', `${USERS_URL}/8`).flush(envelope(detailFixture({ userId: 8 })));

      expect(store.selectedUser()?.userId).toBe(8);
    });

    it('interpolates an identifier of 0 into a path without rewriting or skipping it', () => {
      store.selectUser(0);

      expectRequest('GET', `${USERS_URL}/0`).flush(envelope(detailFixture({ userId: 0 })));

      expect(store.selectedUserId()).toBe(0);
    });

    it('interpolates an identifier of -1 into a path without rewriting or skipping it', () => {
      store.selectUser(-1);

      expectRequest('GET', `${USERS_URL}/-1`).flush(envelope(detailFixture({ userId: -1 })));

      expect(store.selectedUserId()).toBe(-1);
    });

    it('retains a declaration identifier of 0, matching the zero-seeded key tables', () => {
      // Defensive symmetry.
      store.selectProfileDefinition(0);

      expectRequest('GET', `${DEFINITIONS_URL}/0`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 0, moduleDefId: 0 })),
      );

      expect(store.selectedPropertyDefinitionId()).toBe(0);

      const held = store.selectedProfileDefinition();

      if (held === null) {
        throw new Error('expected the selected declaration to have been read');
      }

      expect(held.propertyDefinitionId).toBe(0);
      expect(held.moduleDefId)
        .withContext('a zero module association is an association, not an absence')
        .toBe(0);
    });

    it('retains an empty string rather than turning it into an absence', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture({ displayName: '' })]));

      const [first] = store.userRows();

      expect(first.displayName).toBe('');
      expect(first.displayName).not.toBeNull();
    });

    it('keeps an empty string and a null distinguishable on the members that admit both', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture({ displayName: '', address: null, telephone: '' })]),
      );

      const [first] = store.userRows();

      expect(first.displayName).toBe('');
      expect(first.address).toBeNull();
      expect(first.telephone).toBe('');
    });

    it('retains a false boolean as data on every flag it carries', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(
        pageFixture([
          listItemFixture({
            isApproved: false,
            isSuperUser: false,
            isLockedOut: false,
            isOnline: false,
          }),
        ]),
      );

      const [first] = store.userRows();

      expect(first.isApproved).toBeFalse();
      expect(first.isSuperUser).toBeFalse();
      expect(first.isLockedOut).toBeFalse();
      expect(first.isOnline).toBeFalse();
    });

    it('reports a false credential obligation as false, not as unknown', () => {
      // Three states, deliberately: undefined means only that no account has been read,
      // and it is produced by the store rather than by the wire.
      expect(store.selectedUserMustChangePassword())
        .withContext('nothing has been read yet')
        .toBeUndefined();

      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(
        envelope(detailFixture({ mustChangePassword: false })),
      );

      const obligation = store.selectedUserMustChangePassword();

      expect(obligation).toBeFalse();
      expect(obligation).not.toBeUndefined();
    });

    it('retains the least representable date rather than turning it into a null', () => {
      const legacyNullDate = '0001-01-01T00:00:00';

      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture({ createdDate: legacyNullDate, lastLoginDate: null })]),
      );

      const [first] = store.userRows();

      expect(first.createdDate).toBe(legacyNullDate);
      expect(first.createdDate).not.toBeNull();
      expect(first.lastLoginDate)
        .withContext('a genuine null stays a null; the two are not normalised together')
        .toBeNull();
    });

    it('retains a zero-valued enumeration member as a value', () => {
      // The least-restrictive visibility really is zero, and the declared data type
      // really can be zero.
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([
          definitionFixture({
            propertyDefinitionId: 0,
            dataType: 0,
            visibility: PROFILE_VISIBILITY.allUsers,
            viewOrder: 0,
            length: 0,
          }),
        ]),
      );

      const [held] = store.profileDefinitions();

      expect(PROFILE_VISIBILITY.allUsers).toBe(0);
      expect(held.visibility).toBe(0);
      expect(held.dataType).toBe(0);
      expect(held.viewOrder).toBe(0);
      expect(held.length).toBe(0);
    });

    it('numbers the stored-credential representations from zero, and acts on none of them', () => {
      // The legacy installation ran with the reversible representation and a symmetric key committed to
      // source control. The vocabulary survives only so a legacy record can be READ; the target hashes
      // one-way, and no member of it is ever selected for a new credential.
      expect(PasswordFormat.Clear).toBe(0);
      expect(PasswordFormat.Hashed).toBe(1);
      expect(PasswordFormat.Encrypted).toBe(2);

      // No published slice carries the representation at all, so nothing here can act on
      // it even by accident.
      const surface = Object.getOwnPropertyNames(UserStore.prototype);

      for (const name of surface) {
        expect(/passwordformat/i.test(name)).toBe(false);
      }
    });

    it('expresses nothing-selected as undefined, never as 0 and never as -1', () => {
      expect(store.selectedUserId()).toBeUndefined();

      store.selectUser(-1);
      expectRequest('GET', `${USERS_URL}/-1`).flush(envelope(detailFixture({ userId: -1 })));
      expect(store.selectedUserId()).toBe(-1);

      store.clearSelectedUser();

      const cleared = store.selectedUserId();

      expect(cleared).toBeUndefined();
      expect(cleared).not.toBe(-1);
      expect(cleared).not.toBe(0);
      expect(store.selectedUser()).toBeNull();
    });
  });

  // =========================================================================
  // THE ACCOUNT-CREATION VOCABULARY
  // =========================================================================

  describe('the account-creation vocabulary', () => {
    it('succeeds at 13 and reserves 0 for the initial no-error-yet marker', () => {
      expect(UserCreateStatus.Success).toBe(13);
      expect(UserCreateStatus.Success).not.toBe(0);
      expect(UserCreateStatus.AddUser).toBe(0);
      expect(UserCreateStatus.AddUserToPortal)
        .withContext('also an operation marker rather than an error')
        .toBe(17);
    });

    it('keeps the three name failures as three distinct values', () => {
      // They look redundant and are not: merging or renaming any of them would change
      // the integers the legacy data records.
      expect(UserCreateStatus.UsernameAlreadyExists).toBe(1);
      expect(UserCreateStatus.DuplicateUserName).toBe(5);
      expect(UserCreateStatus.InvalidUserName).toBe(11);

      const values = [
        UserCreateStatus.UsernameAlreadyExists,
        UserCreateStatus.DuplicateUserName,
        UserCreateStatus.InvalidUserName,
      ];

      expect(new Set(values).size)
        .withContext('three distinct values, neither merged nor aliased')
        .toBe(3);
    });

    it('records a creation failure by its string code and never by an ordinal', () => {
      store.createUser(createRequestFixture());

      expectRequest('POST', USERS_URL).flush(
        problemFixture({
          type: `${FAILURE_TYPE}user.create.duplicate-username`,
          status: 409,
          title: 'Conflict',
        }),
        { status: 409, statusText: 'Conflict' },
      );

      const code = store.failureReasonCode();

      expect(typeof code)
        .withContext('a string, always - never a number')
        .toBe('string');
      // The shared reader folds a hyphen onto an underscore and lower-cases, exactly as
      // the server does, so a caller may key on either spelling.
      expect(code).toBe('user.create.duplicate_username');
      expect(recordedFailure().operation).toBe('createUser');
    });
  });

  // =========================================================================
  // THE ACCOUNT LIFECYCLE
  // =========================================================================

  describe('the account lifecycle', () => {
    it('reads one account over the relative collection path', () => {
      store.selectUser(7);

      const request = expectRequest('GET', `${USERS_URL}/7`);

      expect(request.request.method).toBe('GET');
      request.flush(envelope(detailFixture({ userId: 7 })));

      const held = store.selectedUser();

      if (held === null) {
        throw new Error('expected the selected account to have been read');
      }

      expect(held.userId).toBe(7);
      expect(store.selectedUserLoading()).toBeFalse();
    });

    it('records a null answer as a failure rather than holding it as an empty account', () => {
      store.selectUser(999);

      expectRequest('GET', `${USERS_URL}/999`).flush(envelope(null));

      expect(store.selectedUser()).toBeNull();
      expect(store.selectedUserLoading()).toBeFalse();
      expect(store.failure()?.operation).toBe('loadUser');
      expect(store.selectedUserId())
        .withContext('the selection stands so a retry addresses the account that was asked for')
        .toBe(999);
    });

    it('adopts a created account from the answer of the server, not from the request', () => {
      const request = createRequestFixture();

      store.createUser(request);

      const posted = expectRequest('POST', USERS_URL);

      expect(posted.request.body)
        .withContext('the body travels exactly as supplied')
        .toEqual(request);
      expect(store.saving()).toBeTrue();

      // A creation answers 201 with the account as recorded, including the identifier the
      // server issued and anything it defaulted.
      posted.flush(envelope(detailFixture({ userId: 91, displayName: 'New Account' })), {
        status: 201,
        statusText: 'Created',
      });

      expect(store.selectedUserId())
        .withContext('the identifier is the one the server issued')
        .toBe(91);

      const held = store.selectedUser();

      if (held === null) {
        throw new Error('expected the created account to have been adopted');
      }

      expect(held.userId).toBe(91);
      expect(store.saving()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('re-reads the listing after a creation rather than splicing a row in', () => {
      // MIGRATION: the legacy cache is not reproduced. Where the new row falls depends on an ordering this
      // side does not own, and the paging facts are the server's, so a locally spliced list would be right
      // only until it was not.
      openListingAtPageSize(25);

      store.createUser(createRequestFixture());
      expectRequest('POST', USERS_URL).flush(envelope(detailFixture({ userId: 91 })), {
        status: 201,
        statusText: 'Created',
      });

      const reread = expectRequest('GET', USERS_URL);

      expect(parameter(reread, 'pageSize'))
        .withContext('the re-read keeps the size the policy declared')
        .toBe('25');

      reread.flush(pageFixture([listItemFixture(), listItemFixture({ userId: 91 })], {
        pageSize: 25,
        totalCount: 2,
      }));

      expect(store.userRows().length).toBe(2);
    });

    it('replaces one account and adopts the written answer', () => {
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      const request = updateRequestFixture();

      store.updateUser(7, request);

      const written = expectRequest('PUT', `${USERS_URL}/7`);

      expect(written.request.body).toEqual(request);
      written.flush(envelope(detailFixture({ userId: 7, lastName: 'Administrator' })));

      const held = store.selectedUser();

      if (held === null) {
        throw new Error('expected the written account to have been adopted');
      }

      expect(held.lastName).toBe('Administrator');
    });

    it('does not replace the held selection when a different account is written', () => {
      // A listing screen can act on a row without having selected it, and re-reading in
      // that case would replace whichever account another pane was showing.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.updateUser(8, updateRequestFixture());
      expectRequest('PUT', `${USERS_URL}/8`).flush(
        envelope(detailFixture({ userId: 8, lastName: 'Other' })),
      );

      const held = store.selectedUser();

      if (held === null) {
        throw new Error('expected the original selection to have survived');
      }

      expect(held.userId).toBe(7);
      expect(held.lastName).toBe('Admin');
    });

    it('removes one account from a bodiless response and clears the selection', () => {
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.deleteUser(7);

      const removed = expectRequest('DELETE', `${USERS_URL}/7`);

      // A removal answers with no body at all, which is why the listing is re-read rather
      // than edited locally.
      removed.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.selectedUserId()).toBeUndefined();
      expect(store.selectedUser()).toBeNull();
      expect(store.saving()).toBeFalse();
    });

    it('exposes no bulk removal command', () => {
      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(/deleteall|deleteunauthor|purge|bulk/i.test(name))
          .withContext(`"${name}" would restore an unbounded removal`)
          .toBe(false);
      }
    });

    it('reports a refused creation as a warning rather than as a fault', () => {
      store.createUser(createRequestFixture());

      expectRequest('POST', USERS_URL).flush(problemFixture({ status: 403, title: 'Forbidden' }), {
        status: 403,
        statusText: 'Forbidden',
      });

      const failure = recordedFailure();

      expect(failure.operation).toBe('createUser');
      expect(failure.summary.severity).toBe('warning');
      expect(failure.summary.severity).not.toBe('error');
      expect(store.failureSeverity()).toBe('warning');
      expect(store.saving()).toBeFalse();
    });

    it('reports a refused update as a warning rather than as a fault', () => {
      store.updateUser(1, updateRequestFixture());

      expectRequest('PUT', `${USERS_URL}/1`).flush(
        problemFixture({ status: 403, title: 'Forbidden' }),
        { status: 403, statusText: 'Forbidden' },
      );

      const failure = recordedFailure();

      expect(failure.operation).toBe('updateUser');
      expect(failure.summary.severity).toBe('warning');
      expect(store.failureSeverity()).not.toBe('error');
    });

    it('reports a protected removal as a warning and surfaces the code verbatim', () => {
      store.deleteUser(1);

      expectRequest('DELETE', `${USERS_URL}/1`).flush(
        problemFixture({
          type: `${FAILURE_TYPE}user.delete.superuser-protected`,
          status: 403,
          title: 'Forbidden',
        }),
        { status: 403, statusText: 'Forbidden' },
      );

      const failure = recordedFailure();

      expect(failure.operation).toBe('deleteUser');
      expect(failure.summary.severity).toBe('warning');
      expect(store.failureReasonCode()).toBe('user.delete.superuser_protected');
    });

    it('clears the previous failure before dispatching the next command', () => {
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(problemFixture({ status: 404 }), {
        status: 404,
        statusText: 'Not Found',
      });

      expect(store.failure()).not.toBeNull();

      store.selectUser(8);

      expect(store.failure())
        .withContext('a retry must not show the previous attempt of a message')
        .toBeNull();

      expectRequest('GET', `${USERS_URL}/8`).flush(envelope(detailFixture({ userId: 8 })));
    });
  });

  // =========================================================================
  // CREDENTIALS
  // =========================================================================

  describe('credentials', () => {
    /** A credential request whose values are transparently synthetic. */
    const changeRequest = (): ChangePasswordRequest => ({
      operation: 'change',
      currentPassword: 'fake-current-not-a-credential',
      newPassword: 'fake-replacement-not-a-credential',
      confirmPassword: 'fake-replacement-not-a-credential',
    });

    it('completes a credential change from a response carrying no body', () => {
      // MEASURED DIVERGENCE: the plan named a replace verb here. The built transport POSTS to the
      // credential child of the account, matching the controller, and the response carries no body at all -
      // so the proof that matters is that the store completes without attempting to read one.
      store.changePassword(7, changeRequest());

      const request = expectRequest('POST', `${USERS_URL}/7/password`);

      expect(store.saving()).toBeTrue();
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.saving()).toBeFalse();
      expect(store.failure())
        .withContext('a bodiless success is a success')
        .toBeNull();
    });

    it('completes an administrative reset from a response carrying no body', () => {
      store.resetPassword(7, {
        operation: 'reset',
        currentPassword: null,
        newPassword: 'fake-replacement-not-a-credential',
        confirmPassword: 'fake-replacement-not-a-credential',
      });

      expectRequest('POST', `${USERS_URL}/7/password-reset`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.saving()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('re-reads the account after a credential change, when it is the selected one', () => {
      // A credential change moves the instant it was last changed and can clear the obligation to change
      // it, and the response carries no body, so nothing is assumed about either.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(
        envelope(detailFixture({ userId: 7, mustChangePassword: true })),
      );

      store.changePassword(7, changeRequest());
      expectRequest('POST', `${USERS_URL}/7/password`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expectRequest('GET', `${USERS_URL}/7`).flush(
        envelope(detailFixture({ userId: 7, mustChangePassword: false })),
      );

      expect(store.selectedUserMustChangePassword()).toBeFalse();
    });

    it('never writes a credential into any published slice', () => {
      // The point is laboured because the legacy arrangement made it necessary - the provider was
      // registered with a reversible format and retrieval switched on and the symmetric key that reversed
      // it was committed to source control in the clear at L89-L93, so anyone who could read the repository
      // could read every stored credential.
      const synthetic = 'fake-sentinel-value-that-must-not-be-retained';

      store.changePassword(7, {
        operation: 'change',
        currentPassword: synthetic,
        newPassword: `${synthetic}-2`,
        confirmPassword: `${synthetic}-2`,
      });

      const request = expectRequest('POST', `${USERS_URL}/7/password`);

      expect(JSON.stringify(request.request.body))
        .withContext('the request itself of course carries it - that is the whole point')
        .toContain(synthetic);

      request.flush(null, { status: 204, statusText: 'No Content' });

      // Every published slice, serialised together. A store that retained the request
      // anywhere - even to echo it back to a form - fails here.
      const published = JSON.stringify({
        users: store.users(),
        search: store.search(),
        selectedUser: store.selectedUser(),
        profile: store.profile(),
        membershipSettings: store.membershipSettings(),
        profileDefinitions: store.profileDefinitions(),
        selectedProfileDefinition: store.selectedProfileDefinition(),
        failure: store.failure(),
      });

      expect(published)
        .withContext('no slice may carry a credential, in any form')
        .not.toContain(synthetic);
    });

    it('exposes no credential-retrieval command, on the store or on its transport', () => {
      // There is deliberately no recover-it, remind-me or reveal-it command, and none could be written -
      // the transport exposes no method that returns a credential. Both surfaces are scanned, because a
      // store is only as constrained as the transport beneath it.
      const retrieval = /retriev|reveal|remind|recover|getpassword|readpassword|sendpassword/i;

      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(retrieval.test(name))
          .withContext(`the command "${name}" would restore credential retrieval`)
          .toBe(false);
      }

      for (const name of Object.getOwnPropertyNames(UserService.prototype)) {
        expect(retrieval.test(name))
          .withContext(`the transport method "${name}" would restore credential retrieval`)
          .toBe(false);
      }
    });

    it('obliges an account to change its credential without choosing one', () => {
      // Sets the obligation only: it does not choose, generate, transmit or return a credential. Only the
      // selected account is re-read - the obligation appears on no column of the listing, so re-reading the
      // listing would cost a request that could not change a rendered value.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.requirePasswordChange(7);

      const request = expectRequest('POST', `${USERS_URL}/7/require-password-change`);

      expect(request.request.body)
        .withContext('the account is the whole of the request')
        .toBeNull();
      request.flush(null, { status: 204, statusText: 'No Content' });

      expectRequest('GET', `${USERS_URL}/7`).flush(
        envelope(detailFixture({ userId: 7, mustChangePassword: true })),
      );

      expect(store.selectedUserMustChangePassword()).toBeTrue();
      httpMock.expectNone((probe) => probe.url === USERS_URL);
    });

    it('sets an approval state explicitly, transmitting false as false', () => {
      store.setApproval(7, false);

      const request = expectRequest('PUT', `${USERS_URL}/7/approval`);

      expect(parameter(request, 'isApproved')).toBe('false');
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.saving()).toBeFalse();
    });

    it('releases a locked-out account', () => {
      store.unlockUser(7);

      const request = expectRequest('POST', `${USERS_URL}/7/unlock`);

      expect(request.request.body).toBeNull();
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.saving()).toBeFalse();
    });
  });

  // =========================================================================
  // THE PROFILE
  // =========================================================================

  describe('the profile', () => {
    it('reads one profile and holds its values in the order the server returned them', () => {
      // A profile is a set of rows keyed by the declarations of the tenant, not a fixed field list. The
      // legacy shape declared nineteen members of which seventeen were hardcoded named fields, so anything
      // a tenant added was reachable only through a separate untyped collection.
      store.loadProfile(7);

      const request = expectRequest('GET', `${USERS_URL}/7/profile`);

      request.flush(envelope(profileFixture(7)));

      expect(store.profileValues().length).toBe(2);

      const [first, second] = store.profileValues();

      expect(first.definition.propertyName).toBe('Nickname');
      expect(second.definition.propertyName).toBe('City');
      expect(store.profileLoading()).toBeFalse();
    });

    it('reports an empty value set before any profile has been read', () => {
      expect(store.profile()).toBeNull();
      expect(store.profileValues()).toEqual([]);
    });

    it('re-reads a profile after writing it, because the write answers with no body', () => {
      // The server records the instant each value was last written, and a locally
      // assembled profile would carry no such instant or a wrong one.
      store.saveProfile(7, submissionFixture(7));

      const written = expectRequest('PUT', `${USERS_URL}/7/profile`);

      expect(written.request.body).toEqual(submissionFixture(7));
      written.flush(null, { status: 204, statusText: 'No Content' });

      expectRequest('GET', `${USERS_URL}/7/profile`).flush(envelope(profileFixture(7)));

      expect(store.profileValues().length).toBe(2);
      expect(store.saving()).toBeFalse();
    });

    it('retains an empty recorded profile value rather than treating it as unset', () => {
      store.loadProfile(7);
      expectRequest('GET', `${USERS_URL}/7/profile`).flush(envelope(profileFixture(7)));

      const [first] = store.profileValues();

      expect(first.propertyValue)
        .withContext('the empty string is the recorded value, not an absence')
        .toBe('');
      expect(first.lastUpdatedDate)
        .withContext('never written is a genuine null, and stays one')
        .toBeNull();
    });

    it('drops a held profile as soon as the selection changes', () => {
      // A screen must not be able to render the details of one account beside the profile
      // of another while the second is still arriving.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.loadProfile(7);
      expectRequest('GET', `${USERS_URL}/7/profile`).flush(envelope(profileFixture(7)));
      expect(store.profileValues().length).toBe(2);

      store.selectUser(8);

      expect(store.profile())
        .withContext('the profile of the previous account must not survive the change')
        .toBeNull();

      expectRequest('GET', `${USERS_URL}/8`).flush(envelope(detailFixture({ userId: 8 })));
    });
  });

  // =========================================================================
  // PROFILE DECLARATIONS - UNPAGED
  // =========================================================================

  describe('profile declarations (unpaged)', () => {
    it('reads the declarations with no paging, ordering or filter parameter at all', () => {
      store.loadProfileDefinitions();

      const request = expectRequest('GET', DEFINITIONS_URL);

      expectOmitted(request, PAGING_PARAMETERS);
      expect(request.request.urlWithParams)
        .withContext('no query string at all')
        .toBe(DEFINITIONS_URL);

      request.flush(envelope([definitionFixture()]));
    });

    it('holds no page index, page size or total for the declarations', () => {
      // The declarations arrive as a plain array. The paging coordinates on this store belong to the
      // account listing alone, and reading the declarations must not populate them.
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([
          definitionFixture({ propertyDefinitionId: 0 }),
          definitionFixture({ propertyDefinitionId: 4, propertyName: 'City' }),
        ]),
      );

      expect(store.profileDefinitions().length).toBe(2);
      expect(store.totalCount())
        .withContext('the listing envelope is untouched by a declaration read')
        .toBe(0);
      expect(store.appliedPageSize()).toBe(0);
      expect(store.requestedPageIndex()).toBe(0);
      expect(store.userRows()).toEqual([]);
    });

    it('does not re-sort the declarations, because their order is the server one', () => {
      // Position among siblings is a FIELD on the declaration and the ordering of the
      // server is the authority, so a locally applied sort would contradict it.
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([
          definitionFixture({ propertyDefinitionId: 4, propertyName: 'City', viewOrder: 9 }),
          definitionFixture({ propertyDefinitionId: 0, propertyName: 'Nickname', viewOrder: 1 }),
        ]),
      );

      expect(store.profilePropertyNames())
        .withContext('as received, even though the view orders are descending')
        .toEqual(['City', 'Nickname']);
    });

    it('addresses one declaration by its property-definition identifier', () => {
      // The spelling is load-bearing on both sides of the wire: the route constrains an integer under that
      // name and the contract spells its identity member the same way, so a near-miss produces a route that
      // does not match rather than a parameter that is quietly ignored.
      store.selectProfileDefinition(4);

      expectRequest('GET', `${DEFINITIONS_URL}/4`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 4, propertyName: 'City' })),
      );

      expect(store.selectedPropertyDefinitionId()).toBe(4);

      const held = store.selectedProfileDefinition();

      if (held === null) {
        throw new Error('expected the selected declaration to have been read');
      }

      expect(held.propertyDefinitionId).toBe(4);
    });

    it('changes ordering through the view-order field on a replace, not a move endpoint', () => {
      store.updateProfileDefinition(4, definitionWriteFixture({ viewOrder: 2 }));

      const written = expectRequest('PUT', `${DEFINITIONS_URL}/4`);

      expect(written.request.body).toEqual(definitionWriteFixture({ viewOrder: 2 }));
      written.flush(envelope(definitionFixture({ propertyDefinitionId: 4, viewOrder: 2 })));

      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([definitionFixture({ propertyDefinitionId: 4, viewOrder: 2 })]),
      );

      httpMock.expectNone((request) => request.url.includes('/move'));
      httpMock.expectNone((request) => request.url.includes('/reorder'));
    });

    it('exposes no ordering helper of any kind', () => {
      // Computing which positions to write - swapping a pair, renumbering after a drag is the business of
      // the feature, because only the feature knows the set it is looking at. This store writes the
      // position it is given.
      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(/moveup|movedown|reorder|swap/i.test(name))
          .withContext(`"${name}" would put an ordering decision in the store`)
          .toBe(false);
      }
    });

    it('re-reads the declarations after one is created rather than appending it', () => {
      store.createProfileDefinition({
        propertyName: 'Nickname',
        propertyCategory: 'Contact',
        dataType: 0,
        defaultValue: null,
        length: 0,
        required: false,
        validationExpression: null,
        viewOrder: 0,
        visible: true,
        moduleDefId: null,
      });

      expectRequest('POST', DEFINITIONS_URL).flush(
        envelope(definitionFixture({ propertyDefinitionId: 12 })),
        { status: 201, statusText: 'Created' },
      );

      expect(store.selectedPropertyDefinitionId()).toBe(12);

      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([definitionFixture({ propertyDefinitionId: 12 })]),
      );

      expect(store.profileDefinitions().length).toBe(1);
    });

    it('clears the selection when the selected declaration is removed', () => {
      store.selectProfileDefinition(4);
      expectRequest('GET', `${DEFINITIONS_URL}/4`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 4 })),
      );

      store.deleteProfileDefinition(4);
      expectRequest('DELETE', `${DEFINITIONS_URL}/4`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.selectedPropertyDefinitionId()).toBeUndefined();
      expect(store.selectedProfileDefinition()).toBeNull();

      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));
    });

  // PROFILE DECLARATIONS - THE STAGED BATCH
  // ⚠ WHAT THIS BLOCK GUARDS. A screen that applied N staged rows by issuing the per-row command N times
  // produced N CONCURRENT writes and up to N full catalogue re-reads - one per write, each firing on its
  // own completion - while the shared saving flag fell on the first write to land, leaving a second batch
  // startable on top of the first.

  describe('profile declarations (the staged batch)', () => {
    /** One staged row, addressing a declaration and carrying its replacement. */
    const edit = (
      propertyDefinitionId: number,
      overrides: Partial<UpdateProfilePropertyDefinitionRequest> = {},
    ): ProfileDefinitionEdit => ({
      propertyDefinitionId,
      request: definitionWriteFixture(overrides),
    });

    /**
     * Settles a whole batch, proving as it goes that ONE write is outstanding at a time and that the
     * catalogue has not been re-read yet.
     *
     * @param expected The declaration identifiers, in the order they must be written.
     * @param refuse The identifiers to answer with a refusal instead of a replacement.
     * @returns The bodies written, in order, so a caller can assert what travelled.
     */
    const settleBatch = (
      expected: readonly number[],
      refuse: readonly number[] = [],
    ): readonly unknown[] => {
      const bodies: unknown[] = [];

      for (const propertyDefinitionId of expected) {
        const written = expectRequest('PUT', `${DEFINITIONS_URL}/${propertyDefinitionId}`);

        httpMock.expectNone(
          (request) => request.method === 'PUT',
          'a second write must not be in flight beside this one',
        );
        httpMock.expectNone(
          (request) => request.method === 'GET' && request.url === DEFINITIONS_URL,
          'the catalogue must not be re-read until the batch has settled',
        );

        bodies.push(written.request.body);

        if (refuse.includes(propertyDefinitionId)) {
          written.flush(problemFixture({ status: 403 }), {
            status: 403,
            statusText: 'Forbidden',
          });
        } else {
          written.flush(envelope(definitionFixture({ propertyDefinitionId })));
        }
      }

      return bodies;
    };

    it('writes the staged rows one at a time, in the order supplied', () => {
      store.applyProfileDefinitionEdits([
        edit(4, { viewOrder: 0, propertyName: 'City' }),
        edit(7, { viewOrder: 1, propertyName: 'Region' }),
        edit(9, { viewOrder: 2, propertyName: 'Country' }),
      ]);

      const bodies = settleBatch([4, 7, 9]);

      expect(bodies).toEqual([
        definitionWriteFixture({ viewOrder: 0, propertyName: 'City' }),
        definitionWriteFixture({ viewOrder: 1, propertyName: 'Region' }),
        definitionWriteFixture({ viewOrder: 2, propertyName: 'Country' }),
      ]);

      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([definitionFixture({ propertyDefinitionId: 4 })]),
      );
    });

    it('re-reads the catalogue exactly ONCE, after the last row has settled', () => {
      // ⚠ THE FINDING THIS CLOSES. Three per-row commands re-read the whole catalogue three times, and the
      // third read raced the first two.
      store.applyProfileDefinitionEdits([edit(4), edit(7), edit(9)]);

      settleBatch([4, 7, 9]);

      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([
          definitionFixture({ propertyDefinitionId: 4 }),
          definitionFixture({ propertyDefinitionId: 7, propertyName: 'Region' }),
          definitionFixture({ propertyDefinitionId: 9, propertyName: 'Country' }),
        ]),
      );

      httpMock.expectNone(
        (request) => request.url === DEFINITIONS_URL,
        'one batch reads the catalogue once, whatever its length',
      );
      expect(store.profileDefinitions().length).toBe(3);
      expect(store.profileDefinitionsLoading()).toBeFalse();
    });

    it('holds the saving flag raised for the whole batch, and counts the rows down', () => {
      // ⚠ THE SECOND HALF OF THE FINDING. With per-row commands the flag fell on the FIRST completion, so a
      // form re-enabled itself while later rows were still travelling and a second Apply could be pressed
      // on top of the first.
      expect(store.saving()).toBeFalse();
      expect(store.profileDefinitionBatchRemaining()).toBe(0);

      store.applyProfileDefinitionEdits([edit(4), edit(7), edit(9)]);

      expect(store.saving()).toBeTrue();
      expect(store.profileDefinitionBatchRemaining()).toBe(3);

      expectRequest('PUT', `${DEFINITIONS_URL}/4`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 4 })),
      );

      expect(store.saving())
        .withContext('two rows are still to be written')
        .toBeTrue();
      expect(store.profileDefinitionBatchRemaining()).toBe(2);

      expectRequest('PUT', `${DEFINITIONS_URL}/7`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 7 })),
      );

      expect(store.saving()).toBeTrue();
      expect(store.profileDefinitionBatchRemaining()).toBe(1);

      expectRequest('PUT', `${DEFINITIONS_URL}/9`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 9 })),
      );

      expect(store.saving())
        .withContext('the batch has settled, so the form may re-enable itself')
        .toBeFalse();
      expect(store.profileDefinitionBatchRemaining()).toBe(0);

      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));
    });

    it('attempts every row after a refusal, and still reads the catalogue once', () => {
      // ⚠ THE BATCH IS NOT ATOMIC, AND ABANDONING THE REST WOULD STRAND WORK. The rows address different
      // declarations, so the server applies each on its own merits; a refusal of the middle row must not
      // cost the operator the row behind it.
      store.applyProfileDefinitionEdits([edit(4), edit(7), edit(9)]);

      settleBatch([4, 7, 9], [7]);

      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([
          definitionFixture({ propertyDefinitionId: 4 }),
          definitionFixture({ propertyDefinitionId: 9 }),
        ]),
      );

      const failure = recordedFailure();

      expect(failure.operation)
        .withContext('the batch is the command that failed, not the per-row write')
        .toBe('applyProfileDefinitionEdits');
      expect(failure.summary.severity)
        .withContext('a refusal is a warning, unlike a genuine fault')
        .toBe('warning');
      expect(store.saving()).toBeFalse();
      expect(store.profileDefinitionBatchRemaining()).toBe(0);
    });

    it('records the FIRST refusal when several rows are refused', () => {
      // The failure slot holds one document, and the first refusal is the one whose cause the operator has
      // to deal with. Recording whichever row happened to answer LAST would be an arbitrary choice
      // presented as a diagnosis.
      store.applyProfileDefinitionEdits([edit(4), edit(7), edit(9)]);

      expectRequest('PUT', `${DEFINITIONS_URL}/4`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 4 })),
      );

      expectRequest('PUT', `${DEFINITIONS_URL}/7`).flush(
        problemFixture({ status: 403, detail: 'The first refusal.' }),
        { status: 403, statusText: 'Forbidden' },
      );

      expectRequest('PUT', `${DEFINITIONS_URL}/9`).flush(
        problemFixture({ status: 409, detail: 'The second refusal.' }),
        { status: 409, statusText: 'Conflict' },
      );

      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));

      expect(recordedProblem().detail).toBe('The first refusal.');
      expect(recordedFailure().operation).toBe('applyProfileDefinitionEdits');
    });

    it('clears a previous failure when a batch starts, so a stale message cannot outlive it', () => {
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(problemFixture({ status: 500 }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.failure()).not.toBeNull();

      store.applyProfileDefinitionEdits([edit(4)]);

      expect(store.failure())
        .withContext('the message the operator is now acting on is the batch, not the read')
        .toBeNull();

      settleBatch([4]);
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));
    });

    it('refuses a second batch while one is running, and contacts the server not at all', () => {
      store.applyProfileDefinitionEdits([edit(4), edit(7)]);

      const first = expectRequest('PUT', `${DEFINITIONS_URL}/4`);

      store.applyProfileDefinitionEdits([edit(11), edit(12)]);

      httpMock.expectNone(
        (request) => request.method === 'PUT',
        'the second batch must add no write of its own',
      );
      expect(store.profileDefinitionBatchRemaining())
        .withContext('the count still describes the batch in hand')
        .toBe(2);

      first.flush(envelope(definitionFixture({ propertyDefinitionId: 4 })));

      settleBatch([7]);

      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));

      httpMock.expectNone(
        (request) => request.url.startsWith(`${DEFINITIONS_URL}/1`),
        'neither row of the refused batch was ever written',
      );
    });

    it('accepts a further batch once the one in hand has settled', () => {
      store.applyProfileDefinitionEdits([edit(4)]);
      settleBatch([4]);
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));

      store.applyProfileDefinitionEdits([edit(7)]);

      expect(store.profileDefinitionBatchRemaining())
        .withContext('the count was released, so the next batch is not refused')
        .toBe(1);

      settleBatch([7]);
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));
    });

    it('writes nothing at all for an empty batch, and does not re-read the catalogue', () => {
      store.applyProfileDefinitionEdits([]);

      expect(store.saving()).toBeFalse();
      expect(store.profileDefinitionBatchRemaining()).toBe(0);
      expect(store.failure()).toBeNull();

      httpMock.expectNone(() => true, 'an empty batch dispatches nothing whatsoever');
    });

    it('reconciles the selected declaration from the answer of the row that IS selected', () => {
      store.selectProfileDefinition(7);
      expectRequest('GET', `${DEFINITIONS_URL}/7`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 7, viewOrder: 0 })),
      );

      store.applyProfileDefinitionEdits([edit(4, { viewOrder: 9 }), edit(7, { viewOrder: 5 })]);

      expectRequest('PUT', `${DEFINITIONS_URL}/4`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 4, viewOrder: 9 })),
      );

      expect(store.selectedProfileDefinition()?.propertyDefinitionId)
        .withContext('an unselected row must not replace the selection')
        .toBe(7);
      expect(store.selectedProfileDefinition()?.viewOrder).toBe(0);

      expectRequest('PUT', `${DEFINITIONS_URL}/7`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 7, viewOrder: 5 })),
      );

      expect(store.selectedProfileDefinition()?.viewOrder)
        .withContext("reconciled from the server's own answer, not from the request")
        .toBe(5);

      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([definitionFixture({ propertyDefinitionId: 7, viewOrder: 5 })]),
      );
    });

    it('passes each identifier and body on exactly as supplied, sentinels included', () => {
      // SENTINELS ARE DATA on this contract: the declaration table seeds its key at zero, a zero-valued
      // module association is a real association and an empty default is a real default. A batch that
      // coerced any of them would rewrite the operator's intent.
      store.applyProfileDefinitionEdits([
        edit(0, { viewOrder: 0, length: 0, defaultValue: '', validationExpression: null }),
      ]);

      const written = expectRequest('PUT', `${DEFINITIONS_URL}/0`);

      expect(written.request.body).toEqual(
        definitionWriteFixture({
          viewOrder: 0,
          length: 0,
          defaultValue: '',
          validationExpression: null,
        }),
      );

      written.flush(envelope(definitionFixture({ propertyDefinitionId: 0 })));
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));
    });
  });
  });

  describe('concurrent writes', () => {
    function drainDefinitionReads(): void {
      for (const read of httpMock.match((request) => request.url === DEFINITIONS_URL)) {
        if (!read.cancelled) {
          read.flush(envelope([definitionFixture()]));
        }
      }
    }

    it('reports saving until the LAST of several concurrent writes settles', () => {
      store.updateProfileDefinition(0, definitionWriteFixture({ viewOrder: 1 }));
      store.updateProfileDefinition(4, definitionWriteFixture({ viewOrder: 2 }));
      store.updateProfileDefinition(9, definitionWriteFixture({ viewOrder: 3 }));

      const writes = httpMock.match(
        (request) => request.method === 'PUT' && request.url.startsWith(`${DEFINITIONS_URL}/`),
      );

      expect(writes.length).withContext('one write per changed row, dispatched together').toBe(3);
      expect(store.saving()).toBeTrue();

      writes[0].flush(envelope(definitionFixture({ propertyDefinitionId: 0, viewOrder: 1 })));

      // ⚠ THE ASSERTION THE BOOLEAN FAILED. Two requests are still on the wire.
      expect(store.saving())
        .withContext('the first response does not settle the batch')
        .toBeTrue();

      writes[1].flush(envelope(definitionFixture({ propertyDefinitionId: 4, viewOrder: 2 })));

      expect(store.saving())
        .withContext('nor does the second, while one remains')
        .toBeTrue();

      writes[2].flush(envelope(definitionFixture({ propertyDefinitionId: 9, viewOrder: 3 })));

      expect(store.saving())
        .withContext('and the last one does')
        .toBeFalse();

      drainDefinitionReads();
    });

    it('holds a refusal from early in a batch until the batch has settled', () => {
      // The consumer that matters reads the failure slot at the moment saving turns false, so a
      // refusal raised by the first response has to still be there when the last one lands.
      store.updateProfileDefinition(0, definitionWriteFixture({ required: true }));
      store.updateProfileDefinition(4, definitionWriteFixture({ required: true }));

      const writes = httpMock.match(
        (request) => request.method === 'PUT' && request.url.startsWith(`${DEFINITIONS_URL}/`),
      );

      writes[0].flush(problemFixture({ status: 409 }), { status: 409, statusText: 'Conflict' });

      const refusal = store.failure();

      expect(refusal).withContext('the refusal is recorded when it arrives').not.toBeNull();
      expect(refusal?.operation).toBe('updateProfileDefinition');
      expect(store.saving()).withContext('and the batch is still outstanding').toBeTrue();

      writes[1].flush(envelope(definitionFixture({ propertyDefinitionId: 4, required: true })));

      expect(store.saving()).toBeFalse();
      expect(store.failure())
        .withContext('a sibling write succeeding does not erase the refusal')
        .not.toBeNull();

      drainDefinitionReads();
    });

    it('does not let a write STARTING erase a refusal a sibling write already recorded', () => {
      store.updateProfileDefinition(0, definitionWriteFixture());
      store.updateProfileDefinition(4, definitionWriteFixture());

      const batch = httpMock.match(
        (request) => request.method === 'PUT' && request.url.startsWith(`${DEFINITIONS_URL}/`),
      );

      batch[0].flush(problemFixture({ status: 403 }), { status: 403, statusText: 'Forbidden' });
      expect(store.failure()).not.toBeNull();

      // A third write starts while the second is still outstanding.
      store.updateProfileDefinition(9, definitionWriteFixture());

      expect(store.failure())
        .withContext('the slot is cleared only by the first write of a batch')
        .not.toBeNull();

      const late = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === `${DEFINITIONS_URL}/9`,
      );

      batch[1].flush(envelope(definitionFixture({ propertyDefinitionId: 4 })));
      late.flush(envelope(definitionFixture({ propertyDefinitionId: 9 })));

      expect(store.saving()).toBeFalse();

      drainDefinitionReads();
    });

    it('clears the slot again for a write that starts with nothing outstanding', () => {
      // The behaviour a single write has always had, asserted so the rule above cannot be
      // mistaken for "the failure slot is never cleared".
      store.updateProfileDefinition(0, definitionWriteFixture());
      expectRequest('PUT', `${DEFINITIONS_URL}/0`).flush(problemFixture({ status: 403 }), {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(store.failure()).not.toBeNull();
      expect(store.saving()).toBeFalse();

      store.updateProfileDefinition(0, definitionWriteFixture());

      expect(store.failure())
        .withContext('a fresh attempt starts from a clean slot')
        .toBeNull();

      expectRequest('PUT', `${DEFINITIONS_URL}/0`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 0 })),
      );

      drainDefinitionReads();
    });

    it('zeroes the count when a session boundary releases the writes', () => {
      store.updateProfileDefinition(0, definitionWriteFixture());
      store.createProfileDefinition({
        propertyName: 'Nickname',
        propertyCategory: 'Contact',
        dataType: 0,
        defaultValue: null,
        length: 0,
        required: false,
        validationExpression: null,
        viewOrder: 0,
        visible: true,
        moduleDefId: null,
      });

      const outstanding = httpMock.match((request) => request.url.startsWith(DEFINITIONS_URL));

      expect(outstanding.length).toBe(2);
      expect(store.saving()).toBeTrue();

      store.reset();

      for (const request of outstanding) {
        expect(request.cancelled)
          .withContext('a write must not outlive the session that issued it')
          .toBeTrue();
      }

      expect(store.saving())
        .withContext('and the count goes with them')
        .toBeFalse();

      // The count is genuinely zero rather than merely reported as false: the next write moves it
      // off zero, which a stranded or negative count could not do.
      store.updateProfileDefinition(0, definitionWriteFixture());

      expect(store.saving()).toBeTrue();

      expectRequest('PUT', `${DEFINITIONS_URL}/0`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 0 })),
      );

      expect(store.saving()).toBeFalse();

      drainDefinitionReads();
    });

    it('counts writes across DIFFERENT commands, not per command', () => {
      store.updateUser(7, updateRequestFixture());
      store.updateProfileDefinition(0, definitionWriteFixture());

      const account = expectRequest('PUT', `${USERS_URL}/7`);
      const definition = expectRequest('PUT', `${DEFINITIONS_URL}/0`);

      expect(store.saving()).toBeTrue();

      account.flush(envelope(detailFixture({ userId: 7 })));

      expect(store.saving())
        .withContext('the declaration write is still outstanding')
        .toBeTrue();

      definition.flush(envelope(definitionFixture({ propertyDefinitionId: 0 })));

      expect(store.saving()).toBeFalse();

      httpMock.expectNone((request) => request.url === USERS_URL);
      drainDefinitionReads();
    });
  });

  describe('concurrent writes', () => {
    function drainDefinitionReads(): void {
      for (const read of httpMock.match((request) => request.url === DEFINITIONS_URL)) {
        if (!read.cancelled) {
          read.flush(envelope([definitionFixture()]));
        }
      }
    }

    it('reports saving until the LAST of several concurrent writes settles', () => {
      store.updateProfileDefinition(0, definitionWriteFixture({ viewOrder: 1 }));
      store.updateProfileDefinition(4, definitionWriteFixture({ viewOrder: 2 }));
      store.updateProfileDefinition(9, definitionWriteFixture({ viewOrder: 3 }));

      const writes = httpMock.match(
        (request) => request.method === 'PUT' && request.url.startsWith(`${DEFINITIONS_URL}/`),
      );

      expect(writes.length).withContext('one write per changed row, dispatched together').toBe(3);
      expect(store.saving()).toBeTrue();

      writes[0].flush(envelope(definitionFixture({ propertyDefinitionId: 0, viewOrder: 1 })));

      // ⚠ THE ASSERTION THE BOOLEAN FAILED. Two requests are still on the wire.
      expect(store.saving())
        .withContext('the first response does not settle the batch')
        .toBeTrue();

      writes[1].flush(envelope(definitionFixture({ propertyDefinitionId: 4, viewOrder: 2 })));

      expect(store.saving())
        .withContext('nor does the second, while one remains')
        .toBeTrue();

      writes[2].flush(envelope(definitionFixture({ propertyDefinitionId: 9, viewOrder: 3 })));

      expect(store.saving())
        .withContext('and the last one does')
        .toBeFalse();

      drainDefinitionReads();
    });

    it('holds a refusal from early in a batch until the batch has settled', () => {
      // The consumer that matters reads the failure slot at the moment saving turns false, so a
      // refusal raised by the first response has to still be there when the last one lands.
      store.updateProfileDefinition(0, definitionWriteFixture({ required: true }));
      store.updateProfileDefinition(4, definitionWriteFixture({ required: true }));

      const writes = httpMock.match(
        (request) => request.method === 'PUT' && request.url.startsWith(`${DEFINITIONS_URL}/`),
      );

      writes[0].flush(problemFixture({ status: 409 }), { status: 409, statusText: 'Conflict' });

      const refusal = store.failure();

      expect(refusal).withContext('the refusal is recorded when it arrives').not.toBeNull();
      expect(refusal?.operation).toBe('updateProfileDefinition');
      expect(store.saving()).withContext('and the batch is still outstanding').toBeTrue();

      writes[1].flush(envelope(definitionFixture({ propertyDefinitionId: 4, required: true })));

      expect(store.saving()).toBeFalse();
      expect(store.failure())
        .withContext('a sibling write succeeding does not erase the refusal')
        .not.toBeNull();

      drainDefinitionReads();
    });

    it('does not let a write STARTING erase a refusal a sibling write already recorded', () => {
      store.updateProfileDefinition(0, definitionWriteFixture());
      store.updateProfileDefinition(4, definitionWriteFixture());

      const batch = httpMock.match(
        (request) => request.method === 'PUT' && request.url.startsWith(`${DEFINITIONS_URL}/`),
      );

      batch[0].flush(problemFixture({ status: 403 }), { status: 403, statusText: 'Forbidden' });
      expect(store.failure()).not.toBeNull();

      // A third write starts while the second is still outstanding.
      store.updateProfileDefinition(9, definitionWriteFixture());

      expect(store.failure())
        .withContext('the slot is cleared only by the first write of a batch')
        .not.toBeNull();

      const late = httpMock.expectOne(
        (request) => request.method === 'PUT' && request.url === `${DEFINITIONS_URL}/9`,
      );

      batch[1].flush(envelope(definitionFixture({ propertyDefinitionId: 4 })));
      late.flush(envelope(definitionFixture({ propertyDefinitionId: 9 })));

      expect(store.saving()).toBeFalse();

      drainDefinitionReads();
    });

    it('clears the slot again for a write that starts with nothing outstanding', () => {
      // The behaviour a single write has always had, asserted so the rule above cannot be
      // mistaken for "the failure slot is never cleared".
      store.updateProfileDefinition(0, definitionWriteFixture());
      expectRequest('PUT', `${DEFINITIONS_URL}/0`).flush(problemFixture({ status: 403 }), {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(store.failure()).not.toBeNull();
      expect(store.saving()).toBeFalse();

      store.updateProfileDefinition(0, definitionWriteFixture());

      expect(store.failure())
        .withContext('a fresh attempt starts from a clean slot')
        .toBeNull();

      expectRequest('PUT', `${DEFINITIONS_URL}/0`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 0 })),
      );

      drainDefinitionReads();
    });

    it('zeroes the count when a session boundary releases the writes', () => {
      store.updateProfileDefinition(0, definitionWriteFixture());
      store.createProfileDefinition({
        propertyName: 'Nickname',
        propertyCategory: 'Contact',
        dataType: 0,
        defaultValue: null,
        length: 0,
        required: false,
        validationExpression: null,
        viewOrder: 0,
        visible: true,
        moduleDefId: null,
      });

      const outstanding = httpMock.match((request) => request.url.startsWith(DEFINITIONS_URL));

      expect(outstanding.length).toBe(2);
      expect(store.saving()).toBeTrue();

      store.reset();

      for (const request of outstanding) {
        expect(request.cancelled)
          .withContext('a write must not outlive the session that issued it')
          .toBeTrue();
      }

      expect(store.saving())
        .withContext('and the count goes with them')
        .toBeFalse();

      // The count is genuinely zero rather than merely reported as false: the next write moves it
      // off zero, which a stranded or negative count could not do.
      store.updateProfileDefinition(0, definitionWriteFixture());

      expect(store.saving()).toBeTrue();

      expectRequest('PUT', `${DEFINITIONS_URL}/0`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 0 })),
      );

      expect(store.saving()).toBeFalse();

      drainDefinitionReads();
    });

    it('counts writes across DIFFERENT commands, not per command', () => {
      store.updateUser(7, updateRequestFixture());
      store.updateProfileDefinition(0, definitionWriteFixture());

      const account = expectRequest('PUT', `${USERS_URL}/7`);
      const definition = expectRequest('PUT', `${DEFINITIONS_URL}/0`);

      expect(store.saving()).toBeTrue();

      account.flush(envelope(detailFixture({ userId: 7 })));

      expect(store.saving())
        .withContext('the declaration write is still outstanding')
        .toBeTrue();

      definition.flush(envelope(definitionFixture({ propertyDefinitionId: 0 })));

      expect(store.saving()).toBeFalse();

      httpMock.expectNone((request) => request.url === USERS_URL);
      drainDefinitionReads();
    });
  });

  // =========================================================================
  // FAILURES
  // =========================================================================

  // THE OPENING LISTING IS THE TENANT'S POLICY DECISION
  // All (0) list every account, paged and unfiltered FirstLetter (1) open on the first letter, so a large
  // tenant does not render thousands of rows None (2) list NOTHING and wait to be asked.
  describe('the opening listing follows the tenant policy', () => {
    it('lists everything when the policy says All', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 0 })));

      const request = expectRequest('GET', USERS_URL);

      // No filter member of any kind: the unfiltered listing is the absence of one, never a reserved word
      // transmitted as a filter.
      expectOmitted(request, ['userName', 'email']);
      expect(store.searchMode()).toBe('all');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('opens on the FIRST LETTER when the policy says FirstLetter', () => {
      // ⚠ A BRANCH THAT IS EASILY LOST IN SILENCE: dispatching the unfiltered listing here leaves this
      // assertion found no account-name filter at all. ⚠ THE OPENING READ IS THE BODY-BOUND SEARCH, NOT THE
      // UNFILTERED LISTING, BECAUSE A LETTER ON THE ACCOUNT-NAME AXIS NAMES PEOPLE. The transport is chosen
      // by whether the query identifies anybody — see {@link USERS_SEARCH_URL} — and a first-letter view IS
      // an account-name filter, so it travels in a request body like every other name search rather than
      // putting the axis and its value in a request target that four separate recorders keep.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 1 })));

      const request = expectSearch();

      expect(member(request, 'userName'))
        .withContext('the legacy opened its alphabet strip on A')
        .toBe('A');
      expect(store.searchMode()).toBe('username');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('lists NOTHING when the policy says None, and dispatches no request at all', () => {
      // ⚠ THE POLICY OVERRIDE, ASSERTED DIRECTLY. Absence of a request is the assertion: a tenant
      // that has chosen not to publish its roster must not have it published by the screen opening.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 2 })));

      httpMock.expectNone((request) => request.url === USERS_URL);

      expect(store.searchMode())
        .withContext('the no-query state is retained rather than promoted')
        .toBe('none');
      expect(store.users().items.length).toBe(0);
    });

    it('still lets an operator ASK, on a None tenant', () => {
      // Withholding the opening listing is not withholding the screen. The policy governs what
      // appears unbidden, and an explicit command is bidden.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 2 })));
      httpMock.expectNone((request) => request.url === USERS_URL);

      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expect(store.searchMode()).toBe('all');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('LISTS THE ACCOUNTS when the policy cannot be read, and does not read a refusal as None', () => {
      // ⚠ THE CONTRACT THAT MUST NOT REGRESS. Withholding the roster is a CHOICE a tenant makes, expressed
      // as the no-query display mode; a read that failed is not that choice, so an unreadable policy falls
      // back to the listing rather than to silence — otherwise one refused request would make a tenant's
      // accounts unreachable.
      store.initialise();

      expectRequest('GET', SETTINGS_URL).flush(problemFixture({ status: 404, title: 'Not Found' }), {
        status: 404,
        statusText: 'Not Found',
      });

      const request = expectRequest('GET', USERS_URL);

      expect(store.membershipSettings()).toBeNull();
      expect(store.searchMode()).toBe('all');

      request.flush(pageFixture([listItemFixture()]));

      expect(store.users().items.length).toBe(1);
    });

    it('falls back to the listing for an UNRECOGNISED mode rather than to silence', () => {
      // The contract declares this member as a plain integer validated against no closed set, so an unknown
      // value is reachable. Treating one as "withhold everything" would let a single unrecognised integer
      // make a tenant's accounts unreachable; the listing is recoverable.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 99 })));

      const request = expectRequest('GET', USERS_URL);

      expect(store.searchMode()).toBe('all');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('leaves a search ALREADY CHOSEN exactly as it is, whatever the policy says', () => {
      // The policy decides the OPENING state and nothing else. A caller that has already narrowed
      // must not have its narrowing replaced by a policy default.
      store.searchByEmail('a@example.test');
      expectSearch().flush(pageFixture([listItemFixture()]));

      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 1 })));

      // An address search is body-bound for the same reason a name search is, so the re-read the
      // policy triggers is the search endpoint on both sides of the policy arriving.
      const request = expectSearch();

      expect(member(request, 'email')).toBe('a@example.test');
      expectMembersOmitted(request, ['userName']);
      expect(store.searchMode()).toBe('email');

      request.flush(pageFixture([listItemFixture()]));
    });
  });

  describe('concurrent writes settle independently', () => {
    it('stays saving until the LAST of several writes settles', () => {
      // ⚠ THE DEFECT, EXPRESSED AS A TEST. With a boolean the first flush below took `saving` to
      // false and this assertion failed on the very next line.
      store.updateProfileDefinition(1, definitionWriteFixture());
      store.updateProfileDefinition(2, definitionWriteFixture());
      store.updateProfileDefinition(3, definitionWriteFixture());

      expect(store.writesInFlight()).toBe(3);
      expect(store.saving()).toBeTrue();

      const writes = httpMock.match(
        (candidate) => candidate.method === 'PUT' && candidate.url.includes('/profile-definitions/'),
      );

      expect(writes.length).toBe(3);

      writes[0].flush(envelope(definitionFixture({ propertyDefinitionId: 1 })));

      expect(store.writesInFlight()).withContext('two are still outstanding').toBe(2);
      expect(store.saving())
        .withContext('one write finishing does not mean the batch finished')
        .toBeTrue();

      writes[1].flush(envelope(definitionFixture({ propertyDefinitionId: 2 })));

      expect(store.saving()).toBeTrue();

      writes[2].flush(envelope(definitionFixture({ propertyDefinitionId: 3 })));

      expect(store.writesInFlight()).toBe(0);
      expect(store.saving()).withContext('and now the batch has finished').toBeFalse();

      // Each write re-reads the declarations, and every re-read is answered so the backend
      // verification at teardown is satisfied.
      for (const reread of httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
      )) {
        if (!reread.cancelled) {
          reread.flush(envelope([definitionFixture()]));
        }
      }
    });

    it('counts a FAILED write down as well as a successful one', () => {
      // Settlement is settlement. A failure that did not decrement would leave the store claiming
      // to be saving for the rest of the session and disable every form on it.
      store.updateProfileDefinition(1, definitionWriteFixture());
      store.updateProfileDefinition(2, definitionWriteFixture());

      const writes = httpMock.match(
        (candidate) => candidate.method === 'PUT' && candidate.url.includes('/profile-definitions/'),
      );

      writes[0].flush(problemFixture({ status: 500, title: 'Server' }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.writesInFlight()).toBe(1);
      expect(store.saving()).toBeTrue();

      writes[1].flush(problemFixture({ status: 500, title: 'Server' }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.writesInFlight()).toBe(0);
      expect(store.saving()).toBeFalse();
    });

    it('mixes a success and a failure without either settling the other', () => {
      store.updateProfileDefinition(1, definitionWriteFixture());
      store.updateProfileDefinition(2, definitionWriteFixture());

      const writes = httpMock.match(
        (candidate) => candidate.method === 'PUT' && candidate.url.includes('/profile-definitions/'),
      );

      writes[0].flush(envelope(definitionFixture({ propertyDefinitionId: 1 })));

      expect(store.saving())
        .withContext('a success does not settle the sibling that is still in flight')
        .toBeTrue();

      writes[1].flush(problemFixture({ status: 409, title: 'Conflict' }), {
        status: 409,
        statusText: 'Conflict',
      });

      expect(store.saving()).toBeFalse();
      expect(store.failure()).not.toBeNull();

      for (const reread of httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
      )) {
        if (!reread.cancelled) {
          reread.flush(envelope([definitionFixture()]));
        }
      }
    });

    it('ZEROES the count on reset rather than decrementing it', () => {
      store.updateProfileDefinition(1, definitionWriteFixture());
      store.updateProfileDefinition(2, definitionWriteFixture());
      store.updateProfileDefinition(3, definitionWriteFixture());

      const writes = httpMock.match(
        (candidate) => candidate.method === 'PUT' && candidate.url.includes('/profile-definitions/'),
      );

      expect(writes.length).toBe(3);
      expect(store.writesInFlight()).toBe(3);

      store.reset();

      expect(store.writesInFlight()).toBe(0);
      expect(store.saving()).toBeFalse();

      // The handles were released, so every one of them is abandoned rather than merely ignored —
      // which is what makes the zeroing correct: none of them can ever reach a settle call.
      for (const write of writes) {
        expect(write.cancelled).toBeTrue();
      }
    });

    it('keeps accepting writes after a reset, at an honest count', () => {
      store.updateProfileDefinition(1, definitionWriteFixture());

      const abandoned = httpMock.expectOne(
        (candidate) => candidate.method === 'PUT' && candidate.url.endsWith('/profile-definitions/1'),
      );

      store.reset();

      expect(abandoned.cancelled).toBeTrue();

      store.updateProfileDefinition(2, definitionWriteFixture());

      expect(store.writesInFlight())
        .withContext('one write, counted once — not zero and not two')
        .toBe(1);

      const write = httpMock.expectOne(
        (candidate) => candidate.method === 'PUT' && candidate.url.endsWith('/profile-definitions/2'),
      );

      write.flush(envelope(definitionFixture({ propertyDefinitionId: 2 })));

      expect(store.writesInFlight()).toBe(0);

      for (const reread of httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
      )) {
        if (!reread.cancelled) {
          reread.flush(envelope([definitionFixture()]));
        }
      }
    });
  });

  describe('failures', () => {
    it('records which command failed, so several panes cannot show one message', () => {
      store.loadProfileDefinitions();

      expectRequest('GET', DEFINITIONS_URL).flush(problemFixture({ status: 500 }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      const failure = recordedFailure();

      expect(failure.operation).toBe('loadProfileDefinitions');
      expect(failure.summary.severity)
        .withContext('a genuine fault is an error, unlike a refusal')
        .toBe('error');
      expect(store.profileDefinitionsLoading()).toBeFalse();
    });

    it('retains the trace identifier and the correlation identifier that arrived', () => {
      // MEASURED DIVERGENCE, and it matters: these are two INDEPENDENT identifiers with different formats.
      store.showAllAccounts();

      expectRequest('GET', USERS_URL).flush(problemFixture({ status: 500 }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      const problem = recordedProblem();

      expect(problem.traceId).toBe(TRACE_ID);
      expect(problem.correlationId).toBe(CORRELATION_ID);
      expect(store.failureSupportReference())
        .withContext('the support reference is the correlation identifier')
        .toBe(CORRELATION_ID);
      expect(store.failureSupportReference()).not.toBe(TRACE_ID);
    });

    it('reads per-field messages with bracket access on the index-signature map', () => {
      // The keys are the model-state keys of the server, reproduced byte for byte: they name model members
      // rather than JSON members, so the camel-case body policy does not apply and they stay Pascal-cased.
      store.createUser(createRequestFixture());

      expectRequest('POST', USERS_URL).flush(validationProblemFixture(), {
        status: 422,
        statusText: 'Unprocessable Content',
      });

      const problem = recordedProblem();
      const perField = problem.errors;

      if (perField === undefined) {
        throw new Error('expected the validation failure to carry per-field messages');
      }

      expect(perField['UserName']).toEqual(['The user name is already taken.']);
      expect(perField['Email']).toEqual(['The address is malformed.']);
      expect(perField['username'])
        .withContext('the keys are not camel-cased, so this spelling is genuinely absent')
        .toBeUndefined();
    });

    it('publishes the per-field messages the shared summariser derived', () => {
      store.createUser(createRequestFixture());
      expectRequest('POST', USERS_URL).flush(validationProblemFixture(), {
        status: 422,
        statusText: 'Unprocessable Content',
      });

      const messages = store.failureFieldMessages();

      expect(messages.length).toBe(2);
      expect(recordedFailure().summary.hasFieldMessages).toBeTrue();
    });

    it('holds untrusted markup in a message as an inert plain string', () => {
      const hostile = '<script>alert(1)</script>';

      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(
        problemFixture({ status: 500, detail: `Something failed. ${hostile}` }),
        { status: 500, statusText: 'Internal Server Error' },
      );

      const problem = recordedProblem();

      expect(typeof problem.detail)
        .withContext('a plain string, and nothing wrapped or marked trusted')
        .toBe('string');
      expect(problem.detail).toBe(`Something failed. ${hostile}`);
    });

    it('keeps a self-closing legacy break prefix on the raw document', () => {
      store.createUser(createRequestFixture());
      expectRequest('POST', USERS_URL).flush(
        problemFixture({ status: 422, title: '<br/>The credential was rejected.' }),
        { status: 422, statusText: 'Unprocessable Content' },
      );

      const failure = recordedFailure();
      const problem = recordedProblem();

      expect(problem.title)
        .withContext('the structured document is stored raw, never pre-stripped')
        .toBe('<br/>The credential was rejected.');
      expect(failure.summary.title)
        .withContext('the summariser owns the stripping, and this store delegates it')
        .toBe('The credential was rejected.');
    });

    it('keeps an unclosed legacy break prefix on the raw document', () => {
      store.createUser(createRequestFixture());
      expectRequest('POST', USERS_URL).flush(
        problemFixture({ status: 422, title: '<br>The credential was rejected.' }),
        { status: 422, statusText: 'Unprocessable Content' },
      );

      expect(recordedProblem().title).toBe('<br>The credential was rejected.');
      expect(recordedFailure().summary.title).toBe('The credential was rejected.');
    });

    it('carries no failure code when the document published none', () => {
      // The server allows a document with no type, and does so deliberately rather than
      // inventing a URI that documents nothing.
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(
        problemFixture({ status: 500, type: undefined }),
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(store.failureReasonCode())
        .withContext('null means the document carried no code; undefined means no failure')
        .toBeNull();
    });

    it('reports no failure state at all when nothing has failed', () => {
      expect(store.failure()).toBeNull();
      expect(store.failureSeverity()).toBeUndefined();
      expect(store.failureReasonCode()).toBeUndefined();
      expect(store.failureSupportReference()).toBeUndefined();
      expect(store.failureFieldMessages()).toEqual([]);
    });

    it('discards a recorded failure when a screen dismisses it', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(problemFixture({ status: 500 }), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      expect(store.failure()).not.toBeNull();

      store.clearFailure();

      expect(store.failure()).toBeNull();
      expect(store.failureSeverity()).toBeUndefined();
    });

    it('reports a missing account as a warning rather than as a fault', () => {
      store.selectUser(999);
      expectRequest('GET', `${USERS_URL}/999`).flush(problemFixture({ status: 404 }), {
        status: 404,
        statusText: 'Not Found',
      });

      expect(recordedFailure().summary.severity).toBe('warning');
    });

    it('clears the loading flag of the slice that failed', () => {
      store.loadProfile(7);
      expectRequest('GET', `${USERS_URL}/7/profile`).flush(problemFixture({ status: 500 }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.profileLoading()).toBeFalse();
      expect(store.busy())
        .withContext('nothing is in flight, so nothing is busy')
        .toBeFalse();
    });
  });

  // =========================================================================
  // AUTHORISATION IS THE SERVER'S
  // =========================================================================

  describe('authorisation is the concern of the server', () => {
    it('exposes no permission-deciding member of any kind', () => {
      // NOT ONE LINE OF IT IS REPRODUCED. The API decides, and reports a refusal as a status with a problem
      // document; this store records that refusal and presents it as a refusal rather than as a fault.
      const deciding =
        /canedit|candelete|canview|isallowed|ispermitted|hasperm|isinrole|authorise|authorize|accessdenied/i;

      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(deciding.test(name))
          .withContext(`the member "${name}" would re-implement the legacy decision`)
          .toBe(false);
      }

      for (const name of Object.keys(store)) {
        expect(deciding.test(name))
          .withContext(`the slice "${name}" would re-implement the legacy decision`)
          .toBe(false);
      }
    });

    it('does not pre-check a rule the server owns before dispatching', () => {
      // Counting the accounts of a tenant first would cost a request, would race every other administrator,
      // and would still have to handle the refusal it was trying to predict. The proof is that a creation
      // issues EXACTLY ONE request and no lookup precedes it.
      store.createUser(createRequestFixture());

      const posted = expectRequest('POST', USERS_URL);

      // Counted, and scoped to the verb a pre-check would have used: the emptiness of this list is the
      // claim that nothing was looked up first. `match` removes only what it matched - nothing - so the
      // teardown's `verify()` still guards the creation itself.
      expect(httpMock.match((request) => request.method === 'GET'))
        .withContext('nothing is read before the creation is dispatched')
        .toEqual([]);

      posted.flush(envelope(detailFixture({ userId: 91 })), {
        status: 201,
        statusText: 'Created',
      });
    });

    it('records a refusal without redirecting or navigating anywhere', () => {
      store.updateUser(1, updateRequestFixture());
      expectRequest('PUT', `${USERS_URL}/1`).flush(problemFixture({ status: 403 }), {
        status: 403,
        statusText: 'Forbidden',
      });

      const failure = recordedFailure();

      expect(failure.summary.severity).toBe('warning');
      expect(failure.summary.status).toBe(403);
      // Still usable afterwards: a refusal is not a terminal state.
      store.clearFailure();
      expect(store.failure()).toBeNull();
    });
  });

  // THE ACCOUNT'S OWN SUBSCRIPTIONS
  // `Website/admin/Users/MemberServices.ascx` and its 530-line code-behind.

  describe("the account's own subscriptions", () => {
    it('reads the catalogue a bounded page at a time and publishes the account it belongs to', () => {
      store.loadMemberServices(7);

      const request = expectRequest('GET', SERVICES_URL);

      // MIGRATION: THE CATALOGUE IS NOW READ A BOUNDED PAGE AT A TIME. The legacy grid bound the whole
      // answer in one pass and the endpoint read no parameter; it now answers a page and refuses `sortBy`
      // and `query`, so the two paging arguments are sent and nothing else is.
      expect([...request.request.params.keys()].sort()).toEqual(['pageIndex', 'pageSize']);
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('pageSize')).toBe(String(CATALOGUE_PAGE_SIZE));
      expect(store.memberServicesLoading()).toBeTrue();

      request.flush(cataloguePage([serviceFixture(), serviceFixture({ roleId: 9, isSubscribed: false, isExpired: false, subscriptionAction: 'Subscribe' })]));

      expect(store.memberServices().length).toBe(2);
      expect(store.memberServicesAccountId())
        .withContext('a catalogue is meaningless without the account it belongs to')
        .toBe(7);
      expect(store.memberServicesLoading()).toBeFalse();
      expect(store.hasMemberServices()).toBeTrue();
    });

    it('derives held and lapsed sets from the rows rather than from a second request', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(
        cataloguePage([
          serviceFixture(),
          serviceFixture({ roleId: 9, isSubscribed: true, isExpired: false }),
          serviceFixture({ roleId: 11, isSubscribed: false, isExpired: false }),
        ]),
      );

      expect(store.heldMemberServices().map((offer: MemberService) => offer.roleId)).toEqual([
        0, 9,
      ]);

      // The lapsed test is the SERVER'S, carried per row. Nothing here compares a date against
      // the browser's clock, which would disagree with the server that refuses the command.
      expect(store.lapsedMemberServices().map((offer: MemberService) => offer.roleId)).toEqual([
        0,
      ]);
    });

    it('reports a tenant that offers nothing as an empty catalogue, not as a failure', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([]));

      expect(store.memberServices()).toEqual([]);
      expect(store.hasMemberServices()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('clears the rows when the account changes, and keeps them when it does not', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));

      // A refresh of the SAME account keeps what is on screen: blanking it would flicker a grid
      // that is about to answer with almost the same rows.
      store.loadMemberServices(7);
      expect(store.memberServices().length)
        .withContext('a refresh of the same account keeps the rows in hand')
        .toBe(1);
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));

      // A DIFFERENT account clears them at once, because rendering one account's subscriptions
      // under another account's key is the one outcome that cannot be allowed even briefly.
      store.loadMemberServices(11);
      expect(store.memberServices())
        .withContext("another account's catalogue is never shown while the read is in flight")
        .toEqual([]);
      expect(store.memberServicesAccountId()).toBe(11);
      expectRequest('GET', '/api/v1/users/11/services').flush(cataloguePage([]));
    });

    it('keeps the rows in hand when a read fails, and records the failure by name', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));

      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(problemFixture({ status: 500, title: 'Server' }), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.memberServices().length)
        .withContext('an empty grid beside a message would read as "you are offered nothing"')
        .toBe(1);
      expect(store.failure()?.operation).toBe('loadMemberServices');
      expect(store.memberServicesLoading()).toBeFalse();
    });

    it('subscribes with no body and re-reads the catalogue afterwards', () => {
      store.subscribeToService(7, 0);

      const command = expectRequest('POST', SERVICE_SUBSCRIPTION_URL);

      expect(command.request.body).toBeNull();
      expect(store.saving()).toBeTrue();

      command.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.saving()).toBeFalse();

      // The command answers with no body, so the state on screen can only come from a re-read.
      expectRequest('GET', SERVICES_URL).flush(
        cataloguePage([serviceFixture({ isExpired: false, subscriptionAction: 'Unsubscribe' })]),
      );

      expect(store.memberServices()[0].subscriptionAction).toBe('Unsubscribe');
      expect(store.lapsedMemberServices()).toEqual([]);
    });

    it('records a refusal to subscribe and issues no re-read at all', () => {
      store.subscribeToService(7, 0);

      expectRequest('POST', SERVICE_SUBSCRIPTION_URL).flush(
        problemFixture({
          type: `${FAILURE_TYPE}user.service.payment-required-forbidden`,
          status: 403,
          title: 'Forbidden',
          detail: 'This service requires payment, which this application cannot take.',
        }),
        { status: 403, statusText: 'Forbidden' },
      );

      expect(store.failure()?.operation).toBe('subscribeToService');
      // ⚠ READ AS THE CLIENT NORMALISES IT, NOT AS THE SERVER SPELLS IT. `failureCode` lower-cases the
      // reason and rewrites every hyphen as an underscore, deliberately mirroring what
      // `GlobalExceptionHandler` does before it chooses a status — so one spelling difference cannot make a
      // client and a server disagree about a reason.
      expect(store.failureReasonCode())
        .withContext('the excluded payment path is reported by its own reason')
        .toBe('user.service.payment_required_forbidden');
      expect(store.saving()).toBeFalse();

      // Nothing to verify but the absence: the closing verification of this suite fails if a
      // re-read was dispatched, because it would be left outstanding.
    });

    it('cancels at the subscription address with the removing verb, then re-reads', () => {
      store.cancelService(7, 0);

      const command = expectRequest('DELETE', SERVICE_SUBSCRIPTION_URL);

      expect(command.request.body).toBeNull();
      command.flush(null, { status: 204, statusText: 'No Content' });

      expectRequest('GET', SERVICES_URL).flush(
        cataloguePage([serviceFixture({ isSubscribed: false, isExpired: false, subscriptionAction: 'Subscribe' })]),
      );

      expect(store.heldMemberServices()).toEqual([]);
      expect(store.failure()).toBeNull();
    });

    it('takes a trial at its own address, then re-reads', () => {
      store.startServiceTrial(7, 0);

      const command = expectRequest('POST', SERVICE_TRIAL_URL);

      expect(command.request.body).toBeNull();
      command.flush(null, { status: 204, statusText: 'No Content' });

      expectRequest('GET', SERVICES_URL).flush(
        cataloguePage([serviceFixture({ trialOffered: false, isExpired: false })]),
      );

      expect(store.memberServices()[0].trialOffered).toBeFalse();
    });

    it('records a refusal of a trial the service does not offer', () => {
      store.startServiceTrial(7, 0);

      expectRequest('POST', SERVICE_TRIAL_URL).flush(
        problemFixture({
          type: `${FAILURE_TYPE}user.service.trial-not-offered-forbidden`,
          status: 403,
          title: 'Forbidden',
        }),
        { status: 403, statusText: 'Forbidden' },
      );

      expect(store.failure()?.operation).toBe('startServiceTrial');
      expect(store.failureReasonCode()).toBe('user.service.trial_not_offered_forbidden');
    });

    it('redeems a code as typed, reports every role it joined, and re-reads', () => {
      store.redeemServiceCode(7, '  Founders-2026  ');

      const command = expectRequest('POST', SERVICE_REDEMPTIONS_URL);

      expect(command.request.body).toEqual({ code: '  Founders-2026  ' });

      command.flush(
        envelope({
          roles: [
            { roleId: 0, roleName: 'Premium Members' },
            { roleId: 7, roleName: 'Founders' },
          ],
        } satisfies RedeemServiceCodeResult),
      );

      expect(store.lastRedemption()?.roles.length)
        .withContext('the legacy walk had no early exit, so one code may join several roles')
        .toBe(2);

      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));

      expect(store.memberServices().length).toBe(1);
    });

    it('reports a code that matched nothing as a refusal, with no redemption recorded', () => {
      store.redeemServiceCode(7, 'nope');

      expectRequest('POST', SERVICE_REDEMPTIONS_URL).flush(
        problemFixture({
          type: `${FAILURE_TYPE}user.service.code-not-matched`,
          status: 400,
          title: 'Bad Request',
        }),
        { status: 400, statusText: 'Bad Request' },
      );

      expect(store.lastRedemption()).toBeNull();
      expect(store.failure()?.operation).toBe('redeemServiceCode');
      expect(store.failureReasonCode()).toBe('user.service.code_not_matched');
    });

    it('discards a redemption report once a later command changes the state it described', () => {
      store.redeemServiceCode(7, 'Founders-2026');
      expectRequest('POST', SERVICE_REDEMPTIONS_URL).flush(
        envelope({ roles: [{ roleId: 0, roleName: 'Premium Members' }] }),
      );
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));

      expect(store.lastRedemption()).not.toBeNull();

      store.cancelService(7, 0);

      expect(store.lastRedemption())
        .withContext('a report of what a code joined is no longer true once one is cancelled')
        .toBeNull();

      expectRequest('DELETE', SERVICE_SUBSCRIPTION_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([]));
    });

    it('dismisses its own redemption report without touching the catalogue', () => {
      store.redeemServiceCode(7, 'Founders-2026');
      expectRequest('POST', SERVICE_REDEMPTIONS_URL).flush(
        envelope({ roles: [{ roleId: 0, roleName: 'Premium Members' }] }),
      );
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));

      store.clearRedemption();

      expect(store.lastRedemption()).toBeNull();
      expect(store.memberServices().length)
        .withContext('dismissing a message is not a reason to discard the rows')
        .toBe(1);
    });

    it('discards the catalogue, its account and its report when the session ends', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(cataloguePage([serviceFixture()]));
      store.redeemServiceCode(7, 'Founders-2026');
      expectRequest('POST', SERVICE_REDEMPTIONS_URL).flush(
        envelope({ roles: [{ roleId: 0, roleName: 'Premium Members' }] }),
      );

      const followUp = expectRequest('GET', SERVICES_URL);

      store.reset();

      // A catalogue is the personal subscription state of ONE account, and every endpoint that
      // produces it is gated on ownership — so nothing about it may survive a session boundary.
      expect(store.memberServices()).toEqual([]);
      expect(store.memberServicesAccountId()).toBeUndefined();
      expect(store.lastRedemption()).toBeNull();
      expect(store.memberServicesLoading()).toBeFalse();
      expect(followUp.cancelled)
        .withContext('the read in flight is released rather than allowed to repopulate')
        .toBeTrue();
    });
  });

  // =========================================================================
  // THE PUBLISHED SURFACE
  // =========================================================================

  describe('the published surface', () => {
    it('publishes state that cannot be written to from outside', () => {
      const published = [
        store.users,
        store.search,
        store.selectedUserId,
        store.selectedUser,
        store.profile,
        store.membershipSettings,
        store.profileDefinitions,
        store.requestedPageIndex,
        store.failure,
        store.saving,
        store.usersLoading,
        store.profileDefinitionBatchRemaining,
      ];

      for (const slice of published) {
        expect('set' in slice)
          .withContext('a published slice must expose no setter')
          .toBe(false);
        expect('update' in slice)
          .withContext('a published slice must expose no updater')
          .toBe(false);
      }
    });

    it('publishes derived values that cannot be written to either', () => {
      const derived = [
        store.userRows,
        store.pageMeta,
        store.totalCount,
        store.totalPages,
        store.effectivePageSize,
        store.searchMode,
        store.observedPortalId,
        store.failureSeverity,
      ];

      for (const value of derived) {
        expect('set' in value).toBe(false);
        expect('update' in value).toBe(false);
      }
    });

    it('replaces the page rather than mutating the one a consumer already holds', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()], { totalCount: 1 }));

      const held = store.users();

      expect(held.items.length).toBe(1);

      store.goToPage(1);
      expectRequest('GET', USERS_URL).flush(
        pageFixture([listItemFixture({ userId: 8 }), listItemFixture({ userId: 9 })], {
          pageIndex: 1,
          totalCount: 3,
        }),
      );

      expect(store.users())
        .withContext('a new page is a new object, not an edited one')
        .not.toBe(held);
      expect(held.items.length)
        .withContext('the snapshot a consumer already held is untouched')
        .toBe(1);
      expect(held.meta.totalCount).toBe(1);
      expect(store.users().items.length).toBe(2);
    });

    it('replaces the declaration list rather than mutating it in place', () => {
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([definitionFixture()]));

      const held = store.profileDefinitions();

      // ⚠ THE SECOND READ IS THE EXPLICIT REFRESH, NOT THE BRING-UP COMMAND. The bring-up command reuses
      // what is held, which is the point of the guard asserted below; the refresh command states in its own
      // name that it must re-ask. This specification is about the published surface being REPLACED rather
      // than mutated, so it needs a read that actually happens.
      store.refreshProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([definitionFixture(), definitionFixture({ propertyDefinitionId: 4 })]),
      );

      expect(store.profileDefinitions()).not.toBe(held);
      expect(held.length).toBe(1);
      expect(store.profileDefinitions().length).toBe(2);
    });

    it('reads the tenant catalogue once and reuses it, because it is a tenant-wide constant', () => {
      // ⚠ THE DEFECT THIS GUARDS: the catalogue was re-read on every entry to the account listing and to the
      // catalogue screen, asking the server again for something that changes only when an operator edits it.
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([definitionFixture()]));

      store.loadProfileDefinitions();
      store.loadProfileDefinitions();

      httpMock.expectNone(DEFINITIONS_URL);
      expect(store.profileDefinitions().length)
        .withContext('and what was already read is still published')
        .toBe(1);
    });

    it('reuses a catalogue that is legitimately EMPTY rather than re-asking forever', () => {
      // A tenant that declares no properties is a real answer. Were the guard an emptiness test on the held
      // value instead of a record that a read succeeded, this tenant would re-ask on every screen entry.
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));

      store.loadProfileDefinitions();

      httpMock.expectNone(DEFINITIONS_URL);
      expect(store.profileDefinitions()).toEqual([]);
    });

    it('re-asks after a FAILED read, so a transient fault is not cached as an answer', () => {
      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        { title: 'Service Unavailable', status: 503 },
        { status: 503, statusText: 'Service Unavailable' },
      );

      store.loadProfileDefinitions();

      expectRequest('GET', DEFINITIONS_URL).flush(envelope([definitionFixture()]));
      expect(store.profileDefinitions().length).toBe(1);
    });

    it('answers with a stable reference while nothing has changed', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      expect(store.users()).toBe(store.users());
      expect(store.userRows()).toBe(store.userRows());
    });

    it('seeds every slice so that no consumer has to branch on absence', () => {
      expect(store.users().items).toEqual([]);
      expect(store.userRows()).toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.totalPages()).toBe(0);
      expect(store.hasRecords()).toBeFalse();
      expect(store.profileDefinitions()).toEqual([]);
      expect(store.hasProfileDefinitions()).toBeFalse();
      expect(store.busy()).toBeFalse();
      expect(store.saving()).toBeFalse();
      expect(store.profileDefinitionBatchRemaining()).toBe(0);
      expect(store.searchMode()).toBe('none');
      expect(store.requestedPageIndex()).toBe(0);
    });

    it('returns every slice to its seeded state on a reset', () => {
      openListingAtPageSize(25);

      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.reset();

      expect(store.users().items).toEqual([]);
      expect(store.searchMode()).toBe('none');
      expect(store.requestedPageIndex()).toBe(0);
      expect(store.selectedUserId()).toBeUndefined();
      expect(store.selectedUser()).toBeNull();
      expect(store.membershipSettings()).toBeNull();
      expect(store.profileDefinitions()).toEqual([]);
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.effectivePageSize())
        .withContext('the policy is gone, so the shared fallback applies again')
        .toBe(DEFAULT_PAGE_SIZE);
    });

    it('abandons a read in flight when a newer one supersedes it', () => {
      store.showAllAccounts();
      const first = expectRequest('GET', USERS_URL);

      store.goToPage(1);
      const second = expectRequest('GET', USERS_URL);

      expect(first.cancelled)
        .withContext('the superseded read is abandoned rather than left racing')
        .toBeTrue();
      expect(second.cancelled).toBeFalse();

      second.flush(pageFixture([listItemFixture()], { pageIndex: 1, totalCount: 20 }));

      expect(store.currentPageIndex()).toBe(1);
    });

    /**
     * ⚠ AN ABANDONED READ LOWERS ITS OWN FLAG, AND THIS SUITE EXISTS BECAUSE ONE DID NOT. Unsubscribing
     * kills a request without delivering next, error or complete, so nothing downstream ever runs the
     * handler that would lower the flag - the flag is therefore raised forever.
     */
    it('lowers the listing flag when the query reset abandons the read', () => {
      store.showAllAccounts();
      const listing = expectRequest('GET', USERS_URL);

      expect(store.usersLoading()).toBeTrue();

      store.resetSearchCriteria();

      expect(listing.cancelled)
        .withContext('the read belonging to the abandoned query is abandoned with it')
        .toBeTrue();
      expect(store.usersLoading())
        .withContext('and the flag it raised comes down with it - nothing else will ever lower it')
        .toBeFalse();
    });

    /**
     * ⚠ THE QUERY RESET ABANDONS THE QUERY'S READ AND NOTHING ELSE. The tenant's account policy and
     * profile declarations are not part of a query, and the reset's own documentation says they must
     * survive it - discarding them "would turn one stale query into several redundant requests".
     */
    it('leaves the tenant-scoped reads alone when only the query is being replaced', () => {
      store.loadMembershipSettings();
      const settings = expectRequest('GET', SETTINGS_URL);

      store.loadProfileDefinitions();
      const definitions = expectRequest('GET', DEFINITIONS_URL);

      store.resetSearchCriteria();

      expect(settings.cancelled)
        .withContext('the account policy is not part of the query and must survive it')
        .toBeFalse();
      expect(definitions.cancelled)
        .withContext('nor are the profile declarations')
        .toBeFalse();

      // And they still settle normally afterwards, which is the point of not cancelling them.
      settings.flush(envelope(storedSettingsFixture()));
      definitions.flush(envelope([definitionFixture()]));

      expect(store.membershipSettings()).not.toBeNull();
      expect(store.membershipSettingsLoading()).toBeFalse();
      expect(store.profileDefinitionsLoading()).toBeFalse();
    });

    it('reports reading and writing separately, so a form can disable only itself', () => {
      store.showAllAccounts();
      const listing = expectRequest('GET', USERS_URL);

      expect(store.usersLoading()).toBeTrue();
      expect(store.saving()).toBeFalse();
      expect(store.busy()).toBeTrue();

      listing.flush(pageFixture([listItemFixture()]));

      expect(store.usersLoading()).toBeFalse();

      store.updateUser(7, updateRequestFixture());
      const write = expectRequest('PUT', `${USERS_URL}/7`);

      expect(store.saving()).toBeTrue();
      expect(store.usersLoading()).toBeFalse();

      write.flush(envelope(detailFixture({ userId: 7 })));

      const reread = expectRequest('GET', USERS_URL);
      reread.flush(pageFixture([listItemFixture()]));

      expect(store.saving()).toBeFalse();
      expect(store.busy()).toBeFalse();
    });
  });
  // SESSION ISOLATION
  describe('session isolation', () => {
    it('cancels a read in flight on reset, so its answer cannot repopulate the store', () => {
      store.loadMembershipSettings();

      const pending = expectRequest('GET', SETTINGS_URL);

      store.reset();

      expect(pending.cancelled)
        .withContext('the request is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.membershipSettings()).toBeNull();
      expect(store.membershipSettingsLoading()).toBeFalse();
    });

    it('cancels a WRITE in flight on reset, which reads-only cancellation did not', () => {
      store.createUser(createRequestFixture());

      const pending = expectRequest('POST', USERS_URL);

      store.reset();

      expect(pending.cancelled)
        .withContext('a write must not outlive the session that issued it')
        .toBeTrue();
      expect(store.selectedUser()).toBeNull();
      expect(store.saving()).toBeFalse();
    });

    it('releases a BATCH in flight on reset, so the next operator is not refused', () => {
      // ⚠ A CANCELLED STREAM NEVER COMPLETES, so the arm that lowers the batch count never runs. Left
      // standing, that count would refuse the FIRST batch the next operator staged - silently, and for the
      // remaining life of the store, because nothing else lowers it.
      store.applyProfileDefinitionEdits([
        { propertyDefinitionId: 4, request: definitionWriteFixture() },
        { propertyDefinitionId: 7, request: definitionWriteFixture() },
      ]);

      const pending = expectRequest('PUT', `${DEFINITIONS_URL}/4`);

      expect(store.profileDefinitionBatchRemaining()).toBe(2);

      store.reset();

      expect(pending.cancelled)
        .withContext('a batch must not outlive the session that staged it')
        .toBeTrue();
      expect(store.profileDefinitionBatchRemaining())
        .withContext('released where the writes are released, not on completion')
        .toBe(0);
      expect(store.saving()).toBeFalse();

      // The second row is never written, because the concatenation that would have composed it
      // was abandoned - and the next batch is accepted, which a standing count would have refused.
      store.applyProfileDefinitionEdits([
        { propertyDefinitionId: 9, request: definitionWriteFixture() },
      ]);

      expectRequest('PUT', `${DEFINITIONS_URL}/9`).flush(
        envelope(definitionFixture({ propertyDefinitionId: 9 })),
      );
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([]));

      httpMock.expectNone(
        (request) => request.url === `${DEFINITIONS_URL}/7`,
        'the row behind the cancelled one was never composed',
      );
    });

    it('discards every account-scoped slice on reset', () => {
      store.loadMembershipSettings();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ recordsPerPage: 25 })));

      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(envelope([definitionFixture()]));

      // Selecting reads the account; the profile is a separate command, because a listing screen
      // needs the account without paying for its profile.
      store.selectUser(7);
      expectRequest('GET', `${USERS_URL}/7`).flush(envelope(detailFixture({ userId: 7 })));

      store.loadProfile(7);
      expectRequest('GET', `${USERS_URL}/7/profile`).flush(envelope(profileFixture(7)));

      expect(store.membershipSettings()).not.toBeNull();
      expect(store.profileDefinitions().length).toBe(1);
      expect(store.selectedUser()).not.toBeNull();
      expect(store.profile()).not.toBeNull();

      store.reset();

      expect(store.membershipSettings()).toBeNull();
      expect(store.profileDefinitions().length).toBe(0);
      expect(store.selectedUser())
        .withContext('an account is personal data and must not outlive its session')
        .toBeNull();
      expect(store.profile()).toBeNull();
      expect(store.users().items.length).toBe(0);
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
    });

    it('keeps accepting writes after a reset, which a Subscription container would have broken', () => {
      store.reset();

      store.createUser(createRequestFixture());

      const pending = expectRequest('POST', USERS_URL);

      expect(pending.cancelled)
        .withContext('a write issued after a reset must not be cancelled on arrival')
        .toBeFalse();

      pending.flush(envelope(detailFixture({ userId: 11 })), {
        status: 201,
        statusText: 'Created',
      });

      expect(store.selectedUser()?.userId)
        .withContext('the callback ran, so the handle was live')
        .toBe(11);
      expect(store.saving()).toBeFalse();
    });

    it('releases a write handle when the write settles, so the set cannot grow without bound', () => {
      store.createUser(createRequestFixture());
      expectRequest('POST', USERS_URL).flush(envelope(detailFixture({ userId: 12 })), {
        status: 201,
        statusText: 'Created',
      });

      store.updateUser(12, updateRequestFixture());

      const second = expectRequest('PUT', `${USERS_URL}/12`);

      store.reset();

      expect(second.cancelled)
        .withContext('the outstanding write is released')
        .toBeTrue();
    });
  });

  // WRITE IDENTITY, AND WHY AN AGGREGATE FLAG COULD NOT SETTLE A WRITE
  // (a) TWO WRITES, ONE FLAG. The account list dispatches a removal, a settings pane dispatches a save, the
  // save settles first — the flag falls and BOTH conclude their own write is done.
  describe('every write is settled by identity rather than by an aggregate flag', () => {
    it('hands back a distinct identifier for every write, and never the absent value', () => {
      // Zero is reserved as "no write awaited" by the screens that hold one of these, so the FIRST
      // identifier must not be zero. The counter therefore pre-increments, asserted here rather than left
      // to a comment.
      const first = store.createUser(createRequestFixture());
      const second = store.unlockUser(7);

      expect(first).withContext('the absent marker must never be issued').not.toBe(0);
      expect(second).not.toBe(first);
      expect(second).toBeGreaterThan(first);

      expectRequest('POST', USERS_URL).flush(envelope(detailFixture({ userId: 91 })), {
        status: 201,
        statusText: 'Created',
      });
      expectRequest('POST', `${USERS_URL}/7/unlock`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
    });

    it('publishes a result naming the write that settled, its operation and its own outcome', () => {
      const issued = store.unlockUser(7);

      expect(store.mutation())
        .withContext('nothing is published while the write is open')
        .toBeNull();

      expectRequest('POST', `${USERS_URL}/7/unlock`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      const settled = store.mutation();

      if (settled === null) {
        throw new Error('expected the write to have settled');
      }

      expect(settled.id).toBe(issued);
      expect(settled.operation).toBe('unlockUser');
      expect(settled.failure)
        .withContext('a success settles with no failure attached')
        .toBeNull();
    });

    it('carries a refusal ON the settled result, so no screen reads it out of shared state', () => {
      const issued = store.deleteUser(7);

      expectRequest('DELETE', `${USERS_URL}/7`).flush(
        problemFixture({ status: 409, detail: 'The last administrator cannot be removed.' }),
        { status: 409, statusText: 'Conflict' },
      );

      const settled = store.mutation();

      if (settled === null || settled.failure === null) {
        throw new Error('expected the refusal to travel on the settled result');
      }

      expect(settled.id).toBe(issued);
      expect(settled.operation).toBe('deleteUser');
      expect(settled.failure.operation).toBe('deleteUser');
      expect(settled.failure.problem?.status).toBe(409);
    });

    it('settles the first of two open writes without settling the second', () => {
      // ⚠ DEFECT (a), AND THE CASE THE AGGREGATE FLAG COULD NOT EXPRESS. Both writes are open; the second
      // answers first. The result must name the SECOND, and the aggregate must stay raised because the
      // first is still open.
      const firstWrite = store.deleteUser(7);
      const secondWrite = store.unlockUser(8);

      const removal = expectRequest('DELETE', `${USERS_URL}/7`);

      expectRequest('POST', `${USERS_URL}/8/unlock`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.mutation()?.id).toBe(secondWrite);
      expect(store.saving())
        .withContext('one write settling must not report the other as settled')
        .toBeTrue();

      removal.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.mutation()?.id).toBe(firstWrite);
      expect(store.saving())
        .withContext('the aggregate falls only once every write has settled')
        .toBeFalse();
    });

    it('does not attach one write\u2019s refusal to another write\u2019s result', () => {
      // ⚠ DEFECT (b). The refused write and the successful one overlap, and the successful one settles LAST
      // — so the shared failure slot holds a refusal at the very moment the successful write's result is
      // published.
      store.deleteUser(7);

      const refused = expectRequest('DELETE', `${USERS_URL}/7`);
      const succeeding = store.unlockUser(8);
      const unlock = expectRequest('POST', `${USERS_URL}/8/unlock`);

      refused.flush(problemFixture({ status: 409, detail: 'That account cannot be removed.' }), {
        status: 409,
        statusText: 'Conflict',
      });

      expect(store.failure()?.operation)
        .withContext('the slot does hold the refusal at this instant')
        .toBe('deleteUser');

      unlock.flush(null, { status: 204, statusText: 'No Content' });

      const settled = store.mutation();

      if (settled === null) {
        throw new Error('expected the successful write to have settled');
      }

      expect(settled.id).toBe(succeeding);
      expect(settled.failure)
        .withContext('a successful write must not inherit the other write\u2019s refusal')
        .toBeNull();

      store.loadMembershipSettings();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture()));

      expect(store.failure())
        .withContext('the shared slot cannot be relied on to still hold the refusal')
        .toBeNull();
      expect(store.mutation()?.id)
        .withContext('while the settled result still names the write it belongs to')
        .toBe(succeeding);
    });

    it('clears the published result and the pending count on a session boundary', () => {
      store.unlockUser(7);
      expectRequest('POST', `${USERS_URL}/7/unlock`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.mutation()).not.toBeNull();

      store.reset();

      expect(store.mutation())
        .withContext('a result from the ended session must not settle a new one\u2019s write')
        .toBeNull();
      expect(store.saving()).toBeFalse();
    });

    it('lowers the pending count when a write is released rather than answered', () => {
      store.createUser(createRequestFixture());

      const pending = expectRequest('POST', USERS_URL);

      expect(store.saving()).toBeTrue();

      store.reset();

      expect(pending.cancelled).withContext('the request is abandoned').toBeTrue();
      expect(store.saving())
        .withContext('a released write must not leave the store permanently busy')
        .toBeFalse();
    });
  });

  // =========================================================================
  // THE SETTLED LATCH — "NOT ASKED YET" IS NOT "ASKED AND EMPTY"
  // =========================================================================

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED, AND THIS SLICE HELD ITS WORST INSTANCE. The opening sequence
  // reads the tenant's policy BEFORE it knows what listing to ask for, and arriving at the screen empties the
  // page, so for a whole round trip the grid held no rows with no request in flight — and painted "Nothing to
  // Display" over a tenant whose accounts had simply not been requested yet.
  describe('the settled latch', () => {
    it('is DOWN on a fresh store and nothing is in flight, which is what made the two states identical', () => {
      expect(store.listSettled()).toBeFalse();
      expect(store.userRows()).toEqual([]);
      expect(store.usersLoading()).toBeFalse();
    });

    it('stays DOWN for the WHOLE policy round trip, which is the window the flash appeared in', () => {
      store.initialise();
      const policy = expectRequest('GET', SETTINGS_URL);

      expect(store.listSettled())
        .withContext('the policy is outstanding, so what to list is not decided yet')
        .toBeFalse();

      policy.flush(envelope(storedSettingsFixture({ displayMode: 0 })));

      // The policy has answered and the listing request now exists - still not settled.
      const listing = expectRequest('GET', USERS_URL);
      expect(store.listSettled()).toBeFalse();

      listing.flush(pageFixture([listItemFixture()]));

      expect(store.listSettled()).toBeTrue();
    });

    it('rises when the tenant\'s policy is that NOTHING is listed until somebody asks', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 2 })));

      httpMock.expectNone(() => true);

      // ⚠ ANSWERED, NOT UNANSWERED. No read will be made, so a screen must stop indicating that one is
      // coming - otherwise the no-query notice sat under a waiting indicator that never resolved.
      expect(store.noQueryIssued()).toBeTrue();
      expect(store.listSettled()).toBeTrue();
      expect(store.usersLoading()).toBeFalse();
    });

    it('rises on a FAILED listing read too, so a waiting indicator cannot stand over a reportable failure', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 0 })));
      expectRequest('GET', USERS_URL).flush(
        { title: 'Server Error', status: 500 },
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(store.listSettled()).toBeTrue();
      expect(store.failure()).not.toBeNull();
    });

    it('goes back DOWN when the criteria are cleared, because that empties the page it spoke for', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 0 })));
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));
      expect(store.listSettled()).toBeTrue();

      // What arriving at the listing screen does, and the reason the flash was on EVERY arrival.
      store.resetSearchCriteria();

      expect(store.listSettled()).toBeFalse();
      expect(store.userRows()).toEqual([]);
    });

    it('goes back DOWN on reset, because the page it spoke for is discarded with the session', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(storedSettingsFixture({ displayMode: 0 })));
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.reset();

      expect(store.listSettled()).toBeFalse();
    });
  });

});
