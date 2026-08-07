import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AppComponent } from './app.component';
import { AUTH_ENDPOINTS } from './core/config/api-endpoints';
import { TokenStorageService } from './core/services/token-storage.service';
import { AuthStore } from './core/state/auth.store';
import { SessionLifecycleService } from './core/state/session-lifecycle.service';
import { environment } from '../environments/environment';

import type { AuthSession } from './core/models/auth.model';

/**
 * A held session, in the shape the token custodian stores.
 *
 * The token values are obvious placeholders rather than anything resembling a real
 * credential: nothing here parses them, and a value that looked like a token would invite
 * somebody to try it.
 *
 * `userId: 0` and `portalId: -1` are not arbitrary either. Both are the identity seeds the
 * baseline schema declares — `Users` at `IDENTITY(0, 1)` and `Portals` at `IDENTITY(-1, 1)`
 * — and both collide with the legacy absent-integer sentinel, so using them here keeps the
 * fixture honest about the values this application actually has to carry.
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

  /**
   * Returns the root component's host element.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        // The shell renders a router outlet and the banner inside it renders a router
        // link, so a router must be present. An empty route table is sufficient: the
        // sign-out navigation is asserted through a spy on the router rather than by
        // resolving a route, so that this specification asserts what this component asks
        // for and the route table is asserted where it is declared.
        provideRouter([]),
        // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()`
        // REPLACES the backend the real client installed, so reversing the two would leave
        // the live backend in place and every expectation below would find nothing.
        //
        // Present because this component now injects the session store and the session
        // coordinator, and the whole graph beneath them — the authentication service, the
        // four domain stores — reaches the HTTP client. Nothing is stubbed: the real graph
        // is what makes "asking to sign out actually ends the session" assertable.
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    session = TestBed.inject(SessionLifecycleService);
    router = TestBed.inject(Router);

    // Spied before the component is created so that no navigation can escape into the
    // empty route table. `resolveTo` rather than `stub`, because the component chains a
    // `catch` onto the returned promise and an undefined return would throw there.
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
   * Establishes a held session, exactly as the sign-in flow would, and renders it.
   *
   * Written through the token custodian rather than by posting credentials, because every
   * assertion here is about the CHROME reading a session rather than about acquiring one,
   * and staging one through the sign-in endpoint would add a request each case would then
   * have to account for.
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
      // Presence is not the interesting property; uniqueness is. A document with two
      // banners or two contentinfo landmarks is a genuine accessibility defect, and
      // the root is the only scope at which the duplication would be observable.
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

      // An outlet at the root level would compile, render, and put every routed view
      // outside the main landmark and outside the shell's page gutter — a defect that
      // is invisible until someone reads the accessibility tree.
      expect(main?.querySelectorAll('router-outlet').length).toBe(1);
    });

    it('exposes a skip link whose fragment resolves from the real root', () => {
      const link = host().querySelector<HTMLAnchorElement>('a.shell__skip-link');

      expect(link).not.toBeNull();

      const fragment = link?.getAttribute('href') ?? '';

      expect(fragment.startsWith('#')).toBeTrue();

      // Resolved against the FULL document tree rather than against the shell's
      // subtree. The fragment a browser follows is resolved document-wide, so this is
      // the scope at which a duplicate id or a stale fragment would actually bite.
      const resolved = host().querySelector(fragment);

      expect(resolved).not.toBeNull();
      expect(resolved).toBe(host().querySelector('main'));
    });

    it('renders no chrome of its own', () => {
      // The root owns no data, no navigation and no chrome; the shell renders all of
      // it. Any of these appearing at this level would be a second definition of
      // something that already exists exactly once.
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
      // Not an assertion about unfinished work: the component supplies `undefined`, the
      // shell forwards it unchanged, and the banner treats an absent name as no account
      // signed in. This is the state a first-time visitor is in.
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('renders the signed-in display name and a sign-out control once a session is held', () => {
      holdSession();

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('Operator A');
      expect(host().querySelector('button.app-header__logout')).not.toBeNull();
    });

    it('falls back to the account key when the display name is blank', () => {
      // The published contract rather than a nicety: `auth.model.ts` documents
      // `displayName` as `NOT NULL` defaulting to the empty string and states that the
      // shell renders `username` in its place. It also prevents a real defect — the
      // sign-out control lives INSIDE the cluster the banner suppresses for a blank name,
      // so passing `''` through would leave this operator unable to sign out.
      holdSession({ displayName: '' });

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('operator.a');
      expect(host().querySelector('button.app-header__logout')).not.toBeNull();
    });

    it('treats a whitespace-only display name as absent, not as a name', () => {
      // The legacy absent-string sentinel is the empty string, and a value that trims to
      // nothing is the same condition wearing a disguise. Deciding this by trimming rather
      // than by truthiness is what makes the two cases behave alike.
      holdSession({ displayName: '   ' });

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('operator.a');
    });

    it('reports no name at all when neither the display name nor the account key is usable', () => {
      holdSession({ displayName: '', username: '' });

      expect(component['userName']()).toBeUndefined();
      expect(host().querySelector('span.app-header__user')).toBeNull();
    });

    it('projects the navigation rail into the secondary region, so it is not collapsed', () => {
      // The shell publishes this region as a projection slot and assigns the mounting
      // decision to whichever component mounts `<app-shell>` — this one. The region is
      // therefore only populated if the root actually projects the rail, and the
      // stylesheet's `.shell__sidebar:empty` collapse rule means an unprojected rail
      // fails silently: the application would simply render with no navigation rather
      // than raise anything. Asserting the rail is present, and present INSIDE the
      // region, is what closes that gap.
      const region = host().querySelector('div.shell__sidebar');
      const rail = host().querySelector('app-sidebar');

      expect(region).not.toBeNull();
      expect(rail).not.toBeNull();
      expect(region?.children.length).toBe(1);
      expect(region?.contains(rail as Node)).toBeTrue();
    });

    it('announces the rail through its own labelled landmark, which the region does not supply', () => {
      // The shell's region is a bare `div` that deliberately writes no `role` and no
      // `aria-label`, leaving the landmark to whatever is projected. That contract only
      // holds if the projected rail brings one.
      const landmark = host().querySelector('app-sidebar nav');

      expect(landmark).not.toBeNull();
      expect(landmark?.getAttribute('aria-label')?.trim().length).toBeGreaterThan(0);
    });
  });

  describe('session — ending it', () => {
    it('revokes the session server-side when the operator asks to sign out', () => {
      holdSession();

      clickSignOut();

      const revocation = httpMock.expectOne(AUTH_ENDPOINTS.logout);

      expect(revocation.request.method).toBe('POST');
      expect(revocation.request.body).toEqual({ refreshToken: 'operator-refresh-token' });

      revocation.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('ends the session through the coordinator rather than through the session store', () => {
      // The distinction is the whole reason the coordinator exists. The store's own
      // sign-out discards the credentials and the identity and knows nothing about the
      // portals, accounts, roles or exported module documents the domain stores hold, so a
      // root that called the store directly would leave every one of those slices legible
      // to whoever signs in next.
      const coordinated = spyOn(session, 'signOut').and.callThrough();
      const storeDirect = spyOn(authStore, 'logout').and.callThrough();

      holdSession();
      clickSignOut();

      expect(coordinated).toHaveBeenCalledTimes(1);

      // Reached only THROUGH the coordinator: one call, made by it rather than by this
      // component. Asserting the count alone would pass either way, so the caller matters.
      expect(storeDirect).toHaveBeenCalledTimes(1);
      expect(coordinated).toHaveBeenCalledBefore(storeDirect);

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
    });

    it('leaves the application unauthenticated once the revocation settles', () => {
      holdSession();
      expect(authStore.isAuthenticated()).toBeTrue();

      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('sends the operator to the sign-in screen, carrying no return address', () => {
      holdSession();
      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledTimes(1);

      // The route gates attach a return address when they INTERRUPT a navigation. Signing
      // out is not an interruption — the operator chose to leave — and restoring an address
      // that named a record the next operator has no right to know exists would defeat the
      // discard that just happened.
      expect(navigate).toHaveBeenCalledWith(['/login']);
    });

    it('still reaches the sign-in screen when the revocation request fails', () => {
      // A refused or unreachable endpoint arrives as a COMPLETION rather than as an error,
      // because `core/state/auth.store.ts` absorbs the refusal DELIBERATELY — local sign-out
      // has already happened unconditionally, and the store records the failed withdrawal in
      // its own report rather than discarding it. Either way the session is already gone, and
      // leaving the operator on an administration screen with no credentials would strand
      // them on a view whose every request is about to be refused.
      //
      // MIGRATION: the absorption used to sit one layer lower, on `auth.service.ts`, which
      //   discarded the failure as well and so reported a clean sign-out while the renewal
      //   credential was still live. This component's two exits are unchanged by the move.
      holdSession();
      clickSignOut();

      httpMock
        .expectOne(AUTH_ENDPOINTS.logout)
        .flush({ detail: 'unreachable' }, { status: 503, statusText: 'Service Unavailable' });
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledWith(['/login']);
      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('marks the sign-out in flight while the revocation is outstanding', () => {
      holdSession();
      clickSignOut();

      expect(component['signingOut']()).toBeTrue();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(component['signingOut']()).toBeFalse();
    });

    it('issues one revocation only, however many times the gesture arrives in a single tick', () => {
      // The banner guards the gesture twice already, but both of its guards read the flag
      // as it stood at the last change detection. The store sets the phase SYNCHRONOUSLY
      // at subscribe time, so the guard on this component is the one that closes the
      // same-tick window — which is exactly what calling the output handler directly,
      // without an intervening render, reproduces.
      holdSession();

      component['onSignOut']();
      component['onSignOut']();
      component['onSignOut']();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('issues nothing at all when no session is held', () => {
      // No cluster is rendered, so there is no control to activate — but the handler is
      // reachable programmatically, and a revocation for a session that does not exist
      // would be a request with no credential to revoke.
      component['onSignOut']();

      // The custodian holds no refresh token, so the authentication service short-circuits
      // to a synchronous completion without issuing anything. `httpMock.verify()` in the
      // teardown is what proves the absence.
      expect(navigate).toHaveBeenCalledWith(['/login']);
    });
  });
});
