import { Injectable, Signal, computed, signal } from '@angular/core';

import { AuthSession } from '../models/auth.model';

/**
 * Holds the current authentication session.
 *
 * MEMORY ONLY, and that is the security posture rather than an unfinished
 * implementation. The session — including the refresh token — lives in a signal
 * inside this instance and is written to no persistent tier: not local storage,
 * not session storage, not a cookie, not IndexedDB. The reasoning is specific to
 * what each tier exposes:
 *
 * - Web storage is readable by any script executing on the origin, so a single
 *   successful cross-site scripting injection anywhere in the application — or in
 *   any dependency it loads — exfiltrates a long-lived refresh token. A token in
 *   a closure is reachable only by code that already holds a reference to this
 *   service.
 * - A cookie would be sent automatically on every same-origin request, which
 *   reintroduces the cross-site request forgery surface that bearer tokens exist
 *   to avoid, and the API authenticates from the `Authorization` header rather
 *   than from a cookie in any case.
 *
 * The cost is explicit and accepted: a full page reload loses the session and the
 * person signs in again. That cost is bounded by design rather than by accident,
 * because the API issues short-lived access tokens with rotating refresh tokens —
 * a persisted session would only extend the window during which a stolen token is
 * useful. Nothing here is deferred: there is no persistent tier to add later, and
 * adding one would be a deliberate weakening that needs its own justification.
 *
 * MIGRATION: the legacy application authenticated with an ASP.NET forms
 * authentication cookie issued by the membership provider, and signed out by
 * clearing that cookie. Neither half survives. There is no cookie to clear, so
 * {@link clear} discards local state and the paired server call revokes the
 * refresh token; the access token itself cannot be recalled and remains valid
 * until it expires, which is the reason its lifetime is short.
 *
 * Registered at the root so that one session is shared by the whole application.
 * The interceptors, the authentication service and the permission directive all
 * read the same instance, which is what keeps them from disagreeing about who is
 * signed in.
 */
@Injectable({ providedIn: 'root' })
export class TokenStorageService {
  /**
   * The live session, or null when nobody is signed in.
   *
   * Private and exposed read-only below, so a consumer cannot assign a session
   * without going through {@link store} — the single place that decides what a
   * valid transition is.
   */
  private readonly _session = signal<AuthSession | null>(null);

  /**
   * The current session, or null when nobody is signed in.
   *
   * A signal rather than an observable so that a component reading it in a
   * template participates in change detection under `OnPush` without a
   * subscription to manage.
   */
  readonly session: Signal<AuthSession | null> = this._session.asReadonly();

  /**
   * The bearer token to present, or null when there is none.
   *
   * Derived rather than stored, so it cannot fall out of step with the session.
   */
  readonly accessToken: Signal<string | null> = computed(() => this._session()?.accessToken ?? null);

  /** The token to present when refreshing, or null when there is none. */
  readonly refreshToken: Signal<string | null> = computed(
    () => this._session()?.refreshToken ?? null,
  );

  /** The signed-in identity, or null when nobody is signed in. */
  readonly currentUser = computed(() => this._session()?.user ?? null);

  /**
   * Whether a session is held.
   *
   * Deliberately reports the presence of a session rather than its validity. An
   * expired access token still produces `true`, because the correct response to
   * expiry is to refresh — which requires the session to still be here — not to
   * behave as though nobody is signed in. A guard wanting the stricter question
   * asks {@link isAccessTokenExpired} as well.
   */
  readonly isAuthenticated: Signal<boolean> = computed(() => this._session() !== null);

  /**
   * The permission keys held by the signed-in account, empty when nobody is.
   *
   * The source the `hasPermission` structural directive consults. Frozen at the
   * empty array when absent so consumers can iterate unconditionally.
   */
  readonly permissions: Signal<readonly string[]> = computed(
    () => this._session()?.user.permissions ?? EMPTY_PERMISSIONS,
  );

  /**
   * Replaces the held session.
   *
   * Called on a successful sign-in and again on every successful refresh, since
   * refresh rotates both tokens — storing only the access token would leave the
   * consumed refresh token in place, and presenting it again is treated by the
   * server as a replay and revokes the whole family.
   *
   * @param session The session to hold.
   */
  store(session: AuthSession): void {
    this._session.set(session);
  }

  /**
   * Discards the held session.
   *
   * Idempotent, so a sign-out racing an expiry does not need to test first. This
   * clears local state only — revoking the refresh token is a server call, and the
   * authentication service performs both.
   */
  clear(): void {
    this._session.set(null);
  }

  /**
   * Whether the access token's absolute expiry has passed.
   *
   * Takes an explicit `now` so the caller controls the clock; a specification can
   * then assert both sides of the boundary without waiting.
   *
   * Returns true when no session is held: with no token, every request is
   * unauthenticated, which is the same practical condition as an expired one. It
   * also returns true for an unparseable expiry, because an instant that cannot be
   * read cannot be shown to be in the future, and treating it as valid would send
   * a token the server will reject.
   *
   * @param now The instant to compare against, defaulting to the present.
   * @returns True when the access token cannot be relied upon.
   */
  isAccessTokenExpired(now: Date = new Date()): boolean {
    const session = this._session();

    if (session === null) {
      return true;
    }

    const expiry = Date.parse(session.expiresAtUtc);

    if (Number.isNaN(expiry)) {
      return true;
    }

    return expiry <= now.getTime();
  }
}

/**
 * The permission list handed out when no session is held.
 *
 * Frozen and shared rather than allocated per read, so that the derived signal
 * returns a stable reference and does not appear to change on every evaluation.
 */
const EMPTY_PERMISSIONS: readonly string[] = Object.freeze([]);
