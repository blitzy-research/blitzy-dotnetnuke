/**
 * The one place a session ENDS.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS EXISTS AT ALL
 * ---------------------------------------------------------------------------
 * Every store in `core/state/` is registered at the application root, which means each
 * one outlives every screen and every session: nothing destroys them when an operator
 * signs out, and a signed bearer token cannot be recalled once issued, so "signing out"
 * is entirely a matter of what this application chooses to discard.
 *
 * Discarding the CREDENTIALS alone is not enough, and the gap is a real disclosure rather
 * than a theoretical one. The session store's own sign-out clears the token and the
 * identity; it knows nothing about the portals, accounts, roles, module content, module
 * settings, exported documents or queued notifications that the domain stores are still
 * holding. Without a coordinator those slices stay resident and legible after user A signs
 * out, and user B — signing in on the same browser without a full page reload — sees them.
 * An exported module document is the sharpest case: it is a complete serialisation of a
 * module's content, and it is held in a signal, not in a screen.
 *
 * The same disclosure reaches ACROSS TENANTS, not only across accounts. The API resolves
 * the tenant from the request itself, so a session that resolves to a different portal
 * leaves every previously held row describing a portal the new session cannot see.
 *
 * ---------------------------------------------------------------------------
 * THE INVARIANT THIS FILE OWNS
 * ---------------------------------------------------------------------------
 * ⚠ NO SESSION MAY END WITHOUT EVERY DOMAIN SLICE BEING DISCARDED, AND NO REQUEST ISSUED
 * BY THE OLD SESSION MAY OUTLIVE IT.
 *
 * Both halves are necessary and neither is sufficient. Clearing the slices while requests
 * are still in flight means a response that was already on the wire repopulates exactly
 * what was cleared — the same disclosure with a delay in front of it. Cancelling the
 * requests without clearing the slices leaves the disclosure as it was. So each store's
 * `reset()` does both, in that order, and every one of them must be called, every time, from
 * the three places a session can end:
 *
 *   1. an explicit sign-out, which the application shell performs — the path THIS service
 *      serves;
 *   2. a TERMINAL refusal, which `core/interceptors/auth.interceptor.ts` reaches when a
 *      request is refused and no renewal can recover it;
 *   3. an identity or tenant REPLACEMENT, where the credentials remain valid but no longer
 *      describe the same operator or the same portal.
 *
 * ⚠ THE DISCARD ITSELF IS NOT DUPLICATED HERE. All three paths run one fan-out, owned by
 * `core/state/session-teardown.service.ts`, which this service delegates to. It used to be
 * written out in both files and the two drifted — see {@link SessionLifecycleService.teardown}.
 * What this service still owns is the sign-out path specifically: the revocation request, and
 * the authentication store's own reset, which the fan-out must not perform.
 *
 * ---------------------------------------------------------------------------
 * WHAT IT DELIBERATELY DOES NOT DO
 * ---------------------------------------------------------------------------
 * It does NOT navigate. Where a caller should end up differs by caller — the shell sends
 * the operator to the sign-in screen, the interceptor is mid-way through re-throwing the
 * server's own response and must not have its outcome displaced by a routing failure — so
 * the navigation belongs to the caller and the discard belongs here. A service that did
 * both would force one policy on both callers.
 *
 * It does NOT decide WHETHER a session has ended. That judgement is the caller's: the
 * shell knows the operator asked, and the interceptor knows a renewal was impossible.
 *
 * It holds NO state of its own. There is no "signed out" flag here to disagree with the
 * token custodian, which is the single authority for whether a session is held.
 *
 * MIGRATION: THIS HAS NO LEGACY COUNTERPART, AND THAT IS THE MEASURED SITUATION RATHER
 * THAN AN OMISSION. `FormsAuthentication.SignOut` cleared one cookie and the next request
 * rebuilt every page from scratch on the server, so there was no client-held state to
 * discard and no cross-session residue possible: `Website/admin/Security/roles.ascx` even
 * sets `EnableViewState="False"` outright, and neither `ViewState(` nor `Session(`
 * appears anywhere in `Website/admin/Security/`. Holding state on the client is what this
 * migration introduced, so ending a session cleanly is a responsibility it introduced too,
 * and this file is where that responsibility lives rather than being spread across the
 * screens.
 */

import { Injectable, inject } from '@angular/core';
import { finalize } from 'rxjs';

import { NotificationService } from '../services/notification.service';

import { AuthStore, REVOCATION_FAILED_MESSAGE, SIGNED_OUT_MESSAGE } from './auth.store';
import { SessionTeardownService } from './session-teardown.service';

import type { Observable } from 'rxjs';

/**
 * Ends a session completely, in one call.
 *
 * Registered at the root, like everything it coordinates, so that the shell and the bearer
 * interceptor reach the same instance and cannot end a session two different ways.
 */
@Injectable({ providedIn: 'root' })
export class SessionLifecycleService {
  /**
   * The session store, whose own reset discards the token, the identity and the
   * verification ladder.
   */
  private readonly authStore = inject(AuthStore);

  /**
   * The domain teardown authority.
   *
   * ⚠ THERE IS EXACTLY ONE LIST OF SLICES TO DISCARD, AND IT IS NOT IN THIS FILE.
   * `SessionTeardownService.purge()` holds it — the four tenant stores and the transient
   * message queue — and the bearer interceptor and the session store already reach the same
   * method. Restating that list here would give the application two teardown authorities for
   * one invariant, so that a store added to `core/state/` and enrolled in one of them would
   * survive a sign-out through the other. Keeping the list exhaustive is therefore the
   * teardown service's standing obligation, and this service's obligation is to call it.
   *
   * ⚠ THE DIRECTION MUST NOT BE REVERSED. Having each store enrol itself from its own
   * constructor looks tidier and closes a dependency cycle the injector refuses at run time:
   * the authentication store injects `SessionTeardownService`, that service injects all four
   * stores, and a store that reached back for either service would arrive at the
   * authentication store again. Angular answers that with NG0200 on the first screen that
   * mounts, and no compiler catches it.
   *
   * Injected as a field rather than resolved lazily inside {@link endSession}. Resolving on
   * demand would construct the graph during a failing request's error path, which is the
   * worst possible moment to run a constructor, and every participant is a root singleton the
   * application has almost certainly built already.
   */
  private readonly teardown = inject(SessionTeardownService);

  /**
   * The surface both sign-out statements are raised on.
   *
   * ⚠ THE STATEMENTS BELONG HERE RATHER THAN IN THE STORE, AND THAT PLACEMENT WAS FORCED BY
   * MEASUREMENT. Both were originally raised inside {@link AuthStore.logout}, which is where
   * the facts they report are established — and both were destroyed a few milliseconds later,
   * every time, without ever being rendered legibly. A real browser observing the message
   * region caught the confirmation being added and then removed 10 ms afterwards; a
   * point-in-time read of the region and even a 40 ms poller both reported it absent, because
   * 10 ms is shorter than one display frame.
   *
   * The cause is this file's own {@link SessionLifecycleService.signOut}. It runs
   * {@link SessionLifecycleService.endSession} in a `finalize`, so the teardown happens AFTER
   * the revocation settles — and therefore after the store has already spoken.
   * `SessionTeardownService.purge` empties the message queue outright, and it must: a notice
   * belonging to the previous session can name that operator's records, so a teardown that
   * spared queue entries would leak them to whoever signs in next on the same page load. The
   * reprieve carried by {@link AppNotification.survivesNavigation} cannot help either, because
   * it survives a change of SCREEN and this is a change of SESSION. Counted on the deliberate
   * sign-out path, the queue is emptied THREE times: once by the store's own discard before
   * the statement, then twice more after it — by the purge below and again by
   * {@link AuthStore.reset}'s discard.
   *
   * So the rule this file follows is: the party that speaks must be the party that OUTLIVES the
   * teardown. Nothing purges after this service, which makes it the only correct speaker.
   */
  private readonly notifications = inject(NotificationService);

  /**
   * Discards every trace of the current session from this browser.
   *
   * IDEMPOTENT, and that matters more than it looks: several concurrent requests can be
   * refused at once, so this can be reached repeatedly within a single turn. Each store's
   * reset assigns fixed values to signals and unsubscribes handles it then nulls, so a
   * second call changes nothing and notifies nobody.
   *
   * ORDER: the domain stores first, the session last. Each domain reset cancels its own
   * in-flight requests, and doing that BEFORE the credentials are discarded is what keeps
   * the bearer interceptor from seeing a request without a token and attempting a renewal
   * for a session that is being torn down.
   *
   * NOTHING IS LOGGED. The values in scope when this runs include the credential that was
   * just refused and whatever the domain slices held, and the non-functional requirements
   * exclude sensitive data from structured logging.
   */
  endSession(): void {
    // The domain slices, the queued notices and the in-flight requests that would refill them,
    // discarded by the one owner of that fan-out. Each store's own `reset()` cancels its reads
    // and writes BEFORE returning its slices to their initial values, which is the ordering that
    // makes the discard effective: a response already on the wire would otherwise land afterwards
    // and repopulate precisely what had just been cleared.
    //
    // ⚠ THE REASON IS `signedOut`, AND IT IS RECORDED RATHER THAN INFERRED. This method is
    // reached when the operator asked, which is a different boundary from a renewal the server
    // refused, and the owner publishes which one was crossed so a consumer — a specification most
    // of all — can tell them apart. It does not change what is discarded.
    this.teardown.purge('signedOut');

    // Last, and deliberately: this is the call that makes the application unauthenticated,
    // so everything that could still have issued a request has already been stopped.
    //
    // ⚠ THIS PURGES A SECOND TIME, THROUGH THE STORE'S OWN DISCARD, AND THAT IS HARMLESS BY
    // CONSTRUCTION. The purge is idempotent — every `reset()` it calls writes fixed initial
    // values and cancels handles it then nulls — so the repetition changes nothing and is far
    // cheaper than either owner trying to detect the other's work.
    this.authStore.reset();
  }

  /**
   * Revokes the session server-side, then discards it locally.
   *
   * The single sign-out path the application offers. It exists so that no caller has to
   * remember to pair the revocation with the discard: the discard runs in a `finalize`, so
   * all three exits reach it — a successful revocation, a failed one, and a caller that
   * unsubscribes early. A person who asks to sign out must end up signed out on this device,
   * and leaving the session in place because a revocation request failed would be the exact
   * opposite of what they asked for.
   *
   * MIGRATION: revocation reaches the REFRESH token only. An access token that has already
   * been issued cannot be recalled, which is why its lifetime is short and why the legacy
   * `FormsAuthentication.SignOut` — which cleared a cookie and took effect at once — has no
   * exact counterpart. The server keeps no deny-list and answers 204 whatever it finds.
   *
   * @returns Completion of the revocation attempt. COLD: it must be subscribed for the
   * request to be issued, exactly once. The caller owns that subscription so that it ends
   * with the caller rather than outliving it.
   */
  signOut(): Observable<void> {
    return this.authStore.logout().pipe(
      finalize(() => {
        /*
         * ⚠ READ BEFORE THE TEARDOWN, BECAUSE THE TEARDOWN ERASES IT. The revocation residue is
         * recorded by `AuthStore.logout`'s own error handler, which has already run by the time a
         * `finalize` fires — but `AuthStore.reset`, called from `endSession` below, routes through
         * the store's private discard, and that discard sets this flag back to `false`. Reading it
         * afterwards would therefore report "nothing outstanding" on exactly the runs where
         * something is, and the residue is the one fact about a sign-out an operator may still have
         * to act on. Captured into a local so the ordering is visible rather than implied.
         */
        const revocationOutstanding = this.authStore.revocationOutstanding();

        this.endSession();

        /*
         * ⚠ A DELIBERATE SIGN-OUT CONFIRMS ITSELF, and before this it confirmed itself to nobody.
         * The redirect to the sign-in screen was the only evidence the operator's request had been
         * carried out, and a sign-in form appearing is weak evidence because it is also what a
         * session lapsing on its own produces — two events that want different responses from the
         * person reading the screen. Both live regions were measured empty at zero height on this
         * path, with nothing written to the console either.
         *
         * `'info'` rather than `'success'`: this reports a state the operator asked for, not the
         * outcome of a task that could have gone the other way.
         *
         * DELIBERATELY WORDED DIFFERENTLY from the sentence the bearer interceptor raises when a
         * session ends WITHOUT being asked. One says an instruction was carried out; the other says
         * something happened to them. Sharing a sentence would erase the distinction the two
         * screens exist to draw.
         *
         * THE REPRIEVE IS CORRECT UNDER BOTH ORDERINGS, which is why it is not conditional on the
         * navigation having happened yet. The shell navigates from this observable's `complete`
         * handler, and RxJS delivers `complete` to the subscriber BEFORE finalizing the
         * subscription — so the departure is already under way when this runs, but its
         * `NavigationEnd` (which must load the sign-in route's chunk) has almost certainly not
         * fired. If it fires after this, the reprieve is spent on it and the statement survives
         * onto the sign-in screen, which is where it is meant to be read. If it has somehow
         * already fired, the reprieve is simply never spent — and the next sign-in purges the queue
         * through `purge('signedIn')`, so it cannot outstay its welcome either way.
         */
        this.notifications.info(SIGNED_OUT_MESSAGE, true);

        /*
         * Reported SECOND and as its own statement, never folded into the confirmation above. Both
         * facts are true: the local sign-out completed, which is the part that was asked for, AND a
         * credential was left un-revoked on the server. Retracting the first to report the second
         * would misdescribe what happened, and an operator who reads only one of the two is better
         * served reading that they are signed out.
         *
         * `'warning'` rather than `'error'`: nothing the operator did failed, and the remedy named
         * by the sign-in screen — which renders this same sentence from its own copy — is available
         * to them.
         */
        if (revocationOutstanding) {
          this.notifications.warning(REVOCATION_FAILED_MESSAGE, true);
        }
      }),
    );
  }
}
