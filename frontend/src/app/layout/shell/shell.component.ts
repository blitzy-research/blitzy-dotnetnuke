import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  booleanAttribute,
  inject,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { environment } from '../../../environments/environment';
import { FooterComponent } from '../footer/footer.component';
import { HeaderComponent } from '../header/header.component';
import { NotificationListComponent } from '../notifications/notification-list.component';

/**
 * The fragment identifier the skip link targets, and therefore the identifier the
 * main region must carry.
 *
 * Held as two derived constants from one source so that the link target and the
 * element identifier cannot drift apart. A skip link whose fragment names no
 * element is worse than no skip link: it is announced, it is reachable, and
 * activating it does nothing.
 */
const MAIN_REGION_ID = 'main-content';

/**
 * The skip link's `href`.
 */
const SKIP_LINK_TARGET = `#${MAIN_REGION_ID}`;

/**
 * The application shell.
 *
 * Composes the fixed regions of every administration screen — skip link, banner,
 * secondary navigation and footer — around the routed outlet, and is the only
 * component in the workspace that renders a router outlet.
 *
 * ## Why this file is the contract hinge
 *
 * `app.component.ts` imports this class by name and `app.component.html` mounts it
 * as its single element, so two things here are load-bearing and fail
 * independently of one another: the exported class must be named `ShellComponent`,
 * and the selector must be `app-shell`. Strict template checking is enabled, so a
 * wrong selector is a compile error in the root template rather than a silent
 * no-op, and a wrong class name is a module-resolution error.
 *
 * The root template deliberately emits no wrapper element. That keeps this
 * component's host a direct child of the root's host, which is what preserves the
 * parent-child relationship the shell grid depends on, and it avoids duplicating a
 * landmark.
 *
 * ## SELECTOR CONTRACT
 *
 * The grid itself is published GLOBALLY by `src/styles/_layout.scss`, not by this
 * component's own stylesheet, because a `:host`-scoped grid cannot place children
 * that the global partial names. `shell.component.scss` therefore compiles to only
 * three rules, and each of the three is a statement about this component's markup:
 *
 * ```text
 * :host(:not(.shell)) { display: block }   the fallback when the host class is missing
 * .shell__skip-link   { color: … }         the skip link's foreground on the primary fill
 * main                { min-inline-size: 0; overflow-wrap: break-word }
 * ```
 *
 * The first of those three is the reason this component declares
 * `host: { class: 'shell' }`. The global partial keys the entire grid off the
 * `.shell` class, so a host without it would lay out as a plain block and every
 * region would stack in source order at full width regardless of viewport. The
 * class is applied through the component's own host metadata rather than left to
 * whichever parent happens to mount it, so the shell cannot be mounted wrongly.
 *
 * The third rule is why the main region is a native `<main>` element rather than a
 * generic element carrying an explicit main role: the stylesheet selects the bare
 * element name.
 *
 * ## PRESENTATIONAL BY CONSTRUCTION
 *
 * The shell injects no service, issues no request, holds no transfer object and
 * performs no data access. The session facts the banner needs — the signed-in
 * account's display name, whether a sign-out is in flight — arrive as inputs and
 * are forwarded straight through, and the sign-out gesture is re-emitted upward
 * unchanged. That is a deliberate scope decision. A layout that injected the
 * authentication surface to obtain one display name would couple every screen
 * beneath it to that surface. Because the shell forwards rather than resolves, the
 * session is wired in exactly one place — the component that mounts the shell —
 * with no change to this file.
 *
 * The single output is an Angular event emitter used as an output, which is the
 * framework's declarative parent-notification mechanism. It is not a state
 * container: nothing in this component subscribes to it, reads a current value
 * from it, or stores anything in it. Client state elsewhere in the workspace is
 * held in signals.
 *
 * ## ACCESSIBILITY
 *
 * Three landmarks are rendered and each is a native element, so none needs an
 * explicit role: `<header>` from the banner component, `<main>` here, `<footer>`
 * from the footer component. The skip link is the first focusable thing in the
 * document, is visually hidden until focused — the global partial does that with
 * `:not(:focus-visible)` rather than `display: none`, so it stays in the tab order
 * — and moves focus into the main region, which carries `tabindex="-1"` so that it
 * can receive programmatic focus without joining the tab order itself.
 *
 * The whole of this accessibility layer is net-new. The legacy checkout contains no
 * skip link, no accessibility attribute of any kind, no explicit role on any page
 * or user control, and no main element — so none of it is a translation of
 * something that existed, and none of it has any visual cost.
 *
 * ## ⚠⚠⚠ THE SECONDARY NAVIGATION SEAM — READ BEFORE CHANGING THIS FILE
 *
 * The navigation region is published here as a projection slot. The paired template
 * renders `<div class="shell__sidebar"><ng-content /></div>`, so the navigation rail
 * is supplied as projected content by whichever component mounts `<app-shell>`,
 * rather than being imported by the shell itself. That arrangement keeps the shell
 * free of any knowledge of what navigation exists, and it is the arrangement the
 * rail's own published contract specifies.
 *
 * The rail that belongs in this slot is `SidebarComponent`, selector `app-sidebar`,
 * at `../sidebar/sidebar.component`. It is standalone, takes no required input and
 * emits nothing, so projecting it needs no binding:
 *
 * ```text
 * <app-shell …><app-sidebar /></app-shell>
 * ```
 *
 * ⚠ It is NOT imported here, and the omission is deliberate and measured rather
 * than an oversight. Two independent reasons, either of which is sufficient:
 *
 * 1. The rail's own contract assigns the mounting decision to the component that
 *    mounts the shell, not to the shell. Importing it here would contradict the
 *    published contract of a component this file does not own.
 * 2. At the time of writing, the rail declares a template and a stylesheet that do
 *    not yet exist on disk. Referencing the class pulls it into the compilation
 *    graph, and the build then fails on the unresolved template with a cascading
 *    error reported against this file's import list. That was verified by building,
 *    not inferred. Adding the reference before those two files land would publish a
 *    workspace that does not compile.
 *
 * Consequently the region renders empty today, and the stylesheet collapses it with
 * `.shell__sidebar:empty { display: none }` so an empty region contributes no width
 * and is not announced as a nameless landmark. Two specifications assert that empty
 * default. Whoever supplies the rail must project it from the mounting component and
 * update those expectations in the same change; it is a one-element edit to the
 * mounting template plus a class reference in the mounting component, and this file
 * needs no edit at all.
 *
 * ## MIGRATION RECORD
 *
 * Every divergence from the legacy page chrome is recorded here rather than
 * silently absorbed. Line references were read from the checkout, not carried over
 * from a summary; the three legacy sources are `Website/Default.aspx` (30 lines),
 * `Website/Default.aspx.vb` (700 lines) and the only complete skin,
 * `Website/Portals/_default/Skins/MinimalExtropy/index.ascx` (126 lines).
 *
 * MIGRATION: run-time dynamic layout selection is replaced by one static,
 * compile-time composition. The legacy chrome was not a component at all.
 * `Default.aspx` declared a single placeholder (L25) and `Default.aspx.vb` filled
 * it per request: `LoadSkin` (L217-L243) stripped the application path from the
 * configured path (L221-L222), instantiated the control with
 * `LoadControl("~" & SkinPath)` (L224) and then called `DataBind()` (L226) — the
 * comment at L225 is explicit that this executes "any server logic in the skin", so
 * the layout was executable code and not merely markup. L240 let the package
 * override the page doctype, and L590 added the instantiated control to the
 * placeholder. None of that survives: there is no run-time layout selection, no
 * per-portal chrome, no package registry, no theme selector and no dynamic control
 * loading. Skinning, containers and skin objects are all out of scope. Note also
 * that this generation of the product used skins rather than master pages — the
 * checkout contains zero master-page files — so no master-page-to-layout mapping
 * was owed in the first place.
 *
 * MIGRATION: the run-time, cache-backed, ordered stylesheet cascade is replaced by
 * a single build-time bundle. `ManageStyleSheets` (L355-L408) read a cached table
 * keyed `"CSS"` (L360) and appended, in order, the host default marked
 * `' default style sheet ( required )'` (L366-L368), the package stylesheet probed
 * for existence, the layout-file stylesheet, and finally the per-portal stylesheet
 * from the portal's home directory (L402-L406) — writing the cache only when the
 * host performance setting permitted. It ran TWICE per request, straddling the
 * layout injection: L587 before, L590 the injection, L593 after, L596 the favicon.
 * The replacement is one ordered entry point resolved at build time, plus
 * component-scoped stylesheets, so the cascade is fixed at compile time and no
 * request participates in assembling it.
 *
 * MIGRATION: view state and the postback form are eliminated. `Default.aspx`
 * wrapped the page in a single multipart form (L23, `ENCTYPE="multipart/form-data"`,
 * `autocomplete="off"`, `style="height: 100%"`) containing a diagnostic label (L24)
 * and two hidden inputs (L26 `ScrollTop`, L27 `__dnnVariable`) whose only purpose
 * was to carry scroll position and client variables across postbacks. There is no
 * form here, no hidden input and no round trip. Scroll position is restored by the
 * router's own in-memory scrolling, configured once where the router is provided —
 * this component deliberately implements none of its own.
 *
 * MIGRATION: the table-based pane layout is replaced by a grid. The skin laid its
 * content out with a full-width table (L83-L100) holding five server-side cells —
 * a top pane spanning three columns (L85), left, content and right panes
 * (L89/L91/L93) and a bottom pane spanning three columns (L97) — every one of them
 * `visible="false"` by default, each band wrapped in a three-element sliding-doors
 * idiom, and the float context reset by three clearing elements (L55, L73, L121).
 * All of it is deleted rather than translated: the grid places the regions and the
 * clearing elements have no counterpart, which is the "delete the workaround, adopt
 * the primitive" rule applied literally.
 *
 * MIGRATION: the secondary navigation region is NET-NEW information architecture,
 * and the evidence matters because the opposite assumption is so natural. The only
 * complete skin put its menu at L42-L47 as a navigation control declared
 * `ControlOrientation="Horizontal"`, nested at L38-L41 four elements deep inside the
 * header band and sharing that band with the search control at L49 — a horizontal
 * bar, not a rail. A second horizontal navigation surface rendered a root-level link
 * list in the footer band at L109. The decisive evidence is L89: the left pane was a
 * table cell inside the content table, hidden by default, that an administrator
 * could drop modules into — a content pane, never a navigation region. So the
 * vertical rail this shell publishes a region for corresponds to nothing in the
 * legacy layout; it is a decision of this migration.
 *
 * MIGRATION: the legacy menu control itself is not reproduced. It was rendered
 * through a swappable navigation provider, configured by ELEVEN separate node-class
 * attributes plus a child-indicator setting, with fly-out submenu behaviour. The
 * navigation-provider family and the menu control library are both excluded, so
 * none of that is carried over: no provider indirection, no per-node class
 * vocabulary, no fly-out submenus and no child indicators.
 *
 * MIGRATION: the excluded skin-object vocabulary is deliberately not composed. The
 * skin declared a control-panel slot (L17) and the language, logo, navigation,
 * search, breadcrumb, links, privacy, terms, copyright, user, login, text and style
 * objects, closing at L126 with a conditional stylesheet served only to browsers
 * older than a 2006-era release. The checkout carries 24 such objects as user
 * controls under the administration tree and none is ported; the shell composes a
 * banner, a navigation region, a main region and a footer, and nothing else.
 *
 * MIGRATION: the document envelope is reallocated, not dropped. The doctype literal
 * (`Default.aspx` L3), the html element's run-time attributes (L4), the TWELVE meta
 * elements (L6-L17), the empty script-disabled element (L22), the stylesheet
 * injection points (L18-L19) and the favicon — resolved per portal, cached under a
 * portal-scoped key and injected as a shortcut-icon link by `ManageFavicon`
 * (L410-L427, the link written at L422) — together with the package doctype
 * override in `SetSkinDoctype` (L255), all belonged to the layout at run time. They
 * now belong to the single static document and its static icon asset, which are
 * outside this component. This is a deliberate reallocation of responsibility, and
 * naming it here is what stops a reader hunting for them in the shell.
 *
 * MIGRATION: this component declares no dependency registration of its own, by
 * design. Every provider the application needs is declared once where the
 * application is configured, so a layout primitive that registered anything would
 * give each of its instances a private copy and quietly diverge from the rest of the
 * screen. The legacy equivalent was configuration-driven indirection — eight
 * request-pipeline modules and six handlers registered in the site configuration,
 * plus a provider element per subsystem — which collapses into that one composition
 * root.
 *
 * MIGRATION: the page lifecycle and the postback model are gone. There is no
 * initialise handler, no load handler, no event wiring and no server round trip for
 * an interaction; composition is declarative and the class body holds only the
 * forwarding surface and the skip-link behaviour. The two client-script
 * registrations the legacy page performed on every request — the script manager
 * (L210) and the shared client script include (L213) — are replaced wholesale by the
 * compiled bundle.
 *
 * MIGRATION: skin-load diagnostics are not reproduced. A failure was caught
 * (L227-L237), surfaced only to portal or page administrators behind a role check
 * (L230, commented at L231 "only display the error to administrators"), html-encoded
 * (L232) and logged (L235). That is legacy precedent for not leaking diagnostics to
 * unprivileged callers, and it is honoured by omission: this component has no error
 * surface, no diagnostic panel and no debug flag. Failures are reported through the
 * application's own error surface, which is scoped to the main region.
 *
 * MIGRATION: the localisation mechanism is not ported. Legacy wording came from
 * resource files keyed by control identifier and property name — 40 of them in
 * scope, reached through 48 call sites — which is a construct of the retired
 * presentation framework. The framework-level translation runtime is out of scope,
 * so user-facing wording is authored directly in templates and the resource files
 * serve only as the authoritative reference for the English text.

 */
@Component({
  selector: 'app-shell',
  standalone: true,
  // Exactly the directives and components the paired template renders. The
  // navigation rail is projected rather than imported, for the two reasons set out
  // above; the router outlet is the directive, not the router's module, because
  // nothing in this workspace declares an Angular module at all.
  imports: [RouterOutlet, HeaderComponent, FooterComponent, NotificationListComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The global layout partial keys the shell grid off this class. Applying it here
  // rather than at every mount site makes a correctly laid-out shell the only shell
  // that can exist, and makes the stylesheet's `:host(:not(.shell))` fallback the
  // genuinely exceptional path it is written as.
  host: { class: 'shell' },
})
export class ShellComponent {
  /**
   * The banner's identity text, forwarded to the banner component.
   *
   * Defaults to the build-time application name so that the shell is correct with
   * no binding at all.
   */
  @Input() applicationName: string = environment.applicationName;

  /**
   * The signed-in account's display name, or `undefined` when no account is
   * signed in.
   *
   * Forwarded verbatim; the shell applies no interpretation of its own, so the
   * banner's rule that a blank name means no session holds unchanged whichever
   * component the value is read from.
   */
  @Input() userName?: string;

  /**
   * Whether a sign-out is currently in flight.
   *
   * Declared with `booleanAttribute` so the bare attribute form is understood as
   * `true`, matching the banner input it forwards to.
   */
  @Input({ transform: booleanAttribute }) signingOut = false;

  /**
   * Re-emitted when the banner's sign-out control is activated.
   *
   * The shell adds nothing to the gesture — no confirmation, no state change —
   * because it owns no session to end. Re-emitting rather than handling keeps the
   * decision with whichever component supplied the session in the first place.
   */
  @Output() readonly signOut = new EventEmitter<void>();

  /**
   * The identifier the main region carries, and the target the skip link names.
   */
  readonly mainRegionId: string = MAIN_REGION_ID;

  /**
   * The skip link's fragment target.
   */
  readonly skipLinkTarget: string = SKIP_LINK_TARGET;

  /**
   * This component's own host element, used to locate the main region when the
   * skip link is activated.
   *
   * Queried from the host rather than from the document so the lookup is scoped to
   * the shell that was activated. A duplicate identifier introduced by a routed
   * feature therefore cannot capture the shell's own skip link.
   */
  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * Forwards the banner's sign-out gesture to this component's own consumer.
   */
  protected onSignOut(): void {
    this.signOut.emit();
  }

  /**
   * Moves keyboard focus to the main region, and cancels the anchor's default
   * action.
   *
   * ⚠ THE CANCELLATION IS LOAD-BEARING, AND IT GUARDS A DEFECT THAT WAS MEASURED
   * RATHER THAN INFERRED. A fragment-only `href` resolves against the document's
   * BASE url, and `index.html` declares a root base href because deep links
   * require it. From any address below the root the href therefore names a
   * DIFFERENT PATH, and letting it resolve performs a FULL DOCUMENT RELOAD that
   * discards the current screen — observed in a real browser against the
   * production bundle as `/portals/3/settings` becoming `/` with every bundle
   * re-fetched.
   *
   * Two alternatives were evaluated and rejected. A router link with a fragment
   * navigates without reloading but moves NO focus, which is the one thing the
   * affordance exists to do. Removing the root base href would fix the resolution
   * and break deep linking.
   *
   * The anchor keeps its `href` regardless: the `href` is what makes it a tab stop
   * and what makes assistive technology announce it as a link.
   */
  protected focusMainRegion(event: Event): void {
    event.preventDefault();

    const mainRegion = this.hostElement.nativeElement.querySelector<HTMLElement>(
      `#${MAIN_REGION_ID}`,
    );

    // `tabindex="-1"` on the region is what makes this call effective; without it a
    // `<main>` is not focusable and focus would stay on the link.
    mainRegion?.focus();
  }
}
