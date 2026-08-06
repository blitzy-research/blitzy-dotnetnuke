import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map } from 'rxjs';

import type { HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { toPagedResult } from '../models/paged-result.model';
import { emptyQueryParams, portalListParams } from '../utils/http-params.util';

import type { ApiResponse, PagedResponse } from '../models/paged-result.model';
import type {
  CreatePortalAliasRequest,
  CreatePortalRequest,
  PortalAlias,
  PortalDetail,
  PortalListItem,
  PortalListPage,
  PortalSettings,
  UpdatePortalAliasRequest,
  UpdatePortalRequest,
  UpdatePortalSettingsRequest,
} from '../models/portal.model';
import type { PagedRequestParams, PortalListFilter } from '../utils/http-params.util';

/**
 * The portal (tenant) resource, its settings projection and its host-name aliases.
 *
 * A TYPED TRANSPORT ONLY. Every member below is one HTTP call to one endpoint,
 * returning the observable the framework produced. There is no validation, no
 * derivation of a domain value, no orchestration of two calls into one, no retry
 * policy and no cache: Angular services are restricted to API communication by the
 * migration discipline, and the decisions those things would embody belong either to
 * the server or to the portal feature store. Nothing here subscribes, so nothing here
 * can start a request; a caller that does not subscribe makes no call at all.
 *
 * ---------------------------------------------------------------------------
 * THE SURFACE IS CLOSED AT TWELVE MEMBERS, one for each action the two controllers
 * publish. It spans `PortalsController` (seven) and `PortalAliasesController`
 * (five), which are two classes on the server but one resource to a caller: an
 * alias has no identity apart from the portal it resolves to, so both live here
 * rather than in a service of their own.
 *
 * Adding a thirteenth would mean inventing a route. Several plausible ones do NOT
 * exist and must not be added: no key/value settings accessor (see the settings note
 * below), no site-wizard or portal-template operation, no arbitrary-SQL execution, no
 * bulk delete, no filesystem or upload endpoint, and no expired-portal listing (see
 * the migration note on {@link PortalService.list}). The page collection beneath a
 * portal is deliberately absent too — `GET /api/v1/portals/{portalId}/tabs` is served
 * by the page controller and belongs to `core/services/tab.service.ts`, because that
 * controller also serves the standalone page resource and splitting one controller
 * across two services would put the same rows behind two owners.
 *
 * The health endpoint is likewise unreachable from here by design. It is mapped at
 * the host root, outside the versioned prefix, anonymous and unthrottled, because the
 * container health check and the compose dependency condition probe it before the
 * application is serving anything; a service call to it would be meaningless.
 *
 * ---------------------------------------------------------------------------
 * WHY EACH METHOD UNWRAPS AN ENVELOPE, AND WHY THAT IS NOT LOGIC.
 *
 * The API does not return bare contracts. A single record arrives inside
 * `ApiResponse<T>` as `{ data, meta }`, and a page arrives as `{ items, meta }`
 * (`PagedResponse<T>`). Both shapes are transport framing rather than domain data, so
 * each method types its call as the framing it will actually receive and hands back
 * the payload. Typing a call as the bare payload instead is the quietest defect
 * available in this code base: it compiles, it returns 200, and every member of the
 * result reads as `undefined` because the real body had them one level deeper.
 *
 * The paged member routes its body through the normaliser the paging contract owns
 * rather than reading `items` and `meta` here, so the one place a page count may be
 * derived stays the one place that already documents doing it.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS SERVICE DOES NOT PUT ON THE WIRE.
 *
 * No URL is built here. Paths come from `core/config/api-endpoints.ts`, which is the
 * only module that reads the configured base, and each template is passed to the
 * client exactly as returned. Prefixing a returned template again would produce a
 * doubled version segment — a 404 that no compiler, no linter and no successful build
 * detects.
 *
 * No query string is built here either. Parameters come from
 * `core/utils/http-params.util.ts`, the only module that serialises one, so that
 * encoding happens once and so that the rule about which values count as absent is
 * applied in one place.
 *
 * No header is set here. The correlation identifier, the bearer token and the
 * translation of an RFC 7807 problem document are applied by the three interceptors
 * registered in `app.config.ts`, in that fixed order: the correlation identifier
 * first so the server's middleware consumes it and echoes it back on a fault, the
 * token second, and the error translation last so it observes the final response
 * after any token refresh has been retried.
 */
@Injectable({ providedIn: 'root' })
export class PortalService {
  private readonly http = inject(HttpClient);

  /**
   * Lists portals, a page at a time, optionally restricted by name.
   *
   * The only paged member of this service. The alias collection is deliberately
   * unpaged — see {@link PortalService.listAliases}.
   *
   * Both arguments are forwarded to the parameter serialiser untouched. This method
   * supplies no page size of its own: an omitted size is transmitted as an omission
   * so the server applies its own default, because the effective size is a per-portal
   * setting rather than a constant, and the single client-side copy of the legacy
   * default of ten lives in the paging contract.
   *
   * MIGRATION: the page index on the wire is ZERO-BASED, and no arithmetic is applied
   * to it here or anywhere downstream. The legacy screen counted from one and
   * subtracted one immediately before calling the provider —
   * `Website/admin/Portal/Portals.ascx.vb:L142` reads
   * `PortalController.GetPortalsByName(Filter + <wildcard>, CurrentPage - 1, PageSize, TotalRecords)`,
   * where the third and fourth arguments are the page size and the receiving total.
   * The one-based counter survives only as a presentation concern in the pager
   * component, and the mapping between the two bases is performed in the portal
   * feature store. Adjusting the index here would serve the neighbouring page and
   * report success while doing it.
   *
   * MIGRATION: the search pattern is the SERVER'S to compose, and this method
   * contributes no character to it. That same legacy line concatenated a trailing
   * wildcard onto the operator's text at the call site, before it reached the data
   * layer. Match semantics now belong entirely to the repository behind the
   * data-access abstraction, so the caller's text is transmitted byte for byte:
   * untrimmed, its case unchanged and undecorated.
   *
   * Note that the legacy pattern and the predicate the repository issues today are NOT
   * the same test, and a caller that assumes otherwise will mis-describe the field to a
   * user. The legacy pattern anchored at the start of the name; the repository —
   * `Repositories/PortalRepository.cs:L141-L142` in the infrastructure project — trims
   * the text, folds its case and matches it anywhere within the name. Verified against
   * the running API rather than inferred: a mid-word fragment returns the portals whose
   * names merely include it, where an anchored predicate would return none. That
   * difference is the server's to state and to change, and contributing a character
   * here could only double whatever pattern the repository already builds.
   *
   * MIGRATION: the legacy screen's `Expired` pseudo-filter has NO successor and none
   * is invented. `Portals.ascx.vb:L138-L140` compared the filter text against
   * `Localization.GetString("Expired", LocalResourceFile)` and, on a match, called
   * `PortalController.GetExpiredPortals()` and hid the pager outright — so which
   * query ran depended on the display language, and translating a resource file
   * changed the behaviour of the screen. No endpoint exposes that listing, and no
   * member of this service may branch on user-facing text.
   *
   * MIGRATION: the legacy URL keys were lower-case and one-based. The `FilterURL`
   * helper at `Portals.ascx.vb:L215-L232` emitted `"filter=" & Filter` and
   * `"currentpage=" & CurrentPage`, the latter carrying the screen's one-based value.
   * The target spells its parameters in camel case and counts from zero, so both the
   * spelling and the base changed, and both changes are deliberate.
   *
   * MIGRATION: replaces the untyped collection `PortalController.vb:L1263` returned —
   * `Public Function GetPortals() As ArrayList` — which carried neither an element
   * type nor a total, so a caller could not tell how many records existed beyond the
   * page it held. The page and its total now travel together on one value.
   *
   * MIGRATION: none of the legacy caching is reproduced on the client. The legacy
   * portal controller reached the shared cache thirteen times, and its writes cleared
   * whole scopes at a stroke — `PortalController.vb:L916` calls
   * `DataCache.ClearPortalCache(PortalId, True)` and `:L1128` calls
   * `DataCache.ClearHostCache(True)`, one of one hundred and sixteen in-scope call
   * sites into `Library/Components/Providers/Caching/DataCache.vb` (317 lines; note
   * the path — the action plan cites a `Library/Components/Shared/DataCache.vb` that
   * does not exist in this repository). Caching is now a server concern behind an
   * in-memory cache service with named keys and explicit invalidation. Nothing is
   * cached here, so nothing here can serve a stale portal.
   *
   * @param request The zero-based page index, the page size, the ordering and the
   * paging contract's own free-text filter. Every member is optional; an omitted one
   * is not sent.
   * @param filter The portal-name restriction, or omitted or `null` to list every
   * portal. `null` is accepted because a reset filter control yields it and it means
   * the same thing as omission.
   * @returns One page of portal rows, with the total and the coordinates the server
   * reported.
   */
  list(
    request: PagedRequestParams,
    filter?: PortalListFilter | null,
  ): Observable<PortalListPage> {
    const params: HttpParams = portalListParams(request, filter);

    // Typed as the paged envelope because that is the shape received: `items` and
    // `meta` sit at the top level of this body rather than inside `data`. The
    // normaliser belongs to the paging contract, so the page count it may derive is
    // derived in the one module that documents deriving it.
    return this.http
      .get<PagedResponse<PortalListItem>>(API_ENDPOINTS.portals.collection(), { params })
      .pipe(map(toPagedResult));
  }

  /**
   * Reads one portal in full.
   *
   * The identifier is interpolated exactly as supplied and is never tested first.
   *
   * MIGRATION: `0` AND `-1` are both real portal identifiers, so no request may be
   * guarded on one being non-zero, positive or unequal to the absent-integer marker.
   * Two facts collide in the legacy schema.
   * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77`
   * declares `[PortalID] [int] IDENTITY (-1, 1) NOT NULL`, so the first portal ever
   * created carries `-1` and the second carries `0`; and
   * `Library/Components/Shared/Null.vb:L41-L45` defines the missing-integer marker as
   * `-1`, its body being literally `Return -1`. The same number therefore means both
   * "the first portal" and "no portal", distinguishable only by context a transport
   * does not have. A truthiness test would drop a request for the second portal, and a
   * comparison against the marker would drop a request for the first. The server holds
   * a dedicated identifier type for the same reason, and its route constraint accepts
   * every integer rather than imposing a lower bound.
   *
   * The related string marker is `Null.vb:L71-L75`, whose body is literally
   * `Return ""` — so an empty string is the legacy spelling of an absent string, not
   * a null reference. That is why no member of this service coalesces an empty string
   * into a null, or the reverse.
   *
   * @param portalId The portal to read. Every integer is meaningful.
   * @returns The portal. A portal that does not exist is reported as `404` by the
   * server rather than as a successful response carrying nothing, so a response that
   * reaches a subscriber always carries a record.
   */
  getById(portalId: number): Observable<PortalDetail> {
    return this.http
      .get<ApiResponse<PortalDetail>>(API_ENDPOINTS.portals.byId(portalId))
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Creates a portal.
   *
   * The body is sent exactly as the caller composed it. Every member the request
   * contract declares is transmitted, including one holding `0`, `-1`, an empty
   * string or `false`: each of those is a value in this domain rather than an
   * absence, and the server's own serialiser is configured never to elide a written
   * member, so the two sides of the wire agree about what "present" means. Stripping
   * a member here because it looked empty would silently ask for a different portal
   * than the one described.
   *
   * MIGRATION: a positional parameter list becomes a request contract.
   * `Library/Components/Portal/PortalController.vb:L980` declared
   * `CreatePortal` with FIFTEEN positional parameters, eleven of them `String`, so
   * adjacent arguments were interchangeable to the compiler and a transposed pair
   * produced a portal with its description in its keywords and no error anywhere. A
   * named contract cannot be transposed.
   *
   * @param request The portal to create, including its first host name and the
   * administrator account to establish alongside it.
   * @returns The created portal, as the server stored it. Answered `201`.
   */
  create(request: CreatePortalRequest): Observable<PortalDetail> {
    return this.http
      .post<ApiResponse<PortalDetail>>(API_ENDPOINTS.portals.collection(), request)
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Replaces one portal's editable state.
   *
   * The identifier travels in the path AND on the body, because the request contract
   * declares its own and the server requires the two to agree. Both are forwarded
   * unchanged and neither is reconciled here: detecting a disagreement is the server's
   * check, and silently overwriting one with the other would hide a caller's mistake
   * and write a portal it did not mean to address.
   *
   * MIGRATION: a positional parameter list becomes a request contract.
   * `PortalController.vb:L1568` declared `UpdatePortalInfo` with TWENTY-SEVEN
   * positional parameters and passed all twenty-seven straight through to the data
   * provider in the same order, so the call site and the provider had to agree on a
   * sequence nothing verified.
   *
   * @param portalId The portal to write. Must match the identifier on the body.
   * @param request The complete editable state to store.
   * @returns The portal as stored.
   */
  update(portalId: number, request: UpdatePortalRequest): Observable<PortalDetail> {
    return this.http
      .put<ApiResponse<PortalDetail>>(API_ENDPOINTS.portals.byId(portalId), request)
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Removes one portal.
   *
   * @param portalId The portal to remove. Every integer is meaningful — see the
   * identifier note on {@link PortalService.getById}.
   * @returns Nothing. Answered `204`, so there is no body to read; a refusal because
   * the installation must retain at least one portal is reported as a conflict
   * carrying the reason `portal.last_remaining`, and a re-sent identical request
   * cannot succeed while that state holds.
   */
  delete(portalId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.portals.byId(portalId));
  }

  /**
   * Reads one portal's configuration projection.
   *
   * MIGRATION: there is NO portal-settings table, and this endpoint is therefore not
   * a key/value accessor. Four findings establish it: the abstract data surface
   * declares no portal-setting member among its two hundred and sixty-nine overrides;
   * the concrete provider invokes no portal-setting procedure among its two hundred
   * and forty-five; no such table appears in any of the eighty-eight schema scripts,
   * which define only module, host, page-module and schedule-item settings; and
   * positively, `Library/Components/Portal/PortalController.vb:L1209-L1210` shows
   * `GetCurrentPortalSettings()` returning
   * `CType(HttpContext.Current.Items("PortalSettings"), PortalSettings)` — a composite
   * assembled per request and held in ambient request state, never a persisted
   * aggregate.
   *
   * Portal configuration is therefore columns on the portal row, this contract is a
   * projection of those columns, and the ambient composite became an immutable
   * request-scoped context on the server. Consequently there is deliberately no
   * single-setting read, no single-setting write and no partial patch on this service:
   * the whole projection is read and the whole projection is replaced. Several legacy
   * members of that composite have no column behind them and are not carried forward
   * at all, so no member of this service names one.
   *
   * @param portalId The portal whose settings to read.
   * @returns The settings projection.
   */
  getSettings(portalId: number): Observable<PortalSettings> {
    return this.http
      .get<ApiResponse<PortalSettings>>(API_ENDPOINTS.portals.settings(portalId))
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Replaces one portal's configuration projection.
   *
   * Shares its URL with {@link PortalService.getSettings} because the two are the read
   * and write representations of one settings screen rather than two resources.
   *
   * The body is the write contract minus its identifier: the portal is already named
   * by the path, so accepting it twice would create a disagreement the endpoint would
   * then have to detect. That is why this takes the settings write contract and not
   * the projection it returns — a shape built for reading is the wrong shape for a
   * write, and the server publishes them separately for that reason.
   *
   * @param portalId The portal to write.
   * @param request The complete settings state to store.
   * @returns The settings projection as stored.
   */
  updateSettings(
    portalId: number,
    request: UpdatePortalSettingsRequest,
  ): Observable<PortalSettings> {
    return this.http
      .put<ApiResponse<PortalSettings>>(API_ENDPOINTS.portals.settings(portalId), request)
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Lists every host name bound to one portal.
   *
   * DELIBERATELY UNPAGED, and the empty parameter set says so explicitly rather than
   * by omission. A portal's aliases are small, bounded reference data returned whole,
   * exactly as the legacy screen returned them, so no page index, page size, ordering
   * or filter may be sent: the server binds none of them, and a parameter it does not
   * bind is discarded silently, leaving a caller believing it had asked for something
   * it had not.
   *
   * MIGRATION: exact-match alias resolution replaced a substring match, and the change
   * closed a multi-tenant mis-resolution hazard rather than tidying a query. The legacy
   * tenant-resolution procedure — created as `GetPortalSettings` at
   * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569`,
   * whose name describes a settings reader and whose body is a tenant resolver —
   * matched the requested host name with a LIKE predicate wrapping it in wildcards on
   * both sides, at `:L4582`. One portal's alias being a substring of another's was
   * therefore enough to resolve a request to the wrong tenant, and the lowest matching
   * identifier won. Resolution is
   * now an exact match on the host name in the request, performed by the alias
   * resolution middleware. That procedure was dropped for good at
   * `02.02.00.SqlDataProvider:L267` and superseded by `GetPortal` taking a portal
   * identifier at `:L273`.
   *
   * MIGRATION: the legacy query-string key `paid` is not carried forward.
   * `Website/admin/Portal/EditPortalAlias.ascx.vb:L57` addressed an alias row through
   * `Request.QueryString("paid")`; the target names it in full as a path segment,
   * because a segment is read far more often than it is typed.
   *
   * @param portalId The portal whose aliases to list.
   * @returns Every alias of the portal, in the order the server returned them.
   */
  listAliases(portalId: number): Observable<readonly PortalAlias[]> {
    const params: HttpParams = emptyQueryParams();

    return this.http
      .get<ApiResponse<readonly PortalAlias[]>>(
        API_ENDPOINTS.portalAliases.forPortal.collection(portalId),
        { params },
      )
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Binds an additional host name to one portal.
   *
   * @param portalId The portal to bind the host name to.
   * @param request The host name to bind.
   * @returns The created alias, carrying the identifier the database assigned.
   * Answered `201`; a host name already bound elsewhere is reported as a conflict
   * carrying the problem type `portal.alias_duplicate`.
   */
  createAlias(
    portalId: number,
    request: CreatePortalAliasRequest,
  ): Observable<PortalAlias> {
    return this.http
      .post<ApiResponse<PortalAlias>>(
        API_ENDPOINTS.portalAliases.forPortal.collection(portalId),
        request,
      )
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Reads one alias of one portal.
   *
   * Both identifiers are named rather than positional at the template boundary, so a
   * transposed pair is a compile error instead of a request for a row that does not
   * exist. They are forwarded exactly as supplied and neither is tested.
   *
   * @param portalId The portal that owns the alias.
   * @param portalAliasId The alias to read.
   * @returns The alias. One that does not exist within that portal is reported as
   * `404`.
   */
  getAlias(portalId: number, portalAliasId: number): Observable<PortalAlias> {
    return this.http
      .get<ApiResponse<PortalAlias>>(
        API_ENDPOINTS.portalAliases.forPortal.byId({ portalId, portalAliasId }),
      )
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Changes the host name one alias binds.
   *
   * Returns NOTHING, and that is the endpoint's contract rather than an omission here:
   * this is the one write in this service that answers `204` instead of returning the
   * record it wrote, so a caller that needs the stored row reads it back with
   * {@link PortalService.getAlias}. The owning portal is not re-bound by this call —
   * the request contract carries the host name alone.
   *
   * @param portalId The portal that owns the alias. Checked by the server against the
   * stored row's owner, because changing another tenant's alias would make that tenant
   * unreachable at the host name its users hold.
   * @param portalAliasId The alias to change.
   * @param request The host name to store in place of the current one.
   * @returns Nothing. Answered `204`; a host name already bound to another alias is
   * reported as a conflict carrying the problem type `portal.alias_duplicate`.
   */
  updateAlias(
    portalId: number,
    portalAliasId: number,
    request: UpdatePortalAliasRequest,
  ): Observable<void> {
    return this.http.put<void>(
      API_ENDPOINTS.portalAliases.forPortal.byId({ portalId, portalAliasId }),
      request,
    );
  }

  /**
   * Unbinds one alias from one portal.
   *
   * @param portalId The portal that owns the alias.
   * @param portalAliasId The alias to unbind.
   * @returns Nothing. Answered `204`, including when the alias was the portal's last:
   * the legacy screen hid the affordance in that case but never refused the write, and
   * no refusal is invented.
   */
  deleteAlias(portalId: number, portalAliasId: number): Observable<void> {
    return this.http.delete<void>(
      API_ENDPOINTS.portalAliases.forPortal.byId({ portalId, portalAliasId }),
    );
  }
}

// MIGRATION: the legacy administration pages compiled with Option Strict OFF —
//   `Website/release.config:L125` declares `<compilation debug="false" strict="false">` —
//   so the thirteen portal code-behinds this service replaces could legally contain late
//   binding and implicit narrowing conversions that no compiler reported. Every coercion
//   that survived into the target is explicit, and strict TypeScript is what forces one to
//   surface here: with `strict`, `isolatedModules` and no implicit `any`, a member whose
//   type does not match the contract it is sent to is a build failure rather than a value
//   quietly widened, narrowed or read as `undefined` at run time. No type assertion, no
//   non-null assertion and no suppression comment appears above, which is what makes that
//   guarantee hold for this file rather than merely being available to it.
