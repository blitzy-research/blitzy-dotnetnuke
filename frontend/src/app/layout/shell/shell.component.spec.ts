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

/** SPECIFICATION FOR THE APPLICATION SHELL */

const FUTURE_SESSION_EXPIRY_UTC: string = new Date(Date.now() + 60 * 60 * 1000).toISOString();

/**
 * A held session, exactly as the sign-in endpoint answers one. Written through the token custodian rather
 * than by posting credentials, because every assertion here is about the CHROME reading a session rather
 * than about acquiring one, and staging one through the sign-in endpoint would add a request each case
 * would then have to account for.
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

const DEEP_PROBE_PATH = 'deep/route/below/the/root';

/** The inert screen `DEEP_PROBE_PATH` activates. */
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

  /** Returns the shell's host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Returns the one element matching `selector` within the shell, failing with a descriptive message when
   * it is absent.
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
   * @param name The input to set.
   * @param value The value to set it to, before any declared transform.
   */
  function setInput(name: 'applicationName', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /**
   * Reports whether `first` precedes `second` in document order. Comparing positions is preferred over
   * indexing into a child list because it holds regardless of how deeply either element sits, which keeps
   * the tab-order assertion independent of the wrapper decisions inside the composed children.
   *
   * @param first The element expected to come first.
   * @param second The element expected to follow it.
   * @returns True when `second` follows `first`.
   */
  function precedes(first: HTMLElement, second: HTMLElement): boolean {
    return (first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING) > 0;
  }

  /**
   * Returns the banner component instance the shell composes. Resolved through the node injector so the
   * returned value is typed, which is what lets the forwarding specifications below assert on the
   * banner's INPUTS rather than on the banner's internal markup.
   */
  function banner(): HeaderComponent {
    const node = fixture.debugElement.query(By.directive(HeaderComponent));

    if (node === null) {
      throw new Error('expected the shell to compose the banner component');
    }

    return node.injector.get(HeaderComponent);
  }

  /** Returns the footer component instance the shell composes. */
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
        // A router is required three times over: the shell renders a router outlet, the banner it composes
        // renders a router link, and the rail renders router links. None of the three needs a route table
        // at all.
        provideRouter([{ path: DEEP_PROBE_PATH, component: RouteProbeComponent }]),
        // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()` REPLACES the
        // backend the real client installed, so reversing the two would leave the live backend in place and
        // every expectation below would find nothing.
        provideHttpClient(),
        provideHttpClientTesting(),
        // The base href every case below except the tenant suite at the foot of this file assumes, declared
        // rather than inherited.
        { provide: APP_BASE_HREF, useValue: '/' },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    session = TestBed.inject(SessionLifecycleService);
    router = TestBed.inject(Router);

    // Spied before the component is created so that no navigation can escape into the empty route table.
    // `resolveTo` rather than `stub`, because the component chains a `catch` onto the returned promise and
    // an undefined return would throw there.
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
      expect(host().classList.contains('shell')).toBeTrue();
    });
  });

  describe('region composition', () => {
    it('renders exactly five element children, each carrying its published grid area', () => {
      const regions = Array.from(host().children).map((child) => child.className);

      expect(regions.length).toBe(5);
      expect(regions[0]).toContain('shell__skip-link');
      expect(regions[1]).toContain('shell__header');
      expect(regions[2]).toContain('shell__sidebar');
      expect(regions[3]).toContain('shell__main');
      expect(regions[4]).toContain('shell__footer');

      expect(host().children[2].tagName.toLowerCase()).toBe('app-sidebar');
    });

    it('renders exactly one router outlet, which is the application\'s only outlet', () => {
      expect(count('router-outlet')).toBe(1);
    });

    it('renders exactly one main region', () => {
      expect(count('main')).toBe(1);
    });

    it('places the router outlet inside the main region', () => {
      // This is the accessibility contract rather than decoration: the routed screen has to be inside the
      // landmark the skip link moves focus to, or the skip link lands the user somewhere that does not
      // contain the content they asked for.
      const outletWithinMain = requireElement('main').querySelector('router-outlet');

      expect(outletWithinMain).not.toBeNull();
    });

    it('composes the banner, the rail and the footer exactly once each', () => {
      expect(count('app-header')).toBe(1);
      expect(count('app-sidebar')).toBe(1);
      expect(count('app-footer')).toBe(1);
    });

    it('composes the real banner and footer components, not merely elements so named', () => {
      expect(banner()).toBeInstanceOf(HeaderComponent);
      expect(footerBand()).toBeInstanceOf(FooterComponent);
    });
  });

  describe('landmark inventory', () => {
    it('emits exactly one banner landmark and one footer landmark', () => {
      expect(count('header')).toBe(1);
      expect(count('footer')).toBe(1);
    });

    it('emits exactly one navigation landmark, which the rail it owns supplies', () => {
      expect(count('app-sidebar')).toBe(1);

      // ⚠ AND THE LANDMARK IS CONDITIONAL ON THE SESSION, WHICH IS WHY THIS CASE HOLDS ONE. Every
      // destination the rail offers is an administration screen, so the rail withholds the landmark
      // entirely rather than announcing a region a reader can navigate to and find nothing in.
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
      // An anchor without an address is not focusable and is not announced as a link. The address is
      // therefore load-bearing even though its default action is cancelled, which is exactly the sort of
      // thing a reader deletes as redundant.
      const link = requireElement('a.shell__skip-link');

      expect(link.tagName.toLowerCase()).toBe('a');
      expect(link.hasAttribute('href')).toBeTrue();
    });

    it('names a fragment that the main region actually carries', () => {
      const target = requireElement('a.shell__skip-link').getAttribute('href') ?? '';
      const region = requireElement('main');

      expect(region.id).not.toBe('');
      expect(target.endsWith(`#${region.id}`)).toBeTrue();
      expect(target.split('#').length).toBe(2);
    });

    it('qualifies its address with the path currently showing, so the fragment cannot leave the document', async () => {
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

      // Markup alone cannot distinguish a working affordance from a broken one here: the fragment is
      // correct, the target exists and the link is the first tab stop in both the working and the broken
      // arrangement. What the affordance exists to DO is move focus, so that is what is asserted.
      expect(document.activeElement).toBe(region);
    });

    it('cancels its default action, guarding a full-document reload that was measured', () => {
      const link = requireElement('a.shell__skip-link');
      const activation = new MouseEvent('click', { bubbles: true, cancelable: true });

      link.dispatchEvent(activation);

      // The reload itself cannot be reproduced inside the runner, because the specification's document is
      // the runner's own. What CAN be asserted is the single condition that prevents it.
      expect(activation.defaultPrevented).withContext('skip link default action').toBeTrue();
    });
  });

  describe('tab order', () => {
    it('follows source order from the skip link through to the footer', () => {
      // Placement is the stylesheet's decision, so visual position is never a reason to reorder the
      // template.
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
      // The component stylesheet selects the bare element name, and a native element makes the
      // accessibility tree correct without a single attribute. Swapping in a generic element with an
      // explicit role would break the first and leave the second only apparently intact.
      expect(requireElement('.shell__main').tagName.toLowerCase()).toBe('main');
    });

    it('writes no explicit role, because a native main is already the main landmark', () => {
      expect(requireElement('main').hasAttribute('role')).toBeFalse();
    });

    it('is programmatically focusable without joining the tab order', () => {
      // This is what makes the skip link able to move focus rather than merely scroll the viewport. Without
      // it some browsers scroll but leave focus behind, and the next Tab press returns the user to the
      // banner they were trying to skip.
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
      const surface = requireElement('app-notification-list');

      expect(surface.querySelectorAll('nav').length).toBe(0);
      expect(surface.querySelectorAll('main').length).toBe(0);
    });
  });

  describe('secondary navigation region', () => {
    it('is the rail itself, carrying the region class on its own host', () => {
      // The region is no longer a wrapper with a projection slot. The rail's host IS the grid child, so the
      // class and the component are the same element — which is what keeps the grid placing a direct child
      // of the shell.
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
      expect(host().querySelectorAll('ng-content').length).toBe(0);
      expect(requireElement('.shell__sidebar').childNodes.length).toBeGreaterThan(0);
    });
  });

  describe('session — the boundary this component owns', () => {
    it('forwards a build-time application name when nothing is bound', () => {
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
      holdSession({ userId: 7 });

      expect(banner().accountProfileLink).toBe('/users/7/profile');
      expect(banner().accountPasswordLink).toBe('/users/7/password');
    });

    it('composes an address naming account zero unchanged', () => {
      // ⚠ SENTINEL DISCIPLINE. The fixture's account key is zero, so this is a sentinel proof as well as a
      // composition one: a truthiness test or a `> 0` guard anywhere on the path from the session to the
      // rendered address would drop the affordance entirely for the first account the installer creates.
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
      // ⚠ THE DISTINCTION IS THE WHOLE POINT. The store's own sign-out discards the credentials and the
      // identity and knows nothing about the portals, accounts, roles or exported module documents the
      // domain stores still hold.
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
      holdSession();
      clickSignOut();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);
    });

    it('still reaches the sign-in screen when the revocation request fails', () => {
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
      // The re-entry guard closes a window the markup cannot: both of the banner's guards read the flag as
      // it stood at the last change detection, whereas the store sets the phase synchronously at subscribe
      // time.
      holdSession();

      const control = host().querySelector<HTMLButtonElement>('button.app-header__logout');

      control?.click();
      control?.click();
      fixture.detectChanges();

      // ⚠ COUNTED, BECAUSE "EXACTLY ONE" IS THE WHOLE CLAIM. `expectOne` does enforce it - it throws on a
      // second match - but it asserts by throwing, so the runner records no expectation and reports this
      // spec as claiming nothing.
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

/** The shell as a child portal addressed beneath a path segment renders it. */
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

    expect(skipLinkHref()).toBe(`${TENANT_BASE_HREF}${DEEP_PROBE_PATH}#main-content`);
  });

  it('never publishes the router-internal address, which would name the parent tenant', async () => {
    const onArrival = skipLinkHref();

    await router.navigateByUrl(`/${DEEP_PROBE_PATH}`);
    fixture.detectChanges();

    const afterNavigating = skipLinkHref();

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

    // The behaviour a keyboard operator experiences is unchanged by the conversion. This is the case that
    // would have caught a `prepareExternalUrl` call placed on the element identifier as well as on the
    // path, which would have left the fragment naming nothing.
    expect(document.activeElement).toBe(region);
  });
});
