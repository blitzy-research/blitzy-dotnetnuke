/**
 * Specifications for the session-boundary coordinator.
 *
 * WHAT THESE CASES ARE ABOUT. Not the mechanics of a counter, but the tenant-isolation
 * property the counter exists to buy: after a boundary is crossed, NOTHING one operator's
 * session put into a root-provided feature store is still readable, and nothing that was in
 * flight for that session can put it back. Every case below therefore populates real stores
 * through their real transports and then asserts what survives.
 *
 * The stores are the real implementations, resolved from the injector, because the defect
 * being guarded lives in the seam between the coordinator and them: a store whose `reset`
 * missed a slice, or which left a request listening, would satisfy a mocked collaborator and
 * still leak. Only the HTTP backend is substituted.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { API_ENDPOINTS } from '../config/api-endpoints';
import { ModuleStore } from './module.store';
import { PortalStore } from './portal.store';
import { RoleStore } from './role.store';
import { SessionCoordinator } from './session.coordinator';
import { UserStore } from './user.store';

import type { ApiMeta } from '../models/paged-result.model';

/**
 * A paging envelope for one row, spelled the way the API spells it.
 *
 * Declared once rather than per case so that a change to the envelope shape is a change in one
 * place. The counts are the truthful ones for a single row, because a store that reads them
 * publishes them and a case asserting "empty afterwards" must not be comparing against
 * nonsense to begin with.
 */
const ONE_ROW_META: ApiMeta = {
  pageIndex: 0,
  pageSize: 10,
  totalCount: 1,
  totalPages: 1,
};

describe('SessionCoordinator', () => {
  let coordinator: SessionCoordinator;
  let httpMock: HttpTestingController;
  let portals: PortalStore;
  let modules: ModuleStore;
  let users: UserStore;
  let roles: RoleStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // `provideHttpClient` before `provideHttpClientTesting`, because the testing provider
      // replaces the backend the former installs; the other order lets the real backend win
      // and the requests leave the browser.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    coordinator = TestBed.inject(SessionCoordinator);
    httpMock = TestBed.inject(HttpTestingController);
    portals = TestBed.inject(PortalStore);
    modules = TestBed.inject(ModuleStore);
    users = TestBed.inject(UserStore);
    roles = TestBed.inject(RoleStore);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('the session generation', () => {
    it('starts at one, so a captured value of zero can mean "no session yet"', () => {
      expect(coordinator.generation()).toBe(1);
    });

    it('reports no boundary crossed before the first reset', () => {
      expect(coordinator.lastReason()).toBeNull();
      expect(coordinator.hasEndedASession()).toBeFalse();
    });

    it('moves on for every boundary and never rewinds', () => {
      const first = coordinator.reset('signedIn');
      const second = coordinator.reset('signedOut');
      const third = coordinator.reset('renewalRefused');
      const fourth = coordinator.reset('tenantChanged');

      expect([first, second, third, fourth]).toEqual([2, 3, 4, 5]);
      expect(coordinator.generation()).toBe(5);
    });

    it('records which boundary was crossed', () => {
      coordinator.reset('tenantChanged');

      expect(coordinator.lastReason()).toBe('tenantChanged');
      expect(coordinator.hasEndedASession()).toBeTrue();
    });

    it('answers the staleness question by exact identity', () => {
      const captured = coordinator.generation();

      expect(coordinator.isCurrent(captured)).toBeTrue();

      coordinator.reset('signedOut');

      expect(coordinator.isCurrent(captured))
        .withContext('work captured under the previous session is stale')
        .toBeFalse();
      expect(coordinator.isCurrent(coordinator.generation())).toBeTrue();
    });
  });

  describe('discarding what a session held', () => {
    it('empties the portal listing a previous session read', () => {
      portals.loadPortals();

      httpMock.expectOne((request) => request.url === API_ENDPOINTS.portals.collection()).flush({
        items: [
          {
            portalId: 0,
            portalName: 'Tenant of the previous operator',
            aliases: ['localhost'],
            users: 3,
            pages: 12,
            hostSpace: 0,
            hostFee: 0,
            expiryDate: null,
          },
        ],
        meta: ONE_ROW_META,
      });

      expect(portals.portals().length).withContext('the row was read').toBe(1);

      coordinator.reset('signedOut');

      expect(portals.portals().length).withContext('and does not survive the boundary').toBe(0);
      expect(portals.totalCount()).toBe(0);
      expect(portals.listLoading()).toBeFalse();
    });

    it('empties the module listing, which previously had no full reset at all', () => {
      modules.loadModules();

      httpMock.expectOne((request) => request.url.includes('/modules')).flush({
        items: [
          {
            moduleId: 0,
            tabModuleId: 1,
            tabId: 0,
            moduleDefId: 4,
            moduleTitle: 'Module of the previous operator',
            friendlyName: 'Announcements',
            desktopModuleId: 2,
            moduleName: 'Announcements',
            description: '',
            version: '01.00.00',
            moduleOrder: 1,
            allTabs: false,
            visibility: 0,
            isDeleted: false,
            displayTitle: true,
            startDate: null,
            endDate: null,
          },
        ],
        meta: ONE_ROW_META,
      });

      expect(modules.modules().length).withContext('the row was read').toBe(1);

      coordinator.reset('signedOut');

      expect(modules.modules().length).withContext('and does not survive the boundary').toBe(0);
      expect(modules.listLoading()).toBeFalse();
      expect(modules.module()).toBeNull();
      expect(modules.tabs().length).toBe(0);
      expect(modules.definitions().length).toBe(0);
      expect(modules.failure()).toBeNull();
    });

    it('returns the module listing query to its declared coordinate', () => {
      // The import screen widens the page size for its own picker. Left in place, that widened
      // coordinate would outlive the session and change what every other module screen asks
      // for, which is the shared-state half of the same defect.
      modules.setPageSize(100);

      // The setter records the coordinate and issues nothing, which is the store's documented
      // arrangement — a screen composes a coordinate and then reads once. No request is
      // therefore expected here, and the case is about what survives the boundary.
      expect(modules.query().pageSize).toBe(100);

      coordinator.reset('signedOut');

      expect(modules.query()).toEqual({ pageIndex: 0, pageSize: 10 });
    });

    it('does not deliver a read that was already in flight when the session ended', () => {
      portals.loadPortals();

      const outstanding = httpMock.expectOne((request) => request.url === API_ENDPOINTS.portals.collection());

      coordinator.reset('renewalRefused');

      expect(outstanding.cancelled)
        .withContext('the request is cancelled rather than merely ignored')
        .toBeTrue();
      expect(portals.portals().length).toBe(0);
      expect(portals.listLoading())
        .withContext('and the loading flag does not settle from a cancelled read')
        .toBeFalse();
    });

    it('leaves every store usable afterwards, so a boundary is not a one-way door', () => {
      coordinator.reset('signedOut');
      coordinator.reset('signedIn');

      // The point of this case is the WRITE path. A store that tracked its writes in a single
      // subscription container would have closed it on the first reset, and every write for
      // the rest of the application's life would then be cancelled the instant it was issued.
      users.setApproval(7, true);

      const written = httpMock.expectOne(
        (request) => request.url === API_ENDPOINTS.users.approval(7),
      );

      expect(written.cancelled).withContext('the write survived two boundaries').toBeFalse();

      written.flush({ data: null, meta: null });

      // The follow-on reads the command issues are answered so that `verify()` has nothing
      // outstanding; their content is not what this case is about.
      for (const pending of httpMock.match(() => true)) {
        pending.flush({ items: [], meta: { ...ONE_ROW_META, totalCount: 0, totalPages: 0 } });
      }
    });

    it('discards role state through the same one call', () => {
      roles.setRolesQuery('previous operator');

      httpMock.expectOne((request) => request.url.includes('/roles')).flush({
        items: [],
        meta: { ...ONE_ROW_META, totalCount: 0, totalPages: 0 },
      });

      coordinator.reset('tenantChanged');

      expect(roles.roleItems().length).toBe(0);
      expect(roles.selectedRole()).toBeNull();
      expect(roles.rolesLoading()).toBeFalse();
      expect(roles.failure()).toBeNull();
    });
  });
});
