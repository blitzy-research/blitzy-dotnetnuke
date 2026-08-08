import type { Routes } from '@angular/router';

import { permissionGuard } from '../../core/guards/permission.guard';

/**
 * The security role administration feature's route barrel.
 *
 * `app.routes.ts:L201-L204` mounts this array beneath the `roles` path with
 * `loadChildren` and attaches the session gate to that parent, so every child below
 * inherits it and none of them re-declares it. Each path here is therefore
 * RELATIVE to `roles` — re-prefixing one would address `/roles/roles`, and every link in
 * the shipped screens would resolve to nothing.
 *
 * ⚠ DECLARATION ORDER IS LOAD-BEARING. `new` MUST stay above `:roleId`, which matches any
 * single segment and would otherwise take the literal `new` as a role key: the form would
 * open in edit mode and try to load a role whose identifier is the string `"new"`. The
 * build would succeed and the screen would render, so nothing but the empty form would
 * say why. `app.routes.spec.ts:L313` asserts the ordering, which is what turns a future
 * reordering into a failing suite rather than a confused caller.
 *
 * WHY ALL FOUR ROUTES DECLARE THE SAME POLICY, THE LISTING INCLUDED
 * ----------------------------------------------------------------
 * `RolesController` declares its policy ONCE, on the CLASS, and no action overrides it —
 * so tenant administration gates the LISTING READ exactly as tightly as it gates the
 * writes. Every one of the four addresses below therefore declares that one name, and the
 * uniformity is read from the controller rather than chosen here.
 *
 * The uniformity is a property of the domain rather than a gap in the controller: a role is
 * a tenant-scoped object with no per-record grant of its own, so unlike a module or a tab
 * there is no "role edit" permission to ask about — only whether the caller administers the
 * tenant. That is precisely the policy `permission.guard.ts` resolves without needing a
 * subject, which is why no route here carries an identifier for the gate's benefit; the two
 * that do carry `:roleId` carry it for the screen.
 *
 * MIGRATION: a previous revision left the LISTING ungated and gave three reasons, and all
 * three are recorded here with what was wrong with them, because each looked sound in
 * isolation:
 *
 *   * "It preserves the legacy reachability." It did not. `Roles.ascx.vb:L321-L323` refuses
 *     a caller who is not in the administrator role before the grid is ever bound, so the
 *     legacy listing was NOT served to any administration-tab visitor. Gating it is parity;
 *     leaving it open was the divergence.
 *   * "It matches the sibling barrels." Consistency with a sibling is not evidence about
 *     THIS controller. The sibling barrels' listings sit behind endpoints with their own,
 *     different policies, and the portal listing in particular remains ungated for a reason
 *     that is stated on that route and does not transfer here.
 *   * "It costs nothing, because the gate is advisory." This is the one that mattered, and
 *     it had the cost backwards. Advisory is precisely why an ungated listing is wrong: a
 *     non-administrator was admitted to a screen whose FIRST request is certain to be
 *     refused, so the screen rendered its chrome, its column headings and its empty grid,
 *     and then a 403 banner — a worse outcome than never offering the navigation. Nothing
 *     was disclosed, and nothing useful was offered either.
 *
 * ⚠ THE GATE REMAINS ADVISORY AND THE SERVER REMAINS THE AUTHORITY. `RolesController`
 * answers 403 on its own account whatever this table says. What the declaration buys is
 * that an operator is not walked into a screen the client can already tell is unusable.
 *
 * ⚠ THE ROLE GROUP FORM IS NOT DECLARED IN THIS BARREL, DELIBERATELY. `role-group-form`
 * lives in this feature folder, but it is NOT addressed beneath `roles` — because a role
 * group is a sibling aggregate with its own controller (`RoleGroupsController`,
 * class-gated on the same policy) rather than a child of a role. Its route is declared as
 * a top-level leaf in `app.routes.ts:L219-L227`, where it also carries the session gate
 * because a standalone leaf has no parent to inherit one from, and the address itself is
 * held once by `role-list.component.ts:L144` as `ADD_ROLE_GROUP_LINK`. That the shipped
 * listing already navigates there is what makes the distinction binding rather than
 * stylistic: declaring the form here would nest its address under this mount, and the
 * existing link would resolve to nothing. Neither that component nor its address is
 * named anywhere in this file, and that absence is the contract — which is also why the
 * path literal is not restated here, since a duplicated address in a comment can go
 * stale without anything noticing.
 *
 * MIGRATION: routing moves from the SERVER to the BROWSER. The legacy feature had no
 * client-side router and no route table of any kind. Every screen was a user control
 * reached by a full page request to `Website/Default.aspx`, and navigation between them
 * was performed imperatively — `Response.Redirect(NavigateURL(...))` to leave a screen,
 * and DNN's `EditUrl(...)` helper to build an address for a link, which encoded the
 * target control and its arguments as QUERY STRING state rather than as a path.
 * `Roles.ascx.vb:L84` is the canonical shape:
 * `lnkEditGroup.NavigateUrl = EditUrl("RoleGroupId", RoleGroupId.ToString, "EditGroup")`.
 * The four declarations below replace that with addresses that describe themselves, and
 * the set of reachable role screens becomes readable from this one file instead of having
 * to be reconstructed by reading every control's load handler.
 *
 * MIGRATION — LEGACY DEFECT D-R4, CARRIED HERE BECAUSE IT IS A ROUTING DEFECT. The legacy
 * wrote and read the role-group key with different capitalisation: `Roles.ascx.vb:L84`
 * WRITES the query key `"RoleGroupId"` with a lower-case `d`, while
 * `EditGroups.ascx.vb:L61-L62` READS `Request.QueryString("RoleGroupID")` with a capital
 * `D`. The screen worked anyway, but only because ASP.NET's `QueryString` collection
 * resolves its keys CASE-INSENSITIVELY — the mismatch was latent, not benign, and any
 * reader comparing the two files would have had to know that to conclude the code was
 * correct. ANGULAR ROUTE PARAMETERS ARE CASE-SENSITIVE, and `withComponentInputBinding()`
 * matches a parameter to a component input BY NAME, so the same mismatch here would bind
 * nothing at all: the input would silently keep its default, the screen would render
 * empty, and the build would report success. The target therefore standardises on a
 * single spelling with one lower-case `d` — `roleId` here and `roleGroupId` for the group
 * — and applies it even where the legacy was already self-consistent. The role key was:
 * `Roles.ascx.vb:L216` writes `EditUrl("RoleID", "KEYFIELD", "Edit")` and
 * `EditRoles.ascx.vb:L100-L101` reads `"RoleID"`, both capitalised. Renaming that too is
 * what leaves the convention uniform rather than half-inherited.
 *
 * MIGRATION: the legacy feature presented FIVE role screens; this barrel declares FOUR.
 * The fifth is the role-group form, mounted at the application level for the reason set
 * out above, so the count differs by placement rather than by any dropped capability.
 * Underneath, the four decompose only THREE legacy controls, whose boundaries did not line
 * up with their addresses: `Roles.ascx.vb` listed the roles AND carried the role-group
 * affordances, `EditRoles.ascx.vb` both created and edited a role from a single control
 * depending on the query string it was reached with, and `SecurityRoles.ascx.vb` managed
 * membership. Splitting the second into a create address and an edit address is what lets
 * each address describe one intention while still being served by one component.
 *
 * MIGRATION: ROLE GROUP EDIT AND DELETE HAVE NO ROUTE, WHICH MATCHES THE LEGACY RATHER
 * THAN REDUCING IT. Both were inline affordances on the roles listing, not screens of
 * their own: `roles.ascx` L5-L16 declares a single `trGroups` row holding the
 * `cboRoleGroups` selector alongside `lnkEditGroup` (L10) and the `cmdDelete` image button
 * (L13), so a group was edited and deleted from the list without ever leaving it. The
 * listing component keeps that arrangement, which is why `role-group-form` is
 * CREATE-ONLY and why no `:roleGroupId` parameter appears anywhere in this table.
 *
 * MIGRATION: the wording of every `title` below is the measured value of the
 * corresponding legacy resource entry under
 * `Website/admin/Security/App_LocalResources/`, so a caller sees the heading they already
 * know. The resource files are read for WORDING ONLY — the localisation mechanism itself
 * is not ported, there is no translation runtime in this workspace, and these strings are
 * authored directly.
 */
export const ROLE_ROUTES: Routes = [
  {
    /**
     * The role listing at `/roles`. Reached by `role-list.component.ts:L135`'s own
     * `ROLES_PATH = '/roles'` once a mutation settles, and by `user-form` and
     * `role-assignment` when they navigate back out.
     *
     * Gated on tenant administration, like every other address in this barrel, because
     * `RolesController`'s class-level declaration covers `GET roles` as tightly as it
     * covers the writes. The listing is the one screen whose FIRST act is a request, so
     * admitting a caller the server will refuse produced a fully rendered screen that
     * could never show a row.
     *
     * `data.permission` is declared TOGETHER with the gate and never separately —
     * `app.routes.spec.ts` asserts that a declared policy and an attached gate always
     * agree, so half of this pair would be a failing spec rather than a subtle bug.
     *
     * MIGRATION: the legacy control performed this very check imperatively.
     * `Roles.ascx.vb:L321-L323` redirects a caller who is neither the account under
     * inspection nor in the administrator role to the access-denied screen, before the grid
     * is bound. The declaration below is that test moved to the route table; leaving the
     * address ungated, as a previous revision did, was the divergence rather than the
     * parity.
     *
     * MIGRATION: `Roles.ascx.resx` → `ControlTitle_.Text` = `Security Roles`. The empty
     * suffix on that key is not a typo: the legacy control took its title from
     * `ControlTitle_<mode>` and the listing was the modeless default.
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
     * Role creation, at the address `role-list.component.ts:L141` already holds as
     * `ADD_ROLE_LINK = '/roles/new'`.
     *
     * ⚠ This literal MUST remain above `:roleId` — see the ordering note at the head of
     * this file.
     *
     * Served by the same component as the edit address. Its `roleId` input is optional
     * (`role-form.component.ts:L827`), so the ABSENT parameter is what selects the
     * create heading and the create submit path; mode is derived from the parameter's
     * presence, never from its value, because a role key of `0` is a real role. The
     * component's other inputs — `administratorRoleId`, `registeredRoleId` and
     * `paymentProcessorConfigured` — all declare defaults and are deliberately NOT
     * supplied as route data: they are tenant facts the screen resolves from its store,
     * and pinning them here would freeze one tenant's configuration into the
     * application's routing.
     *
     * MIGRATION: `Roles.ascx.resx` → `AddContent.Action` = `Add New Role`. Taken from
     * the listing's own add affordance because the legacy had no separate create control
     * to carry a title of its own — `EditRoles.ascx` served both jobs.
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
     * No numeric matcher and no coercion of `:roleId`. Role keys are `IDENTITY(0, 1)` in
     * the baseline schema, so `0` is a REAL role — it is the Administrators role in a
     * freshly installed tenant — and the legacy null contract at
     * `Library/Components/Shared/Null.vb` returns `-1` for a missing integer, so absence
     * and identity are not distinguishable by magnitude anywhere in this data. Treating
     * a falsy key as "no role" would make the single most important role in the product
     * unreachable, which is why presence is decided by the component from the
     * parameter's existence instead.
     *
     * MIGRATION: `EditRoles.ascx.resx` → `ControlTitle_edit.Text` =
     * `Edit Security Roles`.
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
     * `roles/{roleId}/users`, and this address mirrors that nesting segment for segment.
     * The trailing segment is spelled exactly as `role-list.component.ts:L138` holds it
     * in `ROLE_MEMBERS_SEGMENT = 'users'`, so the link that screen builds resolves here.
     *
     * MIGRATION: `SecurityRoles.ascx.resx` → `ControlTitle_user roles.Text` =
     * `User Roles`. That key really does contain a space, and it is the STATIC control
     * title, which is what a document title has to be. The screen's other measured
     * heading, `RoleTitle.Text` = `Manage Users in Role: {0}`, is PARAMETERISED by the
     * role's name and so cannot be honoured by a route `title`, which is resolved before
     * the role is loaded and takes no arguments. Amputating its `{0}` to fit would ship a
     * truncated sentence and duplicate a string the component already owns — the
     * parameterised heading belongs to `role-assignment.component.ts:L170`, which
     * interpolates the role name properly and falls back on its own. The route
     * contributes the static title the legacy had for exactly this purpose; the
     * component contributes the specific one.
     */
    path: ':roleId/users',
    title: 'User Roles',
    canActivate: [permissionGuard],
    data: { permission: 'PortalAdministrator' },
    loadComponent: () =>
      import('./role-assignment/role-assignment.component').then((m) => m.RoleAssignmentComponent),
  },
];
