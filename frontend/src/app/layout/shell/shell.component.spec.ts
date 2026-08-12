import { APP_BASE_HREF } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';

import { AUTH_ENDPOINTS } from '../../core/config/api-endpoints';
import { TokenStorageService } from '../../core/services/token-storage.service';
import { AuthStore } from '../../core/state/auth.store';
import { SessionLifecycleService } from '../../core/state/session-lifecycle.service';
import { FooterComponent } from '../footer/footer.component';
import { HeaderComponent } from '../header/header.component';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { ShellComponent } from './shell.component';

import type { AuthSession } from '../../core/models/auth.model';

/**
 * SPECIFICATION FOR THE APPLICATION SHELL
 *
 * The shell is the only component in the workspace that renders a router outlet and
 * the only one that renders the main landmark, so a handful of facts about its markup
 * are load-bearing for every screen the console will ever have. Those facts are not
 * self-evident from reading the template: a second outlet would double-render every
 * route, a missing one would render nothing at all, and a skip link whose fragment
 * names no element is announced, reachable, and does nothing when activated. Each of
 * those failures is silent. This file makes them mechanical.
 *
 * The assertions are deliberately structural rather than visual. Placement is the
 * stylesheet's decision and is verified by looking at the rendered console, but
 * SOURCE ORDER is what the Tab key follows and what a screen reader in document-order
 * mode follows, so source order is asserted here and is never a matter of taste.
 *
 * ## THE RAIL IS OWNED, NOT PROJECTED, AND THAT IS ASSERTED IN ONE ARRANGEMENT
 *
 * The shell imports `SidebarComponent` and renders `<app-sidebar class="shell__sidebar" />`
 * itself. There is no projection slot and no `<ng-content />` anywhere in the component,
 * so the landmark inventory has exactly ONE legitimate answer: a bare mount of the shell
 * contains one `<header>`, one `<footer>`, one `<main>`, one `<nav>` and one
 * `<router-outlet>`.
 *
 * That is a correction rather than a preference. The region used to be a slot filled by
 * whichever component mounted the shell, and its failure mode was silent: with nothing
 * projected the region matched the stylesheet's `:empty` collapse rule, so a console with
 * NO navigation rendered without a raise, a warning or a compile error. Owning the rail
 * makes that state unreachable, and the single inventory below is what pins it.
 *
 * ## THE SESSION BOUNDARY IS ASSERTED HERE, BECAUSE IT LIVES HERE
 *
 * The shell reads the signed-in identity from the session store, hands the banner the name
 * and the in-flight flag, and turns the banner's gesture into an ended session followed by
 * a navigation. Those decisions used to sit in the root component; they are asserted here
 * now, against the REAL store graph rather than stubs, because the defect worth catching
 * lives in the seam between the shell and that graph.
 *
 * An HTTP provider is therefore configured — the store graph reaches the HTTP client — and
 * the testing backend replaces the real one so that no request escapes. `httpMock.verify()`
 * after every case is what turns "the shell issues exactly one revocation" into a checked
 * claim rather than a described one. The shell itself still injects no transport and reads
 * no domain slice.
 *
 * A router IS required, three times over: the shell renders a router outlet, the banner it
 * composes renders a router link, and the rail renders router links of its own. An empty
 * route table satisfies all three, and the sign-out navigation is asserted through a spy
 * rather than by resolving a route.
 *
 * ## NO EFFECTS, SO NO EFFECT FLUSHING
 *
 * Neither the shell nor any component it renders declares a reactive effect, so there
 * is nothing to drain between an input change and an assertion. `TestBed.flushEffects`
 * exists on the pinned framework version and `TestBed.tick` does not, but this file
 * uses neither, because reaching for either would imply asynchrony that this render
 * graph does not contain.
 *
 * ## ORDER INDEPENDENCE
 *
 * The runner leaves the framework's spec randomisation at its default, so every spec
 * here is independent and order-agnostic: the fixture is rebuilt in `beforeEach` and
 * no mutable state is shared between specs.
 *
 * ## MIGRATION RECORD
 *
 * MIGRATION: this entire file is a NET ADDITION. There is no .NET or VB.NET test
 * project, fixture or assertion anywhere in the legacy trees — both legacy solution
 * files contain no test project, and no `.vb` file in the checkout carries a test
 * attribute or an assertion — so nothing here is a port of an existing test. The one
 * qualification is worth stating precisely rather than overclaiming: the checkout does
 * track seven hand-run browser pages under `Website/js/ClientAPITests/`, which
 * exercise the legacy client-side script API through a bespoke assertion harness of
 * their own. They are opened manually in a browser, they are not part of any automated
 * suite, and they have no bearing on page composition. Every behaviour asserted below
 * was, in the legacy application, verified only by manual click-through.
 *
 * MIGRATION: the single-outlet assertion pins an invariant the legacy page held
 * STRUCTURALLY rather than by test. `Website/Default.aspx` is 30 lines long and
 * declared exactly one injection point, the placeholder at L25, into which
 * `Website/Default.aspx.vb` added the resolved layout control at L590. One placeholder
 * in one page cannot be duplicated by accident. A component template can, so the
 * invariant that was previously guaranteed by the shape of the file is now guaranteed
 * by this specification instead.
 *
 * MIGRATION: every accessibility assertion below tests a NET ADDITION, not preserved
 * behaviour. Measured across the whole legacy checkout: zero skip links, zero
 * accessibility attributes of any kind, zero explicit roles in any page or user
 * control, and zero main elements. There is therefore no legacy behaviour to preserve
 * here and no risk of regressing any — the skip link, the main landmark and the
 * programmatic focus target are all new, and all of them are free of visual cost.
 *
 * MIGRATION: no skin, theme or layout-variant test exists here BY DESIGN. The legacy
 * chrome was selected per request: `LoadSkin` (L217-L243) stripped the application
 * path from a database-configured path, instantiated the control with
 * `LoadControl("~" & SkinPath)` (L224) and called `DataBind()` (L226) — the comment at
 * L225 is explicit that this executed "any server logic in the skin", so the layout
 * was executable code. Skinning, containers and skin objects are all out of scope, and
 * this generation of the product used skins rather than master pages: the checkout
 * contains zero master-page files. The target has exactly one compile-time layout, so
 * a variant matrix would be testing a feature that must not exist.
 *
 * MIGRATION: this specification asserts RELATIVE addresses only, and in fact asserts
 * no address at all, because the shell references none. The test target carries no
 * file replacements, so specs compile against the production environment, whose API
 * base is the relative `/api/v1`. Asserting an absolute host here would encode a value
 * that is wrong in the container topology the console ships in.
 */

/**
 * An expiry comfortably ahead of whenever this suite runs, derived from the clock rather than written
 * down.
 *
 * ⚠ AN ABSOLUTE DATE IS A TEST THAT EXPIRES. This fixture used to carry `2030-01-01T00:00:00Z`,
 * which holds a session valid by the calendar rather than by anything the specification controls: on the
 * first of January 2030 every case depending on it begins asserting the opposite of what it was written
 * to assert, and it does so SILENTLY, because a session read as already expired is a state this
 * application handles rather than an error it reports.
 *
 * One hour is longer than any run of this suite and shorter than any window the application treats as
 * unusual, and it is computed ONCE per module load so every case in the file shares one instant rather
 * than racing the clock between them.
 */
const FUTURE_SESSION_EXPIRY_UTC: string = new Date(Date.now() + 60 * 60 * 1000).toISOString();

/**
 * A held session, exactly as the sign-in endpoint answers one.
 *
 * Written through the token custodian rather than by posting credentials, because every
 * assertion here is about the CHROME reading a session rather than about acquiring one, and
 * staging one through the sign-in endpoint would add a request each case would then have to
 * account for.
 *
 * The display name is deliberately distinct from the account key so that the fallback
 * assertions below cannot pass by coincidence.
 */
const SESSION_BODY: AuthSession = {
  accessToken: 'operator-access-token',
  refreshToken: 'operator-refresh-token',
  expiresAtUtc: FUTURE_SESSION_EXPIRY_UTC,
  mustChangePassword: false,
  mustUpdateProfile: false,
  passwordExpiring: false,
  user: {
    userId: 0,
    portalId: -1,
    portalName: 'Measured Portal',
    username: 'operator.a',
    displayName: 'Operator A',
    email: 'operator.a@example.test',
    isSuperUser: false,
    isPortalAdministrator: false,
    roles: [],
    permissions: [],
  },
};

/**
 * A deep address used to prove the skip link's `href` follows the current route.
 *
 * Several segments deep on purpose: the defect being guarded is that a bare fragment resolves
 * against the ROOT base href, which is indistinguishable from correct behaviour when the test
 * never leaves the root.
 */
const DEEP_PROBE_PATH = 'deep/route/below/the/root';

/** The inert screen `DEEP_PROBE_PATH` activates. It renders nothing and requests nothing. */
@Component({ selector: 'app-route-probe', standalone: true, template: '' })
class RouteProbeComponent {}

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let component: ShellComponent;
  let httpMock: HttpTestingController;
  let tokens: TokenStorageService;
  let authStore: AuthStore;
  let session: SessionLifecycleService;
  let router: Router;
  let navigate: jasmine.Spy;

  /**
   * Returns the shell's host element.
   *
   * Cast exactly once, here, so that no other line in this file needs to assert
   * anything about the fixture's element type.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Returns the one element matching `selector` within the shell, failing with a
   * descriptive message when it is absent.
   *
   * Every DOM read in this file goes through this helper. It narrows
   * `Element | null` to `Element` by throwing rather than by asserting non-null, so a
   * missing element produces a named failure instead of a type error suppressed at
   * author time and a property access on nothing at run time.
   *
   * @param selector The selector to resolve against the shell's host element.
   * @returns The matching element.
   */
  function requireElement(selector: string): HTMLElement {
    const found = host().querySelector<HTMLElement>(selector);

    if (found === null) {
      throw new Error(`expected the shell to render an element matching '${selector}'`);
    }

    return found;
  }

  /**
   * Counts the elements matching `selector` within the shell.
   *
   * @param selector The selector to count.
   * @returns The number of matches.
   */
  function count(selector: string): number {
    return host().querySelectorAll(selector).length;
  }

  /**
   * Sets one of the shell's inputs and re-renders.
   *
   * Assigning to the instance field would NOT re-render: the shell declares on-push
   * change detection, so a fixture-level render pass skips it unless it has been
   * marked dirty. Going through the component reference both marks it dirty and runs
   * the input's declared transform, which is what a real template binding does.
   *
   * @param name The input to set.
   * @param value The value to set it to, before any declared transform.
   */
  function setInput(name: 'applicationName', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /**
   * Reports whether `first` precedes `second` in document order.
   *
   * Comparing positions is preferred over indexing into a child list because it holds
   * regardless of how deeply either element sits, which keeps the tab-order assertion
   * independent of the wrapper decisions inside the composed children.
   *
   * @param first The element expected to come first.
   * @param second The element expected to follow it.
   * @returns True when `second` follows `first`.
   */
  function precedes(first: HTMLElement, second: HTMLElement): boolean {
    return (first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING) > 0;
  }

  /**
   * Returns the banner component instance the shell composes.
   *
   * Resolved through the node injector so the returned value is typed, which is what
   * lets the forwarding specifications below assert on the banner's INPUTS rather than
   * on the banner's internal markup. The banner is another component's file; reaching
   * into its class names from here would couple this specification to details it does
   * not own and would fail on a purely cosmetic change there.
   */
  function banner(): HeaderComponent {
    const node = fixture.debugElement.query(By.directive(HeaderComponent));

    if (node === null) {
      throw new Error('expected the shell to compose the banner component');
    }

    return node.injector.get(HeaderComponent);
  }

  /**
   * Returns the footer component instance the shell composes.
   *
   * Resolved by directive rather than by element name for the same reason as the
   * banner: matching `app-footer` proves only that an element with that name exists,
   * whereas resolving the directive proves the real component class is mounted there.
   */
  function footerBand(): FooterComponent {
    const node = fixture.debugElement.query(By.directive(FooterComponent));

    if (node === null) {
      throw new Error('expected the shell to compose the footer component');
    }

    return node.injector.get(FooterComponent);
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        // A router is required three times over: the shell renders a router outlet, the
        // banner it composes renders a router link, and the rail renders router links. None
        // of the three needs a route table at all.
        //
        // THE ONE ENTRY EXISTS FOR ONE CASE, and it is a real route rather than a stubbed
        // navigation on purpose. The skip link's address is refreshed on `NavigationEnd`, so
        // the only honest way to assert that it follows the operator is to let the real
        // router complete a real navigation to a deep address - a synthetic event pushed into
        // the event stream would assert that the handler works, not that the router ever
        // calls it. Nothing else navigates here (`navigate` is spied below), so the outlet
        // activates this stub in exactly one case.
        provideRouter([{ path: DEEP_PROBE_PATH, component: RouteProbeComponent }]),
        // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()`
        // REPLACES the backend the real client installed, so reversing the two would leave
        // the live backend in place and every expectation below would find nothing.
        //
        // Present because the shell owns the session boundary, and the graph beneath it —
        // the session store, the authentication service, the four domain stores the
        // lifecycle service discards — reaches the HTTP client. Nothing is stubbed: the real
        // graph is what makes "asking to sign out actually ends the session" assertable.
        provideHttpClient(),
        provideHttpClientTesting(),
        // The base href every case below except the tenant suite at the foot of this file
        // assumes, declared rather than inherited. Without it `PathLocationStrategy` falls
        // back to reading the base element out of whatever document the runner serves, so
        // the skip link's asserted address would depend on the harness rather than on the
        // component - and the assertion would go quiet, not fail, if the runner's document
        // ever changed. `/` is what `appBaseHref()` derives at the root deployment.
        { provide: APP_BASE_HREF, useValue: '/' },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    session = TestBed.inject(SessionLifecycleService);
    router = TestBed.inject(Router);

    // Spied before the component is created so that no navigation can escape into the empty
    // route table. `resolveTo` rather than `stub`, because the component chains a `catch`
    // onto the returned promise and an undefined return would throw there.
    navigate = spyOn(router, 'navigate').and.resolveTo(true);

    fixture = TestBed.createComponent(ShellComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    // Proves no request was left outstanding by any case, which is the assertion that
    // catches a revocation issued twice as reliably as a count does.
    httpMock.verify();
  });

  /**
   * Establishes a held session, exactly as the sign-in flow would, and renders it.
   *
   * @param overrides Identity members to replace on the stored session.
   */
  function holdSession(overrides: Partial<AuthSession['user']> = {}): void {
    tokens.store({ ...SESSION_BODY, user: { ...SESSION_BODY.user, ...overrides } });
    fixture.detectChanges();
  }

  /** Activates the banner's sign-out control the way an operator does. */
  function clickSignOut(): void {
    host().querySelector<HTMLButtonElement>('button.app-header__logout')?.click();
    fixture.detectChanges();
  }

  describe('component definition', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('is standalone, because nothing in this workspace declares a module', () => {
      const definition = (
        ShellComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.standalone).toBeTrue();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        ShellComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('carries the shell class on its own host, without which the global grid never engages', () => {
      // The grid is published globally rather than by this component's stylesheet,
      // because a host-scoped grid cannot place children the global partial names.
      // The class is applied by the component itself rather than by whichever parent
      // mounts it, which is what makes a correctly laid-out shell the only shell that
      // can exist. Without it the host matches the stylesheet's fallback and every
      // region stacks at full width regardless of viewport.
      expect(host().classList.contains('shell')).toBeTrue();
    });
  });

  describe('region composition', () => {
    it('renders exactly five element children, each carrying its published grid area', () => {
      // The region classes must sit on the shell's DIRECT children, because the host
      // itself is the grid container. A wrapper interposed here would sever the
      // parent-child relationship the grid depends on, so the count is asserted
      // alongside the classes rather than the classes alone.
      const regions = Array.from(host().children).map((child) => child.className);

      expect(regions.length).toBe(5);
      expect(regions[0]).toContain('shell__skip-link');
      expect(regions[1]).toContain('shell__header');
      expect(regions[2]).toContain('shell__sidebar');
      expect(regions[3]).toContain('shell__main');
      expect(regions[4]).toContain('shell__footer');

      // The navigation region is the rail's OWN host rather than a wrapper around it, so
      // the grid places the rail directly. A wrapper would satisfy the class assertion
      // above and still break the layout, which is why the element is named here.
      expect(host().children[2].tagName.toLowerCase()).toBe('app-sidebar');
    });

    it('renders exactly one router outlet, which is the application\'s only outlet', () => {
      // Two outlets would double-render every route; none would render nothing. Both
      // failures are silent, and neither is visible in a code review of the template.
      expect(count('router-outlet')).toBe(1);
    });

    it('renders exactly one main region', () => {
      expect(count('main')).toBe(1);
    });

    it('places the router outlet inside the main region', () => {
      // This is the accessibility contract rather than decoration: the routed screen
      // has to be inside the landmark the skip link moves focus to, or the skip link
      // lands the user somewhere that does not contain the content they asked for.
      const outletWithinMain = requireElement('main').querySelector('router-outlet');

      expect(outletWithinMain).not.toBeNull();
    });

    it('composes the banner, the rail and the footer exactly once each', () => {
      expect(count('app-header')).toBe(1);
      expect(count('app-sidebar')).toBe(1);
      expect(count('app-footer')).toBe(1);
    });

    it('composes the real banner and footer components, not merely elements so named', () => {
      // Resolving each by directive rather than by element name is the difference
      // between proving a component is mounted and proving a tag exists. An element
      // named `app-footer` with no component behind it would satisfy the count above
      // and fail here, which is exactly the substitution this assertion guards.
      expect(banner()).toBeInstanceOf(HeaderComponent);
      expect(footerBand()).toBeInstanceOf(FooterComponent);
    });
  });

  describe('landmark inventory', () => {
    it('emits exactly one banner landmark and one footer landmark', () => {
      // The shell's own template emits NEITHER element. Both arrive from the composed
      // children, so the count that matters is the one across the composed tree: one
      // banner from the banner component and one content-info from the footer
      // component. Asserting zero here would be wrong, and asserting more than one
      // would mean a duplicated landmark that assistive technology would announce
      // twice.
      expect(count('header')).toBe(1);
      expect(count('footer')).toBe(1);
    });

    it('emits exactly one navigation landmark, which the rail it owns supplies', () => {
      // ONE ANSWER, BECAUSE THERE IS ONE ARRANGEMENT. The rail is imported and rendered by
      // the shell rather than projected into it, so the element is present at a bare mount and
      // the landmark comes from the rail rather than from this component's own markup.
      expect(count('app-sidebar')).toBe(1);

      // ⚠ AND THE LANDMARK IS CONDITIONAL ON THE SESSION, WHICH IS WHY THIS CASE HOLDS ONE. Every
      // destination the rail offers is an administration screen, so the rail withholds the
      // landmark entirely rather than announcing a region a reader can navigate to and find
      // nothing in. A bare mount therefore has the ELEMENT and no landmark, which is asserted
      // first so the conditionality is proved here rather than inferred from the rail's own file.
      expect(count('nav'))
        .withContext('no landmark stands over a rail with nothing in it')
        .toBe(0);

      holdSession({ isPortalAdministrator: true });

      expect(count('nav')).toBe(1);
      expect(count('app-sidebar')).toBe(1);

      // The landmark is NAMED, which is what makes it announceable as the console's
      // navigation rather than as an anonymous region.
      const navigation = requireElement('nav');

      expect(navigation.getAttribute('aria-label')?.trim().length ?? 0).toBeGreaterThan(0);
    });

    it('emits no landmark twice over, so nothing is announced as a duplicate', () => {
      // A single sweep guarding the whole inventory. `<aside>` and `<nav>` are checked
      // because either could be introduced by a well-meaning edit to a composed child
      // and neither would be visible in this component's own template.
      expect(count('main')).toBe(1);
      expect(count('header')).toBe(1);
      expect(count('footer')).toBe(1);
      expect(count('aside')).toBe(0);
    });
  });

  describe('skip link', () => {
    it('is the first element in the shell, so it is the first thing the Tab key reaches', () => {
      const first = host().children[0];

      expect(first.classList.contains('shell__skip-link')).toBeTrue();
    });

    it('is an anchor carrying an address, which is what makes it a tab stop at all', () => {
      // An anchor without an address is not focusable and is not announced as a link.
      // The address is therefore load-bearing even though its default action is
      // cancelled, which is exactly the sort of thing a reader deletes as redundant.
      const link = requireElement('a.shell__skip-link');

      expect(link.tagName.toLowerCase()).toBe('a');
      expect(link.hasAttribute('href')).toBeTrue();
    });

    it('names a fragment that the main region actually carries', () => {
      // THE ASSERTION THIS BLOCK EXISTS FOR. A skip link whose fragment matches no
      // element is announced, is reachable, and does nothing — a failure that is
      // invisible in production and invisible in a template review. Both halves are
      // read from the rendered DOM rather than from the component or from a literal,
      // so the assertion survives a rename of the identifier and still fails if the
      // two sides ever drift apart.
      //
      // ⚠ THIS USED TO ASSERT THE WHOLE ADDRESS EQUALLED THE BARE FRAGMENT, AND IT HAD
      // ENCODED A DEFECT. A fragment-only address resolves against the ROOT base href this
      // application declares, so from a deep route it named a different document; the
      // address is now composed against the current path, and only its TAIL is the
      // fragment. The claim in the title — that the fragment names an element that exists
      // — is unchanged and is what is tested here; the path qualification is the case
      // below.
      const target = requireElement('a.shell__skip-link').getAttribute('href') ?? '';
      const region = requireElement('main');

      expect(region.id).not.toBe('');
      expect(target.endsWith(`#${region.id}`)).toBeTrue();
      expect(target.split('#').length).toBe(2);
    });

    it('qualifies its address with the path currently showing, so the fragment cannot leave the document', async () => {
      // The hazard being tested is a CROSS-DOCUMENT load: were the anchor's default action
      // ever to run with a bare fragment, the root base href would resolve it to a different
      // path and reload the application, discarding the in-memory session. A path-qualified
      // address makes the fallback a same-document fragment navigation instead.
      const before = requireElement('a.shell__skip-link').getAttribute('href') ?? '';

      expect(before.startsWith('/')).toBeTrue();

      await router.navigateByUrl(`/${DEEP_PROBE_PATH}`);
      fixture.detectChanges();

      const after = requireElement('a.shell__skip-link').getAttribute('href') ?? '';

      // The address FOLLOWED the operator. A constant would have failed here, which is the
      // whole point of reading it after a navigation rather than only on arrival.
      expect(after).toBe(`/${DEEP_PROBE_PATH}#main-content`);
      expect(after).not.toBe(before);
    });

    it('is labelled with visible text, so its purpose is announced', () => {
      expect(requireElement('a.shell__skip-link').textContent?.trim()).toBe('Skip to main content');
    });

    it('moves focus into the main region when it is activated', () => {
      const link = requireElement('a.shell__skip-link');
      const region = requireElement('main');

      link.click();

      // Markup alone cannot distinguish a working affordance from a broken one here:
      // the fragment is correct, the target exists and the link is the first tab stop
      // in both the working and the broken arrangement. What the affordance exists to
      // DO is move focus, so that is what is asserted.
      expect(document.activeElement).toBe(region);
    });

    it('cancels its default action, guarding a full-document reload that was measured', () => {
      const link = requireElement('a.shell__skip-link');
      const activation = new MouseEvent('click', { bubbles: true, cancelable: true });

      link.dispatchEvent(activation);

      // A fragment-only address resolves against the document's BASE url rather than
      // against the address showing. The static document declares a root base, which
      // deep links require, so from any route below the root the address names a
      // DIFFERENT PATH and letting it resolve performs a full document reload that
      // discards the current screen.
      //
      // The reload itself cannot be reproduced inside the runner, because the
      // specification's document is the runner's own. What CAN be asserted is the
      // single condition that prevents it. If a future edit replaces the handler with
      // a plain anchor, or drops the cancellation, this fails while every markup
      // assertion above still passes.
      expect(activation.defaultPrevented).withContext('skip link default action').toBeTrue();
    });
  });

  describe('tab order', () => {
    it('follows source order from the skip link through to the footer', () => {
      // Placement is the stylesheet's decision, so visual position is never a reason
      // to reorder the template. Source order is what the Tab key and a screen reader
      // in document-order mode follow, which is why it is pinned here: the skip link
      // must come first for it to be reachable before the banner, and the footer must
      // sit last.
      const link = requireElement('a.shell__skip-link');
      const bannerHost = requireElement('app-header');
      const navigationRegion = requireElement('app-sidebar.shell__sidebar');
      const region = requireElement('main');
      const footerHost = requireElement('app-footer');

      expect(precedes(link, bannerHost)).withContext('skip link before banner').toBeTrue();
      expect(precedes(bannerHost, navigationRegion)).withContext('banner before rail').toBeTrue();
      expect(precedes(navigationRegion, region)).withContext('rail before main').toBeTrue();
      expect(precedes(region, footerHost)).withContext('main before footer').toBeTrue();
    });
  });

  describe('main region', () => {
    it('is a native main element, which is both the landmark and the stylesheet selector', () => {
      // The component stylesheet selects the bare element name, and a native element
      // makes the accessibility tree correct without a single attribute. Swapping in a
      // generic element with an explicit role would break the first and leave the
      // second only apparently intact.
      expect(requireElement('.shell__main').tagName.toLowerCase()).toBe('main');
    });

    it('writes no explicit role, because a native main is already the main landmark', () => {
      expect(requireElement('main').hasAttribute('role')).toBeFalse();
    });

    it('is programmatically focusable without joining the tab order', () => {
      // This is what makes the skip link able to move focus rather than merely scroll
      // the viewport. Without it some browsers scroll but leave focus behind, and the
      // next Tab press returns the user to the banner they were trying to skip.
      expect(requireElement('main').getAttribute('tabindex')).toBe('-1');
    });
  });

  describe('notification surface', () => {
    it('mounts the notification surface exactly once', () => {
      expect(count('app-notification-list')).toBe(1);
    });

    it('mounts it inside the main region, so the skip link carries the user to it', () => {
      const surfaceWithinMain = requireElement('main').querySelector('app-notification-list');

      expect(surfaceWithinMain).not.toBeNull();
    });

    it('mounts it above the outlet, so an announcement is not something to scroll for', () => {
      const surface = requireElement('app-notification-list');
      const outlet = requireElement('router-outlet');

      expect(precedes(surface, outlet)).withContext('announcement before routed screen').toBeTrue();
    });

    it('contributes no landmark of its own, being a live region rather than a region', () => {
      // The surface is announced through a live region, which is not a landmark. If it
      // ever grew one it would be counted by the landmark inventory above, so this
      // assertion states the intent directly rather than leaving it implied.
      const surface = requireElement('app-notification-list');

      expect(surface.querySelectorAll('nav').length).toBe(0);
      expect(surface.querySelectorAll('main').length).toBe(0);
    });
  });

  describe('secondary navigation region', () => {
    it('is the rail itself, carrying the region class on its own host', () => {
      // The region is no longer a wrapper with a projection slot. The rail's host IS the
      // grid child, so the class and the component are the same element — which is what
      // keeps the grid placing a direct child of the shell.
      const region = requireElement('.shell__sidebar');

      expect(region.tagName.toLowerCase()).toBe('app-sidebar');
      expect(count('div.shell__sidebar')).toBe(0);
    });

    it('mounts the real rail component, not merely an element so named', () => {
      // Resolving by directive rather than by element name is the difference between
      // proving the component is mounted and proving a tag exists.
      const node = fixture.debugElement.query(By.directive(SidebarComponent));

      expect(node).not.toBeNull();
      expect(node.injector.get(SidebarComponent)).toBeInstanceOf(SidebarComponent);
    });

    it('writes no landmark and no accessible name of its own, leaving both to the rail', () => {
      // The shell must not annotate the region: the rail renders its own labelled
      // navigation landmark, and a second role or name here would compete with it.
      const region = requireElement('.shell__sidebar');

      expect(region.hasAttribute('role')).toBeFalse();
      expect(region.hasAttribute('aria-label')).toBeFalse();
    });

    it('renders no projection slot anywhere, so navigation cannot be omitted silently', () => {
      // THE REGRESSION THIS OWNERSHIP EXISTS TO PREVENT. A slot rendered nothing when a
      // mount site supplied nothing, and the stylesheet's `:empty` branch then collapsed
      // the region — shipping a console with no navigation, with no raise and no warning.
      // A shell that owns the rail cannot reach that state, and the absence of any
      // projected content is what proves the slot is gone.
      expect(host().querySelectorAll('ng-content').length).toBe(0);
      expect(requireElement('.shell__sidebar').childNodes.length).toBeGreaterThan(0);
    });
  });

  describe('session — the boundary this component owns', () => {
    // These specifications drive the REAL session store rather than component inputs,
    // because the shell resolves the session itself now. The banner's INPUTS are what is
    // asserted, not the banner's markup: the shell's contribution is the binding, and the
    // banner is another component's file.

    it('forwards a build-time application name when nothing is bound', () => {
      // The default is read from the component rather than from a literal or from the
      // environment module, so this holds whatever the configured name is while still
      // proving that a default reaches the banner rather than an empty string.
      expect(banner().applicationName).toBe(component.applicationName);
      expect(banner().applicationName.length).toBeGreaterThan(0);
    });

    it('forwards a bound application name', () => {
      setInput('applicationName', 'Administration');

      expect(banner().applicationName).toBe('Administration');
    });

    it('resolves no display name while no account is signed in', () => {
      expect(banner().userName).toBeUndefined();
    });

    it('resolves the signed-in display name from the session store', () => {
      holdSession();

      expect(banner().userName).toBe('Operator A');
    });

    it('falls back to the account key when the display name is blank', () => {
      // THE PUBLISHED CONTRACT, and it guards a concrete defect. The banner decides
      // whether an account is signed in by testing the name it was given, and the
      // sign-out control lives inside that cluster — so passing a blank name through
      // would leave an operator signed in with no way to sign out.
      holdSession({ displayName: '' });

      expect(banner().userName).toBe('operator.a');
    });

    it('treats a whitespace-only display name as absent, not as a name', () => {
      holdSession({ displayName: '   ' });

      expect(banner().userName).toBe('operator.a');
    });

    it('reports no name at all when neither the display name nor the account key is usable', () => {
      holdSession({ displayName: '  ', username: '  ' });

      expect(banner().userName).toBeUndefined();
    });

    it('forwards the in-flight flag from the store rather than from a local mirror', () => {
      holdSession();

      expect(banner().signingOut).toBeFalse();

      clickSignOut();

      expect(banner().signingOut).toBeTrue();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(banner().signingOut).toBeFalse();
    });

    it('offers no account address while no session is held', () => {
      expect(banner().accountProfileLink).toBeUndefined();
      expect(banner().accountPasswordLink).toBeUndefined();
    });

    it("composes the signed-in account's own profile and password addresses", () => {
      // MIGRATION: these two are the WHOLE of the console's account-scoped chrome, and they are the
      // only routes an ordinary account holder has to a screen it operates on its own behalf: every
      // navigation-rail entry requires a portal or host administrator.
      // `ManageUsers.ascx.vb:L439-L456` records that the legacy command bar offered both to an
      // account viewing itself, so this reproduces measured behaviour rather than inventing it.
      //
      // A third address, to the legacy `cmdServices` panel, was composed here and is withdrawn with
      // the route it named: the migration plan freezes the console at twenty-five screens and
      // declares no member-services address, so this component was handing the banner a link to a
      // screen the route table does not publish.
      holdSession({ userId: 7 });

      expect(banner().accountProfileLink).toBe('/users/7/profile');
      expect(banner().accountPasswordLink).toBe('/users/7/password');
    });

    it('composes an address naming account zero unchanged', () => {
      // ⚠ SENTINEL DISCIPLINE. The fixture's account key is zero, so this is a sentinel proof as
      // well as a composition one: a truthiness test or a `> 0` guard anywhere on the path from the
      // session to the rendered address would drop the affordance entirely for the first account the
      // installer creates. Account, role, page and module keys all seed at zero or below in this
      // schema, so that is a defect class rather than a preference.
      holdSession({ userId: 0 });

      expect(banner().accountProfileLink).toBe('/users/0/profile');
      expect(banner().accountPasswordLink).toBe('/users/0/password');
    });

    it('revokes the session server-side when the operator asks to sign out', () => {
      holdSession();

      clickSignOut();

      const revocation = httpMock.expectOne(AUTH_ENDPOINTS.logout);

      expect(revocation.request.method).toBe('POST');

      revocation.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('ends the session through the lifecycle service rather than through the session store', () => {
      // ⚠ THE DISTINCTION IS THE WHOLE POINT. The store's own sign-out discards the
      // credentials and the identity and knows nothing about the portals, accounts, roles
      // or exported module documents the domain stores still hold. Calling the store would
      // leave every one of those resident and legible to whoever signs in next.
      const endSession = spyOn(session, 'endSession').and.callThrough();

      holdSession();
      clickSignOut();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(endSession).toHaveBeenCalledTimes(1);
    });

    it('leaves the application unauthenticated once the revocation settles', () => {
      holdSession();

      expect(authStore.isAuthenticated()).toBeTrue();

      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(authStore.currentUser()).toBeNull();
      expect(banner().userName).toBeUndefined();
    });

    it('sends the operator to the sign-in screen, carrying no return address', () => {
      // NO RETURN ADDRESS, DELIBERATELY. The route gates attach one when they INTERRUPT a
      // navigation; signing out is not an interruption, and restoring an address that named
      // a record the next operator has no right to know exists would defeat the discard.
      holdSession();
      clickSignOut();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);
    });

    it('still reaches the sign-in screen when the revocation request fails', () => {
      // The session has already been discarded by the time the failure arrives, so leaving
      // the operator on an administration screen would strand them on a view whose every
      // request is about to be refused.
      holdSession();
      clickSignOut();

      httpMock
        .expectOne(AUTH_ENDPOINTS.logout)
        .flush({ title: 'Refused' }, { status: 500, statusText: 'Server Error' });
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);
      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('issues one revocation only, however many times the gesture arrives in a single tick', () => {
      // The re-entry guard closes a window the markup cannot: both of the banner's guards
      // read the flag as it stood at the last change detection, whereas the store sets the
      // phase synchronously at subscribe time.
      holdSession();

      const control = host().querySelector<HTMLButtonElement>('button.app-header__logout');

      control?.click();
      control?.click();
      fixture.detectChanges();

      // ⚠ COUNTED, BECAUSE "EXACTLY ONE" IS THE WHOLE CLAIM. `expectOne` does enforce it - it throws on a
      // second match - but it asserts by throwing, so the runner records no expectation and reports this
      // spec as claiming nothing. Matching the set and sizing it makes the number itself the assertion,
      // which is the property under test rather than a side effect of how it is checked.
      const revocations = httpMock.match(AUTH_ENDPOINTS.logout);

      expect(revocations)
        .withContext('two gestures in one tick, one revocation')
        .toHaveSize(1);

      revocations[0]?.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('issues nothing at all when no session is held', () => {
      // With no session the banner renders no sign-out control, so the gesture cannot even
      // be made. `httpMock.verify()` in the teardown is what proves nothing was sent.
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });
  });

  describe('instrumentation', () => {
    it('logs nothing while composing, rendering or being interacted with', () => {
      // Structured logging belongs to the API and to the workspace's own notification
      // surface. A layout primitive that logged would emit on every screen, and a
      // console statement left behind during debugging is exactly the kind of thing
      // that reaches production and leaks whatever it was inspecting. The spies are
      // captured in variables and asserted through those, so no direct member access
      // on the console object appears anywhere in this file.
      const logSpy = spyOn(console, 'log');
      const warnSpy = spyOn(console, 'warn');
      const errorSpy = spyOn(console, 'error');
      const infoSpy = spyOn(console, 'info');
      const debugSpy = spyOn(console, 'debug');

      // A fresh fixture is built INSIDE the spied window so that construction and the
      // first render are observed too, not merely the interaction afterwards.
      const probe = TestBed.createComponent(ShellComponent);
      probe.detectChanges();

      const probeElement = probe.nativeElement as HTMLElement;
      probeElement.querySelector<HTMLElement>('a.shell__skip-link')?.click();

      probe.componentRef.setInput('applicationName', 'Administration');
      probe.detectChanges();

      expect(logSpy).not.toHaveBeenCalled();
      expect(warnSpy).not.toHaveBeenCalled();
      expect(errorSpy).not.toHaveBeenCalled();
      expect(infoSpy).not.toHaveBeenCalled();
      expect(debugSpy).not.toHaveBeenCalled();
    });
  });
});


/**
 * The shell as a child portal addressed beneath a path segment renders it.
 *
 * A suite of its own rather than a case inside the one above, because the thing under test is
 * an INJECTED CONSTANT the shell reads at construction: `APP_BASE_HREF` cannot be changed after
 * the injector is built, so proving both deployments requires two injectors.
 *
 * ## What this pins, and why the main suite cannot
 *
 * `Router.url` is internal — expressed relative to the base href — so at `/acme/` the router
 * reports `/roles` for the screen whose real address is `/acme/roles`. Every `routerLink` in the
 * workspace already converts that for its own `href`; the shell's skip link is the one address
 * in the application composed by hand, and it published the internal form. The published form
 * resolves against the bare host, and the bare host is the PARENT tenant, so the one
 * hand-composed address in the console pointed at a different portal's data.
 *
 * The main suite provides `/` and therefore cannot see this: at the root deployment the
 * conversion is the identity, which is exactly why the defect survived a full specification.
 * That is the argument for this suite existing rather than a stronger assertion up there.
 */
describe('ShellComponent under a tenant path base href', () => {
  /** The prefix a child portal is addressed beneath, matching the alias `localhost:4200/acme`. */
  const TENANT_BASE_HREF = '/acme/';

  let fixture: ComponentFixture<ShellComponent>;
  let router: Router;
  let httpMock: HttpTestingController;

  /** @returns The skip link's `href` exactly as the attribute carries it. */
  function skipLinkHref(): string {
    const element = fixture.nativeElement as HTMLElement;
    const link = element.querySelector<HTMLAnchorElement>('a.shell__skip-link');

    if (link === null) {
      throw new Error('expected the shell to render a skip link');
    }

    // The ATTRIBUTE rather than the property, deliberately. The property is resolved against
    // the document and would read as an absolute URL, which would hide a missing prefix behind
    // the runner's own origin; the attribute is the value a browser hands to "open in new tab".
    return link.getAttribute('href') ?? '';
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideRouter([{ path: DEEP_PROBE_PATH, component: RouteProbeComponent }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_BASE_HREF, useValue: TENANT_BASE_HREF },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);

    fixture = TestBed.createComponent(ShellComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it("carries the tenant's prefix on the skip link, so the one hand-composed address names this portal", async () => {
    await router.navigateByUrl(`/${DEEP_PROBE_PATH}`);
    fixture.detectChanges();

    // The address a middle-click, an "open in new tab" or a "copy link address" consumes. The
    // internal form `/deep/route/below/the/root#main-content` is what this used to read, and it
    // resolves against the bare host.
    expect(skipLinkHref()).toBe(`${TENANT_BASE_HREF}${DEEP_PROBE_PATH}#main-content`);
  });

  it('never publishes the router-internal address, which would name the parent tenant', async () => {
    const onArrival = skipLinkHref();

    await router.navigateByUrl(`/${DEEP_PROBE_PATH}`);
    fixture.detectChanges();

    const afterNavigating = skipLinkHref();

    // Asserted on BOTH the initial address and one reached by navigating, because the href is
    // composed twice by two different callers - once in a field initialiser and once from the
    // `NavigationEnd` handler - and either could have been left converting nothing.
    for (const published of [onArrival, afterNavigating]) {
      expect(published.startsWith(TENANT_BASE_HREF)).toBeTrue();
      expect(published.startsWith(`/${DEEP_PROBE_PATH}`)).toBeFalse();
    }

    expect(afterNavigating).not.toBe(onArrival);
  });

  it('still ends in the fragment naming the main region, exactly once', () => {
    const element = fixture.nativeElement as HTMLElement;
    const region = element.querySelector('main');

    if (region === null) {
      throw new Error('expected the shell to render a main region');
    }

    // The prefix must not have displaced the part of the address that does the work: the
    // fragment is what makes this a skip link rather than a link to the current screen.
    expect(region.id).not.toBe('');
    expect(skipLinkHref().endsWith(`#${region.id}`)).toBeTrue();
    expect(skipLinkHref().split('#').length).toBe(2);
  });

  it('still moves focus into the main region, so the prefix changed the address and nothing else', () => {
    const element = fixture.nativeElement as HTMLElement;
    const link = element.querySelector<HTMLAnchorElement>('a.shell__skip-link');
    const region = element.querySelector('main');

    link?.click();

    // The behaviour a keyboard operator experiences is unchanged by the conversion. This is the
    // case that would have caught a `prepareExternalUrl` call placed on the element identifier
    // as well as on the path, which would have left the fragment naming nothing.
    expect(document.activeElement).toBe(region);
  });
});
