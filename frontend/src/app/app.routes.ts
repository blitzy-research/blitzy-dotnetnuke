import { inject } from '@angular/core';
import type { RedirectFunction, Routes } from '@angular/router';

import { SIGN_IN_ROUTE } from './core/config/app-routes.config';
import { authGuard } from './core/guards/auth.guard';
import { permissionGuard } from './core/guards/permission.guard';
import { unsavedChangesGuard } from './core/guards/unsaved-changes.guard';
import { AuthStore } from './core/state/auth.store';

/** The wording the fallback route renders. */
export const NO_ROUTED_VIEW_MESSAGE =
  'No administration screen is available at this address.';

/**
 * The address the application root resolves to. Held as a named constant because it is a contract with a
 * component that already ships: `layout/header/header.component.ts:L14-L19` documents its identity
 * affordance as targeting the application root precisely so that the destination is "declared in exactly
 * one place", and names that place as this redirect.
 */
export const ROOT_REDIRECT_PATH = 'portals';

/**
 * Where a HOST account lands when it arrives at the application root. The tenant listing, which is the
 * screen a host operator opens the console for and the destination this redirect has always resolved to.
 */
export const HOST_LANDING_ROUTE = `/${ROOT_REDIRECT_PATH}`;

/** Where a TENANT ADMINISTRATOR who is not a host account lands at the application root. */
export const TENANT_LANDING_ROUTE = '/modules';

/**
 * @param userId The signed-in account, which is also the only account this address may name — the
 * server's allowance requires the route's account to BE the caller.
 * @returns The absolute address of that account's password screen.
 */
export function credentialRemediationRoute(userId: number): string {
  return `/users/${String(userId)}/password`;
}

/**
 * Where a caller with an outstanding MANDATORY PROFILE COMPLETION is sent.
 *
 * @param userId The signed-in account, which is also the only account this address may name.
 * @returns The absolute address of that account's profile screen.
 */
export function profileRemediationRoute(userId: number): string {
  return `/users/${String(userId)}/profile`;
}

/** @returns The address the root resolves to for the caller arriving at it. */
export const rootLandingRedirect: RedirectFunction = () => {
  const authStore = inject(AuthStore);

  if (authStore.isAuthenticated() === false) {
    return SIGN_IN_ROUTE;
  }

  const caller = authStore.currentUser();

  if (caller !== null) {
    if (authStore.mustChangePassword()) {
      return credentialRemediationRoute(caller.userId);
    }

    if (authStore.mustUpdateProfile()) {
      return profileRemediationRoute(caller.userId);
    }
  }

  if (authStore.isSuperUser()) {
    return HOST_LANDING_ROUTE;
  }

  if (authStore.administersCurrentPortal()) {
    return TENANT_LANDING_ROUTE;
  }

  return caller === null ? HOST_LANDING_ROUTE : profileRemediationRoute(caller.userId);
};

export const APP_ROUTES: Routes = [
  {
    path: '',
    redirectTo: rootLandingRedirect,
    pathMatch: 'full',
  },
  {
    /** The sign-in group. Ungated, per the invariant recorded above and in the barrel. */
    path: 'login',
    loadChildren: () => import('./features/auth/auth.routes').then((m) => m.AUTH_ROUTES),
  },
  {
    /** Tenant administration. Five children; see `features/portal/portal.routes.ts`. */
    path: ROOT_REDIRECT_PATH,
    canActivate: [authGuard],
    loadChildren: () => import('./features/portal/portal.routes').then((m) => m.PORTAL_ROUTES),
  },
  {
    /** Module administration. */
    path: 'modules',
    canActivate: [authGuard],
    loadChildren: () => import('./features/module/module.routes').then((m) => m.MODULE_ROUTES),
  },
  {
    /** Account administration. */
    path: 'users',
    canActivate: [authGuard],
    loadChildren: () => import('./features/user/user.routes').then((m) => m.USER_ROUTES),
  },
  {
    /** Security role administration. Four children; see `features/role/role.routes.ts`. */
    path: 'roles',
    canActivate: [authGuard],
    loadChildren: () => import('./features/role/role.routes').then((m) => m.ROLE_ROUTES),
  },
  {
    /** Role group creation. */
    path: 'role-groups/new',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
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
     * The tenant's account-administration settings. Under `settings/` rather than under `users/` because
     * it configures the TENANT rather than an account, and because the address is already held by two
     * screens that link to it: `membership-settings` is reached from `role-list.component.ts`'s
     * `MEMBERSHIP_SETTINGS_LINK = '/settings/membership'`.
     */
    path: 'settings/membership',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
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
     * Profile property declarations for the tenant. A sibling of the account settings above for the same
     * reason — it declares what every account in the tenant may record, so it is tenant configuration
     * rather than a property of one account.
     */
    path: 'settings/profile-definitions',
    canDeactivate: [unsavedChangesGuard],
    title: 'Manage Profile Properties',
    canActivate: [authGuard, permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./features/user/profile-definition-list/profile-definition-list.component').then(
        (m) => m.ProfileDefinitionListComponent,
      ),
  },
  {
    /** The catch-all. See the ordering constraint above: this must stay last. */
    path: '**',

    /**
     * Resolves to a VIEW rather than redirecting. `redirectTo` would rewrite the address bar, making a
     * mistyped URL indistinguishable from a deliberate navigation; resolving keeps the address intact and
     * states plainly that nothing answers it. Lazily loaded, like every other view in this table, so the
     * fallback costs nothing on first paint.
     */
    loadComponent: () =>
      import('./features/not-found/not-found.component').then((m) => m.NotFoundComponent),

    /**
     * Bound to the component's `message` input by `withComponentInputBinding()`, which `app.config.ts`
     * enables. The router matches data keys to input names, so this key is load-bearing and must stay
     * spelled exactly as the input is.
     */
    data: { message: NO_ROUTED_VIEW_MESSAGE },

    /**
     * Rendered in the browser tab and announced by screen readers on navigation. Set here because the
     * document title is otherwise fixed by `index.html` for the whole application, so without it an
     * unrecognised address would keep the title of wherever the reader came from.
     */
    title: 'Not Found — DotNetNuke Administration',
  },
];

export const routes: Routes = APP_ROUTES;
