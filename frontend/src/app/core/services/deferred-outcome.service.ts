import { Injectable, Injector, effect, inject, untracked } from '@angular/core';
import type { EffectRef, Signal } from '@angular/core';

import { NotificationService } from './notification.service';

/** How a write that outlived its screen turned out. */
export type DeferredOutcome = 'pending' | 'succeeded' | 'failed';

/**
 * How a write that outlived its screen is to be reported when it did NOT succeed. ⚠ DELIBERATELY NOT A
 * `ProblemDetails`, AND THE NARROWNESS IS THE POINT. What must not travel to a screen the operator has
 * moved to is the failure DOCUMENT — its per-field messages have no fields to sit beside and its `detail`
 * describes a form that is no longer open.
 */
export interface DeferredFailureNotice {
  /** One self-contained sentence naming the operation that did not complete. */
  readonly message: string;

  /** The support reference for the failure, or `null` when the failure carried none. */
  readonly reference: string | null;
}

@Injectable({ providedIn: 'root' })
export class DeferredOutcomeService {
  private readonly notifications = inject(NotificationService);

  /**
   * The root environment injector. ⚠ THIS IS THE ENTIRE REASON THIS SERVICE IS A SERVICE and not a helper
   * the components call inline.
   */
  private readonly injector = inject(Injector);

  /**
   * Watches one already-dispatched write and states its outcome once it settles.
   *
   * @param verdict Resolves the outcome from whatever the caller's store publishes.
   * @param describeSuccess Words the confirmation, evaluated only once the verdict is `'succeeded'` so it
   * may read values that arrive with the response.
   * @param describeFailure Words the refusal, evaluated only once the verdict is `'failed'`, so it may
   * read the failure that arrived with the response.
   */
  announceWhenSettled(
    verdict: Signal<DeferredOutcome>,
    describeSuccess: () => string | null,
    describeFailure: () => DeferredFailureNotice | null,
  ): void {
    // ⚠ ONE-SHOT, AND GUARDED TWICE ON PURPOSE. `watcher` releases the effect so it stops consuming the
    // slot it watches, and `reported` is what makes the release SAFE: an effect is scheduled rather than
    // run at creation, so the reference below is assigned before the body can execute - but a body that
    // re-entered before the destruction took hold would otherwise announce twice.
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
            const notice: DeferredFailureNotice | null = describeFailure();

            if (notice === null) {
              return;
            }

            this.notifications.error(notice.message, notice.reference);

            return;
          }

          const message: string | null = describeSuccess();

          if (message === null) {
            return;
          }

          // `survivesNavigation` is deliberately NOT set.
          this.notifications.success(message);
        });
      },
      { injector: this.injector },
    );
  }
}
