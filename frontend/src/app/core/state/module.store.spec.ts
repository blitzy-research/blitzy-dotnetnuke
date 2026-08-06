import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ModuleVisibility } from '../models/module.model';
import { ModuleStore } from './module.store';

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

/**
 * Specification for {@link ModuleStore} - the module-administration state slice.
 *
 * The legacy tree contains NO automated test of any kind, so nothing in this file was ported. Every
 * assertion below was authored either from the destination contracts - read out of `module.store.ts`,
 * `module.model.ts`, `tab.model.ts`, `module.service.ts`, `tab.service.ts`, `paged-result.model.ts` and
 * `problem-details.model.ts` rather than from a summary of them - or from the measured legacy behaviour
 * cited inline, and every legacy line reference in this file was opened and read rather than copied
 * forward.
 *
 * No project rule document governs this work: the rules review returned the single line
 * `No user rules provided.` on every call, including calls whose range began past the first line, so
 * there is no document body left to page through. The enterprise baseline in the migration plan governs
 * instead, and it is what the boundary section below states.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHAT THIS FILE IS FOR
 * ---------------------------------------------------------------------------------------------------
 * The store under test is the ONLY place in the application where four things happen, and each of them
 * admits defects that no compiler, linter or type check can see:
 *
 * 1. REQUESTS ARE SEQUENCED. The two transports it drives are each "one method, one endpoint, one
 *    request, no decision", so ordering two calls - pages before a listing, a re-read after a removal -
 *    is this file's subject. A missing follow-up read and an unasked-for extra request are both silent.
 * 2. THE PAGE HIERARCHY IS DERIVED. There is no tree endpoint and no tree type in the models layer, and
 *    `tab.service.ts` states in terms that folding a flat list into a hierarchy "belongs to a signal
 *    store or to the component that renders the indentation". This specification is the only proof that
 *    derivation is correct.
 * 3. SENTINEL VALUES SURVIVE. `0`, `-1`, `''` and `false` are all real data in this schema, and a
 *    truthiness test anywhere in the slice corrupts state behind a perfectly successful status code.
 * 4. STATE IS EXPOSED WITHOUT BEING WRITABLE. Every public projection is a readonly signal, which is a
 *    claim that is easy to make and easy to break by accident.
 *
 * Three mechanisms carry the weight, and each catches a different class of mistake:
 *
 * - `httpMock.verify()` runs after EVERY specification. It fails on any request no expectation consumed,
 *   which turns "the removal re-read the listing and nothing else happened" from a review comment into a
 *   test result. It is also how a stray page mutation or a duplicated read would be caught.
 * - PATHS ARE ASSERTED AS LITERAL STRINGS. Building an expected path from the same route helper the
 *   transport uses would assert nothing at all - the two sides would agree by construction and a wrong
 *   template would pass. The literals below are the independent statement of where each call goes.
 * - FIXTURES ARE TYPED AS THE REAL CONTRACTS. A partial fixture would let a required member be dropped
 *   without any specification noticing, and a misspelled member would resolve to `undefined` at runtime
 *   with no compile error.
 *
 * ---------------------------------------------------------------------------------------------------
 * PATHS ARE RELATIVE, AND THAT IS A PROPERTY OF THE TEST TARGET
 * ---------------------------------------------------------------------------------------------------
 * `angular.json` declares its `fileReplacements` in the direction opposite to the usual Angular
 * scaffold: the `production` configuration substitutes nothing and only `development` swaps the
 * environment module out, so `environment.ts` IS the production module and it carries a RELATIVE base
 * path because one reverse proxy serves the application and the API from a single origin. The `test`
 * target declares no substitution at all, so every specification here compiles against that relative
 * base. Asserting an absolute origin would be asserting against a bundle that is never built under
 * test: it would pass, and it would describe nothing.
 *
 * The environment module is deliberately NOT imported. Reading the base from it and concatenating the
 * rest would reproduce the failure the literal-path rule exists to prevent - the assertion would follow
 * the configuration wherever it went instead of pinning it.
 *
 * ---------------------------------------------------------------------------------------------------
 * THE HARNESS, AND WHY IT NEEDS NEITHER A SPY NOR A FAKE CLOCK
 * ---------------------------------------------------------------------------------------------------
 * Both transports run for real against the testing backend. That is a deliberate choice over spying on
 * them: it proves the verb, the exact path, which values became query parameters, which values stayed in
 * the body and how the response was folded into state, all in one pass. The request BODY matters
 * uniquely here - one specification exists to prove a body member of exactly minus one reaches the wire -
 * and a spy would have nothing to say about it.
 *
 * Everything the testing backend does is synchronous, so by the time a flush returns the signals are
 * already settled and no specification needs to be asynchronous. The store declares no `effect`, which
 * its own header states as a design decision, so nothing here needs to flush effects; request order is a
 * property of the command methods rather than of change detection. No real timer, no clock reading, no
 * randomness and no network access appears anywhere below.
 *
 * ---------------------------------------------------------------------------------------------------
 * MIGRATION CONTEXT - WHAT THE LEGACY DID, AND WHY THIS SLICE LOOKS NOTHING LIKE IT
 * ---------------------------------------------------------------------------------------------------
 * Each item was measured against the checkout rather than assumed, and each is asserted somewhere below.
 *
 * 1. VIEW STATE AND SESSION STATE ARE GONE, BUT THERE WAS NONE HERE TO BEGIN WITH.
 *    `grep -ro 'ViewState(' Website/admin/Modules/ | wc -l` returns ZERO, exactly as it does for the
 *    security and page administration trees, and `Session(` returns zero everywhere. What the legacy
 *    module screens did instead was declare `Private Shadows ModuleId As Integer = -1`
 *    (`Website/admin/Modules/Export.ascx.vb` line 49 and `Import.ascx.vb` line 51) and re-parse that
 *    identifier out of the request on every postback. This slice therefore replaces POSTBACK RE-BINDING,
 *    not state round-tripping: a signal holds the selection once, for as long as a screen needs it.
 * 2. THERE IS NO LEGACY MODULE-LIST SCREEN. `ls Website/admin/Modules` yields only the export, import
 *    and settings screens. Every paging, ordering and filtering assertion below therefore comes from
 *    `module.service.ts` and `paged-result.model.ts`, and none is borrowed from the portal or account
 *    screens, whose contracts are theirs. The listing IS paged, and the wire page index is ZERO-BASED.
 * 3. THE TWO CACHE PERIODS ARE TWO FACTS. `Library/Components/Modules/ModuleInfo.vb` line 731 seeds
 *    `_CacheTime = 0` while line 759 seeds `_DefaultCacheTime = -1`, in the SAME constructor, with the
 *    accessors at lines 203 and 482. Two adjacent members, two different markers, and they live on two
 *    different contracts in the target. Nothing may coalesce them.
 * 4. REMOVAL IS SOFT, SO THE LISTING IS RE-READ. The endpoint answers `204` and the row survives with
 *    its marker set, which is what the legacy recycle bin read. Whether such a row still appears is the
 *    LISTING endpoint's decision, expressed through its inclusion flag, so an optimistic local splice
 *    would wrongly hide a row the server would still return. There is no reversal endpoint at all.
 * 5. EXPORT RETURNS THE DOCUMENT IN THE `200` BODY. `Export.ascx.vb` line 157 obtained it through a
 *    double late-bound cast - legal only because the administration code-behinds were compiled with
 *    Option Strict OFF (`Website/release.config` line 125) - and lines 168 to 186 then wrote that string
 *    to a file beneath the portal's home directory map path. The target does neither: the string is
 *    returned, and this slice holds it as an OPAQUE value it never inspects.
 * 6. IMPORT CARRIES NO ROUTE IDENTIFIER, and a body target of minus one is legitimate transmitted data
 *    (`Import.ascx.vb` line 51, `Export.ascx.vb` line 49, against the integer absence marker at
 *    `Library/Components/Shared/Null.vb` lines 41 to 45).
 * 7. THE PAGE TREE IS DERIVED ON THE CLIENT because no tree endpoint exists and the page transport is
 *    closed at three flat, unpaged operations - read a portal's pages, read one page, replace one page.
 *    This slice consumes exactly ONE of the three.
 * 8. THE ROOT TEST IS AN EXACT EQUALITY, AND BOTH ENCODINGS ARE ADMITTED. `tab.model.ts` states that a
 *    root page arrives as `parentId: null` because the backend converts the legacy marker at the
 *    boundary; the legacy form is minus one, measured at `Library/Components/Tabs/TabInfo.vb` line 91
 *    and `TabController.vb` lines 1032 and 1074. Because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`
 *    (`01.00.00.SqlDataProvider` line 140), a parent of ZERO is a REAL PARENT under both encodings, and
 *    only an exact comparison survives that.
 * 9. `dbo.Modules.ModuleID` IS `IDENTITY (0, 1)` (`01.00.00.SqlDataProvider` line 221), so module ZERO
 *    is the first module of an installation and a truthiness guard loses it silently.
 * 10. FOUR DISTINCT MEANINGS OF MINUS ONE ARE KEPT APART: a root page, an unset default cache period, a
 *    transfer target that has not been assigned, and the FIRST REAL PORTAL - `[PortalID]` is
 *    `IDENTITY (-1, 1)` (line 77). No generic "normalise a negative identifier" helper may exist.
 * 11. LATE-BOUND ACTIVATION OF A MODULE'S OWN CONTROLLER IS GONE. The legacy resolved a class name
 *    through reflection at `Library/Components/Modules/ModuleController.vb` lines 231 and 431 and in the
 *    event-message processor; the target resolves it server-side through dependency injection over a
 *    closed set. None of those sites was ever true component-object interop.
 * 12. HAND-ROLLED ROW HYDRATION IS GONE. `ModuleController.vb` line 54 built the object and lines 66 to
 *    72 assigned each column through a marker-substituting conversion, one statement per column. The
 *    server's object-relational materialiser replaced it, so nothing on this side reads a column.
 * 13. THE LEGACY CACHE LAYER IS NOT REPRODUCED CLIENT-SIDE. `ModuleController` was the largest single
 *    consumer of the legacy static cache, at twenty-one call sites with a scaled-expiry set and a coarse
 *    portal-wide clear. Caching in the target is server-side, behind an interface; this slice caches
 *    nothing, which is why every read below issues a request every time it is called.
 * 14. THE VISIBILITY ENUMERATION WAS RENAMED, AND ITS MEMBERS ARE ALL REAL. `Maximized` is `0` and
 *    `None` is `2`; neither is an absence marker.
 * 15. UNTYPED COLLECTION CONTRACTS BECAME TYPED ONES. The listing arrives as a paged envelope rather
 *    than through an out-parameter carrying a total alongside an untyped list.
 * 16. OPTIONAL PARAMETER TAILS BECAME REQUEST OBJECTS. The transfer operations take a request contract
 *    rather than a run of defaulted positional arguments.
 * 17. AUTHORISATION IS DECIDED SERVER-SIDE. A refusal arrives as `403`, is recorded as the problem
 *    document it is, and lands at WARNING severity - the legacy precedent is the access-denied page,
 *    which performs no permission check of its own and renders both of its branches as a yellow warning.
 *    This slice exposes no method that decides a permission. The rate-limit status does not arise on
 *    these endpoints at all: that policy is partitioned by address and applied to the authentication
 *    routes alone, so no specification below asserts one.
 * 18. LOCALISATION IS NOT PORTED. Three of the in-scope resource files belong to the module tree and
 *    they are read for wording only; no translation runtime exists in this workspace.
 * 19. EVERY IMPLICIT CONVERSION THE LEGACY GOT FOR FREE IS NOW EXPLICIT. Option Strict was off for the
 *    administration code-behinds, which is what made the double cast at `Export.ascx.vb` line 157 legal.
 *    Nothing in this file relies on an implicit conversion, and no suppression comment appears.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHAT IS DELIBERATELY NOT ASSERTED, AND THE MEASURED REASONS
 * ---------------------------------------------------------------------------------------------------
 * - NO WORDING. Resolving a problem document into sentences belongs to the shared form-error helper
 *   under `core/utils/`, which is not a dependency of this specification. Severity is asserted because
 *   the store records it; the sentences it sits beside are not.
 * - NO PRIVATE MEMBER. Every assertion goes through a public readonly signal or a command method.
 * - NO PAGE MUTATION. The page transport publishes no create and no delete route, and this slice
 *   consumes only the portal-scoped read, so those are asserted as ABSENT rather than exercised.
 * - NO RATE-LIMIT CASE, for the reason in item 17.
 * - NO SUCCESS-SHAPED FAILURE. The server's result wrapper never crosses the wire, so there is no
 *   specification for a failure flag beside a successful status.
 */

// =====================================================================================================
// LITERALS COMPOSED AT RUNTIME
// =====================================================================================================
//
// Two families of token must be PROVED ABSENT from the traffic this slice generates, and writing either
// as a source literal would make the absence unprovable by inspection: a reviewer grepping this tree for
// the token would find the assertion that forbids it and could not tell the two apart. Both are
// therefore assembled from fragments at runtime, which keeps the source clean while the assertion still
// runs against the real string. The same technique is used for the wildcard character below and in the
// transport specification alongside this one.

/**
 * The path fragments a reversal endpoint would carry, if one existed.
 *
 * It does not. The target publishes no bin endpoint and no reversal endpoint, and the page surface is
 * closed at read, read-one and replace, so no command on this slice can undo a removal. A removed module
 * can be made VISIBLE through the inclusion flag, which is a different thing from being brought back.
 */
const REVERSAL_FRAGMENTS: readonly string[] = [
  ['re', 'store'].join(''),
  ['recycle', 'bin'].join('-'),
  ['un', 'delete'].join(''),
];

/**
 * The trailing-wildcard character, obtained by code point.
 *
 * The legacy readers decorated a filter pattern at the call site and matched from the START of a value;
 * the target moved that decoration behind the repository interfaces, where the server applies a
 * substring match of its own. A pattern arriving here already decorated would be decorated twice.
 */
const WILDCARD: string = String.fromCharCode(37);

/** A date at the bottom of the calendar - the legacy absent-date marker, and real data on the wire. */
const MIN_DATE = '0001-01-01T00:00:00';

/** The prefix the server puts in front of every application failure code inside a problem `type`. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

// =====================================================================================================
// FIXTURES
// =====================================================================================================
//
// Every fixture is produced by a FACTORY rather than held as a shared constant. The reason is specific to
// this slice: the hierarchy derivation builds a nested structure out of the rows it is given and holds the
// row objects themselves on the nodes, so a specification that mutated a shared row in place would change
// what a later specification observed. A factory hands each specification its own objects and makes that
// impossible.
//
// Each factory is typed as the REAL contract, so a member this file misspells is a compile error rather
// than an `undefined` nobody notices. That matters more here than usual: camel-casing lower-cases only a
// leading run of capitals, so the wire member is `moduleId` with one lower-case `d` and never `moduleID`,
// and the same policy is why the definition key below is the ABBREVIATED `moduleDefId` while the route
// parameter that addresses the same concept is spelled out in full. The two genuinely differ and the
// contracts declare each separately; neither is unified here by guesswork.

/**
 * One row of the module listing, keyed AT THE IDENTITY SEEDS.
 *
 * `moduleId` and `tabId` default to ZERO because a row at the seed is precisely the row a truthiness test
 * loses, and the listing is where such a loss would be least visible.
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
 * One module placement in full.
 *
 * `portalId` is MINUS ONE - the first portal the schema ever creates and simultaneously the legacy
 * absent-integer marker - and `cacheTime` is ZERO, which is a CHOSEN lifetime rather than an unset one.
 * The empty strings on the markup and icon members are the legacy absent-string marker, whose accessor
 * body is literally `Return ""`.
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
    visibility: ModuleVisibility.Maximized,
    displayTitle: false,
    friendlyName: 'Announcements',
    moduleName: 'Announcements',
    description: '',
    version: '01.00.00',
    ...overrides,
  };
}

/**
 * One catalogue definition.
 *
 * `defaultCacheTime` defaults to MINUS ONE, which is the value the legacy constructor seeded it with -
 * against a `_CacheTime` of ZERO in the same constructor. The pairing is deliberate: a specification that
 * saw a zero where this minus one belongs would have caught the two facts being folded together.
 */
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
 * One page of the portal, at the identity seed and at the ROOT of the hierarchy.
 *
 * The default `parentId` is `null`, which is the form the wire uses: the backend converts the legacy
 * marker at the boundary because minus one is simultaneously a legitimate tenant identifier. Both
 * encodings are exercised below.
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

/**
 * A creation request in which every optional-looking member is falsy.
 *
 * Four kinds of falsy value appear on purpose - the empty string, `false`, `null` and `0` - so that a
 * whole-body comparison fails if any single kind is ever filtered out. A fixture of plausible non-empty
 * values would pass against a truthiness filter and prove nothing.
 */
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

/**
 * A replacement request carrying the page it edits, exactly as the contract requires.
 *
 * The update endpoint takes NO placement selector: the page is named by the required `tabId` member of
 * the body itself, which the server uses to select the placement being edited.
 */
function updateRequest(overrides: Partial<UpdateModuleRequest> = {}): UpdateModuleRequest {
  return {
    tabId: 0,
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
    visibility: ModuleVisibility.None,
    displayTitle: false,
    setAsDefaultSettings: false,
    applyToAllModules: false,
    ...overrides,
  };
}

/**
 * Both settings maps, with cleared values in each.
 *
 * The most marker-sensitive payload in the feature. The absent-string marker IS the empty string, the maps
 * legitimately hold cleared values, and a truthiness filter applied before sending would DELETE every
 * setting an operator had cleared while the request still answered `204`. The empty-valued keys are what
 * would make that failure loud.
 */
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

/** The two members of the export request contract. The module is named by the route, not by the body. */
function exportRequest(overrides: Partial<ModuleExportRequest> = {}): ModuleExportRequest {
  return { fileName: 'Announcements', folder: '', ...overrides };
}

/**
 * The four members of the import request contract, whose target defaults to MINUS ONE.
 *
 * That default is the point of the fixture rather than a convenience: the legacy screen declared its
 * target field initialised to the integer absence marker, so minus one is a value a caller may genuinely
 * hold, and the member is nullable on the contract precisely so an omission can be told apart from a
 * caller naming module ZERO, which is a real module.
 */
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

/** Wraps a payload in the single-item envelope every non-paged read answers with. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A problem document carrying the server's own failure code.
 *
 * The `type` member is where the code lives, behind the URN prefix the failure-code reader strips. The
 * codes are the SERVER's vocabulary rather than the legacy enumeration member names: the legacy
 * PascalCase names could never match a value taken off the wire, so every lookup keyed on them returned
 * nothing. `traceId` is present on every fixture because it is the only join key between what a person
 * saw in the browser and what the server logged.
 */
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
 * A field-level refusal, whose per-field dictionary uses .NET model-state keys.
 *
 * Those keys are NOT camel-cased, and because the dictionary is an index signature under
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
 * Narrows a request body to the import contract.
 *
 * The framework types a request body loosely, so it is narrowed through a predicate rather than widened
 * with a suppression or an escape-hatch annotation. The specification that uses it asserts the predicate
 * held BEFORE reading through it, so a body of the wrong shape fails loudly rather than skipping the
 * assertions inside the guard.
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
 * Every page the derivation placed, flattened depth-first, paired with the depth it was placed at.
 *
 * Used to prove CONSERVATION: every row handed to the derivation must appear exactly once across the
 * placed nodes and the reported unplaceable rows together, so nothing can be silently dropped. Written
 * iteratively for the same reason the derivation itself is - a malformed input must not exhaust the stack
 * in the assertion helper either.
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
      //
      // The store is listed explicitly even though it declares itself at the application root, so that
      // the instance under test is pinned to THIS injector and cannot be shared across specifications.
      // No interceptor is registered: the correlation identifier and the bearer credential are attached
      // by the two functional interceptors wired once at application configuration, and running that
      // chain here would mean asserting several units at once.
      providers: [provideHttpClient(), provideHttpClientTesting(), ModuleStore],
    });

    store = TestBed.inject(ModuleStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The single most valuable line in the file. It fails on any request no expectation consumed, which
    // is what turns "the removal re-read the listing, and nothing else happened" into a test result. It
    // is equally how a MISSING follow-up read is caught, because the expectation for it fails first, and
    // how a page mutation this slice must never issue would surface.
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // Local helpers. Declared inside the suite because each closes over the backend for this
  // specification, and the backend is rebuilt for every one of them.
  // ---------------------------------------------------------------------------------------------------

  /**
   * Consumes exactly one pending request at a literal path.
   *
   * The predicate form is used rather than the string form so that the VERB is asserted alongside the
   * path. This slice drives two transports through one backend, so a specification that matched on a path
   * alone could consume the wrong request and still pass.
   */
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

  /**
   * Reads a portal's pages and answers with the rows given, so the hierarchy can be inspected.
   *
   * The page listing answers inside the same single-item envelope every non-paged read uses, which is why
   * the rows are wrapped rather than flushed bare.
   */
  function loadTabsWith(portalId: number, rows: readonly TabListItem[]): void {
    store.loadTabs(portalId);
    expectRequest('GET', `/api/v1/portals/${portalId}/tabs`).flush(envelope(rows));
  }

  /** Asserts that no reversal endpoint was addressed. There is none to address. */
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

  // ===================================================================================================
  // PROOF 1 - IDENTIFIERS OF ZERO ARE REAL, ON BOTH THE MODULE SIDE AND THE PAGE SIDE
  // ===================================================================================================
  //
  // `01.00.00.SqlDataProvider` line 221 declares `[ModuleID] [int] IDENTITY (0, 1)` and line 140 declares
  // `[TabID] [int] IDENTITY (0, 1)`, so the first module and the first page of an installation are both
  // numbered ZERO. Line 77 declares `[PortalID] [int] IDENTITY (-1, 1)`, so minus one is the first portal
  // and zero is the second. Every specification in this group would pass trivially against a store that
  // guarded its identifiers with a truthiness test - EXCEPT that such a store would issue no request at
  // all, which is what the paired expectation and the verification in `afterEach` turn into a failure.
  describe('identifiers of zero and minus one', () => {
    it('issues GET /api/v1/modules/0 for module ZERO rather than skipping the read', () => {
      store.loadModule(0);

      // The literal path, asserted whole. A guard of the form "read only when the identifier is truthy"
      // produces NO request here, and this line is what makes that omission loud.
      const call = expectRequest('GET', '/api/v1/modules/0');

      // No placement selector was named, so the read addresses the MODULE rather than one of its
      // placements, and the two are materially different requests.
      expect(call.request.params.has('tabModuleId')).toBeFalse();

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
      expect(store.selectedTabModuleId()).toBeUndefined();
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

      store.selectPlacement(0);
      expect(store.selectedTabModuleId()).toBe(0);
      store.selectPlacement(undefined);
      expect(store.selectedTabModuleId()).toBeUndefined();
    });

    it('addresses one placement of module ZERO when a placement is selected', () => {
      // A placement identity seeds at 1 rather than at 0, so the three keys are not interchangeable even
      // where their ranges overlap. Selecting a placement must narrow the read to it.
      store.selectPlacement(7);
      store.loadModule(0);

      const call = expectRequest('GET', '/api/v1/modules/0');
      expect(call.request.params.get('tabModuleId')).toBe('7');

      call.flush(envelope(detail({ moduleId: 0, tabModuleId: 7 })));
      expect(store.module()?.tabModuleId).toBe(7);
    });
  });

  // ===================================================================================================
  // PROOF 2 - THE TWO CACHE PERIODS ARE TWO FACTS, AND NOTHING FOLDS ONE ONTO THE OTHER
  // ===================================================================================================
  //
  // `Library/Components/Modules/ModuleInfo.vb` initialises `_CacheTime = 0` at line 731 and
  // `_DefaultCacheTime = -1` at line 759 - IN THE SAME CONSTRUCTOR - with the property accessors at
  // lines 203 and 482. Two adjacent members, two different markers. The target puts the instance period
  // on the module contracts and the default on the definition contract, so folding either into the other
  // would change which modules expose a cache control and for how long the rest cache.
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
      // Asserted as an absence because a merged projection is the specific defect this pairing exists to
      // prevent: a screen reading one value that was silently substituted for the other cannot tell that
      // it happened. If any of these ever appears, it is a behavioural-equivalence violation to report
      // rather than a member to test.
      expect('effectiveCacheTime' in store).toBeFalse();
      expect('resolvedCacheTime' in store).toBeFalse();
      expect('cacheTimeOrDefault' in store).toBeFalse();
      expect('cachePeriod' in store).toBeFalse();
    });

    it('does not invent a cache member on a listing row when a replacement is echoed back', () => {
      // The listing projection deliberately omits the cache period: it lives on the module contracts and
      // the default lives on the definition contract, and neither appears on a row. A projection that
      // added one here would be manufacturing a fact the endpoint never sent.
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

  // ===================================================================================================
  // PROOF 3 - REMOVAL IS SOFT, SO THE LISTING IS RE-READ AND NO ROW IS PRUNED LOCALLY
  // ===================================================================================================
  //
  // The endpoint answers `204 No Content`, yet the row survives in the database with its marker set -
  // which is exactly what the legacy recycle bin read. Whether such a row still appears is the LISTING
  // endpoint's decision, expressed through its inclusion flag, and it is not this slice's to infer.
  describe('removal is soft and requires a re-read', () => {
    it('issues the removal and then a SECOND request that re-reads the listing', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 1 })]);

      store.deleteModule(0);

      // First the removal, at the literal path for module ZERO. A truthiness-guarded identifier would
      // produce no request at all, and this expectation is what fails when that happens.
      const removal = expectRequest('DELETE', '/api/v1/modules/0');
      expect(removal.request.params.has('tabModuleId')).toBeFalse();
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
      expect(reread.request.params.get('includeDeleted')).toBe('true');
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
      expect(removal.request.params.get('tabModuleId')).toBe('2');
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
      // replace, so nothing on this slice can undo a removal. The fragments are composed at runtime so
      // that the token being forbidden does not itself appear in this tree - see the note at the head.
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


  // ===================================================================================================
  // PROOF 4 - THE EXPORTED DOCUMENT ARRIVES IN THE BODY AND IS HELD AS AN OPAQUE STRING
  // ===================================================================================================
  //
  // `Website/admin/Modules/Export.ascx.vb` line 157 obtained the document through a double late-bound
  // cast, legal only because the administration code-behinds were compiled with Option Strict OFF
  // (`Website/release.config` line 125), and lines 168 to 186 then wrote that string to a file beneath the
  // portal's home directory map path, registering it in the file table afterwards. The target does
  // NEITHER: the endpoint answers `200` with the document in the response body, and this slice holds it
  // verbatim. Presenting it or saving it is the export SCREEN's concern.
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

      // The request contract is transmitted WHOLE, both members, with the empty folder present rather
      // than filtered out. The module is named by the ROUTE and never by the body, which is the one
      // structural difference between this operation and its import counterpart.
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
      // The exported content is module-authored, which makes it the most untrusted string this slice
      // holds - a second, independent untrusted channel alongside the wording in a problem document. The
      // in-scope legacy resource files carry seventy-six values containing markup, four of them a script
      // element, so a slice that wrapped this value as trusted markup would be a live injection vector.
      // It is stored as a string, and it is neither parsed nor marked safe anywhere.
      const hostile = '<content type="Announcements"><script>alert(1)</script></content>';

      store.exportModule(0, exportRequest());
      expectRequest('POST', '/api/v1/modules/0/export').flush(hostile);

      const held: string | null = store.exportedContent();
      expect(typeof held).toBe('string');
      expect(held).toBe(hostile);
      // Character-for-character, with the markup still escaped-free and un-neutralised: the value was
      // not sanitised-and-trusted, because sanitising here would be this slice deciding a rendering
      // question that belongs to the component that renders it.
      expect(held).toContain('<script>alert(1)</script>');
    });

    it('retains an EMPTY document as the empty string rather than normalising it to null', () => {
      // `Export.ascx.vb` line 159 tested `Content <> ""`, so an empty export was a recognised answer
      // rather than a fault, and the legacy absent-string marker IS the empty string. Here `null` means
      // "nothing has been exported yet" and the empty string means "the export produced nothing", and the
      // two must stay distinguishable.
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
      // The success response carries a markup media type, so the whole exchange is read as text - which
      // means a problem document arrives as an unparsed string rather than as an object. The slice parses
      // it once, in one place, and a body that is not a document at all falls back to the status.
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

  // ===================================================================================================
  // PROOF 5 - IMPORT CARRIES NO ROUTE IDENTIFIER, AND A BODY TARGET OF MINUS ONE IS TRANSMITTED VERBATIM
  // ===================================================================================================
  //
  // `Website/admin/Modules/Import.ascx.vb` line 51 declared `Private Shadows ModuleId As Integer = -1` -
  // an identifier field seeded with the integer absence marker from
  // `Library/Components/Shared/Null.vb` lines 41 to 45 - and lines 67 to 68 parsed the real target out of
  // a request value into it. Minus one is therefore a value a caller may genuinely hold, and the member is
  // nullable on the contract precisely so an omission can be told apart from a caller naming module ZERO.
  describe('import posts to a route with no identifier', () => {
    it('addresses exactly /api/v1/modules/import, with no identifier segment in either direction', () => {
      store.importModule(importRequest());

      const call = expectRequest('POST', '/api/v1/modules/import');

      // Asserted twice, positively and negatively. The obvious mistake is to interpolate the target
      // between the collection segment and the operation segment, the way every other module operation
      // addresses its subject; that produces a plausible-looking `404`, or worse a successful import
      // against the wrong module.
      expect(call.request.url).toBe('/api/v1/modules/import');
      expect(call.request.url).not.toMatch(/\/modules\/-?\d+\/import$/);
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
      // and changes no column the listing projects, so the command records completion and issues no
      // further request; the verification in `afterEach` is what proves the absence. What matters for
      // behavioural equivalence is the other half of the claim, and it holds: nothing is manufactured from
      // the request, so no row appears that the server never returned.
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
      // `Import.ascx.vb` line 184 opened a stream against the portal's home directory map path joined to
      // the operator's folder and name, lines 188 to 192 built a document and reported a parse failure,
      // line 197 compared the declared type against the module's own name and line 200 handed the inner
      // markup to the module's portability behaviour, passing an acting account explicitly. Every part of
      // that is server behaviour now, and the acting account comes from the authenticated caller, because
      // an identifier a request could choose for itself would let one account attribute an import to
      // another. The document travels as TEXT inside a JSON body: no multipart form, no upload primitive
      // and no server path appears anywhere in this operation.
      store.importModule(importRequest());

      const call = expectRequest('POST', '/api/v1/modules/import');
      expect(call.request.responseType).toBe('json');
      expect(typeof call.request.body).toBe('object');

      call.flush(null, { status: 204, statusText: 'No Content' });
    });
  });


  // ===================================================================================================
  // PROOF 6 - THE PAGE HIERARCHY IS DERIVED HERE, AND THE ROOT TEST IS AN EXACT EQUALITY
  // ===================================================================================================
  //
  // This is the highest-value group in the file, because the derivation exists nowhere else. The page
  // transport is closed at three operations and states that folding a flat list into a hierarchy "belongs
  // to a signal store or to the component that renders the indentation", and the models layer declares no
  // tree type at all. So there is no server answer to compare against and no other specification covering
  // it: these assertions are the whole proof.
  //
  // Two encodings of "root" are in play and BOTH are admitted, which is not hedging. `tab.model.ts` states
  // that a root page arrives as `parentId: null` because the backend converts the legacy marker at the
  // boundary, where minus one would otherwise be indistinguishable from a legitimate tenant identifier.
  // The legacy encoding is minus one, measured at `Library/Components/Tabs/TabInfo.vb` line 91 and at
  // `TabController.vb` lines 1032 and 1074, both of which assign the integer absence marker at the point
  // where a page is being made root-level. Testing minus one ALONE against the current contract would put
  // every root page into the unplaceable set and render an EMPTY tree behind a successful response;
  // testing null alone would silently re-parent a root page if a legacy-shaped payload ever arrived.
  //
  // What neither test can be wrong about is the case that matters most: `dbo.Tabs.TabID` is
  // `IDENTITY (0, 1)` (`01.00.00.SqlDataProvider` line 140), so a parent of ZERO is a REAL PARENT under
  // both encodings, and only an exact equality survives that. A truthiness test, a comparison against a
  // bound or a loose null comparison would each re-parent every child of page zero to the root.
  describe('page hierarchy derivation', () => {
    it('nests a page whose parent is page ZERO under page zero, and does NOT re-parent it to the root', () => {
      // The decisive specification. It fails against every defective root test at once - a truthiness
      // negation, a comparison against zero or one, a loose null comparison and a double negation - because
      // each of those classifies a parent of zero as "no parent".
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

      // Stated explicitly, because minus one is the DOMAIN VALUE ITSELF here rather than a stand-in for
      // absence - which is what makes this the one legitimate comparison against it in this file. The
      // strict form is used deliberately: `parentId !== -1` for "has a parent" is equally legitimate,
      // while a comparison against a bound or a truthiness test is not.
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
      // A page names one parent, so a cycle can only form among pages unreachable from any root. The
      // traversal expands only pages not yet placed, which bounds it on any input whatsoever - a mutual
      // pair, a self-parenting page, or a long chain - without a separate cycle detector and without
      // recursion that could exhaust the stack. This specification would hang or overflow against a naive
      // recursive implementation, which is precisely why it is here.
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
      // A caller can legitimately hold a list whose parents were filtered out, so this is an ordinary
      // outcome rather than a fault - and it is reported rather than discarded, because a page that
      // vanished from both the tree and the report would be invisible to the operator who owns it.
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

      // Every member of the listing contract is reachable through the node, and the depth the traversal
      // computed sits BESIDE the server's own value rather than overwriting it: the two can legitimately
      // differ when the server computed its value against a parent chain this response does not contain.
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
      expectRequest('GET', '/api/v1/portals/0/tabs').flush(envelope(rows));

      rows.push(tabRow({ tabId: 1, tabName: 'Added later', parentId: 0 }));

      // The slice took a snapshot, so a caller still holding the array cannot change what the hierarchy
      // reports afterwards.
      expect(store.tabs().length).toBe(1);
      expect(store.tabTree()[0].children.length).toBe(0);
    });
  });


  // ===================================================================================================
  // PROOF 7 - EVERY LOOKUP IS UNPAGED, AND NO PAGING STATE IS KEPT FOR ONE
  // ===================================================================================================
  //
  // Three collections are deliberately unpaged: a portal's pages, the definition catalogue, and the
  // definitions of one deployed bundle. The page listing is unpaged for a stated reason - "a hierarchy is
  // read whole because a partially fetched tree cannot be indented correctly" - and the catalogue is small,
  // bounded reference data the upgrade scripts seed, whose endpoint accepts no query parameter at all.
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

      expect(call.request.params.keys().length).toBe(0);
      expect(call.request.params.has('pageIndex')).toBeFalse();
      expect(call.request.params.has('pageSize')).toBeFalse();
      expect(call.request.params.has('sortBy')).toBeFalse();
      expect(call.request.params.has('sortDir')).toBeFalse();
      expect(call.request.params.has('query')).toBeFalse();

      call.flush(envelope([tabRow({ tabId: 0, parentId: -1 })]));
      expect(store.tabs().length).toBe(1);
    });

    it('reads the whole definition catalogue with an entirely empty query', () => {
      store.loadDefinitions();
      const call = expectRequest('GET', '/api/v1/module-definitions');

      expect(call.request.params.keys().length).toBe(0);

      call.flush(envelope([definition({ moduleDefId: 4 }), definition({ moduleDefId: 5 })]));

      expect(store.definitions().length).toBe(2);
      expect(store.definitionsLoading()).toBeFalse();
    });

    it('reads the definitions of one bundle from a path segment, not from a query parameter', () => {
      store.loadDesktopDefinitions(2);
      const call = expectRequest('GET', '/api/v1/module-definitions/desktop-modules/2');

      expect(call.request.params.keys().length).toBe(0);

      call.flush(envelope([definition({ desktopModuleId: 2 })]));

      expect(store.desktopDefinitions().length).toBe(1);
      // Held apart from the whole catalogue rather than overwriting it, because a form showing one bundle
      // still needs the catalogue behind it.
      expect(store.definitions().length).toBe(0);
    });

    it('addresses one definition by the ROUTE spelling while the response keeps its own member name', () => {
      // NAMING, STATED EXPLICITLY BECAUSE THE TWO SPELLINGS GENUINELY DIFFER AND BOTH ARE CORRECT. The
      // endpoint is declared `GET /module-definitions/{moduleDefinitionId}`, spelled out in full, and the
      // command below names its argument that way; the RESPONSE contract abbreviates the same concept on
      // its own member, which `module.model.ts` declares as `moduleDefId` on four separate shapes. Each
      // spelling is taken from its own declaration and neither is unified here by guesswork, so the
      // argument passed in is the route's and the member read back out is the response's. The bundle
      // identifier has only one spelling, `desktopModuleId`, and it is used unchanged.
      store.loadDefinition(4);
      const call = expectRequest('GET', '/api/v1/module-definitions/4');

      expect(call.request.params.keys().length).toBe(0);

      call.flush(envelope(definition({ moduleDefId: 4, defaultCacheTime: 900 })));

      expect(store.definition()?.moduleDefId).toBe(4);
      expect(store.definition()?.defaultCacheTime).toBe(900);

      store.clearDefinition();
      expect(store.definition()).toBeNull();
    });

    it('keeps NO page index, page size or total for any of the three unpaged collections', () => {
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

  // ===================================================================================================
  // PROOF 8 - THE MODULE LISTING IS PAGED, AND ITS WIRE PAGE INDEX IS ZERO-BASED
  // ===================================================================================================
  //
  // There is no legacy module-list screen to inherit a contract from: `ls Website/admin/Modules` yields
  // only the export, import and settings screens. Every member asserted here therefore comes from
  // `module.service.ts` and `paged-result.model.ts`. The legacy sibling screens carried a ONE-based counter
  // and subtracted one at the call site - `Website/admin/Users/Users.ascx.vb` line 265 and
  // `Website/admin/Portal/Portals.ascx.vb` line 142 both pass `CurrentPage - 1` - and that subtraction
  // survives only as a presentation concern. This slice holds the SERVER's coordinate.
  describe('the paged module listing', () => {
    it('requests the first page as index ZERO with the default size, performing no arithmetic', () => {
      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe('10');
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
      expect(call.request.params.get('pageIndex')).toBe('1');
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
      expect(call.request.params.get('pageIndex')).toBe('0');
      expect(call.request.params.get('pageSize')).toBe('25');
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
      expect(call.request.params.get('sortBy')).toBe('moduleTitle');
      expect(call.request.params.get('sortDir')).toBe('Descending');
      call.flush(pagedBody([]));
    });

    it('clears an ordering back to the server default without dropping the member', () => {
      store.setSort('moduleTitle', 'Ascending');
      store.setSort(null, null);

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      // A null argument means "let the server choose", so the member is not transmitted at all - which is
      // a different request from transmitting an empty ordering.
      expect(call.request.params.has('sortBy')).toBeFalse();
      expect(call.request.params.has('sortDir')).toBeFalse();
      call.flush(pagedBody([]));
    });

    it('holds the filter text EXACTLY as supplied and appends no wildcard', () => {
      store.setQuery('news');

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(call.request.params.get('query')).toBe('news');
      // Composing a pattern is the server's business: the legacy readers decorated it at the call site and
      // matched from the START of a value, and the target applies a substring match server-side. A pattern
      // decorated here would be decorated twice.
      expect(call.request.params.get('query')).not.toContain(WILDCARD);
      call.flush(pagedBody([]));
    });

    it('transmits an EMPTY filter as an empty filter rather than as no filter', () => {
      // The legacy absent-string marker IS the empty string, so the two are held apart here and neither is
      // normalised into the other. Reinterpreting an explicit empty filter would be this slice deciding a
      // question the server already answers.
      store.setQuery('');

      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(call.request.params.has('query')).toBeTrue();
      expect(call.request.params.get('query')).toBe('');
      call.flush(pagedBody([]));
    });

    it('sends a page restriction of ZERO and removes it only for an explicit undefined', () => {
      store.setTabFilter(0);
      store.loadModules();
      const restricted = expectRequest('GET', '/api/v1/modules');
      expect(restricted.request.params.has('tabId')).toBeTrue();
      expect(restricted.request.params.get('tabId')).toBe('0');
      restricted.flush(pagedBody([listRow({ tabId: 0 })]));

      store.setTabFilter(undefined);
      store.loadModules();
      const unrestricted = expectRequest('GET', '/api/v1/modules');
      expect(unrestricted.request.params.has('tabId')).toBeFalse();
      unrestricted.flush(pagedBody([listRow()]));
    });

    it('sends an inclusion flag of FALSE as false, because false is a value', () => {
      store.setIncludeDeleted(false);
      store.loadModules();
      const call = expectRequest('GET', '/api/v1/modules');

      expect(call.request.params.has('includeDeleted')).toBeTrue();
      expect(call.request.params.get('includeDeleted')).toBe('false');
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
      // which is confined to one method, one endpoint, one request. The pages are needed before the listing
      // can be restricted to one, because a screen that fired both at once could restrict the listing to a
      // page that turned out not to exist.
      store.loadPortalScope(-1, 0);

      const pages = expectRequest('GET', '/api/v1/portals/-1/tabs');
      pages.flush(envelope([tabRow({ tabId: 0, parentId: -1 })]));

      const listing = expectRequest('GET', '/api/v1/modules');
      expect(listing.request.params.get('tabId')).toBe('0');
      listing.flush(pagedBody([listRow({ tabId: 0 })]));

      expect(store.tabPortalId()).toBe(-1);
      expect(store.selectedTabId()).toBe(0);
      expect(store.modules().length).toBe(1);
    });

    it('scopes to a portal without a page restriction when none is named', () => {
      store.loadPortalScope(0);

      expectRequest('GET', '/api/v1/portals/0/tabs').flush(envelope([tabRow({ tabId: 0, parentId: -1 })]));

      const listing = expectRequest('GET', '/api/v1/modules');
      expect(listing.request.params.has('tabId')).toBeFalse();
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


  // ===================================================================================================
  // PROOF 9 - EVERY VISIBILITY CODE IS A REAL VALUE, INCLUDING ZERO AND TWO
  // ===================================================================================================
  //
  // The enumeration was renamed on the way across and its numbers were written down explicitly rather than
  // left to declaration order, because a member inserted in the middle of the legacy declaration would have
  // silently remapped every stored row. `None` means "renders without container chrome" - a rendering
  // instruction an operator CHOSE, offered as the third of three radio options on the legacy screen - and it
  // is emphatically not "no visibility recorded".
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
      // Imported as a value because it is an ordinary numeric enumeration rather than a constant one -
      // `isolatedModules` is enabled, under which a constant enumeration is not a sound declaration. A
      // second local declaration of these codes is exactly how two spellings of one fact start to drift.
      expect(ModuleVisibility.Maximized).toBe(0);
      expect(ModuleVisibility.Minimized).toBe(1);
      expect(ModuleVisibility.None).toBe(2);
    });
  });

  // ===================================================================================================
  // PROOF 10 - THE PAGE SURFACE IS READ-ONLY FROM HERE, AND ONE OF ITS THREE READS IS UNUSED
  // ===================================================================================================
  //
  // The page transport publishes exactly three operations - read a portal's pages, read one page, replace
  // one page - and there is NO create route and NO delete route on the server to call. This slice consumes
  // only the first of the three: it needs a flat list to derive a hierarchy and a picker from, and it neither
  // reads nor replaces an individual page. Both facts are asserted as ABSENCES, because an absence is what
  // they are.
  describe('page mutation is never issued from this slice', () => {
    it('never posts to or deletes from the page collection', () => {
      loadTabsWith(0, [tabRow({ tabId: 0, parentId: -1 }), tabRow({ tabId: 1, parentId: 0 })]);

      store.selectTab(1);
      store.setTabFilter(1);

      // No create route and no delete route exists, so addressing one would be addressing something the
      // API does not serve.
      httpMock.expectNone((candidate) => candidate.method === 'POST' && candidate.url === '/api/v1/tabs');
      httpMock.expectNone((candidate) => candidate.method === 'DELETE' && candidate.url.startsWith('/api/v1/tabs'));
      httpMock.expectNone((candidate) => candidate.method === 'PUT' && candidate.url.startsWith('/api/v1/tabs'));
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

  // ===================================================================================================
  // PROOF 11 - SENTINEL FIDELITY, WHICH IS THE LARGEST SINGLE RISK IN THIS FEATURE
  // ===================================================================================================
  //
  // `Library/Components/Shared/Null.vb` defines a marker for every primitive: lines 36 to 45 give minus one
  // for a short and for an integer, line 48 gives 255 for a byte, lines 51 to 65 give the smallest value of
  // each floating type, lines 66 to 70 give the bottom of the calendar for a date, lines 71 to 75 give THE
  // EMPTY STRING for a string - the accessor body is literally `Return ""`, not a null - lines 76 to 80 give
  // `False` for a boolean and lines 81 to 85 give the empty identifier for one of those. Its test at lines
  // 208 to 237 returns true for every one of those values, which is why the legacy code could not tell
  // `false` from "not recorded". The module tree is the LARGEST consumer of that helper of all five in-scope
  // trees.
  //
  // In the target every one of those values is DATA. The server serialises with an ignore condition that
  // elides nothing, so they arrive un-elided, and the boolean members are non-nullable.
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
      // Both markers are treated identically by the LEGACY predicate, and the target keeps them
      // distinguishable: one legacy screen tested a message against the empty string directly while
      // another tested it against the marker accessor, which is independent proof that the two were
      // interchangeable there and must not be here.
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
      expect(read.request.params.get('tabModuleId')).toBe('1');
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
      expect(call.request.params.has('tabModuleId')).toBeFalse();
      call.flush(envelope(settingsBag({ tabModuleId: null })));

      expect(store.settings()?.tabModuleId).toBeNull();
    });

    it('keeps the FOUR distinct meanings of MINUS ONE apart within one scenario', () => {
      // The same number carries four unrelated meanings in this feature, and conflating any pair would be a
      // silent behavioural change. All four appear below at once, each asserted through the member that owns
      // its meaning:
      //   a root page                            - the hierarchy places it at the top level
      //   an unset default cache period          - held on the definition, untouched
      //   a transfer target not yet assigned     - transmitted verbatim in a request body
      //   the FIRST REAL PORTAL                  - the tenant identifier on a placement
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

    it('transmits a creation request whose every falsy member survives, and re-reads the listing', () => {
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

      // The created placement becomes the loaded module AND the listing is re-read, so a screen showing a
      // form beside a grid does not have to ask for the refresh itself.
      expect(store.module()?.moduleId).toBe(0);
      expect(store.selectedModuleId()).toBe(0);
      expectRequest('GET', '/api/v1/modules').flush(pagedBody([listRow({ moduleId: 0, tabModuleId: 9 })]));

      expect(store.saving()).toBeFalse();
      expect(store.modules().length).toBe(1);
    });
  });


  // ===================================================================================================
  // PROOF 12 - FAILURES ARE HELD STRUCTURALLY, WITH THE TRACE IDENTIFIER, AND NEVER AS MARKUP
  // ===================================================================================================
  //
  // The server answers with an RFC 7807 document under a problem media type. Its per-field dictionary uses
  // .NET model-state keys, which are NOT camel-cased, and because that dictionary is an index signature
  // under `noPropertyAccessFromIndexSignature` every read of it below is an INDEX EXPRESSION.
  //
  // Severity is resolved by the shared helper rather than decided here, and the distinction it draws is
  // load-bearing: a REFUSAL is a WARNING. The legacy authority is the access-denied page, fifty lines that
  // perform no permission check of their own and render BOTH of their branches as a yellow warning, with the
  // untrusted message HTML-encoded before display.
  //
  // The rate-limit status does not arise on any endpoint in this feature. That policy is partitioned by
  // address and applied to the authentication routes alone, so no specification here asserts one - writing a
  // case for a status no request can elicit would mislead a reader more than saying nothing would.
  describe('failure handling', () => {
    it('records a refusal on an all-pages replacement at WARNING severity, not as an error', () => {
      // The server enforces a rule of its own on the all-pages flag and no check anticipating it exists in
      // the slice: a copy of a server rule on the client gives an HTTP caller a different answer from every
      // other caller, and the two copies drift.
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
      // Required rather than optional. The identifier is derived server-side from the ambient activity or
      // the request identifier, so the correlation value the application sends on every request round-trips
      // back into this body - and it is the ONLY join key between what a person saw in the browser and what
      // the server logged. It matters most on the transfer paths, which are the hardest to reproduce.
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
      // Measured across the in-scope resource files: seventy-six values carry an HTML tag, and four carry a
      // script element - one of them a live script block in a portal settings resource. So wording taken off
      // the wire is untrusted, and this slice stores the STRUCTURED DOCUMENT and never pre-rendered markup.
      // Nothing here builds markup, marks a value trusted, or stores the result of a sanitiser.
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
      // The legacy screens prefixed a message with a break tag in two different spellings - the unclosed
      // form in the portal signup screen and the self-closing form in the account screen - and stripping it
      // is the shared form-error helper concern rather than this one. The RAW document is what is stored.
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

    it('re-reads the listing when a replacement echoes NO placement back', () => {
      loadListWith([listRow({ moduleId: 0, tabModuleId: 5 })]);

      store.updateModule(0, updateRequest(), 5);
      expectRequest('PUT', '/api/v1/modules/0').flush(envelope(null));

      // With no echo to merge, the listing is the only source of truth for the row, so it is re-read rather
      // than left stale on screen.
      expectRequest('GET', '/api/v1/modules').flush(pagedBody([listRow({ moduleId: 0, tabModuleId: 5 })]));

      expect(store.module()).toBeNull();
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

  // ===================================================================================================
  // PROOF 13 - THE PUBLIC SURFACE IS OBSERVABLE BUT NOT WRITABLE, AND THE BOUNDARY HOLDS
  // ===================================================================================================
  //
  // Composition belongs in this slice: sequencing several calls, holding in-flight and failure state,
  // deriving the hierarchy, projecting the rows. Formatting, deciding a permission, assembling an HTTP
  // request by hand, validating a rule and deciding what a pager shows do NOT. Each of those is asserted as
  // an absence, because a leak across that boundary is how a second, divergent copy of a rule gets created.
  describe('the public surface and its boundary', () => {
    it('exposes writable state ONLY as readonly signals', () => {
      // A writable signal carries both a setter and an updater; a readonly projection carries neither. The
      // property-presence test is the type-safe proof: attempting the write instead would require a cast,
      // and a cast is how an escape hatch gets into a specification.
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
      expect('set' in store.selectedTabModuleId).toBeFalse();
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
      // Two closed vocabularies exist and they are not interchangeable: the persisted permission keys on one
      // side and the policy names the server enforces on the other. Neither decides anything on the client,
      // there is no deny prefix in this generation of the schema, and a refusal arrives as a `403`.
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
      // Cross-aggregate coordination is the feature component concern. A store reaching into another store
      // would make one screen fetch what a different screen owns, and the traffic would be invisible in
      // either of their specifications.
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
      // over a closed set, and none of those legacy sites was ever component-object interop. Nothing on this
      // side names a class, probes an assembly or invokes a behaviour.
      expect('businessControllerClass' in store).toBeFalse();
      expect('createObject' in store).toBeFalse();
      expect('resolveController' in store).toBeFalse();
      expect('invokeModule' in store).toBeFalse();
    });

    it('caches nothing, so every read issues a request', () => {
      // The legacy module controller was the largest single consumer of the legacy static cache, at
      // twenty-one call sites, with a scaled-expiry set and a coarse portal-wide clear. Caching in the target
      // is server-side behind an interface. A slice that memoised a read would serve a stale catalogue after
      // an upgrade seeded a new definition.
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

    it('clears the loaded module and the selections that addressed it', () => {
      store.selectPlacement(5);
      store.loadModule(0, 5);
      expectRequest('GET', '/api/v1/modules/0').flush(envelope(detail({ moduleId: 0, tabModuleId: 5 })));

      expect(store.module()?.moduleId).toBe(0);
      expect(store.selectedModuleId()).toBe(0);

      store.clearModule();

      expect(store.module()).toBeNull();
      expect(store.selectedModuleId()).toBeUndefined();
      expect(store.selectedTabModuleId()).toBeUndefined();
    });
  });
});

