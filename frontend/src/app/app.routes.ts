import type { Routes } from '@angular/router';

import { authGuard } from './core/guards/auth.guard';
import { permissionGuard } from './core/guards/permission.guard';

/**
 * The wording the fallback route renders.
 *
 * Held as a named constant rather than inlined in the route so that a
 * specification can assert the rendered text against the same string the route
 * supplies, instead of restating it and letting the two drift apart.
 *
 * It is phrased as a statement about the ADDRESS rather than about the
 * application's completeness — "no screen at this address" rather than "not built
 * yet" — because this route is permanent: as further feature routes are declared
 * above it, this is still the sentence an unrecognised URL produces, and a
 * sentence about unfinished work would then be wrong.
 */
export const NO_ROUTED_VIEW_MESSAGE =
  'No administration screen is available at this address.';

/**
 * The address the application root resolves to.
 *
 * Held as a named constant because it is a contract with a component that already
 * ships: `layout/header/header.component.ts:L14-L19` documents its identity
 * affordance as targeting the application root precisely so that the destination
 * is "declared in exactly one place", and names that place as this redirect. The
 * band deliberately does not name the portals list itself, so this constant is the
 * one declaration both sides depend on.
 */
export const ROOT_REDIRECT_PATH = 'portals';

/**
 * The application's top-level route table.
 *
 * Every screen is reached through a dynamic `import`, so no feature is part of the
 * initial bundle; the shell, the shared components and the global stylesheet are,
 * and nothing else. `withPreloading(PreloadAllModules)` in `app.config.ts` then
 * fetches the lazy chunks in the background once the first navigation has settled,
 * so the small initial payload costs nothing on a subsequent navigation.
 *
 * SHAPE OF THE TABLE
 * ------------------
 * Five feature groups are reached with `loadChildren` and a route barrel — the
 * arrangement the migration plan specifies — and three screens are reached
 * directly with `loadComponent` because their addresses are not children of any
 * group. The groups are:
 *
 * ```text
 *   login        -> features/auth/auth.routes.ts        AUTH_ROUTES     (ungated)
 *   portals      -> features/portal/portal.routes.ts    PORTAL_ROUTES   (authGuard)
 *   modules      -> features/module/module.routes.ts    MODULE_ROUTES   (authGuard)
 *   users        -> features/user/user.routes.ts        USER_ROUTES     (authGuard)
 *   roles        -> features/role/role.routes.ts        ROLE_ROUTES     (authGuard)
 * ```
 *
 * Each screen declares the tenant, module, account or role it operates on as a
 * route PARAMETER, and `withComponentInputBinding()` delivers that parameter
 * straight into a declared component input. A routed screen therefore never
 * injects `ActivatedRoute`, which is what keeps these components presentational
 * and directly constructible in a specification.
 *
 * ⚠ PARAMETER NAMES ARE A RUNTIME CONTRACT. `:portalId`, `:moduleId`, `:userId` and
 * `:roleId` are matched to component inputs BY NAME, and `permissionGuard` resolves
 * a scoped policy's subject from `:moduleId` as well (`permission.guard.ts:L164`).
 * Renaming a parameter breaks the binding silently: the input keeps its default, the
 * screen renders, and nothing in the build says why the record never loaded.
 *
 * ⚠ NO PARAMETER IS COERCED, MATCHED NUMERICALLY OR DEFAULTED ANYWHERE IN THIS
 * TABLE. Portal keys are `IDENTITY(-1, 1)` in the baseline schema and module, tab and
 * role keys are `IDENTITY(0, 1)`, so `-1` and `0` are DATA rather than absence — and
 * the legacy null contract at `Library/Components/Shared/Null.vb:L41-L45` returns
 * `-1` for a missing integer, so one value means both a real record and "no record".
 * A `:int` matcher would be harmless; a falsy test or a substituted default would not,
 * and each screen's own input decides presence by identity rather than by magnitude.
 *
 * WHERE THE GATES ARE ATTACHED, AND WHY THERE
 * -------------------------------------------
 * `authGuard` is attached to feature ROOTS only — the four protected group parents
 * and the three standalone leaves — never to a child inside a barrel, so the session
 * requirement is declared once per group and inherited. Two addresses deliberately
 * carry no session gate, and both omissions are invariants rather than choices:
 *
 *   * `login`, because `core/guards/auth.guard.ts:L49-L56` states that a gate on the
 *     screen it redirects TO cannot be satisfied by any caller. The guard hardens its
 *     own half of that at L247 so a mistake degrades to "the sign-in screen is
 *     reachable" rather than to a redirect cycle, but this table must not lean on it.
 *   * `**`, because the same note requires the not-found screen to stay reachable in
 *     order to be able to say that nothing is there.
 *
 * `permissionGuard` is attached where a policy is actually declarable, which is inside
 * the barrels for the grouped children and here for the three standalone leaves. Each
 * barrel documents its own policy choices against the controller attributes they
 * mirror; the short version is that the API registers eight policies
 * (`Api/Authorization/PolicyNames.cs`) while the client's registered set is closed at
 * five (`permission.guard.ts:L105-L111`), and a route whose endpoint requires one of
 * the other three declares no policy rather than substituting a different one.
 *
 * ⚠ ORDERING CONSTRAINT FOR EVERY FUTURE EDIT. The router matches in declaration order
 * and stops at the first match, and `**` matches EVERYTHING, including the empty path.
 * It must therefore remain LAST in this array for all time. A feature group appended
 * after it is unreachable, and unreachable in the most confusing possible way: the
 * build succeeds, the lazy chunk is emitted, the route object is present in the
 * configuration, and every navigation to it silently renders the fallback instead.
 *
 * The same ordering rule applies INSIDE each barrel, where a literal segment such as
 * `new` or `import` must precede the parameter route that would otherwise swallow it.
 * Each barrel states that constraint at its head.
 *
 * The configuration is also never allowed to be empty. An empty array is not an
 * application with no routes; it is an application whose FIRST navigation fails. The
 * router performs an initial navigation to the document URL during bootstrap, finds
 * nothing to match, and raises `NG04002: Cannot match any routes`, which surfaces as
 * an unhandled rejection in the console on every single page load.
 *
 * MIGRATION: routing moves from the SERVER to the BROWSER, and the addressing scheme
 * changes with it. The legacy application had no route table. Every request arrived at
 * `Website/Default.aspx` and the page resolved which content to render from
 * query-string state — a numeric `TabId` selecting the page and a `PortalId` selecting
 * the tenant — after which `LoadSkin` (`Website/Default.aspx.vb` L217-L243) assembled
 * the markup at run time. Addresses were therefore opaque and identifier-bearing rather
 * than descriptive, and which control answered a given address was tenant
 * configuration rather than application structure. Here they are path-based and
 * readable, the tenant is resolved by the API from the request rather than named in the
 * URL, and no server-side page assembly is involved: the proxy returns the same
 * document for every path and this table decides what is mounted.
 *
 * MIGRATION: navigation gating moves from IMPERATIVE tests inside each page's load
 * handler to declarative route data answered in one place. `Portals.ascx.vb:L339-L341`
 * and `ModuleSettings.ascx.vb:L191-L193` are the canonical legacy shapes, and each
 * navigated away by side effect, so the set of protected screens could only be
 * discovered by reading every screen. It is now readable from this table and the five
 * barrels it names.
 */
export const APP_ROUTES: Routes = [
  {
    /**
     * The application root.
     *
     * `pathMatch: 'full'` is mandatory and not stylistic: the default prefix matching
     * would make the empty path match EVERY address, so this redirect would fire on
     * every navigation and no route below it would ever be reached.
     *
     * A redirect rather than a landing screen of its own, because this workspace has no
     * dashboard component and inventing one would be a feature addition rather than a
     * routing decision. The destination is the constant above, which
     * `layout/header/header.component.ts` already documents as the target of its
     * identity affordance.
     *
     * An unauthenticated visitor is not shown the portals list by this: the redirect
     * resolves to `portals`, whose `authGuard` then redirects to the sign-in screen with
     * the original address preserved as `returnUrl`, so arriving at the root of the
     * application signs a caller in and returns them here.
     */
    path: '',
    redirectTo: ROOT_REDIRECT_PATH,
    pathMatch: 'full',
  },
  {
    /**
     * The sign-in group. Ungated, per the invariant recorded above and in the barrel.
     *
     * Declared before the protected groups purely for readability — it is the first
     * screen a caller sees — since no two paths in this table overlap and the order
     * between distinct literal segments carries no meaning.
     */
    path: 'login',
    loadChildren: () => import('./features/auth/auth.routes').then((m) => m.AUTH_ROUTES),
  },
  {
    /**
     * Tenant administration. Five children; see `features/portal/portal.routes.ts`.
     */
    path: ROOT_REDIRECT_PATH,
    canActivate: [authGuard],
    loadChildren: () => import('./features/portal/portal.routes').then((m) => m.PORTAL_ROUTES),
  },
  {
    /**
     * Module administration. Six children; see `features/module/module.routes.ts`.
     */
    path: 'modules',
    canActivate: [authGuard],
    loadChildren: () => import('./features/module/module.routes').then((m) => m.MODULE_ROUTES),
  },
  {
    /**
     * Account administration. Five children; see `features/user/user.routes.ts`, which
     * documents why the group's own empty child resolves to an explanation rather than to
     * an account listing.
     */
    path: 'users',
    canActivate: [authGuard],
    loadChildren: () => import('./features/user/user.routes').then((m) => m.USER_ROUTES),
  },
  {
    /**
     * Security role administration. Four children; see `features/role/role.routes.ts`.
     */
    path: 'roles',
    canActivate: [authGuard],
    loadChildren: () => import('./features/role/role.routes').then((m) => m.ROLE_ROUTES),
  },
  {
    /**
     * Role group creation.
     *
     * A top-level leaf rather than a child of `roles`, because a role group is a sibling
     * aggregate with its own controller (`RoleGroupsController`, class-gated on portal
     * administration at `:L173`) rather than a child of a role — and because the address
     * already exists in the shipped code: `role-list.component.ts` holds it as
     * `ADD_ROLE_GROUP_LINK = '/role-groups/new'`, so declaring it under `roles` would
     * break a link rather than tidy one.
     *
     * Both gates are attached here, unlike the grouped children, because a standalone
     * leaf has no parent to inherit the session requirement from.
     */
    path: 'role-groups/new',
    title: 'Add New Role Group',
    canActivate: [authGuard, permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./features/role/role-group-form/role-group-form.component').then(
        (m) => m.RoleGroupFormComponent,
      ),
  },
  {
    /**
     * The tenant's account-administration settings.
     *
     * Under `settings/` rather than under `users/` because it configures the TENANT
     * rather than an account, and because the address is already held by two screens
     * that link to it: `membership-settings` is reached from
     * `role-list.component.ts`'s `MEMBERSHIP_SETTINGS_LINK = '/settings/membership'`.
     *
     * `UsersController` gates the settings read and write on portal administration, so
     * that is what the route declares.
     */
    path: 'settings/membership',
    title: 'User Settings',
    canActivate: [authGuard, permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./features/user/membership-settings/membership-settings.component').then(
        (m) => m.MembershipSettingsComponent,
      ),
  },
  {
    /**
     * Profile property declarations for the tenant.
     *
     * A sibling of the account settings above for the same reason — it declares what
     * every account in the tenant may record, so it is tenant configuration rather than
     * a property of one account. `ProfileDefinitionsController` is class-gated on portal
     * administration (`:L165`), which the route mirrors.
     *
     * Reached from `membership-settings.component.ts`'s
     * `PROFILE_DEFINITIONS_PATH = '/settings/profile-definitions'`, so the path is a
     * contract with a screen that already ships.
     */
    path: 'settings/profile-definitions',
    title: 'Profile Properties',
    canActivate: [authGuard, permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./features/user/profile-definition-list/profile-definition-list.component').then(
        (m) => m.ProfileDefinitionListComponent,
      ),
  },
  {
    /**
     * The catch-all. See the ordering constraint above: this must stay last.
     */
    path: '**',

    /**
     * An unmatched address resolves to a not-found VIEW rather than redirecting to
     * one of the screens above, because a redirect rewrites the address bar and
     * makes a mistyped URL indistinguishable from a deliberate navigation.
     *
     * Lazily loaded, like every other view in the table, so the fallback costs
     * nothing on first paint. The shared empty-state component is reused here
     * rather than a dedicated not-found component being written, and the reuse is
     * a deliberate fit rather than a convenience: the component's entire purpose
     * is to explain, in one sentence, why a region has nothing to show, which is
     * precisely what this route needs to say.
     *
     * It is also the one component in the workspace authored to accept
     * router-supplied wording. Its `message` input widens its write type to
     * `string | readonly string[] | null | undefined` and documents the reason as
     * `null` and `undefined` being "values the router's binder genuinely passes",
     * so binding it from route data is a supported use of its published contract
     * rather than a coincidence that happens to work.
     *
     * `features/user/user.routes.ts` reuses it a second time, for the account listing
     * address, with wording of its own — deliberately distinguishable from this one,
     * because "this address resolves to no screen" and "this screen is not part of the
     * workspace" are different statements and an operator needs to know which applies.
     */
    loadComponent: () =>
      import('./shared/components/empty-state/empty-state.component').then(
        (m) => m.EmptyStateComponent,
      ),

    /**
     * Bound to the component's `message` input by `withComponentInputBinding()`,
     * which `app.config.ts` enables. The router matches data keys to input names,
     * so this key is load-bearing and must stay spelled exactly as the input is.
     *
     * Supplying the wording from the route rather than letting the component fall
     * back to its own default is what makes the sentence specific to this
     * situation: the component's default describes an empty result set, which is a
     * different thing from an address that resolves to no screen.
     */
    data: { message: NO_ROUTED_VIEW_MESSAGE },

    /**
     * Rendered in the browser tab and announced by screen readers on navigation.
     * Set here because the document title is otherwise fixed by `index.html` for
     * the whole application, so without it an unrecognised address would keep the
     * title of wherever the reader came from.
     */
    title: 'Not Found — DotNetNuke Administration',
  },
];
