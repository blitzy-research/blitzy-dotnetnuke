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
 * ## Why this screen reads the membership listing itself
 *
 * The shared role store exposes an assignment listing, but its page coordinate is private
 * with only a page-INDEX setter, so a screen that also has to prefill from one account's
 * membership cannot drive it. This component therefore reads the listing through the same
 * interface the store itself uses and holds ONE page of it in its own signals. No URL is
 * built here, no HTTP client is touched here, and no header is set here.
 *
 * ## Why the grid is paged even though the legacy grid was not
 *
 * MIGRATION: the legacy grid was UNPAGED — `securityroles.ascx:L56` declares no
 * `AllowPaging`, no pager style and `enableviewstate="false"` — and the first port
 * reproduced that literally: it asked for the largest page the contract allows and then
 * followed every further page the server reported, concurrently, holding the union. That
 * is not a faithful reproduction of an unpaged grid, it is an unbounded read: the listing
 * behind it counts and windows in SQL per request, so following N pages costs N windowed
 * queries whose retained result grows without limit, and a mis-reported page count turned
 * one screen into a fan-out. One ACTIVE page is rendered instead, with the shared pager
 * making the rest reachable, and the page a read answers replaces the one before it rather
 * than accumulating. Every membership remains reachable, so no Delete command goes out of
 * reach; what changes is that reaching the eleventh one takes a pager click, which is the
 * affordance the rest of this application already uses for exactly this situation.
 *
 * The two places that needed the WHOLE set — the action's label and the date prefill — do
 * not read the visible page. They ask the server about the one account that was chosen, so
 * their answer is the same whichever page happens to be on screen; see
 * {@link RoleAssignmentComponent.selectUser}.
 */

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Input,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { RouterLink } from '@angular/router';
import type { Subscription } from 'rxjs';

import {
  DEFAULT_PAGE_SIZE,
  MAX_PAGE_SIZE,
} from '../../../core/models/paged-result.model';
import { isProblemDetails, type ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, RoleAssignmentRequest, UserRole } from '../../../core/models/role.model';
import type { UserListItem, UserListQuery } from '../../../core/models/user.model';
import {
  NotificationService,
  type NotificationSeverity,
} from '../../../core/services/notification.service';
import { RoleService } from '../../../core/services/role.service';
import { UserService } from '../../../core/services/user.service';
import {
  conflictMessage,
  failureCode,
  fieldErrorMessages,
  isValidationProblemDetails,
  summarizeProblem,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
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
   * account holder, and the assignment contract still carries the member - so the member is transmitted
   * rather than dropped. What is NOT carried is the mail subsystem, which this migration excludes
   * wholesale, so no notification is sent for any value of the flag.
   *
   * SAID HERE, BEFORE THE DECISION, RATHER THAN NOT AT ALL. The control used to arrive TICKED and its
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

/** The first page of any listing, counted from nought as the paging contract counts. */
const FIRST_PAGE_INDEX = 0;

/**
 * How many memberships one page of the grid holds.
 *
 * The application's default rather than a size chosen here, so this grid pages on the same
 * coordinate as every other listing in the workspace and an operator meets one pager
 * behaviour throughout. Exactly ONE page is held at a time; see the note on the class.
 */
const MEMBERSHIP_PAGE_SIZE = DEFAULT_PAGE_SIZE;

/**
 * The page size of the single-account membership probe.
 *
 * The probe filters the role's memberships by the chosen account's login name, which the
 * listing matches as a case-insensitive substring of either the login name or the display
 * name, so a handful of rows can come back for a name that is a fragment of another. The
 * largest page the contract allows is asked for so that the one row being looked for cannot
 * fall off the end of the answer in any realistic tenant; when the server reports that even
 * that was truncated, the probe reports "not known" rather than "no membership", which
 * {@link RoleAssignmentComponent.applyProbeAnswer} treats as the empty prefill the legacy
 * screen showed for an account it had no row for.
 */
const MEMBERSHIP_PROBE_PAGE_SIZE = MAX_PAGE_SIZE;


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
  if (typeof value === 'number') {
    return Number.isInteger(value) ? value : null;
  }
  if (typeof value !== 'string') {
    return null;
  }
  const text = value.trim();
  if (/^[+-]?\d+$/.test(text) === false) {
    return null;
  }
  const parsed = Number.parseInt(text, 10);
  return Number.isNaN(parsed) ? null : parsed;
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
    PaginationComponent,
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
  // MIGRATION: 16. unpaged grid -> one page plus the shared pager; :L56; see the class note.
  // MIGRATION: 17. whole-set scans for the label and the prefill -> one keyed server probe;
  // MIGRATION:     :L253, :L273-L303, :L656-L658; see selectUser and probeMembership.

  private readonly roleService = inject(RoleService);
  private readonly userService = inject(UserService);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly roleIdSignal: WritableSignal<number | null> = signal<number | null>(null);
  private readonly roleSignal: WritableSignal<Role | null> = signal<Role | null>(null);
  private readonly roleLoadingSignal: WritableSignal<boolean> = signal(false);
  private readonly assignmentsSignal: WritableSignal<readonly UserRole[]> = signal<
    readonly UserRole[]
  >([]);
  private readonly assignmentsLoadingSignal: WritableSignal<boolean> = signal(false);
  private readonly savingSignal: WritableSignal<boolean> = signal(false);
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

  /** The page of memberships on screen, counted from nought. */
  private readonly pageIndexSignal: WritableSignal<number> = signal(FIRST_PAGE_INDEX);

  /** How many memberships the role has in total, as the last read reported it. */
  private readonly totalCountSignal: WritableSignal<number> = signal(0);

  /**
   * The chosen account's existing membership of this role, as the probe answered.
   *
   * `null` means "no membership is known", which covers three cases the screen treats
   * identically because the legacy screen did: no account is chosen, the account holds no
   * membership, and the probe could not settle the question. All three show empty bounds and
   * the 'Add User' label — the state the legacy grid showed when no row matched.
   */
  private readonly selectedMembershipSignal: WritableSignal<UserRole | null> =
    signal<UserRole | null>(null);

  /**
   * The in-flight read of each slice, held so a NEW read can cancel the one it replaces.
   *
   * Destruction-time cleanup alone is not enough: within one mounted screen the addressed
   * role, the page, the search term and the chosen account all change while a read is in
   * flight, and an HTTP answer that arrives after its request stopped being the current one
   * would overwrite newer state with older. One handle per slice, unsubscribed before it is
   * replaced, makes that impossible — unsubscribing a request observable both abandons the
   * exchange and detaches this observer, so a late answer reaches nothing.
   *
   * A read is still piped through {@link takeUntilDestroyed} as well, so nothing survives
   * the screen itself; these handles govern replacement WITHIN its lifetime.
   */
  private roleRequest: Subscription | null = null;

  /** @see roleRequest */
  private assignmentsRequest: Subscription | null = null;

  /** @see roleRequest */
  private userLookupRequest: Subscription | null = null;

  /** @see roleRequest */
  private membershipProbeRequest: Subscription | null = null;

  /**
   * Which addressed role the screen's state belongs to.
   *
   * A counter rather than a flag, and advanced by {@link resetForRole}, so an answer can be
   * matched against the role it was asked for instead of merely against "some role is
   * addressed". Unsubscribing already stops a superseded read from landing; this is the
   * second fence, and it is the one that holds if a read is ever composed in a way that
   * escapes its handle — a deferred callback, a retry, or a stream that completes
   * synchronously before its handle has been assigned.
   */
  private roleGeneration = 0;

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
      // UNTICKED AND DISABLED, departing from the measured initial state deliberately. The
      // contract's member is still transmitted - a disabled control reports its value like any
      // other - but it now carries FALSE, which is the truthful request: nothing is asking for a
      // notification, because nothing can send one. See notifyHelp above.
      notify: new FormControl<boolean>({ value: false, disabled: true }, { nonNullable: true }),
    },
    { validators: [dateOrderValidator] },
  );

  private readonly formStateSignal: WritableSignal<RoleAssignmentFormState> =
    signal<RoleAssignmentFormState>(this.readFormState());

  /** The role addressed by the route, or `null` when the route carried nothing usable. */
  public readonly resolvedRoleId: Signal<number | null> = this.roleIdSignal.asReadonly();

  /** The addressed role once it has been fetched, used for the heading. */
  public readonly role: Signal<Role | null> = this.roleSignal.asReadonly();

  /** Whether the addressed role is still being fetched. */
  public readonly roleLoading: Signal<boolean> = this.roleLoadingSignal.asReadonly();

  /** The page of memberships on screen. Exactly one page is held; the pager reaches the rest. */
  public readonly assignments: Signal<readonly UserRole[]> = this.assignmentsSignal.asReadonly();

  /** Whether the membership listing is in flight. */
  public readonly assignmentsLoading: Signal<boolean> =
    this.assignmentsLoadingSignal.asReadonly();

  /** The page on screen, counted from nought, for the shared pager's `page` input. */
  public readonly pageIndex: Signal<number> = this.pageIndexSignal.asReadonly();

  /** The page size in effect, for the shared pager's `pageSize` input. */
  public readonly pageSize: Signal<number> = signal(MEMBERSHIP_PAGE_SIZE).asReadonly();

  /** How many memberships the role has, for the shared pager's `totalCount` input. */
  public readonly totalCount: Signal<number> = this.totalCountSignal.asReadonly();

  /**
   * Whether the pager has anything to offer.
   *
   * The same predicate the rest of the workspace draws its pager on — more memberships exist
   * than fit on one page — so a role with ten or fewer members renders exactly what the
   * unpaged legacy grid rendered, with no pager in sight. This says whether the control has
   * work to do; whether it is DRAWN is the template's decision and the control's own.
   */
  public readonly pagerRequired: Signal<boolean> = computed(
    () => MEMBERSHIP_PAGE_SIZE < this.totalCountSignal(),
  );

  /** The chosen account's existing membership of this role, or `null` when none is known. */
  public readonly selectedMembership: Signal<UserRole | null> =
    this.selectedMembershipSignal.asReadonly();

  /** Whether a write is in flight, so the action can be held. */
  public readonly saving: Signal<boolean> = this.savingSignal.asReadonly();

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
    const current = this.roleSignal();
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
      this.savingSignal() === false &&
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
   * the action when a grid row's account matched the chosen one.
   *
   * MIGRATION: the fact is read from the SERVER'S answer about the chosen account rather than
   * by scanning the rendered rows. The legacy grid held every membership, so "a rendered row
   * matches" and "the account holds this role" were the same statement there; with one page
   * rendered they are not, and scanning the page would relabel the action according to which
   * page happens to be on screen. The probe answers the question the legacy test was actually
   * asking, so the label is right on page one and on page nine alike.
   */
  public readonly actionLabel: Signal<string> = computed(() => {
    const chosen = this.formStateSignal().userId;
    if (chosen === null) {
      return ROLE_ASSIGNMENT_TEXT.addUser;
    }
    const membership = this.selectedMembershipSignal();
    const holdsRole = membership !== null && membership.userId === chosen;
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
   * Each lookup CANCELS the one before it. The operator types, so terms supersede one another
   * quickly and the answers need not come back in the order they were asked for; without the
   * cancellation a slow answer to 'a' could land after the answer to 'ann' and put the wider
   * match list back under the narrower term.
   *
   * @param term The name fragment the operator typed.
   */
  public onUserSearch(term: string): void {
    const query = term.trim();
    this.userLookupRequest?.unsubscribe();
    this.userLookupRequest = null;
    this.userLookupTermSignal.set(query);
    if (query.length === 0) {
      this.userLookupLoadingSignal.set(false);
      this.userMatchesSignal.set([]);
      return;
    }
    this.userLookupLoadingSignal.set(true);
    const request: UserListQuery = {
      pageIndex: FIRST_PAGE_INDEX,
      pageSize: USER_LOOKUP_PAGE_SIZE,
      userName: query,
    };
    const generation = this.roleGeneration;
    this.userLookupRequest = this.userService
      .list(request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page): void => {
          if (this.roleGeneration !== generation) {
            return;
          }
          this.userLookupRequest = null;
          this.userLookupLoadingSignal.set(false);
          // The service decodes its own answer against the published contract, so the page
          // arrives already framed AND record-checked. Re-framing it here would erase the
          // record type and re-do a validation the service boundary owns.
          this.userMatchesSignal.set(page.items);
        },
        error: (error: unknown): void => {
          if (this.roleGeneration !== generation) {
            return;
          }
          this.userLookupRequest = null;
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
   * MIGRATION: the membership is asked of the SERVER rather than found among the rendered
   * rows. The legacy screen looked it up in a grid that held every membership
   * (`:L253` bound the account's whole role set), so a lookup over what was on screen was a
   * lookup over everything; with one page on screen it no longer is, and an account whose
   * membership sits on another page would have been prefilled with nothing. One keyed read
   * restores the legacy answer without restoring the unbounded set that used to back it. The
   * bounds are cleared FIRST, so the previous account's dates never sit under a new name
   * while the read is in flight.
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
    this.applyProbeAnswer(null, true);
    const roleId = this.roleIdSignal();
    if (roleId === null) {
      return;
    }
    this.probeMembership(roleId, user, true);
  }

  /** Forgets the chosen account and clears the bounds that were prefilled from it. */
  public clearSelectedUser(): void {
    this.membershipProbeRequest?.unsubscribe();
    this.membershipProbeRequest = null;
    this.selectedUserSignal.set(null);
    this.form.controls.userId.setValue(null);
    this.applyProbeAnswer(null, true);
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
    if (this.savingSignal() === true) {
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
    this.savingSignal.set(true);
    this.roleService
      .assignUser(roleId, this.buildAssignmentRequest(roleId, userId))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (): void => {
          this.savingSignal.set(false);
          this.loadAssignments(roleId, null);
          this.reprobeSelectedMembership();
        },
        error: (error: unknown): void => {
          this.savingSignal.set(false);
          this.raise(this.failureNotice(error));
        },
      });
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
    if (target === null || this.savingSignal() === true) {
      return;
    }
    const roleId = this.roleIdSignal();
    if (roleId === null) {
      return;
    }
    this.problemSignal.set(null);
    this.savingSignal.set(true);
    this.roleService
      .removeUser(roleId, target.userId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (): void => {
          this.savingSignal.set(false);
          this.loadAssignments(roleId, null);
          this.reprobeSelectedMembership();
        },
        error: (error: unknown): void => {
          this.savingSignal.set(false);
          const problem = readProblemDetails(error);
          this.problemSignal.set(problem);
          const code = failureCode(problem);
          if (code === PROTECTED_ASSIGNMENT_CODE) {
            this.markProtectedPairing(target.roleId, target.userId);
          }
          this.loadAssignments(roleId, this.removalNotice(problem, code));
        },
      });
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
    this.reprobeSelectedMembership();
  }

  /**
   * Moves the grid to another page of memberships.
   *
   * The index is passed through untouched: the shared pager reports a ZERO-BASED index and the
   * listing takes one, so there is no base to convert between. The read that follows cancels
   * whichever page read was in flight, so clicking through the pager cannot leave an earlier
   * page's answer to land on top of a later one.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  public onPageChange(pageIndex: number): void {
    const roleId = this.roleIdSignal();
    if (roleId === null || pageIndex === this.pageIndexSignal()) {
      return;
    }
    this.pageIndexSignal.set(pageIndex);
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
   * Every in-flight read is CANCELLED first and the generation is advanced, so nothing asked
   * for on behalf of the previous role can repopulate the state that was just cleared. Both
   * matter for the same reason: this method runs from the route-input setter, so a role change
   * arrives while the previous role's role read, page read, account lookup and membership
   * probe may all still be outstanding.
   */
  private resetForRole(): void {
    this.cancelReads();
    this.roleGeneration += 1;
    this.roleSignal.set(null);
    this.roleLoadingSignal.set(false);
    this.assignmentsSignal.set([]);
    this.assignmentsLoadingSignal.set(false);
    this.pageIndexSignal.set(FIRST_PAGE_INDEX);
    this.totalCountSignal.set(0);
    this.savingSignal.set(false);
    this.problemSignal.set(null);
    this.userMatchesSignal.set([]);
    this.userLookupLoadingSignal.set(false);
    this.userLookupTermSignal.set('');
    this.selectedUserSignal.set(null);
    this.selectedMembershipSignal.set(null);
    this.pendingRemovalSignal.set(null);
    this.protectedPairingsSignal.set(new Set<string>());
    this.form.reset();
    this.formStateSignal.set(this.readFormState());
  }

  /** Abandons every read this screen has outstanding, and forgets their handles. */
  private cancelReads(): void {
    this.roleRequest?.unsubscribe();
    this.roleRequest = null;
    this.assignmentsRequest?.unsubscribe();
    this.assignmentsRequest = null;
    this.userLookupRequest?.unsubscribe();
    this.userLookupRequest = null;
    this.membershipProbeRequest?.unsubscribe();
    this.membershipProbeRequest = null;
  }

  /**
   * Fetches the addressed role, which supplies the heading's name.
   *
   * Cancels whichever role read was outstanding, so a slow answer for the role the operator
   * has navigated away from cannot put its name back into the heading.
   *
   * @param roleId The addressed role.
   */
  private loadRole(roleId: number): void {
    this.roleRequest?.unsubscribe();
    this.roleLoadingSignal.set(true);
    const generation = this.roleGeneration;
    this.roleRequest = this.roleService
      .getRole(roleId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (envelope): void => {
          if (this.roleGeneration !== generation) {
            return;
          }
          this.roleRequest = null;
          this.roleLoadingSignal.set(false);
          this.roleSignal.set(envelope.data);
        },
        error: (error: unknown): void => {
          if (this.roleGeneration !== generation) {
            return;
          }
          this.roleRequest = null;
          this.roleLoadingSignal.set(false);
          this.raise(this.failureNotice(error));
        },
      });
  }

  /**
   * Reads ONE page of the addressed role's memberships and raises any deferred message
   * afterwards.
   *
   * One request, whatever the role's size. The page coordinate comes from the screen's own
   * state, so this one method serves the initial read, a pager click, a retry and the re-read
   * that follows every write, and each of those cancels whichever page read was still in
   * flight rather than racing it.
   *
   * MIGRATION: the total the server reports is kept, because it is what tells the pager
   * whether there is anything beyond this page. It is read from the answer and never
   * accumulated from the rows on screen.
   *
   * A page can be left PAST THE END by a removal — take the only member of the last page away
   * and the coordinate the operator is standing on no longer exists. The legacy grid could not
   * reach that state, because it had no pages; here the read that discovers it steps back to
   * the last page that does exist and asks once more. That correction is deliberately bounded:
   * the corrective read is issued with clamping disabled, so a listing that keeps shrinking
   * underneath the screen produces at most one extra request per read and never a loop. A
   * deferred message rides along with the correction rather than being raised twice, which
   * keeps the legacy ordering — the grid is refreshed, and only then is the message shown.
   *
   * @param roleId The addressed role.
   * @param notice A message to raise once the read has settled, or `null`.
   * @param allowClamp Whether a past-the-end answer may issue one corrective read.
   */
  private loadAssignments(
    roleId: number,
    notice: DeferredNotice | null,
    allowClamp = true,
  ): void {
    this.assignmentsRequest?.unsubscribe();
    this.assignmentsLoadingSignal.set(true);
    const generation = this.roleGeneration;
    const requestedPageIndex = this.pageIndexSignal();
    this.assignmentsRequest = this.roleService
      .listUsers(roleId, { pageIndex: requestedPageIndex, pageSize: MEMBERSHIP_PAGE_SIZE })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response): void => {
          if (this.roleGeneration !== generation) {
            return;
          }
          this.assignmentsRequest = null;
          const page = response;
          this.totalCountSignal.set(page.meta.totalCount);
          const pastTheEnd =
            page.items.length === 0 &&
            requestedPageIndex > FIRST_PAGE_INDEX &&
            page.meta.totalCount > 0;
          if (allowClamp && pastTheEnd) {
            const lastPageIndex = Math.max(page.meta.totalPages - 1, FIRST_PAGE_INDEX);
            this.pageIndexSignal.set(lastPageIndex);
            this.loadAssignments(roleId, notice, false);
            return;
          }
          this.assignmentsLoadingSignal.set(false);
          this.assignmentsSignal.set(page.items);
          this.raise(notice);
        },
        error: (error: unknown): void => {
          if (this.roleGeneration !== generation) {
            return;
          }
          this.assignmentsRequest = null;
          this.assignmentsLoadingSignal.set(false);
          this.raise(notice);
          this.raise(this.failureNotice(error));
        },
      });
  }

  /**
   * Asks the server whether one account already holds the addressed role, and on what terms.
   *
   * The listing's free-text filter is a case-insensitive SUBSTRING match against either the
   * login name or the display name, so filtering by the chosen account's login name narrows
   * the role's memberships to a handful and the wanted row is then picked out by identifier —
   * the name is the filter, the identifier is the match. A name is not an identifier, which is
   * why the identifier decides and the name only reduces what has to be looked through.
   *
   * @param roleId The addressed role.
   * @param user The account the operator chose.
   * @param prefillBounds Whether the answer may also write the two date boxes.
   */
  private probeMembership(roleId: number, user: UserListItem, prefillBounds: boolean): void {
    this.membershipProbeRequest?.unsubscribe();
    const generation = this.roleGeneration;
    this.membershipProbeRequest = this.roleService
      .listUsers(roleId, {
        pageIndex: FIRST_PAGE_INDEX,
        pageSize: MEMBERSHIP_PROBE_PAGE_SIZE,
        query: user.username,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response): void => {
          if (this.roleGeneration !== generation || this.selectedUserSignal() !== user) {
            return;
          }
          this.membershipProbeRequest = null;
          const page = response;
          const found = page.items.find((row) => row.userId === user.userId);
          this.applyProbeAnswer(found ?? null, prefillBounds);
        },
        error: (error: unknown): void => {
          if (this.roleGeneration !== generation || this.selectedUserSignal() !== user) {
            return;
          }
          this.membershipProbeRequest = null;
          // A failed probe leaves the screen in the state it shows for an account with no
          // membership - empty bounds and the 'Add User' label - because that is the state the
          // legacy screen showed whenever its lookup found no row, and because the write is an
          // upsert either way: the server settles which of the two it is. The failure is still
          // reported, so the operator is not left thinking the account definitely has none.
          this.applyProbeAnswer(null, prefillBounds);
          this.raise(this.failureNotice(error));
        },
      });
  }

  /**
   * Re-asks whether the chosen account holds the role, after a write may have changed it.
   *
   * MIGRATION: this refreshes the FACT and deliberately leaves the two date boxes alone. The
   * legacy handler did not touch the form after a write — `SecurityRoles.ascx.vb:L546` rebound
   * the grid and nothing else — so an operator who left the expiry empty and let the server
   * derive one saw an empty box afterwards, not the derived value. Writing the stored bounds
   * back here would put a value the operator never typed into a box that a second submit would
   * then send explicitly, which is a change to what gets STORED and not merely to what is
   * shown. Only the label's fact is refreshed, which is the one thing the legacy rebind did
   * change.
   */
  private reprobeSelectedMembership(): void {
    const roleId = this.roleIdSignal();
    const user = this.selectedUserSignal();
    if (roleId === null || user === null) {
      return;
    }
    this.probeMembership(roleId, user, false);
  }

  /**
   * Records the probe's answer, and prefills the two bounds from it when asked to.
   *
   * Passing `null` is how the screen expresses "no membership is known", and with prefilling
   * on it produces exactly the state the legacy screen showed for an account with no row: both
   * boxes empty and neither of them marked as visited, so no dynamic validator has anything to
   * say about a value the operator never typed.
   *
   * @param membership The chosen account's membership of this role, or `null`.
   * @param prefillBounds Whether the two date boxes are written from the answer.
   */
  private applyProbeAnswer(membership: UserRole | null, prefillBounds: boolean): void {
    this.selectedMembershipSignal.set(membership);
    if (prefillBounds === false) {
      return;
    }
    const effective = membership === null ? NO_DATE : toCalendarDateValue(membership.effectiveDate);
    const expiry = membership === null ? NO_DATE : toCalendarDateValue(membership.expiryDate);
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
   * @param problem The problem document the refusal carried, or `null`.
   * @param code The failure code read out of that document, or `null`.
   * @returns The deferred message, to be raised after the re-read.
   */
  private removalNotice(problem: ProblemDetails | null, code: string | null): DeferredNotice {
    if (code === PROTECTED_ASSIGNMENT_CODE) {
      // The WORDING is the conflict vocabulary's, because that is where the legacy `RoleRemoveError`
      // sentence lives. The SEVERITY is the shared summariser's, for the reason set out on
      // {@link failureNotice}: this refusal arrives as `403` - the vocabulary records that explicitly,
      // since every other conflict code arrives as `409` - and an access failure is a WARNING rather
      // than an error in the three-valued vocabulary the legacy screens used. Hard-coding a severity
      // here would give one decision two homes that could disagree.
      const published = conflictMessage(code);
      const refusal = summarizeProblem(problem);
      return {
        severity: refusal.severity,
        message: published === null ? ROLE_ASSIGNMENT_TEXT.removalRefused : published,
      };
    }
    const summary = summarizeProblem(problem);
    return { severity: summary.severity, message: summary.message };
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

