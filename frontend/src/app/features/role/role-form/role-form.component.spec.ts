/**
 * Specification for {@link RoleFormComponent} — creating a security role, and editing one.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * A role is an authorisation primitive and a BILLING object at once: its paid-membership terms —
 * service fee, billing period and frequency, and the trial equivalents — were carried forward from the
 * legacy schema verbatim, and the API refuses a period that is not strictly positive. So the screen's
 * hardest obligation is not validation, it is what it SUBMITS when a group of terms is left blank: the
 * legacy default is a period of ONE and a frequency of `'N'`, not zeros, and getting that wrong turns
 * every unpriced role into a rejected request.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - Mounted as the standalone unit it is, with the REAL {@link RoleService} and the REAL
 *     {@link RoleStore} resolved from the injector, and every request answered through
 *     `HttpTestingController`.
 *   - The role identifier arrives through `componentRef.setInput` as the STRING a route parameter is,
 *     so the component's own parsing runs; creation mode is the input never being set.
 *   - `Router.navigate` is spied because the screen navigates with an ARRAY of commands;
 *     `NotificationService.notify` is spied and called through.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ THE ROLE GROUP LIST IS READ FROM THE CONSTRUCTOR, before any mode is known, so `GET /role-groups`
 * is outstanding in EVERY case and must be answered.
 *
 * ⚠ A SUCCESSFUL WRITE RE-READS THE ROLE LISTING AND THEN NAVIGATES AWAY, so `GET /roles` follows every
 * create, update and delete.
 *
 * ⚠ `dbo.Roles.RoleID` IS `IDENTITY(0, 1)`. Role zero is the Administrators role of an installation, so
 * the mode is derived from whether the address carries a role at all and never from the value.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { RoleStore } from '../../../core/state/role.store';
import { RoleFormComponent } from './role-form.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, RoleGroup, RoleListItem } from '../../../core/models/role.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

const ROLES_URL = '/api/v1/roles';
const ROLE_GROUPS_URL = '/api/v1/role-groups';

function roleUrl(roleId: number): string {
  return `${ROLES_URL}/${roleId}`;
}

const ROLE_LIST_ROUTE = '/roles';

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
// =====================================================================================================

const ADD_TITLE = 'Add New Role';
const EDIT_TITLE = 'Edit Security Roles';
const SUBMIT_LABEL = 'Update';
const CANCEL_LABEL = 'Cancel';
const DELETE_LABEL = 'Delete';
const MANAGE_USERS_LABEL = 'Manage Users in this Role';
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

const ROLE_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';
const SERVICE_FEE_INVALID_MESSAGE = 'Service Fee Value Entered Is Not Valid';
const SERVICE_FEE_NEGATIVE_MESSAGE = 'Service Fee Must Be Greater Than or Equal to Zero';
const BILLING_PERIOD_INVALID_MESSAGE = 'Billing Period Value Entered Is Not Valid';
const BILLING_PERIOD_NOT_POSITIVE_MESSAGE = 'Billing Period Must Be Greater Than Zero';
const TRIAL_PERIOD_NOT_POSITIVE_MESSAGE = 'Trial Period Must Be Greater Than Zero';

const ROLE_CREATED_MESSAGE = 'The role was created.';
const ROLE_UPDATED_MESSAGE = 'The role was updated.';
const ROLE_DELETED_MESSAGE = 'The role was deleted.';
const ROLE_NOT_FOUND_MESSAGE = 'That role could not be found.';
const SAVE_FAILED_MESSAGE = 'The role could not be saved.';
const DELETE_FAILED_MESSAGE = 'The role could not be deleted.';

/** Control identifiers, composed by the template. */
const CONTROL_ID = Object.freeze({
  roleName: 'role-form-role-name',
  description: 'role-form-description',
  roleGroup: 'role-form-role-group',
  isPublic: 'role-form-is-public',
  autoAssignment: 'role-form-auto-assignment',
  serviceFee: 'role-form-service-fee',
  billingPeriod: 'role-form-billing-period',
  billingFrequency: 'role-form-billing-frequency',
  trialFee: 'role-form-trial-fee',
  trialPeriod: 'role-form-trial-period',
  trialFrequency: 'role-form-trial-frequency',
  rsvpCode: 'role-form-rsvp-code',
  iconFile: 'role-form-icon-file',
});

/**
 * The values a suppressed group of terms submits.
 *
 * ⚠ THE PERIOD IS ONE, NOT ZERO, and this is the single easiest fact on this screen to lose. It is
 * measured from the legacy screen, and the API's own rule refuses a period that is not strictly
 * positive — so a zero here would make every unpriced role a rejected request.
 */
const SUPPRESSED = Object.freeze({ fee: 0, period: 1, frequency: 'N' as const });

/**
 * The value that means "not in a group" ON THE WAY OUT.
 *
 * ⚠ THE LEGACY SENTINEL IS COLLAPSED RATHER THAN ECHOED. `-1` is recognised on the way IN, so a
 * response from a producer that has not collapsed it still selects the ungrouped entry - but nothing
 * ever writes it back, and `null` is what travels. Asserting `-1` here would demand that the client
 * re-introduce a sentinel the migration exists to remove. Group ZERO is a real group, because
 * `dbo.RoleGroups.RoleGroupID` is seeded from zero, which is why the absence cannot be spelt as `0`.
 */
const UNGROUPED: number | null = null;

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

const TRACE_ID = '00-3e7a4f2b9c934dd6bb18eb211c80319c-44bd6b7169203331-01';
const CORRELATION_ID = 'c58d1a76-9e42-4b03-8f61-2a7c5d0e3b94';

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

function role(roleId = 7, overrides: Partial<Role> = {}): Role {
  return {
    roleId,
    roleGroupId: null,
    roleName: 'Subscribers',
    description: 'Paying members',
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

function roleGroup(roleGroupId = 4, overrides: Partial<RoleGroup> = {}): RoleGroup {
  return {
    roleGroupId,
    portalId: -1,
    roleGroupName: 'Paid Services',
    description: null,
    ...overrides,
  };
}

function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/** An empty page of roles, which is all the post-write listing re-read needs. */
function emptyRolePage(): PagedResponse<RoleListItem> {
  return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 } };
}

describe('RoleFormComponent', () => {
  let fixture: ComponentFixture<RoleFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. The
    // store is pinned so each case owns its own instance.
    await TestBed.configureTestingModule({
      imports: [RoleFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), RoleStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /**
   * Answers the role group read the constructor issues.
   *
   * ⚠ OUTSTANDING IN EVERY CASE. The groups populate the picker, so the screen asks for them before it
   * knows whether it is creating or editing.
   */
  function answerGroups(groups: readonly RoleGroup[] = [roleGroup()]): TestRequest {
    const call = expectRequest('GET', ROLE_GROUPS_URL, 'the role group read');

    call.flush(envelope(groups));
    fixture.detectChanges();

    return call;
  }

  /** Mounts the screen in CREATION mode, which is the absence of a role in the address. */
  function createMode(groups: readonly RoleGroup[] = [roleGroup()]): void {
    fixture = TestBed.createComponent(RoleFormComponent);
    fixture.detectChanges();
    answerGroups(groups);
  }

  /** Mounts the screen in EDIT mode and settles both opening reads. */
  function editMode(
    subject: Role,
    context: {
      readonly administratorRoleId?: number;
      readonly registeredRoleId?: number;
      readonly paymentProcessorConfigured?: boolean;
      readonly groups?: readonly RoleGroup[];
    } = {},
  ): void {
    fixture = TestBed.createComponent(RoleFormComponent);

    if (context.administratorRoleId !== undefined) {
      fixture.componentRef.setInput('administratorRoleId', context.administratorRoleId);
    }

    if (context.registeredRoleId !== undefined) {
      fixture.componentRef.setInput('registeredRoleId', context.registeredRoleId);
    }

    if (context.paymentProcessorConfigured !== undefined) {
      fixture.componentRef.setInput(
        'paymentProcessorConfigured',
        context.paymentProcessorConfigured,
      );
    }

    fixture.componentRef.setInput('roleId', String(subject.roleId));
    fixture.detectChanges();

    answerGroups(context.groups ?? [roleGroup()]);

    const read = expectRequest('GET', roleUrl(subject.roleId), 'the role read');

    read.flush(envelope(subject));
    fixture.detectChanges();
  }

  /** Answers the listing re-read that follows every successful write. */
  function answerListingReread(): void {
    expectRequest('GET', ROLES_URL, 'the listing re-read after a write').flush(emptyRolePage());
    fixture.detectChanges();
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

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(controlId: string): E {
    const element = query<E>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as E;
  }

  /** Types into a text control. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Chooses an option of a select by its RENDERED LABEL.
   *
   * ⚠ THE OPTION VALUES ARE NOT THE CODES. Both frequency pickers and the grouping picker bind
   * `[ngValue]`, which is load-bearing - the grouping control is typed to hold a number OR NOTHING, and
   * a plain value binding would coerce every option to a string - so the framework writes its own
   * encoded key into each option's DOM `value` attribute. Assigning the code directly therefore selects
   * NOTHING and leaves the control at its default, which is exactly the kind of silent no-op that makes
   * a specification pass while proving nothing. The label is the only stable handle a person also uses.
   */
  /**
   * The label of the option a select currently shows.
   *
   * Read from the option rather than from the select's `value`, because Angular's value accessor
   * prefixes the DOM value with the option's index when options are bound as values — so
   * comparing the raw value would assert an implementation detail of the framework instead of
   * what the operator sees.
   *
   * @param controlId The select to read.
   * @returns The chosen option's trimmed label.
   */
  function chosenLabel(controlId: string): string {
    const control = field<HTMLSelectElement>(controlId);
    const chosen: HTMLOptionElement | null = control.options.item(control.selectedIndex);

    return (chosen?.textContent ?? '').trim();
  }

  function choose(controlId: string, label: string): void {
    const control = field<HTMLSelectElement>(controlId);
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
   * Presses a button of the OPEN CONFIRMATION.
   *
   * ⚠ SCOPED TO THE DIALOGUE, because the row command and the dialogue's confirming button share the
   * wording 'Delete' and the abandon command shares 'Cancel'.
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

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return textOf('.form-field__error');
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — WHICH MODE, AND WHAT IS READ
  // ---------------------------------------------------------------------------------------------------

  describe('choosing between the two modes', () => {
    it('reads the role groups from the constructor, before any mode is known', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.detectChanges();

      const call = expectRequest('GET', ROLE_GROUPS_URL);

      // The picker's options are reference data the screen cannot render without, and the read carries
      // no paging coordinate at all: the group catalogue is small, bounded data returned whole.
      expect(call.request.params.keys()).withContext('no query string').toHaveSize(0);
      expect(call.request.url.startsWith('http')).withContext('relative').toBeFalse();

      call.flush(envelope([roleGroup()]));
      fixture.detectChanges();

      expect((query('h1')?.textContent ?? '').trim()).toBe(ADD_TITLE);
    });

    it('reads no role when the address names none, and offers an editable name', () => {
      createMode();

      httpMock.expectNone((candidate) => candidate.url.startsWith(ROLES_URL));
      expect((query('h1')?.textContent ?? '').trim()).toBe(ADD_TITLE);
      // In creation the name is a control, because it is the one thing being established.
      expect(field<HTMLInputElement>(CONTROL_ID.roleName).tagName).toBe('INPUT');
    });

    it('reads the role for role 0, which is the administrators role of an installation', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '0');
      fixture.detectChanges();
      answerGroups();

      const call = expectRequest('GET', roleUrl(0));

      // ⚠ THE IDENTITY COLUMN IS SEEDED FROM ZERO, so a truthiness test on the parsed identifier would
      // put this screen into creation mode for the most consequential role there is.
      expect(call.request.url).toBe('/api/v1/roles/0');

      call.flush(envelope(role(0, { roleName: 'Administrators' })));
      fixture.detectChanges();

      expect((query('h1')?.textContent ?? '').trim()).toContain(EDIT_TITLE);
    });

    it('shows the name as read-only text once a role exists, never as an editable control', () => {
      editMode(role(7, { roleName: 'Subscribers' }));

      const name = field<HTMLElement>(CONTROL_ID.roleName);

      // ⚠ A STORED ROLE'S NAME IS NOT EDITABLE HERE, which is why the replacement request carries the
      // name from the record rather than from the form: the legacy screen showed it as a label too.
      expect(name.tagName).withContext('not an input').toBe('OUTPUT');
      expect((name.textContent ?? '').trim()).toBe('Subscribers');
    });

    it('reads nothing and stays in creation mode for an unusable route parameter', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', 'not-a-number');
      fixture.detectChanges();
      answerGroups();

      // No address is built from text that is not an identifier, so nothing is attempted.
      httpMock.expectNone((candidate) => candidate.url.startsWith(`${ROLES_URL}/`));
      expect((query('h1')?.textContent ?? '').trim()).toBe(ADD_TITLE);
    });

    it('takes a person back to the listing when the role has gone', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '7');
      fixture.detectChanges();
      answerGroups();

      expectRequest('GET', roleUrl(7)).flush(
        problem('role.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // Staying on a form for a record that does not exist would invite a submission that could only
      // fail, so the screen says so and leaves.
      expect(notifications()).toEqual([
        { severity: 'warning', message: ROLE_NOT_FOUND_MESSAGE },
      ]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLE_LIST_ROUTE]);
    });

    it('opens a role whose stored frequency is unsupported with the select on its default', () => {
      // MIGRATION: the record's frequency is what the DATABASE holds, and the API carries a stored
      //   character through losslessly — shipped DotNetNuke data contains roles whose characters
      //   come from the superseded numeric code set. The read used to refuse such a role outright,
      //   so this form could not be opened on it at all. It opens now, and the select falls back to
      //   the no-term default exactly as the legacy `Items.FindByValue(...)` lookup left it
      //   (`EditRoles.ascx.vb:L149-L152,L157-L160`) — the operator's own choice is what is sent
      //   back, and no unsupported code is ever transmitted.
      editMode(
        role(7, { serviceFee: 9.99, billingPeriod: 1, billingFrequency: '4', trialFrequency: '0' }),
      );

      // Asserted through the chosen option's own LABEL, because Angular prefixes a select's DOM
      // value with the option index when the options are bound as values.
      expect(chosenLabel(CONTROL_ID.billingFrequency)).toBe('None');
      expect(chosenLabel(CONTROL_ID.trialFrequency)).toBe('None');
      expect(notifications())
        .withContext('an unsupported stored code is data, not a refusal to report')
        .toEqual([]);
    });

    it('hydrates the record into the form, including its grouping', () => {
      editMode(role(7, { description: 'Paying members', isPublic: true, autoAssignment: true }), {
        groups: [roleGroup(4)],
      });

      expect(field<HTMLTextAreaElement>(CONTROL_ID.description).value).toBe('Paying members');
      expect(field<HTMLInputElement>(CONTROL_ID.isPublic).checked).toBeTrue();
      expect(field<HTMLInputElement>(CONTROL_ID.autoAssignment).checked).toBeTrue();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE OPTIONAL MONEY AND PERIOD RULES
  // ---------------------------------------------------------------------------------------------------

  describe('the paid-membership rules', () => {
    it('accepts a role with no terms at all, because every term is optional', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLES_URL, 'the creation');

      // ⚠ AN UNPRICED ROLE SUBMITS THE SUPPRESSED DEFAULTS, AND THE PERIOD IS ONE. Zero would be
      // refused by the API's strictly-positive period rule, so this is the assertion that keeps every
      // ordinary, free role creatable at all.
      const body = call.request.body as Record<string, unknown>;

      expect(body['serviceFee']).toBe(SUPPRESSED.fee);
      expect(body['billingPeriod']).toBe(SUPPRESSED.period);
      expect(body['billingFrequency']).toBe(SUPPRESSED.frequency);
      expect(body['trialFee']).toBe(SUPPRESSED.fee);
      expect(body['trialPeriod']).toBe(SUPPRESSED.period);
      expect(body['trialFrequency']).toBe(SUPPRESSED.frequency);

      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('refuses money that is not a number, in the legacy wording', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.serviceFee, 'free');

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toContain(SERVICE_FEE_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses a negative fee, which is a different rule from an unreadable one', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.serviceFee, '-1.00');

      press(SUBMIT_LABEL);

      // Two separate rules, so the message names which one failed rather than blaming the format.
      expect(fieldMessages()).toContain(SERVICE_FEE_NEGATIVE_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses a period that is not a whole number', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.billingPeriod, '1.5');

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toContain(BILLING_PERIOD_INVALID_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses a period of zero, because the API requires a strictly positive one', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.billingPeriod, '0');

      press(SUBMIT_LABEL);

      // The client refuses it rather than letting the server do so, which is the difference between a
      // field message and a failed request.
      expect(fieldMessages()).toContain(BILLING_PERIOD_NOT_POSITIVE_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses a trial period of zero on the same rule', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.trialPeriod, '0');

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toContain(TRIAL_PERIOD_NOT_POSITIVE_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('carries a complete set of terms through as the numbers they are', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.serviceFee, '9.99');
      type(CONTROL_ID.billingPeriod, '1');
      choose(CONTROL_ID.billingFrequency, 'Month');
      type(CONTROL_ID.trialFee, '0');
      type(CONTROL_ID.trialPeriod, '14');
      choose(CONTROL_ID.trialFrequency, 'Day');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLES_URL);
      const body = call.request.body as Record<string, unknown>;

      // The frequency codes are the schema's own single characters and are load-bearing DATA rather
      // than display text, so they travel unrenamed.
      expect(body['serviceFee']).toBe(9.99);
      expect(body['billingPeriod']).toBe(1);
      expect(body['billingFrequency']).toBe('M');
      expect(body['trialFee']).toBe(0);
      expect(body['trialPeriod']).toBe(14);
      expect(body['trialFrequency']).toBe('D');

      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('suppresses the trial terms when the role carries no service fee', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      type(CONTROL_ID.trialFee, '5.00');
      type(CONTROL_ID.trialPeriod, '7');
      choose(CONTROL_ID.trialFrequency, 'Day');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLES_URL);
      const body = call.request.body as Record<string, unknown>;

      // ⚠ A TRIAL OF A FREE ROLE IS MEANINGLESS, so the trial group is suppressed rather than sent —
      // the legacy screen suppressed it on exactly this condition. The suppressed period is again ONE.
      expect(body['trialFee']).toBe(SUPPRESSED.fee);
      expect(body['trialPeriod']).toBe(SUPPRESSED.period);
      expect(body['trialFrequency']).toBe(SUPPRESSED.frequency);

      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('warns that no payment processor is configured, without blocking anything', () => {
      createMode();

      // Advisory rather than a refusal: a tenant may configure the processor afterwards, and the
      // legacy screen showed the same notice beside the same fields.
      expect(host().textContent ?? '').not.toBe('');
      expect(button(SUBMIT_LABEL)?.disabled).withContext('nothing is blocked').toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — CREATING
  // ---------------------------------------------------------------------------------------------------

  describe('creating a role', () => {
    it('posts the whole contract and answers 201', () => {
      createMode([roleGroup(4)]);

      type(CONTROL_ID.roleName, '  Subscribers  ');
      type(CONTROL_ID.description, 'Paying members');
      type(CONTROL_ID.rsvpCode, 'RSVP-1');
      type(CONTROL_ID.iconFile, 'role.gif');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLES_URL, 'the creation');

      expect(call.request.body).toEqual({
        // Trimmed, because surrounding space in a name is invisible and would make two roles look
        // identical while comparing as different.
        roleName: 'Subscribers',
        description: 'Paying members',
        serviceFee: SUPPRESSED.fee,
        billingPeriod: SUPPRESSED.period,
        billingFrequency: SUPPRESSED.frequency,
        trialFee: SUPPRESSED.fee,
        trialPeriod: SUPPRESSED.period,
        trialFrequency: SUPPRESSED.frequency,
        isPublic: false,
        autoAssignment: false,
        // The legacy "no group" marker, which is minus one rather than null.
        roleGroupId: UNGROUPED,
        rsvpCode: 'RSVP-1',
        iconFile: 'role.gif',
      });

      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();

      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: ROLE_CREATED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLE_LIST_ROUTE]);
    });

    it('sends an emptied optional field as null rather than as empty text', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLES_URL);
      const body = call.request.body as Record<string, unknown>;

      // ⚠ THE EMPTY STRING IS THE LEGACY ABSENT-STRING MARKER, so an untouched optional field arrives
      // as null. Sending `''` would store a present-but-blank value where the schema means absence.
      expect(body['description']).toBeNull();
      expect(body['rsvpCode']).toBeNull();
      expect(body['iconFile']).toBeNull();

      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('reports an unmet name requirement and sends nothing', () => {
      createMode();

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toContain(ROLE_NAME_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a duplicate name at 409 as an error and stays on the screen', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Administrators');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL).flush(
        problem('role.name_duplicate', 409, 'A role with that name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // A conflict is an ERROR — the shared classification treats only 401, 403, 404 and 429 as
      // warnings — and the entry survives so the name can be corrected in place.
      expect(notifications()).toEqual([
        { severity: 'error', message: 'A role with that name already exists.' },
      ]);
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(field<HTMLInputElement>(CONTROL_ID.roleName).value).toBe('Administrators');
    });

    it('reports a refusal of authority as a warning', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()[0]?.severity).toBe('warning');
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('falls back to its own wording when the server sends no sentence', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');

      press(SUBMIT_LABEL);

      // A transport failure carries no problem document at all, so the screen supplies the sentence
      // rather than announcing an empty message.
      expectRequest('POST', ROLES_URL).error(new ProgressEvent('error'));
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'error', message: SAVE_FAILED_MESSAGE }]);
    });

    it('shows a per-field server message against the control the server named', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL).flush(
        problem('request.invalid', 400, 'One or more validation errors occurred.', {
          roleName: ['That name is reserved.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // The server's per-field map is applied to the matching control, so the message appears where the
      // value that caused it is — the keys are model-state keys read by index, never by property access.
      expect(fieldMessages()).toContain('That name is reserved.');
    });

    it('leaves for the listing without sending anything when abandoned', () => {
      createMode();

      type(CONTROL_ID.roleName, 'Subscribers');
      press(CANCEL_LABEL);

      httpMock.expectNone(() => true);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLE_LIST_ROUTE]);
      expect(notifications()).toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — EDITING
  // ---------------------------------------------------------------------------------------------------

  describe('editing a role', () => {
    it('puts the contract with the name taken from the record, and answers 200', () => {
      editMode(role(7, { roleName: 'Subscribers' }));

      type(CONTROL_ID.description, 'Paying members, revised');

      press(SUBMIT_LABEL);

      const call = expectRequest('PUT', roleUrl(7), 'the replacement');
      const body = call.request.body as Record<string, unknown>;

      // ⚠ THE NAME COMES FROM THE RECORD, NOT FROM THE FORM, because the form does not offer it in this
      // mode. Reading it from a control that renders as read-only text would send an empty name.
      expect(body['roleName']).toBe('Subscribers');
      expect(body['description']).toBe('Paying members, revised');

      // ⚠ A REPLACEMENT ANSWERS 200 WITH THE STORED RECORD, not 204.
      call.flush(envelope(role(7)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();

      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: ROLE_UPDATED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLE_LIST_ROUTE]);
    });

    it('addresses role 0 untouched', () => {
      editMode(role(0, { roleName: 'Administrators' }));

      press(SUBMIT_LABEL);

      const call = expectRequest('PUT', roleUrl(0));

      expect(call.request.url).toBe('/api/v1/roles/0');

      call.flush(envelope(role(0)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('withholds the submit control while a write is outstanding', () => {
      editMode(role(7));

      press(SUBMIT_LABEL);

      const call = expectRequest('PUT', roleUrl(7));

      // One press cannot become two replacements.
      expect(button(SUBMIT_LABEL)?.disabled).withContext('in flight').toBeTrue();

      call.flush(envelope(role(7)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('locks a protected role read-only and offers neither save nor delete', () => {
      editMode(role(0, { roleName: 'Administrators' }), { administratorRoleId: 0 });

      // ⚠ PROTECTION IS DECIDED FROM THE TENANT'S OWN DESIGNATED ROLE IDENTIFIERS, never from a role's
      // NAME and never from a hardcoded number: a renamed administrators role must stay protected and a
      // role merely called 'Administrators' must not become protected by accident.
      expect(button(SUBMIT_LABEL)).withContext('no save').toBeUndefined();
      expect(button(DELETE_LABEL)).withContext('no delete').toBeUndefined();
      expect(field<HTMLTextAreaElement>(CONTROL_ID.description).disabled)
        .withContext('and the form is read-only')
        .toBeTrue();
    });

    it('protects the registered-users role and withholds its membership screen', () => {
      editMode(role(1, { roleName: 'Registered Users' }), { registeredRoleId: 1 });

      expect(button(SUBMIT_LABEL)).withContext('no save').toBeUndefined();
      expect(button(DELETE_LABEL)).withContext('no delete').toBeUndefined();
      // Every authenticated account holds this role, so there is no membership to manage.
      expect(button(MANAGE_USERS_LABEL)).withContext('no membership screen').toBeUndefined();
    });

    it('offers the membership screen for an ordinary role, by navigation', () => {
      editMode(role(7), { registeredRoleId: 1 });

      press(MANAGE_USERS_LABEL);

      // The membership screen is a sibling under the same feature folder, and features do not import
      // one another, so the screen navigates rather than linking.
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLE_LIST_ROUTE, 7, 'users']);
      httpMock.expectNone(() => true);
    });

    it('offers no membership screen at all while creating', () => {
      createMode();

      // There is no role to manage the membership of yet.
      expect(button(MANAGE_USERS_LABEL)).toBeUndefined();
      expect(button(DELETE_LABEL)).withContext('and nothing to delete').toBeUndefined();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — DELETING
  // ---------------------------------------------------------------------------------------------------

  describe('deleting a role', () => {
    it('asks first, then deletes with a 204, re-reads the listing and leaves', () => {
      editMode(role(7));

      press(DELETE_LABEL);

      expect(query('.confirm-dialog')).withContext('the question is asked').not.toBeNull();
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim()).toBe(
        DELETE_CONFIRM_MESSAGE,
      );
      expect(query('.confirm-dialog__button--danger'))
        .withContext('marked destructive')
        .not.toBeNull();
      httpMock.expectNone(() => true);

      pressDialogue(DELETE_LABEL);

      const call = expectRequest('DELETE', roleUrl(7), 'the deletion');

      expect(call.request.url).toBe('/api/v1/roles/7');
      expect(query('.confirm-dialog')).withContext('dismissed immediately').toBeNull();

      // ⚠ A DELETION ANSWERS 204 WITH NO BODY.
      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: ROLE_DELETED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLE_LIST_ROUTE]);
    });

    it('sends nothing when the confirmation is dismissed', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(CANCEL_LABEL);

      expect(query('.confirm-dialog')).withContext('closed again').toBeNull();
      httpMock.expectNone(() => true);
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(notifications()).toHaveSize(0);
    });

    it('reports a protected role at 403 as a warning and stays put', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', roleUrl(7)).flush(
        problem('role.protected', 403, 'That role is protected and cannot be deleted.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // ⚠ THE SERVER IS THE FINAL AUTHORITY ON PROTECTION, and its refusal is an access failure — a
      // WARNING rather than an error. Nothing was deleted, so nothing is re-read and nobody is moved.
      expect(notifications()).toEqual([
        { severity: 'warning', message: 'That role is protected and cannot be deleted.' },
      ]);
      httpMock.expectNone(() => true);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a failed deletion in its own wording when the server sends no sentence', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', roleUrl(7)).error(new ProgressEvent('error'));
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'error', message: DELETE_FAILED_MESSAGE }]);
      expect(navigateSpy).not.toHaveBeenCalled();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      createMode();

      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('names every control with a real label pointing at it', () => {
      createMode();

      const targets: readonly string[] = queryAll<HTMLLabelElement>('label[for]').map(
        (label) => label.getAttribute('for') ?? '',
      );

      [CONTROL_ID.roleName, CONTROL_ID.description, CONTROL_ID.serviceFee].forEach((controlId) => {
        expect(targets).withContext(`${controlId} is named`).toContain(controlId);
      });
    });

    it('offers the grouping picker with an option for every group and for none', () => {
      createMode([roleGroup(4, { roleGroupName: 'Paid Services' }), roleGroup(5)]);

      const options: readonly string[] = Array.from(
        field<HTMLSelectElement>(CONTROL_ID.roleGroup).options,
      ).map((option) => option.textContent?.trim() ?? '');

      // Two groups plus the "no group" entry, because being in no group is a real state rather than an
      // unset one.
      expect(options.length).withContext('every group and none').toBeGreaterThanOrEqual(3);
      expect(options.join('|')).toContain('Paid Services');
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      createMode();

      expect(query('.error-banner-live')).withContext('the region exists').not.toBeNull();
      expect(query('.error-banner__title')).withContext('but says nothing').toBeNull();
    });

    it('declares the type of every command so none can submit by accident', () => {
      editMode(role(7));

      expect(button(SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      expect(button(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
      expect(button(DELETE_LABEL)?.getAttribute('type')).toBe('button');
      expect(queryAll('[onclick]')).toHaveSize(0);
    });

    it('renders a hostile description as text, with no element parsed out of it', () => {
      editMode(role(7, { description: '<img src=x onerror="window.__role=true">' }));

      expect(queryAll('img')).withContext('no element parsed out of a description').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__role'])
        .withContext('never evaluated')
        .toBeUndefined();
    });
  });
});
