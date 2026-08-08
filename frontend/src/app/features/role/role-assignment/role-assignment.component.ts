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
 * The listing is read ONE PAGE AT A TIME, `RoleStore.loadAssignments`, and the shared pager
 * reaches the rest. The legacy grid declared no pager — `securityroles.ascx:L56` carries no
 * `AllowPaging`, no pager style and `enableviewstate="false"` — and a first port reproduced
 * that literally, by reading every page the server reported and rendering the union: an
 * unbounded read of a listing the endpoint counts and windows per request, and an unbounded
 * render of whatever came back. One page is held instead. Every membership stays addressable
 * because the pager reaches it, so the Delete command on an eleventh member is one click away
 * rather than out of reach, and the pager is drawn on the same predicate as every other
 * listing here — only when more memberships exist than fit on one page — which means a role
 * small enough to have fitted in the legacy grid renders with no pager at all and looks
 * exactly as it did.
 *
 * The two questions the legacy screen answered by SCANNING that whole grid — what bounds to
 * show for the account the operator chose, and whether to relabel the action 'Update User
 * Role' — are answered by a keyed probe instead, `RoleStore.probeAssignment`. A scan of one
 * page would answer "holds nothing" for an account whose row sits on another page, which the
 * legacy screen never did; one narrow request, filtered to the chosen account and matched by
 * identifier, reproduces the legacy answer without materialising the membership.
 *
 * The one transport this screen still calls directly is the ACCOUNT CANDIDATE LIST behind
 * whichever account control the tenant asked for, `UserService.list`. That is deliberate and is
 * not the defect above: the candidates it returns are a transient list held nowhere else, owned
 * by nothing else and discarded when the choice is made, so there is no second copy to diverge
 * from. Routing it through the account store would instead make this screen mutate that store's
 * shared search term and page coordinate, which would move a sibling account listing under its
 * own operator — trading a copy that cannot diverge for state that genuinely can.
 *
 * The tenant's ACCOUNT POLICY is the other way round, and comes from {@link UserStore} for
 * exactly the same reason stated in reverse: the policy IS held elsewhere and IS owned elsewhere,
 * so a private copy here could disagree with the screen that edits it. Reading it disturbs no
 * coordinate another screen holds, which is what makes the two decisions consistent rather than
 * arbitrary.
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

import { EMPTY, count, expand, map, throwError, type Observable, type Subscription } from 'rxjs';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { RouterLink } from '@angular/router';

import {
  DEFAULT_PAGE_SIZE,
  MAX_PAGE_SIZE,
  toPagedResult,
  type ApiMeta,
  type PagedResponse,
  type PagedResult,
} from '../../../core/models/paged-result.model';
import { isProblemDetails, type ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, RoleAssignmentRequest, UserRole } from '../../../core/models/role.model';
import type { UserListItem, UserListQuery } from '../../../core/models/user.model';
import {
  NotificationService,
  type NotificationSeverity,
} from '../../../core/services/notification.service';
import { UserService } from '../../../core/services/user.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { OperationGeneration } from '../../../core/utils/operation-generation.util';
import {
  RoleStore,
  type RoleStoreFailure,
  type RoleStoreOperation,
} from '../../../core/state/role.store';
import { UserStore } from '../../../core/state/user.store';
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
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
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

  /**
   * Standing context under the offered matches, shown only when MORE accounts match than are
   * being offered. `{0}` is how many are offered, `{1}` the server's own count of the match set.
   *
   * It exists because the lookup examines every page of the match set rather than only the
   * first, so the number of accounts it found and the number it can reasonably offer as buttons
   * are now different numbers. Saying both is what keeps the shorter list from reading as the
   * whole answer.
   */
  lookupPartial:
    'Showing {0} of {1} matching accounts. Type more of the name to narrow the list.',

  /**
   * The same context for a lookup that DID find the exact name, on the same two substitutions.
   *
   * Separate wording rather than one sentence for both, because the narrowing advice is wrong here:
   * the operator typed the whole name and got it, so telling them to type more of it would be
   * advice against an outcome that already succeeded. What they still need to know is that the
   * shorter list is not the whole match set.
   */
  lookupExact: 'The exact match is offered first. Showing {0} of {1} matching accounts.',

  /**
   * Shown when the walk stopped at its own page ceiling without finding an exact match. `{0}` is
   * how many accounts it examined, `{1}` the server's own count of the match set.
   */
  lookupCurtailed:
    'The search examined {0} of {1} matching accounts without finding an exact match and stopped ' +
    'there. Type more of the name to narrow it.',

  /** The label on the drop-down the tenant's account policy can ask for instead of the name box. */
  userChoiceLabel: 'User Name',

  /**
   * MIGRATION: `plUsers.HelpText` belongs to the name box and says 'Enter The User Name and
   * click Validate to confirm', which is untrue of the drop-down the other policy value selects.
   * The legacy screen carried one help string for both controls because both shared one label
   * cell (`securityroles.ascx:L14`); this states what the drop-down actually does.
   */
  userChoiceHelp: 'Choose an account from every account in this site.',

  /** The unselected entry of that drop-down. */
  userChoicePrompt: '<None Specified>',

  /** Shown while the tenant's account policy is being read, before either control is offered. */
  accountPolicyLoading: 'Reading how this site asks you to choose an account…',

  /**
   * Shown while a lookup is walking pages.
   *
   * ⚠ THE LOOKUP'S OWN, distinct from {@link accountChoicesLoading}. A lookup searches for a name
   * and the drop-down walk lists every account; one wording for both would tell an operator who
   * typed a name that the site was being enumerated.
   */
  lookupLoading: 'Searching for matching accounts…',

  /**
   * Shown when the account policy could not be read, so the name box is offered without knowing
   * which control the tenant prefers.
   */
  accountPolicyUnavailable:
    "This site's preferred account selector could not be read, so the name box is offered.",

  /**
   * Shown when the tenant asked for the drop-down but the complete account list could not be
   * assembled, so the name box is offered in its place.
   *
   * The name box needs no complete list, which is why this degrades rather than fails: the
   * capability the operator loses is browsing, and the capability they keep — naming the account
   * — is the one the legacy screen's own help text described.
   */
  accountChoicesUnavailable:
    'Every account in this site could not be listed, so the name box is offered instead.',

  /** Shown while the complete account list for the drop-down is being assembled. */
  accountChoicesLoading: 'Listing every account in this site…',

  /** Shown when the tenant asked for the drop-down and the site holds no accounts to offer. */
  accountChoicesEmpty: 'This site holds no accounts to choose from.',

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

/**
 * How many accounts one request of the lookup walk asks for.
 *
 * ⚠ THE SERVER'S OWN MAXIMUM, and it replaced the shared DEFAULT of ten. The account listing's
 * paging validator refuses a larger page, so this is the widest legal request and it is what
 * keeps the number of round trips the walk in {@link RoleAssignmentComponent.onUserSearch} has
 * to make as low as the endpoint allows. It is NOT by itself the fix for that walk — see the
 * note there.
 */
const USER_LOOKUP_PAGE_SIZE = MAX_PAGE_SIZE;

/**
 * How many matched accounts are offered as choices at once.
 *
 * A rendering bound rather than a search bound, and the distinction is what makes it honest.
 * The walk examines every page the server reports until it finds the exact name or exhausts the
 * match set; this limits only how many of the near matches become buttons, because a one-letter
 * prefix in a large tenant legitimately matches thousands of accounts and offering thousands of
 * buttons is not a choice anybody can make. Whenever it bites, the template says so and reports
 * the server's own total — see {@link RoleAssignmentComponent.userLookupSummary}.
 *
 * An exact match is ALWAYS offered regardless of this bound, because it is the one result the
 * legacy screen existed to produce.
 */
const USER_LOOKUP_DISPLAY_LIMIT = MAX_PAGE_SIZE;

/**
 * The hard ceiling on how many pages one account lookup will request.
 *
 * ⚠ DELIBERATELY MODEST, BECAUSE THE WALK IS A GUARANTEE RATHER THAN THE MECHANISM. The lookup
 * asks the listing to order by login name, which — the filter being a literal prefix match — puts
 * an exact match first among every account it can return, so the ordinary answer arrives in ONE
 * request. The walk exists so that correctness does not *depend* on the server honouring that
 * order, and it goes deep only for a term short enough to match thousands of accounts none of
 * which is the name typed. At {@link USER_LOOKUP_PAGE_SIZE} accounts a page it still examines two
 * thousand of them, which is far past the point at which the operator is better served by typing
 * more of the name — and far cheaper than a ceiling generous enough to make a one-character search
 * cost hundreds of round trips.
 *
 * ⚠ REACHING IT DOES NOT PRODUCE A REFUSAL, and that asymmetry with the store's walks is
 * deliberate. Those walks answer "every module" and "every membership", where a partial answer
 * masquerading as complete is the defect; this one answers "does this name exist", where the
 * matches already examined are genuinely useful and the operator's next move — typing more of
 * the name — is both obvious and offered. The template states that the search was cut short
 * rather than pretending it was exhaustive.
 */
const MAX_USER_LOOKUP_PAGES = 20;

/**
 * The hard ceiling on how many pages the complete-account-list walk will request.
 *
 * Unlike {@link MAX_USER_LOOKUP_PAGES}, reaching this one is a REFUSAL — see
 * {@link RoleAssignmentComponent.walkAllAccounts} for why the two differ. At
 * {@link USER_LOOKUP_PAGE_SIZE} accounts a page it accommodates a hundred thousand accounts,
 * which is two orders of magnitude past the thousand-account threshold at which the legacy code
 * itself stopped offering this drop-down (`UserModuleBase.vb:L178-L186`).
 */
const MAX_ACCOUNT_CHOICE_PAGES = 1000;

/**
 * How the tenant's account policy says this screen should let an operator pick an account.
 *
 * MIGRATION: this is `Security_UsersControl`, read at `SecurityRoles.ascx.vb:L133-L136` through
 * `UserModuleBase.GetSetting(PortalId, "Security_UsersControl")` and acted on at `:L202-L221`,
 * where `UsersControl.Combo` bound `cboUsers` to the tenant's whole account listing and hid the
 * text box, and the other value did the reverse. `:L106-L109` then read the chosen account from
 * whichever control was live. The two numeric values are the legacy enumeration's own and are
 * the values the settings contract carries, so they are preserved rather than renamed.
 */
const USERS_CONTROL = Object.freeze({
  /** A drop-down list of every account in the tenant. `UsersControl.Combo`. */
  combo: 0,

  /** A name box with a lookup. `UsersControl.TextBox`. */
  textBox: 1,
} as const);

/** Which account-selection control this screen is presenting. */
type UsersControlMode = 'combo' | 'lookup';

/** Why an account lookup stopped walking pages. */
type UserLookupCompletion =
  /** Every account the server said matches was examined. */
  | 'complete'
  /** The exact name was found, so there was nothing left worth examining. */
  | 'exact'
  /** {@link MAX_USER_LOOKUP_PAGES} was reached first. */
  | 'curtailed';

/**
 * What one completed account lookup found.
 *
 * Carried as one value rather than as three separate signals so the matches, the counts and the
 * reason the walk stopped are published together and cannot describe different lookups.
 */
interface UserLookupOutcome {
  /** The accounts offered as choices — already limited to {@link USER_LOOKUP_DISPLAY_LIMIT}. */
  readonly matches: readonly UserListItem[];

  /** How many accounts the walk examined, which is at least `matches.length`. */
  readonly examined: number;

  /** The server's own count of the whole match set. */
  readonly reportedTotal: number;

  /** Why the walk stopped. */
  readonly completion: UserLookupCompletion;
}

/** An account lookup that found nothing, for the cleared and failed states. */
const NO_USER_LOOKUP: UserLookupOutcome = Object.freeze({
  matches: Object.freeze([]) as readonly UserListItem[],
  examined: 0,
  reportedTotal: 0,
  completion: 'complete',
});

/**
 * Chooses which of a lookup's matches become buttons, keeping the exact one whatever else goes.
 *
 * The head of the set is offered, because the listing returns matches in the server's own order
 * and the nearest prefixes come first. THE EXACT MATCH IS HOISTED TO THE FRONT when it falls
 * outside that head: the walk stops on the page that holds it, so a name found on the fourth page
 * of a large match set would otherwise be examined and then dropped from the very list it ended
 * the search — which would reproduce the unreachable-account defect one layer higher up.
 *
 * @param collected Every account the walk examined, in the order the server supplied them.
 * @param isExact Whether one account carries exactly the name that was searched for.
 * @returns The accounts to offer, never more than {@link USER_LOOKUP_DISPLAY_LIMIT}.
 */
function offerableMatches(
  collected: readonly UserListItem[],
  isExact: (candidate: UserListItem) => boolean,
): readonly UserListItem[] {
  if (collected.length <= USER_LOOKUP_DISPLAY_LIMIT) {
    return collected;
  }

  const head = collected.slice(0, USER_LOOKUP_DISPLAY_LIMIT);
  if (head.some(isExact)) {
    return head;
  }

  const exact = collected.find(isExact);
  if (exact === undefined) {
    return head;
  }

  return [exact, ...head.slice(0, USER_LOOKUP_DISPLAY_LIMIT - 1)];
}

/**
 * The paging facts to report while no answer for the addressed role is in hand.
 *
 * Its applied size is NOUGHT, which is the value the shared pager treats as unresolved and
 * declines to render for — so a screen that has not yet received a page shows no pager at all
 * rather than one claiming a single empty page. The same envelope the paging contract returns
 * for an empty result, restated here because this screen must publish something while the
 * store's slice belongs to another role.
 */
const UNRESOLVED_PAGE_META: ApiMeta = Object.freeze({
  totalCount: 0,
  pageIndex: 0,
  pageSize: 0,
  totalPages: 0,
});

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
    LoadingSpinnerComponent,
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
  // MIGRATION: 16. unpaged grid -> one page plus the shared pager; securityroles.ascx:L56; see
  // MIGRATION:     loadAssignments and pagerRequired.
  // MIGRATION: 17. whole-set scans for the label and the prefill -> one keyed server probe;
  // MIGRATION:     :L253, :L273-L303, :L656-L658; see selectUser and selectedMembership.

  private readonly store = inject(RoleStore);
  private readonly userService = inject(UserService);

  /**
   * The identity, read for ONE fact: which tenant the caller belongs to.
   *
   * Taken from the caller rather than from a route, because this screen addresses a role and names
   * no portal.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The tenant's own record, for the protected pairing this screen must not offer to remove.
   *
   * CORE state, injected as the account listing and the role editor both inject it. The store owns
   * the request, de-duplicates it across screens, and discards one tenant's facts the moment
   * another tenant is asked for.
   */
  private readonly portals = inject(PortalStore);

  /**
   * The owner of the tenant's account policy, which decides how an account is chosen here.
   *
   * ⚠ THE STORE RATHER THAN THE TRANSPORT, and that is this file's own rule applied rather than
   * an inconsistency with the lookup beside it. The lookup calls the transport because its
   * matches are "held nowhere else, owned by nothing else"; the account policy is held somewhere
   * else and owned by something else — {@link UserStore} publishes it and the account-listing
   * screen already reads it from there — so a second copy here is exactly the divergence this
   * screen's header records having corrected for the role and its memberships.
   *
   * Reading it disturbs no coordinate another screen holds: `loadMembershipSettings` touches the
   * policy slice and that store's failure slice, and never the account listing's search term, page
   * coordinate or rows — which is the one objection that kept the lookup on the transport. Clearing
   * the failure slice is the store's own contract for starting a read and costs nothing here, since
   * only one routed screen is mounted at a time and the account listing re-records its own failures
   * when it next reads.
   */
  private readonly accounts = inject(UserStore);
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

  /**
   * The complete-account-list walk in flight, or `null` when none is.
   *
   * Held for the same reason as {@link userLookupRequest}: a walk the screen has stopped waiting
   * for is still a sequence of requests the tenant pays for, and its answer would repopulate a
   * drop-down the policy may since have replaced with the name box.
   */
  private accountChoicesRequest: Subscription | null = null;

  /** The membership write this screen is waiting on, or `null` when none is outstanding. */
  private readonly awaitedWrite: WritableSignal<AwaitedAssignmentWrite | null> =
    signal<AwaitedAssignmentWrite | null>(null);

  /**
   * The identifier the store issued for {@link RoleAssignmentComponent.awaitedWrite}.
   *
   * Zero means "no write of ours is outstanding". That is safe rather than a sentinel collision: the
   * store pre-increments, so the first identifier it ever issues is 1 and no real write holds 0.
   */
  private readonly awaitedWriteId: WritableSignal<number> = signal<number>(0);

  /**
   * The generation of the account lookup now wanted.
   *
   * ⚠ CANCELLATION ALONE IS NOT SUFFICIENT HERE, WHICH IS WHY THIS EXISTS ALONGSIDE THE HANDLE.
   * Releasing the handle stops a superseded lookup being delivered, and it is the stronger fix where
   * it applies — but the handle is released on two paths that are not "a newer lookup started": the
   * box being emptied, and the addressed role changing. A response already scheduled to commit is not
   * recalled by either, so the callback also asks whether the answer it is holding is still the answer
   * to the question that was asked.
   */
  private readonly userLookupGeneration = new OperationGeneration();

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
   * The account whose probe answer may still write the two date boxes, or `null`.
   *
   * A one-shot marker rather than a mode. The probe runs in two circumstances — the operator
   * has just chosen an account, and a write has just settled — and only the FIRST of those may
   * touch the form: `SecurityRoles.ascx.vb:L546` rebound the grid after a write and left the
   * form alone, so an operator who left the expiry empty and let the server derive one saw an
   * empty box afterwards, not the derived value. Writing the stored bounds back after a write
   * would put a value the operator never typed into a box that a second submit would then send
   * explicitly — a change to what gets STORED, not merely to what is shown.
   *
   * Held as the account's identifier and compared with `===`, because a probe answer that
   * arrives after the operator has moved on to a different account must not prefill from it.
   * Absence is `null`: account identifiers seed at one on this schema, but sibling tables seed
   * at zero and minus one, so absence is never a number here.
   */
  private readonly awaitedPrefillUserId: WritableSignal<number | null> = signal<number | null>(
    null,
  );

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
  /**
   * What the last completed account lookup found.
   *
   * One value rather than several so the offered matches and the two counts beside them can never
   * describe different lookups. See {@link UserLookupOutcome}.
   */
  private readonly userLookupSignal: WritableSignal<UserLookupOutcome> =
    signal<UserLookupOutcome>(NO_USER_LOOKUP);

  private readonly userLookupLoadingSignal: WritableSignal<boolean> = signal(false);
  private readonly userLookupTermSignal: WritableSignal<string> = signal('');

  /** Every account in the tenant, for the drop-down the account policy can ask for. */
  private readonly accountChoicesSignal: WritableSignal<readonly UserListItem[]> = signal<
    readonly UserListItem[]
  >([]);

  private readonly accountChoicesLoadingSignal: WritableSignal<boolean> = signal(false);

  /**
   * Whether the complete account list could NOT be assembled, so the drop-down cannot be offered.
   *
   * A refusal rather than a truncation, on the same terms the membership and module walks settled:
   * a drop-down that claims to hold every account and silently holds some of them hides the
   * accounts it dropped, and an operator cannot tell a missing account from an absent one. What is
   * different here is the remedy — the name box needs no complete list and reaches any account by
   * name, so the screen falls back to it and says so instead of failing outright.
   */
  private readonly accountChoicesFailedSignal: WritableSignal<boolean> = signal(false);
  private readonly selectedUserSignal: WritableSignal<UserListItem | null> =
    signal<UserListItem | null>(null);
  private readonly pendingRemovalSignal: WritableSignal<UserRole | null> = signal<UserRole | null>(
    null,
  );
  private readonly protectedPairingsSignal: WritableSignal<ReadonlySet<string>> = signal<
    ReadonlySet<string>
  >(new Set<string>());

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
   * The page of memberships on screen. Exactly one page is held; the pager reaches the rest.
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
   * The paging facts of the page in hand, gated on the addressed role.
   *
   * One gate for all three pager inputs, so they cannot disagree with each other or with the
   * rows: a slice read for another role reports {@link UNRESOLVED_PAGE_META}, whose applied size
   * of nought is what withholds the pager entirely until a real answer has landed.
   */
  private readonly assignmentsMeta: Signal<ApiMeta> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return UNRESOLVED_PAGE_META;
    }

    return this.store.assignmentsMeta();
  });

  /**
   * The page on screen, counted from nought, for the shared pager's `page` input.
   *
   * The index the SERVER reported is bound rather than the one this screen last asked for, so
   * the pager can never claim to be on a page whose request failed. No arithmetic appears
   * anywhere on this path: the pager's input and its output are both zero-based, as the wire
   * is.
   */
  public readonly pageIndex: Signal<number> = computed(() => this.assignmentsMeta().pageIndex);

  /**
   * The page size in effect, for the shared pager's `pageSize` input.
   *
   * Also the server's own figure and deliberately not a constant declared here: the size that
   * was applied is a fact about the answer in hand, and binding a local constant would make the
   * pager compute a page count the server did not.
   */
  public readonly pageSize: Signal<number> = computed(() => this.assignmentsMeta().pageSize);

  /** How many memberships the role has in total, for the shared pager's `totalCount` input. */
  public readonly totalCount: Signal<number> = computed(() => this.assignmentsMeta().totalCount);

  /**
   * Whether the pager has anything to offer.
   *
   * The same predicate the rest of the workspace draws its pager on — more memberships exist
   * than fit on one page — so a role with ten or fewer members renders exactly what the unpaged
   * legacy grid rendered, with no pager in sight. This says whether the control has work to do;
   * whether it is DRAWN is the template's decision and the control's own.
   *
   * The size is tested for a positive value first, because the slice starts at the empty
   * envelope whose applied size is nought, and `0 < 0` would otherwise be the only thing
   * standing between an unresolved page size and a pager bound to it.
   */
  public readonly pagerRequired: Signal<boolean> = computed(() => {
    const size = this.pageSize();

    return size > 0 && size < this.totalCount();
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

  /**
   * The accounts the current lookup offers as choices.
   *
   * Not every account it matched: see {@link USER_LOOKUP_DISPLAY_LIMIT}, and
   * {@link userLookupSummary} for the sentence that reports the difference whenever there is one.
   */
  public readonly userMatches: Signal<readonly UserListItem[]> = computed(
    () => this.userLookupSignal().matches,
  );

  /** Whether an account lookup is in flight. */
  public readonly userLookupLoading: Signal<boolean> = this.userLookupLoadingSignal.asReadonly();

  /**
   * The sentence describing a lookup whose match set is larger than what is offered, or `null`.
   *
   * `null` in the ordinary case, where every matching account is a button and there is nothing to
   * explain. It reports the server's own count rather than a count of what is displayed, because
   * the whole point of saying anything is that the two differ.
   */
  public readonly userLookupSummary: Signal<string | null> = computed(() => {
    const outcome = this.userLookupSignal();
    if (outcome.matches.length === 0) {
      return null;
    }

    // A server that under-reports its own total would otherwise produce a sentence claiming fewer
    // accounts exist than were examined. The larger of the two figures is the only defensible one.
    const total = Math.max(outcome.reportedTotal, outcome.examined);

    if (outcome.completion === 'curtailed') {
      return ROLE_ASSIGNMENT_TEXT.lookupCurtailed.replace('{0}', String(outcome.examined)).replace(
        '{1}',
        String(total),
      );
    }

    if (outcome.matches.length >= total) {
      return null;
    }

    const template =
      outcome.completion === 'exact'
        ? ROLE_ASSIGNMENT_TEXT.lookupExact
        : ROLE_ASSIGNMENT_TEXT.lookupPartial;

    return template.replace('{0}', String(outcome.matches.length)).replace('{1}', String(total));
  });

  /**
   * Which account-selection control the tenant's policy asks this screen to present.
   *
   * MIGRATION: this is `Security_UsersControl`, acted on at `SecurityRoles.ascx.vb:L202-L221`.
   * The legacy screen resolved it once during page load and rendered exactly one of the two
   * controls, hiding the other; the same holds here, and {@link accountPolicyPending} is what
   * keeps a control from being offered before the policy is known and then swapped underneath the
   * operator.
   *
   * The name box is the answer whenever the drop-down cannot be honoured — an unread policy, or a
   * complete account list that could not be assembled — because it is the affordance that needs no
   * tenant-wide read, and it is the one the legacy help text described.
   */
  public readonly usersControlMode: Signal<UsersControlMode> = computed(() => {
    if (this.accountChoicesFailedSignal()) {
      return 'lookup';
    }

    const settings = this.accounts.membershipSettings();
    if (settings === null) {
      return 'lookup';
    }

    return settings.securityUsersControl === USERS_CONTROL.combo ? 'combo' : 'lookup';
  });

  /**
   * Whether the account policy has not yet resolved, so neither control should be offered.
   *
   * The legacy screen never had this state — it decided before it rendered — and reproducing that
   * means holding the field rather than guessing and correcting.
   */
  public readonly accountPolicyPending: Signal<boolean> = computed(
    () => this.accounts.membershipSettings() === null && this.accounts.membershipSettingsLoading(),
  );

  /** Whether the account policy could not be read at all, so the template can say why. */
  public readonly accountPolicyUnavailable: Signal<boolean> = computed(
    () =>
      this.accounts.membershipSettings() === null &&
      this.accounts.membershipSettingsLoading() === false,
  );

  /** Every account in the tenant, for the drop-down. Empty in every other mode. */
  public readonly accountChoices: Signal<readonly UserListItem[]> =
    this.accountChoicesSignal.asReadonly();

  /** Whether the complete account list is being assembled. */
  public readonly accountChoicesLoading: Signal<boolean> =
    this.accountChoicesLoadingSignal.asReadonly();

  /** Whether the drop-down was asked for but could not be built. See {@link usersControlMode}. */
  public readonly accountChoicesUnavailable: Signal<boolean> =
    this.accountChoicesFailedSignal.asReadonly();

  /** Whether the drop-down resolved and the tenant simply holds no accounts. */
  public readonly accountChoicesEmpty: Signal<boolean> = computed(
    () =>
      this.usersControlMode() === 'combo' &&
      this.accountChoicesLoadingSignal() === false &&
      this.accountChoicesSignal().length === 0,
  );

  /** The account chosen for the next write, or `null` when none has been chosen. */
  public readonly selectedUser: Signal<UserListItem | null> = this.selectedUserSignal.asReadonly();

  /**
   * The chosen account's existing membership of the addressed role, or `null` when none is
   * known.
   *
   * `null` covers three cases this screen treats identically, because the legacy screen did:
   * no account is chosen, the account holds no membership, and the probe could not settle the
   * question. All three show empty bounds and the 'Add User to Role' label — the state the
   * legacy grid scan produced when no row matched.
   *
   * Gated on the store's probe KEY, so an answer about another pairing can never be read as
   * this one's. Both halves of the key are compared with `===` because zero and minus one are
   * legitimate identifiers on this schema.
   */
  public readonly selectedMembership: Signal<UserRole | null> = computed(() => {
    const addressed = this.roleIdSignal();
    const chosen = this.selectedUserSignal();
    const key = this.store.probedAssignmentKey();

    if (addressed === null || chosen === null || key === null) {
      return null;
    }

    if (key.roleId !== addressed || key.userId !== chosen.userId) {
      return null;
    }

    return this.store.probedAssignment();
  });

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
   *
   * MIGRATION: the fact comes from the KEYED PROBE and never from the rows on screen. The
   * legacy scan covered every membership because its grid was unpaged; a scan of one page would
   * relabel for an account on this page and not for the same account on the next, which is a
   * different answer from the legacy one. See {@link RoleAssignmentComponent.selectedMembership}.
   */
  public readonly actionLabel: Signal<string> = computed(() => {
    const chosen = this.formStateSignal().userId;
    if (chosen === null) {
      return ROLE_ASSIGNMENT_TEXT.addUser;
    }
    const membership = this.selectedMembership();
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
      this.userLookupSignal().matches.length === 0,
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
    const previous = this.roleIdSignal();
    if (resolved === previous) {
      return;
    }
    this.roleIdSignal.set(resolved);
    this.resetForRole();
    if (resolved === null) {
      // The route stopped naming a role, so this screen is no longer looking at any listing. Said
      // before returning, because the early return below skips the reads and would otherwise leave
      // the store believing the previous role is still on screen.
      if (previous !== null) {
        this.store.closeAssignmentsView(previous);
      }
      return;
    }
    // ⚠ ANNOUNCED BEFORE THE READS, AND ON EVERY CHANGE OF ROLE. The store refuses to refresh a
    // membership listing that is not the one on screen, and it can only know which that is because
    // this screen tells it. Announcing the new role is the whole of what a reused instance owes:
    // `open` replaces `open`, and no close is due because the screen never left.
    this.store.openAssignmentsView(resolved);
    this.loadRole(resolved);
    this.loadAssignments(resolved, null);
  }

  public get roleId(): number | null {
    return this.roleIdSignal();
  }

  /*
   * THE TENANT'S PROTECTED PAIRING IS READ, NOT SUPPLIED.
   *
   * ⚠ THESE WERE THREE OPTIONAL INPUTS THAT NOTHING SUPPLIED, AND THAT IS WHY THEY ARE GONE. They
   * were declared "supplied rather than derived because the tenant's settings are not part of this
   * screen's contract", with the documented consequence that "when it is not supplied the row
   * command is offered and the server refuses the write, which is the same outcome by a different
   * route". No route in `role.routes.ts` and no parent template ever supplied one, so the guard
   * shipped permanently disarmed — and a refusal is NOT the same outcome by a different route. It
   * invites the operator to confirm the removal of the membership that makes an account part of the
   * tenant, waits, and then reports a failure the screen already had the facts to prevent.
   *
   * {@link PortalStore} is CORE state and every feature may inject it; this is not a reach into the
   * portal FEATURE. `GET /api/v1/portals/{portalId}` is declared under the same
   * `PortalAdministrator` policy this screen's own route declares, and the tenant key comes from
   * the caller's identity rather than from a route segment — this screen addresses a ROLE, not a
   * portal, and must never be able to protect one tenant's membership using another tenant's keys.
   *
   * ⚠ AN UNRESOLVED READ STILL DISARMS THE GUARD, which is the fail-safe direction here: until the
   * record arrives each key is `null`, {@link canRemove} offers the command and the server's
   * refusal governs — exactly the behaviour that shipped — rather than a capability being withheld
   * from every row for the duration of a request.
   */

  /** The tenant's designated administrator account, or `null` until its record resolves. */
  public readonly administratorUserId: Signal<number | null> = computed(() =>
    this.portals.administratorUserId(),
  );

  /** The tenant's administrator role, on the same terms as {@link administratorUserId}. */
  public readonly administratorRoleId: Signal<number | null> = computed(() =>
    this.portals.administratorRoleId(),
  );

  /**
   * The tenant's registered-users role, on the same terms as {@link administratorUserId}.
   *
   * `Roles.RoleID` is `IDENTITY(0, 1)`, so nought is a real role key; every comparison against
   * these values is an explicit equality test against `null` and never a truthiness test.
   */
  public readonly registeredRoleId: Signal<number | null> = computed(() =>
    this.portals.registeredRoleId(),
  );

  public constructor() {
    // ⚠ THE STORE IS TOLD WHEN THIS SCREEN GOES, AND THAT IS NOT COSMETIC BOOKKEEPING. Both
    // membership writes re-read the listing when they settle, and a write dispatched here can settle
    // after the operator has moved on. The store refuses that re-read only if it knows the screen has
    // gone; without this it would go on believing this role's grid is still in front of somebody,
    // re-read a listing nobody is looking at, and clear the shared failure slot underneath whichever
    // screen replaced it. Runtime validation reproduced exactly that, three times out of three, by
    // enrolling an account and returning to the role listing before the write settled.
    //
    // The role is passed so the close is idempotent and order-independent — see
    // `RoleStore.closeAssignmentsView`. Reading the signal here is safe: destruction hooks run
    // outside change detection, and this only reads.
    this.destroyRef.onDestroy(() => {
      const roleId = this.roleIdSignal();

      if (roleId !== null) {
        this.store.closeAssignmentsView(roleId);
      }
    });

    // The control event stream is the only source that reports a TOUCHED change as well as a
    // value or status change, which is what the dynamic-display gating above needs. One
    // subscription keeps the mirrored snapshot current for every derived view.
    this.form.events.pipe(takeUntilDestroyed()).subscribe(() => {
      this.formStateSignal.set(this.readFormState());
    });

    // The tenant's own record, for the protected pairing. Read from the CALLER'S identity and never
    // from a route, and idempotent in the store — several screens asking on initialisation issue one
    // request between them. Presence is tested explicitly because `Portals.PortalID` is
    // `IDENTITY(-1, 1)`, so -1 and 0 are both real tenants and a truthiness test would silently
    // skip the request for either.
    const portalId: number | undefined = this.auth.currentUser()?.portalId;

    if (portalId !== undefined) {
      this.portals.loadCurrentPortalContext(portalId);
    }

    // ACCOUNT POLICY. Which control this screen offers for choosing an account is the tenant's
    // decision, not this screen's, so the policy is read before either control is rendered -
    // reproducing `SecurityRoles.ascx.vb:L202-L221`, which resolved `Security_UsersControl` during
    // page load and rendered exactly one of the two.
    //
    // Read through the store rather than the transport, and read UNCONDITIONALLY rather than only
    // when absent: the store owns the policy and a screen cannot tell a policy read from this
    // session apart from one cached before a setting was changed elsewhere. A policy already in the
    // slice is still shown at once, so the refresh costs the operator no wait.
    this.accounts.loadMembershipSettings();

    // COMPLETE ACCOUNT LIST. Assembled only when the policy actually asks for the drop-down, which
    // is what keeps a tenant that uses the name box from paying for a walk of every account it
    // holds. The mode is derived, so this fires when the policy arrives rather than on a guess, and
    // the emptiness test is what makes it fire ONCE rather than on every unrelated notification -
    // a re-entry after a refusal would otherwise loop, because a refusal leaves the list empty.
    effect(() => {
      const mode = this.usersControlMode();

      untracked(() => {
        if (mode !== 'combo') {
          return;
        }

        if (
          this.accountChoicesSignal().length > 0 ||
          this.accountChoicesLoadingSignal() ||
          this.accountChoicesFailedSignal()
        ) {
          return;
        }

        this.loadAccountChoices();
      });
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

    // MEMBERSHIP PROBE BRIDGE. The probe answers the two questions the legacy grid scan answered,
    // and only ONE of its two callers may write the form. The marker names the account whose
    // answer is allowed to prefill, so the probe issued after a write refreshes the label and
    // leaves the boxes alone — the ordering `SecurityRoles.ascx.vb:L546` fixed.
    //
    // The answer is read through the identity-gated projection rather than from the store
    // directly, so an answer about another pairing cannot prefill from a row that is not the
    // chosen account's. A probe that failed publishes no membership, which prefills empty bounds:
    // the state the legacy screen showed for an account it found no row for.
    effect(() => {
      const awaited = this.awaitedPrefillUserId();
      const inFlight = this.store.assignmentProbeLoading();
      const membership = this.selectedMembership();

      if (awaited === null || inFlight === true) {
        return;
      }

      untracked(() => {
        this.awaitedPrefillUserId.set(null);

        const chosen = this.selectedUserSignal();

        if (chosen === null || chosen.userId !== awaited) {
          return;
        }

        this.applyProbeAnswer(membership);
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
    // Success announces NOTHING and re-reads no LISTING. The legacy handler was silent on both
    // writes — `SecurityRoles.ascx.vb:L546` rebound the grid and said nothing — and the store
    // performs that re-read itself, so a second one here would race the first. What IS re-asked
    // is the keyed probe, because a write is exactly what can change whether the chosen account
    // holds the role: the legacy rebind refreshed that fact as a side effect of rebuilding the
    // grid it scanned, and with a paged grid the fact has its own request. The re-probe carries
    // no prefill marker, so it moves the label and never the boxes.
    //
    // A refusal is the ordered path: the pairing is remembered against the row that was
    // refused, the listing is re-read, and the message is raised only once that read settles.
    effect(() => {
      const awaited = this.awaitedWrite();
      const awaitedId = this.awaitedWriteId();
      const settled = this.store.mutation();

      // ⚠ SETTLED ON THE IDENTIFIER, NOT ON THE AGGREGATE FLAG FALLING. The published result carries
      // the identifier the store handed back at dispatch, so a result belonging to any other screen's
      // write fails this comparison and is ignored — a total test that needs no knowledge of what else
      // is in flight. The operation is asserted as well, which cannot disagree with the identifier but
      // states the expectation the branches below rely on.
      if (awaited === null || settled === null || settled.id !== awaitedId) {
        return;
      }

      untracked(() => {
        this.awaitedWrite.set(null);
        this.awaitedWriteId.set(0);
        const target = this.outstandingRemoval();
        this.outstandingRemoval.set(null);

        // The failure travels ON the settled result rather than being read from the store's shared
        // slot, which a concurrent dispatch clears. `null` here means THIS write succeeded.
        const failure = settled.failure;

        if (failure === null || settled.operation !== awaited) {
          // ⚠ ON SUCCESS ONLY. A refused write changed nothing, so re-asking the probe after one would
          // spend a request to be told what the screen already knows.
          this.reprobeSelectedMembership();
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
    const administratorUserId = this.administratorUserId();
    const administratorRoleId = this.administratorRoleId();
    const registeredRoleId = this.registeredRoleId();
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
   * ## Why it walks pages instead of reading one
   *
   * ⚠ THE PREFIX MATCH SET IS UNBOUNDED AND A PAGE OF IT IS NOT THE ANSWER. This previously
   * asked for page zero alone and published its items, which made an account reachable only when
   * it happened to fall in the first page of everything sharing its prefix. In a tenant where a
   * hundred accounts begin `sm`, `smith` was findable and `smithson` was not — and nothing on
   * screen distinguished "no such account" from "further down a list you cannot see", because the
   * no-matches state and a full first page look the same to an operator who typed the whole name.
   * The account that could not be selected could not be enrolled in the role, which is the entire
   * purpose of this screen.
   *
   * So the walk requests pages in sequence and stops at the FIRST of these:
   *
   * - a page containing the EXACT name, case-insensitively. This is the legacy capability
   *   (`GetUserByName` at `SecurityRoles.ascx.vb:L480`) and once it is in hand nothing further can
   *   improve the answer, so the walk ends there however many pages remain. Ordinarily this is the
   *   FIRST page, because the request asks the listing to order by login name and the filter is a
   *   literal prefix match — see the note on the request itself.
   * - having examined as many accounts as the server says match. The server's own total, not a
   *   short page — a short page is NOT treated as the end, for the reason the store's walks record.
   * - an empty page, which can only mean the reported total will never be reached.
   * - {@link MAX_USER_LOOKUP_PAGES}, which is reported rather than hidden.
   *
   * Sequential rather than fanned out, because the exact match usually arrives on the first page
   * and every request after it would be work nobody needed; and because a prefix that matches
   * thousands of accounts would otherwise open thousands of concurrent requests at once.
   *
   * The walk examines the whole match set; {@link USER_LOOKUP_DISPLAY_LIMIT} bounds how much of it
   * becomes buttons, and {@link userLookupSummary} states both figures whenever they differ.
   *
   * @param term The name fragment the operator typed.
   */
  public onUserSearch(term: string): void {
    const query = term.trim();
    this.userLookupTermSignal.set(query);

    // The lookup this term replaces is ABANDONED before the next one starts, and also when the box
    // is emptied - a lookup nobody is waiting for is still a request the tenant pays for, and its
    // answer would otherwise repopulate a list the operator has just cleared. With a walk rather
    // than a single read this matters more than it did: an abandoned walk would otherwise keep
    // requesting pages for a term the operator has already replaced.
    this.userLookupRequest?.unsubscribe();
    this.userLookupRequest = null;

    // ⚠ THE PREVIOUS TERM'S RESULTS ARE DISCARDED HERE, NOT LEFT UNTIL THE NEW ONES ARRIVE, and the
    // same goes for the banner. Superseding a term used to leave both in place: the matches for `a`
    // stayed on screen for the whole of the lookup for `ann`, so the operator could read - and
    // CHOOSE - an account that does not match what the box says, and a refusal from the previous
    // lookup stayed visible beside a lookup that had not failed. Emptying them at dispatch means the
    // screen only ever shows matches for the term it is displaying. The register holds the matches
    // AND whether the bounded walk reached the row being looked for, so emptying it empties both.
    this.userLookupSignal.set(NO_USER_LOOKUP);
    this.problemSignal.set(null);

    // The generation moves on every dispatch, INCLUDING the empty-box path below, so an answer
    // already scheduled to commit cannot repopulate a list the operator has just cleared.
    const generation = this.userLookupGeneration.begin();

    if (query.length === 0) {
      this.userLookupLoadingSignal.set(false);
      this.userLookupSignal.set(NO_USER_LOOKUP);
      return;
    }

    this.userLookupLoadingSignal.set(true);
    this.userLookupRequest = this.walkUserLookup(query)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome: UserLookupOutcome): void => {
          // Fenced as well as cancelled. See {@link RoleAssignmentComponent.userLookupGeneration}:
          // the handle is released on paths that are not "a newer lookup started", and a response
          // already scheduled to commit is not recalled by any of them.
          if (this.userLookupGeneration.isCurrent(generation) === false) {
            return;
          }

          this.userLookupLoadingSignal.set(false);
          this.userLookupSignal.set(outcome);
        },
        error: (error: unknown): void => {
          if (this.userLookupGeneration.isCurrent(generation) === false) {
            return;
          }

          this.userLookupLoadingSignal.set(false);
          this.userLookupSignal.set(NO_USER_LOOKUP);
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
   * MIGRATION: the membership is ASKED FOR rather than looked up in the rows on screen, because
   * the grid holds one page. The marker is set before the command so the answer this call
   * produces is the one allowed to write the boxes; see
   * {@link RoleAssignmentComponent.awaitedPrefillUserId}.
   *
   * @param user The account the operator chose from the lookup.
   */
  public selectUser(user: UserListItem): void {
    this.selectedUserSignal.set(user);
    this.form.controls.userId.setValue(user.userId);
    this.formStateSignal.set(this.readFormState());

    const roleId = this.roleIdSignal();

    if (roleId === null) {
      return;
    }

    this.awaitedPrefillUserId.set(user.userId);
    this.store.probeAssignment(roleId, user.userId, user.username);
  }

  /**
   * Chooses an account from the drop-down the tenant's account policy asked for.
   *
   * MIGRATION: this is `cboUsers`, read at `SecurityRoles.ascx.vb:L106-L109`. The legacy control
   * carried `autopostback="True"` (`securityroles.ascx:L26`) so choosing an entry round-tripped the
   * whole page to reach the prefill at `:L273-L303`; the prefill happens here without one.
   *
   * The raw value is matched rather than parsed, so no numeric coercion stands between the entry
   * the operator chose and the account it denotes — the empty prompt value simply matches nothing
   * and clears the choice, which is what `<None Specified>` meant.
   *
   * The event is narrowed here rather than in the template. A template that reached through the
   * event target would need the type-check escape hatch to do it, which would switch strict template
   * checking off for that expression; narrowing in TypeScript keeps the check on and makes a
   * non-select target return rather than throw.
   *
   * @param event The change event the drop-down raised.
   */
  public selectAccountChoice(event: Event): void {
    const target: EventTarget | null = event.target;

    if (!(target instanceof HTMLSelectElement)) {
      return;
    }

    const chosen = this.accountChoicesSignal().find(
      (candidate) => String(candidate.userId) === target.value,
    );

    if (chosen === undefined) {
      this.clearSelectedUser();

      return;
    }

    this.selectUser(chosen);
  }

  /** Forgets the chosen account and clears the bounds that were prefilled from it. */
  public clearSelectedUser(): void {
    this.selectedUserSignal.set(null);
    this.awaitedPrefillUserId.set(null);
    this.store.clearProbedAssignment();
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
    // The identifier is captured from the command's own return value, so what this screen waits on is
    // the very write it just dispatched and not merely "a write of this kind".
    this.awaitedWriteId.set(
      this.store.assignUser(roleId, this.buildAssignmentRequest(roleId, userId)),
    );
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
    this.awaitedWriteId.set(this.store.removeAssignment(roleId, target.userId));
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
   * listing takes one, so there is no base to convert between, and the pager only ever emits an
   * index inside the range it was given. The store's read cancels whichever page read was in
   * flight, so clicking through the pager cannot leave an earlier page's answer to land on top
   * of a later one.
   *
   * The keyed probe is NOT re-asked: whether the chosen account holds the role does not depend
   * on which page is on screen, which is the whole reason the fact has its own request.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  public onPageChange(pageIndex: number): void {
    if (this.roleIdSignal() === null || pageIndex === this.pageIndex()) {
      return;
    }

    this.store.setAssignmentsPage(pageIndex);
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
   * Walks the account listing for one name until the exact account is found or the whole match
   * set has been examined.
   *
   * The termination rules and the reasoning behind them are stated on
   * {@link RoleAssignmentComponent.onUserSearch}; this is their implementation.
   *
   * ⚠ THE PAGES ARE JOINED IN PLACE inside the projector rather than by re-spreading an
   * accumulator per page. Copying the whole accumulation on every page makes the join quadratic
   * in the number of pages, which at this walk's ceiling would be millions of element copies for a
   * result nobody would wait for. The array is never observable while it is being built: the
   * outcome is composed once, after the walk completes.
   *
   * @param term The name the operator typed, already trimmed and known non-empty.
   * @returns The completed lookup, emitted exactly once.
   */
  private walkUserLookup(term: string): Observable<UserLookupOutcome> {
    const wanted = term.toLocaleLowerCase();
    const collected: UserListItem[] = [];
    let examined = 0;
    let reportedTotal = 0;
    let completion: UserLookupCompletion = 'complete';

    // MIGRATION: the comparison is CASE-INSENSITIVE because the legacy one was. `GetUserByName`
    // resolved through a SQL Server lookup under the database's own collation, which for a default
    // installation does not distinguish case, so an operator who typed 'Admin' found 'admin'. A
    // case-sensitive test here would refuse a name the legacy screen accepted.
    const isExact = (candidate: UserListItem): boolean =>
      candidate.username.toLocaleLowerCase() === wanted;

    const requestPage = (pageIndex: number): Observable<PagedResponse<UserListItem>> => {
      const request: UserListQuery = {
        pageIndex,
        pageSize: USER_LOOKUP_PAGE_SIZE,
        userName: term,
        // ⚠ THE ORDER IS WHAT MAKES THE COMMON CASE ONE REQUEST, and it is a guarantee rather
        // than a heuristic. The filter is a literal PREFIX match, so every account this listing
        // returns has `term` at the start of its login name; ascending by that name therefore puts
        // the SHORTEST match first, and the shortest possible match is `term` itself. So when an
        // account with exactly this name exists it is the first row of the first page, and the walk
        // below stops there.
        //
        // Without it the listing orders by display name — which bears no relation to the login name
        // being searched for — and an exact match could sit on any page of the set. That is the
        // defect this walk exists to survive, and the ordering is what keeps surviving it cheap.
        sortBy: 'Username',
        sortDir: 'Ascending',
      };

      return this.userService.list(request);
    };

    return requestPage(0).pipe(
      expand((response: PagedResponse<UserListItem>, index: number) => {
        // `UserService.list` already answers a decoded page; this re-reads its framing rather than
        // unwrapping an envelope, which is what makes the same projector safe for every page.
        const page: PagedResult<UserListItem> = toPagedResult<UserListItem>(response);

        collected.push(...page.items);
        examined += page.items.length;
        reportedTotal = page.meta.totalCount;

        // The one ending that beats every other: the account the operator named is in hand, so no
        // remaining page can improve the answer.
        if (page.items.some(isExact)) {
          completion = 'exact';

          return EMPTY;
        }

        // The server's own total is what ends the walk. A SHORT PAGE IS NOT AN ENDING - a page
        // shorter than requested is what a filtered listing produces mid-set, and treating it as
        // the end is precisely how a truncated answer passes for a complete one.
        if (examined >= page.meta.totalCount) {
          completion = 'complete';

          return EMPTY;
        }

        // An empty page cannot be followed by a fuller one, so the reported total will never be
        // reached. There is nothing further to examine and nothing to refuse: what was gathered is
        // every account the server was willing to supply for this name.
        if (page.items.length === 0) {
          completion = 'complete';

          return EMPTY;
        }

        // `index` counts emissions of this walk, which began at page 0, so the page just handled is
        // `index` and the next one is `index + 1`.
        if (index + 1 >= MAX_USER_LOOKUP_PAGES) {
          completion = 'curtailed';

          return EMPTY;
        }

        return requestPage(index + 1);
      }),
      // Consumes every page and emits once at completion. The pages themselves are not the answer -
      // the closure above holds it - so the emission is counted and discarded rather than retained.
      count(),
      map(
        (): UserLookupOutcome => ({
          matches: offerableMatches(collected, isExact),
          examined,
          reportedTotal,
          completion,
        }),
      ),
    );
  }

  /**
   * Assembles the complete account list the tenant's drop-down policy needs, or fails.
   *
   * The same termination rules as {@link walkUserLookup} minus the exact-match ending, which has
   * no meaning when the whole list is the answer, and with the ceiling turned into a REFUSAL rather
   * than a reported stop. That asymmetry is the point: a lookup that stops early still answers a
   * question the operator asked, whereas a drop-down claiming to hold every account while holding
   * some of them hides the accounts it dropped. {@link accountChoicesFailedSignal} records the
   * refusal and the screen offers the name box instead.
   *
   * @returns Every account in the tenant, emitted once.
   */
  private walkAllAccounts(): Observable<readonly UserListItem[]> {
    const collected: UserListItem[] = [];
    let gathered = 0;

    // NO SORT IS ASKED FOR, and that is the right request rather than an omission. The listing's own
    // default orders by DISPLAY name, which is what the drop-down's entries are captioned with, so
    // the entries read in the order they are shown. Asking for the login-name order the lookup uses
    // would sort the list by a value the operator cannot see.
    const requestPage = (pageIndex: number): Observable<PagedResponse<UserListItem>> =>
      this.userService.list({ pageIndex, pageSize: USER_LOOKUP_PAGE_SIZE });

    return requestPage(0).pipe(
      expand((response: PagedResponse<UserListItem>, index: number) => {
        const page: PagedResult<UserListItem> = toPagedResult<UserListItem>(response);

        collected.push(...page.items);
        gathered += page.items.length;

        if (gathered >= page.meta.totalCount) {
          return EMPTY;
        }

        if (page.items.length === 0) {
          return throwError(
            () =>
              new Error(
                'The accounts of this site could not be listed completely: the server reports ' +
                  `${page.meta.totalCount} accounts but supplied ${gathered} and then answered ` +
                  'with an empty page.',
              ),
          );
        }

        if (index + 1 >= MAX_ACCOUNT_CHOICE_PAGES) {
          return throwError(
            () =>
              new Error(
                'The accounts of this site could not be listed completely: the server reports ' +
                  `${page.meta.totalCount} accounts and stopped supplying them after ` +
                  `${MAX_ACCOUNT_CHOICE_PAGES} pages (${gathered} gathered).`,
              ),
          );
        }

        return requestPage(index + 1);
      }),
      count(),
      map((): readonly UserListItem[] => collected),
    );
  }

  /**
   * Assembles the complete account list for the drop-down, abandoning any earlier attempt.
   *
   * A refusal is reported TWICE deliberately, and the two say different things: the banner names
   * what went wrong, and the note beside the name box says what is offered in its place. Reporting
   * only the second would leave a server fault looking like a policy choice.
   */
  private loadAccountChoices(): void {
    this.accountChoicesRequest?.unsubscribe();
    this.accountChoicesLoadingSignal.set(true);
    this.accountChoicesFailedSignal.set(false);

    this.accountChoicesRequest = this.walkAllAccounts()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (accounts: readonly UserListItem[]): void => {
          this.accountChoicesLoadingSignal.set(false);
          this.accountChoicesSignal.set(accounts);
        },
        error: (error: unknown): void => {
          this.accountChoicesLoadingSignal.set(false);
          this.accountChoicesSignal.set([]);
          this.accountChoicesFailedSignal.set(true);
          this.raise(this.failureNotice(error));
        },
      });
  }

  /**
   * Returns the screen to its initial state, which a change of addressed role requires.
   *
   * The role and the rows are NOT cleared here and do not need to be: both are derived from
   * the store gated on the addressed key, so they empty themselves the moment the key changes.
   * What is cleared is everything this screen owns outright — the outstanding markers, the
   * banner, the lookup, the confirmation and the form.
   *
   * THE ACCOUNT LIST AND THE ACCOUNT POLICY ARE DELIBERATELY KEPT. Both are tenant-scoped rather
   * than role-scoped — the same accounts are selectable whichever role is addressed — so clearing
   * them would re-walk every account in the site each time the operator moved between roles, for
   * an identical answer.
   *
   * The lookup walk in flight is ABANDONED rather than merely ignored. Clearing the matches without
   * ending the walk would leave it requesting pages for a role nobody is looking at, and its answer
   * would land in a field belonging to a different role.
   */
  private resetForRole(): void {
    this.awaitedRoleKey.set(null);
    this.awaitedWrite.set(null);
    this.awaitedWriteId.set(0);
    this.awaitedPrefillUserId.set(null);
    this.outstandingRemoval.set(null);
    this.deferredNotice.set(null);
    this.assignmentsSettling.set(false);
    this.problemSignal.set(null);

    // ⚠ THE LOOKUP IS RELEASED, NOT MERELY BLANKED, AND ITS GENERATION IS INVALIDATED.
    //
    // Clearing the signals without doing either was the defect. Because the route reuses one
    // component instance, moving from role A to role B runs this method while a lookup started under
    // role A may still be outstanding — and nothing here stopped it. The sequence was:
    //
    //     role A: operator types 'ann'                -> lookup A dispatched
    //     operator navigates to role B                -> resetForRole blanks the matches
    //     lookup A lands                              -> role A's matches rendered under role B
    //
    // The matches are then a live selection list: choosing one prefills the enrolment form from that
    // account's membership of a DIFFERENT role, and submitting enrols it into role B with bounds read
    // from role A. Both are done because they answer different questions — the handle stops the
    // delivery, and the generation refuses a commit already scheduled, which no cancellation can
    // recall.
    this.userLookupRequest?.unsubscribe();
    this.userLookupRequest = null;
    this.userLookupGeneration.invalidate();

    this.userLookupSignal.set(NO_USER_LOOKUP);
    this.userLookupLoadingSignal.set(false);
    this.userLookupTermSignal.set('');
    this.selectedUserSignal.set(null);
    this.pendingRemovalSignal.set(null);
    this.protectedPairingsSignal.set(new Set<string>());
    // The probe's answer is about a pairing that included the role being left, so it is released
    // rather than left to be gated out - and releasing it also abandons a probe still in flight.
    this.store.clearProbedAssignment();
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
   * Asks the store for ONE PAGE of the addressed role's memberships, deferring one message
   * until it has settled.
   *
   * MIGRATION: the grid IS paged and the legacy one was not. `securityroles.ascx:L56` declares
   * no `AllowPaging` and no pager style, and a first port reproduced that by reading every page
   * the server reported and rendering the union — an unbounded read of a listing that counts and
   * windows per request. One page is read instead and the shared pager reaches the rest, so
   * every membership is still addressable and no Delete command is out of reach. The store keeps
   * the coordinate for a role it is already showing and returns to the first page on a change of
   * role, so a refresh after a write leaves the operator on the window they were looking at.
   *
   * The notice is recorded before the command is issued, so the read that raises it is always
   * the read this call started. See {@link RoleAssignmentComponent.deferredNotice}.
   *
   * @param roleId The addressed role.
   * @param notice A message to raise once the read has settled, or `null`.
   */
  private loadAssignments(roleId: number, notice: DeferredNotice | null): void {
    this.deferredNotice.set(notice);
    this.store.loadAssignments(roleId);
  }

  /**
   * Re-asks whether the chosen account holds the role, after a write may have changed it.
   *
   * MIGRATION: this refreshes the FACT and deliberately leaves the two date boxes alone — no
   * prefill marker is set. `SecurityRoles.ascx.vb:L546` rebound the grid after a write and
   * touched nothing else, so an operator who left the expiry empty and let the server derive one
   * saw an empty box afterwards, not the derived value.
   *
   * Does nothing when there is no pairing to ask about, which is the ordinary case for a removal
   * the operator performed without having chosen an account.
   */
  private reprobeSelectedMembership(): void {
    const roleId = this.roleIdSignal();
    const chosen = this.selectedUserSignal();

    if (roleId === null || chosen === null) {
      return;
    }

    this.store.probeAssignment(roleId, chosen.userId, chosen.username);
  }

  /**
   * Prefills the two bounds from the probe's answer.
   *
   * Passing `null` is how "no membership is known" is expressed, and it produces exactly the
   * state the legacy screen showed for an account with no row: both boxes empty and neither
   * marked as visited, so no dynamic validator has anything to say about a value the operator
   * never typed.
   *
   * @param membership The chosen account's membership of the addressed role, or `null`.
   */
  private applyProbeAnswer(membership: UserRole | null): void {
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
    const administratorUserId = this.administratorUserId();
    const administratorRoleId = this.administratorRoleId();
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
