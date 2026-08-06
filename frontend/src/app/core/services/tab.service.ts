import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { decodeTabDetail, decodeTabListItem } from '../models/tab.model';
import { arrayOf, decodeResponse, envelopeOf } from '../utils/decode.util';
import { presentedInContext } from './notification.service';

import type { Decoder } from '../utils/decode.util';
import type { TabDetail, TabListItem, UpdateTabRequest } from '../models/tab.model';

/** One decoder per response shape this transport reads, composed once at module scope. */
const TAB_LIST_RESPONSE: Decoder<readonly TabListItem[]> = envelopeOf(arrayOf(decodeTabListItem));
const TAB_DETAIL_RESPONSE: Decoder<TabDetail> = envelopeOf(decodeTabDetail);

/**
 * Transport for the page resource - the abstraction the database, the legacy source and the
 * wire contract all still call a "tab", and which an administrator sees as a Page.
 *
 * A LOOKUP, NOT A FEATURE. This application owns no page-management route and no page
 * feature area: the module screens need to know which page a placement sits on, and this
 * service is how they ask. Nothing referencing it yet does not make it dead code - it is the
 * declared consumer surface for that lookup, and its absence would leave the module screens
 * with no way to resolve a placement's page.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS SERVICE IS ALLOWED TO DO, AND WHY THE LIST IS SO SHORT.
 *
 * One method, one endpoint, one typed request, returned as it stands. Angular services in
 * this application are restricted to API communication, which rules out more than it may
 * appear to:
 *
 * - NO TREE BUILDING. {@link TabService.getByPortal} answers a FLAT list. Folding it into a
 *   hierarchy from `parentId`, `level` and `tabOrder` is a derivation, and it belongs to a
 *   signal store or to the component that renders the indentation. Doing it here would make
 *   every caller pay for a shape only some of them want, and would put a second
 *   interpretation of the hierarchy in a file whose job is transport.
 * - NO sorting, filtering, grouping, paging or reshaping of a response beyond unwrapping the
 *   transport envelope described below.
 * - NO validation. The server owns every rule, and the reason codes it answers with are
 *   listed on {@link TabService.update}. Re-checking a rule here would create a second
 *   implementation of it that could disagree with the first.
 * - NO caching, NO retry, NO multi-call orchestration, NO derived state.
 * - NO headers. The correlation identifier, the bearer credential and the translation of an
 *   RFC 7807 problem document are each applied by a request interceptor, registered once in
 *   `app.config.ts` in a fixed order: the correlation interceptor first, so the server's
 *   correlation middleware receives an identifier it can echo back into a problem document's
 *   trace field; the authentication interceptor second; the error interceptor last, so it
 *   observes the final response after any credential refresh has been retried. Setting a
 *   header here would duplicate or defeat one of them.
 * - NO subscription and no blocking conversion to a promise. Every method hands back a cold
 *   observable, and the caller decides when it runs and when it stops.
 * ---------------------------------------------------------------------------
 *
 * MIGRATION: THE PAGE SURFACE IS DELIBERATELY NARROW - THREE ENDPOINTS, AND THAT CLOSES A
 * GENUINELY LARGE LEGACY SURFACE. `Library/Components/Tabs/TabController.vb` is 1,302 lines
 * and `Library/Components/Tabs/TabInfo.vb` a further 616; only page lookup and page update
 * are carried forward. Creating, deleting, reordering, moving, exporting, importing and
 * restoring a page are all absent, and each was measured in the legacy source rather than
 * assumed away:
 *
 * - `Website/admin/Tabs/Tabs.ascx.vb:L70` declares `Private Sub DeleteTab()` and `:L73` calls
 *   `TabController.DeleteTab(objTab.TabID, PortalSettings, UserId)`.
 * - `:L214` declares `Private Sub UpDown_Click(...) Handles cmdDown.Click, cmdUp.Click`, and
 *   the four ordering calls at `:L188`, `:L190`, `:L223` and `:L225` drive
 *   `objTabs.UpdatePortalTabOrder(PortalId, objTab.TabId, objTab.ParentId, ...)` with the
 *   one-step level and order deltas the arrow buttons produced. Nudging a hierarchy by
 *   relative steps depends on the whole tree being rendered and posted back in one request,
 *   which is precisely the presentation model this migration replaces.
 * - `Website/admin/Tabs/Export.ascx.vb` and `Website/admin/Tabs/Import.ascx.vb` moved page
 *   definitions through server-side folders, and this API publishes no filesystem surface.
 * - `Website/admin/Tabs/RecycleBin.ascx.vb` restored and purged soft-deleted pages. The
 *   reversible half of that survives, but as a field rather than as a route: `isDeleted` on
 *   {@link UpdateTabRequest}. Permanent purging is out of scope.
 *
 * Those seven line citations were re-read first-hand against this checkout rather than
 * carried from a planning summary, and all seven matched.
 *
 * MIGRATION: THE SKIN AND CONTAINER TOKENS ARE READABLE BUT NOT WRITABLE. Page skinning and
 * containers are out of scope for this migration, so `skinSrc` and `containerSrc` appear on
 * the read shape {@link TabDetail} - a stored choice stays observable - and are deliberately
 * absent from {@link UpdateTabRequest}. This service therefore accepts no argument for either
 * and passes neither through; the legacy skin-and-container composition became the static
 * application shell under `app/layout/` instead. Because an update replaces the whole
 * editable row, omitting them is also strictly safer than carrying them: a caller that left
 * either out of a body that DID declare them would blank an administrator's stored token on
 * every unrelated edit.
 *
 * MIGRATION: LEGACY CACHING IS NOT REPRODUCED ON THE CLIENT. The legacy page and permission
 * controllers read and wrote a shared static cache on almost every call - 116 in-scope call
 * sites in total, of which the page controller alone accounted for 12 and the page-permission
 * controller a further 10 - and invalidated it with coarse portal-wide and host-wide clears.
 * Source: `Library/Components/Providers/Caching/DataCache.vb`, 317 lines. Caching now lives
 * on the server, behind its own abstraction over an in-memory cache, where a single
 * invalidation is correct for every client at once; nothing is cached here, and no response
 * is memoised. NOTE THE PATH: the frozen plan cites this file at
 * `Library/Components/Shared/DataCache.vb`, which does not exist in this checkout - the real
 * location is the one above, verified together with its line count.
 *
 * @see `Dtos/Tab/TabListItemDto.cs`, `Dtos/Tab/TabDetailDto.cs` and
 * `Dtos/Tab/UpdateTabRequest.cs` for the server shapes these contracts mirror, and
 * `TabsController.cs` for the routes.
 */
@Injectable({ providedIn: 'root' })
export class TabService {
  private readonly http = inject(HttpClient);

  // THE PAYLOAD ARRIVES INSIDE THE SHARED SUCCESS ENVELOPE, so each method below unwraps it
  // before anything reads it. Every one of these three actions returns its outcome through the
  // API's one shared result translator, which ends in `Ok(ApiResponse<TValue>.Success(value))`
  // and whose own remark records that "an action cannot opt out of the envelope without
  // abandoning this helper". A body is therefore `{ data, meta }`, not the payload itself.
  //
  // Typing a call as the bare payload would compile, bundle and deploy without a single
  // diagnostic, and then fail at run time in the quietest possible way: every member of every
  // page would read as `undefined`, and a caller would render a shape-correct blank rather than
  // an error it could report. The envelope contract is imported for its TYPE ONLY, so this
  // unwrapping costs no runtime dependency on the module that declares it.
  //
  // `meta` is ignored rather than forwarded, and deliberately: it describes a PAGE of records,
  // and none of these three responses is paged. The server sends it as an explicit `null` here
  // for exactly that reason. The page list in particular is UNPAGED - a hierarchy is read whole,
  // because a partially fetched tree cannot be indented correctly - so it travels through the
  // single-payload envelope and NOT through the shared paging envelope. No paging coordinate,
  // sort key or filter is sent by any method below, and no paged wrapper is returned by one.

  /**
   * Lists every page belonging to one portal, as a flat collection.
   *
   * `GET /api/v1/portals/{portalId}/tabs`, answering `200` with the rows. The route is
   * portal-scoped because a page LISTING belongs to its tenant, while an individual page is
   * addressed at the root by {@link TabService.getById} - the two endpoints have deliberately
   * different shapes, and the portal-scoped form is the only way to list. There is no flat
   * `tabs` collection to filter, so this call cannot be replaced by one.
   *
   * UNPAGED, and unlike every other list in this application. The whole hierarchy comes back in
   * one response so that it can be indented from a single answer.
   *
   * Rows arrive in the server's order and are handed on untouched. Both the menu-inclusion flag
   * and the recycle-bin flag are present on each row precisely so that a caller can reproduce
   * the legacy list's asymmetry - it excluded soft-deleted pages while including menu-excluded
   * ones - and neither is applied here.
   *
   * MIGRATION: `Optional ByVal` PARAMETER TAILS BECOME EXPLICIT ARGUMENTS. The legacy page
   * controller hid behavioural switches in defaulted trailing parameters -
   * `Library/Components/Tabs/TabController.vb:L243` declares
   * `Private Sub MoveTab(..., Optional ByVal blnAddChild As Boolean = True)` and `:L550`
   * declares `Public Sub UpdatePortalTabOrder(..., Optional ByVal NewTab As Boolean = False)` -
   * so a caller could change what a call did by omitting an argument, and a reader of the call
   * site could not see which behaviour had been selected. Every parameter on this service is
   * required and named, and no method carries a default: what a call does is legible from the
   * call itself.
   *
   * @param portalId The tenant whose pages are wanted. Interpolated exactly as supplied - see
   * the sentinel note on {@link TabService.getById}, which applies equally here: `0` and `-1`
   * are both real portals.
   * @returns The portal's pages, in the order the server returned them. Failures propagate as
   * the server's own problem document: `tab.portal_not_found` when no portal bears the
   * identifier, mapped to `404`; `401` when no valid credential was presented; `403` when the
   * caller holds no administrative grant on the portal.
   */
  getByPortal(portalId: number): Observable<readonly TabListItem[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.tabs.forPortal(portalId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(TAB_LIST_RESPONSE, body)));
  }

  /**
   * Retrieves one page in full.
   *
   * `GET /api/v1/tabs/{tabId}`, answering `200` with the detail shape. Addressed at the root
   * without its portal, because a page identifier is unique across an installation.
   *
   * MIGRATION: ZERO IS A REAL PAGE IDENTIFIER, AND MINUS ONE IS A REAL TENANT IDENTIFIER, so no
   * request this service makes is guarded on an identifier being truthy or positive. Two facts
   * collide in the legacy schema. `dbo.Tabs` is declared `[TabID] [int] IDENTITY (0, 1)`
   * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L140`), so the
   * first page an installation ever creates is numbered zero; `dbo.Portals` is declared
   * `[PortalID] [int] IDENTITY (-1, 1)` (`:L77`), so the first portal carries minus one and the
   * second carries zero; and `Library/Components/Shared/Null.vb:L41-L45` simultaneously defines
   * `NullInteger` as minus one, its body literally `Return -1`. The same number therefore means
   * both "the first portal" and "no portal", distinguishable only by context that a transport
   * layer does not have. The identifier is consequently interpolated exactly as it was handed
   * over: never tested for truthiness, never compared against zero or minus one, never
   * defaulted, and never coalesced in either direction. Deciding whether an identifier is known
   * belongs to the caller, which has the context to decide it - and the caller should make that
   * decision by comparing against `null`, since no numeric value is available to mean "absent".
   *
   * The sibling string sentinel is worth knowing for the same reason, because it is the one that
   * defeats intuition: `Null.vb:L71-L75` returns the EMPTY STRING for a missing string rather
   * than a null, its body literally `Return ""`. The server converts both sentinels to genuine
   * nulls at its boundary, which is why every optional member of {@link TabDetail} is typed
   * nullable rather than optional - so read a cleared value as `null`, not as `-1` or `''`.
   *
   * @param tabId The page wanted. Interpolated exactly as supplied. The parameter name is
   * load-bearing on the server side as well: the route is declared `tabs/{tabId:int}` and the
   * authorisation handler that protects it resolves the page it is checking from a route value
   * named `tabId` — and from THAT NAME ALONE. There is no fallback to a bare `id`, and the
   * handler documents why it admits none: a generic fallback would let a nested route hand it
   * some other entity's key, deciding a page question from an account's identifier, which is
   * worse than refusing (`Api/Authorization/PermissionAuthorizationHandler.cs:L112-L128`). A
   * differently named parameter therefore loses the authorisation scope outright.
   * @returns The page. Failures propagate as the server's own problem document:
   * `tab.not_found` when no page bears the identifier, mapped to `404`; `401` when no valid
   * credential was presented; `403` when the caller holds no view grant - which the server also
   * answers for a page that does not exist, deliberately, so that the two are indistinguishable
   * to a caller probing for identifiers.
   */
  getById(tabId: number): Observable<TabDetail> {
    return this.http
      .get<unknown>(API_ENDPOINTS.tabs.byId(tabId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(TAB_DETAIL_RESPONSE, body)));
  }

  /**
   * Replaces the editable state of one page.
   *
   * `PUT /api/v1/tabs/{tabId}`, answering `200` with the page as it now stands.
   *
   * A COMPLETE REPLACEMENT OF THE EDITABLE SUBSET, NOT A PARTIAL PATCH, exactly as the legacy
   * postback was. An absent field does not mean "leave that column alone": the server writes the
   * value it binds, so a missing member clears a nullable column, sets a flag false, and moves
   * the page to the root of the hierarchy. A caller must therefore send the page's CURRENT
   * values for everything it does not intend to change. Two consequences deserve stating because
   * each is a data-loss trap: omitting the menu-inclusion flag drops the page out of the
   * navigation, and omitting the recycle-bin flag RESTORES a page that was sitting in the
   * recycle bin.
   *
   * The body is forwarded byte-for-byte as assembled, with no member stripped and none added.
   * That fidelity is required rather than incidental: the server serialises with an ignore
   * condition of `Never`, so a field is present-but-possibly-null and never missing, and `0`,
   * `-1`, `''` and `false` are every one of them legitimate stored values in this schema.
   * Dropping a member because it looked empty would silently rewrite a row.
   *
   * THE FAILURE CONTRACT, MEASURED FROM THE SERVER RATHER THAN ASSUMED. This endpoint declares
   * `200`, `400`, `401`, `403` and `404`, and the reason codes its service can produce are:
   *
   * - `tab.name_reserved` - `400`. The page name is a reserved device name. The legacy
   *   reserved-name check is preserved as a stable reason code.
   * - `tab.parent_cycle` - `400`. The requested parent is the page itself, or one of its own
   *   descendants. MIGRATION: a discovered legacy defect is corrected here rather than carried.
   *   When the legacy screen's ancestry walk tripped it abandoned the save and rendered nothing
   *   at all - a silent no-op a user could easily read as success. The rejection is preserved;
   *   the silence is not.
   * - `tab.parent_cross_portal` - `400`. The requested parent belongs to another tenant. This
   *   matters more than it did on the legacy screen, because the value now arrives in a request
   *   body rather than from a tenant-filtered picker.
   * - `tab.parent_not_found` - `404`. A parent named in the body does not exist.
   * - `401` when no valid credential was presented, and `403` when the caller holds no edit
   *   grant on the page - which the server also answers when the page does not exist.
   *
   * NONE of these is pre-checked here, and none is translated into a message here. Surfacing a
   * problem document to a person is the error interceptor's job together with the shared
   * form-error helper; this method's only responsibility is to let the failure through unaltered.
   *
   * THERE IS DELIBERATELY NO `409 Conflict` ON THIS ENDPOINT, and its absence is a measured
   * finding rather than an oversight. The legacy duplicate-path refusal sat behind an
   * `If String.IsNullOrEmpty(strAction)` guard in the page editor, while the edit branch was
   * entered under `If strAction = "edit"`, so the duplicate check never ran on an update at all -
   * it belonged to the create path, which this API deliberately does not publish. The service
   * declares no conflict reason code to match. Two codes named in this file's own brief,
   * `InvalidTabName` and `TabExists`, were searched for across the whole server tree and exist
   * nowhere in it; the codes listed above are the real ones. Documenting a status no request can
   * elicit would mislead the next reader more than saying nothing would, so it is not documented.
   *
   * @param tabId The page to replace. Interpolated exactly as supplied; the sentinel and
   * parameter-name notes on {@link TabService.getById} apply unchanged.
   * @param request The complete editable state. Carries no skin or container token by design -
   * see the note on the class - and no identifier, no tenant, no sort position, no depth and no
   * materialised path, because the route supplies the first and the server recomputes the rest.
   * @returns The page as it now stands, so a caller can render the server's own result rather
   * than the state it hoped it had written.
   */
  update(tabId: number, request: UpdateTabRequest): Observable<TabDetail> {
    return this.http
      .put<unknown>(API_ENDPOINTS.tabs.byId(tabId), request, { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(TAB_DETAIL_RESPONSE, body)));
  }
}
