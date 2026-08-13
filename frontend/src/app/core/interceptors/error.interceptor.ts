// WHAT THIS FILE IS
// One functional interceptor and three helpers.

import { HttpErrorResponse } from '@angular/common/http';
import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import {
  CORRELATION_ID_HEADER,
  isCanonicalCorrelationId,
} from './correlation-id.interceptor';
import { isProblemDetails } from '../models/problem-details.model';
import type { ProblemDetails } from '../models/problem-details.model';
import { NotificationService, PRESENTED_IN_CONTEXT } from '../services/notification.service';
import type { NotificationSeverity } from '../services/notification.service';
import {
  isValidationProblemDetails,
  problemSeverity,
  statusMessage,
  stripLegacyBreakTags,
  summarizeProblem,
} from '../utils/form-errors.util';
import type { ProblemSummary } from '../utils/form-errors.util';

// ---------------------------------------------------------------------------
// TRANSPORT-LEVEL WORDING
// ---------------------------------------------------------------------------

/** Shown when no response arrived at all. */
const NETWORK_UNAVAILABLE = 'The server could not be reached. Check your connection and try again.';

/**
 * The statuses at which the server is REFUSING rather than FAILING. Used for one decision only - whether
 * to quote the trace identifier - and it is a different question from wording or severity, which is why
 * it is answered here rather than delegated.
 */
const REFUSAL_STATUSES: readonly number[] = Object.freeze([400, 403, 404, 409, 422, 429]);

// ---------------------------------------------------------------------------
// THE INTERCEPTOR
// ---------------------------------------------------------------------------

export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  // Injected inside the interceptor rather than captured at module load, because an `HttpInterceptorFn`
  // runs in the injection context of the request. It is also what keeps this module free of state and
  // therefore safe under parallel tests.
  const notifications = inject(NotificationService);

  // The marker is read from the request CONTEXT rather than inferred from the URL or the caller, because
  // only the caller knows whether it presents its own failures.
  const presentedByCaller: boolean = req.context.get(PRESENTED_IN_CONTEXT);

  return next(req).pipe(
    catchError((error: unknown) => {
      // Narrowed rather than assumed.
      if (error instanceof HttpErrorResponse && !presentedByCaller) {
        announce(notifications, error);
      }

      return throwError(() => error);
    }),
  );
};

// ---------------------------------------------------------------------------
// ANNOUNCEMENT
// ---------------------------------------------------------------------------

/**
 * Queues at most one notification for a failed response.
 *
 * @param notifications The queue to report through.
 * @param error The failed response.
 */
function announce(notifications: NotificationService, error: HttpErrorResponse): void {
  const status: number = error.status;

  if (status === 401) {
    return;
  }

  const severity: NotificationSeverity = problemSeverity(status);

  // A status of zero means the response never arrived: the network is unavailable, the request was blocked,
  // or it was aborted. Resolved BEFORE the body is read, and that ordering is load-bearing rather than
  // tidy.
  if (status === 0) {
    // No response arrived, so there is no server-side reference to quote.
    notifications.notify(severity, NETWORK_UNAVAILABLE, null);

    return;
  }

  const problem: ProblemDetails | null = readProblem(error);
  const summary: ProblemSummary = summarizeProblem(problem, statusMessage(status));

  if (isValidationProblemDetails(problem) && summary.hasFieldMessages) {
    return;
  }

  notifications.notify(
    severity,
    summary.message,
    resolveReference(summary.supportReference ?? headerCorrelationId(error), status),
  );

  // RETENTION IS ALREADY CALLER-OWNED, WHICH IS WHY REMOVING IT LOSES NOTHING. Every path that actually
  // redirects or ejects asks for it explicitly and at the point it knows a departure is coming: the
  // permission guard in three places, the authentication store, the session-teardown service, and the
  // module-import, portal-settings, role-form and membership-settings screens.
}

/**
 * Reads a canonical correlation identifier from the response headers, or null when there is none. ⚠ THE
 * GATEWAY IS WHY THIS EXISTS. The correlation identifier normally reaches an operator through the problem
 * document's own `correlationId` member, which the API populates - but not every failure response is
 * written by the API. A `502` or `503` produced by the reverse proxy because the API could not be reached
 * carries a problem document the PROXY composed, and a `504` may carry no document at all.
 *
 * @param error The failed response.
 * @returns The identifier, or null when the header is absent or not canonical.
 */
function headerCorrelationId(error: HttpErrorResponse): string | null {
  const header: string | null = error.headers.get(CORRELATION_ID_HEADER);

  if (header === null) {
    return null;
  }

  return isCanonicalCorrelationId(header) ? header : null;
}

// ---------------------------------------------------------------------------
// BODY UNWRAPPING
// ---------------------------------------------------------------------------

/**
 * Reads the problem document out of a failed response, or null when there is none.
 *
 * @param error The failed response.
 * @returns The problem document, or null when the body is not one.
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
    // Not JSON at all. Deliberately swallowed here and only here: the caller's own
    // failure is re-thrown regardless, so nothing is lost.
    return null;
  }
}

// ---------------------------------------------------------------------------
// TRACE IDENTIFIER
// ---------------------------------------------------------------------------

/**
 * Decides whether the server's trace identifier should be quoted, and normalises it. The identifier is
 * the only join key between what an operator saw in the browser and the request as the server recorded
 * it.
 *
 * @param reference The support identifier from the problem document, or null when absent.
 * @param status The transport status of the failed response.
 * @returns The normalised identifier, or null when none should be quoted.
 */
function resolveReference(reference: string | null, status: number): string | null {
  if (reference === null || isRefusal(status)) {
    return null;
  }

  const quoted = stripLegacyBreakTags(reference);

  return quoted.length > 0 ? quoted : null;
}

/**
 * Whether a status means the server refused the request rather than failed it.
 *
 * @param status The transport status of the failed response.
 * @returns True for a refusal, false for a fault or an unanticipated status.
 */
function isRefusal(status: number): boolean {
  return REFUSAL_STATUSES.includes(status);
}
