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

/**
 * The administrator session, re-keyed to a particular account.
 *
 * For the two self-service addresses, whose policies are decided against the account the
 * route NAMES rather than against a role. Everything else about the identity is unchanged,
 * so a case using this differs from {@link ADMIN_SESSION} in exactly the one member the
 * policy reads.
 *
 * @param userId The account the caller is.
 * @returns A held session for that account.
 */
function sessionForAccount(userId: number): AuthSession {
  return { ...ADMIN_SESSION, user: { ...ADMIN_SESSION.user, userId } };
}

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
  ['/users/0/services', 'MemberServicesComponent'],
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
      //
      // ⚠ THE CREDENTIAL CASE SEATS THE ACCOUNT HOLDER RATHER THAN THE ADMINISTRATOR, and
      // that is a property of the route rather than a convenience for the harness.
      // `/users/:userId/password` declares `AccountOwner`, matching the change endpoint's
      // own policy, which has NO administrator arm — so the administrator session, whose
      // account key is 0, is legitimately refused at `/users/7/password` and the harness
      // receives null. Seating account 7 is what makes this case exercise input binding
      // instead of accidentally re-asserting the gate.
      const cases: ReadonlyArray<readonly [string, string, unknown, AuthSession]> = [
        ['/portals/7/aliases', 'portalId', 7, ADMIN_SESSION],
        ['/modules/7/export', 'moduleId', 7, ADMIN_SESSION],
        ['/users/7/password', 'userId', 7, sessionForAccount(7)],
        ['/roles/7/users', 'roleId', 7, ADMIN_SESSION],
      ];

      for (const [url, input, expected, session] of cases) {
        configure();
        TestBed.inject(TokenStorageService).store(session);

        const harness = await RouterTestingHarness.create();
        const instance = (await harness.navigateByUrl(url)) as Record<string, unknown>;
        const held: unknown = instance[input];
        const value: unknown = typeof held === 'function' ? (held as () => unknown)() : held;

        expect(value).withContext(`${url} -> ${input}`).toBe(expected);
        TestBed.resetTestingModule();
      }
    });

    it('refuses the credential change to anybody but the account it names', async () => {
      // The complement of the case above, asserted in its own right because it is the
      // behaviour the route's policy exists for. An administrator — a HOST account here,
      // the widest identity this suite holds — is still not the account holder, so the
      // gate cancels the navigation and no component is created.
      configure();
      TestBed.inject(TokenStorageService).store(ADMIN_SESSION);

      const harness = await RouterTestingHarness.create();
      const instance: unknown = await harness.navigateByUrl('/users/7/password');

      expect(instance).toBeNull();
      expect(TestBed.inject(Router).url).not.toBe('/users/7/password');
    });

    it('resolves the application root to the portals list for a HOST account', async () => {
      // The session this suite holds is a host account, which is the one authority for which
      // the tenant listing is the right landing screen. The three other answers the root
      // resolver gives — tenant administrator, ordinary account holder, and nobody at all —
      // are asserted with the identities that produce them, in the two describes below.
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

    it('sends an anonymous caller from the application root to the sign-in screen, carrying NO return address', async () => {
      // ⚠ THE ABSENT `returnUrl` IS THE ASSERTION, not an omission in it. The root used to
      // redirect to `portals` before authentication was considered, so the session gate then
      // captured `/portals` as the address the caller had "asked for" — and after a
      // successful sign-in the screen dutifully returned them to the one listing a non-host
      // may not open, where the policy gate cancelled the navigation and left them on this
      // screen holding a valid token. A caller who typed the root asked for the application,
      // so nothing is captured and the sign-in falls through to the root resolver instead.
      configure();

      const harness = await RouterTestingHarness.create();
      const instance: unknown = await harness.navigateByUrl('/');

      expect(TestBed.inject(Router).url).toBe('/login');
      expect((instance as object).constructor.name).toBe('LoginComponent');
    });
  });

  describe('the policy gate, exercised through the real table', () => {
    /**
     * A held session for a caller with the authority described, driven through the real
     * custodian so the gate reads it exactly as it does in the application.
     *
     * @param overrides The identity members this caller differs in.
     * @returns The session to store.
     */
    function sessionFor(overrides: Partial<AuthSession['user']>): AuthSession {
      return { ...ADMIN_SESSION, user: { ...ADMIN_SESSION.user, ...overrides } };
    }

    /** The identity of a signed-in caller holding no administration of any kind. */
    const ORDINARY = {
      userId: 42,
      isSuperUser: false,
      isPortalAdministrator: false,
      roles: ['Subscribers'],
      permissions: [],
    } as const;

    /** The identity of a tenant administrator who is NOT a host account. */
    const TENANT_ADMINISTRATOR = {
      userId: 9,
      isSuperUser: false,
      isPortalAdministrator: true,
      roles: ['Administrators'],
      permissions: [],
    } as const;

    /**
     * Navigates as one caller and reports where the router came to rest.
     *
     * The resting address is the assertion surface rather than the navigation result,
     * because a refusal cancels the navigation and leaves the previous address standing —
     * which is distinguishable here without depending on how a cancelled navigation
     * happens to resolve.
     *
     * @param user The identity to hold.
     * @param url The address to attempt.
     * @returns The router's address once the attempt has settled.
     */
    async function attemptAs(
      user: Partial<AuthSession['user']>,
      url: string,
    ): Promise<string> {
      TestBed.resetTestingModule();
      configure();
      TestBed.inject(TokenStorageService).store(sessionFor(user));

      const harness = await RouterTestingHarness.create('/login');

      // The rejection is swallowed rather than allowed to fail the test: a cancelled
      // navigation is the OUTCOME being asserted, and how the router's promise settles for
      // one is not part of the contract. The resting address is.
      await harness.navigateByUrl(url).catch(() => undefined);

      return TestBed.inject(Router).url;
    }

    it('refuses a signed-in caller with no administration every administrative listing', async () => {
      // ⚠ FAILING CLOSED MEANS NOT MOUNTING THE SCREEN, NOT MERELY BEING REFUSED BY THE API.
      // All three listings were once reachable by any signed-in caller — the account and role
      // listings because their barrels declared no policy, and each screen then issued its
      // own reads on that caller's behalf to be answered 403. The endpoints require tenant
      // administration (`UsersController.cs:L349-L350`, `RolesController.cs:L284`,
      // `ModulesController.cs:L186-L187`), so the client now declines to navigate at all and
      // no request is issued.
      expect(await attemptAs(ORDINARY, '/users')).toBe('/login');
      expect(await attemptAs(ORDINARY, '/roles')).toBe('/login');
      expect(await attemptAs(ORDINARY, '/modules')).toBe('/login');
    });

    it('refuses a tenant administrator the host-only tenant creation form', async () => {
      // `PortalsController.cs:L388-L389` gates `POST /portals` on `HostAdministrator`. The
      // route declared `PortalAdministrator`, which is the BROADER predicate, so this caller
      // was admitted to a host-only form and learnt otherwise only on submission.
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/portals/new')).toBe('/login');
    });

    it('admits a host account to tenant creation', async () => {
      expect(await attemptAs({ isSuperUser: true }, '/portals/new')).toBe('/portals/new');
    });

    it('resolves the application root per authority rather than sending everybody to the host-only listing', async () => {
      // ⚠ THE DEFECT THIS CLOSES WAS A COMPLETE LOCKOUT OF EVERY NON-HOST OPERATOR, observed
      // in a browser against the running stack rather than reasoned about: the root redirected
      // to `portals` for every caller, the listing declares `HostAdministrator` because
      // `PortalsController.cs:L241` requires host authority to enumerate tenants, and a
      // refused navigation is CANCELLED — so a tenant administrator who signed in correctly
      // was returned to `/login?returnUrl=%2Fportals` with the sign-in form still mounted and
      // a warning about content they had never asked for.
      //
      // Each answer is the address that caller can actually open: the tenant listing for a
      // host, the first rail entry a tenant administrator's authority admits, and the account
      // holder's own services — whose policy is `AccountOwner`, satisfied by construction
      // because the identifier comes from the caller's own identity.
      expect(await attemptAs({ isSuperUser: true }, '/')).toBe('/portals');
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/')).toBe('/modules');
      expect(await attemptAs(ORDINARY, '/')).toBe(`/users/${String(ORDINARY.userId)}/services`);
    });

    it('admits an account holder to its OWN profile and credential screens', async () => {
      // ⚠ THE MISMATCH THIS CLOSES WAS A LOCKOUT RATHER THAN A LOOSENING. Both addresses
      // declared tenant administration, so an ordinary account holder was refused the two
      // self-service screens the endpoints exist to give them —
      // `UsersController.cs:L887-L888` admits the owner or an administrator to the profile,
      // and `:L575-L576` admits ONLY the owner to the credential change.
      expect(await attemptAs(ORDINARY, `/users/${ORDINARY.userId}/profile`)).toBe(
        `/users/${ORDINARY.userId}/profile`,
      );
      expect(await attemptAs(ORDINARY, `/users/${ORDINARY.userId}/password`)).toBe(
        `/users/${ORDINARY.userId}/password`,
      );
    });

    it('refuses an account holder somebody else\u2019s profile and credential screens', async () => {
      expect(await attemptAs(ORDINARY, '/users/1/profile')).toBe('/login');
      expect(await attemptAs(ORDINARY, '/users/1/password')).toBe('/login');
    });

    it('admits a tenant administrator another account\u2019s profile, but never its credential change', async () => {
      // The two ownership policies differ in exactly one place and it is load-bearing:
      // `AccountOwnerOrPortalAdministrator` has an administrator arm and `AccountOwner` has
      // none, because a change presents the current credential. An administrator who must
      // intervene uses the reset endpoint (`UsersController.cs:L626-L627`) from the
      // administrative account screen.
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/users/1/profile')).toBe('/users/1/profile');
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/users/1/password')).toBe('/login');
    });
  });

  describe('structural contract', () => {
    it('declares the root redirect first and the catch-all last', () => {
      expect(APP_ROUTES[0]?.path).toBe('');
      // A FUNCTION rather than a string, which is the structural half of the per-authority
      // landing. The resolution itself is asserted by navigating, above; this case exists so
      // that replacing the resolver with a fixed destination again cannot pass silently.
      expect(typeof APP_ROUTES[0]?.redirectTo).toBe('function');
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
      // ⚠ ALL EIGHT, WHICH IS THE WHOLE SET THE GATE AND THE API REGISTER — not a
      // convenient subset. `permission.guard.ts` lists exactly these eight and
      // `Api/Authorization/PolicyNames.cs` declares exactly these eight.
      //
      // A previous revision of this assertion listed only five, and the omission was not
      // inert: it made three legitimate policies fail a specification, which in turn
      // pushed the route tables into declaring approximations of the policies their
      // endpoints really required. Portal creation declared the tenant policy in place of
      // the host policy, admitting every portal administrator to a form certain to be
      // refused; the two self-service account screens declared the tenant policy in place
      // of the ownership policies, refusing every account holder its own profile and its
      // own credential change. Widening the list here is what let all three be corrected.
      const registered = [
        'ModuleView',
        'ModuleEdit',
        'TabView',
        'TabEdit',
        'PortalAdministrator',
        'HostAdministrator',
        'AccountOwner',
        'AccountOwnerOrPortalAdministrator',
      ];
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

    it('declares, for every address, the policy its own API endpoint declares', () => {
      // ⚠ THE ROUTE/POLICY CONTRACT, ASSERTED ADDRESS BY ADDRESS. Every expectation below is
      // transcribed from the controller attribute cited beside it, so this table is the client's
      // copy of the server's authorisation surface and drifting from it fails here.
      //
      // WHY IT HAS TO BE A TABLE AND NOT A RULE. The sibling assertions prove internal consistency
      // — a policy always paired with a gate, always a registered name, always scoped when it needs
      // a scope — and a table of wrong-but-consistent declarations satisfies all three. Several
      // defects shipped underneath them: tenant creation declared `PortalAdministrator` where the
      // endpoint requires `HostAdministrator`, all three collection screens declared nothing where
      // their endpoints require a policy, and the profile and credential addresses declared
      // `PortalAdministrator` where the endpoints require the two ownership policies.
      //
      // ⚠ A LOOSER DECLARATION IS AS WRONG AS A STRICTER ONE, WHICH IS WHY EQUALITY IS ASSERTED
      // RATHER THAN AN ORDERING. Too loose walks an operator into a screen the server will refuse;
      // too strict withholds a screen the server would allow, and the credential change managed
      // both at once. `undefined` is spelled out for the one address that legitimately declares no
      // policy, so an omission cannot pass as an unlisted case.
      const expected: readonly (readonly [readonly Route[], string, string | undefined])[] = [
        // PortalsController.cs — the listing and creation are HOST operations
        // (`[Authorize(Policy = PolicyNames.HostAdministrator)]` on both), while the record
        // addresses and every alias action are tenant-scoped.
        [PORTAL_ROUTES, '', 'HostAdministrator'],
        [PORTAL_ROUTES, 'new', 'HostAdministrator'],
        [PORTAL_ROUTES, ':portalId', 'PortalAdministrator'],
        [PORTAL_ROUTES, ':portalId/settings', 'PortalAdministrator'],
        [PORTAL_ROUTES, ':portalId/aliases', 'PortalAdministrator'],

        // ModulesController.cs — the listing and the import are tenant-scoped; the per-record
        // addresses carry the module-scoped grant.
        [MODULE_ROUTES, '', 'PortalAdministrator'],
        [MODULE_ROUTES, 'import', 'PortalAdministrator'],
        // Module creation declares no policy BY DESIGN and not by omission. `POST /modules` carries
        // no policy of its own because the page a module is placed on arrives in the BODY, so no
        // route-reading policy could reach it and any policy here would fail closed and refuse
        // everyone. The grant is evaluated by the service after binding. There is therefore no
        // server-side name for this address to mirror, and the inherited session gate is the whole
        // of the client-side requirement.
        [MODULE_ROUTES, 'new', undefined],
        [MODULE_ROUTES, ':moduleId', 'ModuleEdit'],
        [MODULE_ROUTES, ':moduleId/settings', 'ModuleEdit'],
        [MODULE_ROUTES, ':moduleId/export', 'ModuleEdit'],

        // UsersController.cs — the collection and record addresses are tenant-scoped; the profile
        // admits the owner OR an administrator; and the credential change is
        // `[Authorize(Policy = PolicyNames.AccountOwner)]` with NO administrator arm, which is
        // measured rather than chosen — a host account is refused another account's password by the
        // API itself, so widening the gate would only reopen the "screen renders, API refuses"
        // mismatch it was corrected to remove.
        [USER_ROUTES, '', 'PortalAdministrator'],
        [USER_ROUTES, 'new', 'PortalAdministrator'],
        [USER_ROUTES, ':userId', 'PortalAdministrator'],
        [USER_ROUTES, ':userId/profile', 'AccountOwnerOrPortalAdministrator'],
        [USER_ROUTES, ':userId/password', 'AccountOwner'],
        // ⚠ THE OTHER `AccountOwner` ADDRESS. All five member-services endpoints declare it with no
        // administrator arm, which is measured rather than chosen: the legacy panel operated on the
        // SIGNED-IN account and its container hid the tab from an administrator outright. Widening
        // this to the union policy would publish an affordance the legacy application refused.
        [USER_ROUTES, ':userId/services', 'AccountOwner'],

        // RolesController.cs gates the CLASS on tenant administration and no action overrides it,
        // so every address in the barrel names that one policy.
        [ROLE_ROUTES, '', 'PortalAdministrator'],
        [ROLE_ROUTES, 'new', 'PortalAdministrator'],
        [ROLE_ROUTES, ':roleId', 'PortalAdministrator'],
        [ROLE_ROUTES, ':roleId/users', 'PortalAdministrator'],

        // Declared directly by the root table: membership settings on UsersController and the
        // declarations on the class-gated ProfileDefinitionsController.
        [APP_ROUTES, 'role-groups/new', 'PortalAdministrator'],
        [APP_ROUTES, 'settings/membership', 'PortalAdministrator'],
        [APP_ROUTES, 'settings/profile-definitions', 'PortalAdministrator'],
      ];

      for (const [table, path, policy] of expected) {
        const route = table.find((candidate) => candidate.path === path);

        expect(route).withContext(`route ${path} must exist`).toBeDefined();
        expect(route?.data?.['permission'])
          .withContext(`policy declared at "${path === '' ? '(index)' : path}"`)
          .toBe(policy);

        // A declared policy is inert without the gate that reads it, so the two are asserted
        // together rather than in separate cases that could drift apart.
        if (policy !== undefined) {
          expect((route?.canActivate ?? []).some((guard) => guard.name === 'permissionGuard'))
            .withContext(`permission gate for "${path === '' ? '(index)' : path}"`)
            .toBeTrue();
        }
      }

      // Nothing outside the table may declare a policy: a new guarded address must be added above
      // with the controller policy it mirrors, not merely gated.
      const guarded = [
        ...APP_ROUTES,
        ...PORTAL_ROUTES,
        ...MODULE_ROUTES,
        ...USER_ROUTES,
        ...ROLE_ROUTES,
      ].filter((route) => route.data?.['permission'] !== undefined);

      expect(guarded.length).toBe(expected.filter(([, , policy]) => policy !== undefined).length);
    });

    it('leaves no feature listing ungated', () => {
      // The specific regression this pins: all three collection screens were once reachable
      // without a policy, each justified by the other two doing the same.
      for (const [name, routes] of [
        ['portals', PORTAL_ROUTES],
        ['users', USER_ROUTES],
        ['roles', ROLE_ROUTES],
      ] as ReadonlyArray<readonly [string, readonly Route[]]>) {
        const index = routes.find((route) => route.path === '');

        expect(index?.data?.['permission'])
          .withContext(`${name} listing must declare a policy`)
          .toEqual(jasmine.any(String));
      }
    });

    it('scopes every account-scoped policy to a route carrying the account identifier', () => {
      // The companion of the module-scoped assertion below. `permission.guard.ts` resolves these
      // two policies from a `userId` segment and REFUSES a route that declares one without it, so a
      // declaration on an unscoped address would fail closed and lock everybody out rather than
      // degrading quietly.
      for (const route of [...APP_ROUTES, ...USER_ROUTES, ...ROLE_ROUTES]) {
        const policy = route.data?.['permission'];

        if (policy === 'AccountOwner' || policy === 'AccountOwnerOrPortalAdministrator') {
          expect(String(route.path))
            .withContext(`${String(route.path)} declares ${String(policy)} without a scope`)
            .toContain(':userId');
        }
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
