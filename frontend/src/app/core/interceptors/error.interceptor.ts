import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import {
  ProblemDetails,
  isProblemDetails,
  problemDetailsMessage,
} from '../models/problem-details.model';
import { NotificationService } from '../services/notification.service';

/**
 * Translates a failed response into one readable sentence and reports it.
 *
 * LAST in the chain, after the correlation-id and authentication interceptors. The
 * position is what makes it correct: a 401 that the authentication interceptor
 * recovers from by refreshing and retrying never reaches here, so a person is not
 * told their session expired when it was renewed for them without their noticing.
 *
 * The error is always RE-THROWN. This interceptor reports, it does not swallow: a
 * feature still needs to know its request failed so it can leave its form open,
 * keep the row it failed to delete, or restore the value it failed to save.
 * Converting the failure into a successful empty response would make every caller
 * appear to succeed.
 *
 * Two classes of failure are deliberately NOT announced:
 *
 * - A validation failure (422, or a 400 carrying per-field messages). Those belong
 *   beside the fields they describe, which is what the shared form-field and
 *   error-banner components render. Announcing them as well would say the same
 *   thing twice, once in a place the person cannot act on.
 * - A 401. Either the authentication interceptor is about to recover it, in which
 *   case there is nothing to say, or the session is genuinely gone, in which case
 *   the sign-in screen is the message. A notification would be noise in both cases.
 *
 * MIGRATION: the legacy application had no uniform failure path. Server-side
 * validators rendered a summary into the page, unhandled exceptions produced an
 * error page, and provider failures surfaced however each call site chose. The
 * legacy exception plumbing lived at HTTP-module and page level, so there are no
 * in-scope call sites to translate — this is new behaviour, and it is documented as
 * new rather than presented as a port.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const notifications = inject(NotificationService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && shouldAnnounce(error)) {
        notifications.error(describe(error));
      }

      return throwError(() => error);
    }),
  );
};

/**
 * Whether a failed response should produce a notification.
 *
 * @param error The failed response.
 * @returns True when the failure is worth announcing.
 */
function shouldAnnounce(error: HttpErrorResponse): boolean {
  if (error.status === 401) {
    return false;
  }

  // A per-field message set means the failure is already being rendered against the
  // fields. `problem.errors` is the only reliable marker: the status alone cannot
  // distinguish a validation failure from any other rejected request, because the
  // API answers 400 for both a malformed request and a refused domain operation.
  const problem = readProblem(error);

  return !(problem !== null && problem.errors !== undefined);
}

/**
 * Resolves the sentence to show for a failed response.
 *
 * @param error The failed response.
 * @returns A non-blank sentence.
 */
function describe(error: HttpErrorResponse): string {
  // Status 0 means the response never arrived: the network is unavailable, the
  // request was blocked, or it was aborted. There is no body to read and no server
  // message to relay, and the browser's own text ("Http failure response for
  // /api/v1/portals: 0 Unknown Error") names an internal URL and a status of zero,
  // which tells a person nothing they can act on.
  if (error.status === 0) {
    return NETWORK_UNAVAILABLE;
  }

  const problem = readProblem(error);

  if (problem !== null) {
    return problemDetailsMessage(problem, fallbackForStatus(error.status));
  }

  return fallbackForStatus(error.status);
}

/**
 * Reads the problem document from a failed response, or null when there is none.
 *
 * The body arrives already parsed when the response advertised JSON, and as a
 * string when it did not — which happens for a failure produced by the reverse
 * proxy rather than by the API, such as a gateway timeout returning an HTML page.
 * The string case is parsed defensively because a proxy is free to return anything
 * at all, and a parse failure here must not replace the caller's error with a
 * syntax error.
 *
 * @param error The failed response.
 * @returns The problem document, or null.
 */
function readProblem(error: HttpErrorResponse): ProblemDetails | null {
  const body: unknown = error.error;

  if (isProblemDetails(body)) {
    return body;
  }

  if (typeof body !== 'string' || body.trim().length === 0) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(body);

    return isProblemDetails(parsed) ? parsed : null;
  } catch {
    // Not JSON. An HTML error page from a proxy lands here, and the status-based
    // fallback describes it better than any part of that page would.
    return null;
  }
}

/**
 * The sentence used when the response carries no usable message.
 *
 * Grouped by what the person can do about it rather than by exact status, because
 * a status code is not a sentence and reciting one helps nobody. Every string is
 * authored here as a constant rather than written inline at the call site: the
 * template compiler collapses runs of whitespace inside template text, and binding
 * from a constant is what keeps punctuation and spacing exactly as authored.
 *
 * @param status The HTTP status code.
 * @returns A non-blank sentence.
 */
function fallbackForStatus(status: number): string {
  if (status === 403) {
    return FORBIDDEN;
  }

  if (status === 404) {
    return NOT_FOUND;
  }

  if (status === 409) {
    return CONFLICT;
  }

  if (status === 429) {
    return TOO_MANY_REQUESTS;
  }

  if (status >= 500) {
    return SERVER_ERROR;
  }

  return REQUEST_REJECTED;
}

/** Shown when the response never arrived. */
const NETWORK_UNAVAILABLE =
  'The server could not be reached. Check your connection and try again.';

/** Shown for 403, where the caller is known and the operation is refused. */
const FORBIDDEN = 'You do not have permission to perform this action.';

/** Shown for 404. */
const NOT_FOUND = 'The requested item could not be found.';

/** Shown for 409, where the record changed underneath the caller. */
const CONFLICT =
  'This item was changed by someone else. Reload it and apply your changes again.';

/** Shown for 429, where the caller has been rate limited. */
const TOO_MANY_REQUESTS = 'Too many attempts. Wait a moment and try again.';

/** Shown for any 5xx. */
const SERVER_ERROR = 'The server could not complete the request. Try again shortly.';

/** Shown for any other rejected request. */
const REQUEST_REJECTED = 'The request could not be completed.';
