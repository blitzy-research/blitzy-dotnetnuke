import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  Observable,
  catchError,
  finalize,
  map,
  of,
  shareReplay,
  switchMap,
  tap,
  throwError,
} from 'rxjs';

import { AUTH_ENDPOINTS } from '../config/api-endpoints';
import {
  AuthSession,
  CurrentUser,
  LoginRequest,
  LoginResponse,
  RefreshTokenRequest,
  sessionFromLoginResponse,
} from '../models/auth.model';
import { ApiResponse } from '../models/paged-result.model';
import { TokenStorageService } from './token-storage.service';

const AUTHORIZATION_HEADER = 'Authorization';

/**
 * Owns the authentication surface: sign in, refresh, sign out, and describe the caller.
 *
 * Deliberately narrow. It performs API communication and updates the stored
 * session, and it holds no screen state, no form state and no business rules —
 * Angular services are restricted to API communication by the migration
 * discipline, and every authorisation decision belongs to the server.
 *
 * The single-flight refresh is the reason this is a service rather than a helper
 * inside the interceptor. Interceptors are functions, so per-instance state has
 * nowhere to live in them except module scope, and module scope leaks between test
 * cases and between application instances. Holding the in-flight refresh on an
 * injectable keeps it scoped to the injector that created it.
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
  private readonly tokenStorage = inject(TokenStorageService);

  /**
   * The refresh currently in progress, or null when none is.
   *
   * This is what makes a burst of simultaneous 401 responses produce ONE refresh
   * call rather than one per failed request. Without it, six parallel list
   * requests expiring together would each present the same refresh token; the
   * first would rotate it and the remaining five would present a token that had
   * already been used, which the server treats as a replay and answers by revoking
   * the account's entire refresh-token family — signing the person out precisely
   * because the client tried to keep them signed in.
   */
  private refreshInFlight: Observable<AuthSession> | null = null;

  /** The signed-in identity, or null. Re-exposed so consumers need one injection. */
  readonly currentUser = this.tokenStorage.currentUser;

  /** Whether a session is held. */
  readonly isAuthenticated = this.tokenStorage.isAuthenticated;

  /**
   * Whether the signed-in account must complete its profile before continuing.
   *
   * Re-exposed for the same reason as {@link currentUser}: a consumer of this
   * service should not need a second injection to read a fact about the session it
   * just established. {@link login} returns the identity rather than the session, so
   * without this projection the advisory would have no reader at all.
   */
  readonly mustUpdateProfile = this.tokenStorage.mustUpdateProfile;

  /**
   * Exchanges credentials for a token pair and stores the resulting session.
   *
   * A failure is re-thrown unchanged so the caller can render the server's problem
   * document, which distinguishes bad credentials from a locked-out account and
   * from a password the server refuses to accept as secure. The stored session is
   * cleared first, so a failed sign-in cannot leave an earlier session in place.
   *
   * @param request The credentials, optionally naming the tenant.
   * @returns The signed-in identity.
   */
  login(request: LoginRequest): Observable<CurrentUser> {
    this.tokenStorage.clear();

    // The payload arrives inside the shared success envelope, so it is unwrapped before
    // anything reads it. Typing the call as the bare payload instead would compile and
    // then fail at run time in the quietest possible way: every member of the session
    // would read as undefined, and the stored session would be a shape-correct blank.
    return this.http.post<ApiResponse<LoginResponse>>(AUTH_ENDPOINTS.login, request).pipe(
      map((envelope) => sessionFromLoginResponse(envelope.data)),
      switchMap((session) =>
        this.loadCurrentUser(session.accessToken).pipe(
          map((user) => ({ ...session, user })),
        ),
      ),
      tap((session) => this.tokenStorage.store(session)),
      map((session) => session.user),
      catchError((error: unknown) => {
        this.tokenStorage.clear();
        return throwError(() => error);
      }),
    );
  }

  /**
   * Exchanges the stored refresh token for a new pair, coalescing concurrent
   * callers onto one request.
   *
   * Fails immediately when no refresh token is held, rather than posting an empty
   * one: the server would answer 400, and the caller would have to distinguish
   * that from a genuine rejection. The error is produced lazily inside the returned
   * observable so that this method never throws synchronously — an interceptor
   * calling it inside a `catchError` must be able to rely on getting an observable
   * back.
   *
   * On failure the session is discarded. A refresh token that the server refuses
   * cannot be retried, and keeping it would mean re-presenting it on the next
   * 401 and being refused again.
   *
   * @returns The refreshed session.
   */
  refresh(): Observable<AuthSession> {
    const inFlight = this.refreshInFlight;

    if (inFlight !== null) {
      return inFlight;
    }

    const refreshToken = this.tokenStorage.refreshToken();

    if (refreshToken === null || refreshToken.length === 0) {
      return throwError(() => new Error('No refresh token is held, so the session cannot be renewed.'));
    }

    const body: RefreshTokenRequest = { refreshToken };

    const request = this.http.post<ApiResponse<LoginResponse>>(AUTH_ENDPOINTS.refresh, body).pipe(
      map((envelope) => sessionFromLoginResponse(envelope.data)),
      switchMap((session) =>
        this.loadCurrentUser(session.accessToken).pipe(
          map((user) => ({ ...session, user })),
        ),
      ),
      // Storing here rather than at the call site is what guarantees the rotated
      // refresh token replaces the consumed one exactly once, however many
      // subscribers are sharing this request.
      tap((session) => this.tokenStorage.store(session)),
      catchError((error: unknown) => {
        this.tokenStorage.clear();

        return throwError(() => error);
      }),
      // Clears the slot on completion, error and unsubscription alike, so a later
      // 401 starts a fresh refresh rather than replaying this one's outcome
      // forever. Placed before `shareReplay` so it observes the source, not each
      // subscriber.
      finalize(() => {
        this.refreshInFlight = null;
      }),
      // `refCount: false` keeps the single subscription to the HTTP request alive
      // even if every current subscriber unsubscribes, so a cancelled request does
      // not silently abandon the refresh for the subscribers still waiting.
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    this.refreshInFlight = request;

    return request;
  }

  /**
   * Asks the server to describe the caller, using the session already held.
   *
   * This is the fourth and last operation of the authentication surface, and the only
   * one of the four that requires a bearer token. It is how a client learns its own
   * roles and granted permission codes, which is why no client needs the
   * administrative permission-query operations merely to describe itself.
   *
   * Deliberately sets NO headers. The bearer token is attached by
   * `core/interceptors/auth.interceptor.ts` and the correlation identifier by
   * `core/interceptors/correlation-id.interceptor.ts`, in that fixed order, and both
   * are registered once where the HTTP client is provided. Attaching a token here as
   * well would substitute a value the interceptor is responsible for choosing and
   * would defeat its ability to recover from a refused request — which is precisely
   * the difference between this method and {@link loadCurrentUser} below, and the
   * reason both exist.
   *
   * The answer is NOT enforcement. Every authorisation decision is made again on the
   * server for every request; the permission codes returned here exist so that a
   * screen can avoid offering an action that would be refused, never so that a client
   * can decide an access question for itself.
   *
   * @returns The caller's identity, roles and granted permission codes.
   */
  me(): Observable<CurrentUser> {
    return this.http
      .get<ApiResponse<CurrentUser>>(AUTH_ENDPOINTS.me)
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Reads expanded display authority only from the explicit current-user endpoint.
   *
   * Login and refresh responses intentionally carry an authority-minimised identity, and access
   * tokens carry no role or permission claims. The freshly issued token is attached explicitly so
   * the general auth interceptor neither substitutes an older token nor recursively refreshes this
   * bootstrap read.
   *
   * This is the ONE place a credential header is written by hand, and it is not an
   * alternative to {@link me} — it is the same read performed at a moment when the
   * interceptor cannot serve it. During sign-in and renewal the rotated token exists
   * only as a local value: it is deliberately not stored until the identity has been
   * fetched, so that a failed bootstrap cannot leave a half-populated session behind.
   * An interceptor reading storage at that instant would attach the PREVIOUS token, or
   * none at all, and on renewal would treat the resulting refusal as cause for another
   * renewal. Passing the freshly issued token explicitly is what breaks that
   * recursion. Once a session is held, {@link me} is the correct entry point and this
   * one must not be reached for.
   */
  private loadCurrentUser(accessToken: string): Observable<CurrentUser> {
    const headers = new HttpHeaders({
      [AUTHORIZATION_HEADER]: `Bearer ${accessToken}`,
    });

    return this.http
      .get<ApiResponse<CurrentUser>>(AUTH_ENDPOINTS.me, { headers })
      .pipe(map((envelope) => envelope.data));
  }

  /**
   * Revokes the stored refresh token and discards the session.
   *
   * Local state is cleared whatever the server answers, and the observable
   * completes successfully even when the call fails. A person who asks to sign out
   * must end up signed out on this device; leaving the session in place because a
   * revocation request failed would be the opposite of what they asked for, and
   * they cannot act on the error in any case.
   *
   * MIGRATION: sign-out has no effect on an access token that has already been
   * issued. A bearer token cannot be recalled, so this revokes the refresh token
   * and the access token remains valid until it expires — which is why its
   * lifetime is short. The legacy `FormsAuthentication.SignOut` cleared a cookie
   * and took effect at once.
   */
  logout(): Observable<void> {
    const refreshToken = this.tokenStorage.refreshToken();

    this.tokenStorage.clear();
    this.refreshInFlight = null;

    if (refreshToken === null || refreshToken.length === 0) {
      return of(undefined);
    }

    const body: RefreshTokenRequest = { refreshToken };

    // No envelope here, and none is expected. Sign-out answers 204, which HTTP forbids
    // from carrying a body, so there is nothing to unwrap - see the note on
    // EmptyApiResponse, which is why that shape has no producer.
    return this.http.post<void>(AUTH_ENDPOINTS.logout, body).pipe(
      map(() => undefined),
      catchError(() => of(undefined)),
    );
  }
}
