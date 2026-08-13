import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn, UrlTree } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import type { Observable } from 'rxjs';

import { RETURN_URL_QUERY_KEY, SIGN_IN_ROUTE } from '../config/app-routes.config';
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

  const hasSession = authStore.isAuthenticated();

  const accessToken = tokenStorage.accessToken();
  const hasBearerToken = accessToken !== null && accessToken.length > 0;

  if (hasSession && hasBearerToken) {
    if (!tokenStorage.isAccessTokenExpired()) {
      return true;
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
      map((): boolean | UrlTree => true),
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
