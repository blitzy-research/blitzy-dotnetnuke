import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

import { isAnonymousAuthEndpoint, isApiRequest } from '../config/api-endpoints';
import { AuthService } from '../services/auth.service';
import { TokenStorageService } from '../services/token-storage.service';

/**
 * The request header the API reads a bearer token from.
 *
 * Named here rather than imported from the transport library because it is part of
 * the wire contract with the API, and because the framework's own constant is not
 * exported for this purpose.
 */
const AUTHORIZATION_HEADER = 'Authorization';

/**
 * Attaches the stored bearer token to API requests and renews an expired session
 * once when the API answers 401.
 *
 * Second in the chain, between the correlation-id interceptor and the error
 * interceptor. The order is fixed and load-bearing: the correlation id must be
 * stamped before anything can retry, so that a retried request is identifiable as
 * a retry of the same operation, and error translation must run last so that it
 * observes the FINAL response — a 401 that this interceptor successfully recovers
 * from must never reach the error interceptor and must never be shown to a person.
 *
 * Four requests are deliberately left untouched:
 *
 * 1. Anything not addressed to this application's API. Attaching a bearer token to
 *    a static asset, a template or a third-party URL would disclose the token to
 *    whoever serves it.
 * 2. The anonymous authentication endpoints — login, refresh and logout. They
 *    authenticate the credentials or the refresh token they carry, not a bearer
 *    token. Sending an expired bearer token to the refresh endpoint would be
 *    pointless at best, and attempting to recover a failed refresh with another
 *    refresh is the recursion this exclusion prevents.
 * 3. A request that already carries an `Authorization` header. A caller that set
 *    one explicitly has a reason, and silently overwriting it would make that
 *    impossible to express.
 * 4. Any request made while no session is held. There is nothing to attach, and the
 *    server's 401 is then the correct and final answer.
 *
 * MIGRATION: the legacy application authenticated with a forms-authentication
 * cookie that the browser attached automatically to every request to the origin,
 * including requests for images and stylesheets. Presenting a token explicitly and
 * only where it is needed is a narrowing of that exposure, not a translation of it.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Injected inside the interceptor rather than captured at module load, because an
  // `HttpInterceptorFn` runs in the injection context of the request. This is also
  // what keeps the interceptor free of module-level state and therefore safe to run
  // in parallel test cases.
  const tokenStorage = inject(TokenStorageService);
  const auth = inject(AuthService);

  if (!isApiRequest(req.url) || isAnonymousAuthEndpoint(req.url)) {
    return next(req);
  }

  if (req.headers.has(AUTHORIZATION_HEADER)) {
    return next(req);
  }

  const token = tokenStorage.accessToken();

  if (token === null || token.length === 0) {
    return next(req);
  }

  return next(withBearerToken(req, token)).pipe(
    catchError((error: unknown) => {
      // Only an expired or rejected token is recoverable. A 403 means the server
      // knows who the caller is and is refusing the operation, so refreshing would
      // change nothing; anything else is not an authentication condition at all.
      if (!isUnauthorized(error)) {
        return throwError(() => error);
      }

      // A session that has already been discarded — by a concurrent refresh
      // failure, or by an explicit sign-out that raced this request — cannot be
      // renewed. Reporting the original 401 is then correct and terminal.
      if (tokenStorage.refreshToken() === null) {
        return throwError(() => error);
      }

      return auth.refresh().pipe(
        // Exactly one retry, and only after a refresh that actually succeeded. The
        // retry re-reads the token from the refreshed session rather than reusing
        // the value captured above. Because this pipeline is entered only from a
        // request that carried a token, and because the refresh call itself is
        // excluded from this interceptor, there is no path by which the retry can
        // fail its way back into another refresh.
        switchMap((session) => next(withBearerToken(req, session.accessToken))),
        catchError((refreshError: unknown) => {
          // The refresh failed, so the session is gone — the authentication service
          // has already cleared it. The ORIGINAL 401 is re-thrown rather than the
          // refresh failure, because that is the failure the caller asked about;
          // reporting the refresh error would replace "your request was not
          // authorised" with an unrelated message about a token the caller never
          // sent.
          void refreshError;

          return throwError(() => error);
        }),
      );
    }),
  );
};

/**
 * Returns a copy of the request carrying the bearer token.
 *
 * `HttpHeaders` is immutable, so a header is applied by cloning the request. The
 * clone is what makes a retry safe: the original request object is never mutated,
 * so re-sending it cannot accumulate headers from a previous attempt.
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
 * interceptor or an operator can throw anything at all, and reading `.status` off
 * such a value would yield `undefined` and silently compare unequal.
 *
 * @param error The caught value.
 * @returns True when the value is a 401 response.
 */
function isUnauthorized(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 401;
}
