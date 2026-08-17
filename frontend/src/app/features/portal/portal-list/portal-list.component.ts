// And, symmetrically, the things it must NOT do, each of which has an owner:
// The portal (tenant) listing screen of the dnn-migration administration front end, served at `/portals`.

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PORTAL_LIST_ROUTE } from '../../../core/config/app-routes.config';
import { ListReturnStore } from '../../../core/state/list-return.store';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { problemDetailsMessage } from '../../../core/models/problem-details.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { AbsentValueComponent } from '../../../shared/components/absent-value/absent-value.component';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { RovingFocusDirective } from '../../../shared/directives/roving-focus.directive';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
import { DateDisplayPipe, parseDisplayInstant } from '../../../shared/pipes/date-display.pipe';

import {
  addressStatesQuery,
  FILTER_PARAM,
  FIRST_PAGE_INDEX,
  firstPageParameter,
  PAGE_PARAM,
  PAGE_SIZE_PARAM,
  parsePageIndex,
  parsePageSize,
  parseSortDirection,
  parseSortKey,
  SORT_BY_PARAM,
  SORT_DIR_PARAM,
} from '../../../core/utils/list-query.util';

import type { OnInit, Signal, TemplateRef } from '@angular/core';
import type { ParamMap, Params } from '@angular/router';
import type { SortDirection } from '../../../core/models/paged-result.model';
import type { PortalListItem } from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { PortalFailure, PortalListQuery } from '../../../core/state/portal.store';
import type {
  DataTableCellContext,
  DataTableColumn,
  DataTableSortChange,
} from '../../../shared/components/data-table/data-table.component';

// Wording - local resource file
// Each constant names the resource key it carries, so a reader can check the value against
// `Website/admin/Portal/App_LocalResources/Portals.ascx.resx` without leaving this file.

/** `ControlTitle_.Text`. */
const PAGE_TITLE = 'Portals';

/** `PortalId.Header`. */
const PORTAL_ID_HEADING = 'Portal Id';

/** `Title.Header`. Painted over the portal NAME, which is why the column key differs. */
const TITLE_HEADING = 'Title';

/** `Portal Aliases.Header`. */
const ALIASES_HEADING = 'Portal Aliases';

/** `Users.Header`. */
const USERS_HEADING = 'Users';

/** `Pages.Header`. */
const PAGES_HEADING = 'Pages';

/**
 * Column key of the account tally. Named rather than written inline because the shared cell template that
 * serves BOTH tally columns switches on it: the template is handed the column it is rendering, so one
 * template can answer two columns, and the key is the only thing that tells them apart.
 */
const USERS_COLUMN_KEY = 'users';

/** Column key of the page tally. See {@link USERS_COLUMN_KEY}. */
const PAGES_COLUMN_KEY = 'pages';

/**
 * Column key of the disk allowance. See {@link USERS_COLUMN_KEY}.
 *
 * ⚠ THE MEASURED DEFECT THIS CLOSES. This column was declared as a PLAIN FIELD while the two tallies
 * beside it were template columns, so a portal holding the legacy absent-integer marker painted a literal
 * `-1` in Disk Space and an em dash in Users and Pages - on the SAME ROW. `-1` read as a real,
 * negative allowance, and Rule T7 is explicit that sentinels survive at the boundary and not in the
 * display.
 *
 * Legacy treated all three identically and gave none of them any treatment at all:
 * `Website/admin/Portal/portals.ascx:L44-L46` declares Users, Pages and DiskSpace as bare
 * `dnn:textcolumn` data fields, so the legacy grid printed `-1` in every one of them. The dash marker is
 * therefore a deliberate divergence that was already taken for two of the three columns; this makes the
 * third agree with them rather than introducing a new idea.
 */
const DISK_SPACE_COLUMN_KEY = 'hostSpace';

/** `DiskSpace.Header`. */
const DISK_SPACE_HEADING = 'Disk Space';

/** `HostingFee.Header`. */
const HOSTING_FEE_HEADING = 'Hosting Fee';

/** `Expires.Header`. */
const EXPIRES_HEADING = 'Expires';

/**
 * `Expired.Text`, from THIS screen's own local resource file. the WORDING is this screen's own and the
 * SEMANTICS are the account-services screen's.
 */
const EXPIRED_QUALIFIER = 'Expired';

const EDIT_COMMAND_LABEL = 'Edit this Portal';

/**
 * The word the settings affordance PAINTS, and therefore the word its accessible name must open with.
 * Declared here rather than written into the template so the two cannot drift: the template interpolates
 * this constant and {@link PortalListComponent.editCommandName} composes from it, so a change to the
 * visible word changes both at once and Label in Name cannot silently break.
 */
const EDIT_COMMAND_VISIBLE_LABEL = 'Settings';

/**
 * Describes where a row's TITLE leads, for the record link's tooltip and description. ⚠ MAJOR
 * (reachability) — NET-NEW, because the legacy grid had no such affordance to measure: its
 * `portals.ascx:L26` title column was a plain bound column with no address at all.
 */
const RECORD_LINK_DESCRIPTION = 'Open this portal record';

/**
 * Announced beside a negative hosting fee, and never painted. ⚠ MINOR (money differentiation) — the
 * colour and weight this screen gives a negative fee are a VISUAL cue, and a visual cue alone would carry
 * the meaning by colour only, which WCAG 1.4.1 forbids. This is the same cue in words, hidden from the
 * page and present in the accessibility tree, exactly as the absent-integer mark's description is.
 */
const NEGATIVE_FEE_QUALIFIER = 'negative';

/** `AddContent.Action`. */
const ADD_PORTAL_ACTION = 'Add New Portal';

/** `PortalDeleted.Text`, surfaced at success severity. */
const PORTAL_DELETED_MESSAGE = 'Portal deleted successfully';

/** `Filter.Text`, verbatim and complete. */
const LETTER_FILTER_CSV = 'A,B,C,D,E,F,G,H,I,J,K,L,M,N,O,P,Q,R,S,T,U,V,W,X,Y,Z';

// Wording - global resource file

/** `All.Text`. */
const ALL_FILTER_LABEL = 'All';

/**
 * `DeleteItem.Text`. The row-deletion confirmation, singular. ⚠ NOT RENDERED ALONE ANY MORE. See {@link
 * PortalListComponent.deleteConfirmMessage}: the measured wording is kept verbatim as the question, and
 * the identity of the row being destroyed is appended to it, because this dialog covers the very row the
 * operator was reading.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `LastPortal.Text`. The refusal when an installation would be left with no portal. */
const LAST_PORTAL_MESSAGE = 'You Can Not Delete The Last Portal In Your Database';

// Wording - authored for this screen

/** The grid's accessible name, projected into the shared table's caption slot. authored. */
const GRID_CAPTION = 'Portals, with their host names, account and page counts, and hosting terms';

/** Accessible name for the first-letter filter strip, which is a group and not a landmark. */
const FILTER_STRIP_LABEL = 'Filter portals by first letter';

const SEARCH_PLACEHOLDER = 'Name begins with';

/** Shown when nothing matched at all - a total of nought. */
const NO_PORTALS_MESSAGE = 'No portals match the current filter.';

/** Shown when records exist but the page in hand holds none. */
const PAST_END_MESSAGE = 'This page is past the end of the results. Return to the first page.';

/** Fallback wording when a failure carried no readable problem document. */
const LIST_FAILED_MESSAGE = 'The portals could not be loaded.';

/** Fallback wording when a deletion failed and carried no readable problem document. */
const DELETE_FAILED_MESSAGE = 'The portal could not be deleted.';

// ROUTE SEGMENTS

/** Root segment of the portal feature, as mounted by the application's route table. */
const PORTALS_SEGMENT = '/portals';

/**
 * Child segment the edit affordance targets. the row's edit affordance opens SITE SETTINGS and not a
 * create/edit form, which is the legacy behaviour rather than a design choice.
 */
const SETTINGS_SEGMENT = 'settings';

/** Child segment the page-level add action targets. */
const NEW_SEGMENT = 'new';

// FORMATTING

/** Decimal places for the hosting fee. */
const HOSTING_FEE_FRACTION_DIGITS = 2;

/**
 * The scheme the alias links carry when the stored host name states none. ⚠ MAJOR (CWE-319 cleartext
 * transmission) — HTTPS, AND THE PREVIOUS VALUE WAS `http://`.
 */
const HTTPS_SCHEME_PREFIX = 'https://';

const ABSOLUTE_ADDRESS_MARKERS: readonly string[] = Object.freeze([
  'mailto:',
  '://',
  '~',
  '\\\\',
]);

/** Matches a run of leading break tags, in either spelling and in any case. */
const LEADING_BREAK_TAGS = /^(?:\s*<br\s*\/?>)+\s*/i;

/** The status the server answers when an installation must retain its last portal. */
const HTTP_CONFLICT = 409;

/**
 * The legacy absent-integer marker, whose value is minus one. `Library/Components/Shared/Null.vb:L41`
 * declares `NullInteger` as `-1`, and `Library/Components/Portal/PortalInfo.vb:L61-L62` seeds BOTH tally
 * properties with it.
 */
const ABSENT_INTEGER = -1;


// THE ADDRESS CONTRACT

// ⚠ THE FIVE PARAMETER NAMES AND THE FIVE READERS NOW LIVE IN `core/utils/list-query.util.ts` AND ARE
// SHARED WITH THE OTHER THREE LISTINGS. They were private to this file while it was the only screen that
// kept its query in the address; they were hoisted verbatim, with no change of behaviour, when the account,
// role and module listings adopted the same contract.

/**
 * The five column keys the collection endpoint will order by, in its own spelling. ⚠ THIS SET IS THE
 * ENDPOINT'S, NOT THIS SCREEN'S, AND OFFERING A SIXTH WOULD PRODUCE A REFUSED REQUEST.
 * `SortableFields.Portals` admits exactly these five, matched without regard to case - so the camel-cased
 * column keys below are accepted as they stand - and answers anything else with a field-level `400`
 * naming the five.
 */
const SORTABLE_COLUMN_KEYS: readonly string[] = Object.freeze([
  'portalId',
  'portalName',
  'hostSpace',
  'hostFee',
  'expiryDate',
]);

/**
 * Reads the whole listing query out of an address.
 *
 * @param address The route's query parameters.
 * @returns The query to apply, with every unusable value resolved to its default.
 */
function parseListQuery(address: ParamMap): PortalListQuery {
  const sortBy: string | null = parseSortKey(address.get(SORT_BY_PARAM), SORTABLE_COLUMN_KEYS);

  return {
    pageIndex: parsePageIndex(address.get(PAGE_PARAM)),
    pageSize: parsePageSize(address.get(PAGE_SIZE_PARAM)),
    name: address.get(FILTER_PARAM),
    sortBy,
    // A direction with no field to apply it to is dropped rather than kept, so the address cannot carry an
    // ordering half. A field with no direction is kept: the endpoint has a default.
    sortDir: sortBy === null ? null : parseSortDirection(address.get(SORT_DIR_PARAM)),
  };
}

/**
 * Writes a listing query back out as address parameters. A default coordinate is emitted as `null`, which
 * the router REMOVES from the address rather than writing as an empty value - so an unfiltered first page
 * is the bare path and not a trail of empty parameters.
 *
 * @param query The query in force.
 * @returns The parameters to merge into the address.
 */
function serialiseListQuery(query: PortalListQuery): Params {
  return {
    [PAGE_PARAM]: firstPageParameter(query.pageIndex),
    [PAGE_SIZE_PARAM]: query.pageSize === null ? null : String(query.pageSize),
    [FILTER_PARAM]: query.name,
    [SORT_BY_PARAM]: query.sortBy,
    [SORT_DIR_PARAM]: query.sortBy === null || query.sortDir === null ? null : query.sortDir,
  };
}

/** How a portal's hosting term stands. Exactly THREE states, and the absence of a fourth is deliberate. */
export type PortalExpiryState = 'none' | 'current' | 'expired';

/**
 * @param expiryDate The expiry as it arrived on the wire.
 * @param now The moment to judge against.
 * @returns `none` when there is no usable expiry, `expired` when it has passed, `current` otherwise.
 */
function resolveExpiryState(expiryDate: string | null, now: Date): PortalExpiryState {
  // The pipe's own verdict, so a qualifier can never be painted beside an empty cell and a date can never be
  // painted without one. See `parseDisplayInstant`.
  const instant: Date | null = parseDisplayInstant(expiryDate);
  if (instant === null) {
    return 'none';
  }

  const startOfToday = new Date(0);
  startOfToday.setUTCFullYear(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  startOfToday.setUTCHours(0, 0, 0, 0);

  return instant.getTime() > startOfToday.getTime() ? 'current' : 'expired';
}

/**
 * Paints one tally, answering the legacy absent-integer marker with a mark rather than with the number.
 * the marker is ERASED AT THE DISPLAY LAYER ONLY, which is the same discipline the expiry column already
 * applies and is the discipline Rule T7 asks for - "sentinels survive at the boundary, not in the
 * domain".
 *
 * @param tally The value as it arrived.
 * @returns The mark when the value is the absent-integer marker, otherwise the number as text.
 */
function formatTally(tally: number): string {
  // ⚠ THE MARK ITSELF IS NO LONGER COMPOSED HERE. The absent case is rendered by the shared absent-value
  // component, which owns the mark, its colour and the words behind it for every listing; the cell's template
  // asks `isTallyAbsent` and takes that branch, so this formatter is only ever reached for a real number. The
  // empty string is returned for the marker so that a caller which does reach it cannot paint minus one.
  return tally === ABSENT_INTEGER ? '' : String(tally);
}

// VIEW-MODEL TYPES

/**
 * One entry of the first-letter filter strip. `value` is the text to filter by, or `null` for the entry
 * that clears the filter.
 */
export interface PortalFilterOption {
  /** The text painted on the entry. */
  readonly label: string;

  /** The name filter the entry applies, or `null` to clear it. */
  readonly value: string | null;
}

export interface PortalAliasLink {
  readonly href: string | null;

  /** The host name as stored, rendered as escaped text. */
  readonly label: string;
}

/** The empty alias list, shared so that a portal with no host names allocates nothing. */
const NO_ALIAS_LINKS: readonly PortalAliasLink[] = Object.freeze([]);

// PURE HELPERS

/**
 * Builds the filter strip's entries. the strip holds TWENTY-SEVEN entries where the legacy held
 * twenty-eight, and the ORDER of the twenty-seven is the legacy order rather than a tidied one.
 *
 * @returns The twenty-six letters in resource order, then the clear-filter entry.
 */
function buildFilterOptions(): readonly PortalFilterOption[] {
  const options: PortalFilterOption[] = LETTER_FILTER_CSV.split(',').map(
    (letter: string): PortalFilterOption => ({ label: letter, value: letter }),
  );

  options.push({ label: ALL_FILTER_LABEL, value: null });

  return Object.freeze(options);
}

/** The filter strip, built once at module load because the entries never change. */
const PORTAL_FILTER_OPTIONS: readonly PortalFilterOption[] = buildFilterOptions();

/**
 * @param alias The host name exactly as stored.
 * @returns The address to navigate to.
 */
function toAliasHref(alias: string): string | null {
  // The legacy helper's own outer test, `If strURL <> ""`, kept for fidelity to the function being
  // reproduced. Defence in depth rather than a live path: the one caller below drops an empty host name
  // before it reaches here, so this arm is not reachable through it.
  if (alias.length === 0) {
    return null;
  }

  for (const marker of ABSOLUTE_ADDRESS_MARKERS) {
    if (alias.includes(marker)) {
      return allowedHostAddress(alias);
    }
  }

  return allowedHostAddress(`${HTTPS_SCHEME_PREFIX}${alias}`);
}

/**
 * The candidate address, or `null` when it is not an `http`/`https` address naming a host. An allowlist,
 * not a denylist, and that direction is the whole point.
 *
 * @param candidate The address to admit or refuse.
 * @returns The address when it is one, or null.
 */
function allowedHostAddress(candidate: string): string | null {
  let parsed: URL;

  try {
    parsed = new URL(candidate);
  } catch {
    // Not an absolute address at all. An application-relative path and a network share both land here, as
    // does anything the parser cannot make sense of.
    return null;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return null;
  }

  // The value as STORED is returned, not the parser's normalised serialisation. Normalising would append a
  // trailing slash, lower-case the host and re-encode the path, so the address in the status bar would no
  // longer be the value the operator stored - and this screen exists to report stored state faithfully.
  return candidate;
}

/**
 * Projects a portal's host names into renderable links. an EMPTY host name produces no entry, where the
 * legacy produced an empty anchor.
 *
 * @param aliases The host names as received.
 * @returns One link per non-empty host name, in the order received.
 */
function toAliasLinks(aliases: readonly string[]): readonly PortalAliasLink[] {
  const links: PortalAliasLink[] = [];

  for (const alias of aliases) {
    if (alias.length === 0) {
      continue;
    }

    links.push({ href: toAliasHref(alias), label: alias });
  }

  return links.length === 0 ? NO_ALIAS_LINKS : links;
}

/**
 * @param hostFee The recurring fee as received.
 * @returns The fee with two decimals and no group separator, or an empty cell.
 */
function formatHostingFee(hostFee: number): string {
  if (Number.isFinite(hostFee) === false) {
    return '';
  }

  return hostFee.toFixed(HOSTING_FEE_FRACTION_DIGITS);
}

/**
 * The largest amount every hundredth of which a double represents exactly. `Portals.HostFee` is SQL
 * `money`, a scaled 64-bit integer with four decimal places, and it crosses the wire as a JSON number —
 * so it is read into an IEEE-754 double before any code here runs.
 */
const EXACT_CENTS_BOUND = Number.MAX_SAFE_INTEGER / 100;

/** The word appended to a fee whose painted figure may differ from the stored one. */
const APPROXIMATE_FEE_DESCRIPTION = 'approximate';

/**
 * Whether a hosting fee is large enough that its painted cents may differ from the stored ones.
 *
 * @param hostFee The fee as received.
 * @returns `true` when the painted figure may differ from the stored one.
 */
function isFeeApproximate(hostFee: number): boolean {
  return Number.isFinite(hostFee) && Math.abs(hostFee) >= EXACT_CENTS_BOUND;
}

/** Matches any run of whitespace, including tabs and line breaks. */
const WHITESPACE_RUN = /\s+/g;

/**
 * @param title The stored portal title, exactly as received.
 * @returns The title with whitespace runs collapsed to single spaces and the ends trimmed.
 */
function nameForAnnouncement(title: string): string {
  return title.replace(WHITESPACE_RUN, ' ').trim();
}

/**
 * Whether a hosting fee is below zero. ⚠ MINOR (money differentiation) — a negative fee, a zero fee and a
 * positive fee were painted IDENTICALLY: runtime testing measured `-125.50`, `0.00` and `4321.99` with
 * the same colour, the same weight and the same size, so the only thing distinguishing a loss was a
 * single minus glyph. The negative case alone is marked, and only the negative case.
 *
 * @param hostFee The fee as received.
 * @returns True when the fee is a real number below zero.
 */
function isNegativeFee(hostFee: number): boolean {
  return Number.isFinite(hostFee) && hostFee < 0;
}

/**
 * Removes leading break tags from a message before it is rendered as text. Defensive rather than
 * decorative.
 *
 * @param message The message as composed or received.
 * @returns The message with any leading break tags removed.
 */
function stripLeadingBreakTags(message: string): string {
  return message.replace(LEADING_BREAK_TAGS, '');
}

/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE: exactly one per screen, stating that screen's
 * SCOPE - the record it acts on when the title does not already name it, otherwise what the screen is for
 * in one line - and never a status, a count or a progress readout.
 */
const PAGE_SUBTITLE =
  'The portals hosted by this installation.';

@Component({
  selector: 'app-portal-list',
  standalone: true,
  imports: [
    RouterLink,
    // The page heading plus its projected action bar.
    PageHeaderComponent,
    // The free-text name filter. A shared component rather than a bare input, so the control keeps its label
    // association and its own debounce.
    SearchInputComponent,
    // The ten-column grid.
    DataTableComponent,
    // The pager, fed the same three facts the legacy pager was handed.
    PaginationComponent,
    // The row-deletion confirmation. Its presence in the DOM is what "open" means.
    ConfirmDialogComponent,
    // The ONE rendering of an absent value, shared with every other listing.
    AbsentValueComponent,
    // The structured-failure surface, which carries its own live region.
    ErrorBannerComponent,
    // Renders the expiry column, and is the reason that column needs no formatter here.
    DateDisplayPipe,
    RovingFocusDirective,
  ],
  templateUrl: './portal-list.component.html',
  styleUrl: './portal-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalListComponent implements OnInit {
  // COLLABORATORS

  /** The listing slice, its filter, its paging coordinates and its failures. */
  private readonly store = inject(PortalStore);

  /**
   * The signed-in session, consulted for ONE fact: which tenant is being browsed. That fact has no other
   * home.
   */
  private readonly session = inject(AuthStore);

  /** The queue the delete outcome is announced through. */
  private readonly notifications = inject(NotificationService);

  /** The route, read for the listing coordinates it carries. */
  private readonly route = inject(ActivatedRoute);

  /**
   * This screen's lifetime, so the address subscription ends with it. Needed explicitly because the
   * subscription is opened in `ngOnInit`, which is NOT an injection context - so the no-argument form of
   * the unsubscribe operator is unavailable there and the reference has to be captured here, where it is.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The router, written to when a coordinate changes. ⚠ THE ADDRESS IS THE ONLY WRITE PATH FOR LISTING
   * STATE, AND THAT IS DELIBERATE. Every affordance on this screen - a letter, a search, a page turn, a
   * heading - navigates, and the address change is what reaches the store.
   */
  private readonly router = inject(Router);

  /** Where this listing stands, so a form returning to it lands on the same page and filter. */
  private readonly listReturn = inject(ListReturnStore);

  // CELL TEMPLATES
  // Static queries, so they resolve before `ngOnInit` and the column set can be assembled there. A template
  // the host declares belongs to the host's view whether or not another component ends up rendering it,
  // which is what makes this work.

  @ViewChild('editCommand', { static: true })
  private editCommandTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('deleteCommand', { static: true })
  private deleteCommandTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  // THE TITLE CELL
  // ⚠ MAJOR (reachability) — the title was a BOUND column, which paints a string and can carry no
  // affordance at all, so the portal RECORD screen at `/portals/:portalId` had no inbound link anywhere in
  // the application: it was reachable only by typing its address.
  @ViewChild('portalNameCell', { static: true })
  private portalNameCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('aliasesCell', { static: true })
  private aliasesCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('expiresCell', { static: true })
  private expiresCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('tallyCell', { static: true })
  private tallyCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  // THE HOSTING-FEE CELL
  // ⚠ MINOR (money differentiation) — the column was a FORMATTED column, which paints a string and can
  // carry no per-value treatment at all, so a negative fee could not be told from a positive one.
  @ViewChild('hostFeeCell', { static: true })
  private hostFeeCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  // THE NAME FILTER CONTROL
  // Queried so that the box can be kept SHOWING the filter that is actually in force. Statically, because
  // the control is declared unconditionally in the filter row and is therefore in the view before
  // `ngOnInit` - which is when the first address arrives and the first adoption happens.

  /**
   * The free-text name filter control, so its displayed text can be reconciled with the filter in force.
   * ⚠ THIS QUERY IS WHAT MAKES A CLEAR-SEARCH WORK; WITHOUT IT THE CLEAR REACHES NOTHING. The control is
   * deliberately uncontrolled - it owns its own value and its own debounce, and it suppresses a REPEAT of
   * the term it last emitted so that the immediate submit path and the debounced tail cannot both query.
   */
  @ViewChild(SearchInputComponent, { static: true })
  private nameFilterControl?: SearchInputComponent;

  // State owned by this screen

  /** The column descriptors, assembled once the cell templates have resolved. */
  private readonly columnSet = signal<readonly DataTableColumn<PortalListItem>[]>([]);

  /** The row awaiting a deletion confirmation, or `null` when the dialog is closed. */
  private readonly pendingDeletion = signal<PortalListItem | null>(null);

  /**
   * The row whose deletion is awaiting an outcome, or `null` when nothing is in flight. Separate from
   * {@link pendingDeletion} because the two answer different questions: one decides whether the dialog is
   * attached, the other decides whether an announcement is owed.
   */
  private readonly awaitedDeletion = signal<PortalListItem | null>(null);

  // Store-derived surface.

  /** The rows of the page in hand. */
  protected readonly portals: Signal<readonly PortalListItem[]> = this.store.portals;

  /** Whether a listing request is in flight. */
  protected readonly loading: Signal<boolean> = this.store.listLoading;

  /** Whether a deletion is in flight, which the dialog and row buttons disable against. */
  protected readonly deleting: Signal<boolean> = this.store.detailLoading;

  /** The active name filter, or `null` when unfiltered. */
  protected readonly activeFilter: Signal<string | null> = this.store.nameFilter;

  /**
   * The column key the listing is ordered by, or `null` for the server's own ordering. Bound INTO the
   * shared grid, which never decides its own sort: the grid marks whichever heading matches this key and
   * reports activations back out, so the ordering in force is held in exactly one place - the address,
   * projected through the store - and the grid cannot drift from it.
   */
  protected readonly sortBy: Signal<string | null> = this.store.sortBy;

  /** The direction the ordering is applied in, or `null` for the server's default. */
  protected readonly sortDir: Signal<SortDirection | null> = this.store.sortDir;

  /**
   * The page coordinate the pager paints. The REQUESTED index rather than the served one, which is exact
   * parity: the legacy screen handed its pager `CurrentPage`, the page the operator had asked for, not a
   * value read back from the query.
   */
  protected readonly pageIndex: Signal<number> = this.store.pageIndex;

  protected readonly pageSize: Signal<number> = this.store.servedPageSize;

  protected readonly totalCount: Signal<number> = this.store.totalCount;

  protected readonly pagerRequired: Signal<boolean> = this.store.pagerRequired;

  /** Whether nothing matched at all - a reported total of nought. */
  protected readonly isListEmpty: Signal<boolean> = this.store.isListEmpty;

  /** Whether records exist but the requested page holds none. */
  protected readonly isPastEnd: Signal<boolean> = this.store.isPastEnd;

  /** The column descriptors, exposed read-only so a template cannot replace them. */
  protected readonly columns: Signal<readonly DataTableColumn<PortalListItem>[]> =
    this.columnSet.asReadonly();

  /** The row awaiting confirmation. */
  protected readonly pendingRemoval: Signal<PortalListItem | null> =
    this.pendingDeletion.asReadonly();

  /** Whether the grid has at least one row to draw. */
  protected readonly hasRows: Signal<boolean> = computed<boolean>(
    () => this.portals().length > 0,
  );

  /**
   * The range and total, restated in the page header. ⚠ P-M9 — THE PAGER STAYS WHERE THE LEGACY SCREEN
   * PUT IT AND THE COUNT COMES UP TO MEET THE OPERATOR. The finding is that the pager sits below the fold
   * at 1280x900, measured at y = 908 on page one - eight pixels past the edge, so nothing about paging is
   * visible until the operator scrolls.
   */
  protected readonly resultSummary: Signal<string | undefined> = computed<
    string | undefined
  >(() => {
    if (this.hasRows() === false) {
      return undefined;
    }

    const total = this.totalCount();
    const size = this.pageSize();
    const shownCount = this.portals().length;

    // The first shown ordinal is one-based for the reader, and is derived from the page index and the
    // SERVED size rather than counted, because a page turn commits its index before its rows and counting
    // would report the previous page's range for one frame.
    const first = this.pageIndex() * size + 1;
    const last = first + shownCount - 1;

    return `${first}\u2013${last} of ${total}`;
  });

  /**
   * Whether to show the full-screen indicator instead of the grid. Only while the FIRST page is being
   * read.
   */
  protected readonly showInitialSpinner: Signal<boolean> = computed<boolean>(
    // ⚠ THE UN-ASKED STATE COUNTS AS LOADING, AND LEAVING IT OUT IS WHAT CAUSED THE EMPTY-TABLE FLASH. The
    // read is issued from the address subscription, so between this component mounting and that request
    // going out there is a change-detection pass in which `loading()` is still false and no rows are held -
    // and every screen read that as a genuine zero-result and painted "No records found." for a listing it
    // had not yet asked about. Measured on every post-save return to a listing. `listSettled` is the store's
    // own record of whether a read has ever settled, so an un-asked listing now shows the same indicator as
    // one that is mid-request, which is what it actually is.
    () => (this.loading() || !this.store.listSettled()) && this.portals().length === 0,
  );

  /**
   * The listing failure as an RFC 7807 document, or `null` when the last read succeeded. The document is
   * passed WHOLE to the shared banner, which preserves the trace and correlation identifiers an operator
   * quotes when reporting a fault.
   */
  protected readonly listProblem: Signal<ProblemDetails | null> =
    computed<ProblemDetails | null>(() => {
      const failure: PortalFailure | null = this.store.listFailure();

      return failure === null ? null : failure.problem;
    });

  /** Whether a listing failure is being reported at all, document or not. */
  protected readonly hasListFailure: Signal<boolean> = computed<boolean>(
    () => this.store.listFailure() !== null,
  );

  /**
   * Each row's host names, projected once per page rather than once per redraw. The alias cell has to
   * become real anchor elements, and the projection that turns bare host names into address-and-text
   * pairs allocates.
   */
  private readonly aliasLinkIndex: Signal<ReadonlyMap<number, readonly PortalAliasLink[]>> =
    computed<ReadonlyMap<number, readonly PortalAliasLink[]>>(() => {
      const index = new Map<number, readonly PortalAliasLink[]>();

      for (const portal of this.portals()) {
        index.set(portal.portalId, toAliasLinks(portal.aliases));
      }

      return index;
    });

  /** The wording for a listing failure that carried no readable document. */
  protected readonly listFailureMessage: Signal<string> = computed<string>(() => {
    const failure: PortalFailure | null = this.store.listFailure();

    return failure === null
      ? ''
      : stripLeadingBreakTags(problemDetailsMessage(failure.problem, LIST_FAILED_MESSAGE));
  });

  // Wording, exposed for the template and for specifications

  /** `ControlTitle_.Text`. */
  /** The one-line scope statement shown beneath the title. */
  protected readonly pageSubtitle = PAGE_SUBTITLE;

  protected readonly heading = PAGE_TITLE;

  /** `AddContent.Action`. */
  protected readonly addPortalLabel = ADD_PORTAL_ACTION;

  /** The grid's clipped accessible name. */
  protected readonly gridCaption = GRID_CAPTION;

  /**
   * What the grid's progress indicator says while a read is in flight. Names the collection rather than
   * saying "Loading…", so the announcement identifies WHAT is loading; the same label serves the first-read
   * placeholder and the refetch strip, so this screen has one loading vocabulary.
   */
  protected readonly loadingLabel = 'Loading portals…';

  /** Accessible name for the first-letter filter group. */
  protected readonly filterStripLabel = FILTER_STRIP_LABEL;

  /** Placeholder for the free-text name filter. */
  protected readonly searchPlaceholder = SEARCH_PLACEHOLDER;

  /**
   * The confirmation body: the global `DeleteItem.Text` question, then WHICH record it means. ⚠ THE
   * MEASURED DEFECT. The body was the bare legacy sentence "Are You Sure You Wish To Delete This Item?"
   * and named nothing, while the dialog is a real modal that PHYSICALLY COVERS the table - the row being
   * destroyed included.
   */
  protected readonly deleteConfirmMessage: Signal<string> = computed<string>(() => {
    const target: PortalListItem | null = this.pendingDeletion();

    if (target === null) {
      return DELETE_CONFIRM_MESSAGE;
    }

    return `${DELETE_CONFIRM_MESSAGE} ${target.portalName}`;
  });

  /** Local `Edit.Text`, used as the row link's accessible name. */
  protected readonly editCommandLabel = EDIT_COMMAND_LABEL;

  /** The twenty-seven filter entries, in legacy order. */
  protected readonly filterOptions = PORTAL_FILTER_OPTIONS;

  /** The zero-result wording, chosen from the state that actually holds. */
  protected readonly emptyMessage: Signal<string> = computed<string>(() =>
    this.isPastEnd() ? PAST_END_MESSAGE : NO_PORTALS_MESSAGE,
  );

  /** The route the page-level add action targets. */
  protected readonly addPortalLink: (string | number)[] = [PORTALS_SEGMENT, NEW_SEGMENT];

  /** The legacy word an expired term is qualified with. */
  protected readonly expiredQualifier = EXPIRED_QUALIFIER;

  private readonly today = signal<Date>(new Date());

  /**
   * Whether a portal's hosting term records an expiry at all.
   *
   * ⚠ THE CELL NEEDS THIS BECAUSE THE DISPLAY PIPE CANNOT SAY IT. The pipe answers the empty string for an
   * absent instant, which is why six of seven expiry cells rendered nothing whatsoever - no text, no children,
   * and nothing for a screen reader either. The template asks this first and renders the shared absent-value
   * mark instead, so an unrecorded term is stated rather than left blank.
   *
   * @param portal The row.
   * @returns True when an expiry instant is recorded.
   */
  protected hasExpiry(portal: PortalListItem): boolean {
    // ⚠ THE PIPE'S OWN PARSER IS ASKED, RATHER THAN THIS METHOD DECIDING FOR ITSELF, AND THE DIFFERENCE IS
    // A DEFECT THIS FIXED. A presence test on the string was true for the LEGACY ABSENT-DATE MARKER — the
    // wire carries `0001-01-01T00:00:00Z`, which is a non-empty string and a real ISO instant — so the cell
    // took the "there is a date" branch and then painted the pipe's answer for it, which is the empty
    // string. The result was the very blank cell the shared absent value exists to end, on precisely the
    // rows most likely to carry it. `Portals.ascx.vb:L250-L260` treated that marker as "no expiry", and the
    // parser is the one place in this workspace that encodes it, so both verdicts now come from it: a
    // qualifier can never be painted beside an empty cell, and a date can never be painted without one.
    return parseDisplayInstant(portal.expiryDate) !== null;
  }

  /**
   * The name filter as this screen last asked for, or `undefined` when it has asked for none yet. ⚠ THIS
   * IS AN ECHO GUARD AND REMOVING IT WOULD ERASE THE OPERATOR'S KEYSTROKES. The reconciliation effect
   * below writes the filter in force into the search box, and adopting a term also CANCELS whatever
   * emission the box has pending.
   */
  private ownFilterRequest: string | null | undefined = undefined;

  // CONSTRUCTION

  constructor() {
    effect(() => {
      const inForce: string | null = this.store.nameFilter();

      untracked(() => {
        if (this.ownFilterRequest !== undefined && this.ownFilterRequest === inForce) {
          // The echo of this screen's own request. The box already holds the operator's text - possibly with
          // more typed since - so it is left entirely alone.
          return;
        }

        // Whatever arrives next is not an echo of a request this screen made.
        this.ownFilterRequest = undefined;
        this.nameFilterControl?.cancelPendingSearch(inForce ?? '');
      });
    });
    effect(() => {
      const awaited: PortalListItem | null = this.awaitedDeletion();
      const inFlight: boolean = this.store.detailLoading();
      const failure: PortalFailure | null = this.store.detailFailure();

      if (awaited === null || inFlight) {
        return;
      }

      // The announcement writes two signals of its own. Doing that inside the tracked body would make this
      // effect depend on what it had just written.
      untracked(() => {
        this.awaitedDeletion.set(null);
        this.reportDeletionOutcome(failure);
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
  protected readonly portalRowKey = (row: PortalListItem): number => row.portalId;

  // LIFECYCLE

  /**
   * Assembles the column set, then reads the first page. In this order because the descriptors carry the
   * cell templates, and a grid bound to rows before its columns exist would render a table with no
   * columns for one frame.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two requests for the same page on every entry.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: PortalListQuery = parseListQuery(address);

        if (!addressStatesQuery(address, serialiseListQuery(query))) {
          void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: serialiseListQuery(query),
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });

          return;
        }

        // ⚠ REMEMBERED HERE, WHERE THE COORDINATE IS ALREADY SETTLED AND ALREADY CANONICAL. The branch above
        // rewrites a non-canonical address and returns, so by this line the parameters are the ones the
        // listing will actually read - which is what a form must be returned to. Recording it at this single
        // point covers every route into a changed coordinate, whether the operator paged, filtered, sorted
        // or arrived on a pasted address, without each of those handlers having to remember to say so.
        this.listReturn.remember(PORTAL_LIST_ROUTE, serialiseListQuery(query));

        this.store.applyListQuery(query);
      });
  }

  // The first-letter filter strip

  /**
   * Applies one entry of the filter strip. `All` CLEARS the filter rather than sending its own label.
   *
   * @param option The entry chosen.
   */
  protected onFilterSelected(option: PortalFilterOption): void {
    // ⚠ THE PENDING EMISSION IS CALLED OFF FIRST, AND THE ORDER MATTERS. The search box holds its own
    // debounce, so an operator who types "bl" and then presses "C" a moment later would otherwise have the
    // elapsed delay emit "bl" AFTER this letter has been applied - the older intent silently replacing the
    // newer one, with "C" painted as selected over a listing of B. Cancelling here, before the navigation,
    // is what makes the newer intent win.
    this.nameFilterControl?.cancelPendingSearch();
    this.applyFilter(option.value);
  }

  /**
   * Whether a strip entry is the one currently applied.
   *
   * @param option The entry to test.
   * @returns True when the entry describes the filter in force.
   */
  protected isFilterSelected(option: PortalFilterOption): boolean {
    return this.activeFilter() === option.value;
  }

  /**
   * Applies the free-text name filter. the text is forwarded BYTE FOR BYTE - untrimmed, its case
   * unchanged and with NO pattern character appended.
   *
   * @param term The operator's text, exactly as typed.
   */
  protected onSearch(term: string): void {
    const wanted: string | null = term.length === 0 ? null : term;

    // Armed BEFORE the navigation, because the store settles synchronously once the address emits and the
    // reconciliation effect reads it immediately afterwards. See {@link ownFilterRequest} for why an echo
    // of this request must not be written back into the box the operator is still typing in.
    this.ownFilterRequest = wanted;
    this.applyFilter(wanted);
  }

  /**
   * Puts a filter into the address, returning to the first page. returning to the first page reproduces
   * the legacy behaviour exactly rather than adding a convenience.
   *
   * @param name The filter to apply, or `null` to clear it.
   */
  private applyFilter(name: string | null): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        [FILTER_PARAM]: name,
        [PAGE_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  // PAGING

  /**
   * Moves to another page. The index is passed through untouched.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  protected onPageChange(pageIndex: number): void {
    this.goToPage(pageIndex);
  }

  /** Returns to the first page, offered from the past-the-end surface. */
  protected onReturnToFirstPage(): void {
    this.goToPage(FIRST_PAGE_INDEX);
  }

  /**
   * Puts a page into the address. A PUSHED entry rather than a replaced one, which is the legacy
   * behaviour and the one an operator expects: `Portals.ascx.vb:L215-L222` composed `currentpage` into a
   * real navigation, so every page turn was a history entry and back returned to the previous page.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  private goToPage(pageIndex: number): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        [PAGE_PARAM]: firstPageParameter(pageIndex),
      },
      queryParamsHandling: 'merge',
    });
  }

  // ORDERING

  /**
   * Reorders the listing, returning to the first page. MIGRATION: sorting is a NET ADDITION and the
   * endpoint is what makes it offerable.
   *
   * @param change The heading that was activated and the direction to apply.
   */
  protected onSortChange(change: DataTableSortChange): void {
    // A NULL DIRECTION CLEARS THE ORDERING RATHER THAN DEFAULTING IT: the grid's cycle has a third step
    // that asks for no ordering at all, which is the state this screen arrives in, so the KEY leaves the
    // address alongside the direction. A key with no direction would be a different question entirely.
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

  // TALLY CELLS

  /**
   * Reads the tally a tally column paints. One template serves both tally columns - the shared grid hands
   * a cell template the column it is rendering, precisely so that it can - and this is where the column
   * becomes a value.
   *
   * @param portal The row.
   * @param columnKey The column being rendered.
   * @returns The tally as it arrived, marker and all.
   * @throws Error when the key is not one of the two tally columns.
   */
  private tallyOf(portal: PortalListItem, columnKey: string): number {
    switch (columnKey) {
      case USERS_COLUMN_KEY:
        return portal.users;
      case PAGES_COLUMN_KEY:
        return portal.pages;
      case DISK_SPACE_COLUMN_KEY:
        return portal.hostSpace;
      default:
        throw new Error(
          `The shared tally cell template was rendered for column "${columnKey}", which is not a tally ` +
            `column. It serves "${USERS_COLUMN_KEY}", "${PAGES_COLUMN_KEY}" and ` +
            `"${DISK_SPACE_COLUMN_KEY}" only.`,
        );
    }
  }

  /**
   * Whether a tally cell holds the legacy absent-integer marker rather than a count.
   *
   * @param portal The row.
   * @param columnKey The column being rendered.
   * @returns True when the cell should paint the mark instead of the number.
   */
  protected isTallyAbsent(portal: PortalListItem, columnKey: string): boolean {
    return this.tallyOf(portal, columnKey) === ABSENT_INTEGER;
  }

  /**
   * The text a tally cell paints.
   *
   * @param portal The row.
   * @param columnKey The column being rendered.
   * @returns The count as text, or the absent mark.
   */
  protected tallyText(portal: PortalListItem, columnKey: string): string {
    return formatTally(this.tallyOf(portal, columnKey));
  }

  // EXPIRY

  /**
   * How a portal's hosting term stands, for the expiry cell to convey.
   *
   * @param portal The row.
   * @returns The state of the term.
   */
  protected expiryState(portal: PortalListItem): PortalExpiryState {
    return resolveExpiryState(portal.expiryDate, this.today());
  }

  // ROW AFFORDANCES

  /**
   * The settings route of every portal on the page, keyed by identifier. Precomputed once per page rather
   * than per row per change-detection pass, and bound as an index rather than called.
   */
  protected readonly editSettingsLinks: Signal<Readonly<Record<number, (string | number)[]>>> =
    computed(() => {
      const links: Record<number, (string | number)[]> = {};

      for (const portal of this.portals()) {
        links[portal.portalId] = [PORTALS_SEGMENT, portal.portalId, SETTINGS_SEGMENT];
      }

      return links;
    });

  /**
   * The RECORD route of every portal on the page, keyed by identifier. ⚠ MAJOR (reachability) — THIS IS
   * THE ONLY INBOUND AFFORDANCE THE PORTAL RECORD SCREEN HAS. The route `/portals/:portalId` is part of
   * the frozen route table and renders the record form - the title, description and keywords, with the
   * optimistic revision marker - yet no affordance anywhere in the application named it, so it was
   * reachable only by typing its address.
   */
  protected readonly recordLinks: Signal<Readonly<Record<number, (string | number)[]>>> = computed(() => {
    const links: Record<number, (string | number)[]> = {};

    for (const portal of this.portals()) {
      links[portal.portalId] = [PORTALS_SEGMENT, portal.portalId];
    }

    return links;
  });

  /**
   * Whether the row may offer a delete affordance. the row rule is preserved exactly.
   *
   * @param portal The row.
   * @returns True when the row is not the tenant being browsed.
   */
  protected canDelete(portal: PortalListItem): boolean {
    return this.session.portalId() !== portal.portalId;
  }

  /**
   * The row's host names, ready to render as anchors. Read from a projection built once per page rather
   * than recomputed per redraw, so the link objects stay identity-stable and the template's tracked loop
   * does not rebuild its anchors on every change-detection pass.
   *
   * @param portal The row.
   * @returns One link per non-empty host name, in the order received.
   */
  protected aliasLinks(portal: PortalListItem): readonly PortalAliasLink[] {
    const projected: readonly PortalAliasLink[] | undefined = this.aliasLinkIndex().get(
      portal.portalId,
    );

    return projected === undefined ? NO_ALIAS_LINKS : projected;
  }

  /**
   * The accessible name for one row's delete affordance. The affordances repeat down the column, so the
   * portal's own title is what distinguishes them; an unnamed repeated control reaches assistive
   * technology as a list of identical commands.
   *
   * @param portal The row.
   * @returns The command's accessible name.
   */
  protected deleteCommandLabel(portal: PortalListItem): string {
    return `Delete ${nameForAnnouncement(portal.portalName)}`;
  }

  /**
   * The accessible name for one row's settings affordance. Built from the LOCAL resource value
   * `Edit.Text` - "Edit this Portal", which overrides the terser global `Edit.Text` of "Edit" - qualified
   * by the row's own title for the same reason as the delete affordance.
   *
   * @param portal The row.
   * @returns The link's accessible name.
   */
  protected editCommandName(portal: PortalListItem): string {
    return `${EDIT_COMMAND_VISIBLE_LABEL}: ${nameForAnnouncement(portal.portalName)}`;
  }

  /**
   * The DESCRIPTION of one row's settings affordance - the legacy tooltip wording, which is no longer its
   * name. ⚠ MINOR (WCAG 2.5.3) — see {@link EDIT_COMMAND_LABEL}. Composed rather than constant so the
   * description names the row too, which is what the legacy tooltip could not do.
   *
   * @param portal The row.
   * @returns The link's description.
   */
  protected editCommandDescription(portal: PortalListItem): string {
    return `${EDIT_COMMAND_LABEL}: ${nameForAnnouncement(portal.portalName)}`;
  }

  protected readonly editCommandVisibleLabel = EDIT_COMMAND_VISIBLE_LABEL;

  /**
   * The DESCRIPTION of one row's record link - never its name. ⚠ MAJOR (reachability) — the row title IS
   * the link's accessible name, which is exactly what WCAG 2.5.3 wants and what makes the affordance
   * addressable by voice, so no `aria-label` is applied to it: one would REPLACE the title with a
   * composed sentence and a speech-input user saying the portal's name would match nothing.
   *
   * @param portal The row.
   * @returns The link's description.
   */
  protected recordLinkDescription(portal: PortalListItem): string {
    return `${RECORD_LINK_DESCRIPTION}: ${nameForAnnouncement(portal.portalName)}`;
  }

  /**
   * Whether one row's hosting fee is below zero. ⚠ MINOR (money differentiation) — see {@link
   * isNegativeFee} for why only this case is marked.
   *
   * @param portal The row.
   * @returns True when the fee is negative.
   */
  protected isFeeNegative(portal: PortalListItem): boolean {
    return isNegativeFee(portal.hostFee);
  }

  /**
   * Whether the row's hosting fee is painted approximately.
   *
   * @param portal The row being drawn.
   * @returns `true` when the painted figure may differ from the stored one.
   */
  protected isHostFeeApproximate(portal: PortalListItem): boolean {
    return isFeeApproximate(portal.hostFee);
  }

  /**
   * The text of one row's hosting-fee cell. The SAME formatter the column used before this change,
   * reached through the component so the cell can be rendered from a template and still paint exactly the
   * characters it did - two fraction digits, no grouping, no currency symbol, reproducing
   * `portals.ascx:L47 DataFormatString="{0:0.00}"`.
   *
   * @param portal The row.
   * @returns The formatted fee, or an empty string when the fee is not a finite number.
   */
  protected hostingFeeText(portal: PortalListItem): string {
    return formatHostingFee(portal.hostFee);
  }

  protected readonly negativeFeeQualifier = NEGATIVE_FEE_QUALIFIER;

  /** The word appended to an approximately painted fee. */
  protected readonly approximateFeeQualifier = APPROXIMATE_FEE_DESCRIPTION;

  // THE DELETE FLOW

  /**
   * Asks for confirmation before removing a portal. the confirmation moves from a BROWSER DIALOG to an
   * in-page one, and its wording is carried across unchanged.
   *
   * @param portal The row whose delete affordance was pressed.
   */
  protected requestDeletion(portal: PortalListItem): void {
    this.pendingDeletion.set(portal);
  }

  /** Closes the confirmation without removing anything. */
  protected onDeletionCancelled(): void {
    this.pendingDeletion.set(null);
  }

  /**
   * Removes the confirmed portal. the page is RE-READ after a removal, reproducing the `BindData()` call
   * at `Portals.ascx.vb`.
   */
  protected onDeletionConfirmed(): void {
    const portal: PortalListItem | null = this.pendingDeletion();

    if (portal === null) {
      return;
    }

    this.pendingDeletion.set(null);
    this.awaitedDeletion.set(portal);
    this.store.deletePortal(portal.portalId);
  }

  /** Dismisses a reported listing failure without re-reading. */
  protected onFailureDismissed(): void {
    this.store.clearFailures();
  }

  /** Re-reads the current page after a failure. */
  protected onRetry(): void {
    this.store.reloadPortals();
  }

  /**
   * Announces the outcome of a confirmed deletion. the two outcomes keep their legacy severities, which
   * are NOT the same.
   *
   * @param failure The classified failure, or `null` when the removal succeeded.
   */
  private reportDeletionOutcome(failure: PortalFailure | null): void {
    if (failure === null) {
      this.notifications.success(PORTAL_DELETED_MESSAGE);

      return;
    }

    if (failure.status === HTTP_CONFLICT) {
      this.notifications.error(LAST_PORTAL_MESSAGE);

      return;
    }

    this.notifications.notify(
      failure.severity,
      stripLeadingBreakTags(problemDetailsMessage(failure.problem, DELETE_FAILED_MESSAGE)),
    );
  }

  // THE COLUMN SET

  /**
   * Describes the grid's ten columns. The ORDER is the legacy markup order, `portals.ascx`: two command
   * columns, then the identifier, the title, the host names, the account count, the page count, the disk
   * allowance, the fee and the expiry.
   *
   * @returns The ten columns, in legacy order.
   * @throws Error if a required cell template is missing from the paired template file.
   */
  private buildColumns(): readonly DataTableColumn<PortalListItem>[] {
    return [
      // 1. `dnn:imagecommandcolumn CommandName="Edit" EditMode="URL" KeyField="PortalID"` An `actions`
      // column, which suppresses row activation so that following the link never doubles as selecting the
      // row.
      {
        key: 'edit',
        label: EDIT_COMMAND_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        // The command track, sized for the target it holds. See the token for why `min-content` was wrong.
        width: 'var(--table-command-column-text-inline-size)',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.editCommandTemplate, 'editCommand'),
      },

      // The label is the GLOBAL `cmdDelete.Text`, which is "Delete".
      {
        key: 'delete',
        label: 'Delete',
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'var(--table-command-column-text-inline-size)',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.deleteCommandTemplate, 'deleteCommand'),
      },

      // 3. Template column over a label bound to the identifier, start-aligned in body AND heading.
      {
        key: 'portalId',
        label: PORTAL_ID_HEADING,
        headerAlign: 'start',
        bodyAlign: 'start',
        // ⚠ WIDTHS ARE DECLARED ON EVERY COLUMN OF THIS GRID, AND THE ABSENCE OF THEM WAS A MEASURED DEFECT.
        // A fixed table layout gives every track with no declared width the SAME share, so a three-character
        // identifier was as wide as a portal title: at 1440 the title and host columns broke names mid-word
        // - "Administrato / rs" - while the identifier column sat mostly empty, and at 375 every track
        // resolved near 49px. The percentages below weight each track by what its content actually needs.
        width: '9.5%',
        atomic: true,
        field: 'portalId',
        // Ordering: the key IS the endpoint's own sort name. See the sortability note on this class.
        sortable: true,
      },

      // 4. Template column over a label bound to the portal NAME, under the heading "Title" The key follows
      //   the contract member and the label follows the resource value; they differ, and both are correct.
      {
        key: 'portalName',
        rowHeader: true,
        label: TITLE_HEADING,
        headerAlign: 'start',
        bodyAlign: 'start',
        // ⚠ THIS COLUMN DELIBERATELY DECLARES NO WIDTH, AND EXACTLY ONE COLUMN PER GRID MUST NOT.
        //
        // Under `table-layout: fixed` the percentage tracks are resolved against the table width and whatever
        // is LEFT OVER is handed to the columns that declared something else. With every column weighted, that
        // leftover went to the two command columns: they asked for 5rem each and painted 89.875px, so the icon
        // columns were as wide as a title column while the titles broke mid-word — the very defect the
        // weighting exists to end. Proven with a probe: `52px`, `3.25rem` and the token all rendered 909.73px
        // beside percentages summing to 24%.
        //
        // The title is the right column to carry the slack: it holds the longest free-text value on the
        // screen, it is this row's header and its only inbound affordance, and it is the column a reader
        // scans. Every other column now gets exactly the share it declares.
        // ⚠ MAJOR (reachability) — A TEMPLATE COLUMN RATHER THAN A BOUND ONE, so the title can carry the
        // record screen's only inbound affordance.
        kind: 'template',
        cellTemplate: this.requireTemplate(this.portalNameCellTemplate, 'portalNameCell'),
        // Ordering is unaffected by the change of column kind: the key IS the endpoint's own sort name and
        // the server orders on the stored title, not on what the cell renders.
        sortable: true,
      },

      // 5. Template column over `FormatPortalAliases(...)`.
      {
        key: 'aliases',
        label: ALIASES_HEADING,
        headerAlign: 'start',
        bodyAlign: 'start',
        width: '13%',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.aliasesCellTemplate, 'aliasesCell'),
      },

      {
        key: USERS_COLUMN_KEY,
        label: USERS_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: '7%',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.tallyCellTemplate, 'tallyCell'),
      },

      {
        key: PAGES_COLUMN_KEY,
        label: PAGES_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: '7%',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.tallyCellTemplate, 'tallyCell'),
      },

      // ⚠ A TEMPLATE COLUMN, so the absent-integer marker is rendered rather than printed. See
      // {@link DISK_SPACE_COLUMN_KEY}. Ordering is unaffected: the server still sorts on the STORED value,
      // so a row carrying the marker keeps the place the server gave it instead of being reordered by a
      // display rule - the same arrangement the expiry column states for itself.
      {
        key: DISK_SPACE_COLUMN_KEY,
        label: DISK_SPACE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: '11.5%',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.tallyCellTemplate, 'tallyCell'),
        sortable: true,
      },

      // ⚠ MINOR (money differentiation) — A TEMPLATE COLUMN, not a formatted one, and the change is about
      // treatment rather than text. The two fraction digits are still the legacy format string and are
      // still produced by the same formatter, now reached from the template through `hostingFeeText`.
      {
        key: 'hostFee',
        label: HOSTING_FEE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        // NOT `atomic`: the cell carries a qualifier after the figure on the rows that need one, and clipping
        // that qualifier would remove information. The figure itself is held together by `data-atomic-value`
        // in the paired template.
        width: '10%',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.hostFeeCellTemplate, 'hostFeeCell'),
        sortable: true,
      },

      // 10. Template column over `FormatExpiryDate(...)`.
      {
        key: 'expiryDate',
        label: EXPIRES_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        // NOT `atomic`, for the same reason as the fee: an expired row carries a marker after the date.
        width: '12%',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.expiresCellTemplate, 'expiresCell'),
        // Ordered on the STORED date, so the rows an absent expiry paints as an empty cell still take their
        // place in the sequence the server produced rather than being reordered by the display rule.
        sortable: true,
      },
    ];
  }

  /**
   * Resolves one captured cell template, reporting the reference when it is missing. A template column
   * with no template renders a blank cell on every row, which looks like missing DATA rather than a
   * mis-declared column.
   *
   * @param captured The statically-queried template, or `undefined` when absent.
   * @param reference The template reference the paired file must declare.
   * @returns The template.
   * @throws Error when the template was not declared.
   */
  private requireTemplate(
    captured: TemplateRef<DataTableCellContext<PortalListItem>> | undefined,
    reference: string,
  ): TemplateRef<DataTableCellContext<PortalListItem>> {
    if (captured === undefined) {
      throw new Error(
        `portal-list.component.html must declare an ng-template named "#${reference}" at ` +
          'the top level of the template, outside any control-flow block.',
      );
    }

    return captured;
  }
}
