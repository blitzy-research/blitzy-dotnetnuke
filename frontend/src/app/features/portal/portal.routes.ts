import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The portal (multi-tenant site) administration feature's route barrel.
 *
 * A DECLARATION MODULE AND NOTHING ELSE. It exports one route array. It holds no
 * component, no service, no template, no state and no side effect, and it reads no
 * configuration — its entire job is to say which five screens exist, at which relative
 * addresses, in which order, behind which gate, each fetched on demand.
 *
 * `app.routes.ts:L175-L177` mounts this array beneath the `portals` path with
 * `loadChildren` and attaches `authGuard` to that parent, so every route below inherits
 * the session requirement without restating it. Two consequences follow and both are
 * easy to get wrong:
 *
 *   * EVERY PATH BELOW IS RELATIVE. Re-prefixing any of them with `portals` would
 *     produce `/portals/portals`, `/portals/portals/new` and so on, breaking all five
 *     addresses at once while the build stays silent.
 *   * `authGuard` IS DELIBERATELY ABSENT HERE. Gates inherit, so restating it would run
 *     the same check twice per navigation and leave a reader two places to look for one
 *     decision.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING. `new` MUST stay above `:portalId`. The router
 * matches in declaration order and stops at the first match, and `:portalId` matches any
 * single segment including the literal `new`, so reversing the two sends `/portals/new`
 * to the edit screen with the string `'new'` as the identifier it is meant to load. The
 * failure is quiet: the form renders, reports the tenant as missing, and neither the
 * build nor the console says why.
 *
 * ⚠ NO IDENTIFIER IS COERCED, MATCHED NUMERICALLY OR DEFAULTED ANYWHERE BELOW, and that
 * omission is a requirement rather than an oversight — see the note on `:portalId`.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN
 * ---------------------------------------
 * A child declares the policy ITS OWN primary endpoint declares, read from the
 * controller rather than assumed. The gate resolves a declared name against the closed
 * set it registers at `core/guards/permission.guard.ts:L131-L140`, which mirrors the
 * eight names the API registers in `Api/Authorization/PolicyNames.cs` (L54, L61, L73,
 * L80, L115, L139, L153, L164) and activates in
 * `Api/Extensions/AuthenticationExtensions.cs:L300-L354`. A name outside that set is
 * refused outright by the gate (`permission.guard.ts:L589-L595`) rather than forwarded,
 * because the API registers no policy provider that could invent one on demand and would
 * therefore throw while authorising rather than answer with a tidy refusal.
 *
 * ⚠ THE CLIENT'S ROUTE VOCABULARY IS NARROWER THAN THE API'S, AND THE GAP IS REAL
 * RATHER THAN AN OVERSIGHT. Of the eight policies the API registers, a route in this
 * workspace may declare only the five the client's route contract admits — the four
 * record-scoped names and `PortalAdministrator` — a restriction asserted directly in
 * `app.routes.spec.ts:L275`. Two of this feature's endpoints sit outside that five:
 *
 *   * `PortalsController.cs:L241` (list) and `:L389` (create) require
 *     `HostAdministrator`, because the portal COLLECTION addresses no single tenant.
 *     `PolicyNames.cs:L120-L130` records why that is not interchangeable with portal
 *     administration: the tenant-scoped policy falls back to the tenant the caller
 *     arrived through, so asking it about a tenant-wide operation is a truthful but
 *     irrelevant question that would let an administrator of one tenant enumerate or
 *     create tenants.
 *   * `PortalsController.cs:L569` requires it for deletion too, which is why the list
 *     screen's delete affordance can be refused by the server on a screen this table
 *     admitted.
 *
 * Everything else is genuinely tenant-scoped and declares `PortalAdministrator`:
 * `PortalsController.cs:L291`, `:L500`, `:L615` and `:L658`, plus all five actions of
 * `PortalAliasesController.cs` (L233, L304, L370, L421, L475).
 *
 * Where an endpoint's real policy is not declarable, this table declares the closest
 * policy that IS — never a name outside the contract, which the gate would refuse
 * outright (`permission.guard.ts:L589-L595`) and the API would throw on. That is safe in
 * one specific direction worth stating: a host account satisfies portal administration
 * as well (`permission.guard.ts:L539-L540` resolves it through the caller's host flag),
 * so approximating a host rule with the tenant rule NEVER locks out the operator the
 * server intends to admit — it only admits some it will then refuse.
 *
 * That residue is acceptable because A GATE IS A NAVIGATION AFFORDANCE AND NEVER AN
 * ENFORCEMENT POINT. The server is the authority and answers 403 regardless of what
 * happened here, so no route below is made safe by its gate; the gate exists to keep the
 * client from offering screens it can already tell are unusable, and the API's refusal
 * is surfaced through the shared error banner when the approximation lets one through.
 *
 * MIGRATION: navigation gating moves from an IMPERATIVE test inside the page's own load
 * handler to declarative route data answered in one place. `Portals.ascx.vb:L339-L341`
 * is the legacy shape — `If Not UserInfo.IsSuperUser Then Response.Redirect(...)` —
 * which navigated away by side effect, so the protected set could only be discovered by
 * reading every screen. It is now readable from this table.
 *
 * MIGRATION: the authorisation GRANULARITY narrows deliberately. The legacy console
 * admitted only a host account to the portal list (`Portals.ascx.vb:L339-L341`) and
 * separated its actions by two access levels — `SecurityAccessLevel.Host` for adding a
 * portal (`Portals.ascx.vb:L435`) and `SecurityAccessLevel.Admin` for the two actions
 * dropped below (`:L436`, `:L437`). The target expresses the host/tenant split through
 * the two policies named above and has no `SuperUser` or per-action access-level
 * concept; nothing here reconstructs one, because inventing a policy the API does not
 * register would be refused by the gate and would throw at the endpoint.
 *
 * MIGRATION: the legacy list's Edit affordance did NOT open an edit page of its own — it
 * opened SITE SETTINGS carrying the tenant key. `Portals.ascx.vb:L308-L310` builds the
 * grid's Edit column address as
 * `GetTabByName("Site Settings", ...)` then `NavigateURL(objTab.TabID, "", "pid=KEYFIELD")`,
 * substituting the row key for `KEYFIELD`. The `:portalId/settings` route below is that
 * address, which is why the list's primary affordance targets it rather than
 * `:portalId`.
 *
 * MIGRATION: the site wizard receives NO route of its own. `Website/admin/Portal/SiteWizard.ascx.vb`
 * walked a tenant through its initial configuration as a separate multi-step control;
 * that workflow collapses into the tabbed `:portalId/settings` screen below, so every
 * field the wizard collected remains reachable while the step sequencing does not
 * survive. Recorded as a deliberate reduction rather than an omission.
 *
 * MIGRATION: two legacy list actions are DROPPED because no endpoint answers them, and
 * both are functional reductions rather than oversights.
 *   * `ExportTemplate.Action` — "Export Portal Template" — offered at
 *     `Portals.ascx.vb:L436` through `EditUrl("Template")` and implemented by
 *     `Website/admin/Portal/Template.ascx.vb`. `PortalsController` exposes no template
 *     export, so no route is declared for it.
 *   * `DeleteExpired.Action` — "Delete Expired Portals" — offered at
 *     `Portals.ascx.vb:L437` and implemented by the bulk sub at `Portals.ascx.vb:L189`,
 *     which called `PortalController.DeleteExpiredPortals(...)` at `:L191` over the set
 *     returned by `GetExpiredPortals()` at `:L139`. The API exposes per-tenant deletion
 *     only and no bulk operation, so neither the action nor the `Expired` filter bucket
 *     that fed it has a route here.
 */
export const PORTAL_ROUTES: Routes = [
  {
    /**
     * The tenant listing at `/portals`, and the feature's landing screen.
     *
     * NO FULL-PATH MATCH FLAG, deliberately. A terminal route — one carrying no children
     * of its own — matches only when it consumes the entire remaining address, so an
     * empty path cannot swallow `/portals/new`. Setting the flag would be inert here and
     * would suggest a hazard that does not exist.
     *
     * UNGATED, and the omission is reasoned rather than skipped. `PortalsController.cs:L241`
     * requires host authority to enumerate tenants, which is a policy this route COULD
     * declare — but a listing mutates nothing, the inherited session gate already excludes
     * anonymous callers, and gating it would hide the feature's only entry point behind a
     * check the server repeats anyway. A caller without host authority reaches the screen
     * and the API's refusal is surfaced through the shared error banner, which tells them
     * what a silent redirect would not.
     *
     * The title is the legacy control's own, so the browser tab reads as it did:
     * `ControlTitle_.Text` in `Website/admin/Portal/App_LocalResources/Portals.ascx.resx:L144-L145`
     * is "Portals".
     */
    path: '',
    title: 'Portals',
    loadComponent: () =>
      import('./portal-list/portal-list.component').then((m) => m.PortalListComponent),
  },
  {
    /**
     * Tenant creation.
     *
     * ⚠ MUST REMAIN ABOVE `:portalId`. See the ordering note at the head of this file.
     *
     * GATED, AND GATED WITH AN APPROXIMATION THAT IS RECORDED RATHER THAN HIDDEN.
     * `PortalsController.cs:L389` requires host authority for `POST /portals`, which is
     * not a name a route here may declare (see the vocabulary note above), so this route
     * declares the nearest policy it can. The approximation errs only toward admitting: a
     * host account satisfies portal administration too, so the operator the server means
     * to admit is never turned away here, while a portal administrator who is admitted
     * meets the server's narrower rule on submission and sees it reported. Leaving the
     * route ungated instead would be strictly worse — it would offer tenant creation to
     * every signed-in caller.
     *
     * Reaches the same component as the edit route below. The form decides which heading
     * to render from whether an identifier arrived, so the distinction between creating
     * and editing falls out of the absent parameter rather than out of a route flag.
     *
     * MIGRATION: the legacy list reached this screen through a separate control —
     * `Portals.ascx.vb:L435` adds the action as `EditUrl("Signup")`, so the address was
     * the Signup control rather than a child of the list. The title preserves that
     * control's wording: `AddPortal.Text` in
     * `Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L285-L286` is
     * "Add New Portal".
     */
    path: 'new',
    title: 'Add New Portal',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./portal-form/portal-form.component').then((m) => m.PortalFormComponent),
  },
  {
    /**
     * Tenant editing. `PortalsController.cs:L291` gates the read and `:L500` the update
     * on portal administration, so that is what this route declares.
     *
     * ⚠ THE PARAMETER NAME IS A RUNTIME CONTRACT IN TWO DIRECTIONS.
     * `withComponentInputBinding()` (`app.config.ts:L88`) delivers the segment into the
     * component's declared input BY NAME, and `portal-form.component.ts:L1096` declares
     * it as exactly `portalId`. Renaming this segment to `:id` or `:portalID` severs the
     * binding with no TypeScript error and no template error — the input simply keeps its
     * default. The API spells it the same way, as `/portals/{portalId}`.
     *
     * MIGRATION: no numeric matcher, no coercion and no default-as-absence is applied to
     * this segment, and that is a correctness requirement rather than a stylistic
     * preference. `Portals.PortalID` is declared `IDENTITY (-1, 1)` at
     * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77`, so
     * the first real tenant is 0 and -1 is a real tenant too — while `-1` is ALSO the
     * legacy absent-marker returned by `Library/Components/Shared/Null.vb:L41`. One value
     * therefore means both "this record" and "no record", so a falsy test, a magnitude
     * comparison or a substituted default would each discard a real tenant. Presence is
     * decided by identity inside the component, never here.
     *
     * The title is the legacy edit heading: `ControlTitle_edit.Text` in
     * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx:L480-L481` is
     * "Edit Portals".
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
     * The tenant's own settings screen, and the destination of the list's Edit
     * affordance for the reason recorded at the head of this file.
     *
     * `PortalsController.cs:L615` and `:L658` gate the settings read and write on portal
     * administration. The identifier is bound by name into
     * `portal-settings.component.ts:L1204-L1205`, which declares the same `portalId`.
     *
     * MIGRATION: the legacy screen stored these values as MODULE settings rather than in
     * a table of their own — `Library/Components/Portal/PortalSettings.vb` routes
     * `GetSiteSettings` through `GetModuleSettings(GetModuleByDefinition(portalId, "Site Settings"))`,
     * using the Site Settings module as a storage area for tenant-wide values. No
     * `PortalSettings` table exists to expose, so this screen is the tenant's own columns
     * and nothing here declares a key/value settings address.
     *
     * The title is the legacy control's own: `ControlTitle_.Text` in
     * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx:L477-L478` is
     * "Site Settings".
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
     * The tenant's address list, with creation and editing handled in place.
     *
     * Every action on `PortalAliasesController.cs` declares portal administration — L233,
     * L304, L370, L421 and L475 — so one policy covers the whole screen. The identifier
     * is bound by name into `portal-alias-list.component.ts:L1427-L1428`.
     *
     * MIGRATION: the legacy pair of controls collapses into one screen. `PortalAlias.ascx.vb:L81`
     * declared the list's actions and `:L90` added creation as
     * `EditUrl("pid", intPortalID.ToString, "Edit")`, navigating to the separate
     * `EditPortalAlias` control; both the list and that editor are presented here, so no
     * `:portalId/aliases/:portalAliasId` address is declared.
     *
     * The title is the legacy control's own: `ControlTitle_.Text` in
     * `Website/admin/Portal/App_LocalResources/PortalAlias.ascx.resx:L48-L49` is
     * "Portal Aliases".
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
