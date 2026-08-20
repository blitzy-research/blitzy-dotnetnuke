/** Specification for `core/state/session-lifecycle.service.ts`. */

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

const FUTURE_SESSION_EXPIRY_UTC: string = new Date(Date.now() + 60 * 60 * 1000).toISOString();

/**
 * A credential pair, shaped as the sign-in endpoint shapes one. The values are obvious placeholders
 * rather than anything resembling a real token: nothing here is parsed, and a value that looked like a
 * credential would invite somebody to try.
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
    mustChangePassword: false,
    mustUpdateProfile: false,
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
      // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()` REPLACES the
      // backend the real client installed, so reversing the two would leave the live backend in place and
      // every expectation below would find nothing.
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
   * Establishes a held session, exactly as the sign-in flow would. Written through the token custodian
   * rather than by posting credentials, because this specification is about ENDING a session and staging
   * one through the sign-in endpoint would add a request every case then has to account for.
   */
  function holdSession(): void {
    tokens.store(SESSION_BODY);
  }

  /** Loads one real record into each of the four domain stores and queues a notification. */
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
          isAdmin: false,
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
      // Revocation reaches the REFRESH token only. An access token already issued cannot be recalled, which
      // is why its lifetime is short and why the legacy `FormsAuthentication.SignOut` has no exact
      // counterpart.
      expect(revocation.request.body).toEqual({ refreshToken: SESSION_BODY.refreshToken });

      // 204, which HTTP forbids from carrying a body.
      revocation.flush(null, { status: 204, statusText: 'No Content' });

      expect(completed).toBeTrue();
      expect(authStore.isAuthenticated()).toBeFalse();
      expect(portals.portals()).toEqual([]);
      expect(roles.roleItems()).toEqual([]);

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

      expect(confirmation.message)
        .withContext('the survivor is a fixed sentence, carrying nothing from the ended session')
        .not.toContain('Operator A');
    });

    it('confirms the sign-out AFTER its own teardown, not before it', () => {
      holdSession();

      // The ordering is the whole defect, so it is asserted directly rather than inferred from the end
      // state. `purge` is what empties the message queue, and a confirmation raised before it cannot
      // survive it — so this case proves the queue was written last by watching the two events in sequence.
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

      // The queue is emptied SEVERAL times on this path — once by the store's discard at subscribe time,
      // then again by `purge` and by `AuthStore.reset`'s own discard inside the teardown — so the count is
      // not asserted and must not be.
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

      expect(completed)
        .withContext('the failure is absorbed rather than surfaced to the caller')
        .toBeTrue();
      expect(authStore.isAuthenticated()).toBeFalse();
      expect(portals.portals()).toEqual([]);
      expect(modules.modules()).toEqual([]);

      // ⚠ BOTH FACTS ARE REPORTED, AS TWO STATEMENTS. The local sign-out completed, which is the part the
      // operator asked for, AND a refresh credential was left un-revoked on the server.
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
      // The shell sends the operator to the sign-in screen; the interceptor is mid-way through re-throwing
      // the server's own response and must not have that outcome displaced by a routing failure. So the
      // navigation belongs to the caller.
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
