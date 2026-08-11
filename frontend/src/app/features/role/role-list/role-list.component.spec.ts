/**
 * Specification for {@link RoleListComponent} — the security-role listing at `/roles`.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * It is TWO screens sharing one surface: a listing of roles, and an inline editor for the role GROUP
 * currently being filtered by. The group half is where the risk lives — the group being edited is the
 * one the FILTER names, so the filter and the editor must never disagree about which group that is, and
 * a group may only be removed once nothing is in it.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - Mounted as the standalone unit it is, with the REAL {@link RoleStore} pinned to each case's
 *     injector, and every request answered through `HttpTestingController`.
 *   - `NotificationService.notify` is spied and called through. No router is spied: every cross-screen
 *     movement this screen offers is a link, asserted as an address.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ ARRIVAL IS ONE CHAINED READ, NOT TWO INDEPENDENT ONES. `GET /role-groups` is issued first and
 * `GET /roles` follows only once the groups have answered — they are one subscription joined by a
 * switch — so a case must answer them IN THAT ORDER. Answering the roles read first finds nothing.
 *
 * ⚠ THE FILTER IS NOT SENT AS A NUMBER. The two pseudo-entries are negative values IN THE DOM ONLY;
 * what travels is a named scope (`All` or `Ungrouped`) or a real `roleGroupId`. A negative number
 * reaching the wire would be a request for a group that cannot exist.
 *
 * ⚠ A GROUP CAN ONLY BE REMOVED WHILE THE LISTING IS EMPTY. That is the client's own precondition, and
 * the server's `role_group.in_use` refusal at `409` is the backstop for the race between them.
 */
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { RoleStore } from '../../../core/state/role.store';
import { RoleListComponent } from './role-list.component';

import type { WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { BillingFrequency, RoleGroup, RoleListItem } from '../../../core/models/role.model';
import type { UserDetail } from '../../../core/models/user.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

const ROLES_URL = '/api/v1/roles';

/**
 * The memberships-of-one-account address, for the account key these cases use.
 *
 * Nested under the ACCOUNT, because the answer is one person's memberships rather than one role's
 * members — `RolesController.cs:839` routes it at `users/{userId:int}/roles`.
 */
const USER_ROLES_URL = '/api/v1/users/42/roles';

/** A second account's address, so a switch of subject is provable. */
const OTHER_USER_ROLES_URL = '/api/v1/users/43/roles';

/** The account keyed nought's address, which no truthiness test may reduce to the bare listing. */
const USER_ZERO_ROLES_URL = '/api/v1/users/0/roles';

/** The account key these cases narrow to. */
const ACCOUNT_ID = 42;

/** A second account key. */
const OTHER_ACCOUNT_ID = 43;
const ROLE_GROUPS_URL = '/api/v1/role-groups';

function roleGroupUrl(roleGroupId: number): string {
  return `${ROLE_GROUPS_URL}/${roleGroupId}`;
}

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
// =====================================================================================================

const PAGE_TITLE = 'Security Roles';
const ADD_ROLE_LABEL = 'Add New Role';
const ADD_ROLE_GROUP_LABEL = 'Add New Role Group';
const MEMBERSHIP_SETTINGS_LABEL = 'User Settings';
const EDIT_LABEL = 'Edit';
const MANAGE_USERS_LABEL = 'Manage Users';
const ALL_ROLES_OPTION_LABEL = '< All Roles >';
const GLOBAL_ROLES_OPTION_LABEL = '< Global Roles >';
const RETRY_LABEL = 'Try again';
const GROUP_EDITOR_SUBMIT_LABEL = 'Update';
const GROUP_EDITOR_CANCEL_LABEL = 'Cancel';
const REMOVAL_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const REMOVAL_CONFIRM_LABEL = 'Delete';
const GROUP_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/** The shared conflict vocabulary's sentence for a group that still holds roles. */
const GROUP_IN_USE_MESSAGE =
  'That role group still contains roles, so it was not removed. Move or delete its roles first.';

const FILTER_CONTROL_ID = 'role-list-group-filter';
const GROUP_NAME_CONTROL_ID = 'role-list-group-name';
const GROUP_DESCRIPTION_CONTROL_ID = 'role-list-group-description';

/** The DOM-only pseudo-entry values. Neither ever reaches the wire. */
const ALL_ROLES_VALUE = '-2';
const GLOBAL_ROLES_VALUE = '-1';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-5a9c2e4f1b834dd6bb18eb211c80319c-66bd6b7169203331-01';
const CORRELATION_ID = 'e93b6d18-4a72-4c05-8d31-7f2e0a5c9b64';

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
 * One listing row.
 *
 * ⚠ THE DEFAULT IDENTIFIER IS ZERO, because `dbo.Roles.RoleID` is seeded from zero: role zero is the
 * Administrators role of an installation and must render and link like any other.
 */
function roleRow(roleId = 0, overrides: Partial<RoleListItem> = {}): RoleListItem {
  return {
    roleId,
    roleName: 'Administrators',
    description: 'Portal Administration',
    serviceFee: null,
    billingPeriod: null,
    billingFrequency: null,
    trialFee: null,
    trialPeriod: null,
    trialFrequency: null,
    isPublic: false,
    autoAssignment: false,
    ...overrides,
  };
}

function roleGroup(roleGroupId = 4, overrides: Partial<RoleGroup> = {}): RoleGroup {
  return {
    roleGroupId,
    portalId: -1,
    roleGroupName: 'Paid Services',
    description: 'Groups that carry a fee',
    ...overrides,
  };
}

function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A page of roles.
 *
 * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture spelling it otherwise
 * flushes successfully and unwraps to no rows at all.
 */
function pageOf(
  items: readonly RoleListItem[],
  totalCount: number = items.length,
): PagedResponse<RoleListItem> {
  return {
    items,
    meta: { totalCount, pageIndex: 0, pageSize: 10, totalPages: Math.ceil(totalCount / 10) },
  };
}

// =====================================================================================================
// STRUCTURAL PROBES
// =====================================================================================================

/**
 * The field the Angular compiler writes the component definition onto.
 *
 * Held as a constant so the single unusual identifier in this file appears exactly once, and read
 * with bracket notation because `noPropertyAccessFromIndexSignature` is enabled.
 */
const COMPONENT_DEFINITION_FIELD = 'ɵcmp';

/** The flag the compiler sets from `changeDetection: ChangeDetectionStrategy.OnPush`. */
const ON_PUSH_FIELD = 'onPush';

/**
 * Whether a component type was compiled with `OnPush` change detection.
 *
 * ⚠ WHY THIS IS READ STRUCTURALLY RATHER THAN OBSERVED. The usual demonstration is to move an
 * input with `componentRef.setInput` and show the view repaints only once the component is marked
 * dirty. {@link RoleListComponent} does now declare one input — the route-bound account key — so
 * that move is possible, but it does not witness the STRATEGY: the input is a SIGNAL input, which
 * marks its consumer dirty under either strategy, and everything it feeds is a signal too. Nor is
 * the strategy observable through the store, for the same reason: every slice this screen renders
 * is a signal, and a signal read in a template marks its consumer dirty under EITHER strategy, so
 * the two behave identically from the outside. The compiled definition is therefore the only honest
 * witness, and the declaration is worth witnessing: the non-functional requirements make `OnPush`
 * mandatory on every component, and nothing else in this suite would notice its removal.
 *
 * Reads through `unknown` rather than through `any`, so no assertion here is unchecked.
 */
function declaresOnPush(componentType: unknown): boolean {
  const definition: unknown = fieldOf(componentType, COMPONENT_DEFINITION_FIELD);

  return fieldOf(definition, ON_PUSH_FIELD) === true;
}

/**
 * One named field of an unknown value, or `undefined` where the value cannot carry fields.
 *
 * ⚠ READ THROUGH `Reflect.get` RATHER THAN THROUGH A CAST. Asserting an unknown into an index
 * signature is the shape of assertion that hides a mistake — it type-checks against a value that may
 * be a number, a string or nothing at all, and fails at run time instead. `Reflect.get` needs only
 * that the target IS an object, which the guard establishes, so nothing here is unchecked.
 */
function fieldOf(carrier: unknown, field: string): unknown {
  if (carrier === null || (typeof carrier !== 'object' && typeof carrier !== 'function')) {
    return undefined;
  }

  return Reflect.get(carrier, field);
}

/**
 * Whether a signal is read-only — that is, whether it withholds both mutators.
 *
 * A `WritableSignal` carries `set` and `update`; the projection `asReadonly()` returns, and a
 * `computed()`, carry neither. Testing for their absence is what proves a component cannot write
 * to state it only renders.
 */
function withholdsMutators(source: unknown): boolean {
  if (typeof source !== 'function') {
    return false;
  }

  return !('set' in source) && !('update' in source);
}

describe('RoleListComponent', () => {
  let fixture: ComponentFixture<RoleListComponent>;
  let httpMock: HttpTestingController;
  let administersPortal: WritableSignal<boolean>;
  let notifySpy: jasmine.Spy;

  /**
   * Whether {@link create} has run in the CURRENT case.
   *
   * ⚠ A CLOSURE VARIABLE SURVIVES THE CASE THAT ASSIGNED IT, so `fixture` still holds the
   * previous case's component even in a case that never mounted one. This flag is what lets
   * teardown tell "nothing was mounted" from "something was", rather than destroying a fixture
   * that a previous case already destroyed. It is reset for every case, below.
   */
  let mounted = false;

  beforeEach(async () => {
    mounted = false;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    /*
     * ⚠ TENANT ADMINISTRATION IS THE INPUT TO THIS SCREEN, so it is held in a signal the cases can
     * move. The two CREATE affordances address `/roles/new` and `/role-groups/new`, both of which
     * declare the `PortalAdministrator` policy on their own routes — so the fact that decides whether
     * to offer them is the server's own determination, re-exposed by the identity projection as
     * `administersCurrentPortal`. Nothing else in this component's subtree touches the projection at
     * all, which is why a one-member double is honest here rather than a convenience.
     *
     * ⚠ THIS REPLACED A PERMISSION KEY, AND THE DIFFERENCE IS NOT COSMETIC. The affordances used to be
     * gated by `*hasPermission="'EDIT'"`. `EDIT` is a PERSISTED grant over a module or page instance,
     * carried in `ModulePermissions` and `TabPermissions`; it is consulted by nothing on the
     * role-administration path, so the old gate could both hide a screen the caller may reach and
     * offer one the router will refuse.
     *
     * Seeded as ADMINISTERING, so the ordinary cases below describe the screen an administrator sees.
     * The gating itself is proved separately, by taking the determination away.
     */
    administersPortal = signal<boolean>(true);

    await TestBed.configureTestingModule({
      imports: [RoleListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // ⚠ A ROUTE THAT ALWAYS MATCHES, because this screen now keeps its narrowing and its page in the
        // ADDRESS and writes them with a real navigation. An empty route table refuses every navigation, so
        // the write would silently fail and the read that follows the address change would never be issued -
        // which is exactly how thirty cases below started failing when the address contract was introduced.
        // The component under test is named as the target so the tree is genuinely resolvable.
        provideRouter([{ path: '**', component: RoleListComponent }]),
        RoleStore,
        { provide: AuthStore, useValue: { administersCurrentPortal: administersPortal } },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    /*
     * ⚠ THE FIXTURE IS TORN DOWN BEFORE THE BACKEND IS VERIFIED, AND THAT ORDER IS DELIBERATE.
     *
     * The confirmation this screen raises is a native `<dialog>`, and the top layer it opens into
     * belongs to the DOCUMENT rather than to the fixture — one Karma page hosts every spec in the
     * whole suite, so a confirmation left open here is still open when an unrelated spec runs and
     * its modal backdrop swallows that spec's clicks. Destroying explicitly closes it. Teardown
     * also cancels the store's outstanding reads, which is what makes the verification below a
     * statement about requests the SCREEN issued rather than about ones its teardown left behind.
     *
     * `create()` is what assigns the fixture, and a case may fail before reaching it, so the
     * guard is a real branch and not defensive noise. One case destroys its own fixture to observe
     * what teardown does; a second destruction is a no-op, so that case needs no exemption here.
     */
    if (mounted) {
      fixture.destroy();
      mounted = false;
    }

    /*
     * Doubles as a positive assertion: every case answers exactly the requests it provoked, so an
     * unanticipated call — a duplicated read, a mutation issued twice, a request the screen should
     * not have made at all — fails here even where nothing asserted its absence.
     */
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. The chained read is issued from `ngOnInit`, during this first pass. */
  function create(): void {
    fixture = TestBed.createComponent(RoleListComponent);
    mounted = true;
    fixture.detectChanges();
  }

  /**
   * Lets a navigation this screen started actually happen.
   *
   * The narrowing selector, the pager and the ordering controls write the ADDRESS rather than calling the
   * store, and a router navigation is asynchronous, so the read that follows one is not issued in the same
   * task. Anything that changes the narrowing, the page or the ordering must therefore be followed by this
   * before its request can be expected.
   */
  async function settleAddress(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /**
   * Answers the chained arrival read.
   *
   * ⚠ THE ORDER IS FIXED BY THE COMPONENT, NOT BY THIS HELPER. The groups are read first and the roles
   * read is issued only once they have answered, so answering them the other way round finds nothing.
   */
  function answerArrival(
    groups: readonly RoleGroup[] = [roleGroup()],
    roles: readonly RoleListItem[] = [roleRow()],
  ): TestRequest {
    expectRequest('GET', ROLE_GROUPS_URL, 'the group read').flush(envelope(groups));
    fixture.detectChanges();

    const rolesRead = expectRequest('GET', ROLES_URL, 'the role read');

    rolesRead.flush(pageOf(roles));
    fixture.detectChanges();

    return rolesRead;
  }

  /** Mounts the screen and settles its arrival. */
  function arrive(
    groups: readonly RoleGroup[] = [roleGroup()],
    roles: readonly RoleListItem[] = [roleRow()],
  ): void {
    create();
    answerArrival(groups, roles);
  }

  /**
   * The component's host element.
   *
   * ⚠ BY ASSIGNMENT, NOT BY CAST. `ComponentFixture.nativeElement` is declared `any`, so the
   * annotated local is what gives it a type — and it is a real check rather than a cosmetic one,
   * because a cast would equally have accepted a wrong element type and pushed the failure into
   * whichever assertion happened to touch it first.
   */
  function host(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /**
   * The one element matching `selector`, narrowed by a real check.
   *
   * ⚠ THIS EXISTS BECAUSE A NON-NULL ASSERTION IS NOT ALLOWED HERE. `element!.textContent` would
   * silence the compiler and then read `textContent` of `null` at run time, and Jasmine reports
   * that as a bare `TypeError` naming neither the selector nor the case's intent. Throwing on the
   * absence names the selector that was missing, which is the difference between a diagnosis and
   * a puzzle.
   */
  function queryOrFail<E extends Element>(root: ParentNode, selector: string): E {
    const found: E | null = root.querySelector<E>(selector);

    if (found === null) {
      throw new Error(`Expected to find "${selector}"`);
    }

    return found;
  }

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** The painted role rows. */
  function rows(): readonly HTMLTableRowElement[] {
    return queryAll<HTMLTableRowElement>('tr.data-table__row');
  }

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(controlId: string): E {
    const element = query<E>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as E;
  }

  /** Chooses a grouping filter by its rendered label. */
  async function chooseFilter(label: string): Promise<void> {
    const control = field<HTMLSelectElement>(FILTER_CONTROL_ID);
    const option: HTMLOptionElement | undefined = Array.from(control.options).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );

    expect(option).withContext(`the option labelled "${label}" is offered`).not.toBeUndefined();

    control.value = (option as HTMLOptionElement).value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    // ⚠ THE NARROWING NOW TRAVELS THROUGH THE ADDRESS, so the read it causes is issued in a LATER task.
    // Settling here rather than in each caller keeps the asynchrony where its cause is.
    await settleAddress();
  }

  /** A button by its rendered wording. */
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
   * Presses one of the two icon-only group commands, which carry no text of their own.
   *
   * ⚠ THEY CANNOT BE FOUND BY WORDING. Each is a real button whose visible content is an image plus
   * clipped text, so the class is the handle; the accessible name is asserted separately.
   */
  function pressGroupCommand(kind: 'edit' | 'remove'): void {
    const controls: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
      kind === 'remove'
        ? 'button.role-list__filter-action--danger'
        : 'button.role-list__filter-action',
    ).filter((candidate) =>
      kind === 'remove'
        ? candidate.classList.contains('role-list__filter-action--danger')
        : !candidate.classList.contains('role-list__filter-action--danger'),
    );

    expect(controls.length).withContext(`the group ${kind} command is offered`).toBeGreaterThan(0);

    (controls[0] as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** Whether one of the two icon-only group commands is on screen at all. */
  function hasGroupCommand(kind: 'edit' | 'remove'): boolean {
    return (
      queryAll<HTMLButtonElement>('button.role-list__filter-action').filter((candidate) =>
        kind === 'remove'
          ? candidate.classList.contains('role-list__filter-action--danger')
          : !candidate.classList.contains('role-list__filter-action--danger'),
      ).length > 0
    );
  }

  /** Presses a button of the OPEN CONFIRMATION, scoped to the dialogue. */
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

  /** Types into a control of the inline group editor. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVAL
  // ---------------------------------------------------------------------------------------------------

  /**
   * Answers the subject account's NAME read.
   *
   * ⚠ EVERY NARROWED CASE MUST ANSWER THIS, and `afterEach`'s `verify()` is what enforces it. The
   * screen names the person beneath the heading — `SecurityRoles.ascx.vb` L104-L105 read the account
   * for exactly that — so narrowing to an account provokes a second read alongside the membership
   * one, and a case that leaves it open fails as an unanticipated call.
   *
   * Answered SEPARATELY from the membership read rather than folded into it, because the two are
   * independent: the membership read can be refused while the name read succeeds, and vice versa,
   * and several cases below rely on exactly that.
   *
   * @param account The account key the read is expected for.
   * @param name The display name to answer with, or `undefined` to refuse the read.
   */
  function answerAccountNameRead(account: number, name?: string): void {
    const request = expectRequest('GET', `/api/v1/users/${account}`, 'the account name read');

    if (name === undefined) {
      request.flush(
        problem('user.not_found', 404, `Portal -1 has no member bearing identifier ${account}.`),
        { status: 404, statusText: 'Not Found' },
      );
    } else {
      request.flush(envelope(accountDetail(account, name)));
    }

    fixture.detectChanges();
  }

  /**
   * A minimal account-detail payload, carrying only what the subtitle reads.
   *
   * The decoder requires the whole contract, so this states every member rather than the two the
   * screen consumes — a partial payload would be rejected before the name reached the subtitle.
   *
   * @param userId The account key.
   * @param displayName The name to carry.
   * @returns The payload.
   */
  function accountDetail(userId: number, displayName: string): UserDetail {
    return {
      userId,
      portalId: -1,
      username: `login_${userId}`,
      firstName: 'Measured',
      lastName: 'Member',
      displayName,
      email: `member${userId}@example.test`,
      isSuperUser: false,
      affiliateId: null,
      isApproved: true,
      isLockedOut: false,
      isOnline: false,
      mustChangePassword: false,
      createdDate: '2024-01-05T09:00:00Z',
      lastLoginDate: null,
      lastActivityDate: null,
      lastLockoutDate: null,
      lastPasswordChangeDate: null,
      roles: [],
      canDelete: true,
    };
  }

  describe('arriving on the screen', () => {
    it('reads the groups first and the roles only once they have answered', () => {
      create();

      const groupRead = expectRequest('GET', ROLE_GROUPS_URL);

      // ⚠ THE ROLES READ DOES NOT EXIST YET, and that ordering is deliberate: the groups decide which
      // narrowing the roles read carries, so issuing both at once could read the roles under a filter
      // the screen is about to abandon.
      httpMock.expectNone((candidate) => candidate.url === ROLES_URL);
      expect(groupRead.request.params.keys()).withContext('no query string').toHaveSize(0);

      groupRead.flush(envelope([roleGroup()]));
      fixture.detectChanges();

      const roleRead = expectRequest('GET', ROLES_URL);

      expect(roleRead.request.url.startsWith('http')).withContext('relative').toBeFalse();
      // The default narrowing is the ungrouped scope, sent as a NAME rather than as a number.
      expect(roleRead.request.params.get('scope')).toBe('Ungrouped');
      expect(roleRead.request.params.has('roleGroupId')).withContext('no numeric group').toBeFalse();

      roleRead.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      expect(rows()).toHaveSize(1);
      httpMock.expectNone(() => true);
    });

    it('paints the heading, the caption and the three page-level links', () => {
      arrive();

      expect((query('h1')?.textContent ?? '').trim()).toBe(PAGE_TITLE);
      expect((query('caption')?.textContent ?? '').trim()).toBe(PAGE_TITLE);

      const links: readonly HTMLAnchorElement[] =
        queryAll<HTMLAnchorElement>('a.role-list__page-action');
      const labelled: readonly string[] = links.map((link) => (link.textContent ?? '').trim());

      expect(labelled).toContain(ADD_ROLE_LABEL);
      expect(labelled).toContain(ADD_ROLE_GROUP_LABEL);
      expect(labelled).toContain(MEMBERSHIP_SETTINGS_LABEL);
      expect(links.map((link) => link.getAttribute('href'))).toEqual([
        '/roles/new',
        '/role-groups/new',
        '/settings/membership',
      ]);
    });

    it('withholds the two CREATE affordances from a caller that does not administer the tenant', () => {
      // ⚠ AN AFFORDANCE AND NEVER AN ENFORCEMENT POINT. The API re-authorises every request and its
      // verdict governs, so hiding these links protects nothing — what it does is stop offering an
      // operator two screens the router is certain to refuse, which is the difference between an
      // application that knows what you may do and one that lets you find out by failing.
      //
      // ⚠ AND THE SETTINGS LINK IS NOT GATED, DELIBERATELY. Its route carries authentication alone,
      // so the outcome of following it is genuinely uncertain from here and the entry is shown.
      administersPortal.set(false);

      arrive();

      const labelled: readonly string[] = queryAll<HTMLAnchorElement>(
        'a.role-list__page-action',
      ).map((link) => (link.textContent ?? '').trim());

      expect(labelled).not.toContain(ADD_ROLE_LABEL);
      expect(labelled).not.toContain(ADD_ROLE_GROUP_LABEL);
      expect(labelled).toEqual([MEMBERSHIP_SETTINGS_LABEL]);
    });

    it('restores the CREATE affordances the moment the determination arrives', () => {
      // The gate is re-read from the identity rather than cached at first render, so an administrator
      // whose determination arrives after the screen has painted — which is the ORDINARY case, the
      // identity being fetched while the sign-in snapshot leaves the derived fact false — is not left
      // looking at a screen it cannot act on.
      administersPortal.set(false);

      arrive();

      expect(queryAll('a.role-list__page-action')).toHaveSize(1);

      administersPortal.set(true);
      fixture.detectChanges();

      expect(
        queryAll<HTMLAnchorElement>('a.role-list__page-action').map((link) =>
          (link.textContent ?? '').trim(),
        ),
      ).toEqual([ADD_ROLE_LABEL, ADD_ROLE_GROUP_LABEL, MEMBERSHIP_SETTINGS_LABEL]);
    });

    it('addresses both row commands by the role identifier, including role zero', () => {
      arrive([roleGroup()], [roleRow(0)]);

      const row: HTMLTableRowElement = rows()[0] as HTMLTableRowElement;
      const links: readonly HTMLAnchorElement[] = Array.from(
        row.querySelectorAll<HTMLAnchorElement>('a'),
      );

      // ⚠ ROLE ZERO ADDRESSES CORRECTLY. A truthiness test on the identifier would leave the
      // administrators role of an installation with no working commands at all.
      expect(links.map((link) => link.getAttribute('href'))).toContain('/roles/0');
      expect(links.map((link) => link.getAttribute('href'))).toContain('/roles/0/users');
      expect(links.map((link) => (link.textContent ?? '').trim())).toContain(EDIT_LABEL);
      expect(links.map((link) => (link.textContent ?? '').trim())).toContain(MANAGE_USERS_LABEL);
    });

    it('hides the filter row entirely when the tenant has no groups', () => {
      arrive([], [roleRow()]);

      // With nothing to filter by, a picker holding only its two pseudo-entries would be a control that
      // cannot do anything — so the whole row is withheld rather than shown empty.
      expect(query(`#${FILTER_CONTROL_ID}`)).toBeNull();
      expect(hasGroupCommand('edit')).withContext('and no group commands').toBeFalse();
    });

    it('falls back to the all-roles scope when the tenant has no groups', () => {
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([]));
      fixture.detectChanges();

      const roleRead = expectRequest('GET', ROLES_URL);

      // The ungrouped scope is meaningless where no group exists, so the screen reads every role
      // instead of showing an empty list that looks like a tenant with no roles.
      expect(roleRead.request.params.get('scope')).toBe('All');

      roleRead.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    it('shows the grid own wait while the chained read is outstanding', () => {
      create();

      expect(query('td.data-table__message[data-placeholder]'))
        .withContext('the table carries the wait')
        .not.toBeNull();
      // The shared table renders the wait itself, so this screen must not add a second indicator.
      expect(
        queryAll<HTMLElement>('app-loading-spinner').filter(
          (indicator) => indicator.closest('td.data-table__message') === null,
        ),
      )
        .withContext('no second indicator')
        .toHaveSize(0);

      answerArrival();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — FILTERING BY GROUP
  // ---------------------------------------------------------------------------------------------------

  // ---------------------------------------------------------------------------------------------------
  // NARROWED TO ONE ACCOUNT
  //
  // MIGRATION: `Users.ascx.vb:L542` built the account listing's roles command as
  // `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD")`, and the screen it reached served TWO
  // MODES from one page keyed by either a role or an account (`SecurityRoles.ascx.vb:L413-L418`).
  // The target had only the role-keyed mode, so the command discarded the row's account and landed
  // the operator on every role in the tenant. These cases describe the account-keyed mode.
  // ---------------------------------------------------------------------------------------------------

  describe('narrowed to one account', () => {
    /**
     * Mounts the screen already narrowed to one account, and settles all three arrival reads.
     *
     * ⚠ THREE READS, NOT TWO. The group read and the chained role read are the screen's ordinary
     * arrival; the membership read is issued by the effect that watches the account key, on the
     * same first pass. Answering them in this order is required — the role read does not exist
     * until the group read has answered.
     *
     * @param held The memberships the account holds.
     * @param account The account key the address carries.
     * @param roles The tenant-wide listing, which must remain reachable behind the narrowing.
     */
    function arriveForAccount(
      held: readonly RoleListItem[] = [roleRow(0)],
      account: number = ACCOUNT_ID,
      roles: readonly RoleListItem[] = [roleRow(7, { roleName: 'Subscribers' })],
    ): void {
      fixture = TestBed.createComponent(RoleListComponent);
      mounted = true;
      fixture.componentRef.setInput('userId', String(account));
      fixture.detectChanges();

      expectRequest('GET', ROLE_GROUPS_URL, 'the group read').flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL, 'the role read').flush(pageOf(roles));
      fixture.detectChanges();
      expectRequest('GET', accountUrl(account), 'the membership read').flush(envelope(held));
      fixture.detectChanges();
      answerAccountNameRead(account);
    }

    /**
     * The membership address of one account.
     *
     * @param account The account key.
     * @returns The address the transport builds for it.
     */
    function accountUrl(account: number): string {
      return `/api/v1/users/${account}/roles`;
    }

    /** The wording rendered beneath the heading, or the empty string when there is none. */
    function subtitle(): string {
      return (query<HTMLElement>('.page-header__subtitle')?.textContent ?? '').trim();
    }

    /**
     * The role names the grid is currently rendering, in order.
     *
     * Located by its HEADING rather than by a fixed offset: the first cell of a row carries the
     * edit command, and a positional read would silently compare command labels instead of names.
     */
    function renderedRoleNames(): readonly string[] {
      const headings: readonly HTMLTableCellElement[] = queryAll<HTMLTableCellElement>(
        'th.data-table__header',
      );
      // Matched by CONTAINMENT, because a sortable heading nests a button and a sort indicator
      // alongside its label, so its text is not the label alone.
      const index: number = headings.findIndex((cell) =>
        (cell.textContent ?? '').trim().startsWith('Name'),
      );

      expect(index).withContext('a column headed "Name" is rendered').toBeGreaterThan(-1);

      return rows().map((row) => {
        const cell: HTMLTableCellElement | undefined = Array.from(
          row.querySelectorAll<HTMLTableCellElement>('td,th'),
        )[index];

        return (cell?.textContent ?? '').trim();
      });
    }

    /** The label of every page-level action link, in order. */
    function pageActions(): readonly string[] {
      return queryAll<HTMLElement>('.role-list__page-action').map((each) =>
        (each.textContent ?? '').trim(),
      );
    }

    it('READS THE ACCOUNT\u2019S MEMBERSHIPS on arrival, at the account address', () => {
      arriveForAccount();

      // Proved by the read having been claimed inside the helper: the backend verification in
      // teardown fails on an unclaimed request, so a screen that issued none would fail there.
      expect(renderedRoleNames()).toEqual(['Administrators']);
    });

    it('RENDERS THE MEMBERSHIPS rather than the tenant-wide listing', () => {
      // ⚠ THE DEFECT THIS FIXES. The tenant listing answered with a role the account does NOT
      // hold; rendering it would show the operator every role in the tenant under a heading that
      // names one person.
      arriveForAccount([roleRow(0, { roleName: 'Administrators' })], ACCOUNT_ID, [
        roleRow(7, { roleName: 'Subscribers' }),
        roleRow(8, { roleName: 'Translators' }),
      ]);

      expect(renderedRoleNames()).toEqual(['Administrators']);
      expect(renderedRoleNames()).not.toContain('Subscribers');
    });

    it('names the account beneath the heading, with the count it came for', () => {
      arriveForAccount([roleRow(0), roleRow(7, { roleName: 'Subscribers' })]);

      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID} \u2014 2 roles`);
    });

    it('says ROLE rather than ROLES for a single membership', () => {
      arriveForAccount([roleRow(0)]);

      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID} \u2014 1 role`);
    });

    it('says nothing about a count while the membership read is still outstanding', () => {
      fixture = TestBed.createComponent(RoleListComponent);
      mounted = true;
      fixture.componentRef.setInput('userId', String(ACCOUNT_ID));
      fixture.detectChanges();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow(7)]));
      fixture.detectChanges();

      const outstanding = expectRequest('GET', USER_ROLES_URL);

      // The account is named — the operator asked about it — but no count is claimed.
      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID}`);

      outstanding.flush(envelope([roleRow(0)]));
      fixture.detectChanges();

      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID} \u2014 1 role`);

      // The NAME read is answered last, which is what makes the two assertions above meaningful:
      // both were made while the name was still unknown, so both exercise the identifier fallback.
      answerAccountNameRead(ACCOUNT_ID, 'Measured Member');

      // And once the name lands, the person is named rather than keyed.
      expect(subtitle()).toBe('Roles held by Measured Member \u2014 1 role');
    });

    it('OFFERS THE WAY BACK to every role in the tenant, at this screen\u2019s own address', () => {
      arriveForAccount();

      // ⚠ SELECTED BY LABEL, NOT BY POSITION. Narrowing to an account now offers TWO affordances —
      // the way BACK to the person and the way SIDEWAYS to every role — and a positional read would
      // silently assert about whichever happened to come first.
      const link = queryAll<HTMLAnchorElement>('.role-list__page-action').find(
        (candidate) => (candidate.textContent ?? '').trim() === 'Show All Roles',
      );

      expect(link).withContext('the unnarrowing affordance is offered').not.toBeUndefined();
      // Addressing this same route WITHOUT the parameter is what clears the narrowing, so no
      // second route and no separate command are needed.
      expect((link as HTMLAnchorElement).getAttribute('href')).toBe('/roles');

      // The way back to the account the operator came from, which is the FIRST of the two.
      expect(pageActions()[0]).toBe('Back to Account');
      expect(pageActions()[1]).toBe('Show All Roles');

      const back = queryAll<HTMLAnchorElement>('.role-list__page-action').find(
        (candidate) => (candidate.textContent ?? '').trim() === 'Back to Account',
      );

      expect((back as HTMLAnchorElement).getAttribute('href')).toBe(`/users/${ACCOUNT_ID}`);
    });

    it('offers NO account affordance and issues NO membership read when no account is the subject', () => {
      arrive();

      expect(pageActions()).not.toContain('Show All Roles');
      expect(subtitle()).toBe('');
      // Nothing was claimed at the account address, and teardown's verification is what proves it.
      httpMock.expectNone(
        (candidate) => candidate.url.startsWith('/api/v1/users/'),
        'no membership read without an account',
      );
    });

    it('RE-READS FOR THE NEW ACCOUNT when the address moves between two accounts', () => {
      // ⚠ THE COMPONENT IS NOT RECREATED, because both addresses match the same route. The input
      // moves and the effect re-reads, which is exactly the case the store\u2019s dispatch-time
      // account recording exists to make safe.
      arriveForAccount([roleRow(0, { roleName: 'Administrators' })]);

      fixture.componentRef.setInput('userId', String(OTHER_ACCOUNT_ID));
      fixture.detectChanges();

      expectRequest('GET', OTHER_USER_ROLES_URL, 'the second membership read').flush(
        envelope([roleRow(9, { roleName: 'Translators' })]),
      );
      fixture.detectChanges();

      expect(renderedRoleNames()).toEqual(['Translators']);
      expect(subtitle()).toBe(`Roles held by account ${OTHER_ACCOUNT_ID} \u2014 1 role`);

      // The NAME is re-read for the second account too, and until it answers the subtitle keys the
      // new account rather than naming the previous one.
      answerAccountNameRead(OTHER_ACCOUNT_ID, 'Second Member');

      expect(subtitle()).toBe('Roles held by Second Member \u2014 1 role');
    });

    it('SHOWS NOTHING OF THE PREVIOUS ACCOUNT while a second account is read', () => {
      // ⚠ THE MOST CONSEQUENTIAL CASE HERE, AND IT FOUND A REAL DEFECT. Between the switch and the
      // answer the store recorded the NEW subject while still holding the OLD account's rows, so
      // the two agreed and the subtitle claimed "account 43 — 1 role" about account 42's single
      // membership. The store now discards a previous answer when the subject changes, which is
      // what makes both statements below true at once.
      //
      // The GRID shows the shared table's waiting state for the duration — waiting wins over both
      // data and emptiness there — so the assertion about the grid is that the previous account's
      // row is nowhere on screen, rather than that some other row is.
      arriveForAccount([roleRow(0, { roleName: 'Administrators' })], ACCOUNT_ID, [
        roleRow(7, { roleName: 'Subscribers' }),
      ]);
      expect(renderedRoleNames()).toEqual(['Administrators']);

      fixture.componentRef.setInput('userId', String(OTHER_ACCOUNT_ID));
      fixture.detectChanges();

      expect(renderedRoleNames())
        .withContext('the previous account\u2019s membership is not painted under the new name')
        .not.toContain('Administrators');
      // The count is the part that reached the operator, and it is now withheld until the new
      // account answers.
      expect(subtitle()).toBe(`Roles held by account ${OTHER_ACCOUNT_ID}`);

      expectRequest('GET', OTHER_USER_ROLES_URL).flush(
        envelope([roleRow(9, { roleName: 'Translators' })]),
      );
      fixture.detectChanges();

      expect(renderedRoleNames()).toEqual(['Translators']);
      expect(subtitle()).toBe(`Roles held by account ${OTHER_ACCOUNT_ID} \u2014 1 role`);

      // ⚠ THE SECOND ACCOUNT'S NAME READ IS ANSWERED WITH THE FIRST ACCOUNT'S NAME ON PURPOSE, and
      // the assertion is that it is IGNORED. The screen pairs a held name with the key it belongs
      // to, so an answer that arrives for a superseded subject cannot name the current one — the
      // same protection the rows already have, applied to the label.
      answerAccountNameRead(OTHER_ACCOUNT_ID, 'Second Member');

      expect(subtitle())
        .withContext('the name answered for THIS account is the one shown')
        .toBe('Roles held by Second Member \u2014 1 role');
    });

    it('shows NO rows and states the refusal when the membership read is refused', () => {
      fixture = TestBed.createComponent(RoleListComponent);
      mounted = true;
      fixture.componentRef.setInput('userId', String(ACCOUNT_ID));
      fixture.detectChanges();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow(7, { roleName: 'Subscribers' })]));
      fixture.detectChanges();

      expectRequest('GET', USER_ROLES_URL).flush(problem('forbidden', 403, 'The authenticated caller is not permitted to perform this operation.'), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      /*
       * ⚠ THIS WAS A CRITICAL FALSE-DATA DEFECT AND THE ASSERTION IS NOW ITS INVERSE. The screen used
       * to render the TENANT listing here, which meant `/roles?userId=999` - whose read is refused
       * with `404` "Portal -1 has no member bearing identifier 999" - displayed every role in the
       * portal beneath the subtitle "Roles held by account 999". It asserted in writing that a
       * non-existent account held every role, with no banner, no notification and no console error.
       *
       * The rows that answer "what roles exist in this tenant" must not stand in for an answer to
       * "what roles does this account hold" that was never given. Showing none of them, and stating
       * the refusal, is the only presentation that claims nothing untrue.
       *
       * The prior reasoning - that an empty grid reads as "this account holds nothing" - is still
       * respected, and is why the refusal is now SURFACED: the banner carries the server's own
       * sentence, so the empty grid is read together with the reason it is empty rather than as a
       * membership fact. The case below proves a genuinely empty ANSWER is still rendered as one, so
       * the two are not conflated.
       */
      expect(renderedRoleNames())
        .withContext('no borrowed rows from a different question')
        .toEqual([]);
      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID}`);

      const banner = query('app-error-banner');

      expect(banner).withContext('the refusal is stated').not.toBeNull();
      expect(banner?.textContent ?? '')
        .withContext("and it carries the server's own explanation")
        .toContain('not permitted');

      // ⚠ THE NAME READ IS INDEPENDENT OF THE MEMBERSHIP READ, and it is REFUSED here too so that
      // this case proves the pairing the screen relies on: a refused name leaves the subtitle keyed
      // by identifier rather than blank, and it raises NO second banner of its own. One failure slot
      // serves this screen, and the membership refusal is the more informative of the two.
      answerAccountNameRead(ACCOUNT_ID);

      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID}`);
      expect(queryAll('app-error-banner'))
        .withContext('the refused name read does not add a second banner')
        .toHaveSize(1);
    });

    it('renders an account that holds NO role as an empty grid, not as the tenant listing', () => {
      // ⚠ THE COUNTERPART TO THE CASE ABOVE. An empty ANSWER is a successful answer and must be
      // rendered as one; only a failed or mismatched read falls back.
      arriveForAccount([], ACCOUNT_ID, [roleRow(7, { roleName: 'Subscribers' })]);

      expect(renderedRoleNames()).not.toContain('Subscribers');
      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID} \u2014 0 roles`);
    });

    it('narrows to an account keyed NOUGHT, which is not an absence', () => {
      // ⚠ A truthiness test on the key would treat this address as unnarrowed and read nothing.
      arriveForAccount([roleRow(0)], 0);

      expect(subtitle()).toBe('Roles held by account 0 \u2014 1 role');
      expect(pageActions()).toContain('Show All Roles');
      expect(USER_ZERO_ROLES_URL).toBe(accountUrl(0));
    });

    it('IGNORES a parameter that is not a whole number, rather than reading account NaN', () => {
      fixture = TestBed.createComponent(RoleListComponent);
      mounted = true;
      fixture.componentRef.setInput('userId', 'not-a-key');
      fixture.detectChanges();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow(7, { roleName: 'Subscribers' })]));
      fixture.detectChanges();

      expect(renderedRoleNames()).toEqual(['Subscribers']);
      expect(subtitle()).toBe('');
      httpMock.expectNone(
        (candidate) => candidate.url.startsWith('/api/v1/users/'),
        'a malformed key reads nothing',
      );
    });

    it('RETURNS to every role when the parameter is dropped from the address', () => {
      arriveForAccount([roleRow(0, { roleName: 'Administrators' })], ACCOUNT_ID, [
        roleRow(7, { roleName: 'Subscribers' }),
      ]);

      fixture.componentRef.setInput('userId', undefined);
      fixture.detectChanges();

      expect(renderedRoleNames()).toEqual(['Subscribers']);
      expect(subtitle()).toBe('');
      expect(pageActions()).not.toContain('Show All Roles');
    });

    it('keeps the tenant-wide affordances beside the account ones, in that order', () => {
      arriveForAccount();

      // The way BACK to the person comes first, then the way SIDEWAYS to every role, then the
      // create affordances, which remain gated as they were — so nothing is lost by narrowing.
      //
      // ⚠ THE RETURN PATH IS THE ADDITION THAT CLOSED THE REPORTED DEFECT: an operator who pressed a
      // per-account command on the account listing could not get back to the account from here.
      expect(pageActions()).toEqual([
        'Back to Account',
        'Show All Roles',
        'Add New Role',
        'Add New Role Group',
        // Worded as the legacy `UserSettings.Action` was, which is what this screen already renders.
        'User Settings',
      ]);
    });
  });

  // =====================================================================================================
  // ORDERING
  // =====================================================================================================
  /**
   * SORTING IS A NET-NEW AFFORDANCE HERE, AND ITS BOUNDS ARE THE ENDPOINT'S.
   *
   * It was previously declined on the ground that the legacy grid had no sort affordance. That is true, and
   * a case-insensitive census across BOTH legacy trees finds `AllowSorting` exactly ONCE in either of them,
   * in `Website/admin/Files/filemanager.ascx`, a screen the AAP places out of scope - so not one in-scope
   * legacy grid could be reordered, INCLUDING the module listing that has offered sorting since it was
   * written. The census says the same thing about every grid in this application and therefore cannot
   * support the affordance on one screen and its absence on the rest.
   *
   * What bounds it is `SortableFields.Roles` in
   * `backend/src/DnnMigration.Application/Validation/SortableFields.cs`, which permits every member of the
   * role list contract. Ten of the eleven are offered; `RoleId` is not, because this grid paints no
   * identifier column and a control cannot sit on a column that is not shown.
   */
  describe('ordering', () => {
    /** The rendered sort controls, in column order. */
    function sortControls(): readonly HTMLButtonElement[] {
      return queryAll<HTMLButtonElement>('th.data-table__header button.data-table__sort');
    }

    /** The heading cells reporting an active direction. */
    function announcedDirections(): readonly string[] {
      return queryAll<HTMLElement>('th.data-table__header')
        .map((cell) => cell.getAttribute('aria-sort') ?? '')
        .filter((value) => value === 'ascending' || value === 'descending');
    }

    it('paints a control on the ten permitted data columns and on neither command column', () => {
      arrive();

      const names: readonly string[] = sortControls().map(
        (control) => control.getAttribute('aria-label') ?? '',
      );

      expect(names).toHaveSize(10);

      // Each control states the action and keeps the visible heading text, per WCAG 2.5.3.
      for (const control of sortControls()) {
        const accessibleName: string = control.getAttribute('aria-label') ?? '';

        expect(accessibleName.startsWith('Sort by ')).withContext(accessibleName).toBeTrue();
        expect(accessibleName).toContain((control.textContent ?? '').trim());
      }
    });

    it('re-reads ordered by the pressed column, in the server spelling, from the first page', async () => {
      arrive();

      // The first data control is the role name, whose column key IS the endpoint's sort name.
      sortControls()[0]?.click();
      await settleAddress();

      const ordered: TestRequest = expectRequest('GET', ROLES_URL, 'the ordered role read');

      expect(ordered.request.params.get('sortBy')).toBe('roleName');
      expect(ordered.request.params.get('sortDir')).toBe('Ascending');
      expect(ordered.request.params.get('pageIndex')).toBe('0');

      ordered.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      expect(announcedDirections()).toEqual(['ascending']);
    });

    it('reverses on the second press and CLEARS on the third', async () => {
      arrive();

      const press = async (): Promise<void> => {
        sortControls()[0]?.click();
        await settleAddress();
      };

      await press();
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()]));
      fixture.detectChanges();

      await press();
      const descending: TestRequest = expectRequest('GET', ROLES_URL, 'the descending read');
      expect(descending.request.params.get('sortDir')).toBe('Descending');
      descending.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      // THE THIRD PRESS RETURNS THE LISTING TO THE SERVER'S OWN ORDER, which is the state the screen
      // arrives in and which a two-step toggle left reachable only by reloading the page. Asserted on the
      // WIRE because the omission is the point.
      await press();
      const cleared: TestRequest = expectRequest('GET', ROLES_URL, 'the unordered read');
      expect(cleared.request.params.has('sortBy')).withContext('no key is sent').toBeFalse();
      expect(cleared.request.params.has('sortDir')).withContext('no direction is sent').toBeFalse();
      cleared.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      expect(announcedDirections()).withContext('no column reports itself sorted').toEqual([]);
    });

    /**
     * ⚠ THE AFFORDANCE IS WITHHELD WHEN AN ACCOUNT IS THE SUBJECT, and this is not tidiness. Those rows
     * come from a different and unpaged slice - the memberships the account holds - which the ordering
     * command cannot reach: it re-reads the browsable listing, so a press would issue a request whose
     * answer this grid never renders, and the heading would then announce an order the visible rows are not
     * in. An affordance that does nothing is worse than none.
     */
    it('paints no sort control at all while an account is the subject', () => {
      fixture = TestBed.createComponent(RoleListComponent);
      mounted = true;
      fixture.componentRef.setInput('userId', '3');
      fixture.detectChanges();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow(7)]));
      fixture.detectChanges();
      expectRequest('GET', '/api/v1/users/3/roles').flush(envelope([roleRow(0)]));
      fixture.detectChanges();
      answerAccountNameRead(3, 'Runtime Host');

      expect(sortControls()).toHaveSize(0);

      // And no heading claims a sort state either way.
      expect(announcedDirections()).toEqual([]);
    });
  });

  describe('filtering by role group', () => {
    it('offers both pseudo-entries plus one option per group', () => {
      arrive([
        roleGroup(4, { roleGroupName: 'Paid Services' }),
        roleGroup(5, { roleGroupName: 'Trials' }),
      ]);

      const options: readonly string[] = Array.from(
        field<HTMLSelectElement>(FILTER_CONTROL_ID).options,
      ).map((option) => (option.textContent ?? '').trim());

      expect(options).toContain(ALL_ROLES_OPTION_LABEL);
      expect(options).toContain(GLOBAL_ROLES_OPTION_LABEL);
      expect(options).toContain('Paid Services');
      expect(options).toContain('Trials');
    });

    it('sends a named scope for each pseudo-entry, never its negative number', async () => {
      arrive();

      await chooseFilter(ALL_ROLES_OPTION_LABEL);

      const all = expectRequest('GET', ROLES_URL, 'the all-roles read');

      // ⚠ THE NEGATIVE VALUES EXIST IN THE DOM ONLY, because a select must carry strings. What travels
      // is a named scope; a `roleGroupId` of minus two would be a request for a group that cannot exist.
      expect(all.request.params.get('scope')).toBe('All');
      expect(all.request.params.has('roleGroupId')).toBeFalse();
      expect(all.request.urlWithParams).not.toContain(ALL_ROLES_VALUE);

      all.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      await chooseFilter(GLOBAL_ROLES_OPTION_LABEL);

      const global = expectRequest('GET', ROLES_URL, 'the ungrouped read');

      expect(global.request.params.get('scope')).toBe('Ungrouped');
      expect(global.request.urlWithParams).not.toContain(GLOBAL_ROLES_VALUE);

      global.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    it('sends the real identifier for a chosen group, and returns to the first page', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter('Paid Services');

      const call = expectRequest('GET', ROLES_URL, 'the grouped read');

      expect(call.request.params.get('roleGroupId')).toBe('4');
      expect(call.request.params.has('scope')).withContext('no scope name').toBeFalse();
      // A coordinate measured against one match set does not address the same rows once the set changes.
      expect(call.request.params.get('pageIndex')).toBe('0');

      call.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('reveals the group commands once a real group is chosen, and only then', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], [roleRow()]);

      // Neither pseudo-entry is a resource, so there is nothing to edit or remove while one is chosen.
      expect(hasGroupCommand('edit')).withContext('nothing to edit').toBeFalse();

      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()]));
      fixture.detectChanges();

      expect(hasGroupCommand('edit')).withContext('the group can be edited').toBeTrue();
    });

    it('withholds the group removal while the group still holds roles', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()]));
      fixture.detectChanges();

      // ⚠ THE CLIENT'S OWN PRECONDITION: a group holding roles cannot be removed, so the affordance is
      // withheld rather than offered and refused. The server's conflict is the backstop, not the gate.
      expect(hasGroupCommand('remove')).withContext('withheld while occupied').toBeFalse();
    });

    it('offers the group removal once the group is empty', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();

      expect(hasGroupCommand('remove')).withContext('offered when empty').toBeTrue();
    });

    /**
     * ⚠ A NARROWING CHANGED TWICE MUST NOT PAINT THE FIRST ANSWER.
     *
     * The legacy screen posted back for each narrowing and the browser discarded the earlier response
     * for us. Nothing discards it here, so the store holds one handle per read and abandons the
     * outstanding one before issuing its replacement. Were it not to, the slower first answer would
     * land last and paint rows that do not belong to the narrowing on screen — a silently wrong grid.
     */
    it('abandons a superseded narrowing, so its answer cannot paint over the newest one', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter('Paid Services');

      const superseded = expectRequest('GET', ROLES_URL, 'the grouped read');

      expect(superseded.request.params.get('roleGroupId')).toBe('4');

      // The narrowing changes again before the first answer arrives.
      await chooseFilter(ALL_ROLES_OPTION_LABEL);

      const newest = expectRequest('GET', ROLES_URL, 'the all-roles read');

      expect(newest.request.params.get('scope')).toBe('All');
      expect(superseded.cancelled).withContext('the superseded read is abandoned').toBeTrue();

      newest.flush(pageOf([roleRow(3, { roleName: 'Newest Answer' })]));
      fixture.detectChanges();

      // Only the newest answer is on screen, and the abandoned one can no longer be answered at all.
      const painted: string = rows()
        .map((row) => (row.textContent ?? '').trim())
        .join(' | ');

      expect(painted).withContext('the newest answer is painted').toContain('Newest Answer');
      expect(painted).withContext('the superseded answer is not').not.toContain('Stale Answer');
      expect(() => superseded.flush(pageOf([roleRow(9, { roleName: 'Stale Answer' })]))).toThrowError(
        /cancelled/i,
      );
      expect(rows()).withContext('one row, from the newest read').toHaveSize(1);
      httpMock.verify({ ignoreCancelled: true });
    });

    /**
     * ⚠ THE READ IS OWNED BY THE ROOT-PROVIDED STORE, NOT BY THIS SCREEN.
     *
     * `RoleStore` is declared `providedIn: 'root'`, so it deliberately outlives every screen that reads
     * through it: navigating away and back must not throw away a listing that has already been paid
     * for. Abandonment therefore belongs to the store's own teardown, and this proves BOTH halves —
     * leaving the screen does not abandon the read, and the store's teardown does.
     */
    it('leaves an outstanding narrowing to the store, which abandons it on its own teardown', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter('Paid Services');

      const outstanding = expectRequest('GET', ROLES_URL, 'the grouped read');
      const store = TestBed.inject(RoleStore);

      fixture.destroy();

      expect(outstanding.cancelled).withContext('the store still owns the read').toBeFalse();

      store.ngOnDestroy();

      // Landing after teardown would write to state nothing is reading, so the store lets it go.
      expect(outstanding.cancelled).withContext('abandoned with the store').toBeTrue();
      expect(notifications()).withContext('nothing announced for abandoned work').toHaveSize(0);
      httpMock.verify({ ignoreCancelled: true });
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE INLINE GROUP EDITOR
  // ---------------------------------------------------------------------------------------------------

  describe('editing the chosen role group', () => {
    /** Arrives with a real group chosen, which is the editor's precondition. */
    async function arriveWithGroup(roles: readonly RoleListItem[] = [roleRow()]): Promise<void> {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services', description: 'Fee-bearing' })], roles);
      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf(roles));
      fixture.detectChanges();
    }

    it('opens with the chosen group own values', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');

      expect(field<HTMLInputElement>(GROUP_NAME_CONTROL_ID).value).toBe('Paid Services');
      expect(field<HTMLTextAreaElement>(GROUP_DESCRIPTION_CONTROL_ID).value).toBe('Fee-bearing');
      // ⚠ THE EDITOR EDITS THE GROUP THE FILTER NAMES. Opening it against anything else — the first
      // group, say — would let a person edit one group while looking at another's roles.
      expect(httpMock.match(() => true)).withContext('opening reads nothing').toHaveSize(0);
    });

    it('puts the trimmed values and answers 200 with the stored group', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, '  Premium Services  ');
      type(GROUP_DESCRIPTION_CONTROL_ID, '  Now premium  ');

      press(GROUP_EDITOR_SUBMIT_LABEL);

      const call = expectRequest('PUT', roleGroupUrl(4), 'the replacement');

      // Trimmed on the way out, because surrounding space in a name is invisible in every listing while
      // comparing as a different value.
      expect(call.request.body).toEqual({
        roleGroupName: 'Premium Services',
        description: 'Now premium',
      });

      // ⚠ A GROUP REPLACEMENT ANSWERS 200 WITH THE STORED RECORD, which the store merges in place — so
      // no re-read follows.
      call.flush(envelope(roleGroup(4, { roleGroupName: 'Premium Services' })), {
        status: 200,
        statusText: 'OK',
      });
      fixture.detectChanges();

      expect(httpMock.match(() => true)).withContext('nothing is re-read').toHaveSize(0);
      expect(notifications()).toEqual([
        { severity: 'success', message: 'Role group "Premium Services" was updated.' },
      ]);
      // The editor closes on submission, so a second press cannot resubmit.
      expect(query(`#${GROUP_NAME_CONTROL_ID}`)).withContext('the editor closes').toBeNull();
    });

    it('sends an emptied description as null rather than as blank text', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_DESCRIPTION_CONTROL_ID, '   ');

      press(GROUP_EDITOR_SUBMIT_LABEL);

      const call = expectRequest('PUT', roleGroupUrl(4));

      // A description of nothing but space is an absence, and the schema spells an absence as null here.
      expect((call.request.body as { description: string | null }).description).toBeNull();

      call.flush(envelope(roleGroup(4)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
    });

    it('refuses an emptied name in the legacy wording and sends nothing', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, '');

      press(GROUP_EDITOR_SUBMIT_LABEL);

      // The legacy sentence carried a leading line break, which is stripped rather than rendered as
      // markup — the wording survives, the markup does not.
      expect(textOf('.form-field__error')).toContain(GROUP_NAME_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(query(`#${GROUP_NAME_CONTROL_ID}`)).withContext('the editor stays open').not.toBeNull();
    });

    it('closes without sending anything when abandoned', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, 'Premium Services');
      press(GROUP_EDITOR_CANCEL_LABEL);

      expect(query(`#${GROUP_NAME_CONTROL_ID}`)).withContext('closed').toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(notifications()).toHaveSize(0);

      // Re-opening starts from the stored values rather than resurrecting the abandoned edit.
      pressGroupCommand('edit');
      expect(field<HTMLInputElement>(GROUP_NAME_CONTROL_ID).value).toBe('Paid Services');
    });

    /**
     * The legacy screen refused a duplicate group name with `EditGroups.ascx.resx` →
     * `DuplicateRoleGroup.Text`. The modern wire spelling of that same condition is
     * `role_group.name_duplicate`, and the sentence an operator reads is the PUBLISHED one rather than
     * whatever prose the server happened to send — the published sentence says what to do next.
     */
    it('reports a duplicate group name at 409 in the published wording, not the server prose', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, 'Trials');

      press(GROUP_EDITOR_SUBMIT_LABEL);

      expectRequest('PUT', roleGroupUrl(4)).flush(
        problem('role_group.name_duplicate', 409, 'A group with that name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // A conflict is an error under the shared classification, and the sentence is the published
      // conflict wording rather than the server's terser one.
      expect(notifications()).toEqual([
        {
          severity: 'error',
          message: 'A role group with the same name already exists. The new group was not added.',
        },
      ]);
      expect(notifications()[0]?.message)
        .withContext('the server prose is superseded, not forwarded')
        .not.toBe('A group with that name already exists.');
    });

    it('reports a refused replacement as a warning at 403', async () => {
      await arriveWithGroup();

      pressGroupCommand('edit');
      press(GROUP_EDITOR_SUBMIT_LABEL);

      expectRequest('PUT', roleGroupUrl(4)).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()[0]?.severity).toBe('warning');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — REMOVING A GROUP
  // ---------------------------------------------------------------------------------------------------

  describe('removing the chosen role group', () => {
    /** Arrives with a real, EMPTY group chosen, which is the removal's precondition. */
    async function arriveWithEmptyGroup(): Promise<void> {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();
    }

    it('asks first, then removes with a 204 and re-reads the whole administration', async () => {
      await arriveWithEmptyGroup();

      pressGroupCommand('remove');

      expect(query('.confirm-dialog')).withContext('the question is asked').not.toBeNull();
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim()).toBe(REMOVAL_MESSAGE);
      expect(query('.confirm-dialog__button--danger'))
        .withContext('marked destructive')
        .not.toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing sent yet').toHaveSize(0);

      pressGroupCommand('remove');
      pressDialogue(REMOVAL_CONFIRM_LABEL);

      const call = expectRequest('DELETE', roleGroupUrl(4), 'the removal');

      expect(call.request.url).toBe('/api/v1/role-groups/4');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // ⚠ THE FILTER MUST NOT BE LEFT NAMING A GROUP THAT NO LONGER EXISTS, so the removal resets it and
      // re-reads BOTH halves — the chained arrival read all over again.
      answerArrival([], []);

      expect(notifications()).toEqual([
        { severity: 'success', message: 'Role group "Paid Services" was deleted.' },
      ]);
    });

    it('sends nothing when the confirmation is dismissed', async () => {
      await arriveWithEmptyGroup();

      pressGroupCommand('remove');
      pressDialogue(GROUP_EDITOR_CANCEL_LABEL);

      expect(query('.confirm-dialog')).withContext('closed again').toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(notifications()).toHaveSize(0);
    });

    it('reports a group still in use at 409 and re-reads both halves', async () => {
      await arriveWithEmptyGroup();

      pressGroupCommand('remove');
      pressDialogue(REMOVAL_CONFIRM_LABEL);

      expectRequest('DELETE', roleGroupUrl(4)).flush(
        problem('role_group.in_use', 409, 'The group still contains roles.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // ⚠ THIS IS THE RACE THE CLIENT PRECONDITION CANNOT CLOSE: the listing said the group was empty,
      // and by the time the request landed it was not. So the refusal re-reads BOTH halves — the groups
      // and the roles, as two independent requests this time — to show what is actually there.
      expectRequest('GET', ROLE_GROUPS_URL, 'the group re-read').flush(envelope([roleGroup(4)]));
      expectRequest('GET', ROLES_URL, 'the role re-read').flush(pageOf([roleRow()]));
      fixture.detectChanges();

      // ⚠ THE PUBLISHED CONFLICT SENTENCE WINS OVER THE SERVER'S, because it says what to do next; and
      // the severity is an ERROR, since only 401, 403, 404 and 429 are warnings.
      expect(notifications()).toEqual([{ severity: 'error', message: GROUP_IN_USE_MESSAGE }]);
    });

    it('reports a refused removal as a warning at 403 without re-reading', async () => {
      await arriveWithEmptyGroup();

      pressGroupCommand('remove');
      pressDialogue(REMOVAL_CONFIRM_LABEL);

      expectRequest('DELETE', roleGroupUrl(4)).flush(
        problem('auth.not_permitted', 403, 'Not permitted.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // Only a conflict teaches the screen that its view is stale, so an access refusal re-reads nothing.
      expect(httpMock.match(() => true)).withContext('nothing is re-read').toHaveSize(0);
      expect(notifications()).toEqual([{ severity: 'warning', message: 'Not permitted.' }]);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — A REFUSED READ
  // ---------------------------------------------------------------------------------------------------

  describe('a refused read', () => {
    it('shows a failed group read inline with a way to retry it', () => {
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(
        problem('portal.tenant_unresolved', 403, 'No portal could be resolved.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(textOf('.error-banner__message').join(' ')).toContain('No portal could be resolved.');
      expect(button(RETRY_LABEL)).withContext('a way to retry').not.toBeUndefined();
      // A read failure is shown in full by the banner, so nothing transient is raised for the same event.
      expect(notifications()).withContext('nothing transient').toHaveSize(0);
    });

    it('retries the whole chained read on demand', () => {
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(
        problem('server.unexpected_failure', 500, 'Something went wrong.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      press(RETRY_LABEL);

      // The retry starts again from the groups, because the roles read depends on them.
      answerArrival([roleGroup()], [roleRow()]);

      expect(rows()).toHaveSize(1);
      expect(query('.error-banner__title')).withContext('the failure is cleared').toBeNull();
    });

    it('shows a failed role read inline as well', () => {
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();

      expectRequest('GET', ROLES_URL).flush(
        problem('role.paging_invalid', 400, 'The paging arguments are invalid.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(textOf('.error-banner__message').join(' ')).toContain(
        'The paging arguments are invalid.',
      );
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — OPERABILITY
  // ---------------------------------------------------------------------------------------------------

  describe('operability', () => {
    /**
     * ⚠ THE SHELL OWNS EVERY LANDMARK, AND A SECOND ONE IS WORSE THAN NONE. A screen reader offers a
     * landmark list as the primary way to move around a page, and two elements answering to the same
     * landmark role make that list ambiguous rather than richer. The roles are checked as well as the
     * elements, because `role="banner"` on a `<div>` is a landmark just as much as a `<header>` is, and
     * only the element form would be caught by a tag-name check.
     */
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      arrive();

      expect(queryAll('main, nav, header, footer, aside')).toHaveSize(0);
      expect(
        queryAll(
          '[role="banner"], [role="main"], [role="navigation"],'
            + ' [role="contentinfo"], [role="complementary"]',
        ),
      )
        .withContext('nor a landmark declared by role rather than by element')
        .toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('names the filter with a real label pointing at the picker', () => {
      arrive();

      const target: HTMLLabelElement | undefined = queryAll<HTMLLabelElement>('label[for]').find(
        (label) => label.getAttribute('for') === FILTER_CONTROL_ID,
      );

      expect(target).withContext('the picker carries a real label').not.toBeUndefined();
    });

    it('gives each icon-only group command a discernible name and hides its image', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();

      const commands: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>(
        'button.role-list__filter-action',
      );

      expect(commands.length).withContext('both commands are offered').toBe(2);
      commands.forEach((command) => {
        // ⚠ AN IMAGE IS NOT A NAME. The legacy affordance was an unlabelled image; each command here
        // carries real text, and its decorative image is hidden from assistive technology so it cannot
        // compete with that text.
        expect((command.textContent ?? '').trim())
          .withContext('carries discernible text')
          .not.toBe('');
        expect(command.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
        expect(command.getAttribute('type')).toBe('button');
      });
    });

    it('locks both group commands while a mutation is in flight', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();

      pressGroupCommand('remove');
      pressDialogue(REMOVAL_CONFIRM_LABEL);

      const call = expectRequest('DELETE', roleGroupUrl(4));

      queryAll<HTMLButtonElement>('button.role-list__filter-action').forEach((command) => {
        expect(command.disabled).withContext('locked in flight').toBeTrue();
      });

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerArrival([], []);
    });

    it('renders a hostile role name as text, with no element parsed out of it', () => {
      arrive(
        [roleGroup()],
        [roleRow(0, { roleName: '<img src=x onerror="window.__listed=true">' })],
      );

      expect(queryAll('img')).withContext('no element parsed out of a name').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__listed'])
        .withContext('never evaluated')
        .toBeUndefined();
      expect(host().innerHTML).toContain('&lt;img');
    });

    it('renders a stored frequency code outside the published six exactly as it is stored', () => {
      // MIGRATION: the listing used to be unopenable in a real migrated database whenever any role
      //   held one of these codes. The API carries a stored frequency character through losslessly
      //   and shipped DotNetNuke data seeds two roles with characters from the superseded numeric
      //   code set, but the read decoder was closed to the six published codes — so one legacy row
      //   refused the whole page and an administrator saw no roles at all.
      //
      // The grid binds the raw field, exactly as the legacy grid did, so the character renders as
      // itself and is never expanded into a word or mapped onto a supported code.
      arrive([roleGroup()], [roleRow(0, { billingFrequency: '4', trialFrequency: 'm' })]);

      const cells: readonly string[] = Array.from(
        (rows()[0] as HTMLTableRowElement).querySelectorAll('td,th'),
      ).map((cell) => (cell.textContent ?? '').trim());

      expect(rows()).withContext('the page is rendered rather than refused').toHaveSize(1);
      expect(cells).toContain('4');
      expect(cells)
        .withContext('case is data: a lower-case code is not folded onto the upper-case one')
        .toContain('m');
    });

    it('renders the absent-money and absent-period markers as empty cells', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: null, billingPeriod: null })]);

      const cells: readonly string[] = Array.from(
        (rows()[0] as HTMLTableRowElement).querySelectorAll('td,th'),
      ).map((cell) => (cell.textContent ?? '').trim());

      // Two absences on the wire and one rendering, because a fee of "nothing" is not a fee of zero and
      // must not be drawn as one.
      expect(cells.filter((text) => text.length === 0).length)
        .withContext('both absences render empty')
        .toBeGreaterThanOrEqual(2);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — THE THREE-WAY GROUPING FILTER, WITHOUT CONFLATION
  //
  // The legacy screen carried THREE distinct grouping concepts through ONE integer, and two of them
  // are negative. `Roles.ascx.vb` L48 initialises the field to -1, L72 branches on `< -1` — strictly,
  // so -1 does NOT take the all-roles path — and L112/L114 offer -2 and -1 as the two pseudo-entries.
  // Every case below pins one of the three apart from the other two.
  // ---------------------------------------------------------------------------------------------------

  describe('the three-way grouping filter', () => {
    /** The picker's options, as rendered: label and DOM value together, in document order. */
    function offeredOptions(): readonly { label: string; value: string }[] {
      return Array.from(field<HTMLSelectElement>(FILTER_CONTROL_ID).options).map((option) => ({
        label: (option.textContent ?? '').trim(),
        value: option.value,
      }));
    }

    /**
     * ⭐ THE DEFAULT NARROWING IS THE UNGROUPED SCOPE, NOT THE ALL-ROLES ONE.
     *
     * `Roles.ascx.vb` L48 is `Private RoleGroupId As Integer = -1`, and L72's `If RoleGroupId < -1`
     * is STRICTLY less-than, so the initial -1 falls to the ELSE arm and reads
     * `GetRolesByGroup(PortalId, -1)` — the roles belonging to no group. It does NOT read every role.
     * A default of -2, or a branch written `<= -1` or `< 0`, would list the whole tenant on arrival,
     * which looks like a working screen and is a different screen.
     */
    it('arrives on the ungrouped scope, which is the legacy default and not the all-roles one', () => {
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup(4)]));
      fixture.detectChanges();

      const arrival = expectRequest('GET', ROLES_URL, 'the arrival role read');

      expect(arrival.request.params.get('scope'))
        .withContext('the ungrouped scope, from the legacy default of -1')
        .toBe('Ungrouped');
      expect(arrival.request.params.get('scope'))
        .withContext('and emphatically not the all-roles scope')
        .not.toBe('All');
      expect(arrival.request.params.has('roleGroupId'))
        .withContext('a pseudo-entry is not a group identifier')
        .toBeFalse();

      arrival.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    /**
     * ⭐ THE CORRECTION FOR LEGACY DEFECT D-R3.
     *
     * `Roles.ascx.vb` L115 reads `If RoleGroupId < 0 Then liItem.Selected = True`, and that condition
     * is attached to the GLOBAL ROLES entry alone — so with the filter at -2 the legacy picker
     * selected "Global Roles" while the grid below it showed EVERY role. The picker lied about what
     * was on screen. Here the chosen option is derived from the actual narrowing, so the two agree.
     */
    it('shows the ungrouped pseudo-entry as the chosen one on arrival, not the all-roles one', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      const picker = field<HTMLSelectElement>(FILTER_CONTROL_ID);
      const chosen: readonly string[] = Array.from(picker.options)
        .filter((option) => option.selected)
        .map((option) => (option.textContent ?? '').trim());

      expect(chosen).toEqual([GLOBAL_ROLES_OPTION_LABEL]);
      expect(chosen)
        .withContext('the picker must not read "all" while the grid shows the ungrouped scope')
        .not.toContain(ALL_ROLES_OPTION_LABEL);
      expect(picker.value).toBe(GLOBAL_ROLES_VALUE);
    });

    /**
     * The legacy order is fixed by construction: L112 adds the all-roles entry, L114-118 adds the
     * global one, and only then does L120's loop append the groups. Every one is `.Items.Add`, never
     * `.Items.Insert`, so nothing is ever placed ahead of the two pseudo-entries.
     *
     * The labels carry their angle brackets AND the spaces inside them. `SharedResources.resx` stores
     * them escaped, as `&lt; All Roles &gt;` and `&lt; Global Roles &gt;`, which decodes to a bracket,
     * a space, the words, a space and a bracket. Trimming the interior would be a different label.
     */
    it('offers the two pseudo-entries in the legacy order, ahead of every group', () => {
      arrive([
        roleGroup(4, { roleGroupName: 'Paid Services' }),
        roleGroup(5, { roleGroupName: 'Trials' }),
      ]);

      expect(offeredOptions()).toEqual([
        { label: '< All Roles >', value: '-2' },
        { label: '< Global Roles >', value: '-1' },
        { label: 'Paid Services', value: '4' },
        { label: 'Trials', value: '5' },
      ]);
    });

    /**
     * ⭐ THE TWO PSEUDO-ENTRIES ARE NOT INTERCHANGEABLE, and this is the case that would fail if they
     * were ever merged. -2 asks for every role in the tenant; -1 asks for the roles that belong to no
     * group, which is a REAL PERSISTED VALUE of `Roles.RoleGroupID` and therefore a genuine data
     * predicate. `EditRoles.ascx.vb`'s own group picker offers -1 as a storable choice and never
     * mentions -2 at all — the one is a row value, the other is only ever a view.
     */
    it('keeps the two pseudo-entries distinct, because only one of them is a stored row value', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter(ALL_ROLES_OPTION_LABEL);

      const all = expectRequest('GET', ROLES_URL, 'the all-roles read');
      const allScope: string | null = all.request.params.get('scope');

      all.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      await chooseFilter(GLOBAL_ROLES_OPTION_LABEL);

      const ungrouped = expectRequest('GET', ROLES_URL, 'the ungrouped read');
      const ungroupedScope: string | null = ungrouped.request.params.get('scope');

      expect(allScope).toBe('All');
      expect(ungroupedScope).toBe('Ungrouped');
      expect(ungroupedScope)
        .withContext('merging the two would list the whole tenant where one group was asked for')
        .not.toBe(allScope);
      // Neither pseudo-entry is ever expressed as a number on the wire, in either direction.
      expect(all.request.params.has('roleGroupId')).toBeFalse();
      expect(ungrouped.request.params.has('roleGroupId')).toBeFalse();

      ungrouped.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    /**
     * ⭐ GROUP ZERO IS A GROUP. Nothing in this screen may test a grouping identifier for truthiness
     * or for a positive sign: `>= 0` is the whole of the "real group" test, so zero addresses the
     * group it names and travels as the digit it is. A `if (roleGroupId)` guard anywhere on this path
     * would silently redirect group zero to the ungrouped scope.
     */
    it('treats group zero as a real group rather than as an absence', async () => {
      arrive([roleGroup(0, { roleGroupName: 'Seeded Group' })]);

      await chooseFilter('Seeded Group');

      const grouped = expectRequest('GET', ROLES_URL, 'the group-zero read');

      expect(grouped.request.params.get('roleGroupId')).toBe('0');
      expect(grouped.request.params.has('scope'))
        .withContext('a real identifier replaces the named scope rather than accompanying it')
        .toBeFalse();

      grouped.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    /**
     * With no group in the tenant there is nothing to narrow BY, so the legacy screen forced the
     * filter to -2 and hid the whole row (`Roles.ascx.vb` L129-L130). Keeping the ungrouped default
     * there would show only the ungrouped roles behind a control the operator cannot see or change.
     */
    it('drops to the all-roles scope and hides the row when the tenant has no group at all', () => {
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([]));
      fixture.detectChanges();

      const arrival = expectRequest('GET', ROLES_URL, 'the arrival role read');

      expect(arrival.request.params.get('scope')).toBe('All');

      arrival.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      expect(query(`#${FILTER_CONTROL_ID}`)).withContext('no picker').toBeNull();
      expect(queryAll('select')).withContext('and no picker under another name').toHaveSize(0);
    });
  });


  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — THE GRID'S TEN DATA COLUMNS
  //
  // `roles.ascx` L34-L77 declares TWELVE columns: two icon commands and ten data columns. Each case
  // below pins one rendering rule taken from the legacy screen, and the sentinel cases are the ones
  // that matter most — the legacy code distinguishes "zero" from "absent" and a naive port does not.
  // ---------------------------------------------------------------------------------------------------

  describe('the grid data columns', () => {
    /** Every heading cell of the grid, in column order. */
    function headerCells(): readonly HTMLTableCellElement[] {
      return queryAll<HTMLTableCellElement>('th.data-table__header');
    }

    /** The headings an operator can READ, which excludes the two clipped command headings. */
    function visibleHeadings(): readonly string[] {
      return headerCells()
        .filter((cell) => cell.querySelector('.data-table__label--hidden') === null)
        .map((cell) => (cell.textContent ?? '').trim());
    }

    /** Every body cell of the single painted row, in column order. */
    function cellsOfFirstRow(): readonly string[] {
      const row: HTMLTableRowElement = queryOrFail<HTMLTableRowElement>(
        host(),
        'tr.data-table__row',
      );

      return Array.from(row.querySelectorAll('td,th')).map((cell) => (cell.textContent ?? '').trim());
    }

    /**
     * The body cell sitting beneath a named heading.
     *
     * Addressed by heading rather than by ordinal, because an ordinal silently follows a column
     * reordering while a heading name does not — and because a case that names the column it means
     * reads as the sentence it is proving.
     */
    function cellUnder(heading: string): string {
      return cellsOfFirstRow()[columnIndexOf(heading)] ?? '';
    }

    /**
     * What a cell of the FIRST row actually PAINTS, with clipped content excluded.
     *
     * Two cell kinds on this screen now carry content that is deliberately in the accessibility tree and
     * deliberately not on the screen: the absent-value mark's own words, and the expansion of a stored
     * frequency character. `textContent` returns both, so a case asserting on the painted appearance has
     * to exclude them explicitly — otherwise a rule that changed the VISIBLE output would still pass
     * because the hidden words made up the difference.
     *
     * @param heading The column heading to read under.
     * @returns The trimmed painted text, with every clipped span removed.
     */
    function paintedCellUnder(heading: string): string {
      const cell: HTMLTableCellElement | undefined = Array.from(
        rows()[0]?.querySelectorAll<HTMLTableCellElement>('td,th') ?? [],
      )[columnIndexOf(heading)];

      if (cell === undefined) {
        return '';
      }

      const copy: HTMLTableCellElement = cell.cloneNode(true) as HTMLTableCellElement;

      copy.querySelectorAll('.role-list__absent-value').forEach((clipped) => {
        clipped.remove();
      });

      return (copy.textContent ?? '').trim();
    }

    /**
     * The clipped expansion inside a cell of the first row, or the empty string when there is none.
     *
     * @param heading The column heading to read under.
     * @returns The trimmed clipped text.
     */
    function clippedCellUnder(heading: string): string {
      const cell: HTMLTableCellElement | undefined = Array.from(
        rows()[0]?.querySelectorAll<HTMLTableCellElement>('td,th') ?? [],
      )[columnIndexOf(heading)];

      return (cell?.querySelector('.role-list__absent-value')?.textContent ?? '').trim();
    }

    /** The ordinal of the column carrying a named heading, asserted to exist. */
    function columnIndexOf(heading: string): number {
      const index: number = headerCells().findIndex(
        (cell) => (cell.textContent ?? '').trim() === heading,
      );

      expect(index).withContext(`a column headed "${heading}" is rendered`).toBeGreaterThan(-1);

      return index;
    }

    /** One named column read down every painted row, in row order. */
    function columnUnder(heading: string): readonly string[] {
      const index: number = columnIndexOf(heading);

      return rows().map((row) => {
        const cell: HTMLTableCellElement | undefined =
          Array.from(row.querySelectorAll<HTMLTableCellElement>('td,th'))[index];

        return (cell?.textContent ?? '').trim();
      });
    }

    /**
     * ⭐ EIGHT LEGACY HEADER KEYS FOR TEN COLUMNS, DISAMBIGUATED.
     *
     * `Roles.ascx.resx` supplies exactly eight `.Header` entries — Name, Description, Fee, Every,
     * Period, Trial, Public, Auto — because DotNetNuke localised a grid heading by its `HeaderText`
     * VALUE rather than by the column, so `Every.Header` and `Period.Header` each served two columns
     * and the rendered grid showed "Every" and "Period" twice with nothing to tell the pairs apart.
     * A screen reader moving across a row announced two different cells under the same column name.
     *
     * The four are qualified here, and the set assertion is what keeps them qualified: if any future
     * edit reintroduced a duplicate the sizes would diverge.
     */
    it('names all ten data columns distinctly, disambiguating the two pairs the legacy left colliding', () => {
      arrive();

      const headings: readonly string[] = visibleHeadings();

      expect(headings).toEqual([
        'Name',
        'Description',
        'Fee',
        'Billing Every',
        'Billing Period',
        'Trial',
        'Trial Every',
        'Trial Period',
        'Public',
        'Auto',
      ]);
      expect(new Set(headings).size)
        .withContext('no two column headings share a label')
        .toBe(headings.length);
    });

    /**
     * The two `ImageCommandColumn`s carry no `HeaderText` at all, so DotNetNuke's own localiser skipped
     * them — `If Not String.IsNullOrEmpty(col.HeaderText)` is the guard — and the legacy grid rendered
     * two blank headings. Here each command column keeps a real accessible name and merely clips it
     * visually: blank to the eye, named to a reader, which is strictly better than the legacy blank
     * and still emits no visible heading the legacy did not have.
     */
    it('clips the heading of each command column rather than publishing one', () => {
      arrive();

      const clipped: readonly string[] = headerCells()
        .filter((cell) => cell.querySelector('.data-table__label--hidden') !== null)
        .map((cell) => (cell.textContent ?? '').trim());

      // THREE commands now, not two: the removal command was added to this listing, and the reasoning
      // for it — parity as a floor rather than a ceiling, plus the three sibling listings that all carry
      // one — is recorded on the view query in the component. It is headed and clipped exactly like the
      // two it joins, because it must read as the same kind of column.
      expect(headerCells()).withContext('three commands plus ten data columns').toHaveSize(13);
      expect(clipped).toEqual([EDIT_LABEL, MANAGE_USERS_LABEL, 'Delete']);
    });

    /**
     * ⭐⭐ ZERO IS A PRICE. `RoleController.vb` L494 discriminates a paid assignment from a free one
     * with `userRole.ServiceFee > 0.0`, so a fee of zero is a role that is deliberately FREE — a real,
     * stored, meaningful value — and `FormatPrice` renders it, because its only guard is
     * `If price <> Null.NullSingle`. Rendering it blank would be indistinguishable from a role whose
     * terms were never set, and rendering it as the word "Free" would invent wording the legacy screen
     * never showed.
     */
    it('renders a fee of zero as zero money in both fee columns', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: 0, trialFee: 0 })]);

      expect(cellUnder('Fee')).toBe('0.00');
      expect(cellUnder('Trial')).toBe('0.00');
    });

    /**
     * ⭐⭐ ZERO IS A PERIOD, for the same reason and by the same guard: `FormatPeriod`'s only test is
     * `If period <> Null.NullInteger`, so only -1 is withheld and zero prints.
     */
    it('renders a period of zero as zero in both period columns', () => {
      arrive([roleGroup()], [roleRow(0, { billingPeriod: 0, trialPeriod: 0 })]);

      expect(cellUnder('Billing Every')).toBe('0');
      expect(cellUnder('Trial Every')).toBe('0');
    });

    /**
     * ⭐⭐ THE TWO SENTINELS, AND ONLY THE TWO SENTINELS, RENDER BLANK.
     *
     * `Null.vb` spells an absent single as `Single.MinValue` and an absent integer as -1, and those
     * are the exact values the legacy formatters withhold. The single sentinel is written here to the
     * full IEEE-754 value of `Single.MinValue` rather than to a rounded stand-in, because a rounded
     * literal is a different number and would prove nothing about the boundary.
     */
    it('withholds the two sentinels, and marks the period cells as not recorded', () => {
      arrive(
        [roleGroup()],
        [
          roleRow(0, {
            serviceFee: -3.4028234663852886e38,
            trialFee: -3.4028234663852886e38,
            billingPeriod: -1,
            trialPeriod: -1,
          }),
        ],
      );

      // ⚠ THE TWO FEE COLUMNS CARRY THE MARK TOO, AND THE ASYMMETRY THAT PRECEDED IT WAS FOUND BY A
      // BROWSER PASS RATHER THAN BY READING. The mark reached the period columns first; a run over the
      // whole 145-role dataset then found that on the one role whose fees are stored NULL the money cells
      // rendered as empty strings while the count cells beside them on the SAME ROW carried the mark and
      // its words. A reader heard "not recorded" for the counts and silence for the fees, for one
      // indistinguishable state.
      expect(paintedCellUnder('Fee')).toBe('\u2014');
      expect(paintedCellUnder('Trial')).toBe('\u2014');
      expect(clippedCellUnder('Fee')).toBe('not recorded');
      expect(clippedCellUnder('Trial')).toBe('not recorded');

      // ⚠ R-M1: THE TWO PERIOD COLUMNS NOW MARK THE ABSENCE. An empty cell could not distinguish a
      // period nobody recorded from one nobody had looked at, and the same grid rendered a stored `-3`
      // as "-3" — two negative periods presented as different kinds of value. The VALUE is unchanged
      // and still withheld; the cell says so, with the mark painted and the words clipped.
      expect(paintedCellUnder('Billing Every')).toBe('\u2014');
      expect(paintedCellUnder('Trial Every')).toBe('\u2014');
      expect(clippedCellUnder('Billing Every'))
        .withContext('a reader who cannot see the dash is told what it means')
        .toBe('not recorded');
      expect(clippedCellUnder('Trial Every')).toBe('not recorded');
    });

    /**
     * ⚠ ZERO IS A PRICE AND MUST NEVER REACH THE ABSENT BRANCH.
     *
     * `RoleController.vb:L494` discriminates a paid assignment from a free one with
     * `userRole.ServiceFee > 0.0`, so nought is a role that is deliberately FREE — a real, stored,
     * meaningful value. Marking it "not recorded" would be a lie about the data, and this case is what
     * stops the absent-value branch from widening into it.
     */
    it('renders a zero fee and a zero period as themselves, never as absent', () => {
      arrive(
        [roleGroup()],
        [roleRow(0, { serviceFee: 0, trialFee: 0, billingPeriod: 0, trialPeriod: 0 })],
      );

      expect(paintedCellUnder('Fee')).toBe('0.00');
      expect(paintedCellUnder('Trial')).toBe('0.00');
      expect(paintedCellUnder('Billing Every')).toBe('0');
      expect(paintedCellUnder('Trial Every')).toBe('0');
      expect(clippedCellUnder('Fee')).toBe('');
      expect(clippedCellUnder('Billing Every')).toBe('');
    });

    /**
     * ⚠ R-M1, THE OTHER HALF: A NEGATIVE PERIOD THAT IS NOT THE SENTINEL STILL RENDERS ITSELF.
     *
     * `Roles.ascx.vb:L152-L162` guards on `period <> Null.NullInteger` and nothing else, so a stored
     * `-3` is data and is displayed. That behaviour is unchanged — what changed is that the sentinel
     * case beside it is no longer indistinguishable from it.
     */
    it('renders a negative period that is not the sentinel as itself', () => {
      arrive([roleGroup()], [roleRow(0, { billingPeriod: -3, trialPeriod: -3 })]);

      expect(paintedCellUnder('Billing Every')).toBe('-3');
      expect(clippedCellUnder('Billing Every'))
        .withContext('a real value carries no absence words')
        .toBe('');
    });

    /**
     * ⚠ R-M4: AN AMOUNT THE CELL CANNOT STATE EXACTLY SAYS SO, IN WORDS, AND STILL PAINTS ITSELF.
     *
     * The column is SQL `money` — exact decimal at the full width of a 64-bit integer — and the wire
     * carries a JSON number, which is read as an IEEE-754 double. Above `MAX_SAFE_INTEGER / 100` a
     * double cannot hold an amount to the nearest hundredth, so the figure that arrives has already
     * moved from the figure that is stored: runtime measurement found `922337203685477.5807` stored
     * against `922337203685477.63` painted, in the same colour and weight as an exact amount, on the
     * screen an administrator uses to review what a role costs.
     *
     * The rounding happens when the wire is parsed and cannot be undone here, so the cell discloses
     * rather than corrects. This case pins BOTH halves: the figure is still painted (an operator needs
     * the magnitude) and the qualifier is present in the accessibility tree.
     */
    it('marks a fee too large for a double to state exactly as approximate', () => {
      arrive(
        [roleGroup()],
        [
          roleRow(0, {
            serviceFee: 922_337_203_685_477.5807,
            trialFee: 922_337_203_685_477.5807,
          }),
        ],
      );

      expect(paintedCellUnder('Fee'))
        .withContext('the magnitude is still painted; only its exactness is qualified')
        .toBe('922337203685477.63');
      expect(clippedCellUnder('Fee')).toBe('approximate');
      expect(clippedCellUnder('Trial')).toBe('approximate');
    });

    /**
     * ⚠ THE R-M4 QUALIFIER MUST NOT WIDEN INTO ORDINARY MONEY.
     *
     * Every amount an operator will ever type is exact in a double, so a qualifier appearing on one
     * would be noise on every row of every page — and would train a reader to ignore it on the one
     * row where it matters. The threshold is `MAX_SAFE_INTEGER / 100`; this case sits an amount just
     * BELOW it and one far below it, and asserts silence for both.
     */
    it('leaves an exactly representable fee unqualified, however large', () => {
      arrive(
        [roleGroup()],
        [roleRow(0, { serviceFee: 90_071_992_547_409.8, trialFee: 9.99, billingPeriod: 1 })],
      );

      expect(clippedCellUnder('Fee'))
        .withContext('just below the bound: exact to the cent, so nothing is claimed')
        .toBe('');
      expect(paintedCellUnder('Fee')).toBe('90071992547409.80');
      expect(clippedCellUnder('Trial')).toBe('');
    });

    /**
     * ⚠ NO THOUSANDS SEPARATOR ON THIS SCREEN. The listing formats with `"##0.00"`
     * (`Roles.ascx.vb` L179) and the role EDITOR formats the same three amounts with `"#,##0.00"`
     * (`EditRoles.ascx.vb`). That inconsistency is measured, not inferred, and this case pins the
     * LIST side of it: separating the thousands here would be a visible change to a screen whose
     * appearance is meant to carry over.
     */
    it('renders a four-figure fee without a thousands separator', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: 1234.5, trialFee: 1000 })]);

      expect(cellUnder('Fee')).toBe('1234.50');
      expect(cellUnder('Fee')).not.toContain(',');
      expect(cellUnder('Trial')).toBe('1000.00');
      expect(cellUnder('Trial')).not.toContain(',');
    });

    /**
     * ⭐ ALL SIX PUBLISHED FREQUENCY CODES, VERBATIM, IN BOTH COLUMNS.
     *
     * `RoleController.vb` L540-L547 switches on six characters — N, O, D, W, M and Y — with no
     * `Case Else`, and `Roles.BillingFrequency` is `char(1)`, so the character IS the stored datum.
     * `roles.ascx` L50-L52 and L63-L65 bind the raw `DataField`, which is why the legacy grid showed
     * the letter rather than a word: the `CodeFrequency` lookup table that maps N to "None" and O to
     * "One-time Fee" exists in the schema and this grid does not join to it.
     *
     * Two failure modes are pinned out by rendering the character itself. An enum serialised by name
     * would arrive as "None" or "OneTimeFee"; an enum serialised by ordinal would arrive as a digit.
     * Neither is what the legacy screen displayed, and neither is what the contract carries — the
     * model declares `'N' | 'O' | 'D' | 'W' | 'M' | 'Y'` and the cell prints it unchanged.
     */
    it('renders each of the six published frequency codes as the single character it is', () => {
      const codes: readonly BillingFrequency[] = ['N', 'O', 'D', 'W', 'M', 'Y'];

      // One row per code, so all six are proved against ONE rendering of the grid rather than against
      // six successive reads — the assertion is about the cell, not about the fetch.
      arrive(
        [roleGroup()],
        codes.map((code, index) =>
          roleRow(index, { billingFrequency: code, trialFrequency: code }),
        ),
      );

      expect(rows()).toHaveSize(codes.length);

      // ⚠ THE PAINTED CHARACTER IS UNCHANGED, which is what this case has always been about. The
      // expansion added for R-M3 is CLIPPED, so it must not appear in the painted reading — and the
      // painted reading is taken with clipped content excluded precisely so that a rule which started
      // painting the word would fail here rather than pass on the strength of the hidden text.
      const painted = (heading: string): readonly string[] =>
        rows().map((row, index) => {
          const cell: HTMLTableCellElement | undefined = Array.from(
            row.querySelectorAll<HTMLTableCellElement>('td,th'),
          )[columnIndexOf(heading)];
          const copy = cell?.cloneNode(true) as HTMLElement | undefined;

          copy?.querySelectorAll('.role-list__absent-value').forEach((clipped) => {
            clipped.remove();
          });

          expect(copy).withContext(`row ${index} has a cell under "${heading}"`).not.toBeUndefined();

          return (copy?.textContent ?? '').trim();
        });

      expect(painted('Billing Period')).toEqual(['N', 'O', 'D', 'W', 'M', 'Y']);
      expect(painted('Trial Period')).toEqual(['N', 'O', 'D', 'W', 'M', 'Y']);
    });

    /**
     * ⚠ R-M3: WHAT THE CHARACTER MEANS REACHES A READER, WITHOUT REACHING THE SCREEN.
     *
     * The legacy grid bound the raw field rather than the joined description (`roles.ascx:L50-L52`), so
     * a reader met an unexplained letter while the role editor two clicks away rendered the same datum
     * as "Month". The expansion closes that gap at zero visual cost, and it takes its words from the one
     * shared vocabulary the editor's own select captions are built from — so the two screens cannot
     * drift apart about what `M` is called.
     */
    it('clips an expansion of each frequency character for a reader', () => {
      arrive([roleGroup()], [roleRow(0, { billingFrequency: 'M', trialFrequency: 'Y' })]);

      expect(clippedCellUnder('Billing Period')).toBe('Month');
      expect(clippedCellUnder('Trial Period')).toBe('Year');
    });

    /**
     * A character outside the closed vocabulary contributes NOTHING rather than a guess. The column is
     * constrained by `FK_Roles_CodeFrequency`, so anything else is data this application cannot
     * interpret, and narrating it would be inventing a meaning.
     */
    it('says nothing about a frequency character it cannot interpret', () => {
      arrive([roleGroup()], [roleRow(0, { billingFrequency: 'Q' })]);

      expect(paintedCellUnder('Billing Period')).toBe('Q');
      expect(clippedCellUnder('Billing Period')).toBe('');
    });

    /**
     * ⭐ THE TWO FLAGS ARE BOOLEANS, AND ONE CELL EACH.
     *
     * The legacy grid drew two `<asp:image>` elements per flag and chose between them with
     * `DataBinder.Eval(..., "IsPublic")="true"` — a STRING comparison that only ever worked because
     * `release.config` L125 compiles the pages with `strict="false"`, letting VB coerce the boolean to
     * a string before comparing. Ported literally into TypeScript, comparing the boolean member to the
     * lower-cased word as a string is not merely fragile — it is statically ALWAYS FALSE, since the two
     * operand types cannot overlap, and every row would have rendered as unchecked.
     *
     * So the flag is read as the boolean it is and announced as a word. One cell, no images: an icon
     * pair conveys nothing to a screen reader, and the wording is the shared resource's own.
     */
    it('renders both flags as announced words, one cell each', () => {
      arrive([roleGroup()], [roleRow(0, { isPublic: true, autoAssignment: true })]);

      expect(cellUnder('Public')).toBe('Yes');
      expect(cellUnder('Auto')).toBe('Yes');
      expect(queryAll('td img')).withContext('no checked/unchecked image pair').toHaveSize(0);
    });

    /**
     * ⚠ FALSE IS DATA, NOT ABSENCE — and the legacy code is the reason this needs saying. `Null.vb`
     * spells an absent boolean `Return False`, and the companion `IsNull` therefore answers True for a
     * perfectly ordinary stored `False`. The two are indistinguishable through that helper, so a port
     * that treated the sentinel as missing would blank every non-public role's cell.
     *
     * A role that is not public is a role whose visibility is KNOWN. It renders the negative word.
     */
    it('renders a false flag as the negative word rather than as an empty cell', () => {
      arrive([roleGroup()], [roleRow(0, { isPublic: false, autoAssignment: false })]);

      expect(cellUnder('Public')).toBe('No');
      expect(cellUnder('Auto')).toBe('No');
      expect(cellUnder('Public')).not.toBe('');
      expect(cellUnder('Auto')).not.toBe('');
    });

    /**
     * ⭐⭐ ROLE ZERO IS A ROLE. `dbo.Roles.RoleID` is declared `IDENTITY(0,1)`, so the first role ever
     * written to an installation — the Administrators role — has the identifier zero. Any truthiness
     * test on it would drop the row, or paint it and leave both its commands addressing nothing.
     */
    it('paints role zero and addresses both of its commands by that identifier', () => {
      arrive([roleGroup()], [roleRow(0, { roleName: 'Administrators' })]);

      const row: HTMLTableRowElement = queryOrFail<HTMLTableRowElement>(
        host(),
        'tr.data-table__row',
      );
      const commands: readonly HTMLAnchorElement[] = Array.from(
        row.querySelectorAll<HTMLAnchorElement>('a.role-list__row-action'),
      );

      expect(cellUnder('Name')).toBe('Administrators');
      expect(commands.map((link) => link.getAttribute('href'))).toEqual([
        '/roles/0',
        '/roles/0/users',
      ]);
    });
  });


  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — STRUCTURAL PARITY WITH THE LEGACY GRID
  //
  // What the legacy screen did NOT have is as much a part of the specification as what it did, because
  // an addition here is an unrequested change of behaviour. Each absence below was measured in
  // `roles.ascx` rather than assumed.
  // ---------------------------------------------------------------------------------------------------

  describe('structural parity', () => {
    /**
     * ⚠ THE PAGER IS AN ADDITION, AND THIS CASE RECORDS IT AS ONE RATHER THAN DENYING IT.
     *
     * `roles.ascx` declares no `AllowPaging`, no `PagerStyle` visibility and no paging control of any
     * kind; `Roles.ascx.vb` binds a plain `ArrayList` straight onto the grid at L77 and L91 and mentions
     * neither a page index, a page size nor a total. This case previously asserted that ABSENCE, and the
     * reconciliation it described was that the screen asked for "one page wide enough to hold the
     * tenant's roles" — which is not what the store did: it walked page after page and joined them,
     * because no single request can be wide enough.
     *
     * Runtime testing measured what that cost on a hundred and forty-five roles: an eight-thousand-pixel
     * document at 320 units wide with no affordance but scrolling, three requests per arrival, the whole
     * walk re-issued on every Back and after every write. The pager replaced the walk. A tenant of the
     * size the legacy product shipped — six stock roles — still fits one page and is still offered no
     * page-to-page affordance, which is what this case now pins: the SUMMARY is present because the
     * pager owns its own shape, and the STEPS are not.
     */
    it('draws the pager beneath the grid, and offers no steps when everything fits one page', () => {
      arrive([roleGroup()], [roleRow(0), roleRow(1, { roleName: 'Registered Users' })]);

      const pager: Element | null = query('app-pagination');

      expect(pager).withContext('mounted unconditionally, so the range summary always shows').not.toBeNull();
      // Two roles at ten a page is one page. The pager renders its summary and withholds the steps, so
      // a legacy-sized tenant sees no affordance the legacy screen lacked.
      expect(pager?.querySelectorAll('button') ?? [])
        .withContext('no page-to-page steps for a single page')
        .toHaveSize(0);
    });

    /** The legacy screen filtered by role GROUP alone. There was no text box and no free-text search. */
    it('offers no free-text filter, because the legacy screen had none', () => {
      arrive();

      expect(query('app-search-input')).toBeNull();
      expect(queryAll('input[type="search"]')).toHaveSize(0);
    });

    /**
     * ⚠ THREE ROW COMMANDS: THE TWO THE LEGACY DECLARED, PLUS A REMOVAL THAT IS AN ADDITION.
     *
     * `roles.ascx` L34-L35 declares exactly two `dnn:imagecommandcolumn`s — `Edit` and `UserRoles` —
     * and the string `commandname="Delete"` appears nowhere in the file. The `cmdDelete` control on the
     * same page is the ROLE-GROUP removal button (`Roles.ascx.vb:L81-L86`), gated on the selected group
     * holding no roles; it is not a row command and never was. This case previously asserted that the
     * listing offered no removal, on the reasoning that adding one would be "a new destructive
     * affordance".
     *
     * It is one, and it is added deliberately: functional parity is a floor rather than a ceiling, the
     * three sibling listings in this application each carry a row-level removal reached through the same
     * shared confirmation, and roles was the one listing where the workflow existed but could only be
     * found by opening a role first. The mis-press hazard is answered by construction — the command
     * opens the shared destructive dialog, which names what will be removed — rather than by leaving the
     * workflow hidden.
     *
     * The two navigation commands stay ANCHORS and the removal is a BUTTON, because one addresses a
     * screen and the other changes state, and the removal is placed LAST so it is never the command a
     * pointer meets first.
     */
    it('offers three commands per row: two links, then the removal button', () => {
      arrive([roleGroup()], [roleRow(0), roleRow(1, { roleName: 'Registered Users' })]);

      for (const row of rows()) {
        const links: readonly HTMLAnchorElement[] = Array.from(
          row.querySelectorAll<HTMLAnchorElement>('a.role-list__row-action'),
        );
        const buttons: readonly HTMLButtonElement[] = Array.from(
          row.querySelectorAll<HTMLButtonElement>('button.role-list__row-action'),
        );

        expect(links).withContext('edit and manage-users remain links').toHaveSize(2);
        expect(links.map((link) => (link.textContent ?? '').trim())).toEqual([
          EDIT_LABEL,
          MANAGE_USERS_LABEL,
        ]);
        expect(buttons).withContext('exactly one destructive command, and it is a button').toHaveSize(1);
        expect(buttons[0]?.type).toBe('button');
        expect(buttons[0]?.classList).toContain('role-list__row-action--danger');
      }
    });

    /**
     * The table carries a caption and NO `summary` attribute.
     *
     * MIGRATION: the caption is a net addition — `default.css` styles no caption anywhere in its 1030
     * lines, because the legacy grid had none. The `summary` attribute the legacy grid DID carry is
     * deliberately not reproduced: `roles.ascx` L24 sets it to "Roles Design Table", which is a Visual
     * Studio design-surface artefact rather than a description of anything an operator reads, and the
     * attribute is obsolete in HTML5 besides. The accessible name is the caption's job.
     */
    it('names the table with a caption and emits no design-surface summary', () => {
      arrive();

      const caption: HTMLTableCaptionElement = queryOrFail<HTMLTableCaptionElement>(
        host(),
        'caption',
      );

      expect((caption.textContent ?? '').trim()).toBe(PAGE_TITLE);
      expect(queryAll('table[summary]')).withContext('no summary attribute').toHaveSize(0);
      expect(queryAll('[summary]')).withContext('nor anywhere else').toHaveSize(0);
    });

    /**
     * The three page-level actions are the legacy module's own three, in its own order:
     * `Roles.ascx.vb` L307 adds `AddContent.Action`, L308 `AddGroup.Action` and L309
     * `UserSettings.Action`. Their wording comes from `Roles.ascx.resx` unchanged.
     */
    it('projects the legacy module actions, addressed and worded as the legacy declared them', () => {
      arrive();

      const actions: readonly HTMLAnchorElement[] = queryAll<HTMLAnchorElement>(
        'app-page-header a.role-list__page-action',
      );

      expect(actions.map((link) => (link.textContent ?? '').trim())).toEqual([
        ADD_ROLE_LABEL,
        ADD_ROLE_GROUP_LABEL,
        MEMBERSHIP_SETTINGS_LABEL,
      ]);
      expect(actions.map((link) => link.getAttribute('href'))).toEqual([
        '/roles/new',
        '/role-groups/new',
        '/settings/membership',
      ]);
      expect((queryOrFail<HTMLElement>(host(), 'h1').textContent ?? '').trim()).toBe(PAGE_TITLE);
    });

    /**
     * ⚠ ONPUSH IS NOT OBSERVABLE FROM OUTSIDE THIS COMPONENT, so it is read from the compiled
     * definition. See {@link declaresOnPush} for why every behavioural alternative is unavailable
     * here: the component declares no inputs to move, and signal reads mark a consumer dirty under
     * either strategy. The declaration is still worth pinning — the non-functional requirements make
     * it mandatory on every component, and nothing else in this suite would notice its removal.
     */
    it('declares OnPush change detection', () => {
      expect(declaresOnPush(RoleListComponent)).toBeTrue();
    });

    /**
     * The screen renders projections it cannot write to. Both slices reach the template from the store
     * as read-only signals, so a component-side mutation is not merely discouraged, it is unavailable:
     * neither mutator is present on the value at all.
     */
    it('renders read-only projections of the store state, never writable ones', () => {
      arrive();

      const store = TestBed.inject(RoleStore);

      expect(withholdsMutators(store.roleItems))
        .withContext('the row set is read-only')
        .toBeTrue();
      expect(withholdsMutators(store.roleGroups))
        .withContext('the group set is read-only')
        .toBeTrue();
      expect(withholdsMutators(store.rolesLoading))
        .withContext('the wait flag is read-only')
        .toBeTrue();
    });

    /**
     * ⚠ THE ROW SET IS REPLACED, NEVER MUTATED IN PLACE. The shared table tracks its rows by OBJECT
     * REFERENCE, so an array mutated in place would leave every previously rendered row identical to
     * the framework and the grid would keep painting the old rows after a narrowing changed. Replacing
     * the array is what makes the re-render happen.
     */
    it('replaces the row set on a re-read rather than mutating it', async () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], [roleRow(0)]);

      const store = TestBed.inject(RoleStore);
      const before: readonly RoleListItem[] = store.roleItems();

      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow(1, { roleName: 'Subscribers' })]));
      fixture.detectChanges();

      const after: readonly RoleListItem[] = store.roleItems();

      expect(after).not.toBe(before);
      expect(before.length).withContext('the earlier array is left untouched').toBe(1);
      expect(before[0]?.roleName).toBe('Administrators');
      expect(after[0]?.roleName).toBe('Subscribers');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 9 — WORDING, SEVERITY AND SAFETY
  // ---------------------------------------------------------------------------------------------------

  describe('wording and severity', () => {
    /**
     * Arrives with a real, EMPTY group chosen.
     *
     * Both preconditions this block needs at once: a real group is what makes the group commands
     * appear at all (neither pseudo-entry is a resource), and an empty listing is what makes the
     * removal command appear rather than only the editor.
     */
    async function arriveWithEmptyGroup(): Promise<void> {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();
    }

    /** The same arrival, named for the cases that care only that a real group is chosen. */
    async function arriveWithChosenGroup(): Promise<void> {
      await arriveWithEmptyGroup();
    }

    /**
     * ⚠ NO CLAIM OF PERMANENCE, ANYWHERE IN THE CONFIRMATION — and this is a correctness requirement,
     * not a tone preference. Removal in this domain is frequently NOT destruction: cancelling a paid
     * role assignment whose trial has already been used sets an expiry date of yesterday and UPDATES
     * the row, precisely so the trial-used fact survives (`RoleController.vb` L494-L497). Wording that
     * told an operator the action could not be undone would be false about the system it describes.
     */
    it('makes no claim of permanence in the removal confirmation', async () => {
      await arriveWithEmptyGroup();

      pressGroupCommand('remove');

      const dialogue: HTMLElement = queryOrFail<HTMLElement>(host(), 'app-confirm-dialog');
      const wording: string = (dialogue.textContent ?? '').toLowerCase();
      const forbidden: readonly string[] = [
        'cannot be undone',
        'permanently',
        'irreversible',
        'this action is final',
        'you will not be able to recover',
      ];

      for (const claim of forbidden) {
        expect(wording).withContext(`the confirmation must not claim "${claim}"`).not.toContain(claim);
      }

      expect(dialogue.textContent ?? '').toContain(REMOVAL_MESSAGE);

      pressDialogue(GROUP_EDITOR_CANCEL_LABEL);
    });

    /**
     * ⚠ THE CONFIRMATION'S PRESENCE IS WHAT "OPEN" MEANS. It exposes no `open` input, so the guard in
     * the template is the whole of the gating: absent until a removal is pending, present once one is.
     */
    it('raises the confirmation only once a removal is pending', async () => {
      await arriveWithEmptyGroup();

      expect(query('app-confirm-dialog')).withContext('nothing pending yet').toBeNull();

      pressGroupCommand('remove');

      expect(query('app-confirm-dialog')).withContext('now pending').not.toBeNull();

      pressDialogue(GROUP_EDITOR_CANCEL_LABEL);

      expect(query('app-confirm-dialog')).withContext('abandoned again').toBeNull();
    });

    /**
     * ⚠ ESCAPE ABANDONS THE REMOVAL, AND NOTHING IS SENT.
     *
     * A synthetic event neither moves focus nor takes the user agent's own `<dialog>` cancel path, so
     * the keydown is dispatched on the dialogue itself and MUST bubble — the handler sits on the
     * component host, not on the button that happens to hold focus. The key is compared by `key`; a
     * `keyCode` comparison would be reading a property the platform has deprecated.
     */
    it('abandons the removal on Escape without sending anything', async () => {
      await arriveWithEmptyGroup();

      pressGroupCommand('remove');

      const dialogue: HTMLElement = queryOrFail<HTMLElement>(host(), 'app-confirm-dialog');

      dialogue.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      );
      fixture.detectChanges();

      expect(query('app-confirm-dialog')).withContext('the confirmation closes').toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing was removed').toHaveSize(0);
      expect(notifications()).withContext('and nothing was announced').toHaveSize(0);
    });

    /**
     * ⚠ A REFUSAL OF AUTHORITY IS A WARNING, NOT AN ERROR, AND ITS WORDING IS THE LEGACY SENTENCE.
     *
     * `AccessDenied.ascx.vb` raises its message at `ModuleMessageType.YellowWarning` on BOTH of its
     * branches — L43 for a supplied message and L45 for the resource default — and the legacy severity
     * vocabulary is genuinely three-valued rather than binary. Flattening a refusal into the error
     * severity would lose a distinction the original drew deliberately, and it is the distinction
     * between "you did something wrong" and "you are not the one who may do this".
     *
     * The three-way removal guard is proved elsewhere in this suite; what this case adds is that the
     * refusal an operator READS is the sentence the legacy screen showed, unchanged.
     */
    it('reports a refusal of authority at warning severity, in the legacy wording', async () => {
      const accessDenied =
        'Either you are not currently logged in, or you do not have access to this content.';

      await arriveWithEmptyGroup();

      pressGroupCommand('remove');
      pressDialogue(REMOVAL_CONFIRM_LABEL);

      expectRequest('DELETE', roleGroupUrl(4)).flush(
        problem('auth.not_permitted', 403, accessDenied),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'warning', message: accessDenied }]);
      expect(notifications()[0]?.severity)
        .withContext('a refusal of authority is never reported as an error')
        .not.toBe('error');
    });

    /**
     * ⚠ AN RFC 7807 FIELD KEY IS READ WITH BRACKETS, NEVER WITH A DOT. `errors` is an index signature
     * and `noPropertyAccessFromIndexSignature` is enabled workspace-wide, so `refusal.errors.RoleGroupName`
     * does not compile — in this file any more than in the component. The key is therefore held as a
     * named constant and indexed, which also documents that the casing is the SERVER'S: the key arrives
     * Pascal-cased from a .NET model-state document while the control is camel-cased, and the shared
     * reader matches the two case-insensitively rather than either side renaming the other.
     *
     * ⚠ MEASURED BEHAVIOUR, NOT THE ONE THAT MIGHT BE ASSUMED: the editor CLOSES the moment a
     * replacement is submitted, so by the time a refusal lands there is no longer a control to sit
     * beside and the refusal is delivered as an announcement. Asserting a message beside the field here
     * would be asserting a screen this component does not present.
     */
    it('delivers a refused replacement as an announcement, the editor having closed on submission', async () => {
      const refusedKey = 'RoleGroupName';
      const refusedDetail = 'One or more members were refused.';
      const refusal: ProblemDetails = problem('validation.failed', 400, refusedDetail, {
        [refusedKey]: ['A group name may be at most 50 characters.'],
      });
      const refusedMessages: readonly string[] = refusal.errors?.[refusedKey] ?? [];

      expect(refusedMessages)
        .withContext('the fixture names the refused member by its wire key')
        .toHaveSize(1);

      await arriveWithChosenGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, 'Premium Services');
      press(GROUP_EDITOR_SUBMIT_LABEL);

      expectRequest('PUT', roleGroupUrl(4)).flush(refusal, {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'error', message: refusedDetail }]);
      expect(query(`#${GROUP_NAME_CONTROL_ID}`))
        .withContext('the editor closed when the replacement was submitted')
        .toBeNull();
    });

    /**
     * ⚠ STORED WORDING IS TEXT, INCLUDING ITS MARKUP.
     *
     * The legacy resource values are not clean strings. This screen's own required-name message is
     * stored as `'<br>You Must Enter a Valid Name'` — a layout instruction sitting inside a sentence,
     * which was meaningful only because Web Forms wrote the value straight into the page — and a
     * sibling resource stores `'<br> Invalid effective date'`, break THEN space. One resource value in
     * this migration's reference set carries a live `<script>` element with a REMOTE `src`, so treating
     * stored wording as markup is not a theoretical hazard.
     *
     * The break is stripped so the sentence reads as a sentence; nothing else is interpreted. Both
     * halves are asserted, because either alone would pass while the other failed: the words can be
     * right while a `<br>` element was parsed out of them, and no element can be present while the
     * characters `<br>` are still visible in the sentence.
     */
    it('renders stored wording as text, with its layout markup stripped rather than honoured', async () => {
      await arriveWithChosenGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, '');
      press(GROUP_EDITOR_SUBMIT_LABEL);

      const messages: readonly string[] = textOf('.form-field__error');

      expect(messages).toEqual([GROUP_NAME_REQUIRED_MESSAGE]);
      expect(messages[0])
        .withContext('the break left no leading whitespace behind either')
        .toBe('You Must Enter a Valid Name');
      expect(messages.join(' '))
        .withContext('the break is gone from the words')
        .not.toContain('<br>');
      expect(query('.form-field__errors br'))
        .withContext('and never became an element')
        .toBeNull();
      expect(httpMock.match(() => true)).withContext('and nothing was sent').toHaveSize(0);
    });

    /**
     * A hostile value in a GROUP name gets the same treatment a hostile ROLE name does, checked here
     * because the group name reaches the DOM through a different path — an editor control and an
     * announcement rather than a grid cell.
     */
    it('renders a hostile group name as characters in both the control and the announcement', async () => {
      const hostile = '<b>x</b>';

      arrive([roleGroup(4, { roleGroupName: hostile })], []);
      await chooseFilter(hostile);
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();

      pressGroupCommand('edit');

      expect(field<HTMLInputElement>(GROUP_NAME_CONTROL_ID).value).toBe(hostile);
      expect(query('b')).withContext('no element parsed out of stored wording').toBeNull();

      press(GROUP_EDITOR_SUBMIT_LABEL);
      expectRequest('PUT', roleGroupUrl(4)).flush(envelope(roleGroup(4, { roleGroupName: hostile })), {
        status: 200,
        statusText: 'OK',
      });
      fixture.detectChanges();

      expect(notifications()[0]?.severity).toBe('success');
      expect(notifications()[0]?.message).toContain(hostile);
      expect(query('b')).withContext('nor in the announcement').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF — THE ADDRESS CARRIES THE NARROWING AND THE PAGE
  //
  // Runtime testing on the sibling portal listing measured five separate desyncs from keeping this state
  // privately: pager clicks advanced the grid while the address stayed on the bare route, pressing back
  // from page three was not possible because paging created no history entry at all, a typed address with a
  // filter on it issued no request, and a fresh arrival from another screen landed on a page and a narrowing
  // the operator could not see, because these stores are provided at the application root and OUTLIVE their
  // routes. This block is the contract that closes all of it for the role listing.
  // ---------------------------------------------------------------------------------------------------

  describe('the address', () => {
    /**
     * Mounts the screen on a tenant with enough roles for the pager to render its steps.
     *
     * `arrive` reports a total equal to the number of rows it is given, which is one page - and a pager on
     * one page correctly withholds its steps, so a case that needs to press one has to arrive differently.
     *
     * @param totalCount The tenant's role count to report.
     */
    function arriveAcrossPages(totalCount: number): void {
      create();
      expectRequest('GET', ROLE_GROUPS_URL, 'the group read').flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL, 'the role read').flush(pageOf([roleRow()], totalCount));
      fixture.detectChanges();
    }

    /** Navigates to an address BEFORE the screen mounts, which is how an entry is simulated. */
    async function enterAt(url: string): Promise<void> {
      await TestBed.inject(Router).navigateByUrl(url);
    }


    /**
     * Presses one of the pager's steps by its accessible name, then settles the navigation it starts.
     *
     * The real control rather than the component method, so the case exercises the same path an operator
     * does - the method is `protected` and reaching past that would be asserting an interface nobody uses.
     */
    async function pressStep(name: 'First page' | 'Previous page' | 'Next page' | 'Last page'): Promise<void> {
      const step: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        `app-pagination button[aria-label="${name}"]`,
      );

      expect(step).withContext(`the "${name}" step is offered`).not.toBeNull();
      expect(step?.disabled).withContext(`the "${name}" step is available`).toBeFalse();

      step?.click();
      fixture.detectChanges();
      await settleAddress();
    }

    /** The query parameters the screen has actually navigated to. */
    function addressParams(): Readonly<Record<string, string>> {
      const router: Router = TestBed.inject(Router);

      return router.parseUrl(router.url).queryParams as Readonly<Record<string, string>>;
    }

    it('writes a chosen narrowing into the address rather than keeping it privately', async () => {
      await arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      await chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));

      // The real key, not the legacy `-2`/`-1` vocabulary the dropdown itself still uses.
      expect(addressParams()['group']).toBe('4');
    });

    it('writes the two pseudo-narrowings as words, never as their legacy negative numbers', async () => {
      await arrive();

      await chooseFilter('< All Roles >');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));

      expect(addressParams()['group'])
        .withContext('an address is read by people, so -2 says nothing and "all" says everything')
        .toBe('all');
    });

    it('omits the default narrowing instead of stating it', async () => {
      // So the address of a listing nobody has narrowed is the bare route. The measured legacy default is
      // the UNGROUPED narrowing (`Roles.ascx.vb:L48` initialises to -1, ungrouped per `:L114`).
      await arrive();

      await chooseFilter('< All Roles >');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      await chooseFilter('< Global Roles >');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));

      expect(addressParams()['group']).toBeUndefined();
    });

    it('writes a page turn into the address, one-based', async () => {
      // Four pages of roles, so the pager renders its steps at all.
      arriveAcrossPages(40);

      await pressStep('Next page');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()], 40));

      expect(addressParams()['currentpage'])
        .withContext('the address is one-based even though the store and the wire are not')
        .toBe('2');
    });

    it('restores a whole view from the address on entry: narrowing and page together', async () => {
      // ⚠ ONE READ, AT THE RIGHT COORDINATE. Restoring the two through the ordinary commands would issue
      // two reads, and the narrowing would reset the page on its way through - discarding the page the
      // address had just asked for. This is the case that would catch that.
      await enterAt('/roles?group=all&currentpage=3');
      create();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();

      const read: TestRequest = expectRequest('GET', ROLES_URL);

      expect(read.request.params.get('pageIndex')).toBe('2');
      // The WIRE spelling, which is not the union member's name: the store maps `AllRoles` to `All`.
      expect(read.request.params.get('scope')).toBe('All');

      read.flush(pageOf([roleRow()], 40));
      fixture.detectChanges();

      expect(httpMock.match(() => true))
        .withContext('and nothing further, so the restore cost exactly one listing read')
        .toHaveSize(0);
    });

    it('starts clean on a fresh entry, even though the store outlives the route', async () => {
      // THE MEASURED DEFECT: a fresh sidebar click landed on the page and narrowing of a previous visit.
      await arrive();

      await chooseFilter('< All Roles >');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()], 90));
      fixture.detectChanges();

      await pressStep('Last page');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()], 90));

      expect(addressParams()['currentpage'])
        .withContext('the previous visit really did leave a narrowing and a page behind')
        .toBe('9');
      expect(addressParams()['group']).toBe('all');

      fixture.destroy();
      mounted = false;

      // A fresh arrival at the bare route, with the same store still holding the previous coordinates.
      await enterAt('/roles');
      create();
      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();

      const read: TestRequest = expectRequest('GET', ROLES_URL);

      expect(read.request.params.get('pageIndex'))
        .withContext('the bare address means the first page, whatever the store still held')
        .toBe('0');
      expect(read.request.params.get('scope'))
        .withContext('the measured legacy default is the UNGROUPED narrowing')
        .toBe('Ungrouped');

      read.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    it('corrects an address that names no page, and replaces the entry rather than adding one', async () => {
      await enterAt('/roles?currentpage=abc');
      create();

      // ⚠ NOTHING IS READ ON THE FIRST EMISSION, WHICH IS THE CONTRACT. An unusable address is replaced and
      // the handler returns without reading; the replacement emits again and THAT emission does the read. So
      // the address has to settle before any request exists to expect.
      expect(httpMock.match(() => true))
        .withContext('the uncorrected address reads nothing')
        .toHaveSize(0);

      await settleAddress();

      expectRequest('GET', ROLE_GROUPS_URL).flush(envelope([roleGroup()]));
      fixture.detectChanges();

      expect(addressParams()['currentpage'])
        .withContext('an unusable value is corrected away, not obeyed and not kept')
        .toBeUndefined();

      const read: TestRequest = expectRequest('GET', ROLES_URL);

      expect(read.request.params.get('pageIndex')).toBe('0');
      read.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    it('leaves a parameter belonging to something else on the address alone', async () => {
      // ⚠ THE CASE THAT PROTECTS `?userId=`. This screen can be narrowed to one account by a route-bound
      // input reading that parameter. Reconciliation examines only the keys its own writer produces, so an
      // unusable page beside a foreign parameter corrects the page and keeps the account.
      await enterAt('/roles?userId=7&currentpage=1');
      create();
      await settleAddress();

      expect(addressParams()['userId']).toBe('7');
      expect(addressParams()['currentpage']).toBeUndefined();

      httpMock.match(() => true).forEach((pending) => pending.flush(envelope([])));
      fixture.detectChanges();
      httpMock.match(() => true).forEach((pending) => pending.flush(pageOf([])));
      fixture.detectChanges();
    });

    it('reads the narrowing selector once on entry and not again on a page turn', async () => {
      // Phase-6 work measured a page change down to exactly one request. The groups populate the selector
      // and no page turn can change them, so re-reading them here would silently return it to two.
      arriveAcrossPages(40);

      await pressStep('Next page');

      expectRequest('GET', ROLES_URL, 'the page read').flush(pageOf([roleRow()], 40));
      fixture.detectChanges();

      expect(httpMock.match((candidate) => candidate.url === ROLE_GROUPS_URL))
        .withContext('the groups are not re-read for a page turn')
        .toHaveSize(0);
    });
  });
});
