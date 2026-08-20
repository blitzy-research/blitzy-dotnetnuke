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
import {
  MEMBERSHIP_SETTINGS_ROUTE,
  ROLE_LIST_GROUP_PARAM,
  ROLE_LIST_ROUTE,
} from '../../../core/config/app-routes.config';
import { ListReturnStore } from '../../../core/state/list-return.store';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import type { ParamMap, Params } from '@angular/router';

import { QUERY_MAX_LENGTH } from '../../../core/models/paged-result.model';
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
import { PortalStore } from '../../../core/state/portal.store';
import { DEFAULT_ROLE_GROUP_FILTER, ROLES_PAGE_SIZE, RoleStore } from '../../../core/state/role.store';
import { NotificationService } from '../../../core/services/notification.service';
import { RoleService } from '../../../core/services/role.service';
import { UserService } from '../../../core/services/user.service';
import type { UserDetail } from '../../../core/models/user.model';
import {
  conflictMessage,
  fieldErrorMessages,
  stripLegacyBreakTags,
} from '../../../core/utils/form-errors.util';
import {
  ABSENT_VALUE_DESCRIPTION as SHARED_ABSENT_VALUE_DESCRIPTION,
  ABSENT_VALUE_MARK as SHARED_ABSENT_VALUE_MARK,
  AbsentValueComponent,
} from '../../../shared/components/absent-value/absent-value.component';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
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

const GROUP_PARAM = ROLE_LIST_GROUP_PARAM;

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

/**
 * The address parameter carrying the free-text filter.
 *
 * ⚠ THE FILTER LIVES IN THE ADDRESS, like every other part of this listing's coordinate. The narrowing, the
 * page and the ordering are all addressable here, and a filter that was not would be the one part of the
 * state a reader could neither bookmark, share, nor return to through Back - and the screen would present
 * rows that its own address does not describe.
 */
const SEARCH_PARAM = 'search';

/** The longest filter the server accepts, so a longer address is truncated rather than refused with a 400. */
const SEARCH_MAX_LENGTH = QUERY_MAX_LENGTH;

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

  /** The free-text filter to apply, or `null` for none. Matched by the server against the role NAME only. */
  readonly search: string | null;

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
    search: parseAddressSearch(address.get(SEARCH_PARAM)),
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
/**
 * Reads the free-text filter out of an address.
 *
 * ⚠ A FILTER OF NOTHING BUT SPACES IS NOT A FILTER, and is resolved to absence rather than transmitted. It
 * returned the whole listing in any case, and sending it made an unfiltered grid look like a filtered one.
 * Over-long text is TRUNCATED rather than refused, because a hand-written or stale address is not worth a
 * `400` when the intent is unambiguous.
 *
 * @param stated The value the address carries, or `null` when it states none.
 * @returns The filter to apply, or `null` for none.
 */
function parseAddressSearch(stated: string | null): string | null {
  if (stated === null) {
    return null;
  }

  const tidied: string = stated.trim();

  if (tidied.length === 0) {
    return null;
  }

  return tidied.length > SEARCH_MAX_LENGTH ? tidied.slice(0, SEARCH_MAX_LENGTH) : tidied;
}

function serialiseRoleListQuery(query: RoleListQuery): Params {
  return {
    [GROUP_PARAM]: groupFilterParameter(query.groupFilter),
    [SEARCH_PARAM]: query.search,
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

/**
 * Target of the legacy `UserSettings.Action` module action.
 *
 * Aliased from the shared declaration rather than spelled again here, now that the module listing names the
 * same address for an administrative module's settings.
 */
const MEMBERSHIP_SETTINGS_LINK = MEMBERSHIP_SETTINGS_ROUTE;

// WORDING

/** `Roles.ascx.resx` &rarr; `ControlTitle_.Text`. */
const PAGE_TITLE = 'Security Roles';

/** `Roles.ascx.resx` &rarr; `plRoleGroups.Text`, verbatim INCLUDING the trailing colon. */
const GROUP_FILTER_LABEL = 'Filter By Role Group:';

/** `Roles.ascx.resx` &rarr; `plRoleGroups.Help`. */
const GROUP_FILTER_HELP = 'Select the Role Group you would like to view';

/** `SharedResources.resx` L834 &rarr; `AllRoles.Text`, stored as `&lt; All Roles &gt;`. */
const ALL_ROLES_OPTION_LABEL = '< All Roles >';

/** Why the listing is empty when a real group is selected: the narrowing, not the absence of roles. */
const EMPTY_GROUP_MESSAGE = 'No roles belong to the selected role group.';

/** Why the listing is empty when nothing is narrowing it. */
const EMPTY_LISTING_MESSAGE = 'No security roles have been defined for this site yet.';

/**
 * Why the listing is empty when a NAME FILTER matched nothing - QA-9.
 *
 * MEASURED FAULT IT ANSWERS: filtering by a name no role carries produced "No security roles have been
 * defined for this site yet." over a tenant holding fifteen roles. The sentence was simply false, and it
 * contradicted the `Filtered:` disclosure sitting directly above it, which correctly said a filter was in
 * force. The group-narrowed path already got this right; only the search path did not.
 */
const EMPTY_SEARCH_MESSAGE = 'No role name matches this filter. Clear the filter to see every role.';

/** `SharedResources.resx` L837 &rarr; `GlobalRoles.Text`, stored as `&lt; Global Roles &gt;`. */
const GLOBAL_ROLES_OPTION_LABEL = '< Global Roles >';

/** `SharedResources.resx` L213 &rarr; `Edit.Text`; the accessible name of the row edit command. */
const EDIT_ROLE_LABEL = 'Edit';

/** `Roles.ascx.resx` &rarr; `UserRoles.Text`; the accessible name of the row membership command. */
const MANAGE_USERS_LABEL = 'Manage Users';

/**
 * The state a protected row reports in place of a command it does not offer.
 *
 * One word, because it shares a narrow command column with two others. The full reason travels as the row's
 * own accessible text, which names WHICH role and WHY. MIGRATION: net addition - the legacy simply rendered
 * nothing in these cells.
 */
const PROTECTED_ROLE_STATE_LABEL = 'Protected';

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

/**
 * Appends the identity of the record being destroyed to the legacy question, or returns the question alone
 * when there is no record in hand or it carries no usable name.
 *
 * @param name The record's name, if there is a record.
 * @returns The confirmation body.
 */
function nameRemoval(name: string | undefined): string {
  const named: string = (name ?? '').trim();

  return named.length === 0 ? DELETE_CONFIRMATION_MESSAGE : `${DELETE_CONFIRMATION_MESSAGE} ${named}`;
}

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
const ABSENT_VALUE_MARK = SHARED_ABSENT_VALUE_MARK;

/**
 * The word painted where a fee is a recorded ZERO. A word rather than the figure, because "0.00" and "0.01"
 * are one keystroke apart on screen and the difference between them is the whole question this column answers.
 */
const FREE_FEE_LABEL = 'Free';

/** What {@link FREE_FEE_LABEL} stands for, announced with the amount it replaces so nothing is withheld. */
const FREE_FEE_DESCRIPTION = 'no charge, amount ';

/**
 * What a recorded period count of ZERO means, announced beside the figure.
 *
 * ⚠ A ZERO PERIOD IS A STATE, AND IT WAS PAINTED AS AN AMOUNT. "Every 0 months" is not a recurrence, so a row
 * carrying it has no working schedule at all - yet the count rendered in the same colour and weight as a
 * genuine "2", while the fee column beside it already named its own recorded zero rather than pricing it. The
 * figure is still painted verbatim, exactly as the legacy grid painted it; what is added is the state treatment
 * the fee column established and a sentence saying what the state is.
 */
const ZERO_PERIOD_DESCRIPTION = 'no recurring period';

/**
 * What a period count whose RECORDED unit cannot be named means, announced beside the figure.
 *
 * ⚠ A COUNT WITHOUT A USABLE UNIT IS NOT A PERIOD. The frequency column already marks a stored code it cannot
 * name; the count beside it said "2" in the ordinary treatment either way, so the two cells disagreed about the
 * same row. A count is only meaningful with its unit, so where a recorded unit cannot be named the count says
 * so too. An ABSENT unit is a different state and is not covered here - see `isPeriodUnitless`.
 */
const UNITLESS_PERIOD_DESCRIPTION = 'unit not recognised, so the period is unknown';

/**
 * The word behind a negative amount, for the accessibility tree only — R3.
 *
 * ⚠ THE SAME TREATMENT THE PORTAL LISTING ALREADY GIVES THE SAME STATE, reused rather than reinvented. A
 * negative fee there is painted in the danger colour at bold weight with this word clipped beside it, so the
 * sign is never carried by colour alone (WCAG 1.4.1); on this grid the identical state was a bare text node
 * in the ordinary colour and weight, indistinguishable from a positive amount but for one minus glyph.
 */
const NEGATIVE_FEE_QUALIFIER = 'negative';

/** The zero-result sentence for a page addressed beyond the end of a real result set — R5. */
const PAST_END_MESSAGE = 'This page is past the end of the results. Return to the first page.';

/** The wording of the return-to-first-page affordance — R5. */
const FIRST_PAGE_LABEL = 'First page';

/** The words behind {@link ABSENT_VALUE_MARK}, for the accessibility tree. */
const ABSENT_VALUE_DESCRIPTION = SHARED_ABSENT_VALUE_DESCRIPTION;

/**
 * What a stored frequency character means when it is not one of the six this console publishes wording
 * for. `Roles.BillingFrequency` and `Roles.TrialFrequency` are `char(1)`, and although
 * `FK_Roles_CodeFrequency` names a lookup table, a real installation can hold a code this console has no
 * word for. The character itself keeps being painted - it is load-bearing data and the legacy grid bound
 * the raw field - but it must not reach a reader as SILENCE, which is what an empty sr-only sibling was.
 */
const UNNAMEABLE_FREQUENCY_DESCRIPTION_PREFIX = 'frequency code ';

const UNNAMEABLE_FREQUENCY_DESCRIPTION_SUFFIX = ', name unavailable';

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
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE: exactly one per screen, stating that screen's
 * SCOPE - the record it acts on when the title does not already name it, otherwise what the screen is for
 * in one line - and never a status, a count or a progress readout.
 */
const PAGE_SUBTITLE =
  'The security roles on this site, and the groups they belong to.';

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
    // The free-text filter. The shared control owns its own debounce, trimming and clear affordance, so this
    // screen supplies only the prompt and consumes the term.
    SearchInputComponent,
    // The group-removal confirmation. Its presence in the DOM is what "open" means.
    // The ONE rendering of an absent value, shared with every other listing.
    AbsentValueComponent,
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


  /** Where this listing stands, so a form returning to it restores the same place. */

  private readonly listReturn = inject(ListReturnStore);

  /**
   * The tenant's protected role identifiers. ⚠ READ HERE FOR THE SAME REASON THE EDITOR READS THEM: the two
   * roles the portal depends on cannot be renamed or destroyed, and a listing that offers those commands
   * anyway sends the operator to a screen that refuses them - or, for removal, to a confirmation dialog for
   * an act the server will not perform.
   */
  private readonly portals = inject(PortalStore);

  /** Ties the address subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Read directly for ONE purpose: the membership count the removal dialog discloses. Everything else on this
   * screen goes through {@link RoleStore}, and this does not, because the count is a fact about a record the
   * operator is about to destroy rather than a slice of listing state - putting it in the store would give it
   * a lifetime beyond the dialog that owns it.
   */
  private readonly roleService = inject(RoleService);

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
   * Description cell. A template rather than a bound field because an absent description has to paint the
   * shared mark AND expose its clipped expansion, which is two elements, and a bound field emits one string.
   */
  @ViewChild('descriptionCell', { static: true })
  private descriptionCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

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

  /**
   * What the GRID is told about waiting, which is broader than "a request is in flight".
   *
   * ⚠ AN UN-ASKED LISTING IS A WAITING LISTING, NOT AN EMPTY ONE, and conflating the two is the measured
   * empty-table flash. The shared grid prefers its waiting placeholder over its empty one, so handing it
   * this instead of the raw in-flight flag is what stops a listing that has not been read yet from
   * asserting that the tenant has no roles. The store's latch is raised on a read's success AND on its
   * failure, so this cannot leave a spinner standing over a failure the grid is able to report.
   */
  protected readonly listWaiting = computed<boolean>(
    () => this.loading() || !this.store.listSettled(),
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

  /**
   * Why the removal command is absent while a real group is chosen, or `null` when there is nothing to
   * explain.
   *
   * ⚠ A HIDDEN COMMAND IS INDISTINGUISHABLE FROM A MISSING FEATURE. The server refuses to destroy a group
   * that still has roles in it (`role_group.in_use`), and the screen withheld the command silently - so an
   * operator looking for it concluded the application could not do it, rather than learning that the group
   * has to be emptied first. The condition is stated instead, and it states the REMEDY rather than only the
   * rule, because the remedy is what the operator has to act on.
   *
   * Returns `null` when no real group is chosen, because then no removal is expected and an explanation for
   * an absence nobody noticed is just noise.
   *
   * ⚠ THE COUNT IS THE SERVER'S, AND IT IS NAMED. Two things follow from that, and both were defects
   * before. First, the sentence survives filtering: it used to disappear at exactly the moment the filter
   * emptied the page, which is the moment the false Delete command appeared, so the operator lost the
   * explanation and gained an affordance that could not work. Second, it quantifies the remedy - a group
   * that classifies one role is one move away from being removable, and a group that classifies forty is
   * not, and "still has roles in it" said nothing about which.
   */
  protected readonly groupRemovalWithheldReason = computed<string | null>(() => {
    if (this.canRemoveSelectedGroup()) {
      return null;
    }

    const chosen: RoleGroup | null = this.selectedRoleGroup();

    if (chosen === null) {
      return null;
    }

    const classified: number = chosen.classifiedRoleCount;
    const roles: string = classified === 1 ? '1 role' : `${classified} roles`;

    return (
      // "contains", not "classifies". The server's refusal says "classifies" and the count travels as
      // `classifiedRoleCount`, but that is internal vocabulary; an operator reading a filter row wants the
      // plain relation between a group and the roles in it.
      `${chosen.roleGroupName} still contains ${roles}, so it cannot be deleted. ` +
      'Move or delete those roles first.'
    );
  });

  /** The prompt in the filter box, naming what the server actually matches on. */
  protected readonly searchPlaceholder = 'Filter by role name';

  /**
   * What the listing is filtered by, or `null` when nothing is.
   *
   * ⚠ IT NAMES THE ROLE NAME AND NOTHING ELSE, because that is all the server matches. `RoleRepository`
   * filters on `role.RoleName.ToLower().Contains(...)` alone - not the description, not the group - and copy
   * promising more would send an operator hunting for a role by a description that can never match.
   */
  protected readonly filterDisclosure = computed<string | null>(() => {
    const inForce: string | null = this.store.rolesPage().query ?? null;

    if (inForce === null || inForce.length === 0) {
      return null;
    }

    return `Filtered: role name contains “${inForce}”.`;
  });

  /**
   * Whether the default narrowing is hiding grouped roles, so the screen can say so.
   *
   * ⚠ THE DEFAULT NARROWS, AND SILENTLY. `< Global Roles >` lists only the roles belonging to no group at
   * all - measured at eleven of this tenant's fifteen - so an operator who could not find a role they knew
   * existed had no way to tell the listing was narrowed, because a default reads as "no filter". Stated only
   * when the tenant actually HAS groups, since with none there is nothing being hidden.
   */
  protected readonly defaultNarrowingInForce = computed<boolean>(
    () => this.store.groupFilter().kind === 'GlobalRoles' && this.store.hasRoleGroups(),
  );

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

  /**
   * Whether the ROLE LISTING READ failed, so zero rows describes a failure rather than a tenant with no
   * roles. Narrowed to the listing operation on purpose: a failed WRITE says nothing about whether the rows
   * on screen are trustworthy, and would wrongly withdraw the empty state after, say, a refused deletion.
   */
  protected readonly listFailed = computed<boolean>(
    () => this.store.failure()?.operation === 'loadRoles',
  );

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

  /**
   * What the grid's progress indicator says while a read is in flight. Names the collection rather than
   * saying "Loading…", so the announcement identifies WHAT is loading; the same label serves the first-read
   * placeholder and the refetch strip, so this screen has one loading vocabulary.
   */
  protected readonly loadingLabel = 'Loading roles…';

  protected readonly accountSubtitle = computed<string | undefined>(() => {
    const subject: number | undefined = this.userId();

    if (subject === undefined) {
      // Not filtered to one account, so the scope is the listing itself. The slot is never left empty:
      // under the subtitle rule every screen states its scope, and this listing's scope is the site's roles.
      return PAGE_SUBTITLE;
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

  /**
   * Accessible name of the inline group-edit affordance.
   *
   * ⚠ IT NAMES ITS OBJECT, BECAUSE ITS SIBLING DOES. This reused the row command's bare `Edit.Text`
   * while the button immediately beside it announced itself as "Delete role group" - so of the two group
   * commands sharing one row, one said what it acted on and the other did not, and a reader tabbing the
   * filter row met "Edit" with nothing to say WHICH of the several editable things on this screen it meant.
   * The ROW commands are unambiguous because each is named with its own role ("Edit QA Free Members"); this
   * one carried no such qualifier.
   */
  protected readonly editGroupLabel = 'Edit role group';

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

  /**
   * Whether a row is one of the two roles the portal protects.
   *
   * ⚠ THE SAME RULE THE EDITOR APPLIES, READ FROM THE SAME SOURCE. `EditRoles.ascx.vb` called
   * `ActivateControls(False)` for the administrator and registered-users roles and withheld the removal
   * command entirely, and the server refuses both writes with `role.protected`. Offering the commands in the
   * listing anyway meant an operator could press Delete on the administrators role, be asked to confirm the
   * destruction of the role their own access depends on, and only then be refused - which reads as a fault
   * in the application rather than as a rule.
   *
   * @param row The role being rendered.
   * @returns Whether the portal protects it.
   */
  protected isProtectedRole(row: RoleListItem): boolean {
    return row.roleId === this.portals.administratorRoleId()
      || row.roleId === this.portals.registeredRoleId();
  }

  /**
   * Why a protected role offers no editing or removal command, named for the row it belongs to.
   *
   * @param row The role being rendered.
   * @returns The explanation an operator reads instead of a refusal.
   */
  protected protectedRoleReason(row: RoleListItem): string {
    // The purpose is named because the two protected roles are protected for DIFFERENT reasons, and the
    // server says which: it refuses with "Role N is the portal's designated administrators role" or
    // "... designated registered members role". Saying only "required by this site" made the two rows
    // indistinguishable and told the operator nothing they could act on.
    const purpose: string = row.roleId === this.portals.administratorRoleId()
      ? 'administrators'
      : 'registered members';

    return `${row.roleName} is this site's designated ${purpose} role, `
      + 'so it cannot be edited or deleted.';
  }

  /**
   * The word rendered IN the command cell of a protected row.
   *
   * ⚠ A CELL THAT RENDERS ONLY AN EM DASH IS A CELL THAT SAYS NOTHING. The dash was marked
   * `aria-hidden`, the reason beside it was visually hidden, and the only carrier left for a sighted
   * operator was a `title` on a `<span>` - an element that takes no focus, so the tooltip was unreachable
   * by keyboard and the cell read as empty or broken rather than as governed by a rule. Naming the STATE in
   * the cell costs one word and makes the rule visible at a glance.
   *
   * The word is the server's own: both writes are refused with `role.protected`, so the screen and the
   * refusal speak one vocabulary rather than two.
   */
  protected readonly protectedRoleStateLabel = PROTECTED_ROLE_STATE_LABEL;

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
   * this reads as the same question with the answer to "which one" added. This dialog removes a role GROUP rather than a role, which is precisely the case where naming
   * matters most: the grid behind it lists roles, so nothing on screen states which group the question is
   * about.
   */
  protected readonly groupRemovalMessage: Signal<string> = computed<string>(() =>
    nameRemoval(this.pendingGroupRemoval()?.roleGroupName),
  );

  /**
   * The ROLE-removal confirmation body. ⚠ A SECOND COMPUTED RATHER THAN A SHARED ONE, and the split is the
   * correction. Both dialogs on this screen bound ONE `removalMessage`, which was defensible only while the
   * wording named nothing: the moment it names a record, one sentence cannot serve two different records,
   * and naming a role group in the dialog that destroys a ROLE would have been worse than naming nothing at
   * all.
   */
  protected readonly roleRemovalMessage: Signal<string> = computed<string>(() => {
    const named: string = nameRemoval(this.pendingRoleRemoval()?.roleName);
    const held: number | null = this.pendingRoleMemberCount();

    // ⚠ THE CASCADE IS DISCLOSED, AND ONLY ONCE IT IS KNOWN. Destroying a role also destroys every membership
    // in it, which the server does silently and the dialog did not mention - so an operator confirming the
    // removal of a role could not know they were also revoking it from everyone holding it. The count is read
    // when the dialog opens rather than carried on every listing row, because a per-row count would put a
    // correlated subquery on every read of the grid to answer a question almost no read asks.
    if (held === null) {
      return named;
    }

    if (held === 0) {
      return `${named} No accounts hold this role.`;
    }

    // Verb agreement as well as noun: "1 account currently hold" reads as a defect in the application.
    const subject: string = held === 1 ? 'account currently holds' : 'accounts currently hold';

    return `${named} ${held} ${subject} this role and will lose it.`;
  });

  /**
   * How many accounts hold the role awaiting confirmation, or `null` while that is unknown - either because
   * no dialog is open, or because the count has been asked for and has not arrived.
   *
   * `null` is deliberately indistinguishable between "not asked" and "could not be read": in both cases the
   * dialog states the removal without a count rather than guessing one, and a failure to read the count must
   * never block the removal itself.
   */
  private readonly pendingRoleMemberCount = signal<number | null>(null);

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
    // ⚠ WITHOUT THIS THE PROTECTED ROLES ARE NEVER RECOGNISED. `administratorRoleId` and `registeredRoleId`
    // are read from the tenant's own record, and nothing on this screen was asking for that record - so both
    // signals stayed `null`, `isProtectedRole` answered false for every row, and the withheld commands
    // rendered as ordinary ones. Runtime testing caught exactly this: the row corrected itself only on a
    // screen that had already loaded the record for its own reasons.
    //
    // Read from the CALLER'S identity rather than from a route, matching the editor, and idempotent in the
    // store - several screens asking on initialisation issue one request between them.
    const tenantId: number | undefined = this.auth.currentUser()?.portalId;

    if (tenantId !== undefined) {
      this.portals.loadCurrentPortalContext(tenantId);
    }

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
   * Replaces a group narrowing that names a group the client's own group set does not contain, leaving every
   * other part of the query alone.
   *
   * ⚠ AN UNREAD GROUP SET IS NOT AN EMPTY ONE, and the distinction is the whole guard - which is why it asks
   * whether the read SETTLED rather than whether the set has members. Both states hold an empty array, and
   * conflating them breaks the guard in both directions: treating unread as empty discards every legitimate
   * bookmarked narrowing, while treating a genuinely empty set as unread keeps re-issuing a read the server
   * can only refuse. Before the read settles the narrowing is left exactly as stated, and the first read of
   * an arrival is covered by the store, which heals the narrowing when the server reports the group gone.
   * Once settled, a key outside the set is dropped here so no refusable read is issued at all.
   *
   * @param query The query as the address states it.
   * @returns The query to apply, with an unreadable narrowing replaced by the default.
   */
  private withReadableNarrowing(query: RoleListQuery): RoleListQuery {
    const narrowing: RoleGroupFilter = query.groupFilter;

    if (narrowing.kind !== 'Group') {
      return query;
    }

    if (!this.store.roleGroupsSettled()) {
      return query;
    }

    const groups: readonly RoleGroup[] = this.store.roleGroups();

    if (groups.some((group: RoleGroup): boolean => group.roleGroupId === narrowing.roleGroupId)) {
      return query;
    }

    return { ...query, groupFilter: DEFAULT_ROLE_GROUP_FILTER };
  }

  /**
   * Applies the reader's free-text filter through the ADDRESS, which is what issues the read.
   *
   * The text is recorded exactly as the shared control emitted it - no wildcard is appended and no pattern
   * syntax is introduced, because match semantics belong to the server. Writing the ADDRESS rather than the
   * store keeps this listing's single source of truth intact: every other part of its coordinate travels the
   * same way, and a filter written straight to the store would be overwritten by the next emission.
   *
   * @param term The text the shared search control emitted, already debounced and trimmed by it.
   */
  protected onSearch(term: string): void {
    const wanted: string | null = term.trim().length === 0 ? null : term;

    // Remembered so the reconciliation in `ngOnInit` can tell this request's own echo from a coordinate
    // change arriving by another route, and leave live typing alone in the first case.
    this.ownSearchRequest = wanted;

    void this.router.navigate([], {
      relativeTo: this.route,
      // ⚠ THE PAGE IS DROPPED WITH THE FILTER. A filtered listing is shorter, so page four of the
      // unfiltered set is routinely past the end of the filtered one and would answer an empty grid for a
      // filter that matches plenty.
      queryParams: { [SEARCH_PARAM]: wanted, [PAGE_PARAM]: null },
      queryParamsHandling: 'merge',
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
  /**
   * The shared search control, so the box can be reconciled with the filter the ADDRESS actually holds.
   * `static: true` because the reconciliation below runs from the address subscription in `ngOnInit`,
   * which fires before the first change detection completes; a control resolved later would miss the
   * filter this screen arrived with.
   */
  @ViewChild(SearchInputComponent, { static: true })
  private searchBox?: SearchInputComponent;

  /**
   * The term this screen most recently ASKED the address for, or `undefined` when the last address change
   * came from somewhere else. Distinguishes the echo of this screen's own request - where the box already
   * holds the operator's text, possibly with more typed since - from a coordinate change arriving by any
   * other route, where the box may be stale and must be corrected.
   */
  private ownSearchRequest: string | null | undefined = undefined;

  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two reads of the same page on every arrival.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        // ⚠ A NARROWING THE GROUP SET NO LONGER CONTAINS IS DROPPED BEFORE IT IS EVER STAGED, which is what
        // stops the address re-issuing a read the server can only refuse. The address is what issues this
        // read, so a group deleted in this session - or in somebody else's - leaves an address that asks for
        // a group that is gone, and every later emission (a Back, a paging click, a sort) asked again and was
        // refused again. Correcting the QUERY rather than navigating separately means the existing
        // canonicalisation below carries the fix: the corrected query no longer matches the stated address,
        // so that branch rewrites the address and returns, and the single read comes from the re-emission.
        // Adding a second navigation source instead cost a duplicate listing read on every correction.
        const query: RoleListQuery = this.withReadableNarrowing(parseRoleListQuery(address));

        if (!addressStatesQuery(address, serialiseRoleListQuery(query))) {
          void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: serialiseRoleListQuery(query),
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });

          return;
        }

        // Remembered at the single point where the coordinate is settled and canonical, so every route
        // into a changed coordinate is covered without each handler having to say so.
        this.listReturn.remember(ROLE_LIST_ROUTE, serialiseRoleListQuery(query));

        // ⚠ THE BOX IS RECONCILED WITH THE ADDRESS HERE, AND HERE ONLY - QA-9.
        //
        // MEASURED FAULT IT ANSWERS: emptying the box and then immediately changing the group left
        // `search=` standing in the address while the box read empty, so the grid showed a narrowed - often
        // EMPTY - listing with nothing on screen saying what it was narrowed by. The mechanism is that the
        // box emits on a debounce, `cancelPendingSearch` DISCARDS a delay still in flight, and the group
        // change merges the address it finds; so the empty term the operator asked for could be dropped
        // before it was ever emitted while the group's own navigation carried the old term forward.
        //
        // Correcting it at the settled coordinate rather than inside `onGroupFilterChange` covers every
        // route into a changed narrowing with one rule - the group selector, a Back, a sort, a paging click,
        // and the group-deletion healing above - instead of leaving each handler to remember. The echo guard
        // is what keeps this from overwriting live typing: when the term in force is the one this screen
        // just asked for, the operator owns the box and it is left exactly as it is.
        if (this.ownSearchRequest === query.search) {
          this.ownSearchRequest = undefined;
        } else {
          this.ownSearchRequest = undefined;
          // `''` rather than `null`, because the box holds a string and an absent filter is an empty box.
          this.searchBox?.cancelPendingSearch(query.search ?? '');
        }

        this.store.stageListQuery(
          query.groupFilter,
          query.pageIndex,
          query.sortBy,
          query.sortDir,
          query.search,
        );

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
      // ⚠ EVERY COLUMN OF THIS GRID NOW DECLARES A WIDTH, AND THE PREVIOUS NOTE HERE HAS BEEN SUPERSEDED.
      // It recorded that `min-content` on the command columns made the "Manage Users" command overpaint the
      // Name column - 30.70px of overlap at 320, 16.22px still at 1024 - and concluded that declaring no
      // width at all was safer. Measurement of the result showed the conclusion cost more than it saved: with
      // no width anywhere, a fixed table layout gave all thirteen tracks the same share, so a hidden-label
      // command column was as wide as the Name column, names broke mid-word at 1440 ("Administrato / rs") and
      // every track resolved near 49px at 375.
      //
      // The real fault was the KIND of value, not the act of declaring one. `min-content` is not a length, so
      // the fixed algorithm cannot use it and falls back to the automatic share; a length resolves exactly as
      // written. The commands therefore take a length token sized for one interactive target plus the cell's
      // padding, and the remaining ten tracks take percentages weighted by what their content needs.
      {
        key: 'editCommand',
        label: this.editRoleLabel,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        // The command track, a LENGTH sized for the target it holds. `min-content` was tried here first and
        // is what caused the overlap the note above records: it is not a length, so the fixed table algorithm
        // fell back to the automatic share and the command overpainted the name column.
        width: 'var(--table-command-column-inline-size)',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.editCommandTemplate, 'editCommand'),
      },
      {
        key: 'membersCommand',
        label: this.manageUsersLabel,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        // The command track, a LENGTH sized for the target it holds. `min-content` was tried here first and
        // is what caused the overlap the note above records: it is not a length, so the fixed table algorithm
        // fell back to the automatic share and the command overpainted the name column.
        width: 'var(--table-command-column-inline-size)',
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
        // The command track, a LENGTH sized for the target it holds. `min-content` was tried here first and
        // is what caused the overlap the note above records: it is not a length, so the fixed table algorithm
        // fell back to the automatic share and the command overpainted the name column.
        width: 'var(--table-command-column-inline-size)',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.deleteCommandTemplate, 'deleteCommand'),
      },

      // 3. `asp:boundcolumn DataField="RoleName"`.
      // ⚠ THIS COLUMN IS THE DONOR THAT PAYS FOR THE SIX HEADINGS BESIDE IT, and the reduction from the 18%
      // it once declared is deliberate rather than a tuning accident. This is the widest grid in the
      // application at thirteen columns, and measurement at the table's floor width showed six of its
      // headings being ellipsised — "Public", "Auto", and both wrapped lines of "Billing Every", "Billing
      // Period", "Trial Every" and "Trial Period" — because each of those columns had been given less room
      // than its own heading text needs once the 24px of fixed heading overhead is taken out. The width had
      // to come from somewhere on the same grid, and a name was the place it was taken from. The full
      // arithmetic, and the two separate mechanisms by which a heading is lost, are recorded once on the
      // shared column contract rather than restated here.
      //
      // ⚠ AND THE REASONING ABOVE CONTAINED ONE FALSE PREMISE, WHICH IS WHY 3.5% OF IT IS BEING REPAID. It
      // says a role name "WRAPS and stays wholly legible at a narrower measure". It does not: this column is
      // `atomic` — the annotation immediately below says so, and says why — so its cells do not wrap, they
      // ELLIPSISE. The donation was therefore not a trade of wrapping against clipping; it was a trade of one
      // clipped thing for another. Measured on the rendered grid at 1440, SEVEN of ten role names painted with
      // an ellipsis in a 125.78px track, the longest needing 315.77px.
      //
      // The repayment comes from the four columns that measurement showed holding genuine surplus rather than
      // from the description, so the slack column keeps every pixel it had:
      //
      //     Fee     6% -> 5.5%   heading needs 49.85, had 57.59 at the floor
      //     Trial   7.5% -> 6%   heading needs 54.85, had 72.00
      //     Public  8% -> 7%     heading needs 53.01 of word, had 76.80
      //     Auto    6.5% -> 6%   heading needs 42.15 of word, had 62.39
      //
      // None of those four is atomic, so what the reduction costs them is a heading laid out on two lines or a
      // sort indicator sitting under its label — never a character. The 3.5% they release takes this column
      // from 125.78px to 167.72px at 1440 and from 100.80px to 134.40px at the floor, which holds the common
      // role names outright; the exceptional ones stay recoverable through the shared grid's truncation
      // affordance.
      {
        key: 'roleName',
        width: '14%',
        // ⚠ ATOMIC BECAUSE A ROLE NAME IS NOT A PHRASE, AND WRAPPING ONE FRACTURES IT. The shared stylesheet
        // lets any cell break inside a word so a narrow column never overflows, which is right for prose and
        // wrong for a value read as a single token: measured at a 768 viewport, this column rendered
        // `Administrators` as `Administrator` + `s`, in a 100.80px track.
        // Marked atomic the value stays on one line and a column too narrow to hold it ellipsises instead, so
        // what is on screen is a recognisable prefix rather than two fragments that read as corruption. The
        // whole value stays in the accessibility tree either way. The weights are derived as a set and still
        // sum to the same total after the repayment recorded above.
        atomic: true,
        rowHeader: true,
        // Ordering: the key IS the endpoint's own sort name. See the sortability note on `columns`.
        sortable: true,
        label: NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'roleName',
      },

      // 4. `asp:boundcolumn DataField="Description"`. Nullable on the contract.
      //
      // ⚠ QA-20 — AN ABSENT DESCRIPTION PAINTED NOTHING, AND THIS GRID DISAGREED WITH ITSELF ABOUT IT. Four
      // other columns on this very row — both fees, both periods, both frequencies — already answered an
      // absent value with the shared mark and its clipped words, while this one rendered an empty cell that a
      // reader could not tell from a cell that had failed to draw. It was the last column here bound as a
      // plain field, which is why it was the last one still doing it: a field column emits one string and
      // cannot emit a mark plus a hidden sentence.
      {
        key: 'description',
        sortable: true,
        label: DESCRIPTION_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.descriptionCellTemplate, 'descriptionCell'),
        // ⚠ THE ONE COLUMN ON THIS GRID THAT DECLARES NO WIDTH, AND ONE MUST NOT.
        //
        // Under `table-layout: fixed` the leftover after the percentage tracks goes to whichever columns did
        // NOT declare a percentage — which, with everything weighted, meant the three icon command columns.
        // They asked for 3.25rem each and painted 63.906px, so a sixteen-unit glyph sat in a track wider than
        // the fee columns beside it. The description is the right place for the slack: it is the longest
        // free-text value here, it wraps cleanly at word boundaries, and losing a few characters of it at a
        // narrow width costs a reader far less than losing them from a role's name.
      },

      // 5. Template column over `FormatPrice(ServiceFee)`.
      {
        key: 'serviceFee',
        // Reduced by half a point to repay the name column. The heading wraps rather than clipping, and it
        // still has 52.80px at the table's floor against the 49.85px it needs.
        width: '5.5%',
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
        // ⚠ FUNDS THE PUBLIC/AUTO CORRECTION, AND CAN AFFORD TO. This heading is TWO WORDS, so the
        // shared label box wraps it rather than ellipsising it - measured: "Billing Every" wants 90.86px
        // in a 52.8px box and simply takes two lines. A narrower track therefore costs a line of heading
        // height and no text, which is exactly the trade the single-word headings beside it CANNOT make.
        // Its values are one or two characters wide, so nothing in the body is at risk either. Taken from
        // here rather than from the description slack, which a specification holds at a fixed budget.
        width: '7.5%',
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
        // ⚠ FUNDS THE PUBLIC/AUTO CORRECTION, AND CAN AFFORD TO. This heading is TWO WORDS, so the
        // shared label box wraps it rather than ellipsising it - measured: "Billing Every" wants 90.86px
        // in a 52.8px box and simply takes two lines. A narrower track therefore costs a line of heading
        // height and no text, which is exactly the trade the single-word headings beside it CANNOT make.
        // Its values are one or two characters wide, so nothing in the body is at risk either. Taken from
        // here rather than from the description slack, which a specification holds at a fixed budget.
        width: '7.75%',
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
        // Reduced to repay the name column: this was the largest single surplus on the grid, 50.99px at 1440,
        // for a heading of one short word. 57.60px remains at the floor against 54.85px needed.
        width: '6%',
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
        width: '7.5%',
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
        // ⚠ FUNDS THE PUBLIC/AUTO CORRECTION, AND CAN AFFORD TO. This heading is TWO WORDS, so the
        // shared label box wraps it rather than ellipsising it - measured: "Billing Every" wants 90.86px
        // in a 52.8px box and simply takes two lines. A narrower track therefore costs a line of heading
        // height and no text, which is exactly the trade the single-word headings beside it CANNOT make.
        // Its values are one or two characters wide, so nothing in the body is at risk either. Taken from
        // here rather than from the description slack, which a specification holds at a fixed budget.
        width: '7.5%',
        sortable: true,
        label: TRIAL_PERIOD_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.frequencyCellTemplate, 'frequencyCell'),
      },

      {
        key: 'isPublic',
        // ⚠ RAISED BACK, BECAUSE THE PREDICTION IN THE COMMENT THIS REPLACES WAS MEASURED WRONG. It read
        // "a single 45.01px word that still fits outright ... costs a line of heading height and no text at
        // all", and that holds at a wide viewport where the table takes the container's width. It does NOT
        // hold at the table's own 60rem minimum, which is what 768 and 320 both collapse to: 7% of 960px is
        // 67.2px, and after the cell padding and the 12px sort-indicator gutter the label box measures
        // 43.19px against a 45.01px word. Over by 1.82px - and because the ellipsis glyph takes width of its
        // own, that 1.82px cost SEVERAL characters, painting the heading as "Pu…".
        //
        // A single-word heading cannot wrap its way out of this the way "Billing Every" does, so the width
        // has to hold it. 7.75% of 960px is 74.4px, leaving the label box about 50px for a 45.01px word.
        // Repaid from `description`, which declares no width and absorbs the remainder by design.
        width: '7.75%',
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
        // Raised for the same measured reason as "Public" beside it, and the claim that "the word itself fits
        // at every width" was wrong in the same way: at the table's 60rem minimum the label box measured
        // 33.59px against a 34.15px word, so "Auto" painted as "A…" - a heading reduced to one letter by a
        // 0.56px shortfall. 6.5% of 960px gives the box about 38px. Repaid from `description`.
        width: '6.5%',
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

  /**
   * Whether the listing is currently narrowed to one group, which is the only empty state a reader can widen
   * their way out of. The two synthetic filters — every role, and the roles that belong to no group — are not
   * narrowings of anything, so offering to widen them would offer nothing.
   *
   * @returns True when a real group is selected.
   */
  protected isNarrowedByGroup(): boolean {
    return this.store.groupFilter().kind === 'Group';
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
    this.pendingRoleMemberCount.set(null);
    this.pendingRoleDeletion.set(role);

    // ONE record is asked for and only the TOTAL is used, so the answer is as small as the contract allows.
    // The subscription is bounded by this component's lifetime; an operator who dismisses the dialog and
    // leaves before the count lands simply never sees it, and the removal is unaffected either way.
    this.roleService
      .listUsers(role.roleId, { pageIndex: 0, pageSize: 1 })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          // Discarded if the operator has moved on to a different role in the meantime, so a slow answer for
          // one record can never be attributed to another.
          if (this.pendingRoleDeletion()?.roleId === role.roleId) {
            this.pendingRoleMemberCount.set(page.meta.totalCount);
          }
        },
        // A count that cannot be read leaves the dialog as it was. Reporting a failure for a figure the
        // operator did not ask for would raise an alarm about the removal itself, which is still available.
        error: () => undefined,
      });
  }

  /**
   * Removes the confirmed role. ⚠ NOT GATED ON WHETHER THE ROLE LOOKS REMOVABLE, and that is the same
   * rule the group removal follows.
   */
  protected onRoleRemovalConfirmed(): void {
    const role: RoleListItem | null = this.pendingRoleDeletion();

    this.pendingRoleDeletion.set(null);
    this.pendingRoleMemberCount.set(null);

    if (role === null || this.saving()) {
      return;
    }

    this.awaitedMutation.set({ operation: 'deleteRole', subjectName: role.roleName });
    this.store.deleteRole(role.roleId, { thenReadListing: true });
  }

  /** Dismisses the role confirmation without removing anything. */
  protected onRoleRemovalCancelled(): void {
    this.pendingRoleDeletion.set(null);
    this.pendingRoleMemberCount.set(null);
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

  /** The word a fee of exactly zero is named with, in place of a price nobody can tell from another price. */
  protected readonly freeFeeLabel = FREE_FEE_LABEL;

  /** What that word means, announced in full alongside the amount it stands for. */
  protected readonly freeFeeDescription = FREE_FEE_DESCRIPTION;

  /** The clipped word behind a negative amount — R3. */
  protected readonly negativeFeeQualifier = NEGATIVE_FEE_QUALIFIER;

  /** {@link ZERO_PERIOD_DESCRIPTION}, for the template. */
  protected readonly zeroPeriodDescription = ZERO_PERIOD_DESCRIPTION;

  /** {@link UNITLESS_PERIOD_DESCRIPTION}, for the template. */
  protected readonly unitlessPeriodDescription = UNITLESS_PERIOD_DESCRIPTION;

  /** The wording of the return-to-first-page affordance — R5. */
  protected readonly firstPageLabel = FIRST_PAGE_LABEL;

  /**
   * Whether a fee column holds an amount below zero — R3.
   *
   * An ABSENT amount is not a negative one even though its sentinel is far below zero, so the absent test
   * runs first and owns that row; only a real stored amount can be marked. Zero is not marked either: the
   * distinction being drawn is a charge against a credit, and there is nothing negative about nothing.
   *
   * @param role The row.
   * @param key Which of the two fee columns is being drawn.
   * @returns True when the stored amount is below zero.
   */
  protected isFeeNegative(role: RoleListItem, key: string): boolean {
    if (this.isFeeAbsent(role, key)) {
      return false;
    }

    const price: number | null = key === 'trialFee' ? role.trialFee : role.serviceFee;

    return price !== null && Number.isFinite(price) && price < 0;
  }

  /**
   * Whether the page IN HAND holds rows, which is what mounts the pager — R5.
   *
   * ⚠ NARROWER THAN THE TOTAL, AND THE DIFFERENCE IS THE DEFECT. A page past the end of a real result set
   * has a total and no rows, so a pager mounted on the total alone painted a range - measured as
   * "21-30 of 30" - beside a grid reading "No records found.", describing records it cannot show. The portal
   * and module listings gate on their rows for exactly this reason.
   */
  protected readonly hasRows = computed<boolean>(() => this.roles().length > 0);

  /** Whether the address names a page beyond the end of the result set — R5. */
  protected readonly isPastEnd = this.store.isPastEnd;

  /**
   * The zero-result wording, chosen from the state that actually holds — R5.
   *
   * Three states, three sentences: past the end of a real result set, a narrowing that matched nothing, and
   * a tenant with no roles at all. Answering all three with one sentence is what let a populated range stand
   * beside "No records found." with no route back.
   *
   * ⚠ THE NARROWING TEST IS {@link isNarrowedByGroup} AND NOT "anything but every role". The two synthetic
   * filters - every role, and the roles that belong to no group - are not narrowings of anything, so offering
   * to widen them would offer nothing.
   */
  protected readonly emptyMessage = computed<string>(() => {
    if (this.isPastEnd()) {
      return PAST_END_MESSAGE;
    }

    // ⚠ THE NAME FILTER IS TESTED BEFORE THE GROUP, because a filter the reader just typed is the narrowing
    // they are holding in mind, and it is the one they can undo in a single action. Reusing
    // `filterDisclosure` as the test rather than reading the term again keeps the two statements on screen
    // from ever disagreeing: the sentence claiming a filter is in force and the sentence explaining the
    // empty result are now driven by one value.
    if (this.filterDisclosure() !== null) {
      return EMPTY_SEARCH_MESSAGE;
    }

    return this.isNarrowedByGroup() ? EMPTY_GROUP_MESSAGE : EMPTY_LISTING_MESSAGE;
  });

  /** Returns to the first page, which is the only recovery from an address past the end — R5. */
  protected onReturnToFirstPage(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [PAGE_PARAM]: null },
      queryParamsHandling: 'merge',
    });
  }

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
  /**
   * Whether a role's description is absent. Null and whitespace-only both count: the contract admits null,
   * and a description of spaces is not a description — collapsing the two here is what stops one row saying
   * "not recorded" while the next says nothing at all for the same practical state.
   *
   * @param row The role being rendered.
   * @returns True when there is no description to show.
   */
  protected isDescriptionAbsent(row: RoleListItem): boolean {
    return row.description === null || row.description.trim().length === 0;
  }

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
   * Whether a fee column holds a recorded amount of exactly nothing — a FREE role.
   *
   * ⚠ ZERO AND A REAL PRICE WERE INDISTINGUISHABLE, WHICH IS THE MEASURED DEFECT THIS ANSWERS. A stored zero
   * painted "0.00" in the same colour and the same weight as "0.01", so the one fact an operator scans this
   * column for - is this role free? - could only be established by reading two decimal places. The negative
   * amount already carried a word beside it for exactly this reason; zero now does too. Absence is a different
   * state again and is answered by {@link isFeeAbsent} before this is ever reached.
   *
   * @param role The row.
   * @param key Which of the two fee columns is being drawn.
   * @returns True when the stored amount is present and equal to zero.
   */
  protected isFeeFree(role: RoleListItem, key: string): boolean {
    const stored: number | null = key === 'trialFee' ? role.trialFee : role.serviceFee;

    return stored !== null && Number.isFinite(stored) && stored === 0;
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
   * Whether a period column holds a recorded count of exactly nothing.
   *
   * ⚠ ZERO IS NOT A SHORTER PERIOD, IT IS THE ABSENCE OF ONE. "Every 0 months" describes no recurrence, so a
   * row carrying it has no working schedule - and it was painted in the same colour and weight as a genuine
   * count. This is the same question, and the same answer, that {@link isFeeFree} already gives for a recorded
   * zero amount one column to the left. Absence is a different state again and {@link isPeriodAbsent} answers
   * it before this is reached.
   *
   * @param role The row.
   * @param key Which of the two period columns is being drawn.
   * @returns True when the stored count is present and equal to zero.
   */
  protected isPeriodZero(role: RoleListItem, key: string): boolean {
    const stored: number | null = key === 'trialPeriod' ? role.trialPeriod : role.billingPeriod;

    return stored !== null && Number.isFinite(stored) && stored === 0;
  }

  /**
   * Whether a period count is recorded but its unit cannot be named.
   *
   * ⚠ THE TWO CELLS DESCRIBED THE SAME ROW DIFFERENTLY. The frequency cell tells an absent unit and an
   * unrecognised stored code apart from a nameable one and marks both; the count beside it was drawn the same
   * way whichever of the three was in force. A count is only a period once its unit is known, so the count now
   * carries the same doubt its unit does.
   *
   * @param role The row.
   * @param key Which of the two period columns is being drawn.
   * @returns True when a count is present and the frequency governing it cannot be named.
   */
  protected isPeriodUnitless(role: RoleListItem, key: string): boolean {
    const frequencyKey: string = key === 'trialPeriod' ? 'trialFrequency' : 'billingFrequency';

    // ⚠ RECORDED-BUT-UNNAMEABLE ONLY, AND AN ABSENT UNIT IS DELIBERATELY EXCLUDED. Reading an absent frequency
    // as "not recognised" was wrong twice over: it states the wrong thing - nothing was recorded, so there is
    // nothing to fail to recognise - and it is the ordinary state of every role with no paid membership at all,
    // so it would have hung a doubt on the commonest row on the grid. That state is already told correctly by
    // the frequency cell's own absence mark, and the count beside it stays exactly as the legacy grid drew it.
    return (
      this.isFrequencyAbsent(role, frequencyKey) === false &&
      this.isNameableFrequency(role, frequencyKey) === false
    );
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

  /**
   * Whether a row's stored frequency character is one this console can name.
   *
   * @param role The row.
   * @param key Which of the two frequency columns is being drawn.
   * @returns True when a word exists for the stored character.
   */
  protected isNameableFrequency(role: RoleListItem, key: string): boolean {
    return this.frequencyName(role, key) !== '';
  }

  /**
   * What a row's stored frequency character means, ALWAYS in words and never the empty string.
   *
   * ⚠ THIS IS THE FIX FOR A CELL THAT REACHED ASSISTIVE TECHNOLOGY AS NOTHING AT ALL. The painted character
   * sits in an `aria-hidden` span, and the sr-only sibling beside it used to be rendered only when a word
   * existed - so `Q`, `X` or `Z` painted a bare 9x15 pixel letter with no legend anywhere and handed a
   * screen-reader user an EMPTY cell. Three separate treatments are returned here: the word for a published
   * code, wording naming the stored code for one that has no word, and the shared absent-value wording when
   * the column is null.
   *
   * @param role The row.
   * @param key Which of the two frequency columns is being drawn.
   * @returns Wording that is never empty.
   */
  protected frequencyDescription(role: RoleListItem, key: string): string {
    const code: string = this.frequencyCode(role, key);

    if (code === '') {
      return ABSENT_VALUE_DESCRIPTION;
    }

    const named: string = this.frequencyName(role, key);

    if (named !== '') {
      return named;
    }

    return (
      `${UNNAMEABLE_FREQUENCY_DESCRIPTION_PREFIX}${code}` +
      UNNAMEABLE_FREQUENCY_DESCRIPTION_SUFFIX
    );
  }

  /**
   * Whether a row's frequency column holds nothing at all, as distinct from holding a code with no word.
   *
   * @param role The row.
   * @param key Which of the two frequency columns is being drawn.
   * @returns True when the column is null.
   */
  protected isFrequencyAbsent(role: RoleListItem, key: string): boolean {
    return this.frequencyCode(role, key) === '';
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

  // ─────────────────────────────────────────────────────────────────────────────────────────────────
  // DELIBERATELY NOT DONE: rewriting the ADDRESS when the selected group is deleted - QA-9.
  //
  // OBSERVED: deleting the group the listing is narrowed to leaves `?group=<id>` standing in the address
  // for a group that no longer exists. The LISTING recovers correctly - the selector falls back to the
  // default and the right roles are shown - but the address string keeps the dead key.
  //
  // A correction was written, measured, and REMOVED, and the measurement is the reason. Navigating to drop
  // the key supplies a second navigation source, and the address subscription above issues the listing read
  // on every emission - so the correction bought a tidier URL at the price of a DUPLICATE listing read on
  // every group deletion, on top of the re-read the delete already performs. Its own spec proved it: adding
  // the navigation turned one green test red with "Cannot flush a cancelled request", because the extra
  // navigation cancelled the read already in flight. The comment on that subscription had recorded this
  // exact cost in advance.
  //
  // Leaving it is safe because the dead key is already healed on every route back INTO the screen, by two
  // layers that are both tested: the store heals it when the server reports the group gone (the first read
  // of an arrival, before the group set is known), and `withReadableNarrowing` drops it from every later
  // emission once the set IS known. So a Back, a bookmark and a shared link all land on a populated
  // listing. What survives is a momentarily inaccurate URL string on a screen that is showing the correct
  // rows - not a state anyone can act on wrongly, and not worth a duplicate read of the whole listing.
  // ─────────────────────────────────────────────────────────────────────────────────────────────────

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
