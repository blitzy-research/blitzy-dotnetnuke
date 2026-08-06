import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The security role administration feature's route barrel.
 *
 * `app.routes.ts` mounts this array beneath the `roles` path with `loadChildren` and
 * attaches `authGuard` to that parent, so every child inherits the session requirement.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING. `new` MUST stay above `:roleId`, which matches any
 * single segment and would otherwise take the literal `new` as a role key.
 *
 * HOW EACH ROUTE'S `permission` WAS CHOSEN — AND WHY EVERY ONE IS THE SAME
 * ----------------------------------------------------------------------
 * `RolesController` declares its policy ONCE, on the class (`:L284`), and no action
 * overrides it. Every role read, write, delete and membership change is therefore gated
 * on `PortalAdministrator`, so all four routes below declare it and none of them is
 * approximating anything: this group is the one place in the table where the route data
 * and the endpoint attributes agree by construction rather than by careful matching.
 *
 * The uniformity is a property of the domain rather than an oversight in the controller.
 * A role is a tenant-scoped object with no per-record grant of its own — there is no
 * "role view" or "role edit" permission in the schema the way there is for a module or a
 * tab — so the only question that can be asked is whether the caller administers the
 * tenant, and the API resolves the tenant from the request rather than from the route.
 * That is exactly the policy `permission.guard.ts:L253` reports as needing no scope, so
 * every route here is answerable without carrying an identifier for the gate's benefit.
 *
 * ⚠ THE ROLE GROUP FORM IS NOT DECLARED IN THIS BARREL, DELIBERATELY. `role-group-form`
 * lives in this feature folder, but its address is `/role-groups/new` — not
 * `/roles/...` — because a role group is a sibling aggregate with its own controller
 * (`RoleGroupsController`, class-gated on the same policy at `:L173`) rather than a child
 * of a role. Its route is therefore declared as a top-level leaf in `app.routes.ts`, and
 * the address the existing screens already navigate to is the reason the distinction
 * matters rather than a preference: `role-list.component.ts` holds it as
 * `ADD_ROLE_GROUP_LINK = '/role-groups/new'`, so moving it under `roles` would break a
 * link that already exists.
 *
 * MIGRATION: the four routes decompose three legacy controls whose boundaries did not
 * line up with their addresses. `Website/admin/Security/Roles.ascx.vb` listed roles AND
 * carried the role-group affordances, `EditRoles.ascx.vb` both created and edited a role
 * from one control depending on the query string it was reached with, and
 * `SecurityRoles.ascx.vb` managed membership. Splitting the second into a create route and
 * an edit route is what lets each address describe one intention, and is why the same
 * component serves both with two measured headings — `Add New Role` and
 * `Edit Security Roles` at `role-form.component.ts:L358` and `:L369`.
 */
export const ROLE_ROUTES: Routes = [
  {
    /**
     * The role listing at `/roles`. Reached by `role-list.component.ts`'s own
     * `ROLES_PATH = '/roles'` after a mutation settles, and by `user-form` and
     * `role-assignment` when they navigate back.
     */
    path: '',
    title: 'Security Roles',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-list/role-list.component').then((m) => m.RoleListComponent),
  },
  {
    /**
     * Role creation, at the address `role-list.component.ts` already holds as
     * `ADD_ROLE_LINK = '/roles/new'`.
     *
     * The component's `roleId` input is optional (`role-form.component.ts:L826`), so the
     * absent parameter is what selects the create heading and the create submit path. Its
     * three other inputs — `administratorRoleId`, `registeredRoleId` and
     * `paymentProcessorConfigured` — all declare defaults and are deliberately NOT
     * supplied as route data: they are tenant facts the screen resolves from its store,
     * and pinning them in the route table would freeze a tenant's configuration into the
     * application's routing.
     */
    path: 'new',
    title: 'Add New Role',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-form/role-form.component').then((m) => m.RoleFormComponent),
  },
  {
    /**
     * Role editing.
     *
     * No numeric matcher and no coercion: role keys are `IDENTITY(0, 1)` in the baseline
     * schema (`01.00.00.SqlDataProvider:L114`), so `0` is a real role — it is the
     * Administrators role in a freshly installed tenant — and treating it as absence would
     * make the single most important role in the product unreachable.
     */
    path: ':roleId',
    title: 'Edit Security Roles',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-form/role-form.component').then((m) => m.RoleFormComponent),
  },
  {
    /**
     * Role membership. `RolesController` exposes the members collection at
     * `roles/{roleId}/users` (`:L777` to read, `:L952` to add, `:L1012` to remove), and
     * this address mirrors that nesting segment for segment.
     *
     * The trailing segment is spelled exactly as `role-list.component.ts` holds it in
     * `ROLE_MEMBERS_SEGMENT = 'users'`, so the link that screen builds resolves here.
     */
    path: ':roleId/users',
    title: 'Manage Users in Role',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-assignment/role-assignment.component').then((m) => m.RoleAssignmentComponent),
  },
];
