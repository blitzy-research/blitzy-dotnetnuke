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
 * The two response contracts this transport reads, composed once at module scope.
 *
 * The decoders are the point of this file, not an ornament on it. `HttpClient` accepts a type argument and
 * hands back a value asserted to have that shape WITHOUT INSPECTING IT, so
 * `post<ApiResponse<LoginResponse>>(...)` is a promise the compiler makes on the server's behalf and cannot
 * keep. On this boundary the unchecked value becomes a bearer credential, a renewal credential, an expiry
 * instant and an authority list.
 *
 * MIGRATION: these decoders existed and had no consumer. `auth.model.ts` declared `decodeLoginResponse` and
 * `decodeCurrentUser` — the strictest decoders in the workspace, written precisely for this boundary — while
 * every method here read `envelope.data` off a generic and trusted it. So the intended validation boundary
 * was bypassed for exactly the payloads that matter most: a drifted body reached the session store, the
 * shell header and the permission list unchecked, `roles` arriving as null would have faulted the first
 * `.includes` call somewhere unrelated, and an unparseable expiry would have compared false against every
 * clock and made a lapsed session look current. Each call below now requests `unknown` and decodes it, so a
 * body this client never declared becomes one located failure at the moment it arrives rather than a
 * plausible value travelling onward.
 */
const LOGIN_RESPONSE: Decoder<LoginResponse> = envelopeOf(decodeLoginResponse);
const CURRENT_USER_RESPONSE: Decoder<CurrentUser> = envelopeOf(decodeCurrentUser);

/**
 * Owns the authentication surface: sign in, refresh, sign out, and describe the caller.
 *
 * A transport, and nothing else. Four methods, four addresses, one request each, every response decoded
 * before it is handed back. It holds NO state: no session, no credential, no in-flight flag and no signal. It
 * reads nothing from storage and writes nothing to it, announces nothing to a person, makes no decision about
 * an outcome, and never subscribes to its own observables. Every authorisation decision belongs to the server.
 *
 * MIGRATION: the session lifecycle used to live here as well as in `core/state/auth.store.ts`, and two owners
 * of one session is where the resurrection races and duplicate announcements documented in that file came
 * from. Storing, clearing, generation comparison, renewal coalescing, the second identity read and the
 * operator notification are now the store's alone.
 *
 * MIGRATION: sign-out no longer reports success for a failed revocation. Absorbing the server's refusal made
 * a 400, a 429, a 503 or a dropped connection indistinguishable from a withdrawn credential while the renewal
 * credential stayed live for its full lifetime — the exact outcome signing out exists to prevent. The refusal
 * now propagates; deciding that local sign-out completes anyway is a policy owned by the lifecycle owner,
 * which can also record the outstanding revocation and say so to the operator.
 *
 * | Operation | Address              | Auth      | Success                     |
 * | --------- | -------------------- | --------- | --------------------------- |
 * | sign in   | `POST auth/login`    | anonymous | 200, token pair + identity  |
 * | renew     | `POST auth/refresh`  | anonymous | 200, rotated pair           |
 * | sign out  | `POST auth/logout`   | anonymous | 204, unconditionally        |
 * | describe  | `GET auth/me`        | bearer    | 200, caller's description   |
 *
 * The surface is closed at those four: there is no verification, registration, external-provider,
 * challenge-image or credential-recovery endpoint to call, and changing a credential is an operation on the
 * user resource. Every path is taken from `core/config/api-endpoints.ts`, which already composes the
 * configured base in, so a path is never re-prefixed at a call site — doing so yields a doubled version
 * segment, a run-time 404 that no compiler and no client-stubbing test would catch.
 *
 * Successful bodies arrive inside the API's shared envelope, so each call states the envelope explicitly and
 * projects its data member out. Typing a call as the bare payload compiles and then fails in the quietest
 * possible way: every member reads as undefined and a stored session is a shape-correct blank.
 *
 * Sign-out takes a body of the same shape renewal uses, because the API declares one as required; posting
 * nothing would be refused as malformed before the operation ran. It revokes the renewal credential only and
 * keeps no deny-list, so an access token already issued stays valid until its stamped expiry.
 *
 * No key, salt, issuer, audience or signing material appears in this file, and nothing here is written to a
 * log sink. Retry after renewal, expiry arithmetic, token decoding and credential-policy enforcement all
 * belong elsewhere: this service inspects no status code, reads no clock and validates nothing.
 */

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  /**
   * ⚠ EVERY REQUEST BELOW IS MARKED AS PRESENTED BY ITS CALLER, AND THIS FILE WAS THE LAST HOLD-OUT.
   *
   * `PRESENTED_IN_CONTEXT` is the workspace's single arbitration rule for who reports a failed
   * request: a marked request is reported by whoever issued it, and an unmarked one is reported by
   * the global announcer in `core/interceptors/error.interceptor.ts`. Every other service in
   * `core/services/` marked its requests; these four did not, so a refused sign-in was reported
   * TWICE and a rate-limited one THREE TIMES - the shared banner rendering the server's document,
   * the form-level sentence on the sign-in screen, and a queued notification worded differently
   * again from either. The legacy screen it replaces had exactly one message surface, so three is
   * not a behaviour being preserved.
   *
   * The marker only moves ownership, so each operation needs an owner that cannot be silent, and
   * each has one:
   *
   *   - SIGN-IN is reported by the sign-in screen, whose banner takes the problem document and
   *     whose form-level sentence covers every case the banner cannot: the two are complements by
   *     construction, because the screen's sentence resolves to null ONLY when a document is held.
   *   - IDENTITY is reached in production from inside the sign-in and renewal chains alone, so its
   *     refusal is the outer operation's refusal and is reported by that operation's owner.
   *   - RENEWAL is reported by `core/state/auth.store.ts`: a terminal refusal of authority ends the
   *     session and sends the operator to the sign-in screen, which is the report; any other
   *     refusal is announced there in words, because a renewal has no screen of its own to bind a
   *     banner to.
   *   - SIGN-OUT is reported by the same store, which announces the outstanding revocation.
   *
   * The default is unmarked-means-announced, so the failure mode of forgetting the marker is a
   * duplicate rather than a silence. That is why the marker is applied here, at each call, rather
   * than by something that wraps them all: a future operation added to this class must make the
   * same decision explicitly.
   */

  /**
   * Exchanges credentials for a token pair and the identity it was issued for.
   *
   * `POST auth/login`, answering `200` with the pair, or refusing: `401` for bad credentials, `403` for a
   * locked-out or not-approved account, `400` for a malformed submission and `429` when the credential
   * window is spent. Every refusal is re-thrown exactly as it arrived so the caller can render the server's
   * problem document, which distinguishes the four.
   *
   * Nothing is stored here, and nothing else is fetched here. The response is decoded and returned.
   * Committing it to the session, reading the caller's expanded authority through {@link AuthService.me},
   * and deciding what a failed attempt does to a session already held are all the lifecycle owner's,
   * `core/state/auth.store.ts`.
   *
   * @param request The credentials, and the verification code when one was supplied. Transmitted exactly as
   *   given: an empty code travels as an empty string and an omitted one stays omitted, because the legacy
   *   ladder branched on that very distinction.
   * @param selector The tenant to sign in to, when the arrival host resolves none.
   * @returns The decoded token pair and the authority-minimised identity it was issued for.
   */
  login(
    request: LoginRequest,
    selector?: LoginPortalSelector | null,
  ): Observable<LoginResponse> {
    return this.http
      .post<unknown>(AUTH_ENDPOINTS.login, request, {
        // The tenant selector travels as a QUERY parameter, not in the body: the endpoint resolves the
        // tenant from the arrival host and admits `?portalId=` only as the fallback for a host with no alias
        // row. `loginParams` transmits -1 and 0 as data, because `Portals.PortalID` is `IDENTITY(-1,1)` and
        // both name real tenants.
        params: loginParams(selector),

        // Reported by the sign-in screen. See the note at the head of this class.
        context: presentedInContext(),
      })
      .pipe(map((body) => LOGIN_RESPONSE(body, RESPONSE_ROOT)));
  }

  /**
   * Exchanges a renewal credential for a rotated pair.
   *
   * `POST auth/refresh`, answering `200` with a pair of the same shape sign-in returns — the API declares no
   * separate renewal response — or refusing with `401`, `400` or `429`.
   *
   * The credential is an argument, not something read from storage. This method neither knows where the
   * renewal credential is kept nor whether one is held, and it does not coalesce concurrent callers:
   * presenting one rotating credential twice is a replay the server answers by revoking the whole family, so
   * exactly one owner must serialise renewals, and that owner is `core/state/auth.store.ts`. A second
   * in-flight slot here would be a second source of truth for the same fact — the arrangement in which a
   * renewal begun before a sign-out could store its pair afterwards and resurrect the session the operator
   * had just ended.
   *
   * @param request The renewal credential to present, in the body shape the API declares.
   * @returns The decoded rotated pair.
   */
  refresh(request: RefreshTokenRequest): Observable<LoginResponse> {
    return this.http
      // Reported by the lifecycle owner. See the note at the head of this class.
      .post<unknown>(AUTH_ENDPOINTS.refresh, request, { context: presentedInContext() })
      .pipe(map((body) => LOGIN_RESPONSE(body, RESPONSE_ROOT)));
  }

  /**
   * Asks the server to withdraw a renewal credential.
   *
   * `POST auth/logout`, answering `204` for a credential it found and for one it did not — a different
   * answer would turn the operation into a probe for which sessions are live, and an anonymous caller must
   * not be handed that. It can still REFUSE: `400` for a malformed body, `429` when the revocation window is
   * spent and `503` when the session store cannot be reached.
   *
   * A refusal PROPAGATES rather than being absorbed and reported as completion, which would make a live
   * renewal credential indistinguishable from a withdrawn one. Whether local sign-out completes regardless
   * is a policy decision, and the lifecycle owner makes it - together with recording the outstanding
   * revocation and telling the operator.
   *
   * MIGRATION: sign-out reaches the RENEWAL credential only. An access token already issued cannot be
   * recalled, so it stays valid until it lapses, which is why that lifetime is short; the legacy
   * `FormsAuthentication.SignOut` cleared a cookie and took effect at once and has no stateless counterpart.
   * The body is required and is the same shape renewal uses — there is no distinct sign-out contract — so
   * posting nothing would be refused before the operation ran.
   *
   * @param request The renewal credential to withdraw.
   * @returns Completion. `204` carries no body, so there is nothing to decode.
   */
  logout(request: RefreshTokenRequest): Observable<void> {
    // Reported by the lifecycle owner, which announces the outstanding revocation. See the note at
    // the head of this class.
    return this.http.post<void>(AUTH_ENDPOINTS.logout, request, {
      context: presentedInContext(),
    });
  }

  /**
   * Asks the server to describe the caller.
   *
   * `GET auth/me`, the only authorised operation of the four, and the way a client learns its own roles and
   * granted permission codes — which is why no client needs the administrative permission-query operations
   * merely to describe itself. Answers `200` with the description, `401` when identity was not proved, `404`
   * when the token names an account that no longer resolves, and `429` on its own budget.
   *
   * The answer is not enforcement. Every authorisation decision is made again on the server for every
   * request; the permission codes exist so that a screen can avoid offering an action that would be refused,
   * never so that a client can decide an access question for itself.
   *
   * The optional credential is the one place a bearer header is written by hand, and it exists for a single
   * caller: the bootstrap read that follows a sign-in or a renewal. At that moment the rotated token exists
   * only as a local value — it is deliberately not stored until the identity has been fetched, so that a
   * failed bootstrap cannot leave a half-populated session behind — and an interceptor reading storage would
   * attach the PREVIOUS token, or none, and on renewal would treat the resulting refusal as cause for
   * another renewal. Passing the freshly issued token explicitly is what breaks that recursion, and the
   * interceptor skips a request that already carries the header.
   *
   * Omit the argument once a session is held: the bearer token is then attached by
   * `core/interceptors/auth.interceptor.ts` and the correlation identifier by
   * `core/interceptors/correlation-id.interceptor.ts`, in that fixed order, and substituting either by hand
   * would defeat the interceptor's ability to recover from a refused request.
   *
   * @param accessToken The freshly issued token to present, for the bootstrap read alone. Omitted or null
   *   for every other caller, which leaves the credential to the interceptor.
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
      .get<unknown>(AUTH_ENDPOINTS.me, options)
      .pipe(map((body) => CURRENT_USER_RESPONSE(body, RESPONSE_ROOT)));
  }
}
