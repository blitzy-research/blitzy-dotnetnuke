/**
 * The User Accounts listing screen.
 *
 * Replaces the DotNetNuke Web Forms control pair `Website/admin/Users/users.ascx` and
 * `Website/admin/Users/Users.ascx.vb`. Reached at `/users`, the empty child path of
 * `USER_ROUTES`.
 *
 * ---------------------------------------------------------------------------
 * THE CONTRACT WITH `user-list.component.html`
 * ---------------------------------------------------------------------------
 *
 * Seven `ng-template` declarations must exist at the TOP LEVEL of the template file,
 * outside every control-flow block, because the shared table renders rich cells and row
 * commands through an outlet fed by a `TemplateRef` this class captures:
 *
 * | Reference           | Renders                                                      |
 * |---------------------|--------------------------------------------------------------|
 * | `#editCommand`      | the row edit command, `routerLink` to `/users/{userId}`       |
 * | `#deleteCommand`    | the row delete command, calling {@link UserListComponent.requestDeletion} |
 * | `#manageRolesCommand` | the row roles command, `routerLink` to `/roles`             |
 * | `#emailCell`        | the electronic-mail cell, a `mailto:` anchor bound with `[href]` |
 * | `#createdDateCell`  | `{{ row.createdDate | dateDisplay: 'datetime' }}`            |
 * | `#lastLoginCell`    | `{{ row.lastLoginDate | dateDisplay: 'datetime' }}`         |
 * | `#approvedCell`     | `{{ row.isApproved | yesNo }}`                               |
 *
 * A missing declaration raises an error naming the reference rather than rendering a
 * silently wrong grid — see {@link UserListComponent.requireTemplate}.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS SCREEN DOES NOT DO
 * ---------------------------------------------------------------------------
 *
 * No request is issued from this file. Every fact shown and every request made belongs to
 * `core/state/user.store.ts`, which is injected and commanded; the transport, the endpoint
 * table, the query-parameter builder and the HTTP client are all unreachable from here by
 * design, because Minimal Change Clause item 5 confines data access to the store and its
 * service. Nothing is cached locally either: the legacy `DataCache` wrapper
 * (`Library/Components/Providers/Caching/DataCache.vb`, reached from 116 in-scope call
 * sites) is deliberately not reproduced anywhere in the client, so there is no cache map,
 * no expiry stamp and no staleness flag below.
 *
 * MIGRATION: view state disappears entirely. The legacy page index descended from
 * `ViewState("PageNo")` (`ManageUsers.ascx.vb` L174-L185, seeded at nought) and every
 * post-back re-bound the grid from scratch. Page, search and ordering are now signals the
 * store owns, and `Session(` appears nowhere in the legacy tree to begin with.
 *
 * MIGRATION: localisation is not ported. The framework's localisation package is outside
 * this workspace's closed dependency set, so none of the 48 in-scope localisation calls is
 * reproduced, and neither the tagged-template localisation helper nor a translation
 * attribute appears anywhere here. The twelve Users resource files were read for WORDING
 * ONLY, and every user-visible string below is a module-level constant carrying the
 * resource key it was taken from.
 *
 * MIGRATION: resource text is untrusted markup and is rendered as PLAIN TEXT ONLY. Across
 * the 37 in-scope resource files 76 values carry a raw HTML tag and one carries a live
 * script element; this directory's own `ModuleHelp.Text` is itself HTML. Nothing here is
 * ever bound as raw markup, no sanitiser bypass is used, and no help text is copied out of
 * a resource value — where the screen needs prose it is authored as real template markup.
 */

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
  type OnInit,
  type Signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import {
  addressStatesQuery,
  FILTER_PARAM,
  firstPageParameter,
  PAGE_PARAM,
  parsePageIndex,
  parseSortDirection,
  parseSortKey,
  SORT_BY_PARAM,
  SORT_DIR_PARAM,
} from '../../../core/utils/list-query.util';

import type { ParamMap, Params } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { UserStore } from '../../../core/state/user.store';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
import { DateDisplayPipe } from '../../../shared/pipes/date-display.pipe';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';

import type { SortDirection } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { MembershipSettings, UserListItem, UserSortField } from '../../../core/models/user.model';
import type { UserFailure, UserMutation, UserSearch } from '../../../core/state/user.store';
import type {
  DataTableCellContext,
  DataTableColumn,
  DataTableSortChange,
} from '../../../shared/components/data-table/data-table.component';

// ---------------------------------------------------------------------------
// WORDING
// ---------------------------------------------------------------------------
//
// Every string below was read out of a legacy resource file and is recorded with the key
// it came from, so a reader can trace the wording back to what the operator used to see.
//
// ⚠ THE RESOURCE VALUE IS THE AUTHORITY, NEVER THE MARKUP ATTRIBUTE. `Users.ascx.vb` L585
// ran `Localization.LocalizeDataGrid`, which rewrote every heading from
// `GetString(HeaderText & ".Header", ResourceFile)` at run time, so five of the markup
// `headertext` values in `users.ascx` are contradicted by the resource file and the
// resource file wins. `FirstName` renders as "First Name", `LastName` as "Last Name",
// `DisplayName` as "Name", `CreatedDate` as "Created Date" and `LastLogin` as
// "Last Login". Taking the markup attribute would have produced five wrong headings that
// no compiler could have caught.

/** `Users.ascx.resx` `ControlTitle_.Text`. */
const PAGE_TITLE = 'User Accounts';

/** `Users.ascx.resx` `AddContent.Action`. */
const ADD_USER_LABEL = 'Add New User';

/** `Users.ascx.resx` `UserSettings.Action`. */
const MEMBERSHIP_SETTINGS_LABEL = 'User Settings';

/** `Users.ascx.resx` `ManageProfile.Action`. */
const PROFILE_DEFINITIONS_LABEL = 'Manage Profile Properties';

/**
 * Disclosed when the tenant's account policy could not be read, so this listing is running on its
 * documented fallbacks — #5.
 *
 * AUTHORED, and reported as a net addition. There is no legacy wording to recover because the
 * legacy screen could not reach this state: `Website/admin/Users/Users.ascx.vb` read every setting
 * through `UserModuleBase.GetSetting(PortalId, key)`, which answered the hard-coded default for an
 * unreadable or absent key WITHOUT reporting that it had done so, and `Page_Init` L508-L517 then
 * applied that answer to the grid. The legacy screen was therefore structurally incapable of
 * saying this, and its silence is the reason a fallback could be mistaken for a preference.
 *
 * It names the two things the policy actually decides on this screen and nothing else, because
 * over-claiming here would repeat in a quieter voice the very error being fixed: the accounts
 * themselves are unaffected and the sentence must not imply otherwise.
 */
const POLICY_DEGRADED_NOTICE =
  'This site\u2019s user settings could not be read, so this list is shown at the default page size with the default columns. The accounts themselves are unaffected.';

/** `Users.ascx.resx` `Search.Text`, from `users.ascx` L5 `lblSearch resourcekey="Search"`. */
const SEARCH_LABEL = 'Search:';

/**
 * Label for the search-type selector.
 *
 * MIGRATION: `users.ascx` L8 declared `ddlSearchType` with NO associated label of any kind,
 * so the legacy control reached assistive technology unnamed. The resource file supplies no
 * key for it either. This wording is therefore authored rather than ported, and it is an
 * accessibility addition with no visual counterpart in the legacy screen: the selector is
 * wrapped in the shared form field so that the name is programmatically associated.
 */
const SEARCH_FIELD_LABEL = 'Search by';

/**
 * `SharedResources.resx` `Edit.Text`.
 *
 * The local resource file carries NO `Edit` key, so the legacy three-level lookup at
 * `Users.ascx.vb` L550 — `Localization.GetString(imageColumn.CommandName, LocalResourceFile)`
 * — fell through to the shared global resources, where the value is "Edit".
 */
const EDIT_COMMAND_LABEL = 'Edit';

/** `Users.ascx.resx` `Delete.Text`. Present locally, so the shared value is never reached. */
const DELETE_COMMAND_LABEL = 'Delete';

/**
 * `Users.ascx.resx` `UserRoles.Text`.
 *
 * The label is keyed by the legacy COMMAND NAME rather than by anything visible:
 * `Users.ascx` L34 declares `CommandName="UserRoles"` and L550 uses that name as the
 * resource key, which resolves to "Manage Roles".
 */
const MANAGE_ROLES_COMMAND_LABEL = 'Manage Roles';

/** `SharedResources.resx` `DeleteItem.Text`, reached from `Users.ascx.vb` L523. */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `Users.ascx.resx` `UserDeleted.Text`, reported at `Users.ascx.vb` L655 on success. */
const USER_DELETED_MESSAGE = 'User Deleted Successfully';

/**
 * `SharedResources.resx` `UserDeleteError.Text`, reported at `Users.ascx.vb` L657 on failure.
 *
 * The local resource file carries no `UserDeleteError` key, so this is another three-level
 * fall-through to the shared global resources.
 */
const USER_DELETE_ERROR_MESSAGE = 'Error Deleting User';

/** `SharedResources.resx` `All.Text`, the unfiltered affordance appended at `Users.ascx.vb` L308. */
const ALL_FILTER_LABEL = 'All';

/**
 * The notice shown while the tenant's opening-view policy has issued no query.
 *
 * AUTHORED, and there is no legacy wording to recover because the legacy screen showed NONE:
 * `Users.ascx.vb` L266 excluded the bare marker `"None"` from every branch of `BindData`, so
 * `grdUsers.DataSource` was assigned `Nothing` and the grid rendered unbound and silent. An
 * operator arriving on a tenant configured that way — which is every tenant that has configured
 * nothing, since `UserModuleBase.vb` L126-L130 defaulted the setting to that mode — was shown an
 * empty grid and left to work out that the accounts were merely unrequested.
 *
 * ⚠ THE ALTERNATIVE IS NOT SILENCE, IT IS A FALSEHOOD. Without this notice the shared grid renders
 * its own empty state, whose wording says nothing was found — and nothing was looked for. It names
 * both ways forward using the wording those affordances actually carry, so the sentence and the
 * controls agree.
 */
const NO_QUERY_NOTICE =
  'No accounts have been requested yet. Choose a letter, or select All, to list this site’s accounts.';

/**
 * `Users.ascx.resx` `Filter.Text`, verbatim.
 *
 * A pure 26-letter list: no "All" entry, no "0-9" entry and no punctuation beyond the
 * separators. `Users.ascx.vb` L306-L310 read this value and then APPENDED the unfiltered,
 * signed-in and unauthorised affordances to it before splitting, which is why the letters
 * and the "All" entry are assembled separately below.
 */
const LETTER_FILTER_LIST = 'A,B,C,D,E,F,G,H,I,J,K,L,M,N,O,P,Q,R,S,T,U,V,W,X,Y,Z';

/** The separator `Users.ascx.vb` L312 split {@link LETTER_FILTER_LIST} on. */
const LETTER_FILTER_SEPARATOR = ',';

/** `Users.ascx.resx` `Username.Header`. */
const USERNAME_HEADING = 'Username';

/** `Users.ascx.resx` `FirstName.Header` — NOT the markup's "FirstName". */
const FIRST_NAME_HEADING = 'First Name';

/** `Users.ascx.resx` `LastName.Header` — NOT the markup's "LastName". */
const LAST_NAME_HEADING = 'Last Name';

/** `Users.ascx.resx` `DisplayName.Header` — NOT the markup's "DisplayName". */
const DISPLAY_NAME_HEADING = 'Name';

/** `Users.ascx.resx` `Address.Header`. */
const ADDRESS_HEADING = 'Address';

/** `Users.ascx.resx` `Telephone.Header`. */
const TELEPHONE_HEADING = 'Telephone';

/** `Users.ascx.resx` `Email.Header`. */
const EMAIL_HEADING = 'Email';

/** `Users.ascx.resx` `CreatedDate.Header` — NOT the markup's "CreatedDate". */
const CREATED_DATE_HEADING = 'Created Date';

/** `Users.ascx.resx` `LastLogin.Header` — NOT the markup's "LastLogin". */
const LAST_LOGIN_HEADING = 'Last Login';

/** `Users.ascx.resx` `Authorized.Header`. */
const AUTHORIZED_HEADING = 'Authorized';

/**
 * Placeholder for the free-text search control. Authored: the legacy textbox had none.
 *
 * ⚠ U-M13 — IT STATES THE PREDICATE, AND THE PREVIOUS WORDING PROMISED THE WRONG ONE. It read
 * "Search accounts", which an operator reasonably takes to mean a search of the account — anywhere
 * in it. The match is a STARTS-WITH: `Website/admin/Users/Users.ascx.vb` L269, L271 and L274 each
 * append a single TRAILING wildcard and nothing leading, and the target endpoint reproduces that
 * appending exactly. So typing `smith` finds `smithers` and does not find `johnsmith`, and an
 * operator working from the old wording would read that as missing data rather than as the rule.
 *
 * ⚠ IT NAMES NO FIELD, DELIBERATELY, and that is not vagueness. The axis is chosen in the selector
 * beside this box and is an OPEN SET — account name, electronic-mail address, or any profile
 * property the tenant has declared — so the field is what the selector says and the predicate is
 * what this says. Naming a field here would contradict the selector for two of its three kinds of
 * entry.
 */
const SEARCH_PLACEHOLDER = 'Begins with';

/**
 * The filter-in-force disclosure — U-M12 and the other half of #7.
 *
 * ⚠ WHY THIS EXISTS AT ALL. The alphabet strip announces the entry in force through `aria-pressed`,
 * and it is TRUTHFUL for it to announce none while a free-text term is filtering the listing —
 * none of the twenty-seven entries is what is in force. But truthful silence still leaves an
 * operator unable to see that a filter is applied: the strip shows nothing pressed, exactly as it
 * does on an unfiltered listing, and the two states look identical.
 *
 * ⚠ IT ALSO CLOSES THE COMPOUND HALF OF #7, WHICH NOTHING ELSE COULD. Changing the search axis
 * re-queries nothing — `users.ascx` L8 declared `ddlSearchType` with no auto-post-back, and
 * `Users.ascx.vb` L586 read the selection at QUERY time — so after switching from account name to
 * electronic-mail address, the listing on screen is still filtered by the OLD axis until a search
 * is run. That is parity and is kept. What was missing is any way to see it: this sentence names
 * the axis the LISTING is filtered on, so an operator comparing it against the selector can see at
 * a glance that their new selection has not been applied yet.
 *
 * AUTHORED. The legacy screen rendered no such statement, because its filter travelled in the
 * query string and was therefore visible in the address bar of every filtered listing
 * (`FilterURL`, L446-L456). The target keeps the filter in a store, so the address no longer says
 * it and the screen must.
 *
 * `{field}` is substituted with the axis in words and `{text}` with the term exactly as it is being
 * matched. See {@link UserListComponent.filterDisclosure}.
 */
const FILTER_DISCLOSURE_TEMPLATE = 'Filtered: {field} begins with \u201c{text}\u201d.';

/** The axis wording for {@link FILTER_DISCLOSURE_TEMPLATE} when the search is on the account name. */
const USERNAME_AXIS_WORDING = 'user name';

/** The axis wording when the search is on the electronic-mail address. */
const EMAIL_AXIS_WORDING = 'email';

/**
 * The mark painted where a profile value the tenant has chosen to show is not recorded — U-M2.
 *
 * ⚠ MEASURED BEFORE IT WAS DESIGNED, AND THE MEASUREMENT CHANGED THE ANSWER. The postal address and
 * the telephone number are two of the columns this tenant's policy SHOWS, and both render nothing on
 * every row: measured across all two hundred and fifty-three accounts, `address` and `telephone` are
 * null on every one — on the detail endpoint as well as on the listing, so nothing is being dropped
 * in projection and there is no server-side value to recover. They are simply not recorded. An empty
 * cell is therefore TRUTHFUL, and the defect is that it is indistinguishable from a cell that failed
 * to render: two hundred and thirty-eight units of a grid saying nothing at all, with no way to tell
 * "nobody recorded this" from "this is broken".
 *
 * ⚠ NOT AN INVENTED PLACEHOLDER, AND SPECIFICALLY NOT A WORD. An em dash paints the absence without
 * ever being mistaken for a value — no address and no telephone number can be spelled this way,
 * whereas "None", "N/A" or "Unknown" are all things a person could have typed into a free-text
 * profile field. The same mark and the same reasoning already carry the absent host name on the
 * portal alias listing, so a reader meets one convention for absence across the console rather than
 * two.
 */
const ABSENT_PROFILE_VALUE_MARK = '\u2014';

/**
 * What the mark above MEANS, for a reader who cannot see it.
 *
 * The mark itself is hidden from assistive technology and this sentence is exposed in its place,
 * because an em dash announces as punctuation or as nothing at all depending on the reader's
 * verbosity setting — so on its own it would restore exactly the silence it is there to break.
 */
const ABSENT_PROFILE_VALUE_DESCRIPTION = 'not recorded';

/**
 * The qualifier shown beside the approval word when an account is LOCKED OUT — U-M1.
 *
 * ⚠ THE APPROVAL WORD ALONE IS MISLEADING FOR THESE ACCOUNTS, WHICH IS WHY THIS EXISTS. The column
 * is the legacy `Authorized` column and it reports approval, which is genuinely a different fact
 * from lock-out — but an operator reads the column to answer one question, "can this account be
 * used", and for a locked-out account the answer is no while the cell says `Yes`. Measured on this
 * tenant: eleven of two hundred and fifty-three accounts are locked out and every one of them
 * rendered an unqualified `Yes`, so the only accounts an operator could actually find were the
 * thirty-four unapproved ones.
 *
 * MIGRATION: a net addition to the LISTING, and reported as one. Lock-out was legacy-visible only on
 * the per-account panel — `Website/admin/Users/Membership.ascx` is a read-only panel hosted beside
 * the account editor at `manageusers.ascx` L59-L63, and it carried the unlock command — so the
 * capability is legacy and only its DISCOVERABILITY is new. The approval word itself is unchanged
 * and no column is added: the fact is attached to the cell whose meaning it qualifies, which keeps
 * the tenant's `Column_Authorized` gate governing both.
 */
const LOCKED_OUT_QUALIFIER = 'Locked';

/**
 * What the qualifier above means, spelled out for a reader who meets it without the column heading.
 *
 * Exposed alongside the visible word rather than replacing it: the short word is what fits a grid
 * cell, and the sentence is what makes it unambiguous.
 */
const LOCKED_OUT_DESCRIPTION = 'locked out, cannot sign in';

/**
 * Accessible name for the alphabet strip's navigation landmark.
 *
 * AUTHORED, and invisible. `users.ascx` L14 wrapped the strip in a centred panel with no name
 * of any kind, so a screen reader met twenty-seven unexplained links. Naming the landmark
 * costs nothing visually and the resource file supplies no key for it, so the wording is
 * written here rather than ported.
 */
const FILTER_STRIP_LABEL = 'Filter accounts by first letter';

// ---------------------------------------------------------------------------
// NAVIGATION TARGETS
// ---------------------------------------------------------------------------
//
// Plain path strings, declared once. Each is bound through `routerLink`, so the router
// owns URL construction and nothing here concatenates a query string — which is what the
// legacy screen did at `Users.ascx.vb` L164-L188, assembling `filter`, `filterproperty`
// and `currentpage` by hand into a string it then substituted a placeholder into.

/** The listing's own segment, and the prefix of every account editor route. */
const USERS_PATH = '/users';

/** `Users.ascx.vb` L725 `ModuleActionType.AddContent`, whose editor target is the create form. */
const ADD_USER_LINK = '/users/new';

/**
 * `Users.ascx.vb` L732 `UserSettings.Action`.
 *
 * A TOP-LEVEL route rather than a child of `users`, because it configures the tenant and not
 * one account; `app.routes.ts` loads `settings/membership` directly.
 */
const MEMBERSHIP_SETTINGS_LINK = '/settings/membership';

/**
 * `Users.ascx.vb` L730 `ManageProfile.Action`.
 *
 * Also a top-level route, loaded directly by `app.routes.ts` as
 * `settings/profile-definitions`.
 */
const PROFILE_DEFINITIONS_LINK = '/settings/profile-definitions';

/**
 * Target of the third row command.
 *
 * MIGRATION: A REDUCTION, AND A DELIBERATE ONE. `Users.ascx.vb` L542 navigated to
 * `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD", …)`, a PER-ACCOUNT role screen. No
 * such route exists in the target's closed route set — the only membership route is
 * `/roles/:roleId/users`, which is keyed by ROLE and belongs to the role feature. A feature
 * may not import another feature, so this command navigates to the role listing by URL
 * string and the operator reaches a specific account's memberships from there. Recorded as a
 * documented functional reduction.
 */
const MANAGE_ROLES_LINK = '/roles';

// ---------------------------------------------------------------------------
// THE SEARCH AXIS
// ---------------------------------------------------------------------------

/**
 * The two search fields the legacy `Select Case` matched by name.
 *
 * `Users.ascx.vb` L267-L276 switched on the selector's value with `Case "Email"`,
 * `Case "Username"` and `Case Else`, and `Page_Load` L577-L578 added exactly these two
 * entries before any tenant-declared profile property. The spellings are therefore the
 * legacy ones and are matched by identity, never case-folded.
 *
 * MIGRATION: a tenant that declares a profile property literally named `Username` or `Email`
 * produces a duplicate entry whose value collides with one of these two, and the account
 * field wins because the legacy switch tested it first. That quirk is PRESERVED rather than
 * corrected: the ordering below is the legacy ordering, and silently renaming or
 * de-duplicating a tenant's own declaration would be a behavioural change of exactly the
 * kind Minimal Change Clause item 1 forbids.
 */
const USERNAME_SEARCH_FIELD = 'Username';

/** The second of the two account fields the legacy switch matched by name. */
const EMAIL_SEARCH_FIELD = 'Email';

// ---------------------------------------------------------------------------
//  THE ADDRESS
//
//  This listing keeps its search, its search AXIS and its page in the address, so a reload, a bookmark and
//  the browser's own back and forward buttons all reproduce what is on screen. Runtime testing on the
//  sibling portal listing measured what the absence of that cost: pager clicks advanced the grid while the
//  address stayed on the bare route, pressing back from page three was not possible because paging created no
//  history entry at all, and a fresh arrival landed on a page and a filter the operator could not see,
//  because this store is provided at the application root and OUTLIVES this route.
//
//  ⚠ THIS LISTING HAS A THIRD STATE THE OTHER THREE DO NOT, and the address has to express it. An empty
//  address does NOT mean "show everything": it means NOTHING HAS BEEN ASKED FOR, which is a real legacy state
//  - `Users.ascx.vb` L266 compared against a magic string that fell through every branch and left the grid
//  unbound - and it is distinct from the unfiltered listing, which really does ask the server for every
//  account. Absence therefore leaves the tenant's policy to choose the opening view exactly as it does on a
//  first ever visit, and the unfiltered listing is stated explicitly as `?searchby=all`.

/**
 * Address parameter carrying the axis a search applies to.
 *
 * The axis is an OPEN SET: the two account fields below plus any profile property the tenant declares, which
 * is why this parameter carries the field NAME rather than an index into a closed list. It also carries the
 * reserved {@link ALL_ACCOUNTS_TOKEN}, which names no axis at all.
 */
const SEARCH_BY_PARAM = 'searchby';

/**
 * The {@link SEARCH_BY_PARAM} value standing for every account in the tenant, unfiltered.
 *
 * Reserved, and therefore unusable as a profile property name. That collision is accepted: it would require a
 * tenant to declare a property called exactly `all`, and the alternative - a second boolean parameter beside
 * the axis - would make two parameters able to contradict each other.
 */
const ALL_ACCOUNTS_TOKEN = 'all';

/** Identifier for the search-type selector, so the shared form field can name it. */
const SEARCH_FIELD_CONTROL_ID = 'user-list-search-field';

/** The character `HtmlUtils.FormatEmail` tested for at `HtmlUtils.vb` L94. */
const MAILBOX_SEPARATOR = '@';

/**
 * The search, axis and page this listing is showing, as the address states them.
 *
 * Held as one object because they are restored TOGETHER on entry: every search command returns the listing to
 * the first page, so applying a search and a page separately would discard the page the address asked for.
 */
/**
 * The grid columns this listing offers an ordering on, each mapped to the field the endpoint binds.
 *
 * ⚠ A MAP RATHER THAN A LIST, because the two vocabularies genuinely differ: the grid keys its columns in
 * camel case, while {@link UserSortField} is the endpoint's own Pascal-cased member set. Sending a column key
 * would work today - the model binder is case-insensitive, verified - but the STORE is typed to
 * `UserSortField`, so the translation has to exist somewhere and a declared map is the one place it can be
 * read off rather than inferred.
 *
 * ⚠ EVERY ENTRY WAS VERIFIED AGAINST THE RUNNING ENDPOINT, and so was every exclusion, because an affordance
 * that produces a refused request is worse than no affordance. `GET /api/v1/users` accepts exactly
 * DisplayName, Email, FirstName, IsSuperUser, LastName, UserId and Username, and answers anything else with a
 * field-level `400`. The five below were each issued and observed to return `200` with a genuinely different
 * order; `address`, `telephone`, `createdDate`, `lastLoginDate` and `approved` were each issued and observed
 * to be REFUSED, which is why those five headings stay inert. `UserId` and `IsSuperUser` are accepted by the
 * endpoint but have no heading here to attach an ordering to - this grid renders neither.
 *
 * MIGRATION: sorting is a NET ADDITION; the legacy grid declared no `AllowSorting`. It is offered because AAP
 * 0.3.2 specifies `sortBy`, `sortDir` and `sortChange` on the shared record grid and the review recorded
 * their absence here as an AAP compliance failure rather than a design choice.
 */
const SORTABLE_COLUMNS: Readonly<Record<string, UserSortField>> = Object.freeze({
  userName: 'Username',
  firstName: 'FirstName',
  lastName: 'LastName',
  displayName: 'DisplayName',
  email: 'Email',
});

/** The column keys of {@link SORTABLE_COLUMNS}, derived rather than restated so the two cannot drift. */
const SORTABLE_COLUMN_KEYS: readonly string[] = Object.freeze(Object.keys(SORTABLE_COLUMNS));

/**
 * The grid column key an endpoint field came from, or `null`.
 *
 * Derived by reversing {@link SORTABLE_COLUMNS} at each call rather than by keeping a second frozen table,
 * because two tables can disagree and one cannot. The map has five entries, so the scan is free.
 *
 * @param field The endpoint field the store is holding, or undefined when it holds none.
 * @returns The column key to mark active in the grid, or `null` when no column corresponds.
 */
function columnKeyForSortField(field: UserSortField | undefined): string | null {
  if (field === undefined) {
    return null;
  }

  const match = Object.entries(SORTABLE_COLUMNS).find(([, bound]) => bound === field);

  return match === undefined ? null : match[0];
}

interface UserListAddressQuery {
  /** The search to apply. Mode `none` means the address asked for nothing. */
  readonly search: UserSearch;

  /**
   * The axis the search-type selector should show.
   *
   * Carried separately from {@link search} because the selector holds a value even in modes that have no
   * axis of their own - the unfiltered listing and the nothing-asked-for state - and an operator returning
   * to a bookmarked unfiltered listing should still find the selector where they left it.
   */
  readonly axis: string;

  /** The page to read, counted from nought. */
  readonly pageIndex: number;

  /**
   * The grid column to order by, or `null` to accept the endpoint's own default ordering.
   *
   * A COLUMN KEY, not an endpoint field: the address speaks the grid's vocabulary so that what is in the
   * address matches what the heading is called. {@link SORTABLE_COLUMNS} performs the translation at the one
   * point the store is spoken to.
   */
  readonly sortBy: string | null;

  /** The direction, or `null`. Only ever set alongside {@link UserListAddressQuery.sortBy}. */
  readonly sortDir: SortDirection | null;
}

/**
 * Reads a search out of an address.
 *
 * The three prefix modes are distinguished by the AXIS, reproducing the legacy switch: the two account fields
 * are matched by name and anything else is a profile property, which is what keeps the third axis an open set
 * (`Users.ascx.vb` L268-L274).
 *
 * @param axis The axis parameter, or `null` when absent.
 * @param text The filter parameter, or `null` when absent.
 * @returns The search to apply.
 */
function parseAddressSearch(axis: string | null, text: string | null): UserSearch {
  if (axis !== null && axis.trim().toLowerCase() === ALL_ACCOUNTS_TOKEN) {
    return { mode: 'all' };
  }

  // ⚠ AN ABSENT FILTER IS NOT AN EMPTY ONE. With no text there is nothing to match on, so the address has
  // asked for nothing and the policy chooses the opening view - which is NOT the same as searching for the
  // empty string, a query the legacy screen treated as no filter at all.
  if (text === null) {
    return { mode: 'none' };
  }

  const field: string = axis ?? USERNAME_SEARCH_FIELD;

  if (field === EMAIL_SEARCH_FIELD) {
    return { mode: 'email', text };
  }

  if (field === USERNAME_SEARCH_FIELD) {
    return { mode: 'username', text };
  }

  return { mode: 'profileProperty', propertyName: field, text };
}

/**
 * Reads the whole listing query out of an address.
 *
 * @param address The route's query parameters.
 * @returns The query to apply, with every unusable value resolved to its default.
 */
function parseUserListQuery(address: ParamMap): UserListAddressQuery {
  const axis: string | null = address.get(SEARCH_BY_PARAM);
  // Byte for byte, and NOT trimmed: the server is the one that trims, and the trailing wildcard is the
  // server's to append. Only an entirely absent parameter means no filter.
  const text: string | null = address.get(FILTER_PARAM);
  const search: UserSearch = parseAddressSearch(axis, text);

  // Resolved before the direction, because a direction is only meaningful once a field has survived
  // validation against what the endpoint will actually accept.
  const sortBy: string | null = parseSortKey(address.get(SORT_BY_PARAM), SORTABLE_COLUMN_KEYS);

  return {
    search,
    // The reserved token names no axis, so the selector falls back to its default rather than displaying it.
    axis:
      axis === null || axis.trim().toLowerCase() === ALL_ACCOUNTS_TOKEN ? USERNAME_SEARCH_FIELD : axis,
    pageIndex: parsePageIndex(address.get(PAGE_PARAM)),
    sortBy,
    // A direction with no field to apply it to is DROPPED, so the address can never carry half an ordering.
    sortDir: sortBy === null ? null : parseSortDirection(address.get(SORT_DIR_PARAM)),
  };
}

/**
 * The two search parameters a search should be written as.
 *
 * @param search The search in force.
 * @param axis The axis the selector is showing, used by the modes that carry one.
 * @returns The axis and filter parameters, either of which may be `null` to omit it.
 */
function searchParameters(search: UserSearch, axis: string): Params {
  switch (search.mode) {
    case 'none':
      // Nothing asked for is the BARE address, which is what leaves the policy free to choose.
      return { [SEARCH_BY_PARAM]: null, [FILTER_PARAM]: null };
    case 'all':
      return { [SEARCH_BY_PARAM]: ALL_ACCOUNTS_TOKEN, [FILTER_PARAM]: null };
    case 'username':
      // The default axis is omitted rather than stated, so a plain name search reads as `?filter=A`.
      return {
        [SEARCH_BY_PARAM]: axis === USERNAME_SEARCH_FIELD ? null : axis,
        [FILTER_PARAM]: search.text,
      };
    case 'email':
      return { [SEARCH_BY_PARAM]: EMAIL_SEARCH_FIELD, [FILTER_PARAM]: search.text };
    case 'profileProperty':
      return { [SEARCH_BY_PARAM]: search.propertyName, [FILTER_PARAM]: search.text };
  }
}

/**
 * Writes a listing query back out as address parameters.
 *
 * @param query The query in force.
 * @returns The parameters to merge into the address.
 */
function serialiseUserListQuery(query: UserListAddressQuery): Params {
  return {
    ...searchParameters(query.search, query.axis),
    // ⚠ A SEARCH-LESS ADDRESS CARRIES NO MEANINGFUL PAGE, so one is corrected away rather than obeyed. Which
    // page four IS depends on a result set nobody has asked for: with no search stated the tenant's policy
    // chooses the opening view and returns to the first page, so an address naming a page would describe a
    // screen that cannot exist. Every affordance on this screen writes the search ALONGSIDE the page for
    // exactly this reason, so this branch is only ever reached by a hand-edited or truncated address.
    [PAGE_PARAM]: query.search.mode === 'none' ? null : firstPageParameter(query.pageIndex),
    [SORT_BY_PARAM]: query.sortBy,
    // Emitted only alongside a field, matching exactly what the reader accepts back, so a round trip through
    // the address is stable rather than shedding a parameter on the way through.
    [SORT_DIR_PARAM]: query.sortBy === null || query.sortDir === null ? null : query.sortDir,
  };
}

/** The scheme `HtmlUtils.FormatEmail` emitted at `HtmlUtils.vb` L95. */
const MAILTO_SCHEME = 'mailto:';

/**
 * Display wording for a search field, keyed by its legacy name.
 *
 * `AddSearchItem` (`Users.ascx.vb` L205-L218) resolved each entry through
 * `Localization.GetString(name, LocalResourceFile)` and FELL BACK TO THE RAW NAME when the
 * lookup returned nothing. These twenty-one entries are the `*.Text` values the Users
 * resource file actually supplies; the fallback is reproduced by
 * {@link searchFieldLabel}, which is what keeps the third search axis an OPEN SET — a
 * tenant may declare any profile property, and one this map does not know is labelled with
 * its own name rather than rejected.
 *
 * Typed with an optional value so that an unknown key reads as `undefined` at the type
 * level too; the workspace does not enable unchecked indexed access, so a bare
 * `Record<string, string>` would have claimed a value that is not there.
 */
const SEARCH_FIELD_LABELS: Readonly<Record<string, string | undefined>> = Object.freeze({
  Username: 'Username',
  Email: 'Email',
  Prefix: 'Prefix',
  FirstName: 'First Name',
  MiddleName: 'Middle Name',
  LastName: 'Last Name',
  Suffix: 'Suffix',
  Unit: 'Unit',
  Street: 'Street',
  City: 'City',
  Region: 'Region',
  Country: 'Country',
  PostalCode: 'Postal Code',
  Telephone: 'Telephone',
  Cell: 'Cell',
  Fax: 'Fax',
  Website: 'Website',
  IM: 'IM',
  Biography: 'Biography',
  TimeZone: 'Time Zone',
  PreferredLocale: 'Preferred Locale',
});

/** One entry of the search-type selector. */
export interface UserSearchFieldOption {
  /** The value transmitted as the search axis: an account field name or a profile property name. */
  readonly value: string;

  /** The wording shown, resolved by {@link searchFieldLabel}. */
  readonly label: string;

  /**
   * The template's tracking key: this entry's ordinal joined to its value.
   *
   * ⚠ CARRIED AS DATA RATHER THAN DERIVED IN THE TEMPLATE, and the composition is load-bearing on both
   * halves. The ORDINAL is what keeps the key unique when a tenant declares a profile property literally
   * named `Username` or `Email`: that produces a genuine duplicate of an account field's value, which the
   * paired component preserves rather than de-duplicating because the legacy switch tested the account
   * field first, so a key of the value alone would raise a duplicated-key error on exactly the tenant
   * configuration this screen goes out of its way to keep working. The VALUE is what keeps the key STABLE:
   * the entries are rebuilt by a `computed()` whenever the declared properties are re-read, so tracking by
   * object identity re-created the entire list on every recomputation - Angular reports that as NG0956 -
   * and tracking by ordinal alone would silently reuse a row for a different axis when the declarations
   * change.
   *
   * The separator is the unit separator (U+001F), a control character that cannot occur in a profile
   * property name, so no pair of ordinal and value can collide with another by concatenation.
   */
  readonly trackKey: string;
}

/**
 * The separator joining an entry's ordinal to its value in {@link UserSearchFieldOption.trackKey}.
 *
 * The unit separator rather than a printable character: a profile property name is free text from the
 * tenant, so any printable choice - a colon, a hyphen, a pipe - is a character a name may legitimately
 * contain, and two different pairs could then compose the same key.
 */
const SEARCH_FIELD_KEY_SEPARATOR = '\u001F';

/**
 * Builds one entry of the search-type selector, key included.
 *
 * @param ordinal The entry's position in the selector, which the legacy `Items.Add` order fixes.
 * @param value The search axis transmitted verbatim: an account field name or a profile property name.
 * @returns The entry, carrying its resolved wording and its stable tracking key.
 */
function searchFieldOption(ordinal: number, value: string): UserSearchFieldOption {
  return {
    value,
    label: resolveSearchFieldLabel(value),
    trackKey: `${ordinal}${SEARCH_FIELD_KEY_SEPARATOR}${value}`,
  };
}

/**
 * The electronic-mail cell of one row, already decided.
 *
 * MIGRATION: this is `HtmlUtils.FormatEmail` (`Library/Components/Shared/HtmlUtils.vb`
 * L89-L102) expressed as data rather than as markup. The legacy helper CONCATENATED an
 * anchor element around the stored value and returned it as a string that a label control
 * then emitted, which is a script-injection vector for any address containing markup. Here
 * the address and the link target travel separately, the template binds the target through
 * `[href]` so the framework's URL sanitiser sees it, and the address itself is interpolated
 * as text and therefore escaped.
 *
 * MIGRATION: `Globals.CloakText` (`Library/Components/Shared/Globals.vb` L1137-L1165) is NOT
 * ported. It converted every character of the address to its numeric code and rebuilt the
 * markup at run time through `document.write` inside an injected script element, as
 * obfuscation against address harvesters. Reproducing it would require writing raw markup
 * into the document, which this workspace forbids outright, and it never protected an
 * address from a client that executes script. A documented functional reduction.
 */
export interface UserEmailCell {
  /** The address exactly as stored, for display. Empty when the account has none to show. */
  readonly text: string;

  /**
   * The `mailto:` target, or null when no link is warranted.
   *
   * Null in exactly the two cases the legacy helper declined to link: a blank value, and a
   * value carrying no mailbox separator. `HtmlUtils.vb` L94 tested for the separator and L97
   * returned the value UNCHANGED when it was absent, so a stored value that is not an
   * address renders as plain text in both the legacy screen and this one.
   */
  readonly mailto: string | null;
}

/**
 * Which optional columns the tenant shows.
 *
 * One member per `Column_*` setting the account policy carries. `Username` has NO member
 * because it has no setting: `Users.ascx.vb` L510-L511 made a column with an empty heading,
 * or a heading whose lower-cased form is `username`, visible UNCONDITIONALLY, and the
 * settings screen's resource file declares nine `Column_*` keys with no `Column_Username`
 * among them.
 */
interface UserColumnVisibility {
  readonly firstName: boolean;
  readonly lastName: boolean;
  readonly displayName: boolean;
  readonly address: boolean;
  readonly telephone: boolean;
  readonly email: boolean;
  readonly createdDate: boolean;
  readonly lastLogin: boolean;
  readonly authorized: boolean;
}

/**
 * The visibility a tenant that has configured nothing sees.
 *
 * MEASURED, NOT ASSUMED, and NOT uniformly true. `UserModuleBase.GetSettings`
 * (`Library/Components/Users/UserModuleBase.vb` L98-L124) filled each unset key with the
 * values below, so four of the nine columns were HIDDEN by default: the two name parts, the
 * address column's neighbour and the last-login column. Assuming a default of true would
 * have shown four columns the legacy screen did not.
 *
 * Applied only when the account policy could not be read at all — the store dispatches the
 * listing on both outcomes, so a tenant with an unreadable policy still sees its accounts,
 * and these are the closest thing to what it would have seen.
 */
const LEGACY_DEFAULT_COLUMN_VISIBILITY: UserColumnVisibility = Object.freeze({
  firstName: false,
  lastName: false,
  displayName: true,
  address: true,
  telephone: true,
  email: false,
  createdDate: true,
  lastLogin: false,
  authorized: true,
});

/**
 * Resolves the wording for a search field, falling back to the field's own name.
 *
 * Reproduces `AddSearchItem` (`Users.ascx.vb` L211-L212): look the name up, and use the name
 * itself when the lookup yields nothing.
 *
 * @param fieldName The account field name or tenant-declared profile property name.
 * @returns The wording to show, never empty for a non-empty name.
 */
function resolveSearchFieldLabel(fieldName: string): string {
  const label: string | undefined = SEARCH_FIELD_LABELS[fieldName];

  if (label === undefined) {
    return fieldName;
  }

  return label;
}

/**
 * Renders a nullable profile value as display text.
 *
 * The two states are kept distinct up to the point of display and are then rendered
 * identically, which is what the legacy screen did: `Null.NullString` is the EMPTY STRING
 * rather than null (`Library/Components/Shared/Null.vb` L71-L75), so a stored empty value and
 * an absent one were indistinguishable once read. The comparison is explicit against `null`
 * — never a coalesce, never a truthiness test — because the empty string is a legitimate
 * stored value on this contract and must not be turned into anything else.
 *
 * @param value The stored value, or null when the profile carries none.
 * @returns The value, or empty text when there is none.
 */
function plainProfileText(value: string | null): string {
  if (value === null) {
    return '';
  }

  return value;
}

/**
 * Whether a profile value the tenant has chosen to show carries nothing to paint — U-M2.
 *
 * ⚠ THE EMPTY STRING COUNTS AS ABSENT HERE, AND THAT IS A DELIBERATE DEPARTURE FROM
 * {@link plainProfileText}'s null-only comparison. The two are answering different questions. That
 * function asks "what is the value", where an empty string is a legitimate stored value the legacy
 * null contract makes indistinguishable from an absence (`Null.vb` L71-L75) and must not be
 * rewritten. This one asks "is there anything for a reader to see", and the answer for an empty
 * string is no — the cell paints nothing either way, so leaving it unmarked would reinstate exactly
 * the blank cell the mark exists to explain. Whitespace is treated the same way and for the same
 * reason: a cell holding three spaces looks identical to one holding nothing.
 *
 * @param value The stored value, or null when the profile carries none.
 * @returns True when the cell would otherwise paint nothing at all.
 */
function isProfileValueAbsent(value: string | null): boolean {
  return value === null || value.trim().length === 0;
}

/**
 * Whether a stored value is a mailbox this screen is willing to build a `mailto:` target from.
 *
 * ⚠ MAJOR (CWE-20 improper input validation) — THIS GATE DID NOT EXIST, AND ITS ABSENCE WAS AN
 * INJECTION. `dbo.Users.Email` is `[nvarchar] (256) NOT NULL` with no format constraint of any kind,
 * and the legacy application applied none either: `AddUser` stored whatever the caller supplied, so
 * the column holds arbitrary operator-supplied text on any installation with a history. The previous
 * rule was "contains an `@`", and everything after that character went into the address verbatim.
 *
 * A `mailto:` address is not opaque text to a mail client. Everything after a `?` is a QUERY, and its
 * `to`, `cc`, `bcc`, `subject` and `body` fields are honoured, so a stored value of
 * `a@b.example?bcc=harvester@elsewhere.example&body=…` composed a message that silently copied a
 * third party and pre-filled its own content. A `%0A` or a literal newline injects a HEADER. Two
 * separators — `a@b.example,victim@elsewhere.example` — address two mailboxes from one link. In every
 * case the operator sees the stored address in the cell and a different message in their mail client.
 *
 * The gate is deliberately CONSERVATIVE and structural rather than a full RFC 5322 grammar. It admits
 * exactly one separator with a non-empty local part and a non-empty domain, refuses every character
 * that carries meaning in a `mailto:` address or in a URL, and refuses whitespace and control
 * characters outright. A stricter-than-the-column rule is the correct direction here: a refused value
 * is still SHOWN, exactly as stored, so nothing is hidden from the operator — only the link is
 * withheld, and a link a mail client would misread is worth less than no link.
 *
 * @param value The stored value, already known to be non-blank.
 * @returns True when a `mailto:` target may be built from it.
 */
function isLinkableMailbox(value: string): boolean {
  // One separator, and exactly one. Two mailboxes in one stored value is a real legacy shape and is
  // precisely the case a lenient rule turns into a second recipient.
  const separator: number = value.indexOf(MAILBOX_SEPARATOR);

  if (separator <= 0 || separator !== value.lastIndexOf(MAILBOX_SEPARATOR)) {
    return false;
  }

  if (separator === value.length - 1) {
    return false;
  }

  // Everything that either carries meaning in a mailto address or terminates one. `?` opens the
  // query; `&` and `=` are its field syntax; `,` and `;` separate recipients; `#` opens a fragment;
  // `/` and `\` and `:` can restate a scheme; the quote and bracket family delimit display names and
  // domain literals; `%` is percent-encoding, which is how a control character is smuggled in as
  // text. Refused as a SET rather than escaped one by one, because escaping leaves the question of
  // whether the escape was correct and refusing does not.
  if (/[?&=,;#/\\:<>()[\]"'%\s]/.test(value)) {
    return false;
  }

  // C0 controls and DEL. A newline or a carriage return in a mailto address injects a header, and the
  // pattern above already refuses whitespace, but a control such as NUL or DEL is not whitespace.
  for (const character of value) {
    const code: number = character.codePointAt(0) ?? 0;

    if (code <= 0x1f || code === 0x7f) {
      return false;
    }
  }

  return true;
}

/**
 * Decides the electronic-mail cell for one address.
 *
 * Reproduces `HtmlUtils.FormatEmail` (`HtmlUtils.vb` L89-L102) branch for branch, with ONE deliberate
 * narrowing: a blank or whitespace-only value yields nothing at all; a value that is a linkable
 * mailbox yields a linked address; anything else yields the value unchanged and unlinked. The blank
 * test is made on a TRIMMED COPY while the value displayed is the original, exactly as the legacy
 * helper did — it tested `String.IsNullOrEmpty(Email.Trim)` and then concatenated the untrimmed
 * `Email`.
 *
 * ⚠ MAJOR (CWE-20) — THE NARROWING IS THE FIX, AND IT MOVES NOTHING OUT OF SIGHT. The legacy test was
 * "contains an `@`", which linked any stored value carrying one and passed everything after it into
 * the target unexamined; see {@link isLinkableMailbox} for what that admitted. A value that fails the
 * gate now takes the SAME arm a value with no separator always took: shown exactly as stored, with
 * nothing to follow. So the set of values this screen DISPLAYS is unchanged and only the set it links
 * is narrowed — which is the one behaviour a `mailto:` target can get wrong.
 *
 * The target is assembled here rather than in the template, so that no binding concatenates a URL, and
 * it is then CHECKED rather than escaped. `encodeURIComponent` was tried and rejected: the gate already
 * refuses every character it would escape except one — `+`, which is legal in a local part and carries
 * meaning there, so escaping it would emit `grace%2Badmin@…` for a stored `grace+admin@…` and would
 * change the address for any client that does not decode the escape. Percent-escaping a value that
 * needs none is not a safety measure; it is a silent rewrite of what the operator stored.
 *
 * What replaces it is {@link isSafeMailtoTarget}, applied to the assembled target. That is a check on
 * the OUTPUT rather than a second copy of the input rule, which is what makes it worth having: it holds
 * whatever the gate above is later relaxed to admit, and whatever the assembly is later changed to do.
 *
 * @param value The stored address. Non-nullable on the row contract, because the column is
 * declared not-null with an empty-string default.
 * @returns The text to show and the link target, or a null target when no link is warranted.
 */
function toEmailCell(value: string): UserEmailCell {
  if (value.trim().length === 0) {
    return { text: '', mailto: null };
  }

  if (!isLinkableMailbox(value)) {
    return { text: value, mailto: null };
  }

  const target = `${MAILTO_SCHEME}${value}`;

  // The output check. Unreachable through the gate above, and deliberately not written as an assertion
  // or an exception: a stored value can never be allowed to break the listing, so a target that cannot
  // be shown to be safe simply is not offered and the value renders as text like any other.
  if (!isSafeMailtoTarget(target)) {
    return { text: value, mailto: null };
  }

  return { text: value, mailto: target };
}

/**
 * Whether an assembled `mailto:` target addresses exactly one mailbox and states nothing else.
 *
 * ⚠ MAJOR (CWE-20) — THE OUTPUT CHECK, AND IT IS NOT A DUPLICATE OF THE INPUT GATE. The gate answers
 * "may this stored value be linked at all"; this answers "does what I am about to emit say only what I
 * meant". The two can diverge — a relaxed gate, an assembly that gains a parameter, a scheme constant
 * that changes — and the whole class of defect this finding names is a target saying more than the
 * characters on the screen. Checking the emitted string is what holds through any of those changes.
 *
 * @param target The assembled target.
 * @returns True when the target is a single-mailbox `mailto:` address.
 */
function isSafeMailtoTarget(target: string): boolean {
  if (!target.startsWith(MAILTO_SCHEME)) {
    return false;
  }

  const mailbox: string = target.slice(MAILTO_SCHEME.length);

  // Exactly one mailbox: one separator, with something on each side of it.
  const parts: readonly string[] = mailbox.split(MAILBOX_SEPARATOR);

  if (parts.length !== 2 || parts[0].length === 0 || parts[1].length === 0) {
    return false;
  }

  // Nothing that opens a query, a fragment, a second recipient or an escape, and no whitespace or
  // control character. Stated positively where it can be: only these characters may appear.
  return /^[A-Za-z0-9!#$&'*+\-/=^_`{|}~.@]+$/.test(mailbox) === true
    && /[?#,;%\s]/.test(mailbox) === false;
}

// ---------------------------------------------------------------------------
// THE COMPONENT
// ---------------------------------------------------------------------------

/**
 * Lists a tenant's registered accounts, with a free-text search, an alphabet filter, a pager
 * and three row commands.
 *
 * MIGRATION: THE COLUMN SET IS FOURTEEN LEGACY COLUMNS RESOLVED TO THIRTEEN. Three image
 * command columns become three command columns, ten data columns survive, and one is dropped:
 * `users.ascx` L35-L39 declared an unlabelled template column holding a single
 * `~/images/userOnline.gif` image whose visibility came from `Users.ascx.vb` L702. It carries
 * no heading, no alternative text and no information a heading could announce, and
 * users-online is out of scope for this migration, so it is not reproduced. A documented
 * functional reduction. (The row contract does carry a signed-in flag, so the reduction is a
 * scope decision rather than a data limitation.)
 *
 * MIGRATION: THERE IS NO ZEBRA STRIPING, and its absence is deliberate. `users.ascx` L25-L26
 * gave the item style and the alternating item style the SAME class, so alternate rows were
 * never tinted, and L23 set `GridLines="None"` so no cell carried a rule. The shared table is
 * therefore rendered without either affordance.
 *
 * MIGRATION: FIVE COLUMNS OFFER SORTING, AS A NET-NEW AFFORDANCE RATHER THAN A PORTED ONE. It
 * was previously declined here because `users.ascx` L22-L23 declares no `AllowSorting` and the
 * code-behind has no sort handler, which is true - and a case-insensitive census across BOTH
 * legacy trees finds the attribute exactly ONCE in either of them, in
 * `Website/admin/Files/filemanager.ascx`, a screen the AAP places out of scope. Not one in-scope
 * legacy grid could be reordered, INCLUDING the module listing which has offered sorting since
 * it was written, so the census says the same thing about every grid in this application and
 * cannot support having the affordance on one screen and not the rest. What bounds it instead is
 * the endpoint's own permitted set; the sortable columns, the endpoint field each carries and the
 * reasons the remaining columns are excluded are all recorded on
 * {@link SORTABLE_COLUMNS}.
 *
 * MIGRATION: NO ROW IS SELECTABLE. The legacy grid declared a selected-item style at
 * `users.ascx` L28 but no select command and no selection handler, so the style never
 * rendered. The shared table's row-activation output is accordingly not handled.
 */
@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [
    // Typed route segments for the two navigating row commands and the three header actions.
    RouterLink,

    // The page heading and its projected action bar.
    PageHeaderComponent,
    // The thirteen-column grid. It renders its OWN progress indicator and its own empty state
    // in a single spanning row, and lets waiting win over empty, so neither
    // LoadingSpinnerComponent nor EmptyStateComponent is declared here even though both are
    // available.
    DataTableComponent,
    // The pager, rendered as a SIBLING of the grid. `users.ascx` L82-L83 emitted two line
    // breaks and then declared its paging control AFTER the closing grid tag; the two line
    // breaks become a spacing token in this feature's stylesheet.
    PaginationComponent,
    // The free-text search control.
    SearchInputComponent,
    // Names the search-type selector. Composed BESIDE the search control rather than folded
    // into it: widening the shared search control's surface for this one consumer, or adding
    // an eleventh member to the shared library, would both be worse than composing the two
    // members that already exist.
    FormFieldComponent,
    // The per-row delete confirmation, with its focus trap and its escape handling. Its
    // PRESENCE IN THE DOM is what "open" means; it has no visibility input.
    ConfirmDialogComponent,
    // Inline surface for a listing, policy or declaration read that failed.
    ErrorBannerComponent,
    // Renders the authorisation flag as announced text rather than as one of a pair of
    // untitled images.
    YesNoPipe,
    // Renders the two instants. Both cells ask for the date-and-time shape explicitly; see
    // the column set.
    DateDisplayPipe,
  ],
  templateUrl: './user-list.component.html',
  styleUrl: './user-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserListComponent implements OnInit {
  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------

  /**
   * Owns every fact this screen shows and every request it issues.
   *
   * Root-provided and obtained through `inject`, so this component declares NO providers of
   * its own and nothing here can hold a second copy of the listing state.
   */
  private readonly store = inject(UserStore);

  /** The address this screen reads its search, axis and page from, and writes them back to. */
  private readonly route = inject(ActivatedRoute);

  /** Used to write the listing coordinates into the address rather than holding them privately. */
  private readonly router = inject(Router);

  /** Ties the address subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Whether the opening sequence has already been started for this visit.
   *
   * Held as a plain field rather than a signal because nothing renders from it. It exists so the FIRST
   * address emission only STAGES its search and lets the policy read that follows do the dispatching, while
   * every later emission dispatches for itself.
   */
  private hasOpened = false;


  /** Carries the transient outcome of a reader-initiated removal. */
  private readonly notifications = inject(NotificationService);

  /**
   * The session projection, read for the caller's identity and its administration fact.
   *
   * ⚠ THE RIGHT VOCABULARY FOR THIS QUESTION, AND THE PREVIOUS ONE WAS WRONG. The mutating
   * affordances were gated on the persisted permission KEY `EDIT`, which is a different
   * question over different data: the caller's permission keys are a union across the pages
   * and modules it holds rights on, and no member of that union says whether the caller may
   * administer accounts. Every address those affordances lead to is declared under the
   * tenant-administration POLICY, so that is the fact the gate reads — the same fact the route
   * guard reads, from the same authority.
   *
   * Also read for the caller's own account key and host status, which the row-level removal
   * guard needs: see {@link canRemove}.
   */
  private readonly auth = inject(AuthStore);


  // -------------------------------------------------------------------------
  // CELL AND COMMAND TEMPLATES
  // -------------------------------------------------------------------------
  //
  // Static queries, so every reference is resolved before `ngOnInit` runs and the command
  // columns can be assembled there. An `ng-template` the host declares belongs to the host's
  // view whether or not another component ends up rendering it, which is what makes projecting
  // one into the shared table work at all.

  /** Row edit command. Legacy `users.ascx` L32 `dnn:imagecommandcolumn CommandName="Edit"`. */
  @ViewChild('editCommand', { static: true })
  private editCommandTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Row delete command. Legacy `users.ascx` L33 `commandname="Delete"`, a post-back. */
  @ViewChild('deleteCommand', { static: true })
  private deleteCommandTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Row roles command. Legacy `users.ascx` L34 `CommandName="UserRoles"`. */
  @ViewChild('manageRolesCommand', { static: true })
  private manageRolesCommandTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /**
   * Postal address cell — U-M2. Legacy `users.ascx` L44-L49 `Profile.Street`.
   *
   * A TEMPLATE column rather than a formatted-text one, and the change of kind is what the fix
   * needed: a formatted-text column emits one string, and this cell has to emit a painted mark and
   * a hidden explanation of it as two separate elements when the value is absent.
   */
  @ViewChild('addressCell', { static: true })
  private addressCellTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Telephone cell — U-M2. Legacy `users.ascx` L50-L55 `Profile.Telephone`. Same kind change. */
  @ViewChild('telephoneCell', { static: true })
  private telephoneCellTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Electronic-mail cell. Legacy `users.ascx` L56-L61 `DisplayEmail(Membership.Email)`. */
  @ViewChild('emailCell', { static: true })
  private emailCellTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Creation instant. Legacy `users.ascx` L62-L67 `DisplayDate(Membership.CreatedDate)`. */
  @ViewChild('createdDateCell', { static: true })
  private createdDateCellTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Last sign-in instant. Legacy `users.ascx` L68-L73 `DisplayDate(Membership.LastLoginDate)`. */
  @ViewChild('lastLoginCell', { static: true })
  private lastLoginCellTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  /** Authorisation flag. Legacy `users.ascx` L74-L79, a pair of mutually exclusive images. */
  @ViewChild('approvedCell', { static: true })
  private approvedCellTemplate?: TemplateRef<DataTableCellContext<UserListItem>>;

  // -------------------------------------------------------------------------
  // WORDING AND TARGETS THE TEMPLATE BINDS
  // -------------------------------------------------------------------------

  /** `ControlTitle_.Text`. */
  protected readonly pageTitle = PAGE_TITLE;

  /** `AddContent.Action`. */
  protected readonly addUserLabel = ADD_USER_LABEL;

  /** `UserSettings.Action`. */
  protected readonly membershipSettingsLabel = MEMBERSHIP_SETTINGS_LABEL;

  /** {@link POLICY_DEGRADED_NOTICE}. Rendered only while {@link policyDegraded} holds. */
  protected readonly policyDegradedNotice = POLICY_DEGRADED_NOTICE;

  /** {@link ABSENT_PROFILE_VALUE_MARK} — U-M2. Painted, and hidden from assistive technology. */
  protected readonly absentProfileValueMark = ABSENT_PROFILE_VALUE_MARK;

  /** {@link ABSENT_PROFILE_VALUE_DESCRIPTION} — U-M2. Exposed, and hidden from the painted page. */
  protected readonly absentProfileValueDescription = ABSENT_PROFILE_VALUE_DESCRIPTION;

  /** {@link LOCKED_OUT_QUALIFIER} — U-M1. Painted beside the approval word. */
  protected readonly lockedOutQualifier = LOCKED_OUT_QUALIFIER;

  /** {@link LOCKED_OUT_DESCRIPTION} — U-M1. Exposed beside the painted qualifier. */
  protected readonly lockedOutDescription = LOCKED_OUT_DESCRIPTION;

  /** `ManageProfile.Action`. */
  protected readonly profileDefinitionsLabel = PROFILE_DEFINITIONS_LABEL;

  /**
   * `Search.Text`, from `users.ascx` L5 `lblSearch resourcekey="Search"`.
   *
   * ⚠ THE SHARED SEARCH CONTROL ALREADY PAINTS THIS EXACT WORDING as its own associated label,
   * so the template must NOT render it a second time beside the control. It is retained here so
   * the resource provenance of that wording is recorded in one place, and for use as an
   * accessible name wherever the screen needs one.
   */
  protected readonly searchLabel = SEARCH_LABEL;

  /** Authored, invisible name for the alphabet strip's navigation landmark. */
  protected readonly filterStripLabel = FILTER_STRIP_LABEL;

  /** Authored name for the unnamed legacy selector. */
  protected readonly searchFieldCaption = SEARCH_FIELD_LABEL;

  /** Associates {@link searchFieldLabel} with the selector element. */
  protected readonly searchFieldControlId = SEARCH_FIELD_CONTROL_ID;

  /** Placeholder for the free-text control. */
  protected readonly searchPlaceholder = SEARCH_PLACEHOLDER;

  /** `SharedResources.resx` `Edit.Text`. */
  protected readonly editCommandLabel = EDIT_COMMAND_LABEL;

  /** `Delete.Text`. */
  protected readonly deleteCommandLabel = DELETE_COMMAND_LABEL;

  /** `UserRoles.Text`. */
  protected readonly manageRolesCommandLabel = MANAGE_ROLES_COMMAND_LABEL;

  /** `SharedResources.resx` `DeleteItem.Text`. */
  protected readonly deleteConfirmMessage = DELETE_CONFIRM_MESSAGE;

  /** The create-account route. */
  protected readonly addUserLink = ADD_USER_LINK;

  /** The tenant account-policy route. */
  protected readonly membershipSettingsLink = MEMBERSHIP_SETTINGS_LINK;

  /** The tenant profile-declaration route. */
  protected readonly profileDefinitionsLink = PROFILE_DEFINITIONS_LINK;

  /** The role listing, target of the reduced third row command. */
  protected readonly manageRolesLink = MANAGE_ROLES_LINK;

  /** The unfiltered affordance's wording, which is also its filter value. */
  protected readonly allFilterLabel = ALL_FILTER_LABEL;

  /**
   * The alphabet strip: twenty-six letters, then the unfiltered affordance.
   *
   * Assembled exactly as `CreateLetterSearch` (`Users.ascx.vb` L304-L316) assembled it, minus
   * two entries. The letters come from the resource value; the unfiltered entry was appended
   * at L308. L309 and L310 appended a signed-in entry and an unauthorised entry as well, and
   * NEITHER is reproduced: `Users.ascx.vb` L258-L263 answered both from unpaged readers that
   * took no page coordinate, one of them from session tracking and a scheduled purge that this
   * migration does not carry forward, and no endpoint serves either. Two documented functional
   * reductions.
   *
   * A plain array rather than a signal, because the strip never changes: the resource value is
   * a constant here and the tenant cannot configure it.
   */
  protected readonly filterAffordances: readonly string[] = Object.freeze([
    ...LETTER_FILTER_LIST.split(LETTER_FILTER_SEPARATOR),
    ALL_FILTER_LABEL,
  ]);

  // -------------------------------------------------------------------------
  // STATE OWNED BY THIS SCREEN
  // -------------------------------------------------------------------------

  /**
   * The three command columns, assembled once in `ngOnInit`.
   *
   * Held separately from the data columns because their content comes from view queries that
   * resolve at a fixed moment, whereas the data columns depend on the tenant's policy and are
   * therefore derived. {@link columns} joins the two.
   */
  private readonly commandColumns = signal<readonly DataTableColumn<UserListItem>[]>([]);

  /**
   * The search axis the reader has chosen.
   *
   * Seeded to the account name because `Users.ascx.vb` L577 added that entry FIRST and
   * `AddSearchItem` selected an entry only when it matched a `filterProperty` query-string
   * value, so with no query string the first entry was the selected one. `Users.ascx.vb` L586
   * then passed `ddlSearchType.SelectedItem.Value` into every query.
   *
   * Held here rather than in the store because it is a control's state, not a listing fact: the
   * legacy selector had no auto-post-back, so changing it re-queried NOTHING until the search
   * button was pressed.
   */
  private readonly _searchField = signal<string>(USERNAME_SEARCH_FIELD);

  /** The account whose removal is awaiting confirmation, or null when none is. */
  private readonly _pendingRemoval = signal<UserListItem | null>(null);

  /**
   * The identifier of the removal this screen dispatched, or zero when none is outstanding.
   *
   * ⚠ AN IDENTIFIER AND NOT A BOOLEAN, AND THE DIFFERENCE IS A CORRECTNESS ONE. The store is
   * provided at the application root and publishes ONE aggregate write flag, so this screen used to
   * settle its removal by watching that flag fall — which happens when the FIRST write anywhere in
   * the application finishes. A save on another screen therefore consumed this screen's removal
   * marker: the outcome of a removal that was still in the air was reported from whatever the shared
   * failure slot happened to hold, and the refusal that arrived afterwards had no marker left to be
   * attributed to, so a row the server refused to delete silently stayed with nothing said.
   *
   * Zero is safe as "none outstanding" rather than being a sentinel collision: the store
   * pre-increments its counter, so the first identifier it ever issues is 1.
   */
  private readonly awaitedRemovalId = signal<number>(0);

  /** The chosen search axis, for the selector to mark its current option. */
  protected readonly searchField = this._searchField.asReadonly();

  /**
   * The shared search box, so the alphabet strip can call off a pending emission it would otherwise
   * be overtaken by.
   *
   * A view query rather than a bound input, because what is needed is a COMMAND at a moment in time —
   * see {@link SearchInputComponent.cancelPendingSearch}. Optional because the box is inside no
   * conditional block today, so it is always present, and asserting that with a required query would
   * make a future conditional a run-time failure rather than a no-op.
   */
  private readonly searchBox = viewChild(SearchInputComponent);

  /**
   * The account awaiting removal confirmation, or null when none is.
   *
   * Read by the template to decide whether to render the confirmation at all: the shared
   * dialogue has no visibility input, so its PRESENCE in the document is what "open" means, and
   * its removal from the document is what closing it means.
   */
  protected readonly pendingRemoval = this._pendingRemoval.asReadonly();

  /**
   * Whether the caller may be offered the tenant-administration affordances.
   *
   * Gates the create link and the row-level edit and removal commands — every one of which
   * addresses a route or an endpoint declared under the tenant-administration policy. Reads
   * `false` while the caller's identity is unresolved, which is the safe direction for a gate.
   *
   * ⚠ THIS IS THE SERVER'S OWN DETERMINATION, re-exposed rather than recomputed. The store's
   * `administersCurrentPortal` is `isSuperUser` OR the API's `isPortalAdministrator`, and
   * nothing here inspects a role NAME: `Portals.AdministratorRoleId` is what confers tenant
   * administration, the designated role is renameable, and a tenant may hold several roles
   * that administer it.
   */
  protected readonly administersPortal: Signal<boolean> = this.auth.administersCurrentPortal;

  // -------------------------------------------------------------------------
  // STORE-DERIVED SURFACE
  // -------------------------------------------------------------------------
  //
  // These are the store's own signals re-exposed under template-facing names. They are NOT
  // copies — assigning a signal shares it — so there is exactly one source of truth and nothing
  // here can drift from it. None of them is writable: the store publishes read-only views and
  // is commanded through its methods, so no `set` or `update` call on listing state appears
  // anywhere below.

  /**
   * The accounts on the page in hand.
   *
   * A fresh array arrives on every re-query, because the store replaces the paged envelope
   * rather than mutating it — which the shared table requires, since it tracks a row by object
   * reference rather than by an identifier member.
   */
  protected readonly rows: Signal<readonly UserListItem[]> = this.store.userRows;

  /**
   * Whether the listing request is in flight.
   *
   * Handed to the shared table, which shows its own progress indicator in a single spanning row
   * and lets waiting win over empty, so this screen renders neither a spinner nor an empty state
   * of its own.
   */
  protected readonly loading: Signal<boolean> = this.store.usersLoading;

  /** Whether a write is in flight. Used to keep a second removal from being dispatched. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * The ZERO-BASED index of the page in hand, exactly as the server reported it.
   *
   * MIGRATION — AND THIS IS THE OFF-BY-ONE THE MIGRATION TURNS ON. The legacy screen ran BOTH
   * bases at once: `Users.ascx.vb` L51 seeded a ONE-based `CurrentPage`, while every provider
   * call passed `CurrentPage - 1` (L265, L269, L271 and L274). NO ±1 ARITHMETIC APPEARS IN THIS
   * FILE, and that is a verified decision rather than an oversight: the shared pagination
   * component's `page` input is itself zero-based, it derives the one-based number a reader
   * sees internally, and its change output emits a zero-based index. Both ends of the binding
   * are therefore already on the wire's base, and adding one here would show the wrong page
   * number and request the wrong page.
   *
   * The index REPORTED BY THE SERVER is bound rather than the index this screen last requested,
   * so the pager can never claim to be on a page whose request failed.
   */
  protected readonly pageIndex: Signal<number> = this.store.currentPageIndex;

  /**
   * The page size the server actually applied.
   *
   * NEVER A CONSTANT IN THIS FILE. `Users.ascx.vb` L114-L119 read the size from the tenant's
   * records-per-page setting through `UserModuleBase.GetSetting`, whose fallback of ten lives at
   * `UserModuleBase.vb` L134-L136 and, in the target, in the shared paging contract. The store
   * owns that resolution and this screen reads the applied result, so no page size is hard-coded
   * and no default is restated here.
   */
  protected readonly pageSize: Signal<number> = this.store.appliedPageSize;

  /**
   * The total across every page.
   *
   * MIGRATION: the legacy total arrived through an argument passed BY REFERENCE —
   * `GetUsers(portalId, …, pageIndex, pageSize, ByRef totalRecords)` — so the count was a side
   * effect on a caller's variable. It now travels inside the paged envelope beside the rows.
   */
  protected readonly totalCount: Signal<number> = this.store.totalCount;

  /**
   * Whether the tenant's preference and the page in hand together warrant a pager.
   *
   * ADVISORY. The rule is the legacy one — `Users.ascx.vb` L278-L280 narrowed the pager's
   * visibility to the case where the page size was smaller than the total, and only when the
   * tenant had asked for suppression — and the store computes it so that no screen restates it.
   * Whether a pager RENDERS remains the shared pagination component's own decision; it declines
   * to render when everything fits on one page.
   */
  protected readonly pagerWarranted: Signal<boolean> = this.store.pagerWarranted;

  /** Whether nothing at all matched, as distinct from having paged past the end. */
  protected readonly isEmptyResult: Signal<boolean> = this.store.isEmptyResult;

  /**
   * Whether there is a result COUNT worth stating, which is what mounts the shared pager.
   *
   * ⚠ WIDER THAN "MORE THAN ONE PAGE", AND NARROWER THAN "ALWAYS". The pager decides its own shape - the
   * range summary alone when everything fits on one page, the summary plus the steps when it does not -
   * so mounting it on navigability would remove the only on-screen confirmation of how many records
   * matched, which runtime testing measured happening on every list screen. Mounting it unconditionally
   * would instead leave an empty custom element in the document on a zero-result screen, where the
   * empty-state component already says what happened in words. Counting from one upwards is the
   * condition that gives both statements a place to live.
   */
  protected readonly hasResults: Signal<boolean> = computed(() => this.totalCount() > 0);


  /**
   * Whether the listing has been asked for nothing at all, as distinct from having matched nothing.
   *
   * True when the tenant's `Display_Mode` selects the no-query view — which is also the mode the
   * legacy applied to an absent setting, so it is the state a newly configured tenant opens in.
   */
  protected readonly noQueryIssued: Signal<boolean> = this.store.noQueryIssued;

  /** The notice shown while no query has been issued. */
  protected readonly noQueryNotice: string = NO_QUERY_NOTICE;

  /** Whether the requested page lies beyond a match set that is not itself empty. */
  protected readonly isPastEnd: Signal<boolean> = this.store.isPastEnd;

  // -------------------------------------------------------------------------
  // DERIVED VIEWS
  // -------------------------------------------------------------------------

  /**
   * The entries of the search-type selector.
   *
   * Reproduces `Page_Load` L577-L582 in order: the account name, then the address, then ONE
   * ENTRY PER TENANT-DECLARED PROFILE PROPERTY. The declarations come from the store's own
   * unpaged slice rather than from a call made here, because reading them is a transport concern
   * the store owns.
   *
   * The property names are transmitted VERBATIM: this axis is an OPEN SET, so a name is never
   * validated, never case-folded and never checked against a fixed list. `Users.ascx.vb`
   * L272-L274 passed its field name straight through as the property name for exactly that
   * reason, and an unrecognised name is the server's to refuse.
   *
   * ⚠ EACH ENTRY CARRIES ITS OWN TRACKING KEY, and that is what makes the template's `@for` cheap.
   * This is a `computed()` over the store's declared-property slice, and that slice is itself derived,
   * so it publishes a NEW array - and this one rebuilds NEW entry objects - every time the declarations
   * are re-read. Tracking by object identity therefore re-created the whole selector on each
   * recomputation, which Angular reports as NG0956 (measured: 6 occurrences across three collections in
   * the test suite before this change). The key composed by {@link searchFieldOption} is stable across
   * recomputation and unique per entry, so an unchanged list re-uses its rows and a grown list keeps the
   * two leading account rows. See {@link UserSearchFieldOption.trackKey} for why neither the value alone
   * nor the ordinal alone would do.
   */
  protected readonly searchFieldOptions: Signal<readonly UserSearchFieldOption[]> = computed(
    () => {
      const options: UserSearchFieldOption[] = [
        searchFieldOption(0, USERNAME_SEARCH_FIELD),
        searchFieldOption(1, EMAIL_SEARCH_FIELD),
      ];

      for (const propertyName of this.store.profilePropertyNames()) {
        options.push(searchFieldOption(options.length, propertyName));
      }

      return options;
    },
  );

  /**
   * Which optional columns the tenant shows.
   *
   * MIGRATION: the gate is the legacy gate. `Page_Init` L508-L517 walked every grid column,
   * composed a settings key as `"Column_" + column.HeaderText` FROM THE RAW MARKUP HEADING, read
   * the tenant's setting and assigned the result to `column.Visible`. The raw heading matters:
   * `Page_Init` runs BEFORE `Page_Load` L585 localises the headings, which is why the keys are
   * `Column_CreatedDate`, `Column_LastLogin` and `Column_DisplayName` rather than the localised
   * spellings "Created Date", "Last Login" and "Name". Those keys arrive here already resolved
   * onto the account policy contract, so no key is composed at run time and the trap cannot be
   * re-entered.
   *
   * Every flag is compared EXPLICITLY against `true`. False is DATA on this contract, not an
   * absence — the legacy absent-Boolean marker was itself `False`
   * (`Library/Components/Shared/Null.vb` L76-L80), so in the legacy model a switched-off column
   * and an unset one were indistinguishable, whereas here the wire value means what it says. A
   * truthiness test, a negation, a coalesce or a cast would each reintroduce that ambiguity.
   */
  private readonly columnVisibility: Signal<UserColumnVisibility> = computed(() => {
    const settings: MembershipSettings | null = this.store.membershipSettings();

    if (settings === null) {
      return LEGACY_DEFAULT_COLUMN_VISIBILITY;
    }

    return {
      firstName: settings.columnFirstName === true,
      lastName: settings.columnLastName === true,
      displayName: settings.columnDisplayName === true,
      address: settings.columnAddress === true,
      telephone: settings.columnTelephone === true,
      email: settings.columnEmail === true,
      createdDate: settings.columnCreatedDate === true,
      lastLogin: settings.columnLastLogin === true,
      authorized: settings.columnAuthorized === true,
    };
  });

  /**
   * The column set handed to the shared table: the three commands, then the visible data columns.
   *
   * Derived rather than assembled once, because the tenant's policy arrives asynchronously and
   * the visible set changes the moment it does.
   *
   * ⚠ THE ACCOUNT-NAME COLUMN IS UNCONDITIONAL and is deliberately not gated. `Page_Init`
   * L510-L511 made a column visible without consulting any setting when its heading was empty or
   * lower-cased to `username`, and the settings screen declares no `Column_Username` key at all.
   *
   * Every body cell is START-aligned and every heading is CENTRE-aligned, reproducing
   * `users.ascx` L24 (`verticalalign="Top" horizontalalign="Center"` on the heading style) and
   * L25 (`horizontalalign="Left"` on the item style). Alignment is passed per column rather than
   * set once, because the shared table's default differs from the legacy heading treatment.
   */
  protected readonly columns: Signal<readonly DataTableColumn<UserListItem>[]> = computed(() => {
    const visible: UserColumnVisibility = this.columnVisibility();
    const set: DataTableColumn<UserListItem>[] = [...this.commandColumns()];

    // `users.ascx` L40. `key` is this column's stable identity; `field` is the row contract's
    // own member spelling, which is the single lower-case word `username`. The two differ
    // deliberately and the difference is load-bearing: a mis-spelled `field` would read
    // `undefined` off the row and render an empty cell with no error anywhere.
    set.push({
      key: 'userName',
      // The row's NAME. Emitted as `<th scope="row">` so a screen reader announces which record
      // each cell belongs to - without it, traversing a row gives the column name and the value
      // and never the record's identity. This column is the one a person would read aloud to say
      // which row they mean. No visual change: the shared stylesheet restores a body row
      // header's normal weight.
      rowHeader: true,
      // Ordering: see SORTABLE_COLUMNS, which is the sortable set and the only place a column key
      // is paired with the endpoint's own sort name.
      sortable: true,
      label: USERNAME_HEADING,
      headerAlign: 'center',
      bodyAlign: 'start',
      field: 'username',
    });

    // `users.ascx` L41. Hidden by DEFAULT — see LEGACY_DEFAULT_COLUMN_VISIBILITY.
    if (visible.firstName === true) {
      set.push({
        key: 'firstName',
        sortable: true,
        label: FIRST_NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'firstName',
      });
    }

    // `users.ascx` L42. Hidden by default.
    if (visible.lastName === true) {
      set.push({
        key: 'lastName',
        sortable: true,
        label: LAST_NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'lastName',
      });
    }

    // `users.ascx` L43, whose heading the resource file renames from "DisplayName" to "Name".
    if (visible.displayName === true) {
      set.push({
        key: 'displayName',
        sortable: true,
        label: DISPLAY_NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'displayName',
      });
    }

    // `users.ascx` L44-L49 composed six profile values through `DisplayAddress` →
    // `Globals.FormatAddress` (`Library/Components/Shared/Globals.vb` L1096-L1136), in the order
    // unit, street, city, region, country, postal code, appending each non-blank part behind a
    // comma and space and then stripping the leading separator.
    //
    // MIGRATION: THAT COMPOSITION NOW HAPPENS SERVER-SIDE and the row contract carries the
    // finished text — the six parts are profile VALUES that the 02.02.01 upgrade script moved off
    // the account table, so composing them client-side would require fetching a profile per row.
    // Nothing is re-composed here; the value is rendered as it arrives, and an absent address
    // renders as empty text rather than as the word "null".
    if (visible.address === true) {
      set.push({
        key: 'address',
        label: ADDRESS_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.addressCellTemplate, 'addressCell'),
      });
    }

    // `users.ascx` L50-L55.
    //
    // MIGRATION — DEFECT CORRECTED AND ANNOTATED. The legacy cell applied `DisplayEmail` to the
    // TELEPHONE NUMBER, i.e. the electronic-mail formatter to a value that is not an address.
    // The consequence was LATENT rather than visible: `HtmlUtils.FormatEmail` emitted an anchor
    // only when the value carried a mailbox separator (`HtmlUtils.vb` L94), so a well-formed
    // telephone number passed through unchanged and the grid looked correct. A number containing
    // an "@" — a stored extension note, say — would have been wrapped in a `mailto:` link
    // pointing at a telephone number. It is formatted as a telephone number here, which is a
    // deliberate divergence recorded rather than a silent fix.
    if (visible.telephone === true) {
      set.push({
        key: 'telephone',
        label: TELEPHONE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.telephoneCellTemplate, 'telephoneCell'),
      });
    }

    // `users.ascx` L56-L61. A template column, because the cell carries a link rather than text;
    // {@link emailCells} decides the link and the text together.
    if (visible.email === true) {
      set.push({
        key: 'email',
        // Ordered on the STORED address, not on the anchor the cell template builds from it.
        sortable: true,
        label: EMAIL_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.emailCellTemplate, 'emailCell'),
      });
    }

    // `users.ascx` L62-L67. Hidden by default is FALSE for this one: the tenant default shows it.
    //
    // A template column because a pipe cannot run in a bound-text column, and the shape must be
    // requested EXPLICITLY. `DisplayDate` (`Users.ascx.vb` L400) rendered the instant with the
    // plain general format — a short date AND a long time — whereas the shared date pipe defaults
    // to a short date alone, so this cell and the next ask for the date-and-time shape by name.
    if (visible.createdDate === true) {
      set.push({
        key: 'createdDate',
        label: CREATED_DATE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.createdDateCellTemplate, 'createdDateCell'),
      });
    }

    // `users.ascx` L68-L73. Hidden by default.
    if (visible.lastLogin === true) {
      set.push({
        key: 'lastLoginDate',
        label: LAST_LOGIN_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.lastLoginCellTemplate, 'lastLoginCell'),
      });
    }

    // `users.ascx` L74-L79.
    //
    // MIGRATION — THE OPTION-STRICT COERCION IS MADE EXPLICIT. The legacy markup drew one of two
    // images by evaluating `Membership.Approved=true` and `Membership.Approved=false`, comparing
    // a strongly-typed Boolean against UNQUOTED Boolean literals. It compiled only because the
    // administration pages were built with strict type checking switched off —
    // `Website/release.config` L125 declares `<compilation debug="false" strict="false">` and
    // `Website/development.config` L123 does the same — while the class library was built with it
    // on. The sibling role listing shows the same construct from the other side, comparing the
    // same kind of flag against the STRING "true" over an untyped collection. Neither survives
    // translation: the flag is a Boolean here, the shared pipe compares it by identity against
    // `true`, and both states render as announced words instead of as one of a pair of untitled
    // images.
    if (visible.authorized === true) {
      set.push({
        key: 'approved',
        label: AUTHORIZED_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.approvedCellTemplate, 'approvedCell'),
      });
    }

    return set;
  });

  /**
   * The editor route of every account on the page, keyed by identifier.
   *
   * PRECOMPUTED ONCE PER PAGE rather than per row per change-detection pass, and bound as an
   * index rather than called from the template. A router link is compared by IDENTITY, so an
   * array rebuilt on every pass is a new reference every time and the router re-parses a target
   * that has not changed, once per row, on every pass — which defeats push change detection
   * outright.
   *
   * The arrays are intentionally MUTABLE rather than read-only: the router's link input is
   * declared as a mutable array union, and a read-only element type is not assignable to it under
   * strict template checking.
   *
   * The identifier is used exactly as received. `Users.UserID` is declared `IDENTITY(1, 1)` so no
   * account is keyed nought in practice, but nothing here relies on that: no identifier in this
   * file is tested for truthiness, for positivity or against minus one, because this schema makes
   * every such test wrong somewhere — tenant keys seed at minus one, role, page and module keys
   * seed at nought, and minus one is simultaneously the legacy marker for a missing integer.
   *
   * Replaces `Users.ascx.vb` L530, which built `EditUrl("UserId", "KEYFIELD", "Edit", …)` with a
   * dummy token and then substituted a format placeholder into the rendered URL.
   */
  protected readonly editUserLinks: Signal<Readonly<Record<number, (string | number)[]>>> =
    computed(() => {
      const links: Record<number, (string | number)[]> = {};

      for (const account of this.rows()) {
        links[account.userId] = [USERS_PATH, account.userId];
      }

      return links;
    });

  /**
   * The electronic-mail cell of every account on the page, keyed by identifier.
   *
   * Precomputed on the same terms and for the same reason as {@link editUserLinks}: the shared
   * table's own documentation warns that a template-invoked formatter re-runs on every
   * change-detection pass, once per cell, and calls that the single most likely performance
   * defect in a grid.
   */
  protected readonly emailCells: Signal<Readonly<Record<number, UserEmailCell>>> = computed(() => {
    const cells: Record<number, UserEmailCell> = {};

    for (const account of this.rows()) {
      cells[account.userId] = toEmailCell(account.email);
    }

    return cells;
  });

  /**
   * The problem document behind a failed READ, or null when no read has failed.
   *
   * ⚠ #5 — CONFINED TO THE TWO READS WHOSE SUBJECT IS THIS SCREEN'S OWN CONTENT, AND THE ACCOUNT
   * POLICY IS DELIBERATELY NOT ONE OF THEM. It used to be, and that was the defect: a failed
   * policy read raised an assertive screen-level banner reading "Not Found", with a Try-again
   * command, directly above a listing of two hundred and fifty-one healthy accounts. Nothing was
   * missing from the screen — the listing had loaded, the rows were correct, and the only casualty
   * was the tenant's preferred page size and column selection, for which this screen already holds
   * a documented fallback. The banner therefore reported a failure of the listing that had not
   * happened, and gave the operator a retry for a listing that needed none. The policy is an
   * ENHANCEMENT to this screen, not a prerequisite of it, so its failure is disclosed quietly
   * through {@link policyDegraded} instead of asserted here. See also the store's own
   * `dispatchSettings`, which already dispatches the listing on BOTH policy outcomes for the same
   * reason.
   *
   * A failed WRITE is reported through the notification service instead — see {@link reportRemovalOutcome} — because the legacy screen
   * reported a failed removal as a transient module message rather than as a permanent surface,
   * and because a refusal must be presented at WARNING severity rather than as an error, which
   * the notification path and the shared summariser between them already arrange.
   */
  protected readonly readFailure: Signal<ProblemDetails | null> = computed(() => {
    const held: UserFailure | null = this.store.failure();

    if (held === null) {
      return null;
    }

    if (held.operation !== 'loadUsers' && held.operation !== 'loadProfileDefinitions') {
      return null;
    }

    return held.problem;
  });

  /**
   * The sentence to show when a read of this screen's failed WITHOUT a problem document.
   *
   * ⚠ THIS CLOSES A CLASS OF FAILURE THAT WAS COMPLETELY SILENT, AND SILENCE WAS THE WHOLE DEFECT.
   * The runtime decoders that check each response against its published contract run inside the
   * service's own mapping, which is DOWNSTREAM of the interceptor's error handling — so a `200`
   * whose body does not match its contract throws a plain error carrying no document, no status and
   * no support reference. {@link readFailure} is therefore `null` for it, the banner rendered
   * nothing, the grid stayed empty because no rows were committed, and no surface on the screen said
   * why. An operator saw an account listing that had simply stopped having accounts in it.
   *
   * The store's own authored summary is used rather than a sentence invented here: the shared
   * summariser already words a failure with no document, and the store already holds that wording on
   * the failure it recorded, so this reads it out instead of composing a second vocabulary. The
   * retry path is the screen's existing search and paging affordances, which re-dispatch the read —
   * nothing is disabled by a failed read, so they remain reachable.
   *
   * Null whenever a document IS present, so the banner shows the server's own explanation in
   * preference to this and the two can never both speak.
   */
  protected readonly readFailureSummary: Signal<string | null> = computed(() => {
    const held: UserFailure | null = this.store.failure();

    if (held === null || held.problem !== null) {
      return null;
    }

    if (held.operation !== 'loadUsers' && held.operation !== 'loadProfileDefinitions') {
      return null;
    }

    return held.summary.message;
  });

  /**
   * Whether this screen has a read failure to present at all.
   *
   * ⚠ THIS IS THE GATE, AND IT IS DELIBERATELY NOT "IS THERE A DOCUMENT". The failure surface is
   * wrapped in a block, and that block used to be opened by {@link readFailure} alone — so a failure
   * carrying no problem document opened nothing, and the authored summary beside it could never be
   * reached however correctly it was bound. That is precisely the contract-violating `200` case: the
   * decoders run downstream of the interceptor, so there is no document to gate on. The gate is
   * therefore "either surface has something to say", which is the union of the two inputs the block
   * contains rather than one of them.
   *
   * Not derived from `store.failure() !== null`, because the store is provided at the application
   * root and its slot holds whatever failed most recently ANYWHERE. Both members below are already
   * confined to this screen's three read operations, so composing them keeps that confinement in one
   * place instead of restating it a third time.
   */
  protected readonly hasReadFailure: Signal<boolean> = computed(
    () => this.readFailure() !== null || this.readFailureSummary() !== null,
  );

  /**
   * Whether the tenant's account policy could not be read, so this screen is running on its
   * documented fallbacks.
   *
   * ⚠ #5 — THIS IS WHAT THE SCREEN-LEVEL BANNER WAS DOING WRONG, DONE RIGHT. The policy decides two
   * things here and nothing else: how many accounts a page holds, and which of the nine optional
   * columns are shown. Both already have a fallback that this file declares and documents — the
   * shared default page size and {@link LEGACY_DEFAULT_COLUMN_VISIBILITY} — so an unread policy
   * costs the operator a preference, not a listing. What it must NOT cost them is the truth: a
   * screen silently showing ten rows a page when the tenant asked for fifty, with no indication
   * that its preference was not honoured, is the mirror of the defect being fixed.
   *
   * Both conditions are required. The failure slot is at the application root and holds whatever
   * failed most recently ANYWHERE, so naming the operation is what confines this to a policy read;
   * and a policy that arrived on an earlier visit is still in the store and still being applied, so
   * a later failure of a REFRESH has degraded nothing and must say nothing.
   *
   * ⚠ A TENANT THAT STORES NO POLICY IS NOT DEGRADED, and the distinction is the point of the first
   * test below. The server answers such a tenant `200` with ITS OWN legacy defaults and marks the
   * document `isStored: false`, so the columns and the page size on screen came from the server and
   * are the values the legacy screens applied for the same tenant. Nothing was lost, so there is
   * nothing to disclose here - the store publishes the provenance through
   * `membershipSettingsUnconfigured` and the POLICY screen is where that is explained, because it is
   * the only screen on which the distinction changes what an operator may do. Reporting it here as
   * degradation would put a notice on a healthy listing, which is the defect this notice was added to
   * fix, wearing the opposite sign.
   *
   * What remains, and is genuinely degradation, is a policy the client does not hold at all: a read
   * that was REFUSED. Then these fallbacks are this file's own rather than the server's, and an
   * operator whose tenant asked for fifty rows is looking at ten with no other indication.
   */
  protected readonly policyDegraded: Signal<boolean> = computed(() => {
    if (this.store.membershipSettings() !== null) {
      return false;
    }

    const held: UserFailure | null = this.store.failure();

    return held !== null && held.operation === 'loadMembershipSettings';
  });

  /**
   * The sentence naming the filter the LISTING is currently applying, or `null` when the strip
   * already says it.
   *
   * ⚠ U-M12 AND THE COMPOUND HALF OF #7. Rendered only for the states the alphabet strip cannot
   * announce, so the two surfaces never say the same thing twice:
   *
   *   - `'all'` and a single-letter account-name prefix are both announced by a pressed strip
   *     entry, so this is `null` for them;
   *   - `'none'` is the tenant's own opening view choosing to issue no query, which the screen's
   *     existing no-query notice already explains, so this stays out of it;
   *   - every other state — a multi-character account-name prefix, ANY electronic-mail prefix, and
   *     any profile-property prefix — leaves every strip entry unpressed, which is truthful and
   *     silent, and is exactly where this speaks.
   *
   * The axis it names is the axis the LISTING is filtered on, taken from the store's own search
   * discriminator, and NOT the axis currently showing in the selector. The distinction is the point:
   * a selector change applies nothing until a search is run, so an operator who has changed it sees
   * this sentence still naming the old axis and can tell their change is not yet in force.
   *
   * The term is rendered exactly as it is being matched — not trimmed, not case-folded, not
   * decorated — because a disclosure that tidied the term would describe a query the server is not
   * running. It is interpolated as plain text, so no markup can reach the document through it.
   */
  protected readonly filterDisclosure: Signal<string | null> = computed(() => {
    const search = this.store.search();

    if (search.mode === 'none' || search.mode === 'all') {
      return null;
    }

    if (search.mode === 'username') {
      // A single letter is exactly what the strip announces, so saying it again here would put the
      // same fact on the screen twice. Compared on length rather than against the strip's entries,
      // because `isFilterApplied` already owns that comparison and duplicating it would let the two
      // disagree.
      if (search.text.length <= 1) {
        return null;
      }

      return this.composeFilterDisclosure(USERNAME_AXIS_WORDING, search.text);
    }

    if (search.mode === 'email') {
      return this.composeFilterDisclosure(EMAIL_AXIS_WORDING, search.text);
    }

    // A profile property is named by the tenant, so its own name is the only wording available and
    // it is used verbatim. `propertyName` is an open set and is never validated here.
    return this.composeFilterDisclosure(search.propertyName, search.text);
  });

  // -------------------------------------------------------------------------
  // OUTCOME REPORTING
  // -------------------------------------------------------------------------

  /**
   * Reports the outcome of a removal once it has settled.
   *
   * AN EFFECT, because emitting a reader-visible message IS a genuine side effect: the store's
   * command methods return void and subscribe internally, so there is no completion callback to
   * hang one on. This effect LOADS NOTHING — `ngOnInit` does the loading — because an effect used
   * as a loader re-fires on every unrelated signal change it happens to read.
   *
   * IDEMPOTENT BY CONSTRUCTION. It acts on a TRANSITION rather than on a state: it returns
   * immediately unless the settled write is the one this screen dispatched, and the first thing it
   * does once that holds is clear the identifier, so a later change to any signal it reads cannot
   * report the same outcome twice. Clearing it inside `untracked` keeps that write out of the
   * effect's own dependency set, which is what stops it re-triggering itself.
   *
   * ⚠ SETTLED ON THE STORE'S PUBLISHED RESULT, NOT ON ITS AGGREGATE FLAG FALLING, AND THE FAILURE IS
   * TAKEN FROM THAT RESULT. Both halves matter and each closed a different defect. The flag falls when
   * the first write anywhere in the application finishes, so watching it let an unrelated save settle
   * this screen's removal. And the shared failure slot is cleared at every dispatch and holds whatever
   * failed most recently, so reading the outcome from there could report another screen's refusal as
   * this removal's — or report a refused removal as successful, if anything else dispatched in
   * between. The result carries the identifier the store handed back and the failure the write itself
   * recorded, so neither mistake is expressible.
   */
  constructor() {
    // ⚠ THE CHOSEN AXIS IS RECONCILED AGAINST WHAT IS STILL DECLARED, AND WITHOUT THIS THE SCREEN
    // LIED ABOUT WHAT IT WAS SEARCHING. The third axis is one entry per tenant-declared profile
    // property, and the declarations are read into the store independently of this selector: they
    // arrive after the screen opens, and they change when a property is removed on the neighbouring
    // profile-declarations screen, which shares the same application-scoped store. The chosen axis was
    // written only by the selector's own change handler and never revisited, so when the property it
    // named stopped being declared its `<option>` disappeared — and a `<select>` whose selected value
    // is no longer among its options FALLS BACK TO THE FIRST OPTION IN THE BROWSER while the component
    // went on holding the removed name. The operator read "Username" on screen — the label the legacy
    // resource file gives that axis — and every search they ran queried the deleted property, which the
    // server answers by refusing or by matching nothing.
    //
    // Reset to the ACCOUNT-NAME axis, not to whatever now happens to be first: that is the entry
    // `Page_Load` L577 added first and therefore the legacy default, and it is the value this
    // component seeds with, so the reconciliation lands where the screen started.
    //
    // An effect rather than a `computed`, because this WRITES the state a person chose. A computed
    // would have to be read to take effect and would silently discard the choice on every unrelated
    // recomputation.
    effect(() => {
      const chosen: string = this._searchField();
      const declared: readonly string[] = this.store.profilePropertyNames();

      untracked(() => {
        if (chosen === USERNAME_SEARCH_FIELD || chosen === EMAIL_SEARCH_FIELD) {
          return;
        }

        if (declared.includes(chosen)) {
          return;
        }

        this._searchField.set(USERNAME_SEARCH_FIELD);
      });
    });

    effect(() => {
      const awaited: number = this.awaitedRemovalId();
      const settled: UserMutation | null = this.store.mutation();

      if (awaited === 0 || settled === null || settled.id !== awaited) {
        return;
      }

      untracked(() => {
        this.awaitedRemovalId.set(0);

        // A successful removal re-reads the listing, and that read has its own failure path. The
        // operation is asserted so a failed re-read cannot be reported as a failed removal.
        const failure: UserFailure | null =
          settled.failure !== null && settled.operation === 'deleteUser' ? settled.failure : null;

        this.reportRemovalOutcome(failure);
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
   * whole body is rebuilt to display records that never changed. `userId` is unique by definition, being
   * the record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly userRowKey = (row: UserListItem): number => row.userId;

  /**
   * The grid column the listing is ordered by, or `null` for the endpoint's default ordering.
   *
   * Projected from the STORE and translated back into the grid's vocabulary, so the heading marked active and
   * the field the request actually carried can never disagree - the failure a locally-held copy invites.
   */
  protected readonly sortBy: Signal<string | null> = computed(() =>
    columnKeyForSortField(this.store.sortField()),
  );

  /** The direction the listing is ordered in, or `null`. Projected from the store for the same reason. */
  protected readonly sortDir: Signal<SortDirection | null> = computed(
    () => this.store.sortDirection() ?? null,
  );

  /**
   * Re-orders the listing on the heading that was activated.
   *
   * The ordering goes into the ADDRESS and nowhere else. The subscription watching the query parameters is
   * the single thing that stages state and issues the read, so writing the address is the whole of the
   * change; touching the store as well would stage the ordering twice and read twice.
   *
   * ⚠ THE SEARCH IS RE-STATED RATHER THAN LEFT TO `merge`, and on this screen that matters more than on the
   * others. A bare address here means "nothing was asked for", a mode that deliberately issues no request
   * and holds no rows - so an ordering written on its own would name a column of a result set that does not
   * exist. Re-stating the search in force keeps the two coordinates one fact, exactly as every other
   * affordance on this screen does.
   *
   * The page is cleared alongside it: which page an account falls on depends on the ordering.
   *
   * @param change The heading that was activated and the direction to apply. The shared grid owns the
   * direction - it toggles on the active column and starts ascending on any other.
   */
  protected onSortChange(change: DataTableSortChange): void {
    // A KEY WITH NO MAPPING IS REFUSED RATHER THAN TRANSMITTED. Only the mapped columns declare
    // `sortable`, so this is unreachable through the rendered grid; it exists so a later column added
    // without its entry in SORTABLE_COLUMNS fails silently at the boundary instead of asking the server
    // to order by a field it does not accept.
    if (SORTABLE_COLUMNS[change.key] === undefined) {
      return;
    }

    // A NULL DIRECTION CLEARS THE ORDERING RATHER THAN DEFAULTING IT. The shared grid’s cycle has a
    // third step which asks for no ordering at all, and that is the state this screen arrives in - so
    // the KEY leaves the address alongside the direction. An address carrying a key with no direction,
    // or a direction with no key, would be a different question asked of the server.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        ...searchParameters(this.store.search(), this._searchField()),
        [SORT_BY_PARAM]: change.direction === null ? null : change.key,
        [SORT_DIR_PARAM]: change.direction,
        [PAGE_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  // LIFECYCLE
  // -------------------------------------------------------------------------

  /**
   * Brings the screen up.
   *
   * ONE INITIALISATION COMMAND, and the sequencing behind it is deliberately not re-derived here.
   * The store reads the tenant's account policy, learns the page size that policy declares and
   * only then reads the first page, because the listing cannot be requested correctly until the
   * size is in hand — `Users.ascx.vb` L114-L119 read that size from a tenant setting, so it was
   * never a constant. Sequencing the two calls in a component would re-derive the same order on
   * every screen that lists accounts, which is exactly the composition Minimal Change Clause item
   * 5 places in the store.
   *
   * The profile declarations are a SEPARATE, independent read and are asked for separately,
   * because they feed the search-type selector rather than the listing. `Page_Load` L579-L582
   * populated that selector from `ProfileController.GetPropertyDefinitionsByPortal`, and the
   * initialisation command deliberately does not include them.
   *
   * MIGRATION: `Display_Mode` DECIDES WHICH VIEW THIS SCREEN OPENS ON, and the store applies it.
   * `Page_Init` L494-L506 chose the opening filter from that setting — the unfiltered listing, the
   * first letter of the alphabet strip, or the bare marker `"None"` — and `BindData` L248-L290
   * then branched on the filter, with `"None"` matching no branch so that no query was issued at
   * all. `UserModuleBase.vb` L126-L130 defaulted the setting to that third mode, so a tenant that
   * has configured nothing opens with no rows until the operator presses a letter or searches.
   * All three modes are reproduced in the store, which owns the choice because it owns both the
   * policy read and the listing read and must sequence them; deciding it here would fire a second
   * request. This screen's part is to present the no-query state honestly rather than as a match
   * set that came back empty — see {@link noQueryIssued}.
   *
   * The command columns are assembled here rather than in a field initialiser because their
   * content comes from static view queries, which are resolved by the time this runs. Loading
   * happens from a lifecycle hook rather than from an effect for the reason given on the
   * constructor.
   */
  ngOnInit(): void {
    this.commandColumns.set(this.buildCommandColumns());
    // ⚠ BEFORE anything is asked for. This screen's free-text box and axis control are component
    // state and are rebuilt EMPTY on every mount, while the store outlives the screen and kept the
    // previous search. Measured: filter, open an account, come back — the controls claimed no
    // filter, the strip showed no letter applied, and the store re-issued the retained search
    // anyway, so the grid read "Nothing to Display" with nothing on screen explaining why. Clearing
    // the criteria here makes a fresh arrival genuinely fresh, so every control on it is telling the
    // truth about the rows beside it. It clears rather than re-queries, which lets the opening path
    // below decide what to ask for exactly as it does on a first visit — the tenant's display-mode
    // policy, not a remembered filter. Measured on a tenant whose membership settings are
    // unavailable: the return arrival now shows the full listing with an empty box and no letter
    // applied, identical in every respect to the session's very first arrival.
    this.store.resetSearchCriteria();
    // ⚠ THE ADDRESS IS SUBSCRIBED BEFORE THE POLICY IS READ, AND THE ORDER IS THE WHOLE DESIGN. Subscribing
    // emits synchronously, so the address's search is STAGED before `initialise` dispatches; the policy read
    // then finishes, sees a search already chosen, and honours it instead of choosing an opening view of its
    // own. That yields exactly ONE listing read at the address's coordinates. Reversed, the policy would pick
    // a view, dispatch for it, and the address would then dispatch again over the top - two reads and a
    // visible flicker between two different result sets.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: UserListAddressQuery = parseUserListQuery(address);
        // An address that says something unusable is CORRECTED rather than obeyed silently, so that what is
        // on screen and what is in the address never disagree. The correction REPLACES the entry rather than
        // adding one, and returns without reading, because the replacement navigation emits again and that
        // emission does the read.
        if (!addressStatesQuery(address, serialiseUserListQuery(query))) {
          void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: serialiseUserListQuery(query),
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });
          return;
        }
        // The selector is restored too, so an operator returning to a bookmarked search finds the axis it was
        // made on rather than the default. Set directly rather than through the change handler, which exists
        // to read a real control's value.
        this._searchField.set(query.axis);
        // ⚠ AN ADDRESS THAT STATES NO SEARCH ALWAYS MEANS THE NOTHING-ASKED-FOR STATE, on every emission and
        // not merely on arrival. That is what makes a fresh entry start clean - this store is provided at the
        // application root and OUTLIVES this route, so a previous visit's search is still held, and runtime
        // testing measured a fresh sidebar click landing on a filter the operator could not see - and it is
        // equally what makes pressing Back onto the bare address return to the bare view rather than leaving
        // the previous rows and a pressed letter on screen.
        //
        // An earlier revision kept the search in force for a bare address once the screen had opened, to stop
        // a page turn discarding the opening view the tenant's policy had chosen. That is no longer needed and
        // was measurably wrong: `onPageChange` now writes the search in force ALONGSIDE the page, so an
        // affordance never produces a bare address carrying a page, and the only way to reach one is to ask
        // for it.
        this.store.stageSearch(
          query.search,
          query.pageIndex,
          query.sortBy === null ? undefined : SORTABLE_COLUMNS[query.sortBy],
          query.sortDir ?? undefined,
        );
        // ⚠ THE BOX IS RECONCILED FROM THE ADDRESS, so a filter in force is visible and clearable. Runtime
        // testing measured the alternative on the sibling portal listing: a listing narrowed to 231 of 250
        // rows while the search box read empty and every letter reported itself unpressed, leaving the pager
        // string as the only clue that anything was hidden.
        this.searchBox()?.cancelPendingSearch(this.searchTermFor(query.search));
        if (!this.hasOpened) {
          // The opening read belongs to the policy chain `initialise` starts below, which honours the search
          // staged just now instead of choosing its own opening view.
          this.hasOpened = true;
          return;
        }
        this.store.loadUsers();
      });
    this.store.initialise();
    this.store.loadProfileDefinitions();
  }

  /**
   * The text a search should show in the box, which is empty for the modes that carry none.
   *
   * @param search The search in force.
   * @returns The term to display.
   */
  private searchTermFor(search: UserSearch): string {
    switch (search.mode) {
      case 'none':
      case 'all':
        return '';
      default:
        return search.text;
    }
  }

  /**
   * Whether the removal command may be offered for one account.
   *
   * MIGRATION: this is `Website/admin/Users/Users.ascx.vb` L693-L694 reproduced member for
   * member. The legacy grid hid its delete image on exactly two conditions, joined with
   * `AndAlso`:
   *
   * ```vb
   * delImage.Visible = Not (user.UserID = PortalSettings.AdministratorId) AndAlso _
   *                    Not (user.UserID = Me.UserId And user.IsSuperUser)
   * ```
   *
   * The first protects the tenant's DESIGNATED ADMINISTRATOR: removing that account would leave
   * `Portals.AdministratorId` naming an account that no longer exists. The second stops a
   * signed-in HOST account deleting ITSELF — and both halves of that clause are load-bearing,
   * because one host account may legitimately remove another, and an ordinary account removing
   * itself was never guarded here.
   *
   * ⚠ SERVER REFUSAL IS NOT EQUIVALENT BEHAVIOUR, which is why this is reproduced rather than
   * delegated. Offering a destructive command that will be refused invites the operator to
   * confirm a deletion, waits, and then reports a failure for something the screen already knew
   * was impossible — on the two accounts where a mistaken attempt is most alarming.
   *
   * ⚠ EVERY COMPARISON IS EXPLICIT EQUALITY AGAINST A RESOLVED KEY. `administratorUserId` reads
   * `null` both for a tenant with no designation and for an unresolved read, and a truthiness
   * test would treat a legitimate key on either side as absence. Nothing coalesces to `-1`.
   *
   * ⚠ AN UNRESOLVED FACT WITHHOLDS NOTHING, which is the fail-safe direction here. Until the
   * tenant's record has been read `administratorUserId` is `null`, the first clause protects
   * nobody, and behaviour is what it was before this guard existed: the command is offered and
   * the API's refusal governs. Hiding the command until the read completed would instead remove
   * a capability from every row for the duration of a request.
   *
   * @param account The row being rendered.
   * @returns True when the removal command may be shown for this account.
   */
  protected canRemove(account: UserListItem): boolean {
    // ⚠ THE SERVER'S OWN PER-ROW VERDICT COMES FIRST, AND IT IS THE WIDER OF THE TWO RULES.
    // `UserListItemDto.canDelete` is published by the endpoint that enforces the removal, and it
    // withholds EVERY installation administrator rather than only one that is also the caller —
    // which is what a removal request actually refuses. Two predicates existed here for a while,
    // this one and a guard on the command handler, and only the handler consulted the flag: so a
    // row the server would refuse was still RENDERED a destructive command, and the refusal arrived
    // only after the operator had confirmed the deletion and waited. Both rules are honoured in this
    // one place, so the template cannot honour one and forget the other.
    if (!account.canDelete) {
      return false;
    }

    // ⚠ U-M5 — THE DESIGNATED-ADMINISTRATOR COMPARISON USED TO BE REPEATED HERE, AND REMOVING IT IS
    // WHAT TOOK THIS SCREEN FROM FOUR REQUESTS PER LOAD TO THREE. It read the tenant's record purely to
    // learn `Portals.AdministratorId` and compare it against the row — which is EXACTLY the comparison
    // the server has already made: `UserMappings.ToListItem` computes `canDelete` as
    // `!user.IsSuperUser && (portalAdministratorId is not { } designated || designated != user.UserId)`,
    // so the flag consulted above already withholds the designated administrator. The client was issuing
    // a whole extra request, on every visit, to recompute a verdict it was already being handed.
    //
    // It is not merely redundant, it was WEAKER: the flag is published by the endpoint that enforces the
    // removal and withholds every host account as well, while this clause covered one account only. And
    // it was RACY: the tenant read settles independently of the listing, so for the interval before it
    // arrived the clause protected nobody and the command was rendered on a row the server would refuse.
    // Deferring wholly to the flag removes the request, the duplication and the window together.
    const caller: number | undefined = this.auth.currentUser()?.userId;
    const callerIsThisAccount: boolean = caller !== undefined && caller === account.userId;

    return (callerIsThisAccount && account.isSuperUser) === false;
  }

  // -------------------------------------------------------------------------
  // THE SEARCH
  // -------------------------------------------------------------------------

  /**
   * Records the search axis the reader chose.
   *
   * RE-QUERIES NOTHING, and that is parity rather than an omission: `users.ascx` L8 declared
   * `ddlSearchType` with no auto-post-back, so changing the selection had no effect at all until
   * the search button at L9 was pressed. `Users.ascx.vb` L586 then read
   * `ddlSearchType.SelectedItem.Value` at query time.
   *
   * The value is taken from the element and stored verbatim — never trimmed, never case-folded and
   * never checked against a list — because the third search axis is an open set of tenant-declared
   * property names.
   *
   * @param event The change event raised by the selector.
   */
  protected onSearchFieldChange(event: Event): void {
    const target: EventTarget | null = event.target;

    if (target instanceof HTMLSelectElement === false) {
      return;
    }

    this._searchField.set(target.value);
  }

  /**
   * Runs a search for the given text on the chosen axis.
   *
   * MIGRATION: THE MATCH IS A STARTS-WITH AND THE WILDCARD BELONGS TO THE SERVER. `Users.ascx.vb`
   * L269, L271 and L274 each appended a single trailing `%` to whatever had been typed before
   * handing it to the provider, and the target endpoint reproduces that appending. The text is
   * therefore passed RAW: no wildcard is added here, no pattern syntax is introduced, nothing is
   * escaped, nothing is trimmed and nothing is case-folded. Appending a wildcard here would
   * produce a doubled pattern; leading with one would silently turn a starts-with into a contains.
   *
   * MIGRATION: THE BRANCH IS CHOSEN BY A TYPED DISCRIMINATOR, NEVER BY COMPARING TEXT AGAINST A
   * LOCALISED WORD. `Users.ascx.vb` L258, L261 and L264 each compared the search text against a
   * resource lookup, so which query an operator got depended on the language the page had been
   * rendered in — and none of "All", "Online" or "Unauthorized" could be searched for at all,
   * even though each is an ordinary thing to type. L266 compared against the bare marker `"None"`
   * with the same consequence. Here the store's command surface is the discriminator, no reserved
   * word is ever transmitted, and every one of those words is searchable like any other text.
   *
   * @param text The reader's text, raw and exactly as typed.
   */
  protected onSearch(text: string): void {
    this.dispatchSearch(text);
  }

  /**
   * Applies an alphabet-strip affordance.
   *
   * MIGRATION: A LETTER IS NOT A SEPARATE QUERY — it is a prefix search on the axis currently
   * chosen in the selector. `Users.ascx.vb` L586 passed the filter and
   * `ddlSearchType.SelectedItem.Value` into the same `BindData` the search button used, so
   * pressing "A" with the default axis listed accounts whose NAME began with A. The unfiltered
   * affordance is the one exception and asks the server for everything.
   *
   * MIGRATION: EITHER AFFORDANCE RETURNS TO THE FIRST PAGE. `FilterURL` (L446-L456) was called
   * from the strip with a literal page argument of `"1"` (`users.ascx` L16), and L631 shows a new
   * search doing the same before redirecting. The store's search commands return to page index
   * nought for exactly that reason, so the reset is inherited rather than restated — and it is
   * deliberately NOT inherited by removal, which preserves the page.
   *
   * @param affordance A single letter, or the unfiltered affordance's own wording.
   */
  protected onFilterSelected(affordance: string): void {
    // ⚠ THE BOX'S PENDING EMISSION IS CALLED OFF FIRST, AND THE ORDER MATTERS. Both affordances
    // filter the same listing, and the box emits on a delay — so an operator who typed "bl" and then
    // pressed "C" a moment later used to get a query for C, followed by the delay elapsing and a
    // query for "bl": the newer intent silently replaced by the older one, with the strip showing C
    // over a listing of B. Nothing this screen could do prevented it, because the pending emission
    // lived inside the shared control's own stream; it now publishes a command for exactly this.
    //
    // The affordance is ADOPTED into the box as well as cancelling it, for the unfiltered case as
    // much as for a letter: the box then shows what is actually being filtered on rather than a term
    // that is no longer in force, and adopting is emit-free so it cannot re-dispatch what this method
    // is about to dispatch itself.
    if (affordance === ALL_FILTER_LABEL) {
      this.searchBox()?.cancelPendingSearch('');

      // The unfiltered listing is stated EXPLICITLY in the address, because absence means something else
      // here - that nothing has been asked for and the policy should choose - and the two are different
      // queries against the server.
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: {
          [SEARCH_BY_PARAM]: ALL_ACCOUNTS_TOKEN,
          [FILTER_PARAM]: null,
          [PAGE_PARAM]: null,
        },
        queryParamsHandling: 'merge',
      });

      return;
    }

    this.searchBox()?.cancelPendingSearch(affordance);
    this.dispatchSearch(affordance);
  }

  /**
   * The query parameters that carry one row's account to the role listing.
   *
   * ⚠ THE ACCOUNT MUST NOT BE DROPPED, which is what this exists to prevent. The command used to
   * navigate to the bare role listing address, so the row it was pressed on was discarded and the
   * operator arrived at every role in the tenant. The legacy command carried the account —
   * `Users.ascx.vb:L542` built `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD", …)` — and the
   * screen it reached was keyed by either a role or an account.
   *
   * A QUERY PARAMETER rather than a path segment, because the target's route set is closed and
   * contains no per-account membership address. This adds nothing to it. It also matches how the
   * legacy carried the value, which was itself a query argument.
   *
   * The identifier is passed through UNTOUCHED — not coerced, not guarded, not compared against a
   * bound. An account identifier of nought is an ordinary account, so any positivity test here
   * would silently strip the context for exactly one row.
   *
   * A per-row object rather than a single constant, so nothing on this class holds a value that
   * belongs to one row.
   *
   * @param row The account whose roles to show.
   * @returns The parameters to append to the role listing address.
   */
  protected manageRolesQueryParams(row: UserListItem): Record<string, number> {
    return { userId: row.userId };
  }

  /**
   * Whether one strip entry is the one currently applied.
   *
   * ⚠ THIS CLOSES A GAP THIS SCREEN USED TO REPORT RATHER THAN FIX. The template previously
   * carried a note saying the applied entry could not be announced because "the letter in force
   * lives inside the store's search discriminator and is not re-published on the screen's
   * surface", and declined to emit `aria-pressed` rather than invent a state. The right answer
   * was to publish the predicate here, which is what the sibling portal listing already does —
   * so the gap was a missing three lines on this class, not a limit of the store.
   *
   * The strip's two kinds of entry are answered from the same discriminator:
   *
   *   - the clearing entry is applied when the search is the unfiltered listing, which is what
   *     {@link UserListComponent.onFilterSelected} dispatches for it;
   *   - a letter is applied when the search is a sign-in-name prefix whose text is exactly that
   *     letter. Compared case-INSENSITIVELY, because the strip renders upper case while a
   *     caller may have typed the same prefix in lower case through the free-text field and
   *     landed in an identical search — the strip should then show itself as applied rather than
   *     disagreeing with the listing it is describing.
   *
   * Every other search state — an electronic-mail prefix, a profile-property prefix, a
   * multi-character sign-in prefix, or no query at all — leaves EVERY entry unpressed, which is
   * the truthful answer: none of them is what the strip offers.
   *
   * A plain method rather than a computed signal because it takes an argument. The signal it
   * reads registers normally, so the binding re-evaluates whenever the search changes.
   *
   * @param affordance The strip entry to test, as rendered.
   * @returns True when that entry describes the search in force.
   */
  protected isFilterApplied(affordance: string): boolean {
    const search = this.store.search();

    if (affordance === ALL_FILTER_LABEL) {
      return search.mode === 'all';
    }

    return (
      search.mode === 'username' && search.text.toUpperCase() === affordance.toUpperCase()
    );
  }

  // -------------------------------------------------------------------------
  // PAGING
  // -------------------------------------------------------------------------

  /**
   * Moves to another page.
   *
   * The index arrives ZERO-BASED from the shared pager and is passed on UNCHANGED — nothing is
   * added to it, subtracted from it or clamped. The pager emits only a whole index inside the
   * available range, and the store passes the index straight to the transport, so an arithmetic
   * adjustment here would request the wrong page.
   *
   * Offset paging only. There is no cursor, no continuation token and no link relation anywhere
   * in this feature: the envelope carries a total and a page index, which is what the legacy
   * pager consumed.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  protected onPageChange(pageIndex: number): void {
    // A page turn is a PUSHED history entry, not a replaced one: runtime testing found that pressing back
    // from page three was not possible because paging created no entry at all, and returning to the page you
    // came from is the ordinary meaning of that button.
    // ⚠ THE SEARCH IS WRITTEN ALONGSIDE THE PAGE, and it is what makes the page meaningful. The tenant's
    // policy may have chosen the opening view, in which case the address still states no search of its own -
    // and a page on a search-less address describes a screen that cannot exist, so it is corrected away.
    // Stating the search in force turns the address into a complete description of what is on screen, which
    // is the whole point of keeping it there.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        ...searchParameters(this.store.search(), this._searchField()),
        [PAGE_PARAM]: firstPageParameter(pageIndex),
      },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Re-reads the tenant's policy, its profile declarations and the listing.
   *
   * Offered beside a failed read so that a transient fault does not require a navigation. The
   * policy is re-read as well as the listing, because a listing read at the fallback page size
   * beside an unreadable policy is exactly the state this recovers from.
   */
  protected reload(): void {
    this.store.clearFailure();
    this.store.initialise();
    this.store.loadProfileDefinitions();
  }

  // -------------------------------------------------------------------------
  // REMOVAL
  // -------------------------------------------------------------------------

  /**
   * Asks for confirmation before removing an account.
   *
   * MIGRATION: THE CONFIRMATION IS A REAL DIALOGUE. `Page_Init` L522-L524 attached the confirmation
   * as a JavaScript string on the command column — `imageColumn.OnClickJS =
   * Localization.GetString("DeleteItem")` — which the framework emitted as a browser confirmation
   * prompt. The shared dialogue replaces it, with a focus trap, escape handling and an accessible
   * name that the prompt had none of. The wording is the legacy wording, resolved through the
   * three-level fall-through to the shared global resources.
   *
   * ⚠ THE COMMAND IS WITHHELD FROM A PROTECTED ROW, and the row itself says which rows those are.
   * `grdUsers_ItemDataBound` L681-L705 hid the command when the account was the tenant's designated
   * administrator, and when it was both the signed-in caller's own account and an installation
   * administrator. Neither fact was reachable from this feature — the tenant's administrator
   * identifier is not on the account row and the caller's own identifier lives in the authentication
   * store, which a feature may not import — so the capability is now published ON THE ROW by the
   * server that enforces it, as {@link UserListItem.canDelete}. The predicate is the server's rule
   * rather than the legacy markup's: every installation administrator is withheld, not only one that
   * is also the caller, because that is what a removal request actually refuses. The widening is
   * recorded in MIGRATION_NOTES.md.
   *
   * The flag is ADVISORY and this method still guards on it, because a template is not a security
   * boundary: the server re-checks and answers `403` regardless of what was rendered. Guarding here
   * is what stops a stale row — one read before an administrator was designated — from dispatching a
   * request that can only fail.
   *
   * The legacy authorisation check is deliberately NOT re-implemented. `UserModuleBase.vb` L466-L505
   * performed one inside a property getter, complete with a database round trip at L481 and a
   * redirect as a side effect at L494. Authorisation is the server's, and it answers with a status.
   *
   * @param account The account the reader asked to remove.
   */
  protected requestRemoval(account: UserListItem): void {
    if (!account.canDelete) {
      return;
    }

    this._pendingRemoval.set(account);
  }

  /**
   * Removes the confirmed account.
   *
   * MIGRATION: THE CURRENT PAGE IS PRESERVED, and the asymmetry with search is deliberate.
   * `grdUsers_DeleteCommand` L646-L669 re-bound the grid after removing an account and never
   * touched `CurrentPage`, whereas both the search button (L631) and the alphabet strip
   * (`users.ascx` L16, via `FilterURL`) reset it to the first page. The store's removal command
   * re-reads the page in hand without returning to the first, so the asymmetry is inherited exactly.
   *
   * The listing is RE-READ rather than edited locally, because the response carries no body and
   * splicing the row out here would additionally require adjusting a total that the server owns.
   *
   * Guarded against a second dispatch while a write is in flight: the legacy screen post-backed, so
   * the reader could not press twice, and the shared dialogue is removed from the document the
   * moment this runs.
   */
  protected onRemovalConfirmed(): void {
    const account: UserListItem | null = this._pendingRemoval();

    if (account === null) {
      return;
    }

    this._pendingRemoval.set(null);

    // ⚠ GUARDED ON THIS SCREEN'S OWN OUTSTANDING REMOVAL, NOT ON THE STORE BEING BUSY. Guarding on
    // the aggregate refused a legitimate removal whenever any unrelated screen happened to be
    // writing, which is a refusal the operator can neither see nor explain.
    if (this.awaitedRemovalId() !== 0) {
      return;
    }

    this.awaitedRemovalId.set(this.store.deleteUser(account.userId));
  }

  /**
   * Abandons a removal.
   *
   * Nothing is dispatched and no message is emitted: the legacy browser prompt's cancel branch
   * suppressed the post-back and reported nothing either.
   */
  protected onRemovalCancelled(): void {
    this._pendingRemoval.set(null);
  }

  // -------------------------------------------------------------------------
  // PRIVATE
  // -------------------------------------------------------------------------

  /**
   * Assembles the three command columns.
   *
   * THREE SEPARATE COLUMNS rather than one column of three controls, because that is what the
   * legacy grid declared: `users.ascx` L32, L33 and L34 are three distinct
   * `dnn:imagecommandcolumn` elements. Each key is unique within the set and distinct from every
   * display label, because the shared table uses the key as the tracking expression of both its
   * heading loop and its cell loop and cannot detect a collision from a type alone.
   *
   * Every heading is HIDDEN VISUALLY BUT KEPT IN THE ACCESSIBILITY TREE. The legacy account listing
   * supplied no heading text for any of its three command columns, so painting one would be an
   * addition; keeping the label announced means a command cell is still read out with its column
   * name, which closes a real gap at no visual cost. All three legacy commands were unlabelled
   * images.
   *
   * The width is an intrinsic measure, which the shared table's own guidance names as the correct
   * choice for a column of row commands: a proportional track could be narrower than the controls
   * it carries.
   *
   * MIGRATION: the labels are keyed by the legacy COMMAND NAME, not by anything visible.
   * `Page_Init` L549-L551 assigned `imageColumn.Text = Localization.GetString(imageColumn.CommandName,
   * LocalResourceFile)`, so `Edit`, `Delete` and `UserRoles` are resource KEYS — which is why the
   * third command reads "Manage Roles" and not "User Roles".
   *
   * MIGRATION: the roles command is offered unconditionally. `Page_Init` L536-L537 hid it while the
   * screen was being shown from the installation-wide menu, a hosting distinction that has no
   * counterpart in the target: there is one account listing, scoped to the tenant the request
   * resolves to, and no host-level administration is in scope.
   *
   * MIGRATION: nine legacy raster assets are replaced by text, and this feature references no image
   * at all — `edit.gif`, `delete.gif`, `icon_securityroles_16px.gif`, `userOnline.gif`,
   * `checked.gif`, `unchecked.gif` and `icon_search_16px.gif` among them.
   *
   * @returns The command columns, in the legacy order.
   * @throws Error when the template file has not declared one of the three references.
   */
  private buildCommandColumns(): readonly DataTableColumn<UserListItem>[] {
    return [
      {
        key: 'editCommand',
        label: EDIT_COMMAND_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'start',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.editCommandTemplate, 'editCommand'),
      },
      {
        key: 'deleteCommand',
        label: DELETE_COMMAND_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'start',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.deleteCommandTemplate, 'deleteCommand'),
      },
      {
        key: 'manageRolesCommand',
        label: MANAGE_ROLES_COMMAND_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'start',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.manageRolesCommandTemplate, 'manageRolesCommand'),
      },
    ];
  }

  /**
   * Dispatches a prefix search on the axis currently chosen.
   *
   * Reproduces the legacy `Select Case SearchField` (`Users.ascx.vb` L267-L276) branch for branch
   * AND IN THE LEGACY ORDER: the address axis was tested first, then the account name, then
   * everything else fell through to a profile-property search. The order is preserved because it
   * decides the winner when a tenant declares a profile property whose name collides with one of the
   * two account fields.
   *
   * Each store command returns the listing to its first page, which is the reset the legacy screen
   * performed at L631 and through the alphabet strip's page argument.
   *
   * @param text The reader's text, raw and exactly as typed.
   */
  /**
   * Whether one row's postal address is absent, so the cell paints the mark instead — U-M2.
   *
   * A method rather than a precomputed index, deliberately, and this is the one place on this class
   * where that is the right call: it is a single comparison over a value already on the row, with no
   * allocation and no lookup, so the cost the precomputed indexes on this class exist to avoid does
   * not arise. Precomputing it would add a second map keyed by identifier for a Boolean.
   *
   * @param row The account being rendered.
   * @returns True when nothing would be painted.
   */
  protected isAddressAbsent(row: UserListItem): boolean {
    return isProfileValueAbsent(row.address);
  }

  /**
   * Whether one row's telephone number is absent — U-M2. See {@link isAddressAbsent}.
   *
   * @param row The account being rendered.
   * @returns True when nothing would be painted.
   */
  protected isTelephoneAbsent(row: UserListItem): boolean {
    return isProfileValueAbsent(row.telephone);
  }

  /**
   * Fills {@link FILTER_DISCLOSURE_TEMPLATE}.
   *
   * A method rather than a template expression so the substitution happens in one place and the
   * wording stays a single constant. Both substitutions are literal replacements of a distinct
   * token; neither value can introduce the other's token because the field wording comes from a
   * closed set or from a tenant-declared property name, and the term is placed last.
   *
   * @param field The axis in words.
   * @param text The term exactly as it is being matched.
   * @returns The disclosure sentence.
   */
  private composeFilterDisclosure(field: string, text: string): string {
    return FILTER_DISCLOSURE_TEMPLATE.replace('{field}', field).replace('{text}', text);
  }

  private dispatchSearch(text: string): void {
    // ⚠ THE ADDRESS IS WRITTEN AND THE STORE IS NOT TOUCHED. The subscription in `ngOnInit` applies the search
    // and issues the read, so writing the address is the whole of the change: the navigation emits, the
    // emission applies, and the operator's browser history records that they searched. Calling the store as
    // well would apply it twice and read twice.
    //
    // The axis is carried from the selector rather than being written by it, which is what preserves the
    // legacy behaviour: the legacy selector had no auto-post-back (`Users.ascx.vb` L205-L218 builds it with
    // none), so changing it re-queries nothing until a search is actually run. It reaches the address here, on
    // the search that uses it, and not on the change that chose it.
    //
    // The page is cleared alongside: a different search yields a different result set in which the page the
    // operator was on has no counterpart. `FilterURL` (L446-L456) was called from the strip with a literal
    // page argument of "1" and L631 shows a new search doing the same, so the reset is the legacy behaviour
    // and it now lives in the address, where it survives a reload with the search it belongs to.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        ...searchParameters(this.searchFor(text), this._searchField()),
        [PAGE_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * The search the chosen axis makes of some text.
   *
   * Reproduces the legacy switch (`Users.ascx.vb` L268-L274): the two account fields are matched by name and
   * anything else is a profile property, which is what keeps the third axis an open set.
   *
   * @param text The caller's text, raw and exactly as typed.
   * @returns The search to state in the address.
   */
  private searchFor(text: string): UserSearch {
    const field: string = this._searchField();

    if (field === EMAIL_SEARCH_FIELD) {
      return { mode: 'email', text };
    }

    if (field === USERNAME_SEARCH_FIELD) {
      return { mode: 'username', text };
    }

    return { mode: 'profileProperty', propertyName: field, text };
  }

  /**
   * Reports the outcome of a settled removal.
   *
   * The wording is the legacy wording in both branches: `Users.ascx.vb` L655 reported success with
   * the local `UserDeleted` value at the success severity, and L657 reported failure with
   * `UserDeleteError`, which the local resource file does not carry and which therefore falls
   * through to the shared global resources.
   *
   * MIGRATION: THE SEVERITY IS DELEGATED, WHICH IS HOW A REFUSAL BECOMES A WARNING RATHER THAN AN
   * ERROR. The legacy vocabulary had three levels and a refusal used the WARNING one — the
   * access-denied page performs no permission check at all and merely presents a denial, and both
   * branches of its load handler render at the warning level. The shared summariser the store
   * already applies resolves a refusal to that severity, so this reports at the severity it is
   * given rather than deciding a second time; encoding the rule again here is exactly what would let
   * the two disagree.
   *
   * The server's own message is appended behind the legacy wording rather than replacing it, so the
   * operator sees the sentence they used to see AND the detail the server supplied. Both are plain
   * text: nothing is composed into markup, and the support reference is passed as its own argument
   * rather than concatenated, so truncation can never reach it.
   *
   * @param failure The failure the store recorded, or null when the removal succeeded.
   */
  private reportRemovalOutcome(failure: UserFailure | null): void {
    if (failure === null) {
      this.notifications.success(USER_DELETED_MESSAGE);

      return;
    }

    // A successful removal triggers a re-read, so a failure recorded against a READ belongs to that
    // re-read and not to the removal. The removal itself succeeded.
    if (failure.operation !== 'deleteUser') {
      this.notifications.success(USER_DELETED_MESSAGE);

      return;
    }

    const detail: string = failure.summary.message.trim();
    const message: string =
      detail.length === 0
        ? USER_DELETE_ERROR_MESSAGE
        : `${USER_DELETE_ERROR_MESSAGE} ${detail}`;

    this.notifications.notify(failure.summary.severity, message, failure.summary.supportReference);
  }

  /**
   * Returns a captured template, or fails with a message naming what is missing.
   *
   * Failing loudly is the right outcome here and is the pattern the sibling listing screens already
   * follow. A missing declaration is a defect in the template file, not a run-time condition to
   * degrade around, and the alternatives are all worse: omitting the column would silently remove a
   * command an operator needs, and substituting text would silently drop the formatting the legacy
   * cell applied. There is no logging channel in this component to report it through, and a thrown
   * error is the only form a specification can assert on.
   *
   * @param captured The template the view query resolved, or undefined when the reference is absent.
   * @param reference The reference name, quoted back in the message.
   * @returns The captured template.
   * @throws Error when the reference has not been declared.
   */
  private requireTemplate(
    captured: TemplateRef<DataTableCellContext<UserListItem>> | undefined,
    reference: string,
  ): TemplateRef<DataTableCellContext<UserListItem>> {
    if (captured === undefined) {
      throw new Error(
        `user-list.component.html must declare an ng-template named "#${reference}" at the ` +
          'top level of the template, outside any control-flow block.',
      );
    }

    return captured;
  }
}
