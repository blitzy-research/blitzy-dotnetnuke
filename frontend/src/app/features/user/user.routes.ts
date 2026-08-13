import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';
import { unsavedChangesGuard } from '../../core/guards/unsaved-changes.guard';

/** The five child routes mounted beneath `/users`. */
export const USER_ROUTES: Routes = [
  {
    path: '',
    title: 'User Accounts',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./user-list/user-list.component').then((m) => m.UserListComponent),
  },
  {
    /**
     * `/users/new` — account creation, gated on the policy the create endpoint declares. MUST STAY ABOVE
     * `':userId'`; see the ordering note in the file header.
     */
    path: 'new',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Add New User',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () => import('./user-form/user-form.component').then((m) => m.UserFormComponent),
  },
  {
    /**
     * `/users/{userId}` — account credentials; the legacy `cmdUser` tab. Gated on tenant administration,
     * the policy the update and delete endpoints declare.
     */
    path: ':userId',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Edit User Accounts',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () => import('./user-form/user-form.component').then((m) => m.UserFormComponent),
  },
  {
    /**
     * `/users/{userId}/profile` — the account's profile properties; the legacy `cmdProfile` tab. Gated on
     * OWNERSHIP-OR-TENANT-ADMINISTRATION, which is exactly what `UsersController.cs:L888` and `:L927`
     * declare for the read and the write behind this screen.
     */
    path: ':userId/profile',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Manage Profile',
    canActivate: [permissionGuard],
    data: { permission: 'AccountOwnerOrPortalAdministrator' },
    loadComponent: () =>
      import('./user-profile/user-profile.component').then((m) => m.UserProfileComponent),
  },
  {
    path: ':userId/password',
    canDeactivate: [unsavedChangesGuard],
    title: 'Manage Password',
    canActivate: [permissionGuard],
    data: { permission: 'AccountOwnerOrPortalAdministrator' },
    loadComponent: () =>
      import('./user-password/user-password.component').then((m) => m.UserPasswordComponent),
  },
];
