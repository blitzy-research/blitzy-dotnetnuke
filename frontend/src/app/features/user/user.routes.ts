/**
 * The account administration feature's lazy route barrel: six child routes, no logic.
 *
 * MIGRATION: the legacy five-tab container is replaced by ROUTING, and the tab set is deliberately not
 * reproduced one-for-one. `cmdUser` becomes `:userId`, `cmdProfile` becomes `:userId/profile` and
 * `cmdPassword` becomes `:userId/password`; `cmdRoles` becomes no route here at all, and `cmdServices` and
 * the sixth `pnlRegister` panel are DROPPED, because there is no member-services endpoint server-side and
 * public self-registration is not part of an administration console. Dropping a tab is a functional reduction
 * rather than a re-arrangement, which is why it is recorded rather than left to be inferred from an absence.
 *
 * MIGRATION: no per-account roles route exists in the application's closed route table, so the legacy
 * `cmdRoles` tab and the grid's role-membership column have no counterpart here. Role membership is reached
 * from the ROLE side, at `/roles/:roleId/users`, and the listing screen therefore emits a plain address
 * string to it. Features are strict siblings that never import one another, so nothing here references the
 * role feature and no route is added for it.
 *
 * MIGRATION: the two remaining account screens - the tenant's membership settings and the profile-property
 * declarations - are mounted by `app.routes.ts` as top-level leaves of its own. They are deliberately NOT
 * children of this barrel even though their components live in this feature folder, because they configure
 * the TENANT rather than one account, and mounting them here would publish each screen at two addresses and
 * mount each component twice.
 *
 * MIGRATION: the five-tab `pnlTabs` container is replaced by ROUTING, and the tab set is
 * deliberately not reproduced one-for-one. `cmdUser` becomes `:userId`, `cmdProfile`
 * becomes `:userId/profile`, `cmdPassword` becomes `:userId/password`, and `cmdServices`
 * becomes `:userId/services`. `cmdRoles` becomes no route here at all — see the next note.
 * The sixth panel `pnlRegister` is DROPPED: public self-registration is not part of an
 * administration console. Dropping a tab is a functional reduction rather than a
 * re-arrangement, which is why it is recorded here instead of being left to be inferred
 * from an absence.
 *
 * ⚠ THE SERVICES ROUTE WAS ITSELF A DOCUMENTED OMISSION UNTIL THE ENDPOINTS EXISTED. An
 * earlier revision of this note recorded `cmdServices` as dropped "because there is no
 * member-services endpoint server-side", which was true of the API as it then stood; the
 * account resource now publishes the five self-service endpoints the legacy panel needed,
 * so the omission is withdrawn and the tab has a route again.
 *
 * Declaration order is load-bearing. The router matches in declaration order and takes the first match, and
 * `:userId` matches ANY single segment - including the literal `new`. `'new'` must therefore stay above
 * `':userId'`, or `/users/new` resolves to the edit screen carrying the string `"new"` as an account key,
 * which the API answers with a refusal rather than a form.
 *
 * MIGRATION: the legacy imperative, per-page access test is replaced by a declarative policy declaration, and
 * the policy vocabulary is CLOSED. A child either names one policy the client gate has registered or names
 * none at all, and naming one is always paired with attaching the gate - a policy without a gate declares an
 * intention nothing acts on, and a gate without a policy fails closed and refuses everybody. Account
 * administration maps to the tenant-administration policy declared on the four children below, which asks
 * about the caller WITHIN a tenant and resolves without needing a subject identifier. The gate is advisory
 * and the server is the authority: every address below is re-authorised server-side and refused with HTTP 403
 * on its own account, so admission here never implies the next request will succeed.
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
 * everybody.
 *
 * ⚠ EACH CHILD DECLARES THE POLICY ITS OWN PRIMARY ENDPOINT DECLARES, READ FROM THE
 * CONTROLLER. That rule is what makes these declarations useful rather than merely present,
 * and getting it wrong is silent in the worst direction: a route that declares a NARROWER
 * policy than its endpoint refuses a caller the server would have admitted, and the caller
 * never reaches the screen to find out. Three of the four children below are administrative
 * and declare tenant administration, matching `UsersController.cs:L451` (create), `:L493`
 * (update) and `:L541` (delete). The two SELF-SERVICE children do not, because their
 * endpoints do not:
 *
 *   * `:userId/profile` declares `AccountOwnerOrPortalAdministrator`, matching
 *     `UsersController.cs:L888` (read) and `:L927` (write). The profile is a resource the
 *     account holder and its tenant's administrator both legitimately reach.
 *   * `:userId/password` declares `AccountOwnerOrPortalAdministrator`, and it is the ONE
 *     address in this barrel that does not mirror a single endpoint — because its screen
 *     posts to two. `UsersController.cs:L576` is the credential CHANGE and declares
 *     `AccountOwner` with no administrator arm, since a change presents the current
 *     credential; `:L627` is the RESET beside it and declares tenant administration. The
 *     route therefore declares the UNION of the two, and the screen refuses per operation.
 *     See the address's own note below for why declaring ownership alone was a defect
 *     rather than a tightening.
 *
 * MIGRATION: a previous revision declared tenant administration on BOTH self-service
 * children. That was a defect rather than a conservative choice, and it is worth recording
 * because the failure was invisible from this file: an ordinary account holder was turned
 * away from its own profile and its own password change — the two screens a blocking
 * remediation exists to send it to — while the server would have admitted it. The gate
 * being advisory does not soften that: an advisory gate that refuses is the only thing
 * standing between the caller and a screen it is entitled to, because the request the
 * server would have allowed is never issued.
 *
 * Both policies resolve their subject from the `:userId` segment, which is the name the
 * server reads (`PortalAdministrationEvaluator.cs:L84`) and the name the gate resolves
 * (`permission.guard.ts` `ACCOUNT_SCOPE_PARAM`). The segment is therefore load-bearing for
 * authorisation as well as for loading, and renaming it would make the gate refuse both
 * screens outright.
 *
 * The legacy predicate behind the ADMINISTRATIVE arm is measured rather than assumed:
 * `Library/Components/Users/UserModuleBase.vb:L287-L291` defines `IsAdmin` as
 * `UserInfo.IsInRole(PortalSettings.AdministratorRoleName)` combined with the host-account
 * flag, which is exactly what the client gate derives. And the legacy screen itself had the
 * self-service arm too: the code-behind refuses at L440 a caller who is
 * `Not IsAdmin And Not IsUser` — an OR of administration and ownership, not administration
 * alone — so admitting the account holder to its own screens is parity rather than a
 * relaxation.
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
 * The six child routes mounted beneath `/users`.
 *
 * Named exactly as `app.routes.ts` resolves it — `m.USER_ROUTES` — and exported by name rather than as a
 * default: a rename would leave the dynamic import resolving to `undefined` and take the whole `/users` tree
 * to the catch-all, with no compile error to report it.
 */
export const USER_ROUTES: Routes = [
  {
    /**
     * This address is a live navigation target from three places that already exist: the account form
     * navigates here after a record is created and again after one is deleted — the second reproducing the
     * measured legacy redirect of the code-behind — and the membership settings screen links here from its
     * header action slot. It is also the rail's entry for account administration.
     *
     * Carries no `canActivate` and names no policy: reading the listing is admitted to any signed-in
     * operator by the parent's session gate, exactly as the sibling role barrel leaves its own listing
     * ungated and gates only its writes. The mutating affordances inside the screen are gated by the
     * permission directive, and the server answers 403 regardless — which is the only authority.
     *
     * ⚠ Carries no `canActivate` and names no policy: reading the listing is admitted to
     * any signed-in operator by the parent's session gate. The mutating affordances inside
     * the screen are gated by the permission directive, and the server answers 403
     * regardless — which is the only authority.
     *
     * ⚠ AND THAT IS A KNOWN DIVERGENCE FROM THE ENDPOINT, RECORDED RATHER THAN LEFT TO BE
     * INFERRED. `UsersController.cs:L450` gates `GET /users` on tenant administration, so a
     * caller without it reaches this screen and its first request is refused. An earlier
     * revision of this note justified the omission by pointing at the sibling role barrel
     * "leaving its own listing ungated" — that is no longer true; the role listing now
     * declares the policy its class-gated controller requires. The claim was removed rather
     * than restated because a justification that rests on another file's behaviour stops
     * being a justification the moment that file changes.
     *
     * ⚠ Resolved by NAME, not as a default export: a rename would leave the dynamic import
     * yielding `undefined` and take the address to the catch-all with no compile error to
     * report it.
     */
    path: '',
    title: 'User Accounts',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./user-list/user-list.component').then((m) => m.UserListComponent),
  },
  {
    /**
     * `/users/new` — account creation, gated on the policy the create endpoint declares.
     *
     * MUST STAY ABOVE `':userId'`; see the ordering note in the file header. This is the literal segment the
     * parameter route would otherwise swallow.
     *
     * Reaches the same component as the edit route below: create and edit are unified into one screen,
     * exactly as the legacy container hosted a single `dnn:user ctlUser` control for both. The component
     * chooses between its two measured headings — `Add New User` and `Edit User Accounts` — from whether an
     * identifier arrived, so no mode flag is passed and none may be added.
     *
     * Titled from the value the legacy screen actually rendered: `AddUser.Text` in the control's own
     * resource file reads "Add New User", and its code-behind assigns exactly that resource to the screen
     * title in create mode.
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
     * Gated on tenant administration, the policy the update and delete endpoints declare. The legacy screen
     * paired `dnn:user ctlUser` with `dnn:membership ctlMembership` in ONE row, which is the measured
     * evidence that the per-account membership actions — authorise, unauthorise, unlock and force a password
     * change — belong on this detail screen rather than on a tenant-level settings page.
     *
     * No custom matching and no coercion on the parameter: the component takes the raw string, and the
     * sentinel discipline in the file header forbids reading any numeric value as absence.
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
     * `/users/{userId}/profile` — the account's profile properties; the legacy `cmdProfile` tab.
     *
     * Gated on OWNERSHIP-OR-TENANT-ADMINISTRATION, which is exactly what
     * `UsersController.cs:L888` and `:L927` declare for the read and the write behind this
     * screen. Declaring tenant administration alone here would refuse every account holder
     * its own profile while the server stood ready to serve it.
     *
     * The subject is the `:userId` segment: the gate resolves ownership by comparing that
     * value against the signed-in account's own key and admits the tenant administrator on
     * the other arm, in the same order the server evaluates them.
     *
     * The component declares its own view-or-edit `mode` input WITH a default, and that
     * default is the mode this address wants, so no `mode` key is declared here — the
     * router's input binder turns every additional data key into an implicit input binding,
     * which is why `permission` is the only one present. The toggle is in-component state;
     * there is deliberately no second, view-only profile route.
     *
     * Titled from `ControlTitle_profile.Text` = "Manage Profile", which is also the measured
     * `cmdProfile.Text` tab label.
     */
    path: ':userId/profile',
    title: 'Manage Profile',
    canActivate: [permissionGuard],
    data: { permission: 'AccountOwnerOrPortalAdministrator' },
    loadComponent: () =>
      import('./user-profile/user-profile.component').then((m) => m.UserProfileComponent),
  },
  {
    /**
     * `/users/{userId}/password` — the account's password; the legacy `cmdPassword` tab.
     *
     * ⚠ GATED ON OWNERSHIP **OR** TENANT ADMINISTRATION, BECAUSE THIS SCREEN PERFORMS TWO
     * OPERATIONS AGAINST TWO ENDPOINTS WITH TWO DIFFERENT POLICIES. An earlier revision
     * declared ownership alone, reasoning that tenant administration "would admit an operator
     * to a form whose only endpoint would refuse them". That premise was simply untrue: the
     * screen posts to `POST {userId}/password` for a self-service change, which
     * `UsersController.cs:L716` restricts to the account holder, AND to
     * `POST {userId}/password-reset` for an administrative reset, which `:L767` restricts to a
     * tenant administrator. The component already chooses between them from `isSelf`.
     *
     * Declaring only the narrower of the two policies therefore locked a tenant administrator
     * out of the reset entirely — not out of an endpoint, out of the SCREEN: a refused
     * navigation is cancelled and redirected, so an operator who followed the account edit
     * screen's own affordance for this address — the "Manage User's Password" link at
     * `user-form/user-form.component.html` L92, on a route this same barrel gates on tenant
     * administration — was returned to the sign-in screen instead. The reset endpoint existed,
     * the administrator was entitled to it, the application published a link to it, and no
     * address in the application reached it.
     *
     * ⚠ THE UNION IS NOT A LOOSENING, BECAUSE NEITHER SERVER POLICY MOVES. Each operation is
     * still authorised by its own endpoint on its own terms: an administrator who reaches this
     * screen for another account can reset but cannot change, and an account holder can change
     * but cannot reset. The route admits the union of the two callers; the server keeps the
     * intersection empty where it should be.
     *
     * The component derives its own predicate rather than trusting this declaration, precisely
     * because the gate is advisory and the server is the authority — and it now also FAILS
     * CLOSED per operation, so a caller admitted here for one operation is never offered the
     * other.
     *
     * The component declares its `userId` input as REQUIRED, so this address can never be reached without
     * the parameter; that is why there is no sibling route at `users/password`.
     *
     * Titled from the measured `cmdPassword.Text` tab label = "Manage Password". The nearer resource,
     * `Password.ascx.resx` → `PasswordTitle.Text`, reads "Manage Password - {0} (Id: {1})" and is
     * deliberately NOT used: a format string carrying placeholders cannot serve as a static document title,
     * and inventing replacement wording would be worse than reusing the tab label the screen was reached by.
     */
    path: ':userId/password',
    title: 'Manage Password',
    canActivate: [permissionGuard],
    data: { permission: 'AccountOwnerOrPortalAdministrator' },
    loadComponent: () =>
      import('./user-password/user-password.component').then((m) => m.UserPasswordComponent),
  },
  {
    /**
     * `/users/{userId}/services` — the account's own subscriptions; the legacy `cmdServices`
     * tab.
     *
     * ⚠ GATED ON OWNERSHIP ALONE, WITH NO ADMINISTRATOR ARM, which is what all five of its
     * endpoints declare. See the policy note in the file header for the measurement behind
     * that: the legacy panel operated on the signed-in account and its container hid the tab
     * from an administrator, so the union policy would publish an affordance the legacy
     * application refused. An administrator's route to the same underlying rows is the role
     * resource, at `/roles/:roleId/users`, where effective and expiry dates are administered.
     *
     * The component lives in the `membership-settings` folder rather than in one named after
     * itself, because that is the folder the transformation plan maps
     * `Website/admin/Users/MemberServices.ascx.vb` into. It is nevertheless a separate
     * component from the tenant's account policy screen: the two differ in whose data they
     * show and in who may see it, and one route cannot satisfy two authorisation policies.
     *
     * No custom matching and no coercion on the parameter: the component parses it with the
     * shared route-identifier grammar, and the sentinel discipline in the file header forbids
     * reading any numeric value as absence.
     *
     * Titled from the measured `cmdServices.Text` tab label = "Manage Services". The panel's
     * own resource file declares no title, because it was a tab inside a container that
     * supplied one.
     */
    path: ':userId/services',
    title: 'Manage Services',
    canActivate: [permissionGuard],
    data: { permission: 'AccountOwner' },
    loadComponent: () =>
      import('./membership-settings/member-services/member-services.component').then(
        (m) => m.MemberServicesComponent,
      ),
  },
];
