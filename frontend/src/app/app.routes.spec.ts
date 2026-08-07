/**
 * The route table's own specification.
 *
 * WHY A SPECIFICATION FOR A CONFIGURATION OBJECT
 * ---------------------------------------------
 * A route table is the one part of the application whose defects are invisible to the
 * compiler AND to every component specification. A route declared after the catch-all
 * builds, emits its lazy chunk, appears in the configuration object and silently renders
 * the fallback. A parameter spelled `:id` instead of `:moduleId` resolves the same
 * address and leaves the screen's input at its default. A guard attached to the sign-in
 * route makes the application unreachable to everybody. None of those is a type error and
 * none of them is observable from inside the component that suffers it, so the route table
 * is asserted here, at the level where it is declared.
 *
 * TWO KINDS OF ASSERTION, DELIBERATELY BOTH
 * -----------------------------------------
 * The runtime cases NAVIGATE, through a real router with the real table, and read back
 * the component the router actually mounted. That is the only evidence that catches a
 * misordered literal, an unresolvable lazy import or a guard that refuses what it should
 * admit.
 *
 * The structural cases read the exported arrays directly. They catch the class of mistake
 * that a navigation cannot: a policy declared without the gate that answers it, a policy
 * name the client gate has never registered, a scoped policy on a route carrying no scope
 * to resolve. Each of those produces a route that works for the wrong reason, or that
 * refuses everybody while looking correct.
 *
 * ⚠ `withComponentInputBinding()` IS MANDATORY IN THE PROVIDERS BELOW, and it is not
 * mirroring `app.config.ts` for tidiness. Omitting it was measured: two screens —
 * `module-export` and `user-password` — declare their route identifier as a REQUIRED
 * SIGNAL input, and without the binder both raise `NG0950: Input is required but no value
 * is available yet` the moment an effect reads it. A required DECORATOR input, which
 * `portal-alias-list` uses, fails silently in the same situation instead. That asymmetry is
 * why the bound-value cases exist as well as the resolution cases: asserting the mounted
 * component class alone cannot tell a bound parameter from an unbound one.
 *
 * MIGRATION: there is nothing to port here. The legacy application had no route table at
 * all — every request arrived at `Website/Default.aspx` and the page resolved its content
 * from a numeric `TabId` in the query string, so which control answered a given address
 * was tenant CONFIGURATION held in the database rather than application structure. There
 * was correspondingly nothing a test could assert about it. This file asserts a contract
 * the migration introduced.
 */
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { APP_ROUTES } from './app.routes';
import { TokenStorageService } from './core/services/token-storage.service';
import { AUTH_ROUTES } from './features/auth/auth.routes';
import { MODULE_ROUTES } from './features/module/module.routes';
import { PORTAL_ROUTES } from './features/portal/portal.routes';
import { ROLE_ROUTES } from './features/role/role.routes';
import { USER_ROUTES } from './features/user/user.routes';

import type { AuthSession } from './core/models/auth.model';
import type { Route } from '@angular/router';

const ADMIN_SESSION: AuthSession = {
  accessToken: 'a',
  refreshToken: 'r',
  expiresAtUtc: '2030-01-01T00:00:00Z',
  mustChangePassword: false,
  mustUpdateProfile: false,
  passwordExpiring: false,
  user: {
    userId: 0,
    portalId: -1,
    portalName: 'P',
    username: 'admin',
    displayName: 'Admin',
    email: 'a@example.test',
    isSuperUser: true,
    isPortalAdministrator: false,
    roles: ['Administrators'],
    permissions: ['EDIT'],
  },
};

/** Every address the migration plan enumerates, with the component class it must resolve to. */
const EXPECTED: ReadonlyArray<readonly [string, string]> = [
  ['/login', 'LoginComponent'],
  ['/portals', 'PortalListComponent'],
  ['/portals/new', 'PortalFormComponent'],
  ['/portals/-1', 'PortalFormComponent'],
  ['/portals/0/settings', 'PortalSettingsComponent'],
  ['/portals/-1/aliases', 'PortalAliasListComponent'],
  ['/modules', 'ModuleListComponent'],
  ['/modules/new', 'ModuleFormComponent'],
  ['/modules/import', 'ModuleImportComponent'],
  ['/modules/0', 'ModuleFormComponent'],
  ['/modules/0/settings', 'ModuleSettingsComponent'],
  ['/modules/0/export', 'ModuleExportComponent'],
  ['/users', 'UserListComponent'],
  ['/users/new', 'UserFormComponent'],
  ['/users/0', 'UserFormComponent'],
  ['/users/0/profile', 'UserProfileComponent'],
  ['/users/0/password', 'UserPasswordComponent'],
  ['/roles', 'RoleListComponent'],
  ['/roles/new', 'RoleFormComponent'],
  ['/roles/0', 'RoleFormComponent'],
  ['/roles/0/users', 'RoleAssignmentComponent'],
  ['/role-groups/new', 'RoleGroupFormComponent'],
  ['/settings/membership', 'MembershipSettingsComponent'],
  ['/settings/profile-definitions', 'ProfileDefinitionListComponent'],
  ['/no/such/address', 'EmptyStateComponent'],
];

describe('APP_ROUTES', () => {
  function configure(): void {
    TestBed.configureTestingModule({
      providers: [
        // `withComponentInputBinding()` mirrors `app.config.ts` and is LOAD-BEARING rather than
        // incidental: `module-export` and `user-password` both declare their route identifier as a
        // REQUIRED signal input, so without the binder they raise NG0950 the moment an effect reads
        // it. Omitting it here reported two failures that were faults in this harness rather than in
        // the route table, and the two components are the proof that the application must enable it.
        provideRouter(APP_ROUTES, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
  }

  describe('runtime resolution with an administrator session held', () => {
    for (const [url, expected] of EXPECTED) {
      it(`resolves ${url} to ${expected}`, async () => {
        configure();
        TestBed.inject(TokenStorageService).store(ADMIN_SESSION);

        const harness = await RouterTestingHarness.create();
        const instance: unknown = await harness.navigateByUrl(url);

        expect(TestBed.inject(Router).url).withContext(`url for ${url}`).toBe(url);
        expect((instance as object).constructor.name)
          .withContext(`component for ${url}`)
          .toBe(expected);
      });
    }

    it('binds each route parameter to the input of the same name', async () => {
      // The path resolving is not sufficient evidence: a parameter spelled `:id` instead of
      // `:moduleId` resolves the same address and silently leaves the input at its default.
      // These four read the value the component actually received.
      const cases: ReadonlyArray<readonly [string, string, unknown]> = [
        ['/portals/7/aliases', 'portalId', 7],
        ['/modules/7/export', 'moduleId', 7],
        ['/users/7/password', 'userId', 7],
        ['/roles/7/users', 'roleId', 7],
      ];

      for (const [url, input, expected] of cases) {
        configure();
        TestBed.inject(TokenStorageService).store(ADMIN_SESSION);

        const harness = await RouterTestingHarness.create();
        const instance = (await harness.navigateByUrl(url)) as Record<string, unknown>;
        const held: unknown = instance[input];
        const value: unknown = typeof held === 'function' ? (held as () => unknown)() : held;

        expect(value).withContext(`${url} -> ${input}`).toBe(expected);
        TestBed.resetTestingModule();
      }
    });

    it('resolves the application root to the portals list', async () => {
      configure();
      TestBed.inject(TokenStorageService).store(ADMIN_SESSION);

      const harness = await RouterTestingHarness.create();
      const instance: unknown = await harness.navigateByUrl('/');

      expect(TestBed.inject(Router).url).toBe('/portals');
      expect((instance as object).constructor.name).toBe('PortalListComponent');
    });
  });

  describe('the session gate, exercised through the real table', () => {
    it('redirects an anonymous caller from a protected group to the sign-in screen', async () => {
      configure();

      const harness = await RouterTestingHarness.create();
      const instance: unknown = await harness.navigateByUrl('/roles');

      expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Froles');
      expect((instance as object).constructor.name).toBe('LoginComponent');
    });

    it('redirects an anonymous caller from a standalone leaf too', async () => {
      configure();

      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl('/settings/membership');

      expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fsettings%2Fmembership');
    });

    it('admits an anonymous caller to the sign-in screen and to the fallback', async () => {
      configure();

      const harness = await RouterTestingHarness.create();

      expect(((await harness.navigateByUrl('/login')) as object).constructor.name).toBe(
        'LoginComponent',
      );
      expect(((await harness.navigateByUrl('/nowhere')) as object).constructor.name).toBe(
        'EmptyStateComponent',
      );
    });
  });

  describe('structural contract', () => {
    it('declares the root redirect first and the catch-all last', () => {
      expect(APP_ROUTES[0]?.path).toBe('');
      expect(APP_ROUTES[0]?.redirectTo).toBe('portals');
      expect(APP_ROUTES[0]?.pathMatch).toBe('full');
      expect(APP_ROUTES[APP_ROUTES.length - 1]?.path).toBe('**');
    });

    it('reaches all five feature groups with loadChildren', () => {
      const groups = APP_ROUTES.filter((route) => route.loadChildren !== undefined);

      expect(groups.map((route) => route.path)).toEqual([
        'login',
        'portals',
        'modules',
        'users',
        'roles',
      ]);
    });

    it('attaches exactly one session gate to every protected root and to none other', () => {
      const gated = APP_ROUTES.filter((route) => (route.canActivate?.length ?? 0) > 0).map(
        (route) => route.path,
      );

      expect(gated).toEqual([
        'portals',
        'modules',
        'users',
        'roles',
        'role-groups/new',
        'settings/membership',
        'settings/profile-definitions',
      ]);

      // The two invariants stated by the guards themselves.
      expect(APP_ROUTES.find((route) => route.path === 'login')?.canActivate).toBeUndefined();
      expect(APP_ROUTES.find((route) => route.path === '**')?.canActivate).toBeUndefined();
    });

    it('pairs every policy declaration with a permission gate, and never the reverse', () => {
      const everyRoute: readonly Route[] = [
        ...APP_ROUTES,
        ...AUTH_ROUTES,
        ...PORTAL_ROUTES,
        ...MODULE_ROUTES,
        ...USER_ROUTES,
        ...ROLE_ROUTES,
      ];

      for (const route of everyRoute) {
        const declaresPolicy = route.data?.['permission'] !== undefined;
        const gates = (route.canActivate ?? []).some((guard) => guard.name === 'permissionGuard');

        expect(declaresPolicy)
          .withContext(`policy and gate must agree on ${String(route.path)}`)
          .toBe(gates);
      }
    });

    it('declares only policies the client gate has registered', () => {
      const registered = ['ModuleView', 'ModuleEdit', 'TabView', 'TabEdit', 'PortalAdministrator'];
      const declared = [
        ...APP_ROUTES,
        ...PORTAL_ROUTES,
        ...MODULE_ROUTES,
        ...USER_ROUTES,
        ...ROLE_ROUTES,
      ]
        .map((route) => route.data?.['permission'])
        .filter((policy): policy is string => policy !== undefined);

      expect(declared.length).toBeGreaterThan(0);

      for (const policy of declared) {
        expect(registered).withContext(`policy ${policy}`).toContain(policy);
      }
    });

    it('never declares a module-scoped policy on a route with no module identifier', () => {
      for (const route of MODULE_ROUTES) {
        const policy = route.data?.['permission'];

        if (policy === 'ModuleEdit' || policy === 'ModuleView') {
          expect(String(route.path))
            .withContext(`${String(route.path)} declares ${String(policy)} without a scope`)
            .toContain(':moduleId');
        }
      }
    });

    it('orders every literal segment above the parameter route that would swallow it', () => {
      const indexOf = (routes: readonly Route[], path: string): number =>
        routes.findIndex((route) => route.path === path);

      expect(indexOf(PORTAL_ROUTES, 'new')).toBeLessThan(indexOf(PORTAL_ROUTES, ':portalId'));
      expect(indexOf(MODULE_ROUTES, 'new')).toBeLessThan(indexOf(MODULE_ROUTES, ':moduleId'));
      expect(indexOf(MODULE_ROUTES, 'import')).toBeLessThan(indexOf(MODULE_ROUTES, ':moduleId'));
      expect(indexOf(USER_ROUTES, 'new')).toBeLessThan(indexOf(USER_ROUTES, ':userId'));
      expect(indexOf(ROLE_ROUTES, 'new')).toBeLessThan(indexOf(ROLE_ROUTES, ':roleId'));
    });

    it('re-attaches no session gate inside any barrel', () => {
      for (const route of [
        ...AUTH_ROUTES,
        ...PORTAL_ROUTES,
        ...MODULE_ROUTES,
        ...USER_ROUTES,
        ...ROLE_ROUTES,
      ]) {
        for (const guard of route.canActivate ?? []) {
          expect(guard.name)
            .withContext(`${String(route.path)} must not re-attach the session gate`)
            .not.toBe('authGuard');
        }
      }
    });

    it('gives every route a document title, except the ones that only redirect or group', () => {
      const leaves = [
        ...AUTH_ROUTES,
        ...PORTAL_ROUTES,
        ...MODULE_ROUTES,
        ...USER_ROUTES,
        ...ROLE_ROUTES,
        ...APP_ROUTES.filter((route) => route.loadComponent !== undefined),
      ];

      for (const route of leaves) {
        expect(route.title)
          .withContext(`title for ${String(route.path)}`)
          .toEqual(jasmine.any(String));
      }
    });
  });
});
