//
// Specification for the account, profile, account-policy and profile-definition
// transport.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE PROVES
// ---------------------------------------------------------------------------
// One thing, in twenty-four parts: that each method on the service under test issues
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
 * The body-bound account search.
 *
 * ⚠ A SEPARATE ADDRESS FROM {@link USERS}, AND THE DISTINCTION IS A PRIVACY BOUNDARY RATHER
 * THAN A ROUTING DETAIL. Four of the listing's filters identify a person — a user name, an
 * email address, and an arbitrary profile-property name paired with the value to match — and a
 * query parameter travels in the REQUEST TARGET, which is written to the browser's history, to
 * every forward and reverse proxy's access log, to the server's access log and to any telemetry
 * that samples URLs. All of those sit at an END of the encrypted channel, so HTTPS does not
 * address it: this is CWE-598. A search carrying any of the four goes here, in a body; a listing
 * that names nobody stays on the cacheable `GET`.
 */
const USERS_SEARCH = '/api/v1/users/search';

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
 * The member-services catalogue of account 1.
 *
 * Spelled out in full rather than composed from {@link USERS}, on the same terms as every
 * other address in this file: composing it would let a wrong template agree with itself.
 */
const MEMBER_SERVICES = '/api/v1/users/1/services';

/**
 * The subscription of account 1 to service 0.
 *
 * ⚠ THE SERVICE IDENTIFIER IS ZERO ON PURPOSE. `Roles.RoleID` seeds `IDENTITY(0, 1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L114), so role
 * zero is the administrator role of every shipped installation - and it is also the value a
 * truthiness test drops. Exercising the address with zero is what proves the subject
 * interpolates what it was handed.
 */
const MEMBER_SERVICE_SUBSCRIPTION = '/api/v1/users/1/services/0/subscription';

/** The trial of service 0, taken by account 1. */
const MEMBER_SERVICE_TRIAL = '/api/v1/users/1/services/0/trial';

/** The invitation-code redemptions of account 1. */
const MEMBER_SERVICE_REDEMPTIONS = '/api/v1/users/1/services/redemptions';

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
const STORED_ACCOUNT_POLICY_BODY: MembershipSettings = {
  // ⚠ #5/#6 — the flag that distinguishes a stored policy from the defaults that stand in for
  // one. True here because this fixture stands for a policy a tenant really saved. The unstored
  // counterpart is {@link UNSTORED_ACCOUNT_POLICY_BODY}, and BOTH exist because a single fixture
  // hard-coding one value structurally prevents the other branch from ever being covered.
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
 * defaults.
 *
 * ⚠ #5/#6 — THE BRANCH A SINGLE `isStored: true` FIXTURE MADE UNTESTABLE. The server answers a
 * tenant that holds no "User Accounts" module instance `200` with the measured legacy defaults and
 * `isStored: false`; it does NOT answer `404`, and it has not since the read stopped reporting
 * absence on the status line. The backend authority for the pair is
 * `backend/tests/DnnMigration.IntegrationTests/Api/UserApiTests.cs`
 * `MembershipSettings_WithoutAUserAccountsModule_ReadsDefaultsAndRefusesTheWrite`, which asserts the
 * `200` with `isStored == false` on the read and the `409` on the write for the same address.
 *
 * The values are the ones `Library/Components/Users/UserModuleBase.vb` L98-L190 applied for an absent
 * key, so this fixture is what a real unstored tenant receives rather than a convenient stand-in: the
 * display-name column shown, the electronic-mail column hidden, ten records a page, and no landing
 * page selected for any of the three outcomes.
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
 * The same policy with the two collision values EXCHANGED.
 *
 * Sending zero where the first fixture sent minus one, and minus one where it sent zero,
 * is what distinguishes "both values survive" from "one value happens to survive twice".
 */
const STORED_ACCOUNT_POLICY_BODY_EXCHANGED: MembershipSettings = {
  ...STORED_ACCOUNT_POLICY_BODY,
  recordsPerPage: -1,
  redirectAfterLogin: 0,
  redirectAfterLogout: -1,
};

/**
 * The report a policy write answers with, wrapped in the shared envelope.
 *
 * Both members carry a value that is NOT the type's default - a true flag and a non-zero
 * count - so a decoder that dropped either would be visible rather than reading as an
 * unremarkable "nothing changed".
 */
const ACCOUNT_POLICY_UPDATE_ENVELOPE = {
  data: { displayNameFormatChanged: true, displayNamesRewritten: 3 },
  meta: null,
};

/**
 * One profile definition.
 *
 * Its identity is ZERO, which is a real identifier here for the same reason minus one is
 * a real tenant: several of this schema's identities are seeded below one. Its name
 * carries mixed case, a hyphen and a space, so any normalisation applied anywhere on the
 * path would be visible.
 */
/**
 * One row of the member-services catalogue: a paid service the account already holds, whose
 * subscription has lapsed.
 *
 * Chosen to be the row that exercises the most contract at once. Its service identifier is
 * ZERO - a real role, and the value a truthiness test drops. Its fee is fifty CENTS, which the
 * legacy projection could not express at all: `GetServices`
 * (`Website/Providers/DataProviders/SqlDataProvider/04.06.00.SqlDataProvider` L993-L1013)
 * selected the fee only when `convert(int, R.ServiceFee) <> 0`, so this value arrived as null
 * and the grid said "Free" for a role the subscribe path still sent to a payment page. And its
 * command is `Renew`, which the legacy screen derived from an expiry earlier than today
 * (`MemberServices.ascx.vb` L288-L305).
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
 * What an invitation code admitted the account to.
 *
 * TWO roles, because the legacy walk had no early exit (`MemberServices.ascx.vb` L397-L433):
 * one code recorded against several roles joined every one of them, so a client that read only
 * the first would under-report what the submission did. One of the two is role zero.
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
 * One account's profile: the tenant's declared properties with this account's values,
 * plus the tenant's decision on whether per-property visibility is offered at all.
 *
 * That last fact travels on the profile projection because the settings endpoint that
 * declares it is administrator-only, so an account reading its own profile cannot see
 * it any other way.
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

  /** The one outstanding body-bound account search. */
  const expectSearch = (): TestRequest => expectRequest('POST', USERS_SEARCH);

  /**
   * The body a search transmitted, narrowed by throwing rather than asserted.
   *
   * The transport types the body as `unknown`, and the workspace forbids the assertion that
   * would silence that. Reading it through an index signature keeps every member access below
   * checked while still letting a case name a member the contract does not declare — which is
   * what a regression would look like.
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
   * Asserts that a request's TARGET carries none of the given values, in its path or its query.
   *
   * The load-bearing assertion of the privacy cases: it is not enough that the value appears in
   * the body, it must be ABSENT from the string that gets logged. The whole target is examined
   * rather than one parameter, because a value smuggled into the path would pass a
   * parameter-by-parameter check.
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

  /**
   * Asserts that a `200` carrying a null payload is REFUSED at the boundary.
   *
   * ⚠ THE COUNTERPART OF THE `404` CASES BESIDE EVERY CALL SITE, AND THE TWO TOGETHER ARE THE
   * WHOLE CONTRACT: absence arrives as a status, a success carries a value, and there is no
   * third answer. A body claiming one is drift and is reported as drift, naming the member.
   *
   * MIGRATION: the four single-resource reads used to be specified as TOLERATING this body,
   *   which is exactly what kept the defect out of sight — the null was emitted as a success,
   *   the store committed it, and a screen showed a blank account, profile, policy or
   *   declaration with nothing anywhere to explain it.
   *
   * The report is checked for personal data for the same reason the malformed-body cases are:
   * this boundary carries names, addresses and telephone numbers.
   *
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

      // The listing answers with the paging envelope DIRECTLY rather than nesting it
      // inside the single-payload wrapper, so nothing is unwrapped and no paging fact is
      // discarded on the way through.
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
      // ⚠ THE GENERIC FILTER CHANGES THE TRANSPORT, AND THIS CASE USED TO ASSERT THAT IT DID NOT.
      // The server matches the paging contract's `query` as a SUBSTRING across the login name, the
      // display name AND the electronic-mail address, so a search through it reaches the same rows
      // the named filters reach - which means it identifies a person just as squarely and must not
      // travel in a request target. Only the four NAMED filters used to be classified, so a
      // query-only search stayed on the GET and put the identifier in the URL. Every OTHER member
      // travels unchanged; the address is what moves.
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

    it('refuses a null payload from a non-conforming intermediary', () => {
      // The return type does NOT admit null, because the endpoint cannot answer that way — the
      // case above is its contract. A proxy, a gateway or a partially rolled-out server can
      // still send one, and the answer is a located refusal rather than a successful blank: an
      // account record shown as empty is indistinguishable, to the operator, from an account
      // with nothing in it.
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
        data: STORED_ACCOUNT_POLICY_BODY,
        meta: null,
      } satisfies ApiResponse<MembershipSettings>);

      expect(observed.values).toEqual([STORED_ACCOUNT_POLICY_BODY]);
      expect(observed.completions.length).toBe(1);
    });

    it('delivers the defaults a tenant with no settings source is answered with, as a 200', () => {
      /*
       * ⚠ #5/#6 — THE ABSENCE OF A STORE ARRIVES INSIDE A SUCCESSFUL DOCUMENT, NOT ON THE STATUS
       * LINE, and this is the fact the frontend fixtures used to contradict. The backend authority is
       * `UserApiTests.MembershipSettings_WithoutAUserAccountsModule_ReadsDefaultsAndRefusesTheWrite`:
       * the read answers `200` carrying the measured legacy defaults with `isStored: false`, and the
       * write for the same address answers `409`. Nothing about this outcome is a failure, so the
       * observable emits a value and completes.
       *
       * MIGRATION: defaults are the behaviour-preserving answer.
       * `Library/Components/Users/UserModuleBase.vb` L94-L194 applied a measured default for every
       * key it could not read, so a tenant without the account module still saw values; and
       * `Library/Components/Users/UserController.vb` L656-L671 assigned its result only inside a
       * not-nothing guard, so such a tenant received `Nothing` with no error whatsoever.
       */
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
      /*
       * ⚠ RETAINED FOR A GENUINELY UNRESOLVED RESOURCE ONLY. A `404` on this address no longer means
       * "this tenant stores no policy" - that answer is the `200` above - so nothing about this status
       * is an ordinary outcome any more and it reaches the caller as a failure like any other read's.
       * The status is exercised here rather than dropped because an intermediary, a withdrawn route or
       * a mis-versioned prefix can all still produce one, and a client that treated it as an absence
       * would present a fault as a configuration state.
       */
      const observed = observe(service.getMembershipSettings());

      const request = expectRequest('GET', ACCOUNT_POLICY);
      request.flush(RESOURCE_NOT_FOUND, { status: 404, statusText: 'Not Found' });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(observed.failures[0].status).toBe(404);
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      // Refused for the same reason as the account read, with one of its own: every screen that
      // pages a listing reads its page size from this policy, so a null committed as a success
      // would silently move the listing onto the fallback size with no failure to explain it.
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
      // ⚠ #5/#6 — TWENTY-FOUR, not twenty-three. `isStored` joined the contract so a tenant with
      // no stored policy can be answered 200 with the legacy defaults instead of 404, and it is
      // ACCEPTED BACK on the write because the API binds request bodies with unmapped-member
      // handling set to disallow - a member present on the read and absent from the write would
      // make every save 400. The count is asserted rather than left implicit precisely so that a
      // member joining or leaving the contract has to be acknowledged here.
      expect(Object.keys(STORED_ACCOUNT_POLICY_BODY).length).toBe(24);
      expectNoInterceptorHeaders(request);

      request.flush(ACCOUNT_POLICY_UPDATE_ENVELOPE);

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);

      // ⚠ THIS WRITE ANSWERS 200 WITH A BODY, unlike every other settings write in this
      // workspace. Adopting a new display-name format rewrites every account's stored display
      // name, and the caller cannot tell from its own request whether it did or how many it
      // touched - so the report is part of the response and is decoded rather than discarded.
      expect(observed.values[0]).toEqual({
        displayNameFormatChanged: true,
        displayNamesRewritten: 3,
      });
    });

    it('keeps a page size of zero and a landing page of minus one distinct from one another', () => {
      // The two collide with the legacy sentinel table, which is exactly why neither may
      // be coalesced: `Null.vb` L41-L45 makes minus one the marker for a missing integer,
      // yet minus one and zero are both real values on this contract. Sending each in
      // turn, and then exchanging them, is what proves nothing is being defaulted.
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
      // `Null.vb` L71-L75 defines the marker for a missing string as the EMPTY STRING,
      // whose body is literally a return of two quotes, and `Users.ascx.vb` L252
      // initialises its query string from that marker. An empty string is therefore what
      // the legacy application produced when it had nothing to say, and converting it to
      // a null on the way out would change the value the server stores.
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
  // The account's own subscriptions
  //
  // Measured against `Website/admin/Users/MemberServices.ascx` and its 530-line
  // code-behind. The whole panel was SELF-SERVICE: every operation it performed passed
  // `UserInfo.UserID` - the signed-in account - even though its container assigned it a
  // user identifier at `manageusers.ascx.vb` L517, and the container hid the tab outright
  // whenever an administrator reached the screen (L61-L66). That is why these five
  // endpoints are gated on account ownership rather than on tenant administration, and it
  // is why they are not sub-resources of the role collection.
  // =========================================================================

  describe('listMemberServices', () => {
    it('reads the catalogue of one account with no query string at all', () => {
      const observed = observe(service.listMemberServices(1));

      const request = expectRequest('GET', MEMBER_SERVICES);

      // Unpaged, exactly as the legacy `grdServices` grid was: it bound the whole answer of
      // `GetUserRoles(portalId, userId, False)` in one pass (`MemberServices.ascx.vb`
      // L147-L157). A page coordinate here would be a parameter the endpoint does not read.
      expect(request.request.params.keys()).toEqual([]);
      expectNoInterceptorHeaders(request);

      request.flush({ data: [MEMBER_SERVICE], meta: null } satisfies ApiResponse<
        readonly MemberService[]
      >);

      expect(observed.values).toEqual([[MEMBER_SERVICE]]);
      expect(observed.completions.length).toBe(1);
    });

    it('carries a sub-unit fee through unrounded, where the legacy projection erased it', () => {
      // THE DIVERGENCE THIS ASSERTION PINS. `GetServices` published the fee only when
      // `convert(int, R.ServiceFee) <> 0`, so a fee of fifty cents arrived as null and the grid
      // rendered "Free" for a role the subscribe path still handed to a payment page. The stored
      // value crosses intact here, and the payment flag - not the presence of a number - is what
      // says whether a charge applies.
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush({
        data: [MEMBER_SERVICE],
        meta: null,
      } satisfies ApiResponse<readonly MemberService[]>);

      const rows = observed.values[0] as readonly MemberService[];

      expect(rows[0].serviceFee).toBe(0.5);
      expect(rows[0].subscriptionRequiresPayment).toBeTrue();
      expect(rows[0].roleId).withContext('role zero is a real role').toBe(0);
    });

    it('answers an account offered nothing with an empty catalogue rather than a failure', () => {
      // A tenant with no public roles is an ordinary state, and the legacy screen showed an
      // empty grid for it. An empty array is therefore a success; only an absent tenant or an
      // absent account is a 404.
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush({ data: [], meta: null } satisfies ApiResponse<
        readonly MemberService[]
      >);

      expect(observed.values).toEqual([[]]);
      expect(observed.failures).toEqual([]);
    });

    it('refuses a command word the API does not publish', () => {
      // The command vocabulary is closed at three, so an unknown word is drift rather than data
      // - it has no wording and no handler on this side. This is the one member of the row
      // validated against a value list; the frequency codes beside it are validated for shape
      // only, because `char(1)` may legitimately carry a code a later release adds.
      const observed = observe(service.listMemberServices(1));

      expectRequest('GET', MEMBER_SERVICES).flush({
        data: [{ ...MEMBER_SERVICE, subscriptionAction: 'Cancel' }],
        meta: null,
      });

      expect(observed.values).toEqual([]);
      expect(observed.failures.length).toBe(1);
      expect(isContractViolation(observed.failures[0])).toBeTrue();
    });

    it('refuses a null payload from a non-conforming intermediary', () => {
      expectNullPayloadRefused(service.listMemberServices(1), MEMBER_SERVICES, 'response.data');
    });
  });

  describe('subscribeToService', () => {
    it('subscribes with no body and completes without a payload', () => {
      const observed = observe(service.subscribeToService(1, 0));

      const request = expectRequest('POST', MEMBER_SERVICE_SUBSCRIPTION);

      // No body: the account and the service are the whole of the request. The legacy screen
      // carried its role identifier as the command argument of the link and nothing else
      // (`MemberServices.ascx` L36).
      expect(request.request.body).toBeNull();
      expect(request.request.params.keys()).toEqual([]);

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(observed.failures).toEqual([]);
      expect(observed.completions.length).toBe(1);
    });

    it('issues the same request for a renewal as for a first subscription', () => {
      // `ServiceText` returned `Subscribe` or `Renew` from the same row state and BOTH ran the
      // same command (`MemberServices.ascx.vb` L288-L305, dispatched at L439-L452). The word is
      // presentation; the request is one request. The catalogue row in hand carries `Renew`.
      expect(MEMBER_SERVICE.subscriptionAction).toBe('Renew');

      observe(service.subscribeToService(1, MEMBER_SERVICE.roleId));

      const request = expectRequest('POST', MEMBER_SERVICE_SUBSCRIPTION);

      expect(request.request.body).toBeNull();
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('propagates the refusal of a service that would require payment', () => {
      // THE EXCLUDED PAYMENT PATH, REPORTED RATHER THAN SIMULATED. The legacy screen redirected
      // a fee-bearing role to `~/admin/Sales/PayPalSubscription.aspx` (`MemberServices.ascx.vb`
      // L113); sales administration is out of scope, so the API answers 403 with its own reason.
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
      // The same protection the role resource enforces, reached through the account resource:
      // one implementation of the rule, two callers. `RoleController.vb` L494-L496 is the other
      // half of it - a paid assignment is EXPIRED rather than removed, which is a success with
      // its own reason rather than a refusal.
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
      // `ShowTrial` (`MemberServices.ascx.vb` L325-L342) offered a trial only for a public role
      // that charges a service fee, charges nothing for the trial, and has not already been
      // tried by this account. The API applies the same predicate and refuses the rest.
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

      // ⚠ UNTRIMMED AND UNFOLDED. The legacy comparison was ordinary string equality against
      // the stored code (`MemberServices.ascx.vb` L410), so leading space and case both
      // mattered. Trimming here would admit codes the legacy application refused, and would
      // make the outcome depend on which client typed the code.
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
      // The legacy screen had two distinct messages for the two outcomes - `RSVPSuccess.Text`
      // and `RSVPFailure.Text` - so a client that treated an empty answer as success would
      // report the failure as a success. The API refuses instead, and this pins that.
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
      // The legacy handler guarded on a non-empty code (`MemberServices.ascx.vb` L403), and the
      // guard was load-bearing: a role with no code recorded read as the empty string through
      // the legacy null contract, so an empty submission would otherwise have matched every
      // such role. The API owns that rule and answers 400; this transport does not second-guess
      // it, because two copies of one rule are one copy too many. A screen may of course also
      // decline to submit.
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

    /**
     * The body counterpart of {@link expectNoTrailingMatchCharacter}.
     *
     * The three prefix filters now travel in a body, so the assertion that no pattern character
     * was appended has to follow them there — a check that only read the query string would pass
     * vacuously and prove nothing about a search at all.
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
      // `Users.ascx.vb` L270-L271 read the name branch and passed the typed text with one
      // match character appended. Appending one here as well would send a doubled pattern.
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
      // `Users.ascx.vb` L268-L269 is the address branch, appending the same character.
      observe(service.list({ pageIndex: 0, pageSize: 25, email: 'ada@example.test' }));

      const request = expectSearch();

      expect(searchBody(request)['email']).toBe('ada@example.test');
      expectTargetCarriesNoneOf(request, ['ada@example.test', 'ada', 'example.test']);
      expectNoTrailingMatchCharacter(request);
      expectBodyCarriesNoMatchCharacter(request);
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

      const request = expectSearch();

      expect(searchBody(request)['profilePropertyName']).toBe(oddPropertyName);
      expect(searchBody(request)['profilePropertyValue']).toBe('Ada');

      // ⚠ THE SHARPEST CASE OF ALL, AND THE REASON THE BODY EXISTS. A tenant declares whatever
      // profile properties it likes, so BOTH halves of this pair are arbitrary tenant data whose
      // meaning neither side knows — the value may be a national identifier or a telephone number.
      // Neither half may appear in the target, and the property NAME is asserted too because a
      // name alone discloses what the tenant collects about its members.
      expectTargetCarriesNoneOf(request, [oddPropertyName, 'Preferred-Locale', 'Ada']);
      expectNoTrailingMatchCharacter(request);
      expectBodyCarriesNoMatchCharacter(request);
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

      // Supplying the word AS a user name makes the request a search, so it travels in a body —
      // and the word is ordinary text there, exactly as it would have been in a query.
      const searched = expectSearch();

      expect(searchBody(searched)['userName'])
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
  // CWE-598 — NOTHING THAT NAMES A PERSON TRAVELS IN A REQUEST TARGET
  //
  // The account listing used to send every filter as a query parameter, including a user
  // name, an email address and an arbitrary profile-property name paired with the value to
  // match. A request target is the most widely recorded part of an HTTP exchange: the
  // browser writes it to its own history, every forward and reverse proxy writes it to an
  // access log, the server writes it to another, and URL-sampling telemetry writes it to a
  // third. All four sit at an END of the encrypted channel rather than in the middle of it,
  // so transport encryption addresses none of them, and redaction is a control that has to
  // be re-applied at every hop and stops working silently when one is added.
  //
  // These cases pin the OUTCOME rather than the mechanism: for each identifying filter, the
  // value must be absent from the transmitted target and present in the body. They also pin
  // the boundary in the other direction — a listing that names nobody must NOT become a
  // POST, because that would give up caching for nothing.
  // =========================================================================

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

      // The presence half, counted: the identifying value is in the BODY, which is the whole point of
      // moving the search off the target. `expectNone` above states the absence half and asserts by
      // throwing, so it records no expectation of its own - this is the one the runner counts.
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

    // ⚠ THE FIFTH IDENTIFYING MEMBER, AND THE ONE THAT WAS MISSING. `query` belongs to the PAGING
    // contract rather than to the account filter, so it was not among the four the compensator
    // classified - yet the server matches it as a case-insensitive SUBSTRING across the login name,
    // the display name and the electronic-mail address (`UserRepository.cs` L131-L134). Any one of a
    // person's three identifiers therefore searched successfully through a member that stayed in the
    // request target, which protected the identifiers an operator selects a mode for and not the one
    // they simply type - the more likely of the two.
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
      // The server reads a blank filter as absent, so such a value can identify nobody. Switching the
      // transport on it would move the UNFILTERED administrative listing off the cacheable GET - and
      // would flip the address on the single input an operator produces by clearing the box.
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
      // A body carrying `profilePropertyName: null` would state a restriction the caller never
      // asked for, and would make an unfiltered search indistinguishable from a filtered one in
      // any request log kept for debugging.
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

      // ⚠ AN EMPTY USER NAME IS STILL A SEARCH, AND MUST STILL USE THE BODY. The transport chooses
      // its address on ABSENCE, never on emptiness: the server matches empty text as a prefix that
      // every value begins with, so a caller sending it is placing a real restriction. Routing this
      // one case back onto the query string would reopen the exposure for precisely the input an
      // operator produces by clearing the box.
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

    /**
     * Every method on the service, each paired with the exact success the SERVER declares.
     *
     * ⚠️ THE SUCCESS STATUS AND BODY ARE PART OF EACH CASE, and they are not
     * interchangeable. An earlier revision of this file drove every method with one
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
        body: { data: [MEMBER_SERVICE], meta: null },
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
      // One pass over every method, asserting the two interceptor headers are unset. The
      // reason to do it once for all of them rather than trusting the per-method checks is
      // that a header added to a shared options object would appear everywhere at once.
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
      // having to be inferred from twenty-four flushes. `ApiResults.Complete(Result)` answers 204
      // and `Created(...)` answers 201; both are read off the controllers' own declarations.
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

      // ELEVEN operations answer 200 with a payload. Two of them are not reads, and each is
      // worth naming. The redemption answers `Ok(...)` carrying the roles the code joined, NOT
      // `Created`, because the resource it creates - an assignment - has no address of its own
      // to name in a location header. The account-policy write answers `Ok(...)` because
      // adopting a new display-name format rewrites the tenant's accounts, and the number it
      // rewrote is not derivable from the request. Subscribing, by contrast, answers 204 for the
      // same reason it takes no body: it is the whole of the request.
      expect(byStatus(200).length).toBe(11);
      expect(byStatus(200)).toContain('PUT /api/v1/users/settings');
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
      'listProfileDefinitions',
      'passwordReset',
      'redeemServiceCode',
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

    it('exposes exactly twenty-five methods and not one more', () => {
      const actual: readonly string[] = Object.getOwnPropertyNames(UserService.prototype).sort();

      expect(actual)
        .withContext('a method added without a specification fails here first')
        .toEqual([...PROTOTYPE_MEMBERS]);
      expect(actual.filter((name) => name !== 'constructor').length).toBe(25);
    });

    it('reads an account picker through its own method, not through a mode of the listing', () => {
      // ⚠ TWENTY-FOUR BECAME TWENTY-FIVE FOR A REASON WORTH STATING AT THE SURFACE. A performance
      // and privacy review measured the role-assignment screen filling its account drop-down — and
      // its account-count probe — from `list`, whose row carries a postal address, a telephone
      // number, an electronic-mail address, a creation instant, a last-login instant and four status
      // flags. All of it crossed the wire so that a key and two captions could be rendered, on a
      // screen permitted to enumerate a tenant of up to a thousand accounts.
      //
      // The remedy is a METHOD rather than an argument, and that is the load-bearing part. A `slim`
      // flag on `list` would let one call site widen the payload for every other, and the widening
      // would compile. Two methods, two return types, and a caller that needs a grid row has to say
      // so.
      expect(PROTOTYPE_MEMBERS).toContain('listChoices');

      for (const forbidden of ['listSlim', 'listMinimal', 'listNames', 'listForPicker']) {
        expect(PROTOTYPE_MEMBERS)
          .withContext(`${forbidden} must not exist; the picker's method is listChoices`)
          .not.toContain(forbidden);
      }
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
        { data: { ...STORED_ACCOUNT_POLICY_BODY, redirectAfterLogin: '5' }, meta: null },
        'response.data.redirectAfterLogin',
      );
    });

    it('refuses a policy-write report that omits the rewrite count', () => {
      // The count is REQUIRED rather than optional, and this is why. A stale server - one that
      // still answers this write with an empty `204` - would satisfy an optional member as
      // `undefined`, and the screen would then report "no accounts were rewritten" when what
      // actually happened is that it cannot tell. Refusing the shape says so.
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
      // The capability decides whether a destructive command is OFFERED. An optional member
      // would arrive as `undefined`, read as falsy, and silently withhold the command from
      // every row on a server that had simply not been upgraded - a listing on which nothing
      // can be removed and no failure anywhere to explain it.
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
      // ⚠ THE SENTINEL COLLISION. `Tabs.TabID` seeds at zero, so zero is an ordinary page
      // and `null` is the only expression of "no redirect". A guard on the value being
      // positive would silently discard a redirect to the first page ever created.
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
      /*
       * ⚠ REFUSED RATHER THAN DEFAULTED, AND THE REASON IS THAT NEITHER DEFAULT IS SAFE.
       * `displayVisibilityEnabled` decides whether the profile screen offers the per-value
       * visibility control at all. Defaulting an absent member to `true` would present the control
       * on a tenant that had switched it off; defaulting to `false` would hide it on a tenant that
       * had not. The API serialises with its ignore condition set to never, so the member is always
       * on the wire and an absent one is contract drift - which is a fact worth reporting rather
       * than papering over with a guess.
       */
      expectViolationAt(
        service.getProfile(1),
        `${USERS}/1/profile`,
        { data: { userId: 1, properties: [] }, meta: null },
        'response.data.displayVisibilityEnabled',
      );
    });

    it('carries the tenant visibility policy through in both states', () => {
      // BOTH STATES, because a decoder that dropped the member would satisfy a case asserting only
      // the `true` one: `undefined` and `true` are not distinguishable by a truthiness test, and
      // `false` is the state the tenant has to store deliberately.
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
      // The profile read answers with the profile or refuses with a not-found problem document.
      // A null is neither, and committing one would present an account as having no profile at
      // all — which is a different fact from a profile whose values are empty, asserted above.
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
