/**
 * Specification for `core/state/session-teardown.service.ts`.
 *
 * The service itself is four calls long, so almost nothing here is about its body. What is proven is
 * the set of properties that make those four calls WORTH MAKING, each of which is invisible to the
 * compiler:
 *
 * 1. It reaches EVERY domain store. A store added later and not wired in would be silently retained
 *    across a sign-out, so the reach is asserted store by store rather than in aggregate.
 * 2. It purges state that was genuinely populated, including the slices no `clear*` member touches —
 *    most consequentially the exported module content, a serialised copy of a module's data held as a
 *    plain string.
 * 3. It is IDEMPOTENT, because the paths that call it overlap: a renewal failing at the same moment
 *    the operator presses sign out runs it twice, and the second run must be a no-op rather than a
 *    fault.
 * 4. It defeats the repopulation race. A read still in the air when the purge runs must NOT be allowed
 *    to land afterwards and refill the slices, which is the failure mode that makes an unguarded reset
 *    worse than none at all: the stores look correctly emptied at the instant of sign-out and refill a
 *    moment later with nobody watching.
 * 5. It does NOT navigate, does NOT touch the token custodian and does NOT issue a request, because
 *    each of those belongs to a different owner and a second opinion here would be a second answer.
 * 6. The stores it purges are the SAME INSTANCES the screens read, which is what makes the purge
 *    observable at all — a component-provided copy would empty a store nobody was looking at.
 *
 * The legacy tree contains no automated test of any kind, so nothing here is ported; it is authored
 * against the store sources and the disclosure they permitted.
 */
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { NotificationService } from '../services/notification.service';

import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { SESSION_ENDED_MESSAGE, SessionTeardownService } from './session-teardown.service';
import { UserStore } from './user.store';

import { ModuleVisibility } from '../models/module.model';
import type { ModuleListItem } from '../models/module.model';
import type { PagedResult } from '../models/paged-result.model';

/** The listing path, asserted whole so a change of route is a failure here rather than a surprise. */
const MODULES_URL = '/api/v1/modules';

/** The role listing path. */
const ROLES_URL = '/api/v1/roles';

/** The account listing path. */
const USERS_URL = '/api/v1/users';

/**
 * The paging envelope the server sends, built around the rows given.
 *
 * @param items The rows to carry.
 * @param pageIndex The coordinate to report.
 * @returns The envelope to flush.
 */
function pagedBody(
  items: readonly ModuleListItem[],
  pageIndex = 0,
): PagedResult<ModuleListItem> {
  return {
    items,
    meta: {
      totalCount: items.length,
      pageIndex,
      pageSize: 10,
      totalPages: items.length === 0 ? 0 : 1,
    },
  };
}

/**
 * One row of the module listing, with every member the contract declares.
 *
 * The whole row is built rather than a partial cast, so that a change to the contract fails here
 * instead of being hidden behind an assertion.
 *
 * @param overrides The members to vary.
 * @returns A complete listing row.
 */
function listRow(overrides: Partial<ModuleListItem> = {}): ModuleListItem {
  return {
    moduleId: 11,
    tabModuleId: 1,
    tabId: 0,
    moduleDefId: 4,
    moduleTitle: 'Announcements',
    friendlyName: 'Announcements',
    desktopModuleId: 2,
    moduleName: 'Announcements',
    description: '',
    version: '01.00.00',
    moduleOrder: 1,
    allTabs: false,
    visibility: ModuleVisibility.Maximized,
    isDeleted: false,
    displayTitle: true,
    startDate: null,
    endDate: null,
    ...overrides,
  };
}

describe('SessionTeardownService', () => {
  let teardown: SessionTeardownService;
  let portalStore: PortalStore;
  let userStore: UserStore;
  let roleStore: RoleStore;
  let moduleStore: ModuleStore;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // ⚠ ORDER IS LOAD-BEARING: the mock backend REPLACES the backend the first provider
        // installed, so reversing these two leaves the real one in place and every expectation
        // below fails in a way that reads like a defect in the service.
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    teardown = TestBed.inject(SessionTeardownService);
    portalStore = TestBed.inject(PortalStore);
    userStore = TestBed.inject(UserStore);
    roleStore = TestBed.inject(RoleStore);
    moduleStore = TestBed.inject(ModuleStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Every request dispatched below is deliberately left UNANSWERED, because a cancelled
    // request is the whole point of several specifications here. `verify` would fail on those,
    // so outstanding requests are discarded rather than asserted against.
    httpMock.verify({ ignoreCancelled: true });
  });

  // -----------------------------------------------------------------------------------------------
  // REACH
  // -----------------------------------------------------------------------------------------------

  it('purges every domain store', () => {
    const portalReset = spyOn(portalStore, 'reset').and.callThrough();
    const userReset = spyOn(userStore, 'reset').and.callThrough();
    const roleReset = spyOn(roleStore, 'reset').and.callThrough();
    const moduleReset = spyOn(moduleStore, 'reset').and.callThrough();

    teardown.purge('signedOut');

    expect(portalReset).withContext('the portal store is purged').toHaveBeenCalledTimes(1);
    expect(userReset).withContext('the user store is purged').toHaveBeenCalledTimes(1);
    expect(roleReset).withContext('the role store is purged').toHaveBeenCalledTimes(1);
    expect(moduleReset).withContext('the module store is purged').toHaveBeenCalledTimes(1);
  });

  it('purges the stores the screens actually read, not private copies of them', () => {
    // The purge is only observable if the instance it empties is the instance a screen holds.
    // Both are resolved from the root injector, so this asserts the registration rather than
    // the service: a store that had been provided at a component would fail here.
    expect(TestBed.inject(PortalStore)).toBe(portalStore);
    expect(TestBed.inject(UserStore)).toBe(userStore);
    expect(TestBed.inject(RoleStore)).toBe(roleStore);
    expect(TestBed.inject(ModuleStore)).toBe(moduleStore);
  });

  it('is idempotent, because the paths that call it overlap', () => {
    const moduleReset = spyOn(moduleStore, 'reset').and.callThrough();

    expect(() => {
      teardown.purge('signedOut');
      teardown.purge('signedOut');
      teardown.purge('signedOut');
    })
      .withContext('a repeated purge is a no-op rather than a fault')
      .not.toThrow();

    expect(moduleReset).toHaveBeenCalledTimes(3);
  });

  // -----------------------------------------------------------------------------------------------
  // WHAT IS ACTUALLY DISCARDED
  // -----------------------------------------------------------------------------------------------

  it('discards a populated module listing, its tenant page hierarchy and its query coordinate', () => {
    moduleStore.setPageIndex(3);
    moduleStore.loadModules();

    // Answered so the store genuinely HOLDS rows before the purge; proving that an
    // already-empty store is empty would prove nothing.
    httpMock.expectOne((request) => request.url === MODULES_URL).flush(
      pagedBody([listRow()], 3),
    );

    moduleStore.loadTabs(7);
    httpMock.expectOne((request) => request.url === `/api/v1/portals/7/tabs`).flush({
      // The COMPLETE published shape. The tab decoder validates every declared member and
      // fails the read on a partial body, so a two-member fixture would leave the slice empty
      // and this specification's precondition would be about the fixture rather than the purge.
      data: [
        {
          tabId: 4,
          tabName: 'Home',
          title: null,
          tabOrder: 1,
          parentId: null,
          level: 0,
          tabPath: '//Home',
          isVisible: true,
          disableLink: false,
          isDeleted: false,
          hasChildren: false,
          isSecure: false,
          url: null,
          iconFile: null,
        },
      ],
    });

    expect(moduleStore.modules().length)
      .withContext('precondition: rows are genuinely held')
      .toBe(1);
    expect(moduleStore.tabs().length)
      .withContext('precondition: the page hierarchy is genuinely held')
      .toBe(1);

    teardown.purge('signedOut');

    expect(moduleStore.modules()).withContext('no rows survive').toEqual([]);
    expect(moduleStore.tabs()).withContext('no tenant page hierarchy survives').toEqual([]);
    expect(moduleStore.tabPortalId())
      .withContext('the tenant the hierarchy was read for is forgotten too')
      .toBeUndefined();
    expect(moduleStore.query().pageIndex)
      .withContext('the query coordinate returns to the first page')
      .toBe(0);
  });

  it('discards the exported module content, which no clear member touched', () => {
    // ⚠ THE MOST CONSEQUENTIAL SLICE IN ANY OF THESE STORES. It is a serialised copy of a
    // module's data, produced under the authority of the account that is signing out, held as a
    // plain string in a root-provided store. Before this service existed nothing cleared it, so
    // a download affordance could hand it to whoever signed in next on the same page load.
    moduleStore.exportModule(11, { fileName: 'Announcements', folder: '' });

    httpMock
      .expectOne((request) => request.url === `${MODULES_URL}/11/export`)
      .flush('<content><secret>tenant data</secret></content>');

    expect(moduleStore.exportedContent())
      .withContext('precondition: the export is genuinely held before the purge')
      .not.toBeNull();

    teardown.purge('signedOut');

    expect(moduleStore.exportedContent())
      .withContext('the export does not survive the session that produced it')
      .toBeNull();
    expect(moduleStore.exporting()).withContext('and the transfer is at rest').toBeFalse();
  });

  it('returns every loading and saving flag to rest, so no screen is left spinning', () => {
    // Dispatched and deliberately never answered: the flags are raised and the purge is the
    // only thing that can lower them, since the response never arrives.
    moduleStore.loadModules();
    roleStore.loadRoles();
    userStore.loadUsers();

    teardown.purge('signedOut');

    expect(moduleStore.listLoading()).withContext('the module listing is at rest').toBeFalse();
    expect(moduleStore.saving()).withContext('no module write is reported in flight').toBeFalse();
    expect(roleStore.rolesLoading()).withContext('the role listing is at rest').toBeFalse();
    expect(userStore.usersLoading()).withContext('the user listing is at rest').toBeFalse();
  });

  // -----------------------------------------------------------------------------------------------
  // THE REPOPULATION RACE
  // -----------------------------------------------------------------------------------------------

  it('cancels a module read in flight, so its answer cannot refill the store after the purge', () => {
    moduleStore.loadModules();

    const inFlight = httpMock.expectOne((request) => request.url === MODULES_URL);

    teardown.purge('signedOut');

    expect(inFlight.cancelled)
      .withContext('the read is cancelled rather than merely ignored')
      .toBeTrue();

    // Belt and braces: even if the transport delivered anyway, nothing may be committed.
    if (!inFlight.cancelled) {
      inFlight.flush(pagedBody([listRow()]));
    }

    expect(moduleStore.modules())
      .withContext('a superseded answer does not repopulate a purged store')
      .toEqual([]);
  });

  it('cancels a role read in flight, so its answer cannot refill the store after the purge', () => {
    roleStore.loadRoles();

    const inFlight = httpMock.expectOne((request) => request.url === ROLES_URL);

    teardown.purge('signedOut');

    expect(inFlight.cancelled).withContext('the role read is cancelled').toBeTrue();
    expect(roleStore.roles().items).toEqual([]);
    expect(roleStore.rolesLoading())
      .withContext('cancelling does not leave the flag raised')
      .toBeFalse();
  });

  it('cancels a user read in flight, so its answer cannot refill the store after the purge', () => {
    userStore.loadUsers();

    const outstanding = httpMock.match(() => true);

    teardown.purge('signedOut');

    for (const request of outstanding) {
      expect(request.cancelled)
        .withContext(`the read of ${request.request.url} is cancelled`)
        .toBeTrue();
    }

    expect(userStore.users().items).toEqual([]);
    expect(userStore.selectedUser()).toBeNull();
  });

  // -----------------------------------------------------------------------------------------------
  // WHAT IT DELIBERATELY DOES NOT DO
  // -----------------------------------------------------------------------------------------------

  it('issues no request of its own', () => {
    teardown.purge('signedOut');

    // Any request at all would mean the purge had an opinion about the network. Revocation
    // belongs to the auth store's sign-out command, which issues it through
    // `core/services/auth.service.ts`. `match` is used rather than `expectNone` so the
    // failure message names what was sent.
    expect(httpMock.match(() => true).map((request) => request.request.url))
      .withContext('purging local state contacts nobody')
      .toEqual([]);
  });

  it('does not navigate, because where to go next belongs to the caller', () => {
    const router = TestBed.inject(Router);
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);

    teardown.purge('signedOut');

    expect(navigate)
      .withContext('the interceptor and the shell each choose their own destination')
      .not.toHaveBeenCalled();
  });

  // =========================================================================
  // EXPLAINING AN ENDING NOBODY ASKED FOR
  //
  // ⚠ THE MEASUREMENT THAT PUT THESE CASES HERE. Driven in a real browser: a session whose renewal
  // credential had been revoked was torn down PERFECTLY — credential cleared, every domain slice
  // emptied, the operator returned to the sign-in screen with no residue in storage or cookies — and
  // both live regions were EMPTY. Somebody mid-task therefore reached a sign-in form with no account,
  // no work and no explanation, and a non-visual operator had nothing at all to go on.
  //
  // ⚠ AND WHY THE SENTENCE IS RAISED HERE RATHER THAN BY THE CALLERS. Two paths end a session without
  // the operator asking, and a browser found the second one only after the first had been fixed: the
  // bearer interceptor's terminal path, when a refused request cannot be recovered, and the
  // authentication store's discard, when the renewal a NAVIGATION GATE asked for is refused. The
  // second reached the operator with the renewal's own problem document instead — "Unauthorized" over
  // "The refresh token is not valid." over a bare correlation identifier — which is an account of a
  // credential they never knew existed. One sentence raised from two files could not have covered
  // both and would have drifted; raised from the point both already pass through, it covers any
  // future third path by construction.
  // =========================================================================
  describe('explaining an ending nobody asked for', () => {
    let notifications: NotificationService;

    beforeEach(() => {
      notifications = TestBed.inject(NotificationService);
    });

    it('tells the operator the session has ended when a renewal was refused', () => {
      teardown.purge('renewalRefused');

      const queued = notifications.notifications();

      expect(queued.length).withContext('one ending, one notice').toBe(1);
      expect(queued[0]?.message).toBe(SESSION_ENDED_MESSAGE);
      // A warning and not an error: nothing failed on the operator's part, they were simply away
      // longer than a credential lives. The severity also carries the longer on-screen lifetime a
      // message read on ARRIVAL at another screen needs.
      expect(queued[0]?.severity).toBe('warning');
    });

    it('words the ending for a person, naming no token, status or identifier', () => {
      // The regression this guards is a specific one: passing the renewal's own problem document
      // through to the sign-in screen, which is what an operator was shown before the sentence
      // existed. Every word of that document was true and none of it was usable.
      teardown.purge('renewalRefused');

      const message = notifications.notifications()[0]?.message ?? '';

      expect(message.toLowerCase()).not.toContain('token');
      expect(message.toLowerCase()).not.toContain('unauthorized');
      expect(message).not.toMatch(/\b40\d\b/);
      expect(message)
        .withContext('and it says what happened and what to do about it')
        .toBe(SESSION_ENDED_MESSAGE);
    });

    it('exempts the notice from the navigation sweep it is raised alongside', () => {
      // ⚠ WITHOUT THIS THE MESSAGE WOULD BE RAISED AND NEVER SEEN. The shell clears transient
      // notices on a completed navigation, and this notice exists precisely to accompany the
      // navigation to the sign-in screen — so it has to outlive the very sweep that navigation
      // triggers.
      teardown.purge('renewalRefused');

      notifications.clearOnNavigation();

      expect(notifications.notifications().map((entry) => entry.message))
        .withContext('it survives arriving at the screen it explains')
        .toEqual([SESSION_ENDED_MESSAGE]);
    });

    it('raises it AFTER emptying the queue, so the clearing cannot erase it', () => {
      // ⚠ THE ORDER IS THE WHOLE MECHANISM. Clearing empties the queue AND the exemption set, so a
      // notice raised before it is destroyed by it — which is exactly what happened to the wording
      // the authentication store raises from inside the renewal, and the reason the sentence could
      // not simply be added there.
      notifications.success('A record you will never see again was saved.');
      notifications.error('And a fault belonging to the session that is ending.');

      expect(notifications.notifications().length).toBe(2);

      teardown.purge('renewalRefused');

      expect(notifications.notifications().map((entry) => entry.message))
        .withContext("the previous operator's notices go, and only the ending is left")
        .toEqual([SESSION_ENDED_MESSAGE]);
    });

    it('says nothing for an ending the operator asked for, or chose', () => {
      // The negative half, and the reason the reason is consulted at all. Announcing any of these
      // would tell somebody something they had just done.
      for (const reason of ['signedOut', 'signedIn', 'tenantChanged'] as const) {
        notifications.clear();

        teardown.purge(reason);

        expect(notifications.notifications())
          .withContext(`${reason} is a boundary the operator crossed deliberately`)
          .toEqual([]);
      }
    });

    it('still discards exactly the same footprint whichever reason it is given', () => {
      // ⚠ THE INVARIANT THE ANNOUNCEMENT MUST NOT HAVE WEAKENED. The reason decides whether the
      // operator is TOLD; it must never decide what is discarded, because a partial discard is a
      // per-caller judgement about which of somebody else's rows are acceptable to leave on screen.
      const generations: number[] = [];

      for (const reason of ['signedOut', 'renewalRefused', 'signedIn', 'tenantChanged'] as const) {
        generations.push(teardown.purge(reason));

        expect(moduleStore.modules()).toEqual([]);
        expect(portalStore.portals()).toEqual([]);
        expect(teardown.lastReason()).toBe(reason);
      }

      // And the generation advances by exactly one per crossing, whatever was said about it — an
      // announcement must not cost a generation, and a silence must not save one. Asserted as
      // successive DIFFERENCES rather than absolute values, because the starting value belongs to
      // whatever this suite did beforehand and is not this case's business.
      const steps: number[] = generations
        .slice(1)
        .map((value, index) => value - (generations[index] ?? 0));

      expect(steps).toEqual([1, 1, 1]);
    });
  });

  it('survives being called before any store has read anything', () => {
    // The teardown paths do not know whether a screen was ever opened, so the purge must be
    // safe on a completely cold application — this is the state after a failed first sign-in.
    expect(() => {
      teardown.purge('signedOut');
    }).not.toThrow();

    expect(moduleStore.modules()).toEqual([]);
    expect(portalStore.portals()).toEqual([]);
  });
});
