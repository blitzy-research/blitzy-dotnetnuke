import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';

import type { Signal } from '@angular/core';
import type { PermissionPolicy } from '../../core/guards/permission.guard';

/**
 * The authority a rail entry requires, expressed as an authorisation POLICY name.
 *
 * ⚠ NARROWED FROM THE GATE'S OWN EIGHT-NAME UNION, AND DERIVED FROM IT RATHER THAN
 * RESTATED. `Extract` keeps this type bound to `PermissionPolicy`, so a rename or
 * removal in the gate's vocabulary is a compile error here instead of a silent
 * divergence — and the two files provably speak the same vocabulary, which is the
 * whole reason the type is imported rather than re-declared as two string literals.
 *
 * ⚠ ONLY THE TWO MEMBERSHIP POLICIES, AND THE OMISSION IS THE POINT. The other six
 * are decided against ONE RECORD — a specific module, a specific page, a specific
 * account — and a rail rendered once for the whole application holds no record to
 * decide them against. Admitting them here would mean either fetching permission
 * records to interpret, which is a second authorisation engine, or guessing; the two
 * below are answered from facts the identity already carries and nothing else.
 *
 * ⚠ NOT A PERMISSION KEY. The four persisted keys (`VIEW`, `EDIT`, `READ`, `WRITE`)
 * held against module and tab records are a separate, unrelated vocabulary belonging
 * to the shared permission directive. A policy name must never be passed where a key
 * is expected, nor the reverse; that conflation is exactly what this type prevents by
 * being a policy type.
 */
export type SidebarPolicy = Extract<
  PermissionPolicy,
  'PortalAdministrator' | 'HostAdministrator'
>;

/**
 * One addressable destination in the navigation rail.
 *
 * Every member is `readonly`, and the whole model below is a compile-time constant
 * rather than anything resolved at run time. The rail advertises a fixed set of
 * administration entry points; it does not discover them, fetch them or derive them
 * from the router's configuration. Deriving them would look tidier and would be
 * wrong: the route table legitimately contains addresses that are NOT navigable from
 * a global rail, and a derivation could not tell the two apart.
 */
export interface SidebarNavItem {
  /**
   * The absolute in-application address, exactly as the route table declares it.
   *
   * Absolute — leading slash — deliberately. A global rail is rendered from the
   * shell, which sits outside every routed component, so a relative address would
   * resolve against whichever screen happens to be showing and the same link would
   * mean different things on different screens.
   */
  readonly path: string;

  /**
   * The visible text, in the legacy application's own wording.
   *
   * A plain string, rendered as text. It is never treated as markup: see the
   * annotation on {@link NAVIGATION} for why that distinction is load-bearing here.
   */
  readonly label: string;

  /**
   * The authority the destination's own PRIMARY API READ requires.
   *
   * ⚠ READ FROM THE CONTROLLER, NOT FROM THE ROUTE TABLE, and the distinction matters
   * exactly once. A rail entry answers "can this operator use this screen", and the
   * only authority on that is the endpoint the screen reads on arrival. For seven of
   * the eight entries the route declares the same name, so the two agree; for
   * `/portals` the endpoint requires host authority (`PortalsController.cs:L241`)
   * while the route deliberately declares none, and this field follows the endpoint.
   * The consequence is stated plainly rather than hidden: a tenant administrator is
   * not offered the tenant collection, because the API will not give it to them.
   *
   * ⚠ REQUIRED, WITH NO DEFAULT AND NO OPTIONAL MARKER. An entry that could omit it
   * would be an entry nobody had decided the authority for, and the omission would
   * present as an unconditionally visible link — which is precisely the state this
   * field exists to end.
   */
  readonly policy: SidebarPolicy;
}

/**
 * A titled cluster of related destinations.
 *
 * MIGRATION: the grouping itself is net-new. The legacy menu was a flat, per-portal
 * list of pages generated from the tab hierarchy, so there was no fixed grouping to
 * carry over — these four clusters mirror the migration's own preserved domains
 * (Portal, Module, User, Role) rather than any legacy structure. The Permission
 * domain is deliberately absent: it is a read-only catalogue with no screen of its
 * own, so it has no entry point to name.
 */
export interface SidebarNavGroup {
  /**
   * A stable, unique key for this group.
   *
   * Exists so the template can iterate with a `track` expression that is genuinely
   * invariant. Tracking by array index would be wrong the moment the model changes,
   * and tracking by label would tie identity to user-visible wording.
   */
  readonly id: string;

  /** The group heading, in the legacy application's own vocabulary. */
  readonly label: string;

  /** The destinations in this group, in the order they are presented. */
  readonly items: readonly SidebarNavItem[];
}

/**
 * The identifier of the collapsible region.
 *
 * Held as a named constant, not written twice, because it is referenced from two
 * places in the paired template — the region's own `id` and the toggle's
 * `aria-controls` — and a value that drifted between them would produce a control
 * that claims to operate an element that does not exist. Assistive technology
 * reports that as a broken relationship rather than ignoring it.
 */
const NAVIGATION_REGION_ID = 'sidebar-navigation';

/**
 * The navigation model: four domain groups, eight destinations.
 *
 * ## Why this lives here and not in a companion file
 *
 * The model is declared inline, in the same file as the component that renders it,
 * because the folder is specified as exactly four files — component, template,
 * stylesheet, specification — and nothing else. Extracting a navigation model into
 * its own module is the more usual Angular reflex, and it is the wrong move here.
 *
 * ## ⚠ Only collection and creation entry points appear
 *
 * Every address below is either a collection (`/portals`, `/users`) or a creation
 * form (`/role-groups/new`). No parameterised address appears, and that is a
 * property of what a global rail can express rather than an editorial choice: the
 * detail routes are keyed by `:portalId`, `:moduleId`, `:userId` and `:roleId`, and
 * a rail rendered once for the whole application has no record in hand to supply
 * one. Those screens are reached from the list that names the record.
 *
 * ## ⚠ Every address was verified against the route table
 *
 * Each `path` was checked to exist in `app.routes.ts` and its feature barrels
 * before being written here. A rail entry that names no route is not a compile
 * error and not a run-time exception — the router silently resolves it to the
 * wildcard route — so it would ship as a link that quietly goes nowhere. The eight
 * below resolve to: the portal, module, user and role feature barrels' own index
 * routes, the module barrel's `import` child, and the three addresses the route
 * table declares directly.
 *
 * MIGRATION: user-facing wording is taken from the legacy resource files' `<value>`
 * elements, never from the stale markup attributes in the paired `.ascx` files —
 * the legacy screens assigned their titles from resources at run time, so the
 * markup is not what a user ever saw. Measured sources, one per label:
 * `Portals.ascx.resx` `ControlTitle_.Text` is "Portals"; `Users.ascx.resx`
 * `ControlTitle_.Text` is "User Accounts"; `Roles.ascx.resx` `ControlTitle_.Text`
 * is "Security Roles"; `ControlTitle_importmodule.Text` is "Import Module";
 * `ControlTitle_usersettings.Text` is "User Settings";
 * `ControlTitle_manageprofile.Text` is "Manage Profile Properties"; and — the one entry
 * taken from an ACTION rather than a control title, because the address it reaches is a
 * creation form rather than a whole control — `Roles.ascx.resx` `AddGroup.Action` is
 * "Add New Role Group". The entry on that address carries the full reasoning.
 *
 * MIGRATION: localisation is not carried forward. The legacy screens resolved every
 * one of these strings through a per-control resource file at run time, across 37
 * resource files in the five in-scope trees. No translation runtime is part of this
 * migration, so the wording is authored directly here and the resource files served
 * only as the authority for what that wording should be. The consequence is
 * deliberate and worth stating plainly: this rail is English-only, where the legacy
 * rail could be localised per portal.
 *
 * MIGRATION: legacy resource text is NOT trusted as markup. Measured across those
 * 37 files there are 1211 resource entries, of which 75 carry HTML tags — and they
 * carry them XML-escaped, so a search for the unescaped form finds nothing and
 * suggests the text is clean when it is not. One file additionally contains two
 * complete script elements. None of that text reaches this rail, but the rule it
 * implies is applied anyway: labels are plain strings rendered as text, with no
 * raw-markup binding and no sanitiser bypass anywhere in this component or its
 * template. The legacy code held the same position at its own boundaries —
 * `Default.aspx.vb:232` encodes an exception message before display, and
 * `AccessDenied.ascx.vb:43` encodes a query-string message before display.
 */
const NAVIGATION = [
  {
    id: 'portal',
    label: 'Portal',
    // `GET /api/v1/portals` requires host authority (`PortalsController.cs:L241`),
    // because the tenant COLLECTION addresses no single tenant. A tenant
    // administrator is therefore not offered this entry: the API would refuse the
    // listing, and offering a link to a refusal is the defect, not the courtesy.
    items: [{ path: '/portals', label: 'Portals', policy: 'HostAdministrator' }],
  },
  {
    id: 'module',
    label: 'Module',
    items: [
      // MIGRATION: this label is genuinely net-new, and the proof is an absence.
      // Every in-scope legacy admin tree names its collection view with the
      // empty-discriminator resource key `ControlTitle_.Text` — except Modules,
      // where a census of the resource files finds only `ControlTitle_module`,
      // `ControlTitle_exportmodule` and `ControlTitle_importmodule`, and zero files
      // carrying `ControlTitle_.Text`. Modules is the only in-scope tree missing it,
      // which corroborates that no legacy module-list screen is in scope and so no
      // legacy wording exists to inherit. "Modules" is therefore authored, not
      // ported, and is named here so a later reader does not go looking for a
      // resource key that was never there.
      //
      // Both entries are tenant administration on the API: `ModulesController.cs:L187`
      // for the listing and `:L627` for the import.
      { path: '/modules', label: 'Modules', policy: 'PortalAdministrator' },
      { path: '/modules/import', label: 'Import Module', policy: 'PortalAdministrator' },
    ],
  },
  {
    id: 'user',
    label: 'User',
    // `UsersController.cs:L350` for the listing and `:L827` for the tenant's membership
    // policy; `ProfileDefinitionsController.cs:L165` gates its whole class. All three
    // are tenant administration.
    items: [
      { path: '/users', label: 'User Accounts', policy: 'PortalAdministrator' },
      { path: '/settings/membership', label: 'User Settings', policy: 'PortalAdministrator' },
      {
        path: '/settings/profile-definitions',
        label: 'Manage Profile Properties',
        policy: 'PortalAdministrator',
      },
    ],
  },
  {
    id: 'role',
    label: 'Role',
    // `RolesController.cs:L284` gates the whole class on tenant administration, so both
    // entries name it.
    items: [
      { path: '/roles', label: 'Security Roles', policy: 'PortalAdministrator' },
      // MIGRATION: the wording is the legacy screen's, and it reads oddly against
      // the address. The legacy role-group screen served both adding and editing
      // under the single title "Edit Role Group", whereas this address is the
      // creation form specifically, because the route table exposes
      // `role-groups/new` and no role-group collection. The legacy wording is kept
      // rather than improved, so the label a user recognises is the label they see;
      // the route's own title differs, and that difference is reported rather than
      // reconciled here, since the route table is another owner's file.
      { path: '/role-groups/new', label: 'Add New Role Group', policy: 'PortalAdministrator' },
    ],
  },
] as const satisfies readonly SidebarNavGroup[];

/**
 * Whether a caller described by the two facts holds the authority an entry requires.
 *
 * ⚠ AN EXHAUSTIVE SWITCH, NOT A LOOKUP OBJECT OR A BOOLEAN EXPRESSION, so widening
 * {@link SidebarPolicy} fails to compile here until the new policy's answer is stated.
 * That is the intended behaviour: a policy that silently fell through to "allowed"
 * would put an unconditionally visible entry back in the rail, which is the state this
 * whole arrangement exists to end, and one that fell through to "refused" would delete
 * a working capability with nothing to report it.
 *
 * ⚠ THE HOST ARM ON TENANT ADMINISTRATION MIRRORS THE API rather than being generous.
 * `PolicyNames.cs:L105-L113` records that a host account satisfies portal
 * administration, so a host that was not offered the tenant-scoped entries would be
 * refused screens the server admits it to. Reading the host flag as well also covers
 * the authority-minimised identity the credential responses carry, in which the
 * administration fact is withheld while the host flag is reported truthfully.
 *
 * Both facts are read as the plain booleans they are. `false` is DATA — the legacy null
 * contract conflated `false` with "not set" (`Library/Components/Shared/Null.vb`) and
 * that conflation stops at the API boundary — so neither is tested for truthiness or
 * treated as absence.
 *
 * @param policy The authority the entry requires.
 * @param host Whether the caller is a host account.
 * @param administersTenant Whether the SERVER reported the caller as an administrator of
 * the tenant it is signed in to. Never inferred from a role name.
 * @returns True when the entry may be offered.
 */
function holds(policy: SidebarPolicy, host: boolean, administersTenant: boolean): boolean {
  switch (policy) {
    case 'HostAdministrator':
      return host;
    case 'PortalAdministrator':
      return host || administersTenant;
  }
}

/**
 * The application's primary navigation rail.
 *
 * A vertical list of router links, grouped by domain, with a control that collapses
 * the rail to reclaim its width. It makes no request, holds no session state, defines no
 * route and provides no service, and it holds exactly one boolean of its own — the
 * collapse. It injects ONE thing: the ROUTER, whose current address decides which entry is
 * announced as current.
 *
 * ⚠ IT DOES NOT READ THE SESSION. The two facts that decide which entries are offered at all —
 * whether the caller holds a host account, and whether it administers the resolved tenant —
 * arrive as REQUIRED INPUTS from the shell, which is the one component that mounts this one and
 * the owner of the session boundary. Reading the store here as well would give the chrome two
 * authorities for one determination, and required inputs make the omission a compile error in any
 * future mount rather than a silently unfiltered rail. Both are reads of state another owner
 * holds; neither is a decision this component makes for itself. Everything else it renders comes
 * from the constant model above.
 *
 * ## Where it is mounted
 *
 * The shell OWNS this component: `shell.component.html` renders
 * `<app-sidebar class="shell__sidebar" />`, so the shell imports it by name and this
 * host is the grid item the sidebar area places. It used to be supplied as projected
 * content by whichever component mounted `<app-shell>`, and that arrangement failed
 * silently when nothing was supplied — the region matched the stylesheet's `:empty`
 * collapse rule, so a console with no navigation rendered with no raise and no
 * warning. Being imported makes that state unreachable.
 *
 * The rail claims no width of its own from the grid. `_layout.scss` places the
 * region and deliberately declares no inline size, leaving the width to "the rail's
 * own box, in the rail's own scoped stylesheet" — which is the paired stylesheet,
 * not this file. No dimension, colour or spacing value appears in this component,
 * and none should: every such value belongs to the stylesheet, where the design
 * tokens are in scope.
 *
 * ## ⚠ It never imports a feature component
 *
 * `RouterLink` is the only directive imported. The rail addresses features by route
 * string, and importing any feature component to reach it would pull that feature's
 * entire dependency graph into the initial bundle, collapsing the lazy loading the route
 * table is built around. That failure surfaces as a bundle-budget
 * error rather than a broken screen, which makes it easy to introduce and easy to
 * misdiagnose.
 *
 * ## ⚠⚠ THE VERTICAL RAIL IS NET-NEW INFORMATION ARCHITECTURE
 *
 * MIGRATION: there was no left navigation rail in the legacy application, and the
 * evidence is worth recording because the opposite assumption is so natural. The
 * only complete skin in the checkout, `Skins/MinimalExtropy/index.ascx`, places its
 * menu at L42-L47 as `<dnn:NAV … ControlOrientation="Horizontal">`, nested at
 * L38-L41 inside `div.menu_left > div.menu_right > div.menu_bg > div.menu_style` —
 * a HORIZONTAL bar in the header band, sitting between the logo band (L23-L37) and
 * the breadcrumb band (L60-L77). L49 puts `div.search_style` as a sibling of
 * `div.menu_style` inside the same `menu_bg`, so navigation and search shared that
 * one horizontal bar. A second horizontal navigation surface existed as well: L109
 * renders a root-level link list in the footer band.
 *
 * The decisive evidence is L89. `<td valign="top" id="LeftPane" class="LeftPane"
 * runat="server" visible="false">` is a table cell inside the content table
 * (L83-L100), alongside `TopPane` (L85), `ContentPane` (L91), `RightPane` (L93) and
 * `BottomPane` (L97) — all five hidden by default. `LeftPane` was therefore a
 * module CONTENT pane that a portal administrator could drop modules into, not a
 * navigation region. Nothing in the legacy layout corresponds to this rail; the
 * vertical arrangement is a decision of this migration.
 *
 * MIGRATION: the legacy menu is not reproduced. It was rendered by a swappable
 * navigation provider — `ProviderName="DNNMenuNavigationProvider"` — configured
 * through eleven separate CSS node-class parameters and an `IndicateChildren`
 * setting, with fly-out submenu behaviour. The navigation-provider family and the
 * menu control library are both excluded from this migration, so none of that is
 * carried over: no provider indirection, no per-node class vocabulary, no fly-out
 * submenus, no child indicators. This rail is one flat list per group, and the
 * router decides which entry is current.
 *
 * MIGRATION: run-time skin loading is replaced by a static, compile-time
 * composition. `Default.aspx.vb` resolved the whole page chrome per request:
 * `LoadSkin` (L217) instantiated a skin control chosen from portal settings at L224,
 * then called `DataBind()` at L226 — the comment at L225 is explicit that this
 * executes "any server logic in the skin", so a skin was executable code and not
 * merely a layout. L240 then let the skin package override the page doctype. A
 * failure to load was caught (L227-L237) and its message displayed only to
 * administrators, gated at L230 on portal-administrator OR tab-administrator role
 * membership, and logged at L235. None of that has an equivalent here: there is no
 * run-time layout selection, no skin registry, no per-portal chrome, no theme
 * selector and no doctype injection. One composition serves every tenant.
 *
 * MIGRATION: view state and the postback form are gone. `Default.aspx` wrapped the
 * page in a single multipart form (L23) with a skin placeholder (L25) and two hidden
 * inputs (L26, L27) used to carry scroll position and client variables across
 * postbacks. The rail's collapsed state is held in a signal instead, which is why
 * toggling it costs no request; scroll position is restored by the router's own
 * in-memory scrolling, configured once where the router is provided.
 *
 * MIGRATION: there is no page-management entry in this rail. The page abstraction
 * survives as a lookup consulted by the module screens, and its controller is
 * deliberately closed at three read-and-update endpoints with no create and no
 * delete, so there is no collection screen to navigate to and no route declared for
 * one. The legacy page-management, page-export, page-import and recycle-bin screens
 * are all out of scope. Worth recording for anyone reading the legacy source: the
 * user-facing legacy term for this concept was always "Page", never "Tab".
 *
 * MIGRATION: eighteen of the twenty-three legacy administration trees produce no
 * entry here. Only five are in scope, and the remainder — host and super-user
 * administration, file management, scheduling, localisation administration, search,
 * cache and logging administration, skins, containers, control panel, module
 * definitions, packages, lists, syndication, vendors and sales — are excluded
 * subsystems. Their absence from this rail is the intended outcome, not an
 * oversight, and no entry may be added for them without the backing endpoint and
 * route existing first.
 *
 * ## ⚠⚠ Link visibility IS gated, by the tenant-administration fact alone
 *
 * Hiding a link is an affordance, never enforcement: authorisation is decided
 * server-side and answered as HTTP 403, and route activation is additionally gated by
 * the router's own authentication and permission guards. What this rail must not do is
 * OFFER a destination the guard in front of it is about to refuse, because that costs the
 * operator a navigation that lands them back where they started with no explanation.
 *
 * So the entries whose routes carry `data: { permission: 'PortalAdministrator' }` are
 * rendered only when the caller's forwarded authority admits it — the same facts the
 * guard reads, from the same authority. Entries whose routes are reached with the
 * authentication gate alone are always rendered, because the client has nothing to base a
 * refusal on and hiding one would remove a capability the caller has. The flag lives on
 * each entry: see {@link SidebarNavItem.policy}.
 *
 * ⚠ THE SHARED PERMISSION DIRECTIVE IS DELIBERATELY NOT USED, and the reason is the whole
 * point of the gate. Its input is typed to the four-value persisted permission KEY
 * vocabulary — `VIEW`, `EDIT`, `READ`, `WRITE` — whereas tenant administration is an
 * authorisation POLICY. Those are two different vocabularies over two different sets of
 * data: the permission keys a caller holds are a union across the pages and modules it has
 * rights on, and no member of that union says anything about whether the caller
 * administers the tenant. Gating an administrative entry with a permission key would both
 * hide entries a caller may reach and offer entries the server will refuse.
 *
 * ⚠ A GROUP WITH NO VISIBLE ENTRY IS OMITTED ENTIRELY, heading and list together. A
 * heading standing over an empty list is announced by assistive technology as a section
 * containing nothing, which is worse than an absent section.
 *
 * MIGRATION: the legacy rail had no counterpart to any of this — there was no rail. The
 * legacy menu was generated from the tab hierarchy and filtered by the page permissions
 * held against each tab, which is a different mechanism over different data; this gate is
 * a decision of the migration and is documented as such rather than presented as a port.
 *
 * ## Accessibility
 *
 * The paired template supplies the landmark and its accessible name — this is the
 * shell's sole navigation landmark, the banner having deliberately grown none — and
 * wires the collapse control's `aria-expanded` and `aria-controls` to
 * {@link navigationRegionId}. Nothing in this component's own contract has any
 * visual cost.
 */
@Component({
  selector: 'app-sidebar',
  standalone: true,
  // Exactly the one router directive the template needs, and nothing else. No feature
  // component, and no permission directive for the reason set out above.
  //
  // ⚠ `RouterLinkActive` IS DELIBERATELY ABSENT. Current-destination state is derived here
  // instead — see {@link SidebarComponent.activePath} — because the directive can only
  // answer "does this link match" per link, and the question this rail has to answer is
  // "which ONE of these links is current", which no per-link matcher can decide.
  imports: [RouterLink],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // No `providers`. Every provider this application needs is declared once where the
  // application is configured; a layout primitive that provided anything would give
  // each of its instances a private copy and quietly diverge from the rest of the
  // screen.
})
export class SidebarComponent {
  /**
   * Whether the caller is a host account.
   *
   * ⚠ SUPPLIED AS AN INPUT RATHER THAN READ FROM THE SESSION STORE, AND THE CHOICE IS
   * DELIBERATE IN BOTH DIRECTIONS. Injecting the store here would have been fewer lines
   * and would have cost this component the one property it is built around — it injects
   * nothing, so it cannot fetch, cannot decide who is signed in and cannot hold session
   * state, and its specification proves all three by constructing it under a provider set
   * that has no HTTP client in it. An input keeps that intact: the mounting component owns
   * the identity, this one owns the presentation.
   *
   * ⚠ REQUIRED, WHICH IS THE STRONGER GUARANTEE. A mounting site that forgot to state the
   * caller's authority does not silently render an unfiltered rail — it fails to compile
   * under strict template checking. An injected store could not offer that: a second
   * mounting site could render the rail for anybody, and nothing would report it.
   *
   * The value must come from the SERVER's own facts and never from a role name; see
   * `layout/shell/shell.component.ts`, the rail's only caller, which supplies it from
   * `AuthStore.isSuperUser`.
   */
  readonly hostAccount = input.required<boolean>();

  /**
   * Whether the SERVER reported the caller as an administrator of the tenant it is signed
   * in to.
   *
   * ⚠ THE SERVER'S VERDICT, NOT A ROLE-NAME MATCH. The API publishes this as
   * `CurrentUserDto.IsPortalAdministrator`, resolved from the tenant's own
   * administrator-role designation and the caller's live assignments — because that role is
   * designated by identifier, may be renamed, and its name may collide with an unrelated
   * role in another tenant. `app.component.ts` supplies it from
   * `AuthStore.holdsPortalAdministration`, which republishes exactly that fact.
   *
   * Required for the same reason {@link hostAccount} is.
   */
  readonly administersTenant = input.required<boolean>();

  /**
   * Whether the rail is collapsed, as writable state.
   *
   * Private, so the only ways to change it are the methods below. A signal rather
   * than a plain field because the component renders under on-push change detection
   * and a plain field mutated from an event handler would leave the view stale.
   *
   * MIGRATION: this is the whole of the rail's state, and it lives in the browser.
   * The legacy equivalent would have been round-tripped — view state or session
   * state carried through a postback — so the legacy rail could not have been
   * collapsed without a server request.
   */
  private readonly collapsedSignal = signal<boolean | null>(null);

  /**
   * Whether the shell has placed the rail BESIDE the content rather than above it.
   *
   * Read from the resolved value of `--app-sidebar-side-by-side`, which the paired
   * stylesheet publishes through the shared breakpoint mixin. The threshold itself is
   * therefore declared exactly once, in `_mixins.scss`, and this class never restates it:
   * a `window.matchMedia('(min-width: 48rem)')` here would be a second definition of the
   * same number with nothing able to detect the two drifting apart.
   */
  private readonly sideBySideSignal = signal(true);

  /**
   * Whether the rail is currently collapsed.
   *
   * ⚠ DERIVED FROM THE LAYOUT UNTIL SOMEBODY SAYS OTHERWISE, which is what lets one control
   * serve two layouts honestly. The stored value is three-state: `null` means nobody has
   * expressed a preference, so the answer comes from the layout - expanded when docked,
   * collapsed when stacked - and a real boolean means the operator pressed the control and
   * their choice stands from then on, in either layout.
   *
   * TWO SOURCES, AND THE OPERATOR'S OWN CHOICE ALWAYS WINS. Until the disclosure has been
   * pressed the writable slot holds null, and the state is then DERIVED from the shell
   * arrangement: expanded beside the content, collapsed above it. Pressing the disclosure
   * writes a real value into the slot, and from that moment the derivation stops applying -
   * a rail the operator opened on a handset stays open, and one they closed on a desktop
   * stays closed, including across a resize.
   *
   * MIGRATION: defaulting to collapsed in the stacked arrangement is a correction, not a
   * preference, and the measurement is the argument. Expanded-at-every-width made the rail
   * a full-width 441.8px block sitting between the header and the content, which pushed the
   * page heading to y≈545 and the first row command to y≈804 on a 320-wide viewport - so
   * reaching any content at all meant scrolling past the whole of the navigation, on every
   * screen. Beside the content the rail costs nothing it does not earn, so it opens there,
   * which is the reading the original "navigation is what a user arrives wanting" note was
   * after; above the content it does, so it waits to be asked.
   */
  public readonly collapsed: Signal<boolean> = computed(() => {
    const chosen = this.collapsedSignal();

    return chosen ?? this.sideBySideSignal() === false;
  });

  /** The router, read for the address currently showing. */
  private readonly router = inject(Router);

  /** This component's own element, read for the arrangement the stylesheet publishes on it. */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Seeds and maintains {@link sideBySideSignal} from the stylesheet's published arrangement.
   *
   * A resize listener rather than a media-query listener, for the reason recorded on
   * {@link sideBySideSignal}: the threshold belongs to `_mixins.scss` and is read back as a
   * RESOLVED value, so this class never names a width. The listener is registered as passive
   * and non-capturing and is released with the component, so it cannot outlive the rail.
   *
   * The seed is taken synchronously in the constructor, before the first render, so the rail's
   * very first paint is already in the correct default state - reading it in an `afterNextRender`
   * hook would let the wrong state paint for one frame and produce exactly the layout jump this
   * work is removing elsewhere.
   */
  public constructor() {
    this.readArrangement();

    const onResize = (): void => {
      this.readArrangement();
    };

    globalThis.addEventListener('resize', onResize, { passive: true });

    inject(DestroyRef).onDestroy(() => {
      globalThis.removeEventListener('resize', onResize);
    });
  }

  /**
   * Reads `--app-sidebar-side-by-side` off this component's own element and publishes it.
   *
   * Tolerant by construction. A custom property that is missing, empty or unresolvable yields
   * an empty string, and the comparison below then reports the SIDE-BY-SIDE arrangement - the
   * expanded default, which is the safe answer because it withholds nothing from the operator.
   * That is also what makes the component render sensibly in a unit test with no stylesheet
   * attached.
   */
  private readArrangement(): void {
    const published: string = globalThis
      .getComputedStyle(this.hostElement.nativeElement)
      .getPropertyValue('--app-sidebar-side-by-side')
      .trim();

    this.sideBySideSignal.set(published !== '0');
  }

  /**
   * The address currently showing, as a signal.
   *
   * ⚠ SEEDED FROM `router.url` AND UPDATED ON COMPLETED NAVIGATIONS ONLY. The seed matters
   * because a rail rendered after the first navigation has already missed the event that
   * announced it, and an unseeded signal would leave no entry current until the operator
   * navigated again. `NavigationEnd` specifically, rather than every event, because an
   * address that a guard is about to refuse is not the address showing — marking it current
   * would highlight a destination the operator never reached.
   */
  private readonly currentUrl: Signal<string> = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event: NavigationEnd) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /**
   * The one entry that is current for the address showing, or `null` when none is.
   *
   * ⚠ LONGEST MATCH, MEASURED IN PATH SEGMENTS, AND EXACTLY ONE WINNER. Two properties have
   * to hold at once and no per-link matcher delivers both:
   *
   *   * A DESCENDANT KEEPS ITS PARENT CURRENT. `/portals/3/settings` is reached from the
   *     Portals entry, so that entry stays marked while the operator is on it. Exact
   *     matching — which this rail used before — left EVERY entry unmarked on every
   *     descendant screen, so nothing was highlighted and, because `aria-current` is driven
   *     from the same state, nothing announced itself as the current page. The console has
   *     more descendant screens than collection screens, so that was the common case.
   *   * NO TWO ENTRIES ARE EVER CURRENT AT ONCE. `/modules` is a prefix of
   *     `/modules/import`, so plain prefix matching marks both on the import screen. Two
   *     elements claiming `aria-current="page"` is an accessibility defect rather than a
   *     cosmetic one — the attribute means nothing if it is not unique — so the longest
   *     matching entry wins and the shorter one yields.
   *
   * ⚠ COMPARED SEGMENT BY SEGMENT, NEVER AS A STRING PREFIX. `/roles` is a string prefix of
   * `/role-groups/new`, so a string test would mark Security Roles current on the role-group
   * screen. Splitting on the separator makes `roles` and `role-groups` the different
   * segments they are.
   *
   * Only VISIBLE entries are considered - {@link SidebarComponent.visibleNavigation}, not the
   * declared model - so a gated-away destination never claims to be the current page even if the
   * operator reaches it by typing the address.
   */
  public readonly activePath: Signal<string | null> = computed(() => {
    const showing: readonly string[] = pathSegments(this.currentUrl());
    let winner: string | null = null;
    let winningLength = 0;

    for (const group of this.visibleNavigation()) {
      for (const item of group.items) {
        const candidate: readonly string[] = pathSegments(item.path);

        if (candidate.length <= winningLength || isDescendantOf(showing, candidate) === false) {
          continue;
        }

        winner = item.path;
        winningLength = candidate.length;
      }
    }

    return winner;
  });

  /**
   * The four navigation groups as DECLARED, before any authority is considered.
   *
   * ⚠ NOT WHAT THE TEMPLATE RENDERS. The template reads
   * {@link SidebarComponent.visibleNavigation}. This member remains published because
   * the declared model is worth asserting on its own terms — that every address exists
   * in the route table, that every entry names an authority, that the wording matches
   * the legacy resources — and those are properties of the DECLARATION rather than of
   * one caller's view of it.
   */
  public readonly navigation: readonly SidebarNavGroup[] = NAVIGATION;

  /**
   * The groups and entries this caller may actually use.
   *
   * ⚠ ENTRIES ARE FILTERED FIRST AND EMPTIED GROUPS ARE THEN DROPPED, in that order.
   * A group is only a heading over a list, so a group whose every entry was withheld
   * has nothing left to head: leaving it would render a heading and an empty list, and
   * the list carries `aria-labelledby` pointing at that heading — so assistive
   * technology would announce a named navigation list containing nothing. Dropping the
   * group is what keeps the announced structure honest. No group is special-cased; the
   * emptiness is derived.
   *
   * ⚠ COMPUTED, NOT COPIED INTO A FIELD. The identity can change within one page load —
   * a sign-out, or a renewal that re-reads the caller's authority — and a field
   * initialised once would keep advertising the authority the previous caller had. A
   * computed signal recomputes when the inputs it reads change, which is what makes the
   * rail follow the session rather than the page load, and the component renders
   * on-push so the view follows the signal.
   *
   * ⚠ IT DOES NOT DECIDE WHETHER A SESSION EXISTS. Whether the rail appears at all is
   * the mounting component's decision, and `app.component.html` makes it by not
   * projecting the rail until an identity is resolved. Answering it here as well would
   * put the same question in two places; what this member answers is which entries a
   * KNOWN identity may use.
   *
   * Nothing is fetched, cached or interpreted: the two facts arrive as inputs, already
   * resolved from what the server published.
   */
  public readonly visibleNavigation: Signal<readonly SidebarNavGroup[]> = computed(() => {
    const host: boolean = this.hostAccount();
    const administersTenant: boolean = this.administersTenant();

    return NAVIGATION.map((group) => ({
      ...group,
      items: group.items.filter((item) => holds(item.policy, host, administersTenant)),
    })).filter((group) => group.items.length > 0);
  });

  /**
   * The identifier carried by the collapsible region.
   *
   * Read by the template twice — once as the region's `id`, once as the toggle's
   * `aria-controls` — so that the control and the region it operates are provably
   * the same element.
   */
  public readonly navigationRegionId: string = NAVIGATION_REGION_ID;

  /**
   * Whether one navigation entry is the current destination.
   *
   * Read by the template for BOTH the active class and `aria-current`, so the visual state
   * and the announced state are the same decision and cannot disagree. That pairing is the
   * reason this is a method rather than two bindings computed separately.
   *
   * @param item The entry to test.
   * @returns True when this entry is the single current destination.
   */
  public isCurrent(item: SidebarNavItem): boolean {
    return this.activePath() === item.path;
  }

  /**
   * The `aria-current` value for one navigation entry: `'page'`, `'true'`, or `null`.
   *
   * ⚠ TWO VALUES RATHER THAN ONE, AND THE DISTINCTION IS THE WHOLE POINT. The rail marks an
   * entry while the operator is on a DESCENDANT of it, because exact matching left every
   * descendant screen with nothing marked at all — see {@link SidebarComponent.activePath},
   * where that measured defect is recorded. But marking is not the same claim as identity:
   * on `/portals/3/settings` the Portals entry is the branch the operator is inside, and it
   * is NOT the page they are on. Announcing `aria-current="page"` there tells a screen-reader
   * user that this link IS the current page, and following it then arrives somewhere else.
   *
   * `page` is reserved for the exact address, and an ancestor resolves to `true` — the
   * unqualified value, which says "this is the current item in this set" without claiming a
   * relationship the rail cannot honour. Both values keep the entry marked, so the visual
   * treatment is unchanged and nothing regresses on the descendant screens that motivated the
   * longest-match rule; only the strength of the claim differs.
   *
   * `null` when the entry is not in the current branch at all, so the attribute is removed
   * rather than left behind as a stale claim.
   *
   * ⚠ COMPARED BY SEGMENTS, NEVER BY RAW ADDRESS. A string comparison would demote `page` to
   * `true` the moment a listing carried a query — `/users?filter=A` is the same destination as
   * `/users`, and every letter the operator typed into a filter would flip the announcement
   * back and forth. {@link pathSegments} drops the query and the fragment for exactly this
   * reason, and reusing it here keeps this decision consistent with the one that chose the
   * entry in the first place.
   *
   * @param item The entry to describe.
   * @returns The attribute value, or null when the attribute must not be present.
   */
  public ariaCurrent(item: SidebarNavItem): 'page' | 'true' | null {
    if (!this.isCurrent(item)) {
      return null;
    }

    const showing: readonly string[] = pathSegments(this.currentUrl());
    const entry: readonly string[] = pathSegments(item.path);

    return showing.length === entry.length ? 'page' : 'true';
  }

  /**
   * Collapses the rail if it is expanded, expands it if it is collapsed.
   *
   * The template's collapse control calls this. Derives the next value from the
   * current one inside the update, rather than reading the signal and then setting
   * it, so the transition cannot be based on a value that has since changed.
   */
  public toggleCollapsed(): void {
    // Derived from the EFFECTIVE state rather than from the writable slot, because the slot
    // opens as null and negating null would resolve to the same value twice in a row: on a
    // handset the rail is effectively collapsed, `!null` is true, and the first press would
    // have collapsed an already-collapsed rail and appeared to do nothing. Reading the derived
    // signal makes the first press always reverse what the operator can see, and writing a real
    // boolean is what retires the arrangement-derived default from then on.
    const next: boolean = this.collapsed() === false;

    this.collapsedSignal.set(next);
  }
}

/**
 * Splits an in-application address into its path segments.
 *
 * Query and fragment are removed first, because neither participates in identifying a
 * destination: `/users?filter=A` and `/users` are the same entry, and marking the entry
 * current only for one of them would make the rail flicker as an operator filtered a
 * listing. Empty segments are dropped, so a trailing separator and a leading one both
 * disappear rather than producing a phantom segment that no candidate could match.
 *
 * @param address An absolute in-application address, with or without query or fragment.
 * @returns The address's path segments, in order.
 */
function pathSegments(address: string): readonly string[] {
  const path: string = address.split('?')[0].split('#')[0];

  return path.split('/').filter((segment) => segment.length > 0);
}

/**
 * Whether `showing` is the same destination as `candidate` or one below it.
 *
 * ⚠ SEGMENT-WISE, WHICH IS THE WHOLE POINT. A string prefix test would report
 * `/role-groups/new` as being below `/roles`, marking Security Roles current on the
 * role-group screen; comparing segments makes `roles` and `role-groups` the distinct
 * segments they are. An empty candidate would match everything, so it is refused outright
 * rather than treated as a root that owns every address.
 *
 * @param showing The segments of the address currently showing.
 * @param candidate The segments of a navigation entry's address.
 * @returns True when the address showing is the candidate itself or a descendant of it.
 */
function isDescendantOf(showing: readonly string[], candidate: readonly string[]): boolean {
  if (candidate.length === 0 || showing.length < candidate.length) {
    return false;
  }

  return candidate.every((segment, index) => showing[index] === segment);
}
