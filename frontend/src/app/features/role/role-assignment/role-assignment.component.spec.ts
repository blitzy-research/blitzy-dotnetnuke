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
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { RoleAssignmentComponent } from './role-assignment.component';
import { API_ENDPOINTS } from '../../../core/config/api-endpoints';
import { MAX_PAGE_SIZE } from '../../../core/models/paged-result.model';
import { RoleStore } from '../../../core/state/role.store';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, UserRole } from '../../../core/models/role.model';
import type { UserListItem } from '../../../core/models/user.model';

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

function roleUrl(roleId: number): string {
  return `${ROLES_URL}/${roleId}`;
}

function membersUrl(roleId: number): string {
  return `${ROLES_URL}/${roleId}/users`;
}

function memberUrl(roleId: number, userId: number): string {
  return `${membersUrl(roleId)}/${userId}`;
}



/** The page size the account lookup asks for. */
const LOOKUP_PAGE_SIZE = '10';

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
const NO_MATCHING_USERS = 'No accounts match that name.';
const ROLE_UNRESOLVED = 'No security role was addressed, so no memberships can be shown.';

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

/** One account the lookup can offer. */
function account(overrides: Partial<UserListItem> = {}): UserListItem {
  return {
    userId: 42,
    portalId: -1,
    username: 'ada',
    firstName: 'Ada',
    lastName: 'Lovelace',
    displayName: 'Ada Lovelace',
    address: null,
    telephone: null,
    email: 'ada@example.test',
    createdDate: '2024-01-01T00:00:00.000Z',
    lastLoginDate: null,
    isApproved: true,
    isOnline: false,
    isSuperUser: false,
    isLockedOut: false,
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

  beforeEach(async () => {
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
  // reached for below, and deliberately so: this component holds no `effect()` at all — every derived
  // view is a `computed()`, which is pull-based — and its form snapshot is kept current by one plain
  // subscription to the control event stream. `fixture.detectChanges()` is therefore what settles the
  // view, and the polyfills are zone-based, so it behaves conventionally.
  // -------------------------------------------------------------------------------------------------

  /** The component under test, for the few assertions that are about its state rather than its view. */
  function component(): RoleAssignmentComponent {
    return fixture.componentInstance;
  }

  /**
   * Mounts the screen.
   *
   * The identifiers are delivered as the STRINGS route parameters are, so each input's own strict
   * parsing runs. Setting the role identifier is what starts both opening reads, so it is set LAST —
   * the other three are tenant context those reads do not need.
   */
  function create(
    roleId: string | null,
    context: {
      readonly administratorUserId?: string;
      readonly administratorRoleId?: string;
      readonly registeredRoleId?: string;
    } = {},
  ): void {
    fixture = TestBed.createComponent(RoleAssignmentComponent);

    if (context.administratorUserId !== undefined) {
      fixture.componentRef.setInput('administratorUserId', context.administratorUserId);
    }

    if (context.administratorRoleId !== undefined) {
      fixture.componentRef.setInput('administratorRoleId', context.administratorRoleId);
    }

    if (context.registeredRoleId !== undefined) {
      fixture.componentRef.setInput('registeredRoleId', context.registeredRoleId);
    }

    fixture.componentRef.setInput('roleId', roleId);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      typeof description === 'string' ? description : `${method} ${url}`,
    );
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
   * ⚠ NARROWED BY THE ABSENCE OF `query`, AND THAT IS NOT A CONVENIENCE. The page read and the keyed
   * membership probe are issued to the SAME address — the probe is the same listing filtered to one
   * login name — so a criteria naming only the verb and the address matches both and fails with
   * "found 2 requests" as soon as a write puts both in flight together. Telling them apart by the
   * parameter that actually distinguishes them in production is what keeps each case answering the
   * read it means.
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

    call.flush(pageOf(rows, totalCount));
    fixture.detectChanges();

    return call;
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
      readonly administratorUserId?: string;
      readonly administratorRoleId?: string;
      readonly registeredRoleId?: string;
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

  /** Answers the account lookup. */
  function answerLookup(matches: readonly UserListItem[]): TestRequest {
    const call = expectRequest('GET', USERS_URL, 'the account lookup');

    call.flush(pageOf(matches, matches.length, 0, 10));
    fixture.detectChanges();

    return call;
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

  // ⚠ CHOOSING AN ACCOUNT ASKS THE SERVER NOTHING, and neither does releasing one. The membership
  // is read WHOLE - the store follows every page the listing reports - so "does this person already
  // hold the role?" is answered from the rows in hand. A keyed probe would re-ask a question the
  // screen can already answer, and its answer would arrive after the wording it was meant to decide.
  expect(
    httpMock.match(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url.endsWith('/users') &&
        candidate.params.get('query') !== null,
    ),
  )
    .withContext('choosing an account issues no further read')
    .toHaveSize(0);

  // `held` states, at the point of choosing, which memberships that account holds. Those rows must
  // already have been delivered by the opening read, so this asserts they were rather than letting a
  // case smuggle them in behind a second request.
  for (const row of held) {
    expect(component().assignments().some((candidate) => candidate.userId === row.userId))
      .withContext('the memberships the case names are already in hand')
      .toBeTrue();
  }
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
    return Array.from(row.querySelectorAll('td')).map((cell) => textIn(cell).trim());
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
      expect(listRead.request.params.get('pageSize')).toBe(String(MAX_PAGE_SIZE));
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
      answerMemberships(0, [membership()]);
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

      const call = expectRequest('GET', USERS_URL, 'the account lookup');

      // The term is sent RAW: the listing matches on a prefix, so appending a wildcard would search
      // for the wildcard itself.
      expect(call.request.params.get('userName')).toBe('ada');
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe(LOOKUP_PAGE_SIZE);

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

    it('prefills the window from an existing membership when one is chosen', () => {
      // MIGRATION: this is the first branch of `GetDates` at `SecurityRoles.ascx.vb:L273-L303`, which
      // showed an existing membership's two bounds and skipped either one the null test reported as
      // unset (`:L281-L286`). The membership is asked of the SERVER rather than found among the
      // rendered rows, because one page is rendered and the account's row may sit on another.
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

    it('offers an empty window for somebody who does not hold the role yet', () => {
      arrive(0, []);
      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', []);

      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe('');
      // The window is prefilled from the memberships the screen holds, so "nothing to prefill"
      // is the absence of a row for the chosen account rather than a separate published slice.
      expect(component().assignments().find((row) => row.userId === 42)).toBeUndefined();
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
      // `SecurityRoles.ascx.vb:L546`. ONE read, issued by the store and still at the complete scope -
      // which is also what lets the action's wording follow a write that turned an addition into a
      // replacement, because the rows it is decided from have just been refreshed.
      answerMemberships(0, [membership()]);

      expect(rows()).toHaveSize(1);
      // Nothing is announced on success, and in particular nothing is announced as a failure.
      expect(notifications()).toHaveSize(0);
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

      expect(rows()).withContext('the success path was taken').toHaveSize(1);
      expect(notifications()).withContext('and nothing was reported as a failure').toHaveSize(0);
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
      expect(notifications()).withContext('and success is not announced').toHaveSize(0);
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
      expect(cellsOf(rows()[0])[3]).toBe(BACKDATED_EXPIRY_RENDERED);
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
     * ⚠ ASSERTED ON THE ACCESSIBLE NAME — the global `cmdDelete.Text` — AND ON THE ELEMENT, never on a
     * CSS class. A class is a styling decision that may be renamed without changing behaviour; the
     * name a person hears is the contract.
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
        { administratorUserId: '42', administratorRoleId: '0' },
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

    it('withholds the command for the registered-users role', () => {
      // Every authenticated account holds this role, so removing anybody from it would take their
      // authentication away; the legacy rule refused it for the same reason.
      arrive(1, [membership({ userId: 42, roleId: 1 })], { registeredRoleId: '1' });

      expect(button(DELETE_LABEL)).withContext('withheld for registered users').toBeUndefined();
    });

    it('offers the command when no protection applies', () => {
      arrive(7, [membership({ userId: 42, roleId: 7 })], {
        administratorUserId: '1',
        administratorRoleId: '0',
        registeredRoleId: '1',
      });

      expect(button(DELETE_LABEL)).withContext('offered').not.toBeUndefined();
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
     * and no footer style — and the successor keeps it that way: the membership is read whole through
     * the role store, so whatever the size of the role, the grid renders every row it holds and no
     * pager is in sight.
     */
    it('renders no pager, because the membership is read whole', () => {
      arrive(0, [membership()]);

      // No page index, page size, total or page-change handler is declared by this screen, so the
      // absence is structural rather than conditional: there is no size of role that would mount one.
      expect(query('app-pagination')).withContext('not mounted').toBeNull();
      expect(query('.pagination')).toBeNull();
    });

    /**
     * ⚠ NO ROLE PICKER. `SecurityRoles.ascx.vb:L183-L198` is the proof of what this mode rendered: it
     * put the single role into the dropdown at `:L191`, set the title from `RoleTitle.Text` at `:L193`,
     * and then hid BOTH the dropdown and its label at `:L195` and `:L196`. The role is fixed by the
     * route and named in the heading.
     */
    it('renders no role picker, and no dropdown of any kind', () => {
      arrive(0, [membership()]);

      // The account picker was replaced by a lookup for the same reason, so no `select` survives at all.
      expect(queryAll('select')).toHaveSize(0);
    });

    /**
     * `securityroles.ascx:L56` declares no `AllowSorting`, so the legacy grid could not be reordered.
     * The shared grid emits a sort request but never performs one, and leaving every column unsortable
     * is what keeps the two in step. There is no alternating-item style to reproduce either, so no
     * striping is asserted — and the shared grid refuses a column that is both sortable and
     * heading-hidden, which is a second reason the commands column carries no sort.
     */
    it('marks no column sortable and handles no sort request', () => {
      arrive(0, [membership()]);

      expect(queryAll('button.data-table__sort')).toHaveSize(0);
      expect(queryAll('th[aria-sort]')).toHaveSize(0);
      expect(queryAll('.data-table__sort-indicator')).toHaveSize(0);
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

      // And the store re-reads on the write's success, screen or no screen, so that the slice a sibling
      // reads reflects the enrolment. Settled here for the verification in `afterEach`.
      httpMock
        .expectOne(
          (candidate) =>
            candidate.method === 'GET' &&
            candidate.url === membersUrl(0) &&
            candidate.params.get('query') === null,
          "the store's re-read",
        )
        .flush(pageOf([membership()], 1));
    });

    it('abandons an outstanding account lookup when the screen goes away', () => {
      arrive(0, []);

      lookUp('ada');

      const call = expectRequest('GET', USERS_URL, 'the account lookup');

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

      const first = expectRequest('GET', USERS_URL, 'the first lookup');

      lookUp('ann');

      const second = expectRequest('GET', USERS_URL, 'the narrower lookup');

      expect(first.cancelled).withContext('superseded').toBeTrue();
      expect(second.cancelled).withContext('current').toBeFalse();
      expect(second.request.params.get('userName')).toBe('ann');

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
 * through the store, and the membership is read at the COMPLETE scope because the legacy grid was
 * unpaged (`securityroles.ascx:L56` declares no pager).
 *
 * Each case fails for a different reason if the wiring regresses:
 *
 *   - the listing is read at the widest page and every further page the server reports is followed,
 *     so an eleventh member's Delete command is reachable;
 *   - the store holds the rows, which is what a sibling screen reads;
 *   - a write is followed by exactly ONE re-read, issued by the store, and that re-read stays at the
 *     COMPLETE scope rather than collapsing to the first page;
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
   * Answers every outstanding membership read, page by page.
   *
   * @param pages One row list per page, in page order.
   * @returns The page sizes the requests asked for, in the order they were matched.
   */
  function answerMembership(pages: readonly (readonly UserRole[])[]): readonly string[] {
    const requests = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === MEMBERS_URL,
    );
    const askedFor = requests.map((request) => request.request.params.get('pageSize') ?? '');

    requests.forEach((request, index) => {
      const rows = pages[index] ?? [];

      request.flush({
        items: rows,
        meta: {
          totalCount: pages.reduce((total, page) => total + page.length, 0),
          pageIndex: Number(request.request.params.get('pageIndex') ?? '0'),
          pageSize: MAX_PAGE_SIZE,
          totalPages: pages.length,
        },
      });
    });

    fixture.detectChanges();

    return askedFor;
  }

  /** Renders the screen addressed at the role above and answers its role read. */
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

    fixture.detectChanges();
  }

  /** The wordings announced so far, paired with their severity. */
  function announcements(): readonly string[] {
    return notify.calls.allArgs().map(([severity, message]) => `${String(severity)}:${String(message)}`);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RoleAssignmentComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(RoleStore);
    notify = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    store.reset();
    httpMock.verify();
  });

  it('reads the whole membership, following every page the server reports', () => {
    render();

    // The first page, at the widest size the paging contract publishes.
    expect(answerMembership([[membership(1, 'First')], [membership(2, 'Second')]])).toEqual([
      String(MAX_PAGE_SIZE),
    ]);

    // Two pages were reported, so the second is followed. A first-page-only read would put the
    // second member's Delete command out of reach on a grid that has no pager.
    expect(answerMembership([[membership(2, 'Second')]])).toEqual([String(MAX_PAGE_SIZE)]);

    expect(component.assignments().map((row) => row.displayName)).toEqual(['First', 'Second']);
  });

  it('leaves the rows in the store, which is what a sibling screen reads', () => {
    render();
    answerMembership([[membership(1, 'First')]]);

    expect(store.assignmentsRoleId()).toBe(0);
    expect(store.assignmentItems().map((row) => row.displayName)).toEqual(['First']);

    // The whole set is held, so the published metadata describes ONE page rather than a window the
    // screen offers no way to move through.
    expect(store.assignmentsMeta().totalPages).toBe(1);
    expect(store.assignmentsScope()).toBe('complete');
  });

  it('writes an enrolment through the store and lets the store re-read, at the complete scope', () => {
    render();
    answerMembership([[membership(1, 'First')]]);

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

    // ONE re-read, issued by the store, and still at the complete scope. A second read asked for here
    // would race the store's; a paged re-read would silently shrink the grid.
    expect(answerMembership([[membership(1, 'First'), membership(2, 'Second')]])).toEqual([
      String(MAX_PAGE_SIZE),
    ]);

    expect(component.assignments().length).toBe(2);
  });

  it('raises a refused removal only after the listing has refreshed, and remembers the pairing', () => {
    render();
    answerMembership([[membership(1, 'First')]]);

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

    answerMembership([[membership(1, 'First')]]);

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
    answerMembership([[membership(1, 'First')]]);

    component.onUserSearch('sec');
    fixture.detectChanges();

    const lookup = httpMock.expectOne(
      (request) => request.method === 'GET' && request.url === API_ENDPOINTS.users.collection(),
    );

    // The term travels RAW, because the listing matches on a prefix and a wildcard would be searched
    // for literally.
    expect(lookup.request.params.get('userName')).toBe('sec');

    lookup.flush({
      items: [],
      meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 },
    });
    fixture.detectChanges();

    // The membership slice is untouched by a lookup, and so is its scope.
    expect(store.assignmentItems().length).toBe(1);
    expect(store.assignmentsScope()).toBe('complete');
  });

  it('writes once however many times the action is pressed while a write is outstanding', () => {
    render();
    answerMembership([[membership(1, 'First')]]);

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
    answerMembership([[membership(1, 'First')]]);

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
