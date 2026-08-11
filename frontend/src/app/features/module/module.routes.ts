import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';
import { unsavedChangesGuard } from '../../core/guards/unsaved-changes.guard';

/**
 * The module administration feature's route barrel.
 *
 * `app.routes.ts:183-185` mounts this array beneath the `modules` path as a lazy child
 * group and attaches the session gate to that parent, so the sign-in requirement is
 * declared once and inherited rather than restated on each child. Nothing in this file
 * re-attaches it: doing so would run the same gate twice per navigation, and
 * `app.routes.spec.ts:316-330` asserts that no barrel does.
 *
 * Every path below is therefore RELATIVE to `modules`. A child spelled `'modules'` would
 * resolve to `/modules/modules`, which is the single easiest way to break the whole group.
 *
 * MIGRATION: DECLARATION ORDER IS LOAD-BEARING, AND THIS GROUP HAS TWO LITERALS TO PROTECT
 * RATHER THAN ONE. The router matches in declaration order and `:moduleId` matches any
 * single segment, so both `new` and `import` must stay above it. Were `:moduleId` declared
 * first, `/modules/new` and `/modules/import` would match it and the edit screen would be
 * handed the string `'new'` or `'import'` as the record to load — a runtime failure that
 * compiles cleanly and raises no type error, which is why the ordering is asserted
 * explicitly at `app.routes.spec.ts:305-314` rather than left to review. `/modules/import`
 * is the only address in this application whose literal segment sits at the same depth as
 * a parameterised sibling, so this barrel is the strictest instance of the rule. The sort
 * key is neither alphabetical nor by length: it is literal segments before parameterised
 * ones.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN
 * ---------------------------------------
 * A child declares the policy its own primary endpoint declares — the API is authoritative
 * and this table only mirrors it, so a caller is never shown a screen the server will then
 * refuse. Reading down `ModulesController.cs`: the listing at `:186-187` and the content
 * import at `:626-627` carry `PortalAdministrator`; the update at `:337-338`, the settings
 * pair at `:419-420` and `:455-456`, and the content export at `:540-541` carry
 * `ModuleEdit`; and the create action at `:267-273` carries NO policy attribute at all.
 * That last one is the interesting case and is explained on the route itself.
 *
 * ⚠ A SCOPED POLICY WITHOUT A SCOPE REFUSES EVERY CALLER. `permission.guard.ts:322-337`
 * maps `ModuleView` and `ModuleEdit` onto the `moduleId` route parameter — one exact name,
 * with no fallback to a bare `id`, matching the contract the API states at
 * `PermissionAuthorizationHandler.cs:122`. When a route names one of those policies but
 * carries no such parameter anywhere in its ancestry, the gate refuses outright at
 * `permission.guard.ts:685` rather than waving the caller through, for the reason recorded
 * at `:590-591`. The consequence for this file is concrete and easy to get wrong:
 * `ModuleEdit` may be declared ONLY on a route that actually carries `:moduleId`.
 * Declaring it on `''`, `new` or `import` would make those three screens unreachable to
 * everyone — including a host account — with the build green and the route object present
 * in the configuration. `app.routes.spec.ts:293-303` asserts this invariant directly.
 *
 * MIGRATION: THE IMPERATIVE PAGE-LOAD REDIRECT BECOMES A DECLARATIVE ROUTE POLICY.
 * `Website/admin/Modules/ModuleSettings.ascx.vb:191-193` opened every request with
 *
 * ```vb
 * If PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False And PortalSecurity.IsInRoles(PortalSettings.ActiveTab.AdministratorRoles.ToString) = False Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 * ```
 *
 * Two `= False` tests joined by `And` admit a caller who satisfies EITHER the portal
 * administrator role OR the active page's administrator roles — which is exactly the
 * module-and-page scoping `ModuleEdit` expresses, and exactly why the create action is
 * ungated server-side. The redirect-on-load is replaced by a route gate, but that gate is
 * ADVISORY ONLY: it exists to avoid rendering a screen whose data will be refused, and the
 * server remains the authority, answering with 403. Neither legacy export nor legacy import
 * carried an inline check at all — grepping `Export.ascx.vb` and `Import.ascx.vb` for a
 * role test returns nothing, as both relied on the permissions of the admin page hosting
 * them — so their policies here are taken from their endpoints rather than invented.
 *
 * MIGRATION: THE SIX ROUTES BELOW ARE A DECOMPOSITION OF THREE LEGACY PAGES, not a
 * one-to-one port. `ModuleSettings.ascx.vb` was a single control that created, edited and
 * configured a placement depending on the query string it was reached with, while
 * `Export.ascx.vb` and `Import.ascx.vb` were separate pages. Splitting the first into a
 * create route, an edit route and a settings route makes each address describe one
 * intention, and is why `modules/:moduleId` and `modules/:moduleId/settings` legitimately
 * render the SAME measured heading — `Module Settings` at `module-form.component.ts:369`
 * and `module-settings.component.ts:715` — since both are views of the one legacy screen
 * that carried that title.
 *
 * MIGRATION: ROUTES THAT DELIBERATELY DO NOT EXIST. `Library/Components/Modules/ModuleController.vb`
 * exposes placement operations that the migrated API does not surface, so no address is
 * invented for them here: `SynchronizeModule` (`:614`), `CopyModule` (`:700` and `:743`),
 * `DeleteAllModules` (`:795`), `MoveModule` (`:1078`), `UpdateModuleOrder` (`:1160`) and
 * `UpdateTabModuleOrder` (`:1197` and `:1432`). A move in particular needs two page
 * identifiers — the placement being edited and its destination — and this contract carries
 * one, so it is a reduction rather than an oversight. There is likewise no page feature and
 * no `/tabs` group: pages are consumed as a lookup through `core/services/tab.service.ts`
 * because the pages controller is closed at three endpoints. No install, packaging, bulk,
 * permission-mutation, recycle-bin or filesystem address exists either. Each omission is
 * recorded in the repository's migration notes rather than papered over with a route that
 * would 404 or 405 on use.
 */
export const MODULE_ROUTES: Routes = [
  {
    /**
     * The placement listing at `/modules`.
     *
     * `ModulesController.cs:186-187` gates the list read on `PortalAdministrator`, which
     * resolves no scope (`permission.guard.ts:334-336`) and so is answerable on a route
     * that carries no parameter. The tenant-wide policy is the correct one here precisely
     * because a listing addresses no single module — there is no module yet to scope to.
     *
     * MIGRATION: a wholly NEW screen with no legacy predecessor. The measured
     * `asp:DataGrid` count across `Website/admin/Modules/` is zero: the legacy application
     * offered no placement listing, reaching module administration through the page being
     * edited instead. No legacy grid is reconstructed here, and none is claimed.
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
     * Module placement — the create screen.
     *
     * ⚠ UNGATED DELIBERATELY, MIRRORING THE ENDPOINT EXACTLY. The create action at
     * `ModulesController.cs:267-273` is the one mutating action in the API carrying no
     * policy attribute, and its own commentary at `:319-327` gives the reason: the legacy
     * gate quoted in this file's header admitted the portal administrator OR an
     * administrator of the page being edited, so demanding portal administration would
     * refuse a caller the legacy application admitted — a narrowing the
     * behaviour-preservation obligation forbids. The target page arrives in the request
     * BODY, so no route-reading policy can reach it; the service evaluates the page grant
     * after binding, and the legacy field-level restriction on promoting a module across
     * every page (`ModuleSettings.ascx.vb:215-220`, where `chkAllTabs`, `chkDefault`,
     * `chkAllModules` and `cboTab` were disabled for non-administrators) is enforced there
     * too, because it is a rule about one field of the request rather than about reaching
     * this address.
     *
     * The omission is spelled out rather than left to be inferred because attaching
     * `ModuleEdit` here would be wrong twice over: it would narrow exactly as the server's
     * commentary forbids, AND it would fail closed regardless, since this route carries no
     * `:moduleId` for the gate to resolve a scope from and every caller would be refused —
     * making the create screen unreachable rather than merely over-restricted.
     *
     * The session gate inherited from the parent still applies, so this is an authenticated
     * address with the grant question deferred to the API — the same posture the endpoint
     * itself takes. `app.routes.spec.ts:264-271` asserts the corollary: a route declaring a
     * policy carries the gate, and a route declaring none carries no gate.
     */
    path: 'new',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
    canDeactivate: [unsavedChangesGuard],
    title: 'Add Module',
    loadComponent: () =>
      import('./module-form/module-form.component').then((m) => m.ModuleFormComponent),
  },
  {
    /**
     * Content import.
     *
     * `ModulesController.cs:626-627` gates the import on `PortalAdministrator` rather than
     * on the module-level grant its siblings use, and this route mirrors that: an import
     * creates content inside the tenant rather than editing one existing placement, so the
     * tenant-wide policy is the one the API actually applies — and, as with the listing, it
     * is the only kind of policy answerable on a route that names no module.
     *
     * The screen selects its target placement from a form rather than from the address
     * (`module-import.component.ts:81` holds `moduleId` as a form control), which is why
     * this path is a bare literal and takes no parameter.
     */
    path: 'import',
    canDeactivate: [unsavedChangesGuard],
    title: 'Import Module',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./module-import/module-import.component').then((m) => m.ModuleImportComponent),
  },
  {
    /**
     * Placement editing.
     *
     * `ModulesController.cs:337-338` gates the update on `ModuleEdit`, and this route
     * supplies the `:moduleId` that policy resolves its scope from, so the declaration is
     * answerable here in a way it would not be on any of the three routes above.
     *
     * ⚠ THE PARAMETER NAME IS EXACTLY `moduleId` AND A RENAME FAILS SILENTLY.
     * `app.config.ts:88` enables component input binding, which delivers a route parameter
     * to a component input OF THE SAME NAME; `module-form.component.ts:1058` declares that
     * input, and `module-export.component.ts:558-570` carries the same warning against
     * renaming it. The gate reads the identical name, so a spelling such as `:id` or
     * `:moduleID` would break the binding and the scope resolution at once, with no
     * compile error from either.
     *
     * MIGRATION: THE `-1` SENTINEL IS REPLACED BY ROUTE STRUCTURE, NOT BY A TEST.
     * `ModuleSettings.ascx.vb:68` declared `Private Shadows ModuleId As Integer = -1` and
     * `:222` branched on `If ModuleId <> -1 Then BindData() Else <create defaults>`, so one
     * address served both intentions and told them apart by a magic value. Here `new` and
     * `:moduleId` are distinct routes, and presence versus absence is expressed
     * STRUCTURALLY by whether the matched address carries the segment at all. That is why
     * this file performs no identifier arithmetic whatsoever — no numeric matcher, no
     * coercion, no `> 0` test and no coalescing. It could not do so safely in any case:
     * module keys are `IDENTITY(0, 1)` in the baseline schema, so `0` is a real record, and
     * the legacy null contract at `Library/Components/Shared/Null.vb:41-45` returns `-1`
     * for a missing integer — so neither value may be read as absence. The component's own
     * input takes the raw string (`module-form.component.ts:1058`), and
     * `module-list.component.ts:797` records the same hazard from the other side, noting a
     * module that "is the first module of an installation" would be given no links at all
     * by a truthiness test.
     */
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
     * Per-placement settings. `ModulesController.cs:419-420` and `:455-456` gate the
     * settings read and write on `ModuleEdit`.
     *
     * The policy sits on this route and the identifier it applies to sits on the same path,
     * so no ancestry walk is required — but the gate performs one anyway
     * (`permission.guard.ts:296-305`), which is what lets a deeper child inherit a parent's
     * parameter without having to repeat it.
     */
    path: ':moduleId/settings',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
    canDeactivate: [unsavedChangesGuard],
    title: 'Module Settings',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-settings/module-settings.component').then((m) => m.ModuleSettingsComponent),
  },
  {
    /**
     * Content export. `ModulesController.cs:540-541` gates the export on `ModuleEdit` —
     * reading a module's content out is treated as an edit-level capability rather than a
     * view-level one, and this route declares what the endpoint declares rather than the
     * weaker `ModuleView` the verb might otherwise suggest.
     */
    path: ':moduleId/export',
    title: 'Export Module',
    canActivate: [permissionGuard],
    data: { permission: 'ModuleEdit' },
    loadComponent: () =>
      import('./module-export/module-export.component').then((m) => m.ModuleExportComponent),
  },
];
