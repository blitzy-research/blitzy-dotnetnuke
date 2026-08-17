/**
 * Specification for {@link permissionGuard}, the navigation gate that reads the authorisation POLICY a
 * route declares and refuses to mount a screen the caller demonstrably cannot use. NET-NEW COVERAGE, NOT
 * A PORTED TEST. The legacy tree contains ZERO automated tests of any kind, so nothing here is a
 * translation of a prior assertion.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, convertToParamMap, provideRouter } from '@angular/router';
import type { ActivatedRouteSnapshot, RouterStateSnapshot, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { NotificationService } from '../services/notification.service';
import { AuthStore } from '../state/auth.store';
import { permissionGuard } from './permission.guard';

/** The refusal wording, spelled again rather than imported. */
const ACCESS_REFUSED_MESSAGE = 'You do not have permission to view this content.';

/** The sign-in route the gate redirects an unauthenticated caller to. */
const SIGN_IN_PATH = '/login';

/** The query-parameter key the attempted address travels under. */
const RETURN_URL_KEY = 'returnUrl';

/**
 * The name of the role `Library/Components/Portal/PortalController.vb:L1390` creates for a new tenant —
 * held here ONLY as the negative fixture the gate must ignore. ⚠ THIS NAME CONFERS NOTHING, AND PROVING
 * THAT IS THE POINT OF KEEPING IT. The gate used to admit the tenant-administration policy on
 * `roles().includes('Administrators')`, which was wrong three ways: administration is conferred by
 * `Portals.AdministratorRoleId`, a per-tenant COLUMN naming whichever role administers that tenant;
 * `Roles.RoleName` is an ordinary updatable column, so renaming the role stripped every administrator of
 * their screens; and a role of the same name may belong to a DIFFERENT tenant, which makes a name match
 * right about the word and wrong about the portal.
 */
const PORTAL_ADMINISTRATOR_ROLE = 'Administrators';

/**
 * The complete set of policy names the API registers, transcribed from its `Authorization/PolicyNames.cs`
 * and the registrations in `Authorization`/`Extensions/AuthenticationExtensions.cs`. Held here as an
 * INDEPENDENT copy on purpose.
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
  'PortalContentEditor',
] as const;

/**
 * Names that must NEVER be accepted as a policy, each for a stated reason. ⚠ THESE APPEAR IN THIS FILE
 * ONLY AS NEGATIVE FIXTURES and must never appear in the gate itself. The first six are
 * plausible-sounding inventions — the shapes a well-meaning change reaches for when adding a screen — and
 * none is registered.
 */
const UNREGISTERED_POLICY_NAMES: readonly string[] = [
  'PortalView',
  'PortalEdit',
  'ManageRoles',
  'RolesWrite',
  'ManageUsers',
  'UsersWrite',
  'SuperUser',
  'AuthenticatedUser',
  '',
];

/**
 * The PERSISTED permission keys, which are a different vocabulary and must not be accepted where a policy
 * name belongs. ⚠ TWO CLOSED, NON-INTERCHANGEABLE SETS. These four are the values stored against module,
 * tab and folder records, compared with exact string equality — the member name IS the persisted value,
 * as at `Library/Components/Security/Permissions/ModulePermissionController.vb:L36`.
 */
const PERSISTED_PERMISSION_KEYS: readonly string[] = ['VIEW', 'EDIT', 'READ', 'WRITE'];

/**
 * The catalogue endpoint the gate must never call, spelled RELATIVELY. Relative because that is the only
 * form that works in the deployed topology: the proxy serves the application and forwards `/api/` to the
 * API container, so the browser reaches the API through the same origin that served the page.
 */
const PERMISSION_CATALOGUE_URL = '/api/v1/permissions';

/** The current-user endpoint, which the gate must never call either. */
const CURRENT_USER_URL = '/api/v1/auth/me';

/**
 * Members of {@link AuthStore} the gate must never reach, by their REAL names. ⚠ `permissions` IS THE
 * IMPORTANT ONE. The store exposes a permission-key projection and documents itself as deciding nothing
 * with it; the API's own current-user contract likewise exposes the keys while offering no membership
 * test.
 */
const STORE_MEMBERS_OFF_LIMITS: readonly string[] = [
  'permissions',
  'roles',
  'holdsPortalAdministration',
  'login',
  'logout',
  'loadCurrentUser',
  'refreshSession',
  'renewSession',
  'endSession',
  'reset',
  'clearError',
];

/**
 * Members of {@link NotificationService} the gate must never reach. ⚠ THE SEVERITY ALIASES ARE THE POINT.
 * A refusal must be raised at WARNING severity, and the shortest way to break that is to call `error(…)`
 * instead of `notify('warning', …)`.
 */
const NOTIFIER_MEMBERS_OFF_LIMITS: readonly string[] = [
  'error',
  'success',
  'info',
  'warning',
  'dismiss',
  'clear',
];

/**
 * Builds one spy per member name, so that reaching any of them is a recorded event.
 *
 * @param names The member names to install spies for.
 * @returns A record keyed by member name.
 */
function offLimitsSpies(names: readonly string[]): Record<string, jasmine.Spy> {
  const spies: Record<string, jasmine.Spy> = {};

  for (const name of names) {
    spies[name] = jasmine.createSpy(name);
  }

  return spies;
}

/**
 * The identity shape the gate consults, derived from the store rather than imported. Deriving it keeps
 * this file's imports to the four modules it genuinely depends on while still binding the fixture to the
 * real contract: if the identity's `userId` were renamed or retyped, this alias changes with it and the
 * fixture below stops compiling.
 */
type Identity = NonNullable<ReturnType<AuthStore['currentUser']>>;

/**
 * The route parameters a given policy needs in order to be decidable at all. Declared once so that a test
 * iterating the whole catalogue supplies each policy's scope without restating the mapping — and so that
 * the mapping itself is stated in exactly one place, where it can be compared against the gate's own
 * scope table.
 *
 * @param policy A registered policy name.
 * @returns The parameters to place on the route, empty for the unscoped policies.
 */
function scopeFor(policy: (typeof SERVER_POLICY_NAMES)[number]): Record<string, string> {
  switch (policy) {
    case 'ModuleView':
    case 'ModuleEdit':
      return { moduleId: '5' };
    case 'TabView':
    case 'TabEdit':
      return { tabId: '9' };
    case 'AccountOwner':
    case 'AccountOwnerOrPortalAdministrator':
      return { userId: '7' };
    case 'PortalAdministrator':
    case 'HostAdministrator':
    case 'PortalContentEditor':
      return {};
  }
}

/** A trivial destination, so a real navigation has something to activate. */
@Component({ selector: 'app-guard-target', standalone: true, template: 'target' })
class GuardTargetComponent {}

/** The four members of the store the gate reads, each writable so a test can state them. */
interface AuthStoreDouble {
  /** Whether a session is held. Settled first by the gate, before any policy is examined. */
  readonly isAuthenticated: WritableSignal<boolean>;
  /** The fetched identity, which is null until it arrives even while a session is held. */
  readonly currentUser: WritableSignal<Identity | null>;
  /** The host-account flag, which alone confers the host-administration policy. */
  readonly isSuperUser: WritableSignal<boolean>;
  /**
   * Whether the caller administers the tenant it is signed in to. ⚠ WRITABLE INDEPENDENTLY OF THE ROLE
   * LIST, WHICH IS WHAT MAKES THE DERIVATION TESTABLE. The real store computes this as the host flag OR
   * the API's derived `isPortalAdministrator`, and consults no role name on the way.
   */
  readonly administersCurrentPortal: WritableSignal<boolean>;
  /**
   * The portal the caller's own session names. ⚠ NULLABLE AND DEFAULTED TO `null`, matching the real
   * store, which projects it from the fetched identity and therefore has nothing to report until that
   * identity arrives.
   */
  readonly portalId: WritableSignal<number | null>;
}

/**
 * A store double in the least-privileged state that still reports a session. Defaulted to "signed in,
 * identity not yet resolved, administers nothing, not a host account" so that a test which forgets to
 * state an identity exercises the deferred-identity path rather than being admitted by an accidentally
 * generous default.
 *
 * @returns Four independently writable signals.
 */
function authStoreDouble(): AuthStoreDouble {
  return {
    isAuthenticated: signal(true),
    currentUser: signal<Identity | null>(null),
    isSuperUser: signal(false),
    administersCurrentPortal: signal(false),
    portalId: signal<number | null>(null),
  };
}

/**
 * An identity carrying plausible values for every member of the real contract. ⚠ SENTINELS ARE POPULATED
 * RATHER THAN OMITTED. `portalId` is `0` and the display name is a real string, because the API
 * serialises without eliding empty values — so `0`, `''` and `false` all arrive as DATA. A fixture that
 * omitted them would describe a payload the API never sends.
 *
 * @param overrides The members a test cares about.
 * @returns A complete identity.
 */
function identity(overrides: Partial<Identity> = {}): Identity {
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
 * A guarded route declaring a policy. The declared value is typed `unknown` so that the deliberately
 * malformed declarations pass through exactly as a route table would carry them.
 *
 * @param path The route path.
 * @param permission The declared policy value, valid or otherwise.
 * @returns One route definition.
 */
function guarded(path: string, permission: unknown): Routes[number] {
  return { path, component: GuardTargetComponent, canActivate: [permissionGuard], data: { permission } };
}

/**
 * The route table under test. Four arrangements matter and all four are present: * `modules/:moduleId` is
 * COMPONENTLESS with children, which is what puts the identifier on a parent snapshot and the policy on a
 * child's.
 */
const routes: Routes = [
  { path: 'login', component: GuardTargetComponent },
  { path: 'start', component: GuardTargetComponent },

  // A route that forgot the key entirely, and routes whose value is not a usable name.
  { path: 'undeclared', component: GuardTargetComponent, canActivate: [permissionGuard] },
  guarded('wrong-type', 42),
  guarded('null-valued', null),
  guarded('undefined-valued', undefined),
  guarded('object-valued', { name: 'ModuleView' }),
  guarded('unregistered', 'ModuleDelete'),

  // ⚠ The policy declared under the WRONG KEY. `data` carries a value that would be legal
  // under `permission`, but the gate reads that one key and nothing else.
  {
    path: 'wrong-key',
    component: GuardTargetComponent,
    canActivate: [permissionGuard],
    data: { policy: 'PortalAdministrator' },
  },

  // The two unscoped policies.
  guarded('portal-admin', 'PortalAdministrator'),
  guarded('host-admin', 'HostAdministrator'),
  guarded('content-edit', 'PortalContentEditor'),

  // Scoped by the server's module key, with the child policy BENEATH the parameter.
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

  // Scoped policies declared with NO parameter segment at all.
  guarded('unscoped-module', 'ModuleEdit'),
  guarded('unscoped-tab', 'TabEdit'),
  guarded('unscoped-account', 'AccountOwner'),
];

describe('permissionGuard', () => {
  let store: AuthStoreDouble;
  let notify: jasmine.Spy;
  let retainAcrossNavigation: jasmine.Spy;
  let storeOffLimits: Record<string, jasmine.Spy>;
  let notifierOffLimits: Record<string, jasmine.Spy>;
  let harness: RouterTestingHarness;
  let router: Router;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    store = authStoreDouble();

    // Returns `undefined` because the real member returns void; what is measured is the
    // ARGUMENTS it was called with and how many times, never a value it hands back.
    notify = jasmine.createSpy('notify');

    retainAcrossNavigation = jasmine.createSpy('retainAcrossNavigation');

    storeOffLimits = offLimitsSpies(STORE_MEMBERS_OFF_LIMITS);
    notifierOffLimits = offLimitsSpies(NOTIFIER_MEMBERS_OFF_LIMITS);

    TestBed.configureTestingModule({
      providers: [
        // ⚠ ORDER IS LOAD-BEARING. The real transport is registered FIRST and the mock backend SECOND,
        // because the mock REPLACES the backend the first provider installed. Reversing them would leave
        // the live backend in place and this specification would attempt real requests.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(routes),
        {
          provide: AuthStore,
          useValue: {
            isAuthenticated: store.isAuthenticated,
            currentUser: store.currentUser,
            isSuperUser: store.isSuperUser,
            administersCurrentPortal: store.administersCurrentPortal,
            portalId: store.portalId,
            ...storeOffLimits,
          },
        },
        {
          provide: NotificationService,
          useValue: { notify, retainAcrossNavigation, ...notifierOffLimits },
        },
      ],
    });

    router = TestBed.inject(Router);
    httpMock = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create('/start');
  });

  afterEach(() => {
    // ⚠ MANDATORY, AND THE EXECUTABLE FORM OF "THE GATE PERFORMS NO I/O". It fails the specification if ANY
    // request was issued that a test did not expect, and since no test below expects one, every test in
    // this file silently asserts that the gate stayed offline.
    httpMock.verify();
  });

  /**
   * Puts the store into the state of a fully resolved ordinary account. The two projections are derived
   * from the identity's OWN members rather than set independently, so a fixture cannot accidentally
   * describe a store that disagrees with the identity it is holding — which is the drift that let a role
   * list and an administration flag tell two different stories.
   *
   * @param overrides The identity members a test cares about.
   */
  function signInAs(overrides: Partial<Identity> = {}): void {
    const resolved = identity(overrides);

    store.isAuthenticated.set(true);
    store.currentUser.set(resolved);
    store.isSuperUser.set(resolved.isSuperUser);

    // Mirrors the real store's own computation — the host flag OR the API's derived fact — so a test states
    // an IDENTITY and the projection follows from it, exactly as it does in the application. Note what is
    // absent: the role list contributes nothing.
    store.administersCurrentPortal.set(resolved.isSuperUser || resolved.isPortalAdministrator);

    // Projected from the identity for the same reason: the real store reads it off the fetched account, so
    // a fixture cannot describe a session signed in to one portal while claiming another.
    store.portalId.set(resolved.portalId);
  }

  /** Puts the store into the state of a resolved host account. */
  function signInAsHostAccount(): void {
    signInAs({ isSuperUser: true });
  }

  /**
   * Puts the store into the state of a resolved administrator of the current tenant. ⚠ NO ROLE NAME IS
   * SUPPLIED, and that is deliberate. Administration is carried by the server's derived fact; a fixture
   * that also handed over the role name would let a gate that had quietly returned to name matching keep
   * passing.
   */
  function signInAsPortalAdministrator(): void {
    signInAs({ isPortalAdministrator: true });
  }

  /**
   * Attempts an address through the REAL router and reports where the router ended up.
   *
   * @param url The address to attempt.
   * @returns The router's address once the navigation has settled.
   */
  async function attempt(url: string): Promise<string> {
    await harness.navigateByUrl(url);

    return router.url;
  }

  /**
   * A route snapshot double carrying declared data and route parameters. Built by DOUBLE ASSERTION rather
   * than by instantiating the router's own class, because that class exposes `pathFromRoot` and
   * `paramMap` as GETTERS with no setters — a real instance cannot be given an ancestry from outside.
   *
   * @param data The route's declared data, passed through exactly as written.
   * @param params The route's own parameters.
   * @returns A snapshot the gate can read.
   */
  function makeRoute(
    data: Record<string, unknown>,
    params: Record<string, string> = {},
  ): ActivatedRouteSnapshot {
    const snapshot = {
      data,
      paramMap: convertToParamMap(params),
    } as unknown as ActivatedRouteSnapshot;

    return Object.assign(snapshot, { pathFromRoot: [snapshot] });
  }

  /**
   * Rebuilds an ancestry so that the LAST snapshot is the activated one. Every snapshot shares one
   * ancestry array, which is how the router assembles it, so the gate sees the same root-to-leaf ordering
   * from whichever member it is handed.
   *
   * @param snapshots The ancestry, root first.
   * @returns The activated (deepest) snapshot.
   */
  function nest(...snapshots: readonly ActivatedRouteSnapshot[]): ActivatedRouteSnapshot {
    const ancestry = [...snapshots];

    for (const snapshot of ancestry) {
      Object.assign(snapshot, { pathFromRoot: ancestry });
    }

    return ancestry[ancestry.length - 1];
  }

  /**
   * A router-state double carrying only the attempted address.
   *
   * @param url The full attempted address, exactly as the router would serialise it.
   * @returns A router-state snapshot.
   */
  function makeState(url: string): RouterStateSnapshot {
    return { url } as unknown as RouterStateSnapshot;
  }

  /**
   * Invokes the gate directly, inside an injection context. A functional gate resolves its collaborators
   * with `inject`, which is legal only inside such a context — the router supplies one in production, and
   * `runInInjectionContext` supplies one here.
   *
   * @param route The activated route snapshot.
   * @param url The attempted address.
   * @returns Whatever the gate decided.
   */
  function runGuard(route: ActivatedRouteSnapshot, url = '/target'): boolean | UrlTree {
    const decision = TestBed.runInInjectionContext(() => permissionGuard(route, makeState(url)));

    // The gate is documented as fully synchronous — neither observable nor promise — so a narrowing that
    // accepted either would weaken the very claim being tested. This assertion is what makes the cast below
    // honest rather than hopeful.
    expect(typeof decision === 'boolean' || decision instanceof UrlTree)
      .withContext('the gate decides synchronously; it returns neither observable nor promise')
      .toBeTrue();

    return decision as boolean | UrlTree;
  }

  /**
   * Asserts that a declaration was refused, and that the refusal was reported correctly.
   *
   * @param route The snapshot to decide.
   * @param context A description of the case, quoted on failure.
   */
  function expectRefused(route: ActivatedRouteSnapshot, context: string): void {
    notify.calls.reset();

    expect(runGuard(route)).withContext(context).toBeFalse();

    expect(notify).toHaveBeenCalledTimes(1);

    const args: readonly unknown[] = notify.calls.mostRecent().args;

    expect(args[0]).withContext('a refusal is a warning, never a fault').toBe('warning');
    expect(args[1])
      .withContext('the shared app-authored denial stem, so a refusal reads as one wherever it is met')
      .toBe(ACCESS_REFUSED_MESSAGE);
    expect(args[2])
      .withContext('no support reference: nothing failed, so there is nothing to look up')
      .toBeNull();
    expect(args[3])
      .withContext('it does not outlive a navigation of its own accord')
      .toBeFalse();
    expect(args[4])
      .withContext('it retires itself, because it asks nothing of the reader and quotes nothing')
      .toBeTrue();
  }

  /**
   * Asserts that a declaration was admitted with nothing reported.
   *
   * @param route The snapshot to decide.
   * @param context A description of the case, quoted on failure.
   */
  function expectAdmitted(route: ActivatedRouteSnapshot, context: string): void {
    notify.calls.reset();

    expect(runGuard(route)).withContext(context).toBeTrue();
    expect(notify).not.toHaveBeenCalled();
  }

  // =========================================================================
  // 1 — THE DECLARED POLICY IS READ FROM ONE NAMED KEY, AND ONLY THAT KEY
  // =========================================================================
  describe('the route data key', () => {
    beforeEach(() => {
      signInAsHostAccount();
    });

    it('reads the declared policy from the "permission" key', () => {
      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'a policy declared under the documented key must be honoured',
      );
    });

    it('ignores the same policy declared under any other key, and fails closed', async () => {
      // ⚠ THE KEY IS PART OF THE CONTRACT. The route table declares `data: { permission: … }`, and a gate
      // that also accepted `policy`, `permissions` or `requires` would make the route table's own spelling
      // advisory — a route could then appear guarded while being wide open, which is the failure that
      // cannot be seen by reading either file alone.
      for (const key of ['policy', 'permissions', 'requires', 'Permission', 'PERMISSION']) {
        expectRefused(
          makeRoute({ [key]: 'PortalAdministrator' }),
          `"${key}" is not the declared key and must not be honoured`,
        );
      }

      // Proved once more through the real router, because a route table is where this
      // mistake actually gets made.
      expect(await attempt('/wrong-key')).toBe('/start');
    });

    it('reads only the declared key even when a decoy sits beside it', () => {
      // A route carrying both an unusable declaration and a legal-looking decoy must be refused. A gate
      // that scanned `data` for anything policy-shaped would be admitted here, and nothing else in this
      // file would catch it.
      expectRefused(
        makeRoute({ permission: 'ModuleDelete', policy: 'PortalAdministrator' }),
        'a decoy under another key must not rescue an unusable declaration',
      );
    });
  });

  // =========================================================================
  // 2 — THE CATALOGUE AGREES WITH THE SERVER, ALL NINE NAMES
  // =========================================================================
  describe('the declared policy catalogue', () => {
    beforeEach(() => {
      signInAsHostAccount();
    });

    it('admits a route declaring each of the nine policies the server registers', async () => {
      // ⚠ THE POSITIVE CONTROL FOR THE WHOLE FINDING, and the reason this block cannot be replaced by
      // refusal tests. Before the catalogue was corrected, three of these names were absent from the client
      // and every route declaring one was refused outright.
      const addresses: Record<(typeof SERVER_POLICY_NAMES)[number], string> = {
        ModuleView: '/modules/5',
        ModuleEdit: '/modules/5/settings',
        TabView: '/tabs/9',
        TabEdit: '/tab-edit/9',
        PortalAdministrator: '/portal-admin',
        HostAdministrator: '/host-admin',
        AccountOwner: '/users/7',
        AccountOwnerOrPortalAdministrator: '/accounts/7',
        PortalContentEditor: '/content-edit',
      };

      for (const policy of SERVER_POLICY_NAMES) {
        const target = addresses[policy];

        expect(await attempt(target))
          .withContext(`"${policy}" is registered server-side and must be declarable`)
          .toBe(target);
      }

      expect(notify).not.toHaveBeenCalled();
    });

    it('covers every policy the server registers, with none left untested', () => {
      // Guards the SUITE rather than the gate. If a TENTH policy is registered server-side and mirrored
      // into the gate, the map above stops being exhaustive and this fails loudly instead of quietly
      // testing nine of ten.
      expect(SERVER_POLICY_NAMES.length).toBe(9);
      expect(new Set(SERVER_POLICY_NAMES).size).toBe(9);
    });

    it('treats the catalogue as case-sensitive, refusing a differently-cased name', () => {
      // The server compares policy names with exact equality, so a case-folding client
      // would admit a route the server then faults on.
      for (const name of ['moduleview', 'MODULEVIEW', 'portaladministrator']) {
        expectRefused(makeRoute({ permission: name }), `"${name}" is not the registered spelling`);
      }
    });

    it('refuses a registered name carrying stray whitespace', () => {
      // Nothing trims the declaration, and nothing should: a padded name is a typo in the route table, and
      // silently repairing it would hide the typo while leaving the server to fault on the value it
      // actually receives.
      for (const name of [' ModuleView', 'ModuleView ', ' PortalAdministrator ']) {
        expectRefused(makeRoute({ permission: name }), `"${name}" is not the registered spelling`);
      }
    });
  });

  // =========================================================================
  // 3 — AN UNREGISTERED NAME FAILS CLOSED
  // =========================================================================
  describe('an unregistered policy name', () => {
    beforeEach(() => {
      signInAsHostAccount();
    });

    it('is refused at warning severity rather than being forwarded to the API', () => {
      // ⚠ WHY REFUSING IS STRICTLY SAFER THAN SHRUGGING. The API registers no policy provider that could
      // manufacture a policy on demand, so an unregistered name does NOT produce a tidy 403 at the endpoint
      // — it throws while the request is being authorised.
      for (const name of UNREGISTERED_POLICY_NAMES) {
        expectRefused(
          makeRoute({ permission: name }),
          `"${name}" is not a registered policy and must be refused`,
        );
      }
    });

    it('is refused even when the name merely looks like a registered one', async () => {
      expectRefused(makeRoute({ permission: 'ModuleDelete' }), 'ModuleDelete is not registered');

      expect(await attempt('/unregistered')).toBe('/start');
    });

    it('does not accept a registered name nested inside a longer string', () => {
      // A substring test in place of an equality test would admit all of these.
      for (const name of ['XModuleView', 'ModuleViewX', 'Module.View', 'ModuleView,TabView']) {
        expectRefused(makeRoute({ permission: name }), `"${name}" is not an exact registered name`);
      }
    });
  });

  // =========================================================================
  // 4 — A DECLARATION THAT IS NOT A USABLE STRING FAILS CLOSED
  // =========================================================================
  describe('a malformed declaration', () => {
    beforeEach(() => {
      signInAsHostAccount();
    });

    it('is refused when the key is absent, null, undefined or not a string at all', async () => {
      // ⚠ THIS IS WHY THE GATE MUST WIDEN TO `unknown`. The router types route data as an index signature
      // onto an unchecked type, so the property arrives with every compile-time guarantee switched off — it
      // could be missing, a number, a boolean or an object, and none of that would be caught at build time.
      const malformed: readonly Record<string, unknown>[] = [
        {},
        { permission: undefined },
        { permission: null },
        { permission: 42 },
        { permission: 0 },
        { permission: true },
        { permission: false },
        { permission: ['ModuleView'] },
        { permission: { name: 'ModuleView' } },
      ];

      for (const data of malformed) {
        expectRefused(makeRoute(data), `${JSON.stringify(data)} is not a usable declaration`);
      }

      // The same four shapes a route table can actually express, driven through the router.
      for (const path of ['/undeclared', '/wrong-type', '/null-valued', '/undefined-valued', '/object-valued']) {
        expect(await attempt(path)).withContext(`${path} must be refused`).toBe('/start');
      }
    });

    it('refuses a numeric zero declaration rather than treating it as absent-and-harmless', () => {
      expectRefused(makeRoute({ permission: 0 }), 'a numeric declaration is not a policy name');
    });
  });

  // =========================================================================
  // 5 and 6 — SCOPE RESOLUTION MIRRORS THE SERVER'S KEY NAMES EXACTLY
  // =========================================================================
  describe('scope resolution', () => {
    beforeEach(() => {
      signInAsHostAccount();
    });

    it('resolves a module scope from moduleId, the one key the server reads', async () => {
      expectAdmitted(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '5' }),
        'moduleId is the declared module scope key',
      );

      expect(await attempt('/modules/5')).toBe('/modules/5');
    });

    it('resolves a tab scope from tabId and an account scope from userId', () => {
      expectAdmitted(
        makeRoute({ permission: 'TabEdit' }, { tabId: '9' }),
        'tabId is the declared tab scope key',
      );

      expectAdmitted(
        makeRoute({ permission: 'AccountOwner' }, { userId: '7' }),
        'userId is the declared account scope key',
      );
    });

    it('requires no scope parameter for the two unscoped policies', () => {
      // Unscoped for two DIFFERENT reasons, and the distinction matters. Tenant administration HAS a scope,
      // but the API resolves the tenant itself — from the route's portal when it names one and from the
      // arrival tenant otherwise — so the route is free not to name it.
      expectAdmitted(makeRoute({ permission: 'PortalAdministrator' }), 'no scope is required');
      expectAdmitted(makeRoute({ permission: 'HostAdministrator' }), 'no scope is required');
    });

    it('does NOT accept a generic id in place of the server key', async () => {
      // ⚠ THE SECOND FINDING, STATED AS BEHAVIOUR. A removed revision listed `id` alongside each explicit
      // key, and the server has no such fallback: its authorisation handler reads `moduleId`, `tabId` and
      // `userId` and nothing else.
      expectRefused(
        makeRoute({ permission: 'ModuleEdit' }, { id: '5' }),
        'a bare id is not the module scope key',
      );
      expectRefused(
        makeRoute({ permission: 'TabEdit' }, { id: '9' }),
        'a bare id is not the tab scope key',
      );
      expectRefused(
        makeRoute({ permission: 'AccountOwner' }, { id: '7' }),
        'a bare id is not the account scope key',
      );

      for (const path of ['/legacy-modules/5', '/legacy-tabs/9', '/legacy-users/7']) {
        expect(await attempt(path)).withContext(`${path} names its record id`).toBe('/start');
      }
    });

    it('does not accept one scope key in place of another', () => {
      // A module policy scoped by a tab key describes an unanswerable question, and admitting
      // it would authorise a page identifier against a module.
      expectRefused(
        makeRoute({ permission: 'ModuleEdit' }, { tabId: '9' }),
        'a tab key does not scope a module policy',
      );
      expectRefused(
        makeRoute({ permission: 'TabEdit' }, { moduleId: '5' }),
        'a module key does not scope a tab policy',
      );
      expectRefused(
        makeRoute({ permission: 'AccountOwner' }, { moduleId: '5' }),
        'a module key does not scope an account policy',
      );
    });

    it('resolves an identifier held on an ANCESTOR when the policy sits on a child route', async () => {
      expect(await attempt('/modules/5/settings')).toBe('/modules/5/settings');
      expect(notify).not.toHaveBeenCalled();

      // The same shape, hand-built, so the ancestry walk is exercised directly.
      const parent = makeRoute({}, { moduleId: '5' });
      const child = makeRoute({ permission: 'ModuleEdit' });

      expectAdmitted(nest(parent, child), 'the identifier may sit on an ancestor snapshot');
    });

    it('prefers the NEAREST ancestor when the same key appears at more than one depth', () => {
      // The only ambiguity that can genuinely arise once the fallback is gone. The activated
      // screen is about the nearest identifier, so the walk runs deepest-first.
      const grandparent = makeRoute({}, { userId: '11' });
      const parent = makeRoute({}, { userId: '7' });
      const child = makeRoute({ permission: 'AccountOwner' });

      // The identity owns account 7 and not account 11, so admitting proves that 7 — the
      // nearer of the two — is the value that was resolved.
      signInAs({ userId: 7 });

      expectAdmitted(
        nest(grandparent, parent, child),
        'the nearest ancestor supplies the identifier',
      );
    });

    it('walks the ancestry without mutating it', () => {
      const parent = makeRoute({}, { moduleId: '5' });
      const child = makeRoute({ permission: 'ModuleEdit' });
      const activated = nest(parent, child);
      const orderBefore = [...activated.pathFromRoot];

      expect(runGuard(activated)).toBeTrue();

      // Compared by IDENTITY, element by element, rather than by deep equality: each snapshot holds the
      // ancestry array that holds it, so the structure is circular and a deep comparison would be answering
      // a much harder question than the one being asked.
      expect(activated.pathFromRoot.length).toBe(orderBefore.length);

      activated.pathFromRoot.forEach((snapshot, index) => {
        expect(snapshot)
          .withContext(`ancestry position ${index} must be left as the router assembled it`)
          .toBe(orderBefore[index]);
      });
    });

    // -----------------------------------------------------------------------
    // 5 — SENTINEL DISCIPLINE: ZERO AND MINUS ONE ARE REAL IDENTIFIERS
    // -----------------------------------------------------------------------
    it('accepts a scope identifier of "0", which is a real key in this schema', async () => {
      // ⚠ THE IDENTIFIER TRAP, AND IT IS A DOUBLE ONE. Module, tab and role keys are all declared
      // `IDENTITY(0, 1)` in the baseline schema, so `0` is DATA. A route parameter arrives as a STRING,
      // which makes `'0'` truthy while the number it denotes is falsy — so a presence test written as a
      // truthiness test appears to work right up until row zero is reached, and a test that converted first
      // would refuse it immediately.
      expectAdmitted(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '0' }),
        'module keys seed at zero, so "0" is a real identifier',
      );
      expectAdmitted(
        makeRoute({ permission: 'TabEdit' }, { tabId: '0' }),
        'tab keys seed at zero, so "0" is a real identifier',
      );

      expect(await attempt('/modules/0')).toBe('/modules/0');
      expect(await attempt('/tabs/0')).toBe('/tabs/0');
    });

    it('accepts "-1", which is both a real key and the legacy absent marker', async () => {
      expectAdmitted(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '-1' }),
        'minus one is a real identifier and must not be read as absence',
      );

      expect(await attempt('/modules/-1')).toBe('/modules/-1');
    });

    it('treats an EMPTY scope value as absent, and says so through a length test', () => {
      // The empty string is exactly what the legacy null contract returns for a missing string, so it is
      // the one value that genuinely means absence — and it is a case a route table cannot express, because
      // the router will not match an empty segment. Only direct invocation can reach it.
      expectRefused(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '' }),
        'an empty identifier is absence, not a record',
      );
    });

    it('refuses an identifier that does not denote an integer at all', () => {
      // The gate matches a strict sign-and-digits pattern before converting, because the generous
      // conversions are all wrong in a way that matters here: hexadecimal and exponent forms would coerce a
      // non-identifier into one, a padded value would be silently trimmed, and a digits-then-letters value
      // would be truncated to its numeric prefix.
      signInAs({ userId: 7 });

      for (const value of ['abc', '7abc', '0x10', '1e3', ' 7 ', '7.0', '+7']) {
        expectRefused(
          makeRoute({ permission: 'AccountOwner' }, { userId: value }),
          `"${value}" does not denote an account identifier`,
        );
      }
    });

    it('refuses an identifier too large to be represented exactly, rather than rounding it', () => {
      signInAs({ userId: 7 });

      for (const value of ['9007199254740993', '99999999999999999999', '-9007199254740993']) {
        expectRefused(
          makeRoute({ permission: 'AccountOwner' }, { userId: value }),
          `"${value}" cannot be represented exactly and must not be rounded into a match`,
        );
      }
    });

    it('accepts the largest identifier that IS represented exactly', () => {
      // The other side of the same boundary, so the range check is pinned as a boundary rather
      // than as a blanket refusal of large values.
      signInAs({ userId: Number.MAX_SAFE_INTEGER });

      expectAdmitted(
        makeRoute({ permission: 'AccountOwner' }, { userId: String(Number.MAX_SAFE_INTEGER) }),
        'an exactly representable identifier is usable',
      );
    });

    // -----------------------------------------------------------------------
    // 7 — A SCOPED POLICY WITH NO RESOLVABLE SCOPE FAILS CLOSED
    // -----------------------------------------------------------------------
    it('refuses a scoped policy whose route supplies no identifier', async () => {
      const scoped: readonly string[] = [
        'ModuleView',
        'ModuleEdit',
        'TabView',
        'TabEdit',
        'AccountOwner',
        'AccountOwnerOrPortalAdministrator',
      ];

      for (const policy of scoped) {
        expectRefused(
          makeRoute({ permission: policy }),
          `"${policy}" is scoped and cannot be decided without an identifier`,
        );
      }

      for (const path of ['/unscoped-module', '/unscoped-tab', '/unscoped-account']) {
        expect(await attempt(path)).withContext(`${path} declares no scope`).toBe('/start');
      }
    });
  });

  // =========================================================================
  // 8 and 9 — HOW A REFUSAL IS REPORTED: WARNING SEVERITY, PLAIN TEXT
  // =========================================================================
  describe('the refusal notice', () => {
    /**
     * Every distinct way the gate can refuse, so the reporting assertions below cover all three causes
     * rather than whichever one a test happened to reach.
     *
     * @returns One snapshot per refusal cause.
     */
    function everyRefusalCause(): readonly ActivatedRouteSnapshot[] {
      return [
        makeRoute({}),
        makeRoute({ permission: 42 }),
        makeRoute({ permission: 'ModuleDelete' }),
        makeRoute({ permission: 'ModuleEdit' }),
        makeRoute({ permission: 'PortalAdministrator' }),
        makeRoute({ permission: 'HostAdministrator' }),
        makeRoute({ permission: 'AccountOwner' }, { userId: '99' }),
      ];
    }

    beforeEach(() => {
      // An ordinary, fully resolved account, so the entitlement-based refusals are reachable
      // alongside the declaration-based ones.
      signInAs({ userId: 7, roles: ['Subscribers'] });
    });

    it('reports every refusal at exactly "warning" severity', () => {
      for (const route of everyRefusalCause()) {
        notify.calls.reset();

        expect(runGuard(route)).toBeFalse();
        expect(notify).toHaveBeenCalledTimes(1);

        const severity: unknown = notify.calls.mostRecent().args[0];

        expect(severity)
          .withContext('the first argument of every notify call must be the warning severity')
          .toBe('warning');
      }
    });

    it('never escalates a refusal to error severity, by argument or by alias', () => {
      for (const route of everyRefusalCause()) {
        expect(runGuard(route)).toBeFalse();
      }

      for (const call of notify.calls.all()) {
        expect(call.args[0])
          .withContext('no refusal may be reported as an error')
          .not.toBe('error');
      }

      for (const name of NOTIFIER_MEMBERS_OFF_LIMITS) {
        expect(notifierOffLimits[name])
          .withContext(`the gate must not reach NotificationService.${name}`)
          .not.toHaveBeenCalled();
      }
    });

    it('presents the refusal as PLAIN TEXT, never as markup', () => {
      for (const route of everyRefusalCause()) {
        expect(runGuard(route)).toBeFalse();
      }

      for (const call of notify.calls.all()) {
        const message: unknown = call.args[1];

        expect(typeof message).toBe('string');

        const text = typeof message === 'string' ? message : '';

        expect(text).not.toContain('<');
        expect(text).not.toContain('>');
        expect(text).not.toContain('&');
        expect(text.length).toBeGreaterThan(0);
      }
    });

    it('uses one wording for every cause, without disclosing which cause applied', () => {
      // MIGRATION: the wording is authored INLINE IN ENGLISH rather than resolved from a resource file.
      const messages = new Set<string>();

      for (const route of everyRefusalCause()) {
        notify.calls.reset();

        expect(runGuard(route)).toBeFalse();

        const message: unknown = notify.calls.mostRecent().args[1];

        messages.add(typeof message === 'string' ? message : '<not a string>');
      }

      expect(messages.size)
        .withContext('all refusal causes must present identical wording')
        .toBe(1);
      expect([...messages]).toEqual([ACCESS_REFUSED_MESSAGE]);
    });

    it('reports a refusal exactly once per decision', () => {
      // A gate that reported from more than one branch would stack duplicate notices on the
      // screen for a single refused navigation.
      notify.calls.reset();

      expect(runGuard(makeRoute({ permission: 'HostAdministrator' }))).toBeFalse();

      expect(notify).toHaveBeenCalledTimes(1);
    });

    it('passes no support reference alongside a refusal', () => {
      notify.calls.reset();

      expect(runGuard(makeRoute({ permission: 'HostAdministrator' }))).toBeFalse();

      expect(notify.calls.mostRecent().args[2])
        .withContext('a refusal quotes no support reference')
        .toBeNull();
    });
  });

  // =========================================================================
  // 10 — AN UNAUTHENTICATED CALLER IS REDIRECTED, NOT MERELY BLOCKED
  // =========================================================================
  describe('an unauthenticated caller', () => {
    beforeEach(() => {
      store.isAuthenticated.set(false);
      store.currentUser.set(null);
    });

    it('is redirected to the sign-in route with the attempted address preserved', () => {
      const decision = runGuard(makeRoute({ permission: 'PortalAdministrator' }), '/portals');

      expect(decision)
        .withContext('an unauthenticated caller must be redirected rather than refused')
        .toBeInstanceOf(UrlTree);

      // Narrowed by an explicit instance test rather than by a non-null assertion, so the
      // serialisation below cannot be reached with a boolean.
      const target = decision instanceof UrlTree ? router.serializeUrl(decision) : '';

      expect(target).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);
    });

    it('preserves a query string in the attempted address without double-encoding it', () => {
      // The router re-encodes the value when it serialises the tree, so encoding it again in
      // the gate would hand the sign-in screen an address it cannot navigate back to.
      const decision = runGuard(
        makeRoute({ permission: 'PortalAdministrator' }),
        '/users?page=2&query=smith',
      );
      const target = decision instanceof UrlTree ? router.serializeUrl(decision) : '';

      expect(target).toBe(
        `${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fusers%3Fpage%3D2%26query%3Dsmith`,
      );
    });

    it('is redirected WITHOUT a notification, because the redirect is itself the affordance', () => {
      runGuard(makeRoute({ permission: 'PortalAdministrator' }), '/portals');

      expect(notify).not.toHaveBeenCalled();
    });

    it('is redirected BEFORE the policy is examined, so a mis-declared route is not reported', async () => {
      // Ordering matters. An unauthenticated caller reaching a mis-configured route still lands on sign-in
      // rather than being told they lack access to something the route never named properly — which would
      // be a confusing report of the wrong problem.
      const decision = runGuard(makeRoute({ permission: 'ModuleDelete' }), '/unregistered');
      const target = decision instanceof UrlTree ? router.serializeUrl(decision) : '';

      expect(target).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Funregistered`);
      expect(notify).not.toHaveBeenCalled();

      expect(await attempt('/unregistered')).toBe(
        `${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Funregistered`,
      );
    });

    it('returns the redirect rather than performing the navigation itself', () => {
      // Returning a tree lets the router replace the in-flight navigation atomically;
      // calling `navigate` would race a second navigation against the first.
      const navigate = spyOn(router, 'navigate').and.resolveTo(true);
      const navigateByUrl = spyOn(router, 'navigateByUrl').and.resolveTo(true);

      runGuard(makeRoute({ permission: 'PortalAdministrator' }), '/portals');

      expect(navigate).not.toHaveBeenCalled();
      expect(navigateByUrl).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // 14 — THE COARSE ENTITLEMENT CHECKS, AND THE AUTHORITY THEY READ THEM FROM
  // =========================================================================
  describe('the coarse entitlement checks', () => {
    it('admits an administrator of the tenant to the tenant administration policy', () => {
      signInAsPortalAdministrator();

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'the administrator role satisfies tenant administration',
      );
    });

    it('refuses a caller holding the administrator role NAME when the server says otherwise', () => {
      // Stated here in the direction that a name-matching gate CANNOT pass: the caller carries the exact
      // role name the product creates, and the server's derived fact is false — a role named
      // `Administrators` in a tenant that designates a different role, which is precisely the arrangement
      // the old comparison mis-read.
      signInAs({ roles: [PORTAL_ADMINISTRATOR_ROLE], isPortalAdministrator: false });

      expectRefused(
        makeRoute({ permission: 'PortalAdministrator' }),
        'a role NAME confers no administration; the server\u2019s determination decides',
      );

      // And every near-miss spelling is equally irrelevant, for the same reason: the gate reads
      // no role name at all, so none of these can admit or refuse anything.
      for (const name of [
        'Administrator',
        'Administrations',
        'Portal Administrators',
        'administrators',
        'ADMINISTRATORS',
      ]) {
        signInAs({ roles: [name], isPortalAdministrator: false });

        expectRefused(
          makeRoute({ permission: 'PortalAdministrator' }),
          `"${name}" is not consulted, so it cannot admit`,
        );
      }
    });

    it('admits a RENAMED administrator role, because the name is not the authority', () => {
      signInAs({ roles: ['Tenant Owners', 'Editors'], isPortalAdministrator: true });

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'a renamed administrator role must not cost an administrator its own console',
      );
    });

    it('refuses an unrelated role that merely SHARES the administrator name', () => {
      // ⚠ THE SAME DEFECT IN THE OTHER DIRECTION, AND THE MORE DANGEROUS ONE. A role name is not unique
      // across the product — a tenant may give any role any name, so a caller can legitimately hold a role
      // called `Administrators` that carries no administration at all, and the designation of another
      // tenant's administrator role has nothing to do with it.
      signInAs({ roles: [PORTAL_ADMINISTRATOR_ROLE], isPortalAdministrator: false });

      expectRefused(
        makeRoute({ permission: 'PortalAdministrator' }),
        'holding a role that shares the name is not holding the administration',
      );
    });

    it('derives administration from the projection alone, never from the role list', () => {
      signInAsPortalAdministrator();

      runGuard(makeRoute({ permission: 'PortalAdministrator' }));
      runGuard(makeRoute({ permission: 'HostAdministrator' }));
      runGuard(makeRoute({ permission: 'AccountOwnerOrPortalAdministrator' }, { userId: '7' }));

      expect(storeOffLimits['roles'])
        .withContext('administration must come from the server projection, not a role name')
        .not.toHaveBeenCalled();
      expect(store.administersCurrentPortal()).toBeTrue();
    });

    it('admits a caller holding NO role at all when the server says it administers the tenant', () => {
      // ⚠ THE OTHER HALF OF THE RULE, and the half whose absence refuses working screens.
      signInAs({ roles: [], isPortalAdministrator: true });

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'the server\u2019s derived determination is the authority, not a name',
      );
    });

    it('admits a host account to the tenant administration policy', () => {
      // The API keeps this arm, so the client keeps it: a host account satisfies tenant administration.
      signInAsHostAccount();

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'a host account satisfies tenant administration',
      );
    });

    it('refuses an ordinary account the tenant administration policy', () => {
      signInAs({ roles: ['Subscribers'] });

      expectRefused(
        makeRoute({ permission: 'PortalAdministrator' }),
        'an ordinary account holds no tenant administration',
      );
    });

    it('grants the host policy on the host flag ALONE, never on the administrator role', () => {
      // ⚠ THE TWO ADMINISTRATION POLICIES ARE NOT INTERCHANGEABLE. Host administration exists for
      // operations that address no single portal — the tenant collection, tenant creation, aliases
      // addressed by their own global identifier.
      signInAsPortalAdministrator();

      expectRefused(
        makeRoute({ permission: 'HostAdministrator' }),
        'tenant administration does not confer host administration',
      );

      signInAsHostAccount();

      expectAdmitted(
        makeRoute({ permission: 'HostAdministrator' }),
        'the host flag alone confers host administration',
      );
    });

    it('applies NO entitlement check whatever to the four record-scoped policies', () => {
      // ⚠ THE LOAD-BEARING OMISSION. Answering a module or page policy would mean fetching the permission
      // records held against that one record and interpreting them — exactly the second authorisation
      // engine this gate must not become, and one free to disagree with the server.
      signInAs({ roles: ['Subscribers'] });

      expectAdmitted(
        makeRoute({ permission: 'ModuleView' }, { moduleId: '5' }),
        'a module policy is the API\u2019s to answer',
      );
      expectAdmitted(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '5' }),
        'a module policy is the API\u2019s to answer',
      );
      expectAdmitted(
        makeRoute({ permission: 'TabView' }, { tabId: '9' }),
        'a tab policy is the API\u2019s to answer',
      );
      expectAdmitted(
        makeRoute({ permission: 'TabEdit' }, { tabId: '9' }),
        'a tab policy is the API\u2019s to answer',
      );
    });

    it('refuses an account reaching a route that names somebody else', () => {
      // ⚠ THE ONE SCOPED QUESTION THE CLIENT MAY ANSWER, AND WHY IT IS NOT A SECOND AUTHORISATION ENGINE.
      // Every other scoped policy would require RE-DERIVING a stored permission record.
      signInAs({ userId: 7 });

      expectRefused(
        makeRoute({ permission: 'AccountOwner' }, { userId: '99' }),
        'an account does not own somebody else\u2019s record',
      );

      expectAdmitted(
        makeRoute({ permission: 'AccountOwner' }, { userId: '7' }),
        'the caller\u2019s own account is admitted by the same comparison',
      );
    });

    it('has NO administrator arm on the owner-only policy, but does on the combined one', () => {
      signInAs({ userId: 7, roles: [PORTAL_ADMINISTRATOR_ROLE], isPortalAdministrator: true });

      expectRefused(
        makeRoute({ permission: 'AccountOwner' }, { userId: '99' }),
        'tenant administration does not open another account\u2019s credential change',
      );

      expectAdmitted(
        makeRoute({ permission: 'AccountOwnerOrPortalAdministrator' }, { userId: '99' }),
        'the combined policy admits the administrator of the account\u2019s tenant',
      );
    });

    it('admits the combined policy on ownership alone, with no administration at all', () => {
      signInAs({ userId: 7, roles: ['Subscribers'] });

      expectAdmitted(
        makeRoute({ permission: 'AccountOwnerOrPortalAdministrator' }, { userId: '7' }),
        'ownership alone satisfies the combined policy',
      );

      expectRefused(
        makeRoute({ permission: 'AccountOwnerOrPortalAdministrator' }, { userId: '99' }),
        'neither ownership nor administration holds',
      );
    });

    it('admits a held session whose identity has not yet resolved, and lets the API decide', () => {
      // ⚠ PREVENTS A REAL DEFECT rather than guarding a hypothetical one.
      store.isAuthenticated.set(true);
      store.currentUser.set(null);
      store.administersCurrentPortal.set(false);
      store.isSuperUser.set(false);

      for (const policy of SERVER_POLICY_NAMES) {
        expectAdmitted(
          makeRoute({ permission: policy }, scopeFor(policy)),
          `"${policy}" must not be refused on an unresolved identity`,
        );
      }
    });

    it('still refuses a mis-declared route while the identity is unresolved', () => {
      // The deferral above applies to ENTITLEMENTS only. A declaration the gate cannot use is
      // a configuration mistake regardless of who is asking, so it is still refused.
      store.isAuthenticated.set(true);
      store.currentUser.set(null);

      expectRefused(
        makeRoute({ permission: 'ModuleDelete' }),
        'an unusable declaration is refused whoever is asking',
      );
      expectRefused(
        makeRoute({ permission: 'ModuleEdit' }),
        'a missing scope is refused whoever is asking',
      );
    });
  });

  // =========================================================================
  // 11 — THE GATE NEVER ASSERTS AUTHORISATION ON ITS OWN BEHALF
  // =========================================================================
  describe('no authorisation of its own', () => {
    /**
     * Drives the gate over every decision path, so the purity assertions cover all of them.
     *
     * @returns The number of decisions taken.
     */
    function exerciseEveryPath(): number {
      const cases: readonly ActivatedRouteSnapshot[] = [
        makeRoute({}),
        makeRoute({ permission: 42 }),
        makeRoute({ permission: 'ModuleDelete' }),
        makeRoute({ permission: 'ModuleEdit' }),
        makeRoute({ permission: 'ModuleView' }, { moduleId: '5' }),
        makeRoute({ permission: 'TabView' }, { tabId: '9' }),
        makeRoute({ permission: 'PortalAdministrator' }),
        makeRoute({ permission: 'HostAdministrator' }),
        makeRoute({ permission: 'AccountOwner' }, { userId: '7' }),
        makeRoute({ permission: 'AccountOwnerOrPortalAdministrator' }, { userId: '99' }),
      ];

      for (const route of cases) {
        runGuard(route);
      }

      return cases.length;
    }

    beforeEach(() => {
      // An administering identity, so `exerciseEveryPath` drives ADMISSIONS as well as
      // refusals and the purity assertions below cover both directions.
      signInAs({ userId: 7, isPortalAdministrator: true });
    });

    it('issues no HTTP request on any decision path', () => {
      expect(exerciseEveryPath()).toBeGreaterThan(0);

      httpMock.expectNone(PERMISSION_CATALOGUE_URL);
      httpMock.expectNone(CURRENT_USER_URL);
    });

    it('never consults the store\u2019s permission-key projection', () => {
      // ⚠ THE PROJECTION MUST NOT DECIDE ANYTHING. The store exposes the caller's permission keys and
      // documents itself as deciding nothing with them; the API's current-user contract likewise exposes
      // the keys while offering no membership test at all — there is no `HasPermission` and no `IsInRole`
      // to call.
      exerciseEveryPath();

      expect(storeOffLimits['permissions'])
        .withContext('the permission-key projection must not be consulted')
        .not.toHaveBeenCalled();
    });

    it('reaches none of the store members that would mutate the session or duplicate its logic', () => {
      exerciseEveryPath();

      for (const name of STORE_MEMBERS_OFF_LIMITS) {
        expect(storeOffLimits[name])
          .withContext(`the gate must not reach AuthStore.${name}`)
          .not.toHaveBeenCalled();
      }
    });

    it('re-decides on every invocation rather than caching a verdict', async () => {
      const route = makeRoute({ permission: 'PortalAdministrator' });

      signInAsPortalAdministrator();
      expectAdmitted(route, 'an administrator is admitted');

      signInAs({ roles: ['Subscribers'] });
      expectRefused(route, 'the same route is refused once the role is revoked');

      signInAsPortalAdministrator();
      expectAdmitted(route, 'and admitted again once the role is restored');

      // Proved through real navigations too, since that is how the gate is actually invoked.
      expect(await attempt('/portal-admin')).toBe('/portal-admin');

      signInAs({ roles: ['Subscribers'] });

      expect(await attempt('/start')).toBe('/start');
      expect(await attempt('/portal-admin')).toBe('/start');
    });

    it('decides two different routes independently within one session', () => {
      // A per-session cache keyed on nothing but the caller would answer the second route with
      // the first route's verdict.
      signInAsPortalAdministrator();

      expectAdmitted(makeRoute({ permission: 'PortalAdministrator' }), 'tenant administration holds');
      expectRefused(makeRoute({ permission: 'HostAdministrator' }), 'host administration does not');
      expectAdmitted(makeRoute({ permission: 'PortalAdministrator' }), 'and the first is unchanged');
    });

    it('leaves the route snapshot and its data untouched', () => {
      // The gate answers a question about the route; writing to it would make a later gate or
      // component observe a snapshot the router never produced.
      const route = makeRoute({ permission: 'PortalAdministrator' }, { moduleId: '5' });
      const dataBefore = JSON.stringify(route.data);

      signInAsPortalAdministrator();
      expect(runGuard(route)).toBeTrue();

      expect(JSON.stringify(route.data)).toBe(dataBefore);
      expect(route.paramMap.get('moduleId')).toBe('5');
    });
  });

  // =========================================================================
  // 12 and 13 — THE TWO VOCABULARIES, AND THE LEGACY REPRESENTATIONS
  // =========================================================================
  describe('vocabulary isolation', () => {
    beforeEach(() => {
      // A host account, so any refusal below is attributable to the VALUE alone.
      signInAsHostAccount();
    });

    it('refuses a persisted permission key handed in where a policy name belongs', () => {
      for (const key of PERSISTED_PERMISSION_KEYS) {
        expectRefused(
          makeRoute({ permission: key }),
          `"${key}" is a persisted permission key, not a policy name`,
        );
      }
    });

    it('refuses the lower-cased and mixed-case forms of a persisted key too', () => {
      for (const key of ['view', 'edit', 'View', 'Edit', 'read', 'write']) {
        expectRefused(
          makeRoute({ permission: key }),
          `"${key}" belongs to the persisted vocabulary`,
        );
      }
    });

    it('keeps the two vocabularies disjoint, so no value can be read as both', () => {
      // Guards the SUITE: were a persisted key ever added to the policy catalogue, the refusal assertions
      // above would start contradicting the admission assertions earlier in this file, and this states the
      // invariant that makes them consistent.
      const policies: readonly string[] = SERVER_POLICY_NAMES;

      for (const key of PERSISTED_PERMISSION_KEYS) {
        expect(policies).not.toContain(key);
      }
    });

    it('refuses a semicolon-delimited role string, the legacy flattened representation', () => {
      // MIGRATION: the delimited string is NOT carried forward. The legacy evaluator flattened grants into
      // a semicolon-delimited string and fed it to `PortalSecurity.IsInRoles(roles As String)`, which split
      // it on the delimiter.
      for (const value of [
        'Administrators;',
        ';Administrators;',
        'ModuleView;ModuleEdit',
        'Administrators;Subscribers;',
      ]) {
        expectRefused(
          makeRoute({ permission: value }),
          `"${value}" is a flattened role string, not a policy name`,
        );
      }
    });

    it('refuses a bracketed per-account pseudo-role, the legacy per-user grant encoding', () => {
      // MIGRATION: the bracketed form is not carried forward either — and it was an EVALUATION INPUT rather
      // than a display format.
      for (const value of ['[7]', '[7];', 'Administrators;[7];', '[-1]']) {
        expectRefused(
          makeRoute({ permission: value }),
          `"${value}" is a bracketed per-account grant, not a policy name`,
        );
      }
    });

    it('models no negation, because the legacy generation has none to model', () => {
      // Measured across `Library/Components/Security/`, a leading-bang role prefix, a prefix test and a
      // prefix strip all occur ZERO times, and so does any mention of denial. This generation of the
      // product grants and never revokes, so a role either appears in a grant or does not.
      for (const value of ['!ModuleView', '!PortalAdministrator', '!Administrators']) {
        expectRefused(
          makeRoute({ permission: value }),
          `"${value}" is not a registered policy; negation does not exist here`,
        );
      }
    });

    it('reproduces neither form of the legacy access-record gate', () => {
      // MIGRATION: the legacy record-level gate is not reproduced in EITHER of its two forms, because the
      // two disagree with each other.
      signInAs({ roles: [] });

      expectAdmitted(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '5' }),
        'the record-level question belongs to the API',
      );
      expectAdmitted(
        makeRoute({ permission: 'TabEdit' }, { tabId: '9' }),
        'the record-level question belongs to the API',
      );
    });
  });

  describe('the portal a tenant-administration address NAMES', () => {
    // ⚠ THESE PIN A DEFECT MEASURED AGAINST THE RUNNING API, NOT A HYPOTHETICAL. The policy previously
    // resolved NO scope, so the gate asked only "does this caller administer some portal" and admitted a
    // tenant administrator to another tenant's screens. Measured live as `setup_admin`, an administrator of
    // portal -1 and not a host: `/portals/-1` and `/portals/-1/aliases` answer 200, while `/portals/2`,
    // `/portals/2/aliases` and `/portals/0` all answer 403 auth.not_permitted.
    // `PortalAdministrationEvaluator` is explicit about it - it reads the same route key and then requires
    // `ReadTokenPortalId(user) == portalId` - so the screens rendered host-only content over five refused
    // reads, claimed fields "were not saved" on a load that never submitted anything, and asserted the
    // portal had no aliases that were never retrieved.

    /**
     * A tenant-administration address naming one portal.
     *
     * @param portalId The portal the address names.
     * @returns The snapshot to decide.
     */
    function addressNaming(portalId: string): ActivatedRouteSnapshot {
      return makeRoute({ permission: 'PortalAdministrator' }, { portalId });
    }

    it('refuses a tenant administrator the settings of a portal that is not theirs', () => {
      signInAs({ isPortalAdministrator: true, portalId: 0 });

      expectRefused(addressNaming('2'), 'the API answers 403 auth.not_permitted for another tenant');
    });

    it('refuses the aliases of a portal that is not theirs on the same ground', () => {
      signInAs({ isPortalAdministrator: true, portalId: 0 });

      expectRefused(addressNaming('2'), 'the aliases of another tenant are refused too');
    });

    it('admits a tenant administrator to their OWN portal', () => {
      signInAs({ isPortalAdministrator: true, portalId: 0 });

      expectAdmitted(addressNaming('0'), 'their own tenant answers 200');
    });

    it('admits a host account to ANY portal, as the server does', () => {
      signInAsHostAccount();

      expectAdmitted(addressNaming('2'), 'a host account is not bound to one tenant');
    });

    it('treats -1 as a REAL portal rather than an absent one', () => {
      // `Portals.PortalID` is seeded `IDENTITY(-1,1)`, so the first portal is -1 and the second 0. The
      // legacy sentinel vocabulary read -1 as "nothing", and a gate inheriting that reading would either
      // admit everyone to portal -1 or refuse its own administrator.
      signInAs({ isPortalAdministrator: true, portalId: -1 });

      expectAdmitted(addressNaming('-1'), 'the administrator of portal -1 administers portal -1');
      expectRefused(addressNaming('0'), 'and still not portal 0');
    });

    it('leaves a tenant-administration address that names NO portal exactly as it was', () => {
      // ⚠ THE REGRESSION GUARD FOR THIS VERY FIX. Declaring the portal a MANDATORY scope was tried first
      // and refused every account, module, role and settings screen outright, because those addresses
      // carry this policy without naming a portal - they act on the caller's own tenant implicitly. The
      // existing suite caught it at once. The binding is therefore applied only when an address names one.
      signInAs({ isPortalAdministrator: true, portalId: 0 });

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'an unscoped tenant-administration address must stay admitted',
      );
    });

    it('claims nothing when the address names a portal that is not an integer', () => {
      // A malformed address is not an unauthorised one. Answering it with a permission refusal would send
      // the caller to ask for rights that would not help; the API answers that case properly.
      signInAs({ isPortalAdministrator: true, portalId: 0 });

      expectAdmitted(addressNaming('abc'), 'a malformed identifier is the server\'s to answer');
    });

    it('refuses an ordinary member either way, own portal or not', () => {
      signInAs({ portalId: 0 });

      expectRefused(addressNaming('0'), 'administering nothing is refused even on the caller\'s own portal');
    });
  });
});
