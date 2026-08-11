import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';

// Type-only, and deliberately so. `HttpErrorResponse` above is a VALUE import
// because the 401 test is an `instanceof` check against the concrete class, whereas
// these two are erased at compile time and must not emit a runtime import.
import type { HttpInterceptorFn, HttpRequest } from '@angular/common/http';

import { isAnonymousAuthEndpoint, isApiRequest } from '../config/api-endpoints';
import { RETURN_URL_QUERY_KEY, SIGN_IN_ROUTE } from '../config/app-routes.config';
import { NotificationService } from '../services/notification.service';
import { TokenStorageService } from '../services/token-storage.service';
// MIGRATION: the renewal used to be reached through `core/services/auth.service`, which owned the
//   in-flight slot, the two-request composition and custody of the stored session. That service is
//   now a typed transport closed at four operations and holds nothing, so the renewal is reached
//   through the session's owner instead. Nothing else about this file's relationship to it changes:
//   it asks for a renewal and is told the outcome, and every recovery decision below is still made
//   here against the custodian.
import { AuthStore } from '../state/auth.store';
import { SessionTeardownService } from '../state/session-teardown.service';

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
 * What an operator is told when their session ended without them asking.
 *
 * MIGRATION: AUTHORED BECAUSE THE LEGACY HAD NOTHING TO PORT. A search of the authentication and
 * security resource files for session, sign-in, logon and expiry wording returns no such string:
 * the legacy portal relied on ASP.NET Forms Authentication, whose expiry redirected the browser to
 * the login page as a plain HTTP response, so the operator's evidence that anything had happened
 * was the login page arriving in place of the page they asked for. A single-page application has no
 * equivalent - the shell never reloads, so an unannounced ejection is indistinguishable from an
 * ordinary in-app navigation - which is why the statement has to exist here even though nothing
 * corresponds to it upstream.
 *
 * Worded as a fact and a next step, with no apology and no diagnostic: an expired session is the
 * expected end of a session, not a fault. It deliberately does NOT promise that unsaved work was
 * kept, because it was not - the screen is torn down with the session. What the ejection preserves
 * is the ADDRESS, so signing in again returns the operator to the screen they were on rather than to
 * the default landing page, and they can see for themselves what did and did not persist. Claiming
 * more than that would be the more damaging failure, since it would stop them checking.
 */
const SESSION_ENDED_MESSAGE = 'Your session has ended. Please sign in again to continue.';

/*
 * Where an operator is sent once a session cannot be renewed: `SIGN_IN_ROUTE`, IMPORTED from
 * `core/config/app-routes.config.ts` at the head of this file.
 *
 * A route rather than a full reload, so the single-page application is not started again from
 * scratch for what is an ordinary end of session.
 *
 * `APP_ROUTES` declares that path and mounts the sign-in feature behind it UNGATED, so the
 * navigation resolves to the sign-in screen rather than to the catch-all. It must stay
 * unguarded: guarding the one destination an expired session is sent to would bounce that
 * session between the guard and this interceptor. Nothing else in this file depends on the
 * destination beyond its resolving.
 *
 * MIGRATION: this was a private `LOGIN_PATH` copy, one of four across the workspace. Both
 * route gates and the root component held the others, and the value is only correct if it
 * matches an ungated entry in the route table - a mismatch resolves to the catch-all with no
 * diagnostic anywhere, so an expired session would land on the not-found view. The four are
 * now one constant in a module that imports nothing, which is what removes the drift without
 * coupling this transport concern to a navigation gate.
 */

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
  const authStore = inject(AuthStore);
  const router = inject(Router);
  const sessionTeardown = inject(SessionTeardownService);
  const notifications = inject(NotificationService);

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

  /*
   * ⚠ THE SESSION THIS REQUEST BELONGS TO IS CAPTURED HERE, ALONGSIDE ITS TOKEN, AND EVERY
   * RECOVERY DECISION BELOW IS CONDITIONED ON IT.
   *
   * This is the fix for a cross-session replay that the previous structure allowed. The old
   * recovery path asked only "is SOME renewal credential held?" and then retried the original
   * request with whatever token storage held by then. Consider an operator who signs out and
   * signs back in as somebody else while a request is in the air:
   *
   *   1. account A's request goes out carrying A's access token;
   *   2. the operator signs out, then signs in as account B;
   *   3. A's request comes back 401, because A's token was expired or A's session was ended;
   *   4. the presence test passes — B holds a refresh token — so the renewal proceeds and
   *      succeeds, renewing B's perfectly healthy session;
   *   5. the original request, composed under A's authority, is retried carrying B's bearer
   *      token, and the server executes it AS B.
   *
   * Step 5 is the defect: an operation one account initiated is performed under another's
   * identity. On a mutating request against a record A could reach and B could not, or the
   * reverse, it is a genuine authorisation crossing rather than a cosmetic confusion.
   *
   * Recovery is now gated at BOTH points where the old code trusted ambient state, and the two
   * gates ask the question in the two different forms it needs to be asked in:
   *
   *   - BEFORE renewing, against this captured epoch — "has the session changed at all since
   *     this request was composed?" Nothing in this path has run yet, so a plain equality
   *     test is exact.
   *   - BEFORE retrying, against TOKEN IDENTITY — "is the session the renewal produced
   *     actually the one being held?" An epoch comparison would have to predict how many
   *     transitions the renewal itself caused, which hard-codes another file's internals;
   *     identity answers it directly. See the note at that gate.
   */
  const requestGeneration = tokenStorage.generation();

  // The RECOVERY handler is attached to the FIRST attempt only. The retry below carries a
  // handler of its own, but a CLOSED one: it neither renews nor re-sends, so it cannot
  // re-enter this recovery path. That structure — not a counter and not a marker header — is
  // what bounds recovery to exactly one retry.
  return next(withBearerToken(req, token)).pipe(
    catchError((error: unknown) => {
      // Only an expired or rejected token is recoverable. A 403 means the server knows
      // who the caller is and is refusing the operation, so renewing would change
      // nothing; anything else is not an authentication condition at all.
      if (!isUnauthorized(error)) {
        return throwError(() => error);
      }

      /*
       * ⚠ CHECK ONE: HAS THE SESSION CHANGED SINCE THIS REQUEST WAS COMPOSED?
       *
       * If it has, this 401 belongs to a session that is already over and there is nothing
       * here to recover. Crucially, the response is to do NOTHING to the current session —
       * not renew it, not clear it, not navigate away from it. Some other party made that
       * transition deliberately: a sign-out the operator asked for, or a sign-in that
       * succeeded. Acting on this stale refusal would undo their action.
       *
       * The original 401 is propagated so the caller still learns its request failed, which
       * is true and is what the error surface should report.
       *
       * This test comes BEFORE the renewable-session test, and the order matters. The old
       * code's first question was "is a refresh token held?", which a NEW session answers
       * yes to — so it would proceed to renew a session that had no problem, on the strength
       * of an old session's failure.
       */
      if (!tokenStorage.isCurrentGeneration(requestGeneration)) {
        return throwError(() => error);
      }

      // A session discarded while this request was in flight — by a concurrent
      // renewal failure, or by a sign-out that raced it — cannot be renewed. The
      // session is ended explicitly so the operator is asked to sign in rather than
      // left looking at a screen that will refuse every subsequent action.
      //
      // Reached only when the generation still matches, so "discarded" here means
      // discarded WITHOUT a session replacing it. That is why ending the session and
      // navigating is the right response at this point and would have been wrong above.
      if (!hasRenewableSession(tokenStorage)) {
        endSession(tokenStorage, router, sessionTeardown, notifications);

        return throwError(() => error);
      }

      // The single-flight guarantee lives in the SESSION'S OWNER, which coalesces concurrent
      // callers onto one request and commits the rotated pair once, conditioned on the session
      // not having moved on. A second in-flight slot here would be a second source of truth for
      // the same fact: signing out abandons the owner's slot AND advances its session
      // generation, whereas a private slot here could be advanced by neither — so a 401 racing
      // a sign-out would replay a cached renewal and resurrect the session the operator had just
      // ended. One owner, deliberately, and this file holds no state of any kind.
      //
      // ⚠ THE PRIMITIVE IS CALLED, NOT THE OWNER'S DELIBERATE RENEWAL COMMAND, and the
      // distinction is load-bearing rather than incidental. That command discards the session
      // and records the failure on every refusal, which is right for a caller that ASKED to
      // renew and is about to be sent to sign in. It is wrong here in both directions: the three
      // branches below make the terminal decision under conditions this file owns — an older
      // renewal's refusal must leave a NEWER session completely alone — and a failure recorded
      // for a renewal the operator never asked for would put a stale message in front of
      // somebody whose own next action succeeded.
      // ⚠ THE ORDER OF THESE TWO OPERATORS IS THE WHOLE POINT, AND REVERSING IT IS A DEFECT.
      // `catchError` is attached to the RENEWAL, BEFORE the `switchMap` that retries - so it
      // sees a renewal failure and nothing else.
      //
      // MIGRATION: it used to sit AFTER the `switchMap`, where it caught the RETRY's failure as
      //   though the renewal had failed. Two consequences, both serious. First, a renewal that
      //   SUCCEEDED followed by a retry the server answered 403, 404, 409, 429, 500 or a network
      //   failure ended the session and sent the operator to sign in again - destroying a session
      //   that had just been renewed and was perfectly valid, for a request that had nothing to do
      //   with authentication. Second, every one of those statuses was replaced by the original 401
      //   on the way out, so the caller was told "not authorised" about a conflict, a missing record
      //   or a server fault, and the real status never reached the error interceptor that words it.
      //   Catching before the retry restores both: only a genuine renewal failure is terminal, and a
      //   retry failure propagates exactly as the server sent it.
      return authStore.renewSession().pipe(
        catchError((renewalError: unknown) => {
          // Terminal. The renewal credential the server refused cannot be retried, so the
          // session is over and the operator is routed to sign in again.
          //
          // The ORIGINAL 401 is re-thrown rather than the renewal failure, because that is the
          // failure the caller asked about; reporting the renewal error would replace "your
          // request was not authorised" with an unrelated message about a token the caller never
          // sent. The renewal error is deliberately not logged either - it can carry the
          // credential that was refused.
          void renewalError;

          /*
           * ⚠ TEARDOWN IS CONDITIONED ON NO SESSION BEING HELD, which is the one predicate that
           * reads correctly for every way this branch is reachable:
           *
           *   - THE RENEWAL WAS REFUSED for this session. The session's owner has already
           *     discarded it, so nothing is held and the operator is asked to sign in.
           *   - THE LINEAGE MOVED ON BECAUSE OF A SIGN-OUT. Nothing is held, and navigating to
           *     sign-in is what the operator asked for anyway.
           *   - THE LINEAGE MOVED ON BECAUSE OF A NEWER SIGN-IN. A healthy session IS held, and it
           *     is left completely alone. Tearing it down here would sign out an operator whose own
           *     sign-in had just succeeded.
           */
          if (tokenStorage.isAuthenticated()) {
            return throwError(() => error);
          }

          endSession(tokenStorage, router, sessionTeardown, notifications);

          return throwError(() => error);
        }),
        // Exactly one retry, and only after a renewal that actually succeeded. The request is
        // re-cloned from the ORIGINAL so the correlation identifier stamped by the outer
        // interceptor is carried onto the second attempt. Every refusal of that retry propagates
        // with the status the server actually sent, and the handler attached to it is CLOSED - it
        // issues no request and asks for no renewal - so the retry cannot re-enter this recovery
        // path. That structure, not a counter and not a marker header, is what bounds recovery to
        // exactly one retry.
        switchMap((session) => {
          /*
           * ⚠ CHECK TWO: IS THE RENEWED SESSION *ACTUALLY THE ONE BEING HELD*?
           *
           * A renewal is two round trips, so the window between CHECK ONE and this point is the
           * widest in the whole path - and a sign-out or an account switch landing inside it is
           * precisely the race being closed.
           *
           * The test is TOKEN IDENTITY rather than epoch arithmetic, deliberately. Counting
           * transitions here would mean predicting how many the renewal itself caused, which
           * hard-codes an internal detail of the session's owner into this file. Comparing
           * the held token against the token this renewal produced needs no prediction and is
           * exhaustive over the ways the lineage can move on: the renewal's store was SUPPRESSED
           * because a newer transition had advanced the epoch past the one it captured, or the
           * store succeeded and a FURTHER transition replaced it immediately after. Either way, do
           * not retry, do not touch the session that is now current, and propagate the ORIGINAL
           * 401 - retrying here is what would execute one account's request under another's
           * identity.
           */
          if (tokenStorage.accessToken() !== session.accessToken) {
            return throwError(() => error);
          }

          return next(withBearerToken(req, session.accessToken)).pipe(
            catchError((retryError: unknown) => {
              /*
               * ⚠ CHECK THREE: THE RETRY WAS REFUSED *WITH A FRESHLY RENEWED CREDENTIAL*.
               *
               * MIGRATION: the retry used to be returned with no handler at all, and the gap that
               *   left was the defect this closes. A 401 answering a request that carried a token
               *   issued MOMENTS earlier propagated to the caller while the rotated session stayed
               *   fully installed — so the custodian went on reporting an authenticated session,
               *   the shell went on rendering the account, the route gates went on admitting
               *   navigations, and every subsequent request presented the same rejected credential
               *   and was refused in turn. The operator was left on an administration screen where
               *   nothing worked and nothing explained why, with no path back to signing in except
               *   reloading the application by hand. Renewal is the only recovery this file has, and
               *   it has already been spent: a credential the server refuses immediately after
               *   issuing it cannot be repaired by asking for another one.
               *
               * ONLY A 401 IS TERMINAL, and that distinction is the whole reason this handler is
               * status-specific rather than a catch-all. A 403 means the server knows exactly who
               * the caller is and is refusing the OPERATION; a 404, a 409, a 422, a 429, a 500 or a
               * dropped connection say nothing about the credential at all. Ending the session for
               * any of those would destroy a perfectly valid session because one request failed for
               * an unrelated reason — which is the same defect, in the same place, that moving the
               * renewal handler ahead of this retry already fixed once. Every non-401 refusal is
               * therefore re-thrown exactly as the server sent it.
               */
              if (!isUnauthorized(retryError)) {
                return throwError(() => retryError);
              }

              /*
               * ⚠ AND CONDITIONED, EXACTLY AS CHECK TWO WAS, ON TOKEN IDENTITY.
               *
               * The retry is a further round trip, so a sign-out or an account switch can land
               * while it is in the air. The predicate is deliberately the same one asked above,
               * for the same reason: it is exhaustive over the ways the lineage can move on, and
               * it needs no prediction of how many transitions anything else caused.
               *
               *   - THE SESSION IS STILL THE ONE THAT WAS RETRIED. Terminal: the credential this
               *     request presented is the credential being held, and the server has just
               *     refused it. The operator is asked to sign in again.
               *   - THE OPERATOR SIGNED OUT MEANWHILE. Nothing is held, and the sign-out path has
               *     already torn the session down and navigated. Tearing down again would be
               *     harmless but the navigation could contend with the one already under way, and
               *     the operator is going where they asked to go regardless.
               *   - A NEWER SIGN-IN REPLACED IT. A healthy session IS held and is left completely
               *     alone. Ending it here would sign out an operator whose own sign-in had just
               *     succeeded, on the strength of a refusal belonging to a session that is over.
               */
              if (tokenStorage.accessToken() !== session.accessToken) {
                return throwError(() => retryError);
              }

              endSession(tokenStorage, router, sessionTeardown, notifications);

              /*
               * THE RETRY'S OWN 401 is re-thrown, not the original. Both are 401s, but this one is
               * the server's answer to the request that actually carried the renewed credential,
               * so its problem document is the one that describes what happened. Re-thrown rather
               * than swallowed because the caller asked for something and did not get it, and the
               * error interceptor downstream is what words that for a screen.
               */
              return throwError(() => retryError);
            }),
          );
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
 * ⚠ THE PURGE IS NOT OPTIONAL HERE, and this is the likeliest place a session actually
 * ends. A deliberate sign-out goes through `core/state/auth.store.ts`, but an expired
 * token whose renewal cannot be completed ends the session from THIS function instead,
 * with no screen involved. Clearing the custodian revokes the session's authority and,
 * because the identity projection is stamped with the session generation the custodian
 * advances, it also retracts the published account. It does NOT empty the domain stores:
 * the portal, user, role and module stores are each root-provided, so each holds one
 * instance that survives the session, and without this call the previous operator's tenant
 * listings, the account record they had open, the role assignments naming other accounts
 * and a serialised export of a module's data would all still be in memory behind the
 * sign-in screen — and legible to whoever signed in next on the same page load.
 *
 * A failed navigation is swallowed and reported as "did not navigate". The caller is
 * mid-way through re-throwing the response the server actually sent, and a routing
 * problem must not displace it — nor become an unhandled rejection that surfaces
 * somewhere unrelated. Nothing is written to a log sink here, because the values in
 * scope at this point include the credential that was just refused.
 *
 * @param tokenStorage The session store to clear.
 * @param router The router to leave through.
 * @param sessionTeardown The fan-out that empties the domain stores AND explains the ending.
 */
function endSession(
  tokenStorage: TokenStorageService,
  router: Router,
  sessionTeardown: SessionTeardownService,
  notifications: NotificationService,
): void {
  // Cleared FIRST, because clearing advances the session generation that every late
  // callback tests itself against. Purging before the generation moved would leave a read
  // already in flight still believing its session was current, free to repopulate the very
  // slices the purge had just emptied.
  tokenStorage.clear();

  // ⚠ THE REASON IS RECORDED, AND IT IS NOT `signedOut`. This path is reached when a renewal
  // could not recover a refusal, so the session ended WITHOUT the operator asking — and the
  // one owner of the boundary publishes which boundary was crossed so a consumer, a
  // specification most of all, can tell the two apart. It does not change what is discarded.
  //
  // ⚠ AND IT IS WHAT MAKES THE OPERATOR TOLD WHY, WHICH IS WHY NOTHING IS RAISED HERE. Measured in
  // a browser: the teardown was complete and correct, and both live regions were EMPTY, so somebody
  // mid-task was returned to the sign-in screen with no account, no work and no explanation. This
  // file briefly carried the announcement itself, and that was the wrong home for it: a SECOND path
  // ends a session un-asked-for — the navigation gate's renewal, refused in
  // `core/state/auth.store.ts` — and it reached the operator with the renewal's own problem document
  // instead, wording an ended session as "The refresh token is not valid.". One sentence raised from
  // two files could not fix the second path and would have drifted from it.
  //
  // The announcement therefore lives at the one point BOTH paths already pass through, beside the
  // queue-clearing it has to be ordered against: see `SESSION_ENDED_MESSAGE` and `purge` in
  // `core/state/session-teardown.service.ts`. This call is what selects it, so ending a session here
  // explains itself by construction rather than by remembering to.
  sessionTeardown.purge('renewalRefused');

  /*
   * ⚠ SAID IN WORDS, BECAUSE THIS PATH USED TO SAY NOTHING AT ALL. A session ending here ended
   * without the operator asking, so unlike a deliberate sign-out there is nothing on screen to
   * explain why the screen they were working on has been replaced by the sign-in form. Both live
   * regions were measured empty at zero height on every variant of this ejection, and nothing was
   * written to the console either — so a submission that failed this way was indistinguishable
   * from one that succeeded, since a successful create also ends by navigating away.
   *
   * `'warning'` and not `'error'`: the session lapsed, which is ordinary and expected, and the
   * remedy is entirely in the operator's hands. Nothing failed that they need to report.
   *
   * The reprieve argument is REQUIRED here rather than incidental. The statement exists to be read
   * on the sign-in screen, and the navigation below is a change of screen — so without it the
   * surface would discard this message a moment after it was raised, which is the very fault being
   * fixed, merely relocated.
   *
   * ⚠ NOTHING FROM THE REFUSAL IS QUOTED - no status, no body, no header and above all no
   * credential. The note above about not writing to a log sink here applies with equal force to a
   * user-facing surface, and this message is a fixed sentence for that reason.
   */
  notifications.warning(SESSION_ENDED_MESSAGE, true);

  /*
   * ⚠ THE DESTINATION IS PRESERVED, AND IT IS THE SAME CONTRACT THE ROUTE GATES USE. This
   * navigation used to be a bare `navigate([SIGN_IN_ROUTE])`, which produced an asymmetry that
   * favoured the rarer case: a gate-blocked navigation preserved where the operator was heading,
   * while a refused REQUEST - the common event, a token lapsing mid-session - discarded it. Signing
   * in again therefore returned them to the default landing screen rather than to the work they
   * were interrupted in, with nothing to indicate that anything had been lost.
   *
   * The key is imported from the same module the gates import it from, so the writer here and the
   * reader on the sign-in screen cannot drift apart. `router.url` is the address currently held,
   * which is the screen the refused request was issued from.
   *
   * ⚠ `replaceUrl` IS NOT COSMETIC. Pushing would leave the abandoned screen in forward history as
   * an entry that can never be restored - the session that rendered it is gone - so BACK would
   * appear to work and land on a screen that immediately ejects again. Replacing also stops this
   * ejection from manufacturing the "ghost" entries observed for screens that never painted.
   *
   * The guard against re-attaching the sign-in address to itself matters because this function is
   * reachable from several concurrent failures: a second refusal arriving after the first has
   * already ejected must not rewrite the destination to `/login`, which would strand the operator
   * at the sign-in screen with itself as the place to return to.
   */
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
