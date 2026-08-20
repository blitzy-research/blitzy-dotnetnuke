import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';
import { unsavedChangesGuard } from '../../core/guards/unsaved-changes.guard';

export const ROLE_ROUTES: Routes = [
  {
    path: '',
    title: 'Security Roles',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-list/role-list.component').then((m) => m.RoleListComponent),
  },
  {
    /**
     * Role creation, at the address `role-list.component.ts:L141` already holds as `ADD_ROLE_LINK =
     * '/roles/new'`. ⚠ This literal MUST remain above `:roleId` — see the ordering note at the head of
     * this file. Served by the same component as the edit address.
     */
    path: 'new',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Add New Role',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-form/role-form.component').then((m) => m.RoleFormComponent),
  },
  {
    /** Role editing. No numeric matcher and no coercion of `:roleId`. */
    path: ':roleId',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Edit Security Roles',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-form/role-form.component').then((m) => m.RoleFormComponent),
  },
  {
    /**
     * Role membership. `RolesController` exposes the members collection at `roles/{roleId}/users`, and
     * this address mirrors that nesting segment for segment.
     */
    path: ':roleId/users',
    canDeactivate: [unsavedChangesGuard],
    title: 'User Roles',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-assignment/role-assignment.component').then((m) => m.RoleAssignmentComponent),
  },
];
