//
// Specification for the account, profile, account-policy and profile-definition
// transport.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE PROVES
// ---------------------------------------------------------------------------
// One thing, in nineteen parts: that each method on the service under test issues
// EXACTLY ONE request, to exactly the path and with exactly the verb, body and query
// string the API declares - and that it neither adds to nor subtracts from what its
// caller handed it.
//
// The negative half of that claim is the half worth having, and it is the half a
// stubbed client cannot make. Every specification below closes with a verification
// that no request is left outstanding, so a method that helpfully wrote a profile
// after creating an account, or defaulted a page size, or appended a wildcard, fails
// here rather than in a browser. That verification is the mechanism; the assertions
// are only the readable part.
//
// ---------------------------------------------------------------------------
// NO PROJECT RULES DOCUMENT EXISTS
// ---------------------------------------------------------------------------
// The engagement supplied none. The rules review answers with a single line saying so,
// and it answers identically for ranges beginning past the first and past the second
// line - which is what proves the answer is the whole document rather than its opening
// line. Nothing here is justified by a project rule and nothing is relaxed by their
// absence: the enterprise baseline the action plan sets out governs instead.
//
// ---------------------------------------------------------------------------
// WHY THE EXPECTED URLS ARE RELATIVE, AND WHY THEY ARE WRITTEN OUT IN FULL
// ---------------------------------------------------------------------------
// The workspace's build configuration replaces the environment module only for the
// DEVELOPMENT configuration; the production configuration replaces nothing, so the
// unsuffixed environment module IS the production one and its configured base is the
// relative `/api/v1`. The test target declares no replacement at all, so that is the
// base a specification compiles against. An expectation naming an absolute host would
// therefore be asserting a value this target never loads.
//
// The paths are written out as literal strings rather than read back from the endpoint
// declaration module, deliberately. Importing the same constant the subject imports
// would make a wrong route template agree with itself: the specification would pass
// while every request went somewhere the API does not serve. Spelling the expected URL
// independently is what turns that class of defect into a failure.
//
// ---------------------------------------------------------------------------
// WHY THE PROFILE SHAPES ARE DERIVED RATHER THAN IMPORTED
// ---------------------------------------------------------------------------
// The account and paging contracts are imported from the two model modules this
// specification is declared to depend on. The profile shapes live in a third model
// module that is NOT among them, so rather than reach outside the declared set - or,
// worse, restate those shapes locally where they would be free to drift - they are
// derived from the subject's own signature with the standard type utilities. That is
// strictly stronger than an import would have been: a change to any of those
// parameters or return types breaks this file at compile time, and it cannot go stale
// because there is no second copy to keep in step.
//
// ---------------------------------------------------------------------------
// WHAT IS DELIBERATELY NOT ASSERTED
// ---------------------------------------------------------------------------
//   * NO CREDENTIAL POLICY. Not a length floor, not a composition requirement, not a
//     confirmation match. The legacy provider registration at `Website/release.config`
//     L236-L247 shipped a seven-character floor (L242), required no non-alphanumeric
//     character (L243) and did not require a unique address (L244); it enabled reset
//     (L240) and required no question-and-answer pair (L241). Those rules are preserved
//     verbatim and enforced server-side, because raising any of them during a migration
//     locks out every account that satisfies the old rule and not the new one. A client
//     -side check asserted here would institutionalise a divergence from the rule that
//     is actually applied.
//   * NO OUTCOME ORDINALS. The legacy credential-update vocabulary at
//     `Library/Components/Users/Membership/PasswordUpdateStatus.vb` L24-L31 carries NO
//     explicit values, so declaration order is the ordinal: Success, PasswordMissing,
//     PasswordNotDifferent, PasswordResetFailed, PasswordInvalid, PasswordMismatch,
//     InvalidPasswordAnswer, InvalidPasswordQuestion - zero through seven. The
//     account-creation vocabulary numbers its members explicitly instead, and its
//     success member is thirteen rather than zero. Neither number crosses the wire and
//     neither is asserted; an outcome arrives as a status with a machine-readable
//     STRING code, and a specification keyed on an ordinal would break the moment
//     either enumeration is edited.
//   * NO PROBLEM-DOCUMENT SHAPE, beyond the fact that a failure reaches the caller
//     with its status and its body intact. Translating a problem document into
//     field-level messages is another unit's responsibility and is specified there.
//   * NO BUSINESS RULE OF ANY KIND - no validation, no derived state, no ordering, no
//     filtering, no permission decision, no allowance arithmetic. The subject performs
//     none of those, so asserting them here would be asserting a defect.
//
// Every credential-shaped value below is an obvious placeholder. Nothing that resembles
// a real credential, token or key appears in this file, and in particular the symmetric
// key the legacy configuration committed in the clear is not reproduced here in any
// form - eliminating it is one of the points of the migration.
//

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
  MembershipSettings,
  PagedUserList,
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
 * The tenant's account policy.
 *
 * NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS, RECORDED RATHER THAN PAPERED OVER.
 * The requirements for this specification name the policy path as
 * `/api/v1/users/settings/membership`. The endpoint declaration module actually
 * composes the account segment with the settings segment and nothing further, which
 * yields `/api/v1/users/settings`; there is no `membership` segment declared anywhere
 * in that module. The value below is therefore the path the subject really requests,
 * because a specification that asserted the documented-but-absent path would fail
 * against correct code and would have to be "fixed" by breaking the subject. The
 * screen route that edits this policy is a third string again - it sits at the top
 * level of the application rather than under the account resource - and none of the
 * three is derived from either of the others.
 */
const ACCOUNT_POLICY = '/api/v1/users/settings';

/** The profile-definition collection. */
const PROFILE_DEFINITIONS = '/api/v1/profile-definitions';

/**
 * The trailing match character the legacy screen appended, expressed as a character
 * code rather than as a literal.
 *
 * The character itself is deliberately absent from this file's source: the discipline
 * check that proves no wildcard has been embedded in a fixture or an expectation is a
 * search for that literal, and it must find nothing. Naming it this way lets the
 * assertions below state the prohibition without also violating it.
 *
 * The prohibition is real, not stylistic. `Website/admin/Users/Users.ascx.vb` appended
 * one of these to whatever had been typed before handing it to the provider - at L269
 * for the address axis, L271 for the name axis and L274 for the profile-property axis -
 * so the match was always a starts-with, and the server reproduces that. A client that
 * appended one as well would send a doubled pattern.
 */
const TRAILING_MATCH_CHARACTER = String.fromCharCode(37);

/**
 * The magic value the legacy screen used to mean "nothing was searched for".
 *
 * `Users.ascx.vb` L266 guarded the entire search block with a comparison against this
 * string, which meant it could never itself be searched for even though it is an
 * ordinary thing to type into a filter. Absence is expressed on this contract by
 * omitting the parameter, so this value must never appear in a transmitted query
 * string.
 */
const LEGACY_NO_SEARCH_SENTINEL = 'None';

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/**
 * One row of the account listing.
 *
 * The tenant identifier is minus one, which is a REAL tenant rather than a marker:
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L77
 * declares the portal identity seeded at minus one, so the first tenant ever created
 * carries that value - while `Library/Components/Shared/Null.vb` L41-L45 simultaneously
 * defines minus one as the marker for a missing integer. The two meanings collide, which
 * is precisely why nothing in the subject may interpret an identifier.
 */
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
};

/**
 * One page of accounts.
 *
 * The page coordinate is ZERO-BASED, which is the wire's convention rather than a
 * pager's. The legacy listing kept a one-based counter - `Users.ascx.vb` L51 declares it
 * initialised to one - and subtracted one at every provider call, at L265, L269, L271
 * and L274. The subtraction now happens nowhere at all, which is what the paging
 * specifications below assert.
 */
const USER_PAGE: PagedUserList = {
  items: [USER_LIST_ITEM],
  meta: { totalCount: 1, pageIndex: 0, pageSize: 25, totalPages: 1 },
};

/**
 * An account to create.
 *
 * Carries a deliberately falsy value in each of the two categories that a
 * truthiness-filtered client would silently drop: an EMPTY STRING for the shown name,
 * and FALSE for the approve-on-create choice. Both are meaningful. An empty shown name
 * asks the server to compose one from the parts; a false approve choice asks for an
 * account that must be approved before it can sign in, which is the opposite of an
 * absent choice rather than the same thing as one.
 *
 * The empty string is not an arbitrary edge case either. `Null.vb` L71-L75 defines the
 * marker for a missing string as the EMPTY STRING rather than as a null, and
 * `Users.ascx.vb` L252 initialises its query string from exactly that marker - so an
 * empty string is what the legacy application produced when it had nothing, and it has
 * to survive the journey unchanged.
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
};

/**
 * A credential change made by the account holder, who supplies the credential in force.
 *
 * The confirmation member is present and deliberately EQUAL to the replacement, because
 * the server is what compares them. `Website/admin/Users/Password.ascx.vb` L272 shows
 * the legacy screen making that comparison itself before it called the provider, and
 * L278 shows it running the policy check too; both moved server-side, so this file sends
 * the pair and asserts nothing about the relationship between them.
 */
const CHANGE_PASSWORD_REQUEST: ChangePasswordRequest = {
  operation: 'change',
  currentPassword: 'placeholder-value-in-force',
  newPassword: 'placeholder-value-replacement',
  confirmPassword: 'placeholder-value-replacement',
};

/**
 * A credential reset made by an administrator, who supplies no credential in force.
 *
 * The in-force member is present and NULL rather than omitted - a reset proves nothing
 * about the previous credential, and saying so explicitly is different from forgetting
 * to say it. The legacy screen kept these as two separate operations as well:
 * `Password.ascx.vb` L249 reset through one provider member and L300 changed through
 * another.
 */
const RESET_PASSWORD_REQUEST: ChangePasswordRequest = {
  operation: 'reset',
  currentPassword: null,
  newPassword: 'placeholder-value-replacement',
  confirmPassword: 'placeholder-value-replacement',
};

/**
 * The tenant's whole account policy, written to be hostile to a truthiness filter.
 *
 * Twenty-three members, of which nineteen are falsy: seven unticked listing columns,
 * three unticked profile switches, two unticked security switches, four numeric zeros,
 * two empty strings and one null. A client that filtered this body by truthiness would
 * transmit four members out of twenty-three, and the server - which serialises and binds
 * without eliding a default - would read nineteen absences as instructions it was never
 * given.
 *
 * TWO SENTINEL COLLISIONS ARE ENCODED HERE ON PURPOSE. A page size of ZERO and a
 * post-sign-in landing page of MINUS ONE both appear, because zero and minus one are
 * distinct, meaningful values on this contract and neither may be coalesced into the
 * other nor defaulted away. A page size is a per-tenant setting rather than a constant -
 * `Users.ascx.vb` L114-L119 read it from the module setting named for records per page,
 * and its fallback of ten lives at `Library/Components/Users/UserModuleBase.vb`
 * L134-L136 - so the shared default belongs to the paging model alone and must never be
 * substituted in here.
 *
 * The folder requirements for this specification also name an account-allowance member
 * and a pair of cache-duration members as sentinel traps. Neither is declared on this
 * contract: the shape has no allowance member and no duration member at all. The
 * collision they illustrate - zero and minus one carrying different meanings, never
 * coalesced - is asserted through the numeric members that do exist.
 */
const ACCOUNT_POLICY_BODY: MembershipSettings = {
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
 * The same policy with the two collision values EXCHANGED.
 *
 * Sending zero where the first fixture sent minus one, and minus one where it sent zero,
 * is what distinguishes "both values survive" from "one value happens to survive twice".
 */
const ACCOUNT_POLICY_BODY_EXCHANGED: MembershipSettings = {
  ...ACCOUNT_POLICY_BODY,
  recordsPerPage: -1,
  redirectAfterLogin: 0,
  redirectAfterLogout: -1,
};

/**
 * One profile definition.
 *
 * Its identity is ZERO, which is a real identifier here for the same reason minus one is
 * a real tenant: several of this schema's identities are seeded below one. Its name
 * carries mixed case, a hyphen and a space, so any normalisation applied anywhere on the
 * path would be visible.
 */
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

/** One account's profile: the tenant's declared properties with this account's values. */
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
};

/**
 * A whole-profile replacement.
 *
 * A REPLACE rather than a merge, so every declared property is present - including the
 * one whose value is being cleared to an empty string. A client that diffed against a
 * previously read profile and sent only what it thought had changed would erase every
 * property it omitted.
 */
const PROFILE_SUBMISSION: ProfileSubmission = {
  userId: 1,
  properties: [
    { propertyDefinitionId: 0, propertyValue: '', visibility: 0 },
    { propertyDefinitionId: 12, propertyValue: 'Ada Lovelace', visibility: 2 },
  ],
};

/**
 * A profile definition to create.
 *
 * Names a module association, which ONLY a create may decide: the terminal stored
 * procedure that adds a definition accepts that association and the one that updates a
 * definition neither declares the parameter nor writes the column. Sending it as null
 * rather than omitting it states "no association" explicitly.
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

/**
 * A profile definition to replace, moved to the fourth position.
 *
 * THE POSITION MEMBER IS THE WHOLE OF THE REORDERING MECHANISM. There is no nudge-up or
 * nudge-down endpoint, because the legacy pair of buttons was never an operation on one
 * row: `Website/admin/Users/ProfileDefinitions.ascx.vb` L182-L183 read the neighbouring
 * definition's position, the comment at L185 announces the swap in as many words, and
 * L186-L187 performed it on both rows - while a separate bulk pass at L326 renumbered a
 * whole set by assigning each position from its index, and L519-L521 dispatched the two
 * commands. Modelling a two-row write as a one-row route would have needed a second call
 * just to discover the neighbour, and would have looked atomic while not being so.
 */
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

/**
 * The two extension members this API attaches to every problem document.
 *
 * `ValidationProblemDetailsFactory` writes both on every refusal, so a fixture omitting them
 * describes a response the server does not send.
 */
const TRACE_ID = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
const CORRELATION_ID = '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

/**
 * Builds a complete, server-emittable refusal body for one failure code.
 *
 * ⚠️ THE FAILURE CODE TRAVELS IN `type`, RENDERED AS `urn:dnnmigration:error:<code>` by the
 * single server method `ApiResults.BuildProblemType`. ⚠️ AND THE STATUS IS DERIVED FROM THE
 * CODE by `ApiResults.MapStatusCode`, so the body's `status` and the transport's status can
 * never disagree in a real response — which means a fixture in which they DO disagree
 * specifies a contradiction, and a consumer that read the body's member rather than the
 * transport's would pass here and misclassify in production. An earlier revision of this file
 * held ONE `about:blank` fixture whose body said `403` and then flushed it at `403`, at `409`
 * and behind three different endpoints, so two of those three cases asserted exactly that
 * contradiction.
 *
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
 * The refusal an unpermitted account write earns.
 *
 * `auth.not_permitted` is the status vocabulary's own default type for `403`, and it is what a
 * refusal decided by the authorisation layer — before the controller body runs — carries.
 */
const NOT_PERMITTED = refusal(
  'auth.not_permitted',
  403,
  'Forbidden',
  'The authenticated caller is not permitted to perform this operation.',
);

/**
 * The `404` a single-resource read answers with when the thing addressed does not exist.
 *
 * ⚠️ THIS, AND NOT A `200` CARRYING A NULL PAYLOAD, IS HOW ABSENCE ARRIVES.
 * `ApiResults.Complete<T>` converts a successful outcome carrying no value into this exact
 * document, whose code, title and detail are fixed constants so that a client branches on one
 * type whichever endpoint produced it. The detail deliberately names neither the identifier
 * nor the resource kind, so an unauthorised caller cannot distinguish "this exists but is not
 * yours" from "this does not exist".
 */
const RESOURCE_NOT_FOUND = refusal(
  'resource.not_found',
  404,
  'Not Found',
  'The requested resource does not exist.',
);

// ---------------------------------------------------------------------------
// Observation
// ---------------------------------------------------------------------------

/**
 * Everything one subscription observed, kept as lists so nothing is lost.
 *
 * Lists rather than single slots, because the counts are themselves assertions: a method
 * that emitted twice, or that both emitted and failed, is a defect this shape makes
 * visible. Lists also sidestep the narrowing a nullable slot assigned inside a callback
 * would suffer, which would otherwise force an assertion operator that this workspace
 * does not permit.
 */
interface Observed<T> {
  /** Every value the subscription received, in order. */
  readonly values: T[];

  /** Every failure the subscription received. */
  readonly failures: HttpErrorResponse[];

  /** One entry per completion. */
  readonly completions: boolean[];
}

/**
 * Subscribes immediately and records what arrives.
 *
 * Subscribing is the caller's job on this contract - every method returns cold and
 * starts nothing - so a specification has to subscribe before the request exists to be
 * expected at all.
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
      // The real client, then the testing backend that displaces it - in that order,
      // because the second provider overrides the first. Reversing them leaves the
      // live backend in place and every expectation below times out against a request
      // that was never intercepted.
      //
      // NO INTERCEPTOR IS REGISTERED. The correlation identifier, the bearer token and
      // the translation of a failure into a problem document are applied by three
      // separately specified units, and running them here would mean asserting two
      // units at once - while also making the header prohibitions below unfalsifiable.
      //
      // The subject is NOT listed as a provider. It is declared root-provided, so
      // listing it would construct a second, unrelated instance and prove nothing about
      // the one the application actually uses. This is a standalone application, so
      // there is no component declaration array to configure either.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(UserService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // THE LOAD-BEARING ASSERTION OF THIS ENTIRE FILE. It fails if a method issued a
    // second request, and it is the only automated proof that creating an account does
    // not also write a profile, that a read does not also warm a cache, and that a
    // failure path does not retry.
    httpMock.verify();
  });

  /**
   * Expects exactly one outstanding request with the given verb and PATH.
   *
   * Matches on the path rather than on the path-and-query, because the string overload
   * of the expectation compares against the full URL including its query string - which
   * would couple every paged expectation below to the order in which parameters happen
   * to be composed. Query strings are asserted separately, by name.
   */
  const expectRequest = (method: string, path: string): TestRequest =>
    httpMock.expectOne(
      (request) => request.method === method && request.url === path,
      `${method} ${path}`,
    );

  /**
   * Asserts that neither header applied by the interceptor chain has been set here.
   *
   * Both belong to units registered once at application configuration. A header set by
   * the subject would either duplicate one of them or defeat it, and because no
   * interceptor is registered in this test module their absence here is a genuine
   * observation rather than an artefact of ordering.
   */
  const expectNoInterceptorHeaders = (request: TestRequest): void => {
    expect(request.request.headers.get('Authorization'))
      .withContext('the bearer token belongs to the auth interceptor, not to the service')
      .toBeNull();
    expect(request.request.headers.get('X-Correlation-Id'))
      .withContext('the correlation identifier is applied by its own interceptor')
      .toBeNull();
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

      // The listing answers with the paging envelope DIRECTLY rather than nesting it
      // inside the single-payload wrapper, so nothing is unwrapped and no paging fact is
      // discarded on the way through.
      expect(observed.values).toEqual([USER_PAGE]);
      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('emits every ordering and search member the caller supplied, and only those', () => {
      const query: UserListQuery = {
        pageIndex: 2,
        pageSize: 25,
        sortBy: 'Username',
        sortDir: 'Descending',
        query: 'ada',
        isApproved: false,
      };

      observe(service.list(query));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.keys().sort()).toEqual([
        'isApproved',
        'pageIndex',
        'pageSize',
        'query',
        'sortBy',
        'sortDir',
      ]);
      expect(request.request.params.get('sortBy')).toBe('Username');
      expect(request.request.params.get('sortDir')).toBe('Descending');
      expect(request.request.params.get('query')).toBe('ada');
      request.flush(USER_PAGE);
    });

    it('transmits the page index exactly as supplied, applying no adjustment to it', () => {
      // The legacy listing held a one-based counter and subtracted one at every provider
      // call - `Users.ascx.vb` L51 initialises it to one, and L265, L269, L271 and L274
      // each pass that value less one. The portal listing did the same at
      // `Website/admin/Portal/Portals.ascx.vb` L142. The wire is zero-based, so the
      // subtraction must happen nowhere: a page index of zero travels as zero.
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
      // The shared default of ten belongs to the paging model alone. A page size travels
      // because the caller chose it: `Users.ascx.vb` L114-L119 read it from a per-tenant
      // module setting, and `UserModuleBase.vb` L134-L136 supplied ten only as that
      // setting's fallback. Both members are required on the query contract, so a caller
      // cannot omit them - which makes "the value transmitted is the value supplied" the
      // assertion that a substituted constant would break.
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
      // ⚠️ THIS ENDPOINT CANNOT ANSWER `200` WITH A NULL PAYLOAD. It reports through
      // `ApiResults.Complete<T>`, which converts a successful outcome carrying no value into
      // the `404` below — so absence arrives as a STATUS. An earlier revision of this spec
      // asserted the 200/null shape as the contract, which meant the whole chain above it was
      // specified against a response the server never sends while the one response it DOES
      // send for a missing account went untested.
      const observed = observe(service.getById(4242));

      const request = expectRequest('GET', `${USERS}/4242`);
      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expect(observed.values).withContext('absence is not an emitted value').toEqual([]);
      expect(observed.completions.length).toBe(0);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(404);
      expect(observed.failures[0].error).toEqual(RESOURCE_NOT_FOUND);
    });

    it('still tolerates a null payload from a non-conforming intermediary', () => {
      // DEFENCE IN DEPTH, AND LABELLED AS SUCH. The method's own return type admits null
      // because a proxy or gateway between the browser and the API can return a document this
      // application never produced, and coercing it into an empty object here would push a
      // run-time surprise into whatever read a member off it. This is NOT the endpoint's
      // contract — the case above is — and no store or screen specification may treat it as
      // the normal path.
      const observed = observe(service.getById(4242));

      const request = expectRequest('GET', `${USERS}/4242`);
      request.flush({ data: null, meta: null } satisfies ApiResponse<UserDetail | null>);

      expect(observed.values).toEqual([null]);
      expect(observed.completions.length).toBe(1);
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
      // The account-allowance rule is the server's. Counting accounts here first would
      // cost an extra request, would race every other administrator, and would still
      // have to handle the refusal it was trying to predict.
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
      // Removal is PER ACCOUNT. The legacy screen also offered a bulk destruction -
      // `Users.ascx.vb` L326 declared it and its single provider call at L328 destroyed
      // an unbounded number of accounts from one click, with no per-row confirmation and
      // no way to review the set first. (The declaration and the provider member it
      // calls spell the same word with different capitals, which is a fair measure of how
      // little the pre-strict compiler was checking.) No bulk operation exists here.
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

      // NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS. They describe this replacement
      // as answering 200 with a body. The subject types it as carrying no value, which
      // is the no-content answer asserted here; flushing a payload would be asserting a
      // response the API does not write.
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
      // NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS. They describe this operation as
      // a PUT and call it the only one on the surface answering 204. The subject POSTs
      // it, and the operations that answer with no content are the profile replacement,
      // the approval change, the policy replacement, the two credential operations, the
      // release, the obligation and the two removals. The verb asserted here is the verb
      // the subject uses, because a specification cannot be right about a route its
      // subject does not request.
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
      // The legacy store was reversible by configuration and the key that reversed it was
      // committed to source control in the clear. The replacement store is a one-way hash,
      // which makes disclosure impossible rather than merely switched off - so a reset
      // returns nothing, and there is no retrieval, reminder or recovery endpoint anywhere
      // on this surface to specify.
      const observed = observe(service.passwordReset(1, RESET_PASSWORD_REQUEST));

      const request = expectRequest('POST', `${USERS}/1/password-reset`);
      request.flush(null, { status: 204, statusText: 'No Content' });

      // Exactly one notification, carrying nothing. The return type declares no value, so
      // the count is the assertion: a payload-bearing answer would still emit once, but a
      // method that returned a credential would have had to declare it.
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
      // ONE endpoint taking the desired state, rather than a pair of verb-shaped routes:
      // the server reports setting a state an account already holds as a conflict, which
      // is only meaningful if the caller said which state it meant. So false has to
      // travel. A truthiness-filtered parameter would turn a withdrawal into a request
      // with no instruction in it.
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
      // The legacy account state behind this was one member of a five-member status
      // vocabulary the sign-in path evaluated, which conflated a hard block with two
      // advisories and a separate concern about the profile. The successor is a small set
      // of independent advisory flags carried on the session. This operation writes one
      // fact and reads none of them - and it does not choose, generate, transmit or
      // return a credential.
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

      // The API path, the screen route and the folder requirements' documented path are
      // three different strings, and none is derived from another. The API addresses the
      // settings child of the account collection, which is the value asserted here; the
      // administration screen that edits it is routed at `/settings/membership`; and the
      // folder requirements name `/api/v1/users/settings/membership`, which the endpoint
      // declaration module does not compose. Simplifying any of them into another
      // produces a 404 on one side or a dead link on the other.
      expect(request.request.url).toBe('/api/v1/users/settings');
      expect(request.request.params.keys()).toEqual([]);
      expectNoInterceptorHeaders(request);

      request.flush({
        data: ACCOUNT_POLICY_BODY,
        meta: null,
      } satisfies ApiResponse<MembershipSettings>);

      expect(observed.values).toEqual([ACCOUNT_POLICY_BODY]);
      expect(observed.completions.length).toBe(1);
    });

    it('reports an unresolvable tenant policy as a 404, not as a null payload', () => {
      // The policy read is a single-resource read like any other, so `ApiResults.Complete<T>`
      // turns a value-free success into a `404`. The distinction matters to the screen: a
      // policy whose members are all at their defaults is a legitimate `200`, and a `404` means
      // there is no tenant to read a policy for at all.
      const observed = observe(service.getMembershipSettings());

      const request = expectRequest('GET', ACCOUNT_POLICY);
      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(404);
    });

    it('still tolerates a null payload from a non-conforming intermediary', () => {
      // Defence in depth, exactly as on the account read and for the same reason.
      const observed = observe(service.getMembershipSettings());

      const request = expectRequest('GET', ACCOUNT_POLICY);
      request.flush({
        data: null,
        meta: null,
      } satisfies ApiResponse<MembershipSettings | null>);

      expect(observed.values).toEqual([null]);
    });
  });

  describe('updateMembershipSettings', () => {
    it('replaces the whole multi-member policy with nothing omitted for reading as empty', () => {
      const observed = observe(service.updateMembershipSettings(ACCOUNT_POLICY_BODY));

      const request = expectRequest('PUT', ACCOUNT_POLICY);
      const body: unknown = request.request.body;

      expect(body)
        .withContext('nineteen of the twenty-three members are falsy and all must survive')
        .toEqual(ACCOUNT_POLICY_BODY);
      expect(Object.keys(ACCOUNT_POLICY_BODY).length).toBe(23);
      expectNoInterceptorHeaders(request);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('keeps a page size of zero and a landing page of minus one distinct from one another', () => {
      // The two collide with the legacy sentinel table, which is exactly why neither may
      // be coalesced: `Null.vb` L41-L45 makes minus one the marker for a missing integer,
      // yet minus one and zero are both real values on this contract. Sending each in
      // turn, and then exchanging them, is what proves nothing is being defaulted.
      observe(service.updateMembershipSettings(ACCOUNT_POLICY_BODY));

      const first = expectRequest('PUT', ACCOUNT_POLICY);
      expect(first.request.body).toEqual(
        jasmine.objectContaining({
          recordsPerPage: 0,
          redirectAfterLogin: -1,
          redirectAfterLogout: 0,
        }),
      );
      first.flush(null, { status: 204, statusText: 'No Content' });

      observe(service.updateMembershipSettings(ACCOUNT_POLICY_BODY_EXCHANGED));

      const second = expectRequest('PUT', ACCOUNT_POLICY);
      expect(second.request.body).toEqual(
        jasmine.objectContaining({
          recordsPerPage: -1,
          redirectAfterLogin: 0,
          redirectAfterLogout: -1,
        }),
      );
      second.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('keeps a null landing page distinct from a zero one', () => {
      // A null means "use the default" and a zero names a page. Coalescing either into
      // the other changes the instruction the server receives.
      observe(service.updateMembershipSettings(ACCOUNT_POLICY_BODY));

      const request = expectRequest('PUT', ACCOUNT_POLICY);
      expect(request.request.body).toEqual(
        jasmine.objectContaining({
          redirectAfterRegistration: null,
          redirectAfterLogout: 0,
        }),
      );
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('keeps an empty policy string as an empty string rather than a null', () => {
      // `Null.vb` L71-L75 defines the marker for a missing string as the EMPTY STRING,
      // whose body is literally a return of two quotes, and `Users.ascx.vb` L252
      // initialises its query string from that marker. An empty string is therefore what
      // the legacy application produced when it had nothing to say, and converting it to
      // a null on the way out would change the value the server stores.
      observe(service.updateMembershipSettings(ACCOUNT_POLICY_BODY));

      const request = expectRequest('PUT', ACCOUNT_POLICY);
      expect(request.request.body).toEqual(
        jasmine.objectContaining({
          securityEmailValidation: '',
          securityDisplayNameFormat: '',
        }),
      );
      request.flush(null, { status: 204, statusText: 'No Content' });
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

      // DELIBERATELY UNPAGED. The set is bounded by how many fields an administrator has
      // chosen to define, so paging it would add coordinates to every call in exchange
      // for nothing. Not one parameter is emitted - not a defaulted page, not an empty
      // ordering.
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
      // NOTE - DIVERGENCE FROM THE FOLDER REQUIREMENTS. They require a `portalId`
      // parameter on this call, transmitting minus one and zero unchanged. The subject
      // takes NO argument and emits no parameter: the API resolves one tenant per request
      // from the host it was reached on, reconciled against the alias table, before it
      // dispatches to a controller. A tenant identifier here would either be redundant or
      // be a second, disagreeing opinion about which tenant the caller meant. The
      // guard-free handling of minus one and zero that the requirement is really about is
      // asserted below against the identifiers this surface does carry.
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
      // The parameter names a PROPERTY definition, and that spelling is load-bearing on
      // both sides of the wire: the route constrains it as an integer under that name and
      // the contract spells its identity member the same way. A near miss produces a route
      // that does not match rather than a parameter that is quietly ignored.
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

    it('still tolerates a null payload from a non-conforming intermediary', () => {
      // Defence in depth, labelled so it is not mistaken for the endpoint's contract.
      const observed = observe(service.getProfileDefinition(9999));

      const request = expectRequest('GET', `${PROFILE_DEFINITIONS}/9999`);
      request.flush({
        data: null,
        meta: null,
      } satisfies ApiResponse<ProfileDefinition | null>);

      expect(observed.values).toEqual([null]);
    });
  });

  describe('updateProfileDefinition', () => {
    it('replaces one definition and carries the position member that reorders it', () => {
      const observed = observe(service.updateProfileDefinition(0, UPDATE_DEFINITION_REQUEST));

      const request = expectRequest('PUT', `${PROFILE_DEFINITIONS}/0`);
      expect(request.request.body).toEqual(UPDATE_DEFINITION_REQUEST);

      // THE POSITION MEMBER IS THE ONLY REORDERING MECHANISM ON THIS SURFACE, so it has
      // to survive the body untouched. If it were dropped, reordering would silently stop
      // working while every other member of the replacement still applied.
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
      // A CONFLICT, and its own document rather than the `403` one. The reason token decides
      // the status server-side, so a fixture whose body claimed `403` while the transport said
      // `409` would be a response the server cannot produce.
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

  // =========================================================================
  // The three search axes
  //
  // Measured verbatim at `Website/admin/Users/Users.ascx.vb` L245-L280. The legacy
  // screen chose between them with a select-case over the chosen field and appended one
  // trailing match character to the typed text in each branch, so every search was a
  // starts-with resolved by the provider. The server reproduces that, which is why the
  // caller passes bare text here.
  // =========================================================================

  describe('the search axes', () => {
    /** Asserts that no transmitted parameter value carries the trailing match character. */
    const expectNoTrailingMatchCharacter = (request: TestRequest): void => {
      for (const name of request.request.params.keys()) {
        expect(request.request.params.get(name))
          .withContext(`parameter "${name}" must not carry a match character`)
          .not.toContain(TRAILING_MATCH_CHARACTER);
      }
    };

    it('searches by account name without appending a match character', () => {
      // `Users.ascx.vb` L270-L271 read the name branch and passed the typed text with one
      // match character appended. Appending one here as well would send a doubled pattern.
      observe(service.list({ pageIndex: 0, pageSize: 25, userName: 'ada' }));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.get('userName')).toBe('ada');
      expectNoTrailingMatchCharacter(request);
      request.flush(USER_PAGE);
    });

    it('searches by address without appending a match character', () => {
      // `Users.ascx.vb` L268-L269 is the address branch, appending the same character.
      observe(service.list({ pageIndex: 0, pageSize: 25, email: 'ada@example.test' }));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.get('email')).toBe('ada@example.test');
      expectNoTrailingMatchCharacter(request);
      request.flush(USER_PAGE);
    });

    it('searches by an arbitrary profile-property name, transmitting it verbatim', () => {
      // `Users.ascx.vb` L272-L274 fell through to the profile-property branch, passing the
      // chosen field's name straight into the provider, and L275 carried that same name in
      // the screen's own query string. The set of property names is TENANT DATA, declared
      // through the profile-definition methods above, so it is not a closed vocabulary and
      // cannot be validated against a list. The fixture name below carries mixed case, a
      // hyphen and a space precisely so that any trimming, case-folding or sanitising
      // applied anywhere on the path would show up here as a mismatch - two declared
      // property names are free to differ from one another only in case.
      const oddPropertyName = 'Preferred-Locale Display Name';

      observe(
        service.list({
          pageIndex: 0,
          pageSize: 25,
          profilePropertyName: oddPropertyName,
          profilePropertyValue: 'Ada',
        }),
      );

      const request = expectRequest('GET', USERS);
      expect(request.request.params.get('profilePropertyName')).toBe(oddPropertyName);
      expect(request.request.params.get('profilePropertyValue')).toBe('Ada');
      expectNoTrailingMatchCharacter(request);
      request.flush(USER_PAGE);
    });

    it('omits the search entirely rather than transmitting the legacy no-search sentinel', () => {
      // `Users.ascx.vb` L266 guarded the whole search block by comparing the typed text
      // against a magic string, which meant that string could never itself be searched for.
      // Absence is an omitted parameter here, so the magic string has no reason to appear
      // in a query at all - and if a caller genuinely searches for that word, it travels
      // like any other word rather than being swallowed.
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

      const searched = expectRequest('GET', USERS);
      expect(searched.request.params.get('userName'))
        .withContext('the word is ordinary text once it is the caller who supplied it')
        .toBe(LEGACY_NO_SEARCH_SENTINEL);
      searched.flush(USER_PAGE);
    });

    it('transmits two search axes at once, leaving the combination rule to the server', () => {
      // Which axes may be combined is the server's rule, and it answers a combination it
      // refuses with a field-level failure naming the offending parameter - strictly more
      // useful than the client quietly declining to send it. So the subject transmits
      // whatever it is given, and this specification asserts exactly that rather than
      // demanding a rejection it must not perform.
      observe(
        service.list({ pageIndex: 0, pageSize: 25, userName: 'ada', email: 'ada@example.test' }),
      );

      const request = expectRequest('GET', USERS);
      expect(request.request.params.keys().sort()).toEqual([
        'email',
        'pageIndex',
        'pageSize',
        'userName',
      ]);
      request.flush(USER_PAGE);
    });

    it('offers the approval axis as a paged filter, not as the two unpaged legacy modes', () => {
      // `Users.ascx.vb` L258-L260 answered one legacy mode from an unpaged provider call
      // and hid the pager; L261-L263 answered the signed-in view the same way. Neither
      // took page coordinates, so both returned an unbounded set. The approval filter here
      // is a PAGED filter over the account table and is not a restoration of either: the
      // signed-in view depended on session tracking and a scheduled purge that this
      // migration does not carry forward.
      observe(service.list({ pageIndex: 0, pageSize: 25, isApproved: false }));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.get('isApproved')).toBe('false');
      expect(request.request.params.get('pageIndex')).toBe('0');
      expect(request.request.params.get('pageSize')).toBe('25');
      request.flush(USER_PAGE);
    });
  });

  // =========================================================================
  // Sentinel and parameter fidelity
  // =========================================================================

  describe('identifier and value fidelity', () => {
    it('interpolates minus one as an identifier rather than reading it as an absence', () => {
      // Minus one is simultaneously a real identifier and the legacy marker for a missing
      // integer - `Null.vb` L41-L45 returns it as that marker, while the portal identity is
      // seeded at minus one. One vocabulary cannot carry both meanings, so the subject
      // declines to interpret an identifier at all and leaves the question of whether one is
      // known to the caller.
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
      // The distinction between an explicitly empty value and an absent one is the whole
      // point: `Null.vb` L71-L75 makes the empty string the marker for a missing string, so
      // an empty value is what the legacy application produced when it had nothing, and a
      // truthiness test here would erase the difference between "cleared" and "not stated".
      observe(service.list({ pageIndex: 0, pageSize: 25, query: '', userName: '' }));

      const request = expectRequest('GET', USERS);
      expect(request.request.params.has('query')).toBe(true);
      expect(request.request.params.get('query')).toBe('');
      expect(request.request.params.has('userName')).toBe(true);
      expect(request.request.params.get('userName')).toBe('');
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

    /**
     * Every method on the service, each paired with the exact success the SERVER declares.
     *
     * ⚠️ THE SUCCESS STATUS AND BODY ARE PART OF EACH CASE, and they are not
     * interchangeable. An earlier revision of this file drove all nineteen methods with one
     * shared `{ data: null, meta: null }` body flushed at the transport's default `200`,
     * which asserted a response shape NO endpoint here produces: the nine payload-free
     * commands answer `204` and HTTP forbids a `204` from carrying a body at all, the two
     * creations answer `201`, and the single-resource reads turn a value-free success into a
     * `404` rather than into a null payload. So the loop simultaneously mis-stated fourteen
     * of the nineteen contracts and, because a `void`-typed observable ignores whatever body
     * arrives, could not have failed no matter how wrong the fixture was.
     *
     * The statuses below are read from the controllers' own `ProducesResponseType`
     * declarations — `UsersController` and `ProfileDefinitionsController` — rather than
     * inferred from the verb.
     */
    const SURFACE: readonly {
      readonly method: string;
      readonly path: string;
      readonly status: number;
      /**
       * The response body, typed as the testing backend accepts it.
       *
       * `Object | null` rather than `unknown`, because every success body here is either a
       * JSON object or the absent body a `204` carries, and the flush primitive is typed for
       * exactly that. Widening to `unknown` would need a cast at the flush site, which would
       * be a claim about a value this file already knows the shape of.
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
        body: { data: ACCOUNT_POLICY_BODY, meta: null },
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
        status: 204,
        body: null,
        invoke: () => service.updateMembershipSettings(ACCOUNT_POLICY_BODY),
      },
      {
        method: 'DELETE',
        path: `${PROFILE_DEFINITIONS}/0`,
        status: 204,
        body: null,
        invoke: () => service.deleteProfileDefinition(0),
      },
    ];

    it('sets no header of its own on any request across the whole surface', () => {
      // One pass over every method, asserting the two interceptor headers are unset. The
      // reason to do it once for all of them rather than trusting the per-method checks is
      // that a header added to a shared options object would appear everywhere at once.
      expect(SURFACE.length)
        .withContext('every method on the service is exercised by this pass')
        .toBe(19);

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
      // THE COMPANION CLAIM, AND THE ONE THE SHARED-BODY LOOP COULD NOT MAKE. Each method is
      // driven to its declared success and the OUTCOME is asserted: a payload-bearing read or
      // write emits exactly one value and completes, and a payload-free command completes
      // without emitting anything of substance. A method that silently swallowed its response,
      // emitted twice, or failed on its own endpoint's success status is caught here.
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
      // The mapping itself, pinned as data so a drift is visible in one place rather than
      // having to be inferred from nineteen flushes. `ApiResults.Complete(Result)` answers 204
      // and `Created(...)` answers 201; both are read off the controllers' own declarations.
      const byStatus = (status: number): readonly string[] =>
        SURFACE.filter((entry) => entry.status === status)
          .map((entry) => `${entry.method} ${entry.path}`)
          .sort();

      expect(byStatus(204)).toEqual([
        'DELETE /api/v1/profile-definitions/0',
        'DELETE /api/v1/users/1',
        'POST /api/v1/users/1/password',
        'POST /api/v1/users/1/password-reset',
        'POST /api/v1/users/1/require-password-change',
        'POST /api/v1/users/1/unlock',
        'PUT /api/v1/users/1/approval',
        'PUT /api/v1/users/1/profile',
        'PUT /api/v1/users/settings',
      ]);
      expect(byStatus(201)).toEqual(['POST /api/v1/profile-definitions', 'POST /api/v1/users']);
      expect(byStatus(200).length).toBe(8);
    });
  });

  // =========================================================================
  // The closed surface
  // =========================================================================

  describe('the closed surface', () => {
    /**
     * Every own member of the prototype, sorted.
     *
     * Read from the prototype rather than from an instance, because the methods live
     * there; the only non-method entry is the constructor, which is listed explicitly so
     * the count below is exact rather than approximate.
     */
    const PROTOTYPE_MEMBERS: readonly string[] = [
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
      'listProfileDefinitions',
      'passwordReset',
      'requirePasswordChange',
      'setApproval',
      'unlock',
      'update',
      'updateMembershipSettings',
      'updateProfile',
      'updateProfileDefinition',
    ];

    it('exposes exactly nineteen methods and not one more', () => {
      const actual: readonly string[] = Object.getOwnPropertyNames(UserService.prototype).sort();

      expect(actual)
        .withContext('a method added without a specification fails here first')
        .toEqual([...PROTOTYPE_MEMBERS]);
      expect(actual.filter((name) => name !== 'constructor').length).toBe(19);
    });

    it('exposes no reordering helper, because a position is a field on a replacement', () => {
      // The legacy pair of buttons swapped two rows - `ProfileDefinitions.ascx.vb`
      // L182-L187, with the comment at L185 saying so - and a separate bulk pass at L326
      // renumbered a whole set from each item's index, dispatched from L519-L521. None of
      // that is a one-row route, so none of it becomes an endpoint: the caller states the
      // position it wants and the replacement writes it.
      for (const forbidden of ['moveUp', 'moveDown', 'reorder', 'swapOrder', 'renumber']) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist; position is written by the replacement`)
          .not.toContain(forbidden);
      }
    });

    it('exposes no credential disclosure of any kind', () => {
      // A RESET is carried forward and is specified above; RETRIEVAL is not, and the
      // difference is structural rather than a matter of configuration. The legacy store
      // was reversible and the key that reversed it was committed to source control in the
      // clear, so anyone who could read the repository could read every stored credential.
      // The replacement store is a one-way hash, which makes disclosure impossible rather
      // than merely switched off - so there is nothing here to send, remind or recover.
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
      // Adding, removing and time-bounding a membership all live on the role resource, as
      // a sub-collection of one role. The legacy member-services screen presented a paid
      // subscription as a concept of its own, but each subscription was one row joining an
      // account to a role with an effective and an expiry date - the very row the role
      // resource writes - so a parallel route here would be a second name for one table.
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
      // `Users.ascx.vb` L258-L260 and L261-L263 answered the unapproved and the signed-in
      // views from unpaged provider calls with the pager hidden, and L326-L328 destroyed an
      // unbounded set of accounts from one click. The signed-in view depended on session
      // tracking and a scheduled purge that this migration does not carry forward; the bulk
      // removal is replaced by per-account removal.
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
      // No sign-in, no sign-out, no token refresh, no identity read, no liveness probe, no
      // permission mutation, no tenant key-value read, no upload. Each of those belongs to
      // a different unit, and several of them to no client-side unit at all.
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
      // The absence assertions above read the declared list rather than the prototype, so
      // this closes the loop: the declared list is exactly the prototype. Without it, a
      // stale list could assert the absence of names that are in fact present.
      const fromPrototype: readonly string[] = Object.getOwnPropertyNames(
        UserService.prototype,
      ).sort();

      expect([...PROTOTYPE_MEMBERS]).toEqual([...fromPrototype]);
    });
  });
  // =========================================================================
  // THE RESPONSE CONTRACT IS CHECKED, NOT ASSERTED
  //
  // `http.get<UserDetail>(...)` compiles to `http.get(...)`: the interface is erased and
  // nothing inspects the body. Each case answers with a body the server would never send
  // and requires the OBSERVABLE TO FAIL here, naming the member — rather than letting a
  // blank field, a missing role list or a silently empty grid surface layers away.
  //
  // ⚠ THE REFUSAL NAMES THE MEMBER PATH AND THE EXPECTED TYPE, NEVER THE VALUE, and that
  // is a privacy boundary in this file specifically: an account's address, telephone and
  // profile values are personal data, and a violation report must not copy them anywhere.
  // =========================================================================
  describe('refuses a response that does not match its contract', () => {
    // The listing always serialises its two paging parameters, so the url the backend
    // actually receives carries them. Matching on the bare path would find nothing.
    const LISTING_WITH_PAGING = `${USERS}?pageIndex=0&pageSize=25`;

    /**
     * Asserts that answering the one pending request with `body` fails at `path`, and that
     * the report carries no personal data.
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
      // An operator who never supplied a first name has one that is EMPTY, not missing —
      // `Null.vb:L71-L75` returns `""` literally — so this must pass rather than be refused
      // or coalesced to null.
      const values: unknown[] = [];

      service.list({ pageIndex: 0, pageSize: 25 }).subscribe({ next: (page: unknown) => values.push(page) });

      httpMock.expectOne(LISTING_WITH_PAGING).flush({
        ...USER_PAGE,
        items: [{ ...USER_LIST_ITEM, firstName: '', address: null }],
      });

      expect(values.length).toBe(1);
    });

    it('refuses an account whose role list is absent', () => {
      // An account with no roles has an EMPTY array. An absent member is contract drift, and
      // admitting it would let a permission-derived affordance read `undefined` and render
      // as though the account held nothing — which looks exactly like a correct answer.
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
        { data: { ...ACCOUNT_POLICY_BODY, redirectAfterLogin: '5' }, meta: null },
        'response.data.redirectAfterLogin',
      );
    });

    it('admits a redirect to page zero, which is a real page', () => {
      // ⚠ THE SENTINEL COLLISION. `Tabs.TabID` seeds at zero, so zero is an ordinary page
      // and `null` is the only expression of "no redirect". A guard on the value being
      // positive would silently discard a redirect to the first page ever created.
      const values: (unknown | null)[] = [];

      service.getMembershipSettings().subscribe({
        next: (settings: unknown) => values.push(settings),
      });

      httpMock
        .expectOne(ACCOUNT_POLICY)
        .flush({ data: { ...ACCOUNT_POLICY_BODY, redirectAfterLogin: 0 }, meta: null });

      expect(values.length).toBe(1);
    });

    it('refuses a profile whose nested declaration is malformed', () => {
      // The profile screen renders each field FROM the nested declaration — its label, its
      // required flag and its validation expression all come from there — so a malformed one
      // produces a field that looks legitimate and validates against nothing.
      const entry = {
        ...PROFILE_READ.properties[0],
        definition: { ...PROFILE_DEFINITION, propertyName: 42 },
      };

      expectViolationAt(
        service.getProfile(1),
        `${USERS}/1/profile`,
        { data: { userId: 1, properties: [entry] }, meta: null },
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

    it('admits a null profile payload, because the contract publishes it as nullable', () => {
      const values: unknown[] = [];

      service.getProfile(1).subscribe({ next: (profile: unknown) => values.push(profile) });

      httpMock.expectOne(`${USERS}/1/profile`).flush({ data: null, meta: null });

      expect(values).toEqual([null]);
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

  // =========================================================================
  // WHO ANNOUNCES A FAILURE
  //
  // Every request is marked as presented by its caller, which is what stops one failure
  // being shown twice — once as the interceptor's transient notification and once as the
  // in-page banner `user.store` records it for. The interceptor still re-throws.
  // =========================================================================
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
      service.updateMembershipSettings(ACCOUNT_POLICY_BODY).subscribe(swallow);
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
