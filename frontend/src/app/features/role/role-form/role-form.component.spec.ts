import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { ListReturnStore } from '../../../core/state/list-return.store';
import { RoleStore } from '../../../core/state/role.store';
import { RoleFormComponent } from './role-form.component';

/**
 * The tenant the doubled identity reports. `Portals.PortalID` is `IDENTITY(-1, 1)`, so the first tenant a
 * schema creates carries -1 — which is also the legacy absent-integer marker.
 */
const TENANT_ID = -1;

import type { WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { Params } from '@angular/router';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  BillingFrequency,
  CreateRoleRequest,
  Role,
  RoleGroup,
  UpdateRoleRequest,
} from '../../../core/models/role.model';

// =====================================================================================================
// ADDRESSES — ROOT-RELATIVE, ALWAYS
// =====================================================================================================

/** `GET` the collection, `POST` to create. */
const ROLES_URL = '/api/v1/roles';

/** Unpaged: role groups are deliberately never wrapped in a paged envelope. */
const ROLE_GROUPS_URL = '/api/v1/role-groups';

/** The paging keys the workspace's parameter builder emits, asserted ABSENT on the group read. */
const PAGING_KEYS: readonly string[] = ['pageIndex', 'pageSize', 'sortBy', 'sortDir', 'query'];

function roleUrl(roleId: number): string {
  return `${ROLES_URL}/${roleId}`;
}

const ROLE_LIST_ROUTE = '/roles';

// THE WORDING THIS SCREEN PUBLISHES
// Every validation sentence below is the `.Text` VALUE from
// `Website/admin/Security/App_LocalResources/EditRoles.ascx.resx`, with its leading break markup stripped —
// never the inline `ErrorMessage` attribute from `editroles.ascx`.

const ADD_TITLE = 'Add New Role';
const EDIT_TITLE = 'Edit Security Roles';

/** The sentence shown when the address names no readable role. */
const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a role that can be read. Return to the role list and try again.';

// The primary action's caption, ONE COMMAND whose wording follows the mode. `EditRoles.ascx` declared a
// single `cmdUpdate` for both, so "Update" is the documented legacy wording for EDIT and there is no legacy
// wording for create at all - and applying "Update" to a role that does not exist yet is what left the four
// create screens reading as three vocabularies.
const SUBMIT_LABEL = 'Update';
const CREATE_SUBMIT_LABEL = 'Create Role';
const CANCEL_LABEL = 'Cancel';
const DELETE_LABEL = 'Delete';
const MANAGE_USERS_LABEL = 'Manage Users in this Role';

const STALE_MANAGE_LABEL = 'Manage Users';

const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `valRoleName.Text`. */
const ROLE_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/** `valServiceFee1.Text` — the `Type="Currency" Operator="DataTypeCheck"` refusal. */
const SERVICE_FEE_INVALID_MESSAGE = 'Service Fee Value Entered Is Not Valid';

/** `valServiceFee2.Text` — resource and inline agree here. */
const SERVICE_FEE_NEGATIVE_MESSAGE = 'Service Fee Must Be Greater Than or Equal to Zero';

/** `valBillingPeriod1.Text` — the `Type="Integer" Operator="DataTypeCheck"` refusal. */
const BILLING_PERIOD_INVALID_MESSAGE = 'Billing Period Value Entered Is Not Valid';

/** `valBillingPeriod2.Text` — ⚠ CORRECTED against a stale inline twin; see AREA 5. */
const BILLING_PERIOD_NOT_POSITIVE_MESSAGE = 'Billing Period Must Be Greater Than Zero';

/** The stale inline `ErrorMessage` for the billing period, which must NEVER be rendered. */
const STALE_BILLING_PERIOD_MESSAGE = 'Billing Period Must Be Greater Than or Equal to Zero';

/** `valTrialFee1.Text`. */
const TRIAL_FEE_INVALID_MESSAGE = 'Trial Fee Value Entered Is Not Valid';

/** `valTrialFee2.Text` — ⚠ CORRECTED against a stale inline twin; see AREA 5. */
const TRIAL_FEE_NEGATIVE_MESSAGE = 'Trial Fee Must Be Greater Than or Equal to Zero';

/** The stale inline `ErrorMessage` for the trial fee, which must NEVER be rendered. */
const STALE_TRIAL_FEE_MESSAGE = 'Trial Fee Must Be Greater Than Zero';

/** `valTrialPeriod1.Text`. */
const TRIAL_PERIOD_INVALID_MESSAGE = 'Trial Period Value Entered Is Not Valid';

/** `valTrialPeriod2.Text` — resource and inline agree here. */
const TRIAL_PERIOD_NOT_POSITIVE_MESSAGE = 'Trial Period Must Be Greater Than Zero';

/** `DuplicateRole.Text`, verbatim, raised by the legacy at `RedError`. */
const DUPLICATE_ROLE_MESSAGE = 'A role with the same name already exists. The role was not added.';

/** The component's own length wording, formed from the rule that failed. */
function lengthMessage(limit: number): string {
  return `Enter at most ${limit} characters.`;
}

const ROLE_CREATED_MESSAGE = 'The role was created.';
const ROLE_UPDATED_MESSAGE = 'The role was updated.';
const ROLE_DELETED_MESSAGE = 'The role was deleted.';
/**
 * ⚠ THE SHARED SHAPE, NOT THIS SCREEN'S OWN SENTENCE. Each of the four detail screens worded a missing
 * record differently; one builder now words all three of the app-authored ones, and the account screen keeps
 * its legacy `NoUser.Text` wording as the documented exception.
 */
const ROLE_NOT_FOUND_MESSAGE = 'The role could not be found. It may have been removed.';
const SAVE_FAILED_MESSAGE = 'The role could not be saved.';
const DELETE_FAILED_MESSAGE = 'The role could not be deleted.';

const GLOBAL_ROLES_LABEL = '< Global Roles >';

/**
 * The opening words of `ModuleHelp.Text`, asserted ABSENT. No shared component renders module help, so
 * the entry is read for the record and never rendered. Its first heading is the handle used to prove
 * that.
 */
const MODULE_HELP_OPENING = 'About Edit Security Roles';

// =====================================================================================================
// THE PERSISTED FREQUENCY VOCABULARY
// =====================================================================================================

/** One frequency code and the caption the select shows beside it. */
interface FrequencyChoice {
  readonly code: BillingFrequency;
  readonly label: string;
}

/** The six codes, in the order the legacy `CodeFrequency` lookup seeded them. */
const FREQUENCIES: readonly FrequencyChoice[] = Object.freeze<readonly FrequencyChoice[]>([
  { code: 'N', label: 'None' },
  { code: 'O', label: 'One Time' },
  { code: 'D', label: 'Day' },
  { code: 'W', label: 'Week' },
  { code: 'M', label: 'Month' },
  { code: 'Y', label: 'Year' },
]);

/** The caption of the code that means "no recurring term". */
const NO_FREQUENCY_LABEL = 'None';

/** The code that means "no recurring term". */
const NO_FREQUENCY: BillingFrequency = 'N';

// =====================================================================================================
// THE VALUES A SUPPRESSED GROUP OF TERMS SUBMITS
// =====================================================================================================

/**
 * ⚠ THE PERIOD IS ONE, NOT ZERO. This is the single easiest fact on this screen to lose. Measured at
 * `EditRoles.ascx.vb:L212-L214` and `:L222-L224`, which initialise `sglServiceFee = 0`, `intBillingPeriod
 * = 1` and `strBillingFrequency = "N"` before either gate is tested.
 */
const SUPPRESSED_FEE = 0;
const SUPPRESSED_PERIOD = 1;
const SUPPRESSED_FREQUENCY: BillingFrequency = NO_FREQUENCY;

const UNGROUPED: number | null = null;

/** The legacy ungrouped marker, which a producer that has not collapsed it may still send. */
const LEGACY_UNGROUPED = -1;

/**
 * The list screen's own filter sentinel, asserted NEVER to appear on this form. `Roles.ascx.vb:L112` adds
 * an `< All Roles >` entry with the value `-2` to the LIST screen's narrowing picker.
 */
const LIST_FILTER_SENTINEL = -2;

/** `Null.NullInteger`, which a period column uses to mean "absent". */
const ABSENT_PERIOD = -1;

/** `Null.NullSingle`, which a money column uses to mean "absent". */
const ABSENT_MONEY = -3.4028235e38;

// =====================================================================================================
// CONTROL IDENTIFIERS, COMPOSED BY THE TEMPLATE
// =====================================================================================================

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

/** The four optional money and period controls, which AREAS 1 to 3 sweep as a set. */
const NUMERIC_CONTROLS: readonly string[] = Object.freeze([
  CONTROL_ID.serviceFee,
  CONTROL_ID.billingPeriod,
  CONTROL_ID.trialFee,
  CONTROL_ID.trialPeriod,
]);

/** The two `Type="Currency"` controls, which admit two decimal places. */
const CURRENCY_CONTROLS: readonly string[] = Object.freeze([
  CONTROL_ID.serviceFee,
  CONTROL_ID.trialFee,
]);

/** The two `Type="Integer"` controls, which admit no fractional part at all. */
const INTEGER_CONTROLS: readonly string[] = Object.freeze([
  CONTROL_ID.billingPeriod,
  CONTROL_ID.trialPeriod,
]);

/** One numeric control paired with the data-type sentence its own resource entry holds. */
interface NumericField {
  readonly controlId: string;
  readonly dataTypeMessage: string;
  readonly comparisonMessage: string;
}

const NUMERIC_FIELDS: readonly NumericField[] = Object.freeze<readonly NumericField[]>([
  {
    controlId: CONTROL_ID.serviceFee,
    dataTypeMessage: SERVICE_FEE_INVALID_MESSAGE,
    comparisonMessage: SERVICE_FEE_NEGATIVE_MESSAGE,
  },
  {
    controlId: CONTROL_ID.billingPeriod,
    dataTypeMessage: BILLING_PERIOD_INVALID_MESSAGE,
    comparisonMessage: BILLING_PERIOD_NOT_POSITIVE_MESSAGE,
  },
  {
    controlId: CONTROL_ID.trialFee,
    dataTypeMessage: TRIAL_FEE_INVALID_MESSAGE,
    comparisonMessage: TRIAL_FEE_NEGATIVE_MESSAGE,
  },
  {
    controlId: CONTROL_ID.trialPeriod,
    dataTypeMessage: TRIAL_PERIOD_INVALID_MESSAGE,
    comparisonMessage: TRIAL_PERIOD_NOT_POSITIVE_MESSAGE,
  },
]);

/**
 * The data-type sentence declared for a control, looked up from the table above. A lookup rather than a
 * second literal, so a message can never be asserted against a string this file alone believes in.
 *
 * @param controlId The rendered control's id.
 * @returns The control's own data-type wording.
 */
function dataTypeMessageFor(controlId: string): string {
  const field = NUMERIC_FIELDS.find((candidate: NumericField) => candidate.controlId === controlId);
  if (field === undefined) {
    throw new Error(`No numeric field is declared for "${controlId}".`);
  }

  return field.dataTypeMessage;
}

/**
 * The comparison sentence declared for a control, looked up from the same table.
 *
 * @param controlId The rendered control's id.
 * @returns The control's own comparison wording.
 */
function comparisonMessageFor(controlId: string): string {
  const field = NUMERIC_FIELDS.find((candidate: NumericField) => candidate.controlId === controlId);
  if (field === undefined) {
    throw new Error(`No numeric field is declared for "${controlId}".`);
  }

  return field.comparisonMessage;
}

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = Object.freeze({
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
});

/** A W3C trace-context value, FIXED so that no case depends on a clock. */
const TRACE_ID = '00-3e7a4f2b9c934dd6bb18eb211c80319c-44bd6b7169203331-01';

/** The support reference the server echoes, FIXED for the same reason. */
const CORRELATION_ID = 'c58d1a76-9e42-4b03-8f61-2a7c5d0e3b94';

/** How the banner presents whichever identifier it found. */
const REFERENCE_PREFIX = 'If you report this, quote reference';

/** Options for a refusal document, so a case can withhold exactly the members it means to. */
interface ProblemOptions {
  readonly detail?: string;
  readonly title?: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
  readonly traceId?: string;
  readonly correlationId?: string;
}

/**
 * Builds an RFC 7807 refusal document. Members are omitted rather than nulled when a case withholds them,
 * which is what the framework's own problem-details type does: each of its five standard members carries
 * a per-member null-omission condition that overrides the collection-wide policy.
 *
 * @param status The HTTP status the server answered with.
 * @param code The application failure code, which forms the problem type.
 * @param options Which optional members to include.
 * @returns The document to flush as the response body.
 */
function problem(status: number, code: string, options: ProblemOptions = {}): ProblemDetails {
  const title: string = options.title ?? STATUS_TITLE[status] ?? 'Error';
  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title,
    status,
  };

  const withDetail: ProblemDetails =
    options.detail === undefined ? document : { ...document, detail: options.detail };
  const withErrors: ProblemDetails =
    options.errors === undefined ? withDetail : { ...withDetail, errors: options.errors };
  const withTrace: ProblemDetails =
    options.traceId === undefined ? withErrors : { ...withErrors, traceId: options.traceId };

  return options.correlationId === undefined
    ? withTrace
    : { ...withTrace, correlationId: options.correlationId };
}

/** A refusal that says NOTHING a person can use, so the client's own fallback governs. */
function silentProblem(status: number, code: string): ProblemDetails {
  return { type: `${FAILURE_TYPE_PREFIX}${code}`, status };
}

// FIXTURES — BUILT FROM THE IMPORTED CONTRACTS, NEVER FROM A LOCAL RE-DECLARATION

/**
 * An unpriced, ungrouped role — the shape the overwhelming majority of roles have.
 *
 * @param roleId The identifier, which may legitimately be zero.
 * @param overrides The members this case cares about.
 * @returns The role as the API reports it.
 */
function role(roleId = 7, overrides: Partial<Role> = {}): Role {
  return {
    roleId,
    roleGroupId: null,
    roleName: 'Subscribers',
    description: 'Paying members',
    billingFrequency: NO_FREQUENCY,
    serviceFee: 0,
    trialFrequency: NO_FREQUENCY,
    trialPeriod: 0,
    billingPeriod: 0,
    trialFee: 0,
    isPublic: false,
    autoAssignment: false,
    rsvpCode: null,
    iconFile: null,
    // The revision marker the API serves with every role detail. Declared BEFORE the spread so a case may
    // replace it or set it to null - the form is required to carry whatever it read into the update it
    // composes, and both of those are cases worth asserting.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * @param roleId The identifier.
 * @param overrides The members this case cares about.
 * @returns A role with real billing terms.
 */
function pricedRole(roleId = 7, overrides: Partial<Role> = {}): Role {
  return role(roleId, {
    serviceFee: 25,
    billingPeriod: 3,
    billingFrequency: 'M',
    ...overrides,
  });
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

/** The single-payload wire envelope, DECLARED LOCALLY rather than imported. */
interface WireEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

/** Wraps one payload in the single-payload envelope. */
function envelope<T>(data: T): WireEnvelope<T> {
  return { data, meta: null };
}

// ⚠ THERE IS DELIBERATELY NO PAGE FIXTURE HERE ANY MORE. This screen dispatches three writes and not one of
// them reads a collection: the listing owns listing reads, because its page, narrowing and ordering live in
// its address.

describe('RoleFormComponent', () => {
  let fixture: ComponentFixture<RoleFormComponent>;
  let httpMock: HttpTestingController;
  let administratorRole: WritableSignal<number | null>;
  let registeredRole: WritableSignal<number | null>;
  let processorConfigured: WritableSignal<boolean>;
  let tenantResolved: WritableSignal<boolean>;
  let loadCurrentPortalContext: jasmine.Spy;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ THE PROVIDER ORDER IS LOAD-BEARING AND IS VERIFIED BY LINE NUMBER IN THE COMPLETION REPORT.
    // `provideHttpClient()` MUST come first because `provideHttpClientTesting()` OVERRIDES the real backend
    // — with nothing registered first there is nothing to override, and requests would reach a real
    // transport.
    administratorRole = signal<number | null>(null);
    registeredRole = signal<number | null>(null);
    processorConfigured = signal<boolean>(false);
    tenantResolved = signal<boolean>(false);

    // The request for those facts, spied rather than served: the real portal store would add a tenant read
    // to every case in this file, and the spy records which tenant was asked for and whether it was asked
    // at all.
    loadCurrentPortalContext = jasmine.createSpy('loadCurrentPortalContext');

    await TestBed.configureTestingModule({
      imports: [RoleFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        RoleStore,
        // The identity, doubled for ONE fact: which tenant the caller belongs to. Read from the
        // caller rather than from a route, because this screen addresses a role and names no portal.
        { provide: AuthStore, useValue: { currentUser: signal({ portalId: TENANT_ID }) } },
        {
          provide: PortalStore,
          useValue: {
            administratorRoleId: administratorRole,
            registeredRoleId: registeredRole,
            paymentProcessorConfigured: processorConfigured,
            contextResolved: tenantResolved,
            loadCurrentPortalContext,
          },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    // ⚠ MANDATORY. This is what turns an unexpected or unflushed request into a failure rather than
    // silent noise, and it is what makes every "sends nothing" assertion below meaningful.
    httpMock.verify();
  });

  // HARNESS

  /** The component's host element, typed by assignment rather than by an assertion. */
  function host(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  /** Finds one node, throwing when it is absent. */
  function queryOrFail<E extends Element>(root: ParentNode, selector: string): E {
    const found: E | null = root.querySelector<E>(selector);

    if (found === null) {
      throw new Error(`Expected to find "${selector}"`);
    }

    return found;
  }

  /** Finds every matching node, as a real array. */
  function queryAll<E extends Element>(selector: string, root: ParentNode = host()): readonly E[] {
    return Array.from(root.querySelectorAll<E>(selector));
  }

  /** The trimmed text of one node. */
  function textOf(node: Element): string {
    return (node.textContent ?? '').trim();
  }

  /** The trimmed text of every matching node. */
  function textsOf(selector: string, root: ParentNode = host()): readonly string[] {
    return queryAll<Element>(selector, root).map((node) => textOf(node));
  }

  /** One text control, by the identifier the template composes. */
  function input(controlId: string): HTMLInputElement {
    return queryOrFail<HTMLInputElement>(host(), `#${controlId}`);
  }

  /** One select, by the identifier the template composes. */
  function select(controlId: string): HTMLSelectElement {
    return queryOrFail<HTMLSelectElement>(host(), `#${controlId}`);
  }

  /** The multi-line description control, which is a real text area rather than an editor. */
  function textArea(): HTMLTextAreaElement {
    return queryOrFail<HTMLTextAreaElement>(host(), `#${CONTROL_ID.description}`);
  }

  /** The labelled-field wrapper that owns one control. */
  function fieldOf(controlId: string): Element {
    const wrapper: Element | null = queryOrFail<Element>(
      host(),
      `#${controlId}`,
    ).closest('app-form-field');

    if (wrapper === null) {
      throw new Error(`Expected #${controlId} to sit inside an app-form-field`);
    }

    return wrapper;
  }

  /** Every message currently shown beneath one control's field wrapper. */
  function messagesFor(controlId: string): readonly string[] {
    return textsOf('.form-field__error', fieldOf(controlId));
  }

  /** Every message currently on screen, from every field. */
  function allMessages(): readonly string[] {
    return textsOf('.form-field__error');
  }

  /** Types into a text control, which marks it dirty exactly as a person typing would. */
  function type(controlId: string, value: string): void {
    const control: HTMLInputElement | HTMLTextAreaElement =
      controlId === CONTROL_ID.description ? textArea() : input(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Chooses an option of a select BY ITS RENDERED CAPTION. ⚠ THE DOM OPTION VALUES ARE NOT THE PERSISTED
   * CODES, and that is a framework fact rather than a contract defect.
   *
   * @param controlId The select to operate.
   * @param label The caption to choose.
   */
  function choose(controlId: string, label: string): void {
    const control: HTMLSelectElement = select(controlId);
    const option: HTMLOptionElement | undefined = Array.from(control.options).find(
      (candidate) => textOf(candidate) === label,
    );

    if (option === undefined) {
      throw new Error(`Expected #${controlId} to offer an option captioned "${label}"`);
    }

    control.value = option.value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** The caption a select currently shows. */
  function chosenLabel(controlId: string): string {
    const control: HTMLSelectElement = select(controlId);
    const chosen: HTMLOptionElement | null = control.options.item(control.selectedIndex);

    return chosen === null ? '' : textOf(chosen);
  }

  /** Every caption a select offers, in order. */
  function optionLabels(controlId: string): readonly string[] {
    return Array.from(select(controlId).options).map((option) => textOf(option));
  }

  /** The command bar's controls, scoped so that field help toggles are never mistaken for them. */
  function commands(): readonly HTMLButtonElement[] {
    return queryAll<HTMLButtonElement>('.role-form__actions button');
  }

  /** The captions of every command currently offered. */
  function commandLabels(): readonly string[] {
    return commands().map((command) => textOf(command));
  }

  /**
   * One command by its caption, or nothing when it is not offered.
   *
   * The primary action answers to EITHER of its two captions, because it is one command whose wording
   * follows the mode: a caller asking to press the submit means the submit, not a particular word. Every
   * other command is matched on its caption exactly, and the wording itself is asserted in AREA 11.
   */
  function command(label: string): HTMLButtonElement | undefined {
    const byCaption = commands().find((candidate) => textOf(candidate) === label);

    if (byCaption !== undefined || (label !== SUBMIT_LABEL && label !== CREATE_SUBMIT_LABEL)) {
      return byCaption;
    }

    return commands().find((candidate) => candidate.classList.contains('form-action--primary'));
  }

  /** Presses one command, throwing when it is not offered. */
  function press(label: string): void {
    const control: HTMLButtonElement | undefined = command(label);

    if (control === undefined) {
      throw new Error(`Expected the "${label}" command to be offered`);
    }

    control.click();
    fixture.detectChanges();
  }

  /**
   * Presses a button of the OPEN CONFIRMATION. ⚠ SCOPED TO THE DIALOGUE, because the command bar's own
   * delete control and the dialogue's confirming button share the caption `Delete`, and the abandon
   * command shares `Cancel`.
   *
   * @param label The caption, matched as a substring because the dangerous button carries a cue.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => textOf(candidate).includes(label));

    if (control === undefined) {
      throw new Error(`Expected the confirmation to offer a "${label}" button`);
    }

    control.click();
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
   * Answers the role group read the constructor issues. ⚠ OUTSTANDING IN EVERY CASE. The groups populate
   * the grouping picker, so the screen asks for them before it knows which mode it is in.
   *
   * @param groups The groups the server reports.
   * @returns The request, so a case can inspect its parameters.
   */
  function answerGroups(groups: readonly RoleGroup[] = [roleGroup()]): TestRequest {
    const call: TestRequest = expectRequest('GET', ROLE_GROUPS_URL, 'the role group read');

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

  /** The portal-scoped facts a caller may supply, and the groups to answer with. */
  interface EditContext {
    readonly administratorRoleId?: number;
    readonly registeredRoleId?: number;
    readonly paymentProcessorConfigured?: boolean;
    readonly groups?: readonly RoleGroup[];
  }

  /**
   * Mounts the screen in EDIT mode and settles both opening reads.
   *
   * @param subject The role the server will report.
   * @param context The portal-scoped facts to supply, and the groups to answer with.
   */
  function editMode(subject: Role, context: EditContext = {}): void {
    // Supplying ANY of the three marks the tenant RESOLVED, because in production the three arrive together
    // on one record and the processor warning is withheld until that record lands.
    if (
      context.administratorRoleId !== undefined ||
      context.registeredRoleId !== undefined ||
      context.paymentProcessorConfigured !== undefined
    ) {
      tenantResolved.set(true);
    }

    if (context.administratorRoleId !== undefined) {
      administratorRole.set(context.administratorRoleId);
    }

    if (context.registeredRoleId !== undefined) {
      registeredRole.set(context.registeredRoleId);
    }

    if (context.paymentProcessorConfigured !== undefined) {
      processorConfigured.set(context.paymentProcessorConfigured);
    }

    fixture = TestBed.createComponent(RoleFormComponent);
    fixture.componentRef.setInput('roleId', String(subject.roleId));
    fixture.detectChanges();

    answerGroups(context.groups ?? [roleGroup()]);

    const read: TestRequest = expectRequest('GET', roleUrl(subject.roleId), 'the role read');

    read.flush(envelope(subject));
    fixture.detectChanges();
  }

  /**
   * Asserts that a successful write does NOT re-read the listing. ⚠ THIS HELPER REFUSES SUCH A READ
   * RATHER THAN ANSWERING IT, BECAUSE THE DUPLICATE IS MEASURED. Runtime testing quantified it: 2,466 B
   * sent and 25,283 B decoded per save, in two shapes depending on timing - a complete-and-discard and a
   * network abort - with 36 aborted listing refetches in a single session.
   */
  function expectNoListingReread(): void {
    expect(httpMock.match((candidate) => candidate.url === ROLES_URL))
      .withContext('a write must not duplicate the read the listing issues for itself')
      .toHaveSize(0);
    fixture.detectChanges();
  }

  /** Fills the one field a creation genuinely demands. */
  function fillRoleName(value = 'Subscribers'): void {
    type(CONTROL_ID.roleName, value);
  }

  /** The creation request the screen posted, decoded from the pending call. */
  function submittedCreate(): CreateRoleRequest {
    const call: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');
    const body: CreateRoleRequest = call.request.body;

    call.flush(envelope(role()), { status: 201, statusText: 'Created' });
    expectNoListingReread();

    return body;
  }

  /**
   * The update request the screen put, decoded from the pending call.
   *
   * @param roleId The role being updated, which may legitimately be zero.
   * @returns The request contract that went on the wire.
   */
  function submittedUpdate(roleId: number): UpdateRoleRequest {
    const call: TestRequest = expectRequest('PUT', roleUrl(roleId), 'the update');
    const body: UpdateRoleRequest = call.request.body;

    call.flush(envelope(role(roleId)));
    expectNoListingReread();

    return body;
  }

  /** One announcement, reduced to the two members every case asserts on. */
  interface Announcement {
    readonly severity: string;
    readonly message: string;
  }

  /** Every announcement requested, oldest first. */
  function announcements(): readonly Announcement[] {
    return notifySpy.calls.allArgs().map((args: readonly unknown[]) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  /**
   * The support reference each announcement quoted, oldest first, `null` where none was quoted. ⚠
   * DELIBERATELY A SEPARATE PROJECTION FROM {@link Announcement}.
   *
   * @returns The third argument of every `notify` call, oldest first.
   */
  function announcementReferences(): readonly (string | null)[] {
    return notifySpy.calls
      .allArgs()
      .map((args: readonly unknown[]) =>
        typeof args[2] === 'string' && args[2].length > 0 ? args[2] : null,
      );
  }

  /** The support reference the most recent announcement quoted, or `null`. */
  function lastReference(): string | null {
    const quoted: readonly (string | null)[] = announcementReferences();

    return quoted.length === 0 ? null : quoted[quoted.length - 1];
  }

  /** The most recent announcement, or nothing when none was requested. */
  function lastAnnouncement(): Announcement | undefined {
    const queue: readonly Announcement[] = announcements();

    return queue.length === 0 ? undefined : queue[queue.length - 1];
  }

  /** Everything the shared banner is currently saying, whitespace collapsed. */
  function bannerText(): string {
    return (host().querySelector('.error-banner')?.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  /**
   * The one recovery action a detail screen offers once its record cannot be shown, or `null`. Resolved
   * through the header slot, which is where every detail screen puts it.
   */
  function recoveryLink(): HTMLAnchorElement | null {
    return host().querySelector<HTMLAnchorElement>('app-page-header a.page-action');
  }

  function bannerMessage(): string | null {
    const node: Element | null = host().querySelector('.error-banner__message');

    return node === null ? null : textOf(node);
  }

  /**
   * The messages that would still be on screen after the shell's navigation sweep. The queue is REAL in
   * this suite - `notify` is spied and called through - so this exercises the actual retention rule
   * rather than asserting that a method was called.
   *
   * @returns The surviving messages, in queue order.
   */
  function messagesSurvivingNavigation(): readonly string[] {
    const notifications = TestBed.inject(NotificationService);
    notifications.clearOnNavigation();

    return notifications.notifications().map((entry) => entry.message);
  }

  /** The whole rendered document as text, for absence assertions. */
  function documentText(): string {
    return host().textContent ?? '';
  }

  // AREA 1 — EVERY NUMERIC FIELD REFUSES A VALUE OF THE WRONG DATA TYPE
  // The FIRST validator of each pair. `valServiceFee1` and `valTrialFee1` declare `Type="Currency"
  // Operator="DataTypeCheck"`; `valBillingPeriod1` and `valTrialPeriod1` declare `Type="Integer"
  // Operator="DataTypeCheck"`.

  describe('AREA 1 — the data-type refusals', () => {
    NUMERIC_FIELDS.forEach((field: NumericField) => {
      it(`refuses text that is not a number in #${field.controlId}, in the resource wording`, () => {
        createMode();
        fillRoleName();

        type(field.controlId, 'abc');

        expect(messagesFor(field.controlId))
          .withContext(`${field.controlId} reports its own data-type sentence`)
          .toEqual([field.dataTypeMessage]);
      });
    });

    INTEGER_CONTROLS.forEach((controlId: string) => {
      it(`refuses a fractional value in #${controlId}, because Type="Integer" is stricter`, () => {
        createMode();
        fillRoleName();

        type(controlId, '1.5');

        expect(messagesFor(controlId))
          .withContext(`${controlId} admits no fractional part`)
          .not.toEqual([]);
      });
    });

    CURRENCY_CONTROLS.forEach((controlId: string) => {
      it(`accepts two decimal places in #${controlId}, because Type="Currency" admits them`, () => {
        createMode();
        fillRoleName();

        type(controlId, '9.99');

        expect(messagesFor(controlId))
          .withContext(`${controlId} accepts a real money value`)
          .toEqual([]);
      });
    });

    it('accepts the grouped money form a loaded paid role puts in the box', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '1,234.56');

      expect(messagesFor(CONTROL_ID.serviceFee))
        .withContext('the grouped form this screen itself writes is accepted')
        .toEqual([]);
    });

    CURRENCY_CONTROLS.forEach((controlId: string) => {
      it(`refuses more than two decimal places in #${controlId}, which Type="Currency" never admitted`, () => {
        // ⚠ THE SENTENCE IS THE DATA-TYPE ONE, AND THAT IS CORRECT RATHER THAN A CONFUSION OF TWO RULES.
        // The framework's currency conversion measures the digits after the decimal separator against the
        // culture's `CurrencyDecimalDigits` — two — and refuses a longer value BEFORE parsing it, so
        // `-0.001` never reaches a comparison at all.
        createMode();
        fillRoleName();

        type(controlId, '-0.001');

        expect(messagesFor(controlId)).toHaveSize(1);
        expect(messagesFor(controlId)).toEqual([dataTypeMessageFor(controlId)]);
      });

      it(`refuses the exponent form in #${controlId}, which NumberStyles.Currency omits`, () => {
        // `Currency` parses under `NumberStyles.Currency`, which does not include
        // `AllowExponent`, so the legacy validator refused this too.
        createMode();
        fillRoleName();

        type(controlId, '1e5');

        expect(messagesFor(controlId)).toHaveSize(1);
        expect(messagesFor(controlId)).toEqual([dataTypeMessageFor(controlId)]);
      });

      it(`answers a WELL-FORMED negative amount in #${controlId} with the sign sentence, not the data-type one`, () => {
        // The pair is distinguishable in both directions, which is the whole claim: a value that is a real
        // money amount and merely negative gets the comparison sentence, and the two sentences are
        // different strings.
        createMode();
        fillRoleName();

        type(controlId, '-1.00');

        expect(messagesFor(controlId)).toEqual([comparisonMessageFor(controlId)]);
        expect(comparisonMessageFor(controlId)).not.toBe(dataTypeMessageFor(controlId));
      });
    });

    it('shows exactly ONE sentence for a value that breaks both rules on the same control', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, 'minus five');

      expect(messagesFor(CONTROL_ID.serviceFee)).toHaveSize(1);
      expect(messagesFor(CONTROL_ID.serviceFee)).toEqual([SERVICE_FEE_INVALID_MESSAGE]);
    });
  });

  // AREA 2 — EVERY COMPARISON IS NUMERIC, CORRECTING A LEXICAL COMPARISON DEFECT
  // MIGRATION — THE HIGHEST-VALUE PARITY DECISION ON THIS SCREEN, AND A DELIBERATE DIVERGENCE.

  describe('AREA 2 — numeric comparison, not lexical', () => {
    NUMERIC_FIELDS.forEach((field: NumericField) => {
      it(`refuses -5 in #${field.controlId}, which the lexical comparison wrongly passed`, () => {
        createMode();
        fillRoleName();

        type(field.controlId, '-5');

        expect(messagesFor(field.controlId))
          .withContext(`${field.controlId} judges -5 as a NUMBER`)
          .toEqual([field.comparisonMessage]);
      });
    });

    NUMERIC_CONTROLS.forEach((controlId: string) => {
      it(`accepts 9 in #${controlId}, now for the right reason rather than by string order`, () => {
        createMode();
        fillRoleName();

        type(controlId, '9');

        expect(messagesFor(controlId)).toEqual([]);
      });

      it(`accepts 10 in #${controlId}, which string order would have judged one character at a time`, () => {
        createMode();
        fillRoleName();

        type(controlId, '10');

        expect(messagesFor(controlId)).toEqual([]);
      });
    });

    CURRENCY_CONTROLS.forEach((controlId: string) => {
      it(`accepts zero in #${controlId}, because GreaterThanEqual admits it`, () => {
        createMode();
        fillRoleName();

        type(controlId, '0');

        expect(messagesFor(controlId)).toEqual([]);
      });
    });

    it('refuses zero in the billing period, because GreaterThan does NOT admit it', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.billingPeriod, '0');

      expect(messagesFor(CONTROL_ID.billingPeriod)).toEqual([
        BILLING_PERIOD_NOT_POSITIVE_MESSAGE,
      ]);
    });

    it('refuses zero in the trial period, on the same operator', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.trialPeriod, '0');

      expect(messagesFor(CONTROL_ID.trialPeriod)).toEqual([TRIAL_PERIOD_NOT_POSITIVE_MESSAGE]);
    });

    it('separates the two operators: zero passes a fee and fails a period on one submission', () => {
      // The two operators differ, and this is the case where the two stale sentences disagreed with
      // the operators that governed them; see AREA 5.
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '0');
      type(CONTROL_ID.trialFee, '0');
      type(CONTROL_ID.billingPeriod, '0');
      type(CONTROL_ID.trialPeriod, '0');

      expect(messagesFor(CONTROL_ID.serviceFee)).withContext('a free fee is legal').toEqual([]);
      expect(messagesFor(CONTROL_ID.trialFee)).withContext('a free trial is legal').toEqual([]);
      expect(messagesFor(CONTROL_ID.billingPeriod)).toEqual([BILLING_PERIOD_NOT_POSITIVE_MESSAGE]);
      expect(messagesFor(CONTROL_ID.trialPeriod)).toEqual([TRIAL_PERIOD_NOT_POSITIVE_MESSAGE]);
    });
  });

  // AREA 3 — EVERY NUMERIC FIELD IS VALID WHEN EMPTY
  // LOAD-BEARING, AND THE REASON THIS AREA EXISTS AT ALL.

  describe('AREA 3 — the four numeric fields are optional', () => {
    NUMERIC_CONTROLS.forEach((controlId: string) => {
      it(`accepts an emptied #${controlId}, which a comparison validator always did`, () => {
        createMode();
        fillRoleName();

        // Filled, then cleared, so the control is DIRTY and empty — the state in which a message
        // would render if one existed.
        type(controlId, '5');
        type(controlId, '');

        expect(messagesFor(controlId))
          .withContext(`${controlId} says nothing about being empty`)
          .toEqual([]);
      });

      it(`accepts a whitespace-only #${controlId}, which is empty once trimmed`, () => {
        createMode();
        fillRoleName();

        type(controlId, '   ');

        expect(messagesFor(controlId)).toEqual([]);
      });

      it(`renders NO required marker beside #${controlId}`, () => {
        createMode();

        expect(queryAll('.form-field__required', fieldOf(controlId)))
          .withContext(`${controlId} is never presented as demanded`)
          .toHaveSize(0);
      });
    });

    it('marks the role name as the ONE demanded field in creation mode', () => {
      createMode();

      expect(queryAll('.form-field__required', fieldOf(CONTROL_ID.roleName)))
        .withContext('the role name is the only control with a required-field validator')
        .not.toHaveSize(0);
    });

    it('submits with nothing but a role name, and sends the suppressed defaults', () => {
      createMode();
      fillRoleName('Subscribers');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.roleName).toBe('Subscribers');
      expect(body.serviceFee).withContext('fee default').toBe(SUPPRESSED_FEE);
      expect(body.billingPeriod).withContext('period default is ONE').toBe(SUPPRESSED_PERIOD);
      expect(body.billingFrequency).toBe(SUPPRESSED_FREQUENCY);
      expect(body.trialFee).toBe(SUPPRESSED_FEE);
      expect(body.trialPeriod).withContext('trial period default is ONE').toBe(SUPPRESSED_PERIOD);
      expect(body.trialFrequency).toBe(SUPPRESSED_FREQUENCY);
    });

    it('reports no requirement against the four numeric fields when a submission is attempted', () => {
      createMode();

      // Submitted with EVERYTHING empty, so the update command marks every control touched — which is
      // the one moment a spurious required rule would surface.
      press(SUBMIT_LABEL);

      NUMERIC_CONTROLS.forEach((controlId: string) => {
        expect(messagesFor(controlId))
          .withContext(`${controlId} is never demanded`)
          .toEqual([]);
      });

      expect(messagesFor(CONTROL_ID.roleName))
        .withContext('only the role name is demanded')
        .toEqual([ROLE_NAME_REQUIRED_MESSAGE]);
      httpMock.expectNone(ROLES_URL, 'an invalid form sends nothing');
    });
  });

  // ===================================================================================================
  // AREA 4 — THE ROLE NAME'S RULES, THE DESCRIPTION'S ONE RULE, AND NAME IMMUTABILITY
  // ===================================================================================================

  describe('AREA 4 — the role name and the description', () => {
    it('demands a role name when creating, in the resource wording', () => {
      createMode();

      press(SUBMIT_LABEL);

      expect(messagesFor(CONTROL_ID.roleName)).toEqual([ROLE_NAME_REQUIRED_MESSAGE]);
    });

    it('accepts a role name of exactly fifty characters', () => {
      createMode();

      type(CONTROL_ID.roleName, 'a'.repeat(50));

      expect(messagesFor(CONTROL_ID.roleName)).toEqual([]);
    });

    it('refuses a role name of fifty-one characters', () => {
      // `txtRoleName MaxLength="50"` (`editroles.ascx:L27`) and `dbo.Roles.RoleName nvarchar(50)`.
      createMode();

      type(CONTROL_ID.roleName, 'a'.repeat(51));

      expect(messagesFor(CONTROL_ID.roleName)).toEqual([lengthMessage(50)]);
    });

    it('declares the role name limit on the control itself, as the legacy box did', () => {
      createMode();

      expect(input(CONTROL_ID.roleName).getAttribute('maxlength')).toBe('50');
    });

    it('accepts a description of exactly one thousand characters', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.description, 'd'.repeat(1000));

      expect(messagesFor(CONTROL_ID.description)).toEqual([]);
    });

    it('refuses a description of one thousand and one characters', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.description, 'd'.repeat(1001));

      expect(messagesFor(CONTROL_ID.description)).toEqual([lengthMessage(1000)]);
    });

    it('declares the description limit on the control, and renders it as a text area', () => {
      createMode();

      expect(textArea().getAttribute('maxlength')).toBe('1000');
      expect(textArea().tagName).withContext('a plain text area, not an editor').toBe('TEXTAREA');
    });

    it('imposes NO other rule on the description, which carried no validator at all', () => {
      // `txtDescription` (`editroles.ascx:L39-L40`) declares NO validator of any kind, so the length
      // limit is its whole rule set. An empty description and a long legal one are both silent.
      createMode();
      fillRoleName();

      type(CONTROL_ID.description, '');
      expect(messagesFor(CONTROL_ID.description))
        .withContext('an empty description is legal')
        .toEqual([]);

      type(CONTROL_ID.description, 'd'.repeat(999));
      expect(messagesFor(CONTROL_ID.description))
        .withContext('a long but legal description is legal')
        .toEqual([]);

      expect(queryAll('.form-field__required', fieldOf(CONTROL_ID.description)))
        .withContext('the description is never presented as demanded')
        .toHaveSize(0);
    });

    it('shows the name as read-only text when editing, never as an editable control', () => {
      editMode(role(7, { roleName: 'Subscribers' }));

      const readOnlyName: HTMLElement = queryOrFail<HTMLElement>(
        host(),
        `#${CONTROL_ID.roleName}`,
      );

      expect(readOnlyName.tagName).withContext('a produced value, not typed input').toBe('OUTPUT');
      expect(textOf(readOnlyName)).toBe('Subscribers');
      expect(host().querySelector(`input#${CONTROL_ID.roleName}`))
        .withContext('no editable name control exists in edit mode')
        .toBeNull();
    });

    it('submits an update with no editable name, and sends the loaded name unchanged', () => {
      editMode(role(7, { roleName: 'Subscribers' }));

      press(SUBMIT_LABEL);

      const body: UpdateRoleRequest = submittedUpdate(7);

      expect(body.roleName).withContext('the name a person sees is the name that is sent').toBe(
        'Subscribers',
      );
      expect(allMessages())
        .withContext('an absent editable name never blocks an update')
        .toEqual([]);
    });
  });

  // AREA 5 — THE TWO CORRECTED SENTENCES, AND DYNAMIC DISPLAY

  describe('AREA 5 — the corrected sentences and dynamic display', () => {
    it('shows the billing period the RESOURCE sentence, never its stale inline twin', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.billingPeriod, '0');

      expect(documentText()).toContain(BILLING_PERIOD_NOT_POSITIVE_MESSAGE);
      expect(documentText())
        .withContext('the stale inline sentence was never shown to a user and is not shown now')
        .not.toContain(STALE_BILLING_PERIOD_MESSAGE);
    });

    it('shows the trial fee the RESOURCE sentence, never its stale inline twin', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.trialFee, '-1');

      expect(documentText()).toContain(TRIAL_FEE_NEGATIVE_MESSAGE);
      expect(documentText())
        .withContext('the transposed twin of the billing-period sentence')
        .not.toContain(STALE_TRIAL_FEE_MESSAGE);
    });

    it('strips the leading break markup every one of the nine resource values carries', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, 'abc');
      type(CONTROL_ID.trialPeriod, '0');

      allMessages().forEach((message: string) => {
        expect(message.startsWith('<br>'))
          .withContext(`"${message}" carries no leading break`)
          .toBeFalse();
      });

      expect(documentText()).not.toContain('<br>');
      expect(queryAll('br')).withContext('no break element is emitted either').toHaveSize(0);
    });

    it('renders NOTHING on first view, although the creation form is already invalid', () => {
      createMode();

      expect(allMessages()).withContext('nothing is announced before anything is done').toEqual([]);
      expect(queryAll('.form-field__errors')).toHaveSize(0);
    });

    it('renders the message once a submission has exercised the rule', () => {
      createMode();

      press(SUBMIT_LABEL);

      expect(allMessages()).toEqual([ROLE_NAME_REQUIRED_MESSAGE]);
      expect(queryAll('.form-field__errors[role="alert"]'))
        .withContext('the message region announces itself')
        .not.toHaveSize(0);
    });

    it('reports one sentence per control even where two rules govern it', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.trialPeriod, 'abc');
      expect(messagesFor(CONTROL_ID.trialPeriod)).toEqual([TRIAL_PERIOD_INVALID_MESSAGE]);

      type(CONTROL_ID.trialPeriod, '-2');
      expect(messagesFor(CONTROL_ID.trialPeriod)).toEqual([TRIAL_PERIOD_NOT_POSITIVE_MESSAGE]);
    });
  });

  // AREA 6 — THE SERVER'S REFUSALS
  // The legacy guarded its insert with its OWN lookup — `If objRoleController.GetRoleByName(PortalId,
  // objRoleInfo.RoleName) Is Nothing Then` — and showed `DuplicateRole` at `RedError` when it found a match
  // (`:L256`).

  describe('AREA 6 — what the server refuses, and how it is surfaced', () => {
    it('reports a duplicate name at 409 in the legacy wording, at error severity', () => {
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');

      call.flush(silentProblem(409, 'duplicate-role'), { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(bannerMessage())
        .withContext('the banner carries the legacy refusal sentence')
        .toBe(DUPLICATE_ROLE_MESSAGE);
      expect(lastAnnouncement()).toEqual({
        severity: 'error',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(navigateSpy).withContext('the operator stays on the screen').not.toHaveBeenCalled();
    });

    it("surfaces the server's own sentence verbatim when it sends one", () => {
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(409, 'duplicate-role', { detail: 'Role name Administrators is already in use.' }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // VERBATIM ON THE BANNER, which is the surface that resolves document-before-fallback. The
      // notification states the outcome in this screen's own words, so the two surfaces complement each
      // other instead of repeating one sentence twice.
      expect(bannerMessage()).toBe('Role name Administrators is already in use.');
      expect(lastAnnouncement()).toEqual({
        severity: 'error',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(bannerMessage())
        .withContext('the two surfaces never carry the same sentence')
        .not.toBe(lastAnnouncement()?.message ?? null);
    });

    it('handles a 409 on the UPDATE path too, which the legacy never checked for', () => {
      // The legacy ran NO duplicate check when updating — `:L259-L262` updates unconditionally — which it
      // could afford because `UpdateRole` has no `RoleName` parameter and so could not create a collision.
      editMode(role(7));

      press(SUBMIT_LABEL);

      expectRequest('PUT', roleUrl(7), 'the update').flush(silentProblem(409, 'duplicate-role'), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      expect(bannerMessage()).toBe(DUPLICATE_ROLE_MESSAGE);
      expect(lastAnnouncement()).toEqual({
        severity: 'error',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a refusal of authority as a WARNING, never as an error', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(403, 'forbidden', { detail: 'You do not administer this portal.' }),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // The SEVERITY is what this case is about, and it travels with the outcome sentence; the
      // server's own explanation is beside it on the banner.
      expect(lastAnnouncement()).toEqual({
        severity: 'warning',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(bannerMessage()).toBe('You do not administer this portal.');
    });

    it('falls back to its own wording when a refusal explains nothing', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(silentProblem(500, 'unexpected'), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({ severity: 'error', message: SAVE_FAILED_MESSAGE });
    });

    it('RETIRES the refusal when the next press is blocked by a rule of its own', () => {
      // ⚠ A BANNER THAT OUTLIVED THE SNAPSHOT IT DESCRIBED. Runtime testing refused a duplicate name with a
      // `409`, cleared the name, and pressed Update: the press was blocked by the presence rule, and the
      // page went on saying that a role with the same name already exists - about a name that was no longer
      // in the box, beside a field message saying the name was missing.
      createMode();
      fillRoleName('Administrators');
      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(silentProblem(409, 'duplicate-role'), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      expect(bannerMessage()).withContext('the refusal is on screen').toBe(DUPLICATE_ROLE_MESSAGE);

      // The name is cleared, so the next press cannot leave this screen.
      type(CONTROL_ID.roleName, '');
      press(SUBMIT_LABEL);

      expect(bannerMessage()).withContext('and the stale refusal is gone').toBeNull();
      expect(messagesFor(CONTROL_ID.roleName))
        .withContext('replaced by the rule that actually blocked the press')
        .toContain('You Must Enter a Valid Name');
      httpMock.expectNone(ROLES_URL, 'nothing was sent');
    });

    it('preserves the trace identifier from the refusal document as the support reference', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(409, 'duplicate-role', { detail: DUPLICATE_ROLE_MESSAGE, traceId: TRACE_ID }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      const reference: string = textOf(queryOrFail<Element>(host(), '.error-banner__trace'));

      expect(reference).toContain(REFERENCE_PREFIX);
      expect(reference).withContext('the trace identifier is not discarded').toContain(TRACE_ID);
    });

    it('prefers the correlation identifier when the document carries both', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(409, 'duplicate-role', {
          detail: DUPLICATE_ROLE_MESSAGE,
          traceId: TRACE_ID,
          correlationId: CORRELATION_ID,
        }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(textOf(queryOrFail<Element>(host(), '.error-banner__trace')))
        .withContext('the correlation identifier is the one an operator can trace')
        .toContain(CORRELATION_ID);
    });

    it('QUOTES the support reference when a refusal is announced', () => {
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(409, 'duplicate-role', {
          detail: DUPLICATE_ROLE_MESSAGE,
          correlationId: CORRELATION_ID,
        }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(lastAnnouncement())
        .withContext('the announcement still says what happened in this screen own words')
        .toEqual({ severity: 'error', message: SAVE_FAILED_MESSAGE });
      expect(lastReference())
        .withContext('and now carries the identifier the server recorded the refusal under')
        .toBe(CORRELATION_ID);
    });

    it('quotes the SAME reference the banner beside it quotes', () => {
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(409, 'duplicate-role', {
          detail: DUPLICATE_ROLE_MESSAGE,
          traceId: TRACE_ID,
          correlationId: CORRELATION_ID,
        }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      const shown: string = textOf(queryOrFail<Element>(host(), '.error-banner__trace'));

      expect(shown)
        .withContext('the banner quotes the correlation identifier')
        .toContain(CORRELATION_ID);
      expect(lastReference())
        .withContext('and the announcement quotes that identifier, not the trace one')
        .toBe(CORRELATION_ID);
    });

    it('quotes NO reference when the refusal carried none to quote', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').error(new ProgressEvent('network error'));
      fixture.detectChanges();

      expect(lastAnnouncement())
        .withContext('the failed save is still reported')
        .toEqual({ severity: 'error', message: SAVE_FAILED_MESSAGE });
      expect(lastReference())
        .withContext('with nothing quoted, because there was nothing to quote')
        .toBeNull();
    });

    it('places a per-field refusal on the control the server named', () => {
      // The keys are the server's own model-state keys and are Pascal-cased, so they are read with
      // bracket access and their first character is lowered to reach a control of the same name.
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(400, 'validation-failed', {
          errors: { RoleName: ['That name is reserved for the portal.'] },
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(messagesFor(CONTROL_ID.roleName)).toEqual(['That name is reserved for the portal.']);
    });

    it('announces a creation and leaves for the listing when the server accepts it', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);
      submittedCreate();

      expect(announcements()).toContain({ severity: 'success', message: ROLE_CREATED_MESSAGE });
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('still announces a creation that settles AFTER the operator has left the screen', () => {
      // ⚠ THE MEASURED DEFECT, AND ITS CAUSE IS A LIFETIME RATHER THAN A MESSAGE. The write bridge is an
      // effect in this component's injection context, so it dies WITH the component - and an operator who
      // submits and then immediately clicks somewhere else destroys the only party that was going to tell
      // them what happened.
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');

      // The operator leaves while the write is still in flight. Destroying the fixture is exactly what a
      // route change does, and it is what tore the bridge down.
      fixture.destroy();
      navigateSpy.calls.reset();

      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      TestBed.flushEffects();

      expect(notifySpy)
        .withContext('the operator is told the write committed, by the party that outlived the screen')
        .toHaveBeenCalledWith('success', ROLE_CREATED_MESSAGE, null, false);
      expect(navigateSpy)
        .withContext('and is NOT dragged back to the listing they deliberately left')
        .not.toHaveBeenCalled();
    });

    it('states the refusal when a write that outlived the screen was refused', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');

      fixture.destroy();
      notifySpy.calls.reset();

      call.flush(
        {
          type: 'urn:test',
          title: 'Conflict',
          status: 409,
          detail: 'A role of that name exists.',
          correlationId: '4d19ae7c1b8f4e2a9d6c3f5b7a091e2d',
        },
        { status: 409, statusText: 'Conflict' },
      );
      TestBed.flushEffects();

      expect(notifySpy)
        .withContext('the operator is told the write did NOT commit, by the party that outlived the screen')
        .toHaveBeenCalledWith('error', SAVE_FAILED_MESSAGE, '4d19ae7c1b8f4e2a9d6c3f5b7a091e2d');
      expect(notifySpy.calls.allArgs().map((args) => args[1]))
        .withContext('the server detail and any per-field message stay with the banner')
        .not.toContain('A role of that name exists.');
    });

    it('announces an update and leaves for the listing when the server accepts it', () => {
      editMode(role(7));

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      expect(announcements()).toContain({ severity: 'success', message: ROLE_UPDATED_MESSAGE });
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('leaves the form settled at the instant it navigates, so the guard cannot question a saved role', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      editMode(role(7));

      type(CONTROL_ID.description, 'Edited by the operator');

      expect(tracker.isDirty())
        .withContext('a dirty form with no write in flight is what the guard exists to catch')
        .toBeTrue();

      // ⚠ SAMPLED AT THE INSTANT OF NAVIGATION, NOT AFTERWARDS, because it is the navigation the save
      // itself triggers that the guard would have refused. Measured in a real browser before this was
      // settled: every successful save raised "You have unsaved changes on this page.
      let dirtyAtNavigation: boolean | null = null;
      navigateSpy.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
      expect(dirtyAtNavigation)
        .withContext('the guard must see a settled form on the navigation the save itself triggered')
        .toBeFalse();
    });

    it('leaves the confirmation readable at the listing it navigates to', () => {
      editMode(role(7));

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      expect(messagesSurvivingNavigation())
        .withContext('the confirmation belongs at the destination, as the legacy showed it')
        .toContain(ROLE_UPDATED_MESSAGE);
    });

    it('does NOT leave an unrelated earlier message behind at the destination', () => {
      editMode(role(7));

      TestBed.inject(NotificationService).notify('info', 'An earlier, unrelated message.');

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      const surviving = messagesSurvivingNavigation();

      expect(surviving).toContain(ROLE_UPDATED_MESSAGE);
      expect(surviving).not.toContain('An earlier, unrelated message.');
    });
  });

  // AREA 7 — ALL SIX BILLING-FREQUENCY CODES ROUND-TRIP VERBATIM
  // The six codes are LOAD-BEARING PERSISTED DATA, not presentation, and three independent proofs establish
  // them:

  describe('AREA 7 — the six frequency codes', () => {
    it('offers exactly the six captions, in the order the lookup seeded them, on BOTH selects', () => {
      createMode();

      const expected: readonly string[] = FREQUENCIES.map((choice) => choice.label);

      expect(optionLabels(CONTROL_ID.billingFrequency)).toEqual(expected);
      expect(optionLabels(CONTROL_ID.trialFrequency))
        .withContext('one vocabulary, shared by both fields')
        .toEqual(expected);
    });

    it('opens both selects on the no-term code, as the legacy did on first load', () => {
      createMode();

      expect(chosenLabel(CONTROL_ID.billingFrequency)).toBe(NO_FREQUENCY_LABEL);
      expect(chosenLabel(CONTROL_ID.trialFrequency)).toBe(NO_FREQUENCY_LABEL);
    });

    it('submits the no-term code when neither select is touched', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.billingFrequency).toBe(NO_FREQUENCY);
      expect(body.trialFrequency).toBe(NO_FREQUENCY);
    });

    FREQUENCIES.forEach((choice: FrequencyChoice) => {
      it(`sends the billing code "${choice.code}" verbatim for the caption "${choice.label}"`, () => {
        createMode();
        fillRoleName();

        type(CONTROL_ID.serviceFee, '25.00');
        type(CONTROL_ID.billingPeriod, '3');
        choose(CONTROL_ID.billingFrequency, choice.label);

        press(SUBMIT_LABEL);

        const body: CreateRoleRequest = submittedCreate();

        // The no-term code suppresses the whole group, and the suppressed default IS that same code,
        // so the assertion holds on either path.
        expect(body.billingFrequency)
          .withContext(`"${choice.label}" persists as "${choice.code}"`)
          .toBe(choice.code);
      });

      it(`sends the trial code "${choice.code}" verbatim for the caption "${choice.label}"`, () => {
        createMode();
        fillRoleName();

        // A trial is only stored against a PRICED role; see AREA 8.
        type(CONTROL_ID.serviceFee, '25.00');
        type(CONTROL_ID.billingPeriod, '3');
        choose(CONTROL_ID.billingFrequency, 'Month');

        type(CONTROL_ID.trialFee, '5.00');
        type(CONTROL_ID.trialPeriod, '2');
        choose(CONTROL_ID.trialFrequency, choice.label);

        press(SUBMIT_LABEL);

        const body: CreateRoleRequest = submittedCreate();

        expect(body.trialFrequency)
          .withContext(`"${choice.label}" persists as "${choice.code}"`)
          .toBe(choice.code);
      });
    });

    it('keeps a stored code on the form when a role is read back', () => {
      editMode(pricedRole(7, { billingFrequency: 'Y', billingPeriod: 2, serviceFee: 99 }));

      expect(chosenLabel(CONTROL_ID.billingFrequency)).toBe('Year');

      press(SUBMIT_LABEL);

      expect(submittedUpdate(7).billingFrequency)
        .withContext('read as "Y", written as "Y"')
        .toBe('Y');
    });

    it('falls back to the no-term code for a stored value outside the six', () => {
      // The read vocabulary is deliberately wider than the write vocabulary, so an unrecognised stored
      // character cannot be echoed back into a column constrained by a foreign key.
      editMode(pricedRole(7, { billingFrequency: 'Q' }));

      expect(chosenLabel(CONTROL_ID.billingFrequency)).toBe(NO_FREQUENCY_LABEL);
    });
  });

  // AREA 8 — THE THREE-PART BILLING GATE AND THE CROSS-FIELD TRIAL GATE

  describe('AREA 8 — the billing gate and the trial gate', () => {
    it('carries a complete set of terms through as the numbers they are', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '25.00');
      type(CONTROL_ID.billingPeriod, '3');
      choose(CONTROL_ID.billingFrequency, 'Month');

      type(CONTROL_ID.trialFee, '5.00');
      type(CONTROL_ID.trialPeriod, '2');
      choose(CONTROL_ID.trialFrequency, 'Week');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).toBe(25);
      expect(body.billingPeriod).toBe(3);
      expect(body.billingFrequency).toBe('M');
      expect(body.trialFee).toBe(5);
      expect(body.trialPeriod).toBe(2);
      expect(body.trialFrequency).toBe('W');
    });

    it('accepts a grouped fee and sends the number it denotes', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '1,234.56');
      type(CONTROL_ID.billingPeriod, '1');
      choose(CONTROL_ID.billingFrequency, 'Year');

      press(SUBMIT_LABEL);

      expect(submittedCreate().serviceFee)
        .withContext('the thousands separator this screen itself writes round-trips')
        .toBe(1234.56);
    });

    it('suppresses the WHOLE billing group when the frequency is left on the no-term code', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '25.00');
      type(CONTROL_ID.billingPeriod, '3');
      // The frequency is left on its default, which fails the third conjunct.

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).toBe(SUPPRESSED_FEE);
      expect(body.billingPeriod).toBe(SUPPRESSED_PERIOD);
      expect(body.billingFrequency).toBe(SUPPRESSED_FREQUENCY);
    });

    it('suppresses the WHOLE billing group when the fee is empty', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.billingPeriod, '3');
      choose(CONTROL_ID.billingFrequency, 'Month');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).toBe(SUPPRESSED_FEE);
      expect(body.billingPeriod).withContext('period ONE, not the entered three').toBe(
        SUPPRESSED_PERIOD,
      );
      expect(body.billingFrequency).toBe(SUPPRESSED_FREQUENCY);
    });

    it('suppresses the WHOLE billing group when the period is empty', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '25.00');
      choose(CONTROL_ID.billingFrequency, 'Month');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).toBe(SUPPRESSED_FEE);
      expect(body.billingPeriod).toBe(SUPPRESSED_PERIOD);
      expect(body.billingFrequency).toBe(SUPPRESSED_FREQUENCY);
    });

    it('suppresses the trial when the role carries no service fee', () => {
      createMode();
      fillRoleName();

      // A REAL, FREE billing arrangement: zero is entered deliberately and survives as itself.
      type(CONTROL_ID.serviceFee, '0');
      type(CONTROL_ID.billingPeriod, '1');
      choose(CONTROL_ID.billingFrequency, 'Month');

      // Trial terms entered against a free role, which the first conjunct discards.
      type(CONTROL_ID.trialFee, '5.00');
      type(CONTROL_ID.trialPeriod, '2');
      choose(CONTROL_ID.trialFrequency, 'Week');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).withContext('zero is a REAL free value, never coalesced away').toBe(0);
      expect(body.trialFee).toBe(SUPPRESSED_FEE);
      expect(body.trialPeriod).withContext('trial period ONE, not the entered two').toBe(
        SUPPRESSED_PERIOD,
      );
      expect(body.trialFrequency).toBe(SUPPRESSED_FREQUENCY);
    });

    it('says NOTHING about trial terms entered against a free role', () => {
      // The legacy resolved this silently, so a surfaced error would break parity.
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '0');
      type(CONTROL_ID.trialFee, '5.00');
      type(CONTROL_ID.trialPeriod, '2');
      choose(CONTROL_ID.trialFrequency, 'Week');

      expect(allMessages()).withContext('a silent resolution, exactly as measured').toEqual([]);
    });

    it('suppresses the trial transitively when the billing group was itself suppressed', () => {
      createMode();
      fillRoleName();

      // A priced fee with no period fails the billing gate, so the resolved fee is zero — and the
      // trial gate then reads that resolved zero rather than the text in the box.
      type(CONTROL_ID.serviceFee, '25.00');
      choose(CONTROL_ID.billingFrequency, 'Month');

      type(CONTROL_ID.trialFee, '5.00');
      type(CONTROL_ID.trialPeriod, '2');
      choose(CONTROL_ID.trialFrequency, 'Week');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).toBe(SUPPRESSED_FEE);
      expect(body.trialFrequency)
        .withContext('the cascade is measured behaviour and is preserved')
        .toBe(SUPPRESSED_FREQUENCY);
    });

    it('suppresses the trial when the trial frequency is left on the no-term code', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '25.00');
      type(CONTROL_ID.billingPeriod, '3');
      choose(CONTROL_ID.billingFrequency, 'Month');

      type(CONTROL_ID.trialFee, '5.00');
      type(CONTROL_ID.trialPeriod, '2');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.serviceFee).withContext('the billing group still stands').toBe(25);
      expect(body.trialFee).toBe(SUPPRESSED_FEE);
      expect(body.trialPeriod).toBe(SUPPRESSED_PERIOD);
      expect(body.trialFrequency).toBe(SUPPRESSED_FREQUENCY);
    });

    it('stores a FREE trial, whose fee is zero and whose frequency is real', () => {
      // The measured read-side asymmetry, exercised on the write side: a zero trial fee with a real
      // frequency is a legitimate free trial and must survive.
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '25.00');
      type(CONTROL_ID.billingPeriod, '3');
      choose(CONTROL_ID.billingFrequency, 'Month');

      type(CONTROL_ID.trialFee, '0');
      type(CONTROL_ID.trialPeriod, '14');
      choose(CONTROL_ID.trialFrequency, 'Day');

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.trialFee).withContext('a free trial is not an absent trial').toBe(0);
      expect(body.trialPeriod).toBe(14);
      expect(body.trialFrequency).toBe('D');
    });

    it('never throws and never coerces a zero for a value the legacy parse would have rejected', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, 'abc');
      type(CONTROL_ID.billingPeriod, '3');
      choose(CONTROL_ID.billingFrequency, 'Month');

      // The data-type rule refuses the value BEFORE any parse is attempted, so the update command
      // reports and sends nothing at all — no exception, and no silent zero on the wire.
      press(SUBMIT_LABEL);

      expect(messagesFor(CONTROL_ID.serviceFee)).toEqual([SERVICE_FEE_INVALID_MESSAGE]);
      httpMock.expectNone(ROLES_URL, 'an unparseable fee sends nothing');
    });

    it('reads a priced role back into the form with its own money format', () => {
      editMode(pricedRole(7, { serviceFee: 1234.5, billingPeriod: 6, billingFrequency: 'W' }));

      expect(input(CONTROL_ID.serviceFee).value)
        .withContext('this screen groups thousands; the list screen does not')
        .toBe('1,234.50');
      expect(input(CONTROL_ID.billingPeriod).value).toBe('6');
      expect(chosenLabel(CONTROL_ID.billingFrequency)).toBe('Week');
    });
  });

  // AREA 9 — MODE BY PRESENCE, THE UNGROUPED ROLE GROUP, AND SENTINEL RENDERING
  // ⚠ `dbo.Roles.RoleID` IS `IDENTITY (0, 1)`
  // (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115`), so ZERO IS A REAL
  // ROLE ID and must open the EDIT form.

  describe('AREA 9 — the mode, the grouping and the sentinels', () => {
    it('reads the role groups from the constructor, before either mode is known', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.detectChanges();

      answerGroups();

      expect(chosenLabel(CONTROL_ID.roleGroup)).toBe(GLOBAL_ROLES_LABEL);
    });

    it('reads the group list UNPAGED, with no paging parameter of any kind', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.detectChanges();

      const call: TestRequest = answerGroups();

      PAGING_KEYS.forEach((key: string) => {
        expect(call.request.params.has(key))
          .withContext(`the group read carries no "${key}"`)
          .toBeFalse();
      });
    });

    it('opens the CREATION form when the address names no role, and reads none', () => {
      createMode();

      expect(textOf(queryOrFail<Element>(host(), 'h1'))).toBe(ADD_TITLE);
      expect(host().querySelector(`output#${CONTROL_ID.roleName}`))
        .withContext('an editable name, not a produced one')
        .toBeNull();
      httpMock.expectNone(
        (candidate) => candidate.url.startsWith(`${ROLES_URL}/`),
        'no role is read when none is named',
      );
    });

    it('opens the EDIT form for role ZERO, and reads exactly /api/v1/roles/0', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '0');
      fixture.detectChanges();

      answerGroups();

      const read: TestRequest = expectRequest('GET', roleUrl(0), 'the role read for role zero');

      read.flush(envelope(role(0, { roleName: 'Administrators' })));
      fixture.detectChanges();

      expect(read.request.url).toBe('/api/v1/roles/0');
      expect(textOf(queryOrFail<Element>(host(), 'h1'))).toBe(EDIT_TITLE);
      expect(textOf(queryOrFail<Element>(host(), `output#${CONTROL_ID.roleName}`))).toBe(
        'Administrators',
      );
    });

    it('updates role ZERO with a PUT to its own address, never a POST to the collection', () => {
      editMode(role(0, { roleName: 'Administrators' }));

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('PUT', roleUrl(0), 'the update of role zero');

      expect(call.request.url).toBe('/api/v1/roles/0');
      call.flush(envelope(role(0)));
      expectNoListingReread();

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('creates with a POST to the collection, carrying no identifier in the address', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');

      expect(call.request.url).toBe('/api/v1/roles');
      call.flush(envelope(role()), { status: 201, statusText: 'Created' });
      expectNoListingReread();
    });

    it('offers no form at all for a route parameter that is not an integer', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', 'not-a-role');
      fixture.detectChanges();

      answerGroups();

      expect(textOf(queryOrFail<Element>(host(), 'h1'))).toBe(EDIT_TITLE);

      // Nothing to fill in and nothing to submit: the surface is withdrawn rather than disabled, so
      // there is no control an operator can reach at all.
      expect(host().querySelectorAll('form').length)
        .withContext('no form is offered for an address that names nothing readable')
        .toBe(0);
      expect(host().querySelectorAll('input, select, textarea').length)
        .withContext('and therefore no field either')
        .toBe(0);
      expect(host().querySelector('button[type="submit"]'))
        .withContext('above all no submit: pressing it would have created a role')
        .toBeNull();

      // The state is STATED rather than merely left blank, so the address is diagnosable - and it is stated
      // by the SHARED BANNER, which is the application's one assertive owner for a refusal. It used to be a
      // paragraph of this screen's own: a live region created together with its first message, which is
      // announced inconsistently, where the banner's region is already in the document.
      expect(bannerText()).toContain(UNREADABLE_ADDRESS_MESSAGE);
      expect(host().querySelector('.role-form__notice'))
        .withContext('one statement, not two')
        .toBeNull();

      // And the one way out, in the header slot every detail screen uses for it.
      expect(recoveryLink()?.getAttribute('href')).toBe('/roles');

      httpMock.expectNone(
        (candidate) => candidate.url.startsWith(`${ROLES_URL}/`),
        'an unusable parameter reads nothing',
      );
    });

    it('reads role 1 for a route parameter written with leading zeros', () => {
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '00001');
      fixture.detectChanges();

      answerGroups();

      expect(textOf(queryOrFail<Element>(host(), 'h1'))).toBe(EDIT_TITLE);

      const read: TestRequest = expectRequest('GET', roleUrl(1), 'the role read');

      expect(read.request.url)
        .withContext('the id is normalised rather than transmitted as typed')
        .not.toContain('00001');

      read.flush(envelope(role(1, { roleName: 'Subscribers' })));
      fixture.detectChanges();
    });

    it('transmits nothing for a route parameter beyond the signed 32-bit range', () => {
      // ⚠ THE REQUEST IS THE DEFECT, NOT THE ID. Every identifier column in this schema is a SQL Server
      // `int` and the API binds the segment with `int.TryParse`, so this value cannot name a record and the
      // round trip was guaranteed to fail.
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '2147483648');
      fixture.detectChanges();

      answerGroups();

      // Counted rather than merely asserted through the testing backend, for the same reason: `match`
      // returns what it found, so the size IS the assertion and the case cannot pass vacuously.
      expect(httpMock.match((candidate) => candidate.url.startsWith(`${ROLES_URL}/`)))
        .withContext('an unaddressable id reads nothing')
        .toHaveSize(0);
    });

    it('reads the largest addressable role id, so the bound refuses nothing valid', () => {
      // ⚠ THE BOUNDARY, in the other direction. A guard that refused the range limit itself would
      // make one real record unreachable.
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '2147483647');
      fixture.detectChanges();

      answerGroups();

      const read: TestRequest = expectRequest('GET', roleUrl(2147483647), 'the role read');

      expect(read.request.url)
        .withContext('the largest addressable id is read, not refused')
        .toBe(roleUrl(2147483647));

      read.flush(envelope(role(2147483647)));
      fixture.detectChanges();
    });

    it('states a role that has gone where it was asked for, and offers the one way out', () => {
      // ⚠ MIGRATION - THE LEGACY BOUNCE IS GONE, DELIBERATELY, AND THIS SPEC ASSERTED IT. `:L170-L172`
      // treated an unreadable role as an attempt to reach an item outside the module and redirected to the
      // Security Roles page; this screen reproduced that with a surviving toast and a replaced history
      // entry. It was the only one of the four detail screens that moved the reader, it needed an explicit
      // exemption from the shell's navigation sweep for its own explanation to survive the navigation it
      // caused, and it discarded the address the reader had followed. All four screens now state a missing
      // record in place, in one shared wording, through the one assertive region, with one working way out.
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '404');
      fixture.detectChanges();

      answerGroups();

      expectRequest('GET', roleUrl(404), 'the role read').flush(silentProblem(404, 'role-missing'), {
        status: 404,
        statusText: 'Not Found',
      });
      fixture.detectChanges();

      expect(bannerText()).toContain(ROLE_NOT_FOUND_MESSAGE);
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(lastAnnouncement()).toBeUndefined();

      // No form, and no support reference: a record that is not there is a legitimate state rather than an
      // occurrence anyone can look up.
      expect(host().querySelectorAll('form').length).toBe(0);
      expect(host().querySelector('.error-banner__trace')).toBeNull();
      expect(recoveryLink()?.textContent?.trim()).toBe('Back to Security Roles');
    });

    // ⚠ THE CASE THAT PINNED A SUPPORT REFERENCE ON THIS REFUSAL WAS REMOVED, AND ITS PREMISE IS WHY. It
    // existed because the refusal was only ever shown as a toast: the screen it belonged to was destroyed by
    // the navigation back to the listing, taking its error banner and the banner's reference line with it, so
    // the toast was the only place the reference could appear. That navigation is gone - a role that is not
    // there is now stated in place - so the screen survives, no toast is raised at all, and the case above
    // pins the replacement: no announcement, no reference, and one working way out. A record that is not
    // there is a legitimate state rather than an occurrence anyone can look up.

    it('offers the ungrouped choice FIRST, captioned "< Global Roles >" with its brackets', () => {
      createMode([roleGroup(4, { roleGroupName: 'Paid Services' }), roleGroup(5, { roleGroupName: 'Staff' })]);

      expect(optionLabels(CONTROL_ID.roleGroup)).toEqual([
        GLOBAL_ROLES_LABEL,
        'Paid Services',
        'Staff',
      ]);
    });

    it('never offers the list screen\u2019s filter sentinel, which has no meaning as a stored value', () => {
      createMode([roleGroup(4, { roleGroupName: 'Paid Services' })]);

      expect(optionLabels(CONTROL_ID.roleGroup))
        .withContext('the narrowing entry the LIST screen adds belongs only there')
        .not.toContain('< All Roles >');

      Array.from(select(CONTROL_ID.roleGroup).options).forEach((option: HTMLOptionElement) => {
        expect(option.value)
          .withContext('no option carries the filter sentinel')
          .not.toContain(String(LIST_FILTER_SENTINEL));
      });
    });

    it('sends the ungrouped state as nothing at all, never as the legacy marker', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const body: CreateRoleRequest = submittedCreate();

      expect(body.roleGroupId).toBe(UNGROUPED);
      expect(body.roleGroupId).not.toBe(LEGACY_UNGROUPED);
      expect(body.roleGroupId).withContext('group ZERO is a real group').not.toBe(0);
    });

    it('recognises the legacy marker on the way IN, and still writes an absence out', () => {
      editMode(role(7, { roleGroupId: LEGACY_UNGROUPED }), {
        groups: [roleGroup(4, { roleGroupName: 'Paid Services' })],
      });

      expect(chosenLabel(CONTROL_ID.roleGroup))
        .withContext('a producer that has not collapsed the sentinel still selects the right entry')
        .toBe(GLOBAL_ROLES_LABEL);

      press(SUBMIT_LABEL);

      expect(submittedUpdate(7).roleGroupId).toBe(UNGROUPED);
    });

    it('round-trips group ZERO, which the identity seed makes a real group', () => {
      editMode(role(7, { roleGroupId: 0 }), {
        groups: [roleGroup(0, { roleGroupName: 'Core Services' })],
      });

      expect(chosenLabel(CONTROL_ID.roleGroup)).toBe('Core Services');

      press(SUBMIT_LABEL);

      expect(submittedUpdate(7).roleGroupId)
        .withContext('never mistaken for an absence')
        .toBe(0);
    });

    it('renders an absent fee as EMPTY, never as zero and never as the raw sentinel', () => {
      editMode(role(7, { serviceFee: ABSENT_MONEY }));

      expect(input(CONTROL_ID.serviceFee).value).toBe('');
      expect(documentText()).not.toContain(String(ABSENT_MONEY));
    });

    it('renders an absent period as EMPTY, never as minus one', () => {
      editMode(pricedRole(7, { billingPeriod: ABSENT_PERIOD }));

      expect(input(CONTROL_ID.billingPeriod).value).toBe('');
    });

    it('renders an absent description and an absent code as EMPTY text, never as a word', () => {
      // The empty string is the legacy absent-string sentinel, so it survives on the wire and must
      // never surface as the word a null would print.
      editMode(role(7, { description: null, rsvpCode: null, iconFile: null }));

      expect(textArea().value).toBe('');
      expect(input(CONTROL_ID.rsvpCode).value).toBe('');
      expect(input(CONTROL_ID.iconFile).value).toBe('');
      expect(documentText()).not.toContain('null');
      expect(documentText()).not.toContain('undefined');
    });

    it('renders no date at all, because the role contract declares none', () => {
      // The legacy expiry arithmetic lives entirely on the server, driven by the frequency code, so this
      // screen has no date to show and no clock to depend on. The absent-date sentinel therefore cannot
      // surface here as `01/01/0001`.
      editMode(pricedRole(7));

      expect(documentText()).not.toContain('01/01/0001');
      expect(documentText()).not.toContain('0001-01-01');
      expect(queryAll('input[type="date"]')).toHaveSize(0);
    });
  });

  // AREA 10 — THE PORTAL-PROTECTED ROLES
  // So the ADMINISTRATOR role keeps its membership command while the registered-users role does not, and
  // that asymmetry is preserved rather than tidied: the administrator role's membership is genuinely
  // manageable, whereas every authenticated user holds the other.

  describe('AREA 10 — the roles the portal protects', () => {
    it('locks the administrator role read-only and offers neither save nor delete', () => {
      editMode(role(0, { roleName: 'Administrators' }), { administratorRoleId: 0 });

      expect(command(SUBMIT_LABEL)).withContext('no save command').toBeUndefined();
      expect(command(DELETE_LABEL)).withContext('no delete command').toBeUndefined();
    });

    it('keeps the membership command for the administrator role, which is the measured asymmetry', () => {
      editMode(role(0, { roleName: 'Administrators' }), { administratorRoleId: 0 });

      expect(command(MANAGE_USERS_LABEL))
        .withContext('the second guard names only the registered-users role')
        .not.toBeUndefined();
    });

    it('protects the registered-users role AND withholds its membership screen', () => {
      editMode(role(3, { roleName: 'Registered Users' }), { registeredRoleId: 3 });

      expect(command(SUBMIT_LABEL)).toBeUndefined();
      expect(command(DELETE_LABEL)).toBeUndefined();
      expect(command(MANAGE_USERS_LABEL))
        .withContext('every authenticated user holds this role, so there is nothing to manage')
        .toBeUndefined();
    });

    it('conveys the locked state PROGRAMMATICALLY, not by colour alone', () => {
      editMode(role(0), { administratorRoleId: 0 });

      expect(textArea().disabled).withContext('the description is disabled').toBeTrue();
      expect(select(CONTROL_ID.roleGroup).disabled).withContext('the grouping is disabled').toBeTrue();
      expect(input(CONTROL_ID.serviceFee).disabled).withContext('the fee is disabled').toBeTrue();
      expect(select(CONTROL_ID.billingFrequency).disabled).toBeTrue();
      expect(input(CONTROL_ID.rsvpCode).disabled).toBeTrue();
      expect(input(CONTROL_ID.iconFile).disabled)
        .withContext('the icon field the legacy left live is disabled too')
        .toBeTrue();
    });

    it('explains the locked state in words, which the legacy never did', () => {
      editMode(role(0), { administratorRoleId: 0 });

      expect(textOf(queryOrFail<Element>(host(), '.role-form__notice')))
        .withContext('a greyed field with no reason for it is worse than none')
        .not.toBe('');
    });

    it('leaves an ordinary role fully editable, and offers all four commands', () => {
      editMode(role(7), { administratorRoleId: 0, registeredRoleId: 3 });

      expect(command(SUBMIT_LABEL)).not.toBeUndefined();
      expect(command(CANCEL_LABEL)).not.toBeUndefined();
      expect(command(DELETE_LABEL)).not.toBeUndefined();
      expect(command(MANAGE_USERS_LABEL)).not.toBeUndefined();
      expect(textArea().disabled).toBeFalse();
    });

    it('ASKS FOR THE TENANT\u2019S OWN RECORD on arrival, for the caller\u2019s tenant', () => {
      // ⚠ THE FACTS ARE READ, NOT AWAITED FROM A CALLER. They were three optional inputs that nothing in
      // the application supplied — no route, no parent template — so the guards below shipped permanently
      // disarmed.
      editMode(role(7));

      expect(loadCurrentPortalContext).toHaveBeenCalledWith(TENANT_ID);
      expect(loadCurrentPortalContext).toHaveBeenCalledTimes(1);
    });

    it('stays editable while the tenant record is still OUTSTANDING, and defers to the API', () => {
      // ⚠ THE FAIL-SAFE DIRECTION, AND IT IS DELIBERATE. Until the record arrives each key is absent, every
      // comparison is false and the form behaves exactly as it did before the guard existed: the command is
      // offered and the server decides, its refusal surfacing as a warning.
      editMode(role(0, { roleName: 'Administrators' }));

      expect(command(SUBMIT_LABEL))
        .withContext('nothing on the role itself discriminates it, so nothing is guessed')
        .not.toBeUndefined();

      press(SUBMIT_LABEL);

      expectRequest('PUT', roleUrl(0), 'the update the API will refuse').flush(
        problem(403, 'forbidden', { detail: 'That role is maintained by the portal.' }),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({
        severity: 'warning',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(bannerMessage())
        .withContext("the API's own explanation is what the banner shows")
        .toBe('That role is maintained by the portal.');
    });

    it('ARMS the guard as the tenant record arrives, without the screen being remounted', () => {
      // The record arrives after the form is already on screen, which is the ordinary sequence: the request
      // is issued on construction and answers a moment later. Every consumer is a `computed` over the
      // store's signals, so the transition needs no reload and no second visit.
      editMode(role(0, { roleName: 'Administrators' }));

      expect(command(SUBMIT_LABEL))
        .withContext('offered while the tenant is unread')
        .not.toBeUndefined();

      administratorRole.set(0);
      tenantResolved.set(true);
      fixture.detectChanges();

      expect(command(SUBMIT_LABEL))
        .withContext('withdrawn the moment the tenant names this role as its administrator role')
        .toBeUndefined();
      expect(command(DELETE_LABEL)).toBeUndefined();
    });

    it('protects the role keyed NOUGHT, which the identity seed makes a real role', () => {
      // ⚠ `Roles.RoleID` is `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L114`), so the administrator role
      // of a freshly created tenant genuinely carries nought — and a guard that tested either side for
      // truthiness would leave exactly that role unprotected.
      editMode(role(0, { roleName: 'Administrators' }), { administratorRoleId: 0 });

      expect(command(SUBMIT_LABEL)).toBeUndefined();
      expect(command(DELETE_LABEL)).toBeUndefined();
    });

    it('protects only the two roles the TENANT names, and no other', () => {
      // The keys are the tenant's, not a hardcoded pair and not a role name. A role that is neither
      // is fully editable even when both keys are known.
      editMode(role(7, { roleName: 'Subscribers' }), {
        administratorRoleId: 0,
        registeredRoleId: 1,
      });

      expect(command(SUBMIT_LABEL)).not.toBeUndefined();
      expect(command(DELETE_LABEL)).not.toBeUndefined();
    });
  });

  // THE NAME IS TIDIED, AND A BLANK ONE IS REFUSED BY THE RULE ITSELF
  // PADDING is settled on submit, by tidying the name INTO ITS OWN CONTROL rather than on the way into the
  // request, so the value that was validated and the value that is sent are one string. the legacy stored
  // what was posted, padding and all.

  describe('tidying the role name before judging it', () => {
    it('REFUSES a whitespace-only name rather than posting an empty one', () => {
      createMode();
      fillRoleName('   ');

      press(SUBMIT_LABEL);

      httpMock.expectNone(() => true);

      // And the requirement is reported, beside a field the operator did fill in — they typed
      // something, so they are owed an explanation of why it amounts to nothing.
      expect(messagesFor(CONTROL_ID.roleName))
        .withContext('the form complains rather than deferring to the server')
        .not.toEqual([]);
    });

    it('refuses a whitespace-only name AS SOON AS IT IS TYPED, without waiting for a submit', () => {
      // The moment matters, and it is the moment the sibling form uses. An ASP.NET validator was wired to
      // the control's own change event and updated its display there, so the legacy reported this before
      // any postback; a rule that waited for the submit would report it later than the screen it replaces.
      createMode();
      fillRoleName('   ');

      expect(messagesFor(CONTROL_ID.roleName))
        .withContext('the presence rule trims before judging, so it fires on the entry itself')
        .not.toEqual([]);
    });

    it('leaves a refused whitespace-only entry exactly as typed, rather than blanking the box', () => {
      createMode();
      fillRoleName('   ');

      press(SUBMIT_LABEL);

      httpMock.expectNone(() => true);
      expect(input(CONTROL_ID.roleName).value).toBe('   ');
    });

    it('TIDIES a padded name into the control, so what is shown is what is sent', () => {
      createMode();
      fillRoleName('  Subscribers  ');

      press(SUBMIT_LABEL);

      expect(submittedCreate().roleName).toBe('Subscribers');

      // The control agrees with the payload, so nobody is left looking at an entry that differs from
      // the one that was accepted.
      expect(input(CONTROL_ID.roleName).value).toBe('Subscribers');
    });

    it('still ACCEPTS a name that tidying merely shortens, which must not regress', () => {
      // ⚠ THE BOUNDARY. A guard that refused everything tidying touched would refuse the case above,
      // so this pins that tidying to a NON-EMPTY value is unaffected by the re-judgement.
      createMode();
      fillRoleName('Subscribers ');

      press(SUBMIT_LABEL);

      expect(submittedCreate().roleName).toBe('Subscribers');
      expect(messagesFor(CONTROL_ID.roleName)).toEqual([]);
    });

    it('lets a corrected name through immediately, so the refusal does not latch', () => {
      createMode();
      fillRoleName('\u00a0');
      press(SUBMIT_LABEL);

      httpMock.expectNone(() => true);

      // The operator reads the message and types a real name. Nothing else is re-entered.
      fillRoleName('Subscribers');
      press(SUBMIT_LABEL);

      expect(submittedCreate().roleName).toBe('Subscribers');
    });

    it('leaves the DESCRIPTION to its own rule, which collapses a blank one to nothing', () => {
      createMode();
      fillRoleName('Subscribers');
      type(CONTROL_ID.description, '  padded  ');

      press(SUBMIT_LABEL);

      expect(submittedCreate().description)
        .withContext('the nullable member takes its own rule, not the name\u2019s')
        .toBe('padded');
    });

    it('sends NO description at all for a whitespace-only one, rather than refusing the form', () => {
      // The counterpart, and the reason the two rules must not be merged: a blank description is
      // accepted as an absence, whereas a blank NAME is refused outright.
      createMode();
      fillRoleName('Subscribers');
      type(CONTROL_ID.description, '   ');

      press(SUBMIT_LABEL);

      expect(submittedCreate().description).toBeNull();
    });

    it('does not mark a pristine form dirty by tidying a name that needs none', () => {
      // `setValue` is skipped outright when the value is already trimmed, so no unnecessary write can
      // move the form's state. Proved by consequence: the creation goes out unchanged.
      createMode();
      fillRoleName('Subscribers');

      press(SUBMIT_LABEL);

      expect(submittedCreate().roleName).toBe('Subscribers');
    });
  });

  // ===================================================================================================
  // AREA 11 — THE COMMANDS EACH MODE OFFERS, AND THEIR WORDING
  //
  // MEASURED AT `EditRoles.ascx.vb:L183-L188`: creation mode hides Delete and hides Manage, shows the
  // name box and hides its read-only twin. So a creation form offers exactly TWO commands.
  //
  // The captions: `cmdUpdate`, `cmdCancel` and `cmdDelete` have NO local resource key, fall back to the
  // global resources, and their global captions are IDENTICAL to their inline text — so those three are
  // not stale and are used as they stand. `cmdManage` DOES have a local key, and its value
  // `Manage Users in this Role` supersedes the inline `Manage Users`.
  //
  // MIGRATION: `EditRoles.ascx.resx` has NO `ControlTitle_add` key, so the legacy ADD form showed the
  // same "Edit Security Roles" heading as the edit form. The creation caption is taken instead from the
  // list screen's own action that reaches this form — `Roles.ascx.resx` `AddContent.Action` — because a
  // heading saying "Edit" above a blank creation form is a defect, and the substituted wording is the
  // site's own rather than invented.
  //
  // MIGRATION: localisation is NOT ported. The legacy resolved every caption through
  // `Localization.GetString`, and no translation runtime is added to this workspace, so the measured
  // English wording is authored directly and the resource files serve as its authority.
  // ===================================================================================================

  describe('AREA 11 — the commands and their wording', () => {
    it('offers EXACTLY a create command and Cancel when creating', () => {
      createMode();

      expect(commandLabels()).toEqual([CREATE_SUBMIT_LABEL, CANCEL_LABEL]);
    });

    // ⚠ #22 — ONE ACTION, ONE WORD PER MODE. The four create screens offered three different words for this
    // action, because two of them named their subject and two said "Update" over a form for something that
    // did not exist yet. Both captions are asserted here so neither can drift back.
    it('names what it will create on the creation form, and says Update on the edit form', () => {
      createMode();

      const created = command(CREATE_SUBMIT_LABEL);

      expect(created).withContext('the creation form offers a primary action').not.toBeUndefined();
      expect(created === undefined ? '' : textOf(created))
        .withContext('and it names its subject')
        .toBe('Create Role');

      editMode(role(7));

      const updated = command(SUBMIT_LABEL);

      expect(updated).withContext('the edit form offers a primary action').not.toBeUndefined();
      expect(updated === undefined ? '' : textOf(updated))
        .withContext('and it keeps the legacy wording, where the legacy screen applied it')
        .toBe('Update');
    });

    it('offers no delete and no membership command when creating', () => {
      createMode();

      expect(command(DELETE_LABEL)).toBeUndefined();
      expect(command(MANAGE_USERS_LABEL)).toBeUndefined();
    });

    it('offers all four commands when editing an ordinary role, in the measured order', () => {
      editMode(role(7));

      expect(commandLabels()).toEqual([
        SUBMIT_LABEL,
        CANCEL_LABEL,
        DELETE_LABEL,
        MANAGE_USERS_LABEL,
      ]);
    });

    it('uses the LOCAL caption for the membership command, never its stale inline twin', () => {
      editMode(role(7));

      const captions: readonly string[] = commandLabels();

      expect(captions).toContain(MANAGE_USERS_LABEL);
      expect(captions)
        .withContext('the inline caption is stale wherever a local resource key exists')
        .not.toContain(STALE_MANAGE_LABEL);
    });

    it('heads the creation form with the list screen\u2019s own action wording', () => {
      createMode();

      expect(textOf(queryOrFail<Element>(host(), 'h1'))).toBe(ADD_TITLE);
    });

    it('heads the edit form with the local title entry', () => {
      editMode(role(7));

      expect(textOf(queryOrFail<Element>(host(), 'h1'))).toBe(EDIT_TITLE);
    });

    it('withholds every command while a write of its own is outstanding', () => {
      // The legacy got this free from a synchronous postback; here it is explicit.
      editMode(role(7));

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('PUT', roleUrl(7), 'the update');

      expect(command(SUBMIT_LABEL)?.disabled).withContext('no double submission').toBeTrue();
      expect(command(CANCEL_LABEL)?.disabled).toBeTrue();

      call.flush(envelope(role(7)));
      expectNoListingReread();
    });
  });

  // AREA 12 — THE THREE COMMANDS THAT DO NOT VALIDATE
  // The delete command's browser confirmation at `:L112` — `ClientAPI.AddButtonConfirm(cmdDelete,
  // Localization.GetString("DeleteItem"))` — becomes the shared dialogue, which adds the focus trap and the
  // escape key the browser confirmation never had. Its wording is the global resource value VERBATIM.

  describe('AREA 12 — the commands that do not validate', () => {
    it('abandons the form without validating, even when it is invalid', () => {
      createMode();

      // Left invalid: the role name is empty and demanded.
      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { queryParams: {} });
      expect(allMessages())
        .withContext('nothing is marked, so nothing is announced')
        .toEqual([]);
      httpMock.expectNone(ROLES_URL, 'abandoning sends nothing');
    });

    it('opens the confirmation without validating, and adds no message as a side effect', () => {
      editMode(role(7));

      // Made invalid deliberately, so that a command which validated would be caught doing it.
      type(CONTROL_ID.serviceFee, 'abc');

      const before: number = allMessages().length;

      press(DELETE_LABEL);

      expect(allMessages().length)
        .withContext('the delete command validates nothing')
        .toBe(before);
      expect(textOf(queryOrFail<Element>(host(), '.confirm-dialog__message'))).toBe(
        DELETE_CONFIRM_MESSAGE,
      );

      pressDialogue(CANCEL_LABEL);
    });

    it('asks first, then deletes and leaves without re-reading the listing', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', roleUrl(7), 'the deletion').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expectNoListingReread();

      expect(announcements()).toContain({ severity: 'success', message: ROLE_DELETED_MESSAGE });
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('sends nothing when the confirmation is dismissed', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(CANCEL_LABEL);

      expect(host().querySelector('.confirm-dialog')).withContext('the dialogue closes').toBeNull();
      httpMock.expectNone(roleUrl(7), 'a dismissed confirmation deletes nothing');
    });

    it('reports a refused deletion as a warning and stays put', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', roleUrl(7), 'the deletion').flush(
        problem(403, 'forbidden', { detail: 'That role cannot be removed.' }),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({
        severity: 'warning',
        message: DELETE_FAILED_MESSAGE,
      });
      expect(bannerMessage()).toBe('That role cannot be removed.');
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a failed deletion in its own wording when the server explains nothing', () => {
      editMode(role(7));

      press(DELETE_LABEL);
      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', roleUrl(7), 'the deletion').flush(silentProblem(500, 'unexpected'), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({ severity: 'error', message: DELETE_FAILED_MESSAGE });
    });

    it('reaches the membership screen by navigating, without validating', () => {
      editMode(role(7));

      type(CONTROL_ID.serviceFee, 'abc');

      const before: number = allMessages().length;

      press(MANAGE_USERS_LABEL);

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE, 7, 'users']);
      expect(allMessages().length)
        .withContext('the membership command validates nothing')
        .toBe(before);
    });

    it('declares the type of every command, so only the update can submit', () => {
      editMode(role(7));

      expect(command(SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      expect(command(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
      expect(command(DELETE_LABEL)?.getAttribute('type')).toBe('button');
      expect(command(MANAGE_USERS_LABEL)?.getAttribute('type')).toBe('button');
      expect(queryAll('[onclick]')).withContext('no inline handler is emitted').toHaveSize(0);
    });
  });

  // AREA 13 — THE RENDERED DOCUMENT
  // MEASURED DISCLOSURE STATE. `dshBasic` declares NO expanded state and the legacy default is EXPANDED.
  // `dshAdvanced` (`:L68-L70`) declares `IsExpanded="False"` and is therefore COLLAPSED ON FIRST RENDER.

  describe('AREA 13 — the rendered document', () => {
    /** The advanced disclosure, which is the only collapsible region on this screen. */
    function advancedSection(): HTMLDetailsElement {
      return queryOrFail<HTMLDetailsElement>(host(), 'details.role-form__section--advanced');
    }

    /**
     * The slot one control sits in, narrowed without a cast.
     *
     * @param control The control whose slot is wanted.
     * @returns The slot element.
     */
    function slotOf(control: HTMLElement): HTMLElement {
      const slot: HTMLElement | null = control.parentElement;

      if (slot === null) {
        throw new Error('Expected the control to sit in a slot, but it had no parent.');
      }

      return slot;
    }

    /** Its toggle. */
    function advancedSummary(): HTMLElement {
      return queryOrFail<HTMLElement>(advancedSection(), 'summary');
    }

    // ⚠ WHAT WAS MEASURED. The invitation-code control was capped at 96px - a 78px content box - while the
    // stored value `GOLDPASS2026X` measured 124.79px, so six characters were clipped away with no ellipsis and
    // no title to recover them. The server REQUIRES at least twelve characters, so the control was too narrow
    // for every legal value it could ever hold, not merely for a long one.
    it('sizes the invitation code for a legal value rather than for a money amount', () => {
      editMode(role(7, { rsvpCode: 'GOLDPASS2026X' }));

      const control = input(CONTROL_ID.rsvpCode);
      const slot = control.parentElement;

      expect(slot).withContext('the control sits in a slot').not.toBeNull();
      expect(slot?.classList)
        .withContext('NOT the narrow slot, which is sized for a fee and a period count')
        .not.toContain('role-form__control--narrow');
      expect(slot?.classList)
        .withContext('the ordinary control slot every other text field on this screen uses')
        .toContain('role-form__control');

      // The narrow measure is what caused the defect, so the slot must not resolve to it. Compared against the
      // fee control beside it, which legitimately IS narrow: the two must no longer be the same measure.
      const codeCap = getComputedStyle(slotOf(control)).maxInlineSize;
      const feeCap = getComputedStyle(slotOf(input(CONTROL_ID.serviceFee))).maxInlineSize;

      expect(codeCap).withContext('a measure is declared').not.toBe('none');
      expect(codeCap)
        .withContext('and it is no longer the narrow measure the fee keeps')
        .not.toBe(feeCap);
      expect(parseFloat(codeCap))
        .withContext('the code slot is the wider of the two')
        .toBeGreaterThan(parseFloat(feeCap));
    });

    it('renders the advanced section COLLAPSED on first view', () => {
      createMode();

      expect(advancedSection().open)
        .withContext('IsExpanded="False" is reproduced exactly')
        .toBeFalse();
      expect(advancedSummary().getAttribute('aria-expanded')).toBe('false');
    });

    it('announces the advanced section as expanded once it is opened', () => {
      createMode();

      const section: HTMLDetailsElement = advancedSection();

      section.open = true;
      section.dispatchEvent(new Event('toggle'));
      fixture.detectChanges();

      expect(advancedSummary().getAttribute('aria-expanded'))
        .withContext('the announced state is read from where the browser keeps it')
        .toBe('true');
      expect(input(CONTROL_ID.serviceFee)).withContext('its fields are reachable').not.toBeNull();
    });

    it('offers the advanced toggle as a natively keyboard-reachable element', () => {
      createMode();

      expect(advancedSummary().tagName)
        .withContext('operable by Enter and Space with no scripting at all')
        .toBe('SUMMARY');
      expect(advancedSummary().getAttribute('tabindex'))
        .withContext('the legacy toggle withdrew itself from the tab order; this one does not')
        .toBeNull();
    });

    it('withdraws NOTHING from the tab order', () => {
      createMode();

      expect(queryAll('a[tabindex="-1"], button[tabindex="-1"], input[tabindex="-1"], select[tabindex="-1"], textarea[tabindex="-1"], summary[tabindex="-1"], [role="button"][tabindex="-1"]'))
        .withContext('the legacy help affordance and its image were both unreachable')
        .toHaveSize(0);
    });

    it('renders the basic section unconditionally, which is how "always expanded" is expressed', () => {
      createMode();

      const basic: HTMLElement = queryOrFail<HTMLElement>(host(), 'fieldset.role-form__section');

      expect(textOf(queryOrFail<Element>(basic, 'legend'))).toBe('Basic Settings');
      expect(basic.querySelector(`#${CONTROL_ID.roleName}`))
        .withContext('its content is present with no disclosure to operate')
        .not.toBeNull();
      expect(basic.getAttribute('aria-expanded'))
        .withContext('a grouping caption is not a disclosure widget, so it carries no state')
        .toBeNull();
    });

    it('emits NO table of any kind, because all three legacy tables were layout', () => {
      editMode(pricedRole(7));

      expect(queryAll('table')).toHaveSize(0);
      expect(queryAll('tr')).toHaveSize(0);
      expect(queryAll('td')).toHaveSize(0);
      expect(queryAll('th')).toHaveSize(0);
    });

    it('emits no break element and no non-breaking space', () => {
      editMode(pricedRole(7));

      expect(queryAll('br')).toHaveSize(0);
      expect(documentText())
        .withContext('the spacers and separators are stylesheet concerns now')
        .not.toContain('\u00a0');
    });

    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      createMode();

      expect(queryAll('main, nav, header, footer'))
        .withContext('each landmark is owned once, by the layout')
        .toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('never renders the module help entry, which has no home in the shared inventory', () => {
      createMode();

      expect(documentText()).not.toContain(MODULE_HELP_OPENING);
    });

    it('places the billing period and its frequency inside ONE labelled field', () => {
      createMode();

      expect(fieldOf(CONTROL_ID.billingPeriod))
        .withContext('one field, one caption, two controls')
        .toBe(fieldOf(CONTROL_ID.billingFrequency));
    });

    it('places the trial period and its frequency inside ONE labelled field', () => {
      createMode();

      expect(fieldOf(CONTROL_ID.trialPeriod)).toBe(fieldOf(CONTROL_ID.trialFrequency));
    });

    it('group-labels each paired field, so the select is named even without one of its own', () => {
      createMode();

      const paired: Element = fieldOf(CONTROL_ID.billingPeriod);
      const group: HTMLElement = queryOrFail<HTMLElement>(paired, '.form-field__control');

      expect(group.getAttribute('role')).toBe('group');
      expect(group.getAttribute('aria-labelledby'))
        .withContext('the group takes its name from the field caption')
        .not.toBeNull();
    });

    it('names every control with a real label pointing at it', () => {
      createMode();

      const targets: readonly string[] = queryAll<HTMLLabelElement>('label[for]').map(
        (label) => label.getAttribute('for') ?? '',
      );

      [
        CONTROL_ID.roleName,
        CONTROL_ID.description,
        CONTROL_ID.roleGroup,
        CONTROL_ID.isPublic,
        CONTROL_ID.autoAssignment,
        CONTROL_ID.serviceFee,
        CONTROL_ID.billingPeriod,
        CONTROL_ID.trialFee,
        CONTROL_ID.trialPeriod,
        CONTROL_ID.rsvpCode,
        CONTROL_ID.iconFile,
      ].forEach((controlId: string) => {
        expect(targets).withContext(`${controlId} is named`).toContain(controlId);
      });
    });

    it('renders the processor warning with real emphasis markup, not parsed resource text', () => {
      tenantResolved.set(true);
      processorConfigured.set(false);
      createMode();

      const emphasis: Element = queryOrFail<Element>(host(), '.role-form__warning strong');

      expect(textOf(emphasis)).toBe('Warning:');
      expect(documentText()).toContain('fee-base roles/services');
    });

    it('WITHHOLDS the processor warning until the tenant record resolves', () => {
      // Nothing is supplied, so the tenant stays unresolved: the state every visit began in while the fact
      // was an input nobody passed.
      createMode();

      expect(host().querySelector('.role-form__warning'))
        .withContext('no claim is made about a tenant nobody has read')
        .toBeNull();

      // Once the record lands with no processor, the reproduction is exact.
      tenantResolved.set(true);
      fixture.detectChanges();

      expect(host().querySelector('.role-form__warning')).not.toBeNull();
    });

    it('hides the processor warning once the tenant reports a configured processor', () => {
      editMode(role(7), { paymentProcessorConfigured: true });

      expect(host().querySelector('.role-form__warning')).toBeNull();
    });

    it('renders a hostile description as text, with no element parsed out of it', () => {
      // The runtime proof that no markup binding exists on this path. Resource and record text alike are
      // interpolated, so the framework escapes them.
      editMode(role(7, { description: '<img src=x onerror="document.title=1">' }));

      expect(queryAll('img')).withContext('no element is parsed out of a description').toHaveSize(0);
      expect(queryAll('script')).toHaveSize(0);
      expect(textArea().value)
        .withContext('the characters survive as characters')
        .toBe('<img src=x onerror="document.title=1">');
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      createMode();

      expect(queryOrFail<Element>(host(), '.error-banner-live').getAttribute('role')).toBe('alert');
      expect(host().querySelector('.error-banner__title'))
        .withContext('mounted, but saying nothing')
        .toBeNull();
    });
  });

  // ===================================================================================================
  // BEYOND THE THIRTEEN — THE REMAINING CONTRACT MEMBERS AND THE DROPPED AFFORDANCES
  // ===================================================================================================

  // AREA 14 — WHOSE WRITE SETTLED, AND WHO CLASSIFIES A REFUSAL
  // WRITE IDENTITY. The shared store published ONE boolean for "a write is in flight" and ONE failure slot.

  describe('AREA 14 — write identity and severity ownership', () => {
    it('stays held when an unrelated role write settles first', () => {
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const creation: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');

      expect(command(SUBMIT_LABEL)?.disabled).withContext('held while ours is open').toBeTrue();

      // A sibling screen's write, dispatched straight at the shared store and settled while ours is
      // still in the air.
      const store: RoleStore = TestBed.inject(RoleStore);

      store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });
      expectRequest('POST', ROLE_GROUPS_URL, 'the sibling write').flush(
        envelope(roleGroup(3)),
        { status: 201, statusText: 'Created' },
      );
      expectRequest('GET', ROLE_GROUPS_URL, 'the sibling re-read').flush(envelope([roleGroup()]));
      fixture.detectChanges();

      expect(command(SUBMIT_LABEL)?.disabled)
        .withContext('another screen\u2019s write must not release our submit lock')
        .toBeTrue();
      expect(announcements())
        .withContext('and must not be reported as the outcome of ours')
        .toEqual([]);

      creation.flush(envelope(role()), { status: 201, statusText: 'Created' });
      expectNoListingReread();

      expect(announcements()).toContain({ severity: 'success', message: ROLE_CREATED_MESSAGE });
    });

    it('does not report an unrelated write\u2019s refusal as its own outcome', () => {
      // ⚠ THE SHARED FAILURE SLOT HOLDS THE SIBLING'S REFUSAL at the moment our write succeeds. The
      // outcome travels ON the settled result instead, so ours is reported as the success it was.
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      const creation: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');
      const store: RoleStore = TestBed.inject(RoleStore);

      store.createRoleGroup({ roleGroupName: 'Paid Services', description: null });
      expectRequest('POST', ROLE_GROUPS_URL, 'the sibling write').flush(
        problem(409, 'role_group.duplicate_name', { detail: 'That group already exists.' }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      creation.flush(envelope(role()), { status: 201, statusText: 'Created' });
      expectNoListingReread();

      expect(announcements())
        .withContext('exactly one announcement, and it is ours')
        .toEqual([{ severity: 'success', message: ROLE_CREATED_MESSAGE }]);
    });

    it('presents a rate-limit refusal at the shared classification, not at its own', () => {
      // ⚠ THE DISAGREEMENT THIS CLOSES. The local table resolved every status other than 404 to `error`, so
      // a 429 was announced as a failure on this screen while the shared classifier calls it `info` —
      // nothing was rejected on its merits, the caller is simply early.
      createMode();
      fillRoleName();

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(429, 'rate_limited', { detail: 'Too many attempts. Try again shortly.' }),
        { status: 429, statusText: 'Too Many Requests' },
      );
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({
        severity: 'info',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(bannerMessage()).toBe('Too many attempts. Try again shortly.');
    });

    it('presents a missing role at the shared classification, which agrees with the legacy', () => {
      // 404 is the one status the local table already got right, and it must keep being right for the
      // shared reason rather than for a local one.
      editMode(role(7));

      press(SUBMIT_LABEL);

      expectRequest('PUT', roleUrl(7), 'the update').flush(
        problem(404, 'role.not_found', { detail: 'That role no longer exists.' }),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({
        severity: 'warning',
        message: SAVE_FAILED_MESSAGE,
      });
      expect(bannerMessage()).toBe('That role no longer exists.');
    });
  });

  describe('the remaining members and the dropped affordances', () => {
    it('round-trips the reservation code and the icon path, which both contracts declare', () => {
      editMode(role(7, { rsvpCode: 'JOIN-2024', iconFile: 'images/role.gif' }));

      expect(input(CONTROL_ID.rsvpCode).value).toBe('JOIN-2024');
      expect(input(CONTROL_ID.iconFile).value).toBe('images/role.gif');

      press(SUBMIT_LABEL);

      const body: UpdateRoleRequest = submittedUpdate(7);

      expect(body.rsvpCode).toBe('JOIN-2024');
      expect(body.iconFile).toBe('images/role.gif');
    });

    it('sends an emptied optional field as nothing rather than as empty text', () => {
      editMode(role(7, { rsvpCode: 'JOIN-2024' }));

      type(CONTROL_ID.rsvpCode, '');

      press(SUBMIT_LABEL);

      expect(submittedUpdate(7).rsvpCode).toBeNull();
    });

    it('offers no reservation LINK at all, which was dropped unconditionally', () => {
      editMode(role(7, { rsvpCode: 'JOIN-2024' }));

      expect(host().querySelector('#role-form-rsvp-link')).toBeNull();
      expect(documentText()).not.toContain('?rsvp=');
    });

    it('refuses an icon reference that escapes the portal\u2019s own folder', () => {
      createMode();
      fillRoleName();

      type(CONTROL_ID.iconFile, '../../secrets.gif');

      expect(messagesFor(CONTROL_ID.iconFile))
        .withContext('the rule mirrors the API, so no round trip is spent learning it')
        .not.toEqual([]);
    });

    it('carries the two flags through as the booleans they are', () => {
      editMode(role(7, { isPublic: true, autoAssignment: true }));

      expect(input(CONTROL_ID.isPublic).checked).toBeTrue();
      expect(input(CONTROL_ID.autoAssignment).checked).toBeTrue();

      press(SUBMIT_LABEL);

      const body: UpdateRoleRequest = submittedUpdate(7);

      expect(body.isPublic).toBeTrue();
      expect(body.autoAssignment).toBeTrue();
    });

    it('hydrates the whole record into the form, including its grouping', () => {
      editMode(pricedRole(7, { roleName: 'Gold', description: 'Premium tier', roleGroupId: 4 }), {
        groups: [roleGroup(4, { roleGroupName: 'Paid Services' })],
      });

      expect(textOf(queryOrFail<Element>(host(), `output#${CONTROL_ID.roleName}`))).toBe('Gold');
      expect(textArea().value).toBe('Premium tier');
      expect(chosenLabel(CONTROL_ID.roleGroup)).toBe('Paid Services');
      expect(input(CONTROL_ID.serviceFee).value).toBe('25.00');
    });
  });

  // AREA 15 — NUMERIC INTEGRITY, THE REVISION MARKER, AND THE RECOVERY PATH
  // 1. `1,5` in a fee field was accepted, the separator was stripped, and `15` was persisted — a TEN-FOLD
  // monetary error, silent end to end.

  describe('AREA 15 — numeric integrity, the revision marker and the recovery path', () => {
    /** Puts a role in edit mode with its billing group loaded, then reveals the advanced section. */
    function openAdvanced(): HTMLDetailsElement {
      const section: HTMLDetailsElement = queryOrFail<HTMLDetailsElement>(
        host(),
        'details.role-form__section--advanced',
      );

      section.open = true;
      fixture.detectChanges();

      return section;
    }

    describe('the values the legacy bind withholds are STATED rather than hidden', () => {
      /**
       * The sentence the advanced section prints above the paid-membership boxes, or ''.
       *
       * Located by the phrase every arm of the notice shares. It used to be located by the prefix
       * `'This role has no'`, which is exactly the assertion-of-absence the finding calls factually wrong,
       * so the helper would have gone on reporting '' after the wording was corrected.
       */
      function withheldNotice(): string {
        const notices: readonly string[] = textsOf('p.role-form__notice');

        return notices.find((sentence) => sentence.includes('stay empty')) ?? '';
      }

      it('states every withheld value, in the wording the role listing uses', () => {
        editMode(role(0));
        openAdvanced();

        expect(withheldNotice()).toBe(
          'The paid-membership boxes below stay empty. The legacy editor filled the billing boxes only '
            + 'for a role with a service fee to charge, and the trial boxes only for a trial frequency it '
            + 'recognised, and this role meets neither condition. The values stored for it are Service Fee '
            + '0.00, Billing Period 0, Trial Fee 0.00 and Trial Period 0, and saving this form replaces '
            + 'them.',
        );
        // And the boxes themselves are untouched, which is the half of this the legacy owns.
        expect(input(CONTROL_ID.serviceFee).value).toBe('');
        expect(input(CONTROL_ID.billingPeriod).value).toBe('');
        expect(input(CONTROL_ID.trialFee).value).toBe('');
        expect(input(CONTROL_ID.trialPeriod).value).toBe('');
        expect(allMessages()).withContext('nothing opened invalid').toEqual([]);
        expect(command('Update')?.disabled).withContext('and the command is live').toBeFalse();
      });

      it('states a withheld period of one, because a stored 1 is as invisible as a stored 0', () => {
        // The shipped `Subscribers` role holds `billingPeriod 1` beside a frequency of None, so its
        // boxes are blank for the same reason and its stored values are not zeros at all.
        editMode(role(2, { billingPeriod: 1, trialPeriod: 1 }));
        openAdvanced();

        expect(withheldNotice()).toContain('Billing Period 1');
        expect(withheldNotice()).toContain('Trial Period 1');
      });

      it('names the RECURRENCE UNIT a zero fee hides, which nothing else on the screen shows', () => {
        editMode(role(7, { serviceFee: 0, billingFrequency: 'M', billingPeriod: 3 }));
        openAdvanced();

        expect(withheldNotice()).toContain('Billing Period 3');
        expect(withheldNotice()).toContain('Billing Frequency Month');
        expect(withheldNotice())
          .withContext('the trial select shows its own stored value, so nothing is withheld from it')
          .not.toContain('Trial Frequency');
      });

      it('says nothing at all when both groups carry their values themselves', () => {
        // Priced AND on trial, so neither group is suppressed and every box holds the record.
        editMode(
          role(7, {
            serviceFee: 25,
            billingPeriod: 1,
            billingFrequency: 'M',
            trialFee: 5,
            trialPeriod: 2,
            trialFrequency: 'W',
          }),
        );
        openAdvanced();

        expect(withheldNotice()).withContext('nothing is being withheld').toBe('');
        expect(input(CONTROL_ID.serviceFee).value).toBe('25.00');
        expect(input(CONTROL_ID.trialPeriod).value).toBe('2');
      });

      it('describes only the TRIAL group when the billing group carries its own values', () => {
        editMode(role(7, { serviceFee: 25, billingPeriod: 1, billingFrequency: 'M' }));
        openAdvanced();

        expect(withheldNotice()).toBe(
          'The trial boxes below stay empty. The legacy editor filled them only for a trial frequency it '
            + 'recognises, and this role stores none it can name. The values stored for it are Trial Fee '
            + '0.00 and Trial Period 0, and saving this form replaces them.',
        );
        expect(input(CONTROL_ID.serviceFee).value).toBe('25.00');
      });

      it('never denies a trial in the same breath as reporting the trial values it holds', () => {
        // THE DISCRIMINATING CASE for the wording half of this finding. A role storing real trial values
        // whose trial FREQUENCY the console cannot name fails the legacy bind gate, so the boxes stay
        // empty - and the sentence explaining that used to open by asserting the role "has no trial"
        // before listing Trial Fee 5.00 and Trial Period 2. One sentence, two contradictory clauses.
        // PRICED deliberately, so that ONLY the trial group is withheld and this case pins the trial arm of
        // the lead rather than the both-groups arm. Without that the case passes under the old wording too.
        editMode(
          role(7, {
            serviceFee: 25,
            billingPeriod: 1,
            billingFrequency: 'M',
            trialFee: 5,
            trialPeriod: 2,
            trialFrequency: 'X' as never,
          }),
        );
        openAdvanced();

        const notice: string = withheldNotice();

        expect(notice).withContext('the values are still reported').toContain('Trial Fee 5.00');
        expect(notice).toContain('Trial Period 2');
        expect(notice).withContext('but no absence is asserted').not.toContain('has no trial');
        expect(notice).not.toContain('This role has no');
      });

      it('names a stored frequency code it cannot set, and states what saving records instead', () => {
        // `coerceFrequency` maps any code outside the six onto 'N', and the outgoing write initialises the
        // frequency to 'N' as well, so this stored 'Q' is REPLACED by saving. The notice previously stated
        // only a frequency it could name, so the one value whose loss was certain went unmentioned.
        editMode(role(7, { serviceFee: 0, billingPeriod: 3, billingFrequency: 'Q' as never }));
        openAdvanced();

        const notice: string = withheldNotice();

        expect(notice).toContain('Billing Frequency Q');
        expect(notice).toContain('is not among the frequencies this console can set');
        expect(notice).toContain('saving records None in its place');
      });

      it('names BOTH unnameable codes, in the plural, when each group stores one', () => {
        editMode(
          role(7, {
            serviceFee: 0,
            billingFrequency: 'Q' as never,
            trialFee: 5,
            trialFrequency: 'X' as never,
          }),
        );
        openAdvanced();

        const notice: string = withheldNotice();

        expect(notice).toContain('Billing Frequency Q');
        expect(notice).toContain('Trial Frequency X');
        expect(notice).toContain('are not among the frequencies this console can set');
        expect(notice).toContain('saving records None in their place');
      });

      it('says nothing about a replacement when the stored code is one it CAN set', () => {
        // The narrowing guard: a nameable code loses nothing, because the select can carry it back, so no
        // replacement sentence is owed and inventing one would alarm without cause.
        editMode(role(7, { serviceFee: 0, billingPeriod: 3, billingFrequency: 'M' }));
        openAdvanced();

        expect(withheldNotice()).not.toContain('is not among the frequencies this console can set');
      });

      it('withholds the sentence on the creation form, where there is no record to describe', () => {
        createMode();
        openAdvanced();

        expect(withheldNotice()).toBe('');
      });

      it('leaves out a value the record genuinely does not hold', () => {
        // An ABSENT amount is not a withheld one. The money sentinel formats to nothing on both screens,
        // and a caption with nothing after it would invent a value the record has never held.
        editMode(role(7, { serviceFee: null, trialFee: null }));
        openAdvanced();

        expect(withheldNotice()).not.toContain('Service Fee');
        expect(withheldNotice()).not.toContain('Trial Fee');
        expect(withheldNotice()).toContain('Billing Period 0');
      });
    });

    describe('a thousands separator must separate thousands', () => {
      it('refuses "1,5" in a fee field with the legacy data-type wording, and sends nothing', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.serviceFee, '1,5');
        press('Update');

        expect(messagesFor(CONTROL_ID.serviceFee)).toContain(
          'Service Fee Value Entered Is Not Valid',
        );
        // No new sentence is invented for it: the legacy resource already had words for an invalid
        // currency value, and those are the words shown.
        expect(announcements()).toEqual([]);
      });

      it('still accepts the grouped form the screen itself writes', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.serviceFee, '1,234.56');
        type(CONTROL_ID.billingPeriod, '1');
        choose(CONTROL_ID.billingFrequency, 'Month');
        press('Update');

        // The whole point of tolerating separators at all: a role loaded with a four-figure fee is
        // rendered `1,234.56`, and a form that refused it would be invalid before anyone touched it.
        expect(submittedCreate().serviceFee).toBe(1234.56);
      });

      it('refuses a group that is neither absent nor three digits', () => {
        createMode();
        fillRoleName();
        openAdvanced();

        for (const malformed of ['1,23', '1,2345', '12,34,567', '1,']) {
          type(CONTROL_ID.serviceFee, malformed);

          expect(messagesFor(CONTROL_ID.serviceFee))
            .withContext(malformed)
            .toContain('Service Fee Value Entered Is Not Valid');
        }
      });
    });

    describe('a value the store cannot hold is refused here, beside the field', () => {
      it('refuses a period beyond Int32 and accepts the largest one that fits', () => {
        createMode();
        fillRoleName();
        openAdvanced();

        type(CONTROL_ID.billingPeriod, '2147483648');

        expect(messagesFor(CONTROL_ID.billingPeriod)).toContain(
          'That number is outside the range this site can store. Enter a whole number between ' +
            '-2,147,483,648 and 2,147,483,647.',
        );

        type(CONTROL_ID.billingPeriod, '2147483647');

        expect(messagesFor(CONTROL_ID.billingPeriod)).toEqual([]);
      });

      it('refuses an amount beyond what the money column can hold', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.serviceFee, '123456789012345678901');

        expect(messagesFor(CONTROL_ID.serviceFee)).toContain(
          'That amount is outside the range this site can store. Enter an amount between ' +
            '-922,337,203,685,477.58 and 922,337,203,685,477.58.',
        );
      });

      it('refuses an amount that would arrive with different digits from the ones typed', () => {
        createMode();
        fillRoleName();
        openAdvanced();

        // Inside the column's range, and still not carryable: a double cannot hold this to the cent,
        // so the value that would be STORED is not the value that was TYPED.
        type(CONTROL_ID.serviceFee, '922337203685477.58');

        expect(messagesFor(CONTROL_ID.serviceFee)).toContain(
          'That amount has more digits than can be stored without rounding. Enter a shorter amount.',
        );
      });

      it('leaves an ordinary two-decimal amount alone', () => {
        createMode();
        fillRoleName();
        openAdvanced();

        for (const ordinary of ['0', '0.00', '9.99', '100000000000000.00', '1,234,567.89']) {
          type(CONTROL_ID.serviceFee, ordinary);

          expect(messagesFor(CONTROL_ID.serviceFee)).withContext(ordinary).toEqual([]);
        }
      });

      // The legacy sentence wins wherever it applies. A negative fee breaks `valServiceFee2` as well as
      // nothing else, and the wording a person sees must be the one this screen has always used.
      it('reports the legacy comparison wording, not a storability sentence, for a negative fee', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.serviceFee, '-49.95');

        expect(messagesFor(CONTROL_ID.serviceFee)).toEqual([
          'Service Fee Must Be Greater Than or Equal to Zero',
        ]);
      });
    });

    describe('normalisation happens where it can be seen', () => {
      it('shows a typed fee back in the form it will be stored in', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.serviceFee, '007');

        const box: HTMLInputElement = input(CONTROL_ID.serviceFee);
        box.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        expect(box.value).toBe('7.00');
      });

      it('shows a typed period back without its leading zeros', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.billingPeriod, '007');

        const box: HTMLInputElement = input(CONTROL_ID.billingPeriod);
        box.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        expect(box.value).toBe('7');
      });

      it('leaves text that will not parse exactly as it was typed', () => {
        createMode();
        fillRoleName();
        openAdvanced();
        type(CONTROL_ID.serviceFee, '1,5');

        const box: HTMLInputElement = input(CONTROL_ID.serviceFee);
        box.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        // A person correcting a mistake needs to see the mistake. There is also no canonical form of
        // something that is not a number.
        expect(box.value).toBe('1,5');
      });

      it('leaves a loaded, untouched field completely alone', () => {
        editMode(pricedRole(7));

        const box: HTMLInputElement = input(CONTROL_ID.serviceFee);
        const loaded: string = box.value;

        box.dispatchEvent(new Event('blur'));
        fixture.detectChanges();

        // ⚠ THIS PINS AN INVARIANT RATHER THAN EXERCISING A BRANCH, and saying so is the honest
        // description. A loaded fee is written into the control BY `formatMoney` (`applyRole`), so it is
        // already in canonical form and would survive a rewrite unchanged in any case.
        expect(box.value).toBe(loaded);
        expect(queryOrFail<HTMLFormElement>(host(), 'form').classList).toContain('ng-pristine');
      });
    });

    describe('a refused submit reaches the control that caused it', () => {
      it('opens the advanced section and focuses the offending field', () => {
        // ⚠ THE FIXTURE WITHHOLDS NOTHING, DELIBERATELY AND EXPLICITLY. The default `role()` stores a
        // service fee of nought, which is itself a withheld value, and the section now opens ITSELF
        // whenever there is a withheld-values warning to read. Leaving the default here would have started
        // the section OPEN and quietly stopped this case exercising the reveal it exists to prove. Every
        // paid value is therefore absent, which is the one state that produces no notice.
        editMode(
          role(7, {
            serviceFee: null,
            billingPeriod: null,
            billingFrequency: null,
            trialFee: null,
            trialPeriod: null,
            trialFrequency: null,
          }),
        );

        const section: HTMLDetailsElement = queryOrFail<HTMLDetailsElement>(
          host(),
          'details.role-form__section--advanced',
        );

        // The section starts CLOSED, which is the whole hazard: four of the twelve controls live inside
        // it, and a control inside closed disclosure content cannot take focus.
        expect(section.open).toBeFalse();

        section.open = true;
        fixture.detectChanges();
        type(CONTROL_ID.billingPeriod, '0');
        section.open = false;
        fixture.detectChanges();

        press('Update');

        expect(section.open).toBeTrue();
        expect(document.activeElement).toBe(input(CONTROL_ID.billingPeriod));
      });
    });

    /**
     * R4, SECOND ROUND — THE CORRECTIONS RUNTIME TESTING FORCED. The first attempt tied the
     * unnameable-code disclosure to the withheld-boxes condition, and browser measurement showed that to be
     * the wrong hinge entirely: the rewrite happens because the SELECT cannot represent the code, which is
     * true whether or not the fee boxes were populated. Two roles proved it — one priced at 249.50 storing
     * `'Q'` received no notice whatsoever, and one storing both `'Z'` and `'X'` disclosed only the `'X'`.
     */
    describe('disclosing a stored frequency code the console cannot set', () => {
      /** The notice the advanced section prints above the paid-membership boxes, or '' when silent. */
      function notice(): string {
        return (
          queryAll<HTMLParagraphElement>('details.role-form__section--advanced p.role-form__notice')[0]
            ?.textContent ?? ''
        ).trim();
      }

      /**
       * THE CASE THAT WAS SILENT. A priced role fills its billing boxes, so it withholds nothing and the
       * notice used to be suppressed outright — while its stored `'Q'` was destroyed on save regardless.
       */
      it('speaks for a PRICED role whose billing code cannot be represented', () => {
        // ⚠ EVERY TRIAL VALUE IS ABSENT, AND THAT IS WHAT MAKES THIS CASE BITE. The fixture default stores a
        // trial fee of nought, which formats to "0.00" and is itself a withheld term - so the notice would
        // have been rendered by the withheld list alone and this case would have passed without ever
        // reaching the code under test. Absent trial values withhold NOTHING, which is exactly the state of
        // the role measured in the browser: priced at 249.50, storing 'Q', and warned about nowhere.
        editMode(
          role(7, {
            serviceFee: 249.5,
            billingPeriod: 1,
            billingFrequency: 'Q',
            trialFee: null,
            trialPeriod: null,
            trialFrequency: null,
          }),
        );

        expect(notice()).withContext('a notice is rendered at all').not.toBe('');
        expect(notice()).toContain('Billing Frequency Q');
        expect(notice())
          .withContext('and it names the value that replaces it')
          .toContain(`saving records ${NO_FREQUENCY_LABEL} in its place`);
      });

      /** BOTH codes are doomed, so both must be named. Only the trial one used to be. */
      it('names EVERY unrepresentable code, not merely the one in a withheld group', () => {
        editMode(
          role(7, {
            serviceFee: 5,
            billingPeriod: 2,
            billingFrequency: 'Z',
            trialFrequency: 'X',
          }),
        );

        expect(notice()).toContain('Billing Frequency Z');
        expect(notice()).toContain('Trial Frequency X');
        expect(notice())
          .withContext('and reads as a plural, since two codes are named')
          .toContain('The stored codes');
      });

      /**
       * ⚠ THE NARROWING CASE. A recognised code loses nothing when the write records it back, so it must
       * NOT be reported as rewritten. An implementation that reported every frequency would satisfy both
       * cases above and cry wolf on every priced role in the portal.
       */
      it('says nothing about a code the console CAN set', () => {
        editMode(
          role(7, {
            serviceFee: 19.99,
            billingPeriod: 1,
            billingFrequency: 'M',
            trialFrequency: 'D',
            trialPeriod: 14,
            trialFee: 0,
          }),
        );

        expect(notice()).not.toContain('not among the frequencies');
        expect(notice()).not.toContain('The stored code');
      });

      /**
       * R4's FIRST defect, on the one branch a browser round found still carrying it: the billing-only lead
       * asserted "and this role has none" while the sentence after it listed the record's own stored values.
       */
      it('never asserts an absence, on any lead', () => {
        // ⚠ ONE ROLE PER LEAD, AND THE THIRD IS THE ONE THAT MATTERS. The billing-only lead is the branch
        // that still carried the absence assertion, and it is reached ONLY by a role that is unpriced (so
        // its billing boxes are withheld) whose trial frequency IS recognised (so its trial boxes are not).
        // Without that third shape this case exercised the both-groups and trial leads twice over and could
        // not have failed.
        for (const subject of [
          // both groups withheld
          role(7, { serviceFee: 0, billingPeriod: 0, billingFrequency: 'N' }),
          // trial only
          role(7, { serviceFee: 5, billingPeriod: 1, billingFrequency: 'M', trialFee: 2 }),
          // BILLING ONLY
          role(7, {
            serviceFee: 0,
            billingPeriod: 2,
            billingFrequency: 'M',
            trialFrequency: 'D',
            trialPeriod: 7,
            trialFee: 3,
          }),
        ]) {
          editMode(subject);

          expect(notice().toLowerCase())
            .withContext('no lead asserts what the record does not hold')
            .not.toContain('has no');
        }
      });

      /**
       * ⚠ THE WARNING MUST BE ON SCREEN BEFORE THE COMMAND THAT ACTS ON IT. Update sits OUTSIDE this
       * disclosure and is operable without expanding it, so a collapsed section let the destructive save run
       * with the warning never once visible. Measured in the browser: the section renders collapsed by
       * default and Update is fully reachable.
       */
      it('reveals itself, rather than hiding inside a collapsed disclosure', () => {
        editMode(role(7, { serviceFee: 249.5, billingPeriod: 1, billingFrequency: 'Q' }));

        const section: HTMLDetailsElement = queryOrFail<HTMLDetailsElement>(
          host(),
          'details.role-form__section--advanced',
        );

        expect(notice()).not.toBe('');
        expect(section.open).withContext('the section holding the warning is open').toBeTrue();
        expect(command('Update'))
          .withContext('and the command the warning is about is offered')
          .toBeDefined();
      });

      /** And it is announced, since it arrives only after the record lands. */
      it('announces the warning through a polite live region', () => {
        editMode(role(7, { serviceFee: 249.5, billingPeriod: 1, billingFrequency: 'Q' }));

        const element = queryAll<HTMLParagraphElement>(
          'details.role-form__section--advanced p.role-form__notice',
        )[0];

        expect(element?.getAttribute('aria-live')).toBe('polite');
      });
    });

    describe('the revision marker', () => {
      it('carries the token from the read into the update', () => {
        editMode(role(7, { concurrencyToken: 'revision-from-the-read' }));
        type(CONTROL_ID.description, 'Edited');
        press('Update');

        // The token must come from the role that was READ. Re-reading it before a save would obtain the
        // CURRENT revision, and the check would then always pass while looking watertight.
        expect(submittedUpdate(7).concurrencyToken).toBe('revision-from-the-read');
      });

      it('always carries a token on an update, because a read serving none is refused', () => {
        editMode(role(7, { concurrencyToken: 'revision-2' }));
        type(CONTROL_ID.description, 'Edited');
        press('Update');

        const sent: string | null = submittedUpdate(7).concurrencyToken;

        expect(typeof sent)
          .withContext('a marker, never a null this screen invented and never one it omitted')
          .toBe('string');
        expect(sent).toBe('revision-2');
      });

      it('carries the empty string through, because that is the server unset spelling', () => {
        editMode(role(7, { concurrencyToken: '' }));
        type(CONTROL_ID.description, 'Edited');
        press('Update');

        expect(submittedUpdate(7).concurrencyToken).toBe('');
      });

      it('sends no token on a creation, because there is no prior revision', () => {
        createMode();
        fillRoleName();
        press('Update');

        expect(submittedCreate()).not.toEqual(
          jasmine.objectContaining({ concurrencyToken: jasmine.anything() }),
        );
      });
    });

    describe('a conflict is recoverable without leaving the application', () => {
      /** Refuses the pending update the way the API refuses a stale one. */
      function refuseAsStale(roleId: number): void {
        expectRequest('PUT', roleUrl(roleId), 'the update').flush(
          {
            type: 'urn:dnnmigration:error:role.concurrency_conflict',
            title: 'Conflict',
            status: 409,
            detail:
              `Role ${roleId} was changed by someone else after you read it, so nothing was ` +
              `written. Reload the role to see the current values, then apply your change again.`,
          },
          { status: 409, statusText: 'Conflict' },
        );
        fixture.detectChanges();
      }

      it('offers a re-read, announces the server sentence, and stays on the screen', () => {
        editMode(role(7, { concurrencyToken: 'stale' }));
        type(CONTROL_ID.description, 'Mine');
        press('Update');
        refuseAsStale(7);

        expect(bannerMessage() ?? '').toContain('was changed by someone else');
        expect(lastAnnouncement()).toEqual({ severity: 'error', message: SAVE_FAILED_MESSAGE });
        expect(navigateSpy).not.toHaveBeenCalled();

        const reload: HTMLButtonElement = queryOrFail<HTMLButtonElement>(
          host(),
          'button.role-form__conflict-reload',
        );

        // Outside the form and explicitly not a submit, because a button inside a form submits it.
        expect(reload.type).toBe('button');
        expect(reload.closest('form')).toBeNull();
        expect(documentText()).toContain('Anything you have typed here and not saved will be replaced');
      });

      it('does not offer a re-read for a duplicate name, which is corrected in a field', () => {
        createMode();
        fillRoleName('Administrators');
        press('Update');

        expectRequest('POST', ROLES_URL, 'the creation').flush(
          {
            type: 'urn:dnnmigration:error:role.name_duplicate',
            title: 'Conflict',
            status: 409,
            detail: 'A role with that name already exists.',
          },
          { status: 409, statusText: 'Conflict' },
        );
        fixture.detectChanges();

        // The two 409s are different failures. Keying on the published code rather than the status is
        // what keeps them apart.
        expect(host().querySelector('button.role-form__conflict-reload')).toBeNull();
      });

      it('replaces the stale values on re-read and withdraws the recovery affordance', () => {
        editMode(role(7, { description: 'Stored', concurrencyToken: 'stale' }));
        type(CONTROL_ID.description, 'Mine');
        press('Update');
        refuseAsStale(7);

        queryOrFail<HTMLButtonElement>(host(), 'button.role-form__conflict-reload').click();
        fixture.detectChanges();

        const reread: TestRequest = expectRequest('GET', roleUrl(7), 'the re-read');

        reread.flush(
          envelope(role(7, { description: 'Theirs', concurrencyToken: 'revision-2' })),
        );
        fixture.detectChanges();

        // The re-read must actually reach the form. `appliedRoleKey` exists to stop a second arrival of the
        // same role overwriting typed values, which is right for the store's echo after a save and wrong
        // here, where replacing them is the entire purpose of the command.
        expect(textArea().value).toBe('Theirs');
        expect(host().querySelector('button.role-form__conflict-reload')).toBeNull();

        // And the next save carries the NEW revision, so the recovery actually recovers.
        type(CONTROL_ID.description, 'Mine again');
        press('Update');

        expect(submittedUpdate(7).concurrencyToken).toBe('revision-2');
      });
    });
  });

  // AREA 16 — NAMING A PAIR, DISCLOSING A TRUNCATION, AND STATING A BUSY FORM
  // Three findings whose common shape is that the screen already behaved correctly and said nothing about
  // it, so nothing an operator or a screen reader could perceive distinguished the right state from the
  // wrong one.

  describe('AREA 16 — naming a pair, disclosing a truncation, and stating a busy form', () => {
    /** Reveals the advanced section, where both period pairs live. */
    function revealAdvanced(): void {
      queryOrFail<HTMLDetailsElement>(host(), 'details.role-form__section--advanced').open = true;
      fixture.detectChanges();
    }

    /** The accessible name a control resolves to, composed from its `aria-labelledby` references. */
    function accessibleNameOf(controlId: string): string {
      const control: HTMLElement = queryOrFail<HTMLElement>(host(), `#${controlId}`);
      const references: string = control.getAttribute('aria-labelledby') ?? '';

      return references
        .split(/\s+/)
        .filter((id: string): boolean => id.length > 0)
        .map((id: string): string => (host().querySelector(`#${id}`)?.textContent ?? '').trim())
        .join(' ')
        .replace(/\s+/g, ' ')
        .trim();
    }

    describe('R-M8 — each half of a period pair is named distinctly', () => {
      it('gives the count and the unit different names on both pairs', () => {
        createMode();
        revealAdvanced();

        expect(accessibleNameOf(CONTROL_ID.billingPeriod)).toBe('Billing Period (Every) count');
        expect(accessibleNameOf(CONTROL_ID.billingFrequency)).toBe('Billing Period (Every) unit');
        expect(accessibleNameOf(CONTROL_ID.trialPeriod)).toBe('Trial Period (Every) count');
        expect(accessibleNameOf(CONTROL_ID.trialFrequency)).toBe('Trial Period (Every) unit');

        // All four are distinct, which is the property the finding was about rather than any
        // particular wording.
        const names: readonly string[] = [
          accessibleNameOf(CONTROL_ID.billingPeriod),
          accessibleNameOf(CONTROL_ID.billingFrequency),
          accessibleNameOf(CONTROL_ID.trialPeriod),
          accessibleNameOf(CONTROL_ID.trialFrequency),
        ];

        expect(new Set(names).size).withContext('four controls, four names').toBe(4);
      });

      /**
       * ⚠ WCAG 2.5.3 LABEL IN NAME. The qualifier is APPENDED, so the text an operator can read remains a
       * prefix of the name an assistive technology announces — which is what lets someone using voice
       * control say the visible words and reach the control.
       */
      it('keeps the visible label as a prefix of each name', () => {
        createMode();
        revealAdvanced();

        const visible: string = (
          queryOrFail<HTMLLabelElement>(
            host(),
            `label[for="${CONTROL_ID.billingPeriod}"]`,
          ).textContent ?? ''
        )
          .replace(/\s+/g, ' ')
          .trim();

        expect(visible).toBe('Billing Period (Every)');
        expect(accessibleNameOf(CONTROL_ID.billingPeriod).startsWith(visible)).toBeTrue();
        expect(accessibleNameOf(CONTROL_ID.billingFrequency).startsWith(visible)).toBeTrue();
      });

      /**
       * ⚠ THE QUALIFIERS MUST NOT BE DRAWN. They exist to be announced; painting them would put two stray
       * words into a field whose visible wording is a resource value this migration preserves. The
       * clipping helper is asserted through the rendered class, because the rule that hides it lives in a
       * stylesheet the test bed does not apply.
       */
      it('never paints the qualifiers into the field', () => {
        createMode();
        revealAdvanced();

        const qualifiers: readonly Element[] = Array.from(
          host().querySelectorAll('.role-form__qualifier'),
        );

        expect(qualifiers.length).withContext('one per sub-control, four in all').toBe(4);
        expect(qualifiers.map((node) => (node.textContent ?? '').trim())).toEqual([
          'count',
          'unit',
          'count',
          'unit',
        ]);
      });
    });

    describe('R-M18 — a name held at its limit says so', () => {
      /** A name of exactly the stored maximum. */
      const AT_LIMIT = 'X'.repeat(50);

      /** The notice, or `null` when the form is not disclosing one. */
      function lengthNotice(): string | null {
        const notice: Element | null = host().querySelector('p.role-form__notice[aria-live]');

        return notice === null ? null : (notice.textContent ?? '').trim();
      }

      /**
       * ⚠ THE CASE THAT NAMES THE DEFECT. `maxlength` is faithful — `editroles.ascx:L31` declares
       * `MaxLength="50"` — and a browser enforcing it discards the surplus with no indication at all.
       */
      it('discloses the limit once the name reaches it', () => {
        createMode();

        expect(lengthNotice()).withContext('an empty field claims nothing').toBeNull();

        type(CONTROL_ID.roleName, AT_LIMIT);

        expect(lengthNotice()).toContain('Maximum length reached');
        expect(lengthNotice())
          .withContext('the limit itself is stated, so the operator knows what was dropped')
          .toContain('50 characters');
      });

      /**
       * ⚠ AND IT MUST BE SILENT BELOW THE LIMIT. A notice standing permanently beside a field is a notice
       * nobody reads, and this one earns its place only by appearing at the moment the field stops
       * accepting input.
       */
      it('says nothing while the name is shorter than the limit', () => {
        createMode();
        type(CONTROL_ID.roleName, 'X'.repeat(49));

        expect(lengthNotice()).toBeNull();
      });

      /**
       * ⚠ IT IS A NOTICE AND NOT AN ERROR. The value at the limit is VALID — `maxLength(50)` is satisfied
       * by exactly fifty — so it must not appear in the field's error region, must not be announced
       * assertively, and must not mark the control invalid. Conflating the two would train an operator to
       * treat a real refusal as noise.
       */
      it('does not report the limit as a validation failure', () => {
        createMode();
        type(CONTROL_ID.roleName, AT_LIMIT);

        expect(messagesFor(CONTROL_ID.roleName)).toEqual([]);
        expect(
          queryOrFail<HTMLInputElement>(host(), `#${CONTROL_ID.roleName}`).getAttribute(
            'aria-invalid',
          ),
        ).toBeNull();
        expect(
          queryOrFail<HTMLElement>(host(), 'p.role-form__notice[aria-live]').getAttribute(
            'aria-live',
          ),
        )
          .withContext('polite: reaching a limit is not an interruption')
          .toBe('polite');
      });
    });

    describe('R-M21 — a form mid-save states that it is busy', () => {
      /** The form element's `aria-busy`, or `null`. */
      function busy(): string | null {
        return queryOrFail<HTMLFormElement>(host(), 'form.role-form__form').getAttribute(
          'aria-busy',
        );
      }

      /**
       * ⚠ THE CASE THAT NAMES THE DEFECT, AND ITS SUBJECT IS `aria-busy` ALONE. Every one of those
       * presses found the button enabled, because no change-detection pass had run between them, and the
       * only signal of the in-flight write was a spinner — nothing about the state was programmatically
       * determinable.
       */
      it('carries aria-busy from the press until the write settles', () => {
        createMode();
        fillRoleName('Zzz Busy Probe');

        expect(busy()).withContext('an idle form claims nothing').toBeNull();

        press('Update');

        expect(busy()).withContext('stated as soon as the press is processed').toBe('true');

        expectRequest('POST', ROLES_URL, 'the create').flush(envelope(role(9)), {
          status: 201,
          statusText: 'Created',
        });
        expectNoListingReread();
        fixture.detectChanges();

        expect(busy())
          .withContext('withdrawn once the outcome has been reported, not merely on response')
          .toBeNull();
      });

      /**
       * ⚠ A REFUSED SUBMIT MUST NOT LEAVE THE FORM MARKED BUSY. `onSubmit` returns before issuing
       * anything when the form is invalid, so a busy state raised optimistically on the press would stick
       * forever and tell every reader the screen was working when it was waiting for them.
       */
      it('never marks a form busy when the submit was refused locally', () => {
        createMode();
        press('Update');

        expect(busy()).toBeNull();
      });
    });

    /**
     * R1 — ENTER IN A VALUE BOX MUST COMMIT NOTHING (Issue 14).
     *
     * ⚠ THESE SPECS ASSERT `defaultPrevented`, NOT THE ABSENCE OF A REQUEST, AND THE DISTINCTION IS THE
     * WHOLE REASON THEY HAVE TEETH. Implicit submission is a DEFAULT ACTION of a real key press, and a
     * default action is something only a trusted event performs: `dispatchEvent` of a synthetic
     * `KeyboardEvent` never submits a form in any browser. A spec that pressed a synthetic Enter and then
     * asserted `httpMock.expectNone(...)` would therefore pass identically with and without the fix — it
     * would be measuring the test harness, not the behaviour.
     *
     * What the fix actually does is cancel that default action, and `defaultPrevented` is the observable
     * that reports it: false when nothing cancelled the keystroke, true when something did. That is exactly
     * the state the browser consults before submitting, so asserting it models the mechanism rather than
     * approximating it.
     *
     * The legacy authority is `Website/admin/Security/editroles.ascx` L179-L188, where all four commands are
     * `asp:LinkButton` — anchors calling `__doPostBack`, not submit controls — alongside eight
     * `asp:TextBox` controls. Under HTML's implicit-submission rule, a form with no submit button and more
     * than one blocking field does nothing on Enter. The port shipped a real `button[type="submit"]`, so the
     * keystroke ran the save that `EditRoles.ascx.vb:L212-L231` deliberately preserves — the one that
     * overwrites six stored paid-membership values the form never renders.
     */
    describe('implicit submission', () => {
      /** Dispatches one cancelable keystroke and reports whether anything cancelled it. */
      function keystroke(
        target: Element,
        key = 'Enter',
        init: KeyboardEventInit = {},
      ): boolean {
        const event = new KeyboardEvent('keydown', {
          key,
          bubbles: true,
          cancelable: true,
          ...init,
        });

        target.dispatchEvent(event);
        fixture.detectChanges();

        return event.defaultPrevented;
      }

      it('cancels Enter in the monetary box the finding names', () => {
        editMode(pricedRole(7));

        expect(keystroke(input(CONTROL_ID.serviceFee)))
          .withContext('Enter in Service Fee must not ask this form to submit')
          .toBeTrue();
      });

      it('cancels Enter in every other value box on the form', () => {
        editMode(pricedRole(7));

        for (const controlId of [
          CONTROL_ID.serviceFee,
          CONTROL_ID.trialFee,
          CONTROL_ID.trialPeriod,
          CONTROL_ID.billingPeriod,
          CONTROL_ID.rsvpCode,
        ]) {
          expect(keystroke(input(controlId)))
            .withContext(`Enter in ${controlId} must not ask this form to submit`)
            .toBeTrue();
        }
      });

      /**
       * ⚠ THE ROLE NAME BOX IS COVERED IN CREATE MODE, BECAUSE THAT IS THE ONLY MODE IN WHICH IT IS A
       * BOX. On edit the same id belongs to an `<output>` — the read-only treatment this console preserves
       * deliberately, since `editroles.ascx:L28-L33` renders the name as a label once a role exists. An
       * `<output>` is not a field that blocks implicit submission and cannot be typed into, so there is no
       * keystroke there to cancel; asserting against it would have been a spec that passed whatever the
       * implementation did.
       */
      it('cancels Enter in the role name box on the create form', () => {
        createMode();

        expect(keystroke(input(CONTROL_ID.roleName)))
          .withContext('Enter in Role Name must not ask this form to submit')
          .toBeTrue();
      });

      /**
       * ⚠ THE COMMANDS MUST STAY KEYBOARD-OPERABLE. Cancelling Enter on a button would turn the fix into
       * a regression: the browser translates Enter on a focused button into a click, and that is the ONLY
       * way a keyboard user reaches Update or Cancel. Suppressing it would leave the form unsubmittable
       * without a pointer.
       */
      it('leaves Enter on a command entirely alone', () => {
        editMode(pricedRole(7));

        for (const label of commandLabels()) {
          const control: HTMLButtonElement | undefined = command(label);

          expect(control).withContext(`the ${label} command is rendered`).toBeDefined();
          expect(keystroke(control as HTMLButtonElement))
            .withContext(`Enter must still activate ${label}`)
            .toBeFalse();
        }
      });

      /**
       * ⚠ ENTER IN A TEXTAREA INSERTS A NEWLINE AND NEVER SUBMITS, so there is nothing to prevent and
       * preventing it would stop an operator typing a second line of description.
       */
      it('leaves Enter in the description textarea alone', () => {
        editMode(pricedRole(7));

        expect(keystroke(textArea()))
          .withContext('Enter must still insert a newline in a multi-line field')
          .toBeFalse();
      });

      /** Only Enter is touched; every other keystroke reaches the field untouched. */
      it('leaves other keystrokes in a value box alone', () => {
        editMode(pricedRole(7));

        for (const key of ['a', '5', 'Tab', 'Escape', ' ']) {
          expect(keystroke(input(CONTROL_ID.serviceFee), key))
            .withContext(`${key} must not be cancelled`)
            .toBeFalse();
        }
      });

      /**
       * ⚠ AN INPUT METHOD ENDS A COMPOSITION SESSION WITH ENTER. Swallowing that keystroke would stop an
       * operator committing the characters they are in the middle of typing, so the guard stands down for
       * it. This is why `isComposing` is consulted rather than assumed false.
       */
      it('stands down for the Enter that ends a composition session', () => {
        editMode(pricedRole(7));

        expect(keystroke(input(CONTROL_ID.serviceFee), 'Enter', { isComposing: true }))
          .withContext('an IME must be able to commit its composition')
          .toBeFalse();
      });

      /**
       * THE PAIRED HALF: the explicit command must still save. A fix that suppressed the keystroke by
       * disabling submission altogether would satisfy every case above and break the screen.
       */
      it('still saves when the Update command is pressed explicitly', () => {
        editMode(pricedRole(7));
        keystroke(input(CONTROL_ID.serviceFee));
        press('Update');

        expect(submittedUpdate(7)).withContext('the explicit command is unaffected').toBeTruthy();
      });
    });
  });
  /**
   * AREA 17 — the invitation-code strength rule, and the legacy codes it must not lock out.
   *
   * The rule is net-new hardening: an invitation code is a shared secret that grants role membership, and
   * the legacy application bounded it only above, so a one-character code was accepted and thereafter
   * redeemable by anyone who guessed it. The rule is therefore held against AUTHORING.
   *
   * ⚠ THE GRANDFATHERING IS WHAT MAKES IT SURVIVABLE, and these cases exist to keep it. Held against every
   * submission, the rule made a role carrying a pre-rule code un-editable: amending its description posts
   * the stored code back and the rule refuses it, so the only escape is rotating the code - which
   * invalidates it for every member holding it. AAP 0.7.5.5 forbids exactly that class of migration-time
   * tightening. The screen mirrors the server: it judges the value being WRITTEN, not the value submitted.
   */
  describe('AREA 17 — authoring an invitation code, and grandfathering a stored one', () => {
    /** A code that satisfies the rule: at least twelve characters carrying two character classes. */
    const STRONG_CODE = 'JOINUS-2026x';

    /** The wording both sides report, mirroring `RoleTermsRules.RsvpCodeTooWeakMessage`. */
    const TOO_WEAK =
      'An RSVP Code must be at least 12 characters long and must mix letters with digits or punctuation.';

    /**
     * A stored short code is left alone. This is the case the server fix admits and the screen must not
     * contradict - a refusal here would block the save before the request was ever sent, reintroducing the
     * trap one layer higher.
     */
    it('says nothing about a stored short code that has not been touched', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));

      expect(input(CONTROL_ID.rsvpCode).value).withContext('the stored code is rendered').toBe('JOIN');
      expect(messagesFor(CONTROL_ID.rsvpCode))
        .withContext('nothing is authored, so there is nothing to judge')
        .toEqual([]);
    });

    /**
     * THE PAIRED HALF of the case above: the untouched short code must not merely be silent, it must still
     * SAVE. A screen that reported nothing but refused to submit would be just as broken.
     */
    it('saves an unrelated edit while a stored short code rides along unchanged', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      type(CONTROL_ID.description, 'Amended description');
      press('Update');

      const sent: UpdateRoleRequest | undefined = submittedUpdate(7);
      expect(sent).withContext('the update reaches the server').toBeTruthy();
      expect(sent?.rsvpCode).withContext('the stored code is carried back verbatim').toBe('JOIN');
    });

    /**
     * Rotating a weak code IN is authoring, and is refused before the round trip. This is the guidance the
     * register asked for: the caller learns of the rule while typing rather than through a 400 after saving.
     */
    it('refuses a different weak code, naming the rule', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      type(CONTROL_ID.rsvpCode, 'JOIN2008');

      expect(messagesFor(CONTROL_ID.rsvpCode)).withContext('the rule is stated').toEqual([TOO_WEAK]);
    });

    /**
     * Casing-only change counts as authoring, matching the server's ordinal, case-SENSITIVE comparison. The
     * value is a shared secret rather than a name, so a different casing is a different secret.
     */
    it('treats a casing-only change as authoring', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      type(CONTROL_ID.rsvpCode, 'join');

      expect(messagesFor(CONTROL_ID.rsvpCode))
        .withContext('a different secret is being authored')
        .toEqual([TOO_WEAK]);
    });

    /** Restoring the stored value clears the refusal, so the rule is recoverable rather than sticky. */
    it('clears the refusal once the stored code is typed back', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      type(CONTROL_ID.rsvpCode, 'JOIN2008');
      expect(messagesFor(CONTROL_ID.rsvpCode)).withContext('refused first').toEqual([TOO_WEAK]);

      type(CONTROL_ID.rsvpCode, 'JOIN');

      expect(messagesFor(CONTROL_ID.rsvpCode)).withContext('nothing is authored again').toEqual([]);
    });

    /** A strong replacement is admitted, so the rule permits the rotation it is asking for. */
    it('admits a strong replacement', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      type(CONTROL_ID.rsvpCode, STRONG_CODE);

      expect(messagesFor(CONTROL_ID.rsvpCode)).withContext('the rule is met').toEqual([]);
    });

    /**
     * Creation stores nothing, so every non-empty value there is authored and the rule always applies. This
     * is the arm that keeps the hardening real once the unchanged case is admitted.
     */
    it('refuses a weak code on the creation route, where nothing is stored', () => {
      createMode();
      type(CONTROL_ID.rsvpCode, 'JOIN');

      expect(messagesFor(CONTROL_ID.rsvpCode))
        .withContext('nothing is stored, so this is authoring')
        .toEqual([TOO_WEAK]);
    });

    /** An empty code is not a code, and clearing one must stay permitted. */
    it('permits clearing the code altogether', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      type(CONTROL_ID.rsvpCode, '');

      expect(messagesFor(CONTROL_ID.rsvpCode)).withContext('an absent code breaks no rule').toEqual([]);
    });

    /**
     * The requirement is disclosed BEFORE a caller types, which is what the register asked for. Both halves
     * are asserted: the rule itself, and the fact that a stored code is exempt - a hint stating only the
     * rule would leave an administrator believing a working legacy code was broken.
     */
    it('discloses the rule and the exemption in the field help', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));

      // The help sits behind a disclosure, so it is absent from the document until opened. Asserting the
      // collapsed field's text would pass against an EMPTY help string, which is why the toggle is pressed
      // first and the disclosure's own paragraph is the thing read.
      const field = fieldOf(CONTROL_ID.rsvpCode);
      queryOrFail<HTMLButtonElement>(field, 'button.form-field__help-toggle').click();
      fixture.detectChanges();

      const help = textOf(queryOrFail<HTMLElement>(fieldOf(CONTROL_ID.rsvpCode), 'p.form-field__help'));

      expect(help).withContext('the length is stated').toContain('12');
      expect(help).withContext('the mixing rule is stated').toContain('mix letters');
      expect(help).withContext('the exemption is stated').toContain('keeps working');
    });

    /**
     * ⚠ NO NATIVE `minlength` OR `pattern` ON THE CONTROL. Both are provenance-blind, so either would refuse
     * a stored short code the role is entitled to keep - the very trap this area exists to prevent. This
     * case pins their absence so a later "helpful" addition cannot silently reinstate it.
     */
    it('carries no provenance-blind native constraint', () => {
      editMode(role(7, { rsvpCode: 'JOIN' }));
      const control = input(CONTROL_ID.rsvpCode);

      expect(control.getAttribute('minlength')).withContext('no native minimum').toBeNull();
      expect(control.getAttribute('pattern')).withContext('no native pattern').toBeNull();
      expect(control.checkValidity())
        .withContext('the browser must not block the stored code')
        .toBeTrue();
    });
  });
  /**
   * AREA 18 — the remembered listing coordinate, and the group that is no longer there.
   *
   * ⚠ THE MEASURED DEFECT: A SAVE THAT SUCCEEDED LANDED ON A LISTING REPORTING FAILURE, WITH THE NEW ROLE
   * NOWHERE IN IT. The listing remembers where the operator was so a save does not lose their place, and that
   * is right - but "where they were" is remembered as an ADDRESS, and an address naming a group that has
   * since been deleted is a request the server answers 404 to. The role was created; it simply could not be
   * seen, which is indistinguishable from data loss.
   */
  describe('AREA 18 — returning to a listing whose group may be gone', () => {
    /** Remembers a listing coordinate narrowed to one group, as the listing itself would have. */
    function rememberNarrowedTo(roleGroupId: number): void {
      TestBed.inject(ListReturnStore).remember(ROLE_LIST_ROUTE, {
        group: String(roleGroupId),
        currentpage: '2',
      });
    }

    /** The query parameters the screen navigated back to. */
    function returnedWith(): Params {
      const call: readonly unknown[] | undefined = navigateSpy.calls.mostRecent()?.args;
      const options = call?.[1] as { queryParams?: Params } | undefined;

      return options?.queryParams ?? {};
    }

    /**
     * A narrowing the group set still contains is kept, which is the whole point of remembering it. Asserted
     * FIRST, because a guard that dropped every narrowing would satisfy every case below and quietly undo the
     * feature it is protecting.
     */
    it('keeps a remembered narrowing whose group still exists', () => {
      rememberNarrowedTo(4);
      editMode(role(7), { groups: [roleGroup(4)] });
      fillRoleName('Renamed');

      press('Update');
      submittedUpdate(7);

      expect(returnedWith()['group']).withContext('the operator keeps their place').toBe('4');
      expect(returnedWith()['currentpage']).withContext('including their page').toBe('2');
    });

    /**
     * A narrowing naming a group the set does NOT contain is dropped, so the return address cannot be refused.
     * The rest of the coordinate is left alone - only the unusable part is removed.
     */
    it('drops a remembered narrowing whose group has gone, keeping the rest', () => {
      rememberNarrowedTo(42);
      editMode(role(7), { groups: [roleGroup(4)] });
      fillRoleName('Renamed');

      press('Update');
      submittedUpdate(7);

      expect(returnedWith()['group'])
        .withContext('a group that is not there cannot be returned to')
        .toBeNull();
      expect(returnedWith()['currentpage'])
        .withContext('only the unusable part is dropped')
        .toBe('2');
    });

    /**
     * ⚠ AN UNREAD GROUP SET IS NOT AN EMPTY ONE, and the guard distinguishes them by asking whether the read
     * SETTLED rather than whether the set has members. Its unsettled arm is DEFENSIVE rather than reachable,
     * and this case is what establishes that: no save can happen under an unsettled group set, because the
     * form is not offered until the set arrives. Recorded so the arm is not mistaken for dead code and
     * removed - the latch is set on the failure path too, so "unsettled" means "in flight", and the day this
     * screen renders its fields before that read completes, the arm is the only thing standing between a
     * valid remembered narrowing and being discarded on a guess.
     */
    it('offers no save at all while the group set is still in flight', () => {
      rememberNarrowedTo(42);

      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '7');
      fixture.detectChanges();

      const groups: TestRequest = expectRequest('GET', ROLE_GROUPS_URL, 'the group read');
      expectRequest('GET', roleUrl(7), 'the role read').flush(envelope(role(7)));
      fixture.detectChanges();

      expect(command('Update'))
        .withContext('the fields are not offered before the group set arrives, so nothing can be saved')
        .toBeUndefined();
      expect(navigateSpy).withContext('and nothing has navigated').not.toHaveBeenCalled();

      // Settling the read is what reveals the form, which is the paired half of the same fact.
      groups.flush(envelope([roleGroup(4)]));
      fixture.detectChanges();

      expect(command('Update')).withContext('and it is offered once the set arrives').toBeDefined();
    });

    /**
     * A genuinely EMPTY group set is settled knowledge, and a narrowing cannot survive it. This is the case
     * the length-based test could never express: the set holds nothing AND that is the answer.
     */
    it('drops a remembered narrowing when the group set is settled and empty', () => {
      rememberNarrowedTo(42);
      editMode(role(7), { groups: [] });
      fillRoleName('Renamed');

      press('Update');
      submittedUpdate(7);

      expect(returnedWith()['group'])
        .withContext('an answered empty set contains no group to return to')
        .toBeNull();
    });

    /**
     * The listing's own intent tokens are not keys, so there is no group to look up and nothing to
     * invalidate. Dropping one would silently reset the operator's chosen scope.
     */
    it('leaves a non-numeric intent token exactly as remembered', () => {
      TestBed.inject(ListReturnStore).remember(ROLE_LIST_ROUTE, { group: 'all' });
      editMode(role(7), { groups: [roleGroup(4)] });
      fillRoleName('Renamed');

      press('Update');
      submittedUpdate(7);

      expect(returnedWith()['group']).withContext('an intent is not a key').toBe('all');
    });
  });
});
