import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { AUTH_ENDPOINTS } from '../config/api-endpoints';
import { presentedInContext } from './notification.service';
import { type LoginPortalSelector, loginParams } from '../utils/http-params.util';
import {
  CurrentUser,
  LoginRequest,
  LoginResponse,
  RefreshTokenRequest,
  decodeCurrentUser,
  decodeLoginResponse,
} from '../models/auth.model';
import { RESPONSE_ROOT, envelopeOf } from '../utils/decode.util';

import type { Decoder } from '../utils/decode.util';

const AUTHORIZATION_HEADER = 'Authorization';

/**
 * The two response contracts this transport reads, composed once at module scope. The decoders are the
 * point of this file, not an ornament on it.
 */
const LOGIN_RESPONSE: Decoder<LoginResponse> = envelopeOf(decodeLoginResponse);
const CURRENT_USER_RESPONSE: Decoder<CurrentUser> = envelopeOf(decodeCurrentUser);

/**
 * Owns the authentication surface: sign in, refresh, sign out, and describe the caller. A transport, and
 * nothing else.
 */

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  /**
   * ⚠ EVERY REQUEST BELOW IS MARKED AS PRESENTED BY ITS CALLER, AND THIS FILE WAS THE LAST HOLD-OUT.
   * `PRESENTED_IN_CONTEXT` is the workspace's single arbitration rule for who reports a failed request: a
   * marked request is reported by whoever issued it, and an unmarked one is reported by the global
   * announcer in `core/interceptors/error.interceptor.ts`.
   */

  /**
   * Exchanges credentials for a token pair and the identity it was issued for. `POST auth/login`,
   * answering `200` with the pair, or refusing: `401` for bad credentials, `403` for a locked-out or
   * not-approved account, `400` for a malformed submission and `429` when the credential window is spent.
   *
   * @param request The credentials, and the verification code when one was supplied.
   * @param selector The tenant to sign in to, when the arrival host resolves none.
   * @returns The decoded token pair and the authority-minimised identity it was issued for.
   */
  login(
    request: LoginRequest,
    selector?: LoginPortalSelector | null,
  ): Observable<LoginResponse> {
    return this.http
      .post<unknown>(AUTH_ENDPOINTS.login(), request, {
        // The tenant selector travels as a QUERY parameter, not in the body: the endpoint resolves the
        // tenant from the arrival host and admits `?portalId=` only as the fallback for a host with no
        // alias row.
        params: loginParams(selector),

        // Reported by the sign-in screen. See the note at the head of this class.
        context: presentedInContext(),
      })
      .pipe(map((body) => LOGIN_RESPONSE(body, RESPONSE_ROOT)));
  }

  /**
   * Exchanges a renewal credential for a rotated pair. `POST auth/refresh`, answering `200` with a pair
   * of the same shape sign-in returns — the API declares no separate renewal response — or refusing with
   * `401`, `400` or `429`.
   *
   * @param request The renewal credential to present, in the body shape the API declares.
   * @returns The decoded rotated pair.
   */
  refresh(request: RefreshTokenRequest): Observable<LoginResponse> {
    return this.http
      // Reported by the lifecycle owner. See the note at the head of this class.
      .post<unknown>(AUTH_ENDPOINTS.refresh(), request, { context: presentedInContext() })
      .pipe(map((body) => LOGIN_RESPONSE(body, RESPONSE_ROOT)));
  }

  /**
   * Asks the server to withdraw a renewal credential. `POST auth/logout`, answering `204` for a
   * credential it found and for one it did not — a different answer would turn the operation into a probe
   * for which sessions are live, and an anonymous caller must not be handed that.
   *
   * @param request The renewal credential to withdraw.
   * @returns Completion. `204` carries no body, so there is nothing to decode.
   */
  logout(request: RefreshTokenRequest): Observable<void> {
    // Reported by the lifecycle owner, which announces the outstanding revocation. See the note at
    // the head of this class.
    return this.http.post<void>(AUTH_ENDPOINTS.logout(), request, {
      context: presentedInContext(),
    });
  }

  /**
   * Asks the server to describe the caller. `GET auth/me`, the only authorised operation of the four, and
   * the way a client learns its own roles and granted permission codes — which is why no client needs the
   * administrative permission-query operations merely to describe itself.
   *
   * @param accessToken The freshly issued token to present, for the bootstrap read alone.
   * @returns The caller's decoded identity, roles and granted permission codes.
   */
  me(accessToken?: string | null): Observable<CurrentUser> {
    // Reported by whichever operation this read is part of. See the note at the head of this class.
    const options =
      accessToken === undefined || accessToken === null
        ? { context: presentedInContext() }
        : {
            context: presentedInContext(),
            headers: new HttpHeaders({ [AUTHORIZATION_HEADER]: `Bearer ${accessToken}` }),
          };

    return this.http
      .get<unknown>(AUTH_ENDPOINTS.me(), options)
      .pipe(map((body) => CURRENT_USER_RESPONSE(body, RESPONSE_ROOT)));
  }
}
