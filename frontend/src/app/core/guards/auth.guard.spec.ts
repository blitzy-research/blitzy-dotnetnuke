/**
 * Specification for {@link authGuard}, the navigation gate that admits a signed-in caller and redirects
 * everyone else to the sign-in screen. NET-NEW COVERAGE, NOT A PORTED TEST. The legacy tree contains ZERO
 * automated tests of any kind, so nothing here is a translation of a prior assertion.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import type { Observable } from 'rxjs';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';

import type { AuthSession } from '../models/auth.model';
import { TokenStorageService } from '../services/token-storage.service';
import { AuthStore } from '../state/auth.store';
import { authGuard } from './auth.guard';

/** The sign-in route, spelled again rather than imported. */
const SIGN_IN_PATH = '/login';

/** The query-parameter key the attempted address travels under. */
const RETURN_URL_KEY = 'returnUrl';

/** The two signals the gate reads, held independently so every combination is reachable. */
interface SessionDouble {
  /** Whether the store reports a held session. */
  readonly isAuthenticated: WritableSignal<boolean>;
  /** The bearer token the held session carries, or null when it carries none. */
  readonly accessToken: WritableSignal<string | null>;
}

/** The current-user endpoint, spelled RELATIVELY and spelled out rather than imported. */
const CURRENT_USER_URL = '/api/v1/auth/me';

/** The renewal endpoint. */
const RENEWAL_URL = '/api/v1/auth/refresh';

/**
 * The liveness endpoint, which is NOT under the versioned API. Mapped at the host root, anonymous and
 * unthrottled, because the container's own health probe and the compose dependency condition both read it
 * before any credential exists. A gate that probed it would be reaching outside the versioned surface for
 * a fact that says nothing about the caller.
 */
const HEALTH_URL = '/health';

/**
 * A deliberately implausible bearer token, used where the assertion is that the value NEVER APPEARS
 * somewhere. Shaped so it cannot be mistaken for, or matched by a scanner looking for, a real credential
 * of any provider: it is not a JSON web token, carries no base64 payload and names itself as a fixture.
 */
const NEVER_LOG_TOKEN = 'fixture-only.never-log-this-value.not-a-credential';

/**
 * The session a successful renewal hands back. TYPED AS THE REAL CONTRACT RATHER THAN AS A CONVENIENT
 * PARTIAL, and the reason is a class of defect rather than tidiness.
 */
const RENEWED_SESSION: AuthSession = {
  accessToken: 'rotated.fixture-only.not-a-credential',
  expiresAtUtc: '2099-01-01T00:00:00.0000000Z',
  refreshToken: 'rotated-renewal.fixture-only.not-a-credential',
  mustChangePassword: false,
  mustUpdateProfile: false,
  passwordExpiring: false,
  user: {
    userId: 1,
    portalId: -1,
    portalName: 'Baseline Portal',
    username: 'admin',
    displayName: 'Administrator',
    email: 'admin@example.invalid',
    isSuperUser: false,
    isPortalAdministrator: true,
    roles: ['Administrators'],
    permissions: ['EDIT'],
  },
};

/** Members of {@link AuthStore} this gate must never reach, by their REAL names. */
const STORE_MEMBERS_OFF_LIMITS: readonly string[] = [
  'roles',
  'permissions',
  'isSuperUser',
  'holdsPortalAdministration',
  'verificationRequired',
  'verificationPrompt',
  'failureStatus',
  'login',
  'logout',
  'loadCurrentUser',
  'endSession',
];

/**
 * Members of {@link TokenStorageService} this gate must never reach, by their REAL names. `store` and
 * `clear` are the only two ways session custody changes, so spying on both is how "the gate reads the
 * session and never writes it" is proved rather than asserted.
 */
const CUSTODIAN_MEMBERS_OFF_LIMITS: readonly string[] = [
  'store',
  'clear',
  'refreshToken',
  'currentUser',
];

/**
 * Builds one spy per member name, so that reaching any of them is a recorded event.
 *
 * @param names The member names to install spies for.
 * @returns A record keyed by member name.
 */
function offLimitsSpies(names: readonly string[]): Record<string, jasmine.Spy> {
  const spies: Record<string, jasmine.Spy> = {};

  for (const name of names) {
    spies[name] = jasmine.createSpy(name);
  }

  return spies;
}

describe('authGuard', () => {
  let session: SessionDouble;
  let router: Router;
  let httpMock: HttpTestingController;
  let navigate: jasmine.Spy;
  let navigateByUrl: jasmine.Spy;
  let expiryProbe: jasmine.Spy;
  let renewSession: jasmine.Spy;
  let storeOffLimits: Record<string, jasmine.Spy>;
  let custodianOffLimits: Record<string, jasmine.Spy>;

  beforeEach(() => {
    session = {
      isAuthenticated: signal(false),
      accessToken: signal<string | null>(null),
    };

    storeOffLimits = offLimitsSpies(STORE_MEMBERS_OFF_LIMITS);
    custodianOffLimits = offLimitsSpies(CUSTODIAN_MEMBERS_OFF_LIMITS);

    /**
     * The clock-reading member of the custodian, stubbed so that BOTH the verdict it returns and the
     * number of times it is consulted are under this spec's control.
     */
    expiryProbe = jasmine.createSpy('isAccessTokenExpired').and.returnValue(false);

    /**
     * The renewal the gate delegates to when the held token has lapsed. A DOUBLE ON THE STORE RATHER THAN
     * A MOCKED RESPONSE, deliberately.
     */
    renewSession = jasmine
      .createSpy('refreshSession')
      .and.returnValue(throwError(() => new Error('no renewal was armed by this test')));

    TestBed.configureTestingModule({
      providers: [
        // ⚠ ORDER IS LOAD-BEARING. The real transport is registered first and the mock backend second,
        // because the mock REPLACES the backend the first provider installed.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthStore,
          useValue: {
            isAuthenticated: session.isAuthenticated,
            refreshSession: renewSession,
            ...storeOffLimits,
          },
        },
        {
          provide: TokenStorageService,
          useValue: {
            accessToken: session.accessToken,
            isAuthenticated: session.isAuthenticated,
            isAccessTokenExpired: expiryProbe,
            ...custodianOffLimits,
          },
        },
      ],
    });

    router = TestBed.inject(Router);
    httpMock = TestBed.inject(HttpTestingController);
    navigate = spyOn(router, 'navigate').and.resolveTo(true);
    navigateByUrl = spyOn(router, 'navigateByUrl').and.resolveTo(true);
  });

  afterEach(() => {
    // Fails the spec if ANY request was issued that a test did not expect. Since no test
    // below expects one, this is the executable form of "the gate performs no I/O".
    httpMock.verify();
  });

  /**
   * Puts the store into the "signed in, with a usable credential" state.
   *
   * @param token The bearer token the held session carries.
   */
  function signIn(token = 'header.payload.signature'): void {
    session.isAuthenticated.set(true);
    session.accessToken.set(token);
  }

  /**
   * Runs the gate for one attempted address.
   *
   * @param url The full attempted address, exactly as the router would serialise it.
   * @returns Whatever the gate decided.
   */
  function runGuard(url: string): boolean | UrlTree {
    const route = {} as ActivatedRouteSnapshot;
    const state = { url } as RouterStateSnapshot;

    const decision = TestBed.runInInjectionContext(() => authGuard(route, state));

    // The gate is documented as fully synchronous — no observable and no promise — so a narrowing that
    // would accept either would weaken the very claim being tested. This assertion is what makes the cast
    // below honest.
    expect(typeof decision === 'boolean' || decision instanceof UrlTree)
      .withContext('the gate decides synchronously; it returns neither observable nor promise')
      .toBeTrue();

    return decision as boolean | UrlTree;
  }

  /**
   * Runs the gate and asserts it produced a redirect, returning the serialised address.
   *
   * @param url The attempted address.
   * @returns The redirect target, serialised by the real router.
   */
  function redirectFor(url: string): string {
    const decision = runGuard(url);

    expect(decision)
      .withContext(`"${url}" must be refused with a redirect rather than admitted`)
      .toBeInstanceOf(UrlTree);

    return router.serializeUrl(decision as UrlTree);
  }

  /**
   * Runs the gate on the one path that DEFERS its answer — a lapsed token being renewed — and settles the
   * decision. Declared once at this level and shared by every test that needs it, rather than restated
   * per describe block.
   *
   * @param url The attempted address.
   * @returns The decision the gate eventually produced.
   */
  function settleDeferred(url: string): boolean | UrlTree {
    const decision = TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    );

    let settled: boolean | UrlTree | undefined;
    (decision as Observable<boolean | UrlTree>).subscribe((value) => {
      settled = value;
    });

    expect(settled)
      .withContext('the renewal double emits synchronously, so a decision must be present')
      .not.toBeUndefined();

    return settled as boolean | UrlTree;
  }

  // =========================================================================
  // ADMISSION — BOTH CONDITIONS, AND NEITHER ALONE
  // =========================================================================

  describe('admission', () => {
    it('admits a held session that carries a bearer token', () => {
      signIn();

      expect(runGuard('/portals')).toBeTrue();
    });

    it('admits without consulting the target route at all', () => {
      signIn();

      for (const url of ['/portals', '/users/0', '/roles/-1/users', '/modules/0/settings']) {
        expect(runGuard(url))
          .withContext(`"${url}" is admitted on session evidence alone`)
          .toBeTrue();
      }
    });

    it('refuses a held session whose token is ABSENT, which is a reachable state', () => {
      // Reachable because the custodian's write path validates nothing and the session member is
      // non-nullable, so a response that omitted it leaves a stored session with no credential.
      session.isAuthenticated.set(true);
      session.accessToken.set(null);

      expect(redirectFor('/portals')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);
    });

    it('refuses a held session whose token is the EMPTY STRING', () => {
      session.isAuthenticated.set(true);
      session.accessToken.set('');

      expect(redirectFor('/portals')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);
    });

    it('refuses when no session is held even though a token lingers', () => {
      // The reverse asymmetry: the store's verdict is authoritative for "is anybody signed
      // in", so a stale token cannot resurrect a session the store has already discarded.
      session.isAuthenticated.set(false);
      session.accessToken.set('header.payload.signature');

      expect(redirectFor('/portals')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);
    });

    it('admits a token that is merely short rather than empty', () => {
      // Presence is tested by length and never by plausibility. Whether a token is ACCEPTABLE is the
      // server's question, and pre-judging it here would refuse a caller the API would have served.
      signIn('x');

      expect(runGuard('/portals')).toBeTrue();
    });
  });

  // =========================================================================
  // THE ATTEMPTED ADDRESS SURVIVES THE ROUND TRIP
  // =========================================================================

  describe('returnUrl preservation', () => {
    it('carries a plain path through exactly once, encoded by the router alone', () => {
      expect(redirectFor('/portals')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);
    });

    it('preserves a query string on the attempted address', () => {
      const serialised = redirectFor('/users?page=2&query=smith');

      expect(serialised).toBe(
        `${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fusers%3Fpage%3D2%26query%3Dsmith`,
      );

      // Read back through the real parser, the value is byte-for-byte what was attempted.
      // This is the assertion that would fail on a double encode, a trim or a case fold.
      expect(router.parseUrl(serialised).queryParams[RETURN_URL_KEY]).toBe(
        '/users?page=2&query=smith',
      );
    });

    it('preserves a fragment on the attempted address', () => {
      const serialised = redirectFor('/portals/0/settings#appearance');

      expect(router.parseUrl(serialised).queryParams[RETURN_URL_KEY]).toBe(
        '/portals/0/settings#appearance',
      );
    });

    it('preserves an address that already contains percent-encoding', () => {
      // A round trip that decoded and re-encoded would normalise `%20` into `+` or a raw
      // space and change the address. The gate forwards the serialised string untouched.
      const serialised = redirectFor('/users?query=de%20Souza');

      expect(router.parseUrl(serialised).queryParams[RETURN_URL_KEY]).toBe(
        '/users?query=de%20Souza',
      );
    });

    it('preserves identifiers zero and minus one rather than normalising them', () => {
      expect(router.parseUrl(redirectFor('/portals/-1')).queryParams[RETURN_URL_KEY]).toBe(
        '/portals/-1',
      );
      expect(router.parseUrl(redirectFor('/roles/0/users')).queryParams[RETURN_URL_KEY]).toBe(
        '/roles/0/users',
      );
    });

    it('carries the returnUrl and nothing else', () => {
      const tree = router.parseUrl(redirectFor('/modules'));

      expect(Object.keys(tree.queryParams))
        .withContext('exactly one parameter travels, so nothing leaks into the sign-in address')
        .toEqual([RETURN_URL_KEY]);
      expect(tree.fragment)
        .withContext('the redirect itself carries no fragment')
        .toBeNull();
    });

    it('redirects the application root, which is not the sign-in route', () => {
      // The root reduces to no leading segment at all, and the sign-in route reduces to
      // `login`, so the two are not equal and the root is guarded like anything else.
      expect(redirectFor('/')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2F`);
    });
  });

  // =========================================================================
  // LOOP PREVENTION — EQUALITY, NOT PREFIX
  // =========================================================================

  describe('loop prevention', () => {
    it('admits the sign-in route itself so the application can still start', () => {
      expect(runGuard(SIGN_IN_PATH)).toBeTrue();
    });

    it('admits the sign-in route when it already carries a returnUrl', () => {
      // The query string is discarded before the comparison, so a second pass over an
      // address this gate itself produced cannot start a cycle.
      expect(runGuard(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`)).toBeTrue();
    });

    it('admits the sign-in route when it carries a fragment', () => {
      // A serialised router address may carry one, so the fragment is discarded too.
      expect(runGuard(`${SIGN_IN_PATH}#form`)).toBeTrue();
    });

    it('admits the sign-in route through a doubled separator', () => {
      // Empty segments are dropped, so a leading slash and any doubled separator are
      // tolerated and cannot defeat the comparison.
      expect(runGuard('//login')).toBeTrue();
      expect(runGuard('login')).toBeTrue();
    });

    it('REDIRECTS an address that merely begins with the sign-in path', () => {
      // ⚠ THE PREFIX TRAP. A `startsWith` test would treat `/loginx` as the sign-in route
      // and admit an unauthenticated caller to it. Segment EQUALITY is what refuses it.
      expect(redirectFor('/loginx')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Floginx`);
      expect(redirectFor('/login-help')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Flogin-help`);
    });

    it('REDIRECTS an address that only contains the sign-in path deeper down', () => {
      // Only the LEADING segment is compared, so a nested segment of the same name is a
      // different screen and is guarded.
      expect(redirectFor('/portals/login')).toBe(
        `${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals%2Flogin`,
      );
    });

    it('admits the sign-in route whether or not a session is held', () => {
      signIn();

      expect(runGuard(SIGN_IN_PATH))
        .withContext('a signed-in caller reaching the sign-in screen is admitted by condition one')
        .toBeTrue();
    });
  });

  // =========================================================================
  // NO SIDE EFFECTS — THE DECISION IS RETURNED, NOT PERFORMED
  // =========================================================================

  describe('purity', () => {
    it('builds the redirect rather than performing it', () => {
      // Returning the destination lets the router replace the in-flight navigation atomically: one
      // navigation begins and one ends, and no history entry is left pointing at a refused address.
      redirectFor('/portals');

      expect(navigate).not.toHaveBeenCalled();
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('presents a denial as a redirect and never escalates it to error severity', () => {
      const errorChannel = spyOn(console, 'error');
      const warnChannel = spyOn(console, 'warn');

      expect(() => redirectFor('/portals')).not.toThrow();

      expect(errorChannel)
        .withContext('a denial is a warning in this system, so it reaches no error channel')
        .not.toHaveBeenCalled();
      expect(warnChannel)
        .withContext('the redirect IS the affordance; no notification accompanies it')
        .not.toHaveBeenCalled();
    });

    it('performs no navigation on the admission path either', () => {
      signIn();

      expect(runGuard('/portals')).toBeTrue();
      expect(navigate).not.toHaveBeenCalled();
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('reads the clock only once it has a credential worth asking about', () => {
      // The order of the two admission conditions is itself the property: presence is established first, so
      // a caller with no session and a caller whose session carries no token are both decided WITHOUT a
      // clock read.
      runGuard('/portals');

      session.isAuthenticated.set(true);
      session.accessToken.set('');
      runGuard('/portals');

      session.accessToken.set(null);
      runGuard('/portals');

      expect(expiryProbe)
        .withContext('a caller with nothing to validate is refused before any clock is read')
        .not.toHaveBeenCalled();
    });

    it('renews nothing while the held credential is still live', () => {
      // The gate is anticipatory about EXPIRY and passive about everything else: a live token is admitted
      // on the spot. Renewing here as well would spend a rotation, and the operator's rate-limit budget on
      // the credential endpoints, on every single navigation.
      signIn();

      expect(runGuard('/portals')).toBeTrue();
      expect(expiryProbe)
        .withContext('validity is established for a present credential')
        .toHaveBeenCalled();
      expect(renewSession)
        .withContext('a live credential needs no renewal')
        .not.toHaveBeenCalled();
    });

    it('mutates no session state, so repeated decisions are identical', () => {
      session.isAuthenticated.set(true);
      session.accessToken.set('');

      const first = redirectFor('/portals');
      const second = redirectFor('/portals');

      expect(second).toBe(first);
      expect(session.isAuthenticated())
        .withContext('the gate reads the session; it never writes it')
        .toBeTrue();
      expect(session.accessToken()).toBe('');
    });

    it('decides again from scratch, caching nothing', () => {
      // Nothing is cached, so a change in the session takes effect on the next navigation
      // rather than persisting until something is invalidated.
      expect(redirectFor('/portals')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);

      signIn();

      expect(runGuard('/portals')).toBeTrue();

      session.isAuthenticated.set(false);

      expect(redirectFor('/portals')).toBe(`${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals`);
    });
  });

  // =========================================================================
  // EXPIRY — RESOLVED WITH ONE BOUNDED RENEWAL, NEVER PUNISHED, NEVER IGNORED
  // =========================================================================

  describe('a lapsed access token', () => {
    /**
     * Runs the gate on the expiry path, where the decision is deferred.
     *
     * @param url The attempted address.
     * @returns The decision the gate eventually produced.
     */
    function runDeferredGuard(url: string): boolean | UrlTree {
      const route = {} as ActivatedRouteSnapshot;
      const state = { url } as RouterStateSnapshot;

      const decision = TestBed.runInInjectionContext(() => authGuard(route, state));

      expect(typeof decision === 'boolean' || decision instanceof UrlTree)
        .withContext('the expiry path defers its answer rather than settling immediately')
        .toBeFalse();

      let settled: boolean | UrlTree | undefined;
      (decision as Observable<boolean | UrlTree>).subscribe((value) => {
        settled = value;
      });

      expect(settled)
        .withContext('the navigation waits for the renewal rather than proceeding hopefully')
        .not.toBeUndefined();

      return settled as boolean | UrlTree;
    }

    beforeEach(() => {
      signIn();
      expiryProbe.and.returnValue(true);
    });

    it('admits the navigation when exactly one renewal succeeds', () => {
      // ⚠ A LAPSED ACCESS TOKEN IS NOT AN ENDED SESSION. The renewal credential outlives it by design, so
      // refusing here would collapse the effective session to the access token's own lifetime — the precise
      // outcome the short-token/long-renewal pair exists to avoid.
      renewSession.and.returnValue(of(RENEWED_SESSION));

      expect(runDeferredGuard('/portals')).toBeTrue();
      expect(renewSession)
        .withContext('one renewal per navigation, never a retry')
        .toHaveBeenCalledTimes(1);
    });

    it('fails closed to the sign-in screen when the renewal is refused, keeping the address', () => {
      // The store discards the session as its own first act on a refusal, so there is nothing left to admit
      // by the time this redirect is built.
      renewSession.and.returnValue(throwError(() => new Error('refused')));

      const decision = runDeferredGuard('/portals/0/settings');

      expect(decision).toBeInstanceOf(UrlTree);
      expect(router.serializeUrl(decision as UrlTree)).toBe(
        `${SIGN_IN_PATH}?${RETURN_URL_KEY}=%2Fportals%2F0%2Fsettings`,
      );
      expect(navigate).not.toHaveBeenCalled();
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('admits the sign-in route itself without spending a renewal', () => {
      // Ordering, asserted: the sign-in test sits BEFORE the renewal.
      expect(runGuard(SIGN_IN_PATH)).toBeTrue();
      expect(renewSession).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // NO TRANSPORT OF ITS OWN — THE GATE ASKS THE STORE, NEVER THE SERVER
  // =========================================================================

  describe('no requests of its own', () => {
    it('issues NO request on any path, and never revalidates the session against the server', () => {
      // MIGRATION: a deliberate divergence whose COMPENSATING CONTROL is the reason this assertion earns
      // its place.
      signIn();
      expect(runGuard('/portals')).toBeTrue();

      session.isAuthenticated.set(false);
      redirectFor('/portals');

      session.isAuthenticated.set(true);
      session.accessToken.set('');
      redirectFor('/users/0');

      expect(runGuard(SIGN_IN_PATH)).toBeTrue();

      httpMock.expectNone(CURRENT_USER_URL);
      httpMock.expectNone(RENEWAL_URL);
    });

    it('delegates renewal to the store instead of posting the renewal itself', () => {
      // The single request this gate can CAUSE is a renewal, and it causes it indirectly. Single-flight
      // coalescing, credential rotation and discard-on-refusal all belong to the store and are specified
      // beside it; the gate calls one method and waits.
      signIn();
      expiryProbe.and.returnValue(true);
      renewSession.and.returnValue(of(RENEWED_SESSION));

      expect(settleDeferred('/portals')).toBeTrue();

      expect(renewSession).toHaveBeenCalledTimes(1);
      httpMock.expectNone(RENEWAL_URL);
      httpMock.expectNone(CURRENT_USER_URL);
    });

    it('never probes the liveness endpoint, which lives outside the versioned API', () => {
      // `/health` is mapped at the HOST ROOT rather than under `/api/v1`, and is anonymous and unthrottled
      // because the container's own probe reads it before any credential exists — the image's `HEALTHCHECK`
      // uses `wget --spider` precisely because the Alpine runtime ships `wget` and not `curl`, and the
      // compose dependency condition holds the proxy back until it answers.
      signIn();
      expect(runGuard('/portals')).toBeTrue();

      session.isAuthenticated.set(false);
      redirectFor('/portals');

      httpMock.expectNone(HEALTH_URL);
    });
  });

  // =========================================================================
  // CREDENTIAL HYGIENE — THE TOKEN IS READ, AND GOES NOWHERE ELSE
  // =========================================================================

  describe('credential hygiene', () => {
    it('writes no credential to any log channel', () => {
      // "never log a credential" is one of two absolute rules this migration adopted FROM the habit it
      // replaced.
      const consoleSpies: readonly jasmine.Spy[] = [
        spyOn(console, 'log'),
        spyOn(console, 'warn'),
        spyOn(console, 'error'),
        spyOn(console, 'info'),
        spyOn(console, 'debug'),
      ];

      signIn(NEVER_LOG_TOKEN);
      expect(runGuard('/portals')).toBeTrue();

      session.isAuthenticated.set(false);
      redirectFor('/portals');

      // And once more on the path where the credential is present but lapsed, which is the
      // only path that hands the token's own session to another collaborator.
      session.isAuthenticated.set(true);
      session.accessToken.set(NEVER_LOG_TOKEN);
      expiryProbe.and.returnValue(true);
      expect(runGuard(SIGN_IN_PATH)).toBeTrue();

      for (const spy of consoleSpies) {
        expect(spy)
          .withContext('the gate writes to no console channel, on any path')
          .not.toHaveBeenCalled();
      }

      // Belt and braces. Even were a channel called for an unrelated reason, the token must not be among
      // the arguments.
      for (const spy of consoleSpies) {
        const recorded: readonly unknown[][] = spy.calls.allArgs();

        expect(JSON.stringify(recorded))
          .withContext('no argument on any channel carries the bearer token')
          .not.toContain(NEVER_LOG_TOKEN);
      }
    });

    it('does not carry the credential into the redirect it builds', () => {
      // The redirect is handed to the router and ends up in the address bar and in browser history.
      session.isAuthenticated.set(true);
      session.accessToken.set(NEVER_LOG_TOKEN);
      expiryProbe.and.returnValue(true);
      renewSession.and.returnValue(throwError(() => new Error('refused')));

      // The refusal path is chosen deliberately: it is the ONLY path that both holds a credential and
      // builds a redirect, so it is the only place a token could reach the address bar at all.
      const settled = settleDeferred('/portals');

      expect(settled).toBeInstanceOf(UrlTree);
      expect(router.serializeUrl(settled as UrlTree)).not.toContain(NEVER_LOG_TOKEN);
    });
  });

  // =========================================================================
  // NO SECOND OPINION — THE STORE OWNS THE VERDICT, THE GATE READS IT
  // =========================================================================

  describe('no second opinion', () => {
    it('consults no entitlement and no member of the sign-in verification ladder', () => {
      // Equally, the gate must not upgrade "holds a token" into "is authorised". Entitlements are the
      // separate permission gate's question, and the server re-authorises every request regardless, so
      // reading a role here would only let a screen offer an affordance the API then refuses.
      signIn();
      expect(runGuard('/portals')).toBeTrue();

      session.isAuthenticated.set(false);
      redirectFor('/users/0');
      redirectFor('/roles/-1/users');
      expect(runGuard(SIGN_IN_PATH)).toBeTrue();

      for (const [member, spy] of Object.entries(storeOffLimits)) {
        expect(spy)
          .withContext(`the gate must not consult AuthStore.${member}`)
          .not.toHaveBeenCalled();
      }
    });

    it('never changes session custody and never touches the renewal credential', () => {
      // Reading the session is the gate's whole job; writing it is the store's.
      signIn();
      expect(runGuard('/portals')).toBeTrue();

      session.accessToken.set('');
      redirectFor('/portals');

      expiryProbe.and.returnValue(true);
      signIn();
      renewSession.and.returnValue(of(RENEWED_SESSION));
      expect(settleDeferred('/portals')).toBeTrue();

      for (const [member, spy] of Object.entries(custodianOffLimits)) {
        expect(spy)
          .withContext(`the gate must not reach TokenStorageService.${member}`)
          .not.toHaveBeenCalled();
      }
    });

    it('has its off-limits probes genuinely installed, so the two assertions above can fail', () => {
      // ⚠ THE GUARD RAIL ON THE GUARD RAILS. A negative assertion is worth exactly as much as the wiring
      // behind it: were these spies not the very members the gate resolves through the injector,
      // `not.toHaveBeenCalled()` would pass vacuously and go on passing for ever, reporting success while
      // protecting nothing.
      const store = TestBed.inject(AuthStore);
      const custodian = TestBed.inject(TokenStorageService);

      expect(Object.keys(storeOffLimits).length).toBe(STORE_MEMBERS_OFF_LIMITS.length);
      expect(Object.keys(custodianOffLimits).length).toBe(CUSTODIAN_MEMBERS_OFF_LIMITS.length);

      for (const [member, spy] of Object.entries(storeOffLimits)) {
        const installed: unknown = (store as unknown as Record<string, unknown>)[member];

        expect(installed)
          .withContext(`AuthStore.${member} must be the probe this specification installed`)
          .toBe(spy);
      }

      for (const [member, spy] of Object.entries(custodianOffLimits)) {
        const installed: unknown = (custodian as unknown as Record<string, unknown>)[member];

        expect(installed)
          .withContext(`TokenStorageService.${member} must be the probe this specification installed`)
          .toBe(spy);
      }
    });

    it('is a plain function rather than an activation class', () => {
      // The class-based activation contract is deprecated in this generation of the router.
      expect(typeof authGuard).toBe('function');
      expect('canActivate' in authGuard)
        .withContext('a functional gate exposes no activation method')
        .toBeFalse();
      expect(authGuard.length)
        .withContext('the router hands it the activated route and the router state')
        .toBe(2);
    });
  });
});
