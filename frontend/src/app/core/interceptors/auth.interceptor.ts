import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';

// Type-only, and deliberately so. `HttpErrorResponse` above is a VALUE import because the 401 test is an
// `instanceof` check against the concrete class, whereas these two are erased at compile time and must not
// emit a runtime import.
import type { HttpInterceptorFn, HttpRequest } from '@angular/common/http';

import { isAnonymousAuthEndpoint, isApiRequest } from '../config/api-endpoints';
import { RETURN_URL_QUERY_KEY, SIGN_IN_ROUTE } from '../config/app-routes.config';
import { TokenStorageService } from '../services/token-storage.service';
import { AuthStore } from '../state/auth.store';
import { SessionTeardownService } from '../state/session-teardown.service';

/**
 * The request header the API reads a bearer token from. Named here rather than imported from the
 * transport library because it is part of the wire contract with the API, and because the framework's own
 * constant is not exported for this purpose.
 */
const AUTHORIZATION_HEADER = 'Authorization';

/**
 * The health-probe paths, which are published at the HOST ROOT rather than under the API's versioned
 * prefix. All three are anonymous by design: a container health check must be able to reach them without
 * a credential and without spending a rate-limit budget, and the compose file gates the front-end
 * container on the first of them reporting healthy.
 */
const HEALTH_PROBE_PATHS: readonly string[] = Object.freeze([
  '/health',
  '/health/ready',
  '/health/live',
]);

/**
 * Attaches the stored bearer token to API requests and renews an expired session once when the API
 * answers 401.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Injected inside the interceptor rather than captured at module load, because an `HttpInterceptorFn`
  // runs in the injection context of the request being handled.
  const tokenStorage = inject(TokenStorageService);
  const authStore = inject(AuthStore);
  const router = inject(Router);
  const sessionTeardown = inject(SessionTeardownService);

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

  // ⚠ THE SESSION THIS REQUEST BELONGS TO IS CAPTURED HERE, ALONGSIDE ITS TOKEN, AND EVERY RECOVERY
  // DECISION BELOW IS CONDITIONED ON IT.
  const requestGeneration = tokenStorage.generation();

  // The RECOVERY handler is attached to the FIRST attempt only. The retry below carries a handler of its
  // own, but a CLOSED one: it neither renews nor re-sends, so it cannot re-enter this recovery path.
  return next(withBearerToken(req, token)).pipe(
    catchError((error: unknown) => {
      // Only an expired or rejected token is recoverable. A 403 means the server knows who the caller is
      // and is refusing the operation, so renewing would change nothing; anything else is not an
      // authentication condition at all.
      if (!isUnauthorized(error)) {
        return throwError(() => error);
      }

      if (!tokenStorage.isCurrentGeneration(requestGeneration)) {
        return throwError(() => error);
      }

      if (!hasRenewableSession(tokenStorage)) {
        endSession(tokenStorage, router, sessionTeardown);

        return throwError(() => error);
      }

      // ⚠ THE PRIMITIVE IS CALLED, NOT THE OWNER'S DELIBERATE RENEWAL COMMAND, and the distinction is
      // load-bearing rather than incidental. That command discards the session and records the failure on
      // every refusal, which is right for a caller that ASKED to renew and is about to be sent to sign in.
      return authStore.renewSession().pipe(
        catchError((renewalError: unknown) => {
          // Terminal. The renewal credential the server refused cannot be retried, so the session is over
          // and the operator is routed to sign in again.
          void renewalError;

          if (tokenStorage.isAuthenticated()) {
            return throwError(() => error);
          }

          endSession(tokenStorage, router, sessionTeardown);

          return throwError(() => error);
        }),
        // Exactly one retry, and only after a renewal that actually succeeded. The request is re-cloned
        // from the ORIGINAL so the correlation identifier stamped by the outer interceptor is carried onto
        // the second attempt.
        switchMap((session) => {
          // A renewal is two round trips, so the window between CHECK ONE and this point is the widest in
          // the whole path - and a sign-out or an account switch landing inside it is precisely the race
          // being closed.
          if (tokenStorage.accessToken() !== session.accessToken) {
            return throwError(() => error);
          }

          return next(withBearerToken(req, session.accessToken)).pipe(
            catchError((retryError: unknown) => {
              if (!isUnauthorized(retryError)) {
                return throwError(() => retryError);
              }

              // ⚠ AND CONDITIONED, EXACTLY AS CHECK TWO WAS, ON TOKEN IDENTITY.
              if (tokenStorage.accessToken() !== session.accessToken) {
                return throwError(() => retryError);
              }

              endSession(tokenStorage, router, sessionTeardown);

              // THE RETRY'S OWN 401 is re-thrown, not the original. Both are 401s, but this one is the
              // server's answer to the request that actually carried the renewed credential, so its problem
              // document is the one that describes what happened.
              return throwError(() => retryError);
            }),
          );
        }),
      );
    }),
  );
};

/**
 * Returns a copy of the request carrying the bearer token. `HttpHeaders` is immutable, so a header is
 * applied by cloning the request.
 *
 * @param req The outbound request.
 * @param token The access token to present.
 * @returns A clone carrying the `Authorization` header.
 */
function withBearerToken<T>(req: HttpRequest<T>, token: string): HttpRequest<T> {
  return req.clone({ setHeaders: { [AUTHORIZATION_HEADER]: `Bearer ${token}` } });
}

/**
 * Whether a caught value is an HTTP 401 response. Tests the concrete response type before reading the
 * status, because a failure reaching an interceptor is not necessarily an `HttpErrorResponse` — a
 * downstream interceptor or an operator can throw anything at all, and reading `.status` off such a value
 * would yield `undefined` and silently compare unequal.
 *
 * @param error The caught value.
 * @returns True when the value is a 401 response.
 */
function isUnauthorized(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 401;
}

/**
 * Whether a renewal credential is held to attempt a renewal with. An empty value is treated as absent
 * rather than transmitted.
 *
 * @param tokenStorage The session store.
 * @returns True when a renewal can be attempted.
 */
function hasRenewableSession(tokenStorage: TokenStorageService): boolean {
  const refreshToken = tokenStorage.refreshToken();

  return refreshToken !== null && refreshToken.length > 0;
}

/**
 * Discards the session and asks the operator to sign in again. Safe to reach from several concurrent
 * failures at once: discarding is idempotent, and the router ignores a repeat navigation to the URL it is
 * already on. ⚠ THE PURGE IS NOT OPTIONAL HERE, and this is the likeliest place a session actually ends.
 *
 * @param tokenStorage The session store to clear.
 * @param router The router to leave through.
 * @param sessionTeardown The fan-out that empties the domain stores AND explains the ending.
 */
function endSession(
  tokenStorage: TokenStorageService,
  router: Router,
  sessionTeardown: SessionTeardownService,
): void {
  // Cleared FIRST, because clearing advances the session generation that every late callback tests itself
  // against. Purging before the generation moved would leave a read already in flight still believing its
  // session was current, free to repopulate the very slices the purge had just emptied.
  tokenStorage.clear();

  // ⚠ AND IT IS WHAT MAKES THE OPERATOR TOLD WHY, WHICH IS WHY NOTHING IS RAISED HERE. Measured in a
  // browser: the teardown was complete and correct, and both live regions were EMPTY, so somebody mid-task
  // was returned to the sign-in screen with no account, no work and no explanation.
  sessionTeardown.purge('renewalRefused');

  // The duplication was not harmless.

  const attempted = router.url;
  const returnTo = attempted.split('?')[0] === SIGN_IN_ROUTE ? null : attempted;

  void router
    .navigate([SIGN_IN_ROUTE], {
      replaceUrl: true,
      ...(returnTo === null ? {} : { queryParams: { [RETURN_URL_QUERY_KEY]: returnTo } }),
    })
    .catch(() => false);
}

/**
 * Whether a request URL addresses one of the anonymous health probes. Compared on the RESOLVED path so
 * that the answer describes where the request goes rather than how it is spelled, with a trailing slash
 * treated as the same path and a query string or fragment ignored.
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
