/**
 * Manage Users in Role — the role-centric half of the legacy security-roles screen.
 *
 * Route `/roles/:roleId/users`. This is the routed CONTAINER for the screen: it reads the
 * role from the route, lists the accounts holding it, enrols a new account with optional
 * effective and expiry bounds, and removes an existing membership behind a confirmation.
 *
 * ## Legacy provenance
 *
 * Ported from `Website/admin/Security/SecurityRoles.ascx.vb` (668 lines, the workflow
 * authority) and `Website/admin/Security/securityroles.ascx` (92 lines, authoritative for
 * the fields, the three validators and the grid), with wording taken from
 * `Website/admin/Security/App_LocalResources/SecurityRoles.ascx.resx` and
 * `Website/App_GlobalResources/SharedResources.resx`. Behavioural authority for the
 * assignment rules is `Library/Components/Security/Roles/RoleController.vb`. All of those
 * files are read-only references and are unchanged by this work.
 *
 * MIGRATION: the legacy control served TWO workflows from one file, choosing between them
 * on a mode discriminator — `plUsers`/`plRoles` shared one table cell
 * (`securityroles.ascx:L14`) and `cboUsers`/`cboRoles` shared another (`:L26`/`:L27`).
 * This component implements ONLY the role-centric half. `SecurityRoles.ascx.vb:L183-L198`
 * is the proof of what that half renders: it puts the single role into `cboRoles`, sets
 * the title from `RoleTitle.Text`, and then hides BOTH the role dropdown and its label
 * (`:L195` and `:L196`). The account-centric half — "Manage Roles for User", reached at
 * `:L201-L224` and `:L249-L255` — belongs to the account feature and is out of scope, so
 * `plRoles.*`, `AddRole.Text`, `UserTitle.Text` and the design-time `cmdAdd.Text`
 * default of 'Add Role' are deliberately unused here. A documented reduction.
 *
 * ## What the sibling template must declare
 *
 * The `.html` and `.scss` beside this file, and the feature's route table, are authored
 * separately. This class publishes the whole contract the template binds to, and the four
 * grid columns are assembled from `<ng-template>` references the template declares. The
 * reference names are published as {@link ROLE_ASSIGNMENT_CELL_TEMPLATE} so there is one
 * spelling of each rather than two. Every cell template receives the shared grid's cell
 * context, so `let-row="row"` (or the implicit value) is one {@link UserRole} row:
 *
 * - `#roleAssignmentCommandsCell` — the row command. Wrap it in
 *   `@if (canRemove(row))` and call {@link RoleAssignmentComponent.requestRemoval}. This
 *   reproduces `visible='<%# DeleteButtonVisible(UserID, RoleID) %>'` from
 *   `securityroles.ascx:L68`, which the shared grid cannot express because it publishes no
 *   row-command output — row commands are projected by the consuming feature precisely so
 *   a per-row predicate like this one is possible.
 * - `#roleAssignmentUserCell` — `<a [routerLink]="['/users', row.userId]">{{ row.displayName }}</a>`.
 * - `#roleAssignmentEffectiveDateCell` — `{{ row.effectiveDate | dateDisplay }}`.
 * - `#roleAssignmentExpiryDateCell` — `{{ row.expiryDate | dateDisplay }}`.
 *
 * All four must be declared at the TOP LEVEL of the template. A view query does not descend
 * into an embedded view, so a reference nested inside `@if`, `@for` or another `<ng-template>`
 * is invisible to the query and its column would silently fall back.
 *
 * Rows are keyed by `userRoleId`, which is the membership's own surrogate key, even though
 * the removal address is keyed by `userId`. The grid's caption is projected with the
 * `dataTableCaption` attribute and its wording is {@link ROLE_ASSIGNMENT_TEXT.caption}.
 *
 * Each column degrades safely: when a template reference has not resolved the column still
 * renders its value as plain text, so the grid can never come up empty because a reference
 * name drifted. The commands column is the one exception — a command has no plain-text
 * form — so {@link RoleAssignmentComponent.requestRemoval} is public and may be reached
 * from anywhere in the template.
 *
 * ## Every role read and every role write goes through the store
 *
 * The role, the membership listing and both membership writes are all owned by
 * {@link RoleStore}. This component holds NO copy of any of them: the role and the rows are
 * derived from the store's slices, and the two writes are commands issued to it.
 *
 * That is a correction. This screen previously called the role transport directly while the
 * store held its own copy of the same role and the same memberships, so the two could
 * disagree — a removal accepted here left a sibling screen's listing showing the row, and a
 * role renamed on the editor left this heading showing the old name. One owner removes the
 * possibility rather than papering over it.
 *
 * The listing is read at the COMPLETE scope, `RoleStore.loadAllAssignments`, because the
 * legacy grid was unpaged — `securityroles.ascx:L56` declares no `AllowPaging`, no pager
 * style and `enableviewstate="false"` — so a first page would put an eleventh member's
 * Delete command out of reach. The store follows every page the server reports and
 * remembers that the caller asked for the whole set, so the re-read it performs after each
 * write reproduces the whole set rather than collapsing it to a page.
 *
 * The one transport this screen still calls directly is the ACCOUNT LOOKUP behind the search
 * field, `UserService.list`. That is deliberate and is not the defect above: the matches it
 * returns are a transient candidate list held nowhere else, owned by nothing else and
 * discarded when the field is cleared, so there is no second copy to diverge from. Routing it
 * through the account store would instead make this screen mutate that store's shared search
 * term and page coordinate, which would move a sibling account listing under its own
 * operator — trading a copy that cannot diverge for state that genuinely can.
 *
 * ## How an outcome is observed
 *
 * A store command reports nothing to its caller; it settles a flag and, on failure, records a
 * document. Every outcome on this screen is therefore observed by an effect that waits for
 * the flag to fall and matches the recorded failure's OPERATION against the command that was
 * issued. The operation match is load-bearing: the store re-reads the listing after a
 * successful write, so without it that re-read's failure would be reported as the write's.
 *
 * A message that must appear AFTER the listing has refreshed — the removal refusal, whose
 * order `SecurityRoles.ascx.vb:L579-L584` fixes — is deferred and raised once the read
 * settles, rather than being raised beside the write.
 */

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Input,
  TemplateRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import type { Subscription } from 'rxjs';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { RouterLink } from '@angular/router';

import { DEFAULT_PAGE_SIZE, toPagedResult } from '../../../core/models/paged-result.model';
import { isProblemDetails, type ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, RoleAssignmentRequest, UserRole } from '../../../core/models/role.model';
import type { UserListItem, UserListQuery } from '../../../core/models/user.model';
import {
  NotificationService,
  type NotificationSeverity,
} from '../../../core/services/notification.service';
import { UserService } from '../../../core/services/user.service';
import {
  RoleStore,
  type RoleStoreFailure,
  type RoleStoreOperation,
} from '../../../core/state/role.store';
import {
  conflictMessage,
  fieldErrorMessages,
  isValidationProblemDetails,
  summarizeProblem,
} from '../../../core/utils/form-errors.util';
import { parseRouteId } from '../../../core/utils/route-id.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
import { DateDisplayPipe } from '../../../shared/pipes/date-display.pipe';

/**
 * Every user-facing string on this screen, taken from the resource VALUE rather than from a
 * markup attribute.
 *
 * The resource value is authoritative because the markup attributes are demonstrably
 * unreliable: on the sibling role editor two validator messages are exactly swapped
 * relative to their own operators while the resource file has them the right way round.
 *
 * MIGRATION: localisation is NOT ported. The legacy screen resolved every one of these
 * through the Web Forms resource pipeline, and there is no translation runtime in this
 * workspace, so the wording is authored here directly and the resource files are read for
 * their wording only.
 *
 * MIGRATION: resource text is treated as UNTRUSTED MARKUP and rendered as plain text
 * everywhere. Across the in-scope resource files 76 values carry an HTML tag and one holds
 * a live remote script; the tags are stored escaped, so a literal scan for an opening
 * script tag finds nothing and would wrongly clear the risk. This screen's own
 * `ModuleHelp.Text` is `'<h1>About Manage Security Roles</h1><p>…</p>'` — module help has
 * no home in the shared component set, so it is read for context and never rendered.
 */
export const ROLE_ASSIGNMENT_TEXT = Object.freeze({
  /** `RoleTitle.Text` with its `{0}` placeholder filled by the role's name. */
  titleTemplate: 'Manage Users in Role: {0}',

  /** Shown while the role's name is still being fetched, so the heading is never blank. */
  titleFallback: 'Manage Users in Role',

  /** `ControlTitle_user roles.Text` — note that the legacy resource key contains a space. */
  caption: 'User Roles',

  /** `plUsers.Text`. */
  userLabel: 'User Name',

  /** `plUsers.Help`. */
  userHelp: 'Enter The User Name and click Validate to confirm',

  /** `cmdValidate.Text`, reused as the lookup field's placeholder. */
  validate: 'Validate',

  /** `plEffectiveDate.Text`. */
  effectiveDateLabel: 'Effective Date',

  /** `plEffectiveDate.Help` — the parenthesised '( Optional )' is the legacy spacing. */
  effectiveDateHelp:
    'Specify The Date This Role Assignment Should Start ( Optional ). Entering No Value ' +
    'Will Indicate that the role will start immediately.',

  /** `plExpiryDate.Text`. */
  expiryDateLabel: 'Expiry Date',

  /** `plExpiryDate.Help`. */
  expiryDateHelp:
    'Specify The Date This Role Assignment Should Expire ( Optional ). Entering No Value ' +
    'Will Indicate No Expiry Date.',

  /** `SendNotification.Text`. */
  notifyLabel: 'Send Notification?',

  /**
   * The sentence stating why the notification choice cannot be made.
   *
   * MIGRATION: `SecurityRoles.ascx.vb:L542` and `:L569` passed this choice to a routine that mailed the
   * account holder, and the assignment contract still carries the member — so the member is transmitted
   * rather than dropped. What is NOT carried is the mail subsystem, which this migration excludes
   * wholesale, so no notification is sent for any value of the flag.
   *
   * ⚠ SAID HERE, BEFORE THE DECISION, RATHER THAN NOT AT ALL. The control used to arrive TICKED and its
   * true value was transmitted, so the operator asked for a notification, received a success, and had
   * every reason to believe one had gone out. Stating the gap beside the control is the honest
   * alternative, and it is the same treatment the account editor's notify control receives.
   */
  notifyHelp:
    'Unavailable: this installation exposes no mail endpoint, so no notification e-mail can be sent.',

  /** `AddUser.Text` — the action's label while the chosen account holds no membership. */
  addUser: 'Add User to Role',

  /** `UpdateRole.Text` — the label once the chosen account already holds one. */
  updateUserRole: 'Update User Role',

  /** `UserName.Header`. */
  userNameHeader: 'User Name',

  /** `EffectiveDate.Header`. */
  effectiveDateHeader: 'Effective Date',

  /** `ExpiryDate.Header`. */
  expiryDateHeader: 'Expiry Date',

  /** Global `cmdDelete.Text` — the row command's accessible name. */
  delete: 'Delete',

  /** Global `DeleteItem.Text`, raised by `SecurityRoles.ascx.vb:L608`. */
  confirmRemoval: 'Are You Sure You Wish To Delete This Item?',

  /** Global `Cancel.Action`, the screen's single module action at `:L634`. */
  cancel: 'Cancel',

  /** `valEffectiveDate.Text`. Its leading break is followed by a SPACE, unlike its sibling. */
  invalidEffectiveDate: '<br> Invalid effective date',

  /** `valExpiryDate.Text`. */
  invalidExpiryDate: '<br>Invalid expiry date',

  /** `valDates.Text`. */
  expiryNotAfterEffective: '<br>Expiry Date must be Greater than Effective Date',

  /** `RoleRemoveError.Text`, used when the server does not itself supply the wording. */
  removalRefused: 'You Can Not Remove The Portal Administrator Or The Registered Users Role',

  /**
   * MIGRATION: the legacy lookup failed SILENTLY. `SecurityRoles.ascx.vb:L476-L488` looked
   * the account up by name and, on no match, simply blanked the box at `:L484` with no
   * message at all. That is replaced by a visible state; a documented improvement.
   */
  noMatchingUsers: 'No accounts match that name.',

  /** Shown when the route did not carry a usable role identifier. */
  roleUnresolved: 'No security role was addressed, so no memberships can be shown.',
} as const);

/**
 * Document identifiers for the three labelled controls, so the shared field wrapper's `for`
 * input and the control's own `id` attribute cannot drift apart.
 */
export const ROLE_ASSIGNMENT_CONTROL_ID = Object.freeze({
  /** The account lookup field. */
  user: 'role-assignment-user',

  /** The effective-bound field. */
  effectiveDate: 'role-assignment-effective-date',

  /** The expiry-bound field. */
  expiryDate: 'role-assignment-expiry-date',

  /** The notification choice. */
  notify: 'role-assignment-notify',
} as const);

/**
 * Column keys for the membership grid.
 *
 * A key is a UNIQUE IDENTITY and is deliberately not the display label: the shared grid
 * rejects a duplicate key outright because the key tracks both the heading and every cell
 * in its column.
 */
export const ROLE_ASSIGNMENT_COLUMN_KEY = Object.freeze({
  /** The projected row command. */
  commands: 'commands',

  /** The account's display name, linked to the account. */
  userName: 'userName',

  /** The membership's effective bound. */
  effectiveDate: 'effectiveDate',

  /** The membership's expiry bound. */
  expiryDate: 'expiryDate',
} as const);

/**
 * The `<ng-template>` reference names this component queries out of its own view.
 *
 * Published so the template and the class agree on one spelling. A reference the template
 * does not declare is not an error: the column falls back to plain text, except for the
 * commands column, which is omitted because a command has no plain-text form.
 */
export const ROLE_ASSIGNMENT_CELL_TEMPLATE = Object.freeze({
  /** Wraps the row command in the per-row removability predicate. */
  commands: 'roleAssignmentCommandsCell',

  /** Renders the account's display name as a link to the account. */
  user: 'roleAssignmentUserCell',

  /** Renders the effective bound through the shared date pipe. */
  effectiveDate: 'roleAssignmentEffectiveDateCell',

  /** Renders the expiry bound through the shared date pipe. */
  expiryDate: 'roleAssignmentExpiryDateCell',
} as const);

/**
 * The screen's typed form.
 *
 * Declared as an explicit model so the group is `FormGroup<RoleAssignmentFormModel>` and its
 * value is fully typed rather than a partial. Every control is constructed non-nullable, so
 * a reset returns each one to its initial value instead of to `null`.
 *
 * MIGRATION: there is NO required validator on {@link RoleAssignmentFormModel.userId}, and
 * that is settled by the source rather than chosen. `SecurityRoles.ascx.vb:L520` gated the
 * write on `Page.IsValid`, which covered only the three DATE validators, and the account
 * was checked separately at `:L521` by `(Not Role Is Nothing) AndAlso (Not User Is Nothing)`
 * — a guard clause, not a validator. The equivalent here is
 * {@link RoleAssignmentComponent.canSubmit}, which gates the action; adding a validator with
 * no legacy counterpart would change which messages the screen shows.
 *
 * MIGRATION: neither date carries a required validator either. An ASP.NET comparison
 * validator passes on an empty field, and both help strings say so in as many words —
 * '( Optional ). Entering No Value Will Indicate…'. Adding one would break parity.
 */
export interface RoleAssignmentFormModel {
  /**
   * The chosen account, or `null` when none has been chosen yet.
   *
   * `null` here means "nothing chosen", which is a fact about the form and never a stand-in
   * for an identifier. Account identifiers are seeded from one, but role identifiers are
   * seeded from zero and tenant identifiers from minus one, so no identifier anywhere on
   * this screen is tested for truthiness or for sign — absence is tested with `=== null`.
   */
  userId: FormControl<number | null>;

  /**
   * The effective bound as the `yyyy-MM-dd` value of a native date control, or `''`.
   *
   * MIGRATION: `''` is the empty state and it is converted to `null` on the way out. The
   * legacy screen substituted the integer/date null sentinel for an empty box
   * (`SecurityRoles.ascx.vb:L528-L539`), but the assignment contract documents that the
   * sentinel cannot reach the wire at all — the column is a SQL Server `datetime`, whose
   * range begins in 1753 and which refuses 0001-01-01 — so an unset bound is a genuine
   * `null` and `null` is what is sent. The contract is followed exactly; no third
   * representation is invented.
   */
  effectiveDate: FormControl<string>;

  /** The expiry bound, on the same terms as {@link RoleAssignmentFormModel.effectiveDate}. */
  expiryDate: FormControl<string>;

  /**
   * Whether the account should be notified, defaulting to checked.
   *
   * MIGRATION: retained because the assignment contract carries `notifyUser`.
   * `securityroles.ascx:L49` declares `chkNotify` with `Checked="True"` and places it AFTER
   * `</asp:panel>`, outside the add form, because it governed removal as well
   * (`SecurityRoles.ascx.vb:L542` for the add and `:L569` for the removal). The removal
   * address is a `DELETE` and carries no body, and the interface exposes no notification
   * argument on it, so the choice reaches the ADD only. That the mail subsystem itself is
   * out of scope is recorded on the contract, not decided here.
   */
  notify: FormControl<boolean>;
}

/**
 * The form's state, mirrored into a signal so the derived views below can be `computed`.
 *
 * Reactive-form state is not itself reactive in the signal sense, and touched-state changes
 * do not surface on the value or status streams at all. The control event stream does carry
 * them, so it is the single subscription that keeps this snapshot current.
 */
interface RoleAssignmentFormState {
  /** The chosen account, or `null`. */
  readonly userId: number | null;

  /** The effective bound as typed. */
  readonly effectiveDate: string;

  /** The expiry bound as typed. */
  readonly expiryDate: string;

  /** Whether the group as a whole is valid. */
  readonly valid: boolean;

  /** Whether the effective bound failed its own data-type check. */
  readonly effectiveDateInvalid: boolean;

  /** Whether the effective bound has been visited. */
  readonly effectiveDateTouched: boolean;

  /** Whether the expiry bound failed its own data-type check. */
  readonly expiryDateInvalid: boolean;

  /** Whether the expiry bound has been visited. */
  readonly expiryDateTouched: boolean;

  /** Whether the group-level ordering rule is failing. */
  readonly datesOutOfOrder: boolean;
}

/** A message to raise once a re-read has settled, never before it. */
interface DeferredNotice {
  /** How the message should be presented. */
  readonly severity: NotificationSeverity;

  /** The message itself, already plain text. */
  readonly message: string;
}

/** Validation key raised when a date box holds something that is not a calendar date. */
const INVALID_DATE_ERROR = 'invalidDate';

/** Validation key raised when the expiry bound is not strictly later than the effective one. */
const DATE_ORDER_ERROR = 'expiryNotAfterEffective';

/** Control name of the effective bound, used for group-level and server-side lookups. */
const EFFECTIVE_DATE_CONTROL = 'effectiveDate';

/** Control name of the expiry bound. */
const EXPIRY_DATE_CONTROL = 'expiryDate';

/** The `yyyy-MM-dd` value a native date control produces. */
const CALENDAR_DATE_VALUE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** An ISO 8601 instant as the API publishes it, whose date part is what this screen reads. */
const WIRE_INSTANT =
  /^(\d{4})-(\d{2})-(\d{2})(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$/;

/** The empty value of a date box, and of a wire bound that is absent. */
const NO_DATE = '';

/** Calendar year of the legacy date sentinel, which is data to be ignored and never a date. */
const SENTINEL_YEAR = 1;

/** How many accounts one lookup returns before the operator is asked to narrow the name. */
const USER_LOOKUP_PAGE_SIZE = DEFAULT_PAGE_SIZE;

/**
 * The failure code a refused removal carries.
 *
 * The API answers a protected membership and a caller who does not administer the tenant with
 * the SAME status — 403 — and distinguishes them on the problem type alone, so this code is
 * what separates the legacy refusal wording from a permission failure. Its published wording is
 * the legacy `RoleRemoveError` text verbatim, which is why the message is read from the shared
 * table rather than restated in the branch that raises it.
 */
const PROTECTED_ASSIGNMENT_CODE = 'role_assignment.protected';

/**
 * The membership write this screen is waiting on, named by the store operation that reports it.
 *
 * Held rather than inferred from the store's saving flag, because that flag is raised by EVERY
 * role write in the application — a sibling screen's role rename would otherwise look to this
 * screen like its own membership write settling.
 */
type AwaitedAssignmentWrite = Extract<RoleStoreOperation, 'assignUser' | 'removeAssignment'>;


/**
 * Whether a year, month and day name a day that exists.
 *
 * Round-tripped through a UTC probe so that, for example, the thirty-first of February is
 * rejected rather than silently rolled forward into March.
 *
 * @param year Four-digit calendar year.
 * @param month One-based calendar month.
 * @param day One-based day of the month.
 * @returns `true` when the three parts name a real day.
 */
function isRealCalendarDay(year: number, month: number, day: number): boolean {
  if (month < 1 || month > 12 || day < 1 || day > 31) {
    return false;
  }
  const probe = new Date(0);
  probe.setUTCFullYear(year, month - 1, day);
  probe.setUTCHours(0, 0, 0, 0);
  return (
    probe.getUTCFullYear() === year &&
    probe.getUTCMonth() === month - 1 &&
    probe.getUTCDate() === day
  );
}

/**
 * Reads a `yyyy-MM-dd` date-box value as an instant at UTC midnight.
 *
 * MIGRATION: this replaces an UNGUARDED parse. `SecurityRoles.ascx.vb:L528-L539` called
 * `Date.Parse` on whatever the box held, with no `TryParse` anywhere and with the
 * administration pages compiled with strict type checking off, so a value the parser could
 * not read threw out of the click handler. Here an unreadable value is `null`, the caller
 * decides what that means, and nothing throws.
 *
 * @param text The raw value of a date box.
 * @returns Milliseconds since the epoch at UTC midnight, or `null` when the value is empty
 * or is not a calendar date.
 */
export function parseCalendarDateValue(text: string): number | null {
  const fields = CALENDAR_DATE_VALUE.exec(text.trim());
  if (fields === null) {
    return null;
  }
  const year = Number(fields[1]);
  const month = Number(fields[2]);
  const day = Number(fields[3]);
  if (isRealCalendarDay(year, month, day) === false) {
    return null;
  }
  const instant = new Date(0);
  instant.setUTCFullYear(year, month - 1, day);
  instant.setUTCHours(0, 0, 0, 0);
  return instant.getTime();
}

/**
 * Converts a bound as the API publishes it into the value a native date control accepts.
 *
 * The date part is taken as written and read as a UTC calendar date, which is the same
 * reading the shared date pipe applies when it renders the bound in the grid, so the box and
 * the grid can never disagree about which day a membership starts.
 *
 * This is not a second copy of that pipe: the pipe produces a LOCALISED DISPLAY string and
 * this produces the control's `yyyy-MM-dd` value, which is a different job with a fixed
 * format. What the two do share is the emptiness rule, and that is deliberate.
 *
 * MIGRATION: the legacy sentinel is honoured on the way IN even though the contract says it
 * cannot be stored. `Library/Components/Shared/Null.vb` defines the date sentinel as
 * `Date.MinValue`, and `SecurityRoles.ascx.vb:L281-L286` skipped a bound the sentinel test
 * reported as unset rather than showing it. A minimum-value instant therefore reads as "no
 * bound" here too and never as the first day of the first year.
 *
 * @param wire The published bound, which may be `null` when the membership has none.
 * @returns A `yyyy-MM-dd` value, or `''` when there is no bound to show.
 */
export function toCalendarDateValue(wire: string | null | undefined): string {
  if (typeof wire !== 'string') {
    return NO_DATE;
  }
  const fields = WIRE_INSTANT.exec(wire.trim());
  if (fields === null) {
    return NO_DATE;
  }
  const year = Number(fields[1]);
  const month = Number(fields[2]);
  const day = Number(fields[3]);
  if (year === SENTINEL_YEAR && month === 1 && day === 1) {
    return NO_DATE;
  }
  if (isRealCalendarDay(year, month, day) === false) {
    return NO_DATE;
  }
  return `${fields[1]}-${fields[2]}-${fields[3]}`;
}

/**
 * Reads a child control's value as text without letting an untyped value through.
 *
 * @param group The form group holding the control.
 * @param name The control's name within the group.
 * @returns The control's value when it is a string, otherwise `''`.
 */
function readTextControl(group: AbstractControl, name: string): string {
  const control = group.get(name);
  if (control === null) {
    return NO_DATE;
  }
  const held: unknown = control.value;
  return typeof held === 'string' ? held : NO_DATE;
}

/**
 * The data-type check both date boxes carry.
 *
 * Reproduces the two comparison validators declared at `securityroles.ascx:L45` and `:L46`,
 * each of which is `type="Date"` with `operator="DataTypeCheck"`. Both PASS on an empty
 * field, which is what makes the bounds optional.
 *
 * @param control The date control being checked.
 * @returns `null` when the value is empty or is a calendar date, otherwise the failure.
 */
export function calendarDateValidator(control: AbstractControl): ValidationErrors | null {
  const held: unknown = control.value;
  const text = typeof held === 'string' ? held.trim() : NO_DATE;
  if (text.length === 0) {
    return null;
  }
  return parseCalendarDateValue(text) === null ? { [INVALID_DATE_ERROR]: true } : null;
}

/**
 * The cross-field ordering rule, attached to the group and surfaced on the expiry field.
 *
 * Reproduces the third validator at `securityroles.ascx:L47`: `type="Date"`,
 * `operator="GreaterThan"`, validating `txtExpiryDate` against `txtEffectiveDate`.
 *
 * Two properties of that declaration are load-bearing and are reproduced exactly. It is
 * `GreaterThan` rather than `GreaterThanEqual`, so EQUAL BOUNDS ARE INVALID and the expiry
 * must be strictly later. And an ASP.NET comparison validator passes on an empty field, so
 * this rule SKIPS whenever either bound is empty; an unreadable bound is skipped too,
 * because the field's own data-type check already reports that and reporting it twice would
 * put two messages on screen for one mistake.
 *
 * The correctly-typed declaration on this screen is worth stating plainly, because the
 * sibling role editor carries four comparison validators that omit `type` altogether: all
 * three validators HERE are typed, so there is no such defect on this screen to annotate.
 *
 * @param group The form group holding both date controls.
 * @returns `null` when the rule passes or does not apply, otherwise the failure.
 */
export function dateOrderValidator(group: AbstractControl): ValidationErrors | null {
  const effective = parseCalendarDateValue(readTextControl(group, EFFECTIVE_DATE_CONTROL));
  const expiry = parseCalendarDateValue(readTextControl(group, EXPIRY_DATE_CONTROL));
  if (effective === null || expiry === null) {
    return null;
  }
  return expiry > effective ? null : { [DATE_ORDER_ERROR]: true };
}

/**
 * Reads the route's raw parameter as a role identifier.
 *
 * Route parameters arrive as strings, so the conversion is explicit. It is also strict: a
 * value with anything other than an optional sign and digits is rejected outright rather
 * than salvaged, because a partial parse would address a different role than the one asked
 * for. There is no non-null assertion and no cast anywhere in it.
 *
 * MIGRATION: minus one is NOT treated as absent here, and zero is not treated as missing.
 * The role table is seeded `IDENTITY(0, 1)`, so zero is the first role a tenant is given and
 * the screen must load normally for it, while the tenant table is seeded `IDENTITY(-1, 1)`,
 * which is simultaneously the legacy integer null sentinel. `SecurityRoles.ascx.vb:L51-L53`
 * initialised its three identifiers to minus one and `:L413-L419` overwrote them from the
 * query string, so minus one was that screen's "not supplied" marker. On this screen a
 * missing parameter is `null` and every supplied number is a real identifier.
 *
 * @param value The route parameter, which arrives as a string.
 * @returns The identifier, or `null` when the route carried nothing usable.
 */
export function parseRouteIdentifier(value: number | string | null | undefined): number | null {
  // The parse is delegated to the one parser in the workspace that performs it: it applies
  // the same shape test this function applied, plus the safe-integer ceiling and the API's
  // 32-bit range, which this function did not. `null` for absence is already this screen's
  // representation, so nothing is adapted — and on this screen a missing parameter is
  // `null` while every supplied number, minus one and zero included, is a real identifier.
  return parseRouteId(value);
}

/**
 * Extracts a problem document from a failed request without importing the HTTP layer.
 *
 * The shape is read structurally rather than by class, which is the pattern the client layer
 * already uses: a component has no business importing anything from the HTTP client, and the
 * structural read is exactly as safe because the guard validates every member it relies on.
 *
 * @param error Whatever the request stream failed with.
 * @returns The problem document, or `null` when the failure carried none.
 */
function readProblemDetails(error: unknown): ProblemDetails | null {
  if (typeof error !== 'object' || error === null || 'error' in error === false) {
    return null;
  }
  const body: unknown = error.error;
  if (isProblemDetails(body)) {
    return body;
  }
  if (typeof body !== 'string' || body.trim().length === 0) {
    return null;
  }
  try {
    const parsed: unknown = JSON.parse(body);
    return isProblemDetails(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/** Composes the stable key of one account-and-role pairing. */
function pairingKey(roleId: number, userId: number): string {
  return `${roleId}:${userId}`;
}


/**
 * Routed container for `/roles/:roleId/users`.
 *
 * Declared standalone with the push change-detection strategy and a single stylesheet, and
 * with NO providers of its own: every interface it consumes is registered at the application
 * root, so declaring them here would give this screen its own copies of shared state.
 */
@Component({
  selector: 'app-role-assignment',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    ErrorBannerComponent,
    FormFieldComponent,
    SearchInputComponent,
    DataTableComponent,
    ConfirmDialogComponent,
    DateDisplayPipe,
  ],
  templateUrl: './role-assignment.component.html',
  styleUrl: './role-assignment.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleAssignmentComponent {
  // MIGRATION: index of every deliberate divergence from the legacy control. Each entry names the
  // member whose documentation carries the full note, so this list cannot drift out of date the way
  // line references would. Legacy citations without a filename are `SecurityRoles.ascx.vb`.
  // MIGRATION:  1. XSS anchor -> escaped interpolation + routerLink; :L394-L396; see columns.
  // MIGRATION:  2. user-centric half of the bidirectional screen dropped; :L183-L198; see file header.
  // MIGRATION:  3. all-accounts dropdown -> paged starts-with lookup; :L455-L468; see onUserSearch.
  // MIGRATION:  4. calendar pop-ups -> native date inputs; :L329-L335; see controlId.
  // MIGRATION:  5. integer-vs-string admin compare + non-short-circuit And made explicit; :L522-L526.
  // MIGRATION:  6. unguarded Date.Parse -> safe parsing; :L528-L539; see parseCalendarDateValue.
  // MIGRATION:  7. chkNotify retained because the contract carries notifyUser; :L542, :L569.
  // MIGRATION:  8. list re-read after add and removal, error after the rebind; :L546, :L579-L584.
  // MIGRATION:  9. br-prefixed messages stripped by shared/form-field, rendered as plain text.
  // MIGRATION: 10. resource text treated as untrusted markup; ModuleHelp.Text never rendered.
  // MIGRATION: 11. localisation not ported; wording authored directly from the resource values.
  // MIGRATION: 12. permission denial surfaced at warning severity; AccessDenied.ascx.vb:L43, :L45.
  // MIGRATION: 13. 'Security Role' column omitted because role-centric mode hid it; :L245.
  // MIGRATION: 14. no required validator on the account field; :L520-L521 was a guard clause.
  // MIGRATION: 15. silent blank on a failed lookup -> visible no-matches state; :L476-L488.

  private readonly store = inject(RoleStore);
  private readonly userService = inject(UserService);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly roleIdSignal: WritableSignal<number | null> = signal<number | null>(null);

  /**
   * The role whose read this screen is waiting on, or `null` when none is outstanding.
   *
   * Absence is `null` and nothing else: `Roles.RoleID` is seeded `IDENTITY(0, 1)`, so a role
   * key of zero is the portal's first role and `0` cannot mean "not waiting".
   */
  private readonly awaitedRoleKey: WritableSignal<number | null> = signal<number | null>(null);

  /**
   * The account lookup in flight, or `null` when none is.
   *
   * ⚠ A HANDLE, NOT MERELY A TEARDOWN. `takeUntilDestroyed` ends a lookup when the screen goes away,
   * which is a different question from ending the one a newer term replaces. Two lookups outstanding
   * at once answer in whichever order the network chooses, so without this the answer to `a` can land
   * after the answer to `ann` and leave the wider match set on screen under the narrower term.
   */
  private userLookupRequest: Subscription | null = null;

  /** The membership write this screen is waiting on, or `null` when none is outstanding. */
  private readonly awaitedWrite: WritableSignal<AwaitedAssignmentWrite | null> =
    signal<AwaitedAssignmentWrite | null>(null);

  /**
   * The membership an outstanding removal addressed, so a refusal can be attributed to it.
   *
   * Needed because the store command reports the failure without echoing what was addressed,
   * and a refused pairing has to be remembered against the row that was refused.
   */
  private readonly outstandingRemoval: WritableSignal<UserRole | null> = signal<UserRole | null>(
    null,
  );

  /**
   * A message to raise once the membership listing has finished refreshing, or `null`.
   *
   * The order is load-bearing rather than cosmetic: `SecurityRoles.ascx.vb:L579-L584` rebound
   * the grid unconditionally and raised the refusal only afterwards, so the operator read the
   * message against a refreshed grid.
   */
  private readonly deferredNotice: WritableSignal<DeferredNotice | null> =
    signal<DeferredNotice | null>(null);

  /**
   * Whether a membership read is in flight that this screen has not yet reported on.
   *
   * A latch rather than a mirror. The listing flag falls once per read, and the deferred
   * message must be raised on that fall exactly once — including for the re-read the store
   * performs after a write, which this screen did not dispatch itself.
   */
  private readonly assignmentsSettling: WritableSignal<boolean> = signal(false);

  private readonly problemSignal: WritableSignal<ProblemDetails | null> =
    signal<ProblemDetails | null>(null);
  private readonly userMatchesSignal: WritableSignal<readonly UserListItem[]> = signal<
    readonly UserListItem[]
  >([]);
  private readonly userLookupLoadingSignal: WritableSignal<boolean> = signal(false);
  private readonly userLookupTermSignal: WritableSignal<string> = signal('');
  private readonly selectedUserSignal: WritableSignal<UserListItem | null> =
    signal<UserListItem | null>(null);
  private readonly pendingRemovalSignal: WritableSignal<UserRole | null> = signal<UserRole | null>(
    null,
  );
  private readonly protectedPairingsSignal: WritableSignal<ReadonlySet<string>> = signal<
    ReadonlySet<string>
  >(new Set<string>());
  private readonly administratorUserIdSignal: WritableSignal<number | null> = signal<number | null>(
    null,
  );
  private readonly administratorRoleIdSignal: WritableSignal<number | null> = signal<number | null>(
    null,
  );
  private readonly registeredRoleIdSignal: WritableSignal<number | null> = signal<number | null>(
    null,
  );

  /**
   * The screen's typed form.
   *
   * Public so the template can bind the group and its controls, and so a specification can
   * drive it without reaching through the view.
   */
  public readonly form = new FormGroup<RoleAssignmentFormModel>(
    {
      userId: new FormControl<number | null>(null, { nonNullable: true }),
      // MIGRATION: the two CALENDAR POP-UPS ARE DROPPED and each bound is a native date
      // control inside the shared field wrapper instead. `SecurityRoles.ascx.vb:L329-L335`
      // pointed two hyperlinks at a pop-up calendar helper and gave each one a raster image
      // with localised alternative text; the helper is a Web Forms client-script facility with
      // no counterpart here, the shared component set is closed and holds no date picker, and
      // no eleventh member may be added to it. A plain control inside the wrapper is the
      // sanctioned shape for exactly this situation, and the native control brings its own
      // picker, keyboard handling and locale. The two raster assets are dropped with it. A
      // documented substitution: the affordance changes, the value the operator can express
      // does not.
      effectiveDate: new FormControl<string>(NO_DATE, {
        nonNullable: true,
        validators: [calendarDateValidator],
      }),
      expiryDate: new FormControl<string>(NO_DATE, {
        nonNullable: true,
        validators: [calendarDateValidator],
      }),
      // ⚠ UNTICKED AND DISABLED, DEPARTING FROM THE MEASURED INITIAL STATE DELIBERATELY. The contract's
      // member is still transmitted — a disabled control reports its value like any other — but it now
      // carries FALSE, which is the truthful request: nothing is asking for a notification, because
      // nothing can send one. See the help wording above.
      notify: new FormControl<boolean>({ value: false, disabled: true }, { nonNullable: true }),
    },
    { validators: [dateOrderValidator] },
  );

  private readonly formStateSignal: WritableSignal<RoleAssignmentFormState> =
    signal<RoleAssignmentFormState>(this.readFormState());

  /** The role addressed by the route, or `null` when the route carried nothing usable. */
  public readonly resolvedRoleId: Signal<number | null> = this.roleIdSignal.asReadonly();

  /**
   * The addressed role once the store holds it, used for the heading.
   *
   * Derived from the store's selected role and GATED ON IDENTITY, so a role another screen
   * selected can never appear in this heading. The gate also makes the reset on a change of
   * address automatic: the moment the addressed key changes, the previously held role stops
   * matching and this reads `null` without anything being cleared.
   */
  public readonly role: Signal<Role | null> = computed(() => {
    const addressed = this.roleIdSignal();
    const selected = this.store.selectedRole();

    if (addressed === null || selected === null) {
      return null;
    }

    return selected.roleId === addressed ? selected : null;
  });

  /** Whether the addressed role is still being fetched. */
  public readonly roleLoading: Signal<boolean> = computed(() => this.awaitedRoleKey() !== null);

  /**
   * Every membership of the addressed role, unpaged.
   *
   * Gated on identity for the same reason as {@link role}: the store's assignment slice is
   * keyed by the role it was read for, and a slice read for another role is not this screen's
   * grid.
   */
  public readonly assignments: Signal<readonly UserRole[]> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return [];
    }

    return this.store.assignmentItems();
  });

  /**
   * Whether the membership listing is in flight.
   *
   * Read from the store rather than latched here, so the re-read the store performs after a
   * write also turns the spinner — without it the rows would change under the operator with no
   * indication that anything was happening.
   */
  public readonly assignmentsLoading: Signal<boolean> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return false;
    }

    return this.store.assignmentsLoading();
  });

  /**
   * Whether a membership write of this screen's is in flight, so the action can be held.
   *
   * Deliberately NOT the store's saving flag, which every role write in the application
   * raises. See {@link AwaitedAssignmentWrite}.
   */
  public readonly saving: Signal<boolean> = computed(() => this.awaitedWrite() !== null);

  /** The last failure as a problem document, for the shared banner's `problem` input. */
  public readonly problem: Signal<ProblemDetails | null> = this.problemSignal.asReadonly();

  /** The accounts the current lookup matched. */
  public readonly userMatches: Signal<readonly UserListItem[]> =
    this.userMatchesSignal.asReadonly();

  /** Whether an account lookup is in flight. */
  public readonly userLookupLoading: Signal<boolean> = this.userLookupLoadingSignal.asReadonly();

  /** The account chosen for the next write, or `null` when none has been chosen. */
  public readonly selectedUser: Signal<UserListItem | null> = this.selectedUserSignal.asReadonly();

  /** The membership awaiting confirmation, or `null` when no dialogue is open. */
  public readonly pendingRemoval: Signal<UserRole | null> = this.pendingRemovalSignal.asReadonly();

  /** The form's mirrored state, for a specification that wants to assert on it directly. */
  public readonly formState: Signal<RoleAssignmentFormState> = this.formStateSignal.asReadonly();

  /** The wording table, so the template needs no literals of its own. */
  public readonly text = ROLE_ASSIGNMENT_TEXT;

  /** The control identifiers, so `for` and `id` cannot drift apart. */
  public readonly controlId = ROLE_ASSIGNMENT_CONTROL_ID;

  private readonly commandsCellTemplate = viewChild<TemplateRef<DataTableCellContext<UserRole>>>(
    ROLE_ASSIGNMENT_CELL_TEMPLATE.commands,
  );

  private readonly userCellTemplate = viewChild<TemplateRef<DataTableCellContext<UserRole>>>(
    ROLE_ASSIGNMENT_CELL_TEMPLATE.user,
  );

  private readonly effectiveDateCellTemplate = viewChild<
    TemplateRef<DataTableCellContext<UserRole>>
  >(ROLE_ASSIGNMENT_CELL_TEMPLATE.effectiveDate);

  private readonly expiryDateCellTemplate = viewChild<TemplateRef<DataTableCellContext<UserRole>>>(
    ROLE_ASSIGNMENT_CELL_TEMPLATE.expiryDate,
  );

  /**
   * The heading, with the role's name substituted into the legacy title template.
   *
   * MIGRATION: the placeholder is filled by INTERPOLATION and never by a markup-injecting
   * binding. `SecurityRoles.ascx.vb:L193` formatted the same template with the role's name
   * and identifier, and the template itself uses only the first of the two, so only the name
   * is substituted here.
   */
  public readonly title: Signal<string> = computed(() => {
    const current = this.role();
    if (current === null) {
      return ROLE_ASSIGNMENT_TEXT.titleFallback;
    }
    return ROLE_ASSIGNMENT_TEXT.titleTemplate.replace('{0}', current.roleName);
  });

  /**
   * Whether the write may proceed.
   *
   * This is the guard clause from `SecurityRoles.ascx.vb:L521` expressed as a derived view
   * rather than as a validator, which is why the account field carries no required rule. The
   * account is tested with `=== null` because zero would be a legitimate identifier on other
   * tables and a truthiness test would lose it.
   */
  public readonly canSubmit: Signal<boolean> = computed(() => {
    const state = this.formStateSignal();
    return (
      state.userId !== null &&
      state.valid &&
      this.saving() === false &&
      this.roleIdSignal() !== null
    );
  });

  /**
   * The action's label, which becomes 'Update User Role' once the chosen account already
   * holds the role.
   *
   * MIGRATION: only ONE of the two legacy relabel branches is live in this mode.
   * `SecurityRoles.ascx.vb:L650` tested the role identifier against the null sentinel, which
   * is false here, so `:L651-L653` was dead code on this screen; `:L656` tested the ACCOUNT
   * identifier, which is true here, and `:L657-L658` is the branch that ran — it relabelled
   * the action when a grid row's account matched the chosen one. That is reproduced exactly.
   */
  public readonly actionLabel: Signal<string> = computed(() => {
    const chosen = this.formStateSignal().userId;
    if (chosen === null) {
      return ROLE_ASSIGNMENT_TEXT.addUser;
    }
    const holdsRole = this.assignments().some((row) => row.userId === chosen);
    return holdsRole ? ROLE_ASSIGNMENT_TEXT.updateUserRole : ROLE_ASSIGNMENT_TEXT.addUser;
  });

  /**
   * Whether the lookup ran and matched nothing, so the template can say so.
   *
   * MIGRATION: this is the replacement for the silent failure at
   * `SecurityRoles.ascx.vb:L484`, which blanked the box and said nothing at all.
   */
  public readonly showNoMatchingUsers: Signal<boolean> = computed(
    () =>
      this.userLookupTermSignal().length > 0 &&
      this.userLookupLoadingSignal() === false &&
      this.userMatchesSignal().length === 0,
  );

  /** Whether the route addressed a role at all, so the template can explain its absence. */
  public readonly roleUnresolved: Signal<boolean> = computed(() => this.roleIdSignal() === null);


  /**
   * Messages for the effective-bound field, gated the way `Display="Dynamic"` gated them.
   *
   * A dynamic validator showed nothing until it had something to say, so a message appears
   * here only once the field has been VISITED and is failing. That gating is the caller's
   * obligation: the shared field wrapper renders whatever it is given and does not decide
   * when to show it.
   *
   * MIGRATION: the wording is passed through EXACTLY as the resource file holds it, leading
   * break tag and all, because the shared field wrapper already strips a leading
   * `<br>`/`<br/>`/`<br />` case-insensitively together with the whitespace that follows it
   * and then trims. That matters for this particular message: `valEffectiveDate.Text` is
   * `'<br> Invalid effective date'` — a break followed by a SPACE — so a stripper that
   * removed only the tag would leave the message indented by one space, and its sibling
   * `valExpiryDate.Text` has no such space. The wrapper strips; this component does not, and
   * either way the message is rendered as plain text and never as markup.
   */
  public readonly effectiveDateMessages: Signal<readonly string[]> = computed(() => {
    const state = this.formStateSignal();
    const messages: string[] = [];
    if (state.effectiveDateTouched && state.effectiveDateInvalid) {
      messages.push(ROLE_ASSIGNMENT_TEXT.invalidEffectiveDate);
    }
    messages.push(...this.serverMessagesFor(EFFECTIVE_DATE_CONTROL));
    return messages;
  });

  /**
   * Messages for the expiry-bound field.
   *
   * The group-level ordering rule surfaces HERE rather than on the effective bound, because
   * the legacy validator declared `controltovalidate="txtExpiryDate"`. It waits for BOTH
   * fields to have been visited, since a rule about the pair has nothing to say until the
   * operator has engaged with both halves of it.
   */
  public readonly expiryDateMessages: Signal<readonly string[]> = computed(() => {
    const state = this.formStateSignal();
    const messages: string[] = [];
    if (state.expiryDateTouched && state.expiryDateInvalid) {
      messages.push(ROLE_ASSIGNMENT_TEXT.invalidExpiryDate);
    }
    if (state.effectiveDateTouched && state.expiryDateTouched && state.datesOutOfOrder) {
      messages.push(ROLE_ASSIGNMENT_TEXT.expiryNotAfterEffective);
    }
    messages.push(...this.serverMessagesFor(EXPIRY_DATE_CONTROL));
    return messages;
  });

  /** Server-reported messages about the account, when a rejected write named that field. */
  public readonly userMessages: Signal<readonly string[]> = computed(() =>
    this.serverMessagesFor('userId'),
  );

  /**
   * The membership grid's columns: FOUR of them, and deliberately not five.
   *
   * MIGRATION: the 'Security Role' column is OMITTED. `securityroles.ascx:L76` declares it as
   * the third column, but `SecurityRoles.ascx.vb:L245` hides it — `Columns(2).Visible = False`
   * — precisely in the role-centric mode this component implements, because every row on this
   * screen belongs to the one role already named in the heading. Emitting it would be a
   * column the legacy screen never showed. The mirrored branch at `:L252` hides the account
   * column instead, which is the out-of-scope account-centric mode.
   *
   * MIGRATION: NO column is sortable and no sort state is bound. `grdUserRoles` declares no
   * `AllowSorting` at `securityroles.ascx:L56`, so the legacy grid could not be reordered;
   * the shared grid emits a sort request but never performs one, and leaving every column
   * unsortable is what keeps the two in step. Nothing here binds `sortBy` or `sortDir` and
   * nothing handles `sortChange`.
   *
   * MIGRATION: the commands column carries an ACCESSIBLE NAME even though the legacy column
   * had no heading text at all (`securityroles.ascx:L60`). The heading is hidden rather than
   * absent, so the name reaches assistive technology while the grid looks as it did. The
   * shared grid refuses a column that is both sortable and heading-hidden, which is another
   * reason no column here is sortable.
   */
  public readonly columns: Signal<readonly DataTableColumn<UserRole>[]> = computed(() => {
    const columns: DataTableColumn<UserRole>[] = [];

    const commands = this.commandsCellTemplate();
    if (commands !== undefined) {
      columns.push({
        key: ROLE_ASSIGNMENT_COLUMN_KEY.commands,
        label: ROLE_ASSIGNMENT_TEXT.delete,
        headerHidden: true,
        bodyAlign: 'start',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: commands,
      });
    }

    // MIGRATION: `FormatUser` at `SecurityRoles.ascx.vb:L394-L396` was a LIVE STORED-XSS SINK.
    // It built an anchor by string concatenation and interpolated the account's display name
    // into the markup UNENCODED, so a name containing markup executed in the administrator's
    // browser. The replacement is an escaped interpolation inside a router link, which the
    // framework escapes by default; no markup-injecting binding, no sanitiser and no bypass
    // appears anywhere in this component. The legacy tree already knew better in the same
    // folder — `AccessDenied.ascx.vb:L43` wraps an externally supplied message in an HTML
    // encoder before showing it. The link's target belongs to the account feature, and a link
    // is a URL rather than an import, so no feature imports another here.
    const user = this.userCellTemplate();
    columns.push(
      user === undefined
        ? {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.userName,
            label: ROLE_ASSIGNMENT_TEXT.userNameHeader,
            value: (row: UserRole): string => row.displayName,
          }
        : {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.userName,
            label: ROLE_ASSIGNMENT_TEXT.userNameHeader,
            kind: 'template',
            cellTemplate: user,
          },
    );

    const effective = this.effectiveDateCellTemplate();
    columns.push(
      effective === undefined
        ? {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.effectiveDate,
            label: ROLE_ASSIGNMENT_TEXT.effectiveDateHeader,
            value: (row: UserRole): string => toCalendarDateValue(row.effectiveDate),
          }
        : {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.effectiveDate,
            label: ROLE_ASSIGNMENT_TEXT.effectiveDateHeader,
            kind: 'template',
            cellTemplate: effective,
          },
    );

    const expiry = this.expiryDateCellTemplate();
    columns.push(
      expiry === undefined
        ? {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.expiryDate,
            label: ROLE_ASSIGNMENT_TEXT.expiryDateHeader,
            value: (row: UserRole): string => toCalendarDateValue(row.expiryDate),
          }
        : {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.expiryDate,
            label: ROLE_ASSIGNMENT_TEXT.expiryDateHeader,
            kind: 'template',
            cellTemplate: expiry,
          },
    );

    return columns;
  });

  /**
   * The role addressed by the route.
   *
   * The name is LITERALLY `roleId` because the application binds route parameters onto
   * component inputs of the same name, and the parameter is spelled `roleId` with one
   * lower-case `d`. Renaming it would break the binding silently, with no compilation error
   * anywhere, so the spelling is part of the contract rather than a preference. It is public
   * because the workspace compiles inputs with strict access modifiers.
   *
   * The legacy screens made this hazard concrete: one of them wrote a query-string key as
   * `RoleGroupId` while its sibling read `RoleGroupID`, which worked only because ASP.NET
   * looked query-string keys up case-insensitively. Route parameters are case-SENSITIVE.
   */
  @Input()
  public set roleId(value: number | string | null | undefined) {
    const resolved = parseRouteIdentifier(value);
    if (resolved === this.roleIdSignal()) {
      return;
    }
    this.roleIdSignal.set(resolved);
    this.resetForRole();
    if (resolved === null) {
      return;
    }
    this.loadRole(resolved);
    this.loadAssignments(resolved, null);
  }

  public get roleId(): number | null {
    return this.roleIdSignal();
  }

  /**
   * The tenant's designated administrator account, when the caller knows it.
   *
   * Supplied rather than derived because the tenant's settings are not part of this screen's
   * contract. When it is not supplied the row command is offered and the server refuses the
   * write, which is the same outcome by a different route — see {@link canRemove}.
   */
  @Input()
  public set administratorUserId(value: number | string | null | undefined) {
    this.administratorUserIdSignal.set(parseRouteIdentifier(value));
  }

  public get administratorUserId(): number | null {
    return this.administratorUserIdSignal();
  }

  /** The tenant's administrator role, on the same terms as {@link administratorUserId}. */
  @Input()
  public set administratorRoleId(value: number | string | null | undefined) {
    this.administratorRoleIdSignal.set(parseRouteIdentifier(value));
  }

  public get administratorRoleId(): number | null {
    return this.administratorRoleIdSignal();
  }

  /** The tenant's registered-users role, on the same terms as {@link administratorUserId}. */
  @Input()
  public set registeredRoleId(value: number | string | null | undefined) {
    this.registeredRoleIdSignal.set(parseRouteIdentifier(value));
  }

  public get registeredRoleId(): number | null {
    return this.registeredRoleIdSignal();
  }

  public constructor() {
    // The control event stream is the only source that reports a TOUCHED change as well as a
    // value or status change, which is what the dynamic-display gating above needs. One
    // subscription keeps the mirrored snapshot current for every derived view.
    this.form.events.pipe(takeUntilDestroyed()).subscribe(() => {
      this.formStateSignal.set(this.readFormState());
    });

    // ROLE READ BRIDGE. The store reports a read by settling its flag, so the outcome is
    // observed rather than returned: once the flag falls with a read of ours outstanding, a
    // failure recorded AGAINST THAT OPERATION is reported and the marker is released. The
    // operation match matters because the store's flag is shared with every screen that
    // selects a role.
    //
    // Nothing is copied out on success. The heading reads the store's selected role through an
    // identity-gated projection, so the only work left here is releasing the marker.
    effect(() => {
      const awaited = this.awaitedRoleKey();
      const inFlight = this.store.selectedRoleLoading();
      const failure = this.store.failure();

      if (awaited === null || inFlight === true) {
        return;
      }

      untracked(() => {
        this.awaitedRoleKey.set(null);

        if (failure !== null && failure.operation === 'loadRole') {
          this.raise(this.failureNoticeFor(failure));
        }
      });
    });

    // MEMBERSHIP READ BRIDGE. Raises the deferred message on the fall of the listing flag, and
    // reports a listing failure alongside it — the order the legacy handler fixed
    // (`SecurityRoles.ascx.vb:L579-L584`) and the pairing its error path used, which raised the
    // deferred message and the read failure both.
    //
    // The latch is what makes this fire exactly once per read, including for the re-read the
    // store performs after a write, which this screen never dispatched and so cannot mark.
    effect(() => {
      const loading = this.assignmentsLoading();
      const failure = this.store.failure();

      untracked(() => {
        if (loading === true) {
          this.assignmentsSettling.set(true);
          return;
        }

        if (this.assignmentsSettling() === false) {
          return;
        }

        this.assignmentsSettling.set(false);
        const notice = this.deferredNotice();
        this.deferredNotice.set(null);
        this.raise(notice);

        if (failure !== null && failure.operation === 'loadAssignments') {
          this.raise(this.failureNoticeFor(failure));
        }
      });
    });

    // MEMBERSHIP WRITE BRIDGE. A write is reported the same way: the marker proves the write was
    // ours, the flag falling proves it settled, and the operation-matched failure proves which
    // way it went.
    //
    // Success announces NOTHING and re-reads nothing. The legacy handler was silent on both
    // writes — `SecurityRoles.ascx.vb:L546` rebound the grid and said nothing — and the store
    // performs that re-read itself, so a second one here would race the first.
    //
    // A refusal is the ordered path: the pairing is remembered against the row that was
    // refused, the listing is re-read, and the message is raised only once that read settles.
    effect(() => {
      const awaited = this.awaitedWrite();
      const inFlight = this.store.saving();
      const failure = this.store.failure();

      if (awaited === null || inFlight === true) {
        return;
      }

      untracked(() => {
        this.awaitedWrite.set(null);
        const target = this.outstandingRemoval();
        this.outstandingRemoval.set(null);

        if (failure === null || failure.operation !== awaited) {
          return;
        }

        if (awaited === 'assignUser') {
          this.raise(this.failureNoticeFor(failure));
          return;
        }

        this.problemSignal.set(failure.problem);

        if (failure.conflict === PROTECTED_ASSIGNMENT_CODE && target !== null) {
          this.markProtectedPairing(target.roleId, target.userId);
        }

        const roleId = this.roleIdSignal();
        const notice = this.removalNoticeFor(failure);

        if (roleId === null) {
          this.raise(notice);
          return;
        }

        this.loadAssignments(roleId, notice);
      });
    });
  }


  /**
   * Whether the row command should be offered for one membership.
   *
   * MIGRATION: this is `DeleteButtonVisible` from `SecurityRoles.ascx.vb:L360-L363`, which
   * delegated to `RoleController.CanRemoveUserFromRole`. That rule is one expression at
   * `RoleController.vb:L745`: a membership may not be removed when it is the designated
   * administrator's hold on the administrator role, nor when the role is the registered-users
   * role — the membership that makes an account part of the tenant at all. The legacy source
   * carried the rule TWICE, in two bodies with a comment admitting the duplication
   * (`:L741-L746` and `:L764-L769`); it appears once here.
   *
   * MIGRATION: the legacy expression joined its two administrator comparisons with `And`
   * rather than `AndAlso`, so it did NOT short-circuit and both comparisons always ran. For
   * two integer comparisons that is behaviourally identical, so the outcome is preserved
   * while the target reads left to right and stops as soon as it knows; recorded because the
   * operator differs, not because the answer does.
   *
   * The affordance is ADVISORY and the server is authoritative. The tenant facts the rule
   * needs are inputs rather than fetched here, and when they are absent the command is
   * offered and the write is refused with a machine-readable code, which
   * {@link confirmRemoval} turns into the legacy refusal wording. A pairing the server has
   * already refused is remembered, so the affordance corrects itself without another attempt.
   *
   * @param row One membership of the addressed role.
   * @returns `true` when the command should be rendered.
   */
  public canRemove(row: UserRole): boolean {
    if (this.protectedPairingsSignal().has(pairingKey(row.roleId, row.userId))) {
      return false;
    }
    const administratorUserId = this.administratorUserIdSignal();
    const administratorRoleId = this.administratorRoleIdSignal();
    const registeredRoleId = this.registeredRoleIdSignal();
    const isDesignatedAdministrator =
      administratorUserId !== null &&
      administratorRoleId !== null &&
      administratorUserId === row.userId &&
      administratorRoleId === row.roleId;
    const isRegisteredUsersRole = registeredRoleId !== null && registeredRoleId === row.roleId;
    return isDesignatedAdministrator === false && isRegisteredUsersRole === false;
  }

  /**
   * Runs the account lookup behind the shared search field.
   *
   * MIGRATION: this replaces a dropdown holding EVERY account in the tenant.
   * `securityroles.ascx:L26` declared `cboUsers` with `autopostback="True"`, bound at
   * `SecurityRoles.ascx.vb:L204` to the unfiltered account listing, which does not scale past
   * a small tenant. The screen's own help text says what was intended — 'Enter The User Name
   * and click Validate to confirm' — so the text box was the primary affordance and the
   * lookup replaces the dropdown with it.
   *
   * The term is sent RAW. The listing matches on a prefix, so appending a wildcard would
   * search for the wildcard itself.
   *
   * @param term The name fragment the operator typed.
   */
  public onUserSearch(term: string): void {
    const query = term.trim();
    this.userLookupTermSignal.set(query);

    // The lookup this term replaces is ABANDONED before the next one starts, and also when the box
    // is emptied - a lookup nobody is waiting for is still a request the tenant pays for, and its
    // answer would otherwise repopulate a list the operator has just cleared.
    this.userLookupRequest?.unsubscribe();
    this.userLookupRequest = null;

    if (query.length === 0) {
      this.userLookupLoadingSignal.set(false);
      this.userMatchesSignal.set([]);
      return;
    }
    this.userLookupLoadingSignal.set(true);
    const request: UserListQuery = {
      pageIndex: 0,
      pageSize: USER_LOOKUP_PAGE_SIZE,
      userName: query,
    };
    this.userLookupRequest = this.userService
      .list(request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page): void => {
          this.userLookupLoadingSignal.set(false);
          // `UserService.list` already answers a decoded page, so the envelope needs no
          // second unwrapping here.
          this.userMatchesSignal.set(page.items);
        },
        error: (error: unknown): void => {
          this.userLookupLoadingSignal.set(false);
          this.userMatchesSignal.set([]);
          this.raise(this.failureNotice(error));
        },
      });
  }

  /**
   * Chooses an account and prefills the bounds from its existing membership, if it has one.
   *
   * MIGRATION: this is `GetDates` from `SecurityRoles.ascx.vb:L273-L303`, and only its first
   * branch is reproduced. Where a membership already exists the legacy screen showed its two
   * bounds, skipping either one the null test reported as unset (`:L281-L286`); that is done
   * here, from the values the membership contract publishes.
   *
   * MIGRATION: the second branch — the DERIVED DEFAULT EXPIRY at `:L290-L296` — is
   * deliberately NOT reproduced, and this is a reduction rather than an omission. That branch
   * computed a suggested expiry from the role's BILLING terms alone, whereas the write path it
   * fed prefers the role's TRIAL terms whenever the trial has not been used
   * (`RoleController.vb:L521`), so the suggestion and the stored value could already disagree
   * in the legacy screen. The membership contract does not publish whether a trial has been
   * used, so a client-side suggestion could not tell which terms apply. Leaving the bound
   * empty sends `null`, which the assignment contract documents as "let the server derive one
   * from the role's terms" — the server holds the clock and the trial fact, so it derives the
   * same bound the legacy write path did. What is lost is only the SUGGESTION appearing in the
   * box before the write; what is stored is unchanged.
   *
   * @param user The account the operator chose from the lookup.
   */
  public selectUser(user: UserListItem): void {
    this.selectedUserSignal.set(user);
    this.form.controls.userId.setValue(user.userId);
    this.applyPrefill(user.userId);
  }

  /** Forgets the chosen account and clears the bounds that were prefilled from it. */
  public clearSelectedUser(): void {
    this.selectedUserSignal.set(null);
    this.form.controls.userId.setValue(null);
    this.form.controls.effectiveDate.setValue(NO_DATE);
    this.form.controls.expiryDate.setValue(NO_DATE);
    this.form.controls.effectiveDate.markAsUntouched();
    this.form.controls.expiryDate.markAsUntouched();
    this.formStateSignal.set(this.readFormState());
  }

  /**
   * Enrols the chosen account in the addressed role.
   *
   * MIGRATION: the write is accepted on BOTH a created and a no-content answer. The legacy
   * path was an upsert — `RoleController.vb:L503` started from the null identifier and
   * `:L550-L555` updated an existing membership instead of failing — and the interface this
   * screen calls resolves to nothing at all, so no status is inspected here and either answer
   * lands on the success path. A behavioural nuance worth knowing rather than coding around:
   * the legacy path logged and notified only when the membership was NEW
   * (`RoleController.vb:L655`), so an update was silent.
   *
   * MIGRATION: the listing is RE-READ after a successful write, reproducing the unconditional
   * rebind at `SecurityRoles.ascx.vb:L546`.
   *
   * MIGRATION: an invalid form marks its fields visited and writes nothing, which is what the
   * client-side validators achieved. The legacy handler also rebound the grid on that path
   * (`:L546` sits outside the validity test); the rebind is skipped here because the listing
   * is already current and re-reading it would render identical rows. An immaterial
   * difference, recorded rather than absorbed.
   */
  public submit(): void {
    if (this.saving() === true) {
      return;
    }
    if (this.form.invalid === true) {
      this.form.markAllAsTouched();
      this.formStateSignal.set(this.readFormState());
      return;
    }
    const roleId = this.roleIdSignal();
    const userId = this.form.controls.userId.value;
    if (roleId === null || userId === null) {
      return;
    }
    this.problemSignal.set(null);
    this.awaitedWrite.set('assignUser');
    this.store.assignUser(roleId, this.buildAssignmentRequest(roleId, userId));
  }

  /**
   * Opens the confirmation for one membership.
   *
   * MIGRATION: opening the dialogue does NOT touch the form. The legacy row command declared
   * `causesvalidation="False"` (`securityroles.ascx:L65`), so a removal worked while the add
   * form was invalid and never marked a field visited. Nothing on this path validates,
   * marks or resets the form.
   *
   * @param row The membership the operator asked to remove.
   */
  public requestRemoval(row: UserRole): void {
    this.pendingRemovalSignal.set(row);
  }

  /** Closes the confirmation without writing. */
  public cancelRemoval(): void {
    this.pendingRemovalSignal.set(null);
  }

  /**
   * Removes the confirmed membership.
   *
   * MIGRATION: the listing is ALWAYS re-read and the row is NEVER removed from local state
   * optimistically. A no-content answer does not mean the row is gone: for a paid role whose
   * trial has been used the legacy path back-dated the expiry bound and kept the row so the
   * trial-usage fact survived (`RoleController.vb:L494`, `:L496` and `:L497`, against the
   * plain delete at `:L500`), and the address this screen calls documents the same two
   * outcomes behind one answer. Splicing the row out locally would make a membership that
   * still exists disappear from the screen, which is a correctness defect rather than a
   * cosmetic one.
   *
   * MIGRATION: the ORDER is load-bearing. `SecurityRoles.ascx.vb:L579-L584` rebound the grid
   * unconditionally and only THEN raised the message, so a refusal was read against a
   * refreshed grid. The refusal message is therefore deferred and raised once the re-read has
   * settled, on both its outcomes.
   *
   * MIGRATION: a refusal is told apart from a permission failure by its CODE, not its status.
   * The API answers both with 403 and distinguishes them on the problem type: a protected
   * membership carries `role_assignment.protected`, whose published wording is the legacy
   * `RoleRemoveError` text verbatim, and it is raised at ERROR severity because the legacy
   * screen raised it as a red error (`:L583`). Any other 403 is a permission failure, which
   * the access-denied page presented as a yellow WARNING (`AccessDenied.ascx.vb:L43` and
   * `:L45`), and the shared summariser already maps that status to a warning.
   */
  public confirmRemoval(): void {
    const target = this.pendingRemovalSignal();
    this.pendingRemovalSignal.set(null);
    if (target === null || this.saving() === true) {
      return;
    }
    const roleId = this.roleIdSignal();
    if (roleId === null) {
      return;
    }
    this.problemSignal.set(null);
    this.outstandingRemoval.set(target);
    this.awaitedWrite.set('removeAssignment');
    this.store.removeAssignment(roleId, target.userId);
  }

  /** Clears the banner once the operator has read it. */
  public dismissProblem(): void {
    this.problemSignal.set(null);
  }

  /** Re-reads the addressed role and its memberships, for a retry affordance. */
  public refresh(): void {
    const roleId = this.roleIdSignal();
    if (roleId === null) {
      return;
    }
    this.problemSignal.set(null);
    this.loadRole(roleId);
    this.loadAssignments(roleId, null);
  }


  /** Reads the live form into the snapshot the derived views above depend on. */
  private readFormState(): RoleAssignmentFormState {
    const controls = this.form.controls;
    return {
      userId: controls.userId.value,
      effectiveDate: controls.effectiveDate.value,
      expiryDate: controls.expiryDate.value,
      valid: this.form.valid,
      effectiveDateInvalid: controls.effectiveDate.hasError(INVALID_DATE_ERROR),
      effectiveDateTouched: controls.effectiveDate.touched,
      expiryDateInvalid: controls.expiryDate.hasError(INVALID_DATE_ERROR),
      expiryDateTouched: controls.expiryDate.touched,
      datesOutOfOrder: this.form.hasError(DATE_ORDER_ERROR),
    };
  }

  /**
   * Messages a rejected write reported against one field.
   *
   * The shared narrowing guard decides whether the document carried field messages at all,
   * rather than a predicate written here, and the shared lookup matches the field name against
   * the server's own key spelling — which is not camel-cased and may be prefixed.
   *
   * @param controlName The form control the messages would belong to.
   * @returns The messages, already plain text, or an empty list.
   */
  private serverMessagesFor(controlName: string): readonly string[] {
    const problem = this.problemSignal();
    if (problem === null || isValidationProblemDetails(problem) === false) {
      return [];
    }
    return fieldErrorMessages(problem, controlName);
  }

  /**
   * Returns the screen to its initial state, which a change of addressed role requires.
   *
   * The role and the rows are NOT cleared here and do not need to be: both are derived from
   * the store gated on the addressed key, so they empty themselves the moment the key changes.
   * What is cleared is everything this screen owns outright — the outstanding markers, the
   * banner, the lookup, the confirmation and the form.
   */
  private resetForRole(): void {
    this.awaitedRoleKey.set(null);
    this.awaitedWrite.set(null);
    this.outstandingRemoval.set(null);
    this.deferredNotice.set(null);
    this.assignmentsSettling.set(false);
    this.problemSignal.set(null);
    this.userMatchesSignal.set([]);
    this.userLookupLoadingSignal.set(false);
    this.userLookupTermSignal.set('');
    this.selectedUserSignal.set(null);
    this.pendingRemovalSignal.set(null);
    this.protectedPairingsSignal.set(new Set<string>());
    this.form.reset();
    this.formStateSignal.set(this.readFormState());
  }

  /**
   * Asks the store for the addressed role, which supplies the heading's name.
   *
   * The marker is set BEFORE the command, because the command dispatches synchronously and the
   * bridge that observes its outcome needs to know a read of ours is outstanding.
   *
   * @param roleId The role to read.
   */
  private loadRole(roleId: number): void {
    this.awaitedRoleKey.set(roleId);
    this.store.selectRole(roleId);
  }

  /**
   * Asks the store for every membership of the addressed role, deferring one message until it
   * has settled.
   *
   * MIGRATION: the grid is UNPAGED, as `securityroles.ascx:L56` declares it, so the COMPLETE
   * scope is requested — `RoleStore.loadAllAssignments` follows every page the server reports
   * and remembers the scope, so the re-read each write performs stays complete. Nothing here
   * consumes the shared pager, because the legacy grid had none.
   *
   * The notice is recorded before the command is issued, so the read that raises it is always
   * the read this call started. See {@link RoleAssignmentComponent.deferredNotice}.
   *
   * @param roleId The addressed role.
   * @param notice A message to raise once the read has settled, or `null`.
   */
  private loadAssignments(roleId: number, notice: DeferredNotice | null): void {
    this.deferredNotice.set(notice);
    this.store.loadAllAssignments(roleId);
  }

  /** Prefills the two bounds from the chosen account's existing membership, if it has one. */
  private applyPrefill(userId: number): void {
    const existing = this.assignments().find((row) => row.userId === userId);
    const effective = existing === undefined ? NO_DATE : toCalendarDateValue(existing.effectiveDate);
    const expiry = existing === undefined ? NO_DATE : toCalendarDateValue(existing.expiryDate);
    this.form.controls.effectiveDate.setValue(effective);
    this.form.controls.expiryDate.setValue(expiry);
    this.form.controls.effectiveDate.markAsUntouched();
    this.form.controls.expiryDate.markAsUntouched();
    this.formStateSignal.set(this.readFormState());
  }

  /**
   * Composes the assignment body from the form.
   *
   * MIGRATION: an empty box becomes `null` and never a minimum-value instant. The legacy
   * handler substituted the date sentinel for an empty box
   * (`SecurityRoles.ascx.vb:L528-L539`), but the assignment contract states that the sentinel
   * cannot be stored — the column's range begins in 1753 — and that `null` is how an unset
   * bound is expressed. The contract is followed exactly.
   *
   * MIGRATION: a bound is sent as the BARE calendar date the control produced, with no time
   * part and no zone. Adding a zone would let a conversion move the stored day, and the legacy
   * write stored the day the operator typed at local midnight with no conversion at all, so
   * the bare date is the form that preserves the day.
   *
   * MIGRATION: the designated administrator's own membership of the administrator role has
   * BOTH bounds cleared before the write, reproducing `SecurityRoles.ascx.vb:L522-L526`. Two
   * things about that line are made explicit rather than inherited. It compared an INTEGER
   * role identifier against a STRING — `Role.RoleID = PortalSettings.AdministratorRoleId.ToString`
   * — which compiled only because the administration pages were built with strict type
   * checking off (`Website/release.config:L125` declares `strict="false"`); here both sides are
   * numbers. And it joined the two comparisons with `And` rather than `AndAlso`, so neither
   * side short-circuited; the outcome is identical for integer comparisons and is preserved.
   *
   * @param roleId The addressed role.
   * @param userId The chosen account.
   * @returns The body to send.
   */
  private buildAssignmentRequest(roleId: number, userId: number): RoleAssignmentRequest {
    const administratorUserId = this.administratorUserIdSignal();
    const administratorRoleId = this.administratorRoleIdSignal();
    const isDesignatedAdministrator =
      administratorUserId !== null &&
      administratorRoleId !== null &&
      administratorUserId === userId &&
      administratorRoleId === roleId;
    const effective = isDesignatedAdministrator
      ? NO_DATE
      : this.form.controls.effectiveDate.value.trim();
    const expiry = isDesignatedAdministrator ? NO_DATE : this.form.controls.expiryDate.value.trim();
    return {
      userId,
      effectiveDate: effective.length === 0 ? null : effective,
      expiryDate: expiry.length === 0 ? null : expiry,
      notifyUser: this.form.controls.notify.value,
    };
  }

  /**
   * Turns a refused removal into the message the legacy screen showed.
   *
   * The refusal is recognised by the store's own CONFLICT CODE rather than by a status: the API
   * answers a protected membership and an unprivileged caller alike with 403 and separates them
   * on the problem type. The store already ran that recognition, so no code is re-read here.
   *
   * @param failure The failure the store recorded for the removal.
   * @returns The deferred message, to be raised after the re-read.
   */
  private removalNoticeFor(failure: RoleStoreFailure): DeferredNotice {
    if (failure.conflict === PROTECTED_ASSIGNMENT_CODE) {
      const published = conflictMessage(failure.conflict);
      return {
        // ⚠ THE WORDING IS OVERRIDDEN HERE; THE SEVERITY IS NOT. The published legacy sentence says
        // something the generic summary cannot, so it replaces the message - but the severity has one
        // home, the shared summariser, which reads it from the status. Hard-coding it here gave this
        // refusal a red error while an access refusal answered with the same 403 was a yellow warning,
        // so one decision had two homes and they disagreed.
        severity: failure.summary.severity,
        message: published === null ? ROLE_ASSIGNMENT_TEXT.removalRefused : published,
      };
    }
    return { severity: failure.summary.severity, message: failure.summary.message };
  }

  /**
   * Records a store failure for the banner and describes it for the notification queue.
   *
   * The counterpart of {@link RoleAssignmentComponent.failureNotice} for an outcome the store
   * observed rather than one this screen's own request threw. The wording and severity come
   * from the summary the store already composed, so the two paths cannot describe the same
   * answer differently.
   *
   * @param failure The failure the store recorded.
   * @returns The message to raise.
   */
  private failureNoticeFor(failure: RoleStoreFailure): DeferredNotice {
    this.problemSignal.set(failure.problem);
    return { severity: failure.summary.severity, message: failure.summary.message };
  }

  /**
   * Records a failure for the banner and describes it for the notification queue.
   *
   * The severity comes from the shared summariser, which maps an unauthenticated, forbidden,
   * not-found or throttled answer to a WARNING and everything else to an error — the
   * three-valued vocabulary the legacy screens used, where an access failure was presented as
   * a yellow warning rather than a red error.
   *
   * @param error Whatever the request stream failed with.
   * @returns The message to raise.
   */
  private failureNotice(error: unknown): DeferredNotice {
    const problem = readProblemDetails(error);
    this.problemSignal.set(problem);
    const summary = summarizeProblem(problem);
    return { severity: summary.severity, message: summary.message };
  }

  /** Raises a deferred message, if there is one. */
  private raise(notice: DeferredNotice | null): void {
    if (notice === null) {
      return;
    }
    this.notifications.notify(notice.severity, notice.message);
  }

  /** Remembers a pairing the server has refused, so its command stops being offered. */
  private markProtectedPairing(roleId: number, userId: number): void {
    const key = pairingKey(roleId, userId);
    this.protectedPairingsSignal.update((known) => {
      if (known.has(key)) {
        return known;
      }
      const next = new Set<string>(known);
      next.add(key);
      return next;
    });
  }
}
