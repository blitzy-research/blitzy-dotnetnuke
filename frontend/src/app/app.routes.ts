import { inject } from '@angular/core';
import type { RedirectFunction, Routes } from '@angular/router';

import { SIGN_IN_ROUTE } from './core/config/app-routes.config';
import { authGuard } from './core/guards/auth.guard';
import { permissionGuard } from './core/guards/permission.guard';
import { unsavedChangesGuard } from './core/guards/unsaved-changes.guard';
import { AuthStore } from './core/state/auth.store';

/**
 * The wording the fallback route renders.
 *
 * Held as a named constant rather than inlined in the route so that a
 * specification can assert the rendered text against the same string the route
 * supplies, instead of restating it and letting the two drift apart.
 *
 * It is phrased as a statement about the ADDRESS rather than about the
 * application's completeness — "no screen at this address" rather than "not built
 * yet" — because this route is permanent: as further feature routes are declared
 * above it, this is still the sentence an unrecognised URL produces, and a
 * sentence about unfinished work would then be wrong.
 */
export const NO_ROUTED_VIEW_MESSAGE =
  'No administration screen is available at this address.';

/**
 * The address the application root resolves to.
 *
 * Held as a named constant because it is a contract with a component that already
 * ships: `layout/header/header.component.ts:L14-L19` documents its identity
 * affordance as targeting the application root precisely so that the destination
 * is "declared in exactly one place", and names that place as this redirect. The
 * band deliberately does not name the portals list itself, so this constant is the
 * one declaration both sides depend on.
 */
export const ROOT_REDIRECT_PATH = 'portals';

/**
 * Where a HOST account lands when it arrives at the application root.
 *
 * The tenant listing, which is the screen a host operator opens the console for and the
 * destination this redirect has always resolved to. Expressed in terms of
 * {@link ROOT_REDIRECT_PATH} rather than restating the segment, so the landing address and
 * the portal group's own segment cannot drift apart.
 */
export const HOST_LANDING_ROUTE = `/${ROOT_REDIRECT_PATH}`;

/**
 * Where a TENANT ADMINISTRATOR who is not a host account lands at the application root.
 *
 * The module listing, chosen because it is the FIRST address the navigation rail offers
 * such a caller — `layout/sidebar/sidebar.component.ts` declares the Module group first
 * and gates its listing on `PortalAdministrator`, exactly as `module.routes.ts` does — so
 * the root resolves to the same screen the operator would reach by taking the first
 * affordance in front of them. It is not an arbitrary second choice: a host account
 * satisfies `PortalAdministrator` too, so this address is reachable by BOTH administrative
 * authorities and the ordering above only decides which of the two screens each one opens
 * on.
 */
export const TENANT_LANDING_ROUTE = '/modules';

/**
 * Where a caller with an outstanding MANDATORY CREDENTIAL CHANGE is sent.
 *
 * The account's own password screen, which is the only screen such a caller can use: the API
 * exempts `POST api/v1/users/{id}/password` from the remediation refusal and refuses very
 * nearly everything else.
 *
 * @param userId The signed-in account, which is also the only account this address may name —
 * the server's allowance requires the route's account to BE the caller.
 * @returns The absolute address of that account's password screen.
 */
export function credentialRemediationRoute(userId: number): string {
  return `/users/${String(userId)}/password`;
}

/**
 * Where a caller with an outstanding MANDATORY PROFILE COMPLETION is sent.
 *
 * The account's own profile screen, exempted on the same terms and for the same reason as the
 * password screen above.
 *
 * @param userId The signed-in account, which is also the only account this address may name.
 * @returns The absolute address of that account's profile screen.
 */
export function profileRemediationRoute(userId: number): string {
  return `/users/${String(userId)}/profile`;
}

/**
 * Resolves the application root to an address the arriving caller can actually use.
 *
 * ⚠ THIS IS A FUNCTION AND NOT A STRING, AND THE REASON IS A MEASURED DEAD END RATHER
 * THAN A PREFERENCE. A single static destination cannot be correct for every caller,
 * because the console's authorities are DISJOINT at the top: `/portals` enumerates tenants
 * and `PortalsController.cs:L241` requires host authority to do it, so the portal listing
 * declares `HostAdministrator` (`features/portal/portal.routes.ts`) and its rail entry
 * declares the same. With a static `redirectTo: 'portals'`, a tenant administrator signing
 * in was sent to the one screen the client can prove they may not open: the gate refused
 * the navigation, the refusal cancelled it, and the caller was left standing on
 * `/login?returnUrl=%2Fportals` — authenticated, holding a valid token, with the sign-in
 * form still mounted and a warning telling them they have no access to content they never
 * asked for. Reproduced in a browser against the running stack for the seeded tenant
 * administrator, and reproduced identically twice.
 *
 * The two facts that produce it are individually correct and must both stand. The listing
 * IS host-only — `Website/admin/Portal/Portals.ascx.vb:L339-L341` opened with
 * `If Not UserInfo.IsSuperUser Then Response.Redirect(NavigateURL("Access Denied"), True)`,
 * so refusing a non-host is the legacy behaviour rather than a narrowing of it — and the
 * root MUST resolve to something, since a router with no match for `/` raises `NG04002` on
 * the first navigation of every page load. What was wrong was the assumption joining them:
 * that one address serves every caller.
 *
 * WHAT EACH CALLER GETS, and why each answer is the honest one:
 *
 *   * No session: the sign-in screen, WITHOUT a `returnUrl`. Deliberately without: a
 *     caller who typed the root asked for "the application", not for the portals list, and
 *     capturing `/portals` here is precisely what used to bake an unreachable destination
 *     into the sign-in that followed. The address they did not choose is not preserved,
 *     because preserving it is what stranded them.
 *   * A caller owing MANDATORY REMEDIATION, whatever their authority: the one screen that
 *     can clear it — {@link credentialRemediationRoute} for an outstanding credential
 *     change, {@link profileRemediationRoute} for an outstanding profile completion. This
 *     test comes FIRST, ahead of every authority test below, and the body explains why in
 *     detail: while an advisory is outstanding the API refuses all three authority-based
 *     landings, so resolving authority first produced the same class of dead end this
 *     function was written to remove, for a different reason.
 *   * A host account: {@link HOST_LANDING_ROUTE}, unchanged from the behaviour this
 *     redirect has always had.
 *   * A tenant administrator: {@link TENANT_LANDING_ROUTE}, the first rail entry their
 *     authority admits.
 *   * Any other signed-in account: their own profile, the same address
 *     `layout/shell/shell.component.ts` computes for the header's profile affordance. The
 *     route declares `AccountOwnerOrPortalAdministrator`, whose first arm the caller
 *     satisfies by construction because the identifier comes from their own identity, so
 *     this can never resolve to a screen they will be refused. It used to be the account's
 *     own SERVICES screen; that address is withdrawn with the twenty-sixth route it
 *     belonged to, and the profile is the remaining screen a non-administrative account is
 *     entitled to operate on its own behalf.
 *   * A held session whose identity has not resolved yet: {@link HOST_LANDING_ROUTE},
 *     which preserves the previous behaviour for the one window in which nothing better is
 *     knowable. The policy gate admits while the identity is unresolved — refusing there
 *     would lock out the operators the screens exist for — so this window cannot strand a
 *     caller, and the screen's own read settles what the identity could not. This window
 *     cannot coincide with mandatory remediation, for the reason the body records: an
 *     advisory and the account it applies to are read from the same session object.
 *
 * ORDER IS LOAD-BEARING TWICE OVER. Remediation is resolved before authority, because an
 * advisory makes every authority-based destination unreachable rather than merely
 * suboptimal. Then among the authority tests the host test comes first, because
 * `administersCurrentPortal()` is satisfied BY a host account
 * (`core/state/auth.store.ts:L628-L629` reads it as the host flag or tenant administration),
 * so testing tenant administration first would send every host operator to the module
 * listing instead of the tenant listing.
 *
 * The router runs this inside an injection context, which is what makes {@link inject}
 * legal in the body — the same contract the two functional gates in this table rely on.
 * It reads the store's published verdicts and recomputes nothing, so the root cannot
 * resolve on a different view of the identity from the one the gates then apply.
 *
 * @returns The address the root resolves to for the caller arriving at it.
 */
export const rootLandingRedirect: RedirectFunction = () => {
  const authStore = inject(AuthStore);

  if (authStore.isAuthenticated() === false) {
    return SIGN_IN_ROUTE;
  }

  const caller = authStore.currentUser();

  // MANDATORY REMEDIATION OUTRANKS AUTHORITY, and it has to: while an advisory is outstanding
  // the API refuses every authority-based landing this function can name. Measured against the
  // running API for a session carrying an outstanding credential change, '/portals', '/modules'
  // and the account's own services screen were each refused 403 auth.remediation_required, so
  // resolving authority first sent the caller to a screen that could not load and left them
  // holding a refusal instead of the one screen that would have cleared it.
  //
  // ⚠ WHICH REMEDIATION SCREEN IS NOT A PREFERENCE, IT IS THE SERVER'S RULE.
  // RemediationAuthorizationHandler admits the password endpoints only while the CREDENTIAL
  // advisory is outstanding and the profile endpoints only while the PROFILE advisory is, so
  // naming the wrong one produces a second dead end rather than a slower route to the same
  // place. Measured: with only the credential outstanding, GET api/v1/users/{id}/profile is
  // refused 403 auth.not_permitted.
  //
  // The order below therefore reads: whichever single advisory is outstanding decides the
  // destination, and when BOTH are outstanding the credential goes first. Credential-first
  // reproduces the legacy precedence — UserValidStatus.vb ordered PASSWORDEXPIRED ahead of
  // UPDATEPROFILE, and only one of its values could be reported at a time — and it is also the
  // only order that terminates: the password screen renews the session on success, which clears
  // the credential advisory, leaves the profile advisory standing and returns here, which then
  // resolves to the profile screen. Profile-first would clear the profile advisory while the
  // credential advisory still refused the profile endpoints that were meant to clear it.
  //
  // No fallback is needed for an unresolved identity, and none is written: both advisories are
  // read from the held session and AuthSession.user is not optional, so an advisory that can be
  // reported necessarily carries the account it applies to. A null caller here means no session
  // is held, in which case both advisories read false and neither branch is taken.
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

  // The ordinary member's landing, and the ONE address in the closed route table a caller with no
  // administrative authority is entitled to. `profileRemediationRoute` composes the same address
  // for an outstanding profile advisory, and it is reused here rather than spelled a second time so
  // the two cannot drift: an advisory-driven arrival and an ordinary one land on the same screen.
  return caller === null ? HOST_LANDING_ROUTE : profileRemediationRoute(caller.userId);
};

/**
 * The application's top-level route table.
 *
 * Every screen is reached through a dynamic `import`, so no feature is part of the
 * initial bundle; the shell, the shared components and the global stylesheet are,
 * and nothing else. `withPreloading(PreloadAllModules)` in `app.config.ts` then
 * fetches the lazy chunks in the background once the first navigation has settled,
 * so the small initial payload costs nothing on a subsequent navigation.
 *
 * SHAPE OF THE TABLE
 * ------------------
 * Five feature groups are reached with `loadChildren` and a route barrel — the
 * arrangement the migration plan specifies — and three screens are reached
 * directly with `loadComponent` because their addresses are not children of any
 * group. The groups are:
 *
 * ```text
 *   login        -> features/auth/auth.routes.ts        AUTH_ROUTES     (ungated)
 *   portals      -> features/portal/portal.routes.ts    PORTAL_ROUTES   (authGuard)
 *   modules      -> features/module/module.routes.ts    MODULE_ROUTES   (authGuard)
 *   users        -> features/user/user.routes.ts        USER_ROUTES     (authGuard)
 *   roles        -> features/role/role.routes.ts        ROLE_ROUTES     (authGuard)
 * ```
 *
 * Each screen declares the tenant, module, account or role it operates on as a
 * route PARAMETER, and `withComponentInputBinding()` delivers that parameter
 * straight into a declared component input. A routed screen therefore never
 * injects `ActivatedRoute`, which is what keeps these components presentational
 * and directly constructible in a specification.
 *
 * ⚠ PARAMETER NAMES ARE A RUNTIME CONTRACT. `:portalId`, `:moduleId`, `:userId` and
 * `:roleId` are matched to component inputs BY NAME, and `permissionGuard` resolves
 * a scoped policy's subject from `:moduleId` as well (`permission.guard.ts:L164`).
 * Renaming a parameter breaks the binding silently: the input keeps its default, the
 * screen renders, and nothing in the build says why the record never loaded.
 *
 * ⚠ MIGRATION: NO PARAMETER IS COERCED, MATCHED NUMERICALLY OR DEFAULTED ANYWHERE IN
 * THIS TABLE, and the reason is a genuine collision in the legacy key space rather than
 * caution. Portal keys are `IDENTITY(-1, 1)` in the baseline schema and module, tab and
 * role keys are `IDENTITY(0, 1)`, so `-1` and `0` are DATA rather than absence — and
 * the legacy null contract at `Library/Components/Shared/Null.vb:L41-L45` returns
 * `-1` for a missing integer, so one value means both a real record and "no record".
 * `0` is therefore a legitimate portal, role, tab and module identifier, and `-1` is a
 * legitimate portal identifier as well as the legacy absent-marker. A `:int` matcher
 * would be harmless; a falsy test (`if (id)`) or an `id > 0` test would silently discard
 * the first row of four tables, so no route matcher or guard in this table rejects
 * either value, and each screen's own input decides presence by identity rather than by
 * magnitude.
 *
 * WHERE THE GATES ARE ATTACHED, AND WHY THERE
 * -------------------------------------------
 * `authGuard` is attached to feature ROOTS only — the four protected group parents
 * and the three standalone leaves — never to a child inside a barrel, so the session
 * requirement is declared once per group and inherited. Two addresses deliberately
 * carry no session gate, and both omissions are invariants rather than choices:
 *
 *   * `login`, because `core/guards/auth.guard.ts:L49-L56` states that a gate on the
 *     screen it redirects TO cannot be satisfied by any caller. The guard hardens its
 *     own half of that at L247 so a mistake degrades to "the sign-in screen is
 *     reachable" rather than to a redirect cycle, but this table must not lean on it.
 *   * `**`, because the same note requires the not-found screen to stay reachable in
 *     order to be able to say that nothing is there.
 *
 * `permissionGuard` is attached where a policy is actually declarable, which is inside
 * the barrels for the grouped children and here for the three standalone leaves. Each
 * barrel documents its own policy choices against the controller attributes they
 * mirror. A policy is named through route `data` under the key `permission`, which is
 * the key `permission.guard.ts` reads (`POLICY_DATA_KEY`, `:L152`); the two sides must
 * keep spelling it identically, because a policy under any other key is simply not
 * seen and the route then admits everybody the session gate admits.
 *
 * ⚠ EVERY ROUTE THAT DECLARES A POLICY ALSO ATTACHES `permissionGuard`, AND NOTHING
 * DECLARES ONE WITHOUT THE OTHER. A policy without its gate is inert — the route reads
 * as protected and is not — and a gate without a policy has nothing to answer. The
 * route specification asserts the biconditional across this table and all five barrels.
 *
 * MIGRATION: the POLICY VOCABULARY IS CLOSED, and an unknown name is not a soft
 * failure. The client's declarable set is the SAME NINE the API registers —
 * `ModuleView`, `ModuleEdit`, `TabView`, `TabEdit`, `PortalAdministrator`,
 * `HostAdministrator`, `AccountOwner`, `AccountOwnerOrPortalAdministrator` and
 * `PortalContentEditor` (`permission.guard.ts:L142-L152`, mirroring
 * `Api/Authorization/PolicyNames.cs` L86, L93, L105, L112, L147, L171, L185, L196, L231)
 * — and because the API registers no
 * `IAuthorizationPolicyProvider`, an unregistered policy name throws when the request is
 * authorised rather than degrading to a denial. Names are therefore never invented to fit
 * a screen, and equally never SUBSTITUTED for one another: an earlier revision of this
 * comment described the declarable set as "exactly five", a later one as eight, and the
 * first of those licensed each barrel to
 * approximate a host or account rule with the tenant rule, or to declare nothing at all.
 * Both licences are withdrawn. Every route declares the policy its own endpoint declares:
 * the portal COLLECTION and portal creation are `HostAdministrator` because they address
 * no single tenant; the account profile and credential screens are
 * `AccountOwnerOrPortalAdministrator` because their endpoints admit the account holder as
 * well as the tenant administrator; tenant, account, role and role-group administration
 * resolve to `PortalAdministrator`, because each is administration WITHIN a tenant and the
 * corresponding controllers are class-gated on exactly that; and module administration
 * resolves to `ModuleEdit`, which is scoped to one module instance and so is only ever
 * declared on a route that carries `:moduleId` for the API to resolve the scope from.
 *
 * MIGRATION: THE GATE IS ADVISORY; THE SERVER IS AUTHORITATIVE. Both guards decide
 * only whether to MOUNT a screen, using claims the browser already holds, so they
 * remove a screen the caller cannot use from the caller's path — they do not protect
 * the data behind it. Every request the screen then issues is authorised again by the
 * API, which answers `403` when the policy is not satisfied, and that answer is the
 * enforcement. A tampered token, a stale claim or a route with no policy declared
 * therefore changes what is DISPLAYED and never what is PERMITTED. The legacy
 * application had the same division without the vocabulary for it: each admin control
 * tested permission in its own load handler and redirected to
 * `Website/admin/Security/AccessDenied.ascx`, whose `Page_Load` renders a localized
 * warning and nothing else, while the data access behind it was guarded separately.
 *
 * ⚠ ORDERING CONSTRAINT FOR EVERY FUTURE EDIT. The router matches in declaration order
 * and stops at the first match, and `**` matches EVERYTHING, including the empty path.
 * It must therefore remain LAST in this array for all time. A feature group appended
 * after it is unreachable, and unreachable in the most confusing possible way: the
 * build succeeds, the lazy chunk is emitted, the route object is present in the
 * configuration, and every navigation to it silently renders the fallback instead.
 *
 * MIGRATION: ORDER IS LOAD-BEARING HERE IN A WAY IT NEVER WAS IN THE LEGACY
 * APPLICATION, and that is a new hazard rather than a ported one. Legacy addressing
 * dispatched on an exact numeric `TabId` looked up in the database, so two addresses
 * could not shadow one another and declaration order carried no meaning at all. Path
 * matching introduces shadowing: a STATIC segment must be declared BEFORE any parameter
 * that would otherwise swallow it. At this level that means the `''` redirect stays
 * first and `**` stays last; inside each barrel it means `new` precedes `:portalId`,
 * `:userId` and `:roleId`, and BOTH `new` AND `import` precede `:moduleId` — otherwise
 * `/modules/import` resolves as a module whose identifier is the string "import". Each
 * barrel restates that constraint at its head, and the route specification asserts it.
 *
 * The configuration is also never allowed to be empty. An empty array is not an
 * application with no routes; it is an application whose FIRST navigation fails. The
 * router performs an initial navigation to the document URL during bootstrap, finds
 * nothing to match, and raises `NG04002: Cannot match any routes`, which surfaces as
 * an unhandled rejection in the console on every single page load.
 *
 * MIGRATION: routing moves from the SERVER to the BROWSER, and the addressing scheme
 * changes with it. This table REPLACES the legacy DotNetNuke page pipeline outright.
 * The legacy application had no route table. Every request arrived at
 * `Website/Default.aspx` and the page resolved which content to render from
 * query-string state — a numeric `TabId` selecting the requested tab and a `PortalId`
 * selecting the tenant — after which `LoadSkin` (`Website/Default.aspx.vb` L217)
 * loaded the tenant's skin control and the page injected it into the single
 * `<asp:PlaceHolder ID="SkinPlaceHolder">` the document declared
 * (`Website/Default.aspx` L25), assembling the markup at run time.
 * Addresses were therefore opaque and identifier-bearing rather
 * than descriptive, and which control answered a given address was tenant
 * configuration rather than application structure. Here they are path-based and
 * readable, the tenant is resolved by the API from the request rather than named in the
 * URL, and no server-side page assembly is involved: the proxy returns the same
 * document for every path and this table decides what is mounted.
 *
 * MIGRATION: navigation gating moves from IMPERATIVE tests inside each page's load
 * handler to declarative route data answered in one place. `Portals.ascx.vb:L339-L341`
 * and `ModuleSettings.ascx.vb:L191-L193` are the canonical legacy shapes, and each
 * navigated away by side effect, so the set of protected screens could only be
 * discovered by reading every screen. It is now readable from this table and the five
 * barrels it names.
 */
export const APP_ROUTES: Routes = [
  {
    /**
     * The application root.
     *
     * `pathMatch: 'full'` is mandatory and not stylistic: the default prefix matching
     * would make the empty path match EVERY address, so this redirect would fire on
     * every navigation and no route below it would ever be reached.
     *
     * MIGRATION: this entry is NAVIGATION PLUMBING RATHER THAN A SCREEN, and it is the
     * only entry in this table that is not one of the twenty-five addresses the
     * migration plan enumerates. It exists because the root URL must resolve to
     * something: a router with no match for `/` raises `NG04002` on the very first
     * navigation of every page load. A redirect rather than a landing screen of its own,
     * because this workspace has no dashboard component and inventing one would be a
     * feature addition rather than a routing decision. It remains the single declared
     * destination `layout/header/header.component.ts` documents its identity affordance as
     * targeting, so that affordance still has exactly one place to point at.
     *
     * THE DESTINATION IS RESOLVED PER CALLER, and {@link rootLandingRedirect} carries the
     * whole reasoning — which authority lands where, why the order of its tests matters,
     * and the dead end a single static destination produced for every operator who is not
     * a host account.
     *
     * An unauthenticated visitor is still never shown the portals list by this: the
     * resolver answers the sign-in address directly. It answers it WITHOUT a `returnUrl`,
     * which is the one behavioural difference from the static form and is deliberate — the
     * caller asked for the application rather than for a particular screen, and capturing
     * a screen they did not choose is what used to send them back to an unreachable one
     * after signing in. That still reproduces the legacy behaviour, where an
     * unauthenticated request to the site root was answered by the login control rather
     * than by the requested page.
     */
    path: '',
    redirectTo: rootLandingRedirect,
    pathMatch: 'full',
  },
  {
    /**
     * The sign-in group. Ungated, per the invariant recorded above and in the barrel.
     *
     * Declared before the protected groups purely for readability — it is the first
     * screen a caller sees — since no two paths in this table overlap and the order
     * between distinct literal segments carries no meaning.
     */
    path: 'login',
    loadChildren: () => import('./features/auth/auth.routes').then((m) => m.AUTH_ROUTES),
  },
  {
    /**
     * Tenant administration. Five children; see `features/portal/portal.routes.ts`.
     */
    path: ROOT_REDIRECT_PATH,
    canActivate: [authGuard],
    loadChildren: () => import('./features/portal/portal.routes').then((m) => m.PORTAL_ROUTES),
  },
  {
    /**
     * Module administration. Six children; see `features/module/module.routes.ts`.
     */
    path: 'modules',
    canActivate: [authGuard],
    loadChildren: () => import('./features/module/module.routes').then((m) => m.MODULE_ROUTES),
  },
  {
    /**
     * Account administration. Five children; see `features/user/user.routes.ts`. The
     * group's own empty child is the account listing, which is why this group is reached
     * both from the navigation rail and from three screens that navigate to `/users`
     * directly.
     */
    path: 'users',
    canActivate: [authGuard],
    loadChildren: () => import('./features/user/user.routes').then((m) => m.USER_ROUTES),
  },
  {
    /**
     * Security role administration. Four children; see `features/role/role.routes.ts`.
     */
    path: 'roles',
    canActivate: [authGuard],
    loadChildren: () => import('./features/role/role.routes').then((m) => m.ROLE_ROUTES),
  },
  {
    /**
     * Role group creation.
     *
     * A top-level leaf rather than a child of `roles`, because a role group is a sibling
     * aggregate with its own controller (`RoleGroupsController`, class-gated on portal
     * administration at `:L173`) rather than a child of a role — and because the address
     * already exists in the shipped code: `role-list.component.ts` holds it as
     * `ADD_ROLE_GROUP_LINK = '/role-groups/new'`, so declaring it under `roles` would
     * break a link rather than tidy one.
     *
     * Both gates are attached here, unlike the grouped children, because a standalone
     * leaf has no parent to inherit the session requirement from.
     *
     * The title is measured rather than invented, and comes from the ACTION label the
     * legacy listing used to reach this screen rather than from the editor's own
     * heading: `AddGroup.Action` in
     * `Website/admin/Security/App_LocalResources/Roles.ascx.resx` is "Add New Role
     * Group". The editor's heading, `ControlTitle_editgroup.Text` in
     * `EditGroups.ascx.resx`, reads "Edit Role Group" because that one legacy control
     * served both adding and editing; this route is the ADD address only, so the action
     * label is the accurate wording and `role.routes.ts` takes the same approach for
     * `new` with `AddContent.Action`.
     */
    path: 'role-groups/new',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
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
     * The tenant's account-administration settings.
     *
     * Under `settings/` rather than under `users/` because it configures the TENANT
     * rather than an account, and because the address is already held by two screens
     * that link to it: `membership-settings` is reached from
     * `role-list.component.ts`'s `MEMBERSHIP_SETTINGS_LINK = '/settings/membership'`.
     *
     * `UsersController` gates the settings read and write on portal administration, so
     * that is what the route declares.
     *
     * The title is the legacy control's own heading, measured rather than invented:
     * `ControlTitle_usersettings.Text` in
     * `Website/admin/Users/App_LocalResources/UserSettings.ascx.resx` is "User
     * Settings", corroborated independently by `UserSettings.Action` in both
     * `Users.ascx.resx` and `Website/admin/Security/App_LocalResources/Roles.ascx.resx`.
     */
    path: 'settings/membership',
    // ⚠ LEAVING THIS SCREEN IS GUARDED, because it mounts an editing form. Measured before this
    // existed: Cancel, any in-application link and the browser's Back button all discarded a
    // dirty form in silence, with instrumented `confirm`, `alert` and `beforeunload` recording
    // nothing. The guard asks only when the mounted screen reports unsaved entry, so a clean
    // form still leaves without a word.
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
     * Profile property declarations for the tenant.
     *
     * A sibling of the account settings above for the same reason — it declares what
     * every account in the tenant may record, so it is tenant configuration rather than
     * a property of one account. `ProfileDefinitionsController` is class-gated on portal
     * administration (`:L165`), which the route mirrors.
     *
     * Reached from `membership-settings.component.ts`'s
     * `PROFILE_DEFINITIONS_PATH = '/settings/profile-definitions'`, so the path is a
     * contract with a screen that already ships.
     *
     * The title is the legacy control's own heading, measured rather than invented:
     * `ControlTitle_manageprofile.Text` in
     * `Website/admin/Users/App_LocalResources/ProfileDefinitions.ascx.resx` is
     * "Manage Profile Properties", corroborated by `ManageProfile.Action` in
     * `Website/admin/Users/App_LocalResources/Users.ascx.resx`, which is the wording
     * the legacy listing used on the link that reached this screen.
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
    /**
     * The catch-all. See the ordering constraint above: this must stay last.
     */
    path: '**',

    /**
     * MIGRATION: THE FALLBACK REUSES `empty-state` AND NO NOT-FOUND COMPONENT WAS
     * AUTHORED. The shared component library is closed at ten members, a not-found
     * component is not one of them, and adding an eleventh to render one sentence
     * would widen the design system rather than use it. The reuse is a deliberate
     * fit rather than a convenience: `empty-state`'s entire purpose is to explain,
     * in one sentence, why a region has nothing to show, which is precisely what
     * this route needs to say. It is also the one component in the workspace
     * authored to accept router-supplied wording, so the binding below is a
     * supported use of its published contract rather than a coincidence.
     *
     * The alternative the plan permits — `redirectTo` — was NOT taken, and it was a
     * real choice rather than an oversight: a redirect rewrites the address bar, so
     * a mistyped URL becomes indistinguishable from a deliberate navigation and the
     * reader loses the evidence of what they actually asked for. Resolving to a VIEW
     * keeps the address intact and states plainly that nothing answers it. The
     * legacy application had no equivalent at all — an unmatched address was a tab
     * lookup that returned nothing and the pipeline fell back to the tenant's home
     * tab — so this behaviour is introduced by the migration rather than ported.
     *
     * Lazily loaded, like every other view in the table, so the fallback costs
     * nothing on first paint.
     *
     * It is also the one component in the workspace authored to accept
     * router-supplied wording. Its `message` input widens its write type to
     * `string | readonly string[] | null | undefined` and documents the reason as
     * `null` and `undefined` being "values the router's binder genuinely passes",
     * so binding it from route data is a supported use of its published contract
     * rather than a coincidence that happens to work.
     *
     * This is the ONLY route in the workspace that resolves to the shared empty state, and
     * it should stay that way. Every declared address now resolves to the screen that owns
     * it, so reaching this one means the address was not recognised at all — which is the
     * single statement this wording has to make. Pressing the empty state into service as a
     * placeholder for a screen that is merely absent would blur that statement, leaving an
     * operator unable to tell a mistyped address from a missing feature.
     */
    loadComponent: () =>
      import('./features/not-found/not-found.component').then((m) => m.NotFoundComponent),

    /**
     * Bound to the component's `message` input by `withComponentInputBinding()`,
     * which `app.config.ts` enables. The router matches data keys to input names,
     * so this key is load-bearing and must stay spelled exactly as the input is.
     *
     * Supplying the wording from the route rather than letting the component fall
     * back to its own default is what makes the sentence specific to this
     * situation: the component's default describes an empty result set, which is a
     * different thing from an address that resolves to no screen.
     *
     * ONE KEY, AND IT MATCHES A DECLARED INPUT. That constraint is real - the binder
     * "reports an unknown property" for a data key matching no input and then does
     * nothing, so a misspelled key buys a console error and no behaviour - which is
     * why this key must stay spelled exactly as its input is.
     *
     * MIGRATION: a second key, `standalone`, was declared here and is REMOVED. It was
     * meant to raise the heading level of the shared empty state for the one screen that
     * is nothing else, but `NotFoundComponent` - the component this route resolves to, and
     * therefore the only one the binder can reach - declares no such input, so the key
     * bought a development-mode console warning and no behaviour whatsoever. The outline
     * it was written to fix is already correct without it: this screen composes
     * `app-page-header`, which contributes the level-one heading a routed view owes the
     * document, and the shared empty state's level two sits correctly beneath it. One
     * page-level heading, and nothing dead in the route table.
     */
    data: { message: NO_ROUTED_VIEW_MESSAGE },

    /**
     * Rendered in the browser tab and announced by screen readers on navigation.
     * Set here because the document title is otherwise fixed by `index.html` for
     * the whole application, so without it an unrecognised address would keep the
     * title of wherever the reader came from.
     *
     * This is the one title in the table with no legacy resource behind it, because
     * the legacy application had no not-found screen whose heading could be measured.
     * It is therefore authored, and deliberately kept to the plainest possible
     * statement suffixed with the application title `index.html:L13` already sets, so
     * that the tab reads consistently with every other address.
     */
    title: 'Not Found — DotNetNuke Administration',
  },
];

/**
 * The route table under the name the Angular CLI's own scaffolding uses.
 *
 * The SAME array as `APP_ROUTES` above rather than a second copy of it, so the two
 * names cannot drift apart and there is exactly one table to reason about.
 *
 * Both names exist because two conventions meet at this file and each has a real
 * consumer. `APP_ROUTES` is the name this workspace settled on and it is load-bearing:
 * `app.config.ts:L11` imports it to build `provideRouter`, and `app.routes.spec.ts:L50`
 * imports it to assert this table's structure. Neither file is ours to edit, so that
 * name cannot be retired. `routes` is the name a stock `ng new` workspace exports and
 * the one the migration plan names for this module, so it is published as well — an
 * alias costs a single binding and means a reader arriving with either expectation
 * finds what they came for.
 *
 * Adding a route to `APP_ROUTES` therefore adds it here automatically; ordering
 * constraints stated above apply to both names because there is only one array.
 */
export const routes: Routes = APP_ROUTES;
