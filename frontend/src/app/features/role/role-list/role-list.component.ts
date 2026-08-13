import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import type { ParamMap, Params } from '@angular/router';

import type { SortDirection } from '../../../core/models/paged-result.model';

import {
  addressStatesQuery,
  FIRST_PAGE_INDEX,
  firstPageParameter,
  PAGE_PARAM,
  parsePageIndex,
  parseSortDirection,
  parseSortKey,
  SORT_BY_PARAM,
  SORT_DIR_PARAM,
} from '../../../core/utils/list-query.util';

import { AuthStore } from '../../../core/state/auth.store';
import { DEFAULT_ROLE_GROUP_FILTER, ROLES_PAGE_SIZE, RoleStore } from '../../../core/state/role.store';
import { NotificationService } from '../../../core/services/notification.service';
import { UserService } from '../../../core/services/user.service';
import type { UserDetail } from '../../../core/models/user.model';
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
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';

import type { OnInit } from '@angular/core';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import { BILLING_FREQUENCY_NAMES } from '../../../core/models/role.model';
import type { RoleGroup, RoleListItem } from '../../../core/models/role.model';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import type { RoleGroupFilter, RoleStoreFailure, RoleStoreOperation } from '../../../core/state/role.store';
import type {
  DataTableCellContext,
  DataTableColumn,
  DataTableSortChange,
} from '../../../shared/components/data-table/data-table.component';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// THE SELECTOR'S THREE-WAY IDENTIFIER

// THE ADDRESS

const GROUP_PARAM = 'group';

const SORTABLE_COLUMN_KEYS: readonly string[] = Object.freeze([
  'roleName',
  'description',
  'serviceFee',
  'billingPeriod',
  'billingFrequency',
  'trialFee',
  'trialPeriod',
  'trialFrequency',
  'isPublic',
  'autoAssignment',
]);

/** The {@link GROUP_PARAM} value standing for every role in the portal, whatever its grouping. */
const ALL_GROUPS_TOKEN = 'all';

/** The {@link GROUP_PARAM} value standing for the roles belonging to no group at all. */
const UNGROUPED_TOKEN = 'none';

/** `option` value standing for the legacy "&lt; All Roles &gt;" pseudo-filter (legacy `-2`). */
const ALL_ROLES_FILTER_VALUE = -2;

/** `option` value standing for the legacy "&lt; Global Roles &gt;" filter (legacy `-1`). */
const GLOBAL_ROLES_FILTER_VALUE = -1;

/** The narrowing and the page this listing is showing, as the address states them. */
interface RoleListQuery {
  /** The narrowing to apply. */
  readonly groupFilter: RoleGroupFilter;

  /** The page to read, counted from nought. */
  readonly pageIndex: number;

  /**
   * The column key to order by, or `null` to accept the endpoint's own default ordering. Constrained to
   * {@link SORTABLE_COLUMN_KEYS} by the reader, so a hand-written address naming a column the endpoint
   * would refuse resolves to `null` and lists the default order rather than producing a `400`.
   */
  readonly sortBy: string | null;

  /** The direction to order in, or `null`. Only ever set alongside {@link RoleListQuery.sortBy}. */
  readonly sortDir: SortDirection | null;
}

/**
 * Reads a narrowing out of an address. An absent or unusable value resolves to {@link
 * DEFAULT_ROLE_GROUP_FILTER} rather than throwing.
 *
 * @param raw The parameter as it appears in the address, or `null` when absent.
 * @returns The narrowing to apply.
 */
function parseAddressGroupFilter(raw: string | null): RoleGroupFilter {
  if (raw === null) {
    return DEFAULT_ROLE_GROUP_FILTER;
  }

  const token: string = raw.trim().toLowerCase();

  if (token === ALL_GROUPS_TOKEN) {
    return { kind: 'AllRoles' };
  }

  if (token === UNGROUPED_TOKEN) {
    return { kind: 'GlobalRoles' };
  }

  const parsed: number = Number(token);

  if (token.length === 0 || !Number.isInteger(parsed) || parsed < 0) {
    return DEFAULT_ROLE_GROUP_FILTER;
  }

  return { kind: 'Group', roleGroupId: parsed };
}

/**
 * The value {@link GROUP_PARAM} should carry for a narrowing, or `null` to omit it.
 *
 * @param filter The narrowing in force.
 * @returns The parameter value, or `null` when the narrowing is the default.
 */
function groupFilterParameter(filter: RoleGroupFilter): string | null {
  switch (filter.kind) {
    case 'AllRoles':
      return ALL_GROUPS_TOKEN;
    case 'GlobalRoles':
      // The default, and therefore omitted rather than stated.
      return null;
    case 'Group':
      return String(filter.roleGroupId);
  }
}

/**
 * Reads the whole listing query out of an address.
 *
 * @param address The route's query parameters.
 * @returns The query to apply, with every unusable value resolved to its default.
 */
function parseRoleListQuery(address: ParamMap): RoleListQuery {
  // Resolved first, because the direction is only meaningful once a field has survived validation.
  const sortBy: string | null = parseSortKey(address.get(SORT_BY_PARAM), SORTABLE_COLUMN_KEYS);

  return {
    groupFilter: parseAddressGroupFilter(address.get(GROUP_PARAM)),
    pageIndex: parsePageIndex(address.get(PAGE_PARAM)),
    sortBy,
    // A direction with no field to apply it to is DROPPED rather than kept, so the address can never carry
    // half an ordering. A field with no direction is kept, because the endpoint has its own default.
    sortDir: sortBy === null ? null : parseSortDirection(address.get(SORT_DIR_PARAM)),
  };
}

/**
 * Writes a listing query back out as address parameters.
 *
 * @param query The query in force.
 * @returns The parameters to merge into the address.
 */
function serialiseRoleListQuery(query: RoleListQuery): Params {
  return {
    [GROUP_PARAM]: groupFilterParameter(query.groupFilter),
    [PAGE_PARAM]: firstPageParameter(query.pageIndex),
    [SORT_BY_PARAM]: query.sortBy,
    // Emitted only alongside a field, matching what the reader will accept back, so a round trip through
    // the address is stable rather than shedding a parameter on the way.
    [SORT_DIR_PARAM]: query.sortBy === null || query.sortDir === null ? null : query.sortDir,
  };
}

// THE LEGACY NULL SENTINELS THIS SCREEN HAS TO RECOGNISE

/** `Null.NullInteger`: the legacy marker for an absent `Integer`. */
const LEGACY_NULL_INTEGER = -1;

/**
 * Lower bound at which a single-precision fee is treated as `Null.NullSingle`. `Single.MinValue` is
 * exactly `-3.4028234663852886e38`.
 */
const LEGACY_NULL_SINGLE_BOUND = -3.4028234e38;

/** Fractional digits for a fee, from the legacy `"##0.00"` format string. */
const FEE_FRACTION_DIGITS = 2;

/**
 * The smallest amount whose hundredths a double can no longer represent exactly — R-M4.
 * `Number.MAX_SAFE_INTEGER` is the largest integer an IEEE-754 double holds exactly; expressed in
 * hundredths, dividing it by a hundred gives the largest amount whose every cent is exact.
 */
const EXACT_CENTS_BOUND = Number.MAX_SAFE_INTEGER / 100;

/**
 * The clipped qualifier beside an amount too large to state exactly — R-M4. Announced and never drawn, on
 * the same footing as the absent-value description beside it: a reader who cannot see the figure is told
 * it is approximate, and a reader who can is not read a sentence on a row whose ordinary neighbours say
 * nothing.
 */
const APPROXIMATE_VALUE_DESCRIPTION = 'approximate';

// ROUTES
// These replace `EditUrl("RoleID", "KEYFIELD", "Edit")` and `NavigateURL(TabId, "User Roles",
// "RoleId=KEYFIELD")` two string-formatted query URLs built by substituting a placeholder token into an
// already-rendered link. That mechanism carried a whole class of defect with it.

/** Root path of the roles feature. */
const ROLES_PATH = '/roles';

/** Child segment listing the accounts held in one role. */
const ROLE_MEMBERS_SEGMENT = 'users';

/** Target of the legacy `AddContent.Action` module action. */
const ADD_ROLE_LINK = '/roles/new';

const ROLE_LIST_LINK = '/roles';

const ACCOUNT_SUBJECT_PREFIX = 'Roles held by account';

/** The same lead-in for the case where the person's own name is known. */
const ACCOUNT_SUBJECT_PREFIX_NAMED = 'Roles held by';

/** The affordance that returns to the account whose memberships are on screen. */
const SUBJECT_ACCOUNT_LABEL = 'Back to Account';

/**
 * How an account is named in the subtitle. ⚠ THE DISPLAY NAME MAY BE THE EMPTY STRING RATHER THAN ABSENT,
 * so this cannot be a null check.
 *
 * @param account The account that was read.
 * @returns The name to render.
 */
function displayedAccountName(account: UserDetail): string {
  const displayed: string = account.displayName.trim();

  return displayed.length > 0 ? displayed : account.username;
}

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

// WORDING

/** `Roles.ascx.resx` &rarr; `ControlTitle_.Text`. */
const PAGE_TITLE = 'Security Roles';

/** `Roles.ascx.resx` &rarr; `plRoleGroups.Text`, verbatim INCLUDING the trailing colon. */
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
 * `EditGroups.ascx.resx` &rarr; `valRoleGroupName.Text`, retained with its leading break tag so the
 * transformation is auditable, then normalised by the SHARED helper. The raw resource value is `<br>You
 * Must Enter a Valid Name`.
 */
const GROUP_NAME_REQUIRED_MESSAGE = stripLegacyBreakTags('<br>You Must Enter a Valid Name');

/** Client-side guard message for the group name's 50-character storage limit. */
const GROUP_NAME_TOO_LONG_MESSAGE = 'A group name may be at most 50 characters.';

/** Client-side guard message for the description's 1000-character storage limit. */
const GROUP_DESCRIPTION_TOO_LONG_MESSAGE = 'A description may be at most 1000 characters.';

/**
 * `SharedResources.resx` L120 &rarr; `DeleteItem.Text`, verbatim including its question mark.
 * `form-field` strips one trailing colon and preserves other punctuation, and the confirmation dialog
 * applies no punctuation policy at all, so the question mark stands.
 */
const DELETE_CONFIRMATION_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** Affirmative button wording for the group-removal confirmation. */
const DELETE_CONFIRMATION_LABEL = 'Delete';

/** The store operations whose failure belongs INLINE, on the error banner. */
/**
 * The read failures this screen states in its own banner. ⚠ THE NARROWED READ IS HERE BECAUSE ITS ABSENCE
 * WAS A CRITICAL DEFECT. It records a failure like any other read, but this list did not include it, so
 * `/roles?userId=999` - which the endpoint refuses with `404` "Portal -1 has no member bearing identifier
 * 999" - reported nothing at all: no banner, no notification, no console error.
 */
const INLINE_FAILURE_OPERATIONS: readonly RoleStoreOperation[] = [
  'loadRoles',
  'loadRoleGroups',
  'loadRolesHeldByUser',
];

/**
 * The empty row set, shared rather than built per evaluation. A fresh literal would be a new reference on
 * every read of the computed that returns it, which would defeat the change-detection saving the shared
 * table gets from an unchanged input.
 */
const NO_ROLES: readonly RoleListItem[] = Object.freeze([]);

/** Name of the group-name control, matched against the problem document's field keys. */
/** The mark drawn where a period or a fee was never recorded — an em dash. */
const ABSENT_VALUE_MARK = '\u2014';

/** The words behind {@link ABSENT_VALUE_MARK}, for the accessibility tree. */
const ABSENT_VALUE_DESCRIPTION = 'not recorded';

const GROUP_NAME_CONTROL = 'roleGroupName';

/** Name of the group-description control, matched against the problem document's field keys. */
const GROUP_DESCRIPTION_CONTROL = 'description';

/** Storage limit of `RoleGroups.RoleGroupName nvarchar(50) NOT NULL`. */
const GROUP_NAME_MAX_LENGTH = 50;

/** Storage limit of `RoleGroups.Description nvarchar(1000) NULL`. */
const GROUP_DESCRIPTION_MAX_LENGTH = 1000;

// COLUMN HEADINGS
// The headings are therefore DISAMBIGUATED, using the legacy's own vocabulary rather than invented wording:
// `EditRoles.ascx.resx` names the same two facts `BillingPeriod.Text` = "Billing Period (Every)" and
// `TrialPeriod.Text` = "Trial Period (Every)", and their help text - "These two fields are used in
// conjunction to enter a Billing Period. e.g 2 weeks, or 1 month" - proves the count and the frequency code
// are one logical value.

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
  /** The `option` element's value. */
  readonly value: string;

  /** The painted text. */
  readonly label: string;
}

/** Typed model of the inline role-group editor. */
interface RoleGroupFormModel {
  /** The group's name. */
  roleGroupName: FormControl<string>;

  /** The group's description, at most 1000 characters. */
  description: FormControl<string>;
}

/** A role-group mutation whose outcome has been requested but not yet reported. */
interface AwaitedGroupMutation {
  /** Which store operation is in flight. */
  readonly operation: Extract<
    RoleStoreOperation,
    'deleteRoleGroup' | 'updateRoleGroup' | 'deleteRole'
  >;

  /** The subject's name at the moment the request was issued, for the outcome wording. */
  readonly subjectName: string;
}

/**
 * The Security Roles listing screen. Migrated from `Website/admin/Security/roles.ascx` and its 317-line
 * code-behind `Roles.ascx.vb`.
 */
@Component({
  selector: 'app-role-list',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
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
    // The pager beneath the grid. An ADDITION with no legacy counterpart — see the note beside the
    // element in the paired template, and `ROLES_PAGE_SIZE` in the store for the measurements.
    PaginationComponent,
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

  /** The address this screen reads its narrowing and page from, and writes them back to. */
  private readonly route = inject(ActivatedRoute);

  private readonly router = inject(Router);

  /** Ties the address subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  /** Carries the transient outcome of a reader-initiated mutation. */
  private readonly notifications = inject(NotificationService);

  /**
   * The session projection, read for ONE fact: whether the caller administers the tenant. ⚠ THE RIGHT
   * VOCABULARY FOR THIS QUESTION, AND THE PREVIOUS ONE WAS WRONG. The two create affordances below were
   * gated on the persisted permission KEY `EDIT`, which is a different question over different data: the
   * caller's permission keys are a union across the pages and modules it holds rights on, and no member
   * of that union says whether the caller may create a role.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The account transport, used for exactly one thing: naming the subject account. A CORE service, not
   * the account feature's.
   */
  private readonly users = inject(UserService);

  /** The account the held name belongs to, so a name is never shown against the wrong key. */
  private readonly _subjectAccountId: WritableSignal<number | null> = signal<number | null>(null);

  /** The subject account's rendered name, or `null` while unread or unreadable. */
  private readonly _subjectAccountName: WritableSignal<string | null> = signal<string | null>(null);

  /** {@link RoleListComponent._subjectAccountId}, read-only. */
  protected readonly subjectAccountId: Signal<number | null> = this._subjectAccountId.asReadonly();

  /** {@link RoleListComponent._subjectAccountName}, read-only. */
  protected readonly subjectAccountName: Signal<string | null> =
    this._subjectAccountName.asReadonly();

  /**
   * Whether the caller may be offered the tenant-administration affordances. Exposed for the template's
   * two create links.
   */
  protected readonly administersPortal: Signal<boolean> = this.auth.administersCurrentPortal;

  // CELL TEMPLATES
  // Static queries, so they are resolved before `ngOnInit` and the column set can be assembled there. An
  // `ng-template` the host writes belongs to the host's view whether or not another component ends up
  // rendering it, which is what makes this work.

  /** Row edit command. */
  @ViewChild('editCommand', { static: true })
  private editCommandTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Row membership command. */
  @ViewChild('membersCommand', { static: true })
  private membersCommandTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Row delete command. AN ADDITION WITH NO LEGACY COLUMN BEHIND IT, and it is recorded as one. */
  @ViewChild('deleteCommand', { static: true })
  private deleteCommandTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Fee cell, shared by the service-fee and trial-fee columns. */
  @ViewChild('feeCell', { static: true })
  private feeCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /**
   * Period cell, shared by the billing and trial period columns. A template rather than a bound value
   * because an absent period now renders a MARK plus its clipped expansion rather than an empty string —
   * see {@link absentValueMark} for the inconsistency that forced it.
   */
  @ViewChild('periodCell', { static: true })
  private periodCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /** Frequency cell, shared by the billing and trial frequency columns. */
  @ViewChild('frequencyCell', { static: true })
  private frequencyCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

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

  /**
   * Whether the chained administration read has already been issued for this visit. Held as a plain field
   * rather than a signal because nothing renders from it: it exists solely so that the FIRST address
   * emission fetches the narrowing selector's groups alongside the listing and every later one fetches
   * the listing alone.
   */
  private hasReadAdministration = false;

  /** Whether the inline role-group editor is showing. */
  private readonly editorOpen = signal(false);

  /** The group whose removal is awaiting confirmation, or null when none is. */
  private readonly pendingRemoval = signal<RoleGroup | null>(null);

  /** The role whose removal is awaiting confirmation, or null when none is. */
  private readonly pendingRoleDeletion = signal<RoleListItem | null>(null);

  /** The group mutation whose outcome has not yet been reported. */
  private readonly awaitedMutation = signal<AwaitedGroupMutation | null>(null);

  // STORE-DERIVED SURFACE
  // The store replaces the legacy POST-BACK RE-BIND, not view state. This screen never used view state to
  // begin with - `roles.ascx` L23 sets `EnableViewState="false"` outright, and neither `ViewState(` nor
  // `Session(` appears anywhere in `Website/admin/Security/`.

  /**
   * The roles to paint. A fresh array arrives on every re-query - the store replaces the paged result
   * rather than mutating it - which is what the shared table requires, since it tracks a row by object
   * reference.
   */
  /**
   * The account whose memberships are the subject, arriving from `?userId=` on the address. ⚠ A
   * ROUTE-BOUND INPUT, not a parameter this screen reads for itself. Component input binding is
   * configured on the router, so a query parameter of this name binds here with no `ActivatedRoute`
   * subscription to manage and nothing to unsubscribe.
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
   * browsable listing otherwise. ⚠ THE ACCOUNT-NARROWED SLICE IS USED ONLY ONCE IT DESCRIBES THE ACCOUNT
   * ASKED ABOUT. The store records the account at dispatch, so during a read of account B the slice may
   * still hold account A's answer; rendering that would show one person's memberships under another's
   * name.
   */
  protected readonly roles = computed<readonly RoleListItem[]>(() => {
    const subject: number | undefined = this.userId();
    const held: readonly RoleListItem[] | null = this.store.rolesHeldByUser();

    // ⚠ A REFUSED NARROWING RENDERS NOTHING, NOT THE UNNARROWED LISTING. Falling back here was a CRITICAL
    // defect: `/roles?userId=999` is refused with `404` "Portal -1 has no member bearing identifier 999",
    // and the fallback then showed all three roles beneath the subtitle "Roles held by account 999" - a
    // false statement about a non-existent account, presented as data.
    if (subject !== undefined && this.heldRolesFailed()) {
      return NO_ROLES;
    }

    if (subject === undefined || held === null || this.store.heldRolesUserId() !== subject) {
      return this.store.roleItems();
    }

    return held;
  });

  /** Whether the narrowed read was refused, as distinct from returning no memberships. */
  protected readonly heldRolesFailed = computed<boolean>(
    () => this.store.failure()?.operation === 'loadRolesHeldByUser',
  );

  /** Whether an account is the subject of the listing. */
  protected readonly narrowedToAccount = computed<boolean>(() => this.userId() !== undefined);

  /** The portal's role groups, in the order the endpoint returned them. Unpaged, as the legacy was. */
  protected readonly roleGroups = this.store.roleGroups;

  /**
   * Whether the roles request is in flight. Handed to the shared table, which renders the spinner and the
   * empty state itself in a single spanning row and lets loading win over empty.
   */
  protected readonly loading = computed<boolean>(
    () => this.store.rolesLoading() || this.store.heldRolesLoading(),
  );

  /** Whether a mutation is in flight; disables the editor and the removal affordance. */
  protected readonly saving = this.store.saving;

  /** The currently selected group, or null when a sentinel filter is active. */
  protected readonly selectedRoleGroup = this.store.selectedRoleGroup;

  protected readonly filterRowVisible = this.store.hasRoleGroups;

  /**
   * Whether the selected group may be edited inline. Reproduces the first delete guard, `Roles.ascx.vb`
   * L79-L84: when the filter is either sentinel the legacy set `lnkEditGroup.Visible = False` and
   * `cmdDelete.Visible = False` together, because neither pseudo-entry is a resource that can be edited
   * or removed.
   */
  protected readonly canEditSelectedGroup = computed<boolean>(
    () => this.store.selectedRoleGroupId() !== null,
  );

  protected readonly canRemoveSelectedGroup = this.store.canDeleteSelectedGroup;

  /** Whether the inline role-group editor is showing. */
  protected readonly groupEditorOpen = this.editorOpen.asReadonly();

  /** The group awaiting removal confirmation; its presence is what opens the dialog. */
  protected readonly pendingGroupRemoval = this.pendingRemoval.asReadonly();

  /**
   * The twelve columns, assembled in `ngOnInit` once the cell templates exist, with the sort affordance
   * withheld while an account is the subject of the listing. ⚠ THE ORDERING AFFORDANCE BELONGS TO THE
   * BROWSABLE LISTING AND TO NOTHING ELSE. When an account is the subject, the rows come from a DIFFERENT
   * and unpaged slice - the memberships that account holds - which the ordering command cannot reach: it
   * re-reads the browsable listing, so a press would issue a request whose answer this grid never renders
   * and would leave the heading announcing an order the visible rows are not in.
   */
  protected readonly columns = computed<readonly DataTableColumn<RoleListItem>[]>(() => {
    const built: readonly DataTableColumn<RoleListItem>[] = this.columnSet();

    if (this.narrowedToAccount() === false) {
      return built;
    }

    return built.map((column: DataTableColumn<RoleListItem>): DataTableColumn<RoleListItem> =>
      column.sortable === true ? { ...column, sortable: false } : column,
    );
  });

  /**
   * The COLUMN KEY the listing is ordered by, or `null` for the server's own ordering. Read from the
   * store rather than held here, so a heading can never announce an order whose request failed: the
   * shared grid derives `aria-sort` from this input alone and never updates its own state.
   */
  protected readonly sortBy = computed<string | null>(() =>
    this.narrowedToAccount() ? null : this.store.rolesPage().sortBy,
  );

  /**
   * The direction {@link RoleListComponent.sortBy} is applied in, or `null` for the server's default. The
   * token is the server's own member name; an abbreviated spelling is refused by the model binder with
   * `400`, which is why the type comes from the paging contract rather than being written out here.
   */
  protected readonly sortDir = computed<SortDirection | null>(() =>
    this.narrowedToAccount() ? null : this.store.rolesPage().sortDir,
  );
  /** The role awaiting removal confirmation; its presence is what opens the dialog. */
  protected readonly pendingRoleRemoval = this.pendingRoleDeletion.asReadonly();

  /**
   * The page the listing is standing on, counted from zero. Read from the COORDINATE rather than from the
   * served metadata, because the pager has to reflect the page that was ASKED for even while the answer
   * for it is still outstanding — binding the served index would make the control jump back to the
   * previous page for the duration of every request.
   */
  protected readonly pageIndex: Signal<number> = computed(() =>
    // ⚠ THE ACCOUNT-NARROWED READ IS UNPAGED, SO ITS COORDINATES ARE DERIVED FROM THE ROWS THEMSELVES. `GET
    // /users/{userId}/roles` answers with a plain array and no metadata, so there IS no served page for it
    // - and reading the BROWSABLE listing's coordinate here made the pager describe a different question
    // than the grid was answering.
    this.narrowedToAccount() ? FIRST_PAGE_INDEX : this.store.rolesPage().pageIndex,
  );

  /** The page size in effect. */
  protected readonly servedPageSize: Signal<number> = computed(() => {
    if (this.narrowedToAccount()) {
      return Math.max(this.roles().length, 1);
    }

    const served: number = this.store.rolesMeta().pageSize;

    if (served > 0) {
      return served;
    }

    return this.store.rolesPage().pageSize ?? ROLES_PAGE_SIZE;
  });

  /** How many roles the current narrowing matches. */
  protected readonly totalCount: Signal<number> = computed(() =>
    this.narrowedToAccount() ? this.roles().length : this.store.rolesMeta().totalCount,
  );

  /**
   * The selector's entries, in the legacy order. `Roles.ascx.vb` L112-L126 uses `Items.Add` throughout
   * rather than `Items.Insert`, so the order is "all roles", then "global roles", then each group as
   * `GetRoleGroups` returned it.
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
   * The selector's current value, derived from the filter the store actually holds. this corrects defect
   * D-R3 by construction.
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

  /** The problem document to show inline, or null when nothing failed to load. */
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
   * Server-reported messages against the group name, if any. Read through the shared field-message reader
   * rather than by indexing the document.
   */
  private readonly groupNameServerMessages = computed<readonly string[]>(() =>
    fieldErrorMessages(this.editorProblem(), GROUP_NAME_CONTROL),
  );

  /** Server-reported messages against the group description, if any. */
  private readonly groupDescriptionServerMessages = computed<readonly string[]>(() =>
    fieldErrorMessages(this.editorProblem(), GROUP_DESCRIPTION_CONTROL),
  );

  // THE INLINE ROLE-GROUP EDITOR
  // The legacy filter row carried an edit link to a separate `EditGroup` screen. The migrated route table
  // is closed and declares no group-edit route, so the two facts a group owns are edited in place here
  // instead, composed from the shared labelled-field and confirmation components.

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

  protected readonly accountSubtitle = computed<string | undefined>(() => {
    const subject: number | undefined = this.userId();

    if (subject === undefined) {
      return undefined;
    }

    const named: string | null =
      this.subjectAccountId() === subject ? this.subjectAccountName() : null;
    const who: string = named === null ? `${ACCOUNT_SUBJECT_PREFIX} ${subject}` : `${ACCOUNT_SUBJECT_PREFIX_NAMED} ${named}`;

    // Reads the narrowed rows, so while the read is in flight this says nothing about a count it
    // does not yet have.
    const held: readonly RoleListItem[] | null = this.store.rolesHeldByUser();

    if (held === null || this.store.heldRolesUserId() !== subject) {
      return who;
    }

    return `${who} — ${held.length} ${held.length === 1 ? ROLE_SINGULAR : ROLE_PLURAL}`;
  });

  /**
   * The address of the account whose memberships are on screen, or `undefined` when none is. ⚠ THIS IS
   * THE RETURN PATH, and its absence is the second half of the reported defect: an operator who pressed a
   * per-account command on the account listing arrived here with no way back to the person they came
   * from. The unnarrowed-listing link goes sideways, not back.
   */
  protected readonly subjectAccountLink = computed<(string | number)[] | undefined>(() => {
    const subject: number | undefined = this.userId();

    // A mutable array rather than a readonly one: the router's own link input is declared over a
    // mutable array, so a readonly type is rejected at the template boundary.
    return subject === undefined ? undefined : ['/users', subject];
  });

  /** The wording of the affordance that returns to the account. */
  protected readonly subjectAccountLabel = SUBJECT_ACCOUNT_LABEL;

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
   * Accessible name of the group-removal affordance. the legacy `cmdDelete` image button carried NEITHER
   * a resource key NOR alternative text, so it reached assistive technology with no accessible name at
   * all - a reader met an unlabelled button that destroyed a record.
   */
  protected readonly removeGroupLabel = 'Delete role group';

  /** Accessible name of the row edit command. */
  protected readonly editRoleLabel = EDIT_ROLE_LABEL;

  /** Accessible name of the row membership command: `UserRoles.Text`. */
  protected readonly manageUsersLabel = MANAGE_USERS_LABEL;

  /**
   * Accessible name of the row delete command. `SharedResources.resx` `Delete.Text`, which is the wording
   * every other removal in this application shows, so one action reads the same everywhere.
   */
  protected readonly deleteRoleLabel = 'Delete';

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

  /** Body of the removal confirmation: `DeleteItem.Text`. */
  protected readonly removalMessage = DELETE_CONFIRMATION_MESSAGE;

  /** Affirmative button wording of the removal confirmation. */
  protected readonly removalConfirmLabel = DELETE_CONFIRMATION_LABEL;

  // -------------------------------------------------------------------------
  // THE OUTCOME BRIDGE
  // -------------------------------------------------------------------------

  /**
   * Reports the outcome of a role-group mutation to the notification queue. An effect, because emitting a
   * user-visible notification IS a side effect - the store's mutators return `void` and subscribe
   * internally, so there is no completion callback to hang one on.
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

  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them. ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT.
   * The grid's own fallback is the row OBJECT, which is a correct key only while the same objects stay in
   * play; every read from the server decodes fresh objects, so without this a refetch of the same page
   * presents entirely new keys and the whole body is rebuilt to display records that never changed.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly roleRowKey = (row: RoleListItem): number => row.roleId;

  // LIFECYCLE
  // -------------------------------------------------------------------------

  /**
   * Fetches the role groups and then the roles. Reproduces `Roles.ascx.vb` L249-L261, where `Page_Load`
   * called `BindGroups`, which populated the selector and then called `BindData` at L133.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two reads of the same page on every arrival.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: RoleListQuery = parseRoleListQuery(address);

        if (!addressStatesQuery(address, serialiseRoleListQuery(query))) {
          void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: serialiseRoleListQuery(query),
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });

          return;
        }

        this.store.stageListQuery(query.groupFilter, query.pageIndex, query.sortBy, query.sortDir);

        if (this.hasReadAdministration) {
          this.store.loadRoles();

          return;
        }

        this.hasReadAdministration = true;
        this.store.loadRoleAdministration();
      });
  }

  /**
   * Keeps the account-narrowed slice in step with the address. A genuine side effect — it issues a read —
   * so an effect is the right mechanism rather than a computed.
   */
  private readonly accountSubjectEffect = effect((): void => {
    const subject: number | undefined = this.userId();

    untracked((): void => {
      if (subject === undefined) {
        this.store.clearRolesHeldByUser();

        return;
      }

      this.store.loadRolesHeldByUser(subject);
      this.readSubjectAccountName(subject);
    });
  });

  /**
   * Reads the subject account's own name, so the subtitle can NAME the person. this is the read at
   * `SecurityRoles.ascx.vb` L104-L105 — `UserController.GetUser(PortalId, UserId, False)`, made whenever
   * that screen was addressed with an account rather than a role — and it exists for the same purpose,
   * which is to render the person rather than their key. ⚠ IT IS NOT ROUTED THROUGH THE ROLE STORE, and
   * that is deliberate.
   *
   * @param subject The account whose name is wanted.
   */
  private readSubjectAccountName(subject: number): void {
    this._subjectAccountId.set(subject);
    this._subjectAccountName.set(null);

    this.users
      .getById(subject)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (account: UserDetail): void => {
          // Discarded when the address has moved on, so a slow answer for the previous account cannot
          // name the current one.
          if (this._subjectAccountId() !== subject) {
            return;
          }

          this._subjectAccountName.set(displayedAccountName(account));
        },

        // Deliberately silent, for the reason given above. The name stays unknown and the subtitle
        // falls back to the identifier.
        error: (): void => {
          if (this._subjectAccountId() === subject) {
            this._subjectAccountName.set(null);
          }
        },
      });
  }

  // -------------------------------------------------------------------------
  // ROW COMMAND TARGETS
  // -------------------------------------------------------------------------

  /**
   * The editor route of every role on the page, keyed by identifier. PRECOMPUTED ONCE PER PAGE rather
   * than per row per change-detection pass, and bound as an index rather than called.
   */
  protected readonly editRoleLinks: Signal<Readonly<Record<number, (string | number)[]>>> =
    computed(() => {
      const links: Record<number, (string | number)[]> = {};

      for (const role of this.roles()) {
        links[role.roleId] = [ROLES_PATH, role.roleId];
      }

      return links;
    });

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
   * @returns The twelve columns, in legacy order.
   * @throws Error if a required cell template is missing from the sibling template file.
   */
  private buildColumns(): readonly DataTableColumn<RoleListItem>[] {
    return [
      // ⚠ NO WIDTH IS DECLARED ON THESE TWO COMMAND COLUMNS EITHER, AND THAT IS A MEASURED CONCLUSION
      // RATHER THAN AN OMISSION. The "Manage Users" command overpainted the Name column in every row -
      // 30.70px of overlap at a 320-wide viewport, 16.22px still at 1024 - and `width: 'min-content'` was
      // tried here first as the obvious remedy.
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

      // 3. THE REMOVAL COMMAND — an addition, placed LAST of the three so that the destructive one is never
      // the command a pointer lands on first when moving in from the row's leading edge.
      {
        key: 'deleteCommand',
        label: this.deleteRoleLabel,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.deleteCommandTemplate, 'deleteCommand'),
      },

      // 3. `asp:boundcolumn DataField="RoleName"`.
      {
        key: 'roleName',
        rowHeader: true,
        // Ordering: the key IS the endpoint's own sort name. See the sortability note on `columns`.
        sortable: true,
        label: NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'roleName',
      },

      // 4. `asp:boundcolumn DataField="Description"`. Nullable on the contract; the shared
      // table renders an absent value as empty rather than as the word "null".
      {
        key: 'description',
        sortable: true,
        label: DESCRIPTION_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'description',
      },

      // 5. Template column over `FormatPrice(ServiceFee)`.
      {
        key: 'serviceFee',
        sortable: true,
        label: FEE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.feeCellTemplate, 'feeCell'),
      },

      // 6. Template column over `FormatPeriod(BillingPeriod)`. The COUNT, before its unit.
      {
        key: 'billingPeriod',
        sortable: true,
        label: BILLING_EVERY_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.periodCellTemplate, 'periodCell'),
      },

      // 7. `asp:boundcolumn DataField="BillingFrequency"`, with the bare item style noted above.
      {
        key: 'billingFrequency',
        sortable: true,
        label: BILLING_PERIOD_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.frequencyCellTemplate, 'frequencyCell'),
      },

      // 8. Template column over `FormatPrice(TrialFee)` - the FEE, despite the heading
      // reading "Trial"; verified at `roles.ascx` L55.
      {
        key: 'trialFee',
        sortable: true,
        label: TRIAL_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.feeCellTemplate, 'feeCell'),
      },

      // 9. Template column over `FormatPeriod(TrialPeriod)`. The COUNT, before its unit.
      {
        key: 'trialPeriod',
        sortable: true,
        label: TRIAL_EVERY_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.periodCellTemplate, 'periodCell'),
      },

      // 10. `asp:boundcolumn DataField="TrialFrequency"`, the second bare item style.
      {
        key: 'trialFrequency',
        sortable: true,
        label: TRIAL_PERIOD_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.frequencyCellTemplate, 'frequencyCell'),
      },

      {
        key: 'isPublic',
        // Ordered on the STORED boolean, not on the announced word the pipe produces from it.
        sortable: true,
        label: PUBLIC_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.publicCellTemplate, 'publicCell'),
      },
      {
        key: 'autoAssignment',
        sortable: true,
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
   * Applies a new group filter. Reproduces `Roles.ascx.vb` L273-L278: a change of selection re-queried
   * the ROLES only.
   *
   * @param event The change event raised by the selector.
   */
  protected onGroupFilterChange(event: Event): void {
    const target: EventTarget | null = event.target;

    if (!(target instanceof HTMLSelectElement)) {
      return;
    }

    // The page is cleared alongside it, because a different narrowing yields a different result set in
    // which the page the operator was on has no counterpart.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        [GROUP_PARAM]: groupFilterParameter(this.parseGroupFilter(target.value)),
        [PAGE_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  /** Refetches the group list and the roles. */
  protected reload(): void {
    this.store.clearError();
    this.store.loadRoleAdministration();
  }

  // -------------------------------------------------------------------------
  // THE INLINE GROUP EDITOR
  // -------------------------------------------------------------------------

  /**
   * Opens the editor on the selected group. Does nothing when no real group is selected, which mirrors
   * the first delete guard: the legacy hid the edit link entirely for both sentinel filters, so there was
   * nothing to activate.
   */
  protected openGroupEditor(): void {
    const group: RoleGroup | null = this.selectedRoleGroup();

    if (group === null) {
      return;
    }

    this.store.clearError();
    this.groupForm.reset({
      roleGroupName: group.roleGroupName,
      // An absent description is `null` on the contract and the empty string in the control.
      description: group.description === null ? '' : group.description,
    });
    this.editorOpen.set(true);
  }

  /** Abandons the editor without submitting. */
  protected closeGroupEditor(): void {
    this.editorOpen.set(false);
  }

  /** Submits the edited group. */
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

    this.awaitedMutation.set({ operation: 'updateRoleGroup', subjectName: trimmedName });
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
   * Asks for confirmation before removing the selected group. Reproduces the confirmation the legacy
   * attached to its delete button through `ClientAPI.AddButtonConfirm`.
   */
  protected requestGroupRemoval(): void {
    const group: RoleGroup | null = this.selectedRoleGroup();

    if (group === null) {
      return;
    }

    this.store.clearError();
    this.pendingRemoval.set(group);
  }

  protected onGroupRemovalConfirmed(): void {
    const group: RoleGroup | null = this.pendingRemoval();

    this.pendingRemoval.set(null);

    if (group === null || this.saving()) {
      return;
    }

    this.awaitedMutation.set({
      operation: 'deleteRoleGroup',
      subjectName: group.roleGroupName,
    });
    this.store.deleteRoleGroup(group.roleGroupId);
  }

  /** Dismisses the confirmation without removing anything. */
  protected onGroupRemovalCancelled(): void {
    this.pendingRemoval.set(null);
  }

  // -------------------------------------------------------------------------
  // ROLE REMOVAL
  // -------------------------------------------------------------------------

  /**
   * Asks for confirmation before removing one role. The pending role is what puts the shared dialog in
   * the document, and the dialog's presence there IS its open state.
   *
   * @param role The row whose command was pressed.
   */
  protected requestRoleRemoval(role: RoleListItem): void {
    this.store.clearError();
    this.pendingRoleDeletion.set(role);
  }

  /**
   * Removes the confirmed role. ⚠ NOT GATED ON WHETHER THE ROLE LOOKS REMOVABLE, and that is the same
   * rule the group removal follows.
   */
  protected onRoleRemovalConfirmed(): void {
    const role: RoleListItem | null = this.pendingRoleDeletion();

    this.pendingRoleDeletion.set(null);

    if (role === null || this.saving()) {
      return;
    }

    this.awaitedMutation.set({ operation: 'deleteRole', subjectName: role.roleName });
    this.store.deleteRole(role.roleId, { thenReadListing: true });
  }

  /** Dismisses the role confirmation without removing anything. */
  protected onRoleRemovalCancelled(): void {
    this.pendingRoleDeletion.set(null);
  }

  /**
   * Re-orders the listing on the heading that was activated. The ordering goes into the ADDRESS and
   * nothing else, exactly as the page and the narrowing do: the subscription that watches the query
   * parameters is the single thing that stages state and issues the read, so writing the address is the
   * whole of the change here.
   *
   * @param change The heading that was activated and the direction to apply.
   */
  protected onSortChange(change: DataTableSortChange): void {
    // ⚠ REFUSED WHILE THE LISTING IS NARROWED TO ONE ACCOUNT, for the reason {@link columns} states: the
    // rows shown then come from a different slice that this command cannot reach, so acting would order a
    // listing nobody is looking at.
    if (this.narrowedToAccount()) {
      return;
    }

    // A NULL DIRECTION CLEARS THE ORDERING RATHER THAN DEFAULTING IT: the grid’s cycle has a third step
    // that asks for no ordering at all, which is the state this screen arrives in, so the KEY leaves the
    // address alongside the direction.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        [SORT_BY_PARAM]: change.direction === null ? null : change.key,
        [SORT_DIR_PARAM]: change.direction,
        [PAGE_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Moves the listing to another page. The index is forwarded to the store exactly as the pager reported
   * it.
   *
   * @param pageIndex The page to move to, counted from zero.
   */
  protected onPageChange(pageIndex: number): void {
    if (this.narrowedToAccount()) {
      return;
    }

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [PAGE_PARAM]: firstPageParameter(pageIndex) },
      queryParamsHandling: 'merge',
    });
  }

  protected readonly absentValueMark = ABSENT_VALUE_MARK;

  /** The words behind {@link absentValueMark}, for the accessibility tree. */
  protected readonly absentValueDescription = ABSENT_VALUE_DESCRIPTION;

  /** The clipped qualifier beside an amount the cell cannot state exactly — R-M4. */
  protected readonly approximateValueDescription = APPROXIMATE_VALUE_DESCRIPTION;

  /**
   * Whether a period column holds nothing to render for this row.
   *
   * @param role The row.
   * @param key Which of the two period columns is being drawn.
   * @returns True when the stored value is absent or the legacy marker.
   */
  protected isPeriodAbsent(role: RoleListItem, key: string): boolean {
    return this.formatPeriod(key === 'trialPeriod' ? role.trialPeriod : role.billingPeriod) === '';
  }

  /**
   * Whether a fee column holds nothing to render for this row. ⚠ FOUND BY RUNTIME MEASUREMENT, NOT BY
   * READING. The absent mark reached the two PERIOD columns first, and a browser pass over the whole
   * 145-role dataset then found the asymmetry it had left behind: on the one role whose fees are stored
   * NULL, the two money cells rendered as completely empty strings while the two count cells beside them
   * on the SAME ROW rendered the mark plus its clipped words.
   *
   * @param role The row.
   * @param key Which of the two fee columns is being drawn.
   * @returns True when the stored amount is absent or the legacy marker.
   */
  protected isFeeAbsent(role: RoleListItem, key: string): boolean {
    return this.formatPrice(key === 'trialFee' ? role.trialFee : role.serviceFee) === '';
  }

  /**
   * The text of a fee column.
   *
   * @param role The row.
   * @param key Which of the two fee columns is being drawn.
   * @returns The amount as text, or the empty string when absent.
   */
  protected feeText(role: RoleListItem, key: string): string {
    return this.formatPrice(key === 'trialFee' ? role.trialFee : role.serviceFee);
  }

  /**
   * The text of a period column.
   *
   * @param role The row.
   * @param key Which of the two period columns is being drawn.
   * @returns The count as text, or the empty string when absent.
   */
  protected periodText(role: RoleListItem, key: string): string {
    return this.formatPeriod(key === 'trialPeriod' ? role.trialPeriod : role.billingPeriod);
  }

  /**
   * The stored frequency character for a row, exactly as the column holds it.
   *
   * @param role The row.
   * @param key Which of the two frequency columns is being drawn.
   * @returns The single character, or the empty string when the column is null.
   */
  protected frequencyCode(role: RoleListItem, key: string): string {
    const stored = key === 'trialFrequency' ? role.trialFrequency : role.billingFrequency;

    return stored === null ? '' : stored;
  }

  /**
   * What a stored frequency character MEANS, for the accessibility tree only. ⚠ R-M3: THE PAINTED
   * CHARACTER DOES NOT CHANGE, AND THAT IS THE WHOLE DESIGN. The legacy grid bound the raw field rather
   * than the joined description, so `M` rendered as `M` — and it still does, because the character is
   * load-bearing data and the schema even seeds a lookup table the grid declined to join.
   *
   * @param role The row.
   * @param key Which of the two frequency columns is being drawn.
   * @returns The word behind the character, or the empty string when there is none to give.
   */
  protected frequencyName(role: RoleListItem, key: string): string {
    return BILLING_FREQUENCY_NAMES[this.frequencyCode(role, key)] ?? '';
  }

  // FORMATTING

  /**
   * Renders a period count, filtering out the legacy absent-value marker. Legacy body: `If period <>
   * Null.NullInteger Then _FormatPeriod = period.ToString`, starting from `Null.NullString` - which is
   * the EMPTY STRING and not null - so an absent period rendered as an empty cell.
   *
   * @param period The period count from the row, the legacy sentinel, or null.
   * @returns The count as text, or the empty string when absent.
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
   * Renders a fee, filtering out the legacy absent-value marker. Legacy body: `If price <>
   * Null.NullSingle Then _FormatPrice = price.ToString("##0.00")`, again starting from the empty string.
   *
   * @param price The fee from the row, the legacy sentinel, or null.
   * @returns The fee as text with two fractional digits, or the empty string when absent.
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

  /**
   * True when a fee is too large for this cell to state exactly — R-M4. ⚠ THE CELL IS PAINTING A NUMBER
   * THAT IS NOT THE STORED NUMBER, AND NOTHING SAID SO. The column behind these two cells is SQL `money`,
   * which carries four fractional digits of exact decimal at the full width of a 64-bit integer; the wire
   * carries a JSON number, which every browser reads as an IEEE-754 double.
   *
   * @param role The row.
   * @param key The column being drawn, which names which of the two fees is meant.
   * @returns `true` when the painted figure may differ from the stored one.
   */
  protected isFeeApproximate(role: RoleListItem, key: string): boolean {
    if (this.isFeeAbsent(role, key)) {
      return false;
    }

    const price: number | null = key === 'trialFee' ? role.trialFee : role.serviceFee;

    return price !== null && Number.isFinite(price) && Math.abs(price) >= EXACT_CENTS_BOUND;
  }

  // -------------------------------------------------------------------------
  // PRIVATE HELPERS
  // -------------------------------------------------------------------------

  /**
   * Turns a selector value back into the typed filter. The two negative values are recognised by name and
   * mapped onto their own union arms; they are never treated as identifiers and never forwarded to the
   * server.
   *
   * @param raw The selected `option` value.
   * @returns The filter to apply.
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
   * Returns a required cell template, or explains precisely which one is missing. The shared table
   * declares `cellTemplate` as required on both the template and the actions column kinds, because a
   * template column with no template is a blank column on every row.
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

    this.notifications.notify(
      failure.summary.severity,
      this.failureMessage(failure),
      failure.summary.supportReference,
    );
  }

  /**
   * Wording for a settled, successful group mutation.
   *
   * @param awaited What was requested.
   * @returns The confirmation to announce.
   */
  private successMessage(awaited: AwaitedGroupMutation): string {
    if (awaited.operation === 'deleteRole') {
      return `Role "${awaited.subjectName}" was deleted.`;
    }

    return awaited.operation === 'deleteRoleGroup'
      ? `Role group "${awaited.subjectName}" was deleted.`
      : `Role group "${awaited.subjectName}" was updated.`;
  }

  /**
   * @param failure The failure the store recorded.
   * @returns The message to announce.
   */
  private failureMessage(failure: RoleStoreFailure): string {
    const shared: string | null = conflictMessage(failure.conflict);

    if (shared !== null) {
      return stripLegacyBreakTags(shared);
    }

    return stripLegacyBreakTags(failure.summary.message);
  }
}
