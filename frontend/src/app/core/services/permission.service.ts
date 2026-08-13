// MIGRATION: permission EVALUATION is deliberately absent here. This service asks WHICH permissions exist;
// it never decides whether a caller holds one.

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { decodePermission } from '../models/permission.model';
import { arrayOf, decodeResponse, decodeString, responseOf } from '../utils/decode.util';
import { presentedInContext } from './notification.service';
import { map } from 'rxjs';

import type { Decoder } from '../utils/decode.util';
import type { Permission } from '../models/permission.model';
import type { ApiResponse } from '../models/paged-result.model';
import { permissionListParams, type PermissionListFilter } from '../utils/http-params.util';

/**
 * One decoder per response shape this transport reads, composed once at module scope. Both are built with
 * `responseOf` rather than `envelopeOf` because the published signatures of the methods below RETURN THE
 * ENVELOPE, and validating must not change the shape they return.
 */
const PERMISSION_KEY_LIST_RESPONSE: Decoder<ApiResponse<readonly string[]>> = responseOf(
  arrayOf(decodeString),
);
const PERMISSION_RESPONSE: Decoder<ApiResponse<Permission>> = responseOf(decodePermission);

/**
 * Reads the permission catalogue: which permission keys this installation defines. Transport only: each
 * method is one endpoint, one typed request, one cold observable returned directly from the HTTP client.
 */
@Injectable({ providedIn: 'root' })
export class PermissionService {
  /**
   * The HTTP client, resolved from the injector rather than declared as a constructor parameter so that
   * the class needs no constructor at all. The client itself is provided once, application-wide, by
   * `provideHttpClient` in `app.config.ts`.
   */
  private readonly http = inject(HttpClient);

  /**
   * Reads the catalogue, optionally narrowed by the filters the server accepts. The payload is a bare
   * array of key strings, not a list of records - that is the server's contract, and it is what a
   * client-side permission check tests against.
   *
   * @param filter The restrictions to apply, or omitted or `null` to read the whole catalogue.
   * @returns The success envelope carrying every matching key. 200 is the only success status.
   */
  list(filter?: PermissionListFilter | null): Observable<ApiResponse<readonly string[]>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.permissions.collection(), {
        params: permissionListParams(filter),
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PERMISSION_KEY_LIST_RESPONSE, body)));
  }

  /**
   * Reads one catalogue definition by its identifier. Answers with the whole record rather than a bare
   * key, because the same key is declared repeatedly across scopes and a key alone would not say which
   * scope code or module definition it belongs to.
   *
   * @param permissionId Identifier of the definition wanted, interpolated exactly as supplied.
   * @returns The success envelope carrying the definition. 200 on success; 400 if the identifier cannot
   * be bound, 401 without a valid credential, 403 without administrator standing, and 404 when no
   * definition bears that identifier.
   */
  getById(permissionId: number): Observable<ApiResponse<Permission>> {
    return this.http
      .get<unknown>(API_ENDPOINTS.permissions.byId(permissionId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(PERMISSION_RESPONSE, body)));
  }
}
