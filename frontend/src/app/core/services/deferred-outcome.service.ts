import { Injectable, Injector, effect, inject, untracked } from '@angular/core';
import type { EffectRef, Signal } from '@angular/core';

import { NotificationService } from './notification.service';

/**
 * How a write that outlived its screen turned out.
 *
 * Three members rather than a boolean, because "not yet" is a real answer and the whole purpose of this
 * mechanism is to wait for it. A caller resolves the member from its own store's slots; nothing here
 * inspects a store, a response or a status.
 */
export type DeferredOutcome = 'pending' | 'succeeded' | 'failed';

/**
 * Reports the outcome of a write whose screen has already gone.
 *
 * ⚠ THE PROBLEM THIS EXISTS FOR IS A LIFETIME, NOT A MESSAGE. Every editing screen in this application
 * announces its own outcome, which is correct - the wording belongs to the screen that knows what was
 * attempted, and the stores deliberately publish state without composing sentences. But each of those
 * announcements is made from an `effect` created in the component's own injection context, so it is
 * destroyed WITH the component. An operator who submits and then immediately clicks somewhere else
 * destroys the only party that was going to tell them what happened.
 *
 * That is not a theoretical window. A browser audit measured it on the role creation form: the request
 * completed with `201` and was never aborted, the record really was created, the destination screen was
 * perfectly healthy - and the operator was never told the write had committed. Nothing recovers the
 * statement afterwards, because the outcome is published on a signal slot that nobody is left watching.
 *
 * The screen therefore hands the last step over as it goes: it captures which write it is waiting for and
 * how to word the result, and this service - provided at the application root, so its `Injector` is the
 * ROOT environment injector and an effect created with it lives as long as the application - watches on
 * its behalf and speaks once.
 *
 * ⚠ IT ANNOUNCES AND IT DOES NOT NAVIGATE, and the omission is deliberate rather than incomplete. The
 * operator went somewhere ELSE on purpose; the screen's own success path redirects to a listing, and
 * performing that redirect from here would yank them out of the screen they deliberately chose in order
 * to show them a confirmation. The confirmation alone is what was missing.
 *
 * ⚠ IT IS NOT A SECOND PUBLISHER. Registration happens only where the screen has established that its
 * own bridge will NOT run - it is going out of existence with a write still outstanding - so exactly one
 * of the two speaks for any given write. Registering while a screen is still mounted would double every
 * confirmation.
 */
@Injectable({ providedIn: 'root' })
export class DeferredOutcomeService {
  private readonly notifications = inject(NotificationService);

  /**
   * The root environment injector.
   *
   * ⚠ THIS IS THE ENTIRE REASON THIS SERVICE IS A SERVICE and not a helper the components call inline.
   * A component cannot obtain a root-lived injector for itself: its own node injector dies with it, and
   * the nearest `EnvironmentInjector` may be a lazily loaded route's, which is destroyed when that route
   * configuration is no longer in use - so an effect created with either could be torn down at exactly
   * the moment it is needed. A `providedIn: 'root'` service is constructed in the root environment
   * injector, so what is injected here outlives every screen by construction.
   */
  private readonly injector = inject(Injector);

  /**
   * Watches one already-dispatched write and states its outcome once it settles.
   *
   * @param verdict Resolves the outcome from whatever the caller's store publishes. Called reactively,
   *   so it must read signals and nothing else - it decides, it does not act. `'pending'` is returned
   *   for a write still in flight AND for a settled result belonging to somebody else, which is what
   *   makes the test total without this service knowing anything about the store.
   * @param describeSuccess Words the confirmation, evaluated only once the verdict is `'succeeded'` so
   *   it may read values that arrive with the response. Returning `null` states the outcome silently,
   *   which is how a caller declines to announce a case it does not want announced.
   */
  announceWhenSettled(verdict: Signal<DeferredOutcome>, describeSuccess: () => string | null): void {
    // ⚠ ONE-SHOT, AND GUARDED TWICE ON PURPOSE. `watcher` releases the effect so it stops consuming the
    // slot it watches, and `reported` is what makes the release SAFE: an effect is scheduled rather than
    // run at creation, so the reference below is assigned before the body can execute - but a body that
    // re-entered before the destruction took hold would otherwise announce twice. The flag settles that
    // without depending on the framework's scheduling, which is the kind of assumption that produced a
    // timing-dependent defect elsewhere in this codebase.
    let watcher: EffectRef | null = null;
    let reported = false;

    watcher = effect(
      () => {
        const settled: DeferredOutcome = verdict();

        if (reported || settled === 'pending') {
          return;
        }

        reported = true;

        // The announcement and the release both WRITE, so neither belongs in the tracked body: reading a
        // signal there would make this effect depend on what it had just done.
        untracked(() => {
          watcher?.destroy();

          if (settled === 'failed') {
            // ⚠ A REFUSAL IS DELIBERATELY NOT RELAYED. Every failure this application presents is a
            // document - a title, a detail, per-field messages and the support reference an operator
            // quotes - and its home is the banner ON the screen that attempted the write. That screen is
            // gone, so there is no field for a field message to sit beside and no form to correct; a
            // decontextualised sentence thrown at whatever screen the operator has moved to would tell
            // them something failed without showing them what or letting them fix it. The record is not
            // lost: the store still holds the failure, so returning to the screen presents it in full.
            return;
          }

          const message: string | null = describeSuccess();

          if (message === null) {
            return;
          }

          // `survivesNavigation` is deliberately NOT set. The navigation the operator chose has already
          // completed by the time a write settles after it, so there is no departure left for this
          // statement to outlive - and claiming one would spend the exemption on the operator's NEXT
          // change of screen, leaving the confirmation sitting over a screen it has nothing to do with.
          this.notifications.success(message);
        });
      },
      { injector: this.injector },
    );
  }
}
