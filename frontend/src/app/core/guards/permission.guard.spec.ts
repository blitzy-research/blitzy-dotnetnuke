/**
 * Specification for {@link permissionGuard}, the navigation gate that reads the
 * authorisation POLICY a route declares and refuses to mount a screen the caller
 * demonstrably cannot use.
 *
 * NET-NEW COVERAGE, NOT A PORTED TEST. The legacy tree contains ZERO automated tests of
 * any kind, so nothing here is a translation of a prior assertion. The behaviour under
 * test replaces a scatter of imperative per-page checks, each asked in its own load
 * handler and each navigating away by side effect. Two of them are quoted because they
 * DISAGREE WITH ONE ANOTHER, which is the whole reason a single gate had to be designed
 * rather than transliterated:
 *
 * ```vbnet
 * ' Website/admin/Portal/Portals.ascx.vb:L339-L341 — a host account is REQUIRED
 * If Not UserInfo.IsSuperUser Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 *
 * ' Website/admin/Modules/ModuleSettings.ascx.vb:L191-L193 — portal OR active-tab admin
 * If PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False _
 *         And PortalSecurity.IsInRoles(PortalSettings.ActiveTab.AdministratorRoles.ToString) = False Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 * ```
 *
 * The gate reproduces only their INTERSECTION and none of their individual quirks, so
 * this file pins the intersection and deliberately asserts nothing about the quirks.
 *
 * ⚠ WHAT THIS FILE IS GUARDING AGAINST, since every one of these fails SILENTLY:
 *
 *   1. **The policy catalogue drifting from the API's.** The client's list once held five
 *      names while the API registers eight, so three real policies could not be declared
 *      on a route at all — every one of them was refused for no reason a person could
 *      see. Nothing but a positive control per name catches that, because a suite of
 *      refusals passes just as happily against a gate that refuses everything.
 *   2. **A scope key the API does not read.** The module and tab keys once carried a
 *      generic `id` fallback with no server-side counterpart, so a route naming its
 *      record `id` satisfied the client and then failed at the endpoint — surfacing as an
 *      inexplicable 403 on a screen the gate had just admitted.
 *   3. **A truthiness test on an identifier.** Module, tab and role keys seed at zero and
 *      portal keys at minus one, so `0` and `-1` are DATA. A route parameter arrives as a
 *      STRING, which makes `'0'` truthy and hides the defect until the day row zero is
 *      reached.
 *   4. **A refusal escalated to error severity, or rendered as markup.** Both are
 *      one-token changes that no compiler objects to.
 *
 * ⚠ THE GATE IS AN AFFORDANCE, NEVER AN ENFORCEMENT POINT, AND THIS SPEC MUST NOT DRIFT
 * INTO TESTING IT AS ONE. Every policy-protected endpoint re-authorises server-side
 * against stored state and answers `403` on its own account, and that refusal is the
 * authoritative one. So no assertion below claims that an admitted caller will succeed,
 * and none asks the gate to evaluate a stored permission record. A gate that answered
 * those questions would be a second authorisation engine free to disagree with the first
 * — and in the refusing direction it would hide a screen the server would have served.
 *
 * TWO HARNESSES, EACH PROVING WHAT THE OTHER CANNOT, which is the load-bearing design
 * decision in this file:
 *
 *   * **A REAL ROUTER**, for everything touching route structure. The gate resolves a
 *     scope identifier by walking `route.pathFromRoot`, because route DATA is inherited
 *     down onto every snapshot while route PARAMETERS are not — a child beneath
 *     `modules/:moduleId` carries the policy on its own snapshot and the identifier on
 *     its parent's. A hand-built snapshot with a fabricated ancestry would test the
 *     fabrication rather than the router's genuine inheritance, and would agree with the
 *     gate even if the gate were wrong about where parameters live.
 *   * **DIRECT INVOCATION**, for everything a route table cannot express. A route table
 *     cannot declare a policy under a DIFFERENT data key, and it cannot carry a scope
 *     parameter whose value is the empty string, because the router will not match an
 *     empty segment. Those cases are driven through hand-built snapshots, and that
 *     harness is also where "this gate issues no request of any kind" is proved.
 *
 * The collaborators are doubles. The gate's four inputs — a held session, a resolved
 * identity, a host-account flag and a role list — must be varied INDEPENDENTLY, including
 * the combination where a session is held while the identity has not yet arrived, which
 * is a genuine window in the real store and the one that used to lock administrators out
 * of their own screens. Signal-backed doubles express those states honestly; driving the
 * real store to reach them would prove nothing about this gate.
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

/**
 * The refusal wording, spelled again rather than imported.
 *
 * The gate declares it as a module-private constant, so there is nothing to import, and
 * that is the arrangement it asks for: a derived expectation would agree with a reworded
 * constant and report success. Spelling it out means a change to the wording has to be
 * made deliberately in two places.
 */
const ACCESS_REFUSED_MESSAGE = 'You do not have access to this content.';

/** The sign-in route the gate redirects an unauthenticated caller to. Spelled again, likewise. */
const SIGN_IN_PATH = '/login';

/** The query-parameter key the attempted address travels under. Spelled again, likewise. */
const RETURN_URL_KEY = 'returnUrl';

/**
 * The name of the role `Library/Components/Portal/PortalController.vb:L1390` creates for a
 * new tenant — held here ONLY as the negative fixture the gate must ignore.
 *
 * ⚠ THIS NAME CONFERS NOTHING, AND PROVING THAT IS THE POINT OF KEEPING IT. The gate used
 * to admit the tenant-administration policy on `roles().includes('Administrators')`, which
 * was wrong three ways: administration is conferred by `Portals.AdministratorRoleId`, a
 * per-tenant COLUMN naming whichever role administers that tenant; `Roles.RoleName` is an
 * ordinary updatable column, so renaming the role stripped every administrator of their
 * screens; and a role of the same name may belong to a DIFFERENT tenant, which makes a name
 * match right about the word and wrong about the portal. The gate now reads the server's own
 * derived determination, and the specifications below hold this name on both sides of the
 * correction: a caller carrying it whose derived fact is false is REFUSED, and a caller
 * carrying no role at all whose derived fact is true is ADMITTED.
 */
const PORTAL_ADMINISTRATOR_ROLE = 'Administrators';

/**
 * The complete set of policy names the API registers, transcribed from its
 * `Authorization/PolicyNames.cs` and the registrations in
 * `Authorization`/`Extensions/AuthenticationExtensions.cs`.
 *
 * Held here as an INDEPENDENT copy on purpose. Importing the gate's own constant would
 * make the catalogue assertion a tautology — it would compare the list to itself and pass
 * for any list at all, including the five-name list that caused the defect. This copy is
 * the server's list, so the assertion genuinely asks whether the client agrees with the
 * server.
 *
 * ⚠ EIGHT, NOT FIVE. An earlier reading of this migration described the vocabulary as
 * closed at five. It is not, and the difference is not cosmetic: `HostAdministrator`,
 * `AccountOwner` and `AccountOwnerOrPortalAdministrator` are registered policies that a
 * route may legitimately declare, so treating them as unknown names would refuse three
 * working screens.
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

/**
 * Names that must NEVER be accepted as a policy, each for a stated reason.
 *
 * ⚠ THESE APPEAR IN THIS FILE ONLY AS NEGATIVE FIXTURES and must never appear in the
 * gate itself.
 *
 * The first six are plausible-sounding inventions — the shapes a well-meaning change
 * reaches for when adding a screen — and none is registered. `SuperUser` and
 * `AuthenticatedUser` describe a CALLER rather than a policy, and the empty string is the
 * value a route declares when somebody types the key and forgets the value.
 *
 * ⚠ `HostAdministrator` is deliberately ABSENT from this list even though an earlier
 * reading of the migration placed it here. It is a genuinely registered policy, and
 * asserting that it is refused would have locked in the very defect this file exists to
 * prevent.
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
 * The PERSISTED permission keys, which are a different vocabulary and must not be
 * accepted where a policy name belongs.
 *
 * ⚠ TWO CLOSED, NON-INTERCHANGEABLE SETS. These four are the values stored against
 * module, tab and folder records, compared with exact string equality — the member name
 * IS the persisted value, as at
 * `Library/Components/Security/Permissions/ModulePermissionController.vb:L36`. Measured
 * across the tree only `EDIT` and `VIEW` are ever evaluated, so `EDIT` covers create,
 * update AND delete; `READ` and `WRITE` are folder keys that never appear in a policy at
 * all. That vocabulary belongs to the shared permission directive, not to this gate, and
 * a value from one set arriving where the other is expected is a wiring mistake that must
 * fail closed rather than resolve to something plausible.
 */
const PERSISTED_PERMISSION_KEYS: readonly string[] = ['VIEW', 'EDIT', 'READ', 'WRITE'];

/**
 * The catalogue endpoint the gate must never call, spelled RELATIVELY.
 *
 * Relative because that is the only form that works in the deployed topology: the proxy
 * serves the application and forwards `/api/` to the API container, so the browser reaches
 * the API through the same origin that served the page. An absolute address would resolve
 * only inside the container network and fail from a browser — and because the test target
 * declares no `fileReplacements`, these specifications compile against the PRODUCTION
 * environment, where the base address is exactly this relative prefix. That is what makes
 * an absolute address here not merely wrong but undetectable until deployment.
 */
const PERMISSION_CATALOGUE_URL = '/api/v1/permissions';

/** The current-user endpoint, which the gate must never call either. Relative, likewise. */
const CURRENT_USER_URL = '/api/v1/auth/me';

/**
 * Members of {@link AuthStore} the gate must never reach, by their REAL names.
 *
 * ⚠ `permissions` IS THE IMPORTANT ONE. The store exposes a permission-key projection and
 * documents itself as deciding nothing with it; the API's own current-user contract
 * likewise exposes the keys while offering no membership test. Consulting them here would
 * build a second authorisation engine out of a projection that was never meant to
 * arbitrate. Installing the member as a spy is what makes "the gate adds no interpretation
 * of its own" an executable assertion rather than a comment.
 *
 * ⚠ `roles` IS THE ONE THAT RECORDS A CORRECTED DEFECT. The gate used to decide tenant
 * administration by testing the role list for a literal name; it now reads the store's
 * `administersCurrentPortal`, which is the server's own determination. Spying on `roles`
 * means any return to name matching fails here by name rather than surviving as a passing
 * test with a wrong premise.
 *
 * `holdsPortalAdministration` is off limits for a related reason. It is one ARM of the
 * answer — the derived fact alone, which the sign-in and renewal snapshots leave false for a
 * host account — and reading it directly would withhold every administrative screen from a
 * host account until the current-account read completed. The gate must take the combined
 * projection, never this arm on its own.
 *
 * The command members are off limits because a navigation gate must not mutate session
 * state as a side effect of deciding a route.
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
 * Members of {@link NotificationService} the gate must never reach.
 *
 * ⚠ THE SEVERITY ALIASES ARE THE POINT. A refusal must be raised at WARNING severity, and
 * the shortest way to break that is to call `error(…)` instead of `notify('warning', …)`.
 * Spying on every alias means the escalation is caught by name as well as by argument, so
 * neither route to an over-severe refusal is left unasserted.
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
 * The spies return `undefined`, which is deliberate rather than lazy: none of these
 * members is supposed to be consulted, so a meaningful return value would only make an
 * accidental consultation harder to notice.
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
 * The identity shape the gate consults, derived from the store rather than imported.
 *
 * Deriving it keeps this file's imports to the four modules it genuinely depends on while
 * still binding the fixture to the real contract: if the identity's `userId` were renamed
 * or retyped, this alias changes with it and the fixture below stops compiling. An
 * independent hand-written interface would have gone on compiling against a shape that no
 * longer existed.
 */
type Identity = NonNullable<ReturnType<AuthStore['currentUser']>>;

/**
 * The route parameters a given policy needs in order to be decidable at all.
 *
 * Declared once so that a test iterating the whole catalogue supplies each policy's scope
 * without restating the mapping — and so that the mapping itself is stated in exactly one
 * place, where it can be compared against the gate's own scope table.
 *
 * The return type is annotated rather than inferred, because an inferred union of
 * differently-shaped literals is not assignable to an index signature.
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
   * Whether the caller administers the tenant it is signed in to.
   *
   * ⚠ WRITABLE INDEPENDENTLY OF THE ROLE LIST, WHICH IS WHAT MAKES THE CORRECTION
   * TESTABLE. The real store computes this as the host flag OR the API's derived
   * `isPortalAdministrator`, and consults no role name on the way. Holding it as its own
   * signal here lets a specification state the two combinations that the previous
   * name-matching gate got wrong: carrying the role name while the fact is false, and
   * carrying no role at all while the fact is true.
   */
  readonly administersCurrentPortal: WritableSignal<boolean>;
}

/**
 * A store double in the least-privileged state that still reports a session.
 *
 * Defaulted to "signed in, identity not yet resolved, administers nothing, not a host
 * account" so that a test which forgets to state an identity exercises the
 * deferred-identity path rather than being admitted by an accidentally generous default.
 *
 * @returns Four independently writable signals.
 */
function authStoreDouble(): AuthStoreDouble {
  return {
    isAuthenticated: signal(true),
    currentUser: signal<Identity | null>(null),
    isSuperUser: signal(false),
    administersCurrentPortal: signal(false),
  };
}

/**
 * An identity carrying plausible values for every member of the real contract.
 *
 * ⚠ SENTINELS ARE POPULATED RATHER THAN OMITTED. `portalId` is `0` and the display name
 * is a real string, because the API serialises without eliding empty values — so `0`,
 * `''` and `false` all arrive as DATA. A fixture that omitted them would describe a
 * payload the API never sends.
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
 * A guarded route declaring a policy.
 *
 * The declared value is typed `unknown` so that the deliberately malformed declarations
 * pass through exactly as a route table would carry them. The gate's narrowing is then
 * exercised against the real shape the router delivers rather than a pre-narrowed one.
 *
 * @param path The route path.
 * @param permission The declared policy value, valid or otherwise.
 * @returns One route definition.
 */
function guarded(path: string, permission: unknown): Routes[number] {
  return { path, component: GuardTargetComponent, canActivate: [permissionGuard], data: { permission } };
}

/**
 * The route table under test.
 *
 * Four arrangements matter and all four are present:
 *
 *   * `modules/:moduleId` is COMPONENTLESS with children, which is what puts the
 *     identifier on a parent snapshot and the policy on a child's. That is the ancestry
 *     case the gate documents, and it cannot be reproduced with a flat table.
 *   * the `legacy-*` routes name their record `id` rather than the server's key, which is
 *     precisely the shape the removed fallback used to admit.
 *   * `login` is present and unguarded, because the gate redirects to it and the redirect
 *     has to be able to resolve.
 *   * `start` is the standing address every refused navigation leaves in place, which is
 *     how a refusal is told apart from an admission.
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

    // ⚠ A SECOND MEMBER THE GATE LEGITIMATELY REACHES, and it is not off limits. The refusal it
    // queues is raised DURING the navigation the gate is refusing, and the shell sweeps stale
    // notifications on every completed navigation - so without this exemption the one message
    // explaining the refusal would be queued and swept inside a single task and the operator
    // would be redirected with no explanation at all. Spied rather than stubbed silently so the
    // closing blocks can assert that the gate pairs every refusal with the exemption.
    retainAcrossNavigation = jasmine.createSpy('retainAcrossNavigation');

    // Installed on EVERY test rather than only on the ones that assert about them, so that
    // any test which accidentally drives the gate into consulting an entitlement, mutating
    // the session or escalating a severity records the fact where the closing blocks will
    // see it.
    storeOffLimits = offLimitsSpies(STORE_MEMBERS_OFF_LIMITS);
    notifierOffLimits = offLimitsSpies(NOTIFIER_MEMBERS_OFF_LIMITS);

    TestBed.configureTestingModule({
      providers: [
        // ⚠ ORDER IS LOAD-BEARING. The real transport is registered FIRST and the mock
        // backend SECOND, because the mock REPLACES the backend the first provider
        // installed. Reversing them would leave the live backend in place and this
        // specification would attempt real requests. The transport is present at all only
        // so that "this gate issues no request" can be asserted rather than assumed.
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
    // ⚠ MANDATORY, AND THE EXECUTABLE FORM OF "THE GATE PERFORMS NO I/O". It fails the
    // specification if ANY request was issued that a test did not expect, and since no test
    // below expects one, every test in this file silently asserts that the gate stayed
    // offline. That claim is what keeps the authoritative verdict on the server.
    httpMock.verify();
  });

  /**
   * Puts the store into the state of a fully resolved ordinary account.
   *
   * The two projections are derived from the identity's OWN members rather than set
   * independently, so a fixture cannot accidentally describe a store that disagrees with the
   * identity it is holding — which is the drift that let a role list and an administration
   * flag tell two different stories. The one test that needs them to disagree stages that
   * deliberately, by writing the projection directly.
   *
   * @param overrides The identity members a test cares about.
   */
  function signInAs(overrides: Partial<Identity> = {}): void {
    const resolved = identity(overrides);

    store.isAuthenticated.set(true);
    store.currentUser.set(resolved);
    store.isSuperUser.set(resolved.isSuperUser);

    // Mirrors the real store's own computation — the host flag OR the API's derived fact —
    // so a test states an IDENTITY and the projection follows from it, exactly as it does in
    // the application. Note what is absent: the role list contributes nothing.
    store.administersCurrentPortal.set(resolved.isSuperUser || resolved.isPortalAdministrator);
  }

  /** Puts the store into the state of a resolved host account. */
  function signInAsHostAccount(): void {
    signInAs({ isSuperUser: true });
  }

  /**
   * Puts the store into the state of a resolved administrator of the current tenant.
   *
   * ⚠ NO ROLE NAME IS SUPPLIED, and that is deliberate. Administration is carried by the
   * server's derived fact; a fixture that also handed over the role name would let a gate
   * that had quietly returned to name matching keep passing.
   */
  function signInAsPortalAdministrator(): void {
    signInAs({ isPortalAdministrator: true });
  }

  /**
   * Attempts an address through the REAL router and reports where the router ended up.
   *
   * The router's resulting address is the assertion surface rather than the navigation
   * promise's boolean, because the three outcomes are all distinguishable there without
   * depending on how a cancelled navigation happens to resolve: an admitted route becomes
   * the address, a refused one leaves the previous address standing, and a redirect lands
   * on the sign-in screen with the attempt preserved.
   *
   * ⚠ THE REJECTION IS DELIBERATELY NOT CAUGHT, AND THAT IS A CORRECTNESS PROPERTY OF THIS
   * WHOLE FILE RATHER THAN A STYLE CHOICE. Nothing the gate legitimately does rejects this
   * promise: the installed harness awaits `Router.navigateByUrl`, a gate returning `false`
   * raises `NavigationCancel` and RESOLVES `false`, and a gate returning a `UrlTree`
   * performs the redirect and resolves once it settles. Only a genuine `NavigationError` —
   * a component that throws while activating, a lazy import that fails, an unmatched
   * address — rejects.
   *
   * Swallowing that rejection would leave `router.url` standing at the PREVIOUS address,
   * which is the exact observation a refusal produces. Every case below that expects
   * `/start` would therefore pass on a crash it had nothing to do with, and the file's
   * central claim — that these addresses are refused BY THE GATE — would be unfalsifiable.
   * An unexpected failure is allowed to reject and fail the case that provoked it. If a
   * particular known cancellation ever needs special handling, it is narrowed to that exact
   * condition at that one call site rather than absorbed here for all fifteen.
   *
   * @param url The address to attempt.
   * @returns The router's address once the navigation has settled.
   */
  async function attempt(url: string): Promise<string> {
    await harness.navigateByUrl(url);

    return router.url;
  }

  /**
   * A route snapshot double carrying declared data and route parameters.
   *
   * Built by DOUBLE ASSERTION rather than by instantiating the router's own class, because
   * that class exposes `pathFromRoot` and `paramMap` as GETTERS with no setters — a real
   * instance cannot be given an ancestry from outside. A plain object has no getter to
   * collide with, so the ancestry can be stated.
   *
   * The snapshot is its own sole ancestor by default, which is the shape a top-level route
   * genuinely has. {@link nest} builds the multi-level case.
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
   * Rebuilds an ancestry so that the LAST snapshot is the activated one.
   *
   * Every snapshot shares one ancestry array, which is how the router assembles it, so the
   * gate sees the same root-to-leaf ordering from whichever member it is handed.
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
   * `url` is the single member the gate reads, and it reads it solely to preserve the
   * attempt across a redirect.
   *
   * @param url The full attempted address, exactly as the router would serialise it.
   * @returns A router-state snapshot.
   */
  function makeState(url: string): RouterStateSnapshot {
    return { url } as unknown as RouterStateSnapshot;
  }

  /**
   * Invokes the gate directly, inside an injection context.
   *
   * A functional gate resolves its collaborators with `inject`, which is legal only inside
   * such a context — the router supplies one in production, and `runInInjectionContext`
   * supplies one here.
   *
   * @param route The activated route snapshot.
   * @param url The attempted address.
   * @returns Whatever the gate decided.
   */
  function runGuard(route: ActivatedRouteSnapshot, url = '/target'): boolean | UrlTree {
    const decision = TestBed.runInInjectionContext(() => permissionGuard(route, makeState(url)));

    // The gate is documented as fully synchronous — neither observable nor promise — so a
    // narrowing that accepted either would weaken the very claim being tested. This
    // assertion is what makes the cast below honest rather than hopeful.
    expect(typeof decision === 'boolean' || decision instanceof UrlTree)
      .withContext('the gate decides synchronously; it returns neither observable nor promise')
      .toBeTrue();

    return decision as boolean | UrlTree;
  }

  /**
   * Asserts that a declaration was refused, and that the refusal was reported correctly.
   *
   * Every refusal in this file goes through here, so the severity and the plain-text
   * requirements are enforced on all of them at once rather than being remembered
   * case by case.
   *
   * @param route The snapshot to decide.
   * @param context A description of the case, quoted on failure.
   */
  function expectRefused(route: ActivatedRouteSnapshot, context: string): void {
    notify.calls.reset();

    expect(runGuard(route)).withContext(context).toBeFalse();

    expect(notify).toHaveBeenCalledTimes(1);
    expect(notify).toHaveBeenCalledWith('warning', ACCESS_REFUSED_MESSAGE);
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
      // A host account satisfies every coarse check the gate makes, so a refusal anywhere in
      // this block can only come from the DECLARATION being rejected — never from the
      // caller's entitlements. Without this the two causes would be indistinguishable.
      signInAsHostAccount();
    });

    it('reads the declared policy from the "permission" key', () => {
      // MIGRATION: the access question is now DECLARED AS DATA on the route and answered in
      // one place, replacing the imperative per-page tests that each asked it in their own
      // load handler and navigated away by side effect — `Portals.ascx.vb:L339-L341` and
      // `ModuleSettings.ascx.vb:L191-L193` are the canonical shapes. The consequence worth
      // pinning is that the set of guarded screens is now READABLE FROM THE ROUTE TABLE
      // instead of having to be discovered by reading every screen's implementation, and
      // that only holds while the gate reads the one key the route table writes.
      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'a policy declared under the documented key must be honoured',
      );
    });

    it('ignores the same policy declared under any other key, and fails closed', async () => {
      // ⚠ THE KEY IS PART OF THE CONTRACT. The route table declares
      // `data: { permission: … }`, and a gate that also accepted `policy`, `permissions` or
      // `requires` would make the route table's own spelling advisory — a route could then
      // appear guarded while being wide open, which is the failure that cannot be seen by
      // reading either file alone.
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
      // A route carrying both an unusable declaration and a legal-looking decoy must be
      // refused. A gate that scanned `data` for anything policy-shaped would be admitted
      // here, and nothing else in this file would catch it.
      expectRefused(
        makeRoute({ permission: 'ModuleDelete', policy: 'PortalAdministrator' }),
        'a decoy under another key must not rescue an unusable declaration',
      );
    });
  });

  // =========================================================================
  // 2 — THE CATALOGUE AGREES WITH THE SERVER, ALL EIGHT NAMES
  // =========================================================================
  describe('the declared policy catalogue', () => {
    beforeEach(() => {
      signInAsHostAccount();
    });

    it('admits a route declaring each of the eight policies the server registers', async () => {
      // ⚠ THE POSITIVE CONTROL FOR THE WHOLE FINDING, and the reason this block cannot be
      // replaced by refusal tests. Before the catalogue was corrected, three of these names
      // were absent from the client and every route declaring one was refused outright. A
      // suite that only proved refusals would have passed against that gate.
      //
      // Each policy is exercised at an address that supplies whatever scope it needs, so a
      // pass here means the name is accepted AND its scope requirement is satisfiable.
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

        expect(await attempt(target))
          .withContext(`"${policy}" is registered server-side and must be declarable`)
          .toBe(target);
      }

      expect(notify).not.toHaveBeenCalled();
    });

    it('covers every policy the server registers, with none left untested', () => {
      // Guards the SUITE rather than the gate. If a ninth policy is registered server-side
      // and mirrored into the gate, the map above stops being exhaustive and this fails
      // loudly instead of quietly testing eight of nine.
      expect(SERVER_POLICY_NAMES.length).toBe(8);
      expect(new Set(SERVER_POLICY_NAMES).size).toBe(8);
    });

    it('treats the catalogue as case-sensitive, refusing a differently-cased name', () => {
      // The server compares policy names with exact equality, so a case-folding client
      // would admit a route the server then faults on.
      for (const name of ['moduleview', 'MODULEVIEW', 'portaladministrator']) {
        expectRefused(makeRoute({ permission: name }), `"${name}" is not the registered spelling`);
      }
    });

    it('refuses a registered name carrying stray whitespace', () => {
      // Nothing trims the declaration, and nothing should: a padded name is a typo in the
      // route table, and silently repairing it would hide the typo while leaving the server
      // to fault on the value it actually receives.
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
      // ⚠ WHY REFUSING IS STRICTLY SAFER THAN SHRUGGING. The API registers no policy
      // provider that could manufacture a policy on demand, so an unregistered name does
      // NOT produce a tidy 403 at the endpoint — it throws while the request is being
      // authorised. Blocking here costs a correctly configured route nothing and spares a
      // mis-configured one an obscure server-side fault.
      //
      // MIGRATION: the fail-closed posture is inherited rather than invented. The legacy
      // sign-in handler at
      // `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L163` opened with
      // `Dim loginStatus As UserLoginStatus = UserLoginStatus.LOGIN_FAILURE`, so refusal was
      // the default outcome and only an affirmative result displaced it.
      for (const name of UNREGISTERED_POLICY_NAMES) {
        expectRefused(
          makeRoute({ permission: name }),
          `"${name}" is not a registered policy and must be refused`,
        );
      }
    });

    it('is refused even when the name merely looks like a registered one', async () => {
      // `ModuleDelete` is the shape a well-meaning change reaches for on a delete screen. It
      // is not registered, because the persisted `EDIT` key already covers create, update
      // AND delete — measured across the tree, only `EDIT` and `VIEW` are ever evaluated.
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
      // ⚠ THIS IS WHY THE GATE MUST WIDEN TO `unknown`. The router types route data as an
      // index signature onto an unchecked type, so the property arrives with every
      // compile-time guarantee switched off — it could be missing, a number, a boolean or an
      // object, and none of that would be caught at build time. The gate therefore annotates
      // the binding as `unknown` and narrows with an explicit `typeof` test, and these are
      // the cases that prove the narrowing is really there.
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
      // `0` is falsy, so a truthiness test on the DECLARATION would land in the same branch
      // as a missing key. The outcome happens to be identical here, and that is exactly why
      // it is asserted: the reason must be "not a string", not "falsy".
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
      // Unscoped for two DIFFERENT reasons, and the distinction matters. Tenant
      // administration HAS a scope, but the API resolves the tenant itself — from the
      // route's portal when it names one and from the arrival tenant otherwise — so the
      // route is free not to name it. Host administration has no portal binding of any kind
      // by design, because it exists precisely for operations addressing no single portal.
      expectAdmitted(makeRoute({ permission: 'PortalAdministrator' }), 'no scope is required');
      expectAdmitted(makeRoute({ permission: 'HostAdministrator' }), 'no scope is required');
    });

    it('does NOT accept a generic id in place of the server key', async () => {
      // ⚠ THE SECOND FINDING, STATED AS BEHAVIOUR. A removed revision listed `id` alongside
      // each explicit key, and the server has no such fallback: its authorisation handler
      // reads `moduleId`, `tabId` and `userId` and nothing else. Accepting `id` here would
      // let the client authorise one record while the endpoint authorised another — the exact
      // confusion the server refuses — and the disagreement would surface as an
      // unexplainable 403 on a screen this gate had just admitted.
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
      // ⚠ THE REASON A REAL ROUTER IS USED AT ALL. Route DATA is inherited down onto every
      // snapshot, but route PARAMETERS are not: the router's default inheritance strategy
      // leaves a parameter on the snapshot whose path segment declared it. So a child
      // beneath `modules/:moduleId` carries `ModuleEdit` on its own snapshot while the
      // identifier lives on its parent's. A gate reading only the activated snapshot would
      // refuse a correctly configured route, and a fabricated ancestry would have agreed
      // with the mistake.
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
      // The gate reverses a COPY of the ancestry. Reversing in place would be a side effect
      // in a function whose entire job is to answer a question, and it would silently corrupt
      // the router's own state for everything downstream.
      const parent = makeRoute({}, { moduleId: '5' });
      const child = makeRoute({ permission: 'ModuleEdit' });
      const activated = nest(parent, child);
      const orderBefore = [...activated.pathFromRoot];

      expect(runGuard(activated)).toBeTrue();

      // Compared by IDENTITY, element by element, rather than by deep equality: each snapshot
      // holds the ancestry array that holds it, so the structure is circular and a deep
      // comparison would be answering a much harder question than the one being asked.
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
      // ⚠ THE IDENTIFIER TRAP, AND IT IS A DOUBLE ONE. Module, tab and role keys are all
      // declared `IDENTITY(0, 1)` in the baseline schema, so `0` is DATA. A route parameter
      // arrives as a STRING, which makes `'0'` truthy while the number it denotes is falsy —
      // so a presence test written as a truthiness test appears to work right up until row
      // zero is reached, and a test that converted first would refuse it immediately. The
      // gate tests the string's LENGTH instead, and this pins that choice.
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
      // ⚠ THE COLLISION IS REAL, NOT HYPOTHETICAL. `Portals.PortalID` is declared
      // `IDENTITY(-1, 1)`, so the first tenant IS minus one — while
      // `Library/Components/Shared/Null.vb:L41-L45` returns `-1` from `NullInteger` as the
      // legacy encoding for a MISSING integer. One value therefore means both "a real row"
      // and "no row", and a gate that rejected non-positive identifiers would refuse two
      // legitimate records.
      expectAdmitted(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '-1' }),
        'minus one is a real identifier and must not be read as absence',
      );

      expect(await attempt('/modules/-1')).toBe('/modules/-1');
    });

    it('treats an EMPTY scope value as absent, and says so through a length test', () => {
      // The empty string is exactly what the legacy null contract returns for a missing
      // string (`Null.vb:L71-L75`), so it is the one value that genuinely means absence — and
      // it is a case a route table cannot express, because the router will not match an
      // empty segment. Only direct invocation can reach it.
      expectRefused(
        makeRoute({ permission: 'ModuleEdit' }, { moduleId: '' }),
        'an empty identifier is absence, not a record',
      );
    });

    it('refuses an identifier that does not denote an integer at all', () => {
      // The gate matches a strict sign-and-digits pattern before converting, because the
      // generous conversions are all wrong in a way that matters here: hexadecimal and
      // exponent forms would coerce a non-identifier into one, a padded value would be
      // silently trimmed, and a digits-then-letters value would be truncated to its numeric
      // prefix. Only the ACCOUNT policies compare the value numerically, so they are where
      // the consequence shows.
      signInAs({ userId: 7 });

      for (const value of ['abc', '7abc', '0x10', '1e3', ' 7 ', '7.0', '+7']) {
        expectRefused(
          makeRoute({ permission: 'AccountOwner' }, { userId: value }),
          `"${value}" does not denote an account identifier`,
        );
      }
    });

    it('refuses an identifier too large to be represented exactly, rather than rounding it', () => {
      // ⚠ A ROUNDED KEY COMPARES EQUAL TO A KEY IT IS NOT, which is the whole hazard. These
      // values are nothing but digits, so the pattern test admits them, and only the
      // exact-representation check stops them: converting `9007199254740993` yields
      // `9007199254740992`, so a gate that accepted the conversion would decide the ownership
      // of one account from the identifier of a different one. Refusing is the only answer
      // that cannot be silently wrong.
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
      // The API reaches the same conclusion from the other side and records it as "a
      // registration mistake, not a permission decision", on the reasoning that the
      // alternative would be to invent a key and grant against whatever it happened to
      // match. Refusing here reports the mistake at the point it can still be fixed.
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
     * Every distinct way the gate can refuse, so the reporting assertions below cover all
     * three causes rather than whichever one a test happened to reach.
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
      // ⚠ THE EVIDENCE IS THE LEGACY PAGE ITSELF. `Website/admin/Security/AccessDenied.ascx.vb`
      // presents the refusal with
      // `UI.Skins.Controls.ModuleMessage.ModuleMessageType.YellowWarning` on BOTH of its
      // branches — L43 for a query-string message and L45 for the localised default — and the
      // file contains NO access test of its own; its `Page_Load` only PRESENTS the refusal.
      // A refusal is therefore an expected outcome of asking for something one cannot have,
      // not a fault, and escalating it would misreport it.
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

      // ⚠ THE OTHER ROUTE TO THE SAME MISTAKE. The service offers one-word aliases, and
      // calling `error(…)` would escalate the severity without ever passing the string
      // `'error'` anywhere the assertion above could see it. Every alias is spied, so both
      // routes are closed.
      for (const name of NOTIFIER_MEMBERS_OFF_LIMITS) {
        expect(notifierOffLimits[name])
          .withContext(`the gate must not reach NotificationService.${name}`)
          .not.toHaveBeenCalled();
      }
    });

    it('presents the refusal as PLAIN TEXT, never as markup', () => {
      // ⚠ LEGACY RESOURCE TEXT IS UNTRUSTED. Across the in-scope legacy resource files a
      // substantial minority of entries carry HTML, and at least one carries a live script
      // element, so treating any of that wording as markup would be an injection vector. The
      // legacy page itself set the precedent by passing its message through
      // `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(…))` before displaying it. Nothing here
      // may be rendered as HTML, and the message must therefore contain no markup to begin
      // with.
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
      // MIGRATION: the wording is authored INLINE IN ENGLISH rather than resolved from a
      // resource file. `AccessDenied.ascx.vb:L45` read its text through
      // `Services.Localization.Localization.GetString("AccessDenied", …)`, and that mechanism
      // is deliberately not carried forward — the localisation package is out of scope for
      // this migration, so no translation runtime exists to resolve a key against. The legacy
      // resource remains the authority for the PHRASING only, which is why this assertion
      // pins the literal string rather than a lookup.
      //
      // ⚠ THE CAUSE IS DELIBERATELY WITHHELD. Naming it would tell a caller whether the
      // account, module or page they addressed exists and whether they merely lack a role —
      // a disclosure the API itself avoids by answering every refusal with a uniform 403.
      // The three causes are distinguishable in the logs, never on the screen.
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
      // The reference exists for a server-issued fault a person may quote to support. A
      // refusal is neither a fault nor server-issued, so quoting a reference would invite a
      // support conversation about a decision the client made locally.
      notify.calls.reset();

      expect(runGuard(makeRoute({ permission: 'HostAdministrator' }))).toBeFalse();

      expect(notify.calls.mostRecent().args.length)
        .withContext('a refusal carries a severity and a message and nothing else')
        .toBe(2);
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
      // Announcing a failure at the exact moment the application is already doing the helpful
      // thing — landing the caller on the screen that resolves the problem — would misreport
      // it.
      runGuard(makeRoute({ permission: 'PortalAdministrator' }), '/portals');

      expect(notify).not.toHaveBeenCalled();
    });

    it('is redirected BEFORE the policy is examined, so a mis-declared route is not reported', async () => {
      // Ordering matters. An unauthenticated caller reaching a mis-configured route still
      // lands on sign-in rather than being told they lack access to something the route never
      // named properly — which would be a confusing report of the wrong problem.
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
      // ⚠ THE REGRESSION TEST FOR THE DEFECT THIS GATE CARRIED. It used to decide tenant
      // administration with `roles().includes('Administrators')`. That reading was wrong three
      // ways at once: administration is conferred by `Portals.AdministratorRoleId`, a per-tenant
      // COLUMN naming whichever role administers that tenant; `Roles.RoleName` is an ordinary
      // updatable column, so renaming the role stripped every administrator of their screens;
      // and a role of the same name may belong to a DIFFERENT tenant, which makes a name match
      // right about the word and wrong about the portal.
      //
      // Stated here in the direction that a name-matching gate CANNOT pass: the caller carries
      // the exact role name the product creates, and the server's derived fact is false — a role
      // named `Administrators` in a tenant that designates a different role, which is precisely
      // the arrangement the old comparison mis-read.
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
      // ⚠ THE CASE THE ROLE-NAME MATCH GOT WRONG IN THE FIRST DIRECTION. The administrator
      // role is designated per tenant BY IDENTIFIER, and its name is an ordinary editable
      // field the role editor exposes — so a tenant that renames it keeps exactly the same
      // administrators. The gate used to compare the literal `Administrators`, so every one
      // of those administrators was refused here while the API admitted them: an
      // administration console its own administrators could not navigate.
      //
      // The server's verdict is affirmative and the role list says something else entirely,
      // which is the whole point of the fixture.
      signInAs({ roles: ['Tenant Owners', 'Editors'], isPortalAdministrator: true });

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'a renamed administrator role must not cost an administrator its own console',
      );
    });

    it('refuses an unrelated role that merely SHARES the administrator name', () => {
      // ⚠ THE SAME DEFECT IN THE OTHER DIRECTION, AND THE MORE DANGEROUS ONE. A role name is
      // not unique across the product — a tenant may give any role any name, so a caller can
      // legitimately hold a role called `Administrators` that carries no administration at
      // all, and the designation of another tenant's administrator role has nothing to do with
      // it. Matching the name showed that caller every administrative route.
      //
      // The identity states the colliding name and the server states the truth. The gate must
      // believe the server.
      signInAs({ roles: [PORTAL_ADMINISTRATOR_ROLE], isPortalAdministrator: false });

      expectRefused(
        makeRoute({ permission: 'PortalAdministrator' }),
        'holding a role that shares the name is not holding the administration',
      );
    });

    it('derives administration from the projection alone, never from the role list', () => {
      // The executable form of the correction, asserted as the absence of a read rather than
      // as an outcome. `roles` is installed as a spy on the off-limits list, so any
      // reintroduction of name matching — however it is spelled, and whatever result it
      // happens to produce — is recorded here.
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
      // ⚠ THE OTHER HALF OF THE CORRECTION, and the half that was breaking working screens. A
      // tenant may designate any role as its administrator — the designation is a column, and
      // the role is renameable — so a legitimate administrator routinely holds a role list that
      // contains nothing called `Administrators`. The old gate refused every one of them.
      signInAs({ roles: [], isPortalAdministrator: true });

      expectAdmitted(
        makeRoute({ permission: 'PortalAdministrator' }),
        'the server\u2019s derived determination is the authority, not a name',
      );
    });

    it('admits a host account to the tenant administration policy', () => {
      // The API keeps this arm, so the client keeps it: a host account satisfies tenant
      // administration.
      //
      // MIGRATION: the legacy condition that joined the two tests with `OrElse` — and thereby
      // redirected a host account AWAY from the screen — was defective and is deliberately not
      // carried across.
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
      // ⚠ THE TWO ADMINISTRATION POLICIES ARE NOT INTERCHANGEABLE. Host administration exists
      // for operations that address no single portal — the tenant collection, tenant creation,
      // aliases addressed by their own global identifier. Because tenant administration falls
      // back to the arrival tenant, using it on a global operation would ask a truthful but
      // irrelevant question and let an administrator of one tenant enumerate every tenant.
      // Admitting a portal administrator here would invent an entitlement the API never
      // issues.
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
      // ⚠ THE LOAD-BEARING OMISSION. Answering a module or page policy would mean fetching
      // the permission records held against that one record and interpreting them — exactly
      // the second authorisation engine this gate must not become, and one free to disagree
      // with the server. In the refusing direction that disagreement HIDES a screen the
      // server would have served, which is the worse of the two failures. An ordinary account
      // is therefore admitted to every record-scoped route and the API answers on its own
      // account.
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
      // ⚠ THE ONE SCOPED QUESTION THE CLIENT MAY ANSWER, AND WHY IT IS NOT A SECOND
      // AUTHORISATION ENGINE. Every other scoped policy would require RE-DERIVING a stored
      // permission record. Ownership is not of that kind: it is the equality of two
      // identifiers the client already holds — the identity's own `userId` against the
      // route's — and it is the very same comparison the server makes. The client cannot
      // reach a different answer, so making it here costs a navigation the server would
      // refuse anyway and saves mounting a screen that would then collapse.
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
      // The owner-only policy guards the credential change, and a change presents the current
      // credential — so only its holder can perform one. Admitting an administrator would
      // collapse the change and the reset into a single operation whose effect depended on
      // which fields were populated, which is the shape that previously allowed a credential
      // to be overwritten with no proof of entitlement. An administrator who must intervene
      // uses the reset, which carries tenant administration and is recorded as its own act.
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
      // ⚠ PREVENTS A REAL DEFECT rather than guarding a hypothetical one. A session can be
      // held while the identity behind it is not yet known, and on that evidence a genuine
      // administrator reports no administration and a genuine account holder reports no key at
      // all — so refusing there would lock the very operators these screens exist for out of
      // them, and would refuse an account holder its own credential change.
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
      // ⚠ THE EXECUTABLE FORM OF "THE SERVER IS AUTHORITATIVE". A gate that fetched a
      // permission catalogue or re-read the current user would be building a verdict of its
      // own — and would then have to keep that verdict in step with the server's, which is
      // the arrangement this design exists to avoid. The `afterEach` verification covers
      // every test in this file; these two expectations name the endpoints explicitly so the
      // intent survives a refactor of the harness.
      expect(exerciseEveryPath()).toBeGreaterThan(0);

      httpMock.expectNone(PERMISSION_CATALOGUE_URL);
      httpMock.expectNone(CURRENT_USER_URL);
    });

    it('never consults the store\u2019s permission-key projection', () => {
      // ⚠ THE PROJECTION MUST NOT DECIDE ANYTHING. The store exposes the caller's permission
      // keys and documents itself as deciding nothing with them; the API's current-user
      // contract likewise exposes the keys while offering no membership test at all — there
      // is no `HasPermission` and no `IsInRole` to call. Deciding from the projection would
      // build an authorisation engine out of data that was never meant to arbitrate.
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
      // A revoked administrator must lose the screen on the very next navigation rather than
      // keeping it until something is invalidated. Asserted in both directions, because a
      // cache would show up as either a stale admission or a stale refusal.
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
      // ⚠ TWO CLOSED, NON-INTERCHANGEABLE VOCABULARIES. These four are the values persisted
      // against module, tab and folder records, compared with exact string equality at
      // `Library/Components/Security/Permissions/ModulePermissionController.vb:L36`. Measured
      // across the tree only `EDIT` and `VIEW` are ever evaluated — so `EDIT` covers create,
      // update AND delete — while `READ` and `WRITE` are folder keys that never appear in a
      // policy at all. That vocabulary belongs to the shared permission directive, not to
      // this gate, and a value from one set arriving where the other is expected is a wiring
      // mistake that must fail closed rather than resolve to something plausible.
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
      // Guards the SUITE: were a persisted key ever added to the policy catalogue, the
      // refusal assertions above would start contradicting the admission assertions earlier
      // in this file, and this states the invariant that makes them consistent.
      const policies: readonly string[] = SERVER_POLICY_NAMES;

      for (const key of PERSISTED_PERMISSION_KEYS) {
        expect(policies).not.toContain(key);
      }
    });

    it('refuses a semicolon-delimited role string, the legacy flattened representation', () => {
      // MIGRATION: the delimited string is NOT carried forward. The legacy evaluator flattened
      // grants into a semicolon-delimited string and fed it to
      // `PortalSecurity.IsInRoles(roles As String)`, which split it on the delimiter. A
      // per-account grant is a first-class nullable identifier server-side, so nothing here
      // parses, builds or reproduces the delimited form — and a value carrying the delimiter
      // is a sign that something upstream still does.
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
      // MIGRATION: the bracketed form is not carried forward either — and it was an
      // EVALUATION INPUT rather than a display format. `ModulePermissionController.vb:L42`
      // feeds `"[" & objModulePermission.UserID.ToString & "]"` straight into
      // `PortalSecurity.IsInRoles`, so the encoding genuinely drove the decision. Per-account
      // grants are a first-class nullable identifier server-side, and this gate must never
      // parse or reproduce the encoding.
      for (const value of ['[7]', '[7];', 'Administrators;[7];', '[-1]']) {
        expectRefused(
          makeRoute({ permission: value }),
          `"${value}" is a bracketed per-account grant, not a policy name`,
        );
      }
    });

    it('models no negation, because the legacy generation has none to model', () => {
      // MIGRATION: measured across `Library/Components/Security/`, a leading-bang role prefix,
      // a prefix test and a prefix strip all occur ZERO times, and so does any mention of
      // denial. This generation of the product grants and never revokes, so a role either
      // appears in a grant or does not. A negated name is therefore not a policy with
      // inverted meaning — it is simply not a policy, and it fails closed like any other
      // unregistered value.
      for (const value of ['!ModuleView', '!PortalAdministrator', '!Administrators']) {
        expectRefused(
          makeRoute({ permission: value }),
          `"${value}" is not a registered policy; negation does not exist here`,
        );
      }
    });

    it('reproduces neither form of the legacy access-record gate', () => {
      // MIGRATION: the legacy record-level gate is not reproduced in EITHER of its two forms,
      // because the two disagree with each other. The collection form at
      // `ModulePermissionController.vb:L33-L50` compares the permission key WITHOUT
      // consulting the record's allow flag, while the same file requires the flag elsewhere,
      // and the tab controller splits the same way. Reproducing one half would embed a defect
      // and reproducing both is impossible, so the record-level question is left entirely to
      // the API — which is observable here as the record-scoped policies being admitted
      // without any local evaluation, for a caller holding no roles at all.
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
});
