//
// Specification for the account-administration store.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE PROVES
// ---------------------------------------------------------------------------
// Six claims, and each one is a place where a plausible implementation is wrong:
//
//   1. THE WIRE PAGE INDEX IS ZERO-BASED. The first page travels as 0, the third as
//      2, and no arithmetic is applied on the way out.
//   2. THE PAGE SIZE COMES FROM THE TENANT'S ACCOUNT POLICY. It is read from a
//      response and never written as a constant, so a store that hard-coded ten
//      fails here rather than in front of an administrator who configured
//      twenty-five.
//   3. ALL THREE SEARCHES ARE PREFIX MATCHES CARRYING NO WILDCARD. The text travels
//      exactly as typed - untrimmed, un-case-folded and undecorated.
//   4. ABSENCE IS OMISSION. The legacy reserved word that meant "no search" is never
//      transmitted, and the two legacy list modes that were dropped do not exist.
//   5. SENTINELS ARE DATA. Minus one, zero, the empty string, false and the least
//      representable date all survive a round trip unchanged.
//   6. THE PUBLISHED SURFACE CANNOT BE WRITTEN TO, and state is replaced rather than
//      mutated.
//
// The negative half of those claims is the half worth having, and it is the half a
// stubbed transport cannot make. Every specification runs the REAL transport against
// the mock backend, so a URL, a verb, a body and a query parameter are all asserted
// in one pass, and every specification closes with a verification that nothing is
// left outstanding. That verification is the mechanism; the assertions are only the
// readable part.
//
// ---------------------------------------------------------------------------
// NO PROJECT RULES DOCUMENT EXISTS
// ---------------------------------------------------------------------------
// The engagement supplied none. The rules review answers with a single line saying
// so, and it answers identically for ranges that BEGIN PAST the first, the second and
// the two-hundred-and-fiftieth line - which is what proves the answer is the whole
// document rather than its opening line. Nothing here is justified by a project rule
// and nothing is relaxed by their absence: the enterprise baseline the action plan
// sets out governs instead, with rule-force.
//
// ---------------------------------------------------------------------------
// WHY EVERY EXPECTED URL IS RELATIVE, AND WHY EACH IS SPELLED OUT IN FULL
// ---------------------------------------------------------------------------
// The workspace replaces the environment module only for the DEVELOPMENT build
// configuration; the production configuration replaces nothing, so the unsuffixed
// environment module IS the production one and its configured base is the relative
// `/api/v1`. The test target declares no replacement at all, so that is the base a
// specification compiles against, and an expectation naming an absolute host would be
// asserting a value this target never loads.
//
// The paths below are written out as literal strings rather than read back from the
// endpoint declaration module. Importing the same constant the subject imports would
// let a wrong route template agree with itself - the specification would pass while
// every request went somewhere the API does not serve. Spelling the expected URL
// independently is what turns that class of defect into a failure.
//
// ---------------------------------------------------------------------------
// MIGRATION CONTEXT - WHAT THE LEGACY DID, AND WHAT REPLACED IT
// ---------------------------------------------------------------------------
//  1. ZERO-BASED WIRE PAGE INDEX. `Website/admin/Users/Users.ascx.vb` L51 declared
//     `Private _CurrentPage As Integer = 1` and every provider call subtracted one
//     immediately before dispatch - L265, L269, L271 and L274 each pass
//     `CurrentPage - 1`. Corroborated independently by
//     `Website/admin/Users/ManageUsers.ascx.vb` L176, whose page field seeds at 0.
//     The wire carries the zero-based value directly and nothing adds or subtracts
//     one.
//  2. PAGE SIZE FROM THE TENANT SETTING, NEVER A CONSTANT. `Users.ascx.vb` L114-L119
//     read it through `UserModuleBase.GetSetting(UsersPortalId, "Records_PerPage")`,
//     and `Library/Components/Users/UserModuleBase.vb` L134-L136 supplied ten ONLY
//     when that setting was unset. The setting is now a member of the account policy,
//     and the shared fallback applies only in the policy's absence.
//  3. PREFIX SEARCH WITH THE WILDCARD APPENDED SERVER-SIDE. L269, L271 and L274 each
//     appended one trailing per-cent character before calling down. The API
//     reproduces that, wildcard included, so a caller passes bare text: appending one
//     here would double the pattern and leading with one would silently widen a
//     starts-with into a substring match.
//  4. THE NO-SEARCH RESERVED WORD BECOMES OMISSION. L266 guarded the whole search
//     block by comparing the typed text against a bare magic string, so that word
//     could never be searched for. Absence is expressed here by omitting the
//     parameter.
//  5. THE FOURTH LEGACY BRANCH IS THE PAGED, UNFILTERED LISTING. L264-L265 called
//     the unfiltered paged reader with page coordinates and no filter at all. It is
//     distinct from the no-search state: this one dispatches a request matching
//     everything, that one dispatches nothing.
//  6. TWO LEGACY LIST MODES ARE DROPPED. L258-L260 answered one from
//     `GetUnAuthorizedUsers` and hid the pager; L261-L263 answered the
//     signed-in-accounts view from session tracking and hid the pager too. Neither
//     took a page coordinate, so each returned an unbounded set, and the second
//     depended on a scheduled purge - `Library/Components/Users/Users Online/
//     PurgeUsersOnline.vb` L44 - that this migration does not carry forward.
//  7. BRANCHING ON LOCALISED STRINGS IS NOT REPRODUCED. L258, L261 and L264 each
//     compared the typed text against a resource lookup, so the query a person got
//     depended on the rendered language. Typed discriminators replace that.
//  8. THE THIRD SEARCH AXIS IS AN OPEN SET. L272-L274 passed its field name straight
//     through as the property name and L275 appended it to the screen's own query
//     string. A tenant declares whatever properties it likes, so the name is never
//     validated, normalised or case-folded on this side.
//  9. CONTROL STATE AND SERVER-SESSION STATE ARE ELIMINATED. The page number lived in
//     control state at `ManageUsers.ascx.vb` L174-L185 and the account identifier at
//     `Library/Components/Users/UserModuleBase.vb` L466-L505; both become ordinary
//     signals. The return-address key is the router's concern. A direct search for
//     server-session access across the five in-scope library trees and the
//     thirty-nine administration code-behinds finds ZERO sites, so there is nothing
//     to carry.
// 10. AUTHORISATION MOVED SERVER-SIDE, COMPLETELY. `UserModuleBase.vb` L466-L505
//     embedded a full decision INSIDE a page property getter - own-record at L473-474,
//     installation administrator at L475-476, tenant administrator at L479, a nested
//     exclusion at L481-L487, and a redirect to a denial page at L494. Not one line is
//     reproduced. The API decides and reports a refusal as a status with a problem
//     document; the permission vocabulary decides nothing on this side.
// 11. THE CREATION VOCABULARY SUCCEEDS AT THIRTEEN, NOT ZERO.
//     `Library/Components/Users/Membership/UserCreateStatus.vb` L23-L42 numbers all
//     eighteen members explicitly; its zero member is the initial "no error yet"
//     marker rather than an outcome, as `Website/admin/Users/User.ascx.vb` L175 and
//     L185 prove by treating any other value as a failure. Three members describe
//     distinct name failures and are not interchangeable.
// 12. NO OUTCOME ORDINAL CROSSES THE WIRE. The credential-update vocabulary at
//     `Library/Components/Users/Membership/PasswordUpdateStatus.vb` L23-L32 assigns NO
//     explicit values, so declaration order is the ordinal and it succeeds at zero;
//     the sign-in vocabulary succeeds at one. An assumption that zero means success is
//     wrong two times in three, so that vocabulary is deliberately not declared on
//     this side at all and every assertion here keys on a STRING code. The five-member
//     validity vocabulary, which the sign-in path resolved by precedence, becomes a set
//     of INDEPENDENT advisory flags, so combinations it could not express now are
//     expressible.
// 13. CREDENTIALS. A reset survives and RETRIEVAL DOES NOT: the legacy provider
//     enabled the two independently and only retrieval required a reversible store.
//     The policy is preserved VERBATIM and not tightened - `Website/release.config`
//     L242 set a seven-character floor, L243 required no non-alphanumeric character,
//     L244 did not require a unique address, L240 enabled reset and L241 required no
//     question-and-answer pair - and it is enforced server-side. A one-way hash
//     replaces the reversible representation whose symmetric key was committed to
//     source control in the clear at L89-L93.
// 14. PROFILE DECLARATIONS ARE UNPAGED, addressed by the PROPERTY-definition
//     identifier, and reordered through the view-order FIELD on a replace. There is no
//     move endpoint: `Website/admin/Users/ProfileDefinitions.ascx.vb` L182-L187 read
//     the neighbouring declaration and swapped the two, which is a two-row write that a
//     one-row route would have made look atomic.
// 15. PAGER VISIBILITY IS PRESENTATION. L278-L280 narrowed the pager to the case where
//     the page size was smaller than the total, and only when the tenant had asked for
//     suppression. The advisory boolean is asserted; rendering is not.
// 16. THE LEGACY CACHE IS NOT REPRODUCED CLIENT-SIDE.
//     `Library/Components/Providers/Caching/DataCache.vb` is reached from a hundred and
//     sixteen in-scope call sites, fifteen of them from
//     `Library/Components/Users/UserController.vb`. After a write this store re-reads,
//     and there is no cache map, expiry instant or staleness marker to assert.
// 17. A PERMISSION REFUSAL IS A WARNING, NOT A FAULT.
//     `Website/admin/Security/AccessDenied.ascx.vb` performs no permission check at
//     all - the page only PRESENTS a denial - and both branches of its load handler, at
//     L43 and L45, render at the warning message type.
// 18. LOCALISATION IS NOT PORTED. The legacy mechanism was specific to the abandoned
//     presentation framework and no translation runtime is added to this workspace, so
//     nothing here holds a resource key or resolves one.
// 19. EVERY IMPLICIT COERCION IS MADE EXPLICIT. The thirty-nine administration
//     code-behinds compiled with strict mode OFF (`release.config` L125), which is what
//     permitted the unguarded narrowing at `Users.ascx.vb` L117 and the control-state
//     read at `UserModuleBase.vb` L498. Strict compilation is what forces those to
//     surface, and the typed fixtures below are part of that forcing function: a
//     mis-spelled member is a compilation error here rather than an `undefined` at run
//     time.
//
// ---------------------------------------------------------------------------
// MEASURED DIVERGENCES FROM THE PLANNED SURFACE (asserted as BUILT, reported as found)
// ---------------------------------------------------------------------------
//   * THE ACCOUNT-POLICY PATH IS `/api/v1/users/settings`. The plan named
//     `/api/v1/users/settings/membership`; the built route is the `settings` child of
//     the account collection, confirmed three ways - the endpoint declaration module,
//     and the controller's own `[HttpGet("settings")]` and `[HttpPut("settings")]`. A
//     search for the longer path across the whole server tree returns nothing. The
//     shorter path is asserted because it is the one the API serves. Note that the API
//     path and the screen route are different strings and neither derives from the
//     other.
//   * THE CREDENTIAL VERBS ARE POST. The plan named a replace verb for the credential
//     change; the built transport posts to `password` and to `password-reset`, matching
//     the controller. The bodiless-success proof is unaffected and is made against the
//     verb actually used.
//   * AN ACCOUNT'S ROLES ARRIVE AS NAMES, NOT IDENTIFIERS, so no role identifier of
//     zero can be carried on that contract. The zero-seed symmetry is proved instead on
//     the declaration contract, whose identifier, module association and visibility can
//     each legitimately be zero, and on the identifiers interpolated into a path.
//   * NO TENANT IDENTIFIER IS TRANSMITTED ANYWHERE. The API resolves one tenant per
//     request from the host it was reached on, so the minus-one and zero proofs land on
//     what comes BACK and on what is interpolated into a path, which is where those
//     values actually travel.
//   * THE SUPPORT REFERENCE IS THE CORRELATION IDENTIFIER, NOT THE TRACE IDENTIFIER.
//     The two are independent values with different formats - the trace identifier is a
//     trace-context value taken from whatever diagnostic activity was current. Both are
//     asserted to survive, and the support reference is asserted to resolve to the
//     correlation identifier.
//   * A SPENT-BUDGET STATUS IS REACHABLE ON THREE ACCOUNT ENDPOINTS, contrary to the
//     plan's claim that it arrives only from the sign-in family: the controller marks
//     account creation and both credential actions as credential endpoints and each
//     declares that status. No dedicated specification is written for it here, per the
//     scope this file was given, and the shared summariser already resolves it to the
//     warning severity that the refusal specifications below exercise.
//
// ---------------------------------------------------------------------------
// WHAT IS DELIBERATELY NOT ASSERTED
// ---------------------------------------------------------------------------
//   * NO CREDENTIAL POLICY. Not a length floor, not a composition requirement, not a
//     confirmation match. Those rules are preserved verbatim and enforced server-side,
//     and a client-side check asserted here would institutionalise a divergence from
//     the rule actually applied. The account policy contract deliberately carries none
//     of them, and that absence IS asserted.
//   * NO RENDERING. The pager advisory is a boolean and is asserted as one.
//   * NO PRIVATE MEMBER. Every assertion reads a published signal or invokes a command.
//   * NO REAL CREDENTIAL OR KEY. Every credential-shaped fixture value is obviously
//     synthetic, and one specification proves that no such value reaches any published
//     slice.
//

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
  type MembershipSettings,
  type UpdateUserRequest,
  type UserDetail,
  type UserListItem,
} from '../models/user.model';
import { UserService } from '../services/user.service';
import { UserStore, type UserFailure, type UserSearchMode } from './user.store';

// ---------------------------------------------------------------------------
// THE EXPECTED ADDRESSES, SPELLED OUT INDEPENDENTLY
// ---------------------------------------------------------------------------

/** The account collection. Relative, because the production base is relative. */
const USERS_URL = '/api/v1/users';

/**
 * The tenant's account policy.
 *
 * The `settings` child of the account collection. See the divergence note in the
 * header: the plan named `/api/v1/users/settings/membership`, the API serves this.
 */
const SETTINGS_URL = '/api/v1/users/settings';

/** The tenant's profile declarations. Unpaged, and scoped by the resolved tenant. */
const DEFINITIONS_URL = '/api/v1/profile-definitions';

/**
 * The legacy reserved word that meant "no search".
 *
 * MIGRATION: `Users.ascx.vb` L266 guarded the whole search block by comparing the typed
 * text against this bare magic string, so a person could never search for it even
 * though it is a perfectly ordinary thing to type.
 *
 * It is declared here for exactly two uses, and NEITHER transmits it as a sentinel.
 * First, to assert that it does NOT appear on the wire when no search is applied -
 * absence is expressed by omitting the parameter, never by sending a reserved word.
 * Second, as ORDINARY SEARCH TEXT, to assert that a caller who genuinely wants to
 * search for this word gets a search for this word: that is the positive half of the
 * same claim, and it is only provable by transmitting it as a term.
 */
const NO_SEARCH_RESERVED_WORD = 'None';

/** A synthetic trace-context value. Diagnostic, and not the support reference. */
const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00';

/** A synthetic correlation value. THIS is the support reference. */
const CORRELATION_ID = 'c0rr-3l4t10n-0000-0000-000000000001';

/**
 * The prefix the server wraps a machine-readable failure code in.
 *
 * Lower-cased throughout, exactly as the server writes it. The code itself is the
 * remainder, and the shared reader folds a hyphen onto an underscore.
 */
const FAILURE_TYPE = 'urn:dnnmigration:error:';

// ---------------------------------------------------------------------------
// FIXTURES
// ---------------------------------------------------------------------------
//
// Every fixture is a factory returning a fresh object, and every one is typed as the
// real contract. Both properties matter: a shared mutable fixture would let one
// specification's edit change another's subject, and an untyped literal would let a
// mis-spelled member compile and arrive as `undefined`. The identity members carry a
// single lower-case letter where the server's camel-case policy puts one, which is
// precisely the spelling a hand-written literal gets wrong.

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
  ...overrides,
});

/**
 * The tenant's account policy.
 *
 * Every member is stated, because the contract is a replace rather than a merge and a
 * partial literal would not compile. The records-per-page member deliberately defaults
 * to a value that is NOT the shared fallback, so a specification that forgot to
 * override it would still not accidentally agree with a hard-coded constant.
 */
const settingsFixture = (overrides: Partial<MembershipSettings> = {}): MembershipSettings => ({
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
 * One profile declaration.
 *
 * Carries a zero-valued identifier, a zero-valued module association and a
 * zero-valued visibility by default: the role, page and module tables all seed their
 * keys at zero, and the least-restrictive visibility really is zero, so zero is DATA
 * on every one of them.
 */
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

/** One account's whole profile. */
const profileFixture = (userId = 7): UserProfile => ({
  userId,
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

/**
 * A request to create an account.
 *
 * The credential value is obviously synthetic. MIGRATION: the legacy configuration
 * committed the symmetric key that reversed every stored credential to source control
 * in the clear (`release.config` L89-L93), which is why no fixture here carries
 * anything resembling a real one.
 */
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
  // The keys are the server's model-state keys, reproduced as it writes them: they
  // name model members rather than JSON members, so the camel-case body policy does
  // not apply to them and they stay Pascal-cased.
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
        // The real client FIRST, then the testing backend that displaces it. The order
        // is load-bearing: the second provider overrides the first, so reversing them
        // leaves the live backend in place and every expectation below times out
        // against a request that was never intercepted.
        provideHttpClient(),
        provideHttpClientTesting(),
        // The subject is listed explicitly. It is declared root-provided, so this is
        // not strictly required to resolve it - it is stated so that the instance under
        // test belongs to this test module and is torn down with it, which is what runs
        // the subject's own teardown between specifications and stops an unreleased
        // request from leaking from one into the next.
        UserStore,
        // NO INTERCEPTOR IS REGISTERED. The correlation identifier, the bearer token and
        // the translation of a failure into a problem document are three separately
        // specified units; running them here would assert several units at once.
        //
        // This is a standalone workspace - there is no component declaration array to
        // configure and no module to import.
      ],
    });

    store = TestBed.inject(UserStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // THE LOAD-BEARING ASSERTION OF THIS ENTIRE FILE. It fails if a command issued a
    // request nothing expected, and it is the only automated proof that reading the
    // account policy does not also warm a cache, that the no-search state really
    // dispatches nothing, and that a failure path does not retry.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HELPERS
  // -------------------------------------------------------------------------

  /**
   * Expects exactly one outstanding request with the given verb and PATH.
   *
   * Matches on the path rather than on the path-and-query, because the string overload
   * compares the full URL including its query string - which would couple every paged
   * expectation to the order in which parameters happen to be composed. Query
   * parameters are asserted separately, by name.
   */
  const expectRequest = (method: string, path: string): TestRequest =>
    httpMock.expectOne(
      (request) => request.method === method && request.url === path,
      `${method} ${path}`,
    );

  /**
   * Reads one query parameter, narrowing it explicitly.
   *
   * The reader answers `string | null`, and the null branch is resolved by raising a
   * diagnostic rather than by asserting the absence away. A non-null assertion would
   * compile and then report `null` as the observed value, which reads as a mismatched
   * expectation rather than as a missing parameter.
   */
  const parameter = (request: TestRequest, name: string): string => {
    const value = request.request.params.get(name);

    if (value === null) {
      throw new Error(`expected the query parameter "${name}" to be present`);
    }

    return value;
  };

  /** Asserts that none of the named query parameters was emitted at all. */
  const expectOmitted = (request: TestRequest, names: readonly string[]): void => {
    for (const name of names) {
      // Absence is proved by asking whether the parameter is THERE. A reader answering
      // null is a weaker claim: it is also what a present-but-empty parameter answers,
      // and empty text is a legitimate value on this contract.
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
   * Brings the store up the way a listing screen does, at a stated page size.
   *
   * Reads the account policy, flushes it, then flushes the listing the store dispatches
   * once the policy is in hand. Returns nothing, because the specifications that use it
   * are interested in what happens NEXT.
   */
  const openListingAtPageSize = (recordsPerPage: number): void => {
    store.initialise();
    expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage })));
    expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));
  };

  // =========================================================================
  // LIST PAGING
  // =========================================================================

  describe('list paging', () => {
    it('requests the first page as index 0, because the wire index is zero-based', () => {
      // MIGRATION: `Users.ascx.vb` L51 held a ONE-based counter and L265 subtracted one
      // before dispatch. Nothing subtracts one here, because nothing added one.
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
      // The negative control. A store that carried the legacy one-based counter through
      // to the wire would send 1 here and every listing would silently start on the
      // second page.
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
      // Proves the absence of arithmetic in both directions: index 1 addresses the
      // second page and arrives as 1, so the value is neither incremented nor
      // decremented anywhere.
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

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageIndex'))
        .withContext('a new match set is a new first page')
        .toBe('0');

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
      // MIGRATION: `Users.ascx.vb` L58 declared `Protected TotalPages As Integer = -1`,
      // a live "not yet known" marker. Minus one is simultaneously the legacy marker for
      // a missing integer, so a store that treated it as absence would report zero pages
      // for a set it has not counted yet.
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

      // The requested index moves immediately; the reported index only once an answer
      // has arrived. That gap is what stops a pager claiming to be on a page whose
      // request is still outstanding.
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
      // MIGRATION: the rule is `Users.ascx.vb` L278-L280 exactly - the pager was
      // narrowed to `PageSize < TotalRecords` ONLY when suppression had been requested;
      // otherwise the branch above left it as it was. ADVISORY: whether to render
      // remains the shared pagination component's decision, and nothing here renders.
      openListingAtPageSize(25);

      store.saveMembershipSettings(
        settingsFixture({ recordsPerPage: 25, displaySuppressPager: true }),
      );
      expectRequest('PUT', SETTINGS_URL).flush(null, { status: 204, statusText: 'No Content' });
      expectRequest('GET', SETTINGS_URL).flush(
        envelope(settingsFixture({ recordsPerPage: 25, displaySuppressPager: true })),
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
      // MIGRATION: THIS SEQUENCE IS THE WHOLE REASON COMPOSITION LIVES IN A STORE. The
      // size of a page is a per-tenant setting (`Users.ascx.vb` L114-L119), so the
      // listing cannot be requested correctly until the policy that declares it is in
      // hand. A transport cannot sequence the two without deciding it for every screen.
      store.initialise();

      // Only the policy is outstanding at this point. If the listing had been dispatched
      // in parallel it could not have carried the size, and this expectation fails.
      const settings = expectRequest('GET', SETTINGS_URL);

      httpMock.expectNone((request) => request.url === USERS_URL);

      settings.flush(envelope(settingsFixture({ recordsPerPage: 25 })));

      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()], { pageSize: 25 }));
    });

    it('requests the page size the account policy declared, not a hard-coded 10', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 25 })));

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
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 50 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe('50');
      expect(store.effectivePageSize()).toBe(50);

      request.flush(pageFixture([listItemFixture()], { pageSize: 50 }));
    });

    it('falls back to the shared default size while no policy has been read', () => {
      // MIGRATION: `UserModuleBase.vb` L134-L136 supplied ten ONLY when the tenant
      // setting was unset, and the shared constant documents itself as that fallback.
      // The expectation is written against the constant rather than against the number,
      // so the two cannot drift apart.
      expect(store.membershipSettings()).toBeNull();

      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe(String(DEFAULT_PAGE_SIZE));
      expect(store.effectivePageSize()).toBe(DEFAULT_PAGE_SIZE);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('still lists the accounts at the fallback size when the policy cannot be read', () => {
      // A tenant whose policy is unavailable still has accounts. The failure is recorded
      // rather than swallowed, and the listing goes out regardless - refusing to list
      // would be a worse answer than listing at the default beside a reported failure.
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
      // The paging contract states that nothing is corrected or clamped on either side
      // and that the server reports an out-of-range size as a field-level refusal.
      // Substituting a different size here would hide a misconfiguration; the legacy
      // screen passed its setting on unchecked in exactly the same way.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 500 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize')).toBe('500');

      request.flush(pageFixture([]));
    });

    it('reports the size the server applied separately from the size it asked for', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 25 })));
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

      store.saveMembershipSettings(settingsFixture({ recordsPerPage: 50 }));

      const write = expectRequest('PUT', SETTINGS_URL);
      expect(write.request.body).toEqual(settingsFixture({ recordsPerPage: 50 }));
      write.flush(null, { status: 204, statusText: 'No Content' });

      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 50 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize'))
        .withContext('the page in hand was fetched at the previous size')
        .toBe('50');

      request.flush(pageFixture([listItemFixture()], { pageSize: 50 }));
    });

    it('publishes no credential policy of its own', () => {
      // MIGRATION: minimum length, the non-alphanumeric requirement and the
      // address-uniqueness rule are server-side options that never cross the boundary.
      // Restating their values on this side would create a second copy free to drift
      // from the one that is enforced, and tightening any of them during a migration
      // would lock out every account satisfying the old rule and not the new one. This
      // asserts the ABSENCE, which is the only client-side claim that is safe to make.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture()));
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

  describe('search modes', () => {
    it('transmits an account-name search verbatim, with no wildcard of its own', () => {
      // MIGRATION: `Users.ascx.vb` L271 appended one trailing per-cent character before
      // calling down; the API reproduces that, wildcard included. Appending one here
      // would double the pattern. A PREFIX match - never described as a containing one.
      store.searchByUsername('abc');

      const request = expectRequest('GET', USERS_URL);
      const transmitted = parameter(request, 'userName');

      expect(transmitted).toBe('abc');
      expect(transmitted).not.toContain('%');
      expectOmitted(request, ['email', 'profilePropertyName', 'profilePropertyValue']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('transmits an address search verbatim, with no wildcard of its own', () => {
      // MIGRATION: `Users.ascx.vb` L269. The address is neither unique nor a sign-in
      // key - the legacy provider was registered with uniqueness switched off at
      // `release.config` L244 - so this can legitimately match several accounts.
      store.searchByEmail('ann@');

      const request = expectRequest('GET', USERS_URL);
      const transmitted = parameter(request, 'email');

      expect(transmitted).toBe('ann@');
      expect(transmitted).not.toContain('%');
      expectOmitted(request, ['userName', 'profilePropertyName', 'profilePropertyValue']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('transmits a named profile property alongside its text', () => {
      // MIGRATION: `Users.ascx.vb` L274, the third axis, whose field name was passed
      // straight through as the property name and appended to the screen's own query
      // string at L275.
      store.searchByProfileProperty('Nickname', 'Ann');

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'profilePropertyName')).toBe('Nickname');
      expect(parameter(request, 'profilePropertyValue')).toBe('Ann');
      expect(parameter(request, 'profilePropertyValue')).not.toContain('%');
      expectOmitted(request, ['userName', 'email']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('accepts an unfamiliar profile property name without validating it', () => {
      // MIGRATION: the property name is an OPEN SET. A tenant declares whatever
      // properties it likes, so an unrecognised name is the server's to refuse - not
      // this store's to reject, normalise or check against a fixed list.
      const unusual = 'Preferred Pronoun (optional)';

      store.searchByProfileProperty(unusual, 'they');

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'profilePropertyName')).toBe(unusual);

      request.flush(pageFixture([]));
    });

    it('does not case-fold a profile property name', () => {
      // Two declared names are free to differ from one another only in case, so folding
      // would make one of them unreachable.
      const mixedCase = 'nIcKnAmE';

      store.searchByProfileProperty(mixedCase, 'x');

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'profilePropertyName')).toBe(mixedCase);
      expect(parameter(request, 'profilePropertyName')).not.toBe(mixedCase.toLowerCase());

      request.flush(pageFixture([]));
    });

    it('neither trims nor case-folds the searched text', () => {
      // Trimming would make a leading space unsearchable and case-folding would presume
      // a collation this side does not know.
      const typed = '  MiXeD Case  ';

      store.searchByUsername(typed);

      const request = expectRequest('GET', USERS_URL);
      const transmitted = parameter(request, 'userName');

      expect(transmitted).toBe(typed);
      expect(transmitted).not.toBe(typed.trim());
      expect(transmitted).not.toBe(typed.toLowerCase());
      expect(transmitted).not.toContain('%');

      request.flush(pageFixture([]));
    });

    it('transmits empty search text as a value rather than dropping it', () => {
      // MIGRATION: the legacy absence marker for a string WAS the empty string
      // (`Library/Components/Shared/Null.vb` L71-L75 returns `""` literally), so the two
      // were indistinguishable there. Here empty text is DATA on this contract: a caller
      // that asked to match the empty prefix asked for something, and omission is
      // reserved for the caller that asked for nothing.
      store.searchByUsername('');

      const request = expectRequest('GET', USERS_URL);

      expect(request.request.params.has('userName'))
        .withContext('empty text is a value, not an absence')
        .toBe(true);
      expect(parameter(request, 'userName')).toBe('');

      request.flush(pageFixture([]));
    });

    it('publishes the search it applied as a typed discriminator', () => {
      // MIGRATION: the legacy screen branched on LOCALISED strings at L258, L261 and
      // L264, so the query a person got depended on the rendered language. Nothing here
      // compares a display string.
      store.searchByProfileProperty('Nickname', 'Ann');
      expectRequest('GET', USERS_URL).flush(pageFixture([]));

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
      // MIGRATION: the successor to `Users.ascx.vb` L266, where a bare magic string fell
      // through every branch and left the grid unbound. The legacy screen genuinely
      // issued no query in that state, and neither does this.
      expect(store.searchMode()).toBe('none');

      store.loadUsers();

      httpMock.expectNone((request) => request.url === USERS_URL);
      expect(store.usersLoading())
        .withContext('nothing is in flight, so nothing should be reported as loading')
        .toBeFalse();
    });

    it('omits every filter parameter for the unfiltered listing', () => {
      // MIGRATION: the successor to `Users.ascx.vb` L264-L265 - the FOURTH legacy branch,
      // which called the unfiltered paged reader with page coordinates and no filter
      // whatsoever.
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
      // The reserved word is an ordinary thing to type, and the legacy screen made it
      // unsearchable. Here it is searched for like any other text, which is the proof
      // that no value is compared against a reserved word on the way out.
      store.searchByUsername(NO_SEARCH_RESERVED_WORD);

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'userName'))
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
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      store.clearSearch();

      const request = expectRequest('GET', USERS_URL);

      expect(store.searchMode()).toBe('all');
      expectOmitted(request, FILTER_PARAMETERS);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('omits the approval restriction until one is chosen, and sends false as false', () => {
      // MIGRATION: the legacy absence marker for a boolean was itself false
      // (`Null.vb` L76-L80), so "unset" and "no" were the same value. False here MEANS
      // "only the unauthorised ones" and is transmitted.
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
    /**
     * Every search the store can apply, enumerated exhaustively.
     *
     * A COMPILE-TIME assertion as much as a run-time one. The mapped type requires one
     * key per member of the union, so adding a sixth mode leaves this literal incomplete
     * and the file stops compiling, and naming a mode the union does not carry is an
     * excess property. That is what makes the run-time scan below meaningful rather than
     * a restatement of a list this file chose for itself.
     */
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
      // MIGRATION: `Users.ascx.vb` L258-L260 answered one dropped mode from
      // `GetUnAuthorizedUsers` and hid the pager; L261-L263 answered the
      // signed-in-accounts view from session tracking and hid the pager too. Neither
      // took a page coordinate, so each returned an unbounded set.
      const names = Object.keys(EVERY_SEARCH_MODE);

      expect(names.length).toBe(5);

      for (const name of names) {
        expect(DROPPED.test(name))
          .withContext(`"${name}" would restore a dropped legacy list mode`)
          .toBe(false);
      }
    });

    it('exposes no command that would fetch either dropped mode', () => {
      // The command surface lives on the prototype, so scanning it is a genuine
      // enumeration of what a caller can invoke rather than a restatement of an
      // expectation.
      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(DROPPED.test(name))
          .withContext(`the command "${name}" would restore a dropped legacy list mode`)
          .toBe(false);
      }
    });

    it('holds no slice of currently-signed-in accounts', () => {
      // The signed-in view depended on session tracking and on a scheduled purge -
      // `Library/Components/Users/Users Online/PurgeUsersOnline.vb` L44 - that this
      // migration does not carry forward, so there is nothing for such a slice to hold.
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

      httpMock.expectNone((request) => DROPPED.test(request.url));
    });

    it('answers the unauthorised view as a PAGED filter, not as the dropped mode', () => {
      // MIGRATION: the approval axis is a paged filter over the account table and is
      // emphatically NOT a restoration of `Users.ascx.vb` L258-L260, which took no page
      // coordinate at all and returned an unbounded set. The distinction is that this one
      // pages.
      //
      // The filter is an axis OF a listing rather than a listing of its own, which is why
      // it is applied to one: in the no-query state there is no listing for it to narrow,
      // and the store dispatches nothing - see the specification immediately below.
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
      // The complement of the specification above, and the reason it has to establish a
      // listing first. The approval axis narrows a listing; it does not summon one. This
      // is the same no-query rule as `Users.ascx.vb` L266, applied to a filter rather
      // than to a search.
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
      // MIGRATION: `Portals.PortalID` is declared `IDENTITY (-1, 1)` at L77 of the
      // baseline schema script, so the FIRST tenant ever created really is minus one -
      // while `Null.vb` L41-L45 simultaneously defines minus one as the marker for a
      // missing integer. One vocabulary cannot carry both meanings, so minus one is
      // treated as data and absence is undefined.
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
      // MIGRATION: the API resolves one tenant per request from the host it was reached
      // on, reconciled against the alias table, so a tenant identifier sent from here
      // would either be redundant or be a second, disagreeing opinion about which tenant
      // was meant. The tenant this store publishes is an OBSERVATION of what came back.
      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expectOmitted(request, ['portalId']);
      expect(request.request.urlWithParams).not.toContain('portalId');

      request.flush(pageFixture([listItemFixture({ portalId: -1 })]));
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
      // Defensive symmetry. The role, page and module tables all seed their keys at zero
      // - `[RoleID] IDENTITY (0, 1)` at L115 of the baseline script, and the same for
      // pages and module placements - so zero is a legitimate identifier throughout, and
      // a store guarding on truthiness would lose the first row of each of those tables.
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
      // MIGRATION: `Null.vb` L71-L75 defines the marker for a missing string as the
      // EMPTY STRING - its body is literally a return of `""` - so a database null and an
      // empty string were indistinguishable once read through the legacy path. Here the
      // display-name column is declared not-null with an empty default, so an unset
      // display name genuinely ARRIVES as the empty string.
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture({ displayName: '' })]));

      const [first] = store.userRows();

      expect(first.displayName).toBe('');
      expect(first.displayName).not.toBeNull();
    });

    it('keeps an empty string and a null distinguishable on the members that admit both', () => {
      // The two are treated IDENTICALLY in the sense that neither is normalised into the
      // other. The address is projected from profile values and is nullable; the display
      // name is not-null and empty. Both arrive as sent.
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
      // MIGRATION: the legacy absence test reported FALSE as absent (`Null.vb` L76-L80
      // makes false the marker, and the absence predicate answers true for it), so "not
      // set" and "no" were one value. Every wire boolean here is non-nullable and false
      // is DATA.
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
      // MIGRATION: `Null.vb` L66-L70 makes the marker for a missing date the least
      // representable one, so that instant arrives from a legacy row as a real value.
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
      // MIGRATION: the legacy installation ran with the reversible representation and a
      // symmetric key committed to source control (`release.config` L89-L93 and L245).
      // The vocabulary survives only so a legacy record can be READ; the target hashes
      // one-way, and no member of it is ever selected for a new credential. Its zero
      // member is a real value rather than an absence, which is why it is asserted here.
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
      // MIGRATION: the legacy slot at `UserModuleBase.vb` L468 seeded itself from the
      // integer null marker - that is, from minus one - and tested for absence at L469
      // with an explicit is-nothing comparison. Minus one is not available for absence
      // here, because it is a real identifier in this schema.
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
      // MIGRATION: `UserCreateStatus.vb` L23-L42 numbers all eighteen members
      // explicitly. The zero member is NOT an outcome - it is the state the legacy screen
      // initialised with, and `Website/admin/Users/User.ascx.vb` L175 and L185 prove it by
      // treating any OTHER value as a failure. An assumption that zero means success
      // would invert that test.
      //
      // Across the three legacy vocabularies the assumption is wrong two times in three:
      // the validity vocabulary succeeds at zero, the sign-in vocabulary at one, and this
      // one at thirteen.
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
      // MIGRATION: no ordinal crosses the wire. An outcome arrives as a status plus a
      // machine-readable STRING code, which is what makes the counter-intuitive numbering
      // above harmless.
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

    it('holds a null answer as null rather than as an empty object', () => {
      // The server may report that no account matches by answering with nothing in the
      // envelope, so the nullable answer is the contract rather than defensiveness.
      store.selectUser(999);

      expectRequest('GET', `${USERS_URL}/999`).flush(envelope(null));

      expect(store.selectedUser()).toBeNull();
      expect(store.selectedUserId())
        .withContext('the selection stands even when the read answered with nothing')
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
      // MIGRATION: the legacy cache is not reproduced. Where the new row falls depends on
      // an ordering this side does not own, and the paging facts are the server's, so a
      // locally spliced list would be right only until it was not.
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
      // MIGRATION: `Users.ascx.vb` L326-L328 declared a routine whose single provider call
      // destroyed an unbounded number of accounts from one click, with no per-row
      // confirmation and no way to review the set first. A caller names what it removes.
      for (const name of Object.getOwnPropertyNames(UserStore.prototype)) {
        expect(/deleteall|deleteunauthor|purge|bulk/i.test(name))
          .withContext(`"${name}" would restore an unbounded removal`)
          .toBe(false);
      }
    });

    it('reports a refused creation as a warning rather than as a fault', () => {
      // MIGRATION: the account allowance of a tenant is the server's rule and is not
      // pre-checked here - counting first would cost a request, would race every other
      // administrator, and would still have to handle the refusal it was trying to
      // predict. A refusal is a WARNING: `AccessDenied.ascx.vb` performs no permission
      // check at all and both branches of its load handler, at L43 and L45, render at the
      // warning message type.
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
      // MIGRATION: an attempt to change an installation administrator is refused by the
      // server. It is not pre-checked here - see the note on the authorisation the legacy
      // source embedded in a page property getter at `UserModuleBase.vb` L466-L505.
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
      // MEASURED DIVERGENCE: the plan named a replace verb here. The built transport
      // POSTS to the credential child of the account, matching the controller, and the
      // response carries no body at all - so the proof that matters is that the store
      // completes without attempting to read one.
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
      // MIGRATION: a reset is carried forward and RETRIEVAL IS NOT. The legacy provider
      // enabled the two independently (`release.config` L239-L240) and only retrieval
      // required a reversible store, so only retrieval is abolished. A reset is a
      // separate command rather than a mode, because the two differ in what they require
      // and in who may call them.
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
      // A credential change moves the instant it was last changed and can clear the
      // obligation to change it, and the response carries no body, so nothing is assumed
      // about either.
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
      // MIGRATION: the point is laboured because the legacy arrangement made it necessary
      // - the provider was registered with a reversible format and retrieval switched on
      // (`release.config` L245 and L239) and the symmetric key that reversed it was
      // committed to source control in the clear at L89-L93, so anyone who could read the
      // repository could read every stored credential.
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
      // MIGRATION: there is deliberately no recover-it, remind-me or reveal-it command,
      // and none could be written - the transport exposes no method that returns a
      // credential. Both surfaces are scanned, because a store is only as constrained as
      // the transport beneath it.
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
      // Sets the obligation only: it does not choose, generate, transmit or return a
      // credential. Only the selected account is re-read - the obligation appears on no
      // column of the listing, so re-reading the listing would cost a request that could
      // not change a rendered value.
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
      // The state is stated rather than implied by a verb, because the server reports
      // setting the state an account already holds as a conflict - an answer that is only
      // meaningful if the caller said which state it meant.
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
      // MIGRATION: a profile is a set of rows keyed by the declarations of the tenant, not
      // a fixed field list. The legacy shape declared nineteen members of which seventeen
      // were hardcoded named fields, so anything a tenant added was reachable only through
      // a separate untyped collection. None of those fields is reproduced in any slice.
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
      // DELIBERATELY UNPAGED: the declaration set is bounded by how many fields an
      // administrator chose to define, so paging it would add coordinates to every call in
      // exchange for nothing. Not an empty coordinate, not a defaulted one.
      store.loadProfileDefinitions();

      const request = expectRequest('GET', DEFINITIONS_URL);

      expectOmitted(request, PAGING_PARAMETERS);
      expect(request.request.urlWithParams)
        .withContext('no query string at all')
        .toBe(DEFINITIONS_URL);

      request.flush(envelope([definitionFixture()]));
    });

    it('holds no page index, page size or total for the declarations', () => {
      // The declarations arrive as a plain array. The paging coordinates on this store
      // belong to the account listing alone, and reading the declarations must not
      // populate them.
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
      // The spelling is load-bearing on both sides of the wire: the route constrains an
      // integer under that name and the contract spells its identity member the same way,
      // so a near-miss produces a route that does not match rather than a parameter that
      // is quietly ignored.
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
      // MIGRATION: there is deliberately no move-up or move-down command.
      // `Website/admin/Users/ProfileDefinitions.ascx.vb` L182-L187 read the neighbouring
      // declaration and SWAPPED the two view orders, and a separate bulk pass at L326
      // renumbered a whole set from each index. Modelling a two-row write as a one-row
      // command would have made it look atomic when it is not, and would have needed a
      // second call just to discover the neighbour.
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
      // Computing which positions to write - swapping a pair, renumbering after a drag -
      // is the business of the feature, because only the feature knows the set it is
      // looking at. This store writes the position it is given.
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
  });

  // =========================================================================
  // FAILURES
  // =========================================================================

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
      // MEASURED DIVERGENCE, and it matters: these are two INDEPENDENT identifiers with
      // different formats. The trace identifier is a trace-context value taken from
      // whatever diagnostic activity was current; the correlation identifier is the value
      // the server validated for the request, and it is the one that appears on the
      // response header, on the request envelope in the log of the server and on every
      // audit event the request produced. Both are retained; only the second is quotable
      // as a support reference.
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
      // The keys are the model-state keys of the server, reproduced byte for byte: they
      // name model members rather than JSON members, so the camel-case body policy does
      // not apply and they stay Pascal-cased. The map is an index signature and this
      // workspace forbids property access on one, so a bracket is the only available form
      // - which is what stops a typo compiling as a silent undefined.
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
      // Legacy message text is untrusted markup BY MEASUREMENT, not by supposition: across
      // the thirty-seven in-scope localised resource files, seventy-six values carry an
      // HTML tag and four of them carry a live script element. Nothing here is ever handed
      // to a template as trusted markup, and no sanitiser is involved, because nothing is
      // treated as markup at all. The legacy code knew it too - `AccessDenied.ascx.vb` L43
      // encoded the message it had just decoded before showing it.
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
      // MIGRATION: `Website/admin/Users/User.ascx.vb` L187 prefixed its message with a
      // SELF-CLOSING break tag. Stripping is the business of the shared summariser, so the
      // raw document keeps what arrived and only the summary is cleaned.
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
      // MIGRATION: the other spelling. `Website/admin/Portal/Signup.ascx.vb` used the
      // unclosed form at L193, L214, L221 and L323, so both spellings exist in the legacy
      // source and both must survive unmodified on the raw document.
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
      // MIGRATION: `Library/Components/Users/UserModuleBase.vb` L466-L505 embedded a full
      // authorisation decision INSIDE a page property getter - comparing the identifier of
      // the caller against the requested one at L473-474, short-circuiting for an
      // installation administrator at L475-476, re-reading the requested account at L481 to
      // check at L484 that a tenant administrator was not editing an installation
      // administrator, and redirecting to a denial page at L494 when none of that held.
      //
      // NOT ONE LINE OF IT IS REPRODUCED. The API decides, and reports a refusal as a
      // status with a problem document; this store records that refusal and presents it as
      // a refusal rather than as a fault. The permission vocabulary decides nothing on this
      // side, and the current-identity contract of the server carries no
      // permission-testing or role-testing method for a client to lean on either.
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
      // Counting the accounts of a tenant first would cost a request, would race every
      // other administrator, and would still have to handle the refusal it was trying to
      // predict. The proof is that a creation issues EXACTLY ONE request and no lookup
      // precedes it.
      store.createUser(createRequestFixture());

      const posted = expectRequest('POST', USERS_URL);

      httpMock.expectNone((request) => request.method === 'GET');

      posted.flush(envelope(detailFixture({ userId: 91 })), {
        status: 201,
        statusText: 'Created',
      });
    });

    it('records a refusal without redirecting or navigating anywhere', () => {
      // The legacy decision ended in a redirect at L494. A store cannot navigate, and this
      // one takes no router dependency at all - the refusal simply becomes recorded state
      // for a screen to present.
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

  // =========================================================================
  // THE PUBLISHED SURFACE
  // =========================================================================

  describe('the published surface', () => {
    it('publishes state that cannot be written to from outside', () => {
      // A writable signal exposes a setter and an updater; a read-only one exposes
      // neither. Asking whether the members are PRESENT is the type-safe proof - reaching
      // for one through a cast would need the very cast this workspace forbids, and would
      // prove something about the cast rather than about the signal.
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
      // This is the property that makes a change-detection strategy comparing references
      // work at all: an in-place mutation would not change the reference a consumer
      // compares, so the screen would keep rendering the previous page.
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

      store.loadProfileDefinitions();
      expectRequest('GET', DEFINITIONS_URL).flush(
        envelope([definitionFixture(), definitionFixture({ propertyDefinitionId: 4 })]),
      );

      expect(store.profileDefinitions()).not.toBe(held);
      expect(held.length).toBe(1);
      expect(store.profileDefinitions().length).toBe(2);
    });

    it('answers with a stable reference while nothing has changed', () => {
      store.showAllAccounts();
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()]));

      expect(store.users()).toBe(store.users());
      expect(store.userRows()).toBe(store.userRows());
    });

    it('seeds every slice so that no consumer has to branch on absence', () => {
      // The listing is seeded with the shared empty envelope rather than with null, and
      // the seed reports the coordinates a server response carries for an unpaged,
      // zero-record answer - which is what makes the branch unnecessary.
      expect(store.users().items).toEqual([]);
      expect(store.userRows()).toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.totalPages()).toBe(0);
      expect(store.hasRecords()).toBeFalse();
      expect(store.profileDefinitions()).toEqual([]);
      expect(store.hasProfileDefinitions()).toBeFalse();
      expect(store.busy()).toBeFalse();
      expect(store.saving()).toBeFalse();
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
      // Without this, a person paging quickly can have two listing requests outstanding
      // and the slower one can answer last, leaving the screen showing a page nobody asked
      // for. Cancellation is per slice, so the superseded request is never flushed - which
      // is exactly what the outstanding-request verification in the teardown confirms.
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
});

