/**
 * The Angular 19 Signal store that owns module-administration state.
 *
 * It holds the paged module listing with its page and recycle-bin filters, the module currently being
 * edited, that module's two settings maps, the read-only definition and bundle catalogues, the exported
 * document, the outcome of an import, and the flat page list from which this file DERIVES the page tree
 * the module screens need in order to place a module.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHY COMPOSITION LIVES HERE AND NOWHERE ELSE
 * ---------------------------------------------------------------------------------------------------
 * `core/services/module.service.ts` and `core/services/tab.service.ts` are typed transports: one method,
 * one endpoint, one cold observable, and they never subscribe. That is the migration discipline's
 * confinement of Angular services to API communication, and it leaves exactly three responsibilities
 * without a home, all of which land here:
 *
 *   1. SEQUENCING. Loading a portal's pages and then the modules placed on the selected page is two
 *      requests with an ordering between them. So is re-reading a listing after a removal, and re-reading
 *      the settings after a replacement that answers `204` with no body.
 *   2. THE PAGE TREE. There is no tree endpoint and no tree type on any contract. The flat, unpaged page
 *      list arrives and the hierarchy is computed from it, in this file. `tab.service.ts` says so in as
 *      many words: tree building "belongs to `core/state/*.store.ts` or the consuming component".
 *   3. THE ONE-BASED / ZERO-BASED BOUNDARY. The wire page index is zero-based and the transport performs
 *      no arithmetic on it in either direction, stating that "the mapping between the two lives in the
 *      feature store". This file holds the zero-based value the server wants; a pager may present a
 *      one-based counter over it.
 *
 * Conversely, three responsibilities are deliberately NOT here. Validation belongs to the reactive forms
 * under `features/module/**`. Presentation - wording, formatting, pager visibility, section collapse -
 * belongs to the components. Authorisation belongs to the server, which answers `403`.
 *
 * This is the one store that legitimately injects TWO transports, because a module's placement and the
 * page it is placed on are inseparable. It injects no other store, so no cycle is possible.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHAT THIS FILE IS NOT
 * ---------------------------------------------------------------------------------------------------
 * Not an HTTP client: nothing here constructs a URL, a query string or a header, and it imports neither
 * the HTTP client nor the endpoint registry nor the query-parameter helpers nor any environment module.
 * Not a file-download engine and not a document parser - see the export note below. Not a presenter, not
 * an authoriser, not a page-administration store, and not a module loader: the legacy Web Forms
 * control-loading subsystem is excluded wholesale, so nothing here activates a type by name.
 *
 * ---------------------------------------------------------------------------------------------------
 * MIGRATION NOTES
 * ---------------------------------------------------------------------------------------------------
 * MIGRATION: VIEW STATE AND SESSION STATE ARE ELIMINATED, AND IN THIS FEATURE THERE WAS NOTHING TO
 *   ELIMINATE. Measured rather than assumed: `grep -ro 'ViewState(' Website/admin/Modules/` returns
 *   ZERO, and `grep -ro 'Session(' Website/admin/` returns ZERO across all thirty-nine administration
 *   code-behinds. The plan's "ViewState usage and Session state" source for the five stores is therefore
 *   satisfied VACUOUSLY here, and no analogue of either is manufactured. What this store actually
 *   replaces is narrower and real: `Website/admin/Modules/Export.ascx.vb:L49` and
 *   `Website/admin/Modules/Import.ascx.vb:L51` each declared `Private Shadows ModuleId As Integer = -1`,
 *   a field RE-PARSED FROM THE QUERY STRING ON EVERY POSTBACK. A signal holds that selection once, for
 *   as long as the screen needs it, and no round trip re-establishes it. Separately, the eleven
 *   `ViewState("UrlReferrer")` sites elsewhere in the administration tree are return-navigation and are
 *   an Angular ROUTER concern; not one of them becomes a signal here.
 *
 * MIGRATION: THERE IS NO LEGACY MODULE-LIST ADMINISTRATION PAGE, SO THE LISTING CONTRACT IS NEW.
 *   `ls Website/admin/Modules` yields only `Export`, `Import` and `ModuleSettings` with their markup and
 *   resources. The plan describes the list screen as a "new list screen over an existing query surface",
 *   sourced from `Library/Components/Modules/ModuleController.vb` rather than from a screen. Consequently
 *   there is no legacy page size, no legacy `CurrentPage - 1` call site and no legacy sort to preserve,
 *   and none is invented: every paging and filtering member below is taken from the transport's own
 *   signature. The sibling portal and user screens do have such contracts, and theirs are theirs.
 *
 * MIGRATION: A MODULE IDENTIFIER OF ZERO IS A REAL MODULE AND A PAGE IDENTIFIER OF ZERO IS A REAL PAGE.
 *   `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L221` declares
 *   `[ModuleID] [int] IDENTITY (0, 1)` and `:L140` declares `[TabID] [int] IDENTITY (0, 1)`, while
 *   `:L77` declares `[PortalID] [int] IDENTITY (-1, 1)`. The first module and the first page of an
 *   installation are numbered ZERO and the first portal is numbered MINUS ONE - the same values the
 *   legacy sentinel helper used for "absent" (`Library/Components/Shared/Null.vb:L41-L45`). Every
 *   selection below is therefore `number | undefined`, absence is `undefined` ALONE, and every presence
 *   test is an explicit `=== undefined`. No identifier in this file is tested for truthiness, compared
 *   against zero or minus one as though that meant "unset", coalesced, or defaulted. The legacy
 *   precedent is exactly this: every legacy view-state read guarded with `Is Nothing`, an explicit
 *   absence test, which a module numbered zero survives and a truthiness test does not.
 *
 * MIGRATION: THE INSTANCE CACHE PERIOD AND THE DEFINITION'S DEFAULT ARE NEVER COALESCED, AND THE PROOF
 *   IS MEASURED. `Library/Components/Modules/ModuleInfo.vb` declared both on one class and initialised
 *   them to DIFFERENT values in one constructor: `_CacheTime = 0` at L731 and `_DefaultCacheTime = -1`
 *   at L759. Zero on the instance means "this placement is not cached"; minus one on the definition
 *   means "no default is declared". They are distinct facts, they live on distinct contracts here -
 *   `cacheTime` on the module shapes and `defaultCacheTime` on the definition shape - and this store
 *   holds both side by side without merging them. No expression anywhere below coalesces the instance
 *   period onto the definition default, in either direction, and none derives a single "effective"
 *   period: resolving the two is the server's business, and merging them would discard the distinction
 *   the legacy contract took care to keep. Neither member is dropped for being zero.
 *
 * MIGRATION: REMOVAL IS SOFT, SO THE LISTING IS RE-READ RATHER THAN LOCALLY PRUNED. `DELETE` answers
 *   `204` and the row SURVIVES with its deleted marker set - which is precisely what the legacy
 *   recycle-bin screen under `Website/admin/Tabs/` consumed. Whether a removed module still appears is
 *   the listing endpoint's decision, expressed through its inclusion flag, and is not this store's to
 *   infer, so {@link ModuleStore.deleteModule} re-reads instead of splicing a row out of a local array.
 *   There is NO restore endpoint and NO recycle-bin endpoint in the target surface, and no method here
 *   reverses a removal; adding one would address a route the API does not serve.
 *
 * MIGRATION: THE EXPORT COMES BACK IN THE RESPONSE BODY, AND THE LEGACY FILE WRITE IS DROPPED ENTIRELY.
 *   `Export.ascx.vb:L157` obtained the document through a DOUBLE LATE-BOUND CAST -
 *   `CType(CType(objObject, IPortable).ExportModule(ModuleID), String)` - and L168-L186 then wrote that
 *   string to a file beneath the portal's home-directory map path. The target does NEITHER: the endpoint
 *   answers `200` with the document as the response body, and this store holds it as an OPAQUE string.
 *   Nothing here parses it, pretty-prints it, sanitises it, names it, resolves a path for it or hands it
 *   to a download mechanism. An empty document remains a distinct answer and is not collapsed into an
 *   absence, matching the legacy `If Content <> ""` branch at L159.
 *
 * MIGRATION: IMPORT CARRIES NO ROUTE IDENTIFIER, AND ITS BODY MEMBER MAY LEGITIMATELY BE MINUS ONE. The
 *   endpoint is `POST /modules/import` with no identifier in the path; the target module is a MEMBER OF
 *   THE REQUEST. `Import.ascx.vb:L51` seeded that field with the integer absence marker, so minus one is
 *   a value a caller may genuinely hold and send. {@link ModuleStore.importModule} transmits the request
 *   WHOLE and untouched: it does not rewrite minus one to null, does not omit the member, does not refuse
 *   the request, and does not route it through a per-module path. The server adjudicates the document,
 *   its declared type and its target together, which is why splitting the target into the path would let
 *   a caller address a module the payload contradicts.
 *
 * MIGRATION: THE PAGE TREE IS DERIVED ON THE CLIENT, AND THE ROOT TEST IS EXPLICIT. See the extended
 *   note on {@link ModuleStore.tabTree}, which records a genuine divergence between the legacy in-band
 *   sentinel and what the wire actually carries.
 *
 * MIGRATION: `tab.model.ts` DELIBERATELY DECLARES NO TREE TYPE, so {@link TabTreeNode} is declared in
 *   this file and is a shape of this store rather than of the models layer, which this file does not
 *   edit.
 *
 * MIGRATION: FOUR DISTINCT MEANINGS OF MINUS ONE ARE KEPT APART, AND NO HELPER "NORMALISES NEGATIVE
 *   IDENTIFIERS". On a definition, `defaultCacheTime` of minus one means no default is declared
 *   (`ModuleInfo.vb:L759`). On an export or import body, a module of minus one means "not yet chosen"
 *   (`Export.ascx.vb:L49`, `Import.ascx.vb:L51`). On a page, a parent of minus one meant ROOT LEVEL in
 *   the legacy encoding - not absence - (`TabInfo.vb:L91`, `TabController.vb:L1032` and `:L1074`). On a
 *   portal, the six page references use minus one for "no such page is configured". A fifth value
 *   completes the set and is not a sentinel at all: `cacheTime` of ZERO means "not cached"
 *   (`ModuleInfo.vb:L731`). Each is handled where it occurs and none is folded into another.
 *
 * MIGRATION: THE UNRENDERED VISIBILITY CODE IS A REAL VALUE. `ModuleVisibility.None` is 2 and means the
 *   placement renders without its container chrome - an operator's choice, persisted in a `NOT NULL`
 *   column. It is never read as "unset". Its sibling `Maximized` is 0, which is both the default and a
 *   real value, so no truthiness test may touch a visibility code either. This store transports the code
 *   and interprets it nowhere, so it needs no switch over it; the legacy enumeration was named
 *   `VisibilityState` and was renamed on the way across.
 *
 * MIGRATION: THE HAND-ROLLED ROW HYDRATION IS DELETED RATHER THAN TRANSLATED, ON BOTH SIDES.
 *   `Library/Components/Modules/ModuleController.vb:L54` built the object and L66-L72 then assigned each
 *   column through `Convert.ToInt32(Null.SetNull(dr("Column"), currentValue))`, one statement per
 *   column. The server's object-relational materialiser replaced that outright, and no client-side
 *   analogue is introduced here: this store assigns whatever the contract declares, field for field, and
 *   contains no hydration helper, no column reader and no sentinel substitution.
 *
 * MIGRATION: THE LEGACY CACHING LAYER IS NOT REPRODUCED ON THE CLIENT, AND THE TEMPTATION IS LARGEST IN
 *   THIS FEATURE. The 317-line `Library/Components/Providers/Caching/DataCache.vb` was reached from 116
 *   sites in scope, of which `ModuleController.vb` alone accounted for 21 - the single largest share -
 *   with the timeout-times-performance-multiplier idiom at L998, L1052, L1264 and L1355, and coarse
 *   portal-wide and host-wide clears. None of it is ported: there is no cache map, no expiry, no
 *   time-to-live, no staleness flag and no key vocabulary in this file, and no method clears a cache.
 *   Note the trap this note exists to disarm: `cacheTime` and `defaultCacheTime` are DOMAIN DATA that
 *   this store holds and a screen displays. They are NOT instructions for this store to implement
 *   caching.
 *
 * MIGRATION: THE LEGACY MODULE-LOADER INFRASTRUCTURE IS EXCLUDED WHOLESALE. `PortalModuleBase.vb` (881
 *   lines), `PaWriter.vb` (563), `PaFileInfo.vb` (87) and `EventMessageProcessor.vb` (125) have no
 *   counterpart. Module registration and lifecycle survive as a SERVER-side domain concern behind an
 *   injected factory; the Web Forms control-loading mechanism does not survive at all. Nothing here
 *   activates a stored type name, loads a component dynamically, or reads a business-controller class.
 *   The five legacy reflective activation sites were ordinary .NET reflection rather than COM
 *   interoperation, so the exclusion covering COM, VB6 and ActiveX removes nothing from this codebase
 *   and is reported as vacuous rather than claimed as work.
 *
 * MIGRATION: AUTHORISATION IS DECIDED BY THE SERVER AND IS NEVER PRE-EMPTED HERE. A well-formed update
 *   can legitimately answer `403` - the server enforces a rule of its own on the all-pages flag - and
 *   this store surfaces that refusal rather than anticipating it. Duplicating a server rule on the
 *   client would give an HTTP caller a different answer from every other caller, and the two copies
 *   would drift. No permission key decides anything in this file, and the two closed vocabularies the
 *   migration keeps apart - the persisted permission keys and the policy names - appear in neither form.
 *
 * MIGRATION: A REFUSAL IS WARNING SEVERITY, NOT ERROR SEVERITY. The legacy precedent is
 *   `Website/admin/Security/AccessDenied.ascx.vb`, a fifty-line page that performs NO permission check
 *   of its own and renders both of its branches, at L43 and L45, as a yellow warning rather than a red
 *   error. Severity is resolved by delegating to `problemSeverity` inside
 *   `core/utils/form-errors.util.ts`, which already implements that mapping and reports the measured
 *   legacy distribution behind it. Re-implementing the mapping here would create a second copy to drift.
 *
 * MIGRATION: NO WILDCARD IS APPENDED TO A FILTER ON THE CLIENT. The legacy readers decorated the pattern
 *   at the call site and matched from the start of the value rather than anywhere within it; the
 *   migration moved that decoration behind the repository interfaces where it belongs. The free-text
 *   filter is held here exactly as the caller supplied it, and match semantics are neither restated nor
 *   relied upon.
 *
 * MIGRATION: LOCALISATION IS NOT PORTED. The legacy resource mechanism was Web Forms specific and no
 *   translation runtime is added to this workspace. Every user-facing string reaching a screen comes
 *   from the shared error utilities, whose wording is taken verbatim from the legacy resource files;
 *   this store authors no message text of its own.
 *
 * MIGRATION: EVERY IMPLICIT COERCION THE LEGACY CODE RELIED ON IS MADE EXPLICIT. The thirty-nine
 *   administration code-behinds compiled with Option Strict OFF (`Website/release.config:L125` declares
 *   `<compilation debug="false" strict="false">`), which is what permitted the live double cast at
 *   `Export.ascx.vb:L157`. Strict TypeScript is what forces such a coercion to surface, and there is
 *   none in this file: the exported document is declared as text and held as text, every identifier is a
 *   number, every flag is a boolean, and nothing is narrowed implicitly.
 *
 * The repository-root `MIGRATION_NOTES.md` is owned by another agent and is deliberately not edited from
 * here; these inline notes are this file's mechanism for recording its own divergences.
 */

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
 * Reported when a module read answers with a record other than the one that was asked for.
 *
 * A refusal rather than a silent discard, because this outcome means the transport, a proxy or the
 * server disagreed with this store about which record was requested, and an operator who sees nothing at
 * all would simply try again. It carries no identifier: the mismatch is a fault in the exchange rather
 * than something the person at the keyboard did, and naming records here would put one tenant's key in
 * front of whoever triggered the read.
 */
const MISMATCHED_MODULE_MESSAGE =
  'The server answered with a different module from the one requested. Nothing was loaded.';

/**
 * The hard ceiling on how many pages one complete choice-set walk will request.
 *
 * A walk driven by a short page and by the server's own record total needs no ceiling to terminate
 * under correct behaviour, and this exists ONLY for the case where both of those assumptions fail at
 * once: a server that kept answering with full pages and an ever-growing total would otherwise have
 * this client request pages until the tab died. At {@link MAX_PAGE_SIZE} rows a page it admits fifty
 * thousand placements in one tenant, which is orders of magnitude beyond anything a DotNetNuke
 * installation holds.
 *
 * ⚠ REACHING IT IS A FAILURE, NOT AN ANSWER. {@link ModuleStore.loadChoices} raises rather than
 * publishing what it has, because a choice set that quietly omits choices is the exact defect the walk
 * was introduced to remove — and an escape hatch that reintroduced it would be worse than no ceiling at
 * all.
 */
export const MAXIMUM_CHOICE_PAGES = 500;

// =====================================================================================================
// SERVICE ARGUMENT TYPES, DERIVED RATHER THAN IMPORTED OR RESTATED
// =====================================================================================================
//
// The transport's paging, filtering and placement arguments are declared by the query-parameter helper
// module under `core/utils/`, which owns the spelling of every query key. This file must not import that
// module - assembling a query is the transport's concern, and importing its registry here would blur
// that line - and it must not restate the shapes either, because a second declaration is a second thing
// to drift.
//
// Both constraints are satisfied by DERIVING each argument type from the method that consumes it. The
// aliases below carry no declaration of their own: they resolve to whatever the transport's signature
// says today, so a change to that signature surfaces here as a type error rather than as a silently
// stale duplicate. Nothing is imported from the parameter registry and nothing is re-declared.

/**
 * The paging, ordering and free-text arguments of the module listing.
 *
 * Every member is optional and nullable, because an omitted member is not transmitted and the server
 * then applies its own default. Resolves to the transport's own declared parameter type.
 */
type ModuleListQuery = Parameters<ModuleService['listModules']>[0];

/**
 * The page and recycle-bin restrictions the module listing accepts alongside the paging arguments.
 *
 * `NonNullable` strips the `null | undefined` that the transport admits on this optional parameter, so
 * this alias names the filter object itself.
 */
type ModuleListFilterState = NonNullable<Parameters<ModuleService['listModules']>[1]>;

/**
 * The selector that narrows a module operation to ONE of its placements.
 *
 * A module whose all-pages flag is set has one placement per page of the portal, so the module
 * identifier alone does not name a single row. Supplying this selector picks one; omitting it addresses
 * the module itself. The two are materially different operations and this store never blurs them with a
 * default.
 */
type ModulePlacement = NonNullable<Parameters<ModuleService['getModule']>[1]>;

// =====================================================================================================
// THE PAGE TREE NODE
// =====================================================================================================

/**
 * One node of the page hierarchy that {@link ModuleStore.tabTree} derives from the flat page list.
 *
 * MIGRATION: THIS SHAPE IS DECLARED HERE BECAUSE THE MODELS LAYER DELIBERATELY HAS NO TREE TYPE.
 *   `core/models/tab.model.ts` declares exactly three page shapes - a list row, a detail and an update
 *   request - and states that it is type-only, imports nothing, and that "nothing here walks a
 *   hierarchy". A tree node is not a wire shape: no endpoint produces one, because there is no tree
 *   endpoint. It is therefore a shape of THIS store, declared in this file, and the models layer is left
 *   untouched. It is exported from here rather than kept private so that a consuming component can
 *   annotate the nodes it renders; an unexported type on a public signal would force every consumer to
 *   recover the name through a type query.
 */
export interface TabTreeNode {
  /** The page this node stands for, exactly as the listing returned it. */
  readonly tab: TabListItem;

  /**
   * The depth of this node within the tree THIS store built, where a root node is zero.
   *
   * Derived from the traversal rather than copied from {@link TabListItem.level}, so that it always
   * agrees with the structure below it. The server's own depth remains available and unaltered on
   * {@link TabTreeNode.tab}; the two can legitimately differ when the server's value was computed
   * against a parent chain this response does not contain, and in that case the traversal depth is the
   * one an indented list must use.
   */
  readonly depth: number;

  /** This node's children, ordered by page order. Empty for a leaf - never absent. */
  readonly children: readonly TabTreeNode[];
}

/**
 * The node under construction, whose child list is still being appended to.
 *
 * Structurally assignable to {@link TabTreeNode} because a mutable array is assignable to a readonly
 * one, which is what lets the builder append during traversal and publish the finished node without a
 * second pass or a cast.
 */
interface TabTreeNodeBuilder {
  readonly tab: TabListItem;
  readonly depth: number;
  readonly children: TabTreeNode[];
}

/**
 * The derived page hierarchy, together with every page the hierarchy could not place.
 *
 * Returned as one value so that the two can never disagree: they are produced by a single pass over a
 * single snapshot of the flat list.
 */
export interface TabHierarchy {
  /** The root-level pages, ordered by page order, each carrying its descendants. */
  readonly roots: readonly TabTreeNode[];

  /**
   * Every page that could not be attached to a root, in the order the listing returned it.
   *
   * MIGRATION: SURFACED RATHER THAN DISCARDED. A page lands here for one of exactly two reasons: it
   *   names a parent that is not present in this response - which is ordinary when a caller holds a
   *   partial list or when a parent is filtered out - or it participates in a parent cycle, which is
   *   malformed data. Silently dropping either would make a page vanish from an administration screen
   *   with nothing to explain it, so both are reported. A consumer may render them as a flat remainder.
   */
  readonly orphans: readonly TabListItem[];
}

// =====================================================================================================
// FAILURE STATE
// =====================================================================================================

/**
 * Which command a failure came from.
 *
 * Held alongside the failure so that a screen showing several affordances at once - a listing, a
 * settings panel and a transfer panel on one route - can attribute a refusal to the affordance that
 * caused it rather than raising one banner for the whole page. The union is closed and every member
 * corresponds to exactly one command method below.
 */
export type ModuleStoreOperation =
  | 'listModules'
  /**
   * The PICKER-CHOICE read, which is a different operation from the browsable listing even though
   * the two reach the same endpoint.
   *
   * MIGRATION: {@link ModuleStore.loadChoices} used to record its failures as `listModules`, and
   * that misattribution had consequences in both directions. A screen filtering the failure slot
   * for its own work could not tell a picker read it had started from a grid read a sibling screen
   * had started, so the transfer screens surfaced a refusal belonging to somebody else's listing;
   * and a listing screen showed a refusal raised by a picker it knows nothing about. The two reads
   * already have separate slices and separate handles, and this is the third member of that set.
   */
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
 * One failure, resolved once and held structurally.
 *
 * MIGRATION: THE STORE HOLDS THE STRUCTURED PROBLEM AND NEVER PRE-RENDERED MARKUP. The server answers
 *   with an RFC 7807 document under `application/problem+json`, and every string a person reads is
 *   produced by `core/utils/form-errors.util.ts`, whose output is plain text with legacy break markup
 *   already resolved. Nothing here builds HTML, marks a value trusted, or stores a sanitiser result: the
 *   in-scope legacy resource files carry 76 values containing HTML - including a live script block - so
 *   binding any of this as markup would be a live injection vector. The legacy precedent is
 *   `Website/admin/Security/AccessDenied.ascx.vb:L43`, which HTML-encoded the untrusted message before
 *   displaying it.
 */
export interface ModuleStoreFailure {
  /** The command that failed. */
  readonly operation: ModuleStoreOperation;

  /**
   * The problem document as the server sent it, or `null` when the response carried none.
   *
   * MIGRATION: RETAINED WHOLE, AND `traceId` IS THE REASON. The document's trace identifier is derived
   *   server-side from the ambient activity or the request identifier, so the correlation value the
   *   application sends on every request round-trips back into this body. It is the ONLY join key
   *   between what a person saw in the browser and what the server logged, and it matters most on the
   *   transfer paths, which are the hardest to reproduce. Its per-field dictionary is likewise kept
   *   intact: those keys are .NET model-state keys and are NOT camel-cased, and because the dictionary
   *   is an index signature under `noPropertyAccessFromIndexSignature`, a consumer reads an entry with
   *   an index expression - `problem.errors?.['ModuleTitle']` - and never with a property access.
   */
  readonly problem: ProblemDetails | null;

  /**
   * Everything the presentation layer needs about the failure, resolved once.
   *
   * Carries the severity, the title, one sentence, the per-field and form-level messages and the support
   * reference. The severity mapping - notably that a refusal is a WARNING rather than an error - is the
   * utility's, not this file's.
   */
  readonly summary: ProblemSummary;

  /**
   * The failure code the server published, or `null` when the document carried none.
   *
   * MIGRATION: THE CODE IS THE SERVER'S SPELLING, NOT THE LEGACY ENUMERATION MEMBER NAME, AND THAT
   *   CORRECTION IS LOAD-BEARING. The migration requirement asks that the legacy transfer refusals be
   *   surfaced verbatim, and they are - but verbatim applies to the WORDING, which
   *   `core/utils/form-errors.util.ts` preserves from the legacy resource files, not to the KEY. That
   *   module records the measured finding directly: the legacy PascalCase names "could never match a
   *   value taken off the wire", so every lookup keyed on them "returned null and the tables were
   *   unreachable in practice". The server publishes its own vocabulary inside the document's `type`
   *   member, and the three transfer refusals arrive as `module.content_invalid` for the legacy
   *   `NotValidXml`, `module.content_type_mismatch` for `NotCorrectType` and `module.not_portable` for
   *   `ImportNotSupported`; the reserved-page-name refusal arrives as `tab.name_reserved` for the legacy
   *   `InvalidTabName`. No PascalCase name is hard-coded anywhere in this file, and the code is read
   *   with the utility's own parser rather than by matching a prefix here.
   */
  readonly code: string | null;
}

// =====================================================================================================
// PURE HELPERS
// =====================================================================================================
//
// Free functions rather than methods: none of them reads or writes a signal, so keeping them outside the
// class states that fact structurally and lets each be reasoned about on its own.

/**
 * Reads one member of an unknown value without asserting anything about the whole.
 *
 * The transport failure that reaches a subscriber is deliberately typed `unknown`: this file must not
 * import the framework's HTTP module, and the interceptor that owns transport narrowing re-throws the
 * original failure rather than a translated one. Reading structurally is therefore the only way to
 * reach the response body, and widening every read to `unknown` is what forces the caller to narrow it.
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
 * Extracts the RFC 7807 document from a failed request, or synthesises the minimum from its status.
 *
 * ⚠ THE TRANSPORT STATUS IS RESOLVED FIRST, AND A STATUS OF ZERO SHORT-CIRCUITS BEFORE THE BODY IS
 * LOOKED AT. That ordering is a correctness requirement, and getting it wrong produced incoherent
 * wording rather than an obvious fault.
 *
 * A status of zero means NO RESPONSE ARRIVED - the server was unreachable, the request was blocked, the
 * connection was cut. In that case the framework puts a DOM `ProgressEvent` in the body slot, and a
 * `ProgressEvent` carries a string `type` member. The narrowing predicate is deliberately permissive
 * about absence, because any subset of the standard members is a legal problem document - so it ACCEPTS
 * that progress event, and a body-first reading committed it as though the server had answered with a
 * document whose `type` happened to be `"error"`. Everything downstream then resolved against a document
 * that was never sent: the failure code parser read `"error"` as a candidate failure type, and the
 * severity and the sentence were chosen for a response that did not exist. An unreachable server was
 * reported as though it had refused something.
 *
 * MIGRATION: `core/state/portal.store.ts` already resolved this correctly and its own note explains the
 * same hazard; `core/interceptors/error.interceptor.ts` applies the same ordering. This function is
 * aligned to them rather than to a fourth reading, so all three agree about what a status of zero means.
 *
 * The remaining cases, in order once zero is excluded. The response BODY is examined before the failure
 * object itself, because a failure object carries a numeric status of its own and would otherwise be
 * narrowed as though IT were the document, discarding the real one. A textual body is parsed, because a
 * server that answers with a problem document under a media type the client did not parse leaves it as a
 * string. Failing both, a document carrying the status ALONE is synthesised, which invents nothing - the
 * status is what the server sent - and is what lets severity and wording still resolve correctly, so
 * that a refusal is still presented as a refusal when its body was empty.
 *
 * @param cause The value a subscriber's error callback received.
 * @returns The problem document, or `null` when neither a document nor a status could be read.
 */
function problemFromCause(cause: unknown): ProblemDetails | null {
  const status: unknown = readMember(cause, 'status');

  // The unreachable-server case, composed through the SHARED transport helper rather than published
  // as a bare status.
  //
  // ⚠ IT USED TO RETURN `{ status: 0 }`, AND THAT LEFT THE BANNER WITHOUT A TITLE. Measured on the
  // module listing with the network unreachable: the banner rendered its severity word and its
  // message, but `.error-banner__title` matched NOTHING — an unfilled Angular anchor sat where the
  // title belongs — because a document carrying only a status has no title to render. The portal
  // store already composed this case through `transportProblem`, so the same offline failure was
  // presented as `Error / Network error / The server could not be reached…` on one screen and with the
  // title silently missing on another. One helper for one condition removes the divergence, and it
  // invents nothing: every string it returns is derived from the status, which is what the transport
  // reported.
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
 * Separated from {@link problemFromCause} so that the failure of a parse is contained to one expression.
 * A body that is not JSON at all is an ordinary outcome rather than a fault - an infrastructure device
 * can answer with plain text - so the parse failure is swallowed HERE and only here, and the caller
 * continues to the status fallback.
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
 * Whether a page sits at the root of the hierarchy.
 *
 * MIGRATION: THE ROOT TEST IS EXPLICIT, AND IT ADMITS BOTH THE WIRE FORM AND THE LEGACY FORM. This is
 *   the single most defect-prone decision in this file, and the two authorities genuinely disagree, so
 *   both are honoured.
 *
 *   The WIRE form is `null`, and `core/models/tab.model.ts` is unambiguous about it: "A ROOT-LEVEL PAGE
 *   ARRIVES AS `parentId: null`, NEVER AS `-1` ... The backend converts that sentinel to a genuine null
 *   at the boundary, because `-1` is simultaneously a legitimate tenant identifier and the two were
 *   otherwise indistinguishable on the wire ... Test the root case with `parentId === null`." Both page
 *   shapes accordingly declare `parentId: number | null`.
 *
 *   The LEGACY form is `-1`, and that is measured: `Library/Components/Tabs/TabInfo.vb:L91` seeds
 *   `_ParentId = Null.NullInteger`, `Library/Components/Tabs/TabController.vb:L1032` and `:L1074` both
 *   assign `objTab.ParentId = Null.NullInteger` at the point where a page is being made root-level, and
 *   `Library/Components/Shared/Null.vb:L41-L45` defines that marker as `-1`.
 *
 *   Admitting both is not hedging. Testing `-1` ALONE against the current contract would put EVERY root
 *   page into the orphan set and render an EMPTY tree - a total failure behind a successful response -
 *   while testing `null` alone would silently re-parent a root page to the orphan set if a legacy-shaped
 *   payload ever reached this code. Neither test can be wrong about the case that matters most: a parent
 *   of ZERO is a REAL parent under both encodings, because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`
 *   (`01.00.00.SqlDataProvider:L140`), and both comparisons are exact equalities that a zero survives.
 *   A truthiness test, a `<= 0` test or a loose null comparison would each re-parent every child of page
 *   zero to the root, which is exactly the silent defect this function exists to make impossible.
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
 * The exact complement of {@link isRootParent}, expressed as a type predicate so that the builder can use
 * the parent reference as a map key without a cast or a non-null assertion. The narrowing is SOUND rather
 * than convenient: the only non-numeric value the member admits is `null`, and {@link isRootParent}
 * excludes it, so whenever this returns true the value genuinely is a number.
 *
 * Declared as a companion rather than by inlining the comparison, so that the root test itself lives in
 * exactly one place and cannot be changed in one branch and not the other.
 *
 * @param parentId The page's parent reference, as the contract declares it.
 * @returns True when the reference names a parent page.
 */
function isNestedUnder(parentId: number | null): parentId is number {
  return !isRootParent(parentId);
}

/**
 * Orders two pages as siblings.
 *
 * Page order is the primary key, exactly as the legacy hierarchy ordered itself. The page identifier
 * breaks a tie so that the result is TOTAL and therefore stable: two siblings sharing a page order is
 * ordinary in this schema, and without a tiebreak their relative position would depend on the order the
 * server happened to return them in, making a screen appear to reshuffle between reads.
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
 * MIGRATION: DERIVED ON THE CLIENT BECAUSE THERE IS NO TREE ENDPOINT. The page surface is closed at
 *   three operations - read a portal's pages, read one page, replace one page - and `tab.service.ts`
 *   states that tree building "belongs to `core/state/*.store.ts` or the consuming component". The list
 *   is UNPAGED for exactly this reason, which that model records: "A hierarchy is read whole because a
 *   partially fetched tree cannot be indented correctly."
 *
 * The traversal is ITERATIVE and guarded, not recursive. A page names one parent, so a cycle can only
 * form among pages unreachable from any root; expanding only pages not yet placed therefore terminates
 * on any input whatsoever, including a self-parenting page or a mutual pair, and cannot exhaust the call
 * stack on a deep hierarchy. Whatever the walk does not reach is reported rather than dropped: a page
 * naming an absent parent and a page caught in a cycle are both ordinary enough to surface and neither
 * is ever discarded silently.
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
      // The parent reference is a real identifier here, narrowed by the predicate rather than asserted.
      // It is looked up later rather than assumed present: a caller can legitimately hold a list whose
      // parents were filtered out.
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
  // reported above. Reported in listing order, and never dropped. A set of the identifiers already
  // reported keeps this pass linear rather than quadratic in the number of unplaceable pages.
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
 * Module-administration state for the module feature screens.
 *
 * Registered at the application root so that a listing, a form, a settings panel and the two transfer
 * panels share one selection and one view of the catalogue. NO component declares a provider for it: a
 * component-level provider would give each route its own copy and the page tree would be re-fetched on
 * every navigation.
 *
 * MIGRATION: SIGNALS ARE THE STATE MECHANISM, AND NOTHING ELSE IS. Writable slices are `signal`, derived
 *   views are `computed`, and every public projection is `asReadonly` so that a consumer can observe the
 *   state without being able to write it. `effect` is deliberately absent: this store performs no side
 *   effect that a command method does not already perform explicitly, and an effect that issued a request
 *   would make the request order depend on change detection. There is no external store library, no
 *   subject of any kind standing in for state, and no event emitter: those are the mechanisms this
 *   migration replaces, not mechanisms it adopts.
 *
 * MIGRATION: EVERY UPDATE IS IMMUTABLE, which is what makes a consumer using change detection on push
 *   re-render. Note that the identity comparison used to replace a row is an exact `===` on a value that
 *   can legitimately be ZERO, and that the row key is the PLACEMENT rather than the module: a module
 *   whose all-pages flag is set contributes one row per page, all sharing one module identifier, so
 *   keying by module would collapse those rows onto one another.
 */
@Injectable({ providedIn: 'root' })
export class ModuleStore implements OnDestroy {

  private readonly moduleService = inject(ModuleService);
  private readonly tabService = inject(TabService);

  // ---------------------------------------------------------------------------------------------------
  // IN-FLIGHT REQUEST HANDLES
  // ---------------------------------------------------------------------------------------------------
  //
  // ⚠ ONE HANDLE PER READ SLICE, AND STARTING A READ CANCELS THE PREVIOUS ONE FOR THAT SLICE.
  //
  // Before these existed every request in this store was fire-and-forget, and two consequences
  // followed. The first is a stale-response hazard: a screen reached for module A, navigated to module
  // B on the same route, and whichever response returned LAST won — so B's screen could be showing A's
  // record while the route said B, and a save from that screen would then combine B's route key with
  // A's form data. The second is that a session teardown could not actually take effect, because a read
  // still in the air would repopulate the slices immediately after they were cleared.
  //
  // The pattern is the one `core/state/portal.store.ts` and `core/state/user.store.ts` already
  // establish, adopted rather than reinvented: unsubscribe before restarting, so the superseded
  // response is never delivered at all. Cancellation is the strongest available fix for a read because
  // it removes the possibility of a late commit rather than testing for one — a read has no server-side
  // consequence, which is what makes abandoning it safe.
  //
  // EVERY read slice carries a handle, including the three that address no record parameter - the
  // definition catalogue's two lists and the single definition. Those three cannot suffer the
  // wrong-record hazard above, so a handle earns its place there for the second reason alone: "this read
  // names no record" does not mean "this read is safe to let land after sign-out", and without a handle
  // teardown would have no way to stop it.
  //
  // ⚠ THE INVARIANT, STATED ONCE AND HELD BY EVERY READ METHOD BELOW: exactly ONE pre-dispatch
  // cancellation per method, and it releases ONLY the handle that method is about to assign.
  //
  // MIGRATION: neither half of that was true. Six methods released their own handle TWICE - inert in
  // effect, but it stated the rule twice and invited a reader to believe the second release was doing
  // something the first had not. Two methods released a handle they do not own: `loadDefinition` and
  // `loadDesktopDefinitions` each began by releasing `definitionsRequest`, the CATALOGUE read that
  // neither of them performs. That was a real defect rather than an untidiness. The three definition
  // reads serve different screens, so reading one definition abandoned a catalogue read a form was
  // waiting on - and a cancelled subscription delivers NEITHER a value NOR an error, so the abandoned
  // screen was left holding an empty catalogue with no failure to explain it and no request in flight
  // to finish it. The whole point of a per-slice handle is that one slice cannot do this to another.
  //
  // Releasing another slice's handle is therefore forbidden anywhere except the two members whose job
  // it explicitly is: {@link ModuleStore.cancelInFlight}, which releases everything for teardown, and
  // the `clear*` members, each of which releases the one handle feeding the slice it discards.

  private listRequest: Subscription | null = null;
  private moduleRequest: Subscription | null = null;
  private settingsRequest: Subscription | null = null;
  private definitionsRequest: Subscription | null = null;
  private definitionRequest: Subscription | null = null;
  private desktopDefinitionsRequest: Subscription | null = null;
  private tabsRequest: Subscription | null = null;

  /**
   * The export in flight, held so that closing the transfer panel can release it.
   *
   * An export is a POST and is therefore also tracked as a write, which is what keeps teardown able to
   * reach it. This second handle exists because closing the panel abandons THIS operation specifically,
   * and releasing every write to do it would be far too broad.
   */
  private exportRequest: Subscription | null = null;
  /**
   * The picker-choice read in flight, or null when none is.
   *
   * Held separately from {@link ModuleStore.listRequest} because the two reads answer different
   * questions over the same endpoint: the listing is the paged grid somebody is reading, the choices
   * are the whole tenant's modules used to populate a picker. Sharing one handle would let a picker
   * read abandon a grid read that a different screen was waiting on.
   */
  private choicesRequest: Subscription | null = null;

  /*
   * A container for WRITES, which are deliberately never cancelled to supersede one another.
   * Abandoning a write client-side does not undo it server-side, so cancelling one would leave this
   * store confident about a change it can no longer observe. Concurrency is instead surfaced through
   * the saving flags, which a form binds to disable its own submit. The container exists only so that
   * teardown can release anything still outstanding; a settled write detaches itself from it.
   *
   * A SET, NOT AN RXJS `Subscription` CONTAINER, and the distinction is load-bearing rather than
   * stylistic. An rxjs container is single-use: once `unsubscribe` has been called on it, it is closed
   * for good, and every handle added afterwards is unsubscribed the instant it arrives. This store is
   * root-provided and therefore OUTLIVES the session it was serving, so `reset` runs on sign-out and
   * the SAME instance then serves the next account. With an rxjs container, the first sign-out would
   * silently break every subsequent write - each one cancelled at birth, the server never called, the
   * saving flag never cleared - and nothing would report it. A set is cleared and reused instead, which
   * is the pattern `core/state/portal.store.ts:L473` already establishes.
   */
  private readonly writeRequests = new Set<Subscription>();

  // ---------------------------------------------------------------------------------------------------
  // OPERATION GENERATIONS
  // ---------------------------------------------------------------------------------------------------
  //
  // ⚠ THE SECOND LAYER, AND IT GUARDS THE COMMIT RATHER THAN THE TRANSPORT.
  //
  // The handles above cancel a superseded request so its answer is never delivered. That is the stronger
  // fix and it closes the common case, but it does not cover every way a delivered answer can stop being
  // the one that is wanted:
  //
  // - Clearing a slice, or purging this store when a session ends, changes what a pending response MEANS
  //   without there being a newer request whose dispatch would have cancelled it.
  // - A screen holds several of these slices at once, started by different commands. Cancelling the
  //   module read does not invalidate a settings read that is already scheduled to commit.
  //
  // So each of the three record-addressed slices carries a ticket taken at dispatch and re-checked at
  // the commit. One per slice rather than one for the store, so that reading the settings cannot
  // invalidate a module read that is still legitimately wanted.
  //
  // The mechanism is `core/utils/operation-generation.util.ts`, which is the same shape as the session
  // generation in `core/services/token-storage.service.ts` and the phase ticket in
  // `core/state/auth.store.ts` - adopted rather than reinvented.

  /** Guards the loaded module against a superseded or abandoned read. */
  private readonly moduleReads = new OperationGeneration();

  /** Guards the settings bag. */
  private readonly settingsReads = new OperationGeneration();

  /** Guards the exported document, whose delivery is the most consequential commit in this store. */
  private readonly exportOperations = new OperationGeneration();

  // ---------------------------------------------------------------------------------------------------
  // WRITABLE SLICES
  // ---------------------------------------------------------------------------------------------------

  /** The current page of the module listing. Seeded with a client-side empty page. */
  private readonly _page = signal<ModuleListPage>(emptyPagedResult<ModuleListItem>());

  /**
   * The paging, ordering and free-text arguments the next listing request will carry.
   *
   * MIGRATION: THE PAGE INDEX HELD HERE IS ZERO-BASED, WHICH IS THE WIRE'S CONVENTION. The transport
   *   performs no arithmetic on it in either direction and states that "the mapping between the two
   *   lives in the feature store", so this member is the server's coordinate and a pager may present a
   *   one-based counter over it. The legacy screens carried a one-based counter and subtracted one at
   *   the call site; that subtraction survives only as a presentation concern and is not performed here.
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
   * Modules read as CHOICES for a picker, held apart from the browsable listing.
   *
   * ⚠ THIS SLICE EXISTS SO THAT A PICKER CANNOT DISTURB A LISTING. A screen that needs every module as
   *   options wants a different query from a screen that lets an operator page through them: the widest
   *   page the endpoint allows, unordered, unfiltered. Expressing that by moving
   *   {@link ModuleStore._query} would rewrite the browsable listing's page size and replace its rows,
   *   so merely opening a picker would silently resize a sibling listing under its own operator and lose
   *   the page they were on. The two reads are therefore two slices with two queries and two flags, and
   *   nothing a picker does is observable on {@link ModuleStore.page}.
   */
  private readonly _choices = signal<readonly ModuleListItem[]>([]);

  /**
   * How many modules the SERVER said match, which is not necessarily how many are in
   * {@link ModuleStore._choices}.
   *
   * ⚠ THIS EXISTS SO THAT AN INCOMPLETE WALK CANNOT LOOK COMPLETE. The walk in
   * {@link ModuleStore.loadChoices} gathers every page, so the two normally agree — and a consumer that
   * finds them disagreeing is looking at a set the page ceiling truncated, which is a fact about the
   * server rather than about the tenant. Publishing the row count here instead would make the two agree
   * by construction and destroy the only signal that anything was left out.
   *
   * It is NOT a paging coordinate and no pager is derived from it: this slice offers no page index and no
   * page size, because the walk has already been past every page there is.
   */
  private readonly _choicesTotalCount = signal(0);

  /** Whether a choices request is in flight. Distinct from the listing's flag, by design. */
  private readonly _choicesLoading = signal(false);


  /** The module currently being read or edited, or `null` when none has been loaded. */
  private readonly _module = signal<ModuleDetail | null>(null);

  /**
   * The module the screens are working with, or `undefined` when none is selected.
   *
   * MIGRATION: ABSENCE IS `undefined` ALONE - never zero and never minus one. `dbo.Modules.ModuleID` is
   *   `IDENTITY (0, 1)` (`01.00.00.SqlDataProvider:L221`), so module ZERO is the first module of an
   *   installation, and minus one is a value the legacy transfer screens genuinely held
   *   (`Export.ascx.vb:L49`, `Import.ascx.vb:L51`). Either value used as "nothing selected" would
   *   collide with a real module.
   */
  private readonly _selectedModuleId = signal<number | undefined>(undefined);


  /** Whether a single-module read is in flight. */
  private readonly _moduleLoading = signal(false);

  /** Whether a create, replace or remove is in flight. */
  private readonly _saving = signal(false);

  /**
   * The loaded settings, or `null` when none have been read.
   *
   * MIGRATION: TWO MAPS, HELD SEPARATELY AND NEVER MERGED. One is recorded against the module and is
   *   identical on every page it appears on; the other belongs to a single occurrence on a single page.
   *   The terminal schema keeps them in two tables that the upgrade chain maintains separately, and
   *   collapsing them into one map would destroy the distinction the migration exists to express.
   */
  private readonly _settings = signal<ModuleSettingsBag | null>(null);

  /** Whether a settings read is in flight. */
  private readonly _settingsLoading = signal(false);

  /** Whether a settings replacement is in flight. */
  private readonly _settingsSaving = signal(false);

  /** The definition catalogue. UNPAGED, so no paging state accompanies it. */
  private readonly _definitions = signal<readonly ModuleDefinition[]>([]);

  /** The definitions belonging to one deployed bundle. Also UNPAGED. */
  private readonly _desktopDefinitions = signal<readonly ModuleDefinition[]>([]);

  /**
   * One definition read on its own, or `null` when none has been read.
   *
   * Held apart from the catalogue rather than merged into it, because a single read answers for a
   * definition the catalogue may not contain - the catalogue is scoped to what the resolved tenant may
   * use, and a single read is not.
   */
  private readonly _definition = signal<ModuleDefinition | null>(null);

  /**
   * How many definition reads are outstanding: the catalogue, one bundle's definitions, and one
   * definition on its own.
   *
   * ⚠ A COUNT RATHER THAN A BOOLEAN, AND THE DIFFERENCE IS A DEFECT EITHER WAY IT IS GOT WRONG.
   * Three commands read definitions into three separate slices and all three report through the one
   * public flag below, because the only consumer asks a single question - "is a definition read
   * outstanding?" - and does not care which. With a boolean, whichever read answered FIRST cleared it
   * while the others were still on the wire, so the busy indicator disappeared and a form rendered as
   * though its catalogue had arrived, with nothing in it.
   *
   * A boolean can only be made honest by having each command cancel its siblings, and that cure is
   * worse: the three reads serve different screens, a cancelled subscription delivers NEITHER a value
   * NOR an error, and the store is provided at the application root - so cancelling a sibling left
   * another screen holding an empty slice with no failure to explain it and no request in flight to
   * finish it. Counting keeps every read's own handle private to the command that owns it, which is
   * the invariant stated in the handle block above, AND leaves the flag true until the LAST read has
   * settled. Nothing has to be cancelled for the flag to tell the truth.
   *
   * Held privately as a number and published as a boolean, so no caller can come to depend on the
   * count itself.
   */
  private readonly _definitionReadsInFlight = signal<number>(0);

  /**
   * The flat page list for the portal in scope. UNPAGED, so no paging state accompanies it.
   *
   * Held flat and turned into a hierarchy by {@link ModuleStore.tabHierarchy}, because the derivation
   * belongs in a `computed` rather than in a field a command method recomputes by hand.
   */
  private readonly _tabs = signal<readonly TabListItem[]>([]);

  /**
   * The portal whose pages are loaded, or `undefined` when none have been.
   *
   * MIGRATION: A PORTAL IDENTIFIER OF MINUS ONE **OR** ZERO IS REAL. `dbo.Portals.PortalID` is
   *   `IDENTITY (-1, 1)` (`01.00.00.SqlDataProvider:L77`), so the first portal is numbered minus one and
   *   the second is numbered zero. Absence is `undefined`.
   */
  private readonly _tabPortalId = signal<number | undefined>(undefined);

  /** The page the screens are working with, or `undefined` when none is selected. Page zero is real. */
  private readonly _selectedTabId = signal<number | undefined>(undefined);

  /** Whether a page-list read is in flight. */
  private readonly _tabsLoading = signal(false);

  /**
   * The exported document, or `null` when nothing has been exported in this session.
   *
   * MIGRATION: AN OPAQUE STRING, AND THE MOST UNTRUSTED VALUE THIS STORE HOLDS. It is module-authored
   *   content that arrived in a `200` response body. It is not parsed, not pretty-printed, not
   *   sanitised, not marked trusted and never bound as markup. An EMPTY document is a distinct outcome
   *   from no document at all - the legacy branch at `Export.ascx.vb:L159` tested `Content <> ""` - so
   *   the empty string and `null` are held apart rather than normalised into one another.
   */
  private readonly _exportedContent = signal<string | null>(null);

  /** Whether an export is in flight. */
  private readonly _exporting = signal(false);

  /** Whether an import is in flight. */
  private readonly _importing = signal(false);

  /**
   * Whether the most recent import succeeded.
   *
   * The import endpoint answers `204` with no body, so there is no payload to hold; this flag is the
   * outcome. Cleared by {@link ModuleStore.clearTransferOutcome} so that a screen does not report a
   * stale success.
   */
  private readonly _importCompleted = signal(false);

  /** The most recent failure, or `null` when the last command in each area succeeded. */
  private readonly _failure = signal<ModuleStoreFailure | null>(null);

  /**
   * How the SETTINGS read ended, retained independently of {@link ModuleStore._failure}.
   *
   * ⚠ THIS SLOT EXISTS BECAUSE THE SHARED ONE CANNOT ANSWER "WAS I ALLOWED TO READ THE SETTINGS", AND A
   *   SCREEN THAT GUESSED FROM THE SHARED SLOT GUESSED WRONG. The settings screen issues three reads —
   *   the module, its settings and its definition — and every command clears the shared slot as it
   *   starts, so whichever read finishes LAST owns it. Measured against a running server: an
   *   administrative module answers `200` for the module, `403 module.settings_protected` for the
   *   settings and `404` for the definition, so the definition's absence displaced the refusal, and the
   *   not-found branch of that screen's announcer then discarded the document in favour of a transient
   *   advisory. A full editable form rendered beneath no banner, offering a save the server had already
   *   refused.
   *
   *   A screen could not fix that for itself by capturing the shared value as it passed, and an earlier
   *   revision tried: the capture ran in a reactive effect, and whether the effect observed the refusal
   *   at all depended on whether the definition read was issued in the same flush — so the same code
   *   passed against a slow network and failed against a fast one, which is exactly the class of defect
   *   that reaches production intermittently. Writing this slot from the settings handler ITSELF removes
   *   the timing question: the fact is recorded where it is discovered and stays recorded until the next
   *   settings read replaces it.
   *
   * `null` means "the settings read has not failed" — either it succeeded, or it has not been issued.
   * Cleared when a settings read starts, when one succeeds, and by {@link ModuleStore.reset}, so it can
   * only ever describe the settings read a screen is currently looking at.
   */
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

  /** Every module read as a picker choice. See {@link ModuleStore._choices}. */
  readonly choices = this._choices.asReadonly();

  /**
   * How many modules the server reported for the picker. See {@link ModuleStore._choicesTotalCount}.
   *
   * Compare it against `choices().length` to learn whether the walk returned everything: equal means
   * complete, and a larger total means the page ceiling stopped the walk short.
   */
  readonly choicesTotalCount = this._choicesTotalCount.asReadonly();

  /**
   * Whether the picker holds every module the server reported.
   *
   * Offered so that a consumer does not have to compare a length against a total itself and get the
   * direction of the comparison wrong. True on a cold slice, because nothing has been reported missing.
   */
  readonly choicesComplete = computed<boolean>(() => this._choices().length >= this._choicesTotalCount());

  /** Whether a choices request is in flight. */
  readonly choicesLoading = this._choicesLoading.asReadonly();

  /**
   * How many modules the SERVER said the picker is choosing among.
   *
   * Published so a screen can state the size of the set it is offering. Read together with
   * {@link ModuleStore.choices}: the two agree after a completed walk, and a screen that ever
   * observes them disagreeing is looking at a walk that failed, in which case
   * {@link ModuleStore.failure} carries the reason and the choices slice has been cleared.
   */
  readonly choicesTotal = this._choicesTotalCount.asReadonly();

  /** The module currently loaded, or `null`. */
  readonly module = this._module.asReadonly();

  /** The selected module, or `undefined`. Test with `=== undefined`. */
  readonly selectedModuleId = this._selectedModuleId.asReadonly();


  /** Whether a single-module read is in flight. */
  readonly moduleLoading = this._moduleLoading.asReadonly();

  /** Whether a create, replace or remove is in flight. */
  readonly saving = this._saving.asReadonly();

  /** The loaded settings, or `null`. Its two maps are never merged. */
  readonly settings = this._settings.asReadonly();

  /** Whether a settings read is in flight. */
  readonly settingsLoading = this._settingsLoading.asReadonly();

  /** How the settings READ ended, independently of the shared slot. See {@link ModuleStore._settingsFailure}. */
  readonly settingsFailure = this._settingsFailure.asReadonly();

  /** Whether a settings replacement is in flight. */
  readonly settingsSaving = this._settingsSaving.asReadonly();

  /** The definition catalogue, unpaged. */
  readonly definitions = this._definitions.asReadonly();

  /** One bundle's definitions, unpaged. */
  readonly desktopDefinitions = this._desktopDefinitions.asReadonly();

  /** One definition read on its own, or `null`. Its `defaultCacheTime` is never merged with a
   * placement's `cacheTime`. */
  readonly definition = this._definition.asReadonly();

  /**
   * Whether ANY definition read is in flight - the catalogue, one bundle's, or one definition.
   *
   * Derived from {@link ModuleStore._definitionReadsInFlight} rather than mirroring one boolean, so it
   * stays true until the last outstanding read has settled. Read-only by construction: it is a
   * derivation, so there is nothing on it to set.
   */
  readonly definitionsLoading = computed<boolean>(() => this._definitionReadsInFlight() > 0);

  /** The flat page list, unpaged, in the order the listing returned it. */
  readonly tabs = this._tabs.asReadonly();

  /** The portal whose pages are loaded, or `undefined`. Minus one and zero are both real portals. */
  readonly tabPortalId = this._tabPortalId.asReadonly();

  /** The selected page, or `undefined`. Page zero is real. */
  readonly selectedTabId = this._selectedTabId.asReadonly();

  /** Whether a page-list read is in flight. */
  readonly tabsLoading = this._tabsLoading.asReadonly();

  /** The exported document as an opaque string, or `null`. The empty string is a distinct outcome. */
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

  /**
   * Whether any request this store issues is in flight.
   *
   * Offered so that a route can disable its whole form without enumerating seven flags. A screen that
   * needs to distinguish a listing refresh from a settings save reads the individual flags instead.
   */
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

  /**
   * The page hierarchy derived from the flat page list, with every unplaceable page reported.
   *
   * MIGRATION: A `computed` RATHER THAN A FIELD A COMMAND RECOMPUTES. The hierarchy is a pure function of
   *   the flat list, so deriving it reactively means it can never fall out of step with the list it came
   *   from - which a field assigned inside a command method can, the moment any other command touches the
   *   list. The root test and the traversal are documented on {@link buildTabHierarchy} and
   *   {@link isRootParent}; the short version is that the root test is an exact comparison admitting both
   *   the wire's `null` and the legacy `-1`, and that a parent of ZERO is a real parent under both.
   */
  readonly tabHierarchy = computed<TabHierarchy>(() => buildTabHierarchy(this._tabs()));

  /** The root-level pages with their descendants, ordered by page order at every level. */
  readonly tabTree = computed<readonly TabTreeNode[]>(() => this.tabHierarchy().roots);

  /** Every page the hierarchy could not place - an absent parent, or a parent cycle. Never discarded. */
  readonly orphanTabs = computed<readonly TabListItem[]>(() => this.tabHierarchy().orphans);

  /**
   * The definitions whose bundle supports content transfer.
   *
   * MIGRATION: `false` HERE IS DATA, NOT ABSENCE. The legacy sentinel helper reported `False` itself as
   *   "absent" (`Null.vb:L76-L80`, and its test at `:L208-L237` returns true for `False`), so the legacy
   *   code could not tell a module that does not support transfer from one whose support was unknown.
   *   Every flag on these contracts is a non-nullable boolean, so `false` means "does not support it" and
   *   nothing else. This filter therefore tests the flag directly and treats no value as missing.
   */
  readonly portableDefinitions = computed<readonly ModuleDefinition[]>(() =>
    this._definitions().filter((definition) => definition.isPortable),
  );

  /**
   * The selected page, resolved against the loaded page list, or `undefined` when it is not present.
   *
   * MIGRATION: the comparison is an exact `===` on a page identifier that can legitimately be ZERO
   *   (`01.00.00.SqlDataProvider:L140`), and the selection's absence is `undefined` alone.
   */
  readonly selectedTab = computed<TabListItem | undefined>(() => {
    const tabId: number | undefined = this._selectedTabId();

    if (tabId === undefined) {
      return undefined;
    }

    return this._tabs().find((tab) => tab.tabId === tabId);
  });


  // ---------------------------------------------------------------------------------------------------
  // SELECTION
  // ---------------------------------------------------------------------------------------------------
  //
  // MIGRATION: THESE REPLACE A PER-POSTBACK QUERY-STRING RE-READ, NOT VIEW STATE. The Modules tree
  //   contained ZERO view-state sites - `grep -ro 'ViewState(' Website/admin/Modules/` returns nothing -
  //   and zero session sites, so there is no round-tripped control state to eliminate here. What the
  //   legacy screens did instead was declare `Private Shadows ModuleId As Integer = -1`
  //   (`Export.ascx.vb:L49`, `Import.ascx.vb:L51`) and re-parse that identifier out of the request on
  //   every postback. A signal holds the selection ONCE, for as long as the screen needs it, and no
  //   round trip re-establishes it.

  /**
   * Selects the module the screens are working with, or clears the selection.
   *
   * @param moduleId The module to select. Pass `undefined` to clear. Zero and minus one are both real
   * values and neither clears the selection.
   */
  selectModule(moduleId: number | undefined): void {
    this._selectedModuleId.set(moduleId);
  }


  /**
   * Selects the page the screens are working with, or clears the selection.
   *
   * Selection alone does not restrict the listing; {@link ModuleStore.setTabFilter} does that.
   *
   * @param tabId The page to select. Pass `undefined` to clear. Page zero is real.
   */
  selectTab(tabId: number | undefined): void {
    this._selectedTabId.set(tabId);
  }

  // ---------------------------------------------------------------------------------------------------
  // LISTING ARGUMENTS
  // ---------------------------------------------------------------------------------------------------
  //
  // Each setter records the argument and nothing else. None issues a request, so a screen can compose a
  // page coordinate, an ordering and a filter and then read once - which is what stops a filter panel
  // from firing a request per keystroke.

  /**
   * Sets the zero-based page coordinate of the next listing request.
   *
   * @param pageIndex The page to request. ZERO IS THE FIRST PAGE and is an ordinary value.
   */
  setPageIndex(pageIndex: number): void {
    this._query.update((current) => ({ ...current, pageIndex }));
  }

  /**
   * Sets the page size of the next listing request and returns to the first page.
   *
   * The coordinate is reset because a page index measured in rows of one size does not identify the same
   * rows once the size changes, so keeping it would silently show a different window than the pager
   * claims.
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
   * MIGRATION: THE TEXT IS HELD EXACTLY AS SUPPLIED AND NO WILDCARD IS APPENDED. The legacy readers
   *   decorated the pattern at the call site and matched from the START of the value rather than anywhere
   *   within it; the migration moved that decoration behind the repository interfaces, where a change of
   *   match semantics is one server-side edit rather than an edit in every caller. Appending a wildcard
   *   here would both duplicate a server concern and silently change the semantics the server intends.
   *   The empty string and `null` are held apart and neither is normalised into the other, because the
   *   legacy string absence marker WAS the empty string (`Null.vb:L71-L75`, whose body is literally
   *   `Return ""`).
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
   * @param tabId The page to restrict to. Pass `undefined` to place no restriction. Page ZERO is a real
   * page and restricts the listing rather than clearing it.
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
   * MIGRATION: THIS IS THE ONLY WAY A REMOVED MODULE BECOMES VISIBLE, AND THERE IS NO WAY TO RESTORE ONE.
   *   Removal is soft, so the row survives with its marker set - which is what the legacy recycle bin at
   *   the legacy recycle-bin screen under `Website/admin/Tabs/` read - but the target exposes no restore
   *   endpoint and no bin endpoint, so this flag reveals such rows without offering to reverse them.
   *
   * @param includeDeleted Whether to include removed modules. `false` is transmitted as `false`; it is a
   * value rather than an absence.
   */
  setIncludeDeleted(includeDeleted: boolean): void {
    this._filter.update((current) => ({ ...current, includeDeleted }));
    this._query.update((current) => ({ ...current, pageIndex: 0 }));
  }

  // ---------------------------------------------------------------------------------------------------
  // READS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Reads the current page of the module listing.
   *
   * The tenant is NOT an argument: the server resolves it from the request host before dispatching, so
   * this store neither holds nor sends one.
   *
   * MIGRATION: THE LISTING CONTRACT IS NEW, BECAUSE THERE WAS NO LEGACY MODULE-LIST SCREEN TO PRESERVE.
   *   `ls Website/admin/Modules` yields only the export, import and settings screens. Every paging,
   *   ordering and filtering member here therefore comes from the transport's own signature rather than
   *   from a measured legacy contract, and none is borrowed from the portal or user screens, whose
   *   contracts are theirs.
   */
  loadModules(): void {
    // ⚠ EXACTLY ONE CANCELLATION, AND ONLY OF THIS METHOD'S OWN HANDLE. See the note on the
    // request-handle block: a doubled release was harmless in effect but stated the rule twice, and
    // a method that released a handle it does not own broke the rule outright.
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
   * Reads every module the endpoint will return in one call, as CHOICES for a picker.
   *
   * ⚠ THIS DOES NOT TOUCH THE BROWSABLE LISTING. Neither {@link ModuleStore.query},
   *   {@link ModuleStore.filter} nor {@link ModuleStore.page} is read or written here, so a screen that
   *   opens a picker cannot resize, re-order, re-filter or repaginate a listing a sibling screen is
   *   showing. That separation is the whole reason this command exists rather than callers moving the
   *   shared page size themselves.
   *
   * ⚠ EVERY PAGE IS WALKED, BECAUSE A PICKER OFFERS NO PAGER. This used to issue ONE request for the
   * widest page the endpoint accepts and publish its items as though they were the whole set. That is a
   * silent data loss rather than a smaller view, and the shape of the loss is what makes it serious: the
   * consumer is the import screen's target picker, so a tenant holding more placements than one page had
   * modules that simply COULD NOT BE CHOSEN as an import target — absent from the list, with no pager to
   * reach them, no indication that anything had been left out, and nothing an operator could do about it.
   * The endpoint's validator caps a page at {@link MAX_PAGE_SIZE}, so "every module" is not a request
   * this client can make; the pages are therefore walked and joined here.
   *
   * The previous prose called this "a documented limit of a picker rather than a silent truncation". It
   * was documented in this comment and nowhere the operator could see, which is what a silent truncation
   * is. The walk removes the limit, and {@link ModuleStore.choicesTotalCount} publishes the server's own
   * total so that any shortfall the guard below does cause remains visible.
   *
   * HOW IT TERMINATES, in three independent ways, so no server behaviour can leave it spinning:
   *
   *   1. A SHORT PAGE ends it — fewer records than were asked for is the last page by definition. This
   *      is the condition a correct server always reaches, and normally on the FIRST request, so the
   *      common case still costs exactly one round trip.
   *   2. THE SERVER'S OWN TOTAL ends it, once as many records have been gathered as the server said
   *      exist. Belt and braces against a server that padded a final page.
   *   3. THE PAGE CEILING ends it — see {@link MAXIMUM_CHOICE_PAGES}.
   *
   * ⚠ THE TOTAL PUBLISHED IS THE SERVER'S, NOT THE ROW COUNT, for the same reason the role walk
   * publishes the server's: reporting the rows gathered would make a truncated walk indistinguishable
   * from a complete one, which is the very defect this removes.
   *
   * Nothing is sorted, filtered or de-duplicated here. No narrowing is sent at all — a picker wants every
   * placement — and the pages are concatenated in request order, so the assembled order is the server's.
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
        // ⚠ NOTHING IS PUBLISHED FROM A FAILED WALK. The pages already gathered are discarded rather
        // than left standing, because a partial choice set is indistinguishable from a complete one on
        // screen — which is the whole reason the walk raises instead of truncating.
        this._choices.set([]);
        this._choicesTotalCount.set(0);
        this._choicesLoading.set(false);
        // ⚠ ATTRIBUTED TO THIS COMMAND, NOT TO THE LISTING. The picker read and the grid read share an
        // endpoint, but a failure slot names the COMMAND that failed rather than the route it addressed,
        // and the screens that filter on it have no other way to tell a picker read from a grid read.
        this.recordFailure('loadChoices', cause);
      },
    });
  }

  /**
   * Abandons the picker-choice read and returns its slice to rest.
   *
   * ⚠ THE LEASE A COMPONENT-SCOPED READ NEEDS FROM A ROOT-SCOPED STORE. This store outlives every
   * screen that reads it, and the choice slice is used by exactly one kind of screen: the transfer
   * panels, which open a picker and then navigate away. Without this member such a screen had no way
   * to release what it had started, and three consequences followed once it was destroyed - the
   * request continued to be paid for by the tenant with nobody to receive it; `_choicesLoading`
   * stayed true, so {@link ModuleStore.busy} reported the whole store busy on account of a screen
   * that no longer existed, disabling affordances on whatever screen replaced it; and a late refusal
   * landed in the failure slot addressed to a screen that had gone.
   *
   * Distinct from {@link ModuleStore.cancelInFlight}, which releases EVERY handle and belongs to
   * session teardown alone: a screen releasing its own read must not abandon reads its siblings are
   * waiting on.
   *
   * The retained choices are deliberately NOT discarded. They are a lookup rather than a selection,
   * so leaving them costs nothing and a screen re-entered before they go stale renders immediately;
   * what must not survive is the IN-FLIGHT state, which is what this releases.
   *
   * Idempotent: calling it with nothing outstanding releases nothing and writes the same value.
   */
  cancelChoices(): void {
    this.choicesRequest?.unsubscribe();
    this.choicesRequest = null;
    this._choicesLoading.set(false);
  }

  /**
   * Loads a portal's pages and then the modules placed on the selected one.
   *
   * MIGRATION: THIS IS THE SEQUENCING THE TRANSPORT LAYER DELIBERATELY DOES NOT DO. Both services are
   *   confined to API communication - "one method, one endpoint, one request" - and neither chains a
   *   second call onto the first. Ordering two requests is composition, so it lives here: the pages are
   *   needed before the listing can be restricted to one of them, and a screen that fired both at once
   *   could restrict the listing to a page that turned out not to exist.
   *
   * @param portalId The portal whose pages to read. MINUS ONE AND ZERO ARE BOTH REAL PORTALS
   * (`01.00.00.SqlDataProvider:L77`).
   * @param tabId The page to restrict the listing to, or `undefined` to list every placement in the
   * tenant. Page zero restricts rather than clears.
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
   * Reads a portal's pages without touching the listing.
   *
   * Offered separately from {@link ModuleStore.loadPortalScope} because a form needs the page tree in
   * order to render a parent picker even when no listing is on screen.
   *
   * MIGRATION: THE RESPONSE IS FLAT AND UNPAGED, AND IT IS READ WHOLE. `core/models/tab.model.ts`
   *   records why: "A hierarchy is read whole because a partially fetched tree cannot be indented
   *   correctly." No paging state accompanies this slice.
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
   * Reads one module in full and selects it.
   *
   * MIGRATION: A SUCCESSFUL READ ALWAYS CARRIES A MODULE. The endpoint answers `200` with the
   *   placement or refuses with a not-found problem document, so the absent case arrives at the
   *   error handler below and is announced. This handler used to admit a successful `null` and
   *   commit it, which published a blank record as though the read had succeeded — a state the
   *   server cannot produce and a screen cannot explain. The slice remains nullable because
   *   NOTHING SELECTED is a real state of this store; what is gone is the idea that the transport
   *   can report it.
   *
   * @param moduleId The module to read. Forwarded exactly as supplied; module zero is real.
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
        // ⚠ TWO INDEPENDENT CHECKS, AND NEITHER IS REDUNDANT.
        //
        // The ticket refuses an answer that is no longer wanted: a newer read started, the slice was
        // cleared, or the session was purged. It is the only one of the two that can tell a re-read of
        // the SAME module from the read it replaced, because both carry the same identifier.
        //
        // The identifier comparison refuses an answer that describes the wrong record - a mismatch that
        // would otherwise be committed silently and then be submitted back under the route's key,
        // overwriting a module the operator never opened.
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
   * Reads the whole definition catalogue.
   *
   * MIGRATION: UNPAGED, UNFILTERED AND READ-ONLY. The endpoint accepts no query parameter at all, so
   *   this store holds no page index, page size or total for it, and there is no write half: no method
   *   here adds, changes or removes a definition, because no such route exists. The legacy mechanism for
   *   that wrote archives to disk and reflected over the assemblies it found - `PaWriter.vb` and its
   *   companions - and is excluded wholesale.
   */
  loadDefinitions(): void {
    // ⚠ ONLY THIS METHOD'S OWN HANDLE, for the reason recorded on
    // {@link ModuleStore.loadDefinition}: the catalogue, the single definition and a bundle's
    // definitions are three separate slices with three separate handles, and a read of one must never
    // abort a read of another. The shared loading flag is a presentation detail and is not a licence
    // to share cancellation.
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

  /**
   * Reads one module definition on its own.
   *
   * Offered so that a form can resolve a single definition - most often for its
   * {@link ModuleDefinition.defaultCacheTime} - without loading the whole catalogue, and so that no
   * consumer has to reach past this store to the transport for the one operation the store would
   * otherwise leave unreachable.
   *
   * MIGRATION: THE PARAMETER SPELLING IS THE ROUTE'S, NOT THE RESPONSE MEMBER'S, AND THE TWO GENUINELY
   *   DIFFER. The endpoint is `GET /module-definitions/{moduleDefinitionId}`, spelled out in full, while
   *   the response contract abbreviates the same concept on its own member. The transport records that
   *   the two "are deliberately not unified by guesswork", each taken from its own declaration. This
   *   store therefore names its argument `moduleDefinitionId`, matching the route, and never reads the
   *   abbreviated member - the definition is held whole and a screen that needs the identifier reads it
   *   from the object it already has.
   *
   * MIGRATION: the definition's default cache period is a DIFFERENT FACT from a placement's cache
   *   period, and loading one here does not make it a fallback for the other. See the header note; the
   *   measured proof is `ModuleInfo.vb:L731` (`_CacheTime = 0`) against `:L759`
   *   (`_DefaultCacheTime = -1`).
   *
   * @param moduleDefinitionId The definition to read. Forwarded exactly as supplied. The identity behind
   * it seeds at 1, so no legal value coincides with a legacy sentinel - but it is still an opaque key and
   * is not compared against a bound.
   */
  loadDefinition(moduleDefinitionId: number): void {
    // ⚠ ONLY THIS METHOD'S OWN HANDLE. It previously released `definitionsRequest` as well - the
    // CATALOGUE read, which this method does not perform and does not own. Reading one definition
    // therefore abandoned a catalogue read a different screen was waiting on, and the abandoned
    // screen was left with an empty catalogue and no failure to explain it, because a cancelled
    // subscription delivers neither a value nor an error. The two reads are separate slices with
    // separate handles precisely so that neither can do this to the other.
    this.releaseDefinitionRead(this.definitionRequest);
    this.definitionRequest = null;
    this.beginDefinitionRead();
    this.clearFailure();

    this.definitionRequest = this.moduleService.getModuleDefinition(moduleDefinitionId).subscribe({
      // A successful read carries a definition: the endpoint answers `200` with it or refuses with a
      // not-found problem document, so an unknown definition reaches the error handler and is
      // announced rather than being committed as an empty success.
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
   * @param desktopModuleId The deployed bundle whose definitions to read. A path segment on the wire
   * rather than a query parameter, and forwarded exactly as supplied.
   */
  loadDesktopDefinitions(desktopModuleId: number): void {
    // ⚠ ONLY THIS METHOD'S OWN HANDLE, for the same reason given on
    // {@link ModuleStore.loadDefinition}: this method released the CATALOGUE handle too, so reading
    // one bundle's definitions silently abandoned a catalogue read belonging to another screen.
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
   * Creates a module placement.
   *
   * The request is transmitted WHOLE and is not inspected, filtered or defaulted here: it was assembled
   * and validated by the feature form, and the server applies its own rules. In particular a page
   * identifier of ZERO and a module order of MINUS ONE are both meaningful values on that contract - the
   * first names the first page of the installation, the second appends the module at the bottom of its
   * pane - and neither is rewritten.
   *
   * On success the created placement becomes the loaded module. The listing is deliberately NOT
   * re-read; see the block on that line for the measurement that removed it.
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
          // itself from its own address on entry, so two identical reads were issued for one create. They
          // did not merely duplicate work - they RACED, and this store serialises its listing reads by
          // abandoning the one in flight before starting the next, so the loser was cancelled mid-request.
          // Measured in a real browser on this exact path: `POST /api/v1/modules` answered 201, then
          // `GET /api/v1/modules?pageIndex=0&pageSize=10` was reported `net::ERR_ABORTED` with an XHR status
          // of 0 after 18 ms, and an IDENTICAL `GET` answered 200 exactly one millisecond later. An aborted
          // request still crossed the network, still cost a round trip, and still shows up in a browser's
          // network panel as a failure an operator or a reviewer has to rule out.
          //
          // The listing is the sole owner of listing reads: its page, its narrowing and its ordering all
          // live in its address, and it reads on entry and on every address change, so a caller arriving
          // there always sees authoritative rows and totals without this command asking for them too. The
          // created placement is published on the slots above, which covers the other direction - a screen
          // that is already mounted sees the new record immediately.
          //
          // ⚠ THE DELETE COMMAND KEEPS ITS RE-READ and must. It is reachable FROM the listing, where no
          // navigation follows and no address changes, so without it a removed row would stay on screen -
          // and the same runtime capture confirms the distinction, showing no aborted request anywhere on
          // the delete path. This is the identical division the sibling role store records on its own
          // create command; the two stores must not disagree about it.
        },
        error: (cause: unknown) => {
          this._saving.set(false);
          this.recordFailure('createModule', cause);
        },
      }),
    );
  }

  /**
   * Replaces one module placement.
   *
   * MIGRATION: A WHOLE-ROW REPLACEMENT, EXACTLY AS THE LEGACY POSTBACK WAS. An omitted nullable member is
   *   not "leave it alone" - the server writes the absent value and clears the column - which is what the
   *   legacy screen did when a text box was posted empty, and without it an operator could set a header
   *   but never remove one. The request is therefore transmitted whole and nothing is stripped here.
   *
   * MIGRATION: A `403` IS A LEGITIMATE ANSWER AND IS NOT PRE-EMPTED. The server enforces a rule of its
   *   own on the all-pages flag, and no check anticipating it exists in this file: a copy of a server rule
   *   on the client gives an HTTP caller a different answer from every other caller, and the two copies
   *   drift. The refusal is recorded as the problem document it is, at WARNING severity, per the legacy
   *   `AccessDenied` precedent.
   *
   * Note which value identifies the placement ON THE WIRE: the update endpoint takes NO placement
   * selector, because the page is named by the required `tabId` member of the request body itself - the
   * server uses it to select the exact placement being edited and refuses when the module is not placed
   * on that page. The `tabModuleId` argument below is therefore NOT sent anywhere; it names which listed
   * row the echoed response corresponds to, so that the local row can be replaced instead of the whole
   * listing being re-read.
   *
   * MIGRATION: A SUCCESSFUL REPLACEMENT ALWAYS ECHOES THE PLACEMENT, so there is no longer an
   *   echo-less branch that re-read the listing. The endpoint answers `200` with the updated
   *   placement or refuses - a module that no longer resolves is a not-found problem document, a
   *   rule violation a forbidden one - and both reach the error handler below. The removed branch
   *   was unreachable in practice and actively misleading: it committed `null` over a record the
   *   operator had just edited, so a successful save could blank the form it was saved from.
   *
   * @param moduleId The module to replace. Forwarded exactly as supplied.
   * @param request The complete replacement state, transmitted whole. Its `tabId` member identifies the
   * placement being edited and is required for that reason.
   * @param tabModuleId The listed row the echo corresponds to, or omitted to use the current selection.
   * Used only to decide whether one row can be replaced locally; never transmitted.
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
   * Removes one module placement, then RE-READS the listing.
   *
   * MIGRATION: THE REMOVAL IS SOFT AND THE ROW SURVIVES, SO NOTHING IS PRUNED LOCALLY. The endpoint
   *   answers `204 No Content`, yet the module row remains in the database with its deleted marker set -
   *   which is precisely what the legacy recycle-bin screen under `Website/admin/Tabs/` read.
   *   Whether a removed module continues to appear is the LISTING endpoint's decision, expressed through
   *   its inclusion flag, and it is not this store's to infer: with that flag set the row should REMAIN
   *   visible after a successful removal, which an optimistic local splice would wrongly hide. The listing
   *   is therefore re-read and no row is removed from the local array.
   *
   * MIGRATION: THERE IS NO RESTORE PATH. The target surface exposes no recycle-bin endpoint and no
   *   restore endpoint, and the page surface is closed at read, read-one and replace, so no method here
   *   reverses this operation. Adding one would address a route the API does not serve. A removed module
   *   can be made visible with {@link ModuleStore.setIncludeDeleted}, which is a different thing from
   *   being able to bring it back.
   *
   * @param moduleId The module to remove. Forwarded exactly as supplied; module zero is real.
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

          // ⚠ THE CACHED RECORD OF THE MODULE JUST REMOVED IS DISCARDED, AND OMITTING THIS WAS A
          // MEASURED CRITICAL DEFECT. This store is `providedIn: 'root'`, so the settings bag read for
          // one screen outlives that screen. Measured: remove a placement, then reach `/modules/{id}`
          // for the same identifier IN THE SAME SESSION, and the editor rendered the DELETED module's
          // data in a fully populated form with Update, Cancel and Delete all ENABLED — beneath an
          // inline Not-Found alert saying the record could not be read. It was the only surface in the
          // application that permitted a second, doomed removal of something already gone, which
          // answered 404. On a COLD navigation the same address correctly rendered no controls at all,
          // which is what made the defect state-dependent and easy to miss: the request fails
          // identically either way, and what differed was whether this signal still held an answer for
          // the form to hydrate from.
          //
          // Scoped by identifier rather than cleared outright, deliberately. A bag belonging to some
          // OTHER module is still a truthful answer about that module and there is no reason to make
          // the next screen read it again; only the record that has just stopped existing is discarded.
          // The comparison is on the identifier and never on truthiness, because `Modules.ModuleID` is
          // `IDENTITY(0, 1)` and nought is a legitimate module.
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
   * MIGRATION: THE TWO MAPS ARE HELD APART. One is recorded against the module and applies on every page
   *   it appears on; the other belongs to one occurrence on one page. They are returned as two maps
   *   because they land in two different tables, and this store holds them exactly as received. Note also
   *   that the cache period on the module and the default on its definition are DISTINCT FACTS that this
   *   store never merges - `_CacheTime = 0` and `_DefaultCacheTime = -1` were initialised to different
   *   values in one legacy constructor (`ModuleInfo.vb:L731` and `:L759`) - so no expression here reads
   *   one as the other's fallback and no derived "effective" period is computed.
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
      // A successful read carries both maps. An empty map is a real answer - a module with no
      // settings recorded - and is not the same fact as an unresolvable module, which the endpoint
      // refuses with a not-found problem document that reaches the error handler below.
      next: (bag: ModuleSettingsBag) => {
        // ⚠ THE TICKET IS THE ONLY AVAILABLE CHECK HERE, which is precisely why the mechanism cannot be
        // an identifier comparison. A settings bag is operator-authored key-and-value data and carries
        // NO module key of its own, so there is nothing in the response to compare against the module
        // that was asked for. Without this, module A's settings would be committed under module B's
        // route and then saved back to B, replacing B's configuration wholesale - the update endpoint
        // writes the bag as a whole rather than merging it.
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
   * Replaces one module's settings, then RE-READS them.
   *
   * MIGRATION: THE REPLACEMENT ANSWERS `204` WITH NO BODY, WHICH IS WHY IT IS FOLLOWED BY A READ. There
   *   is no echoed state to hold, so a caller needing the stored settings reads them back deliberately;
   *   that second request is composition and therefore belongs here rather than in the transport.
   *
   * MIGRATION: THE MAPS ARE TRANSMITTED WHOLE, INCLUDING EVERY EMPTY VALUE. This is the most
   *   sentinel-sensitive payload in the feature: the settings maps legitimately hold empty strings, the
   *   legacy string absence marker IS the empty string (`Null.vb:L71-L75`, body literally `Return ""`),
   *   and the server elides nothing in either direction. Filtering the maps for truthiness before sending
   *   - the obvious and wrong implementation - would silently delete every setting a user had cleared,
   *   and the request would still answer `204`. Nothing here filters, trims or normalises either map.
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
   * MIGRATION: THE DOCUMENT ARRIVES IN THE `200` RESPONSE BODY, AND THE LEGACY FILE WRITE IS GONE.
   *   `Website/admin/Modules/Export.ascx.vb:L157` obtained it through a double late-bound cast -
   *   `CType(CType(objObject, IPortable).ExportModule(ModuleID), String)`, legal only because Option
   *   Strict was off for the administration code-behinds (`Website/release.config:L125`) - and L168-L186
   *   then wrote that string to a file beneath the portal's home-directory map path. The target does
   *   NEITHER. No filesystem endpoint exists in this API by design, so the string is simply returned, and
   *   this store holds it as an OPAQUE value.
   *
   * MIGRATION: NOTHING IS DONE TO THE DOCUMENT HERE. It is not parsed, inspected, validated, formatted,
   *   named or wrapped: there is no document parser, no serialiser, no binary container, no object URL,
   *   no anchor and no filename composition anywhere in this file. `Export.ascx.vb:L124` composed the
   *   stored name from the module's programmatic name, the operator's text and an extension; that
   *   composition is not carried across, and whatever name the caller puts on the request is transmitted
   *   verbatim. Presenting or saving the document is the export SCREEN's concern, using the string held
   *   here. Because the content is module-authored it is also the most untrusted string this store holds
   *   and must never be bound as markup.
   *
   * MIGRATION: AN EMPTY DOCUMENT IS A DISTINCT OUTCOME. The legacy branch at `Export.ascx.vb:L159` tested
   *   `Content <> ""`, so an empty export was a recognised answer rather than a fault. The empty string is
   *   stored as the empty string and is never normalised to `null`, which is reserved for "nothing has
   *   been exported".
   *
   * @param moduleId The module to export. Forwarded exactly as supplied.
   * @param request The name the caller intends for the payload, and an optional folder. Both are
   * forwarded verbatim; the folder is resolved against nothing.
   */
  exportModule(moduleId: number, request: ModuleExportRequest): void {
    this._exporting.set(true);
    this._exportedContent.set(null);
    this.clearFailure();

    const ticket = this.exportOperations.begin();

    this.exportRequest = this.moduleService.exportModule(moduleId, request).subscribe({
      next: (content: string) => {
        // ⚠ THE MOST CONSEQUENTIAL GUARD IN THIS FILE, because the commit it protects is an
        // EXFILTRATION sink rather than a display slice. The document carries no module key - it is
        // the module's serialised data as a bare string - so the ticket is again the only check
        // available. Publishing module A's export while the screen has moved to module B hands the
        // operator A's data under B's composed filename, and nothing downstream can detect the
        // substitution. An export is also deliberately NOT cancelled by a later one, so this ticket
        // is the only thing standing between two overlapping exports and the wrong one winning.
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

    // ⚠ TRACKED AS A WRITE AS WELL AS HELD ABOVE, and both are needed. The dedicated handle lets the
    // transfer panel abandon THIS operation when it closes; the write container is what lets a session
    // teardown reach it, since releasing every write to close one panel would be far too broad.
    this.track(this.exportRequest);
  }

  /**
   * Imports content into a module.
   *
   * MIGRATION: THE ROUTE CARRIES NO IDENTIFIER, AND THIS METHOD TAKES NONE. The endpoint is
   *   `POST /modules/import`; the target module is a MEMBER OF THE REQUEST. That mirrors the legacy page,
   *   which received its target out of band rather than as part of its address
   *   (`Website/admin/Modules/Import.ascx.vb:L67-L68` parsed it from a request value into the field
   *   declared at `:L51`), and it reflects that the payload is adjudicated as a whole: the document, its
   *   declared type and the target are refused together, so lifting the target into the path would let a
   *   caller address a module the payload contradicts.
   *
   * MIGRATION: A TARGET OF MINUS ONE IS TRANSMITTED AS MINUS ONE. `Import.ascx.vb:L51` declared
   *   `Private Shadows ModuleId As Integer = -1` - an identifier field seeded with the integer absence
   *   marker (`Null.vb:L41-L45`) - so minus one is a value a caller may genuinely hold. The request is
   *   passed through UNTOUCHED: this method does not rewrite minus one to `null`, does not omit the
   *   member, does not refuse the request and does not route it through a per-module path. The member is
   *   nullable on the contract precisely so an omission can be told apart from a caller naming module
   *   ZERO, which is a real module, and the server rejects the omission on the caller's behalf.
   *
   * MIGRATION: NO FILE IS READ AND NO DOCUMENT IS PARSED. `Import.ascx.vb:L184` opened a stream against
   *   the portal's home-directory map path, L188-L192 built a document and reported a parse failure, L197
   *   compared the declared type against the module's own name, and L200 handed the inner markup to the
   *   module's portability contract, passing an acting account explicitly. Every part of that is server
   *   behaviour now, and the acting account is taken from the authenticated caller rather than from the
   *   request, because an identifier a request could choose for itself would let one account attribute an
   *   import to another. The three refusals this path can produce arrive as problem documents and are
   *   recorded under the server's own codes - see {@link ModuleStoreFailure.code}.
   *
   * @param request The target module, the document as text, and the optional descriptive folder and name
   * members. Transmitted whole.
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

  /** Discards the recorded failure. Call when a screen dismisses its banner. */
  clearFailure(): void {
    this._failure.set(null);
  }

  /**
   * Discards the exported document and the import outcome.
   *
   * Called when a transfer panel closes, so that a later visit does not open onto a document exported for
   * a different module or report a success that has already been acknowledged.
   */
  clearTransferOutcome(): void {
    // Abandons any export still in the air. Without this, closing the panel and reopening it could be
    // met by the earlier export's document arriving and presenting itself as the new one's result.
    //
    // ⚠ THE HANDLE IS RELEASED AND THE FLAG IS LOWERED, not just the ticket invalidated. An abandoned
    // operation has to return its slice to REST, because nothing else will: the refused commit returns
    // early by design, so if the flag were left raised the screen would report a transfer in progress
    // for the remainder of the page's life, with no request behind it and no way to clear it.
    this.exportOperations.invalidate();
    this.cancelExport();

    this._exportedContent.set(null);
    this._exporting.set(false);
    this._importCompleted.set(false);
  }

  /** Discards the loaded module and the selection that addressed it. */
  clearModule(): void {
    // Abandons any module read in the air, so a response cannot repopulate the very slice this just
    // discarded. There is no newer request here whose dispatch would have superseded it.
    //
    // The handle is released and the loading flag lowered for the reason given on
    // {@link ModuleStore.clearTransferOutcome}: an abandoned read must leave its slice at rest.
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
   * Returns every slice to the state it held before anything was read.
   *
   * ⚠ THIS IS A SESSION-TEARDOWN OPERATION, NOT A SCREEN-LEVEL CLEAR. The five `clear*` members above
   * are for a screen tidying up after itself — closing a transfer panel, dismissing a banner, leaving a
   * detail view. This one exists for the moment the SESSION ends, and the distinction matters because
   * this store is root-provided: one instance is shared by every module screen and it outlives all of
   * them. Without this member, signing out left the entire contents of that instance in memory, and the
   * next person to sign in on the same page load inherited it.
   *
   * WHAT WAS ACTUALLY RETAINED, and why it is not a cosmetic concern. The five `clear*` members between
   * them touch six of the twenty-five slices below. Everything else survived a sign-out, including:
   *
   * - the module LISTING — every row of one tenant's module placements, with titles and definition names;
   * - the loaded module's SETTINGS bag, which is arbitrary operator-authored configuration;
   * - the page hierarchy read for a tenant, and the tenant identifier it was read for;
   * - the EXPORTED MODULE CONTENT, which is the most consequential of the lot: a serialised copy of a
   *   module's data, held as a plain string, produced under one account's authority. Leaving it in a
   *   root-scoped store after that account signed out means the next account on the same page load could
   *   be handed it by a download affordance.
   *
   * Every one of those is data belonging to a tenant and an account that are no longer signed in, so it
   * is cleared rather than merely hidden.
   *
   * ⚠ IN-FLIGHT WORK IS CANCELLED FIRST, before any slice is written. Resetting the slices while a read
   * was still outstanding would accomplish nothing: the response would arrive afterwards and repopulate
   * exactly what had just been discarded, which is worse than not resetting at all because it looks
   * correct. Cancellation therefore precedes the writes and is not optional.
   *
   * Called from `core/state/session-teardown.service.ts` and deliberately from nowhere else. A screen
   * must not reach for this — it would silently discard state its siblings are reading.
   *
   * The values written below are the SAME initialisers the fields declare, restated rather than derived,
   * because a reset that quietly diverged from the initial state would leave the store in a condition it
   * can never otherwise be in.
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
   * Releases every request handle when the injector holding this store is destroyed.
   *
   * A root-provided store lives as long as the application, so in production this runs at teardown. It
   * matters most in a specification, where each one builds its own injector: a request left listening
   * across that boundary would report into a store the next specification has already replaced, and the
   * failure would surface as an unrelated test failing intermittently.
   *
   * Deliberately NOT a call to {@link ModuleStore.reset}. Destruction has no session semantics — writing
   * every slice back to its initial value on the way out accomplishes nothing, because the instance is
   * being discarded, and it would make the two concerns impossible to tell apart at a call site.
   */
  ngOnDestroy(): void {
    this.cancelInFlight();
  }

  // ---------------------------------------------------------------------------------------------------
  // INTERNALS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Holds a write's handle until it settles, so that teardown can release it.
   *
   * A write is never cancelled to make way for a later one — see the note on the handles at the head of
   * this class — so the handle is discarded when the write FINISHES rather than when the next one starts,
   * and the set cannot therefore grow without bound.
   *
   * A handle that is already closed is not retained at all: a synchronous observable settles before
   * `subscribe` returns, and holding a closed handle would leak it until teardown for no benefit.
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
   * Releases the export in flight, if any, and forgets its handle.
   *
   * Narrower than {@link ModuleStore.cancelWrites} on purpose: closing the transfer panel abandons the
   * export the panel started and must not disturb an unrelated save.
   */
  private cancelExport(): void {
    this.exportRequest?.unsubscribe();
    this.exportRequest = null;
  }

  /**
   * Cancels every read in flight and forgets its handle.
   *
   * Abandoning a read is safe in a way abandoning a write is not: a read has no server-side consequence,
   * so nothing is left half-done by refusing to listen to its answer.
   *
   * The three definition handles are released through {@link ModuleStore.cancelDefinitionReads} rather
   * than listed again here, so that the set of reads sharing the definitions flag is enumerated in
   * exactly ONE place. Two lists would drift, and a handle present in one but not the other would leak
   * through whichever path omitted it.
   */
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
   * Emits ONE envelope carrying every module the picker endpoint will return.
   *
   * The walk and its three terminating conditions are described on {@link ModuleStore.loadChoices}; this
   * method is the mechanism alone. It sends NO narrowing, NO ordering and NO free-text filter, because a
   * picker wants every placement and this store's browsable coordinates belong to a different slice that
   * must not be read here — reading {@link ModuleStore._query} would make opening a picker depend on
   * whatever page a sibling listing happens to be on.
   *
   * @returns The complete choice set as one envelope whose `meta.totalCount` is the SERVER's total.
   * Cold: nothing is requested until it is subscribed, which is what lets {@link ModuleStore.loadChoices}
   * hold one handle that cancels the whole walk rather than just the request in flight.
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

        // An empty inner observable is how `expand` is told to stop: it emits nothing and completes, so
        // the outer stream completes with the pages already emitted. This is the ONLY silent stop, and
        // it is the only one that means the answer is complete.
        if (gathered >= reportedTotal) {
          return EMPTY;
        }

        // ⚠ AN INCOMPLETE CHOICE SET IS A REFUSAL, NOT A SHORTER ANSWER, AND A SHORT PAGE IS NOT A
        // TERMINATION CONDITION. An earlier revision stopped the walk on any page carrying fewer rows
        // than it asked for and published what it had, which is the defect rather than the guard: the
        // picker is a "choose from every placement" affordance, so a truncated set is a set with
        // placements missing that a reader cannot tell from a complete one — they simply are not
        // offered, silently, with no indication that anything was withheld. The screen that consumes
        // this offers NO PICKER AT ALL rather than a partial one, which is only possible if the read
        // raises.
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
            // Nought and "one page holding everything" are the coordinates the paging contract publishes
            // for an unpaged answer, so a consumer cannot tell this envelope from one the server
            // assembled unpaged.
            pageIndex: 0,
            pageSize: accumulated.items.length + page.items.length,
            totalPages: accumulated.items.length + page.items.length > 0 ? 1 : 0,
          },
        }),
        emptyPagedResult<ModuleListItem>(),
      ),
    );
  }

  /**
   * Opens one definition read. Paired with {@link ModuleStore.endDefinitionRead}.
   *
   * Every path that dispatches a definition read calls this, and every path that settles or abandons one
   * calls the closer, so {@link ModuleStore.definitionsLoading} is true for exactly as long as at least
   * one of the three reads is outstanding.
   */
  private beginDefinitionRead(): void {
    this._definitionReadsInFlight.update((open) => open + 1);
  }

  /**
   * Closes one definition read.
   *
   * Clamped at zero rather than allowed to go negative. A count that has already been reset - by
   * teardown, or by a session boundary discarding the store - must not be driven below zero by a
   * callback that was already queued when the reset ran, because a negative count would make the next
   * read's increment leave the flag reading false while that read was genuinely in flight.
   */
  private endDefinitionRead(): void {
    this._definitionReadsInFlight.update((open) => (open > 0 ? open - 1 : 0));
  }

  /**
   * Releases ONE definition read's handle and closes its count, if that handle is outstanding.
   *
   * ⚠ THE COUNT MUST BE CLOSED HERE, BECAUSE UNSUBSCRIBING RUNS NEITHER CALLBACK. A cancelled
   * subscription delivers no value and no error, so the closer on the `next`/`error` paths never runs
   * for a read that was abandoned - and without this the count would leak upward until the flag was
   * permanently true and every screen bound to it reported itself permanently busy.
   *
   * The handle is only ever passed by the command that owns it, or by
   * {@link ModuleStore.cancelDefinitionReads} on teardown. A settled read nulls its own handle, so
   * passing an already-settled handle here is a no-op rather than a double decrement.
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

  /**
   * Releases all THREE definition reads, whichever of them is outstanding. TEARDOWN ONLY.
   *
   * ⚠ THIS IS NOT A PRE-DISPATCH STEP, AND CALLING IT FROM A LOAD COMMAND WOULD REINTRODUCE A REAL
   * DEFECT. Three commands read definitions — the whole catalogue, one definition, and one bundle's
   * definitions — into three separate slices, and each of them releases ONLY its own handle, which is
   * the invariant stated in the handle block near the top of this class. Two of them once released the
   * catalogue handle as well: the three reads serve different screens and the store is provided at the
   * application root, so reading one definition abandoned a catalogue read another screen was waiting
   * on, and because a cancelled subscription delivers neither a value nor an error that screen was left
   * holding an empty catalogue with no failure to explain it and no request in flight to finish it.
   *
   * The reason cross-cancellation looked necessary was the shared loading flag: with one boolean, the
   * first read to answer cleared it while the others were still on the wire, so the only way to keep it
   * honest was to guarantee that only one read could ever be outstanding. That premise is gone —
   * {@link ModuleStore._definitionReadsInFlight} COUNTS the outstanding reads, so the flag stays true
   * until the last one settles without anything having to be cancelled. Both defects are therefore
   * closed at once, and neither fix is bought at the other's expense.
   *
   * All three are released here because teardown is discarding the whole store, which is the one
   * situation in which abandoning another slice's read is exactly the intent.
   */
  private cancelDefinitionReads(): void {
    this.releaseDefinitionRead(this.definitionsRequest);
    this.definitionsRequest = null;
    this.releaseDefinitionRead(this.definitionRequest);
    this.definitionRequest = null;
    this.releaseDefinitionRead(this.desktopDefinitionsRequest);
    this.desktopDefinitionsRequest = null;
  }

  /**
   * Releases every write handle.
   *
   * Only for teardown and for a reset that is discarding the whole store. A write in flight is not
   * otherwise abandoned, because releasing the handle stops the client listening without undoing
   * anything the server may already have committed.
   *
   * Iterates a COPY of the set, because unsubscribing runs the teardown registered by
   * {@link ModuleStore.track}, which deletes from the set being iterated.
   */
  private cancelWrites(): void {
    for (const request of [...this.writeRequests]) {
      request.unsubscribe();
    }

    this.writeRequests.clear();

    // The export is tracked as a write, so it has just been released; its dedicated handle is forgotten
    // here so the two cannot disagree about whether one is outstanding.
    this.exportRequest = null;
  }

  /**
   * Cancels everything this store has outstanding, reads and writes alike.
   *
   * The single entry point used by {@link ModuleStore.reset} and {@link ModuleStore.ngOnDestroy}, so that
   * neither can gain a handle the other forgets to release.
   */
  private cancelInFlight(): void {
    this.cancelReads();
    this.cancelWrites();
  }

  /**
   * Abandons every operation ticket, so nothing already issued can still be current.
   *
   * Paired with {@link ModuleStore.cancelInFlight} rather than folded into it, because the two answer
   * different questions and one is not a substitute for the other. Cancelling releases a handle so the
   * response is never delivered; abandoning refuses the COMMIT of any response that is delivered anyway.
   * A write is the case that makes the distinction concrete: it is deliberately not abandoned mid-flight
   * server-side, and its handle may already have been released by the time its answer arrives.
   */
  private abandonOperations(): void {
    this.moduleReads.invalidate();
    this.settingsReads.invalidate();
    this.exportOperations.invalidate();
  }

  /**
   * Resolves which placement an operation addresses.
   *
   * The caller names the placement or names nothing; naming nothing addresses the MODULE rather than one
   * of its placements. The test is an exact `=== undefined`, because a placement identifier is an opaque
   * key and no numeric value of it means "absent" - in particular not zero.
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
   * MIGRATION: THE ROW KEY IS THE PLACEMENT, NOT THE MODULE. A module whose all-pages flag is set
   *   contributes ONE ROW PER PAGE, each with its own placement identifier and its own position, while
   *   the module identifier repeats across them; keying by module would collapse those rows onto one
   *   another. The echoed detail names exactly one placement, so exactly one row is replaced, and the
   *   comparison is an exact `===` on a value that can legitimately be zero.
   *
   * When the update addressed the MODULE rather than one placement - which for an all-pages module
   * rewrites every occurrence - a single row replacement cannot describe the outcome, so the listing is
   * re-read instead of being patched.
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
   * Records a failed command.
   *
   * MIGRATION: SEVERITY IS DELEGATED, NOT DECIDED HERE, AND A REFUSAL IS A WARNING. `summarizeProblem`
   *   in `core/utils/form-errors.util.ts` resolves the severity, and it maps a refusal to WARNING rather
   *   than to error. The legacy authority for that is `Website/admin/Security/AccessDenied.ascx.vb`, a
   *   fifty-line page that performs no permission check of its own and renders both of its branches, at
   *   L43 and L45, as a yellow warning; the utility also records the measured legacy distribution behind
   *   the distinction. Re-implementing the mapping here would create a second copy to drift, so this
   *   method resolves nothing and stores what the utility returns.
   *
   * MIGRATION: THE STRUCTURED DOCUMENT IS KEPT ALONGSIDE THE RESOLVED WORDING, `traceId` INCLUDED, and
   *   no markup is ever produced. See {@link ModuleStoreFailure}.
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
 * Only the members the listing carries are taken from the echo; the row's read-only catalogue projections
 * - the definition's display name, the bundle's name, description and version - are preserved from the
 * existing row, because the listing computes them by join and an update cannot change them.
 *
 * MIGRATION: THE INSTANCE CACHE PERIOD IS NOT AMONG THE MEMBERS COPIED, BECAUSE THE LISTING DOES NOT
 *   CARRY IT. `cacheTime` lives on the module contracts and `defaultCacheTime` on the definition
 *   contract, and neither appears on a listing row. That separation is preserved rather than papered
 *   over: this function does not invent a cache member on the row, and it certainly does not derive one
 *   by reading either value as the other's fallback.
 *
 * @param row The listed row being replaced.
 * @param detail The placement the server echoed back.
 * @returns A new row. The input is never mutated.
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
