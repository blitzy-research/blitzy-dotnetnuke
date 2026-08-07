import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

/**
 * One addressable destination in the navigation rail.
 *
 * Both members are `readonly`, and the whole model below is a compile-time constant
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
 * `ControlTitle_manageprofile.Text` is "Manage Profile Properties";
 * `ControlTitle_editgroup.Text` is "Edit Role Group".
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
    items: [{ path: '/portals', label: 'Portals' }],
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
      { path: '/modules', label: 'Modules' },
      { path: '/modules/import', label: 'Import Module' },
    ],
  },
  {
    id: 'user',
    label: 'User',
    items: [
      { path: '/users', label: 'User Accounts' },
      { path: '/settings/membership', label: 'User Settings' },
      { path: '/settings/profile-definitions', label: 'Manage Profile Properties' },
    ],
  },
  {
    id: 'role',
    label: 'Role',
    items: [
      { path: '/roles', label: 'Security Roles' },
      // MIGRATION: the wording is the legacy screen's, and it reads oddly against
      // the address. The legacy role-group screen served both adding and editing
      // under the single title "Edit Role Group", whereas this address is the
      // creation form specifically, because the route table exposes
      // `role-groups/new` and no role-group collection. The legacy wording is kept
      // rather than improved, so the label a user recognises is the label they see;
      // the route's own title differs, and that difference is reported rather than
      // reconciled here, since the route table is another owner's file.
      { path: '/role-groups/new', label: 'Edit Role Group' },
    ],
  },
] as const satisfies readonly SidebarNavGroup[];

/**
 * The application's primary navigation rail.
 *
 * A vertical list of router links, grouped by domain, with a control that collapses
 * the rail to reclaim its width. Presentational by construction: it injects nothing,
 * makes no request, holds no session state, defines no route and provides no
 * service. Everything it renders comes from the constant model above.
 *
 * ## Where it is mounted
 *
 * The shell publishes its navigation region as a projection slot —
 * `shell.component.html` renders `<div class="shell__sidebar"><ng-content /></div>` —
 * so this component is supplied as projected content by whichever component mounts
 * `<app-shell>`, rather than being imported by the shell itself. That arrangement is
 * the shell's, not this component's, and it is what keeps the shell free of any
 * knowledge of what navigation exists.
 *
 * The rail claims no width of its own from the grid. `_layout.scss` places the
 * region and deliberately declares no inline size, leaving the width to "the
 * projected content's own box, in the scoped stylesheet of whichever component
 * supplies it" — which is the paired stylesheet, not this file. No dimension,
 * colour or spacing value appears in this component, and none should: every such
 * value belongs to the stylesheet, where the design tokens are in scope.
 *
 * ## ⚠ It never imports a feature component
 *
 * Only `RouterLink` and `RouterLinkActive` are imported. The rail addresses features
 * by route string, and importing any feature component to reach it would pull that
 * feature's entire dependency graph into the initial bundle, collapsing the lazy
 * loading the route table is built around. That failure surfaces as a bundle-budget
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
 * ## ⚠⚠ Permission-based link visibility is deliberately NOT applied here
 *
 * MIGRATION: hiding a link is an affordance, never enforcement, and this rail
 * currently offers neither. Authorisation is decided server-side and answered as
 * HTTP 403; on the client, route activation is gated by the router's own
 * authentication and permission guards. The rail shows the entry points and lets
 * those two authorities refuse — which is the safer default, because a rail that
 * hid an entry the guard would have allowed would silently remove a capability,
 * whereas showing one the guard refuses costs a redirect.
 *
 * The shared permission directive was evaluated for group-level gating and
 * deliberately not adopted, because its input is typed to the four-value permission
 * key vocabulary (`VIEW`, `EDIT`, `READ`, `WRITE`) rather than to authorisation
 * policy names. Those are two different vocabularies, and the directive's own
 * documentation identifies conflating them as precisely the confusion its type is
 * there to prevent. Passing a policy name would not compile under strict template
 * checking, and were it to reach run time the directive fails closed on an
 * unrecognised value — so every group would disappear permanently and the rail
 * would render empty. The gap is reported rather than papered over: gating this rail
 * needs either a policy-typed input on that directive or a resolved permission-key
 * projection to gate against, and neither exists yet.
 *
 * Note also that the two module entries are declared in the route table under the
 * portal-administration policy rather than a module-scoped one, so a naive
 * module-scoped gate would not have matched the guard's own decision either.
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
  // Exactly the two router directives the template needs, and nothing else. No
  // feature component, and no permission directive for the reason set out above.
  imports: [RouterLink, RouterLinkActive],
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
  private readonly collapsedSignal = signal(false);

  /**
   * Whether the rail is currently collapsed.
   *
   * Exposed read-only, so the template and the paired specification can observe the
   * state while neither can assign to it. {@link toggleCollapsed} is the only way to
   * change it, which keeps every transition expressible in one place.
   *
   * Starts expanded. An administration console's navigation is the thing a user
   * arrives wanting, so the rail earns its width until they say otherwise.
   */
  public readonly collapsed = this.collapsedSignal.asReadonly();

  /** The four navigation groups, in presentation order. */
  public readonly navigation: readonly SidebarNavGroup[] = NAVIGATION;

  /**
   * The identifier carried by the collapsible region.
   *
   * Read by the template twice — once as the region's `id`, once as the toggle's
   * `aria-controls` — so that the control and the region it operates are provably
   * the same element.
   */
  public readonly navigationRegionId: string = NAVIGATION_REGION_ID;

  /**
   * Link-activity matching options for the template's `routerLinkActive` bindings.
   *
   * ⚠ EXACT MATCHING IS REQUIRED, NOT PREFERRED, AND ONE PAIR OF ADDRESSES PROVES
   * IT. `routerLinkActive` matches by prefix unless told otherwise, and `/modules`
   * is a prefix of `/modules/import`. On the import screen, prefix matching would
   * mark BOTH entries active — two highlighted links for one location, and, because
   * the template drives `aria-current="page"` from the same state, two elements
   * claiming to be the current page. `aria-current="page"` is meaningless if it is
   * not unique, so this is an accessibility defect and not only a cosmetic one.
   *
   * Held as a single stable object rather than written as a literal in the template,
   * because a literal is a new object on every evaluation, and this way the same
   * options instance is shared by all eight links.
   */
  public readonly exactMatchOptions: { readonly exact: true } = { exact: true };

  /**
   * Collapses the rail if it is expanded, expands it if it is collapsed.
   *
   * The template's collapse control calls this. Derives the next value from the
   * current one inside the update, rather than reading the signal and then setting
   * it, so the transition cannot be based on a value that has since changed.
   */
  public toggleCollapsed(): void {
    this.collapsedSignal.update((collapsed) => !collapsed);
  }
}

