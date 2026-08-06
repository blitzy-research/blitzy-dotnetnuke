import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The portal administration feature's route barrel.
 *
 * `app.routes.ts` mounts this array beneath the `portals` path with `loadChildren` and
 * attaches `authGuard` to that parent, so every route below inherits the session
 * requirement without restating it. Attaching it again here would run the same gate
 * twice per navigation and give a reader two places to look for one decision.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING. `new` MUST stay above `:portalId`. The router
 * matches in order and stops at the first match, and `:portalId` matches any single
 * segment including the literal `new`, so reversing the two sends `/portals/new` to the
 * edit screen with the string `'new'` as the identifier it is meant to load. The failure
 * is quiet — the form renders, reports the tenant as missing, and nothing in the build
 * or the console says why.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN
 * ---------------------------------------
 * A child declares the policy its OWN primary endpoint declares, and declares nothing
 * when that policy has no expression in the client's vocabulary. The gate's registered
 * set is closed at five names (`core/guards/permission.guard.ts:L105-L111`) while the API
 * registers eight (`Api/Authorization/PolicyNames.cs`), and the three it does not share —
 * `HostAdministrator`, `AccountOwner`, `AccountOwnerOrPortalAdministrator` — cannot be
 * named here at all: the gate refuses an unregistered name outright at L397 rather than
 * passing it through.
 *
 * Two routes below are therefore left ungated, and the reason is worth stating because
 * the omission looks like a gap and is not one. `PortalsController` requires
 * `HostAdministrator` for the tenant LISTING (`:L241`) and for CREATING a tenant
 * (`:L389`) — a portal is a host-level object, so a portal administrator may configure
 * the tenant they administer but may not enumerate or create tenants. Substituting
 * `PortalAdministrator` on those two routes would not approximate that rule, it would
 * INVERT the affordance: the screen would present itself as available to precisely the
 * operators the API is about to refuse. Leaving them ungated presents them to every
 * signed-in caller instead, and the server answers with the refusal the screen already
 * renders through the shared error banner. A gate is a navigation affordance and never an
 * enforcement point, so neither choice weakens anything; the ungated one merely stops the
 * client from making a promise it cannot keep.
 *
 * MIGRATION: the legacy console gated these screens IMPERATIVELY, inside each page's own
 * load handler — `Website/admin/Portal/Portals.ascx.vb:L339-L341` is the canonical shape,
 * testing the caller and redirecting by side effect. Declaring the policy as route data
 * makes the guarded set readable from the route table instead of discoverable only by
 * reading every screen, which is the arrangement `permission.guard.ts:L380-L385` records
 * as the intent.
 */
export const PORTAL_ROUTES: Routes = [
  {
    /**
     * The tenant listing at `/portals`.
     *
     * Ungated for the reason set out above: `PortalsController:L241` requires host
     * authority, which this client cannot express.
     */
    path: '',
    title: 'Portals',
    loadComponent: () =>
      import('./portal-list/portal-list.component').then((m) => m.PortalListComponent),
  },
  {
    /**
     * Tenant creation. Ungated for the same reason as the listing —
     * `PortalsController:L389` requires host authority.
     *
     * Reaches the same component as the edit route below. The form decides which of the
     * two measured headings to render from whether an identifier arrived
     * (`portal-form.component.ts:L1310`), so `Add New Portal` here and `Edit Portals`
     * there fall out of the absent parameter rather than out of a route flag.
     */
    path: 'new',
    title: 'Add New Portal',
    loadComponent: () =>
      import('./portal-form/portal-form.component').then((m) => m.PortalFormComponent),
  },
  {
    /**
     * Tenant editing. `PortalsController:L500` gates the update on portal
     * administration, so that is what this route declares.
     *
     * The parameter name is part of the contract in two directions at once:
     * `withComponentInputBinding()` matches it to the component's declared `portalId`
     * input by NAME (`portal-form.component.ts:L1021`), and no coercion or numeric
     * matcher is applied to it — portal keys are `IDENTITY(-1, 1)` in the baseline
     * schema, so `-1` and `0` are real tenants rather than absent ones.
     */
    path: ':portalId',
    title: 'Edit Portals',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-form/portal-form.component').then((m) => m.PortalFormComponent),
  },
  {
    /**
     * The tenant's own settings screen. `PortalsController:L615` and `:L658` gate the
     * settings read and write on portal administration.
     */
    path: ':portalId/settings',
    title: 'Site Settings',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-settings/portal-settings.component').then((m) => m.PortalSettingsComponent),
  },
  {
    /**
     * The tenant's address list. Every action on `PortalAliasesController` — L233, L304,
     * L370, L421 and L475 — declares portal administration, so one policy covers the
     * whole screen.
     */
    path: ':portalId/aliases',
    title: 'Portal Aliases',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-alias-list/portal-alias-list.component').then(
        (m) => m.PortalAliasListComponent,
      ),
  },
];
