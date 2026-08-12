/**
 * Specification for {@link RoleAssignmentComponent} — Manage Users in Role.
 *
 * ## THERE IS NO PREDECESSOR HARNESS, AND THAT IS STATED RATHER THAN GLOSSED
 *
 * The legacy tree contains no automated tests of any kind, so nothing here was ported: every
 * assertion below is derived from MEASURED legacy source, and each one names the file and line it
 * came from. The five behavioural authorities are `Website/admin/Security/securityroles.ascx` (93
 * lines — the fields, the three validators and the grid), `Website/admin/Security/SecurityRoles.ascx.vb`
 * (668 lines — the workflow), the paired `App_LocalResources/SecurityRoles.ascx.resx` (every visible
 * string), `Website/App_GlobalResources/SharedResources.resx` (the Delete and confirmation wording)
 * and `Library/Components/Security/Roles/RoleController.vb` (the assignment rules), with
 * `Library/Components/Shared/Null.vb` supplying the sentinel table. All are read-only references and
 * none is altered by this work.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * Membership of a role IS authorisation on this platform: the permission evaluator resolves what a
 * caller may do from the roles they hold. A membership added to the wrong role, removed when it
 * should have been protected, or written with the wrong effective window changes who can administer a
 * tenant. Nothing else in the workspace asserts any of it.
 *
 * ## AND WHY IT IS THE ONLY THING THAT TYPE-CHECKS ITS SIBLINGS
 *
 * `tsconfig.app.json` declares `files: ["src/main.ts"]` and type-checks by IMPORT GRAPH, so a clean
 * production build does not prove this component or its template compiles. `tsconfig.spec.json`
 * instead INCLUDES every specification in the source tree and declares no `files` array, which makes
 * this file the route by which the class, the template and every binding in it finally reach the
 * compiler. That is a first-class responsibility of this specification and not a side effect of it.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - The component is mounted as the standalone unit it is, with the REAL {@link RoleService} and
 *     {@link UserService} resolved from the injector and every request answered through
 *     `HttpTestingController`, so each assertion about an address, a body or a status is an assertion
 *     about the wire. This screen talks to those services directly rather than through the role
 *     store, because the store's page coordinate is private with only a page-index setter.
 *   - Its four identifiers are delivered through `componentRef.setInput` as the STRINGS route
 *     parameters are, so each input's own parsing runs rather than being bypassed.
 *   - `NotificationService.notify` is spied and called through, so announcements are observable.
 *   - `LOCALE_ID` IS PINNED. The shared date pipe injects it (`shared/pipes/date-display.pipe.ts`
 *     takes `@Inject(LOCALE_ID)`), so without the pin every rendered date would be
 *     machine-dependent and the sentinel cases below would prove nothing portable.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE, WITH THE THREE PLACES PLANNING AND CODE DISAGREE
 *
 * ⚠ THE ADD ENDPOINT DECLARES `204`, NOT `201`. `RoleService.assignUser` is typed
 * `Observable<void>` and its own note records that the migration plan described `201` for an add and
 * `204` for an update while "the controller as built declares `204` for BOTH". The screen therefore
 * inspects no status at all, which is what makes the legacy UPSERT faithful — see the pair of cases
 * under "adding a membership", where both answers land on the success path.
 *
 * ⚠ `role_assignment.protected` ARRIVES AS `403`, NOT `409`. It is the one member of the shared
 * conflict vocabulary that does, which the vocabulary itself records inline, and `problemSeverity`
 * maps `403` to a WARNING. The refusal therefore surfaces at warning severity carrying the legacy
 * `RoleRemoveError` sentence verbatim. The legacy screen showed that sentence as a red error
 * (`SecurityRoles.ascx.vb:L583`) while presenting an access refusal as a yellow warning
 * (`AccessDenied.ascx.vb:L43` and `:L45`); this API routes the refusal through the access status, so
 * the shared summariser's severity is the one that applies and the wording is what the code carries.
 * Asserting `'error'` here would assert a value this implementation cannot produce.
 *
 * ⚠ EVERY WRITE IS FOLLOWED BY A RE-READ OF THE MEMBERSHIPS, INCLUDING A FAILED REMOVAL, and the
 * message is raised AFTER that read. The screen re-reads so that what a person sees is what the
 * server holds rather than what the browser guessed, which matters because a `204` from the removal
 * does not promise the row is gone.
 */
import { LOCALE_ID, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { RoleAssignmentComponent } from './role-assignment.component';
import { API_ENDPOINTS } from '../../../core/config/api-endpoints';
import { DEFAULT_PAGE_SIZE } from '../../../core/models/paged-result.model';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { RoleStore } from '../../../core/state/role.store';

import type { WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { HttpRequest } from '@angular/common/http';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, UserRole } from '../../../core/models/role.model';
import type { MembershipSettings, UserChoice } from '../../../core/models/user.model';

/**
 * The tenant the doubled identity reports.
 *
 * `Portals.PortalID` is `IDENTITY(-1, 1)`, so the first tenant a schema ever creates carries -1 —
 * which is also the legacy absent-integer marker. Using it here is what proves the request is issued
 * for a real tenant rather than skipped by a truthiness test.
 */
const TENANT_ID = -1;

// ==================================================================================================
// ADDRESSES — RELATIVE, ALWAYS
//
// `environment.ts` sets `apiBaseUrl` to the root-relative '/api/v1' because the containerised
// application is served through a reverse proxy that forwards `/api/` to the API on the very origin
// that served it. The `test` architect target declares NO `fileReplacements` — and this workspace's
// replacements are INVERTED, with `production` holding an empty list while `development` swaps the
// file — so a specification compiles against that production module. No absolute host, no scheme and
// no import of the environment module appears anywhere below.
// ==================================================================================================

const ROLES_URL = '/api/v1/roles';
const USERS_URL = '/api/v1/users';

/**
 * The account PICKER's address, which every account read on this screen uses.
 *
 * ⚠ THREE READS SHARE IT, AND THAT IS THE POINT. The count probe, the complete-list walk that fills
 * the drop-down, and the name box's lookup all ask this one address for a key and two captions —
 * `userId`, `username` and `displayName` — because those three values are all this screen renders.
 * They used to ask the account LISTING instead, whose row carries a postal address, a telephone
 * number, an electronic-mail address, a creation instant, a last-login instant and four status flags
 * besides; on a tenant the drop-down may enumerate, that was up to a thousand accounts' worth of
 * personal detail moved into browser memory to caption a `<select>`. A performance and privacy
 * review measured it, and this address is the remedy.
 *
 * ⚠ A `GET` IS CORRECT HERE, and it is worth saying why, because the account listing's own search is
 * deliberately a `POST`. That one filters on an electronic-mail address and on arbitrary
 * tenant-defined profile values, which identify a person and must not reach a request target — the
 * browser's history, every proxy's access log, the server's access log and any URL-sampling
 * telemetry all record one, and each sits at an END of the encrypted channel rather than in the
 * middle of it (CWE-598). This address takes ONE filter: a prefix of the login name, which is a
 * value it returns in the response body anyway. There is nothing here that a request target would
 * record and the body did not already carry.
 */
const USERS_CHOICES_URL = '/api/v1/users/choices';

/**
 * The role-group collection, addressed by the cases that stand a SIBLING screen's write alongside
 * this screen's own.
 *
 * Written out for the same reason as its two neighbours rather than derived from the endpoint table:
 * the point of stating an address literally is that a change to it fails here.
 */
const ROLE_GROUPS_URL = '/api/v1/role-groups';

function roleUrl(roleId: number): string {
  return `${ROLES_URL}/${roleId}`;
}

function membersUrl(roleId: number): string {
  return `${ROLES_URL}/${roleId}/users`;
}

function memberUrl(roleId: number, userId: number): string {
  return `${membersUrl(roleId)}/${userId}`;
}

/**
 * The address shape of the keyed membership probe: one role's key, then one account's key.
 *
 * Held as a pattern rather than as a literal because the probe is issued for whichever pairing a case
 * chose, and a helper that had to be told the identifiers would be told them twice — once by the case
 * and once by the component — with nothing making the two agree. Both keys admit `0` and `-1`, because
 * the role identity is seeded at zero and no identifier on this path is ever tested for magnitude.
 */
const MEMBERSHIP_PROBE_PATH = /^\/api\/v1\/roles\/-?\d+\/users\/-?\d+$/;



/** Where the tenant's account policy is read from, which is what selects the account control. */
const MEMBERSHIP_SETTINGS_URL = `${USERS_URL}/settings`;

/**
 * The page size the account lookup asks for.
 *
 * ⚠ THE SERVER'S MAXIMUM, NOT THE SHARED DEFAULT OF TEN. The lookup walks pages until it finds the
 * exact name, so it asks for the widest page the listing's own paging rules permit — the fewer
 * requests one walk makes, the sooner the answer arrives. Restated as the literal the request
 * carries rather than derived, so a change to it is detected here.
 */
const LOOKUP_PAGE_SIZE = '100';

/**
 * How many pages one account lookup will request before it stops and says so.
 *
 * Restated rather than imported for the same reason as the page size. It is also why the ceiling
 * case below drives every one of those pages by hand: proving the walk stops requires reaching the
 * stop, and a mocked constant would prove only that the mock was honoured.
 */
const LOOKUP_PAGE_CEILING = 20;

/**
 * How many pages the complete-account-list walk will request before it refuses.
 *
 * Restated rather than imported for the same reason as its two neighbours, and it earns the
 * restatement more than either: the component DERIVES it from the legacy enumeration threshold, so a
 * literal here is what proves the derivation still lands where it should. It was one thousand pages —
 * a hundred thousand accounts — before a performance review measured what that permitted.
 */
const ACCOUNT_CHOICE_PAGE_CEILING = 10;

/**
 * The order the lookup asks the account listing for, which is what makes the ordinary case one
 * request.
 *
 * The filter is a literal PREFIX match, so every account returned begins with the typed term;
 * ascending by login name therefore puts the shortest match first, and the shortest possible match
 * is the term itself. Asserted on the request rather than assumed, because the guarantee is the
 * server's ordering and a silently dropped sort parameter would remove it without failing anything
 * else.
 */
const LOOKUP_SORT_FIELD = 'Username';
const LOOKUP_SORT_DIRECTION = 'Ascending';

/**
 * The two values of the tenant's account-control policy, as the legacy `UsersControl` enumeration
 * numbered them (`UserModuleBase.vb:L42-L45`).
 *
 * Restated rather than imported for the reason the wording block below gives: a change to either
 * number is a change to the contract and must be noticed here.
 */
const USERS_CONTROL_COMBO = 0;

/**
 * An account count comfortably inside the legacy enumeration threshold.
 *
 * Used by every case that asks for the drop-down without being about the threshold, so the mount
 * answers the count probe with a size that leaves the stored preference standing. Cases that ARE about
 * the threshold pass their own count.
 */
const SMALL_TENANT_ACCOUNT_COUNT = 3;
const USERS_CONTROL_TEXT_BOX = 1;

// ==================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported, so a change to any of it is detected HERE rather than silently
// agreed to. Each is the legacy resource VALUE, its capitalisation included.
// ==================================================================================================

const TITLE_FALLBACK = 'Manage Users in Role';
const CAPTION = 'User Roles';
const USER_LABEL = 'User Name';
const VALIDATE_PLACEHOLDER = 'Validate';
const ADD_USER_LABEL = 'Add User to Role';
const UPDATE_USER_ROLE_LABEL = 'Update User Role';
const DELETE_LABEL = 'Delete';
const CANCEL_LABEL = 'Cancel';
const CONFIRM_REMOVAL_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const NO_MATCHING_USERS =
  "No account's login name starts with that. The search matches the beginning of the login " +
  'name, not the display name.';
const MATCHES_LABEL = 'Matching accounts';
const MATCHES_SELECTOR = 'ul.role-assignment__matches';
const MATCH_ACTION_SELECTOR = 'button.role-assignment__match-action';
const ROLE_UNRESOLVED = 'No security role was addressed, so no memberships can be shown.';
const USER_HELP = 'Enter The User Name and click Validate to confirm';
const USER_CHOICE_HELP = 'Choose an account from every account in this site.';
const USER_CHOICE_PROMPT = '<None Specified>';
const ACCOUNT_CHOICES_EMPTY = 'This site holds no accounts to choose from.';
const ACCOUNT_CHOICES_UNAVAILABLE =
  'Every account in this site could not be listed, so the name box is offered instead.';
const ACCOUNT_POLICY_UNAVAILABLE =
  "This site's preferred account selector could not be read, so the name box is offered.";

/**
 * The measured legacy threshold, mirrored rather than imported because the component keeps it
 * module-private. `UserModuleBase.vb:L178-L183` defaulted an ABSENT `Security_UsersControl` to the
 * name box above one thousand accounts and to the drop-down at or below it.
 */
const LEGACY_ACCOUNT_LISTING_CEILING = 1000;

const ACCOUNT_POLICY_DEFAULTED_BY_SIZE =
  "This site's preferred account selector could not be read, and the site holds more than " +
  `${LEGACY_ACCOUNT_LISTING_CEILING} accounts, so the name box is offered rather than a list of ` +
  'every one of them.';

/** The page size the count probe asks for: one record, because the answer wanted is the total. */
const ACCOUNT_COUNT_PROBE_PAGE_SIZE = '1';

/**
 * The three validator messages, as the resource file holds them AFTER the shared field wrapper has
 * normalised them.
 *
 * ⚠ THE ASYMMETRY IS REAL AND IS THE POINT. `valEffectiveDate.Text` is a break tag followed by a
 * SPACE — `'<br> Invalid effective date'` — while `valExpiryDate.Text` has no such space. A stripper
 * that removed only the tag would indent one message and not the other, so the wrapper's
 * `stripLegacyBreakTags` consumes the tag AND the whitespace after it before trimming. Every
 * assertion below compares the rendered text EXACTLY, with no trimming of its own, so a reappearing
 * leading space fails rather than hides.
 */
const INVALID_EFFECTIVE_DATE_MESSAGE = 'Invalid effective date';
const INVALID_EXPIRY_DATE_MESSAGE = 'Invalid expiry date';
const DATES_OUT_OF_ORDER_MESSAGE = 'Expiry Date must be Greater than Effective Date';

/** `RoleRemoveError.Text`, published by the shared conflict vocabulary for this code. */
const REMOVAL_REFUSED_MESSAGE =
  'You Can Not Remove The Portal Administrator Or The Registered Users Role';

/** The generic wording a permission refusal carries when it names no failure code. */
const FORBIDDEN_MESSAGE = 'You do not have permission to perform this action.';

/**
 * `SecurityRole.Header`, which this screen must NEVER render.
 *
 * `SecurityRoles.ascx.vb:L245` executes `grdUserRoles.Columns(2).Visible = False` in precisely the
 * role-centric mode this component implements, so the column the markup declares at
 * `securityroles.ascx:L76` was never shown. It is asserted ABSENT so a future editor cannot re-add
 * a column the legacy screen did not have.
 */
const SECURITY_ROLE_HEADER = 'Security Role';

/** `ModuleHelp.Text`, an untrusted HTML fragment that has no home in the closed component set. */
const MODULE_HELP_FRAGMENT = 'About Manage Security Roles';

const EFFECTIVE_DATE_CONTROL_ID = 'role-assignment-effective-date';
const EXPIRY_DATE_CONTROL_ID = 'role-assignment-expiry-date';
const NOTIFY_CONTROL_ID = 'role-assignment-notify';

// ==================================================================================================
// FIXED DATES
//
// ⚠ NOT ONE READ OF THE CLOCK APPEARS IN THIS FILE. No zero-argument date construction, no epoch
// helper, no high-resolution timer and no source of randomness: date logic asserted against a floating
// clock is flaky by construction, and the whole point of these cases is that they mean the same thing
// on every run.
// ==================================================================================================

/** An ordinary effective bound. */
const EFFECTIVE_DATE = '2024-03-01';

/** An ordinary expiry bound, later than {@link EFFECTIVE_DATE}. */
const EXPIRY_DATE = '2024-06-15';

/** The equal-bounds case, which `operator="GreaterThan"` makes INVALID. */
const EQUAL_DATE = '2024-06-15';

/** An expiry EARLIER than {@link EFFECTIVE_DATE}, which inverts the window. */
const EARLIER_EXPIRY_DATE = '2024-01-10';

/** A well-formed value naming a day that does not exist, which the data-type check must reject. */
const IMPOSSIBLE_DATE = '2024-02-31';

/** The legacy date sentinel — `Null.NullDate` is `Date.MinValue` (`Null.vb:L66-L70`). */
const SENTINEL_DATE = '0001-01-01T00:00:00';

/**
 * The sentinel carrying a NON-ZERO TIME.
 *
 * `Null.vb:L222-L224` compares `objDate.Date.Equals(NullDate.Date)` — the DATE PART ALONE — and
 * `GetNull` notes at `:L183-L187` that this "avoids subtle time differences". A sentinel with a time
 * on it is therefore still unset, and the shared pipe reproduces that by testing the UTC date triple.
 */
const SENTINEL_DATE_WITH_TIME = '0001-01-01T13:45:00';

/**
 * The real "perpetual" expiry, and NOT a sentinel.
 *
 * `RoleController.vb:L542` maps the `'O'` billing code to `New System.DateTime(9999, 12, 31)`, as
 * against `:L541` where `'N'` maps to `Null.NullDate`. It is a stored value and must render like any
 * other.
 */
const PERPETUAL_DATE = '9999-12-31';

/** {@link EXPIRY_DATE} as the pinned locale renders it through the shared pipe. */
const EXPIRY_DATE_RENDERED = '6/15/2024';

/** {@link PERPETUAL_DATE} as the pinned locale renders it. */
const PERPETUAL_DATE_RENDERED = '12/31/9999';

/** What the sentinel must NEVER render as. */
const SENTINEL_DATE_MISRENDERED = '01/01/0001';

/** What an unparseable instant must never render as either. */
const INVALID_DATE_TEXT = 'Invalid Date';

/**
 * The bound a refused-but-expired assignment comes back with.
 *
 * `RoleController.vb:L496` back-dates the expiry by one day rather than deleting the row, so a
 * removal can legitimately answer `204` and leave the membership in place with a bound already
 * behind it. A fixed leap day is used so the case reads the same on every run and so the shared
 * pipe's calendar validation is exercised on a day that genuinely exists.
 */
const BACKDATED_EXPIRY_DATE = '2024-02-29';

/** {@link BACKDATED_EXPIRY_DATE} as the pinned locale renders it. */
const BACKDATED_EXPIRY_RENDERED = '2/29/2024';

// ==================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// ==================================================================================================

/** The scheme and namespace the API puts in front of every failure code it publishes. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

/**
 * The title for one wire status.
 *
 * Written as an explicit narrowing rather than with an absent-value shorthand, which is the house style
 * of the code under test and keeps this file free of the coalescing forms the sentinel discipline rules
 * out — a missing entry is a fact to handle, not a blank to fill in silently.
 */
function statusTitle(status: number): string {
  const held: string | undefined = STATUS_TITLE[status];

  return typeof held === 'string' ? held : 'Error';
}

const TRACE_ID = '00-9c2e4f1b7a934dd6bb18eb211c80319c-55bd6b7169203331-01';
const CORRELATION_ID = 'b91d5c37-8a2e-4f60-91c4-3e7d0b2a6c48';

/**
 * One problem document, in the shape the API publishes.
 *
 * @param code The failure code, which travels inside `type` behind the published namespace.
 * @param status The wire status, which is what the shared summariser reads the severity from.
 * @param detail The human sentence.
 * @param errors Per-field messages, keyed by the server's own MODEL-STATE spelling.
 */
function problem(
  code: string,
  status: number,
  detail: string,
  errors?: Readonly<Record<string, readonly string[]>>,
): ProblemDetails {
  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: statusTitle(status),
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };

  return errors === undefined ? document : { ...document, errors };
}

/**
 * A problem document carrying a TRACE identifier and no correlation identifier.
 *
 * `problemSupportReference` prefers the correlation identifier and falls back to the trace one, so
 * this is the only shape that proves the trace identifier survives to the banner.
 */
function tracedProblem(code: string, status: number, detail: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: statusTitle(status),
    status,
    detail,
    traceId: TRACE_ID,
  };
}

/**
 * A problem document carrying a code and a status and NOTHING ELSE.
 *
 * Every member of the contract but `status` is optional, and the shared message resolver prefers
 * `detail`, then `title`, then the wording the STATUS itself implies. Omitting both sentences is
 * therefore the only way to reach that last fallback, which is what proves the status-derived wording.
 */
function bareProblem(code: string, status: number): ProblemDetails {
  return { type: `${FAILURE_TYPE_PREFIX}${code}`, status };
}

// ==================================================================================================
// FIXTURES
//
// ⚠ EVERY FIXTURE IS TYPED AS THE REAL MODEL INTERFACE, and that is a correctness device rather than
// tidiness. The API serialises with camel casing and `JsonIgnoreCondition.Never`, and the DTO members
// carry a single lower-case `d` — `RoleId`, `UserId`, `UserRoleId` — so the wire keys are `roleId`,
// `userId` and `userRoleId`. A leading uppercase RUN would lower-case whole, making `RoleID` into
// `roleID`; mis-spelling one that way yields `undefined` with no runtime error at all. Typing each
// fixture turns that silent hole into a compilation failure.
//
// ⚠ EVERY MEMBER IS PRESENT ON EVERY FIXTURE. Absence is never used to mean "unset", because the
// server never omits a member: `null`, `0`, `''` and `false` all survive on the wire, and each is
// DATA rather than a gap.
// ==================================================================================================

/**
 * One role.
 *
 * ⚠ THE DEFAULT IDENTIFIER IS ZERO. `dbo.Roles.RoleID` is `IDENTITY(0, 1)`
 * (`01.00.00.SqlDataProvider:L115`), so role zero is a real role — the Administrators role of an
 * installation, the single most consequential one there is. Every case below therefore exercises the
 * zero identifier by default rather than treating it as an edge.
 *
 * ⚠ THE FEES DEFAULT TO ZERO, NOT TO NULL. `RoleController.vb:L494` discriminates on
 * `ServiceFee > 0.0` and the numeric sentinel is `Single.MinValue` (`Null.vb:L51-L55`), so nought is
 * a real, free price and nothing may coerce it away.
 */
function role(roleId = 0, overrides: Partial<Role> = {}): Role {
  return {
    roleId,
    roleGroupId: null,
    roleName: 'Administrators',
    description: 'Portal Administration',
    billingFrequency: 'N',
    serviceFee: 0,
    trialFrequency: 'N',
    trialPeriod: 0,
    billingPeriod: 0,
    trialFee: 0,
    isPublic: false,
    autoAssignment: false,
    rsvpCode: null,
    iconFile: null,
    // Served with every role detail; this screen carries it nowhere, because it performs no role
    // update. Declared so the fixture is a whole `Role` rather than a near-one.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * One membership row.
 *
 * The assignment's own key is seeded `IDENTITY(1, 1)` (`01.00.00.SqlDataProvider:L239`), unlike the
 * role key beside it — which is exactly why no identifier anywhere in this file is tested for
 * truthiness or for sign.
 */
function membership(overrides: Partial<UserRole> = {}): UserRole {
  return {
    userRoleId: 11,
    userId: 42,
    username: 'ada',
    displayName: 'Ada Lovelace',
    roleId: 0,
    roleName: 'Administrators',
    effectiveDate: null,
    expiryDate: null,
    ...overrides,
  };
}

/**
 * One account the picker can offer.
 *
 * ⚠ THREE MEMBERS, AND THE SHORTNESS OF THIS BUILDER IS ITSELF THE ASSERTION. It used to construct a
 * full account-listing row — sixteen members including a postal address, a telephone number, an
 * electronic-mail address and two audit instants — because that is what the screen's reads returned.
 * They now return `UserChoice`, so a fixture cannot supply a field the screen has no business
 * receiving, and a server that started sending one would be refused by the contract decoder rather
 * than quietly retained.
 */
function account(overrides: Partial<UserChoice> = {}): UserChoice {
  return {
    userId: 42,
    username: 'ada',
    displayName: 'Ada Lovelace',
    ...overrides,
  };
}

/**
 * The tenant's account policy, of which exactly one member matters to this screen.
 *
 * Every member is present because the contract's decoder requires them all; `securityUsersControl`
 * is the one that decides which account control is rendered, and it defaults to the name box so that
 * a case saying nothing about the policy exercises the affordance the legacy help text described.
 *
 * @param overrides Members to replace.
 * @returns The policy.
 */
function membershipSettings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
  return {
    // ⚠ #5/#6 — stated rather than left to the override, so a specification that says nothing about
    // provenance still gets a policy claiming to be stored. Provenance-sensitive specifications pass
    // `isStored: false` explicitly.
    isStored: true,
    columnFirstName: true,
    columnLastName: true,
    columnDisplayName: true,
    columnAddress: true,
    columnTelephone: true,
    columnEmail: true,
    columnCreatedDate: true,
    columnLastLogin: true,
    columnAuthorized: true,
    displayMode: 0,
    displaySuppressPager: false,
    recordsPerPage: 10,
    profileDefaultVisibility: 2,
    profileDisplayVisibility: true,
    profileManageServices: false,
    redirectAfterLogin: null,
    redirectAfterRegistration: null,
    redirectAfterLogout: null,
    securityEmailValidation: '',
    securityRequireValidProfile: false,
    securityRequireValidProfileAtLogin: false,
    securityUsersControl: USERS_CONTROL_TEXT_BOX,
    securityDisplayNameFormat: '',
    ...overrides,
  };
}

/** The single-resource envelope. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A page.
 *
 * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data`. A fixture spelling it otherwise
 * flushes successfully and unwraps to no records at all, so every later assertion would be made
 * against an empty list rather than against the screen.
 */
function pageOf<T>(
  items: readonly T[],
  totalCount: number = items.length,
  pageIndex = 0,
  pageSize = 100,
): PagedResponse<T> {
  const totalPages: number = pageSize > 0 ? Math.ceil(totalCount / pageSize) : 0;

  return { items, meta: { totalCount, pageIndex, pageSize, totalPages } };
}

describe('RoleAssignmentComponent', () => {
  let fixture: ComponentFixture<RoleAssignmentComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let designatedAdministrator: WritableSignal<number | null>;
  let administratorRole: WritableSignal<number | null>;
  let registeredRole: WritableSignal<number | null>;
  let loadCurrentPortalContext: jasmine.Spy;

  beforeEach(async () => {
    /*
     * THE TENANT'S PROTECTED PAIRING, HELD IN SIGNALS THE CASES CAN MOVE.
     *
     * ⚠ THESE USED TO BE THREE COMPONENT INPUTS, AND THE CHANGE IS THE POINT. They were declared as
     * optional inputs on the reasoning that "the tenant's settings are not part of this screen's
     * contract", and NOTHING in the application ever supplied one — no route, no parent template —
     * so the removal guard shipped permanently disarmed and the server's refusal was the only thing
     * protecting the membership that makes an account part of the tenant. The facts are now read
     * from the portal store, which is CORE state every feature may inject.
     *
     * Each opens ABSENT, so the ordinary cases below describe a screen whose tenant record has not
     * arrived — which is the fail-safe direction: the command is offered and the API's refusal
     * governs, exactly the behaviour that shipped. The guard itself is proved by supplying the facts.
     */
    designatedAdministrator = signal<number | null>(null);
    administratorRole = signal<number | null>(null);
    registeredRole = signal<number | null>(null);

    /*
     * The request for those facts, spied rather than served. The real portal store would issue a
     * tenant read on arrival that every case in this file would have to answer, and the spy is the
     * sharper assertion in any event: it records which tenant was asked for, and whether it was
     * asked at all.
     */
    loadCurrentPortalContext = jasmine.createSpy('loadCurrentPortalContext');

    await TestBed.configureTestingModule({
      // The component is STANDALONE, so it is imported rather than declared. There is no
      // `declarations` array anywhere in this file and there is no module to build one in.
      imports: [RoleAssignmentComponent],
      providers: [
        // ⚠⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that
        // displaces its handler. Reversed, the testing backend is never installed and every
        // `expectOne` below fails for a reason that looks entirely unrelated to the ordering.
        provideHttpClient(),
        provideHttpClientTesting(),
        // The real router, never the deprecated testing module. An empty table is enough: the
        // screen emits links and never navigates itself.
        provideRouter([]),
        // ⚠ MANDATORY, NOT DEFENSIVE. `DateDisplayPipe` takes `@Inject(LOCALE_ID)` and formats
        // through the framework's own formatter, so without this pin every rendered date would
        // depend on the machine's locale and the sentinel cases would prove nothing portable.
        { provide: LOCALE_ID, useValue: 'en-US' },
        /*
         * The identity, doubled for ONE fact: which tenant the caller belongs to. The tenant is read
         * from the caller rather than from a route, because this screen addresses a ROLE and names no
         * portal — and must never be able to protect one tenant's membership with another's keys.
         */
        {
          provide: AuthStore,
          useValue: { currentUser: signal({ portalId: TENANT_ID }) },
        },
        /* The tenant's record, doubled to the three facts this screen reads plus the request for them. */
        {
          provide: PortalStore,
          useValue: {
            administratorUserId: designatedAdministrator,
            administratorRoleId: administratorRole,
            registeredRoleId: registeredRole,
            loadCurrentPortalContext,
          },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    // ⚠⚠ UNCONDITIONAL, AND THE MECHANISM BY WHICH A MISSING RE-READ FAILS LOUDLY. Verification is
    // what turns "the screen did not ask again" into a failure instead of a silent pass, so it is
    // never omitted and never wrapped in a condition.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------------------------------
  // HARNESS
  //
  // Signal note: the version of the framework installed here exposes `TestBed.flushEffects()` and no
  // `TestBed.tick()`, which was verified against the installed typings rather than assumed. It is not
  // reached for below, and deliberately so: every one of this component's `effect()`s is created in
  // its constructor and is therefore a COMPONENT effect, which the framework runs as part of change
  // detection. `fixture.detectChanges()` is what settles both those and the pull-based `computed()`
  // views, the polyfills are zone-based, so it behaves conventionally, and reaching for the explicit
  // flush would settle the effects at a different point in the cycle than production does.
  // -------------------------------------------------------------------------------------------------

  /** The component under test, for the few assertions that are about its state rather than its view. */
  function component(): RoleAssignmentComponent {
    return fixture.componentInstance;
  }

  /**
   * Mounts the screen.
   *
   * The role identifier is delivered as the STRING a route parameter is, so the input's own strict
   * parsing runs. It is set LAST because setting it is what starts both opening reads.
   *
   * ⚠ THE TENANT CONTEXT IS PUT IN THE STORE, NOT PASSED IN. It used to arrive as three optional
   * inputs that nothing in the application ever supplied; it is now read from the portal store, so a
   * case that wants the guard armed writes the facts into the doubled signals BEFORE the component
   * reads them — which is before the first change detection, since every consumer is a `computed`.
   */
  function create(
    roleId: string | null,
    context: {
      readonly administratorUserId?: number;
      readonly administratorRoleId?: number;
      readonly registeredRoleId?: number;

      /**
       * The account policy to answer the opening read with, or `null` to refuse it.
       *
       * Omitted means the name-box policy, which is what every case predating the policy wiring
       * asserted against.
       */
      readonly usersControl?: number | null;

      /**
       * The tenant account count to answer the probe with when the policy asks for the drop-down.
       *
       * ⚠ A STORED DROP-DOWN PREFERENCE NOW PROBES THE COUNT, and this option is how a case says
       * what the count is. The enumeration threshold is not a default the setting replaces: it is the
       * size at which enumerating a tenant to fill a `<select>` stops being sensible, and the legacy
       * framework enforced it by writing the name box back as the tenant's setting the first time the
       * count exceeded it (`UserModuleBase.vb:L178-L186`). A tenant whose stored preference predates
       * its growth therefore keeps asking for a drop-down it should no longer be offered, and the
       * walk that fills it was the one unbounded read on this screen.
       *
       * Omitted means a small tenant, so a case that says nothing about size gets the drop-down it
       * asked for. `null` refuses the probe.
       */
      readonly accountCount?: number | null;
    } = {},
  ): void {
    if (context.administratorUserId !== undefined) {
      designatedAdministrator.set(context.administratorUserId);
    }

    if (context.administratorRoleId !== undefined) {
      administratorRole.set(context.administratorRoleId);
    }

    if (context.registeredRoleId !== undefined) {
      registeredRole.set(context.registeredRoleId);
    }

    fixture = TestBed.createComponent(RoleAssignmentComponent);
    fixture.componentRef.setInput('roleId', roleId);
    fixture.detectChanges();

    // ⚠ ANSWERED FOR EVERY MOUNT, BECAUSE THE POLICY READ IS UNCONDITIONAL. The screen asks which
    // account control the tenant wants before it offers either one, so a case that left this
    // outstanding would fail the unconditional verification in `afterEach` rather than on its own
    // assertion. It is settled here, in the mount, so no case has to know the read exists.
    const policy: number | null =
      context.usersControl === undefined ? USERS_CONTROL_TEXT_BOX : context.usersControl;

    answerAccountPolicy(policy);

    // ⚠ SETTLED HERE FOR THE SAME REASON THE POLICY READ IS. A stored preference for the drop-down is
    // measured against the enumeration threshold, so the screen probes the count before it offers
    // either control — and a case that left that probe outstanding would fail the unconditional
    // verification in `afterEach` rather than on its own assertion. A stored preference for the NAME
    // BOX probes nothing, because the threshold can only move a tenant towards the name box and so
    // cannot change an answer that is already there. A REFUSED policy probes too, but the cases that
    // exercise that branch answer it themselves, because what the count says is the whole subject of
    // each of them.
    if (policy === USERS_CONTROL_COMBO) {
      answerAccountCount(context.accountCount === undefined ? SMALL_TENANT_ACCOUNT_COUNT : context.accountCount);
    }
  }

  /**
   * Answers the account-policy read, or refuses it.
   *
   * @param usersControl The policy value to answer with, or `null` to answer with a server fault.
   */
  function answerAccountPolicy(usersControl: number | null): TestRequest {
    const call = expectRequest('GET', MEMBERSHIP_SETTINGS_URL, 'the account policy read');

    if (usersControl === null) {
      call.flush(
        { type: 'about:blank', title: 'Server error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
    } else {
      call.flush(envelope(membershipSettings({ securityUsersControl: usersControl })));
    }

    fixture.detectChanges();

    return call;
  }

  /**
   * Answers the complete-account-list walk the drop-down policy triggers.
   *
   * One call per page, so a case can prove the walk followed every page the server reported rather
   * than stopping at the first — the defect this screen's lookup was corrected for.
   *
   * @param accounts The accounts this page carries.
   * @param totalCount The server's own count of the whole list.
   * @param pageIndex The page being answered.
   */
  function answerAccountChoicesPage(
    accounts: readonly UserChoice[],
    totalCount: number = accounts.length,
    pageIndex = 0,
  ): TestRequest {
    // ⚠ THE PAGE SIZE IS PART OF THE MATCH, not decoration. The count probe is also a GET of this
    // address with no filter and page index zero, so a matcher that ignored the size would consume
    // whichever request happened to be pending and the two would be indistinguishable.
    const call = httpMock.expectOne(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url === USERS_CHOICES_URL &&
        candidate.params.get('query') === null &&
        candidate.params.get('pageSize') === LOOKUP_PAGE_SIZE &&
        candidate.params.get('pageIndex') === String(pageIndex),
      `the account list, page ${pageIndex}`,
    );

    call.flush(pageOf(accounts, totalCount, pageIndex, Number(LOOKUP_PAGE_SIZE)));
    fixture.detectChanges();

    return call;
  }

  /**
   * Answers the account-count probe the fallback rule issues, or refuses it.
   *
   * Matched on its page size, which is what distinguishes it from the drop-down walk's first page.
   *
   * @param totalCount The count to report, or `null` to refuse the probe.
   */
  function answerAccountCount(totalCount: number | null): TestRequest {
    const call = httpMock.expectOne(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url === USERS_CHOICES_URL &&
        candidate.params.get('pageSize') === ACCOUNT_COUNT_PROBE_PAGE_SIZE,
      'the account-count probe',
    );

    if (totalCount === null) {
      call.flush(
        { type: 'about:blank', title: 'Server error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
    } else {
      // One record is what the probe asked for, so one is what a real server would answer with -
      // and the case must not be able to pass because the probe read a page it never requested.
      call.flush(pageOf(totalCount > 0 ? [account()] : [], totalCount, 0, 1));
    }

    fixture.detectChanges();

    return call;
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      typeof description === 'string' ? description : `${method} ${url}`,
    );
  }

  /**
   * The terms a lookup transmitted, read from its query parameters.
   *
   * ⚠ THIS USED TO READ A REQUEST BODY, and the change of location is the change of endpoint. The
   * lookup once posted to the account listing's search, because that address's filters — an
   * electronic-mail address, an arbitrary tenant-defined profile value — identify a person and must
   * not reach a request target (CWE-598). It now asks the account PICKER, whose single filter is a
   * prefix of the login name: a value the response returns in full, so a target that records it
   * discloses nothing the body did not already carry. What the move bought is that the response no
   * longer carries a postal address, a telephone number, an electronic-mail address, two audit
   * instants and four status flags for every candidate.
   *
   * A parameter that was never sent reads as `null`, which is what the absence assertions below
   * expect; nothing is coerced to the empty string, because an absent filter and a blank one are
   * different requests.
   *
   * @param request The lookup whose parameters to read.
   * @returns The transmitted parameters, absent ones reading as `null`.
   */
  function lookupTerms(request: TestRequest): Readonly<Record<string, string | null>> {
    const params = request.request.params;

    return {
      query: params.get('query'),
      pageIndex: params.get('pageIndex'),
      pageSize: params.get('pageSize'),
      sortBy: params.get('sortBy'),
      sortDir: params.get('sortDir'),
    };
  }

  /** Answers the role read. */
  function answerRole(subject: Role): TestRequest {
    const call = expectRequest('GET', roleUrl(subject.roleId), 'the role read');

    call.flush(envelope(subject));
    fixture.detectChanges();

    return call;
  }

  /**
   * Answers the membership PAGE read.
   *
   * ⚠ NARROWED BY THE ABSENCE OF `query` AS WELL AS BY THE ADDRESS, and the redundancy is deliberate.
   * The keyed membership probe now addresses the PAIRING — `…/users/{userId}` — so the two reads no
   * longer share a URL and the address alone tells them apart. The parameter test is retained as a
   * second, independent guard: it is what would fail loudly if the probe were ever moved back onto
   * the listing and narrowed by a login name, which is the CWE-598 shape this suite exists to keep
   * out. The page read itself carries no free-text filter.
   */
  function answerMemberships(
    roleId: number,
    rows: readonly UserRole[],
    totalCount: number = rows.length,
  ): TestRequest {
    const call = httpMock.expectOne(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url === membersUrl(roleId) &&
        candidate.params.get('query') === null,
      'the membership read',
    );

    // ⚠ THE ANSWER ECHOES THE COORDINATE THAT WAS ASKED FOR, as the API does: the applied size and the
    // index are facts about the response, and the screen binds the SERVER's figures to its pager. A
    // fixture that answered with a size nobody requested would make the pager compute a page count the
    // server never published, and the pager cases would then prove nothing about production.
    const askedIndex: number = Number(call.request.params.get('pageIndex') ?? '0');
    const askedSize: number = Number(call.request.params.get('pageSize') ?? String(DEFAULT_PAGE_SIZE));

    call.flush(pageOf(rows, totalCount, askedIndex, askedSize));
    fixture.detectChanges();

    return call;
  }

  /**
   * Proves a settled write re-asks the pairing endpoint NOTHING.
   *
   * ⚠ THIS HELPER USED TO ANSWER A REQUEST; IT NOW ASSERTS THAT NO REQUEST IS MADE. A write is exactly
   * what can change whether the chosen account holds the role, and the fact used to be re-asked with a
   * keyed probe once the write settled. That probe answered `404` for every account that held nothing —
   * a refusal used as a question, which reached the browser console as an error on an ordinary,
   * successful path. The listing is re-read on the same settle (the legacy rebind at
   * `SecurityRoles.ascx.vb:L546`), and those rows carry the very membership the probe was asking
   * about, so the second request bought nothing and cost a logged failure.
   *
   * Every case that uses this therefore proves two things at once: that the pairing endpoint is left
   * alone, and — through the assertions beside it — that the label and the boxes still settle to the
   * right values from the re-read rows.
   */
  function expectNoReprobe(): void {
    expect(httpMock.match(isMembershipProbe))
      .withContext('a settled write asks the pairing endpoint nothing at all')
      .toHaveSize(0);
  }

  /**
   * Whether an open request is the keyed membership probe.
   *
   * ⚠ MATCHED ON THE PAIRING ADDRESS, WHICH IS THE WHOLE POINT OF THE PROBE'S SHAPE. It used to be
   * matched on the listing address plus a `query` parameter, because the probe WAS the listing
   * narrowed by the account's login name — and the server matches that filter against the login name
   * and the display name, so the name had to be in the request target for the question to be
   * answerable. A request target is kept in browser history and written in full to every proxy and
   * server access log, none of which is on the wire: CWE-598. Two opaque identifiers in a path
   * disclose nothing.
   *
   * @param candidate An open request.
   * @returns True when it addresses one account's membership of one role.
   */
  function isMembershipProbe(candidate: HttpRequest<unknown>): boolean {
    return candidate.method === 'GET' && MEMBERSHIP_PROBE_PATH.test(candidate.url);
  }

  /**
   * Settles one keyed probe with the membership it finds, or with the refusal that means none.
   *
   * ⚠ NO MEMBERSHIP IS A `404`, NOT AN EMPTY SUCCESS. The endpoint answers `200` with the row or
   * `404` when the account holds nothing, and the store reads that refusal as the negative answer
   * without recording a failure. Answering an empty success here instead would specify a response
   * the server cannot send.
   *
   * @param probe The open probe.
   * @param held One membership when the pairing holds one, or empty when it holds nothing.
   */
  function answerProbe(probe: TestRequest, held: readonly UserRole[]): void {
    expect(held.length)
      .withContext('the probe answers about ONE pairing, so it finds at most one row')
      .toBeLessThan(2);

    if (held.length === 0) {
      probe.flush(
        {
          type: 'urn:dnnmigration:error:role_assignment.not_found',
          title: 'Not Found',
          status: 404,
          detail: 'The account holds no such membership.',
        },
        { status: 404, statusText: 'Not Found' },
      );

      return;
    }

    probe.flush(envelope(held[0]));
  }


  /**
   * Mounts the screen and settles both of its opening reads.
   *
   * ⚠ BOTH READS ARE ISSUED TOGETHER BY THE ROLE INPUT, so they are outstanding at the same time
   * and are answered in whichever order this helper chooses — itself worth stating, because a screen
   * that depended on one arriving before the other would be order-sensitive in a way no browser
   * guarantees.
   */
  function arrive(
    roleId = 0,
    rows: readonly UserRole[] = [membership()],
    context: {
      readonly administratorUserId?: number;
      readonly administratorRoleId?: number;
      readonly registeredRoleId?: number;
    } = {},
    totalCount: number = rows.length,
  ): void {
    create(String(roleId), context);
    answerRole(role(roleId));
    answerMemberships(roleId, rows, totalCount);
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /**
   * The text of one node, narrowed EXPLICITLY rather than coalesced.
   *
   * `Node.textContent` is typed `string | null` because a document or a doctype node has none while an
   * element always does. The narrowing is written as a test rather than with an absent-value shorthand,
   * matching the house style of the code under test; the other way of silencing the type — a non-null
   * assertion — is ruled out outright.
   */
  function textIn(node: Element | null | undefined): string {
    if (node === null || node === undefined) {
      return '';
    }

    const held: string | null = node.textContent;

    return typeof held === 'string' ? held : '';
  }

  /** One attribute of one element, narrowed on the same terms as {@link textIn}. */
  function attributeIn(element: Element | null | undefined, name: string): string {
    if (element === null || element === undefined) {
      return '';
    }

    const held: string | null = element.getAttribute(name);

    return typeof held === 'string' ? held : '';
  }

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => textIn(node).trim());
  }

  /** A button by its rendered wording, anywhere on the screen. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => textIn(candidate).trim() === label,
    );
  }

  /** Presses a button by its rendered wording. */
  function press(label: string): void {
    const control = button(label);

    expect(control).withContext(`the "${label}" control is offered`).not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /**
   * Presses a button of the OPEN CONFIRMATION.
   *
   * ⚠ SCOPED TO THE DIALOGUE ON PURPOSE. The row command and the dialogue's confirming button share
   * the wording 'Delete', and the abandon link and the dialogue's dismissing button share 'Cancel',
   * so an unscoped lookup by wording would press the wrong one and the case would prove something
   * other than what it claims.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => textIn(candidate).trim().includes(label));

    expect(control)
      .withContext(`the "${label}" button of the confirmation is offered`)
      .not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /**
   * Searches for an account through the shared lookup.
   *
   * The lookup debounces its own typing, so the immediate path is used here: its own submit gesture
   * emits at once. That keeps every case free of a fake clock while still going through the real
   * control rather than calling the handler behind it.
   */
  function lookUp(term: string): void {
    const field = query<HTMLInputElement>('input[type="search"]');

    expect(field).withContext('the account lookup is rendered').not.toBeNull();

    (field as HTMLInputElement).value = term;
    (field as HTMLInputElement).dispatchEvent(new Event('input'));
    fixture.detectChanges();

    (field as HTMLInputElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();
  }

  /**
   * Claims the one outstanding account LOOKUP, distinguished from the picker's other two reads.
   *
   * ⚠ THE FILTER IS WHAT IDENTIFIES IT. All three account reads on this screen are a `GET` of the
   * same address: the count probe (one row, no filter), the complete-list walk (a full page, no
   * filter) and this lookup (a full page WITH a filter). Matching on the presence of the filter is
   * therefore the precise test, and a matcher that named only the method and the address would
   * consume whichever of the three happened to be pending.
   *
   * @param description Named in the failure message when nothing matches.
   * @returns The outstanding lookup.
   */
  function expectLookupRequest(description: string): TestRequest {
    return httpMock.expectOne(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url === USERS_CHOICES_URL &&
        candidate.params.get('query') !== null,
      description,
    );
  }

  /**
   * Answers the account lookup with a single, complete page.
   *
   * The reported total equals what is supplied, which is what tells the walk it has seen the whole
   * match set and may stop. A case that wants to prove the walk FOLLOWS pages uses
   * {@link answerLookupPage} instead and reports a larger total.
   */
  function answerLookup(matches: readonly UserChoice[]): TestRequest {
    const call = expectLookupRequest('the account lookup');

    call.flush(pageOf(matches, matches.length, 0, Number(LOOKUP_PAGE_SIZE)));
    fixture.detectChanges();

    return call;
  }

  /**
   * Answers ONE page of the account-lookup walk, identified by the page coordinate it asked for.
   *
   * Matched on `pageIndex` rather than by consuming whatever is outstanding, so a case proves the
   * walk asked for the page it claims to have asked for. The `userName` test is what keeps this from
   * matching the complete-account-list walk, which carries no name.
   *
   * @param matches The accounts this page carries.
   * @param totalCount The server's own count of the whole match set.
   * @param pageIndex The page being answered.
   */
  function answerLookupPage(
    matches: readonly UserChoice[],
    totalCount: number,
    pageIndex: number,
  ): TestRequest {
    // ⚠ THE FILTER IS WHAT KEEPS THIS FROM MATCHING THE COMPLETE-LIST WALK, which asks the same
    // address for the same page size and carries none. Both the filter and the page coordinate are
    // read out of the query parameters: this address takes one filter, a prefix of the login name,
    // which is a value it returns in the response body anyway — unlike the account listing's search,
    // whose electronic-mail and profile-value filters identify a person and therefore travel in a
    // body (CWE-598).
    const call = httpMock.expectOne(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url === USERS_CHOICES_URL &&
        candidate.params.get('query') !== null &&
        candidate.params.get('pageIndex') === String(pageIndex),
      `the account lookup, page ${pageIndex}`,
    );

    call.flush(pageOf(matches, totalCount, pageIndex, Number(LOOKUP_PAGE_SIZE)));
    fixture.detectChanges();

    return call;
  }

  /**
   * A page's worth of accounts sharing one prefix, none of which is the name being searched for.
   *
   * @param prefix The shared prefix.
   * @param count How many to make.
   * @param firstId The identifier of the first, so successive pages do not collide.
   * @returns The accounts.
   */
  function accountRun(prefix: string, count: number, firstId: number): readonly UserChoice[] {
    return Array.from({ length: count }, (_unused, offset) =>
      account({
        userId: firstId + offset,
        username: `${prefix}${String(firstId + offset)}`,
        displayName: `${prefix} ${String(firstId + offset)}`,
      }),
    );
  }

  /**
   * Chooses an offered account by its rendered wording, and releases it when pressed again.
   *
   * ⚠ THE SELECTOR NAMES THE BUTTON, NOT ITS CONTAINER. Each offer is a real toggle button inside a
   * list item, and a document-ordered query that also admitted the item would return the ITEM first —
   * whose text contains the same wording — so the case would click a non-interactive element, nothing
   * would happen, and every assertion afterwards would fail against an unmade choice.
   *
   * @param label The rendered wording of the offer.
   * @param held The membership rows the keyed probe finds for that account's login name.
   */
  function chooseAccount(label: string, held: readonly UserRole[] = []): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      'button.role-assignment__match-action',
    ).find((candidate) => textIn(candidate).trim().includes(label));

    expect(control).withContext(`the account "${label}" is offered`).not.toBeUndefined();

  (control as HTMLButtonElement).click();
  fixture.detectChanges();

  // ⚠ CHOOSING AN ACCOUNT ASKS AT MOST ONE QUESTION, AND USUALLY NONE. "Does this person already hold
  // the role?" is answered from the rendered rows when they carry the pairing, and from the listing's
  // own published total when every row it counts is on screen. Only when neither settles it - a
  // membership that could sit on a page nobody has read - is one keyed probe issued, addressed at the
  // PAIRING so that no login name reaches a request target. Releasing an account asks nothing.
  const probes: readonly TestRequest[] = httpMock.match(isMembershipProbe);

  if (probes.length === 0) {
    // ⚠ NO REQUEST AT ALL IS NOW THE ORDINARY PATH. The rendered rows answer "does this person already
    // hold the role?" whenever they carry the pairing, and a listing whose own published total is
    // fully rendered proves the negative — so the probe is reserved for the one case neither can
    // settle, a membership that could be on a page nobody has read. Releasing an account likewise asks
    // nothing. What the caller declared is asserted against what the screen RESOLVED, so a case cannot
    // pass a membership that never reached the component.
    const resolved: UserRole | null = component().selectedMembership();

    if (held.length === 0) {
      expect(resolved)
        .withContext('nothing chosen, or nothing held, resolves to no membership')
        .toBeNull();

      return;
    }

    expect(resolved?.userRoleId)
      .withContext('the rows on screen answered, so nothing had to be asked')
      .toBe(held[0]?.userRoleId);

    return;
  }

  expect(probes).withContext('choosing an account issues exactly one keyed probe').toHaveSize(1);
  // BOUNDED AND IDENTIFYING NOBODY, asserted here so every case that chooses an account proves it:
  // one request, no query parameter at all, and never a walk.
  expect(probes[0].request.params.keys()).toEqual([]);
  expect(probes[0].request.urlWithParams).not.toContain('ada');

  // `held` states whether the pairing holds a membership.
  answerProbe(probes[0], held);
  fixture.detectChanges();
  }

  /** Whether the account is currently held, read off the toggle's own pressed state. */
  function accountIsChosen(label: string): boolean {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      'button.role-assignment__match-action',
    ).find((candidate) => textIn(candidate).trim().includes(label));

    return control?.getAttribute('aria-pressed') === 'true';
  }

  /** Mounts the screen and chooses one account through the real lookup. */
  function arriveAndChoose(
    roleId = 0,
    rows: readonly UserRole[] = [],
    held: readonly UserRole[] = [],
  ): void {
    arrive(roleId, rows);
    lookUp('ada');
    answerLookup([account()]);
    chooseAccount('Ada Lovelace', held);
  }

  /** Types into a native date control. */
  function typeDate(controlId: string, value: string): void {
    const control = query<HTMLInputElement>(`#${controlId}`);

    expect(control).withContext(`#${controlId} is rendered`).not.toBeNull();

    (control as HTMLInputElement).value = value;
    (control as HTMLInputElement).dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Leaves a date field, which is what marks it VISITED.
   *
   * The class gates each message the way `display="Dynamic"` gated it — nothing is said until the
   * field has been visited and is failing — so a case asserting a rendered message has to leave the
   * field, exactly as a person does. Typing alone marks nothing.
   */
  function leaveDate(controlId: string): void {
    const control = query<HTMLInputElement>(`#${controlId}`);

    expect(control).withContext(`#${controlId} is rendered`).not.toBeNull();

    (control as HTMLInputElement).dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  /**
   * Puts a value the NATIVE DATE CONTROL WILL NOT HOLD into the form model.
   *
   * ⚠ WHY THIS DOES NOT GO THROUGH THE INPUT. A native date control applies the platform's own
   * value-sanitising algorithm on assignment, so a value that is not a valid calendar date — including
   * a well-formed but impossible one such as the thirty-first of February — is discarded and the
   * control is left empty. The data-type check the legacy screen declared at `securityroles.ascx:L45`
   * and `:L46` therefore cannot be reached through the rendered control at all, which is itself an
   * improvement worth having. It remains a genuine second line of defence for a value arriving from
   * anywhere else, so it is exercised through the form model, which is the same object the control
   * writes to.
   *
   * @param field Which bound to write.
   * @param value The value to write.
   * @param markVisited Whether to mark the field visited, which is what unlocks its message.
   */
  function forceDate(
    field: 'effectiveDate' | 'expiryDate',
    value: string,
    markVisited = true,
  ): void {
    const control = component().form.controls[field];

    control.setValue(value);

    if (markVisited) {
      control.markAsTouched();
    }

    fixture.detectChanges();
  }

  /** The membership rows painted by the shared grid. */
  function rows(): readonly HTMLTableRowElement[] {
    return queryAll<HTMLTableRowElement>('tr.data-table__row');
  }

  /** The cells of one painted row, in document order. */
  function cellsOf(row: HTMLTableRowElement): readonly string[] {
    return Array.from(row.querySelectorAll('td,th')).map((cell) => textIn(cell).trim());
  }

  /**
   * The messages currently rendered by the shared field wrapper, across every field.
   *
   * ⚠ RETURNED UNTRIMMED, ON PURPOSE. The wrapper emits each message as a one-line paragraph, so the
   * raw text is exactly what the wrapper produced — which is the only form in which the leading-space
   * hazard described on {@link INVALID_EFFECTIVE_DATE_MESSAGE} can fail rather than hide. Trimming
   * here would silently accept `' Invalid effective date'`.
   */
  function fieldErrors(): readonly string[] {
    return queryAll<Element>('.form-field__error').map((node) => textIn(node));
  }

  /** The support reference each announcement quoted, newest last, `null` where none was quoted. */
  function announcementReferences(): readonly (string | null)[] {
    return notifySpy.calls
      .allArgs()
      .map((args) => (typeof args[2] === 'string' && args[2].length > 0 ? args[2] : null));
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // -------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVING, AND THE ZERO IDENTIFIER
  // -------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    /**
     * ⚠ THE SPECIFICATION THAT CATCHES EVERY TRUTHINESS BUG ON THIS SCREEN.
     *
     * `dbo.Roles.RoleID` is `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L115`) and a Registered Users
     * role is seeded during installation, so ZERO IS A REAL ROLE and the screen must load normally for
     * it. A truthiness test on the identifier, a negation of it, a comparison of it against nought in
     * either direction, and a coalescing default onto the legacy integer sentinel ALL pass a case
     * written against role seven and all fail this one. That is what makes it worth writing.
     */
    it('reads role ZERO as a real role, from relative addresses', () => {
      create('0');

      const roleRead = expectRequest('GET', roleUrl(0), 'the role read');
      const listRead = httpMock.expectOne(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === membersUrl(0) &&
          candidate.params.get('query') === null,
        'the membership read',
      );

      expect(roleRead.request.url).toBe('/api/v1/roles/0');
      expect(listRead.request.url).toBe('/api/v1/roles/0/users');
      // The route parameter arrived as the string '0' and was parsed, not tested for truthiness.
      expect(component().resolvedRoleId()).toBe(0);
      // The paging coordinate is zero-based on the wire and is transmitted rather than adjusted.
      expect(listRead.request.params.get('pageIndex')).toBe('0');
      // ONE PAGE, at the size every other listing in the workspace opens at. The widest legal page
      // would be a window too, one order of magnitude further out, and asking for it here is what an
      // earlier revision did before following every further page the metadata reported.
      expect(listRead.request.params.get('pageSize')).toBe(String(DEFAULT_PAGE_SIZE));
      // The page read carries no free-text filter; only the keyed probe does.
      expect(listRead.request.params.has('query')).toBeFalse();

      roleRead.flush(envelope(role(0)));
      listRead.flush(pageOf([membership()]));
      fixture.detectChanges();

      expect(rows()).toHaveSize(1);
    });

    /**
     * ⚠ A COMPANION TO THE ABOVE, ON THE ROW'S OWN IDENTIFIERS. `UserRoleID` is seeded
     * `IDENTITY(1, 1)` and `UserID` likewise, but neither is guaranteed positive by anything this
     * screen can see, so a row carrying nought for either must be retained and rendered rather than
     * treated as absent.
     */
    it('retains a membership whose row and account identifiers are both ZERO', () => {
      arrive(0, [membership({ userRoleId: 0, userId: 0 })]);

      expect(rows()).withContext('the row survives').toHaveSize(1);
      expect(component().assignments()[0]?.userRoleId).toBe(0);
      expect(component().assignments()[0]?.userId).toBe(0);
      // The account link is built from the identifier as it stands.
      expect(query<HTMLAnchorElement>('tr.data-table__row a')?.getAttribute('href')).toBe(
        '/users/0',
      );

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      // And the removal addresses the pairing with both zeroes intact.
      const call = expectRequest('DELETE', memberUrl(0, 0), 'the removal');

      expect(call.request.url).toBe('/api/v1/roles/0/users/0');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerMemberships(0, []);
    });

    it('names the screen from the role it read, and falls back before it arrives', () => {
      create('0');

      // `RoleTitle.Text` needs the role's name, which has not arrived yet, so the heading is never
      // blank in the meantime.
      expect(textIn(query('h1')).trim()).toBe(TITLE_FALLBACK);

      answerRole(role(0, { roleName: 'Subscribers' }));
      answerMemberships(0, []);

      // `SecurityRoles.ascx.vb:L193` formatted the template with the role's name and identifier; the
      // template itself uses only the name, so only the name is substituted.
      expect(textIn(query('h1')).trim()).toBe('Manage Users in Role: Subscribers');
    });

    it('names the grid for a reader through the shared caption slot', () => {
      arrive();

      // `ControlTitle_user roles.Text` — a legacy resource key that genuinely contains a space.
      expect(textIn(query('caption')).trim()).toBe(CAPTION);
    });

    it('reads nothing at all when the address names no role, and says so', () => {
      create(null);

      // Absence is `null`, never minus one. `SecurityRoles.ascx.vb:L51-L53` initialised its three
      // identifiers to minus one and `:L413-L419` overwrote them from the query string, so minus one
      // was that screen's "not supplied" marker — while the tenant table is seeded `IDENTITY(-1, 1)`,
      // which makes minus one a legitimate tenant. The two meanings are kept apart here.
      expect(component().resolvedRoleId()).toBeNull();
      expect(component().roleUnresolved()).toBeTrue();
      expect(textIn(query('.role-assignment__unresolved')).trim()).toBe(
        ROLE_UNRESOLVED,
      );
      httpMock.expectNone(() => true);
    });

    // ⚠ THERE IS DELIBERATELY NO PAGED-READ CASE HERE, AND ITS ABSENCE IS THE POINT.
    //
    // A case once stood here asserting that ONE page is read and a shared pager reaches the rest. This
    // screen does not do that. `securityroles.ascx:L56` declares no `AllowPaging`, no pager style and
    // no footer style, so the legacy grid was UNPAGED, and the membership is read WHOLE through the
    // role store: the widest page first, then every further page the server reports. Nothing here
    // declares a page index, a page size, a total or a page-change handler, so there is no pager to
    // mount and no second page for the operator to reach for.
    //
    // The whole read itself is proved in the store-delegation suite at the foot of this file - "reads
    // the whole membership, following every page the server reports" - which is the right place for
    // it, because the store is what follows the pages. The parity guard below proves the visible half:
    // no pager is rendered.

    it('discards everything and re-reads when the address names another role', () => {
      arrive(0, [membership()]);

      fixture.componentRef.setInput('roleId', '7');
      fixture.detectChanges();

      // Nothing of the previous role survives the change, so a late answer cannot repopulate it.
      expect(component().assignments()).toHaveSize(0);
      expect(component().role()).toBeNull();

      answerRole(role(7, { roleName: 'Subscribers' }));
      answerMemberships(7, [membership({ roleId: 7, roleName: 'Subscribers' })]);

      expect(rows()).toHaveSize(1);
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 2 — THE THREE DATE VALIDATORS, EXACTLY AS DECLARED
  //
  // `securityroles.ascx` declares three comparison validators and no others: `valEffectiveDate` at
  // `:L45`, `valExpiryDate` at `:L46` and `valDates` at `:L47`. ALL THREE CARRY `type="Date"`, so the
  // typed-comparison defect that afflicts the sibling role editor's validators does not exist on this
  // screen and is deliberately not asserted here.
  // -------------------------------------------------------------------------------------------------

  describe('the effective-bound data-type check', () => {
    it('rejects a value that is not a calendar date, with the resource wording', () => {
      arriveAndChoose();

      // FIRST LINE OF DEFENCE: the native control will not even hold it. The platform's
      // value-sanitising algorithm discards anything that is not a valid calendar date, and the
      // thirty-first of February is well-formed but impossible.
      typeDate(EFFECTIVE_DATE_CONTROL_ID, IMPOSSIBLE_DATE);

      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value)
        .withContext('the native control refuses a non-date outright')
        .toBe('');

      // SECOND LINE OF DEFENCE: the validator itself, reached through the model the control writes to,
      // which is where a value arriving from anywhere else would land.
      forceDate('effectiveDate', IMPOSSIBLE_DATE);

      expect(component().form.controls.effectiveDate.hasError('invalidDate')).toBeTrue();
      expect(component().form.controls.effectiveDate.valid).toBeFalse();
      // ⚠ EXACT, AND UNTRIMMED. `valEffectiveDate.Text` is a break tag followed by a SPACE, so a
      // stripper that removed only the tag would leave ' Invalid effective date' here.
      expect(fieldErrors()).toEqual([INVALID_EFFECTIVE_DATE_MESSAGE]);
      expect(button(ADD_USER_LABEL)?.disabled).withContext('and nothing may be written').toBeTrue();
    });

    it('says nothing until the field has been visited, the way Display="Dynamic" gated it', () => {
      arriveAndChoose();

      forceDate('effectiveDate', IMPOSSIBLE_DATE, false);

      expect(component().form.controls.effectiveDate.valid).withContext('failing').toBeFalse();
      expect(fieldErrors()).withContext('but silent until visited').toEqual([]);
    });
  });

  describe('the expiry-bound data-type check', () => {
    it('rejects a value that is not a calendar date, with its own resource wording', () => {
      arriveAndChoose();

      forceDate('expiryDate', IMPOSSIBLE_DATE);

      expect(component().form.controls.expiryDate.hasError('invalidDate')).toBeTrue();
      // ⚠ NO LEADING SPACE ON THIS ONE. `valExpiryDate.Text` is a bare break tag, unlike its sibling,
      // and the pair is what makes the wrapper's whitespace-consuming strip observable.
      expect(fieldErrors()).toEqual([INVALID_EXPIRY_DATE_MESSAGE]);
    });
  });

  describe('the ordering rule between the two bounds', () => {
    /**
     * ⚠ THE RULE ITSELF, ASSERTED ON THE MODEL, WHERE NO GESTURE ORDER CAN REACH IT.
     *
     * The three renderings below depend on the class's mirrored form snapshot, which has an ordering
     * sensitivity worth keeping out of the load-bearing assertion — see the note on the next case. This
     * one drives the controls directly, so the operator `GreaterThan` is proved for all three
     * relationships regardless of how the values got there.
     */
    it('holds the rule on the model however the two bounds arrive', () => {
      arriveAndChoose();

      const controls = component().form.controls;

      controls.effectiveDate.setValue(EFFECTIVE_DATE);
      controls.expiryDate.setValue(EARLIER_EXPIRY_DATE);
      fixture.detectChanges();

      expect(component().form.hasError('expiryNotAfterEffective'))
        .withContext('an earlier expiry')
        .toBeTrue();

      // The equal case, reached by writing the LATER value first, so neither order is privileged.
      controls.expiryDate.setValue(EQUAL_DATE);
      controls.effectiveDate.setValue(EQUAL_DATE);
      fixture.detectChanges();

      expect(component().form.hasError('expiryNotAfterEffective'))
        .withContext('equal bounds')
        .toBeTrue();

      controls.expiryDate.setValue(EXPIRY_DATE);
      controls.effectiveDate.setValue(EFFECTIVE_DATE);
      fixture.detectChanges();

      expect(component().form.hasError('expiryNotAfterEffective'))
        .withContext('a later expiry')
        .toBeFalse();
      expect(component().form.valid).toBeTrue();
    });

    /**
     * ⚠ THE ORDER OF THE GESTURES BELOW IS DELIBERATE, AND THE REASON IS RECORDED RATHER THAN HIDDEN.
     *
     * The class mirrors the form into a snapshot fed by the GROUP'S event stream, and a group emits a
     * touched change only on its FIRST transition: `markAsTouched` computes
     * `const changed = this.touched === false` BEFORE propagating to the parent, so the SECOND field to
     * be visited produces no group-level event at all and the snapshot does not catch up. A case that
     * visited both bounds last would therefore assert against a stale snapshot and fail for a reason
     * that has nothing to do with the rule under test.
     *
     * Both bounds are visited FIRST and filled in afterwards — an ordinary way to fill a two-field
     * form, and one that leaves the snapshot current because the trailing value change refreshes it.
     * This is reported as a fragility of the class, not worked around silently; the rule itself is
     * asserted above, where no gesture order can affect it.
     */
    it('refuses an expiry EARLIER than the effective bound, with the resource wording', () => {
      arriveAndChoose();

      leaveDate(EFFECTIVE_DATE_CONTROL_ID);
      leaveDate(EXPIRY_DATE_CONTROL_ID);
      typeDate(EFFECTIVE_DATE_CONTROL_ID, EFFECTIVE_DATE);
      typeDate(EXPIRY_DATE_CONTROL_ID, EARLIER_EXPIRY_DATE);

      expect(component().form.controls.effectiveDate.touched).toBeTrue();
      expect(component().form.controls.expiryDate.touched).toBeTrue();
      expect(component().form.hasError('expiryNotAfterEffective')).toBeTrue();
      expect(component().form.valid).toBeFalse();
      // The rule surfaces on the EXPIRY field because `securityroles.ascx:L47` declared
      // `controltovalidate="txtExpiryDate"`, not on the effective one it compares against.
      expect(fieldErrors()).toEqual([DATES_OUT_OF_ORDER_MESSAGE]);
      expect(button(ADD_USER_LABEL)?.disabled).toBeTrue();
    });

    /**
     * ⚠⚠ THE SINGLE HIGHEST-VALUE ASSERTION IN THIS FILE.
     *
     * `securityroles.ascx:L47` declares `operator="GreaterThan"` and NOT `GreaterThanEqual`, so an
     * expiry equal to the effective bound is INVALID — a window of zero length is not a window. A
     * naive `>=` implementation passes every other case in this file and fails only this one.
     */
    it('refuses an expiry EQUAL to the effective bound, because the operator is GreaterThan', () => {
      arriveAndChoose();

      // Both bounds visited first, then filled in — see the note on the previous case.
      leaveDate(EFFECTIVE_DATE_CONTROL_ID);
      leaveDate(EXPIRY_DATE_CONTROL_ID);
      typeDate(EFFECTIVE_DATE_CONTROL_ID, EQUAL_DATE);
      typeDate(EXPIRY_DATE_CONTROL_ID, EQUAL_DATE);

      expect(component().form.controls.effectiveDate.value).toBe(EQUAL_DATE);
      expect(component().form.controls.expiryDate.value).toBe(EQUAL_DATE);
      expect(component().form.hasError('expiryNotAfterEffective'))
        .withContext('equal bounds are INVALID')
        .toBeTrue();
      expect(component().form.valid).toBeFalse();
      expect(fieldErrors()).toEqual([DATES_OUT_OF_ORDER_MESSAGE]);
      expect(button(ADD_USER_LABEL)?.disabled).toBeTrue();
    });

    it('accepts an expiry strictly LATER than the effective bound', () => {
      arriveAndChoose();

      typeDate(EFFECTIVE_DATE_CONTROL_ID, EFFECTIVE_DATE);
      leaveDate(EFFECTIVE_DATE_CONTROL_ID);
      typeDate(EXPIRY_DATE_CONTROL_ID, EXPIRY_DATE);
      leaveDate(EXPIRY_DATE_CONTROL_ID);

      expect(component().form.hasError('expiryNotAfterEffective')).toBeFalse();
      expect(component().form.valid).toBeTrue();
      expect(fieldErrors()).toEqual([]);

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0), 'the assignment');

      // The bounds travel as the bare calendar values that were typed — no time part and no zone, so
      // no conversion can move the stored day.
      expect(call.request.body).toEqual({
        userId: 42,
        effectiveDate: EFFECTIVE_DATE,
        expiryDate: EXPIRY_DATE,
        notifyUser: false,
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      // ⚠ THE RE-READ ANSWERS WITH WHAT WAS STORED, which for a completed write is what was typed. It
      // is answered that way deliberately: the boxes are re-baselined from these rows, so a fixture that
      // replied with a membership holding no bounds at all would be asserting against an answer the
      // server could not have given for the write that just succeeded.
      answerMemberships(0, [
        membership({
          effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z`,
          expiryDate: `${EXPIRY_DATE}T00:00:00Z`,
        }),
      ]);
      expectNoReprobe();

      // So the bounds on screen are the bounds that were written - now standing as the STORED values
      // the amend command would carry, rather than as unsaved entry.
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe(EFFECTIVE_DATE);
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe(EXPIRY_DATE);
    });

    it('SKIPS when only the effective bound is given', () => {
      arriveAndChoose();

      typeDate(EFFECTIVE_DATE_CONTROL_ID, EFFECTIVE_DATE);
      leaveDate(EFFECTIVE_DATE_CONTROL_ID);
      leaveDate(EXPIRY_DATE_CONTROL_ID);

      // An ASP.NET comparison validator PASSES on an empty field, so a rule about the pair has
      // nothing to say until both halves exist.
      expect(component().form.hasError('expiryNotAfterEffective')).toBeFalse();
      expect(component().form.valid).toBeTrue();
      expect(fieldErrors()).toEqual([]);
    });

    it('SKIPS when only the expiry bound is given', () => {
      arriveAndChoose();

      typeDate(EXPIRY_DATE_CONTROL_ID, EXPIRY_DATE);
      leaveDate(EXPIRY_DATE_CONTROL_ID);
      leaveDate(EFFECTIVE_DATE_CONTROL_ID);

      expect(component().form.hasError('expiryNotAfterEffective')).toBeFalse();
      expect(component().form.valid).toBeTrue();
      expect(fieldErrors()).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 3 — WHAT IS *NOT* REQUIRED, AND WHY THAT IS SETTLED BY THE SOURCE
  // -------------------------------------------------------------------------------------------------

  describe('the optional fields', () => {
    /**
     * Both help strings say so in as many words. `plEffectiveDate.Help` reads "…( Optional ). Entering
     * No Value Will Indicate that the role will start immediately." and `plExpiryDate.Help` reads
     * "…( Optional ). Entering No Value Will Indicate No Expiry Date."
     *
     * This case exists to stop a future editor adding a required validator with no legacy counterpart,
     * which would change which messages the screen shows and break functional parity.
     */
    it('accepts BOTH bounds empty, and writes with neither', () => {
      arriveAndChoose();

      const controls = component().form.controls;

      expect(controls.effectiveDate.value).toBe('');
      expect(controls.expiryDate.value).toBe('');
      expect(controls.effectiveDate.valid).withContext('empty is valid').toBeTrue();
      expect(controls.expiryDate.valid).withContext('empty is valid').toBeTrue();
      expect(controls.effectiveDate.hasError('required')).withContext('not required').toBeFalse();
      expect(controls.expiryDate.hasError('required')).withContext('not required').toBeFalse();
      expect(component().form.valid).withContext('and the group is valid').toBeTrue();
      // No field on the screen renders a required marker, because none is required.
      expect(queryAll('.form-field__required')).toHaveSize(0);

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0), 'the assignment');

      // ⚠ AN OMITTED BOUND TRAVELS AS `null`, NEVER AS `''`. The empty string is the legacy
      // absent-STRING marker (`Null.vb:L71-L75`), and a blank where a date is expected is a value the
      // server would have to guess at. The legacy handler substituted the DATE sentinel here
      // (`SecurityRoles.ascx.vb:L528-L539`), which the column cannot hold at all — its range begins in
      // 1753 — so `null` is the contract and `null` is what is sent.
      expect(call.request.body).toEqual({
        userId: 42,
        effectiveDate: null,
        expiryDate: null,
        notifyUser: false,
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerMemberships(0, [membership()]);
      expectNoReprobe();
    });

    /**
     * ⚠ THE ACCOUNT FIELD CARRIES NO REQUIRED VALIDATOR EITHER, AND THE WRITE IS STILL WITHHELD.
     *
     * `SecurityRoles.ascx.vb:L520` gated the write on `Page.IsValid`, which covered ONLY the three
     * date validators above. The account was checked separately at `:L521` by
     * `(Not Role Is Nothing) AndAlso (Not User Is Nothing)` — A GUARD CLAUSE, NOT A VALIDATOR. The
     * equivalent here is the derived view behind the action's disabled state, so the screen shows the
     * same messages the legacy screen showed while refusing the same writes it refused.
     */
    it('withholds the write with nobody chosen, without claiming the field is required', () => {
      arrive(0, []);

      const userId = component().form.controls.userId;

      expect(userId.value).withContext('nothing chosen is null, never an identifier').toBeNull();
      expect(userId.hasError('required')).withContext('no required rule').toBeFalse();
      expect(userId.valid).withContext('the control itself is valid').toBeTrue();
      expect(component().form.valid).withContext('and so is the group').toBeTrue();
      expect(fieldErrors()).withContext('so nothing is said about it').toEqual([]);
      expect(queryAll('.form-field__required')).toHaveSize(0);

      // Yet the action is gated on the guard clause rather than on validity.
      expect(component().canSubmit()).toBeFalse();

      const control = button(ADD_USER_LABEL);

      expect(control).withContext('the action is rendered').not.toBeUndefined();
      expect((control as HTMLButtonElement).disabled).toBeTrue();

      (control as HTMLButtonElement).click();
      fixture.detectChanges();

      httpMock.expectNone(membersUrl(0));
      expect(notifications()).toHaveSize(0);
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 4 — CHOOSING AN ACCOUNT, AND THE ACTION'S OWN WORDING
  // -------------------------------------------------------------------------------------------------

  describe('looking an account up', () => {
    it('queries the account listing by name, one page at a time', () => {
      // MIGRATION: `securityroles.ascx:L26` declared a dropdown bound at
      // `SecurityRoles.ascx.vb:L204` to EVERY account in the tenant, which does not scale. The
      // screen's own help text — 'Enter The User Name and click Validate to confirm' — says the text
      // box was the intended affordance, so the lookup replaces the dropdown with it.
      arrive(0, []);

      lookUp('ada');

      const call = expectLookupRequest('the account lookup');

      // The term is sent RAW: the picker matches on a prefix, so appending a wildcard would search
      // for the wildcard itself — and the server escapes what it is given, so a per-cent sign an
      // operator typed matches a per-cent sign.
      const sent = lookupTerms(call);

      expect(sent['query']).toBe('ada');
      expect(sent['pageIndex']).toBe('0');
      expect(sent['pageSize']).toBe(LOOKUP_PAGE_SIZE);

      // ⚠ AND IT ASKS THE PICKER, NOT THE ACCOUNT LISTING, which is what keeps a postal address, a
      // telephone number, an electronic-mail address, two audit instants and four status flags out of
      // every answer this screen receives. Stated as an address assertion because the address IS the
      // projection: nothing else about the request distinguishes a three-member answer from a
      // sixteen-member one.
      expect(call.request.url)
        .withContext('the picker answers a key and two captions; the listing answers a grid row')
        .toBe(USERS_CHOICES_URL);

      // ⚠ AND THE ORDER, which is what makes an exact match reachable in one request. Ascending by
      // LOGIN NAME over a prefix-matched set puts the shortest match first, and the shortest match
      // is the typed name itself. The picker's own default orders by DISPLAY name — the caption, not
      // the value searched for — which is precisely how an exact match ended up unreachable.
      expect(sent['sortBy']).toBe(LOOKUP_SORT_FIELD);
      expect(sent['sortDir']).toBe(LOOKUP_SORT_DIRECTION);

      call.flush(pageOf([account()], 1, 0, 10));
      fixture.detectChanges();

      expect(component().userMatches()).toHaveSize(1);
    });

    it('says so when nothing matches, rather than blanking the field in silence', () => {
      // MIGRATION: `SecurityRoles.ascx.vb:L476-L488` looked the account up by name and, on no match,
      // blanked the box at `:L484` with no message at all. A visible state replaces that.
      arrive(0, []);

      lookUp('nobody');
      answerLookup([]);

      expect(component().showNoMatchingUsers()).toBeTrue();
      expect(textIn(query('.role-assignment__no-matches')).trim()).toBe(
        NO_MATCHING_USERS,
      );
    });

    it('re-runs an identical search on an explicit submit, rather than answering from memory', () => {
      // MIGRATION: the legacy Validate button re-queried on every press, because a postback had no
      // memory of the previous one. A change gate on the shared control's submit path made a second
      // press silently do nothing, which is at its worst exactly here: an operator who has just
      // created the account they are looking for presses Search again and is told, from a cached
      // answer, that no such login name exists.
      arrive(0, []);

      lookUp('ada');
      answerLookup([]);

      expect(component().showNoMatchingUsers()).toBeTrue();

      // The same term again, through the same rendered control - not the handler behind it, because
      // the gate that had to be removed lived in the control.
      lookUp('ada');

      const repeat = expectLookupRequest('the repeated lookup');

      expect(lookupTerms(repeat)['query']).toBe('ada');

      // And the stale answer was cleared at dispatch rather than left standing while the repeat was
      // in flight, so the screen never shows a refusal beside a search that has not failed.
      expect(component().showNoMatchingUsers()).toBeFalse();

      repeat.flush(pageOf([account()], 1, 0, 100));
      fixture.detectChanges();

      expect(component().userMatches()).toHaveSize(1);
      expect(component().showNoMatchingUsers()).toBeFalse();
    });

    it('explains that the search matches the beginning of the LOGIN name when nothing is found', () => {
      // The wording is the remedy for a working lookup that looked broken: the filter is a prefix on
      // the login name, so 'Admin' finds nothing on a site whose administrator logs in as
      // 'runtime_admin' - an account named in every button this control offers.
      arrive(0, []);

      lookUp('Admin');
      answerLookup([]);

      const note = textIn(query('.role-assignment__no-matches')).trim();

      expect(note).toBe(NO_MATCHING_USERS);
      expect(note).withContext('it names the field searched').toContain('login');
      expect(note).withContext('and the part of it matched').toContain('beginning');
    });

    it('names the list of offered matches, so its purpose is announced and not merely its length', () => {
      arrive(0, []);
      lookUp('ada');
      answerLookup([account()]);

      const list = query<HTMLElement>(MATCHES_SELECTOR);

      // A list announced by role and length alone says how many things there are and nothing
      // about what they are.
      expect(list).not.toBeNull();
      expect(list?.getAttribute('aria-label')).toBe(MATCHES_LABEL);
    });

    it('offers each match as a real toggle button rather than as a listbox option', () => {
      arrive(0, []);
      lookUp('ada');
      answerLookup([account()]);

      const list = query<HTMLElement>(MATCHES_SELECTOR);
      const actions = queryAll<HTMLButtonElement>(MATCH_ACTION_SELECTOR);

      expect(actions).toHaveSize(1);

      // ⚠ THE ABSENCES ARE THE POINT, and each one is a conformance claim rather than a
      // preference. `role="option"` would replace the button role on these controls, and
      // `option` does not support `aria-pressed` — so the pair would be invalid, not merely
      // unconventional. A listbox is additionally a composite widget with a mandatory
      // keyboard contract this screen does not implement, and the matches are inline content
      // rather than a popup, so neither `listbox` nor `combobox` describes them.
      expect(list?.getAttribute('role')).toBeNull();
      for (const action of actions) {
        expect(action.tagName.toLowerCase()).toBe('button');
        expect(action.type).toBe('button');
        expect(action.getAttribute('role')).toBeNull();
        expect(action.getAttribute('aria-selected')).toBeNull();
        expect(action.getAttribute('aria-pressed')).toBe('false');
        expect(action.getAttribute('tabindex')).toBeNull();
      }
      expect(query('datalist')).toBeNull();
      expect(query('[role="listbox"]')).toBeNull();
      expect(query('[role="combobox"]')).toBeNull();
      expect(query('[role="option"]')).toBeNull();
    });

    it('carries the selection on the control itself once a match is chosen', () => {
      arrive(0, []);
      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      const action = query<HTMLButtonElement>(MATCH_ACTION_SELECTOR);

      // The state a reader hears and the state the stylesheet paints are one attribute, so
      // they cannot disagree; and it stays a button, so releasing it needs no extra key.
      expect(action?.getAttribute('aria-pressed')).toBe('true');
      expect(action?.tagName.toLowerCase()).toBe('button');
    });

    it('names the lookup field once, with the caption the legacy screen carried', () => {
      arrive(0, []);

      const field = query<HTMLInputElement>('input.search-input__field');
      const labels = Array.from(field?.labels ?? []);

      // The shared lookup withholds its own generic 'Search:' caption here, because this
      // field already carries `plUsers.Text`. Two labels would give the control a composite
      // accessible name in which the caption nearest the box is no longer the whole name —
      // the SC 2.5.3 Label in Name mismatch.
      expect(field).not.toBeNull();
      expect(labels).toHaveSize(1);
      expect(labels[0]?.textContent?.trim()).toBe(USER_LABEL);
      expect(query('label.search-input__label')).toBeNull();
    });

    it('prefills the window from an existing membership when one is chosen', () => {
      // MIGRATION: this is the first branch of `GetDates` at `SecurityRoles.ascx.vb:L273-L303`, which
      // showed an existing membership's two bounds and skipped either one the null test reported as
      // unset (`:L281-L286`). The membership is taken from the ROWS ON SCREEN when they carry it, and
      // asked of the server only when they cannot - the rows are the same fact, already fetched.
      const held = membership({
        userId: 42,
        effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z`,
        expiryDate: `${EXPIRY_DATE}T00:00:00Z`,
      });

      arrive(0, [held]);
      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', [held]);

      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe(EFFECTIVE_DATE);
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe(EXPIRY_DATE);
      // Prefilled and NOT marked visited, so no dynamic validator speaks about a value the operator
      // never typed.
      expect(component().form.controls.effectiveDate.touched).toBeFalse();
      expect(fieldErrors()).toEqual([]);
    });

    it('ASKS NOTHING when the rendered listing can answer, so no refusal is used as a signal', () => {
      // ⚠ THE `404`-AS-A-QUESTION, WHICH IS WHAT THIS CASE EXISTS TO KEEP CLOSED. Choosing an account
      // used to issue `GET /roles/{roleId}/users/{userId}` on every selection, and the endpoint answers
      // `404` for an account that holds nothing - so the ordinary, entirely successful path of enrolling
      // somebody new emitted a failed request and a red console error before the write was even sent.
      // Runtime testing measured the whole sequence: accounts read `200`, pairing probe `404`,
      // assignment `204`. A refusal is an answer to a request that should not have been made.
      //
      // The rows already hold the fact. When the listing's published total is fully rendered - which is
      // the ordinary case for a role with one page of members - absence from those rows PROVES the
      // account holds nothing, and the question is settled without asking anybody.
      arrive(0, [membership({ userId: 43, username: 'grace', displayName: 'Grace Hopper' })]);
      lookUp('ada');
      answerLookup([account()]);

      chooseAccount('Ada Lovelace');

      // Not "no probe" but NO REQUEST OF ANY KIND, asserted at the transport rather than at one address,
      // so a differently-shaped question would fail here too.
      expect(httpMock.match(() => true))
        .withContext('choosing an account the rendered listing can answer for asks nothing')
        .toHaveSize(0);
      expect(component().selectedMembership())
        .withContext('and the answer is settled, not merely unknown')
        .toBeNull();
      expect(component().actionLabel()).toBe(ADD_USER_LABEL);
    });

    it('still ASKS when the listing on screen cannot settle it, because a page is unread', () => {
      // The complement of the case above, and the reason the probe is kept rather than deleted. A
      // listing that publishes more members than it rendered leaves a page nobody has read, so absence
      // from the rows proves nothing - and answering from the visible page would relabel the command
      // according to which page happened to be showing.
      arrive(
        0,
        [membership({ userId: 43, username: 'grace', displayName: 'Grace Hopper' })],
        {},
        // Forty members published, one rendered: thirty-nine rows nobody on this screen has seen.
        40,
      );
      lookUp('ada');
      answerLookup([account()]);

      const held = membership({ userId: 42, effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z` });

      chooseAccount('Ada Lovelace', [held]);

      expect(component().selectedMembership()?.userRoleId)
        .withContext('the pairing endpoint answered what the rows could not')
        .toBe(11);
      expect(component().actionLabel()).toBe(UPDATE_USER_ROLE_LABEL);
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe(EFFECTIVE_DATE);
    });

    it('offers an empty window for somebody who does not hold the role yet', () => {
      arrive(0, []);
      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', []);

      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe('');
      // "Nothing to prefill" is a SETTLED answer rather than an unanswered question: this listing
      // published a total of nothing and rendered all of it, so there is no page the account's row
      // could be hiding on.
      expect(component().selectedMembership()).toBeNull();
    });

    it('clears the chosen account and its window on demand', () => {
      const held = membership({ userId: 42, effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z` });

      arrive(0, [held]);
      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', [held]);

      expect(accountIsChosen('Ada Lovelace')).withContext('held').toBeTrue();

      // Pressing the chosen account again releases it, which is how a mis-click is corrected. The
      // pressed state IS the choice, so it is read from the control rather than inferred from colour.
      chooseAccount('Ada Lovelace');

      expect(accountIsChosen('Ada Lovelace')).withContext('released').toBeFalse();
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(button(ADD_USER_LABEL)?.disabled).withContext('nobody chosen again').toBeTrue();
      httpMock.expectNone(() => true);
    });

    it('says it is searching, so a walk in progress is not mistaken for no match', () => {
      // ⚠ THE TWO STATES RENDER IDENTICALLY WITHOUT THIS: an empty match list and no message. A walk
      // takes visibly longer than the single read it replaced, so the distinction stopped being
      // theoretical. The wording is the LOOKUP'S own and not the account list's — telling somebody
      // who typed a name that the site is being enumerated would describe a different operation.
      arrive(0, []);

      lookUp('sm');

      expect(component().userLookupLoading()).toBeTrue();
      expect(textIn(query('app-loading-spinner'))).toContain('Searching for matching accounts');
      expect(component().showNoMatchingUsers())
        .withContext('not yet a no-match state')
        .toBeFalse();

      answerLookupPage([], 0, 0);

      expect(component().userLookupLoading()).toBeFalse();
      expect(query('app-loading-spinner')).withContext('withdrawn once settled').toBeNull();
      expect(component().showNoMatchingUsers()).withContext('now a no-match state').toBeTrue();
    });

    it('offers no pager, because walking the match set is what the operator no longer has to do', () => {
      // MIGRATION: `securityroles.ascx` declares no pager on this control at all. A pager would also
      // reintroduce exactly the ambiguity the walk removes — a window with page controls is what a
      // truncated answer looks like — so the totals are stated in words instead.
      arrive(0, []);

      lookUp('sm');
      answerLookupPage(accountRun('sm', 100, 1), 150, 0);
      answerLookupPage(accountRun('sm', 50, 101), 150, 1);

      expect(queryAll('app-pagination')).withContext('the shared pager').toHaveSize(0);

      // And nothing hand-rolled stands in for one. Every control inside the account field is either
      // one of the offered accounts or part of the shared search box itself — the search box's own
      // submit button is why this is filtered by ancestry rather than counted outright.
      const strays = queryAll<HTMLButtonElement>('app-form-field button').filter(
        (control) =>
          control.classList.contains('role-assignment__match-action') === false &&
          control.closest('app-search-input') === null &&
          control.classList.contains('form-field__help-toggle') === false,
      );

      expect(strays.map((control) => textIn(control).trim())).toEqual([]);
    });

    it('follows page after page until it finds the exact account, which one page would have missed', () => {
      // ⚠ THE DEFECT THIS SCREEN WAS CORRECTED FOR, stated as a case. The lookup read page zero
      // alone, so an account was selectable only when it happened to fall in the first page of
      // everything sharing its prefix. Here 'sm' matches 250 accounts and 'smithson' is the 201st,
      // so the old behaviour could not reach it at ANY page size the paging rules allow - and an
      // account that cannot be selected cannot be enrolled, which is the whole purpose of the screen.
      arrive(0, []);

      lookUp('smithson');

      answerLookupPage(accountRun('sm', 100, 1), 250, 0);
      answerLookupPage(accountRun('sm', 100, 101), 250, 1);
      answerLookupPage(
        [account({ userId: 500, username: 'smithson', displayName: 'Ada Smithson' })],
        250,
        2,
      );

      expect(
        component()
          .userMatches()
          .map((match) => match.username),
      ).toContain('smithson');
    });

    it('stops the moment the exact account is in hand, however many pages remain', () => {
      // Once the named account is found nothing a later page could carry would improve the answer, so
      // every remaining request would be work nobody needed. The server reports 250 matches and only
      // one page is asked for.
      arrive(0, []);

      lookUp('ada');
      answerLookupPage([account({ username: 'ada' })], 250, 0);

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
      expect(component().userMatches()).toHaveSize(1);
    });

    it('does NOT treat a page shorter than requested as the end of the match set', () => {
      // ⚠ THE TERMINATION RULE, AND THE ONE THAT IS EASY TO GET WRONG. A short page is what a
      // filtered listing produces mid-set, so ending on it is precisely how a truncated answer
      // passes for a complete one. The server's own total is what ends the walk: three matches are
      // reported, the first page carries one, and the walk asks again.
      arrive(0, []);

      lookUp('sm');

      answerLookupPage([account({ userId: 1, username: 'sm1', displayName: 'One' })], 3, 0);
      answerLookupPage(
        [
          account({ userId: 2, username: 'sm2', displayName: 'Two' }),
          account({ userId: 3, username: 'sm3', displayName: 'Three' }),
        ],
        3,
        1,
      );

      expect(
        component()
          .userMatches()
          .map((match) => match.displayName),
      ).toEqual(['One', 'Two', 'Three']);
      expect(component().userLookupSummary()).withContext('nothing left unsaid').toBeNull();
    });

    it('ends the walk on an empty page rather than asking for the same total for ever', () => {
      // A server that reports more matches than it will supply cannot be waited out: an empty page
      // can only be followed by another empty one. What was gathered is every account it was willing
      // to supply for this name, so it is published rather than refused.
      arrive(0, []);

      lookUp('sm');

      answerLookupPage([account({ userId: 1, username: 'sm1', displayName: 'One' })], 99, 0);
      answerLookupPage([], 99, 1);

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
      expect(component().userMatches()).toHaveSize(1);
    });

    it('matches the exact account without regard to case, the way the legacy lookup did', () => {
      // MIGRATION: `GetUserByName` at `SecurityRoles.ascx.vb:L480` resolved through a SQL Server
      // lookup under the database's own collation, which for a default installation does not
      // distinguish case - so an operator who typed 'Ada' found 'ada'. A case-sensitive test here
      // would refuse a name the legacy screen accepted, and would keep walking past the answer.
      arrive(0, []);

      lookUp('ADA');
      answerLookupPage([account({ username: 'ada' })], 250, 0);

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
      expect(component().userMatches()).toHaveSize(1);
    });

    it('reports the server\u2019s own total when more accounts match than it can offer', () => {
      // The walk examines the whole match set; only so many of it can reasonably become buttons. The
      // difference is STATED rather than hidden, because the shorter list would otherwise read as the
      // whole answer - which is the same misreading the first-page-only read invited.
      arrive(0, []);

      lookUp('sm');

      // 150 matches reported, the first page carries 100 and holds no exact name, so the second is
      // followed; 150 are then examined and 100 offered.
      answerLookupPage(accountRun('sm', 100, 1), 150, 0);
      answerLookupPage(accountRun('sm', 50, 101), 150, 1);

      expect(component().userMatches()).toHaveSize(100);
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(
        'Showing 100 of 150 matching accounts. Type more of the name to narrow the list.',
      );
    });

    it('offers the exact account even when it falls beyond what can be shown', () => {
      // ⚠ THE UNREACHABLE-ACCOUNT DEFECT, ONE LAYER UP. The walk stops on the page holding the exact
      // name, so a name found on the third page would be examined and then dropped from the very
      // list it ended the search. It is hoisted to the FRONT instead, and the note says how many
      // were examined.
      arrive(0, []);

      lookUp('smithson');

      answerLookupPage(accountRun('sm', 100, 1), 400, 0);
      answerLookupPage(accountRun('sm', 100, 101), 400, 1);
      answerLookupPage(
        [
          ...accountRun('sm', 99, 201),
          account({ userId: 500, username: 'smithson', displayName: 'Ada Smithson' }),
        ],
        400,
        2,
      );

      const offered = component().userMatches();

      expect(offered).toHaveSize(100);
      expect(offered[0].username).withContext('hoisted to the front').toBe('smithson');
      // The SERVER'S total is reported — 400 match, 100 are offered — not the 300 the walk happened
      // to examine before the exact name ended it. And the wording drops the "type more of the name"
      // advice, which would be advice against a search that already succeeded exactly.
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(
        'The exact match is offered first. Showing 100 of 400 matching accounts.',
      );
    });

    it('abandons a walk in flight when a newer term supersedes it, rather than paging on', () => {
      // A walk nobody is waiting for is a sequence of requests the tenant pays for, and its answer
      // would repopulate a list the operator has already replaced. With a walk rather than a single
      // read this matters more than it did: the abandoned one would keep asking for pages.
      arrive(0, []);

      lookUp('sm');
      answerLookupPage(accountRun('sm', 100, 1), 500, 0);

      // Page one is outstanding for 'sm' when the narrower term arrives.
      lookUp('smithson');

      // ⚠ A CANCELLED REQUEST IS STILL A MATCHABLE ONE, so the proof is the cancellation flag rather
      // than a count: the testing backend marks an abandoned request cancelled and leaves it in its
      // open set. Both are claimed here, in the order they were issued.
      const [abandoned, restarted] = httpMock.match(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('query') !== null,
      );

      // The superseded walk's page ONE is the request that was in flight, and it is dead.
      expect(lookupTerms(abandoned)['query'])
        .withContext('the superseded term')
        .toBe('sm');
      expect(lookupTerms(abandoned)['pageIndex'])
        .withContext('mid-walk')
        .toBe('1');
      expect(abandoned.cancelled).withContext('superseded').toBeTrue();

      // And the new walk starts from the beginning rather than continuing the old coordinate.
      expect(lookupTerms(restarted)['query']).toBe('smithson');
      expect(lookupTerms(restarted)['pageIndex']).toBe('0');
      expect(restarted.cancelled).withContext('current').toBeFalse();

      restarted.flush(pageOf([account({ username: 'smithson' })], 1, 0, 100));
      fixture.detectChanges();

      // Nothing further is asked: the abandoned walk cannot resume, so no page two of 'sm' appears.
      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('query') !== null,
      );
      expect(component().userMatches()).toHaveSize(1);
    });

    it('says the search was cut short rather than passing a partial sweep off as complete', () => {
      // The ceiling is a REPORTED stop rather than a refusal, and the asymmetry with the store's
      // walks is deliberate: those answer "every module" and "every membership", where a partial
      // answer masquerading as complete is the defect. This one answers "does this name exist",
      // where what was examined is genuinely useful and the operator's next move - typing more of
      // the name - is both obvious and offered.
      arrive(0, []);

      lookUp('s');

      // Reported total is never reached, so only the ceiling can end it. Every page is answered by
      // the same criteria, which is why the page coordinate is read back from the request.
      for (let page = 0; page < LOOKUP_PAGE_CEILING; page += 1) {
        answerLookupPage(accountRun('s', 1, page + 1), 1_000_000, page);
      }

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(
        `The search examined ${String(LOOKUP_PAGE_CEILING)} of 1000000 matching accounts without ` +
          'finding an exact match and stopped there. Type more of the name to narrow it.',
      );
    });
  });

  describe("the tenant's account-selection policy", () => {
    /**
     * MIGRATION: `Security_UsersControl`, read at `SecurityRoles.ascx.vb:L133-L136` and acted on at
     * `:L202-L221`. `UsersControl.Combo` bound `cboUsers` to the tenant's whole account listing and
     * hid the name box; the other value did the reverse. `:L106-L109` then read the chosen account
     * from whichever control was live.
     *
     * The setting was previously read, written and validated end to end and then consumed by
     * nothing — a screen that offered the name box whatever the tenant chose. Every case here is
     * about the consumption.
     */

    /** The account dropdown, or `null` when the name box is what is rendered. */
    function choices(): HTMLSelectElement | null {
      return query<HTMLSelectElement>('select.role-assignment__choices');
    }

    /** The wording of every entry the dropdown offers, prompt included. */
    function choiceLabels(): readonly string[] {
      return queryAll<HTMLOptionElement>('select.role-assignment__choices option').map((option) =>
        textIn(option).trim(),
      );
    }

    /** The name box, or `null` when the dropdown is what is rendered. */
    function nameBox(): HTMLInputElement | null {
      return query<HTMLInputElement>('input[type="search"]');
    }

    /** Chooses an entry of the dropdown the way a browser does, by value and a change event. */
    function chooseFromDropdown(value: string, held: readonly UserRole[] = []): void {
      const control = choices();

      expect(control).withContext('the dropdown is rendered').not.toBeNull();

      (control as HTMLSelectElement).value = value;
      (control as HTMLSelectElement).dispatchEvent(new Event('change'));
      fixture.detectChanges();

      // ⚠ CHOOSING FROM THE DROPDOWN RESOLVES EXACTLY AS THE NAME-BOX PATH DOES: from the rendered
      // rows, or from a listing whose published total is fully rendered, and only otherwise with one
      // keyed probe addressed at the PAIRING rather than by narrowing the listing with a login name.
      // Releasing a choice asks nothing either way.
      const probes: readonly TestRequest[] = httpMock.match(isMembershipProbe);

      if (probes.length === 0) {
        // The same rows-first resolution the name-box path uses; see `chooseAccount`.
        const resolved: UserRole | null = component().selectedMembership();

        if (held.length === 0) {
          expect(resolved)
            .withContext('releasing a choice resolves to no membership')
            .toBeNull();

          return;
        }

        expect(resolved?.userRoleId)
          .withContext('the rows on screen answered, so nothing had to be asked')
          .toBe(held[0]?.userRoleId);

        return;
      }

      expect(probes).withContext('choosing an account issues exactly one keyed probe').toHaveSize(1);
      expect(probes[0].request.params.keys()).toEqual([]);

      answerProbe(probes[0], held);
      fixture.detectChanges();
    }

    it('offers the dropdown, holding every account, when the tenant asks for it', () => {
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [membership()]);
      answerAccountChoicesPage([
        account({ userId: 42, username: 'ada', displayName: 'Ada Lovelace' }),
        account({ userId: 43, username: 'grace', displayName: 'Grace Hopper' }),
      ]);

      expect(choices()).withContext('the dropdown').not.toBeNull();
      // ⚠ AND THE NAME BOX IS GONE. The legacy screen hid one control when it showed the other
      // (`:L206` and `:L214`), so offering both would be a screen the legacy never rendered.
      expect(nameBox()).withContext('the name box').toBeNull();
      // ⚠ AN ACCOUNT THAT ALREADY HOLDS THE ROLE IS ANNOTATED RATHER THAN WITHHELD, and both halves of
      // that are deliberate. `SecurityRoles.ascx.vb:L204` filled this list from
      // `UserController.GetUsers(PortalId, False)` with NO membership exclusion, and choosing an
      // existing member is a supported workflow rather than a mistake - it is how the legacy screen
      // amended a membership's bounds (`:L273-L303` prefills them from the existing row). Withholding
      // those accounts would delete that workflow. What was genuinely wrong was that the list said
      // nothing: `ada` holds this role and was offered indistinguishably from `grace`, who does not.
      expect(choiceLabels()).toEqual([
        USER_CHOICE_PROMPT,
        'Ada Lovelace (ada) — already in this role',
        'Grace Hopper (grace)',
      ]);
    });

    it('describes the dropdown with its own help, not the name box\u2019s instruction', () => {
      // MIGRATION: `plUsers.HelpText` says 'Enter The User Name and click Validate to confirm',
      // which is untrue of a dropdown. The legacy screen carried one help string for both controls
      // only because both shared one label cell (`securityroles.ascx:L14`).
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [membership()]);
      answerAccountChoicesPage([account()]);

      // The help sits behind the shared field's own disclosure, so it is revealed rather than read
      // from the collapsed document — the wrapper removes the block entirely while it is closed.
      const field = choices()?.closest('app-form-field');

      field?.querySelector<HTMLButtonElement>('.form-field__help-toggle')?.click();
      fixture.detectChanges();

      expect(textIn(field?.querySelector('.form-field__help')).trim()).toBe(USER_CHOICE_HELP);
      // And the name box's instruction appears nowhere, revealed or not.
      expect(host().textContent).not.toContain(USER_HELP);
    });

    it('follows every page of the account list, so no account is missing from the dropdown', () => {
      // The dropdown claims to hold EVERY account, so a first-page-only read would hide the accounts
      // it dropped and an operator could not tell a missing account from an absent one.
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [membership()]);

      const first = answerAccountChoicesPage(accountRun('a', 100, 1), 150, 0);
      answerAccountChoicesPage(accountRun('b', 50, 101), 150, 1);

      expect(component().accountChoices()).toHaveSize(150);
      expect(choiceLabels()).toHaveSize(151);

      // ⚠ AND NO SORT IS ASKED FOR, unlike the lookup's request. The listing's default orders by
      // DISPLAY name, which is what these entries are captioned with, so they read in the order they
      // are shown; asking for the lookup's login-name order would sort the list by a value the
      // operator cannot see.
      expect(first.request.params.get('sortBy')).toBeNull();
      expect(first.request.params.get('userName')).toBeNull();
    });

    it('chooses an account from the dropdown and prefills its window', () => {
      // MIGRATION: `cboUsers` carried `autopostback="True"` (`securityroles.ascx:L26`), so choosing an
      // entry round-tripped the whole page to reach the prefill at `:L273-L303`. It happens without
      // one, from the memberships already in hand.
      const held = membership({
        userId: 42,
        effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z`,
        expiryDate: `${EXPIRY_DATE}T00:00:00Z`,
      });

      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [held]);
      answerAccountChoicesPage([account({ userId: 42 })]);

      // ⚠ THE PREFILL COMES FROM THE KEYED PROBE, NOT FROM A WHOLE-SET READ, and that is the point of
      // passing the membership here. Reading a role's entire membership was WITHDRAWN — it retained
      // the whole set and re-read it on every write — so the rows in hand are one page and the
      // account chosen from a dropdown of every account may well not be on it. The probe asks the one
      // narrow question the prefill needs, and it is bounded to a single request.
      chooseFromDropdown('42', [held]);

      expect(component().formState().userId).toBe(42);
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe(EFFECTIVE_DATE);
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe(EXPIRY_DATE);
      // And nothing FURTHER is asked: one probe, already settled above, and no second request of any
      // kind — no re-read of the page, no walk, no second probe.
      httpMock.expectNone(() => true);
    });

    it('releases the choice when the empty prompt is chosen again', () => {
      // `<None Specified>` is what the legacy prompt entry meant. The raw value is MATCHED rather
      // than parsed, so the empty value simply matches no account and the choice is cleared.
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [
        membership({ userId: 42, effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z` }),
      ]);
      answerAccountChoicesPage([account({ userId: 42 })]);

      chooseFromDropdown('42', [
        membership({ userId: 42, effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z` }),
      ]);
      expect(component().formState().userId).withContext('chosen').toBe(42);

      // Releasing asks nothing, which the helper asserts by finding no probe outstanding.
      chooseFromDropdown('');

      expect(component().formState().userId).withContext('released').toBeNull();
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(button(ADD_USER_LABEL)?.disabled).toBeTrue();
    });

    it('offers the name box, and walks no account list at all, when the tenant asks for it', () => {
      // The other branch, and the one that keeps a large tenant from paying for a walk of every
      // account it holds. `create` answers the policy with the name-box value by default.
      arrive(0, [membership()]);

      expect(nameBox()).withContext('the name box').not.toBeNull();
      expect(choices()).withContext('the dropdown').toBeNull();
      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
    });

    it('offers neither control until the policy has answered', () => {
      // The legacy screen decided during page load and rendered exactly one control, so it never had
      // this state. Reproducing that means HOLDING the field rather than guessing and correcting -
      // a control swapped underneath an operator mid-interaction is worse than a moment's wait.
      fixture = TestBed.createComponent(RoleAssignmentComponent);
      fixture.componentRef.setInput('roleId', '0');
      fixture.detectChanges();

      expect(nameBox()).withContext('the name box').toBeNull();
      expect(choices()).withContext('the dropdown').toBeNull();
      expect(component().accountPolicyPending()).toBeTrue();

      // Settled so the unconditional verification has nothing left outstanding.
      answerAccountPolicy(USERS_CONTROL_TEXT_BOX);
      answerRole(role(0));
      answerMemberships(0, []);

      expect(nameBox()).withContext('the name box, once the policy arrived').not.toBeNull();
    });

    it('withholds the dropdown from a tenant above the threshold even when it asks for one', () => {
      // ⚠ THE FINDING THIS CASE EXISTS FOR. A stored `Security_UsersControl` of `Combo` used to be
      // taken as the last word, so the screen walked every account the tenant held to fill a
      // `<select>` — and the only bound on that walk was a page ceiling two orders of magnitude past
      // the size at which the legacy framework itself stopped offering the control. A performance
      // review measured it as the one unbounded read on this screen.
      //
      // The threshold is not a default a setting replaces. `UserModuleBase.vb:L178-L186` enforced it
      // by WRITING the name box back as the tenant's setting the first time the count exceeded it, so
      // a tenant whose stored preference predates its growth is exactly the case the legacy handled
      // and this screen did not.
      create('0', { usersControl: USERS_CONTROL_COMBO, accountCount: LEGACY_ACCOUNT_LISTING_CEILING + 1 });
      answerRole(role(0));
      answerMemberships(0, []);

      expect(component().usersControlMode()).toBe('lookup');
      expect(nameBox()).withContext('the name box').not.toBeNull();
      expect(choices()).withContext('the dropdown').toBeNull();

      // ⚠ AND NOTHING IS WALKED. Bounding the walk would have limited the damage; not walking at all
      // is the fix, and this is the assertion that tells the two apart.
      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('query') === null &&
          candidate.params.get('pageSize') === LOOKUP_PAGE_SIZE,
      );
    });

    it('honours a stored dropdown preference at the threshold itself, matching the legacy comparison', () => {
      // ⚠ THE BOUNDARY, AND IT IS THE LEGACY'S OWN. The legacy test was `> 1000`, so a tenant holding
      // exactly one thousand accounts kept the drop-down. An off-by-one here would take the control
      // away from a real site that the legacy served.
      create('0', { usersControl: USERS_CONTROL_COMBO, accountCount: LEGACY_ACCOUNT_LISTING_CEILING });
      answerRole(role(0));
      answerMemberships(0, []);

      expect(component().usersControlMode()).toBe('combo');

      answerAccountChoicesPage([account()]);

      expect(choices()).withContext('the dropdown').not.toBeNull();
    });

    it('offers neither control while the count a stored dropdown preference depends on is outstanding', () => {
      // The control must not be rendered and then exchanged underneath an operator who has already
      // started using it. A stored preference for the drop-down now depends on the count, so the field
      // is held through that read exactly as it is held through the policy read — and the walk waits
      // too, because a walk started on the optimistic reading would enumerate a tenant the count is
      // about to disqualify.
      fixture = TestBed.createComponent(RoleAssignmentComponent);
      fixture.componentRef.setInput('roleId', '0');
      fixture.detectChanges();

      answerAccountPolicy(USERS_CONTROL_COMBO);
      answerRole(role(0));
      answerMemberships(0, []);

      expect(component().accountPolicyPending()).withContext('while the count is outstanding').toBeTrue();
      expect(nameBox()).withContext('the name box').toBeNull();
      expect(choices()).withContext('the dropdown').toBeNull();

      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('query') === null &&
          candidate.params.get('pageSize') === LOOKUP_PAGE_SIZE,
      );

      answerAccountCount(2);

      expect(component().accountPolicyPending()).withContext('once the count answered').toBeFalse();

      answerAccountChoicesPage([account()]);

      expect(choices()).withContext('the dropdown, once the count allowed it').not.toBeNull();
    });

    it('bounds the eagerly materialised choice list by the legacy enumeration threshold', () => {
      // ⚠ THE CEILING IS DERIVED, NOT CHOSEN, and this case pins the derivation. It was one thousand
      // PAGES — a hundred thousand accounts at the server's maximum page size — which permitted a
      // thousand sequential requests and a hundred thousand retained rows to fill a control the legacy
      // stopped offering at one thousand accounts. It is now exactly that threshold expressed in
      // pages, so the walk can never materialise more than the legacy screen was itself willing to.
      expect(ACCOUNT_CHOICE_PAGE_CEILING * Number(LOOKUP_PAGE_SIZE)).toBe(
        LEGACY_ACCOUNT_LISTING_CEILING,
      );
    });

    it('applies the legacy account-count default when the policy cannot be read, offering the DROPDOWN', () => {
      // ⚠ THIS CASE PREVIOUSLY ASSERTED THE NAME BOX, AND THE ASSERTION WAS THE DEFECT. It reasoned
      // that the name box is the affordance that survives an unreadable policy, which is true of the
      // read but not of the DECISION: `UserModuleBase.vb:L178-L183` resolved an absent
      // `Security_UsersControl` from the tenant's account count and defaulted to the drop-down at or
      // below one thousand accounts. `GET /api/v1/users/settings` answers 404 permanently on a tenant
      // with no User Accounts module instance, so the screen offered the wrong control on every visit
      // to such a site — not once, and not transiently.
      create('0', { usersControl: null });
      answerRole(role(0));
      answerMemberships(0, []);

      // The count is asked for FIRST, which is the whole point of the ordering: the threshold exists
      // so that a site larger than it is never enumerated to fill a select.
      answerAccountCount(2);

      expect(component().usersControlMode()).toBe('combo');

      // And then the list is walked, exactly as a tenant that had asked for the drop-down would.
      answerAccountChoicesPage([account({ userId: 7, displayName: 'Ada Lovelace' })], 1);

      expect(choices()).withContext('the dropdown the legacy would have chosen').not.toBeNull();
      expect(nameBox()).withContext('and not the name box').toBeNull();

      // Nothing is explained, because nothing was degraded.
      expect(query('.role-assignment__lookup-note')).toBeNull();
    });

    it('falls back to the name box when the site is too large to enumerate, and says so', () => {
      // The other half of the same legacy rule, and the reason the count is read rather than assumed.
      create('0', { usersControl: null });
      answerRole(role(0));
      answerMemberships(0, []);

      answerAccountCount(LEGACY_ACCOUNT_LISTING_CEILING + 1);

      expect(component().usersControlMode()).toBe('lookup');
      expect(nameBox()).not.toBeNull();
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(
        ACCOUNT_POLICY_DEFAULTED_BY_SIZE,
      );

      // ⚠ AND THE LIST IS NEVER WALKED. A site of this size is precisely what the threshold protects,
      // so a walk here would defeat the rule it is implementing.
      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
    });

    it('treats the threshold itself as within reach, matching the legacy comparison exactly', () => {
      // ⚠ THE BOUNDARY. The legacy test was `> 1000`, so a site holding exactly one thousand accounts
      // got the drop-down. An off-by-one here would change which control a real site is offered.
      create('0', { usersControl: null });
      answerRole(role(0));
      answerMemberships(0, []);

      answerAccountCount(LEGACY_ACCOUNT_LISTING_CEILING);

      expect(component().usersControlMode()).toBe('combo');

      answerAccountChoicesPage([account()], 1);

      expect(choices()).not.toBeNull();
    });

    it('falls back to the name box when the count itself cannot be read, and says why', () => {
      // The decision cannot be made at all, which is the one case where the operator is looking at a
      // control the site's own settings may say should not be there. That is what the sentence
      // explains, and it is a different sentence from the size one because nothing is broken there.
      create('0', { usersControl: null });
      answerRole(role(0));
      answerMemberships(0, []);

      answerAccountCount(null);

      expect(component().usersControlMode()).toBe('lookup');
      expect(nameBox()).not.toBeNull();
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(
        ACCOUNT_POLICY_UNAVAILABLE,
      );

      // A refusal must not be retried on every notification: the probe fires once. Any lookup the
      // blank search dispatched is drained first, so what remains outstanding is only what the effect
      // itself issued.
      component().onUserSearch('');
      fixture.detectChanges();
      httpMock.match(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('query') !== null,
      ).forEach((call) => call.flush(pageOf([], 0, 0, 100)));
      fixture.detectChanges();

      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('pageSize') === ACCOUNT_COUNT_PROBE_PAGE_SIZE,
      );
    });

    it('offers NEITHER control while the fallback count is outstanding', () => {
      // The policy has answered - with nothing - so the count is what decides, and until it settles
      // the answer is genuinely unknown. Holding the field is what stops a control being offered and
      // then exchanged for the other underneath an operator already typing into it.
      create('0', { usersControl: null });
      answerRole(role(0));
      answerMemberships(0, []);

      expect(component().accountPolicyPending()).toBeTrue();
      expect(nameBox()).withContext('the name box').toBeNull();
      expect(choices()).withContext('the dropdown').toBeNull();

      answerAccountCount(2);
      answerAccountChoicesPage([account()], 1);

      expect(component().accountPolicyPending()).toBeFalse();
      expect(choices()).not.toBeNull();
    });

    it('probes no count at all when the tenant policy answered', () => {
      // The count decides nothing once the tenant has said which control it wants, so asking would be
      // a request whose answer is discarded.
      create('0', { usersControl: USERS_CONTROL_TEXT_BOX });
      answerRole(role(0));
      answerMemberships(0, []);

      expect(component().usersControlMode()).toBe('lookup');
      httpMock.expectNone(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === USERS_CHOICES_URL &&
          candidate.params.get('pageSize') === ACCOUNT_COUNT_PROBE_PAGE_SIZE,
      );
    });

    it('falls back to the name box when the account list cannot be completed, and says why', () => {
      // ⚠ A REFUSAL RATHER THAN A TRUNCATION, and the remedy is what makes the refusal affordable.
      // A dropdown claiming to hold every account while holding some of them hides the ones it
      // dropped; the name box reaches any account by name, so nothing the operator could do with a
      // complete dropdown is lost.
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, []);

      // The server reports 150 accounts, supplies 100, and then answers the second page with nothing.
      answerAccountChoicesPage(accountRun('a', 100, 1), 150, 0);
      answerAccountChoicesPage([], 150, 1);

      expect(component().accountChoicesUnavailable()).toBeTrue();
      expect(component().usersControlMode()).toBe('lookup');
      expect(choices()).withContext('no partial dropdown').toBeNull();
      expect(nameBox()).withContext('the name box instead').not.toBeNull();
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(
        ACCOUNT_CHOICES_UNAVAILABLE,
      );
      // The fault itself is announced as well, so a server error does not read as a policy choice.
      expect(notifySpy).toHaveBeenCalled();
    });

    it('says the site holds no accounts rather than offering an empty dropdown in silence', () => {
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, []);
      answerAccountChoicesPage([]);

      expect(component().accountChoicesEmpty()).toBeTrue();
      expect(textIn(query('.role-assignment__lookup-note')).trim()).toBe(ACCOUNT_CHOICES_EMPTY);
      // The dropdown stays, holding only its prompt: the tenant asked for it and it is not broken,
      // it is empty.
      expect(choiceLabels()).toEqual([USER_CHOICE_PROMPT]);
    });

    it('walks the account list ONCE, not again for every notification the screen raises', () => {
      // The walk is triggered from a derived view, so it has to fire on the policy arriving and not
      // on every unrelated settling. Proven by moving the screen to another role - tenant-scoped
      // state is deliberately kept across that - and asserting no second walk.
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [membership()]);
      answerAccountChoicesPage([account()]);

      fixture.componentRef.setInput('roleId', '5');
      fixture.detectChanges();
      answerRole(role(5));
      answerMemberships(5, []);

      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_CHOICES_URL,
      );
      expect(component().accountChoices()).toHaveSize(1);
    });
  });


  describe("the action's wording", () => {
    /**
     * MIGRATION: only ONE of the two legacy relabel branches is live in this mode.
     * `SecurityRoles.ascx.vb:L650` tested the ROLE identifier against the null sentinel, which is
     * false here, so `:L651-L653` was dead code on this screen. `:L656` tested the ACCOUNT identifier,
     * which is true here, and `:L657-L658` is the branch that ran — it relabelled the action to
     * `UpdateRole.Text` when a row's account matched the chosen one.
     *
     * MIGRATION: the fact is read from the SERVER'S answer about the chosen account rather than by
     * scanning the rendered rows. The legacy grid held every membership, so "a rendered row matches"
     * and "the account holds this role" were the same statement; with one page rendered they are not,
     * and scanning the page would relabel the action according to which page happens to be on screen.
     */
    it("becomes 'Update User Role' once the chosen account already holds the role", () => {
      const held = membership({ userId: 42 });

      arrive(0, [held]);

      expect(component().actionLabel())
        .withContext('before anybody is chosen')
        .toBe(ADD_USER_LABEL);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', [held]);

      expect(component().actionLabel()).toBe(UPDATE_USER_ROLE_LABEL);
      expect(button(UPDATE_USER_ROLE_LABEL)).withContext('rendered').not.toBeUndefined();
      expect(button(ADD_USER_LABEL)).withContext('and replaced').toBeUndefined();
    });

    it("stays 'Add User to Role' for an account that holds no membership", () => {
      arrive(0, []);
      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', []);

      expect(component().actionLabel()).toBe(ADD_USER_LABEL);
      expect(button(ADD_USER_LABEL)).not.toBeUndefined();
      expect(button(UPDATE_USER_ROLE_LABEL)).toBeUndefined();
    });

    it('never shows the design-time default or the account-centric wording', () => {
      arrive(0, []);

      const text: string = textIn(host());

      // 'Add Role' is the design-time `cmdAdd.Text` default in the markup and 'Add Role to User'
      // belongs to the account-centric half of the legacy control, which is out of scope — there is no
      // roles-held-by-one-account endpoint declared anywhere in this application.
      expect(text).not.toContain('Add Role');
      expect(text).not.toContain('Add Role to User');
      expect(text).not.toContain('Manage Roles for User');
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 5 — ADDING A MEMBERSHIP: THE UPSERT, AND THE RE-READ THAT FOLLOWS IT
  // -------------------------------------------------------------------------------------------------

  describe('adding a membership', () => {
    /**
     * ⚠ THE CONTRACT'S OWN ANSWER IS `204` WITH NO BODY. `RoleService.assignUser` is typed
     * `Observable<void>` and the controller declares no content, because an assignment is an EDGE
     * between two existing resources rather than a new resource of its own.
     */
    it('posts the four declared members and accepts 204 with no body at all', () => {
      arriveAndChoose();

      typeDate(EFFECTIVE_DATE_CONTROL_ID, EFFECTIVE_DATE);
      typeDate(EXPIRY_DATE_CONTROL_ID, EXPIRY_DATE);

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0), 'the assignment');

      // Asserted as a WHOLE object, so a member added, renamed or dropped by either side fails here.
      expect(call.request.body).toEqual({
        userId: 42,
        effectiveDate: EFFECTIVE_DATE,
        expiryDate: EXPIRY_DATE,
        notifyUser: false,
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // MIGRATION: the listing is RE-READ, reproducing the unconditional rebind at
      // `SecurityRoles.ascx.vb:L546`. ONE page read, issued by the store, and NOTHING ELSE: those rows
      // carry the membership the pairing probe used to be asked for, so they are what moves the action's
      // wording from an addition to a replacement.
      answerMemberships(0, [membership()]);
      expectNoReprobe();

      expect(component().actionLabel()).toBe(UPDATE_USER_ROLE_LABEL);
      expect(rows()).toHaveSize(1);
      // ⚠ THE ADDITION IS ANNOUNCED, AND IT NAMES BOTH PARTIES. This case previously asserted that
      // NOTHING was said - which was faithful to a legacy screen that answered every write with a full
      // page reload, and indefensible in a document that does not reload: the row appeared in a grid
      // that may be scrolled out of view, and a role's own create, update and delete all announced
      // themselves. The wording is chosen from the state captured AT DISPATCH, so an addition reads as
      // an addition even though the same write leaves the screen in the amend state afterwards.
      expect(notifications()).toEqual([
        { severity: 'success', message: 'Ada Lovelace was added to the Administrators role.' },
      ]);
    });

    it('RE-BASELINES the two bounds from the stored membership once the write has settled', () => {
      // ⚠ THE STATE THE AMEND COMMAND WOULD HAVE WRITTEN. A successful addition leaves this screen in
      // the amend state - the command reads "Update User Role", because the chosen account now holds the
      // role - while the boxes still held whatever was typed before the write. With the expiry left
      // empty that is not merely stale, it is DESTRUCTIVE: the server derives a bound of its own
      // (`RoleController.vb:L493-L501` back-dates a used trial, and the paid-term rules set one
      // otherwise), so pressing the amend command from an empty box would have written the derived bound
      // away without the operator ever seeing it existed.
      //
      // The re-read that follows every write carries the stored row, so the bounds are taken from it.
      arriveAndChoose(0, []);

      // Nothing typed: the operator is enrolling somebody and letting the server decide the window.
      press(ADD_USER_LABEL);

      expectRequest('POST', membersUrl(0), 'the assignment').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();

      // The stored membership the server derived, which the operator never typed.
      answerMemberships(0, [
        membership({
          effectiveDate: `${EFFECTIVE_DATE}T00:00:00Z`,
          expiryDate: `${EXPIRY_DATE}T00:00:00Z`,
        }),
      ]);
      expectNoReprobe();

      expect(component().actionLabel())
        .withContext('the command now offers to amend')
        .toBe(UPDATE_USER_ROLE_LABEL);
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value)
        .withContext('and the values behind it are the stored ones')
        .toBe(EFFECTIVE_DATE);
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value)
        .withContext('including the bound the server derived from an empty box')
        .toBe(EXPIRY_DATE);
      // Re-baselined, not typed: neither box is marked visited, so no dynamic validator speaks about a
      // value nobody entered.
      expect(component().form.controls.expiryDate.touched).toBeFalse();
      expect(fieldErrors()).toEqual([]);
    });

    it('leaves the boxes alone when a listing read the OPERATOR asked for settles', () => {
      // The re-baseline is keyed to a write. A page turn or a sort re-reads the same listing, and
      // rewriting the boxes then would take a half-typed window away from whoever was typing it.
      arriveAndChoose(0, []);
      typeDate(EXPIRY_DATE_CONTROL_ID, EXPIRY_DATE);

      // No write: the reader re-orders the grid, which re-reads the same listing. The answer carries a
      // membership for somebody else entirely, so a re-baseline here would blank both boxes.
      component().onSortChange({ key: 'userName', direction: 'Ascending' });
      fixture.detectChanges();
      answerMemberships(0, [
        membership({ userId: 43, username: 'grace', displayName: 'Grace Hopper' }),
      ]);

      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value)
        .withContext('what was typed is still what is on screen')
        .toBe(EXPIRY_DATE);
    });

    /**
     * ⚠ AND A `201` WITH A BODY LANDS ON THE SAME SUCCESS PATH.
     *
     * The legacy write was an UPSERT: `RoleController.vb:L503` seeds the assignment identifier with the
     * null-integer sentinel and `:L550-L555` branches `If UserRoleId <> -1 Then` to UPDATE the
     * existing row, otherwise calling `AddUserRole`. The screen therefore inspects no status at all,
     * which is exactly what makes it tolerant of either answer — asserting on the status would be this
     * client re-deriving a fact the server chose not to publish.
     *
     * ⚠ A NUANCE WORTH KNOWING RATHER THAN CODING AROUND: `RoleController.vb:L647-L663` logged the
     * event and sent the notification ONLY when the membership was new (`:L655` tests
     * `If objUserRole Is Nothing Then`), so an update was silent on the server side. That asymmetry
     * lives on the server; this screen treats both outcomes alike.
     */
    it('accepts a 201 carrying a body just as readily, because the write is an upsert', () => {
      arriveAndChoose();

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0), 'the assignment');

      call.flush(envelope(null), { status: 201, statusText: 'Created' });
      fixture.detectChanges();

      answerMemberships(0, [membership()]);
      expectNoReprobe();

      expect(rows()).withContext('the success path was taken').toHaveSize(1);
      // The same one success sentence either way, because the screen inspects no status: a `201` and a
      // `204` are one outcome to an upsert, and they must read as one to the operator too.
      expect(notifications()).toEqual([
        { severity: 'success', message: 'Ada Lovelace was added to the Administrators role.' },
      ]);
    });

    it('states the mail reduction rather than offering a choice it cannot honour', () => {
      arriveAndChoose();

      const notify = query<HTMLInputElement>(`#${NOTIFY_CONTROL_ID}`);

      expect(notify).withContext('the notification choice is still shown').not.toBeNull();
      // ⚠ UNTICKED AND DISABLED, DEPARTING FROM THE MEASURED INITIAL STATE DELIBERATELY.
      // `securityroles.ascx:L49` declares `chkNotify` with `Checked="True"` and its true value reached
      // a routine that mailed the account holder (`SecurityRoles.ascx.vb:L542`). This migration
      // excludes the mail subsystem wholesale, so leaving it ticked would let an operator ask for a
      // notification, receive a success, and reasonably believe one had gone out.
      expect((notify as HTMLInputElement).checked).withContext('unticked').toBeFalse();
      expect((notify as HTMLInputElement).disabled).withContext('and not offered').toBeTrue();

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0));

      // The member is still TRANSMITTED — the contract declares it — carrying the truthful `false`. A
      // disabled control reports its value like any other, which is what keeps the request complete.
      expect((call.request.body as { notifyUser: boolean }).notifyUser).toBeFalse();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerMemberships(0, [membership()]);
      expectNoReprobe();
    });

    it('reports a refused assignment and does not re-read the list', () => {
      arriveAndChoose();

      press(ADD_USER_LABEL);

      expectRequest('POST', membersUrl(0)).flush(
        problem('role.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // Nothing changed, so there is nothing to re-read — and a not-found answer is a WARNING rather
      // than an error, the three-valued vocabulary the legacy screens used.
      httpMock.expectNone(() => true);
      expect(notifications()).toEqual([
        { severity: 'warning', message: 'The requested resource does not exist.' },
      ]);
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });

    /**
     * ⚠ THE KEYS ARE .NET MODEL-STATE KEYS AND ARE **NOT** CAMEL-CASED, so the fixture spells one in
     * Pascal case exactly as the server does. They are read with an INDEX EXPRESSION because the error
     * map is an index signature and `noPropertyAccessFromIndexSignature` is enabled — a dotted read
     * would not compile.
     */
    it('shows a per-field server message beside the field the server named', () => {
      arriveAndChoose();

      const messages: Readonly<Record<string, readonly string[]>> = {
        EffectiveDate: ['The effective date is not acceptable.'],
      };
      const document = problem('request.invalid', 400, 'One or more validation errors occurred.', {
        ...messages,
      });

      expect(document.errors?.['EffectiveDate']).toEqual(messages['EffectiveDate']);

      press(ADD_USER_LABEL);

      expectRequest('POST', membersUrl(0)).flush(document, {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      expect(fieldErrors()).toContain('The effective date is not acceptable.');
    });

    it('preserves the trace identifier when no correlation identifier is published', () => {
      arriveAndChoose();

      press(ADD_USER_LABEL);

      expectRequest('POST', membersUrl(0)).flush(
        tracedProblem('server.unexpected_failure', 500, 'Something went wrong.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // The shared reference prefers a correlation identifier and falls back to the trace one, so this
      // is the shape that proves the trace identifier survives to something quotable.
      expect(textOf('.error-banner__trace').join(' ')).toContain(TRACE_ID);
      expect(component().problem()?.traceId).toBe(TRACE_ID);
      expect(notifications()).toEqual([{ severity: 'error', message: 'Something went wrong.' }]);
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 6 — REMOVING A MEMBERSHIP: A `204` DOES NOT PROMISE THE ROW IS GONE
  // -------------------------------------------------------------------------------------------------

  describe('removing a membership', () => {
    it('asks first, then removes with a 204 and re-reads the list', () => {
      arrive(0, [membership({ userId: 42 })]);

      press(DELETE_LABEL);

      expect(query('.confirm-dialog')).withContext('the question is asked').not.toBeNull();
      expect(textIn(query('.confirm-dialog__message')).trim()).toBe(
        CONFIRM_REMOVAL_MESSAGE,
      );
      expect(query('.confirm-dialog')?.getAttribute('role')).toBe('alertdialog');
      expect(query('.confirm-dialog__button--danger'))
        .withContext('marked destructive')
        .not.toBeNull();
      httpMock.expectNone(() => true);

      pressDialogue(DELETE_LABEL);

      const call = expectRequest('DELETE', memberUrl(0, 42), 'the removal');

      // The membership is addressed by BOTH identities, because that pair IS the membership: the role
      // alone names everybody in it and the account alone names every role they hold. The legacy grid
      // declared `datakeyfield="UserRoleID"` at `securityroles.ascx:L56` and then OVERRODE it to
      // `"UserId"` at runtime (`SecurityRoles.ascx.vb:L244`), which is why the address is keyed by the
      // account while the row's own identity remains the assignment key.
      expect(call.request.url).toBe('/api/v1/roles/0/users/42');
      expect(query('.confirm-dialog')).withContext('no modal sits over a request').toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerMemberships(0, []);

      expect(rows()).withContext('the membership is gone').toHaveSize(0);
      // ⚠ THE REMOVAL IS ANNOUNCED. The row vanishing is the only other evidence, and a row vanishing
      // from a grid is exactly what a mis-click looks like; the sentence names WHO left WHICH role, and
      // it is composed from the row that was removed rather than from the rows that remain, because by
      // this point the removed row is gone from both the screen and the store.
      expect(notifications()).toEqual([
        { severity: 'success', message: 'Ada Lovelace was removed from the Administrators role.' },
      ]);
    });

    /**
     * ⚠⚠ THE SPECIFICATION AN OPTIMISTIC IMPLEMENTATION CANNOT PASS.
     *
     * `RoleController.vb:L493-L501` is the whole of it. `:L494` tests
     * `userRole IsNot Nothing AndAlso userRole.ServiceFee > 0.0 AndAlso userRole.IsTrialUsed`; when
     * that holds, `:L496` back-dates the expiry bound by one day and `:L497` calls
     * `provider.UpdateUserRole(userRole)` — AN UPDATE, NOT A REMOVAL, precisely so the trial-used fact
     * survives — and only otherwise does `:L500` call `DeleteUserRole`. The wire status is `204` either
     * way and carries no body to tell them apart.
     *
     * So the screen must ASK AGAIN rather than splice the row out locally: a membership that still
     * exists would vanish from the screen, which is a correctness defect and not a cosmetic one. The
     * second `GET` below is that proof, and `httpMock.verify()` in `afterEach` is what makes its
     * ABSENCE fail rather than pass.
     */
    it('re-reads after a 204 and still renders a membership the server merely EXPIRED', () => {
      const paid = membership({ userRoleId: 11, userId: 42, expiryDate: null });

      arrive(0, [paid]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      const call = expectRequest('DELETE', memberUrl(0, 42), 'the removal');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // THE SECOND READ. The server expired the assignment instead of deleting it, so it answers with
      // the same row carrying a bound already behind it.
      const expired = membership({
        userRoleId: 11,
        userId: 42,
        expiryDate: BACKDATED_EXPIRY_DATE,
      });

      answerMemberships(0, [expired]);

      expect(rows()).withContext('the row STILL EXISTS').toHaveSize(1);
      expect(component().assignments()[0]?.userRoleId).toBe(11);

      // ⚠ THE ASSERTION WAS `toBe(BACKDATED_EXPIRY_RENDERED)` AND IS NOW `toContain`, BECAUSE R-M24
      // ADDED A QUALIFIER TO EXACTLY THIS CASE — and that makes the case stronger rather than weaker.
      // A back-dated bound is the clearest example of the state R-M24 exists to reveal: the row is
      // still present and the account no longer holds the role, which is a distinction the legacy grid
      // could not draw at all. The DATE is still asserted exactly, so the formatter is still pinned.
      expect(cellsOf(rows()[0])[3]).toContain(BACKDATED_EXPIRY_RENDERED);
      expect(cellsOf(rows()[0])[3])
        .withContext('a bound the server put behind us reads as lapsed, not as ordinary')
        .toContain('Expired');
    });

    it('sends nothing when the confirmation is dismissed', () => {
      arrive(0, [membership()]);

      press(DELETE_LABEL);
      pressDialogue(CANCEL_LABEL);

      expect(query('.confirm-dialog')).withContext('closed again').toBeNull();
      httpMock.expectNone(() => true);
      expect(notifications()).toHaveSize(0);
      expect(rows()).withContext('nothing was removed').toHaveSize(1);
    });

    /**
     * ⚠ THE ORDER IS LOAD-BEARING. `SecurityRoles.ascx.vb:L579-L580` sets `EditItemIndex = -1` and
     * calls `BindGrid()` UNCONDITIONALLY, and only then does `:L582-L584` raise the message. A refusal
     * was therefore read against a REFRESHED grid, so the message is deferred here until the re-read
     * has settled — asserted below by the announcement queue being empty before the read is answered
     * and populated afterwards.
     *
     * ⚠ AND THE SEVERITY IS A WARNING, NOT AN ERROR. `role_assignment.protected` is the one member of
     * the shared conflict vocabulary that arrives as `403` rather than `409` — the vocabulary records
     * that inline — and `problemSeverity` maps `403` to a warning. The legacy screen raised this
     * sentence as a red error at `:L583` while presenting an access refusal as a yellow warning
     * (`AccessDenied.ascx.vb:L43` and `:L45`); this API routes the refusal through the access status,
     * so the shared summariser's severity applies and the WORDING is what carries the legacy meaning.
     * Taking the severity from one place rather than hard-coding it here is what stops one decision
     * having two homes that can disagree.
     */
    it('re-reads BEFORE announcing a protected membership, with the legacy wording', () => {
      arrive(0, [membership({ userId: 42 })]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', memberUrl(0, 42)).flush(
        problem(
          'role_assignment.protected',
          403,
          'The portal administrator cannot be removed from the administrators role.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()).withContext('deferred until the list is back').toHaveSize(0);

      answerMemberships(0, [membership({ userId: 42 })]);

      // `RoleRemoveError.Text`, verbatim, published by the shared conflict vocabulary for this code.
      expect(notifications()).toEqual([{ severity: 'warning', message: REMOVAL_REFUSED_MESSAGE }]);
      expect(rows()).withContext('and the row is still there').toHaveSize(1);
    });

    it('surfaces a permission refusal at WARNING severity with the access wording', () => {
      arrive(7, [membership({ userId: 42, roleId: 7 })]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      // The same status WITHOUT the protected code, so the severity is proved to come from the status
      // while the wording is proved to come from the code.
      expectRequest('DELETE', memberUrl(7, 42)).flush(bareProblem('authorization.forbidden', 403), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      answerMemberships(7, [membership({ userId: 42, roleId: 7 })]);

      expect(notifications()).toEqual([{ severity: 'warning', message: FORBIDDEN_MESSAGE }]);
      // An access refusal teaches nothing about the pairing itself, so the affordance stays.
      expect(button(DELETE_LABEL)).withContext('still offered').not.toBeUndefined();
    });

    it('QUOTES the support reference the refusal document carried', () => {
      // ⚠ THIS SCREEN DEFERS ITS ANNOUNCEMENTS, AND THE DEFERRAL IS WHERE THE IDENTIFIER WAS LOST. A
      // message here is not raised when the failure is observed - it is described into a notice, held until
      // the re-read settles, and raised afterwards, so the notice is the only thing that travels. It carried
      // a severity and a sentence and nothing else, so the identifier the server recorded the refusal under
      // was discarded at the moment of description, before any call was made. The notice now carries it as a
      // required member, which is what makes a new producer state its answer rather than inherit silence.
      arrive(7, [membership({ userId: 42, roleId: 7 })]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', memberUrl(7, 42)).flush(
        problem(
          'authorization.forbidden',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      answerMemberships(7, [membership({ userId: 42, roleId: 7 })]);

      expect(announcementReferences())
        .withContext('the deferred notice carried the identifier through to the announcement')
        .toEqual([CORRELATION_ID]);
    });

    it('quotes NO reference when the refusal document carried none', () => {
      // The other half of the rule. A reference is quoted because the answer HAD one, never as decoration -
      // so a document without a correlation identifier must announce its outcome and quote nothing.
      // Without this case the one above could be satisfied by inventing an identifier, which would hand
      // support a reference it cannot find.
      arrive(7, [membership({ userId: 42, roleId: 7 })]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', memberUrl(7, 42)).flush(bareProblem('authorization.forbidden', 403), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      answerMemberships(7, [membership({ userId: 42, roleId: 7 })]);

      expect(notifications())
        .withContext('the refusal is still reported')
        .toEqual([{ severity: 'warning', message: FORBIDDEN_MESSAGE }]);
      expect(announcementReferences())
        .withContext('with nothing quoted, because there was nothing to quote')
        .toEqual([null]);
    });

    it('reports an unrelated fault at ERROR severity, and re-reads all the same', () => {
      arrive(7, [membership({ userId: 42, roleId: 7 })]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', memberUrl(7, 42)).flush(
        problem(
          'server.unexpected_failure',
          500,
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // The rebind is unconditional in the legacy handler, so it happens on this path too.
      answerMemberships(7, [membership({ userId: 42, roleId: 7 })]);

      expect(notifications()).toEqual([
        {
          severity: 'error',
          message: 'An unexpected error occurred while processing the request.',
        },
      ]);
    });

    it('withdraws the removal affordance from a pairing the server has refused', () => {
      arrive(0, [membership({ userId: 42 })]);

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', memberUrl(0, 42)).flush(
        problem('role_assignment.protected', 403, 'That membership is protected.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();
      answerMemberships(0, [membership({ userId: 42 })]);

      // Learning from the refusal rather than offering it again: nobody is invited to repeat an action
      // the server has already said it will refuse.
      expect(button(DELETE_LABEL)).withContext('the affordance is withdrawn').toBeUndefined();
    });

    /**
     * MIGRATION: this is `DeleteButtonVisible` from `SecurityRoles.ascx.vb:L360-L363`, bound per row at
     * `securityroles.ascx:L68` with the source's own note at `:L62` — "[DNN-4285] Hide the button if
     * the user cannot be removed from the role". It delegated to
     * `RoleController.CanRemoveUserFromRole`, whose whole body is one expression at
     * `RoleController.vb:L745`: a membership may not be removed when it is the designated
     * administrator's hold on the administrator role, nor when the role is the registered-users role.
     * The legacy source carried that rule TWICE, in two bodies with a comment at `:L743` admitting the
     * duplication.
     *
     * ⚠ THE AFFORDANCE IS ADVISORY AND THE SERVER IS AUTHORITATIVE, which is precisely why the refusal
     * cases above also exist: the tenant facts this rule needs are inputs, and when they are absent
     * the command is offered and the write is refused with a machine-readable code.
     *
     * ⚠ ASSERTED ON THE VISIBLE WORD — the global `cmdDelete.Text` — AND ON THE ELEMENT, never on a
     * CSS class. A class is a styling decision that may be renamed without changing behaviour.
     *
     * This comment used to call the assertion below an accessible-name check. It is not, and the
     * distinction became load-bearing once the command gained an `aria-label`: `textContent` is the
     * PAINTED word, and an `aria-label` replaces the accessible name without touching it. Both are
     * contracts and both are now asserted — the painted word here, the accessible name in the test that
     * follows.
     */
    it('hides the command on the protected row of a two-row listing, and keeps the other', () => {
      arrive(
        0,
        [
          membership({ userRoleId: 11, userId: 42, displayName: 'Ada Lovelace' }),
          membership({
            userRoleId: 12,
            userId: 7,
            username: 'grace',
            displayName: 'Grace Hopper',
          }),
        ],
        { administratorUserId: 42, administratorRoleId: 0 },
      );

      expect(rows()).withContext('both rows are painted').toHaveSize(2);

      const commands: readonly string[] = rows().map((row) =>
        textIn(row.querySelector('button')).trim(),
      );

      // Row one is the designated administrator's hold on the administrators role; row two is ordinary.
      expect(commands).toEqual(['', DELETE_LABEL]);
      expect(component().canRemove(component().assignments()[0])).toBeFalse();
      expect(component().canRemove(component().assignments()[1])).toBeTrue();
    });

    it('names each removal command after the account it removes, so two are never confusable', () => {
      /*
       * ⚠ THE DEFECT THIS PINS WAS MEASURED FROM THE ACCESSIBILITY TREE, not inferred. Both removal
       * commands on the fixture computed the byte-identical name "Delete", with `aria-label`, `title` and
       * `aria-describedby` all null, so a screen-reader user heard "Delete, button ... Delete, button" and
       * could not tell which membership each one ended — on an action that cannot be undone.
       *
       * This application already names row commands after their subject everywhere else it renders one: the
       * roles listing computes "Delete Administrators", the portals listing "Delete FIX010 Verify Portal".
       * This screen was the exception, which made it an inconsistency rather than a considered choice, and
       * the review named the class explicitly — two control pairs sharing one label in a way an automated
       * checker cannot detect.
       *
       * THE PAINTED WORD MUST NOT CHANGE. The legacy button read the global `cmdDelete.Text` and still
       * does; only the accessible name is qualified. Both halves are asserted, because a fix that silently
       * rewrote the visible label would be a different and unwanted change.
       */
      arrive(
        0,
        [
          membership({ userRoleId: 11, userId: 42, displayName: 'Ada Lovelace' }),
          membership({
            userRoleId: 12,
            userId: 7,
            username: 'grace',
            displayName: 'Grace Hopper',
          }),
        ],
        {},
      );

      const commands: readonly (HTMLElement | null)[] = rows().map((row) =>
        row.querySelector<HTMLElement>('button'),
      );

      expect(commands).toHaveSize(2);

      const names: readonly string[] = commands.map(
        (command) => command?.getAttribute('aria-label') ?? '',
      );

      expect(names).toEqual([`${DELETE_LABEL} Ada Lovelace`, `${DELETE_LABEL} Grace Hopper`]);

      // The point of the change, stated as its own assertion rather than left implicit in the pair above.
      expect(names[0]).not.toBe(names[1]);

      // ...and the painted word is untouched on both, so nothing about the row looks different.
      for (const command of commands) {
        expect(textIn(command).trim()).toBe(DELETE_LABEL);
      }
    });

    it('withholds the command for the registered-users role', () => {
      // Every authenticated account holds this role, so removing anybody from it would take their
      // authentication away; the legacy rule refused it for the same reason.
      arrive(1, [membership({ userId: 42, roleId: 1 })], { registeredRoleId: 1 });

      expect(button(DELETE_LABEL)).withContext('withheld for registered users').toBeUndefined();
    });

    /**
     * ⚠ R-M24: THE WHOLE COLUMN GOES, NOT JUST THE COMMANDS INSIDE IT.
     *
     * `RoleController.vb:L745` forbids removal from the registered-users role for EVERY account, so
     * leaving the column in place produced a heading over a hundred and twenty empty cells — measured
     * at runtime. The heading is CLIPPED rather than painted, so a sighted reader saw an unexplained
     * empty track while a screen-reader user was told the listing has a `Delete` column that never
     * holds a command.
     *
     * Withholding a column outright is THIS SCREEN'S OWN LEGACY BEHAVIOUR:
     * `SecurityRoles.ascx.vb:L245` is `Columns(2).Visible = False`, dropping the security-role column
     * in exactly this mode because every row belonged to the one role already named in the heading.
     */
    it('emits no removal column at all on a role whose memberships cannot be removed', () => {
      arrive(1, [membership({ userId: 42, roleId: 1 })], { registeredRoleId: 1 });

      const headers: readonly string[] = queryAll<HTMLTableCellElement>(
        'th.data-table__header',
      ).map((cell) => textIn(cell).trim());

      expect(headers)
        .withContext('three columns, and no heading for a command that cannot exist')
        .toEqual(['User Name', 'Effective Date', 'Expiry Date']);
      expect(headers).not.toContain(DELETE_LABEL);
      expect(cellsOf(rows()[0])).toHaveSize(3);
    });

    /**
     * ⚠ AND THE ABSENCE IS EXPLAINED. An operator who has removed accounts from every other role
     * would otherwise find the affordance simply gone, with nothing stating whether that is a rule or
     * a fault. The sibling role editor states the reason for its own protected-role state on exactly
     * this footing, because the legacy disabled its controls silently and left a sighted user with
     * greyed fields and no reason for them.
     */
    it('states why no account can be removed from that role', () => {
      arrive(1, [membership({ userId: 42, roleId: 1 })], { registeredRoleId: 1 });

      const notice: Element | null = query('p.role-assignment__notice');

      expect(notice).withContext('the reason is stated, not left to be inferred').not.toBeNull();
      expect(textIn(notice)).toContain('cannot be removed from this role');
    });

    /**
     * ⚠ AND THE EXPLANATION IS THE ONLY THING FROM THAT BLOCK THAT REACHES THE PAGE.
     *
     * The authoring note that justifies the withheld command sat OUTSIDE any comment delimiter in this
     * template — the note above it closed its own delimiter before the paragraph began — so seven lines
     * of commentary rendered as end-user copy on every visit: the ⚠ glyph, a back-ticked legacy source
     * path with a line number, and the name of a private member of this component. Runtime testing read
     * it off the screen, and it is exactly the class of internal detail this project's own rules forbid
     * putting in front of a user.
     *
     * The case is written against the SIGNATURES rather than against the paragraph, so it fails for any
     * future note that escapes its delimiter in the same way rather than only for this one.
     */
    it('renders no authoring commentary from that block as page copy', () => {
      arrive(1, [membership({ userId: 42, roleId: 1 })], { registeredRoleId: 1 });

      const painted: string = textIn(host());

      expect(painted).withContext('no warning glyph reaches the page').not.toContain('⚠');
      expect(painted).withContext('no legacy source path reaches the page').not.toContain('.vb:L');
      expect(painted).withContext('no code citation punctuation reaches the page').not.toContain('`');
      expect(painted)
        .withContext('no private member name reaches the page')
        .not.toContain('removalAvailable');
    });

    /**
     * ⚠ THE COLUMN MUST SURVIVE A ROLE WHERE ONLY *ONE* ROW IS PROTECTED. The designated-administrator
     * rule withholds a single command on the administrators role, so the column still has commands and
     * must still be emitted — and the notice must NOT appear, because removal is available there.
     * Conflating the row-level rule with the role-level one would take the affordance away from every
     * other member of that role.
     */
    it('keeps the column and stays silent when only one row is protected', () => {
      arrive(
        0,
        [membership({ userId: 1, roleId: 0 }), membership({ userId: 42, roleId: 0 })],
        { administratorUserId: 1, administratorRoleId: 0, registeredRoleId: 1 },
      );

      expect(cellsOf(rows()[0]))
        .withContext('four columns: the command column is still emitted')
        .toHaveSize(4);
      expect(query('p.role-assignment__notice')).toBeNull();
      expect(button(DELETE_LABEL)).withContext('the unprotected row still offers it').not
        .toBeUndefined();
    });

    it('offers the command when no protection applies', () => {
      arrive(7, [membership({ userId: 42, roleId: 7 })], {
        administratorUserId: 1,
        administratorRoleId: 0,
        registeredRoleId: 1,
      });

      expect(button(DELETE_LABEL)).withContext('offered').not.toBeUndefined();
    });

    it('ASKS FOR THE TENANT\u2019S OWN RECORD on arrival, for the caller\u2019s tenant', () => {
      // ⚠ THE FACTS ARE READ, NOT AWAITED FROM A CALLER. They were three optional inputs that nothing
      // in the application supplied, so this guard shipped permanently disarmed. The tenant comes from
      // the caller's identity because this screen addresses a ROLE and names no portal, and the key is
      // passed through untouched: `Portals.PortalID` is `IDENTITY(-1, 1)`, so -1 and 0 are both real
      // tenants and a truthiness test would skip the request for either.
      arrive(0, [membership({ userId: 42 })]);

      expect(loadCurrentPortalContext).toHaveBeenCalledWith(TENANT_ID);
      expect(loadCurrentPortalContext).toHaveBeenCalledTimes(1);
    });

    it('OFFERS the command while the tenant record is still outstanding, deferring to the API', () => {
      // ⚠ THE FAIL-SAFE DIRECTION, AND IT IS DELIBERATE. Until the record arrives each key is absent,
      // every comparison is false, the command is offered and the server's refusal governs — exactly
      // the behaviour that shipped. Withholding it until the read completed would take a capability
      // away from every row for the duration of a request.
      arrive(0, [membership({ userId: 42, roleId: 0 })]);

      expect(button(DELETE_LABEL))
        .withContext('nothing on the membership itself discriminates it, so nothing is guessed')
        .not.toBeUndefined();
    });

    it('ARMS the guard as the tenant record arrives, without the screen being remounted', () => {
      // The record arrives after the grid is already painted, which is the ordinary sequence: the
      // request is issued on construction and answers a moment later. `canRemove` reads the store's
      // signals, so the transition needs no reload and no second visit.
      arrive(0, [membership({ userId: 42, roleId: 0 })]);

      expect(button(DELETE_LABEL)).not.toBeUndefined();

      designatedAdministrator.set(42);
      administratorRole.set(0);
      fixture.detectChanges();

      expect(button(DELETE_LABEL))
        .withContext('withdrawn the moment the tenant names this pairing as its administrator\u2019s')
        .toBeUndefined();
    });

    it('protects the pairing whose ROLE key is nought, which the identity seed makes real', () => {
      // ⚠ `Roles.RoleID` is `IDENTITY(0, 1)`, so the administrator role of a freshly created tenant
      // genuinely carries nought — and a guard that tested either side for truthiness would leave
      // exactly that pairing unprotected.
      arrive(0, [membership({ userId: 42, roleId: 0 })], {
        administratorUserId: 42,
        administratorRoleId: 0,
      });

      expect(button(DELETE_LABEL)).toBeUndefined();
    });

    it('needs BOTH halves of the pairing, so the same account in another role is removable', () => {
      // The rule is a PAIRING and not an account: `RoleController.vb:L745` protects the designated
      // administrator's hold on the ADMINISTRATOR role, and nothing else about that account.
      arrive(7, [membership({ userId: 42, roleId: 7 })], {
        administratorUserId: 42,
        administratorRoleId: 0,
      });

      expect(button(DELETE_LABEL))
        .withContext('the administrator\u2019s membership of an ORDINARY role is removable')
        .not.toBeUndefined();
    });

    /**
     * MIGRATION: the row command declared `causesvalidation="False"` (`securityroles.ascx:L65`), so a
     * removal had to work while the add form was invalid and had to leave the form untouched — the
     * submit action was the ONLY validating affordance, declaring `CausesValidation="true"` at `:L41`.
     * The command is therefore declared outside the form element and typed as a plain button, so it
     * can never submit, never validate and never mark a field visited.
     */
    it('removes while the add form is INVALID, and marks no field visited', () => {
      arrive(0, [membership({ userId: 42 })]);

      // An inverted window: two perfectly valid dates whose ORDER breaks the group-level rule.
      typeDate(EFFECTIVE_DATE_CONTROL_ID, EXPIRY_DATE);
      typeDate(EXPIRY_DATE_CONTROL_ID, EARLIER_EXPIRY_DATE);

      expect(component().form.invalid).withContext('the form is failing').toBeTrue();
      expect(component().form.controls.effectiveDate.touched).toBeFalse();
      expect(component().form.controls.expiryDate.touched).toBeFalse();

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      const call = expectRequest('DELETE', memberUrl(0, 42), 'the removal');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerMemberships(0, []);

      // The removal went through, and it neither validated nor marked anything.
      expect(component().form.controls.effectiveDate.touched)
        .withContext('still unvisited')
        .toBeFalse();
      expect(component().form.controls.expiryDate.touched)
        .withContext('still unvisited')
        .toBeFalse();
      expect(fieldErrors()).withContext('and nothing was said about the form').toEqual([]);
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 7 — WHAT A ROW RENDERS: ESCAPING, SENTINELS AND ZERO-VALUED DATA
  // -------------------------------------------------------------------------------------------------

  describe('rendering a membership row', () => {
    /**
     * ⚠ `FormatUser` AT `SecurityRoles.ascx.vb:L394-L396` WAS A LIVE STORED-XSS SINK. It built an
     * anchor by string concatenation and interpolated the account's display name into that markup
     * UNENCODED, so a display name containing markup executed in the administrator's own browser. The
     * legacy tree already knew better in the same folder — `AccessDenied.ascx.vb:L43` wraps an
     * externally supplied message in an HTML encoder before showing it.
     *
     * TECHNIQUE USED FOR THE SECOND HALF: rather than reading the template file, this asserts against
     * the RENDERED anchor — its `innerHTML` is the ESCAPED form of its own text, which is only possible
     * if the binding was a plain interpolation. A markup-injecting binding would have produced an
     * `img` element instead. This file contains no markup-injecting binding, no sanitiser and no
     * trust-bypass of its own either.
     */
    it('escapes a hostile display name, parsing no element out of it', () => {
      const hostile = '<img src=x onerror="window.__roleAssignmentXss=true">Ann';

      arrive(0, [membership({ displayName: hostile })]);

      const link = query<HTMLAnchorElement>('tr.data-table__row a');

      expect(link).withContext('the member is still a link').not.toBeNull();

      const anchor = link as HTMLAnchorElement;

      // The characters arrived as CHARACTERS.
      expect(anchor.textContent).toBe(hostile);
      // No element was parsed out of them, anywhere on the screen.
      expect(query('img')).withContext('no element parsed out of a name').toBeNull();
      expect(queryAll('img')).toHaveSize(0);
      // And nothing ran.
      expect((window as unknown as Record<string, unknown>)['__roleAssignmentXss'])
        .withContext('never evaluated')
        .toBeUndefined();
      // The anchor's markup is the escaped form of its text, which is what a plain interpolation
      // produces and what a markup-injecting binding could not.
      expect(anchor.innerHTML).toContain('&lt;img');
      expect(anchor.innerHTML).not.toContain('<img');
    });

    /**
     * ⚠ THE LEGACY DATE SENTINEL RENDERS EMPTY, AND NEVER AS A DATE IN THE YEAR ONE.
     *
     * `SecurityRoles.ascx.vb:L377-L383` returned an empty string when `Null.IsNull` reported the value
     * unset and a short date otherwise. `Null.vb:L66-L70` defines that sentinel as `Date.MinValue`, and
     * `:L222-L224` compares `objDate.Date.Equals(NullDate.Date)` — THE DATE PART ALONE, with `GetNull`
     * noting at `:L183-L187` that this "avoids subtle time differences".
     */
    it('renders the date sentinel as an empty cell', () => {
      arrive(0, [membership({ effectiveDate: SENTINEL_DATE, expiryDate: SENTINEL_DATE })]);

      const cells = cellsOf(rows()[0]);

      expect(cells[2]).withContext('the effective bound').toBe('');
      expect(cells[3]).withContext('the expiry bound').toBe('');
      // Stated as prohibitions too, because these are the two things a permissive parser produces.
      expect(cells.join('|')).not.toContain(SENTINEL_DATE_MISRENDERED);
      expect(cells.join('|')).not.toContain(INVALID_DATE_TEXT);
      expect(cells.join('|')).not.toContain('0001');
    });

    /**
     * ⚠ R-M24: A LAPSED MEMBERSHIP SAYS SO, AND A PENDING ONE SAYS SO.
     *
     * Neither `securityroles.ascx` nor `SecurityRoles.ascx.vb` compares either bound against the clock
     * anywhere, so a membership that lapsed years ago, one that begins years hence and one in force
     * today were drawn identically — same colour, same weight, no other mark — on the one screen whose
     * purpose is administering who holds a role. Runtime testing measured exactly that.
     * `Website/admin/Users/MemberServices.ascx.vb:L172-L186` is the in-scope screen that DOES own a
     * clock and it supplies both the test and the word `Expired`.
     *
     * The bounds are set relative to the moment the component captured, so the case cannot drift with
     * the calendar. THE DATE IS STILL PAINTED: the qualifier is added beside it, not in place of it,
     * because an administrator needs to know WHEN a membership lapsed as well as that it has.
     */
    it('qualifies a lapsed membership and leaves its date intact', () => {
      const lastYear = new Date(Date.now() - 400 * 24 * 60 * 60 * 1000).toISOString();

      arrive(0, [membership({ effectiveDate: null, expiryDate: lastYear })]);

      const cells = cellsOf(rows()[0]);

      expect(cells[3]).withContext('the qualifier joins the date, never replaces it').toContain(
        'Expired',
      );
      expect(cells[3]).withContext('and the date itself survives').toMatch(/\d/);
      expect(cells[2]).withContext('the effective bound claims nothing').toBe('');
    });

    it('qualifies a membership whose effective bound has not yet arrived', () => {
      const nextYear = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000).toISOString();

      arrive(0, [membership({ effectiveDate: nextYear, expiryDate: null })]);

      const cells = cellsOf(rows()[0]);

      expect(cells[2]).toContain('Pending');
      expect(cells[3]).withContext('the expiry bound claims nothing').toBe('');
    });

    /**
     * ⚠ THE QUALIFIERS MUST NOT WIDEN INTO AN ORDINARY MEMBERSHIP. Most memberships are in force and
     * carry no bounds at all; a mark on those would be noise on every row and would train a reader to
     * ignore it on the rows that matter. A membership with NO bounds is `current` by definition — it
     * has neither lapsed nor is it waiting to start.
     */
    it('marks a membership in force with nothing at all', () => {
      const lastYear = new Date(Date.now() - 400 * 24 * 60 * 60 * 1000).toISOString();
      const nextYear = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000).toISOString();

      arrive(0, [
        membership({ userId: 1, effectiveDate: null, expiryDate: null }),
        membership({ userId: 2, effectiveDate: lastYear, expiryDate: nextYear }),
      ]);

      for (const row of rows()) {
        const cells = cellsOf(row);

        expect(cells.join('|')).not.toContain('Expired');
        expect(cells.join('|')).not.toContain('Pending');
      }
    });

    /**
     * ⚠ A SENTINEL BOUND MUST NOT ACQUIRE A QUALIFIER. `Null.vb` spells an unset date as
     * `Date.MinValue`, which is in the year one and therefore very much in the past — so a naive clock
     * comparison would stamp `Expired` on every membership with no expiry at all, which is the exact
     * opposite of what an unset bound means. Both the emptiness and the qualifier are decided by the
     * SAME parser, which is what makes this impossible rather than merely unlikely.
     */
    it('never qualifies a bound the cell renders as empty', () => {
      arrive(0, [membership({ effectiveDate: SENTINEL_DATE, expiryDate: SENTINEL_DATE })]);

      const cells = cellsOf(rows()[0]);

      expect(cells[2]).toBe('');
      expect(cells[3]).toBe('');
      expect(cells.join('|')).not.toContain('Expired');
      expect(cells.join('|')).not.toContain('Pending');
    });

    it('renders a sentinel carrying a NON-ZERO TIME as an empty cell as well', () => {
      arrive(
        0,
        [membership({ effectiveDate: SENTINEL_DATE_WITH_TIME, expiryDate: SENTINEL_DATE_WITH_TIME })],
      );

      const cells = cellsOf(rows()[0]);

      // The date-part-only rule, which is what stops a stored time of day turning an unset bound into
      // a visible one.
      expect(cells[2]).toBe('');
      expect(cells[3]).toBe('');
      expect(cells.join('|')).not.toContain(SENTINEL_DATE_MISRENDERED);
      expect(cells.join('|')).not.toContain(INVALID_DATE_TEXT);
    });

    it('renders an absent bound as an empty cell, identically to the sentinel', () => {
      arrive(0, [membership({ effectiveDate: null, expiryDate: null })]);

      const cells = cellsOf(rows()[0]);

      // Two different absences on the wire and one rendering, because to a person they mean the same
      // thing. The wire still distinguishes them; the erasure is confined to the display layer.
      expect(cells[2]).toBe('');
      expect(cells[3]).toBe('');
    });

    /**
     * ⚠ THE INVERSE, AND IT MATTERS AS MUCH. `RoleController.vb:L542` maps the `'O'` billing code to
     * `New System.DateTime(9999, 12, 31)` — a real, stored, PERPETUAL expiry — as against `:L541` where
     * `'N'` maps to `Null.NullDate`. Blanking the far-future value as though it were a sentinel would
     * hide a genuine bound.
     */
    it('renders the far-future perpetual bound NORMALLY', () => {
      arrive(0, [membership({ effectiveDate: EXPIRY_DATE, expiryDate: PERPETUAL_DATE })]);

      const cells = cellsOf(rows()[0]);

      // Machine-independent because `LOCALE_ID` is pinned in the harness above.
      expect(cells[2]).toBe(EXPIRY_DATE_RENDERED);
      expect(cells[3]).toBe(PERPETUAL_DATE_RENDERED);
    });

    /**
     * ⚠ NOUGHT IS A REAL, FREE PRICE. `RoleController.vb:L494` discriminates on `ServiceFee > 0.0`, and
     * the numeric sentinel is `Single.MinValue` (`Null.vb:L51-L55`) — NOT nought. A coercion such as
     * A coalescing or disjunctive default onto nought would be indistinguishable from a genuine free
     * role here, and no absent-value shorthand of any kind appears anywhere in this file.
     */
    it('preserves a zero service fee, a zero trial fee and their zero periods', () => {
      create('0');
      answerRole(
        role(0, {
          serviceFee: 0,
          trialFee: 0,
          billingPeriod: 0,
          trialPeriod: 0,
          billingFrequency: 'N',
          trialFrequency: 'N',
        }),
      );
      answerMemberships(0, []);

      const subject = component().role();

      expect(subject).not.toBeNull();
      expect(subject?.serviceFee).withContext('nought, not null').toBe(0);
      expect(subject?.trialFee).toBe(0);
      expect(subject?.billingPeriod).toBe(0);
      expect(subject?.trialPeriod).toBe(0);
      // The single-character codes ARE the contract — they are the bytes stored in two `char(1)`
      // columns — so they travel verbatim and are never mapped to a display label.
      expect(subject?.billingFrequency).toBe('N');
      expect(subject?.trialFrequency).toBe('N');
      // And a genuinely absent grouping stays absent rather than becoming nought.
      expect(subject?.roleGroupId).toBeNull();
    });

    it('links each member to their account rather than repeating the name as plain text', () => {
      arrive(0, [membership({ userId: 42 })]);

      const link = query<HTMLAnchorElement>('tr.data-table__row a');

      expect(link).withContext('the member is a link').not.toBeNull();
      // A router link is a URL and not an import, so no feature reaches into another here.
      expect((link as HTMLAnchorElement).getAttribute('href')).toBe('/users/42');
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 8 — PARITY GUARDS: WHAT THIS SCREEN MUST *NOT* GROW
  //
  // Each case below asserts an ABSENCE, which is the only way a reduction stays reduced. A future
  // editor adding any of these would be adding something the legacy screen did not have.
  // -------------------------------------------------------------------------------------------------

  // -------------------------------------------------------------------------------------------------
  // THE ACCOUNT LOOKUP UNDER SUPERSESSION
  //
  // The lookup is the one read this screen issues itself, and it is issued from a text box the
  // operator retypes. Three ways it went wrong, all invisible from a single interaction:
  //
  //   - the matches for the PREVIOUS term stayed on screen for the whole of the next lookup, so the
  //     offered list could disagree with the box that produced it — and choosing from it prefilled
  //     the enrolment form from an account the operator was no longer looking for;
  //   - a refusal from a superseded lookup stayed rendered beside a lookup that had not failed;
  //   - the addressed role could change while a lookup was outstanding, and the answer then
  //     populated the list under a role it was never asked about.
  //
  // Cancellation alone does not close the last two: a response already scheduled to commit is not
  // recalled by releasing its handle, which is why the callbacks are fenced by generation as well.
  // -------------------------------------------------------------------------------------------------

  describe('the account lookup under supersession', () => {
    it('discards the previous term\u2019s matches at dispatch rather than on arrival', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);

      expect(component().userMatches()).toHaveSize(1);

      // The next term is dispatched and NOT answered. The previous matches must already be gone: for
      // as long as they are rendered, the offered list contradicts the box above it.
      lookUp('bab');

      expect(component().userMatches())
        .withContext('the list shows matches for the term on screen, or nothing')
        .toHaveSize(0);

      answerLookup([]);
    });

    it('clears a previous lookup\u2019s refusal at the next dispatch', () => {
      arrive(0, []);

      lookUp('ada');
      expectLookupRequest('the account lookup').flush(
        problem('users.unavailable', 503, 'The directory is unavailable.'),
        { status: 503, statusText: 'Service Unavailable' },
      );
      fixture.detectChanges();

      expect(component().problem()).not.toBeNull();

      lookUp('bab');

      expect(component().problem())
        .withContext('a refusal from a superseded lookup is not a refusal of this one')
        .toBeNull();

      answerLookup([]);
    });

    it('refuses a superseded answer that arrives after the term moved on', () => {
      // ⚠ THE ORDER IS THE POINT. Both lookups are open, and the FIRST answers LAST. Nothing about
      // the network guarantees otherwise, and the wider match set must not land under the narrower
      // term.
      arrive(0, []);

      lookUp('a');

      const wide = expectLookupRequest('the wide lookup');

      lookUp('ada');

      const narrow = expectLookupRequest('the narrow lookup');

      expect(wide.cancelled)
        .withContext('the superseded lookup is abandoned, not merely ignored')
        .toBeTrue();

      narrow.flush(pageOf([account()], 1, 0, 10));
      fixture.detectChanges();

      expect(component().userMatches()).toHaveSize(1);
      expect(component().userLookupLoading()).toBeFalse();
    });

    it('refuses a superseded refusal, so the banner speaks for the current term only', () => {
      arrive(0, []);

      lookUp('a');

      const wide = expectLookupRequest('the wide lookup');

      lookUp('ada');

      const narrow = expectLookupRequest('the narrow lookup');

      narrow.flush(pageOf([account()], 1, 0, 10));
      fixture.detectChanges();

      // The superseded lookup is already released, so its refusal cannot be delivered at all. This
      // states the outcome the release exists to produce.
      expect(wide.cancelled).toBeTrue();
      expect(component().problem()).toBeNull();
      expect(component().userMatches()).toHaveSize(1);
    });

    it('abandons an outstanding lookup when the addressed role changes', () => {
      // ⚠ THE ROUTE REUSES ONE COMPONENT INSTANCE, so moving between roles runs the reset while a
      // lookup started under the previous role may still be outstanding. Left alone it landed under
      // the new role, and choosing from it prefilled the enrolment form from a membership of the
      // OTHER role.
      arrive(0, []);

      lookUp('ada');

      const stale = expectLookupRequest('the lookup under the first role');

      fixture.componentRef.setInput('roleId', '1');
      fixture.detectChanges();

      expect(stale.cancelled)
        .withContext('a lookup for the role being left is abandoned')
        .toBeTrue();
      expect(component().userMatches()).toHaveSize(0);
      expect(component().userLookupLoading()).toBeFalse();

      // The role change starts its own two reads, answered here so nothing is left outstanding.
      answerRole(role(1));
      answerMemberships(1, []);
    });
  });

  // -------------------------------------------------------------------------------------------------
  // WHOSE WRITE SETTLED
  //
  // The store is provided at the application root, so every role write in the application used to
  // settle the same aggregate flag this screen watched. Watching it fall released this screen's
  // enrolment lock and consumed its outstanding-removal marker whenever an UNRELATED write finished:
  // a second enrolment could be submitted while the first was still in the air, and a refusal
  // arriving afterwards had no marker left to be attributed to.
  // -------------------------------------------------------------------------------------------------

  describe('whose write settled', () => {
    it('stays held when an unrelated role write settles', () => {
      arriveAndChoose();

      press(ADD_USER_LABEL);

      const enrolment = httpMock.expectOne(
        (candidate) => candidate.method === 'POST' && candidate.url === membersUrl(0),
        'the enrolment',
      );

      expect(component().saving()).withContext('our write is open').toBeTrue();

      // A sibling screen's write, dispatched straight at the shared store and settled while ours is
      // still in the air. Ours must remain held and nothing must be announced.
      const store = TestBed.inject(RoleStore);

      store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });
      httpMock
        .expectOne(
          (candidate) => candidate.method === 'POST' && candidate.url === ROLE_GROUPS_URL,
          'the sibling write',
        )
        .flush(
          {
            data: {
              roleGroupId: 3,
              portalId: -1,
              roleGroupName: 'Paid Services',
              description: null,
            },
            meta: null,
          },
          { status: 201, statusText: 'Created' },
        );
      httpMock
        .expectOne(
          (candidate) => candidate.method === 'GET' && candidate.url === ROLE_GROUPS_URL,
          'the sibling re-read',
        )
        .flush({ data: [], meta: null });
      fixture.detectChanges();

      expect(component().saving())
        .withContext('another screen\u2019s write must not release our lock')
        .toBeTrue();

      enrolment.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(component().saving()).withContext('our own write releases it').toBeFalse();

      // The store re-reads the membership itself once the enrolment succeeds — the PAGE in hand,
      // and then the keyed probe for the chosen account, which is what moves the action's label.
      // Reading the role's whole membership was withdrawn, so both are needed and neither is a walk.
      answerMemberships(0, [membership()]);
      expectNoReprobe();
    });

    it('does not attribute an unrelated write\u2019s refusal to its own enrolment', () => {
      arriveAndChoose();

      press(ADD_USER_LABEL);

      const enrolment = httpMock.expectOne(
        (candidate) => candidate.method === 'POST' && candidate.url === membersUrl(0),
        'the enrolment',
      );

      const store = TestBed.inject(RoleStore);

      store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });
      httpMock
        .expectOne(
          (candidate) => candidate.method === 'POST' && candidate.url === ROLE_GROUPS_URL,
          'the sibling write',
        )
        .flush(problem('role_group.duplicate_name', 409, 'That group already exists.'), {
          status: 409,
          statusText: 'Conflict',
        });
      fixture.detectChanges();

      // The shared failure slot now holds a refusal that is not ours. Our enrolment then SUCCEEDS,
      // and nothing may be raised about it.
      enrolment.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // The one follow-up the success dispatches is settled: the page in hand. Leaving it outstanding
      // would fail verification rather than anything this case is about.
      answerMemberships(0, [membership()]);
      expectNoReprobe();

      // ⚠ WHAT THIS CASE IS ABOUT IS WHOSE OUTCOME IS REPORTED, not whether one is. The enrolment
      // announces its OWN success and says nothing whatever about the sibling role-group refusal that
      // is sitting in the shared failure slot - which is the misattribution this case exists to refuse.
      expect(notifySpy.calls.allArgs().map((args) => String(args[1])))
        .withContext('its own outcome, and nobody else\u2019s')
        .toEqual(['Ada Lovelace was added to the Administrators role.']);
      expect(notifySpy.calls.allArgs().map((args) => String(args[0])))
        .withContext('and reported as the success it was')
        .toEqual(['success']);
      expect(component().saving()).toBeFalse();
    });
  });


  describe('parity guards', () => {
    /**
     * ⚠ FOUR VISIBLE COLUMNS, NOT FIVE. `securityroles.ascx:L76` declares
     * `<asp:boundcolumn datafield="RoleName" headertext="SecurityRole" />` as the third column, but
     * `SecurityRoles.ascx.vb:L245` executes `grdUserRoles.Columns(2).Visible = False` in precisely the
     * role-centric mode this screen implements, because every row here belongs to the one role already
     * named in the heading. The mirrored branch at `:L252` hides the ACCOUNT column instead, which is
     * the out-of-scope account-centric mode.
     */
    it('renders four columns and NO security-role column', () => {
      arrive(0, [membership()]);

      const headers: readonly string[] = textOf('th.data-table__header');

      expect(headers).toEqual([DELETE_LABEL, USER_LABEL, 'Effective Date', 'Expiry Date']);
      expect(headers).not.toContain(SECURITY_ROLE_HEADER);
      expect(textIn(host())).not.toContain(SECURITY_ROLE_HEADER);
      expect(cellsOf(rows()[0])).toHaveSize(4);
    });

    /**
     * The legacy grid was UNPAGED — `securityroles.ascx:L56` declares no `AllowPaging`, no pager style
     * and no footer style — so a role whose members fit on one page offers NO STEP: there is nowhere to
     * step to, and four disabled buttons would say otherwise. What it does state is HOW MANY ACCOUNTS
     * HOLD THE ROLE, which is the one place on this screen that figure appears, and which runtime
     * testing found missing on every list because the count had been tied to navigability.
     */
    it('offers no step for a role whose members fit on one page, and states the membership count', () => {
      arrive(0, [membership()]);

      expect(component().pagerRequired()).withContext('nothing to move through').toBeFalse();
      expect(query('app-pagination')).withContext('mounted for its count').not.toBeNull();
      expect((query('.pagination__status')?.textContent ?? '').trim()).toContain('of 1');
      expect(queryAll('.pagination__button')).withContext('no steps').toHaveSize(0);
    });

    it('mounts no pager at all for a role with no members', () => {
      arrive(0, []);

      // Nothing to count, so nothing is mounted — not an emptied container and not a hidden one.
      expect(query('app-pagination')).withContext('not mounted').toBeNull();
      expect(query('.pagination')).toBeNull();
    });

    /**
     * ⚠ THE REGRESSION THIS PINS DOWN. An earlier revision read page zero at the widest legal size and
     * then fetched every further page the metadata reported, together, joining them into one set — a
     * burst of concurrent requests, the whole membership of a role retained in memory, and a DOM
     * proportional to it. ONE page is read instead, and the pager is what keeps the rest reachable, so
     * this case asserts both halves: exactly one request per read, and a pager that moves.
     */
    it('renders a pager when the role has more members than one page, and moves pages through it', () => {
      // Eleven members at ten to a page. The response's own metadata is what the pager is bound to.
      arrive(0, [membership()], {}, 11);

      expect(component().pagerRequired()).withContext('a further page exists').toBeTrue();
      expect(component().pageIndex()).toBe(0);
      expect(component().pageSize()).toBe(DEFAULT_PAGE_SIZE);
      expect(component().totalCount()).toBe(11);
      expect(query('app-pagination')).withContext('mounted').not.toBeNull();

      // No walk followed the first page: the read that arrived is the only one outstanding.
      httpMock.expectNone(
        (candidate) => candidate.method === 'GET' && candidate.url === membersUrl(0),
      );

      component().onPageChange(1);
      fixture.detectChanges();

      const second = httpMock.expectOne(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === membersUrl(0) &&
          candidate.params.get('query') === null,
        'the second page',
      );

      // The index travels as the pager reported it. No base conversion happens on either side.
      expect(second.request.params.get('pageIndex')).toBe('1');
      expect(second.request.params.get('pageSize')).toBe(String(DEFAULT_PAGE_SIZE));

      second.flush({
        items: [membership({ userRoleId: 12, userId: 43, displayName: 'Grace Hopper' })],
        meta: { totalCount: 11, pageIndex: 1, pageSize: DEFAULT_PAGE_SIZE, totalPages: 2 },
      });
      fixture.detectChanges();

      expect(component().pageIndex()).toBe(1);
      expect(rows()).toHaveSize(1);

      // Asking for the page already on screen asks the server nothing, so a repeated click cannot
      // re-issue a read.
      component().onPageChange(1);
      fixture.detectChanges();
    });

    /**
     * ⚠ NO ROLE PICKER. `SecurityRoles.ascx.vb:L183-L198` is the proof of what this mode rendered: it
     * put the single role into the dropdown at `:L191`, set the title from `RoleTitle.Text` at `:L193`,
     * and then hid BOTH the dropdown and its label at `:L195` and `:L196`. The role is fixed by the
     * route and named in the heading.
     */
    it('renders no role picker, whichever account control the tenant asked for', () => {
      arrive(0, [membership()]);

      // Under the name-box policy nothing on the screen is a dropdown at all, so counting them is
      // enough to prove the role picker is absent.
      expect(queryAll('select')).toHaveSize(0);
    });

    it('renders no role picker even when a dropdown IS rendered for the account', () => {
      // The sharper form of the guard above. The tenant's policy can ask for an account dropdown, so
      // "no `select` exists" stops proving anything about the ROLE picker — the count has to be one,
      // and the one has to be the account control. Named by its own class rather than by position,
      // because a second dropdown appearing anywhere would then fail this rather than shift an index.
      create('0', { usersControl: USERS_CONTROL_COMBO });
      answerRole(role(0));
      answerMemberships(0, [membership()]);
      answerAccountChoicesPage([account()]);

      const dropdowns = queryAll<HTMLSelectElement>('select');

      expect(dropdowns).toHaveSize(1);
      expect(dropdowns[0].classList).toContain('role-assignment__choices');
    });

    /**
     * ⚠ THE SORTABLE SET IS THE ENDPOINT'S, AND THIS SPEC USED TO ASSERT THE EMPTY SET ON A LEGACY GROUND
     * THAT DOES NOT DISTINGUISH THIS SCREEN.
     *
     * `securityroles.ascx:L56` declares no `AllowSorting`, which is true - and a case-insensitive census
     * across BOTH legacy trees finds the attribute exactly ONCE in either of them, in
     * `Website/admin/Files/filemanager.ascx`, a screen the AAP places out of scope. Not one in-scope legacy
     * grid could be reordered, INCLUDING the module listing that has offered sorting since it was written,
     * so the census says the same thing about every grid in this application.
     *
     * `SortableFields.RoleUsers` in `backend/src/DnnMigration.Application/Validation/SortableFields.cs`
     * bounds it instead: ACCOUNT fields only, and it records that the two assignment dates the projection
     * carries are DELIBERATELY excluded. So exactly one column offers a control - the account - and the two
     * dates must not, which is the half of this spec that still asserts an absence and matters most,
     * because a control on either would compose a name the boundary refuses. The commands column carries no
     * sort for a second, independent reason: the shared grid refuses a column that is both sortable and
     * heading-hidden.
     */
    it('offers a sort on the account column alone, and on neither assignment date', () => {
      arrive(0, [membership()]);

      const controls = queryAll<HTMLButtonElement>('button.data-table__sort');

      expect(controls).toHaveSize(1);
      // Named by its action while keeping the visible heading text, per WCAG 2.5.3.
      expect(controls[0].getAttribute('aria-label')).toBe('Sort by User Name');
      expect(controls[0].getAttribute('aria-label') ?? '').toContain(
        (controls[0].textContent ?? '').trim(),
      );

      // `aria-sort` appears on the sortable column and NOWHERE else: announcing "none" on an unsortable
      // column would claim it can be reordered.
      expect(queryAll('th[aria-sort]')).toHaveSize(1);
      expect(queryAll('th[aria-sort]')[0].getAttribute('aria-sort')).toBe('none');

      // Nothing is sorted yet, so no direction glyph is PAINTED - and the box that would hold one is
      // still there. ⚠ THE ELEMENT'S PRESENCE IS THE FIX, NOT A LEAK. The indicator used to be added
      // and removed with the ordering, which changed the heading's measure at the moment it was pressed
      // - Start Date jumped 70.859 -> 86.750 px - so a control could move out from under the finger that
      // pressed it. It is now always present at a fixed measure and EMPTY until there is a direction to
      // show, carrying `aria-hidden` so an empty box contributes nothing to the accessible name.
      const indicators = queryAll('.data-table__sort-indicator');

      expect(indicators).toHaveSize(1);
      expect((indicators[0].textContent ?? '').trim())
        .withContext('present, reserving its space, and painting nothing')
        .toBe('');
    });

    /**
     * ⚠ THE TRANSMITTED FIELD IS `DisplayName`, NOT THE COLUMN KEY `userName`. Both are members of the
     * permitted set, and they are DIFFERENT account fields: the cell renders the display name, so ordering
     * by the sign-in name would produce a sequence the reader cannot explain from the column in front of
     * them. Only a request-level assertion can tell the two apart, because either spelling looks correct in
     * the template and both are accepted by the server.
     */
    it('orders the membership listing by the field the column actually shows', () => {
      arrive(0, [membership()]);

      queryAll<HTMLButtonElement>('button.data-table__sort')[0].click();
      fixture.detectChanges();

      const ordered = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === membersUrl(0),
        'the ordered membership read',
      );

      expect(ordered.request.params.get('sortBy')).toBe('DisplayName');
      expect(ordered.request.params.get('sortDir')).toBe('Ascending');
      expect(ordered.request.params.get('pageIndex'))
        .withContext('a record page depends on the ordering')
        .toBe('0');

      ordered.flush(pageOf([membership()], 1, 0, DEFAULT_PAGE_SIZE));
      fixture.detectChanges();

      // The heading announces it, which proves the projection back from the stored field to the column key.
      expect(queryAll('th[aria-sort]')[0].getAttribute('aria-sort')).toBe('ascending');
    });

    /**
     * `SecurityRoles.ascx.vb:L329-L335` pointed two hyperlinks at a scripted pop-up calendar and gave
     * each a raster image with localised alternative text. The helper is a Web Forms client-script
     * facility with no counterpart here, the shared component set is closed and holds no date picker,
     * and no eleventh member may be added to it — so both bounds are native date controls and the two
     * raster assets are dropped with the pop-ups.
     */
    it('renders native date controls and no calendar pop-up affordance', () => {
      arrive(0, [membership()]);

      expect(query(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.getAttribute('type')).toBe('date');
      expect(query(`#${EXPIRY_DATE_CONTROL_ID}`)?.getAttribute('type')).toBe('date');
      // No image affordance anywhere: the native control brings its own picker.
      expect(queryAll('img')).toHaveSize(0);
    });

    /**
     * `ModuleHelp.Text` is `'<h1>About Manage Security Roles</h1><p>…</p>'` — untrusted markup with no
     * home in the closed shared component set. It was read for context and is deliberately not
     * rendered, so the fragment must not appear in the document.
     */
    it('renders no module help fragment', () => {
      arrive(0, [membership()]);

      expect(textIn(host())).not.toContain(MODULE_HELP_FRAGMENT);
      // And the screen contributes exactly one heading, since the shell owns the landmarks.
      expect(queryAll('h1')).toHaveSize(1);
      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
    });

    it('names the lookup with a real label pointing at the control it renders', () => {
      arrive();

      const label: HTMLLabelElement | undefined = queryAll<HTMLLabelElement>('label[for]').find(
        (candidate) => textIn(candidate).trim().startsWith(USER_LABEL),
      );

      expect(label).withContext('the lookup carries a real label').not.toBeUndefined();

      const target: string = attributeIn(label, 'for');

      // ⚠ THE ASSOCIATION POINTS AT THE SHARED LOOKUP'S OWN FIELD IDENTIFIER, because the lookup owns
      // the input it renders. Pointing at a name no element carries would be a dangling association,
      // which is worse than none at all.
      expect(query(`#${target}`)).withContext('the association resolves').not.toBeNull();
      // A placeholder is a hint and never an accessible name, which is why the label is mandatory.
      expect(query<HTMLInputElement>('input[type="search"]')?.placeholder).toBe(
        VALIDATE_PLACEHOLDER,
      );
    });

    it('offers the single module action as a link, because it navigates', () => {
      arrive();

      // `SecurityRoles.ascx.vb:L634` exposed exactly one module action, `Cancel.Action`, pointing at
      // the return address.
      const back: HTMLAnchorElement | undefined = queryAll<HTMLAnchorElement>('a').find(
        (candidate) => textIn(candidate).trim() === CANCEL_LABEL,
      );

      expect(back).withContext('the way back is a link').not.toBeUndefined();
      expect((back as HTMLAnchorElement).getAttribute('href')).toBe('/roles');
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      arrive();

      // Bound unconditionally: the banner owns an assertive live region, so wrapping it in control flow
      // would tear that region out of the accessibility tree between failures and lose the
      // announcement.
      expect(query('.error-banner-live')).withContext('the region exists').not.toBeNull();
      expect(query('.error-banner__title')).withContext('but says nothing').toBeNull();
    });
  });

  // -------------------------------------------------------------------------------------------------
  // PROOF 9 — CANCELLATION AND TEARDOWN
  //
  // ⚠ WHAT OUTLIVES THE SCREEN, AND WHAT MUST NOT. This screen owns exactly one request of its own -
  // the account lookup - and that one is abandoned when the screen goes away and when a newer term
  // supersedes it. The role read, the membership read and both membership writes belong to the SHARED
  // ROLE STORE, which is root-provided and deliberately outlives any one screen:
  //
  //   * a read whose answer populates shared state is still wanted by the sibling screens that read the
  //     same slices, so abandoning it because one screen closed would starve them; and
  //   * a WRITE must never be abandoned - the server may already have applied it, and cancelling in
  //     flight leaves the operator unable to say whether it took.
  //
  // Each case below therefore proves WHOSE request it is from that request's own cancelled state, and
  // settles the store-owned ones so the verification in `afterEach` still holds.
  // -------------------------------------------------------------------------------------------------

  describe('leaving the screen', () => {
    it('registers an unsaved-entry probe, so a part-completed enrolment is not discarded in silence', () => {
      /*
       * ⚠ THIS SCREEN'S ROUTE DECLARES `unsavedChangesGuard`, AND THE DECLARATION USED TO BE
       * ANSWERED BY REFLECTION over the component's fields. That sweep is gone - it made
       * `@angular/forms` reachable from the eager import graph of an application whose every form
       * screen is lazily loaded - so this screen registers a probe of its own. A screen declaring the
       * gate without one is not merely unprotected: it LOOKS protected in the route table, and the
       * guard reads it as clean.
       *
       * ⚠ WHAT IS AT STAKE ON THIS SCREEN IS MORE THAN A TYPED DATE. Above the enumeration ceiling
       * the account is reached by typing a login name and waiting on a lookup, so an operator who has
       * found the right account and set a bound has performed several separate acts of work that the
       * form is holding and the server knows nothing about.
       *
       * The probe reads THIS screen's own in-flight flag rather than the role store's, which is
       * raised by every role write in the application; reading the store's would let an unrelated
       * write elsewhere suppress this screen's warning. `isDirty()` is the guard's own public
       * surface, so this asserts through the very call the guard makes.
       */
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arrive(0, []);

      // THE CONTROL: an untouched form must not warn, or a later `true` proves nothing.
      expect(tracker.isDirty())
        .withContext('an untouched enrolment form is not unsaved entry')
        .toBeFalse();

      typeDate(EFFECTIVE_DATE_CONTROL_ID, '2026-03-01');

      expect(tracker.isDirty())
        .withContext('a set bound with no enrolment in flight is what the guard exists to catch')
        .toBeTrue();
    });

    /**
     * ⚠ ASSERTED ON EACH REQUEST'S OWN CANCELLED STATE, NOT BY COUNTING WHAT IS LEFT OPEN. Consuming
     * the pending requests in order to count them would REMOVE them from the controller, after which
     * both an emptiness assertion and the verification in `afterEach` would pass whether or not the
     * screen had abandoned anything. The state on the request itself is the only self-proving form.
     */
    it('leaves both opening reads to the store when the screen goes away', () => {
      create('0');

      const roleRead = expectRequest('GET', roleUrl(0), 'the role read');
      const listRead = httpMock.expectOne(
        (candidate) =>
          candidate.method === 'GET' &&
          candidate.url === membersUrl(0) &&
          candidate.params.get('query') === null,
        'the membership read',
      );

      expect(roleRead.cancelled).withContext('in flight').toBeFalse();
      expect(listRead.cancelled).withContext('in flight').toBeFalse();

      fixture.destroy();

      // Neither is this screen's to abandon: both were issued by the shared store, whose slices a
      // sibling screen reads too. The store cancels a read the moment a NEWER read supersedes it,
      // which is the supersession that matters, and clears everything on a session boundary.
      expect(roleRead.cancelled).withContext('still the store owns it').toBeFalse();
      expect(listRead.cancelled).withContext('still the store owns it').toBeFalse();

      // Settled here so nothing is left outstanding for the verification in `afterEach`.
      roleRead.flush(envelope(role(0)));
      listRead.flush(pageOf([], 0));
    });

    it('never abandons an outstanding write, even when the screen goes away', () => {
      arriveAndChoose();

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0), 'the assignment');

      expect(call.cancelled).toBeFalse();

      fixture.destroy();

      // ⚠ DELIBERATE, AND THE ONE CASE WHERE OUTLIVING THE SCREEN IS THE CORRECT BEHAVIOUR. The write
      // is the store's, and a write cannot be undone by hanging up on it: the server may already have
      // applied it, so cancelling would leave the operator unable to say whether the enrolment took.
      expect(call.cancelled).withContext('a write is never hung up on').toBeFalse();

      call.flush(null, { status: 204, statusText: 'No Content' });

      // ⚠ AND NO RE-READ FOLLOWS, WHICH IS A CORRECTION TO WHAT THIS CASE USED TO ASSERT. It expected
      // the store to re-read "screen or no screen, so that the slice a sibling reads reflects the
      // enrolment" — but there is no sibling: this screen is the only consumer of the membership
      // slice, and runtime validation caught the re-read going out for a grid that had already been
      // destroyed, three times out of three, on the ordinary path back through the role listing. It is
      // not a harmless spare round trip either, because the re-read clears the store's shared failure
      // slot and would erase a message the screen that replaced this one is showing.
      //
      // The write itself is still honoured to the end — that is what the assertions above pin — so
      // what changed is only who is told about it, not whether it completes. Destroying the screen
      // closes its claim on the listing through `RoleStore.closeAssignmentsView`.
      httpMock.verify();
    });

    it('abandons an outstanding account lookup when the screen goes away', () => {
      arrive(0, []);

      lookUp('ada');

      const call = expectLookupRequest('the account lookup');

      expect(call.cancelled).toBeFalse();

      fixture.destroy();

      expect(call.cancelled).withContext('abandoned').toBeTrue();
    });

    /**
     * A NEW read CANCELS the one it replaces, within one mounted screen. The addressed role, the page,
     * the search term and the chosen account all change while a read is in flight, and an answer that
     * arrived after its request stopped being the current one would overwrite newer state with older.
     */
    it('abandons the previous account lookup when a narrower term supersedes it', () => {
      arrive(0, []);

      lookUp('a');

      const first = expectLookupRequest('the first lookup');

      lookUp('ann');

      const second = expectLookupRequest('the narrower lookup');

      expect(first.cancelled).withContext('superseded').toBeTrue();
      expect(second.cancelled).withContext('current').toBeFalse();
      expect(lookupTerms(second)['query']).toBe('ann');

      second.flush(pageOf([], 0, 0, 10));
      fixture.detectChanges();
    });
  });
});



/**
 * Specification for the role-membership screen, centred on WHO OWNS ITS READS AND WRITES and on the
 * ORDER in which a refusal reaches the operator.
 *
 * This screen called the role transport directly for the role, the membership listing and both
 * membership writes, while the shared role store held its own copy of all three — so a removal
 * accepted here left a sibling screen's listing showing the row. Every read and both writes now go
 * through the store, and the listing is read ONE PAGE AT A TIME: the legacy grid declared no pager
 * (`securityroles.ascx:L56`), and a first port reproduced that by walking every page the server
 * reported and joining them, which turned one screen into a burst of concurrent requests and held a
 * role's whole membership in memory. The pager keeps every membership reachable instead.
 *
 * Each case fails for a different reason if the wiring regresses:
 *
 *   - the listing is read with exactly ONE request per read, and no further page is followed;
 *   - the store holds the rows, which is what a sibling screen reads;
 *   - a write is followed by exactly ONE re-read, issued by the store, and it re-reads the page the
 *     operator is standing on rather than throwing them back to the first;
 *   - a refused removal raises its message AFTER the listing has refreshed, which is the order
 *     `SecurityRoles.ascx.vb:L579-L584` fixes, and remembers the pairing so the command stops being
 *     offered;
 *   - the account lookup behind the search field does NOT disturb the shared account state.
 *
 * The role key is 0 throughout: `dbo.Roles.RoleID` is seeded `IDENTITY(0, 1)`, so zero is the
 * portal's first role and nothing may read it as absence.
 */
describe('RoleAssignmentComponent (store delegation)', () => {
  let fixture: ComponentFixture<RoleAssignmentComponent>;
  let component: RoleAssignmentComponent;
  let httpMock: HttpTestingController;
  let store: RoleStore;
  let notify: jasmine.Spy;

  /** The role the screen is opened on. */
  const ROLE: Role = {
    roleId: 0,
    roleGroupId: null,
    roleName: 'Subscribers',
    description: null,
    billingFrequency: 'N',
    serviceFee: 0,
    trialFrequency: 'N',
    trialPeriod: 0,
    billingPeriod: 0,
    trialFee: 0,
    isPublic: false,
    autoAssignment: false,
    rsvpCode: null,
    iconFile: null,
    concurrencyToken: 'revision-1',
  };

  /** One membership row. */
  function membership(userId: number, displayName: string): UserRole {
    return {
      userRoleId: userId + 100,
      userId,
      username: `user${String(userId)}`,
      displayName,
      roleId: 0,
      roleName: 'Subscribers',
      effectiveDate: null,
      expiryDate: null,
    };
  }

  /** The membership address, which both the read and the write use. */
  const MEMBERS_URL = API_ENDPOINTS.roles.forCurrentPortal.members(0);

  /**
   * Answers every outstanding membership PAGE read.
   *
   * The answer echoes the coordinate that was asked for and reports a total that may exceed the rows
   * it carries, which is how a listing says "there is another page". Returning the sizes requested is
   * what lets a case assert that exactly one request was made and at which size — the property an
   * earlier revision's page walk violated.
   *
   * @param rows The rows the page carries.
   * @param totalCount The total across every page, defaulting to the rows supplied.
   * @returns The page sizes the matched requests asked for, in the order they were matched.
   */
  function answerMembership(rows: readonly UserRole[], totalCount: number = rows.length): readonly string[] {
    const requests = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === MEMBERS_URL,
    );
    const askedFor = requests.map((request) => request.request.params.get('pageSize') ?? '');

    requests.forEach((request) => {
      const askedSize: number = Number(
        request.request.params.get('pageSize') ?? String(DEFAULT_PAGE_SIZE),
      );

      request.flush({
        items: rows,
        meta: {
          totalCount,
          pageIndex: Number(request.request.params.get('pageIndex') ?? '0'),
          pageSize: askedSize,
          totalPages: askedSize > 0 ? Math.ceil(totalCount / askedSize) : 0,
        },
      });
    });

    fixture.detectChanges();

    return askedFor;
  }

  /**
   * Renders the screen addressed at the role above and answers its role and account-policy reads.
   *
   * The account policy is answered with the NAME-BOX value, so this block exercises the account
   * lookup rather than the drop-down. Which control the tenant asks for is not what these cases are
   * about; that it is asked at all is why the read has to be settled here.
   *
   * ⚠ ANSWERED THROUGH THE SHARED FACTORY, not through a payload written out here. The policy
   * decoder requires every member, including the provenance flag that says whether the tenant stored
   * a policy at all, and a hand-written payload that omits one is REFUSED rather than partially
   * accepted. A refused policy reads as absent, which sends this screen down its fallback path and
   * issues a tenant-wide count probe no case here answers - measured, as nine failures in this block.
   */
  function render(): void {
    fixture = TestBed.createComponent(RoleAssignmentComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('roleId', '0');
    fixture.detectChanges();

    httpMock
      .match(
        (request) =>
          request.method === 'GET' && request.url === API_ENDPOINTS.roles.forCurrentPortal.byId(0),
      )
      .forEach((read) => read.flush({ data: ROLE }));

    httpMock
      .match(
        (request) => request.method === 'GET' && request.url === API_ENDPOINTS.users.membershipSettings(),
      )
      .forEach((read) => read.flush(envelope(membershipSettings())));

    fixture.detectChanges();
  }

  /** The wordings announced so far, paired with their severity. */
  function announcements(): readonly string[] {
    return notify.calls.allArgs().map(([severity, message]) => `${String(severity)}:${String(message)}`);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RoleAssignmentComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // The tenant context this screen now reads. Doubled to absent facts and a spied request,
        // because these cases are about what the screen delegates to the store and not about the
        // removal guard — and the real portal store would add a tenant read to every one of them.
        { provide: AuthStore, useValue: { currentUser: signal({ portalId: TENANT_ID }) } },
        {
          provide: PortalStore,
          useValue: {
            administratorUserId: signal<number | null>(null),
            administratorRoleId: signal<number | null>(null),
            registeredRoleId: signal<number | null>(null),
            loadCurrentPortalContext: jasmine.createSpy('loadCurrentPortalContext'),
          },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(RoleStore);
    notify = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    store.reset();
    httpMock.verify();
  });

  it('reads ONE page per read and follows no further page, however many the server reports', () => {
    render();

    // One request, at the shared default size.
    expect(answerMembership([membership(1, 'First')], 2)).toEqual([String(DEFAULT_PAGE_SIZE)]);

    // ⚠ THE REGRESSION THIS PINS DOWN. The answer reported a total of two against a page holding one,
    // which is a listing saying "there is more". An earlier revision took that as an instruction to
    // fetch every remaining page and join them; nothing further may be requested here, because the
    // pager is what reaches the rest and it does so one page at a time, when the operator asks.
    httpMock.expectNone((candidate) => candidate.method === 'GET' && candidate.url === MEMBERS_URL);

    expect(component.assignments().map((row) => row.displayName)).toEqual(['First']);
    expect(component.pagerRequired()).withContext('the pager reaches the rest').toBeFalse();
    expect(component.totalCount()).withContext("the SERVER's total is kept intact").toBe(2);
  });

  it('moves to another page through the store, with one request and no accumulation', () => {
    render();
    answerMembership([membership(1, 'First')], 24);

    component.onPageChange(2);
    fixture.detectChanges();

    const requests = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === MEMBERS_URL,
    );

    expect(requests).withContext('exactly one page read').toHaveSize(1);
    expect(requests[0].request.params.get('pageIndex')).toBe('2');

    requests[0].flush({
      items: [membership(2, 'Second')],
      meta: { totalCount: 24, pageIndex: 2, pageSize: DEFAULT_PAGE_SIZE, totalPages: 3 },
    });
    fixture.detectChanges();

    // The page REPLACES what was held; nothing is appended, so the slice never grows past one page.
    expect(component.assignments().map((row) => row.displayName)).toEqual(['Second']);
    expect(store.assignmentsPage().pageIndex).toBe(2);
  });

  it('leaves the rows in the store, which is what a sibling screen reads', () => {
    render();
    answerMembership([membership(1, 'First')]);

    expect(store.assignmentsRoleId()).toBe(0);
    expect(store.assignmentItems().map((row) => row.displayName)).toEqual(['First']);

    // The metadata the SERVER published is held as it stands - the coordinate that was applied and
    // the total across every page - because that is what a pager is bound to.
    expect(store.assignmentsMeta().pageIndex).toBe(0);
    expect(store.assignmentsMeta().pageSize).toBe(DEFAULT_PAGE_SIZE);
    expect(store.assignmentsMeta().totalPages).toBe(1);
  });

  it('writes an enrolment through the store and lets the store re-read the page in hand', () => {
    render();
    answerMembership([membership(1, 'First')]);

    component.form.controls.userId.setValue(2);
    component.submit();
    fixture.detectChanges();

    const written = httpMock.expectOne(
      (request) => request.method === 'POST' && request.url === MEMBERS_URL,
    );
    expect(written.request.body).toEqual({
      userId: 2,
      effectiveDate: null,
      expiryDate: null,
      // ⚠ FALSE, AND THE CHANGE IS THE POINT. The contract's member survives, but the choice that fed it
      // cannot be made: there is no mail endpoint, so the control is disabled and unticked and the
      // request no longer asks for a notification nothing can send.
      notifyUser: false,
    });

    written.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    // ONE re-read, issued by the store, at the same coordinate. A second read asked for here would
    // race the store's; a re-read that reset the index would throw the operator back to page one.
    expect(answerMembership([membership(1, 'First'), membership(2, 'Second')])).toEqual([
      String(DEFAULT_PAGE_SIZE),
    ]);

    expect(component.assignments().length).toBe(2);
    expect(store.assignmentsPage().pageIndex).toBe(0);
  });

  it('raises a refused removal only after the listing has refreshed, and remembers the pairing', () => {
    render();
    answerMembership([membership(1, 'First')]);

    const target = component.assignments()[0];
    expect(component.canRemove(target)).toBeTrue();

    component.requestRemoval(target);
    component.confirmRemoval();
    fixture.detectChanges();

    httpMock
      .expectOne(
        (request) =>
          request.method === 'DELETE' &&
          request.url === API_ENDPOINTS.roles.forCurrentPortal.member({ roleId: 0, userId: 1 }),
      )
      .flush(
        {
          type: 'urn:dnnmigration:error:role_assignment.protected',
          title: 'Forbidden',
          status: 403,
        },
        { status: 403, statusText: 'Forbidden' },
      );
    fixture.detectChanges();

    // NOTHING yet. The legacy rebound the grid unconditionally and raised the message only
    // afterwards, so the operator read it against a refreshed grid.
    expect(announcements()).toEqual([]);

    answerMembership([membership(1, 'First')]);

    // The legacy refusal WORDING, at the severity the shared summariser reads from the status.
    //
    // ⚠ WARNING, NOT ERROR, AND THE DIFFERENCE IS DELIBERATE. `role_assignment.protected` is the one
    // member of the shared conflict vocabulary that arrives as 403 rather than 409, and the summariser
    // maps every access-shaped status to a warning - which is how the legacy screens presented an
    // access refusal (`AccessDenied.ascx.vb:L43` and `:L45`). The published sentence is what carries the
    // legacy meaning here; taking the severity from the summariser rather than restating it is what
    // stops one decision having two homes that can disagree.
    expect(announcements()).toEqual([
      'warning:You Can Not Remove The Portal Administrator Or The Registered Users Role',
    ]);

    // The refused pairing is remembered, so the command corrects itself without another attempt.
    expect(component.canRemove(component.assignments()[0])).toBeFalse();
  });

  it('keeps the account lookup off the shared listing, because its matches belong to nobody else', () => {
    render();
    answerMembership([membership(1, 'First')]);

    component.onUserSearch('sec');
    fixture.detectChanges();

    const lookup = httpMock.expectOne(
      (request) => request.method === 'GET' && request.url === API_ENDPOINTS.users.choices(),
    );

    // The term travels RAW, because the picker matches on a prefix and a wildcard would be searched
    // for literally — the server escapes what it is given, so an operator's own per-cent sign matches
    // a per-cent sign.
    expect(lookup.request.params.get('query')).toBe('sec');

    // ⚠ AND IT ASKS THE PICKER RATHER THAN THE ACCOUNT LISTING, which is the assertion that keeps a
    // postal address, a telephone number, an electronic-mail address, two audit instants and four
    // status flags out of every answer this screen receives. The address IS the projection: nothing
    // else about the request distinguishes a three-member answer from a sixteen-member one.
    expect(lookup.request.url)
      .withContext('the picker answers a key and two captions; the listing answers a grid row')
      .not.toBe(API_ENDPOINTS.users.collection());

    lookup.flush({
      items: [],
      meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 },
    });
    fixture.detectChanges();

    // The membership slice is untouched by a lookup, and so is its page coordinate.
    expect(store.assignmentItems().length).toBe(1);
    expect(store.assignmentsPage().pageIndex).toBe(0);
  });

  it('writes once however many times the action is pressed while a write is outstanding', () => {
    render();
    answerMembership([membership(1, 'First')]);

    component.form.controls.userId.setValue(2);
    component.submit();
    component.submit();
    fixture.detectChanges();

    expect(
      httpMock.match((request) => request.method === 'POST' && request.url === MEMBERS_URL).length,
    ).toBe(1);
  });

  it('offers no notification choice, because nothing can send one', () => {
    render();
    answerMembership([membership(1, 'First')]);

    const notifyBox = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '#role-assignment-notify',
    );

    if (notifyBox === null) {
      throw new Error('the notification control was not rendered');
    }

    // ⚠ DISABLED AND UNTICKED. It used to arrive ticked and its true value was transmitted, so the
    // operator asked for a notification, received a success, and had every reason to think one had been
    // sent. The control is retained because the contract's member is retained.
    expect(notifyBox.disabled).toBeTrue();
    expect(notifyBox.checked).toBeFalse();

    // And the reason is beside it, before the decision, behind the shared field's own help affordance.
    const field = notifyBox.closest('app-form-field');
    field?.querySelector<HTMLButtonElement>('.form-field__help-toggle')?.click();
    fixture.detectChanges();

    expect(field?.querySelector('.form-field__help')?.textContent ?? '').toContain(
      'no mail endpoint',
    );
  });

});
