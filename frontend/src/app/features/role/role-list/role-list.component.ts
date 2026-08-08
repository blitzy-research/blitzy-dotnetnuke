import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  type Signal,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AuthStore } from '../../../core/state/auth.store';
import { DEFAULT_ROLE_GROUP_FILTER, RoleStore } from '../../../core/state/role.store';
import { NotificationService } from '../../../core/services/notification.service';
import {
  conflictMessage,
  fieldErrorMessages,
  stripLegacyBreakTags,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';

import type { OnInit } from '@angular/core';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { RoleGroup, RoleListItem } from '../../../core/models/role.model';
import type {
  RoleGroupFilter,
  RoleStoreFailure,
  RoleStoreOperation,
} from '../../../core/state/role.store';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';

// ---------------------------------------------------------------------------
// THE SELECTOR'S THREE-WAY IDENTIFIER
// ---------------------------------------------------------------------------
//
// MIGRATION: the legacy screen overloaded ONE `Integer` for three distinct concepts,
// and the two negative bands are not interchangeable:
//
//   -2  "All Roles"    - a pure UI pseudo-filter. NEVER persisted. `EditRoles.ascx.vb`
//                        never emits it into a form at all.
//   -1  "Global Roles" - both a filter AND the persisted "ungrouped" value on a role
//                        row (`Null.NullInteger`).
//   >=0 a real role-group identifier. Zero is REAL: `RoleGroups.RoleGroupID` is seeded
//       `IDENTITY(0, 1)`.
//
// The typed union that keeps the three apart is declared by `core/state/role.store.ts`
// and imported here rather than restated, so one vocabulary serves the whole feature.
// NO "normalise negative identifiers" helper exists anywhere in this file, deliberately:
// collapsing the bands is precisely the class of defect the union exists to prevent.
//
// These two constants are the ONLY place the magic integers appear, and they are used
// for exactly one purpose: the `value` attribute of an `option` element, which the DOM
// requires to be a string. They NEVER travel to the server. `RoleListFilter` in
// `core/utils/http-params.util.ts` states the rule outright - "Omit the member to place
// no group restriction; do not express that by sending a negative number" - and the
// store honours it by translating the union into the API's named `scope` instead
// (`All` and `Ungrouped`). Reproducing the negative integers on the wire would be a
// regression, not fidelity.

/** `option` value standing for the legacy "&lt; All Roles &gt;" pseudo-filter (legacy `-2`). */
const ALL_ROLES_FILTER_VALUE = -2;

/** `option` value standing for the legacy "&lt; Global Roles &gt;" filter (legacy `-1`). */
const GLOBAL_ROLES_FILTER_VALUE = -1;

// ---------------------------------------------------------------------------
// THE LEGACY NULL SENTINELS THIS SCREEN HAS TO RECOGNISE
// ---------------------------------------------------------------------------
//
// `Library/Components/Shared/Null.vb` L41-L84, read first-hand: `NullInteger` is `-1`,
// `NullSingle` is `Single.MinValue`, `NullString` is the EMPTY STRING rather than null,
// and `NullBoolean` is `False`. The first two reach this screen through the four money
// and period columns, so both are recognised below.
//
// The API models an absent value as JSON `null` - every one of the four members is
// typed `number | null` on `RoleListItem` - but the sentinels can also survive a
// round trip, so BOTH forms are handled. That is not belt-and-braces: a value that
// slipped through as `-1` and rendered as the text "-1" would be a silent data defect
// with no visible symptom other than a wrong grid cell.

/** `Null.NullInteger`: the legacy marker for an absent `Integer`. */
const LEGACY_NULL_INTEGER = -1;

/**
 * Lower bound at which a single-precision fee is treated as `Null.NullSingle`.
 *
 * `Single.MinValue` is exactly `-3.4028234663852886e38`. A THRESHOLD is compared rather
 * than that literal because the value crosses two lossy boundaries on its way here -
 * single to double, then double to JSON text and back - and either can perturb the low
 * bits, at which point an equality test silently stops matching and a sentinel renders
 * as a fee of minus 340 undecillion.
 *
 * The bound is deliberately slightly ABOVE `Single.MinValue` in magnitude terms
 * (`-3.4028234e38` is the less negative number), so the comparison `fee <= bound`
 * catches the sentinel and anything beyond it. Nothing legitimate can reach it: the
 * backing column is a service fee.
 *
 * `Number.MIN_VALUE` is NOT used and must never be: it is the smallest POSITIVE double
 * (`5e-324`), so the comparison would never match and the sentinel would leak into the
 * rendered grid with no error anywhere to show for it.
 */
const LEGACY_NULL_SINGLE_BOUND = -3.4028234e38;

/** Fractional digits for a fee, from the legacy `"##0.00"` format string. */
const FEE_FRACTION_DIGITS = 2;

// ---------------------------------------------------------------------------
// ROUTES
// ---------------------------------------------------------------------------
//
// MIGRATION: these replace `EditUrl("RoleID", "KEYFIELD", "Edit")` (`Roles.ascx.vb`
// L216-L218) and `NavigateURL(TabId, "User Roles", "RoleId=KEYFIELD")` (L225-L227) -
// two string-formatted query URLs built by substituting a placeholder token into an
// already-rendered link. That mechanism carried a whole class of defect with it. In one
// 317-line file the legacy used FOUR spellings of two concepts - `RoleGroupId` (L84),
// `RoleGroupID` (L252-L253), `RoleID` (L216) and `RoleId` (L225) - and the first pair is
// a genuine broken round trip: the edit link WRITES `RoleGroupId` while `Page_Load`
// READS `RoleGroupID`, so returning to this screen from the group editor silently lost
// the filter (defect D-R4). The tab key `"User Roles"` even contains a space. Typed
// route segments remove the substitution, the casing and the whole class of error.

/** Root path of the roles feature. */
const ROLES_PATH = '/roles';

/** Child segment listing the accounts held in one role. */
const ROLE_MEMBERS_SEGMENT = 'users';

/** Target of the legacy `AddContent.Action` module action. */
const ADD_ROLE_LINK = '/roles/new';

/**
 * This screen's own address, used to clear an account narrowing.
 *
 * Navigating here WITHOUT a query parameter is what returns the listing to every role: the router
 * matches the same route, the account input becomes absent, and the effect that watches it discards
 * the narrowed slice. No separate command is needed and no route is added.
 */
const ROLE_LIST_LINK = '/roles';

/**
 * The lead-in for the wording that names the account whose memberships are on screen.
 *
 * Authored here rather than taken from a legacy resource value, because the legacy screen had no
 * equivalent line: it rendered the account's name into its own heading through a control this
 * target does not have. The wording is therefore new, and it is deliberately plain.
 */
const ACCOUNT_SUBJECT_PREFIX = 'Roles held by account';

/** The unit for a single membership, for the count beside the account. */
const ROLE_SINGULAR = 'role';

/** The unit for none or several memberships. */
const ROLE_PLURAL = 'roles';

/** The affordance that returns the listing to every role in the tenant. */
const SHOW_ALL_ROLES_LABEL = 'Show All Roles';

/** Target of the legacy `AddGroup.Action` module action. */
const ADD_ROLE_GROUP_LINK = '/role-groups/new';

/** Target of the legacy `UserSettings.Action` module action. */
const MEMBERSHIP_SETTINGS_LINK = '/settings/membership';

// ---------------------------------------------------------------------------
// WORDING
// ---------------------------------------------------------------------------
//
// Every string below is the measured value of a legacy resource key, quoted verbatim.
// No translation runtime exists in this application - the Angular localisation package is
// out of scope and is not installed - so the wording is authored here and the resource
// files are the authority for what it says rather than the mechanism that resolves it.
//
// RESOURCE TEXT IS UNTRUSTED MARKUP and is treated as plain text throughout: a
// substantial minority of the 37 in-scope resource files carry HTML, and at least one
// carries a live script element with a remote source. Every string here is rendered
// through interpolation, which escapes it; raw-HTML binding and sanitiser bypass are
// forbidden in this file and neither appears. `Roles.ascx.resx`'s own `ModuleHelp.Text`
// contains heading and paragraph markup: it is read for context and deliberately NOT
// rendered, because the closed component library has no surface for it.

/** `Roles.ascx.resx` &rarr; `ControlTitle_.Text`. */
const PAGE_TITLE = 'Security Roles';

/**
 * `Roles.ascx.resx` &rarr; `plRoleGroups.Text`, verbatim INCLUDING the trailing colon.
 *
 * The colon is embedded in the resource value on THIS screen and absent from the same
 * key on the role editor, where it reads `Role Group`. The list-screen value is the one
 * reproduced. `form-field` strips exactly one trailing ASCII colon and applies none of
 * its own, so the value is passed through unaltered and renders without it. Stripping it
 * here as well would remove nothing and would hide where the punctuation came from.
 */
const GROUP_FILTER_LABEL = 'Filter By Role Group:';

/** `Roles.ascx.resx` &rarr; `plRoleGroups.Help`. */
const GROUP_FILTER_HELP = 'Select the Role Group you would like to view';

/** `SharedResources.resx` L834 &rarr; `AllRoles.Text`, stored as `&lt; All Roles &gt;`. */
const ALL_ROLES_OPTION_LABEL = '< All Roles >';

/** `SharedResources.resx` L837 &rarr; `GlobalRoles.Text`, stored as `&lt; Global Roles &gt;`. */
const GLOBAL_ROLES_OPTION_LABEL = '< Global Roles >';

/** `SharedResources.resx` L213 &rarr; `Edit.Text`; the accessible name of the row edit command. */
const EDIT_ROLE_LABEL = 'Edit';

/** `Roles.ascx.resx` &rarr; `UserRoles.Text`; the accessible name of the row membership command. */
const MANAGE_USERS_LABEL = 'Manage Users';

/** `Roles.ascx.resx` &rarr; `AddContent.Action`. */
const ADD_ROLE_LABEL = 'Add New Role';

/** `Roles.ascx.resx` &rarr; `AddGroup.Action`. */
const ADD_ROLE_GROUP_LABEL = 'Add New Role Group';

/** `Roles.ascx.resx` &rarr; `UserSettings.Action`. */
const MEMBERSHIP_SETTINGS_LABEL = 'User Settings';

/** `EditGroups.ascx.resx` &rarr; `ControlTitle_editgroup.Text`. */
const GROUP_EDITOR_LEGEND = 'Edit Role Group';

/** `EditGroups.ascx.resx` &rarr; `plRoleGroupName.Text`. */
const GROUP_NAME_LABEL = 'Group Name:';

/** `EditGroups.ascx.resx` &rarr; `plRoleGroupName.Help`. */
const GROUP_NAME_HELP = 'Enter the name of the role group.';

/** `EditGroups.ascx.resx` &rarr; `plDescription.Text`. */
const GROUP_DESCRIPTION_LABEL = 'Description:';

/** `EditGroups.ascx.resx` &rarr; `plDescription.Help`. */
const GROUP_DESCRIPTION_HELP = 'Enter a description of the role group.';

/**
 * `EditGroups.ascx.resx` &rarr; `valRoleGroupName.Text`, retained with its leading break
 * tag so the transformation is auditable, then normalised by the SHARED helper.
 *
 * The raw resource value is `<br>You Must Enter a Valid Name`. A leading break is a
 * layout artefact of the legacy validation summary and has no meaning in a form field,
 * so it is removed rather than escaped and shown. `stripLegacyBreakTags` already exists
 * in `core/utils/form-errors.util.ts` and handles every spelling the resource files use,
 * so it is imported; re-implementing it here would fork the rule.
 */
const GROUP_NAME_REQUIRED_MESSAGE = stripLegacyBreakTags('<br>You Must Enter a Valid Name');

/** Client-side guard message for the group name's 50-character storage limit. */
const GROUP_NAME_TOO_LONG_MESSAGE = 'A group name may be at most 50 characters.';

/** Client-side guard message for the description's 1000-character storage limit. */
const GROUP_DESCRIPTION_TOO_LONG_MESSAGE = 'A description may be at most 1000 characters.';

/**
 * `SharedResources.resx` L120 &rarr; `DeleteItem.Text`, verbatim including its question mark.
 *
 * `form-field` strips one trailing colon and preserves other punctuation, and the
 * confirmation dialog applies no punctuation policy at all, so the question mark stands.
 *
 * MIGRATION: the legacy attached this same string to the delete button through
 * `ClientAPI.AddButtonConfirm` (`Roles.ascx.vb` L86), a browser confirm dialog. It is
 * now the message of the shared confirmation dialog, which supplies the focus trap,
 * `Escape` handling, focus capture and restore, the alert-dialog role and an emit-once
 * guard. None of that is written here.
 *
 * The wording deliberately makes no claim about permanence. Removing a role group
 * releases the roles it classified; and more generally in this domain removal is not
 * always destruction - cancelling a paid role assignment whose trial has been used sets
 * an expiry date of yesterday and UPDATES the assignment rather than deleting it
 * (`RoleController.vb` L494-L497). Wording that promised irreversibility would therefore
 * be untrue as well as alarming.
 */
const DELETE_CONFIRMATION_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** Affirmative button wording for the group-removal confirmation. */
const DELETE_CONFIRMATION_LABEL = 'Delete';

/** Heading of the group-removal confirmation. */
const DELETE_CONFIRMATION_TITLE = 'Delete role group';

/**
 * The store operations whose failure belongs INLINE, on the error banner.
 *
 * A listing that could not be fetched is not transient: the screen has nothing to show
 * and the message has to persist next to the empty grid. Outcomes of operations the
 * reader initiated go to the notification queue instead, so the two channels never
 * compete for the same failure.
 */
const INLINE_FAILURE_OPERATIONS: readonly RoleStoreOperation[] = ['loadRoles', 'loadRoleGroups'];

/** Name of the group-name control, matched against the problem document's field keys. */
const GROUP_NAME_CONTROL = 'roleGroupName';

/** Name of the group-description control, matched against the problem document's field keys. */
const GROUP_DESCRIPTION_CONTROL = 'description';

/** Storage limit of `RoleGroups.RoleGroupName nvarchar(50) NOT NULL`. */
const GROUP_NAME_MAX_LENGTH = 50;

/** Storage limit of `RoleGroups.Description nvarchar(1000) NULL`. */
const GROUP_DESCRIPTION_MAX_LENGTH = 1000;

// ---------------------------------------------------------------------------
// COLUMN HEADINGS
// ---------------------------------------------------------------------------
//
// MIGRATION: `Roles.ascx.resx` carries only EIGHT `.Header` keys for TEN data columns.
// DotNetNuke localised a grid heading by its `HeaderText` VALUE - `Localization.vb`
// L1483-L1493 builds the key as `HeaderText & ".Header"` - so one `Every.Header` and one
// `Period.Header` each served two columns and the legacy grid genuinely painted "Every"
// twice and "Period" twice. Two identically named columns in one table is a real
// screen-reader hazard: a cell is announced with its column name, and an announcement of
// "Period" cannot tell a reader whether they are in the billing pair or the trial pair.
//
// The headings are therefore DISAMBIGUATED, using the legacy's own vocabulary rather
// than invented wording: `EditRoles.ascx.resx` names the same two facts
// `BillingPeriod.Text` = "Billing Period (Every)" and `TrialPeriod.Text` = "Trial Period
// (Every)", and their help text - "These two fields are used in conjunction to enter a
// Billing Period. e.g 2 weeks, or 1 month" - proves the count and the frequency code are
// one logical value. Splitting that across "Billing Every" and "Billing Period" keeps
// both halves attributable and each heading unique.
//
// The `key` of a column is its tracking identity and is separate from its label for
// exactly this reason; the shared table rejects a duplicate key outright but permits a
// duplicate label, so uniqueness of the painted heading is this screen's decision to
// make and it is made here.

/** Heading of the role-name column: `Name.Header`. */
const NAME_HEADING = 'Name';

/** Heading of the description column: `Description.Header`. */
const DESCRIPTION_HEADING = 'Description';

/** Heading of the recurring-fee column: `Fee.Header`. */
const FEE_HEADING = 'Fee';

/** Disambiguated heading of the billing-count column: `Every.Header`, first use. */
const BILLING_EVERY_HEADING = 'Billing Every';

/** Disambiguated heading of the billing-unit column: `Period.Header`, first use. */
const BILLING_PERIOD_HEADING = 'Billing Period';

/** Heading of the trial-fee column: `Trial.Header`. */
const TRIAL_HEADING = 'Trial';

/** Disambiguated heading of the trial-count column: `Every.Header`, second use. */
const TRIAL_EVERY_HEADING = 'Trial Every';

/** Disambiguated heading of the trial-unit column: `Period.Header`, second use. */
const TRIAL_PERIOD_HEADING = 'Trial Period';

/** Heading of the self-subscription column: `Public.Header`. */
const PUBLIC_HEADING = 'Public';

/** Heading of the automatic-enrolment column: `Auto.Header`. */
const AUTO_HEADING = 'Auto';

// ---------------------------------------------------------------------------
// LOCAL SHAPES
// ---------------------------------------------------------------------------

/** One entry of the role-group selector. */
interface RoleGroupOption {
  /**
   * The `option` element's value.
   *
   * A string because the DOM requires one. It is parsed back into the typed filter union
   * on change and never sent to the server.
   */
  readonly value: string;

  /** The painted text. Rendered through interpolation, so angle brackets survive intact. */
  readonly label: string;
}

/** Typed model of the inline role-group editor. */
interface RoleGroupFormModel {
  /** The group's name. Required, at most 50 characters. */
  roleGroupName: FormControl<string>;

  /**
   * The group's description, at most 1000 characters.
   *
   * Non-nullable at the control so `getRawValue()` is fully typed rather than a partial;
   * the empty string is mapped back to `null` on submission, which is what the contract
   * declares for an absent description.
   */
  description: FormControl<string>;
}

/**
 * A role-group mutation whose outcome has been requested but not yet reported.
 *
 * The store's mutators return `void` and subscribe internally, so there is no completion
 * callback to hang a notification on. This records what was asked for; a single effect
 * observes the store settling and reports the outcome once.
 */
interface AwaitedGroupMutation {
  /** Which store operation is in flight. */
  readonly operation: Extract<RoleStoreOperation, 'deleteRoleGroup' | 'updateRoleGroup'>;

  /** The group's name at the moment the request was issued, for the outcome wording. */
  readonly roleGroupName: string;
}

/**
 * The Security Roles listing screen.
 *
 * Migrated from `Website/admin/Security/roles.ascx` and its 317-line code-behind
 * `Roles.ascx.vb`. The screen lists a portal's security roles in a twelve-column grid,
 * narrows that list with a role-group selector, and offers the two per-row commands the
 * legacy grid carried plus inline editing and removal of the selected group.
 *
 * ## What this component is, and is not
 *
 * It is a presentation surface. Every fact it shows comes from `RoleStore`, every request
 * it issues goes through that store's methods, and it holds no copy of the store's state.
 * The only state owned here is genuinely local to the screen: which templates the grid
 * renders, whether the inline group editor is open, its form, and which group removal is
 * awaiting confirmation. Formatting lives here because it is presentation - that is why
 * the two legacy formatting helpers were relocated into this class rather than into the
 * closed shared component library or the two shared pipes, neither of which has a home
 * for them.
 *
 * ## The contract this class declares for its sibling template
 *
 * `role-list.component.html` binds to the protected surface below. Four `ng-template`
 * elements are REQUIRED, referenced by these exact names, and each must sit at the top
 * level of the template rather than inside an `@if` or `@for` block, because they are
 * captured by static view queries that resolve before `ngOnInit` runs:
 *
 * | Reference          | Renders                                                        |
 * | ------------------ | -------------------------------------------------------------- |
 * | `#editCommand`     | the row's edit link, bound to `editRoleLink(row)`              |
 * | `#membersCommand`  | the row's membership link, bound to `manageUsersLink(row)`     |
 * | `#publicCell`      | `{{ row.isPublic \| yesNo }}`                                   |
 * | `#autoCell`        | `{{ row.autoAssignment \| yesNo }}`                             |
 *
 * Each receives {@link DataTableCellContext}, so `let-row` (or `let-row="row"`) yields a
 * {@link RoleListItem}. The two boolean cells are template columns rather than bound
 * columns by necessity: the shared table's bound-column member accepts text-shaped values
 * only, and it imports no pipe of its own, which is why `YesNoPipe` is declared in this
 * component's own imports and used in this component's own template context.
 *
 * @see {@link https://learn.microsoft.com/aspnet/web-forms} for the superseded page model.
 */
@Component({
  selector: 'app-role-list',
  standalone: true,
  imports: [
    // Reactive forms back the inline role-group editor.
    ReactiveFormsModule,
    // Typed route segments for the two row commands and the three header actions.
    RouterLink,
    // Page heading plus the projected action bar.
    PageHeaderComponent,
    // The twelve-column grid. It renders its own spinner and empty state, so neither
    // LoadingSpinnerComponent nor EmptyStateComponent is declared here.
    DataTableComponent,
    // The group-filter row and the two controls of the inline group editor.
    FormFieldComponent,
    // The group-removal confirmation. Its presence in the DOM is what "open" means.
    ConfirmDialogComponent,
    // Inline surface for a listing that could not be fetched.
    ErrorBannerComponent,
    // Renders the two boolean columns as announced text.
    YesNoPipe,
  ],
  templateUrl: './role-list.component.html',
  styleUrl: './role-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleListComponent implements OnInit {
  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------

  /** Owns every fact this screen shows and every request it issues. */
  private readonly store = inject(RoleStore);

  /** Carries the transient outcome of a reader-initiated mutation. */
  private readonly notifications = inject(NotificationService);

  /**
   * The session projection, read for ONE fact: whether the caller administers the tenant.
   *
   * ⚠ THE RIGHT VOCABULARY FOR THIS QUESTION, AND THE PREVIOUS ONE WAS WRONG. The two create
   * affordances below were gated on the persisted permission KEY `EDIT`, which is a
   * different question over different data: the caller's permission keys are a union across
   * the pages and modules it holds rights on, and no member of that union says whether the
   * caller may create a role. Both addresses those affordances lead to are declared under
   * the tenant-administration POLICY, so that is the fact the gate has to read — the same
   * fact the route guard reads, from the same authority.
   *
   * Advisory only. The API re-authorises every request against stored state and answers 403,
   * so withholding a control here never stands in for the policy the API enforces.
   */
  private readonly auth = inject(AuthStore);

  /**
   * Whether the caller may be offered the tenant-administration affordances.
   *
   * Exposed for the template's two create links. Reads `false` while the caller's identity is
   * unresolved, which is the safe direction for an affordance gate.
   */
  protected readonly administersPortal: Signal<boolean> = this.auth.administersCurrentPortal;

  // -------------------------------------------------------------------------
  // CELL TEMPLATES
  // -------------------------------------------------------------------------
  //
  // Static queries, so they are resolved before `ngOnInit` and the column set can be
  // assembled there. An `ng-template` the host writes belongs to the host's view whether
  // or not another component ends up rendering it, which is what makes this work.

  /** Row edit command. Legacy `dnn:imagecommandcolumn commandname="Edit"`. */
  @ViewChild('editCommand', { static: true })
  private editCommandTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Row membership command. Legacy `dnn:imagecommandcolumn commandname="UserRoles"`. */
  @ViewChild('membersCommand', { static: true })
  private membersCommandTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Self-subscription cell. Legacy template column with the checked/unchecked image pair. */
  @ViewChild('publicCell', { static: true })
  private publicCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Automatic-enrolment cell. Legacy template column with the same image pair. */
  @ViewChild('autoCell', { static: true })
  private autoCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  // -------------------------------------------------------------------------
  // STATE OWNED BY THIS SCREEN
  // -------------------------------------------------------------------------

  /** Backing store of {@link columns}; populated once, in `ngOnInit`. */
  private readonly columnSet = signal<readonly DataTableColumn<RoleListItem>[]>([]);

  /** Whether the inline role-group editor is showing. */
  private readonly editorOpen = signal(false);

  /** The group whose removal is awaiting confirmation, or null when none is. */
  private readonly pendingRemoval = signal<RoleGroup | null>(null);

  /** The group mutation whose outcome has not yet been reported. */
  private readonly awaitedMutation = signal<AwaitedGroupMutation | null>(null);

  // -------------------------------------------------------------------------
  // STORE-DERIVED SURFACE
  // -------------------------------------------------------------------------
  //
  // These are the store's own signals, re-exposed under template-facing names. They are
  // NOT copies: assigning the signal shares it, so there is exactly one source of truth
  // and nothing here can drift from it.
  //
  // MIGRATION: the store replaces the legacy POST-BACK RE-BIND, not view state. This
  // screen never used view state to begin with - `roles.ascx` L23 sets
  // `EnableViewState="false"` outright, and neither `ViewState(` nor `Session(` appears
  // anywhere in `Website/admin/Security/`. What disappears is `BindData`/`BindGroups`
  // running again on every post-back to rebuild the grid from scratch.

  /**
   * The roles to paint.
   *
   * A fresh array arrives on every re-query - the store replaces the paged result rather
   * than mutating it - which is what the shared table requires, since it tracks a row by
   * object reference.
   *
   * MIGRATION: the listing endpoint is PAGED and the legacy screen was not. `roles.ascx`
   * declares no paging control and no `AllowPaging`; its grid bound a plain untyped list
   * (`Roles.ascx.vb` L77) and the code-behind carries no page index, page size or record
   * total anywhere. The pager style declared at L32 never rendered. Accordingly NO pager
   * is offered here and the shared pagination component is not consumed.
   *
   * ⚠ AND "NO PAGER" IS NOT THE SAME STATEMENT AS "ONE PAGE", WHICH IS THE WHOLE POINT.
   * Binding a single windowed response to a grid with no pager does not reproduce an
   * unpaged screen; it produces a silently TRUNCATED one, in which a portal's later roles
   * do not exist as far as an operator can tell and no affordance exists to reach them.
   * The store therefore reads EVERY page and joins them before this signal ever sees
   * them - see `core/state/role.store.ts`, `loadRoles` and its complete-listing walk - so
   * what arrives here is the whole result set under the current narrowing. This screen
   * consumes it exactly as the legacy grid consumed its untyped list, and the paging
   * metadata on the envelope is read by nobody here because one envelope now carries
   * everything.
   */
  /**
   * The account whose memberships are the subject, arriving from `?userId=` on the address.
   *
   * ⚠ A ROUTE-BOUND INPUT, not a parameter this screen reads for itself. Component input binding
   * is configured on the router, so a query parameter of this name binds here with no
   * `ActivatedRoute` subscription to manage and nothing to unsubscribe.
   *
   * Declared as `number | undefined` and TRANSFORMED FROM THE RAW STRING, because a query
   * parameter always arrives as text. Absence is `undefined` alone — never nought and never minus
   * one — because `Users.UserID` seeds at 1 in this schema but the value is nonetheless an opaque
   * key, and a screen that treated `0` as "no account" would silently ignore a real one.
   *
   * MIGRATION: the legacy account listing's roles command carried exactly this
   * (`Users.ascx.vb:L542`, `UserId=KEYFIELD`) and the screen it reached served two modes from one
   * page keyed by either a role or an account (`SecurityRoles.ascx.vb:L413-L418`). This input is
   * the account-keyed mode.
   */
  public readonly userId = input<number | undefined, string | number | undefined>(undefined, {
    transform: (value: string | number | undefined): number | undefined => {
      if (value === undefined || value === '') {
        return undefined;
      }

      const parsed: number = typeof value === 'number' ? value : Number(value);

      // A parameter that is not a number at all is treated as absent rather than as account NaN,
      // which would be forwarded to the transport and refused with a confusing message.
      return Number.isInteger(parsed) ? parsed : undefined;
    },
  });

  /**
   * The rows the grid renders: the account's memberships when an account is the subject, and the
   * browsable listing otherwise.
   *
   * ⚠ THE ACCOUNT-NARROWED SLICE IS USED ONLY ONCE IT DESCRIBES THE ACCOUNT ASKED ABOUT. The store
   * records the account at dispatch, so during a read of account B the slice may still hold account
   * A's answer; rendering that would show one person's memberships under another's name. Until the
   * two agree the browsable listing is shown, which is also what a failed read falls back to.
   */
  protected readonly roles = computed<readonly RoleListItem[]>(() => {
    const subject: number | undefined = this.userId();
    const held: readonly RoleListItem[] | null = this.store.rolesHeldByUser();

    if (subject === undefined || held === null || this.store.heldRolesUserId() !== subject) {
      return this.store.roleItems();
    }

    return held;
  });

  /** Whether an account is the subject of the listing. */
  protected readonly narrowedToAccount = computed<boolean>(() => this.userId() !== undefined);

  /** The portal's role groups, in the order the endpoint returned them. Unpaged, as the legacy was. */
  protected readonly roleGroups = this.store.roleGroups;

  /**
   * Whether the roles request is in flight.
   *
   * Handed to the shared table, which renders the spinner and the empty state itself in a
   * single spanning row and lets loading win over empty. Neither is rendered directly
   * here, and no custom empty wording is supplied: the legacy grid showed no empty-state
   * message at all, so the component's own default is the closest thing to parity.
   */
  protected readonly loading = computed<boolean>(
    () => this.store.rolesLoading() || this.store.heldRolesLoading(),
  );

  /** Whether a mutation is in flight; disables the editor and the removal affordance. */
  protected readonly saving = this.store.saving;

  /** The currently selected group, or null when a sentinel filter is active. */
  protected readonly selectedRoleGroup = this.store.selectedRoleGroup;

  /**
   * Whether the whole group-filter row is shown.
   *
   * Reproduces `Roles.ascx.vb` L110/L127-L130 exactly: a portal with no role groups gets
   * `trGroups.Visible = False`, and the effective filter falls back to "all roles". BOTH
   * halves matter and the second is easy to miss - the store applies the fallback itself
   * when the group list comes back empty, so the grid lists every role rather than the
   * ungrouped subset while the row that would have explained the narrowing is hidden.
   */
  protected readonly filterRowVisible = this.store.hasRoleGroups;

  /**
   * Whether the selected group may be edited inline.
   *
   * Reproduces the first delete guard, `Roles.ascx.vb` L79-L84: when the filter is either
   * sentinel the legacy set `lnkEditGroup.Visible = False` and `cmdDelete.Visible = False`
   * together, because neither pseudo-entry is a resource that can be edited or removed.
   * The store's identifier is null for both sentinels and non-null only for a real group,
   * so the test is an explicit null comparison and never a truthiness or sign test - group
   * zero is a real identifier, `RoleGroups.RoleGroupID` being seeded `IDENTITY(0, 1)`.
   */
  protected readonly canEditSelectedGroup = computed<boolean>(
    () => this.store.selectedRoleGroupId() !== null,
  );

  /**
   * Whether the removal affordance is offered for the selected group.
   *
   * Reproduces the second delete guard, `Roles.ascx.vb` L85:
   * `cmdDelete.Visible = Not (arrRoles.Count > 0)` - removal is offered only while the
   * selected group holds no roles. The store computes it from the same two facts.
   *
   * This governs the AFFORDANCE only. The server is authoritative and refuses a group
   * that still classifies roles with `409`, and that refusal is handled whether or not
   * the affordance was ever shown: hiding a control is not enforcement, and the legacy
   * gap this closes was that any caller issuing the post-back directly walked straight
   * through the hidden button.
   */
  protected readonly canRemoveSelectedGroup = this.store.canDeleteSelectedGroup;

  /** Whether the inline role-group editor is showing. */
  protected readonly groupEditorOpen = this.editorOpen.asReadonly();

  /** The group awaiting removal confirmation; its presence is what opens the dialog. */
  protected readonly pendingGroupRemoval = this.pendingRemoval.asReadonly();

  /** The twelve columns, assembled in `ngOnInit` once the cell templates exist. */
  protected readonly columns = this.columnSet.asReadonly();

  /**
   * The selector's entries, in the legacy order.
   *
   * `Roles.ascx.vb` L112-L126 uses `Items.Add` throughout rather than `Items.Insert`, so
   * the order is "all roles", then "global roles", then each group as `GetRoleGroups`
   * returned it. That order is reproduced literally.
   */
  protected readonly groupOptions = computed<readonly RoleGroupOption[]>(() => {
    const sentinels: readonly RoleGroupOption[] = [
      { value: String(ALL_ROLES_FILTER_VALUE), label: ALL_ROLES_OPTION_LABEL },
      { value: String(GLOBAL_ROLES_FILTER_VALUE), label: GLOBAL_ROLES_OPTION_LABEL },
    ];

    const groups: readonly RoleGroupOption[] = this.store
      .roleGroups()
      .map((group) => ({ value: String(group.roleGroupId), label: group.roleGroupName }));

    return [...sentinels, ...groups];
  });

  /**
   * The selector's current value, derived from the filter the store actually holds.
   *
   * MIGRATION: this corrects defect D-R3 by construction. `Roles.ascx.vb` L114-L117 built
   * the "global roles" entry and then marked it selected with `If RoleGroupId < 0`, a test
   * that conflates minus one with minus two - so with "all roles" active the selector read
   * "Global Roles" while the grid showed every role. It should have compared for equality.
   * Binding the control to the filter itself makes the two incapable of disagreeing, which
   * is a documented divergence in rendered behaviour rather than a fix applied to legacy
   * logic: no legacy branch is altered, and the defect is recorded rather than silently
   * absorbed.
   */
  protected readonly groupFilterValue = computed<string>(() => {
    const filter: RoleGroupFilter = this.store.groupFilter();

    switch (filter.kind) {
      case 'AllRoles':
        return String(ALL_ROLES_FILTER_VALUE);
      case 'GlobalRoles':
        return String(GLOBAL_ROLES_FILTER_VALUE);
      case 'Group':
        return String(filter.roleGroupId);
      default:
        return String(GLOBAL_ROLES_FILTER_VALUE);
    }
  });

  /**
   * The problem document to show inline, or null when nothing failed to load.
   *
   * Restricted to the two fetch operations: an outcome the reader asked for belongs in the
   * notification queue, so the two channels never report the same failure twice. When the
   * store recorded a failure it could not parse into a document - a transport error with
   * no body, for instance - a minimal document is synthesised from the summary the store
   * did produce, because a banner bound to null renders nothing and a listing that failed
   * silently is worse than one that explains itself.
   *
   * The banner derives its own severity, renders the field-error map as a summary and
   * shows the support reference itself, so none of that is done here.
   */
  protected readonly loadProblem = computed<ProblemDetails | null>(() => {
    const failure: RoleStoreFailure | null = this.store.failure();

    if (failure === null) {
      return null;
    }

    if (!INLINE_FAILURE_OPERATIONS.some((operation) => operation === failure.operation)) {
      return null;
    }

    if (failure.problem !== null) {
      return failure.problem;
    }

    const status: number | null = failure.summary.status;
    const described: ProblemDetails =
      status === null
        ? { title: failure.summary.title, detail: failure.summary.message }
        : { title: failure.summary.title, detail: failure.summary.message, status };

    return described;
  });

  /** The problem document from the most recent group update, for per-field messages. */
  private readonly editorProblem = computed<ProblemDetails | null>(() => {
    const failure: RoleStoreFailure | null = this.store.failure();

    if (failure === null || failure.operation !== 'updateRoleGroup') {
      return null;
    }

    return failure.problem;
  });

  /**
   * Server-reported messages against the group name, if any.
   *
   * Read through the shared field-message reader rather than by indexing the document.
   * That matters twice over: the reader matches a leaf key as well as an exact one, so a
   * server key of `request.roleGroupName` still resolves; and the errors map is an index
   * signature, which the workspace forbids reaching into with dot access, so bracket
   * access would otherwise be mandatory at every site. Doing it in one shared place is
   * better than doing it correctly in many.
   */
  private readonly groupNameServerMessages = computed<readonly string[]>(() =>
    fieldErrorMessages(this.editorProblem(), GROUP_NAME_CONTROL),
  );

  /** Server-reported messages against the group description, if any. */
  private readonly groupDescriptionServerMessages = computed<readonly string[]>(() =>
    fieldErrorMessages(this.editorProblem(), GROUP_DESCRIPTION_CONTROL),
  );

  // -------------------------------------------------------------------------
  // THE INLINE ROLE-GROUP EDITOR
  // -------------------------------------------------------------------------
  //
  // The legacy filter row carried an edit link to a separate `EditGroup` screen
  // (`Roles.ascx.vb` L84). The migrated route table is closed and declares no group-edit
  // route, so the two facts a group owns are edited in place here instead, composed from
  // the shared labelled-field and confirmation components. Composing beats importing the
  // sibling create screen: that would pull a second feature screen into this lazily
  // loaded chunk for the sake of two text inputs, and `role-group-form/` is in any case
  // not yet populated. No route is declared here; routing belongs to `app.routes.ts` and
  // to this feature's own route file.
  //
  // Every control is `nonNullable`, so `getRawValue()` is fully typed rather than a
  // partial and a reset returns each control to its initial value instead of to null.

  /** Typed form backing the inline editor. */
  protected readonly groupForm = new FormGroup<RoleGroupFormModel>({
    roleGroupName: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(GROUP_NAME_MAX_LENGTH)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(GROUP_DESCRIPTION_MAX_LENGTH)],
    }),
  });

  // -------------------------------------------------------------------------
  // WORDING AND LINKS EXPOSED TO THE TEMPLATE
  // -------------------------------------------------------------------------

  /** Page heading: `ControlTitle_.Text`. */
  protected readonly pageTitle = PAGE_TITLE;

  /**
   * The wording beneath the heading when an account is the subject, or `undefined` otherwise.
   *
   * ⚠ THE ACCOUNT IS NAMED BY ITS IDENTIFIER, NOT BY ITS DISPLAY NAME, and that is a deliberate
   * limit rather than an oversight. The read this screen makes returns ROLES; it carries no account
   * name, and this feature holds no account transport of its own. Fetching one would mean either
   * importing the account feature's service — which no feature here does — or adding a second
   * request for one label. The identifier is what the address carries and what the operator
   * navigated with, so it is what is stated, and it is enough to confirm the listing is narrowed and
   * to which key.
   *
   * The count is included because it is the fact the operator came for, and it is read from the rows
   * actually rendered rather than from the store, so it cannot disagree with the grid.
   */
  protected readonly accountSubtitle = computed<string | undefined>(() => {
    const subject: number | undefined = this.userId();

    if (subject === undefined) {
      return undefined;
    }

    // Reads the narrowed rows, so while the read is in flight this says nothing about a count it
    // does not yet have.
    const held: readonly RoleListItem[] | null = this.store.rolesHeldByUser();

    if (held === null || this.store.heldRolesUserId() !== subject) {
      return `${ACCOUNT_SUBJECT_PREFIX} ${subject}`;
    }

    return `${ACCOUNT_SUBJECT_PREFIX} ${subject} — ${held.length} ${
      held.length === 1 ? ROLE_SINGULAR : ROLE_PLURAL
    }`;
  });

  /** The wording of the affordance that returns to the unnarrowed listing. */
  protected readonly showAllRolesLabel = SHOW_ALL_ROLES_LABEL;

  /** The address of the unnarrowed listing, which is this screen without its query parameter. */
  protected readonly showAllRolesLink = ROLE_LIST_LINK;

  /** Filter label, colon included; the shared field strips it on display. */
  protected readonly groupFilterLabel = GROUP_FILTER_LABEL;

  /** Filter help text: `plRoleGroups.Help`. */
  protected readonly groupFilterHelp = GROUP_FILTER_HELP;

  /** Identifier the filter label is associated with; bind it to the `select` as well. */
  protected readonly groupFilterControlId = 'role-list-group-filter';

  /** Legend of the inline editor. */
  protected readonly groupEditorLegend = GROUP_EDITOR_LEGEND;

  /** Group-name label and help, and the identifier that ties them to the input. */
  protected readonly groupNameLabel = GROUP_NAME_LABEL;

  /** Group-name help text. */
  protected readonly groupNameHelp = GROUP_NAME_HELP;

  /** Identifier of the group-name input. */
  protected readonly groupNameControlId = 'role-list-group-name';

  /** Group-description label. */
  protected readonly groupDescriptionLabel = GROUP_DESCRIPTION_LABEL;

  /** Group-description help text. */
  protected readonly groupDescriptionHelp = GROUP_DESCRIPTION_HELP;

  /** Identifier of the group-description input. */
  protected readonly groupDescriptionControlId = 'role-list-group-description';

  /** Maximum length accepted by the group-name input. */
  protected readonly groupNameMaxLength = GROUP_NAME_MAX_LENGTH;

  /** Maximum length accepted by the group-description input. */
  protected readonly groupDescriptionMaxLength = GROUP_DESCRIPTION_MAX_LENGTH;

  /** Accessible name of the inline edit affordance: `SharedResources.resx` `Edit.Text`. */
  protected readonly editGroupLabel = EDIT_ROLE_LABEL;

  /**
   * Accessible name of the group-removal affordance.
   *
   * MIGRATION: the legacy `cmdDelete` image button (`roles.ascx` L13) carried NEITHER a
   * resource key NOR alternative text, so it reached assistive technology with no
   * accessible name at all - a reader met an unlabelled button that destroyed a record.
   * That is the real accessibility gap on this screen and it is closed here. By contrast
   * `imgEditGroup` (L11) carried both `AlternateText="Edit"` and `resourcekey="Edit"`, and
   * although `Roles.ascx.resx` declares no local `Edit` key, resolution fell back to
   * `SharedResources.resx` where `Edit.Text` exists at L213 - so that affordance WAS
   * labelled and this migration claims no credit for it.
   */
  protected readonly removeGroupLabel = 'Delete role group';

  /** Accessible name of the row edit command. */
  protected readonly editRoleLabel = EDIT_ROLE_LABEL;

  /** Accessible name of the row membership command: `UserRoles.Text`. */
  protected readonly manageUsersLabel = MANAGE_USERS_LABEL;

  /** Header action: `AddContent.Action`. */
  protected readonly addRoleLabel = ADD_ROLE_LABEL;

  /** Header action target. */
  protected readonly addRoleLink = ADD_ROLE_LINK;

  /** Header action: `AddGroup.Action`. */
  protected readonly addRoleGroupLabel = ADD_ROLE_GROUP_LABEL;

  /** Header action target. */
  protected readonly addRoleGroupLink = ADD_ROLE_GROUP_LINK;

  /** Header action: `UserSettings.Action`. */
  protected readonly membershipSettingsLabel = MEMBERSHIP_SETTINGS_LABEL;

  /** Header action target. */
  protected readonly membershipSettingsLink = MEMBERSHIP_SETTINGS_LINK;

  /** Heading of the removal confirmation. */
  protected readonly removalTitle = DELETE_CONFIRMATION_TITLE;

  /** Body of the removal confirmation: `DeleteItem.Text`. */
  protected readonly removalMessage = DELETE_CONFIRMATION_MESSAGE;

  /** Affirmative button wording of the removal confirmation. */
  protected readonly removalConfirmLabel = DELETE_CONFIRMATION_LABEL;

  // -------------------------------------------------------------------------
  // THE OUTCOME BRIDGE
  // -------------------------------------------------------------------------

  /**
   * Reports the outcome of a role-group mutation to the notification queue.
   *
   * An effect, because emitting a user-visible notification IS a side effect - the store's
   * mutators return `void` and subscribe internally, so there is no completion callback to
   * hang one on. This effect loads no data: `ngOnInit` does that, and using an effect as a
   * loader is exactly the anti-pattern this avoids.
   *
   * Writing the cleared marker inside `untracked` keeps the write out of this effect's own
   * dependency set. The store clears any recorded failure before each mutation begins, so
   * a failure still present once the mutation has settled belongs to that mutation; the
   * operation is matched as well, because a successful removal triggers a reload whose own
   * failure must not be misreported as a failed removal.
   */
  constructor() {
    effect(() => {
      const awaited: AwaitedGroupMutation | null = this.awaitedMutation();
      const inFlight: boolean = this.store.saving();
      const failure: RoleStoreFailure | null = this.store.failure();

      if (awaited === null || inFlight) {
        return;
      }

      untracked(() => {
        this.awaitedMutation.set(null);
        this.reportGroupOutcome(awaited, failure);
      });
    });
  }

  // -------------------------------------------------------------------------
  // LIFECYCLE
  // -------------------------------------------------------------------------

  /**
   * Fetches the role groups and then the roles.
   *
   * Reproduces `Roles.ascx.vb` L249-L261, where `Page_Load` called `BindGroups`, which
   * populated the selector and then called `BindData` at L133. The order is load-bearing:
   * the no-groups fallback at L129 can only be applied once the group list is known, and
   * that fallback decides which roles the grid then asks for. The store's combined loader
   * sequences the two requests for exactly that reason.
   *
   * The cell templates are captured by static view queries, so they are resolved by the
   * time this runs and the column set can be assembled here. Data is loaded from a
   * lifecycle hook rather than from an effect: an effect that fetched would fire again on
   * every unrelated signal change it happened to read.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());
    this.store.loadRoleAdministration();
  }

  /**
   * Keeps the account-narrowed slice in step with the address.
   *
   * A genuine side effect — it issues a read — so an effect is the right mechanism rather than a
   * computed. Registered in the field initialiser so it runs in this component's injection context
   * and is torn down with it.
   *
   * Navigating BETWEEN two accounts does not recreate this component, because both addresses match
   * the same route: the input changes and this effect re-reads, which is exactly the case the
   * store's dispatch-time account recording exists to make safe.
   *
   * `untracked` guards the store call so that nothing the command writes is mistaken for a
   * dependency of this effect, which would otherwise re-enter it.
   */
  private readonly accountSubjectEffect = effect((): void => {
    const subject: number | undefined = this.userId();

    untracked((): void => {
      if (subject === undefined) {
        this.store.clearRolesHeldByUser();

        return;
      }

      this.store.loadRolesHeldByUser(subject);
    });
  });

  // -------------------------------------------------------------------------
  // ROW COMMAND TARGETS
  // -------------------------------------------------------------------------

  /**
   * The editor route of every role on the page, keyed by identifier.
   *
   * PRECOMPUTED ONCE PER PAGE rather than per row per change-detection pass, and bound as an
   * index rather than called. Twelve columns are rendered per row and a router link is
   * compared by IDENTITY, so an array built afresh on each pass is a new reference every
   * time: the router re-parses a target that has not changed, for every row, on every pass.
   * Deriving the lookup from the page means the references change only when the rows do,
   * which is the whole point of rendering this screen with push change detection.
   *
   * A plain object rather than a map, because a template can index one and cannot call
   * `Map.get`; the point is to remove the per-pass call, so an indexed read is what the
   * binding needs. Bounded by construction: one entry per row of the CURRENT page, rebuilt
   * rather than appended to whenever the page changes.
   *
   * The arrays are intentionally MUTABLE rather than `readonly`: the router's link input is
   * declared as `any[] | string | UrlTree | null | undefined`, and a read-only array is not
   * assignable to it, so a read-only element type would fail the strict template check at
   * the binding site.
   *
   * The identifier is used as the key and interpolated exactly as received. `Roles.RoleID`
   * is declared with an identity seed of ZERO, so the first role of every tenant is keyed
   * `0` and a falsy-looking key is a legitimate one.
   *
   * Replaces `Roles.ascx.vb` L216-L218, which built `EditUrl("RoleID", "KEYFIELD", "Edit")`
   * with a dummy token and then substituted a format placeholder into the rendered URL.
   */
  protected readonly editRoleLinks: Signal<Readonly<Record<number, (string | number)[]>>> =
    computed(() => {
      const links: Record<number, (string | number)[]> = {};

      for (const role of this.roles()) {
        links[role.roleId] = [ROLES_PATH, role.roleId];
      }

      return links;
    });

  /**
   * The membership route of every role on the page, keyed by identifier.
   *
   * Precomputed on the same terms and for the same reason as {@link editRoleLinks}.
   *
   * Replaces `Roles.ascx.vb` L225-L227, which built
   * `NavigateURL(TabId, "User Roles", "RoleId=KEYFIELD")` - a tab key containing a space,
   * substituted into a query string.
   */
  protected readonly manageUsersLinks: Signal<Readonly<Record<number, (string | number)[]>>> =
    computed(() => {
      const links: Record<number, (string | number)[]> = {};

      for (const role of this.roles()) {
        links[role.roleId] = [ROLES_PATH, role.roleId, ROLE_MEMBERS_SEGMENT];
      }

      return links;
    });

  // -------------------------------------------------------------------------
  // THE COLUMN SET
  // -------------------------------------------------------------------------

  /**
   * Assembles the twelve columns in the legacy order.
   *
   * `roles.ascx` L33-L78 declares exactly twelve: two `dnn:imagecommandcolumn` commands
   * (L34, L35) followed by ten data columns. Two details of that ordering are load-bearing
   * and easy to get wrong. The count comes BEFORE the unit in both the billing pair and the
   * trial pair - period then frequency, twice - and the column headed "Trial" binds
   * `TrialFee` (L55), not `TrialPeriod`, so the two trial money and count columns are not
   * adjacent in the way the headings suggest.
   *
   * There is deliberately NO row-level delete. The legacy grid declares no delete command
   * at all; role removal lives on the role editor (`editroles.ascx` L185). The dead branch
   * at `Roles.ascx.vb` L208-L210, which configured a confirmation for a `"Delete"` image
   * column that the markup never declared, is not ported - it could not have executed.
   *
   * No column is marked sortable and no sort binding is offered, matching the legacy grid,
   * which had neither a sort affordance nor a text filter nor the letter filter that the
   * portal list carried. Adding one would be a capability the screen never had.
   *
   * ALIGNMENT reproduces two independent legacy declarations. The grid's heading style
   * (`roles.ascx` L26) is centred with a top vertical alignment; its item style (L27) starts
   * inline. The shared table resolves heading and body alignment independently from separate
   * members, so both are stated on every column rather than one being derived from the other.
   * Columns five and eight - the two frequency columns - additionally declare their own bare
   * item style (L51, L64) carrying a CSS class and NO horizontal alignment; whether that is
   * read as inheriting the grid's start alignment through style merging or as resolving to the
   * default, the rendered result is the same start alignment, so it is stated explicitly and
   * annotated rather than left to look like an oversight.
   *
   * No width is declared on any column: the legacy grid sized itself to the full width and
   * declared no per-column width. The shared table accepts only a percentage, an intrinsic
   * keyword or a custom-property reference, and rejects pixels outright.
   *
   * @returns The twelve columns, in legacy order.
   * @throws Error if a required cell template is missing from the sibling template file.
   */
  private buildColumns(): readonly DataTableColumn<RoleListItem>[] {
    return [
      // 1-2. The two command columns. Header-less in the legacy, because neither
      // `imagecommandcolumn` declared `HeaderText` and `Localization.vb` L1483-L1493 built
      // its heading key from that value - so a column with no heading text was skipped
      // entirely. Each still carries a label here and hides it, which keeps the column
      // named in the accessibility tree while painting nothing: a cell is announced with
      // its column name either way, so nothing is lost and an unlabelled command column is
      // avoided. The actions kind also suppresses row activation, so pressing Edit never
      // doubles as selecting the row.
      {
        key: 'editCommand',
        label: this.editRoleLabel,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.editCommandTemplate, 'editCommand'),
      },
      {
        key: 'membersCommand',
        label: this.manageUsersLabel,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.membersCommandTemplate, 'membersCommand'),
      },

      // 3. `asp:boundcolumn DataField="RoleName"`.
      {
        key: 'roleName',
        label: NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'roleName',
      },

      // 4. `asp:boundcolumn DataField="Description"`. Nullable on the contract; the shared
      // table renders an absent value as empty rather than as the word "null".
      {
        key: 'description',
        label: DESCRIPTION_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'description',
      },

      // 5. Template column over `FormatPrice(ServiceFee)`.
      {
        key: 'serviceFee',
        label: FEE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        value: (role: RoleListItem): string => this.formatPrice(role.serviceFee),
      },

      // 6. Template column over `FormatPeriod(BillingPeriod)`. The COUNT, before its unit.
      {
        key: 'billingPeriod',
        label: BILLING_EVERY_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        value: (role: RoleListItem): string => this.formatPeriod(role.billingPeriod),
      },

      // 7. `asp:boundcolumn DataField="BillingFrequency"`, with the bare item style noted
      // above. The single persisted character is rendered VERBATIM, exactly as the legacy
      // did: the schema seeds a frequency lookup table and the DDL even joins it, but this
      // grid binds the raw field and not the joined description, so `M` renders as `M` and
      // never as `Month`. The code is load-bearing data and is never renamed, case-folded,
      // aliased or turned into a number.
      {
        key: 'billingFrequency',
        label: BILLING_PERIOD_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'billingFrequency',
      },

      // 8. Template column over `FormatPrice(TrialFee)` - the FEE, despite the heading
      // reading "Trial"; verified at `roles.ascx` L55.
      {
        key: 'trialFee',
        label: TRIAL_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        value: (role: RoleListItem): string => this.formatPrice(role.trialFee),
      },

      // 9. Template column over `FormatPeriod(TrialPeriod)`. The COUNT, before its unit.
      {
        key: 'trialPeriod',
        label: TRIAL_EVERY_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        value: (role: RoleListItem): string => this.formatPeriod(role.trialPeriod),
      },

      // 10. `asp:boundcolumn DataField="TrialFrequency"`, the second bare item style.
      {
        key: 'trialFrequency',
        label: TRIAL_PERIOD_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'trialFrequency',
      },

      // 11-12. The two boolean columns.
      //
      // MIGRATION: the legacy rendered each as a PAIR of image elements with opposite
      // visibility, driven by `DataBinder.Eval(Container.DataItem,"IsPublic")="true"` and
      // the same expression against `"false"` (`roles.ascx` L68-L69, L74-L75). That is a
      // comparison of a late-bound object against a lower-case string literal. VB's
      // `Boolean.ToString` yields `"True"` with a capital letter, and the library compiles
      // with `OptionCompare Binary` (`DotNetNuke.Library.vbproj` L22), under which a genuine
      // string comparison of `"True"` with `"true"` is FALSE. The expressions worked only
      // because the pages compile with Option Strict OFF - `release.config` L125 declares
      // `strict="false"` - so VB resolved the late-bound comparison by converting the STRING
      // to a Boolean instead. The grid bound a plain untyped list (`roles.ascx` L77), which
      // is what made the binding late-bound in the first place. Legacy correctness here was
      // accidental.
      //
      // In TypeScript the equivalent string comparison is unconditionally false, so the
      // boolean is tested by identity instead - which is what the shared pipe does, and why
      // its input is a non-nullable `boolean`. `Null.NullBoolean` is literally `False` and
      // the legacy null test returned true for it, so `false` IS DATA here: it renders as
      // the negative word and never as blank, and it is never reached through a truthiness
      // test, a negation, a coalesce or a cast.
      //
      // Neither legacy image carried alternative text, so both states reached assistive
      // technology as an unnamed graphic. One cell of announced text replaces the pair,
      // which closes that gap at no visual cost. These are template columns rather than
      // bound columns because the shared table's bound member accepts text-shaped values
      // only and imports no pipe of its own.
      {
        key: 'isPublic',
        label: PUBLIC_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.publicCellTemplate, 'publicCell'),
      },
      {
        key: 'autoAssignment',
        label: AUTO_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.autoCellTemplate, 'autoCell'),
      },
    ];
  }

  // -------------------------------------------------------------------------
  // THE GROUP FILTER
  // -------------------------------------------------------------------------

  /**
   * Applies a new group filter.
   *
   * Reproduces `Roles.ascx.vb` L273-L278: a change of selection re-queried the ROLES only.
   * It called `BindData` and never `BindGroups`, so the selector's own contents were left
   * alone. The store's filter setter does exactly that, and the distinction matters - a
   * filter change that also refetched the group list would reset the very control the
   * reader had just used.
   *
   * MIGRATION: the legacy read the selection with a bare `Int32.Parse` (L275, and again at
   * L253 over a query-string value), which throws on anything unparseable. The parse here
   * is guarded and falls back to the default filter, so a tampered or stale option value
   * narrows the listing harmlessly instead of failing the screen.
   *
   * @param event The change event raised by the selector.
   */
  protected onGroupFilterChange(event: Event): void {
    const target: EventTarget | null = event.target;

    if (!(target instanceof HTMLSelectElement)) {
      return;
    }

    this.store.setGroupFilter(this.parseGroupFilter(target.value));
  }

  /**
   * Refetches the group list and the roles.
   *
   * Offered beside a failed listing so a transient fault does not require a navigation.
   */
  protected reload(): void {
    this.store.clearError();
    this.store.loadRoleAdministration();
  }

  // -------------------------------------------------------------------------
  // THE INLINE GROUP EDITOR
  // -------------------------------------------------------------------------

  /**
   * Opens the editor on the selected group.
   *
   * Does nothing when no real group is selected, which mirrors the first delete guard: the
   * legacy hid the edit link entirely for both sentinel filters (`Roles.ascx.vb` L79-L81),
   * so there was nothing to activate. Guarding the handler as well as the affordance means
   * a stray activation cannot open an editor over a pseudo-entry.
   */
  protected openGroupEditor(): void {
    const group: RoleGroup | null = this.selectedRoleGroup();

    if (group === null) {
      return;
    }

    this.store.clearError();
    this.groupForm.reset({
      roleGroupName: group.roleGroupName,
      // An absent description is `null` on the contract and the empty string in the
      // control. The mapping is written as an explicit comparison rather than a coalesce,
      // because in this domain the empty string is itself the legacy marker for an absent
      // string (`Null.NullString` returns `""`, not null) and the two must stay visibly
      // distinguishable at every boundary that crosses between them.
      description: group.description === null ? '' : group.description,
    });
    this.editorOpen.set(true);
  }

  /** Abandons the editor without submitting. */
  protected closeGroupEditor(): void {
    this.editorOpen.set(false);
  }

  /**
   * Submits the edited group.
   *
   * Sends `PUT /api/v1/role-groups/{roleGroupId}` through the store. The editor closes
   * optimistically about the DIALOG only, never about the DATA: the store replaces the
   * group in its own list from the response, and a refusal is reported through the
   * notification queue with the field messages left available to the reopened editor.
   */
  protected submitGroupEditor(): void {
    if (this.saving()) {
      return;
    }

    const group: RoleGroup | null = this.selectedRoleGroup();

    if (group === null) {
      return;
    }

    if (this.groupForm.invalid) {
      this.groupForm.markAllAsTouched();
      return;
    }

    const value = this.groupForm.getRawValue();
    const trimmedName: string = value.roleGroupName.trim();
    const trimmedDescription: string = value.description.trim();

    this.awaitedMutation.set({ operation: 'updateRoleGroup', roleGroupName: trimmedName });
    this.store.updateRoleGroup(group.roleGroupId, {
      roleGroupName: trimmedName,
      description: trimmedDescription.length === 0 ? null : trimmedDescription,
    });
    this.editorOpen.set(false);
  }

  /** Messages to show beside the group-name control. */
  protected get groupNameMessages(): readonly string[] {
    const control = this.groupForm.controls.roleGroupName;

    if (control.invalid && (control.dirty || control.touched)) {
      return control.hasError('required')
        ? [GROUP_NAME_REQUIRED_MESSAGE]
        : [GROUP_NAME_TOO_LONG_MESSAGE];
    }

    return this.groupNameServerMessages();
  }

  /** Messages to show beside the group-description control. */
  protected get groupDescriptionMessages(): readonly string[] {
    const control = this.groupForm.controls.description;

    if (control.invalid && (control.dirty || control.touched)) {
      return [GROUP_DESCRIPTION_TOO_LONG_MESSAGE];
    }

    return this.groupDescriptionServerMessages();
  }

  // -------------------------------------------------------------------------
  // GROUP REMOVAL
  // -------------------------------------------------------------------------

  /**
   * Asks for confirmation before removing the selected group.
   *
   * Reproduces the confirmation the legacy attached to its delete button through
   * `ClientAPI.AddButtonConfirm` (`Roles.ascx.vb` L86). The pending group is what puts the
   * shared dialog in the document, and the dialog's presence there IS its open state - it
   * exposes no open input to set. This feature owns the flow; the dialog only asks.
   */
  protected requestGroupRemoval(): void {
    const group: RoleGroup | null = this.selectedRoleGroup();

    if (group === null) {
      return;
    }

    this.store.clearError();
    this.pendingRemoval.set(group);
  }

  /**
   * Removes the confirmed group.
   *
   * Reproduces the third delete guard, `Roles.ascx.vb` L292-L297: removal proceeded only
   * for a real group, the filter was then reset to "global roles", and the selector was
   * rebound. The store performs all three - it resets the filter to the default and reloads
   * groups and roles together - so none of it is repeated here.
   *
   * The refusal the server raises for a group that still classifies roles is handled even
   * though the affordance is hidden in that case, because the affordance is not enforcement.
   */
  protected onGroupRemovalConfirmed(): void {
    const group: RoleGroup | null = this.pendingRemoval();

    this.pendingRemoval.set(null);

    if (group === null || this.saving()) {
      return;
    }

    // The removal is NOT gated on the affordance's own guard. The affordance is hidden for
    // a group that still holds roles, but hiding a control is not enforcement and the
    // grid's view of the group's contents can be stale; the server decides, and its
    // refusal is reported. Gating here would instead swallow a legitimate attempt.
    this.awaitedMutation.set({
      operation: 'deleteRoleGroup',
      roleGroupName: group.roleGroupName,
    });
    this.store.deleteRoleGroup(group.roleGroupId);
  }

  /** Dismisses the confirmation without removing anything. */
  protected onGroupRemovalCancelled(): void {
    this.pendingRemoval.set(null);
  }

  // -------------------------------------------------------------------------
  // FORMATTING
  // -------------------------------------------------------------------------
  //
  // MIGRATION: both helpers are relocated from `Roles.ascx.vb` - `FormatPeriod` at
  // L152-L162 and `FormatPrice` at L175-L185, where they were public members of the page
  // called from the grid's own item templates. They live in this component because
  // formatting is presentation and because there is nowhere else for them: the shared
  // component library is closed at ten members and the two shared pipes cover a boolean and
  // a date, neither of which is a fee or a period count. Neither belongs in the store,
  // which holds data rather than its rendering.
  //
  // Both are PURE, side-effect-free and TOTAL. They are invoked from inside the shared
  // table's own memoised projection, once per row per redraw, where a side effect would
  // fire at an unpredictable point in change detection and a throw would take out the whole
  // projection. The legacy bodies each wrapped their one statement in a handler that
  // returned the empty string on failure; no equivalent is written here because neither
  // body has a throwing path to guard - reproducing the handler would be dead structure
  // rather than fidelity.

  /**
   * Renders a period count, filtering out the legacy absent-value marker.
   *
   * Legacy body: `If period <> Null.NullInteger Then _FormatPeriod = period.ToString`,
   * starting from `Null.NullString` - which is the EMPTY STRING and not null - so an absent
   * period rendered as an empty cell.
   *
   * Zero is a REAL, displayed value and renders as `0`. Only minus one is absent.
   *
   * MIGRATION: the legacy call site passed `DataBinder.Eval(...)`, an `Object`, into an
   * `Integer` parameter - an implicit narrowing conversion at the call site, legal only
   * because the pages compiled with Option Strict off. The conversion is explicit here: the
   * parameter is typed, the contract types the member `number | null`, and both the null and
   * the sentinel forms are rejected by name.
   *
   * @param period The period count from the row, the legacy sentinel, or null.
   * @returns The count as text, or the empty string when absent. Never null or undefined.
   */
  private formatPeriod(period: number | null): string {
    if (period === null) {
      return '';
    }

    if (!Number.isFinite(period)) {
      return '';
    }

    if (period === LEGACY_NULL_INTEGER) {
      return '';
    }

    return String(period);
  }

  /**
   * Renders a fee, filtering out the legacy absent-value marker.
   *
   * Legacy body: `If price <> Null.NullSingle Then _FormatPrice = price.ToString("##0.00")`,
   * again starting from the empty string.
   *
   * A fee of zero is a REAL, displayed value and renders as `0.00`. It is emphatically not
   * "absent" and never "Free": zero versus positive is the paid-role discriminator the
   * domain itself uses, at `RoleController.vb` L494 (`userRole.ServiceFee > 0.0`).
   *
   * MIGRATION: the format string is `"##0.00"` - two fractional digits and NO thousands
   * separator - so `toFixed` reproduces it directly. That is a measured inconsistency in the
   * legacy itself, not a simplification here: the role EDITOR formats the same two fees with
   * `"#,##0.00"`, WITH a separator, at `EditRoles.ascx.vb` L146, L147 and L155. Each screen
   * is kept faithful to its own format rather than harmonised, because harmonising would
   * change what one of the two screens renders.
   *
   * MIGRATION: the rounding MODE differs and cannot be reconciled without hand-rolling a
   * rounder. .NET's custom numeric formatting rounds a half away from zero; `toFixed`
   * rounds from the exact binary double, so a value whose decimal expansion sits a hair
   * below a half rounds down where .NET rounded up. It is documented rather than emulated:
   * the divergence is at most one hundredth of a currency unit on a value that is already
   * only single-precision, and a bespoke rounder would be far more likely to introduce a
   * defect than to remove one.
   *
   * @param price The fee from the row, the legacy sentinel, or null.
   * @returns The fee as text with two fractional digits, or the empty string when absent.
   *   Never null or undefined.
   */
  private formatPrice(price: number | null): string {
    if (price === null) {
      return '';
    }

    if (!Number.isFinite(price)) {
      return '';
    }

    if (price <= LEGACY_NULL_SINGLE_BOUND) {
      return '';
    }

    return price.toFixed(FEE_FRACTION_DIGITS);
  }

  // -------------------------------------------------------------------------
  // PRIVATE HELPERS
  // -------------------------------------------------------------------------

  /**
   * Turns a selector value back into the typed filter.
   *
   * The two negative values are recognised by name and mapped onto their own union arms;
   * they are never treated as identifiers and never forwarded to the server. Everything else
   * must be a non-negative integer to be a real group - zero included, because
   * `RoleGroups.RoleGroupID` is seeded `IDENTITY(0, 1)` - and anything else falls back to
   * the default filter rather than throwing, which is the guarded replacement for the
   * legacy's bare `Int32.Parse`.
   *
   * `Number.isInteger` is used rather than a truthiness or sign test: zero is a legitimate
   * identifier and would be discarded by either.
   *
   * @param raw The selected `option` value.
   * @returns The filter to apply. Never null or undefined.
   */
  private parseGroupFilter(raw: string): RoleGroupFilter {
    const trimmed: string = raw.trim();

    if (trimmed === String(ALL_ROLES_FILTER_VALUE)) {
      return { kind: 'AllRoles' };
    }

    if (trimmed === String(GLOBAL_ROLES_FILTER_VALUE)) {
      return { kind: 'GlobalRoles' };
    }

    const parsed: number = Number(trimmed);

    if (trimmed.length === 0 || !Number.isInteger(parsed) || parsed < 0) {
      return DEFAULT_ROLE_GROUP_FILTER;
    }

    return { kind: 'Group', roleGroupId: parsed };
  }

  /**
   * Returns a required cell template, or explains precisely which one is missing.
   *
   * The shared table declares `cellTemplate` as required on both the template and the
   * actions column kinds, because a template column with no template is a blank column on
   * every row. A static view query resolves to `undefined` only when the referenced
   * `ng-template` is absent from the sibling template file or has been moved inside a
   * control-flow block, so the failure is a wiring mistake and the message names the
   * reference that has to be restored.
   *
   * @param captured The captured template, or undefined when the query found none.
   * @param reference The `ng-template` reference name the query looks for.
   * @returns The captured template.
   * @throws Error if the template is absent.
   */
  private requireTemplate(
    captured: TemplateRef<DataTableCellContext<RoleListItem>> | undefined,
    reference: string,
  ): TemplateRef<DataTableCellContext<RoleListItem>> {
    if (captured === undefined) {
      throw new Error(
        `role-list.component.html must declare an ng-template named "#${reference}" at the ` +
          'top level of the template, outside any control-flow block.',
      );
    }

    return captured;
  }

  /**
   * Reports the settled outcome of a role-group mutation.
   *
   * A failure whose operation does not match the one that was awaited is NOT this
   * mutation's: a successful removal triggers a reload, and a reload that then failed would
   * otherwise be announced as a failed removal. Such a failure reaches the reader through
   * the inline banner instead, which is where a failed listing belongs.
   *
   * Severity comes from the shared summary, which already answers `warning` rather than
   * `error` for a refusal of permission. That is the legacy's own reading: the access-denied
   * control raised its message as a yellow warning on BOTH of its branches
   * (`AccessDenied.ascx.vb` L43 and L45), and the legacy severity vocabulary was three
   * valued, so a success is announced as a success and a refusal is not dressed up as a
   * fault.
   *
   * @param awaited What was requested.
   * @param failure The failure the store recorded, or null when nothing failed.
   */
  private reportGroupOutcome(
    awaited: AwaitedGroupMutation,
    failure: RoleStoreFailure | null,
  ): void {
    if (failure === null || failure.operation !== awaited.operation) {
      this.notifications.notify('success', this.successMessage(awaited));
      return;
    }

    this.notifications.notify(failure.summary.severity, this.failureMessage(failure));
  }

  /**
   * Wording for a settled, successful group mutation.
   *
   * @param awaited What was requested.
   * @returns The confirmation to announce. Never empty.
   */
  private successMessage(awaited: AwaitedGroupMutation): string {
    return awaited.operation === 'deleteRoleGroup'
      ? `Role group "${awaited.roleGroupName}" was deleted.`
      : `Role group "${awaited.roleGroupName}" was updated.`;
  }

  /**
   * Wording for a refused group mutation.
   *
   * Two sources, in order of specificity: the shared conflict vocabulary when the server
   * published a code that vocabulary recognises, so the sentence is reproduced rather than
   * paraphrased, and otherwise the shared summary's own message. Both branches pass through
   * the shared break-tag normaliser, because the conflict wording is lifted verbatim from
   * resource values and at least one of those carries a leading layout break.
   *
   * MIGRATION: an earlier revision carried a THIRD branch and a private sentence for the
   * in-use refusal on a removal, recognised by its bare `409` rather than by its code. That
   * existed only because the shared vocabulary did not list `role_group.in_use`, and the
   * comment on it said so and reported the gap. The gap is now closed in the shared utility,
   * so the branch and the private sentence are gone: the refusal is recognised by its CODE
   * like every other, and the wording lives in one place. Recognising a refusal by status
   * alone was always the weaker test - the same status arrives for a duplicate name, and only
   * the ordering of the branches kept the two apart.
   *
   * @param failure The failure the store recorded.
   * @returns The message to announce. Never empty.
   */
  private failureMessage(failure: RoleStoreFailure): string {
    const shared: string | null = conflictMessage(failure.conflict);

    if (shared !== null) {
      return stripLegacyBreakTags(shared);
    }

    return stripLegacyBreakTags(failure.summary.message);
  }
}
