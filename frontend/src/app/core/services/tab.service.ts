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
 * Transport for the page resource - the abstraction the database, the legacy source and the wire contract
 * all still call a "tab", and which an administrator sees as a Page. A LOOKUP, NOT A FEATURE. This
 * application owns no page-management route and no page feature area: the module screens need to know
 * which page a placement sits on, and this service is how they ask.
 */
@Injectable({ providedIn: 'root' })
export class TabService {
  private readonly http = inject(HttpClient);

  /**
   * Lists every page belonging to one portal, as a flat collection. `GET
   * /api/v1/portals/{portalId}/tabs`, answering `200` with the rows.
   *
   * @param portalId The tenant whose pages are wanted.
   * @returns The portal's pages, in the order the server returned them.
   */
  getByPortal(portalId: number): Observable<readonly TabListItem[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.tabs.forPortal(portalId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(TAB_LIST_RESPONSE, body)));
  }

  /**
   * Retrieves one page in full. `GET /api/v1/tabs/{tabId}`, answering `200` with the detail shape.
   *
   * @param tabId The page wanted.
   * @returns The page.
   */
  getById(tabId: number): Observable<TabDetail> {
    return this.http
      .get<unknown>(API_ENDPOINTS.tabs.byId(tabId), { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(TAB_DETAIL_RESPONSE, body)));
  }

  /**
   * @param tabId The page to replace.
   * @param request The complete editable state.
   * @returns The page as it now stands, so a caller can render the server's own result rather than the
   * state it hoped it had written.
   */
  update(tabId: number, request: UpdateTabRequest): Observable<TabDetail> {
    return this.http
      .put<unknown>(API_ENDPOINTS.tabs.byId(tabId), request, { context: presentedInContext() })
      .pipe(map((body) => decodeResponse(TAB_DETAIL_RESPONSE, body)));
  }
}
