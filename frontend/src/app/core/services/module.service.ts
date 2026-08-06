import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { moduleListParams, modulePlacementParams } from '../utils/http-params.util';
import { toPagedResult } from '../models/paged-result.model';

import type { HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';

import type {
  ModuleListFilter,
  ModulePlacementSelector,
  PagedRequestParams,
} from '../utils/http-params.util';
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
import type { ApiResponse, PagedResponse } from '../models/paged-result.model';

/**
 * Typed transport for the module placement and module definition endpoints.
 *
 * ONE METHOD, ONE ENDPOINT, ONE REQUEST. The migration discipline restricts Angular
 * services to API communication, and this class is written to that rule literally:
 * every member below issues exactly one request and hands back the observable cold.
 * Consequently there is deliberately NO validation, NO derived value, NO document
 * parsing, NO orchestration of one call after another, NO retry policy and NO cache
 * policy anywhere in this file. Deciding when to call, what to show and what to do
 * with a refusal belongs to the feature store and the components above it; deciding
 * whether a request is legal belongs to the server, which answers with an RFC 7807
 * problem document that the shared error handler in the HTTP chain surfaces.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS FILE DOES NOT TOUCH, AND WHY EACH OMISSION IS LOAD-BEARING
 * ---------------------------------------------------------------------------
 * - No URL is built here. Every path comes from `core/config/api-endpoints.ts`,
 *   which composes it from the configured API base. Those strings are passed
 *   STRAIGHT to the client and are never prefixed again: prefixing a second time
 *   yields a doubled version segment, which is a 404 that type-checks, bundles and
 *   deploys without a single warning. This file therefore never reads configuration.
 * - No query string is assembled here. `core/utils/http-params.util.ts` owns that,
 *   and it decides omission with an explicit undefined-or-null test rather than a
 *   truthiness test, which is the only way `0`, `-1`, the empty string and `false`
 *   survive as the real values they are in this schema. Two of its helpers exist for
 *   these endpoints specifically and both are used below.
 * - No header is set here. The correlation identifier, the bearer token and the
 *   problem-document translation are applied by the three functional interceptors
 *   registered once at application configuration, in that fixed order, so that the
 *   correlation value reaches the server's middleware, the token is attached after
 *   it, and the error translation observes the final response after any token
 *   renewal has been attempted.
 *
 * ---------------------------------------------------------------------------
 * THE ENVELOPE IS UNWRAPPED, AND THAT IS TRANSPORT SHAPE RATHER THAN A DECISION
 * ---------------------------------------------------------------------------
 * Single-payload endpoints answer inside the shared success envelope, so each such
 * call is typed as that envelope and the payload is lifted out of it. Typing a call
 * as the bare payload instead compiles and then fails in the quietest possible way:
 * every member reads as undefined and the result is a shape-correct blank. Lifting a
 * declared member out of the transport wrapper invents nothing and is the convention
 * the sibling authentication service already follows.
 *
 * The paged listing is the one endpoint that answers WITHOUT that wrapper — its page
 * envelope is the body — and it is normalised through the paging model's own
 * completion function, which fills in a page count the body may omit using the
 * server's own arithmetic. Nothing else about the page is computed, and the input and
 * output of that function are the same declared type.
 *
 * ---------------------------------------------------------------------------
 * SENTINELS: NO IDENTIFIER IS EVER GUARDED, DEFAULTED OR COALESCED HERE
 * ---------------------------------------------------------------------------
 * MIGRATION: `moduleId` and `tabId` of ZERO are REAL identifiers, and `portalId` of
 *   zero AND of minus one are both real. `Library/Components/Shared/Null.vb` L41-L45
 *   defines the integer "absent" marker as minus one and L71-L75 defines the string
 *   marker as the EMPTY STRING - its body is literally a bare pair of quotes, not
 *   `Nothing` - while
 *   `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` seeds
 *   the module identity at ZERO (L221), the page identity at ZERO (L140) and the
 *   portal identity at MINUS ONE (L77). The same value therefore means both "the
 *   first row" and "no row", decided only by context a transport layer does not have.
 *   Every identifier below is consequently forwarded exactly as supplied. Not one
 *   method applies a truthiness test to an identifier, a positive-value or
 *   non-negative comparison, a test against the sentinel, a null-coalescing default
 *   or a logical-or default, and none may be added: each would silently rewrite a
 *   request for a real row into a request for a different one, or drop it, and return
 *   a perfectly successful status while doing so.
 *
 * MIGRATION: request bodies are transmitted WHOLE. The server serialises without
 *   eliding default values, so a zero, a minus one, an empty string and a `false` all
 *   reach the wire in both directions. No member of any request contract is stripped
 *   here for being falsy - least of all on the settings replacement, whose two maps
 *   legitimately hold empty strings, and on the import contract, whose module member
 *   is nullable precisely so that an omission can be told apart from a caller naming
 *   module zero.
 *
 * @see API_ENDPOINTS for the route templates this class consumes.
 */
@Injectable({ providedIn: 'root' })
export class ModuleService {
  private readonly http = inject(HttpClient);

  /**
   * Lists the resolved tenant's module placements, one page at a time.
   *
   * `GET /modules`, answering `200` with a page envelope. The tenant is NOT a
   * parameter: the server resolves it from the request host before dispatching, so a
   * caller names only the page it wants and the restrictions it wants applied.
   *
   * The page index on the wire is ZERO-BASED and no arithmetic is performed on it in
   * either direction here. The one-based counter the legacy screens carried survives
   * only as a presentation concern, and the mapping between the two lives in the
   * feature store; adjusting it here would quietly serve the neighbouring page behind
   * a successful status code.
   *
   * The free-text filter is transmitted exactly as the caller supplied it. Match
   * semantics are the server's alone and are not characterised, restated or relied on
   * here, and no wildcard character is appended to the text: the legacy readers
   * decorated the pattern at the call site, which this migration moved behind the
   * repository interfaces where it belongs.
   *
   * @param request The page coordinate, page size, ordering and free-text filter.
   * Omitted members are not transmitted, so the server applies its own defaults.
   * @param filter The page restriction and whether soft-deleted rows are included, or
   * omitted to list the tenant's live placements.
   * @returns The requested page, with every paging coordinate present.
   */
  listModules(
    request: PagedRequestParams,
    filter?: ModuleListFilter | null,
  ): Observable<ModuleListPage> {
    const params: HttpParams = moduleListParams(request, filter);

    // The paged listing is the one endpoint here whose body IS the envelope, so no
    // payload member is lifted out of it. It is passed through the paging model's own
    // completion function, whose input and output are the same declared type: the only
    // value it can add is a page count the body omitted, computed the way the server
    // computes it, so a pager never divides by a page size of zero.
    return this.http
      .get<PagedResponse<ModuleListItem>>(API_ENDPOINTS.modules.collection(), { params })
      .pipe(map((page) => toPagedResult(page)));
  }

  /**
   * Reads one module placement in full.
   *
   * `GET /modules/{moduleId}`, answering `200` with the placement, or `404` when no
   * such module is visible to the caller.
   *
   * MIGRATION: THIS ADDRESSES ONE PLACEMENT, WHICH IS WHY A SELECTOR EXISTS. A module
   *   whose all-pages flag is set has one placement per page of the portal, so the
   *   module identity alone does not name a single row. Supplying the placement
   *   selector picks one; omitting it addresses the module itself. The two are
   *   materially different reads and the difference is not blurred by a default here.
   *
   * MIGRATION: the legacy hand-rolled row reader is gone.
   *   `Library/Components/Modules/ModuleController.vb` L54 built the object and
   *   L66-L72 then assigned each column through a sentinel-substituting conversion,
   *   one statement per column. The server's object-relational materialiser replaced
   *   that entirely, so nothing on this side reads a column or substitutes a sentinel.
   *   The sentinel VALUES still travel, which is why no member is guarded here.
   *
   * MIGRATION: the legacy `ModuleInfo` class was a 936-line flattened join across the
   *   module, placement, definition and control tables
   *   (`Library/Components/Modules/ModuleInfo.vb` L36-L936), and the target splits it
   *   into four separate entities behind four separate contracts. That flattened shape
   *   is deliberately not reconstructed: whatever the response contract declares is
   *   what this method returns. The token-accessor interface the legacy class
   *   implemented at L37 went with the excluded token-replacement subsystem, and the
   *   legacy visibility enumeration was renamed on the way across - the target spelling
   *   is the one the model declares.
   *
   * @param moduleId The module to read. Forwarded exactly as supplied; zero is a real
   * module.
   * @param placement The placement to address, or omitted to address the module.
   * @returns The placement, or `null` when the response carried none.
   */
  getModule(
    moduleId: number,
    placement?: ModulePlacementSelector | null,
  ): Observable<ModuleDetail | null> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http
      .get<ApiResponse<ModuleDetail | null>>(API_ENDPOINTS.modules.byId(moduleId), { params })
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Adds a module placement to the resolved tenant.
   *
   * `POST /modules`, answering `201` with the created placement. A refusal arrives as
   * `400` for a field-level violation, `403` when policy denies the write, `404` when
   * a referenced definition or page does not exist, or `409` on a conflict; each is a
   * problem document that propagates untranslated.
   *
   * MIGRATION: the cache lifetime the request carries and the definition's own default
   *   lifetime are DISTINCT facts and neither is coalesced into the other.
   *   `Library/Components/Modules/ModuleInfo.vb` declared them separately at L203 and
   *   L482, and the target keeps them on separate contracts - the lifetime on the
   *   module contracts, the default on the definition contract - so folding one into
   *   the other would change which modules expose a cache control and for how long the
   *   rest cache. Neither is dropped for being zero.
   *
   * @param request The placement to create, transmitted whole.
   * @returns The created placement.
   */
  createModule(request: CreateModuleRequest): Observable<ModuleDetail> {
    return this.http
      .post<ApiResponse<ModuleDetail>>(API_ENDPOINTS.modules.collection(), request)
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Replaces one module placement.
   *
   * `PUT /modules/{moduleId}`, answering `200` with the updated placement.
   *
   * A `403` is a legitimate answer to a well-formed request here, and it is NOT
   * pre-empted. The server enforces a field-level rule of its own on the all-pages
   * flag, and no check anticipating it exists in this file: duplicating a server rule
   * on the client gives an HTTP caller a different answer from every other caller, and
   * the two copies drift. The refusal propagates as the problem document it is.
   *
   * Placement changes travel through THIS endpoint. There is no page create, delete,
   * reorder or move operation anywhere in this API, so moving a module between pages
   * is an update to the module rather than an operation on a page.
   *
   * @param moduleId The module to replace. Forwarded exactly as supplied.
   * @param request The complete replacement state, transmitted whole.
   * @returns The updated placement, or `null` when the response carried none.
   */
  updateModule(
    moduleId: number,
    request: UpdateModuleRequest,
  ): Observable<ModuleDetail | null> {
    return this.http
      .put<ApiResponse<ModuleDetail | null>>(API_ENDPOINTS.modules.byId(moduleId), request)
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Removes one module placement.
   *
   * `DELETE /modules/{moduleId}`, answering `204` with no body.
   *
   * MIGRATION: THE REMOVAL IS SOFT, AND THE CALLER MUST NOT ASSUME THE ROW IS GONE.
   *   The row survives with its deleted marker set - which is precisely what the
   *   legacy bin screen at `Website/admin/Tabs/RecycleBin.ascx.vb` consumed - and the
   *   listing hides it unless deleted rows are asked for, which is what the listing's
   *   inclusion flag is for. Two consequences are deliberate. Nothing here asserts
   *   local removal or drops a row from a collection, because this class holds no
   *   collection. And no re-read follows the removal: chaining a second request onto
   *   the first would be orchestration, which this layer does not do - the feature
   *   store decides whether to refresh.
   *
   * MIGRATION: the legacy bin screen has NO target counterpart, so there is
   *   deliberately no method here that reverses this operation or empties the store.
   *   Adding one would address a route the API does not serve.
   *
   * Supplying the placement selector narrows the effect to a single occurrence,
   * leaving the module on its other pages; omitting it addresses the module itself.
   *
   * @param moduleId The module to remove. Forwarded exactly as supplied.
   * @param placement The single placement to remove, or omitted to remove the module.
   * @returns Completion, with no payload.
   */
  deleteModule(
    moduleId: number,
    placement?: ModulePlacementSelector | null,
  ): Observable<void> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http.delete<void>(API_ENDPOINTS.modules.byId(moduleId), { params });
  }

  /**
   * Reads one module's settings.
   *
   * `GET /modules/{moduleId}/settings`, answering `200` with both settings maps.
   *
   * The response carries two separate maps because they land in two different tables,
   * one keyed by the module and one by the placement. They are returned exactly as
   * received and are never merged: merging them would lose which is which, and the
   * distinction decides which page a value applies to.
   *
   * @param moduleId The module whose settings to read. Forwarded exactly as supplied.
   * @param placement The placement whose own settings to include, or omitted for the
   * module-scoped settings alone.
   * @returns Both settings maps, or `null` when the response carried none.
   */
  getModuleSettings(
    moduleId: number,
    placement?: ModulePlacementSelector | null,
  ): Observable<ModuleSettingsBag | null> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http
      .get<
        ApiResponse<ModuleSettingsBag | null>
      >(API_ENDPOINTS.modules.settings(moduleId), { params })
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Replaces one module's settings.
   *
   * `PUT /modules/{moduleId}/settings`, answering `204` with no body. Note the status:
   * this write returns NOTHING, so a caller that needs the stored state reads it back
   * deliberately rather than expecting it in the response.
   *
   * The settings object is transmitted WHOLE, including every key whose value is the
   * empty string. This is the single most sentinel-sensitive payload in the file: the
   * settings maps legitimately hold empty values, the legacy string "absent" marker IS
   * the empty string (`Library/Components/Shared/Null.vb` L71-L75), and the server does
   * not elide default values in either direction. Filtering the maps for truthiness
   * before sending - which is the obvious and wrong implementation - would silently
   * delete every setting a user had cleared, and the request would still answer `204`.
   *
   * @param moduleId The module whose settings to replace. Forwarded exactly as supplied.
   * @param settings The complete settings state, transmitted whole and unfiltered.
   * @param placement The placement whose own settings are being replaced, or omitted.
   * @returns Completion, with no payload.
   */
  updateModuleSettings(
    moduleId: number,
    settings: ModuleSettingsBag,
    placement?: ModulePlacementSelector | null,
  ): Observable<void> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http.put<void>(API_ENDPOINTS.modules.settings(moduleId), settings, { params });
  }

  /**
   * Exports the content held by one module.
   *
   * `POST /modules/{moduleId}/export`, answering **`200` with the exported document in
   * the RESPONSE BODY**. Note that this is a `200` and not a `201`: the request
   * produces a payload rather than creating a resource, which makes it one of only two
   * departures from this API's uniform status map.
   *
   * MIGRATION: THE DOCUMENT COMES BACK IN THE RESPONSE; THE FILESYSTEM HALF OF THE
   *   LEGACY EXPORT IS DROPPED. `Website/admin/Modules/Export.ascx.vb` L143 declared
   *   `ExportModule(ModuleID, FileName, Folder)`, L150 gated on the module declaring a
   *   business controller class and being portable - failing that gate is the origin of
   *   the unsupported-module refusal - L157 obtained the document as a string, and L159
   *   distinguished an empty document, which remains a distinct answer rather than
   *   being collapsed into an absence. Having built the document the legacy page then
   *   wrote it to a folder on the server. No filesystem endpoint exists in this API by
   *   design, so the string is simply returned to the caller.
   *
   * MIGRATION: NO NAME IS COMPUTED HERE. `Export.ascx.vb` L124 composed the stored name
   *   from the module's programmatic name, the operator's text and an extension via a
   *   name-sanitising helper, behind the folder-and-text gate at L121. Neither the
   *   composition nor the helper is carried into this class: whatever name the caller
   *   puts on the request contract is transmitted verbatim and nothing is derived from
   *   it. The request's folder member is likewise forwarded and resolved against
   *   nothing.
   *
   * MIGRATION: Option Strict was OFF for the legacy administration code-behinds
   *   (`Website/release.config` L125), which is what allowed the live double cast at
   *   `Export.ascx.vb` L157 - a late-bound call whose result was cast twice to reach a
   *   string. Strict TypeScript is what forces every such implicit coercion to surface,
   *   and there is none in this method: the payload is declared as text and read as text.
   *
   * The response is read as TEXT rather than as JSON, and that is required rather than
   * stylistic. The server writes the document with an XML media type through a content
   * result, so the client's default JSON parse would throw on the very first character
   * of a well-formed response. No `Accept` header is set: the server deliberately
   * declares the media type on the success response alone, leaving refusals to
   * negotiate as the JSON problem documents every other endpoint returns, and pinning
   * an XML `Accept` here would break exactly that.
   *
   * Nothing is done to the returned document. It is not parsed, not inspected, not
   * validated, not wrapped in a binary container and not handed to a download
   * mechanism - presenting or saving it belongs to the export screen.
   *
   * @param moduleId The module to export. Forwarded exactly as supplied.
   * @param request The name the caller intends for the payload, and an optional folder.
   * @returns The exported document as text, which may legitimately be empty.
   */
  exportModule(moduleId: number, request: ModuleExportRequest): Observable<string> {
    return this.http.post(API_ENDPOINTS.modules.export(moduleId), request, {
      responseType: 'text',
    });
  }

  /**
   * Imports content into a module.
   *
   * `POST /modules/import`, answering `204` with no body.
   *
   * MIGRATION: THERE IS NO IDENTIFIER IN THIS ROUTE, AND THIS METHOD TAKES NO
   *   IDENTIFIER PARAMETER. The target module is a MEMBER OF THE REQUEST CONTRACT. That
   *   mirrors the legacy page, which received its target out of band rather than as
   *   part of its address - `Website/admin/Modules/Import.ascx.vb` L67-L68 parsed it
   *   from a request value into the field declared at L51 - and it reflects that the
   *   payload is adjudicated as a whole: the document, its declared type and the target
   *   are refused together, so splitting the target into the path would let a caller
   *   address a module the payload contradicts. The route template is taken verbatim
   *   from the endpoint declaration and carries no interpolation.
   *
   * MIGRATION: MINUS ONE IS A LEGITIMATE TRANSMITTED VALUE ON THIS CONTRACT.
   *   `Import.ascx.vb` L51 declared the target as `Private Shadows ModuleId As Integer`
   *   initialised to minus one - an identifier field seeded with the integer "absent"
   *   marker from `Library/Components/Shared/Null.vb` L41-L45. The target member is
   *   consequently nullable on the target contract precisely so that an omission can be
   *   told apart from a caller naming module zero, which is a real module. It is never
   *   coalesced, never defaulted, never treated as absent and never guarded on being
   *   positive here; the server rejects an omission on the caller's behalf.
   *
   * MIGRATION: NO FILE IS READ AND NO DOCUMENT IS PARSED HERE. `Import.ascx.vb` L184
   *   opened a stream against the portal's home directory map path joined to the
   *   operator's folder and file name, L188-L192 then constructed a document and
   *   reported a parse failure as the invalid-document message, L197 compared the
   *   declared type against the module's own name - the origin of the wrong-type
   *   refusal at L204 and L217 - and L200 handed the inner markup to the module's
   *   portability contract. Every part of that is server behaviour now. The
   *   invalid-document, wrong-type and unsupported-module refusals
   *   (L208 and L214) are SERVER responses that propagate untranslated, and the acting
   *   account that L200 passed explicitly is taken from the authenticated caller
   *   instead, because an identifier a request could choose for itself would let one
   *   account attribute an import to another.
   *
   * The document travels as text inside the JSON request body. There is no multipart
   * form, no upload primitive and no server path anywhere in this operation.
   *
   * @param request The target module, the document as text, and optional descriptive
   * folder and name members. Transmitted whole.
   * @returns Completion, with no payload.
   */
  importModule(request: ModuleImportRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.modules.import(), request);
  }

  /**
   * Reads the whole module definition catalogue available to the resolved tenant.
   *
   * `GET /module-definitions`, answering `200` with every definition.
   *
   * DELIBERATELY UNPAGED AND UNFILTERED. This endpoint accepts no query parameter at
   * all - not a page coordinate, not an ordering, not a restriction - because the
   * catalogue is small, bounded reference data that the upgrade scripts seed and this
   * application only reads. It is returned whole, so this method takes no arguments.
   *
   * MIGRATION: THE CATALOGUE IS READ-ONLY, AND ITS WRITE HALF IS OUT OF SCOPE
   *   ENTIRELY. There is no method here - and no route on the server - that adds,
   *   changes or removes a definition, nor one that deploys module code. The legacy
   *   mechanism for that wrote archives to disk and reflected over the assemblies it
   *   found, across `PortalModuleBase.vb` (881 lines), `PaWriter.vb` (563),
   *   `PaFileInfo.vb` (87) and `EventMessageProcessor.vb` (125), all excluded: module
   *   registration and lifecycle survive as a server-side domain concern, while the Web
   *   Forms control-loading mechanism does not. Authoring any write method against this
   *   resource would address a route that does not exist.
   *
   * MIGRATION: late-bound activation was replaced by dependency injection on the
   *   server. The five reflective activation sites -
   *   `Library/Components/Modules/ModuleController.vb` L231 and L431, and
   *   `Library/Components/Modules/EventMessageProcessor.vb` L32, L52 and L77 - each
   *   handed a stored type NAME to a reflection helper. A closed, injected set replaced
   *   them, and no type name crosses this boundary in either direction. Every one of
   *   those five sites is ordinary .NET reflection rather than COM interoperation, so
   *   the exclusion covering COM, VB6 and ActiveX removes nothing from this codebase
   *   and is reported as vacuous rather than claimed as work.
   *
   * MIGRATION: the legacy caching layer is NOT reproduced on the client. The 317-line
   *   `Library/Components/Providers/Caching/DataCache.vb` was reached from 116 sites in
   *   scope, 21 of them in `ModuleController.vb` alone - the largest single share - and
   *   caching in the target is a server concern behind an injected cache abstraction.
   *   Reference data like this catalogue is therefore fetched on request: no expiry, no
   *   key vocabulary and no invalidation policy exists in this file, and no method here
   *   clears a cache.
   *
   * @returns Every definition available to the resolved tenant, or an empty list.
   */
  listModuleDefinitions(): Observable<readonly ModuleDefinition[]> {
    return this.http
      .get<
        ApiResponse<readonly ModuleDefinition[]>
      >(API_ENDPOINTS.moduleDefinitions.collection())
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Reads one module definition.
   *
   * `GET /module-definitions/{moduleDefinitionId}`, answering `200` with the
   * definition, or `404` when no such definition is visible to the caller.
   *
   * The route segment is spelled `moduleDefinitionId` in full. That spelling is
   * load-bearing and is NOT the spelling of the corresponding response member, which
   * the contract abbreviates: the parameter and the field are two different names for
   * the same concept and are deliberately not unified by guesswork. Both come from
   * their own declarations - the parameter from the endpoint template, the member from
   * the model.
   *
   * @param moduleDefinitionId The definition to read. Forwarded exactly as supplied.
   * @returns The definition, or `null` when the response carried none.
   */
  getModuleDefinition(moduleDefinitionId: number): Observable<ModuleDefinition | null> {
    return this.http
      .get<
        ApiResponse<ModuleDefinition | null>
      >(API_ENDPOINTS.moduleDefinitions.byId(moduleDefinitionId))
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Reads the module definitions belonging to one deployed module bundle.
   *
   * `GET /module-definitions/desktop-modules/{desktopModuleId}`, answering `200`.
   *
   * TWO THINGS ABOUT THIS ROUTE ARE EASY TO GET WRONG, so both are stated. The bundle
   * identifier is a PATH SEGMENT rather than a query parameter, and the tenant is not a
   * parameter here at all - the server resolves it from the request - so the template
   * takes exactly one identifier. And the response is a list of module DEFINITIONS
   * scoped to that bundle, NOT a list of bundles: no endpoint in this API enumerates
   * bundles, so this method is named for what it returns rather than for the segment it
   * addresses.
   *
   * The hyphenated segment is spelled by the endpoint declaration and never assembled
   * here.
   *
   * @param desktopModuleId The deployed bundle whose definitions to read. Forwarded
   * exactly as supplied.
   * @returns That bundle's definitions, or an empty list.
   */
  listDesktopModuleDefinitions(
    desktopModuleId: number,
  ): Observable<readonly ModuleDefinition[]> {
    return this.http
      .get<
        ApiResponse<readonly ModuleDefinition[]>
      >(API_ENDPOINTS.moduleDefinitions.forDesktopModule(desktopModuleId))
      .pipe(map((envelope) => envelope.data));
  }
}
