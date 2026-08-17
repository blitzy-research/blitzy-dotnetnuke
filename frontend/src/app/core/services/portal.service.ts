import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map } from 'rxjs';

import type { HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import {
  decodePortalAdministrator,
  decodePortalAlias,
  decodePortalDetail,
  decodePortalListItem,
  decodePortalSettings,
} from '../models/portal.model';
import { arrayOf, decodeResponse, envelopeOf, pageOf } from '../utils/decode.util';
import {
  carriesPortalSearchText,
  emptyQueryParams,
  portalListParams,
  portalSearchBody,
} from '../utils/http-params.util';
import { presentedInContext } from './notification.service';

import type { Decoder } from '../utils/decode.util';
import type {
  CreatePortalAliasRequest,
  CreatePortalRequest,
  PortalAdministrator,
  PortalAlias,
  PortalDetail,
  PortalListPage,
  PortalSettings,
  UpdatePortalAliasRequest,
  UpdatePortalRequest,
  UpdatePortalSettingsRequest,
} from '../models/portal.model';
import type { PagedRequestParams, PortalListFilter } from '../utils/http-params.util';

/**
 * One decoder per response shape this transport reads, composed once at module scope. Composed here
 * rather than inside each method because a decoder is a pure value: building it once per module keeps the
 * per-call work to the traversal itself, and puts the whole read surface of this service in one readable
 * block.
 */
const PORTAL_PAGE: Decoder<PortalListPage> = pageOf(decodePortalListItem);
const PORTAL_DETAIL_RESPONSE: Decoder<PortalDetail> = envelopeOf(decodePortalDetail);
const PORTAL_SETTINGS_RESPONSE: Decoder<PortalSettings> = envelopeOf(decodePortalSettings);
const PORTAL_ALIAS_RESPONSE: Decoder<PortalAlias> = envelopeOf(decodePortalAlias);
const PORTAL_ALIAS_LIST_RESPONSE: Decoder<readonly PortalAlias[]> = envelopeOf(
  arrayOf(decodePortalAlias),
);
const PORTAL_ADMINISTRATOR_LIST_RESPONSE: Decoder<readonly PortalAdministrator[]> = envelopeOf(
  arrayOf(decodePortalAdministrator),
);

/**
 * The portal (tenant) resource, its settings projection and its host-name aliases. A TYPED TRANSPORT
 * ONLY. Every member below is one HTTP call to one endpoint, returning the observable the framework
 * produced.
 */
@Injectable({ providedIn: 'root' })
export class PortalService {
  private readonly http = inject(HttpClient);

  /**
   * @param request The zero-based page index, the page size, the ordering and the paging contract's own
   * free-text filter.
   * @param filter The portal-name restriction, or omitted or `null` to list every portal.
   * @returns One page of portal rows, with the total and the coordinates the server reported.
   */
  list(
    request: PagedRequestParams,
    filter?: PortalListFilter | null,
  ): Observable<PortalListPage> {
    // ⚠ THE TRANSPORT IS CHOSEN BY WHETHER THE QUERY CARRIES A TERM A PERSON TYPED, AND THIS BRANCH MUST
    // NOT BE COLLAPSED TO ONE CALL. Two members here can carry one - the paging contract's own free-text
    // term and the site-name filter - and a query string is written into the reverse proxy's access log and
    // into the API's own request log. The reasoning is set out in full on the module listing.
    if (carriesPortalSearchText(request, filter)) {
      return this.http
        .post<unknown>(API_ENDPOINTS.portals.search(), portalSearchBody(request, filter), {
          context: presentedInContext(),
        })
        .pipe(map((body) => decodeResponse(PORTAL_PAGE, body)));
    }

    const params: HttpParams = portalListParams(request, filter);

    // Read as `unknown` and DECODED, not asserted.
    return this.http
      .get<unknown>(API_ENDPOINTS.portals.collection(), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_PAGE, body)));
  }

  /**
   * Reads one portal in full. The identifier is interpolated exactly as supplied and is never tested
   * first.
   *
   * @param portalId The portal to read.
   * @returns The portal.
   */
  getById(portalId: number): Observable<PortalDetail> {
    return this.http
      .get<unknown>(API_ENDPOINTS.portals.byId(portalId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(PORTAL_DETAIL_RESPONSE, body)));
  }

  /**
   * @param request The portal to create, including its first host name and the administrator account to
   * establish alongside it.
   * @returns The created portal, as the server stored it.
   */
  create(request: CreatePortalRequest): Observable<PortalDetail> {
    return this.http
      .post<unknown>(API_ENDPOINTS.portals.collection(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_DETAIL_RESPONSE, body)));
  }

  /**
   * Replaces one portal's editable state. The identifier travels in the path AND on the body, because the
   * request contract declares its own and the server requires the two to agree.
   *
   * @param portalId The portal to write.
   * @param request The complete editable state to store.
   * @returns The portal as stored.
   */
  update(portalId: number, request: UpdatePortalRequest): Observable<PortalDetail> {
    return this.http
      .put<unknown>(API_ENDPOINTS.portals.byId(portalId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_DETAIL_RESPONSE, body)));
  }

  /**
   * Removes one portal.
   *
   * @param portalId The portal to remove.
   * @returns Nothing. Answered `204`, so there is no body to read; a refusal because the installation
   * must retain at least one portal is reported as a conflict carrying the reason
   * `portal.last_remaining`, and a re-sent identical request cannot succeed while that state holds.
   */
  delete(portalId: number): Observable<void> {
    return this.http.delete<void>(API_ENDPOINTS.portals.byId(portalId), {
      context: presentedInContext(),
    });
  }

  /**
   * Reads one portal's configuration projection. there is NO portal-settings table, and this endpoint is
   * therefore not a key/value accessor.
   *
   * @param portalId The portal whose settings to read.
   * @returns The settings projection.
   */
  getSettings(portalId: number): Observable<PortalSettings> {
    return this.http
      .get<unknown>(API_ENDPOINTS.portals.settings(portalId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_SETTINGS_RESPONSE, body)));
  }

  /**
   * Replaces one portal's configuration projection.
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
      .put<unknown>(API_ENDPOINTS.portals.settings(portalId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_SETTINGS_RESPONSE, body)));
  }

  /**
   * @param portalId The portal whose eligible administrators to list.
   * @returns Every candidate, ordered by the name a selector displays.
   */
  listAdministrators(portalId: number): Observable<readonly PortalAdministrator[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.portals.administrators(portalId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_ADMINISTRATOR_LIST_RESPONSE, body)));
  }

  /**
   * @param portalId The portal whose aliases to list.
   * @returns Every alias of the portal, in the order the server returned them.
   */
  listAliases(portalId: number): Observable<readonly PortalAlias[]> {
    const params: HttpParams = emptyQueryParams();

    return this.http
      .get<unknown>(API_ENDPOINTS.portalAliases.forPortal.collection(portalId), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_ALIAS_LIST_RESPONSE, body)));
  }

  /**
   * Binds an additional host name to one portal.
   *
   * @param portalId The portal to bind the host name to.
   * @param request The host name to bind.
   * @returns The created alias, carrying the identifier the database assigned.
   */
  createAlias(
    portalId: number,
    request: CreatePortalAliasRequest,
  ): Observable<PortalAlias> {
    return this.http
      .post<unknown>(API_ENDPOINTS.portalAliases.forPortal.collection(portalId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_ALIAS_RESPONSE, body)));
  }

  /**
   * Reads one alias of one portal.
   *
   * @param portalId The portal that owns the alias.
   * @param portalAliasId The alias to read.
   * @returns The alias.
   */
  getAlias(portalId: number, portalAliasId: number): Observable<PortalAlias> {
    return this.http
      .get<unknown>(API_ENDPOINTS.portalAliases.forPortal.byId({ portalId, portalAliasId }), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PORTAL_ALIAS_RESPONSE, body)));
  }

  /**
   * Changes the host name one alias binds.
   *
   * @param portalId The portal that owns the alias.
   * @param portalAliasId The alias to change.
   * @param request The host name to store in place of the current one.
   * @returns The alias as stored.
   */
  updateAlias(
    portalId: number,
    portalAliasId: number,
    request: UpdatePortalAliasRequest,
  ): Observable<PortalAlias> {
    return this.http
      .put<unknown>(
        API_ENDPOINTS.portalAliases.forPortal.byId({ portalId, portalAliasId }),
        request,
        { context: presentedInContext() },
      )
      .pipe(map((body) => decodeResponse(PORTAL_ALIAS_RESPONSE, body)));
  }

  /**
   * Unbinds one alias from one portal.
   *
   * @param portalId The portal that owns the alias.
   * @param portalAliasId The alias to unbind.
   * @returns Nothing. Answered `204`, including when the alias was the portal's last: the legacy screen
   * hid the affordance in that case but never refused the write, and no refusal is invented.
   */
  deleteAlias(portalId: number, portalAliasId: number): Observable<void> {
    return this.http.delete<void>(
      API_ENDPOINTS.portalAliases.forPortal.byId({ portalId, portalAliasId }),
      { context: presentedInContext() },
    );
  }
}
