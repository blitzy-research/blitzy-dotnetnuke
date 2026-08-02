import type { Routes } from '@angular/router';

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
 * The application's top-level route table.
 *
 * Every entry loads its component through a dynamic `import`, so no screen is
 * part of the initial bundle; the shell, the shared components and the global
 * stylesheet are, and nothing else. `withPreloading(PreloadAllModules)` in
 * `app.config.ts` then fetches the lazy chunks in the background once the first
 * navigation has settled, so the small initial payload costs nothing on a
 * subsequent navigation.
 *
 * ROUTE SHAPE
 * -----------
 * Each screen declares the tenant, module or account it operates on as a route
 * PARAMETER, and `withComponentInputBinding()` delivers that parameter straight
 * into a declared component input. A routed screen therefore never injects
 * `ActivatedRoute`, which is what keeps these components presentational and
 * directly constructible in a specification.
 *
 * WHAT THIS TABLE DELIBERATELY DOES NOT DECLARE
 * --------------------------------------------
 * The migration plan enumerates twenty-five routes across five feature groups,
 * each reached through its own `*.routes.ts` barrel with `loadChildren`. Only the
 * four screens whose components exist in this workspace are declared below, and
 * the shortfall is stated rather than papered over:
 *
 *   * A route cannot be declared ahead of its target. A dynamic `import()` of a
 *     module that does not exist is a BUILD failure rather than a runtime one —
 *     the bundler resolves every dynamic import specifier statically in order to
 *     emit the lazy chunk — so declaring the remaining screens now would break
 *     the production build outright, and with it the build gate.
 *   * No `loadChildren` barrel is introduced. A barrel that re-exported a single
 *     route would add a module and an indirection without adding a capability,
 *     and the barrels become worthwhile at the point their feature has several
 *     screens to group. Until then `loadComponent` reaches each screen directly
 *     and the lazy boundary is identical.
 *   * No route guard is attached. `authGuard` and `permissionGuard` are named by
 *     the plan and neither module exists here yet. Attaching a placeholder guard
 *     would either admit everything — which is worse than no guard, because it
 *     reads as protection that is not there — or refuse everything and make the
 *     screens unreachable. Server-side authorisation is unaffected either way:
 *     every mutating endpoint the API exposes is policy-protected, and a route
 *     guard is a navigation affordance rather than an enforcement point.
 *   * No `/login` route, because the authentication feature is not part of this
 *     workspace yet.
 *
 * The configuration is also never allowed to be empty. An empty array is not an
 * application with no routes; it is an application whose FIRST navigation fails.
 * The router performs an initial navigation to the document URL during bootstrap,
 * finds nothing to match, and raises `NG04002: Cannot match any routes`, which
 * surfaces as an unhandled rejection in the console on every single page load.
 * The catch-all below answers that navigation whatever the address happens to be.
 *
 * ⚠ ORDERING CONSTRAINT FOR EVERY FUTURE EDIT. The router matches in declaration
 * order and stops at the first match, and `**` matches EVERYTHING, including the
 * empty path. It must therefore remain LAST in this array for all time. A feature
 * group appended after it is unreachable, and unreachable in the most confusing
 * possible way: the build succeeds, the lazy chunk is emitted, the route object is
 * present in the configuration, and every navigation to it silently renders the
 * fallback instead.
 *
 * MIGRATION: routing moves from the SERVER to the BROWSER, and the addressing
 * scheme changes with it. The legacy application had no route table. Every request
 * arrived at `Website/Default.aspx` and the page resolved which content to render
 * from query-string state — a numeric `TabId` selecting the page and a `PortalId`
 * selecting the tenant — after which `LoadSkin` (`Website/Default.aspx.vb`
 * L217-L243) assembled the markup at run time. Addresses were therefore opaque and
 * identifier-bearing rather than descriptive. Here they are path-based and
 * readable, the tenant is resolved by the API from the request rather than named in
 * the URL, and no server-side page assembly is involved: the proxy returns the same
 * document for every path and this table decides what is mounted.
 */
export const APP_ROUTES: Routes = [
  {
    path: 'portals/:portalId/settings',
    title: 'Portal Settings',
    loadComponent: () =>
      import('./features/portal/portal-settings/portal-settings.component').then(
        (m) => m.PortalSettingsComponent,
      ),
  },
  {
    path: 'modules/:moduleId/settings',
    title: 'Module Settings',
    loadComponent: () =>
      import('./features/module/module-settings/module-settings.component').then(
        (m) => m.ModuleSettingsComponent,
      ),
  },
  {
    path: 'users/:userId/profile',
    title: 'User Profile',
    loadComponent: () =>
      import('./features/user/user-profile/user-profile.component').then(
        (m) => m.UserProfileComponent,
      ),
  },
  {
    path: 'settings/profile-definitions',
    title: 'Profile Properties',
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
