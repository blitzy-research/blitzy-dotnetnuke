import type { Routes } from '@angular/router';

/**
 * The sign-in feature's route barrel.
 *
 * `app.routes.ts` mounts this array beneath the `login` path with `loadChildren`, so
 * the sign-in screen and everything it pulls in — the typed credential form, the
 * authentication store's write path — sit in a lazy chunk that a caller who is
 * already signed in never downloads.
 *
 * ⚠ NO ROUTE GATE IS ATTACHED HERE, AND THAT IS A HARD INVARIANT RATHER THAN AN
 * OVERSIGHT. `core/guards/auth.guard.ts:L49-L56` states it as a standing constraint on
 * the route table: the gate must never be attached to the sign-in route, because a gate
 * that refuses the very screen it redirects to cannot be satisfied by any caller and
 * the application never starts. The guard hardens its own half of that invariant —
 * `isSignInRoute` at L136 admits the sign-in address unconditionally, so a mistake
 * degrades to "the sign-in screen is reachable" rather than to a redirect cycle — but
 * the route table must not rely on that safety net. The same applies to
 * `core/guards/permission.guard.ts`, which holds this very address as its own
 * `SIGN_IN_ROUTE` constant at L162 and sends an unauthenticated caller to it at L622.
 *
 * WHY A BARREL FOR ONE SCREEN. The other four feature groups each hold several screens
 * and the barrel earns its place by grouping them. This one holds a single screen, and
 * it exists anyway for two reasons that are about the ROUTE rather than the count.
 * First, `login` is the one address the two route gates redirect to by literal path, so
 * having it declared in exactly one place — beneath one parent segment, in one file —
 * is what keeps that literal answerable. Second, the sign-in feature is the natural
 * home for the password-recovery screen the legacy console reached from the same form
 * (`Website/admin/Security/SendPassword.ascx.vb`), and a group already declared takes a
 * sibling without touching the top-level table.
 *
 * ONE ROUTE, SO THE ORDERING CONSTRAINT IS VACUOUS HERE. `app.routes.ts:L107-L109` records
 * that each barrel states the declaration-order rule at its head, and this is that
 * statement: the array holds a single empty path, so no parameter segment exists to
 * swallow a literal one and there is no order to get wrong. It is written down because the
 * rule goes live the instant the sibling above is added — a literal segment would then have
 * to be declared ABOVE any parameter route, and this entry would still have to keep the
 * empty path rather than be re-prefixed with `login`.
 *
 * MIGRATION: signing in moves from a MODULE ON A PAGE to a route of its own. The legacy
 * arrangement had no sign-in address at all. The credential form was a user control,
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx`, injected into whatever
 * page a portal administrator had assigned the authentication module to, so its address
 * was a tenant-configurable `TabId` rather than a fixed path and differed between
 * portals. Here it is a fixed, tenant-independent path, which is what allows the two
 * route gates to name it as a constant.
 */
export const AUTH_ROUTES: Routes = [
  {
    /**
     * The empty child, so the group's own segment IS the screen's address: this
     * resolves at `/login` rather than at `/login/login`.
     */
    path: '',

    /**
     * The measured legacy heading, taken from the same resource entry the screen's own
     * page header renders — `Website/admin/Authentication/App_LocalResources/Login.ascx.resx:L150`
     * — rather than a separately invented document title, so the tab and the heading
     * cannot drift apart.
     */
    title: 'User Log In',

    loadComponent: () => import('./login/login.component').then((m) => m.LoginComponent),
  },
];
