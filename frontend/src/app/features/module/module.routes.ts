import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The module administration feature's route barrel.
 *
 * `app.routes.ts` mounts this array beneath the `modules` path with `loadChildren` and
 * attaches `authGuard` to that parent, so the session requirement is declared once and
 * inherited rather than restated on each child.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING, AND THIS GROUP HAS TWO LITERALS TO PROTECT, NOT
 * ONE. Both `new` AND `import` must stay above `:moduleId`, because `:moduleId` matches
 * any single segment and would otherwise swallow either literal and hand the edit screen
 * the string `'new'` or `'import'` as the record to load.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN
 * ---------------------------------------
 * A child declares the policy its own primary endpoint declares. Three of the six carry
 * `ModuleEdit`, one carries `PortalAdministrator` twice over, and one carries nothing at
 * all — and that last one is the interesting case, because it is the only route in the
 * whole table where the correct declaration is the absent one for TWO independent reasons
 * at once. See `new` below.
 *
 * ⚠ A SCOPED POLICY WITHOUT A SCOPE REFUSES EVERY CALLER. `permission.guard.ts:L419-L423`
 * refuses outright when a route names `ModuleView`, `ModuleEdit`, `TabView` or `TabEdit`
 * without supplying the identifier the policy applies to, resolving that identifier from
 * `moduleId` or a bare `id` across the whole route ancestry (`:L164`, `:L248`). The
 * consequence for this file is concrete and easy to get wrong: `ModuleEdit` may be
 * declared only on a route that actually carries `:moduleId`. Declaring it on `''`,
 * `new` or `import` would make those three screens unreachable to everyone, with the
 * build green and the route object present in the configuration.
 *
 * MIGRATION: the six routes below are a DECOMPOSITION of two legacy pages rather than a
 * one-to-one port. `Website/admin/Modules/ModuleSettings.ascx.vb` was a single control
 * that created, edited and configured a placement depending on the query string it was
 * reached with, and `Export.ascx.vb` and `Import.ascx.vb` were separate pages. Splitting
 * the first into a create route, an edit route and a settings route makes each address
 * describe one intention, and is why `modules/:moduleId` and `modules/:moduleId/settings`
 * legitimately render the SAME measured heading — `Module Settings` at
 * `module-form.component.ts:L369` and `module-settings.component.ts:L715` — since both
 * are views of the one legacy screen that carried that title.
 */
export const MODULE_ROUTES: Routes = [
  {
    /**
     * The placement listing at `/modules`. `ModulesController:L187` gates the list read
     * on portal administration, which needs no scope, so it is declarable here.
     */
    path: '',
    title: 'Modules',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./module-list/module-list.component').then((m) => m.ModuleListComponent),
  },
  {
    /**
     * Module placement.
     *
     * ⚠ UNGATED DELIBERATELY, MIRRORING THE ENDPOINT EXACTLY. `ModulesController`'s
     * `POST` action (`:L249-L267`) is the one mutating action in the API that carries no
     * policy, and its own comment gives the reason: the legacy gate at
     * `ModuleSettings.ascx.vb:L191` admitted the portal administrator OR an administrator
     * of the page being edited, so requiring portal administration would refuse a caller
     * the legacy application admitted — a narrowing the behaviour-preservation obligation
     * forbids. The target page arrives in the request BODY, so no route-reading policy can
     * reach it, and the service evaluates the page grant after binding instead.
     *
     * Attaching `ModuleEdit` here would be wrong twice over, which is why the omission is
     * spelled out rather than left to be inferred. It would narrow exactly as the server's
     * comment forbids, AND it would fail closed regardless: this route carries no
     * `:moduleId`, so the gate could resolve no scope and would refuse every caller,
     * making the create screen unreachable rather than merely over-restricted.
     *
     * The parent's `authGuard` still applies, so this is an authenticated route with the
     * grant question deferred to the API — the same posture the endpoint itself takes.
     */
    path: 'new',
    title: 'Add Module',
    loadComponent: () =>
      import('./module-form/module-form.component').then((m) => m.ModuleFormComponent),
  },
  {
    /**
     * Content import. `ModulesController:L617` gates the import on portal administration
     * rather than on the module-level edit grant its siblings use, and this route mirrors
     * that: an import creates content inside the tenant, so the tenant-wide policy is the
     * one the API actually applies.
     */
    path: 'import',
    title: 'Import Module',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./module-import/module-import.component').then((m) => m.ModuleImportComponent),
  },
  {
    /**
     * Placement editing. `ModulesController:L338` gates the update on `ModuleEdit`, and
     * this route supplies the `:moduleId` that policy resolves its scope from, so the
     * declaration is answerable.
     *
     * No numeric matcher and no coercion: module keys are `IDENTITY(0, 1)` in the
     * baseline schema, so `0` is a real record, and the legacy null contract at
     * `Library/Components/Shared/Null.vb:L41-L45` returns `-1` for a missing integer — so
     * neither value may be treated as absence. The component's own input takes the raw
     * string (`module-form.component.ts:L968`).
     */
    path: ':moduleId',
    title: 'Module Settings',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-form/module-form.component').then((m) => m.ModuleFormComponent),
  },
  {
    /**
     * Per-placement settings. `ModulesController:L420` and `:L456` gate the settings read
     * and write on `ModuleEdit`.
     *
     * The policy sits on this route while the identifier it applies to sits on the same
     * path, so no ancestry walk is needed — but the guard performs one anyway
     * (`permission.guard.ts:L296-L305`), which is what lets a deeper child inherit a
     * parent's parameter without the route having to repeat it.
     */
    path: ':moduleId/settings',
    title: 'Module Settings',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-settings/module-settings.component').then((m) => m.ModuleSettingsComponent),
  },
  {
    /**
     * Content export. `ModulesController:L541` gates the export on `ModuleEdit` — reading
     * a module's content out is treated as an edit-level capability rather than a view-level
     * one, and this route declares what the endpoint declares rather than the weaker
     * `ModuleView` the verb might suggest.
     */
    path: ':moduleId/export',
    title: 'Export Module',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-export/module-export.component').then((m) => m.ModuleExportComponent),
  },
];
