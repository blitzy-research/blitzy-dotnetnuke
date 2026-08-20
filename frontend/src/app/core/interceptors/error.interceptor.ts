// WHAT THIS FILE IS
// One functional interceptor and three helpers.

import { HttpErrorResponse } from '@angular/common/http';
import type { HttpInterceptorFn, HttpRequest } from '@angular/common/http';
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

/*
 * ⚠ A `REFUSAL_STATUSES` LIST USED TO LIVE HERE, AND ITS REMOVAL IS THE FIX RATHER THAN A TIDY-UP.
 * It held [400, 403, 404, 409, 422, 429] and gated one decision: whether to quote the server's trace
 * identifier. The reasoning was that a refusal is the system working as configured and so needs no
 * diagnostic. That reasoning is wrong in exactly the cases that matter. The failures an operator most
 * often needs help with ARE the refusals - a `404` on a record they were sent a link to, a `403` they
 * believe they should have passed, a `409` whose other party they cannot see - so this withheld the only
 * join key between what they saw and the request as the server recorded it, precisely where it was most
 * needed. Worse, it made the two surfaces disagree: the banner rendered `Reference:` from the same
 * document while the notification beside it showed none. Availability is now the only test - see
 * {@link resolveReference}.
 */

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
        // The OUTBOUND correlation identifier is handed over as well, because it is the only reference that
        // survives a failure where no response arrives at all - see the status-zero branch of `announce`.
        //
        // ⚠ QA-26 - THE REQUEST'S OWN IDENTITY IS THE OPERATION. Two failures of the same method against
        // the same address are two answers to one question, and only the later one is still true, so the
        // newer report supersedes the older rather than stacking beside it. Method and URL together,
        // because a GET and a DELETE of one address are different operations.
        announce(notifications, error, outboundCorrelationId(req), `${req.method} ${req.url}`);
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
 * @param requestCorrelationId The identifier this application put on the outbound request, or null.
 * @param operation The operation this failure is an answer to, so a later answer supersedes it.
 */
function announce(
  notifications: NotificationService,
  error: HttpErrorResponse,
  requestCorrelationId: string | null,
  operation: string,
): void {
  const status: number = error.status;

  if (status === 401) {
    return;
  }

  const severity: NotificationSeverity = problemSeverity(status);

  // A status of zero means the response never arrived: the network is unavailable, the request was blocked,
  // or it was aborted. Resolved BEFORE the body is read, and that ordering is load-bearing rather than
  // tidy.
  if (status === 0) {
    // No response arrived, so there is no server-supplied reference - but the identifier this application
    // generated for the request still exists, and it is the ONLY thing a support request can be searched
    // on for a failure of this kind. If the request did reach the API before the connection broke, the
    // server logged this very value; if it never left the browser, quoting it costs nothing. Passing null
    // here, which is what this branch used to do, discarded the one reference available.
    notifications.notify(severity, NETWORK_UNAVAILABLE, requestCorrelationId, false, null, operation);

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
    resolveReference(summary.supportReference ?? headerCorrelationId(error)),
    false,
    null,
    operation,
  );

  // RETENTION IS ALREADY CALLER-OWNED, WHICH IS WHY REMOVING IT LOSES NOTHING. Every path that actually
  // redirects or ejects asks for it explicitly and at the point it knows a departure is coming: the
  // permission guard in three places, the authentication store, the session-teardown service, and the
  // module-import, portal-settings, role-form and membership-settings screens.
}

/**
 * Reads back the correlation identifier this application put on the OUTBOUND request.
 *
 * ⚠ THIS IS THE ONLY REFERENCE THAT SURVIVES A FAILURE WITH NO RESPONSE. Every other route to a
 * reference reads the answer - the problem document's `correlationId`, or the response header - and a
 * request that was aborted, blocked or sent while the network was down has no answer to read. The value is
 * still validated for canonical shape, so a header injected by a caller cannot put arbitrary text in front
 * of an operator.
 *
 * @param req The outbound request, after the correlation-id interceptor has run.
 * @returns The identifier, or null when the header is absent or not canonical.
 */
function outboundCorrelationId(req: HttpRequest<unknown>): string | null {
  const header: string | null = req.headers.get(CORRELATION_ID_HEADER);

  if (header === null) {
    return null;
  }

  return isCanonicalCorrelationId(header) ? header : null;
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
 * Normalises the server's trace identifier for quoting. The identifier is the only join key between what
 * an operator saw in the browser and the request as the server recorded it.
 *
 * ⚠ THE STATUS NO LONGER PARTICIPATES, WHICH IS THE FIX. This returned null for every status in
 * {@link REFUSAL_STATUSES}, so a `404` on a record an operator had been sent a link to - the single most
 * commonly reported failure there is - produced a notification with nothing to quote, while the banner
 * beside it showed the reference perfectly well from the same document. The two surfaces disagreed about
 * the same failure. Availability is now the only test: if the server supplied an identifier, it is
 * quoted.
 *
 * @param reference The support identifier from the problem document, or null when absent.
 * @returns The normalised identifier, or null when the server supplied none.
 */
function resolveReference(reference: string | null): string | null {
  if (reference === null) {
    return null;
  }

  const quoted = stripLegacyBreakTags(reference);

  return quoted.length > 0 ? quoted : null;
}

