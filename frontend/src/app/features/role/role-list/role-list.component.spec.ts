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
import { provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { RoleStore } from '../../../core/state/role.store';
import { RoleListComponent } from './role-list.component';

import type { WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { RoleGroup, RoleListItem } from '../../../core/models/role.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

const ROLES_URL = '/api/v1/roles';
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

describe('RoleListComponent', () => {
  let fixture: ComponentFixture<RoleListComponent>;
  let httpMock: HttpTestingController;
  let heldPermissions: WritableSignal<readonly string[]>;
  let notifySpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    /*
     * ⚠ THE HELD PERMISSION KEYS ARE AN INPUT TO THIS SCREEN, so they are held in a signal the cases
     * can move. The two CREATE affordances are gated by `*hasPermission`, and that directive reads
     * exactly one member of the identity projection — `permissions()` — which is why a one-member
     * double is honest here rather than a convenience: nothing else in this component's subtree
     * touches the projection at all, verified rather than assumed.
     *
     * Seeded WITH the edit key, so the ordinary cases below describe the screen an administrator sees.
     * The gating itself is proved separately, by taking the key away.
     */
    heldPermissions = signal<readonly string[]>(['EDIT', 'VIEW']);

    await TestBed.configureTestingModule({
      imports: [RoleListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        RoleStore,
        { provide: AuthStore, useValue: { permissions: heldPermissions } },
      ],
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

  /** Creates the screen. The chained read is issued from `ngOnInit`, during this first pass. */
  function create(): void {
    fixture = TestBed.createComponent(RoleListComponent);
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
  function chooseFilter(label: string): void {
    const control = field<HTMLSelectElement>(FILTER_CONTROL_ID);
    const option: HTMLOptionElement | undefined = Array.from(control.options).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );

    expect(option).withContext(`the option labelled "${label}" is offered`).not.toBeUndefined();

    control.value = (option as HTMLOptionElement).value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
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

    it('withholds the two CREATE affordances from an account without the edit key', () => {
      // ⚠ AN AFFORDANCE AND NEVER AN ENFORCEMENT POINT. The API re-authorises every request and its
      // verdict governs, so hiding these links protects nothing — what it does is stop offering an
      // operator two screens whose save is certain to be refused, which is the difference between an
      // application that knows what you may do and one that lets you find out by failing.
      //
      // ⚠ AND THE SETTINGS LINK IS NOT GATED, DELIBERATELY. Reaching a settings screen is not itself a
      // mutation and that screen refuses its own save, so gating it here would hide readable state
      // behind a key that governs writing.
      heldPermissions.set(['VIEW']);

      arrive();

      const labelled: readonly string[] = queryAll<HTMLAnchorElement>(
        'a.role-list__page-action',
      ).map((link) => (link.textContent ?? '').trim());

      expect(labelled).not.toContain(ADD_ROLE_LABEL);
      expect(labelled).not.toContain(ADD_ROLE_GROUP_LABEL);
      expect(labelled).toEqual([MEMBERSHIP_SETTINGS_LABEL]);
    });

    it('restores the CREATE affordances the moment the key is held again', () => {
      // The directive re-evaluates from the identity rather than caching a verdict at first render, so
      // an account whose grants arrive after the screen has painted — which is the ordinary case, the
      // identity being fetched — is not left looking at a screen it cannot act on.
      heldPermissions.set([]);

      arrive();

      expect(queryAll('a.role-list__page-action')).toHaveSize(1);

      heldPermissions.set(['EDIT']);
      fixture.detectChanges();

      expect(
        queryAll<HTMLAnchorElement>('a.role-list__page-action').map((link) =>
          (link.textContent ?? '').trim(),
        ),
      ).toEqual([ADD_ROLE_LABEL, ADD_ROLE_GROUP_LABEL, MEMBERSHIP_SETTINGS_LABEL]);
    });

    it('offers three commands per row, two of them addressed by the role identifier', () => {
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

    it('sends a named scope for each pseudo-entry, never its negative number', () => {
      arrive();

      chooseFilter(ALL_ROLES_OPTION_LABEL);

      const all = expectRequest('GET', ROLES_URL, 'the all-roles read');

      // ⚠ THE NEGATIVE VALUES EXIST IN THE DOM ONLY, because a select must carry strings. What travels
      // is a named scope; a `roleGroupId` of minus two would be a request for a group that cannot exist.
      expect(all.request.params.get('scope')).toBe('All');
      expect(all.request.params.has('roleGroupId')).toBeFalse();
      expect(all.request.urlWithParams).not.toContain(ALL_ROLES_VALUE);

      all.flush(pageOf([roleRow()]));
      fixture.detectChanges();

      chooseFilter(GLOBAL_ROLES_OPTION_LABEL);

      const global = expectRequest('GET', ROLES_URL, 'the ungrouped read');

      expect(global.request.params.get('scope')).toBe('Ungrouped');
      expect(global.request.urlWithParams).not.toContain(GLOBAL_ROLES_VALUE);

      global.flush(pageOf([roleRow()]));
      fixture.detectChanges();
    });

    it('sends the real identifier for a chosen group, and returns to the first page', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      chooseFilter('Paid Services');

      const call = expectRequest('GET', ROLES_URL, 'the grouped read');

      expect(call.request.params.get('roleGroupId')).toBe('4');
      expect(call.request.params.has('scope')).withContext('no scope name').toBeFalse();
      // A coordinate measured against one match set does not address the same rows once the set changes.
      expect(call.request.params.get('pageIndex')).toBe('0');

      call.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('reveals the group commands once a real group is chosen, and only then', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], [roleRow()]);

      // Neither pseudo-entry is a resource, so there is nothing to edit or remove while one is chosen.
      expect(hasGroupCommand('edit')).withContext('nothing to edit').toBeFalse();

      chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()]));
      fixture.detectChanges();

      expect(hasGroupCommand('edit')).withContext('the group can be edited').toBeTrue();
    });

    it('withholds the group removal while the group still holds roles', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([roleRow()]));
      fixture.detectChanges();

      // ⚠ THE CLIENT'S OWN PRECONDITION: a group holding roles cannot be removed, so the affordance is
      // withheld rather than offered and refused. The server's conflict is the backstop, not the gate.
      expect(hasGroupCommand('remove')).withContext('withheld while occupied').toBeFalse();
    });

    it('offers the group removal once the group is empty', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      chooseFilter('Paid Services');
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
    it('abandons a superseded narrowing, so its answer cannot paint over the newest one', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      chooseFilter('Paid Services');

      const superseded = expectRequest('GET', ROLES_URL, 'the grouped read');

      expect(superseded.request.params.get('roleGroupId')).toBe('4');

      // The narrowing changes again before the first answer arrives.
      chooseFilter(ALL_ROLES_OPTION_LABEL);

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
    it('leaves an outstanding narrowing to the store, which abandons it on its own teardown', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      chooseFilter('Paid Services');

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
    function arriveWithGroup(roles: readonly RoleListItem[] = [roleRow()]): void {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services', description: 'Fee-bearing' })], roles);
      chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf(roles));
      fixture.detectChanges();
    }

    it('opens with the chosen group own values', () => {
      arriveWithGroup();

      pressGroupCommand('edit');

      expect(field<HTMLInputElement>(GROUP_NAME_CONTROL_ID).value).toBe('Paid Services');
      expect(field<HTMLTextAreaElement>(GROUP_DESCRIPTION_CONTROL_ID).value).toBe('Fee-bearing');
      // ⚠ THE EDITOR EDITS THE GROUP THE FILTER NAMES. Opening it against anything else — the first
      // group, say — would let a person edit one group while looking at another's roles.
      expect(httpMock.match(() => true)).withContext('opening reads nothing').toHaveSize(0);
    });

    it('puts the trimmed values and answers 200 with the stored group', () => {
      arriveWithGroup();

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

    it('sends an emptied description as null rather than as blank text', () => {
      arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_DESCRIPTION_CONTROL_ID, '   ');

      press(GROUP_EDITOR_SUBMIT_LABEL);

      const call = expectRequest('PUT', roleGroupUrl(4));

      // A description of nothing but space is an absence, and the schema spells an absence as null here.
      expect((call.request.body as { description: string | null }).description).toBeNull();

      call.flush(envelope(roleGroup(4)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
    });

    it('refuses an emptied name in the legacy wording and sends nothing', () => {
      arriveWithGroup();

      pressGroupCommand('edit');
      type(GROUP_NAME_CONTROL_ID, '');

      press(GROUP_EDITOR_SUBMIT_LABEL);

      // The legacy sentence carried a leading line break, which is stripped rather than rendered as
      // markup — the wording survives, the markup does not.
      expect(textOf('.form-field__error')).toContain(GROUP_NAME_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(query(`#${GROUP_NAME_CONTROL_ID}`)).withContext('the editor stays open').not.toBeNull();
    });

    it('closes without sending anything when abandoned', () => {
      arriveWithGroup();

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

    it('reports a duplicate group name at 409 as an error', () => {
      arriveWithGroup();

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
      expect(notifications()).toHaveSize(1);
      expect(notifications()[0]?.severity).toBe('error');
      expect(notifications()[0]?.message).toContain('already exists');
    });

    it('reports a refused replacement as a warning at 403', () => {
      arriveWithGroup();

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
    function arriveWithEmptyGroup(): void {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      chooseFilter('Paid Services');
      expectRequest('GET', ROLES_URL).flush(pageOf([]));
      fixture.detectChanges();
    }

    it('asks first, then removes with a 204 and re-reads the whole administration', () => {
      arriveWithEmptyGroup();

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

    it('sends nothing when the confirmation is dismissed', () => {
      arriveWithEmptyGroup();

      pressGroupCommand('remove');
      pressDialogue(GROUP_EDITOR_CANCEL_LABEL);

      expect(query('.confirm-dialog')).withContext('closed again').toBeNull();
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(notifications()).toHaveSize(0);
    });

    it('reports a group still in use at 409 and re-reads both halves', () => {
      arriveWithEmptyGroup();

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

    it('reports a refused removal as a warning at 403 without re-reading', () => {
      arriveWithEmptyGroup();

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
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      arrive();

      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('names the filter with a real label pointing at the picker', () => {
      arrive();

      const target: HTMLLabelElement | undefined = queryAll<HTMLLabelElement>('label[for]').find(
        (label) => label.getAttribute('for') === FILTER_CONTROL_ID,
      );

      expect(target).withContext('the picker carries a real label').not.toBeUndefined();
    });

    it('gives each icon-only group command a discernible name and hides its image', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      chooseFilter('Paid Services');
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

    it('locks both group commands while a mutation is in flight', () => {
      arrive([roleGroup(4, { roleGroupName: 'Paid Services' })], []);
      chooseFilter('Paid Services');
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

    it('renders the absent-money and absent-period markers as empty cells', () => {
      arrive([roleGroup()], [roleRow(0, { serviceFee: null, billingPeriod: null })]);

      const cells: readonly string[] = Array.from(
        (rows()[0] as HTMLTableRowElement).querySelectorAll('td'),
      ).map((cell) => (cell.textContent ?? '').trim());

      // Two absences on the wire and one rendering, because a fee of "nothing" is not a fee of zero and
      // must not be drawn as one.
      expect(cells.filter((text) => text.length === 0).length)
        .withContext('both absences render empty')
        .toBeGreaterThanOrEqual(2);
    });
  });
});
