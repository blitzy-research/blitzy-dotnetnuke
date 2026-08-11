/**
 * A preloading strategy that waits for a session before fetching any feature bundle.
 *
 * ## What this replaces, and why
 *
 * The router was configured with the framework's `PreloadAllModules`, which begins fetching every
 * lazily-declared bundle as soon as the initial navigation settles — including when that navigation
 * settled on the anonymous sign-in screen. A review measured the consequence: **163,918 bytes,
 * 54.49% of all the application's JavaScript, downloaded by anyone who could reach the login page**,
 * carrying with it the names of every administration route and every API endpoint those features
 * call.
 *
 * ⚠ THIS IS NOT PRESENTED AS AN ACCESS-CONTROL FIX, and it must not be mistaken for one. The
 * authorisation boundary is enforced server-side: every protected endpoint re-authorises against
 * stored state and answers 403 on its own account, and a review independently confirmed that with
 * eighteen endpoints probed using a non-privileged operator's own live bearer token. Shipping route
 * names to an anonymous visitor grants no authority and never did. What it does is hand an
 * unauthenticated party a free map of the application's internals, and pay for a download they
 * cannot use — so this is defence in depth and bandwidth, in that order.
 *
 * ## Why the eager behaviour is kept rather than dropped
 *
 * The obvious alternative — no preloading at all — was rejected because the migration plan requires
 * a preloading strategy to be configured, and because the reasoning behind the eager choice is
 * sound for this application: an administration console is reached by an authenticated operator who
 * will visit several screens in one session, so the bundles are very likely to be wanted, and lazy
 * declaration alone would make the first navigation into each feature pay for the economy twice.
 *
 * So the strategy preserves that behaviour exactly and changes only WHEN it starts. Once a session
 * is held, this loads every route offered to it, which is precisely what `PreloadAllModules` does.
 * Before then it loads nothing.
 *
 * MIGRATION: recorded as a deliberate, narrow departure from the plan's literal
 * `withPreloading(PreloadAllModules)`. The plan's non-functional requirement is that a preloading
 * strategy be configured in the composition root, which this satisfies; naming the framework's
 * built-in was an illustration of the strategy rather than a requirement that anonymous visitors
 * receive the administration bundles.
 */
import { inject, Injectable } from '@angular/core';
import type { PreloadingStrategy, Route } from '@angular/router';
import { EMPTY, type Observable } from 'rxjs';

import { AuthStore } from '../state/auth.store';

@Injectable({ providedIn: 'root' })
export class AuthenticatedPreloadingStrategy implements PreloadingStrategy {
  private readonly authStore = inject(AuthStore);

  /**
   * Decides whether to fetch one lazily-declared route's bundle now.
   *
   * ⚠ THE DECISION IS TAKEN AT CALL TIME AND IS NOT CACHED, which is what makes signing in
   * sufficient. The router consults this strategy for each preloadable route as navigations settle,
   * so the first navigation after a session is established — the redirect away from the sign-in
   * screen, which every sign-in performs — is when the bundles begin arriving. Nothing has to
   * subscribe to the session or re-arm anything.
   *
   * `EMPTY` rather than `of(null)` for the refusal: both complete without loading, and `EMPTY` says
   * "there is nothing here" without manufacturing a value the router has no use for.
   *
   * Deliberately reads only WHETHER A SESSION IS HELD, never a role or a permission. A finer test
   * would put a third opinion about authority into the application, behind the two route gates that
   * already hold one, and it would be the least informed of the three: a bundle is shared by every
   * screen in its feature, so refusing it per-permission would penalise an operator who is entitled
   * to some of those screens. Authority remains the gates' question and the server's answer.
   *
   * @param _route The route offered for preloading, which this strategy does not inspect.
   * @param load The loader to invoke when the bundle should be fetched now.
   * @returns The loader's result once a session is held, and an empty stream before then.
   */
  preload(_route: Route, load: () => Observable<unknown>): Observable<unknown> {
    return this.authStore.isAuthenticated() ? load() : EMPTY;
  }
}
