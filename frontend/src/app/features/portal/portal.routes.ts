import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';
import { unsavedChangesGuard } from '../../core/guards/unsaved-changes.guard';

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
 * set it registers at `core/guards/permission.guard.ts:L142-L152`, which mirrors the
 * nine names the API registers in `Api/Authorization/PolicyNames.cs` (L86, L93, L105,
 * L112, L147, L171, L185, L196, L231) and activates in
 * `Api/Extensions/AuthenticationExtensions.cs`. A name outside that set is refused
 * outright by the gate (`permission.guard.ts:L589-L595`) rather than forwarded,
 * because the API registers no policy provider that could invent one on demand and would
 * therefore throw while authorising rather than answer with a tidy refusal.
 *
 * ⚠ THE CLIENT'S ROUTE VOCABULARY IS THE API'S, ALL NINE NAMES, AND NO APPROXIMATION IS
 * SUBSTITUTED FOR ANY OF THEM. A previous revision of this file asserted the opposite —
 * that a route here could declare only five of them — and declared the tenant-scoped
 * policy on tenant CREATION as the "closest declarable" stand-in for the host policy the
 * endpoint really requires. That claim was false in both halves. The gate registers all
 * nine names, resolves the scope each one needs, and answers the host question from the
 * caller's host flag alone; the only thing that had ever restricted this table was a
 * specification pinning a five-name list, which was itself wrong and has been corrected.
 * The registered set has since grown to nine with `PortalContentEditor`, which no route in
 * THIS table declares — module creation is where it is needed — but which counts toward the
 * closed set the gate resolves against and is named here so the number cannot drift again.
 *
 * The substitution mattered rather than being a documentation slip. Approximating the host
 * rule with the tenant rule errs toward ADMITTING, so it offered tenant creation to every
 * portal administrator in the installation — a screen whose one endpoint is guaranteed to
 * refuse them, reached through a form they can fill in completely before being told. That
 * is the opposite of what a navigation affordance is for.
 *
 * So each route below declares the policy ITS OWN primary endpoint declares, read from the
 * controller:
 *
 *   * `PortalsController.cs:L389` (create) requires `HostAdministrator`, and the `new`
 *     route declares exactly that. `:L241` (list) and `:L569` (delete) require it too,
 *     because the portal COLLECTION addresses no single tenant.
 *     `PolicyNames.cs:L120-L130` records why it is not interchangeable with portal
 *     administration: the tenant-scoped policy falls back to the tenant the caller
 *     arrived through, so asking it about a tenant-wide operation is a truthful but
 *     irrelevant question that would let an administrator of one tenant enumerate or
 *     create tenants.
 *   * Everything else is genuinely tenant-scoped and declares `PortalAdministrator`:
 *     `PortalsController.cs:L291`, `:L500`, `:L615` and `:L658`, plus all five actions of
 *     `PortalAliasesController.cs` (L236, L307, L373, L427, L481).
 *
 * The LISTING declares `HostAdministrator` for the same reason `new` does, and that was a
 * separate decision from the vocabulary question — an earlier revision left it ungated on
 * the reasoning that a listing mutates nothing, and the note on the route itself records
 * why that reasoning is withdrawn, what the objection to gating was, and how it is
 * answered.
 *
 * A name outside the registered nine would still be refused outright by the gate
 * (`permission.guard.ts` fails closed on an unregistered declaration) and the API would
 * throw while authorising, so no such name may be invented here.
 *
 * A GATE REMAINS A NAVIGATION AFFORDANCE AND NEVER AN ENFORCEMENT POINT. The server is the
 * authority and answers 403 regardless of what happened here, so no route below is made
 * safe by its gate; the gate exists to keep the client from offering screens it can already
 * tell are unusable, and the API's refusal is surfaced through the shared error banner
 * whenever one gets through — which here means the window before a fetched identity has
 * resolved, since the gate deliberately admits while it is unknown, and any record-scoped
 * question the client cannot pre-judge at all.
 *
 * MIGRATION: navigation gating moves from an IMPERATIVE test inside the page's own load
 * handler to declarative route data answered in one place. `Portals.ascx.vb:L339-L341`
 * is the legacy shape — `If Not UserInfo.IsSuperUser Then Response.Redirect(...)` —
 * which navigated away by side effect, so the protected set could only be discovered by
 * reading every screen. It is now readable from this table.
 *
 * MIGRATION: the authorisation GRANULARITY is preserved rather than narrowed. The legacy
 * console admitted only a host account to the portal list (`Portals.ascx.vb:L339-L341`)
 * and separated its actions by two access levels — `SecurityAccessLevel.Host` for adding
 * a portal (`Portals.ascx.vb:L435`) and `SecurityAccessLevel.Admin` for the two actions
 * dropped below (`:L436`, `:L437`). The host tier maps to `HostAdministrator` and the
 * tenant tier to `PortalAdministrator`, so the split survives; what does not survive is
 * the per-action access-level CONCEPT, because the target authorises per endpoint rather
 * than per grid action. Nothing here invents a `SuperUser` policy: the API registers none,
 * and a name outside the registered nine would be refused by the gate
 * (`permission.guard.ts:L589-L595`) and would throw while the endpoint was authorised.
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
     * GATED ON `HostAdministrator`, WHICH IS THE POLICY THE ENDPOINT ITSELF DECLARES, so
     * this listing follows the same rule as every other one in the workspace
     * (`module.routes.ts:L108-L111`, and `user.routes.ts` and `role.routes.ts` at their own
     * `''` routes) rather than standing as an exception to it.
     * `PortalsController.cs:L241` requires host authority to enumerate tenants and this
     * screen exists to render that enumeration, so a caller who cannot make the call has
     * nothing to look at. The route also carries the delete affordance, whose endpoint is
     * host-gated too (`:L569`), so an open screen offered tenant enumeration AND tenant
     * deletion to every signed-in caller and relied entirely on the server to say no.
     *
     * MIGRATION: this restores the legacy reachability exactly rather than narrowing it.
     * `Portals.ascx.vb:L339-L341` opened with
     * `If Not UserInfo.IsSuperUser Then Response.Redirect(NavigateURL("Access Denied"), True)`,
     * so the legacy portal list was HOST-ONLY, and the host flag the gate reads is the same
     * fact that predicate read.
     *
     * ⚠ AN UNGATED LISTING WAS CONSIDERED AND IS WITHDRAWN, AND THE OBJECTION TO GATING
     * IS ANSWERED RATHER THAN OVERRULED. That objection was real: this screen is the only
     * navigation entry the rail offers into the whole portal feature, so gating it removes
     * every rail path to `:portalId/settings` and `:portalId/aliases` — screens
     * `PortalsController.cs:L615` and every action of `PortalAliasesController` grant a
     * TENANT administrator by right — and denying access the server would have granted is
     * the opposite failure from the one an advisory gate exists to prevent. Three things
     * settle it. The rail entry for this address declares `HostAdministrator` in its own
     * right (`layout/sidebar/sidebar.component.ts`), so the entry is already withheld from
     * a non-host whatever this route says, and an ungated route would only mean an operator
     * who typed the address reached a screen whose one read is refused 403. Leaving it open
     * did not restore the children either: the listing a non-host sees is empty, so there
     * was never a row to navigate from. And the legacy console answered this exact question
     * by redirecting rather than by rendering a refusal. What gating must NOT do is strand a
     * caller who never asked for this screen, which is why `app.routes.ts` resolves the
     * application root per authority instead of sending everyone here — see
     * `rootLandingRedirect`, which exists because a static root redirect to this address
     * left every tenant administrator sitting on the sign-in screen after a successful
     * sign-in.
     *
     * THE GATE IS STILL ADVISORY AND THE SERVER IS STILL THE AUTHORITY. Nothing here is
     * made safe by this declaration; it exists so the client does not navigate an operator
     * into a screen it can already tell is unusable, and the API's refusal is surfaced
     * through the shared error banner in the cases the client cannot pre-judge.
     *
     * The title is the legacy control's own, so the browser tab reads as it did:
     * `ControlTitle_.Text` in `Website/admin/Portal/App_LocalResources/Portals.ascx.resx:L144-L145`
     * is "Portals".
     */
    path: '',
    title: 'Portals',
    canActivate: [permissionGuard],
    data: { permission: 'HostAdministrator' },
    loadComponent: () =>
      import('./portal-list/portal-list.component').then((m) => m.PortalListComponent),
  },
  {
    /**
     * Tenant creation.
     *
     * ⚠ MUST REMAIN ABOVE `:portalId`. See the ordering note at the head of this file.
     *
     * GATED ON HOST AUTHORITY, WHICH IS WHAT THE ENDPOINT ITSELF REQUIRES.
     * `PortalsController.cs:L389` declares `HostAdministrator` for `POST /portals`, and
     * this route declares the same name. The gate resolves it from the caller's host flag
     * alone, with no tenant reasoning of any kind, because the policy has no portal binding
     * by design — reading a portal to answer it would reintroduce the very question
     * `PolicyNames.cs:L120-L130` says the policy exists to avoid asking.
     *
     * MIGRATION: a previous revision declared `PortalAdministrator` here as the "nearest
     * declarable" policy, on the reasoning that erring toward admitting is harmless because
     * the server refuses anyway. It is not harmless. Every portal administrator in the
     * installation was admitted to a creation form whose only endpoint is certain to refuse
     * them, and learned that after filling it in. The narrower name is the correct one, it
     * is registered, and it turns nobody away that the server would have admitted: a host
     * account satisfies it directly.
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
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
    canDeactivate: [unsavedChangesGuard],
    title: 'Add New Portal',
    canActivate: [permissionGuard],
    data: { permission: 'HostAdministrator' },
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
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
    canDeactivate: [unsavedChangesGuard],
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
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
    canDeactivate: [unsavedChangesGuard],
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
     * Every action on `PortalAliasesController.cs` declares portal administration — L236,
     * L307, L373, L427 and L481 — so one policy covers the whole screen. The identifier
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
