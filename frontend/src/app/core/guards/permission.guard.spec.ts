/**
 * Specifications for `core/guards/permission.guard.ts`.
 *
 * WHY THESE EXIST, AND WHY THEY ARE DRIVEN THROUGH A REAL ROUTER.
 *
 * The gate is the client half of the API's policy-based authorisation, and two review
 * findings were resolved inside it:
 *
 *   * The client's policy catalogue held five names while the server registers eight, so
 *     three policies could never be declared on a route without being refused outright.
 *   * The module and tab scope keys each carried a generic `id` fallback that the server
 *     has no counterpart for. `PermissionAuthorizationHandler` reads `moduleId` and
 *     `tabId` and nothing else, so a route naming a record as `id` would have satisfied
 *     the client and then failed at the endpoint.
 *
 * Both were verified by reading the code and by proving set identity against the server's
 * `PolicyNames.cs`. Neither was verified EXECUTABLY, because no specification for this
 * file existed. That is what this file corrects, and it is why every catalogue entry below
 * gets a positive control as well as a refusal: this class of fix regresses most easily by
 * becoming too strict, and a suite that only proves refusals would pass just as happily
 * against a gate that refuses everything.
 *
 * THE ROUTER IS REAL, AND THAT IS THE LOAD-BEARING DECISION HERE. The gate resolves a
 * scope identifier by walking `route.pathFromRoot`, because route DATA is inherited down
 * onto every snapshot while route PARAMETERS are not — a child route beneath
 * `modules/:moduleId` sees the policy on its own snapshot and the identifier on its
 * parent's. A hand-built `ActivatedRouteSnapshot` with a fabricated `pathFromRoot` would
 * test the fabrication rather than the router's genuine inheritance behaviour, and would
 * agree with the guard even if the guard were wrong about where parameters live. So the
 * table below is a real route table, navigation is real navigation, and the assertions
 * read the router's own resulting address.
 *
 * `AuthStore` and `NotificationService` are doubles, following the arrangement the
 * portal-settings and portal-list specifications already use in this repository: partial
 * objects whose members are writable signals, so a test states the identity it wants
 * rather than driving a sign-in to arrive at it.
 */
import { Component, signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import type { Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { permissionGuard } from './permission.guard';
import { NotificationService } from '../services/notification.service';
import { AuthStore } from '../state/auth.store';
import type { CurrentUser } from '../models/auth.model';

/** The refusal text the gate raises, asserted rather than restated loosely. */
const ACCESS_REFUSED_MESSAGE = 'You do not have access to this content.';

/** The tenant administrator role name the gate matches with exact equality. */
const PORTAL_ADMINISTRATOR_ROLE = 'Administrators';

/**
 * The complete set of policy names the API registers, transcribed from
 * `backend/src/DnnMigration.Api/Authorization/PolicyNames.cs`.
 *
 * Held here as an independent copy ON PURPOSE. Importing the guard's own constant would
 * make the catalogue assertion a tautology — it would compare the list to itself and pass
 * for any list at all. This copy is the server's list, so the assertion below genuinely
 * asks whether the client agrees with the server.
 */
const SERVER_POLICY_NAMES = [
  'ModuleView',
  'ModuleEdit',
  'TabView',
  'TabEdit',
  'PortalAdministrator',
  'HostAdministrator',
  'AccountOwner',
  'AccountOwnerOrPortalAdministrator',
] as const;

/** A trivial destination, so a navigation has something to activate. */
@Component({ selector: 'app-guard-target', standalone: true, template: 'target' })
class GuardTargetComponent {}

/** The members of the store the gate reads, each writable so a test can state them. */
interface AuthStoreDouble {
  isAuthenticated: WritableSignal<boolean>;
  currentUser: WritableSignal<CurrentUser | null>;
  isSuperUser: WritableSignal<boolean>;
  roles: WritableSignal<readonly string[]>;
}

function authStoreDouble(): AuthStoreDouble {
  return {
    isAuthenticated: signal(true),
    currentUser: signal<CurrentUser | null>(null),
    isSuperUser: signal(false),
    roles: signal<readonly string[]>([]),
  };
}

/** An identity carrying only what the gate consults. */
function currentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userId: 7,
    portalId: 0,
    portalName: 'Primary Portal',
    username: 'admin',
    displayName: 'Administrator',
    email: 'admin@example.test',
    isSuperUser: false,
    isPortalAdministrator: false,
    roles: [],
    permissions: [],
    ...overrides,
  };
}

/**
 * A guarded route declaring a policy.
 *
 * `data` is passed through exactly as a route table would declare it, including the
 * deliberately malformed values, so the gate's narrowing is exercised against the real
 * shape the router delivers rather than a pre-narrowed one.
 */
function guarded(path: string, permission: unknown): Routes[number] {
  return {
    path,
    component: GuardTargetComponent,
    canActivate: [permissionGuard],
    data: { permission },
  };
}

/**
 * The route table under test.
 *
 * Three arrangements matter and are all present:
 *
 *   * `modules/:moduleId` is COMPONENTLESS with children, which is what puts the
 *     identifier on a parent snapshot and the policy on a child's. That is the ancestry
 *     case the gate documents, and it cannot be reproduced with a flat table.
 *   * the `legacy-*` routes name their record `id` rather than the server's key, which is
 *     precisely the shape the removed fallback used to admit.
 *   * `login` is present and unguarded, because the gate redirects to it.
 */
const routes: Routes = [
  { path: 'login', component: GuardTargetComponent },
  { path: 'start', component: GuardTargetComponent },

  // No `data` at all, and a `data` whose value is not a string.
  { path: 'undeclared', component: GuardTargetComponent, canActivate: [permissionGuard] },
  guarded('wrong-type', 42),
  guarded('unregistered', 'ModuleDelete'),

  // The two unscoped policies.
  guarded('portal-admin', 'PortalAdministrator'),
  guarded('host-admin', 'HostAdministrator'),

  // Scoped by the server's module key, with the policy on a CHILD of the parameter.
  {
    path: 'modules/:moduleId',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleView' },
    children: [
      { path: '', component: GuardTargetComponent },
      guarded('settings', 'ModuleEdit'),
    ],
  },

  // Scoped by the server's tab and account keys.
  guarded('tabs/:tabId', 'TabView'),
  guarded('tab-edit/:tabId', 'TabEdit'),
  guarded('users/:userId', 'AccountOwner'),
  guarded('accounts/:userId', 'AccountOwnerOrPortalAdministrator'),

  // The same policies, but naming the record `id` — the removed fallback's shape.
  guarded('legacy-modules/:id', 'ModuleView'),
  guarded('legacy-tabs/:id', 'TabView'),
  guarded('legacy-users/:id', 'AccountOwner'),
];

describe('permissionGuard', () => {
  let identity: AuthStoreDouble;
  let notifications: jasmine.SpyObj<Pick<NotificationService, 'notify'>>;
  let harness: RouterTestingHarness;
  let router: Router;

  beforeEach(async () => {
    identity = authStoreDouble();
    notifications = jasmine.createSpyObj<Pick<NotificationService, 'notify'>>(
      'NotificationService',
      ['notify'],
    );

    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        { provide: AuthStore, useValue: identity },
        { provide: NotificationService, useValue: notifications },
      ],
    });

    harness = await RouterTestingHarness.create('/start');
    router = TestBed.inject(Router);
  });

  /**
   * Attempts an address and reports where the router ended up.
   *
   * The router's resulting address is the assertion surface rather than the promise's
   * boolean, because the three outcomes are distinguishable there without depending on
   * how a cancelled navigation resolves: an admitted route becomes the address, a refused
   * one leaves the previous address standing, and a redirect lands on the sign-in screen
   * with the attempt preserved.
   */
  async function attempt(url: string): Promise<string> {
    await harness.navigateByUrl(url).catch(() => undefined);

    return router.url;
  }

  // -------------------------------------------------------------------------
  // F13 — THE POLICY CATALOGUE AGREES WITH THE SERVER
  // -------------------------------------------------------------------------
  describe('the declared policy catalogue', () => {
    beforeEach(() => {
      // A host account satisfies every coarse check the gate makes, so a refusal in this
      // block can only come from the catalogue rejecting the NAME.
      identity.currentUser.set(currentUser({ isSuperUser: true }));
      identity.isSuperUser.set(true);
    });

    it('admits a route declaring each of the eight policies the server registers', async () => {
      // The positive control for the whole finding. Before the fix three of these names
      // were absent from the client catalogue and every one of them was refused, so a
      // route could not declare them at all. Each is exercised at an address that supplies
      // whatever scope the policy needs.
      const addresses: Record<(typeof SERVER_POLICY_NAMES)[number], string> = {
        ModuleView: '/modules/5',
        ModuleEdit: '/modules/5/settings',
        TabView: '/tabs/9',
        TabEdit: '/tab-edit/9',
        PortalAdministrator: '/portal-admin',
        HostAdministrator: '/host-admin',
        AccountOwner: '/users/7',
        AccountOwnerOrPortalAdministrator: '/accounts/7',
      };

      for (const policy of SERVER_POLICY_NAMES) {
        const target = addresses[policy];

        expect(await attempt(target)).toBe(target);
      }

      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('covers every policy the server registers, with none left untested', () => {
      // Guards the suite itself. If a ninth policy is added server-side and mirrored into
      // the guard, the map above stops being exhaustive and this fails rather than
      // silently testing seven of eight.
      expect(SERVER_POLICY_NAMES.length).toBe(8);
      expect(new Set(SERVER_POLICY_NAMES).size).toBe(8);
    });

    it('refuses a name the server does not register, rather than letting it reach the API', async () => {
      // An unregistered name is not a tidy 403 waiting to happen: no provider can
      // manufacture a policy on demand, so it would fault during authorisation. Refusing
      // here costs a correct route nothing.
      expect(await attempt('/unregistered')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('refuses a route that declares no policy at all', async () => {
      expect(await attempt('/undeclared')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('refuses a declaration that is not a string, which the router types would not have caught', async () => {
      // Route data is an index signature onto `any`, so a number arrives with every
      // compile-time guarantee switched off. The gate widens it to `unknown` precisely so
      // this case has to be handled, and this is the specification that it is.
      expect(await attempt('/wrong-type')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });
  });

  // -------------------------------------------------------------------------
  // F08 — SCOPE KEYS MATCH THE SERVER'S, WITH NO GENERIC FALLBACK
  // -------------------------------------------------------------------------
  describe('scope resolution', () => {
    beforeEach(() => {
      identity.currentUser.set(currentUser({ isSuperUser: true }));
      identity.isSuperUser.set(true);
    });

    it('resolves a module scope from moduleId, the key the server reads', async () => {
      expect(await attempt('/modules/5')).toBe('/modules/5');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('refuses a module route that names its record id instead of moduleId', async () => {
      // THE FINDING, stated as behaviour. The removed fallback listed `id` alongside
      // `moduleId`, so this address used to satisfy the client and then fail at the
      // endpoint, because `PermissionAuthorizationHandler` reads `moduleId` only.
      expect(await attempt('/legacy-modules/5')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('refuses a tab route that names its record id instead of tabId', async () => {
      expect(await attempt('/legacy-tabs/9')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('refuses an account route that names its record id instead of userId', async () => {
      expect(await attempt('/legacy-users/7')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('resolves an identifier held on an ancestor when the policy sits on a child route', async () => {
      // The reason the router is real. `modules/:moduleId` is componentless, so the child
      // carries `ModuleEdit` on its own snapshot while `moduleId` lives on the parent's.
      // Route data is inherited downward; parameters are not. A gate that read only its
      // own snapshot would refuse this, and a fabricated snapshot would not have exposed
      // the difference.
      expect(await attempt('/modules/5/settings')).toBe('/modules/5/settings');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('admits the two unscoped policies without requiring any route parameter', async () => {
      // Unscoped for two DIFFERENT reasons: the tenant policy has a scope the API resolves
      // from the request's Host header, and the host policy has no scope at all. Both
      // therefore need no parameter, and neither is a special case bolted on.
      expect(await attempt('/portal-admin')).toBe('/portal-admin');
      expect(await attempt('/host-admin')).toBe('/host-admin');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('accepts a scope identifier of zero, which is a real key in this schema', async () => {
      // ⚠ IDENTIFIER TRAP. Zero is falsy in JavaScript, and the role, page and module keys
      // are all seeded at zero, so a presence test written as a truthiness test would
      // refuse a legitimate record. The gate tests length instead, and this pins it.
      expect(await attempt('/modules/0')).toBe('/modules/0');
      expect(await attempt('/tabs/0')).toBe('/tabs/0');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('accepts a scope identifier of minus one, which is a real tenant key', async () => {
      // `Portals.PortalID` is declared IDENTITY(-1, 1), so minus one is a real key as well
      // as the legacy encoding for a missing integer. A gate that rejected non-positive
      // identifiers would refuse two real records.
      expect(await attempt('/modules/-1')).toBe('/modules/-1');
      expect(notifications.notify).not.toHaveBeenCalled();
    });
  });

  // -------------------------------------------------------------------------
  // AUTHENTICATION IS SETTLED FIRST
  // -------------------------------------------------------------------------
  describe('an unauthenticated caller', () => {
    beforeEach(() => {
      identity.isAuthenticated.set(false);
    });

    it('is redirected to the sign-in route with the attempted address preserved', async () => {
      expect(await attempt('/portal-admin')).toBe('/login?returnUrl=%2Fportal-admin');
    });

    it('is redirected without a notification, because the redirect is itself the affordance', async () => {
      // Announcing a failure at the exact moment the application is already doing the
      // helpful thing would misreport it. The redirect carries the meaning.
      await attempt('/portal-admin');

      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('is redirected before any policy is examined, so an unusable declaration is not reported', async () => {
      // Ordering matters: authentication is settled first, so an unauthenticated caller
      // reaching a mis-configured route still lands on sign-in rather than being told they
      // lack access to something the route never named properly.
      expect(await attempt('/unregistered')).toBe('/login?returnUrl=%2Funregistered');
      expect(notifications.notify).not.toHaveBeenCalled();
    });
  });

  // -------------------------------------------------------------------------
  // THE TWO COARSE ADMINISTRATION CHECKS
  // -------------------------------------------------------------------------
  describe('the coarse administration checks', () => {
    it('admits a portal administrator to the tenant administration policy', async () => {
      identity.currentUser.set(currentUser({ roles: [PORTAL_ADMINISTRATOR_ROLE] }));
      identity.roles.set([PORTAL_ADMINISTRATOR_ROLE]);

      expect(await attempt('/portal-admin')).toBe('/portal-admin');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('admits a host account to the tenant administration policy through the escape hatch', async () => {
      identity.currentUser.set(currentUser({ isSuperUser: true }));
      identity.isSuperUser.set(true);

      expect(await attempt('/portal-admin')).toBe('/portal-admin');
    });

    it('refuses an ordinary account the tenant administration policy, at warning severity', async () => {
      identity.currentUser.set(currentUser({ roles: ['Subscribers'] }));
      identity.roles.set(['Subscribers']);

      expect(await attempt('/portal-admin')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('grants the host policy on the host-account flag alone, never on the administrator role', async () => {
      // The server registers host administration SEPARATELY from tenant administration
      // because the two answer different questions. Admitting a portal administrator here
      // would invent an entitlement the API never issues.
      identity.currentUser.set(currentUser({ roles: [PORTAL_ADMINISTRATOR_ROLE] }));
      identity.roles.set([PORTAL_ADMINISTRATOR_ROLE]);
      identity.isSuperUser.set(false);

      expect(await attempt('/host-admin')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
    });

    it('admits a host account to the host policy', async () => {
      identity.currentUser.set(currentUser({ isSuperUser: true }));
      identity.isSuperUser.set(true);

      expect(await attempt('/host-admin')).toBe('/host-admin');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('admits a held session whose identity has not yet resolved, and lets the API decide', async () => {
      // ⚠ PREVENTS A REAL DEFECT rather than a hypothetical one. A session is reported from
      // the token custodian, but the role list and host-account flag come from a fetched
      // identity that is null until it arrives. In that window a genuine administrator
      // reports no roles, so refusing on that evidence would lock out the very operators
      // the screen exists for.
      identity.isAuthenticated.set(true);
      identity.currentUser.set(null);
      identity.roles.set([]);
      identity.isSuperUser.set(false);

      expect(await attempt('/portal-admin')).toBe('/portal-admin');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('applies no coarse check whatever to the four RECORD-scoped policies', async () => {
      // ⚠ THE LOAD-BEARING OMISSION. Answering a module or page policy would mean fetching
      // the permission records held against that one record and interpreting them, which is
      // exactly the second authorisation engine this gate must not become — and it would be
      // free to disagree with the server, which in the refusing direction hides a screen the
      // server would have served. An ordinary account is therefore admitted to every
      // record-scoped route and the API answers 403 on its own account.
      //
      // The account policies are NOT in this set and are asserted separately: ownership is
      // an identifier comparison the client can make exactly rather than a record it would
      // have to re-derive.
      identity.currentUser.set(currentUser({ roles: ['Subscribers'] }));
      identity.roles.set(['Subscribers']);

      expect(await attempt('/modules/5')).toBe('/modules/5');
      expect(await attempt('/modules/5/settings')).toBe('/modules/5/settings');
      expect(await attempt('/tabs/9')).toBe('/tabs/9');
      expect(await attempt('/tab-edit/9')).toBe('/tab-edit/9');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('refuses an account reaching a route that names somebody else', async () => {
      // ⚠ THE ONE SCOPED QUESTION THE CLIENT MAY ANSWER, AND WHY IT IS NOT A SECOND
      // AUTHORISATION ENGINE. Every other scoped policy would require RE-DERIVING a stored
      // permission record, so the client could disagree with the server and would hide a
      // screen the server would have served. Ownership is not of that kind: it is the
      // equality of two identifiers the client already holds — the identity's own
      // `userId` against the route's — and it is the very same comparison the server makes
      // in `PortalAdministratorAuthorizationHandler.cs:L246-L270`. The client cannot reach
      // a different answer, so making it here costs a navigation the server would refuse
      // anyway and saves mounting a screen that would then collapse.
      //
      // The server additionally requires the account to be bound to the tenant, which the
      // client cannot check and does not attempt — hence admitting one's OWN account here
      // is still not a claim that the request will succeed.
      identity.currentUser.set(currentUser({ userId: 7 }));

      expect(await attempt('/users/99')).toBe('/start');
      expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);

      notifications.notify.calls.reset();

      expect(await attempt('/users/7'))
        .withContext("the caller's own account is admitted by the same comparison")
        .toBe('/users/7');
      expect(notifications.notify).not.toHaveBeenCalled();
    });

    it('has NO administrator arm on the credential change, only on the combined policy', async () => {
      // `AccountOwner` guards the credential change, and admitting an administrator to it
      // would collapse the change and the reset into one operation whose effect depended on
      // which fields were populated — the shape that previously allowed a credential to be
      // overwritten with no proof of entitlement. An administrator who must intervene uses
      // the reset instead, which carries portal administration and is recorded as its own
      // act. The COMBINED policy does admit them, and the contrast is the assertion.
      identity.currentUser.set(
        currentUser({ userId: 7, roles: [PORTAL_ADMINISTRATOR_ROLE] }),
      );
      identity.roles.set([PORTAL_ADMINISTRATOR_ROLE]);

      expect(await attempt('/users/99'))
        .withContext('portal administration does not open somebody else’s credential change')
        .toBe('/start');

      expect(await attempt('/accounts/99'))
        .withContext('the combined policy admits the administrator of the account’s portal')
        .toBe('/accounts/99');
    });
  });

  // -------------------------------------------------------------------------
  // NOTHING IS CACHED
  // -------------------------------------------------------------------------
  it('re-decides on every navigation rather than caching a verdict', async () => {
    // A change in the caller's roles takes effect on the next navigation rather than
    // persisting until something is invalidated, so a revoked administrator loses the
    // screen immediately.
    identity.currentUser.set(currentUser({ roles: [PORTAL_ADMINISTRATOR_ROLE] }));
    identity.roles.set([PORTAL_ADMINISTRATOR_ROLE]);

    expect(await attempt('/portal-admin')).toBe('/portal-admin');

    identity.roles.set(['Subscribers']);
    identity.currentUser.set(currentUser({ roles: ['Subscribers'] }));

    expect(await attempt('/start')).toBe('/start');
    expect(await attempt('/portal-admin')).toBe('/start');
    expect(notifications.notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
  });
});
