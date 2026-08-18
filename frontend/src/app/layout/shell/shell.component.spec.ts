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
import {
  UNSAVED_CHANGES_PROMPT,
  UnsavedChangesTracker,
} from '../../core/guards/unsaved-changes.guard';
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
    mustChangePassword: false,
    mustUpdateProfile: false,
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

    // THE FIRST TAB STOP IS ALSO A POINTER TARGET ONCE REVEALED - QA-18.
    it('meets the target minimum once revealed, and keeps its collapsed geometry until then', () => {
      const link = requireElement('a.shell__skip-link');

      // COLLAPSED FIRST, because this is the half that a bare `min-block-size` would have broken:
      // `min-block-size` clamps `block-size` regardless of specificity, so an unscoped rule would have given
      // the HIDDEN link a 44px box sitting over the top-left corner of the page.
      expect(link.getBoundingClientRect().height).toBeLessThan(44);

      link.focus();
      fixture.detectChanges();

      // Programmatic focus matches `:focus-visible` in this engine, which is what reveals the link. Guarded
      // rather than assumed, so the specification fails loudly if the reveal ever stops happening instead of
      // silently asserting the collapsed box a second time.
      expect(link.matches(':focus-visible')).toBeTrue();
      expect(link.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
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

    // ---------------------------------------------------------------------------------------------
    // SIGNING OUT OF A DIRTY SCREEN
    // ---------------------------------------------------------------------------------------------

    // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. Logout discarded a dirty form in SILENCE while navigating
    // from the very same form raised the confirmation. Ordering was the cause: the revocation and the local
    // teardown both ran before the router could reach `canDeactivate`, so the question came too late to be
    // answerable - and answering "no" would have stranded the operator on unsaved work whose session had
    // already ended.

    describe('signing out while a screen holds unsaved entry', () => {
      let confirmSpy: jasmine.Spy<(message?: string) => boolean>;

      /** Whether the stand-in form currently holds unsaved entry. Read by the probe below on every ask. */
      let screenIsDirty = true;

      beforeEach(() => {
        confirmSpy = spyOn(globalThis, 'confirm').and.returnValue(true);
        screenIsDirty = true;

        // A probe standing in for a mounted form. Registered outside any component, so it stays for the
        // duration of the case and nothing else can release it, and reads a mutable flag so one case can
        // present a CLEAN screen through the very same registration.
        TestBed.runInInjectionContext(() => {
          TestBed.inject(UnsavedChangesTracker).watch(() => screenIsDirty);
        });
      });

      it('asks BEFORE revoking anything, and revokes nothing when the operator declines', () => {
        confirmSpy.and.returnValue(false);

        holdSession();
        clickSignOut();

        expect(confirmSpy)
          .withContext('the question is put once, in the application\u2019s own words')
          .toHaveBeenCalledOnceWith(UNSAVED_CHANGES_PROMPT);
        expect(httpMock.match(AUTH_ENDPOINTS.logout))
          .withContext('\u26a0 NOTHING IRREVERSIBLE HAPPENED: no credential was revoked')
          .toEqual([]);
        expect(tokens.accessToken())
          .withContext('and the session the operator kept is still usable')
          .not.toBeNull();
        expect(navigate)
          .withContext('and they are still on the screen holding their work')
          .not.toHaveBeenCalled();
      });

      it('proceeds once, without asking a second time, when the operator agrees', () => {
        holdSession();
        clickSignOut();

        expect(confirmSpy).toHaveBeenCalledTimes(1);

        httpMock
          .expectOne(AUTH_ENDPOINTS.logout)
          .flush(null, { status: 204, statusText: 'No Content' });
        fixture.detectChanges();

        expect(confirmSpy)
          .withContext('the departure carries the answer already given; the router asks nothing more')
          .toHaveBeenCalledTimes(1);
        expect(navigate).toHaveBeenCalled();
      });

      it('asks nothing at all when the screen holds no unsaved entry', () => {
        screenIsDirty = false;

        holdSession();
        clickSignOut();

        expect(confirmSpy)
          .withContext('nothing is at stake, so an ordinary sign-out is not interrupted')
          .not.toHaveBeenCalled();

        httpMock
          .expectOne(AUTH_ENDPOINTS.logout)
          .flush(null, { status: 204, statusText: 'No Content' });
        fixture.detectChanges();

        expect(navigate)
          .withContext('and it completes exactly as it did before this gate existed')
          .toHaveBeenCalled();
      });
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

  describe('the room it reserves for the pinned header band', () => {
    /** The property `_reset.scss` reads as the document's scroll padding above the side-by-side step. */
    const BAND_PROPERTY = '--layout-header-block-size';

    /** The value currently published on the document element, or the empty string when none is. */
    function published(): string {
      return document.documentElement.style.getPropertyValue(BAND_PROPERTY).trim();
    }

    afterEach(() => {
      // The publication is a deliberate write OUTSIDE this component's own subtree - it has to be, because
      // the declaration consuming it sits on `html` and a custom property flows downward only. It is
      // withdrawn here rather than left to leak into every later spec in the run.
      document.documentElement.style.removeProperty(BAND_PROPERTY);
    });

    it("publishes the band's measured height rather than the token's declared value", async () => {
      await fixture.whenStable();

      const band: HTMLElement = requireElement('.shell__header');
      const measured: number = band.getBoundingClientRect().height;

      expect(measured).withContext('the band has a height worth publishing').toBeGreaterThan(0);
      expect(published())
        .withContext('the document element carries the measurement, in pixels')
        .toBe(`${measured}px`);
    });

    it('never publishes a zero height, so the reservation cannot silently vanish', async () => {
      await fixture.whenStable();

      const beforeCollapse: string = published();

      expect(beforeCollapse).withContext('a real measurement was published first').not.toBe('');

      const band: HTMLElement = requireElement('.shell__header');

      band.style.display = 'none';

      expect(band.getBoundingClientRect().height)
        .withContext('the band now measures nothing at all')
        .toBe(0);

      (component as unknown as { publishHeaderBlockSize(): void }).publishHeaderBlockSize();

      // A zero would reserve nothing and restore the defect, so the last real measurement is kept.
      expect(published())
        .withContext('the last real measurement survives a zero reading')
        .toBe(beforeCollapse);

      band.style.removeProperty('display');
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
  // ⚠ MINOR (responsive) — the content column ran full-bleed at 1920, and nothing on the page was sticky.
  describe('the content column measure and the sticky header', () => {
  /** One flattened stylesheet rule: its selector, its declarations, and the media query guarding it. */
  interface FlatLayoutRule {
    readonly selectorText: string;
    readonly style: CSSStyleDeclaration;
    readonly media: string | null;
  }

  /**
   * Every style rule reachable from the document, including those nested inside media queries.
   *
   * \u26a0 A RULE SCAN RATHER THAN A COMPUTED READING, AND THE REASON IS THE FIXTURE. The declaration under
   * test is gated behind a `min-width` media query, and a fixture is rendered at whatever width the runner
   * happens to give it - so a computed value would assert the runner's width rather than the stylesheet's
   * intent, and would flip between a wide runner and a narrow one. Nested rules are therefore walked
   * unconditionally: this measures what the stylesheet DECLARES.
   *
   * The test build loads `src/styles.scss`, which is what puts these rules in `document.styleSheets`.
   *
   * @returns The flattened rules.
   */
  function flattenedLayoutRules(): readonly FlatLayoutRule[] {
    const collected: FlatLayoutRule[] = [];

    const walk = (rules: CSSRuleList, media: string | null): void => {
      Array.from(rules).forEach((rule) => {
        if (rule instanceof CSSMediaRule) {
          walk(rule.cssRules, rule.conditionText);

          return;
        }

        if (rule instanceof CSSStyleRule) {
          collected.push({ selectorText: rule.selectorText, style: rule.style, media });
        }
      });
    };

    Array.from(document.styleSheets).forEach((sheet) => {
      try {
        walk(sheet.cssRules, null);
      } catch {
        return;
      }
    });

    return collected;
  }

    /**
     * Whether one selector-list part is exactly the given class compound.
     *
     * \u26a0 THE ATTRIBUTE SUFFIX HAS TO BE TOLERATED, AND AN EXACT COMPARISON FAILED BECAUSE OF IT. Angular's
     * emulated encapsulation rewrites every component rule to carry a scoping attribute, so the authored
     * `.app-sidebar` reaches the stylesheet as `.app-sidebar[_ngcontent-ng-c123]` with a suffix that changes
     * every build. A plain equality check therefore found none of them and the first version of these
     * specifications reported zero rules against correct code.
     *
     * @param part One comma-separated part of a selector list.
     * @param compound The class compound to match, including its leading dot.
     * @returns Whether the part is that compound, with or without a scoping attribute.
     */
    function isCompound(part: string, compound: string): boolean {
      const escaped: string = compound.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

      return new RegExp(`^${escaped}(\\[[^\\]]*\\])*$`).test(part.trim());
    }

    /** The rules whose selector list carries the given compound. */
    function rulesFor(compound: string): readonly FlatLayoutRule[] {
      return flattenedLayoutRules().filter((rule) =>
        rule.selectorText.split(',').some((part) => isCompound(part, compound)),
      );
    }

    /** Resolves a CSS length expression to pixels by letting the browser do the conversion. */
    function resolvedLength(expression: string): number {
      const probe: HTMLDivElement = document.createElement('div');
      probe.style.blockSize = expression;
      document.body.appendChild(probe);
      const resolved: number = probe.getBoundingClientRect().height;
      probe.remove();

      return resolved;
    }

    it('caps the main region at the widest breakpoint step and centres it', () => {
      // ⚠ A BARE ELEMENT RATHER THAN A RENDERED SHELL, DELIBERATELY. The declarations under test are global
      // rules keyed on the class, not component styles, so what has to be proven is that carrying the class is
      // enough to get the measure. Rendering the whole shell would drag in a session, a router and an HTTP
      // backend to assert something none of them influences - and would leave the assertion silently dependent
      // on that setup continuing to work.
      const main: HTMLElement = document.createElement('main');
      main.className = 'shell__main';
      document.body.appendChild(main);

      const resolved: CSSStyleDeclaration = getComputedStyle(main);

      // ⚠ THE DISCRIMINATING VALUE IS A DEFINITE LENGTH. Before the fix this computed to `none`, and at 1920
      // that put a two-character value in a 167.8-pixel column and stretched a single-line text input to 1498
      // pixels. `none` and a definite cap are exactly what the two implementations disagree about.
      const cap: number = Number.parseFloat(resolved.maxInlineSize);

      expect(resolved.maxInlineSize).withContext('a measure is declared').not.toBe('none');
      expect(cap).toBeCloseTo(resolvedLength('80rem'), 0);

      main.remove();

      // The centring half, asserted from the STYLESHEET rather than from the computed value.
      // ⚠ `getComputedStyle` CANNOT EVIDENCE AN AUTO MARGIN, and the first version of this specification
      // failed against correct code because of it: Chrome reports the RESOLVED value, which is `0px` for a
      // margin authored as `auto`, so the check read "Expected '0px' to be 'auto'" while the declaration was
      // present and working. A browser measurement of the live page proved the centring geometrically instead -
      // 220.00 pixels of gutter on each side of a 1280-wide column inside a 1720-wide track at 1920 - and here
      // the declaration itself is what is asserted.
      const centred = rulesFor('.shell__main').filter(
        (rule) => rule.style.getPropertyValue('margin-inline').trim() === 'auto',
      );

      expect(centred.length).withContext('the measure is centred in its track').toBeGreaterThan(0);
    });

    it('pins the header at the side-by-side step and nowhere narrower', () => {
      const sticky = rulesFor('.shell__header').filter(
        (rule) => rule.style.getPropertyValue('position').trim() === 'sticky',
      );

      expect(sticky.length).withContext('the header is pinned').toBeGreaterThan(0);
      sticky.forEach((rule) => {
        // The CSSOM normalises a zero length, so the authored `0` reads back as `0px`.
        expect(rule.style.getPropertyValue('inset-block-start').trim()).toBe('0px');

        // ⚠ THE GATE IS THE DISCRIMINATING PART, and it is a measurement rather than a preference: the header
        // measures 69 pixels at 1280 but 177 at 375, where its user-links cluster wraps. Pinning 177 pixels to
        // an 800-pixel viewport spends 22% of the screen on chrome, so an ungated rule would be a regression on
        // the narrowest devices even though it satisfies "the header is sticky".
        expect(rule.media).withContext('gated to the side-by-side step').toMatch(/min-width/);
      });
    });

    it('takes its stacking order from the token scale rather than a literal', () => {
      // The token vocabulary reserves every z-index to its own scale, and `--z-index-sticky` existed from the
      // start with no user at all. A literal here would be the first value in the application outside it.
      const sticky = rulesFor('.shell__header').filter(
        (rule) => rule.style.getPropertyValue('position').trim() === 'sticky',
      );

      expect(sticky.length).toBeGreaterThan(0);
      sticky.forEach((rule) => {
        expect(rule.style.getPropertyValue('z-index').trim()).toBe('var(--z-index-sticky)');
      });
    });

    // ⚠ MAJOR - THE PINNED BAND CONCEALED WHATEVER THE BROWSER SCROLLED TO THE TOP OF THE VIEWPORT.
    // Measured on the account listing at 1280x720: activating "Skip to main content" set `scrollY` to
    // exactly 69, the main region's own document offset, which left FOUR elements 100% concealed with zero
    // pixels visible - the `h1` and all three page actions, three of them focusable links.
    it('reserves the pinned band at the top of every scroll, at the same step and from the same tokens', () => {
      const declared: readonly FlatLayoutRule[] = rulesFor('html').filter(
        (rule) => rule.style.getPropertyValue('scroll-padding-block-start').trim() !== '',
      );

      expect(declared.length)
        .withContext('the document reserves room for the pinned band')
        .toBeGreaterThan(0);
      expect(resolvedLength('var(--layout-header-block-size)'))
        .withContext('the band-height token resolves to a real length')
        .toBeGreaterThan(0);

      declared.forEach((rule) => {
        const value: string = rule.style.getPropertyValue('scroll-padding-block-start').trim();

        // THE PUBLISHED HEIGHT RATHER THAN A NUMBER, and that is the discriminating part. The band is 69
        // pixels only while the account cluster fits on one line; a literal would under-reserve the moment
        // it wraps and reopen the defect silently, which is why `shell.component.ts` measures it instead.
        expect(value)
          .withContext('the reservation is the published band height')
          .toContain('var(--layout-header-block-size)');
        expect(value)
          .withContext('with a token-sized gap, so the focus ring clears the band and not just the box')
          .toContain('var(--space-2)');
        expect(value)
          .withContext('no hard-coded length can drift from the band it is meant to match')
          .not.toMatch(/\d+px/);

        // Gated to the SAME step as the pin: below it the header is `static` and 177 pixels tall, so
        // reserving that there would push every screen down for a band that scrolls away by itself.
        expect(rule.media).withContext('gated to the side-by-side step').toMatch(/min-width/);
      });
    });

  });

});
