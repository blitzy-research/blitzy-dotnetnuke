import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { throwError } from 'rxjs';

import { AppComponent } from './app.component';
import { AUTH_ENDPOINTS } from './core/config/api-endpoints';
import { TokenStorageService } from './core/services/token-storage.service';
import { AuthStore } from './core/state/auth.store';
import { SessionLifecycleService } from './core/state/session-lifecycle.service';
import { environment } from '../environments/environment';

import type { AuthSession } from './core/models/auth.model';

// Every address asserted below is RELATIVE, and that is a load-bearing property of this workspace rather
// than a stylistic preference.

const FUTURE_SESSION_EXPIRY_UTC: string = new Date(Date.now() + 60 * 60 * 1000).toISOString();

/**
 * A held session, in the shape the token custodian stores. The token values are obvious placeholders
 * rather than anything resembling a real credential: nothing here parses them, and a value that looked
 * like a token would invite somebody to try it.
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
    isPortalAdministrator: true,
    mustChangePassword: false,
    mustUpdateProfile: false,
    roles: ['Administrators'],
    permissions: ['EDIT'],
  },
};

describe('AppComponent', () => {
  let fixture: ComponentFixture<AppComponent>;
  let component: AppComponent;
  let httpMock: HttpTestingController;
  let tokens: TokenStorageService;
  let authStore: AuthStore;
  let session: SessionLifecycleService;
  let router: Router;
  let navigate: jasmine.Spy;

  /** Returns the root component's host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        // The shell renders a router outlet and the banner inside it renders a router link, so a router
        // must be present.
        provideRouter([]),
        // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()` REPLACES the
        // backend the real client installed, so reversing the two would leave the live backend in place and
        // every expectation below would find nothing.
        provideHttpClient(),
        provideHttpClientTesting(),
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

    fixture = TestBed.createComponent(AppComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    // Proves no request was left outstanding by any case, which is the assertion that
    // catches a revocation issued twice as reliably as a count does.
    httpMock.verify();
  });

  /**
   * Establishes a held session, exactly as the sign-in flow would, and renders it. Written through the
   * token custodian rather than by posting credentials, because every assertion here is about the CHROME
   * reading a session rather than about acquiring one, and staging one through the sign-in endpoint would
   * add a request each case would then have to account for.
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

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone, so it can be bootstrapped without a module', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });

    it('is selected by the element name the document declares', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { selectors?: unknown[][] };
        }
      ).ɵcmp;

      expect(definition?.selectors?.[0]?.[0]).toBe('app-root');
    });
  });

  describe('composition', () => {
    it('mounts exactly one element, as its stylesheet is written to expect', () => {
      expect(host().children.length).toBe(1);
    });

    it('mounts the shell', () => {
      expect(host().firstElementChild?.tagName.toLowerCase()).toBe('app-shell');
    });

    it('mounts exactly one shell, anywhere in its subtree', () => {
      expect(host().querySelectorAll('app-shell').length).toBe(1);
    });

    it('renders the shell with the grid class, so the layout engages end to end', () => {
      const shell = host().querySelector('app-shell');

      expect(shell?.classList.contains('shell')).toBeTrue();
    });
  });

  describe('regions reachable through the shell', () => {
    it('renders the skip link, the banner, the main region and the footer', () => {
      expect(host().querySelector('a.shell__skip-link')).not.toBeNull();
      expect(host().querySelector('app-header header')).not.toBeNull();
      expect(host().querySelector('main#main-content')).not.toBeNull();
      expect(host().querySelector('app-footer footer')).not.toBeNull();
    });

    it('emits each singular landmark exactly once across the whole application', () => {
      // Presence is not the interesting property; uniqueness is. A document with two banners or two
      // contentinfo landmarks is a genuine accessibility defect, and the root is the only scope at which
      // the duplication would be observable.
      for (const landmark of ['header', 'main', 'footer']) {
        expect(host().querySelectorAll(landmark).length)
          .withContext(`the application must render exactly one <${landmark}>`)
          .toBe(1);
      }
    });

    it('renders exactly one router outlet, and renders it inside the main landmark', () => {
      const main = host().querySelector('main');

      expect(main).not.toBeNull();
      expect(host().querySelectorAll('router-outlet').length).toBe(1);

      // An outlet at the root level would compile, render, and put every routed view outside the main
      // landmark and outside the shell's page gutter — a defect that is invisible until someone reads the
      // accessibility tree.
      expect(main?.querySelectorAll('router-outlet').length).toBe(1);
    });

    it('exposes a skip link whose fragment resolves from the real root', () => {
      const link = host().querySelector<HTMLAnchorElement>('a.shell__skip-link');

      expect(link).not.toBeNull();

      const address = link?.getAttribute('href') ?? '';

      expect(address.startsWith('/')).toBeTrue();

      const [, fragmentName] = address.split('#');
      const fragment = `#${fragmentName ?? ''}`;

      expect(fragmentName).toBeTruthy();

      const resolved = host().querySelector(fragment);

      expect(resolved).not.toBeNull();
      expect(resolved).toBe(host().querySelector('main'));
    });

    it('renders no chrome of its own', () => {
      expect(host().querySelectorAll(':scope > header').length).toBe(0);
      expect(host().querySelectorAll(':scope > nav').length).toBe(0);
      expect(host().querySelectorAll(':scope > main').length).toBe(0);
      expect(host().querySelectorAll(':scope > footer').length).toBe(0);
    });

    it('renders the banner with the build-time application name, no binding required', () => {
      const brand = host().querySelector('a.app-header__brand');

      expect(brand?.textContent?.trim()).toBe(environment.applicationName);
    });
  });

  describe('session — reading it', () => {
    it('renders no session cluster while no account is signed in', () => {
      // Not an assertion about unfinished work: the component supplies `undefined`, the shell forwards it
      // unchanged, and the banner treats an absent name as no account signed in. This is the state a
      // first-time visitor is in.
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('renders the signed-in display name and a sign-out control once a session is held', () => {
      holdSession();

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('Operator A');
      expect(host().querySelector('button.app-header__logout')).not.toBeNull();
    });

    it('falls back to the account key when the display name is blank', () => {
      // The published contract rather than a nicety: `auth.model.ts` documents `displayName` as `NOT NULL`
      // defaulting to the empty string and states that the shell renders `username` in its place.
      holdSession({ displayName: '' });

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('operator.a');
      expect(host().querySelector('button.app-header__logout')).not.toBeNull();
    });

    it('treats a whitespace-only display name as absent, not as a name', () => {
      // The legacy absent-string sentinel is the empty string, and a value that trims to nothing is the
      // same condition wearing a disguise. Deciding this by trimming rather than by truthiness is what
      // makes the two cases behave alike.
      holdSession({ displayName: '   ' });

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('operator.a');
    });

    it('reports no name at all when neither the display name nor the account key is usable', () => {
      holdSession({ displayName: '', username: '' });

      expect(host().querySelector('span.app-header__user')).toBeNull();
    });

    it('offers no account affordance while no account is signed in', () => {
      expect(host().querySelector('a.app-header__account-link')).toBeNull();
    });

    /**
     * @param caption The exact rendered caption.
     * @returns The anchor, or null.
     */
    function accountLink(caption: string): HTMLAnchorElement | null {
      const links = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('a.app-header__account-link'),
      );

      return links.find((link) => link.textContent?.trim() === caption) ?? null;
    }

    it("composes the signed-in account's own profile and password addresses", () => {
      // ⚠ THE FIXTURE'S ACCOUNT KEY IS ZERO, WHICH MAKES THIS A SENTINEL PROOF AS WELL AS A COMPOSITION
      // ONE. A truthiness test or a `> 0` guard anywhere on the path from the session to a rendered `href`
      // would drop both affordances entirely, and the addresses below are what prove none exists.
      holdSession();

      expect(accountLink('Manage Profile')?.getAttribute('href')).toBe('/users/0/profile');
      expect(accountLink('Manage Password')?.getAttribute('href')).toBe('/users/0/password');
    });

    it('offers both account affordances in the legacy command order, and no third', () => {
      holdSession();

      const captions = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('a.app-header__account-link'),
      ).map((link) => link.textContent?.trim());

      expect(captions).toEqual(['Manage Profile', 'Manage Password']);
    });

    it('captions each account affordance with the measured legacy command wording', () => {
      holdSession();

      expect(accountLink('Manage Profile')?.textContent?.trim()).toBe('Manage Profile');
      expect(accountLink('Manage Password')?.textContent?.trim()).toBe('Manage Password');
    });

    it('offers no account affordance at all while no session is held', () => {
      // The complement of the cases above. Each link is gated on a session as well as on an address, so an
      // address left over from a previous identity cannot render a link naming an account nobody is signed
      // in as.
      expect(host().querySelectorAll('a.app-header__account-link').length).toBe(0);
    });

    it('renders the navigation rail exactly once, as the shell\'s own region', () => {
      const rail = host().querySelector('app-sidebar');

      expect(rail).not.toBeNull();
      expect(host().querySelectorAll('app-sidebar').length).toBe(1);
      expect(rail?.classList.contains('shell__sidebar')).toBeTrue();
      expect(host().querySelectorAll('div.shell__sidebar').length).toBe(0);
    });

    it('announces the rail through its own labelled landmark', () => {
      // The shell writes no `role` and no `aria-label` on the region, leaving the landmark to the rail.
      // That contract only holds if the rail brings one.
      holdSession();

      const landmark = host().querySelector('app-sidebar nav');

      expect(landmark).not.toBeNull();
      expect(landmark?.getAttribute('aria-label')?.trim().length).toBeGreaterThan(0);
      expect(host().querySelectorAll('nav').length).toBe(1);
    });
  });

  describe('navigation rail — whether it is offered at all', () => {
    /**
     * The rail's RENDERED NAVIGATION, or null when the rail is offering nothing. ⚠ THE LANDMARK, NOT THE
     * CUSTOM ELEMENT, AND THE DISTINCTION IS THE WHOLE OF THIS SUITE. The shell composes the rail
     * unconditionally — that is what the case above records, and it is what makes shipping
     * without navigation a compile error — so `app-sidebar` is in the document at every address including
     * the sign-in screen.
     */
    function rail(): Element | null {
      return host().querySelector('app-sidebar nav');
    }

    /**
     * The rail's host element, which the shell composes UNCONDITIONALLY. Separate from {@link rail} on
     * purpose: the two say different things, and the cases below need both.
     */
    function railElement(): Element | null {
      return host().querySelector('app-sidebar');
    }

    /** Every navigation address currently rendered anywhere in the application. */
    function railAddresses(): readonly string[] {
      return Array.from(host().querySelectorAll<HTMLAnchorElement>('a.app-sidebar__link')).map(
        (anchor: HTMLAnchorElement): string => anchor.getAttribute('href') ?? '',
      );
    }

    it('projects no rail at all while nobody is signed in', () => {
      expect(rail()).toBeNull();
      expect(railAddresses()).toEqual([]);
    });

    it('leaves the shell region genuinely empty, so its collapse rule engages', () => {
      // The layout collapses `.shell__sidebar:empty`. That branch only engages if the region renders
      // NOTHING, so a rail that emitted an empty frame — a landmark, a heading or even a collapse toggle —
      // would defeat it and reserve a blank column beside the sign-in form.
      const region = host().querySelector('app-sidebar.shell__sidebar');

      expect(region).not.toBeNull();
      expect(region?.children.length).toBe(0);
      expect((region?.textContent ?? '').trim()).toBe('');
      expect(region?.matches(':empty'))
        .withContext('the collapse rule is what takes the column away, so it must actually match')
        .toBeTrue();

      // And no wrapper element carries the class, which would sever the grid relationship.
      expect(host().querySelectorAll('div.shell__sidebar').length).toBe(0);
    });

    it('projects the rail as soon as a session with a resolved identity is held', () => {
      expect(rail()).toBeNull();

      holdSession();

      expect(rail()).not.toBeNull();
      expect(railAddresses().length).toBeGreaterThan(0);
    });

    it('withdraws the rail again when the session ends', () => {
      // ⚠ THE RAIL MUST NOT OUTLIVE THE SESSION IT WAS RENDERED FOR. A sign-out leaves this component
      // mounted — the application is not reloaded — so a rail derived from a value captured once would keep
      // the previous operator's navigation on screen while they were being returned to the sign-in form.
      holdSession();
      expect(rail()).not.toBeNull();

      tokens.clear();
      fixture.detectChanges();

      expect(rail()).toBeNull();
      expect(railAddresses()).toEqual([]);
    });

    it('states the caller\u2019s authority from the server\u2019s own facts, not from a role name', () => {
      // ⚠ BOTH INPUTS ARE REQUIRED, so omitting either is a compile error rather than an unfiltered rail —
      // but the VALUES are this component's responsibility, and getting them from the wrong place is not a
      // compile error.
      holdSession();

      // The two authority facts are published by the SHELL, which owns the session boundary, and their
      // source is asserted in `layout/shell/shell.component.spec.ts`.
      const addresses = railAddresses();

      expect(addresses).not.toContain('/portals');
      expect(addresses).toContain('/users');
      expect(addresses).toContain('/roles');
      expect(addresses).toContain('/modules');
    });

    it('offers the tenant collection once the caller is a host account', () => {
      holdSession({ isSuperUser: true });

      expect(railAddresses()).toContain('/portals');
    });

    it('offers no entry point to a signed-in caller holding no authority at all', () => {
      // ⚠ THE ROLE NAME IN THE FIXTURE IS THE POINT OF THE THIRD ASSERTION. This caller holds a role named
      // 'Subscribers' and the server reports it as administering nothing; a rail that inferred authority
      // from a role name rather than from the server's own flags could not tell this caller from an
      // administrator whose tenant renamed its administrator role.
      holdSession({
        isSuperUser: false,
        isPortalAdministrator: false,
        mustChangePassword: false,
        mustUpdateProfile: false,
        roles: ['Subscribers'],
        permissions: [],
      });

      expect(railElement()).not.toBeNull();
      expect(rail())
        .withContext('no landmark stands over a rail with nothing in it')
        .toBeNull();
      expect(railAddresses()).toEqual([]);
    });

    it('offers module placement to a page editor who administers nothing', () => {
      holdSession({
        isSuperUser: false,
        isPortalAdministrator: false,
        mustChangePassword: false,
        mustUpdateProfile: false,
        roles: ['Subscribers'],
        permissions: ['EDIT'],
      });

      const addresses = railAddresses();

      expect(rail())
        .withContext('one entry survives, so the landmark stands')
        .not.toBeNull();
      expect(addresses).toEqual(['/modules/new']);

      expect(addresses).not.toContain('/modules');
      expect(addresses).not.toContain('/portals');
      expect(addresses).not.toContain('/users');
      expect(addresses).not.toContain('/roles');
    });
  });

  describe('session — ending it', () => {
    it('revokes the session server-side when the operator asks to sign out', () => {
      holdSession();

      clickSignOut();

      const revocation = httpMock.expectOne(AUTH_ENDPOINTS.logout());

      expect(revocation.request.method).toBe('POST');
      expect(revocation.request.body).toEqual({ refreshToken: 'operator-refresh-token' });

      revocation.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('addresses the API relatively, on the origin that served the application', () => {
      holdSession();
      clickSignOut();

      const revocation = httpMock.expectOne(AUTH_ENDPOINTS.logout());
      const address: string = revocation.request.url;

      // Rooted at the origin: a path, not a location.
      expect(address.startsWith('/')).toBeTrue();

      // Not protocol-relative. A leading double slash is a path to the eye and a foreign
      // origin to a browser, which is the failure mode a naive prefix test misses.
      expect(address.startsWith('//')).toBeFalse();

      // Carries no scheme, so it cannot name a host at all.
      expect(/^[a-z][a-z\d+\-.]*:/i.test(address)).toBeFalse();

      // And it is rooted at the base the environment configures rather than at some other
      // path, which is what ties the request to the prefix the proxy forwards.
      expect(address.startsWith(environment.apiBaseUrl)).toBeTrue();

      revocation.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('ends the session through the lifecycle service rather than through the session store', () => {
      // The distinction is the whole reason the lifecycle service exists.
      const coordinated = spyOn(session, 'signOut').and.callThrough();
      const storeDirect = spyOn(authStore, 'logout').and.callThrough();

      holdSession();
      clickSignOut();

      expect(coordinated).toHaveBeenCalledTimes(1);

      // Reached only THROUGH the lifecycle service: one call, made by it rather than by the
      // chrome. Asserting the count alone would pass either way, so the caller matters.
      expect(storeDirect).toHaveBeenCalledTimes(1);
      expect(coordinated).toHaveBeenCalledBefore(storeDirect);

      httpMock.expectOne(AUTH_ENDPOINTS.logout()).flush(null, { status: 204, statusText: 'No Content' });
    });

    it('leaves the application unauthenticated once the revocation settles', () => {
      holdSession();
      expect(authStore.isAuthenticated()).toBeTrue();

      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout()).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('sends the operator to the sign-in screen, carrying no return address', () => {
      holdSession();
      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout()).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledTimes(1);

      expect(navigate).toHaveBeenCalledWith(['/login']);
    });

    it('still reaches the sign-in screen when the revocation request fails', () => {
      holdSession();
      clickSignOut();

      httpMock
        .expectOne(AUTH_ENDPOINTS.logout())
        .flush({ detail: 'unreachable' }, { status: 503, statusText: 'Service Unavailable' });
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledWith(['/login']);
      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('still reaches the sign-in screen when ending the session throws outright', () => {
      spyOn(session, 'signOut').and.returnValue(throwError(() => new Error('teardown failed')));

      holdSession();
      clickSignOut();

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);

      // Nothing was issued, because the coordinator never got as far as the transport. The
      // `verify()` in the teardown is what proves it.
      expect(authStore.isSigningOut()).toBeFalse();
    });

    it('swallows a navigation the router refuses, because the session has ended either way', async () => {
      navigate.and.rejectWith(new Error('navigation refused'));

      holdSession();
      clickSignOut();

      httpMock.expectOne(AUTH_ENDPOINTS.logout()).flush(null, { status: 204, statusText: 'No Content' });

      // Lets the rejected navigation settle, so the discard actually happens inside the specification
      // rather than after it.
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);
      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('marks the sign-out in flight while the revocation is outstanding', () => {
      holdSession();
      clickSignOut();

      // ⚠ ASSERTED ON THE STORE'S PHASE, AND IT HAS TO BE. The store discards the identity SYNCHRONOUSLY at
      // subscribe time — local sign-out is the part the operator asked for and it does not wait for the
      // server — so the banner's session cluster, and with it the control carrying the disabled state, is
      // already gone by the time this line runs.
      expect(authStore.isSigningOut()).toBeTrue();

      httpMock.expectOne(AUTH_ENDPOINTS.logout()).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(authStore.isSigningOut()).toBeFalse();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('issues one revocation only, however many times the gesture arrives in a single tick', () => {
      // The banner guards the gesture twice already, but both of its guards read the flag as it stood at
      // the last change detection.
      holdSession();

      const control = host().querySelector<HTMLButtonElement>('button.app-header__logout');

      control?.click();
      control?.click();
      control?.click();
      fixture.detectChanges();

      httpMock.expectOne(AUTH_ENDPOINTS.logout()).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('offers no sign-out gesture at all when no session is held', () => {
      expect(host().querySelector('button.app-header__logout')).toBeNull();
      expect(navigate).not.toHaveBeenCalled();
    });
  });
  // ---------------------------------------------------------------------------------------------------
  // THE DOCUMENT'S OWN TYPOGRAPHY
  //
  // These read the GLOBAL stylesheet through the CSSOM rather than a rendered box, because that is where
  // the rules live and because a computed size cannot distinguish "declared at this step" from "inherited
  // and happens to match".
  // ---------------------------------------------------------------------------------------------------

  describe('the document typography the global stylesheet declares', () => {
    /** The declared value of one property on the first rule whose selector text matches exactly. */
    function declaredValueFor(selectorText: string, property: string): string | null {
      for (const sheet of Array.from(document.styleSheets)) {
        let rules: CSSRuleList;

        try {
          rules = sheet.cssRules;
        } catch {
          continue;
        }

        for (const rule of Array.from(rules)) {
          if (!(rule instanceof CSSStyleRule)) {
            continue;
          }

          if (rule.selectorText.replace(/\s+/g, ' ').trim() !== selectorText) {
            continue;
          }

          const declared = rule.style.getPropertyValue(property).trim();

          if (declared.length > 0) {
            return declared;
          }
        }
      }

      return null;
    }

    /** A token's value in pixels, resolved from the document root. */
    function tokenAsPixels(token: string): number {
      const probe = document.createElement('div');

      probe.style.fontSize = `var(${token})`;
      document.body.appendChild(probe);

      const measured = Number.parseFloat(getComputedStyle(probe).fontSize);

      probe.remove();

      return measured;
    }

    it('gives the ROOT element the base font family, so nothing computes a browser default', () => {
      // ⚠ MEASURED DEFECT: `html` declared no `font-family` at all while `body` did, so the root element
      // computed the browser's own serif default. Anything positioned outside `body`'s subtree - and
      // anything inheriting from the root before the body rule applies - was drawn in the wrong face.
      expect(declaredValueFor('html', 'font-family')).toBe('var(--font-family-base)');
      expect(getComputedStyle(document.documentElement).fontFamily)
        .withContext('the root resolves to the token, not to a browser default')
        .toContain('Tahoma');
    });

    it('spends ONE size token per heading level, strictly decreasing from h1 to h6', () => {
      // ⚠ MEASURED DEFECT: `h1, h2` shared one token and `h3, h4` shared another, so the not-found title
      // and the empty-state title rendered identically at 24px and a reader could not tell one level from
      // the next. Six levels, six steps.
      const declared = ['h1', 'h2', 'h3', 'h4', 'h5', 'h6'].map((level) => {
        const value = declaredValueFor(level, 'font-size');

        expect(value).withContext(`${level} declares its own size`).not.toBeNull();

        return String(value);
      });

      expect(new Set(declared).size).withContext('six levels, six distinct tokens').toBe(6);

      const sizes = declared.map((value) => {
        const token = /var\((--[a-z0-9-]+)\)/.exec(value)?.[1];

        expect(token).withContext(`${value} resolves through a token`).toBeDefined();

        return tokenAsPixels(String(token));
      });

      for (let index = 1; index < sizes.length; index += 1) {
        expect(sizes[index])
          .withContext(`h${index + 1} is smaller than h${index}`)
          .toBeLessThan(Number(sizes[index - 1]));
      }
    });
  });
});
