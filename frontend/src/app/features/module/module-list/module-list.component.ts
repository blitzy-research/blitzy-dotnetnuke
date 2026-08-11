import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  TemplateRef,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
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

import { ModuleVisibility } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { ModuleStore } from '../../../core/state/module.store';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { PaginationComponent } from '../../../shared/components/pagination/pagination.component';
import { SearchInputComponent } from '../../../shared/components/search-input/search-input.component';
import { HasPermissionDirective } from '../../../shared/directives/has-permission.directive';
import { DateDisplayPipe } from '../../../shared/pipes/date-display.pipe';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';

import type { OnInit, Signal } from '@angular/core';
import type { ModuleListItem } from '../../../core/models/module.model';
import type { SortDirection } from '../../../core/models/paged-result.model';
import type { PermissionKey } from '../../../core/models/permission.model';
import type { ModuleStoreFailure } from '../../../core/state/module.store';
import type {
  DataTableCellContext,
  DataTableColumn,
  DataTableSortChange,
} from '../../../shared/components/data-table/data-table.component';

// =====================================================================================================
// WORDING
// =====================================================================================================
//
// Every string a person reads on this screen is declared here as a named constant and bound, rather
// than written into the sibling template. Two reasons, and neither is stylistic:
//
//   1. A specification can assert rendered text against the SAME constant the screen supplies, instead
//      of restating it and letting the two drift.
//   2. Angular compiles templates with whitespace preservation disabled, which collapses runs of
//      whitespace inside a text node. An interpolated value is not collapsed, so wording that carries
//      significant spacing survives - and one value here genuinely does: the legacy delete
//      confirmation has a SPACE BEFORE ITS QUESTION MARK.
//
// MIGRATION: LOCALISATION IS NOT PORTED, AND THE RESOURCE FILES ARE A WORDING SOURCE ONLY. The three
//   `Website/admin/Modules/App_LocalResources/*.resx` files and
//   `Website/App_GlobalResources/SharedResources.resx` were read for the wording below and for nothing
//   else; none of the in-scope legacy localisation API calls is reproduced, no translation runtime is
//   present in the pinned dependency surface, and no message-tagging attribute or helper appears
//   anywhere in this file. Where a resource key exists its VALUE is authoritative; where none exists
//   the wording is authored directly and is marked as such below.
//
// MIGRATION: A RESOURCE VALUE IS NEVER TREATED AS MARKUP. A substantial minority of the legacy
//   resource values contain HTML - stored escaped, so invisible to a naive search - and at least one
//   carries a live third-party script. Every constant here is plain text, rendered through
//   interpolation or through a component input that interpolates. Nothing in this file marks a value
//   trusted, and no raw-markup binding appears.

/**
 * The page title.
 *
 * MIGRATION: AUTHORED, BECAUSE THERE IS NO LEGACY MODULE-LIST SCREEN TO TAKE IT FROM.
 *   `grep -rio "<asp:DataGrid" Website/admin/Modules/` returns ZERO across every file in that
 *   directory, which holds only `export.ascx`, `import.ascx`, `modulesettings.ascx`, their three
 *   code-behinds, an icon and `App_LocalResources`. `Website/admin/Modules/modulesettings.ascx`
 *   contains NINE `<table>` elements and ZERO `asp:datagrid` - every one of those tables is a
 *   `summary="… Design Table"` layout table, not a data grid. The eight real in-scope grids all live
 *   under `Website/admin/{Portal,Users,Security,Tabs}/`. No legacy source is manufactured for this
 *   screen: what IS derived from measurement is the column ORDER and the alignment discipline, both
 *   taken from `Website/admin/Portal/portals.ascx`, and the column LABELS, taken from
 *   `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`.
 */
const PAGE_TITLE = 'Modules';

/**
 * The sentence beneath the page title.
 *
 * States what a row IS, because that is the single most misread fact about this listing: a row is one
 * PLACEMENT of a module on one page, not one module. See {@link ModuleListComponent} for the
 * consequence.
 */
const PAGE_SUBTITLE = 'Every module placed on a page of this site, one row per placement.';

/** Accessible name of the grid, projected into the shared table's caption slot. */
const TABLE_CAPTION = 'Modules placed on this site';

/** Placeholder of the single filter control. */
const SEARCH_PLACEHOLDER = 'Search modules';

/**
 * Wording of the row commands.
 *
 * `Edit.Text` is a measured value from `Website/App_GlobalResources/SharedResources.resx`. The other
 * three have no resource key anywhere in the legacy tree and are authored.
 */
const COMMAND_LABEL = Object.freeze({
  /** `SharedResources.resx` key `Edit.Text`, value `'Edit'`. */
  edit: 'Edit',
  /** Authored. The legacy settings screen had no grid to be reached FROM. */
  settings: 'Settings',
  /** Authored. */
  export: 'Export',
  /** Authored; the legacy confirmation wording is on {@link REMOVE_CONFIRM_MESSAGE}. */
  remove: 'Delete',
});

/** Wording of the two page-level actions. Both authored - the legacy screen offered neither. */
const PAGE_ACTION_LABEL = Object.freeze({
  create: 'Add Module',
  import: 'Import Module',
});

/**
 * The glyph painted where a module has no title of its own.
 *
 * The em dash is the mark this application already uses for a value that is absent rather than zero
 * or empty - the same one the role listing paints in its period and fee columns and the portal
 * listing paints in its tally columns. It is `aria-hidden`, with {@link ABSENT_TITLE_DESCRIPTION}
 * carrying the meaning for a reader who cannot see it.
 */
const ABSENT_TITLE_MARK = '\u2014';

/**
 * What the absent-title mark means, for assistive technology only.
 *
 * MIGRATION: AUTHORED, because the legacy screen could not express this. `Modules.ModuleTitle` is
 * nullable and the legacy settings screen declared no presence validator on `txtTitle`
 * (`Website/admin/Modules/modulesettings.ascx`), so a module with no title is ordinary data rather
 * than a fault - and the legacy admin area had no module listing at all in which to show it.
 */
const ABSENT_TITLE_DESCRIPTION = 'no title recorded';

/**
 * Builds the phrase that identifies one module inside a command's accessible name.
 *
 * ⚠ THIS EXISTS BECAUSE THE DESTRUCTIVE COMMAND ANNOUNCED ITSELF AS `"Delete "`. Measured at
 * runtime: module 10 stores the EMPTY STRING as its title, the four row commands were named by
 * concatenating a verb with that title, and Chrome's accessibility tree consequently computed
 * `link "Edit "`, `link "Settings "`, `link "Export "` and `button "Delete "` - each with a trailing
 * U+0020 that Chrome does not trim. In a screen reader's control list none of them said which module
 * they acted on, and the one that destroys a placement said least of all. The row carried no
 * `<th scope="row">` to supply the context either.
 *
 * THE FALLBACK IS THE LEGACY'S OWN. `Library/Components/ControlPanel/ControlPanelBase.vb:192-196`
 * reads
 *
 *   `If title = "" Then`
 *   `    objModule.ModuleTitle = objModuleDefinition.FriendlyName`
 *   `Else`
 *   `    objModule.ModuleTitle = title`
 *   `End If`
 *
 * - it tests THE EMPTY STRING, exactly the condition measured here, and substitutes the module
 * definition's friendly name. So the first fallback is not invented; it is what the legacy platform
 * itself put in the field when no title was supplied. `ModuleController.vb:452-453` supplies the
 * ordering of the next one: a template's `<moduledefinition>` element carries
 * `ModuleDefinitionInfo.FriendlyName` while `<definition>` carries `DesktopModuleInfo.ModuleName`, so
 * the friendly name is the finer identifier and the package name the coarser.
 *
 * THE IDENTITY IS APPENDED WHENEVER A FALLBACK IS USED, and that is a deliberate addition rather
 * than a copy of the legacy substitution. A definition name is shared by every module instantiated
 * from it - the measured fixture has three definitions across two hundred and fifty modules - so the
 * friendly name alone would still not distinguish one row from another. The module's own identifier
 * is what makes the name unique, and `Modules.ModuleID` is `IDENTITY(0, 1)`, so nought is a
 * legitimate value and is never treated as absent.
 *
 * A TITLED ROW IS LEFT EXACTLY AS IT WAS. Its title already identifies it, appending an identifier
 * to two hundred and forty-nine names would add noise a reader must hear on every one of them, and
 * this function exists to close a gap rather than to restyle what already worked.
 *
 * @param row The module placement as the listing holds it.
 * @returns A non-empty phrase naming the module.
 */
function describeModule(row: ModuleListItem): string {
  const title: string = (row.moduleTitle ?? '').trim();

  if (title.length > 0) {
    return title;
  }

  // Both fall back to the definition, and both are trimmed: the measured fixture holds a definition
  // whose friendly name ends in a space (`QA010 日本語テスト 中文测试 `), which would otherwise put a
  // stray space in front of the identifier.
  const derived: string = ((row.friendlyName ?? '').trim() || (row.moduleName ?? '').trim()).trim();

  return derived.length > 0
    ? `${derived} (module ${row.moduleId})`
    : `module ${row.moduleId}`;
}

/**
 * Body of the removal confirmation.
 *
 * MEASURED, and the value is exact. `Website/App_GlobalResources/SharedResources.resx` declares
 * `DeleteModule.Confirm` as `'Are You Sure You Wish To Delete This Module ?'` - note the SPACE BEFORE
 * THE QUESTION MARK, which is preserved rather than tidied. The shared dialog's own default is the
 * more general `DeleteItem.Text`, `'Are You Sure You Wish To Delete This Item?'`; the module-specific
 * key exists, so it wins. Minimal Change Clause item 4 requires equivalent messages, and correcting
 * the spacing would be exactly the kind of opportunistic edit item 1 forbids.
 */
const REMOVE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Module ?';

/**
 * Column headings.
 *
 * MEASURED from `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`, whose fifty-nine
 * entries were read directly. The trailing colon of each resource value is STRIPPED: a colon belongs
 * to a form-field label sitting beside its control, not to a column heading sitting above a column of
 * values.
 *
 * MIGRATION: `plAllTabs.Text` reads `'Display Module On All Pages?'` - the legacy interface said
 *   PAGES, not TABS, even though the schema column is `dbo.Modules.AllTabs`. The heading follows the
 *   interface and the member name follows the column, which is why the two differ here.
 *
 * MIGRATION: `plCacheTime.Text` is `'Cache Time (secs):'`, with the unit inside the label - but NO
 *   cache column is declared on this screen, because the listing contract does not carry one. See
 *   {@link ModuleListComponent.buildColumns}.
 */
const COLUMN_LABEL = Object.freeze({
  /** Authored: the identity column has no resource key in the module resources. */
  moduleId: 'ID',
  /** `plTitle.Text`, `'Title:'`. The INSTANCE title an administrator typed. */
  moduleTitle: 'Title',
  /** `plFriendlyName.Text`, `'Module:'`. The DEFINITION's display name. */
  friendlyName: 'Module',
  /** Authored: no resource key names the installed package on this screen. */
  moduleName: 'Package',
  /** `plAllTabs.Text`, `'Display Module On All Pages?'`. */
  allTabs: 'All Pages',
  /** `plVisibility.Text`, `'Visibility:'`. */
  visibility: 'Visibility',
  /** Authored: the placement position has no resource key. */
  moduleOrder: 'Order',
  /** `plStartDate.Text`, `'Start Date:'`. */
  startDate: 'Start Date',
  /** `plEndDate.Text`, `'End Date:'`. */
  endDate: 'End Date',
  /**
   * Accessible name of the row-command column, announced but not painted.
   *
   * MIGRATION: legacy practice was inconsistent - of the eight in-scope grids exactly one labelled
   *   its command columns and `Website/admin/Portal/portals.ascx` supplied no heading text for its
   *   two (`:L21` and `:L22` declare no `HeaderText`). The shared table normalises that: every column
   *   carries a label and a flag decides whether it is painted, so the column stays named in the
   *   accessibility tree while showing nothing.
   */
  rowCommands: 'Actions',
});

/**
 * Wording of the three visibility states.
 *
 * MEASURED TWICE and the two agree. `Website/admin/Modules/modulesettings.ascx:L145-L147` declares
 * `<asp:listitem resourcekey="Maximized" value="0">Maximized</asp:listitem>` and the same shape for
 * `Minimized` at value 1 and `None` at value 2; `SharedResources.resx` independently declares
 * `Maximized.Text`, `Minimized.Text` and `None.Text` with those same three words.
 *
 * MIGRATION: THE ORDINALS ARE IMPLICIT IN THE LEGACY SOURCE AND ARE LOAD-BEARING.
 *   `Library/Components/Modules/ModuleInfo.vb:L30-L34` is, verbatim,
 *   `Public Enum VisibilityState / Maximized / Minimized / None / End Enum` - with NO explicit values,
 *   so 0, 1 and 2 come from declaration order alone and reordering the members would have silently
 *   remapped every stored row. The legacy markup above pins them to the numbers independently, which
 *   is what makes the mapping verifiable rather than inferred. The enumeration was renamed
 *   `ModuleVisibility` on the way across; the numbers were not touched.
 *
 * MIGRATION: `None` IS A REAL STATE, NOT AN ABSENCE. It means the placement renders without its
 *   container chrome - a choice the operator made, offered as the third of three radio options. ZERO
 *   is likewise a real state and the legacy default. Nothing in this file tests `visibility` for
 *   truthiness, coalesces it, or compares it against a bound; it is resolved through the total lookup
 *   below and nothing else.
 *
 * That lookup is declared as a `Record` KEYED BY THE ENUMERATION rather than as a switch, and that is a
 * deliberate choice rather than a stylistic one: a `Record<ModuleVisibility, string>` makes omitting a
 * member a COMPILE error, whereas a switch reports an unhandled case only indirectly. Totality is
 * therefore settled when this file is built rather than when a row is painted.
 */
const VISIBILITY_LABEL: Readonly<Record<ModuleVisibility, string>> = Object.freeze({
  [ModuleVisibility.Maximized]: 'Maximized',
  [ModuleVisibility.Minimized]: 'Minimized',
  [ModuleVisibility.None]: 'None',
});

/**
 * Confirmation shown once a removal has succeeded.
 *
 * Phrased about the PLACEMENT rather than about the module, and deliberately so: after a `204` the
 * module itself may well still exist. See {@link ModuleListComponent.onConfirmDelete}.
 */
const REMOVE_SUCCESS_MESSAGE = 'The module placement was removed.';

/**
 * The sort names the listing endpoint accepts, as this screen's sortable column keys.
 *
 * A column's key IS the sort name it sends, so a sortable column whose key the endpoint does not
 * recognise offers a control that produces a rejected request - which is worse than offering none.
 * These five are therefore not a guess: the server publishes the allow-list as its own
 * `SortableFields.Modules` set, under `backend/.../Application/Validation/SortableFields.cs`, built
 * over `StringComparer.OrdinalIgnoreCase` and containing exactly `ModuleId`, `ModuleTitle`,
 * `IsDeleted`, `StartDate` and `EndDate`; anything else is refused as `module.request_invalid`. The
 * comparison being case-insensitive is what lets the camel-case member names below serve as keys.
 *
 * The server's own note records why the rest of the projection is absent, and the reasoning applies
 * directly to this grid: the placement facts - `tabModuleId`, `tabId`, `moduleOrder`, `allTabs`,
 * `visibility` and `displayTitle` - have as many values as the module has placements, so there is no
 * single value to order a MODULE by; and the definition key together with `friendlyName` come from
 * the DEFINITION, which is resolved per row AFTER the page has been taken, so ordering by either
 * would order the page rather than the collection. Only four of the five appear as columns here -
 * `isDeleted` is permitted but is not rendered, for the reason given in
 * {@link ModuleListComponent.buildColumns}.
 */
const SORTABLE_KEY = Object.freeze({
  moduleId: 'moduleId',
  moduleTitle: 'moduleTitle',
  startDate: 'startDate',
  endDate: 'endDate',
});

/**
 * The column the endpoint orders by when the request names none.
 *
 * ⚠ THIS IS AN OBSERVED SERVER BEHAVIOUR, NOT A CHOICE MADE HERE, and it is recorded because the
 * screen has to tell the reader the truth about what they are looking at. A request carrying no sort
 * parameter returns the collection ordered by title ascending: measured by comparing a bare arrival
 * read against an explicit ascending read and finding the ten rows identical in identical positions.
 * The grid therefore reports this ordering when nothing else has been asked for, so its headings
 * announce the order that is actually rendered.
 *
 * It is deliberately NOT written into the request or into the address. Sending it would be sending
 * the server its own default back, and writing it to the address would make the screen navigate to
 * correct itself on arrival.
 */
const DEFAULT_SORT_KEY: string = SORTABLE_KEY.moduleTitle;

/** The direction {@link DEFAULT_SORT_KEY} is applied in by the endpoint's own default ordering. */
const DEFAULT_SORT_DIRECTION: SortDirection = 'Ascending';

/**
 * The search term, ordering and page this listing is showing, as the address states them.
 *
 * Held as one object because they are restored TOGETHER on entry: applying them one at a time would issue
 * one read per coordinate, and both the search and the ordering reset the page on their way through, which
 * would discard the page the address had just asked for.
 */
interface ModuleListAddressQuery {
  /** The search term, or `null` for none. Carried byte for byte; the server is the one that trims. */
  readonly query: string | null;

  /** The page to read, counted from nought. */
  readonly pageIndex: number;

  /** The column to order by, or `null` for the server's own ordering. */
  readonly sortBy: string | null;

  /** The direction, or `null` for the server's default. Always `null` when there is no column. */
  readonly sortDir: SortDirection | null;
}

/**
 * Reads the whole listing query out of an address.
 *
 * @param address The route's query parameters.
 * @returns The query to apply, with every unusable value resolved to its default.
 */
function parseModuleListQuery(address: ParamMap): ModuleListAddressQuery {
  // The admitted set is the ENDPOINT'S, and it is the same object the columns are keyed by, so a column
  // cannot become sortable without the address accepting its key in the same change.
  const sortBy: string | null = parseSortKey(address.get(SORT_BY_PARAM), Object.values(SORTABLE_KEY));

  return {
    query: address.get(FILTER_PARAM),
    pageIndex: parsePageIndex(address.get(PAGE_PARAM)),
    sortBy,
    // A direction with no column to apply it to is dropped rather than kept, so the address cannot carry
    // half an ordering. A column with no direction is kept: the endpoint has a default.
    sortDir: sortBy === null ? null : parseSortDirection(address.get(SORT_DIR_PARAM)),
  };
}

/**
 * Writes a listing query back out as address parameters.
 *
 * A default coordinate is emitted as `null`, which the router REMOVES from the address rather than writing
 * as an empty value - so an unsearched, unordered first page is the bare path.
 *
 * @param query The query in force.
 * @returns The parameters to merge into the address.
 */
function serialiseModuleListQuery(query: ModuleListAddressQuery): Params {
  return {
    [FILTER_PARAM]: query.query,
    [PAGE_PARAM]: firstPageParameter(query.pageIndex),
    [SORT_BY_PARAM]: query.sortBy,
    [SORT_DIR_PARAM]: query.sortBy === null || query.sortDir === null ? null : query.sortDir,
  };
}

/**
 * Route addresses this screen links to.
 *
 * Assembled as plain path strings and handed to `routerLink`. No router is injected and no
 * navigation is performed imperatively: every cross-screen movement on this screen is a link a person
 * can middle-click, copy and bookmark, which a programmatic navigation is not.
 */
const ROUTE = Object.freeze({
  /** The listing's own root, and the prefix of every per-module address. */
  modules: '/modules',
  /** The create screen. */
  create: '/modules/new',
  /** The import screen. */
  import: '/modules/import',
  /** Sub-path of the per-placement settings screen. */
  settings: 'settings',
  /** Sub-path of the per-placement export screen. */
  export: 'export',
});

/**
 * The module listing, and the navigational entry point for every other module screen.
 *
 * Mounted at `/modules`. Reads a page of module placements from {@link ModuleStore}, paints them in
 * the shared table, and offers three per-row links plus a removal, together with two page-level
 * links. It performs no HTTP call of its own, builds no URL against the API, assembles no query
 * string and sets no request header: the layering is component to store to service to transport, and
 * the correlation identifier every request carries is written by the shared interceptor.
 *
 * ## A ROW IS A PLACEMENT, NOT A MODULE
 *
 * The single most consequential fact about this screen, and it is the listing contract's own
 * statement rather than an interpretation: one module whose all-pages flag is set appears on every
 * page of the site and therefore contributes ONE ROW PER PAGE, each with its own `tabModuleId` and
 * its own `moduleOrder`, while `moduleId` repeats across them. Every consequence below follows from
 * that - the removal addresses a placement, the success wording speaks of a placement, and half the
 * projection is deliberately not sortable because a module has as many values for those members as it
 * has placements.
 *
 * ## MIGRATION REGISTER
 *
 * Every deliberate difference from the legacy application is stated once here and annotated again at
 * the line that causes it. None is absorbed silently. The register is deliberately blunt about the
 * two facts that are easiest to get wrong: this screen has no legacy ancestor, and its removal is
 * soft.
 *
 * MIGRATION: THIS SCREEN IS WHOLLY NEW AND NO LEGACY SOURCE IS MANUFACTURED FOR IT.
 *   `grep -rio "<asp:DataGrid" Website/admin/Modules/` returns ZERO. That directory holds
 *   `export.ascx`, `import.ascx`, `modulesettings.ascx`, `Export.ascx.vb`, `Import.ascx.vb`,
 *   `ModuleSettings.ascx.vb`, one icon and `App_LocalResources` - and nothing that lists modules.
 *   `Website/admin/Modules/modulesettings.ascx` contains nine `<table>` elements and zero
 *   `asp:datagrid`; all nine are `summary="… Design Table"` layout tables. The eight genuine in-scope
 *   grids live under `Website/admin/{Portal,Users,Security,Tabs}/`. What IS measured, and is honoured
 *   exactly, is the grid CONVENTION of `Website/admin/Portal/portals.ascx` and the column WORDING of
 *   `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`.
 *
 * MIGRATION: THE FREE-TEXT FILTER IS NET-NEW; THE LEGACY HAD NO MODULE SEARCH OF ANY KIND.
 *   `Library/Components/Modules/ModuleController.vb:L1032` declares
 *   `Public Function GetSearchModules(ByVal PortalId As Integer) As ArrayList`, which is the
 *   SEARCH-INDEXING surface of the legacy searchable-module contract - it returns the modules that
 *   SUPPORT search so the indexer can walk them - and is emphatically not a user-facing query.
 *   Nothing in the legacy module screens filtered a module list by text, because there was no module
 *   list to filter. See
 *   {@link ModuleListComponent.onSearch} for what is and is not done to the text.
 *
 * MIGRATION: THE REMOVAL IS SOFT, TWO-TIERED, AND THE ROW MAY LEGITIMATELY SURVIVE IT.
 *   `ModuleController.vb:L837` `DeleteTabModule(TabId, ModuleId)` hard-deletes only the per-page
 *   reference row (`:L843`), reorders the survivors on that page (`:L846`), and then tests whether any
 *   other page still references the module (`:L849`). ONLY IF NONE DOES does it soft-delete: the
 *   source's own comment at `:L850` reads verbatim `' soft delete the module`, followed by
 *   `objModule.TabID = Null.NullInteger` (`:L851`), `objModule.IsDeleted = True` (`:L852`) and
 *   `UpdateModule(objModule)` (`:L853`). Consequently a `204` does NOT imply the row disappears, and
 *   nothing here prunes it - see {@link ModuleListComponent.onConfirmDelete}.
 *
 * MIGRATION: THE LEGACY DOCUMENTATION CONTRADICTS THE LEGACY CODE, AND THE DEFECT IS ANNOTATED RATHER
 *   THAN FIXED. `ModuleController.vb:L829` states `''' Delete a module reference permanently from the
 *   database.` while `:L850`, sixteen lines below it in the same routine, says `' soft delete the
 *   module`. The code is authoritative and the comment is wrong. Minimal Change Clause item 1 forbids
 *   the opportunistic correction of a discovered legacy defect, so it is recorded and left alone.
 *   Note also that `DeleteModule` at `:L819` IS the hard delete - and is NOT the endpoint this screen
 *   calls.
 *
 * MIGRATION: THERE IS NO RESTORE, RECYCLE-BIN, PURGE OR UNDELETE AFFORDANCE, because there is no such
 *   endpoint. The legacy controller contains no `RestoreModule` of any kind, and the target surface
 *   exposes nothing that reverses a removal. A soft-removed module simply stops appearing.
 *
 * MIGRATION: `GetPortalTabModules` IS OBSOLETE AND IGNORES ITS PORTAL ARGUMENT, so it is not the
 *   authority for anything here. `ModuleController.vb:L1423` sits inside `#Region "Obsolete"` (opened
 *   at `:L1416`) and is decorated `<Obsolete("Use the new GetTabModules(ByVal TabId As Integer)")>` at
 *   `:L1422`; its body at `:L1424-L1428` walks `GetTabModules(TabId)` alone and NEVER READS
 *   `PortalId`. The non-obsolete authorities are `GetTabModules(TabId)` and, for a whole-site listing,
 *   `GetModules(PortalID)` at `:L915` and `GetModules(PortalID, IncludePermissions)` at `:L928`. That
 *   the target listing is addressed by tenant AND optionally by page is a target convenience, not
 *   preserved legacy behaviour, and permissions are never requested because no permission-mutation
 *   endpoint exists.
 *
 * MIGRATION: THE CACHE PERIOD IS ABSENT FROM THE LISTING, AND THE TWO CACHE FIELDS WERE NEVER
 *   INTERCHANGEABLE ANYWAY. `ModuleInfo.vb` initialises `_CacheTime = 0` at `:L731` and
 *   `_DefaultCacheTime = -1` at `:L759` - two different values in one constructor - with accessors at
 *   `:L203` and `:L482`. They are distinct facts and no expression anywhere may read one as the
 *   other's fallback. On this screen the point is moot in the safest possible way: the listing
 *   contract carries NEITHER, so no cache column exists and no coalescing expression is even
 *   representable. The `plCacheTime.Text` heading measured from the resource file has no column to
 *   sit above.
 *
 * MIGRATION: A MINIMUM-VALUE DATE RENDERS BLANK, WHICH IS EXACTLY WHAT THE LEGACY SCREEN DID.
 *   `Website/admin/Modules/ModuleSettings.ascx.vb:L152-L153` reads
 *   `If Not Null.IsNull(objModule.StartDate) Then txtStartDate.Text =
 *   objModule.StartDate.ToShortDateString`, and `:L155-L156` does the same for the end date - the
 *   field was LEFT BLANK on the sentinel rather than showing a minimum date.
 *   `Library/Components/Shared/Null.vb:L66-L70` defines that sentinel as `Date.MinValue`. The shared
 *   display pipe reproduces the behaviour in its default short-date mode, so the two date columns pass
 *   the value straight through it and this file contains no date guard of its own. A far-future date
 *   such as `9999-12-31` is a REAL value and is not blanked.
 *
 * MIGRATION: ORDERING, PANE PLACEMENT, COPYING, BULK REMOVAL, CACHE CLEARING AND SYNCHRONISATION HAVE
 *   NO ENDPOINT AND THEREFORE NO AFFORDANCE. Each exists in the legacy controller and none survives
 *   into the target surface: `MoveModule` (`:L1078`), `CopyModule` (`:L700` and `:L743`),
 *   `DeleteAllModules` (`:L795`), `UpdateModuleOrder` (`:L1160`), `UpdateTabModuleOrder` (`:L1197`),
 *   `CopyTabModuleSettings` (`:L764`), `SynchronizeModule` (`:L614`) and the three cache members
 *   (`:L478`, `:L482`, `:L486`). This screen therefore offers no drag-to-reorder, no pane picker, no
 *   bulk selection, no copy command and no cache-clear button. The placement position is DISPLAYED as
 *   data and is not editable.
 *
 * MIGRATION: THE EXPORT COMMAND IS NOT GATED CLIENT-SIDE, BECAUSE THE LISTING CARRIES NO PORTABILITY
 *   SIGNAL. `ModuleInfo.vb:L608` declares `IsPortable` as an `<XmlIgnore()> ReadOnly` property
 *   returning `GetFeature(DesktopModuleSupportedFeature.IsPortable)` over the `SupportedFeatures`
 *   bitmask at `:L446`, alongside `IsSearchable` (`:L614`) and `IsUpgradeable` (`:L620)`. The listing
 *   contract exposes NEITHER a portability flag NOR the bitmask, so the command is always offered and
 *   the server answers authoritatively when a module cannot be exported. The bitmask is emphatically
 *   not recomputed here: this file contains no bitwise expression of any kind.
 *
 * MIGRATION: A REFUSAL IS REPORTED AT WARNING SEVERITY, AND THIS COMPONENT IS THE ONE THAT REPORTS IT.
 *   The store records failures structurally and notifies nobody - it injects only the two transports -
 *   so there is no double-notification hazard and the reporting duty falls here. The severity is NOT
 *   decided here either: it arrives already resolved on the failure's summary, where the shared
 *   resolution utility maps a refusal to a warning on the measured legacy evidence that
 *   `Website/admin/Security/AccessDenied.ascx.vb` used `ModuleMessageType.YellowWarning` in BOTH of
 *   its branches. This file passes that severity through unaltered.
 *
 * MIGRATION: VIEW-STATE AND SESSION ROUND-TRIPPING DISAPPEAR WITH NOTHING TO PORT. Measured:
 *   `Website/admin/Modules/` contains ZERO view-state call sites, and session-state access appears
 *   nowhere in scope at all - so this is a clean case rather than a translation. Filters, the page
 *   coordinate and the ordering live in the store as signals, no post-back re-bind exists to
 *   reproduce, and the screen holds exactly one piece of local state of its own.
 */
@Component({
  selector: 'app-module-list',
  standalone: true,
  // Eleven members, and every one of them is used by the sibling template.
  //
  // `RouterLink` is REQUIRED even though no `<a routerLink>` appears in this file: the row commands
  // are declared as an `ng-template` here and CONTENT-PROJECTED into the shared table, and the shared
  // table's own documentation states that such a template is "compiled in the CALLER's template
  // context, not this component's", which is precisely why that component imports no directive and no
  // pipe of its own. The two pipes are in this list for the same reason.
  //
  // The built-in control-flow blocks need no import, so `CommonModule` is deliberately absent - as
  // are the shared spinner and empty-state components, which the table renders itself from its own
  // imports and which would be duplicated if they appeared here.
  //
  // `NgTemplateOutlet` and `HasPermissionDirective` are the two members the row-command GATE needs,
  // and both are genuinely used by the sibling template rather than declared speculatively. The
  // template renders the commands through `@if (holdsAdministration())` with the directive as the
  // second arm, and the command markup itself is declared once in a separate `ng-template` that both
  // arms render through the outlet — so removing either import breaks the gate rather than merely
  // silencing a warning.
  //
  // ⚠ THIS SCREEN IS THE SHARED DIRECTIVE'S ONE CORRECT FEATURE CONSUMER, and that is why the gate
  // lives here and nowhere else. Every OTHER affordance that might appear to want it addresses a
  // route declared under the `PortalAdministrator` POLICY, which the API answers from
  // `Portals.AdministratorRoleId` rather than from a persisted grant row — those screens therefore
  // gate on the store's tenant-administration determination alone, and applying the directive to
  // them would test a grant vocabulary their endpoints never consult. The directive is the shared
  // library's documented instrument for the four PERSISTED keys (`VIEW`, `EDIT`, `READ`, `WRITE`),
  // which are grants over a module or page INSTANCE, and these row commands are the one place that
  // vocabulary genuinely applies — they are `ModuleEdit` on the API, answered from
  // `ModulePermissions`. See `holdsAdministration` below for why the key cannot be the only arm.
  imports: [
    NgTemplateOutlet,
    RouterLink,
    PageHeaderComponent,
    SearchInputComponent,
    DataTableComponent,
    PaginationComponent,
    ConfirmDialogComponent,
    ErrorBannerComponent,
    HasPermissionDirective,
    YesNoPipe,
    DateDisplayPipe,
  ],
  templateUrl: './module-list.component.html',
  styleUrl: './module-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleListComponent implements OnInit {
  // ---------------------------------------------------------------------------------------------------
  // DEPENDENCIES
  // ---------------------------------------------------------------------------------------------------
  //
  // Two, and both are application-wide singletons resolved from the root injector. No `providers`
  // array appears on this component: a component-level provider would give this screen its own store
  // instance and silently detach it from every other module screen.
  //
  // Deliberately NOT injected: the transport service, because the store owns every call this screen
  // needs; the router, because navigation here is a link rather than an imperative call; the HTTP
  // client, which this layer must never see; and the activated route, because the listing route
  // carries no parameter.

  /** The single source of truth for the listing, its filters and its failures. */
  private readonly store = inject(ModuleStore);

  /** The address this screen reads its search, ordering and page from, and writes them back to. */
  private readonly route = inject(ActivatedRoute);

  /** Used to write the listing coordinates into the address rather than holding them privately. */
  private readonly router = inject(Router);

  /** Ties the address subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  /** The transient-message channel. */
  private readonly notifications = inject(NotificationService);

  /** The session store, read only for the permission composition documented below. */
  private readonly authStore = inject(AuthStore);

  // ---------------------------------------------------------------------------------------------------
  // THE COMPOSED PERMISSION GATE ON THE ROW COMMANDS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Whether the caller administers this tenant.
   *
   * ⚠ THE FIRST ARM OF A TWO-ARM GATE, AND OMITTING IT WOULD HIDE WORKING CONTROLS. The four row
   * commands are all `ModuleEdit` on the API, so the persisted `EDIT` key is what governs them and
   * the shared permission directive is the right instrument — but the key list the client holds is
   * derived from GRANT ROWS ALONE. A portal administrator who has never been named in a grant row
   * holds no keys at all, and the API admits them to every one of these operations anyway, because
   * each of those policies has an administrator arm. Gating on the key by itself therefore removes
   * four working affordances from precisely the operator the screen exists for. Measured, not
   * assumed: on the seeded baseline the `admin` account holds the `Administrators` role and ZERO
   * portal-level permission keys.
   *
   * So the gate is `administration OR key`, and the two arms are declared in the template as the two
   * branches of one `@if`, with the command markup declared ONCE in a separate `ng-template` that
   * both branches render through `ngTemplateOutlet`. Duplicating the markup per branch was the
   * alternative and it is worse: four controls, their accessible names and their route builders would
   * exist twice and would drift.
   *
   * Read from the store rather than derived here, because the route gate asks the same question to
   * decide whether this screen may be entered at all — see `AuthStore.holdsPortalAdministration`.
   */
  protected readonly holdsAdministration: Signal<boolean> = this.authStore.holdsPortalAdministration;

  /**
   * The persisted key that admits the row commands, for the second arm of the gate.
   *
   * ⚠ THE UNION SEMANTICS ARE WHAT MAKE THIS CORRECT. The key list the client holds is the union of
   * everything the caller holds ANYWHERE in the portal — the API's own register calls it "the set an
   * administration shell needs to decide which sections to offer" — so a caller granted `EDIT` on a
   * single module holds `EDIT` here and keeps the commands. The gate is coarser than the server's
   * per-record answer by design: it decides whether to offer the column at all, and the server
   * decides each request.
   *
   * Typed as the contract's key union rather than as a bare string, so a mis-spelling is a compile
   * error. The directive itself accepts a plain `string` on purpose, because an installation may
   * carry a key this codebase has never seen; narrowing it HERE costs nothing, because this screen
   * names one of the four keys the product defines.
   */
  protected readonly editPermissionKey: PermissionKey = 'EDIT';

  // ---------------------------------------------------------------------------------------------------
  // CELL TEMPLATES
  // ---------------------------------------------------------------------------------------------------
  //
  // Four cells cannot be plain bound text, and the shared table's column contract says so outright: a
  // text column is for "a string, a number or a large integer" and "a boolean, a date object or a
  // nested object must go through" a formatter or a template column. Three of these four also need a
  // shared PIPE, which is a template construct and has no expression in a formatter function - so a
  // template column is the only shape that fits.
  //
  // STATIC QUERIES, and that is load-bearing rather than incidental. A static view query resolves
  // BEFORE `ngOnInit` runs, which is what allows the column set - whose descriptors must hold real
  // template references - to be assembled there and bound in the very first change-detection pass. A
  // non-static query would still be unresolved at that point and the table would receive a set with
  // absent templates.
  //
  // Each is typed against the row contract, so a template declared for the wrong row shape is a
  // compile error rather than an empty cell. Each is optional here and is unwrapped through
  // {@link requireTemplate}, because the declaration lives in a SIBLING file this component does not
  // own: an omitted `ng-template` must fail loudly and name what is missing, not paint blank cells.

  /** The three per-row links and the removal command. */
  @ViewChild('rowCommands', { static: true })
  private rowCommandsTemplate?: TemplateRef<DataTableCellContext<ModuleListItem>>;

  /**
   * The failure banner, so a new failure can be brought into view.
   *
   * ⚠ THE BANNER RATHER THAN THE COMMANDS BESIDE IT, and the difference was measured. Anchoring on
   * the command region scrolled the commands flush to the top of the viewport and left the banner at
   * top −130.5 / bottom −32.5 — wholly above it — so the operator was shown two buttons and no
   * reason for them. The banner is always in the DOM (null is its documented empty state), so this
   * query needs no `static: false` special case and resolves once for the component's whole life.
   */
  @ViewChild('failureBanner', { read: ElementRef })
  private failureBanner?: ElementRef<HTMLElement>;

  /**
   * The shared search control, so the box can be reconciled with the filter in force.
   *
   * `static: true` because the reconciling effect in the constructor runs before the first change
   * detection completes, and a control resolved later would miss the filter it arrived with.
   */
  @ViewChild(SearchInputComponent, { static: true })
  private searchControl?: SearchInputComponent;

  /**
   * The query this screen last asked for, or `undefined` when it has asked for none yet.
   *
   * ⚠ THIS IS AN ECHO GUARD AND REMOVING IT WOULD ERASE THE OPERATOR'S KEYSTROKES. The reconciling
   * effect writes the filter in force into the search box, and adopting a term also CANCELS whatever
   * emission the box has pending. That is exactly right when the filter changed somewhere else - a
   * back navigation, a fresh arrival - and exactly wrong for the echo of the box's own emission,
   * because by the time the store has settled the operator may already be typing the next word:
   * adopting `"ab"` back into a box that now reads `"abc"` would delete the `"c"` and cancel the
   * query it had started. Recording what this screen asked for lets the echo be recognised and
   * ignored.
   */
  private ownQueryRequest: string | null | undefined = undefined;

  /**
   * The instance title, rendered so that its ABSENCE is visible rather than blank.
   *
   * Measured before this template existed: the cell's `innerHTML` and `textContent` were both `"  "`
   * - two literal spaces, one text node, no element children - and Chrome's accessibility tree
   * reported the cell as unnamed with no children. Nothing on screen distinguished "no title" from a
   * rendering fault.
   */
  @ViewChild('titleCell', { static: true })
  private titleCellTemplate?: TemplateRef<DataTableCellContext<ModuleListItem>>;

  /** The all-pages flag, rendered through the shared yes/no pipe. */
  @ViewChild('allTabsCell', { static: true })
  private allTabsCellTemplate?: TemplateRef<DataTableCellContext<ModuleListItem>>;

  /** The schedule start, rendered through the shared date pipe in its default short-date mode. */
  @ViewChild('startDateCell', { static: true })
  private startDateCellTemplate?: TemplateRef<DataTableCellContext<ModuleListItem>>;

  /** The schedule end, rendered the same way. */
  @ViewChild('endDateCell', { static: true })
  private endDateCellTemplate?: TemplateRef<DataTableCellContext<ModuleListItem>>;

  // ---------------------------------------------------------------------------------------------------
  // STATE OWNED BY THIS SCREEN
  // ---------------------------------------------------------------------------------------------------
  //
  // Two slices, and no more. Everything else this screen shows belongs to the store, which is where a
  // filter, a page coordinate and an ordering are held so that they survive a navigation away and
  // back. Nothing here is an observable subject and nothing is a class field mutated in place: a plain
  // field would not notify an `OnPush` view, and a subject would be a second mechanism for what a
  // signal already does.

  /** Backing store of {@link columns}; written exactly once, in {@link ngOnInit}. */
  private readonly columnSet = signal<readonly DataTableColumn<ModuleListItem>[]>([]);

  /**
   * The placement whose removal is awaiting confirmation, or `null` when none is.
   *
   * THE WHOLE ROW, not an identifier, and that is a deliberate choice about sentinels. A numeric
   * "pending" field would have to represent "nothing pending" as some number, and every candidate is a
   * REAL value on this contract: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)` so zero is the first module
   * of an installation, and minus one is the legacy absence marker
   * (`Library/Components/Shared/Null.vb:L41-L45`) which the wire still carries. Holding the row makes
   * "nothing pending" a distinct `null` that cannot collide with any identifier, and it also gives the
   * confirmation dialog both identities it needs without a second lookup.
   */
  private readonly pendingRemoval = signal<ModuleListItem | null>(null);

  /**
   * The placement whose removal has been requested but whose outcome has not yet been reported.
   *
   * Separate from {@link pendingRemoval}, which is cleared the moment the command is issued so the
   * dialog closes. This one survives until the store answers, which is what lets the reporting effect
   * tell a removal's outcome apart from any other failure the store might record.
   */
  private readonly awaitedRemoval = signal<ModuleListItem | null>(null);

  // ---------------------------------------------------------------------------------------------------
  // STORE-DERIVED SURFACE
  // ---------------------------------------------------------------------------------------------------
  //
  // These are the store's OWN signals, re-exposed under template-facing names. Assigning a signal
  // shares it rather than copying it, so there is exactly one source of truth and nothing here can
  // drift from it. Nothing in this file writes to any of them - a screen reads state and issues
  // commands; it never reaches past a command to mutate a slice.

  /**
   * The placements to paint.
   *
   * A fresh array arrives on every re-query, because the store replaces the whole page envelope rather
   * than mutating it - which is what the shared table requires, since it takes row identity from the
   * object reference.
   */
  protected readonly modules = this.store.modules;

  /**
   * The paging coordinates of the page in hand: total, zero-based index, applied size and page count.
   *
   * Read from the RESPONSE rather than from the request, so the pager describes the page actually on
   * screen instead of one that may still be in flight. The page count here is the server's own and is
   * never recomputed.
   */
  protected readonly meta = this.store.meta;

  /**
   * Whether a listing request is in flight.
   *
   * Handed to the shared table, which renders the progress indicator and the empty state itself, in
   * one spanning row, and lets loading win over empty. Neither is rendered directly by this screen and
   * no custom empty wording is supplied: the table's own wording is the design system's.
   */
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
  protected readonly hasResults: Signal<boolean> = computed(() => this.meta().totalCount > 0);

  protected readonly listLoading = this.store.listLoading;

  /**
   * The current failure, or `null`.
   *
   * Held as the structured record the store built - the operation, the problem document, the resolved
   * summary and the server's failure code - and never as pre-rendered markup.
   *
   * PRIVATE, deliberately. Only the outcome bridge below consumes the record as a whole; what the
   * template needs is the problem DOCUMENT, which {@link problem} derives and the shared banner
   * accepts directly. Exposing the record as well would put two overlapping views of one failure in
   * front of the template author with nothing to choose between them.
   *
   * The store's aggregate busy flag is likewise NOT re-exposed. It was, until runtime validation
   * showed it had no consumer to justify it: the shared table takes the LISTING flag, which
   * {@link listLoading} supplies, and the shared confirmation dialog declares no disabled input at
   * all - its inputs are the title, the message, the confirm label and the danger flag - so there is
   * nothing on this screen an aggregate flag could drive. A member kept for a consumer that cannot
   * exist is dead surface, so it is gone.
   */
  private readonly failure = this.store.failure;

  /**
   * The problem document behind {@link failure}, or `null`.
   *
   * Bound straight to the shared error banner, whose input takes exactly this type and treats `null`
   * as its empty state. The banner resolves its own severity, wording and per-field summary from the
   * document; this screen adds nothing to it.
   *
   * MIGRATION: THE DOCUMENT TRAVELS WHOLE, AND ITS TRACE IDENTIFIER IS WHY. That value is the only
   *   join key between what a person saw in the browser and what the server logged. Its per-field map
   *   travels intact too: those keys are .NET model-state keys, are NOT camel-cased, and - the map
   *   being an index signature under `noPropertyAccessFromIndexSignature` - are read with an index
   *   expression and never with a property access. This screen reads no field entry at all; the banner
   *   does, through the shared utility that owns the narrowing.
   */
  protected readonly problem = computed(() => {
    const current: ModuleStoreFailure | null = this.failure();

    return current === null ? null : current.problem;
  });

  /**
   * The key the listing is currently ordered by.
   *
   * ⚠ FALLS BACK TO THE SERVER'S OWN ORDER RATHER THAN TO NOTHING, AND THAT IS THE POINT. When
   * nothing has been asked for, the endpoint does not return the collection unordered - it returns
   * it by title, ascending. Reporting an absent key in that state made the grid announce itself as
   * unsorted while rendering sorted rows, which runtime measurement caught twice over: every
   * sortable heading read `aria-sort="none"` and drew no indicator, yet the ten rendered rows were
   * row-for-row identical to those an explicit ascending request returns; and pressing the title
   * heading once changed nothing visible except adding the arrow, because the shared table starts a
   * fresh column at ascending and ascending is what was already showing.
   *
   * Declaring the order the reader is actually looking at fixes both at once: the announcement
   * becomes true, the indicator appears where the ordering is, and the first press on that heading
   * now REVERSES it, because the shared table flips the direction of a column it is told is already
   * sorted.
   *
   * This is presentation state and nothing more. The store's query is untouched, so the request
   * still carries no sort parameter and the server still applies its own default; and the address is
   * untouched, so an address that asked for nothing continues to say nothing. Writing the default
   * into either would mean the screen navigating to correct its own address on arrival, which is a
   * shape this feature set has already been burned by.
   *
   * Typed exactly as the shared table's input accepts, so it passes through with no reshaping.
   */
  protected readonly sortBy = computed(() => this.store.query().sortBy ?? DEFAULT_SORT_KEY);

  /** The direction {@link sortBy} is applied in, defaulting with it to the server's own order. */
  protected readonly sortDir = computed(
    () => this.store.query().sortDir ?? DEFAULT_SORT_DIRECTION,
  );

  /** The column descriptors, assembled in {@link ngOnInit} once the cell templates exist. */
  protected readonly columns = this.columnSet.asReadonly();

  /** The placement awaiting confirmation, or `null`. Drives whether the dialog is in the DOM at all. */
  protected readonly removalTarget = this.pendingRemoval.asReadonly();

  // ---------------------------------------------------------------------------------------------------
  // WORDING BOUND BY THE TEMPLATE
  // ---------------------------------------------------------------------------------------------------

  /** @see PAGE_TITLE */
  protected readonly pageTitle = PAGE_TITLE;

  /** @see PAGE_SUBTITLE */
  protected readonly pageSubtitle = PAGE_SUBTITLE;

  /** @see TABLE_CAPTION */
  protected readonly tableCaption = TABLE_CAPTION;

  /** @see SEARCH_PLACEHOLDER */
  protected readonly searchPlaceholder = SEARCH_PLACEHOLDER;

  /** @see COMMAND_LABEL */
  protected readonly commandLabel = COMMAND_LABEL;

  /** @see ABSENT_TITLE_MARK */
  protected readonly absentTitleMark = ABSENT_TITLE_MARK;

  /** @see ABSENT_TITLE_DESCRIPTION */
  protected readonly absentTitleDescription = ABSENT_TITLE_DESCRIPTION;

  /** @see PAGE_ACTION_LABEL */
  protected readonly pageActionLabel = PAGE_ACTION_LABEL;

  /** @see REMOVE_CONFIRM_MESSAGE */
  protected readonly removeConfirmMessage = REMOVE_CONFIRM_MESSAGE;

  /** Address of the create screen, for the primary page action. */
  protected readonly createLink = ROUTE.create;

  /** Address of the import screen, for the secondary page action. */
  protected readonly importLink = ROUTE.import;

  // ---------------------------------------------------------------------------------------------------
  // OUTCOME REPORTING
  // ---------------------------------------------------------------------------------------------------

  /**
   * Bridges a removal's outcome onto the notification queue.
   *
   * AN EFFECT, BECAUSE EMITTING A USER-VISIBLE MESSAGE IS A GENUINE SIDE EFFECT - and because there is
   * no completion callback to hang one on: the store's commands return `void` and subscribe
   * internally. This effect loads NO data. {@link ngOnInit} performs the initial read, and every
   * derived value on this class is a `computed`; using an effect as a loader is precisely the
   * anti-pattern this avoids.
   *
   * Declared in the constructor because that is this workspace's established shape for an outcome
   * bridge, and because it keeps the effect's creation next to the reasoning that explains it.
   *
   * The awaited placement and the store's flags are all read TRACKED, so requesting a removal
   * schedules this effect and its settling re-runs it. The clearing write happens inside `untracked`,
   * which keeps that write out of the effect's own dependency set and stops it re-entering.
   *
   * Gated on the SAVING flag rather than on the store's aggregate busy flag. That distinction is
   * deliberate: a successful removal makes the store re-read the listing, so the aggregate flag is
   * still raised at the moment the removal itself has already settled - waiting for it would delay the
   * message behind an unrelated request, and on a slow listing could leave a person with no feedback at
   * all. The store clears any recorded failure before each command begins, so a failure still present
   * once the removal has settled belongs to that removal; the operation is matched as well, because
   * the re-read that follows a success has a failure of its own that must not be misreported as a
   * failed removal.
   *
   * Reports NOTHING unless a removal is outstanding, so a listing failure - which the banner already
   * shows in full, with its per-field detail and its support reference - does not additionally raise a
   * transient message for the same event.
   */
  constructor() {
    // Keeps the search box showing the filter that is actually in force.
    //
    //  ⚠ WITHOUT THIS THE GRID LIES ABOUT WHAT IT IS SHOWING. Measured: search for a term, leave to
    //  another screen and come back, and the box reads the empty string while the grid is still
    //  filtered - `" 1–10 of 248 "` against a collection of 250, with two rows silently withheld and
    //  the return request still carrying `query=…`. The stores are `providedIn: 'root'` singletons, so
    //  the FILTER survives the screen while the CONTROL is rebuilt empty. A scan of the accessibility
    //  tree for a clear, reset or show-all affordance found none, so there was no cue that a filter
    //  was in force and no way to discover it. The filter could in fact be cleared, but only by
    //  submitting an already-blank box - a recovery that looks like a no-op and that nobody would try
    //  without first knowing there was something to clear.
    //
    //  Reads the STORE rather than the address, because the store is what the grid is drawn from, so
    //  the box agrees with the ROWS even while a navigation is still settling. The write is
    //  `untracked` and goes through the control's adopt-without-emitting path, so it neither
    //  re-enters this effect nor issues a query for a filter that has already been applied.
    //
    //  This is the same defect, and the same remedy, as on the portals listing; the echo guard below
    //  is why it needs a member rather than being a one-line assignment.
    effect(() => {
      // The store's `query` signal is the whole request shape, so the search term is the member of
      // that name inside it rather than the signal itself.
      const inForce: string | null = this.store.query().query ?? null;

      untracked(() => {
        if (this.ownQueryRequest !== undefined && this.ownQueryRequest === inForce) {
          // The echo of this screen's own request. The box already holds the operator's text - possibly
          // with more typed since - so it is left entirely alone.
          return;
        }

        this.ownQueryRequest = undefined;
        this.searchControl?.cancelPendingSearch(inForce ?? '');
      });
    });

    // Brings a newly recorded failure into view.
    //
    //  ⚠ WITHOUT THIS A SIGHTED OPERATOR SEES NOTHING AT ALL. Measured: with the page scrolled down to
    //  reach the pager (`scrollY` 1162) a failed page change rendered its banner at document top
    //  265.5, which is a viewport rect of y −896.5 — eight hundred and twenty-five pixels ABOVE the
    //  top of the viewport — and nothing scrolled it in. The rows correctly stayed as they were, the
    //  pager correctly did not advance, and so the entire visible result of the failure was that
    //  nothing happened. A screen-reader user was served, because the banner is an assertive
    //  `role="alert"` and is announced wherever it sits; a sighted user was not, which is the reverse
    //  of the usual asymmetry and easy to miss in testing precisely because the announcement works.
    //
    //  `block: 'nearest'` deliberately: it scrolls only as far as it must, so a failure that is
    //  ALREADY visible does not move the page under the reader. `behavior: 'auto'` rather than
    //  `smooth`, because a reader who has just lost their result does not need it animated, and an
    //  animation would also outlast the read that follows a retry.
    effect(() => {
      const failed: ModuleStoreFailure | null = this.failure();

      if (failed === null) {
        return;
      }

      untracked(() => {
        // Deferred one turn, because the region is created by the very change this effect is
        // reacting to and does not exist in the DOM until that change has been rendered.
        queueMicrotask(() => {
          this.failureBanner?.nativeElement.scrollIntoView({
            block: 'nearest',
            inline: 'nearest',
            behavior: 'auto',
          });
        });
      });
    });

    effect(() => {
      const awaited: ModuleListItem | null = this.awaitedRemoval();
      const inFlight: boolean = this.store.saving();
      const failed: ModuleStoreFailure | null = this.failure();

      if (awaited === null || inFlight) {
        return;
      }

      // A failure recorded against any other command is not this removal's outcome, so the wait
      // continues rather than being resolved against the wrong event.
      if (failed !== null && failed.operation !== 'deleteModule') {
        return;
      }

      untracked(() => {
        this.awaitedRemoval.set(null);

        if (failed === null) {
          this.notifications.notify('success', REMOVE_SUCCESS_MESSAGE);

          return;
        }

        // The severity arrives ALREADY RESOLVED, and a refusal is a WARNING rather than an error. It is
        // passed through exactly as received: the classification belongs to the shared resolution
        // utility, which owns the measured legacy evidence for it, and re-deriving it here would give
        // one decision two homes that could disagree.
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
          failed.summary.severity,
          failed.summary.message,
          failed.summary.supportReference,
        );
      });
    });
  }

  // ---------------------------------------------------------------------------------------------------
  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them.
   *
   * ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT. The grid's own fallback is the row
   * OBJECT, which is a correct key only while the same objects stay in play; every read from the server
   * decodes fresh objects, so without this a refetch of the same page presents entirely new keys and the
   * whole body is rebuilt to display records that never changed. `tabModuleId` is unique by definition, being
   * the record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly moduleRowKey = (row: ModuleListItem): number => row.tabModuleId;

  // LIFECYCLE
  // ---------------------------------------------------------------------------------------------------

  /**
   * Assembles the column set and reads the first page.
   *
   * The ORDER of the two statements matters. The column descriptors must hold real template
   * references, which only the static view queries above can supply, and those resolve before this
   * hook - so this is the earliest point at which the set can be built, and building it here means the
   * table receives its columns in the same change-detection pass as its rows.
   *
   * The read is issued here rather than from an effect. An effect that loaded data would fire again
   * whenever anything it happened to read changed, which turns a screen entry into an unpredictable
   * number of requests; a lifecycle hook fires exactly once per instantiation. The store carries its
   * filters across a navigation, so re-entering the screen re-reads with whatever filter was last
   * applied, which is the behaviour a person expects of a back button.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two reads of the same page on every arrival.
    //
    // It also settles the stale-state defect at its root. This store is provided at the application root and
    // therefore OUTLIVES this route, so a previous visit's search, ordering and page are all still held when
    // an operator returns. Applying the whole query from the address means a bare `/modules` restores the
    // defaults and a `/modules?filter=x&sortby=moduleTitle&currentpage=4` restores exactly that view, in both
    // directions, for a reload and for back and forward alike.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: ModuleListAddressQuery = parseModuleListQuery(address);

        // An address that says something unusable is CORRECTED rather than obeyed silently, so that what is
        // on screen and what is in the address never disagree. The correction REPLACES the entry rather than
        // adding one - an operator pressing back should reach where they came from, not the uncorrected form
        // of where they already are - and it returns without reading, because the replacement navigation
        // emits again and that emission does the read.
        if (!addressStatesQuery(address, serialiseModuleListQuery(query))) {
          void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: serialiseModuleListQuery(query),
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });

          return;
        }

        // ⚠ THE ECHO GUARD IS NOT ARMED HERE, AND ARMING IT HERE WOULD DISABLE THE SEARCH BOX'S
        // RECONCILIATION ENTIRELY. Only the box's own handler knows that a term came from the box; every
        // OTHER route to this line - a back navigation, a typed address, a correction - is a term the box has
        // not seen and must be shown.
        // ⚠ THE PAGE IS SET LAST, AND THE ORDER IS LOAD-BEARING. Both `setQuery` and `setSort` return the
        // listing to the first page, which is right when an operator has just searched or re-ordered and
        // wrong when the address is naming all three at once - either of them running after `setPageIndex`
        // would silently discard the page the address asked for. Every setter is silent, so this costs one
        // read however many coordinates changed.
        this.store.setQuery(query.query);
        this.store.setSort(query.sortBy, query.sortDir);
        this.store.setPageIndex(query.pageIndex);
        this.store.loadModules();
      });
  }

  // ---------------------------------------------------------------------------------------------------
  // ROW ADDRESSES
  // ---------------------------------------------------------------------------------------------------
  //
  // Three link builders, each returning a plain path string for `routerLink`.
  //
  // NOT ONE OF THEM TESTS AN IDENTIFIER. There is no truthiness check, no comparison against zero or
  // minus one, no coalescing and no absolute value anywhere below, and that is a correctness
  // requirement rather than a stylistic one: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so module ZERO
  // is the first module of an installation and `if (moduleId)` would silently give it no links at all.
  // The identifier is interpolated exactly as the server sent it.

  /**
   * Address of the edit screen for one placement.
   *
   * @param row The row being rendered.
   * @returns `/modules/{moduleId}`.
   */
  protected editLink(row: ModuleListItem): string {
    return `${ROUTE.modules}/${row.moduleId}`;
  }

  /**
   * Address of the settings screen for one placement.
   *
   * @param row The row being rendered.
   * @returns `/modules/{moduleId}/settings`.
   */
  protected settingsLink(row: ModuleListItem): string {
    return `${ROUTE.modules}/${row.moduleId}/${ROUTE.settings}`;
  }

  /**
   * Address of the export screen for one placement.
   *
   * Always offered. The listing contract carries no portability signal - see the register on
   * {@link ModuleListComponent} - so the server decides whether a given module can be exported.
   *
   * @param row The row being rendered.
   * @returns `/modules/{moduleId}/export`.
   */
  protected exportLink(row: ModuleListItem): string {
    return `${ROUTE.modules}/${row.moduleId}/${ROUTE.export}`;
  }

  // ---------------------------------------------------------------------------------------------------
  // CELL FORMATTING
  // ---------------------------------------------------------------------------------------------------

  /**
   * Resolves a visibility code to its display word.
   *
   * Through the enumeration and a lookup keyed by its members - never by truthiness, and never by
   * indexing an array with a raw number. Zero is a real, meaningful state here, so every expression
   * that could treat it as an absence is avoided by construction.
   *
   * The lookup is total over the enumeration, which is what makes the guard below reachable only for a
   * value the contract cannot legally carry. A code outside the three declared members means the wire
   * disagrees with this model, and the honest rendering of that is empty text rather than a fabricated
   * word or a thrown error inside a paint.
   *
   * @param visibility The code as the server sent it.
   * @returns The display word, or empty text for a code this contract does not declare.
   */
  protected visibilityLabel(visibility: ModuleVisibility): string {
    const resolved: string | undefined = VISIBILITY_LABEL[visibility];

    if (resolved === undefined) {
      return '';
    }

    return resolved;
  }

  // ---------------------------------------------------------------------------------------------------
  // FILTERING, ORDERING AND PAGING
  // ---------------------------------------------------------------------------------------------------

  /**
   * Applies the reader's free-text filter and re-reads from the first page.
   *
   * The text is recorded EXACTLY AS TYPED. No wildcard character is appended, no pattern syntax is
   * introduced and no escaping is applied: match semantics belong to the server alone, which is where
   * the legacy call-site pattern decoration was moved to during the migration. Appending a wildcard
   * here would both duplicate a server concern and silently change the semantics the server intends.
   *
   * An empty term becomes `null` rather than the empty string, and the distinction is not cosmetic:
   * `Library/Components/Shared/Null.vb:L71-L75` defines the legacy string-absence marker AS the empty
   * string - its body is literally `Return ""` - so the two were indistinguishable in the legacy and
   * are deliberately held apart here. `null` means "no filter"; the empty string would be a filter for
   * nothing. The comparison is on LENGTH, so it cannot be mistaken for a truthiness test.
   *
   * The store resets the page coordinate to the first page for us, because a coordinate measured
   * against one match set does not address the same rows once the set changes.
   *
   * ⚠ SUBMITTING A QUERY THE ADDRESS ALREADY STATES ISSUES NO REQUEST, AND THAT IS DELIBERATE - the same
   * measured behaviour the sibling portal listing records at its own `onSearch`, for the same reason. On the
   * bare, unfiltered first page with an empty box, a proven-delivered press of Search produced ZERO requests
   * to `/api/v1/modules`: the shared control emitted, this method called `router.navigate` with a target
   * identical to the current address, and Angular's default `onSameUrlNavigation: 'ignore'` dropped it. Since
   * the address is the only thing that asks the store to read, a submit that changes no part of the address
   * asks for nothing - and what it would have asked for is already on screen.
   *
   * The case the report actually raised is unaffected and verified working: clearing a filter that IS in
   * force changes the address, so it navigates and re-reads.
   *
   * @param term The text the shared search control emitted, already debounced and trimmed by it.
   */
  protected onSearch(term: string): void {
    const wanted: string | null = term.length === 0 ? null : term;

    // Recorded BEFORE the store is written, because writing it runs the reconciling effect
    // synchronously and the guard has to be in place by the time that effect reads it. See
    // {@link ownQueryRequest} for what goes wrong without it.
    this.ownQueryRequest = wanted;

    // ⚠ THE ADDRESS IS WRITTEN AND THE STORE IS NOT TOUCHED. The subscription in `ngOnInit` applies the term
    // and issues the read, so writing the address is the whole of the change here. Calling the store as well
    // would apply it twice and read twice. The echo guard above is still set BEFORE the navigation, because
    // the reconciling effect must not write this screen's own term back into the box being typed in.
    //
    // The page is cleared alongside it: a different search yields a different result set in which the page
    // the operator was on has no counterpart. The reset now lives in the address rather than only in the
    // store command, so it survives a reload with the term it belongs to.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [FILTER_PARAM]: wanted, [PAGE_PARAM]: null },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Whether this module has no title of its own, so the cell must say so.
   *
   * Treats a whitespace-only title as absent as well as an empty one, because a title of spaces is
   * indistinguishable from no title once rendered and the legacy field had no presence validator to
   * prevent either.
   *
   * @param row The module placement.
   * @returns `true` when nothing meaningful is stored in the title.
   */
  protected isTitleAbsent(row: ModuleListItem): boolean {
    return (row.moduleTitle ?? '').trim().length === 0;
  }

  /**
   * The accessible name for one row command.
   *
   * Composed rather than concatenated in the template, so that the verb and the identifying phrase
   * are joined in ONE place and cannot drift between the four commands. The phrase itself, and the
   * legacy authority for the fallback it applies, are on {@link describeModule}.
   *
   * @param verb The command's own wording.
   * @param row The module placement the command acts on.
   * @returns The verb followed by a phrase that identifies the module.
   */
  protected commandName(verb: string, row: ModuleListItem): string {
    return `${verb} ${describeModule(row)}`;
  }

  /**
   * Applies the reader's ordering and re-reads from the first page.
   *
   * The key is the column's own key, which IS the sort name the endpoint accepts - only the columns
   * keyed by a permitted name declare themselves sortable, so no control here can produce a rejected
   * request. The direction arrives in the server's own spelling and needs no translation.
   *
   * A NULL DIRECTION CLEARS THE ORDERING RATHER THAN DEFAULTING IT. The shared table's cycle has a
   * third step that asks for no ordering at all, which is the state this screen arrives in - the store
   * initialises both coordinates to null and the request omits both parameters - so the key is cleared
   * alongside the direction and the server chooses again. Substituting a direction of this screen's own
   * here would silently make that third step a no-op and the arrival order permanently unreachable.
   *
   * @param change The key the reader activated and the direction to apply, or null to stop ordering.
   */
  protected onSortChange(change: DataTableSortChange): void {
    const direction: SortDirection | null = change.direction;

    // THE ORDERING GOES INTO THE ADDRESS, and the third step of the cycle clears the KEY as well as
    // the direction: a request carrying a key with no direction would be a different question asked of
    // the server, and the reader who pressed a third time asked for no ordering at all. The page is
    // dropped alongside it, because which page a record falls on depends on the ordering.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        [SORT_BY_PARAM]: direction === null ? null : change.key,
        [SORT_DIR_PARAM]: direction,
        [PAGE_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Moves to another page and re-reads.
   *
   * NO ARITHMETIC, IN EITHER DIRECTION, AND THAT IS THE WHOLE CONTRACT. The wire's page coordinate is
   * zero-based; the shared pager's `page` input IS that zero-based index and its change event emits a
   * zero-based index back. The one-based counter a person reads is rendered inside the pager and never
   * leaves it. Any `+ 1` or `- 1` in this method or in the sibling template would serve the
   * neighbouring page behind a successful response, which no status code would reveal.
   *
   * The pager only ever emits a whole index inside the available range that differs from the current
   * one, so no clamping is applied here - and none should be: the store does not clamp either, and a
   * second guard would just be a second place for the two to disagree.
   *
   * @param pageIndex The zero-based page the reader asked for. Zero is the first page and is an
   * ordinary value.
   */
  protected onPageChange(pageIndex: number): void {
    // A page turn is a PUSHED history entry, not a replaced one: runtime testing found that pressing back
    // from page three was not possible because paging created no entry at all, and returning to the page you
    // came from is the ordinary meaning of that button.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [PAGE_PARAM]: firstPageParameter(pageIndex) },
      queryParamsHandling: 'merge',
    });
  }

  // ---------------------------------------------------------------------------------------------------
  // REMOVAL
  // ---------------------------------------------------------------------------------------------------

  /**
   * Opens the confirmation for one placement.
   *
   * Records the whole row, because both of its identities are needed to address a single placement and
   * because a `null` is the only representation of "nothing pending" that cannot collide with a real
   * identifier.
   *
   * @param row The placement the reader asked to remove.
   */
  protected onRequestDelete(row: ModuleListItem): void {
    this.pendingRemoval.set(row);
  }

  /**
   * Issues the removal for the confirmed placement.
   *
   * ADDRESSES THE PLACEMENT, NOT THE MODULE, and passes BOTH identities for that reason: a module
   * whose all-pages flag is set has one placement per page, so the module identity alone does not name
   * a single row. This mirrors the legacy `DeleteTabModule(TabId, ModuleId)`
   * (`Library/Components/Modules/ModuleController.vb:L837`) and emphatically NOT `DeleteModule`
   * (`:L819`), which is the hard delete.
   *
   * NOTHING IS PRUNED LOCALLY AND NO RE-READ IS ISSUED FROM HERE. The removal is soft and two-tiered:
   * the legacy routine deletes only the per-page reference row (`:L843`) and soft-deletes the module
   * itself ONLY when no other page still references it - `:L850` says `' soft delete the module` in the
   * source's own words, with `:L851` setting the page reference to the absence marker and `:L852`
   * setting the deleted flag. So after a `204` the row MAY OR MAY NOT still belong in the listing, and
   * only the listing endpoint knows which. An optimistic splice would hide a row that legitimately
   * survived. The store re-reads the listing itself on success, which is where that decision belongs;
   * a second read from here would double every request.
   *
   * The dialog is dismissed immediately while the awaited placement is retained, so the screen is not
   * left holding a modal over an in-flight request.
   */
  protected onConfirmDelete(): void {
    const target: ModuleListItem | null = this.pendingRemoval();

    // Defensive rather than expected: the dialog is only in the DOM while a target is held, so this
    // can be reached only if a confirmation were dispatched programmatically.
    if (target === null) {
      return;
    }

    this.pendingRemoval.set(null);
    this.awaitedRemoval.set(target);

    this.store.deleteModule(target.moduleId, target.tabModuleId);
  }

  /**
   * Dismisses the confirmation without removing anything.
   *
   * Named for the intent rather than for the event. A bare `cancel` would read as the dialog's own
   * output and as a host-listener convention, and this is neither.
   */
  protected onCancelDelete(): void {
    this.pendingRemoval.set(null);
  }

  /**
   * Re-issues the listing read after a reported failure.
   *
   * The failure is cleared FIRST and the read issued second, so a second failure produces a fresh
   * report rather than being indistinguishable from the one still on screen, and the awaited removal
   * is dropped for the same reason the dismissal drops it: nothing may be left waiting on an outcome
   * that will never arrive.
   *
   * The store carries the current filter, sort and page, so this repeats the request that failed
   * rather than resetting the screen - which is what makes it a retry rather than a reload.
   */
  protected onRetryRead(): void {
    this.awaitedRemoval.set(null);
    this.store.clearFailure();
    this.store.loadModules();
  }

  /**
   * Dismisses the failure banner.
   *
   * Offered because a banner a person cannot dismiss is a banner that outlives its cause. Clearing the
   * failure also clears the awaited removal, so a dismissal cannot leave the reporting effect waiting
   * on an outcome that will never arrive.
   */
  protected onDismissFailure(): void {
    this.awaitedRemoval.set(null);
    this.store.clearFailure();
  }

  // ---------------------------------------------------------------------------------------------------
  // COLUMN SET
  // ---------------------------------------------------------------------------------------------------

  /**
   * Assembles the ten column descriptors.
   *
   * Built here rather than in the template, because a column set is a structure with invariants - keys
   * unique, alignment stated, a sortable key that the endpoint accepts - and a template can express
   * none of them.
   *
   * ## COLUMN ORDER, measured from `Website/admin/Portal/portals.ascx`
   *
   * Commands FIRST, then identity, then the primary label, then the derived and related values, then
   * the numerics, and the DATES LAST. That grid declares, in order: the edit command (`:L21`), the
   * delete command (`:L22`), the identifier (`:L23`), the title (`:L30`), the derived alias list
   * (`:L37`), four counts and amounts (`:L44` to `:L47`) and the expiry date (`:L48`). The order below
   * is that order, applied to this contract's members.
   *
   * ## KEY IS NEVER THE LABEL
   *
   * Proven twice in that same file: `:L46` binds `DataField="HostSpace"` under
   * `HeaderText="DiskSpace"` and `:L47` binds `DataField="HostFee"` under `HeaderText="HostingFee"`.
   * The key is a stable identity - a sort name, a `track` value and an accessibility anchor - and the
   * label is data that may repeat, may be reworded and may even contain a space. Nothing here derives a
   * key, a class name or an element identifier from a label.
   *
   * ## BOTH ALIGNMENTS ARE STATED ON EVERY COLUMN, ALWAYS
   *
   * They are independent members and the legacy markup separates them at two levels at once, so no
   * table-wide default is relied on. `portals.ascx` sets its grid-level heading alignment to centre at
   * `:L15` and its grid-level body alignment to centre at `:L16`, then overrides BOTH SIDES on three
   * columns - and it states them in OPPOSITE ORDERS in adjacent declarations: `:L24` gives the
   * identifier's `ItemStyle` before `:L25` gives its `HeaderStyle`, whereas `:L31` gives the title's
   * `HeaderStyle` before `:L32` gives its `ItemStyle`. An order-sensitive reading of that markup would
   * miss the pattern entirely. The resolution used below reproduces what that grid actually rendered:
   * every heading CENTRED, as its grid-level style dictated; textual bodies START-aligned, as its three
   * label columns overrode; and numeric, boolean and command bodies CENTRED, as its four count columns
   * inherited. The vocabulary is inline - `start`, `center`, `end` - and never physical left or right,
   * so the grid reads correctly under a right-to-left script.
   *
   * ## WHAT IS DELIBERATELY NOT A COLUMN
   *
   * Three omissions, each measured rather than assumed:
   *
   *   * NO CACHE PERIOD, and no pane. The listing contract carries neither. The resource file supplies
   *     a heading for the former - `plCacheTime.Text`, `'Cache Time (secs):'`, unit included - but a
   *     heading is not a value, and rendering a member the contract does not declare would bind
   *     `undefined` with no compile error to reveal it.
   *   * NO SOFT-DELETED FLAG. It is on the contract and it IS data - `false` is a real state, not an
   *     absence - but the listing excludes soft-removed rows unless a caller asks for them, and the
   *     server's flag is a non-nullable boolean that defaults to excluding them. The column would
   *     therefore read the same on every row of every page. No affordance to vary it is added either:
   *     the shared component set is closed and contains no toggle, and there is no legacy workflow
   *     being dropped, this screen having no legacy ancestor at all.
   *   * NO CONTAINER FLAG, and no other presentation member. `displayTitle` is on the contract, and the
   *     resource file even shows what it really governs - `plDisplayTitle.Text` reads
   *     `'Display Container?'`, not "display title" - but it belongs to the rendering of a page this
   *     application does not render, as do the alignment, colour, border, container source, icon,
   *     header and footer members of the legacy class.
   *
   * Two further exclusions are absolute rather than editorial. `IsDefaultModule`
   * (`Library/Components/Modules/ModuleInfo.vb:L590`) and `AllModules` (`:L599`) carry `<XmlIgnore()>`
   * and are TRANSIENT UI FLAGS rather than persisted data - by contrast `AllTabs` (`:L248`) carries
   * `<XmlElement("alltabs")>` and IS real data, which is why it appears below. And the legacy
   * semicolon-delimited role strings - `Permissions` (`:L473`), `AuthorizedEditRoles` (`:L545`),
   * `AuthorizedViewRoles` (`:L554`) and `AuthorizedRoles` (`:L627`), each of which could carry a
   * bracketed pseudo-role for a single account - are never reproduced, built or parsed anywhere:
   * structured permissions replaced them, and this listing carries none.
   *
   * ## THE THREE NAME MEMBERS ARE NOT INTERCHANGEABLE
   *
   * `ModuleTitle` (`ModuleInfo.vb:L194`) is the INSTANCE title an administrator typed and is this
   * grid's primary label. `FriendlyName` (`:L374`) is the DEFINITION's display name - what the legacy
   * settings screen showed against its `'Module:'` label. `ModuleName` (`:L437`) is the installed
   * PACKAGE's programmatic name. All three are rendered, in separate columns, precisely so the
   * distinction is visible rather than collapsed.
   *
   * MIGRATION: NO FIELD NAME IS EVER DERIVED FROM A LEGACY XML SERIALISATION ATTRIBUTE. The trap is
   *   real and silent: `ModuleInfo.vb:L194` decorates the instance title with `<XmlElement("title")>`,
   *   `:L158` decorates the identifier with `<XmlElement("moduleID")>` in mixed case and `:L167`
   *   decorates the definition key with `<XmlElement("moduledefid")>` in lower case. Those names
   *   described a legacy XML document; the target serialises .NET property names under a camel-case
   *   policy, so the members below are taken from the listing contract itself and from nowhere else. A
   *   name guessed from an attribute would bind `undefined` and paint an empty column with no error
   *   anywhere.
   *
   * @returns The ten descriptors, in the measured legacy order.
   * @throws Error when a required cell template is missing from the sibling template file.
   */
  private buildColumns(): readonly DataTableColumn<ModuleListItem>[] {
    return [
      // 1. The row commands. Header-less, matching `portals.ascx:L21-L22`, which declared no
      //    `HeaderText` on either of its command columns - but still labelled here so the column keeps
      //    an accessible name. The `actions` kind also suppresses row activation, so pressing a command
      //    never doubles as selecting the row. Sized intrinsically: a commands column must never be
      //    given a track narrower than the controls it carries, because a table cell does not clip and
      //    a cramped command would paint over the value beside it.
      {
        key: 'rowCommands',
        label: COLUMN_LABEL.rowCommands,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        width: 'min-content',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.rowCommandsTemplate, 'rowCommands'),
      },

      // 2. Identity, mirroring the identifier column at `portals.ascx:L23`. Sortable: `moduleId` is one
      //    of the five names the endpoint's allow-list accepts.
      {
        key: SORTABLE_KEY.moduleId,
        label: COLUMN_LABEL.moduleId,
        sortable: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        field: 'moduleId',
      },

      // 3. The primary label - the INSTANCE title, mirroring `portals.ascx:L30`. Sortable, and it is
      //    also the server's default ordering. Nullable on the contract, which is ordinary data rather
      //    than a defect: the legacy settings screen declared no presence validator on the field.
      //
      //    ⚠ RENDERED THROUGH A TEMPLATE RATHER THAN A BARE FIELD, so that an absent title is VISIBLE.
      //    A bare `field` binding renders an absent value as empty text, which is correct for a value
      //    that is merely blank but not for one an operator has to act on: measured, the cell's whole
      //    content was two literal spaces and the accessibility tree reported it unnamed, so nothing
      //    distinguished "this module has no title" from "this cell failed to render".
      {
        kind: 'template',
        key: SORTABLE_KEY.moduleTitle,
        label: COLUMN_LABEL.moduleTitle,
        sortable: true,
        headerAlign: 'center',
        bodyAlign: 'start',
        cellTemplate: this.requireTemplate(this.titleCellTemplate, 'titleCell'),
      },

      // 4. The DEFINITION's display name - the legacy `'Module:'` label. NOT sortable, and that is the
      //    server's measured reason rather than an omission: the definition is resolved per row after
      //    the page has been taken, so ordering by it would order the page rather than the collection.
      {
        key: 'friendlyName',
        // The row's NAME. Emitted as `<th scope="row">` so a screen reader announces which record
        // each cell belongs to - without it, traversing a row gives the column name and the value
        // and never the record's identity. This column is the one a person would read aloud to say
        // which row they mean. No visual change: the shared stylesheet restores a body row
        // header's normal weight.
        rowHeader: true,
        label: COLUMN_LABEL.friendlyName,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'friendlyName',
      },

      // 5. The installed PACKAGE's programmatic name. Not sortable, for the same reason.
      {
        key: 'moduleName',
        label: COLUMN_LABEL.moduleName,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'moduleName',
      },

      // 6. The all-pages flag. A TEMPLATE column, necessarily: a boolean is not a text-shaped value,
      //    and the shared yes/no pipe is a template construct. `false` is DATA here and renders as the
      //    negative word rather than as empty output, which is exactly what the pipe guarantees.
      //
      //    MIGRATION: the legacy grids drew such flags as a PAIR of mutually exclusive images with no
      //      alternative text, so the state was drawn but never announced. It is now announced as
      //      words, and no image asset is referenced - the only static asset this workspace ships is a
      //      favicon.
      {
        key: 'allTabs',
        label: COLUMN_LABEL.allTabs,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.allTabsCellTemplate, 'allTabsCell'),
      },

      // 7. The visibility state. A FORMATTED column, so the exhaustive resolution stays in TypeScript
      //    where the enumeration is: a text column would paint the raw ordinal, and 0 is a meaningful
      //    state rather than a missing one.
      {
        key: 'visibility',
        label: COLUMN_LABEL.visibility,
        headerAlign: 'center',
        bodyAlign: 'center',
        value: (row: ModuleListItem): string => this.visibilityLabel(row.visibility),
      },

      // 8. The placement's position within its pane - the numeric column, mirroring the counts at
      //    `portals.ascx:L44-L45`. DISPLAYED ONLY: no reorder endpoint exists, so there is no
      //    affordance to change it. Not sortable, because a module has one position per placement and
      //    therefore no single position to order the module by.
      {
        key: 'moduleOrder',
        label: COLUMN_LABEL.moduleOrder,
        headerAlign: 'center',
        bodyAlign: 'center',
        field: 'moduleOrder',
      },

      // 9-10. The schedule, LAST, mirroring the expiry date at `portals.ascx:L48`. Both are TEMPLATE
      //       columns so the shared date pipe can resolve them in its default short-date mode, which
      //       already renders the legacy minimum-date sentinel as blank - reproducing the
      //       `Null.IsNull` guard at `ModuleSettings.ascx.vb:L152-L156` without a guard of this
      //       screen's own. Sortable: both are on the endpoint's allow-list.
      {
        key: SORTABLE_KEY.startDate,
        label: COLUMN_LABEL.startDate,
        sortable: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.startDateCellTemplate, 'startDateCell'),
      },
      {
        key: SORTABLE_KEY.endDate,
        label: COLUMN_LABEL.endDate,
        sortable: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.endDateCellTemplate, 'endDateCell'),
      },
    ];
  }

  /**
   * Unwraps a captured cell template, failing loudly and by name when it is absent.
   *
   * THROWS rather than degrading. The template is declared in a sibling file this component does not
   * own, so an omission is a wiring defect between the two - and a column with no template paints a
   * blank cell on every row, which reads as missing DATA and is the hardest class of defect to trace.
   * The message names the exact reference and where it must sit, so the fix needs no investigation.
   *
   * @param captured The template the static view query resolved, or `undefined`.
   * @param reference The `ng-template` reference name being required.
   * @returns The template, guaranteed present.
   * @throws Error when the sibling template does not declare the reference.
   */
  private requireTemplate(
    captured: TemplateRef<DataTableCellContext<ModuleListItem>> | undefined,
    reference: string,
  ): TemplateRef<DataTableCellContext<ModuleListItem>> {
    if (captured === undefined) {
      throw new Error(
        `module-list.component.html must declare an ng-template named "#${reference}" at the top ` +
          'level of the template, outside any control-flow block.',
      );
    }

    return captured;
  }
}
