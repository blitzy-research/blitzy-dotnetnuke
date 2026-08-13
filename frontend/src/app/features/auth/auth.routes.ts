import type { Routes } from '@angular/router';

export const AUTH_ROUTES: Routes = [
  {
    /**
     * The empty child, so the group's own segment IS the screen's address: this resolves at `/login`
     * rather than at `/login/login`.
     */
    path: '',

    /**
     * The measured legacy heading, taken from the same resource entry the screen's own page header
     * renders — `Website/admin/Authentication/App_LocalResources/Login.ascx.resx:L150` — rather than a
     * separately invented document title, so the tab and the heading cannot drift apart.
     */
    title: 'User Log In',

    loadComponent: () => import('./login/login.component').then((m) => m.LoginComponent),
  },
];
