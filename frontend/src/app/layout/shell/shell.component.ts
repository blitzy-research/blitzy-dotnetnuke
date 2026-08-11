import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Input,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';

import { environment } from '../../../environments/environment';
import { SIGN_IN_ROUTE as SHARED_SIGN_IN_ROUTE } from '../../core/config/app-routes.config';
import {
  activeRouteGuardsUnsavedChanges,
  holdsUnsavedEdits,
} from '../../core/guards/unsaved-changes.guard';
import { AuthStore } from '../../core/state/auth.store';
import { SessionLifecycleService } from '../../core/state/session-lifecycle.service';
import { FooterComponent } from '../footer/footer.component';
import { HeaderComponent } from '../header/header.component';
import { NotificationListComponent } from '../notifications/notification-list.component';
import { SidebarComponent } from '../sidebar/sidebar.component';

import type { Signal } from '@angular/core';

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
 * The fragment the skip link's `href` ends with.
 *
 * ⚠ A FRAGMENT ALONE IS NOT A SAFE `href` IN THIS APPLICATION, which is why this is only
 * part of one. `index.html` declares a ROOT base href because deep links require it, and a
 * fragment-only address resolves against the BASE url rather than against the address
 * showing - so from `/portals/3/settings` the address `#main-content` names `/#main-content`,
 * a DIFFERENT DOCUMENT. Letting it resolve reloads the application and discards the
 * in-memory session; that was measured in a browser, not inferred. The href is therefore
 * composed against the CURRENT path by {@link ShellComponent.skipLinkTarget}.
 */
const SKIP_LINK_FRAGMENT = `#${MAIN_REGION_ID}`;

/**
 * Where an operator is sent once their session has ended.
 *
 * MIGRATION: this WAS a private copy, and the comment justifying it argued that "the four agreeing
 * by construction matters less than no one of them reaching into another". The premise was right and
 * the conclusion did not follow, which is exactly the reasoning
 * `core/config/app-routes.config.ts` was created to settle: a neutral constants module is how the
 * consumers stop reaching into each other WITHOUT holding several values free to disagree.
 *
 * It is worth recording that this copy SURVIVED that consolidation. The constants module names the
 * four modules it replaced, and one of them was the root component — the sign-out navigation later
 * moved into this shell and was re-declared here on the way, so the count went back up to five
 * without anything noticing. Nothing would have failed loudly either: a mismatch resolves to the
 * catch-all route, so a renamed sign-in screen would leave sign-out landing on the not-found view.
 */
const SIGN_IN_ROUTE = SHARED_SIGN_IN_ROUTE;

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
 * ## THE SESSION BOUNDARY LIVES HERE
 *
 * The shell is the chrome's state boundary: it reads the signed-in identity from
 * `AuthStore`, hands the banner the name to render and the in-flight flag to disable
 * its control with, and turns the banner's gesture into an ended session followed by a
 * navigation. It performs NO data access of its own — no request is issued here, no
 * transfer object is held, and no domain slice is read.
 *
 * ⚠ THE ROOT IS NOT THIS BOUNDARY, AND THAT IS A CORRECTION RATHER THAN A PREFERENCE.
 * The session used to be bound one level up, in `AppComponent`, which also imported the
 * navigation rail and projected it through a slot published here. That put the chrome's
 * composition in two files and made the root a session container, when the specified
 * graph is an empty root mounting one shell that owns header, navigation, the routed
 * outlet and footer. The root is now exactly that mount point: it imports this class and
 * nothing else, declares no member, and renders `<app-shell />` with no binding. Every
 * decision the chrome makes is therefore reachable from this one file.
 *
 * WHAT THIS COMPONENT STILL DELIBERATELY DOES NOT DO. It does not discard the session
 * itself: ending a session means cancelling every in-flight request and clearing every
 * domain slice as well as the credentials, and `core/state/session-lifecycle.service.ts`
 * owns that invariant in one place precisely so that no caller has to remember the whole
 * list. It does not decide WHETHER a session has ended either — it knows only that the
 * operator asked. It does navigate, and that split is the lifecycle service's own design:
 * that service deliberately does not navigate, because where a caller should end up
 * differs by caller — the shell sends the operator to the sign-in screen, while the
 * bearer interceptor is mid-way through re-throwing the server's own response and must
 * not have that outcome displaced by a routing failure.
 *
 * The banner remains stricter still: its own comment records that it "does not end the
 * session, clear any credential or navigate anywhere", so those three decisions have
 * exactly one home, which is this class.
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
 * The navigation rail is OWNED HERE. The paired template renders
 * `<app-sidebar class="shell__sidebar" />`, so the shell imports the rail by name and
 * mounts it itself; there is no projection slot and no `<ng-content />` anywhere in this
 * component.
 *
 * The rail is `SidebarComponent`, selector `app-sidebar`, at
 * `../sidebar/sidebar.component`. It is standalone, takes no required input and emits
 * nothing, so mounting it needs no binding:
 *
 * ```text
 * <app-sidebar class="shell__sidebar" />
 * ```
 *
 * ⚠ IT IS IMPORTED HERE RATHER THAN PROJECTED, and the change is a correction. A slot
 * left the console's navigation dependent on whichever component happened to mount the
 * shell, and its failure mode was silent: with nothing projected the region matched the
 * stylesheet's `:empty` collapse rule, so a console with NO navigation at all rendered
 * without a raise, a warning or a compile error. Owning the rail makes that state
 * unreachable — the only way to ship the shell without navigation is now to delete the
 * import, which is a compile error in the paired template because strict template
 * checking is enabled.
 *
 * It also puts the composition where the specified structure puts it: the shell composes
 * the banner, the rail, the routed outlet and the footer. The shell knowing which rail it
 * renders is the point of that arrangement, not a leak — the rail resolves its own
 * navigation and its own gating, so this component holds no knowledge of what the
 * navigation contains.
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
  // Exactly the directives and components the paired template renders, the navigation
  // rail included: the shell composes banner, rail, routed outlet and footer, so all
  // four are listed here. The router outlet is the directive, not the router's module,
  // because nothing in this workspace declares an Angular module at all.
  imports: [
    RouterOutlet,
    HeaderComponent,
    SidebarComponent,
    FooterComponent,
    NotificationListComponent,
  ],
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

  /** The session store, read for the signed-in identity and the sign-out phase. */
  private readonly authStore = inject(AuthStore);

  /**
   * The one place a session ends.
   *
   * Injected rather than reached through {@link AuthStore.logout} directly, and the
   * distinction is the whole point of the service: the store's own sign-out discards the
   * credentials and the identity and knows nothing about the portals, accounts, roles or
   * exported module documents the domain stores are still holding. Calling the store
   * would leave every one of those slices resident and legible to whoever signs in next.
   */
  private readonly session = inject(SessionLifecycleService);

  /** Used to send the operator to the sign-in screen once the session has ended. */
  private readonly router = inject(Router);

  /**
   * Bounds the sign-out subscription to this component's lifetime.
   *
   * The command is cold and the service behind it is root-provided, so it would never end
   * the subscription itself. In practice the shell outlives every navigation and is
   * destroyed only when the application is, but binding the subscription to a lifetime
   * that is actually shorter than the service's is what makes the leak impossible rather
   * than merely unlikely.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Retires notifications that a change of screen has made stale.
   *
   * ⚠ THIS BELONGS TO THE SHELL AND NOWHERE ELSE, because the shell is the one component that
   * both outlives every route and renders the queue. The queue is root-scoped and the surface
   * sits above the outlet, so before this an entry survived every navigation: an empty-submit
   * warning raised on one screen was measured still reading word for word after seven
   * in-application navigations across five feature areas, with the dismiss control the only
   * way to clear it. On the portal listing it took a full-width band and pushed the page
   * heading down 42px, so it displaced content on screens it did not belong to.
   *
   * `NavigationEnd` specifically, and not every router event. A navigation a guard is about to
   * refuse has not moved the operator anywhere, so its notifications must survive — and one of
   * them IS the guard's own refusal. `NavigationEnd` fires only when the operator has genuinely
   * arrived somewhere else, which is exactly the condition that makes the previous screen's
   * messages stale. The announcements raised BY an arrival exempt themselves through the
   * queue's own one-navigation exemption.
   *
   * Subscribed in a field initialiser so it is established before the first render and torn
   * down with the component.
   */
  /**
   * The skip link's `href`, path-qualified against the address currently showing.
   *
   * Declared ABOVE the subscription that writes it because field initialisers run in
   * declaration order, so the reverse would write to an undefined member on the first
   * navigation that completed.
   */
  private readonly skipLinkHref = signal<string>(this.composeSkipLinkHref());

  private readonly notificationSweep = this.router.events
    .pipe(takeUntilDestroyed(this.destroyRef))
    .subscribe((event) => {
      if (event instanceof NavigationEnd) {
        // ⚠ THE NOTIFICATION SWEEP IS NOT DRIVEN FROM HERE, AND THAT IS A CORRECTION. This handler
        // ran on every completed navigation, including one that changed only the query string - and
        // paging, filtering and ordering are all carried in the query string on all four listings, so
        // turning a page or pressing a column heading swept a refusal the operator was still reading.
        // The sweep now belongs to the surface that renders the queue, which compares the PATH alone
        // and therefore treats a change of screen as a departure and a change of page as not one.
        //
        // The skip link's address must follow the operator from route to route: it names the
        // CURRENT path so that the fragment cannot resolve to a different document. See
        // SKIP_LINK_FRAGMENT for what goes wrong when it does not.
        this.skipLinkHref.set(this.composeSkipLinkHref());
      }
    });

  /**
   * The outlet every routed screen is mounted into.
   *
   * Read for ONE purpose: to ask the screen currently on display whether it is holding edits
   * nobody has saved, when the browser — not the router — is about to leave. Declared here
   * because this component owns the outlet, and because it is the only part of the application
   * guaranteed to be mounted for the whole of a session.
   *
   * `viewChild` rather than a constructor injection: the outlet is a child in this component's
   * own template, so it does not exist until the view is created.
   */
  private readonly outlet = viewChild(RouterOutlet);

  constructor() {
    /*
     * ⚠ THE BROWSER'S OWN EXITS DO NOT REACH THE ROUTER, AND THEY ACCOUNTED FOR HALF THE
     * MEASURED DATA LOSS. `core/guards/unsaved-changes.guard.ts` closes the in-application half —
     * a link, a Cancel, the Back button — but a RELOAD, a tab close, or a navigation to another
     * address entirely bypasses Angular completely. Runtime testing measured four edits across
     * two tabs of the site-settings screen destroyed by exactly that, and recorded that
     * `window.onbeforeunload` was `null`, so the browser had not even been asked to help.
     *
     * Registered here rather than in each screen because this component is mounted for the
     * entire life of the application, so the listener is added once and removed once. The
     * predicate is IMPORTED rather than restated: two definitions of "holding unsaved edits"
     * would eventually disagree, and the disagreement would appear as a screen that warns when
     * you click away but not when you reload.
     *
     * ⚠ THE RELOAD CASE IS ESPECIALLY WORTH GUARDING ON THIS APPLICATION, more so than on most.
     * The access token is held in memory only, so a reload does not merely discard the form —
     * it ends the session outright and returns the operator to the sign-in screen. Two losses,
     * one keystroke, and previously no warning before either.
     */
    const warnBeforeUnload = (event: BeforeUnloadEvent): void => {
      const outlet = this.outlet();

      // `component` THROWS on an un-activated outlet, so `isActivated` is a precondition and not
      // a tidiness check. During the first navigation, and while the shell is mounted over the
      // sign-in screen, there may be nothing routed at all.
      if (outlet === undefined || outlet.isActivated === false) {
        return;
      }

      /*
       * ⚠ ONLY THE SCREENS THE ROUTE TABLE PROTECTS, and asking the route table is what keeps
       * this channel from misfiring. A listing's search box is a `FormGroup` as much as an edit
       * form is, so typing a filter term marks it dirty — and without this test a reload of the
       * portals list would warn about "unsaved changes" for a search the operator had typed and
       * already seen applied. The route gate is declared on the nineteen form routes and on none
       * of the four listings, so reading that declaration back gives both channels one answer.
       */
      if (activeRouteGuardsUnsavedChanges(this.router) === false) {
        return;
      }

      if (holdsUnsavedEdits(outlet.component) === false) {
        return;
      }

      /*
       * `preventDefault()` is the standardised way to request the prompt; assigning to
       * `returnValue` is the legacy form that browsers still honour, and both are performed
       * because the specification and the shipped implementations do not fully agree on which is
       * required. NO WORDING IS SUPPLIED: browsers have ignored author-supplied text for this
       * prompt for years and substitute their own, so offering a sentence here would suggest a
       * control the application does not have.
       */
      event.preventDefault();
      event.returnValue = '';
    };

    window.addEventListener('beforeunload', warnBeforeUnload);

    // A listener outliving the application would keep this closure — and through it the whole
    // component — reachable. The shell is destroyed only when the application is, so this is a
    // correctness statement rather than a practical leak.
    this.destroyRef.onDestroy(() => {
      window.removeEventListener('beforeunload', warnBeforeUnload);
    });
  }

  /**
   * The name the banner renders for the signed-in account, or `undefined` when no
   * account is signed in.
   *
   * ⚠ THE FALLBACK TO THE ACCOUNT KEY IS A PUBLISHED CONTRACT, NOT A CONVENIENCE.
   * `core/models/auth.model.ts` documents `displayName` as never null and never absent
   * because the column is `nvarchar(128) NOT NULL` defaulting to the empty string, and
   * states outright that "a caller with no display name has `""`, and the shell renders
   * `username` in its place". Implementing that substitution is this component's
   * obligation as the boundary that resolves the session.
   *
   * It also prevents a concrete defect rather than tidying a cosmetic one. The banner
   * decides whether an account is signed in by testing the name it was given, so a blank
   * name renders no session cluster — and the sign-out control lives inside that cluster.
   * Passing `""` straight through would therefore leave an operator whose display name
   * happens to be empty signed in with no way to sign out.
   *
   * ⚠ SENTINEL DISCIPLINE. Presence is decided by an explicit comparison against `null`
   * and by an explicit length test after trimming, never by truthiness. The empty string
   * is exactly what the legacy null contract returns for a missing string
   * (`Library/Components/Shared/Null.vb:L71-L75`), so `""` means "not recorded" here and
   * has to be treated as absence — while remaining a value the API genuinely sends rather
   * than one this client may reject.
   *
   * Returns `undefined` rather than `null` for the absent case because that is what the
   * banner's optional input declares, and the banner tests for `undefined` specifically.
   */
  protected readonly userName: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    if (user === null) {
      return undefined;
    }

    const displayName: string = user.displayName.trim();

    if (displayName.length > 0) {
      return displayName;
    }

    // The documented substitution. The account key is `NOT NULL` and unique, so this is a
    // value rather than a second fallback — but it is trimmed and length-tested on the
    // same terms, because a boundary that trusted one field and not the other would
    // reintroduce the blank-name defect one level down.
    const username: string = user.username.trim();

    return username.length > 0 ? username : undefined;
  });

  /**
   * Whether a sign-out is in flight.
   *
   * Read straight from the store's phase rather than mirrored into a local flag, so there
   * is no second source of truth to fall out of step with the request it describes. The
   * banner disables its sign-out control while this is true.
   */
  protected readonly signingOut: Signal<boolean> = this.authStore.isSigningOut;

  /**
   * The address of the signed-in account's own member-services screen, or `undefined`.
   *
   * Handed to the banner so the operator can reach the subscriptions the account holds. Derived
   * here rather than taken as an input because this component owns the session boundary and is the
   * only place that knows which account is signed in; `undefined` when no identity has resolved,
   * which is what the banner tests for before it renders the link at all.
   */
  protected readonly accountServicesLink: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    return user === null ? undefined : `/users/${String(user.userId)}/services`;
  });

  /**
   * The address of the signed-in account's own profile screen, or `undefined`.
   *
   * MIGRATION: added with {@link accountPasswordLink} to close a navigational dead end. Both
   * screens are permitted to the account OWNER by the route table, but every entry in the
   * navigation rail requires a portal or host administrator, so a signed-in member had no link to
   * either and could reach them only by typing a URL containing their own numeric account key.
   * `ManageUsers.ascx.vb:L439-L456` records that the legacy command bar offered both to an account
   * viewing itself, so this restores measured behaviour rather than inventing an affordance.
   *
   * Derived here for the same reason the services address is: this component owns the session
   * boundary and is the only place that knows which account is signed in. `undefined` until an
   * identity resolves, which is what the band tests before rendering the link at all.
   */
  protected readonly accountProfileLink: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    return user === null ? undefined : `/users/${String(user.userId)}/profile`;
  });

  /**
   * The address of the signed-in account's own change-password screen, or `undefined`.
   *
   * See {@link accountProfileLink}; `cmdPassword` in the legacy command bar is the affordance this
   * restores.
   */
  protected readonly accountPasswordLink: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    return user === null ? undefined : `/users/${String(user.userId)}/password`;
  });

  /**
   * Whether the signed-in account is a host (super user), as the SERVER reported it.
   *
   * Forwarded to the navigation rail, which declares this and {@link administersTenant} as REQUIRED
   * inputs so that a caller cannot mount it without stating the caller's authority and silently
   * render an unfiltered administration map. The shell is the rail's only caller and already owns
   * the session boundary, so it is the one place that can answer.
   */
  protected readonly hostAccount: Signal<boolean> = this.authStore.isSuperUser;

  /**
   * Whether the server reported the signed-in account as an administrator of the current tenant.
   *
   * ⚠ THE SERVER'S OWN VERDICT, NEVER A ROLE NAME. The administrator role is renameable, so a name
   * test would admit a colliding role and refuse a renamed one; this is the projection of
   * `CurrentUserDto.IsPortalAdministrator`.
   */
  protected readonly administersTenant: Signal<boolean> = this.authStore.holdsPortalAdministration;

  /**
   * The identifier the main region carries, and the target the skip link names.
   */
  readonly mainRegionId: string = MAIN_REGION_ID;

  /**
   * The skip link's `href` — the path currently showing, plus the main region's fragment.
   *
   * ⚠ PATH-QUALIFIED, NOT A BARE FRAGMENT, AND THAT REMOVES A LATENT HAZARD RATHER THAN
   * TIDYING ONE. A bare `#main-content` resolves against the root base href this document
   * declares, so from any address below the root it named a DIFFERENT PATH and following it
   * would have reloaded the application and discarded the in-memory session. That was
   * survivable only because {@link focusMainRegion} cancels the default action on every
   * activation - which is to say the address was wrong and one line of script was all that
   * stood between it and a destroyed session. Composed against the current path it is
   * correct on its own terms: were the default ever to run, the browser would perform a
   * SAME-DOCUMENT fragment navigation, because the path it names is the path already showing.
   *
   * A signal rather than a constant because the path changes under it. It is refreshed on
   * `NavigationEnd` only - the address is not settled before that, and a navigation a guard
   * is about to refuse must not be published as the link's target.
   */
  readonly skipLinkTarget: Signal<string> = this.skipLinkHref.asReadonly();

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
   * Ends the session the operator asked to end, then sends them to the sign-in screen.
   *
   * ⚠ SUBSCRIBED EXACTLY ONCE, AND THAT IS WHAT ISSUES THE REQUEST. The command is cold
   * by design — `session-lifecycle.service.ts` documents its return as "COLD: it must be
   * subscribed for the request to be issued, exactly once" — so a command nobody
   * subscribes to revokes nothing, and a command subscribed twice revokes twice.
   *
   * THE RE-ENTRY GUARD CLOSES A WINDOW THE MARKUP CANNOT. The banner already guards the
   * gesture twice over, through its template's `[disabled]="signingOut"` binding and again
   * in its own handler, so this is a third check — and it is not redundant with either.
   * Both of the banner's guards read the flag as it stood at the last change detection,
   * whereas {@link AuthStore.logout} sets the phase SYNCHRONOUSLY at subscribe time (it
   * opens with `defer`), so this test sees the update immediately. Two emissions within a
   * single tick — a synthetic pair from a specification, or a second affordance added
   * later — would pass both of the banner's guards and are stopped here.
   *
   * NAVIGATION HAPPENS ON BOTH EXITS, DELIBERATELY. The service discards the session in a
   * `finalize`, so the session is gone whether the revocation succeeded or failed; leaving
   * the operator on an administration screen with no credentials would strand them on a
   * view whose every request is about to be refused. An observable cannot both error and
   * complete, so exactly one of the two handlers below runs.
   *
   * ⚠ WHAT ACTUALLY REACHES THE ERROR HANDLER IS NOT A FAILED REQUEST, and stating that
   * precisely matters more than the handler itself. {@link AuthStore.logout} absorbs the
   * revocation's own failure — the server answers 204 whatever it finds, a network fault
   * is treated the same way, and local sign-out has already happened unconditionally — so
   * a refused or unreachable endpoint arrives here as a COMPLETION, and the navigation
   * below happens through the `complete` handler. The `error` handler covers the remaining
   * case: a throw from inside the teardown itself, since both the store's discard and the
   * service's slice clearing run in a `finalize` whose exception would propagate to this
   * subscriber. It is present because the service PUBLISHES an error exit, and coding to a
   * dependency's published contract rather than to its current implementation is what
   * keeps this component correct if that absorption is ever removed.
   *
   * THE ERROR IS ABSORBED RATHER THAN SURFACED, and this is the one place in the
   * application where that is the right call. The session has already been discarded by
   * the time the handler runs, so there is nothing left for the operator to act on and
   * nothing to retry — a person who asked to sign out has ended up signed out. Rethrowing
   * would raise an unhandled error on a path that succeeded from the operator's point of
   * view, and would do it while the application is mid-teardown of its own session.
   *
   * The navigation itself is not awaited and its rejection is swallowed, matching how the
   * rest of the application navigates: a navigation the router refuses is not this
   * component's failure to report, and the session has already ended either way.
   */
  protected onSignOut(): void {
    if (this.signingOut()) {
      return;
    }

    this.session
      .signOut()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        complete: () => this.goToSignIn(),
        error: () => this.goToSignIn(),
      });
  }

  /**
   * Moves keyboard focus to the main region, and cancels the anchor's default action.
   *
   * ⚠ THE CANCELLATION IS STILL REQUIRED, BUT IT IS NO LONGER ALL THAT STANDS BETWEEN THE
   * LINK AND A DESTROYED SESSION. It used to be. A fragment-only `href` resolves against the
   * document's BASE url, and `index.html` declares a root base href because deep links
   * require it, so from any address below the root the href named a DIFFERENT PATH and
   * letting it resolve performed a FULL DOCUMENT RELOAD that discarded the current screen —
   * observed in a real browser against the production bundle as `/portals/3/settings`
   * becoming `/` with every bundle re-fetched. The address is now composed against the
   * current path ({@link skipLinkTarget}), so the fallback is a same-document fragment
   * navigation rather than a reload.
   *
   * What the cancellation does now is the reason it was always wanted: it keeps the
   * activation from pushing a history entry the operator would have to press Back through,
   * and it puts FOCUS on the region rather than merely scrolling to it. A router link with a
   * fragment was evaluated for the same job and rejected because it moves no focus, which is
   * the one thing this affordance exists to do; removing the root base href was rejected
   * because it would break deep linking.
   *
   * The anchor keeps its `href` regardless: the `href` is what makes it a tab stop and what
   * makes assistive technology announce it as a link.
   */
  /**
   * Builds the skip link's `href` from the address currently showing.
   *
   * Any fragment already on the address is dropped rather than appended to, because two
   * fragments in one address name nothing. `Router.url` always begins with a slash, so the
   * result is root-relative and resolves against the ORIGIN rather than against the base
   * href - which is the whole point of composing it.
   *
   * @returns The current path, query string included, ending in the main region's fragment.
   */
  private composeSkipLinkHref(): string {
    const [pathAndQuery] = this.router.url.split('#');

    return `${pathAndQuery}${SKIP_LINK_FRAGMENT}`;
  }

  protected focusMainRegion(event: Event): void {
    event.preventDefault();

    const mainRegion = this.hostElement.nativeElement.querySelector<HTMLElement>(
      `#${MAIN_REGION_ID}`,
    );

    // `tabindex="-1"` on the region is what makes this call effective; without it a
    // `<main>` is not focusable and focus would stay on the link.
    mainRegion?.focus();
  }

  /**
   * Sends the operator to the sign-in screen.
   *
   * No `returnUrl` is carried, and the omission is the point. The two route gates attach
   * one when they INTERRUPT a navigation, so that a caller who is sent to sign in is
   * returned to the address they asked for. Signing out is not an interruption: the
   * operator chose to leave, and returning them to the screen they deliberately left — or
   * worse, restoring an address that named a record the next operator has no right to know
   * exists — would defeat the discard that just happened.
   */
  private goToSignIn(): void {
    void this.router.navigate([SIGN_IN_ROUTE]).catch(() => false);
  }
}
