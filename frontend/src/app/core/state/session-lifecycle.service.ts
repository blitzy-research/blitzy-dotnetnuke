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
 * `reset()` does both, in that order, and this service's whole job is to make sure every
 * one of them is called, every time, from the three places a session can end:
 *
 *   1. an explicit sign-out, which the application shell performs;
 *   2. a TERMINAL refusal, which `core/interceptors/auth.interceptor.ts` reaches when a
 *      request is refused and no renewal can recover it;
 *   3. an identity or tenant REPLACEMENT, where the credentials remain valid but no longer
 *      describe the same operator or the same portal.
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

import { AuthStore } from './auth.store';
import { ModuleStore } from './module.store';
import { NotificationService } from '../services/notification.service';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { UserStore } from './user.store';

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
   * The four domain stores, in the order their screens are reached.
   *
   * ⚠ THE LIST IS EXHAUSTIVE, AND KEEPING IT SO IS THIS FILE'S STANDING OBLIGATION. A store
   * added to `core/state/` and not added here is a store whose contents survive a sign-out,
   * which is the defect this service exists to prevent rather than a missing nicety. The
   * paired specification asserts the count for exactly that reason.
   *
   * Injected as fields rather than resolved lazily inside {@link endSession}. Resolving them
   * on demand would construct a store during a failing request's error path, which is the
   * worst possible moment to run a constructor, and every one of these is a root singleton
   * that the application has almost certainly built already.
   */
  private readonly portalStore = inject(PortalStore);

  /** @see {@link portalStore} for why every store is injected eagerly. */
  private readonly userStore = inject(UserStore);

  /** @see {@link portalStore} for why every store is injected eagerly. */
  private readonly roleStore = inject(RoleStore);

  /** @see {@link portalStore} for why every store is injected eagerly. */
  private readonly moduleStore = inject(ModuleStore);

  /**
   * The transient-message channel.
   *
   * Cleared with the rest, because a queued notification is session content: it can name a
   * portal, an account or a role the next operator has no right to know exists, and a
   * success message about somebody else's action is confusing even when it discloses
   * nothing.
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
    // Each `reset()` cancels that store's in-flight reads and writes and then returns every
    // slice to the value a freshly constructed store holds. See each store's own reset for
    // why the cancellation has to come first.
    this.portalStore.reset();
    this.userStore.reset();
    this.roleStore.reset();
    this.moduleStore.reset();

    this.notifications.clear();

    // Last, and deliberately: this is the call that makes the application unauthenticated,
    // so everything that could still have issued a request has already been stopped.
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
    return this.authStore.logout().pipe(finalize(() => this.endSession()));
  }
}
