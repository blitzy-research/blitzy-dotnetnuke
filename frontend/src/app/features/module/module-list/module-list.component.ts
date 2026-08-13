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

// WORDING
// 1. A specification can assert rendered text against the SAME constant the screen supplies, instead of
// restating it and letting the two drift. 2.

/**
 * The page title. AUTHORED, BECAUSE THERE IS NO LEGACY MODULE-LIST SCREEN TO TAKE IT FROM. `grep -rio
 * "<asp:DataGrid" Website/admin/Modules/` returns ZERO across every file in that directory, which holds
 * only `export.ascx`, `import.ascx`, `modulesettings.ascx`, their three code-behinds, an icon and
 * `App_LocalResources`.
 */
const PAGE_TITLE = 'Modules';

/** The sentence beneath the page title. */
const PAGE_SUBTITLE = 'Every module placed on a page of this site, one row per placement.';

/** Accessible name of the grid, projected into the shared table's caption slot. */
const TABLE_CAPTION = 'Modules placed on this site';

/** Placeholder of the single filter control. */
const SEARCH_PLACEHOLDER = 'Search modules';

/** Wording of the row commands. */
const COMMAND_LABEL = Object.freeze({
  /** `SharedResources.resx` key `Edit.Text`, value `'Edit'`. */
  edit: 'Edit',
  /** Authored. */
  settings: 'Settings',
  /** Authored. */
  export: 'Export',
  /** Authored; the legacy confirmation wording is on {@link REMOVE_CONFIRM_MESSAGE}. */
  remove: 'Delete',
});

const PAGE_ACTION_LABEL = Object.freeze({
  create: 'Add Module',
  import: 'Import Module',
});

/**
 * The glyph painted where a module has no title of its own. The em dash is the mark this application
 * already uses for a value that is absent rather than zero or empty - the same one the role listing
 * paints in its period and fee columns and the portal listing paints in its tally columns.
 */
const ABSENT_TITLE_MARK = '\u2014';

/**
 * What the absent-title mark means, for assistive technology only. MIGRATION: AUTHORED, because the
 * legacy screen could not express this.
 */
const ABSENT_TITLE_DESCRIPTION = 'no title recorded';

/**
 * Builds the phrase that identifies one module inside a command's accessible name. ⚠ THIS EXISTS BECAUSE
 * THE DESTRUCTIVE COMMAND ANNOUNCED ITSELF AS `"Delete "`.
 *
 * @param row The module placement as the listing holds it.
 * @returns A non-empty phrase naming the module.
 */
function describeModule(row: ModuleListItem): string {
  const title: string = (row.moduleTitle ?? '').trim();

  if (title.length > 0) {
    return title;
  }

  const derived: string = ((row.friendlyName ?? '').trim() || (row.moduleName ?? '').trim()).trim();

  return derived.length > 0
    ? `${derived} (module ${row.moduleId})`
    : `module ${row.moduleId}`;
}

const REMOVE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Module ?';

/**
 * Column headings. MEASURED from `Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx`,
 * whose fifty-nine entries were read directly.
 */
const COLUMN_LABEL = Object.freeze({
  /** Authored: the identity column has no resource key in the module resources. */
  moduleId: 'ID',
  /** `plTitle.Text`, `'Title:'`. */
  moduleTitle: 'Title',
  /** `plFriendlyName.Text`, `'Module:'`. */
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
   * Accessible name of the row-command column, announced but not painted. legacy practice was
   * inconsistent - of the eight in-scope grids exactly one labelled its command columns and
   * `Website/admin/Portal/portals.ascx` supplied no heading text for its two (`:L21` and `:L22` declare
   * no `HeaderText`).
   */
  rowCommands: 'Actions',
});

/** Wording of the three visibility states. MEASURED TWICE and the two agree. */
const VISIBILITY_LABEL: Readonly<Record<ModuleVisibility, string>> = Object.freeze({
  [ModuleVisibility.Maximized]: 'Maximized',
  [ModuleVisibility.Minimized]: 'Minimized',
  [ModuleVisibility.None]: 'None',
});

/**
 * Confirmation shown once a removal has succeeded. Phrased about the PLACEMENT rather than about the
 * module, and deliberately so: after a `204` the module itself may well still exist.
 */
const REMOVE_SUCCESS_MESSAGE = 'The module placement was removed.';

/**
 * The sort names the listing endpoint accepts, as this screen's sortable column keys. A column's key IS
 * the sort name it sends, so a sortable column whose key the endpoint does not recognise offers a control
 * that produces a rejected request - which is worse than offering none.
 */
const SORTABLE_KEY = Object.freeze({
  moduleId: 'moduleId',
  moduleTitle: 'moduleTitle',
  startDate: 'startDate',
  endDate: 'endDate',
});

/**
 * The column the endpoint orders by when the request names none. ⚠ THIS IS AN OBSERVED SERVER BEHAVIOUR,
 * NOT A CHOICE MADE HERE, and it is recorded because the screen has to tell the reader the truth about
 * what they are looking at.
 */
const DEFAULT_SORT_KEY: string = SORTABLE_KEY.moduleTitle;

/** The direction {@link DEFAULT_SORT_KEY} is applied in by the endpoint's own default ordering. */
const DEFAULT_SORT_DIRECTION: SortDirection = 'Ascending';

/**
 * The search term, ordering and page this listing is showing, as the address states them. Held as one
 * object because they are restored TOGETHER on entry: applying them one at a time would issue one read
 * per coordinate, and both the search and the ordering reset the page on their way through, which would
 * discard the page the address had just asked for.
 */
interface ModuleListAddressQuery {
  /** The search term, or `null` for none. */
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
 * Writes a listing query back out as address parameters. A default coordinate is emitted as `null`, which
 * the router REMOVES from the address rather than writing as an empty value - so an unsearched, unordered
 * first page is the bare path.
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

/** Route addresses this screen links to. Assembled as plain path strings and handed to `routerLink`. */
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

@Component({
  selector: 'app-module-list',
  standalone: true,
  // The built-in control-flow blocks need no import, so `CommonModule` is deliberately absent - as are the
  // shared spinner and empty-state components, which the table renders itself from its own imports and
  // which would be duplicated if they appeared here.
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
  // DEPENDENCIES
  // Two, and both are application-wide singletons resolved from the root injector. No `providers` array
  // appears on this component: a component-level provider would give this screen its own store instance and
  // silently detach it from every other module screen.

  /** The single source of truth for the listing, its filters and its failures. */
  private readonly store = inject(ModuleStore);

  /** The address this screen reads its search, ordering and page from, and writes them back to. */
  private readonly route = inject(ActivatedRoute);

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
   * Whether the caller administers this tenant. ⚠ THE FIRST ARM OF A TWO-ARM GATE, AND OMITTING IT WOULD
   * HIDE WORKING CONTROLS. The four row commands are all `ModuleEdit` on the API, so the persisted `EDIT`
   * key is what governs them and the shared permission directive is the right instrument — but the key
   * list the client holds is derived from GRANT ROWS ALONE. A portal administrator who has never been
   * named in a grant row holds no keys at all, and the API admits them to every one of these operations
   * anyway, because each of those policies has an administrator arm.
   */
  protected readonly holdsAdministration: Signal<boolean> = this.authStore.holdsPortalAdministration;

  /**
   * The persisted key that admits the row commands, for the second arm of the gate. ⚠ THE UNION SEMANTICS
   * ARE WHAT MAKE THIS CORRECT. The key list the client holds is the union of everything the caller holds
   * ANYWHERE in the portal — the API's own register calls it "the set an administration shell needs to
   * decide which sections to offer" — so a caller granted `EDIT` on a single module holds `EDIT` here and
   * keeps the commands.
   */
  protected readonly editPermissionKey: PermissionKey = 'EDIT';

  // CELL TEMPLATES
  // Four cells cannot be plain bound text, and the shared table's column contract says so outright: a text
  // column is for "a string, a number or a large integer" and "a boolean, a date object or a nested object
  // must go through" a formatter or a template column.

  /** The three per-row links and the removal command. */
  @ViewChild('rowCommands', { static: true })
  private rowCommandsTemplate?: TemplateRef<DataTableCellContext<ModuleListItem>>;

  /**
   * The failure banner, so a new failure can be brought into view. ⚠ THE BANNER RATHER THAN THE COMMANDS
   * BESIDE IT, and the difference was measured.
   */
  @ViewChild('failureBanner', { read: ElementRef })
  private failureBanner?: ElementRef<HTMLElement>;

  /**
   * The shared search control, so the box can be reconciled with the filter in force. `static: true`
   * because the reconciling effect in the constructor runs before the first change detection completes,
   * and a control resolved later would miss the filter it arrived with.
   */
  @ViewChild(SearchInputComponent, { static: true })
  private searchControl?: SearchInputComponent;

  /**
   * The query this screen last asked for, or `undefined` when it has asked for none yet. ⚠ THIS IS AN
   * ECHO GUARD AND REMOVING IT WOULD ERASE THE OPERATOR'S KEYSTROKES. The reconciling effect writes the
   * filter in force into the search box, and adopting a term also CANCELS whatever emission the box has
   * pending.
   */
  private ownQueryRequest: string | null | undefined = undefined;

  /**
   * The instance title, rendered so that its ABSENCE is visible rather than blank. Measured before this
   * template existed: the cell's `innerHTML` and `textContent` were both `" "` - two literal spaces, one
   * text node, no element children - and Chrome's accessibility tree reported the cell as unnamed with no
   * children.
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

  // STATE OWNED BY THIS SCREEN
  // Two slices, and no more. Everything else this screen shows belongs to the store, which is where a
  // filter, a page coordinate and an ordering are held so that they survive a navigation away and back.

  /** Backing store of {@link columns}; written exactly once, in {@link ngOnInit}. */
  private readonly columnSet = signal<readonly DataTableColumn<ModuleListItem>[]>([]);

  /**
   * The placement whose removal is awaiting confirmation, or `null` when none is. THE WHOLE ROW, not an
   * identifier, and that is a deliberate choice about sentinels.
   */
  private readonly pendingRemoval = signal<ModuleListItem | null>(null);

  /** The placement whose removal has been requested but whose outcome has not yet been reported. */
  private readonly awaitedRemoval = signal<ModuleListItem | null>(null);

  // STORE-DERIVED SURFACE

  /**
   * The placements to paint. A fresh array arrives on every re-query, because the store replaces the
   * whole page envelope rather than mutating it - which is what the shared table requires, since it takes
   * row identity from the object reference.
   */
  protected readonly modules = this.store.modules;

  /**
   * The paging coordinates of the page in hand: total, zero-based index, applied size and page count.
   * Read from the RESPONSE rather than from the request, so the pager describes the page actually on
   * screen instead of one that may still be in flight.
   */
  protected readonly meta = this.store.meta;

  /**
   * Whether a listing request is in flight. Handed to the shared table, which renders the progress
   * indicator and the empty state itself, in one spanning row, and lets loading win over empty.
   */
  /**
   * Whether there is a result COUNT worth stating, which is what mounts the shared pager. ⚠ WIDER THAN
   * "MORE THAN ONE PAGE", AND NARROWER THAN "ALWAYS".
   */
  protected readonly hasResults: Signal<boolean> = computed(() => this.meta().totalCount > 0);

  protected readonly listLoading = this.store.listLoading;

  /**
   * The current failure, or `null`. Held as the structured record the store built - the operation, the
   * problem document, the resolved summary and the server's failure code - and never as pre-rendered
   * markup.
   */
  private readonly failure = this.store.failure;

  /**
   * The problem document behind {@link failure}, or `null`. Bound straight to the shared error banner,
   * whose input takes exactly this type and treats `null` as its empty state.
   */
  protected readonly problem = computed(() => {
    const current: ModuleStoreFailure | null = this.failure();

    return current === null ? null : current.problem;
  });

  /**
   * The key the listing is currently ordered by. ⚠ FALLS BACK TO THE SERVER'S OWN ORDER RATHER THAN TO
   * NOTHING, AND THAT IS THE POINT. When nothing has been asked for, the endpoint does not return the
   * collection unordered - it returns it by title, ascending.
   */
  protected readonly sortBy = computed(() => this.store.query().sortBy ?? DEFAULT_SORT_KEY);

  /** The direction {@link sortBy} is applied in, defaulting with it to the server's own order. */
  protected readonly sortDir = computed(
    () => this.store.query().sortDir ?? DEFAULT_SORT_DIRECTION,
  );

  /** The column descriptors, assembled in {@link ngOnInit} once the cell templates exist. */
  protected readonly columns = this.columnSet.asReadonly();

  /** The placement awaiting confirmation, or `null`. */
  protected readonly removalTarget = this.pendingRemoval.asReadonly();

  // ---------------------------------------------------------------------------------------------------
  // WORDING BOUND BY THE TEMPLATE
  // ---------------------------------------------------------------------------------------------------

  protected readonly pageTitle = PAGE_TITLE;

  protected readonly pageSubtitle = PAGE_SUBTITLE;

  protected readonly tableCaption = TABLE_CAPTION;

  protected readonly searchPlaceholder = SEARCH_PLACEHOLDER;

  protected readonly commandLabel = COMMAND_LABEL;

  protected readonly absentTitleMark = ABSENT_TITLE_MARK;

  protected readonly absentTitleDescription = ABSENT_TITLE_DESCRIPTION;

  protected readonly pageActionLabel = PAGE_ACTION_LABEL;

  protected readonly removeConfirmMessage = REMOVE_CONFIRM_MESSAGE;

  /** Address of the create screen, for the primary page action. */
  protected readonly createLink = ROUTE.create;

  /** Address of the import screen, for the secondary page action. */
  protected readonly importLink = ROUTE.import;

  // ---------------------------------------------------------------------------------------------------
  // OUTCOME REPORTING
  // ---------------------------------------------------------------------------------------------------

  /**
   * Bridges a removal's outcome onto the notification queue. AN EFFECT, BECAUSE EMITTING A USER-VISIBLE
   * MESSAGE IS A GENUINE SIDE EFFECT - and because there is no completion callback to hang one on: the
   * store's commands return `void` and subscribe internally.
   */
  constructor() {
    // ⚠ WITHOUT THIS THE GRID LIES ABOUT WHAT IT IS SHOWING. Measured: search for a term, leave to another
    // screen and come back, and the box reads the empty string while the grid is still filtered - `" 1–10
    // of 248 "` against a collection of 250, with two rows silently withheld and the return request still
    // carrying `query=…`.
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

        // The severity arrives ALREADY RESOLVED, and a refusal is a WARNING rather than an error.
        this.notifications.notify(
          failed.summary.severity,
          failed.summary.message,
          failed.summary.supportReference,
        );
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
  protected readonly moduleRowKey = (row: ModuleListItem): number => row.tabModuleId;

  // LIFECYCLE
  // ---------------------------------------------------------------------------------------------------

  /** Assembles the column set and reads the first page. The ORDER of the two statements matters. */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());

    // ⚠ THE ADDRESS ISSUES THE READ, AND THIS IS THE ONLY PLACE IT IS ISSUED ON ENTRY. Subscribing emits
    // immediately with the address in hand, so the first page is read from that emission rather than from a
    // separate call here - two calls would issue two reads of the same page on every arrival.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((address: ParamMap): void => {
        const query: ModuleListAddressQuery = parseModuleListQuery(address);

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
        // OTHER route to this line - a back navigation, a typed address, a correction - is a term the box
        // has not seen and must be shown. ⚠ THE PAGE IS SET LAST, AND THE ORDER IS LOAD-BEARING. Both
        // `setQuery` and `setSort` return the listing to the first page, which is right when an operator
        // has just searched or re-ordered and wrong when the address is naming all three at once - either
        // of them running after `setPageIndex` would silently discard the page the address asked for.
        this.store.setQuery(query.query);
        this.store.setSort(query.sortBy, query.sortDir);
        this.store.setPageIndex(query.pageIndex);
        this.store.loadModules();
      });
  }

  // ROW ADDRESSES
  // NOT ONE OF THEM TESTS AN IDENTIFIER. There is no truthiness check, no comparison against zero or minus
  // one, no coalescing and no absolute value anywhere below, and that is a correctness requirement rather
  // than a stylistic one: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so module ZERO is the first module of
  // an installation and `if (moduleId)` would silently give it no links at all.

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
   * Applies the reader's free-text filter and re-reads from the first page. The text is recorded EXACTLY
   * AS TYPED. No wildcard character is appended, no pattern syntax is introduced and no escaping is
   * applied: match semantics belong to the server alone, which is where the legacy call-site pattern
   * decoration was moved to during the migration.
   *
   * @param term The text the shared search control emitted, already debounced and trimmed by it.
   */
  protected onSearch(term: string): void {
    const wanted: string | null = term.length === 0 ? null : term;

    // Recorded BEFORE the store is written, because writing it runs the reconciling effect synchronously
    // and the guard has to be in place by the time that effect reads it. See {@link ownQueryRequest} for
    // what goes wrong without it.
    this.ownQueryRequest = wanted;

    // ⚠ THE ADDRESS IS WRITTEN AND THE STORE IS NOT TOUCHED. The subscription in `ngOnInit` applies the
    // term and issues the read, so writing the address is the whole of the change here. Calling the store
    // as well would apply it twice and read twice.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [FILTER_PARAM]: wanted, [PAGE_PARAM]: null },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Whether this module has no title of its own, so the cell must say so.
   *
   * @param row The module placement.
   * @returns `true` when nothing meaningful is stored in the title.
   */
  protected isTitleAbsent(row: ModuleListItem): boolean {
    return (row.moduleTitle ?? '').trim().length === 0;
  }

  /**
   * The accessible name for one row command. Composed rather than concatenated in the template, so that
   * the verb and the identifying phrase are joined in ONE place and cannot drift between the four
   * commands.
   *
   * @param verb The command's own wording.
   * @param row The module placement the command acts on.
   * @returns The verb followed by a phrase that identifies the module.
   */
  protected commandName(verb: string, row: ModuleListItem): string {
    return `${verb} ${describeModule(row)}`;
  }

  /**
   * Applies the reader's ordering and re-reads from the first page. The key is the column's own key,
   * which IS the sort name the endpoint accepts - only the columns keyed by a permitted name declare
   * themselves sortable, so no control here can produce a rejected request.
   *
   * @param change The key the reader activated and the direction to apply, or null to stop ordering.
   */
  protected onSortChange(change: DataTableSortChange): void {
    const direction: SortDirection | null = change.direction;

    // THE ORDERING GOES INTO THE ADDRESS, and the third step of the cycle clears the KEY as well as the
    // direction: a request carrying a key with no direction would be a different question asked of the
    // server, and the reader who pressed a third time asked for no ordering at all.
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
   * Moves to another page and re-reads. NO ARITHMETIC, IN EITHER DIRECTION, AND THAT IS THE WHOLE
   * CONTRACT. The wire's page coordinate is zero-based; the shared pager's `page` input IS that
   * zero-based index and its change event emits a zero-based index back.
   *
   * @param pageIndex The zero-based page the reader asked for.
   */
  protected onPageChange(pageIndex: number): void {
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
   * Opens the confirmation for one placement. Records the whole row, because both of its identities are
   * needed to address a single placement and because a `null` is the only representation of "nothing
   * pending" that cannot collide with a real identifier.
   *
   * @param row The placement the reader asked to remove.
   */
  protected onRequestDelete(row: ModuleListItem): void {
    this.pendingRemoval.set(row);
  }

  /**
   * Issues the removal for the confirmed placement. ADDRESSES THE PLACEMENT, NOT THE MODULE, and passes
   * BOTH identities for that reason: a module whose all-pages flag is set has one placement per page, so
   * the module identity alone does not name a single row.
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

  /** Dismisses the confirmation without removing anything. */
  protected onCancelDelete(): void {
    this.pendingRemoval.set(null);
  }

  /**
   * Re-issues the listing read after a reported failure. The failure is cleared FIRST and the read issued
   * second, so a second failure produces a fresh report rather than being indistinguishable from the one
   * still on screen, and the awaited removal is dropped for the same reason the dismissal drops it:
   * nothing may be left waiting on an outcome that will never arrive.
   */
  protected onRetryRead(): void {
    this.awaitedRemoval.set(null);
    this.store.clearFailure();
    this.store.loadModules();
  }

  /** Dismisses the failure banner. */
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
   * @returns The ten descriptors, in the measured legacy order.
   * @throws Error when a required cell template is missing from the sibling template file.
   */
  private buildColumns(): readonly DataTableColumn<ModuleListItem>[] {
    return [
      // 1. The row commands.
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

      // 3. The primary label - the INSTANCE title, mirroring `portals.ascx:L30`.
      {
        kind: 'template',
        key: SORTABLE_KEY.moduleTitle,
        label: COLUMN_LABEL.moduleTitle,
        sortable: true,
        headerAlign: 'center',
        bodyAlign: 'start',
        cellTemplate: this.requireTemplate(this.titleCellTemplate, 'titleCell'),
      },

      // 4. The DEFINITION's display name - the legacy `'Module:'` label.
      {
        key: 'friendlyName',
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

      // 6. The all-pages flag.
      {
        key: 'allTabs',
        label: COLUMN_LABEL.allTabs,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.allTabsCellTemplate, 'allTabsCell'),
      },

      // 7. The visibility state.
      {
        key: 'visibility',
        label: COLUMN_LABEL.visibility,
        headerAlign: 'center',
        bodyAlign: 'center',
        value: (row: ModuleListItem): string => this.visibilityLabel(row.visibility),
      },

      // 8. The placement's position within its pane - the numeric column, mirroring the counts at
      // `portals.ascx:L44-L45`.
      {
        key: 'moduleOrder',
        label: COLUMN_LABEL.moduleOrder,
        headerAlign: 'center',
        bodyAlign: 'center',
        field: 'moduleOrder',
      },

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
   * Unwraps a captured cell template, failing loudly and by name when it is absent. THROWS rather than
   * degrading.
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
