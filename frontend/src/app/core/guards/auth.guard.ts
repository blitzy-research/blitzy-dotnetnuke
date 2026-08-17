import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn, UrlTree } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import type { Observable } from 'rxjs';

import {
  RETURN_URL_QUERY_KEY,
  SIGN_IN_ROUTE,
  credentialRemediationRoute,
  profileRemediationRoute,
} from '../config/app-routes.config';
import { NotificationService } from '../services/notification.service';
import { TokenStorageService } from '../services/token-storage.service';
import { AuthStore } from '../state/auth.store';

const RETURN_URL_KEY = RETURN_URL_QUERY_KEY;

/**
 * The leading path segment of an address, with the query string, the fragment and every empty segment
 * removed. The single place separators are interpreted, which is what lets the attempted address and the
 * sign-in route be reduced by IDENTICAL rules and then compared.
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
 * @param url The attempted address, as serialised by the router.
 * @returns True when the address is already within the sign-in route.
 */
function isSignInRoute(url: string): boolean {
  return firstPathSegment(url) === firstPathSegment(SIGN_IN_ROUTE);
}

/**
 * The sentence a caller sees when a screen is withheld because their SESSION has an outstanding obligation
 * rather than because their account lacks a permission. ⚠ THE DISTINCTION IS THE WHOLE POINT: one is a task
 * they can finish in the next minute, the other is a standing fact about their account, and telling them the
 * second when the first is true sends them to ask for rights they do not need.
 */
export const PROFILE_REMEDIATION_MESSAGE =
  'Complete the required profile fields before continuing. This site asks every account for them, and the'
  + ' rest of the site becomes available as soon as they are filled in.';

/** The credential counterpart of {@link PROFILE_REMEDIATION_MESSAGE}. */
export const CREDENTIAL_REMEDIATION_MESSAGE =
  'Change your password before continuing. The rest of the site becomes available as soon as it is changed.';

/**
 * Whether an attempted address is the screen that discharges an obligation THIS caller actually owes.
 *
 * ⚠ THIS MIRRORS `RemediationAuthorizationHandler` AND MUST NOT BE LOOSENED TO A SHAPE TEST. The server's
 * rule has two conditions, not one:
 *
 * - the password endpoint is allowed only while `MustChangePassword` stands, and only when the route names
 *   the caller's own account;
 * - the profile endpoint is allowed only while `MustUpdateProfile` stands, on the same ownership condition.
 *
 * An earlier version of this function tested only the SHAPE of the address — `/users/{any-id}/(profile
 * |password)` — and admitted either screen for any account. That was measured as a defect at runtime: a
 * caller owing only a password change followed the header's "Manage Profile" link, this gate admitted it
 * because the shape matched, and the server then refused the screen's own data with
 * `403 auth.not_permitted`. The caller was carried off the one screen that could clear their obligation and
 * stranded on a Forbidden-erroring screen instead. Matching the server's two conditions is what keeps the
 * gate and the API from disagreeing about the same request.
 *
 * When BOTH obligations stand, BOTH screens satisfy it — deliberately, because the server permits both, and
 * a caller who has just changed their password must be able to walk on to the profile screen without this
 * gate redirecting them backwards.
 *
 * @param url The attempted address, as serialised by the router.
 * @param callerId The signed-in account; the only account an admitted address may name.
 * @param credentialOwed Whether a mandatory password change stands.
 * @param profileOwed Whether a mandatory profile completion stands.
 * @returns True when the address discharges an obligation this caller owes.
 */
function dischargesAStandingObligation(
  url: string,
  callerId: number,
  credentialOwed: boolean,
  profileOwed: boolean,
): boolean {
  const path = routePathOf(url);

  if (credentialOwed && path === credentialRemediationRoute(callerId)) {
    return true;
  }

  return profileOwed && path === profileRemediationRoute(callerId);
}

/**
 * Reduces a serialised address to its bare path, so a query string or fragment cannot defeat a comparison
 * against a known route.
 *
 * @param url The attempted address.
 * @returns The path alone, without a trailing separator.
 */
function routePathOf(url: string): string {
  const [beforeFragment = ''] = url.split('#');
  const [path = ''] = beforeFragment.split('?');

  return path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;
}

/**
 * Admits a signed-in caller, and redirects everyone else to the sign-in screen with the address they were
 * trying to reach preserved. A FUNCTION AND NOT A CLASS. The class-based activation contract is
 * deprecated in this generation of the router; a functional gate needs no provider, no decorator and no
 * registration, and resolves its collaborators with {@link inject} at the point the router calls it.
 *
 * @param _route The activated route.
 * @param state The router state, whose `url` is the full attempted address.
 * @returns `true` to admit, or a redirect to the sign-in route.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const router = inject(Router);
  const authStore = inject(AuthStore);
  const tokenStorage = inject(TokenStorageService);
  const notification = inject(NotificationService);

  const hasSession = authStore.isAuthenticated();

  const accessToken = tokenStorage.accessToken();
  const hasBearerToken = accessToken !== null && accessToken.length > 0;

  if (hasSession && hasBearerToken) {
    if (!tokenStorage.isAccessTokenExpired()) {
      return restrictedSessionRedirect(router, notification, authStore, state.url) ?? true;
    }

    // The sign-in route itself is admitted without renewing, and the ordering is deliberate: this test sits
    // BEFORE the renewal rather than after it.
    if (isSignInRoute(state.url)) {
      return true;
    }

    // The token has lapsed. Renewal is attempted exactly once, and the navigation waits for the answer
    // rather than proceeding hopefully: admitting first and renewing in the background is what produced the
    // state this branch exists to prevent — a screen mounted against a session that has already ended.
    return authStore.refreshSession().pipe(
      map(
        (): boolean | UrlTree =>
          restrictedSessionRedirect(router, notification, authStore, state.url) ?? true,
      ),
      catchError((): Observable<boolean | UrlTree> => of(signInRedirect(router, state.url))),
    );
  }

  if (isSignInRoute(state.url)) {
    return true;
  }

  // Returning the destination lets the router replace the in-flight navigation atomically: one navigation
  // begins and one ends, the address bar never shows a screen the caller cannot have, and no history entry
  // is left pointing at a refused address.
  return signInRedirect(router, state.url);
};

/**
 * Builds the redirect to the sign-in screen, carrying the address the caller was trying to reach.
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

/**
 * Redirects a caller whose SESSION carries an outstanding obligation to the screen that discharges it, and
 * says which obligation it is.
 *
 * ⚠ THIS MIRRORS A PRECEDENCE THE SERVER ALREADY ENFORCES, AND ITS ABSENCE HERE WAS MEASURED AS A DEFECT.
 * The API refuses every endpoint that is not explicitly remediation-allowed with
 * `auth.remediation_required` and the sentence "Complete the required profile fields before continuing." -
 * an actionable task. But the permission gate cancelled the navigation BEFORE any request was issued, so
 * that document never arrived, and what the caller read instead was "You do not have access to this
 * content." Two things went wrong at once: a fixable task was reported as a standing fact about their
 * account, and - because the outcome was byte-identical once the fields were filled in - "fix three fields"
 * and "you will never have access" could not be told apart. The landing screen explained nothing either.
 *
 * Deciding it here rather than waiting for a request is not a duplicate authorisation rule. The obligation
 * is a property of the caller's OWN session, published on it, so the client already holds the fact; the
 * server remains the authority and still refuses independently if the client is wrong. What the client gains
 * is the ability to say the actionable thing, and to land the caller on the screen that resolves it rather
 * than nowhere.
 *
 * @param router The router to build the destination with.
 * @param notification The transient-message surface.
 * @param authStore The identity projection, which publishes the obligations.
 * @param attemptedUrl The address the caller was trying to reach.
 * @returns The destination to return from the gate, or `null` when the session carries no obligation.
 */
function restrictedSessionRedirect(
  router: Router,
  notification: NotificationService,
  authStore: AuthStore,
  attemptedUrl: string,
): UrlTree | null {
  if (!authStore.sessionRestricted()) {
    return null;
  }

  if (isSignInRoute(attemptedUrl)) {
    return null;
  }

  const caller = authStore.currentUser();

  if (caller === null) {
    // No account to address a remediation screen with. Nothing is claimed and nothing is announced; the
    // ordinary gates below decide.
    return null;
  }

  // The credential obligation is stated first when both apply, matching the order the root redirect resolves
  // them in, so a caller carrying both is never told about the second while the first still blocks them.
  const credentialFirst = authStore.mustChangePassword();
  const profileOwed = authStore.mustUpdateProfile();

  const destination = credentialFirst
    ? credentialRemediationRoute(caller.userId)
    : profileRemediationRoute(caller.userId);

  // Already where this gate would send them. Admitted unconditionally and without comment, because
  // redirecting an address to itself is a navigation this same gate would refuse again — the two would chase
  // each other. ⚠ THIS TEST DELIBERATELY DOES NOT CONSULT THE OBLIGATIONS: it is the loop-prevention
  // invariant, and it must hold even for a store state the obligations cannot explain.
  if (routePathOf(attemptedUrl) === destination) {
    return null;
  }

  // Not the destination, but still a screen that discharges an obligation this caller owes — which happens
  // when BOTH stand and they walk on to the second. Admitted, because the server permits it.
  if (dischargesAStandingObligation(attemptedUrl, caller.userId, credentialFirst, profileOwed)) {
    return null;
  }

  notification.notify(
    'warning',
    credentialFirst ? CREDENTIAL_REMEDIATION_MESSAGE : PROFILE_REMEDIATION_MESSAGE,
    null,
    false,
    true,
  );
  notification.retainAcrossNavigation();

  return router.parseUrl(destination);
}
