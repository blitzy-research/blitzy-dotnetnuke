/**
 * The navigation gate that admits a signed-in caller and sends everyone else to the
 * sign-in screen.
 *
 * ONE DECLARATIVE GATE REPLACES SEVEN IMPERATIVE ONES. The legacy application had no
 * route table and therefore no single place to express "you must be signed in to be
 * here". Each administrative page re-asked the question in its own load handler and
 * navigated away by side effect. Across the five in-scope administrative trees there
 * are exactly SEVEN executable occurrences of that pattern:
 *
 *   1. `Website/admin/Portal/SQL.ascx.vb:L63`
 *   2. `Website/admin/Portal/Signup.ascx.vb:L71`
 *   3. `Website/admin/Portal/Portals.ascx.vb:L340`
 *   4. `Website/admin/Users/ManageUsers.ascx.vb:L309`
 *   5. `Website/admin/Security/SecurityRoles.ascx.vb:L323`
 *   6. `Website/admin/Modules/ModuleSettings.ascx.vb:L192`
 *   7. `Website/admin/Tabs/ManageTabs.ascx.vb:L587`
 *
 * The two canonical shapes are worth quoting because they show that the legacy test
 * and this gate ask the same question:
 *
 * ```vbnet
 * ' Website/admin/Users/ManageUsers.ascx.vb:L308-L310
 * If PortalSettings.UserRegistration = PortalRegistrationType.NoRegistration And Request.IsAuthenticated = False Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 *
 * ' Website/admin/Portal/Portals.ascx.vb:L339-L341
 * If Not UserInfo.IsSuperUser Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 * ```
 *
 * A further FOUR textual matches for the same destination exist in these trees and
 * are NOT call sites — they are provenance comments of the form
 * `''' [VMasanas] 9/28/2004 Changed redirect to Access Denied` at `SQL.ascx.vb:L55`,
 * `Portals.ascx.vb:L330`, `SecurityRoles.ascx.vb:L432` and `ManageTabs.ascx.vb:L578`.
 * The distinction is recorded because a census that counted eleven would overstate
 * the behaviour being replaced.
 *
 * ⚠ THIS GATE IS AN AFFORDANCE, NEVER AN ENFORCEMENT POINT. It decides what is worth
 * NAVIGATING to; it does not decide what is PERMITTED. Every protected endpoint is
 * re-authorised server-side against stored state and answers 403 on its own account,
 * so a caller this gate admits may still be refused by the API, and that refusal is
 * the authoritative one. Nothing here is cached, and no permission or role is
 * consulted — asking a finer-grained question belongs to the separate permission
 * gate named by the migration plan, not to this file.
 *
 * ⚠ INVARIANT FOR EVERY FUTURE EDIT OF THE ROUTE TABLE. `app.routes.ts` must attach
 * this gate to feature roots ONLY. It must never be attached to the sign-in route,
 * because a gate that refuses the very screen it redirects to cannot be satisfied by
 * any caller, and it must never be attached to the `**` fallback, because the
 * not-found screen has to remain reachable in order to say that nothing is there.
 * {@link isSignInRoute} below hardens the first half of that invariant so a mistake
 * degrades to "the sign-in screen is reachable" rather than to a redirect cycle.
 *
 * ## Why this gate stays silent, and where the announcements come from instead
 *
 * A review finding recorded that every redirect out of this gate leaves both live regions
 * empty, and asked for a "you have been signed out" statement here. It is deliberately NOT
 * added here, because THIS GATE CANNOT TELL THE TWO CASES APART and the wrong one is worse
 * than silence.
 *
 * Token storage is memory-only by design, and that property is load-bearing rather than
 * incidental — a reload destroys the session precisely because the renewal credential dies
 * with the JavaScript heap, which is what keeps it off disk. The consequence is that an
 * anonymous first visit to a protected address and a reload of an authenticated screen
 * arrive here in states that are IDENTICAL: no session, no reason recorded, a brand-new
 * heap. Announcing "your session has ended" from here would therefore tell a first-time
 * visitor, and anyone who followed a shared link, that something had happened to them that
 * had not — and a false statement about a session is worse than no statement, because it
 * sends them looking for work they never lost.
 *
 * The announcement is instead raised by whichever party actually KNOWS what happened, each
 * of which holds evidence this gate does not:
 *
 *  - `core/interceptors/auth.interceptor.ts` knows a session existed and that its renewal
 *    was refused, because it held the credential and saw the refusal. It announces that the
 *    session ended, and preserves the address so signing in returns the operator to it.
 *  - `core/state/auth.store.ts` knows the operator ASKED to sign out, because it is the
 *    party they asked. It confirms that instead, in different words, and adds a second
 *    statement if the server would not confirm the withdrawal.
 *
 * Both mark their statement to outlive the change of screen, which is what carries it onto
 * the sign-in screen this gate redirects to. So the gate is silent and the operator is not:
 * every redirect that has a knowable cause is explained by the party that knows it, and the
 * one case with no knowable cause says nothing rather than guessing.
 *
 * @see `Website/admin/Security/AccessDenied.ascx.vb` — the legacy destination. Both
 * of its branches present the refusal with `ModuleMessage.ModuleMessageType.YellowWarning`,
 * so a refusal was a WARNING and never a fault. This gate's whole affordance is the
 * redirect — but it must never be changed to escalate a refusal to error severity.
 */

import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn, UrlTree } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import type { Observable } from 'rxjs';

import { RETURN_URL_QUERY_KEY, SIGN_IN_ROUTE } from '../config/app-routes.config';
import { TokenStorageService } from '../services/token-storage.service';
import { AuthStore } from '../state/auth.store';

/*
 * The sign-in destination is IMPORTED from `core/config/app-routes.config.ts` rather than
 * held privately here.
 *
 * MIGRATION: this file, the permission gate, the bearer interceptor and the root component
 * each held a private copy, and the comment that used to stand here argued for the
 * duplication: the interceptor "does not export its copy", so reaching across to it "would
 * couple a navigation gate to a transport concern for the sake of one string". Avoiding
 * that coupling was right; four uncoupled copies of one value was the wrong way to get it.
 * The constants module is neutral — it imports nothing and injects nothing — so importing
 * from it couples this gate to no peer at all, while making the four values one value.
 *
 * The comment also claimed a rename would be caught because the specifications spell the
 * path again. They do, and that is still true of the SPECIFICATIONS; it was never true of
 * the four production copies, which nothing compared. See the constants module for what a
 * mismatch actually does — the navigation resolves to the catch-all, silently.
 */

/**
 * The query parameter key carrying the address the caller was trying to reach.
 *
 * IMPORTED rather than declared, for the same reason the sign-in address above it is: this
 * gate, the permission gate, the bearer interceptor and the sign-in screen all have to name
 * the same key, and three of the four used to hold a private copy of it while the fourth
 * held none. The constants module records what that cost.
 */
const RETURN_URL_KEY = RETURN_URL_QUERY_KEY;

/**
 * The leading path segment of an address, with the query string, the fragment and
 * every empty segment removed.
 *
 * The single place separators are interpreted, which is what lets the attempted
 * address and the sign-in route be reduced by IDENTICAL rules and then compared. That
 * matters more than it appears: deriving the sign-in route's segment by trimming its
 * leading slash instead would silently break the comparison the day the destination
 * became a nested path, because the trimmed form would keep a separator the attempted
 * address had already been split on.
 *
 * Reduction rules:
 *
 *   * The query string is discarded, so `/login?returnUrl=%2Fportals` reduces to
 *     `login`.
 *   * The fragment is discarded, because a serialised router address may carry one.
 *   * Empty segments are dropped, so a leading slash and any doubled separator are
 *     tolerated.
 *
 * Implemented with array destructuring defaults rather than indexed access, so no
 * element is ever read without a value to fall back on and no non-null assertion is
 * required. Deliberately no regular expression: the work is splitting on three fixed
 * separators, and a pattern would add a backtracking surface to a function that runs
 * on every single navigation.
 *
 * @param url The address to reduce, as serialised by the router.
 * @returns The leading path segment, or the empty string when there is none.
 */
function firstPathSegment(url: string): string {
  const [withoutQuery = ''] = url.split('?');
  const [withoutFragment = ''] = withoutQuery.split('#');
  const [segment = ''] = withoutFragment.split('/').filter((part) => part.length > 0);

  return segment;
}

/**
 * Whether an attempted address already lies within the sign-in route.
 *
 * Compares leading segments for EQUALITY rather than testing the address for a
 * prefix, so `/loginx` is correctly NOT the sign-in route — a `startsWith` test would
 * have admitted it and then refused to redirect a genuinely guarded screen.
 *
 * @param url The attempted address, as serialised by the router.
 * @returns True when the address is already within the sign-in route.
 */
function isSignInRoute(url: string): boolean {
  return firstPathSegment(url) === firstPathSegment(SIGN_IN_ROUTE);
}

/**
 * Admits a signed-in caller, and redirects everyone else to the sign-in screen with
 * the address they were trying to reach preserved.
 *
 * A FUNCTION AND NOT A CLASS. The class-based activation contract is deprecated in
 * this generation of the router; a functional gate needs no provider, no decorator
 * and no registration, and resolves its collaborators with {@link inject} at the
 * point the router calls it. That call happens inside an injection context, which is
 * what makes {@link inject} legal here.
 *
 * SYNCHRONOUS ON EVERY PATH BUT ONE, AND THE EXCEPTION IS DELIBERATE. A decision that
 * needs no renewal is returned immediately as `true` or as a redirect, so the ordinary
 * navigation resolves without a microtask and there is no window in which a half-decided
 * gate could be observed. The single asynchronous path is the one where the held access
 * token has DEMONSTRABLY LAPSED: that navigation waits on one bounded renewal, because
 * the alternative — admitting first and renewing behind the screen — is exactly the
 * defect this gate was corrected for, a component mounting against a session that had
 * already ended and discovering it one refused request later. No path issues an ordinary
 * API request, and none retries.
 *
 * THE TWO CONDITIONS, AND WHY BOTH ARE NECESSARY:
 *
 *   1. The store reports a held session. This is the authoritative answer to "is
 *      anybody signed in", and it is deliberately not recomputed here — re-deciding
 *      it would put two answers in the application.
 *   2. The held session actually carries a bearer token. This is not redundant with
 *      the first: the stored session type declares its token as a plain non-nullable
 *      string, the custodian's write path accepts whatever it is handed without
 *      validating it, and the API serialises without eliding empty values — so a
 *      session whose token is the empty string is a REACHABLE state that condition 1
 *      alone would admit. Admitting it would send an empty credential and earn a
 *      refusal on every request, which presents to an operator as an application
 *      that is signed in and yet works for nothing.
 *
 * @param _route The activated route. Unused: this gate asks a question about the
 * session rather than about the target, and the parameter is present only because the
 * address it does read is the second argument. Prefixed so it reads as deliberately
 * unused rather than forgotten.
 * @param state The router state, whose `url` is the full attempted address.
 * @returns `true` to admit, or a redirect to the sign-in route.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const router = inject(Router);
  const authStore = inject(AuthStore);
  const tokenStorage = inject(TokenStorageService);

  /*
   * "Valid" means A SESSION IS HELD **AND ITS ACCESS TOKEN HAS NOT LAPSED**, and the
   * second half is what this gate previously omitted.
   *
   * ⚠ WHY PRESENCE ALONE WAS NOT ENOUGH. The custodian reports the PRESENCE of a session
   * and says so explicitly: an expired access token still yields `true`, because the
   * correct response to expiry is to renew rather than to behave as though nobody is
   * signed in. Admitting on presence alone therefore let a KNOWN-EXPIRED session enter a
   * screen: the component mounted, rendered whatever state was still held, and only then
   * discovered — one refused request later — that the session was over. Every screen
   * beneath this gate consequently had to be correct in a state the gate should never
   * have produced.
   *
   * ⚠ AND WHY THE ANSWER IS NOT SIMPLY TO REDIRECT ON EXPIRY. A lapsed access token with
   * a good renewal credential is a RECOVERABLE session; refusing it would collapse the
   * effective session length to the access token's own lifetime, which is precisely the
   * outcome the short access token plus long renewal window exists to avoid. So expiry
   * is resolved rather than punished: one renewal is attempted, and the navigation is
   * admitted if and only if it succeeds.
   *
   * The renewal is BOUNDED AND SINGLE-FLIGHT because the SESSION'S OWNER owns both
   * properties — it coalesces concurrent callers onto one request, stores the rotated
   * pair exactly once, and discards the session when the server refuses. This gate
   * therefore issues at most one renewal per navigation and never retries: a refusal is
   * terminal here, exactly as it is on the refused-request path. Nothing about renewal is
   * re-implemented in this file.
   *
   * MIGRATION: those properties used to belong to `core/services/auth.service.ts`. They
   *   moved to `core/state/auth.store.ts` when that service was closed to a typed
   *   transport, and this gate reaches them through the same store it already injects, so
   *   nothing in this file changed with them.
   */
  const hasSession = authStore.isAuthenticated();

  /*
   * MIGRATION: the empty string is the legacy encoding for "absent", so presence is
   * tested EXPLICITLY rather than by truthiness. `Library/Components/Shared/Null.vb:L71-L75`
   * returns `""` from `NullString`, which means legacy data cannot distinguish an
   * absent credential from an empty one. A truthiness test would coincidentally
   * behave correctly for the empty string and then silently mis-handle any other
   * falsy value that a future contract change introduced, so the comparison is
   * written out. This is the same discipline the numeric sentinels demand elsewhere,
   * where `Null.vb:L41-L45` returns `-1` from `NullInteger` while `-1` is also a real
   * tenant key — which is why absence is never inferred from a value's magnitude
   * anywhere in this application.
   */
  const accessToken = tokenStorage.accessToken();
  const hasBearerToken = accessToken !== null && accessToken.length > 0;

  if (hasSession && hasBearerToken) {
    /*
     * MIGRATION: admitted, and NOT declared authorised. The legacy sign-in handler
     * concluded at `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L187`
     * with `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`, which
     * treated every outcome except outright failure as a successful sign-in — so a
     * locked-out account and both well-known-default-credential outcomes all signed
     * in. That defect is corrected where the outcome vocabulary is actually
     * interpreted, in `core/state/auth.store.ts`, and is deliberately NOT
     * re-implemented here: this gate reads the conclusion the store reached and adds
     * no interpretation of its own. Holding a token consequently means a session
     * exists, and never that the next request will succeed.
     *
     * MIGRATION: the legacy sign-in was additionally gated on an image-based human
     * verification challenge — `Login.ascx.vb:L162` wrapped the entire handler in
     * `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha) Then`. The
     * control providing it lives in a tree this migration excludes wholesale, so its
     * removal is a deliberate functional reduction rather than an oversight, and the
     * named compensating control is a rate limiter on the credential endpoints
     * partitioned by calling address. That control is why this gate never RE-VALIDATES a
     * live session against the server: doing so on every navigation would consume an
     * operator's own rate-limit budget and lock them out of the application by
     * navigating around it. The one request it can cause is a renewal, and only when the
     * held token has demonstrably lapsed — which is a request the transport layer would
     * otherwise make a moment later anyway.
     */
    if (!tokenStorage.isAccessTokenExpired()) {
      return true;
    }

    /*
     * The sign-in route itself is admitted without renewing, and the ordering is deliberate:
     * this test sits BEFORE the renewal rather than after it. A caller heading to the sign-in
     * screen has no need of a live session — that is what they are there to establish — so
     * renewing on their behalf would spend the renewal credential, and the operator's
     * rate-limit budget, on a navigation whose whole purpose is to replace the session. The
     * loop-prevention branch below makes the same admission for an unauthenticated caller and
     * for the same reason; see its note for the cycle it exists to break.
     */
    if (isSignInRoute(state.url)) {
      return true;
    }

    /*
     * The token has lapsed. Renewal is attempted exactly once, and the navigation waits
     * for the answer rather than proceeding hopefully: admitting first and renewing in the
     * background is what produced the state this branch exists to prevent — a screen
     * mounted against a session that has already ended.
     *
     * ⚠ FAILS CLOSED. The store discards the session as its own first act when a renewal
     * is refused, so by the time the redirect below is built there is nothing left to
     * admit; the caller lands on the sign-in screen with the address they wanted
     * preserved. The refusal itself is deliberately not re-thrown: a gate returns a
     * destination, and an error escaping here would surface as a failed navigation with no
     * screen at all.
     *
     * A renewal with no renewal credential fails immediately inside the service rather
     * than posting an empty one, so no presence test is written here — adding one would
     * be a second opinion about a fact the service already owns.
     */
    return authStore.refreshSession().pipe(
      map((): boolean | UrlTree => true),
      catchError((): Observable<boolean | UrlTree> => of(signInRedirect(router, state.url))),
    );
  }

  /*
   * MIGRATION: loop-prevention hardening with no legacy counterpart. The legacy
   * `Response.Redirect(NavigateURL("Access Denied"), True)` sent the browser to a
   * page that ran its own load handler and could redirect again, so the pattern
   * carried a genuine cycle hazard. `app.routes.ts` must never attach this gate to
   * the sign-in route, and this branch guarantees the application still starts if it
   * ever does: the worst outcome becomes an unauthenticated caller reaching the
   * sign-in screen, which is exactly where the redirect would have sent them.
   */
  if (isSignInRoute(state.url)) {
    return true;
  }

  /*
   * A redirect rather than a bare refusal, and built rather than performed.
   *
   * Returning the destination lets the router replace the in-flight navigation
   * atomically: one navigation begins and one ends, the address bar never shows a
   * screen the caller cannot have, and no history entry is left pointing at a
   * refused address. Calling `navigate` from inside a gate instead starts a SECOND
   * navigation while the first is still being decided, which races it.
   *
   * MIGRATION: the attempted address survives the round trip, which is new. The
   * legacy forms-authentication cookie at `Website/release.config:L147` —
   * `<forms name=".DOTNETNUKE" protection="All" timeout="60" cookieless="UseCookies"/>` —
   * kept a sign-in alive across page loads for sixty minutes, so an operator was
   * rarely bounced mid-task and the address they wanted was rarely lost. Credentials
   * are now held in memory only, which is what removes the persistent-cookie attack
   * surface but also means a full page reload genuinely requires signing in again.
   * Preserving the address under `returnUrl` is what keeps that acceptable: the
   * caller resumes where they were aiming instead of landing on a default screen.
   * The access token's own sixty-minute lifetime is deliberate parity with the cookie
   * timeout above, and the longer renewal window preserves the practical session
   * length; both are deployment settings and neither is restated on the client.
   *
   * The address is forwarded exactly as the router serialised it, query string
   * included and otherwise untouched — not trimmed, not lower-cased and not
   * re-encoded. Percent-encoding the value for transport is the router's own job when
   * it serialises this tree, and doing it here as well would double-encode it and
   * hand the sign-in screen an address it could not navigate back to.
   */
  return signInRedirect(router, state.url);
};

/**
 * Builds the redirect to the sign-in screen, carrying the address the caller was trying to
 * reach.
 *
 * Declared once and used by both refusal paths — the unauthenticated one and the
 * unrecoverable-expiry one — so the two cannot drift apart in either the route they name or
 * the parameter they carry it under.
 *
 * The address is forwarded exactly as the router serialised it, query string included and
 * otherwise untouched: not trimmed, not lower-cased and not re-encoded. Percent-encoding the
 * value for transport is the router's own job when it serialises this tree, and doing it
 * here as well would double-encode it and hand the sign-in screen an address it could not
 * navigate back to.
 *
 * @param router The router to build the destination with.
 * @param attemptedUrl The full address the caller was refused.
 * @returns The destination to return from the gate.
 */
function signInRedirect(router: Router, attemptedUrl: string): UrlTree {
  return router.createUrlTree([SIGN_IN_ROUTE], {
    queryParams: { [RETURN_URL_KEY]: attemptedUrl },
  });
}
