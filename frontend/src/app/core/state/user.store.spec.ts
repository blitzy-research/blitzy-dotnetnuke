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

/** The account collection. Relative, because the production base is relative. */
const USERS_URL = '/api/v1/users';

/**
 * The body-bound account search.
 *
 * ⚠ A SEARCH BY NAME, ADDRESS OR PROFILE PROPERTY GOES HERE, NOT TO {@link USERS_URL}, AND THE
 * REASON IS PRIVACY RATHER THAN ROUTING. All four of those filters identify a person, and a query
 * parameter travels in the REQUEST TARGET — which the browser writes to its history, every forward
 * and reverse proxy writes to an access log, the server writes to another, and URL-sampling
 * telemetry writes to a third. Every one of those recorders sits at an END of the encrypted channel
 * rather than in the middle of it, so HTTPS addresses none of them: CWE-598. The unfiltered listing,
 * which carries page coordinates, an ordering and at most an approval state, names nobody and stays
 * on the cacheable `GET`.
 */
const USERS_SEARCH_URL = '/api/v1/users/search';

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
 * The member-services catalogue of account 7 — the account every fixture in this file uses.
 *
 * Unpaged, exactly as the legacy `grdServices` grid was.
 */
const SERVICES_URL = '/api/v1/users/7/services';

/**
 * The subscription of account 7 to service ZERO.
 *
 * ⚠ THE SERVICE IDENTIFIER IS ZERO ON PURPOSE. `Roles.RoleID` seeds `IDENTITY(0, 1)`, so role
 * zero is the administrator role of every shipped installation — and it is exactly the value a
 * truthiness test drops. Every address here uses it.
 */
const SERVICE_SUBSCRIPTION_URL = '/api/v1/users/7/services/0/subscription';

/** The trial of service zero, taken by account 7. */
const SERVICE_TRIAL_URL = '/api/v1/users/7/services/0/trial';

/** The invitation-code redemptions of account 7. */
const SERVICE_REDEMPTIONS_URL = '/api/v1/users/7/services/redemptions';

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

/**
 * The report the account-policy write answers with, wrapped in the shared envelope.
 *
 * Defaults to "nothing was swept", which is what an ordinary settings save produces, so a fact
 * that merely needs the write to succeed does not have to describe a rewrite it never asked for.
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
 * One row of the member-services catalogue.
 *
 * Defaults to a paid service the account already holds whose subscription has LAPSED, which is
 * the row that exercises the most contract at once: service identifier zero, a fifty-cent fee
 * the legacy projection could not express, and the `Renew` command the legacy screen derived
 * from an expiry earlier than today.
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
   * The body counterpart of {@link parameter}, and it throws for the same reason: absence and the
   * empty string are DIFFERENT values on these filters, so a reader that returned one for the other
   * would erase the distinction several of these cases exist to prove.
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
      // Absence is proved by asking whether the member is THERE, for the same reason the query
      // counterpart does: a reader answering undefined is also what a present-but-empty member
      // answers, and empty text is a legitimate value on this contract.
      expect(Object.prototype.hasOwnProperty.call(sent, name))
        .withContext(`the body member "${name}" must be omitted, not sent empty`)
        .toBe(false);
    }
  };

  /**
   * Asserts that a request's TARGET carries none of the given values.
   *
   * The load-bearing assertion of the privacy cases: it is not enough that a searched value reached
   * the server in the body, it must be ABSENT from the string that gets logged.
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
      expectRequest('PUT', SETTINGS_URL).flush(settingsWriteEnvelope());
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
      write.flush(settingsWriteEnvelope());

      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 50 })));

      const request = expectRequest('GET', USERS_URL);

      expect(parameter(request, 'pageSize'))
        .withContext('the page in hand was fetched at the previous size')
        .toBe('50');

      request.flush(pageFixture([listItemFixture()], { pageSize: 50 }));
    });

    it('publishes what the policy write did to the tenant\'s display names', () => {
      // ⚠ THE ONE SETTINGS WRITE IN THIS WORKSPACE WITH A TENANT-WIDE SIDE EFFECT. Adopting a
      // new display-name format recomposes every account's stored display name, and the caller
      // cannot infer from its own request that it happened or to how many accounts - so the
      // report travels back on the response and is kept here.
      //
      // MIGRATION: `Website/admin/Users/UserSettings.ascx.vb:L175-L182` spawned
      // `UserController.UpdateDisplayNames` (`Library/Components/Users/UserController.vb:L1259-L1268`)
      // on a BACKGROUND THREAD and told the operator nothing. This slice is what replaces that
      // silence.
      openListingAtPageSize(25);

      expect(store.lastSettingsWrite())
        .withContext('nothing has been written yet')
        .toBeNull();

      store.saveMembershipSettings(settingsFixture({ securityDisplayNameFormat: '[LASTNAME]' }));

      expectRequest('PUT', SETTINGS_URL).flush(
        settingsWriteEnvelope({ displayNameFormatChanged: true, displayNamesRewritten: 12 }),
      );
      expectRequest('GET', SETTINGS_URL).flush(
        envelope(settingsFixture({ securityDisplayNameFormat: '[LASTNAME]' })),
      );
      expectRequest('GET', USERS_URL).flush(pageFixture([listItemFixture()], { pageSize: 25 }));

      // ⚠ SURVIVES THE RE-READ THAT FOLLOWS THE WRITE. The re-read clears state belonging to a
      // previous answer, so the command publishes the report AFTER dispatching it; publishing
      // first would discard the very report it was meant to accompany.
      expect(store.lastSettingsWrite()).toEqual({
        displayNameFormatChanged: true,
        displayNamesRewritten: 12,
      });
      expect(store.failure()).toBeNull();
    });

    it('keeps a swept-but-unchanged report distinct from no sweep at all', () => {
      // The two are different answers and an operator is looking for the difference: a format
      // left alone reports false and zero because no sweep ran, while a format that changed on a
      // tenant whose accounts already read that way reports true and zero because the sweep ran
      // and found nothing to alter. Collapsing them would make "nothing happened" and "nothing
      // needed to happen" indistinguishable.
      openListingAtPageSize(25);

      store.saveMembershipSettings(settingsFixture({ securityDisplayNameFormat: '[LASTNAME]' }));
      expectRequest('PUT', SETTINGS_URL).flush(
        settingsWriteEnvelope({ displayNameFormatChanged: true, displayNamesRewritten: 0 }),
      );
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([], { pageSize: 25 }));

      expect(store.lastSettingsWrite()).toEqual({
        displayNameFormatChanged: true,
        displayNamesRewritten: 0,
      });

      store.saveMembershipSettings(settingsFixture());
      expectRequest('PUT', SETTINGS_URL).flush(settingsWriteEnvelope());
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([], { pageSize: 25 }));

      expect(store.lastSettingsWrite()).toEqual({
        displayNameFormatChanged: false,
        displayNamesRewritten: 0,
      });
    });

    it('discards the report on request and on reset, and never publishes one for a refusal', () => {
      openListingAtPageSize(25);

      store.saveMembershipSettings(settingsFixture({ securityDisplayNameFormat: '[LASTNAME]' }));
      expectRequest('PUT', SETTINGS_URL).flush(
        settingsWriteEnvelope({ displayNameFormatChanged: true, displayNamesRewritten: 4 }),
      );
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture()));
      expectRequest('GET', USERS_URL).flush(pageFixture([], { pageSize: 25 }));

      store.clearSettingsWriteReport();
      expect(store.lastSettingsWrite())
        .withContext('dismissing a notice is not abandoning the screen, so nothing is re-read')
        .toBeNull();
      httpMock.expectNone(() => true);

      // A refused write publishes a failure and NO report. The width guard refuses the whole
      // policy when the format would overflow the stored column for any one account, so there is
      // no partial sweep to report - and reporting a zero would read as "it ran and changed
      // nothing", which is not what happened.
      store.saveMembershipSettings(settingsFixture({ securityDisplayNameFormat: '[USERNAME]' }));
      expectRequest('PUT', SETTINGS_URL).flush(
        { title: 'Bad Request', status: 400, type: 'urn:dnnmigration:error:user.display-name.too-long' },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(store.lastSettingsWrite()).toBeNull();
      expect(store.failure()).not.toBeNull();
      expect(store.saving()).toBeFalse();
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

  // =========================================================================
  // THE TENANT'S OPENING-VIEW POLICY
  // =========================================================================

  describe('the opening view the tenant configured', () => {
    /*
     * MIGRATION: `Website/admin/Users/Users.ascx.vb` L494-L506 read `Display_Mode` and set the
     * screen's opening `Filter` from it, and `BindData` (L248-L290) then branched on that value:
     * the localised "All" word listed everything (L264), any other non-"None" value fell through to
     * the search-axis switch (L267) whose default axis was `Username` (L577), and the bare marker
     * "None" matched no branch at all so no query was issued.
     *
     * ⚠ AN EARLIER REVISION IGNORED THE SETTING ENTIRELY and always opened on the unfiltered
     * listing. The tenant's choice made no difference to what the screen did, which is the defect
     * these four cases close.
     */

    it('opens on every account when the tenant chose the unfiltered view', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 0 })));

      const listing = expectRequest('GET', USERS_URL);

      expectOmitted(listing, ['userName', 'email', 'profilePropertyName', 'profilePropertyValue']);
      expect(parameter(listing, 'pageIndex')).toBe('0');

      listing.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'all' });
    });

    it('opens on the first letter of the alphabet strip when the tenant chose that view', () => {
      // The letter is `A`, and its provenance is the resource value the legacy read the first
      // character of: `Users.ascx.resx` `Filter.Text` is "A,B,C,…,Z" and L502 kept `Substring(0, 1)`.
      // The AXIS is the account name, because the legacy search selector's first-added item was
      // "Username" (L577) and the first-letter filter fell through to that switch.
      // ⚠ THE OPENING READ IS THE BODY-BOUND SEARCH, NOT THE UNFILTERED LISTING, BECAUSE A LETTER
      // ON THE ACCOUNT-NAME AXIS NAMES PEOPLE. The transport is chosen by whether the query
      // identifies anybody — see {@link USERS_SEARCH_URL} — and a first-letter view IS an
      // account-name filter, so it travels in a request body like every other name search rather
      // than putting the axis and its value in a request target that four separate recorders keep.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 1 })));

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
      /*
       * ⚠ NOT A DEFECT AND NOT WORKED AROUND. This is the deliberate choice a large tenant makes so
       * that opening the screen does not page a hundred thousand accounts, and it is also the
       * DEFAULT the legacy applied when the setting was absent
       * (`Library/Components/Users/UserModuleBase.vb` L126-L130), which the server reproduces. The
       * alphabet strip and the unfiltered affordance are both on the screen for the operator to act
       * on, so nothing is unreachable.
       */
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 2 })));

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
      /*
       * MIGRATION: the legacy `Select Case` had NO `Case Else`, so an unrecognised mode left `Filter`
       * as the empty string - not the "All" word, not "None" - which fell through to the axis switch
       * and queried `GetUsersByUserName(…, "" & "%")`. An empty prefix plus the server's own trailing
       * wildcard matches every account, so the legacy outcome was the unfiltered listing.
       *
       * ⚠ ASKED FOR AS THE UNFILTERED LISTING RATHER THAN AS AN EMPTY-PREFIX NAME SEARCH, because
       * the target endpoint refuses a filter supplied blank - "omit it to search without it" - so
       * sending the legacy's literal empty prefix would be a refused request where the legacy served
       * a page. The result set is identical; only the way of asking differs.
       */
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 99 })));

      const unrecognised = expectRequest('GET', USERS_URL);

      expectOmitted(unrecognised, ['userName', 'email', 'profilePropertyName', 'profilePropertyValue']);
      unrecognised.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'all' });

      /*
       * An UNREADABLE policy is a different situation from an absent key, and is answered
       * differently on purpose. The legacy default applied to a key missing from a policy it could
       * still read; here the whole policy is gone, the page size falls back for the same reason, and
       * a tenant whose policy is unavailable still has accounts. The failure stays recorded.
       */
      store.reset();
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(
        { title: 'Not Found', status: 404, type: 'urn:dnnmigration:error:portal.not_found' },
        { status: 404, statusText: 'Not Found' },
      );

      const fallback = expectRequest('GET', USERS_URL);

      expectOmitted(fallback, ['userName', 'email', 'profilePropertyName', 'profilePropertyValue']);
      fallback.flush(pageFixture([listItemFixture()]));

      expect(store.search()).toEqual({ mode: 'all' });
      expect(store.failure())
        .withContext('the policy failure is still recorded, so nothing is concealed')
        .not.toBeNull();
    });

    it('leaves a search already chosen alone when the policy arrives', () => {
      // The policy decides how the screen OPENS, not what it shows after an operator has asked for
      // something. A policy read that landed after a search would otherwise discard the search.
      store.searchByEmail('ada');
      expectSearch().flush(pageFixture([listItemFixture()]));

      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 0 })));

      const listing = expectSearch();

      expect(member(listing, 'email')).toBe('ada');
      listing.flush(pageFixture([listItemFixture()]));
      expect(store.search()).toEqual({ mode: 'email', text: 'ada' });
    });
  });

  describe('search modes', () => {
    it('transmits an account-name search verbatim, with no wildcard of its own', () => {
      // MIGRATION: `Users.ascx.vb` L271 appended one trailing per-cent character before
      // calling down; the API reproduces that, wildcard included. Appending one here
      // would double the pattern. A PREFIX match - never described as a containing one.
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
      // MIGRATION: `Users.ascx.vb` L269. The address is neither unique nor a sign-in
      // key - the legacy provider was registered with uniqueness switched off at
      // `release.config` L244 - so this can legitimately match several accounts.
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
      // MIGRATION: `Users.ascx.vb` L274, the third axis, whose field name was passed
      // straight through as the property name and appended to the screen's own query
      // string at L275.
      store.searchByProfileProperty('Nickname', 'Ann');

      const request = expectSearch();

      expect(member(request, 'profilePropertyName')).toBe('Nickname');
      expect(member(request, 'profilePropertyValue')).toBe('Ann');
      expect(member(request, 'profilePropertyValue')).not.toContain('%');
      expectMembersOmitted(request, ['userName', 'email']);

      // ⚠ BOTH HALVES ARE ARBITRARY TENANT DATA, which is what makes this the sharpest case: the
      // tenant declares its own properties, so the name discloses what it collects about its members
      // and the value may be anything at all, up to a national identifier.
      expectTargetCarriesNoneOf(request, ['Nickname', 'Ann']);

      request.flush(pageFixture([listItemFixture()]));
    });

    it('accepts an unfamiliar profile property name without validating it', () => {
      // MIGRATION: the property name is an OPEN SET. A tenant declares whatever
      // properties it likes, so an unrecognised name is the server's to refuse - not
      // this store's to reject, normalise or check against a fixed list.
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
      // MIGRATION: the legacy absence marker for a string WAS the empty string
      // (`Library/Components/Shared/Null.vb` L71-L75 returns `""` literally), so the two
      // were indistinguishable there. Here empty text is DATA on this contract: a caller
      // that asked to match the empty prefix asked for something, and omission is
      // reserved for the caller that asked for nothing.
      store.searchByUsername('');

      // ⚠ AN EMPTY TERM IS STILL A SEARCH, AND STILL USES THE BODY. The transport chooses its
      // address on ABSENCE, never on emptiness, so the one input an operator produces by clearing
      // the box does not fall back onto the query string.
      const request = expectSearch();

      expect(Object.prototype.hasOwnProperty.call(body(request), 'userName'))
        .withContext('empty text is a value, not an absence')
        .toBe(true);
      expect(member(request, 'userName')).toBe('');

      request.flush(pageFixture([]));
    });

    it('publishes the search it applied as a typed discriminator', () => {
      // MIGRATION: the legacy screen branched on LOCALISED strings at L258, L261 and
      // L264, so the query a person got depended on the rendered language. Nothing here
      // compares a display string.
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

      // ⚠ AND THE CLEARED LISTING RETURNS TO THE `GET`. Once no filter names anybody there is
      // nothing to keep out of a request target, and staying on the search address would give up
      // caching and idempotence for nothing.
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

    it('records a null answer as a failure rather than holding it as an empty account', () => {
      // MIGRATION: this case used to assert that a `200` carrying nothing was HELD as null, on the
      //   reading that the envelope was how the server reported "no such account". It is not: the
      //   read translates its outcome through the shared helper, which answers a not-found problem
      //   document as soon as the value is absent, so a null payload is drift. The transport refuses
      //   it at the boundary and this store records the refusal — which is what an operator needs,
      //   because the previous behaviour rendered a blank account record indistinguishable from an
      //   account with nothing in it, with no failure anywhere to explain either.
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

  // =========================================================================
  // PROFILE DECLARATIONS - THE STAGED BATCH
  //
  // Legacy: `Website/admin/Users/ProfileDefinitions.ascx.vb` L446-L448 - the Apply handler called
  // `UpdateProperties()` and then `RefreshGrid()`. `UpdateProperties` (L291-L298) walked the
  // collection and issued the update for each row whose dirty flag was up, one after another,
  // because that is all a `For Each` inside one post-back can be; and the grid was rebound
  // EXACTLY ONCE afterwards.
  //
  // ⚠ WHAT THIS BLOCK GUARDS. A screen that applied N staged rows by issuing the per-row command N
  // times produced N CONCURRENT writes and up to N full catalogue re-reads - one per write, each
  // firing on its own completion - while the shared saving flag fell on the first write to land,
  // leaving a second batch startable on top of the first. The proofs below are therefore mostly
  // NEGATIVE: at every step exactly one write is outstanding, no catalogue read has been issued
  // yet, and after the batch settles exactly one has.
  // =========================================================================

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
     * Settles a whole batch, proving as it goes that ONE write is outstanding at a time and
     * that the catalogue has not been re-read yet.
     *
     * The matched write is taken off the outstanding list by the expectation itself, so the
     * "none" that follows it is the proof that it was the only write in flight rather than
     * merely one of several - which is the property the concatenation exists to provide.
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
      // ⚠ THE FINDING THIS CLOSES. Three per-row commands re-read the whole catalogue three
      // times, and the third read raced the first two. One read, after the batch, is what the
      // legacy handler did and it is what a screen needs to derive which rows are still
      // outstanding: whatever still differs from the server.
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
      // ⚠ THE SECOND HALF OF THE FINDING. With per-row commands the flag fell on the FIRST
      // completion, so a form re-enabled itself while later rows were still travelling and a
      // second Apply could be pressed on top of the first.
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
      // ⚠ THE BATCH IS NOT ATOMIC, AND ABANDONING THE REST WOULD STRAND WORK. The rows address
      // different declarations, so the server applies each on its own merits; a refusal of the
      // middle row must not cost the operator the row behind it.
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
      // The failure slot holds one document, and the first refusal is the one whose cause the
      // operator has to deal with. Recording whichever row happened to answer LAST would be an
      // arbitrary choice presented as a diagnosis.
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
      // Nothing staged is nothing to write, and re-reading the catalogue to prove it would be a
      // request spent to change nothing. The outstanding-request verification in the teardown is
      // what proves the silence.
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
      // SENTINELS ARE DATA on this contract: the declaration table seeds its key at zero, a
      // zero-valued module association is a real association and an empty default is a real
      // default. A batch that coerced any of them would rewrite the operator's intent.
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

  // =========================================================================
  // CONCURRENT WRITES
  //
  // ⚠ THE GROUP THAT EXISTS BECAUSE `saving` USED TO BE A BOOLEAN. Every write set it before
  // dispatching and cleared it in both callbacks, which is exactly right for one write and wrong
  // for every case where two are outstanding: the FIRST response to land cleared it while the rest
  // were still on the wire, so "not saving" stopped meaning "every write has settled" and started
  // meaning "at least one has". There is a real screen that does this — the profile-declaration
  // grid's Apply issues ONE WRITE PER CHANGED ROW, concurrently — and every control on it is
  // disabled on this flag, so a mid-batch clear re-enabled all of them, admitted a second write
  // into the middle of the batch, and let the batch's next response settle THAT write: announced
  // as a success before its own request had answered, with the record of what was awaited
  // discarded, so its eventual refusal had nothing left to attribute it to. A false success and a
  // lost failure, from one shared boolean.
  //
  // The same defect had a second half in the single failure slot, which every write emptied as it
  // started — so a later write's START erased a refusal an earlier one had already recorded.
  //
  // These cases pin both halves. They drive the STORE directly rather than through the screen,
  // because the store is where the invariant lives and a screen could satisfy it by accident
  // through a disabled button.
  // =========================================================================

  describe('concurrent writes', () => {
    /**
     * Drains the re-reads a batch of successful writes leaves behind.
     *
     * Each write's callback re-reads the declaration list, and each re-read supersedes the one
     * before it — so a batch of three leaves one live request and two cancelled ones. All three
     * must be accounted for, because the teardown's `verify()` counts a cancelled request just as
     * a live one.
     */
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
      // The second half of the same defect. Every write clears the failure slot as it starts,
      // which is correct for a fresh attempt and destructive while siblings are outstanding: the
      // row that was refused would be left looking as though it had been written, with the
      // refusal discarded by a request rather than by anything the operator did.
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
      // ⚠ WITHOUT THIS THE COUNT WOULD STRAND. A released write fires neither callback, and the
      // callbacks are where the count comes down — so a boundary crossed with two writes
      // outstanding would leave saving stuck at true for the remaining life of the application,
      // with every form on every account screen disabled and no request outstanding to explain it.
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
      // The count is one fact about the store, not one per endpoint: a form disabling itself on
      // this flag is protecting the operator from a second submission of any kind, not only from
      // a second submission of the same shape.
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

      // ⚠ NO LISTING RE-READ IS EXPECTED, and that is this store's own rule rather than an
      // omission: the account listing dispatches nothing while no search has been chosen, which is
      // the state this case leaves it in. The declaration write re-reads its own list, which is
      // drained below.
      httpMock.expectNone((request) => request.url === USERS_URL);
      drainDefinitionReads();
    });
  });

  // =========================================================================
  // FAILURES
  // =========================================================================

  // =========================================================================
  // THE OPENING LISTING IS THE TENANT'S POLICY DECISION
  // =========================================================================
  //
  // `Users.ascx.vb` L494-L506 branched on the portal's own `DisplayMode` setting to decide what a
  // freshly opened listing shows, and its three values are three different answers:
  //
  //   All (0)         list every account, paged and unfiltered
  //   FirstLetter (1) open on the first letter, so a large tenant does not render thousands of rows
  //   None (2)        list NOTHING and wait to be asked
  //
  // The store used to promote the no-query state to the unfiltered listing UNCONDITIONALLY, which
  // collapsed all three onto `All`. For a `FirstLetter` tenant that reverses a performance decision
  // without being asked; for a `None` tenant it OVERRIDES A POLICY — the one setting whose whole
  // purpose is to withhold the roster was the one setting with no effect, and every account was
  // listed to anybody who opened the screen.
  describe('the opening listing follows the tenant policy', () => {
    it('lists everything when the policy says All', () => {
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 0 })));

      const request = expectRequest('GET', USERS_URL);

      // No filter member of any kind: the unfiltered listing is the absence of one, never a
      // reserved word transmitted as a filter. Absence is proved by asking whether the parameter
      // is THERE — a reader answering null is the weaker claim, because that is also what a
      // present-but-empty parameter answers, and empty text IS a value on this contract.
      expectOmitted(request, ['userName', 'email']);
      expect(store.searchMode()).toBe('all');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('opens on the FIRST LETTER when the policy says FirstLetter', () => {
      // ⚠ THE BRANCH THAT WAS SILENTLY LOST. Before the fix this dispatched the unfiltered listing
      // and this assertion found no account-name filter at all.
      // ⚠ THE OPENING READ IS THE BODY-BOUND SEARCH, NOT THE UNFILTERED LISTING, BECAUSE A LETTER
      // ON THE ACCOUNT-NAME AXIS NAMES PEOPLE. The transport is chosen by whether the query
      // identifies anybody — see {@link USERS_SEARCH_URL} — and a first-letter view IS an
      // account-name filter, so it travels in a request body like every other name search rather
      // than putting the axis and its value in a request target that four separate recorders keep.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 1 })));

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
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 2 })));

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
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 2 })));
      httpMock.expectNone((request) => request.url === USERS_URL);

      store.showAllAccounts();

      const request = expectRequest('GET', USERS_URL);

      expect(store.searchMode()).toBe('all');

      request.flush(pageFixture([listItemFixture()]));
    });

    it('LISTS THE ACCOUNTS when the policy cannot be read, and does not read absence as None', () => {
      // ⚠ THE CONTRACT THAT MUST NOT REGRESS, AND THE DISTINCTION THAT MATTERS MOST HERE. The
      // server answers `404` for a tenant that stores no membership settings, so an absent policy is
      // an ORDINARY case rather than an exceptional one. Withholding the roster is a choice a tenant
      // makes; a failed read is not that choice, so absence falls back to the listing rather than to
      // silence — otherwise one unreadable settings row would make a tenant's accounts unreachable.
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
      // The contract declares this member as a plain integer validated against no closed set, so an
      // unknown value is reachable. Treating one as "withhold everything" would let a single
      // unrecognised integer make a tenant's accounts unreachable; the listing is recoverable.
      store.initialise();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 99 })));

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
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ displayMode: 1 })));

      // An address search is body-bound for the same reason a name search is, so the re-read the
      // policy triggers is the search endpoint on both sides of the policy arriving.
      const request = expectSearch();

      expect(member(request, 'email')).toBe('a@example.test');
      expectMembersOmitted(request, ['userName']);
      expect(store.searchMode()).toBe('email');

      request.flush(pageFixture([listItemFixture()]));
    });
  });

  // =========================================================================
  // A WRITE SETTLES ITS OWN FLOW AND NOBODY ELSE'S
  // =========================================================================
  //
  // Every one of this store's thirteen writes reports through one slice. That slice used to be a
  // BOOLEAN, which answers "is anybody writing" — indistinguishable from "is MY write finished" only
  // while at most one write can be outstanding. `profile-definition-list.applyChanges` dispatches
  // ONE WRITE PER PENDING ROW, so several are genuinely in flight at once, and the first to answer
  // set the flag false: every flow watching it concluded its own write had finished, re-enabled its
  // form, cleared its awaited marker and announced a confirmation, while the rest were still on the
  // wire. A later failure among them then arrived at a screen that had already reported success.
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
      // ⚠ THE ONE PLACE THE COUNTER IS SET RATHER THAN STEPPED, and the reason is that a reset
      // releases every write handle, so none of them will ever reach a settle call. A decrement
      // would subtract one from a counter standing at several and leave the store permanently
      // claiming to be saving — every form on it disabled for the rest of the session.
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
  // THE ACCOUNT'S OWN SUBSCRIPTIONS
  //
  // `Website/admin/Users/MemberServices.ascx` and its 530-line code-behind. The panel was
  // SELF-SERVICE throughout: every operation passed `UserInfo.UserID` — the signed-in
  // account — even though its container assigned it a user identifier
  // (`manageusers.ascx.vb` L517), and the container hid the tab whenever an administrator
  // reached the screen (L61-L66). Each of its handlers re-bound the grid after acting
  // (L118, L133, L430), which is the behaviour every command below reproduces.
  // =========================================================================

  describe("the account's own subscriptions", () => {
    it('reads the catalogue unpaged and publishes the account it belongs to', () => {
      store.loadMemberServices(7);

      const request = expectRequest('GET', SERVICES_URL);

      // No page coordinate, no ordering, no filter: the legacy grid bound the whole answer in
      // one pass, and the endpoint reads no parameter.
      expect(request.request.params.keys()).toEqual([]);
      expect(store.memberServicesLoading()).toBeTrue();

      request.flush(envelope([serviceFixture(), serviceFixture({ roleId: 9, isSubscribed: false, isExpired: false, subscriptionAction: 'Subscribe' })]));

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
        envelope([
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
      expectRequest('GET', SERVICES_URL).flush(envelope([]));

      expect(store.memberServices()).toEqual([]);
      expect(store.hasMemberServices()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('clears the rows when the account changes, and keeps them when it does not', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));

      // A refresh of the SAME account keeps what is on screen: blanking it would flicker a grid
      // that is about to answer with almost the same rows.
      store.loadMemberServices(7);
      expect(store.memberServices().length)
        .withContext('a refresh of the same account keeps the rows in hand')
        .toBe(1);
      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));

      // A DIFFERENT account clears them at once, because rendering one account's subscriptions
      // under another account's key is the one outcome that cannot be allowed even briefly.
      store.loadMemberServices(11);
      expect(store.memberServices())
        .withContext("another account's catalogue is never shown while the read is in flight")
        .toEqual([]);
      expect(store.memberServicesAccountId()).toBe(11);
      expectRequest('GET', '/api/v1/users/11/services').flush(envelope([]));
    });

    it('keeps the rows in hand when a read fails, and records the failure by name', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));

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
        envelope([serviceFixture({ isExpired: false, subscriptionAction: 'Unsubscribe' })]),
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
      // ⚠ READ AS THE CLIENT NORMALISES IT, NOT AS THE SERVER SPELLS IT. `failureCode`
      // lower-cases the reason and rewrites every hyphen as an underscore, deliberately
      // mirroring what `GlobalExceptionHandler` does before it chooses a status — so one
      // spelling difference cannot make a client and a server disagree about a reason.
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

      // The server may EXPIRE the assignment rather than remove it — `RoleController.vb`
      // L494-L496 expires one whose role charges a fee — and both are successes answering 204.
      // Re-reading is what shows which happened, which is why the row's own state is the answer.
      expectRequest('GET', SERVICES_URL).flush(
        envelope([serviceFixture({ isSubscribed: false, isExpired: false, subscriptionAction: 'Subscribe' })]),
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
        envelope([serviceFixture({ trialOffered: false, isExpired: false })]),
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

      // ⚠ UNTRIMMED AND UNFOLDED. The legacy comparison was ordinary string equality against
      // the stored code (`MemberServices.ascx.vb` L410), so leading space and case both
      // mattered; trimming here would admit codes the legacy application refused.
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

      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));

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

      // The legacy screen had two distinct messages for the two outcomes, so an empty success
      // would report a failure as a success.
      expect(store.lastRedemption()).toBeNull();
      expect(store.failure()?.operation).toBe('redeemServiceCode');
      expect(store.failureReasonCode()).toBe('user.service.code_not_matched');
    });

    it('discards a redemption report once a later command changes the state it described', () => {
      store.redeemServiceCode(7, 'Founders-2026');
      expectRequest('POST', SERVICE_REDEMPTIONS_URL).flush(
        envelope({ roles: [{ roleId: 0, roleName: 'Premium Members' }] }),
      );
      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));

      expect(store.lastRedemption()).not.toBeNull();

      store.cancelService(7, 0);

      expect(store.lastRedemption())
        .withContext('a report of what a code joined is no longer true once one is cancelled')
        .toBeNull();

      expectRequest('DELETE', SERVICE_SUBSCRIPTION_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expectRequest('GET', SERVICES_URL).flush(envelope([]));
    });

    it('dismisses its own redemption report without touching the catalogue', () => {
      store.redeemServiceCode(7, 'Founders-2026');
      expectRequest('POST', SERVICE_REDEMPTIONS_URL).flush(
        envelope({ roles: [{ roleId: 0, roleName: 'Premium Members' }] }),
      );
      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));

      store.clearRedemption();

      expect(store.lastRedemption()).toBeNull();
      expect(store.memberServices().length)
        .withContext('dismissing a message is not a reason to discard the rows')
        .toBe(1);
    });

    it('discards the catalogue, its account and its report when the session ends', () => {
      store.loadMemberServices(7);
      expectRequest('GET', SERVICES_URL).flush(envelope([serviceFixture()]));
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
  // -------------------------------------------------------------------------
  // SESSION ISOLATION
  //
  // `reset` released the READS and left the WRITES listening, and the two halves of that gap were
  // separately serious.
  //
  // A write's callback selects an account, re-reads the listing and records an outcome. Left
  // listening across a session boundary it performed all three on behalf of the session that
  // ended — repopulating the very slices the reset had just cleared with the PREVIOUS OPERATOR'S
  // accounts. That is personal data: names, addresses, telephone numbers and profile answers,
  // shown to whoever signed in next, with no command issued to explain where it came from.
  //
  // And the handles were held in an RxJS `Subscription` used as a container, which is CLOSED once
  // unsubscribed: anything added afterwards is unsubscribed the instant it is added. So releasing
  // them at a boundary would have released them correctly ONCE and then silently cancelled every
  // subsequent write for the remaining life of the application — every save after one sign-out
  // dispatched and never reporting an outcome. The handles are now a set, which is emptied and
  // reused.
  // -------------------------------------------------------------------------
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
      // ⚠ THE GAP THIS CLOSES. The callback of this write selects the account it wrote and re-reads
      // the listing. Left listening it would put one operator's account into a store that a second
      // operator's screen is about to render.
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
      // ⚠ A CANCELLED STREAM NEVER COMPLETES, so the arm that lowers the batch count never runs.
      // Left standing, that count would refuse the FIRST batch the next operator staged - silently,
      // and for the remaining life of the store, because nothing else lowers it.
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
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture({ recordsPerPage: 25 })));

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
      // ⚠ THE REGRESSION GUARD FOR THE CLOSED-CONTAINER TRAP. With a container, this write would be
      // cancelled the instant it was registered — dispatched, and then silently abandoned — and the
      // operator would watch a save that never reports anything, for the rest of the application's
      // life.
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
      // A set does not detach a finished child by itself, which an RxJS container did — so the
      // teardown is registered explicitly. Without it the set would gain one entry per write ever
      // issued and never lose one.
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

  // -------------------------------------------------------------------------
  // WRITE IDENTITY, AND WHY AN AGGREGATE FLAG COULD NOT SETTLE A WRITE
  //
  // This store published ONE boolean for "a write is in flight" and ONE failure slot, and it is
  // provided at the application root. Every screen that dispatched a write therefore watched the
  // same boolean fall and then read the same slot to learn its own outcome. Three distinct wrong
  // answers follow, and none is visible from inside one screen:
  //
  //   (a) TWO WRITES, ONE FLAG. The account list dispatches a removal, a settings pane dispatches a
  //       save, the save settles first — the flag falls and BOTH conclude their own write is done.
  //       The list clears the marker naming the row it was deleting, so the refusal that arrives
  //       afterwards has nothing to attribute itself to and the row silently stays.
  //   (b) SOMEBODY ELSE'S FAILURE. One write succeeds and another is refused; the successful one
  //       reads the slot, finds the other's refusal and reports it as its own outcome.
  //   (c) A REFUSAL SEEN AS A SUCCESS. Every dispatch clears the slot, so whether a screen sees its
  //       own refusal depends on what else the application happened to do next.
  //
  // The profile-declaration screen made (a) routine rather than occasional: its Apply command
  // dispatches one write per edited row, in parallel, all against the one flag and the one slot.
  //
  // NOTE ON WHAT THESE CASES DO NOT ANSWER. Several of these writes ask the store to re-read the
  // account listing once they land, but a store that has never been given a query issues no listing
  // request at all — `dispatchUsers` returns early for the no-query state, exactly as the legacy
  // screen left its grid unbound. None of these cases sets a query, so none of them answers a
  // listing read, and that is deliberate: write identity is a property of the write, and mixing a
  // listing read into every case would only add an address to keep in step with the transport.
  // -------------------------------------------------------------------------
  describe('every write is settled by identity rather than by an aggregate flag', () => {
    it('hands back a distinct identifier for every write, and never the absent value', () => {
      // Zero is reserved as "no write awaited" by the screens that hold one of these, so the FIRST
      // identifier must not be zero. The counter therefore pre-increments, asserted here rather
      // than left to a comment.
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
      // ⚠ DEFECT (a), AND THE CASE THE AGGREGATE FLAG COULD NOT EXPRESS. Both writes are open; the
      // second answers first. The result must name the SECOND, and the aggregate must stay raised
      // because the first is still open.
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
      // ⚠ DEFECT (b). The refused write and the successful one overlap, and the successful one
      // settles LAST — so the shared failure slot holds a refusal at the very moment the successful
      // write's result is published. The result must still carry no failure.
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

      // ⚠ AND DEFECT (c) IN THE SAME BREATH. Every command on this store opens by clearing the
      // shared slot, so a screen holding the refusal there loses it the moment ANY other screen
      // dispatches anything at all. Here the settings pane simply reads — no write, no failure, no
      // relationship to the removal — and the refusal is gone. A screen that had read its outcome
      // from the slot would now conclude the REFUSED removal succeeded, purely because of what the
      // application happened to do next. The published result is unaffected, which is the point.
      store.loadMembershipSettings();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settingsFixture()));

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
      // ⚠ THE CASE A PAIR OF CALLBACKS CANNOT SEE, WHICH IS WHY THE COUNT IS LOWERED FROM
      // `finalize`. Neither the next nor the error path runs for a subscription that is simply
      // unsubscribed, so a write released by a session boundary would otherwise leave the count
      // raised for the life of the application and the store would report itself busy for ever.
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

});
