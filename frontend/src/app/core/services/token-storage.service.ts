import { Injectable, Signal, computed, signal } from '@angular/core';

// Type-only, and deliberately so: the session shape is erased at compile time, so
// this file emits no import of the model at runtime and cannot participate in a
// cycle with it. The project compiles with isolated modules, under which the
// distinction between a type import and a value import must be explicit rather
// than inferred from usage.
import type { AuthSession, CurrentUser } from '../models/auth.model';

/**
 * Holds the current authentication session.
 *
 * MEMORY ONLY, and that is the security posture rather than an unfinished
 * implementation. The session — including the refresh token — lives in a signal
 * inside this instance and is written to no persistent tier: not local storage,
 * not session storage, not a cookie, not IndexedDB. The reasoning is specific to
 * what each tier exposes:
 *
 * - Web storage is readable by any script executing on the origin and it PERSISTS,
 *   so a single successful cross-site scripting injection anywhere in the
 *   application — or in any dependency it loads — exfiltrates whatever refresh
 *   token is sitting there, including one issued in an earlier session. Holding
 *   the session here removes that persistence and the reload window with it.
 *   IT DOES NOT DEFEAT ACTIVE CROSS-SITE SCRIPTING, and it must not be read as
 *   though it did: script running on this origin shares the application's own
 *   heap, so it can reach this service through the injector, patch `fetch` or the
 *   HTTP client, or simply wait and intercept the next token the server issues.
 *   The benefit is narrower than "tokens are inaccessible" — it is a smaller
 *   window and no credential left behind after the tab closes.
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
 * MIGRATION: the access token's sixty-minute lifetime is exact parity with legacy
 * forms authentication rather than a fresh choice. `Website/release.config:L146`
 * declares `<authentication mode="Forms">` and L147 stamps `timeout="60"` on the
 * `.DOTNETNUKE` ticket. The paired seven-day refresh window has no legacy
 * counterpart, because the legacy ticket carried no renewal credential — it was
 * simply reissued on each request until it lapsed. Both durations belong to the
 * server: it issues the pair and stamps the expiry it chose. This service holds
 * what it was handed and enforces neither, which is why no lifetime constant
 * appears below and no member compares one against a clock.
 *
 * MIGRATION: a DELIBERATE BEHAVIOURAL DIFFERENCE, and the sharpest one here. The
 * legacy `.DOTNETNUKE` ticket was a cookie — `Website/release.config:L147` sets
 * `cookieless="UseCookies"` — so it survived a page load, a second tab and a
 * browser restart for its full sixty minutes. Nothing held here survives any of
 * those. A full page reload therefore ends the session and the person signs in
 * again. That is the intended posture, not an unfinished implementation, and it is
 * recorded as a divergence rather than absorbed silently.
 *
 * MIGRATION: signing out has no stateless counterpart. The legacy sign-out cleared
 * the authentication cookie in the response, which ended the session outright. A
 * bearer token cannot be recalled once issued, so sign-out became two independent
 * actions: the server revokes the refresh token, and {@link clear} discards this
 * copy. The access token stays technically valid until its stamped expiry. That
 * residual window is exactly why the lifetime above is short, and why extending it
 * would be a security decision rather than a convenience.
 *
 * MIGRATION: the credential store being replaced was reversible, which is the
 * habit this file is built against. The legacy membership provider was registered
 * to encrypt rather than hash (`Website/release.config:L245`,
 * `passwordFormat="Encrypted"`) with retrieval enabled (L239), under a symmetric
 * key committed to the repository in plain sight (L89-L93) — and
 * `Website/development.config:L90` commits the identical key. Recovery needed both
 * halves — the committed key material AND the encrypted credential rows in the
 * database — so anyone holding both could turn every stored password back into
 * plaintext, and committing the key is what made one of the two halves free.
 * Two rules follow and are held to absolutely: never commit a secret, and never
 * log a credential. No member below writes to a log or raises an error carrying a token,
 * and there is deliberately no `toString`, no `toJSON` and no debug accessor
 * through which one could reach either.
 *
 * ## Boundaries — what this service refuses to decide
 *
 * Registered at the root so that one session is shared by the whole application,
 * which is what keeps its readers from disagreeing about who is signed in. Those
 * readers own the decisions deliberately absent here:
 *
 * - `core/interceptors/auth.interceptor.ts` attaches the bearer token and drives
 *   renewal on a 401. This service does not know the header's name, never builds a
 *   header, and performs no request, no retry and no refresh orchestration.
 * - `core/services/auth.service.ts` owns the authentication calls and is the only
 *   caller of {@link store} on a sign-in or a successful renewal.
 * - `shared/directives/has-permission.directive.ts` reads {@link permissions} and
 *   decides what to render. This service never evaluates an entitlement.
 *
 * Nothing here decodes a token. The value is an opaque string: no base64 step, no
 * payload parse, no claim read. Identity arrives already modelled on the sign-in
 * response and is stored as handed over, so a malformed or hostile token cannot
 * influence anything this service reports.
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
   * How many times the held session has changed, counting from zero.
   *
   * Private and exposed read-only below, and advanced ONLY by {@link store} and
   * {@link clear} — the two methods every session transition passes through. That is what
   * makes the count authoritative: there is no way to change the session without changing
   * this number, and no way to change this number without changing the session.
   */
  private readonly _generation = signal(0);

  /**
   * The current session, or null when nobody is signed in.
   *
   * A signal rather than an observable so that a component reading it in a
   * template participates in change detection under `OnPush` without a
   * subscription to manage.
   */
  readonly session: Signal<AuthSession | null> = this._session.asReadonly();

  /**
   * A monotonically increasing count of session transitions — the AUTH EPOCH.
   *
   * ## What this exists to prevent
   *
   * Everything asynchronous in the authentication path takes time, and the session can be
   * replaced or discarded while it is in the air. Before this counter existed, no caller
   * could tell the difference between "the session I started under" and "whatever session
   * happens to be held now", and three concrete defects followed from exactly that
   * ambiguity:
   *
   * - A request that failed with 401 under account A would be retried carrying account B's
   *   bearer token, because the retry re-read the token from storage rather than proving the
   *   original session was still current. An operator signing out and back in as somebody
   *   else could therefore see A's request execute as B.
   * - A renewal in flight when a sign-out landed would store its rotated pair on arrival and
   *   RESURRECT the session the operator had just ended — or, on failure, clear a NEWER
   *   session established in the meantime.
   * - Identity read from the current-user endpoint could be published after a sign-out,
   *   leaving one account's roles and personal details on screen after another had signed in.
   *
   * ## How it is used
   *
   * The pattern is the same in all three places and is deliberately uniform: CAPTURE this
   * value when the asynchronous work begins, and before ANY commit — storing a session,
   * clearing one, publishing an identity, retrying a request, resetting shared state — ask
   * {@link isCurrentGeneration} whether it still holds. If it does not, the work belongs to
   * a session that no longer exists and its result must be DISCARDED rather than applied.
   * Discarding is always the safe direction: the caller has already been signed out or
   * replaced, so there is nothing to lose and a stale commit is the only thing that can go
   * wrong.
   *
   * ## What it is not
   *
   * Not a session identifier, and it must never be sent anywhere or logged. It is a purely
   * local ordinal whose only meaningful operation is equality against a value captured
   * earlier in the same browsing context. Its absolute value carries no information: it
   * counts transitions since this instance was constructed, so a specification asserts that
   * it CHANGED rather than what it changed to.
   *
   * Not a replacement for cancellation either. Unsubscribing from an observable stops the
   * work; this stops the RESULT of work that could not be stopped — a shared request other
   * subscribers still need, or a promise already resolved. Both mechanisms are used, and the
   * refresh path uses each for the half it can address.
   *
   * MIGRATION: no legacy counterpart exists, and none could. Legacy sign-out cleared the
   * `.DOTNETNUKE` cookie (`Library/Components/Security/PortalSecurity.vb:L77` clears five
   * cookies), which took effect synchronously on the next request because the session lived
   * in the cookie rather than in client memory — there was no in-flight client-side renewal
   * to invalidate, because there was no renewal credential at all. This counter is therefore
   * net-new machinery made necessary by the move to bearer tokens, not a translated
   * mechanism.
   */
  readonly generation: Signal<number> = this._generation.asReadonly();

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

  /**
   * When {@link accessToken} expires, exactly as the server stamped it, or null
   * when no session is held.
   *
   * A raw passthrough of the single expiry representation the sign-in contract
   * publishes: an ISO 8601 instant in Coordinated Universal Time. It is handed back
   * unread — no parse, no clock, no arithmetic, no `Date`, and no lapsed-or-valid
   * verdict — so a caller that only wants to display or forward the value is not
   * made to pay for a comparison it did not ask for, and cannot be handed a verdict
   * computed against some earlier moment.
   *
   * The verdict is a separate question and lives in {@link isAccessTokenExpired},
   * which takes the instant to compare against. That separation is the point: an
   * expiry is a fact about the session and is stable, whereas whether it has passed
   * is only true relative to a clock read, and the two must not be conflated behind
   * one member.
   */
  readonly accessTokenExpiresAt: Signal<string | null> = computed(
    () => this._session()?.expiresAtUtc ?? null,
  );

  /**
   * The signed-in identity, or null when nobody is signed in.
   *
   * A snapshot taken when the credentials were issued, not a live view: an
   * entitlement granted afterwards appears only once the session is renewed.
   * Because the server re-authorises every request against stored state, a stale
   * snapshot can only make a screen offer an affordance the API then refuses — it
   * can never widen access, which is the correct direction for it to fail.
   */
  readonly currentUser: Signal<CurrentUser | null> = computed(
    () => this._session()?.user ?? null,
  );

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
   * Whether the signed-in account must complete its profile before continuing.
   *
   * Projected as its own signal because it is a BLOCKING advisory: something has to
   * be able to gate navigation on it, and a gate reads one value rather than
   * destructuring a session. The two other advisories are informational and stay
   * reachable through {@link session}, which is the whole stored shape.
   *
   * `false` when nobody is signed in, which is the same answer as "no advisory" and
   * is the correct one for a gate: an unauthenticated caller is stopped by the
   * authentication check rather than by this one.
   */
  readonly mustUpdateProfile: Signal<boolean> = computed(
    () => this._session()?.mustUpdateProfile ?? false,
  );

  /**
   * Replaces the held session.
   *
   * Called on a successful sign-in and again on every successful refresh, since
   * refresh rotates both tokens — storing only the access token would leave the
   * consumed refresh token in place, and presenting it again is treated by the
   * server as a replay and revokes the whole family.
   *
   * Advances {@link generation}. Every transition does, including one session
   * replacing another, because that IS an identity change from the point of view of
   * anything holding a request already in flight.
   *
   * @param session The session to hold.
   */
  store(session: AuthSession): void {
    this._session.set(session);
    this.advanceGeneration();
  }

  /**
   * Discards the held session.
   *
   * Idempotent, so a sign-out racing an expiry does not need to test first. This
   * clears local state only — revoking the refresh token is a server call, and
   * `core/state/auth.store.ts` pairs the two.
   *
   * ⚠ ADVANCES {@link generation} UNCONDITIONALLY, INCLUDING WHEN NO SESSION WAS HELD.
   * That looks redundant and is not. A clear is a deliberate statement that whatever
   * session existed is over, and asynchronous work started under it must not commit
   * afterwards. Advancing only when a session was present would mean two clears in
   * succession left the second one silent — so a refresh that began between them would
   * still observe a matching generation and would resurrect the session that had just
   * been ended twice over.
   */
  clear(): void {
    this._session.set(null);
    this.advanceGeneration();
  }

  /**
   * Whether a generation captured earlier is still the current one.
   *
   * The single question every asynchronous authentication path asks before it commits
   * anything, and the reason it lives here rather than at each call site: the comparison
   * and the counter that feeds it belong to the same owner, so no caller can compare
   * against a number this service did not issue.
   *
   * A plain equality test, deliberately — NOT `captured >= current`, and not a
   * "difference of one" tolerance. Any advance at all means the session changed, and the
   * only safe response to "the session changed" is to discard the work rather than to
   * reason about how much it changed by.
   *
   * @param captured The generation read at the moment the work began.
   * @returns True only when no session transition has occurred since.
   */
  isCurrentGeneration(captured: number): boolean {
    return this._generation() === captured;
  }

  /**
   * Advances the generation counter by one.
   *
   * Private, so the counter can only move as a CONSEQUENCE of a session transition and
   * never on its own. A public bump would let a caller invalidate in-flight work without
   * changing the session, which is a different operation with different consequences and
   * has no call site here.
   *
   * Read-then-write on the signal rather than an `update` callback, so the increment is
   * visibly a single step. Overflow is not a consideration: at one transition per
   * millisecond this counter would need roughly three hundred thousand years to reach the
   * exactly-representable integer limit.
   */
  private advanceGeneration(): void {
    this._generation.set(this._generation() + 1);
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
