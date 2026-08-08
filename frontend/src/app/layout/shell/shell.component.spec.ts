import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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
  expiresAtUtc: '2030-01-01T00:00:00Z',
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
        // banner it composes renders a router link, and the rail renders router links. An
        // empty route table satisfies all three without any navigation occurring.
        provideRouter([]),
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
      const target = requireElement('a.shell__skip-link').getAttribute('href') ?? '';
      const region = requireElement('main');

      expect(region.id).not.toBe('');
      expect(target).toBe(`#${region.id}`);
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
      expect(banner().accountServicesLink).toBeUndefined();
    });

    it("composes the signed-in account's own subscriptions address", () => {
      // MIGRATION: this is the one account-scoped affordance in the console's chrome, and the only
      // route an ordinary account holder has to a screen it operates on its own behalf.
      // `Website/admin/Users/MemberServices.ascx` was a tab an administrator never saw
      // (`ManageUsers.ascx.vb` L61-L66), reached by the signed-in account from the portal's own user
      // affordance - a skin object, and skinning is out of scope - so this band is the equivalent.
      holdSession({ userId: 7 });

      expect(banner().accountServicesLink).toBe('/users/7/services');
    });

    it('composes an address naming account zero unchanged', () => {
      // ⚠ SENTINEL DISCIPLINE. The fixture's account key is zero, so this is a sentinel proof as
      // well as a composition one: a truthiness test or a `> 0` guard anywhere on the path from the
      // session to the rendered address would drop the affordance entirely for the first account the
      // installer creates. Account, role, page and module keys all seed at zero or below in this
      // schema, so that is a defect class rather than a preference.
      holdSession({ userId: 0 });

      expect(banner().accountServicesLink).toBe('/users/0/services');
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

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
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
