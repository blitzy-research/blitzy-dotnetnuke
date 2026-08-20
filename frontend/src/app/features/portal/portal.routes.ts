import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';
import { unsavedChangesGuard } from '../../core/guards/unsaved-changes.guard';

export const PORTAL_ROUTES: Routes = [
  {
    /**
     * The tenant listing at `/portals`, and the feature's landing screen. NO FULL-PATH MATCH FLAG,
     * deliberately.
     */
    path: '',
    title: 'Portals',
    canActivate: [permissionGuard],
    data: { permission: 'HostAdministrator' },
    loadComponent: () =>
      import('./portal-list/portal-list.component').then((m) => m.PortalListComponent),
  },
  {
    path: 'new',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Add New Portal',
    canActivate: [permissionGuard],
    data: { permission: 'HostAdministrator' },
    loadComponent: () =>
      import('./portal-form/portal-form.component').then((m) => m.PortalFormComponent),
  },
  {
    /**
     * Tenant editing. `PortalsController.cs:L291` gates the read and `:L500` the update on portal
     * administration, so that is what this route declares. ⚠ THE PARAMETER NAME IS A RUNTIME CONTRACT IN
     * TWO DIRECTIONS. `withComponentInputBinding()` delivers the segment into the component's declared
     * input BY NAME, and `portal-form.component.ts:L1096` declares it as exactly `portalId`.
     */
    path: ':portalId',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Edit Portals',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-form/portal-form.component').then((m) => m.PortalFormComponent),
  },
  {
    /**
     * The tenant's own settings screen, and the destination of the list's Edit affordance for the reason
     * recorded at the head of this file. `PortalsController.cs:L615` and `:L658` gate the settings read
     * and write on portal administration.
     */
    path: ':portalId/settings',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Site Settings',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-settings/portal-settings.component').then((m) => m.PortalSettingsComponent),
  },
  {
    /**
     * The tenant's address list, with creation and editing handled in place. Every action on
     * `PortalAliasesController.cs` declares portal administration — L236, L307, L373, L427 and L481 — so
     * one policy covers the whole screen.
     */
    path: ':portalId/aliases',
    canDeactivate: [unsavedChangesGuard],
    title: 'Portal Aliases',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-alias-list/portal-alias-list.component').then(
        (m) => m.PortalAliasListComponent,
      ),
  },
];
