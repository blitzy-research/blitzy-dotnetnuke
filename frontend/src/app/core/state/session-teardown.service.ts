/**
 * Purges every store that holds session-scoped state, as one operation.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHY THIS EXISTS
 *
 * Every store under `core/state/**` is registered `providedIn: 'root'`, so exactly ONE instance of
 * each serves the whole application and each one OUTLIVES every screen that reads it — and, more to
 * the point, outlives the SESSION it was populated for. A single-page application is not reloaded
 * between sign-outs: the same JavaScript context, the same injector and the same store instances carry
 * straight across from one account to the next.
 *
 * Four of those stores had a `reset()` written for exactly this moment, and NOTHING CALLED ANY OF
 * THEM. Signing out cleared the token and the identity projection and left everything else where it
 * was, so the next person to sign in on the same page load inherited, without any action on their
 * part:
 *
 * - the previous account's portal listing, selection, settings and host names;
 * - the previous account's user listing, the account they had open, that account's profile values and
 *   the tenant's membership policy;
 * - the previous account's roles, role groups and the user-to-role assignments joining named accounts
 *   to a named role;
 * - the previous account's module listing, the module they had open, its operator-authored settings
 *   bag, the tenant's page hierarchy, and the EXPORTED MODULE CONTENT — a serialised copy of a
 *   module's data, produced under the previous account's authority and held as a plain string;
 * - and the QUEUED NOTIFICATIONS, which are session content in their own right: a notice reads
 *   "Ann saved a role", naming a person and a record the next operator may have no sight of, and one
 *   that survived the boundary would be presented to them as though it were about their own work.
 *
 * That is one operator's tenant data readable by the next, which is the disclosure this service
 * closes.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHY IT IS A SEPARATE SERVICE AND NOT A METHOD ON THE AUTH STORE
 *
 * The dependency direction. `core/state/auth.store.ts` is imported by the guards, the interceptor and
 * the shell, and the four domain stores are imported by the feature screens. NO DOMAIN STORE IMPORTS
 * THE AUTH STORE — verified, and the property is worth keeping, because a domain store that could
 * reach the auth store could also start making authorization decisions of its own.
 *
 * Putting the fan-out here preserves that. The graph is:
 *
 *     auth.store  ->  session-teardown.service  ->  { portal, user, role, module } stores
 *
 * one-way at every edge, so there is no cycle to break and no forward reference to arrange. The auth
 * store keeps its own state — it is the one store whose state IS the session — and delegates
 * everything else here.
 *
 * ---------------------------------------------------------------------------------------------------
 * WHAT IT DELIBERATELY DOES NOT DO
 *
 * - NO NAVIGATION. Where to send the browser after a session ends is a routing decision belonging to
 *   the caller that ended it; a purge that also navigated would make every caller inherit one
 *   opinion, and the interceptor's silent mid-request teardown does not want it.
 * - NO TOKEN HANDLING. `core/services/token-storage.service.ts` is the sole custodian and the auth
 *   store already clears it. A second clear here would be a second opinion about who owns the token.
 * - NO AUTH-STORE RESET. That would be the cycle. The auth store calls this, not the reverse.
 * - NO HTTP. Revocation belongs to the auth store's sign-out command, which issues it through
 *   `core/services/auth.service.ts`. This purges local state only, which is what lets it run on
 *   paths where the network has already failed.
 *
 * ---------------------------------------------------------------------------------------------------
 * ⚠⚠ WHY THIS IS THE ONE SESSION-BOUNDARY OWNER, AND WHAT WAS CONSOLIDATED INTO IT
 *
 * There were briefly THREE abstractions for one boundary. Two were live: this service, reached from
 * the bearer interceptor's terminal path and from the authentication store; and
 * `session-lifecycle.service.ts`, reached from the application shell's sign-out, which performed the
 * SAME five calls again in its own body. A third, `session.coordinator.ts`, was written to own the
 * boundary properly — it added a monotonic session GENERATION and recorded WHICH boundary had been
 * crossed — and it had ZERO production importers, so none of that ever executed.
 *
 * Three owners for one invariant is worse than one owner with a gap, and the drift was not
 * hypothetical: the lifecycle service cleared the notification queue and this service did not, so an
 * identical session ending left the application in two different states depending on which path
 * reached it, and a notice naming one operator's record was presented to the next. That is recorded
 * on {@link SessionTeardownService.notifications} because it is the concrete evidence for the rule.
 *
 * So the fan-out lives HERE, once. The coordinator's two genuinely useful ideas were folded in —
 * {@link SessionTeardownService.generation} and {@link SessionTeardownService.lastReason} — and the
 * file was deleted rather than left as a fourth opinion nobody called. The lifecycle service now
 * DELEGATES its discard to this member instead of repeating it, keeping only what is genuinely its
 * own: the revocation request and the authentication store's own reset, which this service must not
 * touch because the store calls this one.
 *
 * The one-way graph that makes it work:
 *
 *     auth.store ─────────────┐
 *     auth.interceptor ───────┼──► session-teardown ──► { portal, user, role, module } stores
 *     session-lifecycle ──────┘                     └─► notification service
 *     session-lifecycle ──► auth.store
 *
 * Nothing points back. A domain store that reached for this service would close a cycle through the
 * authentication store, which the injector refuses at runtime with NG0200 and no compiler catches —
 * which is why every store publishes a `reset()` for this service to call rather than enrolling
 * itself.
 */
import { Injectable, computed, inject, signal } from '@angular/core';

import { NotificationService } from '../services/notification.service';
import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { UserStore } from './user.store';

import type { Signal } from '@angular/core';

/**
 * Why a session boundary was crossed.
 *
 * Recorded so that a consumer — a specification most of all — can assert WHICH boundary was crossed
 * rather than only that the generation moved. The four members are the four events that cross one;
 * there is deliberately no fifth, because a boundary nobody has decided the handling for is not one
 * this service should silently accept.
 *
 * ⚠ THE REASON NEVER CHANGES WHAT IS DISCARDED. A partial discard would be a per-caller judgement
 * about which of another operator's rows are acceptable to leave on screen, and there is no safe
 * answer to that. The reason is a record, not a switch.
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
 * The single place a session's local footprint is discarded.
 *
 * Registered at the root so that the instance performing the purge is the same instance every screen
 * reads from; a component-provided copy would purge a store nobody was looking at.
 */
@Injectable({ providedIn: 'root' })
export class SessionTeardownService {
  private readonly portalStore = inject(PortalStore);
  private readonly userStore = inject(UserStore);
  private readonly roleStore = inject(RoleStore);
  private readonly moduleStore = inject(ModuleStore);

  /**
   * The queued notices, cleared with the rest.
   *
   * ⚠ THIS SERVICE AND `session-lifecycle.service.ts` MUST DISCARD THE SAME FOOTPRINT, and the queue
   * is where they had drifted apart. Two entry points end a session: the operator pressing sign out,
   * which runs the lifecycle service, and a renewal the server refuses, which runs this one from the
   * transport layer. The lifecycle service cleared the queue and this one did not, so an identical
   * session ending left an identical application in two different states — the notice survived a
   * terminal refusal and was presented to whoever signed in next. Clearing it here makes the two
   * paths equivalent, which is the only defensible relationship between them.
   */
  private readonly notifications = inject(NotificationService);

  /** Backing state for {@link SessionTeardownService.generation}. */
  private readonly _generation = signal(1);

  /** Backing state for {@link SessionTeardownService.lastReason}. */
  private readonly _lastReason = signal<SessionResetReason | null>(null);

  /**
   * The current session generation, counted from one.
   *
   * ⚠ MONOTONIC AND NEVER REWOUND, so one value identifies one session for the lifetime of the
   * application. Work started under a generation can therefore be recognised as stale by comparing
   * the value it captured against this one, which is a TOTAL test needing no knowledge of what
   * happened in between — unlike "has anything changed?", which cannot distinguish two boundaries
   * from none.
   *
   * It starts at ONE rather than zero so a consumer holding a captured value can express "no session
   * has begun" as zero without needing a nullable field.
   *
   * ⚠ NOT THE TOKEN CUSTODIAN'S EPOCH, AND THE TWO ARE DELIBERATELY SEPARATE.
   * `core/services/token-storage.service.ts` keeps its own generation, advanced by every store and
   * clear, and that is the value the authentication store's own late-callback tests compare against —
   * because those tests are about whether a CREDENTIAL is still current. This one counts LOCAL-STATE
   * boundaries, which is what a consumer holding domain data needs. Collapsing them would tie a
   * question about rows to a question about tokens.
   *
   * Read-only: only {@link SessionTeardownService.purge} moves it, so no consumer can invalidate
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
   * Whether a captured generation is still the one in force.
   *
   * For a caller that began asynchronous work under one session and must decide, when the answer
   * arrives, whether it still belongs to the session that asked. Compared with strict equality on a
   * plain number: no magnitude test, no truthiness test and no tolerance.
   *
   * @param generation The value captured when the work began.
   * @returns True when no boundary has been crossed since.
   */
  isCurrent(generation: number): boolean {
    return this._generation() === generation;
  }

  /**
   * Discards every session-scoped slice in every domain store, and cancels the requests that would
   * otherwise refill them.
   *
   * Each store's own `reset()` releases its in-flight reads and writes BEFORE writing its slices back,
   * which is the ordering that makes this effective at all: a response still in the air when the
   * slices were cleared would arrive afterwards and repopulate precisely what had just been discarded.
   * That failure is worse than performing no purge, because the stores look correctly emptied at the
   * instant of sign-out and refill a moment later with nobody watching.
   *
   * ⚠ IDEMPOTENT, AND CALLED ON EVERY PATH THAT ENDS A SESSION — a deliberate sign-out, a refresh that
   * could not be completed, and the adoption of a different account. Being safe to repeat is what
   * allows those paths to overlap: a refresh failing at the same moment the operator presses sign out
   * runs this twice, and the second run must be a no-op rather than a fault. Every `reset()` it calls
   * writes fixed initial values and cancels handles it then nulls, so repetition changes nothing.
   *
   * Every store is purged unconditionally. There is no attempt to purge only the stores that hold
   * something, because "does this store hold anything?" is a question with twenty-five different
   * answers in one of them, and a purge that consulted them would eventually miss one.
   *
   * ⚠ ORDER IS PART OF THE CONTRACT, AND THE GENERATION MOVES FIRST. Anything that captured the
   * generation before this call can then already tell that it is stale, so a late response cannot be
   * mistaken for a fresh one even in the window before the stores have finished resetting. Each store
   * cancels its own reads and writes as the first act of its own reset, which is why nothing is
   * cancelled here directly — a store knows what it has outstanding and this file must not need to.
   *
   * @param reason Which boundary was crossed. RECORDED and published through
   * {@link SessionTeardownService.lastReason}; it does not change what is discarded.
   * @returns The generation now in force, so a caller starting fresh work can capture it.
   */
  purge(reason: SessionResetReason): number {
    this._generation.update((generation) => generation + 1);
    this._lastReason.set(reason);

    this.portalStore.reset();
    this.userStore.reset();
    this.roleStore.reset();
    this.moduleStore.reset();

    /*
     * Last, and after the stores. A `reset()` publishes no notice of its own, so the order is not
     * load-bearing for correctness — but clearing the queue first would leave a window in which a
     * store's teardown could enqueue something that then outlived the purge, and closing that window
     * costs nothing.
     */
    this.notifications.clear();

    return this._generation();
  }
}
