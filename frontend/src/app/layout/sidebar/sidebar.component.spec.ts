/**
 * Specification for {@link SidebarComponent} — the administration console's single
 * `navigation` landmark.
 *
 * ## What this file is responsible for
 *
 * The rail performs no I/O, declares no provider, defines no route and holds exactly one
 * boolean of its own state. It injects two things and nothing else: the router, whose
 * current address decides which entry is announced as current, and the authentication
 * store, whose tenant-administration projection decides which entries are offered at all.
 * A specification for such a component has four jobs, and all four are done here.
 *
 * 1. **Pin the rendered contract.** The paired stylesheet compiles against a published
 *    selector vocabulary — `nav.app-sidebar`, `.app-sidebar--collapsed`,
 *    `.app-sidebar__toggle`, `.app-sidebar__regions`, `.app-sidebar__group`,
 *    `.app-sidebar__list`, `.app-sidebar__item`, `.app-sidebar__link` and
 *    `.app-sidebar__link--active`. Those names are load-bearing rather than
 *    decorative, so every one of them is asserted. A rename that left the stylesheet
 *    behind would otherwise ship silently, because a stylesheet whose selectors match
 *    nothing is not a compile error.
 * 2. **Pin route integrity.** Every address the rail renders is checked against the
 *    application's own top-level route table rather than against a list restated here.
 *    A rail entry that names no route is neither a compile error nor a run-time
 *    exception — the router resolves it to the wildcard entry — so it would ship as a
 *    link that quietly goes nowhere. That is the single most likely regression in a
 *    file of this kind and the only one no other tool catches.
 * 3. **Pin the accessibility contract**, including two hazards that are genuine defects
 *    rather than preferences. `aria-current="page"` must be UNIQUE, and the address pair
 *    `/modules` and `/modules/import` makes plain prefix matching produce two of them.
 *    It must also be PRESENT wherever the reader actually is: every rail entry names a
 *    collection or a creation screen, so the moment a record is opened the address showing
 *    is a descendant of a rail entry rather than one of them, and exact matching leaves
 *    the whole rail unmarked for the greater part of the console's screens. Both hazards
 *    are reproduced against the real router below — under `paths: 'subset'` and under
 *    `paths: 'exact'` respectively — so each expectation proves the hazard exists *and*
 *    proves the component's own segment-wise longest-prefix resolution answers it.
 *    A third hazard sits between the two: marking the ancestor is right, but announcing it as
 *    `page` claims the marked link IS the address being viewed when following it navigates
 *    away. So marking is unique and unconditional, while `page` is reserved for the exact
 *    address and an ancestor announces `true`.
 * 4. **Pin link visibility.** Five of the eight destinations address routes declared under
 *    the `PortalAdministrator` policy, and each is offered only when the caller holds the
 *    server's own determination that it administers the tenant. The cross-check is against
 *    the real route tables rather than a list restated here, so an entry whose gate
 *    disagrees with its route's policy fails.
 *
 * ## Harness
 *
 * Karma with Jasmine, driven by `ng test --watch=false --browsers=ChromeHeadless
 * --code-coverage`. The component is standalone, so it is supplied through `imports`
 * and never through a module declaration list, and the router is supplied by
 * `provideRouter` rather than by the deprecated testing module — `RouterLink` resolves a
 * real `Router` and the component resolves the current address from real router events, so
 * a stub would prove nothing about the addresses actually rendered or about which of them
 * is announced.
 *
 * The authentication store is supplied as a DOUBLE exposing exactly one member, the
 * tenant-administration projection. That is deliberate on two counts: it lets a
 * specification state administration directly, in both directions, rather than assembling
 * a session to imply it; and because the double satisfies the rail's whole store
 * dependency, no HTTP client is needed anywhere in this file — which keeps "this rail
 * issues no request" an assertion rather than a claim. No animations provider and no
 * hydration provider are configured either, deliberately —
 * neither the animations package nor the server-rendering package is installed in this
 * workspace, and the second absence is load-bearing for the project's security posture
 * rather than incidental: the sole runtime advisory outstanding against the pinned
 * framework version is a client-hydration vector, unreachable precisely because no
 * server-side rendering exists here to reach it.
 *
 * ## Determinism
 *
 * No clock is read, no random value is drawn, no timer is scheduled and no request is
 * issued anywhere in this file. Every expectation is a pure function of state this
 * specification itself established, so a failure here is always a real behavioural
 * change and never a flake. Where the router is driven, the navigation is awaited and
 * the fixture is settled explicitly rather than waited on for a duration.
 *
 * ## Notation
 *
 * Not one negation operator appears anywhere in this file. Presence and state are
 * compared against `null`, against `false` or against a value with `===`, never by
 * negating something, and nullable query results are narrowed through {@link present}
 * rather than being asserted away. The paired template adopts the same discipline and
 * records the same reason: the codebase's boundaries carry legacy sentinel values where a
 * truthiness test is a defect, and holding the line uniformly has the further benefit
 * that no character here can be mistaken for a non-null assertion.
 */

// MIGRATION: this entire harness is net-new, with no predecessor to mirror. The legacy
// tree contains ZERO automated tests of any kind — measured as `git ls-files '*.spec.ts'`
// returning nothing outside the workspace being added, and no test project anywhere in
// the two legacy trees. Nothing here was ported; every expectation was derived by reading
// the legacy skin, code-behind and resource files and deciding what the target must
// preserve. That changes how a reviewer should read the file: there is no reference suite
// whose coverage this one can be compared against, so each expectation carries its own
// justification inline.

// MIGRATION: ⚠⚠ THE ASSERTIONS BELOW ENCODE A NET-NEW INFORMATION ARCHITECTURE. There
// was no left navigation rail in the legacy application, and the evidence is recorded
// here because the opposite assumption is the natural one. The only complete skin in the
// checkout, `Website/Portals/_default/Skins/MinimalExtropy/index.ascx` (126 lines), places
// its menu control at L42-L47 declared `ControlOrientation="Horizontal"`, nested at
// L38-L41 four containers deep inside the header band — a HORIZONTAL bar between the logo
// band at L23-L37 and the breadcrumb band at L60-L77. L49 makes the search container a
// SIBLING of the menu container inside that same bar, so navigation and search shared one
// horizontal strip, and L109 renders a second horizontal navigation surface as a
// root-level link list across the footer band.
//
// The decisive evidence is L89: `<td valign="top" id="LeftPane" class="LeftPane"
// runat="server" visible="false">` is one of five hidden cells in the content table at
// L83-L100, alongside `TopPane` (L85), `ContentPane` (L91), `RightPane` (L93) and
// `BottomPane` (L97). That left-hand cell was a module CONTENT pane a portal
// administrator could drop modules into — never a navigation region. So "exactly one
// navigation landmark, in a vertical rail" is a NEW contract this specification
// establishes, not a legacy one it preserves.

// MIGRATION: the exactness of the current-page announcement is asserted because a real
// collision exists, not as a refinement. Link-activity matching is by prefix unless told
// otherwise, and the module collection address is a strict prefix of the module import
// address, so on the import screen prefix matching would mark BOTH entries active — two
// elements each claiming to be the current page. That claim is meaningless unless it is
// unique, making this an accessibility defect rather than a cosmetic one. The collision is
// reproduced directly against the router below so the hazard is demonstrated and not
// merely described.

// MIGRATION: no page-management entry is asserted, and its absence is asserted instead.
// The legacy page abstraction survives only as a lookup consulted by the module screens:
// its controller is closed at three read-and-update endpoints with no create and no
// delete, its client service exposes exactly the matching three calls, and the route table
// declares no collection address for it. The legacy page-management, page-export,
// page-import and recycle-bin screens are all out of scope. Worth recording for anyone
// reading the legacy source: the user-facing legacy term for that concept was always
// "Page", never the internal one.

// MIGRATION: the label expectations use MEASURED legacy wording, read from the `<value>`
// elements of the legacy resource files and never from the paired markup attributes,
// because the legacy screens assigned their titles from resources at run time and the
// markup is provably stale. Provenance, one per label:
//   Portal/App_LocalResources/Portals.ascx.resx            L144-L146  ControlTitle_.Text
//   Users/App_LocalResources/Users.ascx.resx               L147-L149  ControlTitle_.Text
//   Security/App_LocalResources/Roles.ascx.resx            L69-L71    ControlTitle_.Text
//   Modules/App_LocalResources/Import.ascx.resx            L57-L59    ControlTitle_importmodule.Text
//   Users/App_LocalResources/UserSettings.ascx.resx        L120-L122  ControlTitle_usersettings.Text
//   Users/App_LocalResources/ProfileDefinitions.ascx.resx  L120-L122  ControlTitle_manageprofile.Text
//   Security/App_LocalResources/EditGroups.ascx.resx       L57-L59    ControlTitle_editgroup.Text
// Localisation is NOT carried forward — no translation runtime is part of this migration —
// so the wording is authored directly into the template and the resource files served only
// as the authority for what that wording should be. The rail is therefore English-only,
// where the legacy menu could be localised per portal.

// MIGRATION: the module collection's label is the one genuinely NET-NEW string, and the
// proof is an absence I measured rather than an editorial preference. Every in-scope legacy
// administration tree names its collection view with the empty-discriminator resource key
// `ControlTitle_.Text`, and the file counts carrying it are Portal 5, Users 2, Security 1,
// Tabs 2 — and Modules ZERO. The Modules tree's only title keys are `ControlTitle_module`,
// `ControlTitle_exportmodule` and `ControlTitle_importmodule`. Modules is the single
// in-scope tree lacking the collection key, which corroborates that no legacy module-list
// screen is in scope and so no legacy wording exists to inherit.

// MIGRATION: the plain-text expectation exists because legacy resource text is untrusted
// markup. Measured across the 37 resource files of the five in-scope administration trees
// there are 1211 resource entries, of which 75 carry HTML tags — and they carry them
// XML-escaped, so a search for the unescaped form finds nothing and suggests the text is
// clean when it is not. One of those files additionally carries two complete script
// elements. None of that text reaches this rail, and the rule it implies is asserted
// anyway: interpolation escapes its content, whereas the legacy label controls rendered
// such values as live markup. The legacy code held the same position where it did render
// them — `Default.aspx.vb` L232 and `AccessDenied.ascx.vb` L43 both encode before display.

// MIGRATION: ⚠ LINK VISIBILITY IS GATED, AND THE EARLIER READING OF THIS FILE ARGUED THAT
// IT SHOULD NOT BE. That argument — show every entry point and let the router's guard and
// the API's 403 refuse — is sound for an entry whose outcome is genuinely uncertain from
// the client. It does not hold for these five. Each of `/modules`, `/modules/import`,
// `/settings/membership`, `/settings/profile-definitions` and `/role-groups/new` declares
// `data: { permission: 'PortalAdministrator' }` on its own route, so the outcome for a
// caller without that determination is not uncertain at all: the permission gate refuses
// the navigation and redirects to the access-denied screen, every time. Painting the link
// anyway is not a safe default, it is a promise the application has already decided to
// break — and it is measurably worse than the alternative, because the redirect costs the
// reader a screen change to learn what the rail could have told them by omission.
//
// The gate is the SERVER'S OWN DETERMINATION, re-exposed by the authentication store as
// `administersCurrentPortal`. It is not a permission KEY: `VIEW`, `EDIT`, `READ` and
// `WRITE` are persisted grants over a module or page INSTANCE and answer a different
// question of a different vocabulary. It is not a role NAME either: administration is
// conferred by `Portals.AdministratorRoleId`, the designated role is renameable, and a
// role of the same name may belong to another tenant.
//
// Gating remains an AFFORDANCE and never enforcement. The three ungated destinations stay
// ungated because their routes carry authentication alone, and every request is
// re-authorised server-side whatever this rail painted. The legacy refusal surface is still
// the authority on TONE — `Website/admin/Security/AccessDenied.ascx.vb` performs no check
// of its own: `Page_Load` at L41 tests only the query string at L42, renders the supplied
// message encoded at L43 and a localised fallback at L45, and BOTH branches use warning
// severity. Contrast `Website/Default.aspx.vb` L582, which uses error severity for
// insecure defaults: in the legacy vocabulary denial was a WARNING, never an error.

// MIGRATION: view state round-tripping is gone, so the collapse is asserted through the
// document and its accessibility attributes rather than through a posted-back field.
// `Website/Default.aspx` L23 wrapped the entire document in one multipart server form and
// L26-L27 declared the two hidden inputs that shuttled scroll position and client
// variables through every postback, so collapsing a legacy rail would have cost a server
// round trip. Measured in-scope legacy view-state usage is only 4 sites, all of which
// disappear with the postback lifecycle. The expectations below therefore assert that the
// rail renders no form and no hidden input at all.

import { HttpClient } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, RouterLink, provideRouter } from '@angular/router';

import { APP_ROUTES, ROOT_REDIRECT_PATH } from '../../app.routes';
import { AuthStore } from '../../core/state/auth.store';
import { MODULE_ROUTES } from '../../features/module/module.routes';
import { PORTAL_ROUTES } from '../../features/portal/portal.routes';
import { ROLE_ROUTES } from '../../features/role/role.routes';
import { USER_ROUTES } from '../../features/user/user.routes';
import { SidebarComponent } from './sidebar.component';

import type { Signal, WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { IsActiveMatchOptions, Route, Routes } from '@angular/router';
import type { SidebarNavGroup } from './sidebar.component';

/** The accessible name the landmark must carry, so it is distinguishable in a landmark list. */
const LANDMARK_NAME = 'Administration';

/** The stable identifier the region and the disclosure control must BOTH resolve to. */
const REGION_ID = 'sidebar-navigation';

/** The disclosure control's accessible name, which must not change with its state. */
const TOGGLE_NAME = 'Navigation';

/** The wildcard route's path, excluded from address resolution — see {@link routeCovering}. */
const WILDCARD_PATH = '**';

/** One expected destination: the address rendered, the text rendered for it, and the
 * authority its destination's own API endpoint requires.
 *
 * ⚠ THE POLICY IS TRANSCRIBED FROM THE CONTROLLER, NOT COPIED FROM THE COMPONENT. Copying
 * it would make the expectation a tautology that passed for whatever the component happened
 * to declare. Each value below cites the attribute it was read from, so this table is the
 * server's answer and the assertion genuinely asks whether the rail agrees with it.
 */
interface ExpectedItem {
  readonly address: string;
  readonly label: string;
  readonly policy: 'PortalAdministrator' | 'HostAdministrator' | 'PortalContentEditor';
}

/** One expected group: its stable key, its heading text, and its destinations in order. */
interface ExpectedGroup {
  readonly id: string;
  readonly label: string;
  readonly items: readonly ExpectedItem[];
}

/**
 * The whole rendered model, in presentation order.
 *
 * Held as one table rather than as separate lists of addresses and labels so that the
 * pairing itself is pinned: a template that rendered every expected address and every
 * expected label but paired them wrongly would satisfy two independent lists and is
 * caught by this one.
 *
 * Only collection and creation entry points appear, and that is a property of what a
 * global rail can express rather than an editorial choice: the detail routes are keyed
 * by record identifiers, and a rail rendered once for the whole application has no
 * record in hand to supply one.
 */
const EXPECTED_GROUPS: readonly ExpectedGroup[] = [
  {
    id: 'portal',
    label: 'Portal',
    // `PortalsController.cs:L241` — the tenant COLLECTION addresses no single tenant, so
    // enumerating it requires host authority. This is the one entry whose authority is
    // stricter than its route's own declaration, and it follows the endpoint.
    items: [{ address: '/portals', label: 'Portals', policy: 'HostAdministrator' }],
  },
  {
    id: 'module',
    label: 'Module',
    // `ModulesController.cs:L187` (listing) and `:L627` (import). The create entry between them
    // is the one rail destination that is NOT tenant administration: its screen's supporting
    // reads carry `PortalContentEditor` (`ModuleDefinitionsController.cs:168`,
    // `TabsController.cs:187`), which admits a caller holding EDIT on any one of the tenant's
    // pages as well as the tenant's administrators.
    items: [
      { address: '/modules', label: 'Modules', policy: 'PortalAdministrator' },
      { address: '/modules/new', label: 'Add Module', policy: 'PortalContentEditor' },
      { address: '/modules/import', label: 'Import Module', policy: 'PortalAdministrator' },
    ],
  },
  {
    id: 'user',
    label: 'User',
    // `UsersController.cs:L350` (listing), `:L827` (tenant membership policy) and
    // `ProfileDefinitionsController.cs:L165` (class-gated).
    items: [
      { address: '/users', label: 'User Accounts', policy: 'PortalAdministrator' },
      { address: '/settings/membership', label: 'User Settings', policy: 'PortalAdministrator' },
      {
        address: '/settings/profile-definitions',
        label: 'Manage Profile Properties',
        policy: 'PortalAdministrator',
      },
    ],
  },
  {
    id: 'role',
    label: 'Role',
    // `RolesController.cs:L284` gates the whole class on tenant administration.
    items: [
      { address: '/roles', label: 'Security Roles', policy: 'PortalAdministrator' },
      { address: '/role-groups/new', label: 'Add New Role Group', policy: 'PortalAdministrator' },
    ],
  },
];

/**
 * The destinations offered to EVERY signed-in caller: NONE, and the emptiness is the fact.
 *
 * ⚠ AN EARLIER REVISION LISTED THREE — `/portals`, `/users` and `/roles` — ON THE GROUND THAT
 * THEIR ROUTES DECLARED NO POLICY. That was measured against the wrong route. Each of those
 * three is a lazily-mounted PARENT segment in `APP_ROUTES` carrying the session gate alone, and
 * the policy sits on the `''` CHILD inside the feature barrel the parent loads: `/portals`
 * resolves to `PORTAL_ROUTES['']`, which declares `HostAdministrator`, and `/users` and `/roles`
 * resolve to children declaring `PortalAdministrator`. So following any of the three as an
 * ordinary account holder ends at the access-denied screen, which is exactly the outcome the rail
 * exists not to promise. {@link declaredPolicyFor} now resolves the child, which is what makes the
 * cross-check below meaningful rather than vacuous.
 *
 * Every destination this rail offers is an administration screen, so a signed-in caller who
 * administers nothing is offered nothing and the navigation landmark is withheld entirely. That
 * is the specified behaviour, not a capability loss: the rail declines to promise a capability the
 * application has already refused.
 */
const UNGATED_ADDRESSES: readonly string[] = [];

/**
 * The one destination offered only to a HOST account.
 *
 * The tenant collection is host-only on both sides: `PortalsController.cs` gates the listing on
 * `HostAdministrator`, and `PORTAL_ROUTES['']` declares the same policy. A tenant administrator is
 * refused it, which is why it cannot sit in the set below.
 */
const HOST_ONLY_ADDRESSES: readonly string[] = ['/portals'];

/**
 * The seven destinations offered only to a caller that administers the tenant.
 *
 * ⚠ EVERY ONE IS CROSS-CHECKED AGAINST ITS OWN ROUTE DECLARATION below, against the real
 * route tables rather than against this list. The list says which entries the rail gates;
 * the route tables say which entries MUST be gated, and an entry appearing in one and not
 * the other is the defect that check exists to find.
 */
const ADMINISTRATION_ONLY_ADDRESSES: readonly string[] = [
  '/modules',
  '/modules/import',
  '/users',
  '/settings/membership',
  '/settings/profile-definitions',
  '/roles',
  '/role-groups/new',
];

/**
 * The destinations offered to a caller holding EDIT anywhere in the tenant, administrator or not.
 *
 * ⚠ THE ONE SET THAT IS NOT AN ADMINISTRATION SET, and the reason the rail reads a third authority
 * fact at all. `PortalContentEditor` admits the tenant's administrators OR a caller holding EDIT on
 * at least one of its pages, so every address here is reachable by a caller who administers
 * nothing. Before this set existed the only link to module creation sat inside the module listing,
 * which is administration-gated, so such a caller had to guess the address.
 */
const CONTENT_EDIT_ADDRESSES: readonly string[] = ['/modules/new'];

/** The policy every content-edit destination's route must declare. */
const CONTENT_EDIT_POLICY = 'PortalContentEditor';

/**
 * The addresses a caller that administers the tenant but holds no host account is offered.
 *
 * ⚠ IN PRESENTATION ORDER, AND THAT IS WHY IT IS WRITTEN OUT RATHER THAN CONCATENATED. The
 * rendered comparison is order-sensitive, and the content-edit entry sits BETWEEN two
 * administration entries in the module group - so appending one set to the other would produce
 * a set that is right and an order that is wrong.
 *
 * Deliberately NOT equal to the whole rail: the tenant collection is withheld from this caller,
 * so this is the set that proves a group can be dropped while its siblings survive intact. It
 * DOES include the content-edit entry, because the policy's first arm is tenant administration -
 * an administrator holds it without holding any grant.
 */
const TENANT_ADMINISTRATION_ADDRESSES: readonly string[] = [
  '/modules',
  '/modules/new',
  '/modules/import',
  '/users',
  '/settings/membership',
  '/settings/profile-definitions',
  '/roles',
  '/role-groups/new',
];

/** The policy name every gated destination's route must declare. */
const ADMINISTRATION_POLICY = 'PortalAdministrator';

/** The policy the host-only destination's route must declare. */
const HOST_POLICY = 'HostAdministrator';

/**
 * The feature barrels `APP_ROUTES` mounts lazily, by the parent segment that mounts them.
 *
 * Held as a map rather than as a chain of comparisons so that adding a barrel is one entry and
 * cannot be half-done. Consumed only by {@link declaredPolicyFor}.
 */
const FEATURE_BARRELS: ReadonlyMap<string, readonly Route[]> = new Map<string, readonly Route[]>([
  [ROOT_REDIRECT_PATH, PORTAL_ROUTES],
  ['modules', MODULE_ROUTES],
  ['users', USER_ROUTES],
  ['roles', ROLE_ROUTES],
]);

/**
 * The four persisted permission keys, held here to assert that NONE of them appears as a
 * policy on any destination the rail gates.
 *
 * ⚠ THE VOCABULARY CONFUSION THIS FILE GUARDS AGAINST. These are grants over a module or
 * page INSTANCE, carried in `ModulePermissions` and `TabPermissions`. Gating an
 * administration destination with one of them asks a different question of a different
 * vocabulary, and can both hide an entry the caller may reach and offer one the router
 * will refuse.
 */
const PERSISTED_PERMISSION_KEYS: readonly string[] = ['VIEW', 'EDIT', 'READ', 'WRITE'];

/**
 * The three authority facts the rail takes as required inputs.
 *
 * The third is OPTIONAL here and defaults to `false` at the mount, because a caller who holds no
 * grant carries no permission key: an unstated third authority means "holds EDIT nowhere", which is
 * the conservative reading and the one that keeps the content-gated entry out of every spec that
 * says nothing about it.
 */
interface RailAuthority {
  /** Whether the caller holds a host account, as the server reported it. */
  readonly hostAccount: boolean;
  /** Whether the server reported the caller as an administrator of the resolved tenant. */
  readonly administersTenant: boolean;
  /** Whether the caller holds the `EDIT` key anywhere in that tenant. Defaults to `false`. */
  readonly editsContent?: boolean;
}

/** One descendant case: an address a reader can actually be at, and the entry it belongs under. */
interface DescendantCase {
  /** The address showing — a descendant of a rail entry, never a rail entry itself. */
  readonly showing: string;
  /** The rail entry that must be marked current while that address is showing. */
  readonly expectedActive: string;
  /** What the reader is doing there, for the failure message. */
  readonly description: string;
}

/**
 * The descendant addresses that prove exact matching is wrong.
 *
 * ⚠ THESE ARE NOT EDGE CASES — THEY ARE THE CONSOLE'S ORDINARY SCREENS. Every rail entry
 * names a collection or a creation form, so the instant a reader opens a record, edits it,
 * manages its memberships or changes a credential, the address showing is a descendant of
 * a rail entry and is not itself a rail entry. Under exact matching the rail goes
 * completely unmarked at each of them, which is where a reader most needs to know where
 * they are. One case is drawn from each feature group, and each is a real route.
 */
const DESCENDANT_CASES: readonly DescendantCase[] = [
  { showing: '/portals/new', expectedActive: '/portals', description: 'creating a portal' },
  { showing: '/portals/0/settings', expectedActive: '/portals', description: 'a portal\u2019s settings' },
  { showing: '/users/7/profile', expectedActive: '/users', description: 'an account\u2019s profile' },
  { showing: '/roles/0/users', expectedActive: '/roles', description: 'a role\u2019s membership' },
  { showing: '/modules/5/settings', expectedActive: '/modules', description: 'a module\u2019s settings' },
];

/** Every address the rail is expected to render, flattened in presentation order. */
const EXPECTED_ADDRESSES: readonly string[] = EXPECTED_GROUPS.flatMap(
  (group: ExpectedGroup): readonly string[] => group.items.map((item: ExpectedItem): string => item.address),
);

/** Every label the rail is expected to render, flattened in presentation order. */
const EXPECTED_LABELS: readonly string[] = EXPECTED_GROUPS.flatMap(
  (group: ExpectedGroup): readonly string[] => group.items.map((item: ExpectedItem): string => item.label),
);

/**
 * The three labels whose wording is directly attributable to a legacy resource entry
 * keyed `ControlTitle_.Text`, asserted separately from the full label list so the
 * legacy-parity claim is visible as its own expectation.
 */
const MEASURED_LEGACY_TITLES: readonly string[] = ['Portals', 'User Accounts', 'Security Roles'];

/** The one label with no legacy resource key behind it, asserted as present and named as net-new. */
const NET_NEW_LABEL = 'Modules';

/** The module collection and its child, whose prefix relationship is the activity hazard. */
const MODULE_COLLECTION_ADDRESS = '/modules';
const MODULE_IMPORT_ADDRESS = '/modules/import';

/**
 * The authentication address. It IS a declared route and is deliberately NOT a rail
 * entry: it is where an unauthenticated caller is sent, so offering it as navigation
 * would invite a signed-in administrator to leave the console. Asserting both halves —
 * declared, yet unrendered — is what distinguishes a deliberate omission from an
 * address that simply does not exist.
 */
const AUTHENTICATION_ADDRESS = '/login';

/**
 * Addresses that must resolve to NO declared route and must therefore never be
 * rendered. Each is a plausible-looking entry a later change might reach for.
 *
 * `/tabs` heads the list and is the important one: the page controller is closed at
 * three read-and-update endpoints, its client service exposes exactly the matching
 * three calls, there is no page feature folder and no page route. `/permissions` and
 * `/module-definitions` are read-only lookup catalogues with no screen; `/health` is a
 * container probe rather than an address in this application; and `/dashboard`,
 * `/home`, `/logout` and `/profile` are conventions this route table does not adopt.
 */
const ADDRESSES_WITH_NO_ROUTE: readonly string[] = [
  '/tabs',
  '/permissions',
  '/module-definitions',
  '/health',
  '/dashboard',
  '/home',
  '/logout',
  '/profile',
];

/**
 * Legacy user-facing wording for administration areas that are out of scope, asserted
 * absent from the rendered text.
 *
 * MIGRATION: eighteen of the twenty-three legacy administration trees produce no entry
 * in this rail. Their absence is the intended outcome, not an oversight, and no entry may
 * be added for one without the backing endpoint and route existing first. The check is on
 * WORDING rather than on addresses because a later change would reach for the label
 * before it reached for a route — and because these subsystems have no address to name.
 *
 * `pages` leads the list: the legacy page-management tree exists and is deliberately out
 * of nav scope, and the legacy user-facing term for that concept was always "Page",
 * never the internal one, so this is the string a reader of the legacy source would try.
 */
const EXCLUDED_LEGACY_WORDING: readonly string[] = [
  'pages',
  'recycle bin',
  'file manager',
  'host settings',
  'site log',
  'log viewer',
  'scheduler',
  'search admin',
  'newsletter',
  'vendors',
  'skins',
  'languages',
  'extensions',
  'lists',
  'solutions',
];

/**
 * Landmarks that belong to the rail's siblings and must not appear here.
 *
 * The shell divides the landmarks between its members with no overlap: the banner
 * belongs to the header band, the main region and its outlet to the shell itself, and
 * the contentinfo band to the footer. Emitting a second landmark of any of those kinds
 * from this component would put two of that kind in the accessibility tree.
 */
const FOREIGN_LANDMARKS: readonly string[] = ['header', 'main', 'footer', 'aside'];

/** Elements the rail must never render, each for a reason asserted in its own expectation. */
const FORBIDDEN_ELEMENTS: readonly string[] = ['form', 'script', 'iframe', 'img', 'input'];

/** The class names the paired stylesheet selects on, every one of which must be rendered. */
const PUBLISHED_CLASS_NAMES: readonly string[] = [
  'app-sidebar',
  'app-sidebar__toggle',
  'app-sidebar__regions',
  'app-sidebar__group',
  'app-sidebar__list',
  'app-sidebar__item',
  'app-sidebar__link',
];

/** The state class the landmark carries only while collapsed. */
const COLLAPSED_CLASS = 'app-sidebar--collapsed';

/** The class the router applies to the anchor for the address currently showing. */
const ACTIVE_LINK_CLASS = 'app-sidebar__link--active';

/**
 * Exact address matching, held here to DEMONSTRATE A HAZARD rather than to exercise the
 * component — the rail no longer configures the router's activity directive at all.
 *
 * ⚠ THIS IS THE STRATEGY THE RAIL USED TO PUBLISH, AND IT WAS WRONG. Under it a rail entry
 * is active only while its own address is showing exactly, so on `/portals/new`,
 * `/users/7/profile` and every other record screen NO entry is marked and the rail
 * announces no current page whatsoever. That is the greater part of the console. The
 * expectations below drive the real router under this strategy to prove the silence is a
 * property of the strategy, and then prove the component's own resolution answers it.
 *
 * Written in the explicit four-property form rather than as the deprecated boolean, which
 * documents exactly which of the four comparisons is exact.
 */
const EXACT_ADDRESS_MATCH: IsActiveMatchOptions = {
  paths: 'exact',
  queryParams: 'exact',
  fragment: 'ignored',
  matrixParams: 'ignored',
};

/**
 * Plain prefix matching — the router's default, and the OTHER hazard.
 *
 * Under this strategy the module collection address is active while its child is showing,
 * so `/modules/import` marks two entries current at once. Held to demonstrate that
 * collision against the real router: the component's resolution keeps only the LONGEST
 * matching entry, which is what makes the announcement unique without making it silent.
 */
const PREFIX_ADDRESS_MATCH: IsActiveMatchOptions = {
  paths: 'subset',
  queryParams: 'subset',
  fragment: 'ignored',
  matrixParams: 'ignored',
};

/**
 * A minimal, componentless route table covering exactly the rail's own addresses.
 *
 * Derived from {@link EXPECTED_ADDRESSES} rather than restated, so the table navigated
 * against cannot drift from the addresses being asserted. Nothing is rendered at any of
 * these addresses on purpose: the point of navigating is to move the router's current
 * address so that link activity can be observed, and mounting real destinations would
 * drag unrelated feature graphs into this specification and make it depend on files it
 * has no business knowing about.
 *
 * ⚠ THE EMPTY `children` ARRAY IS LOAD-BEARING AND MUST NOT BE REMOVED. The router
 * validates every route on construction and rejects one that declares none of
 * `component`, `loadComponent`, `redirectTo`, `children` or `loadChildren` — it raises
 * NG04014 before any navigation is attempted, so the failure appears as an injector
 * error rather than as a routing failure and is easy to misread. An empty child table
 * satisfies that requirement while still resolving as a leaf, because a route whose child
 * table is empty matches when its own segments consume the whole address.
 */
const NAVIGABLE_ROUTES: Routes = [
  ...EXPECTED_ADDRESSES.map((address: string): Route => ({ path: address.slice(1), children: [] })),
  // The descendant addresses too, declared flat rather than nested: the router matches on
  // the whole path, so a flat declaration resolves `/portals/0/settings` exactly as a
  // nested one would while keeping this table derived from one list per purpose.
  ...DESCENDANT_CASES.map((testCase: DescendantCase): Route => ({
    path: testCase.showing.slice(1),
    children: [],
  })),
];

/**
 * Splits an absolute address into its non-empty path segments.
 *
 * @param address An address beginning with a slash, as the rail renders it.
 * @returns The segments, with empty runs discarded so a trailing slash is harmless.
 */
function segmentsOf(address: string): readonly string[] {
  return address.split('/').filter((segment: string): boolean => segment.length > 0);
}

/**
 * Finds the top-level route that covers an address, preferring the longest match.
 *
 * ⚠ THE WILDCARD ROUTE IS DELIBERATELY EXCLUDED, and that exclusion is the whole point
 * of this helper. The wildcard covers every address, so a search that included it would
 * report success for an address that names no route — which is exactly the silent
 * failure this specification exists to catch. Excluding it means a rail entry with no
 * real route behind it finds nothing and fails.
 *
 * The longest match is preferred because a multi-segment route and a single-segment
 * route could both cover the same address, and the router itself resolves to the more
 * specific declaration.
 *
 * @param address The absolute address to resolve.
 * @returns The covering route, or `null` when the table declares none.
 */
function routeCovering(address: string): Route | null {
  const wanted = segmentsOf(address);

  const candidates = APP_ROUTES.filter((route: Route): boolean => {
    const declared = segmentsOf(route.path ?? '');

    if (declared.length === 0) {
      return false;
    }

    if (route.path === WILDCARD_PATH) {
      return false;
    }

    if (declared.length > wanted.length) {
      return false;
    }

    return declared.every((segment: string, index: number): boolean => segment === wanted[index]);
  });

  let best: Route | null = null;
  let bestLength = 0;

  for (const candidate of candidates) {
    const length = segmentsOf(candidate.path ?? '').length;

    if (length > bestLength) {
      best = candidate;
      bestLength = length;
    }
  }

  return best;
}

/**
 * The authorisation policy one route declares, or null when it declares none.
 *
 * @param route The route declaration, or undefined when none was found.
 * @returns The declared policy name, or null.
 */
function policyOf(route: Route | undefined): string | null {
  const declared: unknown = route?.data?.['permission'];

  return typeof declared === 'string' ? declared : null;
}

/**
 * The policy the route behind an address declares, resolved against the REAL route tables.
 *
 * ⚠ TWO TABLES, BECAUSE THE MODULE DESTINATIONS LIVE IN A LAZY BARREL. `/settings/membership`,
 * `/settings/profile-definitions` and `/role-groups/new` are declared at the top level and are
 * readable from the application table; `/modules` and `/modules/import` are declared inside the
 * module feature's own route file, which is imported here for its DATA alone. Importing the
 * barrel mounts nothing — the component thunks are never invoked — so this stays a data
 * cross-check rather than dragging a feature graph into a layout specification.
 *
 * Resolving the policy from the tables rather than restating it is the whole point: an entry
 * whose gate disagrees with its route's own declaration is exactly the defect being guarded
 * against, and a restated list would agree with itself and prove nothing.
 *
 * @param address The absolute address a rail entry renders.
 * @returns The declared policy name, or null when the route declares none.
 */
function declaredPolicyFor(address: string): string | null {
  const segments = segmentsOf(address);
  const barrel: readonly Route[] | undefined = FEATURE_BARRELS.get(segments[0] ?? '');

  // ⚠ THE CHILD, NOT THE PARENT, AND THIS IS THE WHOLE CORRECTNESS OF THE CROSS-CHECK. Four
  // addresses in the rail begin with a segment that `APP_ROUTES` mounts LAZILY, carrying the
  // session gate and nothing else; the policy that decides whether the address can be followed
  // sits on the child inside the barrel that parent loads. Reading the parent therefore reports
  // "no policy" for four gated destinations, which an earlier revision of this file did — and it
  // then concluded from that reading that three of them were ungated.
  if (barrel !== undefined) {
    const child = segments.slice(1).join('/');

    return policyOf(barrel.find((route: Route): boolean => (route.path ?? '') === child));
  }

  return policyOf(APP_ROUTES.find((route: Route): boolean => (route.path ?? '') === segments.join('/')));
}

/**
 * Narrows a nullable lookup, failing with a message that names what was wanted.
 *
 * Preferred over asserting the result away, so that a missing element or a missing
 * route declaration produces a failure naming what was sought rather than a property
 * access on `null` several lines later — and so that this file needs no non-null
 * assertion anywhere.
 *
 * Deliberately generic over any value rather than over elements alone, because the
 * route-integrity expectations narrow a nullable route declaration with exactly the same
 * discipline they narrow a nullable query result.
 *
 * @param found The result of a DOM query or a route lookup.
 * @param description What the caller was looking for.
 * @returns The same value, narrowed to non-nullable.
 */
function present<T>(found: T | null, description: string): T {
  if (found === null) {
    throw new Error(`Expected the navigation rail to have ${description}, but it did not.`);
  }

  return found;
}

/**
 * Asserts that a published signal is a read-only projection.
 *
 * Checked structurally, by the ABSENCE of the two mutators a writable signal exposes,
 * because that is the only thing a consumer can rely on. Attempting a write instead
 * would need a widening cast to compile, and the cast would weaken exactly the
 * guarantee being tested.
 *
 * @param candidate The published projection.
 * @param name The member name, for the failure message.
 */
function expectReadOnlySignal(candidate: Signal<unknown>, name: string): void {
  expect(typeof candidate).withContext(`${name} must be a callable signal`).toBe('function');
  expect('set' in candidate).withContext(`${name} must not publish set()`).toBeFalse();
  expect('update' in candidate).withContext(`${name} must not publish update()`).toBeFalse();
}

/** Collapses every run of whitespace to one space and trims, for text comparisons. */
function normalise(text: string | null): string {
  return (text ?? '').replace(/\s+/g, ' ').trim();
}

describe('SidebarComponent', () => {
  let fixture: ComponentFixture<SidebarComponent>;
  let component: SidebarComponent;

  /**
   * The one member of the authentication store the rail reads, held writable so a
   * specification can state administration in either direction and flip it mid-test.
   */

  /**
   * Configures the testing module and renders the rail.
   *
   * Called explicitly from each group rather than from one shared `beforeEach`, because
   * the activity group needs a route table it can navigate and the rest deliberately
   * get none — and a testing module cannot be reconfigured once a component has been
   * created from it.
   *
   * The component is supplied through `imports` because it is standalone; a declaration
   * list would not compile. `provideRouter` supplies a REAL router rather than a stub,
   * which is what makes the rendered addresses and the current-page resolution below
   * meaningful.
   *
   * ⚠ THE STORE DOUBLE EXPOSES EXACTLY ONE MEMBER, and that narrowness is an assertion in
   * itself: were the rail to reach for a session, a role list, a permission key or any
   * command, it would fail here rather than pass with a wrong premise. It is also what
   * keeps this file free of an HTTP client — the real store needs one transitively, the
   * double needs nothing, and "the rail issues no request" therefore stays provable.
   *
   * ⚠ THE DEFAULT IS AN ADMINISTERING CALLER, so the groups below describe the COMPLETE
   * rail. Gated visibility is stated on its own, in both directions, in its own group;
   * making the default the withholding case instead would have left every route-integrity,
   * wording and list-semantics expectation silently describing a three-entry rail.
   *
   * ⚠ THE TWO AUTHORITY INPUTS ARE SET BEFORE THE FIRST RENDER, AND THEY MUST BE. Both are
   * declared `input.required`, so reading either before it has been set raises NG0950 —
   * which is the intended contract rather than an inconvenience: a mounting site that
   * omitted the caller's authority must fail loudly instead of rendering the unfiltered
   * rail. Setting them here rather than after `detectChanges()` means the very first render
   * is already filtered, so no expectation below can pass against a rail that was briefly
   * unfiltered.
   *
   * ⚠ THE DEFAULT IS THE MOST-PRIVILEGED CALLER, AND THAT IS DELIBERATE FOR THIS FILE.
   * Almost every group here asserts the RENDERED CONTRACT — selectors, addresses, wording,
   * landmarks, link activity — and those properties are about the rail's markup rather than
   * about who is looking at it, so they need every entry present. The filtering itself is
   * asserted in its own group, which states the authority explicitly for each case.
   *
   * @param routes The route table to configure. Pass an empty table for the default
   *   case, where the router has nowhere to go and no link can be active.
   * @param authority The caller's authority. Defaults to a host account, which is the only
   *   caller every declared entry is offered to.
   */
  async function createComponent(
    routes: Routes,
    authority: RailAuthority = {
      hostAccount: true,
      administersTenant: true,
    },
    arrangement = true,
  ): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [SidebarComponent],
      providers: [
        provideRouter(routes),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SidebarComponent);
    component = fixture.componentInstance;

    // ⚠ THE SHELL ARRANGEMENT IS PINNED BEFORE THE FIRST RENDER, AND EVERY SPEC BELOW DEPENDS
    // ON IT. The rail derives its DEFAULT disclosure state from `--app-sidebar-side-by-side`,
    // which the paired stylesheet resolves through the shared breakpoint mixin — so left
    // unpinned the default would follow the width of whatever frame the runner happens to give
    // the fixture, and the same spec would pass on a wide runner and fail on a narrow one. An
    // inline declaration on the host outranks the `:host` rule, so this states the arrangement
    // instead of inferring it, and the resize event is what makes the component re-read it.
    //
    // No render happens here: all THREE authority inputs are REQUIRED, and rendering before they
    // are supplied raises the framework's required-input error.
    declareArrangement(arrangement);

    fixture.componentRef.setInput('hostAccount', authority.hostAccount);
    fixture.componentRef.setInput('administersTenant', authority.administersTenant);
    // ⚠ DEFAULTED TO FALSE, NEVER TO THE HOST DEFAULT ABOVE. The content-edit fact is the
    // advisory one, and a caller who holds no grant carries no key - so an unstated third
    // authority means "holds EDIT nowhere". Defaulting it true would make the content-gated
    // entry appear in every spec that says nothing about it, which is the opposite of what a
    // silent default should do.
    fixture.componentRef.setInput('editsContent', authority.editsContent ?? false);

    fixture.detectChanges();
  }

  /**
   * States which shell arrangement is in force and lets the rail observe the change.
   *
   * `true` is the SIDE-BY-SIDE arrangement, in which the rail is a column beside the content and
   * opens expanded; `false` is the STACKED arrangement, in which it is a full-width band above
   * the content and opens collapsed. The value is written as an inline custom property because
   * that is exactly the channel the component reads — it calls `getComputedStyle` on its own host
   * — so this exercises the real code path rather than a test-only seam, and it needs no viewport
   * emulation, which Karma cannot offer.
   *
   * The resize event is what makes the component re-read. It is the same event a real browser
   * fires when a drag crosses the breakpoint, so a spec that calls this is reproducing a genuine
   * user action.
   *
   * @param sideBySide Whether the rail is placed beside the content rather than above it.
   */
  function setArrangement(sideBySide: boolean): void {
    declareArrangement(sideBySide);
    fixture.detectChanges();
  }

  /**
   * States the arrangement and lets the component re-read it, WITHOUT rendering.
   *
   * Separated from {@link setArrangement} for one reason: it is also called before the required
   * inputs are supplied, where a render would raise the framework's required-input error.
   *
   * @param sideBySide Whether the rail is placed beside the content rather than above it.
   */
  function declareArrangement(sideBySide: boolean): void {
    pinArrangementOf(fixture, sideBySide);
  }

  /**
   * States the arrangement on ANY fixture's host and lets that instance re-read it.
   *
   * Exists because two specs create a second, independent instance to prove the rail holds no
   * shared state: both instances have to be told the same arrangement, or the comparison would
   * be measuring the runner's frame width rather than the property under test.
   *
   * @param target The fixture whose host should carry the declaration.
   * @param sideBySide Whether the rail is placed beside the content rather than above it.
   */
  function pinArrangementOf(target: ComponentFixture<SidebarComponent>, sideBySide: boolean): void {
    (target.nativeElement as HTMLElement).style.setProperty(
      '--app-sidebar-side-by-side',
      sideBySide ? '1' : '0',
    );
    globalThis.dispatchEvent(new Event('resize'));
  }

  /**
   * Restates the caller's tenant administration and re-renders.
   *
   * ⚠ THE INPUT IS RESTATED, NOT A STORE SIGNAL. The rail's authority arrives as required
   * inputs from its only caller rather than being read from the session store, so this is the one
   * channel that can change it. The render is explicit because the rail uses the on-push
   * change-detection strategy: setting the input marks the view dirty, and this is what makes the
   * new state observable in the document.
   *
   * @param administers Whether the caller administers the tenant.
   */
  function setAdministration(administers: boolean): void {
    fixture.componentRef.setInput('administersTenant', administers);
    fixture.detectChanges();
  }

  /**
   * Moves the router to an address and settles the view.
   *
   * The navigation is awaited and the fixture is then settled and re-rendered
   * explicitly. No duration is waited on anywhere, so this cannot become a timing
   * flake: link activity is recomputed from a router event, and the rail renders under
   * on-push change detection, so the explicit render is what makes the new state
   * observable.
   *
   * @param address The absolute address to navigate to.
   */
  async function navigateTo(address: string): Promise<void> {
    const router = TestBed.inject(Router);

    await router.navigateByUrl(address);
    await fixture.whenStable();

    fixture.detectChanges();
  }

  /**
   * The component's host element, typed once so that every query below is typed too.
   *
   * The fixture publishes its host untyped, which would leave every query result
   * untyped as well; narrowing here, once, is what lets the accessors declare honest
   * return types.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /** The navigation landmark, queried by BOTH its element type and its published class. */
  function landmark(): HTMLElement | null {
    return host().querySelector('nav.app-sidebar');
  }

  /** The disclosure control, queried by both its element type and its published class. */
  function toggle(): HTMLElement | null {
    return host().querySelector('button.app-sidebar__toggle');
  }

  /** The collapsible region, queried by both its element type and its published class. */
  function region(): HTMLElement | null {
    return host().querySelector('div.app-sidebar__regions');
  }

  /** Every destination anchor, in document order. */
  function navAnchors(): readonly HTMLAnchorElement[] {
    return Array.from(host().querySelectorAll<HTMLAnchorElement>('a.app-sidebar__link'));
  }

  /**
   * The rendered addresses, read from the `href` attribute.
   *
   * The attribute is read rather than the property because the property is resolved
   * against the document's base address, whereas the attribute carries exactly what the
   * router link produced — which is the value being asserted.
   */
  function renderedAddresses(): readonly string[] {
    return navAnchors().map((anchor: HTMLAnchorElement): string => anchor.getAttribute('href') ?? '');
  }

  /** The rendered link text, whitespace-normalised. */
  function renderedLabels(): readonly string[] {
    return navAnchors().map((anchor: HTMLAnchorElement): string => normalise(anchor.textContent));
  }

  /** Every element currently claiming to BE the current page. */
  function currentPageElements(): readonly Element[] {
    return Array.from(host().querySelectorAll('[aria-current="page"]'));
  }

  /**
   * Every element carrying `aria-current` at all, whatever its value.
   *
   * Distinct from {@link currentPageElements} because the rail announces two strengths: the
   * exact address claims `page`, and an ancestor of the address showing claims `true`. The
   * uniqueness obligation applies to MARKING - one marked entry, always - while the `page`
   * claim is additionally reserved for the address the reader is actually at. Asserting only
   * the `page` selector would report a correctly marked ancestor as an unmarked rail.
   */
  function markedElements(): readonly Element[] {
    return Array.from(host().querySelectorAll('[aria-current]'));
  }

  /**
   * The single anchor rendering a given address.
   *
   * @param address The absolute address to look for.
   * @returns The anchor, or `null` when the rail renders no such address.
   */
  function anchorFor(address: string): HTMLAnchorElement | null {
    const matches = navAnchors().filter(
      (anchor: HTMLAnchorElement): boolean => anchor.getAttribute('href') === address,
    );

    if (matches.length === 1) {
      return matches[0];
    }

    return null;
  }

  /** Every `RouterLink` instance the template created, one per destination anchor. */
  function linkDirectives(): readonly RouterLink[] {
    return fixture.debugElement
      .queryAll(By.directive(RouterLink))
      .map((debugElement): RouterLink => debugElement.injector.get(RouterLink));
  }

  // ---------------------------------------------------------------------------
  // THE LAYOUT THE RAIL DERIVES ITS DEFAULT FROM
  // ---------------------------------------------------------------------------

  // Installed before anything is rendered, so construction and the first render are
  // covered as well as every later interaction. The enforcement is in the shared
  // `afterEach` below: a presentational layout primitive has nothing to report, and a
  // diagnostic left behind in one would reach every page of the console.
  beforeEach(() => {
    spyOn(console, 'log');
    spyOn(console, 'info');
    spyOn(console, 'debug');
    spyOn(console, 'warn');
    spyOn(console, 'error');
  });

  afterEach(() => {
    expect(console.log).withContext('the navigation rail must not write to the console').not.toHaveBeenCalled();
    expect(console.info).withContext('the navigation rail must not write to the console').not.toHaveBeenCalled();
    expect(console.debug).withContext('the navigation rail must not write to the console').not.toHaveBeenCalled();
    expect(console.warn).withContext('the navigation rail must not warn').not.toHaveBeenCalled();
    expect(console.error).withContext('the navigation rail must not report an error').not.toHaveBeenCalled();
  });

  describe('landmark structure', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('renders exactly one navigation landmark', () => {
      // The headline accessibility contract of the whole folder. The rail is the ONLY
      // member of the shell that may emit a navigation landmark, and a second one of
      // that kind would put two in the accessibility tree — a defect rather than a
      // duplicate, because a landmark list is how a screen-reader user orients.
      expect(host().querySelectorAll('nav').length).toBe(1);
    });

    it('emits no landmark that belongs to a sibling band', () => {
      for (const element of FOREIGN_LANDMARKS) {
        expect(host().querySelectorAll(element).length)
          .withContext(`the navigation rail must not emit a <${element}> landmark`)
          .toBe(0);
      }
    });

    it('names the landmark, so it is distinguishable in a landmark list', () => {
      const nav = present(landmark(), 'a navigation landmark');

      // An unnamed landmark is announced as an anonymous region and cannot be told
      // apart from any other. The name deliberately does not repeat the word
      // "navigation", which assistive technology already announces from the element.
      expect(nav.getAttribute('aria-label')).toBe(LANDMARK_NAME);
      expect(normalise(nav.getAttribute('aria-label')).length).toBeGreaterThan(0);
    });

    it('makes the landmark the root of the rendered tree', () => {
      // Asserted because the paired stylesheet addresses `.app-sidebar` as the rail's
      // own box, and the shell's layout leaves the width to that box. A wrapper element
      // introduced above it would leave the stylesheet sizing the wrong element.
      const nav = present(landmark(), 'a navigation landmark');

      expect(nav.parentElement).toBe(host());
    });

    it('renders every class name the paired stylesheet selects on', () => {
      // A stylesheet whose selectors match nothing is not a compile error, so a rename
      // here would ship as an unstyled rail. Each published hook is asserted by name.
      for (const className of PUBLISHED_CLASS_NAMES) {
        expect(host().querySelectorAll(`.${className}`).length)
          .withContext(`the published hook .${className} is not rendered`)
          .toBeGreaterThan(0);
      }
    });

    it('renders none of the elements a presentational rail has no use for', () => {
      // MIGRATION: the form and hidden-input expectations are the view-state check. The
      // legacy document was wrapped in one multipart server form carrying two hidden
      // round-trip inputs, so collapsing a legacy rail cost a request; here the state is
      // a signal and nothing is posted. The script and iframe expectations pin the
      // plain-text rule, and the image expectation pins that the legacy symbol-font and
      // raster affordances are not reintroduced.
      for (const element of FORBIDDEN_ELEMENTS) {
        expect(host().querySelectorAll(element).length)
          .withContext(`the navigation rail must not render a <${element}>`)
          .toBe(0);
      }
    });
  });

  describe('route integrity', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('renders exactly the expected destinations, in order', () => {
      // Order-sensitive on purpose. A set comparison would pass against a rail that
      // reordered its groups, and the order is the information architecture.
      expect(renderedAddresses()).toEqual(EXPECTED_ADDRESSES);
      expect(navAnchors().length).toBe(EXPECTED_ADDRESSES.length);
    });

    it('resolves every rendered address against the real top-level route table', () => {
      // ⚠ THE MOST IMPORTANT EXPECTATION IN THIS FILE. A rail entry naming no route is
      // neither a compile error nor a run-time exception — the router resolves it to the
      // wildcard entry — so it would ship as a link that quietly goes nowhere. The
      // resolution is performed against the application's own imported route table
      // rather than against a list restated here, so it cannot pass by agreeing with a
      // copy that has drifted, and the wildcard is excluded from the search precisely so
      // that it cannot rescue an address with no real declaration behind it.
      for (const address of renderedAddresses()) {
        expect(routeCovering(address))
          .withContext(`the rendered address ${address} is covered by no declared route`)
          .not.toBeNull();
      }
    });

    it('delegates a deeper address only to a route that actually loads children', () => {
      // The module import address carries one segment more than the top-level route that
      // covers it, so the remaining segment can only resolve inside the lazily loaded
      // child table. Asserting the delegation exists is what makes the previous
      // expectation honest about multi-segment addresses: a covering route that loaded
      // no children could not resolve the extra segment at all.
      for (const address of renderedAddresses()) {
        const covering = present(routeCovering(address), `a declared route covering ${address}`);
        const extraSegments = segmentsOf(address).length - segmentsOf(covering.path ?? '').length;

        if (extraSegments > 0) {
          expect(typeof covering.loadChildren)
            .withContext(`${address} needs ${covering.path} to load children, and it does not`)
            .toBe('function');
        }
      }
    });

    it('agrees with the exported root address constant for the portals destination', () => {
      // A genuine cross-file contract: the application root redirects to this constant,
      // and the rail's first destination must be the same address, or the rail would
      // fail to mark the landing screen as current on arrival.
      expect(renderedAddresses()[0]).toBe(`/${ROOT_REDIRECT_PATH}`);
    });

    it('renders no address for the page abstraction', () => {
      // ⚠ /tabs is the one negative address that is a deliberate scope decision rather
      // than an oversight, so it is asserted on its own. The page controller is closed
      // at three read-and-update endpoints with no create and no delete, its client
      // service exposes exactly the matching three calls, and there is no page feature
      // folder and no page route. Adding an entry here would produce a link resolving
      // only to the wildcard.
      expect(anchorFor('/tabs')).toBeNull();
      expect(renderedAddresses()).not.toContain('/tabs');

      // Asserted against the table as well as against the rendering, so the claim is
      // "no such route exists" rather than merely "no such link is drawn".
      expect(routeCovering('/tabs')).toBeNull();
    });

    it('renders no address that resolves to no route', () => {
      for (const address of ADDRESSES_WITH_NO_ROUTE) {
        expect(renderedAddresses())
          .withContext(`${address} names no route and must not be rendered`)
          .not.toContain(address);

        expect(routeCovering(address))
          .withContext(`${address} unexpectedly became a declared route`)
          .toBeNull();
      }
    });

    it('does not offer the authentication address as navigation', () => {
      // Both halves matter. The address IS declared — proved below — so its absence from
      // the rail is a deliberate omission rather than an address that does not exist: it
      // is where an unauthenticated caller is sent, so offering it as navigation would
      // invite a signed-in administrator to leave the console.
      expect(routeCovering(AUTHENTICATION_ADDRESS))
        .withContext('the authentication address must remain a declared route')
        .not.toBeNull();

      expect(renderedAddresses()).not.toContain(AUTHENTICATION_ADDRESS);
    });

    it('renders no parameterised address', () => {
      // A rail rendered once for the whole application holds no record, so it cannot
      // supply a route parameter. An address containing a parameter placeholder would
      // resolve to the literal text of the placeholder.
      for (const address of renderedAddresses()) {
        expect(address.includes(':'))
          .withContext(`${address} carries a route parameter the rail cannot supply`)
          .toBeFalse();
      }
    });

    it('renders every address as absolute', () => {
      // A relative address would resolve against whichever screen happens to be showing,
      // so the same rail entry would mean different things on different screens.
      for (const address of renderedAddresses()) {
        expect(address.startsWith('/'))
          .withContext(`${address} must be absolute`)
          .toBeTrue();
      }
    });

    it('renders each address exactly once', () => {
      const seen = new Set<string>(renderedAddresses());

      expect(seen.size).toBe(renderedAddresses().length);
    });

    it('renders in-application addresses, never an interface endpoint', () => {
      // ⚠ A ROUTE AND AN ENDPOINT ARE NOT THE SAME THING, and the membership destination
      // is exactly where the two invite confusion: the interface serves those settings
      // under a versioned path nested beneath the users resource, whereas the address a
      // user navigates to is the flat one asserted above. Pasting an interface path into
      // a rail entry produces a link that leaves the application, and no compiler or
      // router would object.
      for (const address of renderedAddresses()) {
        expect(address.startsWith('/api'))
          .withContext(`${address} looks like an interface endpoint rather than a route`)
          .toBeFalse();

        expect(address.includes('/v1/'))
          .withContext(`${address} carries an interface version segment`)
          .toBeFalse();

        expect(address.includes('://'))
          .withContext(`${address} is an absolute location rather than an in-application address`)
          .toBeFalse();
      }
    });

    it('renders every address through a router link rather than a plain document link', () => {
      // A plain `href` would take the browser out of the application and discard every
      // piece of client state, which is the whole benefit the migration is buying.
      expect(linkDirectives().length).toBe(EXPECTED_ADDRESSES.length);
    });
  });

  describe('current-page announcement, with no route table configured', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('claims no current page before any navigation', () => {
      // The announcement is BOUND rather than written as a fixed attribute, and resolves
      // to nothing when the address is not the one showing — binding an empty result
      // removes the attribute outright instead of leaving a stale claim behind.
      expect(currentPageElements().length).toBe(0);
    });

    it('applies the active class to no anchor before any navigation', () => {
      const active = navAnchors().filter((anchor: HTMLAnchorElement): boolean =>
        anchor.classList.contains(ACTIVE_LINK_CLASS),
      );

      expect(active.length).toBe(0);
    });

    it('resolves no active path before any navigation', () => {
      // The published resolution and the document must agree at rest as well as in motion,
      // so the projection is asserted alongside the two rendered consequences above.
      expect(component.activePath()).toBeNull();
    });

    it('delegates the current-page decision to NO router directive', () => {
      // ⚠ THE STRUCTURAL FORM OF THE CORRECTION. Activity used to be delegated to
      // `routerLinkActive` with `{ exact: true }` on every entry, which is what left the
      // rail silent on every descendant address. The component now resolves the current
      // entry itself — segment-wise, longest match wins — and drives BOTH the class and the
      // announcement from that one answer, so the two cannot disagree.
      //
      // Asserted by the absence of the directive rather than by behaviour alone, because a
      // rail carrying both mechanisms could pass every behavioural expectation below while
      // holding a second, contradictory source of truth.
      expect(fixture.debugElement.queryAll(By.directive(RouterLink)).length).toBe(
        EXPECTED_ADDRESSES.length,
      );

      for (const anchor of navAnchors()) {
        expect(anchor.hasAttribute('routerLinkActive'))
          .withContext('the rail must not delegate activity to the router directive')
          .toBeFalse();
      }
    });
  });

  describe('current-page announcement, driven over a navigable route table', () => {
    beforeEach(async () => {
      await createComponent(NAVIGABLE_ROUTES);
    });

    it('claims exactly one current page on the module collection', async () => {
      await navigateTo(MODULE_COLLECTION_ADDRESS);

      const claimed = currentPageElements();

      expect(claimed.length).toBe(1);
      expect(claimed[0]).toBe(present(anchorFor(MODULE_COLLECTION_ADDRESS), 'the module collection anchor'));
    });

    it('claims exactly one current page on the module import screen', async () => {
      // ⚠⚠ THIS IS THE EXPECTATION THE WHOLE ACTIVITY CONTRACT EXISTS FOR. The module
      // collection address is a strict prefix of this one, so under the router's default
      // matching BOTH anchors would be active here and the document would carry two
      // elements each claiming to be the current page. That claim is meaningless unless
      // it is unique, so this is an accessibility defect and not merely cosmetic.
      await navigateTo(MODULE_IMPORT_ADDRESS);

      const claimed = currentPageElements();

      expect(claimed.length).toBe(1);
      expect(claimed[0]).toBe(present(anchorFor(MODULE_IMPORT_ADDRESS), 'the module import anchor'));

      const collection = present(anchorFor(MODULE_COLLECTION_ADDRESS), 'the module collection anchor');

      expect(collection.hasAttribute('aria-current')).toBeFalse();
      expect(collection.classList.contains(ACTIVE_LINK_CLASS)).toBeFalse();
    });

    it('proves the prefix collision is real, and that the longest match is what suppresses it', async () => {
      // Demonstrates the hazard against the REAL router rather than describing it. While the
      // import screen is showing, the collection address is active under the default subset
      // strategy — so the collision is a property of the address pair and not of any
      // implementation. The component keeps only the LONGEST matching entry, which is why
      // exactly one claim survives above.
      await navigateTo(MODULE_IMPORT_ADDRESS);

      const router = TestBed.inject(Router);

      expect(router.isActive(MODULE_COLLECTION_ADDRESS, PREFIX_ADDRESS_MATCH))
        .withContext('the prefix collision this rail must suppress has disappeared')
        .toBeTrue();

      expect(component.activePath())
        .withContext('the longer of the two matching entries is the current one')
        .toBe(MODULE_IMPORT_ADDRESS);
    });

    it('marks the ancestor entry while a DESCENDANT address is showing', async () => {
      // ⚠⚠ THE EXPECTATION THAT WOULD HAVE CAUGHT THE DEFECT THIS FILE ONCE ENCODED. Every
      // rail entry names a collection or a creation form, so a reader opening a record,
      // editing it, managing its memberships or changing a credential is at an address that
      // is a DESCENDANT of a rail entry and is not itself one. Exact matching left the rail
      // entirely unmarked at every one of them — no active class and no `aria-current` — for
      // the greater part of the console's screens, and a screen-reader user navigating by
      // landmark had nothing at all to orient against.
      //
      // Each case is driven through the real router and asserted on all three surfaces: the
      // published resolution, the active class, and the announcement.
      for (const testCase of DESCENDANT_CASES) {
        await navigateTo(testCase.showing);

        const expected = present(
          anchorFor(testCase.expectedActive),
          `an anchor for ${testCase.expectedActive}`,
        );

        expect(component.activePath())
          .withContext(`${testCase.description}: the ancestor entry must be current`)
          .toBe(testCase.expectedActive);

        expect(expected.classList.contains(ACTIVE_LINK_CLASS))
          .withContext(`${testCase.description}: the ancestor anchor must carry the active class`)
          .toBeTrue();

        // ⚠ `true`, NOT `page`, AND THE DIFFERENCE IS THE SECOND DEFECT ON THIS ATTRIBUTE.
        // Marking the ancestor is right; claiming it IS the page is not. On `/portals/3/settings`
        // the Portals entry is the branch the reader is inside, and following it arrives
        // somewhere else - so a screen-reader user was told the current page was a link that
        // would navigate away from it. The unqualified value says "the current item in this set"
        // and claims no more than the rail can honour.
        expect(expected.getAttribute('aria-current'))
          .withContext(`${testCase.description}: the ancestor announces itself as current`)
          .toBe('true');

        expect(markedElements().length)
          .withContext(`${testCase.description}: exactly one entry may be marked`)
          .toBe(1);

        expect(currentPageElements().length)
          .withContext(`${testCase.description}: and no entry may claim to BE this page`)
          .toBe(0);
      }
    });

    it('still claims the page itself when the address carries a query', async () => {
      // The two strengths are decided by SEGMENTS, never by comparing addresses as text. A text
      // comparison would demote the claim from `page` to `true` the moment a listing carried a
      // filter, so every letter typed into a search box would flip the announcement back and
      // forth - and `/users?filter=A` is the same destination as `/users`, which is why the
      // resolution drops the query in the first place.
      await navigateTo('/users?filter=A');

      const users = present(anchorFor('/users'), 'the user accounts anchor');

      expect(users.getAttribute('aria-current')).toBe('page');
      expect(markedElements().length).toBe(1);
    });

    it('proves exact matching is what silenced the rail on those addresses', async () => {
      // The hazard demonstrated against the REAL router, so the correction is shown to
      // answer a real property of the route table rather than a described one. Under exact
      // matching NOTHING the rail renders is active at a descendant address; under the
      // component's own resolution exactly one entry is.
      const router = TestBed.inject(Router);

      for (const testCase of DESCENDANT_CASES) {
        await navigateTo(testCase.showing);

        const activeUnderExactMatching = EXPECTED_ADDRESSES.filter((address: string): boolean =>
          router.isActive(address, EXACT_ADDRESS_MATCH),
        );

        expect(activeUnderExactMatching.length)
          .withContext(`exact matching still marks something at ${testCase.showing}`)
          .toBe(0);

        expect(component.activePath())
          .withContext(`the rail must stay oriented at ${testCase.showing}`)
          .toBe(testCase.expectedActive);
      }
    });

    it('refuses a partial SEGMENT match, so one entry cannot claim another\u2019s screens', async () => {
      // ⚠ SEGMENT-WISE, NEVER STRING-WISE, AND THE ROUTE TABLE MAKES THAT LOAD-BEARING.
      // `/roles` is a string PREFIX of `/role-groups/new`, so a resolution comparing text
      // rather than segments would mark the security-roles entry current while the reader is
      // on the role-group form — a wrong orientation rather than a missing one.
      await navigateTo('/role-groups/new');

      expect(component.activePath()).toBe('/role-groups/new');

      const roles = present(anchorFor('/roles'), 'the security roles anchor');

      expect(roles.classList.contains(ACTIVE_LINK_CLASS))
        .withContext('a shared text prefix is not a shared path')
        .toBeFalse();
      expect(roles.hasAttribute('aria-current')).toBeFalse();
      expect(currentPageElements().length).toBe(1);
    });

    it('never claims more than one current page, at any destination', async () => {
      // Swept across every address the rail offers, because the module pair is the
      // collision that exists today and a later addition could introduce another. The
      // settings pair shares a first segment without either being a prefix of the other,
      // so it is covered here rather than singled out.
      for (const address of EXPECTED_ADDRESSES) {
        await navigateTo(address);

        expect(currentPageElements().length)
          .withContext(`more than one element claimed to be the current page at ${address}`)
          .toBe(1);

        expect(present(anchorFor(address), `an anchor for ${address}`).getAttribute('aria-current')).toBe('page');
      }
    });

    it('marks exactly one anchor with the active class, at any destination', async () => {
      // The visual treatment and the announcement are driven from ONE source, so they
      // cannot disagree — and colour is therefore never the only carrier of which entry
      // is current. Asserted alongside the announcement so a future change that split
      // the two sources fails here.
      for (const address of EXPECTED_ADDRESSES) {
        await navigateTo(address);

        const active = navAnchors().filter((anchor: HTMLAnchorElement): boolean =>
          anchor.classList.contains(ACTIVE_LINK_CLASS),
        );

        expect(active.length)
          .withContext(`the active class was applied to ${active.length} anchors at ${address}`)
          .toBe(1);

        expect(active[0]).toBe(present(anchorFor(address), `an anchor for ${address}`));
      }
    });

    it('keeps the announcement and the active class on the same anchor', async () => {
      await navigateTo(MODULE_IMPORT_ADDRESS);

      const claimed = currentPageElements();
      const active = navAnchors().filter((anchor: HTMLAnchorElement): boolean =>
        anchor.classList.contains(ACTIVE_LINK_CLASS),
      );

      expect(claimed.length).toBe(1);
      expect(active.length).toBe(1);
      expect(claimed[0]).toBe(active[0]);
    });

    it('reports the same answer through the published projection as through the document', async () => {
      // One source of truth, asserted as such: the class, the announcement and the published
      // resolution are three renderings of a single computed value, so a change that split
      // them fails here.
      await navigateTo(MODULE_IMPORT_ADDRESS);

      const claimed = currentPageElements();
      const resolved = component.activePath();

      expect(claimed.length).toBe(1);
      expect(resolved).toBe(MODULE_IMPORT_ADDRESS);
      expect(claimed[0]).toBe(present(anchorFor(MODULE_IMPORT_ADDRESS), 'the module import anchor'));
    });
  });

  describe('collapse disclosure', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('is a real button that acts on the current document', () => {
      const control = present(toggle(), 'a disclosure control');

      // A button is what a control that acts on the current document IS, and it is
      // focusable, keyboard-operable and announced correctly with no attribute added to
      // make it so. The explicit type matters: a button inside a form defaults to
      // submitting it, and although this rail renders no form, one could enclose it.
      expect(control.tagName).toBe('BUTTON');
      expect(control.getAttribute('type')).toBe('button');
      expect(control.hasAttribute('href')).toBeFalse();
    });

    it('carries an accessible name that does not change with its state', () => {
      const control = present(toggle(), 'a disclosure control');

      expect(normalise(control.textContent)).toBe(TOGGLE_NAME);

      control.click();
      fixture.detectChanges();

      // A name that rewrote itself on activation would change the control's identity
      // underneath the user at the moment they were relying on it, and would restate in
      // words something an attribute built to report it already reports.
      expect(normalise(present(toggle(), 'a disclosure control').textContent)).toBe(TOGGLE_NAME);
    });

    it('names the region it operates, and that region exists', () => {
      const control = present(toggle(), 'a disclosure control');
      const controls = present(
        control.getAttribute('aria-controls'),
        'an aria-controls reference on its disclosure control',
      );

      expect(controls).toBe(REGION_ID);

      // ⚠ The relationship is only meaningful if the named element is really there.
      // Assistive technology reports a control pointing at a missing element as a broken
      // relationship rather than silently ignoring it, which is why the region is HIDDEN
      // while collapsed and never removed.
      const named = present(host().querySelector(`#${controls}`), `an element with id ${controls}`);

      expect(named).toBe(present(region(), 'a collapsible region'));
    });

    it('reads the region identifier the component publishes, in both places', () => {
      // Both halves of the relationship read the SAME published identifier, so the
      // control and the region it claims to operate are provably the same element and
      // cannot drift apart.
      expect(component.navigationRegionId).toBe(REGION_ID);
      expect(present(region(), 'a collapsible region').id).toBe(component.navigationRegionId);
      expect(present(toggle(), 'a disclosure control').getAttribute('aria-controls')).toBe(
        component.navigationRegionId,
      );
    });

    it('starts expanded beside the content, because navigation is what a user arrives wanting', () => {
      const control = present(toggle(), 'a disclosure control');

      expect(component.collapsed()).toBeFalse();

      // ⚠ NOTE THE INVERSION. The attribute reports whether the region is SHOWN, whereas
      // the component's state reports whether the rail is COLLAPSED, so the two are
      // deliberately opposite. Asserting the literal string rather than a truthiness
      // check is what catches a template that bound the state through unnegated.
      expect(control.getAttribute('aria-expanded')).toBe('true');
      expect(present(region(), 'a collapsible region').hidden).toBeFalse();
      expect(present(landmark(), 'a navigation landmark').classList.contains(COLLAPSED_CLASS)).toBeFalse();
    });

    it('starts COLLAPSED above the content, so a handset reaches its content first', () => {
      // ⚠ THE OPPOSITE DEFAULT, AND IT IS THE POINT OF THE DERIVATION RATHER THAN AN EXCEPTION
      // TO IT. In the stacked arrangement the rail is a full-width band BETWEEN the header and
      // the content, so an expanded rail pushes the page heading and every row command below it:
      // measured at a 320-wide viewport as a 441.8px block that put the heading at y~545 and the
      // first row command at y~804, which made reaching any content at all mean scrolling past
      // the whole of the navigation, on every screen. Beside the content the rail costs nothing
      // it does not earn and stays open; above it, it waits to be asked.
      setArrangement(false);

      expect(component.collapsed()).toBeTrue();
      expect(present(toggle(), 'a disclosure control').getAttribute('aria-expanded')).toBe('false');
      expect(present(region(), 'a collapsible region').hidden).toBeTrue();
      expect(present(landmark(), 'a navigation landmark').classList.contains(COLLAPSED_CLASS)).toBeTrue();
    });

    it('lets the operator overrule the arrangement, and keeps their choice across a resize', () => {
      // The derivation supplies a DEFAULT and nothing more. Once the disclosure has been pressed
      // the rail holds a real decision, and crossing the breakpoint must not overwrite it -
      // a rail opened on a handset stays open, which is the whole reason the state is stored as
      // "not yet chosen" rather than as a boolean seeded once.
      setArrangement(false);
      expect(component.collapsed()).toBeTrue();

      present(toggle(), 'a disclosure control').click();
      fixture.detectChanges();

      expect(component.collapsed()).toBeFalse();

      setArrangement(true);
      expect(component.collapsed()).toBeFalse();

      setArrangement(false);
      expect(component.collapsed()).toBeFalse();
      expect(present(region(), 'a collapsible region').hidden).toBeFalse();
    });

    it('reverses what the operator can see on the FIRST press in either arrangement', () => {
      // ⚠ REGRESSION GUARD. The writable slot opens as "not yet chosen", so a transition derived
      // by negating that slot rather than the EFFECTIVE state would resolve to the same value
      // twice above the content: the rail is already collapsed there, and the first press would
      // have collapsed it again and appeared to do nothing at all.
      setArrangement(false);

      const control = present(toggle(), 'a disclosure control');

      control.click();
      fixture.detectChanges();

      expect(component.collapsed()).toBeFalse();
      expect(control.getAttribute('aria-expanded')).toBe('true');
    });

    it('flips the disclosure state when activated', () => {
      present(toggle(), 'a disclosure control').click();
      fixture.detectChanges();

      expect(component.collapsed()).toBeTrue();
      expect(present(toggle(), 'a disclosure control').getAttribute('aria-expanded')).toBe('false');
      expect(present(region(), 'a collapsible region').hidden).toBeTrue();
      expect(present(landmark(), 'a navigation landmark').classList.contains(COLLAPSED_CLASS)).toBeTrue();
    });

    it('returns to the expanded state when activated again', () => {
      const control = present(toggle(), 'a disclosure control');

      control.click();
      fixture.detectChanges();
      control.click();
      fixture.detectChanges();

      expect(component.collapsed()).toBeFalse();
      expect(present(toggle(), 'a disclosure control').getAttribute('aria-expanded')).toBe('true');
      expect(present(region(), 'a collapsible region').hidden).toBeFalse();
      expect(present(landmark(), 'a navigation landmark').classList.contains(COLLAPSED_CLASS)).toBeFalse();
    });

    it('derives each transition from the state at that moment', () => {
      // Three activations must land on collapsed, not on whatever a captured value said.
      // The published method updates from the current value rather than reading and then
      // setting, so the transition cannot be based on a value that has since changed.
      const control = present(toggle(), 'a disclosure control');

      for (let activation = 0; activation < 3; activation += 1) {
        control.click();
        fixture.detectChanges();
      }

      expect(component.collapsed()).toBeTrue();
    });

    it('keeps the region in the document while collapsed', () => {
      present(toggle(), 'a disclosure control').click();
      fixture.detectChanges();

      // Hidden, never removed — otherwise the control above would name an element that
      // does not exist. Hiding keeps the element addressable while taking it out of the
      // accessibility tree, so both states are honest.
      const collapsed = present(region(), 'a collapsible region while collapsed');

      expect(collapsed.hidden).toBeTrue();
      expect(collapsed.querySelectorAll('a').length).toBe(EXPECTED_ADDRESSES.length);
    });

    it('really stops presenting the region while collapsed, as the browser computes it', () => {
      // ⚠⚠ THE ONE CROSS-FILE CONTRACT NOTHING ELSE CATCHES, asserted here because this
      // suite runs in a real browser and can therefore ask for a computed value rather
      // than infer one.
      //
      // The region collapses through the native hidden property, whose user-agent rule is
      // a plain `display: none` — and any author-level `display` declaration on that same
      // element outranks it. A stylesheet that laid the region out unconditionally would
      // therefore leave a "collapsed" rail still showing every link, with the property
      // set, the attribute present and the state class applied: every DOM expectation in
      // this file would pass and the rail would be visibly broken.
      //
      // The paired stylesheet avoids that by guarding its layout declaration so it cannot
      // apply while the element is hidden. This expectation is what holds that guard in
      // place, and it fails the moment the guard is dropped.
      const expanded = present(region(), 'a collapsible region');

      // Asserted as the specific value the paired stylesheet declares, not merely as
      // "something other than none". That is what makes the second half of this
      // expectation mutation-sensitive: a division defaults to block layout, so if the
      // scoped stylesheet were not reaching this element at all, the collapsed state
      // would still compute to none purely from the user-agent rule and the expectation
      // would pass while proving nothing. Observing the authored value here establishes
      // that the stylesheet IS live, so the none below can only mean the guard held.
      expect(window.getComputedStyle(expanded).display)
        .withContext('the paired stylesheet is not reaching the region while expanded')
        .toBe('flex');

      present(toggle(), 'a disclosure control').click();
      fixture.detectChanges();

      expect(window.getComputedStyle(present(region(), 'a collapsible region')).display)
        .withContext('an author-level display declaration is overriding the native hidden state')
        .toBe('none');
    });

    it('leaves the toggle reachable while collapsed', () => {
      present(toggle(), 'a disclosure control').click();
      fixture.detectChanges();

      // The control must survive its own action, or the rail could be collapsed once and
      // never reopened. It also must not be taken out of the tab order.
      const control = present(toggle(), 'a disclosure control while collapsed');

      expect(control.hasAttribute('disabled')).toBeFalse();
      expect(control.hasAttribute('hidden')).toBeFalse();
      expect(control.getAttribute('tabindex')).toBeNull();
    });

    it('toggles through the published method as well as through the control', () => {
      component.toggleCollapsed();
      fixture.detectChanges();

      expect(component.collapsed()).toBeTrue();
      expect(present(toggle(), 'a disclosure control').getAttribute('aria-expanded')).toBe('false');
    });
  });

  describe('list semantics and headings', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('renders one genuine list per group', () => {
      expect(host().querySelectorAll('ul').length).toBe(EXPECTED_GROUPS.length);
      expect(host().querySelectorAll('li').length).toBe(EXPECTED_ADDRESSES.length);

      // A list announces its length, so a user knows how many destinations there are
      // before stepping through them. A sequence of anchors in a division does not.
      expect(host().querySelectorAll('ol').length).toBe(0);
    });

    it('places every destination anchor inside a list item', () => {
      for (const anchor of navAnchors()) {
        const item = present(anchor.closest('li'), `a list item around ${anchor.getAttribute('href')}`);

        expect(item.classList.contains('app-sidebar__item')).toBeTrue();
        expect(present(item.parentElement, 'a list around each item').tagName).toBe('UL');
      }
    });

    it('renders one anchor per list item, and nothing else in it', () => {
      for (const item of Array.from(host().querySelectorAll('li'))) {
        const descendants = Array.from(item.querySelectorAll('*'));

        expect(descendants.map((element: Element): string => element.tagName)).toEqual(['A']);
      }
    });

    it('names one group label per group and keeps every one of them OUT of the document outline', () => {
      // ⚠ THIS SPEC ASSERTED THE OPPOSITE, AND THE OPPOSITE WAS THE DEFECT. It required these
      // labels to be `<h2>` elements "in the document outline", reasoning that "a styled
      // division would be invisible to the document outline, so a group heading could not be
      // navigated to". The premise is true and the conclusion was the wrong trade: because the
      // rail precedes `<main>` in document order, four level-two headings stood AHEAD of every
      // screen's `<h1>`. The outline opened at level two and each page's real heading arrived
      // fifth, at the same level as a piece of navigation furniture.
      //
      // Navigability is not lost, because it never depended on the element being a heading: the
      // list carries `aria-labelledby` pointing at this label, so the group is still announced
      // as a NAMED NAVIGATION REGION — asserted by the sibling spec below. What changes is only
      // that the furniture no longer competes with the content for heading level.
      const labels = Array.from(host().querySelectorAll('p.app-sidebar__group'));

      expect(labels.length).toBe(EXPECTED_GROUPS.length);
      expect(labels.map((label: Element): string => normalise(label.textContent))).toEqual(
        EXPECTED_GROUPS.map((group: ExpectedGroup): string => group.label),
      );

      // THE POINT OF THE CHANGE: the rail contributes NO heading at any level, so the first
      // heading a reader meets is the page's own `<h1>` inside the main region.
      expect(host().querySelectorAll('h1,h2,h3,h4,h5,h6').length).toBe(0);
    });

    it('labels each list by its own heading, through an identifier that resolves', () => {
      const lists = Array.from(host().querySelectorAll('ul'));

      expect(lists.length).toBe(EXPECTED_GROUPS.length);

      lists.forEach((list: Element, index: number): void => {
        const labelledBy = present(
          list.getAttribute('aria-labelledby'),
          `an aria-labelledby reference on list ${index}`,
        );
        const label = present(host().querySelector(`#${labelledBy}`), `a label with id ${labelledBy}`);

        // The element is deliberately NOT a heading — see the outline spec above. What matters
        // for the accessible name is that the reference RESOLVES and carries the group's wording,
        // and `aria-labelledby` resolves against any element with an id.
        expect(label.tagName).toBe('P');
        expect(normalise(label.textContent)).toBe(EXPECTED_GROUPS[index].label);

        // Namespaced with the published region identifier so it stays unique in the
        // document even though these values become global identifiers, and derived from
        // the group's own stable key rather than from its user-visible wording.
        expect(labelledBy).toBe(`${REGION_ID}-${EXPECTED_GROUPS[index].id}`);
      });
    });

    it('keeps every rendered identifier unique', () => {
      const identified = Array.from(host().querySelectorAll('[id]'));
      const identifiers = identified.map((element: Element): string => element.id);

      expect(new Set<string>(identifiers).size).toBe(identifiers.length);

      // The region and the four group headings, and nothing else.
      expect(identifiers.length).toBe(EXPECTED_GROUPS.length + 1);
    });

    it('renders each group heading immediately before its own list', () => {
      // The heading is a SIBLING of the list it labels rather than its ancestor, so the
      // adjacency is what a user without the accessible name relies on, and the paired
      // stylesheet spaces the two with a flex gap that assumes exactly this order.
      const children = Array.from(present(region(), 'a collapsible region').children);

      expect(children.length).toBe(EXPECTED_GROUPS.length * 2);

      EXPECTED_GROUPS.forEach((group: ExpectedGroup, index: number): void => {
        expect(children[index * 2].tagName).withContext(`group ${group.id} label`).toBe('P');
        expect(children[index * 2 + 1].tagName).withContext(`group ${group.id} list`).toBe('UL');
      });
    });

    it('renders each group with exactly its own destinations, in order', () => {
      // The strongest structural expectation in the file: it pins the grouping, the
      // order, the addresses and the labels TOGETHER. A rail that rendered every expected
      // address and every expected label but assigned them to the wrong groups would
      // satisfy each of those lists separately and is caught here.
      const lists = Array.from(host().querySelectorAll('ul'));

      EXPECTED_GROUPS.forEach((group: ExpectedGroup, index: number): void => {
        const anchors = Array.from(lists[index].querySelectorAll<HTMLAnchorElement>('a.app-sidebar__link'));

        expect(anchors.map((anchor: HTMLAnchorElement): string => anchor.getAttribute('href') ?? ''))
          .withContext(`addresses in group ${group.id}`)
          .toEqual(group.items.map((item: ExpectedItem): string => item.address));

        expect(anchors.map((anchor: HTMLAnchorElement): string => normalise(anchor.textContent)))
          .withContext(`labels in group ${group.id}`)
          .toEqual(group.items.map((item: ExpectedItem): string => item.label));
      });
    });
  });

  describe('rendered wording', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('renders exactly the expected labels, in order', () => {
      expect(renderedLabels()).toEqual(EXPECTED_LABELS);
    });

    it('renders the measured legacy screen titles verbatim', () => {
      // MIGRATION: these three are the labels attributable to a legacy resource entry
      // keyed `ControlTitle_.Text`, so a user of the legacy console recognises the
      // wording. They are asserted separately from the full list so the legacy-parity
      // claim is visible as its own expectation and cannot be weakened silently by a
      // rewording that still satisfies the list above.
      for (const title of MEASURED_LEGACY_TITLES) {
        expect(renderedLabels())
          .withContext(`the measured legacy title "${title}" is no longer rendered`)
          .toContain(title);
      }
    });

    it('renders the one net-new label, which no legacy resource key backs', () => {
      // MIGRATION: "Modules" is authored rather than ported. Every other in-scope legacy
      // administration tree carries a `ControlTitle_.Text` entry naming its collection
      // view — Portal in 5 files, Users in 2, Security in 1, Tabs in 2 — and the Modules
      // tree carries it in NONE, its only title keys being the module, export and import
      // ones. Named here so a later reader does not go looking for a key that never
      // existed.
      expect(renderedLabels()).toContain(NET_NEW_LABEL);
    });

    it('renders every label as plain text, never as markup', () => {
      // MIGRATION: legacy resource text is untrusted markup. Across the 37 resource files
      // of the five in-scope administration trees there are 1211 entries, 75 of which
      // carry HTML tags XML-escaped — so a search for the unescaped form finds nothing and
      // suggests the text is clean when it is not — and one file carries two complete
      // script elements. Interpolation escapes its content; the legacy label controls
      // rendered such values as live markup.
      for (const anchor of navAnchors()) {
        expect(anchor.children.length)
          .withContext(`${anchor.getAttribute('href')} rendered element content rather than text`)
          .toBe(0);
      }

      // Swept across the whole rendered tree rather than the anchors alone, because a
      // heading or the disclosure control could carry markup just as easily.
      const rendered = normalise(host().textContent);

      expect(rendered.includes('<')).withContext('rendered text must contain no markup delimiter').toBeFalse();

      // Both forms are checked, and the text content is the right oracle for BOTH. A
      // value that arrived carrying a live tag decodes to a plain delimiter in the text,
      // which the expectation above catches; a value that arrived XML-escaped — the form
      // 75 of the legacy resource entries actually use — survives interpolation with its
      // escape intact and appears in the text verbatim, which this one catches. Neither
      // is caught by looking only for the other.
      expect(rendered.includes('&lt;'))
        .withContext('rendered text must contain no escaped markup either')
        .toBeFalse();

      expect(rendered.includes('&#')).withContext('rendered text must carry no character reference').toBeFalse();
    });

    it('renders no empty label', () => {
      for (const label of renderedLabels()) {
        expect(label.length).toBeGreaterThan(0);
      }

      expect(normalise(present(landmark(), 'a navigation landmark').textContent).length).toBeGreaterThan(0);
    });

    it('offers no affordance for an excluded legacy subsystem', () => {
      // MIGRATION: eighteen of the twenty-three legacy administration trees produce no
      // entry here, and the five in scope produce all eight. The excluded subsystems are
      // asserted absent by their legacy user-facing wording, because a later change would
      // reach for the wording before it reached for a route.
      const rendered = normalise(host().textContent).toLowerCase();

      for (const excluded of EXCLUDED_LEGACY_WORDING) {
        expect(rendered)
          .withContext(`the excluded legacy subsystem "${excluded}" appeared in the rail`)
          .not.toContain(excluded);
      }
    });
  });

  describe('published contract', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('publishes the collapse state as a read-only projection', () => {
      // ⚠ Checked by the ABSENCE of the mutators, not by attempting a write: a write
      // attempt would need a widening cast to compile, and the cast would weaken exactly
      // the guarantee being tested. The published method is the only way the state
      // changes, which keeps every transition expressible in one place.
      expectReadOnlySignal(component.collapsed, 'collapsed');
    });

    it('publishes the navigation model matching what is rendered', () => {
      // Cross-checks the published model against the DOM, which is the only place a
      // divergence would be visible to a user. Asserting the model against itself would
      // prove nothing.
      const groups: readonly SidebarNavGroup[] = component.visibleNavigation();

      expect(groups.length).toBe(EXPECTED_GROUPS.length);

      const publishedAddresses = groups.flatMap((group: SidebarNavGroup): readonly string[] =>
        group.items.map((item): string => item.path),
      );

      expect(publishedAddresses).toEqual(EXPECTED_ADDRESSES);
      expect(publishedAddresses).toEqual(renderedAddresses());

      const publishedLabels = groups.flatMap((group: SidebarNavGroup): readonly string[] =>
        group.items.map((item): string => item.label),
      );

      expect(publishedLabels).toEqual(EXPECTED_LABELS);
      expect(publishedLabels).toEqual(renderedLabels());

      expect(groups.map((group: SidebarNavGroup): string => group.id)).toEqual(
        EXPECTED_GROUPS.map((group: ExpectedGroup): string => group.id),
      );
    });

    it('publishes the model and the resolution as read-only projections', () => {
      expectReadOnlySignal(component.visibleNavigation, 'visibleNavigation');
      expectReadOnlySignal(component.activePath, 'activePath');
    });

    it('publishes the same model instance to every reader while nothing has changed', () => {
      // ⚠ THE MODEL IS NOW DERIVED RATHER THAN CONSTANT, so this expectation earns its keep
      // twice over. The rail filters its declared model by the administration fact, and a
      // filter that re-allocated on every read would defeat the template's tracking
      // expressions and re-create every list item on every change-detection pass. A computed
      // projection caches, so two reads with nothing changed in between are the same object.
      expect(component.visibleNavigation()).toBe(component.visibleNavigation());
    });

    it('publishes a group key for every group, and keeps them unique', () => {
      const keys = component.visibleNavigation().map((group: SidebarNavGroup): string => group.id);

      expect(new Set<string>(keys).size).toBe(keys.length);

      for (const key of keys) {
        expect(key.length).toBeGreaterThan(0);
      }
    });
  });

  // =========================================================================
  // LINK VISIBILITY — THE FIVE ADMINISTRATION DESTINATIONS
  // =========================================================================
  describe('link visibility', () => {
    it('offers every destination to a caller that administers the tenant', async () => {
      await createComponent([], { hostAccount: true, administersTenant: true });

      expect(renderedAddresses()).toEqual(EXPECTED_ADDRESSES);
      expect(renderedLabels()).toEqual(EXPECTED_LABELS);
    });

    it('offers nothing at all to a caller that administers neither the tenant nor the host', async () => {
      // ⚠ THE WITHHOLDING IS THE POINT, AND IT IS NOT A CAPABILITY LOSS. Every destination this
      // rail offers declares a policy on the route it actually reaches, so following any of them
      // as this caller ends at the access-denied screen — the rail is not withholding a
      // capability, it is declining to promise one the application has already refused. This
      // console has no non-administrative screen to offer, so "only the ungated ones" is the
      // empty set and the landmark itself is withheld rather than left standing over nothing.
      await createComponent([], { hostAccount: false, administersTenant: false });

      expect(renderedAddresses()).toEqual([]);
      expect(component.visibleNavigation()).toEqual([]);
      expect(host().querySelector('nav'))
        .withContext('a landmark standing over nothing is worse than no landmark')
        .toBeNull();

      for (const address of [...HOST_ONLY_ADDRESSES, ...ADMINISTRATION_ONLY_ADDRESSES]) {
        expect(anchorFor(address))
          .withContext(`${address} must not be offered to a caller that administers nothing`)
          .toBeNull();
      }
    });

    it('drops a whole group when every one of its destinations is gated', async () => {
      // ⚠ ASSERTED FROM A TENANT ADMINISTRATOR WITH NO HOST ACCOUNT, which is the caller that
      // makes this observable at all. The tenant collection is the Portal group's ONLY
      // destination and it is host-only, so that one group empties while its three siblings
      // survive whole. A caller administering nothing empties every group, which proves the
      // landmark is withheld but proves nothing about dropping one group and keeping another.
      //
      // A heading standing over an empty list would be worse than either outcome: it announces a
      // region in the document outline that a reader can navigate to and find nothing in.
      await createComponent([], { hostAccount: false, administersTenant: true });

      const headings = Array.from(host().querySelectorAll('p.app-sidebar__group')).map(
        (heading: Element): string => normalise(heading.textContent),
      );

      expect(headings).toEqual(['Module', 'User', 'Role']);
      expect(host().querySelectorAll('.app-sidebar__list').length).toBe(3);

      // One heading per list, still, with no heading standing over nothing.
      expect(headings.length).toBe(host().querySelectorAll('.app-sidebar__list').length);
    });

    it('keeps every surviving group non-empty, with its own destinations in order', async () => {
      // Same caller as the case above, for the same reason: a partial filter is the only one that
      // can leave a SURVIVING group to make an assertion about.
      await createComponent([], { hostAccount: false, administersTenant: true });

      const lists = Array.from(host().querySelectorAll<HTMLElement>('.app-sidebar__list'));

      expect(lists.length).toBeGreaterThan(0);

      for (const list of lists) {
        expect(list.querySelectorAll('li.app-sidebar__item').length)
          .withContext('a rendered list must carry at least one destination')
          .toBeGreaterThan(0);
      }

      expect(renderedAddresses()).toEqual(TENANT_ADMINISTRATION_ADDRESSES);
    });

    it('follows a change in administration without being recreated', async () => {
      // The determination arrives asynchronously — the current-account read lands after the
      // rail has already rendered — so the rail must widen when it does. Asserted in both
      // directions, because a revoked administrator must lose the entries just as promptly.
      await createComponent([], { hostAccount: false, administersTenant: false });

      expect(renderedAddresses()).toEqual([]);

      setAdministration(true);

      // The tenant set, not the whole rail: this caller holds no host account, so the tenant
      // collection stays withheld however its tenant administration moves.
      expect(renderedAddresses()).toEqual(TENANT_ADMINISTRATION_ADDRESSES);

      setAdministration(false);

      expect(renderedAddresses()).toEqual([]);
    });

    it('gates exactly the destinations whose own route declares the administration policy', async () => {
      // ⚠ THE CROSS-CHECK THAT MAKES THE GATE CORRECT RATHER THAN MERELY PRESENT, and it is
      // stated against the real route tables in both directions. Every gated entry must
      // declare the policy — otherwise the rail hides a destination the router would have
      // served — and every ungated entry must declare none — otherwise the rail offers a
      // destination the router will refuse.
      await createComponent([], { hostAccount: true, administersTenant: true });

      for (const address of ADMINISTRATION_ONLY_ADDRESSES) {
        expect(declaredPolicyFor(address))
          .withContext(`${address} is gated by the rail, so its route must declare the policy`)
          .toBe(ADMINISTRATION_POLICY);
      }

      for (const address of HOST_ONLY_ADDRESSES) {
        expect(declaredPolicyFor(address))
          .withContext(`${address} is host-only in the rail, so its route must declare the host policy`)
          .toBe(HOST_POLICY);
      }

      for (const address of CONTENT_EDIT_ADDRESSES) {
        expect(declaredPolicyFor(address))
          .withContext(`${address} is content-gated by the rail, so its route must declare that policy`)
          .toBe(CONTENT_EDIT_POLICY);
      }

      for (const address of UNGATED_ADDRESSES) {
        expect(declaredPolicyFor(address))
          .withContext(`${address} is offered to everyone, so its route must declare no policy`)
          .toBeNull();
      }
    });

    it('accounts for every rendered destination in exactly one of the four sets', () => {
      // Guards the SUITE rather than the component: a destination added to the rail but to
      // none of the sets would escape every arm of the cross-check above, and this states the
      // invariant that keeps the lists exhaustive. FOUR sets rather than two, and each split is
      // load-bearing: the tenant collection is HOST-only and a tenant administrator is refused
      // it, and module creation is CONTENT-gated and a caller who administers nothing may still
      // reach it. Folding either in with the tenant-administration entries would assert the
      // wrong policy for it.
      const combined = [
        ...UNGATED_ADDRESSES,
        ...HOST_ONLY_ADDRESSES,
        ...ADMINISTRATION_ONLY_ADDRESSES,
        ...CONTENT_EDIT_ADDRESSES,
      ];

      expect(new Set<string>(combined).size).toBe(combined.length);
      expect([...combined].sort()).toEqual([...EXPECTED_ADDRESSES].sort());

      // And the derived set a tenant administrator sees is the whole rail less the host-only
      // entries, stated here so the cases below can rely on it.
      expect([...TENANT_ADMINISTRATION_ADDRESSES].sort()).toEqual(
        [...EXPECTED_ADDRESSES].filter((address) => !HOST_ONLY_ADDRESSES.includes(address)).sort(),
      );
    });

    it('names no gated destination anywhere in the document while withholding it', async () => {
      // Withheld means ABSENT, not hidden. An entry left in the document and merely
      // unstyled is still in the accessibility tree and still reachable by keyboard, so the
      // omission is asserted against the whole rendered markup rather than against the
      // anchor list alone.
      await createComponent([], { hostAccount: false, administersTenant: false });

      const markup = host().innerHTML;

      for (const address of [...ADMINISTRATION_ONLY_ADDRESSES, ...CONTENT_EDIT_ADDRESSES]) {
        expect(markup)
          .withContext(`${address} must not appear in the document at all`)
          .not.toContain(address);
      }

      expect(host().querySelectorAll('[hidden]').length).toBe(0);
      expect(host().querySelectorAll('[aria-hidden="true"]').length).toBe(0);
    });

    it('still announces the current page correctly while destinations are withheld', async () => {
      // The resolution must consider only what is RENDERED. A resolution computed over the
      // declared model would mark a withheld entry current, which is an announcement pointing
      // at an element that is not in the document.
      //
      // ⚠ ASSERTED FROM A TENANT ADMINISTRATOR WITH NO HOST ACCOUNT, and the pairing is what
      // makes the case say something. That caller is offered every tenant-administration entry
      // and withheld the host-only tenant collection, so one address is rendered and resolvable
      // while another is withheld and must not resolve — both halves in one component. A caller
      // administering nothing renders no entry at all, so the second half would be unobservable;
      // and a HOST account is offered everything, because holding the host account satisfies the
      // tenant policy too, so nothing would be withheld from it to assert about.
      await createComponent(NAVIGABLE_ROUTES, { hostAccount: false, administersTenant: true });
      await navigateTo('/portals/0/settings');

      expect(component.activePath())
        .withContext('a withheld entry must never be resolved as the current one')
        .toBeNull();
      expect(currentPageElements().length).toBe(0);

      await navigateTo('/users/7/profile');

      expect(component.activePath()).toBe('/users');

      // Marked, because the reader is inside that branch — and marked as `true` rather than
      // `page`, because a profile screen is not the collection the entry addresses.
      expect(markedElements().length).toBe(1);
      expect(currentPageElements().length).toBe(0);
      expect(present(anchorFor('/users'), 'the user accounts anchor').getAttribute('aria-current')).toBe(
        'true',
      );
    });
  });

  describe('architectural boundaries', () => {
    beforeEach(async () => {
      await createComponent([]);
    });

    it('constructs and renders with nothing but the router and the authentication store', () => {
      // The provider set for this whole file is one call to `provideRouter` and one store
      // double exposing a single member. A rail that injected a feature service, a
      // host-settings reader, an HTTP client or a notification surface would fail to
      // construct here rather than fail subtly later — so every expectation in this file
      // passing at all is the evidence of how narrow the rail's dependency surface is.
      expect(component).toBeInstanceOf(SidebarComponent);
      expect(navAnchors().length).toBe(EXPECTED_ADDRESSES.length);
    });

    it('has no HTTP client available to it, and needs none', () => {
      // Asserted rather than assumed, and still true after the rail took a store dependency:
      // the double satisfies it without a transport, so requesting a client optionally
      // yields nothing — and the rail rendered its full contents regardless, which is the
      // strongest available proof that it issues no request. Angular services communicate
      // with the API; a layout primitive does not, and it does not read a store that would
      // make it do so on its behalf either.
      expect(TestBed.inject(HttpClient, null, { optional: true })).toBeNull();
      expect(renderedAddresses()).toEqual(EXPECTED_ADDRESSES);
    });

    it('declares no provider of its own', () => {
      // A layout primitive that provided anything would give each of its instances a
      // private copy and quietly diverge from the rest of the screen. Observable as
      // identity: the component's own injector resolves the SAME router the application
      // injector holds, so nothing is shadowed at the component level.
      expect(fixture.componentRef.injector.get(Router)).toBe(TestBed.inject(Router));
    });

    it('keeps each instance independent, holding no shared state', () => {
      // MIGRATION: the collapse state lives in the browser, per instance. The legacy
      // equivalent was round-tripped through view state, and a module-level variable
      // here would be the modern version of that same mistake — one rail's collapse
      // would silently move another's.
      const second = TestBed.createComponent(SidebarComponent);

      // The shell arrangement is stated for the second instance exactly as `createComponent`
      // states it for the first, and for the same reason: the rail's DEFAULT disclosure state is
      // derived from the arrangement its stylesheet resolves, so an instance left to infer it
      // from the runner's frame width would differ from its twin for a reason that has nothing
      // to do with shared state.
      pinArrangementOf(second, true);

      // All THREE authority inputs are required, so a second instance must state them too — the
      // same authority as the first, so any difference observed below is the collapse state
      // and nothing else.
      second.componentRef.setInput('hostAccount', true);
      second.componentRef.setInput('administersTenant', true);
      second.componentRef.setInput('editsContent', false);
      second.detectChanges();

      component.toggleCollapsed();
      fixture.detectChanges();

      expect(component.collapsed()).toBeTrue();
      expect(second.componentInstance.collapsed()).toBeFalse();

      second.destroy();
    });

    it('renders identically for a second independent instance', () => {
      // The determinism guard. Nothing in the rail reads a clock, draws a random value or
      // schedules a timer, so two instances rendered in the same run must produce exactly
      // the same addresses, labels and initial state. A hidden non-deterministic input
      // would show up here as a difference.
      const second = TestBed.createComponent(SidebarComponent);

      // The arrangement is stated identically too, so a difference in initial disclosure state
      // below would be genuine non-determinism rather than two instances resolving two
      // different breakpoint branches.
      pinArrangementOf(second, true);

      // All three stated identically to the first instance, so the comparison below is about
      // determinism rather than about two differently authorised callers.
      second.componentRef.setInput('hostAccount', true);
      second.componentRef.setInput('administersTenant', true);
      second.componentRef.setInput('editsContent', false);
      second.detectChanges();

      const secondHost = second.nativeElement as HTMLElement;
      const secondAnchors = Array.from(secondHost.querySelectorAll<HTMLAnchorElement>('a.app-sidebar__link'));

      expect(secondAnchors.map((anchor: HTMLAnchorElement): string => anchor.getAttribute('href') ?? '')).toEqual(
        renderedAddresses(),
      );
      expect(secondAnchors.map((anchor: HTMLAnchorElement): string => normalise(anchor.textContent))).toEqual(
        renderedLabels(),
      );
      expect(second.componentInstance.collapsed()).toBe(component.collapsed());

      second.destroy();
    });

    it('withholds a destination rather than offering it in a refused-looking state', () => {
      // ⚠ THIS REPLACED A TEST THAT ASSERTED THE OPPOSITE. The previous expectation —
      // "applies no permission gate to any destination" — pinned the rail as
      // unconditionally complete, on the reasoning that hiding an entry the guard would
      // have allowed silently removes a capability whereas showing one it refuses only
      // costs a redirect. That is answered by WHERE the authority comes from rather than by
      // declining to filter: the rail reads the server's own published facts, so an entry
      // disappears only when the API has already said it would refuse the screen. See the
      // filtering group below for the behaviour itself.
      //
      // What remains true, and is asserted here, is HOW a withheld entry is withheld. A
      // disabled or aria-disabled link would announce a command that is momentarily
      // unavailable, which is a false promise for a screen this caller cannot use at all,
      // and it would leave the entry point named in the document for a caller who should
      // not see it. Withholding means the element is not rendered.
      const anchors = navAnchors().length;

      expect(anchors).toBe(EXPECTED_ADDRESSES.length);
      expect(host().querySelectorAll('[aria-disabled]').length).toBe(0);
      expect(host().querySelectorAll('a[disabled]').length).toBe(0);

      fixture.componentRef.setInput('hostAccount', false);
      fixture.componentRef.setInput('administersTenant', false);
      fixture.detectChanges();

      expect(navAnchors().length).toBe(0);
      expect(host().querySelectorAll('[aria-disabled]').length).toBe(0);
      expect(host().querySelectorAll('a[disabled]').length).toBe(0);

      for (const key of PERSISTED_PERMISSION_KEYS) {
        expect(host().innerHTML).not.toContain(key);
      }
    });

    it('declares, for every entry, the authority its own API endpoint requires', () => {
      // ⚠ THE DECLARED MODEL, ASSERTED ENTRY BY ENTRY AGAINST THE SERVER. `navigation` is
      // the declaration and `visibleNavigation` is one caller's view of it; this asserts the
      // declaration, because an entry whose authority is wrong is wrong for every caller and
      // is not visible as a rendering difference. The expectations are transcribed from the
      // controller attributes cited in the table above, so this compares the rail to the
      // server rather than to itself.
      //
      // ⚠ AN ENTRY WITH NO AUTHORITY IS THE FAILURE THIS CATCHES. The field is required, so
      // a missing one does not compile — but a NEW entry added with a plausible-looking
      // wrong policy would, and it would present as a link offered to the wrong operators.
      const declared = component.navigation.flatMap((group) =>
        group.items.map((item) => ({ address: item.path, policy: item.policy })),
      );
      const expected = EXPECTED_GROUPS.flatMap((group) =>
        group.items.map((item) => ({ address: item.address, policy: item.policy })),
      );

      expect(declared).toEqual(expected);
    });

    it('cleans up without error when destroyed', () => {
      // The rail holds a router-event subscription of its own — the current address is read
      // from the event stream and bound to the injection context's destruction — so a rail
      // destroyed and recreated must not leave one behind. A leaked subscription surfaces as
      // a console error on the next event, which the shared expectation on console output
      // would then catch.
      expect((): void => {
        fixture.destroy();
      }).not.toThrow();
    });
  });

  // ===========================================================================
  // PERMISSION-AWARE VISIBILITY
  // ===========================================================================
  //
  // ⚠ THE GROUP THAT DID NOT EXIST, AND WHOSE ABSENCE WAS THE DEFECT. The rail advertised
  // every administration entry point to every caller — including, because the application
  // root projected it unconditionally, to a caller with no session on the sign-in screen and
  // on the not-found screen. The condition on the projection lives in
  // `app.component.spec.ts`; what belongs here is the other half: that a rail which IS
  // rendered offers only what its caller can use.
  //
  // ⚠ EVERY CASE STATES THE AUTHORITY EXPLICITLY, and both facts, because the interesting
  // failures live in the combinations rather than in one flag. The two are kept SEPARATE
  // rather than pre-combined into "can administer" precisely so the host-only entry can be
  // told apart from the tenant-scoped ones.
  describe('permission-aware visibility', () => {
    /**
     * The addresses a caller with the stated authority is offered, in presentation order.
     *
     * The testing module is reset first so that one test may examine SEVERAL callers. A
     * testing module cannot be reconfigured once a component has been created from it, so
     * without the reset the second caller in a single test raises "Cannot configure the test
     * module when the test module has already been instantiated" — and comparing callers
     * within one test is the only way to assert that filtering only ever removes.
     */
    async function addressesFor(authority: RailAuthority): Promise<readonly string[]> {
      TestBed.resetTestingModule();
      await createComponent([], authority);

      return renderedAddresses();
    }

    /** The group headings a caller with the stated authority is offered. */
    function renderedGroupHeadings(): readonly string[] {
      return Array.from(host().querySelectorAll<HTMLElement>('p.app-sidebar__group')).map(
        (heading: HTMLElement): string => normalise(heading.textContent),
      );
    }

    it('offers module creation to a page editor who administers nothing at all', async () => {
      // ⚠ THE POSITIVE CONTROL FOR THE DISCOVERABILITY FINDING, and the case that cannot be
      // replaced by any of the withholding cases above. Before this entry existed, the ONLY link
      // to the create screen sat inside the module listing, which is administration-gated - so a
      // caller holding EDIT on one of the tenant's pages, whom `PortalContentEditor` admits and
      // whom the server would serve, was offered nothing anywhere and had to guess the address.
      //
      // This caller administers NOTHING: no host account, no tenant administration. Every other
      // entry is therefore correctly withheld, which is what makes the one offered entry
      // meaningful rather than a side effect of a widened rail.
      expect(
        await addressesFor({ hostAccount: false, administersTenant: false, editsContent: true }),
      ).toEqual(CONTENT_EDIT_ADDRESSES);
    });

    it('emits the navigation landmark for a page editor, because one entry survives', async () => {
      // The landmark is withheld entirely when nothing is offered, so a caller offered exactly one
      // entry is the boundary case: the group and its list must both be present, or the single
      // surviving entry would be unreachable.
      await createComponent([], {
        hostAccount: false,
        administersTenant: false,
        editsContent: true,
      });

      expect(host().querySelectorAll('nav').length).toBe(1);
      expect(renderedGroupHeadings()).toEqual(['Module']);
      expect(host().querySelectorAll('li.app-sidebar__item').length).toBe(1);
    });

    it('withholds module creation from a caller holding the key nowhere', async () => {
      // The negative half, and the reason the fact is read rather than assumed. A signed-in
      // caller who administers nothing AND holds no grant is offered nothing, landmark included.
      expect(
        await addressesFor({ hostAccount: false, administersTenant: false, editsContent: false }),
      ).toEqual([]);
    });

    it('offers module creation to a tenant administrator carrying no permission key', async () => {
      // ⚠ THE ARM THAT WOULD BE MISSING IF THE KEY ALONE WERE READ, and it is a measured hazard
      // rather than a hypothetical. `PermissionEvaluator.ListEffectivePortalPermissionKeysAsync`
      // builds the advisory key list from GRANT ROWS ALONE and has no administrator arm, while the
      // server's own handler (`PermissionService.HasAnyTabPermissionInPortalAsync`) asks its
      // administration question FIRST. So a tenant administrator whose pages carry no explicit
      // grants holds the policy and carries no key - and gating this entry on the key alone would
      // hide it from the tenant's own administrator.
      const addresses = await addressesFor({
        hostAccount: false,
        administersTenant: true,
        editsContent: false,
      });

      expect(addresses).toContain('/modules/new');
      expect(addresses).toEqual(TENANT_ADMINISTRATION_ADDRESSES);
    });

    it('adds nothing for a host account, whose entries are already complete', async () => {
      // The third fact must WIDEN and never reorder or duplicate. A host account is already
      // offered every entry, so raising the key changes nothing at all - and an entry appearing
      // twice, or the module group re-ordering, would show up here as an inequality.
      expect(
        await addressesFor({ hostAccount: true, administersTenant: true, editsContent: true }),
      ).toEqual(EXPECTED_ADDRESSES);
    });

    it('follows a change in the content-edit fact without being recreated', async () => {
      // The determination arrives asynchronously with the current-account read, so the rail must
      // widen when it lands. Asserted in both directions: a caller whose last grant is removed
      // must lose the entry just as promptly.
      await createComponent([], { hostAccount: false, administersTenant: false });

      expect(renderedAddresses()).toEqual([]);

      fixture.componentRef.setInput('editsContent', true);
      fixture.detectChanges();

      expect(renderedAddresses()).toEqual(CONTENT_EDIT_ADDRESSES);

      fixture.componentRef.setInput('editsContent', false);
      fixture.detectChanges();

      expect(renderedAddresses()).toEqual([]);
    });

    it('offers a host account every declared destination', async () => {
      // A host account satisfies tenant administration as well
      // (`PolicyNames.cs:L105-L113`), so it is the one caller for whom the rendered rail and
      // the declared model coincide. A host that was NOT offered the tenant-scoped entries
      // would be withheld screens the server admits it to, which is the failure direction the
      // old unfiltered rail could not have.
      expect(await addressesFor({ hostAccount: true, administersTenant: false })).toEqual(
        EXPECTED_ADDRESSES,
      );
    });

    it('withholds the tenant collection from a tenant administrator, because the API does', async () => {
      // `GET /api/v1/portals` requires host authority (`PortalsController.cs:L241`) — the
      // collection addresses no single tenant, and `PolicyNames.cs:L120-L130` records that
      // deciding it against the arrival tenant would let an administrator of one tenant
      // enumerate every tenant. So this operator is genuinely refused the listing, and a rail
      // that offered it would be advertising a 403.
      //
      // ⚠ THE WHOLE PORTAL GROUP GOES, not just its entry. A heading over an empty list would
      // still be announced as a named navigation list containing nothing, because the list
      // carries `aria-labelledby` pointing at that heading.
      const addresses = await addressesFor({ hostAccount: false, administersTenant: true });

      expect(addresses).not.toContain('/portals');
      expect(renderedGroupHeadings()).toEqual(['Module', 'User', 'Role']);
      expect(addresses).toEqual([
        '/modules',
        // Offered to this caller through the policy's ADMINISTRATION arm, not through a grant:
        // this operator carries no permission key at all in this case.
        '/modules/new',
        '/modules/import',
        '/users',
        '/settings/membership',
        '/settings/profile-definitions',
        '/roles',
        '/role-groups/new',
      ]);
    });

    it('offers a caller with no administration nothing at all, and no empty frame either', async () => {
      // Every declared entry requires one of the two administration authorities, so a
      // signed-in caller holding neither has no entry point in this rail — which is the
      // truthful answer rather than an unhelpful one: all eight destinations would refuse
      // them.
      expect(await addressesFor({ hostAccount: false, administersTenant: false })).toEqual([]);
      expect(renderedGroupHeadings()).toEqual([]);
      expect(host().querySelectorAll('ul.app-sidebar__list').length).toBe(0);

      // ⚠ AND THE LANDMARK GOES WITH THEM, which is a correction to an earlier reading of
      // this case rather than an extra assertion. The wrapper used to be emitted whatever the
      // projection held, on the reasoning that an empty navigation region is a coherent state.
      // Observed in a browser it is not: a named "Administration" landmark containing one
      // disclosure button that reveals an empty region is offered to a screen-reader user
      // orienting by landmark list, and a small bordered "Navigation" box occupies the shell's
      // left column for a sighted one — an affordance leading nowhere in both cases. Nothing
      // was ever disclosed by it, so this is a usability correction and not a security one.
      expect(landmark()).withContext('no navigation landmark is emitted').toBeNull();
      expect(toggle()).withContext('and no disclosure control for an absent region').toBeNull();
      expect(host().querySelectorAll('nav').length).toBe(0);
    });

    it('emits the landmark again the moment ONE entry is admitted', async () => {
      // The other side of the condition, so "withheld when empty" cannot be satisfied by a
      // rail that is never emitted at all. A single admitted entry is enough.
      await createComponent([], { hostAccount: false, administersTenant: false });

      expect(host().querySelectorAll('nav').length).toBe(0);

      fixture.componentRef.setInput('administersTenant', true);
      fixture.detectChanges();

      expect(host().querySelectorAll('nav').length).toBe(1);
      expect(landmark()?.getAttribute('aria-label')).toBe(LANDMARK_NAME);
      expect(navAnchors().length).toBeGreaterThan(0);
    });

    it('follows the authority within one page load, without being recreated', async () => {
      // ⚠ THE STALENESS CASE. The identity can change while the application is running — a
      // renewal re-reads the caller's authority, and an administrator can be demoted — and
      // the rail is NOT recreated for that. A model captured once into a field would keep
      // advertising the previous authority indefinitely, so the projection is computed and
      // the component renders on-push, which is what makes the change observable at all.
      await createComponent([], { hostAccount: false, administersTenant: true });

      expect(renderedAddresses()).not.toContain('/portals');

      fixture.componentRef.setInput('hostAccount', true);
      fixture.detectChanges();

      expect(renderedAddresses()).toEqual(EXPECTED_ADDRESSES);

      fixture.componentRef.setInput('hostAccount', false);
      fixture.componentRef.setInput('administersTenant', false);
      fixture.detectChanges();

      expect(renderedAddresses()).toEqual([]);
    });

    it('publishes the filtered projection read-only, so no consumer can widen it', () => {
      // The rail's own view of what it may offer must not be assignable from outside: a
      // consumer that could write it could restore every entry for a caller the server
      // refuses, and would do so without touching the authority the projection derives from.
      expectReadOnlySignal(component.visibleNavigation, 'visibleNavigation');
    });

    it('never invents a destination the declared model does not carry', async () => {
      // Filtering may only REMOVE. A projection that mapped, re-labelled or substituted an
      // entry would put an address in the rail that no declaration authorised and that
      // nothing else in this file asserts against, so every rendered address is checked to be
      // a member of the declared set for each of the three authority combinations.
      for (const authority of [
        { hostAccount: true, administersTenant: true },
        { hostAccount: false, administersTenant: true },
        { hostAccount: false, administersTenant: false },
      ]) {
        const addresses = await addressesFor(authority);

        for (const address of addresses) {
          expect(EXPECTED_ADDRESSES)
            .withContext(`"${address}" is not a declared destination`)
            .toContain(address);
        }
      }
    });

    it('issues no request in order to decide what to offer', async () => {
      // ⚠ THE EXECUTABLE FORM OF "THIS RAIL IS NOT AN AUTHORISATION ENGINE". The two facts
      // arrive as inputs, already resolved from what the server published; a rail that
      // fetched a permission catalogue to interpret would be building a second verdict that
      // then had to be kept in step with the API's. No HTTP client is provided at all, so a
      // rail that injected one — directly, or transitively through a store — would fail to
      // construct here rather than merely failing an expectation.
      await addressesFor({ hostAccount: false, administersTenant: true });

      expect(TestBed.inject(HttpClient, null, { optional: true })).toBeNull();
    });
  });
});
