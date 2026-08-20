/**
 * Purges every store that holds session-scoped state, as one operation. WHY THIS EXISTS Every store under
 * `core/state/**` is registered `providedIn: 'root'`, so exactly ONE instance of each serves the whole
 * application and each one OUTLIVES every screen that reads it — and, more to the point, outlives the
 * SESSION it was populated for.
 */
import { Injectable, computed, inject, signal } from '@angular/core';

import { NotificationService } from '../services/notification.service';
import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { UserStore } from './user.store';

import type { Signal } from '@angular/core';

/**
 * Why a session boundary was crossed. Recorded so that a consumer — a specification most of all — can
 * assert WHICH boundary was crossed rather than only that the generation moved.
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
 * What an operator is told when their session ends without their asking. ⚠ AUTHORED, WITH NO LEGACY
 * COUNTERPART TO REPRODUCE. Forms authentication expired a cookie silently —
 * `Website/release.config:L147` declares `<forms name=".DOTNETNUKE" protection="All" timeout="60"
 * cookieless="UseCookies"/>` — and the next request was simply redirected to the sign-in page with
 * nothing said.
 */
export const SESSION_ENDED_MESSAGE = 'Your session has ended. Please sign in again to continue.';

/** The single place a session's local footprint is discarded. */
@Injectable({ providedIn: 'root' })
export class SessionTeardownService {
  private readonly portalStore = inject(PortalStore);
  private readonly userStore = inject(UserStore);
  private readonly roleStore = inject(RoleStore);
  private readonly moduleStore = inject(ModuleStore);

  /**
   * The queued notices — cleared with the rest, and the surface an unasked-for ending is explained
   * through. The second role follows from the first rather than being bolted onto it.
   */
  private readonly notifications = inject(NotificationService);

  /** Backing state for {@link SessionTeardownService.generation}. */
  private readonly _generation = signal(1);

  /** Backing state for {@link SessionTeardownService.lastReason}. */
  private readonly _lastReason = signal<SessionResetReason | null>(null);

  /**
   * The current session generation, counted from one. ⚠ MONOTONIC AND NEVER REWOUND, so one value
   * identifies one session for the lifetime of the application.
   */
  readonly generation: Signal<number> = this._generation.asReadonly();

  /** Why the most recent boundary was crossed, or `null` before the first one. */
  readonly lastReason: Signal<SessionResetReason | null> = this._lastReason.asReadonly();

  /** Whether any boundary has been crossed since the application started. */
  readonly hasEndedASession: Signal<boolean> = computed(() => this._lastReason() !== null);

  /**
   * Whether a captured generation is still the one in force.
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
   * @param reason Which boundary was crossed.
   * @returns The generation now in force, so a caller starting fresh work can capture it.
   */
  purge(reason: SessionResetReason): number {
    this._generation.update((generation) => generation + 1);
    this._lastReason.set(reason);

    this.portalStore.reset();
    this.userStore.reset();
    this.roleStore.reset();
    this.moduleStore.reset();

    // Last, and after the stores. A `reset()` publishes no notice of its own, so the order is not
    // load-bearing for correctness — but clearing the queue first would leave a window in which a store's
    // teardown could enqueue something that then outlived the purge, and closing that window costs nothing.
    this.notifications.clear();

    // ⚠ SAY WHY, BUT ONLY FOR THE ENDING NOBODY ASKED FOR. Measured in a browser: the teardown itself was
    // complete and correct — credential cleared, every domain slice emptied, the operator returned to the
    // sign-in screen with no residue — and both live regions were EMPTY, so somebody mid-task was returned
    // to a sign-in form with no account, no work and no explanation, and a non-visual operator had nothing
    // at all to go on.
    if (reason === 'renewalRefused') {
      this.notifications.warning(SESSION_ENDED_MESSAGE);
      this.notifications.retainAcrossNavigation();
    }

    return this._generation();
  }
}
