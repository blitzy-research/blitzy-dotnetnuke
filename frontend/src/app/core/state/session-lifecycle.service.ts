import { Injectable, inject } from '@angular/core';
import { finalize } from 'rxjs';

import { NotificationService } from '../services/notification.service';

import { AuthStore, REVOCATION_FAILED_MESSAGE, SIGNED_OUT_MESSAGE } from './auth.store';
import { SessionTeardownService } from './session-teardown.service';

import type { Observable } from 'rxjs';

/**
 * Ends a session completely, in one call. Registered at the root, like everything it coordinates, so that
 * the shell and the bearer interceptor reach the same instance and cannot end a session two different
 * ways.
 */
@Injectable({ providedIn: 'root' })
export class SessionLifecycleService {
  /** The session store, whose own reset discards the token, the identity and the verification ladder. */
  private readonly authStore = inject(AuthStore);

  /**
   * The domain teardown authority. ⚠ THERE IS EXACTLY ONE LIST OF SLICES TO DISCARD, AND IT IS NOT IN
   * THIS FILE. `SessionTeardownService.purge()` holds it — the four tenant stores and the transient
   * message queue — and the bearer interceptor and the session store already reach the same method.
   */
  private readonly teardown = inject(SessionTeardownService);

  /**
   * The surface both sign-out statements are raised on. ⚠ THE STATEMENTS BELONG HERE RATHER THAN IN THE
   * STORE, AND THAT PLACEMENT WAS FORCED BY MEASUREMENT. Both were originally raised inside {@link
   * AuthStore.logout}, which is where the facts they report are established — and both were destroyed a
   * few milliseconds later, every time, without ever being rendered legibly.
   */
  private readonly notifications = inject(NotificationService);

  /**
   * Discards every trace of the current session from this browser. IDEMPOTENT, and that matters more than
   * it looks: several concurrent requests can be refused at once, so this can be reached repeatedly
   * within a single turn.
   */
  endSession(): void {
    // The domain slices, the queued notices and the in-flight requests that would refill them, discarded by
    // the one owner of that fan-out.
    this.teardown.purge('signedOut');

    // Last, and deliberately: this is the call that makes the application unauthenticated, so everything
    // that could still have issued a request has already been stopped.
    this.authStore.reset();
  }

  /**
   * Revokes the session server-side, then discards it locally. The single sign-out path the application
   * offers.
   *
   * @returns Completion of the revocation attempt.
   */
  signOut(): Observable<void> {
    return this.authStore.logout().pipe(
      finalize(() => {
        // ⚠ READ BEFORE THE TEARDOWN, BECAUSE THE TEARDOWN ERASES IT. The revocation residue is recorded by
        // `AuthStore.logout`'s own error handler, which has already run by the time a `finalize` fires —
        // but `AuthStore.reset`, called from `endSession` below, routes through the store's private
        // discard, and that discard sets this flag back to `false`.
        const revocationOutstanding = this.authStore.revocationOutstanding();

        this.endSession();

        // ⚠ A DELIBERATE SIGN-OUT CONFIRMS ITSELF, and before this it confirmed itself to nobody.
        this.notifications.info(SIGNED_OUT_MESSAGE, true);

        // Reported SECOND and as its own statement, never folded into the confirmation above. Both facts
        // are true: the local sign-out completed, which is the part that was asked for, AND a credential
        // was left un-revoked on the server.
        if (revocationOutstanding) {
          this.notifications.warning(REVOCATION_FAILED_MESSAGE, true);
        }
      }),
    );
  }
}
