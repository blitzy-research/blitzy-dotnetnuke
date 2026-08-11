//
// Failure translation for the dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE IS
// ---------------------------------------------------------------------------
// One functional interceptor and three helpers. It unwraps the transport failure,
// reads the RFC 7807 problem document out of it, hands that already-parsed body
// and a plain numeric status to `core/utils/form-errors.util.ts` for every wording
// and severity decision, queues the single resulting sentence through
// `core/services/notification.service.ts`, and RE-THROWS.
//
// The division of labour is deliberate and is stated on both sides of it. The
// utility module declares that it "performs no request and unwraps no transport
// error: the transport layer is `core/interceptors/error.interceptor.ts`, which
// reads the failed response and hands over an ALREADY-PARSED body plus a plain
// numeric status". This file is that layer, and nothing else in the workspace
// reads an `HttpErrorResponse`. Consequently:
//
//   * unwrapping the response, parsing a string body and recognising the
//     no-response condition are done HERE and only here;
//   * choosing the words, resolving break markup, deciding a severity and
//     discriminating a validation document are done THERE and only there.
//
// Nothing is re-implemented across that line. The wording constants this file
// would otherwise need are already exported by the utility, so they are consumed
// rather than copied: a duplicate would let the same condition be described two
// different ways depending on which layer happened to notice it.
//
// ---------------------------------------------------------------------------
// POSITION IN THE CHAIN, AND WHY IT MEANS SILENCE ON 401
// ---------------------------------------------------------------------------
// `withInterceptors([correlationIdInterceptor, authInterceptor, errorInterceptor])`
// composes as `correlationId(next = auth(next = error(next = backend)))`. The
// listed order is the order on the way OUT, so the order on the way BACK is its
// reverse:
//
//   request:  correlationId -> auth -> error -> backend
//   response: backend -> error -> auth -> correlationId
//
// This interceptor is therefore the INNERMOST of the three on the response path.
// It sees the RAW response FIRST, before the authentication interceptor around it
// has had any opportunity to renew a session and retry. A 401 has therefore had
// nothing attempted "upstream" of this point when it arrives here, and the retry
// that the authentication interceptor issues is dispatched through this
// interceptor as well - so both attempts are observed here, not just the last one.
//
// Announcing a 401 from here would consequently be wrong twice over. A session
// about to be renewed without the operator noticing would still produce a "not
// logged in" notification, and a refused sign-in - which is a 401 from the
// credential endpoint that the sign-in screen reads and presents itself - would be
// reported a second time in a place the operator cannot act on. So the entire 401
// lifecycle (detection, the single refresh, the single retry, and discarding the
// session when the refresh fails) belongs to `auth.interceptor.ts`, and this file
// says nothing at all about a 401.
//
// ---------------------------------------------------------------------------
// EVERY MESSAGE IS PLAIN TEXT. THIS IS A SECURITY BOUNDARY, AND IT IS MEASURED.
// ---------------------------------------------------------------------------
// The API relays legacy message wording unaltered, and that wording came from
// resource files in which raw markup is commonplace. Across the 37 in-scope legacy
// resource files under Website/admin/*/App_LocalResources, unescaping the XML
// entities first - a naive search finds nothing, because the markup is stored
// escaped, and would wrongly conclude the risk is absent - shows 76 values
// carrying an HTML tag, four of them SCRIPT tags, one of which is a live
// third-party advertising block in the `Advertising.Text` entry of
// Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx.
//
// So this file composes STRINGS. It produces no pre-escaped value, no
// trusted-markup value and no member whose name suggests either, and the strings
// it produces are bound as text by the framework's default interpolation, which
// escapes by construction. The legacy application reached the same conclusion by
// hand: Website/admin/Security/AccessDenied.ascx.vb:L43 renders its untrusted
// query-string message through `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(..))`
// before display.
//

import { HttpErrorResponse } from '@angular/common/http';
import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

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

/**
 * Shown when no response arrived at all.
 *
 * The only sentence this file authors itself, and it is authored here rather than
 * imported for a reason: a status of zero is a TRANSPORT condition, not a problem
 * document, and `core/utils/form-errors.util.ts` deliberately words statuses
 * rather than transport failures. Its status ladder would describe this case as a
 * rejected request, which is wrong - nothing was rejected, because nothing was
 * received.
 *
 * The framework's own text for the condition is not used. It reads "Http failure
 * response for /api/v1/portals: 0 Unknown Error", which names an internal path and
 * a status of zero and tells an operator nothing they can act on.
 *
 * Authored as a constant rather than written inline at the call site so that a
 * specification can assert the exact wording, and so that punctuation and spacing
 * survive being bound into a template.
 */
const NETWORK_UNAVAILABLE = 'The server could not be reached. Check your connection and try again.';

// MIGRATION: the `Reference:` label that used to be declared here now lives in
// `core/services/notification.service.ts`, beside the bounding it has to survive.
// Deciding WHETHER to quote a reference is a transport-status question and stays in this
// file - see `resolveReference` - but composing the label into the displayed sentence is
// the queue's job, because the queue is what applies the length bound and only the party
// applying the bound can guarantee the suffix outlives it. The wording is unchanged, so
// no rendered message differs; the label is simply declared once, where it is used.

/**
 * The statuses at which the server is REFUSING rather than FAILING.
 *
 * Used for one decision only - whether to quote the trace identifier - and it is a
 * different question from wording or severity, which is why it is answered here
 * rather than delegated. A refusal is self-explanatory to the operator who
 * provoked it: a duplicate name, a missing record, a permission they do not hold,
 * a rate limit they have reached. Appending a diagnostic identifier to those would
 * add noise to a message the operator can already act on, and would invite them to
 * report a working system as broken.
 *
 * Anything NOT listed here is a fault or an unanticipated status - every 5xx, and
 * any status this application did not expect - and for those the identifier is the
 * only join key between what the operator saw and the request as the server
 * recorded it, so it is quoted.
 *
 * 401 is absent because it never reaches the decision: it returns earlier, unsaid.
 *
 * Typed `readonly number[]` rather than a literal tuple on purpose. A frozen tuple
 * of literal types would narrow `includes` to accept only those six literals and
 * would reject the plain `number` a server actually sends, which is strict typing
 * producing exactly the unsafety it exists to remove.
 */
const REFUSAL_STATUSES: readonly number[] = Object.freeze([400, 403, 404, 409, 422, 429]);

// ---------------------------------------------------------------------------
// THE INTERCEPTOR
// ---------------------------------------------------------------------------

/**
 * Translates a failed response into one readable sentence, reports it, and
 * re-throws.
 *
 * Third and innermost of the three interceptors on the response path - see the
 * file header, which explains why that position is what makes silence on 401
 * correct rather than an omission.
 *
 * The error is ALWAYS re-thrown. This interceptor reports; it does not decide the
 * outcome of the caller's operation. A feature still needs to know its request
 * failed so it can leave its form open, keep the row it failed to delete, restore
 * the value it failed to save, and bind the problem document into the shared
 * error-banner component, which takes the whole document and renders its per-field
 * messages. Converting a failure into a successful empty response would make every
 * caller appear to succeed.
 *
 * Two classes of failure are deliberately NOT announced:
 *
 * - a validation failure carrying per-field messages, because those belong beside
 *   the fields they describe, which is what the shared form-field and error-banner
 *   components render. Announcing them here as well would say the same thing
 *   twice, once in a place the operator cannot act on;
 * - a 401, for the reasons set out in the file header.
 *
 * MIGRATION: the legacy application DID record failures - it recorded them to the
 * DotNetNuke event log, and abundantly. Measured in this repository: 20
 * `LogException` call sites across the five in-scope `Library/Components` domain
 * trees (Portal 2, Modules 5, Users 2, Security 9, Tabs 2), and 112
 * `LogException` / `ProcessModuleLoadException` occurrences spread over 28 of the
 * 39 in-scope administration code-behinds, with the page-level analogue at
 * Website/Default.aspx.vb:L227-L235 catching the exception, wrapping it in a
 * `PageLoadException("Unhandled error loading page.", exc)` and logging that. What
 * never existed is a MACHINE-READABLE error envelope: every one of those sites
 * recorded the failure server-side and rendered a skin module message into the
 * page, so nothing crossed the wire that a client could branch on. This
 * translation path is therefore net-new AS A WIRE CONTRACT - not because the
 * legacy application had no predecessor for reporting a failure.
 *
 * MIGRATION: every sentence shown here is PLAIN TEXT, composed as a string, and
 * that includes resolving the break markup the legacy wording carries. The
 * fragments are real and they accumulate: Website/admin/Portal/Signup.ascx.vb
 * appends `"<br>"` inside a per-character validation loop at L193, L214 and L221
 * and then wraps the whole result at L323 as `"<br>" & strMessage & "<br><br>"`;
 * Website/admin/Users/User.ascx.vb:L187 uses the self-closing `"<br/>"` spelling;
 * Website/admin/Modules/Export.ascx.vb:L189 is a third accumulation site.
 * Resolving them is delegated to `stripLegacyBreakTags`, which
 * `summarizeProblem` applies to every value it returns, so no expression in this
 * file inspects message text and no markup parser is involved anywhere.
 *
 * MIGRATION: a rate-limit refusal is HANDLED but NEVER retried, and it exists in
 * this application only because the legacy verification control was deliberately
 * dropped. Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L162
 * gated every credential submission on
 * `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha) Then`, and that
 * was the only anti-automation control anywhere in the legacy tree - a search for
 * rate limiting or throttling across the five in-scope domain trees and all of the
 * in-scope administration pages matches nothing at all. The compensating control is
 * the API's address-partitioned limiter on the credential endpoints, admitting a
 * fixed permit count per window and refusing with 429; there is deliberately no
 * global limiter, so the health endpoint is never throttled. No automatic
 * re-attempt of any kind is made here - not for a rate-limit refusal, not for a
 * fault, and not for a transport failure. Re-attempting a refusal whose entire
 * purpose is to slow an automated caller down would defeat the control that
 * replaced the verification field and would spend the operator's remaining permits
 * on their behalf. The wording is correspondingly calm: nothing has failed, the
 * caller is simply early.
 *
 * MIGRATION: localisation is deliberately NOT ported. No translation runtime is
 * introduced and no message is looked up at run time; every sentence reachable
 * from here is English text owned by `core/utils/form-errors.util.ts`, authored
 * with the legacy resource files as a reference only. One path caveat, because it
 * is easy to get wrong: the authoritative legacy wording for the credential flow
 * lives at Website/admin/Authentication/App_LocalResources/Login.ascx.resx, NOT
 * under Website/DesktopModules/AuthenticationServices/DNN/App_LocalResources/,
 * which carries only the control captions.
 *
 * MIGRATION: nothing is cached here and no failure is remembered between requests.
 * The legacy equivalent cached aggressively - 120 `DataCache` member references
 * across the five in-scope domain trees, against the 317-line
 * Library/Components/Providers/Caching/DataCache.vb - and that concern is
 * deliberately not reproduced client-side; it belongs to the API's own cache
 * service. The migration plan cites that file under Library/Components/Shared/,
 * where no such file exists; the path above is the real one.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  // Injected inside the interceptor rather than captured at module load, because an
  // `HttpInterceptorFn` runs in the injection context of the request. It is also
  // what keeps this module free of state and therefore safe under parallel tests.
  const notifications = inject(NotificationService);

  // MIGRATION: THIS INTERCEPTOR IS NOW THE FALLBACK ANNOUNCER, NOT THE UNIVERSAL ONE,
  //   and that is the whole of the change. It used to announce EVERY failed response,
  //   while the root signal stores simultaneously retained the same failure on their own
  //   `failure`/`problem` slices for a screen to bind to `error-banner`. One refusal
  //   therefore produced two independently-worded, independently-dismissible reports of
  //   a single incident, and no operator could tell whether they were looking at one
  //   problem or two. The legacy surface it replaces had exactly one message channel -
  //   `AddModuleMessage` rendered a single module message per page render - so two
  //   publishers preserves nothing.
  //
  // The marker is read from the request CONTEXT rather than inferred from the URL or the
  // caller, because only the caller knows whether it presents its own failures. Reading
  // it here, once, before anything else happens, is what makes the arbitration rule
  // stated on `PRESENTED_IN_CONTEXT` a single decision in a single place. The default is
  // "not presented", so a caller that forgets the marker is announced twice rather than
  // not at all.
  const presentedByCaller: boolean = req.context.get(PRESENTED_IN_CONTEXT);

  return next(req).pipe(
    catchError((error: unknown) => {
      // Narrowed rather than assumed. A failure reaching an interceptor is not
      // necessarily an `HttpErrorResponse` - an operator or an inner interceptor can
      // throw anything at all - and reading `.status` off such a value would yield
      // `undefined` and silently compare unequal to every branch below.
      if (error instanceof HttpErrorResponse && !presentedByCaller) {
        announce(notifications, error);
      }

      // The single exit, taken on every path, and it is UNCONDITIONAL. Suppressing the
      // announcement changes who tells the operator; it never changes what the caller
      // hears, so a store still receives the failure it is expected to present.
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

  // MIGRATION: silence on 401 is a correction to the migration plan, which has this
  // interceptor observing the final response after a refresh-and-retry has already
  // been attempted. Under the framework's actual chain semantics that is impossible:
  // the listed order is reversed on the way back, so this interceptor is INNERMOST
  // and sees the raw 401 before the authentication interceptor around it can attempt
  // anything - and it sees the retry too. The interceptor array is not reordered to
  // make the plan's description true, because the stated order is load-bearing for
  // the correlation identifier; ownership of the 401 lifecycle is assigned to
  // `auth.interceptor.ts` instead, and this branch is the whole of the consequence.
  if (status === 401) {
    return;
  }

  // MIGRATION: severity is DERIVED, by the one function in the workspace that makes
  // the decision, and two of its outcomes differ from the summary table in the
  // migration plan. The legacy source is why. The plan presents a conflict and an
  // unprocessable entity as warnings; in the legacy administration pages every
  // conflict message is surfaced through `RedError` - six sites, at
  // Security/EditGroups.ascx.vb:L117, Security/EditRoles.ascx.vb:L256,
  // Portal/EditPortalAlias.ascx.vb:L226 and L238, and Tabs/ManageTabs.ascx.vb:L273
  // and L283 - against a message-type histogram across those same pages of
  // `RedError` 27, `YellowWarning` 21, `GreenSuccess` 12. The legacy application
  // genuinely distinguished a refusal from a fault, and so does this. Encoding that
  // distinction a second time here is exactly what a duplicated rule would be, so
  // the derivation is delegated. The one severity the migration plan makes
  // mandatory - a permission refusal as a warning, never an error - is satisfied
  // by that same function.
  //
  // MIGRATION: that mandatory case is measured, not asserted.
  // Website/admin/Security/AccessDenied.ascx.vb is 50 lines; its `Page_Load`
  // (L41-L47) performs no permission check at all - it only PRESENTS a denial - and
  // both of its branches pass `ModuleMessage.ModuleMessageType.YellowWarning`: L43
  // for the message supplied through the query string, L45 for the localised
  // default. Presenting a refusal in danger styling would tell an operator that
  // something is broken when the system is working exactly as configured.
  //
  // Derived from the TRANSPORT status rather than from the status repeated in the
  // body. The two agree for every response this API produces, but a body written by
  // a proxy rather than by the API carries no status at all, and the transport
  // status is the one that is always present.
  const severity: NotificationSeverity = problemSeverity(status);

  // A status of zero means the response never arrived: the network is unavailable,
  // the request was blocked, or it was aborted. Resolved BEFORE the body is read,
  // and that ordering is load-bearing rather than tidy. The framework puts a DOM
  // `ProgressEvent` in the body slot for this condition, and a `ProgressEvent`
  // carries a string `type` member, which is one of the members the deliberately
  // permissive `isProblemDetails` accepts - so reading the body first would mistake
  // a transport failure for a problem document that happens to say nothing.
  if (status === 0) {
    // No response arrived, so there is no server-side reference to quote.
    notifications.notify(severity, NETWORK_UNAVAILABLE, null);

    return;
  }

  const problem: ProblemDetails | null = readProblem(error);
  const summary: ProblemSummary = summarizeProblem(problem, statusMessage(status));

  // Per-field messages are already being rendered against the fields, so there is
  // nothing to announce. Both halves of the test are needed and they answer
  // different questions: the guard asks whether a per-field dictionary is present at
  // all - an empty `errors` object is emitted whenever model state carries no
  // entries, and it passes - while `hasFieldMessages` asks whether anything
  // renderable was actually reported, against either a field or the request as a
  // whole. A document with `errors: {}` and a usable `detail` is therefore
  // announced, which is correct: no form control is showing it.
  //
  // Note that the status is NOT the marker. The API answers 400 both for a
  // malformed request and for a refused domain operation, so a status test would
  // silence a refusal that no field is displaying.
  if (isValidationProblemDetails(problem) && summary.hasFieldMessages) {
    return;
  }

  // The reference is handed over as its OWN argument rather than concatenated into the
  // sentence. See `resolveReference` for why that matters.
  notifications.notify(
    severity,
    summary.message,
    resolveReference(summary.supportReference, status),
  );

  // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, because a failure announced here is frequently the
  // reason a navigation is about to happen. An expired session, a refused destination and a
  // read that fails during a route resolution all land on this line and are then followed by a
  // redirect, and the shell discards stale notifications on a completed navigation — so without
  // the exemption the operator would be moved with the explanation already swept away. It lasts
  // for exactly one navigation, so a failure announced while the caller stays put is still
  // retired the next time they genuinely go somewhere.
  notifications.retainAcrossNavigation();
}

// ---------------------------------------------------------------------------
// BODY UNWRAPPING
// ---------------------------------------------------------------------------

/**
 * Reads the problem document out of a failed response, or null when there is none.
 *
 * Three body shapes are handled, because all three occur. An object is the normal
 * case: the API answers with a problem document and the framework parses a JSON
 * response on the caller's behalf. A string arrives when the caller asked for text,
 * or when the failure was produced by the reverse proxy rather than by the API - a
 * gateway timeout answering with an HTML page, for instance - so it is parsed
 * defensively, since a proxy is free to return anything at all. Null arrives when
 * there was no body.
 *
 * A parse failure yields null rather than propagating: replacing the caller's
 * failure with a syntax error would report the wrong problem entirely, and the
 * status-derived wording describes an HTML error page better than any part of that
 * page would.
 *
 * @param error The failed response.
 * @returns The problem document, or null when the body is not one.
 */
function readProblem(error: HttpErrorResponse): ProblemDetails | null {
  // Read into `unknown` rather than used directly. The framework types this member
  // loosely, and widening it at the boundary is what forces every read below to be
  // guarded.
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
 * Decides whether the server's trace identifier should be quoted, and normalises it.
 *
 * The identifier is the only join key between what an operator saw in the browser
 * and the request as the server recorded it. The client's own correlation header
 * round-trips into it server-side, and the successful-response envelope
 * deliberately carries no such member, so a problem document is the only place it
 * ever appears.
 *
 * MIGRATION: THIS FUNCTION USED TO CONCATENATE THE REFERENCE ONTO THE MESSAGE, AND THAT
 *   IS WHY THE REFERENCE COULD DISAPPEAR. The composed string was then bounded by the
 *   notification queue at its maximum message length, and bounding removes a SUFFIX — so
 *   on precisely the failures whose server `detail` is long, which are the unexpected
 *   ones worth reporting, the identifier was the first thing discarded. An operator was
 *   left with an unbounded server sentence and nothing to look it up by. The reference is
 *   now returned on its own and passed to the queue as a separate argument, which bounds
 *   the message and the reference independently and appends the reference afterwards. The
 *   rendered wording for every message short enough to survive the bound — which is every
 *   message this application composes itself — is byte-for-byte what it was.
 *
 * MIGRATION: the identifier is normalised through `stripLegacyBreakTags` before it
 * is quoted, even though it cannot legitimately contain markup. Every other
 * server-supplied fragment this file puts in front of an operator has passed that
 * normaliser - `summarizeProblem` applies it to each value it returns - whereas
 * `problemSupportReference`, which produced this value, only trims. Normalising here is what
 * makes "no server-supplied text reaches a notification unnormalised" true without
 * an exception, and an exception is what a later reader would have to rediscover.
 *
 * A value that normalises to nothing is reported as absent rather than as an empty
 * reference, so no `Reference:` label is ever left with nothing after it.
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
