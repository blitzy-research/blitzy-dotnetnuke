//
// The portal (tenant) listing screen of the dnn-migration administration front end, served at `/portals`.
//
// The presentational half of one screen: a first-letter filter strip, a free-text name filter, a ten-column
// grid of portals, a pager, and a confirmed row deletion.
//
// It holds NO list state. `core/state/portal.store.ts` owns the page, its filter, its paging coordinates and
// its structured failures, and this class reads those signals and calls those commands. The four things it does
// own are the column descriptor set, which portal is awaiting a deletion confirmation, which deletion is
// awaiting an outcome, and the four cell templates the shared grid renders. Everything else it exposes is a
// projection of store state.
//
// And, symmetrically, the things it must NOT do, each of which has an owner:
//
//   * REACH THE NETWORK. No HTTP client is injected. The one data-access path is the store, which delegates to
//     `core/services/portal.service.ts`.
//   * BUILD A REQUEST. No URL, no query string, no parameter name and no pattern character. Route templates
//     belong to the endpoint registry under `core/config/` and query parameters to the serialiser under
//     `core/utils/`.
//   * DECIDE A PERMISSION. The server is authoritative and answers 403. The one per-row affordance rule below
//     is a display decision reproduced from the legacy screen, not an authorisation check.
//   * DECLARE A PROVIDER OR A GUARD. Provider wiring lives in `app.config.ts` and route guards in the
//     feature's route table.
//
// Wording authority: every user-facing string is taken from the RESOURCE VALUE and never from a markup
// attribute, and the legacy application settles which is right - it localised its grid at run time by looking
// each column's own header text up as a resource key, so the markup read `PortalId`, `DiskSpace` and
// `HostingFee` while the screen PAINTED `Portal Id`, `Disk Space` and `Hosting Fee`.
//
// MIGRATION: localisation itself is NOT ported. No translation runtime is present in this workspace's pinned
// dependency surface, so the English wording is authored directly into these constants and into the paired
// template, with the legacy resource files serving as the reference for what that wording is.
//
// MIGRATION: resource text is treated as UNTRUSTED HTML and is rendered as PLAIN TEXT ONLY. A substantial
// minority of the in-scope resource values contain HTML tags, stored HTML-escaped and therefore invisible to a
// naive search, and one value in this very screen's own resource file is escaped markup opening with a heading
// tag. Nothing here is bound through a raw-HTML property, a sanitiser bypass or a trusted-HTML wrapper; where a
// legacy value genuinely needed structure it is re-authored as real template markup instead.
//

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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { problemDetailsMessage } from '../../../core/models/problem-details.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
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
//
//  Each constant names the resource key it carries, so a reader can check the value against
//  `Website/admin/Portal/App_LocalResources/Portals.ascx.resx` without leaving this file. The resource file
//  holds SEVENTEEN real entries; a raw count of its `<data name=` occurrences returns twenty-one, four of
//  which - `Name1`, `Color1`, `Bitmap1` and `Icon1` - are the standard schema boilerplate sitting inside the
//  file's leading XML comment block and are not wording at all.

/**
 * `ControlTitle_.Text`. The page heading.
 */
const PAGE_TITLE = 'Portals';

/**
 * `PortalId.Header`. Note the space: the markup spelled this heading `PortalId`.
 */
const PORTAL_ID_HEADING = 'Portal Id';

/**
 * `Title.Header`. Painted over the portal NAME, which is why the column key differs.
 */
const TITLE_HEADING = 'Title';

/**
 * `Portal Aliases.Header`. The resource KEY itself contains a space.
 */
const ALIASES_HEADING = 'Portal Aliases';

/**
 * `Users.Header`.
 */
const USERS_HEADING = 'Users';

/**
 * `Pages.Header`.
 */
const PAGES_HEADING = 'Pages';

/**
 * Column key of the account tally.
 *
 * Named rather than written inline because the shared cell template that serves BOTH tally columns switches
 * on it: the template is handed the column it is rendering, so one template can answer two columns, and the
 * key is the only thing that tells them apart.
 */
const USERS_COLUMN_KEY = 'users';

/**
 * Column key of the page tally. See {@link USERS_COLUMN_KEY}.
 */
const PAGES_COLUMN_KEY = 'pages';

/**
 * `DiskSpace.Header`. Two words in the value, one in the markup attribute.
 */
const DISK_SPACE_HEADING = 'Disk Space';

/**
 * `HostingFee.Header`. Two words in the value, one in the markup attribute.
 */
const HOSTING_FEE_HEADING = 'Hosting Fee';

/**
 * `Expires.Header`.
 */
const EXPIRES_HEADING = 'Expires';

/**
 * `Expired.Text`, from THIS screen's own local resource file
 * (`Website/admin/Portal/App_LocalResources/Portals.ascx.resx:L162-L164`).
 *
 * MIGRATION: the WORDING is this screen's own and the SEMANTICS are the account-services screen's. The entry
 * exists in the portal list's resource file because the legacy strip carried an `Expired` bucket, which is
 * dropped for the reason recorded at {@link buildFilterOptions} - so the word is available to this screen
 * without being borrowed. What it QUALIFIES is taken from `Website/admin/Users/MemberServices.ascx.vb:L176`,
 * the one legacy screen that tested an expiry against the clock.
 *
 * MIGRATION: the qualifier is painted BESIDE the date, where the account-services screen painted it INSTEAD
 * OF the date. That screen served a member their own subscriptions; this one is the host's administration
 * grid, and an administrator needs to know WHEN a term lapsed as well as that it has. Keeping both preserves
 * strictly more than either legacy screen showed, and it keeps this screen's own formatter
 * (`Portals.ascx.vb:L250-L260`, which always printed the date) intact.
 */
const EXPIRED_QUALIFIER = 'Expired';

/**
 * `Edit.Text` from the LOCAL file, which overrides the shorter global `Edit.Text`.
 *
 * ⚠ MINOR (WCAG 2.5.3 Label in Name) — THIS IS NO LONGER THE ACCESSIBLE NAME.
 *
 * It was, and that was the defect: ten row affordances painted the word `Settings` while their
 * accessible names read `Edit this Portal: <title>`, so the accessible name did not CONTAIN the
 * visible text and a speech-input user saying "click Settings" matched nothing. Ten instances
 * on one page.
 *
 * The legacy wording is not discarded, because it is a real measured resource value and it says
 * something the visible word does not - which portal the command acts on, and that it opens for
 * editing. It moves to the affordance's DESCRIPTION, which is exactly what it was: the legacy
 * column rendered an unlabelled image (`portals.ascx:L21`) and this string was its tooltip.
 * A tooltip is a description, not a name, so relocating it corrects a category error as well as
 * a conformance failure.
 *
 * @see EDIT_COMMAND_VISIBLE_LABEL for what the accessible name is built from now.
 */
const EDIT_COMMAND_LABEL = 'Edit this Portal';

/**
 * The word the settings affordance PAINTS, and therefore the word its accessible name must open
 * with.
 *
 * Declared here rather than written into the template so the two cannot drift: the template
 * interpolates this constant and {@link PortalListComponent.editCommandName} composes from it, so
 * a change to the visible word changes both at once and Label in Name cannot silently break.
 */
const EDIT_COMMAND_VISIBLE_LABEL = 'Settings';

/**
 * Announced beside a negative hosting fee, and never painted.
 *
 * ⚠ MINOR (money differentiation) — the colour and weight this screen gives a negative fee are a
 * VISUAL cue, and a visual cue alone would carry the meaning by colour only, which WCAG 1.4.1
 * forbids. This is the same cue in words, hidden from the page and present in the accessibility
 * tree, exactly as the absent-integer mark's description is.
 *
 * The word is purely factual. It states the sign and nothing else - it does not call the value a
 * credit, a refund or an error, because the schema permits it and no legacy screen interpreted it.
 */
const NEGATIVE_FEE_QUALIFIER = 'negative';

/**
 * `AddContent.Action`. The one surviving page-level action.
 */
const ADD_PORTAL_ACTION = 'Add New Portal';

/**
 * `PortalDeleted.Text`, surfaced at success severity.
 */
const PORTAL_DELETED_MESSAGE = 'Portal deleted successfully';

/**
 * `Filter.Text`, verbatim and complete.
 *
 * A pure twenty-six-letter list: no `All` entry, no digit bucket and no expired entry. Split rather than
 * written out as an array so that the string can be compared character for character against the resource
 * value.
 */
const LETTER_FILTER_CSV = 'A,B,C,D,E,F,G,H,I,J,K,L,M,N,O,P,Q,R,S,T,U,V,W,X,Y,Z';

// Wording - global resource file
//
//  `Website/App_GlobalResources/SharedResources.resx`, which holds 353 real entries. The legacy screen
//  reached these three deliberately rather than by accident: the filter strip appended
//  `Localization.GetString("All")` with NO local resource file argument, the delete column's confirmation
//  came from `Localization.GetString("DeleteItem")` the same way, and the refusal was read by the data
//  layer itself.

/**
 * `All.Text`. A LABEL only - see {@link PORTAL_FILTER_OPTIONS} for why it carries no value.
 */
const ALL_FILTER_LABEL = 'All';

/**
 * `DeleteItem.Text`. The row-deletion confirmation, singular.
 *
 * ⚠ NOT RENDERED ALONE ANY MORE. See {@link PortalListComponent.deleteConfirmMessage}: the
 * measured wording is kept verbatim as the question, and the identity of the row being destroyed
 * is appended to it, because this dialog covers the very row the operator was reading.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * `LastPortal.Text`. The refusal when an installation would be left with no portal.
 *
 * MIGRATION: the legacy wording is preserved verbatim, but its DELIVERY changed. The legacy data layer
 * returned this string as a value - `Library/Components/Portal/PortalController.vb` sets `strMessage =
 * Localization.GetString("LastPortal")` inside the else-branch of its portal-count test - and the screen
 * presented whatever came back. The server now answers the attempt with a `409` carrying a published reason
 * code, so the wording is held here rather than received. It is spelled out rather than looked up so that
 * this screen has no dependency on a code-to-wording table it does not own.
 */
const LAST_PORTAL_MESSAGE = 'You Can Not Delete The Last Portal In Your Database';

// Wording - authored for this screen
//
//  Five strings with NO legacy counterpart. Each is authored rather than carried, and each is here rather
//  than in the template so that a specification can assert the rendered text against the same constant the
//  screen supplies.

/**
 * The grid's accessible name, projected into the shared table's caption slot.
 *
 * MIGRATION: authored. The legacy grid carried no `summary` attribute and no caption of any kind, so it
 * reached assistive technology as an unnamed table. Supplying one is invisible accessibility - the shared
 * table clips its caption out of the painted output - so it costs nothing visually and closes a real gap.
 */
const GRID_CAPTION = 'Portals, with their host names, account and page counts, and hosting terms';

/**
 * Accessible name for the first-letter filter strip, which is a group and not a landmark.
 */
const FILTER_STRIP_LABEL = 'Filter portals by first letter';

/**
 * Placeholder for the free-text name filter.
 *
 * MIGRATION: the wording STATES THE PREDICATE, because the predicate is not the one a reader would assume.
 * The legacy screen appended a single trailing wildcard at the call site - `Portals.ascx.vb:L142` reads
 * `GetPortalsByName(Filter + "%", ...)` and the surviving procedure applies it unchanged - so the test is
 * "begins with", and a fragment from the middle of a title matches nothing. Runtime testing found the earlier
 * wording ("Filter by name") over a server that had been widened to match anywhere; the server has since been
 * returned to the legacy prefix test, which is also what makes the twenty-six-letter strip beside this box
 * mean what its name says. Saying so here is the difference between a filter that appears broken and one an
 * operator can use.
 */
const SEARCH_PLACEHOLDER = 'Name begins with';

/**
 * Shown when nothing matched at all - a total of nought.
 */
const NO_PORTALS_MESSAGE = 'No portals match the current filter.';

/**
 * Shown when records exist but the page in hand holds none.
 *
 * A DIFFERENT sentence from {@link NO_PORTALS_MESSAGE}, because the two states are different and the paging
 * contract calls the distinction out: no rows on the fourth page of three means "past the end", whereas a
 * total of nought means "nothing matched", and telling an operator the second when the first is true sends
 * them to change a filter that is working.
 */
const PAST_END_MESSAGE = 'This page is past the end of the results. Return to the first page.';

/**
 * Fallback wording when a failure carried no readable problem document.
 */
const LIST_FAILED_MESSAGE = 'The portals could not be loaded.';

/**
 * Fallback wording when a deletion failed and carried no readable problem document.
 */
const DELETE_FAILED_MESSAGE = 'The portal could not be deleted.';

// ROUTE SEGMENTS

/**
 * Root segment of the portal feature, as mounted by the application's route table.
 */
const PORTALS_SEGMENT = '/portals';

/**
 * Child segment the edit affordance targets.
 *
 * MIGRATION: the row's edit affordance opens SITE SETTINGS and not a create/edit form, which is the legacy
 * behaviour rather than a design choice. `Portals.ascx.vb` resolved the administration page named `Site
 * Settings` through `objTabs.GetTabByName("Site Settings", PortalSettings.PortalId,
 * PortalSettings.AdminTabId)` and built its column's address as `NavigateURL(objTab.TabID, "",
 * "pid=KEYFIELD")` with the placeholder then swapped for a format specifier. That `pid` query pair is the
 * direct ancestor of the `:portalId/settings` route segment used here.
 */
const SETTINGS_SEGMENT = 'settings';

/**
 * Child segment the page-level add action targets.
 */
const NEW_SEGMENT = 'new';

// FORMATTING

/**
 * Decimal places for the hosting fee.
 *
 * MIGRATION: the LIST format is authoritative here and the two legacy formats genuinely differ.
 * `portals.ascx` declares `DataFormatString="{0:0.00}"` - two decimals and NO group separator - whereas the
 * site-settings screen formats the same value with a group separator. Unifying them would change what one of
 * the two screens paints, so they stay apart.
 */
const HOSTING_FEE_FRACTION_DIGITS = 2;

/**
 * The scheme the alias links carry when the stored host name states none.
 */
const HTTP_SCHEME_PREFIX = 'http://';

/**
 * The four fragments whose presence made the legacy helper leave a host name alone.
 *
 * Read off `Library/Components/Shared/Globals.vb`. Note the path: the action plan cites a
 * `Library/Components/Common/Globals.vb` that does not exist in this repository. The last entry is a
 * two-character string of two backslashes, matching the legacy `"\\"` literal.
 */
const ABSOLUTE_ADDRESS_MARKERS: readonly string[] = Object.freeze([
  'mailto:',
  '://',
  '~',
  '\\\\',
]);

/**
 * Matches a run of leading break tags, in either spelling and in any case.
 */
const LEADING_BREAK_TAGS = /^(?:\s*<br\s*\/?>)+\s*/i;

/**
 * The status the server answers when an installation must retain its last portal.
 */
const HTTP_CONFLICT = 409;

/**
 * The legacy absent-integer marker, whose value is minus one.
 *
 * `Library/Components/Shared/Null.vb:L41` declares `NullInteger` as `-1`, and
 * `Library/Components/Portal/PortalInfo.vb:L61-L62` seeds BOTH tally properties with it. It reaches this
 * screen for real: the terminal `GetTabCount` arithmetic is `COUNT(*) - 1` over a portal's pages after its
 * administration page is excluded, and it answers minus one for a portal that records no administration page
 * at all - which is every portal in an installation whose pages have not been built.
 */
const ABSENT_INTEGER = -1;

/**
 * What a tally cell paints when it holds the absent-integer marker.
 *
 * An EM DASH, U+2014, which is the conventional "no value" mark and is announced as such. Paired with
 * {@link ABSENT_TALLY_DESCRIPTION} so the mark is not the only thing a screen reader receives.
 */
const ABSENT_TALLY_MARK = '\u2014';

/**
 * The words behind {@link ABSENT_TALLY_MARK}, for the accessibility tree.
 */
const ABSENT_TALLY_DESCRIPTION = 'not recorded';

// THE ADDRESS CONTRACT
//
//  The listing's four coordinates live in the ADDRESS, so a reload and a back or forward reproduce the view
//  and a fresh navigation starts clean. The parameter SPELLINGS are the legacy ones wherever the legacy
//  screen had one: `Portals.ascx.vb:L215-L222` composed its own address as
//  `NavigateURL(TabId, "", "filter=" & Filter, "currentpage=" & CurrentPage)`, so `filter` and `currentpage`
//  are carried over verbatim, and `currentpage` keeps the legacy ONE-BASED counting seeded at
//  `Portals.ascx.vb:L47` (`Private _CurrentPage As Integer = 1`). The two ordering parameters have no legacy
//  counterpart at all - the legacy grid was declared without `AllowSorting` - so they take the endpoint's own
//  member names in lower case, matching the case of the two that are inherited.

//  ⚠ THE FIVE PARAMETER NAMES AND THE FIVE READERS NOW LIVE IN `core/utils/list-query.util.ts` AND ARE
//  SHARED WITH THE OTHER THREE LISTINGS. They were private to this file while it was the only screen that
//  kept its query in the address; they were hoisted verbatim, with no change of behaviour, when the account,
//  role and module listings adopted the same contract. The vocabulary is the operator's rather than this
//  screen's - once `?currentpage=` means the page here it must mean the page everywhere, or an address
//  hand-edited on one listing fails silently on the next - and four private copies of one reader is a
//  divergence waiting to happen.
//
//  What did NOT move is the pair below, `parseListQuery` and `serialiseListQuery`, because only this screen
//  knows that its filter is a portal-name prefix and that its endpoint orders by exactly five columns. Each
//  listing composes its own pair out of the shared primitives.
//
//  The two size-related notes that were attached to the hoisted declarations are kept here because they are
//  about THIS screen: the page-size parameter has no legacy counterpart, the legacy screen having hard-coded
//  twenty at `Portals.ascx.vb:L92-L98` with the per-portal setting commented out, and it is accepted so that
//  a size an operator arrives with survives a reload while never being written by this screen, which
//  expresses no size preference of its own.

/**
 * The five column keys the collection endpoint will order by, in its own spelling.
 *
 * ⚠ THIS SET IS THE ENDPOINT'S, NOT THIS SCREEN'S, AND OFFERING A SIXTH WOULD PRODUCE A REFUSED REQUEST.
 * `SortableFields.Portals` admits exactly these five, matched without regard to case - so the camel-cased
 * column keys below are accepted as they stand - and answers anything else with a field-level `400` naming
 * the five. The three members the projection publishes but this set omits are omitted for stated reasons:
 * the two tallies are computed counts rather than columns on `Portals`, and the host names are a collection,
 * which has no single value to order by.
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
    // Byte for byte, and NOT trimmed: the server is the one that trims, and a filter of one space is a
    // filter the legacy screen would have applied. Only an entirely absent parameter means "no filter".
    name: address.get(FILTER_PARAM),
    sortBy,
    // A direction with no field to apply it to is dropped rather than kept, so the address cannot carry an
    // ordering half. A field with no direction is kept: the endpoint has a default.
    sortDir: sortBy === null ? null : parseSortDirection(address.get(SORT_DIR_PARAM)),
  };
}

/**
 * Writes a listing query back out as address parameters.
 *
 * A default coordinate is emitted as `null`, which the router REMOVES from the address rather than writing
 * as an empty value - so an unfiltered first page is the bare path and not a trail of empty parameters.
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

/**
 * How a portal's hosting term stands.
 *
 * Exactly THREE states, and the absence of a fourth is deliberate. The legacy test is BINARY -
 * `Website/admin/Users/MemberServices.ascx.vb:L176` is `If expiryDate > Date.Today`, with the absent case
 * handled by the enclosing `If Not Null.IsNull(expiryDate)` at `:L175` - so "expiring soon" has no legacy
 * counterpart and no authority anywhere in the in-scope tree. Inventing one would mean inventing its
 * threshold, which is a design decision this refactor is not entitled to make; the binary state is what the
 * legacy conveyed and it is what is conveyed here.
 */
export type PortalExpiryState = 'none' | 'current' | 'expired';

/**
 * Resolves the state of a hosting term against a clock.
 *
 * MIGRATION: the test is the legacy test, taken from the one legacy screen that made it.
 * `Website/admin/Users/MemberServices.ascx.vb:L172-L186` reads: an absent expiry yields the empty string, an
 * expiry LATER than today yields the date, and anything else yields the word `Expired` - so an expiry falling
 * exactly on today's midnight counts as expired, because `>` is strict. Both halves are reproduced: the
 * comparison operator and the boundary it puts today's midnight on.
 *
 * MIGRATION: the comparison is made in UTC because the CELL is rendered in UTC. The shared date pipe formats
 * in UTC deliberately - the wire carries an absolute instant and localising it would shift dates across
 * midnight - so a qualifier computed against local midnight could contradict the date printed beside it for
 * every reader whose offset is not zero. The legacy `Date.Today` was the server's local midnight, which is
 * the closest available analogue of "the display zone's midnight".
 *
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
 *
 * MIGRATION: the marker is ERASED AT THE DISPLAY LAYER ONLY, which is the same discipline the expiry column
 * already applies and is the discipline Rule T7 asks for - "sentinels survive at the boundary, not in the
 * domain". The contract keeps minus one, so anything that needs to tell the marker from a real count still
 * can; only the CELL stops painting it as a number.
 *
 * ⚠ THE REASON IS A COLLISION, NOT TIDINESS, and it is specific to this grid. `Portals.PortalID` is declared
 * `IDENTITY(-1,1)`, so minus one is a LEGITIMATE portal identifier and the first column paints it verbatim -
 * correctly. Runtime testing measured minus one in the tally columns of all 251 rows of a real installation
 * while the identifier column showed minus one on one of them, and the two are indistinguishable to a reader:
 * the same characters in the same row meaning "portal number minus one" in one cell and "no count available"
 * in another. The identifier column is deliberately left alone; only the two tallies, which cannot hold a
 * negative count, are answered this way.
 *
 * @param tally The value as it arrived.
 * @returns The mark when the value is the absent-integer marker, otherwise the number as text.
 */
function formatTally(tally: number): string {
  return tally === ABSENT_INTEGER ? ABSENT_TALLY_MARK : String(tally);
}

// VIEW-MODEL TYPES

/**
 * One entry of the first-letter filter strip.
 *
 * `value` is the text to filter by, or `null` for the entry that clears the filter. That is the whole reason
 * this is a pair rather than a bare string: the strip's last entry is LABELLED `All` but means "no filter at
 * all", and sending the label as a filter value would search for portals whose name contains the word "All".
 */
export interface PortalFilterOption {
  /**
   * The text painted on the entry.
   */
  readonly label: string;

  /**
   * The name filter the entry applies, or `null` to clear it.
   */
  readonly value: string | null;
}

/**
 * One of a portal's host names, ready to render as a real anchor.
 *
 * MIGRATION: the legacy screen CONCATENATED HTML for this cell. `Portals.ascx.vb` appended `"<a href=""" +
 * AddHTTP(alias) + """>" + alias + "</a>" + "<BR>"` per host name and assigned the result to a label's
 * `Text`, so a stored host name was interpolated into markup unescaped. Splitting the address from the text
 * is what lets the paired template emit real anchor elements through ordinary bindings, with the text
 * interpolated and therefore escaped. No raw-HTML binding, no sanitiser bypass and no trusted-HTML wrapper
 * is used anywhere in this feature.
 */
export interface PortalAliasLink {
  /**
   * The address the anchor navigates to, or `null` when the stored value is not one.
   *
   * Nullable, and the null arm is a security boundary rather than a convenience. A stored host name is
   * operator-supplied data, and the legacy address helper this projection reproduces left a value alone
   * whenever it contained any of four fragments — one of which is the bare `://`. That exclusion admits far
   * more than an address: `javascript://` and `data://` both contain it, so a stored alias could previously
   * be emitted verbatim as the target of a link, and an application-relative `~/…` or a network share
   * `\\host\share` could be emitted as an address that is not one.
   *
   * The projection now ALLOWLISTS: a value is an address only if it parses as an absolute URL whose scheme
   * is `http:` or `https:` and which names a host. Everything else yields `null` and the paired template
   * renders the label as plain text instead of a link. Refusing to link is the correct outcome for a value
   * that does not describe somewhere to go — an operator still sees exactly what is stored, which is what
   * the screen exists to show.
   *
   * This is the resolution of review finding F13.
   */
  readonly href: string | null;

  /**
   * The host name as stored, rendered as escaped text.
   *
   * Unchanged by the allowlist above, deliberately. The label is what the operator stored and the screen's
   * job is to report it; rewriting, truncating or annotating a value because it could not be linked would
   * misreport the stored state and make the row disagree with the edit screen. Only the ADDRESS is withheld.
   */
  readonly label: string;
}

/**
 * The empty alias list, shared so that a portal with no host names allocates nothing.
 */
const NO_ALIAS_LINKS: readonly PortalAliasLink[] = Object.freeze([]);

// PURE HELPERS

/**
 * Builds the filter strip's entries.
 *
 * MIGRATION: the strip holds TWENTY-SEVEN entries where the legacy held twenty-eight, and the ORDER of the
 * twenty-seven is the legacy order rather than a tidied one. `Portals.ascx.vb` read the twenty-six-letter
 * list from the local resource file, then APPENDED `All`, then appended `Expired`, and split the result - so
 * `All` came AFTER `Z` and not before `A`. That order is preserved exactly.
 *
 * MIGRATION: the `Expired` bucket is DROPPED and no successor is invented. It was never a filter:
 * `Portals.ascx.vb` compared the filter text against `Localization.GetString("Expired", LocalResourceFile)`
 * and, on a match, called an entirely different reader - `PortalController.GetExpiredPortals()` - and hid
 * the pager outright, so WHICH QUERY RAN DEPENDED ON THE DISPLAY LANGUAGE and translating a resource file
 * changed the screen's behaviour. No endpoint in the target exposes that listing, so no query parameter of
 * any kind is fabricated for it: not an expired flag, not a filter value, not a mode.
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

/**
 * The filter strip, built once at module load because the entries never change.
 *
 * Twenty-seven entries. Frozen so a template cannot reorder or extend it in place.
 */
const PORTAL_FILTER_OPTIONS: readonly PortalFilterOption[] = buildFilterOptions();

/**
 * Guarantees a host name carries a scheme, reproducing the legacy address helper.
 *
 * `Library/Components/Shared/Globals.vb` prefixed a scheme only when the value was non-empty AND contained
 * none of `mailto:`, `://`, `~` or `\\`. Each of those four exclusions is preserved: an address that already
 * names a protocol, a mail address, an application-relative path and a network share are all left exactly as
 * stored.
 *
 * MIGRATION: the scheme is always `http://`, where the legacy helper chose `https://` when
 * `HttpContext.Current.Request.IsSecureConnection` was true. The legacy decision was made on the SERVER from
 * the inbound request; reproducing it in a component would mean reading the document's own protocol, which
 * is a direct DOM read this feature does not perform. A stored host name that should be reached securely can
 * say so - it need only carry its own `https://`, which the exclusion above then preserves untouched.
 *
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
      // The stored value already claims to be absolute, so no scheme is prefixed - exactly as the legacy
      // helper behaved. Whether it is an ADDRESS is a separate question, answered below: a mail address, an
      // application-relative path and a network share all reach this arm and none of them is somewhere this
      // link may navigate to.
      return allowedHostAddress(alias);
    }
  }

  return allowedHostAddress(`${HTTP_SCHEME_PREFIX}${alias}`);
}

/**
 * The candidate address, or `null` when it is not an `http`/`https` address naming a host.
 *
 * An allowlist, not a denylist, and that direction is the whole point. Enumerating the schemes that must be
 * refused is a losing game: `javascript:`, `data:`, `vbscript:`, `blob:` and `file:` are only the ones
 * anybody thinks of, and a stored value can be spelled with mixed case, leading whitespace or
 * percent-encoded control characters to slip past a fragment test. Admitting exactly two schemes and
 * refusing everything else needs no such enumeration and cannot be outflanked by a spelling.
 *
 * The parse is delegated to the platform URL parser rather than performed with a pattern, because the parser
 * is the thing that decides what a scheme and a host actually are - and it is the same decision the browser
 * makes when the anchor is followed. A pattern would be a second opinion.
 *
 * A host is guaranteed by the parse itself for the two admitted schemes, which is why no separate emptiness
 * test appears below. `http:` and `https:` are special schemes to the parser: it REFUSES `http://`,
 * `https://`, `http:///` and `http://:8080` outright rather than parsing them with an empty host, so
 * anything reaching the return names somewhere. An explicit host test was written here first and removed
 * once measured, because it could not fail and a guard that cannot fail invites the reader to believe it is
 * doing something. Should a third scheme ever be admitted, the test has to come back: `file:`, `blob:`,
 * `about:` and `tel:` all parse with an empty host.
 *
 * MIGRATION: no legacy counterpart, and its absence was a real exposure rather than a simplification.
 * `Portals.ascx.vb` built an anchor by string concatenation and assigned the result to a label's `Text`, so
 * a stored value went into markup unescaped and unexamined. Nothing here uses a raw-HTML binding, a
 * sanitiser bypass or a trusted-value wrapper; the framework escapes the label through an ordinary
 * interpolation, and this function decides whether there is an address at all.
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
  // longer be the value the operator stored - and this screen exists to report stored state faithfully. The
  // parse above has already established that it is safe to navigate to.
  return candidate;
}

/**
 * Projects a portal's host names into renderable links.
 *
 * MIGRATION: an EMPTY host name produces no entry, where the legacy produced an empty anchor.
 * `Portals.ascx.vb` appended one anchor per row of the alias collection with no emptiness test, so an alias
 * whose stored value was the empty string - which is the legacy spelling of an absent string,
 * `Library/Components/Shared/Null.vb` returning literally `""` - rendered as `<a href=""></a>`: a focusable,
 * unlabelled link pointing at the current page. Rendering nothing is the honest representation of "no host
 * name". This is a DELIBERATE behavioural difference rather than a silent correction, and it affects only a
 * row that had no address to link to in the first place.
 *
 * @param aliases The host names as received. Mandatory on the contract and never null, so a portal with none
 *   arrives as an empty array.
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
 * Formats the hosting fee to exactly two decimal places.
 *
 * MIGRATION: an implicit conversion in the legacy screen, made explicit. `portals.ascx` bound the value
 * through `DataFormatString="{0:0.00}"`, which the framework applied to whatever the property happened to
 * hold - a single-precision float, per `Library/Components/Portal/PortalInfo.vb`. The conversion is written
 * out here because the legacy administration code-behinds compiled with strictness OFF
 * (`Website/release.config` declares `<compilation debug="false" strict="false">`) and this workspace
 * compiles with every strictness flag on, so no coercion may be left to the runtime.
 *
 * A non-finite value renders as an empty cell rather than as the words `NaN` or `Infinity`. That matches the
 * shared grid's own treatment of a non-finite bound number and matches every legacy formatter, each of which
 * seeded its result with the empty string; the fee is non-nullable on the contract, so this guards a
 * malformed payload rather than an expected state.
 *
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
 * Matches any run of whitespace, including tabs and line breaks.
 */
const WHITESPACE_RUN = /\s+/g;

/**
 * Normalises a stored title for use INSIDE AN ACCESSIBLE NAME, and nowhere else.
 *
 * ⚠ MINOR (whitespace-trimming parity) — leading and trailing spaces and tabs were surviving
 * verbatim into `aria-label`, where they are inaudible, unremovable by the reader and capable of
 * making one announced name differ from an apparently identical one. Runtime testing confirmed
 * untrimmed values reaching the attribute.
 *
 * THE DISPLAYED VALUE IS DELIBERATELY LEFT ALONE. An operator correcting a title with a stray
 * tab in it must be able to see that the tab is there, and the cell is the only place they can;
 * silently tidying the painted text would hide the very defect they are looking for. That is the
 * same rule the host-name cell on the alias screen follows and the same rule Rule T7 asks for:
 * normalise at the boundary the value is consumed at, never in the value itself.
 *
 * Internal runs are collapsed as well as the ends trimmed, because a name announced with a tab in
 * the middle of it is read as two names by some screen readers.
 *
 * @param title The stored portal title, exactly as received.
 * @returns The title with whitespace runs collapsed to single spaces and the ends trimmed.
 */
function nameForAnnouncement(title: string): string {
  return title.replace(WHITESPACE_RUN, ' ').trim();
}

/**
 * Whether a hosting fee is below zero.
 *
 * ⚠ MINOR (money differentiation) — a negative fee, a zero fee and a positive fee were painted
 * IDENTICALLY: runtime testing measured `-125.50`, `0.00` and `4321.99` with the same colour, the
 * same weight and the same size, so the only thing distinguishing a loss was a single minus glyph.
 *
 * The negative case alone is marked, and only the negative case. Zero is not an anomaly - most of
 * this dataset's portals are hosted free - and a positive fee is the ordinary state, so marking
 * either would be noise. A NEGATIVE fee is data an administrator should question, which is why it
 * earns the same treatment this screen already gives a lapsed term.
 *
 * NOT a validation rule and NOT a refusal. `Website/admin/Portal/sitesettings.ascx` declares
 * `valHostFee` as a currency comparison with no lower bound, and the modern validator's own
 * comment records that a negative fee passes deliberately - so a negative fee is LEGAL and this is
 * a disclosure, not an objection.
 *
 * Guards finiteness first, so a non-finite fee - which {@link formatHostingFee} paints as nothing
 * at all - is never marked as negative.
 *
 * @param hostFee The fee as received.
 * @returns True when the fee is a real number below zero.
 */
function isNegativeFee(hostFee: number): boolean {
  return Number.isFinite(hostFee) && hostFee < 0;
}

/**
 * Removes leading break tags from a message before it is rendered as text.
 *
 * Defensive rather than decorative. Twenty-eight of the thirty-four legacy validator messages in the
 * in-scope resource files open with a break tag and six do not, so the convention is not reliable; a message
 * that reached this screen carrying one would paint the literal characters `<br>`, because every string here
 * is interpolated and therefore escaped. Only a LEADING run is removed - a tag anywhere else is left alone
 * and shows as text, which is the visible symptom of markup arriving where prose was expected.
 *
 * @param message The message as composed or received.
 * @returns The message with any leading break tags removed.
 */
function stripLeadingBreakTags(message: string): string {
  return message.replace(LEADING_BREAK_TAGS, '');
}

/**
 * The portal listing screen.
 *
 * The paired template MUST declare these six `ng-template` elements at its TOP LEVEL, outside every
 * control-flow block, because the column set is assembled in `ngOnInit` from statically-resolved view
 * queries:
 *
 * | Reference          | Renders                                                             |
 * | ------------------ | ------------------------------------------------------------------- |
 * | `#editCommand`     | the row's settings link, indexed from `editSettingsLinks()`          |
 * | `#deleteCommand`   | the row's delete button, guarded by `canDelete(row)`                 |
 * | `#aliasesCell`     | the row's host names, from `aliasLinks(row)`                          |
 * | `#expiresCell`     | the row's expiry, through the shared date pipe, plus its qualifier    |
 * | `#hostFeeCell`     | the row's hosting fee, marked when the fee is below zero              |
 * | `#tallyCell`       | BOTH tally columns, switching on the column key it is handed          |
 *
 * A missing reference is reported by {@link PortalListComponent} with a message naming the reference, rather
 * than rendering a silently blank column.
 *
 * Eight of the shared library's ten members are consumed. The form-field wrapper is left alone because the
 * only control here is the free-text name filter and the shared search input already owns and associates its
 * own label, so wrapping it would produce two labels for one input. The permission directive is left alone
 * because this listing is read-only and its route carries authentication only, so there is no policy for a
 * client-side gate to mirror; the one affordance the screen does withhold - the row delete - is withheld by a
 * measured display rule rather than by a permission check. Nothing else is drawn by hand: there is no bare
 * table, input or select in the paired template, and no value in the paired stylesheet resolves to anything
 * but a design token or a shared mixin.
 *
 * MIGRATION: no row is selectable, by measurement rather than omission - the legacy grid declared no
 * selected-item style, so it offered no such affordance.
 *
 * MIGRATION: FIVE COLUMNS ARE SORTABLE, AND THIS IS A NET-NEW AFFORDANCE RATHER THAN A PORTED ONE. It was
 * previously declined here on two grounds, and both have been re-measured. The first was that the legacy grid
 * declared no `AllowSorting`; that is true, and a case-insensitive census across BOTH legacy trees finds the
 * attribute exactly ONCE in either of them - in `Website/admin/Files/filemanager.ascx`, a screen the AAP
 * places out of scope - so not one in-scope legacy grid offered sorting at all. The census therefore says the
 * same thing about every grid in this application, including the module listing which has offered sorting
 * since it was written, so it cannot distinguish one screen from another and cannot support having the
 * affordance on one and not the rest. The second ground was that a sort control might name a field the
 * endpoint refuses; that risk is now measured away rather than avoided. The endpoint's permitted set is
 * declared in `backend/src/DnnMigration.Application/Validation/SortableFields.cs` as `Portals` and holds
 * exactly `PortalId`, `PortalName`, `ExpiryDate`, `HostFee` and `HostSpace`; the comparison is
 * case-insensitive, every one of the five is a column this grid already paints, and no other column declares
 * itself sortable - so no control here can produce a refused request. `Users`, `Pages` and `Aliases` remain
 * unsortable because the server cannot order by them: the first two are computed counts backed by no column
 * and the third is a collection, which that file records against the same set.
 *
 * MIGRATION: two of the legacy three page-level actions are dropped - exporting a portal template and
 * deleting expired portals - because no portal-template endpoint and no bulk-operation endpoint exist in the
 * target. Neither is faked with a client-side loop over the page in hand, which would silently act on one
 * page of a match set rather than on all of it.
 *
 * MIGRATION: filesystem deletion is out of scope. The legacy delete also removed per-portal resource files,
 * the alias-derived child folder and the portal's home directory from disk; the target's delete removes
 * database references only. Nothing here references a server path, and the portal read contract deliberately
 * publishes no absolute path for one to reference.
 *
 * MIGRATION: the page gate changes granularity. The legacy screen refused itself entirely to anyone who was
 * not a super user; the target expresses portal administration through the single `PortalAdministrator`
 * policy applied to the MUTATING routes, with this read-only listing inheriting authentication from its route
 * group. No super-user or host-level policy exists to reproduce, and inventing one would name a policy the
 * server has not registered, which it answers by throwing at request time. This component attaches no guard
 * of its own - guards belong to the feature's route table - and any client-side affordance rule below is
 * advisory, because the server re-authorises every request.
 */
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
    // The zero-result surface, which also carries the add action so an operator looking at an empty list is
    // not left without a way forward.
    EmptyStateComponent,
    // Shown only while the FIRST page is being read; later reads use the grid's own indicator so the rows
    // already on screen are not replaced by a spinner.
    LoadingSpinnerComponent,
    // The structured-failure surface, which carries its own live region.
    ErrorBannerComponent,
    // Renders the expiry column, and is the reason that column needs no formatter here.
    DateDisplayPipe,
  ],
  templateUrl: './portal-list.component.html',
  styleUrl: './portal-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalListComponent implements OnInit {
  // COLLABORATORS

  /**
   * The listing slice, its filter, its paging coordinates and its failures.
   */
  private readonly store = inject(PortalStore);

  /**
   * The signed-in session, consulted for ONE fact: which tenant is being browsed.
   *
   * That fact has no other home. The legacy row rule compared each row against `PortalSettings.PortalId`,
   * the tenant resolved for the current request, and the session store's own portal identifier is the direct
   * equivalent - it is documented as "the portal, or tenant, the caller is signed in to". The portal store's
   * selected identifier is a DIFFERENT fact - the portal an operator is editing - and using it here would
   * hide the delete affordance on whichever row happened to be selected.
   */
  private readonly session = inject(AuthStore);

  /**
   * The queue the delete outcome is announced through.
   */
  private readonly notifications = inject(NotificationService);

  /**
   * The route, read for the listing coordinates it carries.
   */
  private readonly route = inject(ActivatedRoute);

  /**
   * This screen's lifetime, so the address subscription ends with it.
   *
   * Needed explicitly because the subscription is opened in `ngOnInit`, which is NOT an injection context -
   * so the no-argument form of the unsubscribe operator is unavailable there and the reference has to be
   * captured here, where it is.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The router, written to when a coordinate changes.
   *
   * ⚠ THE ADDRESS IS THE ONLY WRITE PATH FOR LISTING STATE, AND THAT IS DELIBERATE. Every affordance on this
   * screen - a letter, a search, a page turn, a heading - navigates, and the address change is what reaches
   * the store. Writing the store directly AND navigating would issue two reads for one action; writing the
   * store WITHOUT navigating is what left the address stating a view the screen was not showing.
   */
  private readonly router = inject(Router);

  // CELL TEMPLATES
  //
  //  Static queries, so they resolve before `ngOnInit` and the column set can be assembled there. A template
  //  the host declares belongs to the host's view whether or not another component ends up rendering it,
  //  which is what makes this work.

  @ViewChild('editCommand', { static: true })
  private editCommandTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('deleteCommand', { static: true })
  private deleteCommandTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('aliasesCell', { static: true })
  private aliasesCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('expiresCell', { static: true })
  private expiresCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  @ViewChild('tallyCell', { static: true })
  private tallyCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  // THE HOSTING-FEE CELL
  //
  //  ⚠ MINOR (money differentiation) — the column was a FORMATTED column, which paints a string and can
  //  carry no per-value treatment at all, so a negative fee could not be told from a positive one. Rendering
  //  it through a template is what lets the negative case carry a mark; the characters painted are
  //  unchanged, because the template calls the same formatter the formatted column called.
  @ViewChild('hostFeeCell', { static: true })
  private hostFeeCellTemplate?: TemplateRef<DataTableCellContext<PortalListItem>>;

  // THE NAME FILTER CONTROL
  //
  //  Queried so that the box can be kept SHOWING the filter that is actually in force. Statically, because
  //  the control is declared unconditionally in the filter row and is therefore in the view before
  //  `ngOnInit` - which is when the first address arrives and the first adoption happens.

  /**
   * The free-text name filter control, so its displayed text can be reconciled with the filter in force.
   *
   * ⚠ THIS QUERY IS WHAT MAKES A CLEAR-SEARCH WORK, AND ITS ABSENCE WAS THE DEFECT. The shared control is
   * deliberately uncontrolled - it owns its own value and its own debounce, and it suppresses a REPEAT of the
   * term it last emitted so that the immediate submit path and the debounced tail cannot both query. Nothing
   * was ever telling it that the filter had changed underneath it, so three states could arise, all of them
   * measured at runtime: pressing a letter left the box empty while the listing was filtered; pressing `All`
   * cleared the listing but left the box showing the old text; and re-entering the screen replayed a previous
   * visit's filter behind an empty box, with no visible cause for the missing rows and nothing to clear.
   * Reconciling the box with the filter in force closes all three at once, and it also gives the control's
   * own duplicate suppression the right memory to compare against.
   */
  @ViewChild(SearchInputComponent, { static: true })
  private nameFilterControl?: SearchInputComponent;

  // State owned by this screen

  /**
   * The column descriptors, assembled once the cell templates have resolved.
   */
  private readonly columnSet = signal<readonly DataTableColumn<PortalListItem>[]>([]);

  /**
   * The row awaiting a deletion confirmation, or `null` when the dialog is closed.
   */
  private readonly pendingDeletion = signal<PortalListItem | null>(null);

  /**
   * The row whose deletion is awaiting an outcome, or `null` when nothing is in flight.
   *
   * Separate from {@link pendingDeletion} because the two answer different questions: one decides whether
   * the dialog is attached, the other decides whether an announcement is owed. Collapsing them would either
   * announce on cancellation or leave a confirmed deletion unannounced.
   */
  private readonly awaitedDeletion = signal<PortalListItem | null>(null);

  // Store-derived surface.

  /**
   * The rows of the page in hand.
   */
  protected readonly portals: Signal<readonly PortalListItem[]> = this.store.portals;

  /**
   * Whether a listing request is in flight.
   */
  protected readonly loading: Signal<boolean> = this.store.listLoading;

  /**
   * Whether a deletion is in flight, which the dialog and row buttons disable against.
   */
  protected readonly deleting: Signal<boolean> = this.store.detailLoading;

  /**
   * The active name filter, or `null` when unfiltered. Drives the strip's pressed state.
   */
  protected readonly activeFilter: Signal<string | null> = this.store.nameFilter;

  /**
   * The column key the listing is ordered by, or `null` for the server's own ordering.
   *
   * Bound INTO the shared grid, which never decides its own sort: the grid marks whichever heading matches
   * this key and reports activations back out, so the ordering in force is held in exactly one place - the
   * address, projected through the store - and the grid cannot drift from it.
   */
  protected readonly sortBy: Signal<string | null> = this.store.sortBy;

  /**
   * The direction the ordering is applied in, or `null` for the server's default.
   */
  protected readonly sortDir: Signal<SortDirection | null> = this.store.sortDir;

  /**
   * The page coordinate the pager paints.
   *
   * The REQUESTED index rather than the served one, which is exact parity: the legacy screen handed its
   * pager `CurrentPage`, the page the operator had asked for, not a value read back from the query.
   * Zero-based here, where the legacy counter was one-based - the base changed once, in the transport, and
   * no arithmetic is performed on either side of it.
   */
  protected readonly pageIndex: Signal<number> = this.store.pageIndex;

  /**
   * The page size the pager divides by.
   *
   * The size the SERVER applied, matching `Portals.ascx.vb`, which handed the pager the size actually in
   * use. This screen expresses no size preference of its own - see {@link PortalListComponent.ngOnInit} - so
   * the served size is the only size it knows.
   */
  protected readonly pageSize: Signal<number> = this.store.servedPageSize;

  /**
   * The size of the whole match set, as reported. Matches `Portals.ascx.vb`.
   */
  protected readonly totalCount: Signal<number> = this.store.totalCount;

  /**
   * Whether the pager is drawn.
   *
   * MIGRATION: this is the legacy pager predicate, preserved exactly rather than re-derived.
   * `Portals.ascx.vb` returns `True` from `SuppressPager`, and then reads `If SuppressPager And
   * ctlPagingControl.Visible Then ctlPagingControl.Visible = (PageSize < TotalRecords)`. The store publishes
   * that same comparison over the two coordinates the server reported, so the arithmetic is stated in one
   * place and this screen only decides whether to draw the control.
   */
  protected readonly pagerRequired: Signal<boolean> = this.store.pagerRequired;

  /**
   * Whether nothing matched at all - a reported total of nought.
   */
  protected readonly isListEmpty: Signal<boolean> = this.store.isListEmpty;

  /**
   * Whether records exist but the requested page holds none.
   */
  protected readonly isPastEnd: Signal<boolean> = this.store.isPastEnd;

  /**
   * The column descriptors, exposed read-only so a template cannot replace them.
   */
  protected readonly columns: Signal<readonly DataTableColumn<PortalListItem>[]> =
    this.columnSet.asReadonly();

  /**
   * The row awaiting confirmation. Its presence attaches the dialog.
   */
  protected readonly pendingRemoval: Signal<PortalListItem | null> =
    this.pendingDeletion.asReadonly();

  /**
   * Whether the grid has at least one row to draw.
   */
  protected readonly hasRows: Signal<boolean> = computed<boolean>(
    () => this.portals().length > 0,
  );

  /**
   * The range and total, restated in the page header.
   *
   * ⚠ P-M9 — THE PAGER STAYS WHERE THE LEGACY SCREEN PUT IT AND THE COUNT COMES UP TO MEET
   * THE OPERATOR.
   *
   * The finding is that the pager sits below the fold at 1280x900, measured at y = 908 on page
   * one - eight pixels past the edge, so nothing about paging is visible until the operator
   * scrolls. Every obvious remedy was rejected against a measured legacy authority:
   *
   *   - MOVING the pager above the grid contradicts `portals.ascx:L56-L58`, where the grid
   *     closes, a pair of line breaks follows and the paging control is declared after both.
   *     Position below the grid is the legacy arrangement and is kept.
   *   - SHORTENING the grid contradicts `Portals.ascx.vb:L273-L288`, whose `FormatPortalAliases`
   *     appended `<BR>` after every alias, so a portal with sixteen host names was sixteen lines
   *     tall in the legacy screen too. The tall row is legacy behaviour, not a defect, and the
   *     tenant this request arrives through is exactly that row on page one.
   *   - REDUCING the page size contradicts the page size the legacy screen served.
   *
   * What is left is DISCLOSURE, and that is what this adds: the same three facts the pager
   * already states, restated in the header, where they are above the fold at every viewport
   * because the header is the first thing on the screen. It is a NET ADDITION with no legacy
   * counterpart - the legacy screen stated the total only in the paging control - and is
   * recorded as one.
   *
   * The numbers come from THE SAME store signals the pager is handed, so the two cannot
   * disagree; nothing is recomputed here that the pager computes for itself except the range
   * ends, and those are derived from the served page size rather than from the row count, for
   * the same reason the pager derives them that way.
   *
   * UNDEFINED until a read has produced rows, so the header does not assert a count before one
   * exists - the same rule that keeps the pager from announcing ahead of its data. The header
   * treats an absent subtitle as nothing to render, so no empty element is left behind.
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

    // The first shown ordinal is one-based for the reader, and is derived from the page index
    // and the SERVED size rather than counted, because a page turn commits its index before its
    // rows and counting would report the previous page's range for one frame.
    const first = this.pageIndex() * size + 1;
    const last = first + shownCount - 1;

    return `${first}\u2013${last} of ${total}`;
  });

  /**
   * Whether to show the full-screen indicator instead of the grid.
   *
   * Only while the FIRST page is being read. A later read keeps the rows on screen and uses the grid's own
   * indicator, so a filter change or a page turn does not blank the table an operator is reading.
   */
  protected readonly showInitialSpinner: Signal<boolean> = computed<boolean>(
    () => this.loading() && this.portals().length === 0,
  );

  /**
   * The listing failure as an RFC 7807 document, or `null` when the last read succeeded.
   *
   * The document is passed WHOLE to the shared banner, which preserves the trace and correlation identifiers
   * an operator quotes when reporting a fault. Composed with an explicit null test rather than optional
   * chaining so that the two absences - no failure at all, and a failure that carried no document - stay
   * legible.
   */
  protected readonly listProblem: Signal<ProblemDetails | null> =
    computed<ProblemDetails | null>(() => {
      const failure: PortalFailure | null = this.store.listFailure();

      return failure === null ? null : failure.problem;
    });

  /**
   * Whether a listing failure is being reported at all, document or not.
   */
  protected readonly hasListFailure: Signal<boolean> = computed<boolean>(
    () => this.store.listFailure() !== null,
  );

  /**
   * Each row's host names, projected once per page rather than once per redraw.
   *
   * The alias cell has to become real anchor elements, and the projection that turns bare host names into
   * address-and-text pairs allocates. Deriving it here rather than in the per-row accessor means it runs
   * when the PAGE changes rather than on every change-detection pass, and the resulting objects keep their
   * identity so the template's tracked loop reuses its anchors instead of rebuilding them.
   *
   * Keyed by the portal identifier, which is unique per row on this contract.
   */
  private readonly aliasLinkIndex: Signal<ReadonlyMap<number, readonly PortalAliasLink[]>> =
    computed<ReadonlyMap<number, readonly PortalAliasLink[]>>(() => {
      const index = new Map<number, readonly PortalAliasLink[]>();

      for (const portal of this.portals()) {
        index.set(portal.portalId, toAliasLinks(portal.aliases));
      }

      return index;
    });

  /**
   * The wording for a listing failure that carried no readable document.
   *
   * The banner composes its own wording from a document; this covers the case where there was none, which is
   * what a request that never reached the server looks like.
   */
  protected readonly listFailureMessage: Signal<string> = computed<string>(() => {
    const failure: PortalFailure | null = this.store.listFailure();

    return failure === null
      ? ''
      : stripLeadingBreakTags(problemDetailsMessage(failure.problem, LIST_FAILED_MESSAGE));
  });

  // Wording, exposed for the template and for specifications

  /**
   * `ControlTitle_.Text`.
   */
  protected readonly heading = PAGE_TITLE;

  /**
   * `AddContent.Action`.
   */
  protected readonly addPortalLabel = ADD_PORTAL_ACTION;

  /**
   * The grid's clipped accessible name.
   */
  protected readonly gridCaption = GRID_CAPTION;

  /**
   * Accessible name for the first-letter filter group.
   */
  protected readonly filterStripLabel = FILTER_STRIP_LABEL;

  /**
   * Placeholder for the free-text name filter.
   */
  protected readonly searchPlaceholder = SEARCH_PLACEHOLDER;

  /**
   * The confirmation body: the global `DeleteItem.Text` question, then WHICH record it means.
   *
   * ⚠ THE MEASURED DEFECT. The body was the bare legacy sentence "Are You Sure You Wish To
   * Delete This Item?" and named nothing, while the dialog is a real modal that PHYSICALLY
   * COVERS the table - the row being destroyed included. An operator who reached a row command
   * by keyboard, sixteen stops in, had no way on screen to tell which of several near-identical
   * records was about to be destroyed, and the only irreversible action in the console was the
   * one asking them to confirm blind.
   *
   * MIGRATION: the legacy wording is KEPT VERBATIM as the question rather than replaced, because
   * it is the measured value of the global resource key the legacy grid attached to this column.
   * What changed around it is the mechanism: the legacy confirmation was a browser `confirm()`
   * raised from the row the operator had just clicked with a pointer, so the row and the prompt
   * were one gesture apart. This dialog is modal, backdropped and centred over the listing, so
   * the context the legacy prompt could take for granted has to be carried IN the prompt.
   *
   * Falls back to the bare question when no row is pending. That state is not reachable while
   * the dialog is on screen - it renders only when a row is pending - so the fallback exists to
   * keep the type honest rather than to be read.
   */
  protected readonly deleteConfirmMessage: Signal<string> = computed<string>(() => {
    const target: PortalListItem | null = this.pendingDeletion();

    if (target === null) {
      return DELETE_CONFIRM_MESSAGE;
    }

    return `${DELETE_CONFIRM_MESSAGE} ${target.portalName}`;
  });

  /**
   * Local `Edit.Text`, used as the row link's accessible name.
   */
  protected readonly editCommandLabel = EDIT_COMMAND_LABEL;

  /**
   * The twenty-seven filter entries, in legacy order.
   */
  protected readonly filterOptions = PORTAL_FILTER_OPTIONS;

  /**
   * The zero-result wording, chosen from the state that actually holds.
   *
   * Two distinct sentences rather than one, because the paging contract distinguishes "past the end" from
   * "nothing matched" and the two call for different remedies.
   */
  protected readonly emptyMessage: Signal<string> = computed<string>(() =>
    this.isPastEnd() ? PAST_END_MESSAGE : NO_PORTALS_MESSAGE,
  );

  /**
   * The route the page-level add action targets.
   */
  protected readonly addPortalLink: (string | number)[] = [PORTALS_SEGMENT, NEW_SEGMENT];

  /**
   * The legacy word an expired term is qualified with.
   */
  protected readonly expiredQualifier = EXPIRED_QUALIFIER;

  /**
   * The moment the expiry qualifiers are judged against, captured when the screen opens.
   *
   * A signal rather than a call to the clock inside the cell accessor, and the difference is not academic:
   * an accessor that reads the clock returns a different answer on every change-detection pass, so the
   * qualifier of a term expiring in the next second could flicker, and no test could pin the boundary. Held
   * as a signal so the whole page agrees with itself and so the boundary is reachable from a specification.
   *
   * NOT refreshed on a timer. The legacy screen resolved this once per server render, and a grid that
   * silently re-qualified a row while an operator was reading it would be a change nobody asked for.
   */
  private readonly today = signal<Date>(new Date());

  /**
   * The mark a tally cell paints in place of the absent-integer marker.
   */
  protected readonly absentTallyMark = ABSENT_TALLY_MARK;

  /**
   * The words behind that mark, announced but not painted.
   */
  protected readonly absentTallyDescription = ABSENT_TALLY_DESCRIPTION;

  /**
   * The name filter as this screen last asked for, or `undefined` when it has asked for none yet.
   *
   * ⚠ THIS IS AN ECHO GUARD AND REMOVING IT WOULD ERASE THE OPERATOR'S KEYSTROKES. The reconciliation effect
   * below writes the filter in force into the search box, and adopting a term also CANCELS whatever emission
   * the box has pending. That is exactly right when the filter changed somewhere else - a letter, a reset, a
   * back navigation - and exactly wrong for the echo of the box's own emission, because by the time the store
   * has settled the operator may already be typing the next word: adopting "ab" back into a box that now
   * reads "abc" would delete the "c" and cancel the query it had started. Recording what this screen asked
   * for lets the echo be recognised and ignored.
   */
  private ownFilterRequest: string | null | undefined = undefined;

  // CONSTRUCTION

  constructor() {
    // Keeps the search box showing the filter that is actually in force.
    //
    //  Reads the store rather than the address, because the store is what the grid is drawn from - so the box
    //  agrees with the ROWS even if a navigation is still settling. The write is `untracked` and goes through
    //  the control's adopt-without-emitting path, so it neither re-enters this effect nor issues a query for
    //  a filter that has already been applied.
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
    // Announces the outcome of a confirmed deletion exactly once.
    //
    //  The store's delete command invokes its continuation on success only, so a failure would otherwise pass
    //  unannounced. Reading the awaited row together with the in-flight flag and the failure covers both
    //  outcomes from one place: while the request is in flight there is nothing to say, and the moment it
    //  settles the failure signal already holds the verdict.
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
   * elements instead of rebuilding them.
   *
   * ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT. The grid's own fallback is the row
   * OBJECT, which is a correct key only while the same objects stay in play; every read from the server
   * decodes fresh objects, so without this a refetch of the same page presents entirely new keys and the
   * whole body is rebuilt to display records that never changed. `portalId` is unique by definition, being
   * the record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly portalRowKey = (row: PortalListItem): number => row.portalId;

  // LIFECYCLE

  /**
   * Assembles the column set, then reads the first page.
   *
   * In this order because the descriptors carry the cell templates, and a grid bound to rows before its
   * columns exist would render a table with no columns for one frame.
   *
   * MIGRATION: no page size is requested, which is a deliberate deferral rather than an omission of the
   * legacy value. `Portals.ascx.vb` returns the literal `20` with the per-portal `Records_PerPage` read
   * COMMENTED OUT - so the setting the legacy screen meant to honour was never consulted - while the account
   * listing, which did honour that setting, defaulted it to ten. Three answers, none of them authoritative
   * from here. Sending nothing lets the one authority for the effective size answer, and the pager then
   * divides by the size the server reports.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two requests for the same page on every entry.
    //
    // It also settles the stale-state defect at its root. The store is provided at the application root and
    // therefore OUTLIVES this route, so a previous visit's page, filter and ordering are all still held when
    // an operator returns; runtime testing measured a fresh sidebar click landing on page three of a filter
    // the operator could not see. Applying the whole query from the address means a bare `/portals` restores
    // the defaults and a `/portals?filter=X&currentpage=4` restores exactly that view, in both directions,
    // for a reload and for back and forward alike.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: PortalListQuery = parseListQuery(address);

        // An address that says something unusable is CORRECTED rather than obeyed silently, so that what is
        // on screen and what is in the address never disagree. The correction replaces the entry rather than
        // adding one - an operator pressing back should reach where they came from, not the uncorrected form
        // of where they already are - and it returns without reading, because the replacement navigation
        // emits again and that emission does the read.
        if (!addressStatesQuery(address, serialiseListQuery(query))) {
          void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: serialiseListQuery(query),
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });

          return;
        }

        // ⚠ THE ECHO GUARD IS NOT ARMED HERE, AND ARMING IT HERE WOULD DISABLE THE RECONCILIATION ENTIRELY.
        // Only the search box's own handler knows that a filter came from the box; every OTHER route to this
        // line - a letter, a reset, a back navigation, a typed address - is a filter the box has not seen and
        // must be shown.
        this.store.applyListQuery(query);
      });
  }

  // The first-letter filter strip

  /**
   * Applies one entry of the filter strip.
   *
   * MIGRATION: `All` CLEARS the filter rather than sending its own label. `Portals.ascx.vb` did exactly this
   *  - `If Filter = Localization.GetString("All") Then Filter = ""` - so the label never reached the query.
   *    It could not have: the comparison was against a LOCALISED string, and the target's store branches on
   *    no user-facing text at all. Absence is now stated as `null` rather than encoded as the empty string,
   *    because in the legacy contract the empty string WAS the absent string and the two were
   *    indistinguishable.
   *
   * MIGRATION: selecting an entry returns to the FIRST page, reproducing the legacy behaviour rather than
   * adding a convenience. `portals.ascx` bound each entry's address to `FilterURL(Container.DataItem, "1")`
   * - the literal `"1"`, the screen's one-based first page - and the store's filter command performs the
   * same reset. Holding the index would leave an operator on the fourth page of a match set that now has
   * one.
   *
   * MIGRATION: no address is composed here. `Portals.ascx.vb` emitted `"filter=" & Filter` and
   * `"currentpage=" & CurrentPage` into a navigation address, taking the page number as a `String` so the
   * compiler never checked it, and then read the key back CAPITALISED - relying on a case-insensitive query
   * lookup - and converted the text with `CType(..., Integer)` under strictness-off compilation. The filter
   * is client state here: it changes no address, its parameter spelling belongs to the one module that
   * serialises a query string, and the page index is a number throughout.
   *
   * @param option The entry chosen. A letter applies that letter; `All` clears the filter.
   */
  protected onFilterSelected(option: PortalFilterOption): void {
    // ⚠ THE PENDING EMISSION IS CALLED OFF FIRST, AND THE ORDER MATTERS. The search box holds its own
    // debounce, so an operator who types "bl" and then presses "C" a moment later would otherwise have the
    // elapsed delay emit "bl" AFTER this letter has been applied - the older intent silently replacing the
    // newer one, with "C" painted as selected over a listing of B. Cancelling here, before the navigation,
    // is what makes the newer intent win. The letter itself is adopted into the box by the reconciliation
    // effect once the store settles, so it is deliberately not passed here.
    this.nameFilterControl?.cancelPendingSearch();
    this.applyFilter(option.value);
  }

  /**
   * Whether a strip entry is the one currently applied.
   *
   * The clear-filter entry is active precisely when no filter is held. A letter is active on an exact match,
   * so typing free text into the name filter leaves every entry inactive - which is truthful: the list is
   * filtered, but by none of these entries.
   *
   * @param option The entry to test.
   * @returns True when the entry describes the filter in force.
   */
  protected isFilterSelected(option: PortalFilterOption): boolean {
    return this.activeFilter() === option.value;
  }

  /**
   * Applies the free-text name filter.
   *
   * MIGRATION: the text is forwarded BYTE FOR BYTE - untrimmed, its case unchanged and with NO pattern
   * character appended. `Portals.ascx.vb` concatenated a trailing wildcard at the call site, before the text
   * reached the data layer: `GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)`.
   * Composing the pattern now belongs entirely to the repository behind the server's data-access
   * abstraction, so contributing a character here would double whatever pattern it already builds and change
   * which rows match.
   *
   * MIGRATION: the PREDICATE is the legacy one, and getting there took a change on the SERVER rather than
   * here. The legacy pattern was anchored at the start of the name - `Portals.ascx.vb:L142` appends one
   * trailing wildcard and `04.04.00.SqlDataProvider:L245-L269` applies it with `LIKE @NameToMatch` - and an
   * intermediate revision of the repository had widened it to match anywhere in the title. That widening
   * loses no rows, but it costs the twenty-six-letter strip beside this box its entire meaning: against a
   * containment test, pressing `A` returns every title carrying an `a`. The repository now applies the prefix
   * test, the case folding and the wildcard hardening together, and the placeholder on this control states
   * the predicate rather than leaving a reader to infer it.
   *
   * Empty text means NO filter, which is the legacy test `If Filter <> ""` expressed against a type that can
   * say so. Whitespace-only text is NOT treated as empty, because the legacy test would have filtered on it
   * and the server is the one that trims.
   *
   * ⚠ SUBMITTING A QUERY THE ADDRESS ALREADY STATES ISSUES NO REQUEST, AND THAT IS DELIBERATE. Runtime
   * measurement of this exact case: on the bare, unfiltered first page with an empty box, pressing Search
   * produced ZERO requests to `/api/v1/portals`. The click itself was proven to land - `elementFromPoint`
   * resolved to the submit button, the event arrived with `isTrusted` true, the button took focus and the
   * shared control's `submit()` did emit - and `router.navigate` was then called with a target identical to
   * the current address, which Angular's default `onSameUrlNavigation: 'ignore'` correctly drops. Because
   * this screen's filter, page and ordering ALL live in the address, and the address is the only thing that
   * asks the store to read, a submit that changes no part of the address asks for nothing.
   *
   * It is left that way for three reasons, and the first is the strongest. The state the operator asked for
   * is ALREADY THE STATE ON SCREEN: the unfiltered first page is rendered, so a re-read would replace ten
   * rows with the same ten rows. Second, a redundant listing read is itself a defect this work was asked to
   * remove - the roles listing was refetching itself after every mutation and was corrected precisely so a
   * screen reads once per thing actually asked for. Third, closing it means detecting "this navigation will
   * be a no-op" BEFORE navigating and calling the store directly on that branch; a detector that answered
   * wrongly in the other direction would read twice for every search, which is the defect that was fixed.
   *
   * What is NOT left alone is the case that matters: CLEARING a filter that IS in force changes the address
   * from `?filter=...` to the bare path, so it navigates and re-reads. That is the behaviour the report
   * raised, and it is verified working.
   *
   * @param term The operator's text, exactly as typed.
   */
  protected onSearch(term: string): void {
    const wanted: string | null = term.length === 0 ? null : term;

    // Armed BEFORE the navigation, because the store settles synchronously once the address emits and the
    // reconciliation effect reads it immediately afterwards. See {@link ownFilterRequest} for why an echo of
    // this request must not be written back into the box the operator is still typing in.
    this.ownFilterRequest = wanted;
    this.applyFilter(wanted);
  }

  /**
   * Puts a filter into the address, returning to the first page.
   *
   * MIGRATION: returning to the first page reproduces the legacy behaviour exactly rather than adding a
   * convenience. `Portals.ascx.vb:L215-L222` composed the strip's address with `filter` and NO `currentpage`
   * pair, so the screen's counter fell back to the default seeded at `:L47` - `_CurrentPage As Integer = 1`,
   * the first page. Holding the index would leave an operator on the fourth page of a match set that now has
   * one.
   *
   * MIGRATION: the filter now travels in the ADDRESS, which is what the legacy screen did and what the
   * target had stopped doing. `FilterURL` at `:L215-L222` built a real navigation carrying `filter` and
   * `currentpage`, so every letter press was a history entry an operator could go back through. That is
   * restored here - `pushState` rather than `replaceState`, so back returns to the previous filter - while the
   * two behaviours the legacy could not offer are dropped: the page number is a number throughout rather than
   * a string read back under a different capitalisation, and no localised text is ever compared.
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
   * Moves to another page.
   *
   * The index is passed through untouched. It is ZERO-BASED on both sides of this call - the pager reports a
   * zero-based index and the store sends one - so there is no base to convert between and no off-by-one to
   * introduce. An out-of-range value is the server's to refuse with a field-level message rather than this
   * screen's to clamp, which is what keeps an operator's actual request visible instead of silently
   * substituted.
   *
   * @param pageIndex The page to read, counted from nought.
   */
  protected onPageChange(pageIndex: number): void {
    this.goToPage(pageIndex);
  }

  /**
   * Returns to the first page, offered from the past-the-end surface.
   */
  protected onReturnToFirstPage(): void {
    this.goToPage(FIRST_PAGE_INDEX);
  }

  /**
   * Puts a page into the address.
   *
   * A PUSHED entry rather than a replaced one, which is the legacy behaviour and the one an operator expects:
   * `Portals.ascx.vb:L215-L222` composed `currentpage` into a real navigation, so every page turn was a
   * history entry and back returned to the previous page. Runtime testing measured the target turning two
   * pages while the address stayed exactly `/portals`, which left back leaving the screen altogether.
   *
   * The address carries the page counted from ONE, as the legacy parameter did; the index is zero-based on
   * both sides of the store boundary. The conversion happens once, in {@link serialiseListQuery} and
   * {@link parsePageIndex}, so no other line has a base to convert between.
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
   * Reorders the listing, returning to the first page.
   *
   * MIGRATION: sorting is a NET ADDITION and the endpoint is what makes it offerable. The legacy grid was
   * declared without `AllowSorting` (`portals.ascx:L13-L20`), so it offered none - on 251 rows at ten to a
   * page, with the legacy screen's own page-size setting commented out, its only finding aid was the
   * twenty-six-letter strip. The collection endpoint accepts exactly five ordering fields and answers a sixth
   * with a field-level `400`, so precisely those five headings are offered and no more: an affordance that
   * produces a refused request is worse than no affordance. The direction token is the server's own member
   * name, spelled out in full because the binder rejects `asc`.
   *
   * The first page for the same reason a filter change returns to it: which page a row falls on depends on
   * the ordering, so holding the index would land an operator on a page of rows they have already seen.
   *
   * @param change The heading that was activated and the direction to apply. The shared grid decides the
   * direction - it toggles on the active column and starts ascending on any other - so this screen neither
   * remembers nor recomputes it.
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
   * Reads the tally a tally column paints.
   *
   * One template serves both tally columns - the shared grid hands a cell template the column it is
   * rendering, precisely so that it can - and this is where the column becomes a value. An unexpected key
   * fails loudly rather than painting a plausible wrong number, which is the same discipline
   * {@link requireTemplate} applies to a missing template.
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
      default:
        throw new Error(
          `The shared tally cell template was rendered for column "${columnKey}", which is not a tally ` +
            `column. It serves "${USERS_COLUMN_KEY}" and "${PAGES_COLUMN_KEY}" only.`,
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
   * MIGRATION: THE STATE IS A NET ADDITION AND THE DATE ITSELF IS UNCHANGED. `Portals.ascx.vb:L250-L260`
   * formatted the expiry and nothing else, so an expiry that had passed and one a lifetime away painted
   * identically - runtime testing measured `1/15/2020` and `12/31/2099` rendered in the same colour, the same
   * weight and with no other mark, on a screen whose whole purpose is to administer hosting terms. The DATE
   * remains exactly what the legacy painted, including the empty cell for an absent one; what is added is a
   * qualifier BESIDE it, in words as well as in colour, so the state is conveyed to a reader who cannot
   * distinguish the two and to one using a screen reader.
   *
   * @param portal The row.
   * @returns The state of the term.
   */
  protected expiryState(portal: PortalListItem): PortalExpiryState {
    return resolveExpiryState(portal.expiryDate, this.today());
  }

  // ROW AFFORDANCES

  /**
   * The settings route of every portal on the page, keyed by identifier.
   *
   * Precomputed once per page rather than per row per change-detection pass, and bound as an index rather
   * than called. The template renders one of these arrays into a router link on every row, and a link input
   * is compared by IDENTITY: an array built afresh each pass is a new reference every time, so the router
   * re-parses a target that has not changed, for every row, on every pass - work that grows with the page
   * size and that push change detection is supposed to avoid. Deriving the whole lookup from the page means
   * the references change only when the rows do.
   *
   * A plain object rather than a map, because a template can index one and cannot call `Map.get`; the point
   * is to remove the per-pass call, so an indexed read is what the binding needs. Bounded by construction:
   * it holds one entry per row of the CURRENT page and is rebuilt, not appended to, whenever the page
   * changes.
   *
   * MIGRATION: no numeric guard is applied to the identifier, and that is a correctness requirement rather
   * than terseness. `Portals.PortalID` is declared `[int] IDENTITY (-1, 1) NOT NULL`, so the FIRST portal an
   * installation ever creates is numbered `-1` and the second is numbered `0`; and `-1` is simultaneously
   * the legacy absent-integer marker, whose helper body is literally `Return -1`. One number therefore means
   * both "the first portal" and "no portal", and nothing a component can see distinguishes them. A
   * truthiness test would drop the second portal, a magnitude test would drop both, and a comparison against
   * the marker would drop the first. The value is used as the key and interpolated into the route exactly as
   * received, and a negative key is a legitimate one.
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
   * Whether the row may offer a delete affordance.
   *
   * MIGRATION: the row rule is preserved exactly. `Portals.ascx.vb` bound each row and set
   * `delImage.Visible = Not (portal.PortalID = PortalSettings.PortalId)`, so the portal the operator was
   * CURRENTLY BROWSING offered no delete affordance, which stops an operator deleting the tenant whose
   * administration screen they are standing in. The comparison is against the signed-in session's tenant,
   * the direct equivalent of the legacy per-request portal context.
   *
   * The comparison is a plain equality on the identity, never a magnitude test: both `0` and `-1` are real
   * portal identifiers here. When no tenant is resolved the identity is `null`, which equals no row, so
   * every row keeps its affordance - and the server remains authoritative either way, so an attempt it will
   * not permit is refused there rather than silently succeeding.
   *
   * @param portal The row.
   * @returns True when the row is not the tenant being browsed.
   */
  protected canDelete(portal: PortalListItem): boolean {
    return this.session.portalId() !== portal.portalId;
  }

  /**
   * The row's host names, ready to render as anchors.
   *
   * Read from a projection built once per page rather than recomputed per redraw, so the link objects stay
   * identity-stable and the template's tracked loop does not rebuild its anchors on every change-detection
   * pass.
   *
   * @param portal The row.
   * @returns One link per non-empty host name, in the order received.
   */
  protected aliasLinks(portal: PortalListItem): readonly PortalAliasLink[] {
    const projected: readonly PortalAliasLink[] | undefined = this.aliasLinkIndex().get(
      portal.portalId,
    );

    // A row absent from the projection can only mean the page has changed since it was built. An empty list
    // is the safe reading, and it is the shared empty list rather than a fresh allocation. This is a lookup
    // miss on a collection, not a coercion of an identifier - no portal identifier is substituted, defaulted
    // or compared here.
    return projected === undefined ? NO_ALIAS_LINKS : projected;
  }

  /**
   * The accessible name for one row's delete affordance.
   *
   * The affordances repeat down the column, so the portal's own title is what distinguishes them; an unnamed
   * repeated control reaches assistive technology as a list of identical commands. Invisible accessibility -
   * the painted label is unchanged.
   *
   * @param portal The row.
   * @returns The command's accessible name.
   */
  protected deleteCommandLabel(portal: PortalListItem): string {
    return `Delete ${nameForAnnouncement(portal.portalName)}`;
  }

  /**
   * The accessible name for one row's settings affordance.
   *
   * Built from the LOCAL resource value `Edit.Text` - "Edit this Portal", which overrides the terser global
   * `Edit.Text` of "Edit" - qualified by the row's own title for the same reason as the delete affordance.
   *
   * @param portal The row.
   * @returns The link's accessible name.
   */
  protected editCommandName(portal: PortalListItem): string {
    return `${EDIT_COMMAND_VISIBLE_LABEL}: ${nameForAnnouncement(portal.portalName)}`;
  }

  /**
   * The DESCRIPTION of one row's settings affordance - the legacy tooltip wording, which is no
   * longer its name.
   *
   * ⚠ MINOR (WCAG 2.5.3) — see {@link EDIT_COMMAND_LABEL}. Composed rather than constant so the
   * description names the row too, which is what the legacy tooltip could not do.
   *
   * @param portal The row.
   * @returns The link's description.
   */
  protected editCommandDescription(portal: PortalListItem): string {
    return `${EDIT_COMMAND_LABEL}: ${nameForAnnouncement(portal.portalName)}`;
  }

  /** @see EDIT_COMMAND_VISIBLE_LABEL */
  protected readonly editCommandVisibleLabel = EDIT_COMMAND_VISIBLE_LABEL;

  /**
   * Whether one row's hosting fee is below zero.
   *
   * ⚠ MINOR (money differentiation) — see {@link isNegativeFee} for why only this case is marked.
   *
   * @param portal The row.
   * @returns True when the fee is negative.
   */
  protected isFeeNegative(portal: PortalListItem): boolean {
    return isNegativeFee(portal.hostFee);
  }

  /**
   * The text of one row's hosting-fee cell.
   *
   * The SAME formatter the column used before this change, reached through the component so the
   * cell can be rendered from a template and still paint exactly the characters it did - two
   * fraction digits, no grouping, no currency symbol, reproducing
   * `portals.ascx:L47 DataFormatString="{0:0.00}"`.
   *
   * @param portal The row.
   * @returns The formatted fee, or an empty string when the fee is not a finite number.
   */
  protected hostingFeeText(portal: PortalListItem): string {
    return formatHostingFee(portal.hostFee);
  }

  /** @see NEGATIVE_FEE_QUALIFIER */
  protected readonly negativeFeeQualifier = NEGATIVE_FEE_QUALIFIER;

  // THE DELETE FLOW

  /**
   * Asks for confirmation before removing a portal.
   *
   * MIGRATION: the confirmation moves from a BROWSER DIALOG to an in-page one, and its wording is carried
   * across unchanged. `Portals.ascx.vb` assigned `imageColumn.OnClickJS =
   * Localization.GetString("DeleteItem")` - the GLOBAL key, with no local resource file argument - which the
   * framework emitted as a script-level `confirm(...)`. The shared dialog presents the same sentence with a
   * focus trap, an escape key and a named cancel affordance, none of which a browser confirmation offers.
   *
   * Recording the row rather than opening a flag: the confirmed step needs the identifier, and re-reading it
   * from a grid selection would make the dialog depend on the row still being where it was.
   *
   * @param portal The row whose delete affordance was pressed.
   */
  protected requestDeletion(portal: PortalListItem): void {
    this.pendingDeletion.set(portal);
  }

  /**
   * Closes the confirmation without removing anything.
   */
  protected onDeletionCancelled(): void {
    this.pendingDeletion.set(null);
  }

  /**
   * Removes the confirmed portal.
   *
   * MIGRATION: the page is RE-READ after a removal, reproducing the `BindData()` call at `Portals.ascx.vb`.
   * The store issues that re-read itself, having first removed the row from the page in hand so the grid
   * responds without waiting; the paging coordinates are deliberately left as last reported until the
   * re-read replaces them, because a locally decremented total would put a fabricated number where a
   * reported one belongs.
   *
   * The identifier is passed exactly as received - see the note on
   * {@link PortalListComponent.editSettingsLinks} for why no guard may be applied to it.
   *
   * THE COMMAND'S OUTCOME TICKET IS DELIBERATELY NOT SUBSCRIBED, and this is the one write
   * on the portal screens where that is correct. This screen has nothing to do on success
   * that the store has not already done: the row is gone from the grid by optimistic edit,
   * the listing is re-read by the store, and there is no navigation to perform because the
   * operator is already where they would be sent. The outcome that this screen DOES care
   * about — clearing {@link PortalListComponent.awaitedDeletion} — is settled from the
   * store's own state rather than from a continuation, so it settles identically whether the
   * write succeeded or failed.
   *
   * Ignoring the ticket costs nothing: the store subscribes to the transport itself, so
   * every state update happens whether or not anybody is listening.
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

  /**
   * Dismisses a reported listing failure without re-reading.
   */
  protected onFailureDismissed(): void {
    this.store.clearFailures();
  }

  /**
   * Re-reads the current page after a failure.
   */
  protected onRetry(): void {
    this.store.reloadPortals();
  }

  /**
   * Announces the outcome of a confirmed deletion.
   *
   * MIGRATION: the two outcomes keep their legacy severities, which are NOT the same. `Portals.ascx.vb`
   * announced success with `AddModuleMessage(GetString("PortalDeleted", LocalResourceFile),
   * ModuleMessageType.GreenSuccess)`, while announced the refusal with the same surface at
   * `ModuleMessageType.RedError`. Success is therefore success severity and the refusal is error severity,
   * in a queue whose vocabulary is the same three-valued one the legacy message type carried.
   *
   * MIGRATION: the refusal is recognised by STATUS rather than by comparing wording. The legacy screen
   * presented whatever string the data layer returned, so its "was this the last portal?" test was a string
   * test in all but name. The server now answers a refused deletion with a `409`, which is a fact about the
   * response rather than about a language, and the legacy sentence is supplied from this screen's own
   * constant.
   *
   * A permission refusal is deliberately NOT error severity. The legacy access-denied surface presented its
   * message at warning severity on both of its branches, so a `403` is announced as a warning: the system is
   * working exactly as configured, and danger styling would say otherwise. The classification comes from the
   * store's own severity, which resolves `401`, `403`, `404` and `429` to a warning.
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
   * Describes the grid's ten columns.
   *
   * The ORDER is the legacy markup order, `portals.ascx`: two command columns, then the identifier, the
   * title, the host names, the account count, the page count, the disk allowance, the fee and the expiry.
   *
   * Every key below is a stable identifier DISTINCT from the label the column paints, and the legacy
   * application drew the same distinction itself: `Localization.LocalizeDataGrid` looked each heading up as a
   * resource keyed by the column's own identity - `PortalId.Header`, `DiskSpace.Header`, `HostingFee.Header`
   *  - so identity was structure and the label was data. Two columns make the difference visible: the disk
   *    column is keyed `hostSpace` and painted "Disk Space", and the fee column is keyed `hostFee` and
   *    painted "Hosting Fee". Neither the keys nor the underlying contract members are renamed to match the
   *    labels. The shared grid also rejects a duplicated key outright, because a duplicate would make two
   *    headings and two cells share one tracking identity.
   *
   * The remaining five data columns declare only a body vertical alignment and inherit the centre. Vertical
   * alignment is not a member of the descriptor at all: the legacy body cells set it to the top essentially
   * uniformly, so the shared grid normalises it once in its own stylesheet.
   *
   * @returns The ten columns, in legacy order.
   * @throws Error if a required cell template is missing from the paired template file.
   */
  private buildColumns(): readonly DataTableColumn<PortalListItem>[] {
    return [
      // 1. `dnn:imagecommandcolumn CommandName="Edit" EditMode="URL" KeyField="PortalID"` An `actions`
      //   column, which suppresses row activation so that following the link never doubles as selecting the
      //   row.
      //
      //    The heading text is hidden rather than absent. The legacy column declared no `HeaderText`, and
      //    `LocalizeDataGrid` built its resource key from that value, so the column was simply skipped and
      //    reached assistive technology unnamed. Carrying a label and hiding it keeps the column named in
      //    the accessibility tree while painting nothing, so a cell is still announced with its column name.
      //
      //    Content-sized rather than given a track: the shared grid's contract requires an actions column to
      //    size itself, because a track narrower than the control it carries makes the control overhang its
      //    cell and paint over the next column.
      {
        key: 'edit',
        label: EDIT_COMMAND_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.editCommandTemplate, 'editCommand'),
      },

      // 2. `dnn:imagecommandcolumn commandname="Delete" keyfield="PortalID"` No `EditMode`, so this one
      //   really was a BUTTON - `Portals.ascx.vb` tests its control with
      //    `TypeOf... Is ImageButton`.
      //
      //    MIGRATION: the label is the GLOBAL `cmdDelete.Text`, which is "Delete". There is no `Delete.Text`
      //    entry in this screen's local resource file - the legacy code localised the column's text from its
      //    own `CommandName` against the local file and found nothing - so the global entry is the only
      //    wording that ever applied.
      {
        key: 'delete',
        label: 'Delete',
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.deleteCommandTemplate, 'deleteCommand'),
      },

      // 3. Template column over a label bound to the identifier, start-aligned in body AND heading.
      //
      //    A bound TEXT column, because the cell painted nothing but the number. The shared grid renders a
      //    finite number verbatim, which is what preserves the sentinel: an identifier of `-1` paints as
      //    `-1` and one of `0` paints as `0`, because both are real portals. Nothing here formats, pads or
      //    reinterprets the value.
      {
        key: 'portalId',
        label: PORTAL_ID_HEADING,
        headerAlign: 'start',
        bodyAlign: 'start',
        field: 'portalId',
        // Ordering: the key IS the endpoint's own sort name. See the sortability note on this class.
        sortable: true,
      },

      // 4. Template column over a label bound to the portal NAME, under the heading "Title" The key follows
      //   the contract member and the label follows the resource value; they differ, and both are correct.
      {
        key: 'portalName',
        // The row's NAME. Emitted as `<th scope="row">` so a screen reader announces which record
        // each cell belongs to - without it, traversing a row gives the column name and the value
        // and never the record's identity. This column is the one a person would read aloud to say
        // which row they mean. No visual change: the shared stylesheet restores a body row
        // header's normal weight.
        rowHeader: true,
        label: TITLE_HEADING,
        headerAlign: 'start',
        bodyAlign: 'start',
        field: 'portalName',
        sortable: true,
      },

      // 5. Template column over `FormatPortalAliases(...)`.
      //
      //    MIGRATION: an implicit conversion in the legacy markup, made explicit. reads
      //       `FormatPortalAliases(Convert.toInt32(DataBinder.Eval(Container.DataItem, "PortalID")))` - note
      //       the LOWER-CASE `t` in `Convert.toInt32`, which compiled only because the administration
      //       code-behinds were built with strictness off and the platform resolved the member
      //       case-insensitively. There is no late binding here: the host names arrive on the row already
      //       typed, and the row's own identifier needs no conversion because nothing looks a collection up
      //       by it.
      //
      //    MIGRATION: this is also where the legacy screen made its SECOND query per row.
      //       `FormatPortalAliases` constructed a `PortalAliasController` and called
      //       `GetPortalAliasArrayByPortalID` for EVERY row rendered, so a twenty-row page issued twenty-one
      //       reads. The host names now travel on the list row itself, so the page is one read. That is a
      //       consequence of the target contract rather than an optimisation applied to ported logic.
      //
      //     A template column rather than a formatted one, because the cell is a list of anchors and the
      //     shared grid's formatter returns text.
      {
        key: 'aliases',
        label: ALIASES_HEADING,
        headerAlign: 'start',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.aliasesCellTemplate, 'aliasesCell'),
      },

      // 6. `dnn:textcolumn DataField="Users"`.
      //
      //    MIGRATION: this is a COMPUTED TALLY and is bound raw because it cannot be negative.
      //       `Library/Components/Portal/PortalInfo.vb:L309-L318` seeds the backing field to
      //       `Null.NullInteger` but the GETTER resolves it - `If _Users < 0 Then _Users =
      //       UserController.GetUserCountByPortal(PortalID)` - and the legacy column bound the property, so
      //       the sentinel was replaced by a real count before it could ever be painted. The target read
      //       path computes the same tally from the membership rows, whose count has no negative case, so
      //       there is nothing to guard here and a guard would be unreachable code.
      {
        key: USERS_COLUMN_KEY,
        label: USERS_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.tallyCellTemplate, 'tallyCell'),
      },

      // 7. `dnn:textcolumn DataField="Pages"`. A computed tally like the account count above, but this one
      //   HAS a negative case, so it is formatted rather than bound.
      //
      //   ⚠ AN EARLIER NOTE HERE WAS FACTUALLY WRONG AND IS CORRECTED RATHER THAN QUIETLY DROPPED. It
      //     claimed the legacy column "bound the property RAW", so that a legacy screen would itself paint
      //     `-1` for an unresolved count, and concluded that painting `-1` was ported behaviour. The
      //     property is not raw: `PortalInfo.vb:L320-L329` resolves it through
      //     `TabController.GetTabCount(PortalID)` on any negative value, exactly as the account count does.
      //     So a normally provisioned portal never showed `-1` here - it showed a page count.
      //
      //   WHERE THE NEGATIVE ACTUALLY COMES FROM, measured rather than inferred. The terminal
      //     `GetTabCount` at `04.04.00.SqlDataProvider:L511-L525` is `SELECT COUNT(*) - 1 ... WHERE PortalID
      //     = @PortalID AND TabID <> @AdminTabId`, the subtraction excluding the administration page from
      //     the tally. A portal with no pages therefore answers `0 - 1`, and a portal whose `AdminTabId` is
      //     null answers the same because every row's inequality is then UNKNOWN. Confirmed against the
      //     running database: the portal on this screen holds `AdminTabId NULL` and zero `Tabs` rows, and
      //     the API answers `"pages": -1`. It is ARITHMETIC, not `Null.NullInteger` - the two are
      //     indistinguishable by value, which is why the QA report read it as the sentinel.
      //
      //   THE ARITHMETIC IS PRESERVED AND THE PRESENTATION IS FIXED, which is the only split that satisfies
      //     every rule at once. Migration discipline says a discovered legacy defect is annotated and NOT
      //     repaired, so the repository still returns what the legacy statement returned and the DTO still
      //     carries it - AAP Rule T7's "sentinels survive at the boundary". What Rule T7 does not licence is
      //     painting the artifact at an operator: `-1` is not a quantity of pages under any reading, and no
      //     remedy is suggested by it. A negative tally therefore renders as the empty string, which is what
      //     the shared grid's formatter contract prescribes for an absent value and what the other absent
      //     values in this application already use - no new glyph and no invented marker. A zero or positive
      //     tally reaches the cell verbatim.
      {
        key: PAGES_COLUMN_KEY,
        label: PAGES_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.tallyCellTemplate, 'tallyCell'),
      },

      // 8. `dnn:textcolumn DataField="HostSpace" HeaderText="DiskSpace"`, painted as "Disk Space" from
      //   `DiskSpace.Header`. Bound raw, exactly as the legacy did: this is an ALLOWANCE in megabytes and
      //   nought means no imposed limit, so it is neither formatted nor relabelled per value.
      {
        key: 'hostSpace',
        label: DISK_SPACE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        field: 'hostSpace',
        sortable: true,
      },

      // 9. `asp:BoundColumn DataField="HostFee" HeaderText="HostingFee" DataFormatString="{0:0.00}"`,
      //   painted as "Hosting Fee" from `HostingFee.Header`.
      //
      //   ⚠ MINOR (money differentiation) — A TEMPLATE COLUMN, not a formatted one, and the change is
      //   about treatment rather than text. The two fraction digits are still the legacy format string
      //   and are still produced by the same formatter, now reached from the template through
      //   `hostingFeeText`. What a formatted column could not do is give one VALUE a different
      //   appearance from another, which is why `-125.50`, `0.00` and `4321.99` were measured as
      //   pixel-identical apart from a single minus glyph.
      {
        key: 'hostFee',
        label: HOSTING_FEE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.hostFeeCellTemplate, 'hostFeeCell'),
        sortable: true,
      },

      // 10. Template column over `FormatExpiryDate(...)`.
      //
      //      MIGRATION: an absent expiry renders as an empty cell, never as `01/01/0001` and never as a
      //      placeholder word. `FormatExpiryDate` seeded its result with `String.Empty` and formatted only
      //      when `Not Null.IsNull(DateTime)` - and the legacy absent-date marker was
      //        `Date.MinValue`, which SURVIVES ON THE WIRE because the API omits nothing. The shared date
      //        pipe already answers both states with an empty cell, and it also answers an unparseable value
      //        that way rather than painting a confident wrong date, so the erasure is confined to the
      //        display layer while the contract keeps null and the minimum date distinguishable.
      //
      //      A template column rather than a formatted one so the pipe applies in the paired template, which
      //      is also the faithful shape - the legacy column was a template column too.
      {
        key: 'expiryDate',
        label: EXPIRES_HEADING,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.expiresCellTemplate, 'expiresCell'),
        // Ordered on the STORED date, so the rows an absent expiry paints as an empty cell still take their
        // place in the sequence the server produced rather than being reordered by the display rule.
        sortable: true,
      },
    ];
  }

  /**
   * Resolves one captured cell template, reporting the reference when it is missing.
   *
   * A template column with no template renders a blank cell on every row, which looks like missing DATA
   * rather than a mis-declared column. Failing with the reference name instead points straight at the line
   * to add.
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
