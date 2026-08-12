/**
 * Specification for {@link RoleFormComponent} — creating a security role, and editing one.
 *
 * ## WHY THIS SCREEN CARRIES THE HEAVIEST SPECIFICATION IN THE FEATURE SET
 *
 * `Website/admin/Security/editroles.ascx` declares NINE validators — one
 * `asp:RequiredFieldValidator` and EIGHT `asp:CompareValidator`s, two on each of the four money
 * and period fields. That is the densest validation surface of any in-scope admin screen, so this
 * screen is where UI FUNCTIONAL PARITY is either proved or lost: the Minimal Change Clause
 * requires that validation rules MATCH and that error messages be EQUIVALENT, and every case
 * below is a parity proof rather than a smoke test.
 *
 * A role is also an authorisation primitive and a BILLING object at once. Its paid-membership
 * terms — service fee, billing period and frequency, and the trial equivalents — were carried
 * forward from the legacy schema verbatim, and the API refuses a period that is not strictly
 * positive. So the screen's hardest obligation is not validation at all: it is what it SUBMITS
 * when a group of terms is left blank. The legacy default is a period of ONE and a frequency of
 * `'N'`, never zeros, and losing that turns every unpriced role into a rejected request.
 *
 * ## NO USER-SPECIFIED RULES EXIST FOR THIS PROJECT
 *
 * The project's rules document holds exactly one line stating that no user rules were provided,
 * so ZERO obligations here originate from a rule and NO coverage threshold is imposed by one.
 * Their absence is not licence to lower the bar: the standard applied instead is the plan's own
 * normative body — functional parity, data-model fidelity, behavioural equivalence, strict
 * TypeScript — and no rule, convention document or coverage floor is invented to fill the gap.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - Mounted as the standalone unit it is, with the REAL `RoleService` and the REAL
 *     {@link RoleStore} resolved from the injector, and every request answered through
 *     `HttpTestingController`. The component injects the store and never the transport, so the
 *     store is the only thing between a command here and a request on the wire.
 *   - The role identifier arrives through `componentRef.setInput('roleId', …)` as the STRING a
 *     route parameter is, so the component's own parsing runs. Creation mode is the input never
 *     being set at all.
 *   - `Router.navigate` is spied because the screen navigates with an ARRAY of commands;
 *     `NotificationService.notify` is spied and called through, so severities are observable.
 *   - Every assertion is made through the rendered document or the outgoing request. Nothing
 *     reaches into the component's protected form group, so these cases prove the TEMPLATE and
 *     the wire contract, not merely the class.
 *   - `TestBed.flushEffects()` and `TestBed.tick()` are BOTH avoided deliberately. Checked against
 *     the installed `@angular/core@19.2.25`: `flushEffects` exists but is annotated developer
 *     preview, and `TestBed.tick()` does not exist at all in this version. The harness is
 *     zone-based, so `fixture.detectChanges()` settles the effects and no preview API is taken.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ THE ROLE GROUP LIST IS READ FROM THE CONSTRUCTOR, before any mode is known, so
 * `GET /api/v1/role-groups` is outstanding in EVERY case and must be answered.
 *
 * ⚠ A SUCCESSFUL WRITE RE-READS THE ROLE LISTING AND THEN NAVIGATES AWAY, so `GET /api/v1/roles`
 * follows every create, update and delete. A FAILED write issues no such read.
 *
 * ⚠ `dbo.Roles.RoleID` IS `IDENTITY (0, 1)`. Role zero is a real role, so the mode is derived from
 * whether the address carries a role AT ALL and never from the value. Forbidden here and in the
 * component alike: `if (id)`, `!id`, `id > 0`, `id ?? -1`, `?? 0`, `|| 0`, `|| ''`, `Math.abs(`.
 *
 * ⚠ EVERY ADDRESS ASSERTED BELOW IS ROOT-RELATIVE. The test target declares no build-time file
 * replacement, so these cases compile against the production configuration whose API base is the
 * relative `/api/v1` — which is what lets the proxy in front of the container serve the API from
 * the same origin that served the application. No absolute origin appears anywhere in this file.
 *
 * ## THE THIRTEEN AREAS THIS FILE PROVES
 *
 *  1. Every numeric field refuses a value of the wrong data type.
 *  2. Every numeric comparison is NUMERIC, correcting a lexical comparison defect.
 *  3. Every numeric field is VALID WHEN EMPTY, and only the role name is ever demanded.
 *  4. The role name's rules, the description's single rule, and name immutability once created.
 *  5. The two corrected messages, and dynamic display.
 *  6. The server's refusals: a duplicate name, a refusal of authority, and the support reference.
 *  7. All six billing-frequency codes round-trip VERBATIM on both frequency fields.
 *  8. The three-part billing gate and the cross-field trial gate.
 *  9. Mode by PRESENCE, the ungrouped role group, and sentinel rendering.
 * 10. The portal-protected roles.
 * 11. The commands each mode offers, and their wording.
 * 12. The three commands that do not validate.
 * 13. The rendered document: disclosure state, grouping, landmarks and markup hygiene.
 *
 * ## LEGACY SOURCES (read-only references; not one is modified by this work)
 *
 * `Website/admin/Security/editroles.ascx` · `EditRoles.ascx.vb` ·
 * `App_LocalResources/EditRoles.ascx.resx` · `App_LocalResources/Roles.ascx.resx` ·
 * `Website/App_GlobalResources/SharedResources.resx` ·
 * `Library/Components/Security/Roles/RoleController.vb` · `RoleInfo.vb` ·
 * `Website/admin/Security/Roles.ascx.vb` · `Website/release.config`.
 */
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { RoleStore } from '../../../core/state/role.store';
import { RoleFormComponent } from './role-form.component';

/**
 * The tenant the doubled identity reports.
 *
 * `Portals.PortalID` is `IDENTITY(-1, 1)`, so the first tenant a schema creates carries -1 — which is
 * also the legacy absent-integer marker. Using it here proves the tenant request is issued for a real
 * key rather than skipped by a truthiness test.
 */
const TENANT_ID = -1;

import type { WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
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

/** The destination of every `Response.Redirect(NavigateURL())` on the legacy screen. */
const ROLE_LIST_ROUTE = '/roles';

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Every validation sentence below is the `.Text` VALUE from
// `Website/admin/Security/App_LocalResources/EditRoles.ascx.resx`, with its leading break markup
// stripped — never the inline `ErrorMessage` attribute from `editroles.ascx`.
//
// MIGRATION: the resource file is the authority and the proof is mechanical rather than a matter of
// taste. A validator's `Text` rendered INLINE beside its field, while its `ErrorMessage` rendered
// ONLY inside an `<asp:ValidationSummary>` — and there is not ONE `<asp:ValidationSummary>` in any of
// the thirty-nine in-scope admin controls. The inline `ErrorMessage` values were therefore NEVER SHOWN
// TO A USER AT ALL, and two of them had rotted into each other; see AREA 5.
// =====================================================================================================

const ADD_TITLE = 'Add New Role';
const EDIT_TITLE = 'Edit Security Roles';

/**
 * The sentence shown when the address names no readable role.
 *
 * Restated here rather than imported, matching how every other expected string in this file is
 * declared. Importing the component's own constant would compare it against itself and pass for any
 * wording at all, including an empty one.
 */
const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a role that can be read. Return to the role list and try again.';

const SUBMIT_LABEL = 'Update';
const CANCEL_LABEL = 'Cancel';
const DELETE_LABEL = 'Delete';
const MANAGE_USERS_LABEL = 'Manage Users in this Role';

/** The stale inline caption the local resource entry supersedes. It must never be rendered. */
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
const ROLE_NOT_FOUND_MESSAGE = 'That role could not be found.';
const SAVE_FAILED_MESSAGE = 'The role could not be saved.';
const DELETE_FAILED_MESSAGE = 'The role could not be deleted.';

/**
 * The caption of the ungrouped choice, MEASURED rather than paraphrased.
 *
 * `EditRoles.ascx.vb:L75` calls the one-argument `Localization.GetString("GlobalRoles")`, which
 * resolves from global resources, and `Website/App_GlobalResources/SharedResources.resx` holds
 * `GlobalRoles.Text` as `< Global Roles >` — the angle brackets AND the spaces inside them
 * included. The caption is never written without them.
 */
const GLOBAL_ROLES_LABEL = '< Global Roles >';

/**
 * The opening words of `ModuleHelp.Text`, asserted ABSENT.
 *
 * MIGRATION: module help has no home in the closed shared component inventory, so the entry is
 * read for the record and never rendered. Its first heading is the handle used to prove that.
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

/**
 * The six codes, in the order the legacy `CodeFrequency` lookup seeded them.
 *
 * These are LOAD-BEARING PERSISTED DATA. `dbo.Roles.BillingFrequency` and
 * `dbo.Roles.TrialFrequency` are `char(1)` columns constrained by `FK_Roles_CodeFrequency`, and
 * `Library/Components/Security/Roles/RoleController.vb:L540-L546` switches on the raw characters:
 * `'N'` leaves the expiry unbounded, `'O'` sets 9999-12-31, and `'D'`, `'W'`, `'M'`, `'Y'` add
 * days, weeks, months and years.
 *
 * MIGRATION: the options are declared LOCALLY rather than fetched. The legacy bound both selects
 * from `ListController.GetListEntryInfoCollection("Frequency", "")`
 * (`EditRoles.ascx.vb:L116-L125`), but `Library/Components/Lists` is out of scope, so no frequency
 * lookup endpoint exists and none is invented. The vocabulary is closed by a foreign key, so
 * declaring it locally loses nothing.
 */
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
 * ⚠ THE PERIOD IS ONE, NOT ZERO. This is the single easiest fact on this screen to lose.
 *
 * Measured at `EditRoles.ascx.vb:L212-L214` and `:L222-L224`, which initialise
 * `sglServiceFee = 0`, `intBillingPeriod = 1` and `strBillingFrequency = "N"` before either gate
 * is tested. The API's own rule refuses a period that is not strictly positive, so a zero here
 * would make every unpriced role a rejected request.
 */
const SUPPRESSED_FEE = 0;
const SUPPRESSED_PERIOD = 1;
const SUPPRESSED_FREQUENCY: BillingFrequency = NO_FREQUENCY;

/**
 * The value that means "not in a group" ON THE WAY OUT.
 *
 * MIGRATION: `null`, NOT `-1`, and this is a deliberate divergence from the legacy wire value
 * rather than an oversight. The legacy select carried the string `"-1"`
 * (`EditRoles.ascx.vb:L75`) and the membership provider then wrapped the argument in
 * `GetNull(RoleGroupId)` (`Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb`
 * L235 and L243) so that `-1` reached SQL Server as `NULL`. The target performs that collapse ONCE,
 * at the contract boundary: `roleGroupId` is a nullable integer on both request contracts, and
 * `-1`, `< Global Roles >` and SQL `NULL` are one state of which `null` is the honest
 * representation. Sending `-1` would bypass a translation the target does not reproduce and reach
 * the foreign key as a literal `-1`.
 *
 * The legacy value is still recognised on the way IN, which AREA 9 proves. Nothing writes it out.
 *
 * ⚠ THE ABSENCE CANNOT BE SPELT `0`: `dbo.RoleGroups.RoleGroupID` is `IDENTITY (0, 1)`, so group
 * zero is a real group.
 */
const UNGROUPED: number | null = null;

/** The legacy ungrouped marker, which a producer that has not collapsed it may still send. */
const LEGACY_UNGROUPED = -1;

/**
 * The list screen's own filter sentinel, asserted NEVER to appear on this form.
 *
 * `Roles.ascx.vb:L112` adds an `< All Roles >` entry with the value `-2` to the LIST screen's
 * narrowing picker. It is a filter sentinel and has no meaning as a stored value, so a form that
 * offered it could persist it.
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
 * The data-type sentence declared for a control, looked up from the table above.
 *
 * A lookup rather than a second literal, so a message can never be asserted against a string this
 * file alone believes in.
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

/**
 * A W3C trace-context value, FIXED so that no case depends on a clock.
 *
 * The workspace's own reader prefers the correlation identifier and falls back to this one, so a
 * document carrying only this member is what proves the trace identifier survives into the surface.
 */
const TRACE_ID = '00-3e7a4f2b9c934dd6bb18eb211c80319c-44bd6b7169203331-01';

/** The support reference the server echoes, FIXED for the same reason. */
const CORRELATION_ID = 'c58d1a76-9e42-4b03-8f61-2a7c5d0e3b94';

/** How the banner presents whichever identifier it found. */
const REFERENCE_PREFIX = 'Reference:';

/** Options for a refusal document, so a case can withhold exactly the members it means to. */
interface ProblemOptions {
  readonly detail?: string;
  readonly title?: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
  readonly traceId?: string;
  readonly correlationId?: string;
}

/**
 * Builds an RFC 7807 refusal document.
 *
 * Members are omitted rather than nulled when a case withholds them, which is what the framework's
 * own problem-details type does: each of its five standard members carries a per-member
 * null-omission condition that overrides the collection-wide policy.
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

/**
 * A refusal that says NOTHING a person can use, so the client's own fallback governs.
 *
 * Withholding `title` as well as `detail` is deliberate: the workspace's reader prefers `detail`,
 * then `title`, then the fallback, so both must be absent for a fallback assertion to mean
 * anything.
 */
function silentProblem(status: number, code: string): ProblemDetails {
  return { type: `${FAILURE_TYPE_PREFIX}${code}`, status };
}


// =====================================================================================================
// FIXTURES — BUILT FROM THE IMPORTED CONTRACTS, NEVER FROM A LOCAL RE-DECLARATION
//
// The shapes come from `core/models/role.model.ts`, so a member renamed there breaks this file
// loudly rather than letting a stale fixture pass. No date member exists on the role contract, so no
// case here constructs a date and none needs a clock.
// =====================================================================================================

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
    // The revision marker the API serves with every role detail. Declared BEFORE the spread so a case
    // may replace it or set it to null - the form is required to carry whatever it read into the
    // update it composes, and both of those are cases worth asserting.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/**
 * A PRICED role, whose billing group survives the load gate.
 *
 * The load gate is `serviceFee !== 0` on the NUMBER. MIGRATION: `EditRoles.ascx.vb:L146` decided
 * this by FORMATTING the fee and comparing the resulting TEXT against `"0.00"`, which is fragile in
 * a way the culture makes obvious — under a culture whose decimal separator is a comma the
 * formatted zero is `"0,00"`, so every free role would have been treated as priced.
 *
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

/**
 * The single-payload wire envelope, DECLARED LOCALLY rather than imported.
 *
 * The workspace's envelope types live in a paging module that is not among this file's declared
 * dependencies, so the two response shapes it needs are declared here instead of reaching outside
 * that set. They are structural mirrors of the wire contract and nothing more: the real shape is
 * enforced on the other side of every flush by the decoders the transport applies, so a local shape
 * that drifted from the contract would fail these cases loudly rather than quietly.
 */
interface WireEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

/** Wraps one payload in the single-payload envelope. */
function envelope<T>(data: T): WireEnvelope<T> {
  return { data, meta: null };
}

// ⚠ THERE IS DELIBERATELY NO PAGE FIXTURE HERE ANY MORE. This screen dispatches three writes and not one
// of them reads a collection: the listing owns listing reads, because its page, narrowing and ordering
// live in its address. A page fixture would only exist to answer a read that must never be issued, and
// having one available invites answering it instead of asserting its absence.

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
    // `provideHttpClient()` MUST come first because `provideHttpClientTesting()` OVERRIDES the real
    // backend — with nothing registered first there is nothing to override, and requests would reach
    // a real transport. `RoleStore` is pinned so each case owns a fresh instance of the shared state
    // rather than inheriting whatever a previous case left in it.
    //
    // The component is STANDALONE, so it goes in `imports`. No testing module wrapper is used: the
    // provider functions are the whole of the wiring.
    /*
     * THE TENANT'S PROTECTED ROLE KEYS AND ITS PROCESSOR STATE, HELD IN SIGNALS THE CASES CAN MOVE.
     *
     * ⚠ THESE USED TO BE THREE COMPONENT INPUTS, AND THE CHANGE IS THE POINT. They were declared as
     * optional inputs on the reasoning that "a portal-settings call is outside this screen's endpoint
     * boundary", and NOTHING in the application ever supplied one — so the three guards
     * `EditRoles.ascx.vb:L174-L182` declared shipped permanently disarmed, and the form offered
     * Update and Delete on the two roles that hold a tenant together. The facts are now read from the
     * portal store, which is CORE state every feature may inject.
     *
     * Each key opens ABSENT and the tenant opens UNRESOLVED, so the ordinary cases below describe a
     * screen whose tenant record has not arrived — which is the fail-safe direction: the form stays
     * editable and the API's refusal governs, exactly the behaviour that shipped.
     */
    administratorRole = signal<number | null>(null);
    registeredRole = signal<number | null>(null);
    processorConfigured = signal<boolean>(false);
    tenantResolved = signal<boolean>(false);

    /*
     * The request for those facts, spied rather than served: the real portal store would add a tenant
     * read to every case in this file, and the spy records which tenant was asked for and whether it
     * was asked at all.
     */
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

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  //
  // Every helper is cast-free. Where a node must exist for a case to mean anything the helper THROWS,
  // which both narrows the type and produces a better failure than a null dereference would.
  // ---------------------------------------------------------------------------------------------------

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
   * Chooses an option of a select BY ITS RENDERED CAPTION.
   *
   * ⚠ THE DOM OPTION VALUES ARE NOT THE PERSISTED CODES, and that is a framework fact rather than a
   * contract defect. Both frequency pickers and the grouping picker bind through `ngValue`, which is
   * load-bearing — the grouping control holds a number OR NOTHING, and a plain value binding would
   * coerce every option to a string — so Angular writes its own encoded key into each option's DOM
   * `value` attribute. Assigning a raw code would therefore select NOTHING and silently leave the
   * control on its default, which is exactly the kind of no-op that makes a case pass while proving
   * nothing. The caption is the only stable handle, and it is the handle a person uses too. The
   * PERSISTED CODE is proved where it is authoritative: on the wire, in AREA 7.
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

  /** One command by its caption, or nothing when it is not offered. */
  function command(label: string): HTMLButtonElement | undefined {
    return commands().find((candidate) => textOf(candidate) === label);
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
   * Presses a button of the OPEN CONFIRMATION.
   *
   * ⚠ SCOPED TO THE DIALOGUE, because the command bar's own delete control and the dialogue's
   * confirming button share the caption `Delete`, and the abandon command shares `Cancel`.
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
   * Answers the role group read the constructor issues.
   *
   * ⚠ OUTSTANDING IN EVERY CASE. The groups populate the grouping picker, so the screen asks for
   * them before it knows which mode it is in.
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
   * The identifier is supplied as the STRING a route parameter is, under the exact input name
   * `roleId` — a single lower-case `d`. Route parameters are bound onto a component input OF THE SAME
   * NAME, so any other spelling would bind nothing, yield nothing, compile without complaint and
   * fail only at run time.
   *
   * @param subject The role the server will report.
   * @param context The portal-scoped facts to supply, and the groups to answer with.
   */
  function editMode(subject: Role, context: EditContext = {}): void {
    // ⚠ THE TENANT CONTEXT IS PUT IN THE STORE, NOT PASSED IN. It used to arrive as three optional
    // inputs that nothing in the application supplied; it is now read from the portal store, so a
    // case that wants a guard armed writes the fact into the doubled signal BEFORE the component
    // reads it — which is before the first change detection, since every consumer is a `computed`.
    //
    // Supplying ANY of the three marks the tenant RESOLVED, because in production the three arrive
    // together on one record and the processor warning is withheld until that record lands.
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
   * Asserts that a successful write does NOT re-read the listing.
   *
   * ⚠ THIS HELPER USED TO ANSWER SUCH A READ, AND ITS INVERSION IS THE FIX FOR A MEASURED DEFECT. Runtime
   * testing quantified the duplicate the old behaviour produced: 2,466 B sent and 25,283 B decoded per save,
   * in two shapes depending on timing - a complete-and-discard and a network abort - with 36 aborted listing
   * refetches in a single session. The store re-read the listing at the same moment this screen navigated TO
   * the listing, which reads itself from its own address on entry, so the two raced and one was cancelled
   * mid-flight.
   *
   * MIGRATION: `DataCache.RemoveCache("GetRoles")` (`EditRoles.ascx.vb:L264` and `:L296`) has NO client
   * equivalent — it evicted a server-side cache entry and no endpoint exposes that. What replaces it is that
   * the LISTING owns listing reads: its page, narrowing and ordering live in its address, so it reads on entry
   * and on every address change, and a caller arriving there always sees authoritative rows and totals. No
   * cache-invalidation endpoint is invented and no read is duplicated.
   *
   * ⚠ THE RULE COVERS DELETION TOO, AND IT DID NOT ALWAYS. The delete command still re-reads the listing
   * when it is asked to, because deletion is ALSO reachable from the listing itself, where no navigation
   * follows and no address changes, so a removed row would otherwise stay on screen. But this screen is not
   * that caller: it departs for the listing, so it asks for no read, and runtime measurement of the old
   * behaviour on this exact path recorded the abort shape directly — two listing reads with different
   * correlation ids, the first `net::ERR_ABORTED` after some seven milliseconds and the second returning the
   * rows actually shown. Which caller wants the read is now stated at each call site rather than decided for
   * both, so this screen and the listing can differ without either inheriting the other's answer.
   *
   * Named as an assertion rather than an answer because that is now what it is.
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
   * The support reference each announcement quoted, oldest first, `null` where none was quoted.
   *
   * ⚠ DELIBERATELY A SEPARATE PROJECTION FROM {@link Announcement}. The eighteen cases that
   * deep-compare an announcement are about its WORDING, and widening the shape they compare would have
   * made every one of them restate a reference it does not care about - which is how an assertion stops
   * describing its own subject. The reference has its own rule and its own cases below.
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

  /**
   * The sentence the failure banner is rendering, or `null` when the banner is empty.
   *
   * The counterpart of {@link lastAnnouncement}, and it exists because the two surfaces now carry
   * DIFFERENT text by design: the notification says what did not happen, the banner says why. A case
   * that asserts only one of them cannot see the duplication that used to exist between them.
   */
  function bannerMessage(): string | null {
    const node: Element | null = host().querySelector('.error-banner__message');

    return node === null ? null : textOf(node);
  }

  /**
   * The messages that would still be on screen after the shell's navigation sweep.
   *
   * The queue is REAL in this suite - `notify` is spied and called through - so this exercises the
   * actual retention rule rather than asserting that a method was called. That distinction is the
   * whole point here: a screen that announces an outcome and then leaves must have its message
   * survive exactly one navigation, and the only way to prove it is to run the sweep.
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

  // ===================================================================================================
  // AREA 1 — EVERY NUMERIC FIELD REFUSES A VALUE OF THE WRONG DATA TYPE
  //
  // The FIRST validator of each pair. `valServiceFee1` and `valTrialFee1` declare
  // `Type="Currency" Operator="DataTypeCheck"`; `valBillingPeriod1` and `valTrialPeriod1` declare
  // `Type="Integer" Operator="DataTypeCheck"`. The integer form is the stricter of the two, and the
  // difference is observable rather than academic: an integer field refuses a decimal that a currency
  // field must accept.
  // ===================================================================================================

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
      // The legacy populated the fee with `Format(fee, "#,##0.00")` (`EditRoles.ascx.vb:L147,L155`),
      // which emits a thousands separator, so a validator that refused the separator would mark a
      // freshly loaded and untouched form invalid.
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, '1,234.56');

      expect(messagesFor(CONTROL_ID.serviceFee))
        .withContext('the grouped form this screen itself writes is accepted')
        .toEqual([]);
    });

    CURRENCY_CONTROLS.forEach((controlId: string) => {
      it(`refuses more than two decimal places in #${controlId}, which Type="Currency" never admitted`, () => {
        // ⚠ THE SENTENCE IS THE DATA-TYPE ONE, AND THAT IS CORRECT RATHER THAN A CONFUSION OF
        // TWO RULES. The framework's currency conversion measures the digits after the decimal
        // separator against the culture's `CurrencyDecimalDigits` — two — and refuses a longer
        // value BEFORE parsing it, so `-0.001` never reaches a comparison at all. It is not a
        // negative amount; it is not an amount.
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
        // The pair is distinguishable in both directions, which is the whole claim: a value that
        // is a real money amount and merely negative gets the comparison sentence, and the two
        // sentences are different strings.
        createMode();
        fillRoleName();

        type(controlId, '-1.00');

        expect(messagesFor(controlId)).toEqual([comparisonMessageFor(controlId)]);
        expect(comparisonMessageFor(controlId)).not.toBe(dataTypeMessageFor(controlId));
      });
    });

    it('shows exactly ONE sentence for a value that breaks both rules on the same control', () => {
      // MIGRATION: the two rules are mutually exclusive by construction. A value that will not parse
      // yields nothing from the comparison rule and defers to the data-type rule, so one sentence is
      // shown rather than two contradictory ones. The legacy reached the same single-sentence outcome
      // by accident, because its lexical comparison happened to pass for most non-numeric text.
      createMode();
      fillRoleName();

      type(CONTROL_ID.serviceFee, 'minus five');

      expect(messagesFor(CONTROL_ID.serviceFee)).toHaveSize(1);
      expect(messagesFor(CONTROL_ID.serviceFee)).toEqual([SERVICE_FEE_INVALID_MESSAGE]);
    });
  });


  // ===================================================================================================
  // AREA 2 — EVERY COMPARISON IS NUMERIC, CORRECTING A LEXICAL COMPARISON DEFECT
  //
  // MIGRATION — THE HIGHEST-VALUE PARITY DECISION ON THIS SCREEN, AND A DELIBERATE DIVERGENCE.
  //
  // `editroles.ascx` places TWO comparison validators on each of the four money and period fields.
  // The FIRST of each pair declares its type. The SECOND of each pair — `valServiceFee2` (L93-L96),
  // `valBillingPeriod2` (L111-L114), `valTrialFee2` (L125-L128) and `valTrialPeriod2` (L143-L146) —
  // OMITS `Type=` entirely, and the ASP.NET default is `Type="String"`.
  //
  // The comparison was therefore a culture-sensitive STRING comparison, and it gave wrong answers in
  // BOTH directions:
  //
  //   • `"-5" > "0"` is TRUE under culture-sensitive collation, which treats the hyphen as ignorable
  //     punctuation. THE LEGACY WRONGLY ACCEPTED A NEGATIVE FEE AND A NEGATIVE PERIOD.
  //   • `"9" > "10"` is TRUE under ordinary lexical ordering, because `'9'` sorts after `'1'`. So a
  //     smaller period passed a "greater than zero" test FOR ENTIRELY THE WRONG REASON, and `"10"`
  //     was judged against `"0"` one character at a time.
  //
  // ROOT CAUSE — THE OPTION STRICT ASYMMETRY. The class library compiled with Option Strict ON, but
  // `Website/release.config:L125` declares `<compilation debug="false" strict="false">`, so the admin
  // pages compiled with Option Strict OFF. Comparing a numeric-looking string against the literal
  // `"0"` raised nothing at compile time.
  //
  // THE TARGET COMPARES NUMBERS. That REJECTS input the legacy accepted, which is a documented
  // divergence rather than a silent one, and it is made because carrying a collation accident into new
  // code would corrupt persisted money. The API compares numerically too, so the two now agree.
  // ===================================================================================================

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
        // A zero service fee is a real, FREE role.
        // `Library/Components/Security/Roles/RoleController.vb:L494` uses `ServiceFee > 0.0` as the
        // paid discriminator, so zero must survive as itself.
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

  // ===================================================================================================
  // AREA 3 — EVERY NUMERIC FIELD IS VALID WHEN EMPTY
  //
  // MIGRATION — LOAD-BEARING, AND THE REASON THIS AREA EXISTS AT ALL.
  //
  // An ASP.NET comparison validator SUCCEEDS ON AN EMPTY INPUT. That is precisely why every field
  // that was genuinely demanded ALSO carried a required-field validator, and `txtRoleName` is the ONLY
  // control on this screen that has one (`editroles.ascx:L29-L31`). ALL FOUR MONEY AND PERIOD FIELDS
  // ARE THEREFORE OPTIONAL.
  //
  // Adding a required rule to any of them would REJECT INPUT THE LEGACY ACCEPTED and break functional
  // parity outright; adding a built-in minimum rule instead of the hand-written comparisons would
  // report a different error key and a different sentence. The API agrees independently: each of its
  // four numeric rules is guarded so that it applies only when the member carries a value.
  // ===================================================================================================

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
      // MIGRATION: A ROLE NAME IS IMMUTABLE ONCE CREATED, by design rather than by oversight, and the
      // proof is an ASYMMETRY IN THE PROVIDER SIGNATURES:
      //   `AddRole(…)`    takes FOURTEEN parameters and INCLUDES `RoleName`
      //   `UpdateRole(…)` takes THIRTEEN  and has NO `RoleName` parameter at all
      //   (`Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb` L234-L235 vs
      //    L242-L243).
      // The stored procedure simply COULD NOT change a name, which is why `EditRoles.ascx.vb:L131-L134`
      // reveals the read-only twin, hides the text box and sets `valRoleName.Enabled = False` for any
      // role that has an id. A DISABLED INPUT WOULD BE WORSE THAN READ-ONLY TEXT, because it invites an
      // edit the procedure could never have performed.
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
      // The required rule MUST be lifted in edit mode. Leaving it on a control the user cannot reach
      // would leave the edit form permanently invalid and its update command permanently inert, which
      // is a parity break rather than a safety measure.
      //
      // MIGRATION: `UpdateRoleRequest` DOES carry `roleName`, because the contract replaces the whole
      // role rather than patching it. The LOADED name is sent, so immutability is preserved in
      // substance. This also avoids reproducing `:L237`, which assigned the hidden text box's EMPTY
      // STRING — harmless only because the procedure discarded it, and destructive now.
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

  // ===================================================================================================
  // AREA 5 — THE TWO CORRECTED SENTENCES, AND DYNAMIC DISPLAY
  //
  // MIGRATION — TWO STALE INLINE SENTENCES, MUTUALLY TRANSPOSED BY A SINGLE COPY-PASTE.
  //
  //   valBillingPeriod2  resource `.Text` "…Must Be Greater Than Zero"             Operator GreaterThan
  //                      inline `ErrorMessage` "…Must Be Greater Than or Equal to Zero"      ← STALE
  //   valTrialFee2       resource `.Text` "…Must Be Greater Than or Equal to Zero" Operator GreaterThanEqual
  //                      inline `ErrorMessage` "…Must Be Greater Than Zero"                  ← STALE
  //
  // The two stale strings are EXACTLY EACH OTHER — one transposition, not two independent mistakes.
  // The `Operator` arbitrates, and in both cases it vindicates the resource file.
  //
  // The mechanism that settles it: a validator's `Text` rendered inline while its `ErrorMessage`
  // rendered ONLY inside an `<asp:ValidationSummary>`, and there is not ONE of those in any in-scope
  // admin control — so the inline values were never shown to a user at all. The refined standing rule
  // is narrower and more useful than "never trust inline text": NEVER TRUST AN INLINE VALUE WHERE A
  // LOCAL RESOURCE KEY EXISTS. The three command captions have no local key, fall back to global
  // resources, and match their inline text, so those are used as they stand.
  // ===================================================================================================

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
      // All nine `val*.Text` entries open with break markup, stored XML-escaped so a naive search
      // returns nothing. It is stripped rather than rendered, and resource text is never treated as
      // markup: the same admin tree holds a resource value carrying a LIVE remote script block.
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
      // `Display="Dynamic"` on all nine validators: a message appeared only once a submission had
      // exercised it. The creation form opens invalid — the role name is empty and demanded — so a
      // template that rendered messages unconditionally would greet every operator with an error.
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
      // The shared field wrapper accepts one sentence or a sequence of them. This screen supplies at
      // most ONE per control by construction, because the data-type rule and the comparison rule on a
      // single control are mutually exclusive — which is the behaviour AREA 1 proves and the reason a
      // person never sees two sentences contradicting each other.
      createMode();
      fillRoleName();

      type(CONTROL_ID.trialPeriod, 'abc');
      expect(messagesFor(CONTROL_ID.trialPeriod)).toEqual([TRIAL_PERIOD_INVALID_MESSAGE]);

      type(CONTROL_ID.trialPeriod, '-2');
      expect(messagesFor(CONTROL_ID.trialPeriod)).toEqual([TRIAL_PERIOD_NOT_POSITIVE_MESSAGE]);
    });
  });


  // ===================================================================================================
  // AREA 6 — THE SERVER'S REFUSALS
  //
  // MIGRATION: the legacy guarded its insert with its OWN lookup —
  // `If objRoleController.GetRoleByName(PortalId, objRoleInfo.RoleName) Is Nothing Then`
  // (`EditRoles.ascx.vb:L252`) — and showed `DuplicateRole` at `RedError` when it found a match
  // (`:L256`). That is a read-then-write race, and the target replaces it with the API's own refusal, a
  // `409`, which the database's unique index makes authoritative. THE WORDING A USER SEES IS UNCHANGED.
  //
  // MIGRATION: `objEventLog.AddLog(…, EventLogType.ROLE_CREATED)` at `:L254`, its update twin and its
  // delete twin at `:L293` are NOT reproduced on the client. The audit trail is written by the API, on
  // the server side of the call, which is the only place that can record it truthfully — so no case
  // here expects the client to emit an audit event, and none does.
  // ===================================================================================================

  describe('AREA 6 — what the server refuses, and how it is surfaced', () => {
    it('reports a duplicate name at 409 in the legacy wording, at error severity', () => {
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      const call: TestRequest = expectRequest('POST', ROLES_URL, 'the creation');

      // The document says nothing a person can use, so the client's own fallback governs — and the
      // fallback IS the legacy sentence. It is rendered by the BANNER: the notification carries the
      // outcome, so one refusal is never spelled out twice on one screen.
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
      // A server message is applied VERBATIM and never rewritten to match the client's own wording,
      // even where the two differ. Reporting what the server actually said is more useful than
      // harmonising it.
      createMode();
      fillRoleName('Administrators');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLES_URL, 'the creation').flush(
        problem(409, 'duplicate-role', { detail: 'Role name Administrators is already in use.' }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // VERBATIM ON THE BANNER, which is the surface that resolves document-before-fallback. The
      // notification states the outcome in this screen's own words, so the two surfaces complement
      // each other instead of repeating one sentence twice.
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
      // MIGRATION: the legacy ran NO duplicate check when updating — `:L259-L262` updates
      // unconditionally — which it could afford because `UpdateRole` has no `RoleName` parameter and so
      // could not create a collision. The target's update contract DOES carry the name, so a collision
      // is possible in principle and the API refuses it. That refusal is handled like any other rather
      // than assumed unreachable.
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
      // MEASURED, and it settles a decision that would otherwise be taken by intuition:
      // `Website/admin/Security/AccessDenied.ascx.vb` raises `ModuleMessageType.YellowWarning` in BOTH
      // of its branches, at `:L43` and at `:L45`, and never `RedError`. Being told one lacks permission
      // is not a failure of the request, it is the answer to it.
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
      // ⚠ A BANNER THAT OUTLIVED THE SNAPSHOT IT DESCRIBED. Runtime testing refused a duplicate name
      // with a `409`, cleared the name, and pressed Update: the press was blocked by the presence rule,
      // and the page went on saying that a role with the same name already exists - about a name that
      // was no longer in the box, beside a field message saying the name was missing. The document
      // described the request that WAS sent; once a further attempt is made it describes nothing on
      // screen, whether that attempt reaches the server or not.
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
      // The document is stored WHOLE, so its diagnostic identifiers survive into the banner for
      // support to quote. This document carries only the trace identifier, so that is the one the
      // banner must fall back to.
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
      // ⚠ THE MEASURED DEFECT. A browser audit submitted a duplicate role name and captured `409`
      // carrying `correlationId` in both the response header and the problem body. The banner rendered
      // `Reference: <id>`. This announcement rendered `The role could not be saved.` and had THREE child
      // nodes - severity, message, dismiss - with no reference node at all: the identifier was in hand and
      // was discarded.
      //
      // It matters because the two surfaces do not cover for one another. The banner sits at the top of a
      // form long enough to scroll, so an operator who submits from the bottom sees this announcement and
      // nothing else - and the same audit found the sibling screen's `403` and `404` produced no console
      // entry either. Without the identifier a refused save cannot be escalated at all.
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
      // Stated separately from the case above because a reference that is merely present is not enough:
      // two surfaces describing one refusal must name one identifier. Reading them from the shared summary
      // rather than re-deriving each is what makes that true, and this is the case that would fail if a
      // later edit re-read the document in one of the two places.
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
      // ⚠ THE OTHER HALF OF THE RULE, AND IT IS WHY THE RULE IS A RULE. A reference is quoted because
      // the answer HAD one, never as decoration - so a transport failure, which arrives with no document
      // and therefore no identifier, must announce its outcome and quote nothing. Without this case the
      // one above could be satisfied by inventing an identifier, and an operator would be handed a
      // reference support cannot find.
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
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { replaceUrl: true });
    });

    it('still announces a creation that settles AFTER the operator has left the screen', () => {
      // ⚠ THE MEASURED DEFECT, AND ITS CAUSE IS A LIFETIME RATHER THAN A MESSAGE. The write bridge is an
      // effect in this component's injection context, so it dies WITH the component - and an operator who
      // submits and then immediately clicks somewhere else destroys the only party that was going to tell
      // them what happened. A browser audit measured it on this screen: the request answered `201`, was
      // never aborted, the role was genuinely created, the destination screen was healthy - and no
      // confirmation was raised anywhere. The outcome was published to a signal slot nobody was left
      // watching.
      //
      // The screen now hands the last step over as it goes, to a root-provided watcher that outlives every
      // screen. It announces and deliberately does NOT navigate: the operator chose to be somewhere else.
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
        // Four arguments, not five: the relay goes through the queue's `success` convenience, which states
        // the reference and the navigation exemption and leaves the lifetime opinion to its default - so a
        // confirmation keeps the severity-derived lifetime every other confirmation has.
        .toHaveBeenCalledWith('success', ROLE_CREATED_MESSAGE, null, false);
      expect(navigateSpy)
        .withContext('and is NOT dragged back to the listing they deliberately left')
        .not.toHaveBeenCalled();
    });

    it('states the refusal when a write that outlived the screen was refused', () => {
      // ⚠ THIS ASSERTION IS INVERTED FROM THE ONE IT REPLACES, which required silence. The old reasoning
      // was that a refusal in this application is a DOCUMENT - a title, a detail, per-field messages, a
      // support reference - whose home is the banner ON this screen; this screen is gone, so there is no
      // field for a field message to sit beside, and the store still holds the failure for anyone who
      // returns.
      //
      // All of that is true about the document and none of it justifies silence about the FACT. The
      // operator submitted a role and moved on: they believe it was created. Nothing tells them otherwise -
      // an absent confirmation is indistinguishable from one they clicked away from - and they find out
      // when something downstream needs a role that does not exist. "Returning here presents it in full"
      // presumes they know they have a reason to return, which is exactly what they do not know.
      //
      // WHAT IS RELAYED IS STILL NOT THE DOCUMENT. This screen supplies the same sentence its own banner
      // falls back to, plus the support reference, and nothing field-scoped travels; the document stays in
      // the store and is still presented in full with its per-field messages on return.
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
        // THREE arguments, not four: the relay goes through the queue's `error` convenience, which states
        // the reference and leaves BOTH the navigation exemption and the lifetime opinion to their
        // defaults - so a refusal keeps the severity-derived lifetime every other error has, and an error
        // is never retired on a timer.
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
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { replaceUrl: true });
    });

    it('leaves the form settled at the instant it navigates, so the guard cannot question a saved role', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      editMode(role(7));

      // ⚠ THE DESCRIPTION, NOT THE NAME. On the edit route the role name is not an editable control at all -
      // it renders as an `<output>`, because `UpdateRole(...)` takes no name parameter - so typing into it
      // would dirty nothing and the control assertion below would fail for the wrong reason. It did, on the
      // first run of this case, which is precisely what that assertion is for.
      type(CONTROL_ID.description, 'Edited by the operator');

      // THE CONTROL. Without it a later `false` would be indistinguishable from a probe that was never
      // registered, or from a form that was never dirty. `isDirty()` is the guard's own public surface, so
      // this is asserted through the very call the guard makes.
      expect(tracker.isDirty())
        .withContext('a dirty form with no write in flight is what the guard exists to catch')
        .toBeTrue();

      // ⚠ SAMPLED AT THE INSTANT OF NAVIGATION, NOT AFTERWARDS, because it is the navigation the save
      // itself triggers that the guard would have refused. Measured in a real browser before this was
      // settled: every successful save raised "You have unsaved changes on this page. Leave without saving
      // and discard them?" about the entry that had just been stored - and because `window.confirm` blocks
      // the JavaScript thread, the confirmation's auto-dismiss timer became due while the dialog stood and
      // fired the instant it was accepted, so the operator never saw the success notice at all. A
      // MutationObserver caught it being emitted and then removed.
      let dirtyAtNavigation: boolean | null = null;
      navigateSpy.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { replaceUrl: true });
      expect(dirtyAtNavigation)
        .withContext('the guard must see a settled form on the navigation the save itself triggered')
        .toBeFalse();
    });

    it('leaves the confirmation readable at the listing it navigates to', () => {
      // ⚠ THE CONFIRMATION USED TO BE SWEPT BY ITS OWN NAVIGATION, and the effect was total
      // silence: a save that succeeded and a save that was never made looked identical, because the
      // screen simply returned to the listing. Measured in a real browser after an untouched save -
      // the destination's live region was the empty string and 44 extracted frames showed no toast
      // appearing or dismissing. The message must survive exactly one navigation.
      editMode(role(7));

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      expect(messagesSurvivingNavigation())
        .withContext('the confirmation belongs at the destination, as the legacy showed it')
        .toContain(ROLE_UPDATED_MESSAGE);
    });

    it('does NOT leave an unrelated earlier message behind at the destination', () => {
      // ⚠ THE BOUNDARY, and it is what stops the fix becoming the defect it replaced. Only the
      // entry raised alongside the navigation is exempted; anything already on screen from an
      // earlier action is still retired, which is the rule the sweep exists to enforce.
      editMode(role(7));

      TestBed.inject(NotificationService).notify('info', 'An earlier, unrelated message.');

      press(SUBMIT_LABEL);
      submittedUpdate(7);

      const surviving = messagesSurvivingNavigation();

      expect(surviving).toContain(ROLE_UPDATED_MESSAGE);
      expect(surviving).not.toContain('An earlier, unrelated message.');
    });
  });

  // ===================================================================================================
  // AREA 7 — ALL SIX BILLING-FREQUENCY CODES ROUND-TRIP VERBATIM
  //
  // The six codes are LOAD-BEARING PERSISTED DATA, not presentation, and three independent proofs
  // establish them:
  //
  //   1. `RoleController.vb:L540-L546` switches on the RAW CHARACTERS — `'N'` leaves the expiry
  //      unbounded, `'O'` sets 9999-12-31, and `'D'`, `'W'`, `'M'`, `'Y'` add days, weeks, months and
  //      years. MIGRATION: the legacy did that arithmetic with `Microsoft.VisualBasic`'s
  //      `DateAdd(DateInterval.Day | Month | Year, …)`, expressing WEEKS as `Day, Period * 7` because
  //      there is no week interval. NO DATE ARITHMETIC HAPPENS ON THE CLIENT: the backend owns it, and
  //      the client submits only the code — which is why the role contract carries no date member and
  //      why not one case in this file constructs a date.
  //   2. The no-trial guard tests `role.TrialFrequency.ToString <> "N"` — a raw character comparison.
  //   3. `FK_Roles_CodeFrequency` constrains both columns to `CodeFrequency([Code])`.
  //
  // ⚠ THE SERIALISATION TRAP, CHECKED RATHER THAN ASSUMED. Had the server declared its frequency as a
  // character-valued enumeration, it would have serialised as the NUMBER 78 without a string converter
  // and as the NAME "None" with one — and NEITHER is the code `'N'`. `core/models/role.model.ts`
  // declares `BillingFrequency` as the union of the six RAW `char(1)` CODES, so the wire contract is
  // correct and every case below asserts the raw code on the wire.
  // ===================================================================================================

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
      // `EditRoles.ascx.vb:L119` and `:L125` both call `Items.FindByValue("N").Selected = True`.
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


  // ===================================================================================================
  // AREA 8 — THE THREE-PART BILLING GATE AND THE CROSS-FIELD TRIAL GATE
  //
  // MEASURED AT `EditRoles.ascx.vb:L212-L229`, conjunct for conjunct.
  //
  //   L216  `If txtServiceFee.Text <> "" And txtBillingPeriod.Text <> ""`
  //         `        And cboBillingFrequency.SelectedItem.Value <> "N" Then`
  //
  //   L226  `If sglServiceFee <> 0 And txtTrialFee.Text <> "" And txtTrialPeriod.Text <> ""`
  //         `        And cboTrialFrequency.SelectedItem.Value <> "N" Then`
  //
  // Fail ANY ONE of the three parts and ALL THREE defaults are submitted instead — fee `0`, period `1`
  // and frequency `'N'`. THE TRIAL GATE'S FIRST CONJUNCT IS THE ALREADY-RESOLVED SERVICE FEE, not a
  // trial field at all, so it is a genuine cross-field rule with a transitive cascade: a role whose
  // billing group was itself suppressed loses its trial too.
  //
  // MIGRATION: the resolution is SILENT and must stay silent. The legacy discarded the values without
  // saying anything, so surfacing a validation error here would reject input the legacy accepted — the
  // opposite of parity.
  //
  // MIGRATION: `Single.Parse` and `Integer.Parse` at `:L217-L218` and `:L227-L228` THREW on a value
  // that slipped past the defective comparison validators, and the only thing between a user and an
  // unhandled exception was a bare `Catch exc As Exception` at `:L271`. That path is eliminated: a
  // value that will not parse is indistinguishable from an absent one and yields the defaults. Nothing
  // is coerced through a zero.
  //
  // MIGRATION: `:L154` gates the TRIAL fields on `TrialFrequency <> "N"` when READING a role — on the
  // FREQUENCY, not on the trial fee, and so asymmetrically with the billing gate one line group above.
  // The asymmetry is reproduced as measured, because a trial with a zero fee and a real frequency is a
  // legitimate FREE trial and testing the fee would hide it.
  //
  // MIGRATION: this screen formatted money with `"#,##0.00"` — WITH a thousands separator
  // (`:L146,L147,L155`) — while the sibling list screen's `FormatPrice` used `"##0.00"` WITHOUT one
  // (`Roles.ascx.vb:L182`). Each screen keeps its own format; neither is harmonised to the other.
  // ===================================================================================================

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

  // ===================================================================================================
  // AREA 9 — MODE BY PRESENCE, THE UNGROUPED ROLE GROUP, AND SENTINEL RENDERING
  //
  // ⚠ `dbo.Roles.RoleID` IS `IDENTITY (0, 1)`
  // (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L115`), so ZERO IS A REAL
  // ROLE ID and must open the EDIT form. The legacy could use `-1` as its "adding" marker because `-1`
  // was simultaneously `Null.NullInteger` and outside the identity range; a route parameter is either
  // supplied or it is not, which is a cleaner discriminator and is the one used here.
  //
  // Consequently the mode decision is taken on PRESENCE alone. Forbidden here and in the component
  // alike: `if (id)`, `!id`, `id > 0`, `id ?? -1`, `?? 0`, `|| 0`, `|| ''` and `Math.abs(`.
  // ===================================================================================================

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

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { replaceUrl: true });
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
      /*
       * ⚠ THIS CASE USED TO ASSERT THE DEFECT, TITLE AND ALL. It was called "stays in creation mode
       * for a route parameter that is not an integer" and required the heading to read
       * `Add New Role` — which is precisely the fault: `/roles/abc` presented a complete, enabled
       * creation form, and because the legacy submit is labelled `Update` in both modes, the address
       * and the verb together read as "editing a role called abc" while pressing it would have
       * CREATED one. Runtime testing called it the most deceptive of the four such screens.
       *
       * The case survived the fix only because it asserted the HEADING rather than the form, so it
       * is rewritten to assert the contract that actually matters. The `expectNone` below is kept
       * from the original, which had that half right.
       */
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', 'not-a-role');
      fixture.detectChanges();

      answerGroups();

      // The address NAMES a role and fails to resolve it, so the screen is the edit screen — which
      // is also what the route's own document title declares. Reading `Add New Role` here is the
      // three-labels-on-one-screen defect measured in a browser.
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

      // The state is STATED rather than merely left blank, so the address is diagnosable.
      expect(textOf(queryOrFail<Element>(host(), '.role-form__notice'))).toBe(
        UNREADABLE_ADDRESS_MESSAGE,
      );

      httpMock.expectNone(
        (candidate) => candidate.url.startsWith(`${ROLES_URL}/`),
        'an unusable parameter reads nothing',
      );
    });

    it('reads role 1 for a route parameter written with leading zeros', () => {
      // ⚠ A REAL ADDRESS, NOT A TYPO TO BE PUNISHED. `00001` is a well-formed decimal integer naming
      // role 1, the API's own `int.TryParse` accepts it, and the portal and user screens normalise it.
      // This screen used to refuse it through a text round-trip test — and the refusal was invisible,
      // because a null key means "creation route", so the CREATE form rendered under an Edit heading.
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
      // ⚠ THE REQUEST IS THE DEFECT, NOT THE ID. Every identifier column in this schema is a SQL
      // Server `int` and the API binds the segment with `int.TryParse`, so this value cannot name a
      // record and the round trip was guaranteed to fail. The portal and user screens refused it
      // without a request; this screen forwarded it.
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

      // Counted, because consuming a request is not the same as asserting one: `expectRequest` raises
      // when the read is missing but registers no expectation, so the case would pass vacuously if the
      // bound ever started refusing this id.
      expect(read.request.url)
        .withContext('the largest addressable id is read, not refused')
        .toBe(roleUrl(2147483647));

      read.flush(envelope(role(2147483647)));
      fixture.detectChanges();
    });

    it('takes a person back to the listing when the role has gone', () => {
      // `EditRoles.ascx.vb:L170-L172` treated an unreadable role as an attempt to reach an item outside
      // the module and redirected without telling the operator anything. The redirect is preserved; a
      // message is added because a silent bounce is indistinguishable from a broken link.
      fixture = TestBed.createComponent(RoleFormComponent);
      fixture.componentRef.setInput('roleId', '404');
      fixture.detectChanges();

      answerGroups();

      expectRequest('GET', roleUrl(404), 'the role read').flush(silentProblem(404, 'role-missing'), {
        status: 404,
        statusText: 'Not Found',
      });
      fixture.detectChanges();

      expect(lastAnnouncement()).toEqual({ severity: 'warning', message: ROLE_NOT_FOUND_MESSAGE });
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { replaceUrl: true });

      // ⚠ AND IT SURVIVES THE ARRIVAL IT CAUSED. The shell retires notifications on a completed
      // navigation, so raising this one in the same task as the navigation was not enough: measured
      // in a real browser, the destination's live region stayed empty and 226 consecutive frames
      // after the listing painted were pixel-identical, so the operator was moved back to the list
      // with no explanation at all. Asserting the survival rather than the call, because the call is
      // not the behaviour the operator experiences.
      expect(messagesSurvivingNavigation())
        .withContext('the explanation must be readable at the destination')
        .toContain(ROLE_NOT_FOUND_MESSAGE);
    });

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
      // MIGRATION: the legacy select carried `"-1"` and the provider then wrapped the argument in
      // `GetNull(RoleGroupId)` so that it reached SQL Server as NULL. The collapse now happens ONCE, at
      // the contract boundary, so the absence travels as an absence.
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
      // `Roles.ascx.vb:L175-L184` proves the display rule: `FormatPrice` starts at `Null.NullString`
      // and returns it unchanged when the price equals `Null.NullSingle`.
      editMode(role(7, { serviceFee: ABSENT_MONEY }));

      expect(input(CONTROL_ID.serviceFee).value).toBe('');
      expect(documentText()).not.toContain(String(ABSENT_MONEY));
    });

    it('renders an absent period as EMPTY, never as minus one', () => {
      // `Roles.ascx.vb:L152-L161` proves the display rule for `FormatPeriod` in the same way.
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


  // ===================================================================================================
  // AREA 10 — THE PORTAL-PROTECTED ROLES
  //
  // MEASURED AT `EditRoles.ascx.vb:L174-L182`, two guards with a deliberate asymmetry between them:
  //
  //   L174-L178  `If RoleID = PortalSettings.AdministratorRoleId Or RoleID = PortalSettings.RegisteredRoleId`
  //              then hide Delete, hide Update, and `ActivateControls(False)`.
  //   L180-L182  `If RoleID = PortalSettings.RegisteredRoleId` then ADDITIONALLY hide Manage.
  //
  // So the ADMINISTRATOR role keeps its membership command while the registered-users role does not,
  // and that asymmetry is preserved rather than tidied: the administrator role's membership is genuinely
  // manageable, whereas every authenticated user holds the other.
  //
  // MIGRATION — THE MEASURED GAP, STATED PLAINLY. NOTHING ON A ROLE DISCRIMINATES A PROTECTED ONE:
  // `RoleInfo.vb` declares no system, administrator or built-in member of any kind, and the target's role
  // contract carries none either. `AdministratorRoleId` and `RegisteredRoleId` are PORTAL-scoped
  // properties that this screen may not read — a portal-settings call is outside its endpoint boundary.
  // The component therefore exposes them as OPTIONAL INPUTS, and these cases drive them. Supplied, the
  // measured behaviour is reproduced exactly; unsupplied, which is the case in the running application
  // today, the form stays fully editable and the API's own refusal governs, arriving as a `403` that is
  // surfaced as a warning. NO ROLE ID IS HARDCODED, no decision is taken on a role's NAME, and no
  // endpoint is invented.
  //
  // MIGRATION — DEFECT 4: the legacy test is written with an unparenthesised `Or`, which in VB is
  // NON-SHORT-CIRCUITING, so both comparisons were evaluated every time. The target's `||` DOES
  // short-circuit. The two are semantically identical here because both operands are side-effect-free
  // integer comparisons, and the change of operator class is recorded rather than made silently.
  //
  // MIGRATION: `ActivateControls(False)` disabled ELEVEN named controls and conspicuously did NOT
  // disable the icon picker or the read-only RSVP link. That omission is a measured legacy
  // inconsistency, not a rule — leaving a picker live on a form whose update command has been hidden
  // serves no purpose — so the WHOLE form is disabled here, which additionally covers the icon path
  // field that replaces the picker.
  // ===================================================================================================

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
      // ⚠ THE FACTS ARE READ, NOT AWAITED FROM A CALLER. They were three optional inputs that nothing
      // in the application supplied — no route, no parent template — so the guards below shipped
      // permanently disarmed. The tenant comes from the caller's identity because this screen names no
      // portal, and the key is passed through untouched: `Portals.PortalID` is `IDENTITY(-1, 1)`, so
      // -1 and 0 are both real tenants and a truthiness test would skip the request for either.
      editMode(role(7));

      expect(loadCurrentPortalContext).toHaveBeenCalledWith(TENANT_ID);
      expect(loadCurrentPortalContext).toHaveBeenCalledTimes(1);
    });

    it('stays editable while the tenant record is still OUTSTANDING, and defers to the API', () => {
      // ⚠ THE FAIL-SAFE DIRECTION, AND IT IS DELIBERATE. Until the record arrives each key is absent,
      // every comparison is false and the form behaves exactly as it did before the guard existed:
      // the command is offered and the server decides, its refusal surfacing as a warning. Locking the
      // form until the read completed would instead take a capability away from EVERY role for the
      // duration of a request.
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
      // The record arrives after the form is already on screen, which is the ordinary sequence: the
      // request is issued on construction and answers a moment later. Every consumer is a `computed`
      // over the store's signals, so the transition needs no reload and no second visit.
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
      // ⚠ `Roles.RoleID` is `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider:L114`), so the administrator
      // role of a freshly created tenant genuinely carries nought — and a guard that tested either
      // side for truthiness would leave exactly that role unprotected.
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

  // ===================================================================================================
  // THE NAME IS TIDIED, AND A BLANK ONE IS REFUSED BY THE RULE ITSELF
  //
  // Two separate obligations, settled in two places, and the split is the point.
  //
  // EMPTINESS is settled by the presence rule on the control, which is the shared TRIM-AWARE rule
  // rather than `Validators.required`. `Validators.required` rejects only the empty string, so three
  // spaces satisfy it — while an ASP.NET `RequiredFieldValidator` trimmed before comparing against its
  // initial value and refused exactly that, and `CreateRoleRequestValidator` declares `NotEmpty`,
  // which treats a whitespace-only string as empty. Under the framework rule alone this screen
  // declared valid what both the legacy screen and this API refuse, and the operator was shown a
  // server rejection for a field the form had raised no complaint about. The rule is the same one the
  // sibling role-group form carries, so the two screens refuse a blank name at the same moment and in
  // the same words.
  //
  // PADDING is settled on submit, by tidying the name INTO ITS OWN CONTROL rather than on the way
  // into the request, so the value that was validated and the value that is sent are one string.
  // MIGRATION: the legacy stored what was posted, padding and all. Trimming it is a deliberate
  // divergence, made because `UpdateRole` has no `RoleName` parameter at all
  // (`Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb:L242-L243`) — a name
  // stored with invisible padding at creation could never afterwards be corrected.
  // ===================================================================================================

  describe('tidying the role name before judging it', () => {
    it('REFUSES a whitespace-only name rather than posting an empty one', () => {
      createMode();
      fillRoleName('   ');

      press(SUBMIT_LABEL);

      // ⚠ NOTHING IS SENT. This is the assertion the defect failed: a `POST` went out carrying
      // `roleName: ""`. The backend verification in teardown fails on any unconsumed request, so a
      // creation issued here would be caught twice over.
      httpMock.expectNone(() => true);

      // And the requirement is reported, beside a field the operator did fill in — they typed
      // something, so they are owed an explanation of why it amounts to nothing.
      expect(messagesFor(CONTROL_ID.roleName))
        .withContext('the form complains rather than deferring to the server')
        .not.toEqual([]);
    });

    it('refuses a whitespace-only name AS SOON AS IT IS TYPED, without waiting for a submit', () => {
      // The moment matters, and it is the moment the sibling form uses. An ASP.NET validator was
      // wired to the control's own change event and updated its display there, so the legacy
      // reported this before any postback; a rule that waited for the submit would report it later
      // than the screen it replaces.
      createMode();
      fillRoleName('   ');

      expect(messagesFor(CONTROL_ID.roleName))
        .withContext('the presence rule trims before judging, so it fires on the entry itself')
        .not.toEqual([]);
    });

    it('leaves a refused whitespace-only entry exactly as typed, rather than blanking the box', () => {
      // The box is NOT rewritten under the operator: the legacy validator refused the value and
      // left it alone, and a field that empties itself as you are told it is required reads as the
      // screen having eaten the entry.
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
      // ⚠ ONLY THE NAME IS TIDIED INTO ITS CONTROL, and the description is why that distinction is
      // worth stating rather than generalising. It reaches a NULLABLE member through `textOrNull`,
      // whose rule is different in kind: a blank entry becomes `null` rather than being refused,
      // because "no description" is a legitimate answer where "no name" is not. That rule stays where
      // it is, on the way into the request, and nothing about it is re-judged.
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
    it('offers EXACTLY Update and Cancel when creating', () => {
      createMode();

      expect(commandLabels()).toEqual([SUBMIT_LABEL, CANCEL_LABEL]);
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

  // ===================================================================================================
  // AREA 12 — THE THREE COMMANDS THAT DO NOT VALIDATE
  //
  // MEASURED: `CausesValidation="False"` is declared on `cmdCancel` (`editroles.ascx:L183`), `cmdDelete`
  // (`:L186`) and `cmdManage` (`:L189`). `cmdUpdate` (`:L179-L180`) OMITS the attribute, and the
  // attribute defaults to TRUE — which is why the legacy update handler opens with `If Page.IsValid Then`
  // at `:L210` while `cmdDelete_Click` at `:L287-L303` contains no such check anywhere.
  //
  // MIGRATION: the delete command's browser confirmation at `:L112` —
  // `ClientAPI.AddButtonConfirm(cmdDelete, Localization.GetString("DeleteItem"))` — becomes the shared
  // dialogue, which adds the focus trap and the escape key the browser confirmation never had. Its
  // wording is the global resource value VERBATIM.
  // ===================================================================================================

  describe('AREA 12 — the commands that do not validate', () => {
    it('abandons the form without validating, even when it is invalid', () => {
      createMode();

      // Left invalid: the role name is empty and demanded.
      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE]);
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
      expect(navigateSpy).toHaveBeenCalledWith([ROLE_LIST_ROUTE], { replaceUrl: true });
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


  // ===================================================================================================
  // AREA 13 — THE RENDERED DOCUMENT
  //
  // MEASURED DISCLOSURE STATE. `dshBasic` (`editroles.ascx:L11-L12`) declares NO expanded state and the
  // legacy default is EXPANDED. `dshAdvanced` (`:L68-L70`) declares `IsExpanded="False"` and is therefore
  // COLLAPSED ON FIRST RENDER.
  //
  // MIGRATION — A DELIBERATE DEPARTURE ON THE BASIC SECTION, RECORDED RATHER THAN SMUGGLED. The basic
  // section carries NO `aria-expanded`, and that is correct rather than an omission: the attribute is
  // defined for elements that expose a disclosure state, the section has no collapse affordance at all,
  // and adding a button purely to host the attribute would put a control on the page that cannot be
  // operated. "Always expanded" is expressed by the content being unconditionally present, which is what
  // these cases assert. The ADVANCED section does collapse, so it carries the attribute on a natively
  // keyboard-reachable summary bound to the element's own open state.
  //
  // MIGRATION: the legacy section-head control withdrew its own toggle from the tab order with a
  // negative tab index, so the legacy collapse was operable BY POINTER ONLY. A native disclosure summary
  // is keyboard-reachable and operable by Enter and Space with no scripting, which is the faithful
  // reversal of that defect. The legacy labelled-field control compounded it, putting both its help link
  // and its image at a negative tab index with no alternative text INSIDE the label element.
  //
  // MIGRATION: all three legacy layout tables — the outer table at `:L6`, `tblBasic` at `:L13` and
  // `tblAdvanced` at `:L71`, each declaring a "Design Table" summary — are LAYOUT tables and are replaced
  // by grid and flexible-box styling. This screen has no data grid at all. The break element at `:L67`,
  // the ten-pixel spacer rows at `:L20`, `:L78` and `:L82`, the paired non-breaking spaces at `:L105` and
  // `:L137`, and the single ones between the four commands at `:L181`, `:L184` and `:L187` all become
  // stylesheet concerns.
  // ===================================================================================================

  describe('AREA 13 — the rendered document', () => {
    /** The advanced disclosure, which is the only collapsible region on this screen. */
    function advancedSection(): HTMLDetailsElement {
      return queryOrFail<HTMLDetailsElement>(host(), 'details.role-form__section--advanced');
    }

    /** Its toggle. */
    function advancedSummary(): HTMLElement {
      return queryOrFail<HTMLElement>(advancedSection(), 'summary');
    }

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

      // ⚠ THE CLAIM IS ABOUT INTERACTIVE ELEMENTS, AND THIS USED TO BE ASSERTED ABOUT ALL OF THEM.
      // The rule being protected is the legacy defect's inverse: the legacy help affordance and its
      // image were both given a negative index, so the only control that could reveal a collapsed
      // section was unreachable by keyboard. A negative index on a NON-interactive element is the
      // opposite kind of thing - it withdraws nothing, because such an element was never in the tab
      // order, and it grants only the ability to be focused deliberately by script. The shared outcome
      // banner carries exactly that, so a screen can bring a server refusal to a reader who pressed a
      // control at the foot of a form taller than the viewport. Asserting over every element made the
      // suite reject that affordance while the rule it exists for was never in question, so the query
      // now names the interactive vocabulary the rule is about.
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
      // The legacy shared one label across the pair (`editroles.ascx:L100` naming `txtBillingPeriod`,
      // with `cboBillingFrequency` at `:L106-L107` beside it and no label of its own), and
      // `BillingPeriod.Help` says outright that the two fields are used in conjunction. Splitting them
      // would leave the select unnamed.
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
      // MIGRATION — DEFECT 5, reproduced rather than repaired. `EditRoles.ascx.vb:L104-L109` runs OUTSIDE
      // the not-a-postback branch and shows the label whenever the portal record is absent OR its
      // processor identifier is empty, while its own comment claims the opposite. THE CODE IS THE
      // BEHAVIOUR and it is also the sensible reading, so the code is what is reproduced and the
      // contradiction is recorded here.
      //
      // The resource value wraps one word in bold. That word is RE-AUTHORED as a real emphasis element
      // rather than passed through any markup binding, because resource text is untrusted: this very
      // admin tree holds an entry carrying a live remote script block. The source typo in "fee-base" is
      // preserved, because the wording is the site's own.
      //
      // ⚠ THE TENANT RECORD IS RESOLVED HERE, WITH NO PROCESSOR, WHICH IS THE STATE THAT WARNS. The
      // screen used to have no way of knowing either fact — the input nothing supplied defaulted to
      // "unconfigured" — so the warning was permanently on screen. It is now read, and the one
      // deliberate departure from the legacy expression is recorded in the case below.
      tenantResolved.set(true);
      processorConfigured.set(false);
      createMode();

      const emphasis: Element = queryOrFail<Element>(host(), '.role-form__warning strong');

      expect(textOf(emphasis)).toBe('Warning:');
      expect(documentText()).toContain('fee-base roles/services');
    });

    it('WITHHOLDS the processor warning until the tenant record resolves', () => {
      // ⚠ THE ONE DELIBERATE DEPARTURE FROM `EditRoles.ascx.vb:L104-L109`, and it is recorded rather
      // than absorbed. The legacy's first clause, `objPortalInfo Is Nothing`, warned when the portal
      // could not be read AT ALL — so an unread portal produced the same warning as a portal with no
      // processor. Telling an administrator to configure a payment processor on the strength of a
      // request that has not answered is an assertion rather than a default, so the warning waits.
      //
      // Nothing is supplied, so the tenant stays unresolved: the state every visit began in while the
      // fact was an input nobody passed.
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

  // ---------------------------------------------------------------------------------------------------
  // AREA 14 — WHOSE WRITE SETTLED, AND WHO CLASSIFIES A REFUSAL
  //
  // Two corrections are pinned here, and each closed a defect that only appears when something else in
  // the application is writing at the same time or when a status other than the common ones arrives.
  //
  // WRITE IDENTITY. The shared store published ONE boolean for "a write is in flight" and ONE failure
  // slot. This screen watched the boolean fall and then read the slot, so an unrelated role write
  // settling elsewhere released this screen's submit lock, drained its outstanding state and could hand
  // it somebody else's refusal to report. The store now issues an identifier per write and publishes
  // the settled outcome under it, and this screen acts only on the identifier it was given.
  //
  // SEVERITY OWNERSHIP. This screen carried its own status-to-severity table, which disagreed with the
  // shared one at two statuses: 404 (this screen said warning, the shared table says warning — but the
  // local table reached that answer for its own reasons) and 429, where the local table said error
  // while the shared table deliberately says info, because nothing was rejected on its merits. The
  // local table is gone; the shared classification is consumed and only the WORDING is overridden.
  // ---------------------------------------------------------------------------------------------------

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
      // ⚠ THE DISAGREEMENT THIS CLOSES. The local table resolved every status other than 404 to
      // `error`, so a 429 was announced as a failure on this screen while the shared classifier calls
      // it `info` — nothing was rejected on its merits, the caller is simply early. Two surfaces on one
      // screen disagreed about the same response.
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
      // MIGRATION: `ctlIcon` (`editroles.ascx:L169-L170`) was a picker over the portal's own files, with
      // an image-type filter applied at `EditRoles.ascx.vb:L129`. It is reduced to a plain path field,
      // because the target exposes NO filesystem, upload or file-listing endpoint. The path itself
      // round-trips unchanged; only the means of choosing it is lost. No picker and no upload control is
      // asserted for, because neither could exist.
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
      // MIGRATION: `txtRSVPLink` (`editroles.ascx:L161`), populated at `EditRoles.ascx.vb:L165-L168`,
      // addressed a self-service subscribe flow the target does not implement, so a link to it would be
      // a link to nothing.
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

  // ===================================================================================================
  // AREA 15 — NUMERIC INTEGRITY, THE REVISION MARKER, AND THE RECOVERY PATH
  //
  // Three defects found by runtime testing, none of which any earlier case in this file could have
  // caught, because each of them produced a form that reported itself VALID.
  //
  //   1. `1,5` in a fee field was accepted, the separator was stripped, and `15` was persisted — a
  //      TEN-FOLD monetary error, silent end to end. The pattern admitted a group of any length.
  //   2. A period beyond `Int32` and a fee with more digits than a double carries were both accepted
  //      here and rewritten or refused elsewhere: the first came back as a server refusal naming no
  //      field, the second reached storage as a different number from the one typed.
  //   3. Two people editing one role both saved their whole snapshot and the later save silently
  //      discarded the earlier one, because no revision marker travelled with the update.
  //
  // Every case below asserts on what leaves the screen or what the person can see, never on a private
  // member, so none of them can pass while the defect survives.
  // ===================================================================================================

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
      /** The sentence the advanced section prints above the paid-membership boxes, or ''. */
      function withheldNotice(): string {
        const notices: readonly string[] = textsOf('p.role-form__notice');

        return notices.find((sentence) => sentence.startsWith('This role has no')) ?? '';
      }

      // ⚠ THE INCONSISTENCY BETWEEN TWO SCREENS, AND WHY THE BOXES ARE NOT SIMPLY FILLED.
      //
      // `/roles/0` holds `serviceFee 0.0000`, `billingPeriod 0`, `trialFee 0.0000`, `trialPeriod 0`, and
      // the role LISTING renders those zeros as "0.00" and "0" - `Roles.ascx.vb:L152-L185` prints every
      // non-sentinel value verbatim. This form leaves the same four boxes EMPTY, because
      // `EditRoles.ascx.vb:L146-L156` fills them only for a priced role. Both are faithful; together
      // they showed one record two ways with nothing saying which reading was the record.
      //
      // Rendering the zeros into these boxes was the reported repair and is refused, because it breaks
      // two rules this screen must match: `valBillingPeriod2` (`editroles.ascx:L111-L114`) is
      // `GreaterThan 0`, so an untouched role would open ALREADY INVALID with its Update command blocked
      // on a number the record actually holds; and the write gate at `:L216` is
      // `txtBillingPeriod.Text <> ""`, so filling the box changes what an untouched save STORES. The
      // values are therefore stated beside the boxes, in the listing's own formatting.
      it('states every withheld value, in the wording the role listing uses', () => {
        editMode(role(0));
        openAdvanced();

        expect(withheldNotice()).toBe(
          'This role has no paid-membership terms, so the boxes below are left empty. ' +
            'The values held for it are Service Fee 0.00, Billing Period 0, Trial Fee 0.00 and ' +
            'Trial Period 0.',
        );
        // And the boxes themselves are untouched, which is the half of this the legacy owns.
        expect(input(CONTROL_ID.serviceFee).value).toBe('');
        expect(input(CONTROL_ID.billingPeriod).value).toBe('');
        expect(input(CONTROL_ID.trialFee).value).toBe('');
        expect(input(CONTROL_ID.trialPeriod).value).toBe('');
        // ⚠ AND THE FORM IS STILL SAVABLE, which is exactly what filling the boxes would have cost: a
        // rendered `0` in the period box fails the legacy's own `GreaterThan 0` rule, so the screen would
        // open with a message about a number nobody typed and an Update command that refuses to run.
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
        // The billing group is suppressed by its FEE, so a role with a zero fee beside a real
        // recurrence unit hides that unit behind a select reading "None". The trial group is suppressed
        // BY its frequency being None, so a suppressed trial select is already showing the stored value
        // and is deliberately not listed.
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
        // The two groups are suppressed independently and by different tests - the billing group by its
        // FEE (`EditRoles.ascx.vb:L146`) and the trial group by its FREQUENCY (`:L154`) - so a priced
        // role with no trial hides two values and shows three, and the sentence must say which.
        editMode(role(7, { serviceFee: 25, billingPeriod: 1, billingFrequency: 'M' }));
        openAdvanced();

        expect(withheldNotice()).toBe(
          'This role has no trial, so the trial boxes below are left empty. ' +
            'The values held for it are Trial Fee 0.00 and Trial Period 0.',
        );
        expect(input(CONTROL_ID.serviceFee).value).toBe('25.00');
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
      // ⚠ THE CASE THAT NAMES THE DEFECT. `1,5` is not a number in any locale this screen writes, and
      // the screen's own formatter emits groups of three (`EditRoles.ascx.vb:L147,L155`). Accepting it
      // and stripping the comma turned one and a half into fifteen.
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

        // Previously this reached the API, failed to BIND — before any validator ran — and came back
        // as `"request": ["The request field is required."]`, which names no field at all.
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

        // `007` was already being sent as `7`; the substitution was correct and invisible, which is the
        // defect. `EditRoles.ascx.vb:L147` re-rendered the box through `Format(fee, "#,##0.00")` on
        // every postback, so this is the legacy's own display behaviour on a client-side event.
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
        // already in canonical form and would survive a rewrite unchanged in any case. What the case
        // guards is the pair of consequences that a rewrite here would have: the displayed value must
        // not move under someone who only tabbed through the field, and the form must not become dirty
        // without an edit — because an unsaved-changes guard would then challenge an exit nobody
        // initiated. Pristine state is read from the class Angular renders onto the form element, which
        // is a fact about the document rather than a private member.
        expect(box.value).toBe(loaded);
        expect(queryOrFail<HTMLFormElement>(host(), 'form').classList).toContain('ng-pristine');
      });
    });

    describe('a refused submit reaches the control that caused it', () => {
      it('opens the advanced section and focuses the offending field', () => {
        editMode(role(7));

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

    describe('the revision marker', () => {
      it('carries the token from the read into the update', () => {
        editMode(role(7, { concurrencyToken: 'revision-from-the-read' }));
        type(CONTROL_ID.description, 'Edited');
        press('Update');

        // The token must come from the role that was READ. Re-reading it before a save would obtain the
        // CURRENT revision, and the check would then always pass while looking watertight.
        expect(submittedUpdate(7).concurrencyToken).toBe('revision-from-the-read');
      });

      // ⚠ MINOR (client/API contract) — THIS SPECIFICATION WAS REWRITTEN, AND THE REWRITE IS THE FIX.
      //
      // It asserted that a read serving NO token produced a token-less update, and called that a
      // last-writer-wins update. The premise was false: `RoleDetailDto.ConcurrencyToken` is declared
      // `public string ... = string.Empty` and is always populated by `RoleMappings.ConcurrencyTokenFor`,
      // so a read serving no token is a MALFORMED response rather than a supported mode. Tolerating it was
      // the defect — the null decoded, reached a write the server does treat as last-writer-wins, and the
      // optimistic check was skipped with nothing reporting it, so a lost update presented as a success.
      //
      // What survives is the ONLY legitimate null: a creation, which has no prior revision. That case is
      // asserted immediately below and is unchanged.
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
        // The server's member is non-nullable and its declared default IS the empty string, so an
        // installation that has never derived a marker serves one. It is carried as received rather than
        // substituted or dropped: substituting would fabricate a marker, and dropping it would opt out of
        // the very check the round trip exists to perform.
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

        // The server's explanation is on the BANNER; the notification states the outcome. Asserted on
        // both surfaces because the sentence used to be on both AT ONCE.
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

        // The re-read must actually reach the form. `appliedRoleKey` exists to stop a second arrival of
        // the same role overwriting typed values, which is right for the store's echo after a save and
        // wrong here, where replacing them is the entire purpose of the command.
        expect(textArea().value).toBe('Theirs');
        expect(host().querySelector('button.role-form__conflict-reload')).toBeNull();

        // And the next save carries the NEW revision, so the recovery actually recovers.
        type(CONTROL_ID.description, 'Mine again');
        press('Update');

        expect(submittedUpdate(7).concurrencyToken).toBe('revision-2');
      });
    });
  });

  // ===================================================================================================
  // AREA 16 — NAMING A PAIR, DISCLOSING A TRUNCATION, AND STATING A BUSY FORM
  //
  // Three findings whose common shape is that the screen already behaved correctly and said nothing
  // about it, so nothing an operator or a screen reader could perceive distinguished the right state
  // from the wrong one.
  //
  //   R-M8   Eight controls resolved to SIX distinct accessible names, because both halves of each
  //          period pair were named from the field's single visible label.
  //   R-M18  `maxlength` discarded the surplus of a pasted name silently, and the shortened name then
  //          collided with a stored one — producing a duplicate-name refusal naming a value the
  //          operator had never typed.
  //   R-M21  Five presses inside one task all found the submit button enabled, because no
  //          change-detection pass had run between them; the busy state was conveyed by a spinner and
  //          by nothing that is programmatically determinable.
  // ===================================================================================================

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
      /**
       * ⚠ THE CASE THAT NAMES THE DEFECT. The legacy screen put `txtBillingPeriod` and
       * `cboBillingFrequency` under the single label `plBillingPeriod` (`editroles.ascx` L98-L115),
       * so the second control carried no name of its own; the shared field then named it from the
       * field's visible label, which is correct for a lone control and leaves a PAIR announcing one
       * identical string twice.
       */
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
       * ⚠ WCAG 2.5.3 LABEL IN NAME. The qualifier is APPENDED, so the text an operator can read
       * remains a prefix of the name an assistive technology announces — which is what lets someone
       * using voice control say the visible words and reach the control.
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
       * ⚠ THE QUALIFIERS MUST NOT BE DRAWN. They exist to be announced; painting them would put two
       * stray words into a field whose visible wording is a resource value this migration preserves.
       * The clipping helper is asserted through the rendered class, because the rule that hides it
       * lives in a stylesheet the test bed does not apply.
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
       * Runtime testing pasted fifty-one characters, watched fifty arrive, and then received a
       * duplicate-name refusal naming a role the operator had never typed.
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
       * ⚠ AND IT MUST BE SILENT BELOW THE LIMIT. A notice standing permanently beside a field is a
       * notice nobody reads, and this one earns its place only by appearing at the moment the field
       * stops accepting input.
       */
      it('says nothing while the name is shorter than the limit', () => {
        createMode();
        type(CONTROL_ID.roleName, 'X'.repeat(49));

        expect(lengthNotice()).toBeNull();
      });

      /**
       * ⚠ IT IS A NOTICE AND NOT AN ERROR. The value at the limit is VALID — `maxLength(50)` is
       * satisfied by exactly fifty — so it must not appear in the field's error region, must not be
       * announced assertively, and must not mark the control invalid. Conflating the two would train
       * an operator to treat a real refusal as noise.
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
       * presses found the button enabled, because no change-detection pass had run between them, and
       * the only signal of the in-flight write was a spinner — nothing about the state was
       * programmatically determinable. That is what this case fixes and asserts.
       *
       * ⚠ IT IS NOT COVERAGE OF `SubmitGuardDirective`, and the earlier wording here implied otherwise
       * by citing a manual five-press observation as though the guard were proven. A manual observation
       * is not a test, and nothing below would fail if the directive were deleted. The directive is
       * asserted against itself, on a host that carries no guard of its own, in
       * `src/app/shared/directives/submit-guard.directive.spec.ts`.
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
       * anything when the form is invalid, so a busy state raised optimistically on the press would
       * stick forever and tell every reader the screen was working when it was waiting for them.
       */
      it('never marks a form busy when the submit was refused locally', () => {
        createMode();
        press('Update');

        expect(busy()).toBeNull();
      });
    });
  });
});
