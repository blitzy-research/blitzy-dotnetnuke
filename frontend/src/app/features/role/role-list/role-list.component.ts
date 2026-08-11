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

// ---------------------------------------------------------------------------
//  THE ADDRESS
//
//  This listing keeps its narrowing and its page in the address, so a reload, a bookmark and the browser's
//  own back and forward buttons all reproduce what is on screen. Runtime testing measured what the absence
//  of that cost on the sibling portal listing: two pager clicks advanced the grid while the address stayed
//  on the bare route, pressing back from page three was not possible because no history entry had ever been
//  created, and a fresh sidebar arrival landed on a page and a narrowing the operator could not see, because
//  this store is provided at the application root and OUTLIVES this route.
//
//  The page parameter and its readers are shared with the other three listings - see
//  `core/utils/list-query.util.ts` - so that one vocabulary covers every grid. Only the narrowing parameter
//  is declared here, because only this listing has one.

/**
 * Address parameter carrying the narrowing.
 *
 * ⚠ THE ADDRESS DOES NOT CARRY THE LEGACY MAGIC NUMBERS, and that is deliberate. The dropdown's own
 * `option` values are still `-2` and `-1` below, because those are the legacy values and the selector is a
 * faithful port of `Roles.ascx.vb:L112-L126`. An ADDRESS is different: it is read and hand-edited by
 * operators, and `?group=-2` says nothing to anybody, whereas `?group=all` says exactly what it does. The
 * two vocabularies are mapped to each other in {@link parseAddressGroupFilter} and
 * {@link groupFilterParameter} and nowhere else.
 */
const GROUP_PARAM = 'group';

/**
 * The column keys this listing offers an ordering on.
 *
 * ⚠ EVERY MEMBER WAS VERIFIED AGAINST THE RUNNING ENDPOINT, because an affordance that produces a refused
 * request is worse than no affordance at all. `GET /api/v1/roles` answers an unaccepted `sortBy` with a
 * field-level `400` naming its whole accepted set - AutoAssignment, BillingFrequency, BillingPeriod,
 * Description, IsPublic, RoleId, RoleName, ServiceFee, TrialFee, TrialFrequency and TrialPeriod - and each of
 * the ten keys below was issued against it and observed to return `200` with a genuinely different order.
 * The binder is case-insensitive, so the grid's own camel-cased keys are sent verbatim and no name mapping
 * is needed or performed.
 *
 * `RoleId` is accepted by the endpoint but is NOT offered here: this grid renders no identifier column, so
 * there would be no heading to attach the ordering to.
 *
 * MIGRATION: sorting is a NET ADDITION. The legacy grid was declared without `AllowSorting`, so it offered
 * none - and a note in this file previously cited that absence as the reason to offer none here. That
 * reading is SUPERSEDED, on two authorities that outrank legacy parity: AAP 0.3.2 specifies `sortBy`,
 * `sortDir` and `sortChange` on the shared record grid, and the review recorded their absence on this
 * listing as an AAP compliance failure rather than a design choice, observing that one of four listings
 * having an ordering makes the other three an internal inconsistency. On 145 roles at ten to a page, an
 * ordering is also the only way to bring a named row within reach without knowing its page.
 */
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
 * The {@link GROUP_PARAM} value standing for every role in the portal, whatever its grouping.
 */
const ALL_GROUPS_TOKEN = 'all';

/**
 * The {@link GROUP_PARAM} value standing for the roles belonging to no group at all.
 */
const UNGROUPED_TOKEN = 'none';

/** `option` value standing for the legacy "&lt; All Roles &gt;" pseudo-filter (legacy `-2`). */
const ALL_ROLES_FILTER_VALUE = -2;

/** `option` value standing for the legacy "&lt; Global Roles &gt;" filter (legacy `-1`). */
const GLOBAL_ROLES_FILTER_VALUE = -1;

/**
 * The narrowing and the page this listing is showing, as the address states them.
 *
 * Held as one object because the two are restored TOGETHER on entry. Applying them one at a time would
 * issue two reads for one view, and the narrowing would reset the page on its way through - discarding the
 * page the address had just asked for.
 */
interface RoleListQuery {
  /** The narrowing to apply. Never null: an absent parameter resolves to the legacy default. */
  readonly groupFilter: RoleGroupFilter;

  /** The page to read, counted from nought. */
  readonly pageIndex: number;

  /**
   * The column key to order by, or `null` to accept the endpoint's own default ordering.
   *
   * Constrained to {@link SORTABLE_COLUMN_KEYS} by the reader, so a hand-written address naming a column the
   * endpoint would refuse resolves to `null` and lists the default order rather than producing a `400`.
   */
  readonly sortBy: string | null;

  /** The direction to order in, or `null`. Only ever set alongside {@link RoleListQuery.sortBy}. */
  readonly sortDir: SortDirection | null;
}

/**
 * Reads a narrowing out of an address.
 *
 * An absent or unusable value resolves to {@link DEFAULT_ROLE_GROUP_FILTER} rather than throwing. That
 * default is the measured legacy one - `Roles.ascx.vb:L48` initialises the field to `-1`, the ungrouped
 * narrowing per `:L114` - so a bare `/roles` lists exactly what the legacy screen's first paint listed.
 *
 * A group key is admitted at nought, because `RoleGroups.RoleGroupID` is seeded `IDENTITY(0, 1)` and the
 * first group any portal creates has key zero. `Number.isInteger` is used rather than a truthiness or sign
 * test for that reason.
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
 * The default narrowing is written as ABSENCE, so the address of a listing whose narrowing an operator has
 * not changed is the bare route.
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
 * A default coordinate is emitted as `null`, which the router REMOVES from the address rather than writing
 * as an empty value - so the unnarrowed first page is the bare path.
 *
 * ⚠ THE KEYS THIS RETURNS ARE THE ONLY ONES RECONCILIATION EXAMINES, which is what keeps `?userId=` - the
 * account this screen can be narrowed to by its route-bound input - from being read as a discrepancy and
 * cleared away on entry.
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

/**
 * The smallest amount whose hundredths a double can no longer represent exactly — R-M4.
 *
 * `Number.MAX_SAFE_INTEGER` is the largest integer an IEEE-754 double holds exactly; expressed in
 * hundredths, dividing it by a hundred gives the largest amount whose every cent is exact. At or
 * above this magnitude the figure that arrives on the wire has already moved from the figure the
 * `money` column stores, so a cell painting it is painting an approximation. Computed rather than
 * written as a literal, so it cannot drift from the platform bound it describes, and it is the same
 * bound the role editor's storability rule uses to refuse an amount it cannot carry.
 */
const EXACT_CENTS_BOUND = Number.MAX_SAFE_INTEGER / 100;

/**
 * The clipped qualifier beside an amount too large to state exactly — R-M4.
 *
 * Announced and never drawn, on the same footing as the absent-value description beside it: a
 * reader who cannot see the figure is told it is approximate, and a reader who can is not read a
 * sentence on a row whose ordinary neighbours say nothing. The word is authored — no legacy screen
 * had the concept, because the legacy read this column into a single-precision float and lost far
 * more of it without remarking on that either.
 */
const APPROXIMATE_VALUE_DESCRIPTION = 'approximate';

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

/**
 * The same lead-in for the case where the person's own name is known.
 *
 * Two constants rather than one with the word "account" interpolated, because "Roles held by account
 * Runtime Member" reads as though "account" were part of the name. The identifier form keeps the word
 * because a bare number needs it.
 */
const ACCOUNT_SUBJECT_PREFIX_NAMED = 'Roles held by';

/** The affordance that returns to the account whose memberships are on screen. */
const SUBJECT_ACCOUNT_LABEL = 'Back to Account';

/**
 * How an account is named in the subtitle.
 *
 * ⚠ THE DISPLAY NAME MAY BE THE EMPTY STRING RATHER THAN ABSENT, so this cannot be a null check.
 * `Null.vb` L71 makes "" a stored value rather than an absence, the contract declares `displayName`
 * non-nullable, and the account listing on this tenant does return "" for members whose display name
 * the tenant hides — so an empty name must fall through to the login name rather than render as a
 * subtitle that names nobody. The login name is never empty: it is the account's key in the legacy
 * membership store.
 *
 * MIGRATION: the measured screen rendered `objUser.Username` into its heading
 * (`SecurityRoles.ascx.vb` L319 reads the account for exactly that), so the login name is the
 * measured choice and the display name is preferred over it only because it is the friendlier of the
 * two when present.
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

/**
 * The store operations whose failure belongs INLINE, on the error banner.
 *
 * A listing that could not be fetched is not transient: the screen has nothing to show
 * and the message has to persist next to the empty grid. Outcomes of operations the
 * reader initiated go to the notification queue instead, so the two channels never
 * compete for the same failure.
 */
/**
 * The read failures this screen states in its own banner.
 *
 * ⚠ THE NARROWED READ IS HERE BECAUSE ITS ABSENCE WAS A CRITICAL DEFECT. It records a failure like
 * any other read, but this list did not include it, so `/roles?userId=999` - which the endpoint
 * refuses with `404` "Portal -1 has no member bearing identifier 999" - reported nothing at all: no
 * banner, no notification, no console error. Combined with the listing falling back to the unnarrowed
 * rows, the screen asserted in writing that a non-existent account held every role in the tenant.
 */
const INLINE_FAILURE_OPERATIONS: readonly RoleStoreOperation[] = [
  'loadRoles',
  'loadRoleGroups',
  'loadRolesHeldByUser',
];

/**
 * The empty row set, shared rather than built per evaluation.
 *
 * A fresh literal would be a new reference on every read of the computed that returns it, which
 * would defeat the change-detection saving the shared table gets from an unchanged input.
 */
const NO_ROLES: readonly RoleListItem[] = Object.freeze([]);

/** Name of the group-name control, matched against the problem document's field keys. */
/**
 * The mark drawn where a period or a fee was never recorded — an em dash.
 *
 * The same mark the portal listing uses for the same purpose, so one absence reads identically across
 * the application. It is decorative and is paired with {@link ABSENT_VALUE_DESCRIPTION}, so the mark is
 * never the only thing a reader receives.
 */
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
  readonly operation: Extract<
    RoleStoreOperation,
    'deleteRoleGroup' | 'updateRoleGroup' | 'deleteRole'
  >;

  /**
   * The subject's name at the moment the request was issued, for the outcome wording.
   *
   * Named for the SUBJECT rather than for the group, because a role removal is now announced
   * through the same bridge. The name is captured at request time deliberately: the record it
   * describes is gone by the time the outcome settles, so reading the name from the store then
   * would either find nothing or find a different row.
   */
  readonly subjectName: string;
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
    FocusFirstInvalidDirective,
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

  /** Used to write the narrowing and the page into the address rather than holding them privately. */
  private readonly router = inject(Router);

  /** Ties the address subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

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
   * The account transport, used for exactly one thing: naming the subject account.
   *
   * A CORE service, not the account feature's. `core/services` is shared by every feature and the
   * sibling assignment screen of this same feature already injects this one, so reaching for it here
   * introduces no coupling that did not already exist.
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

  /**
   * Row delete command.
   *
   * MIGRATION: AN ADDITION WITH NO LEGACY COLUMN BEHIND IT, and it is recorded as one.
   * `roles.ascx:L34-L35` declares exactly two image command columns, `Edit` and `UserRoles`, and no
   * delete command anywhere; the grid's `cmdDelete` sibling is the ROLE-GROUP removal button
   * (`Roles.ascx.vb:L81-L86`), gated on the selected group holding no roles, not a row command. Role
   * removal lived on the role editor and still does — this command does not replace it and performs no
   * removal of its own beyond what that editor's own command performs.
   *
   * It is added because functional parity is a FLOOR rather than a ceiling and the three sibling
   * listings in this application — portals, accounts and modules — each carry a row-level removal with
   * the same shared confirmation. Roles was the one listing where the workflow existed but could only
   * be found by opening a role first, which runtime testing recorded as the reason it looked absent.
   * The safety concern that a destructive command in a dense grid invites a mis-press is answered by
   * construction rather than by argument: it routes through the same shared dialog every other removal
   * uses, marked destructive, and that dialog names what will be removed before anything happens.
   */
  @ViewChild('deleteCommand', { static: true })
  private deleteCommandTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /**
   * Fee cell, shared by the service-fee and trial-fee columns.
   *
   * A template for the same reason the period cell is one, and it arrived for the same reason a browser
   * pass found: an absent amount rendered blank while the absent count beside it carried a mark.
   */
  @ViewChild('feeCell', { static: true })
  private feeCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /**
   * Period cell, shared by the billing and trial period columns.
   *
   * A template rather than a bound value because an absent period now renders a MARK plus its clipped
   * expansion rather than an empty string — see {@link absentValueMark} for the inconsistency that
   * forced it. One template serves both columns; which one is being drawn is read from the column key.
   */
  @ViewChild('periodCell', { static: true })
  private periodCellTemplate?: TemplateRef<DataTableCellContext<RoleListItem>>;

  /**
   * Frequency cell, shared by the billing and trial frequency columns.
   *
   * A template rather than a bound value because the painted character is now accompanied by a clipped
   * expansion of what it means — see {@link frequencyName}. The character itself is unchanged.
   */
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
   * Whether the chained administration read has already been issued for this visit.
   *
   * Held as a plain field rather than a signal because nothing renders from it: it exists solely so that
   * the FIRST address emission fetches the narrowing selector's groups alongside the listing and every
   * later one fetches the listing alone. A signal would invite a template to depend on it.
   */
  private hasReadAdministration = false;

  /** Whether the inline role-group editor is showing. */
  private readonly editorOpen = signal(false);

  /** The group whose removal is awaiting confirmation, or null when none is. */
  private readonly pendingRemoval = signal<RoleGroup | null>(null);

  /**
   * The role whose removal is awaiting confirmation, or null when none is.
   *
   * Held apart from {@link pendingRemoval} rather than sharing one slot with it. The two subjects are
   * different types with different confirmations and different refusals, and one slot holding either
   * would need a discriminator on every read — which is how a group removal ends up asking about a role.
   */
  private readonly pendingRoleDeletion = signal<RoleListItem | null>(null);

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

    // ⚠ A REFUSED NARROWING RENDERS NOTHING, NOT THE UNNARROWED LISTING. Falling back here was a
    // CRITICAL defect: `/roles?userId=999` is refused with `404` "Portal -1 has no member bearing
    // identifier 999", and the fallback then showed all three roles beneath the subtitle "Roles held
    // by account 999" - a false statement about a non-existent account, presented as data. The rows
    // that answer a DIFFERENT question must not stand in for an answer this one never got; the banner
    // above states the refusal instead.
    //
    // Ordered BEFORE the null test because a refused read leaves the collection null too, so the
    // test below cannot tell the two apart. In flight is not affected: the table's own loading state
    // covers a read that has not settled, and it wins over the empty state.
    if (subject !== undefined && this.heldRolesFailed()) {
      return NO_ROLES;
    }

    if (subject === undefined || held === null || this.store.heldRolesUserId() !== subject) {
      return this.store.roleItems();
    }

    return held;
  });

  /**
   * Whether the narrowed read was refused, as distinct from returning no memberships.
   *
   * An account that genuinely holds no role is a SUCCESSFUL empty answer and still belongs in the
   * narrowed presentation. A refused read has no answer at all, and the difference decides whether
   * the screen may show rows.
   */
  protected readonly heldRolesFailed = computed<boolean>(
    () => this.store.failure()?.operation === 'loadRolesHeldByUser',
  );

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

  /**
   * The twelve columns, assembled in `ngOnInit` once the cell templates exist, with the sort affordance
   * withheld while an account is the subject of the listing.
   *
   * ⚠ THE ORDERING AFFORDANCE BELONGS TO THE BROWSABLE LISTING AND TO NOTHING ELSE. When an account is the
   * subject, the rows come from a DIFFERENT and unpaged slice - the memberships that account holds - which
   * the ordering command cannot reach: it re-reads the browsable listing, so a press would issue a request
   * whose answer this grid never renders and would leave the heading announcing an order the visible rows
   * are not in. An affordance that does nothing is worse than none, so the controls are not painted at all
   * in that mode. The account-narrowed endpoint accepts no ordering parameter, so there is nothing to offer
   * instead.
   *
   * A derived value rather than the stored set read directly, because the mode can change WITHOUT this
   * component being recreated: both addresses match the same route, so navigating from the listing to an
   * account's memberships updates the input in place. A set fixed in `ngOnInit` would keep whichever answer
   * the first address gave.
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
   * The COLUMN KEY the listing is ordered by, or `null` for the server's own ordering.
   *
   * Read from the store rather than held here, so a heading can never announce an order whose request
   * failed: the shared grid derives `aria-sort` from this input alone and never updates its own state.
   *
   * Reported as absent while an account is the subject, for the reason {@link RoleListComponent.columns}
   * records - the visible rows are not the ordered ones, so announcing an order over them would be false.
   */
  protected readonly sortBy = computed<string | null>(() =>
    this.narrowedToAccount() ? null : this.store.rolesPage().sortBy,
  );

  /**
   * The direction {@link RoleListComponent.sortBy} is applied in, or `null` for the server's default.
   *
   * The token is the server's own member name; an abbreviated spelling is refused by the model binder with
   * `400`, which is why the type comes from the paging contract rather than being written out here.
   */
  protected readonly sortDir = computed<SortDirection | null>(() =>
    this.narrowedToAccount() ? null : this.store.rolesPage().sortDir,
  );
  /** The role awaiting removal confirmation; its presence is what opens the dialog. */
  protected readonly pendingRoleRemoval = this.pendingRoleDeletion.asReadonly();

  /**
   * The page the listing is standing on, counted from zero.
   *
   * Read from the COORDINATE rather than from the served metadata, because the pager has to reflect
   * the page that was ASKED for even while the answer for it is still outstanding — binding the served
   * index would make the control jump back to the previous page for the duration of every request.
   */
  protected readonly pageIndex: Signal<number> = computed(() =>
    // ⚠ THE ACCOUNT-NARROWED READ IS UNPAGED, SO ITS COORDINATES ARE DERIVED FROM THE ROWS THEMSELVES.
    // `GET /users/{userId}/roles` answers with a plain array and no metadata, so there IS no served
    // page for it - and reading the BROWSABLE listing's coordinate here made the pager describe a
    // different question than the grid was answering. Runtime testing measured the contradiction:
    // `/roles?userId=2` rendered two rows, announced "2 records.", and printed "1-4 of 4" underneath,
    // because the four was the total of an unrelated group narrowing. Zero is the only honest index for
    // a collection returned in full. This mirrors `sortDir`, which already answers `null` under the
    // same narrowing for the same reason.
    this.narrowedToAccount() ? FIRST_PAGE_INDEX : this.store.rolesPage().pageIndex,
  );

  /**
   * The page size in effect.
   *
   * The SERVED size rather than the requested one, because the server is free to answer with a
   * different window than was asked for and the pager's arithmetic must be done on what actually
   * arrived. A zero — which is what an untouched empty envelope reports — would make the pager divide
   * by nothing, so the requested size stands in until a real answer has landed.
   */
  protected readonly servedPageSize: Signal<number> = computed(() => {
    // The narrowed read is unpaged, so the window IS the answer: every row it returned is on the one
    // page there is. Taken from the same computed the grid renders rather than from a separate slice,
    // so the summary and the rows cannot disagree by construction. A floor of one keeps the pager's
    // arithmetic defined when the account holds no role at all - the summary then reports the zero
    // state, which is what it reports for any empty collection.
    if (this.narrowedToAccount()) {
      return Math.max(this.roles().length, 1);
    }

    const served: number = this.store.rolesMeta().pageSize;

    if (served > 0) {
      return served;
    }

    // The coordinate's own size is NULLABLE — the type expresses "no opinion, let the server choose"
    // as `null`, which is a state the pager has no way to divide by. The store seeds the role
    // coordinate with a real size, so this arm is a floor rather than an expected path, and it falls
    // back to the same constant the store seeds with rather than to a literal chosen here.
    return this.store.rolesPage().pageSize ?? ROLES_PAGE_SIZE;
  });

  /**
   * How many roles the current narrowing matches.
   *
   * The SERVER'S total for the browsable listing, whose read is paged and whose total is therefore the
   * only thing that can describe a match set larger than the page in hand. For the account-narrowed
   * read - which is unpaged - the total is the number of rows returned, counted from the same computed
   * the grid renders. Deriving it is not an approximation here: an array returned in full IS its own
   * total, and the alternative measured on the running application was a pager that reported another
   * narrowing's total beneath this one's rows.
   */
  protected readonly totalCount: Signal<number> = computed(() =>
    this.narrowedToAccount() ? this.roles().length : this.store.rolesMeta().totalCount,
  );

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
   * ⚠ THE ACCOUNT IS NAMED BY ITS DISPLAY NAME WHEN ONE IS KNOWN, and an earlier revision of this
   * member refused to do that on a premise that was simply FALSE. It reasoned that naming the person
   * "would mean importing the account feature's service — which no feature here does". But
   * `UserService` does not live in the account feature at all: it lives in `core/services`, which
   * every feature may use, and the SIBLING component of this very feature already injects it
   * (`role-assignment.component.ts` L164, L1067) to populate its own account picker. So the stated
   * blocker did not exist, and the consequence was a screen that identified a person as
   * "account 2" — the defect reported against it.
   *
   * MIGRATION: the measured screen NAMED the person. `SecurityRoles.ascx.vb` L104-L105 read
   * `UserController.GetUser(PortalId, UserId, False)` whenever it was addressed with an account, and
   * L319 read the same account again to render it. Naming the account is therefore PARITY, and the
   * identifier-only wording was the divergence.
   *
   * The identifier remains the fallback in two states, and both are real rather than defensive:
   * while the name is still being read, and when the account cannot be read at all. The second
   * matters — `/roles?userId=999` names no account, and the subtitle must still say which key was
   * asked for rather than going blank or claiming a name it does not have.
   *
   * The count is included because it is the fact the operator came for, and it is read from the rows
   * actually rendered rather than from the store, so it cannot disagree with the grid.
   */
  protected readonly accountSubtitle = computed<string | undefined>(() => {
    const subject: number | undefined = this.userId();

    if (subject === undefined) {
      return undefined;
    }

    // The name, when the read has landed for THIS subject. Compared against the subject for the same
    // reason the rows are: one slot serves the screen, so during a read of account B it may still
    // hold account A, and naming the wrong person is worse than naming none.
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
   * The address of the account whose memberships are on screen, or `undefined` when none is.
   *
   * ⚠ THIS IS THE RETURN PATH, and its absence is the second half of the reported defect: an
   * operator who pressed a per-account command on the account listing arrived here with no way back
   * to the person they came from. The unnarrowed-listing link goes sideways, not back.
   *
   * MIGRATION: the measured command carried the account listing's own filter state with it
   * (`Users.ascx.vb` L542 appends `UserFilter(False)`), so the legacy round trip returned an operator
   * to the listing they had left. That filter state is not reproduced — this screen is reached by a
   * query parameter carrying the account and nothing else — so the return path offered is the
   * ACCOUNT, which is the more useful of the two destinations and is the one the operator was
   * working on.
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

  /**
   * Accessible name of the row delete command.
   *
   * `SharedResources.resx` `Delete.Text`, which is the wording every other removal in this
   * application shows, so one action reads the same everywhere. The row command composes it with the
   * role's own name, for the reason the edit command does: `Delete` repeated once per row tells a
   * reader moving between commands nothing about which role they are standing on.
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
  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them.
   *
   * ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT. The grid's own fallback is the row
   * OBJECT, which is a correct key only while the same objects stay in play; every read from the server
   * decodes fresh objects, so without this a refetch of the same page presents entirely new keys and the
   * whole body is rebuilt to display records that never changed. `roleId` is unique by definition, being
   * the record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly roleRowKey = (row: RoleListItem): number => row.roleId;

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

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two reads of the same page on every arrival.
    //
    // It also settles the stale-state defect at its root. This store is provided at the application root and
    // therefore OUTLIVES this route, so a previous visit's narrowing and page are both still held when an
    // operator returns. Applying the whole query from the address means a bare `/roles` restores the legacy
    // default and a `/roles?group=all&currentpage=4` restores exactly that view, in both directions, for a
    // reload and for back and forward alike.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: RoleListQuery = parseRoleListQuery(address);

        // An address that says something unusable is CORRECTED rather than obeyed silently, so that what is
        // on screen and what is in the address never disagree. The correction REPLACES the entry rather than
        // adding one - an operator pressing back should reach where they came from, not the uncorrected form
        // of where they already are - and it returns without reading, because the replacement navigation
        // emits again and that emission does the read.
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

        // ⚠ WHICH READ FOLLOWS IS NOT INCIDENTAL. The groups populate the narrowing selector and are
        // unaffected by a page turn, so they are fetched ONCE, on entry, by the chained administration read.
        // Re-fetching them on every later address change would return a page turn to the two requests it was
        // deliberately measured down to one from.
        if (this.hasReadAdministration) {
          this.store.loadRoles();

          return;
        }

        this.hasReadAdministration = true;
        this.store.loadRoleAdministration();
      });
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
      this.readSubjectAccountName(subject);
    });
  });

  /**
   * Reads the subject account's own name, so the subtitle can NAME the person.
   *
   * MIGRATION: this is the read at `SecurityRoles.ascx.vb` L104-L105 —
   * `UserController.GetUser(PortalId, UserId, False)`, made whenever that screen was addressed with
   * an account rather than a role — and it exists for the same purpose, which is to render the person
   * rather than their key.
   *
   * ⚠ IT IS NOT ROUTED THROUGH THE ROLE STORE, and that is deliberate. The store holds ONE failure
   * slot for the whole screen, and this read is the one on the screen whose failure must NOT be
   * surfaced: `/roles?userId=999` legitimately fails to name an account, and the membership read has
   * already reported that in the banner. A second report of the same fact through the same slot would
   * either duplicate the message or overwrite the more informative one. So the outcome is written only
   * to this component's own two signals, and a failure simply leaves the name unknown — which the
   * subtitle already renders as the identifier.
   *
   * The identifier is recorded ALONGSIDE the name so the subtitle can tell whose name it is holding.
   * Without that pairing, navigating from account A to account B would show B's key beside A's name
   * until the second read landed.
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
   * TEN OF THE TWELVE COLUMNS ARE SORTABLE, AS A NET-NEW AFFORDANCE RATHER THAN A PORTED ONE.
   * It was previously declined here on the ground that the legacy grid had no sort affordance,
   * which is true - and a case-insensitive census across BOTH legacy trees finds `AllowSorting`
   * exactly ONCE in either of them, in `Website/admin/Files/filemanager.ascx`, a screen the AAP
   * places out of scope. Not one in-scope legacy grid could be reordered, INCLUDING the module
   * listing which has offered sorting since it was written - so the census says the same thing
   * about every grid in this application and cannot support the affordance on one screen and its
   * absence on the rest.
   *
   * What bounds the set is the endpoint. `SortableFields.Roles` in
   * `backend/src/DnnMigration.Application/Validation/SortableFields.cs` permits every member of
   * the role list contract - `RoleId`, `RoleName`, `Description`, `ServiceFee`,
   * `BillingFrequency`, `BillingPeriod`, `TrialFee`, `TrialFrequency`, `TrialPeriod`, `IsPublic`
   * and `AutoAssignment` - compared case-insensitively, and every column key below IS one of
   * those names. The eleventh, `RoleId`, is not offered because this grid paints no identifier
   * column and a sort control cannot sit on a column that is not shown; the two command columns
   * are not data. So no control here can produce a refused request.
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
   * keyword or a custom-property reference, and rejects pixels outright. An intrinsic width on
   * the two command columns was tried and proved inert under the grid's fixed table layout; the
   * measurement is recorded on those columns below so it is not tried again.
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
      //
      // ⚠ NO WIDTH IS DECLARED ON THESE TWO COMMAND COLUMNS EITHER, AND THAT IS A MEASURED
      // CONCLUSION RATHER THAN AN OMISSION. The "Manage Users" command overpainted the Name
      // column in every row - 30.70px of overlap at a 320-wide viewport, 16.22px still at
      // 1024 - and `width: 'min-content'` was tried here first as the obvious remedy. It does
      // NOTHING: the shared grid is `table-layout: fixed` over a fixed table measure, and
      // under fixed layout an intrinsic keyword on a column is not a definite track size, so
      // the browser went on splitting the measure equally with all twelve columns at 53.33px
      // and the keyword present in the markup. Verified in a real browser, not reasoned about.
      // The fix therefore lives where it can work - the command's own label is allowed to
      // stack its two words, in `role-list.component.scss`, which holds the command inside
      // this column at every width with no width authored anywhere. Do not re-add an
      // intrinsic width here expecting it to help.
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

      // 3. THE REMOVAL COMMAND — an addition, placed LAST of the three so that the destructive one is
      // never the command a pointer lands on first when moving in from the row's leading edge. Headed
      // and aligned exactly like its two siblings, because it must read as the same kind of column;
      // what distinguishes it is its ink and its confirmation, not its geometry.
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
        // The row's NAME. Emitted as `<th scope="row">` so a screen reader announces which record
        // each cell belongs to - without it, traversing a row gives the column name and the value
        // and never the record's identity. This column is the one a person would read aloud to say
        // which row they mean. No visual change: the shared stylesheet restores a body row
        // header's normal weight.
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

      // 7. `asp:boundcolumn DataField="BillingFrequency"`, with the bare item style noted
      // above. The single persisted character is rendered VERBATIM, exactly as the legacy
      // did: the schema seeds a frequency lookup table and the DDL even joins it, but this
      // grid binds the raw field and not the joined description, so `M` renders as `M` and
      // never as `Month`. The code is load-bearing data and is never renamed, case-folded,
      // aliased or turned into a number.
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

    // ⚠ THE ADDRESS IS WRITTEN AND THE STORE IS NOT TOUCHED. The subscription in `ngOnInit` is what
    // applies a narrowing and issues the read, so writing the address here is the whole of the change: the
    // navigation emits, the emission applies, and the operator's browser history records that they narrowed
    // the listing. Calling the store as well would apply the narrowing twice and read twice.
    //
    // The page is cleared alongside it, because a different narrowing yields a different result set in which
    // the page the operator was on has no counterpart. The legacy screen did exactly this - its narrowing
    // handler rebound from the first page (`Roles.ascx.vb:L273-L278`) - and the reset now lives in the
    // address rather than in the store command, so it survives a reload with the narrowing it belongs to.
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
   * Asks for confirmation before removing one role.
   *
   * The pending role is what puts the shared dialog in the document, and the dialog's presence there
   * IS its open state. The same flow as the group removal beside it, deliberately: two removals on one
   * screen that asked differently would be two things for an operator to learn.
   *
   * @param role The row whose command was pressed.
   */
  protected requestRoleRemoval(role: RoleListItem): void {
    this.store.clearError();
    this.pendingRoleDeletion.set(role);
  }

  /**
   * Removes the confirmed role.
   *
   * ⚠ NOT GATED ON WHETHER THE ROLE LOOKS REMOVABLE, and that is the same rule the group removal
   * follows. The two roles a tenant cannot lose — its administrators and its registered users — are
   * refused by the server with `403` and that refusal is reported; deciding here would mean this screen
   * holding a second opinion about a rule the server owns, and the facts it would need (the tenant's
   * two protected role keys) are not on the listing contract at all.
   *
   * ⚠ THIS IS THE ONE CALLER THAT ASKS FOR THE LISTING TO BE RE-READ, and it must. Every other screen
   * that deletes a role departs for this listing, which reads itself from its own address on arrival;
   * a row-level delete here changes no address, so without this read the removed row would simply stay
   * on screen. The read also passes through the store's page dispatcher, which steps back a page when
   * the removal emptied the one being viewed — exactly the state a row-level delete can create and the
   * unpaged listing never could.
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
   * Re-orders the listing on the heading that was activated.
   *
   * The ordering goes into the ADDRESS and nothing else, exactly as the page and the narrowing do: the
   * subscription that watches the query parameters is the single thing that stages state and issues the
   * read, so writing the address is the whole of the change here. Calling the store as well would stage the
   * ordering twice and read twice.
   *
   * THE PAGE IS CLEARED ALONGSIDE IT, for the same reason a change of narrowing clears it: which page a
   * given role falls on depends on the ordering, so holding the index would land an operator on a page of
   * rows they have already seen. The reset lives in the address rather than only in a store command, so it
   * survives a reload together with the ordering it belongs to.
   *
   * @param change The heading that was activated and the direction to apply. The shared grid decides the
   * direction - it toggles on the active column and starts ascending on any other - so this screen neither
   * remembers nor recomputes it.
   */
  protected onSortChange(change: DataTableSortChange): void {
    // ⚠ REFUSED WHILE THE LISTING IS NARROWED TO ONE ACCOUNT, for the reason {@link columns} states: the
    // rows shown then come from a different slice that this command cannot reach, so acting would order a
    // listing nobody is looking at. The controls are withheld in that mode, so this is unreachable through
    // the grid and exists so the rule holds however the output is reached.
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
   * Moves the listing to another page.
   *
   * The index is forwarded to the store exactly as the pager reported it. The pager only ever emits an
   * index inside the range it was told about, and the store owns what happens when a page stops
   * existing underneath it, so there is no arithmetic and no clamping here.
   *
   * @param pageIndex The page to move to, counted from zero.
   */
  protected onPageChange(pageIndex: number): void {
    // ⚠ A PAGE TURN MEANS NOTHING UNDER THE ACCOUNT NARROWING, so it is refused rather than dispatched.
    // That read is unpaged and its coordinates are derived from the rows in hand, so the pager reports a
    // single full page and draws no page steps - this arm is a floor rather than an expected path. It
    // exists because navigating would put a `page` parameter in the address that the narrowed read
    // cannot honour, leaving an address that describes a page nobody can be on.
    if (this.narrowedToAccount()) {
      return;
    }

    // A page turn is a PUSHED history entry, not a replaced one: runtime testing found that pressing back
    // from page three was not possible because paging created no entry at all, and returning to the page you
    // came from is the ordinary meaning of that button.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [PAGE_PARAM]: firstPageParameter(pageIndex) },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * The mark that stands in for a period or a fee the tenant never recorded.
   *
   * ⚠ R-M1: AN EMPTY CELL CANNOT DISTINGUISH "NOT RECORDED" FROM "NOT YET LOOKED AT", AND THIS GRID HAD
   * A SPECIFIC INCONSISTENCY TO ANSWER FOR. `formatPeriod` filters the legacy absent-integer marker,
   * whose value is minus one, and renders every other integer verbatim — so a stored `-1` came out as an
   * empty cell while a stored `-3` came out as "-3", two negative periods presented as different KINDS
   * of value. Both behaviours are faithful on their own (`Roles.ascx.vb:L152-L162` guards on
   * `period <> Null.NullInteger` and nothing else), and the inconsistency is the legacy's; what was
   * missing was any way to tell which had happened.
   *
   * AAP Rule T7 puts sentinels at the boundary rather than in the model, which is exactly what this is:
   * the value stays absent, and the CELL says so. The same mark and the same words the portal listing
   * uses, so one absence reads identically across the application.
   */
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
   * Whether a fee column holds nothing to render for this row.
   *
   * ⚠ FOUND BY RUNTIME MEASUREMENT, NOT BY READING. The absent mark reached the two PERIOD columns
   * first, and a browser pass over the whole 145-role dataset then found the asymmetry it had left
   * behind: on the one role whose fees are stored NULL, the two money cells rendered as completely
   * empty strings while the two count cells beside them on the SAME ROW rendered the mark plus its
   * clipped words. A reader heard "not recorded" for the counts and silence for the fees, for one
   * indistinguishable state. Whatever the argument for marking an absence, it cannot apply to one
   * pair of columns and not the other.
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
   * What a stored frequency character MEANS, for the accessibility tree only.
   *
   * ⚠ R-M3: THE PAINTED CHARACTER DOES NOT CHANGE, AND THAT IS THE WHOLE DESIGN. The legacy grid bound
   * the raw field rather than the joined description (`roles.ascx:L50-L52`), so `M` rendered as `M` — and
   * it still does, because the character is load-bearing data and the schema even seeds a lookup table
   * the grid declined to join. What was wrong was not the character but that NOTHING said what it meant,
   * while the role editor two clicks away rendered the same datum as "Month". A reader met an
   * unexplained letter with no route to its meaning.
   *
   * The expansion is therefore added beside it, clipped from sight, taking its words from the one shared
   * vocabulary the editor's own select captions come from. Zero visual change, and the announcement
   * becomes "Billing Period Month" instead of "Billing Period M".
   *
   * An unrecognised character yields the empty string rather than a guess: the column is constrained by
   * a foreign key, so anything outside the vocabulary is data this application cannot interpret and must
   * not narrate.
   *
   * @param role The row.
   * @param key Which of the two frequency columns is being drawn.
   * @returns The word behind the character, or the empty string when there is none to give.
   */
  protected frequencyName(role: RoleListItem, key: string): string {
    return BILLING_FREQUENCY_NAMES[this.frequencyCode(role, key)] ?? '';
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

  /**
   * True when a fee is too large for this cell to state exactly — R-M4.
   *
   * ⚠ THE CELL IS PAINTING A NUMBER THAT IS NOT THE STORED NUMBER, AND NOTHING SAID SO. The column
   * behind these two cells is SQL `money`, which carries four fractional digits of exact decimal at
   * the full width of a 64-bit integer; the wire carries a JSON number, which every browser reads as
   * an IEEE-754 double. Above a certain magnitude a double cannot hold a value to the nearest
   * hundredth at all, so the number that arrives is already a nearby one. Runtime testing measured
   * the consequence on the largest amount the column can hold: `922337203685477.5807` was stored,
   * and `922337203685477.63` was painted — a different figure, in the same colour and the same
   * weight as an exact one, on the screen an administrator uses to review what a role costs.
   *
   * ⚠ THE ROUNDING CANNOT BE UNDONE HERE, WHICH IS WHY THIS DISCLOSES RATHER THAN CORRECTS. The loss
   * happens when the wire's decimal literal is read into a double, before any code in this component
   * runs; the stored digits are gone by then and no amount of formatting recovers them. Sending the
   * amount as a string would recover them and is a CONTRACT change to every consumer of the role
   * listing, which is beyond this finding. So the cell paints what it has and says that it is
   * approximate — the one thing it can do that is not misleading.
   *
   * THE THRESHOLD IS COMPUTED, NOT CHOSEN. `Number.MAX_SAFE_INTEGER` is the largest integer a double
   * represents exactly; divided by a hundred it is the largest amount whose every hundredth is
   * exactly representable. Below it the painted figure is the arrived figure to the cent and no
   * qualifier appears; at or above it the figure may already have moved. That bound is the same one
   * the role editor's own storability rule uses to refuse an amount it cannot carry, so the two
   * screens agree about which amounts are exact.
   *
   * An ABSENT fee never reaches this test — the absent mark is decided first — and neither does a
   * zero or any ordinary amount, so the qualifier appears on the rows that need it and nowhere else.
   * Runtime measurement: exactly one role in a hundred and forty-five.
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

    // ⚠ THE SUPPORT REFERENCE TRAVELS WITH IT. The summary has carried a `supportReference` member all
    // along, and dropping it here threw away the only join key between what an operator saw in the
    // browser and the request as the server recorded it - the correlation identifier the server
    // validated, which is what appears on the response header, on the request envelope in its log and on
    // every audit event the request produced. A browser audit measured the asymmetry: a refusal presented
    // through the shared banner read `Reference: <id>`, while the same class of refusal presented as a
    // notification read nothing an operator could quote. The notification surface appends it AFTER its own
    // message bound, so a long server sentence cannot truncate the identifier away, and a document that
    // carried none resolves to null and is simply not quoted.
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
   * @returns The confirmation to announce. Never empty.
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
