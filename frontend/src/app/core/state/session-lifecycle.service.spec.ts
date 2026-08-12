/**
 * Specification for `core/state/session-lifecycle.service.ts`.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS FILE IS ACTUALLY PROVING
 * ---------------------------------------------------------------------------
 * One property, stated once and asserted from several directions:
 *
 *   ⚠ NO SESSION MAY END WITHOUT EVERY DOMAIN SLICE BEING DISCARDED, AND NO REQUEST ISSUED
 *   BY THE OLD SESSION MAY OUTLIVE IT.
 *
 * The reason it needs a specification of its own rather than being folded into the stores'
 * is that the defect it prevents is INVISIBLE from inside any single store. Each store's
 * `reset()` can be correct while a session still leaks, because the leak is a store nobody
 * called. So the central case here is the cross-session one: real data is loaded into every
 * store as operator A, the session ends, and nothing operator A could see remains legible.
 *
 * ---------------------------------------------------------------------------
 * WHY REAL STORES AND A REAL HTTP BACKEND, NOT MOCKS
 * ---------------------------------------------------------------------------
 * A mocked store would prove that `reset()` was CALLED, which is the weaker of the two
 * statements and the one that cannot fail interestingly. It could not prove that the reset
 * actually empties the slice a screen reads, and it could not prove the cancellation half at
 * all - a spy has no in-flight request to abandon. Every store here is therefore the real
 * one, and every request is answered through the testing backend, so each assertion is made
 * against the state a component would genuinely render.
 *
 * `httpMock.verify()` runs after every case and is load-bearing rather than tidy: it fails
 * the case if any request was issued that no expectation accounted for, which is how "the
 * old session's requests did not outlive it" is proved rather than asserted.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AuthStore, REVOCATION_FAILED_MESSAGE, SIGNED_OUT_MESSAGE } from './auth.store';
import { ModuleStore } from './module.store';
import { NotificationService } from '../services/notification.service';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { SessionLifecycleService } from './session-lifecycle.service';
import { TokenStorageService } from '../services/token-storage.service';
import { UserStore } from './user.store';

import type { TestRequest } from '@angular/common/http/testing';
import type { AuthSession } from '../models/auth.model';

// ---------------------------------------------------------------------------
// FIXTURES
// ---------------------------------------------------------------------------

/**
 * An expiry comfortably ahead of whenever this suite runs, derived from the clock rather than written
 * down.
 *
 * ⚠ AN ABSOLUTE DATE IS A TEST THAT EXPIRES. This fixture used to carry `2030-01-01T00:00:00Z`,
 * which holds a session valid by the calendar rather than by anything the specification controls: on the
 * first of January 2030 every case depending on it begins asserting the opposite of what it was written
 * to assert, and it does so SILENTLY, because a session read as already expired is a state this
 * application handles rather than an error it reports.
 *
 * One hour is longer than any run of this suite and shorter than any window the application treats as
 * unusual, and it is computed ONCE per module load so every case in the file shares one instant rather
 * than racing the clock between them.
 */
const FUTURE_SESSION_EXPIRY_UTC: string = new Date(Date.now() + 60 * 60 * 1000).toISOString();

/**
 * A credential pair, shaped as the sign-in endpoint shapes one.
 *
 * The values are obvious placeholders rather than anything resembling a real token: nothing
 * here is parsed, and a value that looked like a credential would invite somebody to try.
 */
const SESSION_BODY: AuthSession = {
  accessToken: 'operator-a-access-token',
  refreshToken: 'operator-a-refresh-token',
  expiresAtUtc: FUTURE_SESSION_EXPIRY_UTC,
  mustChangePassword: false,
  mustUpdateProfile: false,
  passwordExpiring: false,
  user: {
    userId: 0,
    portalId: -1,
    portalName: 'Operator A Portal',
    username: 'operator.a',
    displayName: 'Operator A',
    email: 'operator.a@example.test',
    isSuperUser: false,
    isPortalAdministrator: false,
    roles: ['Administrators'],
    permissions: ['EDIT'],
  },
};

describe('SessionLifecycleService', () => {
  let service: SessionLifecycleService;
  let httpMock: HttpTestingController;
  let tokens: TokenStorageService;
  let authStore: AuthStore;
  let portals: PortalStore;
  let users: UserStore;
  let roles: RoleStore;
  let modules: ModuleStore;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()`
      // REPLACES the backend the real client installed, so reversing the two would leave the
      // live backend in place and every expectation below would find nothing.
      //
      // No interceptor is registered. The bearer credential and the correlation identifier
      // are attached by the two functional interceptors wired at application configuration,
      // and running that chain here would mean asserting several units at once - the
      // interceptor's own use of this service is asserted in the interceptor's specification.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(SessionLifecycleService);
    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    portals = TestBed.inject(PortalStore);
    users = TestBed.inject(UserStore);
    roles = TestBed.inject(RoleStore);
    modules = TestBed.inject(ModuleStore);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** Claims the one open request at a path, whatever its method. */
  function expectOne(url: string): TestRequest {
    return httpMock.expectOne((candidate) => candidate.url === url);
  }

  /**
   * Establishes a held session, exactly as the sign-in flow would.
   *
   * Written through the token custodian rather than by posting credentials, because this
   * specification is about ENDING a session and staging one through the sign-in endpoint would
   * add a request every case then has to account for.
   */
  function holdSession(): void {
    tokens.store(SESSION_BODY);
  }

  /**
   * Loads one real record into each of the four domain stores and queues a notification.
   *
   * Every answer is flushed, so each store ends in the state a screen would render rather
   * than in a half-loaded one - which is what makes the assertions afterwards meaningful.
   */
  function loadOperatorAData(): void {
    portals.loadPortals();
    expectOne('/api/v1/portals').flush({
      items: [
        {
          portalId: -1,
          portalName: 'Operator A Portal',
          aliases: ['localhost'],
          users: 3,
          pages: 12,
          hostSpace: 0,
          hostFee: 0,
          expiryDate: null,
        },
      ],
      meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
    });

    // The unfiltered listing, which is the one command that really does ask the server for
    // every account. The filtered read deliberately dispatches nothing until a search has been
    // chosen, so it would leave this store empty and prove nothing about the discard.
    users.showAllAccounts();
    expectOne('/api/v1/users').flush({
      items: [
        {
          userId: 7,
          portalId: -1,
          username: 'ann.admin',
          firstName: 'Ann',
          lastName: 'Admin',
          displayName: 'Ann Admin',
          address: null,
          telephone: null,
          email: 'ann.admin@example.invalid',
          createdDate: '2024-01-05T09:15:00Z',
          lastLoginDate: null,
          isApproved: true,
          isOnline: false,
          isSuperUser: false,
          isLockedOut: false,
          canDelete: true,
        },
      ],
      meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
    });

    roles.loadRoles();
    expectOne('/api/v1/roles').flush({
      items: [
        {
          roleId: 0,
          portalId: -1,
          roleGroupId: null,
          roleName: 'Operator A Administrators',
          description: 'Held before the session ended',
          isPublic: false,
          autoAssignment: false,
          serviceFee: 0,
          billingFrequency: 'N',
          billingPeriod: null,
          trialFee: null,
          trialPeriod: null,
          trialFrequency: null,
        },
      ],
      meta: { totalCount: 1, pageIndex: 0, pageSize: 100, totalPages: 1 },
    });

    modules.loadModules();
    expectOne('/api/v1/modules').flush({
      items: [
        {
          moduleId: 0,
          tabModuleId: 0,
          tabId: 0,
          portalId: -1,
          moduleDefId: 1,
          moduleTitle: 'Operator A Announcements',
          moduleOrder: 1,
          paneName: 'ContentPane',
          allTabs: false,
          visibility: 0,
          isDeleted: false,
          displayTitle: true,
          startDate: null,
          endDate: null,
          friendlyName: 'Announcements',
          desktopModuleId: 2,
          moduleName: 'Announcements',
          description: null,
          version: '01.00.00',
        },
      ],
      meta: { totalCount: 1, pageIndex: 0, pageSize: 10, totalPages: 1 },
    });

    notifications.notify('success', 'Operator A saved a role');
  }

  // -------------------------------------------------------------------------
  // THE CROSS-SESSION PROPERTY
  // -------------------------------------------------------------------------

  describe('ending a session discards everything the previous operator could see', () => {
    it('leaves no domain record, no notification and no credential behind', () => {
      holdSession();
      loadOperatorAData();

      // Everything is genuinely loaded first, so the assertions afterwards are about a
      // discard rather than about a store that was empty all along.
      expect(portals.portals().length).toBe(1);
      expect(users.userRows().length).toBe(1);
      expect(roles.roleItems().length).toBe(1);
      expect(modules.modules().length).toBe(1);
      expect(notifications.notifications().length).toBe(1);
      expect(authStore.isAuthenticated()).toBeTrue();

      service.endSession();

      // ⚠ THE DEFECT THIS PREVENTS IS A DISCLOSURE, NOT UNTIDINESS. These stores are
      // root-provided, so they outlive the session; anything surviving here is legible to
      // whoever signs in next on this browser without a full page reload.
      expect(portals.portals())
        .withContext('one operator portals must not be visible to the next')
        .toEqual([]);
      expect(users.userRows())
        .withContext('account rows carry names and e-mail addresses')
        .toEqual([]);
      expect(roles.roleItems()).toEqual([]);
      expect(modules.modules()).toEqual([]);
      expect(notifications.notifications())
        .withContext('a queued message can name a portal, an account or a role')
        .toEqual([]);

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(tokens.accessToken()).toBeNull();
      expect(tokens.refreshToken()).toBeNull();
      expect(authStore.currentUser()).toBeNull();
    });

    it('cancels the work in flight, so nothing arrives afterwards to repopulate a slice', () => {
      holdSession();

      portals.loadPortals();
      const portalRead = expectOne('/api/v1/portals');

      users.showAllAccounts();
      const userRead = expectOne('/api/v1/users');

      roles.loadRoles();
      const roleRead = expectOne('/api/v1/roles');

      modules.loadModules();
      const moduleRead = expectOne('/api/v1/modules');

      service.endSession();

      // ⚠ CLEARING THE SLICES WITHOUT ABANDONING THE REQUESTS IS THE SAME LEAK WITH A DELAY IN
      // FRONT OF IT. Both halves are necessary and neither is sufficient.
      expect(portalRead.cancelled).toBeTrue();
      expect(userRead.cancelled).toBeTrue();
      expect(roleRead.cancelled).toBeTrue();
      expect(moduleRead.cancelled).toBeTrue();

      // The testing backend refuses to answer a cancelled request at all, which is a stronger
      // statement than any arrival order this case could stage.
      expect(() =>
        portalRead.flush({
          items: [],
          meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 },
        }),
      ).toThrowError(/cancelled/i);

      expect(portals.portals()).toEqual([]);
      expect(users.userRows()).toEqual([]);
      expect(roles.roleItems()).toEqual([]);
      expect(modules.modules()).toEqual([]);
    });

    it('is idempotent, because several concurrent refusals can reach it in one turn', () => {
      holdSession();
      loadOperatorAData();

      service.endSession();
      service.endSession();
      service.endSession();

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(portals.portals()).toEqual([]);
      expect(notifications.notifications()).toEqual([]);
    });

    it('works from a cold start, so a refusal before anything was loaded is not a special case', () => {
      expect(() => service.endSession()).not.toThrow();

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(portals.portals()).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // SIGN-OUT
  // -------------------------------------------------------------------------

  describe('signing out revokes server-side and then discards locally', () => {
    it('posts the revocation and ends the session once it answers', () => {
      holdSession();
      loadOperatorAData();

      let completed = false;

      service.signOut().subscribe({
        complete: () => {
          completed = true;
        },
      });

      const revocation = httpMock.expectOne('/api/v1/auth/logout');

      expect(revocation.request.method).toBe('POST');
      // MIGRATION: revocation reaches the REFRESH token only. An access token already issued
      // cannot be recalled, which is why its lifetime is short and why the legacy
      // `FormsAuthentication.SignOut` has no exact counterpart.
      expect(revocation.request.body).toEqual({ refreshToken: SESSION_BODY.refreshToken });

      // 204, which HTTP forbids from carrying a body.
      revocation.flush(null, { status: 204, statusText: 'No Content' });

      expect(completed).toBeTrue();
      expect(authStore.isAuthenticated()).toBeFalse();
      expect(portals.portals()).toEqual([]);
      expect(roles.roleItems()).toEqual([]);

      /*
       * ⚠ THIS ASSERTION REPLACES `expect(notifications.notifications()).toEqual([])`, WHICH
       * ENCODED A DEFECT.
       *
       * That earlier expectation was written when nothing announced a sign-out at all, so an empty
       * queue looked like the clean outcome. Once a confirmation was added it kept passing anyway —
       * because the confirmation was raised from inside `AuthStore.logout`, and this service's own
       * `finalize` teardown then emptied the queue on top of it. A real browser measured the
       * resulting lifetime at 10 ms, invisible to the operator and invisible to this line.
       *
       * A bare emptiness check cannot tell "nothing was ever said" from "something was said and
       * destroyed", which is exactly why it guarded the fault. The queue's CONTENTS are asserted
       * instead, which distinguishes them.
       */
      expect(notifications.notifications().length)
        .withContext('the sign-out is confirmed, and confirmed exactly once')
        .toBe(1);

      const confirmation = notifications.notifications()[0];

      expect(confirmation.message)
        .withContext('the operator is told their instruction was carried out')
        .toBe(SIGNED_OUT_MESSAGE);
      expect(confirmation.severity)
        .withContext('a state they asked for, not the outcome of a task that could have failed')
        .toBe('info');
      // Survives the departure to the sign-in screen, which is where it is meant to be read.
      expect(confirmation.survivesNavigation).toBeTrue();

      /*
       * AND THE SESSION-ISOLATION PROPERTY STILL HOLDS, which the emptiness check used to carry on
       * its own. `loadOperatorAData` queues a notice naming operator A's record; the only survivor
       * is the fixed sentence above, so nothing that operator could see is legible to whoever signs
       * in next on this page load.
       */
      expect(confirmation.message)
        .withContext('the survivor is a fixed sentence, carrying nothing from the ended session')
        .not.toContain('Operator A');
    });

    it('confirms the sign-out AFTER its own teardown, not before it', () => {
      holdSession();

      /*
       * The ordering is the whole defect, so it is asserted directly rather than inferred from the
       * end state. `purge` is what empties the message queue, and a confirmation raised before it
       * cannot survive it — so this case proves the queue was written last by watching the two
       * events in sequence.
       */
      const order: string[] = [];

      // The QUEUE-EMPTYING is watched rather than a store's reset, because emptying the queue is
      // the act that destroyed the statement. Both spies call through, so the end state stays real.
      spyOn(notifications, 'clear').and.callFake(() => {
        order.push('clear');
      });
      spyOn(notifications, 'info').and.callThrough();
      (notifications.info as jasmine.Spy).and.callFake(() => {
        order.push('announce');
      });

      service.signOut().subscribe();
      httpMock
        .expectOne('/api/v1/auth/logout')
        .flush(null, { status: 204, statusText: 'No Content' });

      /*
       * The queue is emptied SEVERAL times on this path — once by the store's discard at subscribe
       * time, then again by `purge` and by `AuthStore.reset`'s own discard inside the teardown — so
       * the count is not asserted and must not be. The load-bearing property is that the statement
       * comes after the LAST of them.
       */
      expect(order.filter((event) => event === 'clear').length)
        .withContext('the teardown does empty the queue, so the ordering question is real')
        .toBeGreaterThan(0);
      expect(order[order.length - 1])
        .withContext('the statement is raised last: any clear after it is the 10 ms toast')
        .toBe('announce');
      expect(order.indexOf('announce'))
        .withContext('and it is raised exactly once, at the end, not once per clear')
        .toBe(order.length - 1);
    });

    it('still ends the session when the revocation FAILS', () => {
      holdSession();
      loadOperatorAData();

      let completed = false;

      service.signOut().subscribe({
        complete: () => {
          completed = true;
        },
      });

      httpMock
        .expectOne('/api/v1/auth/logout')
        .flush('', { status: 500, statusText: 'Internal Server Error' });

      // A person who asks to sign out must end up signed out on this device. Leaving the
      // session in place because a revocation request failed would be the opposite of what
      // they asked for, and they cannot act on the error in any case.
      expect(completed)
        .withContext('the failure is absorbed rather than surfaced to the caller')
        .toBeTrue();
      expect(authStore.isAuthenticated()).toBeFalse();
      expect(portals.portals()).toEqual([]);
      expect(modules.modules()).toEqual([]);

      /*
       * ⚠ BOTH FACTS ARE REPORTED, AS TWO STATEMENTS. The local sign-out completed, which is the
       * part the operator asked for, AND a refresh credential was left un-revoked on the server.
       * Folding the second into the first would misdescribe the outcome, and retracting the first to
       * make room for the second would tell someone who asked to sign out that they had not.
       *
       * This also guards a read-ordering trap that no type can catch: the residue is recorded on the
       * store by `logout`'s error handler, and `AuthStore.reset` — reached from the teardown — sets
       * that flag back to `false`. The service must therefore read it BEFORE tearing down. If it
       * ever reads it afterwards this case fails with one statement instead of two, while every
       * other assertion here still passes.
       */
      const raised = notifications.notifications();

      expect(raised.length)
        .withContext('the sign-out is confirmed AND the residue is reported')
        .toBe(2);
      expect(raised.map((entry) => entry.message)).toEqual([
        SIGNED_OUT_MESSAGE,
        REVOCATION_FAILED_MESSAGE,
      ]);
      expect(raised.map((entry) => entry.severity))
        .withContext('nothing the operator did failed, so the residue is a warning not an error')
        .toEqual(['info', 'warning']);
      expect(raised.every((entry) => entry.survivesNavigation))
        .withContext('both are meant to be read on the sign-in screen this departs to')
        .toBeTrue();
    });

    it('says nothing about a residue when the revocation succeeded', () => {
      holdSession();

      service.signOut().subscribe();
      httpMock
        .expectOne('/api/v1/auth/logout')
        .flush(null, { status: 204, statusText: 'No Content' });

      // The negative control for the case above: without it, a service that raised the residue
      // warning unconditionally would satisfy that one and mislead on every ordinary sign-out.
      expect(notifications.notifications().map((entry) => entry.message))
        .withContext('a clean sign-out reports one thing, not two')
        .toEqual([SIGNED_OUT_MESSAGE]);
      expect(authStore.revocationOutstanding()).toBeFalse();
    });

    it('issues nothing at all until it is subscribed', () => {
      holdSession();

      const pending = service.signOut();

      // COLD by construction, so the caller owns the subscription and it ends with the caller
      // rather than outliving it. `verify()` in the teardown is what proves no request went out.
      expect(authStore.isAuthenticated())
        .withContext('an unsubscribed sign-out must not end the session')
        .toBeTrue();

      pending.subscribe();
      httpMock.expectOne('/api/v1/auth/logout').flush(null, { status: 204, statusText: 'No Content' });

      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('ends the session even when no renewal credential is held to revoke', () => {
      // The custodian holds nothing, so the service has nothing to post. It must still end the
      // session rather than treating the absence as a failure.
      let completed = false;

      service.signOut().subscribe({
        complete: () => {
          completed = true;
        },
      });

      expect(completed).toBeTrue();
      expect(authStore.isAuthenticated()).toBeFalse();
    });
  });

  // -------------------------------------------------------------------------
  // THE BOUNDARY
  // -------------------------------------------------------------------------

  describe('the boundary it deliberately does not cross', () => {
    it('does not navigate, because where a caller should end up differs by caller', () => {
      // The shell sends the operator to the sign-in screen; the interceptor is mid-way through
      // re-throwing the server's own response and must not have that outcome displaced by a
      // routing failure. So the navigation belongs to the caller. Asserted structurally: the
      // service is constructed in an injector with NO router at all, so a navigation would
      // have failed to resolve a dependency.
      expect(() => service.endSession()).not.toThrow();
    });

    it('publishes exactly two members, so no state or policy can accumulate on it', () => {
      const published: string[] = Object.getOwnPropertyNames(
        Object.getPrototypeOf(service) as object,
      ).filter((name) => name !== 'constructor');

      // ⚠ A "signed out" flag here would be a second authority for whether a session is held,
      // free to disagree with the token custodian. There is deliberately no state on this type.
      expect(published.sort()).toEqual(['endSession', 'signOut']);
    });
  });
});
