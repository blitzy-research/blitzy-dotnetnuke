import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AuthStore, REVOCATION_FAILED_MESSAGE, SIGNED_OUT_MESSAGE } from './auth.store';
import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { SessionLifecycleService } from './session-lifecycle.service';
import { SESSION_ENDED_MESSAGE, SessionTeardownService } from './session-teardown.service';
import { UserStore } from './user.store';
import { AUTH_ENDPOINTS } from '../config/api-endpoints';
import { authInterceptor } from '../interceptors/auth.interceptor';
import { NotificationService } from '../services/notification.service';
import { TokenStorageService } from '../services/token-storage.service';

import type { TestRequest } from '@angular/common/http/testing';
import type { AuthSession, CurrentUser } from '../models/auth.model';

const PORTALS_URL = '/api/v1/portals';
const USERS_URL = '/api/v1/users';
const ROLES_URL = '/api/v1/roles';
const MODULES_URL = '/api/v1/modules';

/** The first operator's identity. */
const OPERATOR_A: CurrentUser = {
  userId: 0,
  portalId: -1,
  portalName: 'Measured Portal',
  username: 'ann.admin',
  displayName: 'Ann Admin',
  email: 'ann.admin@example.test',
  isSuperUser: false,
  isPortalAdministrator: false,
  roles: ['Administrators'],
  permissions: ['EDIT'],
};

/** The second operator's identity, who signs in on the same browser afterwards. */
const OPERATOR_B: CurrentUser = {
  userId: 7,
  portalId: -1,
  portalName: 'Measured Portal',
  username: 'bob.editor',
  displayName: 'Bob Editor',
  email: 'bob.editor@example.test',
  isSuperUser: false,
  isPortalAdministrator: false,
  roles: ['Administrators'],
  permissions: ['EDIT'],
};

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

/** A session for one identity, with tokens named after it so a leak is legible. */
function sessionFor(user: CurrentUser): AuthSession {
  return {
    accessToken: `${user.username}-access-token`,
    refreshToken: `${user.username}-refresh-token`,
    expiresAtUtc: FUTURE_SESSION_EXPIRY_UTC,
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user,
  };
}

/**
 * THE CROSS-SESSION ISOLATION PROPERTY.
 *
 * ⚠ WHAT THIS FILE ASSERTS THAT NO OTHER FILE DOES. Each store's own specification proves
 * that its `reset()` clears every slice it owns, and `session-lifecycle.service.spec.ts` and
 * `session-teardown.service.spec.ts` prove that ending a session calls all four and cancels
 * the work in flight. Not one of those stages a SECOND OPERATOR. This file does: it loads one
 * operator's records, ends the session the way the application really ends it — a terminal
 * refusal arriving through the live interceptor chain, and separately an explicit sign-out —
 * then signs a DIFFERENT operator in for real and asserts that nothing the first operator
 * could see is legible to the second.
 *
 * ⚠ WHY THAT IS A DISCLOSURE RATHER THAN UNTIDINESS. The four domain stores are
 * root-provided, so they outlive any component and any session. A single-page application
 * never reloads the document, so whatever a slice still holds when one operator leaves is
 * rendered to whoever signs in next at the same browser. Account rows carry names and e-mail
 * addresses; a queued notification can name a portal, an account or a role.
 *
 * ⚠ THE INTERCEPTOR CHAIN IS REAL HERE, deliberately. The terminal path is not invoked
 * directly: a store read is refused, the interceptor attempts the one renewal it is allowed,
 * the renewal is refused, and `SessionTeardownService` runs as a consequence — reached from
 * the transport layer, not from this file. That is the only way to assert that the production
 * wiring, rather than a purge called by hand, actually isolates the two sessions.
 */
describe('cross-session isolation', () => {
  let httpMock: HttpTestingController;
  let tokens: TokenStorageService;
  let authStore: AuthStore;
  let portals: PortalStore;
  let users: UserStore;
  let roles: RoleStore;
  let modules: ModuleStore;
  let notifications: NotificationService;
  let session: SessionLifecycleService;
  let teardown: SessionTeardownService;
  let navigate: jasmine.Spy;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()`
        // REPLACES the backend the real client installed, so reversing the two would leave
        // the live backend in place and every expectation below would find nothing.
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    portals = TestBed.inject(PortalStore);
    users = TestBed.inject(UserStore);
    roles = TestBed.inject(RoleStore);
    modules = TestBed.inject(ModuleStore);
    notifications = TestBed.inject(NotificationService);
    session = TestBed.inject(SessionLifecycleService);
    teardown = TestBed.inject(SessionTeardownService);

    // The interceptor routes to the sign-in screen when it ends a session. Spied before
    // anything runs so no navigation escapes into the empty route table, and resolved
    // rather than stubbed because a `catch` is chained onto the returned promise.
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** Claims the one open request at a path, whatever its method. */
  function expectOne(url: string): TestRequest {
    return httpMock.expectOne((candidate) => candidate.url === url);
  }

  /** A paged envelope. ⚠ The body is at the TOP LEVEL for a collection, never under `data`. */
  function page(items: readonly unknown[]): Record<string, unknown> {
    return {
      items,
      meta: { totalCount: items.length, pageIndex: 0, pageSize: 10, totalPages: 1 },
    };
  }

  /** Establishes a held session directly, as the sign-in flow would. */
  function holdSessionFor(user: CurrentUser): void {
    tokens.store(sessionFor(user));
  }

  /**
   * Signs an operator in through the real two-request flow.
   *
   * ⚠ A SIGN-IN IS TWO REQUESTS. The credential is posted, and the identity is then read on
   * the freshly issued token BEFORE anything is stored, so the session does not exist until
   * both have been answered.
   */
  function signIn(user: CurrentUser): void {
    authStore.login({ username: user.username, password: 'measured-secret' }).subscribe({
      error: () => undefined,
    });

    httpMock.expectOne(AUTH_ENDPOINTS.login).flush({ data: sessionFor(user), meta: null });
    httpMock.expectOne(AUTH_ENDPOINTS.me).flush({ data: user, meta: null });
  }

  /** Loads one record into each of the four domain stores and queues a notification. */
  function loadRecordsFor(operator: string): void {
    portals.loadPortals();
    expectOne(PORTALS_URL).flush(
      page([
        {
          portalId: -1,
          portalName: `${operator} Portal`,
          aliases: ['localhost'],
          users: 3,
          pages: 12,
          hostSpace: 0,
          hostFee: 0,
          expiryDate: null,
        },
      ]),
    );

    // The unfiltered listing, which is the one command that really asks the server for every
    // account. The filtered read dispatches nothing until a search has been chosen, so it
    // would leave this store empty and prove nothing about a discard.
    users.showAllAccounts();
    expectOne(USERS_URL).flush(
      page([
        {
          userId: 7,
          portalId: -1,
          username: `${operator.toLowerCase()}.account`,
          firstName: operator,
          lastName: 'Account',
          displayName: `${operator} Account`,
          address: null,
          telephone: null,
          email: `${operator.toLowerCase()}@example.invalid`,
          createdDate: '2024-01-05T09:15:00Z',
          lastLoginDate: null,
          isApproved: true,
          isOnline: false,
          isSuperUser: false,
          isLockedOut: false,
          canDelete: true,
        },
      ]),
    );

    roles.loadRoles();
    expectOne(ROLES_URL).flush(
      page([
        {
          roleId: 0,
          portalId: -1,
          roleGroupId: null,
          roleName: `${operator} Administrators`,
          description: 'Held while the session was open',
          isPublic: false,
          autoAssignment: false,
          serviceFee: 0,
          billingFrequency: 'N',
          billingPeriod: null,
          trialFee: null,
          trialPeriod: null,
          trialFrequency: null,
        },
      ]),
    );

    modules.loadModules();
    expectOne(MODULES_URL).flush(
      page([
        {
          moduleId: 0,
          tabModuleId: 0,
          tabId: 0,
          portalId: -1,
          moduleDefId: 1,
          moduleTitle: `${operator} Announcements`,
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
      ]),
    );

    notifications.notify('success', `${operator} saved a role`);
  }

  /** Every slice a screen would render, gathered so emptiness can be asserted in one place. */
  function visibleRecordCounts(): Record<string, number> {
    return {
      portals: portals.portals().length,
      users: users.userRows().length,
      roles: roles.roleItems().length,
      modules: modules.modules().length,
      notifications: notifications.notifications().length,
    };
  }

  /**
   * Ends the session the way the application really ends it.
   *
   * A store read is refused, the interceptor spends its single renewal, the renewal is
   * refused, and `SessionTeardownService` runs as a consequence. The refused read's own
   * failure is absorbed by the store, which records it rather than rethrowing.
   */
  function sufferTerminalRefusal(): void {
    portals.reloadPortals();
    expectOne(PORTALS_URL).flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock
      .expectOne(AUTH_ENDPOINTS.refresh)
      .flush(null, { status: 401, statusText: 'Unauthorized' });
  }

  describe('after a terminal refusal, the next operator starts from nothing', () => {
    beforeEach(() => {
      holdSessionFor(OPERATOR_A);
      loadRecordsFor('Ann');
    });

    it('discards every record the first operator could see', () => {
      // Loaded for real first, so what follows is a discard rather than a store that was
      // empty all along.
      expect(visibleRecordCounts()).toEqual({
        portals: 1,
        users: 1,
        roles: 1,
        modules: 1,
        notifications: 1,
      });

      sufferTerminalRefusal();

      // ⚠ ONE NOTIFICATION REMAINS, AND IT IS NOT A SURVIVOR - IT IS A REPLACEMENT. The
      // teardown clears the queue, and the interceptor then states why the session ended, in
      // that order, so what is left cannot be the message the first operator was shown. This
      // count used to be zero and the change is deliberate: an operator returned to sign-in
      // with both live regions empty had no way to tell what had happened.
      //
      // That entry is not a leak, and the assertions below are what establish it rather than
      // assume it: the statement is a FIXED sentence that quotes nothing from the refusal - no
      // status, no response body, no header, no credential - and nothing from any record the
      // previous operator could see. Checking its exact text, and then checking it against the
      // first operator's own data, is a STRONGER isolation guarantee than the bare count it
      // replaces, which would have been satisfied by announcing nothing at all.
      expect(visibleRecordCounts())
        .withContext('every record slice the first operator could see is gone')
        .toEqual({
          portals: 0,
          users: 0,
          roles: 0,
          modules: 0,
          notifications: 1,
        });

      const remaining = notifications.notifications();

      expect(remaining.length)
        .withContext('and the one thing left is the statement that the session ended')
        .toBe(1);
      expect(remaining[0]?.message).toBe(SESSION_ENDED_MESSAGE);
      // THE ISOLATION CLAIM, ASSERTED DIRECTLY: the surviving message names nothing about the
      // operator whose session just ended. A message that had merely been left in place would
      // fail here even though the count matched.
      expect(remaining[0]?.message).not.toContain(OPERATOR_A.username);
      expect(remaining[0]?.message).not.toContain('Ann');
      expect(remaining[0]?.reference).toBeNull();

      expect(navigate).toHaveBeenCalled();
    });

    it('presents the second operator with empty slices at the moment they sign in', () => {
      sufferTerminalRefusal();
      signIn(OPERATOR_B);

      // ⚠ THE ASSERTION THAT MATTERS. Signing in must not make the previous operator's
      // records visible again, and a store that merely cleared its credential while keeping
      // its rows would fail here and nowhere else.
      expect(visibleRecordCounts()).toEqual({
        portals: 0,
        users: 0,
        roles: 0,
        modules: 0,
        notifications: 0,
      });
      expect(authStore.currentUser()?.username).toBe(OPERATOR_B.username);
    });

    it('shows the second operator their own records and none of the first operator\'s', () => {
      sufferTerminalRefusal();
      signIn(OPERATOR_B);
      loadRecordsFor('Bob');

      expect(portals.portals()[0]?.portalName).toBe('Bob Portal');
      expect(users.userRows()[0]?.username).toBe('bob.account');
      expect(roles.roleItems()[0]?.roleName).toBe('Bob Administrators');
      expect(modules.modules()[0]?.moduleTitle).toBe('Bob Announcements');

      const rendered = JSON.stringify([
        portals.portals(),
        users.userRows(),
        roles.roleItems(),
        modules.modules(),
        notifications.notifications(),
      ]);

      // ⚠ ASSERTED ON WHOLE VALUES, NOT ON THE OPERATOR'S NAME. A bare search for "Ann"
      // matches the module named "Announcements" and would fail for a resemblance rather
      // than a leak — measured, not hypothetical.
      expect(rendered).not.toContain('Ann Portal');
      expect(rendered).not.toContain('Ann Administrators');
      expect(rendered).not.toContain('ann.account');
      expect(rendered).not.toContain('ann@example.invalid');
      expect(rendered).not.toContain('Ann saved a role');
    });

    it('presents the second operator\'s credential, never the first operator\'s', () => {
      sufferTerminalRefusal();
      signIn(OPERATOR_B);

      portals.loadPortals();

      const read = expectOne(PORTALS_URL);

      expect(read.request.headers.get('Authorization')).toBe(
        `Bearer ${OPERATOR_B.username}-access-token`,
      );
      expect(read.request.headers.get('Authorization')).not.toContain(OPERATOR_A.username);

      read.flush(page([]));
    });
  });

  describe('a straggling response from the ended session', () => {
    it('cannot repopulate a slice after the second operator has signed in', () => {
      holdSessionFor(OPERATOR_A);

      // Issued in the first session and deliberately left unanswered.
      users.showAllAccounts();

      const strandedRead = expectOne(USERS_URL);

      loadRecordsFor('Ann');
      sufferTerminalRefusal();
      signIn(OPERATOR_B);

      // ⚠ CLEARING THE SLICES WITHOUT ABANDONING THE REQUESTS IS THE SAME LEAK WITH A DELAY
      // IN FRONT OF IT. The testing backend refuses to answer a cancelled request at all,
      // which is a stronger statement than any arrival order this case could stage.
      expect(strandedRead.cancelled).toBeTrue();
      expect(() => strandedRead.flush(page([]))).toThrowError(/cancelled/i);
      expect(users.userRows()).toEqual([]);
    });

    it('cannot repopulate a DETAIL slice either', () => {
      holdSessionFor(OPERATOR_A);

      users.selectUser(5);

      const accountRead = httpMock.expectOne(`${USERS_URL}/5`);

      roles.selectRole(3);

      const roleRead = httpMock.expectOne(`${ROLES_URL}/3`);

      session.endSession();
      signIn(OPERATOR_B);

      expect(accountRead.cancelled).toBeTrue();
      expect(roleRead.cancelled).toBeTrue();
      expect(users.selectedUser()).toBeNull();
      expect(roles.selectedRole()).toBeNull();
    });
  });

  describe('an explicit sign-out isolates the sessions just as a refusal does', () => {
    it('discards the first operator\'s records and admits the second cleanly', () => {
      holdSessionFor(OPERATOR_A);
      loadRecordsFor('Ann');

      session.signOut().subscribe({ error: () => undefined });
      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      /*
       * The same treatment the terminal-refusal case above already applies, and for the same reason:
       * a deliberate sign-out now confirms itself, so the queue is not empty afterwards and a bare
       * `notifications: 0` would fail. It would also be the WEAKER statement — satisfied by saying
       * nothing at all, which is how a confirmation that lived 10 ms and never rendered went
       * unnoticed. The record slices are asserted empty; the queue is asserted by CONTENT.
       */
      const recordCounts: Record<string, number> = { ...visibleRecordCounts(), notifications: 0 };

      expect(recordCounts)
        .withContext('every record slice the first operator could see is gone')
        .toEqual({ portals: 0, users: 0, roles: 0, modules: 0, notifications: 0 });

      const announced = notifications.notifications();

      expect(announced.map((entry) => entry.message))
        .withContext('the only survivor is the fixed confirmation that the sign-out happened')
        .toEqual([SIGNED_OUT_MESSAGE]);
      // `loadRecordsFor('Ann')` queues a notice naming that operator, so this is a real exclusion
      // rather than a formality: the survivor carries nothing from the session that ended.
      expect(announced[0].message).not.toContain('Ann');
      expect(announced[0].message).not.toContain(OPERATOR_A.username);

      signIn(OPERATOR_B);
      loadRecordsFor('Bob');

      expect(portals.portals().length).toBe(1);
      expect(portals.portals()[0]?.portalName).toBe('Bob Portal');
    });

    it('isolates the sessions even when the revocation itself fails', () => {
      // A revocation that never reached the server leaves the server-side renewal credential
      // alive, which is a reason to end the local session rather than a reason to keep it.
      holdSessionFor(OPERATOR_A);
      loadRecordsFor('Ann');

      session.signOut().subscribe({ error: () => undefined });
      httpMock
        .expectOne(AUTH_ENDPOINTS.logout)
        .flush(null, { status: 500, statusText: 'Server Error' });

      const recordCounts: Record<string, number> = { ...visibleRecordCounts(), notifications: 0 };

      expect(recordCounts)
        .withContext('every record slice the first operator could see is gone')
        .toEqual({ portals: 0, users: 0, roles: 0, modules: 0, notifications: 0 });

      /*
       * TWO survivors on this path, and both are fixed sentences: the sign-out is confirmed, and the
       * un-revoked credential is reported as its own statement rather than folded into the first.
       * Both facts are true and the operator needs both — they are signed out on this device, and a
       * refresh credential may still be live on the server.
       */
      const announced = notifications.notifications();

      expect(announced.map((entry) => entry.message)).toEqual([
        SIGNED_OUT_MESSAGE,
        REVOCATION_FAILED_MESSAGE,
      ]);
      expect(announced.some((entry) => entry.message.includes('Ann')))
        .withContext('neither names the operator whose session it was')
        .toBeFalse();
      expect(announced.some((entry) => entry.message.includes(OPERATOR_A.username))).toBeFalse();

      expect(tokens.accessToken()).toBeNull();

      signIn(OPERATOR_B);

      expect(authStore.isAuthenticated()).toBeTrue();
      expect(authStore.currentUser()?.username).toBe(OPERATOR_B.username);
    });
  });

  describe('the query state the first operator chose', () => {
    it('does not narrow or page what the second operator reads', () => {
      holdSessionFor(OPERATOR_A);

      // ⚠ THE TWO STORES DIFFER IN WHETHER CHOOSING A RESTRICTION ISSUES A READ. Naming a
      // portal filter dispatches immediately, because the store resets the page and reloads;
      // choosing a module query only records it, and the read is issued separately. Both are
      // measured, and driving both the same way would leave one request unclaimed.
      portals.setNameFilter('Ann');
      expectOne(PORTALS_URL).flush(page([]));

      modules.setQuery('announcements');
      modules.loadModules();

      const narrowedModuleRead = expectOne(MODULES_URL);

      expect(narrowedModuleRead.request.params.get('query')).toBe('announcements');
      narrowedModuleRead.flush(page([]));

      session.endSession();
      signIn(OPERATOR_B);

      expect(portals.nameFilter()).toBeNull();
      expect(modules.query().query ?? null).toBeNull();
      expect(modules.query().pageIndex).toBe(0);

      portals.loadPortals();

      const portalRead = expectOne(PORTALS_URL);

      // A residual filter would silently hide records the second operator is entitled to see,
      // which reads as missing data rather than as a leak and is therefore harder to notice.
      expect(portalRead.request.params.has('name')).toBeFalse();
      expect(portalRead.request.params.get('pageIndex')).toBe('0');
      portalRead.flush(page([]));

      modules.loadModules();

      const moduleRead = expectOne(MODULES_URL);

      expect(moduleRead.request.params.has('query')).toBeFalse();
      moduleRead.flush(page([]));
    });
  });

  describe('the latch recording that a listing has been read', () => {
    it('does not carry into the next session, so a write there refreshes nothing unasked', () => {
      /*
       * ⚠ THE DEFECT THIS PINS IS A REQUEST THAT MUST NOT EXIST, and it is the one shape of leak the
       * rest of this file does not cover: not stale DATA carried across the boundary, but a stale
       * DECISION about what the store is entitled to do.
       *
       * `refreshListingIfRead` re-reads the portal listing after a settings write, but ONLY when a
       * listing has already been read - because refreshing something never read is meaningless, and
       * because a PORTAL ADMINISTRATOR IS NOT PERMITTED TO READ THE PORTAL LISTING at all. Where the
       * caller may not read it, the unasked re-read answers 403, the failure interceptor raises "You
       * do not have access to this content." globally, and it lands on whichever screen the operator
       * had navigated to by then: a permission complaint about a listing they never requested, on an
       * unrelated screen, immediately after a save that SUCCEEDED.
       *
       * The latch was set by the first operator - who could read the listing - and `reset()` cleared
       * every other member of the listing slice while leaving it set. So the store told itself "a
       * listing is in hand" about a page it had just emptied, on behalf of a session that no longer
       * existed, and the second operator inherited the first operator's entitlement.
       *
       * `httpMock.verify()` in `afterEach` is what actually fails on a regression here: an unclaimed
       * request is a failure, so the assertion is the ABSENCE of a request rather than a count.
       */
      holdSessionFor(OPERATOR_A);

      // THE CONTROL, AND IT IS LOAD-BEARING. Without it the case would pass against a store whose
      // latch is never set at all, which would prove nothing about the clearing.
      portals.loadPortals();
      expectOne(PORTALS_URL).flush(page([]));

      portals.refreshListingIfRead();
      expectOne(PORTALS_URL).flush(page([]));

      session.endSession();
      signIn(OPERATOR_B);

      // The second operator has read no listing, so this must dispatch NOTHING. Stated as an
      // emptiness assertion as well, so a failure here names the leak rather than surfacing as an
      // unrelated verification error in teardown.
      portals.refreshListingIfRead();

      httpMock.expectNone(() => true);
    });

    it('is re-armed by the next session reading a listing of its own', () => {
      // The complement, so the clearing cannot be mistaken for a permanent disabling: an operator who
      // DOES read a listing gets the coherence re-read that keeps it correct after a write. Without
      // this, clearing the latch and never setting it again would also pass the case above.
      holdSessionFor(OPERATOR_A);

      portals.loadPortals();
      expectOne(PORTALS_URL).flush(page([]));

      session.endSession();
      signIn(OPERATOR_B);

      portals.loadPortals();
      expectOne(PORTALS_URL).flush(page([]));

      portals.refreshListingIfRead();
      expectOne(PORTALS_URL).flush(page([]));
    });
  });

  // =====================================================================================
  // ONE OWNER, AND EVERY TERMINATION PATH REACHES IT
  // =====================================================================================
  //
  // ⚠ THE GROUP THAT EXISTS BECAUSE THERE WERE BRIEFLY THREE OWNERS FOR ONE BOUNDARY. Two were
  // live and performed the same fan-out in two places — the bearer interceptor's terminal path
  // through `SessionTeardownService`, and the shell's sign-out through `SessionLifecycleService`
  // — and they had already drifted: one cleared the queued notices and the other did not, so an
  // identical session ending left the application in two different states depending on which
  // path reached it. A third, `session.coordinator.ts`, was written to own the boundary properly
  // and had ZERO production importers, so its session GENERATION never advanced and its reason
  // was never recorded.
  //
  // The fan-out is now owned once and the coordinator's two useful ideas were folded into it.
  // What this group asserts is the property that consolidation is FOR: that every path which
  // ends a session reaches that one owner, and that the owner records WHICH path it was. The
  // fan-out's own effects are asserted throughout the rest of this file; these cases are about
  // the owner being single and being reached.
  describe('the one session-boundary owner', () => {
    it('advances the generation once per boundary, whichever path crossed it', () => {
      const start: number = teardown.generation();

      holdSessionFor(OPERATOR_A);
      loadRecordsFor('Ann');

      session.signOut().subscribe({ error: () => undefined });
      httpMock
        .expectOne(AUTH_ENDPOINTS.logout)
        .flush(null, { status: 204, statusText: 'No Content' });

      // ⚠ ASSERTED AS AN INCREASE RATHER THAN AS AN EXACT COUNT, deliberately. The sign-out path
      // legitimately reaches the owner more than once — the lifecycle service purges, and the
      // authentication store's own discard purges again — and the purge is idempotent by
      // construction, so the number of calls is not a contract. What IS a contract is that the
      // generation is MONOTONIC, so a captured value can always tell a boundary has been crossed.
      const afterSignOut: number = teardown.generation();

      expect(afterSignOut).toBeGreaterThan(start);
      expect(teardown.hasEndedASession()).toBeTrue();

      signIn(OPERATOR_B);

      expect(teardown.generation()).toBeGreaterThan(afterSignOut);
    });

    it('records a deliberate sign-out and an involuntary ending as DIFFERENT boundaries', () => {
      // The reason is the one thing the discard does not change and the one thing a consumer
      // cannot reconstruct: a purge looks identical whether the operator asked to leave or a
      // renewal was refused underneath them. Recording it is what makes the two distinguishable
      // without inventing a second mechanism to report it.
      holdSessionFor(OPERATOR_A);

      session.signOut().subscribe({ error: () => undefined });
      httpMock
        .expectOne(AUTH_ENDPOINTS.logout)
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(teardown.lastReason()).toBe('signedOut');

      // The terminal path, driven through the REAL interceptor chain rather than invoked
      // directly: a store read is refused, no renewal is possible, and the boundary is crossed
      // as a consequence — which is the only way to prove the production wiring reaches the one
      // owner rather than merely that the owner works when called.
      signIn(OPERATOR_A);
      portals.loadPortals();
      expectOne(PORTALS_URL).flush(null, { status: 401, statusText: 'Unauthorized' });
      expectOne(AUTH_ENDPOINTS.refresh).flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(teardown.lastReason()).toBe('renewalRefused');
      expect(tokens.session()).toBeNull();
    });

    it('records an identity REPLACEMENT as its own boundary, before the new session exists', () => {
      // The third termination path, and the one that is easiest to overlook because no session
      // ends in the ordinary sense: the credentials that arrive are valid, they simply describe
      // somebody else. The store purges BEFORE the attempt is issued rather than after it
      // succeeds, so a refused attempt cannot leave the previous operator's records in memory
      // for its duration.
      holdSessionFor(OPERATOR_A);
      loadRecordsFor('Ann');

      signIn(OPERATOR_B);

      expect(teardown.lastReason()).toBe('signedIn');
      expect(visibleRecordCounts()).toEqual({
        portals: 0,
        users: 0,
        roles: 0,
        modules: 0,
        notifications: 0,
      });
    });

    it('publishes the generation and the reason read-only, so no consumer can rewind them', () => {
      // A consumer that could move the generation could declare its own stale work current, and
      // one that could write the reason could misattribute a boundary. Only the purge moves
      // either.
      expect('set' in teardown.generation).toBeFalse();
      expect('update' in teardown.generation).toBeFalse();
      expect('set' in teardown.lastReason).toBeFalse();
      expect('update' in teardown.lastReason).toBeFalse();
    });

    it('answers a captured generation correctly across a boundary', () => {
      holdSessionFor(OPERATOR_A);

      const captured: number = teardown.generation();

      expect(teardown.isCurrent(captured)).toBeTrue();

      session.endSession();

      expect(teardown.isCurrent(captured)).toBeFalse();
    });
  });
});
