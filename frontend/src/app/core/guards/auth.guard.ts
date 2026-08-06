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
 * @see `Website/admin/Security/AccessDenied.ascx.vb` — the legacy destination. Both
 * of its branches present the refusal with `ModuleMessage.ModuleMessageType.YellowWarning`,
 * so a refusal was a WARNING and never a fault. This gate's whole affordance is the
 * redirect, so it raises no notification at all — but it must never be changed to
 * escalate a refusal to error severity.
 */

import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn } from '@angular/router';

import { TokenStorageService } from '../services/token-storage.service';
import { AuthStore } from '../state/auth.store';

/**
 * The route the gate redirects an unauthenticated caller to.
 *
 * Spelled here as a leading-slash absolute path because that is what
 * {@link Router.createUrlTree} is handed. `core/interceptors/auth.interceptor.ts`
 * declares the identical destination for the case where an in-flight request cannot
 * be renewed; the two are deliberately independent constants rather than one shared
 * export, because the interceptor does not export its copy and reaching across to it
 * would couple a navigation gate to a transport concern for the sake of one string.
 * A rename on either side is caught by the specifications, which spell the path
 * again rather than importing it.
 */
const SIGN_IN_ROUTE = '/login';

/** The query parameter key carrying the address the caller was trying to reach. */
const RETURN_URL_KEY = 'returnUrl';

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
 * FULLY SYNCHRONOUS, BY DESIGN. It returns `true` or a redirect, never an observable
 * and never a promise, because it performs no work that could be asynchronous: no
 * request is issued, no token is renewed and no clock is read. Every navigation
 * therefore resolves immediately, and there is no window in which a half-decided
 * gate could be observed.
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
   * MIGRATION: "valid" means A SESSION IS HELD, not "the access token has not yet
   * lapsed". No clock is read anywhere in this file and no expiry is compared,
   * because renewal is REACTIVE rather than anticipatory: a lapsed token is
   * discovered when the API refuses a request, and
   * `core/interceptors/auth.interceptor.ts` renews it and replays that request. The
   * custodian does publish a lapsed-or-not verdict, and this gate deliberately does
   * not ask for it — doing so would redirect a caller whose access token has lapsed
   * but whose renewal credential is still good, discarding a recoverable session and
   * collapsing the effective session length to the access token's own lifetime. It
   * would also make the outcome depend on the wall clock, which a specification
   * cannot pin down. The interceptor is named here by path and never imported: a
   * navigation gate must not depend on a transport concern.
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
     * partitioned by calling address. That control is the reason this gate issues NO
     * request of any kind: re-validating against the server on every navigation would
     * consume an operator's own rate-limit budget and lock them out of the
     * application by navigating around it.
     */
    return true;
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
  return router.createUrlTree([SIGN_IN_ROUTE], {
    queryParams: { [RETURN_URL_KEY]: state.url },
  });
};
