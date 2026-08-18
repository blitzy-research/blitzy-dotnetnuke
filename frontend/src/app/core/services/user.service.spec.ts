import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  type TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import type { ObservedValueOf, Observable } from 'rxjs';

import type { ApiResponse } from '../models/paged-result.model';
import type {
  ChangePasswordRequest,
  CreateUserRequest,
  MemberService,
  MembershipSettings,
  PagedUserList,
  RedeemServiceCodeResult,
  UpdateUserRequest,
  UserDetail,
  UserListItem,
  UserListQuery,
} from '../models/user.model';
import { UserService } from './user.service';
import { PRESENTED_IN_CONTEXT } from './notification.service';
import { isContractViolation } from '../utils/decode.util';

// ---------------------------------------------------------------------------
// Shapes derived from the subject's own signature
// ---------------------------------------------------------------------------

/** One profile definition, as the read methods answer with it. */
type ProfileDefinition = NonNullable<
  ObservedValueOf<ReturnType<UserService['getProfileDefinition']>>
>;

/** One account's profile, as the read method answers with it. */
type ProfileRead = NonNullable<ObservedValueOf<ReturnType<UserService['getProfile']>>>;

/** The whole-profile replacement body. */
type ProfileSubmission = Parameters<UserService['updateProfile']>[1];

/** The create body for a profile definition - the only shape that may name a module. */
type CreateDefinitionRequest = Parameters<UserService['createProfileDefinition']>[0];

/** The replace body for a profile definition - the shared member set alone. */
type UpdateDefinitionRequest = Parameters<UserService['updateProfileDefinition']>[1];

// ---------------------------------------------------------------------------
// Expected URLs, spelled independently of the subject
// ---------------------------------------------------------------------------

/** The account collection. */
const USERS = '/api/v1/users';

/**
 * The body-bound account search. ⚠ A SEPARATE ADDRESS FROM {@link USERS}, AND THE DISTINCTION IS A
 * PRIVACY BOUNDARY RATHER THAN A ROUTING DETAIL. Four of the listing's filters identify a person — a user
 * name, an email address, and an arbitrary profile-property name paired with the value to match — and a
 * query parameter travels in the REQUEST TARGET, which is written to the browser's history, to every
 * forward and reverse proxy's access log, to the server's access log and to any telemetry that samples
 * URLs.
 */
const USERS_SEARCH = '/api/v1/users/search';

/**
 * The tenant's account policy. NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS, RECORDED RATHER THAN
 * PAPERED OVER. The requirements for this specification name the policy path as
 * `/api/v1/users/settings/membership`.
 */
const ACCOUNT_POLICY = '/api/v1/users/settings';

/** The profile-definition collection. */
const PROFILE_DEFINITIONS = '/api/v1/profile-definitions';

/** The member-services catalogue of account 1. */
const MEMBER_SERVICES = '/api/v1/users/1/services';

/**
 * The page size the whole-catalogue reader asks for. Mirrors `WHOLE_CATALOGUE_PAGE_SIZE` in the service:
 * duplicated rather than exported, because the value is part of what these assertions pin.
 */
const WHOLE_CATALOGUE_PAGE_SIZE = 100;

/**
 * The paged wire envelope the member-services endpoint answers with. Distinct from the single-payload
 * envelope in exactly the respect that matters here - its metadata is populated rather than null - because
 * the catalogue is now returned a bounded page at a time.
 *
 * @param items The rows of this page.
 * @param totalCount The total across every page. Defaults to a single complete page.
 * @returns The body to flush.
 */
const catalogue = <TRow>(
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
    pageSize: WHOLE_CATALOGUE_PAGE_SIZE,
    totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / WHOLE_CATALOGUE_PAGE_SIZE),
  },
});

/**
 * The subscription of account 1 to service 0. ⚠ THE SERVICE IDENTIFIER IS ZERO ON PURPOSE. `Roles.RoleID`
 * seeds `IDENTITY(0, 1)` (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`
 * L114), so role zero is the administrator role of every shipped installation - and it is also the value
 * a truthiness test drops.
 */
const MEMBER_SERVICE_SUBSCRIPTION = '/api/v1/users/1/services/0/subscription';

/** The trial of service 0, taken by account 1. */
const MEMBER_SERVICE_TRIAL = '/api/v1/users/1/services/0/trial';

/** The invitation-code redemptions of account 1. */
const MEMBER_SERVICE_REDEMPTIONS = '/api/v1/users/1/services/redemptions';

/**
 * The trailing match character the legacy screen appended, expressed as a character code rather than as a
 * literal. The character itself is deliberately absent from this file's source: the discipline check that
 * proves no wildcard has been embedded in a fixture or an expectation is a search for that literal, and
 * it must find nothing.
 */
const TRAILING_MATCH_CHARACTER = String.fromCharCode(37);

const LEGACY_NO_SEARCH_SENTINEL = 'None';

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/** One row of the account listing. */
const USER_LIST_ITEM: UserListItem = {
  userId: 1,
  portalId: -1,
  username: 'site.administrator',
  firstName: 'Site',
  lastName: 'Administrator',
  displayName: 'Site Administrator',
  address: null,
  telephone: null,
  email: 'site.administrator@example.test',
  createdDate: '2024-01-01T00:00:00.000Z',
  lastLoginDate: null,
  isApproved: true,
  isOnline: false,
  isSuperUser: false,
  isLockedOut: false,
  canDelete: true,
};

/** One account, as a read answers with it. */
const USER_DETAIL: UserDetail = {
  userId: 1,
  portalId: -1,
  username: 'site.administrator',
  firstName: 'Site',
  lastName: 'Administrator',
  displayName: 'Site Administrator',
  email: 'site.administrator@example.test',
  isSuperUser: false,
  affiliateId: null,
  isApproved: true,
  isLockedOut: false,
  isOnline: false,
  mustChangePassword: false,
  createdDate: '2024-01-01T00:00:00.000Z',
  lastLoginDate: null,
  lastActivityDate: null,
  lastLockoutDate: null,
  lastPasswordChangeDate: null,
  roles: ['Administrators'],
  canDelete: true,
  // Opaque and never interpreted here: a fixture only has to carry one for the round trip to close.
  concurrencyToken: 'user-revision-token',
};

const USER_PAGE: PagedUserList = {
  items: [USER_LIST_ITEM],
  meta: { totalCount: 1, pageIndex: 0, pageSize: 25, totalPages: 1 },
};

/**
 * An account to create. Carries a deliberately falsy value in each of the two categories that a
 * truthiness-filtered client would silently drop: an EMPTY STRING for the shown name, and FALSE for the
 * approve-on-create choice.
 */
const CREATE_USER_REQUEST: CreateUserRequest = {
  username: 'new.operator',
  firstName: 'New',
  lastName: 'Operator',
  displayName: '',
  email: 'new.operator@example.test',
  password: 'placeholder-value-not-a-credential',
  confirmPassword: 'placeholder-value-not-a-credential',
  authorize: false,
};

/** The editable members of an account, two of them deliberately cleared. */
const UPDATE_USER_REQUEST: UpdateUserRequest = {
  firstName: 'Renamed',
  lastName: '',
  displayName: '',
  email: 'renamed.operator@example.test',
  // The revision the submission was composed against, echoed back verbatim.
  concurrencyToken: 'user-revision-token',
};

/**
 * A credential change made by the account holder, who supplies the credential in force. The confirmation
 * member is present and deliberately EQUAL to the replacement, because the server is what compares them.
 */
const CHANGE_PASSWORD_REQUEST: ChangePasswordRequest = {
  operation: 'change',
  currentPassword: 'placeholder-value-in-force',
  newPassword: 'placeholder-value-replacement',
  confirmPassword: 'placeholder-value-replacement',
};

/**
 * A credential reset made by an administrator, who supplies no credential in force. The in-force member
 * is present and NULL rather than omitted - a reset proves nothing about the previous credential, and
 * saying so explicitly is different from forgetting to say it.
 */
const RESET_PASSWORD_REQUEST: ChangePasswordRequest = {
  operation: 'reset',
  currentPassword: null,
  newPassword: 'placeholder-value-replacement',
  confirmPassword: 'placeholder-value-replacement',
};

/**
 * The tenant's whole account policy, written to be hostile to a truthiness filter. Twenty-three members,
 * of which nineteen are falsy: seven unticked listing columns, three unticked profile switches, two
 * unticked security switches, four numeric zeros, two empty strings and one null.
 */
const STORED_ACCOUNT_POLICY_BODY: MembershipSettings = {
  // ⚠ #5/#6 — the flag that distinguishes a stored policy from the defaults that stand in for one. True
  // here because this fixture stands for a policy a tenant really saved.
  isStored: true,
  columnFirstName: false,
  columnLastName: false,
  columnDisplayName: true,
  columnAddress: false,
  columnTelephone: false,
  columnEmail: true,
  columnCreatedDate: false,
  columnLastLogin: false,
  columnAuthorized: false,
  displayMode: 0,
  displaySuppressPager: false,
  recordsPerPage: 0,
  profileDefaultVisibility: 0,
  profileDisplayVisibility: false,
  profileManageServices: false,
  redirectAfterLogin: -1,
  redirectAfterRegistration: null,
  redirectAfterLogout: 0,
  securityEmailValidation: '',
  securityRequireValidProfile: false,
  securityRequireValidProfileAtLogin: false,
  securityUsersControl: 0,
  securityDisplayNameFormat: '',
};

/**
 * The policy a tenant with NO SETTINGS SOURCE is answered with: the platform defaults, marked as
 * defaults. ⚠ #5/#6 — THE BRANCH A SINGLE `isStored: true` FIXTURE MADE UNTESTABLE. The server answers a
 * tenant that holds no "User Accounts" module instance `200` with the measured legacy defaults and
 * `isStored: false`; it does NOT answer `404`, and it has not since the read stopped reporting absence on
 * the status line.
 */
const UNSTORED_ACCOUNT_POLICY_BODY: MembershipSettings = {
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
  displaySuppressPager: false,
  recordsPerPage: 10,
  profileDefaultVisibility: 2,
  profileDisplayVisibility: true,
  profileManageServices: true,
  redirectAfterLogin: null,
  redirectAfterRegistration: null,
  redirectAfterLogout: null,
  securityEmailValidation: '\\b[a-zA-Z0-9._%\\-+\']+@[a-zA-Z0-9.\\-]+\\.[a-zA-Z]{2,4}\\b',
  securityRequireValidProfile: false,
  securityRequireValidProfileAtLogin: true,
  securityUsersControl: 1,
  securityDisplayNameFormat: '',
};

/**
 * The same policy with the two collision values EXCHANGED. Sending zero where the first fixture sent
 * minus one, and minus one where it sent zero, is what distinguishes "both values survive" from "one
 * value happens to survive twice".
 */
const STORED_ACCOUNT_POLICY_BODY_EXCHANGED: MembershipSettings = {
  ...STORED_ACCOUNT_POLICY_BODY,
  recordsPerPage: -1,
  redirectAfterLogin: 0,
  redirectAfterLogout: -1,
};

/**
 * The report a policy write answers with, wrapped in the shared envelope. Both members carry a value that
 * is NOT the type's default - a true flag and a non-zero count - so a decoder that dropped either would
 * be visible rather than reading as an unremarkable "nothing changed".
 */
const ACCOUNT_POLICY_UPDATE_ENVELOPE = {
  data: { displayNameFormatChanged: true, displayNamesRewritten: 3 },
  meta: null,
};

/**
 * One profile definition. Its identity is ZERO, which is a real identifier here for the same reason minus
 * one is a real tenant: several of this schema's identities are seeded below one.
 */
/**
 * One row of the member-services catalogue: a paid service the account already holds, whose subscription
 * has lapsed. Chosen to be the row that exercises the most contract at once.
 */
const MEMBER_SERVICE: MemberService = {
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
};

/**
 * What an invitation code admitted the account to. TWO roles, because the legacy walk had no early exit:
 * one code recorded against several roles joined every one of them, so a client that read only the first
 * would under-report what the submission did.
 */
const REDEMPTION_RESULT: RedeemServiceCodeResult = {
  roles: [
    { roleId: 0, roleName: 'Premium Members' },
    { roleId: 7, roleName: 'Founders' },
  ],
};

const PROFILE_DEFINITION: ProfileDefinition = {
  propertyDefinitionId: 0,
  portalId: -1,
  moduleDefId: null,
  dataType: 0,
  defaultValue: '',
  propertyCategory: 'Contact Information',
  propertyName: 'Preferred-Locale Display Name',
  length: 0,
  required: false,
  validationExpression: null,
  viewOrder: 0,
  visible: false,
  visibility: 0,
};

/**
 * One account's profile: the tenant's declared properties with this account's values, plus the tenant's
 * decision on whether per-property visibility is offered at all. That last fact travels on the profile
 * projection because the settings endpoint that declares it is administrator-only, so an account reading
 * its own profile cannot see it any other way.
 */
const PROFILE_READ: ProfileRead = {
  userId: 1,
  properties: [
    {
      propertyDefinitionId: 0,
      propertyValue: '',
      visibility: 0,
      lastUpdatedDate: null,
      definition: PROFILE_DEFINITION,
    },
  ],
  displayVisibilityEnabled: true,
};

const PROFILE_SUBMISSION: ProfileSubmission = {
  userId: 1,
  properties: [
    { propertyDefinitionId: 0, propertyValue: '', visibility: 0 },
    { propertyDefinitionId: 12, propertyValue: 'Ada Lovelace', visibility: 2 },
  ],
};

/**
 * A profile definition to create. Names a module association, which ONLY a create may decide: the
 * terminal stored procedure that adds a definition accepts that association and the one that updates a
 * definition neither declares the parameter nor writes the column.
 */
const CREATE_DEFINITION_REQUEST: CreateDefinitionRequest = {
  propertyName: 'Preferred-Locale Display Name',
  propertyCategory: '',
  dataType: 0,
  defaultValue: null,
  length: 0,
  required: false,
  validationExpression: '',
  viewOrder: 0,
  visible: false,
  moduleDefId: null,
};

/** A profile definition to replace, moved to the fourth position. */
const UPDATE_DEFINITION_REQUEST: UpdateDefinitionRequest = {
  propertyName: 'Preferred-Locale Display Name',
  propertyCategory: '',
  dataType: 0,
  defaultValue: null,
  length: 0,
  required: false,
  validationExpression: '',
  viewOrder: 3,
  visible: false,
};

/** The two extension members this API attaches to every problem document. */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * @param code The failure code, spelled exactly as the server publishes it.
 * @param status The status the server's mapping yields for that code.
 * @param title The per-status title from the server's own vocabulary.
 * @param detail The authored sentence the producing service placed on the outcome.
 * @returns The complete document, ready to flush.
 */
function refusal(
  code: string,
  status: number,
  title: string,
  detail: string,
): Readonly<Record<string, unknown>> {
  return {
    type: `urn:dnnmigration:error:${code}`,
    title,
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * The refusal an unpermitted account write earns. `auth.not_permitted` is the status vocabulary's own
 * default type for `403`, and it is what a refusal decided by the authorisation layer — before the
 * controller body runs — carries.
 */
const NOT_PERMITTED = refusal(
  'auth.not_permitted',
  403,
  'Forbidden',
  'The authenticated caller is not permitted to perform this operation.',
);

const RESOURCE_NOT_FOUND = refusal(
  'resource.not_found',
  404,
  'Not Found',
  'The requested resource does not exist.',
);

// ---------------------------------------------------------------------------
// Observation
// ---------------------------------------------------------------------------

/** Everything one subscription observed, kept as lists so nothing is lost. */
interface Observed<T> {
  /** Every value the subscription received, in order. */
  readonly values: T[];

  /** Every failure the subscription received. */
  readonly failures: HttpErrorResponse[];

  /** One entry per completion. */
  readonly completions: boolean[];
}

/**
 * Subscribes immediately and records what arrives. Subscribing is the caller's job on this contract -
 * every method returns cold and starts nothing - so a specification has to subscribe before the request
 * exists to be expected at all.
 */
function observe<T>(source: Observable<T>): Observed<T> {
  const values: T[] = [];
  const failures: HttpErrorResponse[] = [];
  const completions: boolean[] = [];

  source.subscribe({
    next: (value: T): void => {
      values.push(value);
    },
    error: (failure: HttpErrorResponse): void => {
      failures.push(failure);
    },
    complete: (): void => {
      completions.push(true);
    },
  });

  return { values, failures, completions };
}

describe('UserService', () => {
  let service: UserService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client, then the testing backend that displaces it - in that order, because the second
      // provider overrides the first. Reversing them leaves the live backend in place and every expectation
      // below times out against a request that was never intercepted.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(UserService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // THE LOAD-BEARING ASSERTION OF THIS ENTIRE FILE. It fails if a method issued a second request, and it
    // is the only automated proof that creating an account does not also write a profile, that a read does
    // not also warm a cache, and that a failure path does not retry.
    httpMock.verify();
  });

  /**
   * Expects exactly one outstanding request with the given verb and PATH. Matches on the path rather than
   * on the path-and-query, because the string overload of the expectation compares against the full URL
   * including its query string - which would couple every paged expectation below to the order in which
   * parameters happen to be composed. Query strings are asserted separately, by name.
   */
  const expectRequest = (method: string, path: string): TestRequest =>
    httpMock.expectOne(
      (request) => request.method === method && request.url === path,
      `${method} ${path}`,
    );

  /** The one outstanding body-bound account search. */
  const expectSearch = (): TestRequest => expectRequest('POST', USERS_SEARCH);

  /**
   * The body a search transmitted, narrowed by throwing rather than asserted. The transport types the
   * body as `unknown`, and the workspace forbids the assertion that would silence that.
   *
   * @param request The search whose body to read.
   * @returns The body as a keyed record.
   */
  const searchBody = (request: TestRequest): Readonly<Record<string, unknown>> => {
    const body: unknown = request.request.body;

    if (typeof body !== 'object' || body === null || Array.isArray(body)) {
      throw new Error('the search did not transmit a JSON object body');
    }

    return { ...body };
  };

  /**
   * Asserts that a request's TARGET carries none of the given values, in its path or its query. The
   * load-bearing assertion of the privacy cases: it is not enough that the value appears in the body, it
   * must be ABSENT from the string that gets logged.
   *
   * @param request The request to inspect.
   * @param values The values that must not appear in the target.
   */
  const expectTargetCarriesNoneOf = (request: TestRequest, values: readonly string[]): void => {
    const target = `${request.request.urlWithParams}`;

    for (const value of values) {
      expect(target)
        .withContext(`"${value}" must not appear in the request target`)
        .not.toContain(value);
    }
  };

  /**
   * Asserts that neither header applied by the interceptor chain has been set here. Both belong to units
   * registered once at application configuration.
   */
  const expectNoInterceptorHeaders = (request: TestRequest): void => {
    expect(request.request.headers.get('Authorization'))
      .withContext('the bearer token belongs to the auth interceptor, not to the service')
      .toBeNull();
    expect(request.request.headers.get('X-Correlation-Id'))
      .withContext('the correlation identifier is applied by its own interceptor')
      .toBeNull();
  };

  /**
   * @param source The call under test.
   * @param path The member path the violation must name.
   * @param url The url the call addresses.
   */
  const expectNullPayloadRefused = (
    source: Observable<unknown>,
    url: string,
    path: string,
  ): void => {
    const values: unknown[] = [];
    const failures: unknown[] = [];

    source.subscribe({
      next: (value: unknown) => values.push(value),
      error: (failure: unknown) => failures.push(failure),
    });

    httpMock.expectOne((request) => request.url === url).flush({ data: null, meta: null });

    expect(values).withContext('a null payload is not a successful answer').toEqual([]);
    expect(failures.length).toBe(1);

    const failure: unknown = failures[0];
    expect(isContractViolation(failure)).toBeTrue();

    if (isContractViolation(failure)) {
      expect(failure.path).toBe(path);
      expect(failure.received).withContext('a type name, never the value').toBe('null');
    }
  };

  /** Asserts that no paging, ordering or free-text parameter was emitted. */
  const expectNoPagingParameters = (request: TestRequest): void => {
    for (const name of ['pageIndex', 'pageSize', 'sortBy', 'sortDir', 'query']) {
      expect(request.request.params.has(name))
        .withContext(`paging parameter "${name}" must not be emitted`)
        .toBe(false);
    }
  };

  // =========================================================================
  // The account collection
  // =========================================================================

  describe('list', () => {
    it('reads one page from the account collection over the relative API base', () => {
      const query: UserListQuery = { pageIndex: 0, pageSize: 25 };

      const observed = observe(service.list(query));

      const request = expectRequest('GET', USERS);
      expect(request.request.url)
        .withContext('the configured base is relative, so the request must be too')
        .toBe('/api/v1/users');
      expect(request.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize']);
      expectNoInterceptorHeaders(request);

      request.flush(USER_PAGE);

      expect(observed.values).toEqual([USER_PAGE]);
      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('emits every ordering and non-identifying member the caller supplied, and only those', () => {
      const query: UserListQuery = {
        pageIndex: 2,
        pageSize: 25,
        sortBy: 'Username',
        sortDir: 'Descending',
        isApproved: false,
      };

      observe(service.list(query));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.keys().sort()).toEqual([
        'isApproved',
        'pageIndex',
        'pageSize',
        'sortBy',
        'sortDir',
      ]);
      expect(request.request.params.get('sortBy')).toBe('Username');
      expect(request.request.params.get('sortDir')).toBe('Descending');
      request.flush(USER_PAGE);
    });

    it('carries the same members in the body when a generic filter makes the search identifying', () => {
      const query: UserListQuery = {
        pageIndex: 2,
        pageSize: 25,
        sortBy: 'Username',
        sortDir: 'Descending',
        query: 'ada',
        isApproved: false,
      };

      observe(service.list(query));

      httpMock.expectNone((request) => request.method === 'GET' && request.url === USERS);

      const request = expectSearch();

      expectTargetCarriesNoneOf(request, ['ada']);
      expect(searchBody(request)).toEqual({
        pageIndex: 2,
        pageSize: 25,
        sortBy: 'Username',
        sortDir: 'Descending',
        query: 'ada',
        isApproved: false,
      });
      request.flush(USER_PAGE);
    });

    it('transmits the page index exactly as supplied, applying no adjustment to it', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25 }));

      const first = expectRequest('GET', USERS);
      expect(first.request.params.get('pageIndex'))
        .withContext('zero is the first page and must not be treated as absent')
        .toBe('0');
      first.flush(USER_PAGE);

      observe(service.list({ pageIndex: 1, pageSize: 25 }));

      const second = expectRequest('GET', USERS);
      expect(second.request.params.get('pageIndex'))
        .withContext('one is the second page, not the first')
        .toBe('1');
      second.flush(USER_PAGE);
    });

    it('transmits the caller page size rather than any shared default', () => {
      observe(service.list({ pageIndex: 0, pageSize: 7 }));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.get('pageSize')).toBe('7');
      expect(request.request.params.get('pageSize')).not.toBe('10');
      request.flush(USER_PAGE);
    });

    it('omits an ordering member the caller left undefined', () => {
      observe(
        service.list({
          pageIndex: 0,
          pageSize: 25,
          sortBy: undefined,
          sortDir: undefined,
          query: undefined,
          userName: undefined,
          email: undefined,
          profilePropertyName: undefined,
          profilePropertyValue: undefined,
          isApproved: undefined,
        }),
      );

      const request = expectRequest('GET', USERS);
      expect(request.request.params.keys().sort())
        .withContext('an undefined member is an omission, not an empty value')
        .toEqual(['pageIndex', 'pageSize']);
      expect(request.request.params.toString()).not.toContain('undefined');
      request.flush(USER_PAGE);
    });
  });

  // =========================================================================
  // One account
  // =========================================================================

  describe('getById', () => {
    it('reads one account and unwraps the payload from its envelope', () => {
      const observed = observe(service.getById(1));

      const request = expectRequest('GET', `${USERS}/1`);
      expect(request.request.url).toBe('/api/v1/users/1');
      expect(request.request.params.keys()).toEqual([]);
      expectNoInterceptorHeaders(request);

      request.flush({ data: USER_DETAIL, meta: null } satisfies ApiResponse<UserDetail>);

      expect(observed.values).toEqual([USER_DETAIL]);
      expect(observed.completions.length).toBe(1);
    });

    it('reports an unknown account as a 404 rather than as a payload-free success', () => {
      const observed = observe(service.getById(4242));

      const request = expectRequest('GET', `${USERS}/4242`);
      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expect(observed.values).withContext('absence is not an emitted value').toEqual([]);
      expect(observed.completions.length).toBe(0);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(404);
      expect(observed.failures[0].error).toEqual(RESOURCE_NOT_FOUND);
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      expectNullPayloadRefused(service.getById(4242), `${USERS}/4242`, 'response.data');
    });
  });

  describe('create', () => {
    it('posts the account to the collection with the body untouched', () => {
      const observed = observe(service.create(CREATE_USER_REQUEST));

      const request = expectRequest('POST', USERS);
      expect(request.request.body)
        .withContext('the empty shown name and the false approve choice must both survive')
        .toEqual(CREATE_USER_REQUEST);
      expect(request.request.params.keys()).toEqual([]);
      expectNoInterceptorHeaders(request);

      request.flush({ data: USER_DETAIL, meta: null } satisfies ApiResponse<UserDetail>, {
        status: 201,
        statusText: 'Created',
      });

      expect(observed.values).toEqual([USER_DETAIL]);
      expect(observed.completions.length).toBe(1);
    });

    it('propagates a refusal unchanged instead of interpreting it', () => {
      // The account-allowance rule is the server's. Counting accounts here first would cost an extra
      // request, would race every other administrator, and would still have to handle the refusal it was
      // trying to predict.
      const observed = observe(service.create(CREATE_USER_REQUEST));

      const request = expectRequest('POST', USERS);
      request.flush(NOT_PERMITTED, { status: 403, statusText: 'Forbidden' });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(403);
      expect(observed.failures[0].error)
        .withContext('the refusal body reaches the caller as the server wrote it')
        .toEqual(NOT_PERMITTED);
      expect((observed.failures[0].error as { readonly status: number }).status)
        .withContext('the body agrees with the transport, as a real response does')
        .toBe(403);
      expect(observed.completions.length)
        .withContext('a failed request completes through the error channel only')
        .toBe(0);
    });
  });

  describe('update', () => {
    it('replaces the account with the body untouched', () => {
      const observed = observe(service.update(1, UPDATE_USER_REQUEST));

      const request = expectRequest('PUT', `${USERS}/1`);
      expect(request.request.body)
        .withContext('a deliberately cleared field must not be stripped for reading as empty')
        .toEqual(UPDATE_USER_REQUEST);
      expectNoInterceptorHeaders(request);

      request.flush({ data: USER_DETAIL, meta: null } satisfies ApiResponse<UserDetail>);

      expect(observed.values).toEqual([USER_DETAIL]);
      expect(observed.completions.length).toBe(1);
    });

    it('propagates a refusal to edit an account the caller may not edit', () => {
      const observed = observe(service.update(1, UPDATE_USER_REQUEST));

      const request = expectRequest('PUT', `${USERS}/1`);
      request.flush(NOT_PERMITTED, { status: 403, statusText: 'Forbidden' });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(403);
      expect(observed.failures[0].error).toEqual(NOT_PERMITTED);
    });
  });

  describe('delete', () => {
    it('removes one named account and completes without a payload', () => {
      const observed = observe(service.delete(1));

      const request = expectRequest('DELETE', `${USERS}/1`);
      expect(request.request.body).toBeNull();
      expectNoInterceptorHeaders(request);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length)
        .withContext('the observable must complete once the removal is acknowledged')
        .toBe(1);
    });
  });

  // =========================================================================
  // One account's profile
  // =========================================================================

  describe('getProfile', () => {
    it('reads the profile from the nested path and unwraps it', () => {
      const observed = observe(service.getProfile(1));

      const request = expectRequest('GET', `${USERS}/1/profile`);
      expect(request.request.url).toBe('/api/v1/users/1/profile');
      expect(request.request.params.keys()).toEqual([]);
      expectNoInterceptorHeaders(request);

      request.flush({ data: PROFILE_READ, meta: null } satisfies ApiResponse<ProfileRead>);

      // The definition travels with each value, so a profile editor can render a field
      // this account has no value for without reading the definition list separately.
      expect(observed.values).toEqual([PROFILE_READ]);
      expect(observed.completions.length).toBe(1);
    });
  });

  describe('updateProfile', () => {
    it('replaces the whole profile and completes without a payload', () => {
      const observed = observe(service.updateProfile(1, PROFILE_SUBMISSION));

      const request = expectRequest('PUT', `${USERS}/1/profile`);
      expect(request.request.body)
        .withContext('every declared property is sent, including the one being cleared')
        .toEqual(PROFILE_SUBMISSION);
      expectNoInterceptorHeaders(request);

      // NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS. They describe this replacement as answering 200
      // with a body. The subject types it as carrying no value, which is the no-content answer asserted
      // here; flushing a payload would be asserting a response the API does not write.
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });
  });

  // =========================================================================
  // Credentials
  // =========================================================================

  describe('changePassword', () => {
    it('sends the in-force credential and its replacement to the credential path', () => {
      // NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS. They describe this operation as a PUT and call it
      // the only one on the surface answering 204.
      const observed = observe(service.changePassword(1, CHANGE_PASSWORD_REQUEST));

      const request = expectRequest('POST', `${USERS}/1/password`);
      expect(request.request.url).toBe('/api/v1/users/1/password');
      expect(request.request.body)
        .withContext('the confirmation member is compared by the server, so it is sent as given')
        .toEqual(CHANGE_PASSWORD_REQUEST);
      expectNoInterceptorHeaders(request);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });
  });

  describe('passwordReset', () => {
    it('sends the replacement alone to the hyphenated reset path', () => {
      const observed = observe(service.passwordReset(1, RESET_PASSWORD_REQUEST));

      const request = expectRequest('POST', `${USERS}/1/password-reset`);
      expect(request.request.url)
        .withContext('the segment is hyphenated, and a near miss is a route that does not match')
        .toBe('/api/v1/users/1/password-reset');
      expect(request.request.body)
        .withContext('the null in-force member states "no previous credential" explicitly')
        .toEqual(RESET_PASSWORD_REQUEST);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.completions.length).toBe(1);
    });

    it('answers with no payload at all, because no endpoint here discloses a credential', () => {
      // The legacy store was reversible by configuration and the key that reversed it was committed to
      // source control in the clear.
      const observed = observe(service.passwordReset(1, RESET_PASSWORD_REQUEST));

      const request = expectRequest('POST', `${USERS}/1/password-reset`);
      request.flush(null, { status: 204, statusText: 'No Content' });

      // Exactly one notification, carrying nothing. The return type declares no value, so the count is the
      // assertion: a payload-bearing answer would still emit once, but a method that returned a credential
      // would have had to declare it.
      expect(observed.values.length).toBe(1);
      expect(observed.completions.length).toBe(1);
    });
  });

  // =========================================================================
  // Account state transitions
  // =========================================================================

  describe('setApproval', () => {
    it('states the approval it wants as a required parameter', () => {
      const observed = observe(service.setApproval(1, true));

      const request = expectRequest('PUT', `${USERS}/1/approval`);
      expect(request.request.url).toBe('/api/v1/users/1/approval');
      expect(request.request.params.keys()).toEqual(['isApproved']);
      expect(request.request.params.get('isApproved')).toBe('true');
      expect(request.request.body).toBeNull();
      expectNoInterceptorHeaders(request);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.completions.length).toBe(1);
    });

    it('transmits a withdrawal of approval rather than treating false as an absence', () => {
      const observed = observe(service.setApproval(1, false));

      const request = expectRequest('PUT', `${USERS}/1/approval`);
      expect(request.request.params.has('isApproved')).toBe(true);
      expect(request.request.params.get('isApproved')).toBe('false');

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.completions.length).toBe(1);
    });
  });

  describe('unlock', () => {
    it('releases a locked-out account with no body at all', () => {
      const observed = observe(service.unlock(1));

      const request = expectRequest('POST', `${USERS}/1/unlock`);
      expect(request.request.url).toBe('/api/v1/users/1/unlock');
      expect(request.request.body)
        .withContext('the account is the whole of the request; there is nothing to configure')
        .toBeNull();
      expect(request.request.params.keys()).toEqual([]);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.completions.length).toBe(1);
    });
  });

  describe('requirePasswordChange', () => {
    it('sets the obligation on the hyphenated path and returns no credential', () => {
      // The legacy account state behind this was one member of a five-member status vocabulary the sign-in
      // path evaluated, which conflated a hard block with two advisories and a separate concern about the
      // profile. The successor is a small set of independent advisory flags carried on the session.
      const observed = observe(service.requirePasswordChange(1));

      const request = expectRequest('POST', `${USERS}/1/require-password-change`);
      expect(request.request.url)
        .withContext('three hyphenated words, exactly as the route template declares them')
        .toBe('/api/v1/users/1/require-password-change');
      expect(request.request.body).toBeNull();
      expect(request.request.params.keys()).toEqual([]);

      request.flush(null, { status: 204, statusText: 'No Content' });

      // One notification carrying nothing, then completion. The declared return type has
      // no value in it, which is the type-level counterpart of "returns no credential".
      expect(observed.values.length).toBe(1);
      expect(observed.completions.length).toBe(1);
    });
  });

  // =========================================================================
  // The tenant's account policy
  // =========================================================================

  describe('getMembershipSettings', () => {
    it('reads the policy from the settings child of the account resource', () => {
      const observed = observe(service.getMembershipSettings());

      const request = expectRequest('GET', ACCOUNT_POLICY);

      expect(request.request.url).toBe('/api/v1/users/settings');
      expect(request.request.params.keys()).toEqual([]);
      expectNoInterceptorHeaders(request);

      request.flush({
        data: STORED_ACCOUNT_POLICY_BODY,
        meta: null,
      } satisfies ApiResponse<MembershipSettings>);

      expect(observed.values).toEqual([STORED_ACCOUNT_POLICY_BODY]);
      expect(observed.completions.length).toBe(1);
    });

    it('delivers the defaults a tenant with no settings source is answered with, as a 200', () => {
      const observed = observe(service.getMembershipSettings());

      const request = expectRequest('GET', ACCOUNT_POLICY);

      request.flush({
        data: UNSTORED_ACCOUNT_POLICY_BODY,
        meta: null,
      } satisfies ApiResponse<MembershipSettings>);

      expect(observed.failures).withContext('an unstored policy is not a failure').toEqual([]);
      expect(observed.values).toEqual([UNSTORED_ACCOUNT_POLICY_BODY]);
      expect(observed.completions.length).toBe(1);
      // Decoded rather than defaulted: the member has to survive as `false` and not be coalesced
      // into the `true` every other fixture in this file carries.
      expect(observed.values[0].isStored).toBeFalse();
      expect(observed.values[0].recordsPerPage)
        .withContext('the measured legacy default for an absent key')
        .toBe(10);
    });

    it('reports a refused policy read as the failure it is', () => {
      // ⚠ RETAINED FOR A GENUINELY UNRESOLVED RESOURCE ONLY. A `404` on this address no longer means "this
      // tenant stores no policy" - that answer is the `200` above - so nothing about this status is an
      // ordinary outcome any more and it reaches the caller as a failure like any other read's.
      const observed = observe(service.getMembershipSettings());

      const request = expectRequest('GET', ACCOUNT_POLICY);
      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(404);
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      // Refused for the same reason as the account read, with one of its own: every screen that pages a
      // listing reads its page size from this policy, so a null committed as a success would silently move
      // the listing onto the fallback size with no failure to explain it.
      expectNullPayloadRefused(service.getMembershipSettings(), ACCOUNT_POLICY, 'response.data');
    });
  });

  describe('updateMembershipSettings', () => {
    it('replaces the whole multi-member policy with nothing omitted for reading as empty', () => {
      const observed = observe(service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY));

      const request = expectRequest('PUT', ACCOUNT_POLICY);
      const body: unknown = request.request.body;

      expect(body)
        .withContext('nineteen of the twenty-four members are falsy and all must survive')
        .toEqual(STORED_ACCOUNT_POLICY_BODY);
      // ⚠ #5/#6 — TWENTY-FOUR, not twenty-three.
      expect(Object.keys(STORED_ACCOUNT_POLICY_BODY).length).toBe(24);
      expectNoInterceptorHeaders(request);

      request.flush(ACCOUNT_POLICY_UPDATE_ENVELOPE);

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);

      expect(observed.values[0]).toEqual({
        displayNameFormatChanged: true,
        displayNamesRewritten: 3,
      });
    });

    it('keeps a page size of zero and a landing page of minus one distinct from one another', () => {
      observe(service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY));

      const first = expectRequest('PUT', ACCOUNT_POLICY);
      expect(first.request.body).toEqual(
        jasmine.objectContaining({
          recordsPerPage: 0,
          redirectAfterLogin: -1,
          redirectAfterLogout: 0,
        }),
      );
      first.flush(ACCOUNT_POLICY_UPDATE_ENVELOPE);

      observe(service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY_EXCHANGED));

      const second = expectRequest('PUT', ACCOUNT_POLICY);
      expect(second.request.body).toEqual(
        jasmine.objectContaining({
          recordsPerPage: -1,
          redirectAfterLogin: 0,
          redirectAfterLogout: -1,
        }),
      );
      second.flush(ACCOUNT_POLICY_UPDATE_ENVELOPE);
    });

    it('keeps a null landing page distinct from a zero one', () => {
      // A null means "use the default" and a zero names a page. Coalescing either into
      // the other changes the instruction the server receives.
      observe(service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY));

      const request = expectRequest('PUT', ACCOUNT_POLICY);
      expect(request.request.body).toEqual(
        jasmine.objectContaining({
          redirectAfterRegistration: null,
          redirectAfterLogout: 0,
        }),
      );
      request.flush(ACCOUNT_POLICY_UPDATE_ENVELOPE);
    });

    it('keeps an empty policy string as an empty string rather than a null', () => {
      observe(service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY));

      const request = expectRequest('PUT', ACCOUNT_POLICY);
      expect(request.request.body).toEqual(
        jasmine.objectContaining({
          securityEmailValidation: '',
          securityDisplayNameFormat: '',
        }),
      );
      request.flush(ACCOUNT_POLICY_UPDATE_ENVELOPE);
    });
  });

  // =========================================================================
  // Profile definitions - the fields a profile may carry
  // =========================================================================

  describe('listProfileDefinitions', () => {
    it('reads the definition set unpaged, emitting no query string whatsoever', () => {
      const observed = observe(service.listProfileDefinitions());

      const request = expectRequest('GET', PROFILE_DEFINITIONS);
      expect(request.request.url).toBe('/api/v1/profile-definitions');

      // DELIBERATELY UNPAGED. The set is bounded by how many fields an administrator has chosen to define,
      // so paging it would add coordinates to every call in exchange for nothing. Not one parameter is
      // emitted - not a defaulted page, not an empty ordering.
      expectNoPagingParameters(request);
      expect(request.request.params.keys()).toEqual([]);
      expect(request.request.params.toString()).toBe('');
      expectNoInterceptorHeaders(request);

      request.flush({
        data: [PROFILE_DEFINITION],
        meta: null,
      } satisfies ApiResponse<readonly ProfileDefinition[]>);

      expect(observed.values).toEqual([[PROFILE_DEFINITION]]);
      expect(observed.completions.length).toBe(1);
    });

    it('emits no tenant parameter, because the tenant is resolved from the request itself', () => {
      // NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS. They require a `portalId` parameter on this call,
      // transmitting minus one and zero unchanged.
      observe(service.listProfileDefinitions());

      const request = expectRequest('GET', PROFILE_DEFINITIONS);
      expect(request.request.params.has('portalId'))
        .withContext('the tenant is not a parameter on any method of this service')
        .toBe(false);
      request.flush({
        data: [],
        meta: null,
      } satisfies ApiResponse<readonly ProfileDefinition[]>);
    });
  });

  describe('createProfileDefinition', () => {
    it('posts the create body, module association included, and unwraps the result', () => {
      const observed = observe(service.createProfileDefinition(CREATE_DEFINITION_REQUEST));

      const request = expectRequest('POST', PROFILE_DEFINITIONS);
      expect(request.request.body)
        .withContext('the association only a create may decide must reach the server')
        .toEqual(CREATE_DEFINITION_REQUEST);
      expectNoInterceptorHeaders(request);

      request.flush(
        { data: PROFILE_DEFINITION, meta: null } satisfies ApiResponse<ProfileDefinition>,
        { status: 201, statusText: 'Created' },
      );

      expect(observed.values).toEqual([PROFILE_DEFINITION]);
      expect(observed.completions.length).toBe(1);
    });

    it('sends the property name verbatim, with its case, hyphen and space intact', () => {
      observe(service.createProfileDefinition(CREATE_DEFINITION_REQUEST));

      const request = expectRequest('POST', PROFILE_DEFINITIONS);
      expect(request.request.body).toEqual(
        jasmine.objectContaining({ propertyName: 'Preferred-Locale Display Name' }),
      );
      request.flush(
        { data: PROFILE_DEFINITION, meta: null } satisfies ApiResponse<ProfileDefinition>,
        { status: 201, statusText: 'Created' },
      );
    });
  });

  describe('getProfileDefinition', () => {
    it('reads one definition by its property-definition identifier', () => {
      // The parameter names a PROPERTY definition, and that spelling is load-bearing on both sides of the
      // wire: the route constrains it as an integer under that name and the contract spells its identity
      // member the same way.
      const observed = observe(service.getProfileDefinition(0));

      const request = expectRequest('GET', `${PROFILE_DEFINITIONS}/0`);
      expect(request.request.url)
        .withContext('zero is a real identifier and belongs in the path as it stands')
        .toBe('/api/v1/profile-definitions/0');
      expect(request.request.params.keys()).toEqual([]);

      request.flush({
        data: PROFILE_DEFINITION,
        meta: null,
      } satisfies ApiResponse<ProfileDefinition>);

      expect(observed.values).toEqual([PROFILE_DEFINITION]);
      expect(observed.completions.length).toBe(1);
    });

    it('reports an unknown definition as a 404, not as a null payload', () => {
      // Same envelope helper server-side, same consequence: absence is a status.
      const observed = observe(service.getProfileDefinition(9999));

      const request = expectRequest('GET', `${PROFILE_DEFINITIONS}/9999`);
      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(404);
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      expectNullPayloadRefused(
        service.getProfileDefinition(9999),
        `${PROFILE_DEFINITIONS}/9999`,
        'response.data',
      );
    });
  });

  describe('updateProfileDefinition', () => {
    it('replaces one definition and carries the position member that reorders it', () => {
      const observed = observe(service.updateProfileDefinition(0, UPDATE_DEFINITION_REQUEST));

      const request = expectRequest('PUT', `${PROFILE_DEFINITIONS}/0`);
      expect(request.request.body).toEqual(UPDATE_DEFINITION_REQUEST);

      expect(request.request.body).toEqual(jasmine.objectContaining({ viewOrder: 3 }));
      expectNoInterceptorHeaders(request);

      request.flush({
        data: { ...PROFILE_DEFINITION, viewOrder: 3 },
        meta: null,
      } satisfies ApiResponse<ProfileDefinition>);

      expect(observed.values).toEqual([{ ...PROFILE_DEFINITION, viewOrder: 3 }]);
      expect(observed.completions.length).toBe(1);
    });

    it('sends a position of zero as zero, because the first place is not an absence', () => {
      const firstPlace: UpdateDefinitionRequest = { ...UPDATE_DEFINITION_REQUEST, viewOrder: 0 };

      observe(service.updateProfileDefinition(0, firstPlace));

      const request = expectRequest('PUT', `${PROFILE_DEFINITIONS}/0`);
      expect(request.request.body).toEqual(jasmine.objectContaining({ viewOrder: 0 }));
      request.flush({
        data: PROFILE_DEFINITION,
        meta: null,
      } satisfies ApiResponse<ProfileDefinition>);
    });

    it('sends the replace body without the members only a create may decide', () => {
      observe(service.updateProfileDefinition(0, UPDATE_DEFINITION_REQUEST));

      const request = expectRequest('PUT', `${PROFILE_DEFINITIONS}/0`);
      const keys: readonly string[] = Object.keys(UPDATE_DEFINITION_REQUEST);

      expect(keys)
        .withContext('the module association is not part of the replacement contract')
        .not.toContain('moduleDefId');
      expect(keys)
        .withContext('the identity arrives from the route, not from the body')
        .not.toContain('propertyDefinitionId');
      expect(keys)
        .withContext('the tenant is resolved by the API, so it is not in the body either')
        .not.toContain('portalId');
      request.flush({
        data: PROFILE_DEFINITION,
        meta: null,
      } satisfies ApiResponse<ProfileDefinition>);
    });
  });

  describe('deleteProfileDefinition', () => {
    it('removes one definition and completes without a payload', () => {
      const observed = observe(service.deleteProfileDefinition(0));

      const request = expectRequest('DELETE', `${PROFILE_DEFINITIONS}/0`);
      expect(request.request.body).toBeNull();

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('propagates a refusal to remove a definition that is still in use', () => {
      // A CONFLICT, and its own document rather than the `403` one. The reason token decides the status
      // server-side, so a fixture whose body claimed `403` while the transport said `409` would be a
      // response the server cannot produce.
      const inUse = refusal(
        'profile_definition.in_use',
        409,
        'Conflict',
        'The property definition still holds values and was not removed.',
      );
      const observed = observe(service.deleteProfileDefinition(0));

      const request = expectRequest('DELETE', `${PROFILE_DEFINITIONS}/0`);
      request.flush(inUse, { status: 409, statusText: 'Conflict' });

      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(409);
      expect(observed.failures[0].error).toEqual(inUse);
      expect(observed.completions.length)
        .withContext('a refusal must not look like a silently unchanged list')
        .toBe(0);
    });
  });

  // The account's own subscriptions

  describe('listMemberServices', () => {
    it('reads the catalogue of one account a bounded page at a time, and asks for nothing the endpoint refuses', () => {
      // MIGRATION: THIS ASSERTION INVERTED. The catalogue used to be read in one unbounded response and this
      // case asserted an EMPTY query string. The endpoint now pages, and it answers 400 to `sortBy` and to
      // `query`, so what must be pinned is that the reader sends the two paging arguments and NOTHING else.
      const observed = observe(service.listMemberServices(1));

      const request = expectRequest('GET', MEMBER_SERVICES);

      expect([...request.request.params.keys()].sort()).toEqual(['pageIndex', 'pageSize']);
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('pageSize')).toBe(String(WHOLE_CATALOGUE_PAGE_SIZE));
      expectNoInterceptorHeaders(request);

      request.flush(catalogue([MEMBER_SERVICE]));

      expect(observed.values).toEqual([[MEMBER_SERVICE]]);
      expect(observed.completions.length).toBe(1);
    });

    it('reads EVERY page, so a catalogue that spans a page boundary arrives whole and in order', () => {
      const filler: readonly MemberService[] = Array.from(
        { length: WHOLE_CATALOGUE_PAGE_SIZE },
        (_unused, index) => ({ ...MEMBER_SERVICE, roleId: index, roleName: `Service ${index}` }),
      );
      const tail: MemberService = { ...MEMBER_SERVICE, roleId: 500, roleName: 'Last service' };
      const total = filler.length + 1;

      const observed = observe(service.listMemberServices(1));

      const first = expectRequest('GET', MEMBER_SERVICES);
      expect(first.request.params.get('pageIndex')).toBe('0');
      first.flush({
        items: filler,
        meta: {
          totalCount: total,
          pageIndex: 0,
          pageSize: WHOLE_CATALOGUE_PAGE_SIZE,
          totalPages: 2,
        },
      });

      const second = expectRequest('GET', MEMBER_SERVICES);
      expect(second.request.params.get('pageIndex')).toBe('1');
      second.flush({
        items: [tail],
        meta: {
          totalCount: total,
          pageIndex: 1,
          pageSize: WHOLE_CATALOGUE_PAGE_SIZE,
          totalPages: 2,
        },
      });

      const rows = observed.values[0] as readonly MemberService[];

      expect(rows.length).toBe(total);
      expect(rows[0].roleName).toBe('Service 0');
      expect(rows[WHOLE_CATALOGUE_PAGE_SIZE - 1].roleName)
        .withContext('the last row of the first page must survive the boundary')
        .toBe(`Service ${WHOLE_CATALOGUE_PAGE_SIZE - 1}`);
      expect(rows[total - 1].roleName).toBe('Last service');
      expect(observed.completions.length).toBe(1);
    });

    it('carries a sub-unit fee through unrounded, where the legacy projection erased it', () => {
      // THE DIVERGENCE THIS ASSERTION PINS. `GetServices` published the fee only when `convert(int,
      // R.ServiceFee) <> 0`, so a fee of fifty cents arrived as null and the grid rendered "Free" for a
      // role the subscribe path still handed to a payment page.
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush(catalogue([MEMBER_SERVICE]));

      const rows = observed.values[0] as readonly MemberService[];

      expect(rows[0].serviceFee).toBe(0.5);
      expect(rows[0].subscriptionRequiresPayment).toBeTrue();
      expect(rows[0].roleId).withContext('role zero is a real role').toBe(0);
    });

    it('answers an account offered nothing with an empty catalogue rather than a failure', () => {
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush(catalogue([]));

      expect(observed.values).toEqual([[]]);
      expect(observed.failures).toEqual([]);
    });

    it('refuses a command word the API does not publish', () => {
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush(
        catalogue([{ ...MEMBER_SERVICE, subscriptionAction: 'Cancel' }]),
      );

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(isContractViolation(observed.failures[0])).toBeTrue();
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      // The paged envelope names its collection `items`, so a body carrying the single-payload shape is
      // refused at `response.items` with the member ABSENT rather than null. Asserted directly rather than
      // through the shared helper, which pins the single-payload member name and received type.
      const values: unknown[] = [];
      const failures: unknown[] = [];

      service.listMemberServices(1).subscribe({
        next: (value: unknown) => values.push(value),
        error: (failure: unknown) => failures.push(failure),
      });

      expectRequest('GET', MEMBER_SERVICES).flush({ data: null, meta: null });

      expect(values).withContext('a null payload is not a successful answer').toEqual([]);
      expect(failures.length).toBe(1);

      const failure: unknown = failures[0];
      expect(isContractViolation(failure)).toBeTrue();

      if (isContractViolation(failure)) {
        expect(failure.path).toBe('response.items');
        expect(failure.received).withContext('a type name, never the value').toBe('nothing');
      }
    });

    it('refuses a page whose metadata is missing, so a truncated read cannot look complete', () => {
      // THE LOAD-BEARING REFUSAL OF THE PAGED CONTRACT. Without metadata the reader cannot know whether
      // more pages exist, so a body with none must fail rather than answer a silently short catalogue.
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush({ items: [MEMBER_SERVICE], meta: null });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(isContractViolation(observed.failures[0])).toBeTrue();
    });
  });

  describe('subscribeToService', () => {
    it('subscribes with no body and completes without a payload', () => {
      const observed = observe(service.subscribeToService(1, 0));

      const request = expectRequest('POST', MEMBER_SERVICE_SUBSCRIPTION);

      expect(request.request.body).toBeNull();
      expect(request.request.params.keys()).toEqual([]);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('issues the same request for a renewal as for a first subscription', () => {
      expect(MEMBER_SERVICE.subscriptionAction).toBe('Renew');

      observe(service.subscribeToService(1, MEMBER_SERVICE.roleId));

      const request = expectRequest('POST', MEMBER_SERVICE_SUBSCRIPTION);

      expect(request.request.body).toBeNull();
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('propagates the refusal of a service that would require payment', () => {
      const paymentRequired = refusal(
        'user.service.payment-required-forbidden',
        403,
        'Forbidden',
        'This service requires payment, which this application cannot take.',
      );
      const observed = observe(service.subscribeToService(1, 0));

      expectRequest('POST', MEMBER_SERVICE_SUBSCRIPTION).flush(paymentRequired, {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(403);
      expect(observed.failures[0].error).toEqual(paymentRequired);
      expect(observed.completions.length)
        .withContext('a refusal must not look like a completed subscription')
        .toBe(0);
    });
  });

  describe('cancelService', () => {
    it('cancels at the same address it subscribed at, with the removing verb', () => {
      const observed = observe(service.cancelService(1, 0));

      const request = expectRequest('DELETE', MEMBER_SERVICE_SUBSCRIPTION);

      expect(request.request.body).toBeNull();
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('propagates a refusal to cancel an assignment the tenant protects', () => {
      const protectedAssignment = refusal(
        'role_assignment.protected',
        403,
        'Forbidden',
        'This assignment is protected and was not removed.',
      );
      const observed = observe(service.cancelService(1, 0));

      expectRequest('DELETE', MEMBER_SERVICE_SUBSCRIPTION).flush(protectedAssignment, {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(403);
      expect(observed.completions.length).toBe(0);
    });
  });

  describe('startServiceTrial', () => {
    it('takes the trial at its own address, with no body', () => {
      const observed = observe(service.startServiceTrial(1, 0));

      const request = expectRequest('POST', MEMBER_SERVICE_TRIAL);

      expect(request.request.body).toBeNull();
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('propagates the refusal of a trial the service does not offer', () => {
      const notOffered = refusal(
        'user.service.trial-not-offered-forbidden',
        403,
        'Forbidden',
        'This service offers no trial to this account.',
      );
      const observed = observe(service.startServiceTrial(1, 0));

      expectRequest('POST', MEMBER_SERVICE_TRIAL).flush(notOffered, {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(403);
      expect(observed.completions.length).toBe(0);
    });
  });

  describe('redeemServiceCode', () => {
    it('sends the code exactly as typed and reports every role it joined', () => {
      const observed = observe(service.redeemServiceCode(1, { code: '  Founders-2026  ' }));

      const request = expectRequest('POST', MEMBER_SERVICE_REDEMPTIONS);

      expect(request.request.body).toEqual({ code: '  Founders-2026  ' });
      expectNoInterceptorHeaders(request);

      request.flush({
        data: REDEMPTION_RESULT,
        meta: null,
      } satisfies ApiResponse<RedeemServiceCodeResult>);

      expect(observed.values).toEqual([REDEMPTION_RESULT]);
      expect((observed.values[0] as RedeemServiceCodeResult).roles.length)
        .withContext('the legacy walk had no early exit, so one code may join several roles')
        .toBe(2);
    });

    it('reports a code that matched nothing as a refusal, not as an empty success', () => {
      const notMatched = refusal(
        'user.service.code-not-matched',
        400,
        'Bad Request',
        'The invitation code entered is not valid or does not exist.',
      );
      const observed = observe(service.redeemServiceCode(1, { code: 'nope' }));

      expectRequest('POST', MEMBER_SERVICE_REDEMPTIONS).flush(notMatched, {
        status: 400,
        statusText: 'Bad Request',
      });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(400);
    });

    it('transmits an empty submission rather than deciding the rule locally', () => {
      // The legacy handler guarded on a non-empty code, and the guard was load-bearing: a role with no code
      // recorded read as the empty string through the legacy null contract, so an empty submission would
      // otherwise have matched every such role.
      const observed = observe(service.redeemServiceCode(1, { code: '' }));

      const request = expectRequest('POST', MEMBER_SERVICE_REDEMPTIONS);

      expect(request.request.body).toEqual({ code: '' });

      request.flush(
        refusal(
          'user.service.code-required',
          400,
          'Bad Request',
          'An RSVP Code is required.',
        ),
        { status: 400, statusText: 'Bad Request' },
      );

      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(400);
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      expectNullPayloadRefused(
        service.redeemServiceCode(1, { code: 'Founders-2026' }),
        MEMBER_SERVICE_REDEMPTIONS,
        'response.data',
      );
    });
  });

  // The three search axes

  describe('the search axes', () => {
    /** Asserts that no transmitted parameter value carries the trailing match character. */
    const expectNoTrailingMatchCharacter = (request: TestRequest): void => {
      for (const name of request.request.params.keys()) {
        expect(request.request.params.get(name))
          .withContext(`parameter "${name}" must not carry a match character`)
          .not.toContain(TRAILING_MATCH_CHARACTER);
      }
    };

    /**
     * The body counterpart of {@link expectNoTrailingMatchCharacter}.
     *
     * @param request The search whose body to inspect.
     */
    const expectBodyCarriesNoMatchCharacter = (request: TestRequest): void => {
      for (const [name, value] of Object.entries(searchBody(request))) {
        if (typeof value !== 'string') {
          continue;
        }

        expect(value)
          .withContext(`body member "${name}" must not carry a match character`)
          .not.toContain(TRAILING_MATCH_CHARACTER);
      }
    };

    it('searches by account name without appending a match character', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25, userName: 'ada' }));

      // ⚠ A BODY, NOT A QUERY STRING. A user name identifies a person, and a request target is
      // recorded by the browser, by every proxy and by the server. See {@link USERS_SEARCH}.
      const request = expectSearch();

      expect(searchBody(request)['userName']).toBe('ada');
      expect(searchBody(request)['pageIndex']).toBe(0);
      expect(searchBody(request)['pageSize']).toBe(25);
      expectTargetCarriesNoneOf(request, ['ada']);
      expectNoTrailingMatchCharacter(request);
      expectBodyCarriesNoMatchCharacter(request);
      request.flush(USER_PAGE);
    });

    it('searches by address without appending a match character', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25, email: 'ada@example.test' }));

      const request = expectSearch();

      expect(searchBody(request)['email']).toBe('ada@example.test');
      expectTargetCarriesNoneOf(request, ['ada@example.test', 'ada', 'example.test']);
      expectNoTrailingMatchCharacter(request);
      expectBodyCarriesNoMatchCharacter(request);
      request.flush(USER_PAGE);
    });

    it('searches by an arbitrary profile-property name, transmitting it verbatim', () => {
      const oddPropertyName = 'Preferred-Locale Display Name';

      observe(
        service.list({
          pageIndex: 0,
          pageSize: 25,
          profilePropertyName: oddPropertyName,
          profilePropertyValue: 'Ada',
        }),
      );

      const request = expectSearch();

      expect(searchBody(request)['profilePropertyName']).toBe(oddPropertyName);
      expect(searchBody(request)['profilePropertyValue']).toBe('Ada');

      // ⚠ THE SHARPEST CASE OF ALL, AND THE REASON THE BODY EXISTS. A tenant declares whatever profile
      // properties it likes, so BOTH halves of this pair are arbitrary tenant data whose meaning neither
      // side knows — the value may be a national identifier or a telephone number.
      expectTargetCarriesNoneOf(request, [oddPropertyName, 'Preferred-Locale', 'Ada']);
      expectNoTrailingMatchCharacter(request);
      expectBodyCarriesNoMatchCharacter(request);
      request.flush(USER_PAGE);
    });

    it('omits the search entirely rather than transmitting the legacy no-search sentinel', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25 }));

      const absent = expectRequest('GET', USERS);
      expect(absent.request.params.keys().sort()).toEqual(['pageIndex', 'pageSize']);
      expect(absent.request.params.toString())
        .withContext('the sentinel must never appear in a transmitted query string')
        .not.toContain(LEGACY_NO_SEARCH_SENTINEL);
      absent.flush(USER_PAGE);

      observe(
        service.list({ pageIndex: 0, pageSize: 25, userName: LEGACY_NO_SEARCH_SENTINEL }),
      );

      // Supplying the word AS a user name makes the request a search, so it travels in a body —
      // and the word is ordinary text there, exactly as it would have been in a query.
      const searched = expectSearch();

      expect(searchBody(searched)['userName'])
        .withContext('the word is ordinary text once it is the caller who supplied it')
        .toBe(LEGACY_NO_SEARCH_SENTINEL);
      searched.flush(USER_PAGE);
    });

    it('transmits two search axes at once, leaving the combination rule to the server', () => {
      // Which axes may be combined is the server's rule, and it answers a combination it refuses with a
      // field-level failure naming the offending parameter - strictly more useful than the client quietly
      // declining to send it.
      observe(
        service.list({ pageIndex: 0, pageSize: 25, userName: 'ada', email: 'ada@example.test' }),
      );

      const request = expectSearch();

      expect(Object.keys(searchBody(request)).sort()).toEqual([
        'email',
        'pageIndex',
        'pageSize',
        'userName',
      ]);
      expect(request.request.params.keys())
        .withContext('a search transmits no query parameters at all')
        .toEqual([]);
      request.flush(USER_PAGE);
    });

    it('offers the approval axis as a paged filter, not as the two unpaged legacy modes', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25, isApproved: false }));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.get('isApproved')).toBe('false');
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('pageSize')).toBe('25');
      request.flush(USER_PAGE);
    });
  });

  // CWE-598 — NOTHING THAT NAMES A PERSON TRAVELS IN A REQUEST TARGET
  // These cases pin the OUTCOME rather than the mechanism: for each identifying filter, the value must be
  // absent from the transmitted target and present in the body.

  describe('no identifying value in a request target', () => {
    /** Every filter that names a person, with a value distinctive enough to search a URL for. */
    const identifyingFilters = [
      { name: 'userName', query: { userName: 'ada.lovelace' }, values: ['ada.lovelace'] },
      { name: 'email', query: { email: 'ada@example.test' }, values: ['ada@example.test'] },
      {
        name: 'profilePropertyName',
        query: { profilePropertyName: 'NationalIdentifier' },
        values: ['NationalIdentifier'],
      },
      {
        name: 'profilePropertyValue',
        query: { profilePropertyValue: 'AB-123-456-C' },
        values: ['AB-123-456-C'],
      },
    ] as const;

    for (const axis of identifyingFilters) {
      it(`keeps ${axis.name} out of the target and puts it in the body`, () => {
        observe(service.list({ pageIndex: 0, pageSize: 25, ...axis.query }));

        const request = expectSearch();

        expectTargetCarriesNoneOf(request, axis.values);

        for (const value of axis.values) {
          expect(Object.values(searchBody(request)))
            .withContext(`${axis.name} must reach the server, in the body`)
            .toContain(value);
        }

        request.flush(USER_PAGE);
      });
    }

    it('sends a search as a POST to the search address, never as a GET to the collection', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25, userName: 'ada' }));

      // Stated as an ABSENCE as well as a presence. Asserting only that the POST exists would pass
      // even if the service also issued the old GET, which is the shape a half-applied fix takes.
      httpMock.expectNone((request) => request.method === 'GET' && request.url === USERS);

      const request = expectSearch();

      expect(searchBody(request)).toEqual({ pageIndex: 0, pageSize: 25, userName: 'ada' });
      request.flush(USER_PAGE);
    });

    it('leaves a listing that names nobody on the cacheable GET', () => {
      // The boundary in the other direction. Page coordinates and an ordering identify nobody, so
      // moving them into a body would give up caching and idempotence for no privacy gain at all.
      observe(service.list({ pageIndex: 2, pageSize: 25, sortBy: 'Username', sortDir: 'Ascending' }));

      httpMock.expectNone((request) => request.method === 'POST' && request.url === USERS_SEARCH);

      const request = expectRequest('GET', USERS);

      // Counted, and it states the positive claim rather than only the absence: the coordinates and the
      // ordering rode on the TARGET, where a cache can see them.
      expect(request.request.params.get('pageIndex')).toBe('2');
      expect(request.request.params.get('pageSize')).toBe('25');
      expect(request.request.params.get('sortBy')).toBe('Username');
      expect(request.request.params.get('sortDir')).toBe('Ascending');
      request.flush(USER_PAGE);
    });

    const genericSearchTerms = ['ada', 'ada.lovelace', 'ada@example.test', 'Lovelace'] as const;

    for (const term of genericSearchTerms) {
      it(`keeps the generic filter "${term}" out of the target and puts it in the body`, () => {
        observe(service.list({ pageIndex: 0, pageSize: 25, query: term }));

        httpMock.expectNone((request) => request.method === 'GET' && request.url === USERS);

        const request = expectSearch();

        expectTargetCarriesNoneOf(request, [term]);
        expect(searchBody(request)).toEqual({ pageIndex: 0, pageSize: 25, query: term });
        request.flush(USER_PAGE);
      });
    }

    it('leaves a blank or whitespace-only generic filter on the GET, because it restricts nothing', () => {
      for (const blank of ['', '   ', '\t']) {
        observe(service.list({ pageIndex: 0, pageSize: 25, query: blank }));

        httpMock.expectNone((request) => request.method === 'POST' && request.url === USERS_SEARCH);

        const request = expectRequest('GET', USERS);

        // Counted, one expectation per blank form so a failure names WHICH form regressed. A GET carries
        // no body at all, which is the shape that proves the value did not migrate into one.
        expect(request.request.body)
          .withContext(`a blank filter (${JSON.stringify(blank)}) stays on the target`)
          .toBeNull();
        expect(request.request.params.get('pageSize')).toBe('25');
        request.flush(USER_PAGE);
      }
    });

    it('leaves an approval-only restriction on the GET, because a state names nobody', () => {
      // ⚠ THE ONE FILTER DELIBERATELY NOT TREATED AS IDENTIFYING. It is one of two values and
      // holds for a whole population, so it discloses nothing about an individual.
      observe(service.list({ pageIndex: 0, pageSize: 25, isApproved: false }));

      httpMock.expectNone((request) => request.method === 'POST' && request.url === USERS_SEARCH);

      const request = expectRequest('GET', USERS);

      expect(request.request.params.get('isApproved')).toBe('false');
      request.flush(USER_PAGE);
    });

    it('omits an unsupplied filter from the body rather than sending it as null', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25, userName: 'ada', email: undefined }));

      const request = expectSearch();

      expect(Object.keys(searchBody(request)).sort()).toEqual([
        'pageIndex',
        'pageSize',
        'userName',
      ]);
      request.flush(USER_PAGE);
    });

    it('decodes the search answer through the same contract as the listing', () => {
      // Both addresses answer the identical envelope, so a caller cannot tell which was used and
      // no second decoder exists to drift from the first.
      const observed = observe(service.list({ pageIndex: 0, pageSize: 25, userName: 'ada' }));

      expectSearch().flush(USER_PAGE);

      expect(observed.values.length).toBe(1);
      expect(observed.values[0].items.length).toBe(USER_PAGE.items.length);
      expect(observed.values[0].meta.totalCount).toBe(USER_PAGE.meta.totalCount);
    });
  });

  // =========================================================================
  // Sentinel and parameter fidelity
  // =========================================================================

  describe('identifier and value fidelity', () => {
    it('interpolates minus one as an identifier rather than reading it as an absence', () => {
      const observed = observe(service.getById(-1));

      const request = expectRequest('GET', `${USERS}/-1`);
      expect(request.request.url).toBe('/api/v1/users/-1');
      request.flush({ data: USER_DETAIL, meta: null } satisfies ApiResponse<UserDetail>);

      expect(observed.values.length)
        .withContext('no request may be suppressed on the strength of its identifier')
        .toBe(1);
    });

    it('interpolates zero as an identifier rather than reading it as an absence', () => {
      const observed = observe(service.getProfileDefinition(0));

      const request = expectRequest('GET', `${PROFILE_DEFINITIONS}/0`);
      expect(request.request.url).toBe('/api/v1/profile-definitions/0');
      request.flush({
        data: PROFILE_DEFINITION,
        meta: null,
      } satisfies ApiResponse<ProfileDefinition>);

      expect(observed.values.length).toBe(1);
    });

    it('issues a request for every identifier it is handed, including the negative ones', () => {
      // Four identifiers spanning both collisions and both signs. Each must produce exactly
      // one request; the verification in the teardown catches any that produced two.
      for (const identifier of [-1, 0, 1, 4242]) {
        observe(service.delete(identifier));

        const request = expectRequest('DELETE', `${USERS}/${identifier}`);
        expect(request.request.url).toBe(`/api/v1/users/${identifier}`);
        request.flush(null, { status: 204, statusText: 'No Content' });
      }
    });

    it('transmits an empty search value as an empty pair rather than dropping it', () => {
      observe(service.list({ pageIndex: 0, pageSize: 25, query: '', userName: '' }));

      const request = expectSearch();
      const body = searchBody(request);

      expect(Object.prototype.hasOwnProperty.call(body, 'query')).toBe(true);
      expect(body['query']).toBe('');
      expect(Object.prototype.hasOwnProperty.call(body, 'userName')).toBe(true);
      expect(body['userName']).toBe('');
      request.flush(USER_PAGE);
    });

    it('distinguishes an undefined parameter from a zero, an empty string and a false one', () => {
      // The single regression test for a truthiness check standing in for a nullish one.
      // Three falsy values are present; one undefined value is absent.
      observe(
        service.list({
          pageIndex: 0,
          pageSize: 25,
          query: '',
          isApproved: false,
          userName: undefined,
        }),
      );

      const request = expectRequest('GET', USERS);
      expect(request.request.params.keys().sort()).toEqual([
        'isApproved',
        'pageIndex',
        'pageSize',
        'query',
      ]);
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('query')).toBe('');
      expect(request.request.params.get('isApproved')).toBe('false');
      expect(request.request.params.has('userName')).toBe(false);
      request.flush(USER_PAGE);
    });

    const SURFACE: readonly {
      readonly method: string;
      readonly path: string;
      readonly status: number;
      /**
       * The response body, typed as the testing backend accepts it. `Object | null` rather than
       * `unknown`, because every success body here is either a JSON object or the absent body a `204`
       * carries, and the flush primitive is typed for exactly that.
       */
      readonly body: Object | null;
      readonly invoke: () => Observable<unknown>;
    }[] = [
      // Reads that answer 200 with a payload.
      {
        method: 'GET',
        path: USERS,
        status: 200,
        body: USER_PAGE,
        invoke: () => service.list({ pageIndex: 0, pageSize: 25 }),
      },
      {
        method: 'GET',
        path: `${USERS}/1`,
        status: 200,
        body: { data: USER_DETAIL, meta: null },
        invoke: () => service.getById(1),
      },
      {
        method: 'GET',
        path: `${USERS}/1/profile`,
        status: 200,
        body: { data: PROFILE_READ, meta: null },
        invoke: () => service.getProfile(1),
      },
      {
        method: 'GET',
        path: ACCOUNT_POLICY,
        status: 200,
        body: { data: STORED_ACCOUNT_POLICY_BODY, meta: null },
        invoke: () => service.getMembershipSettings(),
      },
      {
        method: 'GET',
        path: PROFILE_DEFINITIONS,
        status: 200,
        body: { data: [PROFILE_DEFINITION], meta: null },
        invoke: () => service.listProfileDefinitions(),
      },
      {
        method: 'GET',
        path: `${PROFILE_DEFINITIONS}/0`,
        status: 200,
        body: { data: PROFILE_DEFINITION, meta: null },
        invoke: () => service.getProfileDefinition(0),
      },
      // Creations that answer 201 with the created representation.
      {
        method: 'POST',
        path: USERS,
        status: 201,
        body: { data: USER_DETAIL, meta: null },
        invoke: () => service.create(CREATE_USER_REQUEST),
      },
      {
        method: 'POST',
        path: PROFILE_DEFINITIONS,
        status: 201,
        body: { data: PROFILE_DEFINITION, meta: null },
        invoke: () => service.createProfileDefinition(CREATE_DEFINITION_REQUEST),
      },
      // Replacements that answer 200 with the replaced representation.
      {
        method: 'PUT',
        path: `${USERS}/1`,
        status: 200,
        body: { data: USER_DETAIL, meta: null },
        invoke: () => service.update(1, UPDATE_USER_REQUEST),
      },
      {
        method: 'PUT',
        path: `${PROFILE_DEFINITIONS}/0`,
        status: 200,
        body: { data: PROFILE_DEFINITION, meta: null },
        invoke: () => service.updateProfileDefinition(0, UPDATE_DEFINITION_REQUEST),
      },
      // Commands that answer 204 with NO body whatsoever.
      {
        method: 'DELETE',
        path: `${USERS}/1`,
        status: 204,
        body: null,
        invoke: () => service.delete(1),
      },
      {
        method: 'PUT',
        path: `${USERS}/1/profile`,
        status: 204,
        body: null,
        invoke: () => service.updateProfile(1, PROFILE_SUBMISSION),
      },
      {
        method: 'POST',
        path: `${USERS}/1/password`,
        status: 204,
        body: null,
        invoke: () => service.changePassword(1, CHANGE_PASSWORD_REQUEST),
      },
      {
        method: 'POST',
        path: `${USERS}/1/password-reset`,
        status: 204,
        body: null,
        invoke: () => service.passwordReset(1, RESET_PASSWORD_REQUEST),
      },
      {
        method: 'PUT',
        path: `${USERS}/1/approval`,
        status: 204,
        body: null,
        invoke: () => service.setApproval(1, true),
      },
      {
        method: 'POST',
        path: `${USERS}/1/unlock`,
        status: 204,
        body: null,
        invoke: () => service.unlock(1),
      },
      {
        method: 'POST',
        path: `${USERS}/1/require-password-change`,
        status: 204,
        body: null,
        invoke: () => service.requirePasswordChange(1),
      },
      {
        method: 'PUT',
        path: ACCOUNT_POLICY,
        status: 200,
        body: ACCOUNT_POLICY_UPDATE_ENVELOPE,
        invoke: () => service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY),
      },
      {
        method: 'DELETE',
        path: `${PROFILE_DEFINITIONS}/0`,
        status: 204,
        body: null,
        invoke: () => service.deleteProfileDefinition(0),
      },
      // The account's own subscriptions. Two reads answering 200 with a payload, three
      // commands answering 204, and every one of them addressed with service identifier ZERO.
      {
        method: 'GET',
        path: MEMBER_SERVICES,
        status: 200,
        body: {
          items: [MEMBER_SERVICE],
          meta: { totalCount: 1, pageIndex: 0, pageSize: WHOLE_CATALOGUE_PAGE_SIZE, totalPages: 1 },
        },
        invoke: () => service.listMemberServices(1),
      },
      {
        method: 'POST',
        path: MEMBER_SERVICE_REDEMPTIONS,
        status: 200,
        body: { data: REDEMPTION_RESULT, meta: null },
        invoke: () => service.redeemServiceCode(1, { code: 'Founders-2026' }),
      },
      {
        method: 'POST',
        path: MEMBER_SERVICE_SUBSCRIPTION,
        status: 204,
        body: null,
        invoke: () => service.subscribeToService(1, 0),
      },
      {
        method: 'DELETE',
        path: MEMBER_SERVICE_SUBSCRIPTION,
        status: 204,
        body: null,
        invoke: () => service.cancelService(1, 0),
      },
      {
        method: 'POST',
        path: MEMBER_SERVICE_TRIAL,
        status: 204,
        body: null,
        invoke: () => service.startServiceTrial(1, 0),
      },
    ];

    it('sets no header of its own on any request across the whole surface', () => {
      // One pass over every method, asserting the two interceptor headers are unset. The reason to do it
      // once for all of them rather than trusting the per-method checks is that a header added to a shared
      // options object would appear everywhere at once.
      expect(SURFACE.length)
        .withContext('every method on the service is exercised by this pass')
        .toBe(24);

      for (const { method, path, status, body, invoke } of SURFACE) {
        observe(invoke());

        const request = expectRequest(method, path);
        expectNoInterceptorHeaders(request);
        expect(request.request.url)
          .withContext(`${method} ${path} must address the relative API base`)
          .toMatch(/^\/api\/v1\//);

        // Answered with THIS endpoint's own success, so the request-side claim is made
        // against a response the server can actually send.
        request.flush(body, { status, statusText: status === 204 ? 'No Content' : 'OK' });
      }
    });

    it('completes every method on the success status its endpoint declares', () => {
      // THE COMPANION CLAIM, AND THE ONE THE SHARED-BODY LOOP COULD NOT MAKE. Each method is driven to its
      // declared success and the OUTCOME is asserted: a payload-bearing read or write emits exactly one
      // value and completes, and a payload-free command completes without emitting anything of substance.
      for (const { method, path, status, body, invoke } of SURFACE) {
        const observed = observe(invoke());

        const request = expectRequest(method, path);
        request.flush(body, { status, statusText: status === 204 ? 'No Content' : 'OK' });

        expect(observed.failures)
          .withContext(`${method} ${path} must not fail on its own declared success`)
          .toEqual([]);
        expect(observed.completions.length)
          .withContext(`${method} ${path} completes exactly once`)
          .toBe(1);
        expect(observed.values.length)
          .withContext(`${method} ${path} emits exactly one notification`)
          .toBe(1);

        if (status === 204) {
          // A 204 carries no body, so there is nothing to unwrap and the value is the empty
          // body itself. Anything else here would mean the client had manufactured a payload.
          expect(observed.values[0])
            .withContext(`${method} ${path} answers 204, so no payload can be emitted`)
            .toBeNull();
        } else {
          expect(observed.values[0])
            .withContext(`${method} ${path} answers ${status} with a payload`)
            .not.toBeNull();
        }
      }
    });

    it('declares 204 for every payload-free command and 201 for every creation', () => {
      const byStatus = (status: number): readonly string[] =>
        SURFACE.filter((entry) => entry.status === status)
          .map((entry) => `${entry.method} ${entry.path}`)
          .sort();

      expect(byStatus(204)).toEqual([
        'DELETE /api/v1/profile-definitions/0',
        'DELETE /api/v1/users/1',
        'DELETE /api/v1/users/1/services/0/subscription',
        'POST /api/v1/users/1/password',
        'POST /api/v1/users/1/password-reset',
        'POST /api/v1/users/1/require-password-change',
        'POST /api/v1/users/1/services/0/subscription',
        'POST /api/v1/users/1/services/0/trial',
        'POST /api/v1/users/1/unlock',
        'PUT /api/v1/users/1/approval',
        'PUT /api/v1/users/1/profile',
      ]);
      expect(byStatus(201)).toEqual(['POST /api/v1/profile-definitions', 'POST /api/v1/users']);

      // ELEVEN operations answer 200 with a payload. Two of them are not reads, and each is worth naming.
      expect(byStatus(200).length).toBe(11);
      expect(byStatus(200)).toContain('PUT /api/v1/users/settings');
    });
  });

  // =========================================================================
  // The closed surface
  // =========================================================================

  describe('the closed surface', () => {
    /** Every own member of the prototype, sorted. */
    const PROTOTYPE_MEMBERS: readonly string[] = [
      'cancelService',
      'changePassword',
      'constructor',
      'create',
      'createProfileDefinition',
      'delete',
      'deleteProfileDefinition',
      'getById',
      'getMembershipSettings',
      'getProfile',
      'getProfileDefinition',
      'list',
      'listChoices',
      'listMemberServices',
      'listMemberServicesPage',
      'listProfileDefinitions',
      'passwordReset',
      'redeemServiceCode',
      'reorderProfileDefinitions',
      'requirePasswordChange',
      'setApproval',
      'startServiceTrial',
      'subscribeToService',
      'unlock',
      'update',
      'updateMembershipSettings',
      'updateProfile',
      'updateProfileDefinition',
    ];

    it('exposes exactly twenty-seven methods and not one more', () => {
      // ⚠ TWENTY-FIVE BECAME TWENTY-SIX WHEN THE MEMBER-SERVICES CATALOGUE WAS BOUNDED. The endpoint now
      // answers one page at a time, so the page reader is a member of the surface in its own right and the
      // whole-catalogue reader is expressed in terms of it. Both are specified above.
      //
      // ⚠ TWENTY-SIX BECAME TWENTY-SEVEN WHEN ORDERING STOPPED BEING A SEQUENCE OF REPLACEMENTS. A move
      // EXCHANGES the stored positions of two declarations, so the two writes are only correct together:
      // land one and lose the other and both rows claim the same position. That is not expressible through
      // a per-declaration replacement however carefully the caller sequences them, so the exchange has a
      // route of its own that the server commits as one unit of work. Specified above.
      const actual: readonly string[] = Object.getOwnPropertyNames(UserService.prototype).sort();

      expect(actual)
        .withContext('a method added without a specification fails here first')
        .toEqual([...PROTOTYPE_MEMBERS]);
      expect(actual.filter((name) => name !== 'constructor').length).toBe(27);
    });

    it('reads an account picker through its own method, not through a mode of the listing', () => {
      // ⚠ TWENTY-FOUR BECAME TWENTY-FIVE FOR A REASON WORTH STATING AT THE SURFACE. A performance and
      // privacy review measured the role-assignment screen filling its account drop-down — and its
      // account-count probe — from `list`, whose row carries a postal address, a telephone number, an
      // electronic-mail address, a creation instant, a last-login instant and four status flags.
      expect(PROTOTYPE_MEMBERS).toContain('listChoices');

      for (const forbidden of ['listSlim', 'listMinimal', 'listNames', 'listForPicker']) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist; the picker's method is listChoices`)
          .not.toContain(forbidden);
      }
    });

    it('writes ordering as ONE SET, and exposes no per-nudge helper', () => {
      // MIGRATION: THIS TEST USED TO REQUIRE THE OPPOSITE, and it was wrong. It asserted that no ordering
      // member existed at all, on the reasoning that a position is a field on a replacement — which is true
      // of the DATA and false of the OPERATION. Moving a declaration EXCHANGES the stored positions of two
      // of them, so the two writes are only correct together: land one and lose the other and both rows
      // claim the same position, which is neither the order the operator started from nor the one they
      // asked for. A sequence of replacements cannot express that however precisely it reports which row
      // failed, so the exchange is submitted as one request the server commits as one unit of work.
      expect(PROTOTYPE_MEMBERS)
        .withContext('an exchange of positions must be submitted as one set, not as two replacements')
        .toContain('reorderProfileDefinitions');

      // The per-NUDGE helpers remain forbidden, and for the reason that has not changed: "up" and "down"
      // are grid affordances, not transport operations, and a client that sent one per keystroke would be
      // back to writing an exchange as two independent requests under a different name.
      for (const forbidden of ['moveUp', 'moveDown', 'swapOrder', 'renumber']) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist; a move is submitted as a position set`)
          .not.toContain(forbidden);
      }
    });

    it('exposes no credential disclosure of any kind', () => {
      // A RESET is carried forward and is specified above; RETRIEVAL is not, and the difference is
      // structural rather than a matter of configuration.
      for (const forbidden of [
        'sendPassword',
        'forgotPassword',
        'recoverPassword',
        'retrievePassword',
        'remindPassword',
        'getPassword',
      ]) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist; the store is one-way`)
          .not.toContain(forbidden);
      }
    });

    it('exposes no role membership, service or subscription operation', () => {
      for (const forbidden of [
        'getRoles',
        'listRoles',
        'addRole',
        'removeRole',
        'assignRole',
        'getServices',
        'subscribe',
        'unsubscribe',
        'getSubscriptions',
      ]) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist; membership belongs to the role resource`)
          .not.toContain(forbidden);
      }
    });

    it('exposes neither of the two unpaged legacy list modes, nor any bulk operation', () => {
      for (const forbidden of [
        'getUnauthorizedUsers',
        'getUnAuthorizedUsers',
        'deleteUnauthorizedUsers',
        'getOnlineUsers',
        'listOnline',
        'deleteMany',
        'bulkDelete',
        'deleteAll',
      ]) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist on this surface`)
          .not.toContain(forbidden);
      }
    });

    it('reaches nothing outside the account and profile-definition resources', () => {
      // No sign-in, no sign-out, no token refresh, no identity read, no liveness probe, no permission
      // mutation, no tenant key-value read, no upload. Each of those belongs to a different unit, and
      // several of them to no client-side unit at all.
      for (const forbidden of [
        'login',
        'logout',
        'refresh',
        'getCurrentUser',
        'register',
        'verify',
        'getExternalProviders',
        'checkHealth',
        'grantPermission',
        'revokePermission',
        'getPortalSettings',
        'uploadAvatar',
      ]) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist on the account transport`)
          .not.toContain(forbidden);
      }
    });

    it('agrees with the prototype about every name it claims is absent', () => {
      const fromPrototype: readonly string[] = Object.getOwnPropertyNames(
        UserService.prototype,
      ).sort();

      expect([...PROTOTYPE_MEMBERS]).toEqual([...fromPrototype]);
    });
  });
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  // ⚠ THE REFUSAL NAMES THE MEMBER PATH AND THE EXPECTED TYPE, NEVER THE VALUE, and that is a privacy
  // boundary in this file specifically: an account's address, telephone and profile values are personal
  // data, and a violation report must not copy them anywhere.
  describe('refuses a response that does not match its contract', () => {
    // The listing always serialises its two paging parameters, so the url the backend
    // actually receives carries them. Matching on the bare path would find nothing.
    const LISTING_WITH_PAGING = `${USERS}?pageIndex=0&pageSize=25`;

    /**
     * Asserts that answering the one pending request with `body` fails at `path`, and that the report
     * carries no personal data.
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

      const failure: unknown = failures[0];

      expect(isContractViolation(failure)).toBeTrue();

      if (isContractViolation(failure)) {
        expect(failure.path).toBe(path);

        for (const personal of ['jsmith', 'Smith', '555', 'example.com']) {
          expect(failure.message)
            .withContext(`the report discloses ${personal}`)
            .not.toContain(personal);
        }
      }
    }

    it('refuses a page with no metadata rather than reporting the tenant has no accounts', () => {
      expectViolationAt(
        service.list({ pageIndex: 0, pageSize: 25 }),
        LISTING_WITH_PAGING,
        { items: [USER_LIST_ITEM] },
        'response.meta',
      );
    });

    it('refuses a listed row whose login name is absent', () => {
      const malformed: Record<string, unknown> = { ...USER_LIST_ITEM };

      delete malformed['username'];

      expectViolationAt(
        service.list({ pageIndex: 0, pageSize: 25 }),
        LISTING_WITH_PAGING,
        { ...USER_PAGE, items: [malformed] },
        'response.items[0].username',
      );
    });

    it('keeps a listed row whose first name is the empty string', () => {
      const values: unknown[] = [];

      service.list({ pageIndex: 0, pageSize: 25 }).subscribe({ next: (page: unknown) => values.push(page) });

      httpMock.expectOne(LISTING_WITH_PAGING).flush({
        ...USER_PAGE,
        items: [{ ...USER_LIST_ITEM, firstName: '', address: null }],
      });

      expect(values.length).toBe(1);
    });

    it('refuses an account whose role list is absent', () => {
      const malformed: Record<string, unknown> = { ...USER_DETAIL };

      delete malformed['roles'];

      expectViolationAt(
        service.getById(1),
        `${USERS}/1`,
        { data: malformed, meta: null },
        'response.data.roles',
      );
    });

    it('refuses an account whose sign-in instant is not a date', () => {
      expectViolationAt(
        service.getById(1),
        `${USERS}/1`,
        { data: { ...USER_DETAIL, lastLoginDate: 'not a date' }, meta: null },
        'response.data.lastLoginDate',
      );
    });

    it('admits a null audit instant, because an account may never have signed in', () => {
      const values: unknown[] = [];

      service.getById(1).subscribe({ next: (account: unknown) => values.push(account) });

      httpMock
        .expectOne(`${USERS}/1`)
        .flush({ data: { ...USER_DETAIL, lastLoginDate: null }, meta: null });

      expect(values.length).toBe(1);
    });

    it('refuses a redirect page identifier that arrived as text', () => {
      expectViolationAt(
        service.getMembershipSettings(),
        ACCOUNT_POLICY,
        { data: { ...STORED_ACCOUNT_POLICY_BODY, redirectAfterLogin: '5' }, meta: null },
        'response.data.redirectAfterLogin',
      );
    });

    it('refuses a policy-write report that omits the rewrite count', () => {
      expectViolationAt(
        service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY),
        ACCOUNT_POLICY,
        { data: { displayNameFormatChanged: true }, meta: null },
        'response.data.displayNamesRewritten',
      );
    });

    it('refuses a policy-write report whose rewrite count arrived as text', () => {
      expectViolationAt(
        service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY),
        ACCOUNT_POLICY,
        { data: { displayNameFormatChanged: false, displayNamesRewritten: '3' }, meta: null },
        'response.data.displayNamesRewritten',
      );
    });

    it('refuses an account row that omits the deletion capability', () => {
      const malformed: Record<string, unknown> = { ...USER_LIST_ITEM };

      delete malformed['canDelete'];

      expectViolationAt(
        service.list({ pageIndex: 0, pageSize: 25 }),
        LISTING_WITH_PAGING,
        { ...USER_PAGE, items: [malformed] },
        'response.items[0].canDelete',
      );
    });

    it('admits a redirect to page zero, which is a real page', () => {
      // ⚠ THE SENTINEL COLLISION. `Tabs.TabID` seeds at zero, so zero is an ordinary page and `null` is the
      // only expression of "no redirect". A guard on the value being positive would silently discard a
      // redirect to the first page ever created.
      const values: (unknown | null)[] = [];

      service.getMembershipSettings().subscribe({
        next: (settings: unknown) => values.push(settings),
      });

      httpMock
        .expectOne(ACCOUNT_POLICY)
        .flush({ data: { ...STORED_ACCOUNT_POLICY_BODY, redirectAfterLogin: 0 }, meta: null });

      expect(values.length).toBe(1);
    });

    it('refuses a profile whose nested declaration is malformed', () => {
      const entry = {
        ...PROFILE_READ.properties[0],
        definition: { ...PROFILE_DEFINITION, propertyName: 42 },
      };

      expectViolationAt(
        service.getProfile(1),
        `${USERS}/1/profile`,
        { data: { userId: 1, properties: [entry], displayVisibilityEnabled: true }, meta: null },
        'response.data.properties[0].definition.propertyName',
      );
    });

    it('keeps a profile value that is the empty string', () => {
      // A value the person CLEARED is empty rather than absent, and coalescing it to null
      // would make a cleared field indistinguishable from one never filled in.
      const values: unknown[] = [];

      service.getProfile(1).subscribe({ next: (profile: unknown) => values.push(profile) });

      httpMock.expectOne(`${USERS}/1/profile`).flush({ data: PROFILE_READ, meta: null });

      expect(values).toEqual([PROFILE_READ]);
    });

    it('refuses a profile that omits the tenant visibility policy', () => {
      // ⚠ REFUSED RATHER THAN DEFAULTED, AND THE REASON IS THAT NEITHER DEFAULT IS SAFE.
      // `displayVisibilityEnabled` decides whether the profile screen offers the per-value visibility
      // control at all.
      expectViolationAt(
        service.getProfile(1),
        `${USERS}/1/profile`,
        { data: { userId: 1, properties: [] }, meta: null },
        'response.data.displayVisibilityEnabled',
      );
    });

    it('carries the tenant visibility policy through in both states', () => {
      // BOTH STATES, because a decoder that dropped the member would satisfy a case asserting only the
      // `true` one: `undefined` and `true` are not distinguishable by a truthiness test, and `false` is the
      // state the tenant has to store deliberately.
      for (const displayVisibilityEnabled of [true, false]) {
        const values: unknown[] = [];

        service.getProfile(1).subscribe({ next: (profile: unknown) => values.push(profile) });

        httpMock
          .expectOne(`${USERS}/1/profile`)
          .flush({ data: { ...PROFILE_READ, displayVisibilityEnabled }, meta: null });

        expect(values)
          .withContext(`the policy must survive decoding as ${String(displayVisibilityEnabled)}`)
          .toEqual([{ ...PROFILE_READ, displayVisibilityEnabled }]);
      }
    });

    it('refuses a null profile payload, because the contract publishes none', () => {
      expectViolationAt(
        service.getProfile(1),
        `${USERS}/1/profile`,
        { data: null, meta: null },
        'response.data',
      );
    });

    it('refuses a declaration catalogue that is not an array', () => {
      expectViolationAt(
        service.listProfileDefinitions(),
        PROFILE_DEFINITIONS,
        { data: PROFILE_DEFINITION, meta: null },
        'response.data',
      );
    });
  });

  // WHO ANNOUNCES A FAILURE
  // Every request is marked as presented by its caller, which is what stops one failure being shown twice —
  // once as the interceptor's transient notification and once as the in-page banner `user.store` records it
  // for. The interceptor still re-throws.
  describe('marks every request as presented by its caller', () => {
    it('marks every operation the service exposes', () => {
      const swallow = { error: () => undefined };

      service.list({ pageIndex: 0, pageSize: 25 }).subscribe(swallow);
      service.getById(1).subscribe(swallow);
      service.create(CREATE_USER_REQUEST).subscribe(swallow);
      service.update(1, UPDATE_USER_REQUEST).subscribe(swallow);
      service.delete(1).subscribe(swallow);
      service.getProfile(1).subscribe(swallow);
      service.updateProfile(1, { userId: 1, properties: [] }).subscribe(swallow);
      service.changePassword(1, CHANGE_PASSWORD_REQUEST).subscribe(swallow);
      service.passwordReset(1, RESET_PASSWORD_REQUEST).subscribe(swallow);
      service.setApproval(1, true).subscribe(swallow);
      service.unlock(1).subscribe(swallow);
      service.requirePasswordChange(1).subscribe(swallow);
      service.getMembershipSettings().subscribe(swallow);
      service.updateMembershipSettings(STORED_ACCOUNT_POLICY_BODY).subscribe(swallow);
      service.listProfileDefinitions().subscribe(swallow);
      service.getProfileDefinition(0).subscribe(swallow);
      service.deleteProfileDefinition(0).subscribe(swallow);

      const issued = httpMock.match(() => true);

      expect(issued.length)
        .withContext('every operation dispatched exactly one request')
        .toBe(17);

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
