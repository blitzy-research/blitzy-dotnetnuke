/**
 * The account administration feature's lazy route barrel: five child routes, no logic.
 *
 * LEGACY LINEAGE, MEASURED
 * ------------------------
 * Two legacy controls are replaced by the five children below.
 *
 * `Website/admin/Users/manageusers.ascx` (83 lines) is the true analogue of this file. It
 * is a TABBED CONTAINER rather than a form: `pnlTabs` (L11-L47) holds five
 * `dnn:commandbutton` tabs — `cmdUser` (L15), `cmdRoles` (L21), `cmdPassword` (L27),
 * `cmdProfile` (L33) and `cmdServices` (L39), every one of them
 * `causesvalidation="False"` — and each toggles the visibility of one of six server-side
 * panels: `pnlUser` (L48-L66), `pnlRoles` (L67), `pnlPassword` (L70), `pnlProfile` (L73),
 * `pnlServices` (L76) and `pnlRegister` (L79). Its workflow authority is the code-behind
 * alongside it (973 lines), whose entire tab-switching apparatus the router now performs.
 *
 * `Website/admin/Users/users.ascx` (83 lines) is the account listing: an `asp:datagrid`
 * (`grdUsers`, L22) with three image command columns — Edit (L32), Delete (L33) and
 * UserRoles (L34) — plus a paging control (L83). Its navigation is measured in
 * `Website/admin/Users/Users.ascx.vb`: L530 builds the Edit address with
 * `EditUrl("UserId", "KEYFIELD", "Edit", UserFilter(False))` and L542 builds the
 * role-membership address with `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD", …)`.
 *
 * MIGRATION: the five-tab `pnlTabs` container is replaced by ROUTING, and the tab set is
 * deliberately not reproduced one-for-one. `cmdUser` becomes `:userId`, `cmdProfile`
 * becomes `:userId/profile`, `cmdPassword` becomes `:userId/password`. `cmdRoles` becomes
 * no route here at all — see the next note. `cmdServices` and the sixth panel
 * `pnlRegister` are DROPPED: there is no member-services endpoint server-side, and public
 * self-registration is not part of an administration console. Dropping a tab is a
 * functional reduction rather than a re-arrangement, which is why it is recorded here
 * instead of being left to be inferred from an absence.
 *
 * MIGRATION: no per-account roles route exists in the application's closed route table, so
 * the legacy `cmdRoles` tab and the grid's `UserRoles` column (`users.ascx:L34`) have no
 * counterpart in this barrel. Role membership is reached from the ROLE side, at
 * `/roles/:roleId/users`, and the listing screen therefore emits a plain address string to
 * it. Features are strict siblings that never import one another, so nothing here
 * references the role feature and no route is added for it.
 *
 * MIGRATION: the two remaining account screens — the tenant's membership settings and the
 * profile-property declarations — are mounted by `app.routes.ts` as top-level leaves of its
 * own, at L240 and L262. They are deliberately NOT children of this barrel even though
 * their components live in this feature folder, because they configure the TENANT rather
 * than one account. Adding them here would publish each screen at two addresses and mount
 * each component twice.
 *
 * HOW THIS BARREL IS MOUNTED, AND WHY EVERY PATH BELOW IS RELATIVE
 * ---------------------------------------------------------------
 * `app.routes.ts:L193-L195` mounts this array lazily beneath the parent segment
 * "users", resolving the exported name below, and attaches the session gate to that
 * parent.
 *
 * MIGRATION: because the parent already contributes the `users` segment, every child path
 * here is RELATIVE to it and none may repeat that segment. Re-prefixing a child with it
 * would publish `/users/users`, and nothing would report it: paths are strings, so there is
 * no compile error and no build warning, and the fault surfaces only when somebody follows
 * a link. The parent's session gate is likewise never re-declared on a child, because a
 * child inherits it.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING. The router matches in declaration order and takes
 * the first match, and `:userId` matches ANY single segment — including the literal `new`.
 * `'new'` must therefore stay above `':userId'`, or `/users/new` resolves to the edit
 * screen carrying the string `"new"` as an account key, which the API answers with a
 * refusal rather than a form.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN
 * ---------------------------------------
 * MIGRATION: the legacy imperative, per-page access test is replaced by a declarative
 * policy declaration, and the policy vocabulary is CLOSED. A child either names one policy
 * the client gate has registered or names none at all, and naming one is always paired with
 * attaching the gate — the two travel together, because a policy without a gate declares an
 * intention nothing acts on, and a gate without a policy fails closed and refuses
 * everybody. Account administration maps to the tenant-administration policy declared on
 * the four children below, which asks about the caller WITHIN a tenant and resolves
 * without needing a subject identifier.
 *
 * The legacy predicate is measured rather than assumed:
 * `Library/Components/Users/UserModuleBase.vb:L287-L291` defines `IsAdmin` as
 * `UserInfo.IsInRole(PortalSettings.AdministratorRoleName)` combined with the host-account
 * flag, which is exactly what the client gate derives, and the code-behind refuses at L440 a caller who is
 * `Not IsAdmin And Not IsUser`.
 *
 * ⚠ THE GATE IS ADVISORY AND THE SERVER IS THE AUTHORITY. Every address below is
 * re-authorised server-side against stored state and refused with HTTP 403 on its own
 * account, and that refusal is the authoritative one. These declarations exist only to
 * avoid navigating an operator to a screen they demonstrably cannot use. Admission here
 * never implies the next request will succeed, and nothing in this file is an enforcement
 * point.
 *
 * ⚠ SENTINEL DISCIPLINE — `0` AND `-1` ARE REAL IDENTIFIERS
 * --------------------------------------------------------
 * MIGRATION: no custom URL match function, coercion or validity test is applied to
 * `:userId`, and none may be added. The baseline schema seeds accounts at `UserID IDENTITY(1,1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L98`) but
 * seeds portals at `IDENTITY(-1,1)` (L77) and roles, pages and modules at `IDENTITY(0,1)`
 * (L115, L140, L221), while the legacy null contract at
 * `Library/Components/Shared/Null.vb:L41-L45` returns `-1` for a missing integer — so one
 * value denotes both a real record and "no record". Consumers of this parameter must
 * convert it explicitly, and must never write `if (id)`, `id > 0` or `id ?? -1`: the first
 * two silently refuse row zero, and the third manufactures a real identifier out of an
 * absent one.
 */

import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The wording the account-listing address renders in place of a listing.
 *
 * Held as a named constant so a specification can assert the rendered text against the
 * string the route supplies rather than restating it and letting the two drift.
 *
 * Phrased as a statement about the WORKSPACE rather than about the address, which is what
 * distinguishes it from the top-level catch-all sentence. `/users` is a declared, gated,
 * titled address that three existing screens navigate to; it is not an unrecognised one,
 * and telling an operator "no screen at this address" would misdescribe which of the two
 * situations they are in.
 */
export const ACCOUNT_LISTING_UNAVAILABLE_MESSAGE =
  'An account listing screen is not part of this administration workspace.';

/**
 * The five child routes mounted beneath `/users`.
 *
 * Named exactly as `app.routes.ts` resolves it — `m.USER_ROUTES` — and exported by name
 * rather than as a default: a rename would leave the dynamic import resolving to
 * `undefined` and take the whole `/users` tree to the catch-all, with no compile error to
 * report it.
 */
export const USER_ROUTES: Routes = [
  {
    /**
     * `/users` — the account listing address.
     *
     * ⚠ THIS RESOLVES TO AN EXPLANATION RATHER THAN TO A LISTING, AND THAT IS A RECORDED
     * SHORTFALL RATHER THAN A DESIGN PREFERENCE.
     *
     * `features/user/user-list/user-list.component.ts` exists and declares
     * `UserListComponent`, but its decorator names `./user-list.component.html` and
     * `./user-list.component.scss` and NEITHER FILE IS PRESENT. Pointing this route at it
     * fails the production build outright with
     * `TS-992008: Could not find template file './user-list.component.html'` — established
     * by running the build, not by inference. The component compiles today only because
     * nothing imports it, `tsconfig.app.json` being driven by the import graph from
     * `src/main.ts`. Authoring that template and stylesheet belongs to the component's own
     * author, so it is not done from here.
     *
     * The address still has to resolve, because it is a live navigation target from three
     * places that already exist: the account form navigates here after a record is created
     * and again after one is deleted — the second reproducing the measured legacy redirect
     * at L900 of the code-behind — and the membership settings screen links here from its
     * header action slot. Leaving it unmatched would fall through to the top-level catch-all
     * and land an operator outside the gated area on a not-found view, reading as a mistyped
     * address rather than as a known gap. Redirecting it elsewhere would rewrite
     * the address bar so nobody could tell they had been sent somewhere else. Resolving it
     * to the shared empty state invents no screen, keeps the address inside the gated area
     * with a title of its own, and says plainly what is missing.
     *
     * ONE EDIT COMPLETES IT once the two sibling files land: point the lazy import below at
     * `./user-list/user-list.component`, resolve its exported class instead of the shared
     * empty state, then delete the `data` entry that follows.
     *
     * Carries no `canActivate` and names no policy: reading the listing is admitted to any
     * signed-in operator by the parent's session gate, exactly as the sibling role barrel
     * leaves its own listing ungated and gates only its writes.
     */
    path: '',
    title: 'User Accounts',
    loadComponent: () =>
      import('../../shared/components/empty-state/empty-state.component').then(
        (m) => m.EmptyStateComponent,
      ),

    /**
     * Bound to the shared component's `message` input by the router's component-input
     * binder, which writes route data onto a declared input of the same name — so this key
     * is load-bearing and must stay spelled as the input is. The component's own default
     * describes an empty RESULT SET, which is a different statement from a screen that is
     * not present at all, and this wording is deliberately distinguishable from the
     * top-level catch-all sentence for the same reason.
     */
    data: { message: ACCOUNT_LISTING_UNAVAILABLE_MESSAGE },
  },
  {
    /**
     * `/users/new` — account creation, gated on the policy the create endpoint declares.
     *
     * ⚠ MUST STAY ABOVE `':userId'`; see the ordering note in the file header. This is the
     * literal segment the parameter route would otherwise swallow.
     *
     * Reaches the same component as the edit route below: create and edit are unified into
     * one screen, exactly as the legacy container hosted a single `dnn:user ctlUser`
     * control (`manageusers.ascx:L60`) for both. The component chooses between its two
     * measured headings — `Add New User` and `Edit User Accounts` — from whether an
     * identifier arrived, so no mode flag is passed and none may be added.
     *
     * Titled from the value the legacy screen actually rendered: `AddUser.Text` in the
     * control's own resource file reads "Add New User", and its code-behind assigns exactly
     * that resource to the screen title in create mode at L252-L253.
     */
    path: 'new',
    title: 'Add New User',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () => import('./user-form/user-form.component').then((m) => m.UserFormComponent),
  },
  {
    /**
     * `/users/{userId}` — account credentials; the legacy `cmdUser` tab.
     *
     * Gated on tenant administration, the policy the update and delete endpoints declare.
     * The legacy screen paired `dnn:user ctlUser` with `dnn:membership ctlMembership` in
     * ONE row (`manageusers.ascx:L59-L63`), which is the measured evidence that the
     * per-account membership actions — authorise, unauthorise, unlock and force a password
     * change — belong on this detail screen rather than on a tenant-level settings page.
     *
     * No custom matching and no coercion on the parameter: the component takes the raw
     * string, and
     * the sentinel discipline in the file header forbids reading any numeric value as
     * absence.
     *
     * Titled from `ControlTitle_edit.Text` = "Edit User Accounts".
     */
    path: ':userId',
    title: 'Edit User Accounts',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () => import('./user-form/user-form.component').then((m) => m.UserFormComponent),
  },
  {
    /**
     * `/users/{userId}/profile` — the account's profile properties; the legacy `cmdProfile`
     * tab.
     *
     * Gated on tenant administration. The component declares its own view-or-edit `mode`
     * input WITH a default, and that default is the mode this administrative address wants,
     * so no `mode` key is declared here — the router's input binder turns every additional
     * data key into an implicit input binding, which is why `permission` is the only one
     * present. The toggle is in-component state; there is deliberately no second,
     * view-only profile route.
     *
     * Titled from `ControlTitle_profile.Text` = "Manage Profile", which is also the
     * measured `cmdProfile.Text` tab label.
     */
    path: ':userId/profile',
    title: 'Manage Profile',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./user-profile/user-profile.component').then((m) => m.UserProfileComponent),
  },
  {
    /**
     * `/users/{userId}/password` — the account's password; the legacy `cmdPassword` tab.
     *
     * Gated on tenant administration. The component documents this expectation of its own
     * route in as many words — that the route reaching it "carries a tenant-administration
     * permission and is guarded" — and still derives the predicate rather than assuming it,
     * precisely because the gate is advisory and the server is the authority.
     *
     * The component declares its `userId` input as REQUIRED, so this address can never be
     * reached without the parameter; that is why there is no sibling route at
     * `users/password`.
     *
     * Titled from the measured `cmdPassword.Text` tab label = "Manage Password". The
     * nearer resource, `Password.ascx.resx` → `PasswordTitle.Text`, reads
     * "Manage Password - {0} (Id: {1})" and is deliberately NOT used: a format string
     * carrying placeholders cannot serve as a static document title, and inventing
     * replacement wording would be worse than reusing the tab label the screen was reached
     * by.
     */
    path: ':userId/password',
    title: 'Manage Password',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./user-password/user-password.component').then((m) => m.UserPasswordComponent),
  },
];
