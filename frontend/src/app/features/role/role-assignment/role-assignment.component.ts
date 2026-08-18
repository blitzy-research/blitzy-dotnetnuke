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

import {
  EMPTY,
  count,
  expand,
  map,
  merge,
  throwError,
  type Observable,
  type Subscription,
} from 'rxjs';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import { RouterLink } from '@angular/router';

import {
  MAX_PAGE_SIZE,
  toPagedResult,
  type ApiMeta,
  type PagedResponse,
  type PagedResult,
  type SortDirection,
} from '../../../core/models/paged-result.model';
import { isProblemDetails, type ProblemDetails } from '../../../core/models/problem-details.model';
import type { Role, RoleAssignmentRequest, UserRole } from '../../../core/models/role.model';
import type { UserChoice } from '../../../core/models/user.model';
import type { PagedRequestParams } from '../../../core/utils/http-params.util';
import {
  NotificationService,
  type NotificationSeverity,
} from '../../../core/services/notification.service';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
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
import { AbsentValueComponent } from '../../../shared/components/absent-value/absent-value.component';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  BAD_INPUT_ERROR,
  NativeDateValidityDirective,
  OUT_OF_RANGE_ERROR,
} from '../../../shared/directives/native-date-validity.directive';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
  type DataTableSortChange,
} from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
import { DateDisplayPipe, parseDisplayInstant } from '../../../shared/pipes/date-display.pipe';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

/**
 * Every user-facing string on this screen, taken from the resource VALUE rather than from a markup
 * attribute. The resource value is authoritative because the markup attributes are demonstrably
 * unreliable: on the sibling role editor two validator messages are exactly swapped relative to their own
 * operators while the resource file has them the right way round.
 */
/** One entry of the account drop-down, with its label already composed. */
export interface AccountChoiceEntry {
  /** The account the entry denotes. */
  readonly userId: number;

  /** The text the option renders. */
  readonly label: string;

  /** Whether the addressed role's membership rows in hand already include this account. */
  readonly alreadyInRole: boolean;
}

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
   * The sentence stating why the notification choice cannot be made. `SecurityRoles.ascx.vb:L542` and
   * `:L569` passed this choice to a routine that mailed the account holder, and the assignment contract
   * still carries the member — so the member is transmitted rather than dropped.
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

  /**
   * Why no membership of this role offers a removal command — R-M24. ⚠ WITHOUT THIS SENTENCE THE WITHHELD
   * COLUMN IS UNEXPLAINED. `RoleController.vb:L745` forbids removal from the registered-users role for
   * every account, so the command column is not emitted at all on that role; an operator who has removed
   * accounts from every other role then finds the affordance simply absent, with nothing stating that the
   * absence is a rule rather than a fault.
   */
  removalUnavailable:
    'Accounts cannot be removed from this role. Membership of the registered-users role is what ' +
    'makes an account part of this site, so it is withdrawn by deleting the account rather than by ' +
    'removing the role.',

  /** Qualifier beside an expiry bound that has already passed — R-M24. */
  expired: 'Expired',

  /**
   * Qualifier beside an effective bound that has not yet arrived — R-M24. FULLY AUTHORED: no legacy
   * screen tested an effective bound against the clock at all, so there is no wording anywhere in the
   * resource set to recover.
   */
  pending: 'Pending',

  /**
   * Qualifier beside an expiry bound on a membership that is IN FORCE today — R6.
   *
   * ⚠ STATED RATHER THAN LEFT TO BE INFERRED FROM AN ABSENCE. With only the two exceptional states
   * qualified, a membership in force carried no mark at all, so "in force" and "this console did not judge
   * this row" were the same rendering — and a reader using the accessibility tree received nothing
   * whatsoever for the ordinary case. FULLY AUTHORED, like its two siblings: no legacy screen compared
   * either bound against the clock, so there is no wording in the resource set to recover.
   */
  current: 'Active',

  confirmRemoval: 'Are You Sure You Wish To Delete This Item?',

  /** Global `Cancel.Action`, the screen's single module action at `:L634`. */
  cancel: 'Cancel',

  /** `valEffectiveDate.Text`. */
  invalidEffectiveDate: '<br> Invalid effective date',

  /** `valExpiryDate.Text`. */
  invalidExpiryDate: '<br>Invalid expiry date',

  /** `valDates.Text`. */
  expiryNotAfterEffective: '<br>Expiry Date must be Greater than Effective Date',

  /**
   * Stated at the action when no account has been chosen. NET-NEW WORDING: the legacy screen had no
   * equivalent, because it refused the post server-side rather than gating the button, so there is nothing to
   * reproduce and this names the condition in the screen's own vocabulary.
   */
  chooseAccountFirst: 'Choose an account before adding it to this role.',

  /** Stated at the action when a date bound cannot be read. Net-new wording, for the same reason. */
  correctTheDatesFirst: 'Correct the highlighted dates before saving.',

  /** `RoleRemoveError.Text`, used when the server does not itself supply the wording. */
  removalRefused: 'You Can Not Remove The Portal Administrator Or The Registered Users Role',

  /**
   * The legacy lookup failed SILENTLY. `SecurityRoles.ascx.vb:L476-L488` looked the account up by name
   * and, on no match, simply blanked the box at `:L484` with no message at all.
   */
  noMatchingUsers:
    "No account's login name starts with that. The search matches the beginning of the login " +
    'name, not the display name.',

  /**
   * Names the list of offered matches. Without it the list is announced by its role and length alone -
   * "list, three items" - which says how many things there are and nothing about what they are, and
   * leaves an operator to infer the purpose from the contents of the first entry.
   */
  matchesLabel: 'Matching accounts',

  /**
   * Standing context under the offered matches, shown only when MORE accounts match than are being
   * offered. `{0}` is how many are offered, `{1}` the server's own count of the match set.
   */
  lookupPartial:
    'Showing {0} of {1} matching accounts. Type more of the name to narrow the list.',

  /** The same context for a lookup that DID find the exact name, on the same two substitutions. */
  lookupExact: 'The exact match is offered first. Showing {0} of {1} matching accounts.',

  /** Shown when the walk stopped at its own page ceiling without finding an exact match. */
  lookupCurtailed:
    'The search examined {0} of {1} matching accounts without finding an exact match and stopped ' +
    'there. Type more of the name to narrow it.',

  /** The label on the drop-down the tenant's account policy can ask for instead of the name box. */
  userChoiceLabel: 'User Name',

  /**
   * `plUsers.HelpText` belongs to the name box and says 'Enter The User Name and click Validate to
   * confirm', which is untrue of the drop-down the other policy value selects. The legacy screen carried
   * one help string for both controls because both shared one label cell; this states what the drop-down
   * actually does.
   */
  userChoiceHelp: 'Choose an account from every account in this site.',

  /** The unselected entry of that drop-down. */
  userChoicePrompt: '<None Specified>',

  accountAlreadyInRoleSuffix: ' — already in this role',

  /**
   * Confirmation for a membership WRITE. `{0}` is the account, `{1}` is the role. MIGRATION - net-new at
   * the SUCCESS band, and the omission it closes was measured.
   */
  assignmentAdded: '{0} was added to the {1} role.',

  /** Confirmation for amending an existing membership's bounds. */
  assignmentUpdated: "{0}'s membership of the {1} role was updated.",

  /** Confirmation for ending a membership. */
  assignmentRemoved: '{0} was removed from the {1} role.',

  /** Stands in for an account name the screen does not hold when a write settles. */
  unnamedAccount: 'The account',

  /** Stands in for the role's name before the role read has settled. Same reasoning as above. */
  unnamedRole: 'selected',

  /** Shown while the tenant's account policy is being read, before either control is offered. */
  accountPolicyLoading: 'Reading how this site asks you to choose an account…',

  /**
   * Shown while a lookup is walking pages. ⚠ THE LOOKUP'S OWN, distinct from {@link
   * accountChoicesLoading}.
   */
  lookupLoading: 'Searching for matching accounts…',

  /**
   * Shown when the account policy could not be read, so the name box is offered without knowing which
   * control the tenant prefers.
   */
  accountPolicyUnavailable:
    "This site's preferred account selector could not be read, so the name box is offered.",

  /**
   * Shown when the tenant asked for the drop-down but the complete account list could not be assembled,
   * so the name box is offered in its place. The name box needs no complete list, which is why this
   * degrades rather than fails: the capability the operator loses is browsing, and the capability they
   * keep — naming the account — is the one the legacy screen's own help text described.
   */
  accountChoicesUnavailable:
    'Every account in this site could not be listed, so the name box is offered instead.',

  /** Shown while the complete account list for the drop-down is being assembled. */
  accountChoicesLoading: 'Listing every account in this site…',

  /**
   * Shown when the account policy could not be read AND the site holds more accounts than the legacy's
   * own enumeration threshold, so the name box is offered by that rule rather than by failure. `{0}` is
   * the threshold.
   */
  accountPolicyDefaultedBySize:
    "This site's preferred account selector could not be read, and the site holds more than {0} " +
    'accounts, so the name box is offered rather than a list of every one of them.',

  /** Shown when the tenant asked for the drop-down and the site holds no accounts to offer. */
  accountChoicesEmpty: 'This site holds no accounts to choose from.',

  /**
   * U21 - THE ANNOTATION CANNOT BE COMPLETE AND THE SCREEN NOW SAYS SO. The membership listing is paged,
   * so only the memberships on the page in hand can be marked; an operator scanning the list was
   * misinformed by omission about every member beyond it. The reassurance is not decoration: choosing an
   * account this role already holds IS detected before anything is written, by the server probe the
   * confirmation step performs, so the incomplete marking cannot cause a duplicate assignment.
   */
  accountChoicesAnnotationPartial:
    'Accounts already in this role are marked only where the membership list below has been read this far,'
    + ' so an existing member may appear unmarked. Choosing one is still detected before anything is saved.',

  /** Shown when the route did not carry a usable role identifier. */
  roleUnresolved: 'No security role was addressed, so no memberships can be shown.',
} as const);

/**
 * Document identifiers for the three labelled controls, so the shared field wrapper's `for` input and the
 * control's own `id` attribute cannot drift apart.
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
 * Column keys for the membership grid. A key is a UNIQUE IDENTITY and is deliberately not the display
 * label: the shared grid rejects a duplicate key outright because the key tracks both the heading and
 * every cell in its column.
 */
/**
 * The endpoint field the account column is ordered by. ⚠ DELIBERATELY NOT THE COLUMN KEY. The key is
 * `userName`, which the endpoint's allowlist would accept as `Username` - the account's sign-in name, a
 * DIFFERENT field from the display name this column's cell actually renders.
 */
const ACCOUNT_SORT_FIELD = 'DisplayName';

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

export interface RoleAssignmentFormModel {
  /**
   * The chosen account, or `null` when none has been chosen yet. `null` here means "nothing chosen",
   * which is a fact about the form and never a stand-in for an identifier.
   */
  userId: FormControl<number | null>;

  /**
   * The effective bound as the `yyyy-MM-dd` value of a native date control, or `''`. `''` is the empty
   * state and it is converted to `null` on the way out.
   */
  effectiveDate: FormControl<string>;

  /** The expiry bound, on the same terms as {@link RoleAssignmentFormModel.effectiveDate}. */
  expiryDate: FormControl<string>;

  /**
   * Whether the account should be notified, defaulting to checked. retained because the assignment
   * contract carries `notifyUser`.
   */
  notify: FormControl<boolean>;
}

/** The form's state, mirrored into a signal so the derived views below can be `computed`. */
interface RoleAssignmentFormState {
  /** The chosen account, or `null`. */
  readonly userId: number | null;

  /** The effective bound as typed. */
  readonly effectiveDate: string;

  /** The expiry bound as typed. */
  readonly expiryDate: string;

  /** Whether the group as a whole is valid. */
  readonly valid: boolean;

  /**
   * Whether the effective bound is holding something it cannot pass to the store — for ANY of the three
   * reasons a date field has, not only the parse failure this screen authors itself. ⚠ READING ONLY THIS
   * SCREEN'S OWN KEY LEFT THE OTHER TWO STATES SILENT. See {@link DATE_UNUSABLE_ERRORS} for the measured
   * defect.
   */
  readonly effectiveDateInvalid: boolean;

  /** Whether the effective bound has been visited. */
  readonly effectiveDateTouched: boolean;

  /**
   * Whether the effective bound's failure is one the BROWSER reported rather than one this screen derived
   * from the value — an unparseable entry, or a complete date outside the storable range.
   */
  readonly effectiveDateNativelyUnusable: boolean;

  /**
   * Whether the expiry bound is holding something it cannot pass to the store, for any of the three
   * reasons.
   */
  readonly expiryDateInvalid: boolean;

  /** Whether the expiry bound has been visited. */
  readonly expiryDateTouched: boolean;

  /** Mirrors {@link effectiveDateNativelyUnusable} for the expiry bound. */
  readonly expiryDateNativelyUnusable: boolean;

  /** Whether the group-level ordering rule is failing. */
  readonly datesOutOfOrder: boolean;
}

/** A message to raise once a re-read has settled, never before it. */
interface DeferredNotice {
  /** How the message should be presented. */
  readonly severity: NotificationSeverity;

  /** The message itself, already plain text. */
  readonly message: string;

  /**
   * The support reference to quote, or `null` when this outcome has none. ⚠ REQUIRED, NOT OPTIONAL, SO
   * EVERY PRODUCER HAS TO STATE ITS ANSWER. A server refusal carries a correlation identifier and a
   * success does not, and the difference is a property of the outcome rather than of the call site - so
   * leaving the member optional would let a refusal-shaped producer inherit a success's silence by simply
   * not mentioning it, which is precisely how the duplicate-name refusal on the sibling role form came to
   * render a message with no reference beside a banner that had one.
   */
  readonly reference: string | null;
}

/** Validation key raised when a date box holds something that is not a calendar date. */
const INVALID_DATE_ERROR = 'invalidDate';

/**
 * Every reason a date box on this screen can be holding something the store cannot accept. ⚠ THIS LIST
 * EXISTS BECAUSE READING ONE KEY WAS NOT ENOUGH, AND THE GAP WAS INVISIBLE. The snapshot below used to
 * test `hasError(INVALID_DATE_ERROR)` alone — this screen's own parse check, which reads the control's
 * VALUE. The other two states never reach the value: an unparseable entry is blanked by the browser
 * before it is committed, and a complete-but-unstorable date such as `0001-01-01` parses perfectly well.
 */
const DATE_UNUSABLE_ERRORS: readonly string[] = [
  INVALID_DATE_ERROR,
  BAD_INPUT_ERROR,
  OUT_OF_RANGE_ERROR,
];

/**
 * The subset of {@link DATE_UNUSABLE_ERRORS} the BROWSER reports, as opposed to the one this screen
 * derives from the control's value. These two are not gated on the field having been visited, and that is
 * a deliberate departure from how this screen gates its own parse message.
 */
const NATIVE_DATE_UNUSABLE_ERRORS: readonly string[] = [BAD_INPUT_ERROR, OUT_OF_RANGE_ERROR];

/**
 * Whether a control is currently reporting any of the supplied validation keys.
 *
 * @param control The control to interrogate.
 * @param keys The keys to test for.
 * @returns `true` when at least one of the keys is present on the control.
 */
function hasAnyError(control: AbstractControl, keys: readonly string[]): boolean {
  return keys.some((key: string) => control.hasError(key));
}

/** Validation key raised when the expiry bound is not strictly later than the effective one. */
const DATE_ORDER_ERROR = 'expiryNotAfterEffective';

/** The earliest date either bound can hold, as an `input[type=date]` `min` value. */
const EARLIEST_STORABLE_DATE = '1753-01-01';

/** The latest date either bound can hold. */
const LATEST_STORABLE_DATE = '9999-12-31';

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
 * How many accounts one request of the lookup walk asks for. ⚠ THE SERVER'S OWN MAXIMUM, and it replaced
 * the shared DEFAULT of ten.
 */
const USER_LOOKUP_PAGE_SIZE = MAX_PAGE_SIZE;

/**
 * How many matched accounts are offered as choices at once. A rendering bound rather than a search bound,
 * and the distinction is what makes it honest.
 */
const USER_LOOKUP_DISPLAY_LIMIT = MAX_PAGE_SIZE;

/**
 * The hard ceiling on how many pages one account lookup will request. ⚠ DELIBERATELY MODEST, BECAUSE THE
 * WALK IS A GUARANTEE RATHER THAN THE MECHANISM. The lookup asks the listing to order by login name,
 * which — the filter being a literal prefix match — puts an exact match first among every account it can
 * return, so the ordinary answer arrives in ONE request.
 */
const MAX_USER_LOOKUP_PAGES = 20;

/**
 * The account count above which the legacy defaulted to the NAME BOX rather than the drop-down, when the
 * tenant had never chosen either. ⚠ THIS IS THE LEGACY'S OWN RULE, MEASURED, NOT A LIMIT INVENTED HERE.
 * `Library/Components/Users/UserModuleBase.vb:L178-L183` is the whole of it: when `Security_UsersControl`
 * was absent from the tenant's settings, the framework asked
 * `UserController.GetUserCountByPortal(portalId)` and defaulted to `UsersControl.TextBox` above one
 * thousand accounts and to `UsersControl.Combo` at or below it - and then WROTE THAT DEFAULT BACK as the
 * tenant's setting.
 */
const LEGACY_ACCOUNT_LISTING_CEILING = 1000;

const MAX_ACCOUNT_CHOICE_PAGES = LEGACY_ACCOUNT_LISTING_CEILING / USER_LOOKUP_PAGE_SIZE;

/** The page size used for the count probe. */
const ACCOUNT_COUNT_PROBE_PAGE_SIZE = 1;

/**
 * How the tenant's account policy says this screen should let an operator pick an account. this is
 * `Security_UsersControl`, read at `SecurityRoles.ascx.vb:L133-L136` through
 * `UserModuleBase.GetSetting(PortalId, "Security_UsersControl")` and acted on at `:L202-L221`, where
 * `UsersControl.Combo` bound `cboUsers` to the tenant's whole account listing and hid the text box, and
 * the other value did the reverse.
 */
const USERS_CONTROL = Object.freeze({
  /** A drop-down list of every account in the tenant. `UsersControl.Combo`. */
  combo: 0,

  /** A name box with a lookup. */
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

/** What one completed account lookup found. */
interface UserLookupOutcome {
  /** The accounts offered as choices — already limited to {@link USER_LOOKUP_DISPLAY_LIMIT}. */
  readonly matches: readonly UserChoice[];

  /** How many accounts the walk examined, which is at least `matches.length`. */
  readonly examined: number;

  /** The server's own count of the whole match set. */
  readonly reportedTotal: number;

  /** Why the walk stopped. */
  readonly completion: UserLookupCompletion;
}

/** An account lookup that found nothing, for the cleared and failed states. */
const NO_USER_LOOKUP: UserLookupOutcome = Object.freeze({
  matches: Object.freeze([]) as readonly UserChoice[],
  examined: 0,
  reportedTotal: 0,
  completion: 'complete',
});

/**
 * Chooses which of a lookup's matches become buttons, keeping the exact one whatever else goes. The head
 * of the set is offered, because the listing returns matches in the server's own order and the nearest
 * prefixes come first.
 *
 * @param collected Every account the walk examined, in the order the server supplied them.
 * @param isExact Whether one account carries exactly the name that was searched for.
 * @returns The accounts to offer, never more than {@link USER_LOOKUP_DISPLAY_LIMIT}.
 */
function offerableMatches(
  collected: readonly UserChoice[],
  isExact: (candidate: UserChoice) => boolean,
): readonly UserChoice[] {
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

/** The paging facts to report while no answer for the addressed role is in hand. */
const UNRESOLVED_PAGE_META: ApiMeta = Object.freeze({
  totalCount: 0,
  pageIndex: 0,
  pageSize: 0,
  totalPages: 0,
});

/**
 * The failure code a refused removal carries. The API answers a protected membership and a caller who
 * does not administer the tenant with the SAME status — 403 — and distinguishes them on the problem type
 * alone, so this code is what separates the legacy refusal wording from a permission failure.
 */
const PROTECTED_ASSIGNMENT_CODE = 'role_assignment.protected';

/** The membership write this screen is waiting on, named by the store operation that reports it. */
type AwaitedAssignmentWrite = Extract<RoleStoreOperation, 'assignUser' | 'removeAssignment'>;

/**
 * Whether a year, month and day name a day that exists. Round-tripped through a UTC probe so that, for
 * example, the thirty-first of February is rejected rather than silently rolled forward into March.
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
 * @param text The raw value of a date box.
 * @returns Milliseconds since the epoch at UTC midnight, or `null` when the value is empty or is not a
 * calendar date.
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
 * Converts a bound as the API publishes it into the value a native date control accepts. The date part is
 * taken as written and read as a UTC calendar date, which is the same reading the shared date pipe
 * applies when it renders the bound in the grid, so the box and the grid can never disagree about which
 * day a membership starts.
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
 * The cross-field ordering rule, attached to the group and surfaced on the expiry field. Reproduces the
 * third validator at `securityroles.ascx:L47`: `type="Date"`, `operator="GreaterThan"`, validating
 * `txtExpiryDate` against `txtEffectiveDate`.
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
 * Whether the expiry FIELD is already stating the ordering rule in its own right.
 *
 * ⚠ ONE SENTENCE, SAID ONCE. The rule is stated in two places for two different reasons: the field message
 * marks WHICH bound is at fault and carries `aria-invalid`, `aria-describedby` and the red border with it,
 * while the statement beside the action explains why the action is unavailable. Where both would carry the
 * SAME sentence, a reader meets it twice - once from an assertive field container and again from a polite
 * status line. This predicate is the single condition both sites branch on, so the two can never drift into
 * duplicating or into both falling silent.
 *
 * The field message is withheld until BOTH bounds are touched, because naming an ordering fault against a
 * bound the operator has not yet reached accuses them of a mistake they have not made.
 *
 * @param state The form snapshot.
 * @returns `true` when the expiry field itself is stating the ordering rule.
 */
export function expiryFieldStatesDateOrder(state: RoleAssignmentFormState): boolean {
  return state.effectiveDateTouched && state.expiryDateTouched && state.datesOutOfOrder;
}

/**
 * Reads the route's raw parameter as a role identifier. Route parameters arrive as strings, so the
 * conversion is explicit.
 *
 * @param value The route parameter, which arrives as a string.
 * @returns The identifier, or `null` when the route carried nothing usable.
 */
export function parseRouteIdentifier(value: number | string | null | undefined): number | null {
  return parseRouteId(value);
}

/**
 * Extracts a problem document from a failed request without importing the HTTP layer.
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

export type MembershipLifecycle = 'current' | 'pending' | 'expired';

/**
 * Reduces a wire instant to the start of its UTC day, or `null` when there is no usable date. Uses the
 * shared pipe's OWN parser, so a bound the cell paints as empty can never acquire a qualifier and a bound
 * the cell paints can never be left unqualified.
 *
 * @param wire The bound exactly as it arrived.
 * @returns The instant, or `null`.
 */
function boundInstant(wire: string | null | undefined): Date | null {
  return parseDisplayInstant(wire);
}

/**
 * The start of a clock's UTC day.
 *
 * @param now The moment to reduce.
 * @returns Midnight UTC on the same calendar day.
 */
function startOfUtcDay(now: Date): Date {
  const start = new Date(0);
  start.setUTCFullYear(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  start.setUTCHours(0, 0, 0, 0);

  return start;
}

/**
 * Resolves where a membership stands against a clock — R-M24. ⚠ THE DEFECT THIS CLOSES.
 * `securityroles.ascx` declares four visible columns and neither the markup nor `SecurityRoles.ascx.vb`
 * compares either bound against the clock anywhere, so a membership that lapsed in 2020, one that begins
 * in 2030 and one in force today were drawn in the same colour, the same weight and with no other mark —
 * on the one screen whose purpose is administering who holds a role.
 *
 * @param row The membership as it arrived.
 * @param now The moment to judge against.
 * @returns The membership's lifecycle state.
 */
function resolveMembershipLifecycle(row: UserRole, now: Date): MembershipLifecycle {
  const startOfToday: number = startOfUtcDay(now).getTime();
  const expiry: Date | null = boundInstant(row.expiryDate);

  if (expiry !== null && expiry.getTime() <= startOfToday) {
    return 'expired';
  }

  const effective: Date | null = boundInstant(row.effectiveDate);

  if (effective !== null && effective.getTime() > startOfToday) {
    return 'pending';
  }

  return 'current';
}

/** Routed container for `/roles/:roleId/users`. */
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE. Every screen's header carries exactly one
 * subtitle stating that screen's SCOPE: the record it acts on when the title does not already name it,
 * and otherwise what the screen is for, in one line. It never carries a status, a count or a progress
 * readout - those belong to the live region that owns them, and a count in two places is two owners of
 * one fact. Measured finding: subtitles appeared on ten of the twenty screens and carried three
 * different kinds of thing, so a reader could not tell what the slot was for.
 */
const PAGE_SUBTITLE =
  'The accounts holding this role, and the dates they hold it between.';

@Component({
  selector: 'app-role-assignment',
  standalone: true,
  imports: [
    NativeDateValidityDirective,
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    AbsentValueComponent,
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
  // MIGRATION: index of every deliberate divergence from the legacy control. Each entry names the member
  // whose documentation carries the full note, so this list cannot drift out of date the way line
  // references would.

  private readonly store = inject(RoleStore);
  private readonly userService = inject(UserService);

  /**
   * The identity, read for ONE fact: which tenant the caller belongs to. Taken from the caller rather
   * than from a route, because this screen addresses a role and names no portal.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The tenant's own record, for the protected pairing this screen must not offer to remove. CORE state,
   * injected as the account listing and the role editor both inject it.
   */
  private readonly portals = inject(PortalStore);

  /**
   * The owner of the tenant's account policy, which decides how an account is chosen here. ⚠ THE STORE
   * RATHER THAN THE TRANSPORT, and that is this file's own rule applied rather than an inconsistency with
   * the lookup beside it.
   */
  private readonly accounts = inject(UserStore);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly roleIdSignal: WritableSignal<number | null> = signal<number | null>(null);

  /**
   * The role whose read this screen is waiting on, or `null` when none is outstanding. Absence is `null`
   * and nothing else: `Roles.RoleID` is seeded `IDENTITY(0, 1)`, so a role key of zero is the portal's
   * first role and `0` cannot mean "not waiting".
   */
  private readonly awaitedRoleKey: WritableSignal<number | null> = signal<number | null>(null);

  /**
   * The account lookup in flight, or `null` when none is. ⚠ A HANDLE, NOT MERELY A TEARDOWN.
   * `takeUntilDestroyed` ends a lookup when the screen goes away, which is a different question from
   * ending the one a newer term replaces.
   */
  private userLookupRequest: Subscription | null = null;

  /**
   * The complete-account-list walk in flight, or `null` when none is. Held for the same reason as {@link
   * userLookupRequest}: a walk the screen has stopped waiting for is still a sequence of requests the
   * tenant pays for, and its answer would repopulate a drop-down the policy may since have replaced with
   * the name box.
   */
  private accountChoicesRequest: Subscription | null = null;

  private accountCountRequest: Subscription | null = null;

  /** The membership write this screen is waiting on, or `null` when none is outstanding. */
  private readonly awaitedWrite: WritableSignal<AwaitedAssignmentWrite | null> =
    signal<AwaitedAssignmentWrite | null>(null);

  /**
   * The identifier the store issued for {@link RoleAssignmentComponent.awaitedWrite}. Zero means "no
   * write of ours is outstanding".
   */
  private readonly awaitedWriteId: WritableSignal<number> = signal<number>(0);

  private readonly userLookupGeneration = new OperationGeneration();

  /** The membership an outstanding removal addressed, so a refusal can be attributed to it. */
  private readonly outstandingRemoval: WritableSignal<UserRole | null> = signal<UserRole | null>(
    null,
  );

  /**
   * A message to raise once the membership listing has finished refreshing, or `null`. The order is
   * load-bearing rather than cosmetic: `SecurityRoles.ascx.vb:L579-L584` rebound the grid unconditionally
   * and raised the refusal only afterwards, so the operator read the message against a refreshed grid.
   */
  private readonly deferredNotice: WritableSignal<DeferredNotice | null> =
    signal<DeferredNotice | null>(null);

  /**
   * The account whose probe answer may still write the two date boxes, or `null`. A one-shot marker
   * rather than a mode.
   */
  /**
   * The account whose boxes must be RE-BASELINED once the membership listing settles after a write, or
   * `null` when no write is awaiting one. Deliberately a SECOND marker rather than a reuse of {@link
   * awaitedPrefillUserId}.
   */
  /** Whether the membership write in flight AMENDS an existing enrolment rather than creating one. */
  private readonly writeWasAnAmendment: WritableSignal<boolean> = signal(false);

  /** The account name the write in flight concerns, captured at dispatch for the same reason. */
  private readonly writeSubjectName: WritableSignal<string | null> = signal<string | null>(null);

  private readonly awaitedRebaselineUserId: WritableSignal<number | null> = signal<number | null>(null);

  private readonly awaitedPrefillUserId: WritableSignal<number | null> = signal<number | null>(
    null,
  );

  /**
   * Whether a membership read is in flight that this screen has not yet reported on. A latch rather than
   * a mirror.
   */
  private readonly assignmentsSettling: WritableSignal<boolean> = signal(false);

  private readonly problemSignal: WritableSignal<ProblemDetails | null> =
    signal<ProblemDetails | null>(null);
  /**
   * What the last completed account lookup found. One value rather than several so the offered matches
   * and the two counts beside them can never describe different lookups.
   */
  private readonly userLookupSignal: WritableSignal<UserLookupOutcome> =
    signal<UserLookupOutcome>(NO_USER_LOOKUP);

  private readonly userLookupLoadingSignal: WritableSignal<boolean> = signal(false);
  private readonly userLookupTermSignal: WritableSignal<string> = signal('');

  /** Every account in the tenant, for the drop-down the account policy can ask for. */
  private readonly accountChoicesSignal: WritableSignal<readonly UserChoice[]> = signal<
    readonly UserChoice[]
  >([]);

  private readonly accountChoicesLoadingSignal: WritableSignal<boolean> = signal(false);

  /**
   * How many accounts the tenant holds, or `null` while that is unknown. Read ONLY when the account
   * policy could not be read, because it is only then that the count decides anything: see {@link
   * LEGACY_ACCOUNT_LISTING_CEILING}.
   */
  private readonly accountCountSignal: WritableSignal<number | null> = signal<number | null>(null);

  private readonly accountCountLoadingSignal: WritableSignal<boolean> = signal(false);

  /**
   * Whether the count probe itself failed. The name box is the answer in that case, on the same reasoning
   * as everywhere else on this screen: it is the affordance that needs no tenant-wide read.
   */
  private readonly accountCountFailedSignal: WritableSignal<boolean> = signal(false);

  /** Whether the complete account list could NOT be assembled, so the drop-down cannot be offered. */
  private readonly accountChoicesFailedSignal: WritableSignal<boolean> = signal(false);
  private readonly selectedUserSignal: WritableSignal<UserChoice | null> =
    signal<UserChoice | null>(null);
  private readonly pendingRemovalSignal: WritableSignal<UserRole | null> = signal<UserRole | null>(
    null,
  );
  private readonly protectedPairingsSignal: WritableSignal<ReadonlySet<string>> = signal<
    ReadonlySet<string>
  >(new Set<string>());

  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   *
   * ⚠ THE BUSY EXCLUSION WAS REMOVED, AND ITS REMOVAL CLOSES A MEASURED HOLE. This predicate used to read
   * `dirty && busy === false`, which reported the screen CLEAN for exactly as long as a write was in flight -
   * so navigating away mid-save was admitted in silence, the departure destroyed the component, and
   * `takeUntilDestroyed` cancelled the request. The operator lost the write and was told nothing. A form
   * holding an unfinished write is the LEAST safe moment to leave, not the safest.
   *
   * The exclusion was written to stop the application's OWN post-save navigation being challenged, and that
   * case is already covered properly: every success path replaces the address imperatively, which
   * `unsavedChangesGuard` admits explicitly. Nothing here has to approximate it a second time.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty,
  );

  /** The screen's typed form. */
  public readonly form = new FormGroup<RoleAssignmentFormModel>(
    {
      userId: new FormControl<number | null>(null, { nonNullable: true }),
      // MIGRATION: the two CALENDAR POP-UPS ARE DROPPED and each bound is a native date control inside the
      // shared field wrapper instead.
      effectiveDate: new FormControl<string>(NO_DATE, {
        nonNullable: true,
        validators: [calendarDateValidator],
      }),
      expiryDate: new FormControl<string>(NO_DATE, {
        nonNullable: true,
        validators: [calendarDateValidator],
      }),
      notify: new FormControl<boolean>({ value: false, disabled: true }, { nonNullable: true }),
    },
    { validators: [dateOrderValidator] },
  );

  private readonly formStateSignal: WritableSignal<RoleAssignmentFormState> =
    signal<RoleAssignmentFormState>(this.readFormState());

  /** The role addressed by the route, or `null` when the route carried nothing usable. */
  public readonly resolvedRoleId: Signal<number | null> = this.roleIdSignal.asReadonly();

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

  /** The page of memberships on screen. Exactly one page is held; the pager reaches the rest. */
  public readonly assignments: Signal<readonly UserRole[]> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return [];
    }

    return this.store.assignmentItems();
  });

  /** The paging facts of the page in hand, gated on the addressed role. */
  private readonly assignmentsMeta: Signal<ApiMeta> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return UNRESOLVED_PAGE_META;
    }

    return this.store.assignmentsMeta();
  });

  /**
   * The drop-down's entries, each carrying the label the option renders. Built here rather than in the
   * template so the "already in this role" note is composed in one place and can be asserted from a
   * specification.
   */
  public readonly accountChoiceEntries: Signal<readonly AccountChoiceEntry[]> = computed(() => {
    const memberIds = new Set<number>();
    for (const row of this.assignments()) {
      memberIds.add(row.userId);
    }

    return this.accountChoicesSignal().map((choice: UserChoice) => {
      // POSITIVELY KNOWN MEMBERSHIP ONLY, and the set changes nothing about that.
      const already: boolean = memberIds.has(choice.userId);
      const base = `${choice.displayName} (${choice.username})`;

      return {
        userId: choice.userId,
        label: already ? `${base}${ROLE_ASSIGNMENT_TEXT.accountAlreadyInRoleSuffix}` : base,
        alreadyInRole: already,
      };
    });
  });

  /**
   * Whether the page of memberships in hand IS the whole membership. ⚠ THIS IS WHAT MAKES A LOCAL ANSWER
   * TRUSTWORTHY. The membership listing is paged, so an account absent from the rows on screen is
   * normally UNKNOWN rather than a non-member.
   */
  /**
   * Whether the "already in this role" marking on the picker can be trusted to be COMPLETE - U21. It can
   * only mark accounts whose membership rows are in hand, and the membership listing is paged.
   */
  protected readonly choiceAnnotationIncomplete: Signal<boolean> = computed(
    () => this.accountChoiceEntries().length > 0 && !this.membershipPageIsComplete(),
  );

  private readonly membershipPageIsComplete: Signal<boolean> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return false;
    }

    const meta: ApiMeta = this.assignmentsMeta();
    const held: number = this.assignments().length;

    return meta.pageSize > 0 && meta.totalCount <= held;
  });

  /**
   * The chosen account's membership as the ROWS IN HAND report it, or `null` when they do not. Compared
   * with `===` because zero and minus one are legitimate account identifiers on this schema.
   */
  private readonly membershipFromRows: Signal<UserRole | null> = computed(() => {
    const chosen = this.selectedUserSignal();

    if (chosen === null) {
      return null;
    }

    return this.assignments().find((row: UserRole) => row.userId === chosen.userId) ?? null;
  });

  /**
   * The page on screen, counted from nought, for the shared pager's `page` input. The index the SERVER
   * reported is bound rather than the one this screen last asked for, so the pager can never claim to be
   * on a page whose request failed.
   */
  public readonly pageIndex: Signal<number> = computed(() => this.assignmentsMeta().pageIndex);

  /**
   * The COLUMN KEY the membership listing is ordered by, or `null` for the order the server chose. ⚠ A
   * COLUMN KEY, NOT THE ENDPOINT FIELD THE COORDINATE HOLDS. The shared grid compares this input against
   * a column key to decide which heading announces itself sorted, so handing it the stored `DisplayName`
   * would leave the account heading announcing "sortable, not sorted" while the request carried an
   * ordering - the grid would be telling the reader the opposite of what the server was asked.
   */
  public readonly sortColumnKey: Signal<string | null> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return null;
    }

    return this.store.assignmentsPage().sortBy === null
      ? null
      : ROLE_ASSIGNMENT_COLUMN_KEY.userName;
  });

  /**
   * The direction {@link RoleAssignmentComponent.sortColumnKey} is applied in, or `null` for the server's
   * default. The token is the server's own member name; an abbreviated spelling is refused by the model
   * binder with `400`, which is why the type comes from the paging contract rather than being written out
   * here.
   */
  public readonly sortDirection: Signal<SortDirection | null> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return null;
    }

    return this.store.assignmentsPage().sortDir;
  });

  /** The page size in effect, for the shared pager's `pageSize` input. */
  public readonly pageSize: Signal<number> = computed(() => this.assignmentsMeta().pageSize);

  /** How many memberships the role has in total, for the shared pager's `totalCount` input. */
  public readonly totalCount: Signal<number> = computed(() => this.assignmentsMeta().totalCount);

  /**
   * Whether there is a result COUNT worth stating, which is what mounts the shared pager. ⚠ WIDER THAN
   * "MORE THAN ONE PAGE", AND NARROWER THAN "ALWAYS".
   */
  public readonly hasResults: Signal<boolean> = computed(() => this.totalCount() > 0);

  /**
   * Whether the pager has anything to offer. The same predicate the rest of the workspace draws its pager
   * on — more memberships exist than fit on one page — so a role with ten or fewer members renders
   * exactly what the unpaged legacy grid rendered, with no pager in sight.
   */
  public readonly pagerRequired: Signal<boolean> = computed(() => {
    const size = this.pageSize();

    return size > 0 && size < this.totalCount();
  });

  /**
   * Whether the membership listing is in flight. Read from the store rather than latched here, so the
   * re-read the store performs after a write also turns the spinner — without it the rows would change
   * under the operator with no indication that anything was happening.
   */
  public readonly assignmentsLoading: Signal<boolean> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return false;
    }

    return this.store.assignmentsLoading();
  });

  /**
   * Whether the MEMBERSHIP READ failed, so zero rows means "nothing is known" rather than "this role has no
   * members". Narrowed to the read operation and to THIS screen's role, because the store is provided at the
   * application root and its failure slot holds whatever failed most recently anywhere.
   */
  public readonly assignmentsFailed: Signal<boolean> = computed(() => {
    const addressed = this.roleIdSignal();

    if (addressed === null || this.store.assignmentsRoleId() !== addressed) {
      return false;
    }

    return this.store.failure()?.operation === 'loadAssignments';
  });

  /** Whether a membership write of this screen's is in flight, so the action can be held. */
  public readonly saving: Signal<boolean> = computed(() => this.awaitedWrite() !== null);

  /** The last failure as a problem document, for the shared banner's `problem` input. */
  public readonly problem: Signal<ProblemDetails | null> = this.problemSignal.asReadonly();

  /** The accounts the current lookup offers as choices. */
  public readonly userMatches: Signal<readonly UserChoice[]> = computed(
    () => this.userLookupSignal().matches,
  );

  /** Whether an account lookup is in flight. */
  public readonly userLookupLoading: Signal<boolean> = this.userLookupLoadingSignal.asReadonly();

  /** The sentence describing a lookup whose match set is larger than what is offered, or `null`. */
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
   * Which account-selection control the tenant's policy asks this screen to present. this is
   * `Security_UsersControl`, acted on at `SecurityRoles.ascx.vb:L202-L221`.
   */
  public readonly usersControlMode: Signal<UsersControlMode> = computed(() => {
    if (this.accountChoicesFailedSignal()) {
      return 'lookup';
    }

    const settings = this.accounts.membershipSettings();
    if (settings !== null) {
      if (settings.securityUsersControl !== USERS_CONTROL.combo) {
        return 'lookup';
      }

      // ⚠ A STORED PREFERENCE FOR THE DROP-DOWN DOES NOT OVERRIDE THE ENUMERATION THRESHOLD, and this test
      // is what a performance review found missing.
      return this.exceedsEnumerationThreshold() ? 'lookup' : 'combo';
    }

    // The policy is unreadable, so the legacy's own default rule decides. A count that is still outstanding
    // reads as the name box here and is masked by `accountPolicyPending`, which renders neither control; a
    // count that could not be read at all reads as the name box for real.
    const accountCount = this.accountCountSignal();
    if (accountCount === null) {
      return 'lookup';
    }

    return accountCount > LEGACY_ACCOUNT_LISTING_CEILING ? 'lookup' : 'combo';
  });

  /**
   * Whether the tenant holds more accounts than the drop-down may enumerate. ⚠ AN UNREAD COUNT IS NOT AN
   * EXCEEDED THRESHOLD, and the direction of that default is chosen rather than incidental.
   */
  private readonly exceedsEnumerationThreshold: Signal<boolean> = computed(() => {
    const accountCount = this.accountCountSignal();

    return accountCount !== null && accountCount > LEGACY_ACCOUNT_LISTING_CEILING;
  });

  /**
   * Whether the account policy has not yet resolved, so neither control should be offered. The legacy
   * screen never had this state — it decided before it rendered — and reproducing that means holding the
   * field rather than guessing and correcting.
   */
  public readonly accountPolicyPending: Signal<boolean> = computed(() => {
    const settings = this.accounts.membershipSettings();

    if (settings !== null) {
      // A STORED PREFERENCE FOR THE DROP-DOWN STILL WAITS FOR THE COUNT, because the enumeration threshold
      // overrides it and the threshold cannot be applied without the count.
      return settings.securityUsersControl === USERS_CONTROL.combo && this.accountCountOutstanding();
    }

    if (this.accounts.membershipSettingsLoading()) {
      return true;
    }

    // The policy came back empty. The count decides, so it is pending until it either answers or
    // fails; a failure is an answer, and it resolves to the name box.
    return this.accountCountOutstanding();
  });

  /** Whether the account-count probe has neither answered nor failed. */
  private readonly accountCountOutstanding: Signal<boolean> = computed(
    () => this.accountCountSignal() === null && this.accountCountFailedSignal() === false,
  );

  /**
   * Whether the account policy could not be read AND the name box is what replaced it because the
   * fallback rule could not be applied — the count itself was unreadable. ⚠ NARROWER THAN IT LOOKS, AND
   * THE NARROWING IS THE POINT. An unread policy alone no longer means the name box: the legacy's default
   * rule may well have chosen the drop-down, in which case nothing was degraded and there is nothing to
   * explain.
   */
  public readonly accountPolicyUnavailable: Signal<boolean> = computed(
    () =>
      this.accounts.membershipSettings() === null &&
      this.accounts.membershipSettingsLoading() === false &&
      this.accountCountFailedSignal(),
  );

  /**
   * Whether the name box is offered because the site holds more accounts than the legacy's own
   * enumeration threshold.
   */
  public readonly accountPolicyDefaultedBySize: Signal<boolean> = computed(() => {
    if (this.accounts.membershipSettings() !== null) {
      return false;
    }

    const accountCount = this.accountCountSignal();

    return accountCount !== null && accountCount > LEGACY_ACCOUNT_LISTING_CEILING;
  });

  /** The wording for {@link accountPolicyDefaultedBySize}, carrying the measured threshold. */
  public readonly accountPolicyDefaultedBySizeMessage: string =
    ROLE_ASSIGNMENT_TEXT.accountPolicyDefaultedBySize.replace(
      '{0}',
      String(LEGACY_ACCOUNT_LISTING_CEILING),
    );

  /** Every account in the tenant, for the drop-down. Empty in every other mode. */
  public readonly accountChoices: Signal<readonly UserChoice[]> =
    this.accountChoicesSignal.asReadonly();

  /** Whether the complete account list is being assembled. */
  public readonly accountChoicesLoading: Signal<boolean> =
    this.accountChoicesLoadingSignal.asReadonly();

  /** Whether the drop-down was asked for but could not be built. */
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
  public readonly selectedUser: Signal<UserChoice | null> = this.selectedUserSignal.asReadonly();

  /**
   * The chosen account's existing membership of the addressed role, or `null` when none is known. `null`
   * covers three cases this screen treats identically, because the legacy screen did: no account is
   * chosen, the account holds no membership, and the probe could not settle the question.
   */
  public readonly selectedMembership: Signal<UserRole | null> = computed(() => {
    const addressed = this.roleIdSignal();
    const chosen = this.selectedUserSignal();

    if (addressed === null || chosen === null) {
      return null;
    }

    // ⚠ THE ROWS IN HAND ARE CONSULTED FIRST, AND THEY ARE AUTHORITATIVE WHEN THEY ANSWER. A row of this
    // role's membership listing carries the account, the effective bound and the expiry bound - every
    // member the probe would return - so asking the server for a fact already on screen buys nothing.
    const fromRows: UserRole | null = this.membershipFromRows();

    if (fromRows !== null) {
      return fromRows;
    }

    if (this.membershipPageIsComplete()) {
      return null;
    }

    const key = this.store.probedAssignmentKey();

    if (key === null || key.roleId !== addressed || key.userId !== chosen.userId) {
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

  private readonly today = signal<Date>(new Date());

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
   * The heading, with the role's name substituted into the legacy title template. the placeholder is
   * filled by INTERPOLATION and never by a markup-injecting binding.
   */
  /** The one-line scope statement shown beneath the title. */
  public readonly pageSubtitle = PAGE_SUBTITLE;

  public readonly title: Signal<string> = computed(() => {
    const current = this.role();
    if (current === null) {
      return ROLE_ASSIGNMENT_TEXT.titleFallback;
    }
    return ROLE_ASSIGNMENT_TEXT.titleTemplate.replace('{0}', current.roleName);
  });

  /**
   * Whether the write may proceed. This is the guard clause from `SecurityRoles.ascx.vb:L521` expressed
   * as a derived view rather than as a validator, which is why the account field carries no required
   * rule.
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
   * Why the action cannot be taken yet, or `null` when it can.
   *
   * ⚠ A DISABLED CONTROL STATES THAT SOMETHING IS WRONG AND NEVER WHAT. The submit was gated by
   * {@link canSubmit} alone, so an operator who had not chosen an account - or whose expiry bound did not
   * follow its effective bound - met a dead button with no field error, no banner and no message, and no way
   * to discover which of the two it was. The condition is named here instead.
   *
   * The blockers are reported in the order an operator meets them, and only ONE is reported at a time: naming
   * a second condition they cannot yet act on adds nothing. The date rule is spelt out here ONLY where the
   * expiry field is not already spelling it out - see {@link expiryFieldStatesDateOrder}. That field message
   * is withheld until both bounds are touched while the button is not, so without this the ordering fault was
   * refused in silence; with it stated in both places at once a reader met the same sentence twice. Where the
   * field does carry it, this line points at the marked field instead, which the field's red border and its
   * `aria-invalid` make a true statement rather than a promise.
   */
  public readonly submitBlockedReason: Signal<string | null> = computed(() => {
    // Nothing is blocked while the write is in flight; the busy state speaks for that.
    if (this.saving() || this.roleIdSignal() === null) {
      return null;
    }

    const state = this.formStateSignal();

    if (state.userId === null) {
      return ROLE_ASSIGNMENT_TEXT.chooseAccountFirst;
    }

    if (state.datesOutOfOrder && expiryFieldStatesDateOrder(state) === false) {
      // The legacy sentence carries a leading `<br>` that only the shared field wrapper strips, and this
      // statement is rendered outside one - so it is stripped here rather than shown verbatim.
      return ROLE_ASSIGNMENT_TEXT.expiryNotAfterEffective.replace(/^(?:<br\s*\/?>)+/i, '');
    }

    if (!state.valid) {
      return ROLE_ASSIGNMENT_TEXT.correctTheDatesFirst;
    }

    return null;
  });

  /**
   * Whether the statement beside the action is reporting a FAULT rather than an unfinished step.
   *
   * ⚠ 'NOTHING IS WRONG YET' AND 'WHAT YOU ENTERED IS WRONG' ARE NOT THE SAME MESSAGE. Both were painted in
   * the same muted grey, so arriving at the screen and having nothing chosen looked exactly like entering an
   * expiry that precedes its effective bound. The design vocabulary already carries a colour for validation
   * text, and the wording plus the marked field carry the distinction for a reader who perceives no colour at
   * all, so colour is never the only signal.
   *
   * An account being chosen is the dividing line: until one is, the operator has not asserted anything that
   * could be wrong.
   */
  public readonly submitBlockedByFailure: Signal<boolean> = computed(() => {
    if (this.saving() || this.roleIdSignal() === null) {
      return false;
    }

    const state = this.formStateSignal();

    return state.userId !== null && state.valid === false;
  });

  /**
   * The action's label, which becomes 'Update User Role' once the chosen account already holds the role.
   * only ONE of the two legacy relabel branches is live in this mode.
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

  public readonly showNoMatchingUsers: Signal<boolean> = computed(
    () =>
      this.userLookupTermSignal().length > 0 &&
      this.userLookupLoadingSignal() === false &&
      this.userLookupSignal().matches.length === 0,
  );

  /** Whether the route addressed a role at all, so the template can explain its absence. */
  public readonly roleUnresolved: Signal<boolean> = computed(() => this.roleIdSignal() === null);

  /** Messages for the effective-bound field, gated the way `Display="Dynamic"` gated them. */
  /** The earliest date either bound accepts, from the stored column's own range. */
  public readonly earliestDate = EARLIEST_STORABLE_DATE;

  /** The latest date either bound accepts, from the stored column's own range. */
  public readonly latestDate = LATEST_STORABLE_DATE;

  public readonly effectiveDateMessages: Signal<readonly string[]> = computed(() => {
    const state = this.formStateSignal();
    const messages: string[] = [];
    if (
      state.effectiveDateNativelyUnusable ||
      (state.effectiveDateTouched && state.effectiveDateInvalid)
    ) {
      messages.push(ROLE_ASSIGNMENT_TEXT.invalidEffectiveDate);
    }
    messages.push(...this.serverMessagesFor(EFFECTIVE_DATE_CONTROL));
    return messages;
  });

  /**
   * Messages for the expiry-bound field. The group-level ordering rule surfaces HERE rather than on the
   * effective bound, because the legacy validator declared `controltovalidate="txtExpiryDate"`.
   */
  public readonly expiryDateMessages: Signal<readonly string[]> = computed(() => {
    const state = this.formStateSignal();
    const messages: string[] = [];
    // Raised ONCE for any of the three unusable states, exactly as on the effective bound.
    if (state.expiryDateNativelyUnusable || (state.expiryDateTouched && state.expiryDateInvalid)) {
      messages.push(ROLE_ASSIGNMENT_TEXT.invalidExpiryDate);
    }
    if (expiryFieldStatesDateOrder(state)) {
      messages.push(ROLE_ASSIGNMENT_TEXT.expiryNotAfterEffective);
    }
    messages.push(...this.serverMessagesFor(EXPIRY_DATE_CONTROL));
    return messages;
  });

  /** Server-reported messages about the account, when a rejected write named that field. */
  public readonly userMessages: Signal<readonly string[]> = computed(() =>
    this.serverMessagesFor('userId'),
  );

  public readonly columns: Signal<readonly DataTableColumn<UserRole>[]> = computed(() => {
    const columns: DataTableColumn<UserRole>[] = [];

    // ⚠ THE COMMAND COLUMN IS WITHHELD ON A ROLE WHOSE MEMBERSHIPS CANNOT BE REMOVED — R-M24. See
    // `removalAvailable`, which carries the measurement and the legacy precedent.
    const commands = this.commandsCellTemplate();
    if (commands !== undefined && this.removalAvailable()) {
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

    const user = this.userCellTemplate();
    columns.push(
      user === undefined
        ? {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.userName,
            label: ROLE_ASSIGNMENT_TEXT.userNameHeader,
            rowHeader: true,
            // Ordering: see the note above. Declared on BOTH variants of this column, because the
            // templated form and the plain-text form are the same column.
            sortable: true,
            value: (row: UserRole): string => row.displayName,
          }
        : {
            key: ROLE_ASSIGNMENT_COLUMN_KEY.userName,
            label: ROLE_ASSIGNMENT_TEXT.userNameHeader,
            sortable: true,
            rowHeader: true,
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

  /** The role addressed by the route. */
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
      // The route stopped naming a role, so this screen is no longer looking at any listing. Said before
      // returning, because the early return below skips the reads and would otherwise leave the store
      // believing the previous role is still on screen.
      if (previous !== null) {
        this.store.closeAssignmentsView(previous);
      }
      return;
    }
    // ⚠ ANNOUNCED BEFORE THE READS, AND ON EVERY CHANGE OF ROLE. The store refuses to refresh a membership
    // listing that is not the one on screen, and it can only know which that is because this screen tells
    // it.
    this.store.openAssignmentsView(resolved);
    this.loadRole(resolved);
    this.loadAssignments(resolved, null);
  }

  public get roleId(): number | null {
    return this.roleIdSignal();
  }

  // THE TENANT'S PROTECTED PAIRING IS READ, NOT SUPPLIED.

  /** The tenant's designated administrator account, or `null` until its record resolves. */
  public readonly administratorUserId: Signal<number | null> = computed(() =>
    this.portals.administratorUserId(),
  );

  /** The tenant's administrator role, on the same terms as {@link administratorUserId}. */
  public readonly administratorRoleId: Signal<number | null> = computed(() =>
    this.portals.administratorRoleId(),
  );

  /**
   * The tenant's registered-users role, on the same terms as {@link administratorUserId}. `Roles.RoleID`
   * is `IDENTITY(0, 1)`, so nought is a real role key; every comparison against these values is an
   * explicit equality test against `null` and never a truthiness test.
   */
  public readonly registeredRoleId: Signal<number | null> = computed(() =>
    this.portals.registeredRoleId(),
  );

  public constructor() {
    // ⚠ THE STORE IS TOLD WHEN THIS SCREEN GOES, AND THAT IS NOT COSMETIC BOOKKEEPING. Both membership
    // writes re-read the listing when they settle, and a write dispatched here can settle after the
    // operator has moved on.
    this.destroyRef.onDestroy(() => {
      const roleId = this.roleIdSignal();

      if (roleId !== null) {
        this.store.closeAssignmentsView(roleId);
      }
    });

    // ⚠ THE GROUP'S STREAM ALONE LEAVES THE TOUCHED FLAGS STALE, AND THAT IS NOT A THEORETICAL GAP.
    // `AbstractControl.events` emits a touched event only when THAT control's own touched state changes.
    // Blurring the first date bound flips the GROUP from untouched to touched and does emit; blurring the
    // second flips only the child, leaves the already-touched group unchanged, and emits NOTHING on the
    // group's stream - so the snapshot kept `expiryDateTouched`/`effectiveDateTouched` at whatever they were
    // one blur earlier. The observable consequence was a form that suppressed the expiry field's error, its
    // `aria-invalid`, its `aria-describedby` and its red border for the ordinary fill-then-blur path, while
    // exposing all four for the same logical state reached by a later keystroke. Each bound's own stream is
    // merged in so a blur is never invisible, whichever bound it lands on.
    merge(
      this.form.events,
      this.form.controls.effectiveDate.events,
      this.form.controls.expiryDate.events,
    )
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this.formStateSignal.set(this.readFormState());
      });

    // The tenant's own record, for the protected pairing. Read from the CALLER'S identity and never from a
    // route, and idempotent in the store — several screens asking on initialisation issue one request
    // between them.
    const portalId: number | undefined = this.auth.currentUser()?.portalId;

    if (portalId !== undefined) {
      this.portals.loadCurrentPortalContext(portalId);
    }

    // Read through the store rather than the transport, and read UNCONDITIONALLY rather than only when
    // absent: the store owns the policy and a screen cannot tell a policy read from this session apart from
    // one cached before a setting was changed elsewhere.
    this.accounts.loadMembershipSettings();

    // The first is an ABSENT policy: the legacy resolved an absent `Security_UsersControl` from the
    // tenant's account count, so reproducing that needs the count.
    effect(() => {
      const settings = this.accounts.membershipSettings();
      const reading: boolean = this.accounts.membershipSettingsLoading();

      const wanted: boolean =
        settings === null
          ? reading === false
          : settings.securityUsersControl === USERS_CONTROL.combo;

      untracked(() => {
        if (!wanted) {
          return;
        }

        if (
          this.accountCountSignal() !== null ||
          this.accountCountLoadingSignal() ||
          this.accountCountFailedSignal()
        ) {
          return;
        }

        this.loadAccountCount();
      });
    });

    // COMPLETE ACCOUNT LIST. Assembled only when the policy actually asks for the drop-down, which is what
    // keeps a tenant that uses the name box from paying for a walk of every account it holds.
    effect(() => {
      const mode = this.usersControlMode();

      // ⚠ THE WALK WAITS FOR THE POLICY TO SETTLE, INCLUDING THE COUNT IT NOW DEPENDS ON. Reading the
      // pending state in the TRACKED half is deliberate: the mode reads `combo` while the count is still
      // outstanding - that is the safe default, so that a stored preference is not momentarily contradicted
      // - and a walk started on it would enumerate a tenant the count is about to disqualify.
      const pending: boolean = this.accountPolicyPending();

      untracked(() => {
        if (mode !== 'combo' || pending) {
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

    // MEMBERSHIP READ BRIDGE. Raises the deferred message on the fall of the listing flag, and reports a
    // listing failure alongside it — the order the legacy handler fixed and the pairing its error path
    // used, which raised the deferred message and the read failure both.
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

        this.rebaselineAfterWrite();
      });
    });

    effect(() => {
      const awaited = this.awaitedWrite();
      const awaitedId = this.awaitedWriteId();
      const settled = this.store.mutation();

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
          this.raise(this.writeSuccessNotice(awaited, target));

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
   * Whether ANY membership of the addressed role can ever be removed — R-M24. ⚠ THIS GATES WHETHER THE
   * COMMAND COLUMN IS EMITTED AT ALL, and the reason is measured.
   *
   * @returns `true` when the column should be emitted.
   */
  public readonly removalAvailable: Signal<boolean> = computed(() => {
    const registeredRoleId: number | null = this.registeredRoleId();
    const roleId: number | null = this.roleIdSignal();

    return registeredRoleId === null || roleId === null || registeredRoleId !== roleId;
  });

  /**
   * Where one membership stands in its own lifecycle, for the two date cells to qualify — R-M24.
   *
   * @param row The membership.
   * @returns The lifecycle state.
   */
  public membershipState(row: UserRole): MembershipLifecycle {
    return resolveMembershipLifecycle(row, this.today());
  }

  /**
   * Whether the expiry cell should state that this membership is IN FORCE — R6.
   *
   * ⚠ GATED ON THE BOUND ACTUALLY PAINTING, AND THE GATE IS THE POINT. This screen holds an invariant that
   * predates the finding and outranks it: a qualifier never appears beside a cell the date renders as empty.
   * `Null.vb` spells an unset date as `Date.MinValue`, so both the emptiness and the qualification are
   * decided by the SAME parser precisely so that an unset bound cannot acquire a word. Stating "Active" in
   * an otherwise empty date column would also read as though the word were the date.
   *
   * So a membership with no expiry bound at all keeps its empty cell, exactly as before, and only a
   * membership whose expiry bound is a real painted date is qualified. That is where the finding's complaint
   * actually bites: two painted future dates, one lapsed and one in force, previously differed by the
   * presence or absence of a word and nothing else, leaving "in force" indistinguishable from "not judged".
   *
   * @param row The membership.
   * @returns True when the in-force qualifier belongs beside this row's expiry date.
   */
  public showsInForceQualifier(row: UserRole): boolean {
    return this.membershipState(row) === 'current' && boundInstant(row.expiryDate) !== null;
  }

  /**
   * Whether the row command should be offered for one membership. This is `DeleteButtonVisible` from
   * `SecurityRoles.ascx.vb:L360-L363`, which delegated to `RoleController.CanRemoveUserFromRole`.
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
   * The accessible name for a row's removal command, qualified by the account it removes. ⚠ THE VISIBLE
   * WORD IS UNTOUCHED. The button still reads the global `cmdDelete.Text` and looks exactly as it did;
   * this name is supplied through `aria-label`, so nothing about the painted row changes and the legacy
   * wording is preserved.
   *
   * @param row The membership the command would end.
   * @returns The command's accessible name, qualified by the account's display name.
   */
  /**
   * The confirmation body: the legacy question verbatim, then WHICH record it means.
   *
   * ⚠ THE MEASURED DEFECT. The dialog read only "Are You Sure You Wish To Delete This Item?" and named nothing at
   * all - searched against every identifier on the page it matched none of them - while being a real modal
   * that PHYSICALLY COVERS the grid behind it. Measured with the sixth row targeted, it overlaid the three
   * rows above it and the top of the target itself, so an operator had no way to check what was about to be
   * destroyed: the record's identity existed only on the triggering control's accessible name, which is
   * unreachable once the modal holds focus.
   *
   * The wording is APPENDED rather than rewritten, so the measured legacy sentence survives unchanged and
   * this reads as the same question with the answer to "which one" added. The member is named through the same expression {@link
   * RoleAssignmentComponent.removalCommandName} gives the control that raised the dialog, so the two can
   * never disagree.
   */
  public readonly removalMessage: Signal<string> = computed<string>(() => {
    const target: UserRole | null = this.pendingRemoval();

    if (target === null) {
      return this.text.confirmRemoval;
    }

    const named: string = target.displayName.trim();

    return named.length === 0
      ? this.text.confirmRemoval
      : `${this.text.confirmRemoval} ${named}`;
  });

  public removalCommandName(row: UserRole): string {
    return `${this.text.delete} ${row.displayName}`;
  }

  /** @param term The name fragment the operator typed. */
  public onUserSearch(term: string): void {
    const query = term.trim();
    this.userLookupTermSignal.set(query);

    // The lookup this term replaces is ABANDONED before the next one starts, and also when the box is
    // emptied - a lookup nobody is waiting for is still a request the tenant pays for, and its answer would
    // otherwise repopulate a list the operator has just cleared.
    this.userLookupRequest?.unsubscribe();
    this.userLookupRequest = null;

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
   * Chooses an account and prefills the bounds from its existing membership, if it has one. this is
   * `GetDates` from `SecurityRoles.ascx.vb:L273-L303`, and only its first branch is reproduced.
   *
   * @param user The account the operator chose from the lookup.
   */
  public selectUser(user: UserChoice): void {
    this.selectedUserSignal.set(user);
    this.form.controls.userId.setValue(user.userId);
    this.formStateSignal.set(this.readFormState());

    const roleId = this.roleIdSignal();

    if (roleId === null) {
      return;
    }

    if (this.membershipFromRows() !== null || this.membershipPageIsComplete()) {
      this.awaitedPrefillUserId.set(null);
      this.store.clearProbedAssignment();
      this.applyProbeAnswer(this.membershipFromRows());

      return;
    }

    this.awaitedPrefillUserId.set(user.userId);
    this.store.probeAssignment(roleId, user.userId);
  }

  /**
   * Chooses an account from the drop-down the tenant's account policy asked for. this is `cboUsers`, read
   * at `SecurityRoles.ascx.vb:L106-L109`.
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
   * Enrols the chosen account in the addressed role. the write is accepted on BOTH a created and a
   * no-content answer.
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

    // ⚠ CAPTURED AT DISPATCH, BECAUSE THE ANSWER CHANGES AS A RESULT OF THE WRITE. Whether this enrols an
    // account or amends an enrolment it already had is exactly what the write is about to alter, so the
    // confirmation's wording has to be decided from the state BEFORE it - the same state the command's own
    // label was rendered from, so the sentence agrees with the button the operator pressed.
    this.writeWasAnAmendment.set(this.selectedMembership() !== null);
    this.writeSubjectName.set(this.selectedUserSignal()?.displayName ?? null);
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
   * Removes the confirmed membership. the listing is ALWAYS re-read and the row is NEVER removed from
   * local state optimistically.
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
   * Moves the grid to another page of memberships. The index is passed through untouched: the shared
   * pager reports a ZERO-BASED index and the listing takes one, so there is no base to convert between,
   * and the pager only ever emits an index inside the range it was given.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  public onPageChange(pageIndex: number): void {
    if (this.roleIdSignal() === null || pageIndex === this.pageIndex()) {
      return;
    }

    this.store.setAssignmentsPage(pageIndex);
  }

  /**
   * Applies the reader's ordering to the membership listing, or removes it. ⚠ THE TRANSMITTED FIELD IS
   * `DisplayName`, NOT THE COLUMN KEY. The grid identifies the account column by the key `userName`,
   * which the endpoint's allowlist would accept as `Username` - a DIFFERENT account field from the one
   * this cell renders.
   *
   * @param change The key the reader activated and the direction to apply, or null to stop ordering.
   */
  public onSortChange(change: DataTableSortChange): void {
    if (this.roleIdSignal() === null || change.key !== ROLE_ASSIGNMENT_COLUMN_KEY.userName) {
      return;
    }

    this.store.setAssignmentsSort(
      change.direction === null ? null : ACCOUNT_SORT_FIELD,
      change.direction,
    );
  }

  /**
   * Whether a membership bound renders as nothing, so the cell can state the absence instead of drawing an
   * empty box.
   *
   * ⚠ THIS LISTING WAS THE LAST ONE STILL RENDERING A BARE EMPTY CELL. The legacy formatter returned the
   * empty string for an unrecorded bound, and every other listing in this application has already moved off
   * that: the roles grid, the accounts grid, the tenants grid and the modules grid all draw the shared mark
   * and announce what it means, because an empty cell cannot be told apart from a cell that failed to draw.
   * Both membership date columns still drew nothing at all - measured as zero characters, not even a space -
   * while the row's own name column beside them was fully populated. The mark, its colour and its words are the
   * shared ones, so this listing now answers the question the same way its siblings do.
   *
   * The test goes through the same parser the cell's own pipe uses, for the reason recorded on
   * {@link boundInstant}: a bound the cell paints as empty can never be reported as present, and a bound it
   * paints can never be reported as absent.
   *
   * @param wire The bound exactly as it arrived.
   * @returns True when the cell would otherwise be empty.
   */
  protected isBoundAbsent(wire: string | null | undefined): boolean {
    return boundInstant(wire) === null;
  }

  /** Reads the live form into the snapshot the derived views above depend on. */
  private readFormState(): RoleAssignmentFormState {
    const controls = this.form.controls;
    return {
      userId: controls.userId.value,
      effectiveDate: controls.effectiveDate.value,
      expiryDate: controls.expiryDate.value,
      valid: this.form.valid,
      effectiveDateInvalid: hasAnyError(controls.effectiveDate, DATE_UNUSABLE_ERRORS),
      effectiveDateTouched: controls.effectiveDate.touched,
      effectiveDateNativelyUnusable: hasAnyError(
        controls.effectiveDate,
        NATIVE_DATE_UNUSABLE_ERRORS,
      ),
      expiryDateInvalid: hasAnyError(controls.expiryDate, DATE_UNUSABLE_ERRORS),
      expiryDateTouched: controls.expiryDate.touched,
      expiryDateNativelyUnusable: hasAnyError(controls.expiryDate, NATIVE_DATE_UNUSABLE_ERRORS),
      datesOutOfOrder: this.form.hasError(DATE_ORDER_ERROR),
    };
  }

  /**
   * Messages a rejected write reported against one field.
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
   * Walks the account listing for one name until the exact account is found or the whole match set has
   * been examined. The termination rules and the reasoning behind them are stated on {@link
   * RoleAssignmentComponent.onUserSearch}; this is their implementation. ⚠ THE PAGES ARE JOINED IN PLACE
   * inside the projector rather than by re-spreading an accumulator per page.
   *
   * @param term The name the operator typed, already trimmed and known non-empty.
   * @returns The completed lookup, emitted exactly once.
   */
  private walkUserLookup(term: string): Observable<UserLookupOutcome> {
    const wanted = term.toLocaleLowerCase();
    const collected: UserChoice[] = [];
    let examined = 0;
    let reportedTotal = 0;
    let completion: UserLookupCompletion = 'complete';

    const isExact = (candidate: UserChoice): boolean =>
      candidate.username.toLocaleLowerCase() === wanted;

    const requestPage = (pageIndex: number): Observable<PagedResponse<UserChoice>> => {
      const request: PagedRequestParams = {
        pageIndex,
        pageSize: USER_LOOKUP_PAGE_SIZE,

        // ⚠ THE PICKER'S OWN FILTER, WHICH MATCHES A PREFIX OF THE LOGIN NAME - the same value and the same
        // semantics the account listing's `userName` filter carried, so the ordering guarantee below is
        // unchanged.
        query: term,
        // ⚠ THE ORDER IS WHAT MAKES THE COMMON CASE ONE REQUEST, and it is a guarantee rather than a
        // heuristic.
        sortBy: 'Username',
        sortDir: 'Ascending',
      };

      return this.userService.listChoices(request);
    };

    return requestPage(0).pipe(
      expand((response: PagedResponse<UserChoice>, index: number) => {
        // `UserService.listChoices` already answers a decoded page; this re-reads its framing rather
        // than unwrapping an envelope, which is what makes the same projector safe for every page.
        const page: PagedResult<UserChoice> = toPagedResult<UserChoice>(response);

        collected.push(...page.items);
        examined += page.items.length;
        reportedTotal = page.meta.totalCount;

        // The one ending that beats every other: the account the operator named is in hand, so no
        // remaining page can improve the answer.
        if (page.items.some(isExact)) {
          completion = 'exact';

          return EMPTY;
        }

        if (examined >= page.meta.totalCount) {
          completion = 'complete';

          return EMPTY;
        }

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
   * Assembles the complete account list the tenant's drop-down policy needs, or fails. The same
   * termination rules as {@link walkUserLookup} minus the exact-match ending, which has no meaning when
   * the whole list is the answer, and with the ceiling turned into a REFUSAL rather than a reported stop.
   *
   * @returns Every account in the tenant, emitted once.
   */
  private walkAllAccounts(): Observable<readonly UserChoice[]> {
    const collected: UserChoice[] = [];
    let gathered = 0;

    // NO SORT IS ASKED FOR, and that is the right request rather than an omission. The picker's own default
    // orders by DISPLAY name, which is what the drop-down's entries are captioned with, so the entries read
    // in the order they are shown.
    const requestPage = (pageIndex: number): Observable<PagedResponse<UserChoice>> =>
      this.userService.listChoices({ pageIndex, pageSize: USER_LOOKUP_PAGE_SIZE });

    return requestPage(0).pipe(
      expand((response: PagedResponse<UserChoice>, index: number) => {
        const page: PagedResult<UserChoice> = toPagedResult<UserChoice>(response);

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
      map((): readonly UserChoice[] => collected),
    );
  }

  /**
   * Reads how many accounts the tenant holds, for the enumeration threshold. One page of one record: the
   * answer wanted is the server's own total, so nothing is gathered from the response.
   */
  private loadAccountCount(): void {
    this.accountCountRequest?.unsubscribe();
    this.accountCountLoadingSignal.set(true);
    this.accountCountFailedSignal.set(false);

    this.accountCountRequest = this.userService
      .listChoices({ pageIndex: 0, pageSize: ACCOUNT_COUNT_PROBE_PAGE_SIZE })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response: PagedResponse<UserChoice>): void => {
          this.accountCountLoadingSignal.set(false);
          this.accountCountSignal.set(toPagedResult<UserChoice>(response).meta.totalCount);
        },
        error: (): void => {
          this.accountCountLoadingSignal.set(false);
          this.accountCountFailedSignal.set(true);
        },
      });
  }

  /**
   * Assembles the complete account list for the drop-down, abandoning any earlier attempt. A refusal is
   * reported TWICE deliberately, and the two say different things: the banner names what went wrong, and
   * the note beside the name box says what is offered in its place.
   */
  private loadAccountChoices(): void {
    this.accountChoicesRequest?.unsubscribe();
    this.accountChoicesLoadingSignal.set(true);
    this.accountChoicesFailedSignal.set(false);

    this.accountChoicesRequest = this.walkAllAccounts()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (accounts: readonly UserChoice[]): void => {
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
   * Returns the screen to its initial state, which a change of addressed role requires. The role and the
   * rows are NOT cleared here and do not need to be: both are derived from the store gated on the
   * addressed key, so they empty themselves the moment the key changes.
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
   * Asks the store for the addressed role, which supplies the heading's name. The marker is set BEFORE
   * the command, because the command dispatches synchronously and the bridge that observes its outcome
   * needs to know a read of ours is outstanding.
   *
   * @param roleId The role to read.
   */
  private loadRole(roleId: number): void {
    this.awaitedRoleKey.set(roleId);
    this.store.selectRole(roleId);
  }

  /**
   * Asks the store for ONE PAGE of the addressed role's memberships, deferring one message until it has
   * settled. the grid IS paged and the legacy one was not.
   *
   * @param roleId The addressed role.
   * @param notice A message to raise once the read has settled, or `null`.
   */
  private loadAssignments(roleId: number, notice: DeferredNotice | null): void {
    this.deferredNotice.set(notice);
    this.store.loadAssignments(roleId);
  }

  /**
   * Re-asks whether the chosen account holds the role, after a write may have changed it. this refreshes
   * the FACT and deliberately leaves the two date boxes alone — no prefill marker is set.
   */
  private reprobeSelectedMembership(): void {
    const chosen = this.selectedUserSignal();

    if (this.roleIdSignal() === null || chosen === null) {
      return;
    }

    this.awaitedRebaselineUserId.set(chosen.userId);
  }

  /**
   * The confirmation a SUCCESSFUL membership write reports. Three outcomes, three sentences: enrolling an
   * account, amending the bounds of an enrolment it already had, and ending one.
   *
   * @param awaited Which write settled.
   * @param target The membership row a removal targeted, or `null` for an assignment.
   * @returns The notice to raise.
   */
  private writeSuccessNotice(
    awaited: AwaitedAssignmentWrite,
    target: UserRole | null,
  ): DeferredNotice {
    const roleName: string = this.role()?.roleName ?? this.text.unnamedRole;

    if (awaited === 'removeAssignment') {
      const account: string = target?.displayName ?? this.text.unnamedAccount;

      return {
        severity: 'success',
        message: this.text.assignmentRemoved.replace('{0}', account).replace('{1}', roleName),
        // A completed write has nothing for an operator to escalate, so there is no reference to quote.
        reference: null,
      };
    }

    const account: string = this.writeSubjectName() ?? this.text.unnamedAccount;
    const template: string = this.writeWasAnAmendment()
      ? this.text.assignmentUpdated
      : this.text.assignmentAdded;

    return {
      severity: 'success',
      message: template.replace('{0}', account).replace('{1}', roleName),
      // A completed write has nothing for an operator to escalate, so there is no reference to quote.
      reference: null,
    };
  }

  /**
   * Re-baselines the two bounds from the stored membership once a write's listing re-read has settled. ⚠
   * WHAT THIS CLOSES. A successful addition left the form exactly as the operator had typed it while the
   * command's own label flipped to the amend wording - so the screen offered to UPDATE a membership from
   * boxes that no longer described it.
   */
  private rebaselineAfterWrite(): void {
    const awaited: number | null = this.awaitedRebaselineUserId();

    if (awaited === null) {
      return;
    }

    this.awaitedRebaselineUserId.set(null);

    const chosen = this.selectedUserSignal();
    const roleId = this.roleIdSignal();

    if (chosen === null || roleId === null || chosen.userId !== awaited) {
      return;
    }

    if (this.membershipFromRows() !== null || this.membershipPageIsComplete()) {
      this.store.clearProbedAssignment();
      this.applyProbeAnswer(this.membershipFromRows());

      return;
    }

    this.awaitedPrefillUserId.set(chosen.userId);
    this.store.probeAssignment(roleId, chosen.userId);
  }

  /**
   * Prefills the two date controls from the chosen account's existing membership, if it has one.
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
   * Composes the assignment body from the form. an empty box becomes `null` and never a minimum-value
   * instant.
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
   * @param failure The failure the store recorded for the removal.
   * @returns The deferred message, to be raised after the re-read.
   */
  private removalNoticeFor(failure: RoleStoreFailure): DeferredNotice {
    if (failure.conflict === PROTECTED_ASSIGNMENT_CODE) {
      const published = conflictMessage(failure.conflict);
      return {
        severity: failure.summary.severity,
        message: published === null ? ROLE_ASSIGNMENT_TEXT.removalRefused : published,
        // The wording is overridden above; the identifier the server recorded it under is not.
        reference: failure.summary.supportReference,
      };
    }
    return {
      severity: failure.summary.severity,
      message: failure.summary.message,
      reference: failure.summary.supportReference,
    };
  }

  /**
   * Records a store failure for the banner and describes it for the notification queue.
   *
   * @param failure The failure the store recorded.
   * @returns The message to raise.
   */
  private failureNoticeFor(failure: RoleStoreFailure): DeferredNotice {
    this.problemSignal.set(failure.problem);
    return {
      severity: failure.summary.severity,
      message: failure.summary.message,
      reference: failure.summary.supportReference,
    };
  }

  /**
   * Records a failure for the banner and describes it for the notification queue.
   *
   * @param error Whatever the request stream failed with.
   * @returns The message to raise.
   */
  private failureNotice(error: unknown): DeferredNotice {
    const problem = readProblemDetails(error);
    this.problemSignal.set(problem);
    const summary = summarizeProblem(problem);
    return {
      severity: summary.severity,
      message: summary.message,
      reference: summary.supportReference,
    };
  }

  /** Raises a deferred message, if there is one. */
  private raise(notice: DeferredNotice | null): void {
    if (notice === null) {
      return;
    }
    this.notifications.notify(notice.severity, notice.message, notice.reference);
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
  /**
   * How a row identifies itself to the shared grid, so a re-read of the rows already shown reuses their
   * row elements instead of rebuilding them. ⚠ THE RECORD'S OWN KEY, NOT THE ARRAY POSITION AND NOT THE
   * OBJECT. The grid's fallback is the row OBJECT, which is a correct key only while the same objects
   * stay in play; every read from the server decodes fresh objects, so without this a refetch presents
   * entirely new keys and the whole body is rebuilt to display records that never changed.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly assignmentRowKey = (row: UserRole): number => row.userRoleId;
}
