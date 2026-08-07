/**
 * The one place a session BOUNDARY is enforced across every root-provided feature store.
 *
 * ## The defect this exists to close
 *
 * Every feature store is `providedIn: 'root'`, which is deliberate and correct — a listing,
 * a form and a settings panel must share one selection rather than re-fetching per
 * navigation — but it also means a store outlives the session whose data it holds. Signing
 * out cleared the token custodian and the identity, and nothing else: the portal listing,
 * the module page tree, the account page, the role assignments and every loading flag
 * stayed exactly as the previous operator left them. The next sign-in then rendered those
 * rows immediately, before any request for the new session had settled, with nothing on
 * screen to say whose they were. A tenant change had the same shape: the identity moved,
 * the rows did not.
 *
 * That is a TENANT-ISOLATION defect rather than a tidiness problem, which is why the
 * discard is centralised here instead of being repeated at each call site. Four events
 * cross the boundary and every one of them must be handled identically:
 *
 *   * an identity REPLACEMENT — a sign-in, including one over an existing session;
 *   * a sign-out;
 *   * a terminal renewal failure, which is a sign-out the operator did not choose;
 *   * a TENANT change on the signed-in identity, after which every portal-scoped row held
 *     anywhere belongs to a tenant the caller is no longer acting in.
 *
 * ## What it does, and what it deliberately does not do
 *
 * It cancels and resets. It holds NO session state of its own — no token, no identity, no
 * roles, no permission keys — because `core/services/token-storage.service.ts` owns the
 * session and `core/state/auth.store.ts` owns the identity, and a third holder of either
 * would be a third opinion about who is signed in. It issues no request, performs no
 * navigation and announces nothing: routing belongs to the caller that knows why the
 * session ended, and the failure has already been announced by the time it reaches here.
 *
 * ## Why it injects the stores directly
 *
 * Each store publishes a `reset()` that returns every one of its slices to the state it
 * held before its first request and cancels whatever it had in flight. Injecting the four
 * of them constructs them, which is free: every one is inert until a command is called —
 * no constructor issues a request and not one of them declares an `effect`, which was
 * verified rather than assumed. The alternative, a registration protocol in which a store
 * enrols itself on construction, would reset only the stores that happen to have been
 * touched in the CURRENT session and would therefore miss precisely the case this file
 * exists for: a store populated by the previous operator and not yet touched by this one.
 *
 * ⚠ THE DEPENDENCY DIRECTION IS ONE-WAY AND MUST STAY THAT WAY. This module imports the
 * four feature stores; not one of them imports it, and none imports the session store
 * either — verified across `core/state`. `core/state/auth.store.ts` imports THIS module, so
 * a feature store importing it back would close a cycle through the session store. A
 * feature store never needs to: a store that wants to be reset already publishes the member
 * that does it.
 *
 * MIGRATION: THE LEGACY APPLICATION HAD NO EQUIVALENT PROBLEM AND THEREFORE NO EQUIVALENT
 * SOLUTION, so this file is net-new rather than a port. Screen state lived in `ViewState`
 * and in `Session`, both of which the platform discarded when the forms-authentication
 * cookie went — `Website/release.config:L147` configures that cookie with a sixty-minute
 * timeout — and every page was reassembled from scratch on the next request. Holding
 * client-side state across navigations is what the Signals architecture buys and what
 * obliges this file to exist: the state now survives a sign-out unless something explicitly
 * ends it.
 *
 * MIGRATION: the legacy coarse cache flushes are NOT the ancestor of this member and must
 * not be read as one. `Library/Components/Providers/Caching/DataCache.vb` was flushed
 * portal-wide and host-wide from `PortalController.vb:L916` and `:L1128`, but that was a
 * SERVER cache of records keyed for re-fetching; caching is now a server concern behind
 * `ICacheService`, and nothing here is a cache. There is no key to look an entry up by, no
 * expiry, and no re-population path: a reset discards view state that will be re-read from
 * the API when a screen asks for it again.
 */

import { Injectable, computed, inject, signal } from '@angular/core';
import type { Signal } from '@angular/core';

import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { UserStore } from './user.store';

/**
 * Why a session boundary was crossed.
 *
 * Recorded so that a consumer — a specification most of all — can assert WHICH boundary was
 * crossed rather than only that the generation moved. The four members are the four events
 * enumerated in the file header; there is deliberately no fifth, because a boundary this
 * file does not recognise is one nobody has decided the handling for.
 */
export type SessionResetReason =
  /** A sign-in established, or replaced, the identity. */
  | 'signedIn'
  /** The operator asked to sign out. */
  | 'signedOut'
  /** A renewal was refused, so the session ended without being asked to. */
  | 'renewalRefused'
  /** The signed-in identity's tenant changed. */
  | 'tenantChanged';

/**
 * Coordinates the session generation and the discard of every feature store's state.
 */
@Injectable({ providedIn: 'root' })
export class SessionCoordinator {
  private readonly portals = inject(PortalStore);
  private readonly modules = inject(ModuleStore);
  private readonly users = inject(UserStore);
  private readonly roles = inject(RoleStore);

  /**
   * The current session generation, counted from one.
   *
   * Monotonic and never rewound, so a generation identifies one session for the lifetime of
   * the application. Work started under a generation can therefore be recognised as stale
   * by comparing the value it captured with this one, which is a total test that needs no
   * knowledge of what has happened in between.
   *
   * It starts at ONE rather than zero so that "no session has begun" is expressible as zero
   * by a consumer holding a captured value, without that consumer needing a nullable field.
   */
  private readonly _generation = signal(1);

  /** The reason the most recent boundary was crossed, or `null` before the first one. */
  private readonly _lastReason = signal<SessionResetReason | null>(null);

  /**
   * The generation now in force.
   *
   * Read-only: only the members below may move it, so a consumer cannot silently invalidate
   * everybody else's work.
   */
  readonly generation: Signal<number> = this._generation.asReadonly();

  /** Why the most recent boundary was crossed, or `null` before the first one. */
  readonly lastReason: Signal<SessionResetReason | null> = this._lastReason.asReadonly();

  /**
   * Whether any boundary has been crossed since the application started.
   *
   * Derived rather than stored, so it cannot disagree with {@link lastReason}.
   */
  readonly hasEndedASession: Signal<boolean> = computed(() => this._lastReason() !== null);

  /**
   * Ends the current session generation: cancels every feature store's in-flight work and
   * returns every one of their slices to the state it held before the first request.
   *
   * ⚠ ORDER IS PART OF THE CONTRACT. The generation moves FIRST, so anything that captured
   * it before this call can already tell that it is stale; the stores are reset second, so
   * a late response cannot be mistaken for a fresh one. Each store cancels its own reads and
   * writes as the first act of its own reset, which is why nothing is cancelled here
   * directly — a store knows what it has outstanding and this file must not need to.
   *
   * Idempotent in effect and safe to call from concurrent failures at once: resetting an
   * already-reset store writes the same values again, and the four stores publish no member
   * whose repetition is observable.
   *
   * @param reason Which boundary was crossed. Recorded, and returned to consumers through
   * {@link lastReason}; it does not change what is discarded, because a partial discard
   * would be a per-caller judgement about which of another operator's rows are acceptable
   * to leave on screen, and there is no safe answer to that.
   * @returns The generation now in force, so a caller starting fresh work can capture it.
   */
  reset(reason: SessionResetReason): number {
    this._generation.update((generation) => generation + 1);
    this._lastReason.set(reason);

    this.portals.reset();
    this.modules.reset();
    this.users.reset();
    this.roles.reset();

    return this._generation();
  }

  /**
   * Whether a captured generation is still the one in force.
   *
   * For a caller that began asynchronous work under one session and must decide, when the
   * answer arrives, whether it still belongs to the session that asked. Compared with strict
   * equality on a plain number: no magnitude test, no truthiness test, and no tolerance.
   *
   * @param generation The value captured when the work began.
   * @returns True when no boundary has been crossed since.
   */
  isCurrent(generation: number): boolean {
    return this._generation() === generation;
  }
}
