/**
 * Specification for {@link RoleAssignmentComponent} — the memberships of one security role.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * Membership of a role IS authorisation on this platform: the permission evaluator resolves what a
 * caller may do from the roles they hold, so a membership added to the wrong role, removed when it
 * should have been protected, or written with the wrong effective window changes who can administer a
 * tenant. Nothing else in the workspace asserts any of it.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - The component is mounted as the standalone unit it is, with the REAL {@link RoleService} and
 *     {@link UserService} resolved from the injector and every request answered through
 *     `HttpTestingController`, so each assertion about an address, a body or a status is an assertion
 *     about the wire. This screen talks to the services directly rather than through the role store.
 *   - Its four identifiers are delivered through `componentRef.setInput` as the STRINGS route
 *     parameters are, so each input's own parsing runs rather than being bypassed.
 *   - `NotificationService.notify` is spied and called through, so announcements are observable.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ ADDING A MEMBER ANSWERS `204`, NEVER `201`. The membership endpoint is `POST /roles/{id}/users` and
 * it returns no body at all — the controller declares `204` — because the assignment is an edge between
 * two existing resources rather than a new resource of its own. A specification that flushed `201` with
 * a body would be asserting a response this API cannot produce.
 *
 * ⚠ `role_assignment.protected` ARRIVES AS `403`, NOT `409`. It is the one member of the conflict
 * vocabulary that does, which the vocabulary itself records, and the shared summariser therefore
 * classifies it as a WARNING rather than an error.
 *
 * ⚠ EVERY WRITE IS FOLLOWED BY A RE-READ OF THE MEMBERSHIPS, INCLUDING A FAILED REMOVAL. The screen
 * re-reads so that what a person sees is what the server holds rather than what the browser guessed,
 * and the refusal message is raised AFTER that read so it cannot be buried by the repaint.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { RoleAssignmentComponent } from './role-assignment.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, UserRole } from '../../../core/models/role.model';
import type { UserListItem } from '../../../core/models/user.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

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

/**
 * The page size the membership listing asks for.
 *
 * The screen's OWN page coordinate, not "the whole membership". The listing used to read page 0 at
 * size 100 and then fan out over every further page the server reported, which retained up to a
 * hundred thousand rows for one screen; it now reads exactly the page being looked at and offers a
 * pager for the rest, so no member is hidden and none is needlessly held.
 */
const MEMBERSHIP_PAGE_SIZE = '10';

/**
 * The page size the KEYED MEMBERSHIP PROBE asks for.
 *
 * Choosing an account issues one narrow read keyed by that account's user name, which is what
 * restores the legacy answer - "does this person already hold the role, and through what window?" -
 * for an account on any page, now that the listing no longer holds every page.
 */
const MEMBERSHIP_PROBE_PAGE_SIZE = '100';

/** The page size the account lookup asks for. */
const LOOKUP_PAGE_SIZE = '10';

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported, so a change to any of it is detected here. Each is the legacy resource
// value, its capitalisation included.
// =====================================================================================================

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

/** `RoleRemoveError.Text`, published by the shared conflict vocabulary for this code. */
const REMOVAL_REFUSED_MESSAGE =
  'You Can Not Remove The Portal Administrator Or The Registered Users Role';

const EFFECTIVE_DATE_CONTROL_ID = 'role-assignment-effective-date';
const EXPIRY_DATE_CONTROL_ID = 'role-assignment-expiry-date';
const NOTIFY_CONTROL_ID = 'role-assignment-notify';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-9c2e4f1b7a934dd6bb18eb211c80319c-55bd6b7169203331-01';
const CORRELATION_ID = 'b91d5c37-8a2e-4f60-91c4-3e7d0b2a6c48';

function problem(
  code: string,
  status: number,
  detail: string,
  errors?: Readonly<Record<string, readonly string[]>>,
): ProblemDetails {
  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };

  return errors === undefined ? document : { ...document, errors };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * One role.
 *
 * ⚠ THE DEFAULT IDENTIFIER IS ZERO. `dbo.Roles.RoleID` is `IDENTITY(0, 1)`, so role zero is the
 * Administrators role of an installation — the single most consequential role there is.
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

/** One membership row. */
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
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    await TestBed.configureTestingModule({
      imports: [RoleAssignmentComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Mounts the screen.
   *
   * The identifiers are delivered as the STRINGS route parameters are. Setting the role identifier is
   * what starts both reads, so it is set LAST — the other three are context the reads do not need.
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
      description ?? `${method} ${url}`,
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
   * login name — so a criteria naming only the verb and the address matches both and fails with "found
   * 2 requests" the moment a write puts both in flight together. Telling them apart by the parameter
   * that actually distinguishes them in production is what keeps each case answering the read it means.
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
   * Answers the keyed membership probe the screen re-issues after a write.
   *
   * A write may have created or ended the chosen account's membership, so the screen re-asks the FACT
   * — which of "Add User" and "Update User Role" the action now is — and deliberately leaves the
   * operator's two date boxes alone. Answering it here rather than folding it into the page read keeps
   * that distinction visible, and an unanswered probe would otherwise fail verification in `afterEach`.
   *
   * @param roleId The addressed role.
   * @param held The rows the probe finds for the queried login name.
   */
  function answerProbeRefresh(roleId: number, held: readonly UserRole[]): TestRequest {
    const call = httpMock.expectOne(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url === membersUrl(roleId) &&
        candidate.params.get('query') !== null,
      'the membership probe refresh',
    );

    call.flush(pageOf(held, held.length));
    fixture.detectChanges();

    return call;
  }

  /**
   * Mounts the screen and settles both of its opening reads.
   *
   * ⚠ BOTH READS ARE ISSUED TOGETHER BY THE ROLE INPUT, so they are outstanding at the same moment and
   * are answered in whichever order this helper chooses — which is itself worth stating, because a
   * screen that depended on one arriving before the other would be order-sensitive in a way no browser
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
  ): void {
    create(String(roleId), context);
    answerRole(role(roleId));
    answerMemberships(roleId, rows);
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

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** A button by its rendered wording, anywhere on the screen. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
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
   * ⚠ SCOPED TO THE DIALOGUE ON PURPOSE. The row command and the dialogue's confirming button share the
   * wording 'Delete', and the abandon link and the dialogue's dismissing button share 'Cancel', so an
   * unscoped lookup by wording would press the wrong one and the case would prove something else.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    expect(control)
      .withContext(`the "${label}" button of the confirmation is offered`)
      .not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /**
   * Searches for an account through the shared lookup.
   *
   * The lookup debounces its own typing, so the immediate path is used here: pressing its own submit
   * control emits at once. That keeps the case free of a fake clock while still going through the real
   * control rather than calling the handler.
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
   * list item, and a document-ordered query that also admitted the item would return the ITEM first -
   * whose text contains the same wording - so the case would click a non-interactive element, nothing
   * would happen, and every assertion afterwards would fail against an unmade choice rather than
   * against the screen.
   */
  function chooseAccount(label: string, held: readonly UserRole[] = []): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      'button.role-assignment__match-action',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    expect(control).withContext(`the account "${label}" is offered`).not.toBeUndefined();

    const wasChosen = (control as HTMLButtonElement).getAttribute('aria-pressed') === 'true';

    (control as HTMLButtonElement).click();
    fixture.detectChanges();

    // ⚠ CHOOSING AN ACCOUNT ISSUES A KEYED MEMBERSHIP PROBE, and it is answered here rather than
    // left open. The screen no longer holds every page of the membership list, so it cannot answer
    // "does this person already hold the role?" by scanning what is rendered; it asks. Answering it
    // in the helper is what keeps every case below stating what it is about rather than restating
    // the probe, and an unanswered probe would fail verification in `afterEach` instead.
    //
    // ⚠ RELEASING IS NOT CHOOSING. This control is a toggle — pressing the chosen account again is
    // how a mis-click is corrected — and a release asks the server nothing, because forgetting
    // somebody needs no facts about them. Expecting a probe unconditionally would demand a request
    // the screen is right not to make, so the helper decides from the control's own pressed state.
    const probe = httpMock.match(
      (candidate) =>
        candidate.method === 'GET' &&
        candidate.url.endsWith('/users') &&
        candidate.params.get('query') !== null,
    );

    if (wasChosen) {
      expect(probe.length)
        .withContext('releasing an account asks the server nothing')
        .toBe(0);

      return;
    }

    expect(probe.length)
      .withContext('choosing an account issues exactly one keyed membership probe')
      .toBe(1);
    expect(probe[0]?.request.params.get('pageSize')).toBe(MEMBERSHIP_PROBE_PAGE_SIZE);

    probe[0]?.flush(pageOf(held, held.length));
    fixture.detectChanges();
  }

  /** Whether the account is currently held, read off the toggle's own pressed state. */
  function accountIsChosen(label: string): boolean {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      'button.role-assignment__match-action',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    return control?.getAttribute('aria-pressed') === 'true';
  }

  /** Types into a date control. */
  function typeDate(controlId: string, value: string): void {
    const control = query<HTMLInputElement>(`#${controlId}`);

    expect(control).withContext(`#${controlId} is rendered`).not.toBeNull();

    (control as HTMLInputElement).value = value;
    (control as HTMLInputElement).dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** The membership rows painted by the shared grid. */
  function rows(): readonly HTMLTableRowElement[] {
    return queryAll<HTMLTableRowElement>('tr.data-table__row');
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — THE TWO OPENING READS
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads the role and its whole membership list, from relative addresses', () => {
      create('0');

      const roleRead = expectRequest('GET', roleUrl(0), 'the role read');
      const memberRead = expectRequest('GET', membersUrl(0), 'the membership read');

      // ⚠ ROLE ZERO IS THE ADMINISTRATORS ROLE OF AN INSTALLATION. The identity column is seeded from
      // zero, so a truthiness test anywhere on this path would refuse to manage the one role whose
      // membership matters most.
      expect(roleRead.request.url).toBe('/api/v1/roles/0');
      expect(memberRead.request.url).toBe('/api/v1/roles/0/users');
      expect(roleRead.request.url.startsWith('http')).withContext('relative').toBeFalse();

      // The membership list is read WHOLE rather than a page at a time: a person managing a role needs
      // to see everybody in it, and the screen follows any further pages the server reports.
      expect(memberRead.request.params.get('pageIndex')).toBe('0');
      expect(memberRead.request.params.get('pageSize')).toBe(MEMBERSHIP_PAGE_SIZE);

      roleRead.flush(envelope(role(0)));
      memberRead.flush(pageOf([membership()]));
      fixture.detectChanges();

      httpMock.expectNone(() => true);
      expect(rows()).toHaveSize(1);
    });

    it('reads ONE page and offers a pager for the rest, rather than fanning out over every page', () => {
      // ⚠ THIS PINS DOWN THE ABSENCE OF A FAN-OUT, AND THAT IS THE POINT. The screen used to read
      // page 0 at size 100 and then issue a request for EVERY further page the server reported -
      // up to 999 concurrent reads flattening as many as a hundred thousand retained rows into one
      // component for a grid showing ten. It now reads exactly the page being looked at. No member
      // is hidden by that, because the pager reaches the rest; what is gone is the retention.
      create('0');

      answerRole(role(0));

      const first = expectRequest('GET', membersUrl(0), 'the only opening membership read');

      expect(first.request.params.get('pageIndex')).toBe('0');
      expect(first.request.params.get('pageSize')).toBe(MEMBERSHIP_PAGE_SIZE);

      first.flush(pageOf([membership()], 150, 0, Number(MEMBERSHIP_PAGE_SIZE)));
      fixture.detectChanges();

      // Nothing further is asked for. A second read here would be the fan-out returning.
      httpMock.verify();

      // One page is painted, and the pager is offered because there is more than one page of members.
      expect(rows()).toHaveSize(1);
      expect(query('app-pagination'))
        .withContext('the rest of the membership is reachable rather than hidden')
        .not.toBeNull();
    });

    it('names the screen from the role it read, and falls back before it arrives', () => {
      create('0');

      expect((query('h1')?.textContent ?? '').trim()).toBe(TITLE_FALLBACK);

      answerRole(role(0, { roleName: 'Subscribers' }));
      answerMemberships(0, []);

      expect((query('h1')?.textContent ?? '').trim()).toBe('Manage Users in Role: Subscribers');
    });

    it('names the grid for a reader through the shared caption slot', () => {
      arrive();

      expect((query('caption')?.textContent ?? '').trim()).toBe(CAPTION);
    });

    it('reads nothing at all when the address names no role, and says so', () => {
      create(null);

      // No address can be built from an absent identifier, so nothing is attempted — and the state is
      // explained rather than left as an empty grid a person cannot account for.
      httpMock.expectNone(() => true);
      expect(textOf('.role-assignment__unresolved').join(' ')).toContain(ROLE_UNRESOLVED);
      expect(query('form')).withContext('no form is offered').toBeNull();
    });

    it('discards everything and re-reads when the address names another role', () => {
      arrive(0, [membership()]);

      fixture.componentRef.setInput('roleId', '7');
      fixture.detectChanges();

      // ⚠ THE PREVIOUS ROLE'S STATE MUST NOT SURVIVE. A membership list, a chosen account or a pending
      // removal carried across would let an action be taken against a role the person has left.
      expect(rows()).withContext('the previous list is dropped').toHaveSize(0);

      answerRole(role(7, { roleName: 'Subscribers' }));
      answerMemberships(7, [membership({ roleId: 7, userId: 43, username: 'grace' })]);

      expect((query('h1')?.textContent ?? '').trim()).toBe('Manage Users in Role: Subscribers');
      expect(rows()).toHaveSize(1);
    });

    it('re-reads nothing when the same role is delivered again', () => {
      arrive(0);

      // A router can deliver a parameter more than once for one navigation, and an explicit equality
      // test is what stops that becoming two round trips.
      fixture.componentRef.setInput('roleId', '0');
      fixture.detectChanges();

      // ⚠ `expectNone` IS NOT A JASMINE EXPECTATION, so a case whose only assertion is that one fails
      // outright under this project's no-empty-specification setting. The observable consequence is
      // asserted as well: nothing was discarded, so the list a person is looking at is still there.
      expect(httpMock.match(() => true)).withContext('no further request').toHaveSize(0);
      expect(rows()).withContext('and the list survives').toHaveSize(1);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — FINDING AN ACCOUNT
  // ---------------------------------------------------------------------------------------------------

  describe('looking an account up', () => {
    it('queries the account listing by name, one page at a time', () => {
      arrive(0, []);

      lookUp('ada');

      const call = expectRequest('GET', USERS_URL, 'the account lookup');

      // The legacy screen bound a dropdown holding EVERY account in the tenant, which does not scale
      // past a small one. The lookup queries by name instead, and the term travels exactly as typed:
      // no wildcard is appended and no pattern syntax is introduced, because match semantics are the
      // server's.
      expect(call.request.params.get('userName')).toBe('ada');
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe(LOOKUP_PAGE_SIZE);

      call.flush(pageOf([account()], 1, 0, 10));
      fixture.detectChanges();

      expect(host().textContent ?? '').toContain('Ada Lovelace');
    });

    it('queries nothing for an emptied term, and offers nothing', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);

      lookUp('');

      // An empty term is not a query for everybody: it is the absence of a query, and asking the server
      // for every account is exactly the behaviour the lookup replaced.
      httpMock.expectNone(() => true);
      expect(host().textContent ?? '').not.toContain('Ada Lovelace');
    });

    it('says so when nothing matches, rather than blanking the field in silence', () => {
      arrive(0, []);

      lookUp('nobody');
      answerLookup([]);

      // MIGRATION: the legacy lookup failed SILENTLY — it blanked the box with no message at all. A
      // visible state replaces that, and it is a documented improvement rather than an accident.
      expect(host().textContent ?? '').toContain(NO_MATCHING_USERS);
    });

    it('reports a refused lookup and offers nothing rather than a stale list', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);

      lookUp('grace');
      expectRequest('GET', USERS_URL).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // A refusal is a WARNING rather than an error — the shared summariser classifies 401, 403, 404
      // and 429 that way — and the previous matches are cleared so nothing can be chosen from a list
      // the server has just refused to refresh.
      expect(notifications()).toEqual([
        {
          severity: 'warning',
          message: 'The authenticated caller is not permitted to perform this operation.',
        },
      ]);
      expect(host().textContent ?? '').not.toContain('Ada Lovelace');
    });

    it('prefills the window from an existing membership when one is chosen', () => {
      const held = membership({
        userId: 42,
        effectiveDate: '2026-01-05T00:00:00.000Z',
        expiryDate: '2026-06-30T00:00:00.000Z',
      });

      arrive(0, [held]);

      lookUp('ada');
      answerLookup([account()]);
      // ⚠ THE ROW IS HANDED TO THE PROBE AND NOT ONLY TO THE PAGE. The prefill is answered by one
      // keyed server read rather than by scanning what happens to be rendered, which is what lets it
      // find a membership sitting on ANY page — the rendered-row scan could only ever find one on the
      // page in view, and paid for the attempt with a request per page.
      chooseAccount('Ada Lovelace', [held]);

      // Choosing somebody who already holds the role opens their CURRENT window rather than an empty
      // one, so a person editing a membership is not silently clearing the dates they cannot see.
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('2026-01-05');
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe('2026-06-30');
      // And the action says which of the two things it will do.
      expect(button(UPDATE_USER_ROLE_LABEL)).withContext('a replacement').not.toBeUndefined();
    });

    it('offers an empty window for somebody who does not hold the role yet', () => {
      arrive(0, [membership({ userId: 99, username: 'grace' })]);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(query<HTMLInputElement>(`#${EXPIRY_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(button(ADD_USER_LABEL)).withContext('an addition').not.toBeUndefined();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — ADDING A MEMBERSHIP
  // ---------------------------------------------------------------------------------------------------

  describe('adding a membership', () => {
    it('posts the four declared members and answers 204 with no body at all', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      typeDate(EFFECTIVE_DATE_CONTROL_ID, '2026-02-01');
      typeDate(EXPIRY_DATE_CONTROL_ID, '2026-12-31');

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0), 'the assignment');

      // Asserted as a whole object, so a member added, renamed or dropped by either side fails here.
      // The dates travel as the calendar values they were typed as; the server owns the instant.
      // ⚠ THE NOTIFICATION MEMBER DEFAULTS TO TRUE, matching the legacy `SendNotification` control,
      // which was checked when the screen opened. Asserting `false` here would encode a default this
      // application does not have and would quietly stop informing the accounts it adds.
      expect(call.request.body).toEqual({
        userId: 42,
        effectiveDate: '2026-02-01',
        expiryDate: '2026-12-31',
        notifyUser: false,
      });

      // ⚠ 204 WITH NO BODY, NEVER 201. The assignment is an edge between two existing resources, so
      // there is no created resource to return and the controller declares no content.
      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // The list is re-read rather than guessed at, because the server decides what a membership looks
      // like once it is stored — and the keyed probe is re-issued alongside it, because the write may
      // have turned an addition into a replacement and the action's own wording has to follow.
      answerMemberships(0, [membership()]);
      answerProbeRefresh(0, [membership()]);

      expect(rows()).toHaveSize(1);
    });

    it('sends an omitted window as null rather than as empty text', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0));

      // ⚠ THE EMPTY STRING IS THE LEGACY ABSENT-STRING MARKER, so an omitted date must arrive as null
      // and not as `''`: a blank where a date is expected is a value the server would have to guess at.
      expect(call.request.body).toEqual({
        userId: 42,
        effectiveDate: null,
        expiryDate: null,
        notifyUser: false,
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerMemberships(0, [membership()]);
      answerProbeRefresh(0, [membership()]);
    });

    it('states the mail reduction rather than offering a choice it cannot honour', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      const notify = query<HTMLInputElement>(`#${NOTIFY_CONTROL_ID}`);

      expect(notify).withContext('the notification choice is still shown').not.toBeNull();

      // ⚠ UNTICKED AND DISABLED, DEPARTING FROM THE LEGACY INITIAL STATE DELIBERATELY. The legacy
      // control opened CHECKED and its true value was transmitted to a routine that mailed the
      // account holder. This migration excludes the mail subsystem wholesale, so an operator who
      // left it ticked asked for a notification, received a success, and had every reason to
      // believe one had gone out. The control is therefore unticked and disabled, and the reason is
      // stated BESIDE it rather than nowhere.
      expect((notify as HTMLInputElement).checked).withContext('unticked to begin with').toBeFalse();
      expect((notify as HTMLInputElement).disabled).withContext('and not offered').toBeTrue();

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0));

      // The member is still TRANSMITTED - the contract declares it, so omitting it would leave the
      // decision to the server - and it carries the truthful `false`: nothing is asking for a
      // notification, because nothing can send one. A disabled control reports its value like any
      // other, which is what keeps the request complete.
      expect((call.request.body as { notifyUser: boolean }).notifyUser).toBeFalse();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerMemberships(0, [membership()]);
      answerProbeRefresh(0, [membership()]);
    });

    it('withholds the action until an account has been chosen', () => {
      arrive(0, []);

      // The window fields are meaningless without somebody to apply them to, so the action is out of
      // use until one is chosen — and this is the one state in which it is genuinely unavailable.
      expect(button(ADD_USER_LABEL)?.disabled).withContext('nobody chosen').toBeTrue();

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      expect(button(ADD_USER_LABEL)?.disabled).withContext('somebody chosen').toBeFalse();
    });

    it('sends nothing and reports the order rule when the window is inverted', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      typeDate(EFFECTIVE_DATE_CONTROL_ID, '2026-12-31');
      typeDate(EXPIRY_DATE_CONTROL_ID, '2026-01-01');

      // The rule compares TWO controls, so it belongs to the group that owns both. The action is
      // withheld rather than the request being sent for the server to refuse.
      expect(button(ADD_USER_LABEL)?.disabled).withContext('an inverted window').toBeTrue();

      const control = button(ADD_USER_LABEL);

      (control as HTMLButtonElement).click();
      fixture.detectChanges();

      httpMock.expectNone(() => true);
    });

    it('clears the chosen account and its window on demand', () => {
      const held = membership({ userId: 42, effectiveDate: '2026-01-05T00:00:00.000Z' });

      arrive(0, [held]);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace', [held]);

      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('2026-01-05');

      expect(accountIsChosen('Ada Lovelace')).withContext('held').toBeTrue();

      // Pressing the chosen account again releases it, which is how a person corrects a mis-click. The
      // pressed state is the choice, so it is read from the control rather than inferred from colour.
      chooseAccount('Ada Lovelace');

      expect(accountIsChosen('Ada Lovelace')).withContext('released').toBeFalse();
      expect(query<HTMLInputElement>(`#${EFFECTIVE_DATE_CONTROL_ID}`)?.value).toBe('');
      expect(button(ADD_USER_LABEL)?.disabled).withContext('nobody chosen again').toBeTrue();
      httpMock.expectNone(() => true);
    });

    it('reports a refused assignment and does not re-read the list', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      press(ADD_USER_LABEL);

      expectRequest('POST', membersUrl(0)).flush(
        problem('role.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // Nothing changed, so there is nothing to re-read; and the refusal is a warning at 404.
      httpMock.expectNone(() => true);
      expect(notifications()).toEqual([
        { severity: 'warning', message: 'The requested resource does not exist.' },
      ]);
      // The banner carries the same event with its quotable reference.
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });

    it('shows a per-field server message beside the field the server named', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      press(ADD_USER_LABEL);

      expectRequest('POST', membersUrl(0)).flush(
        problem('request.invalid', 400, 'One or more validation errors occurred.', {
          effectiveDate: ['The effective date is not acceptable.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // The map's keys are .NET model-state keys, are not camel-cased by the client, and are read with
      // an index expression because the map is an index signature under the strict setting.
      expect(textOf('.form-field__error')).toContain('The effective date is not acceptable.');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — REMOVING A MEMBERSHIP
  // ---------------------------------------------------------------------------------------------------

  describe('removing a membership', () => {
    it('asks first, then removes with a 204 and re-reads the list', () => {
      arrive(0, [membership({ userId: 42 })]);

      press(DELETE_LABEL);

      expect(query('.confirm-dialog')).withContext('the question is asked').not.toBeNull();
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim()).toBe(
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
      // alone names everybody in it and the account alone names every role they hold.
      expect(call.request.url).toBe('/api/v1/roles/0/users/42');
      // The dialogue is dismissed the moment the command is issued, so no modal sits over a request.
      expect(query('.confirm-dialog')).toBeNull();

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerMemberships(0, []);

      expect(rows()).withContext('the membership is gone').toHaveSize(0);
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

    it('reports a protected membership as a WARNING and re-reads before announcing it', () => {
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

      // ⚠ THE RE-READ HAPPENS EVEN THOUGH THE REMOVAL FAILED, and the message is deferred until after
      // it: what a person sees must be what the server holds rather than what the browser guessed, and
      // announcing before the repaint would let the message be buried by it.
      expect(notifications()).withContext('deferred until the list is back').toHaveSize(0);

      answerMemberships(0, [membership({ userId: 42 })]);

      // ⚠ THE WORDING IS THE LEGACY `RoleRemoveError` SENTENCE, published by the shared conflict
      // vocabulary — and the SEVERITY IS A WARNING, NOT AN ERROR. This code is the one member of that
      // vocabulary that arrives as `403` rather than `409`, and an access refusal is a warning in the
      // three-valued vocabulary the legacy screens used. Taking the severity from the shared summariser
      // rather than hard-coding it here is what keeps one decision in one place.
      expect(notifications()).toEqual([
        { severity: 'warning', message: REMOVAL_REFUSED_MESSAGE },
      ]);
      // And the row is still there, because nothing was removed.
      expect(rows()).toHaveSize(1);
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

      // Learning from the refusal rather than offering it again: a person is not invited to repeat an
      // action the server has already said it will refuse.
      expect(button(DELETE_LABEL)).withContext('the affordance is withdrawn').toBeUndefined();
    });

    it('withholds the affordance from the designated administrator of the tenant', () => {
      arrive(0, [membership({ userId: 42, roleId: 0 })], {
        administratorUserId: '42',
        administratorRoleId: '0',
      });

      // ⚠ KNOWN BEFORE THE SERVER SAYS SO, from the tenant's own designated administrator and
      // administrators role. Both are zero-or-negative-capable identifiers, so the comparison is by
      // equality and never by truthiness.
      expect(button(DELETE_LABEL)).withContext('withheld for the administrator').toBeUndefined();
    });

    it('withholds the affordance for the registered-users role', () => {
      arrive(1, [membership({ userId: 42, roleId: 1 })], { registeredRoleId: '1' });

      // Every authenticated account holds this role, so removing anybody from it would take their
      // authentication away; the legacy screen refused it for the same reason.
      expect(button(DELETE_LABEL)).withContext('withheld for registered users').toBeUndefined();
    });

    it('offers the affordance on an ordinary membership when no protection applies', () => {
      arrive(7, [membership({ userId: 42, roleId: 7 })], {
        administratorUserId: '1',
        administratorRoleId: '0',
        registeredRoleId: '1',
      });

      expect(button(DELETE_LABEL)).withContext('offered').not.toBeUndefined();
    });

    it('reports an unrelated refusal through the shared summariser', () => {
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
      answerMemberships(7, [membership({ userId: 42, roleId: 7 })]);

      expect(notifications()).toEqual([
        {
          severity: 'error',
          message: 'An unexpected error occurred while processing the request.',
        },
      ]);
      // An unrelated fault teaches nothing about the pairing, so the affordance stays.
      expect(button(DELETE_LABEL)).withContext('still offered').not.toBeUndefined();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — CANCELLATION AND TEARDOWN
  // ---------------------------------------------------------------------------------------------------

  describe('leaving the screen', () => {
    it('abandons every outstanding read when the screen goes away', () => {
      create('0');

      const roleRead = expectRequest('GET', roleUrl(0));
      const memberRead = expectRequest('GET', membersUrl(0));

      fixture.destroy();

      // ⚠ EVERY STREAM IS TIED TO THE COMPONENT'S LIFETIME, so leaving cancels the work rather than
      // letting it land on a screen nobody is looking at — which would write to destroyed state.
      expect(roleRead.cancelled).withContext('the role read is abandoned').toBeTrue();
      expect(memberRead.cancelled).withContext('the membership read is abandoned').toBeTrue();

      // A cancelled request cannot be answered, and `verify` counts it unless told to ignore it.
      expect(() => roleRead.flush(envelope(role(0)))).toThrowError(/cancelled/i);
      httpMock.verify({ ignoreCancelled: true });
    });

    it('abandons an outstanding write when the screen goes away', () => {
      arrive(0, []);

      lookUp('ada');
      answerLookup([account()]);
      chooseAccount('Ada Lovelace');

      press(ADD_USER_LABEL);

      const call = expectRequest('POST', membersUrl(0));

      fixture.destroy();

      expect(call.cancelled).withContext('the assignment is abandoned').toBeTrue();
      // Nothing is announced for work that never completed.
      expect(notifications()).toHaveSize(0);
      httpMock.verify({ ignoreCancelled: true });
    });

    it('abandons an outstanding account lookup when the screen goes away', () => {
      arrive(0, []);

      lookUp('ada');

      const call = expectRequest('GET', USERS_URL);

      fixture.destroy();

      expect(call.cancelled).withContext('the lookup is abandoned').toBeTrue();
      httpMock.verify({ ignoreCancelled: true });
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      arrive();

      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('names the lookup with a real label pointing at the control it renders', () => {
      arrive();

      const label: HTMLLabelElement | undefined = queryAll<HTMLLabelElement>('label[for]').find(
        (candidate) => (candidate.textContent ?? '').trim().startsWith(USER_LABEL),
      );

      expect(label).withContext('the lookup carries a real label').not.toBeUndefined();

      const target: string = (label as HTMLLabelElement).getAttribute('for') ?? '';

      // ⚠ THE ASSOCIATION POINTS AT THE SHARED LOOKUP'S OWN FIELD IDENTIFIER, because the lookup owns
      // the input it renders. Pointing at a name no element carries would be a dangling association,
      // which is worse than none at all.
      expect(query(`#${target}`)).withContext('the association resolves').not.toBeNull();
      // A placeholder is a hint and never an accessible name, which is why the label is mandatory.
      expect(query<HTMLInputElement>('input[type="search"]')?.placeholder).toBe(
        VALIDATE_PLACEHOLDER,
      );
    });

    it('links each member to their account rather than repeating their name as plain text', () => {
      arrive(0, [membership({ userId: 42 })]);

      const link = query<HTMLAnchorElement>('tr.data-table__row a');

      expect(link).withContext('the member is a link').not.toBeNull();
      expect((link as HTMLAnchorElement).getAttribute('href')).toBe('/users/42');
    });

    it('offers the abandon affordance as a link, because it navigates', () => {
      arrive();

      const back: HTMLAnchorElement | undefined = queryAll<HTMLAnchorElement>('a').find(
        (candidate) => (candidate.textContent ?? '').trim() === CANCEL_LABEL,
      );

      expect(back).withContext('the way back is a link').not.toBeUndefined();
      expect((back as HTMLAnchorElement).getAttribute('href')).toBe('/roles');
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      arrive();

      expect(query('.error-banner-live')).withContext('the region exists').not.toBeNull();
      expect(query('.error-banner__title')).withContext('but says nothing').toBeNull();
    });

    it('renders a hostile display name as text, with no element parsed out of it', () => {
      arrive(0, [membership({ displayName: '<img src=x onerror="window.__member=true">' })]);

      expect(queryAll('img')).withContext('no element parsed out of a name').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__member'])
        .withContext('never evaluated')
        .toBeUndefined();
      expect(host().innerHTML).toContain('&lt;img');
    });

    it('renders the legacy marker date and an absent date alike, as empty cells', () => {
      arrive(0, [membership({ effectiveDate: '0001-01-01T00:00:00Z', expiryDate: null })]);

      const cells: readonly string[] = Array.from(
        (rows()[0] as HTMLTableRowElement).querySelectorAll('td'),
      ).map((cell) => (cell.textContent ?? '').trim());

      // Two different absences on the wire and one rendering, because to a person they mean the same
      // thing. The shared pipe owns that decision.
      expect(cells.filter((text) => text.length === 0).length)
        .withContext('both absences render empty')
        .toBeGreaterThanOrEqual(2);
    });
  });
});
