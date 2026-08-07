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
  TemplateRef,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
  untracked,
  type OnInit,
  type Signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
import { HasPermissionDirective } from '../../../shared/directives/has-permission.directive';
import { DateDisplayPipe } from '../../../shared/pipes/date-display.pipe';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';

import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { MembershipSettings, UserListItem } from '../../../core/models/user.model';
import type { UserFailure } from '../../../core/state/user.store';
import type {
  DataTableCellContext,
  DataTableColumn,
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

/** Placeholder for the free-text search control. Authored: the legacy textbox had none. */
const SEARCH_PLACEHOLDER = 'Search accounts';

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

/** Identifier for the search-type selector, so the shared form field can name it. */
const SEARCH_FIELD_CONTROL_ID = 'user-list-search-field';

/** The character `HtmlUtils.FormatEmail` tested for at `HtmlUtils.vb` L94. */
const MAILBOX_SEPARATOR = '@';

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
 * Decides the electronic-mail cell for one address.
 *
 * Reproduces `HtmlUtils.FormatEmail` (`HtmlUtils.vb` L89-L102) branch for branch: a blank or
 * whitespace-only value yields nothing at all; a value carrying the mailbox separator yields
 * a linked address; anything else yields the value unchanged and unlinked. The blank test is
 * made on a TRIMMED COPY while the value displayed and linked is the original, exactly as the
 * legacy helper did — it tested `String.IsNullOrEmpty(Email.Trim)` and then concatenated the
 * untrimmed `Email`.
 *
 * The target is assembled here rather than in the template so that no binding concatenates a
 * URL, and it is left UNENCODED because the legacy helper did not encode either; the
 * framework's URL sanitiser inspects it at the binding site.
 *
 * @param value The stored address. Non-nullable on the row contract, because the column is
 * declared not-null with an empty-string default.
 * @returns The text to show and the link target, or a null target when no link is warranted.
 */
function toEmailCell(value: string): UserEmailCell {
  if (value.trim().length === 0) {
    return { text: '', mailto: null };
  }

  if (value.includes(MAILBOX_SEPARATOR)) {
    return { text: value, mailto: `${MAILTO_SCHEME}${value}` };
  }

  return { text: value, mailto: null };
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
 * MIGRATION: NO COLUMN OFFERS SORTING. `users.ascx` L22-L23 declares no `AllowSorting` and the
 * code-behind has no sort handler, so the legacy grid could not be reordered. Offering sorting
 * would be an enhancement rather than a port, so no column below sets `sortable` and the
 * table's ordering output is not handled. The ordering the server chooses applies.
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
    // Gates the mutating affordances on the caller's edit grant. AN AFFORDANCE ONLY: it fails
    // closed on an absent grant and never substitutes for server authorisation, which answers
    // 403 and is the only authority.
    HasPermissionDirective,
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

  /** Carries the transient outcome of a reader-initiated removal. */
  private readonly notifications = inject(NotificationService);

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

  /** Whether a removal has been dispatched and its outcome not yet reported. */
  private readonly awaitingRemoval = signal<boolean>(false);

  /** The chosen search axis, for the selector to mark its current option. */
  protected readonly searchField = this._searchField.asReadonly();

  /**
   * The account awaiting removal confirmation, or null when none is.
   *
   * Read by the template to decide whether to render the confirmation at all: the shared
   * dialogue has no visibility input, so its PRESENCE in the document is what "open" means, and
   * its removal from the document is what closing it means.
   */
  protected readonly pendingRemoval = this._pendingRemoval.asReadonly();

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
   */
  protected readonly searchFieldOptions: Signal<readonly UserSearchFieldOption[]> = computed(
    () => {
      const options: UserSearchFieldOption[] = [
        { value: USERNAME_SEARCH_FIELD, label: resolveSearchFieldLabel(USERNAME_SEARCH_FIELD) },
        { value: EMAIL_SEARCH_FIELD, label: resolveSearchFieldLabel(EMAIL_SEARCH_FIELD) },
      ];

      for (const propertyName of this.store.profilePropertyNames()) {
        options.push({ value: propertyName, label: resolveSearchFieldLabel(propertyName) });
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
      label: USERNAME_HEADING,
      headerAlign: 'center',
      bodyAlign: 'start',
      field: 'username',
    });

    // `users.ascx` L41. Hidden by DEFAULT — see LEGACY_DEFAULT_COLUMN_VISIBILITY.
    if (visible.firstName === true) {
      set.push({
        key: 'firstName',
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
        value: (row: UserListItem): string => plainProfileText(row.address),
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
        value: (row: UserListItem): string => plainProfileText(row.telephone),
      });
    }

    // `users.ascx` L56-L61. A template column, because the cell carries a link rather than text;
    // {@link emailCells} decides the link and the text together.
    if (visible.email === true) {
      set.push({
        key: 'email',
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
   * Confined to the three read operations on purpose. A failed WRITE is reported through the
   * notification service instead — see {@link reportRemovalOutcome} — because the legacy screen
   * reported a failed removal as a transient module message rather than as a permanent surface,
   * and because a refusal must be presented at WARNING severity rather than as an error, which
   * the notification path and the shared summariser between them already arrange.
   */
  protected readonly readFailure: Signal<ProblemDetails | null> = computed(() => {
    const held: UserFailure | null = this.store.failure();

    if (held === null) {
      return null;
    }

    if (
      held.operation !== 'loadUsers' &&
      held.operation !== 'loadMembershipSettings' &&
      held.operation !== 'loadProfileDefinitions'
    ) {
      return null;
    }

    return held.problem;
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
   * immediately unless a removal is outstanding and the write has settled, and the first thing it
   * does once both hold is clear the marker, so a later change to any signal it reads cannot
   * report the same outcome twice. Clearing the marker inside `untracked` keeps that write out of
   * the effect's own dependency set, which is what stops it re-triggering itself.
   *
   * The operation is matched as well as the presence of a failure, because a SUCCESSFUL removal
   * triggers a re-read of the listing whose own failure must not be reported as a failed removal.
   */
  constructor() {
    effect(() => {
      const outstanding: boolean = this.awaitingRemoval();
      const inFlight: boolean = this.store.saving();
      const failure: UserFailure | null = this.store.failure();

      if (outstanding === false || inFlight === true) {
        return;
      }

      untracked(() => {
        this.awaitingRemoval.set(false);
        this.reportRemovalOutcome(failure);
      });
    });
  }

  // -------------------------------------------------------------------------
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
   * MIGRATION: `Display_Mode` IS CARRIED BY THE ACCOUNT POLICY BUT IS NOT HONOURED, and the
   * divergence is stated rather than absorbed. `Page_Init` L494-L506 chose the opening view from
   * that setting — the unfiltered listing, the first letter of the alphabet strip, or the
   * bare marker `"None"` — and `UserModuleBase.vb` L126-L130 defaulted it to `DisplayMode.None`,
   * so a tenant that had configured nothing opened the screen with NO QUERY ISSUED and NO ROWS at
   * all until the operator acted. The store deliberately promotes that no-query state to the
   * unfiltered listing when it brings a listing screen up, documents that choice at length, and
   * owns the decision; overriding it from here would either fire a second request or reintroduce
   * a screen that opens empty. The no-query state remains reachable through the store's own
   * reset.
   *
   * The command columns are assembled here rather than in a field initialiser because their
   * content comes from static view queries, which are resolved by the time this runs. Loading
   * happens from a lifecycle hook rather than from an effect for the reason given on the
   * constructor.
   */
  ngOnInit(): void {
    this.commandColumns.set(this.buildCommandColumns());
    this.store.initialise();
    this.store.loadProfileDefinitions();
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
    if (affordance === ALL_FILTER_LABEL) {
      this.store.showAllAccounts();

      return;
    }

    this.dispatchSearch(affordance);
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
    this.store.goToPage(pageIndex);
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
   * ⚠ THE COMMAND IS OFFERED FOR EVERY ROW, AND THAT IS A KNOWN PARITY GAP RATHER THAN A CHOICE.
   * `grdUsers_ItemDataBound` L681-L705 hid the command when the account was the tenant's designated
   * administrator, and when it was BOTH the signed-in caller's own account AND an installation
   * administrator. Neither fact is reachable from here: the row contract carries the
   * installation-administrator flag, but the tenant's administrator identifier and the signed-in
   * caller's own identifier live in the authentication store, and a feature may not import another
   * feature's state. The gap is reported rather than worked around, and it is an AFFORDANCE gap
   * only — the server is authoritative and refuses a removal it will not perform.
   *
   * The legacy authorisation check is deliberately NOT re-implemented. `UserModuleBase.vb` L466-L505
   * performed one inside a property getter, complete with a database round trip at L481 and a
   * redirect as a side effect at L494. Authorisation is the server's, and it answers with a status.
   *
   * @param account The account the reader asked to remove.
   */
  protected requestRemoval(account: UserListItem): void {
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

    if (this.store.saving() === true) {
      return;
    }

    this.awaitingRemoval.set(true);
    this.store.deleteUser(account.userId);
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
  private dispatchSearch(text: string): void {
    const field: string = this._searchField();

    if (field === EMAIL_SEARCH_FIELD) {
      this.store.searchByEmail(text);

      return;
    }

    if (field === USERNAME_SEARCH_FIELD) {
      this.store.searchByUsername(text);

      return;
    }

    this.store.searchByProfileProperty(field, text);
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
