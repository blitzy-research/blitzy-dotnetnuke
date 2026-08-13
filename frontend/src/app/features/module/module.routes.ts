import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';
import { unsavedChangesGuard } from '../../core/guards/unsaved-changes.guard';

export const MODULE_ROUTES: Routes = [
  {
    /**
     * The placement listing at `/modules`. `ModulesController.cs:186-187` gates the list read on
     * `PortalAdministrator`, which resolves no scope (`permission.guard.ts:334-336`) and so is answerable
     * on a route that carries no parameter.
     */
    path: '',
    title: 'Modules',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./module-list/module-list.component').then((m) => m.ModuleListComponent),
  },
  {
    path: 'new',
    canActivate: [permissionGuard],
    data: { permission: 'PortalContentEditor' },
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Add Module',
    loadComponent: () =>
      import('./module-form/module-form.component').then((m) => m.ModuleFormComponent),
  },
  {
    /** Content import. */
    path: 'import',
    canDeactivate: [unsavedChangesGuard],
    title: 'Import Module',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./module-import/module-import.component').then((m) => m.ModuleImportComponent),
  },
  {
    /** Placement editing. */
    path: ':moduleId',
    canDeactivate: [unsavedChangesGuard],
    title: 'Module Settings',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-form/module-form.component').then((m) => m.ModuleFormComponent),
  },
  {
    /**
     * Per-placement settings. `ModulesController.cs:419-420` and `:455-456` gate the settings read and
     * write on `ModuleEdit`.
     */
    path: ':moduleId/settings',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this existed:
    // Cancel, any in-application link and the browser's Back button all discarded a dirty form in silence,
    // with instrumented `confirm`, `alert` and `beforeunload` recording nothing.
    canDeactivate: [unsavedChangesGuard],
    title: 'Module Settings',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-settings/module-settings.component').then((m) => m.ModuleSettingsComponent),
  },
  {
    /** Content export. */
    path: ':moduleId/export',
    title: 'Export Module',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-export/module-export.component').then((m) => m.ModuleExportComponent),
  },
];
