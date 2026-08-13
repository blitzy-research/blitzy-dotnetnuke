import { Injectable, Signal, computed, signal } from '@angular/core';

import type { AuthSession, CurrentUser } from '../models/auth.model';

/**
 * Holds the current authentication session. MEMORY ONLY, and that is the security posture rather than an
 * unfinished implementation.
 */
@Injectable({ providedIn: 'root' })
export class TokenStorageService {
  /** The live session, or null when nobody is signed in. */
  private readonly _session = signal<AuthSession | null>(null);

  /** How many times the held session has changed, counting from zero. */
  private readonly _generation = signal(0);

  /** The refresh tokens whose revocation the server has not yet acknowledged. */
  private readonly _pendingRevocations = signal<readonly string[]>(EMPTY_PENDING_REVOCATIONS);

  /** The current session, or null when nobody is signed in. */
  readonly session: Signal<AuthSession | null> = this._session.asReadonly();

  /**
   * A monotonically increasing count of session transitions — the AUTH EPOCH. ## What this exists to
   * prevent Everything asynchronous in the authentication path takes time, and the session can be
   * replaced or discarded while it is in the air.
   */
  readonly generation: Signal<number> = this._generation.asReadonly();

  /**
   * The credentials held for unacknowledged sign-outs, oldest first, empty when none is outstanding.
   * Ordered by retention so a reader draining the set retires the nearest-to-expiry residue first, and
   * read-only so nothing outside this service can retire a credential without going through {@link
   * releasePendingRevocation} — the single place that decides a residue has been dealt with.
   */
  readonly pendingRevocations: Signal<readonly string[]> = this._pendingRevocations.asReadonly();

  /**
   * The bearer token to present, or null when there is none. Derived rather than stored, so it cannot
   * fall out of step with the session.
   */
  readonly accessToken: Signal<string | null> = computed(() => this._session()?.accessToken ?? null);

  /** The token to present when refreshing, or null when there is none. */
  readonly refreshToken: Signal<string | null> = computed(
    () => this._session()?.refreshToken ?? null,
  );

  /**
   * When {@link accessToken} expires, exactly as the server stamped it, or null when no session is held.
   * A raw passthrough of the single expiry representation the sign-in contract publishes: an ISO 8601
   * instant in Coordinated Universal Time.
   */
  readonly accessTokenExpiresAt: Signal<string | null> = computed(
    () => this._session()?.expiresAtUtc ?? null,
  );

  /**
   * The signed-in identity, or null when nobody is signed in. A snapshot taken when the credentials were
   * issued, not a live view: an entitlement granted afterwards appears only once the session is renewed.
   */
  readonly currentUser: Signal<CurrentUser | null> = computed(
    () => this._session()?.user ?? null,
  );

  /** Whether a session is held. Deliberately reports the presence of a session rather than its validity. */
  readonly isAuthenticated: Signal<boolean> = computed(() => this._session() !== null);

  /** The permission keys held by the signed-in account, empty when nobody is. */
  readonly permissions: Signal<readonly string[]> = computed(
    () => this._session()?.user.permissions ?? EMPTY_PERMISSIONS,
  );

  /**
   * Whether the signed-in account must complete its profile before continuing. Projected as its own
   * signal because it is a BLOCKING advisory: something has to be able to gate navigation on it, and a
   * gate reads one value rather than destructuring a session.
   */
  readonly mustUpdateProfile: Signal<boolean> = computed(
    () => this._session()?.mustUpdateProfile ?? false,
  );

  /**
   * Replaces the held session. Called on a successful sign-in and again on every successful refresh,
   * since refresh rotates both tokens — storing only the access token would leave the consumed refresh
   * token in place, and presenting it again is treated by the server as a replay and revokes the whole
   * family.
   *
   * @param session The session to hold.
   */
  store(session: AuthSession): void {
    this._session.set(session);
    this.advanceGeneration();
  }

  /** Discards the held session. Idempotent, so a sign-out racing an expiry does not need to test first. */
  clear(): void {
    this._session.set(null);
    this.advanceGeneration();
  }

  /**
   * Holds one refresh token aside so a sign-out whose server call has not been acknowledged can still be
   * retried. ⚠ THE RETENTION SET IS DELIBERATELY NOT CLEARED BY {@link clear}, AND THAT IS THE WHOLE
   * POINT. Signing out discards the session immediately — it has to, because the operator asked to be
   * signed out and the local state must not survive the request — but the refresh token is also the ONLY
   * credential that can end the session on the server.
   *
   * @param refreshToken The credential awaiting acknowledged revocation.
   */
  retainForRevocation(refreshToken: string): void {
    if (refreshToken.length === 0) {
      return;
    }

    const retained: readonly string[] = this._pendingRevocations();

    // Already held, so nothing to do. Re-adding would post the same withdrawal twice, and moving it to the
    // back would reorder the set for no gain — it is the same credential and it has been outstanding since
    // the first time it was retained.
    if (retained.includes(refreshToken)) {
      return;
    }

    // Room is made from the FRONT, which is the oldest end. See the note on
    // {@link _pendingRevocations} for why that is the right end to give up.
    const kept: readonly string[] =
      retained.length < MAXIMUM_PENDING_REVOCATIONS
        ? retained
        : retained.slice(retained.length - MAXIMUM_PENDING_REVOCATIONS + 1);

    this._pendingRevocations.set(Object.freeze([...kept, refreshToken]));
  }

  /**
   * Forgets one retained credential.
   *
   * @param refreshToken The credential the server has finished with.
   */
  releasePendingRevocation(refreshToken: string): void {
    const retained: readonly string[] = this._pendingRevocations();

    if (retained.length === 0) {
      return;
    }

    const kept: readonly string[] = retained.filter((held) => held !== refreshToken);

    // Nothing was held under that value, so the array identity is left alone rather than replaced by an
    // equal copy — a reader deriving from this signal must not be woken by a release that changed nothing.
    if (kept.length === retained.length) {
      return;
    }

    this._pendingRevocations.set(
      kept.length === 0 ? EMPTY_PENDING_REVOCATIONS : Object.freeze(kept),
    );
  }

  /**
   * Whether a generation captured earlier is still the current one. The single question every
   * asynchronous authentication path asks before it commits anything, and the reason it lives here rather
   * than at each call site: the comparison and the counter that feeds it belong to the same owner, so no
   * caller can compare against a number this service did not issue.
   *
   * @param captured The generation read at the moment the work began.
   * @returns True only when no session transition has occurred since.
   */
  isCurrentGeneration(captured: number): boolean {
    return this._generation() === captured;
  }

  /** Advances the generation counter by one. */
  private advanceGeneration(): void {
    this._generation.set(this._generation() + 1);
  }

  /**
   * Whether the access token's absolute expiry has passed. Takes an explicit `now` so the caller controls
   * the clock; a specification can then assert both sides of the boundary without waiting.
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

/** The permission list handed out when no session is held. */
const EMPTY_PERMISSIONS: readonly string[] = Object.freeze([]);

/** The retention set handed out when no withdrawal is outstanding. */
const EMPTY_PENDING_REVOCATIONS: readonly string[] = Object.freeze([]);

/**
 * How many unacknowledged withdrawals are retained at once. Four, and the number is a judgement between
 * two costs that pull in opposite directions.
 */
const MAXIMUM_PENDING_REVOCATIONS = 4;
