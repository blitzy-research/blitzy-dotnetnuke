import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, of, switchMap, type Observable } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { decodeTabDetail, decodeTabListItem } from '../models/tab.model';
import { decodeResponse, envelopeOf, pageOf } from '../utils/decode.util';
import { pagedRequestParams } from '../utils/http-params.util';
import { presentedInContext } from './notification.service';

import type { Decoder } from '../utils/decode.util';
import type { PagedResult } from '../models/paged-result.model';
import type { TabDetail, TabListItem, UpdateTabRequest } from '../models/tab.model';
import type { PagedRequestParams } from '../utils/http-params.util';

/** One decoder per response shape this transport reads, composed once at module scope. */
const TAB_PAGE: Decoder<PagedResult<TabListItem>> = pageOf(decodeTabListItem);
const TAB_DETAIL_RESPONSE: Decoder<TabDetail> = envelopeOf(decodeTabDetail);

/**
 * The page size used when a caller needs the WHOLE collection. It is the API's own ceiling, so the number of
 * round trips is the smallest the server permits.
 */
const WHOLE_COLLECTION_PAGE_SIZE = 100;

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
   * One page of the pages belonging to one portal. `GET /api/v1/portals/{portalId}/tabs`, answering `200`
   * with the page and the total across every page.
   *
   * The endpoint refuses `sortBy` and `query` with `400`, because a page tree has one meaningful order and
   * no filterable column of its own - so this method sends neither, whatever a caller puts on the request.
   *
   * @param portalId The tenant whose pages are wanted.
   * @param request The page to return and its size.
   * @returns The page, in the paged wire envelope.
   */
  getPageByPortal(
    portalId: number,
    request: PagedRequestParams,
  ): Observable<PagedResult<TabListItem>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.tabs.forPortal(portalId), {
        params: pagedRequestParams({ pageIndex: request.pageIndex, pageSize: request.pageSize }),
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(TAB_PAGE, body)));
  }

  /**
   * Every page belonging to one portal, as a flat collection, read a hundred rows at a time.
   *
   * MIGRATION: THE RESPONSE THIS READS IS NOW BOUNDED AND THIS METHOD'S ANSWER IS NOT, WHICH IS DELIBERATE.
   * The endpoint used to return a tenant's entire page tree in one response - three thousand pages measured
   * at 764 KiB - and the screens that consume this need the whole set, because they render it as a chooser
   * rather than as a browsable list. So the collection is assembled here from bounded pages: no single
   * response is unbounded, and no consumer had to change. A tenant large enough for the round trips to be
   * noticeable is a tenant whose page chooser wants a search rather than a longer list, which is a change to
   * those screens rather than to this transport.
   *
   * @param portalId The tenant whose pages are wanted.
   * @returns The portal's pages, in the navigation order the server returned them, across page boundaries.
   */
  getByPortal(portalId: number): Observable<readonly TabListItem[]> {
    const readFrom = (
      pageIndex: number,
      collected: readonly TabListItem[],
    ): Observable<readonly TabListItem[]> =>
      this.getPageByPortal(portalId, {
        pageIndex,
        pageSize: WHOLE_COLLECTION_PAGE_SIZE,
      }).pipe(
        switchMap((page) => {
          const accumulated: readonly TabListItem[] = [...collected, ...page.items];

          // Bounded by the total the server reported rather than by a page count, so a tenant that gains a
          // page between two requests still terminates.
          return page.items.length === 0 || accumulated.length >= page.meta.totalCount
            ? of(accumulated)
            : readFrom(pageIndex + 1, accumulated);
        }),
      );

    return readFrom(0, []);
  }

  /**
   * Retrieves one page in full. `GET /api/v1/tabs/{tabId}`, answering `200` with the detail shape.
   *
   * ⚠ NO SCREEN CALLS THIS, AND THAT IS DELIBERATE - DO NOT DELETE IT AS DEAD CODE, AND DO NOT WIRE A ROUTE
   * TO IT. The by-identifier page read and {@link update} are an API-ONLY capability in this release. The plan
   * this migration is delivered against enumerates this application's routes exhaustively and none of them
   * addresses a page; it lists the in-scope feature areas as portal, module, user, role and authentication
   * only; and it names this transport explicitly as a lookup that exists even though there is no page feature
   * folder. Adding a page-management screen would contradict all three, so it is a scope decision for a later
   * release rather than a gap to be patched here.
   *
   * The method stays because this transport is the typed client for the page resource as the API actually
   * publishes it, and because a later page-management area should reach the endpoint through this one place
   * rather than opening a second path to it. It is covered by its own specification for the same reason.
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
   * Replaces one page. `PUT /api/v1/tabs/{tabId}`.
   *
   * ⚠ API-ONLY in this release, on exactly the terms set out on {@link getById} - no screen calls it, that is
   * a scope decision rather than an omission, and it is neither dead code nor an invitation to add a route.
   *
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
