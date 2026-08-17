/**
 * Specification for {@link RoleListComponent} — the security-role listing at `/roles`. ## WHY THIS SCREEN
 * NEEDS ITS OWN SPECIFICATION It is TWO screens sharing one surface: a listing of roles, and an inline
 * editor for the role GROUP currently being filtered by.
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

/** The memberships-of-one-account address, for the account key these cases use. */
const USER_ROLES_URL = '/api/v1/users/42/roles';

/** A second account's address, so a switch of subject is provable. */
const OTHER_USER_ROLES_URL = '/api/v1/users/43/roles';

/** The account keyed nought's address, which no truthiness test may reduce to the bare listing. */
const USER_ZERO_ROLES_URL = '/api/v1/users/0/roles';

/** The account key these cases narrow to. */
const ACCOUNT_ID = 42;

/**
 * The scope line the header carries when the listing is NOT narrowed to one account. Under the
 * application's subtitle rule every screen states its scope, so the slot is never empty: it holds the
 * account's identity while filtered, and the listing's own scope otherwise.
 */
const UNFILTERED_SUBTITLE = 'The security roles on this site, and the groups they belong to.';

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

/**
 * The word a fee of nought is NAMED with rather than priced — QA-19. The exact amount is not withheld: it is
 * announced beside the word, so nothing a reader could have read from "0.00" is lost.
 */
const FREE_FEE_LABEL = 'Free';
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

/** The DOM-only pseudo-entry values. */
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
 * One listing row. ⚠ THE DEFAULT IDENTIFIER IS ZERO, because `dbo.Roles.RoleID` is seeded from zero: role
 * zero is the Administrators role of an installation and must render and link like any other.
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
 * A page of roles. ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture spelling it
 * otherwise flushes successfully and unwraps to no rows at all.
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

/** The field the Angular compiler writes the component definition onto. */
const COMPONENT_DEFINITION_FIELD = 'ɵcmp';

/** The flag the compiler sets from `changeDetection: ChangeDetectionStrategy.OnPush`. */
const ON_PUSH_FIELD = 'onPush';

/**
 * Whether a component type was compiled with `OnPush` change detection. ⚠ WHY THIS IS READ STRUCTURALLY
 * RATHER THAN OBSERVED. The usual demonstration is to move an input with `componentRef.setInput` and show
 * the view repaints only once the component is marked dirty. {@link RoleListComponent} does now declare
 * one input — the route-bound account key — so that move is possible, but it does not witness the
 * STRATEGY: the input is a SIGNAL input, which marks its consumer dirty under either strategy, and
 * everything it feeds is a signal too.
 */
function declaresOnPush(componentType: unknown): boolean {
  const definition: unknown = fieldOf(componentType, COMPONENT_DEFINITION_FIELD);

  return fieldOf(definition, ON_PUSH_FIELD) === true;
}

/**
 * One named field of an unknown value, or `undefined` where the value cannot carry fields. ⚠ READ THROUGH
 * `Reflect.get` RATHER THAN THROUGH A CAST. Asserting an unknown into an index signature is the shape of
 * assertion that hides a mistake — it type-checks against a value that may be a number, a string or
 * nothing at all, and fails at run time instead.
 */
function fieldOf(carrier: unknown, field: string): unknown {
  if (carrier === null || (typeof carrier !== 'object' && typeof carrier !== 'function')) {
    return undefined;
  }

  return Reflect.get(carrier, field);
}

/** Whether a signal is read-only — that is, whether it withholds both mutators. */
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
   * Whether {@link create} has run in the CURRENT case. ⚠ A CLOSURE VARIABLE SURVIVES THE CASE THAT
   * ASSIGNED IT, so `fixture` still holds the previous case's component even in a case that never mounted
   * one.
   */
  let mounted = false;

  beforeEach(async () => {
    mounted = false;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    administersPortal = signal<boolean>(true);

    await TestBed.configureTestingModule({
      imports: [RoleListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // ⚠ A ROUTE THAT ALWAYS MATCHES, because this screen now keeps its narrowing and its page in the
        // ADDRESS and writes them with a real navigation.
        provideRouter([{ path: '**', component: RoleListComponent }]),
        RoleStore,
        { provide: AuthStore, useValue: { administersCurrentPortal: administersPortal } },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    // ⚠ THE FIXTURE IS TORN DOWN BEFORE THE BACKEND IS VERIFIED, AND THAT ORDER IS DELIBERATE.
    if (mounted) {
      fixture.destroy();
      mounted = false;
    }

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
   * Lets a navigation this screen started actually happen. The narrowing selector, the pager and the
   * ordering controls write the ADDRESS rather than calling the store, and a router navigation is
   * asynchronous, so the read that follows one is not issued in the same task.
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
   * Answers the chained arrival read. ⚠ THE ORDER IS FIXED BY THE COMPONENT, NOT BY THIS HELPER. The
   * groups are read first and the roles read is issued only once they have answered, so answering them
   * the other way round finds nothing.
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
   * The component's host element. ⚠ BY ASSIGNMENT, NOT BY CAST. `ComponentFixture.nativeElement` is
   * declared `any`, so the annotated local is what gives it a type — and it is a real check rather than a
   * cosmetic one, because a cast would equally have accepted a wrong element type and pushed the failure
   * into whichever assertion happened to touch it first.
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
   * The one element matching `selector`, narrowed by a real check. ⚠ THIS EXISTS BECAUSE A NON-NULL
   * ASSERTION IS NOT ALLOWED HERE. `element!.textContent` would silence the compiler and then read
   * `textContent` of `null` at run time, and Jasmine reports that as a bare `TypeError` naming neither
   * the selector nor the case's intent.
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
   * Presses one of the two icon-only group commands, which carry no text of their own. ⚠ THEY CANNOT BE
   * FOUND BY WORDING. Each is a real button whose visible content is an image plus clipped text, so the
   * class is the handle; the accessible name is asserted separately.
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
   * A minimal account-detail payload, carrying only what the subtitle reads. The decoder requires the
   * whole contract, so this states every member rather than the two the screen consumes — a partial
   * payload would be rejected before the name reached the subtitle.
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
      // narrowing the roles read carries, so issuing both at once could read the roles under a filter the
      // screen is about to abandon.
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
      // ⚠ AN AFFORDANCE AND NEVER AN ENFORCEMENT POINT. The API re-authorises every request and its verdict
      // governs, so hiding these links protects nothing — what it does is stop offering an operator two
      // screens the router is certain to refuse, which is the difference between an application that knows
      // what you may do and one that lets you find out by failing.
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

  // NARROWED TO ONE ACCOUNT

  describe('narrowed to one account', () => {
    /**
     * Mounts the screen already narrowed to one account, and settles all three arrival reads. ⚠ THREE
     * READS, NOT TWO. The group read and the chained role read are the screen's ordinary arrival; the
     * membership read is issued by the effect that watches the account key, on the same first pass.
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
     * The role names the grid is currently rendering, in order. Located by its HEADING rather than by a
     * fixed offset: the first cell of a row carries the edit command, and a positional read would
     * silently compare command labels instead of names.
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
      // ⚠ THE DEFECT THIS FIXES. The tenant listing answered with a role the account does NOT hold;
      // rendering it would show the operator every role in the tenant under a heading that names one
      // person.
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

      // ⚠ SELECTED BY LABEL, NOT BY POSITION. Narrowing to an account now offers TWO affordances — the way
      // BACK to the person and the way SIDEWAYS to every role — and a positional read would silently assert
      // about whichever happened to come first.
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

    it('PAGES ITSELF HONESTLY while narrowed, rather than reporting the tenant listing\u2019s figures', () => {
      arriveForAccount(
        [roleRow(0, { roleName: 'Administrators' }), roleRow(3, { roleName: 'Translators' })],
        ACCOUNT_ID,
        // A tenant listing far larger than the membership, which is what made the contradiction visible.
        [roleRow(7, { roleName: 'Subscribers' })],
      );

      expect(renderedRoleNames()).withContext('the memberships are what is rendered').toHaveSize(2);

      const status: Element | null = query('.pagination__status');

      expect(status).withContext('the range summary is rendered').not.toBeNull();
      expect((status?.textContent ?? '').replace(/\s+/gu, ' ').trim())
        .withContext('the summary counts the rows on screen, not the tenant listing')
        .toBe('1\u20132 of 2');
      expect(queryAll('app-pagination button'))
        .withContext('and offers no step, because the membership read has no second page')
        .toHaveSize(0);
    });

    it('offers NO account affordance and issues NO membership read when no account is the subject', () => {
      arrive();

      expect(pageActions()).not.toContain('Show All Roles');
      expect(subtitle()).toBe(UNFILTERED_SUBTITLE);
      httpMock.expectNone(
        (candidate) => candidate.url.startsWith('/api/v1/users/'),
        'no membership read without an account',
      );
    });

    it('RE-READS FOR THE NEW ACCOUNT when the address moves between two accounts', () => {
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
      // The GRID shows the shared table's waiting state for the duration — waiting wins over both data and
      // emptiness there — so the assertion about the grid is that the previous account's row is nowhere on
      // screen, rather than that some other row is.
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

      // The rows that answer "what roles exist in this tenant" must not stand in for an answer to "what
      // roles does this account hold" that was never given. Showing none of them, and stating the refusal,
      // is the only presentation that claims nothing untrue.
      expect(renderedRoleNames())
        .withContext('no borrowed rows from a different question')
        .toEqual([]);
      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID}`);

      const banner = query('app-error-banner');

      expect(banner).withContext('the refusal is stated').not.toBeNull();
      expect(banner?.textContent ?? '')
        .withContext("and it carries the server's own explanation")
        .toContain('not permitted');

      answerAccountNameRead(ACCOUNT_ID);

      expect(subtitle()).toBe(`Roles held by account ${ACCOUNT_ID}`);
      expect(queryAll('app-error-banner'))
        .withContext('the refused name read does not add a second banner')
        .toHaveSize(1);
    });

    it('keeps the announcing region mounted before anything has failed', () => {
      arrive();

      expect(query('app-error-banner'))
        .withContext('the region is present on a healthy screen')
        .not.toBeNull();
      expect(query('app-error-banner [role="alert"]')?.getAttribute('aria-live'))
        .withContext('with its announcement semantics already declared')
        .toBe('assertive');
      expect(query('app-error-banner .error-banner'))
        .withContext('and nothing painted inside it')
        .toBeNull();
      expect(query('.role-list__failure-retry'))
        .withContext('while the recovery command is offered only for a failure that happened')
        .toBeNull();
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
      expect(subtitle()).toBe(UNFILTERED_SUBTITLE);
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
      expect(subtitle()).toBe(UNFILTERED_SUBTITLE);
      expect(pageActions()).not.toContain('Show All Roles');
    });

    it('keeps the tenant-wide affordances beside the account ones, in that order', () => {
      arriveForAccount();

      // The way BACK to the person comes first, then the way SIDEWAYS to every role, then the create
      // affordances, which remain gated as they were — so nothing is lost by narrowing.
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

    /** ⚠ THE AFFORDANCE IS WITHHELD WHEN AN ACCOUNT IS THE SUBJECT, and this is not tidiness. */
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
     * ⚠ A NARROWING CHANGED TWICE MUST NOT PAINT THE FIRST ANSWER. The legacy screen posted back for each
     * narrowing and the browser discarded the earlier response for us. Nothing discards it here, so the
     * store holds one handle per read and abandons the outstanding one before issuing its replacement.
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
     * ⚠ THE READ IS OWNED BY THE ROOT-PROVIDED STORE, NOT BY THIS SCREEN. `RoleStore` is declared
     * `providedIn: 'root'`, so it deliberately outlives every screen that reads through it: navigating
     * away and back must not throw away a listing that has already been paid for.
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
      // ⚠ THE QUESTION IS ASSERTED AS A PREFIX AND THE RECORD BY NAME, WHICH IS STRONGER THAN THE EQUALITY
      // THIS REPLACES. The body used to be the bare legacy sentence and named nothing - searched against
      // every identifier on the page it matched none of them - while the dialog is a real modal that covers
      // the grid, including the row being destroyed. Keeping the sentence as a PREFIX is what still proves
      // the measured wording survives verbatim; asserting the name is what proves the operator can tell
      // which record is at risk without seeing the row.
      const body: string = (query('.confirm-dialog__message')?.textContent ?? '').trim();

      expect(body.startsWith(REMOVAL_MESSAGE))
        .withContext(`the measured question, verbatim, at the front of: ${body}`)
        .toBeTrue();
      // ⚠ AND IT NAMES THE GROUP, NOT A ROLE. This screen mounts TWO confirmations that shared one message
      // while the wording named nothing; naming a role group in the dialog that destroys a ROLE would be
      // worse than naming nothing, so the two are now separate and this asserts the group's own name.
      expect(body).toContain('Paid Services');
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

      // ⚠ THIS IS THE RACE THE CLIENT PRECONDITION CANNOT CLOSE: the listing said the group was empty, and
      // by the time the request landed it was not. So the refusal re-reads BOTH halves — the groups and the
      // roles, as two independent requests this time — to show what is actually there.
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
     * landmark role make that list ambiguous rather than richer.
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
      arrive([roleGroup()], [roleRow(0, { billingFrequency: '4', trialFrequency: 'm' })]);

      // The PAINTED characters are what is asserted, read from the `aria-hidden` spans alone. Each cell's
      // whole text now also carries the clipped wording that names the code for a reader, so a whole-cell
      // comparison would test two facts at once.
      const painted: readonly string[] = Array.from(
        (rows()[0] as HTMLTableRowElement).querySelectorAll('[aria-hidden="true"]'),
      ).map((span) => (span.textContent ?? '').trim());

      expect(rows()).withContext('the page is rendered rather than refused').toHaveSize(1);
      expect(painted).toContain('4');
      expect(painted)
        .withContext('case is data: a lower-case code is not folded onto the upper-case one')
        .toContain('m');
    });

    // ⚠ THIS REPLACES A FACT THAT REQUIRED THE ABSENT CELLS TO BE EMPTY, AND THE EMPTINESS WAS THE DEFECT —
    // QA-15. The claim it exists for is unchanged and is what the expectations below still pin: a fee of
    // "nothing" is not a fee of zero and must never be drawn as one. What changed is what "nothing" LOOKS
    // like. An empty cell said nothing to a sighted reader and nothing to a screen reader, and on this grid it
    // was measurably inconsistent with itself — the fee and period columns of the same row rendered a mark
    // while the two frequency columns rendered nothing at all. All four columns now use the ONE shared idiom.
    it('marks every absent money, period and frequency with the one shared absence idiom', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: null, billingPeriod: null })]);

      const cells: readonly string[] = Array.from(
        (rows()[0] as HTMLTableRowElement).querySelectorAll('td,th'),
      ).map((cell) => (cell.textContent ?? '').trim());

      // ⚠ THIS TEST USED TO COUNT EMPTY CELLS, AND WHAT IT WAS ACTUALLY COUNTING WERE THE TWO FREQUENCY
      // CELLS. Money and period absences already painted the shared em-dash mark with clipped wording
      // beside it; the two frequency columns painted nothing at all for the same absence, which is one of
      // the eleven separate absent-value idioms this application was measured to be using where two would
      // do. All four now use the one idiom, so NO cell is silently empty.
      expect(cells.filter((text) => text.length === 0))
        .withContext('no absence renders as a silently empty cell')
        .toHaveSize(0);

      const marked: readonly string[] = cells.filter((text) => text.startsWith('\u2014'));

      expect(marked.length)
        .withContext('money, period and frequency absences all carry the mark')
        .toBeGreaterThanOrEqual(4);
      expect(marked.every((text) => text.endsWith('not recorded')))
        .withContext('and every mark is explained in words')
        .toBeTrue();

      // NOT as zero, which is the failure this case has always guarded against.
      expect(cells).not.toContain('0.00');
      expect(cells).not.toContain('0');
    });
  });

  // PROOF 6 — THE THREE-WAY GROUPING FILTER, WITHOUT CONFLATION

  describe('the three-way grouping filter', () => {
    /** The picker's options, as rendered: label and DOM value together, in document order. */
    function offeredOptions(): readonly { label: string; value: string }[] {
      return Array.from(field<HTMLSelectElement>(FILTER_CONTROL_ID).options).map((option) => ({
        label: (option.textContent ?? '').trim(),
        value: option.value,
      }));
    }

    /**
     * ⭐ THE DEFAULT NARROWING IS THE UNGROUPED SCOPE, NOT THE ALL-ROLES ONE. `Roles.ascx.vb` L48 is
     * `Private RoleGroupId As Integer = -1`, and L72's `If RoleGroupId < -1` is STRICTLY less-than, so
     * the initial -1 falls to the ELSE arm and reads `GetRolesByGroup(PortalId, -1)` — the roles
     * belonging to no group. It does NOT read every role.
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
     * The legacy order is fixed by construction: L112 adds the all-roles entry, L114-118 adds the global
     * one, and only then does L120's loop append the groups. Every one is `.Items.Add`, never
     * `.Items.Insert`, so nothing is ever placed ahead of the two pseudo-entries.
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
     * ⭐ THE TWO PSEUDO-ENTRIES ARE NOT INTERCHANGEABLE, and this is the case that would fail if they were
     * ever merged. -2 asks for every role in the tenant; -1 asks for the roles that belong to no group,
     * which is a REAL PERSISTED VALUE of `Roles.RoleGroupID` and therefore a genuine data predicate.
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
     * ⭐ GROUP ZERO IS A GROUP. Nothing in this screen may test a grouping identifier for truthiness or
     * for a positive sign: `>= 0` is the whole of the "real group" test, so zero addresses the group it
     * names and travels as the digit it is.
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
     * With no group in the tenant there is nothing to narrow BY, so the legacy screen forced the filter
     * to -2 and hid the whole row. Keeping the ungrouped default there would show only the ungrouped
     * roles behind a control the operator cannot see or change.
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

  // PROOF 7 — THE GRID'S TEN DATA COLUMNS

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
     * The body cell sitting beneath a named heading. Addressed by heading rather than by ordinal, because
     * an ordinal silently follows a column reordering while a heading name does not — and because a case
     * that names the column it means reads as the sentence it is proving.
     */
    function cellUnder(heading: string): string {
      return cellsOfFirstRow()[columnIndexOf(heading)] ?? '';
    }

    /**
     * Every clipped-content convention a cell on this screen can carry.
     *
     * ⚠ THERE ARE TWO BECAUSE ONE OF THEM IS NOW SHARED — QA-15. `.role-list__absent-value` is this screen's
     * own clipped span, still used for the frequency expansion and the free-fee amount; `.absent-value__
     * description` belongs to the shared absent-value component, which replaced the mark-and-words pair this
     * screen used to compose for itself. A helper that knew only the local class reported the shared
     * component's hidden sentence as PAINTED text, which is the opposite of what it is.
     */
    const CLIPPED_SELECTOR = '.role-list__absent-value,.absent-value__description';

    /**
     * What a cell of the FIRST row actually PAINTS, with clipped content excluded. Two cell kinds on this
     * screen now carry content that is deliberately in the accessibility tree and deliberately not on the
     * screen: the absent-value mark's own words, and the expansion of a stored frequency character.
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

      copy.querySelectorAll(CLIPPED_SELECTOR).forEach((clipped) => {
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

      return (cell?.querySelector(CLIPPED_SELECTOR)?.textContent ?? '').trim();
    }

    /**
     * The body cell ELEMENT sitting beneath a named heading, so a case can assert on what is inside one cell
     * rather than on what is anywhere in the row.
     *
     * @param heading The column heading to read under.
     * @returns The cell element, asserted to exist.
     */
    function cellElementUnder(heading: string): HTMLTableCellElement {
      const cell: HTMLTableCellElement | undefined = Array.from(
        rows()[0]?.querySelectorAll<HTMLTableCellElement>('td,th') ?? [],
      )[columnIndexOf(heading)];

      if (cell === undefined) {
        throw new Error(`no body cell under "${heading}"`);
      }

      return cell;
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
     * ⭐ EIGHT LEGACY HEADER KEYS FOR TEN COLUMNS, DISAMBIGUATED. `Roles.ascx.resx` supplies exactly eight
     * `.Header` entries — Name, Description, Fee, Every, Period, Trial, Public, Auto — because DotNetNuke
     * localised a grid heading by its `HeaderText` VALUE rather than by the column, so `Every.Header` and
     * `Period.Header` each served two columns and the rendered grid showed "Every" and "Period" twice
     * with nothing to tell the pairs apart.
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
     * two blank headings.
     */
    // ⚠ EXACTLY ONE COLUMN TRACK IS LEFT FLEXIBLE, AND THAT IS A REQUIREMENT RATHER THAN AN OMISSION — QA-09.
    //
    // Under `table-layout: fixed` the percentage tracks are resolved against the table width and whatever is
    // LEFT OVER is handed to the columns that declared something else. With every column weighted, that
    // leftover went to the three icon command columns: each asked for 3.25rem and painted 63.906px, wider than the fee columns beside them. A single unweighted column absorbs the slack
    // instead, so every other track resolves to exactly the share it declares.
    it('leaves exactly one column track flexible so the declared tracks resolve as written', () => {
      arrive();

      const tracks = queryAll<HTMLTableColElement>('colgroup col');

      expect(tracks.length).withContext('one track per rendered column').toBe(13);

      const flexible: readonly number[] = tracks
        .map((track, index) => ({ index, declared: track.style.inlineSize }))
        .filter((entry) => entry.declared === '')
        .map((entry) => entry.index);

      expect(flexible).withContext('one and only one flexible track').toEqual([4]);

      // And the command tracks declare the token they are supposed to, rather than inheriting the slack.
      for (let index = 0; index < 3; index += 1) {
        expect(tracks[index]?.style.inlineSize).toContain('--table-command-column-inline-size');
      }
    });

    it('clips the heading of each command column rather than publishing one', () => {
      arrive();

      const clipped: readonly string[] = headerCells()
        .filter((cell) => cell.querySelector('.data-table__label--hidden') !== null)
        .map((cell) => (cell.textContent ?? '').trim());

      // THREE commands now, not two: the removal command was added to this listing, and the reasoning for
      // it — parity as a floor rather than a ceiling, plus the three sibling listings that all carry one —
      // is recorded on the view query in the component.
      expect(headerCells()).withContext('three commands plus ten data columns').toHaveSize(13);
      expect(clipped).toEqual([EDIT_LABEL, MANAGE_USERS_LABEL, 'Delete']);
    });

    // ⚠ THIS REPLACES A FACT THAT REQUIRED A ZERO FEE TO PAINT "0.00", AND THE REPLACEMENT IS A MEASURED
    // CORRECTION — QA-19. The legacy formatter's only test is `If fee <> Null.NullSingle`, so a stored zero
    // prints rather than being withheld, and THAT half is unchanged: the amount is still stated, and it is
    // still stated exactly. What changed is that it is no longer stated ONLY as a figure. A free role and a
    // role costing a penny were told apart by reading two decimal places, in identical colour, weight and
    // slant, on the one column an administrator scans to find the free roles — while the two sibling listings
    // had by then each grown a deliberate non-colour vocabulary for exactly this shape of fact.
    //
    // The figure remains available to a reader who wants it: it is announced, in full, beside the word.
    it('names a fee of zero as free in both fee columns, and still states the amount', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: 0, trialFee: 0 })]);

      expect(paintedCellUnder('Fee')).toBe(FREE_FEE_LABEL);
      expect(paintedCellUnder('Trial')).toBe(FREE_FEE_LABEL);
      expect(clippedCellUnder('Fee')).toBe('no charge, amount 0.00');
      expect(clippedCellUnder('Trial')).toBe('no charge, amount 0.00');

      // NOT the absent rendering. A recorded zero and an unrecorded fee are different facts and this is the
      // expectation that keeps them different — the mark must not appear in EITHER FEE CELL. Scoped to the two
      // cells rather than to the row, because this fixture's periods are genuinely unrecorded and their cells
      // carry the mark correctly.
      expect(cellElementUnder('Fee').querySelector('app-absent-value')).toBeNull();
      expect(cellElementUnder('Trial').querySelector('app-absent-value')).toBeNull();
    });

    // The counterpart, and the one that stops the naming from over-reaching: a penny is a charge, and it is
    // painted as the figure it is with no state word anywhere near it.
    it('paints the smallest real charge as a figure, with no state word', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: 0.01, trialFee: 0.01 })]);

      expect(paintedCellUnder('Fee')).toBe('0.01');
      expect(paintedCellUnder('Trial')).toBe('0.01');
      expect(cellElementUnder('Fee').querySelector('.role-list__fee-state')).toBeNull();
      expect(cellElementUnder('Trial').querySelector('.role-list__fee-state')).toBeNull();
    });

    /**
     * ⭐⭐ ZERO IS A PERIOD, for the same reason and by the same guard: `FormatPeriod`'s only test is `If
     * period <> Null.NullInteger`, so only -1 is withheld and zero prints.
     */
    it('renders a period of zero as zero in both period columns', () => {
      arrive([roleGroup()], [roleRow(0, { billingPeriod: 0, trialPeriod: 0 })]);

      expect(cellUnder('Billing Every')).toBe('0');
      expect(cellUnder('Trial Every')).toBe('0');
    });

    /**
     * ⭐⭐ THE TWO SENTINELS, AND ONLY THE TWO SENTINELS, RENDER BLANK. `Null.vb` spells an absent single
     * as `Single.MinValue` and an absent integer as -1, and those are the exact values the legacy
     * formatters withhold.
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

      // ⚠ THE TWO FEE COLUMNS CARRY THE MARK TOO, AND THE ASYMMETRY THAT PRECEDED IT WAS FOUND BY A BROWSER
      // PASS RATHER THAN BY READING. The mark reached the period columns first; a run over the whole
      // 145-role dataset then found that on the one role whose fees are stored NULL the money cells
      // rendered as empty strings while the count cells beside them on the SAME ROW carried the mark and
      // its words.
      expect(paintedCellUnder('Fee')).toBe('\u2014');
      expect(paintedCellUnder('Trial')).toBe('\u2014');
      expect(clippedCellUnder('Fee')).toBe('not recorded');
      expect(clippedCellUnder('Trial')).toBe('not recorded');

      expect(paintedCellUnder('Billing Every')).toBe('\u2014');
      expect(paintedCellUnder('Trial Every')).toBe('\u2014');
      expect(clippedCellUnder('Billing Every'))
        .withContext('a reader who cannot see the dash is told what it means')
        .toBe('not recorded');
      expect(clippedCellUnder('Trial Every')).toBe('not recorded');
    });

    // ⚠ THE FEE EXPECTATIONS HERE MOVED WITH THE ONE ABOVE — QA-19: a zero fee is now NAMED "Free" and its
    // exact amount announced beside the word. The claim this case exists for is unchanged and is what the
    // period columns still pin: a stored zero is DATA and must never be rendered as an absence, because the
    // legacy guard withholds -1 and nothing else.
    it('renders a zero fee and a zero period as themselves, never as absent', () => {
      arrive(
        [roleGroup()],
        [roleRow(0, { serviceFee: 0, trialFee: 0, billingPeriod: 0, trialPeriod: 0 })],
      );

      expect(paintedCellUnder('Fee')).toBe(FREE_FEE_LABEL);
      expect(paintedCellUnder('Trial')).toBe(FREE_FEE_LABEL);
      expect(clippedCellUnder('Fee'))
        .withContext('the exact stored amount is still available, in words')
        .toBe('no charge, amount 0.00');

      expect(paintedCellUnder('Billing Every')).toBe('0');
      expect(paintedCellUnder('Trial Every')).toBe('0');
      expect(clippedCellUnder('Billing Every')).toBe('');

      // The decisive expectation: none of the four cells this fixture RECORDS a value in claims an absence.
      // Scoped to those four rather than to the row, because the fixture leaves both frequency characters
      // unset and their cells state that absence correctly.
      for (const heading of ['Fee', 'Trial', 'Billing Every', 'Trial Every'] as const) {
        expect(cellElementUnder(heading).querySelector('app-absent-value'))
          .withContext(`${heading} records a value and must not claim an absence`)
          .toBeNull();
      }
    });

    /**
     * ⚠ R-M1, THE OTHER HALF: A NEGATIVE PERIOD THAT IS NOT THE SENTINEL STILL RENDERS ITSELF.
     * `Roles.ascx.vb:L152-L162` guards on `period <> Null.NullInteger` and nothing else, so a stored `-3`
     * is data and is displayed. That behaviour is unchanged — what changed is that the sentinel case
     * beside it is no longer indistinguishable from it.
     */
    // ⚠ THE TWO FREQUENCY COLUMNS WERE THE LAST CELLS ON THIS SCREEN THAT SAID NOTHING AT ALL — QA-15.
    // Measured on the role whose whole paid-membership configuration is unset: its fee, billing-every, trial
    // and trial-every cells each rendered the shared absent mark while these two rendered an entirely empty
    // cell — no text, no children, nothing announced — in the SAME row. A reader could not tell "no frequency
    // recorded" from "this cell failed to draw", and every other unrecorded value beside it could be.
    it('states an unrecorded frequency with the shared absent value rather than an empty cell', () => {
      arrive(
        [roleGroup()],
        [roleRow(0, { billingFrequency: null, trialFrequency: null })],
      );

      for (const heading of ['Billing Period', 'Trial Period'] as const) {
        expect(paintedCellUnder(heading))
          .withContext(`${heading} states its absence`)
          .toBe('\u2014');
        expect(clippedCellUnder(heading)).toBe('not recorded');
        expect(cellElementUnder(heading).querySelector('app-absent-value')).not.toBeNull();
      }
    });

    // The counterpart: a recorded character is STILL painted verbatim, because it is load-bearing data
    // constrained by `FK_Roles_CodeFrequency` and the legacy grid bound the raw field rather than a join.
    it('paints a recorded frequency character verbatim, with its expansion announced', () => {
      arrive([roleGroup()], [roleRow(0, { billingFrequency: 'M', trialFrequency: 'D' })]);

      expect(paintedCellUnder('Billing Period')).toBe('M');
      expect(clippedCellUnder('Billing Period')).toBe('Month');
      expect(cellElementUnder('Billing Period').querySelector('app-absent-value')).toBeNull();

      expect(paintedCellUnder('Trial Period')).toBe('D');
      expect(clippedCellUnder('Trial Period')).toBe('Day');
    });

    it('renders a negative period that is not the sentinel as itself', () => {
      arrive([roleGroup()], [roleRow(0, { billingPeriod: -3, trialPeriod: -3 })]);

      expect(paintedCellUnder('Billing Every')).toBe('-3');
      expect(clippedCellUnder('Billing Every'))
        .withContext('a real value carries no absence words')
        .toBe('');
    });

    /**
     * ⚠ R-M4: AN AMOUNT THE CELL CANNOT STATE EXACTLY SAYS SO, IN WORDS, AND STILL PAINTS ITSELF. The
     * column is SQL `money` — exact decimal at the full width of a 64-bit integer — and the wire carries
     * a JSON number, which is read as an IEEE-754 double.
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
     * R3 — A NEGATIVE AMOUNT IS MARKED, IN THE TREATMENT THE PORTAL LISTING ALREADY USES. There a fee below
     * zero is painted in the danger colour at bold weight with the word "negative" clipped beside it. Here
     * the identical state was a bare text node in the ordinary colour and weight, so a credit and a charge
     * differed by a single minus glyph and by nothing a screen reader could report.
     */
    it('marks a negative amount, in words as well as in colour', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: -99.99, trialFee: -1.5 })]);

      expect(paintedCellUnder('Fee')).toBe('-99.99');
      expect(clippedCellUnder('Fee')).toBe('negative');
      expect(clippedCellUnder('Trial')).toBe('negative');
      expect(query('.role-list__fee--negative'))
        .withContext('and the sign is not carried by the clipped word alone')
        .not.toBeNull();
    });

    /**
     * R7 — THE ACCESSIBLE NAME MUST SEPARATE THE VALUE FROM ITS QUALIFIER. Angular compiles templates with
     * `preserveWhitespaces` disabled, which DELETES a whitespace-only text node standing between two
     * elements, so a clipped qualifier written on its own line joins the value with no separator at all and
     * the cell announces one run-together word. Measured on the sibling listing as `-99.99negative`.
     *
     *
     * ⚠ THE ASSERTION PINS U+00A0 RATHER THAN "SOME WHITESPACE", AND THAT PRECISION IS ITSELF THE FIX. An
     * ordinary space reaches the DOM but is not always handed to a screen reader: where Angular's anchor
     * comments leave the separator an isolated whitespace-only text node, Blink builds no text box for it, so
     * it never becomes a StaticText node and name-from-contents concatenates regardless. That was measured
     * through the CDP Accessibility domain in two independent browsers on the sibling role-assignment screen,
     * where a cell whose DOM `textContent` read "9/20/2027 Active" computed an accessible name of
     * "9/20/2027Active". A spec asserting only `\s` would accept the very character that regressed.
     *
     * This is the case that discriminates: the mark and the word are both present either way, and only the
     * separator tells a correct implementation from the one that shipped.
     */
    it('separates the amount from its clipped qualifier, rather than running them together', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: -99.99 })]);

      const first: HTMLTableRowElement | undefined = rows()[0];

      expect(first).withContext('a row is rendered').not.toBeUndefined();

      const whole: string = (
        Array.from(first?.querySelectorAll<HTMLTableCellElement>('td,th') ?? [])[
          columnIndexOf('Fee')
        ]?.textContent ?? ''
      ).trim();

      expect(whole).toContain('-99.99\u00a0negative');
      expect(whole).not.toContain('-99.99negative');
    });

    it('leaves a positive amount unmarked, so the mark keeps its meaning', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: 25, trialFee: 5 })]);

      expect(clippedCellUnder('Fee')).toBe('');
      expect(query('.role-list__fee--negative')).toBeNull();
    });

    it('does not call an ABSENT amount negative, though its sentinel is far below zero', () => {
      // `Null.NullSingle` is `Single.MinValue`, so a truthiness-free implementation would mark every absent
      // fee as a negative one. The absent branch owns that row.
      arrive([roleGroup()], [roleRow(0, { serviceFee: -3.4028234663852886e38 })]);

      expect(paintedCellUnder('Fee')).toBe('\u2014');
      expect(clippedCellUnder('Fee')).toBe('not recorded');
      expect(query('.role-list__fee--negative')).toBeNull();
    });

    /**
     * ⚠ THE R-M4 QUALIFIER MUST NOT WIDEN INTO ORDINARY MONEY. Every amount an operator will ever type is
     * exact in a double, so a qualifier appearing on one would be noise on every row of every page — and
     * would train a reader to ignore it on the one row where it matters.
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
     * ⚠ NO THOUSANDS SEPARATOR ON THIS SCREEN. The listing formats with `"##0.00"` and the role EDITOR
     * formats the same three amounts with `"#,##0.00"`.
     */
    it('renders a four-figure fee without a thousands separator', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: 1234.5, trialFee: 1000 })]);

      expect(cellUnder('Fee')).toBe('1234.50');
      expect(cellUnder('Fee')).not.toContain(',');
      expect(cellUnder('Trial')).toBe('1000.00');
      expect(cellUnder('Trial')).not.toContain(',');
    });

    /**
     * ⭐ ALL SIX PUBLISHED FREQUENCY CODES, VERBATIM, IN BOTH COLUMNS. `RoleController.vb` L540-L547
     * switches on six characters — N, O, D, W, M and Y — with no `Case Else`, and
     * `Roles.BillingFrequency` is `char(1)`, so the character IS the stored datum.
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

      // ⚠ THE PAINTED CHARACTER IS UNCHANGED, which is what this case has always been about.
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
     * ⚠ R-M3: WHAT THE CHARACTER MEANS REACHES A READER, WITHOUT REACHING THE SCREEN. The legacy grid
     * bound the raw field rather than the joined description, so a reader met an unexplained letter while
     * the role editor two clicks away rendered the same datum as "Month".
     */
    it('clips an expansion of each frequency character for a reader', () => {
      arrive([roleGroup()], [roleRow(0, { billingFrequency: 'M', trialFrequency: 'Y' })]);

      expect(clippedCellUnder('Billing Period')).toBe('Month');
      expect(clippedCellUnder('Trial Period')).toBe('Year');
    });

    /**
     * ⚠ THIS ASSERTION USED TO REQUIRE SILENCE, AND SILENCE WAS THE DEFECT. The reasoning was that a
     * character outside the closed vocabulary should contribute NOTHING rather than a guess, because
     * narrating it would invent a meaning. The measured consequence was worse than a guess: the painted
     * character sits in an `aria-hidden` span, so with the clipped sibling empty the cell reached a
     * screen-reader user as NOTHING AT ALL, while a sighted reader saw an unexplained 9x15-pixel letter with
     * no legend anywhere on the screen. Naming the code without claiming to know what it means - the same
     * treatment the profile-definition listing gives a data type it cannot name - invents nothing and
     * leaves nobody with an empty cell.
     */
    it('names a frequency character it cannot interpret, without claiming to know its meaning', () => {
      arrive([roleGroup()], [roleRow(0, { billingFrequency: 'Q' })]);

      expect(paintedCellUnder('Billing Period')).toBe('Q');
      expect(clippedCellUnder('Billing Period')).toBe('frequency code Q, name unavailable');
    });

    it('renders both flags as announced words, one cell each', () => {
      arrive([roleGroup()], [roleRow(0, { isPublic: true, autoAssignment: true })]);

      expect(cellUnder('Public')).toBe('Yes');
      expect(cellUnder('Auto')).toBe('Yes');
      expect(queryAll('td img')).withContext('no checked/unchecked image pair').toHaveSize(0);
    });

    /**
     * ⚠ FALSE IS DATA, NOT ABSENCE — and the legacy code is the reason this needs saying. `Null.vb`
     * spells an absent boolean `Return False`, and the companion `IsNull` therefore answers True for a
     * perfectly ordinary stored `False`.
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
     * written to an installation — the Administrators role — has the identifier zero. Any truthiness test
     * on it would drop the row, or paint it and leave both its commands addressing nothing.
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

  // PROOF 8 — STRUCTURAL PARITY WITH THE LEGACY GRID
  // What the legacy screen did NOT have is as much a part of the specification as what it did, because an
  // addition here is an unrequested change of behaviour. Each absence below was measured in `roles.ascx`
  // rather than assumed.

  describe('structural parity', () => {
    it('draws the pager beneath the grid, and offers no steps when everything fits one page', () => {
      arrive([roleGroup()], [roleRow(0), roleRow(1, { roleName: 'Registered Users' })]);

      const pager: Element | null = query('app-pagination');

      expect(pager)
        .withContext('mounted whenever the page in hand holds rows, so the range summary shows')
        .not.toBeNull();
      expect(pager?.querySelectorAll('button') ?? [])
        .withContext('no page-to-page steps for a single page')
        .toHaveSize(0);
    });

    it('offers no free-text filter, because the legacy screen had none', () => {
      arrive();

      expect(query('app-search-input')).toBeNull();
      expect(queryAll('input[type="search"]')).toHaveSize(0);
    });

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
     * The table carries a caption and NO `summary` attribute. MIGRATION: the caption is a net addition —
     * `default.css` styles no caption anywhere in its 1030 lines, because the legacy grid had none.
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
     * The three page-level actions are the legacy module's own three, in its own order: `Roles.ascx.vb`
     * L307 adds `AddContent.Action`, L308 `AddGroup.Action` and L309 `UserSettings.Action`. Their wording
     * comes from `Roles.ascx.resx` unchanged.
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

    /** ⚠ ONPUSH IS NOT OBSERVABLE FROM OUTSIDE THIS COMPONENT, so it is read from the compiled definition. */
    it('declares OnPush change detection', () => {
      expect(declaresOnPush(RoleListComponent)).toBeTrue();
    });

    /** The screen renders projections it cannot write to. */
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
    /** Arrives with a real, EMPTY group chosen. */
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
     * ⚠ NO CLAIM OF PERMANENCE, ANYWHERE IN THE CONFIRMATION — and this is a correctness requirement, not
     * a tone preference. Removal in this domain is frequently NOT destruction: cancelling a paid role
     * assignment whose trial has already been used sets an expiry date of yesterday and UPDATES the row,
     * precisely so the trial-used fact survives.
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
     * ⚠ THE CONFIRMATION'S PRESENCE IS WHAT "OPEN" MEANS. It exposes no `open` input, so the guard in the
     * template is the whole of the gating: absent until a removal is pending, present once one is.
     */
    it('raises the confirmation only once a removal is pending', async () => {
      await arriveWithEmptyGroup();

      expect(query('app-confirm-dialog')).withContext('nothing pending yet').toBeNull();

      pressGroupCommand('remove');

      expect(query('app-confirm-dialog')).withContext('now pending').not.toBeNull();

      pressDialogue(GROUP_EDITOR_CANCEL_LABEL);

      expect(query('app-confirm-dialog')).withContext('abandoned again').toBeNull();
    });

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
     * ⚠ AN RFC 7807 FIELD KEY IS READ WITH BRACKETS, NEVER WITH A DOT. `errors` is an index signature and
     * `noPropertyAccessFromIndexSignature` is enabled workspace-wide, so `refusal.errors.RoleGroupName`
     * does not compile — in this file any more than in the component.
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

    /** ⚠ STORED WORDING IS TEXT, INCLUDING ITS MARKUP. The legacy resource values are not clean strings. */
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

  // PROOF — THE ADDRESS CARRIES THE NARROWING AND THE PAGE

  describe('the address', () => {
    /**
     * Mounts the screen on a tenant with enough roles for the pager to render its steps. `arrive` reports
     * a total equal to the number of rows it is given, which is one page - and a pager on one page
     * correctly withholds its steps, so a case that needs to press one has to arrive differently.
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
     * Presses one of the pager's steps by its accessible name, then settles the navigation it starts. The
     * real control rather than the component method, so the case exercises the same path an operator does
     * - the method is `protected` and reaching past that would be asserting an interface nobody uses.
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

    /**
     * R5 — A PAGE PAST THE END OF A REAL RESULT SET IS NOT AN EMPTY RESULT SET.
     *
     * `?currentpage=99` against a real dataset left this grid reading "No records found." beside a pager
     * claiming "21-30 of 30" — a range describing records it was not showing and could not show — with no
     * route back. The portal and module listings already answer the same address by naming the state and
     * offering the button that undoes it.
     *
     * @param totalCount The dataset total the server keeps reporting for a page that holds nothing.
     */
    function arrivePastEnd(totalCount = 30): void {
      create();
      expectRequest('GET', ROLE_GROUPS_URL, 'the group read').flush(envelope([roleGroup()]));
      fixture.detectChanges();
      // A real total with NO rows: exactly what the server answers for a coordinate past the end.
      expectRequest('GET', ROLES_URL, 'the role read').flush(pageOf([], totalCount));
      fixture.detectChanges();
    }

    it('states that the page is past the end, rather than that nothing matched', () => {
      arrivePastEnd();

      expect((host().textContent ?? '')).toContain(
        'This page is past the end of the results. Return to the first page.',
      );
    });

    it('withholds the pager entirely, so no range describes records it cannot show', () => {
      arrivePastEnd();

      expect(query('app-pagination'))
        .withContext('a range beside an empty grid is the defect itself')
        .toBeNull();
      expect((host().textContent ?? '')).not.toContain('of 30');
    });

    it('offers the recovery, and taking it clears the page from the address', async () => {
      await enterAt('/roles?currentpage=99');
      arrivePastEnd();

      const recovery: HTMLButtonElement | null = host().querySelector<HTMLButtonElement>(
        '.role-list__first-page',
      );

      expect(recovery).withContext('the state names itself AND offers a way out').not.toBeNull();

      recovery?.click();
      fixture.detectChanges();
      await settleAddress();
      expectRequest('GET', ROLES_URL, 'the corrected read').flush(pageOf([roleRow()], 30));
      fixture.detectChanges();

      expect(addressParams()['currentpage'])
        .withContext('removed outright rather than rewritten to a number')
        .toBeUndefined();
    });

    /**
     * THE NARROWING GUARD, and the case that discriminates. An ordinarily empty result set is NOT past the
     * end: it keeps the legacy sentence and is offered no page recovery, because there is no page to
     * recover from. An implementation that showed the past-end wording whenever a grid was empty would
     * satisfy every case above and fail this one.
     */
    it('keeps the legacy sentence, and offers no recovery, when the set is simply empty', () => {
      create();
      expectRequest('GET', ROLE_GROUPS_URL, 'the group read').flush(envelope([roleGroup()]));
      fixture.detectChanges();
      expectRequest('GET', ROLES_URL, 'the role read').flush(pageOf([], 0));
      fixture.detectChanges();

      expect((host().textContent ?? '')).toContain('No records found.');
      expect((host().textContent ?? '')).not.toContain('past the end');
      expect(host().querySelector('.role-list__first-page')).toBeNull();
    });

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
      // the handler returns without reading; the replacement emits again and THAT emission does the read.
      // So the address has to settle before any request exists to expect.
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

  // ---------------------------------------------------------------------------------------------------
  // THE EMPTY-TABLE FLASH
  // ---------------------------------------------------------------------------------------------------

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. An un-asked listing and a listing that matched nothing are
  // both an empty page with no request in flight, so the grid painted "No records found." over a listing it
  // had not yet asked about - reported as an empty-table flash on every post-save return to a listing.
  describe('an un-asked listing waits rather than claiming to be empty', () => {
    /** Navigates before the screen mounts, which is how an entry at a given address is simulated. */
    async function enterAt(url: string): Promise<void> {
      await TestBed.inject(Router).navigateByUrl(url);
    }

    it('shows the waiting placeholder, and NO zero-result surface, while the address correction is in flight', async () => {
      // An unusable address takes the correction arm, which replaces the address and returns WITHOUT
      // reading. The replacement navigation happens a task later, so this is exactly the window in which
      // nothing is in flight and nothing is held.
      await enterAt('/roles?currentpage=abc');
      create();

      expect(httpMock.match(() => true))
        .withContext('the precondition: the uncorrected address reads nothing')
        .toHaveSize(0);

      expect(queryAll('td[data-placeholder] app-loading-spinner').length)
        .withContext('the listing has not been asked about, so the grid is waiting')
        .toBe(1);
      expect(queryAll('app-empty-state').length)
        .withContext('nothing may assert that this tenant has no roles before one has been read')
        .toBe(0);

      await settleAddress();
      answerArrival([roleGroup()], [roleRow()]);
    });

    it('shows the zero-result surface once a read has genuinely answered with nothing', () => {
      arrive([roleGroup()], []);

      expect(queryAll('td[data-placeholder] app-loading-spinner').length).toBe(0);
      expect(queryAll('app-empty-state').length)
        .withContext('a settled read that matched nothing IS the empty state')
        .toBe(1);
    });
  });

});

/**
 * Every style rule the document holds, component stylesheets included, flattened out of any media or
 * supports block that contains them.
 *
 * @returns Every style rule, with the media conditions that guard each one.
 */
function everyStyleRuleWithConditions(): readonly { rule: CSSStyleRule; conditions: readonly string[] }[] {
  const collected: { rule: CSSStyleRule; conditions: readonly string[] }[] = [];

  const walk = (rules: CSSRuleList, conditions: readonly string[]): void => {
    for (const rule of Array.from(rules)) {
      if (rule instanceof CSSStyleRule) {
        collected.push({ rule, conditions });
      }

      const nested: unknown = (rule as { cssRules?: CSSRuleList }).cssRules;

      if (nested instanceof CSSRuleList) {
        const condition: unknown = (rule as { conditionText?: string }).conditionText;
        const next =
          typeof condition === 'string' ? [...conditions, condition] : [...conditions];

        walk(nested, next);
      }
    }
  };

  for (const sheet of Array.from(document.styleSheets)) {
    try {
      walk(sheet.cssRules, []);
    } catch {
      continue;
    }
  }

  return collected;
}

/**
 * The hover rules that apply to one class, with the media conditions guarding each.
 *
 * @param className The class whose hover rules are wanted, without a leading dot.
 * @returns One entry per hover rule found.
 */
function hoverRulesFor(
  className: string,
): readonly { readonly declarations: string; readonly conditions: readonly string[] }[] {
  return everyStyleRuleWithConditions()
    .filter(
      (entry) =>
        entry.rule.selectorText.includes(`.${className}`) &&
        entry.rule.selectorText.includes(':hover'),
    )
    .map((entry) => ({ declarations: entry.rule.style.cssText, conditions: entry.conditions }));
}

/**
 * Specification for the hover feedback on the row commands of this listing.
 *
 * ⚠ WHAT WAS MEASURED, AND WHY A CSSOM TEST IS THE RIGHT INSTRUMENT. All three row commands reported an EMPTY
 * set of changed computed properties on hover, so the pointer shape was the only response the operator got from
 * a command that navigates away or destroys a role. Two separate causes were behind it: the declarations sat
 * inside a pointer-capability media block that was not being satisfied, and the destructive command re-set the
 * colour it already had while having stripped away the background and border that the global button hover
 * changes. Neither cause can be caught by hovering in a runner that reports its own pointer capability - the
 * rules themselves have to be read.
 */
describe('RoleListComponent row-command hover feedback', () => {
  // ⚠ THE COMPONENT IS MOUNTED FIRST, AND WITHOUT IT THIS WHOLE GROUP MEASURES NOTHING. A component's
  // stylesheet is injected into the document when the component is first instantiated, so reading the CSSOM
  // before that finds no rules at all - which reads as "the hover is missing" whatever the source says.
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RoleListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', component: RoleListComponent }]),
        RoleStore,
        { provide: AuthStore, useValue: { administersCurrentPortal: signal<boolean>(true) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RoleListComponent);

    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .match(() => true)
      .forEach((pending) => pending.flush({ data: [], meta: null }));
    fixture.detectChanges();
  });

  it('declares the hover for the two navigating commands OUTSIDE any pointer-capability guard', () => {
    const rules = hoverRulesFor('role-list__row-action');

    expect(rules.length).withContext('a hover rule exists at all').toBeGreaterThan(0);

    for (const rule of rules) {
      // The guard is what silently disabled the treatment. Any media condition mentioning hover or pointer
      // capability is the guard, whatever its exact spelling.
      const guarded = rule.conditions.some(
        (condition) => condition.includes('hover') || condition.includes('pointer'),
      );

      expect(guarded)
        .withContext(`no pointer-capability guard around: ${rule.declarations}`)
        .toBeFalse();
    }
  });

  it('changes the ink of the two navigating commands, so hovering is perceivable', () => {
    const declarations = hoverRulesFor('role-list__row-action')
      .map((rule) => rule.declarations)
      .join(' ');

    expect(declarations)
      .withContext('the hover moves the brand ink to its hover companion')
      .toContain('--color-primary-hover');
  });

  it('changes the SURFACE of the destructive command, because its ink is pinned', () => {
    const rules = hoverRulesFor('role-list__row-action--danger');

    expect(rules.length).toBeGreaterThan(0);

    const declarations = rules.map((rule) => rule.declarations).join(' ');

    // The colour cannot move - a destructive command stays in the danger hue and the vocabulary declares one
    // danger token - so the change has to be a surface. The selected tint is the only token that differs from
    // both the plain row surface and the alternating one.
    expect(declarations)
      .withContext('a background change the operator can actually see')
      .toContain('--color-selected');
    expect(declarations)
      .withContext('and the danger ink is kept rather than traded away')
      .toContain('--color-danger');

    for (const rule of rules) {
      expect(
        rule.conditions.some(
          (condition) => condition.includes('hover') || condition.includes('pointer'),
        ),
      )
        .withContext(`no pointer-capability guard around: ${rule.declarations}`)
        .toBeFalse();
    }
  });

  it('leaves the press state able to win over the hover it shares an element with', () => {
    // A pressed control is also a hovered one, so the press rule must come after the hover rule at equal
    // weight - otherwise pressing paints the hover and the command has no press state at all.
    const rules = everyStyleRuleWithConditions().filter((entry) =>
      entry.rule.selectorText.includes('.role-list__row-action'),
    );

    const hoverIndex = rules.findIndex((entry) => entry.rule.selectorText.includes(':hover'));
    const activeIndex = rules.findIndex((entry) => entry.rule.selectorText.includes(':active'));

    expect(hoverIndex).withContext('a hover rule exists').toBeGreaterThan(-1);
    expect(activeIndex).withContext('a press rule exists').toBeGreaterThan(-1);
    expect(activeIndex).withContext('and the press is declared after the hover').toBeGreaterThan(hoverIndex);
  });
});
