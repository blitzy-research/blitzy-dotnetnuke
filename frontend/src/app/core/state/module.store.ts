import { Injectable, computed, inject, signal, type OnDestroy } from '@angular/core';
import { EMPTY, Subscription, expand, reduce, throwError } from 'rxjs';

import { isProblemDetails } from '../models/problem-details.model';
import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE, emptyPagedResult } from '../models/paged-result.model';
import { ModuleService } from '../services/module.service';
import { TabService } from '../services/tab.service';
import { failureCode, summarizeProblem, transportProblem } from '../utils/form-errors.util';
import { OperationGeneration } from '../utils/operation-generation.util';

import type {
  CreateModuleRequest,
  ModuleDefinition,
  ModuleDetail,
  ModuleExportRequest,
  ModuleImportRequest,
  ModuleListItem,
  ModuleListPage,
  ModuleSettingsBag,
  UpdateModuleRequest,
} from '../models/module.model';
import type { Observable } from 'rxjs';

import type { ApiMeta, SortDirection } from '../models/paged-result.model';
import type { ProblemDetails } from '../models/problem-details.model';
import type { TabListItem } from '../models/tab.model';
import type { ProblemSummary } from '../utils/form-errors.util';

/**
 * Reported when a module read answers with a record other than the one that was asked for. A refusal
 * rather than a silent discard, because this outcome means the transport, a proxy or the server disagreed
 * with this store about which record was requested, and an operator who sees nothing at all would simply
 * try again.
 */
const MISMATCHED_MODULE_MESSAGE =
  'The server answered with a different module from the one requested. Nothing was loaded.';

/** The hard ceiling on how many pages one complete choice-set walk will request. */
export const MAXIMUM_CHOICE_PAGES = 500;

// SERVICE ARGUMENT TYPES, DERIVED RATHER THAN IMPORTED OR RESTATED
// The transport's paging, filtering and placement arguments are declared by the query-parameter helper
// module under `core/utils/`, which owns the spelling of every query key.

/**
 * The paging, ordering and free-text arguments of the module listing. Every member is optional and
 * nullable, because an omitted member is not transmitted and the server then applies its own default.
 */
type ModuleListQuery = Parameters<ModuleService['listModules']>[0];

/** The page and recycle-bin restrictions the module listing accepts alongside the paging arguments. */
type ModuleListFilterState = NonNullable<Parameters<ModuleService['listModules']>[1]>;

/** The selector that narrows a module operation to ONE of its placements. */
type ModulePlacement = NonNullable<Parameters<ModuleService['getModule']>[1]>;

// =====================================================================================================
// THE PAGE TREE NODE
// =====================================================================================================

/** One node of the page hierarchy that {@link ModuleStore.tabTree} derives from the flat page list. */
export interface TabTreeNode {
  /** The page this node stands for, exactly as the listing returned it. */
  readonly tab: TabListItem;

  /** The depth of this node within the tree THIS store built, where a root node is zero. */
  readonly depth: number;

  /** This node's children, ordered by page order. Empty for a leaf - never absent. */
  readonly children: readonly TabTreeNode[];
}

/** The node under construction, whose child list is still being appended to. */
interface TabTreeNodeBuilder {
  readonly tab: TabListItem;
  readonly depth: number;
  readonly children: TabTreeNode[];
}

/** The derived page hierarchy, together with every page the hierarchy could not place. */
export interface TabHierarchy {
  /** The root-level pages, ordered by page order, each carrying its descendants. */
  readonly roots: readonly TabTreeNode[];

  /**
   * Every page that could not be attached to a root, in the order the listing returned it. SURFACED
   * RATHER THAN DISCARDED. A page lands here for one of exactly two reasons: it names a parent that is
   * not present in this response - which is ordinary when a caller holds a partial list or when a parent
   * is filtered out - or it participates in a parent cycle, which is malformed data.
   */
  readonly orphans: readonly TabListItem[];
}

// =====================================================================================================
// FAILURE STATE
// =====================================================================================================

/** Which command a failure came from. */
export type ModuleStoreOperation =
  | 'listModules'
  | 'loadChoices'
  | 'loadModule'
  | 'createModule'
  | 'updateModule'
  | 'deleteModule'
  | 'loadSettings'
  | 'saveSettings'
  | 'exportModule'
  | 'importModule'
  | 'loadTabs'
  | 'loadDefinitions'
  | 'loadDefinition'
  | 'loadDesktopDefinitions';

/**
 * One failure, resolved once and held structurally. THE STORE HOLDS THE STRUCTURED PROBLEM AND NEVER
 * PRE-RENDERED MARKUP. The server answers with an RFC 7807 document under `application/problem+json`, and
 * every string a person reads is produced by `core/utils/form-errors.util.ts`, whose output is plain text
 * with legacy break markup already resolved.
 */
export interface ModuleStoreFailure {
  /** The command that failed. */
  readonly operation: ModuleStoreOperation;

  /**
   * The problem document as the server sent it, or `null` when the response carried none. RETAINED WHOLE,
   * AND `traceId` IS THE REASON. The document's trace identifier is derived server-side from the ambient
   * activity or the request identifier, so the correlation value the application sends on every request
   * round-trips back into this body.
   */
  readonly problem: ProblemDetails | null;

  /** Everything the presentation layer needs about the failure, resolved once. */
  readonly summary: ProblemSummary;

  /** The failure code the server published, or `null` when the document carried none. */
  readonly code: string | null;
}

// PURE HELPERS

/**
 * Reads one member of an unknown value without asserting anything about the whole. The transport failure
 * that reaches a subscriber is deliberately typed `unknown`: this file must not import the framework's
 * HTTP module, and the interceptor that owns transport narrowing re-throws the original failure rather
 * than a translated one.
 *
 * @param source Any value, including `null` and a primitive.
 * @param key The member to read.
 * @returns The member's value, or `undefined` when the source cannot carry members.
 */
function readMember(source: unknown, key: string): unknown {
  if (typeof source !== 'object' || source === null) {
    return undefined;
  }

  return (source as Readonly<Record<string, unknown>>)[key];
}

/**
 * Extracts the RFC 7807 document from a failed request, or synthesises the minimum from its status. ⚠ THE
 * TRANSPORT STATUS IS RESOLVED FIRST, AND A STATUS OF ZERO SHORT-CIRCUITS BEFORE THE BODY IS LOOKED AT.
 * That ordering is a correctness requirement, and getting it wrong produced incoherent wording rather
 * than an obvious fault.
 *
 * @param cause The value a subscriber's error callback received.
 * @returns The problem document, or `null` when neither a document nor a status could be read.
 */
function problemFromCause(cause: unknown): ProblemDetails | null {
  const status: unknown = readMember(cause, 'status');

  // ⚠ IT USED TO RETURN `{ status: 0 }`, AND THAT LEFT THE BANNER WITHOUT A TITLE. Measured on the module
  // listing with the network unreachable: the banner rendered its severity word and its message, but
  // `.error-banner__title` matched NOTHING — an unfilled Angular anchor sat where the title belongs —
  // because a document carrying only a status has no title to render.
  if (status === 0) {
    return transportProblem(0);
  }

  const body: unknown = readMember(cause, 'error');

  if (isProblemDetails(body)) {
    return body;
  }

  if (typeof body === 'string' && body.trim().length > 0) {
    const parsed: ProblemDetails | null = parseProblemText(body);

    if (parsed !== null) {
      return parsed;
    }
  }

  return typeof status === 'number' ? { status } : null;
}

/**
 * Parses a textual response body as a problem document.
 *
 * @param text A non-blank response body.
 * @returns The document, or `null` when the text is not a problem document.
 */
function parseProblemText(text: string): ProblemDetails | null {
  try {
    const parsed: unknown = JSON.parse(text);

    return isProblemDetails(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * Whether a page sits at the root of the hierarchy. THE ROOT TEST IS EXPLICIT, AND IT ADMITS BOTH THE
 * WIRE FORM AND THE LEGACY FORM. This is the single most defect-prone decision in this file, and the two
 * authorities genuinely disagree, so both are honoured.
 *
 * @param parentId The page's parent reference, as the contract declares it.
 * @returns True when the page has no parent within the hierarchy.
 */
function isRootParent(parentId: number | null): boolean {
  return parentId === null || parentId === -1;
}

/**
 * Whether a page hangs beneath another page, narrowing its parent reference to a usable key.
 *
 * @param parentId The page's parent reference, as the contract declares it.
 * @returns True when the reference names a parent page.
 */
function isNestedUnder(parentId: number | null): parentId is number {
  return !isRootParent(parentId);
}

/**
 * Orders two pages as siblings. Page order is the primary key, exactly as the legacy hierarchy ordered
 * itself.
 *
 * @param left One page.
 * @param right The other page.
 * @returns A negative number, zero or a positive number, as a comparator requires.
 */
function compareSiblings(left: TabListItem, right: TabListItem): number {
  if (left.tabOrder !== right.tabOrder) {
    return left.tabOrder - right.tabOrder;
  }

  return left.tabId - right.tabId;
}

/**
 * Derives the page hierarchy from a flat page list, reporting whatever it could not place.
 *
 * @param tabs The flat page list, in the order the listing returned it.
 * @returns The root nodes with their descendants, and every unplaceable page.
 */
function buildTabHierarchy(tabs: readonly TabListItem[]): TabHierarchy {
  const known = new Map<number, TabListItem>();

  for (const tab of tabs) {
    known.set(tab.tabId, tab);
  }

  const childrenByParent = new Map<number, TabListItem[]>();
  const rootTabs: TabListItem[] = [];
  const orphans: TabListItem[] = [];

  for (const tab of tabs) {
    const parentId: number | null = tab.parentId;

    if (isNestedUnder(parentId)) {
      const siblings: TabListItem[] | undefined = childrenByParent.get(parentId);

      if (siblings === undefined) {
        childrenByParent.set(parentId, [tab]);
      } else {
        siblings.push(tab);
      }
    } else {
      rootTabs.push(tab);
    }
  }

  // A parent reference that names no page in this response cannot be attached, so its children are
  // reported. Resolved before the walk so the walk itself has nothing to decide.
  for (const [parentId, siblings] of childrenByParent) {
    if (!known.has(parentId)) {
      orphans.push(...siblings);
    }
  }

  rootTabs.sort(compareSiblings);

  const placed = new Set<number>();
  const roots: TabTreeNodeBuilder[] = [];
  const pending: TabTreeNodeBuilder[] = [];

  for (const tab of rootTabs) {
    const node: TabTreeNodeBuilder = { tab, depth: 0, children: [] };

    placed.add(tab.tabId);
    roots.push(node);
    pending.push(node);
  }

  // The loop condition performs the narrowing, so there is no unreachable guard inside the body: an
  // empty stack ends the walk rather than being tested for and broken out of.
  for (let node = pending.pop(); node !== undefined; node = pending.pop()) {
    const siblings: TabListItem[] | undefined = childrenByParent.get(node.tab.tabId);

    if (siblings === undefined) {
      continue;
    }

    const ordered: TabListItem[] = [...siblings].sort(compareSiblings);

    for (const child of ordered) {
      // The guard that makes the walk total. A page already placed is never expanded again, which is
      // what bounds the traversal on malformed data without a separate cycle detector.
      if (placed.has(child.tabId)) {
        continue;
      }

      const childNode: TabTreeNodeBuilder = { tab: child, depth: node.depth + 1, children: [] };

      placed.add(child.tabId);
      node.children.push(childNode);
      pending.push(childNode);
    }
  }

  // Anything the walk never reached is in a cycle, since every page naming an absent parent was already
  // reported above. Reported in listing order, and never dropped.
  const reported = new Set<number>();

  for (const tab of orphans) {
    reported.add(tab.tabId);
  }

  for (const tab of tabs) {
    if (!placed.has(tab.tabId) && !reported.has(tab.tabId)) {
      reported.add(tab.tabId);
      orphans.push(tab);
    }
  }

  return { roots, orphans };
}

// =====================================================================================================
// THE STORE
// =====================================================================================================

/**
 * Module-administration state for the module feature screens. Registered at the application root so that
 * a listing, a form, a settings panel and the two transfer panels share one selection and one view of the
 * catalogue.
 */
@Injectable({ providedIn: 'root' })
export class ModuleStore implements OnDestroy {
  private readonly moduleService = inject(ModuleService);
  private readonly tabService = inject(TabService);

  private listRequest: Subscription | null = null;
  private moduleRequest: Subscription | null = null;
  private settingsRequest: Subscription | null = null;
  private definitionsRequest: Subscription | null = null;
  private definitionRequest: Subscription | null = null;
  private desktopDefinitionsRequest: Subscription | null = null;
  private tabsRequest: Subscription | null = null;

  /** The export in flight, held so that closing the transfer panel can release it. */
  private exportRequest: Subscription | null = null;
  /**
   * The picker-choice read in flight, or null when none is. Held separately from {@link
   * ModuleStore.listRequest} because the two reads answer different questions over the same endpoint: the
   * listing is the paged grid somebody is reading, the choices are the whole tenant's modules used to
   * populate a picker.
   */
  private choicesRequest: Subscription | null = null;

  private readonly writeRequests = new Set<Subscription>();

  private readonly moduleReads = new OperationGeneration();

  /** Guards the settings bag. */
  private readonly settingsReads = new OperationGeneration();

  /** Guards the exported document, whose delivery is the most consequential commit in this store. */
  private readonly exportOperations = new OperationGeneration();

  // ---------------------------------------------------------------------------------------------------
  // WRITABLE SLICES
  // ---------------------------------------------------------------------------------------------------

  /** The current page of the module listing. */
  private readonly _page = signal<ModuleListPage>(emptyPagedResult<ModuleListItem>());

  /**
   * The paging, ordering and free-text arguments the next listing request will carry. THE PAGE INDEX HELD
   * HERE IS ZERO-BASED, WHICH IS THE WIRE'S CONVENTION. The transport performs no arithmetic on it in
   * either direction and states that "the mapping between the two lives in the feature store", so this
   * member is the server's coordinate and a pager may present a one-based counter over it.
   */
  private readonly _query = signal<ModuleListQuery>({
    pageIndex: 0,
    pageSize: DEFAULT_PAGE_SIZE,
  });

  /** The page and recycle-bin restrictions the next listing request will carry. */
  private readonly _filter = signal<ModuleListFilterState>({});

  /** Whether a listing request is in flight. */
  private readonly _listLoading = signal(false);

  /**
   * Modules read as CHOICES for a picker, held apart from the browsable listing. ⚠ THIS SLICE EXISTS SO
   * THAT A PICKER CANNOT DISTURB A LISTING. A screen that needs every module as options wants a different
   * query from a screen that lets an operator page through them: the widest page the endpoint allows,
   * unordered, unfiltered.
   */
  private readonly _choices = signal<readonly ModuleListItem[]>([]);

  /**
   * How many modules the SERVER said match, which is not necessarily how many are in {@link
   * ModuleStore._choices}. ⚠ THIS EXISTS SO THAT AN INCOMPLETE WALK CANNOT LOOK COMPLETE. The walk in
   * {@link ModuleStore.loadChoices} gathers every page, so the two normally agree — and a consumer that
   * finds them disagreeing is looking at a set the page ceiling truncated, which is a fact about the
   * server rather than about the tenant.
   */
  private readonly _choicesTotalCount = signal(0);

  /** Whether a choices request is in flight. */
  private readonly _choicesLoading = signal(false);

  /** The module currently being read or edited, or `null` when none has been loaded. */
  private readonly _module = signal<ModuleDetail | null>(null);

  /**
   * The module the screens are working with, or `undefined` when none is selected. ABSENCE IS `undefined`
   * ALONE - never zero and never minus one.
   */
  private readonly _selectedModuleId = signal<number | undefined>(undefined);

  /** Whether a single-module read is in flight. */
  private readonly _moduleLoading = signal(false);

  /** Whether a create, replace or remove is in flight. */
  private readonly _saving = signal(false);

  /**
   * The loaded settings, or `null` when none have been read. TWO MAPS, HELD SEPARATELY AND NEVER MERGED.
   * One is recorded against the module and is identical on every page it appears on; the other belongs to
   * a single occurrence on a single page.
   */
  private readonly _settings = signal<ModuleSettingsBag | null>(null);

  /** Whether a settings read is in flight. */
  private readonly _settingsLoading = signal(false);

  /** Whether a settings replacement is in flight. */
  private readonly _settingsSaving = signal(false);

  /** The definition catalogue. */
  private readonly _definitions = signal<readonly ModuleDefinition[]>([]);

  /** The definitions belonging to one deployed bundle. */
  private readonly _desktopDefinitions = signal<readonly ModuleDefinition[]>([]);

  /**
   * One definition read on its own, or `null` when none has been read. Held apart from the catalogue
   * rather than merged into it, because a single read answers for a definition the catalogue may not
   * contain - the catalogue is scoped to what the resolved tenant may use, and a single read is not.
   */
  private readonly _definition = signal<ModuleDefinition | null>(null);

  /**
   * How many definition reads are outstanding: the catalogue, one bundle's definitions, and one
   * definition on its own. ⚠ A COUNT RATHER THAN A BOOLEAN, AND THE DIFFERENCE IS A DEFECT EITHER WAY IT
   * IS GOT WRONG. Three commands read definitions into three separate slices and all three report through
   * the one public flag below, because the only consumer asks a single question - "is a definition read
   * outstanding?" - and does not care which.
   */
  private readonly _definitionReadsInFlight = signal<number>(0);

  /** The flat page list for the portal in scope. */
  private readonly _tabs = signal<readonly TabListItem[]>([]);

  /**
   * The portal whose pages are loaded, or `undefined` when none have been. A PORTAL IDENTIFIER OF MINUS
   * ONE **OR** ZERO IS REAL. `dbo.Portals.PortalID` is `IDENTITY (-1, 1)`
   * (`01.00.00.SqlDataProvider:L77`), so the first portal is numbered minus one and the second is
   * numbered zero.
   */
  private readonly _tabPortalId = signal<number | undefined>(undefined);

  /** The page the screens are working with, or `undefined` when none is selected. */
  private readonly _selectedTabId = signal<number | undefined>(undefined);

  /** Whether a page-list read is in flight. */
  private readonly _tabsLoading = signal(false);

  private readonly _exportedContent = signal<string | null>(null);

  /** Whether an export is in flight. */
  private readonly _exporting = signal(false);

  /** Whether an import is in flight. */
  private readonly _importing = signal(false);

  /** Whether the most recent import succeeded. */
  private readonly _importCompleted = signal(false);

  /** The most recent failure, or `null` when the last command in each area succeeded. */
  private readonly _failure = signal<ModuleStoreFailure | null>(null);

  private readonly _settingsFailure = signal<ModuleStoreFailure | null>(null);

  // ---------------------------------------------------------------------------------------------------
  // PUBLIC PROJECTIONS
  // ---------------------------------------------------------------------------------------------------

  /** The current page of the listing, including its server-reported paging coordinates. */
  readonly page = this._page.asReadonly();

  /** The arguments the next listing request will carry. The page index is zero-based. */
  readonly query = this._query.asReadonly();

  /** The restrictions the next listing request will carry. */
  readonly filter = this._filter.asReadonly();

  /** Whether a listing request is in flight. */
  readonly listLoading = this._listLoading.asReadonly();

  /** Every module read as a picker choice. */
  readonly choices = this._choices.asReadonly();

  /** How many modules the server reported for the picker. */
  readonly choicesTotalCount = this._choicesTotalCount.asReadonly();

  /** Whether the picker holds every module the server reported. */
  readonly choicesComplete = computed<boolean>(() => this._choices().length >= this._choicesTotalCount());

  /** Whether a choices request is in flight. */
  readonly choicesLoading = this._choicesLoading.asReadonly();

  /**
   * How many modules the SERVER said the picker is choosing among. Published so a screen can state the
   * size of the set it is offering.
   */
  readonly choicesTotal = this._choicesTotalCount.asReadonly();

  /** The module currently loaded, or `null`. */
  readonly module = this._module.asReadonly();

  /** The selected module, or `undefined`. */
  readonly selectedModuleId = this._selectedModuleId.asReadonly();

  /** Whether a single-module read is in flight. */
  readonly moduleLoading = this._moduleLoading.asReadonly();

  /** Whether a create, replace or remove is in flight. */
  readonly saving = this._saving.asReadonly();

  /** The loaded settings, or `null`. */
  readonly settings = this._settings.asReadonly();

  /** Whether a settings read is in flight. */
  readonly settingsLoading = this._settingsLoading.asReadonly();

  /** How the settings READ ended, independently of the shared slot. */
  readonly settingsFailure = this._settingsFailure.asReadonly();

  /** Whether a settings replacement is in flight. */
  readonly settingsSaving = this._settingsSaving.asReadonly();

  /** The definition catalogue, unpaged. */
  readonly definitions = this._definitions.asReadonly();

  /** One bundle's definitions, unpaged. */
  readonly desktopDefinitions = this._desktopDefinitions.asReadonly();

  /** One definition read on its own, or `null`. */
  readonly definition = this._definition.asReadonly();

  /**
   * Whether ANY definition read is in flight - the catalogue, one bundle's, or one definition. Derived
   * from {@link ModuleStore._definitionReadsInFlight} rather than mirroring one boolean, so it stays true
   * until the last outstanding read has settled.
   */
  readonly definitionsLoading = computed<boolean>(() => this._definitionReadsInFlight() > 0);

  /** The flat page list, unpaged, in the order the listing returned it. */
  readonly tabs = this._tabs.asReadonly();

  /** The portal whose pages are loaded, or `undefined`. */
  readonly tabPortalId = this._tabPortalId.asReadonly();

  /** The selected page, or `undefined`. */
  readonly selectedTabId = this._selectedTabId.asReadonly();

  /** Whether a page-list read is in flight. */
  readonly tabsLoading = this._tabsLoading.asReadonly();

  /** The exported document as an opaque string, or `null`. */
  readonly exportedContent = this._exportedContent.asReadonly();

  /** Whether an export is in flight. */
  readonly exporting = this._exporting.asReadonly();

  /** Whether an import is in flight. */
  readonly importing = this._importing.asReadonly();

  /** Whether the most recent import succeeded. */
  readonly importCompleted = this._importCompleted.asReadonly();

  /** The most recent failure, or `null`. */
  readonly failure = this._failure.asReadonly();

  // ---------------------------------------------------------------------------------------------------
  // DERIVED VIEWS
  // ---------------------------------------------------------------------------------------------------

  /** The rows of the current page. */
  readonly modules = computed<readonly ModuleListItem[]>(() => this._page().items);

  /** The server-reported paging coordinates of the current page. */
  readonly meta = computed<ApiMeta>(() => this._page().meta);

  /** The total number of matching rows the server reported, across all pages. */
  readonly totalCount = computed<number>(() => this._page().meta.totalCount);

  /** Whether the current page carries at least one row. */
  readonly hasModules = computed<boolean>(() => this._page().items.length > 0);

  /** Whether any request this store issues is in flight. */
  readonly busy = computed<boolean>(
    () =>
      this._listLoading() ||
      this._choicesLoading() ||
      this._moduleLoading() ||
      this._saving() ||
      this._settingsLoading() ||
      this._settingsSaving() ||
      this._definitionReadsInFlight() > 0 ||
      this._tabsLoading() ||
      this._exporting() ||
      this._importing(),
  );

  /** The page hierarchy derived from the flat page list, with every unplaceable page reported. */
  readonly tabHierarchy = computed<TabHierarchy>(() => buildTabHierarchy(this._tabs()));

  /** The root-level pages with their descendants, ordered by page order at every level. */
  readonly tabTree = computed<readonly TabTreeNode[]>(() => this.tabHierarchy().roots);

  /** Every page the hierarchy could not place - an absent parent, or a parent cycle. */
  readonly orphanTabs = computed<readonly TabListItem[]>(() => this.tabHierarchy().orphans);

  /**
   * The definitions whose bundle supports content transfer. MIGRATION: `false` HERE IS DATA, NOT ABSENCE.
   * The legacy sentinel helper reported `False` itself as "absent", so the legacy code could not tell a
   * module that does not support transfer from one whose support was unknown.
   */
  readonly portableDefinitions = computed<readonly ModuleDefinition[]>(() =>
    this._definitions().filter((definition) => definition.isPortable),
  );

  /** The selected page, resolved against the loaded page list, or `undefined` when it is not present. */
  readonly selectedTab = computed<TabListItem | undefined>(() => {
    const tabId: number | undefined = this._selectedTabId();

    if (tabId === undefined) {
      return undefined;
    }

    return this._tabs().find((tab) => tab.tabId === tabId);
  });

  // SELECTION

  /**
   * Selects the module the screens are working with, or clears the selection.
   *
   * @param moduleId The module to select.
   */
  selectModule(moduleId: number | undefined): void {
    this._selectedModuleId.set(moduleId);
  }

  /**
   * Selects the page the screens are working with, or clears the selection.
   *
   * @param tabId The page to select.
   */
  selectTab(tabId: number | undefined): void {
    this._selectedTabId.set(tabId);
  }

  // LISTING ARGUMENTS
  // Each setter records the argument and nothing else. None issues a request, so a screen can compose a
  // page coordinate, an ordering and a filter and then read once - which is what stops a filter panel from
  // firing a request per keystroke.

  /**
   * Sets the zero-based page coordinate of the next listing request.
   *
   * @param pageIndex The page to request.
   */
  setPageIndex(pageIndex: number): void {
    this._query.update((current) => ({ ...current, pageIndex }));
  }

  /**
   * Sets the page size of the next listing request and returns to the first page. The coordinate is reset
   * because a page index measured in rows of one size does not identify the same rows once the size
   * changes, so keeping it would silently show a different window than the pager claims.
   *
   * @param pageSize The number of rows to request.
   */
  setPageSize(pageSize: number): void {
    this._query.update((current) => ({ ...current, pageSize, pageIndex: 0 }));
  }

  /**
   * Sets the ordering of the next listing request and returns to the first page.
   *
   * @param sortBy The field to order by, or `null` to let the server choose.
   * @param sortDir The direction, or `null` to let the server choose.
   */
  setSort(sortBy: string | null, sortDir: SortDirection | null): void {
    this._query.update((current) => ({ ...current, sortBy, sortDir, pageIndex: 0 }));
  }

  /**
   * Sets the free-text filter of the next listing request and returns to the first page.
   *
   * @param query The text to filter by, or `null` for no filter.
   */
  setQuery(query: string | null): void {
    this._query.update((current) => ({ ...current, query, pageIndex: 0 }));
  }

  /**
   * Restricts the listing to modules placed on one page, or removes the restriction, and returns to the
   * first page.
   *
   * @param tabId The page to restrict to.
   */
  setTabFilter(tabId: number | undefined): void {
    this._filter.update((current) =>
      tabId === undefined
        ? { includeDeleted: current.includeDeleted }
        : { includeDeleted: current.includeDeleted, tabId },
    );
    this._query.update((current) => ({ ...current, pageIndex: 0 }));
  }

  /**
   * Includes or excludes soft-removed modules in the listing, and returns to the first page.
   *
   * @param includeDeleted Whether to include removed modules.
   */
  setIncludeDeleted(includeDeleted: boolean): void {
    this._filter.update((current) => ({ ...current, includeDeleted }));
    this._query.update((current) => ({ ...current, pageIndex: 0 }));
  }

  // ---------------------------------------------------------------------------------------------------
  // READS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Reads the current page of the module listing. The tenant is NOT an argument: the server resolves it
   * from the request host before dispatching, so this store neither holds nor sends one.
   */
  loadModules(): void {
    // ⚠ EXACTLY ONE CANCELLATION, AND ONLY OF THIS METHOD'S OWN HANDLE. See the note on the request-handle
    // block: a doubled release was harmless in effect but stated the rule twice, and a method that released
    // a handle it does not own broke the rule outright.
    this.listRequest?.unsubscribe();
    this._listLoading.set(true);
    this.clearFailure();

    this.listRequest = this.moduleService.listModules(this._query(), this._filter()).subscribe({
      next: (page: ModuleListPage) => {
        this._page.set(page);
        this._listLoading.set(false);
      },
      error: (cause: unknown) => {
        this._listLoading.set(false);
        this.recordFailure('listModules', cause);
      },
    });
  }

  /**
   * Reads every module the endpoint will return in one call, as CHOICES for a picker. ⚠ THIS DOES NOT
   * TOUCH THE BROWSABLE LISTING. Neither {@link ModuleStore.query}, {@link ModuleStore.filter} nor {@link
   * ModuleStore.page} is read or written here, so a screen that opens a picker cannot resize, re-order,
   * re-filter or repaginate a listing a sibling screen is showing.
   */
  loadChoices(): void {
    this.choicesRequest?.unsubscribe();
    this._choicesLoading.set(true);
    this.clearFailure();

    this.choicesRequest = this.readEveryChoice().subscribe({
      next: (page: ModuleListPage) => {
        this._choices.set(page.items);
        this._choicesTotalCount.set(page.meta.totalCount);
        this._choicesLoading.set(false);
      },
      error: (cause: unknown) => {
        this._choices.set([]);
        this._choicesTotalCount.set(0);
        this._choicesLoading.set(false);
        this.recordFailure('loadChoices', cause);
      },
    });
  }

  /**
   * Abandons the picker-choice read and returns its slice to rest. ⚠ THE LEASE A COMPONENT-SCOPED READ
   * NEEDS FROM A ROOT-SCOPED STORE. This store outlives every screen that reads it, and the choice slice
   * is used by exactly one kind of screen: the transfer panels, which open a picker and then navigate
   * away.
   */
  cancelChoices(): void {
    this.choicesRequest?.unsubscribe();
    this.choicesRequest = null;
    this._choicesLoading.set(false);
  }

  /**
   * Loads a portal's pages and then the modules placed on the selected one. THIS IS THE SEQUENCING THE
   * TRANSPORT LAYER DELIBERATELY DOES NOT DO. Both services are confined to API communication - "one
   * method, one endpoint, one request" - and neither chains a second call onto the first.
   *
   * @param portalId The portal whose pages to read.
   * @param tabId The page to restrict the listing to, or `undefined` to list every placement in the
   * tenant.
   */
  loadPortalScope(portalId: number, tabId?: number): void {
    this.tabsRequest?.unsubscribe();
    this._tabsLoading.set(true);
    this.clearFailure();

    this.tabsRequest = this.tabService.getByPortal(portalId).subscribe({
      next: (tabs: readonly TabListItem[]) => {
        this._tabPortalId.set(portalId);
        // Copied rather than stored by reference, so that the slice cannot be mutated through the array
        // the caller still holds. The hierarchy is derived from this snapshot.
        this._tabs.set([...tabs]);
        this._tabsLoading.set(false);

        if (tabId === undefined) {
          this.setTabFilter(undefined);
        } else {
          this.selectTab(tabId);
          this.setTabFilter(tabId);
        }

        this.loadModules();
      },
      error: (cause: unknown) => {
        this._tabsLoading.set(false);
        this.recordFailure('loadTabs', cause);
      },
    });
  }

  /**
   * Reads a portal's pages without touching the listing. Offered separately from {@link
   * ModuleStore.loadPortalScope} because a form needs the page tree in order to render a parent picker
   * even when no listing is on screen.
   *
   * @param portalId The portal whose pages to read.
   */
  loadTabs(portalId: number): void {
    this.tabsRequest?.unsubscribe();
    this._tabsLoading.set(true);
    this.clearFailure();

    this.tabsRequest = this.tabService.getByPortal(portalId).subscribe({
      next: (tabs: readonly TabListItem[]) => {
        this._tabPortalId.set(portalId);
        this._tabs.set([...tabs]);
        this._tabsLoading.set(false);
      },
      error: (cause: unknown) => {
        this._tabsLoading.set(false);
        this.recordFailure('loadTabs', cause);
      },
    });
  }

  /**
   * @param moduleId The module to read.
   * @param tabModuleId The placement to address, or omitted to use the current selection, which may
   * itself be absent and then addresses the module.
   */
  loadModule(moduleId: number, tabModuleId?: number): void {
    this.moduleRequest?.unsubscribe();
    this._moduleLoading.set(true);
    this.clearFailure();
    this._selectedModuleId.set(moduleId);

    const ticket = this.moduleReads.begin();

    this.moduleRequest = this.moduleService.getModule(moduleId, this.resolvePlacement(tabModuleId)).subscribe({
      next: (detail: ModuleDetail) => {
        // The ticket refuses an answer that is no longer wanted: a newer read started, the slice was
        // cleared, or the session was purged. It is the only one of the two that can tell a re-read of the
        // SAME module from the read it replaced, because both carry the same identifier.
        if (!this.moduleReads.isCurrent(ticket)) {
          return;
        }

        if (detail.moduleId !== moduleId) {
          this._moduleLoading.set(false);
          this.recordFailure('loadModule', new Error(MISMATCHED_MODULE_MESSAGE));

          return;
        }

        this._module.set(detail);
        this._moduleLoading.set(false);
      },
      error: (cause: unknown) => {
        if (!this.moduleReads.isCurrent(ticket)) {
          return;
        }

        this._moduleLoading.set(false);
        this.recordFailure('loadModule', cause);
      },
    });
  }

  /**
   * Reads the whole definition catalogue. UNPAGED, UNFILTERED AND READ-ONLY. The endpoint accepts no
   * query parameter at all, so this store holds no page index, page size or total for it, and there is no
   * write half: no method here adds, changes or removes a definition, because no such route exists.
   */
  loadDefinitions(): void {
    // ⚠ ONLY THIS METHOD'S OWN HANDLE, for the reason recorded on {@link ModuleStore.loadDefinition}: the
    // catalogue, the single definition and a bundle's definitions are three separate slices with three
    // separate handles, and a read of one must never abort a read of another.
    this.releaseDefinitionRead(this.definitionsRequest);
    this.definitionsRequest = null;
    this.beginDefinitionRead();
    this.clearFailure();

    this.definitionsRequest = this.moduleService.listModuleDefinitions().subscribe({
      next: (definitions: readonly ModuleDefinition[]) => {
        this._definitions.set([...definitions]);
        this.definitionsRequest = null;
        this.endDefinitionRead();
      },
      error: (cause: unknown) => {
        this.definitionsRequest = null;
        this.endDefinitionRead();
        this.recordFailure('loadDefinitions', cause);
      },
    });
  }

  /** @param moduleDefinitionId The definition to read. */
  loadDefinition(moduleDefinitionId: number): void {
    this.releaseDefinitionRead(this.definitionRequest);
    this.definitionRequest = null;
    this.beginDefinitionRead();
    this.clearFailure();

    this.definitionRequest = this.moduleService.getModuleDefinition(moduleDefinitionId).subscribe({
      next: (definition: ModuleDefinition) => {
        this._definition.set(definition);
        this.definitionRequest = null;
        this.endDefinitionRead();
      },
      error: (cause: unknown) => {
        this.definitionRequest = null;
        this.endDefinitionRead();
        this.recordFailure('loadDefinition', cause);
      },
    });
  }

  /**
   * Reads the definitions belonging to one deployed bundle.
   *
   * @param desktopModuleId The deployed bundle whose definitions to read.
   */
  loadDesktopDefinitions(desktopModuleId: number): void {
    this.releaseDefinitionRead(this.desktopDefinitionsRequest);
    this.desktopDefinitionsRequest = null;
    this.beginDefinitionRead();
    this.clearFailure();

    this.desktopDefinitionsRequest = this.moduleService.listDesktopModuleDefinitions(desktopModuleId).subscribe({
      next: (definitions: readonly ModuleDefinition[]) => {
        this._desktopDefinitions.set([...definitions]);
        this.desktopDefinitionsRequest = null;
        this.endDefinitionRead();
      },
      error: (cause: unknown) => {
        this.desktopDefinitionsRequest = null;
        this.endDefinitionRead();
        this.recordFailure('loadDesktopDefinitions', cause);
      },
    });
  }

  // ---------------------------------------------------------------------------------------------------
  // WRITES
  // ---------------------------------------------------------------------------------------------------

  /**
   * Creates a module placement. The request is transmitted WHOLE and is not inspected, filtered or
   * defaulted here: it was assembled and validated by the feature form, and the server applies its own
   * rules.
   *
   * @param request The placement to create.
   */
  createModule(request: CreateModuleRequest): void {
    this._saving.set(true);
    this.clearFailure();

    this.track(
      this.moduleService.createModule(request).subscribe({
        next: (detail: ModuleDetail) => {
          this._module.set(detail);
          this._selectedModuleId.set(detail.moduleId);
          this._saving.set(false);

          // ⚠ THE LISTING IS DELIBERATELY NOT RE-READ HERE, AND REMOVING THAT READ IS A FIX RATHER THAN AN
          // OMISSION. This command's only caller navigates TO the listing on success, and the listing reads
          // itself from its own address on entry, so two identical reads were issued for one create.
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('createModule', cause);
        },
      }),
    );
  }

  /**
   * Replaces one module placement. A WHOLE-ROW REPLACEMENT, EXACTLY AS THE LEGACY POSTBACK WAS. An
   * omitted nullable member is not "leave it alone" - the server writes the absent value and clears the
   * column - which is what the legacy screen did when a text box was posted empty, and without it an
   * operator could set a header but never remove one.
   *
   * @param moduleId The module to replace.
   * @param request The complete replacement state, transmitted whole.
   * @param tabModuleId The listed row the echo corresponds to, or omitted to use the current selection.
   */
  updateModule(moduleId: number, request: UpdateModuleRequest, tabModuleId?: number): void {
    this._saving.set(true);
    this.clearFailure();

    this.track(
      this.moduleService.updateModule(moduleId, request).subscribe({
        next: (detail: ModuleDetail) => {
          this._module.set(detail);
          this._saving.set(false);

          this.replaceListedPlacement(detail, tabModuleId);
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('updateModule', cause);
        },
      }),
    );
  }

  /**
   * Removes one module placement, then RE-READS the listing. THE REMOVAL IS SOFT AND THE ROW SURVIVES, SO
   * NOTHING IS PRUNED LOCALLY. The endpoint answers `204 No Content`, yet the module row remains in the
   * database with its deleted marker set - which is precisely what the legacy recycle-bin screen under
   * `Website/admin/Tabs/` read.
   *
   * @param moduleId The module to remove.
   * @param tabModuleId The single placement to remove, or omitted to use the current selection, which
   * when absent removes the module itself.
   */
  deleteModule(moduleId: number, tabModuleId?: number): void {
    this._saving.set(true);
    this.clearFailure();

    this.track(
      this.moduleService.deleteModule(moduleId, this.resolvePlacement(tabModuleId)).subscribe({
        next: () => {
          this._saving.set(false);

          // Scoped by identifier rather than cleared outright, deliberately. A bag belonging to some OTHER
          // module is still a truthful answer about that module and there is no reason to make the next
          // screen read it again; only the record that has just stopped existing is discarded.
          const cached: ModuleSettingsBag | null = this._settings();

          if (cached !== null && cached.moduleId === moduleId) {
            this._settings.set(null);
          }

          // The mandatory re-read. See the note above: the row is not gone, and only the listing knows
          // whether it should still be shown.
          this.loadModules();
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('deleteModule', cause);
        },
      }),
    );
  }

  // ---------------------------------------------------------------------------------------------------
  // SETTINGS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Reads one module's settings.
   *
   * @param moduleId The module whose settings to read.
   * @param tabModuleId The placement whose own settings to include, or omitted to use the current
   * selection.
   */
  loadSettings(moduleId: number, tabModuleId?: number): void {
    this.settingsRequest?.unsubscribe();
    this._settingsLoading.set(true);
    this.clearFailure();

    // The dedicated slot describes THIS read from here on; whatever the previous one was told is gone.
    this._settingsFailure.set(null);

    const ticket = this.settingsReads.begin();

    this.settingsRequest = this.moduleService.getModuleSettings(moduleId, this.resolvePlacement(tabModuleId)).subscribe({
      next: (bag: ModuleSettingsBag) => {
        if (!this.settingsReads.isCurrent(ticket)) {
          return;
        }

        this._settings.set(bag);
        this._settingsLoading.set(false);
        this._settingsFailure.set(null);
      },
      error: (cause: unknown) => {
        if (!this.settingsReads.isCurrent(ticket)) {
          return;
        }

        this._settingsLoading.set(false);
        this.recordFailure('loadSettings', cause);

        // Read back rather than rebuilt: the dedicated slot then carries byte-identical content to the
        // shared one, so a screen reading either sees the same refusal, and only the LIFETIME differs.
        this._settingsFailure.set(this._failure());
      },
    });
  }

  /**
   * Replaces one module's settings, then RE-READS them. THE REPLACEMENT ANSWERS `204` WITH NO BODY, WHICH
   * IS WHY IT IS FOLLOWED BY A READ. There is no echoed state to hold, so a caller needing the stored
   * settings reads them back deliberately; that second request is composition and therefore belongs here
   * rather than in the transport.
   *
   * @param settings The complete settings state, transmitted whole and unfiltered.
   * @param tabModuleId The placement whose own settings are being replaced, or omitted to use the current
   * selection.
   */
  saveSettings(settings: ModuleSettingsBag, tabModuleId?: number): void {
    this._settingsSaving.set(true);
    this.clearFailure();

    const moduleId: number = settings.moduleId;
    const placement: ModulePlacement | undefined = this.resolvePlacement(tabModuleId);

    this.track(
      this.moduleService.updateModuleSettings(moduleId, settings, placement).subscribe({
        next: () => {
          this._settingsSaving.set(false);
          // The write returned nothing, so the stored state is read back rather than assumed to equal what
          // was sent: the server may normalise a value on the way in.
          this.loadSettings(moduleId, tabModuleId);
        },
        error: (cause: unknown) => {
          this._settingsSaving.set(false);
          this.recordFailure('saveSettings', cause);
        },
      }),
    );
  }

  // ---------------------------------------------------------------------------------------------------
  // CONTENT TRANSFER
  // ---------------------------------------------------------------------------------------------------

  /**
   * Exports one module's content and holds the returned document.
   *
   * @param moduleId The module to export.
   * @param request The name the caller intends for the payload, and an optional folder.
   */
  exportModule(moduleId: number, request: ModuleExportRequest): void {
    this._exporting.set(true);
    this._exportedContent.set(null);
    this.clearFailure();

    const ticket = this.exportOperations.begin();

    this.exportRequest = this.moduleService.exportModule(moduleId, request).subscribe({
      next: (content: string) => {
        if (!this.exportOperations.isCurrent(ticket)) {
          return;
        }

        this._exportedContent.set(content);
        this._exporting.set(false);
      },
      error: (cause: unknown) => {
        if (!this.exportOperations.isCurrent(ticket)) {
          return;
        }

        this._exporting.set(false);
        this.recordFailure('exportModule', cause);
      },
    });

    this.track(this.exportRequest);
  }

  /**
   * Imports content into a module.
   *
   * @param request The target module, the document as text, and the optional descriptive folder and name
   * members.
   */
  importModule(request: ModuleImportRequest): void {
    this._importing.set(true);
    this._importCompleted.set(false);
    this.clearFailure();

    this.track(
      this.moduleService.importModule(request).subscribe({
        next: () => {
          this._importing.set(false);
          this._importCompleted.set(true);
        },
        error: (cause: unknown) => {
          this._importing.set(false);
          this.recordFailure('importModule', cause);
        },
      }),
    );
  }

  // ---------------------------------------------------------------------------------------------------
  // RESETS
  // ---------------------------------------------------------------------------------------------------

  /** Discards the recorded failure. */
  clearFailure(): void {
    this._failure.set(null);
  }

  /** Discards the exported document and the import outcome. */
  clearTransferOutcome(): void {
    this.exportOperations.invalidate();
    this.cancelExport();

    this._exportedContent.set(null);
    this._exporting.set(false);
    this._importCompleted.set(false);
  }

  /** Discards the loaded module and the selection that addressed it. */
  clearModule(): void {
    this.moduleReads.invalidate();
    this.moduleRequest?.unsubscribe();
    this.moduleRequest = null;

    this._module.set(null);
    this._selectedModuleId.set(undefined);
    this._moduleLoading.set(false);
  }

  /** Discards the loaded settings. */
  clearSettings(): void {
    this.settingsReads.invalidate();
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = null;

    this._settings.set(null);
    this._settingsLoading.set(false);
    this._settingsFailure.set(null);
  }

  /** Discards the single definition read by {@link ModuleStore.loadDefinition}, leaving the catalogue. */
  clearDefinition(): void {
    this._definition.set(null);
  }

  /**
   * Returns every slice to the state it held before anything was read. ⚠ THIS IS A SESSION-TEARDOWN
   * OPERATION, NOT A SCREEN-LEVEL CLEAR. The five `clear*` members above are for a screen tidying up
   * after itself — closing a transfer panel, dismissing a banner, leaving a detail view.
   */
  reset(): void {
    this.cancelInFlight();
    this.abandonOperations();

    // The listing, its query coordinate and its filters.
    this._page.set(emptyPagedResult<ModuleListItem>());
    this._query.set({ pageIndex: 0, pageSize: DEFAULT_PAGE_SIZE });
    this._filter.set({});
    this._listLoading.set(false);

    this._choices.set([]);
    // Zeroed with the rows it describes. Leaving the previous session's total behind would make an
    // emptied picker report that modules exist which it is not showing.
    this._choicesTotalCount.set(0);
    this._choicesLoading.set(false);

    this._module.set(null);
    this._selectedModuleId.set(undefined);
    this._moduleLoading.set(false);
    this._saving.set(false);

    // The settings bag: operator-authored configuration, tenant-scoped. Its dedicated failure slot goes
    // with it, because a refusal describes a read of THIS tenant's settings and nothing else.
    this._settings.set(null);
    this._settingsLoading.set(false);
    this._settingsFailure.set(null);
    this._settingsSaving.set(false);

    // The definition catalogue and the single definition read from it.
    this._definitions.set([]);
    this._desktopDefinitions.set([]);
    this._definition.set(null);
    this._definitionReadsInFlight.set(0);

    // The page hierarchy, and the tenant it was read for.
    this._tabs.set([]);
    this._tabPortalId.set(undefined);
    this._selectedTabId.set(undefined);
    this._tabsLoading.set(false);

    // ⚠ The exported document. A serialised copy of a module's data, produced under the authority of the
    // account that is signing out. Nothing here may survive them.
    this._exportedContent.set(null);
    this._exporting.set(false);
    this._importing.set(false);
    this._importCompleted.set(false);

    this._failure.set(null);
  }

  /**
   * Releases every request handle when the injector holding this store is destroyed. A root-provided
   * store lives as long as the application, so in production this runs at teardown.
   */
  ngOnDestroy(): void {
    this.cancelInFlight();
  }

  // ---------------------------------------------------------------------------------------------------
  // INTERNALS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Holds a write's handle until it settles, so that teardown can release it. A write is never cancelled
   * to make way for a later one — see the note on the handles at the head of this class — so the handle
   * is discarded when the write FINISHES rather than when the next one starts, and the set cannot
   * therefore grow without bound.
   *
   * @param request The handle to hold.
   */
  private track(request: Subscription): void {
    if (request.closed) {
      return;
    }

    this.writeRequests.add(request);
    request.add(() => {
      this.writeRequests.delete(request);
    });
  }

  /**
   * Releases the export in flight, if any, and forgets its handle. Narrower than {@link
   * ModuleStore.cancelWrites} on purpose: closing the transfer panel abandons the export the panel
   * started and must not disturb an unrelated save.
   */
  private cancelExport(): void {
    this.exportRequest?.unsubscribe();
    this.exportRequest = null;
  }

  /** Cancels every read in flight and forgets its handle. */
  private cancelReads(): void {
    this.listRequest?.unsubscribe();
    this.listRequest = null;
    this.moduleRequest?.unsubscribe();
    this.moduleRequest = null;
    this.settingsRequest?.unsubscribe();
    this.settingsRequest = null;
    this.cancelDefinitionReads();
    this.tabsRequest?.unsubscribe();
    this.tabsRequest = null;
    this.choicesRequest?.unsubscribe();
    this.choicesRequest = null;
  }

  /**
   * Emits ONE envelope carrying every module the picker endpoint will return. The walk and its three
   * terminating conditions are described on {@link ModuleStore.loadChoices}; this method is the mechanism
   * alone.
   *
   * @returns The complete choice set as one envelope whose `meta.totalCount` is the SERVER's total.
   */
  private readEveryChoice(): Observable<ModuleListPage> {
    const requestPage = (pageIndex: number): Observable<ModuleListPage> =>
      this.moduleService.listModules({ pageIndex, pageSize: MAX_PAGE_SIZE }, {});

    let gathered = 0;

    return requestPage(0).pipe(
      // `expand` re-enters with each emission, so this is the walk: every page it emits is both a result
      // to accumulate and the input that decides whether another is needed.
      expand((page: ModuleListPage, index: number) => {
        gathered += page.items.length;

        const reportedTotal: number = page.meta.totalCount;

        if (gathered >= reportedTotal) {
          return EMPTY;
        }

        if (page.items.length === 0) {
          return throwError(
            () =>
              new Error(
                `The module choice set could not be read completely: the server reports ` +
                  `${String(reportedTotal)} placements but supplied ${String(gathered)} and then ` +
                  `answered with an empty page.`,
              ),
          );
        }

        // The bound, and reaching it is a failure rather than an answer for the same reason.
        if (index + 1 >= MAXIMUM_CHOICE_PAGES) {
          return throwError(
            () =>
              new Error(
                `The module choice set could not be read completely: the server reports ` +
                  `${String(reportedTotal)} placements and stopped supplying them after ` +
                  `${String(MAXIMUM_CHOICE_PAGES)} pages (${String(gathered)} gathered).`,
              ),
          );
        }

        return requestPage(index + 1);
      }),
      reduce<ModuleListPage, ModuleListPage>(
        (accumulated, page) => ({
          items: [...accumulated.items, ...page.items],
          meta: {
            // The server's total, deliberately: see the note on the slice.
            totalCount: page.meta.totalCount,
            pageIndex: 0,
            pageSize: accumulated.items.length + page.items.length,
            totalPages: accumulated.items.length + page.items.length > 0 ? 1 : 0,
          },
        }),
        emptyPagedResult<ModuleListItem>(),
      ),
    );
  }

  /** Opens one definition read. */
  private beginDefinitionRead(): void {
    this._definitionReadsInFlight.update((open) => open + 1);
  }

  /** Closes one definition read. Clamped at zero rather than allowed to go negative. */
  private endDefinitionRead(): void {
    this._definitionReadsInFlight.update((open) => (open > 0 ? open - 1 : 0));
  }

  /**
   * Releases ONE definition read's handle and closes its count, if that handle is outstanding. ⚠ THE
   * COUNT MUST BE CLOSED HERE, BECAUSE UNSUBSCRIBING RUNS NEITHER CALLBACK. A cancelled subscription
   * delivers no value and no error, so the closer on the `next`/`error` paths never runs for a read that
   * was abandoned - and without this the count would leak upward until the flag was permanently true and
   * every screen bound to it reported itself permanently busy.
   *
   * @param handle The read's own handle, or `null` when it holds none.
   */
  private releaseDefinitionRead(handle: Subscription | null): void {
    if (handle === null) {
      return;
    }

    handle.unsubscribe();
    this.endDefinitionRead();
  }

  /** Releases all THREE definition reads, whichever of them is outstanding. */
  private cancelDefinitionReads(): void {
    this.releaseDefinitionRead(this.definitionsRequest);
    this.definitionsRequest = null;
    this.releaseDefinitionRead(this.definitionRequest);
    this.definitionRequest = null;
    this.releaseDefinitionRead(this.desktopDefinitionsRequest);
    this.desktopDefinitionsRequest = null;
  }

  /** Releases every write handle. */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();

    // The export is tracked as a write, so it has just been released; its dedicated handle is forgotten
    // here so the two cannot disagree about whether one is outstanding.
    this.exportRequest = null;
  }

  /** Cancels everything this store has outstanding, reads and writes alike. */
  private cancelInFlight(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  /** Abandons every operation ticket, so nothing already issued can still be current. */
  private abandonOperations(): void {
    this.moduleReads.invalidate();
    this.settingsReads.invalidate();
    this.exportOperations.invalidate();
  }

  /**
   * Resolves which placement an operation addresses.
   *
   * @param tabModuleId The placement named by the caller, or `undefined` to address the module itself.
   * @returns The selector to forward, or `undefined` to address the module itself.
   */
  private resolvePlacement(tabModuleId?: number): ModulePlacement | undefined {
    return tabModuleId === undefined ? undefined : { tabModuleId };
  }

  /**
   * Replaces the listed row a successful update echoed back, immutably.
   *
   * @param detail The placement the server echoed back.
   * @param tabModuleId The placement the caller named, or `undefined` when the module itself was
   * addressed.
   */
  private replaceListedPlacement(detail: ModuleDetail, tabModuleId?: number): void {
    const addressedPlacement: boolean = tabModuleId !== undefined;

    if (!addressedPlacement || detail.allTabs) {
      this.loadModules();

      return;
    }

    const listed: readonly ModuleListItem[] = this._page().items;
    const isListed: boolean = listed.some((row) => row.tabModuleId === detail.tabModuleId);

    if (!isListed) {
      // The updated placement is not on the page being shown, so there is nothing local to correct and
      // nothing stale to leave behind. Tested by presence rather than by a numeric search result, so no
      // comparison against a magic index is required.
      return;
    }

    this._page.update((page) => ({
      ...page,
      items: page.items.map((row) =>
        row.tabModuleId === detail.tabModuleId ? projectListedRow(row, detail) : row,
      ),
    }));
  }

  /**
   * Records a failed command. SEVERITY IS DELEGATED, NOT DECIDED HERE, AND A REFUSAL IS A WARNING.
   * `summarizeProblem` in `core/utils/form-errors.util.ts` resolves the severity, and it maps a refusal
   * to WARNING rather than to error.
   *
   * @param operation The command that failed.
   * @param cause The value the error callback received.
   */
  private recordFailure(operation: ModuleStoreOperation, cause: unknown): void {
    const problem: ProblemDetails | null = problemFromCause(cause);

    this._failure.set({
      operation,
      problem,
      summary: summarizeProblem(problem),
      code: failureCode(problem),
    });
  }
}

/**
 * Projects an echoed placement onto the listed row it replaces.
 *
 * @param row The listed row being replaced.
 * @param detail The placement the server echoed back.
 * @returns A new row.
 */
function projectListedRow(row: ModuleListItem, detail: ModuleDetail): ModuleListItem {
  return {
    ...row,
    moduleId: detail.moduleId,
    tabModuleId: detail.tabModuleId,
    tabId: detail.tabId,
    moduleTitle: detail.moduleTitle,
    moduleOrder: detail.moduleOrder,
    allTabs: detail.allTabs,
    visibility: detail.visibility,
    isDeleted: detail.isDeleted,
    displayTitle: detail.displayTitle,
    startDate: detail.startDate,
    endDate: detail.endDate,
  };
}
