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
 * - NO HTTP. Revocation is `core/services/auth.service.ts`'s job. This purges local state only, which
 *   is what lets it run on paths where the network has already failed.
 */
import { Injectable, inject } from '@angular/core';

import { NotificationService } from '../services/notification.service';
import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { UserStore } from './user.store';

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
   */
  purge(): void {
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
  }
}
