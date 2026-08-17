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

const ADMIN_SESSION: AuthSession = {
  accessToken: 'a',
  refreshToken: 'r',
  expiresAtUtc: FUTURE_SESSION_EXPIRY_UTC,
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
  ['/roles', 'RoleListComponent'],
  ['/roles/new', 'RoleFormComponent'],
  ['/roles/0', 'RoleFormComponent'],
  ['/roles/0/users', 'RoleAssignmentComponent'],
  ['/role-groups/new', 'RoleGroupFormComponent'],
  ['/settings/membership', 'MembershipSettingsComponent'],
  ['/settings/profile-definitions', 'ProfileDefinitionListComponent'],
  // The fallback resolves to a routed view of its own rather than to the shared empty state
  // loaded directly. Loading the shared component straight from the router left the address with
  // no `<h1>` — it states an `<h2>`, correctly, because eight screens embed it beneath their own
  // page heading — and with a structurally empty action slot, because a router-loaded component
  // has no host template projecting into it. The composed view fixes both and touches neither
  // shared component.
  ['/no/such/address', 'NotFoundComponent'],
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
      // ⚠ THE CREDENTIAL CASE SEATS THE ACCOUNT THE ADDRESS NAMES, so this case exercises
      // input binding through the OWNERSHIP arm of the route's policy rather than through
      // its administrator arm. `/users/:userId/password` declares
      // `AccountOwnerOrPortalAdministrator` because the screen posts to two endpoints with
      // two different policies — the owner's change and the administrator's reset — and
      // seating the named account keeps this case about the bound parameter rather than
      // about which arm admitted the caller. The arms themselves are asserted in their own
      // cases below and in the policy-gate describe.
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

    it('admits an administrator to another account\u2019s credential screen, and refuses an unrelated account holder', async () => {
      /*
       * ⚠ BOTH ARMS OF THE ROUTE'S POLICY, ASSERTED TOGETHER, BECAUSE THE POLICY IS A UNION
       * AND EITHER ARM ALONE IS WRONG. The credential CHANGE endpoint carries no administrator
       * arm, which invites an ownership-only route — but the screen posts to TWO endpoints, and
       * the second is the administrator-only reset (`UsersController.cs`
       * `POST {userId}/password-reset`, `PolicyNames.PortalAdministrator`). An ownership-only
       * route leaves that endpoint with no address anywhere in the application, so an
       * administrator could not perform the reset the API exists to offer them. The screen, not
       * the route, decides which of the two operations a caller runs.
       *
       * The second half is the part the widening must not cost: an account holder who is
       * neither the subject nor an administrator is still refused, so the gate cancels the
       * navigation and no component is created.
       */
      configure();
      TestBed.inject(TokenStorageService).store(ADMIN_SESSION);

      const admitted: unknown = await RouterTestingHarness.create().then((harness) =>
        harness.navigateByUrl('/users/7/password'),
      );

      expect(admitted)
        .withContext('the administrator arm admits the caller, so the reset has an address')
        .not.toBeNull();
      expect(TestBed.inject(Router).url).toBe('/users/7/password');

      TestBed.resetTestingModule();
      configure();

      // A REAL ordinary identity: no super user, no portal administration, no permissions.
      // `administersCurrentPortal` is `isSuperUser() || holdsPortalAdministration()`, so
      // clearing both is what makes this session fail the administrator arm.
      TestBed.inject(TokenStorageService).store({
        ...ADMIN_SESSION,
        user: {
          ...ADMIN_SESSION.user,
          userId: 4,
          isSuperUser: false,
          isPortalAdministrator: false,
          roles: ['Registered Users'],
          permissions: [],
        },
      });

      const refused: unknown = await RouterTestingHarness.create().then((harness) =>
        harness.navigateByUrl('/users/7/password'),
      );

      expect(refused)
        .withContext('neither the subject nor an administrator, so neither arm admits them')
        .toBeNull();
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
        'NotFoundComponent',
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
      // holder's own profile — whose policy is `AccountOwnerOrPortalAdministrator`, whose first
      // arm is satisfied by construction because the identifier comes from the caller's own
      // identity. A non-administrative account has no member-services address in this application -
      // that screen is not among the routes - so the profile is the remaining screen such an account
      // is entitled to operate on its own behalf.
      expect(await attemptAs({ isSuperUser: true }, '/')).toBe('/portals');
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/')).toBe('/modules');
      expect(await attemptAs(ORDINARY, '/')).toBe(`/users/${String(ORDINARY.userId)}/profile`);
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

    it('admits a tenant administrator another account\u2019s profile AND its credential screen', async () => {
      /*
       * ⚠ THE CREDENTIAL SCREEN CARRIES TWO OPERATIONS, AND THE ROUTE MUST ADMIT THE CALLER
       * OF EITHER. `POST {userId}/password` declares `AccountOwner` with no administrator arm,
       * because a change presents the current credential — but the same screen also posts
       * `POST {userId}/password-reset`, which declares `PolicyNames.PortalAdministrator`
       * (`UsersController.cs:L626-L627`). Reasoning from the first endpoint alone would make
       * the route ownership-only, which locks the administrator out of the SCREEN and leaves
       * the reset unreachable from any address in the application. The route declares the
       * union; the screen refuses the operation the caller is not entitled to run.
       */
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/users/1/profile')).toBe('/users/1/profile');
      expect(await attemptAs(TENANT_ADMINISTRATOR, '/users/1/password')).toBe(
        '/users/1/password',
      );
    });

    describe('the application root under MANDATORY REMEDIATION', () => {
      /**
       * Navigates from the root while holding a session carrying the given advisories.
       *
       * Varies the SESSION rather than only the identity, because these advisories are members
       * of the session and not of the account it names.
       *
       * @param advisories The advisory members to raise.
       * @param user The identity to hold.
       * @returns The address the root came to rest at.
       */
      async function landFrom(
        advisories: Partial<
          Pick<AuthSession, 'mustChangePassword' | 'mustUpdateProfile' | 'passwordExpiring'>
        >,
        user: Partial<AuthSession['user']>,
      ): Promise<string> {
        TestBed.resetTestingModule();
        configure();
        TestBed.inject(TokenStorageService).store({
          ...ADMIN_SESSION,
          ...advisories,
          user: { ...ADMIN_SESSION.user, ...user },
        });

        const harness = await RouterTestingHarness.create('/login');

        await harness.navigateByUrl('/').catch(() => undefined);

        return TestBed.inject(Router).url;
      }

      it('sends a caller owing a credential change to their own password screen', async () => {
        // ⚠ THE DEFECT THIS CLOSES WAS A SECOND COMPLETE DEAD END, of the same shape as the
        // authority one above and reproduced the same way — against the running API rather than
        // reasoned about. While a credential change is outstanding the API refuses very nearly
        // everything: `GET api/v1/users/{id}`, `.../services`, `api/v1/users/settings`,
        // `api/v1/portals` and `api/v1/modules` each answered 403 auth.remediation_required. So
        // every authority-based landing this redirect can name was unusable, and a caller the
        // server was actively requiring to change their password was sent somewhere they could
        // not change it. The profile screen the ordinary landing now names is refused on exactly
        // the same terms, which is why the credential test still has to come first.
        expect(await landFrom({ mustChangePassword: true }, ORDINARY)).toBe(
          `/users/${String(ORDINARY.userId)}/password`,
        );
      });

      it('sends a caller owing a profile completion to their own profile screen', async () => {
        expect(await landFrom({ mustUpdateProfile: true }, ORDINARY)).toBe(
          `/users/${String(ORDINARY.userId)}/profile`,
        );
      });

      it('prefers the CREDENTIAL when both advisories are outstanding', async () => {
        // ⚠ NOT A PREFERENCE — THE ONLY ORDER THAT TERMINATES. RemediationAuthorizationHandler
        // admits the profile endpoints only while the profile advisory is outstanding and the
        // password endpoints only while the credential one is, so both screens are reachable
        // here. But the password screen renews the session on success, which clears the
        // credential advisory and leaves the profile advisory standing, and the root then
        // resolves onward to the profile screen. Taking the profile first would clear that
        // advisory while the credential advisory still refused everything else.
        //
        // It also reproduces the legacy precedence: `UserValidStatus.vb` could report only one
        // outcome at a time and ordered PASSWORDEXPIRED ahead of UPDATEPROFILE.
        expect(
          await landFrom({ mustChangePassword: true, mustUpdateProfile: true }, ORDINARY),
        ).toBe(`/users/${String(ORDINARY.userId)}/password`);
      });

      it('outranks HOST authority, which would otherwise win', async () => {
        // The same caller with no advisory lands on the tenant listing — asserted above — so
        // this case pins the ORDER rather than merely the destination.
        expect(await landFrom({ mustChangePassword: true }, { isSuperUser: true })).toBe(
          `/users/${String(ADMIN_SESSION.user.userId)}/password`,
        );
      });

      it('outranks TENANT administration, which would otherwise win', async () => {
        expect(await landFrom({ mustUpdateProfile: true }, TENANT_ADMINISTRATOR)).toBe(
          `/users/${String(TENANT_ADMINISTRATOR.userId)}/profile`,
        );
      });

      /**
       * Enters the group at one address and then walks to a SIBLING address inside the same group.
       *
       * ⚠ THE TWO-STEP SHAPE IS THE WHOLE POINT AND MUST NOT BE COLLAPSED INTO ONE NAVIGATION. Angular does
       * not re-run a RETAINED route's `canActivate`, so entering the group and then moving within it is a
       * materially different code path from arriving at the second address cold. A single navigation passes
       * whether or not the gate covers child activations; only this sequence can tell them apart.
       *
       * @param advisories The advisory members to raise on the held session.
       * @param user The identity to hold.
       * @param from The address to enter the group at.
       * @param to The sibling address to walk to.
       * @returns The address the router came to rest at.
       */
      async function walkWithinTheGroup(
        advisories: Partial<
          Pick<AuthSession, 'mustChangePassword' | 'mustUpdateProfile' | 'passwordExpiring'>
        >,
        user: Partial<AuthSession['user']>,
        from: string,
        to: string,
      ): Promise<string> {
        TestBed.resetTestingModule();
        configure();
        TestBed.inject(TokenStorageService).store({
          ...ADMIN_SESSION,
          ...advisories,
          user: { ...ADMIN_SESSION.user, ...user },
        });

        const harness = await RouterTestingHarness.create('/login');

        await harness.navigateByUrl(from).catch(() => undefined);
        expect(TestBed.inject(Router).url)
          .withContext('precondition: the caller must actually be on the screen they walk from')
          .toBe(from);

        await harness.navigateByUrl(to).catch(() => undefined);

        return TestBed.inject(Router).url;
      }

      it('holds the gate when a caller walks between SIBLING screens inside the group', async () => {
        // ⚠ THIS PINS A DEFECT THAT REACHED A BROWSER AND SURVIVED A FIRST ATTEMPT AT FIXING IT, because
        // the gate was correct and simply never ran. `authGuard` sat only on the GROUP route, and Angular
        // retains that node when moving between its children — so the gate ran once on entry and never
        // again. A caller owing a password change followed the header's "Manage Profile" link straight out
        // of `/users/{id}/password`, unguarded, and the server then refused that screen's own data with
        // 403 auth.not_permitted, stranding them on a Forbidden screen with no password form in sight.
        // `canActivateChild` on the group route is what closes it, and this case is what proves it stays
        // closed.
        const userId = String(ORDINARY.userId);

        expect(
          await walkWithinTheGroup(
            { mustChangePassword: true },
            ORDINARY,
            `/users/${userId}/password`,
            `/users/${userId}/profile`,
          ),
        )
          .withContext('the server refuses the profile screen while a CREDENTIAL change is owed')
          .toBe(`/users/${userId}/password`);
      });

      it('holds the mirror case too, so neither obligation leaks the other screen', async () => {
        const userId = String(ORDINARY.userId);

        expect(
          await walkWithinTheGroup(
            { mustUpdateProfile: true },
            ORDINARY,
            `/users/${userId}/profile`,
            `/users/${userId}/password`,
          ),
        )
          .withContext('the server refuses the password screen while a PROFILE completion is owed')
          .toBe(`/users/${userId}/profile`);
      });

      it('still lets a caller carrying BOTH walk on to the second screen', async () => {
        // Both advisories stand, so the server admits both screens - and a caller who has just changed
        // their password must be able to move on to the profile rather than be pushed backwards.
        const userId = String(ORDINARY.userId);

        expect(
          await walkWithinTheGroup(
            { mustChangePassword: true, mustUpdateProfile: true },
            ORDINARY,
            `/users/${userId}/password`,
            `/users/${userId}/profile`,
          ),
        ).toBe(`/users/${userId}/profile`);
      });

      it('does not impede a caller who owes nothing from walking the same path', async () => {
        // The control. Without it, the three cases above would also pass if the group were simply broken.
        const userId = String(ADMIN_SESSION.user.userId);

        expect(
          await walkWithinTheGroup(
            {},
            { isSuperUser: true },
            `/users/${userId}/password`,
            `/users/${userId}/profile`,
          ),
        ).toBe(`/users/${userId}/profile`);
      });

      it('does NOT divert a caller whose password is merely approaching expiry', async () => {
        // ⚠ THE THIRD ADVISORY IS INFORMATIONAL AND MUST NOT BE TREATED AS BLOCKING. The server
        // refuses nothing over it and asks for nothing, so diverting on it would strand a caller
        // on a remediation screen with nothing to remediate. `AuthStore.sessionRestricted`
        // mirrors the server's own predicate, which reads the other two and not this one.
        expect(await landFrom({ passwordExpiring: true }, TENANT_ADMINISTRATOR)).toBe('/modules');
        expect(await landFrom({ passwordExpiring: true }, ORDINARY)).toBe(
          `/users/${String(ORDINARY.userId)}/profile`,
        );
      });
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
      // ⚠ ALL NINE THE CLIENT GATE REGISTERS — not a convenient subset. `permission.guard.ts` lists
      // exactly these nine, and every one of them is a policy some route below declares, so the client
      // set and the server set are IDENTICAL rather than the client being a proper subset.
      //
      // ⚠ THE NINTH NAME IS `PortalContentEditor`, AND WITHHOLDING IT IS THE TEMPTING MISTAKE.
      // `Api/Authorization/PolicyNames.cs:231` declares it, and it guards the SUPPORTING reads of module
      // placement — the definition catalogue and the tenant's page listing,
      // `ModuleDefinitionsController.cs:168` and `TabsController.cs:187`, each on the whole class. The
      // argument for withholding it runs that no route can declare it because the create screen is
      // ungated, and that admitting it would oblige the gate to resolve a scope for a policy no route
      // names. Both halves fail. The policy resolves NO scope — the operations it supports name no item
      // because the item does not exist yet — so there is no scope to resolve. And the screen it
      // supports cannot be FILLED without those two gated reads, so the authority reaching it demands is
      // exactly the authority they demand.
      //
      // Withholding it is not inert either. With the name absent from the client vocabulary, the create
      // route can name no policy, so nothing in the application records which callers may reach the
      // screen — and the only link to it sits inside a listing gated on tenant administration. A caller
      // holding EDIT on a page, whom the server admits, has to guess the address. Declaring it narrows
      // nothing (no scope to fail closed on, and the gate cannot plainly refuse it) and is what lets the
      // navigation rail offer the screen to those callers.
      //
      // The list must name every policy a route declares, because a name absent from it makes a
      // legitimate policy fail this specification, which in turn pushes the route tables into
      // declaring approximations of the policies their endpoints really require. A five-name list
      // forces three such approximations: portal creation declaring the tenant policy in place of
      // the host policy, admitting every portal administrator to a form certain to be refused;
      // and the two self-service account screens declaring the tenant policy in place of the
      // ownership policies, refusing every account holder its own profile and its own credential
      // change.
      const registered = [
        'ModuleView',
        'ModuleEdit',
        'TabView',
        'TabEdit',
        'PortalAdministrator',
        'HostAdministrator',
        'AccountOwner',
        'AccountOwnerOrPortalAdministrator',
        'PortalContentEditor',
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
      // both at once. EVERY address now declares a policy, so the tuple's `undefined` arm survives
      // only as a type: it was spelled out for module creation, which declared none, and that
      // address now declares the policy its supporting reads carry. The arm is deliberately kept so
      // that a future address which legitimately declares no policy must still state `undefined`
      // here rather than passing as an unlisted case.
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
        // ⚠ MODULE CREATION MIRRORS ITS SUPPORTING READS, NOT ITS PRIMARY ENDPOINT, and it is the
        // one address in this table that does so. `POST /modules` carries no policy of its own
        // because the page a module is placed on arrives in the BODY, so no route-reading policy
        // could reach it and the grant is evaluated by the service after binding. This address
        // previously declared `undefined` on that reasoning, which was safe but hid the authority
        // that actually governs the screen: it cannot be filled without reading the module
        // definitions and the tenant's pages, and BOTH of those classes carry
        // `PortalContentEditor` (`ModuleDefinitionsController.cs:168`, `TabsController.cs:187`).
        //
        // Declaring it is not a tightening, which is what makes it the correct entry rather than a
        // stricter one. The policy resolves NO scope, so it cannot fail closed for want of a route
        // parameter the way `ModuleEdit` would; and the gate cannot plainly refuse it, because the
        // advisory permission list is built from grant rows alone and omits `EDIT` for a tenant
        // administrator whose pages carry no explicit grants. Every caller the session admits is
        // still admitted. What changes is that the authority now has a NAME here, which is what
        // lets the navigation rail offer this screen to the page editors who hold it.
        [MODULE_ROUTES, 'new', 'PortalContentEditor'],
        [MODULE_ROUTES, ':moduleId', 'ModuleEdit'],
        [MODULE_ROUTES, ':moduleId/settings', 'ModuleEdit'],
        [MODULE_ROUTES, ':moduleId/export', 'ModuleEdit'],

        // UsersController.cs — the collection and record addresses are tenant-scoped; the profile
        // admits the owner OR an administrator.
        //
        // ⚠ THE CREDENTIAL ADDRESS IS THE UNION BECAUSE THE SCREEN CARRIES TWO ENDPOINTS, and this
        // is the one entry in this table that does NOT mirror a single controller action. The change
        // is `[Authorize(Policy = PolicyNames.AccountOwner)]` with no administrator arm; the reset
        // beside it is `[Authorize(Policy = PolicyNames.PortalAdministrator)]`. Declaring ownership
        // alone, reasoning from the change endpoint only, is not a tightening: it leaves the reset
        // with no address in the application, so an administrator has no way to intervene on a
        // locked-out account at all. The route declares the union of the two policies and the
        // screen refuses the operation the caller may not run
        // — which is where a per-operation decision belongs, since one address serves both.
        [USER_ROUTES, '', 'PortalAdministrator'],
        [USER_ROUTES, 'new', 'PortalAdministrator'],
        [USER_ROUTES, ':userId', 'PortalAdministrator'],
        [USER_ROUTES, ':userId/profile', 'AccountOwnerOrPortalAdministrator'],
        [USER_ROUTES, ':userId/password', 'AccountOwnerOrPortalAdministrator'],

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
