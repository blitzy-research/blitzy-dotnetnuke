import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The wording the account-listing address renders.
 *
 * Held as a named constant for the same reason `app.routes.ts` holds the fallback
 * sentence as one: a specification asserts the rendered text against the string the
 * route supplies, rather than restating it and letting the two drift.
 *
 * Phrased as a statement about the WORKSPACE rather than about the address, which is
 * what distinguishes it from the top-level fallback sentence. `/users` is a declared,
 * gated, titled address that three existing screens navigate to; it is not an
 * unrecognised one, and telling an operator "no screen at this address" would be
 * misleading about which of the two situations they are in.
 */
export const ACCOUNT_LISTING_UNAVAILABLE_MESSAGE =
  'An account listing screen is not part of this administration workspace.';

/**
 * The account administration feature's route barrel.
 *
 * `app.routes.ts` mounts this array beneath the `users` path with `loadChildren` and
 * attaches `authGuard` to that parent, so every child inherits the session requirement.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING. `new` MUST stay above `:userId`, which matches any
 * single segment and would otherwise take the literal `new` as an account key.
 *
 * ⚠ THE ACCOUNT LISTING SCREEN DOES NOT EXIST IN THIS WORKSPACE, AND THE EMPTY CHILD
 * BELOW IS THE HONEST CONSEQUENCE RATHER THAN A PLACEHOLDER.
 * -------------------------------------------------------------------------------
 * The migration plan enumerates a `/users` listing sourced from
 * `Website/admin/Users/Users.ascx.vb`, and `features/user/` holds no component for it:
 * the folder contains the account form, the profile screen, the password screen, the
 * tenant's account settings and the profile-property declarations, and nothing that
 * lists accounts. Authoring one here would be a FEATURE ADDITION rather than a route
 * fix, so it is not done, and the shortfall is stated instead of being papered over.
 *
 * The address still has to resolve, because it is a live navigation target from three
 * places that already exist: `user-form.component.ts:L1542` navigates here after an
 * account is created and `:L1589` after one is deleted — the second reproducing the
 * measured legacy redirect at `ManageUsers.ascx.vb:L900` — and
 * `membership-settings.component.ts:L708` links here from its header action slot.
 *
 * Three arrangements were possible and the third is used:
 *
 *   * Declare no empty child. `/users` would then fail to match this component-less
 *     parent — a route matches only when the URL is fully consumed — and fall through to
 *     the top-level `**`. Rejected: the three navigations above would land an operator on
 *     the not-found view, OUTSIDE the gated area, and the address would read as a typo
 *     rather than as a known limitation.
 *   * Redirect `/users` somewhere that does exist. Rejected outright: every candidate —
 *     the tenant's account settings, the account form — answers a different question, and
 *     a redirect would rewrite the address bar so the operator could not tell that what
 *     they asked for was not what they got.
 *   * Resolve it to the shared empty state with wording specific to the situation. Used.
 *     It invents no screen, keeps `/users` inside the gated area with a title of its own,
 *     and says plainly what is missing. The precedent is already set by the top-level
 *     `**` route, whose own documentation calls reusing this component "a deliberate fit
 *     rather than a convenience": explaining in one sentence why a region has nothing to
 *     show is the component's entire purpose, and it is the one component in the
 *     workspace authored to accept router-supplied wording.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN
 * ---------------------------------------
 * A child declares the policy its own primary endpoint declares, and declares nothing
 * when that policy has no expression in the client's five-name vocabulary
 * (`permission.guard.ts:L105-L111`). This group is where that distinction does the most
 * work, because `UsersController` deliberately splits its actions across THREE policies
 * and only one of the three is expressible:
 *
 *   * `PortalAdministrator` — creating (`:L451`), updating (`:L493`) and deleting
 *     (`:L541`) an account, and the tenant's account settings. Expressible, and declared.
 *   * `AccountOwner` — changing one's own password (`:L576`). Not expressible.
 *   * `AccountOwnerOrPortalAdministrator` — reading an account (`:L398`) and reading or
 *     writing its profile (`:L887`, `:L926`). Not expressible.
 *
 * The two inexpressible policies are both WIDER than portal administration, not
 * narrower, and that is precisely why substituting `PortalAdministrator` for them would
 * be a defect rather than an approximation: the password screen and the profile screen
 * are the two self-service screens in the console, so gating them on portal
 * administration would make an ordinary account holder unable to reach their own
 * password or their own profile — a narrowing of exactly the kind the API's own comments
 * refuse elsewhere. They are therefore left to the parent's session gate and to the
 * server, which is the only party that can compare the caller against the account.
 */
export const USER_ROUTES: Routes = [
  {
    /**
     * `/users`. See the note above for why this resolves to an explanation rather than to
     * a listing, and why it is declared here rather than left to the top-level fallback.
     *
     * Lazily loaded like every other view, and gated by the parent's `authGuard`, so the
     * sentence is shown to signed-in operators only.
     */
    path: '',
    title: 'Users',
    loadComponent: () =>
      import('../../shared/components/empty-state/empty-state.component').then(
        (m) => m.EmptyStateComponent,
      ),

    /**
     * Bound to the component's `message` input by `withComponentInputBinding()`, which
     * matches route data keys to input names — so this key is load-bearing and must stay
     * spelled exactly as the input is. The component's own default describes an empty
     * result set, which is a different thing from a screen that is not present at all.
     */
    data: { message: ACCOUNT_LISTING_UNAVAILABLE_MESSAGE },
  },
  {
    /**
     * Account creation. `UsersController:L451` gates the create on portal administration.
     *
     * Reaches the same component as the edit route below, which decides between its two
     * measured headings — `Add New User` and `Edit User Accounts` — from whether an
     * identifier arrived (`user-form.component.ts:L1213`).
     */
    path: 'new',
    title: 'Add New User',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () => import('./user-form/user-form.component').then((m) => m.UserFormComponent),
  },
  {
    /**
     * Account editing. `UsersController:L493` gates the update on portal administration,
     * and this route declares the policy of the action the screen exists to perform
     * rather than the wider one its initial read is admitted under.
     *
     * No numeric matcher and no coercion on the parameter: the component's input takes
     * the raw string (`user-form.component.ts:L960`), and the legacy null contract
     * returns `-1` for a missing integer, so no numeric value may be read as absence.
     */
    path: ':userId',
    title: 'Edit User Accounts',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () => import('./user-form/user-form.component').then((m) => m.UserFormComponent),
  },
  {
    /**
     * The account's profile properties. Ungated for the reason set out above: both profile
     * endpoints (`UsersController:L887`, `:L926`) admit the account owner as well as a
     * portal administrator, and that combined policy has no client expression.
     *
     * Declares no `mode`, so the component's own default applies — `edit`
     * (`user-profile.component.ts:L57`), the mode this administrative address wants. The
     * route is relocated here unchanged from the top-level table; its path, title and
     * component are exactly as they were.
     */
    path: ':userId/profile',
    title: 'User Profile',
    loadComponent: () =>
      import('./user-profile/user-profile.component').then((m) => m.UserProfileComponent),
  },
  {
    /**
     * The account's password. Ungated: `UsersController:L576` gates the change on
     * `AccountOwner`, the narrowest policy the API registers and one with no client
     * expression — and the one policy where substituting portal administration would lock
     * every ordinary account holder out of their own password.
     *
     * The component declares its `userId` input as REQUIRED
     * (`user-password.component.ts:L690`), so this route may never be reached without the
     * parameter — which is why there is no sibling route at `users/password`.
     */
    path: ':userId/password',
    title: 'Manage Password',
    loadComponent: () =>
      import('./user-password/user-password.component').then((m) => m.UserPasswordComponent),
  },
];
