import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';

// Type-only, and deliberately so. `HttpErrorResponse` above is a VALUE import
// because the 401 test is an `instanceof` check against the concrete class, whereas
// these two are erased at compile time and must not emit a runtime import.
import type { HttpInterceptorFn, HttpRequest } from '@angular/common/http';

import { isAnonymousAuthEndpoint, isApiRequest } from '../config/api-endpoints';
import { AuthService } from '../services/auth.service';
import { TokenStorageService } from '../services/token-storage.service';

/**
 * The request header the API reads a bearer token from.
 *
 * Named here rather than imported from the transport library because it is part of
 * the wire contract with the API, and because the framework's own constant is not
 * exported for this purpose. The scheme token is emitted as `Bearer` with a single
 * trailing space; HTTP defines the scheme name as case-insensitive, and this is the
 * conventional casing.
 */
const AUTHORIZATION_HEADER = 'Authorization';

/**
 * Where an operator is sent once a session cannot be renewed.
 *
 * A route rather than a full reload, so the single-page application is not started
 * again from scratch for what is an ordinary end of session.
 *
 * The screen this addresses is not authored yet, so today the path is matched by the
 * catch-all route and the not-found screen is rendered. That is recorded rather than
 * worked around: suppressing the navigation until the screen exists would leave a
 * dead session in place with nothing telling the operator to sign in again, and
 * inventing a different destination would have to be undone once the sign-in screen
 * lands. Nothing else in this file depends on the destination resolving.
 */
const LOGIN_PATH = '/login';

/**
 * The health-probe paths, which are published at the HOST ROOT rather than under the
 * API's versioned prefix.
 *
 * All three are anonymous by design: a container health check must be able to reach
 * them without a credential and without spending a rate-limit budget, and the
 * compose file gates the front-end container on the first of them reporting healthy.
 * They are listed explicitly because the endpoint catalogue does not describe them —
 * it composes paths beneath the API base, and these sit outside it — so this is the
 * one exclusion that cannot be expressed by asking that module.
 *
 * Tested FIRST among the exclusions, and deliberately so. A probe path is not beneath
 * the API base, so {@link isApiRequest} would refuse it anyway — but only for as long
 * as that remains true. Asking this question before the API-address question means the
 * guarantee is stated in its own right rather than inherited from a test that exists
 * for a different purpose, so it survives a future widening of the configured base.
 * Ordering is otherwise immaterial: all three exclusions reach the same pass-through.
 */
const HEALTH_PROBE_PATHS: readonly string[] = Object.freeze([
  '/health',
  '/health/ready',
  '/health/live',
]);

/**
 * Attaches the stored bearer token to API requests and renews an expired session
 * once when the API answers 401.
 *
 * SECOND in the chain, between the correlation-id interceptor and the error
 * interceptor: `withInterceptors([correlationIdInterceptor, authInterceptor,
 * errorInterceptor])`.
 *
 * ## THIS FILE OWNS THE WHOLE 401 LIFECYCLE, AND WHY
 *
 * That array composes as `correlationId(next = auth(next = error(next = backend)))`,
 * so the listed order is the order on the way OUT and its reverse on the way BACK:
 *
 *     request:   correlationId -> auth -> error -> backend
 *     response:  backend -> error -> auth -> correlationId
 *
 * The error interceptor is therefore the INNERMOST of the three on the response
 * path. It sees the raw 401 BEFORE this interceptor — which surrounds it — has had
 * any opportunity to renew and retry, so nothing can have been attempted "upstream"
 * of it. The order is not rearranged to make a tidier story true, because stamping
 * the correlation identifier first is what makes a retry identifiable as a retry of
 * the same operation. Ownership is reassigned instead: detection, the single refresh,
 * the single retry and discarding the session all live here, and the error
 * interceptor says nothing at all about a 401. Both halves of that agreement are
 * annotated in both files.
 *
 * ## THE RETRY IS THE SAME LOGICAL OPERATION, AND IS TRACEABLE AS ONE
 *
 * The retry is cloned from the ORIGINAL request, which the outer correlation-id
 * interceptor has already stamped. Both attempts therefore carry the SAME
 * `X-Correlation-Id`, which is what lets the server's own logs join them into one
 * operation instead of showing an unexplained refusal followed by an unrelated
 * success.
 *
 * ## WHAT IS LEFT UNTOUCHED
 *
 * Five kinds of request pass through with no header and no recovery attempt:
 *
 * 1. Anything not addressed to this application's API. Attaching a bearer token to a
 *    static asset, a template or a third-party URL would disclose it to whoever
 *    serves that URL. The test compares RESOLVED ORIGIN and a segment-bounded path
 *    rather than spelling, because a textual test admits an absolute foreign origin
 *    that merely contains the configured base.
 * 2. The anonymous authentication endpoints — sign in, renew and sign out. They
 *    authenticate the payload they carry, not a bearer token. Excluding renew is also
 *    what makes recursion unreachable: that call is issued through this same chain,
 *    so a refused refresh would otherwise be answered by another refresh.
 * 3. The health probes, for the reasons given on {@link HEALTH_PROBE_PATHS}.
 * 4. A request that already carries an `Authorization` header. A caller that set one
 *    explicitly has a reason, and this is load-bearing rather than merely polite: the
 *    authentication service presents a freshly issued token by hand while it is
 *    establishing an identity, at a point where the rotated token is deliberately not
 *    stored yet. Overwriting it would attach the PREVIOUS token, and on renewal would
 *    treat the resulting refusal as cause for another renewal.
 * 5. Any request made while no session is held. There is nothing to attach, and the
 *    server's 401 is then the correct and final answer — so such a request is not
 *    wrapped for recovery at all.
 *
 * The identity endpoint is deliberately NOT in that list. It requires a bearer token
 * and is fully eligible for renew-and-retry.
 *
 * MIGRATION: the `Authorization` bearer header is entirely NET-NEW. The HTTP header
 * appears nowhere in the legacy tree — the single textual match for the word is
 * English prose inside an unrelated host-administration resource string at
 * `Website/admin/Host/App_LocalResources/RequestFilters.ascx.resx:L151`, not a header.
 * The legacy application carried a forms-authentication cookie instead
 * (`Website/release.config:L146-L147`: `<authentication mode="Forms">` and
 * `<forms name=".DOTNETNUKE" protection="All" timeout="60" cookieless="UseCookies"/>`),
 * which the browser attached automatically to EVERY request to the origin, images and
 * stylesheets included. Presenting a credential explicitly, and only where it is
 * needed, is a narrowing of that exposure rather than a translation of it. The
 * sixty-minute access-token window is exact parity with that `timeout="60"` — the API
 * documents `"ExpirationMinutes": 60` and caps it there — paired with a longer
 * rotating renewal window that the legacy ticket had no counterpart for.
 *
 * MIGRATION: signing out keeps no server-side deny-list, which is why the sign-out
 * endpoint is excluded above. `FormsAuthentication.SignOut()` occurs at exactly ONE
 * site in the whole legacy tree — `Library/Components/Security/PortalSecurity.vb:L79`,
 * a measured count of one — and it took effect at once by clearing the cookie in the
 * response. A bearer token cannot be recalled once issued, so sign-out became two
 * independent actions: the server revokes the renewal credential, and the client
 * discards its copy. An access token stays technically valid until its stamped
 * expiry, which is exactly why that expiry is short.
 *
 * MIGRATION: the legacy challenge-image gate is deliberately deleted, and server-side
 * request rate limiting is its named compensating control. The legacy guard at
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L162` reads
 * `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha) Then` and was the
 * ONLY anti-automation control anywhere in scope — a search for rate limiting or
 * throttling across `Library/Components` and `Website/admin` matches nothing at all.
 * The API now applies a fixed-window limiter to each credential action and answers 429
 * when the window is spent, with the identity read drawing on a separate partition so
 * that polling it cannot spend the budget signing in needs. A 429 is NEVER retried
 * here: no backoff, no attempt counter, and no reading of a retry hint. It arrives as
 * an ordinary problem document and is presented by the error interceptor.
 *
 * MIGRATION: renew-and-retry-once has no legacy precedent to preserve — a search for
 * retry logic across the five in-scope domain trees under `Library/Components` matches
 * nothing — so it is a net-new behaviour rather than a translated one, and it is
 * deliberately the ONLY retry in this file. There is no blanket retry policy, no
 * exponential backoff and no retry for any status other than the single replay that
 * follows a successful renewal.
 *
 * MIGRATION: no authenticated-or-not verdict is computed here, and the legacy defect
 * in that verdict is corrected on the server rather than in this file. The legacy
 * screen decided the outcome with
 * `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)` at
 * `Login.ascx.vb:L187`, which admitted a locked-out account because the preceding
 * branch had already consumed the only status it excluded. Nothing here mirrors that
 * predicate: this interceptor reads a stored token and an HTTP status code, and it
 * neither classifies a sign-in outcome nor keeps a client-side attempt counter or
 * lockout heuristic. The legacy four-character provider discriminator passed
 * positionally at `:L164` and again at `:L191` disappears with the single bearer path,
 * so no request shape here carries an authentication-type argument either.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Injected inside the interceptor rather than captured at module load, because an
  // `HttpInterceptorFn` runs in the injection context of the request being handled.
  // This is also what keeps the file free of module-level mutable state and therefore
  // safe under parallel test cases, each of which builds its own root injector.
  const tokenStorage = inject(TokenStorageService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (isHealthProbe(req.url) || !isApiRequest(req.url) || isAnonymousAuthEndpoint(req.url)) {
    return next(req);
  }

  if (req.headers.has(AUTHORIZATION_HEADER)) {
    return next(req);
  }

  const token = tokenStorage.accessToken();

  if (token === null || token.length === 0) {
    return next(req);
  }

  // `catchError` is attached to the FIRST attempt only. The retry below is returned
  // directly, with no handler of its own, so a 401 on the retry propagates untouched
  // and cannot re-enter this recovery path. That structure — not a counter and not a
  // marker header — is what bounds recovery to exactly one retry.
  return next(withBearerToken(req, token)).pipe(
    catchError((error: unknown) => {
      // Only an expired or rejected token is recoverable. A 403 means the server knows
      // who the caller is and is refusing the operation, so renewing would change
      // nothing; anything else is not an authentication condition at all.
      if (!isUnauthorized(error)) {
        return throwError(() => error);
      }

      // A session discarded while this request was in flight — by a concurrent
      // renewal failure, or by a sign-out that raced it — cannot be renewed. The
      // session is ended explicitly so the operator is asked to sign in rather than
      // left looking at a screen that will refuse every subsequent action.
      if (!hasRenewableSession(tokenStorage)) {
        endSession(tokenStorage, router);

        return throwError(() => error);
      }

      // The single-flight guarantee lives in the authentication service, which
      // coalesces concurrent callers onto one request, stores the rotated pair once,
      // and discards the session if the server refuses it. A second in-flight slot
      // here would be a second source of truth for the same fact: signing out clears
      // the service's slot but could not clear a private one, so a 401 racing a
      // sign-out would replay a cached renewal and resurrect the session the operator
      // had just ended. One owner, deliberately.
      return auth.refresh().pipe(
        // Exactly one retry, and only after a renewal that actually succeeded. The
        // token is re-read from the refreshed session rather than reusing the value
        // captured above, and the request is re-cloned from the ORIGINAL so the
        // correlation identifier stamped by the outer interceptor is carried onto the
        // second attempt.
        switchMap((session) => next(withBearerToken(req, session.accessToken))),
        catchError((refreshError: unknown) => {
          // Terminal. The renewal credential the server refused cannot be retried, so
          // the session is over and the operator is routed to sign in again.
          //
          // The ORIGINAL 401 is re-thrown rather than the renewal failure, because
          // that is the failure the caller asked about; reporting the renewal error
          // would replace "your request was not authorised" with an unrelated message
          // about a token the caller never sent. The renewal error is deliberately not
          // logged either — it can carry the credential that was refused.
          void refreshError;

          endSession(tokenStorage, router);

          return throwError(() => error);
        }),
      );
    }),
  );
};

/**
 * Returns a copy of the request carrying the bearer token.
 *
 * `HttpHeaders` is immutable, so a header is applied by cloning the request. The clone
 * is what makes a retry safe: the original request object is never mutated, so
 * re-sending it cannot accumulate headers from a previous attempt.
 *
 * @param req The outbound request.
 * @param token The access token to present.
 * @returns A clone carrying the `Authorization` header.
 */
function withBearerToken<T>(req: HttpRequest<T>, token: string): HttpRequest<T> {
  return req.clone({ setHeaders: { [AUTHORIZATION_HEADER]: `Bearer ${token}` } });
}

/**
 * Whether a caught value is an HTTP 401 response.
 *
 * Tests the concrete response type before reading the status, because a failure
 * reaching an interceptor is not necessarily an `HttpErrorResponse` — a downstream
 * interceptor or an operator can throw anything at all, and reading `.status` off such
 * a value would yield `undefined` and silently compare unequal.
 *
 * @param error The caught value.
 * @returns True when the value is a 401 response.
 */
function isUnauthorized(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 401;
}

/**
 * Whether a renewal credential is held to attempt a renewal with.
 *
 * An empty value is treated as absent rather than transmitted. The legacy
 * absent-string sentinel WAS the empty string, so the two forms are the same
 * statement, and posting one would be refused as a malformed request instead of
 * reported as a session that has ended.
 *
 * This is a presence test and deliberately not an expiry test. No stored expiry is
 * read and no clock is consulted anywhere in this file: renewal is REACTIVE on a
 * refusal, and a proactive check would only add a second, disagreeing opinion about
 * whether a token the server has not yet rejected is still good.
 *
 * @param tokenStorage The session store.
 * @returns True when a renewal can be attempted.
 */
function hasRenewableSession(tokenStorage: TokenStorageService): boolean {
  const refreshToken = tokenStorage.refreshToken();

  return refreshToken !== null && refreshToken.length > 0;
}

/**
 * Discards the session and asks the operator to sign in again.
 *
 * Safe to reach from several concurrent failures at once: discarding is idempotent,
 * and the router ignores a repeat navigation to the URL it is already on.
 *
 * A failed navigation is swallowed and reported as "did not navigate". The caller is
 * mid-way through re-throwing the response the server actually sent, and a routing
 * problem must not displace it — nor become an unhandled rejection that surfaces
 * somewhere unrelated. Nothing is written to a log sink here, because the values in
 * scope at this point include the credential that was just refused.
 *
 * @param tokenStorage The session store to clear.
 * @param router The router to leave through.
 */
function endSession(tokenStorage: TokenStorageService, router: Router): void {
  tokenStorage.clear();

  void router.navigate([LOGIN_PATH]).catch(() => false);
}

/**
 * Whether a request URL addresses one of the anonymous health probes.
 *
 * Compared on the RESOLVED path so that the answer describes where the request goes
 * rather than how it is spelled, with a trailing slash treated as the same path and a
 * query string or fragment ignored. A value that is not a URL in any form the platform
 * recognises is not a probe.
 *
 * @param url The outbound request URL.
 * @returns True when the request is a health probe.
 */
function isHealthProbe(url: string): boolean {
  let resolved: URL;

  try {
    resolved = new URL(url, document.baseURI);
  } catch {
    // Deliberately swallowed: the contract is a boolean, and a value that cannot be
    // resolved is simply not a probe. The API-address test applies the same reading.
    return false;
  }

  const path = resolved.pathname;
  const normalised = path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;

  return HEALTH_PROBE_PATHS.includes(normalised);
}
