import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ModuleVisibility } from '../models/module.model';
import { MAX_PAGE_SIZE } from '../models/paged-result.model';
import { MAXIMUM_CHOICE_PAGES, ModuleStore } from './module.store';

import type { TabHierarchy, TabTreeNode } from './module.store';
import type {
  CreateModuleRequest,
  ModuleDefinition,
  ModuleDetail,
  ModuleExportRequest,
  ModuleImportRequest,
  ModuleListItem,
  ModuleSettingsBag,
  UpdateModuleRequest,
} from '../models/module.model';

import type { ApiResponse, PagedResult } from '../models/paged-result.model';
import type { ProblemDetails, ValidationProblemDetails } from '../models/problem-details.model';
import type { TabListItem } from '../models/tab.model';

import { HttpParams } from '@angular/common/http';

import type { HttpRequest } from '@angular/common/http';

/**
 * The filters a request carried, presented as ONE parameter bag whichever transport carried them. ⚠ A
 * LISTING READ THAT CARRIES A TERM A PERSON TYPED SENDS ITS FILTERS IN THE BODY, because a query string is
 * written into the reverse proxy's access log and into the API's own request log; a term-free read keeps
 * them in the query string. Specifications below are about WHAT was sent, not about WHERE, so they read
 * through here and stay true across both transports.
 *
 * @param request The request to read, or the raw request it wraps.
 * @returns Every filter it carried, as query-parameter-shaped strings.
 */
function sentFilters(request: TestRequest | HttpRequest<unknown>): HttpParams {
  const raw: HttpRequest<unknown> = 'request' in request ? request.request : request;
  const body = raw.body as Record<string, unknown> | null | undefined;

  if (body === null || body === undefined) {
    return raw.params;
  }

  let carried: HttpParams = new HttpParams();

  for (const [name, value] of Object.entries(body)) {
    if (value !== null && value !== undefined) {
      carried = carried.set(name, String(value));
    }
  }

  return carried;
}


/** The term-free listing address. */
const MODULES_LIST_URL = '/api/v1/modules';

/**
 * The body-bound search address. ⚠ A SEPARATE ADDRESS ON PURPOSE: a listing read that carries a term a
 * person typed goes here, so the term never appears in a logged request line.
 */
const MODULES_LIST_SEARCH_URL = '/api/v1/modules/search';

/**
 * Whether a request is a listing read, on EITHER transport.
 *
 * @param candidate The request to test.
 * @returns True for the term-free read and for the body-bound search alike.
 */
function isListingRead(candidate: HttpRequest<unknown>): boolean {
  return candidate.url === MODULES_LIST_URL || candidate.url === MODULES_LIST_SEARCH_URL;
}


// LITERALS COMPOSED AT RUNTIME
// Two families of token must be PROVED ABSENT from the traffic this slice generates, and writing either as
// a source literal would make the absence unprovable by inspection: a reviewer grepping this tree for the
// token would find the assertion that forbids it and could not tell the two apart.

/** The path fragments a reversal endpoint would carry, if one existed. */
const REVERSAL_FRAGMENTS: readonly string[] = [
  ['re', 'store'].join(''),
  ['recycle', 'bin'].join('-'),
  ['un', 'delete'].join(''),
];

/** The trailing-wildcard character, obtained by code point. */
const WILDCARD: string = String.fromCharCode(37);

/** A date at the bottom of the calendar - the legacy absent-date marker, and real data on the wire. */
const MIN_DATE = '0001-01-01T00:00:00';

/** The prefix the server puts in front of every application failure code inside a problem `type`. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

// FIXTURES
// Each factory is typed as the REAL contract, so a member this file misspells is a compile error rather
// than an `undefined` nobody notices.

/**
 * One row of the module listing, keyed AT THE IDENTITY SEEDS. `moduleId` and `tabId` default to ZERO
 * because a row at the seed is precisely the row a truthiness test loses, and the listing is where such a
 * loss would be least visible.
 */
function listRow(overrides: Partial<ModuleListItem> = {}): ModuleListItem {
  return {
    moduleId: 0,
    tabModuleId: 1,
    tabId: 0,
    moduleDefId: 4,
    moduleTitle: 'Announcements',
    friendlyName: 'Announcements',
    desktopModuleId: 2,
    moduleName: 'Announcements',
    description: '',
    version: '01.00.00',
    isAdmin: false,
    moduleOrder: 1,
    allTabs: false,
    visibility: ModuleVisibility.Maximized,
    isDeleted: false,
    displayTitle: true,
    startDate: null,
    endDate: null,
    ...overrides,
  };
}

/**
 * One module placement in full. `portalId` is MINUS ONE - the first portal the schema ever creates and
 * simultaneously the legacy absent-integer marker - and `cacheTime` is ZERO, which is a CHOSEN lifetime
 * rather than an unset one.
 */
function detail(overrides: Partial<ModuleDetail> = {}): ModuleDetail {
  return {
    moduleId: 0,
    tabModuleId: 1,
    tabId: 0,
    portalId: -1,
    moduleDefId: 4,
    desktopModuleId: 2,
    moduleTitle: 'Announcements',
    allTabs: false,
    header: '',
    footer: '',
    startDate: null,
    endDate: null,
    inheritViewPermissions: false,
    isDeleted: false,
    moduleOrder: 1,
    cacheTime: 0,
    iconFile: '',
    alignment: null,
    color: null,
    border: null,
    visibility: ModuleVisibility.Maximized,
    displayTitle: false,
    friendlyName: 'Announcements',
    moduleName: 'Announcements',
    description: '',
    version: '01.00.00',
    isAdmin: false,
    ...overrides,
  };
}

/** One catalogue definition. */
function definition(overrides: Partial<ModuleDefinition> = {}): ModuleDefinition {
  return {
    moduleDefId: 4,
    friendlyName: 'Announcements',
    desktopModuleId: 2,
    defaultCacheTime: -1,
    moduleName: 'Announcements',
    description: '',
    version: '01.00.00',
    isPremium: false,
    isAdmin: false,
    isPortable: true,
    ...overrides,
  };
}

/**
 * One page of the portal, at the identity seed and at the ROOT of the hierarchy. The default `parentId`
 * is `null`, which is the form the wire uses: the backend converts the legacy marker at the boundary
 * because minus one is simultaneously a legitimate tenant identifier.
 */
function tabRow(overrides: Partial<TabListItem> = {}): TabListItem {
  return {
    tabId: 0,
    tabName: 'Home',
    title: null,
    tabOrder: 1,
    parentId: null,
    level: 0,
    tabPath: '//Home',
    isVisible: true,
    disableLink: false,
    isDeleted: false,
    hasChildren: false,
    isSecure: false,
    url: null,
    iconFile: null,
    ...overrides,
  };
}

/** A creation request in which every optional-looking member is falsy. */
function createRequest(overrides: Partial<CreateModuleRequest> = {}): CreateModuleRequest {
  return {
    moduleDefId: 4,
    tabId: 0,
    moduleTitle: '',
    allTabs: false,
    header: '',
    footer: '',
    startDate: null,
    endDate: null,
    inheritViewPermissions: false,
    moduleOrder: 0,
    cacheTime: 0,
    iconFile: null,
    visibility: ModuleVisibility.Maximized,
    displayTitle: false,
    ...overrides,
  };
}

/** A replacement request carrying the page it edits, exactly as the contract requires. */
function updateRequest(overrides: Partial<UpdateModuleRequest> = {}): UpdateModuleRequest {
  return {
    tabId: 0,
    moveToTabId: null,
    moduleTitle: '',
    allTabs: false,
    header: '',
    footer: '',
    startDate: null,
    endDate: null,
    inheritViewPermissions: false,
    isDeleted: false,
    moduleOrder: 0,
    cacheTime: 0,
    iconFile: null,
    alignment: null,
    color: null,
    border: null,
    visibility: ModuleVisibility.None,
    displayTitle: false,
    setAsDefaultSettings: false,
    applyToAllModules: false,
    ...overrides,
  };
}

/** Both settings maps, with cleared values in each. The most marker-sensitive payload in the feature. */
function settingsBag(overrides: Partial<ModuleSettingsBag> = {}): ModuleSettingsBag {
  return {
    moduleId: 0,
    tabModuleId: 1,
    moduleSettings: {
      Announcements_Description: '',
      Announcements_Length: '0',
      Announcements_Template: 'default',
    },
    tabModuleSettings: {
      Announcements_Heading: '',
      Announcements_Collapsed: 'false',
    },
    ...overrides,
  };
}

/** The two members of the export request contract. */
function exportRequest(overrides: Partial<ModuleExportRequest> = {}): ModuleExportRequest {
  return { fileName: 'Announcements', folder: '', ...overrides };
}

function importRequest(overrides: Partial<ModuleImportRequest> = {}): ModuleImportRequest {
  return {
    moduleId: -1,
    content: '<content type="Announcements" version="01.00.00" />',
    folder: '',
    fileName: 'Announcements.Backup.xml',
    ...overrides,
  };
}

/** Wraps rows in the paged envelope the listing endpoint answers with. */
function pagedBody(
  items: readonly ModuleListItem[],
  pageIndex = 0,
  pageSize = 10,
): PagedResult<ModuleListItem> {
  return {
    items,
    meta: {
      totalCount: items.length,
      pageIndex,
      pageSize,
      totalPages: items.length === 0 ? 0 : 1,
    },
  };
}

/**
 * A page whose reported total is set INDEPENDENTLY of the rows it carries. {@link pagedBody} derives the
 * total from the row count, which is right for a single-page answer and useless for the walk: every
 * interesting property of a walk is about the relationship between the rows in hand and the total the
 * server claims, and a helper that keeps the two equal by construction cannot express a partial page at
 * all.
 *
 * @param items The rows this page carries.
 * @param totalCount What the server claims exists across every page.
 * @param pageIndex The zero-based index this page answers for.
 */
function walkPage(
  items: readonly ModuleListItem[],
  totalCount: number,
  pageIndex = 0,
): PagedResult<ModuleListItem> {
  return {
    items,
    meta: {
      totalCount,
      pageIndex,
      pageSize: MAX_PAGE_SIZE,
      totalPages: Math.ceil(totalCount / MAX_PAGE_SIZE),
    },
  };
}

/**
 * A full page of distinct rows, so a walk has a reason to ask for another.
 *
 * @param count How many rows to build.
 * @param startingAt The first placement identifier to use.
 */
function distinctRows(count: number, startingAt = 1): readonly ModuleListItem[] {
  return Array.from({ length: count }, (_unused, offset) =>
    listRow({
      moduleId: startingAt + offset,
      tabModuleId: startingAt + offset,
      moduleTitle: `Module ${startingAt + offset}`,
    }),
  );
}

/** Wraps a payload in the single-item envelope every non-paged read answers with. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/** The page size the whole-hierarchy reader asks for. Mirrors `WHOLE_COLLECTION_PAGE_SIZE` in `TabService`. */
const TAB_PAGE_SIZE = 100;

/**
 * The PAGED wire envelope the portal-scoped page listing answers with. MIGRATION: the hierarchy used to
 * arrive in one unbounded response - a tenant with three thousand pages sent 764 KiB - and is now read a
 * bounded page at a time, so its body carries populated metadata where the single-payload envelope carried
 * none. The store's own surface is unchanged: it still holds no tab paging coordinate of any kind.
 *
 * @param rows The rows of this page.
 * @param totalCount The total across every page. Defaults to a single complete page.
 * @returns The body to flush.
 */
function tabPage<T>(
  rows: readonly T[],
  totalCount: number = rows.length,
): {
  readonly items: readonly T[];
  readonly meta: {
    readonly totalCount: number;
    readonly pageIndex: number;
    readonly pageSize: number;
    readonly totalPages: number;
  };
} {
  return {
    items: rows,
    meta: {
      totalCount,
      pageIndex: 0,
      pageSize: TAB_PAGE_SIZE,
      totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / TAB_PAGE_SIZE),
    },
  };
}

/** A problem document carrying the server's own failure code. */
function problem(code: string, status: number, detailText: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: 'Request rejected',
    status,
    detail: detailText,
    instance: '/api/v1/modules',
    traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
  };
}

/**
 * A field-level refusal, whose per-field dictionary uses .NET model-state keys. Those keys are NOT
 * camel-cased, and because the dictionary is an index signature under
 * `noPropertyAccessFromIndexSignature`, every read of it below is an INDEX EXPRESSION. A property access
 * is a compile error here, by design.
 */
function validationProblem(status: number): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.failed`,
    title: 'One or more validation errors occurred.',
    status,
    detail: 'The request was rejected.',
    traceId: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
    errors: {
      ModuleTitle: ['The module title exceeds the permitted length.'],
      Content: ['The document is required.'],
    },
  };
}

/**
 * Narrows a request body to the import contract. The framework types a request body loosely, so it is
 * narrowed through a predicate rather than widened with a suppression or an escape-hatch annotation.
 */
function isImportBody(value: unknown): value is ModuleImportRequest {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Readonly<Record<string, unknown>>;
  const target: unknown = candidate['moduleId'];
  const content: unknown = candidate['content'];

  return (target === null || typeof target === 'number') && (content === null || typeof content === 'string');
}

/**
 * Every page the derivation placed, flattened depth-first, paired with the depth it was placed at. Used
 * to prove CONSERVATION: every row handed to the derivation must appear exactly once across the placed
 * nodes and the reported unplaceable rows together, so nothing can be silently dropped.
 */
function flattenTree(roots: readonly TabTreeNode[]): readonly TabTreeNode[] {
  const flattened: TabTreeNode[] = [];
  const pending: TabTreeNode[] = [...roots].reverse();

  for (let node = pending.pop(); node !== undefined; node = pending.pop()) {
    flattened.push(node);

    for (const child of [...node.children].reverse()) {
      pending.push(child);
    }
  }

  return flattened;
}

/** The identifiers the derivation placed, in traversal order. */
function placedIds(hierarchy: TabHierarchy): readonly number[] {
  return flattenTree(hierarchy.roots).map((node) => node.tab.tabId);
}

describe('ModuleStore', () => {
  let store: ModuleStore;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client FIRST and the testing backend SECOND, and the order is load-bearing:
      // `provideHttpClientTesting()` REPLACES the backend that `provideHttpClient()` installed, so
      // reversing the two would leave the real backend in place and every expectation below would find
      // nothing while the specification attempted live requests.
      providers: [provideHttpClient(), provideHttpClientTesting(), ModuleStore],
    });

    store = TestBed.inject(ModuleStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The single most valuable line in the file. It fails on any request no expectation consumed, which is
    // what turns "the removal re-read the listing, and nothing else happened" into a test result.
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // Local helpers. Declared inside the suite because each closes over the backend for this
  // specification, and the backend is rebuilt for every one of them.
  // ---------------------------------------------------------------------------------------------------

  /** Consumes exactly one pending request at a literal path. */
  function expectRequest(method: string, url: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
    );
  }

  /** Reads the module listing and answers it with the rows given. */
  function loadListWith(rows: readonly ModuleListItem[], pageIndex = 0, pageSize = 10): void {
    store.loadModules();
    expectRequest('GET', '/api/v1/modules').flush(pagedBody(rows, pageIndex, pageSize));
  }

  /** Reads a portal's pages and answers with the rows given, so the hierarchy can be inspected. */
  function loadTabsWith(portalId: number, rows: readonly TabListItem[]): void {
    store.loadTabs(portalId);
    expectRequest('GET', `/api/v1/portals/${portalId}/tabs`).flush(tabPage(rows));
  }

  /** Asserts that no reversal endpoint was addressed. */
  function expectNoReversalRequest(): void {
    for (const fragment of REVERSAL_FRAGMENTS) {
      httpMock.expectNone((candidate) => candidate.url.includes(fragment));
    }
  }

  it('is resolvable, and issues no request of its own on construction', () => {
    // Construction must be inert. A store that read anything eagerly would fetch on every injector it
    // was created in, and the failure would only show up as traffic nobody asked for.
    expect(store).toBeInstanceOf(ModuleStore);
    expect(store.listLoading()).toBeFalse();
    expect(store.busy()).toBeFalse();
    expect(store.modules().length).toBe(0);
    expect(store.failure()).toBeNull();
  });

  // PROOF 1 - IDENTIFIERS OF ZERO ARE REAL, ON BOTH THE MODULE SIDE AND THE PAGE SIDE
  // `01.00.00.SqlDataProvider` line 221 declares `[ModuleID] [int] IDENTITY (0, 1)` and line 140 declares
  // `[TabID] [int] IDENTITY (0, 1)`, so the first module and the first page of an installation are both
  // numbered ZERO. Line 77 declares `[PortalID] [int] IDENTITY (-1, 1)`, so minus one is the first portal
  // and zero is the second.
  describe('identifiers of zero and minus one', () => {
    it('issues GET /api/v1/modules/0 for module ZERO rather than skipping the read', () => {
      store.loadModule(0);

      // The literal path, asserted whole. A guard of the form "read only when the identifier is truthy"
      // produces NO request here, and this line is what makes that omission loud.
      const call = expectRequest('GET', '/api/v1/modules/0');

      // No placement selector was named, so the read addresses the MODULE rather than one of its
      // placements, and the two are materially different requests.
      expect(sentFilters(call).has('tabModuleId')).toBeFalse();

      call.flush(envelope(detail({ moduleId: 0 })));

      expect(store.module()?.moduleId).toBe(0);
      expect(store.selectedModuleId()).toBe(0);
      expect(store.moduleLoading()).toBeFalse();
    });

    it('issues GET /api/v1/portals/0/tabs and GET /api/v1/portals/-1/tabs for both real portal seeds', () => {
      loadTabsWith(0, [tabRow({ tabId: 0 })]);
      expect(store.tabPortalId()).toBe(0);
      expect(store.tabs().length).toBe(1);

      loadTabsWith(-1, [tabRow({ tabId: 0 }), tabRow({ tabId: 1, tabName: 'About', tabOrder: 2 })]);
      expect(store.tabPortalId()).toBe(-1);
      expect(store.tabs().length).toBe(2);
    });

    it('keeps modules 0, 1 and 2 through every derivation the listing feeds', () => {
      loadListWith([
        listRow({ moduleId: 0, tabModuleId: 1 }),
        listRow({ moduleId: 1, tabModuleId: 2 }),
        listRow({ moduleId: 2, tabModuleId: 3 }),
      ]);

      expect(store.modules().map((row) => row.moduleId)).toEqual([0, 1, 2]);
      expect(store.hasModules()).toBeTrue();
      expect(store.totalCount()).toBe(3);
    });

    it('resolves the selected page against identifier ZERO', () => {
      loadTabsWith(0, [tabRow({ tabId: 0, tabName: 'Home' }), tabRow({ tabId: 1, tabName: 'About' })]);

      store.selectTab(0);

      // A `find` written against a truthiness test rather than an exact comparison returns the wrong row
      // or none at all here, because page zero is the first page of the installation.
      expect(store.selectedTab()?.tabId).toBe(0);
      expect(store.selectedTab()?.tabName).toBe('Home');
    });

    it('treats "nothing selected" as a DISTINCT undefined, never as zero and never as minus one', () => {
      expect(store.selectedModuleId()).toBeUndefined();
      expect('selectedTabModuleId' in store).toBeFalse();
      expect(store.selectedTabId()).toBeUndefined();

      store.selectModule(0);
      expect(store.selectedModuleId()).toBe(0);

      store.selectModule(-1);
      expect(store.selectedModuleId()).toBe(-1);

      store.selectModule(undefined);
      expect(store.selectedModuleId()).toBeUndefined();

      store.selectTab(0);
      expect(store.selectedTabId()).toBe(0);
      store.selectTab(undefined);
      expect(store.selectedTabId()).toBeUndefined();

      expect('selectPlacement' in store).toBeFalse();
    });

    it('addresses one placement of module ZERO when a placement is NAMED on the call', () => {
      // A placement identity seeds at 1 rather than at 0, so the two keys are not interchangeable even
      // where their ranges overlap. Naming a placement must narrow the read to it, and it is named on the
      // call rather than selected beforehand: the store holds no placement between calls.
      store.loadModule(0, 7);

      const call = expectRequest('GET', '/api/v1/modules/0');
      expect(sentFilters(call).get('tabModuleId')).toBe('7');

      call.flush(envelope(detail({ moduleId: 0, tabModuleId: 7 })));
      expect(store.module()?.tabModuleId).toBe(7);
    });
  });

  // PROOF 2 - THE TWO CACHE PERIODS ARE TWO FACTS, AND NOTHING FOLDS ONE ONTO THE OTHER
  describe('cacheTime and defaultCacheTime', () => {
    it('keeps an instance period of ZERO beside a definition default of MINUS ONE, both exactly', () => {
      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(envelope(detail({ cacheTime: 0 })));

      store.loadDefinitions();
      expectRequest('GET', '/api/v1/module-definitions').flush(
        envelope([definition({ defaultCacheTime: -1 })]),
      );

      // Read as two separate facts, compared exactly. A coalescing expression on either side would show
      // up here as the other side's value appearing, or as a zero being replaced.
      expect(store.module()?.cacheTime).toBe(0);
      expect(store.definitions()[0].defaultCacheTime).toBe(-1);
    });

    it('keeps the INVERSE pairing too, proving neither is normalised onto the other', () => {
      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(envelope(detail({ cacheTime: -1 })));

      store.loadDefinition(4);
      expectRequest('GET', '/api/v1/module-definitions/4').flush(
        envelope(definition({ defaultCacheTime: 0 })),
      );

      expect(store.module()?.cacheTime).toBe(-1);
      expect(store.definition()?.defaultCacheTime).toBe(0);
    });

    it('exposes NO derived "effective" cache period that merges the two', () => {
      expect('effectiveCacheTime' in store).toBeFalse();
      expect('resolvedCacheTime' in store).toBeFalse();
      expect('cacheTimeOrDefault' in store).toBeFalse();
      expect('cachePeriod' in store).toBeFalse();
    });

    it('does not invent a cache member on a listing row when a replacement is echoed back', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5 })]);

      store.updateModule(0, updateRequest(), 5);
      expectRequest('PUT', '/api/v1/modules/0').flush(
        envelope(detail({ moduleId: 0, tabModuleId: 5, cacheTime: 1200 })),
      );

      const patched: ModuleListItem = store.modules()[0];
      expect(patched.tabModuleId).toBe(5);
      expect('cacheTime' in patched).toBeFalse();
      expect('defaultCacheTime' in patched).toBeFalse();
    });
  });

  // PROOF 3 - REMOVAL IS SOFT, SO THE LISTING IS RE-READ AND NO ROW IS PRUNED LOCALLY
  describe('removal is soft and requires a re-read', () => {
    /**
     * Loads a settings bag into the store, which is the state a settings screen leaves behind.
     *
     * @param moduleId The module the bag describes.
     */
    function cacheSettingsFor(moduleId: number): void {
      store.loadSettings(moduleId);
      expectRequest('GET', `/api/v1/modules/${moduleId}/settings`).flush(
        envelope(settingsBag({ moduleId })),
      );
    }

    /** Answers the re-read the removal issues, so no request is left outstanding. */
    function answerReread(): void {
      expectRequest('GET', '/api/v1/modules').flush(pagedBody([]));
    }

    /**
     * ⚠ THE STATE-DEPENDENT CRITICAL DEFECT. This store is `providedIn: 'root'`, so a settings bag read
     * for one screen outlives it.
     */
    it('discards the cached record of the module it removed', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);
      cacheSettingsFor(0);

      expect(store.settings()?.moduleId).withContext('cached before the removal').toBe(0);

      store.deleteModule(0);
      expectRequest('DELETE', '/api/v1/modules/0').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      answerReread();

      expect(store.settings())
        .withContext('nothing is left for a form to hydrate a deleted module from')
        .toBeNull();
    });

    /**
     * The complement, and the reason the invalidation is scoped by identifier rather than clearing the
     * slice outright: a bag describing some OTHER module is still a truthful answer about that module,
     * and discarding it would make the next screen read it again for no reason.
     */
    it('keeps a cached record that belongs to a different module', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);
      cacheSettingsFor(7);

      store.deleteModule(0);
      expectRequest('DELETE', '/api/v1/modules/0').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      answerReread();

      expect(store.settings()?.moduleId).withContext('untouched').toBe(7);
    });

    /**
     * Module ZERO is a real module — `Modules.ModuleID` is `IDENTITY(0, 1)` — so the comparison must be
     * on the identifier and never on truthiness. A truthiness-guarded invalidation would silently skip
     * exactly this row, which is also the row the sibling specs in this block use.
     */
    it('invalidates module ZERO, which a truthiness test would skip', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);
      cacheSettingsFor(0);

      store.deleteModule(0);
      expectRequest('DELETE', '/api/v1/modules/0').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      answerReread();

      expect(store.settings()).toBeNull();
    });

    it('issues the removal and then a SECOND request that re-reads the listing', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);

      store.deleteModule(0);

      // First the removal, at the literal path for module ZERO. A truthiness-guarded identifier would
      // produce no request at all, and this expectation is what fails when that happens.
      const removal = expectRequest('DELETE', '/api/v1/modules/0');
      expect(sentFilters(removal).has('tabModuleId')).toBeFalse();
      removal.flush(null, { status: 204, statusText: 'No Content' });

      // Then the mandatory re-read. A store that optimistically spliced the row out and issued nothing
      // fails HERE, which is the point of the specification.
      const reread = expectRequest('GET', '/api/v1/modules');
      reread.flush(pagedBody([]));

      expect(store.saving()).toBeFalse();
      expect(store.modules().length).toBe(0);
    });

    it('leaves a soft-removed row in place, flagged, when the re-read still returns it', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);

      store.setIncludeDeleted(true);
      store.deleteModule(0);
      expectRequest('DELETE', '/api/v1/modules/0').flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      const reread = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(reread).get('includeDeleted')).toBe('true');
      reread.flush(pagedBody([listRow({ moduleId: 0, tabModuleId: 1, isDeleted: true })]));

      // Retained and MARKED - not silently discarded. An optimistic splice would have hidden a row the
      // server deliberately still returns.
      expect(store.modules().length).toBe(1);
      expect(store.modules()[0].isDeleted).toBeTrue();
      expect(store.modules()[0].moduleId).toBe(0);
    });

    it('removes the row only when the re-read genuinely no longer carries it', () => {
      loadListWith([
        listRow({ moduleId: 0, tabModuleId: 1 }),
        listRow({ moduleId: 1, tabModuleId: 2 }),
      ]);

      store.deleteModule(1, 2);
      const removal = expectRequest('DELETE', '/api/v1/modules/1');
      expect(sentFilters(removal).get('tabModuleId')).toBe('2');
      removal.flush(null, { status: 204, statusText: 'No Content' });

      expectRequest('GET', '/api/v1/modules').flush(pagedBody([listRow({ moduleId: 0, tabModuleId: 1 })]));

      expect(store.modules().map((row) => row.moduleId)).toEqual([0]);
    });

    it('never addresses a reversal endpoint, because the target publishes none', () => {
      loadListWith([listRow()]);

      store.deleteModule(0);
      expectRequest('DELETE', '/api/v1/modules/0').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expectRequest('GET', '/api/v1/modules').flush(pagedBody([]));

      // No bin endpoint, no reversal endpoint, and the page surface is closed at read, read-one and
      // replace, so nothing on this slice can undo a removal. The fragments are composed at runtime so that
      // the token being forbidden does not itself appear in this tree - see the note at the head.
      expectNoReversalRequest();

      for (const fragment of REVERSAL_FRAGMENTS) {
        expect(`${fragment}Module` in store).toBeFalse();
      }

      expect('undoRemoval' in store).toBeFalse();
    });

    it('issues NO re-read when the removal is refused', () => {
      loadListWith([listRow({ moduleId: 0 })]);

      store.deleteModule(0);
      expectRequest('DELETE', '/api/v1/modules/0').flush(problem('module.protected', 403, 'Refused.'), {
        status: 403,
        statusText: 'Forbidden',
      });

      // Nothing was removed, so nothing needs re-reading, and the verification in `afterEach` proves no
      // second request went out. The row the listing already held is untouched.
      expect(store.modules().length).toBe(1);
      expect(store.failure()?.operation).toBe('deleteModule');
      expect(store.saving()).toBeFalse();
    });
  });

  // PROOF 4 - THE EXPORTED DOCUMENT ARRIVES IN THE BODY AND IS HELD AS AN OPAQUE STRING
  describe('export holds the returned document opaquely', () => {
    it('posts to /api/v1/modules/0/export and retains the body as a plain string', () => {
      const request: ModuleExportRequest = exportRequest();
      const document =
        '<?xml version="1.0" encoding="utf-8" ?>' +
        '<content type="Announcements" version="01.00.00">' +
        '<announcement><title>Release</title><text></text></announcement>' +
        '</content>';

      store.exportModule(0, request);

      const call = expectRequest('POST', '/api/v1/modules/0/export');

      expect(call.request.body).toEqual(request);
      expect(call.request.responseType).toBe('text');
      expect(store.exporting()).toBeTrue();

      call.flush(document);

      expect(typeof store.exportedContent()).toBe('string');
      expect(store.exportedContent()).toBe(document);
      expect(store.exporting()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('holds a document containing a script element INERT, as text and nothing else', () => {
      // The exported content is module-authored, which makes it the most untrusted string this slice holds
      // - a second, independent untrusted channel alongside the wording in a problem document.
      const hostile = '<content type="Announcements"><script>alert(1)</script></content>';

      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush(hostile);

      const held: string | null = store.exportedContent();
      expect(typeof held).toBe('string');
      expect(held).toBe(hostile);
      expect(held).toContain('<script>alert(1)</script>');
    });

    it('retains an EMPTY document as the empty string rather than normalising it to null', () => {
      expect(store.exportedContent()).toBeNull();

      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush('');

      expect(store.exportedContent()).toBe('');
      expect(store.exportedContent()).not.toBeNull();
    });

    it('clears the previous document before a new export, so a stale one cannot be read as fresh', () => {
      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush('<content />');
      expect(store.exportedContent()).toBe('<content />');

      store.exportModule(1, exportRequest({ fileName: 'Links' }));
      // Cleared while the second request is in flight, so a panel cannot show module zero's document
      // labelled as module one's.
      expect(store.exportedContent()).toBeNull();

      expectRequest('POST', '/api/v1/modules/1/export').flush('<content type="Links" />');
      expect(store.exportedContent()).toBe('<content type="Links" />');
    });

    it('recovers a refusal that arrived as TEXT, because this response is read as text', () => {
      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush(
        JSON.stringify(problem('module.not_portable', 409, 'Not supported.')),
        { status: 409, statusText: 'Conflict' },
      );

      expect(store.exportedContent()).toBeNull();
      expect(store.exporting()).toBeFalse();
      expect(store.failure()?.operation).toBe('exportModule');
      expect(store.failure()?.code).toBe('module.not_portable');
      expect(store.failure()?.problem?.status).toBe(409);
    });

    it('falls back to the status alone when a refusal body is not a document', () => {
      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush('<html>Gateway problem</html>', {
        status: 502,
        statusText: 'Bad Gateway',
      });

      // The status is what the server sent, so nothing is invented; it is what lets the severity and the
      // wording still resolve, and it is why a refusal with an empty body is still presented as a refusal.
      expect(store.failure()?.problem?.status).toBe(502);
      expect(store.failure()?.code).toBeNull();
      expect(store.failure()?.summary.severity).toBe('error');
    });
  });

  describe('import posts to a route with no identifier', () => {
    it('addresses exactly /api/v1/modules/import, with no identifier segment in either direction', () => {
      store.importModule(importRequest());

      const call = expectRequest('POST', '/api/v1/modules/import');

      expect(call.request.url).toBe('/api/v1/modules/import');
      expect(call.request.url).not.toMatch(/\/modules\/-?\d+\/import$/);

      // The REAL query string, not the folded view: this asserts that the address carries nothing, and this
      // request legitimately carries a body.
      expect(call.request.params.keys().length).toBe(0);

      call.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('transmits a body target of exactly MINUS ONE - not null, not omitted, not zero', () => {
      store.importModule(importRequest({ moduleId: -1 }));

      const call = expectRequest('POST', '/api/v1/modules/import');
      const body: unknown = call.request.body;

      // The body is narrowed through a predicate before being read, so the assertions below cannot be
      // skipped silently by a body of the wrong shape. This is the single most likely place a coalescing
      // expression or a truthiness filter would corrupt a payload while the request still answered `204`.
      expect(isImportBody(body)).toBeTrue();

      if (isImportBody(body)) {
        expect(body.moduleId).toBe(-1);
        expect(body.moduleId).not.toBeNull();
        expect(body.moduleId).not.toBe(0);
      }

      call.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('transmits a body target of exactly ZERO, which names a real module', () => {
      store.importModule(importRequest({ moduleId: 0 }));

      const call = expectRequest('POST', '/api/v1/modules/import');
      const body: unknown = call.request.body;

      expect(isImportBody(body)).toBeTrue();

      if (isImportBody(body)) {
        expect(body.moduleId).toBe(0);
        expect(body.moduleId).not.toBeNull();
      }

      call.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('transmits the whole contract, including the empty descriptive members', () => {
      const request: ModuleImportRequest = importRequest({ folder: '', fileName: '' });

      store.importModule(request);

      const call = expectRequest('POST', '/api/v1/modules/import');
      expect(call.request.body).toEqual(request);

      call.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('reports completion on success WITHOUT synthesising a row from the payload', () => {
      // MEASURED DIVERGENCE, ASSERTED AS THE SLICE ACTUALLY BEHAVES. An import replaces a module's CONTENT
      // and changes no column the listing projects, so the command records completion and issues no further
      // request; the verification in `afterEach` is what proves the absence.
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);

      store.importModule(importRequest({ moduleId: 4 }));
      expectRequest('POST', '/api/v1/modules/import').flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.importCompleted()).toBeTrue();
      expect(store.importing()).toBeFalse();
      expect(store.modules().map((row) => row.moduleId)).toEqual([0]);
    });

    it('surfaces the invalid-document refusal under the server code', () => {
      store.importModule(importRequest());
      expectRequest('POST', '/api/v1/modules/import').flush(
        problem('module.content_invalid', 400, 'The file you selected does not contain a valid XML structure'),
        { status: 400, statusText: 'Bad Request' },
      );

      expect(store.failure()?.operation).toBe('importModule');
      expect(store.failure()?.code).toBe('module.content_invalid');
      expect(store.importCompleted()).toBeFalse();
    });

    it('surfaces the wrong-type refusal under the server code', () => {
      store.importModule(importRequest());
      expectRequest('POST', '/api/v1/modules/import').flush(
        problem(
          'module.content_type_mismatch',
          409,
          'The import file specified is not the correct type for this module',
        ),
        { status: 409, statusText: 'Conflict' },
      );

      expect(store.failure()?.code).toBe('module.content_type_mismatch');
      expect(store.failure()?.problem?.status).toBe(409);
    });

    it('surfaces the unsupported-module refusal under the server code', () => {
      store.importModule(importRequest());
      expectRequest('POST', '/api/v1/modules/import').flush(
        problem('module.not_portable', 409, 'The module selected does not support the importing of content'),
        { status: 409, statusText: 'Conflict' },
      );

      expect(store.failure()?.code).toBe('module.not_portable');
    });

    it('clears a previous completion when a new import starts', () => {
      store.importModule(importRequest());
      expectRequest('POST', '/api/v1/modules/import').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expect(store.importCompleted()).toBeTrue();

      store.importModule(importRequest({ moduleId: 0 }));
      expect(store.importCompleted()).toBeFalse();
      expectRequest('POST', '/api/v1/modules/import').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expect(store.importCompleted()).toBeTrue();

      store.clearTransferOutcome();
      expect(store.importCompleted()).toBeFalse();
      expect(store.exportedContent()).toBeNull();
    });

    it('reads no file and parses no document on this side', () => {
      store.importModule(importRequest());

      const call = expectRequest('POST', '/api/v1/modules/import');
      expect(call.request.responseType).toBe('json');
      expect(typeof call.request.body).toBe('object');

      call.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  // PROOF 6 - THE PAGE HIERARCHY IS DERIVED HERE, AND THE ROOT TEST IS AN EXACT EQUALITY
  // What neither test can be wrong about is the case that matters most: `dbo.Tabs.TabID` is `IDENTITY (0,
  // 1)` (`01.00.00.SqlDataProvider` line 140), so a parent of ZERO is a REAL PARENT under both encodings,
  // and only an exact equality survives that.
  describe('page hierarchy derivation', () => {
    it('nests a page whose parent is page ZERO under page zero, and does NOT re-parent it to the root', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 0, tabName: 'Home', parentId: -1, tabOrder: 1 }),
        tabRow({ tabId: 9, tabName: 'News', parentId: 0, tabOrder: 1, level: 1 }),
      ]);

      const tree: readonly TabTreeNode[] = store.tabTree();

      expect(tree.length).toBe(1);
      expect(tree[0].tab.tabId).toBe(0);
      expect(tree[0].children.length).toBe(1);
      expect(tree[0].children[0].tab.tabId).toBe(9);
      expect(tree[0].children[0].depth).toBe(1);

      // And nothing was reported unplaceable, which is the other half of the claim: a defective root test
      // that promoted the child would leave the root set with two members instead of one.
      expect(store.orphanTabs().length).toBe(0);
    });

    it('treats the LEGACY encoding, where parentId === -1, as a root', () => {
      const legacyRoot: TabListItem = tabRow({ tabId: 3, tabName: 'Home', parentId: -1 });

      expect(legacyRoot.parentId === -1).toBeTrue();

      loadTabsWith(0, [legacyRoot, tabRow({ tabId: 4, tabName: 'News', parentId: 3, level: 1 })]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([3]);
      expect(store.tabTree()[0].children.map((node) => node.tab.tabId)).toEqual([4]);
      expect(store.orphanTabs().length).toBe(0);
    });

    it('treats the WIRE encoding, where the parent is null, as a root', () => {
      const wireRoot: TabListItem = tabRow({ tabId: 7, tabName: 'Home' });

      expect(wireRoot.parentId).toBeNull();

      loadTabsWith(-1, [wireRoot, tabRow({ tabId: 8, tabName: 'News', parentId: 7, level: 1 })]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([7]);
      expect(store.tabTree()[0].children.map((node) => node.tab.tabId)).toEqual([8]);
      expect(store.orphanTabs().length).toBe(0);
    });

    it('nests a three-level chain whose middle page is page ZERO', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 12, tabName: 'Leaf', parentId: 0, tabOrder: 1, level: 2 }),
        tabRow({ tabId: 0, tabName: 'Branch', parentId: 4, tabOrder: 1, level: 1 }),
        tabRow({ tabId: 4, tabName: 'Root', parentId: -1, tabOrder: 1, level: 0 }),
      ]);

      const roots: readonly TabTreeNode[] = store.tabTree();
      expect(roots.length).toBe(1);

      const root: TabTreeNode = roots[0];
      expect(root.tab.tabId).toBe(4);
      expect(root.depth).toBe(0);

      const branch: TabTreeNode = root.children[0];
      expect(branch.tab.tabId).toBe(0);
      expect(branch.depth).toBe(1);

      const leaf: TabTreeNode = branch.children[0];
      expect(leaf.tab.tabId).toBe(12);
      expect(leaf.depth).toBe(2);
      // A leaf carries an EMPTY children collection rather than an absent one, which is what lets a
      // consumer walk the structure without narrowing at every node.
      expect(leaf.children.length).toBe(0);
      expect(store.orphanTabs().length).toBe(0);
    });

    it('orders siblings by page order, breaking a tie by identifier so the result is stable', () => {
      // Two siblings sharing a page order is ordinary in this schema, and without a tiebreak their relative
      // position would depend on the order the server happened to return them in - which makes a screen
      // appear to reshuffle between reads. Page ZERO participates in the ordering as an ordinary value.
      loadTabsWith(0, [
        tabRow({ tabId: 5, tabName: 'Third', parentId: -1, tabOrder: 3 }),
        tabRow({ tabId: 2, tabName: 'Tie B', parentId: -1, tabOrder: 2 }),
        tabRow({ tabId: 0, tabName: 'Tie A', parentId: -1, tabOrder: 2 }),
        tabRow({ tabId: 1, tabName: 'First', parentId: -1, tabOrder: 1 }),
      ]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([1, 0, 2, 5]);

      // Read twice, to prove the ordering is a property of the derivation rather than of the read.
      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([1, 0, 2, 5]);
    });

    it('orders children by page order at every depth', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 0, tabName: 'Root', parentId: -1, tabOrder: 1 }),
        tabRow({ tabId: 6, tabName: 'Second child', parentId: 0, tabOrder: 2, level: 1 }),
        tabRow({ tabId: 3, tabName: 'First child', parentId: 0, tabOrder: 1, level: 1 }),
      ]);

      expect(store.tabTree()[0].children.map((node) => node.tab.tabId)).toEqual([3, 6]);
    });

    it('TERMINATES on a mutual parent cycle and reports both pages rather than looping', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 1, tabName: 'A', parentId: 2 }),
        tabRow({ tabId: 2, tabName: 'B', parentId: 1 }),
        tabRow({ tabId: 0, tabName: 'Home', parentId: -1 }),
      ]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([0]);
      expect(store.orphanTabs().map((tab) => tab.tabId).sort()).toEqual([1, 2]);
    });

    it('TERMINATES on a self-parenting page and reports it', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 4, tabName: 'Loop', parentId: 4 }),
        tabRow({ tabId: 0, tabName: 'Home', parentId: -1 }),
      ]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([0]);
      expect(store.orphanTabs().map((tab) => tab.tabId)).toEqual([4]);
    });

    it('SURFACES a page whose parent is absent from the response rather than dropping it', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 0, tabName: 'Home', parentId: -1 }),
        tabRow({ tabId: 11, tabName: 'Detached', parentId: 99, level: 1 }),
      ]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([0]);
      expect(store.orphanTabs().map((tab) => tab.tabId)).toEqual([11]);
      expect(store.orphanTabs()[0].tabName).toBe('Detached');
    });

    it('CONSERVES every page: each row appears exactly once across the tree and the report', () => {
      const rows: readonly TabListItem[] = [
        tabRow({ tabId: 0, tabName: 'Home', parentId: -1, tabOrder: 1 }),
        tabRow({ tabId: 1, tabName: 'Under home', parentId: 0, tabOrder: 1, level: 1 }),
        tabRow({ tabId: 2, tabName: 'Wire root', tabOrder: 2 }),
        tabRow({ tabId: 3, tabName: 'Detached', parentId: 404, level: 1 }),
        tabRow({ tabId: 4, tabName: 'Cycle A', parentId: 5 }),
        tabRow({ tabId: 5, tabName: 'Cycle B', parentId: 4 }),
      ];

      loadTabsWith(0, rows);

      const hierarchy: TabHierarchy = store.tabHierarchy();
      const placed: readonly number[] = placedIds(hierarchy);
      const reported: readonly number[] = hierarchy.orphans.map((tab) => tab.tabId);
      const accounted: number[] = [...placed, ...reported].sort((left, right) => left - right);

      expect(accounted).toEqual([0, 1, 2, 3, 4, 5]);
      expect(new Set(accounted).size).toBe(rows.length);
      expect([...placed].sort((left, right) => left - right)).toEqual([0, 1, 2]);
      expect([...reported].sort((left, right) => left - right)).toEqual([3, 4, 5]);
    });

    it('holds the listing row itself on each node rather than re-declaring a page shape', () => {
      const home: TabListItem = tabRow({ tabId: 0, tabName: 'Home', parentId: -1 });

      loadTabsWith(0, [home]);

      const node: TabTreeNode = store.tabTree()[0];

      expect(node.tab.tabName).toBe('Home');
      expect(node.tab.tabPath).toBe('//Home');
      expect(node.tab.isVisible).toBeTrue();
      expect(node.tab.level).toBe(0);
      expect(node.depth).toBe(0);
    });

    it('re-derives the hierarchy from the list, so a second read cannot show the first one', () => {
      loadTabsWith(0, [tabRow({ tabId: 0, parentId: -1 })]);
      expect(store.tabTree().length).toBe(1);

      loadTabsWith(-1, [
        tabRow({ tabId: 20, tabName: 'Root', parentId: -1 }),
        tabRow({ tabId: 21, tabName: 'Child', parentId: 20, level: 1 }),
      ]);

      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([20]);
      expect(store.tabTree()[0].children.map((node) => node.tab.tabId)).toEqual([21]);
    });

    it('cannot be corrupted by a consumer mutating the collections it was handed', () => {
      loadTabsWith(0, [
        tabRow({ tabId: 0, parentId: -1 }),
        tabRow({ tabId: 1, parentId: 0, level: 1 }),
      ]);

      const roots: readonly TabTreeNode[] = store.tabTree();
      const children: readonly TabTreeNode[] = roots[0].children;

      // A consumer holds READONLY collections, so a mutation is a compile error rather than a runtime
      // surprise; what it can do is take a copy, and a copy must not be a window onto the slice's own
      // state. This matters more here than anywhere else in the file, because the structure is nested.
      const copiedRoots: TabTreeNode[] = [...roots];
      const copiedChildren: TabTreeNode[] = [...children];
      copiedRoots.length = 0;
      copiedChildren.length = 0;

      expect(store.tabTree().length).toBe(1);
      expect(store.tabTree()[0].children.length).toBe(1);
      expect(store.tabs().length).toBe(2);
    });

    it('copies the response rather than holding the array the transport returned', () => {
      const rows: TabListItem[] = [tabRow({ tabId: 0, parentId: -1 })];

      store.loadTabs(0);
      expectRequest('GET', '/api/v1/portals/0/tabs').flush(tabPage(rows));

      rows.push(tabRow({ tabId: 1, tabName: 'Added later', parentId: 0 }));

      // The slice took a snapshot, so a caller still holding the array cannot change what the hierarchy
      // reports afterwards.
      expect(store.tabs().length).toBe(1);
      expect(store.tabTree()[0].children.length).toBe(0);
    });
  });

  // PROOF 7 - EVERY LOOKUP IS UNPAGED, AND NO PAGING STATE IS KEPT FOR ONE
  describe('the picker choice set is complete, or it is a refusal', () => {
    /**
     * A paged envelope whose total is stated independently of the page's length.
     *
     * @param items The page's rows.
     * @param totalCount The total across every page, as the server states it.
     * @param pageIndex The zero-based index of this page.
     * @returns The envelope.
     */
    function choicePage(
      items: readonly ModuleListItem[],
      totalCount: number,
      pageIndex = 0,
    ): PagedResult<ModuleListItem> {
      return {
        items,
        meta: {
          totalCount,
          pageIndex,
          pageSize: MAX_PAGE_SIZE,
          totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / MAX_PAGE_SIZE),
        },
      };
    }

    /** Every outstanding module-listing request, so concurrency can be counted rather than assumed. */
    function outstandingListings(): readonly TestRequest[] {
      return httpMock.match(
        (candidate) => isListingRead(candidate),
      );
    }

    it('costs exactly one request when the tenant fits inside one page', () => {
      store.loadChoices();

      const only = expectRequest('GET', '/api/v1/modules');

      expect(sentFilters(only).get('pageIndex')).toBe('0');
      expect(sentFilters(only).get('pageSize'))
        .withContext('the widest page the paging validator accepts')
        .toBe(String(MAX_PAGE_SIZE));

      only.flush(choicePage([listRow({ moduleId: 1 }), listRow({ moduleId: 2 })], 2));

      // No follow-on: the server's own total is satisfied, so the ordinary case is no slower than
      // the single read this walk replaced.
      httpMock.verify();

      expect(store.choices().map((row) => row.moduleId)).toEqual([1, 2]);
      expect(store.choicesTotal()).toBe(2);
      expect(store.choicesLoading()).toBeFalse();
      expect(store.failure()).toBeNull();
    });

    it('walks every page so no placement is silently left out of the picker', () => {
      // ⚠ THE M-1 REGRESSION, PINNED. The previous implementation issued ONE request at the widest page
      // size and published its rows as the choice set, so the hundred-and-first placement in a tenant
      // simply could not be chosen and nothing said so.
      const firstPage: readonly ModuleListItem[] = Array.from(
        { length: MAX_PAGE_SIZE },
        (_unused, index) => listRow({ moduleId: index }),
      );

      store.loadChoices();
      expectRequest('GET', '/api/v1/modules').flush(choicePage(firstPage, MAX_PAGE_SIZE + 2));

      const second = expectRequest('GET', '/api/v1/modules');

      expect(sentFilters(second).get('pageIndex'))
        .withContext('the walk continues past the first window')
        .toBe('1');
      second.flush(
        choicePage(
          [listRow({ moduleId: 500 }), listRow({ moduleId: 501 })],
          MAX_PAGE_SIZE + 2,
          1,
        ),
      );

      httpMock.verify();

      expect(store.choices().length).toBe(MAX_PAGE_SIZE + 2);
      expect(store.choices()[MAX_PAGE_SIZE + 1]?.moduleId)
        .withContext('a placement beyond the first window is choosable rather than truncated away')
        .toBe(501);
      expect(store.choicesTotal()).toBe(MAX_PAGE_SIZE + 2);
    });

    it('requests one page at a time rather than fanning them out', () => {
      store.loadChoices();

      let maximumInFlight = 0;
      let issued = 0;

      for (let page = 0; page < 3; page += 1) {
        const outstanding = outstandingListings();

        maximumInFlight = Math.max(maximumInFlight, outstanding.length);
        issued += outstanding.length;

        outstanding.forEach((request) =>
          request.flush(choicePage([listRow({ moduleId: page })], 3, page)),
        );
      }

      expect(maximumInFlight)
        .withContext('a picker opening must not become a burst against the tenant API')
        .toBe(1);
      expect(issued).toBe(3);
      expect(store.choices().length).toBe(3);
    });

    it('refuses the choice set rather than publishing a subset when the server stops short', () => {
      store.loadChoices();
      expectRequest('GET', '/api/v1/modules').flush(choicePage([listRow({ moduleId: 1 })], 90));
      expectRequest('GET', '/api/v1/modules').flush(choicePage([], 90, 1));

      expect(store.choices())
        .withContext('an incomplete choice set that looks complete is the defect being removed')
        .toEqual([]);
      expect(store.choicesTotal()).toBe(0);
      expect(store.choicesLoading()).toBeFalse();
      // The picker read's own failure identity, not the route's: the grid read addresses the same
      // endpoint, and the transfer screens filter their refusal surface on this name.
      expect(store.failure()?.operation).toBe('loadChoices');
    });

    it('stops at the page ceiling by refusing rather than by truncating', () => {
      store.loadChoices();

      let issued = 0;
      let maximumInFlight = 0;

      for (;;) {
        const outstanding = outstandingListings();

        if (outstanding.length === 0) {
          break;
        }

        maximumInFlight = Math.max(maximumInFlight, outstanding.length);
        issued += outstanding.length;
        outstanding.forEach((request) => {
          const pageIndex = Number(sentFilters(request).get('pageIndex') ?? '0');

          // One row per page against an unreachable total, which is what keeps the walk going: the
          // walk continues on the TOTAL rather than on a full page.
          request.flush(
            choicePage([listRow({ moduleId: pageIndex })], Number.MAX_SAFE_INTEGER, pageIndex),
          );
        });
      }

      expect(maximumInFlight).toBe(1);
      expect(issued).toBe(MAXIMUM_CHOICE_PAGES);
      expect(store.choices()).toEqual([]);
      expect(store.choicesTotal()).toBe(0);
      // ⚠ THE PICKER READ HAS ITS OWN FAILURE IDENTITY. It shares an endpoint with the grid read, so naming
      // the route would make a picker refusal indistinguishable from any listing refusal anywhere in the
      // application — and the transfer screens filter their refusal surface on exactly this name.
      expect(store.failure()?.operation).toBe('loadChoices');
      expect(store.choicesLoading()).toBeFalse();
    });

    it('leaves the browsable listing entirely alone', () => {
      // The separation the choices slice exists for: opening a picker must not resize, re-order or
      // repaginate a listing a sibling screen is showing.
      store.setPageSize(25);
      store.setPageIndex(3);
      loadListWith([listRow({ moduleId: 7 })], 3, 25);

      store.loadChoices();
      expectRequest('GET', '/api/v1/modules').flush(choicePage([listRow({ moduleId: 9 })], 1));

      expect(store.query().pageIndex).toBe(3);
      expect(store.query().pageSize).toBe(25);
      expect(store.page().items.map((row) => row.moduleId)).toEqual([7]);
      expect(store.choices().map((row) => row.moduleId)).toEqual([9]);
    });
  });

  describe('unpaged lookups', () => {
    it('sends NO paging, ordering or filtering parameter when reading the pages of a portal', () => {
      // The listing's own paging arguments must not leak onto a different request. Absence is asserted with
      // the presence test rather than by reading a value, because a value read as null is a different claim
      // from a parameter that was never set.
      store.setPageIndex(3);
      store.setPageSize(50);
      store.setSort('moduleTitle', 'Descending');
      store.setQuery('news');

      store.loadTabs(0);
      const call = expectRequest('GET', '/api/v1/portals/0/tabs');

      // MIGRATION: THE HIERARCHY READ NOW CARRIES ITS OWN PAGE COORDINATE, AND STILL NOTHING ELSE. What
      // this case exists to prove is unchanged - the LISTING's coordinate, sort and filter never leak into
      // the hierarchy read - so the two paging arguments the bounded endpoint requires are asserted by name
      // and every listing coordinate is asserted absent.
      expect([...call.request.params.keys()].sort()).toEqual(['pageIndex', 'pageSize']);
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe(String(TAB_PAGE_SIZE));
      expect(call.request.params.has('sortBy')).toBeFalse();
      expect(call.request.params.has('sortDir')).toBeFalse();
      expect(call.request.params.has('query')).toBeFalse();

      call.flush(tabPage([tabRow({ tabId: 0, parentId: -1 })]));
      expect(store.tabs().length).toBe(1);
    });

    it('reads the whole definition catalogue with an entirely empty query', () => {
      store.loadDefinitions();
      const call = expectRequest('GET', '/api/v1/module-definitions');

      expect(sentFilters(call).keys().length).toBe(0);

      call.flush(envelope([definition({ moduleDefId: 4 }), definition({ moduleDefId: 5 })]));

      expect(store.definitions().length).toBe(2);
      expect(store.definitionsLoading()).toBeFalse();
    });

    it('reads the definitions of one bundle from a path segment, not from a query parameter', () => {
      store.loadDesktopDefinitions(2);
      const call = expectRequest('GET', '/api/v1/module-definitions/desktop-modules/2');

      expect(sentFilters(call).keys().length).toBe(0);

      call.flush(envelope([definition({ desktopModuleId: 2 })]));

      expect(store.desktopDefinitions().length).toBe(1);
      // Held apart from the whole catalogue rather than overwriting it, because a form showing one bundle
      // still needs the catalogue behind it.
      expect(store.definitions().length).toBe(0);
    });

    it('addresses one definition by the ROUTE spelling while the response keeps its own member name', () => {
      store.loadDefinition(4);
      const call = expectRequest('GET', '/api/v1/module-definitions/4');

      expect(sentFilters(call).keys().length).toBe(0);

      call.flush(envelope(definition({ moduleDefId: 4, defaultCacheTime: 900 })));

      expect(store.definition()?.moduleDefId).toBe(4);
      expect(store.definition()?.defaultCacheTime).toBe(900);

      store.clearDefinition();
      expect(store.definition()).toBeNull();
    });

    it('keeps NO page index, page size or total for any of the three lookup collections', () => {
      loadTabsWith(0, [tabRow({ tabId: 0, parentId: -1 })]);

      store.loadDefinitions();
      expectRequest('GET', '/api/v1/module-definitions').flush(envelope([definition()]));

      // The only paging coordinates in this slice belong to the module listing, and they are untouched by
      // a lookup read: the empty page the slice was seeded with is still what the coordinates report.
      expect(store.meta().totalCount).toBe(0);
      expect(store.meta().totalPages).toBe(0);
      expect('tabsPageIndex' in store).toBeFalse();
      expect('tabsTotalCount' in store).toBeFalse();
      expect('definitionsPageIndex' in store).toBeFalse();
      expect('definitionsTotalCount' in store).toBeFalse();
      expect('desktopDefinitionsPageIndex' in store).toBeFalse();
    });

    it('filters the catalogue on the portability flag alone, treating false as DATA', () => {
      // The legacy marker helper reported `False` itself as "absent", so the legacy code could not tell a
      // module that does not support transfer from one whose support was unknown. Every flag on these
      // contracts is a non-nullable boolean, so `false` means "does not support it" and nothing else.
      store.loadDefinitions();
      expectRequest('GET', '/api/v1/module-definitions').flush(
        envelope([
          definition({ moduleDefId: 4, isPortable: true }),
          definition({ moduleDefId: 5, isPortable: false }),
          definition({ moduleDefId: 6, isPortable: true, isPremium: false, isAdmin: false }),
        ]),
      );

      expect(store.portableDefinitions().map((entry) => entry.moduleDefId)).toEqual([4, 6]);
      expect(store.definitions().length).toBe(3);
    });
  });

  // PROOF 8 - THE MODULE LISTING IS PAGED, AND ITS WIRE PAGE INDEX IS ZERO-BASED
  describe('the paged module listing', () => {
    it('requests the first page as index ZERO with the default size, performing no arithmetic', () => {
      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(sentFilters(call).get('pageIndex')).toBe('0');
      expect(sentFilters(call).get('pageSize')).toBe('10');
      expect(store.listLoading()).toBeTrue();

      call.flush(pagedBody([listRow()], 0, 10));

      expect(store.listLoading()).toBeFalse();
      expect(store.meta().pageIndex).toBe(0);
      expect(store.meta().pageSize).toBe(10);
      expect(store.meta().totalCount).toBe(1);
    });

    it('carries a page coordinate through unchanged in either direction', () => {
      store.setPageIndex(1);
      expect(store.query().pageIndex).toBe(1);

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(call).get('pageIndex')).toBe('1');
      call.flush(pagedBody([], 1, 10));

      expect(store.meta().pageIndex).toBe(1);
    });

    it('returns to the first page when the page size changes', () => {
      store.setPageIndex(4);
      store.setPageSize(25);

      // A coordinate measured in rows of one size does not identify the same rows once the size changes,
      // so keeping it would show a different window than the pager claims.
      expect(store.query().pageIndex).toBe(0);
      expect(store.query().pageSize).toBe(25);

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(call).get('pageIndex')).toBe('0');
      expect(sentFilters(call).get('pageSize')).toBe('25');
      call.flush(pagedBody([], 0, 25));
    });

    it('transmits the ordering tokens verbatim and returns to the first page', () => {
      store.setPageIndex(2);
      store.setSort('moduleTitle', 'Descending');

      expect(store.query().pageIndex).toBe(0);

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      // The direction token is one of the server enumeration's own member names, and the query-string
      // binder accepts nothing else - an abbreviated or lower-cased spelling is answered with a refusal.
      expect(sentFilters(call).get('sortBy')).toBe('moduleTitle');
      expect(sentFilters(call).get('sortDir')).toBe('Descending');
      call.flush(pagedBody([]));
    });

    it('clears an ordering back to the server default without dropping the member', () => {
      store.setSort('moduleTitle', 'Ascending');
      store.setSort(null, null);

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      // A null argument means "let the server choose", so the member is not transmitted at all - which is
      // a different request from transmitting an empty ordering.
      expect(sentFilters(call).has('sortBy')).toBeFalse();
      expect(sentFilters(call).has('sortDir')).toBeFalse();
      call.flush(pagedBody([]));
    });

    it('holds the filter text EXACTLY as supplied and appends no wildcard', () => {
      store.setQuery('news');

      store.loadModules();

      // The body-bound address, because a term is present. What matters to this specification is the TEXT,
      // which `sentFilters` reads from wherever it travelled.
      const call = httpMock.expectOne((candidate) => isListingRead(candidate));

      expect(sentFilters(call).get('query')).toBe('news');
      expect(sentFilters(call).get('query')).not.toContain(WILDCARD);
      call.flush(pagedBody([]));
    });

    it('transmits an EMPTY filter as an empty filter rather than as no filter', () => {
      store.setQuery('');

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(sentFilters(call).has('query')).toBeTrue();
      expect(sentFilters(call).get('query')).toBe('');
      call.flush(pagedBody([]));
    });

    it('sends a page restriction of ZERO and removes it only for an explicit undefined', () => {
      store.setTabFilter(0);
      store.loadModules();
      const restricted = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(restricted).has('tabId')).toBeTrue();
      expect(sentFilters(restricted).get('tabId')).toBe('0');
      restricted.flush(pagedBody([listRow({ tabId: 0 })]));

      store.setTabFilter(undefined);
      store.loadModules();
      const unrestricted = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(unrestricted).has('tabId')).toBeFalse();
      unrestricted.flush(pagedBody([listRow()]));
    });

    it('sends an inclusion flag of FALSE as false, because false is a value', () => {
      store.setIncludeDeleted(false);
      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(sentFilters(call).has('includeDeleted')).toBeTrue();
      expect(sentFilters(call).get('includeDeleted')).toBe('false');
      call.flush(pagedBody([]));
    });

    it('returns to the first page when a restriction changes', () => {
      store.setPageIndex(6);
      store.setTabFilter(0);
      expect(store.query().pageIndex).toBe(0);

      store.setPageIndex(6);
      store.setIncludeDeleted(true);
      expect(store.query().pageIndex).toBe(0);
    });

    it('sequences the portal scope: the pages FIRST, then the listing restricted to one of them', () => {
      // Ordering two requests is composition, so it lives here rather than in either transport - each of
      // which is confined to one method, one endpoint, one request.
      store.loadPortalScope(-1, 0);

      const pages = expectRequest('GET', '/api/v1/portals/-1/tabs');
      pages.flush(tabPage([tabRow({ tabId: 0, parentId: -1 })]));

      const listing = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(listing).get('tabId')).toBe('0');
      listing.flush(pagedBody([listRow({ tabId: 0 })]));

      expect(store.tabPortalId()).toBe(-1);
      expect(store.selectedTabId()).toBe(0);
      expect(store.modules().length).toBe(1);
    });

    it('scopes to a portal without a page restriction when none is named', () => {
      store.loadPortalScope(0);

      expectRequest('GET', '/api/v1/portals/0/tabs').flush(tabPage([tabRow({ tabId: 0, parentId: -1 })]));

      const listing = expectRequest('GET', '/api/v1/modules');
      expect(sentFilters(listing).has('tabId')).toBeFalse();
      listing.flush(pagedBody([listRow()]));

      expect(store.selectedTabId()).toBeUndefined();
    });

    it('does NOT read the listing when the page read is refused', () => {
      store.loadPortalScope(0, 0);
      expectRequest('GET', '/api/v1/portals/0/tabs').flush(
        problem('tab.portal_not_found', 404, 'No such portal.'),
        { status: 404, statusText: 'Not Found' },
      );

      // The verification in `afterEach` proves the second request never went out, which is the whole point
      // of sequencing rather than firing both at once.
      expect(store.failure()?.operation).toBe('loadTabs');
      expect(store.tabsLoading()).toBeFalse();
      expect(store.modules().length).toBe(0);
    });
  });

  // PROOF 9 - EVERY VISIBILITY CODE IS A REAL VALUE, INCLUDING ZERO AND TWO
  describe('visibility codes', () => {
    it('retains the code ZERO on a listing row rather than reading it as an absence', () => {
      loadListWith([listRow({ visibility: ModuleVisibility.Maximized })]);

      expect(store.modules()[0].visibility).toBe(ModuleVisibility.Maximized);
      expect(store.modules()[0].visibility).toBe(0);
    });

    it('retains the code TWO and does not filter such a row out of any projection', () => {
      loadListWith([
        listRow({ moduleId: 0, tabModuleId: 1, visibility: ModuleVisibility.None }),
        listRow({ moduleId: 1, tabModuleId: 2, visibility: ModuleVisibility.Minimized }),
        listRow({ moduleId: 2, tabModuleId: 3, visibility: ModuleVisibility.Maximized }),
      ]);

      expect(store.modules().map((row) => row.visibility)).toEqual([2, 1, 0]);
      expect(store.modules().length).toBe(3);
      expect(store.hasModules()).toBeTrue();
    });

    it('retains the code TWO on a detail read and on the row a replacement echoes back', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5, visibility: ModuleVisibility.Maximized })]);

      store.updateModule(0, updateRequest({ visibility: ModuleVisibility.None }), 5);
      const call = expectRequest('PUT', '/api/v1/modules/0');
      expect(call.request.body).toEqual(updateRequest({ visibility: ModuleVisibility.None }));
      call.flush(
        envelope(detail({ moduleId: 0, tabModuleId: 5, visibility: ModuleVisibility.None })),
      );

      expect(store.module()?.visibility).toBe(ModuleVisibility.None);
      expect(store.modules()[0].visibility).toBe(2);
    });

    it('uses the enumeration from the models layer and never re-declares the codes', () => {
      expect(ModuleVisibility.Maximized).toBe(0);
      expect(ModuleVisibility.Minimized).toBe(1);
      expect(ModuleVisibility.None).toBe(2);
    });

    // ⚠ THE REGRESSION GUARD FOR THE WORST DEFECT THIS APPLICATION HELD. One row carrying a code outside the
    // published three used to make the decoder refuse the row, the refusal propagated out of the page, the
    // store kept the PREVIOUS page's rows and pager text, the recorded failure carried no problem document
    // so the banner rendered nothing, and the console logged nothing. A successful HTTP 200 carrying every
    // module presented itself as an empty site, or as stale data, with no signal anywhere.
    it('renders a page containing an UNPUBLISHED code instead of discarding the page', () => {
      loadListWith([
        listRow({ moduleId: 0, tabModuleId: 1, visibility: ModuleVisibility.Maximized }),
        listRow({ moduleId: 2, tabModuleId: 3, visibility: 9 }),
        listRow({ moduleId: 5, tabModuleId: 6, visibility: ModuleVisibility.Minimized }),
      ]);

      expect(store.modules()).withContext('every row survives').toHaveSize(3);
      expect(store.modules().map((row) => row.visibility)).toEqual([0, 9, 2 - 1]);
      expect(store.failure()).withContext('and nothing failed').toBeNull();
      expect(store.listFailed()).toBeFalse();
    });

    it('keeps an unpublished code on a detail read too', () => {
      store.loadModule(2, 3);
      expectRequest('GET', '/api/v1/modules/2').flush(
        envelope(detail({ moduleId: 2, tabModuleId: 3, visibility: 9 })),
      );

      expect(store.module()?.visibility).toBe(9);
      expect(store.failure()).toBeNull();
    });
  });

  // PROOF 10 - THE PAGE SURFACE IS READ-ONLY FROM HERE, AND ONE OF ITS THREE READS IS UNUSED
  // The page transport publishes exactly three operations - read a portal's pages, read one page, replace
  // one page - and there is NO create route and NO delete route on the server to call.
  describe('page mutation is never issued from this slice', () => {
    it('never posts to or deletes from the page collection', () => {
      loadTabsWith(0, [tabRow({ tabId: 0, parentId: -1 }), tabRow({ tabId: 1, parentId: 0 })]);

      store.selectTab(1);
      store.setTabFilter(1);

      expect(httpMock.match((candidate) => candidate.method === 'POST' && candidate.url === '/api/v1/tabs'))
        .withContext('no page is created through this slice')
        .toEqual([]);
      expect(
        httpMock.match(
          (candidate) => candidate.method === 'DELETE' && candidate.url.startsWith('/api/v1/tabs'),
        ),
      )
        .withContext('no page is removed through this slice')
        .toEqual([]);
      expect(
        httpMock.match(
          (candidate) => candidate.method === 'PUT' && candidate.url.startsWith('/api/v1/tabs'),
        ),
      )
        .withContext('no page is replaced through this slice')
        .toEqual([]);
    });

    it('never reads an individual page at /api/v1/tabs/0, because it reads them by portal', () => {
      // Page ZERO is a real page - the identity seeds there - and this slice still issues no request for it
      // individually. The single-page read exists on the transport and is simply not one of this slice's
      // concerns: a picker and an indented tree both need the whole flat list, which one request supplies.
      loadTabsWith(0, [tabRow({ tabId: 0, parentId: -1 })]);

      store.selectTab(0);

      expect(store.selectedTab()?.tabId).toBe(0);
      httpMock.expectNone('/api/v1/tabs/0');
    });

    it('exposes no command that would create, replace or remove a page', () => {
      expect('createTab' in store).toBeFalse();
      expect('updateTab' in store).toBeFalse();
      expect('saveTab' in store).toBeFalse();
      expect('deleteTab' in store).toBeFalse();
      expect('moveTab' in store).toBeFalse();
      expect('loadTab' in store).toBeFalse();
    });
  });

  // PROOF 11 - SENTINEL FIDELITY, WHICH IS THE LARGEST SINGLE RISK IN THIS FEATURE
  // `Library/Components/Shared/Null.vb` defines a marker for every primitive: lines 36 to 45 give minus one
  // for a short and for an integer, line 48 gives 255 for a byte, lines 51 to 65 give the smallest value of
  // each floating type, lines 66 to 70 give the bottom of the calendar for a date, lines 71 to 75 give THE
  // EMPTY STRING for a string - the accessor body is literally `Return ""`, not a null - lines 76 to 80
  // give `False` for a boolean and lines 81 to 85 give the empty identifier for one of those.
  describe('sentinel fidelity', () => {
    it('round-trips a detail carrying every marker value unchanged', () => {
      const marked: ModuleDetail = detail({
        moduleId: 0,
        tabId: 0,
        portalId: -1,
        cacheTime: 0,
        moduleOrder: -1,
        header: '',
        footer: '',
        iconFile: '',
        alignment: null,
        color: null,
        border: null,
        description: '',
        moduleTitle: '',
        allTabs: false,
        isDeleted: false,
        inheritViewPermissions: false,
        displayTitle: false,
        visibility: ModuleVisibility.Maximized,
        startDate: MIN_DATE,
        endDate: MIN_DATE,
      });

      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(envelope(marked));

      // Compared WHOLE rather than member by member, so a single value quietly normalised anywhere in the
      // slice fails this one assertion.
      expect(store.module()).toEqual(marked);
    });

    it('retains every FALSE flag as false rather than as an absence', () => {
      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(
        envelope(
          detail({
            allTabs: false,
            isDeleted: false,
            inheritViewPermissions: false,
            displayTitle: false,
          }),
        ),
      );

      const held: ModuleDetail | null = store.module();
      expect(held?.allTabs).toBeFalse();
      expect(held?.isDeleted).toBeFalse();
      expect(held?.inheritViewPermissions).toBeFalse();
      expect(held?.displayTitle).toBeFalse();
      expect(held?.allTabs).not.toBeUndefined();
      expect(held?.displayTitle).not.toBeNull();
    });

    it('retains an EMPTY STRING as the empty string and holds it apart from null', () => {
      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(
        envelope(detail({ header: '', footer: null, iconFile: '', moduleTitle: null })),
      );

      const held: ModuleDetail | null = store.module();
      expect(held?.header).toBe('');
      expect(held?.iconFile).toBe('');
      expect(held?.footer).toBeNull();
      expect(held?.moduleTitle).toBeNull();
    });

    it('retains a date at the bottom of the calendar rather than converting it to null', () => {
      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(
        envelope(detail({ startDate: MIN_DATE, endDate: null })),
      );

      expect(store.module()?.startDate).toBe(MIN_DATE);
      expect(store.module()?.endDate).toBeNull();
    });

    it('retains cleared values in BOTH settings maps and transmits them back unfiltered', () => {
      const bag: ModuleSettingsBag = settingsBag();

      store.loadSettings(0, 1);
      const read = expectRequest('GET', '/api/v1/modules/0/settings');
      expect(sentFilters(read).get('tabModuleId')).toBe('1');
      read.flush(envelope(bag));

      expect(store.settings()).toEqual(bag);
      expect(store.settings()?.moduleSettings['Announcements_Description']).toBe('');
      expect(store.settings()?.tabModuleSettings['Announcements_Heading']).toBe('');

      store.saveSettings(bag, 1);
      const write = expectRequest('PUT', '/api/v1/modules/0/settings');
      // Transmitted WHOLE. Filtering the maps for truthiness before sending - the obvious and wrong
      // implementation - would silently delete every setting an operator had cleared, and the request would
      // still answer `204`.
      expect(write.request.body).toEqual(bag);
      write.flush(null, { status: 204, statusText: 'No Content' });

      // The write returned nothing, so the stored state is read BACK rather than assumed to equal what was
      // sent: the server may normalise a value on the way in.
      const reread = expectRequest('GET', '/api/v1/modules/0/settings');
      reread.flush(envelope(bag));

      expect(store.settingsSaving()).toBeFalse();
      expect(store.settings()).toEqual(bag);

      store.clearSettings();
      expect(store.settings()).toBeNull();
    });

    it('keeps a settings bag addressed at the MODULE apart from one addressed at a placement', () => {
      // Omitting the selector and supplying it are materially different requests, and the difference is not
      // blurred by a default. The placement member is nullable on the contract for the same reason.
      store.loadSettings(0);
      const call = expectRequest('GET', '/api/v1/modules/0/settings');
      expect(sentFilters(call).has('tabModuleId')).toBeFalse();
      call.flush(envelope(settingsBag({ tabModuleId: null })));

      expect(store.settings()?.tabModuleId).toBeNull();
    });

    it('keeps the FOUR distinct meanings of MINUS ONE apart within one scenario', () => {
      // The same number carries four unrelated meanings in this feature, and conflating any pair would be a
      // silent behavioural change.
      loadTabsWith(-1, [tabRow({ tabId: 0, tabName: 'Home', parentId: -1 })]);
      expect(store.tabTree().map((node) => node.tab.tabId)).toEqual([0]);
      expect(store.orphanTabs().length).toBe(0);

      store.loadDefinition(4);
      expectRequest('GET', '/api/v1/module-definitions/4').flush(
        envelope(definition({ defaultCacheTime: -1 })),
      );
      expect(store.definition()?.defaultCacheTime).toBe(-1);

      store.loadModule(0);
      expectRequest('GET', '/api/v1/modules/0').flush(envelope(detail({ portalId: -1, cacheTime: 0 })));
      expect(store.module()?.portalId).toBe(-1);
      expect(store.module()?.cacheTime).toBe(0);

      store.importModule(importRequest({ moduleId: -1 }));
      const transfer = expectRequest('POST', '/api/v1/modules/import');
      const body: unknown = transfer.request.body;
      expect(isImportBody(body)).toBeTrue();
      if (isImportBody(body)) {
        expect(body.moduleId).toBe(-1);
      }
      transfer.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('exposes no helper that would normalise a negative identifier', () => {
      // Such a helper cannot exist without conflating at least two of the four meanings above, which is why
      // its absence is asserted rather than its behaviour tested.
      expect('normaliseId' in store).toBeFalse();
      expect('normalizeId' in store).toBeFalse();
      expect('coerceId' in store).toBeFalse();
      expect('sanitiseId' in store).toBeFalse();
      expect('isNull' in store).toBeFalse();
      expect('setNull' in store).toBeFalse();
    });

    it('transmits a creation request whose every falsy member survives, and re-reads NO listing', () => {
      const request: CreateModuleRequest = createRequest({ tabId: 0, moduleOrder: 0, cacheTime: 0 });

      loadListWith([]);

      store.createModule(request);
      const call = expectRequest('POST', '/api/v1/modules');
      expect(call.request.body).toEqual(request);
      expect(store.saving()).toBeTrue();
      call.flush(envelope(detail({ moduleId: 0, tabModuleId: 9 })), {
        status: 201,
        statusText: 'Created',
      });

      // The created placement becomes the loaded module. Both identifiers are asserted on identity rather
      // than truthiness, which is this case's whole subject: the module identifier really is ZERO here.
      expect(store.module()?.moduleId).toBe(0);
      expect(store.selectedModuleId()).toBe(0);

      expect(
        httpMock.match(
          (candidate) => isListingRead(candidate),
        ),
      )
        .withContext('a create asks for no listing read; the listing reads itself on entry')
        .toHaveSize(0);

      expect(store.saving()).toBeFalse();
    });
  });

  // PROOF 12 - FAILURES ARE HELD STRUCTURALLY, WITH THE TRACE IDENTIFIER, AND NEVER AS MARKUP
  // The server answers with an RFC 7807 document under a problem media type. Its per-field dictionary uses
  // .NET model-state keys, which are NOT camel-cased, and because that dictionary is an index signature
  // under `noPropertyAccessFromIndexSignature` every read of it below is an INDEX EXPRESSION.
  describe('failure handling', () => {
    it('records a refusal on an all-pages replacement at WARNING severity, not as an error', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5 })]);

      store.updateModule(0, updateRequest({ allTabs: true }), 5);
      expectRequest('PUT', '/api/v1/modules/0').flush(
        problem('module.all_pages_denied', 403, 'You do not have permission to perform this action.'),
        { status: 403, statusText: 'Forbidden' },
      );

      const failure = store.failure();
      expect(failure?.operation).toBe('updateModule');
      expect(failure?.summary.severity).toBe('warning');
      expect(failure?.problem?.status).toBe(403);
      expect(store.saving()).toBeFalse();
      // The listing was untouched, so no row was optimistically changed to a state the server refused.
      expect(store.modules()[0].allTabs).toBeFalse();
    });

    it('records a server fault at ERROR severity', () => {
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(problem('server.unavailable', 500, 'Unavailable.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      expect(store.failure()?.summary.severity).toBe('error');
      expect(store.failure()?.operation).toBe('listModules');
      expect(store.listLoading()).toBeFalse();
    });

    it('carries the TRACE IDENTIFIER into the failure slice, whole', () => {
      const document: ProblemDetails = problem('module.content_invalid', 400, 'Not a valid document.');

      store.importModule(importRequest());
      expectRequest('POST', '/api/v1/modules/import').flush(document, {
        status: 400,
        statusText: 'Bad Request',
      });

      expect(store.failure()?.problem?.traceId).toBe(document.traceId);
      expect(store.failure()?.problem?.instance).toBe('/api/v1/modules');
      expect(store.failure()?.problem).toEqual(document);
    });

    it('keeps the per-field dictionary intact, read with an INDEX EXPRESSION', () => {
      const document: ValidationProblemDetails = validationProblem(400);

      store.createModule(createRequest());
      expectRequest('POST', '/api/v1/modules').flush(document, {
        status: 400,
        statusText: 'Bad Request',
      });

      const held: ProblemDetails | null | undefined = store.failure()?.problem;

      // Index expressions throughout, because the keys are .NET model-state keys and a property access is a
      // compile error against an index signature under this configuration.
      expect(held?.errors?.['ModuleTitle']).toBeDefined();
      expect(held?.errors?.['ModuleTitle']?.length).toBe(1);
      expect(held?.errors?.['Content']).toBeDefined();
      expect(store.failure()?.operation).toBe('createModule');
      expect(store.failure()?.summary.hasFieldMessages).toBeTrue();
    });

    it('holds a refusal WORDING inert, even when it carries a script element', () => {
      const hostile = 'Rejected: <script>alert(1)</script>';

      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(problem('module.rejected', 400, hostile), {
        status: 400,
        statusText: 'Bad Request',
      });

      const held: string | undefined = store.failure()?.problem?.detail;
      expect(typeof held).toBe('string');
      expect(held).toBe(hostile);
      expect(held).toContain('<script>alert(1)</script>');
    });

    it('holds a refusal wording carrying a leading break tag in BOTH legacy spellings, raw', () => {
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(
        problem('module.rejected', 400, '<br>The first problem'),
        { status: 400, statusText: 'Bad Request' },
      );
      expect(store.failure()?.problem?.detail).toBe('<br>The first problem');

      store.loadDefinitions();
      expectRequest('GET', '/api/v1/module-definitions').flush(
        problem('module.rejected', 400, '<br/>The second problem'),
        { status: 400, statusText: 'Bad Request' },
      );
      expect(store.failure()?.problem?.detail).toBe('<br/>The second problem');
    });

    it('surfaces a dotted failure code WHOLE and never splits it on the separator', () => {
      // The code is one token. Splitting it - reading only the segment before the separator, or only the one
      // after - would collapse distinct refusals from different aggregates onto one another.
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(
        problem('portal.last_remaining', 409, 'You Can Not Delete The Last Portal In Your Database'),
        { status: 409, statusText: 'Conflict' },
      );

      expect(store.failure()?.code).toBe('portal.last_remaining');
      expect(store.failure()?.code).toContain('.');
    });

    it('reports NO code when the document carries a type this API does not publish', () => {
      // A document may legitimately carry no application code, and the reader answers null rather than a
      // best guess - which is what keeps a lookup from succeeding against a value that means nothing.
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(
        { title: 'Not Found', status: 404, detail: 'No such collection.' },
        { status: 404, statusText: 'Not Found' },
      );

      expect(store.failure()?.code).toBeNull();
      expect(store.failure()?.problem?.status).toBe(404);
      expect(store.failure()?.summary.severity).toBe('warning');
    });

    it('attributes each failure to the command that caused it, so one banner does not swallow another', () => {
      store.loadSettings(0);
      expectRequest('GET', '/api/v1/modules/0/settings').flush(problem('module.not_found', 404, 'Gone.'), {
        status: 404,
        statusText: 'Not Found',
      });
      expect(store.failure()?.operation).toBe('loadSettings');

      store.loadDesktopDefinitions(2);
      expectRequest('GET', '/api/v1/module-definitions/desktop-modules/2').flush(
        problem('module.bundle_not_found', 404, 'Gone.'),
        { status: 404, statusText: 'Not Found' },
      );
      expect(store.failure()?.operation).toBe('loadDesktopDefinitions');
    });

    /**
     * ⚠ THE SETTINGS READ ALSO REPORTS ITSELF INTO A SLOT OF ITS OWN, AND THIS SUITE EXISTS BECAUSE THE
     * SHARED SLOT PROVABLY CANNOT CARRY THAT FACT. The settings screen issues three reads and every
     * command clears the shared slot as it starts, so the last read to finish owns it.
     */
    it('records how the settings read ended in a slot a later failure cannot displace', () => {
      store.loadSettings(0);
      expectRequest('GET', '/api/v1/modules/0/settings').flush(
        problem('module.settings_protected', 403, 'Administrative module settings are protected.'),
        { status: 403, statusText: 'Forbidden' },
      );

      expect(store.settingsFailure()?.operation).toBe('loadSettings');
      expect(store.settingsFailure()?.problem?.status).toBe(403);

      store.loadDefinition(4);
      expectRequest('GET', '/api/v1/module-definitions/4').flush(
        problem('resource.not_found', 404, 'Gone.'),
        { status: 404, statusText: 'Not Found' },
      );

      expect(store.failure()?.operation)
        .withContext('the shared slot behaves exactly as before - it belongs to the last failure')
        .toBe('loadDefinition');
      expect(store.settingsFailure()?.problem?.status)
        .withContext('while the settings refusal is still answerable')
        .toBe(403);
      expect(store.settingsFailure()?.code).toBe('module.settings_protected');
    });

    it('clears the settings slot when that read is retried, and when a bag arrives', () => {
      store.loadSettings(0);
      expectRequest('GET', '/api/v1/modules/0/settings').flush(problem('module.settings_protected', 403, 'No.'), {
        status: 403,
        statusText: 'Forbidden',
      });
      expect(store.settingsFailure()).not.toBeNull();

      // Retrying clears it as the request starts, so a screen never shows a refusal for a read in flight.
      store.loadSettings(0);
      expect(store.settingsFailure())
        .withContext('the slot describes the read now outstanding, not the one before it')
        .toBeNull();

      expectRequest('GET', '/api/v1/modules/0/settings').flush({
        data: { moduleId: 0, tabModuleId: 1, moduleSettings: {}, tabModuleSettings: {} },
      });

      expect(store.settingsFailure())
        .withContext('a bag in hand is the end of the matter')
        .toBeNull();
      expect(store.settings()).not.toBeNull();
    });

    it('discards the settings slot alongside the bag it describes', () => {
      store.loadSettings(0);
      expectRequest('GET', '/api/v1/modules/0/settings').flush(problem('module.settings_protected', 403, 'No.'), {
        status: 403,
        statusText: 'Forbidden',
      });
      expect(store.settingsFailure()).not.toBeNull();

      store.clearSettings();

      expect(store.settingsFailure())
        .withContext('a refusal describes a read of one tenant\'s settings and must not outlive them')
        .toBeNull();
    });

    it('clears a recorded failure explicitly, and again when the next command starts', () => {
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(problem('module.rejected', 400, 'No.'), {
        status: 400,
        statusText: 'Bad Request',
      });
      expect(store.failure()).not.toBeNull();

      store.clearFailure();
      expect(store.failure()).toBeNull();

      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(problem('module.rejected', 400, 'No.'), {
        status: 400,
        statusText: 'Bad Request',
      });
      expect(store.failure()).not.toBeNull();

      // A new command clears the previous refusal before it starts, so a stale banner cannot outlive the
      // request that produced it.
      store.loadModules();
      expect(store.failure()).toBeNull();
      expectRequest('GET', '/api/v1/modules').flush(pagedBody([listRow()]));
      expect(store.failure()).toBeNull();
    });

    it('leaves every in-flight flag down after a refusal, so a screen cannot stay disabled', () => {
      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush('refused', {
        status: 403,
        statusText: 'Forbidden',
      });
      expect(store.exporting()).toBeFalse();

      store.importModule(importRequest());
      expectRequest('POST', '/api/v1/modules/import').flush(problem('module.not_portable', 409, 'No.'), {
        status: 409,
        statusText: 'Conflict',
      });
      expect(store.importing()).toBeFalse();

      store.saveSettings(settingsBag(), 1);
      expectRequest('PUT', '/api/v1/modules/0/settings').flush(problem('module.rejected', 400, 'No.'), {
        status: 400,
        statusText: 'Bad Request',
      });
      expect(store.settingsSaving()).toBeFalse();

      expect(store.busy()).toBeFalse();
    });

    it('refuses a replacement that echoes NO placement back, leaving the held row alone', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5 })]);

      store.updateModule(0, updateRequest(), 5);
      expectRequest('PUT', '/api/v1/modules/0').flush(envelope(null));

      // No second request: the refusal is terminal, and nothing re-reads the listing behind it.
      expect(store.saving()).toBeFalse();
      expect(store.failure()?.operation).toBe('updateModule');
      expect(store.modules().length).toBe(1);
    });

    it('re-reads the listing when a replacement addressed the MODULE rather than one placement', () => {
      // A module whose all-pages flag is set contributes one row per page, so a replacement addressing the
      // module rewrites every occurrence and a single local row replacement cannot describe the outcome.
      loadListWith([
        listRow({ moduleId: 0, tabModuleId: 5, tabId: 0 }),
        listRow({ moduleId: 0, tabModuleId: 6, tabId: 1 }),
      ]);

      store.updateModule(0, updateRequest({ allTabs: true }));
      expectRequest('PUT', '/api/v1/modules/0').flush(
        envelope(detail({ moduleId: 0, tabModuleId: 5, allTabs: true })),
      );

      expectRequest('GET', '/api/v1/modules').flush(
        pagedBody([
          listRow({ moduleId: 0, tabModuleId: 5, allTabs: true }),
          listRow({ moduleId: 0, tabModuleId: 6, tabId: 1, allTabs: true }),
        ]),
      );

      expect(store.modules().every((row) => row.allTabs)).toBeTrue();
    });

    it('replaces exactly ONE row when a placement was addressed, keyed by the PLACEMENT', () => {
      // Keying by the module would collapse the two rows below onto one another, because the module
      // identifier repeats across them while the placement identifier does not.
      loadListWith([
        listRow({ moduleId: 0, tabModuleId: 5, tabId: 0, moduleTitle: 'Before' }),
        listRow({ moduleId: 0, tabModuleId: 6, tabId: 1, moduleTitle: 'Untouched' }),
      ]);

      store.updateModule(0, updateRequest({ moduleTitle: 'After' }), 5);
      expectRequest('PUT', '/api/v1/modules/0').flush(
        envelope(detail({ moduleId: 0, tabModuleId: 5, tabId: 0, moduleTitle: 'After' })),
      );

      // No re-read: one row was corrected locally, which the verification in `afterEach` confirms.
      expect(store.modules().map((row) => row.moduleTitle)).toEqual(['After', 'Untouched']);
      // The read-only catalogue projections survive from the existing row, because the listing computes
      // them by join and a replacement cannot change them.
      expect(store.modules()[0].friendlyName).toBe('Announcements');
      expect(store.modules()[0].version).toBe('01.00.00');
    });

    it('leaves the listing alone when the replaced placement is not on the page being shown', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5 })]);

      store.updateModule(1, updateRequest(), 99);
      expectRequest('PUT', '/api/v1/modules/1').flush(
        envelope(detail({ moduleId: 1, tabModuleId: 99 })),
      );

      // Nothing local to correct and nothing stale to leave behind, so no request follows.
      expect(store.modules().map((row) => row.tabModuleId)).toEqual([5]);
      expect(store.module()?.tabModuleId).toBe(99);
    });
  });

  // PROOF 13 - THE PUBLIC SURFACE IS OBSERVABLE BUT NOT WRITABLE, AND THE BOUNDARY HOLDS
  describe('the public surface and its boundary', () => {
    it('exposes writable state ONLY as readonly signals', () => {
      expect('set' in store.page).toBeFalse();
      expect('update' in store.page).toBeFalse();
      expect('set' in store.module).toBeFalse();
      expect('update' in store.module).toBeFalse();
      expect('set' in store.tabs).toBeFalse();
      expect('update' in store.tabs).toBeFalse();
      expect('set' in store.exportedContent).toBeFalse();
      expect('update' in store.exportedContent).toBeFalse();
      expect('set' in store.failure).toBeFalse();
      expect('update' in store.failure).toBeFalse();
      expect('set' in store.settings).toBeFalse();
      expect('set' in store.definitions).toBeFalse();
      expect('set' in store.desktopDefinitions).toBeFalse();
      expect('set' in store.definition).toBeFalse();
      expect('set' in store.query).toBeFalse();
      expect('set' in store.filter).toBeFalse();
      expect('set' in store.selectedModuleId).toBeFalse();
      expect('set' in store.selectedTabId).toBeFalse();
      expect('set' in store.tabPortalId).toBeFalse();
      expect('set' in store.importCompleted).toBeFalse();
    });

    it('exposes every in-flight flag as a readonly signal too', () => {
      expect('set' in store.listLoading).toBeFalse();
      expect('set' in store.moduleLoading).toBeFalse();
      expect('set' in store.saving).toBeFalse();
      expect('set' in store.settingsLoading).toBeFalse();
      expect('set' in store.settingsSaving).toBeFalse();
      expect('set' in store.definitionsLoading).toBeFalse();
      expect('set' in store.tabsLoading).toBeFalse();
      expect('set' in store.exporting).toBeFalse();
      expect('set' in store.importing).toBeFalse();
    });

    it('exposes every derived view as a computed signal, which is likewise not writable', () => {
      expect('set' in store.modules).toBeFalse();
      expect('update' in store.modules).toBeFalse();
      expect('set' in store.meta).toBeFalse();
      expect('set' in store.totalCount).toBeFalse();
      expect('set' in store.hasModules).toBeFalse();
      expect('set' in store.busy).toBeFalse();
      expect('set' in store.tabHierarchy).toBeFalse();
      expect('set' in store.tabTree).toBeFalse();
      expect('set' in store.orphanTabs).toBeFalse();
      expect('set' in store.portableDefinitions).toBeFalse();
      expect('set' in store.selectedTab).toBeFalse();
    });

    it('cannot be corrupted by a consumer mutating a copy of a collection it was handed', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);

      const rows: readonly ModuleListItem[] = store.modules();
      const copied: ModuleListItem[] = [...rows];
      copied.push(listRow({ moduleId: 99, tabModuleId: 99 }));
      copied.length = 0;

      expect(store.modules().length).toBe(1);
      expect(store.modules()[0].moduleId).toBe(0);

      const definitions: ModuleDefinition[] = [...store.definitions()];
      definitions.push(definition({ moduleDefId: 99 }));
      expect(store.definitions().length).toBe(0);
    });

    it('replaces state IMMUTABLY, so a consumer using push-based change detection re-renders', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5, moduleTitle: 'Before' })]);

      const before: readonly ModuleListItem[] = store.modules();

      store.updateModule(0, updateRequest({ moduleTitle: 'After' }), 5);
      expectRequest('PUT', '/api/v1/modules/0').flush(
        envelope(detail({ moduleId: 0, tabModuleId: 5, moduleTitle: 'After' })),
      );

      const after: readonly ModuleListItem[] = store.modules();

      // A new array and a new row, so an identity comparison sees the change. Patching the existing row in
      // place would leave both references equal and a push-based consumer would never re-render.
      expect(after).not.toBe(before);
      expect(after[0]).not.toBe(before[0]);
      expect(before[0].moduleTitle).toBe('Before');
      expect(after[0].moduleTitle).toBe('After');
    });

    it('exposes NO method that decides a permission - the server is authoritative', () => {
      // Two closed vocabularies exist and they are not interchangeable: the persisted permission keys on
      // one side and the policy names the server enforces on the other. Neither decides anything on the
      // client, there is no deny prefix in this generation of the schema, and a refusal arrives as a `403`.
      expect('hasPermission' in store).toBeFalse();
      expect('canEdit' in store).toBeFalse();
      expect('canView' in store).toBeFalse();
      expect('isInRole' in store).toBeFalse();
      expect('isAuthorised' in store).toBeFalse();
      expect('isAuthorized' in store).toBeFalse();
      expect('permissionKeys' in store).toBeFalse();
    });

    it('exposes NO formatting, validation or pager-visibility decision', () => {
      expect('format' in store).toBeFalse();
      expect('formatDate' in store).toBeFalse();
      expect('displayName' in store).toBeFalse();
      expect('validate' in store).toBeFalse();
      expect('showPager' in store).toBeFalse();
      expect('pagerVisible' in store).toBeFalse();
    });

    it('injects no other store, and addresses no other aggregate endpoint', () => {
      loadListWith([listRow()]);
      loadTabsWith(0, [tabRow({ tabId: 0, parentId: -1 })]);

      httpMock.expectNone((candidate) => candidate.url === '/api/v1/portals');
      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/users'));
      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/roles'));
      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/role-groups'));
      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/permissions'));
      httpMock.expectNone((candidate) => candidate.url.startsWith('/api/v1/auth'));

      expect('portalStore' in store).toBeFalse();
      expect('userStore' in store).toBeFalse();
      expect('roleStore' in store).toBeFalse();
      expect('authStore' in store).toBeFalse();
    });

    it('activates no module-supplied controller, because that resolution moved to the server', () => {
      // The legacy resolved a class name held on the module row through reflection and invoked its
      // portability behaviour in-process; the target resolves it server-side through dependency injection
      // over a closed set, and none of those legacy sites was ever component-object interop.
      expect('businessControllerClass' in store).toBeFalse();
      expect('createObject' in store).toBeFalse();
      expect('resolveController' in store).toBeFalse();
      expect('invokeModule' in store).toBeFalse();
    });

    it('caches nothing, so every read issues a request', () => {
      // The legacy module controller was the largest single consumer of the legacy static cache, at
      // twenty-one call sites, with a scaled-expiry set and a coarse portal-wide clear. Caching in the
      // target is server-side behind an interface.
      store.loadDefinitions();
      expectRequest('GET', '/api/v1/module-definitions').flush(envelope([definition({ moduleDefId: 4 })]));
      expect(store.definitions().length).toBe(1);

      store.loadDefinitions();
      const second = expectRequest('GET', '/api/v1/module-definitions');
      second.flush(envelope([definition({ moduleDefId: 4 }), definition({ moduleDefId: 5 })]));

      expect(store.definitions().length).toBe(2);
      expect('cache' in store).toBeFalse();
      expect('clearCache' in store).toBeFalse();
    });

    it('clears the loaded module and the selection that addressed it', () => {
      store.loadModule(0, 5);
      expectRequest('GET', '/api/v1/modules/0').flush(envelope(detail({ moduleId: 0, tabModuleId: 5 })));

      expect(store.module()?.moduleId).toBe(0);
      expect(store.selectedModuleId()).toBe(0);

      store.clearModule();

      expect(store.module()).toBeNull();
      expect(store.selectedModuleId()).toBeUndefined();
    });
  });
  // THE PICKER READS EVERY PAGE, BECAUSE IT OFFERS NO PAGER
  // The consumer is what makes it serious.
  describe('the picker walks every page', () => {
    /** Consumes one page request of the walk, asserted by index and by the width it asks for. */
    function expectChoicePage(pageIndex: number): TestRequest {
      const call = httpMock.expectOne(
        (candidate) =>
          isListingRead(candidate) &&
          sentFilters(candidate).get('pageIndex') === String(pageIndex),
        `the picker's page ${pageIndex}`,
      );

      // The widest page the validator admits, every time. Narrowing a later request would multiply the
      // round trips for no benefit; widening one would be refused at field level by the server.
      expect(sentFilters(call).get('pageSize')).toBe(String(MAX_PAGE_SIZE));

      return call;
    }

    it('asks for ONE page when the first page is short, which is the ordinary case', () => {
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(3), 3));

      // A short page is the last page by definition, so no second request is made. The common case must not
      // have been made more expensive by the walk: a tenant whose modules fit on one page still costs
      // exactly one round trip.
      httpMock.expectNone(
        (candidate) => isListingRead(candidate),
      );

      expect(store.choices().length).toBe(3);
      expect(store.choicesTotalCount()).toBe(3);
      expect(store.choicesComplete()).toBeTrue();
      expect(store.choicesLoading()).toBeFalse();
    });

    it('walks on past a FULL page and joins every page into one set', () => {
      // ⚠ THE DEFECT, EXPRESSED AS A TEST. Before the walk this store published the first page's rows and
      // stopped, so the assertion below would have found MAX_PAGE_SIZE choices and reported success.
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(MAX_PAGE_SIZE, 1), MAX_PAGE_SIZE + 7));
      expectChoicePage(1).flush(walkPage(distinctRows(7, MAX_PAGE_SIZE + 1), MAX_PAGE_SIZE + 7, 1));

      expect(store.choices().length).toBe(MAX_PAGE_SIZE + 7);

      // Joined in REQUEST ORDER, so the assembled order is the server's rather than a second, disagreeing
      // client-side opinion about an ordering the API already owns.
      expect(store.choices()[0].moduleId).toBe(1);
      expect(store.choices()[MAX_PAGE_SIZE].moduleId).toBe(MAX_PAGE_SIZE + 1);
      expect(store.choicesComplete()).toBeTrue();
      expect(store.choicesLoading()).toBeFalse();
    });

    it('crosses THREE pages, so the walk is a loop rather than one extra request', () => {
      store.loadChoices();

      const total = MAX_PAGE_SIZE * 2 + 1;

      expectChoicePage(0).flush(walkPage(distinctRows(MAX_PAGE_SIZE, 1), total));
      expectChoicePage(1).flush(walkPage(distinctRows(MAX_PAGE_SIZE, MAX_PAGE_SIZE + 1), total, 1));
      expectChoicePage(2).flush(walkPage(distinctRows(1, total), total, 2));

      expect(store.choices().length).toBe(total);
      expect(store.choicesComplete()).toBeTrue();
    });

    it("stops on the SERVER'S OWN TOTAL when a final page is padded to full width", () => {
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(MAX_PAGE_SIZE, 1), MAX_PAGE_SIZE));

      httpMock.expectNone(
        (candidate) => isListingRead(candidate),
      );

      expect(store.choices().length).toBe(MAX_PAGE_SIZE);
      expect(store.choicesComplete()).toBeTrue();
    });

    it("publishes the SERVER'S total and not the row count", () => {
      // ⚠ THE TOTAL IS REPORTED, NEVER RECOMPUTED, and this is the case that tells the two apart. The
      // server contradicts itself in the one direction that still completes a walk: it supplies THREE
      // placements while claiming there are two.
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(3), 2));

      expect(store.choices().length).toBe(3);
      expect(store.choicesTotalCount())
        .withContext("the server's claim, reported even when the rows contradict it")
        .toBe(2);
      expect(store.choicesTotal())
        .withContext('both published names answer from the one slice')
        .toBe(2);

      // Nothing further is asked: everything the server claimed has been gathered.
      httpMock.expectNone(
        (candidate) => isListingRead(candidate),
      );
    });

    it('sends NO narrowing, ordering or free-text filter on any page of the walk', () => {
      // The picker wants every placement, so it sends the paging pair and nothing else.
      store.setSort('moduleTitle', 'Descending');
      store.setQuery('news');
      store.setIncludeDeleted(true);
      store.setPageSize(50);
      store.setPageIndex(3);

      store.loadChoices();

      const first = expectChoicePage(0);

      expect(sentFilters(first).keys().sort()).toEqual(['pageIndex', 'pageSize']);

      first.flush(walkPage(distinctRows(MAX_PAGE_SIZE, 1), MAX_PAGE_SIZE + 1));

      const second = expectChoicePage(1);

      // Asserted on the SECOND page too: a walk that assembled its first request correctly and then
      // widened later ones would leak the listing's state on every page but the first.
      expect(sentFilters(second).keys().sort()).toEqual(['pageIndex', 'pageSize']);

      second.flush(walkPage(distinctRows(1, MAX_PAGE_SIZE + 1), MAX_PAGE_SIZE + 1, 1));

      // And the listing this store also holds is untouched by the whole walk.
      expect(store.query().pageIndex).toBe(3);
      expect(store.query().pageSize).toBe(50);
      expect(store.modules().length).toBe(0);
    });

    it('reports a mid-walk failure once and stops asking', () => {
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(MAX_PAGE_SIZE, 1), MAX_PAGE_SIZE + 5));
      expectChoicePage(1).flush(problem('server_error', 500, 'The listing could not be read.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });

      httpMock.expectNone(
        (candidate) => isListingRead(candidate),
      );

      expect(store.choices().length).toBe(0);
      expect(store.choicesLoading()).toBeFalse();
      // The picker read's own identity — see the note on the shortfall case above.
      expect(store.failure()?.operation).toBe('loadChoices');
    });

    it('cancels the WHOLE walk from one handle, mid-flight', () => {
      // The walk is one cold observable behind one subscription, so the handle the store keeps releases
      // whichever page is outstanding. Holding a handle per page would leave the walk able to continue
      // requesting after a reset.
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(MAX_PAGE_SIZE, 1), MAX_PAGE_SIZE + 5));

      const second = expectChoicePage(1);

      store.reset();

      expect(second.cancelled)
        .withContext('the page in flight is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.choices().length).toBe(0);
      expect(store.choicesTotalCount())
        .withContext("the previous session's total must not outlive its rows")
        .toBe(0);
      expect(store.choicesLoading()).toBeFalse();
    });

    it('abandons an earlier walk when a second is issued', () => {
      store.loadChoices();

      const first = expectChoicePage(0);

      store.loadChoices();

      const second = expectChoicePage(0);

      expect(first.cancelled).toBeTrue();
      expect(second.cancelled).toBeFalse();

      second.flush(walkPage(distinctRows(2), 2));

      expect(store.choices().length).toBe(2);
    });

    it('publishes an unpaged envelope, so no consumer can build a pager from it', () => {
      // The joined result reports the coordinates the paging contract publishes for an unpaged answer, and
      // this slice deliberately exposes no page index and no page size of its own — the walk has already
      // been past every page there is, so a pager over it could not change anything.
      store.loadChoices();

      expectChoicePage(0).flush(walkPage(distinctRows(4), 4));

      expect('choicesPageIndex' in store).toBeFalse();
      expect('choicesPageSize' in store).toBeFalse();
      expect('setChoicesPageIndex' in store).toBeFalse();

      // The browsable listing's own coordinates remain the only paging state in this store.
      expect(store.meta().totalCount).toBe(0);
    });
  });

  // SESSION ISOLATION AND READ CONCURRENCY
  // Two properties, both of which were absent before: this store had NO whole-store reset and NO request
  // handles at all. What that meant in practice is worth stating, because neither symptom looks like a
  // defect from inside a single screen.
  describe('session isolation and read concurrency', () => {
    it('discards every tenant-scoped slice on reset, exported content included', () => {
      loadListWith([listRow({ moduleId: 4, moduleTitle: 'Announcements' })]);
      loadTabsWith(0, [tabRow({ tabId: 7, tabName: 'Home' })]);

      store.loadDefinitions();
      expectRequest('GET', '/api/v1/module-definitions').flush(envelope([definition()]));

      store.exportModule(4, exportRequest());
      expectRequest('POST', '/api/v1/modules/4/export').flush('<content>exported</content>', {
        status: 200,
        statusText: 'OK',
      });

      expect(store.modules().length).toBe(1);
      expect(store.tabs().length).toBe(1);
      expect(store.definitions().length).toBe(1);
      expect(store.exportedContent()).toBe('<content>exported</content>');

      store.reset();

      expect(store.modules().length).toBe(0);
      expect(store.tabs().length).toBe(0);
      expect(store.definitions().length).toBe(0);
      expect(store.exportedContent())
        .withContext('module content must not outlive the session that read it')
        .toBeNull();
      expect(store.selectedModuleId()).toBeUndefined();
      expect(store.tabPortalId()).toBeUndefined();
      expect(store.failure()).toBeNull();
      expect(store.busy()).toBeFalse();
    });

    it('cancels a read in flight on reset, so its answer cannot repopulate the store', () => {
      store.loadModules();

      const pending = expectRequest('GET', '/api/v1/modules');

      store.reset();

      expect(pending.cancelled)
        .withContext('the request is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.listLoading())
        .withContext('and the store is no longer reporting a read in flight')
        .toBeFalse();
      expect(store.modules().length).toBe(0);
    });

    it('cancels a write in flight on reset, so its callback cannot act for the ended session', () => {
      // A write's callback re-reads the listing and replaces a row. Left listening across a boundary it
      // would do both on behalf of the session that ended.
      store.createModule(createRequest());

      const pending = expectRequest('POST', '/api/v1/modules');

      store.reset();

      expect(pending.cancelled).toBeTrue();
      expect(store.saving()).toBeFalse();
    });

    it('keeps accepting writes after a reset, which a Subscription container would have broken', () => {
      // ⚠ A REGRESSION GUARD FOR A REAL TRAP. An RxJS `Subscription` used as a container is CLOSED once
      // unsubscribed, and anything added afterwards is unsubscribed the instant it is added.
      store.reset();

      store.createModule(createRequest());

      const pending = expectRequest('POST', '/api/v1/modules');

      expect(pending.cancelled)
        .withContext('a write issued after a reset must not be cancelled on arrival')
        .toBeFalse();

      pending.flush(envelope(detail({ moduleId: 9 })), { status: 201, statusText: 'Created' });

      expect(store.selectedModuleId()).toBe(9);
      expect(store.saving()).toBeFalse();
    });

    it('abandons the earlier listing read when a second is issued', () => {
      // ⚠ THE STALE-ANSWER RACE. Both requests answer the same question, so the LAST response to arrive
      // wins — which is not necessarily the one asked for last. Releasing the first handle removes it from
      // the race rather than leaving the outcome to timing.
      store.loadModules();

      const first = expectRequest('GET', '/api/v1/modules');

      store.loadModules();

      const second = expectRequest('GET', '/api/v1/modules');

      expect(first.cancelled).toBeTrue();
      expect(second.cancelled).toBeFalse();

      second.flush(pagedBody([listRow({ moduleId: 2, moduleTitle: 'Second' })], 0, 10));

      expect(store.modules().length).toBe(1);
      expect(store.modules()[0].moduleTitle).toBe('Second');
    });

    it('abandons the earlier hierarchy read when a second is issued for another portal', () => {
      // The page tree is the read most likely to be issued twice in quick succession, because changing the
      // portal selector re-issues it — and the two answers describe DIFFERENT portals, so a stale winner
      // shows one tenant's pages under another tenant's name.
      store.loadTabs(0);

      const first = expectRequest('GET', '/api/v1/portals/0/tabs');

      store.loadTabs(4);

      const second = expectRequest('GET', '/api/v1/portals/4/tabs');

      expect(first.cancelled).toBeTrue();

      second.flush(tabPage([tabRow({ tabId: 11, tabName: 'Second portal page' })]));

      expect(store.tabPortalId()).toBe(4);
      expect(store.tabs().length).toBe(1);
    });

    it('abandons the earlier single-module read when a second is issued', () => {
      store.loadModule(1);

      const first = expectRequest('GET', '/api/v1/modules/1');

      store.loadModule(2);

      const second = expectRequest('GET', '/api/v1/modules/2');

      expect(first.cancelled).toBeTrue();

      second.flush(envelope(detail({ moduleId: 2 })));

      expect(store.module()?.moduleId).toBe(2);
      expect(store.moduleLoading()).toBeFalse();
    });

    it('does NOT abandon the definition CATALOGUE when a single definition is read', () => {
      store.loadDefinitions();

      const catalogue = expectRequest('GET', '/api/v1/module-definitions');

      store.loadDefinition(4);

      const one = expectRequest('GET', '/api/v1/module-definitions/4');

      expect(catalogue.cancelled)
        .withContext('reading one definition must not abort the catalogue read')
        .toBeFalse();
      expect(one.cancelled).toBeFalse();

      one.flush(envelope(definition({ moduleDefId: 4 })));
      catalogue.flush(envelope([definition({ moduleDefId: 4 }), definition({ moduleDefId: 5 })]));

      expect(store.definition()?.moduleDefId).toBe(4);
      expect(store.definitions().length)
        .withContext('the catalogue answered, because it was never abandoned')
        .toBe(2);
    });

    it("does NOT abandon the definition CATALOGUE when a bundle's definitions are read", () => {
      // The same defect, on the other of the two commands that carried it. The bundle's definitions are
      // a third slice with a third handle.
      store.loadDefinitions();

      const catalogue = expectRequest('GET', '/api/v1/module-definitions');

      store.loadDesktopDefinitions(2);

      const bundle = expectRequest('GET', '/api/v1/module-definitions/desktop-modules/2');

      expect(catalogue.cancelled)
        .withContext("reading a bundle's definitions must not abort the catalogue read")
        .toBeFalse();

      bundle.flush(envelope([definition({ moduleDefId: 7 })]));
      catalogue.flush(envelope([definition({ moduleDefId: 7 })]));

      expect(store.desktopDefinitions().length).toBe(1);
      expect(store.definitions().length).toBe(1);
    });

    it('abandons the earlier SINGLE-definition read when a second is issued', () => {
      // Own-slice supersession still applies, which is the other half of the same rule: each read
      // cancels its own predecessor and nothing else.
      store.loadDefinition(4);

      const first = expectRequest('GET', '/api/v1/module-definitions/4');

      store.loadDefinition(5);

      const second = expectRequest('GET', '/api/v1/module-definitions/5');

      expect(first.cancelled).toBeTrue();
      expect(second.cancelled).toBeFalse();

      second.flush(envelope(definition({ moduleDefId: 5 })));

      expect(store.definition()?.moduleDefId).toBe(5);
    });

    it('abandons the earlier BUNDLE-definitions read when a second is issued', () => {
      store.loadDesktopDefinitions(2);

      const first = expectRequest('GET', '/api/v1/module-definitions/desktop-modules/2');

      store.loadDesktopDefinitions(3);

      const second = expectRequest('GET', '/api/v1/module-definitions/desktop-modules/3');

      expect(first.cancelled).toBeTrue();

      second.flush(envelope([definition({ moduleDefId: 9 })]));

      expect(store.desktopDefinitions().length).toBe(1);
      expect(store.desktopDefinitions()[0].moduleDefId).toBe(9);
    });

    it('runs a listing, a hierarchy, a module, a settings and a definition read side by side', () => {
      // ⚠ THE WHOLE INVARIANT IN ONE CASE. Five different reads, five different handles, all in the air at
      // once: none may abort another, because a screen composed of several panes dispatches exactly this
      // way and each pane owns its own slice.
      store.loadModules();
      const listing = expectRequest('GET', '/api/v1/modules');

      store.loadTabs(0);
      const hierarchy = expectRequest('GET', '/api/v1/portals/0/tabs');

      store.loadModule(1);
      const module = expectRequest('GET', '/api/v1/modules/1');

      store.loadSettings(1);
      const settings = expectRequest('GET', '/api/v1/modules/1/settings');

      store.loadDefinitions();
      const catalogue = expectRequest('GET', '/api/v1/module-definitions');

      for (const request of [listing, hierarchy, module, settings, catalogue]) {
        expect(request.cancelled)
          .withContext(`${request.request.urlWithParams} was aborted by a read of another slice`)
          .toBeFalse();
      }

      listing.flush(pagedBody([listRow({ moduleId: 1 })], 0, 10));
      hierarchy.flush(tabPage([tabRow({ tabId: 11 })]));
      module.flush(envelope(detail({ moduleId: 1 })));
      settings.flush(envelope(settingsBag()));
      catalogue.flush(envelope([definition({ moduleDefId: 4 })]));

      expect(store.modules().length).toBe(1);
      expect(store.tabs().length).toBe(1);
      expect(store.module()?.moduleId).toBe(1);
      expect(store.settings()).not.toBeNull();
      expect(store.definitions().length).toBe(1);
      expect(store.busy())
        .withContext('every read settled, so nothing is still reported as in flight')
        .toBeFalse();
    });

    it('does NOT abandon a write when a second write is issued', () => {
      // The asymmetry with reads, asserted rather than assumed. Two writes are two distinct instructions,
      // so abandoning the first because a second was issued would drop an outcome the server may already
      // have committed — leaving the operator with no report of a change that happened.
      store.createModule(createRequest());

      const first = expectRequest('POST', '/api/v1/modules');

      store.createModule(createRequest({ moduleTitle: 'Another' }));

      const both = httpMock.match(
        (candidate) => candidate.method === 'POST' && candidate.url === '/api/v1/modules',
      );

      expect(first.cancelled)
        .withContext('a write is never superseded')
        .toBeFalse();
      expect(both.length).toBe(1);

      for (const request of [first, ...both]) {
        request.flush(envelope(detail()), { status: 201, statusText: 'Created' });
      }

      expect(
        httpMock.match(
          (candidate) => isListingRead(candidate),
        ),
      )
        .withContext('a create asks for no listing read; the listing reads itself on entry')
        .toHaveSize(0);
    });
  });

  // PROOF - REQUEST-HANDLE OWNERSHIP, PICKER LIFETIME AND THE UNREACHABLE-SERVER CASE

  describe('request-handle ownership', () => {
    it('does not cancel the definition catalogue when one definition is read', () => {
      store.loadDefinitions();

      const catalogue = expectRequest('GET', '/api/v1/module-definitions');

      store.loadDefinition(3);

      const single = expectRequest('GET', '/api/v1/module-definitions/3');

      expect(catalogue.cancelled)
        .withContext('reading one definition must not abandon the catalogue read')
        .toBeFalse();

      single.flush(envelope(definition({ moduleDefId: 3 })));
      catalogue.flush(envelope([definition({ moduleDefId: 9 })]));

      expect(store.definitions().length).toBe(1);
      expect(store.definition()?.moduleDefId).toBe(3);
    });

    it('does not cancel the definition catalogue when a bundle\'s definitions are read', () => {
      store.loadDefinitions();

      const catalogue = expectRequest('GET', '/api/v1/module-definitions');

      store.loadDesktopDefinitions(5);

      const bundle = expectRequest('GET', '/api/v1/module-definitions/desktop-modules/5');

      expect(catalogue.cancelled)
        .withContext('reading one bundle must not abandon the catalogue read')
        .toBeFalse();

      bundle.flush(envelope([definition({ moduleDefId: 5 })]));
      catalogue.flush(envelope([definition({ moduleDefId: 9 })]));

      expect(store.desktopDefinitions().length).toBe(1);
      expect(store.definitions().length).toBe(1);
    });

    it('does not cancel the browsable listing when the picker choices are read', () => {
      // The complement on the listing side: the two reads address one endpoint but own separate
      // handles, so opening a picker must not abandon a grid read.
      store.loadModules();

      const grid = expectRequest('GET', '/api/v1/modules');

      store.loadChoices();

      const both = httpMock.match(
        (candidate) => isListingRead(candidate),
      );

      expect(grid.cancelled).toBeFalse();

      for (const request of both) {
        if (!request.cancelled) {
          request.flush(pagedBody([], 0, 10));
        }
      }
    });

    it('supersedes only its own slice when the same read is restarted', () => {
      // The invariant's other half: one cancellation per method, and it must still happen.
      store.loadDefinitions();

      const first = expectRequest('GET', '/api/v1/module-definitions');

      store.loadDefinitions();

      const second = expectRequest('GET', '/api/v1/module-definitions');

      expect(first.cancelled).toBeTrue();

      second.flush(envelope([definition()]));
    });
  });

  describe('the picker-choice read', () => {
    it('records its failure against its own command rather than the listing', () => {
      store.loadChoices();

      expectRequest('GET', '/api/v1/modules').flush(
        { title: 'Forbidden', status: 403 },
        { status: 403, statusText: 'Forbidden' },
      );

      expect(store.failure()?.operation).toBe('loadChoices');
    });

    it('releases the read and lowers its busy term when the screen lets it go', () => {
      // The lease. Without it the store reported itself busy on account of a destroyed component, so
      // whatever screen replaced it had its affordances disabled for a read nobody was waiting on.
      store.loadChoices();

      const pending = expectRequest('GET', '/api/v1/modules');

      expect(store.choicesLoading()).toBeTrue();
      expect(store.busy()).toBeTrue();

      store.cancelChoices();

      expect(pending.cancelled)
        .withContext('the request is abandoned, not merely ignored')
        .toBeTrue();
      expect(store.choicesLoading()).toBeFalse();
      expect(store.busy()).toBeFalse();
    });

    it('leaves a sibling read untouched when the picker releases its own', () => {
      store.loadDefinitions();

      const catalogue = expectRequest('GET', '/api/v1/module-definitions');

      store.loadChoices();

      const choices = httpMock.match(
        (candidate) => isListingRead(candidate),
      );

      store.cancelChoices();

      expect(catalogue.cancelled)
        .withContext('releasing the picker must not release a sibling slice')
        .toBeFalse();

      catalogue.flush(envelope([definition()]));

      for (const request of choices) {
        if (!request.cancelled) {
          request.flush(pagedBody([], 0, 10));
        }
      }
    });

    it('keeps the choices it already holds, because they are a lookup and not a selection', () => {
      store.loadChoices();
      expectRequest('GET', '/api/v1/modules').flush(
        pagedBody([listRow({ moduleId: 0, moduleTitle: 'Announcements' })], 0, 200),
      );

      store.cancelChoices();

      expect(store.choices().length).toBe(1);
    });

    it('is idempotent with nothing outstanding', () => {
      expect(() => {
        store.cancelChoices();
        store.cancelChoices();
      }).not.toThrow();
      expect(store.choicesLoading()).toBeFalse();
    });
  });

  describe('an unreachable server', () => {
    it('publishes the transport status alone rather than the progress event in the body slot', () => {
      // ⚠ THE DEFECT: the body was inspected first, and when no response arrives the framework puts a DOM
      // `ProgressEvent` in the body slot.
      store.loadModules();

      expectRequest('GET', '/api/v1/modules').error(new ProgressEvent('error'), {
        status: 0,
        statusText: 'Unknown Error',
      });

      const failure = store.failure();

      expect(failure?.operation).toBe('listModules');

      expect(failure?.problem?.status).toBe(0);
      expect(failure?.problem?.title)
        .withContext('the banner has a title to render')
        .toBe('Network error');
      expect(failure?.problem?.detail)
        .withContext('and a sentence that says what happened')
        .toBe('The server could not be reached. Check your connection and try again.');

      expect(failure?.problem?.type)
        .withContext('a progress event type must never be published as a problem type')
        .toBe('about:blank');
      expect(failure?.code)
        .withContext('an unreachable server publishes no application failure code')
        .toBeNull();
    });

    it('still resolves a real problem document when the server did answer', () => {
      // The complement: the status-first ordering must not shadow a genuine body.
      store.loadModules();

      expectRequest('GET', '/api/v1/modules').flush(
        { type: 'urn:dnnmigration:error:module.not_portable', title: 'Refused', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );

      expect(store.failure()?.problem?.status).toBe(409);
      expect(store.failure()?.code).toBe('module.not_portable');
    });
  });

  // A RESPONSE THIS CLIENT CANNOT READ IS ITS OWN CLASS OF FAILURE
  // Not a refusal, not a fault and NOT a request that never arrived - the three the wording used to
  // collapse it into. It has no transport status and no body, which is exactly why it used to record no
  // problem document at all.
  describe('a response the client cannot read', () => {
    /** Answers the listing with a body whose shape is wrong in a way no code table can tolerate. */
    function answerWithDrift(): void {
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush({
        items: [{ ...listRow(), tabModuleId: 'one' }],
        meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
      });
    }

    it('records a well-formed document instead of nothing, so the banner has something to render', () => {
      answerWithDrift();

      const failure = store.failure();

      expect(failure?.operation).toBe('listModules');
      expect(failure?.problem).withContext('never null again').not.toBeNull();
      expect(failure?.problem?.title).toBe('Unexpected response');
      expect((failure?.problem?.detail ?? '').length).toBeGreaterThan(0);
    });

    it('does NOT word itself as an unreachable server, because the server answered', () => {
      answerWithDrift();

      expect(store.failure()?.problem?.detail)
        .withContext('the connectivity sentence must keep meaning connectivity')
        .not.toContain('could not be reached');
      expect(store.failure()?.problem?.status)
        .withContext('and no transport status describes this')
        .toBeUndefined();
    });

    it('discards the page rather than leaving the previous one on screen', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })], 0, 10);

      expect(store.modules()).toHaveSize(1);

      answerWithDrift();

      expect(store.modules()).withContext('no stale rows survive a failed read').toHaveSize(0);
      expect(store.meta().totalCount).withContext('and no stale total either').toBe(0);
      expect(store.listFailed())
        .withContext('so a consumer can tell an empty listing from an unread one')
        .toBeTrue();
    });

    it('marks a failed CHOICE read, so an empty picker is never read as "there are none"', () => {
      store.loadChoices();
      expectRequest('GET', '/api/v1/modules').flush({
        items: [{ ...listRow(), tabModuleId: 'one' }],
        meta: { totalCount: 1, pageIndex: 0, pageSize: 200, totalPages: 1 },
      });

      expect(store.choices()).toHaveSize(0);
      expect(store.choicesFailed()).toBeTrue();
      expect(store.listFailed())
        .withContext('and the two slices are reported separately')
        .toBeFalse();
    });
  });
  // ---------------------------------------------------------------------------------------------------
  // THE SETTLED LATCH — "NOT ASKED YET" IS NOT "ASKED AND EMPTY"
  // ---------------------------------------------------------------------------------------------------

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. An un-asked listing and a listing that matched nothing are
  // both an empty page with no request in flight, so a grid reading only the rows and the in-flight flag
  // painted "Nothing to Display" over a listing nobody had read yet.
  describe('the settled latch', () => {
    it('is DOWN on a fresh store and nothing is in flight, which is what made the two states identical', () => {
      expect(store.listSettled()).toBeFalse();
      expect(store.modules()).toEqual([]);
      expect(store.listLoading()).toBeFalse();
    });

    it('stays DOWN while the first read is outstanding and rises when it answers', () => {
      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(store.listSettled()).toBeFalse();

      call.flush(pagedBody([listRow()], 0, 10));

      expect(store.listSettled()).toBeTrue();
    });

    it('rises on a FAILED read too, so a waiting indicator cannot stand over a reportable failure', () => {
      store.loadModules();
      expectRequest('GET', '/api/v1/modules').flush(
        { title: 'Server Error', status: 500 },
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(store.listSettled()).toBeTrue();
      expect(store.failure()).not.toBeNull();
    });

    it('goes back DOWN on reset, because the page it spoke for is discarded with the session', () => {
      loadListWith([listRow()]);
      expect(store.listSettled()).toBeTrue();

      store.reset();

      expect(store.listSettled()).toBeFalse();
      expect(store.modules()).toEqual([]);
    });
  });

  // -----------------------------------------------------------------------------------------------------
  // THE GRANT GRID
  // -----------------------------------------------------------------------------------------------------
  // The slice behind the module permission grid the first port of the settings screen did not have.
  describe('the grant grid', () => {
    /** The grid address for module 0 - zero being a real module, since the column is IDENTITY (0, 1). */
    const GRID_URL = '/api/v1/modules/0/permissions';

    /**
     * A minimal grid payload.
     *
     * @param moduleId Which module the grid describes.
     * @returns The payload body.
     */
    function gridBody(moduleId: number): ApiResponse<unknown> {
      return {
        data: {
          moduleId,
          inheritViewPermissions: false,
          inheritedPermissionKey: 'VIEW',
          definitions: [{ permissionId: 1, permissionKey: 'VIEW', permissionName: 'View Module' }],
          roles: [
            {
              roleId: 2,
              roleName: 'Subscribers',
              isAdministrator: false,
              isPseudoRole: false,
              cells: [
                { permissionId: 1, permissionKey: 'VIEW', allowAccess: true, editable: true },
              ],
            },
          ],
          users: [],
        },
        meta: null,
      } as unknown as ApiResponse<unknown>;
    }

    it('holds the grid a successful read answered with', () => {
      store.loadPermissions(0);

      expect(store.permissionsLoading()).toBeTrue();

      expectRequest('GET', GRID_URL).flush(gridBody(0));

      expect(store.permissionsLoading()).toBeFalse();
      expect(store.permissionGrid()?.moduleId).toBe(0);
      expect(store.permissionGrid()?.roles[0]?.roleName).toBe('Subscribers');
      expect(store.permissionsFailure()).toBeNull();
    });

    it('clears the previous module grid as a new read begins', () => {
      store.loadPermissions(0);
      expectRequest('GET', GRID_URL).flush(gridBody(0));

      expect(store.permissionGrid()).not.toBeNull();

      store.loadPermissions(1);

      // A grid left standing would be rendered as the NEW module's grants, and every identifier in it is
      // one the screen's REPLACE would then withdraw.
      expect(store.permissionGrid()).toBeNull();

      expectRequest('GET', '/api/v1/modules/1/permissions').flush(gridBody(1));

      expect(store.permissionGrid()?.moduleId).toBe(1);
    });

    it('keeps a failed READ out of the shared failure slot', () => {
      store.loadPermissions(0);
      expectRequest('GET', GRID_URL).flush(
        { type: 'about:blank', title: 'Server Error', status: 500, detail: 'Unavailable.' },
        { status: 500, statusText: 'Internal Server Error' },
      );

      // ⚠ THE WHOLE POINT OF THE DEDICATED SLOT. The grid is advisory to the screen that shows it, and the
      // shared slot is what a submission's conclusion inspects to decide whether a WRITE was refused - so a
      // failed read landing there would make the next successful save report itself as failed.
      expect(store.permissionsFailure()?.problem.status).toBe(500);
      expect(store.permissionsFailure()?.operation).toBe('loadPermissions');
      expect(store.failure()).withContext('the page banner is left alone').toBeNull();
      expect(store.permissionsLoading()).toBeFalse();
    });

    it('re-reads the grid after a successful replacement, because the write answers 204', () => {
      store.savePermissions(0, { inheritViewPermissions: false, grants: [] });

      expect(store.permissionsSaving()).toBeTrue();

      expectRequest('PUT', GRID_URL).flush(null, { status: 204, statusText: 'No Content' });

      expect(store.permissionsSaving()).toBeFalse();

      // The server withholds view grants while inheritance is on, so what it stored is knowably not always
      // what was submitted.
      expectRequest('GET', GRID_URL).flush(gridBody(0));

      expect(store.permissionGrid()?.moduleId).toBe(0);
    });

    it('records a refused WRITE in the shared slot, because an operator must be told', () => {
      store.savePermissions(0, { inheritViewPermissions: false, grants: [] });
      expectRequest('PUT', GRID_URL).flush(
        { type: 'about:blank', title: 'Forbidden', status: 403, detail: 'Not permitted.' },
        { status: 403, statusText: 'Forbidden' },
      );

      expect(store.permissionsSaving()).toBeFalse();
      expect(store.failure()?.operation).toBe('savePermissions');
      expect(store.failure()?.problem.status).toBe(403);
    });

    it('discards the grid on reset, because it names roles and accounts of the tenant being left', () => {
      store.loadPermissions(0);
      expectRequest('GET', GRID_URL).flush(gridBody(0));

      store.reset();

      expect(store.permissionGrid()).toBeNull();
      expect(store.permissionsFailure()).toBeNull();
      expect(store.permissionsLoading()).toBeFalse();
      expect(store.permissionsSaving()).toBeFalse();
    });
  });

});
