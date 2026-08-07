import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { AUTH_ENDPOINTS } from '../config/api-endpoints';
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
 * ⚠ THE DECODERS ARE THE POINT OF THIS FILE, NOT AN ORNAMENT ON IT. `HttpClient` accepts a type
 * argument and hands back a value asserted to have that shape WITHOUT INSPECTING IT, so
 * `post<ApiResponse<LoginResponse>>(...)` is a promise the compiler makes on the server's behalf
 * and cannot keep. On this boundary the unchecked value becomes a bearer credential, a renewal
 * credential, an expiry instant and an authority list.
 *
 * MIGRATION: THESE DECODERS EXISTED AND HAD NO CONSUMER. `auth.model.ts` declared
 *   `decodeLoginResponse` and `decodeCurrentUser` — the strictest decoders in the workspace,
 *   written precisely for this boundary — while every method here read `envelope.data` off a
 *   generic and trusted it. So the intended validation boundary was bypassed for exactly the
 *   payloads that matter most: a drifted body reached the session store, the shell header and
 *   the permission list unchecked, `roles` arriving as null would have faulted the first
 *   `.includes` call somewhere unrelated, and an unparseable expiry would have compared false
 *   against every clock and made a lapsed session look current. Each call below now requests
 *   `unknown` and decodes it, so a body this client never declared becomes one located failure
 *   at the moment it arrives rather than a plausible value travelling onward.
 */
const LOGIN_RESPONSE: Decoder<LoginResponse> = envelopeOf(decodeLoginResponse);
const CURRENT_USER_RESPONSE: Decoder<CurrentUser> = envelopeOf(decodeCurrentUser);

/**
 * Owns the authentication surface: sign in, refresh, sign out, and describe the caller.
 *
 * ⚠ A TRANSPORT, AND NOTHING ELSE. Four methods, four addresses, one request each, every
 * response decoded before it is handed back. It holds NO state of any kind: no session, no
 * credential, no in-flight slot, no flag and no signal. It reads nothing from storage and
 * writes nothing to it, it announces nothing to a person, it makes no decision about an
 * outcome, and it never subscribes to its own observables. Angular services are restricted
 * to API communication by the migration discipline, and every authorisation decision belongs
 * to the server.
 *
 * MIGRATION: THIS SERVICE USED TO OWN THE SESSION LIFECYCLE, AND THAT WAS THE DEFECT. It
 *   cleared and stored the session, captured and compared the session generation, coalesced
 *   concurrent renewals behind a private in-flight slot, performed a second `auth/me` request
 *   inside sign-in and renewal, published a revocation-outstanding signal, re-exposed three
 *   projections of the token custodian, and emitted a notification to the operator. Every one
 *   of those is a lifecycle concern, `core/state/auth.store.ts` already owned the lifecycle,
 *   and two owners of one session is the shape from which the hardest defects on this boundary
 *   come — the resurrection races and duplicate announcements documented in that file were all
 *   consequences of the split. The whole of it now lives in the store; what is left here is
 *   what a transport is.
 *
 * MIGRATION: SIGN-OUT NO LONGER REPORTS SUCCESS FOR A FAILED REVOCATION. It used to absorb the
 *   server's refusal with `catchError(() => of(undefined))` and complete, so a `400`, a `429`,
 *   a `503` or a dropped connection was indistinguishable from a withdrawn credential — while
 *   the renewal credential stayed live on the server for its full lifetime, which is precisely
 *   the outcome signing out exists to prevent. The refusal now propagates. Deciding that local
 *   sign-out completes anyway is a POLICY, and it belongs to the lifecycle owner that can also
 *   record the outstanding revocation and say so to the operator; a transport cannot make that
 *   decision without lying about what the server did.
 *
 * ---------------------------------------------------------------------------
 * THE ENDPOINT SURFACE IS CLOSED AT FOUR, and every path is taken from
 * `core/config/api-endpoints.ts` rather than written here. That module composes each
 * template from the configured base, so a path is never re-prefixed at a call site:
 * doing so would yield a doubled version segment, which is a run-time 404 that no
 * compiler and no test that stubs the client would catch.
 *
 * | Operation | Address              | Auth      | Success                     |
 * | --------- | -------------------- | --------- | --------------------------- |
 * | sign in   | `POST auth/login`    | anonymous | 200, token pair + identity  |
 * | renew     | `POST auth/refresh`  | anonymous | 200, rotated pair           |
 * | sign out  | `POST auth/logout`   | anonymous | 204, unconditionally        |
 * | describe  | `GET auth/me`        | bearer    | 200, caller's description   |
 *
 * Nothing else under `auth/` exists to be called. There is deliberately no
 * verification endpoint (the code is a member of the sign-in request — see the
 * verification note below), no registration endpoint (creating an account is an
 * administrative operation on the user resource), no external-provider or
 * single-sign-on endpoint (there is one credential path), no challenge-image
 * endpoint, and no credential-recovery endpoint of any kind. The health probe is
 * not addressed from here either: it is published at the host root, outside the
 * versioned prefix, so that a container health check can reach it anonymously and
 * without spending a rate-limit budget.
 * ---------------------------------------------------------------------------
 *
 * ## Recorded divergences from the legacy sign-in
 *
 * Every item below is a deliberate behavioural difference between this client and
 * the Web Forms screen it replaces. The migration discipline requires each to be
 * annotated where it applies rather than absorbed silently, and each carries the
 * legacy citation it was measured from.
 *
 * MIGRATION: **LEGACY DEFECT — THE LOCKOUT BYPASS IS CORRECTED SERVER-SIDE.** At
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L187` the legacy
 * screen decided the outcome with
 * `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`. Because the
 * not-approved status is consumed by the preceding branch at `:L168`, that `Else`
 * treated EVERY remaining non-zero status as a successful sign-in. The arithmetic is
 * unambiguous against the seven explicitly valued members at
 * `Library/Components/Users/Membership/UserLoginStatus.vb:L24-L30`, so a locked-out
 * account (3) and both insecure-password statuses (5 and 6) all authenticated. The
 * API maps all seven deliberately instead: failure (0) is refused as unauthorised,
 * success (1) and super-user (2) succeed, **locked-out (3) is now refused** rather
 * than admitted, not-approved (4) is refused and carries a verification code, and
 * the two insecure-password statuses (5 and 6) succeed while carrying an
 * informational advisory. That mapping is the server's; this client neither
 * reproduces it, branches on it, nor decodes a status ordinal — the ordinals never
 * travel on the wire.
 *
 * MIGRATION: **the verification code is a MEMBER of the sign-in request, and there
 * is no separate verification endpoint.** The legacy ladder was progressive and
 * stateful: `:L109-L116` revealed the code rows from the query string but only when
 * the tenant's registration mode was verified registration; on a not-approved status
 * (`:L168`, `:L170`) the FIRST rejection merely revealed the rows and asked for a
 * code (`:L171`, `:L175`); a later non-empty but wrong code answered differently
 * (`:L177-L178`) from one still empty (`:L180`); and outside verified registration
 * the account was simply refused (`:L184`). Exactly three outcome codes existed —
 * enter-code, invalid-code and not-authorised — and no fourth is invented. This
 * service posts the request and lets the answer propagate; the stateful "a code is
 * now required" flag belongs to the feature that owns the screen, not here.
 *
 * MIGRATION: **the challenge image is dropped, and request rate limiting is the
 * named compensating control.** The legacy guard at `:L162` gated the whole sign-in
 * on a challenge control being valid, with its rows at `:L137-L143`; both went with
 * the excluded legacy control library. The API instead limits the credential
 * endpoints by calling client address and answers 429 when the window is spent.
 * Measured on the API's own controller, the limiter is applied PER ACTION rather
 * than once on the class, and the describe-caller operation draws from a separate
 * partition so that polling it cannot spend the budget sign-in needs. This service
 * does not handle 429: it performs no backoff, keeps no attempt counter, reads no
 * retry hint and never retries a refused credential. A 429 arrives as an ordinary
 * problem document and is rendered by the error interceptor.
 *
 * MIGRATION: **the authentication-type literal disappears with the single bearer
 * path.** The legacy call at `:L164` passed a four-character provider discriminator
 * positionally into an eight-argument sign-in, and `:L191` passed it a second time
 * when raising the authenticated event. A discriminator that can hold exactly one
 * value is not a contract member, so no request shape here carries one.
 *
 * MIGRATION: **the by-reference status argument is gone.** That same `:L164` call
 * reported its outcome through a `ByRef` status argument alongside its return value.
 * A refusal is now an RFC 7807 problem document carrying a code, and a success is a
 * response body; no member of this service has an out-parameter, and none returns a
 * tuple of value-plus-status.
 *
 * MIGRATION: **credential retrieval is abolished rather than ported, and reversible
 * storage is eliminated.** The legacy deployment registered its membership provider
 * with retrieval enabled and a reversible password format
 * (`Website/release.config:L239` and `:L245`), backed by a symmetric key committed
 * to source control in the clear at `:L89-L93` — and committed identically in the
 * development configuration, so every stored credential was recoverable by anyone
 * with repository access. The flow that exploited it mailed the decrypted value
 * (`Website/admin/Security/SendPassword.ascx.vb:L200`, reached through the
 * question-and-answer gate at `:L167`). Credentials are now held as a one-way
 * adaptive hash, re-hashed on the first successful sign-in, with an administrative
 * reset as the only remedy. Consequently there is no recovery, reset-by-mail or
 * change-credential operation on this service; changing a credential is an operation
 * on the user resource. No key, salt, issuer, audience or signing material appears
 * anywhere in this file, and nothing here is ever written to a log sink.
 *
 * MIGRATION: **the credential policy is preserved verbatim and deliberately NOT
 * tightened.** `Website/release.config:L240-L244` shipped reset enabled, no
 * question-and-answer requirement, a minimum length of seven, no requirement for
 * non-alphanumeric characters, and no unique-address requirement. Hardening a policy
 * mid-migration would refuse existing accounts that the legacy application accepted,
 * so the rules are unchanged and are enforced on the server. This service validates
 * nothing: it does not check a length, a complexity rule, or even that a submitted
 * value is non-empty.
 *
 * MIGRATION: **empty text is transmitted, never elided.** The legacy absent-text
 * sentinel was the EMPTY STRING rather than a null reference
 * (`Library/Components/Shared/Null.vb:L71-L75` returns `""`), and the absent-integer
 * sentinel was minus one (`:L41-L45`). The sign-in path seeded both its result
 * variables from those sentinels at `:L165-L166`, and `:L177` then distinguished an
 * empty code from a non-empty one to choose between two different answers — so the
 * distinction is behaviourally load-bearing. Nothing here normalises one form into
 * the other, strips a member because it is falsy, or defaults an identifier: request
 * bodies are passed through exactly as the caller supplied them. Identifiers are
 * never tested for truthiness or compared against zero, because a tenant key of zero
 * and of minus one are both legitimate — the tenant table is seeded from minus one,
 * which is simultaneously the legacy absent-integer sentinel.
 *
 * MIGRATION: **retrying a request after a renewal is not orchestrated here.** That
 * belongs to `core/interceptors/auth.interceptor.ts`, which observes the refused
 * response and decides whether recovery is possible. This service exposes the calls
 * and coalesces concurrent renewals; it does not inspect a status code, does not
 * decide that a request should be replayed, and does not queue requests.
 *
 * MIGRATION: **credential lifetimes are a deployment concern and are not computed
 * here.** The access token's lifetime matches the legacy forms-authentication cookie
 * exactly — `Website/release.config:L146-L147` declared forms authentication with a
 * sixty-minute timeout — and renewal rotates a longer-lived credential. This service
 * performs no expiry arithmetic, reads no clock, and neither parses nor converts the
 * instant the server publishes; that value arrives as an absolute ISO 8601 string
 * and is stored as one. No token is decoded here either: nothing base64-decodes a
 * segment, and no token-inspection library is a dependency.
 *
 * MIGRATION: **the Web Forms event model is not reproduced.** The legacy control
 * base declared seven user-lifecycle events (`Library/Components/Users/UserUserControlBase.vb:L59-L65`,
 * raised at `:L80`) which the sign-in screen fed through its authenticated-event
 * arguments. Those become component outputs and reactive effects in the presentation
 * layer; no client-side event bus is introduced, and no server-side one exists.
 *
 * MIGRATION: **localisation is not ported.** The legacy resource files are read only
 * as the authority for English wording. The authoritative wording for the three
 * sign-in outcome codes is easy to look for in the wrong place: it lives beside the
 * legacy ADMINISTRATIVE sign-in control at
 * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx` — enter-code at
 * L163, invalid-code at L166 and not-authorised at L223 — and NOT beside the
 * authentication-services control this service's behaviour was measured from, whose
 * own resource file contains none of the three. Resource and message text is treated
 * as untrusted: a minority of legacy resource values carry markup, a handful
 * carrying script elements, so no string that crosses this boundary is ever routed
 * into a trusted-HTML sink.
 *
 * MIGRATION: **the legacy caching layer is not reproduced on the client.** The
 * legacy data layer cached through a shared static helper
 * (`Library/Components/Providers/Caching/DataCache.vb`) from many call sites across
 * the in-scope domains, with coarse tenant-wide and host-wide invalidation. Caching
 * is now a server concern behind an explicit abstraction; this service issues a
 * request every time it is called and memoises nothing but an in-flight renewal.
 *
 * MIGRATION: **implicit conversions are made explicit.** The legacy administrative
 * code-behinds compiled with strict type checking DISABLED
 * (`Website/release.config:L125`), so they could legally rely on late binding and
 * silent narrowing. Strict TypeScript is what forces each such coercion to surface
 * here rather than fail at run time, which is why no member of this file is typed as
 * an escape hatch and no assertion suppresses a diagnostic.
 *
 * ## Two contract facts that were measured rather than assumed
 *
 * MIGRATION: **successful bodies arrive inside a shared envelope and are unwrapped
 * before anything reads them.** The API returns its payload as the data member of a
 * response envelope, not as the bare payload. Typing a call as the bare payload
 * would compile and then fail in the quietest possible way — every member would read
 * as undefined and a stored session would be a shape-correct blank — so each call
 * below states the envelope explicitly and projects the data member out of it.
 *
 * MIGRATION: **sign-out carries the credential it revokes.** Sign-out answers 204
 * whatever it finds, revokes the renewal credential only, and keeps no deny-list, so
 * it cannot recall an access token already issued — the legacy cookie-clearing
 * mechanism took effect at once and has no stateless counterpart. It does, however,
 * take a body: the API declares a required body of the same shape the renewal
 * operation uses, and there is no distinct sign-out shape. Posting nothing would be
 * refused as a malformed request before the operation ran, so the credential being
 * revoked is transmitted, and discarding local state is this client's separate
 * responsibility.
 */

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  /**
   * Exchanges credentials for a token pair and the identity it was issued for.
   *
   * `POST auth/login`, answering `200` with the pair, or refusing: `401` for bad credentials,
   * `403` for a locked-out or not-approved account, `400` for a malformed submission and `429`
   * when the credential window is spent. Every refusal is re-thrown exactly as it arrived so the
   * caller can render the server's problem document, which distinguishes the four.
   *
   * ⚠ NOTHING IS STORED HERE, AND NOTHING ELSE IS FETCHED HERE. The response is decoded and
   * returned. Committing it to the session, reading the caller's expanded authority through
   * {@link AuthService.me}, and deciding what a failed attempt does to a session already held are
   * all the lifecycle owner's, `core/state/auth.store.ts`.
   *
   * @param request The credentials, and the verification code when one was supplied. Transmitted
   * exactly as given: an empty code travels as an empty string and an omitted one stays omitted,
   * because the legacy ladder branched on that very distinction.
   * @param selector The tenant to sign in to, when the arrival host resolves none.
   * @returns The decoded token pair and the authority-minimised identity it was issued for.
   */
  login(
    request: LoginRequest,
    selector?: LoginPortalSelector | null,
  ): Observable<LoginResponse> {
    return this.http
      .post<unknown>(AUTH_ENDPOINTS.login, request, {
        // The tenant selector travels as a QUERY parameter, not in the body: the endpoint
        // resolves the tenant from the arrival host and admits `?portalId=` only as the
        // fallback for a host with no alias row. `loginParams` transmits -1 and 0 as data,
        // because `Portals.PortalID` is `IDENTITY(-1,1)` and both name real tenants.
        params: loginParams(selector),
      })
      .pipe(map((body) => LOGIN_RESPONSE(body, RESPONSE_ROOT)));
  }

  /**
   * Exchanges a renewal credential for a rotated pair.
   *
   * `POST auth/refresh`, answering `200` with a pair of the same shape sign-in returns — the API
   * declares no separate renewal response — or refusing with `401`, `400` or `429`.
   *
   * ⚠ THE CREDENTIAL IS AN ARGUMENT, NOT SOMETHING READ FROM STORAGE. This method neither knows
   * where the renewal credential is kept nor whether one is held, and it does not coalesce
   * concurrent callers: presenting one rotating credential twice is a replay the server answers
   * by revoking the whole family, so exactly one owner must serialise renewals, and that owner is
   * `core/state/auth.store.ts`. A second in-flight slot here would be a second source of truth
   * for the same fact — the arrangement in which a renewal begun before a sign-out could store its
   * pair afterwards and resurrect the session the operator had just ended.
   *
   * @param request The renewal credential to present, in the body shape the API declares.
   * @returns The decoded rotated pair.
   */
  refresh(request: RefreshTokenRequest): Observable<LoginResponse> {
    return this.http
      .post<unknown>(AUTH_ENDPOINTS.refresh, request)
      .pipe(map((body) => LOGIN_RESPONSE(body, RESPONSE_ROOT)));
  }

  /**
   * Asks the server to withdraw a renewal credential.
   *
   * `POST auth/logout`, answering `204` for a credential it found and for one it did not — a
   * different answer would turn the operation into a probe for which sessions are live, and an
   * anonymous caller must not be handed that. It can still REFUSE: `400` for a malformed body,
   * `429` when the revocation window is spent and `503` when the session store cannot be reached.
   *
   * ⚠ A REFUSAL PROPAGATES. It used to be absorbed here and reported as completion, which made a
   * live renewal credential indistinguishable from a withdrawn one. Whether local sign-out
   * completes regardless is a policy decision, and it is made — together with recording the
   * outstanding revocation and telling the operator — by the lifecycle owner.
   *
   * MIGRATION: sign-out reaches the RENEWAL credential only. An access token already issued
   * cannot be recalled, so it stays valid until it lapses, which is why that lifetime is short;
   * the legacy `FormsAuthentication.SignOut` cleared a cookie and took effect at once and has no
   * stateless counterpart. The body is required and is the same shape renewal uses — there is no
   * distinct sign-out contract — so posting nothing would be refused before the operation ran.
   *
   * @param request The renewal credential to withdraw.
   * @returns Completion. `204` carries no body, so there is nothing to decode.
   */
  logout(request: RefreshTokenRequest): Observable<void> {
    return this.http.post<void>(AUTH_ENDPOINTS.logout, request);
  }

  /**
   * Asks the server to describe the caller.
   *
   * `GET auth/me`, the only authorised operation of the four, and the way a client learns its own
   * roles and granted permission codes — which is why no client needs the administrative
   * permission-query operations merely to describe itself. Answers `200` with the description,
   * `401` when identity was not proved, `404` when the token names an account that no longer
   * resolves, and `429` on its own budget.
   *
   * ⚠ THE ANSWER IS NOT ENFORCEMENT. Every authorisation decision is made again on the server for
   * every request; the permission codes exist so that a screen can avoid offering an action that
   * would be refused, never so that a client can decide an access question for itself.
   *
   * THE OPTIONAL CREDENTIAL IS THE ONE PLACE A BEARER HEADER IS WRITTEN BY HAND, and it exists
   * for a single caller: the bootstrap read that follows a sign-in or a renewal. At that moment
   * the rotated token exists only as a local value — it is deliberately not stored until the
   * identity has been fetched, so that a failed bootstrap cannot leave a half-populated session
   * behind — and an interceptor reading storage would attach the PREVIOUS token, or none, and on
   * renewal would treat the resulting refusal as cause for another renewal. Passing the freshly
   * issued token explicitly is what breaks that recursion, and the interceptor skips a request
   * that already carries the header.
   *
   * Omit the argument once a session is held: the bearer token is then attached by
   * `core/interceptors/auth.interceptor.ts` and the correlation identifier by
   * `core/interceptors/correlation-id.interceptor.ts`, in that fixed order, and substituting
   * either by hand would defeat the interceptor's ability to recover from a refused request.
   *
   * @param accessToken The freshly issued token to present, for the bootstrap read alone. Omitted
   * or null for every other caller, which leaves the credential to the interceptor.
   * @returns The caller's decoded identity, roles and granted permission codes.
   */
  me(accessToken?: string | null): Observable<CurrentUser> {
    const options =
      accessToken === undefined || accessToken === null
        ? {}
        : { headers: new HttpHeaders({ [AUTHORIZATION_HEADER]: `Bearer ${accessToken}` }) };

    return this.http
      .get<unknown>(AUTH_ENDPOINTS.me, options)
      .pipe(map((body) => CURRENT_USER_RESPONSE(body, RESPONSE_ROOT)));
  }
}
